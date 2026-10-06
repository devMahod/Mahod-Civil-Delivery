using System;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Per-user optional credentials. Never copied into a bundle, profile or run.</summary>
internal static class SemanticMappingAssistantConfiguration
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    internal static readonly byte[] CredentialPurpose = Encoding.UTF8.GetBytes("MahodCivilDelivery/semantic-mapping/gemini/v1");
    internal static string CredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodAI_Civil3D", "civil-delivery", "secrets", "gemini.key.dpapi");

    /// <summary>TrustedInstaller (NT SERVICE\TrustedInstaller), which owns and writes OS-protected files.</summary>
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Rights that let an account change, replace or re-permission the policy file.</summary>
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>GENERIC_ALL and GENERIC_WRITE, which an ACE may carry unmapped.</summary>
    private const int GenericWriteBits = 0x10000000 | 0x40000000;

    /// <summary>
    /// The organisation's non-secret vision switch. It is MACHINE-WIDE and outside every user profile:
    /// <c>%ProgramData%\MahodAI_Civil3D\civil-delivery\semantic-vision.policy.json</c>, with the content described by
    /// <see cref="VisionOrganizationPolicy"/>. It holds no key. An administrator creates it from an elevated session
    /// (so its owner is BUILTIN\Administrators) and keeps it writable only by Administrators, SYSTEM or TrustedInstaller.
    /// It counts only when that holds: a file owned by any other account, or one that any other account may write,
    /// append, delete, re-permission or take ownership of, is ignored. ProgramData lets standard users create files in
    /// sub-folders, so this check (made on the same handle the content is read from) is what keeps the engineer who
    /// grants the per-request <see cref="VisionSendPermit"/> from also switching the organisation policy on.
    /// Absent, unprotected or invalid means text only, even when a key is configured. The permit stays per engineer and
    /// per request.
    /// </summary>
    internal static string VisionPolicyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MahodAI_Civil3D", "civil-delivery", "semantic-vision.policy.json");

    internal static ISemanticMappingAssistant Create() => new GeminiMappingAssistant(Client, ReadKey);
    internal static bool IsConfigured => !string.IsNullOrWhiteSpace(ReadKey());

    /// <summary>
    /// The family-ranking transport on the same redirect-disabled client. The policy is re-read on every send, so
    /// removing the file disables images immediately; a per-request permit is still required on top of it.
    /// </summary>
    internal static IFamilyRecognitionProvider CreateFamilyProvider() =>
        new GeminiMappingAssistant(Client, ReadKey, visionEnabled: () => IsVisionEnabled);

    internal static bool IsVisionEnabled => ReadVisionPolicy(VisionPolicyPath);

    /// <summary>Reads the policy at <paramref name="path"/>; enabled only when it is administrator-controlled and valid.</summary>
    internal static bool ReadVisionPolicy(string path) => ReadVisionPolicy(path, IsAdministratorControlled);

    /// <summary>
    /// Reads the policy through one handle that denies writers and deletion while it is open, so the protection that
    /// <paramref name="isProtected"/> checks belongs to exactly the bytes that are parsed (no swap, no symlink target
    /// other than the one checked). Any failure means disabled.
    /// </summary>
    internal static bool ReadVisionPolicy(string path, Func<FileSecurity, bool> isProtected)
    {
        ArgumentNullException.ThrowIfNull(isProtected);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length is <= 0 or > VisionOrganizationPolicy.MaxFileBytes) return false;
            if (!isProtected(stream.GetAccessControl())) return false;
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return VisionOrganizationPolicy.IsEnabled(bytes);
        }
        catch { return false; /* Vision stays off; the text path and manual mapping remain available. */ }
    }

    /// <summary>
    /// True only when the file's owner is Administrators, SYSTEM or TrustedInstaller and no allow entry grants any write,
    /// append, attribute, delete, permission or ownership right to another account. Deny entries only narrow access.
    /// </summary>
    internal static bool IsAdministratorControlled(FileSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrustedAdministrator(owner))
            return false;
        // Explicit and inherited entries, as SIDs (no name translation).
        foreach (AuthorizationRule entry in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (entry is not FileSystemAccessRule rule) return false;
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var rights = rule.FileSystemRights;
            if ((rights & WriteRights) == 0 && ((int)rights & GenericWriteBits) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrustedAdministrator(sid)) return false;
        }
        return true;
    }

    private static bool IsTrustedAdministrator(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        string.Equals(sid.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);

    private static string? ReadKey()
    {
        // An explicitly provisioned current-user credential takes precedence over
        // stale process environment inherited when Civil started.
        try
        {
            var path = CredentialPath;
            if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= 16384)
            {
                var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), CredentialPurpose, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(clear).Trim(); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
        }
        catch { /* Manual mapping remains available; never log a secret/DPAPI body. */ }
        return Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim();
    }
}
