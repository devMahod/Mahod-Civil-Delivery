using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Read-only PLAN (plan §7.1): CL discovery → alignment resolution → geometry →
    /// planned sources/styles → rerun action. Never mutates the drawing; artifacts
    /// are written outside the DWG by the caller.
    /// </summary>
    public sealed class SectionPlanService
    {
        public const string ToolVersion = "civil-delivery/1.4.2";

        private readonly ClInstructionReader _reader = new();
        private readonly AlignmentCandidateResolver _resolver = new();

        /// <summary>
        /// Above this many CL candidates in discovery mode, PLAN reports instead of
        /// intersecting. Generous for any real CL instruction set (6422 has 16), tiny
        /// against a full model's polyline count.
        /// </summary>
        public const int DiscoveryCandidateBound = 500;

        public SectionPlan Plan(
            Database db,
            Transaction tr,
            CivilDocument civilDoc,
            ProjectProfile profile,
            string? profileHash,
            string? sourceDrawingIdentity = null,
            string? runId = null,
            StageLog? log = null)
        {
            var plan = new SectionPlan
            {
                RunId = runId ?? RunManifest.NewRunId("sections", "plan"),
                ProjectProfileId = profile.ProfileId,
                ProjectProfileHash = profileHash,
                SourceDrawing = sourceDrawingIdentity ?? db.Filename,
            };

            // Sections are authored in metres. Capture the host evidence before any
            // expensive Civil scan and fail closed instead of silently scaling.
            SectionInputIntegrityService.CapturePlanDatabaseState(db, plan, profile);
            var unitFinding = SectionInputIntegrityService.ValidatePlanUnits(
                db, profile.ProfileId, profile);
            if (unitFinding != null)
            {
                plan.Findings.Add(unitFinding);
                plan.Status = DeliveryStatus.Blocked;
                return plan;
            }

            var annotationInventory =
                SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db);
            if (!annotationInventory.IsValid)
            {
                plan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.AnnotationInventoryConflict,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "מלאי הערות החתכים אינו תואם לרישום הבעלות — PLAN חסום",
                    Message = $"layer={annotationInventory.LayerEntityCount}; " +
                              $"registered={annotationInventory.RegisteredCount}; " +
                              string.Join(" | ", annotationInventory.Problems),
                    RecommendedAction =
                        "אין למחוק או לאמץ ישויות אוטומטית. יש לבדוק את השכבות " +
                        $"{SectionAnnotationResourceContracts.AnnotationLayerName} ו-{SectionAnnotationResourceContracts.LegacyAnnotationLayerName} (ישנה) " +
                        "ואת רישום החתכים בשרטוט, או לשחזר את השרטוט מעותק נקי, ואז להריץ PLAN מחדש.",
                    ProjectProfileId = profile.ProfileId,
                });
            }

            // 1. CL discovery (read-only).
            log?.Begin("plan.read_cl", $"layer_patterns={profile.Sections.Cl.LayerPatterns.Count}");
            var read = _reader.Read(db, tr, profile, log);
            log?.End("plan.read_cl", $"cl_records={read.Records.Count}");
            plan.Findings.AddRange(read.Findings);
            foreach (var source in read.ExternalSources)
                ClInstructionReader.AddExternalEvidence(
                    plan.ExternalSources, source, plan.Findings);
            BlockDuplicateSourceRecordIds(read.Records, plan);
            if (read.Records.Count == 0)
            {
                plan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ClSourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "לא נמצאה אף הוראת CL — אין אצוות חתכים שניתן לאשר",
                    RecommendedAction = "יש להגדיר שכבות/קובצי CL נכונים ולהריץ PLAN מחדש.",
                    ProjectProfileId = profile.ProfileId,
                });
            }

            // Discovery mode (no CL layer configured) exists so the engineer can SEE
            // what is there and choose. It must never try to plan every polyline in a
            // real model: 17,557 candidates x 22 alignments froze Civil for 13 minutes
            // on 6422. Above the bound, report the layer histogram and route to Setup.
            if (profile.Sections.Cl.LayerPatterns.Count == 0 &&
                read.Records.Count > DiscoveryCandidateBound)
            {
                var byLayer = read.Records
                    .GroupBy(r => r.SourceLayer ?? "(none)", StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Take(12)
                    .Select(g => $"{g.Key}={g.Count()}");
                plan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ClDegenerate,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = $"לא הוגדרה שכבת CL ובשרטוט יש {read.Records.Count:N0} קווים מועמדים — " +
                            $"יותר מדי כדי לתכנן בלי הגדרה (גבול {DiscoveryCandidateBound:N0})",
                    Message = "Candidates by layer: " + string.Join(", ", byLayer),
                    RecommendedAction =
                        "Run Setup (הגדרת פרויקט) and choose the CL layer / CL drawing, then PLAN again. " +
                        "Nothing was planned.",
                });
                // This is a global fail-closed discovery boundary. Calling it merely
                // "review" would let an alternate caller mistake the empty record
                // batch for an approvable plan.
                plan.Status = DeliveryStatus.Blocked;
                SectionInputIntegrityService.CapturePlanDatabaseState(db, plan, profile);
                log?.Info($"plan aborted: discovery mode with {read.Records.Count} candidates > bound {DiscoveryCandidateBound}");
                return plan;
            }

            // 2. Existing tool-owned objects, for idempotent rerun decisions.
            log?.Begin("plan.scan_owned_objects");
            var owned = ScanOwnedObjects(tr, civilDoc);
            log?.End("plan.scan_owned_objects",
                $"owned={owned.ObjectCount}; unreadable_alignments={owned.UnreadableAlignmentCount}; " +
                $"global_readable={owned.GlobalReadable}");

            // SEC-02 inventory is deliberately separate from tool ownership.  It is
            // read-only and contains only foreign SampleLine + SectionView pairs; PLAN
            // may select one, but neither PLAN nor APPLY ever adopts its ownership.
            log?.Begin("plan.scan_manual_sections");
            var manualSections = ScanManualSectionCandidates(tr, civilDoc);
            log?.End("plan.scan_manual_sections", $"manual_views={manualSections.Count}");

            // A common style across every readable manual donor is defensible office
            // evidence even when more than one manual section exists.  Disagreement
            // remains ambiguous and falls back to the managed clean style.
            var manualStyleDonor = CommonManualSectionViewStyle(manualSections);

            // 3. Available sampled sources in this document.
            log?.Begin("plan.list_surfaces");
            var surfaces = ListSurfaces(tr, civilDoc);
            var surfacePairAlignments = new List<SectionSourceSelectionLogic.Identity>();
            if (profile.Sections.Sources.SurfacePairs.Count > 0)
                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    var alignment = (CivilDb.Alignment)tr.GetObject(alignmentId, OpenMode.ForRead);
                    surfacePairAlignments.Add(new(alignment.Name, alignment.Handle.ToString()));
                }
            log?.End("plan.list_surfaces", $"n={surfaces.Count}");

            log?.Begin("plan.list_corridors");
            var corridors = ListCorridors(tr, civilDoc);
            log?.End("plan.list_corridors", $"n={corridors.Count}");

            log?.Begin("plan.list_pipe_networks");
            var pipeNetworks = ListPipeNetworks(tr, civilDoc);
            log?.End("plan.list_pipe_networks", $"n={pipeNetworks.Count}");

            // Utilities are discovered independently of configuration so a section can
            // never silently omit an existing system (directive §16).
            log?.Begin("plan.discover_utilities");
            var utilities = DiscoverUtilities(tr, civilDoc, db);
            log?.End("plan.discover_utilities", $"n={utilities.Count}");

            // Utilities that live as XREF/host polylines (UT-3D on 6422) — the ones the
            // "אין מערכות בשרטוט" answer wrongly ignored. Collected once, matched per CL.
            var projectable = profile.Sections.Projection.Enabled
                ? SectionGeometryCollector.Collect(tr, db, profile, log)
                : new SectionGeometryCollector.CollectResult(new(), new(), new(), new());
            // Projection geometry failures are scoped to the final CL segments
            // below. Traversal/source failures without bounds remain global.
            foreach (var source in projectable.ExternalSources)
                ClInstructionReader.AddExternalEvidence(
                    plan.ExternalSources, source, plan.Findings);

            // Direction is a per-strip engineering input, not a decoration choice.
            // Collect the complete transformed arrow inventory once and resolve each
            // lane against it below. Missing/conflicting evidence remains visible as
            // a manual-decision blocker; signed offset is never consulted.
            var trafficArrows = SectionTrafficArrowCollector.Collect(tr, db, log);
            plan.Findings.AddRange(trafficArrows.Findings);
            foreach (var source in trafficArrows.ExternalSources)
                ClInstructionReader.AddExternalEvidence(
                    plan.ExternalSources, source, plan.Findings);

            // b7: record id → exact topology findings proven harmless by a local cut proof.
            var locallyProvenByRecord = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            int index = 0;
            foreach (var cl in read.Records)
            {
                index++;
                var record = new SectionPlanRecord { RecordId = cl.RecordId, Cl = cl,
                    SpanDecisionHostDrawingPath = plan.SourceDrawing };
                plan.Records.Add(record);

                // CL-level findings (invalid transform, multi-segment…) flow through.
                record.Findings.AddRange(cl.Findings);
                if (cl.Status is DeliveryStatus.Blocked or DeliveryStatus.Failed or DeliveryStatus.ReviewRequired)
                {
                    record.Status = cl.Status;
                    SectionPlanLogic.TryApplyExplicitExclusion(record, profile);
                    continue;
                }

                log?.Begin("plan.find_crossings", $"{index}/{read.Records.Count} {cl.RecordId}");
                var crossings = _resolver.FindCrossings(civilDoc, tr, cl, profile, record.Findings, log);
                log?.End("plan.find_crossings", $"crossings={crossings.Count}");

                SectionPlanLogic.ResolveAlignment(record, crossings, profile);
                SectionPlanLogic.ValidateNumbering(record, profile);

                if (record.Status != DeliveryStatus.Ready)
                {
                    SectionPlanLogic.TryApplyExplicitExclusion(record, profile);
                    continue;
                }

                record.LogicalKey = LogicalKeys.ForSectionObject(
                    profile.ProfileId, cl.SourceDrawingHash, cl.SourceHandle,
                    record.SelectedAlignment!, "section");
                var existing = owned.ForLogicalKey(record.LogicalKey);
                var ownedInventoryReadable = owned.IsReadableFor(record.SelectedAlignment!);

                // A managed rerun keeps its managed identity.  Otherwise, resolve the
                // exact manual pair before planning any style/layout mutation.
                if (existing.Count == 0 && ownedInventoryReadable)
                {
                    var expected = new[]
                    {
                        new ManualSectionReuseResolver.Point(cl.WcsEndpoints[0], cl.WcsEndpoints[1]),
                        new ManualSectionReuseResolver.Point(cl.WcsEndpoints[2], cl.WcsEndpoints[3]),
                    };
                    var manualDecision = ManualSectionReuseResolver.Resolve(
                        record.SelectedAlignment, record.Station!.Value, expected, manualSections);

                    if (manualDecision.Kind == ManualSectionReuseResolver.DecisionKind.ReviewRequired)
                    {
                        AddManualReuseReview(record, manualDecision);
                        SectionPlanLogic.TryApplyExplicitExclusion(record, profile);
                        continue;
                    }

                    if (manualDecision.Reason ==
                        ManualSectionReuseResolver.DecisionReason.PresentationIncompatible)
                    {
                        var donor = manualDecision.RelevantCandidates.Single();
                        record.Findings.Add(new DeliveryFinding
                        {
                            Code = SectionFindingCodes.ManualSectionPresentationMismatch,
                            Domain = SectionPlanLogic.Domain,
                            Severity = FindingSeverity.Info,
                            Title = "החתך הידני נשמר ללא שינוי; ייווצר חתך מנוהל נקי עם נקודת גובה אחת",
                            Message = $"SampleLine={donor.SampleLineHandle}; SectionView={donor.SectionViewHandle}; " +
                                      $"evidence={donor.PresentationEvidence ?? "(none)"}",
                            RecommendedAction =
                                "החתך הזר אינו עומד בחוזה התצוגה ולכן אינו משמש כתוצר; אפשר למחוק אותו ידנית לאחר בדיקה חזותית.",
                            AffectedRecordIds = { record.RecordId },
                        });
                    }

                    if (manualDecision.Selected is { } selected)
                    {
                        record.ManualSectionReuse = new ManualSectionReusePlan
                        {
                            SampleLineGroupHandle = selected.SampleLineGroupHandle,
                            SampleLineHandle = selected.SampleLineHandle,
                            SectionViewHandle = selected.SectionViewHandle,
                            SampleLineName = selected.SampleLineName,
                            SectionViewName = selected.SectionViewName,
                            PreservedSectionViewStyle = selected.SectionViewStyleName,
                            PreservedBandStyles = selected.BandStyleNames?.ToList() ?? new List<string>(),
                        };
                        record.Findings.Add(new DeliveryFinding
                        {
                            Code = SectionFindingCodes.ManualSectionReused,
                            Domain = SectionPlanLogic.Domain,
                            Severity = FindingSeverity.Info,
                            Title = $"נמצא חתך ידני תואם ויחיד — יישמר ויועשר ללא שינוי באובייקטי Civil שלו",
                            Message = $"SampleLine={selected.SampleLineHandle}; " +
                                      $"SectionView={selected.SectionViewHandle}; " +
                                      $"style={selected.SectionViewStyleName ?? "(unreadable)"}; " +
                                      $"bands={string.Join(",", selected.BandStyleNames ?? Array.Empty<string>())}",
                            AffectedRecordIds = { record.RecordId },
                        });
                    }
                }

                PlanSources(record, profile, surfaces, corridors, pipeNetworks,
                    db.FingerprintGuid, surfacePairAlignments);
                PlanStyles(record, tr, civilDoc, db, profile, manualStyleDonor,
                    existing, ownedInventoryReadable);
                SectionPlanLogic.BuildUtilityCoverage(record, utilities, profile);
                FillProjectedSystems(
                    record, projectable, profile.Sections.Projection.Enabled);
                var straightTraffic = SectionTrafficStraightScopeService.Filter(
                    record, civilDoc, tr, trafficArrows.Evidence);
                record.TrafficStraightScopeEvidence = straightTraffic.CanonicalEvidence;
                if (straightTraffic.Reason != null)
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.TrafficDirectionEvidenceMismatch,
                        Domain = SectionPlanLogic.Domain, Severity = FindingSeverity.Warning,
                        Title = "חצים ללא שיוך מוכח לאותו מקטע ישר לא שימשו ראיה אוטומטית",
                        Message = $"alignment={record.SelectedAlignment}; reason={straightTraffic.Reason}; " +
                                  $"straight_segment={straightTraffic.SegmentKey ?? "(unproven)"}; " +
                                  $"nearby_arrows_rejected={straightTraffic.RejectedNearCutCount}",
                        RecommendedAction = "בעקום, בספירלה או בגבול מקטע אין להסיק שיוך נתיב מקו המשיק. " +
                            "יש לבדוק את המקור; ברצועה מזוהה אפשר לבחור 'הכרעת כיוון' ולאשר עם/נגד כיוון הציר. " +
                            "מעטפת מרובת מסלולים ללא מיקומים מוכחים דורשת בדיקת גאומטריה — לא יומצאו מיקומי רכבים.",
                        AffectedRecordIds = { record.RecordId },
                    });
                FillPresentationCoverage(
                    record, projectable, straightTraffic.Evidence, profile, trafficArrows.ExternalSources,
                    straightTraffic.Reason);

                var affectingProjectionFailures = projectable.Findings.Where(f =>
                    f.Severity == FindingSeverity.Error &&
                    SectionProjectionFailureScope.AffectsCut(f, record.Cl.WcsEndpoints)).ToList();
                var unresolvedSpanCount = UnresolvedSpanCountForFailureScope(record);
                var locallyProven = LocallyProvenFailures(record, affectingProjectionFailures, projectable, profile);
                if (locallyProven.Count > 0) locallyProvenByRecord[record.RecordId] = locallyProven.Keys.ToList();
                var localProjectionFailures = affectingProjectionFailures.Where(f =>
                    SectionProjectionFailureScope.BlocksCreation(f, record.Cl.WcsEndpoints, unresolvedSpanCount) &&
                    !locallyProven.ContainsKey(f.FindingId)).ToList();
                foreach (var failure in affectingProjectionFailures)
                    record.Findings.Add(localProjectionFailures.Contains(failure)
                        ? SectionProjectionFailureScope.ForCut(failure, record.RecordId)
                        : locallyProven.TryGetValue(failure.FindingId, out var proofEvidence) &&
                          !SectionProjectionFailureScope.IsNamedRegionOnly(failure, unresolvedSpanCount)
                            ? SectionProjectionFailureScope.ForLocalCut(failure, record.RecordId, proofEvidence)
                            : SectionProjectionFailureScope.ForNamedCut(failure, record.RecordId));
                if (localProjectionFailures.Count > 0)
                {
                    record.Status = DeliveryStatus.Blocked;
                    record.Action = PlanAction.ReviewRequired;
                    record.PresentationCoverage.Complete = false;
                }

                if (record.Status != DeliveryStatus.Ready)
                {
                    SectionPlanLogic.TryApplyExplicitExclusion(record, profile);
                    continue;
                }

                record.InputFingerprint = SectionPlanLogic.ComputeFingerprint(record);
                if (record.ManualSectionReuse != null)
                {
                    // UPDATE means Mahod-owned annotations are replaced.  The foreign
                    // SampleLine/SectionView themselves remain read-only and unowned.
                    record.Action = PlanAction.Update;
                }
                else
                {
                    var registry = SectionAnnotationRegistry.ReadAnnotationContractEvidence(
                        tr, db, record.LogicalKey);
                    var ownership = SectionOwnedStateLogic.Evaluate(
                        existing,
                        profile.ProfileId,
                        record.InputFingerprint,
                        ownedInventoryReadable,
                        new SectionOwnedStateLogic.RegistryEvidence(
                            registry.IsValid,
                            registry.EntryExists,
                            registry.Entries.Count,
                            registry.Entries.Count(entry => entry.IsLive),
                            registry.Entries.Count > 0 &&
                            registry.Entries.All(entry => entry.OwnershipValid),
                            registry.Error));
                    ApplyOwnedStateDecision(record, ownership);
                }

                // A persisted exclusion whose finding is no longer reproduced is
                // stale evidence and must be surfaced, never silently retained.
                SectionPlanLogic.TryApplyExplicitExclusion(record, profile);
            }

            // Suggestions are computed only after the whole read-only PLAN exists:
            // one section may suggest a name for a structurally homologous span in
            // another section, but no suggestion changes status or becomes an
            // approval.  The engineer still selects the exact target row in the UI.
            var failureCuts = plan.Records.Select(r =>
                new SectionProjectionFailureScope.Cut(r.RecordId, r.Cl.WcsEndpoints,
                    UnresolvedSpanCountForFailureScope(r))).ToList();
            failureCuts = failureCuts.Select(c => c with
                { LocallyProvenFindingIds = locallyProvenByRecord.GetValueOrDefault(c.RecordId) }).ToList();
            plan.Findings.AddRange(projectable.Findings.Select(f =>
                SectionProjectionFailureScope.ForPlan(f, failureCuts)));
            PopulateSpanSuggestions(plan.Records);

            BlockDuplicateLogicalIdentities(plan);

            if (!annotationInventory.IsValid)
            {
                var repairKeys = EligibleDeadAnnotationRecoveryKeys(
                    tr, db, civilDoc, profile.ProfileId, plan.Records);
                var recovery = SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db, repairKeys);
                if (recovery.IsValid && recovery.DeadEntries.Count > 0)
                {
                    plan.Findings.RemoveAll(f => f.Code == SectionFindingCodes.AnnotationInventoryConflict);
                    foreach (var record in plan.Records.Where(r => repairKeys.Contains(r.LogicalKey!)))
                    {
                        var key = SectionAnnotationRegistry.RegistryKeyFor(record.LogicalKey!);
                        var entries = recovery.DeadEntries.Where(x => x.RegistryKey == key).ToList();
                        if (entries.Count == 0) continue;
                        var finding = new DeliveryFinding
                        {
                            Code = SectionFindingCodes.AnnotationRegistryRepairable,
                            Domain = SectionPlanLogic.Domain,
                            Severity = FindingSeverity.Warning,
                            Title = "רישום הערות שאינן קיימות ניתן לתיקון אטומי עם עדכון החתך",
                            Message = $"logical_key={record.LogicalKey}; registry_key={key}; dead_count={entries.Count}; " +
                                "handles=" + string.Join(",", entries.Select(x => x.Handle)),
                            RecommendedAction = "יש להחיל את עדכון החתך. רק הפניות להערות שהוכח שאינן קיימות יוסרו " +
                                "באותה טרנזקציה של הבנייה מחדש; אין למחוק שכבות או ישויות ידנית.",
                            AffectedRecordIds = { record.RecordId },
                        };
                        record.Findings.Add(finding);
                        plan.Findings.Add(finding);
                    }
                }
            }

            // Deterministic sheet layout: station-ordered grid, computed once for the
            // whole plan so placement is stable across reruns and never overlaps.
            log?.Begin("plan.layout");
            AssignLayout(plan, db, tr, profile);
            log?.End("plan.layout");

            SectionInputIntegrityService.CapturePlanDatabaseState(db, plan, profile);
            plan.Status = SectionInputIntegrityService.HasPlanningIntegrityBlocker(plan)
                ? DeliveryStatus.Blocked
                : DeliveryStatusRules.Aggregate(
                    plan.Records.Select(r => r.Status).ToList());
            log?.Info($"plan complete: status={plan.Status} records={plan.Records.Count}");
            return plan;
        }

        /// <summary>
        /// b7, ONE rule for PLAN and APPLY: a topology finding is proven harmless for
        /// THIS cut only when it is the exact finding of a local-cut source, the whole
        /// cut segment is decided, and every span that source is asked about (the same
        /// marks/ROW/frame/Resolve path as the presentation) gets a non-Unknown verdict;
        /// an unresolvable span keeps every local source blocking. Returns finding id →
        /// whole-cut proof evidence. Nothing else is waived.
        /// </summary>
        internal static Dictionary<string, string> LocallyProvenFailures(
            SectionPlanRecord record, IEnumerable<DeliveryFinding> failures,
            SectionGeometryCollector.CollectResult projectable, ProjectProfile profile)
        {
            var proven = new Dictionary<string, string>(StringComparer.Ordinal);
            if (record.Cl.WcsEndpoints is not { Length: 4 } e || !e.All(double.IsFinite)) return proven;
            var candidates = failures.Where(f => projectable.LocalCutSources.ContainsKey(f.FindingId)).ToList();
            if (candidates.Count == 0 || !SectionCutGeometry.TryFrame(record, out var frame) || frame == null) return proven;
            var rowPolicy = SelectAuthoritativeRowMarks(
                projectable.PlanMarks, profile.Sections.Projection.RowAuthorities);
            var merged = SectionCutGeometry.DimensionCrossingsFor(rowPolicy.PlanMarks, frame);
            var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
                merged.Select(SectionProjectionLogic.DimensionPresentationMark).ToList(),
                requiredLeftOffset: frame.MinOffset, requiredRightOffset: frame.MaxOffset);
            var verdicts = new List<SectionHatchSpanLabelService.LocalCutVerdict>();
            _ = SectionHatchSpanLabelService.Resolve(projectable.PlanRegions, merged, analysis, verdicts);
            foreach (var failure in candidates)
            {
                var source = projectable.LocalCutSources[failure.FindingId];
                if (verdicts.Any(v => v.Source == null ||
                        (ReferenceEquals(v.Source, source) && v.Coverage == SectionHatchLocalCut.Coverage.Unknown)))
                    continue;
                var whole = SectionHatchLocalCut.Prove(source,
                    new SectionProjectionLogic.P2(e[0], e[1]), new SectionProjectionLogic.P2(e[2], e[3]));
                if (whole.Decided) proven[failure.FindingId] = whole.Evidence;
            }
            return proven;
        }

        /// <summary>
        /// 0 only when every width span of the cut carries a confident name (so an
        /// unreadable HA area can neither name nor contradict anything there);
        /// otherwise "unknown", which keeps every affecting failure blocking.
        /// </summary>
        internal static int UnresolvedSpanCountForFailureScope(SectionPlanRecord record)
        {
            var coverage = record.PresentationCoverage;
            return coverage.WidthSpanCount > 0 &&
                   coverage.NamedStripCount == coverage.WidthSpanCount &&
                   coverage.UnresolvedSpans.Count == 0
                ? 0
                : int.MaxValue;
        }

        private static void ApplyOwnedStateDecision(
            SectionPlanRecord record,
            SectionOwnedStateLogic.Evaluation ownership)
        {
            switch (ownership.State)
            {
                case SectionOwnedStateLogic.State.Absent:
                    record.Action = PlanAction.Create;
                    return;
                case SectionOwnedStateLogic.State.Complete:
                    record.Action = PlanAction.Unchanged;
                    return;
                case SectionOwnedStateLogic.State.Repairable:
                    record.Action = PlanAction.Update;
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.OwnedStateIncomplete,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Warning,
                        Title = "מצב החתך המנוהל אינו שלם — תבוצע החלפה אטומית במקום לדווח 'ללא שינוי'",
                        Message = $"logical_key={record.LogicalKey}; reason={ownership.Reason}",
                        RecommendedAction =
                            "יש לאשר APPLY מלא; ה-SampleLine/SectionView והאנוטציות של Mahod ייבנו מחדש באותה טרנזקציה.",
                        AffectedRecordIds = { record.RecordId },
                    });
                    return;
                case SectionOwnedStateLogic.State.Conflict:
                    record.Action = PlanAction.ReviewRequired;
                    record.Status = DeliveryStatus.ReviewRequired;
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.OwnershipConflict,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "בעלות החתך אינה חד-משמעית — PLAN חסום ואינו מדווח 'ללא שינוי'",
                        Message = $"logical_key={record.LogicalKey}; reason={ownership.Reason}",
                        RecommendedAction =
                            "יש להשאיר זוג יחיד ומקושר של SampleLine ו-SectionView בבעלות Mahod, " +
                            "או לתקן את רישום האנוטציות, ואז להריץ PLAN מחדש.",
                        AffectedRecordIds = { record.RecordId },
                    });
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ownership));
            }
        }

        private static void BlockDuplicateLogicalIdentities(SectionPlan plan)
        {
            var conflicts = plan.Records
                .Where(record => !string.IsNullOrWhiteSpace(record.LogicalKey))
                .GroupBy(record => record.LogicalKey!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToList();

            foreach (var conflict in conflicts)
            {
                var records = conflict.ToList();
                foreach (var record in records)
                {
                    record.Status = DeliveryStatus.Blocked;
                    record.Action = PlanAction.ReviewRequired;
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.SourceIdentityConflict,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "שתי הוראות CL יוצרות אותה זהות לוגית — PLAN חסום",
                        Message = $"logical_key={conflict.Key}; source={record.Cl.SourceDrawingPath ?? record.Cl.SourceDrawing}; " +
                                  $"handle={record.Cl.SourceHandle}; alignment={record.SelectedAlignment}",
                        RecommendedAction = "יש להסיר מקור CL כפול או להכריע את זהות המקור בפרופיל ולהריץ PLAN מחדש.",
                        AffectedRecordIds = records.Select(r => r.RecordId).ToList(),
                    });
                }
            }
        }

        private static short DimensionMarkColor(
            double offset,
            string kind,
            IReadOnlyList<SectionProjectionLogic.Crossing> mergedMarks)
        {
            var colors = mergedMarks
                .Where(mark => mark.Offset == offset &&
                               string.Equals(mark.Rule.Kind, kind, StringComparison.Ordinal))
                .Select(mark => mark.Rule.ColorIndex)
                .Distinct()
                .ToList();
            if (colors.Count == 1) return colors[0];
            if (colors.Count > 1)
                throw new InvalidOperationException(
                    $"Dimension mark {offset:F3}/{kind} has conflicting source colors.");
            if (string.Equals(kind, "row", StringComparison.Ordinal)) return 1;
            // The only source-free anchors admitted by the coverage contract are ROW
            // boundaries. Keep the non-ROW fallback explicit for deterministic legacy
            // deserialization/tests, but never infer an arbitrary source color.
            return 8;
        }

        /// <summary>
        /// Record ids in the serialized/tool protocol are handle-path based for
        /// backwards compatibility. Distinct side DWGs can legally reuse the same
        /// raw handle, so detect that collision before any resolver or apply action
        /// can address the wrong row. The later logical-key gate independently
        /// protects managed-object identities.
        /// </summary>
        private static void BlockDuplicateSourceRecordIds(
            IReadOnlyCollection<ClSourceRecord> records, SectionPlan plan)
        {
            foreach (var collision in records
                         .GroupBy(record => record.RecordId, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                var duplicates = collision.ToList();
                var sources = duplicates.Select(record =>
                        $"{record.SourceDrawingPath ?? record.SourceDrawing}#{record.SourceHandle}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var finding = new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceIdentityConflict,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "מקורות CL שונים מייצרים מזהה רשומה זהה — PLAN חסום",
                    Message = $"record_id={collision.Key}; sources={string.Join(" | ", sources)}",
                    RecommendedAction =
                        "יש להסיר קובץ CL כפול/מתנגש או להעביר את ההוראה למקור בעל זהות חד-משמעית ולהריץ PLAN מחדש.",
                    AffectedRecordIds = { collision.Key },
                };
                plan.Findings.Add(finding);
                foreach (var duplicate in duplicates)
                {
                    duplicate.Status = DeliveryStatus.Blocked;
                    duplicate.Findings.Add(new DeliveryFinding
                    {
                        Code = finding.Code,
                        Domain = finding.Domain,
                        Severity = finding.Severity,
                        Title = finding.Title,
                        Message = finding.Message,
                        RecommendedAction = finding.RecommendedAction,
                        AffectedRecordIds = { duplicate.RecordId },
                    });
                }
            }
        }

        internal static string? CommonManualSectionViewStyle(
            IReadOnlyList<ManualSectionReuseResolver.Candidate> candidates)
        {
            var readable = candidates
                .Where(c => c.StationReadable && c.GeometryReadable &&
                            c.PresentationReadable && c.SingleDatumPresentationCompatible)
                .ToList();
            if (readable.Count == 0 || readable.Any(c => string.IsNullOrWhiteSpace(c.SectionViewStyleName)))
                return null;

            var styles = readable.Select(c => c.SectionViewStyleName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return styles.Count == 1 ? styles[0] : null;
        }

        // ---------------------------------------------------------------- sources

        /// <summary>
        /// Which exact source-entity crossings will be projected into this section.
        /// WCS identity is independent of Civil's left/right convention; APPLY later
        /// derives the signed display offset but must reproduce these same keys.
        /// </summary>
        private static void FillProjectedSystems(
            SectionPlanRecord record,
            SectionGeometryCollector.CollectResult projectable,
            bool projectionEnabled)
        {
            record.ProjectedEntities.Clear();
            record.ProjectedSystems.Clear();
            var coverage = record.UtilityCoverage;
            coverage.ProjectionScanState =
                SectionProjectionLogic.ResolveProjectionScanState(
                    projectionEnabled,
                    projectable.Findings.Any(f =>
                        SectionProjectionFailureScope.BlocksUtilityScan(f, record.Cl.WcsEndpoints)));
            coverage.ProjectionDrawingEntityCount = projectable.Utilities.Count;
            coverage.ProjectionSectionCrossingCount = 0;

            // Partial results from a blocked traversal are not trustworthy coverage.
            // Keep the drawing-wide count as diagnostic evidence but project nothing.
            if (coverage.ProjectionScanState != UtilityProjectionScanState.Complete)
                return;

            if (!SectionCutGeometry.TryFrame(record, out var frame)) return;
            var merged = SectionCutGeometry.CrossingsFor(projectable.Utilities, frame!)
                .GroupBy(SectionProjectionLogic.ProjectionEvidenceKey, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(SectionProjectionLogic.ProjectionEvidenceKey, StringComparer.Ordinal)
                .ToList();

            coverage.ProjectionSectionCrossingCount = merged.Count;
            foreach (var crossing in merged)
            {
                var sourceLine = projectable.Utilities.FirstOrDefault(line =>
                    string.Equals(line.SourceHandle, crossing.SourceHandle, StringComparison.Ordinal) &&
                    string.Equals(line.Layer, crossing.Layer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(line.Xref, crossing.Xref, StringComparison.OrdinalIgnoreCase));
                record.ProjectedEntities.Add(new ProjectedEntityPlan
                {
                    ProjectionKey = SectionProjectionLogic.ProjectionEvidenceKey(crossing),
                    SystemLabel = crossing.Rule.Label,
                    SourceLayer = crossing.Layer,
                    SourceXref = crossing.Xref,
                    SourceDrawingPath = sourceLine?.SourceDrawingPath,
                    SourceDrawingHash = sourceLine?.SourceDrawingHash,
                    SourceHandle = crossing.SourceHandle ?? string.Empty,
                    IntersectionWcs = new[] { crossing.WcsX ?? 0, crossing.WcsY ?? 0 },
                });
            }

            record.ProjectedSystems.AddRange(merged.Select(c => c.Rule.Label)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// Establishes the visible-presentation promise before APPLY.  This is
        /// deliberately independent of whatever annotations APPLY later happens to
        /// create: zero recognized marks/widths/strip names is a review blocker, not a
        /// vacuously successful section.
        /// </summary>
        private static void FillPresentationCoverage(
            SectionPlanRecord record,
            SectionGeometryCollector.CollectResult projectable,
            IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> trafficArrows,
            ProjectProfile profile,
            IReadOnlyList<SectionExternalSourceEvidence> trafficSources,
            string? trafficScopeReason = null)
        {
            if (record.Cl.WcsEndpoints.Length < 4 || record.SelectedCrossing == null)
                return;

            var frame = SectionCutGeometry.RequireFrame(record);

            var rowPolicy = SelectAuthoritativeRowMarks(
                projectable.PlanMarks, profile.Sections.Projection.RowAuthorities);
            var rowAuthorityReady = rowPolicy.Selection.State is
                SectionRowAuthorityLogic.SelectionState.NoCandidates or
                SectionRowAuthorityLogic.SelectionState.Authoritative;
            if (!rowAuthorityReady)
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.RowAuthorityUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "זכות הדרך לא הוצגה — נמצאו מקורות ROW ללא מקור מוסמך יחיד",
                    Message = $"state={rowPolicy.Selection.State}; " +
                              $"reason={rowPolicy.Selection.Reason}; sources=" +
                              string.Join(", ", rowPolicy.Selection.CandidateSourceKeys),
                    RecommendedAction =
                        "יש לאשר בפרופיל מקור ROW יחיד לפי SHA-256 (ובמידת הצורך XREF/path). " +
                        "עד אז החתך אינו מציג ROW ואינו עוקף את החוסר באמצעות גבולות CL משוערים.",
                    AffectedRecordIds = { record.RecordId },
                });
            }

            var merged = SectionCutGeometry.DimensionCrossingsFor(rowPolicy.PlanMarks, frame);
            var marks = merged.Select(SectionProjectionLogic.DimensionPresentationMark).ToList();
            var baseAnalysis = SectionProjectionLogic.AnalyzePresentationCoverage(
                marks,
                requiredLeftOffset: frame.MinOffset,
                requiredRightOffset: frame.MaxOffset);
            var explicitOverrides = SectionHatchSpanLabelService.Resolve(
                projectable.PlanRegions, merged, baseAnalysis);
            var afterRegions = SectionProjectionLogic.AnalyzePresentationCoverage(
                marks,
                requiredLeftOffset: frame.MinOffset,
                requiredRightOffset: frame.MaxOffset,
                approvedOverrides: explicitOverrides);
            explicitOverrides.AddRange(TrafficArrowSpanOverrides(
                record, afterRegions.UnresolvedSpans, trafficArrows));
            var afterTraffic = SectionProjectionLogic.AnalyzePresentationCoverage(
                marks,
                requiredLeftOffset: frame.MinOffset,
                requiredRightOffset: frame.MaxOffset,
                approvedOverrides: explicitOverrides);
            // This source-only digest must be captured before applying profile labels:
            // neither their text nor approval metadata can invalidate their own key.
            record.PreManualSpanEvidenceDigest = afterTraffic.Summary.EvidenceDigest;
            explicitOverrides.AddRange(BuildManualSpanOverrides(record, afterTraffic, profile));
            // Recheck source conflicts after manual/traffic labels as well. A
            // Historical suggestions cannot hide conflicting regions. A new explicit
            // current-span engineering edit controls the name only; region evidence
            // is retained and participates in the shared PLAN/APPLY digest.
            explicitOverrides = explicitOverrides.Where(item =>
                !SectionHatchSpanLabelService.IsRegionEvidence(item.Source)).ToList();
            var regionBaseline = SectionProjectionLogic.AnalyzePresentationCoverage(
                marks,
                requiredLeftOffset: frame.MinOffset,
                requiredRightOffset: frame.MaxOffset,
                approvedOverrides: explicitOverrides);
            explicitOverrides.AddRange(SectionHatchSpanLabelService.Resolve(
                projectable.PlanRegions, merged, regionBaseline));
            var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
                marks,
                requiredLeftOffset: frame.MinOffset,
                requiredRightOffset: frame.MaxOffset,
                approvedOverrides: explicitOverrides);
            var s = analysis.Summary;
            record.PresentationCoverage = new SectionPresentationCoveragePlan
            {
                PlanMarkCount = s.PlanMarkCount,
                DimensionMarkCount = s.DimensionMarkCount,
                DimensionOffsets = analysis.DimensionMarks.Select(mark => mark.Offset).ToList(),
                WidthSpanCount = s.WidthSpanCount,
                NamedStripCount = s.NamedStripCount,
                VehicleStripCount = s.VehicleStripCount,
                OfficeCarStripCount = s.OfficeCarStripCount,
                EvidenceDigest = s.EvidenceDigest,
                BoundarySource = s.BoundarySource,
                BoundaryFromM = s.BoundaryFrom,
                BoundaryToM = s.BoundaryTo,
                RowAuthorityState = rowPolicy.Selection.State.ToString().ToLowerInvariant(),
                AuthoritativeRowSourceKey = rowPolicy.Selection.AuthoritativeSourceKey,
                Complete = s.IsComplete && rowAuthorityReady,
            };
            record.PresentationCoverage.DimensionMarks.AddRange(
                analysis.DimensionMarks.Select(mark => new SectionDimensionMarkPlan
                {
                    OffsetM = mark.Offset,
                    Kind = mark.Kind,
                    Label = mark.Label,
                    ColorIndex = DimensionMarkColor(mark.Offset, mark.Kind, merged),
                    GeometryKey = SectionProjectionLogic.DimensionGeometryKey(mark.Offset),
                    SourceEvidence = analysis.DimensionBoundaries.Single(boundary =>
                        boundary.Offset == mark.Offset).Sources.ToList(),
                }));
            record.PresentationCoverage.RowCandidateSourceKeys.AddRange(
                rowPolicy.Selection.CandidateSourceKeys);
            record.PresentationCoverage.ExplicitSpanOverrides.AddRange(
                explicitOverrides.Select(item => new SectionSpanLabelOverridePlan
                {
                    OffsetM = item.Offset,
                    Label = item.Label,
                    Source = item.Source,
                    Evidence = item.Evidence,
                }));
            record.PresentationCoverage.ResolvedSpans.AddRange(
                ResolvedSpanEvidence(analysis, merged, explicitOverrides));
            record.PresentationCoverage.UnresolvedSpans.AddRange(
                analysis.UnresolvedSpans.Select(span => new SectionUnresolvedSpanPlan
                {
                    FromOffsetM = span.From,
                    ToOffsetM = span.To,
                    WidthM = span.Width,
                    LeftKind = span.LeftKind,
                    RightKind = span.RightKind,
                    Reason = span.Reason,
                }));
            foreach (var strip in analysis.BusLaneEdgeStrips)
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.StripBesideBusLaneLine,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Info,
                    Title = FormattableString.Invariant(
                        $"רצועה {strip.From:0.00}..{strip.To:0.00} מ' ליד קו נת\"צ נקראה \"{strip.Label}\" — לבדיקה אם זה הנת\"צ"),
                    RecommendedAction = "אם זה נתיב התחבורה הציבורית — לשנות את שם הרצועה בחלון שמות הרצועות. אחרת אין צורך בפעולה.",
                    ProjectProfileId = profile.ProfileId,
                });

            if (s.IsComplete && rowAuthorityReady)
            {
                FillTrafficDirections(record, analysis, trafficArrows, profile, trafficSources, trafficScopeReason);
                return;
            }

            record.Status = DeliveryStatus.ReviewRequired;
            record.Action = PlanAction.ReviewRequired;
            if (s.PlanMarkCount == 0)
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.PlanMarksMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "לא נמצאו סימוני תכנית שחוצים את ה-CL — אין בסיס לחלוקת רצועות",
                    Message = $"alignment={record.SelectedAlignment}; station={record.Station:F2}; " +
                              $"boundary={s.BoundarySource}:{s.BoundaryFrom:F2}..{s.BoundaryTo:F2}",
                    RecommendedAction =
                        "יש לטעון/לתקן את מקור ה-SM/GM שמכיל אבני שפה, נתיבים ומדרכות, " +
                        "ולוודא שה-XREF טעון וטרי. אם ה-CL מחוץ להיקף העבודה בלבד, ניתן להחריג אותו באישור הנדסי מפורש.",
                    AffectedRecordIds = { record.RecordId },
                });
            }
            record.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.PresentationCoverageMissing,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.ReviewRequired,
                Title = "כיסוי התצוגה של החתך אינו מלא — לא ניתן להחיל חתך ללא מידות ושמות רצועות",
                Message = $"marks={s.PlanMarkCount}; dimension_marks={s.DimensionMarkCount}; " +
                          $"widths={s.WidthSpanCount}; named_strips={s.NamedStripCount}; " +
                          $"vehicle_strips={s.VehicleStripCount}; " +
                          $"boundary={s.BoundarySource}:{s.BoundaryFrom:F2}..{s.BoundaryTo:F2}; " +
                          $"continuous={s.ContinuousWidthChain}; " +
                          $"narrow_gaps={s.NarrowGapCount}; oversized_gaps={s.OversizedGapCount}",
                RecommendedAction =
                    "אם קיים ROW יש לאשר מקור יחיד ולהשלים שתי חציות. אם אין ROW, יש לוודא " +
                    "שסימוני תכנית ממשיים חוצים משני צדי ה-CL; רק הטווח בין הסימונים החיצוניים " +
                    "נמדד ומקבל שמות. גבולות ה-CL אינם רצועות ואין להמציא עבורם שם.",
                AffectedRecordIds = { record.RecordId },
            });
        }

        /// <summary>
        /// Binds every resolved label to the evidence that resolved it.  Source marks
        /// and traffic arrows retain their provenance. A new exact-interval engineer
        /// edit has name priority; historical manual decisions do not acquire it.
        /// </summary>
        private static IReadOnlyList<SectionResolvedSpanPlan> ResolvedSpanEvidence(
            SectionProjectionLogic.PresentationAnalysis analysis,
            IReadOnlyList<SectionProjectionLogic.Crossing> mergedMarks,
            IReadOnlyList<SectionProjectionLogic.SpanLabelOverride> explicitOverrides)
        {
            var result = new List<SectionResolvedSpanPlan>();
            foreach (var strip in analysis.StripLabels)
            {
                var left = analysis.DimensionMarks.Single(mark =>
                    mark.Offset == strip.From);
                var right = analysis.DimensionMarks.Single(mark =>
                    mark.Offset == strip.To);
                var sourceMarks = mergedMarks.Where(mark =>
                        string.Equals(mark.Rule.Kind, "strip", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(mark.Rule.Label, strip.Label, StringComparison.Ordinal) &&
                        mark.Offset > strip.From + 1e-9 && mark.Offset < strip.To - 1e-9)
                    .OrderBy(mark => mark.Offset)
                    .ThenBy(mark => mark.Layer, StringComparer.Ordinal)
                    .ThenBy(mark => mark.Xref, StringComparer.Ordinal)
                    .ThenBy(mark => mark.SourceHandle, StringComparer.Ordinal)
                    .ToList();
                var overrides = explicitOverrides.Where(item =>
                        string.Equals(item.Label, strip.Label, StringComparison.Ordinal) &&
                        item.Offset > strip.From + 1e-9 && item.Offset < strip.To - 1e-9)
                    .OrderByDescending(item => item.Source == SectionReviewedSpanLabelLogic.Source ? 3 :
                                               item.Source == "traffic-arrow" ? 2 :
                                               item.Source == "manual-profile" ? 1 : 0)
                    .ThenBy(item => item.Offset)
                    .ToList();

                string source;
                string digest;
                if (overrides.Count > 0 && overrides[0].Source == SectionReviewedSpanLabelLogic.Source &&
                    SectionReviewedSpanLabelLogic.IsExactReview(overrides[0], strip.From, strip.To))
                {
                    source = overrides[0].Source;
                    digest = ArtifactHash.Sha256OfText(overrides[0].Evidence!);
                }
                else if (sourceMarks.Count > 0)
                {
                    source = "source-mark";
                    digest = ArtifactHash.Sha256OfText(string.Join("\n",
                        sourceMarks.Select(mark => FormattableString.Invariant(
                            $"{mark.Offset:R}|{mark.Rule.Label}|{mark.Layer}|{mark.Xref}|{mark.SourceHandle}"))));
                }
                else if (overrides.Count > 0)
                {
                    source = overrides[0].Source;
                    digest = SectionVehicleDirectionPlanner.IsSha256(overrides[0].Evidence)
                        ? overrides[0].Evidence!
                        : ArtifactHash.Sha256OfText(FormattableString.Invariant(
                            $"{overrides[0].Offset:R}|{overrides[0].Label}|{overrides[0].Source}|{overrides[0].Evidence}"));
                }
                else
                {
                    source = "boundary-rule";
                    digest = ArtifactHash.Sha256OfText(FormattableString.Invariant(
                        $"{strip.From:R}|{strip.To:R}|{left.Kind}|{right.Kind}|{strip.Label}"));
                }

                result.Add(new SectionResolvedSpanPlan
                {
                    FromOffsetM = strip.From,
                    ToOffsetM = strip.To,
                    WidthM = strip.To - strip.From,
                    LeftKind = left.Kind,
                    RightKind = right.Kind,
                    Label = strip.Label,
                    EvidenceSource = source,
                    EvidenceDigest = digest,
                });
            }
            return result;
        }

        /// <summary>
        /// Adds review-only suggestions to unresolved rows from independently
        /// evidenced peers on the same alignment and homologous lateral position.
        /// Missing or conflicting evidence deliberately produces no label.
        /// </summary>
        internal static void PopulateSpanSuggestions(
            IReadOnlyCollection<SectionPlanRecord> records)
        {
            var observations = records
                .Where(record => !string.IsNullOrWhiteSpace(record.SelectedAlignment))
                .SelectMany(record =>
                    record.PresentationCoverage.ResolvedSpans.Select(span =>
                            new SectionSpanSuggestionLogic.Observation(
                                record.RecordId, record.SelectedAlignment!, record.Station,
                                span.FromOffsetM, span.ToOffsetM,
                                span.LeftKind, span.RightKind,
                                true, span.Label, span.EvidenceSource, span.EvidenceDigest))
                        .Concat(record.PresentationCoverage.UnresolvedSpans.Select(span =>
                            new SectionSpanSuggestionLogic.Observation(
                                record.RecordId, record.SelectedAlignment!, record.Station,
                                span.FromOffsetM, span.ToOffsetM,
                                span.LeftKind, span.RightKind,
                                false, null, null, null,
                                HasConflictingLocalEvidence: string.Equals(span.Reason,
                                    "conflicting-strip-label-evidence", StringComparison.Ordinal)))))
                .ToList();

            var suggestions = SectionSpanSuggestionLogic.Suggest(observations);
            foreach (var suggestion in suggestions)
            {
                var record = records.Single(item =>
                    string.Equals(item.RecordId, suggestion.RecordKey, StringComparison.Ordinal));
                var span = record.PresentationCoverage.UnresolvedSpans.Single(item =>
                    Math.Abs(item.FromOffsetM - suggestion.From) <= 1e-9 &&
                    Math.Abs(item.ToOffsetM - suggestion.To) <= 1e-9);
                span.HomologyGroupKey = suggestion.HomologyGroupKey;
                span.HomologousSpanCount = suggestion.HomologousSpanCount;
                span.SuggestedLabel = suggestion.Label;
                span.SuggestionConfidence = suggestion.Confidence.ToString().ToLowerInvariant();
                span.SuggestionSupportCount = suggestion.SupportingRecordCount;
                span.SuggestionConflictCount = suggestion.ConflictingLabelCount;
                span.SuggestionSources.AddRange(suggestion.EvidenceSources);
                span.SuggestionEvidence.AddRange(suggestion.EvidenceRecords);
                span.StrongReviewCandidate = suggestion.IsStrongReviewCandidate;
            }
        }

        internal sealed record RowMarkSelection(
            IReadOnlyList<SectionGeometryCollector.CollectedLine> PlanMarks,
            SectionRowAuthorityLogic.Selection Selection);

        /// <summary>
        /// Keeps every ordinary plan mark, but admits ROW lines only from one exact
        /// approved source instance.  Unapproved/ambiguous ROW is suppressed; CL
        /// endpoints remain the honest outer-boundary fallback.
        /// </summary>
        internal static RowMarkSelection SelectAuthoritativeRowMarks(
            IReadOnlyList<SectionGeometryCollector.CollectedLine> planMarks,
            IReadOnlyList<ProjectProfile.SectionsProfile.ProjectionProfile.RowAuthority>? authorities)
        {
            var rowLines = planMarks.Where(line => string.Equals(
                    line.Rule.Kind, "row", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var selection = SectionRowAuthorityLogic.Select(
                rowLines.Select(ToSource),
                (authorities ?? Array.Empty<ProjectProfile.SectionsProfile
                        .ProjectionProfile.RowAuthority>())
                    .Select(authority => new SectionRowAuthorityLogic.Authority(
                        authority.SourceDrawingSha256,
                        authority.SourcePathPattern,
                        authority.XrefPattern,
                        authority.ApprovedBy,
                        authority.ApprovedAtUtc)));
            var accepted = planMarks.Where(line =>
                    !string.Equals(line.Rule.Kind, "row", StringComparison.OrdinalIgnoreCase) ||
                    selection.Accepts(ToSource(line)))
                .ToList();
            return new RowMarkSelection(accepted, selection);

            static SectionRowAuthorityLogic.Source ToSource(
                SectionGeometryCollector.CollectedLine line) => new(
                line.SourceDrawingPath,
                line.SourceDrawingHash,
                line.Xref);
        }

        private static List<SectionProjectionLogic.SpanLabelOverride> TrafficArrowSpanOverrides(
            SectionPlanRecord record,
            IReadOnlyList<SectionProjectionLogic.UnresolvedSpan> spans,
            IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> trafficArrows)
        {
            var result = new List<SectionProjectionLogic.SpanLabelOverride>();
            if (record.SelectedCrossing == null) return result;
            var frame = SectionCutGeometry.RequireFrame(record);
            var tangentRadians = record.SelectedCrossing.TangentDeg * Math.PI / 180.0;
            var tangentX = Math.Cos(tangentRadians);
            var tangentY = Math.Sin(tangentRadians);

            foreach (var span in spans)
            {
                if (span.Width < SectionTrafficSpanLabelLogic.MinimumVehicleWidthM ||
                    span.Width > SectionTrafficSpanLabelLogic.MaximumCompositeCarriagewayWidthM)
                    continue;
                var spanEvidence = trafficArrows
                    .Where(arrow => double.IsFinite(arrow.X) && double.IsFinite(arrow.Y))
                    .Select(arrow =>
                    {
                        var point = new SectionProjectionLogic.P2(arrow.X, arrow.Y);
                        var offset = frame.OffsetAtAlignmentProjection(point);
                        var onCut = frame.PointAt(offset);
                        var along = (arrow.X - onCut.X) * tangentX +
                                    (arrow.Y - onCut.Y) * tangentY;
                        return (Class: TrafficDirectionEvidenceLogic.ClassifySource(
                                arrow.Layer, arrow.BlockName), Offset: offset, Along: along,
                            arrow.Source, arrow.HandlePath);
                    })
                    .Where(item => Math.Abs(item.Along) <=
                                   TrafficDirectionEvidenceLogic.DefaultMaxSearchDistanceM &&
                                   item.Offset > span.From + 0.05 &&
                                   item.Offset < span.To - 0.05 &&
                                   item.Class is TrafficDirectionEvidenceLogic.SourceClass
                                       .ApprovedTrafficArrow or
                                       TrafficDirectionEvidenceLogic.SourceClass.ExcludedBikeArrow)
                    .ToList();
                var resolution = SectionTrafficSpanLabelLogic.Resolve(
                    span.Width,
                    spanEvidence.Select(item => new SectionTrafficSpanLabelLogic.Evidence(
                        item.Offset, item.Class,
                        $"{item.Source}|{item.HandlePath}"))
                        .ToList());
                if (!resolution.IsResolved) continue;
                var evidenceDigest = ArtifactHash.Sha256OfText(string.Join("\n",
                    spanEvidence.OrderBy(item => item.HandlePath, StringComparer.Ordinal)
                        .Select(item => FormattableString.Invariant(
                            $"{item.Class}|{item.Offset:R}|{item.Along:R}|{item.Source}|{item.HandlePath}"))));
                result.Add(new SectionProjectionLogic.SpanLabelOverride(
                    (span.From + span.To) / 2.0, resolution.Label!,
                    "traffic-arrow", evidenceDigest));
            }
            return result;
        }

        private static List<SectionProjectionLogic.SpanLabelOverride> BuildManualSpanOverrides(
            SectionPlanRecord record,
            SectionProjectionLogic.PresentationAnalysis current,
            ProjectProfile profile)
        {
            var result = ManualSpanOverrides(record, current, profile);
            var scoped = profile.Sections.Decisions.SpanLabels.Where(decision =>
                decision.AllowSourceLabelOverride &&
                SectionSpanPhysicalIdentity.SameSourceScope(record, decision) &&
                string.Equals(decision.SourceHandle, record.Cl.SourceHandle, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(decision.AlignmentName, record.SelectedAlignment, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var decision in scoped)
            {
                var matches = current.WidthSpans.Where(span => decision.FromOffsetM == span.From &&
                    decision.ToOffsetM == span.To).ToArray();
                if (matches.Length == 1 && SectionSpanPhysicalIdentity.Matches(record, decision) &&
                    !string.IsNullOrWhiteSpace(decision.Label) &&
                    decision.Label.Trim().Length <= 80 && !string.IsNullOrWhiteSpace(decision.ApprovedBy) &&
                    decision.ApprovedAtUtc is { Kind: DateTimeKind.Utc } approvedAt &&
                    SectionSpanSuggestionLogic.IsCredibleLabel(decision.Label, matches[0].Width))
                {
                    result.Add(SectionReviewedSpanLabelLogic.Create(matches[0].From, matches[0].To,
                        decision.Label, decision.ApprovedBy, approvedAt));
                    continue;
                }
                if (HasCurrentSpanReplacement(record, current, profile, decision)) continue;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SpanLabelDecisionStale,
                    Domain = SectionPlanLogic.Domain, Severity = FindingSeverity.Warning,
                    Title = "שם רצועה שנערך חסר הוכחה פיזית עדכנית או אינו תואם לחתך הנוכחי",
                    Message = $"span={decision.FromOffsetM:F3}..{decision.ToOffsetM:F3}; label={decision.Label}",
                    RecommendedAction = "ההכרעה נשמרה כהיסטוריה. יש לפתוח עריכת שמות, להשוות תחנה, מקור וגבולות ולאשר מחדש רק את היעד הנוכחי.",
                    AffectedRecordIds = { record.RecordId },
                });
            }
            return result;
        }

        private static List<SectionProjectionLogic.SpanLabelOverride> ManualSpanOverrides(
            SectionPlanRecord record,
            SectionProjectionLogic.PresentationAnalysis current,
            ProjectProfile profile)
        {
            const double toleranceM = 0.01;
            var result = new List<SectionProjectionLogic.SpanLabelOverride>();
            var scoped = (profile.Sections.Decisions.SpanLabels ?? new())
                .Where(decision => !decision.AllowSourceLabelOverride &&
                    SectionSpanPhysicalIdentity.SameSourceScope(record, decision) &&
                    string.Equals(decision.SourceHandle, record.Cl.SourceHandle,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(decision.AlignmentName, record.SelectedAlignment,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            var applied = new HashSet<ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision>();
            foreach (var span in current.UnresolvedSpans)
            {
                var matches = scoped.Where(decision =>
                        SectionSpanPhysicalIdentity.Matches(record, decision, span.From, span.To) &&
                        decision.FromOffsetM is { } from &&
                        decision.ToOffsetM is { } to &&
                        Math.Abs(from - span.From) <= toleranceM &&
                        Math.Abs(to - span.To) <= toleranceM &&
                        !string.IsNullOrWhiteSpace(decision.Label) &&
                        !string.IsNullOrWhiteSpace(decision.ApprovedBy) &&
                        decision.ApprovedAtUtc is not null)
                    .ToList();
                if (matches.Count != 1) continue;
                result.Add(new SectionProjectionLogic.SpanLabelOverride(
                    (span.From + span.To) / 2.0,
                    matches[0].Label!.Trim(),
                    "manual-profile",
                    $"approved-by={matches[0].ApprovedBy};approved-at={matches[0].ApprovedAtUtc:O}"));
                applied.Add(matches[0]);
            }

            bool AgreesWithOneResolvedSpan(ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision decision)
            {
                // Legacy approvals fill unknown spans; they do not supersede source
                // labels. Agreement only removes a false stale warning, never adds
                // an override, changes measured bounds or upgrades approval authority.
                if (!SectionSpanPhysicalIdentity.Matches(record, decision) ||
                    decision.FromOffsetM is not { } from || decision.ToOffsetM is not { } to ||
                    !double.IsFinite(from) || !double.IsFinite(to) || to <= from ||
                    string.IsNullOrWhiteSpace(decision.Label) || decision.Label.Trim().Length > 80 ||
                    string.IsNullOrWhiteSpace(decision.ApprovedBy) ||
                    decision.ApprovedAtUtc is not { Kind: DateTimeKind.Utc } at || at == default)
                    return false;
                bool Matches(double left, double right) =>
                    Math.Abs(from - left) <= toleranceM && Math.Abs(to - right) <= toleranceM;
                var spans = current.WidthSpans.Where(span => Matches(span.From, span.To)).ToArray();
                if (spans.Length != 1 || !SectionSpanPhysicalIdentity.Matches(record, decision, spans[0].From, spans[0].To) ||
                    !SectionSpanSuggestionLogic.IsCredibleLabel(decision.Label, spans[0].Width))
                    return false;
                var span = spans[0];
                if (scoped.Count(other => SectionSpanPhysicalIdentity.Matches(record, other) &&
                        other.FromOffsetM is { } left && other.ToOffsetM is { } right &&
                        Math.Abs(left - span.From) <= toleranceM && Math.Abs(right - span.To) <= toleranceM) != 1 ||
                    current.UnresolvedSpans.Any(item => item.From == span.From && item.To == span.To))
                    return false;
                var labels = current.StripLabels.Where(item => item.From == span.From && item.To == span.To).ToArray();
                return labels.Length == 1 && string.Equals(labels[0].Label?.Trim(),
                    decision.Label.Trim(), StringComparison.Ordinal);
            }

            foreach (var stale in scoped.Where(decision =>
                         !applied.Contains(decision) && !AgreesWithOneResolvedSpan(decision) &&
                         !HasCurrentSpanReplacement(record, current, profile, decision)))
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SpanLabelDecisionStale,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Warning,
                    Title = "הכרעת שם רצועה ישנה חסרה הוכחה פיזית עדכנית או אינה תואמת לחתך הנוכחי",
                    Message = $"span={stale.FromOffsetM:F3}..{stale.ToOffsetM:F3}; " +
                              $"label={stale.Label}; alignment={stale.AlignmentName}",
                    RecommendedAction =
                        "ההכרעה נשמרה כהיסטוריה. יש להשוות תחנה, מקור וגבולות ולאשר מחדש את הרצועה הנוכחית מהטבלה.",
                    AffectedRecordIds = { record.RecordId },
                });
            }
            return result;
        }

        // Retain historical decisions, but do not demand the same recovery again
        // after one valid current approval has explicitly superseded this target.
        private static bool HasCurrentSpanReplacement(
            SectionPlanRecord record, SectionProjectionLogic.PresentationAnalysis current,
            ProjectProfile profile,
            ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision historical)
        {
            var spans = current.WidthSpans.Where(span => historical.FromOffsetM == span.From &&
                historical.ToOffsetM == span.To).ToArray();
            if (spans.Length != 1) return false;
            var span = spans[0];
            var candidates = profile.Sections.Decisions.SpanLabels.Where(decision =>
                SectionSpanPhysicalIdentity.SameSourceScope(record, decision) &&
                string.Equals(decision.SourceHandle, record.Cl.SourceHandle, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(decision.AlignmentName, record.SelectedAlignment, StringComparison.OrdinalIgnoreCase) &&
                decision.FromOffsetM == span.From && decision.ToOffsetM == span.To &&
                SectionSpanPhysicalIdentity.Matches(record, decision, span.From, span.To) &&
                !string.IsNullOrWhiteSpace(decision.Label) && decision.Label.Trim().Length <= 80 &&
                !string.IsNullOrWhiteSpace(decision.ApprovedBy) &&
                decision.ApprovedAtUtc is { Kind: DateTimeKind.Utc } at && at != default &&
                SectionSpanSuggestionLogic.IsCredibleLabel(decision.Label, span.Width)).ToArray();
            if (candidates.Length != 1 || ReferenceEquals(candidates[0], historical)) return false;
            if (candidates[0].AllowSourceLabelOverride || current.UnresolvedSpans.Any(item =>
                    item.From == span.From && item.To == span.To)) return true;
            var labels = current.StripLabels.Where(item => item.From == span.From && item.To == span.To).ToArray();
            return labels.Length == 1 && string.Equals(labels[0].Label?.Trim(),
                candidates[0].Label!.Trim(), StringComparison.Ordinal);
        }

        private static void FillTrafficDirections(
            SectionPlanRecord record,
            SectionProjectionLogic.PresentationAnalysis presentation,
            IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> trafficArrows,
            ProjectProfile profile,
            IReadOnlyList<SectionExternalSourceEvidence> trafficSources,
            string? trafficScopeReason = null)
        {
            record.TrafficDirections.Clear();
            record.CompositeTrafficEnvelopes.Clear();
            var trafficCutFrame = SectionCutGeometry.RequireFrame(record);
            foreach (var strip in presentation.StripLabels)
            {
                var spec = SectionFurnitureLogic.VehicleForStrip(strip.Label);
                if (spec == null || !SectionFurnitureLogic.FitsStrip(spec, strip.To - strip.From))
                    continue;

                var targets = new List<(double Offset, string? TrackDigest,
                    IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> Arrows)>
                {
                    ((strip.From + strip.To) / 2.0, null, trafficArrows)
                };
                if (strip.Label == "נתיבי נסיעה")
                {
                    var tracks = SectionTrafficTrackLogic.Resolve(SectionCutGeometry.RequireFrame(record),
                        record.SelectedCrossing!.TangentDeg * Math.PI / 180,
                        strip.From, strip.To, trafficArrows,
                        SectionTrafficPlanContract.SourceAuthorityDigest(trafficSources));
                    record.CompositeTrafficEnvelopes.Add(new SectionCompositeTrafficEnvelopePlan
                    {
                        FromOffsetM = strip.From, ToOffsetM = strip.To,
                        SourceAuthorities = trafficSources.ToList(),
                        SourceTracks = tracks.Tracks.ToList()
                    });
                    targets = tracks.Tracks.Select(track =>
                        (track.OffsetM, (string?)track.EvidenceDigest, track.Arrows)).ToList();
                    if (!tracks.IsResolved)
                    {
                        record.Status = DeliveryStatus.ReviewRequired;
                        record.Action = PlanAction.ReviewRequired;
                        record.Findings.Add(new DeliveryFinding
                        {
                            Code = SectionFindingCodes.TrafficDirectionEvidenceMismatch,
                            Domain = SectionPlanLogic.Domain, Severity = FindingSeverity.ReviewRequired,
                            Title = "מסלולי החצים במעטפת הכביש דורשים בדיקה — לא הוזזו ולא אוחדו",
                            Message = FormattableString.Invariant($"envelope={strip.From:R}..{strip.To:R}; source_tracks={tracks.Tracks.Count}; reason={tracks.Error}; traffic_scope={trafficScopeReason ?? "proven-straight-segment"}"),
                            RecommendedAction = "יש לבדוק את מסלולי החצים ומקום הרכבים מול המקור; אין להסיק גבולות נתיבים מרוחב המעטפת או להזיז מסלול כדי להתאים רכב.",
                            AffectedRecordIds = { record.RecordId }
                        });
                    }
                }
                foreach (var target in targets)
                {
                var midpoint = target.Offset;
                var kind = spec.Key switch
                {
                    "bus" => SectionTrafficDirectionAnnotationLogic.StripKind.Bus,
                    "bike" => SectionTrafficDirectionAnnotationLogic.StripKind.Bike,
                    _ => SectionTrafficDirectionAnnotationLogic.StripKind.Road,
                };
                var mode = kind == SectionTrafficDirectionAnnotationLogic.StripKind.Bike
                    ? SectionVehicleDirectionPlanner.ArrowEvidenceMode.Bicycle
                    : SectionVehicleDirectionPlanner.ArrowEvidenceMode.MotorTraffic;

                SectionVehicleDirectionPlanner.DirectionPlan direction;
                if (!TryLaneTarget(record, midpoint, out var laneX, out var laneY))
                {
                    direction = SectionVehicleDirectionPlanner.Resolve(
                        record.Cl.SourceDrawingHash,
                        record.Cl.SourceHandle,
                        record.SelectedAlignment,
                        midpoint,
                        double.NaN,
                        double.NaN,
                        record.SelectedCrossing!.TangentDeg * Math.PI / 180.0,
                        target.Arrows,
                        profile.Sections.Decisions.TrafficDirections,
                        evidenceMode: mode, laneFromOffsetM: strip.From, laneToOffsetM: strip.To,
                        trackEvidenceDigest: target.TrackDigest, laneCutFrame: trafficCutFrame);
                }
                else
                {
                    direction = SectionVehicleDirectionPlanner.Resolve(
                        record.Cl.SourceDrawingHash,
                        record.Cl.SourceHandle,
                        record.SelectedAlignment,
                        midpoint,
                        laneX,
                        laneY,
                        record.SelectedCrossing!.TangentDeg * Math.PI / 180.0,
                        target.Arrows,
                        profile.Sections.Decisions.TrafficDirections,
                        evidenceMode: mode, laneFromOffsetM: strip.From, laneToOffsetM: strip.To,
                        trackEvidenceDigest: target.TrackDigest, laneCutFrame: trafficCutFrame);
                }

                var plan = new SectionTrafficDirectionPlan
                {
                    FromOffsetM = strip.From,
                    ToOffsetM = strip.To,
                    LaneMidOffsetM = midpoint,
                    TrackEvidenceDigest = target.TrackDigest,
                    StripLabel = strip.Label,
                    StripKind = kind.ToString().ToLowerInvariant(),
                    EvidenceMode = SectionVehicleDirectionPlanner.EvidenceModeToken(mode),
                    State = direction.State.ToString().ToLowerInvariant(),
                    Flow = direction.IsResolved
                        ? SectionVehicleDirectionPlanner.FlowToken(direction.Flow)
                        : null,
                    OfficeCarView = direction.OfficeCarView?.ToString().ToLowerInvariant(),
                    DirectionSource = direction.DirectionSource,
                    DirectionDigest = direction.DirectionDigest,
                    Reason = trafficScopeReason == null ? direction.Reason :
                        "automatic-traffic-scope=" + trafficScopeReason + ";" + direction.Reason,
                };
                record.TrafficDirections.Add(plan);

                if (plan.IsResolved) continue;
                record.Status = DeliveryStatus.ReviewRequired;
                record.Action = PlanAction.ReviewRequired;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.TrafficDirectionUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"כיוון הנסיעה ברצועה {midpoint:F3} מ׳ אינו חד-משמעי — החתך חסום עד הכרעה",
                    Message = $"strip={strip.Label}; mode={plan.EvidenceMode}; state={plan.State}; reason={plan.Reason}",
                    RecommendedAction = "יש לבחור את הרשומה, ללחוץ 'הכרעת כיוון', לאשר עם/נגד כיוון הציר ולהריץ PLAN מחדש.",
                    AffectedRecordIds = { record.RecordId },
                });
                }
            }

            // Vehicle count and direction count are two independently computed
            // promises. Any disagreement is a code/data contract break, never an
            // acceptable empty result.
            if (record.TrafficDirections.Count !=
                SectionTrafficPlanContract.VehicleInstances(record))
            {
                record.Status = DeliveryStatus.Blocked;
                record.Action = PlanAction.ReviewRequired;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.TrafficDirectionEvidenceMismatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "מספר חוזי כיוון הנסיעה אינו תואם למספר רצועות הרכב — PLAN חסום",
                    Message = $"direction_rows={record.TrafficDirections.Count}; vehicle_instances={SectionTrafficPlanContract.VehicleInstances(record)}; vehicle_strips={record.PresentationCoverage.VehicleStripCount}",
                    AffectedRecordIds = { record.RecordId },
                });
            }
        }

        /// <summary>
        /// Reconstructs the lane midpoint on the actual (possibly skewed) CL chord.
        /// The section offset remains only a coordinate: no sign-based direction
        /// inference occurs here or anywhere in the direction resolver.
        /// </summary>
        private static bool TryLaneTarget(
            SectionPlanRecord record, double laneMidOffset,
            out double x, out double y)
        {
            x = y = double.NaN;
            if (!SectionCutGeometry.TryFrame(record, out var frame) ||
                !double.IsFinite(laneMidOffset) ||
                laneMidOffset < frame!.MinOffset || laneMidOffset > frame.MaxOffset)
                return false;
            var point = frame.PointAt(laneMidOffset);
            x = point.X;
            y = point.Y;
            return double.IsFinite(x) && double.IsFinite(y);
        }

        private static void PlanSources(
            SectionPlanRecord record,
            ProjectProfile profile,
            List<(string Name, string Handle)> surfaces,
            List<(string Name, string Handle)> corridors,
            List<(string Name, string Handle)> pipeNetworks,
            string? drawingFingerprint = null,
            IReadOnlyList<SectionSourceSelectionLogic.Identity>? alignmentIdentities = null)
        {
            var rules = profile.Sections.Sources.SampledSourceRules;

            // A wildcard surface rule used to enable every surface in the drawing for
            // every alignment. On 6422 that meant ten required sections per view,
            // including design/bottom surfaces belonging to other axes. Select the
            // engineering pair explicitly and fail closed when the names do not prove it.
            if (rules.Any(r => string.Equals(r.Kind, "surface", StringComparison.OrdinalIgnoreCase)) ||
                profile.Sections.Sources.SurfacePairs.Any(p => string.Equals(p.AlignmentName,
                    record.SelectedAlignment, StringComparison.OrdinalIgnoreCase)))
                PlanSurfaceSources(record, profile, surfaces, drawingFingerprint, alignmentIdentities);

            foreach (var rule in rules.Where(r =>
                         !string.Equals(r.Kind, "surface", StringComparison.OrdinalIgnoreCase)))
            {
                var pool = rule.Kind?.ToLowerInvariant() switch
                {
                    "corridor" => corridors,
                    "pipe-network" => pipeNetworks,
                    _ => null,
                };
                if (pool == null) continue;

                var matches = pool
                    .Where(s => ClInstructionReader.WildcardMatch(s.Name, rule.NamePattern ?? rule.Name ?? "*"))
                    .ToList();

                if (matches.Count == 0 && rule.Required)
                {
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.SourceMissing,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"כלל מקור הדגימה הנדרש '{Bidi.Ltr(rule.Name ?? rule.NamePattern)}' לא התאים לשום דבר " +
                                $"({rule.Kind})",
                        AffectedRecordIds = { record.RecordId },
                    });
                    record.Status = DeliveryStatus.ReviewRequired;
                    continue;
                }

                foreach (var m in matches)
                {
                    record.PlannedSources.Add(new SectionSourcePlan
                    {
                        SourceName = m.Name,
                        SourceType = rule.Kind ?? "unknown",
                        SourceHandle = m.Handle,
                        NativeSampleCapability = true,
                        PlannedState = "sampled",
                        AdapterRequired = false,
                        Required = rule.Required,
                        Status = DeliveryStatus.Ready,
                    });
                }
            }

            // Utility rules are additive; utilities discovered but not yet supported
            // surface explicitly (directive §16 — nothing silently omitted).
            foreach (var rule in profile.Sections.Sources.UtilitySourceRules)
            {
                record.PlannedSources.Add(new SectionSourcePlan
                {
                    SourceName = rule.Name ?? rule.NamePattern ?? "utility",
                    SourceType = rule.Kind ?? "unknown",
                    NativeSampleCapability = rule.Kind is "pipe-network" or "pressure-network",
                    PlannedState = rule.Kind is "pipe-network" or "pressure-network" ? "sampled" : "adapter",
                    AdapterRequired = rule.Kind is not ("pipe-network" or "pressure-network"),
                    Required = rule.Required,
                    Status = DeliveryStatus.Ready,
                });
            }
        }

        private static void PlanSurfaceSources(
            SectionPlanRecord record,
            ProjectProfile profile,
            List<(string Name, string Handle)> surfaces,
            string? drawingFingerprint = null,
            IReadOnlyList<SectionSourceSelectionLogic.Identity>? alignmentIdentities = null)
        {
            var matchingAlignments = (alignmentIdentities ?? Array.Empty<SectionSourceSelectionLogic.Identity>())
                .Where(a => string.Equals(a.Name, record.SelectedAlignment, StringComparison.OrdinalIgnoreCase)).ToArray();
            var explicitPair = SectionSourceSelectionLogic.SelectExplicitPair(drawingFingerprint,
                record.SelectedAlignment, matchingAlignments.Length == 1 ? matchingAlignments[0].Handle : null,
                surfaces.Select(s => new SectionSourceSelectionLogic.Identity(s.Name, s.Handle)).ToArray(),
                profile.Sections.Sources.SurfacePairs);
            if (explicitPair.IsConfigured)
            {
                if (!explicitPair.IsValid)
                {
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.SourceMissing, Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.ReviewRequired,
                        Title = "זוג המשטחים המאושר אינו תואם למקורות הנוכחיים",
                        Message = explicitPair.Error ?? "Reviewed surface pair is invalid.",
                        RecommendedAction = "פתח הגדרת פרויקט, בדוק את זוג קיים/מתוכנן של התוואי ושמור באישור מחדש.",
                        AffectedRecordIds = { record.RecordId },
                    });
                    record.Status = DeliveryStatus.ReviewRequired;
                    return;
                }
                record.ExplicitSurfacePair = explicitPair.Pair! with { };
                AddSelected(explicitPair.Pair!.ExistingName!);
                AddSelected(explicitPair.Pair.DesignName!);
                return;
            }
            var choice = SectionSourceSelectionLogic.Select(
                surfaces.Select(s => s.Name),
                record.SelectedAlignment,
                profile.Sections.Projection.ExistingSurfacePatterns);

            AddChoiceFinding(choice.ExistingGround, "קרקע קיימת", "existing-ground");
            AddChoiceFinding(choice.Design, "משטח תכנון של התוואי", "design");
            if (!choice.IsReady) return;

            if (string.Equals(choice.ExistingGround.Name, choice.Design.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "אותו משטח זוהה גם כקרקע קיימת וגם כתכנון — לא ניתן ליצור חתך אמין",
                    Message = choice.Design.Name ?? string.Empty,
                    AffectedRecordIds = { record.RecordId },
                });
                record.Status = DeliveryStatus.ReviewRequired;
                return;
            }

            AddSelected(choice.ExistingGround.Name!);
            AddSelected(choice.Design.Name!);

            void AddSelected(string name)
            {
                var source = surfaces.First(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                record.PlannedSources.Add(new SectionSourcePlan
                {
                    SourceName = source.Name,
                    SourceType = "surface",
                    SourceHandle = source.Handle,
                    NativeSampleCapability = true,
                    PlannedState = "sampled",
                    AdapterRequired = false,
                    Required = true,
                    Status = DeliveryStatus.Ready,
                });
            }

            void AddChoiceFinding(SectionSourceSelectionLogic.Choice selected, string roleHe, string role)
            {
                if (selected.IsSelected) return;
                var ambiguous = selected.State == SectionSourceSelectionLogic.ChoiceState.Ambiguous;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = ambiguous
                        ? $"נמצאו כמה מועמדים ל-{roleHe} — לא נבחר משטח בניחוש"
                        : $"לא נמצא {roleHe} שניתן לקשור בוודאות לתוואי '{Bidi.Ltr(record.SelectedAlignment)}'",
                    Message = selected.Candidates.Count == 0
                        ? $"No {role} candidate."
                        : "Candidates: " + string.Join(", ", selected.Candidates),
                    RecommendedAction = "יש לקבע בפרופיל את משטח הקרקע הקיימת ואת משטח התכנון של התוואי, ואז להריץ תכנון מחדש.",
                    AffectedRecordIds = { record.RecordId },
                });
                record.Status = DeliveryStatus.ReviewRequired;
            }
        }

        /// <summary>
        /// Places every applicable record on a deterministic grid. READY records keep
        /// their final PLAN position; a presentation-only ReviewRequired record also
        /// receives a temporary position so the engineer can inspect its proven
        /// geometry before resolving labels/ROW/direction. This does not make that
        /// record applicable to APPLY, whose complete-batch/READY gates are separate.
        /// </summary>
        private static void AssignLayout(
            SectionPlan plan,
            Database db,
            Transaction tr,
            ProjectProfile profile)
        {
            var placeable = plan.Records
                .Where(SectionDiagnosticPreviewPolicy.CanAssignLayout)
                .ToList();
            if (placeable.Count == 0) return;

            var layout = profile.Sections.Layout;
            var origin = ResolveLayoutOriginFor(db, tr);

            var baseline = new SectionLayoutPlanner.Options
            {
                Columns  = layout.Columns  is > 0 ? layout.Columns.Value  : 3,
                SpacingX = layout.SpacingX is > 0 ? layout.SpacingX.Value : 120.0,
                SpacingY = layout.SpacingY is > 0 ? layout.SpacingY.Value : 80.0,
                OriginX  = origin.X,
                OriginY  = origin.Y,
            };

            // A section wider than the grid step would collide with its neighbour, so
            // the grid grows to the widest planned swath rather than assuming.
            var widths = placeable.Select(r => SectionCutGeometry.RequireFrame(r).Width).ToList();
            var heights = placeable.Select(_ => 40.0).ToList();
            var options = SectionLayoutPlanner.FitToContent(widths, heights, baseline);

            var cells = SectionLayoutPlanner.Plan(
                placeable.Select(r => new SectionLayoutPlanner.Input(r.RecordId, r.Station, r.SectionId)).ToList(),
                options);

            foreach (var cell in cells)
            {
                var record = placeable.First(r => r.RecordId == cell.RecordId);
                record.PlannedLayoutPosition = new[] { cell.X, cell.Y };
            }
        }

        /// <summary>Grid origin clear of the existing model extents.</summary>
        private static (double X, double Y) ResolveLayoutOriginFor(
            Database db, Transaction tr)
        {
            // Never call Database.UpdateExt from PLAN: despite being described as
            // read-only, that API can dirty host state. Derive the fresh envelope
            // directly from live model-space entity extents inside the aborting
            // PLAN transaction instead. Failure to establish a finite envelope is
            // blocking: silently falling back to (0,0) can place a complete batch on
            // top of existing project geometry while downstream checks stay green.
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(
                table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var found = false;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (ObjectId id in model)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                    var ext = entity.GeometricExtents;
                    if (!double.IsFinite(ext.MaxPoint.X) || !double.IsFinite(ext.MaxPoint.Y))
                        continue;
                    maxX = Math.Max(maxX, ext.MaxPoint.X);
                    maxY = Math.Max(maxY, ext.MaxPoint.Y);
                    found = true;
                }
                catch
                {
                    // One proxy/entity may legitimately have no geometric extents.
                    // The database envelope below remains an independent fallback.
                }
            }

            if (found)
                return RequireFiniteLayoutOrigin(maxX + 200.0, maxY, "live model-space extents");

            var extmax = db.Extmax;
            return RequireFiniteLayoutOrigin(extmax.X + 200.0, extmax.Y, "database Extmax");
        }

        private static (double X, double Y) RequireFiniteLayoutOrigin(
            double x, double y, string source)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new InvalidOperationException(
                    $"Cannot establish a finite section layout origin from {source}; " +
                    "PLAN is blocked to prevent placement over existing geometry.");
            return (x, y);
        }

        // ----------------------------------------------------------------- styles

        private static void PlanStyles(
            SectionPlanRecord record,
            Transaction tr,
            CivilDocument civilDoc,
            Database db,
            ProjectProfile profile,
            string? manualStyleDonor,
            IReadOnlyList<SectionOwnedStateLogic.ObjectEvidence> existing,
            bool ownedInventoryReadable)
        {
            var styles = profile.Sections.Styles;

            // Reused manual views are the style/band evidence themselves.  Recording
            // the evidence is useful to VERIFY, but APPLY must not reapply or alter it.
            if (record.ManualSectionReuse is { } reuse)
            {
                record.PlannedStyles["section_view_style"] =
                    reuse.PreservedSectionViewStyle ?? "(preserved; unreadable name)";
                record.PlannedStyles["band_set_style"] = reuse.PreservedBandStyles.Count == 0
                    ? "(none; preserved single-datum view)"
                    : string.Join(" | ", reuse.PreservedBandStyles);
                return;
            }

            ResolveSectionViewStyle(styles.SectionViewStyle, StyleNames(tr, civilDoc, "section-view"));
            var sampleLineStyle = SectionSampleLineStyleLogic.Resolve(
                styles.SampleLineStyle, StyleNames(tr, civilDoc, "sample-line"),
                ReadOwnedSampleLineStyle(tr, db, record, profile.ProfileId,
                    existing, ownedInventoryReadable));
            if (!sampleLineStyle.IsResolved)
            {
                record.Status = DeliveryStatus.ReviewRequired;
                record.Action = PlanAction.ReviewRequired;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.StyleMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "לא ניתן לקבוע בבטחה את סגנון קו הדגימה — נדרשת בדיקה",
                    Message = sampleLineStyle.Error,
                    RecommendedAction = "בדוק את סגנון קו הדגימה הקיים ואת הגדרת הפרויקט; לא נבחר סגנון חלופי במקום מידע חסר או לא־קריא.",
                    AffectedRecordIds = { record.RecordId },
                });
            }
            else
            {
                record.PlannedStyles["sample_line_style"] = sampleLineStyle.Name!;
                if (sampleLineStyle.Source != SectionSampleLineStyleLogic.Origin.ExplicitProject)
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.StyleMissing,
                        Domain = SectionPlanLogic.Domain,
                        Severity = sampleLineStyle.Source == SectionSampleLineStyleLogic.Origin.ExistingOwned
                            ? FindingSeverity.Info : FindingSeverity.Warning,
                        Title = sampleLineStyle.Source == SectionSampleLineStyleLogic.Origin.ExistingOwned
                            ? $"סגנון קו הדגימה הקיים נשמר: '{Bidi.Ltr(sampleLineStyle.Name!)}'"
                            : $"לא הוגדר סגנון קו דגימה — ליצירה חדשה נבחר '{Bidi.Ltr(sampleLineStyle.Name!)}' (זמני, ניתן לקביעה בהגדרת פרויקט)",
                        Message = sampleLineStyle.Source == SectionSampleLineStyleLogic.Origin.ExistingOwned
                            ? "Read from the exact owned SampleLine; style collection order cannot replace it."
                            : "Deterministic provisional selection from available drawing styles, not an approved office standard.",
                        AffectedRecordIds = { record.RecordId },
                    });
            }
            if (string.IsNullOrWhiteSpace(styles.BandSetStyle))
            {
                // Nataly's acceptance requires one datum/elevation reference, not a
                // document-default band stack.  An empty profile value therefore
                // means an explicit empty band set, not "pick the first style".
                record.PlannedStyles["band_set_style"] = "(none; single datum required)";
            }
            else
            {
                Resolve("band_set_style", styles.BandSetStyle, StyleNames(tr, civilDoc, "band-set"));
            }

            void ResolveSectionViewStyle(string? configured, List<string> available)
            {
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    Resolve("section_view_style", configured, available);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(manualStyleDonor) &&
                    available.Any(a => string.Equals(a, manualStyleDonor, StringComparison.OrdinalIgnoreCase)))
                {
                    record.PlannedStyles["section_view_style"] = manualStyleDonor!;
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.ManualSectionReused,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Info,
                        Title = $"סגנון תצוגת החתך נלקח מהסגנון המשותף לחתכים הידניים: '{Bidi.Ltr(manualStyleDonor!)}'",
                        Message = "Every readable manual donor agrees on this SectionView style; all donor objects remain foreign and unchanged.",
                        AffectedRecordIds = { record.RecordId },
                    });
                    return;
                }

                // The failed 1.2.13 run inherited an arbitrary document default and
                // displayed a tall analysis grid, duplicated side elevations and a
                // huge graph title.  A product-owned style is deterministic and may
                // be safely created by APPLY without rewriting any office style.
                record.PlannedStyles["section_view_style"] =
                    SectionViewPresentationStyleService.StyleName;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.PresentationStyleBuiltIn,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Warning,
                    Title = "לא נמצא סגנון ידני יחיד — ייעשה שימוש בסגנון החתך הנקי של Mahod",
                    Message = "The product-owned style hides the native Civil graph grid, repeated axis elevations and graph title; it does not modify any office style.",
                    RecommendedAction =
                        "בדיקת הגאומטריה אינה מציגה Style/רצועות/בלוקים. יש לאשר חזותית את התוצר המלא רק אחרי APPLY על עותק בדיקה, ואז VERIFY.",
                    AffectedRecordIds = { record.RecordId },
                });
            }

            static string RoleHe(string role) => role switch
            {
                "section_view_style" => "סגנון תצוגת חתך",
                "sample_line_style" => "סגנון קו דגימה",
                "band_set_style" => "סט רצועות (band set)",
                _ => role,
            };

            void Resolve(string role, string? configured, List<string> available)
            {
                // Configured and present in this drawing: use it.
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    if (available.Any(a => string.Equals(a, configured, StringComparison.OrdinalIgnoreCase)))
                    {
                        record.PlannedStyles[role] = configured;
                        return;
                    }

                    // Configured but absent: an explicit failure, never a silent default.
                    record.PlannedStyles[role] = $"{configured} (חסר בשרטוט)";
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.StyleMissing,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"{RoleHe(role)} שהוגדר ('{Bidi.Ltr(configured)}') אינו קיים בשרטוט הזה",
                        Message = available.Count == 0
                            ? "No styles of this kind were found."
                            : "Available: " + string.Join(", ", available.Take(8)),
                        AffectedRecordIds = { record.RecordId },
                    });
                    record.Status = DeliveryStatus.ReviewRequired;
                    return;
                }

                // Not configured: use what the project drawing actually provides, and
                // say plainly that it is a provisional choice rather than an approved standard.
                if (available.Count > 0)
                {
                    record.PlannedStyles[role] = available[0];
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.StyleMissing,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Warning,
                        Title = $"לא הוגדר {RoleHe(role)} — נעשה שימוש בסגנון הפרויקט '{Bidi.Ltr(available[0])}' (ברירת מחדל — לאישור בהגדרת פרויקט)",
                        Message = "יש לאשר את הסגנון הרצוי בפרופיל הפרויקט כדי לקבע אותו.",
                        AffectedRecordIds = { record.RecordId },
                    });
                    return;
                }

                record.PlannedStyles[role] = "(ברירת מחדל של השרטוט — זמני)";
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.StyleMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Warning,
                    Title = $"No {role} available in this drawing — the document default will be used (provisional)",
                    AffectedRecordIds = { record.RecordId },
                });
            }
        }

        private static SectionSampleLineStyleLogic.OwnedStyle ReadOwnedSampleLineStyle(
            Transaction tr, Database db, SectionPlanRecord record, string profileId,
            IReadOnlyList<SectionOwnedStateLogic.ObjectEvidence> existing, bool inventoryReadable)
        {
            SectionSampleLineStyleLogic.OwnedStyle Fail(string reason) => new(
                SectionSampleLineStyleLogic.OwnedState.Conflict, Error: reason);
            if (!inventoryReadable)
                return new(SectionSampleLineStyleLogic.OwnedState.Unreadable,
                    Error: "owned-inventory-unreadable");
            if (existing.Count == 0)
                return new(SectionSampleLineStyleLogic.OwnedState.Absent);
            if (existing.Any(e => !string.Equals(e.Feature, "sections", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(e.ProjectProfileId, profileId, StringComparison.Ordinal) ||
                    !(string.Equals(e.Role, "sample-line", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(e.Role, "section-view", StringComparison.OrdinalIgnoreCase))))
                return Fail("owned-sample-line-style-ownership-conflict");

            var lines = existing.Where(e => string.Equals(e.Role, "sample-line",
                StringComparison.OrdinalIgnoreCase)).ToList();
            var views = existing.Where(e => string.Equals(e.Role, "section-view",
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (lines.Count != 1 || views.Count > 1 ||
                views.Any(e => !string.Equals(e.ParentSampleLineIdentity, lines[0].ObjectIdentity,
                    StringComparison.OrdinalIgnoreCase)))
                return Fail("owned-sample-line-style-ownership-ambiguous");

            try
            {
                var evidence = lines[0];
                var handle = new Handle(long.Parse(evidence.ObjectIdentity,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture));
                var id = db.GetObjectId(false, handle, 0);
                if (id.IsNull || id.IsErased ||
                    tr.GetObject(id, OpenMode.ForRead) is not CivilDb.SampleLine sampleLine)
                    return Fail("owned-sample-line-style-object-missing");
                var meta = SectionOwnershipService.Read(tr, sampleLine);
                if (meta == null || !string.Equals(meta.Feature, "sections", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(meta.Role, "sample-line", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(meta.ProjectProfileId, profileId, StringComparison.Ordinal) ||
                    !string.Equals(meta.InputFingerprint, evidence.InputFingerprint, StringComparison.Ordinal) ||
                    (evidence.LogicalKeyIsExact &&
                     !string.Equals(meta.LogicalKey, record.LogicalKey, StringComparison.Ordinal)))
                    return Fail("owned-sample-line-style-ownership-changed");
                var styleId = sampleLine.StyleId;
                if (styleId.IsNull || styleId.IsErased ||
                    tr.GetObject(styleId, OpenMode.ForRead) is not CivilDb.Styles.StyleBase style ||
                    string.IsNullOrWhiteSpace(style.Name))
                    return new(SectionSampleLineStyleLogic.OwnedState.Unreadable,
                        Error: "owned-sample-line-style-unreadable");
                return new(SectionSampleLineStyleLogic.OwnedState.Readable, style.Name);
            }
            catch (Exception ex)
            {
                return new(SectionSampleLineStyleLogic.OwnedState.Unreadable,
                    Error: $"owned-sample-line-style-read-failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Style names of the requested kind that genuinely exist in the open drawing.
        /// This is the project evidence the product resolves against - no visual standard
        /// is invented from an unavailable source.
        /// </summary>
        internal static List<string> StyleNames(
            Transaction tr, CivilDocument doc, string kind)
        {
            var names = new List<string>();
            var collection = kind switch
            {
                "section-view" => (System.Collections.IEnumerable)doc.Styles.SectionViewStyles,
                "sample-line"  => doc.Styles.SampleLineStyles,
                "band-set"     => doc.Styles.SectionViewBandSetStyles,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };

            foreach (var item in collection)
            {
                if (item is not ObjectId id || id.IsNull || id.IsErased)
                    throw new InvalidDataException(
                        $"The Civil {kind} style collection contains an invalid id.");
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Styles.StyleBase sb ||
                    string.IsNullOrWhiteSpace(sb.Name))
                    throw new InvalidDataException(
                        $"A Civil {kind} style could not be read completely.");
                names.Add(sb.Name);
            }
            return names;
        }

        private static void AddManualReuseReview(
            SectionPlanRecord record,
            ManualSectionReuseResolver.Decision decision)
        {
            var geometryProblem = decision.Reason is
                ManualSectionReuseResolver.DecisionReason.GeometryMismatch or
                ManualSectionReuseResolver.DecisionReason.GeometryUnreadable;
            record.Findings.Add(new DeliveryFinding
            {
                Code = geometryProblem
                    ? SectionFindingCodes.ManualSectionGeometryMismatch
                    : SectionFindingCodes.ManualSectionAmbiguous,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.ReviewRequired,
                Title = geometryProblem
                    ? "קיים חתך ידני באותה תחנה אך קו הדגימה שלו אינו תואם ל-CL — נדרשת בדיקה"
                    : "לא ניתן לזהות חתך ידני יחיד וחד-משמעי — נדרשת בדיקה",
                Message = $"reason={decision.Reason}; candidates=" +
                          string.Join(", ", decision.RelevantCandidates.Select(c =>
                              $"SL:{c.SampleLineHandle}/SV:{c.SectionViewHandle}")),
                RecommendedAction =
                    "יש להשאיר SectionView ידני יחיד התואם לאותו alignment/station/CL, או לאשר יצירה במסלול נפרד לאחר פתרון העמימות.",
                AffectedRecordIds = { record.RecordId },
            });
            record.Action = PlanAction.ReviewRequired;
            record.Status = DeliveryStatus.ReviewRequired;
        }

        // ------------------------------------------------------------- inventory

        /// <summary>
        /// Read-only inventory of genuinely foreign SampleLine/SectionView pairs.
        /// Any Mahod ownership metadata on either object excludes the pair from the
        /// manual resolver; unreadable ownership is also excluded (fail closed).
        /// </summary>
        internal static List<ManualSectionReuseResolver.Candidate> ScanManualSectionCandidates(
            Transaction tr, CivilDocument civilDoc)
        {
            var candidates = new List<ManualSectionReuseResolver.Candidate>();

            foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
            {
                var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as CivilDb.Alignment
                    ?? throw new InvalidDataException(
                        $"Manual-section inventory could not open alignment {alignmentId}.");

                var groups = alignment.GetSampleLineGroupIds();

                foreach (ObjectId groupId in groups)
                {
                    var group = tr.GetObject(groupId, OpenMode.ForRead) as CivilDb.SampleLineGroup
                        ?? throw new InvalidDataException(
                            $"Manual-section inventory could not open sample-line group {groupId}.");

                    var sampleLines = group.GetSampleLineIds();

                    foreach (ObjectId sampleLineId in sampleLines)
                    {
                        var sampleLine = tr.GetObject(sampleLineId, OpenMode.ForRead) as CivilDb.SampleLine
                            ?? throw new InvalidDataException(
                                $"Manual-section inventory could not open sample line {sampleLineId}.");
                        if (SectionOwnershipService.Read(tr, sampleLine) != null)
                            continue;

                        var stationReadable = true;
                        double station;
                        try { station = sampleLine.Station; }
                        catch { station = double.NaN; stationReadable = false; }

                        var geometryReadable = true;
                        var vertices = new List<ManualSectionReuseResolver.Point>();
                        try
                        {
                            var sourceVertices = sampleLine.Vertices;
                            for (var i = 0; i < sourceVertices.Count; i++)
                            {
                                var p = sourceVertices[i].Location;
                                vertices.Add(new ManualSectionReuseResolver.Point(p.X, p.Y));
                            }
                            if (vertices.Count < 2) geometryReadable = false;
                        }
                        catch { geometryReadable = false; }

                        foreach (ObjectId viewId in EnumerateSectionViewIds(sampleLine))
                        {
                            var view = tr.GetObject(viewId, OpenMode.ForRead) as CivilDb.SectionView
                                ?? throw new InvalidDataException(
                                    $"Manual-section inventory could not open section view {viewId}.");
                            if (SectionOwnershipService.Read(tr, view) != null)
                                continue;

                            if (view.StyleId.IsNull ||
                                tr.GetObject(view.StyleId, OpenMode.ForRead) is not CivilDb.Styles.StyleBase style ||
                                string.IsNullOrWhiteSpace(style.Name))
                                throw new InvalidDataException(
                                    $"Manual section view {view.Handle} has no readable style.");
                            var styleName = style.Name;

                            var bandStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            int? bandItemCount = null;
                            using (var bottomBands = view.Bands.GetBottomBandItems())
                            using (var topBands = view.Bands.GetTopBandItems())
                                bandItemCount = CollectBandStyles(bottomBands) +
                                                CollectBandStyles(topBands);

                            var presentationReadable =
                                SectionViewPresentationStyleService.TryReadSingleDatumCompatibility(
                                    tr, view, out var presentationCompatible,
                                    out var presentationEvidence, bandItemCount);

                            candidates.Add(new ManualSectionReuseResolver.Candidate(
                                alignment.Name,
                                station,
                                stationReadable,
                                vertices,
                                geometryReadable,
                                group.Handle.ToString(),
                                sampleLine.Handle.ToString(),
                                view.Handle.ToString(),
                                sampleLine.Name,
                                view.Name,
                                styleName,
                                bandStyles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                                presentationReadable,
                                presentationCompatible,
                                presentationEvidence));

                            int CollectBandStyles(System.Collections.IEnumerable items)
                            {
                                var count = 0;
                                foreach (var item in items)
                                {
                                    count++;
                                    var property = item?.GetType().GetProperty("BandStyleId")
                                        ?? throw new InvalidDataException(
                                            "A manual SectionView band item has no BandStyleId property.");
                                    var value = property.GetValue(item);
                                    if (value is not ObjectId styleId || styleId.IsNull || styleId.IsErased ||
                                        tr.GetObject(styleId, OpenMode.ForRead) is not CivilDb.Styles.StyleBase bandStyle ||
                                        string.IsNullOrWhiteSpace(bandStyle.Name))
                                        throw new InvalidDataException(
                                            "A manual SectionView band style is unreadable.");
                                    bandStyles.Add(bandStyle.Name);
                                }
                                return count;
                            }
                        }
                    }
                }
            }

            return candidates
                .OrderBy(c => c.AlignmentName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.StationReadable ? c.Station : double.MaxValue)
                .ThenBy(c => c.SampleLineHandle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.SectionViewHandle, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static IReadOnlyCollection<string> EligibleDeadAnnotationRecoveryKeys(
            Transaction tr, Database db, CivilDocument civilDoc, string profileId,
            IReadOnlyList<SectionPlanRecord> targets)
        {
            var inventory = ScanOwnedObjects(tr, civilDoc);
            var keys = new List<string>();
            foreach (var record in targets.Where(r => r.Status == DeliveryStatus.Ready &&
                         r.Action == PlanAction.Update && r.ManualSectionReuse == null &&
                         !string.IsNullOrWhiteSpace(r.LogicalKey) && !string.IsNullOrWhiteSpace(r.InputFingerprint)))
            {
                if (targets.Count(r => r.LogicalKey == record.LogicalKey) != 1) continue;
                var registry = SectionAnnotationRegistry.ReadAnnotationContractEvidence(tr, db, record.LogicalKey!);
                var evidence = new SectionOwnedStateLogic.RegistryEvidence(registry.IsValid,
                    registry.EntryExists, registry.Entries.Count, registry.Entries.Count(e => e.IsLive),
                    registry.Entries.Count > 0 && registry.Entries.All(e => e.OwnershipValid), registry.Error);
                if (SectionOwnedStateLogic.CanRepairDeadAnnotations(
                        inventory.ForLogicalKey(record.LogicalKey!), profileId, record.InputFingerprint!,
                        inventory.IsReadableFor(record.SelectedAlignment!), evidence))
                    keys.Add(record.LogicalKey!);
            }
            return keys;
        }

        private sealed class OwnedSectionInventory
        {
            private readonly Dictionary<string, List<SectionOwnedStateLogic.ObjectEvidence>>
                _byLogicalKey = new(StringComparer.Ordinal);
            private readonly HashSet<string> _objectIdentities =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _unreadableAlignments =
                new(StringComparer.OrdinalIgnoreCase);

            internal bool GlobalReadable { get; private set; } = true;
            internal int ObjectCount => _objectIdentities.Count;
            internal int UnreadableAlignmentCount => _unreadableAlignments.Count;

            internal IReadOnlyList<SectionOwnedStateLogic.ObjectEvidence> ForLogicalKey(
                string logicalKey) =>
                _byLogicalKey.TryGetValue(logicalKey, out var entries)
                    ? entries
                    : Array.Empty<SectionOwnedStateLogic.ObjectEvidence>();

            internal bool IsReadableFor(string alignmentName) =>
                GlobalReadable && !_unreadableAlignments.Contains(alignmentName);

            internal void Add(
                string lookupLogicalKey,
                SectionOwnedStateLogic.ObjectEvidence evidence)
            {
                if (!_byLogicalKey.TryGetValue(lookupLogicalKey, out var entries))
                    _byLogicalKey[lookupLogicalKey] = entries = new();
                if (!entries.Any(existing =>
                        string.Equals(existing.ObjectIdentity, evidence.ObjectIdentity,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Role, evidence.Role,
                            StringComparison.OrdinalIgnoreCase)))
                    entries.Add(evidence);
                _objectIdentities.Add(evidence.ObjectIdentity);
            }

            internal void MarkUnreadable(string? alignmentName)
            {
                if (string.IsNullOrWhiteSpace(alignmentName))
                    GlobalReadable = false;
                else
                    _unreadableAlignments.Add(alignmentName);
            }
        }

        private static OwnedSectionInventory ScanOwnedObjects(
            Transaction tr, CivilDocument civilDoc)
        {
            var inventory = new OwnedSectionInventory();
            ObjectIdCollection alignmentIds;
            try { alignmentIds = civilDoc.GetAlignmentIds(); }
            catch
            {
                inventory.MarkUnreadable(null);
                return inventory;
            }

            foreach (ObjectId alignmentId in alignmentIds)
            {
                CivilDb.Alignment? alignment;
                try { alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as CivilDb.Alignment; }
                catch
                {
                    inventory.MarkUnreadable(null);
                    continue;
                }
                if (alignment == null)
                {
                    inventory.MarkUnreadable(null);
                    continue;
                }
                var alignmentName = alignment.Name;

                ObjectIdCollection slgIds;
                try { slgIds = alignment.GetSampleLineGroupIds(); }
                catch
                {
                    inventory.MarkUnreadable(alignmentName);
                    continue;
                }

                foreach (ObjectId slgId in slgIds)
                {
                    CivilDb.SampleLineGroup? slg;
                    try { slg = tr.GetObject(slgId, OpenMode.ForRead) as CivilDb.SampleLineGroup; }
                    catch
                    {
                        inventory.MarkUnreadable(alignmentName);
                        continue;
                    }
                    if (slg == null)
                    {
                        inventory.MarkUnreadable(alignmentName);
                        continue;
                    }
                    CollectOwned(tr, slg, inventory, alignmentName,
                        parentSampleLineIdentity: null);

                    ObjectIdCollection slIds;
                    try { slIds = slg.GetSampleLineIds(); }
                    catch
                    {
                        inventory.MarkUnreadable(alignmentName);
                        continue;
                    }
                    foreach (ObjectId slId in slIds)
                    {
                        CivilDb.SampleLine? sampleLine;
                        try { sampleLine = tr.GetObject(slId, OpenMode.ForRead) as CivilDb.SampleLine; }
                        catch
                        {
                            inventory.MarkUnreadable(alignmentName);
                            continue;
                        }
                        if (sampleLine == null)
                        {
                            inventory.MarkUnreadable(alignmentName);
                            continue;
                        }

                        var sampleLineIdentity = sampleLine.Handle.ToString();
                        CollectOwned(tr, sampleLine, inventory, alignmentName,
                            parentSampleLineIdentity: null);

                        ObjectIdCollection viewIds;
                        try { viewIds = sampleLine.GetSectionViewIds(); }
                        catch
                        {
                            inventory.MarkUnreadable(alignmentName);
                            continue;
                        }
                        foreach (ObjectId viewId in viewIds)
                        {
                            DBObject view;
                            try { view = tr.GetObject(viewId, OpenMode.ForRead); }
                            catch
                            {
                                inventory.MarkUnreadable(alignmentName);
                                continue;
                            }
                            CollectOwned(tr, view, inventory, alignmentName,
                                sampleLineIdentity);
                        }
                    }
                }
            }

            return inventory;
        }

        private static void CollectOwned(
            Transaction tr,
            DBObject obj,
            OwnedSectionInventory inventory,
            string alignmentName,
            string? parentSampleLineIdentity)
        {
            OwnershipMetadata? meta;
            string objectIdentity;
            try
            {
                meta = SectionOwnershipService.Read(tr, obj);
                objectIdentity = obj.Handle.ToString();
            }
            catch
            {
                inventory.MarkUnreadable(alignmentName);
                return;
            }
            if (meta == null || string.IsNullOrWhiteSpace(meta.LogicalKey)) return;

            var exact = new SectionOwnedStateLogic.ObjectEvidence(
                objectIdentity,
                meta.Feature,
                meta.Role,
                meta.ProjectProfileId,
                meta.InputFingerprint,
                LogicalKeyIsExact: true,
                parentSampleLineIdentity);
            inventory.Add(meta.LogicalKey, exact);

            // v1 logical keys embedded the mutable CL file hash.  Keep the object
            // discoverable under the stable key, but mark that alias non-exact so it
            // can only plan UPDATE/migration — never a false UNCHANGED.
            if (string.Equals(meta.Feature, "sections", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(meta.Role, "sample-line", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(meta.Role, "section-view", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(meta.ProjectProfileId) &&
                !string.IsNullOrWhiteSpace(meta.SourceClHandle))
            {
                var stableKey = LogicalKeys.ForSectionObject(
                    meta.ProjectProfileId, string.Empty, meta.SourceClHandle,
                    alignmentName, "section");
                if (!string.Equals(stableKey, meta.LogicalKey, StringComparison.Ordinal))
                    inventory.Add(stableKey, exact with { LogicalKeyIsExact = false });
            }
        }

        internal static IEnumerable<ObjectId> EnumerateSectionViewIds(CivilDb.SampleLine sampleLine)
        {
            foreach (ObjectId id in sampleLine.GetSectionViewIds())
            {
                if (id.IsNull || id.IsErased)
                    throw new InvalidDataException(
                        $"Sample line {sampleLine.Handle} contains an invalid SectionView id.");
                yield return id;
            }
        }

        /// <summary>
        /// Strict variant for MUTATION paths: a read failure is reported, never
        /// silently treated as "no children" (an erased parent would take a foreign
        /// view with it — review, 02/09).
        /// </summary>
        internal static bool TryEnumerateSectionViewIds(
            CivilDb.SampleLine sampleLine, out List<ObjectId> ids, out string? error)
        {
            ids = new List<ObjectId>();
            error = null;
            try
            {
                foreach (ObjectId id in sampleLine.GetSectionViewIds())
                {
                    if (id.IsNull || id.IsErased)
                    {
                        error = $"Sample line {sampleLine.Handle} contains a null/erased SectionView id.";
                        ids.Clear();
                        return false;
                    }
                    ids.Add(id);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static List<(string Name, string Handle)> ListSurfaces(Transaction tr, CivilDocument doc)
        {
            var list = new List<(string, string)>();
            foreach (ObjectId id in doc.GetSurfaceIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Surface s)
                    throw new InvalidDataException(
                        $"Civil surface inventory returned unreadable id {id}.");
                list.Add((s.Name, s.Handle.ToString()));
            }
            return list;
        }

        private static List<(string Name, string Handle)> ListCorridors(Transaction tr, CivilDocument doc)
        {
            var list = new List<(string, string)>();
            foreach (ObjectId id in doc.CorridorCollection)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Corridor c)
                    throw new InvalidDataException(
                        $"Civil corridor inventory returned unreadable id {id}.");
                list.Add((c.Name, c.Handle.ToString()));
            }
            return list;
        }

        /// <summary>
        /// Every utility-bearing source actually present in the drawing, regardless of
        /// what the profile configures. Pipe and pressure networks are natively
        /// sampleable as Section Sources; anything else needs an adapter and is
        /// reported as such rather than quietly dropped.
        /// </summary>
        internal static List<DiscoveredUtility> DiscoverUtilities(Transaction tr, CivilDocument doc, Database db)
        {
            var list = new List<DiscoveredUtility>();

            foreach (ObjectId id in doc.GetPipeNetworkIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Network n)
                    throw new InvalidDataException(
                        $"Civil pipe-network inventory returned unreadable id {id}.");
                var parts = n.GetPipeIds().Count + n.GetStructureIds().Count;
                list.Add(new DiscoveredUtility
                {
                    Name = n.Name,
                    Kind = "pipe-network",
                    Handle = n.Handle.ToString(),
                    NativelySampleable = true,
                    PartCount = parts,
                });
            }

            // Pressure networks live in AeccPressurePipesMgd and are Entities, so they
            // are enumerated from model space rather than through CivilDocument (which
            // exposes no accessor for them in this release).
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in ms)
            {
                var opened = tr.GetObject(id, OpenMode.ForRead);
                if (opened is not CivilDb.PressurePipeNetwork pn) continue;
                if (!seen.Add(pn.Name)) continue;
                var parts = pn.GetPipeIds().Count + pn.GetFittingIds().Count +
                            pn.GetAppurtenanceIds().Count;
                list.Add(new DiscoveredUtility
                {
                    Name = pn.Name,
                    Kind = "pressure-network",
                    Handle = pn.Handle.ToString(),
                    NativelySampleable = true,
                    PartCount = parts,
                });
            }

            return list;
        }

        private static List<(string Name, string Handle)> ListPipeNetworks(Transaction tr, CivilDocument doc)
        {
            var list = new List<(string, string)>();
            foreach (ObjectId id in doc.GetPipeNetworkIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Network n)
                    throw new InvalidDataException(
                        $"Civil pipe-network inventory returned unreadable id {id}.");
                list.Add((n.Name, n.Handle.ToString()));
            }
            return list;
        }
    }
}
