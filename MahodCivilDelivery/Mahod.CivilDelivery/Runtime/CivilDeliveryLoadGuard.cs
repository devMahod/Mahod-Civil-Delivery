using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace MahodAI.Civil3D.Plugin.Runtime;

/// <summary>
/// Verifies that the Core beside this plugin is exactly the one it was built against, before any Civil Delivery
/// type is touched. The same contract as MahodAI's platform guard (SHA-256 of the Core file named in the embedded
/// identity, MVID, version, target framework, load context) for the separate tool's own assembly names.
/// Must remain BCL-only: no Civil, Core or Autodesk types in this boundary.
/// </summary>
internal static class CivilDeliveryLoadGuard
{
    internal const string CoreName = "Mahod.CivilDelivery.Core";
    internal const string IdentityResource = "MahodAI.CivilDelivery.CoreIdentity.v1";
    internal const string UserMessage = "Mahod Civil Delivery לא נטען: רכיב Core אינו תואם לתוסף. " +
        "שאר כלי Mahod נשארים זמינים. יש לסגור את Civil לאחר שמירת העבודה ולהתקין מחדש את Mahod Civil Delivery. " +
        "פרטי הזהות זמינים בפקודה MCD_CHECK.";
    private static readonly object Sync = new();
    private static readonly Assembly Plugin = typeof(CivilDeliveryLoadGuard).Assembly;
    private static string _state = "Unknown";
    private static string _reason = "not_checked";
    private static string _detail = "";
    private static Assembly? _verified;

    internal static string DiagnosticSummary
    {
        get { lock (Sync) return $"Civil Delivery Core: {_state}; {_reason}; {_detail}"; }
    }

    internal static bool EnsureReady()
    {
        lock (Sync)
        {
            if (_state == "Ready") return true;
            if (_state == "Blocked" || _state == "Checking") return false;
            _state = "Checking";
            try
            {
                VerifyAndPreload();
                _state = "Ready";
                _reason = "paired_core_verified";
                return true;
            }
            catch (Exception error)
            {
                _verified = null;
                _state = "Blocked";
                _reason = error is CoreIdentityException ? error.Message : "verification_failed_" + error.GetType().Name;
                _detail += "; plugin=" + Plugin.Location;
                return false;
            }
        }
    }

    internal static Assembly? ResolveCoreRequest(AssemblyName requested)
    {
        // Never recurse into preload during a load event.
        lock (Sync)
        {
            if (_state == "Checking" || !EnsureReady() || _verified == null) return null;
            var actual = _verified.GetName();
            return requested.Version == null || requested.Version == actual.Version ? _verified : null;
        }
    }

    private static void VerifyAndPreload()
    {
        var identity = ReadIdentity();
        if (string.IsNullOrWhiteSpace(Plugin.Location)) Fail("plugin_location_unavailable");
        var pluginDirectory = Path.GetDirectoryName(Path.GetFullPath(Plugin.Location))!;
        var candidate = Path.Combine(pluginDirectory, CoreName + ".dll");
        _detail = "expected=" + candidate + "; sha256=" + identity.Sha256;
        if (!File.Exists(candidate)) Fail("core_file_missing");
        // Keep the verified file open across metadata inspection and load. No write/delete sharing.
        using var file = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualHash = Convert.ToHexString(SHA256.HashData(file));
        _detail += "; actual_sha256=" + actualHash;
        if (!string.Equals(actualHash, identity.Sha256, StringComparison.OrdinalIgnoreCase))
            Fail("core_hash_mismatch");
        file.Position = 0;
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata) Fail("core_not_managed");
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var module = metadata.GetModuleDefinition();
        var name = metadata.GetString(assembly.Name);
        var mvid = metadata.GetGuid(module.Mvid);
        _detail += "; expected_mvid=" + mvid + "; expected_version=" + assembly.Version;
        if (name != CoreName || mvid == Guid.Empty) Fail("core_metadata_invalid");
        if (assembly.Version != Plugin.GetName().Version) Fail("core_version_mismatch");
        var wantedFramework = identity.Tfm.StartsWith("net10.0", StringComparison.Ordinal) ? ".NETCoreApp,Version=v10.0" :
            identity.Tfm.StartsWith("net8.0", StringComparison.Ordinal) ? ".NETCoreApp,Version=v8.0" : "";
        if (wantedFramework.Length == 0 ||
            (identity.Tfm != "net10.0" && identity.Tfm != "net10.0-windows" &&
             identity.Tfm != "net8.0" && identity.Tfm != "net8.0-windows")) Fail("identity_runtime_unsupported");
        if (ReadTargetFramework(metadata, assembly) != wantedFramework ||
            Environment.Version.Major < (identity.Tfm.StartsWith("net10.", StringComparison.Ordinal) ? 10 : 8))
            Fail("core_runtime_mismatch");
        var context = AssemblyLoadContext.GetLoadContext(Plugin);
        if (context == null) Fail("plugin_load_context_unavailable");
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => string.Equals(a.GetName().Name, CoreName, StringComparison.OrdinalIgnoreCase)).ToArray();
        _detail += "; preloaded=" + string.Join(" | ", alreadyLoaded.Take(2)
            .Select(a => a.Location + ",mvid=" + a.ManifestModule.ModuleVersionId + ",version=" + a.GetName().Version));
        if (alreadyLoaded.Length > 1) Fail("multiple_core_assemblies_loaded");
        if (alreadyLoaded.Length == 1)
        {
            CheckLoaded(alreadyLoaded[0], candidate, mvid, assembly.Version, context!);
            _verified = alreadyLoaded[0];
        }
        else
        {
            var loaded = context!.LoadFromAssemblyPath(candidate);
            CheckLoaded(loaded, candidate, mvid, assembly.Version, context);
            _verified = loaded;
        }
        _detail += "; loaded=" + _verified.Location + "; mvid=" + mvid + "; version=" + assembly.Version;
    }

    private static void CheckLoaded(Assembly loaded, string expectedPath, Guid expectedMvid,
        Version expectedVersion, AssemblyLoadContext context)
    {
        if (string.IsNullOrWhiteSpace(loaded.Location) ||
            !string.Equals(Path.GetFullPath(loaded.Location), expectedPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            Fail("loaded_core_path_mismatch");
        if (loaded.GetName().Name != CoreName || loaded.GetName().Version != expectedVersion ||
            loaded.ManifestModule.ModuleVersionId != expectedMvid) Fail("loaded_core_identity_mismatch");
        if (!ReferenceEquals(AssemblyLoadContext.GetLoadContext(loaded), context)) Fail("loaded_core_context_mismatch");
    }

    private static (string Sha256, string Tfm) ReadIdentity()
    {
        using var resource = Plugin.GetManifestResourceStream(IdentityResource);
        if (resource == null || resource.Length > 2048) Fail("embedded_identity_missing_or_invalid");
        using var reader = new StreamReader(resource!);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || !values.TryAdd(parts[0], parts[1])) Fail("embedded_identity_invalid");
        }
        if (values.Count != 4 || !values.TryGetValue("schema", out var schema) || schema != "1" ||
            !values.TryGetValue("name", out var name) || name != CoreName ||
            !values.TryGetValue("sha256", out var sha) || sha.Length != 64 || !sha.All(Uri.IsHexDigit) ||
            !values.TryGetValue("tfm", out var tfm)) Fail("embedded_identity_invalid");
        return (values["sha256"], values["tfm"]);
    }

    private static string? ReadTargetFramework(MetadataReader reader, AssemblyDefinition assembly)
    {
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Runtime.Versioning" ||
                reader.GetString(type.Name) != "TargetFrameworkAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) Fail("core_target_framework_invalid");
            return blob.ReadSerializedString();
        }
        return null;
    }

    private static void Fail(string code) => throw new CoreIdentityException(code);
    private sealed class CoreIdentityException : Exception
    {
        internal CoreIdentityException(string code) : base(code) { }
    }
}
