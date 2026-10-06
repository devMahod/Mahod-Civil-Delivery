using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// THE deterministic Estimate API (mirrors <see cref="SectionsWorkflowService"/>):
    /// direct command, smoke command and MahodAI tools all call this layer.
    /// Chain: scan → Neutral Quantity Records → preflight → build → Excel + trace.
    /// LandQ integration stays BLOCKED_DEPENDENCY until the contract is verified —
    /// this local build path is the plugin-side deterministic workflow, not a second
    /// organizational estimate backend.
    /// </summary>
    public sealed partial class EstimateWorkflowService
    {
        private readonly CivilQuantityExtractionService _extractor = new();

        /// <summary>Receipts of original XREF references read from local read copies (logical path, copy, decision, cleanup).</summary>
        internal const string XrefOriginalReadsArtifact = "xref_original_reference_reads.json";

        // These are current heads, not archived external Excel packages. Any new
        // decision/proposal or withdrawal invalidates both priced export variants.
        // Keep one policy so a partial export cannot silently survive a rebase.
        internal static IReadOnlyCollection<string> DerivedEstimateArtifacts { get; } =
            Array.AsReadOnly(new[]
            {
                "estimate_result.json", "export_result.json", "mapping_proposals.json",
                "partial_priced_export_result.json",
            });

        /// <summary>Stage trail shared with the Sections workflow for hang diagnosis.</summary>
        public StageLog? Log { get; set; }

        public sealed class ScanResult
        {
            // Diagnostic payload is never embedded in the scan/estimate/line JSON.
            // It is written once alongside the published extraction evidence.
            [System.Text.Json.Serialization.JsonIgnore]
            internal HatchAreaFailureDiagnostic.Batch? HatchDiagnostics { get; set; }
            [System.Text.Json.Serialization.JsonIgnore]
            internal XrefBlockExtentsFailureDiagnostic.Batch? XrefBlockDiagnostics { get; set; }
            [System.Text.Json.Serialization.JsonIgnore]
            internal IReadOnlyList<CivilQuantityExtractionService.XrefOverlayRecoveryEvidence>? RecoveredOverlayReferences { get; set; }
            [System.Text.Json.Serialization.JsonIgnore]
            internal XrefReadSnapshots.Ledger? XrefReadCopies { get; set; }
            [System.Text.Json.Serialization.JsonIgnore]
            internal IReadOnlyList<CivilQuantityExtractionService.XrefOriginalReadReceipt>? XrefOriginalReads { get; set; }
            [System.Text.Json.Serialization.JsonIgnore]
            internal EvidenceCoverage? EvidenceCoverage { get; set; }
            public required string RunId { get; init; }
            /// <summary>Completion of the original measurement read, preserved through later classification and export.</summary>
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public DateTime? ScannedAtUtc { get; init; }
            public required string ProjectProfileId { get; init; }
            public required string ProfileSource { get; init; }
            public required string SourceDrawing { get; init; }
            public string? ProjectProfileHash { get; init; }
            public string? ProjectProfileEffectiveHash { get; init; }
            public ProjectProfileWriter.ExpectedProfileState? ProfileWriteState { get; set; }
            public string? DatabaseRevision { get; init; }
            public string? SourceDrawingHash { get; init; }
            public int? SourceDbMod { get; init; }
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public PhysicalDrawingUnitPolicy.Resolution? PhysicalUnits { get; set; }
            /// <summary>b24 (Codex 12:45): the unit contract this scan was measured under ("scan-physical-units/1"),
            /// persisted with the run so the BoQ identifies the scan positively; a scan written before it has none.</summary>
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? PhysicalUnitsContract { get; set; }
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? PhysicalUnitConfigurationHash { get; set; }
            /// <summary>The source-selection snapshot used to measure this scan. Null is legacy all-source scope.</summary>
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? SourceSelectionConfigurationHash { get; set; }
            public List<EstimateExternalSource> ExternalSources { get; init; } = new();
            public List<NeutralQuantityRecord> Records { get; init; } = new();
            /// <summary>Measured station areas, not plan areas or priced BOQ rows.</summary>
            public List<MaterialSectionAreaObservation> MaterialAreas { get; init; } = new();
            public List<DeliveryFinding> Findings { get; init; } = new();
            public int ScannedEntities { get; init; }
            public DeliveryStatus Status { get; set; }

            /// <summary>True when the profile had no rules and everything is UNMAPPED by design.</summary>
            public bool DiscoveryMode { get; init; }

            /// <summary>Discovered work grouped by rule key — the unit of mapping approval.</summary>
            public IEnumerable<IGrouping<string, NeutralQuantityRecord>> ByRuleKey() =>
                Records.GroupBy(r => r.Classification.RuleKey ?? "(none)");

            public ResultScope Scope => ResultScope.For(SourceDrawing, ProjectProfileHash);

            /// <summary>Rejects reuse after a drawing switch or profile edit/change.</summary>
            public string? StaleReason(
                string? activeDrawing, string? activeProfileId, string? activeProfileHash,
                string? activeDatabaseRevision = null)
            {
                if (!string.Equals(ProjectProfileId, activeProfileId, StringComparison.Ordinal))
                    return "התוצאה שייכת לפרופיל פרויקט אחר — יש להריץ סריקת כמויות מחדש";
                if (string.IsNullOrWhiteSpace(ProjectProfileHash) ||
                    string.IsNullOrWhiteSpace(activeProfileHash))
                    return "לא ניתן לאמת את גרסת פרופיל הפרויקט — יש להריץ סריקת כמויות מחדש";
                var scopeReason = Scope.StaleReason(activeDrawing, activeProfileHash);
                if (scopeReason != null) return scopeReason;
                if (activeDatabaseRevision != null &&
                    (string.IsNullOrWhiteSpace(DatabaseRevision) ||
                     !string.Equals(DatabaseRevision, activeDatabaseRevision, StringComparison.Ordinal)))
                    return "השרטוט השתנה מאז סריקת הכמויות — יש להריץ סריקה מחדש לפני בנייה או ייצוא";
                return null;
            }
        }

        public ScanResult Scan(
            Document doc, ProjectProfile profile, string? profileHash,
            string profileSelector,
            string profileWriteTarget,
            ProjectProfileWriter.ExpectedProfileState profileWriteState)
        {
            ArgumentNullException.ThrowIfNull(profileWriteState);
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.EstimateAction);
            var expectedState = profileWriteState;
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);
            var db = doc.Database;
            using var diagnostic = EstimateScanTrace.Start("shared-scan");
            EstimateScanTrace.Mark("scan.entry");
            Log?.Begin("estimate.start_transaction");
            ScanResult result;
            // Publication deliberately lives after this scope. Abort() returning is
            // not the end of a native transaction: Dispose can still fail, and Civil
            // getters may perform lazy database work during close. A scan is not
            // authoritative evidence until both Abort and Dispose have succeeded.
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Log?.End("estimate.start_transaction");
                result = ScanUnpublished(
                    doc, tr, CivilApplication.ActiveDocument, profile, profileHash,
                    profileSelector, profileWriteTarget, expectedState);
                EstimateScanTrace.Step("transaction.abort", tr.Abort);
                ScanDatabaseChangeProbe.SampleDbmod(doc, "transaction.abort");
                EstimateScanTrace.Mark("transaction.dispose.begin");
            }
            EstimateScanTrace.Mark("transaction.dispose.end");
            ScanDatabaseChangeProbe.SampleDbmod(doc, "transaction.dispose");
            EstimateScanTrace.Step("scan.publish", () => PublishScanEvidence(result));
            ScanDatabaseChangeProbe.SampleDbmod(doc, "scan.publish");
            EstimateScanTrace.Mark("scan.complete", result.Records.Count);
            CivilDeliveryUsage.Step(CivilDeliveryUsage.EstimateAction, ok: true);
            return result;
        }

        /// <summary>
        /// Complete scan route for callers that already own a transaction (the AI
        /// tool host). It is deliberately the same implementation as the direct
        /// command: drawing extraction + Civil model quantities + all preflight gates.
        /// </summary>
        internal ScanResult ScanUnpublished(
            Document doc, Transaction tr, CivilDocument civilDoc,
            ProjectProfile profile, string? profileHash, string profileSelector,
            string profileWriteTarget,
            ProjectProfileWriter.ExpectedProfileState profileWriteState)
        {
            ArgumentNullException.ThrowIfNull(profileWriteState);
            var expectedState = profileWriteState;
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);
            if (string.IsNullOrWhiteSpace(profileWriteTarget) ||
                !string.Equals(Path.GetFullPath(profileWriteTarget),
                    Path.GetFullPath(expectedState.TargetPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Quantity scan profile target differs from the target captured when the profile loaded.");
            using var diagnostic = EstimateScanTrace.Start("shared-unpublished");
            EstimateScanTrace.Mark("scan_unpublished.entry");
            var runId = RunManifest.NewRunId("estimate", "extract");
            var db = doc.Database;
            // CaptureLive subscribes the mutation tracker before reading DBMOD or
            // hashing and, crucially, before any entity/Civil measurement begins.
            var startSource = EstimateScanTrace.Step("source.start", () => DrawingRevisionTracker.CaptureLive(doc));
            var sourceFailure = startSource.Failure ?? EstimateSourceSnapshotPolicy.InitialFailure(
                startSource.DrawingHash, startSource.DbMod);
            if (sourceFailure != null)
                throw new InvalidOperationException(
                    "סריקת האומדן נעצרה: לא ניתן לקשור כמויות לגרסת DWG שמורה — " + sourceFailure);
            if (!CatalogIdentity.IsValidSha256(profileHash))
                throw new InvalidOperationException(
                    "סריקת האומדן נעצרה: לא ניתן לאמת את קובץ פרופיל הפרויקט (SHA-256 חסר)");
            var effectiveProfileHash = EstimateTraceIdentity.EffectiveProfileHash(profile);

            var extraction = EstimateScanTrace.Step("extraction", () => _extractor.Extract(db, tr, profile, runId,
                startSource.DrawingPath, startSource.DrawingHash, Log, startSource));
            var modelRecords = new List<NeutralQuantityRecord>();
            var modelFindings = new List<DeliveryFinding>();
            var materialAreas = new List<MaterialSectionAreaObservation>();

            // The model knows quantities the drawing never drew: corridor material
            // volumes (shape areas per station) and cut/fill from the sampled
            // sections. They join the scan as synthetic layers (corridor:Base,
            // earthworks:cut) and flow through mapping and pricing like any layer
            // (engineer, 31/08: "אפשר להוציא עוד דברים מהסיביל... ולשייך").
            try
            {
                if (profile.Estimate.SourceSelection?.Sources.Single(s => s.Key == EstimateSourceSelectionPolicy.HostKey).Included != false)
                {
                var corridorQto = EstimateScanTrace.Step("corridor", () => CorridorQuantityService.Collect(tr, db, civilDoc, profile, runId,
                    startSource.DrawingPath, startSource.DrawingHash, Log));
                modelRecords.AddRange(corridorQto.Records);
                modelFindings.AddRange(corridorQto.Findings);
                materialAreas.AddRange(corridorQto.MaterialAreas);
                }
            }
            catch (System.Exception ex)
            {
                Log?.Info($"corridor_qto skipped: {ex.Message}");
                modelFindings.Add(new DeliveryFinding
                {
                    Code = EstimatePreflightPolicy.CorridorQtoFailedCode,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "קריאת כמויות המודל נכשלה — האומדן אינו שלם וחסום לייצוא",
                    Message = ex.Message,
                    RecommendedAction = "יש לבדוק את מודל ה-Civil, לתקן את כשל קריאת הקורידור/חתכים ולהריץ סריקה מחדש.",
                    ProjectProfileId = profile.ProfileId,
                });
            }

            var endSource = EstimateScanTrace.Step("source.end", () => DrawingRevisionTracker.CaptureLive(doc));
            ScanDatabaseChangeProbe.SampleDbmod(doc, "source.end");
            var endFailure = endSource.Failure ?? EstimateSourceSnapshotPolicy.FreshnessFailure(
                startSource.DrawingHash, startSource.DatabaseRevision,
                endSource.DrawingHash, endSource.DatabaseRevision, endSource.DbMod);
            if (endFailure != null)
                throw new InvalidOperationException(
                    "סריקת האומדן בוטלה: השרטוט השתנה או אינו שמור באופן מלא — " + endFailure);
            var externalFailure = EstimateScanTrace.Step("xref.freshness", () => ExternalSourcesFreshnessReason(extraction.ExternalSources));
            if (externalFailure != null)
                throw new InvalidOperationException(
                    "סריקת האומדן בוטלה: מקור XREF השתנה או אינו ניתן לאימות — " + externalFailure);

            // Close the race across the potentially long Civil/XREF traversal. Never
            // adopt a runtime profile that appeared (or changed) during measurement.
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);

            EstimateScanTrace.Mark("scan.assemble.begin", extraction.Records.Count);
            var result = AssembleScan(
                runId, profile.ProfileId, startSource.DrawingPath, profileHash,
                extraction.Records, extraction.Findings, extraction.ScannedEntities,
                extraction.DiscoveryMode, modelRecords, modelFindings,
                ResolveProfileSource(profileSelector ?? profile.ProfileId),
                IgnoredRulePolicy.ApprovedKeys(profile), endSource.DatabaseRevision,
                startSource.DrawingHash, endSource.DbMod, effectiveProfileHash,
                extraction.ExternalSources, materialAreas, scannedAtUtc: DateTime.UtcNow);
            EstimateScanTrace.Mark("scan.assemble.end", result.Records.Count);

            result.ProfileWriteState = expectedState;
            result.PhysicalUnits = extraction.PhysicalUnits;
            result.PhysicalUnitsContract = ScanUnitEvidence.Contract;
            result.PhysicalUnitConfigurationHash = UnitConfigurationHash(profile);
            result.SourceSelectionConfigurationHash = SourceSelectionHash(profile);
            result.HatchDiagnostics = extraction.HatchDiagnostics;
            result.XrefBlockDiagnostics = extraction.XrefBlockDiagnostics;
            result.RecoveredOverlayReferences = extraction.RecoveredOverlayReferences;
            result.XrefReadCopies = extraction.XrefReadCopies;
            result.XrefOriginalReads = extraction.XrefOriginalReads;
            result.EvidenceCoverage = extraction.EvidenceCoverage;

            return result;
        }

        /// <summary>
        /// Publishes a completed scan only after its caller has proved the read
        /// transaction aborted and disposed successfully. AI callers invoke this from
        /// IReadOnlyTransactionClosedObserver; the direct/UI route invokes it after its
        /// own explicit transaction scope.
        /// </summary>
        internal static void PublishScanEvidence(ScanResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            var manifestFindings = ManifestFindings(result);
            SectionsWorkflowService.PersistEvidenceBundle(
                result.RunId,
                "estimate_scan.json",
                result,
                (pendingRoot, publishedRoot) => WriteEstimateManifest(
                    result, "extract", result.Status,
                    ScanManifestCounts(result, manifestFindings), manifestFindings,
                    pendingRoot, publishedRoot),
                stageAdditionalArtifacts: pendingRoot =>
                {
                    SectionsWorkflowService.WriteArtifact(
                        result.RunId, "neutral_quantity_records.json", result.Records, pendingRoot);
                    SectionsWorkflowService.WriteArtifact(
                        result.RunId, "quantity_preflight.json", result.Findings, pendingRoot);
                    if (result.HatchDiagnostics is { Sources.Count: > 0 } diagnostics)
                        SectionsWorkflowService.WriteArtifact(result.RunId,
                            "hatch_area_failure_diagnostics.json", diagnostics, pendingRoot);
                    if (result.XrefBlockDiagnostics is { Sources.Count: > 0 } blockDiagnostics)
                        SectionsWorkflowService.WriteArtifact(result.RunId,
                            XrefBlockExtentsFailureDiagnostic.ArtifactName, blockDiagnostics, pendingRoot);
                    if (result.RecoveredOverlayReferences is { Count: > 0 } recoveredOverlays)
                        SectionsWorkflowService.WriteArtifact(result.RunId,
                            "xref_overlay_recovery_evidence.json", recoveredOverlays, pendingRoot);
                    // Which original references were read from which local copy, with what decision, and whether
                    // each copy was deleted ("orphan: …" is reported, never called deleted).
                    if (result.XrefReadCopies is { Copies.Count: > 0 } || result.XrefOriginalReads is { Count: > 0 })
                        SectionsWorkflowService.WriteArtifact(result.RunId, XrefOriginalReadsArtifact,
                            new
                            {
                                Copies = result.XrefReadCopies?.Copies,
                                RunFolder = result.XrefReadCopies?.RunFolder,
                                RunFolderCleanup = result.XrefReadCopies?.RunFolderCleanup,
                                LeftoverRunFolders = result.XrefReadCopies?.LeftoverRunFolders,
                                Reads = result.XrefOriginalReads,
                            }, pendingRoot);
                    // One bounded recognition-evidence summary per scan (no per-record findings).
                    if (result.EvidenceCoverage is { } evidenceCoverage)
                        SectionsWorkflowService.WriteArtifact(result.RunId,
                            "evidence_coverage.json", evidenceCoverage, pendingRoot);
                });
        }

        /// <summary>
        /// Withdraws any BUILD/EXPORT/proposal head created by a failed composite AI
        /// operation and atomically restores the already-published scan as the only
        /// actionable evidence. This never manufactures a new measurement.
        /// </summary>
        internal static void WithdrawDerivedEstimateEvidence(ScanResult scan)
        {
            ArgumentNullException.ThrowIfNull(scan);
            RequirePublishedScanEvidence(scan);
            var findings = ManifestFindings(scan);
            SectionsWorkflowService.PersistEvidenceBundle(
                scan.RunId,
                "estimate_scan.json",
                scan,
                (pendingRoot, publishedRoot) => WriteEstimateManifest(
                    scan, "extract", scan.Status,
                    ScanManifestCounts(scan, findings), findings,
                    pendingRoot, publishedRoot),
                replaceExisting: true,
                removeArtifacts: DerivedEstimateArtifacts,
                replaceArtifacts: new[] { "neutral_quantity_records.json", "quantity_preflight.json" },
                stageAdditionalArtifacts: pendingRoot =>
                {
                    SectionsWorkflowService.WriteArtifact(
                        scan.RunId, "neutral_quantity_records.json", scan.Records, pendingRoot);
                    SectionsWorkflowService.WriteArtifact(
                        scan.RunId, "quantity_preflight.json", scan.Findings, pendingRoot);
                });
        }

        internal static ScanResult AssembleScan(
            string runId,
            string projectProfileId,
            string sourceDrawing,
            string? profileHash,
            IEnumerable<NeutralQuantityRecord> drawingRecords,
            IEnumerable<DeliveryFinding> drawingFindings,
            int scannedEntities,
            bool discoveryMode,
            IEnumerable<NeutralQuantityRecord>? modelRecords = null,
            IEnumerable<DeliveryFinding>? modelFindings = null,
            string? profileSource = null,
            IEnumerable<string>? ignoredRuleKeys = null,
            string? databaseRevision = null,
            string? sourceDrawingHash = null,
            int? sourceDbMod = null,
            string? projectProfileEffectiveHash = null,
            IEnumerable<EstimateExternalSource>? externalSources = null,
            IEnumerable<MaterialSectionAreaObservation>? materialAreas = null,
            DateTime? scannedAtUtc = null)
        {
            var records = drawingRecords
                .Concat(modelRecords ?? Enumerable.Empty<NeutralQuantityRecord>())
                .ToList();
            var findings = drawingFindings
                .Concat(modelFindings ?? Enumerable.Empty<DeliveryFinding>())
                .ToList();
            findings.AddRange(DuplicateRiskDetector.Detect(records));
            findings.AddRange(QuantitySignificance.DetectReviewFindings(records, ignoredRuleKeys));

            var recordStatus = records.Count == 0
                ? DeliveryStatus.ReviewRequired
                : DeliveryStatusRules.Aggregate(records.Select(r => r.Status).ToList());
            return new ScanResult
            {
                RunId = runId,
                ScannedAtUtc = scannedAtUtc,
                ProjectProfileId = projectProfileId,
                ProfileSource = profileSource ?? projectProfileId,
                SourceDrawing = sourceDrawing,
                ProjectProfileHash = profileHash,
                ProjectProfileEffectiveHash = projectProfileEffectiveHash,
                DatabaseRevision = databaseRevision,
                SourceDrawingHash = sourceDrawingHash,
                SourceDbMod = sourceDbMod,
                ExternalSources = (externalSources ?? Enumerable.Empty<EstimateExternalSource>()).ToList(),
                MaterialAreas = (materialAreas ?? Enumerable.Empty<MaterialSectionAreaObservation>()).ToList(),
                Records = records,
                Findings = findings,
                ScannedEntities = scannedEntities,
                DiscoveryMode = discoveryMode,
                Status = DeliveryStatusRules.CapByFindings(recordStatus, findings),
            };
        }

        internal static string DrawingIdentity(Document doc) =>
            !string.IsNullOrWhiteSpace(doc.Database.Filename)
                ? doc.Database.Filename
                : doc.Name;

        internal static string? FreshnessReason(Document doc, ScanResult scan)
        {
            var source = DrawingRevisionTracker.CaptureLive(doc);
            var sourceFailure = source.Failure ?? EstimateSourceSnapshotPolicy.FreshnessFailure(
                scan.SourceDrawingHash, scan.DatabaseRevision,
                source.DrawingHash, source.DatabaseRevision, source.DbMod,
                scannedDbMod: scan.SourceDbMod);
            if (sourceFailure != null)
                return "מקור השרטוט אינו תואם לסריקה: " + sourceFailure;
            var externalFailure = ExternalSourcesFreshnessReason(scan.ExternalSources);
            if (externalFailure != null)
                return "מקור XREF אינו תואם לסריקה: " + externalFailure;

            var expectedState = scan.ProfileWriteState;
            if (expectedState == null)
                return "לסריקה אין ראיית מקור/יעד פרופיל שנלכדה בתחילת העבודה";
            try
            {
                ProjectProfileWriter.RequireExpectedFilesUnchanged(expectedState);
            }
            catch (Exception ex)
            {
                return "פרופיל הפרויקט או יעד הכתיבה השתנו מאז תחילת העבודה: " + ex.Message;
            }
            if (!expectedState.SourceExisted)
                return "פרופיל ההתחלה שנוצר לשרטוט טרם נשמר ואושר — יש להשלים הגדרת פרויקט";

            var selector = ActiveProjectProfileService.AuthoritativeReloadSelector(
                scan.ProjectProfileId, expectedState.SourcePath,
                expectedState.TargetPath, File.Exists);
            var loaded = ProjectProfileLoader.LoadFromFile(selector);
            if (!loaded.IsUsable || loaded.Profile == null ||
                !CatalogIdentity.IsValidSha256(loaded.ProfileHash))
                return "לא ניתן לקרוא ולאמת מחדש את פרופיל הפרויקט ששימש לסריקה";
            var effective = EstimateTraceIdentity.EffectiveProfileHash(loaded.Profile);
            if (!string.Equals(scan.ProjectProfileEffectiveHash, effective,
                    StringComparison.OrdinalIgnoreCase))
                return "תוכן פרופיל הפרויקט השתנה מאז הסריקה — יש להריץ סריקה מחדש";

            return scan.StaleReason(
                source.DrawingPath, loaded.Profile.ProfileId, loaded.ProfileHash,
                source.DatabaseRevision);
        }

        private static void RequireFresh(Document doc, ScanResult scan, string operation)
        {
            var reason = FreshnessReason(doc, scan);
            if (reason != null)
                throw new InvalidOperationException($"{operation} נעצר: {reason}");
        }

        /// <summary>
        /// A mapping or relevance approval is an engineering decision made from the
        /// quantities currently shown in the palette.  It therefore has the same
        /// source-freshness boundary as BUILD/EXPORT: a changed host database, saved
        /// DWG, XREF or profile must be refused before the decision is persisted.
        /// </summary>
        internal static void RequireFreshForDecision(
            Document doc, ScanResult scan, string operation)
        {
            RequireFresh(doc, scan, operation);
            // Fresh paths/revisions are necessary but not sufficient. A durable
            // engineering decision may only be based on the exact scan object whose
            // bytes are present in the published run manifest.
            RequirePublishedScanEvidence(scan);
        }

        /// <summary>
        /// Rebases immutable measurement evidence after a decision-only profile edit.
        /// Mapping and relevance approvals do not measure the drawing again; they only
        /// classify (or explicitly exclude) already measured rule groups.  Keeping that
        /// distinction lets an engineer review many groups in one pass without running
        /// a full Civil/XREF scan after every row, while BUILD still verifies the new
        /// durable profile hash and the original host/XREF source revisions.
        /// </summary>
        // b24: a recorded unit review is part of the unit configuration (a new record needs a new scan); a profile without
        // reviews keeps exactly its earlier hash.
        internal static string UnitConfigurationHash(ProjectProfile profile) =>
            ArtifactHash.Sha256OfText(profile.DrawingUnitReviews == null
                ? JsonSerializer.Serialize(profile.DrawingUnitDeclarations)
                : JsonSerializer.Serialize(new { profile.DrawingUnitDeclarations, profile.DrawingUnitReviews }));

        internal static string? SourceSelectionHash(ProjectProfile profile) =>
            profile.Estimate.SourceSelection == null ? null :
                ArtifactHash.Sha256OfText(JsonSerializer.Serialize(profile.Estimate.SourceSelection));

        internal static void RequireUnchangedSourceSelection(ScanResult scan, ProjectProfile profile)
        {
            if (!string.Equals(scan.SourceSelectionConfigurationHash, SourceSelectionHash(profile), StringComparison.Ordinal))
                throw new InvalidOperationException("בחירת המקורות השתנתה מאז המדידה — נדרשת סריקה חדשה; אין לשייך מדידות ישנות להיקף מקורות אחר.");
        }

        /// <summary>
        /// b24 (Codex 13:21): every product entry that builds from scan records (engineer draft, recognition, library gate)
        /// first checks the scan's unit evidence and every CAD record against it — the same validator as the BoQ. A scan
        /// without the unit contract, or with evidence that does not hold, is refused with a rescan, never measured.
        /// </summary>
        internal static void RequireTrustedUnitEvidence(ScanResult scan, string operation)
        {
            ArgumentNullException.ThrowIfNull(scan);
            if (MahodAI.CivilDelivery.Estimate.BoqRulesV2.BoqNeutralRecordAdapter.ScanUnitRefusal(
                    ScanUnitEvidence.OfScan(scan.PhysicalUnitsContract, scan.PhysicalUnits), scan.Records) is { } problem)
                throw new InvalidOperationException($"{operation} נחסם: {problem}.");
        }

        internal static void RequireUnchangedPhysicalUnits(ScanResult scan, ProjectProfile profile)
        {
            if (profile.DrawingUnitDeclarations == null ||
                (scan.PhysicalUnitConfigurationHash == null
                    // b24 (Codex 13:02/13:11): a scan without the hash tolerates only a profile without any unit content
                    ? profile.DrawingUnitDeclarations.Count != 0 || profile.DrawingUnitReviews != null
                    : !string.Equals(scan.PhysicalUnitConfigurationHash, UnitConfigurationHash(profile), StringComparison.Ordinal)))
                throw new InvalidOperationException("הצהרת היחידות השתנתה מאז המדידה — נדרשת סריקה חדשה; אין לשייך מחדש כמויות ישנות ליחידה אחרת.");
        }

        internal static ScanResult RebaseAfterProfileDecision(
            ScanResult scan,
            ProjectProfile profile,
            ProjectProfileWriter.SaveResult saved,
            string? mappedRuleKey = null) =>
            RebaseAfterProfileDecisionSet(scan, profile, saved,
                string.IsNullOrWhiteSpace(mappedRuleKey)
                    ? Array.Empty<string>() : new[] { mappedRuleKey });

        private static ScanResult RebaseAfterProfileDecisionSet(
            ScanResult scan,
            ProjectProfile profile,
            ProjectProfileWriter.SaveResult saved,
            IReadOnlyList<string> mappedRuleKeys)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(saved);
            RequireUnchangedPhysicalUnits(scan, profile);

            RequireUnchangedSourceSelection(scan, profile);

            if (!string.Equals(scan.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Cannot rebase estimate evidence onto a different project profile.");
            if (!CatalogIdentity.IsValidSha256(saved.NewHash))
                throw new InvalidOperationException(
                    "Cannot rebase estimate evidence without the durable profile SHA-256.");
            if (string.IsNullOrWhiteSpace(saved.Path) || !File.Exists(saved.Path))
                throw new InvalidOperationException(
                    "Cannot rebase estimate evidence because the saved profile file is missing.");

            // Validate all selected rules first, then reclassify measured rows once.
            // Re-running this entire method per key made a human review of 129 groups
            // rescan/recompute findings over 74,891 rows 129 times.
            var approvedByKey = new Dictionary<string,
                ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var mappedRuleKey in mappedRuleKeys)
            {
                var matchingRules = profile.Estimate.QuantitySources.Rules
                    .Where(rule => string.Equals(rule.RuleKey, mappedRuleKey,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(rule => CivilQuantityExtractionService.IsExplicitlyApproved(profile, rule))
                    .ToList();
                if (matchingRules.Count != 1)
                    throw new InvalidOperationException(
                        $"The approved mapping '{mappedRuleKey}' is not one unique current rule.");

                approvedByKey[mappedRuleKey] = matchingRules[0];
            }
            var records = scan.Records.Select(record =>
                record.Classification.RuleKey != null &&
                approvedByKey.TryGetValue(record.Classification.RuleKey, out var approved)
                    ? WithApprovedClassification(record, approved)
                    : record).ToList();

            // These two findings are derived from mapping/relevance state.  Recompute
            // them against the durable decision; every geometry/source finding remains
            // byte-for-byte the evidence captured by the original scan.
            var findings = QuantitySignificance.RecomputeDecisionFindings(
                records, scan.Findings, IgnoredRulePolicy.ApprovedKeys(profile)).ToList();

            var recordStatus = records.Count == 0
                ? DeliveryStatus.ReviewRequired
                : DeliveryStatusRules.Aggregate(records.Select(record => record.Status).ToList());

            return new ScanResult
            {
                RunId = scan.RunId,
                ScannedAtUtc = scan.ScannedAtUtc,
                ProjectProfileId = scan.ProjectProfileId,
                ProfileSource = Path.GetFullPath(saved.Path),
                SourceDrawing = scan.SourceDrawing,
                ProjectProfileHash = saved.NewHash,
                ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
                ProfileWriteState = ProjectProfileWriter.CaptureExpectedState(
                    saved.Path, saved.NewHash, saved.Path),
                DatabaseRevision = scan.DatabaseRevision,
                SourceDrawingHash = scan.SourceDrawingHash,
                SourceDbMod = scan.SourceDbMod,
                PhysicalUnits = scan.PhysicalUnits,
                PhysicalUnitsContract = scan.PhysicalUnitsContract,
                PhysicalUnitConfigurationHash = scan.PhysicalUnitConfigurationHash,
                SourceSelectionConfigurationHash = scan.SourceSelectionConfigurationHash,
                ExternalSources = scan.ExternalSources.ToList(),
                MaterialAreas = scan.MaterialAreas.ToList(),
                Records = records,
                Findings = findings,
                ScannedEntities = scan.ScannedEntities,
                DiscoveryMode = scan.DiscoveryMode,
                Status = DeliveryStatusRules.CapByFindings(recordStatus, findings),
            };
        }

        internal static ScanResult RebaseAfterProfileDecisions(
            ScanResult scan,
            ProjectProfile profile,
            ProjectProfileWriter.SaveResult saved,
            IEnumerable<string>? mappedRuleKeys)
        {
            var keys = (mappedRuleKeys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return RebaseAfterProfileDecisionSet(scan, profile, saved, keys);
        }

        internal static void PublishRebasedScanEvidence(
            ScanResult previousScan, ScanResult scan, int profileVersion)
        {
            RequirePublishedScanEvidence(previousScan);
            if (!string.Equals(previousScan.RunId, scan.RunId, StringComparison.Ordinal) ||
                !string.Equals(previousScan.SourceDrawingHash, scan.SourceDrawingHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previousScan.DatabaseRevision, scan.DatabaseRevision,
                    StringComparison.Ordinal) ||
                previousScan.ScannedAtUtc != scan.ScannedAtUtc ||
                !string.Equals(previousScan.PhysicalUnitConfigurationHash, scan.PhysicalUnitConfigurationHash,
                    StringComparison.Ordinal) ||
                !string.Equals(previousScan.SourceSelectionConfigurationHash, scan.SourceSelectionConfigurationHash,
                    StringComparison.Ordinal) ||
                !Equals(previousScan.PhysicalUnits, scan.PhysicalUnits) ||
                !ExternalSourcesEqual(previousScan.ExternalSources, scan.ExternalSources))
                throw new InvalidOperationException(
                    "Rebased estimate evidence changed its measured source lineage.");
            var findings = ManifestFindings(scan);
            SectionsWorkflowService.PersistEvidenceBundle(
                scan.RunId,
                "estimate_scan.json",
                scan,
                (pendingRoot, publishedRoot) => WriteEstimateManifest(
                    scan, "rebase", scan.Status,
                    ScanManifestCounts(scan, findings), findings,
                    pendingRoot, publishedRoot),
                replaceExisting: true,
                removeArtifacts: DerivedEstimateArtifacts,
                replaceArtifacts: new[] { "neutral_quantity_records.json", "quantity_preflight.json" },
                stageAdditionalArtifacts: pendingRoot =>
                {
                    SectionsWorkflowService.WriteArtifact(
                        scan.RunId, "neutral_quantity_records.json", scan.Records, pendingRoot);
                    SectionsWorkflowService.WriteArtifact(
                        scan.RunId, "quantity_preflight.json", scan.Findings, pendingRoot);
                    // One lossless BEFORE snapshot contains the full prior records,
                    // decisions, sources and findings. The new canonical scan is the
                    // AFTER state; a second full per-version record copy is redundant.
                    // Keep any pre-existing plain history untouched for compatibility.
                    SectionsWorkflowService.WriteCompressedArtifact(
                        scan.RunId,
                        $"estimate_scan.before-profile-{profileVersion}.json.gz",
                        previousScan,
                        pendingRoot);
                });
        }

        /// <summary>
        /// Completes one durable profile decision only if its rebased scan evidence is
        /// published. Rebase/publication failure withdraws the exact just-written
        /// profile version before control returns to UI, direct command or AI route.
        /// </summary>
        internal static ScanResult PublishProfileDecisionOrRestore(
            ScanResult previousScan,
            ProjectProfile profile,
            ProjectProfileWriter.SaveResult saved,
            IEnumerable<string>? mappedRuleKeys = null)
        {
            try
            {
                var rebased = RebaseAfterProfileDecisions(
                    previousScan, profile, saved, mappedRuleKeys);
                PublishRebasedScanEvidence(previousScan, rebased, saved.NewVersion);
                return rebased;
            }
            catch (Exception evidenceError)
            {
                try
                {
                    RestoreProfileAfterFailedDecisionEvidence(saved);
                }
                catch (Exception restoreError)
                {
                    throw new AggregateException(
                        "The decision evidence failed and the saved profile could not be restored; all estimate state must remain blocked.",
                        evidenceError, restoreError);
                }

                throw new InvalidOperationException(
                    "Decision evidence could not be published, so the saved profile decision was withdrawn.",
                    evidenceError);
            }
        }

        internal static void RestoreProfileAfterFailedDecisionEvidence(
            ProjectProfileWriter.SaveResult saved)
            => ProjectProfileWriter.RestoreAfterFailedPublication(saved);

        private static NeutralQuantityRecord WithApprovedClassification(
            NeutralQuantityRecord record,
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule approved)
        {
            var tags = record.Classification.Tags
                .Where(tag => !string.Equals(tag, "discovered", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(tag, "rule-classified", StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { "rule-classified" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // The original discovery row legitimately carried EST-UNMAPPED and a
            // ReviewRequired status.  Once this exact rule is durably approved those
            // fields must move with the classification too; otherwise the additive
            // rebase artifact would contradict itself (approved code + "unmapped").
            // Preserve every unrelated measurement/source finding fail-closed.
            var findings = record.Findings
                .Where(finding => finding.Code != EstimateFindingCodes.Unmapped)
                .ToList();
            var status = DeliveryStatusRules.CapByFindings(DeliveryStatus.Ready, findings);
            return new NeutralQuantityRecord
            {
                SchemaVersion = record.SchemaVersion,
                RecordId = record.RecordId,
                ProjectProfileId = record.ProjectProfileId,
                RunId = record.RunId,
                Source = record.Source,
                Measurement = record.Measurement,
                Classification = new QuantityClassification
                {
                    SourceClass = record.Classification.SourceClass,
                    RuleKey = approved.RuleKey,
                    CandidateCatalogCode = approved.CandidateCatalogCode,
                    ApprovedCatalogId = approved.ApprovedCatalogId,
                    ApprovedCatalogHash = approved.ApprovedCatalogHash,
                    ApprovedCatalogItemFingerprint = approved.ApprovedCatalogItemFingerprint,
                    MappingApprovedBy = approved.ApprovedBy,
                    MappingApprovedAtUtc = approved.ApprovedAtUtc,
                    Tags = tags,
                },
                Status = status,
                Findings = findings,
                Provenance = record.Provenance,
            };
        }

        internal static string? ExternalSourcesFreshnessReason(
            IReadOnlyList<EstimateExternalSource> externalSources)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in externalSources.Select(source => source.DrawingPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                hashes[path] = ClInstructionReader.HashFileShared(path);
            return XrefQuantityPolicy.FreshnessFailure(externalSources, hashes);
        }

        internal static bool ExternalSourcesEqual(
            IReadOnlyList<EstimateExternalSource> left,
            IReadOnlyList<EstimateExternalSource> right)
        {
            static string Key(EstimateExternalSource source) => string.Join("|",
                Path.GetFullPath(source.DrawingPath).ToUpperInvariant(),
                source.DrawingHash.ToUpperInvariant(), source.XrefChain,
                source.ReferenceHandlePath);
            return left.Select(Key).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(right.Select(Key).OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }

        internal static string ResolveProfileSource(string selector)
        {
            var resolved = ProfileLocator.Resolve(selector);
            return string.IsNullOrWhiteSpace(resolved)
                ? selector
                : Path.GetFullPath(resolved);
        }

        internal static bool TransactionMatches(Document doc, Transaction tr)
        {
            try
            {
                return tr.TransactionManager.UnmanagedObject ==
                       doc.Database.TransactionManager.UnmanagedObject;
            }
            catch
            {
                return false;
            }
        }

        public sealed class CatalogLoadResult
        {
            public CatalogSnapshot? Snapshot { get; init; }
            /// <summary>The verified price-list file behind <see cref="Snapshot"/> (display-only uses such as chapter titles).</summary>
            public string? CatalogPath { get; init; }
            /// <summary>The registered sheet of an explicitly mapped book (b15); null = the first sheet, as before.</summary>
            public string? CatalogSheetName { get; init; }
            public List<DeliveryFinding> Findings { get; init; } = new();
            /// <summary>
            /// Approved edition links of this list that were not applied (revoked item, changed item, duplicate). Not a
            /// load failure: their library items stay unpriced and the draft says so. Shown to the engineer.
            /// </summary>
            public IReadOnlyList<string> StaleEditionLinks { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// Loads the profile-pinned price book and verifies its hash — pricing against
        /// an unverified snapshot is refused, not warned away (plan §8.10).
        /// </summary>
        public CatalogLoadResult LoadCatalog(ProjectProfile profile) => LoadCatalog(profile, null);

        public CatalogLoadResult LoadCatalog(ProjectProfile profile, string? profileSource)
        {
            var findings = new List<DeliveryFinding>();
            if (!CatalogIdentity.TryGetActiveProfileIdentity(profile, out var identity,
                    out var identityErrors) || identity == null)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.PriceSourceUnverified,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "זהות המחירון הפעיל אינה מלאה או אינה עקבית",
                    Message = string.Join("; ", identityErrors),
                    RecommendedAction = "יש לרשום מחירון דרך הכלי ולהגדירו כפעיל; אין להשלים id/hash/file ידנית.",
                });
                return new CatalogLoadResult { Findings = findings };
            }

            var path = LocateCatalog(profile.ProfileId, identity.CatalogFile,
                identity.FileHash, profileSource);
            if (path == null)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.PriceSourceUnverified,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = $"קובץ המחירון '{identity.CatalogFile}' לא נמצא או שאינו תואם ל-hash הרשום",
                    Message = profileSource == null ? string.Empty : $"פרופיל שנבחר: {profileSource}. מחירון יחסי נבדק ליד קובץ זה בלבד.",
                    RecommendedAction = "בדוק את קובץ הפרופיל שנבחר ואת המחירון שלידו, או טען מחירון דרך הכלי; אין לשנות hash ידנית.",
                });
                return new CatalogLoadResult { Findings = findings };
            }

            var actualHash = ArtifactHash.Sha256OfFile(path);
            if (!CatalogIdentity.IsValidSha256(actualHash) ||
                !string.Equals(identity.FileHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.PriceSourceUnverified,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "קובץ המחירון שונה מהמהדורה הנעוצה בפרופיל (hash לא תואם)",
                    Message = $"expected {identity.FileHash}, actual {actualHash} ({path})",
                });
                return new CatalogLoadResult { Findings = findings };
            }

            try
            {
                // A book registered with an explicit sheet/column choice is read with exactly that choice (b15);
                // its mapping is bound to the entry's FileHash, so other bytes are refused by the reader itself. The
                // entry is the one the identity check already resolved — no second lookup that could miss it (S5-3).
                var mapping = PriceBookRegistry.LoaderMapping(identity.RegistryEntry);
                var snapshot = mapping == null
                    ? PriceBookXlsxLoader.Load(path, identity.SnapshotId)
                    : PriceBookXlsxLoader.Load(path, identity.SnapshotId, mapping);
                if (!CatalogIdentity.SnapshotMatches(identity, snapshot))
                {
                    findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.PriceSourceUnverified,
                        Domain = "estimate",
                        Severity = FindingSeverity.Error,
                        Title = "זהות המחירון שנטען אינה תואמת לרישום הפעיל",
                        Message = $"loaded {snapshot.SnapshotId}/{snapshot.FileHash}; expected " +
                                  $"{identity.SnapshotId}/{identity.FileHash}",
                    });
                    return new CatalogLoadResult { Findings = findings };
                }
                foreach (var rule in profile.Estimate.QuantitySources.Rules)
                {
                    // Missing binding/fingerprint is a legacy unapproved suggestion
                    // and does not prevent loading the catalog for re-approval. A
                    // fully bound but contradictory fingerprint is corruption and
                    // must stop pricing.
                    if (!string.Equals(rule.ApprovedCatalogId, identity.SnapshotId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(rule.ApprovedCatalogHash, identity.FileHash,
                            StringComparison.OrdinalIgnoreCase) ||
                        !CatalogIdentity.IsValidSha256(rule.ApprovedCatalogItemFingerprint))
                        continue;
                    if (string.IsNullOrWhiteSpace(rule.CandidateCatalogCode) ||
                        !snapshot.Items.TryGetValue(rule.CandidateCatalogCode, out var item) ||
                        !string.Equals(rule.ApprovedCatalogItemFingerprint,
                            CatalogIdentity.ItemFingerprint(item),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new DeliveryFinding
                        {
                            Code = EstimateFindingCodes.PriceSourceUnverified,
                            Domain = "estimate",
                            Severity = FindingSeverity.Error,
                            Title = $"אישור המיפוי '{rule.RuleKey}' אינו תואם לפריט שאושר במחירון",
                            RecommendedAction = "יש לבטל ולאשר מחדש את המיפוי מול המחירון הפעיל.",
                        });
                    }
                }
                if (findings.Count > 0)
                    return new CatalogLoadResult { Findings = findings };
                // The verified list with the engineer's approved edition links for exactly this list (EditionLinkPolicy).
                var linked = MahodAI.CivilDelivery.Estimate.Recognition.EditionLinkPolicy.WithLibraryAliases(
                    snapshot, profile.Estimate.EditionLinks, out var staleLinks);
                return new CatalogLoadResult
                {
                    Snapshot = linked, Findings = findings, CatalogPath = path, StaleEditionLinks = staleLinks,
                    CatalogSheetName = mapping?.SheetName,
                };
            }
            catch (Exception ex)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.PriceSourceUnverified,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "קובץ המחירון נדחה בבדיקת המבנה",
                    Message = ex.Message,
                });
                return new CatalogLoadResult { Findings = findings };
            }
        }

        public EstimateResult Build(
            Document doc, ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile)
        {
            RequireFinalEstimateScope(profile);
            return BuildForReview(doc, scan, snapshot, profile);
        }

        /// <summary>
        /// Calculate reviewed lines without treating unfinished project coverage as
        /// complete. The strict Build/Export endpoints retain their scope guards.
        /// Both paths publish the same complete result, including unresolved lines.
        /// </summary>
        public EstimateResult BuildForReview(
            Document doc, ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile)
        {
            RequireReviewedSourceScope(profile);
            RequireFresh(doc, scan, "בניית האומדן");
            RequirePublishedScanEvidence(scan);
            if (!string.Equals(scan.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "בניית האומדן נעצרה: פרופיל הפרויקט שונה מאז הסריקה");
            var effectiveProfileHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
            if (!string.Equals(scan.ProjectProfileEffectiveHash, effectiveProfileHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "בניית האומדן נעצרה: תוכן פרופיל הפרויקט שבזיכרון שונה מאז הסריקה");
            // Groups the engineer marked "not a construction quantity" are kept out of the
            // priced document — but never silently: the finding below names every one of
            // them with its quantity, and the raw records stay in the scan artifacts.
            var ignored = ApplyIgnoredRules(scan.Records, profile);

            // Scan-level preflight is part of the estimate contract, not a side JSON.
            // Duplicate/overlap and stale-corridor findings must exclude affected
            // money and remain present in the adjacent audit.
            var result = EstimateBuilder.Build(
                ignored.PricedRecords, snapshot, profile, scan.RunId, scan.Findings,
                new EstimateBuildContext(
                    scan.ProjectProfileHash ?? "",
                    EstimateBuildContext.SourceFileHash,
                    effectiveProfileHash,
                    EstimateBuildContext.CivilLiveSaved,
                    scan.SourceDrawing,
                    scan.SourceDrawingHash ?? "",
                    scan.DatabaseRevision ?? "",
                    scan.SourceDbMod)
                {
                    ExternalSources = scan.ExternalSources.ToList(),
                });

            result.Exclusions.AddRange(ignored.Exclusions);
            result.Findings.AddRange(ignored.AuditFindings);

            // The runtime result deliberately keeps shared preflight finding objects on
            // affected lines for UI/tool consumers.  The persisted artifact uses a
            // canonical finding registry plus per-line ids so a finding that names N
            // records is not serialized N times (quadratic growth on real drawings).
            var manifestFindings = ManifestFindings(result);
            SectionsWorkflowService.PersistEvidenceBundle(
                scan.RunId,
                "estimate_result.json",
                EstimateResultArtifact.From(result),
                (pendingRoot, publishedRoot) => WriteEstimateManifest(
                    scan, "build", result.Status,
                    BuildManifestCounts(scan, result, manifestFindings), manifestFindings,
                    pendingRoot, publishedRoot),
                replaceExisting: true,
                removeArtifacts: new[] { "export_result.json", "partial_priced_export_result.json" });
            return result;
        }

        internal sealed record IgnoredRuleApplication(
            IReadOnlyList<NeutralQuantityRecord> PricedRecords,
            IReadOnlyList<EstimateExclusion> Exclusions,
            IReadOnlyList<DeliveryFinding> AuditFindings);

        /// <summary>
        /// Pure, testable boundary for the engineer's explicit not-a-quantity decisions.
        /// No record is mutated or destroyed; the audit finding names the exact rule key,
        /// layer, quantity, unit and object count excluded from the priced line set.
        /// </summary>
        internal static IgnoredRuleApplication ApplyIgnoredRules(
            IReadOnlyList<NeutralQuantityRecord> records,
            ProjectProfile profile)
        {
            var decisions = IgnoredRulePolicy.ApprovedDecisions(profile);
            var approvedKeys = decisions.Keys.ToHashSet(StringComparer.Ordinal);
            var findings = new List<DeliveryFinding>();

            // Ignore authority is never allowed to hide a failed measurement.  This
            // check is deliberately performed again when applying persisted decisions:
            // a formerly valid/manual decision may be stale against a later scan.
            // If one record in a rule group is invalid, retain the whole group so a
            // group-wide exclusion cannot create a misleading partial result.
            var invalidApprovedGroups = records
                .Where(r => approvedKeys.Contains(r.Classification.RuleKey ?? string.Empty))
                .Where(r => !QuantitySignificance.IsValidMeasurement(r.Measurement.RawValue))
                .GroupBy(r => r.Classification.RuleKey!, StringComparer.Ordinal)
                .ToList();
            var blockedKeys = invalidApprovedGroups
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var group in invalidApprovedGroups)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MeasurementFailed,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = $"החרגת הכמות '{Bidi.Ltr(group.Key)}' נדחתה בגלל כשל מדידה",
                    Message = "החלטת החרגה קיימת אינה יכולה להסתיר NaN, Infinity, אפס או כמות שלילית; הרשומות נשארו באומדן לבדיקת הכשל.",
                    RecommendedAction = "יש לתקן את מקור המדידה, להריץ סריקה חדשה ורק לאחר מכן לבחון מחדש את החלטת הרלוונטיות.",
                    AffectedRecordIds = group.Select(r => r.RecordId)
                        .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                });
            }
            var effectiveApprovedKeys = approvedKeys
                .Where(key => !blockedKeys.Contains(key))
                .ToHashSet(StringComparer.Ordinal);

            // Bare legacy keys have no reason/approver/time and therefore cannot make
            // measured work disappear. Keep the records and surface the migration.
            var legacyOnly = profile.Estimate.IgnoredRuleKeys
                .Where(key => !approvedKeys.Contains(key))
                .ToHashSet(StringComparer.Ordinal);
            var legacyRecords = records.Where(r => legacyOnly.Contains(
                r.Classification.RuleKey ?? string.Empty)).ToList();
            if (legacyRecords.Count > 0)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.NotAQuantity,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "החרגות ישנות חסרות סיבה, מאשר וחותמת זמן — הכמויות לא הוסרו",
                    Message = string.Join(", ", legacyOnly.OrderBy(x => x, StringComparer.Ordinal)),
                    RecommendedAction = "יש לאשר מחדש כל החרגה עם סיבה הנדסית דרך לשונית האומדן.",
                    AffectedRecordIds = legacyRecords.Select(r => r.RecordId).ToList(),
                });
            }

            if (effectiveApprovedKeys.Count == 0)
                return new IgnoredRuleApplication(records, Array.Empty<EstimateExclusion>(), findings);

            var excludedRecords = records
                .Where(r => effectiveApprovedKeys.Contains(r.Classification.RuleKey ?? ""))
                .ToList();
            if (excludedRecords.Count == 0)
                return new IgnoredRuleApplication(records, Array.Empty<EstimateExclusion>(), findings);

            var priced = records
                .Where(r => !effectiveApprovedKeys.Contains(r.Classification.RuleKey ?? ""))
                .ToList();
            var exclusions = excludedRecords
                .GroupBy(r => r.Classification.RuleKey ?? "?", StringComparer.Ordinal)
                .Select(group =>
                {
                    var decision = decisions[group.Key];
                    return new EstimateExclusion
                    {
                        RuleKey = group.Key,
                        Reason = decision.Reason!.Trim(),
                        ApprovedBy = decision.ApprovedBy!.Trim(),
                        ApprovedAtUtc = decision.ApprovedAtUtc!.Value,
                        Sources = group.Select(r => new EstimateExclusionSource
                        {
                            RecordId = r.RecordId,
                            Drawing = r.Source.Drawing,
                            DrawingPath = r.Source.DrawingPath,
                            DrawingHash = r.Source.DrawingHash,
                            Handle = r.Source.Handle,
                            Xref = r.Source.Xref,
                            EntityType = r.Source.EntityType,
                            Layer = r.Source.Layer,
                            MeasurementMethod = r.Measurement.Method,
                            MeasurementKind = r.Measurement.Kind,
                            Unit = r.Measurement.Unit,
                            RawValue = r.Measurement.RawValue,
                        }).ToList(),
                    };
                })
                .OrderBy(x => x.RuleKey, StringComparer.Ordinal)
                .ToList();
            var excluded = excludedRecords
                .GroupBy(r => (
                    RuleKey: r.Classification.RuleKey ?? "?",
                    Layer: r.Source.Layer ?? "?",
                    Unit: r.Measurement.Unit))
                .Select(g => $"{g.Key.RuleKey} · {g.Key.Layer} " +
                             $"({g.Sum(x => x.Measurement.RawValue):N2} {g.Key.Unit}, {g.Count()} עצמים)")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            var finding = new DeliveryFinding
            {
                Code = EstimateFindingCodes.NotAQuantity,
                Domain = "estimate",
                Severity = FindingSeverity.Info,
                Title = $"{excluded.Count} קבוצות כמות סומנו כלא רלוונטיות ואינן נכללות במסמך המתומחר",
                Message = string.Join(" · ", excluded),
                RecommendedAction = "אפשר להחזיר קבוצה לרשימה בלשונית אומדן — כפתור 'החזר לרשימה'.",
            };
            findings.Add(finding);
            return new IgnoredRuleApplication(priced, exclusions, findings);
        }

        // ------------------------------------------------------------- evidence

        internal static List<DeliveryFinding> ManifestFindings(ScanResult scan) =>
            scan.Findings
                .Concat(scan.Records.SelectMany(r => r.Findings))
                .GroupBy(f => f.FindingId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

        internal static List<DeliveryFinding> ManifestFindings(EstimateResult estimate) =>
            estimate.Findings
                .Concat(estimate.Lines.SelectMany(l => l.Findings))
                .GroupBy(f => f.FindingId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

        internal static Dictionary<string, int> ScanManifestCounts(
            ScanResult scan, IReadOnlyCollection<DeliveryFinding> findings) => new()
        {
            ["scanned_entities"] = scan.ScannedEntities,
            ["neutral_records"] = scan.Records.Count,
            ["ready_records"] = scan.Records.Count(r => r.Status == DeliveryStatus.Ready),
            ["review_required_records"] = scan.Records.Count(r => r.Status == DeliveryStatus.ReviewRequired),
            ["failed_records"] = scan.Records.Count(r => r.Status == DeliveryStatus.Failed),
            ["unmapped_records"] = scan.Records.Count(r =>
                string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode)),
            ["findings"] = findings.Count,
            ["blocking_findings"] = findings.Count(EstimatePreflightPolicy.IsBlocking),
            ["discovery_mode"] = scan.DiscoveryMode ? 1 : 0,
        };

        internal static Dictionary<string, int> BuildManifestCounts(
            ScanResult scan, EstimateResult estimate,
            IReadOnlyCollection<DeliveryFinding> findings) => new()
        {
            ["source_records"] = scan.Records.Count,
            ["estimate_lines"] = estimate.Lines.Count,
            ["included_lines"] = estimate.Lines.Count(l => l.IncludedInTotals),
            ["excluded_lines"] = estimate.Lines.Count(l => !l.IncludedInTotals),
            ["audited_exclusions"] = estimate.Exclusions.Count,
            ["priced_lines"] = estimate.Lines.Count(l =>
                l.PriceStatus == PriceStatus.Priced || l.PriceStatus == PriceStatus.ProjectOverride),
            ["unmapped_lines"] = estimate.Lines.Count(l => l.PriceStatus == PriceStatus.Unmapped),
            ["missing_price_lines"] = estimate.Lines.Count(l => l.PriceStatus == PriceStatus.MissingPrice),
            ["findings"] = findings.Count,
            ["blocking_findings"] = findings.Count(EstimatePreflightPolicy.IsBlocking),
        };

        private static void WriteEstimateManifest(
            ScanResult scan,
            string operation,
            DeliveryStatus status,
            IReadOnlyDictionary<string, int> counts,
            IReadOnlyCollection<DeliveryFinding> findings,
            string? runsRoot = null,
            string? publishedRunsRoot = null,
            IEnumerable<RunManifestArtifactInput>? relatedArtifacts = null)
        {
            RuntimeRunManifestService.Write(
                scan.RunId, "estimate", operation,
                scan.ProjectProfileId, scan.ProjectProfileHash,
                status, counts, findings, ManifestInputs(scan), relatedArtifacts,
                runsRoot: runsRoot, publishedRunsRoot: publishedRunsRoot);
        }

        internal static IEnumerable<RunManifestInput> ManifestInputs(ScanResult scan)
        {
            // One input per distinct (path, hash), first occurrence order: 74,900 records
            // of the full working copy name six drawings. A path supplied with two
            // different hashes still reaches the composer twice and is refused there.
            var inputs = new List<RunManifestInput>
            {
                new RunManifestInput(scan.SourceDrawing, scan.SourceDrawingHash),
            };
            foreach (var record in scan.Records)
            {
                inputs.Add(new RunManifestInput(
                    record.Source.DrawingPath ?? record.Source.Drawing,
                    record.Source.DrawingHash));
            }
            foreach (var source in scan.ExternalSources)
                inputs.Add(new RunManifestInput(source.DrawingPath, source.DrawingHash));
            return inputs.Distinct();
        }

        public EstimateExcelWriter.WriteResult Export(
            Document doc, ScanResult scan, EstimateResult estimate, ProjectProfile profile,
            string? outputDir = null, string? drawingName = null)
        {
            ArgumentNullException.ThrowIfNull(profile);
            RequireFinalEstimateScope(profile);
            return ExportCore(doc, scan, estimate, profile, outputDir, drawingName, partialDraft: false);
        }

        public EstimateExcelWriter.WriteResult ExportPartialPricedDraft(
            Document doc, ScanResult scan, EstimateResult estimate, ProjectProfile profile,
            string? outputDir = null, string? drawingName = null)
        {
            RequireReviewedSourceScope(profile);
            return ExportCore(doc, scan, estimate, profile, outputDir, drawingName, partialDraft: true);
        }

        private EstimateExcelWriter.WriteResult ExportCore(
            Document doc, ScanResult scan, EstimateResult estimate, ProjectProfile profile,
            string? outputDir, string? drawingName, bool partialDraft)
        {
            RequireFresh(doc, scan, "ייצוא האומדן");
            if (!string.Equals(estimate.RunId, scan.RunId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: התוצאה אינה שייכת לסריקת הכמויות הפעילה");
            if (!string.Equals(estimate.ProjectProfileHash, scan.ProjectProfileHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(estimate.ProjectProfileEffectiveHash,
                    scan.ProjectProfileEffectiveHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(estimate.SourceDrawingHash, scan.SourceDrawingHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(estimate.SourceDatabaseRevision, scan.DatabaseRevision,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: עקבת המקור של התוצאה אינה תואמת לסריקה הפעילה");
            if (!ExternalSourcesEqual(estimate.ExternalSources, scan.ExternalSources))
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: עקבת מקורות ה-XREF אינה תואמת לסריקה הפעילה");
            var effectiveProfileHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
            if (!string.Equals(profile.ProfileId, estimate.ProjectProfileId,
                    StringComparison.Ordinal) ||
                !string.Equals(effectiveProfileHash,
                    estimate.ProjectProfileEffectiveHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: הפרופיל שבזיכרון אינו הפרופיל ששימש לבנייה");
            if (!CatalogIdentity.TryGetActiveProfileIdentity(
                    profile, out var exportCatalogIdentity, out var catalogErrors) ||
                exportCatalogIdentity == null ||
                !string.Equals(exportCatalogIdentity.SnapshotId, estimate.PriceBookId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(exportCatalogIdentity.FileHash, estimate.PriceBookHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: זהות המחירון הפעיל אינה זהות המחירון ששימש לבנייה (" +
                    string.Join("; ", catalogErrors) + ")");
            var verifiedCatalog = LoadCatalog(profile, scan.ProfileSource);
            if (verifiedCatalog.Snapshot == null ||
                !CatalogIdentity.SnapshotMatches(
                    exportCatalogIdentity, verifiedCatalog.Snapshot))
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: קובץ המחירון הפעיל אינו זמין או אינו תואם ל-SHA של הבנייה");
            var priceReconciliationErrors = CatalogIdentity.ValidateEstimatePricing(
                estimate, verifiedCatalog.Snapshot);
            if (priceReconciliationErrors.Count > 0)
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: מחירי השורות אינם תואמים למחירון המאומת (" +
                    string.Join("; ", priceReconciliationErrors) + ")");
            RequirePublishedEstimateBuildEvidence(scan, estimate);
            var blockers = partialDraft
                ? EstimatePartialPricedDraftPolicy.Evaluate(estimate).BlockingReasons
                : EstimatePreflightPolicy.ExportBlockingReasons(estimate);
            if (blockers.Count > 0)
            {
                var codes = string.Join(", ", blockers);
                throw new InvalidOperationException(
                    $"האומדן חסום לייצוא: לא כל השורות מוכנות, משויכות, תואמות יחידה, מתומחרות וכלולות ({codes}). " +
                    "יש לפתור את כל הממצאים ולהריץ בנייה מחדש.");
            }

            var dir = outputDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MahodCivilDelivery", estimate.ProjectProfileId);
            var book = exportCatalogIdentity.RegistryEntry;
            var label = $"{book.Publisher} {book.Edition}".Trim();
            var options = new EstimateExcelWriter.WriteOptions(
                ProjectTitle: profile.ProjectName,
                PriceBookLabel: string.IsNullOrWhiteSpace(label) ? null : label,
                DrawingName: drawingName,
                PreparedBy: Environment.UserName);
            var written = partialDraft
                ? EstimateExcelWriter.WritePartialPricedDraft(estimate, dir,
                    $"טיוטה-חלקית-מתומחרת-{estimate.ProjectProfileId}-{DateTime.Now:yyyyMMdd-HHmm}", options)
                : EstimateExcelWriter.Write(estimate, dir,
                    $"אומדן-מוקדם-{estimate.ProjectProfileId}-{DateTime.Now:yyyyMMdd-HHmm}", options);
            var exportArtifact = new
            {
                written.XlsxPath,
                written.AuditPath,
                written.ManifestPath,
                written.XlsxHash,
                written.AuditHash,
                written.ManifestHash,
                PackageKind = partialDraft ? EstimatePartialPricedDraftPolicy.PackageKind : "estimate",
            };
            try
            {
                // Writing a large workbook is not an atomic read of the DWG,
                // XREFs, profile or catalog. Revalidate before publishing it;
                // the catch below withdraws only this newly generated package.
                RequireFresh(doc, scan, "פרסום האומדן לאחר כתיבת Excel");
                if (!string.Equals(EstimateTraceIdentity.EffectiveProfileHash(profile),
                        effectiveProfileHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("פרופיל הפרויקט השתנה במהלך הייצוא.");
                var catalogAfterWrite = LoadCatalog(profile, scan.ProfileSource);
                if (catalogAfterWrite.Snapshot == null ||
                    !CatalogIdentity.SnapshotMatches(exportCatalogIdentity, catalogAfterWrite.Snapshot))
                    throw new InvalidOperationException("המחירון השתנה או אינו ניתן לאימות לאחר הייצוא.");
                RequirePublishedEstimateBuildEvidence(scan, estimate);
                var manifestFindings = ManifestFindings(estimate);
                var counts = BuildManifestCounts(scan, estimate, manifestFindings);
                counts["export_packages"] = 1;
                SectionsWorkflowService.PersistEvidenceBundle(
                    estimate.RunId,
                    partialDraft ? "partial_priced_export_result.json" : "export_result.json",
                    exportArtifact,
                    (pendingRoot, publishedRoot) => WriteEstimateManifest(
                        scan, partialDraft ? "export-partial" : "export", estimate.Status, counts, manifestFindings,
                        pendingRoot, publishedRoot,
                        new[]
                        {
                            new RunManifestArtifactInput(written.XlsxPath, written.XlsxHash),
                            new RunManifestArtifactInput(written.AuditPath, written.AuditHash),
                            new RunManifestArtifactInput(written.ManifestPath, written.ManifestHash),
                        }),
                    replaceExisting: true);
                return written;
            }
            catch (Exception evidenceError)
            {
                var cleanupErrors = new List<string>();
                foreach (var path in new[] { written.XlsxPath, written.AuditPath, written.ManifestPath })
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception cleanupError)
                    {
                        cleanupErrors.Add($"{path}: {cleanupError.Message}");
                    }
                }
                throw new InvalidOperationException(
                    "Export package evidence could not be published, so the newly generated package was withdrawn." +
                    (cleanupErrors.Count == 0
                        ? string.Empty
                        : " Cleanup also failed: " + string.Join(" | ", cleanupErrors)),
                    evidenceError);
            }
        }

        internal static void RequirePublishedEstimateBuildEvidence(
            ScanResult scan, EstimateResult estimate)
        {
            try
            {
                RuntimeRunManifestService.RequirePublishedArtifact(
                    scan.RunId,
                    "estimate_result.json",
                    EstimateResultArtifact.From(estimate),
                    SectionsWorkflowService.Json,
                    "estimate",
                    "build", "export", "export-partial");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "ייצוא האומדן נעצר: שרשרת הראיות של BUILD אינה תואמת לתוצאה הפעילה — יש לבנות מחדש",
                    ex);
            }
        }

        private static PublishedArtifactProof RequirePublishedScanEvidence(
            ScanResult scan)
        {
            try
            {
                return RuntimeRunManifestService.RequirePublishedArtifact(
                    scan.RunId,
                    "estimate_scan.json",
                    scan,
                    SectionsWorkflowService.Json,
                    "estimate",
                    "extract", "rebase", "propose", "build", "export", "export-partial");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "פעולת האומדן נעצרה: ראיות הסריקה אינן תואמות לאובייקט הפעיל — יש לסרוק מחדש",
                    ex);
            }
        }

        /// <summary>
        /// Ranked catalog suggestions for the unmapped groups in a scan. Pure evidence:
        /// nothing is written and no line is priced from a proposal — the engineer must
        /// still approve, and approval re-validates code and unit.
        /// </summary>
        internal sealed record MappingProposalPublication(
            IReadOnlyList<MappingProposal> Proposals,
            string? ReferenceSource,
            string? ReferenceHash,
            int ReferenceCodeCount,
            int GroupCount,
            IReadOnlyList<MappingEvidenceRefusal>? EvidenceRefusals = null,
            IReadOnlyList<MappingFamilyDecisionReview>? FamilyDecisionReviews = null,
            ProjectRuleContext? ProjectRules = null,
            IReadOnlyList<ProjectRuleReview>? ProjectRuleReviews = null);

        /// <summary>Read-only family decision disposition; does not veto independent CAD proposals or approve an item.</summary>
        internal sealed record MappingFamilyDecisionReview(string RuleKey, string? Layer, int ObjectCount,
            MahodAI.CivilDelivery.Estimate.Recognition.CatalogEvidenceBridge.Evidence Evidence);

        /// <summary>
        /// A group for which no catalog item was proposed automatically because its own CAD evidence contradicts
        /// itself (<see cref="MappingProposalEngine.EvidenceRefusal"/>). Shown to the engineer; nothing is excluded.
        /// </summary>
        internal sealed record MappingEvidenceRefusal(string RuleKey, string? Layer, int ObjectCount, string Message);

        public List<MappingProposal> ProposeMappings(
            ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile) =>
            ProposeAndPublishMappings(scan, snapshot, profile).Proposals.ToList();

        /// <summary><see cref="ProposeMappings"/> with the groups whose contradicting evidence stopped a proposal.</summary>
        internal MappingProposalPublication ProposeAndPublishMappings(
            ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile)
        {
            var context = EstimateScanTrace.Step("proposal.rules-context", () => PrepareProjectRuleContext(scan, snapshot));
            var pending = EstimateScanTrace.Step("proposal.compute", () => ProposeMappingsUnpublished(scan, snapshot, profile, context));
            EstimateScanTrace.Step("proposal.publish", () => PublishMappingProposalEvidence(scan, pending, snapshot));
            return pending;
        }

        /// <summary>
        /// Computes suggestions without replacing any run artifacts. AI callers use
        /// this while their host read transaction is live, then publish the immutable
        /// result from IReadOnlyTransactionClosedObserver after Abort+Dispose.
        /// </summary>
        internal MappingProposalPublication ProposeMappingsUnpublished(
            ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile, ProjectRuleContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            RequirePublishedScanEvidence(scan);
            var allGroups = BuildMappingProposalGroups(scan.Records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.For(profile), profile.Estimate.FamilyDecisions);
            var reviews = ProjectRuleReviews(scan.Records, context);
            // L05 (04647): only a group the project rules leave proven-unchanged reaches the heuristic, curated and Top-3 steps;
            // every other group (rule, excluded, unclassified, mixed, unproven lineage, or an unverified context) gets its review.
            var governed = reviews.Where(r => r.Governed).Select(r => r.RuleKey).ToHashSet(StringComparer.Ordinal);
            var groups = allGroups.Where(g => !governed.Contains(g.RuleKey)).ToList();
            // b19 (Codex 03:31): a group the active library holds as a decision with no item gets its review here, before the
            // heuristic and the curated rules (and so before the assistant, which honours every governed review).
            if (groups.Count > 0)
            {
                var libraryReviews = LibraryReviews(LibraryDispositions(scan, snapshot, profile), groups);
                if (libraryReviews.Count > 0)
                {
                    var held = libraryReviews.Select(r => r.RuleKey).ToHashSet(StringComparer.Ordinal);
                    groups = groups.Where(g => !held.Contains(g.RuleKey)).ToList();
                    reviews = reviews.Concat(libraryReviews).ToList();
                }
            }

            var reference = LoadReference(profile);
            if (groups.Count == 0)
                return new MappingProposalPublication(
                    Array.Empty<MappingProposal>(), reference.SourceFile,
                    reference.SourceHash, reference.CatalogCodes.Count, allGroups.Count,
                    ProjectRules: context, ProjectRuleReviews: reviews);

            var heuristic = MappingProposalEngine.Propose(groups, snapshot, reference.CatalogCodes);
            var refusals = groups
                .Select(group => (Group: group, Message: MappingProposalEngine.EvidenceRefusal(group)))
                .Where(item => item.Message != null)
                .Select(item => new MappingEvidenceRefusal(item.Group.RuleKey, item.Group.Layer,
                    item.Group.ObjectCount, item.Message!))
                .ToList();
            var curated = CuratedRuleProposals(groups, snapshot, profile);
            var proposals = curated.Concat(heuristic)
                .GroupBy(p => p.RuleKey, StringComparer.Ordinal)
                .SelectMany(g => g
                    .GroupBy(p => p.ProposedCode, StringComparer.OrdinalIgnoreCase)
                    .Select(code => code.OrderByDescending(p => p.Score).First())
                    .OrderByDescending(p => p.Score)
                    .ThenBy(p => p.ProposedCode, StringComparer.Ordinal)
                    .Take(MappingProposalEngine.MaxProposalsPerGroup))
                .ToList();
            return new MappingProposalPublication(
                proposals, reference.SourceFile, reference.SourceHash,
                reference.CatalogCodes.Count, allGroups.Count, refusals,
                groups.Where(g => g.RecognitionEvidence?.FamilyDecisionResolutions.Any(r => r.DecisionId != null ||
                        r.State == MahodAI.CivilDelivery.Estimate.Recognition.FamilyDecisionState.Stale) == true)
                    .Select(g => new MappingFamilyDecisionReview(g.RuleKey, g.Layer, g.ObjectCount, g.RecognitionEvidence!)).ToList(),
                context, reviews);
        }

        /// <summary>
        /// Shared production assembly point for measured, still-unmapped proposal groups. <paramref name="library"/> is the
        /// profile's library (EngineerBoqLibrary.For), passed explicitly — there is no default (Codex 01:27, 02/10).
        /// </summary>
        internal static List<MappingProposalEngine.DiscoveredGroup> BuildMappingProposalGroups(
            IEnumerable<NeutralQuantityRecord> records,
            MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary library,
            IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? familyDecisions = null)
        {
            ArgumentNullException.ThrowIfNull(library);
            var completeScan = records.ToList();
            var evidenceByRule = MahodAI.CivilDelivery.Estimate.Recognition.CatalogEvidenceBridge.ForProposalGroups(
                completeScan, library, familyDecisions);
            return completeScan
                .Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))
                .GroupBy(r => r.Classification.RuleKey ?? "(none)")
                .Select(g =>
                {
                    var first = g.First();
                    return new MappingProposalEngine.DiscoveredGroup(
                        g.Key,
                        // Eligibility needs the original XREF namespace to corroborate
                        // qualified marker-block identities. Keyword matching performs
                        // its own leaf extraction only after that source-aware gate.
                        first.Source.Layer,
                        first.Measurement.Kind,
                        first.Measurement.Unit,
                        g.Count(),
                        g.Sum(r => r.Measurement.RawValue),
                        QuantityCadMetadataPolicy.Summarize(g.Select(r => r.Measurement)),
                        // Readable object evidence and its local recognition: search terms, a stop on
                        // contradiction and the recognised family's items. Never approval or a price.
                        evidenceByRule[g.Key]);
                })
                .ToList();
        }

        internal static void PublishMappingProposalEvidence(
            ScanResult scan, MappingProposalPublication pending, CatalogSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(pending);
            ArgumentNullException.ThrowIfNull(snapshot);
            if (pending.GroupCount == 0) return;
            // Recheck the exact scan bytes immediately before replacing the run
            // manifest. A proposal calculated from withdrawn/stale evidence must not
            // become the new green head of the run.
            RequirePublishedScanEvidence(scan);
            // ...and the project-rule context the proposals were filtered by (L05): sources, receipt, profile, rules, catalog.
            if (pending.ProjectRules is { } rulesContext)
                RequireCurrentProjectRuleContext(scan, snapshot, rulesContext, pending.Proposals);
            var proposalArtifact = new
            {
                reference_source = pending.ReferenceSource,
                reference_hash = pending.ReferenceHash,
                reference_code_count = pending.ReferenceCodeCount,
                group_count = pending.GroupCount,
                proposals = pending.Proposals,
                family_decision_reviews = (pending.FamilyDecisionReviews ?? Array.Empty<MappingFamilyDecisionReview>())
                    .Select(review => new
                    {
                        rule_key = review.RuleKey, layer = review.Layer, object_count = review.ObjectCount,
                        family_id = review.Evidence.RecognisedFamily,
                        confirmed_decision_ids = review.Evidence.ConfirmedFamilyDecisionIds,
                        withheld = review.Evidence.FamilyWithheld,
                        resolutions = review.Evidence.FamilyDecisionResolutions.Select(r => new
                        {
                            group_id = r.GroupId, state = r.State.ToString(), decision_id = r.DecisionId,
                            family_id = r.FamilyId, stale_reason = r.StaleReason,
                        }).ToList(),
                    }).ToList(),
                evidence_refusals = (pending.EvidenceRefusals ?? Array.Empty<MappingEvidenceRefusal>())
                    .Select(refusal => new
                    {
                        rule_key = refusal.RuleKey, layer = refusal.Layer,
                        object_count = refusal.ObjectCount, message = refusal.Message,
                    }).ToList(),
                project_rules = pending.ProjectRules == null ? null : new
                {
                    state = pending.ProjectRules.State.ToString(),
                    reason = pending.ProjectRules.Reason,
                    stamp = pending.ProjectRules.Snapshot?.Stamp,
                    digest = pending.ProjectRules.Snapshot?.Digest,
                    pending_records = pending.ProjectRules.Snapshot?.Records.Count(r => r.State == ProjectRuleRecordContext.State.PendingLineage),
                    is_approval = false,
                    is_price = false,
                },
                project_rule_reviews = (pending.ProjectRuleReviews ?? Array.Empty<ProjectRuleReview>())
                    .Select(review => new
                    {
                        rule_key = review.RuleKey, layer = review.Layer, object_count = review.ObjectCount,
                        bound = review.BoundCount, pending = review.PendingCount, generic = review.GenericCount,
                        governed = review.Governed, lines = review.Lines, message = review.Message,
                        label = review.Label, scope_state = review.State,
                    }).ToList(),
            };
            var manifestFindings = ManifestFindings(scan);
            SectionsWorkflowService.PersistEvidenceBundle(
                scan.RunId,
                "mapping_proposals.json",
                proposalArtifact,
                (pendingRoot, publishedRoot) => WriteEstimateManifest(
                    scan, "propose", scan.Status,
                    ScanManifestCounts(scan, manifestFindings), manifestFindings,
                    pendingRoot, publishedRoot),
                replaceExisting: true,
                removeArtifacts: DerivedEstimateArtifacts);
        }

        /// <summary>
        /// A shipped/legacy rule is useful evidence but not approval. If its pattern,
        /// kind and exact unit all match the measured group and the pinned catalog,
        /// surface it first as PROPOSED_UNAPPROVED. It still reaches pricing only via
        /// SaveApprovedMappings, which stamps a named approver and timestamp.
        /// </summary>
        internal static List<MappingProposal> CuratedRuleProposals(
            IReadOnlyList<MappingProposalEngine.DiscoveredGroup> groups,
            CatalogSnapshot snapshot,
            ProjectProfile profile)
        {
            var proposals = new List<MappingProposal>();
            var candidates = profile.Estimate.QuantitySources.Rules
                .Select(rule => (Rule: rule, EvidenceKind: "profile-rule"))
                .Concat(BuiltInProposalOverlay(profile)
                    .Select(rule => (Rule: rule, EvidenceKind: "builtin-overlay-v1")))
                .ToList();
            foreach (var group in groups)
            {
                if (!MappingProposalEngine.IsProposalEligible(group)) continue;
                // Contradicting object evidence stops an unapproved profile or overlay rule as it stops the search:
                // "no item proposed" must not sit beside a rule proposal for the same group. Approved rules never
                // reach here (they are mappings, skipped below).
                if (MappingProposalEngine.EvidenceRefusal(group) != null) continue;

                var measured = Units.Parse(group.MeasuredUnit);
                if (measured.Canonical == "?") continue;

                foreach (var candidate in candidates)
                {
                    var rule = candidate.Rule;
                    if (CivilQuantityExtractionService.IsExplicitlyApproved(profile, rule)) continue;
                    if (string.IsNullOrWhiteSpace(rule.CandidateCatalogCode) ||
                        !snapshot.Items.TryGetValue(rule.CandidateCatalogCode, out var item)) continue;
                    if (!string.Equals(rule.MeasurementKind, group.MeasurementKind,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(group.Layer) ||
                        string.IsNullOrWhiteSpace(rule.LayerPattern)) continue;
                    var semanticLayer = SectionProjectionLogic.LayerLeaf(group.Layer);
                    if (!ClInstructionReader.WildcardMatch(group.Layer, rule.LayerPattern) &&
                        !ClInstructionReader.WildcardMatch(semanticLayer, rule.LayerPattern)) continue;
                    if (!measured.SameUnit(item.Unit)) continue;
                    if (!string.IsNullOrWhiteSpace(rule.ExpectedUnit) &&
                        !measured.SameUnit(Units.Parse(rule.ExpectedUnit))) continue;
                    // A broad wall default cannot turn anonymous symbol counts
                    // into wall-mounted assets. Only an intentional exact named-
                    // block count rule may supply that missing subject evidence.
                    var exactNamedCountRule = group.MeasurementKind.Equals("count", StringComparison.OrdinalIgnoreCase) &&
                        group.RuleKey.Contains("|count|block:", StringComparison.Ordinal) &&
                        string.Equals(rule.RuleKey, group.RuleKey, StringComparison.Ordinal) &&
                        (string.Equals(rule.LayerPattern, group.Layer, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rule.LayerPattern, semanticLayer, StringComparison.OrdinalIgnoreCase));
                    var exactLinearRule = group.MeasurementKind.Equals("length", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(rule.RuleKey, group.RuleKey, StringComparison.Ordinal) &&
                        (string.Equals(rule.LayerPattern, group.Layer, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rule.LayerPattern, semanticLayer, StringComparison.OrdinalIgnoreCase));
                    if (!MappingProposalEngine.IsAutomaticMeasurementSubjectCompatible(group, item.Description) &&
                        !exactNamedCountRule && !exactLinearRule)
                        continue;
                    if (!MappingProposalEngine.IsSemanticallyCompatible(group.Layer, item.Description))
                        continue;

                    var hasPrice = snapshot.Prices.TryGetValue(item.Code, out var price) && !price.IsMissing;
                    proposals.Add(new MappingProposal
                    {
                        RuleKey = group.RuleKey,
                        Layer = group.Layer,
                        MeasurementKind = group.MeasurementKind,
                        MeasuredUnit = group.MeasuredUnit,
                        ObjectCount = group.ObjectCount,
                        TotalQuantity = Math.Round(group.TotalQuantity, 3),
                        ProposedCode = item.Code,
                        CatalogDescription = item.Description.Length <= 120
                            ? item.Description
                            : item.Description[..119] + "…",
                        CatalogUnit = item.UnitRaw,
                        Score = 1_000_000 + (hasPrice ? 1 : 0),
                        EvidenceKind = candidate.EvidenceKind,
                        Reasons =
                        {
                            candidate.EvidenceKind == "profile-rule"
                                ? $"כלל פרופיל '{rule.RuleKey}' תואם לשכבה, לסוג המדידה וליחידה"
                                : $"כלל הצעה מובנה ומוגרס '{rule.RuleKey}' תואם לשכבה, לסוג המדידה וליחידה",
                            "הכלל טרם אושר בשם ובחותמת זמן — זו הצעה בלבד",
                            hasPrice ? "יש מחיר במהדורת המחירון הפעילה" : "אין מחיר במהדורת המחירון הפעילה",
                        },
                    });
                }
            }
            return proposals
                .GroupBy(proposal => new
                {
                    proposal.RuleKey,
                    Code = proposal.ProposedCode.ToUpperInvariant(),
                    proposal.MeasurementKind,
                    Unit = Units.Parse(proposal.MeasuredUnit).Canonical,
                })
                .Select(group => group
                    .OrderBy(proposal => proposal.EvidenceKind == "profile-rule" ? 0 : 1)
                    .First())
                .ToList();
        }

        /// <summary>
        /// Safe, proposal-only overlay for newer 6422 defaults.  Installed runtime
        /// profiles deliberately preserve engineers' CL/ROW/approval data, so they
        /// may lag the shipped proposal library.  These rules are read-only hints:
        /// an exact profile contract with a conflicting code suppresses the overlay,
        /// and SaveApprovedMappings still requires named human approval and pins the
        /// live catalog item/unit/hash.
        /// </summary>
        internal static IReadOnlyList<
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule>
            BuiltInProposalOverlay(ProjectProfile profile)
        {
            if (!string.Equals(profile.ProfileId, "6422", StringComparison.Ordinal))
                return Array.Empty<
                    ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule>();

            var builtIns = new[]
            {
                ProposalRule("builtin-overlay-v1:CURB-ILND", "*CURB-ILND*",
                    "length", "U51.06.2140", "מטר"),
                ProposalRule("builtin-overlay-v1:BIKE-LANE-EDGE", "*BIKE-LANE*",
                    "length", "U51.06.2930", "מטר"),
                ProposalRule("builtin-overlay-v1:PL-BIKE-EDGE", "*PL-BIKE*",
                    "length", "U51.06.2930", "מטר"),
            };

            return builtIns.Where(builtIn =>
            {
                var exactContract = profile.Estimate.QuantitySources.Rules.Where(existing =>
                    string.Equals(existing.LayerPattern, builtIn.LayerPattern,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.MeasurementKind, builtIn.MeasurementKind,
                        StringComparison.OrdinalIgnoreCase) &&
                    Units.Parse(existing.ExpectedUnit).SameUnit(
                        Units.Parse(builtIn.ExpectedUnit))).ToList();
                return !exactContract.Any(existing =>
                           CivilQuantityExtractionService.IsExplicitlyApproved(
                               profile, existing)) &&
                       !exactContract.Any(existing =>
                    !string.Equals(existing.CandidateCatalogCode,
                        builtIn.CandidateCatalogCode,
                        StringComparison.OrdinalIgnoreCase));
            }).ToList();

            static ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                ProposalRule(string key, string pattern, string kind, string code, string unit) =>
                new()
                {
                    RuleKey = key,
                    LayerPattern = pattern,
                    MeasurementKind = kind,
                    CandidateCatalogCode = code,
                    ExpectedUnit = unit,
                    Notes = "Mahod built-in proposal overlay v1 — never an approval",
                };
        }

        /// <summary>
        /// Loads the comparable delivered estimate that supplies the reference code
        /// list. Absent reference = weaker ranking, never a failure.
        /// </summary>
        private static ReferenceEstimateLoader.Reference LoadReference(ProjectProfile profile)
        {
            // This bundled workbook is a recorded 6422 reference, not an office-
            // wide approval or a training example for every new project.
            if (!UsesBundled6422Reference(profile.ProfileId))
                return new ReferenceEstimateLoader.Reference
                {
                    SourceFile = string.Empty, SourceHash = string.Empty,
                };
            var dllDir = Path.GetDirectoryName(typeof(EstimateWorkflowService).Assembly.Location);
            var candidates = new List<string>();
            if (dllDir != null)
            {
                candidates.Add(Path.Combine(dllDir, "fixtures", "judgment2-golden.xlsx"));
                candidates.Add(Path.GetFullPath(Path.Combine(dllDir, "..", "..", "..", "..",
                    "fixtures", "civil-delivery", "estimate", "judgment2-golden.xlsx")));
            }
            var profilePath = ProfileLocator.Resolve(profile.ProfileId);
            if (profilePath != null)
                candidates.Add(Path.Combine(Path.GetDirectoryName(profilePath)!, "judgment2-golden.xlsx"));

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return ReferenceEstimateLoader.Load(c);
            }
            return ReferenceEstimateLoader.Load(candidates.FirstOrDefault() ?? "judgment2-golden.xlsx");
        }

        internal static bool UsesBundled6422Reference(string? profileId) =>
            string.Equals(profileId, "6422", StringComparison.Ordinal);

        /// <summary>
        /// Persists an engineer-approved mapping from a discovered rule key to a
        /// catalog code, so the next scan produces a priced estimate line instead of
        /// an UNMAPPED review item. The catalog code must exist in the snapshot and
        /// its unit must match what was actually measured — no silent conversion.
        /// </summary>
        public sealed record MappingApproval(
            string RuleKey, string CatalogCode, string LayerPattern, string EntityType,
            string MeasurementKind, string MeasuredUnit);

        /// <summary>
        /// One group eligible for the explicit reviewed batch route.  "Eligible" is
        /// deliberately much narrower than "has a suggestion": exactly one proven
        /// catalog code, one source contract, same unit, a pinned non-missing price,
        /// plausible quantity, and no duplicate/overlap/mixed-dimension evidence.
        /// </summary>
        public sealed record ProvenBatchMappingCandidate(
            MappingApproval Approval,
            string Layer,
            int ObjectCount,
            double TotalQuantity,
            string Unit,
            string CatalogDescription,
            decimal Price,
            string EvidenceKind,
            string EvidenceSummary);

        internal static IReadOnlyList<ProvenBatchMappingCandidate>
            ProvenBatchMappingCandidates(
                ScanResult scan,
                CatalogSnapshot snapshot,
                ProjectProfile profile,
                IReadOnlyList<MappingProposal> proposals)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(proposals);
            if (!IsReviewedSourceScopeApproved(profile) || !scan.DiscoveryMode)
                return Array.Empty<ProvenBatchMappingCandidate>();
            if (!CatalogIdentity.TryGetActiveProfileIdentity(
                    profile, out var activeIdentity, out _) ||
                activeIdentity == null ||
                !CatalogIdentity.SnapshotMatches(activeIdentity, snapshot))
                return Array.Empty<ProvenBatchMappingCandidate>();

            // Unmapped is the one expected finding this action resolves. Every other
            // record-scoped review/error finding is a reason to keep the group in the
            // individual lane; a new detector must therefore fail closed without
            // somebody remembering to add its code to a hand-maintained allowlist.
            static bool BlocksBatchMapping(DeliveryFinding finding) =>
                !string.Equals(finding.Code, EstimateFindingCodes.Unmapped,
                    StringComparison.Ordinal) &&
                (finding.Severity >= FindingSeverity.ReviewRequired ||
                 EstimatePreflightPolicy.IsBlocking(finding));

            if (scan.Findings.Any(finding =>
                    finding.AffectedRecordIds.Count == 0 &&
                    BlocksBatchMapping(finding)))
                return Array.Empty<ProvenBatchMappingCandidate>();

            var conflictedRecordIds = scan.Findings
                .Where(BlocksBatchMapping)
                .SelectMany(finding => finding.AffectedRecordIds)
                .ToHashSet(StringComparer.Ordinal);
            bool HasExactUnmappedEvidence(NeutralQuantityRecord record) =>
                record.Findings.Any(finding => string.Equals(
                    finding.Code, EstimateFindingCodes.Unmapped,
                    StringComparison.Ordinal)) ||
                scan.Findings.Any(finding =>
                    string.Equals(finding.Code, EstimateFindingCodes.Unmapped,
                        StringComparison.Ordinal) &&
                    finding.AffectedRecordIds.Contains(
                        record.RecordId, StringComparer.Ordinal));
            var closedPolylineKeys = ClosedPolylineAlternativePolicy
                .FindExactPairs(scan.Records)
                .SelectMany(pair => new[] { pair.FirstRuleKey, pair.SecondRuleKey })
                .ToHashSet(StringComparer.Ordinal);
            var result = new List<ProvenBatchMappingCandidate>();

            foreach (var group in scan.Records
                         .Where(record => !string.IsNullOrWhiteSpace(
                             record.Classification.RuleKey))
                         .GroupBy(record => record.Classification.RuleKey!,
                             StringComparer.Ordinal))
            {
                var records = group.ToList();
                if (closedPolylineKeys.Contains(group.Key) ||
                    records.Any(record => conflictedRecordIds.Contains(record.RecordId)) ||
                    records.Any(record =>
                                          record.Status != DeliveryStatus.ReviewRequired ||
                                          !HasExactUnmappedEvidence(record) ||
                                          record.Findings.Any(BlocksBatchMapping)) ||
                    records.Any(record => !string.IsNullOrWhiteSpace(
                        record.Classification.CandidateCatalogCode)) ||
                    ApprovedIgnoredRuleDecision(profile, group.Key) != null ||
                    records.Any(record => !QuantitySignificance.IsValidMeasurement(
                        record.Measurement.RawValue)))
                    continue;

                var layers = records.Select(record =>
                        SectionProjectionLogic.LayerLeaf(record.Source.Layer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var entityTypes = records.Select(record => record.Source.EntityType)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var kinds = records.Select(record => record.Measurement.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var units = records.Select(record => Units.Parse(record.Measurement.Unit).Canonical)
                    .Distinct(StringComparer.Ordinal).ToList();
                if (layers.Count != 1 || string.IsNullOrWhiteSpace(layers[0]) ||
                    entityTypes.Count != 1 || kinds.Count != 1 || units.Count != 1 ||
                    units[0] == "?")
                    continue;

                var total = records.Sum(record => record.Measurement.RawValue);
                if (!MappingProposalEngine.IsProposalEligible(
                        new MappingProposalEngine.DiscoveredGroup(
                            group.Key, layers[0], kinds[0], records[0].Measurement.Unit,
                            records.Count, total)))
                    continue;

                bool IsExactProfileProposal(MappingProposal proposal) =>
                    proposal.EvidenceKind == "profile-rule" &&
                    profile.Estimate.QuantitySources.Rules.Any(rule =>
                        !CivilQuantityExtractionService.IsExplicitlyApproved(profile, rule) &&
                        string.Equals(rule.CandidateCatalogCode, proposal.ProposedCode,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(rule.LayerPattern?.Trim(), layers[0],
                            StringComparison.OrdinalIgnoreCase) &&
                        !(rule.LayerPattern?.IndexOfAny(new[] { '*', '?' }) >= 0) &&
                        string.Equals(rule.MeasurementKind, kinds[0],
                            StringComparison.OrdinalIgnoreCase) &&
                        Units.Parse(rule.ExpectedUnit).Canonical == units[0]);

                var proven = proposals.Where(proposal =>
                        string.Equals(proposal.RuleKey, group.Key,
                            StringComparison.Ordinal) &&
                        (proposal.EvidenceKind == "builtin-overlay-v1" ||
                         IsExactProfileProposal(proposal)))
                    .GroupBy(proposal => proposal.ProposedCode,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(code => code.OrderByDescending(proposal => proposal.Score).First())
                    .ToList();
                if (proven.Count != 1) continue;

                // "One proven proposal" is still unsafe if the review table also
                // carries another plausible catalog code for the same exact group.
                // Batch approval is intentionally smaller than manual approval.
                var distinctDisplayedCodes = proposals
                    .Where(proposal => string.Equals(proposal.RuleKey, group.Key,
                        StringComparison.Ordinal))
                    .Select(proposal => proposal.ProposedCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (distinctDisplayedCodes.Count != 1 ||
                    !string.Equals(distinctDisplayedCodes[0], proven[0].ProposedCode,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var selected = proven[0];
                if (!snapshot.Items.TryGetValue(selected.ProposedCode, out var item) ||
                    !Units.Parse(records[0].Measurement.Unit).SameUnit(item.Unit) ||
                    !MappingProposalEngine.IsSemanticallyCompatible(layers[0], item.Description) ||
                    !snapshot.Prices.TryGetValue(item.Code, out var price) || price.IsMissing ||
                    price.Price is not { } amount)
                    continue;

                result.Add(new ProvenBatchMappingCandidate(
                    new MappingApproval(
                        group.Key,
                        item.Code,
                        layers[0],
                        entityTypes[0],
                        kinds[0],
                        records[0].Measurement.Unit),
                    layers[0],
                    records.Count,
                    Math.Round(total, 3),
                    records[0].Measurement.Unit,
                    item.Description,
                    amount,
                    selected.EvidenceKind,
                    string.Join(" · ", selected.Reasons)));
            }

            return result.OrderBy(candidate => candidate.Layer,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Approval.RuleKey,
                    StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// True only for the complete product scope: discovery across every supported
        /// host entity plus recursively traversed, resolved and hash-verified XREFs.
        /// </summary>
        public static bool IsCompleteDiscoveryScopeApproved(ProjectProfile? profile) =>
            profile != null &&
            string.Equals(
                profile.Estimate.QuantitySources.SourceScopePolicy,
                EstimatePreflightPolicy.DiscoverAllSourceScopePolicy,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                profile.Estimate.QuantitySources.XrefPolicy,
                EstimatePreflightPolicy.IncludeXrefsPolicy,
                StringComparison.OrdinalIgnoreCase);

        public static bool IsReviewedSourceScopeApproved(ProjectProfile? profile) => profile != null &&
            EstimatePreflightPolicy.ValidateSourcePolicies(profile).Count == 0;

        public static string SourceScopeDisplay(ProjectProfile profile) => profile.Estimate.SourceSelection is { } selection
            ? EstimateSourceSelectionPolicy.ScopeSummary(selection) : EstimatePreflightPolicy.CompleteScopeNotice;

        public enum EarthworksDecisionStatus
        {
            Unresolved,
            Included,
            Excluded,
            Invalid,
        }

        /// <summary>
        /// One presentation-independent interpretation of the persisted earthworks
        /// choice. Both the palette and the quantity collector use this result, so a
        /// half-filled YAML entry can never look accepted in one route and blocked in
        /// another.
        /// </summary>
        public sealed record EarthworksDecisionSummary(
            EarthworksDecisionStatus Status,
            string DisplayText,
            string GateText,
            string? DecidedBy,
            DateTime? DecidedAtUtc,
            string? Reason)
        {
            public bool IsResolved =>
                Status is EarthworksDecisionStatus.Included or EarthworksDecisionStatus.Excluded;
            public bool IncludesEarthworks => Status == EarthworksDecisionStatus.Included;
        }

        public static EarthworksDecisionSummary GetEarthworksDecision(ProjectProfile? profile)
        {
            if (profile == null)
            {
                return new EarthworksDecisionSummary(
                    EarthworksDecisionStatus.Unresolved,
                    "עבודות עפר: אין פרופיל פרויקט",
                    "לא ניתן להכריע היקף עבודות עפר ללא פרופיל פרויקט.",
                    null, null, null);
            }

            var decision = profile.Estimate.Earthworks;
            var approver = string.IsNullOrWhiteSpace(decision.DecidedBy)
                ? null : decision.DecidedBy.Trim();
            var reason = string.IsNullOrWhiteSpace(decision.Reason)
                ? null : decision.Reason.Trim();
            var at = decision.DecidedAtUtc;

            if (decision.Requested == null)
            {
                var partial = approver != null || at != null || reason != null;
                return new EarthworksDecisionSummary(
                    partial ? EarthworksDecisionStatus.Invalid : EarthworksDecisionStatus.Unresolved,
                    partial
                        ? "עבודות עפר: החלטה חלקית/לא תקינה — הייצוא חסום"
                        : "עבודות עפר טרם נבדקו — לא אפס ולא הוחרגו",
                    "ניתן למדוד כמויות כלליות כעת. לפני בניית אומדן יש להחליט אם עבודות עפר נכללות או מחוץ להיקפו.",
                    approver, at, reason);
            }

            if (approver == null || at == null || (decision.Requested == false && reason == null))
            {
                var missing = new List<string>();
                if (approver == null) missing.Add("מאשר");
                if (at == null) missing.Add("זמן UTC");
                if (decision.Requested == false && reason == null) missing.Add("סיבה הנדסית");
                return new EarthworksDecisionSummary(
                    EarthworksDecisionStatus.Invalid,
                    "עבודות עפר: החלטה לא תקינה — הייצוא חסום",
                    "החלטת עבודות העפר חסרה: " + string.Join(", ", missing) + ".",
                    approver, at, reason);
            }

            var utc = at.Value.Kind == DateTimeKind.Utc
                ? at.Value
                : at.Value.ToUniversalTime();
            if (decision.Requested == true)
            {
                return new EarthworksDecisionSummary(
                    EarthworksDecisionStatus.Included,
                    $"עבודות עפר: נכללות · אושר ע\"י {approver} · {utc:yyyy-MM-dd HH:mm} UTC",
                    "עבודות העפר נכללות; הייצוא עדיין מותנה בכיסוי חתכים ומקורות קרקע/תכנון מוכחים.",
                    approver, utc, reason);
            }

            return new EarthworksDecisionSummary(
                EarthworksDecisionStatus.Excluded,
                $"עבודות עפר: מחוץ להיקף · אושר ע\"י {approver} · {utc:yyyy-MM-dd HH:mm} UTC · סיבה: {reason}",
                "עבודות העפר הוחרגו במפורש עם סיבה הנדסית; אינן מוצגות כאפס.",
                approver, utc, reason);
        }

        /// <summary>
        /// Measurement and mapping review may run before these decisions. Neither
        /// a direct runtime BUILD nor EXPORT may turn that review into a final scope.
        /// This pure guard is tested without a Document or Civil runtime.
        /// </summary>
        internal static void RequireFinalEstimateScope(ProjectProfile profile)
        {
            RequireReviewedSourceScope(profile);
            if (!GetEarthworksDecision(profile).IsResolved)
                throw new InvalidOperationException(EstimateGuidedActionPolicy.EarthworksNotAssessed);
        }

        internal static void RequireReviewedSourceScope(ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!IsReviewedSourceScopeApproved(profile))
                throw new InvalidOperationException(
                    "המדידות זמינות לעיון; לפני חישוב תמחור נדרש אישור מפורש של היקף המקורות. האישור אינו מאשר מחירים או עבודות עפר.");
        }

        /// <summary>
        /// Persists an explicit include/out-of-scope decision. Exclusion requires an
        /// engineering reason. ProjectProfileWriter performs the atomic replace; this
        /// method restores the in-memory engineering fields if any write step fails.
        /// </summary>
        public ProjectProfileWriter.SaveResult SaveEarthworksDecision(
            ProjectProfile profile,
            bool includeEarthworks,
            string? reason,
            string approvedBy,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            var path = RequireProfileWriteTarget(targetPath);
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException(
                    "An approver is required for the earthworks scope decision.",
                    nameof(approvedBy));

            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            if (!includeEarthworks && normalizedReason == null)
                throw new ArgumentException(
                    "An engineering reason is required when earthworks are outside the estimate scope.",
                    nameof(reason));

            var earthworks = profile.Estimate.Earthworks;
            var oldRequested = earthworks.Requested;
            var oldDecidedBy = earthworks.DecidedBy;
            var oldDecidedAtUtc = earthworks.DecidedAtUtc;
            var oldReason = earthworks.Reason;

            earthworks.Requested = includeEarthworks;
            earthworks.DecidedBy = approvedBy.Trim();
            earthworks.DecidedAtUtc = DateTime.UtcNow;
            earthworks.Reason = includeEarthworks ? null : normalizedReason!;

            try
            {
                var summary = includeEarthworks
                    ? "earthworks scope approved: INCLUDED"
                    : "earthworks scope approved: OUT-OF-SCOPE; reason=" + OneLineForAudit(normalizedReason!);
                return ProjectProfileWriter.Save(
                    profile, path, summary, approvedBy,
                    expectedProfileState);
            }
            catch
            {
                earthworks.Requested = oldRequested;
                earthworks.DecidedBy = oldDecidedBy;
                earthworks.DecidedAtUtc = oldDecidedAtUtc;
                earthworks.Reason = oldReason;
                throw;
            }
        }

        /// <summary>
        /// Persists (or revokes) one audited not-a-construction-quantity decision.
        /// A bare rule key is deliberately insufficient authority: exclusion requires
        /// an engineering reason, named approver and UTC timestamp. The profile writer
        /// performs an atomic replace and this boundary rolls back both decision lists
        /// if validation, serialization or the filesystem write fails.
        /// </summary>
        public ProjectProfileWriter.SaveResult SaveIgnoredRuleDecision(
            ProjectProfile profile,
            ScanResult? scan,
            string ruleKey,
            bool excludeFromEstimate,
            string? reason,
            string approvedBy,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            var path = RequireProfileWriteTarget(targetPath);
            var key = ruleKey?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A quantity rule key is required.", nameof(ruleKey));
            var approver = approvedBy?.Trim();
            if (string.IsNullOrWhiteSpace(approver))
                throw new ArgumentException("An approver is required for a quantity exclusion decision.", nameof(approvedBy));
            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            if (excludeFromEstimate && normalizedReason == null)
                throw new ArgumentException(
                    "An engineering reason is required to exclude measured work.", nameof(reason));

            if (excludeFromEstimate)
            {
                if (scan == null)
                    throw new InvalidOperationException(
                        "A current quantity scan is required before measured work can be excluded.");
                if (!string.Equals(scan.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The quantity scan belongs to a different project profile.");
                var currentEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
                if (!string.Equals(scan.ProjectProfileEffectiveHash, currentEffectiveHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The project profile changed after the quantity scan; run a fresh scan before excluding measured work.");
                if (scan.Records.Any(record => !string.Equals(
                        record.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "The quantity scan contains records from a different project profile.");

                var exactGroup = scan.Records
                    .Where(record => string.Equals(
                        record.Classification.RuleKey, key, StringComparison.Ordinal))
                    .ToList();
                if (exactGroup.Count == 0)
                    throw new InvalidOperationException(
                        $"Quantity rule key '{key}' is not present as an exact group in the current scan.");
                if (exactGroup.Any(record =>
                        !QuantitySignificance.IsValidMeasurement(record.Measurement.RawValue)))
                    throw new InvalidOperationException(
                        $"Quantity rule key '{key}' contains an invalid raw measurement and cannot be excluded.");
            }

            var decisions = profile.Estimate.IgnoredRuleDecisions;
            var legacyKeys = profile.Estimate.IgnoredRuleKeys;
            var oldDecisions = decisions.Select(CloneIgnoredRuleDecision).ToList();
            var oldLegacyKeys = legacyKeys.ToList();

            try
            {
                decisions.RemoveAll(d => string.Equals(
                    d.RuleKey?.Trim(), key, StringComparison.Ordinal));
                legacyKeys.RemoveAll(k => string.Equals(k?.Trim(), key, StringComparison.Ordinal));
                if (excludeFromEstimate)
                {
                    decisions.Add(new ProjectProfile.EstimateProfile.IgnoredRuleDecision
                    {
                        RuleKey = key,
                        Reason = normalizedReason,
                        ApprovedBy = approver,
                        ApprovedAtUtc = DateTime.UtcNow,
                    });
                }

                var summary = excludeFromEstimate
                    ? $"estimate quantity excluded: {key}; reason={OneLineForAudit(normalizedReason!)}"
                    : $"estimate quantity exclusion revoked: {key}";
                return ProjectProfileWriter.Save(
                    profile, path, summary, approver,
                    expectedProfileState);
            }
            catch
            {
                decisions.Clear();
                decisions.AddRange(oldDecisions);
                legacyKeys.Clear();
                legacyKeys.AddRange(oldLegacyKeys);
                throw;
            }
        }

        /// <summary>
        /// One exact, classifier-backed decision in an atomic noise-exclusion batch.
        /// The reason is part of the request on purpose: the service verifies that the
        /// caller reviewed the current classifier verdict instead of accepting a bare
        /// key or a blanket category name.
        /// </summary>
        public sealed record IgnoredRuleDecisionRequest(string RuleKey, string Reason);

        /// <summary>
        /// Atomically records a named engineer's decisions for groups that the current
        /// scan proves are only station geometry or auxiliary drawing furniture.
        /// Existing utilities, implausible magnitudes and ordinary quantities are never
        /// eligible for this batch route. They remain individual engineering decisions.
        ///
        /// Every request is validated before profile mutation; all decisions share one
        /// UTC timestamp and one durable profile write. A validation or write failure
        /// leaves both the audited and legacy ignore lists exactly as they were.
        /// </summary>
        public ProjectProfileWriter.SaveResult SaveIgnoredRuleDecisions(
            ProjectProfile profile,
            ScanResult scan,
            IReadOnlyList<IgnoredRuleDecisionRequest> requests,
            string approvedBy,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(requests);
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            var path = RequireProfileWriteTarget(targetPath);
            if (requests.Count == 0)
                throw new ArgumentException("No quantity relevance decisions to save.", nameof(requests));

            var approver = approvedBy?.Trim();
            if (string.IsNullOrWhiteSpace(approver))
                throw new ArgumentException(
                    "An approver is required for quantity relevance decisions.", nameof(approvedBy));
            if (!string.Equals(scan.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The quantity scan belongs to a different project profile.");
            if (!IsReviewedSourceScopeApproved(profile) || !scan.DiscoveryMode)
                throw new InvalidOperationException(
                    "Atomic drawing-noise exclusion requires an approved discover-all host+XREF scope and a discovery-mode scan.");

            var currentEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
            if (!string.Equals(scan.ProjectProfileEffectiveHash, currentEffectiveHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The project profile changed after the quantity scan; run or rebase the scan before approving relevance decisions.");
            if (scan.Records.Any(record => !string.Equals(
                    record.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "The quantity scan contains records from a different project profile.");

            var normalized = new List<IgnoredRuleDecisionRequest>(requests.Count);
            foreach (var request in requests)
            {
                if (request == null)
                    throw new InvalidOperationException(
                        "Every quantity relevance decision must contain an exact key and reason.");
                var key = request.RuleKey?.Trim();
                if (string.IsNullOrWhiteSpace(key) ||
                    !string.Equals(request.RuleKey, key, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Every quantity relevance decision must contain an exact, unpadded rule key.");
                var reason = request.Reason?.Trim();
                if (string.IsNullOrWhiteSpace(reason) ||
                    !string.Equals(request.Reason, reason, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Quantity relevance decision '{key}' must contain the exact, unpadded classifier reason.");
                normalized.Add(new IgnoredRuleDecisionRequest(key, reason));
            }

            var duplicateKeys = normalized
                .GroupBy(request => request.RuleKey, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateKeys.Count > 0)
                throw new InvalidOperationException(
                    "The quantity relevance batch contains duplicate exact rule keys: " +
                    string.Join(", ", duplicateKeys));

            var groups = scan.Records
                .GroupBy(record => record.Classification.RuleKey ?? "(none)", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            // Validate the complete batch before mutating even one profile decision.
            foreach (var request in normalized)
            {
                if (!groups.TryGetValue(request.RuleKey, out var group))
                    throw new InvalidOperationException(
                        $"Quantity rule key '{request.RuleKey}' is not present in the current scan.");
                if (group.Any(record =>
                        string.IsNullOrWhiteSpace(record.Classification.RuleKey)))
                    throw new InvalidOperationException(
                        "A quantity group without an exact classification rule key cannot be bulk-excluded.");
                if (ApprovedIgnoredRuleDecision(profile, request.RuleKey) != null)
                    throw new InvalidOperationException(
                        $"Quantity rule key '{request.RuleKey}' already has an audited relevance decision.");
                if (group.Any(record =>
                        !string.IsNullOrWhiteSpace(record.Classification.CandidateCatalogCode) ||
                        HasApprovedCatalogMapping(profile, record)))
                    throw new InvalidOperationException(
                        $"Quantity rule key '{request.RuleKey}' has an approved catalog mapping and cannot be bulk-excluded.");
                if (group.Any(record =>
                        !QuantitySignificance.IsValidMeasurement(record.Measurement.RawValue)))
                    throw new InvalidOperationException(
                        $"Quantity rule key '{request.RuleKey}' contains an invalid raw measurement and cannot be excluded.");

                var first = group[0];
                var verdict = QuantitySignificance.Classify(new QuantitySignificance.Group(
                    request.RuleKey,
                    first.Source.Layer,
                    first.Measurement.Unit,
                    group.Sum(record => record.Measurement.RawValue),
                    group.Count));
                if (verdict.Kind is not QuantitySignificance.Kind.StationGeometry and
                    not QuantitySignificance.Kind.Auxiliary)
                    throw new InvalidOperationException(
                        $"Quantity rule key '{request.RuleKey}' is {verdict.Kind}; only StationGeometry or Auxiliary groups are eligible for atomic noise exclusion.");
                if (!string.Equals(request.Reason, verdict.Reason, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Quantity rule key '{request.RuleKey}' reason does not equal the current classifier evidence.");
            }

            var decisions = profile.Estimate.IgnoredRuleDecisions;
            var legacyKeys = profile.Estimate.IgnoredRuleKeys;
            var oldDecisions = decisions.Select(CloneIgnoredRuleDecision).ToList();
            var oldLegacyKeys = legacyKeys.ToList();
            var approvedAtUtc = DateTime.UtcNow;

            try
            {
                var keys = normalized.Select(request => request.RuleKey)
                    .ToHashSet(StringComparer.Ordinal);
                decisions.RemoveAll(decision =>
                    !string.IsNullOrWhiteSpace(decision.RuleKey) &&
                    keys.Contains(decision.RuleKey.Trim()));
                legacyKeys.RemoveAll(key =>
                    !string.IsNullOrWhiteSpace(key) && keys.Contains(key.Trim()));
                decisions.AddRange(normalized.Select(request =>
                    new ProjectProfile.EstimateProfile.IgnoredRuleDecision
                    {
                        RuleKey = request.RuleKey,
                        Reason = request.Reason,
                        ApprovedBy = approver,
                        ApprovedAtUtc = approvedAtUtc,
                    }));

                var orderedKeys = normalized.Select(request => request.RuleKey)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList();
                var keyDigest = ArtifactHash.Sha256OfText(string.Join("\n", orderedKeys));
                var summary = $"estimate noise exclusions approved: count={orderedKeys.Count}; " +
                              $"keys_sha256={keyDigest}; keys={OneLineForAudit(string.Join(", ", orderedKeys))}";
                return ProjectProfileWriter.Save(
                    profile, path, summary, approver,
                    expectedProfileState);
            }
            catch
            {
                decisions.Clear();
                decisions.AddRange(oldDecisions);
                legacyKeys.Clear();
                legacyKeys.AddRange(oldLegacyKeys);
                throw;
            }
        }

        public static ProjectProfile.EstimateProfile.IgnoredRuleDecision?
            ApprovedIgnoredRuleDecision(ProjectProfile? profile, string ruleKey)
        {
            if (profile == null || string.IsNullOrWhiteSpace(ruleKey)) return null;
            return IgnoredRulePolicy.ApprovedDecisions(profile)
                .TryGetValue(ruleKey.Trim(), out var decision)
                    ? decision
                    : null;
        }

        private static ProjectProfile.EstimateProfile.IgnoredRuleDecision
            CloneIgnoredRuleDecision(ProjectProfile.EstimateProfile.IgnoredRuleDecision decision) =>
            new()
            {
                RuleKey = decision.RuleKey,
                Reason = decision.Reason,
                ApprovedBy = decision.ApprovedBy,
                ApprovedAtUtc = decision.ApprovedAtUtc,
            };

        private static string OneLineForAudit(string value)
        {
            var oneLine = string.Join(" ", value
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
            return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "…";
        }

        public sealed record QuantityRuleApprovalSummary(int Approved, int Unapproved);

        /// <summary>Separates durable engineer approvals from shipped/default suggestions.</summary>
        public static QuantityRuleApprovalSummary QuantityRuleApprovals(ProjectProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var approved = profile.Estimate.QuantitySources.Rules.Count(rule =>
                CivilQuantityExtractionService.IsExplicitlyApproved(profile, rule));
            return new QuantityRuleApprovalSummary(
                approved,
                profile.Estimate.QuantitySources.Rules.Count - approved);
        }

        /// <summary>
        /// UI defense-in-depth: a record's candidate code is displayed as approved only
        /// when the active profile still contains the matching named+timestamped rule.
        /// This prevents a stale scan or legacy candidate from looking approved.
        /// </summary>
        internal static string? ApprovedCatalogCodeForDisplay(
            ProjectProfile? profile,
            string ruleKey,
            string layer,
            string measurementKind,
            string? classifiedCode)
        {
            if (profile == null || string.IsNullOrWhiteSpace(classifiedCode)) return null;
            var approved = CivilQuantityExtractionService.FindApprovedRule(
                profile, ruleKey, layer, measurementKind);
            return approved != null && string.Equals(
                    approved.CandidateCatalogCode, classifiedCode,
                    StringComparison.OrdinalIgnoreCase)
                ? classifiedCode
                : null;
        }

        /// <summary>
        /// Candidate/default catalog codes are suggestions, not completed mappings.
        /// This predicate is the source of truth for the palette's pending totals.
        /// </summary>
        internal static bool HasApprovedCatalogMapping(
            ProjectProfile? profile, NeutralQuantityRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return ApprovedCatalogCodeForDisplay(
                       profile,
                       record.Classification.RuleKey ?? "(none)",
                       record.Source.Layer ?? string.Empty,
                       record.Measurement.Kind,
                       record.Classification.CandidateCatalogCode) != null;
        }

        /// <summary>
        /// Persists the engineer's explicit choice to scan every supported host entity
        /// and every resolved nested XREF. ProjectProfileWriter supplies the approval
        /// timestamp, approver, version bump, source trail and previous-file backup.
        /// </summary>
        /// <param name="targetPath">
        /// The exact source-bound write target resolved by ActiveProjectProfileService.
        /// Tests pass a temporary path and never write to an engineer's active profile.
        /// </param>
        public ProjectProfileWriter.SaveResult ApproveCompleteDiscoveryScope(
            ProjectProfile profile,
            string approvedBy,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState,
            EstimateSourceSelection? sourceSelection = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            var path = RequireProfileWriteTarget(targetPath);
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("An approver is required to approve quantity scope.", nameof(approvedBy));

            var sources = profile.Estimate.QuantitySources;
            var oldSourceScope = sources.SourceScopePolicy;
            var oldXrefPolicy = sources.XrefPolicy;
            var oldSelection = profile.Estimate.SourceSelection;
            var oldVersion = profile.Provenance.Version;
            var oldApprovedBy = profile.Provenance.ApprovedBy;
            var oldApprovedAtUtc = profile.Provenance.ApprovedAtUtc;
            var oldSource = profile.Provenance.Source;

            if (sourceSelection != null && EstimateSourceSelectionPolicy.ValidationProblems(sourceSelection).Count != 0)
                throw new InvalidOperationException("בחירת המקורות אינה מאושרת או אינה שלמה");
            if (sourceSelection != null && !string.Equals(sourceSelection.ApprovedBy, approvedBy.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("שם מאשר בחירת המקורות אינו תואם למאשר השמירה");
            profile.Estimate.SourceSelection = sourceSelection;
            sources.SourceScopePolicy = sourceSelection == null ? EstimatePreflightPolicy.DiscoverAllSourceScopePolicy : EstimatePreflightPolicy.ReviewedSourcesPolicy;
            sources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;

            try
            {
                return ProjectProfileWriter.Save(
                    profile,
                    path,
                    sourceSelection == null ? "estimate source scope approved: discover-all + recursive host+XREF (verified provenance)"
                        : EstimateSourceSelectionPolicy.ScopeSummary(sourceSelection),
                    approvedBy,
                    expectedProfileState);
            }
            catch
            {
                // A failed disk write must not leave the in-memory profile looking
                // approved when no durable approval exists.
                sources.SourceScopePolicy = oldSourceScope;
                profile.Estimate.SourceSelection = oldSelection;
                sources.XrefPolicy = oldXrefPolicy;
                profile.Provenance.Version = oldVersion;
                profile.Provenance.ApprovedBy = oldApprovedBy;
                profile.Provenance.ApprovedAtUtc = oldApprovedAtUtc;
                profile.Provenance.Source = oldSource;
                throw;
            }
        }

        /// <param name="targetPath">
        /// Exact source-bound destination. Tests MUST pass a temp path: a test that
        /// wrote to the live profile and then "cleaned up" deleted an engineer's
        /// configuration (2026-08-19).
        /// </param>
        public ProjectProfileWriter.SaveResult SaveApprovedMappings(
            ProjectProfile profile,
            CatalogSnapshot snapshot,
            IReadOnlyList<MappingApproval> approvals,
            string approvedBy,
            string targetPath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState)
        {
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            var path = RequireProfileWriteTarget(targetPath);
            if (approvals.Count == 0)
                throw new ArgumentException("No mappings to save.", nameof(approvals));
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("An approver is required.", nameof(approvedBy));
            if (approvals.Any(a => string.IsNullOrWhiteSpace(a.RuleKey)))
                throw new InvalidOperationException("Every mapping approval must have a non-empty rule key.");
            var duplicateKeys = approvals
                .GroupBy(a => a.RuleKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicateKeys.Count > 0)
                throw new InvalidOperationException(
                    "The mapping approval batch contains duplicate rule keys: " +
                    string.Join(", ", duplicateKeys));

            if (!CatalogIdentity.TryGetActiveProfileIdentity(profile, out var activeIdentity,
                    out var identityErrors) || activeIdentity == null)
                throw new InvalidOperationException(
                    "Active price-book identity is incomplete or inconsistent: " +
                    string.Join("; ", identityErrors));
            if (!CatalogIdentity.SnapshotMatches(activeIdentity, snapshot))
                throw new InvalidOperationException(
                    $"Snapshot '{snapshot.SnapshotId}'/{snapshot.FileHash} does not equal the active " +
                    $"registered price book '{activeIdentity.SnapshotId}'/{activeIdentity.FileHash}.");

            // Validate the complete batch before mutating even one rule. A later bad
            // code/unit must not leave the earlier mappings approved in memory.
            foreach (var a in approvals)
            {
                if (ApprovedIgnoredRuleDecision(profile, a.RuleKey) != null)
                    throw new InvalidOperationException(
                        $"Quantity rule key '{a.RuleKey}' is excluded from the estimate. " +
                        "Return it to the estimate before approving a catalog mapping.");
                if (!snapshot.Items.TryGetValue(a.CatalogCode, out var item))
                    throw new InvalidOperationException(
                        $"Catalog code '{a.CatalogCode}' does not exist in snapshot {snapshot.SnapshotId}");

                var measured = Units.Parse(a.MeasuredUnit);
                if (!measured.SameUnit(item.Unit))
                    throw new InvalidOperationException(
                        $"Unit mismatch for '{a.CatalogCode}': measured {measured.Canonical}, " +
                        $"catalog {item.Unit.Canonical}. A dimensional conversion needs an explicit verified rule.");
            }

            var rules = profile.Estimate.QuantitySources.Rules;
            var oldRules = rules.Select(CloneQuantityRule).ToList();
            var approvedAtUtc = DateTime.UtcNow;
            try
            {
                foreach (var a in approvals)
                {
                    var existing = rules.FirstOrDefault(r => r.RuleKey == a.RuleKey);
                    if (existing != null)
                    {
                        existing.CandidateCatalogCode = a.CatalogCode;
                        existing.ExpectedUnit = a.MeasuredUnit;
                        existing.LayerPattern = a.LayerPattern;
                        existing.EntityType = a.EntityType;
                        existing.MeasurementKind = a.MeasurementKind;
                        existing.ApprovedBy = approvedBy.Trim();
                        existing.ApprovedAtUtc = approvedAtUtc;
                        existing.ApprovedCatalogId = activeIdentity.SnapshotId;
                        existing.ApprovedCatalogHash = activeIdentity.FileHash;
                        existing.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(
                            snapshot.Items[a.CatalogCode]);
                    }
                    else
                    {
                        rules.Add(
                            new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                            {
                                RuleKey = a.RuleKey,
                                LayerPattern = a.LayerPattern,
                                EntityType = a.EntityType,
                                MeasurementKind = a.MeasurementKind,
                                CandidateCatalogCode = a.CatalogCode,
                                ExpectedUnit = a.MeasuredUnit,
                                ApprovedBy = approvedBy.Trim(),
                                ApprovedAtUtc = approvedAtUtc,
                                ApprovedCatalogId = activeIdentity.SnapshotId,
                                ApprovedCatalogHash = activeIdentity.FileHash,
                                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(
                                    snapshot.Items[a.CatalogCode]),
                                Notes = $"approved from discovery scan by {approvedBy}",
                            });
                    }
                }

                var summary = "estimate mappings approved: " +
                    string.Join(", ", approvals.Select(a => $"{a.RuleKey}->{a.CatalogCode}"));
                return ProjectProfileWriter.Save(profile, path, summary, approvedBy,
                    expectedProfileState,
                    new Dictionary<string, string> { [snapshot.SnapshotId] = snapshot.FileHash });
            }
            catch
            {
                rules.Clear();
                rules.AddRange(oldRules);
                throw;
            }
        }

        /// <summary>
        /// Persists one closed-polyline interpretation and the audited exclusion of
        /// its exact area/perimeter sibling in the same atomic profile write.  The
        /// sibling relationship is proven from the immutable source-handle sets in
        /// the active scan; a same-layer guess is never sufficient authority.
        /// </summary>
        public sealed record ClosedPolylineAlternativeExclusion(
            string SelectedRuleKey, string AlternativeRuleKey);

        public ProjectProfileWriter.SaveResult
            SaveApprovedClosedPolylineMapping(
                ProjectProfile profile,
                CatalogSnapshot snapshot,
                ScanResult scan,
                MappingApproval approval,
                string alternativeRuleKey,
                string approvedBy,
                string targetPath,
                ProjectProfileWriter.ExpectedProfileState expectedProfileState) =>
            SaveApprovedMappingsWithClosedPolylineExclusions(
                profile, snapshot, scan, new[] { approval },
                new[]
                {
                    new ClosedPolylineAlternativeExclusion(
                        approval.RuleKey, alternativeRuleKey),
                },
                approvedBy, targetPath, expectedProfileState);

        /// <summary>
        /// Batch form used by the direct command: ordinary mappings and every exact
        /// closed-polyline choice are validated first, then all selected mappings and
        /// sibling exclusions are persisted by one profile replace.
        /// </summary>
        public ProjectProfileWriter.SaveResult
            SaveApprovedMappingsWithClosedPolylineExclusions(
                ProjectProfile profile,
                CatalogSnapshot snapshot,
                ScanResult scan,
                IReadOnlyList<MappingApproval> approvals,
                IReadOnlyList<ClosedPolylineAlternativeExclusion> exclusions,
                string approvedBy,
                string targetPath,
                ProjectProfileWriter.ExpectedProfileState expectedProfileState)
            => SaveMappingsWithClosedPolylineExclusionsCore(profile, snapshot, scan,
                approvals, exclusions, approvedBy, targetPath, expectedProfileState,
                allowReplacingMappedAlternative: false);

        private ProjectProfileWriter.SaveResult SaveMappingsWithClosedPolylineExclusionsCore(
                ProjectProfile profile,
                CatalogSnapshot snapshot,
                ScanResult scan,
                IReadOnlyList<MappingApproval> approvals,
                IReadOnlyList<ClosedPolylineAlternativeExclusion> exclusions,
                string approvedBy,
                string targetPath,
                ProjectProfileWriter.ExpectedProfileState expectedProfileState,
                bool allowReplacingMappedAlternative)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(approvals);
            ArgumentNullException.ThrowIfNull(exclusions);
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            RequireProfileWriteTarget(targetPath);
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("An approver is required.", nameof(approvedBy));
            if (!string.Equals(scan.ProjectProfileId, profile.ProfileId,
                    StringComparison.Ordinal) ||
                !string.Equals(scan.ProjectProfileEffectiveHash,
                    EstimateTraceIdentity.EffectiveProfileHash(profile),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The closed-polyline decisions do not belong to the current project-profile evidence.");

            var duplicateSelected = exclusions
                .GroupBy(exclusion => exclusion.SelectedRuleKey, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateSelected != null)
                throw new InvalidOperationException(
                    $"Closed-polyline rule '{duplicateSelected.Key}' has more than one sibling exclusion.");
            var exactPairs = ClosedPolylineAlternativePolicy.FindUnambiguousExactPairs(scan.Records);
            var validated = new List<(MappingApproval Approval, string Alternative,
                string ApprovedKind, string AlternativeKind, bool AlreadyIgnored)>();

            foreach (var exclusion in exclusions)
            {
                var approval = approvals.SingleOrDefault(candidate => string.Equals(
                    candidate.RuleKey, exclusion.SelectedRuleKey, StringComparison.Ordinal));
                if (approval == null)
                    throw new InvalidOperationException(
                        $"Closed-polyline choice '{exclusion.SelectedRuleKey}' has no selected mapping.");
                var alternative = exclusion.AlternativeRuleKey?.Trim();
                if (string.IsNullOrWhiteSpace(alternative) ||
                    string.Equals(approval.RuleKey, alternative, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "A distinct closed-polyline alternative rule key is required.");
                if (approvals.Any(candidate => string.Equals(
                        candidate.RuleKey, alternative, StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "Both siblings of one closed polyline cannot be approved in the same mapping batch.");

                var pair = exactPairs.SingleOrDefault(candidate =>
                    (string.Equals(candidate.FirstRuleKey, approval.RuleKey,
                         StringComparison.Ordinal) &&
                     string.Equals(candidate.SecondRuleKey, alternative,
                         StringComparison.Ordinal)) ||
                    (string.Equals(candidate.SecondRuleKey, approval.RuleKey,
                         StringComparison.Ordinal) &&
                     string.Equals(candidate.FirstRuleKey, alternative,
                         StringComparison.Ordinal)));
                if (pair == null)
                    throw new InvalidOperationException(
                        "The requested area/perimeter exclusion is not backed by one exact closed-polyline source set.");

                var approvedKind = string.Equals(pair.FirstRuleKey, approval.RuleKey,
                    StringComparison.Ordinal) ? pair.FirstKind : pair.SecondKind;
                var alternativeKind = string.Equals(pair.FirstRuleKey, alternative,
                    StringComparison.Ordinal) ? pair.FirstKind : pair.SecondKind;
                if (!string.Equals(approvedKind, approval.MeasurementKind,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The selected mapping kind does not equal the measured closed-polyline alternative.");
                if (ApprovedIgnoredRuleDecision(profile, approval.RuleKey) != null)
                    throw new InvalidOperationException(
                        "Return the selected closed-polyline rule to the estimate before mapping it.");

                var alreadyIgnored = ApprovedIgnoredRuleDecision(profile, alternative) != null;
                var alternativeRecords = scan.Records.Where(record => string.Equals(
                    record.Classification.RuleKey, alternative, StringComparison.Ordinal)).ToList();
                if (!allowReplacingMappedAlternative && !alreadyIgnored && alternativeRecords.Any(record =>
                        HasApprovedCatalogMapping(profile, record)))
                    throw new InvalidOperationException(
                        "The sibling closed-polyline alternative is already mapped. Exclude one audited alternative before changing the selection.");
                validated.Add((approval, alternative, approvedKind,
                    alternativeKind, alreadyIgnored));
            }

            var decisions = profile.Estimate.IgnoredRuleDecisions;
            var legacyKeys = profile.Estimate.IgnoredRuleKeys;
            var oldDecisions = decisions.Select(CloneIgnoredRuleDecision).ToList();
            var oldLegacyKeys = legacyKeys.ToList();
            try
            {
                foreach (var choice in validated)
                {
                    legacyKeys.RemoveAll(key => string.Equals(
                        key?.Trim(), choice.Alternative, StringComparison.Ordinal));
                    if (choice.AlreadyIgnored) continue;
                    decisions.Add(new ProjectProfile.EstimateProfile.IgnoredRuleDecision
                    {
                        RuleKey = choice.Alternative,
                        Reason = $"נבחרה חלופת {choice.ApprovedKind} ({choice.Approval.RuleKey}) עבור אותם פוליליינים סגורים; " +
                                 $"חלופת {choice.AlternativeKind} היא מדידה חלופית ולא סעיף ביצוע נוסף.",
                        ApprovedBy = approvedBy.Trim(),
                        ApprovedAtUtc = DateTime.UtcNow,
                    });
                }

                // SaveApprovedMappings validates every catalog identity/unit and
                // performs the one durable atomic profile replace.
                return SaveApprovedMappings(
                    profile, snapshot, approvals, approvedBy, targetPath,
                    expectedProfileState);
            }
            catch
            {
                decisions.Clear();
                decisions.AddRange(oldDecisions);
                legacyKeys.Clear();
                legacyKeys.AddRange(oldLegacyKeys);
                throw;
            }
        }

        private static ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            CloneQuantityRule(
                ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule rule) =>
            new()
            {
                RuleKey = rule.RuleKey,
                LayerPattern = rule.LayerPattern,
                EntityType = rule.EntityType,
                MeasurementKind = rule.MeasurementKind,
                CandidateCatalogCode = rule.CandidateCatalogCode,
                ExpectedUnit = rule.ExpectedUnit,
                ApprovedBy = rule.ApprovedBy,
                ApprovedAtUtc = rule.ApprovedAtUtc,
                ApprovedCatalogId = rule.ApprovedCatalogId,
                ApprovedCatalogHash = rule.ApprovedCatalogHash,
                ApprovedCatalogItemFingerprint = rule.ApprovedCatalogItemFingerprint,
                Notes = rule.Notes,
            };

        /// <summary>
        /// Registers a price-book file with the project (any publisher: NTI, Dekel, own),
        /// stores it next to the profile, and optionally makes it the active book. The
        /// profile is saved through the same approval-only writer as mappings.
        /// </summary>
        public PriceBookRegistry.RegisterResult RegisterPriceBook(
            ProjectProfile profile, string sourceXlsx, string registeredBy,
            string targetProfilePath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState,
            string? id = null, string? publisher = null, string? edition = null,
            bool makeActive = false, string? expectedInspectionHash = null,
            PriceBookXlsxLoader.ColumnMapping? mapping = null)
        {
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            var profilePath = RequireProfileWriteTarget(targetProfilePath);
            var profileDir = Path.GetDirectoryName(profilePath)!;
            var before = CloneEstimateProfile(profile.Estimate);
            PriceBookRegistry.RegisterResult? result = null;
            try
            {
                PriceBookRegistry.EnsureLegacyEntry(profile);
                result = PriceBookRegistry.Register(
                    profile, profileDir, sourceXlsx, registeredBy,
                    id, publisher, edition, makeActive, expectedInspectionHash, mapping);

                var summary = $"price book registered: {result.Entry.Id} ({result.Entry.Publisher}, {result.Entry.ItemCount} items)" +
                              (result.Entry.Mapping is { } m
                                  ? $" read as sheet '{m.SheetName}' header {m.HeaderRow} code {m.CodeColumn} price {m.PriceColumn}"
                                  : "") +
                              (makeActive ? " and made ACTIVE" : "");
                ProjectProfileWriter.Save(profile, profilePath, summary, registeredBy,
                    expectedProfileState,
                    new Dictionary<string, string> { [result.Entry.Id!] = result.Entry.FileHash! });
                return result;
            }
            catch
            {
                profile.Estimate = before;
                // Catalog files are immutable and content-hash verified.  Never
                // delete a newly copied file after losing the profile CAS: another
                // concurrent winner may already have published a profile that points
                // at those exact bytes.  An unreferenced immutable file is a harmless
                // orphan; deleting a referenced one would corrupt the active price
                // book and monetary trace.
                throw;
            }
        }

        /// <summary>Switches the active price book. Explicit, named, logged in the profile.</summary>
        public void SetActivePriceBook(
            ProjectProfile profile, string id, string approvedBy, string targetProfilePath,
            ProjectProfileWriter.ExpectedProfileState expectedProfileState)
        {
            ArgumentNullException.ThrowIfNull(expectedProfileState);
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("An approver is required to switch the price book.", nameof(approvedBy));
            var profilePath = RequireProfileWriteTarget(targetProfilePath);
            var profileDir = Path.GetDirectoryName(profilePath)!;
            var before = CloneEstimateProfile(profile.Estimate);
            try
            {
                PriceBookRegistry.EnsureLegacyEntry(profile);
                PriceBookRegistry.MakeActive(profile, id, profileDir);
                ProjectProfileWriter.Save(
                    profile, profilePath, $"active price book -> {id}", approvedBy,
                    expectedProfileState);
            }
            catch
            {
                profile.Estimate = before;
                throw;
            }
        }

        private static ProjectProfile.EstimateProfile CloneEstimateProfile(
            ProjectProfile.EstimateProfile source) =>
            JsonSerializer.Deserialize<ProjectProfile.EstimateProfile>(
                JsonSerializer.Serialize(source))
            ?? throw new InvalidOperationException("Could not snapshot estimate profile state.");

        private static string RequireProfileWriteTarget(string? targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) ||
                !Path.IsPathFullyQualified(targetPath))
                throw new ArgumentException(
                    "An authoritative profile write target is required.", nameof(targetPath));
            return targetPath;
        }

        private static string? LocateCatalog(string profileId, string fileName,
            string? expectedHash = null, string? profileSource = null)
        {
            // A selected file is authoritative even when its id is shared with a
            // different runtime profile. Failure must not switch to that profile
            // or a bundled fixture. Omission alone retains legacy ID resolution.
            if (profileSource != null)
                return EstimateCatalogPathResolver.ResolveFromProfile(
                    profileSource, fileName, expectedHash);

            // Strict registry identity means the configured/registered file itself is
            // resolved. A same-hash file under a different name is not silently
            // substituted; registration is the explicit migration boundary.
            var configuredName = Path.GetFileName(fileName);

            bool HashOk(string f)
            {
                if (string.IsNullOrEmpty(expectedHash)) return true;
                try { return string.Equals(ArtifactHash.Sha256OfFile(f), expectedHash, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }

            if (Path.IsPathRooted(fileName) && File.Exists(fileName) && HashOk(fileName))
                return Path.GetFullPath(fileName);

            var profilePath = ProfileLocator.Resolve(profileId);
            if (profilePath != null)
            {
                var near = Path.Combine(Path.GetDirectoryName(profilePath)!, fileName);
                if (File.Exists(near) && HashOk(near)) return near;
            }

            var dllDir = Path.GetDirectoryName(typeof(EstimateWorkflowService).Assembly.Location);
            if (dllDir != null)
            {
                var candidates = new[]
                {
                    Path.Combine(dllDir, "fixtures", configuredName),
                    Path.GetFullPath(Path.Combine(dllDir, "..", "..", "..", "..",
                        "fixtures", "civil-delivery", "estimate", configuredName)),
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c) && HashOk(c)) return c;
                }
            }
            return null;
        }
    }
}
