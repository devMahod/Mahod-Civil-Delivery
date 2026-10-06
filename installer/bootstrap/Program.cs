using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MahodCivilDeliverySetup;

/// <summary>
/// Self-contained setup bootstrapper. Elevation is decided before any payload is
/// written. A machine-wide upgrade relaunches this same candidate first; only the
/// elevated child extracts into a unique, administrator-only directory.
/// </summary>
internal static class Program
{
    private const string ElevatedInstallArg = "--internal-elevated-install";
    private const string ContextSelfTestArg = "--self-test-validate-original-context";
    private const string SidPrefix = "--original-user-sid=";
    private const string UserPrefix = "--original-user-name=";
    private const string AppDataPrefix = "--original-appdata=";
    private const string LocalAppDataPrefix = "--original-localappdata=";

    private sealed record OriginalUserContext(string Sid, string UserName, string AppData, string LocalAppData);

    /// <summary>Package revision (1.2.x) from the assembly version; the one number the engineer knows.</summary>
    private static string PackageRevision =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    [STAThread]
    private static int Main(string[] args)
    {
        // Read-only/offline release-gate seams. They run before UI, elevation,
        // extraction, registry writes or access to any Autodesk install root.
        if (TryGetSelfTestChildExitCode(args, out int selfTestExitCode))
            return RunSelfTestChild(selfTestExitCode);
        if (TryGetPayloadVerificationDirectory(args, out string? expectedPayloadDirectory))
            return VerifyEmbeddedPayload(expectedPayloadDirectory!);
        if (args.Any(a => a.Equals(ContextSelfTestArg, StringComparison.Ordinal)))
            return ValidateContextSelfTest(args);

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        bool internalElevated = args.Any(a => a.Equals(ElevatedInstallArg, StringComparison.Ordinal));
        bool quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                                   a.Equals("-quiet", StringComparison.OrdinalIgnoreCase));

        // An already-elevated external launch may be running under credentials for
        // a different administrator; Windows does not expose the discarded caller
        // identity reliably. Refuse before extraction instead of silently placing
        // state/profile/ARP data under that administrator. Normal double-click is
        // the supported entry point and self-elevates this EXE when required.
        if (!internalElevated && IsElevated())
        {
            Fail("Setup was started with 'Run as administrator'.\n\n" +
                 "Close it and double-click the setup normally. It will request UAC itself if the machine-wide MahodAI bundle needs updating, while preserving the original user's profile.");
            return 6;
        }

        OriginalUserContext originalUser;
        try
        {
            if (internalElevated)
            {
                if (!IsElevated())
                    throw new InvalidOperationException("The internal elevated phase was started without an elevated token.");
                originalUser = ParseAndValidateOriginalUserContext(args);
            }
            else
            {
                originalUser = CaptureAndValidateOriginalUserContext();
            }
        }
        catch (Exception ex)
        {
            Fail("Could not resolve the original Windows user safely:\n" + ex.Message);
            return 6;
        }

        // The non-elevated parent owns the one confirmation dialog. The internal
        // elevated child skips it, so UAC never causes a second prompt.
        if (!internalElevated && !quiet)
        {
            var result = MessageBox.Show(
                $"להתקין את Mahod Civil Delivery {PackageRevision} עבור Civil 3D?\n\n" +
                "יש לסגור את Civil 3D לפני שממשיכים.\n" +
                "אם MahodAI כבר מותקן לכל המשתמשים, יתבקש אישור מנהל פעם אחת.\n\n" +
                "\u200EInstall Mahod Civil Delivery for Civil 3D? Close Civil 3D first",
                $"Mahod Civil Delivery {PackageRevision} — התקנה", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (result != DialogResult.Yes) return 1;
        }

        // Crucial ordering: complete self-elevation BEFORE opening the embedded ZIP
        // or creating an extraction directory. No elevated process consumes a script
        // written by the unelevated parent.
        if (!internalElevated && MachineWideBundleExists() && !IsElevated())
            return RelaunchSelfElevated(originalUser, quiet);

        string? extract = null;
        try
        {
            extract = CreateUniqueExtractionDirectory(IsElevated());
            ExtractEmbeddedPayloadVerified(extract);

            string script = Path.Combine(extract, "Install-MahodCivilDelivery.ps1");
            string payloadDir = Path.Combine(extract, "payload");
            if (!File.Exists(script) || !Directory.Exists(payloadDir))
                throw new InvalidDataException("Installer script or payload directory is missing after verified extraction.");

            return RunPowerShellInstaller(script, payloadDir, extract, originalUser);
        }
        catch (Exception ex)
        {
            Fail("Could not prepare or start the installer:\n" + ex.Message);
            return 5;
        }
        finally
        {
            if (extract is not null)
            {
                try { Directory.Delete(extract, recursive: true); }
                catch { /* Protected residue is preferable to masking the install result. */ }
            }
        }
    }

    private static int RelaunchSelfElevated(OriginalUserContext originalUser, bool quiet)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the setup executable path.");
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add(ElevatedInstallArg);
        AddOriginalUserArguments(psi, originalUser);
        if (quiet) psi.ArgumentList.Add("/quiet");

        try
        {
            using var child = Process.Start(psi) ?? throw new InvalidOperationException("Elevated setup did not start.");
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Fail("Installation cancelled: administrator approval was declined.\n\n" +
                 "MahodAI is installed for all users on this machine, so updating it needs admin rights.");
            return 4;
        }
        catch (Exception ex)
        {
            Fail("Could not elevate the setup safely:\n" + ex.Message);
            return 5;
        }
    }

    private static int RunPowerShellInstaller(
        string script, string payloadDir, string workingDirectory, OriginalUserContext originalUser)
    {
        // Use the OS-owned absolute executable, never PATH resolution. ArgumentList
        // provides quoting and carries original-user context across alternate-admin UAC.
        string powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
            throw new FileNotFoundException("Windows PowerShell was not found at the protected System32 path.", powerShell);

        var psi = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (string value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                                          "-PayloadDir", payloadDir, "-ShowDialog",
                                          "-OriginalUserSid", originalUser.Sid,
                                          "-OriginalUserName", originalUser.UserName,
                                          "-OriginalAppData", originalUser.AppData,
                                          "-OriginalLocalAppData", originalUser.LocalAppData })
            psi.ArgumentList.Add(value);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void AddOriginalUserArguments(ProcessStartInfo psi, OriginalUserContext context)
    {
        psi.ArgumentList.Add(SidPrefix + context.Sid);
        psi.ArgumentList.Add(UserPrefix + context.UserName);
        psi.ArgumentList.Add(AppDataPrefix + context.AppData);
        psi.ArgumentList.Add(LocalAppDataPrefix + context.LocalAppData);
    }

    private static OriginalUserContext CaptureAndValidateOriginalUserContext()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows identity has no SID.");
        var context = new OriginalUserContext(
            sid,
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        return ValidateOriginalUserContext(context);
    }

    private static OriginalUserContext ParseAndValidateOriginalUserContext(string[] args)
    {
        var context = new OriginalUserContext(
            GetExactlyOneValue(args, SidPrefix),
            GetExactlyOneValue(args, UserPrefix),
            GetExactlyOneValue(args, AppDataPrefix),
            GetExactlyOneValue(args, LocalAppDataPrefix));
        return ValidateOriginalUserContext(context);
    }

    private static string GetExactlyOneValue(string[] args, string prefix)
    {
        string[] matches = args.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0][prefix.Length..]))
            throw new InvalidOperationException($"Expected exactly one non-empty {prefix} argument.");
        string value = matches[0][prefix.Length..];
        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new InvalidOperationException($"Invalid control character in {prefix} argument.");
        return value;
    }

    private static OriginalUserContext ValidateOriginalUserContext(OriginalUserContext context)
    {
        var sid = new SecurityIdentifier(context.Sid);
        if (!string.Equals(sid.Value, context.Sid, StringComparison.Ordinal))
            throw new InvalidOperationException("Original user SID is not canonical.");

        string auditUserName = context.UserName.Trim();
        if (auditUserName.Length is 0 or > 256 || auditUserName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new InvalidOperationException("Original username is invalid.");
        // Username is audit-only; SID + ProfileList are the security authority.
        // Domain/AzureAD/cached accounts do not always translate while offline, so
        // use a translated display name when available but never reject a valid SID
        // solely because directory services cannot resolve it today.
        try
        {
            string translated = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            string translatedLeaf = translated.Split('\\').Last();
            if (!string.IsNullOrWhiteSpace(translatedLeaf)) auditUserName = translatedLeaf;
        }
        catch (IdentityNotMappedException) { }

        string appData = NormalizeAbsolutePath(context.AppData, "APPDATA");
        string localAppData = NormalizeAbsolutePath(context.LocalAppData, "LOCALAPPDATA");
        using RegistryKey? profileKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value,
            writable: false);
        string? rawProfile = profileKey?.GetValue(
            "ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(rawProfile))
            throw new InvalidOperationException("Original user's machine-controlled profile record was not found.");
        string profile = NormalizeAbsolutePath(Environment.ExpandEnvironmentVariables(rawProfile), "profile");

        // This prevents forged internal arguments from becoming an arbitrary
        // privileged file writer; both roots must remain within the SID's profile.
        if (!IsWithin(profile, appData) || !IsWithin(profile, localAppData))
            throw new InvalidOperationException("Original APPDATA/LOCALAPPDATA are outside the registered SID profile.");

        return new OriginalUserContext(sid.Value, auditUserName, appData, localAppData);
    }

    private static string NormalizeAbsolutePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"Original user {label} path is not absolute.");
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWithin(string root, string candidate)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateUniqueExtractionDirectory(bool elevated)
    {
        if (!elevated)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "MahodCivilDelivery_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            return tempPath;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidOperationException("Program Files could not be resolved for protected extraction.");
        string parent = Path.Combine(programFiles, "Mahod Engineering", "MahodCivilDelivery", "InstallerTemp");
        Directory.CreateDirectory(parent);
        ApplyAdministratorOnlyAcl(parent);
        string elevatedPath = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elevatedPath);
        ApplyAdministratorOnlyAcl(elevatedPath);
        return elevatedPath;
    }

    private static void ApplyAdministratorOnlyAcl(string directory)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ExtractEmbeddedPayloadVerified(string destination)
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("payload.zip is not embedded in this setup.");
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') ||
                relative.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("Unsafe path in embedded payload.");

            string target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !targets.Add(target))
                throw new InvalidDataException("Escaping or duplicate path in embedded payload.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] sourceHash;
            using (Stream input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
                sourceHash = hash.GetHashAndReset();
            }
            using FileStream written = File.OpenRead(target);
            byte[] writtenHash = SHA256.HashData(written);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, writtenHash))
                throw new CryptographicException("Extracted payload entry failed SHA256 verification: " + relative);
        }
    }

    private static bool TryGetSelfTestChildExitCode(string[] args, out int exitCode)
    {
        const string prefix = "--self-test-child-exit=";
        string? value = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        return int.TryParse(value?[prefix.Length..], out exitCode) && exitCode is >= 0 and <= 255;
    }

    private static int RunSelfTestChild(int requestedExitCode)
    {
        string command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string value in new[] { "/d", "/c", "exit", requestedExitCode.ToString() })
            psi.ArgumentList.Add(value);
        using var child = Process.Start(psi) ?? throw new InvalidOperationException("Self-test child did not start.");
        child.WaitForExit();
        return child.ExitCode;
    }

    private static int ValidateContextSelfTest(string[] args)
    {
        try { _ = ParseAndValidateOriginalUserContext(args); return 0; }
        catch { return 31; }
    }

    private static bool TryGetPayloadVerificationDirectory(string[] args, out string? directory)
    {
        const string prefix = "--self-test-verify-payload-dir=";
        string? value = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        directory = value?[prefix.Length..];
        return value is not null;
    }

    private static int VerifyEmbeddedPayload(string expectedDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedDirectory) || !Directory.Exists(expectedDirectory)) return 21;
            string root = Path.GetFullPath(expectedDirectory);
            var diskFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), file => file,
                              StringComparer.OrdinalIgnoreCase);
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (resource is null) return 22;
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            var embedded = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Any(part => part == "..") ||
                    !embedded.TryAdd(relative, entry)) return 23;
            }
            if (!embedded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(diskFiles.Keys)) return 24;
            foreach ((string relative, ZipArchiveEntry entry) in embedded)
            {
                using Stream embeddedStream = entry.Open();
                byte[] embeddedHash = SHA256.HashData(embeddedStream);
                using FileStream diskStream = File.OpenRead(diskFiles[relative]);
                byte[] diskHash = SHA256.HashData(diskStream);
                if (!CryptographicOperations.FixedTimeEquals(embeddedHash, diskHash)) return 25;
            }
            return 0;
        }
        catch { return 26; }
    }

    private static bool MachineWideBundleExists()
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        })
        {
            string dll = Path.Combine(root, "Autodesk", "ApplicationPlugins", "MahodAI.bundle", "Contents",
                                      "MahodAI.Civil3D.Plugin.dll");
            if (File.Exists(dll)) return true;
        }
        return false;
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Fail(string message) =>
        MessageBox.Show(message, "Mahod Civil Delivery Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
