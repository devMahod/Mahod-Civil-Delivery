using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Whether this computer can rebuild a corridor (2026-11-03, a coworker's 6403: R-14 had no computed sections). The
    /// office's corridors are built from its own .NET subassemblies (MahodPave4, MahodCutOrFill… in
    /// MahodSubassemblies.dll), which the office's MAHOD_3D package installs per Civil 3D version into
    /// <c>%ProgramData%\Autodesk\C3D &lt;year&gt;\enu</c>. A drawing remembers the DLL by the path of the computer that last
    /// built it (6403: <c>C3D 2025\enu</c>); on a computer without it a rebuild computes nothing, and a corridor that had
    /// sections loses them (RD-40, live 2026-11-02). So a rebuild is offered only when every .NET subassembly the
    /// corridor uses can be found: at its stored path, or under the same file name in the running version's folder (where
    /// Civil 3D's own stock subassemblies sit as well).
    /// </summary>
    public static class CorridorRebuildLogic
    {
        /// <summary>A subassembly's generator as Civil 3D stores it: the mode (UseDotNet, UseVBA…) and the DLL or project.</summary>
        public sealed record Generator(string Mode, string? File);

        /// <summary>The running Civil 3D's subassembly folder, from the path of one of its own assemblies; null when unknown.</summary>
        public static string? VersionFolder(string? civilAssemblyPath, string programData)
        {
            if (string.IsNullOrWhiteSpace(civilAssemblyPath)) return null;
            var year = Regex.Match(civilAssemblyPath, @"AutoCAD (\d{4})");
            return year.Success ? Path.Combine(programData, "Autodesk", "C3D " + year.Groups[1].Value, "enu") : null;
        }

        /// <summary>
        /// The DLL file names of the .NET subassemblies that cannot be found here, each once, in order. An unknown
        /// version folder judges by the stored paths alone.
        /// </summary>
        public static IReadOnlyList<string> MissingFiles(
            IEnumerable<Generator> generators, string? versionFolder, Func<string, bool> fileExists)
        {
            var missing = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var generator in generators)
            {
                if (!string.Equals(generator.Mode, "UseDotNet", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(generator.File) || !seen.Add(generator.File!)) continue;
                var name = Path.GetFileName(generator.File!);
                if (fileExists(generator.File!)) continue;
                if (versionFolder != null && fileExists(Path.Combine(versionFolder, name))) continue;
                if (!missing.Contains(name, StringComparer.OrdinalIgnoreCase)) missing.Add(name);
            }
            return missing;
        }

        /// <summary>The year of a version folder ("C3D 2026"), for the message; empty when unknown.</summary>
        public static string YearOf(string? versionFolder)
        {
            var match = versionFolder == null ? Match.Empty : Regex.Match(versionFolder, @"C3D (\d{4})");
            return match.Success ? match.Groups[1].Value : "";
        }
    }
}
