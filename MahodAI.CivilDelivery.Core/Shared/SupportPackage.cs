using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Chooses what goes into the "ייצוא לתמיכה" package. The first real export zipped the
    /// whole state folder and produced 180 MB (2026-08-19) — an engineer cannot email that.
    /// Logs, the profile and the install receipt are always included because they are small
    /// and are what support actually reads; run artifacts are included newest-first until a
    /// budget is reached, and anything skipped is listed in the package manifest.
    /// </summary>
    public static class SupportPackage
    {
        /// <summary>Total budget for run artifacts (bytes). Logs and profiles are always included.</summary>
        public const long RunBudgetBytes = 20L * 1024 * 1024;

        /// <summary>Any single artifact larger than this is skipped and reported.</summary>
        public const long MaxSingleFileBytes = 4L * 1024 * 1024;

        /// <summary>How many run folders to consider at all, newest first.</summary>
        public const int MaxRuns = 8;

        public sealed record Item(string RelativePath, long Length);

        public sealed record Plan(
            IReadOnlyList<Item> Included,
            IReadOnlyList<(string RelativePath, long Length, string Reason)> Skipped,
            long TotalBytes);

        /// <summary>One entry the caller supplies: a path relative to the state root, and its size.</summary>
        public sealed record Candidate(string RelativePath, long Length, DateTime LastWriteUtc);

        /// <summary>
        /// Pure selection so it can be tested without touching a disk: give it every file
        /// under the state root and it returns what to pack and what to leave out.
        /// </summary>
        public static Plan Choose(IEnumerable<Candidate> files)
        {
            var included = new List<Item>();
            var skipped = new List<(string, long, string)>();
            // A stored secret never enters the package, wherever it sits — also inside a run folder (Codex review 01.10,
            // 02:33: the first filter covered only files outside runs/). Filtered before any branch looks at a file.
            var all = new List<Candidate>();
            foreach (var f in files)
            {
                if (IsSecret(f.RelativePath)) skipped.Add((f.RelativePath, f.Length, SecretReason));
                else if (TopFolder(f.RelativePath).Equals(RestorePointsFolder, StringComparison.OrdinalIgnoreCase))
                    skipped.Add((f.RelativePath, f.Length, RestorePointReason));
                else if (TopFolder(f.RelativePath).Equals(XrefReadSnapshotsFolder, StringComparison.OrdinalIgnoreCase))
                    skipped.Add((f.RelativePath, f.Length, XrefReadSnapshotReason));
                else all.Add(f);
            }

            static string TopFolder(string rel)
            {
                var norm = rel.Replace('\\', '/');
                var i = norm.IndexOf('/');
                return i < 0 ? "" : norm[..i];
            }
            static string RunFolder(string rel)
            {
                var parts = rel.Replace('\\', '/').Split('/');
                return parts.Length >= 2 ? parts[1] : "";
            }

            // Always: everything that is not a run artifact (logs, profiles, install state).
            foreach (var f in all.Where(f => !TopFolder(f.RelativePath).Equals("runs", StringComparison.OrdinalIgnoreCase)))
            {
                if (f.Length > MaxSingleFileBytes)
                {
                    skipped.Add((f.RelativePath, f.Length, "גדול מדי"));
                    continue;
                }
                included.Add(new Item(f.RelativePath, f.Length));
            }

            // Runs: newest folders first, within the budget.
            var runs = all
                .Where(f => TopFolder(f.RelativePath).Equals("runs", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => RunFolder(f.RelativePath))
                .Where(g => g.Key.Length > 0)
                .OrderByDescending(g => g.Max(f => f.LastWriteUtc))
                .ThenByDescending(g => g.Key, StringComparer.Ordinal)
                .ToList();

            long budget = RunBudgetBytes;
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (i >= MaxRuns)
                {
                    foreach (var f in run) skipped.Add((f.RelativePath, f.Length, "ריצה ישנה"));
                    continue;
                }
                foreach (var f in run.OrderBy(f => f.Length))
                {
                    if (f.Length > MaxSingleFileBytes) { skipped.Add((f.RelativePath, f.Length, "גדול מדי")); continue; }
                    if (f.Length > budget) { skipped.Add((f.RelativePath, f.Length, "מעבר לתקציב החבילה")); continue; }
                    included.Add(new Item(f.RelativePath, f.Length));
                    budget -= f.Length;
                }
            }

            return new Plan(included, skipped, included.Sum(i => i.Length));
        }

        /// <summary>The manifest reason of a file left out because it holds a stored secret.</summary>
        public const string SecretReason = "מפתח שמור — לא נשלח";

        /// <summary>
        /// Full copies of earlier installed bundles that the old installer keeps for rollback. Support never needs them, and
        /// they are not bounded like run artifacts: live 01.10.2026 they were 1,485 files / 350 MB of DLLs and videos, and the
        /// package grew to 156 MB again — the 2026-08-19 problem through another folder.
        /// </summary>
        public const string RestorePointsFolder = "restore-points";

        /// <summary>The manifest reason of a file under <see cref="RestorePointsFolder"/>.</summary>
        public const string RestorePointReason = "גיבוי התקנה קודמת — לא נדרש לתמיכה";

        /// <summary>Private drawing copies for XREF reads; cleanup may leave them behind. Never send their bytes.</summary>
        public const string XrefReadSnapshotsFolder = "xref-read-snapshots";

        /// <summary>The manifest reason of a file under <see cref="XrefReadSnapshotsFolder"/>.</summary>
        public const string XrefReadSnapshotReason = "עותק שרטוט זמני לקריאת XREF — לא נשלח";

        /// <summary>
        /// A stored secret never leaves the machine in a support package: anything under a "secrets" folder at any depth,
        /// and any DPAPI-protected or key file by its extension.
        /// </summary>
        public static bool IsSecret(string relativePath)
        {
            var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Take(parts.Length - 1).Any(p => p.Equals("secrets", StringComparison.OrdinalIgnoreCase))) return true;
            var name = parts.Length == 0 ? "" : parts[^1];
            return name.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The MANIFEST.txt that travels inside the package so support sees what is missing.</summary>
        public static string Manifest(Plan plan, string stateRoot)
        {
            var lines = new List<string>
            {
                "Mahod Civil Delivery — חבילת תמיכה",
                "מקור: " + stateRoot,
                $"נכללו {plan.Included.Count} קבצים, {plan.TotalBytes / 1024.0 / 1024.0:F1} MB",
            };
            // Files left out as stored secrets are always named (never their content), so support knows they exist.
            var secrets = plan.Skipped.Where(s => s.Reason == SecretReason).ToList();
            if (secrets.Count > 0)
            {
                lines.Add($"לא נכללו {secrets.Count} קבצי מפתח שמור (לא נשלחים לעולם):");
                foreach (var (rel, _, reason) in secrets.OrderBy(s => s.RelativePath, StringComparer.Ordinal))
                    lines.Add($"  {rel} — {reason}");
            }
            // Install backups are summarised in one line: listed one by one they would fill the top-40 list and hide every
            // other reason.
            var restore = plan.Skipped.Where(s => s.Reason == RestorePointReason).ToList();
            if (restore.Count > 0)
                lines.Add($"לא נכללו {restore.Count} קבצים מתיקיית {RestorePointsFolder} ({restore.Sum(s => s.Length) / 1024.0 / 1024.0:F1} MB) — {RestorePointReason}");
            // Summarise drawing snapshots without listing their names or burying useful run skip reasons.
            var snapshots = plan.Skipped.Where(s => s.Reason == XrefReadSnapshotReason).ToList();
            if (snapshots.Count > 0)
                lines.Add($"לא נכללו {snapshots.Count} קבצים מתיקיית {XrefReadSnapshotsFolder} ({snapshots.Sum(s => s.Length) / 1024.0 / 1024.0:F1} MB) — {XrefReadSnapshotReason}");
            var others = plan.Skipped.Where(s => s.Reason != SecretReason && s.Reason != RestorePointReason && s.Reason != XrefReadSnapshotReason).ToList();
            if (others.Count > 0)
            {
                lines.Add($"לא נכללו {others.Count} קבצים (החבילה מוגבלת בגודל כדי שניתן יהיה לשלוח אותה):");
                foreach (var (rel, len, reason) in others.OrderByDescending(s => s.Length).Take(40))
                    lines.Add($"  {rel}  ({len / 1024.0 / 1024.0:F1} MB) — {reason}");
                if (others.Count > 40) lines.Add($"  … ועוד {others.Count - 40}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// A file path shown inside a Hebrew sentence: the folder reads left-to-right, the
        /// file name stays as typed. Wrapping the WHOLE path in an LTR mark reverses the
        /// Hebrew inside it (live 2026-08-19: "אומדן-מוקדם-6422-…xlsx" came out scrambled).
        /// </summary>
        public static string DisplayPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return fullPath ?? "";
            var dir = Path.GetDirectoryName(fullPath);
            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(dir)) return name;
            return Bidi.Ltr(dir + Path.DirectorySeparatorChar) + Environment.NewLine + name;
        }
    }
}
