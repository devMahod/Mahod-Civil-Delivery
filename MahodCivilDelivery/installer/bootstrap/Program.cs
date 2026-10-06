using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Forms;

namespace MahodCivilDeliverySetup;

/// <summary>
/// Setup for Mahod Civil Delivery — its own per-user AutoCAD bundle, separate from MahodAI. No administrator rights:
/// the embedded payload is extracted (every entry SHA-256 checked, no path may escape) into a private temp folder and
/// handed to Install-MahodCivilDelivery.ps1, whose exit code this returns.
/// </summary>
internal static class Program
{
    private static string Version =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    [STAThread]
    private static int Main(string[] args)
    {
        // Build-time seam: prove the embedded payload is byte-identical to the staged one. No UI, no install.
        const string verifyPrefix = "--self-test-verify-payload-dir=";
        string? verify = args.FirstOrDefault(a => a.StartsWith(verifyPrefix, StringComparison.Ordinal));
        if (verify is not null) return VerifyEmbeddedPayload(verify[verifyPrefix.Length..]);

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        bool quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));

        // Per-user install: "Run as administrator" could belong to another account and would install into that
        // account's profile. The normal double-click is the only supported entry point.
        if (IsElevated() && !quiet)
        {
            Fail("ההתקנה הופעלה כמנהל (Run as administrator).\n\nיש לסגור אותה ולהפעיל שוב בלחיצה כפולה רגילה — " +
                 "ההתקנה היא למשתמש הנוכחי ואינה צריכה הרשאות מנהל.");
            return 6;
        }

        if (!quiet)
        {
            var answer = MessageBox.Show(
                $"להתקין את Mahod Civil Delivery {Version} עבור Civil 3D 2026 ו־2027?\n\n" +
                "יש לסגור את Civil 3D לפני שממשיכים.\n" +
                "הכלי מותקן כתוסף נפרד מ־MahodAI ומופיע ברצועת הכלים בלשונית MahodAI (במחשב ללא MahodAI — בלשונית Mahod).",
                $"Mahod Civil Delivery {Version} — התקנה", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (answer != DialogResult.Yes) return 1;
        }

        string? extract = null;
        try
        {
            extract = Path.Combine(Path.GetTempPath(), "MahodCivilDeliverySetup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extract);
            ExtractEmbeddedPayloadVerified(extract);
            string script = Path.Combine(extract, "Install-MahodCivilDelivery.ps1");
            string payload = Path.Combine(extract, "payload");
            if (!File.Exists(script) || !Directory.Exists(payload))
                throw new InvalidDataException("Installer script or payload is missing after verified extraction.");
            return RunInstaller(script, payload, extract, quiet);
        }
        catch (Exception ex)
        {
            Fail("לא ניתן היה להכין את ההתקנה:\n" + ex.Message);
            return 5;
        }
        finally
        {
            if (extract is not null)
            {
                try { Directory.Delete(extract, recursive: true); } catch { /* temp residue never masks the result */ }
            }
        }
    }

    private static int RunInstaller(string script, string payload, string workingDirectory, bool quiet)
    {
        // The OS-owned absolute executable, never PATH resolution; ArgumentList does the quoting.
        string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
            throw new FileNotFoundException("Windows PowerShell was not found at the protected System32 path.", powerShell);
        var psi = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (string value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-PayloadDir", payload })
            psi.ArgumentList.Add(value);
        if (!quiet) psi.ArgumentList.Add("-ShowDialog");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        process.WaitForExit();
        return process.ExitCode;
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
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Split('/').Any(p => p == ".."))
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
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(written)))
                throw new CryptographicException("Extracted payload entry failed SHA-256 verification: " + relative);
        }
    }

    private static int VerifyEmbeddedPayload(string expectedDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedDirectory) || !Directory.Exists(expectedDirectory)) return 21;
            string root = Path.GetFullPath(expectedDirectory);
            var disk = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => f, StringComparer.OrdinalIgnoreCase);
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (resource is null) return 22;
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            var embedded = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Any(p => p == "..") || !embedded.TryAdd(relative, entry)) return 23;
            }
            if (!embedded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(disk.Keys)) return 24;
            foreach ((string relative, ZipArchiveEntry entry) in embedded)
            {
                using Stream e = entry.Open();
                using FileStream d = File.OpenRead(disk[relative]);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(e), SHA256.HashData(d))) return 25;
            }
            return 0;
        }
        catch { return 26; }
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Fail(string message) =>
        MessageBox.Show(message, "Mahod Civil Delivery — התקנה", MessageBoxButtons.OK, MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
}
