using System;
using System.Collections.Generic;
using System.IO;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Pure (AutoCAD-free) helper that resolves WHERE the pre-built assembly-library
    /// drawing lives and WHICH source assembly to import for a given road type, so
    /// <see cref="CloneAssemblyFromLibraryTool"/> stays unit-testable without a live
    /// Civil 3D document.
    ///
    /// Provisioning model (owner decision 2026-06-15):
    ///   • Default: the library DWG ships inside the plugin bundle under
    ///     <c>Contents/Assemblies/</c>, ONE FILE PER CIVIL 3D VERSION
    ///     (<c>MahodAI_Assemblies_2026.dwg</c> / <c>MahodAI_Assemblies_2027.dwg</c>),
    ///     because a 2027-saved DWG cannot be opened by 2026 (forward-compat only).
    ///   • Override: an explicit <c>library_path</c> tool param OR the
    ///     <c>MAHOD_ASSEMBLY_LIBRARY_PATH</c> environment variable. Either may point at
    ///     a specific .dwg file OR at a directory that contains the per-version files.
    /// </summary>
    public static class AssemblyLibraryLocator
    {
        /// <summary>Environment variable that overrides the library location (file or directory).</summary>
        public const string LibraryPathEnvVar = "MAHOD_ASSEMBLY_LIBRARY_PATH";

        /// <summary>Sub-folder (relative to the plugin DLL) holding the bundled library files.</summary>
        public const string BundleSubFolder = "Assemblies";

        /// <summary>The prefix of the bundled per-version library file names.</summary>
        public const string FileNamePrefix = "MahodAI_Assemblies_";

        /// <summary>Civil 3D versions we ship a library for, newest first (used for fallback scanning).</summary>
        public static IReadOnlyList<int> SupportedYears { get; } = new[] { 2027, 2026 };

        /// <summary>
        /// Maps an AutoCAD/Civil 3D release MAJOR version to its product year.
        /// Civil 3D 2026 = R25.x, Civil 3D 2027 = R26.x. The firm runs only 2026 and 2027,
        /// so anything R26+ resolves to 2027 and everything else to 2026 (the oldest install).
        /// Kept intentionally narrow — widen the table if 2025/2028 ever join the fleet.
        /// </summary>
        public static int YearForVersionMajor(int versionMajor) =>
            versionMajor >= 26 ? 2027 : 2026;

        /// <summary>The per-version library file name for a product year (e.g. 2027 → MahodAI_Assemblies_2027.dwg).</summary>
        public static string LibraryFileName(int year) => $"{FileNamePrefix}{year}.dwg";

        /// <summary>
        /// The source assembly name to import for a road type. Matches the name the
        /// empty-shell path uses (<c>MahodAI_&lt;road_type&gt;</c>) so the library, the
        /// catalog, and the agent all agree on one naming convention. Unknown road types
        /// fall back to the catalog default (rural_2lane).
        /// </summary>
        public static string SourceAssemblyName(string? roadType)
        {
            var key = (roadType ?? string.Empty).Trim().ToLowerInvariant();
            if (!AssemblyTemplateCatalog.IsSupported(key))
                key = AssemblyTemplateCatalog.DefaultRoadType;
            return $"MahodAI_{key}";
        }

        /// <summary>
        /// The Civil 3D STOCK assembly to clone as a ZERO-AUTHORING fallback when no firm-authored
        /// MahodAI library exists (owner choice 2026-06-17: "stock now, custom later"). Civil 3D ships
        /// these complete assemblies under
        /// <c>%ProgramData%\Autodesk\C3D &lt;year&gt;\&lt;lang&gt;\Assemblies\Metric\</c>. Maps each road type to the
        /// closest stock section and returns (dwgFileName, assemblyNameInsideTheDwg). A custom
        /// <c>MahodAI_&lt;type&gt;</c> library, when present, is resolved first and wins over this — so dropping
        /// the MOT-exact library in later silently upgrades the result with no code change.
        /// </summary>
        public static (string fileName, string assemblyName) StockAssemblyFor(string? roadType)
        {
            var key = (roadType ?? string.Empty).Trim().ToLowerInvariant();
            return key switch
            {
                "divided_highway" => ("Divided Highway.dwg", "Divided Highway"),
                // Curbed urban / collector sections → the stock 2-lane "full section" (curb + gutter).
                "urban_2lane" => ("Secondary Road Full Section.dwg", "Secondary Road Full Section"),
                "collector_local" => ("Secondary Road Full Section.dwg", "Secondary Road Full Section"),
                // rural_2lane and the default: lane + shoulder + daylight-to-ground, no curbs —
                // exactly the stock "Basic Assembly" shape.
                _ => ("Basic Assembly.dwg", "Basic Assembly"),
            };
        }

        /// <summary>
        /// Resolves the library .dwg path to read, or null when none can be found.
        /// Probe order: (1) explicit override (file → used directly; directory → version file
        /// inside it, then any supported-year file); (2) the bundled
        /// <c>&lt;bundleDir&gt;/Assemblies/MahodAI_Assemblies_&lt;year&gt;.dwg</c>, then the other
        /// supported-year file as a last resort. <paramref name="fileExists"/> and
        /// <paramref name="combine"/> are injected so the logic is testable without a real
        /// filesystem.
        /// </summary>
        public static string? ResolveLibraryPath(
            string? overridePath,
            string? bundleDir,
            int year,
            Func<string, bool> fileExists,
            Func<string, string, string>? combine = null)
        {
            if (fileExists == null) throw new ArgumentNullException(nameof(fileExists));
            combine ??= Path.Combine;

            string fileName = LibraryFileName(year);

            // (1) Explicit override (param wins over env var; caller passes the already-chosen one).
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                // A path that already names a .dwg is used verbatim.
                if (overridePath!.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    if (fileExists(overridePath)) return overridePath;
                }
                else
                {
                    // Treat as a directory containing the per-version files.
                    var versioned = combine(overridePath, fileName);
                    if (fileExists(versioned)) return versioned;
                    foreach (var y in SupportedYears)
                    {
                        var alt = combine(overridePath, LibraryFileName(y));
                        if (fileExists(alt)) return alt;
                    }
                }
            }

            // (2) Bundled under <bundleDir>/Assemblies/.
            if (!string.IsNullOrWhiteSpace(bundleDir))
            {
                var dir = combine(bundleDir!, BundleSubFolder);
                var versioned = combine(dir, fileName);
                if (fileExists(versioned)) return versioned;
                // Last resort: any supported-year file present (ReadDwgFile will reject a
                // newer-than-runtime file, which the caller turns into a clean fallback).
                foreach (var y in SupportedYears)
                {
                    if (y == year) continue;
                    var alt = combine(dir, LibraryFileName(y));
                    if (fileExists(alt)) return alt;
                }
            }

            return null;
        }
    }
}
