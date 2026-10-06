using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// How a chosen CL drawing is recorded in the project profile.
    ///
    /// The CL instruction drawing is usually a separate file that is never opened and
    /// never attached (6422: CL.dwg next to the Civil model). A profile that hard-codes
    /// one machine's absolute path travels badly: it worked on the build machine and
    /// resolved to nothing on the engineer's, which is exactly how 1.0.0 reached her
    /// with "none of the configured CL files was found" (2026-08-25).
    ///
    /// So: a CL file living beside the model is stored by NAME (portable — it resolves
    /// relative to whatever model the engineer opens), and anything else by absolute
    /// path. Forward slashes keep the value clear of YAML escape traps.
    /// </summary>
    public static class ClSourceSelection
    {
        /// <summary>
        /// The value to persist in <c>sections.cl.source_files</c> for a CL drawing the
        /// engineer picked, given the model drawing she has open.
        /// </summary>
        public static string StoredPath(string clFullPath, string? hostDrawingPath)
        {
            if (string.IsNullOrWhiteSpace(clFullPath)) return string.Empty;
            var cl = clFullPath.Trim();

            if (SameFolderAsHost(cl, hostDrawingPath))
                return Path.GetFileName(cl);

            return Normalize(cl);
        }

        /// <summary>
        /// True when the picked file is the model drawing itself — the CL lines are then
        /// already in model space and no external source is needed.
        /// </summary>
        public static bool IsHostDrawing(string clFullPath, string? hostDrawingPath)
        {
            if (string.IsNullOrWhiteSpace(clFullPath) || string.IsNullOrWhiteSpace(hostDrawingPath))
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(clFullPath.Trim()),
                    Path.GetFullPath(hostDrawingPath.Trim()),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// The CL source list project setup should persist.
        ///
        /// Setup used to REPLACE the list with the open drawing, which silently dropped an
        /// external CL drawing the engineer had chosen and left her sections empty with no
        /// explanation. Every configured source is kept; the host drawing is added so CL
        /// lines drawn in model space (or reached through an XREF) still count.
        /// </summary>
        public static List<string> MergeSources(IEnumerable<string>? existing, string? hostDrawingPath)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in existing ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var value = raw.Trim();
                if (seen.Add(value)) merged.Add(value);
            }

            if (!string.IsNullOrWhiteSpace(hostDrawingPath))
            {
                var hostName = Path.GetFileName(hostDrawingPath.Trim());
                if (!string.IsNullOrEmpty(hostName) && seen.Add(hostName)) merged.Add(hostName);
            }

            return merged;
        }

        /// <summary>
        /// One line for the panel: what the profile now points at, in the engineer's words.
        /// </summary>
        public static string Describe(IReadOnlyList<string> sourceFiles, IReadOnlyList<string> layerPatterns)
        {
            var files = sourceFiles is { Count: > 0 }
                ? string.Join(", ", sourceFiles.Select(Path.GetFileName))
                : "—";
            var layers = layerPatterns is { Count: > 0 } ? string.Join(", ", layerPatterns) : "—";
            return $"קובץ CL: {Bidi.Ltr(files)} · שכבה: {Bidi.Ltr(layers)}";
        }

        private static bool SameFolderAsHost(string clFullPath, string? hostDrawingPath)
        {
            if (string.IsNullOrWhiteSpace(hostDrawingPath)) return false;
            try
            {
                var clDir = Path.GetDirectoryName(Path.GetFullPath(clFullPath));
                var hostDir = Path.GetDirectoryName(Path.GetFullPath(hostDrawingPath.Trim()));
                return !string.IsNullOrEmpty(clDir) && !string.IsNullOrEmpty(hostDir) &&
                       string.Equals(clDir.TrimEnd('\\', '/'), hostDir.TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Normalize(string path) => path.Replace('\\', '/');
    }
}
