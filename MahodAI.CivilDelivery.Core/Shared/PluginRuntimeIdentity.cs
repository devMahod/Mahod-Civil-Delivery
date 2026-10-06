using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Identity of the exact platform assembly and Autodesk package that produced a run.
    /// This deliberately does not use a feature-engine constant: the same engine can ship
    /// in several materially different platform/package builds.
    /// </summary>
    public sealed record PluginRuntimeIdentity(
        string? BuildVersion,
        string? GitSha,
        string? PackageRevision,
        string? AssemblySha256);

    public static class PluginRuntimeIdentityResolver
    {
        private static readonly Regex GitShaPattern = new(
            @"(?:^|[+.])(?<sha>(?:[0-9a-f]{64}|[0-9a-f]{40}))(?:$|[+.])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Resolves identity from assembly metadata and the bundle that physically contains
        /// the assembly.  Every lookup is best-effort so evidence writing can never make a
        /// successful Civil operation fail.
        /// </summary>
        public static PluginRuntimeIdentity FromAssembly(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            string? version = null;
            string? informational = null;
            string? location = null;
            try { version = assembly.GetName().Version?.ToString(); } catch { }
            try
            {
                informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
            }
            catch { }
            try { location = assembly.Location; } catch { }

            return Resolve(version, informational, location);
        }

        /// <summary>
        /// Deterministic overload used by host-free tests and evidence tooling.
        /// </summary>
        public static PluginRuntimeIdentity Resolve(
            string? assemblyVersion,
            string? informationalVersion,
            string? assemblyPath)
        {
            var buildVersion = NullIfWhiteSpace(assemblyVersion)
                ?? VersionPrefix(informationalVersion);
            var gitSha = GitSha(informationalVersion);
            var packageRevision = FindPackageRevision(assemblyPath);
            var assemblySha = FileSha256(assemblyPath);

            return new PluginRuntimeIdentity(
                buildVersion,
                gitSha,
                packageRevision,
                assemblySha);
        }

        internal static string? GitSha(string? informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
            var match = GitShaPattern.Match(informationalVersion);
            return match.Success
                ? match.Groups["sha"].Value.ToLowerInvariant()
                : null;
        }

        /// <summary>The platform updater's copy of a version's own manifest inside its folder (UpdaterPaths).</summary>
        internal const string StashedManifestName = "_PackageContents.xml";

        internal static string? FindPackageRevision(string? assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
                // Versioned bundle (bundle\Contents\<version>\<year>\plugin.dll): the loaded release is the version folder's,
                // named by the manifest the updater keeps inside it. The root manifest may already name the next release.
                if (directory != null && new DirectoryInfo(directory).Parent is { } versionFolder &&
                    string.Equals(versionFolder.Parent?.Name, "Contents", StringComparison.OrdinalIgnoreCase) &&
                    Version.TryParse(versionFolder.Name, out _))
                {
                    var stash = Path.Combine(versionFolder.FullName, StashedManifestName);
                    return (File.Exists(stash) ? NullIfWhiteSpace(XDocument.Load(stash).Root?.Attribute("AppVersion")?.Value) : null)
                        ?? versionFolder.Name;
                }
                // 2027: bundle\Contents\plugin.dll; 2026: bundle\Contents\2026\plugin.dll.
                // Walk a few ancestors instead of assuming either layout.
                for (var depth = 0; depth < 5 && !string.IsNullOrEmpty(directory); depth++)
                {
                    var manifest = Path.Combine(directory, "PackageContents.xml");
                    if (File.Exists(manifest))
                    {
                        var root = XDocument.Load(manifest).Root;
                        return NullIfWhiteSpace(root?.Attribute("AppVersion")?.Value);
                    }
                    directory = Directory.GetParent(directory)?.FullName;
                }
            }
            catch { }
            return null;
        }

        private static string? FileSha256(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return File.Exists(path) ? ArtifactHash.Sha256OfFile(path) : null;
            }
            catch { return null; }
        }

        private static string? VersionPrefix(string? informationalVersion)
        {
            var value = NullIfWhiteSpace(informationalVersion);
            if (value is null) return null;
            var separator = value.IndexOf('+');
            return NullIfWhiteSpace(separator < 0 ? value : value[..separator]);
        }

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
