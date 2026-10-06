using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;
using R = MahodAI.CivilDelivery.Estimate.BoqRulesV2.BoqSourceResolutionReceipt;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    public static partial class BoqRulesSourceResolver
    {
        /// <summary>The selection policy the receipt describes: per role, the latest published scan by CompletedAtUtc
        /// in the active drawing's folder (the order the resolver observed, ties included).</summary>
        public const string ResolutionPolicyVersion = "latest-completed-in-folder-v1";

        private const string RecordsNotFound = "required_records_missing";
        private const string RecordsNotReadable = "required_records_unreadable";

        /// <summary>What was read for one source, from the exact bytes that were hashed and parsed.</summary>
        public sealed record ObservedSource(string Role, string RunId, string DrawingPath,
            string ManifestPath, string ManifestSha256, string RecordsPath, string RecordsSha256);

        /// <summary>
        /// <see cref="Resolution"/> plus the observation behind it (L05, Codex B520 / 0BF95EE5): one byte read per
        /// artifact, the role partition with every candidate seen, and the run folders that could not be read.
        /// <see cref="PendingReasons"/> lists why a project-rule context built on this resolution may not be bound;
        /// it never changes <see cref="Resolution"/>, which the rules export uses exactly as before.
        /// </summary>
        public sealed record ObservedResolution(Resolution Resolution, R.Receipt? Receipt,
            IReadOnlyList<ObservedSource> Sources, IReadOnlyList<string> PendingReasons);

        public static ObservedResolution ResolveObserved(BoqRuleset rules, ActiveScan active, string runsRoot,
            Func<string, string?>? currentHash = null)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(active);
            if (rules.SourceRoles.Count == 0)
                throw new InvalidOperationException("הכללים אינם מגדירים תפקידי קבצים (source_roles) — אי אפשר לשייך את הסריקה לקובץ.");
            var activeRole = rules.SourceRoles.FirstOrDefault(r => r.Matches(active.DrawingPath))
                ?? throw new InvalidOperationException(
                    $"הקובץ הפעיל '{Path.GetFileName(active.DrawingPath)}' אינו אחד מקבצי הכללים ({string.Join(", ", rules.SourceRoles.Select(r => $"{r.Id}: *{r.FilePattern}*"))}).");
            currentHash ??= DiskHash;

            var scans = new List<BoqNeutralRecordAdapter.SourceScan>
            {
                new(activeRole.Id, active.RunId, active.DrawingPath, active.DrawingHash, active.Records, active.Findings, KnownScanTime(active.ScannedAtUtc))
                {
                    HatchDiagnostics = active.HatchDiagnostics,
                    Units = active.Units,
                },
            };
            var missing = new List<string>();
            var notes = new List<string>();
            var pending = new List<string>();
            var observed = new List<ObservedSource>();
            var choices = new List<R.Choice>();
            var folder = Path.GetDirectoryName(Path.GetFullPath(active.DrawingPath)) ?? "";
            var failures = new List<R.Artifact>();
            var manifests = ReadRunManifestsObserved(runsRoot, RecordsArtifact, manifest => ScanOperations.Contains(manifest.Operation), failures);

            choices.Add(ActiveChoice(activeRole, active, runsRoot, currentHash, observed, pending));

            foreach (var role in rules.SourceRoles.Where(r => !ReferenceEquals(r, activeRole)))
            {
                var candidates = manifests
                    .Where(m => m.Manifest.InputDrawings.Count > 0 && role.Matches(m.Manifest.InputDrawings[0]) &&
                                string.Equals(Path.GetDirectoryName(Path.GetFullPath(m.Manifest.InputDrawings[0])), folder, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => m.Manifest.CompletedAtUtc)
                    .ToList();
                var seen = candidates.Select((c, rank) => new R.Candidate(c.Manifest.RunId, c.ManifestPath, c.ManifestSha256,
                    c.Manifest.InputDrawings[0], c.Manifest.CompletedAtUtc, rank)).ToList();
                if (candidates.Count == 0)
                {
                    missing.Add(role.Id);
                    notes.Add($"לא נמצאה סריקת כמויות לקובץ {role.Id} (*{role.FilePattern}*) בתיקייה של הקובץ הפעיל — השורות שלו חסרות. יש לפתוח אותו ולסרוק כמויות.");
                    pending.Add($"missing_role:{role.Id}");
                    choices.Add(new R.Choice(role.Id, failures.Count == 0 ? R.SelectionState.Missing : R.SelectionState.Unavailable,
                        null, null, null, R.DrawingState.NotChecked, null,
                        failures.Count == 0 ? "no_scan_in_folder" : "no_readable_scan_in_folder_inventory_partial", null, seen,
                        AllNotRead(null, "no_candidate")));
                    continue;
                }
                var latest = candidates[0];
                var host = latest.Manifest.InputDrawings[0];
                var artifacts = new Dictionary<R.ArtifactKind, R.Artifact> { [R.ArtifactKind.Manifest] = latest.ManifestArtifact! };
                string? refusal = null;
                List<NeutralQuantityRecord>? records = null;
                var recordsPath = Path.Combine(latest.Folder, RecordsArtifact);
                string? recordsSha = null;

                if (!Listed(latest, RecordsArtifact, out var recordsHash))
                {
                    refusal = "קובץ הרשומות אינו רשום במניפסט הריצה";
                    artifacts[R.ArtifactKind.Records] = R.NotRead(R.ArtifactKind.Records, recordsPath, R.ReadState.Unlisted, "not_listed_in_manifest");
                }
                else
                {
                    var captured = CaptureFile<List<NeutralQuantityRecord>>(R.ArtifactKind.Records, recordsPath, recordsHash, Json);
                    artifacts[R.ArtifactKind.Records] = captured.Evidence;
                    if (captured.Evidence.State == R.ReadState.HashMismatch) refusal = "קובץ הרשומות השתנה מאז שפורסם";
                    else if (!captured.Success) refusal = "קובץ הרשומות אינו קריא";
                    else { records = captured.Value; recordsSha = captured.Evidence.ActualSha256; }
                }
                latest.Manifest.InputHashesByPath.TryGetValue(host, out var hostHash);
                string? now = null;
                if (refusal == null)
                {
                    if (string.IsNullOrWhiteSpace(hostHash)) refusal = "למניפסט אין SHA-256 של הקובץ שנסרק";
                    else if ((now = currentHash(host)) == null) refusal = "הקובץ שנסרק אינו נגיש כעת לאימות";
                    else if (!string.Equals(now, hostHash, StringComparison.OrdinalIgnoreCase)) refusal = "הקובץ השתנה מאז הסריקה";
                }
                // b24 (Codex 12:45): the run's own unit evidence, read from its hash-verified scan header, governs its records.
                var scannedAt = CaptureScanTime(latest, host, hostHash, out var scanMetadata, out var runUnits, out var runUnitConfiguration);
                artifacts[R.ArtifactKind.ScanMetadata] = scanMetadata;
                if (refusal == null && records != null &&
                    (BoqNeutralRecordAdapter.LegacyEvidenceRefusal(records) ?? BoqNeutralRecordAdapter.UnitRefusal(records) ??
                     BoqNeutralRecordAdapter.ScanUnitRefusal(runUnits, records)) is { } evidenceRefusal)
                    refusal = evidenceRefusal;
                // b24 (Codex 13:11): a Bound run must name its unit configuration and it must be the active one; a Legacy run
                // without one is taken only while the profile has no unit content at all.
                if (refusal == null && active.PhysicalUnitConfigurationHash != null &&
                    (runUnitConfiguration != null
                        ? !string.Equals(active.PhysicalUnitConfigurationHash, runUnitConfiguration, StringComparison.Ordinal)
                        : runUnits.State == ScanUnitEvidenceState.Bound ||
                          !string.Equals(active.PhysicalUnitConfigurationHash, ScanUnitEvidence.EmptyUnitConfigurationHash, StringComparison.Ordinal)))
                    refusal = "הגדרות היחידות בפרופיל (הכרעה או בירור) השתנו מאז הסריקה, או שהסריקה לא רשמה אותן";
                if (refusal != null)
                {
                    missing.Add(role.Id);
                    notes.Add($"{role.Id}: הסריקה האחרונה ({latest.Manifest.RunId}) לא נלקחה — {refusal}. יש לסרוק מחדש את {Path.GetFileName(host)}.");
                    pending.Add($"rejected_role:{role.Id}");
                    foreach (var kind in Enum.GetValues<R.ArtifactKind>().Where(k => !artifacts.ContainsKey(k)))
                        artifacts[kind] = R.NotRead(kind, null, R.ReadState.NotRead, "not_read_after_refusal");
                    choices.Add(new R.Choice(role.Id, R.SelectionState.Rejected, latest.Manifest.RunId, host,
                        string.IsNullOrWhiteSpace(hostHash) ? null : hostHash,
                        now == null ? R.DrawingState.NotChecked
                            : string.Equals(now, hostHash, StringComparison.OrdinalIgnoreCase) ? R.DrawingState.Unchanged : R.DrawingState.Changed,
                        now, "latest_scan_refused",
                        null, seen, artifacts.Values.ToList()));
                    continue;
                }

                var findings = new List<DeliveryFinding>();
                var findingsPath = Path.Combine(latest.Folder, FindingsArtifact);
                var findingsRead = CaptureListed<List<DeliveryFinding>>(latest, R.ArtifactKind.Findings, FindingsArtifact, findingsPath);
                artifacts[R.ArtifactKind.Findings] = findingsRead.Evidence;
                if (findingsRead.Success) findings = findingsRead.Value!;
                else notes.Add($"{role.Id}: ממצאי הסריקה (עצמים שלא נמדדו) לא נקראו — ייתכן שחסרים לא מוצגים.");

                JsonElement? diagnostics = null;
                var diagnosticsRead = CaptureListed<JsonElement>(latest, R.ArtifactKind.HatchDiagnostics, HatchDiagnosticsArtifact,
                    Path.Combine(latest.Folder, HatchDiagnosticsArtifact));
                artifacts[R.ArtifactKind.HatchDiagnostics] = diagnosticsRead.Evidence;
                if (diagnosticsRead.Success) diagnostics = diagnosticsRead.Value;

                scans.Add(new BoqNeutralRecordAdapter.SourceScan(role.Id, latest.Manifest.RunId, host, hostHash ?? "", records!, findings, scannedAt)
                {
                    HatchDiagnostics = diagnostics,
                    Units = runUnits,
                });
                observed.Add(new ObservedSource(role.Id, latest.Manifest.RunId, host, latest.ManifestPath, latest.ManifestSha256,
                    recordsPath, recordsSha!));
                choices.Add(new R.Choice(role.Id, R.SelectionState.Selected, latest.Manifest.RunId, host, hostHash, R.DrawingState.Unchanged,
                    now, null, scannedAt, seen, artifacts.Values.ToList()));
            }

            // An inventory failure keeps the receipt Partial. It blocks binding unless its content proves it cannot be a candidate
            // of this policy (Codex 23:25, BABB60AB §2) — never because of the time in its folder name:
            //  • a folder with no run manifest at all was never published, and the policy ranks published manifests only (the
            //    current observation re-reads the inventory, so a manifest that appears later makes the context stale);
            //  • a published scan whose records are missing stays a ranked candidate (above): when it is the latest of its role
            //    it is refused explicitly, otherwise a newer readable manifest of the same role outranks it.
            // A manifest that exists but cannot be read or parsed hides its role and time: pending.
            foreach (var failure in failures)
                if (!(failure.Kind == R.ArtifactKind.Manifest && failure.State == R.ReadState.Missing) &&
                    !(failure.Kind == R.ArtifactKind.Records && failure.ReasonCode is RecordsNotFound or RecordsNotReadable))
                {
                    pending.Add("inventory_unreadable_run:" + Path.GetFileName(Path.GetDirectoryName(failure.Path ?? "") ?? ""));
                }

            R.Receipt? receipt = null;
            try
            {
                receipt = R.Build(new R.Request(ResolutionPolicyVersion, rules.Sha256, active.RunId, folder,
                    failures.Count == 0 ? R.InventoryState.Complete : R.InventoryState.Partial, failures,
                    rules.SourceRoles.Select((r, i) => new R.Role(r.Id, r.FilePattern, i)).ToList(), choices));
            }
            catch (ArgumentException ex)
            {
                pending.Add("receipt_invalid:" + ex.Message);
            }
            return new ObservedResolution(new Resolution(activeRole.Id, scans, missing, notes), receipt, observed, pending);
        }

        private static R.Choice ActiveChoice(BoqSourceRole role, ActiveScan active, string runsRoot,
            Func<string, string?> currentHash, List<ObservedSource> observed, List<string> pending)
        {
            var folder = Path.Combine(runsRoot, active.RunId);
            var manifestPath = Path.Combine(folder, "run_manifest.json");
            var artifacts = new Dictionary<R.ArtifactKind, R.Artifact>();
            var manifestRead = CaptureFile<RunManifest>(R.ArtifactKind.Manifest, manifestPath, null, RunManifestWriter.JsonOptions);
            artifacts[R.ArtifactKind.Manifest] = manifestRead.Evidence;
            var now = currentHash(active.DrawingPath);
            var drawingState = now == null ? R.DrawingState.Unavailable
                : string.Equals(now, active.DrawingHash, StringComparison.OrdinalIgnoreCase) ? R.DrawingState.Unchanged : R.DrawingState.Changed;
            if (drawingState != R.DrawingState.Unchanged) pending.Add("active_drawing_" + drawingState.ToString().ToLowerInvariant());
            if (!manifestRead.Success || manifestRead.Value is not { } manifest || !string.Equals(manifest.RunId, active.RunId, StringComparison.Ordinal))
            {
                pending.Add("active_manifest_unreadable");
                foreach (var kind in Enum.GetValues<R.ArtifactKind>().Where(k => !artifacts.ContainsKey(k)))
                    artifacts[kind] = R.NotRead(kind, null, R.ReadState.NotRead, "active_manifest_unreadable");
                return new R.Choice(role.Id, R.SelectionState.Unavailable, active.RunId, active.DrawingPath, active.DrawingHash,
                    drawingState, now, "active_manifest_unreadable", null, Array.Empty<R.Candidate>(), artifacts.Values.ToList());
            }
            var candidate = new Candidate(folder, manifest)
            {
                ManifestPath = manifestPath, ManifestSha256 = manifestRead.Evidence.ActualSha256!, ManifestArtifact = manifestRead.Evidence,
            };
            var recordsPath = Path.Combine(folder, RecordsArtifact);
            var recordsRead = CaptureListed<List<NeutralQuantityRecord>>(candidate, R.ArtifactKind.Records, RecordsArtifact, recordsPath);
            artifacts[R.ArtifactKind.Records] = recordsRead.Evidence;
            // The published records must be exactly the records the active scan object holds (Codex B520 / 0BF95EE5 §3).
            var recordsEqual = recordsRead.Success &&
                string.Equals(ProjectRuleRecordContext.RecordDigest(recordsRead.Value!), ProjectRuleRecordContext.RecordDigest(active.Records), StringComparison.Ordinal);
            if (!recordsEqual) pending.Add("active_published_records_differ");
            artifacts[R.ArtifactKind.Findings] = CaptureListed<List<DeliveryFinding>>(candidate, R.ArtifactKind.Findings, FindingsArtifact,
                Path.Combine(folder, FindingsArtifact)).Evidence;
            artifacts[R.ArtifactKind.HatchDiagnostics] = CaptureListed<JsonElement>(candidate, R.ArtifactKind.HatchDiagnostics, HatchDiagnosticsArtifact,
                Path.Combine(folder, HatchDiagnosticsArtifact)).Evidence;
            CaptureScanTime(candidate, active.DrawingPath, active.DrawingHash, out var scanMetadata, out _, out _);
            artifacts[R.ArtifactKind.ScanMetadata] = scanMetadata;
            var scannedAt = KnownScanTime(active.ScannedAtUtc);
            if (scannedAt != null && scanMetadata.State != R.ReadState.Parsed) scannedAt = null;
            if (recordsEqual && drawingState == R.DrawingState.Unchanged)
                observed.Add(new ObservedSource(role.Id, active.RunId, active.DrawingPath, manifestPath, candidate.ManifestSha256,
                    recordsPath, recordsRead.Evidence.ActualSha256!));
            return new R.Choice(role.Id,
                recordsEqual && drawingState == R.DrawingState.Unchanged ? R.SelectionState.Active : R.SelectionState.Unavailable,
                active.RunId, active.DrawingPath, active.DrawingHash, drawingState, now,
                recordsEqual && drawingState == R.DrawingState.Unchanged ? null : "active_source_not_proven",
                scannedAt, Array.Empty<R.Candidate>(), artifacts.Values.ToList());
        }

        private static List<R.Artifact> AllNotRead(string? path, string reason) =>
            Enum.GetValues<R.ArtifactKind>().Select(kind => R.NotRead(kind, path, R.ReadState.NotRead, reason)).ToList();

        /// <summary>One read of the file: hash and parse the same bytes (never a second open after hashing). The outcome is the
        /// read's own: Missing only when the file is proven absent (<see cref="ProvenAbsent"/>); denied access, an I/O failure,
        /// a directory at the file's path or a vanished parent folder is Unavailable — never absence by default (Codex 23:55).</summary>
        internal static R.Parsed<T> CaptureFile<T>(R.ArtifactKind kind, string path, string? listedSha256, JsonSerializerOptions options)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (FileNotFoundException) when (ProvenAbsent(path))
            {
                return new R.Parsed<T>(R.NotRead(kind, path, R.ReadState.Missing, "file_missing", Sha(listedSha256)), default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return new R.Parsed<T>(R.NotRead(kind, path, R.ReadState.Unavailable, "file_unreadable", Sha(listedSha256)), default);
            }
            return R.CaptureJson<T>(kind, path, bytes, Sha(listedSha256), options);
        }

        /// <summary>The file is absent only when its parent folder exists, was listed successfully (hidden and system entries
        /// included) and holds no entry of that name — of any type.</summary>
        private static bool ProvenAbsent(string path)
        {
            try
            {
                var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
                if (!parent.Exists) return false;
                var options = new EnumerationOptions
                {
                    MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple, RecurseSubdirectories = false,
                    IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false,
                };
                return !parent.EnumerateFileSystemInfos(Path.GetFileName(path), options).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return false;
            }
        }

        private static R.Parsed<T> CaptureListed<T>(Candidate candidate, R.ArtifactKind kind, string artifact, string path) =>
            Listed(candidate, artifact, out var listed)
                ? CaptureFile<T>(kind, path, listed, Json)
                : new R.Parsed<T>(R.NotRead(kind, path, R.ReadState.Unlisted, "not_listed_in_manifest"), default);

        private static string? Sha(string? value) =>
            value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value : null;

        /// <summary>Published estimate runs, as <see cref="ReadRunManifests"/>, with every manifest read once (bytes hashed and parsed
        /// from the same copy), and the run folders that could not be read recorded in <paramref name="failures"/>.</summary>
        private static List<Candidate> ReadRunManifestsObserved(string runsRoot, string? requiredFile, Func<RunManifest, bool> filter,
            List<R.Artifact> failures)
        {
            var result = new List<Candidate>();
            if (string.IsNullOrWhiteSpace(runsRoot) || !Directory.Exists(runsRoot)) return result;
            foreach (var directory in Directory.EnumerateDirectories(runsRoot))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue; // .pending-* / .replaced-* are never evidence
                var manifestPath = Path.Combine(directory, "run_manifest.json");
                var read = CaptureFile<RunManifest>(R.ArtifactKind.Manifest, manifestPath, null, RunManifestWriter.JsonOptions);
                if (!read.Success)
                {
                    // An estimate run folder whose manifest is missing or unreadable: its role/drawing is unknown, so the
                    // inventory is partial. Folders of other features cannot hold an estimate scan and are not counted.
                    if (requiredFile != null && name.StartsWith("estimate-", StringComparison.Ordinal)) failures.Add(read.Evidence);
                    continue;
                }
                var manifest = read.Value!;
                if (manifest.Feature != "estimate" || !filter(manifest) || !string.Equals(manifest.RunId, name, StringComparison.Ordinal)) continue;
                // A published scan whose records are missing (or not readable) is recorded and stays a candidate: if it is the
                // latest of its role the selection refuses it by its actual capture, never silently taking an older scan (Codex 23:25).
                var requiredPath = requiredFile == null ? null : Path.Combine(directory, requiredFile);
                if (requiredPath != null && !File.Exists(requiredPath))
                    failures.Add(ProvenAbsent(requiredPath)
                        ? R.NotRead(R.ArtifactKind.Records, requiredPath, R.ReadState.Missing, RecordsNotFound)
                        : R.NotRead(R.ArtifactKind.Records, requiredPath, R.ReadState.Unavailable, RecordsNotReadable));
                result.Add(new Candidate(directory, manifest)
                {
                    ManifestPath = manifestPath, ManifestSha256 = read.Evidence.ActualSha256!, ManifestArtifact = read.Evidence,
                });
            }
            return result;
        }

        // A manifest can be republished by rebase/build/export without another measurement. Only the captured time inside
        // that run's proven scan payload describes the read; the artifact records exactly what was hashed (one handle).
        /// <summary>The PhysicalUnits object of a scan header (estimate_scan.json); authorityWritten tells whether the
        /// writer knew the unit contract (b24+ always writes Authority).</summary>
        private static ScanPhysicalUnits? ReadPhysicalUnits(ref Utf8JsonReader reader, out bool authorityPresent, out bool authorityIsText)
        {
            authorityPresent = false;
            authorityIsText = false;
            bool? supported = null; int? raw = null, effective = null; double? factor = null; string? authority = null, digest = null;
            var depth = reader.CurrentDepth;
            while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != depth + 1) continue;
                var name = reader.GetString();
                if (!reader.Read()) break;
                switch (name)
                {
                    case "IsSupported" when reader.TokenType is JsonTokenType.True or JsonTokenType.False: supported = reader.GetBoolean(); break;
                    case "RawUnitCode" when reader.TokenType == JsonTokenType.Number: raw = reader.GetInt32(); break;
                    case "EffectiveUnitCode" when reader.TokenType == JsonTokenType.Number: effective = reader.GetInt32(); break;
                    case "LinearToMetres" when reader.TokenType == JsonTokenType.Number: factor = reader.GetDouble(); break;
                    case "DeclarationDigest" when reader.TokenType == JsonTokenType.String: digest = reader.GetString(); break;
                    case "Authority":
                        authorityPresent = true;
                        if (reader.TokenType == JsonTokenType.String) { authority = reader.GetString(); authorityIsText = true; }
                        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                        break;
                    default:
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                        break;
                }
            }
            return supported is { } s && raw is { } r && factor is { } k
                ? new ScanPhysicalUnits(s, r, effective, k, authority, digest) : null;
        }

        private static DateTime? CaptureScanTime(Candidate candidate, string host, string? hostHash, out R.Artifact artifact,
            out ScanUnitEvidence units, out string? unitConfiguration)
        {
            units = ScanUnitEvidence.Unknown("the scan header was not read");
            unitConfiguration = null;
            var path = Path.Combine(candidate.Folder, ScanArtifact);
            if (!Listed(candidate, ScanArtifact, out var hash))
            {
                artifact = R.NotRead(R.ArtifactKind.ScanMetadata, path, R.ReadState.Unlisted, "not_listed_in_manifest");
                return null;
            }
            var listed = Sha(hash);
            if (!File.Exists(path))
            {
                artifact = ProvenAbsent(path)
                    ? R.NotRead(R.ArtifactKind.ScanMetadata, path, R.ReadState.Missing, "file_missing", listed)
                    : R.NotRead(R.ArtifactKind.ScanMetadata, path, R.ReadState.Unavailable, "file_unreadable", listed);
                return null;
            }
            try
            {
                // Keep one read-only handle: hash the complete artifact without a second large
                // payload allocation, then inspect only its small serialized metadata header.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                var length = stream.Length;
                if (listed == null || !string.Equals(actual, listed, StringComparison.OrdinalIgnoreCase))
                {
                    artifact = listed == null
                        ? R.NotRead(R.ArtifactKind.ScanMetadata, path, R.ReadState.Unlisted, "listed_hash_invalid")
                        : new R.Artifact(R.ArtifactKind.ScanMetadata, path, R.ReadState.HashMismatch, length, actual, listed, "artifact_hash_mismatch");
                    return null;
                }
                stream.Position = 0;
                var header = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
                stream.ReadExactly(header);
                var reader = new Utf8JsonReader(header, isFinalBlock: stream.Position == stream.Length, state: default);
                artifact = new R.Artifact(R.ArtifactKind.ScanMetadata, path, R.ReadState.Parsed, length, actual, listed, null);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
                string? runId = null, drawing = null, drawingHash = null, contract = null;
                DateTime? captured = null;
                ScanPhysicalUnits? physical = null;
                bool authorityPresent = false, authorityIsText = false, contractPresent = false;
                while (reader.Read())
                {
                    if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName) continue;
                    var name = reader.GetString();
                    if (name == "Records") break;   // the header ends where the records begin
                    if (!reader.Read()) break;
                    if (name == "PhysicalUnits" && reader.TokenType == JsonTokenType.StartObject)
                    {
                        physical = ReadPhysicalUnits(ref reader, out authorityPresent, out authorityIsText);
                        continue;
                    }
                    if (name == "PhysicalUnitsContract")
                    {
                        // Presence counts whatever the value: null, a number or a wrong string is never "no marker".
                        contractPresent = true;
                        contract = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        switch (name)
                        {
                            case "RunId": runId = reader.GetString(); break;
                            case "SourceDrawing": drawing = reader.GetString(); break;
                            case "SourceDrawingHash": drawingHash = reader.GetString(); break;
                            case "PhysicalUnitConfigurationHash": unitConfiguration = reader.GetString(); break;
                            case "ScannedAtUtc":
                                if (reader.TryGetDateTime(out var at)) captured = KnownScanTime(at);
                                break;
                        }
                    }
                }
                var identity = runId != null && drawing != null && drawingHash != null &&
                               string.Equals(runId, candidate.Manifest.RunId, StringComparison.Ordinal) &&
                               string.Equals(drawing, host, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(drawingHash, hostHash, StringComparison.OrdinalIgnoreCase);
                // Unit evidence counts only for the run and drawing this header belongs to.
                units = identity ? ScanUnitEvidence.FromHeader(contractPresent, contract, physical, authorityPresent, authorityIsText)
                    : ScanUnitEvidence.Unknown("the scan header does not name this run and drawing");
                // Old or nonstandard payloads without proven metadata in the bounded header stay unknown.
                return identity ? captured : null;
            }
            catch (JsonException)
            {
                artifact = R.NotRead(R.ArtifactKind.ScanMetadata, path, R.ReadState.Unavailable, "header_unreadable", listed);
                units = ScanUnitEvidence.Unknown("the scan header is unreadable");
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                artifact = R.NotRead(R.ArtifactKind.ScanMetadata, path, R.ReadState.Unavailable, "file_unreadable", listed);
                return null;
            }
        }
    }
}
