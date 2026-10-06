using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// Finds the scans a multi-drawing BoQ is built from. The active drawing's fresh scan is authoritative for its own
    /// role; every other role of the ruleset takes the LATEST published estimate scan of a drawing in the same folder whose
    /// file name matches the role pattern — only when the records file still has the hash its manifest recorded and the
    /// drawing on disk is unchanged since that scan. Anything else is reported as a missing role, never guessed.
    /// Read-only: it never writes to the runs store.
    /// </summary>
    public static partial class BoqRulesSourceResolver
    {
        public const string RecordsArtifact = "neutral_quantity_records.json";
        public const string ScanArtifact = "estimate_scan.json";
        public const string FindingsArtifact = "quantity_preflight.json";
        public const string HatchDiagnosticsArtifact = "hatch_area_failure_diagnostics.json";
        public const string CorridorOperation = "corridor-boq";

        public static readonly IReadOnlySet<string> ScanOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "extract", "rebase", "propose", "build", "export", "export-partial",
        };

        public sealed record ActiveScan(
            string RunId, string DrawingPath, string DrawingHash,
            IReadOnlyList<NeutralQuantityRecord> Records, IReadOnlyList<DeliveryFinding> Findings)
        {
            /// <summary>Original measured scan completion time; null when older evidence did not capture it.</summary>
            public DateTime? ScannedAtUtc { get; init; }
            /// <summary>The active scan's hatch failure diagnostics (serialized), when it has any.</summary>
            public JsonElement? HatchDiagnostics { get; init; }
            /// <summary>b24 (Codex 12:45): the active scan's unit authority — required, never defaulted.</summary>
            public required ScanUnitEvidence Units { get; init; }
            /// <summary>The profile unit configuration (declarations and reviews) the active scan was measured under.</summary>
            public string? PhysicalUnitConfigurationHash { get; init; }
        }

        public sealed record Resolution(
            string ActiveRole,
            IReadOnlyList<BoqNeutralRecordAdapter.SourceScan> Scans,
            IReadOnlyList<string> MissingRoles,
            IReadOnlyList<string> Notes);

        // Same number handling as the readers of these artifacts elsewhere (NaN / Infinity may be written as strings).
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        /// <param name="currentHash">SHA-256 of a drawing as it is on disk now (tests inject it; default hashes the file).</param>
        /// <remarks>One selection path: the same observation <see cref="ResolveObserved"/> records (L05 receipt), every
        /// artifact read once — its bytes hashed and parsed from the same copy.</remarks>
        public static Resolution Resolve(BoqRuleset rules, ActiveScan active, string runsRoot, Func<string, string?>? currentHash = null) =>
            ResolveObserved(rules, active, runsRoot, currentHash).Resolution;

        /// <summary>The corridor measurement a combined bill prices chapters 51.01–51.04 from (null = none taken), the notes
        /// that say why, and the files it was verified against (drawing, measures file, raw receipt) with their SHA-256.</summary>
        public sealed record CorridorResolution(BoqRulesWorkbookWriter.CorridorInput? Input, IReadOnlyList<string> Notes,
            IReadOnlyList<(string Path, string Sha256)> Evidence);

        /// <summary>A source choice, not a quantity/scope approval. Includes refused latest runs so they cannot be hidden by older ones.</summary>
        public sealed record CorridorSourceOption(string DrawingPath, string LatestRunId, DateTime LatestCompletedAtUtc, int RunCount);

        /// <summary>Stops export when a source must be chosen or its latest measurement cannot be verified.</summary>
        public sealed class CorridorSourceSelectionException : InvalidOperationException
        {
            public CorridorSourceSelectionException(string message) : base(message) { }
        }

        /// <summary>Published corridor runs of this profile grouped by normalized full source path. Does not approve or hash drawings.</summary>
        public static IReadOnlyList<CorridorSourceOption> ListCorridorSources(string runsRoot, string? profileId) =>
            CorridorSources(CorridorRuns(runsRoot, profileId));

        private static List<Candidate> CorridorRuns(string runsRoot, string? profileId) =>
            string.IsNullOrWhiteSpace(profileId) ? new List<Candidate>() :
                ReadRunManifests(runsRoot, null, m => m.Operation == CorridorOperation &&
                    string.Equals(m.ProjectProfileId, profileId, StringComparison.Ordinal))
                .OrderByDescending(c => c.Manifest.CompletedAtUtc)
                .ThenByDescending(c => c.Manifest.RunId, StringComparer.Ordinal).ToList();

        private static string? NormalizedDrawingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }

        private static string? CorridorDrawing(Candidate run) =>
            run.Manifest.InputDrawings.Count == 0 ? null : NormalizedDrawingPath(run.Manifest.InputDrawings[0]);

        private static IReadOnlyList<CorridorSourceOption> CorridorSources(IReadOnlyList<Candidate> runs) =>
            runs.Select(r => (Run: r, Path: CorridorDrawing(r)))
                .Where(x => x.Path != null)
                .GroupBy(x => x.Path!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CorridorSourceOption(g.Key, g.First().Run.Manifest.RunId,
                    g.First().Run.Manifest.CompletedAtUtc, g.Count()))
                .OrderByDescending(x => x.LatestCompletedAtUtc)
                .ThenBy(x => x.DrawingPath, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Takes the latest run WITHIN the selected source path and profile, never global latest across different drawings.
        /// One source can be used without selection, with explicit disclosure. Multiple sources require an explicit path.
        /// A refused selected latest run throws before workbook publication; it is never replaced by an older run or omitted.
        /// The measures, rules, raw receipt, manifest/drawing identity and drawing freshness guards are unchanged.
        /// </summary>
        /// <param name="selectedDrawingPath">Explicit source for this export, not an approval of its quantities or scope.</param>
        /// <param name="currentHash">SHA-256 of a drawing as it is on disk now (tests inject it; default hashes the file).</param>
        public static CorridorResolution ResolveCorridor(string runsRoot, string? profileId, CorridorBoqRuleset rules, string rulesetSha256,
            string? selectedDrawingPath = null, Func<string, string?>? currentHash = null)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentException.ThrowIfNullOrWhiteSpace(rulesetSha256);
            currentHash ??= DiskHash;
            var none = Array.Empty<(string, string)>();
            // Snapshot the manifest inventory once. A latest run without measures is retained and refused, not skipped.
            var runs = CorridorRuns(runsRoot, profileId);
            if (runs.Count == 0 && selectedDrawingPath == null)
                return new CorridorResolution(null, new[]
                {
                    $"לא נמצאה מדידת קורידורים בפרופיל — פרקים {Bidi.Ltr("51.01–51.04")} (חישוף, עפר, מצעים, אספלט) לא נמדדו בחוברת זו. " +
                    "למדידה: 'כמויות מקורידורים' בשרטוט הקורידורים, ואחריה שוב 'כתב כמויות לפי כללים'.",
                }, none);

            var explicitSelection = selectedDrawingPath != null;
            string source;
            if (explicitSelection)
            {
                source = NormalizedDrawingPath(selectedDrawingPath) ??
                    throw new CorridorSourceSelectionException("מקור הקורידורים שנבחר אינו נתיב מלא תקין. יש לבחור שרטוט מקור מחדש; לא נוצר כתב כמויות.");
            }
            else
            {
                var options = CorridorSources(runs);
                if (options.Count != 1 || runs.Any(r => CorridorDrawing(r) == null))
                    throw new CorridorSourceSelectionException(
                        "נמצאו מדידות קורידורים מכמה שרטוטים או עם זהות מקור חסרה — יש לבחור מקור מפורש לפני ייצוא; לא נוצר כתב כמויות.");
                source = options[0].DrawingPath;
            }

            var latest = runs.FirstOrDefault(r => string.Equals(CorridorDrawing(r), source, StringComparison.OrdinalIgnoreCase));
            if (latest == null)
                throw new CorridorSourceSelectionException(
                    $"לא נמצאה מדידת קורידורים למקור שנבחר '{Bidi.Ltr(source)}' בפרופיל '{profileId}'. יש לבחור מקור עם מדידה מתאימה; לא נוצר כתב כמויות.");
            if (runs.Count(r => string.Equals(CorridorDrawing(r), source, StringComparison.OrdinalIgnoreCase) &&
                r.Manifest.CompletedAtUtc == latest.Manifest.CompletedAtUtc) > 1)
                throw new CorridorSourceSelectionException(
                    $"למקור '{Bidi.Ltr(source)}' קיימות כמה מדידות עם אותו זמן סיום אחרון; לא ניתן לקבוע איזו מדידה אחרונה. יש למדוד מחדש; לא נוצר כתב כמויות.");
            var refusal = CorridorRefusal(latest, profileId!, rules, rulesetSha256, currentHash, out var loaded, out var evidence);
            if (refusal != null || loaded == null)
                throw new CorridorSourceSelectionException(
                    $"מדידת הקורידורים האחרונה במקור '{Bidi.Ltr(source)}' ({latest.Manifest.RunId}) נפסלה — {refusal ?? "קובץ המדידה לא נקרא"}. " +
                    "לא נוצר כתב כמויות ולא נבחרה מדידה ישנה או מקור אחר. יש למדוד מחדש את המקור שנבחר או לבחור מקור מפורש אחר.");
            var measuresHash = evidence[1].Sha256;
            var context = loaded.Context with { RawReceiptPath = evidence[2].Path };
            var selectionNote = explicitSelection ? $"מקור שנבחר במפורש לייצוא: {Bidi.Ltr(Path.GetFileName(source))}. " :
                $"מקור הקורידורים היחיד בפרופיל: {Bidi.Ltr(Path.GetFileName(source))}. ";
            return new CorridorResolution(
                new BoqRulesWorkbookWriter.CorridorInput(loaded.Export, context, latest.Manifest.RunId, measuresHash, rulesetSha256),
                new[] { selectionNote + $"פרקים {Bidi.Ltr("51.01–51.04")} מהמדידה {latest.Manifest.RunId} של {Bidi.Ltr(Path.GetFileName(loaded.Header.DrawingPath))} ({loaded.Header.MeasuredLocal:dd.MM.yyyy HH:mm})." },
                evidence);
        }

        private static string? CorridorRefusal(Candidate run, string profileId, CorridorBoqRuleset rules, string rulesetSha256,
            Func<string, string?> currentHash, out CorridorBoqMeasuresFile.Loaded? loaded, out IReadOnlyList<(string Path, string Sha256)> evidence)
        {
            loaded = null;
            evidence = Array.Empty<(string, string)>();
            // A corridor run is published as a draft for review: complete (ReviewRequired) or partial (Blocked). Anything else
            // (failed, discovered, unknown) is not a measurement to price.
            if (run.Manifest.ResultStatus is not (DeliveryStatus.ReviewRequired or DeliveryStatus.Blocked))
                return $"מצב הריצה ({run.Manifest.ResultStatus}) אינו טיוטה שפורסמה לבדיקה";
            if (!Listed(run, CorridorBoqMeasuresFile.FileName, out var measuresHash))
                return "הריצה נוצרה בגרסה שלא שמרה קובץ מדידה (" + CorridorBoqMeasuresFile.FileName + ")";
            var measuresPath = Path.Combine(run.Folder, CorridorBoqMeasuresFile.FileName);
            // One shared read: the bytes whose SHA-256 is checked are the bytes that are parsed (no window between the two).
            byte[] measuresBytes;
            try
            {
                using var stream = new FileStream(measuresPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                measuresBytes = copy.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "קובץ המדידה חסר או אינו נגיש";
            }
            var measuresNow = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(measuresBytes));
            if (!string.Equals(measuresNow, measuresHash, StringComparison.OrdinalIgnoreCase))
                return "קובץ המדידה השתנה מאז שפורסם";
            try
            {
                loaded = CorridorBoqMeasuresFile.Read(new System.Text.UTF8Encoding(false).GetString(measuresBytes).TrimStart('\uFEFF'), rules, rulesetSha256);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
            {
                return "קובץ המדידה נכתב בכללי קורידורים או בפורמט שאינם של גרסה זו";
            }
            var h = loaded.Header;
            if (!string.Equals(h.ProfileId, run.Manifest.ProjectProfileId, StringComparison.Ordinal) ||
                !string.Equals(h.ProfileId, profileId, StringComparison.Ordinal))
                return $"קובץ המדידה שייך לפרופיל '{h.ProfileId ?? "—"}' ולא לפרופיל הפרויקט '{profileId}'";
            var rawPath = Path.Combine(run.Folder, h.RawReceiptFile);
            if (!string.Equals(Path.GetFileName(h.RawReceiptFile), h.RawReceiptFile, StringComparison.Ordinal) ||
                !Listed(run, h.RawReceiptFile, out var rawListed) || !string.Equals(rawListed, h.RawReceiptSha256, StringComparison.OrdinalIgnoreCase) ||
                DiskHash(rawPath) is not { } rawNow || !string.Equals(rawNow, h.RawReceiptSha256, StringComparison.OrdinalIgnoreCase))
                return "נתוני הגלם של המדידה (לכל חתך) חסרים, אינם רשומים במניפסט או השתנו";
            if (run.Manifest.InputDrawings.Count != 1 || !string.Equals(run.Manifest.InputDrawings[0], h.DrawingPath, StringComparison.OrdinalIgnoreCase) ||
                !run.Manifest.InputHashesByPath.TryGetValue(run.Manifest.InputDrawings[0], out var scannedHash) ||
                !string.Equals(scannedHash, h.DrawingSha256, StringComparison.OrdinalIgnoreCase))
                return "זהות השרטוט בקובץ המדידה אינה תואמת למניפסט הריצה";
            var drawingNow = currentHash(h.DrawingPath);
            if (drawingNow == null) return $"שרטוט הקורידורים {Bidi.Ltr(Path.GetFileName(h.DrawingPath))} אינו נגיש כעת לאימות";
            if (!string.Equals(drawingNow, h.DrawingSha256, StringComparison.OrdinalIgnoreCase))
                return $"שרטוט הקורידורים {Bidi.Ltr(Path.GetFileName(h.DrawingPath))} השתנה מאז המדידה";
            evidence = new[] { (h.DrawingPath, h.DrawingSha256), (measuresPath, measuresHash), (rawPath, h.RawReceiptSha256) };
            return null;
        }

        private static string? DiskHash(string path)
        {
            try { return File.Exists(path) ? ArtifactHash.Sha256OfFile(path) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private sealed record Candidate(string Folder, RunManifest Manifest)
        {
            public string ManifestPath { get; init; } = "";
            public string ManifestSha256 { get; init; } = "";
            public BoqSourceResolutionReceipt.Artifact? ManifestArtifact { get; init; }
        }

        private static DateTime? KnownScanTime(DateTime? value) =>
            value is { Kind: DateTimeKind.Utc } at && at != default ? at : null;

        /// <summary>Published estimate runs (never .pending-* / .replaced-*) whose manifest names its own folder, that pass the
        /// filter and, when <paramref name="requiredFile"/> is given, have that file in the folder.</summary>
        private static List<Candidate> ReadRunManifests(string runsRoot, string? requiredFile, Func<RunManifest, bool> filter) =>
            ReadRunManifestsObserved(runsRoot, requiredFile, filter, new List<BoqSourceResolutionReceipt.Artifact>());

        private static bool Listed(Candidate candidate, string artifact, out string hash)
        {
            foreach (var pair in candidate.Manifest.ArtifactHashes)
                if (string.Equals(Path.GetFileName(pair.Key), artifact, StringComparison.OrdinalIgnoreCase))
                {
                    hash = pair.Value;
                    return true;
                }
            hash = "";
            return false;
        }
    }
}
