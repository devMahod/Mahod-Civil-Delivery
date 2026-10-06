using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// First-run / re-configuration service: scan the drawing for real candidates,
    /// apply the engineer's explicit choices to the ProjectProfile, and persist them
    /// with provenance. Shared by the direct command and the MahodAI route.
    /// </summary>
    public sealed class ProjectSetupService
    {
        private readonly ProjectSetupScanner _scanner = new();

        internal sealed record SourceEvidence(
            string DrawingPath,
            string DrawingHash,
            string DatabaseRevision,
            int DbMod);

        public ProjectSetupScan Scan(
            Document doc,
            ProjectProfile profile,
            string profileHash,
            string profileSource,
            string profileWriteTarget,
            ProjectProfileWriter.ExpectedProfileState profileWriteState,
            StageLog? log = null)
        {
            ArgumentNullException.ThrowIfNull(profileWriteState);
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, profileWriteState);
            var db = doc.Database;
            var source = CaptureReadySource(doc, "project setup scan");
            log?.Begin("setup.start_transaction");
            ProjectSetupScan scan;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var civilDoc = CivilDocument.GetCivilDocument(db);
                log?.End("setup.start_transaction");
                scan = _scanner.Scan(db, tr, civilDoc, profile, log);
                IncludeConfiguredExternalCl(scan, profile, source.DrawingPath, civilDoc, tr, log);
                BindScanEvidence(
                    scan, profile, profileHash, profileSource,
                    profileWriteTarget, source,
                    profileWriteState);
                // Candidate discovery is read-only. Civil getters may lazily touch the
                // database, so commit would turn a scan into an undocumented mutation.
                tr.Abort();
            }

            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, profileWriteState);

            PublishScanEvidenceAfterReadClose(doc, scan);
            return scan;
        }

        internal static SourceEvidence CaptureReadySource(Document doc, string operation)
        {
            var source = DrawingRevisionTracker.CaptureLive(doc);
            var failure = source.Failure ?? EstimateSourceSnapshotPolicy.InitialFailure(
                source.DrawingHash, source.DbMod);
            if (failure != null || source.DbMod != 0)
                throw new InvalidOperationException(
                    $"{operation} requires one saved, unchanged active drawing: " +
                    (failure ?? $"DBMOD={source.DbMod}"));
            return new SourceEvidence(
                source.DrawingPath, source.DrawingHash, source.DatabaseRevision,
                source.DbMod.Value);
        }

        internal static void BindScanEvidence(
            ProjectSetupScan scan,
            ProjectProfile profile,
            string profileHash,
            string profileSource,
            string profileWriteTarget,
            SourceEvidence source,
            ProjectProfileWriter.ExpectedProfileState profileWriteState)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(profile);
            if (!CatalogIdentity.IsValidSha256(profileHash))
                throw new InvalidOperationException(
                    "Project setup scan requires the exact source profile SHA-256.");
            if (string.IsNullOrWhiteSpace(profileSource))
                throw new InvalidOperationException(
                    "Project setup scan requires an authoritative profile source identity.");
            ArgumentNullException.ThrowIfNull(profileWriteState);
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, profileWriteState);
            if (profileWriteState.SourceExisted && !File.Exists(profileSource))
                throw new InvalidOperationException(
                    "Project setup scan requires the existing source profile file selected at load time.");
            if (!string.Equals(Path.GetFullPath(profileWriteTarget),
                    Path.GetFullPath(profileWriteState.TargetPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(profileSource),
                    Path.GetFullPath(profileWriteState.SourcePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(profileHash, profileWriteState.SourceHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Project setup profile evidence differs from the state captured at profile load time.");

            scan.Drawing = source.DrawingPath;
            scan.DrawingHash = source.DrawingHash;
            scan.DatabaseRevision = source.DatabaseRevision;
            scan.SourceDbMod = source.DbMod;
            scan.ProfileSource = Path.GetFullPath(profileSource);
            scan.ProjectProfileHash = profileHash;
            scan.ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
            scan.ProfileWriteState = profileWriteState;
        }

        internal static void PublishScanEvidenceAfterReadClose(
            Document doc, ProjectSetupScan scan)
        {
            RequireCurrentDrawing(doc, scan, "project setup scan publication");
            RequireExternalSourcesUnchanged(scan);
            SectionsWorkflowService.WriteArtifact(
                scan.RunId, "project_setup_scan.json", scan);
        }

        internal static void RequireFreshAndPublished(
            Document doc,
            ProjectProfile profile,
            ProjectSetupScan scan,
            string profileHash,
            string profileSource,
            string? runsRoot = null)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(scan);
            if (!string.Equals(scan.ProjectProfileId, profile.ProfileId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The setup scan belongs to a different project profile.");

            RequireCurrentDrawing(doc, scan, "project setup decision");
            RequireExternalSourcesUnchanged(scan);
            if (!CatalogIdentity.IsValidSha256(profileHash) ||
                !string.Equals(scan.ProjectProfileHash, profileHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(scan.ProjectProfileEffectiveHash,
                    EstimateTraceIdentity.EffectiveProfileHash(profile),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The project profile changed after the setup scan.");

            var expectedSource = Path.GetFullPath(profileSource);
            var expectedState = scan.ProfileWriteState ??
                throw new InvalidOperationException(
                    "The project setup scan has no profile compare-and-swap evidence.");
            if (string.IsNullOrWhiteSpace(scan.ProfileSource) ||
                !string.Equals(Path.GetFullPath(scan.ProfileSource), expectedSource,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The source project-profile identity changed after the setup scan.");

            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);

            RequirePublishedScanEvidence(scan, runsRoot);
        }

        internal static void RequirePublishedScanEvidence(
            ProjectSetupScan scan, string? runsRoot = null)
        {
            if (string.IsNullOrWhiteSpace(scan.RunId) ||
                !string.Equals(Path.GetFileName(scan.RunId), scan.RunId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("The setup scan run id is invalid.");
            var root = Path.GetFullPath(runsRoot ?? SectionsWorkflowService.RunsRoot);
            var artifact = Path.GetFullPath(Path.Combine(
                root, scan.RunId, "project_setup_scan.json"));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!artifact.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(artifact))
                throw new InvalidOperationException(
                    "The exact project setup scan was not published.");

            var expected = ArtifactHash.Sha256OfText(
                JsonSerializer.Serialize(scan, SectionsWorkflowService.Json));
            var actual = ArtifactHash.Sha256OfText(File.ReadAllText(artifact));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The published project setup scan does not equal the reviewed scan.");
        }

        internal static void IncludeConfiguredExternalCl(ProjectSetupScan scan, ProjectProfile profile,
            string hostDrawingPath, CivilDocument civilDoc, Autodesk.AutoCAD.DatabaseServices.Transaction tr,
            StageLog? log = null)
        {
            var scanner = new ProjectSetupScanner();
            foreach (var configuredPath in profile.Sections.Cl.SourceFiles)
            {
                try
                {
                    var path = ClInstructionReader.ResolvePath(configuredPath, hostDrawingPath);
                    if (ClSourceSelection.IsHostDrawing(path ?? "", hostDrawingPath)) continue;
                    if (path == null || !File.Exists(path))
                        throw new InvalidOperationException("קובץ CL אינו זמין.");
                    if (scan.ExternalClHashes.ContainsKey(path)) continue;
                    MergeExternalClScan(scan, scanner.ScanClFile(path, civilDoc, tr, log));
                }
                catch (Exception ex)
                {
                    // Keep the review/picker reachable, but never save a partial scan.
                    RecordExternalClFailure(scan, configuredPath, ex.Message);
                }
            }
            if (scan.ClLayerCandidates.Count > 0)
                scan.Findings.RemoveAll(f => f.Code == SectionFindingCodes.ClSourceMissing);
        }

        internal static void RecordExternalClFailure(ProjectSetupScan scan, string path, string detail)
        {
            scan.ScanComplete = false;
            scan.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.SourceMissing, Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error, ProjectProfileId = scan.ProjectProfileId,
                Title = "לא ניתן להשלים קריאה של מקור CL נפרד",
                Message = path + "\n" + detail,
                RecommendedAction = "בחר קובץ CL זמין בחלון ההגדרה, או תקן את המקור וסרוק מחדש. הבחירות לא יישמרו על סריקה חלקית.",
            });
        }

        /// <summary>
        /// The CL part of a setup selection as one unit: an unknown scope or mode, a scope without a CL file, or the
        /// station-markers mode without a valid width or approver is refused before anything is written.
        /// </summary>
        internal static void ValidateClSelection(ProjectSetupSelection selection)
        {
            ArgumentNullException.ThrowIfNull(selection);
            var candidate = new ProjectProfile.SectionsProfile.ClProfile
            {
                LayerScope = selection.ClLayerScope,
                Mode = selection.ClMode,
                StationMarkerHalfWidthM = selection.ClMode != null ? selection.StationMarkerHalfWidthM : null,
                StationMarkerApprovedBy = selection.ClMode != null ? selection.ApprovedBy?.Trim() : null,
            };
            candidate.SourceFiles.AddRange(selection.ClSourceFiles.Where(v => !string.IsNullOrWhiteSpace(v)));
            var problems = ProjectProfileSchemaPolicy.ClScopeProblems(candidate);
            if (problems.Count > 0)
                throw new InvalidOperationException(string.Join(" ", problems));
        }

        internal static void MergeExternalClScan(
            ProjectSetupScan scan, ProjectSetupScanner.ExternalClScan external)
        {
            external.RequireSourceUnchanged();
            scan.ExternalClHashes.Add(external.Path, external.SourceHash);
            foreach (var (path, hash) in external.DiscoverySourceHashes)
            {
                if (scan.DiscoverySourceHashes.TryGetValue(path, out var existingHash) &&
                    !string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("מקור XREF השתנה תוך כדי גילוי מקורות החתך.");
                scan.DiscoverySourceHashes[path] = hash;
            }
            foreach (var candidate in external.Candidates)
            {
                var existing = scan.ClLayerCandidates.FirstOrDefault(c =>
                    string.Equals(c.Layer, candidate.Layer, StringComparison.OrdinalIgnoreCase));
                // Explicit source identity (Codex 11:51): a same-named host row stays FromHost, so the merged row is
                // mixed and can never be scoped to the separate file.
                (existing ?? candidate).FromSeparateFile = true;
                if (existing == null) scan.ClLayerCandidates.Add(candidate);
                else
                {
                    existing.LineCount += candidate.LineCount;
                    existing.PolylineCount += candidate.PolylineCount;
                    existing.TwoPointCount += candidate.TwoPointCount;
                    existing.CrossingCount += candidate.CrossingCount;
                    existing.EvidenceScore = Math.Max(existing.EvidenceScore, candidate.EvidenceScore);
                    foreach (var alignment in candidate.AlignmentsCrossed)
                        if (!existing.AlignmentsCrossed.Contains(alignment, StringComparer.OrdinalIgnoreCase))
                            existing.AlignmentsCrossed.Add(alignment);
                }
                (existing ?? candidate).Why.Add("קובץ CL נפרד: " + Path.GetFileName(external.Path));
            }
            scan.ClLayerCandidates.Sort((a, b) => b.EvidenceScore.CompareTo(a.EvidenceScore));
        }

        internal static void RequireExternalSourcesUnchanged(ProjectSetupScan scan)
        {
            foreach (var (path, hash) in scan.ExternalClHashes.Concat(scan.DiscoverySourceHashes))
                if (!CatalogIdentity.IsValidSha256(hash) || !File.Exists(path) ||
                    !string.Equals(ArtifactHash.Sha256OfFile(path), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"מקור CL השתנה מאז ההגדרה: {Path.GetFileName(path)}. הבחירות לא נשמרו; סרוק שוב.");
        }

        private static void RequireCurrentDrawing(
            Document doc, ProjectSetupScan scan, string operation)
        {
            var current = CaptureReadySource(doc, operation);
            if (!string.Equals(scan.Drawing, current.DrawingPath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(scan.DrawingHash, current.DrawingHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(scan.DatabaseRevision, current.DatabaseRevision,
                    StringComparison.Ordinal) ||
                scan.SourceDbMod != current.DbMod)
                throw new InvalidOperationException(
                    $"{operation} refused stale drawing evidence; run setup scan again.");
        }

        /// <summary>
        /// Applies explicit selections to the in-memory profile. Every write here
        /// corresponds to a choice the engineer made on the setup screen.
        /// </summary>
        public static void ApplySelection(ProjectProfile profile, ProjectSetupSelection selection)
        {
            // 1.4.1 (Codex 11:51): validate the whole CL part BEFORE any mutation, so a refused selection leaves the
            // in-memory profile exactly as it was.
            ValidateClSelection(selection);
            // Setup is a complete approved snapshot, not a sparse patch. Keeping a
            // value just because the new selection is empty leaks stale CL rules,
            // alignments and sampled sources from a previous drawing.
            profile.Sections.Cl.LayerPatterns.Clear();
            profile.Sections.Cl.LayerPatterns.AddRange(selection.ClLayers
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

            profile.Sections.Cl.SourceFiles.Clear();
            profile.Sections.Cl.SourceFiles.AddRange(selection.ClSourceFiles
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

            // 1.4.1: part of the same complete snapshot (validated above). A selection without these fields returns to
            // the b34 behaviour.
            profile.Sections.Cl.LayerScope = selection.ClLayerScope != null ? ClInstructionReader.LayerScopeSourceFile : null;
            var stationMarkers = selection.ClMode != null;
            profile.Sections.Cl.Mode = stationMarkers ? SectionStationMarkerLogic.ModeStationMarkers : null;
            profile.Sections.Cl.StationMarkerHalfWidthM = stationMarkers ? selection.StationMarkerHalfWidthM : null;
            profile.Sections.Cl.StationMarkerApprovedBy = stationMarkers ? selection.ApprovedBy?.Trim() : null;

            profile.Sections.Alignments.AllowedNames.Clear();
            profile.Sections.Alignments.AllowedNames.AddRange(selection.AllowedAlignments
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

            profile.Sections.Sources.SampledSourceRules.Clear();
            var sampledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, kind) in selection.SampledSources)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind)) continue;
                var cleanName = name.Trim();
                if (!sampledNames.Add(cleanName)) continue;
                profile.Sections.Sources.SampledSourceRules.Add(
                    new ProjectProfile.SectionsProfile.SourcesProfile.SourceRule
                    {
                        Name = cleanName,
                        Kind = kind.Trim(),
                        NamePattern = cleanName,
                        Required = true,
                    });
            }

            profile.Sections.Cl.IntersectionToleranceM =
                selection.IntersectionToleranceM is > 0 and <= 5
                    ? selection.IntersectionToleranceM
                    : null;
        }

        /// <summary>Persists the configured profile to the runtime (writable) location.</summary>
        public static ProjectProfileWriter.SaveResult Save(
            Document doc,
            ProjectProfile profile,
            ProjectSetupSelection selection,
            ProjectSetupScan scan,
            string profileHash,
            string profileSource,
            string targetPath)
        {
            RequireFreshAndPublished(
                doc, profile, scan, profileHash, profileSource);
            var expectedState = scan.ProfileWriteState ??
                throw new InvalidOperationException(
                    "The setup scan has no captured profile compare-and-swap state.");
            return SaveCore(profile, selection, scan, targetPath, expectedState);
        }

        internal static ProjectProfileWriter.SaveResult SaveCoreForContractTests(
            ProjectProfile profile,
            ProjectSetupSelection selection,
            ProjectSetupScan scan,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState? expectedProfileState = null)
        {
            // Explicitly test-only: older validation/rollback fixtures do not load an
            // ActiveLoadResult. Give them a generated target-absence baseline without
            // reintroducing any no-CAS production overload.
            expectedProfileState ??=
                ProjectProfileWriter.CaptureExpectedGeneratedState(
                    profile, EstimateTraceIdentity.EffectiveProfileHash(profile),
                    Path.GetFullPath(targetPath));
            return SaveCore(profile, selection, scan, targetPath,
                expectedProfileState);
        }

        private static ProjectProfileWriter.SaveResult SaveCore(
            ProjectProfile profile,
            ProjectSetupSelection selection,
            ProjectSetupScan scan,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            if (string.IsNullOrWhiteSpace(targetPath) ||
                !Path.IsPathFullyQualified(targetPath))
                throw new ArgumentException(
                    "An authoritative profile write target is required.", nameof(targetPath));
            // b24 (Codex 11:18): the approver is the one the engineer confirmed — never the Windows account.
            if (string.IsNullOrWhiteSpace(selection.ApprovedBy))
                throw new InvalidOperationException("לא נקבע מאשר להגדרת הפרויקט — לא נשמר דבר.");
            ValidateSelectionAgainstScan(profile, selection, scan);
            var reviewedPairs = ValidateSurfacePairs(selection, scan);

            // For a generated first-run seed this exact check must happen before
            // ApplySelection mutates the candidate.  Publication later rechecks only
            // the target CAS, so the engineer's intended setup is not compared to the
            // pre-decision seed while a stale/mutated seed is still refused.
            ProjectProfileWriter.RequireExpectedStateUnchanged(
                profile, expectedProfileState);

            var oldLayers = profile.Sections.Cl.LayerPatterns.ToList();
            var oldSourceFiles = profile.Sections.Cl.SourceFiles.ToList();
            var oldAlignments = profile.Sections.Alignments.AllowedNames.ToList();
            var oldSources = profile.Sections.Sources.SampledSourceRules
                .Select(CloneSourceRule).ToList();
            var oldSurfacePairs = profile.Sections.Sources.SurfacePairs.ToList();
            var oldTolerance = profile.Sections.Cl.IntersectionToleranceM;

            var approver = selection.ApprovedBy!;

            ApplySelection(profile, selection);
            if (selection.SurfacePairs != null)
            {
                var selectedNames = selection.AllowedAlignments.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selectedHandles = scan.Alignments.Where(a => selectedNames.Contains(a.Name))
                    .Select(a => a.Handle ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                profile.Sections.Sources.SurfacePairs.RemoveAll(pair =>
                    selectedNames.Contains(pair.AlignmentName ?? "") ||
                    (!string.IsNullOrWhiteSpace(pair.AlignmentHandle) && selectedHandles.Contains(pair.AlignmentHandle)));
                profile.Sections.Sources.SurfacePairs.AddRange(reviewedPairs);
            }

            var summary =
                $"project setup: cl_layers=[{string.Join(",", profile.Sections.Cl.LayerPatterns)}] " +
                $"alignments=[{string.Join(",", profile.Sections.Alignments.AllowedNames)}] " +
                $"sources=[{string.Join(",", profile.Sections.Sources.SampledSourceRules.Select(r => r.Name))}] " +
                $"explicit_surface_pairs=[{string.Join(",", reviewedPairs.Select(p => p.AlignmentName + ":" + p.ExistingName + "→" + p.DesignName))}]";

            var hashes = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(scan.Drawing) && !string.IsNullOrEmpty(scan.DrawingHash))
                hashes[Path.GetFileName(scan.Drawing)] = scan.DrawingHash!;
            foreach (var (path, hash) in scan.ExternalClHashes) hashes[path] = hash;
            foreach (var (path, hash) in scan.DiscoverySourceHashes) hashes[path] = hash;

            try
            {
                return ProjectProfileWriter.Save(
                    profile, targetPath, summary, approver,
                    expectedProfileState,
                    hashes);
            }
            catch
            {
                profile.Sections.Cl.LayerPatterns.Clear();
                profile.Sections.Cl.LayerPatterns.AddRange(oldLayers);
                profile.Sections.Cl.SourceFiles.Clear();
                profile.Sections.Cl.SourceFiles.AddRange(oldSourceFiles);
                profile.Sections.Alignments.AllowedNames.Clear();
                profile.Sections.Alignments.AllowedNames.AddRange(oldAlignments);
                profile.Sections.Sources.SampledSourceRules.Clear();
                profile.Sections.Sources.SampledSourceRules.AddRange(oldSources);
                profile.Sections.Sources.SurfacePairs.Clear();
                profile.Sections.Sources.SurfacePairs.AddRange(oldSurfacePairs);
                profile.Sections.Cl.IntersectionToleranceM = oldTolerance;
                throw;
            }
        }

        private static void ValidateSelectionAgainstScan(
            ProjectProfile profile,
            ProjectSetupSelection selection,
            ProjectSetupScan scan)
        {
            if (!scan.ScanComplete || scan.Findings.Any(finding =>
                    finding.Severity == FindingSeverity.Error))
                throw new InvalidOperationException(
                    "Project setup cannot be saved from an incomplete/failed discovery scan.");
            if (!string.Equals(scan.ProjectProfileId, profile.ProfileId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The project setup scan belongs to a different project profile.");
            if (selection.SampledSources.Count == 0)
                throw new InvalidOperationException(
                    "Project setup requires at least one engineer-selected section sampling source; partial setup was not saved.");
            if (selection.ClLayers.Count == 0)
                throw new InvalidOperationException(
                    "Project setup requires at least one engineer-selected CL layer; partial setup was not saved.");

            var layers = scan.ClLayerCandidates.Select(item => item.Layer)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownLayer = selection.ClLayers.FirstOrDefault(item =>
                string.IsNullOrWhiteSpace(item) || !layers.Contains(item.Trim()));
            if (unknownLayer != null)
                throw new InvalidOperationException(
                    $"CL layer '{unknownLayer}' is not present in the setup scan.");

            var alignments = scan.Alignments.Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownAlignment = selection.AllowedAlignments.FirstOrDefault(item =>
                string.IsNullOrWhiteSpace(item) || !alignments.Contains(item.Trim()));
            if (unknownAlignment != null)
                throw new InvalidOperationException(
                    $"Alignment '{unknownAlignment}' is not present in the setup scan.");

            foreach (var (name, kind) in selection.SampledSources)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind) ||
                    scan.Sources.Count(item =>
                        string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidOperationException(
                        $"Sampled source '{name}'/'{kind}' is not present exactly in the setup scan.");
            }
        }

        internal static List<ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair> ValidateSurfacePairs(
            ProjectSetupSelection selection, ProjectSetupScan scan)
        {
            var result = new List<ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair>();
            if (selection.SurfacePairs == null) return result;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in selection.SurfacePairs)
            {
                var alignment = scan.Alignments.Where(a => string.Equals(a.Name, choice.AlignmentName,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                if (alignment.Length != 1 || !selection.AllowedAlignments.Contains(alignment[0].Name,
                        StringComparer.OrdinalIgnoreCase) ||
                    !string.Equals(alignment[0].Handle, choice.AlignmentHandle, StringComparison.OrdinalIgnoreCase) ||
                    !seenNames.Add(alignment[0].Name) || !seenHandles.Add(alignment[0].Handle ?? ""))
                    throw new InvalidOperationException("Reviewed surface pair alignment is missing, unselected, duplicated or changed.");
                var approved = choice with
                {
                    DrawingFingerprint = scan.DrawingFingerprint,
                    ApprovedBy = string.IsNullOrWhiteSpace(selection.ApprovedBy)
                        ? throw new InvalidOperationException("לא נקבע מאשר לזוג המשטחים — לא נשמר דבר.")
                        : selection.ApprovedBy,
                    ApprovedAtUtc = DateTime.UtcNow,
                };
                var selected = SectionSourceSelectionLogic.SelectExplicitPair(scan.DrawingFingerprint,
                    alignment[0].Name, alignment[0].Handle,
                    scan.Sources.Where(s => s.Kind == "surface")
                        .Select(s => new SectionSourceSelectionLogic.Identity(s.Name, s.Handle ?? "")).ToArray(),
                    new[] { approved });
                if (!selected.IsValid)
                    throw new InvalidOperationException(selected.Error);
                foreach (var name in new[] { approved.ExistingName!, approved.DesignName! })
                    if (!selection.SampledSources.TryGetValue(name, out var kind) || kind != "surface")
                        throw new InvalidOperationException("Every reviewed surface must also be selected for sampling: " + name);
                result.Add(approved);
            }
            return result;
        }

        private static ProjectProfile.SectionsProfile.SourcesProfile.SourceRule CloneSourceRule(
            ProjectProfile.SectionsProfile.SourcesProfile.SourceRule rule) => new()
        {
            Name = rule.Name,
            Kind = rule.Kind,
            NamePattern = rule.NamePattern,
            LayerPattern = rule.LayerPattern,
            Required = rule.Required,
        };

        /// <summary>
        /// True when the profile cannot yet produce section records — the trigger for
        /// showing setup instead of an empty, misleading result.
        /// </summary>
        public static bool NeedsSetup(ProjectProfile profile) =>
            profile.Sections.Cl.LayerPatterns.Count == 0 ||
            profile.Sections.Sources.SampledSourceRules.Count == 0;
    }
}
