using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// APPLY (plan §7.13): creates/updates ONLY approved READY records as real native
    /// Civil objects — SampleLineGroup, SampleLine with explicit CL geometry, sampled
    /// Section sources, SectionView — inside a document lock and a single atomic
    /// transaction. A runtime failure rolls the whole batch back; a required object
    /// that cannot be created is FAILURE, never success-with-skip (§1.4 boundary).
    /// </summary>
    public sealed class SectionApplyService
    {
        /// <summary>Vertical stack spacing between placed section views, meters (provisional layout).</summary>
        public double ViewSpacing { get; init; } = 60.0;

        /// <summary>
        /// Stage trail for hang diagnosis. Set by <see cref="Apply"/> / passed to
        /// <see cref="ApplyCore"/>; every Civil API call that can block is bracketed.
        /// </summary>
        private StageLog? _log;

        public SectionApplyResult Apply(
            Document doc,
            CivilDocument civilDoc,
            ProjectProfile profile,
            SectionPlan plan,
            IReadOnlyCollection<string>? approvedRecordIds = null,
            string? runId = null,
            StageLog? log = null,
            string? profileHash = null)
        {
            _log = log;
            var result = new SectionApplyResult
            {
                RunId = runId ?? RunManifest.NewRunId("sections", "apply"),
            };

            // A plan belongs to the drawing and profile it was computed from. Applying
            // it anywhere else would write one project's sections into another.
            var staleReason = SectionPlanLogic.ScopeStaleReason(
                plan, DrawingScopeIdentity.For(doc), profile, profileHash);
            if (staleReason != null)
            {
                BlockForStaleScope(result, staleReason, log);
                return result;
            }

            var integrityFindings = SectionInputIntegrityService.ValidateCurrent(
                doc.Database, plan, plan.SourceDatabaseRevision, "APPLY", profile);
            if (SectionInputIntegrityService.HasPlanningIntegrityBlocker(plan) ||
                integrityFindings.Count > 0)
            {
                BlockForInputIntegrity(result, integrityFindings, log);
                return result;
            }

            var unresolved = SectionPlanLogic.UnresolvedBatchRecords(plan);
            if (unresolved.Count > 0)
            {
                foreach (var record in plan.Records)
                {
                    result.Records.Add(new SectionApplyRecordResult
                    {
                        RecordId = record.RecordId,
                        LogicalKey = record.LogicalKey,
                        ActionTaken = record.Action,
                        Status = SectionPlanLogic.HasValidExplicitExclusion(record)
                            ? DeliveryStatus.Verified
                            : record.Status,
                    });
                }
                result.Status = DeliveryStatus.Blocked;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.IncompleteBatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "ההחלה סורבה — לא כל רשומות ה-CL הוכרעו",
                    Message = $"{unresolved.Count} מתוך {plan.Records.Count} רשומות עדיין דורשות בדיקה. " +
                              "כל רשומה חייבת להיות מוכנה או מוחרגת במפורש עם מאשר וסיבה.",
                    AffectedRecordIds = unresolved.Select(r => r.RecordId).ToList(),
                });
                return result;
            }

            var targets = SelectTargets(plan, approvedRecordIds);

            foreach (var skipped in plan.Records.Where(r => !targets.Contains(r)))
            {
                result.Records.Add(new SectionApplyRecordResult
                {
                    RecordId = skipped.RecordId,
                    LogicalKey = skipped.LogicalKey,
                    ActionTaken = skipped.Action,
                    // An explicit exclusion is its own signed evidence.  Merely seeing
                    // Action=Unchanged in PLAN is not APPLY evidence and must never be
                    // promoted to Verified without re-reading the actual Civil objects.
                    Status = SectionPlanLogic.HasValidExplicitExclusion(skipped)
                        ? DeliveryStatus.Verified : skipped.Status,
                });
            }

            if (targets.Count == 0)
            {
                var unchanged = plan.Records
                    .Where(record => record.Action == PlanAction.Unchanged)
                    .Select(record => record.RecordId)
                    .ToList();
                if (unchanged.Count > 0)
                {
                    BlockForNoMutationEvidence(result, unchanged, log);
                    return result;
                }

                // All records are signed explicit exclusions.  No drawing object is
                // claimed to have been applied or verified; the revision only binds
                // the exclusion decision bundle to the current database state.
                result.Committed = true;
                result.PostApplyDatabaseRevision =
                    DrawingRevisionTracker.Capture(doc.Database);
                result.Status = DeliveryStatusRules.Aggregate(result.Records.Select(r => r.Status).ToList());
                return result;
            }

            var db = doc.Database;
            log?.Begin("apply.lock_document");
            var batchFailed = false;
            using (doc.LockDocument())
            {
                log?.End("apply.lock_document");
                log?.Begin("apply.start_transaction");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    log?.End("apply.start_transaction");
                    batchFailed = !ApplyCore(
                        tr, db, civilDoc, profile, DrawingScopeIdentity.For(doc), profileHash,
                        plan, targets, result, log);

                    if (batchFailed)
                    {
                        log?.Begin("apply.abort_transaction");
                        tr.Abort();
                        log?.End("apply.abort_transaction");
                    }
                    else
                    {
                        log?.Begin("apply.commit_transaction");
                        tr.Commit();
                        log?.End("apply.commit_transaction");
                    }
                }
            }

            if (batchFailed)
            {
                result.Committed = false;
                result.Status = DeliveryStatus.Failed;
                // Nothing survived the abort: no record may still read "Applied".
                foreach (var record in result.Records) record.Status = DeliveryStatus.Failed;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SectionViewCreateFailed,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "APPLY batch rolled back — no partial section infrastructure was left in the drawing",
                });
                return result;
            }

            // No database fingerprint/evidence may run while a committed Civil
            // transaction (or its document lock) is still alive.  Dispose failure
            // propagates before this authoritative post-state can be published.
            result.Committed = true;
            result.PostApplyDatabaseRevision =
                DrawingRevisionTracker.Capture(db);
            result.Status = DeliveryStatusRules.Aggregate(result.Records.Select(r => r.Status).ToList());
            return result;
        }

        /// <summary>
        /// Applies one explicitly selected record without relaxing the legacy strict
        /// batch route.  The selected record must independently be READY, have a
        /// complete presentation contract and belong by reference to the fresh PLAN.
        /// Omitted records are not copied into the result and therefore cannot be
        /// mistaken for applied/verified work.
        /// </summary>
        public SectionApplyResult ApplySelected(
            Document doc,
            CivilDocument civilDoc,
            ProjectProfile profile,
            SectionPlan plan,
            string selectedRecordId,
            string? runId = null,
            StageLog? log = null,
            string? profileHash = null) =>
            ApplySelectedTransaction(doc, civilDoc, profile, plan, selectedRecordId, runId, log, profileHash, null);

        internal SectionApplyResult RebuildSelected(
            Document doc, CivilDocument civilDoc, ProjectProfile profile, SectionPlan plan,
            string selectedRecordId, SectionSelectedRebuildPolicy.Authority authority,
            StageLog? log, string? profileHash) =>
            ApplySelectedTransaction(doc, civilDoc, profile, plan, selectedRecordId,
                RunManifest.NewRunId("sections", "apply-selected"), log, profileHash, authority);

        private SectionApplyResult ApplySelectedTransaction(
            Document doc, CivilDocument civilDoc, ProjectProfile profile, SectionPlan plan,
            string selectedRecordId, string? runId, StageLog? log, string? profileHash,
            SectionSelectedRebuildPolicy.Authority? rebuild)
        {
            _log = log;
            var result = new SectionApplyResult
            {
                RunId = runId ?? RunManifest.NewRunId("sections", "apply-selected"),
                Scope = "selected-record",
                SelectedRecordId = selectedRecordId,
            };

            var target = SelectSingleTarget(plan, selectedRecordId);
            var db = doc.Database;
            log?.Begin("apply-selected.lock_document", selectedRecordId);
            var failed = false;
            using (doc.LockDocument())
            {
                log?.End("apply-selected.lock_document");
                log?.Begin("apply-selected.start_transaction");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    log?.End("apply-selected.start_transaction");
                    failed = !ApplySelectedCore(
                        tr, db, civilDoc, profile, DrawingScopeIdentity.For(doc), profileHash,
                        plan, target, result, log, rebuild);
                    if (failed)
                    {
                        log?.Begin("apply-selected.abort_transaction");
                        tr.Abort();
                        log?.End("apply-selected.abort_transaction");
                    }
                    else
                    {
                        log?.Begin("apply-selected.commit_transaction");
                        tr.Commit();
                        log?.End("apply-selected.commit_transaction");
                    }
                }
            }

            if (failed)
            {
                result.Committed = false;
                result.Status = DeliveryStatus.Failed;
                foreach (var record in result.Records) record.Status = DeliveryStatus.Failed;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SectionViewCreateFailed,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "החלת החתך הנבחר בוטלה ב-rollback מלא — שאר החתכים לא שונו",
                    AffectedRecordIds = { selectedRecordId },
                });
                return result;
            }

            result.Committed = true;
            result.PostApplyDatabaseRevision = DrawingRevisionTracker.Capture(db);
            result.Status = DeliveryStatusRules.Aggregate(
                result.Records.Select(record => record.Status).ToList());
            return result;
        }

        private void ApplyOne(
            Transaction tr,
            Database db,
            CivilDocument civilDoc,
            ProjectProfile profile,
            SectionPlan plan,
            SectionPlanRecord record,
            SectionApplyRecordResult recordResult,
            Point3d viewOrigin,
            ObjectId builtInPresentationStyleId,
            IReadOnlyList<SectionPlanRecord> permittedGroupRecords,
            bool batchGroupScope,
            string applyRunId,
            SectionGeometryCollector.CollectResult collected)
        {
            var alignmentId = FindAlignment(tr, civilDoc, record.SelectedAlignment!)
                ?? throw new InvalidOperationException($"Alignment '{record.SelectedAlignment}' disappeared between PLAN and APPLY");

            if (record.ManualSectionReuse != null)
            {
                ApplyExistingManualSection(
                    tr, db, civilDoc, record, recordResult);
                return;
            }

            // ---------------------------------------------------------------- SLG
            var slgName = $"MCD-{record.SelectedAlignment}-{plan.ProjectProfileId}";
            var slgKey = LogicalKeys.ForSectionObject(plan.ProjectProfileId,
                record.Cl.SourceDrawingHash, "GROUP", record.SelectedAlignment!, "sample-line-group");

            _log?.Begin("apply.find_or_create_group", slgName);
            var slgId = FindOrCreateGroup(tr, alignmentId, slgName, slgKey, plan, record, recordResult,
                allowMetadataRewrite: batchGroupScope);
            var slg = (CivilDb.SampleLineGroup)tr.GetObject(slgId, OpenMode.ForWrite);
            recordResult.Handles.SampleLineGroup = slg.Handle.ToString();
            _log?.End("apply.find_or_create_group", $"handle={slg.Handle}");

            // Enable required sampled sources BEFORE creating the sample line so the
            // new sample line samples them. A required source that cannot be enabled
            // is a hard failure.
            _log?.Begin("apply.enable_sources",
                string.Join(",", record.PlannedSources.Where(s => s.PlannedState == "sampled").Select(s => s.SourceName)));
            var groupSampling = SectionPlanLogic.ExpectedGroupSampling(plan, record.SelectedAlignment!);
            // A selected APPLY may converge a group it has to itself; a group that other
            // sample lines share may not change at all (every toggle re-samples them).
            var groupContainsForeignWork = GroupIsSharedWithForeignWork(
                tr, slg, permittedGroupRecords, plan.ProjectProfileId);
            var samplingMode = batchGroupScope
                ? groupContainsForeignWork
                    ? SamplingReconciliationLogic.Mode.BatchSharedGroup
                    : SamplingReconciliationLogic.Mode.Batch
                : groupContainsForeignWork
                    ? SamplingReconciliationLogic.Mode.SelectedSharedGroup
                    : SamplingReconciliationLogic.Mode.SelectedExclusiveGroup;
            var sourceOutcome = SectionSourceService.EnableSampling(
                tr, slg, record, groupSampling.Names, groupSampling.RequiredNames, _log,
                mode: samplingMode);
            recordResult.Findings.AddRange(sourceOutcome.Findings);
            recordResult.DisabledSources.AddRange(sourceOutcome.Disabled);
            _log?.End("apply.enable_sources");

            // ------------------------------------------------- replace on update
            if (record.Action is PlanAction.Update or PlanAction.Replace or PlanAction.Unchanged)
            {
                _log?.Begin("apply.remove_previous_tool_objects", record.LogicalKey);
                RemoveToolOwnedSectionObjects(tr, slg, record, plan.ProjectProfileId);
                _log?.End("apply.remove_previous_tool_objects");
                recordResult.ActionTaken = PlanAction.Replace;
            }

            // ---------------------------------------------------------------- SL
            // Explicit CL geometry is the governing input (plan §1.4): the 2027 API
            // takes the exact WCS points at creation time.
            // The Civil object NAME must be unique within the group by construction.
            // The station is unique per alignment; the CL label is not (6422's CL.dwg
            // carries "291.50" twice and Civil refused the second with "Sample line name
            // should not duplicate", aborting the whole batch - 2026-08-19). So the
            // station is the identity and the label is a readable suffix.
            var stationPart = record.Station is { } sta ? $"STA-{sta:F1}" : $"CL-{record.Cl.SourceHandle}";
            var label = record.Cl.CandidateSectionNumber;
            var sectionLabel = string.IsNullOrWhiteSpace(label) ? stationPart : $"{stationPart}-{label}";
            var slName = $"MCD-{sectionLabel}";
            // Two CLs at the same station on the same alignment would still collide on
            // name (duplicate lines in a CL drawing are common). A name clash is not an
            // engineering failure, so it must never abort the batch: suffix and go on,
            // and say so in the record. Ownership + logical key carry identity, not the name.
            slName = UniqueSampleLineName(tr, slgId, slName, recordResult);
            var viewLabel = slName.StartsWith("MCD-", StringComparison.Ordinal) ? slName.Substring(4) : slName;

            ObjectId slId;
            _log?.Begin("apply.sampleline_create", slName);
            try
            {
                var points = new Point2dCollection
                {
                    new Point2d(record.Cl.WcsEndpoints[0], record.Cl.WcsEndpoints[1]),
                    new Point2d(record.Cl.WcsEndpoints[2], record.Cl.WcsEndpoints[3]),
                };
                slId = CivilDb.SampleLine.Create(slName, slgId, points);
            }
            catch (Exception ex)
            {
                _log?.Fail("apply.sampleline_create", ex);
                throw new InvalidOperationException(
                    $"SampleLine.Create failed for '{slName}': {ex.Message} (SEC-SAMPLELINE-CREATE-FAILED)", ex);
            }

            var sampleLine = (CivilDb.SampleLine)tr.GetObject(slId, OpenMode.ForWrite);
            ApplyPlannedSampleLineStyle(tr, civilDoc, sampleLine, record);
            recordResult.Handles.SampleLine = sampleLine.Handle.ToString();
            _log?.End("apply.sampleline_create", $"handle={sampleLine.Handle}");

            var meta = new OwnershipMetadata
            {
                Feature = "sections",
                Role = "sample-line",
                ProjectProfileId = plan.ProjectProfileId,
                RunId = applyRunId,
                SourceClDrawingHash = record.Cl.SourceDrawingHash,
                SourceClHandle = record.Cl.SourceHandle,
                LogicalKey = record.LogicalKey!,
                InputFingerprint = record.InputFingerprint!,
                CreatedByToolVersion = SectionPlanService.ToolVersion,
            };
            SectionOwnershipService.Write(tr, sampleLine, meta);

            // --------------------------------------------------------------- view
            ObjectId viewId;
            _log?.Begin("apply.sectionview_create", $"MCDV-{viewLabel} @ {viewOrigin.X:F1},{viewOrigin.Y:F1}");
            try
            {
                viewId = CivilDb.SectionView.Create($"MCDV-{viewLabel}", slId, viewOrigin);
            }
            catch (Exception ex)
            {
                _log?.Fail("apply.sectionview_create", ex);
                throw new InvalidOperationException(
                    $"SectionView.Create failed for '{slName}': {ex.Message} (SEC-SECTIONVIEW-CREATE-FAILED)", ex);
            }

            var view = (CivilDb.SectionView)tr.GetObject(viewId, OpenMode.ForWrite);
            _log?.End("apply.sectionview_create", $"handle={view.Handle}");

            _log?.Begin("apply.apply_style");
            TryApplyStyle(
                tr, civilDoc, view, record, recordResult,
                builtInPresentationStyleId);
            _log?.End("apply.apply_style");
            recordResult.Handles.SectionView = view.Handle.ToString();

            // Range + bands change the view's real extents, so they must exist BEFORE
            // the batch is arranged - 1.2.0's first live run attached bands after the
            // grid was measured and the band boxes invaded the neighbouring view.
            _log?.Begin("apply.view_dress");
            var reviewedSurfacePair = SectionSurfacePairPlanLogic.RequireExplicit(record, profile,
                db.FingerprintGuid);
            // SEC-B3: the band must contain every projected utility that carries a
            // proven elevation; the same crossings are drawn by the decorator below.
            var projectedUtilityElevations = SectionCutGeometry
                .CrossingsFor(collected.Utilities, SectionCutGeometry.RequireFrame(record))
                .Where(crossing => crossing.Elevation.HasValue && double.IsFinite(crossing.Elevation.Value))
                .Select(crossing => crossing.Elevation!.Value)
                .ToList();
            SectionDecorationService.ClampElevationRange(tr, view, sampleLine, recordResult,
                profile.Sections.Projection.ExistingSurfacePatterns,
                reviewedSurfacePair == null ? null : new SectionSourceSelectionLogic.Identity(
                    reviewedSurfacePair.ExistingName!, reviewedSurfacePair.ExistingHandle!),
                record, projectedUtilityElevations);
            // A band set stacks an elevation row PER SAMPLED SURFACE — ten rows of
            // 285.13/285.12/... under every section (engineer, 31/08). Attached only
            // when the profile names one; the tool draws its own offsets row + datum.
            if (!string.IsNullOrWhiteSpace(profile.Sections.Styles.BandSetStyle))
                SectionDecorationService.TryAttachBandSet(
                    tr, civilDoc, view, profile, recordResult);
            else
                SectionDecorationService.ClearBandSet(
                    tr, civilDoc, view, recordResult);
            _log?.End("apply.view_dress");

            SectionOwnershipService.Write(tr, view, new OwnershipMetadata
            {
                Feature = "sections",
                Role = "section-view",
                ProjectProfileId = plan.ProjectProfileId,
                RunId = applyRunId,
                SourceClDrawingHash = record.Cl.SourceDrawingHash,
                SourceClHandle = record.Cl.SourceHandle,
                LogicalKey = record.LogicalKey!,
                InputFingerprint = record.InputFingerprint!,
                CreatedByToolVersion = SectionPlanService.ToolVersion,
            });

            recordResult.Handles.Sections.AddRange(
                ReadSectionHandlesStrict(tr, sampleLine));

            // Evidence comes from reading the group back, never from what we intended.
            recordResult.SampledSources.AddRange(
                SectionSourceService.SampledSourceNames(tr, slg));

            recordResult.Status = DeliveryStatus.Applied;
        }

        /// <summary>
        /// Creates the targets inside a CALLER-OWNED transaction (used by the MahodAI
        /// tool route so the platform's P0-01 commit gate governs the commit). Returns
        /// false when the batch failed and the caller must abort the transaction.
        /// </summary>
        public bool ApplyCore(
            Transaction tr,
            Database db,
            CivilDocument civilDoc,
            ProjectProfile profile,
            string currentDrawingIdentity,
            string? currentProfileHash,
            SectionPlan plan,
            IReadOnlyList<SectionPlanRecord> targets,
            SectionApplyResult result,
            StageLog? log = null) =>
            ApplyCoreScoped(
                tr, db, civilDoc, profile, currentDrawingIdentity,
                currentProfileHash, plan, targets, result, log,
                selectedScope: false);

        internal bool ApplySelectedCore(
            Transaction tr,
            Database db,
            CivilDocument civilDoc,
            ProjectProfile profile,
            string currentDrawingIdentity,
            string? currentProfileHash,
            SectionPlan plan,
            SectionPlanRecord target,
            SectionApplyResult result,
            StageLog? log = null,
            SectionSelectedRebuildPolicy.Authority? rebuild = null) =>
            ApplyCoreScoped(
                tr, db, civilDoc, profile, currentDrawingIdentity,
                currentProfileHash, plan, new[] { target }, result, log,
                selectedScope: true, rebuild);

        private bool ApplyCoreScoped(
            Transaction tr,
            Database db,
            CivilDocument civilDoc,
            ProjectProfile profile,
            string currentDrawingIdentity,
            string? currentProfileHash,
            SectionPlan plan,
            IReadOnlyList<SectionPlanRecord> targets,
            SectionApplyResult result,
            StageLog? log,
            bool selectedScope,
            SectionSelectedRebuildPolicy.Authority? rebuild = null)
        {
            _log = log ?? _log;

            // This is also the mutation boundary used by the AI tool.  The direct
            // workflow checked the published PLAN before entering it, but the AI
            // caller previously reached Civil setters with an in-memory plan whose
            // authoritative artifact could be absent or altered. Prove the exact
            // published plan before every database read that can lead to a write.
            try
            {
                SectionsWorkflowService.RequirePlanEvidence(plan);
            }
            catch (Exception ex)
            {
                result.Status = DeliveryStatus.Failed;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.PlanEvidenceInvalid,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "ההחלה סורבה — ראיות ה-PLAN החתומות חסרות או השתנו",
                    Message = ex.Message,
                    AffectedRecordIds = targets.Select(record => record.RecordId).ToList(),
                });
                _log?.Info("apply.plan_evidence refused: " + ex.Message);
                return false;
            }

            var annotationInventory =
                SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db);

            // ApplyCore is the caller-owned-transaction route used by AI. Keep the
            // scope check inside this public boundary so no caller can accidentally
            // bypass the direct workflow's drawing/profile guard.
            var staleReason = SectionPlanLogic.ScopeStaleReason(
                plan, currentDrawingIdentity, profile, currentProfileHash);
            if (staleReason != null)
            {
                BlockForStaleScope(result, staleReason, _log);
                return false;
            }

            var integrityFindings = SectionInputIntegrityService.ValidateCurrent(
                db, plan, plan.SourceDatabaseRevision, "APPLY", profile);
            var selectedPlanningBlockers = selectedScope && targets.Count == 1
                ? SectionInputIntegrityService.SelectedApplyPlanningBlockers(plan, targets[0])
                : Array.Empty<DeliveryFinding>();
            var planningBlocked = selectedScope && targets.Count == 1
                ? SectionInputIntegrityService.HasSelectedApplyPlanningIntegrityBlocker(
                    plan, targets[0])
                : SectionInputIntegrityService.HasPlanningIntegrityBlocker(plan);
            if (planningBlocked ||
                integrityFindings.Count > 0)
            {
                BlockForInputIntegrity(result,
                    integrityFindings.Concat(selectedPlanningBlockers).ToList(), _log);
                return false;
            }

            if (selectedScope)
            {
                if (targets.Count != 1 ||
                    !ReferenceEquals(SelectSingleTarget(plan, targets[0].RecordId), targets[0]))
                {
                    BlockForIncompleteBatch(result, plan, targets, _log,
                        "החלת חתך נבחר סורבה — היעד אינו רשומה יחידה מתוך ה-PLAN הטרי");
                    return false;
                }
                var target = targets[0];
                if (rebuild != null)
                {
                    try { SectionSelectedRebuildPolicy.RequireLiveOwnership(db, tr, rebuild, plan, target); }
                    catch (Exception ex)
                    {
                        BlockForIncompleteBatch(result, plan, targets, _log,
                            "בנייה מחדש של חתך נבחר סורבה — " + ex.Message);
                        return false;
                    }
                }
                if (target.Status != DeliveryStatus.Ready ||
                    !target.PresentationCoverage.Complete ||
                    target.PresentationCoverage.UnresolvedSpans.Count != 0 ||
                    target.TrafficDirections.Any(direction => !direction.IsResolved) ||
                    !(target.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace ||
                      rebuild != null && target.Action == PlanAction.Unchanged))
                {
                    BlockForIncompleteBatch(result, plan, new[] { target }, _log,
                        "החלת חתך נבחר סורבה — החתך אינו READY עם חוזה תצוגה וכיוונים מלאים");
                    return false;
                }
            }
            else
            {
                // ApplyCore is also the caller-owned-transaction route used by AI.
                // Keep the complete-batch gate inside this public boundary.
                var unresolved = SectionPlanLogic.UnresolvedBatchRecords(plan);
                if (unresolved.Count > 0)
                {
                    BlockForIncompleteBatch(result, plan, unresolved, _log);
                    return false;
                }

                var expectedTargets = SelectTargets(plan, approvedRecordIds: null);
                var expectedIds = expectedTargets.Select(r => r.RecordId)
                    .ToHashSet(StringComparer.Ordinal);
                if (!expectedIds.SetEquals(targets.Select(r => r.RecordId)))
                {
                    var omitted = expectedTargets.Where(r =>
                        targets.All(t => t.RecordId != r.RecordId)).ToList();
                    BlockForIncompleteBatch(result, plan, omitted, _log,
                        "ההחלה סורבה — רשימת היעד אינה האצווה המלאה שנקבעה ב-PLAN");
                    return false;
                }

                // The caller-owned (AI) route must obey the same no-op evidence rule
                // as the direct workflow.  An empty target set containing Unchanged
                // rows has no handles or object read-back and cannot become an
                // authoritative APPLY merely because the executor commits an empty
                // transaction.
                if (targets.Count == 0)
                {
                    var unchanged = plan.Records
                        .Where(record => record.Action == PlanAction.Unchanged)
                        .Select(record => record.RecordId)
                        .ToList();
                    if (unchanged.Count > 0)
                    {
                        BlockForNoMutationEvidence(result, unchanged, _log);
                        return false;
                    }
                }
            }

            // Every current PLAN carries exact finite placement evidence. APPLY must
            // consume that evidence verbatim in both scopes; recomputing EXTMAX here
            // dirties global drawing state and can disagree with what the engineer
            // previewed. Legacy plans without placement evidence are stale and must
            // be regenerated rather than guessed.
            if (targets.Any(record =>
                    record.PlannedLayoutPosition is not { Length: 2 } p ||
                    !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
            {
                BlockForIncompleteBatch(result, plan, targets, _log,
                    "ההחלה סורבה — ל-PLAN אין מיקום פריסה סופי וסופי-מספרים; יש להריץ תכנון מחדש");
                return false;
            }

            // Recovery is a mutation, so it runs only AFTER the exact published
            // PLAN, current drawing/profile/source and complete target-scope gates.
            // It is in the same caller-owned transaction as replacement: any later
            // style/geometry/annotation failure restores the original registry too.
            if (!annotationInventory.IsValid)
            {
                try
                {
                    var plannedRepairTargets = targets.Where(record => record.Findings.Any(f =>
                        f.Code == SectionFindingCodes.AnnotationRegistryRepairable)).ToList();
                    var repairKeys = SectionPlanService.EligibleDeadAnnotationRecoveryKeys(
                        tr, db, civilDoc, profile.ProfileId, plannedRepairTargets);
                    var repaired = SectionAnnotationRegistry.RepairDeadEntries(tr, db, repairKeys);
                    annotationInventory = SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db);
                    if (!annotationInventory.IsValid)
                        throw new InvalidOperationException(string.Join(" | ", annotationInventory.Problems));
                    if (repaired > 0)
                        result.Findings.Add(new DeliveryFinding
                        {
                            Code = SectionFindingCodes.AnnotationRegistryRepaired,
                            Domain = SectionPlanLogic.Domain,
                            Severity = FindingSeverity.Info,
                            Title = "הפניות מתות הוסרו בטרנזקציה — התיקון יישמר רק אם עדכון החתך כולו יצליח",
                            Message = $"dead_primary_entries={repaired}; logical_keys={string.Join(",", repairKeys)}; " +
                                "registry-only cleanup; no live entity erased by recovery; strict inventory revalidated; pending transaction commit",
                            AffectedRecordIds = plannedRepairTargets.Select(r => r.RecordId).ToList(),
                        });
                }
                catch (Exception ex)
                {
                    result.Status = DeliveryStatus.Failed;
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.AnnotationInventoryConflict,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "ההחלה סורבה — אין תיקון בטוח ומוגבל לרישום הערות היעד",
                        Message = ex.Message,
                        AffectedRecordIds = targets.Select(record => record.RecordId).ToList(),
                    });
                    return false;
                }
            }
            var layoutOrigin = targets.Count == 0
                ? (X: 0.0, Y: 0.0)
                : (
                    X: targets.Min(record => record.PlannedLayoutPosition![0]),
                    Y: targets.Max(record => record.PlannedLayoutPosition![1]));
            var builtInPresentationStyleId = ObjectId.Null;
            try
            {
                var needsBuiltInStyle = targets.Any(record =>
                    record.ManualSectionReuse == null &&
                    record.PlannedStyles.TryGetValue("section_view_style", out var styleName) &&
                    string.Equals(styleName, SectionViewPresentationStyleService.StyleName,
                        StringComparison.OrdinalIgnoreCase));
                if (needsBuiltInStyle)
                    builtInPresentationStyleId =
                        SectionViewPresentationStyleService.Ensure(
                            tr, civilDoc, allowModify: !selectedScope);
            }
            catch (Exception ex)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.StyleMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "סגנון החתך הנקי לא הוכן — ההחלה בוטלה ללא שינוי חלקי",
                    Message = ex.Message,
                });
                return false;
            }
            int placed = 0;

            // SEC-B3 (review of 1.3.9): the projection sources are read ONCE, before
            // any view is created, so each managed view's elevation band can contain
            // every projected utility with a proven elevation. The decorator below
            // consumes this same read; nothing in between changes source geometry.
            SectionGeometryCollector.CollectResult collected;
            try
            {
                _log?.Begin("apply.collect_projection_sources");
                collected = profile.Sections.Projection.Enabled
                    ? SectionGeometryCollector.Collect(tr, db, profile, _log)
                    : new SectionGeometryCollector.CollectResult(new(), new(), new(), new());
                _log?.End("apply.collect_projection_sources",
                    $"utilities={collected.Utilities.Count} marks={collected.PlanMarks.Count}");
            }
            catch (Exception ex)
            {
                _log?.Info("apply.collect_projection_sources failed: " + ex);
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.LayoutUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "קריאת מקורות ההטלה (מערכות וסימוני תכנית) נכשלה — ההחלה בוטלה ללא שינוי חלקי",
                    Message = ex.Message,
                });
                return false;
            }

            foreach (var record in targets)
            {
                _log?.Info($"apply.record {placed + 1}/{targets.Count} {record.RecordId} " +
                           $"alignment={record.SelectedAlignment} station={record.Station}");
                var recordResult = new SectionApplyRecordResult
                {
                    RecordId = record.RecordId,
                    LogicalKey = record.LogicalKey,
                    ActionTaken = record.Action,
                };
                result.Records.Add(recordResult);

                try
                {
                    // PLAN already computed a deterministic grid position; APPLY honours
                    // it so the sheet an engineer previewed is the sheet they get. The
                    // sequential fallback only covers a record planned before layout.
                    var p = record.PlannedLayoutPosition!;
                    var origin = new Point3d(p[0], p[1], 0);

                    ApplyOne(tr, db, civilDoc, profile, plan, record, recordResult,
                        origin, builtInPresentationStyleId, targets,
                        batchGroupScope: !selectedScope, applyRunId: result.RunId,
                        collected: collected);
                    placed++;
                }
                catch (Exception ex)
                {
                    recordResult.Status = DeliveryStatus.Failed;
                    recordResult.Findings.Add(new DeliveryFinding
                    {
                        Code = FindingCodeFromException(ex),
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = $"ההחלה נכשלה לרשומה {record.RecordId}",
                        Message = ex.ToString(),
                        AffectedRecordIds = { record.RecordId },
                    });
                    return false; // atomic batch: caller aborts, nothing half-created survives
                }
            }

            // Views exist now: arrange them by their real size so none overlaps.
            try
            {
                // A reused manual SectionView keeps its author-chosen location.  Only
                // views created/owned by Mahod participate in the managed sheet grid.
                if (selectedScope)
                {
                    // PLAN already reserved a deterministic position in the complete
                    // sheet. Repacking one view as a one-item grid could overlap
                    // untouched sections, so selected APPLY keeps that exact origin.
                    _log?.Info("apply-selected.arrange_views skipped; planned position retained");
                }
                else
                {
                    var arrangedTargets = targets.Where(r => r.ManualSectionReuse == null).ToList();
                    var moved = ArrangeCreatedViews(
                        tr, db, profile, arrangedTargets, result, layoutOrigin, _log);
                    _log?.Info($"apply.arrange_views moved={moved}");
                }
            }
            catch (Exception ex)
            {
                _log?.Info("apply.arrange_views failed: " + ex);
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.LayoutUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "סידור החתכים לא הושלם לכל הרשומות — ההחלה בוטלה ללא שינוי חלקי",
                    Message = ex.Message,
                });
                return false;
            }

            // The views sit at their final positions — now dress them: existing/design
            // styles, the axis mark, projected utilities and plan marks, band set.
            // Presentation is part of the promised deliverable. Any incomplete
            // decoration fails this same caller-owned transaction so geometry and
            // annotations commit together or roll back together.
            try
            {
                _log?.Begin("apply.decorate");
                var outcome = SectionDecorationService.Decorate(
                    tr, db, civilDoc, profile, targets, result, collected, _log);
                _log?.End("apply.decorate",
                    $"views={outcome.Decorated} utilities={outcome.UtilitiesProjected} marks={outcome.MarksDrawn}");

                if (outcome.Decorated != targets.Count ||
                    result.Records.Any(r => r.Status == DeliveryStatus.Failed))
                {
                    _log?.Info($"apply.decorate incomplete: decorated={outcome.Decorated} expected={targets.Count}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Full exception (type + stack) in the stage log: a native ErrorStatus
                // alone ("eNotImplementedYet") cost a rebuild to locate on 06/09.
                _log?.Info("apply.decorate skipped: " + ex);
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.LayoutUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "סימון החתכים (ציר, מערכות, מידות) לא הושלם — ההחלה בוטלה ללא שינוי חלקי",
                    Message = ex.Message,
                });
                return false;
            }

            // Decoration extends well outside Civil's native SectionView extents.
            // Capture and prove the FINAL visible envelope only after every title,
            // axis, utility, vehicle, arrow and dimension exists. This overwrites the
            // provisional view-only evidence recorded by the batch arranger.
            try
            {
                var managedTargets = targets.Where(r => r.ManualSectionReuse == null).ToList();
                CaptureManagedVisualLayoutEvidence(tr, db, managedTargets, result);
                // Reused manual Civil views are not moved and carry no layout-bounds
                // contract, but the annotations we add around them are still OUR
                // deliverable and must be proven non-overlapping.
                AssertManagedViewsDoNotOverlap(tr, db, targets, result);
            }
            catch (Exception ex)
            {
                _log?.Info("apply.final_visual_layout failed: " + ex);
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.LayoutUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "מעטפת התצוגה הסופית חופפת או אינה ניתנת לאימות — ההחלה בוטלה",
                    Message = ex.Message,
                });
                return false;
            }

            // Close the file-change race: source hashes are checked once more after
            // the expensive Civil work but before the caller is allowed to commit.
            var finalSourceFindings =
                SectionInputIntegrityService.ValidateExternalSourcesCurrent(plan, "APPLY");
            if (finalSourceFindings.Count > 0)
            {
                BlockForInputIntegrity(result, finalSourceFindings, _log);
                return false;
            }

            return true;
        }

        private static string FindingCodeFromException(Exception ex)
        {
            var text = ex.ToString();
            foreach (var code in new[]
                     {
                         SectionFindingCodes.SharedResourceChangeRequired,
                         SectionFindingCodes.OwnershipConflict,
                         SectionFindingCodes.GroupSamplingForeign,
                         SectionFindingCodes.SourceSamplingFailed,
                     })
            {
                if (text.Contains(code, StringComparison.Ordinal)) return code;
            }
            return SectionFindingCodes.SectionViewCreateFailed;
        }

        private static void BlockForStaleScope(
            SectionApplyResult result, string staleReason, StageLog? log)
        {
            log?.Info($"apply refused before write: {staleReason}");
            result.Status = DeliveryStatus.Blocked;
            result.Committed = false;
            result.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.VerifyMismatch,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = "ההחלה סורבה — התכנון אינו שייך לשרטוט/לפרופיל הפעיל",
                Message = staleReason,
                RecommendedAction = "יש להריץ תכנון מחדש על השרטוט שמתכוונים לשנות.",
            });
        }

        private static void BlockForInputIntegrity(
            SectionApplyResult result,
            IReadOnlyList<DeliveryFinding> findings,
            StageLog? log)
        {
            log?.Info("apply refused before write: source/unit/revision integrity failed");
            result.Status = DeliveryStatus.Blocked;
            result.Committed = false;
            result.PostApplyDatabaseRevision = null;
            if (findings.Count > 0)
                result.Findings.AddRange(findings);
            else
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ExternalSourceChanged,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "ההחלה סורבה — PLAN מכיל כשל שלמות מקור",
                    RecommendedAction = "יש לתקן את ממצאי PLAN ולהריץ תכנון מחדש.",
                });
        }

        private static void BlockForIncompleteBatch(
            SectionApplyResult result,
            SectionPlan plan,
            IReadOnlyCollection<SectionPlanRecord> unresolved,
            StageLog? log,
            string title = "ההחלה סורבה — לא כל רשומות ה-CL הוכרעו")
        {
            log?.Info($"apply refused before write: incomplete batch ({unresolved.Count}/{plan.Records.Count})");
            result.Status = DeliveryStatus.Blocked;
            result.Committed = false;
            result.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.IncompleteBatch,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = title,
                Message = $"{unresolved.Count} מתוך {plan.Records.Count} רשומות אינן מוכנות ואינן מוחרגות באישור מלא.",
                AffectedRecordIds = unresolved.Select(r => r.RecordId).ToList(),
            });
        }

        /// <summary>Selects the records APPLY is allowed to touch (shared by both routes).</summary>
        public static List<SectionPlanRecord> SelectTargets(
            SectionPlan plan, IReadOnlyCollection<string>? approvedRecordIds)
        {
            var unresolved = SectionPlanLogic.UnresolvedBatchRecords(plan);
            if (unresolved.Count > 0)
                throw new InvalidOperationException(
                    $"Incomplete section batch: {unresolved.Count}/{plan.Records.Count} CL records " +
                    "must be resolved or explicitly excluded before APPLY.");

            var mutations = plan.Records
                .Where(r => r.Status == DeliveryStatus.Ready)
                .Where(r => r.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace)
                .ToList();

            if (mutations.Count == 0) return new List<SectionPlanRecord>();

            var managedBatch = plan.Records
                .Where(r => r.Status == DeliveryStatus.Ready)
                .Where(r => r.Action is PlanAction.Create or PlanAction.Update or
                    PlanAction.Replace or PlanAction.Unchanged)
                .ToList();

            // Layout is a property of the whole managed sheet. Moving only one updated
            // view to a fresh mini-grid can overlap the 26 unchanged views, while moving
            // unchanged views without recreating their annotations leaves labels behind.
            // Therefore any mutation atomically replaces/redecorates every READY managed
            // section. A partial approval is refused rather than silently broadening it.
            if (approvedRecordIds != null)
            {
                var unapproved = managedBatch
                    .Where(r => !approvedRecordIds.Contains(r.RecordId))
                    .Select(r => r.RecordId)
                    .ToList();
                if (unapproved.Count > 0)
                    throw new InvalidOperationException(
                        "Partial section APPLY is unsafe because layout is batch-wide. " +
                        "Approve all changed READY records or run PLAN again. Unapproved: " +
                        string.Join(", ", unapproved));
            }

            return managedBatch;
        }

        public static SectionPlanRecord SelectSingleTarget(
            SectionPlan plan, string selectedRecordId)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(selectedRecordId))
                throw new ArgumentException(
                    "A selected section record id is required.", nameof(selectedRecordId));
            var matches = plan.Records.Where(record => string.Equals(
                    record.RecordId, selectedRecordId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    "Selected section APPLY requires one exact record from the current PLAN.");
            return matches[0];
        }

        // ------------------------------------------------------------------ bits

        private static void ApplyExistingManualSection(
            Transaction tr,
            Database db,
            CivilDocument civilDoc,
            SectionPlanRecord record,
            SectionApplyRecordResult recordResult)
        {
            var planned = record.ManualSectionReuse
                ?? throw new InvalidOperationException("Manual reuse evidence is missing.");
            if (record.Station == null || record.Cl.WcsEndpoints.Length < 4)
                throw new InvalidOperationException(
                    $"Manual reuse input is incomplete ({SectionFindingCodes.ManualSectionGeometryMismatch}).");

            // Re-resolve from the live transaction.  This closes the PLAN→APPLY race:
            // deletion, a second view, changed geometry or changed ownership all stop
            // the atomic batch instead of adopting whatever now occupies the station.
            var candidates = SectionPlanService.ScanManualSectionCandidates(tr, civilDoc);
            var decision = ManualSectionReuseResolver.Resolve(
                record.SelectedAlignment,
                record.Station.Value,
                new[]
                {
                    new ManualSectionReuseResolver.Point(
                        record.Cl.WcsEndpoints[0], record.Cl.WcsEndpoints[1]),
                    new ManualSectionReuseResolver.Point(
                        record.Cl.WcsEndpoints[2], record.Cl.WcsEndpoints[3]),
                },
                candidates);

            var selected = decision.Selected;
            if (!decision.CanReuse || selected == null ||
                !SameHandle(selected.SampleLineGroupHandle, planned.SampleLineGroupHandle) ||
                !SameHandle(selected.SampleLineHandle, planned.SampleLineHandle) ||
                !SameHandle(selected.SectionViewHandle, planned.SectionViewHandle))
            {
                throw new InvalidOperationException(
                    "The manual SectionView no longer has the unique alignment/station/CL identity " +
                    $"approved by PLAN (reason={decision.Reason}; {SectionFindingCodes.ManualSectionAmbiguous}).");
            }

            var group = OpenByHandle<CivilDb.SampleLineGroup>(
                db, tr, planned.SampleLineGroupHandle, OpenMode.ForRead)
                ?? throw new InvalidOperationException("The planned manual SampleLineGroup disappeared.");
            var sampleLine = OpenByHandle<CivilDb.SampleLine>(
                db, tr, planned.SampleLineHandle, OpenMode.ForRead)
                ?? throw new InvalidOperationException("The planned manual SampleLine disappeared.");
            var view = OpenByHandle<CivilDb.SectionView>(
                db, tr, planned.SectionViewHandle, OpenMode.ForRead)
                ?? throw new InvalidOperationException("The planned manual SectionView disappeared.");

            if (sampleLine.GroupId != group.ObjectId || view.SampleLineId != sampleLine.ObjectId)
                throw new InvalidOperationException(
                    $"Manual section linkage changed after PLAN ({SectionFindingCodes.ManualSectionAmbiguous}).");

            // These are foreign objects.  A metadata change is not an invitation to
            // overwrite it: ownership must still be absent on both exact objects.
            if (SectionOwnershipService.Read(tr, sampleLine) != null ||
                SectionOwnershipService.Read(tr, view) != null)
                throw new InvalidOperationException(
                    $"Manual section ownership changed after PLAN ({SectionFindingCodes.OwnershipConflict}).");

            if (!string.IsNullOrWhiteSpace(planned.PreservedSectionViewStyle))
            {
                string? currentStyle = null;
                try
                {
                    if (!view.StyleId.IsNull &&
                        tr.GetObject(view.StyleId, OpenMode.ForRead) is CivilDb.Styles.StyleBase style)
                        currentStyle = style.Name;
                }
                catch { }
                if (!string.Equals(
                        currentStyle, planned.PreservedSectionViewStyle,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Manual SectionView style changed after PLAN ({SectionFindingCodes.OwnershipConflict}).");
            }

            // Evidence only.  No rename, style/range/band change, source toggle,
            // ownership write or native-object creation occurs on this path.
            recordResult.Handles.SampleLineGroup = group.Handle.ToString();
            recordResult.Handles.SampleLine = sampleLine.Handle.ToString();
            recordResult.Handles.SectionView = view.Handle.ToString();
            recordResult.Handles.Sections.AddRange(
                ReadSectionHandlesStrict(tr, sampleLine));
            recordResult.ActionTaken = PlanAction.Update;
            recordResult.Status = DeliveryStatus.Applied;
            recordResult.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.ManualSectionReused,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Info,
                Title = "נעשה שימוש ב-SectionView הידני ללא שינוי באובייקטי Civil שלו",
                Message = $"SampleLine={sampleLine.Handle}; SectionView={view.Handle}. " +
                          "Only Mahod-owned annotations may be replaced around this view.",
                AffectedRecordIds = { record.RecordId },
            });

            static bool SameHandle(string a, string b) =>
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns a sample line name that does not yet exist in the group. Civil
        /// rejects duplicates with an ArgumentException and that must not abort the
        /// batch: identity is the ownership Xrecord + logical key, the name is a label.
        /// </summary>
        private static string UniqueSampleLineName(
            Transaction tr, ObjectId slgId, string wanted, SectionApplyRecordResult recordResult)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var slg = (CivilDb.SampleLineGroup)tr.GetObject(slgId, OpenMode.ForRead);
                foreach (ObjectId id in slg.GetSampleLineIds())
                {
                    // An UPDATE erases the previous owned sample line in this same
                    // transaction; the group still lists it and the transaction still
                    // hands back the erased object. Its name is free again, otherwise
                    // every update renames the section ("MCD-STA-12145.4-2", live 07/09).
                    if (id.IsNull || id.IsErased) continue;
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.SampleLine sl &&
                            !sl.IsErased && !string.IsNullOrEmpty(sl.Name))
                            taken.Add(sl.Name);
                    }
                    catch { }
                }
            }
            catch
            {
                return wanted; // cannot read the group: let Create report the truth
            }

            if (!taken.Contains(wanted)) return wanted;

            for (int n = 2; n < 1000; n++)
            {
                var candidate = $"{wanted}-{n}";
                if (taken.Contains(candidate)) continue;
                recordResult.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ClStationLabelMismatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Warning,
                    Title = $"שם החתך '{wanted}' כבר קיים בקבוצה; נוצר בשם '{candidate}'",
                    Message = "Two CL lines resolve to the same station/label on this alignment. " +
                              "Both sections were created; check the CL drawing for a duplicate line.",
                });
                return candidate;
            }
            return wanted;
        }

        private ObjectId FindOrCreateGroup(
            Transaction tr,
            ObjectId alignmentId,
            string slgName,
            string slgKey,
            SectionPlan plan,
            SectionPlanRecord record,
            SectionApplyRecordResult recordResult,
            bool allowMetadataRewrite)
        {
            var alignment = (CivilDb.Alignment)tr.GetObject(alignmentId, OpenMode.ForRead);
            ObjectId exactGroupId = ObjectId.Null;
            ObjectId legacyGroupId = ObjectId.Null;

            foreach (ObjectId slgId in alignment.GetSampleLineGroupIds())
            {
                var slg = tr.GetObject(slgId, OpenMode.ForRead) as CivilDb.SampleLineGroup;
                if (slg == null) continue;

                var owned = SectionOwnershipService.Read(tr, slg);
                if (owned != null && owned.LogicalKey == slgKey)
                {
                    if (!string.Equals(owned.Feature, "sections",
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(owned.Role, "sample-line-group",
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(owned.ProjectProfileId, plan.ProjectProfileId,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Sample line group logical key '{slgKey}' collides across feature/role/project " +
                            $"ownership ({SectionFindingCodes.OwnershipConflict})");
                    if (!exactGroupId.IsNull)
                        throw new InvalidOperationException(
                            $"Multiple exact tool-owned sample line groups exist for '{slgName}' " +
                            $"({SectionFindingCodes.OwnershipConflict})");
                    exactGroupId = slgId;
                    continue;
                }

                // v1 group keys also embedded the mutable CL drawing hash. There must
                // be at most one tool-owned group for this profile+alignment; reuse it
                // and rewrite its metadata to v2 instead of attempting a same-name
                // create after CL.dwg was merely saved.
                if (owned != null &&
                    string.Equals(owned.Feature, "sections", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(owned.Role, "sample-line-group", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(owned.ProjectProfileId, plan.ProjectProfileId, StringComparison.Ordinal))
                {
                    if (!legacyGroupId.IsNull && legacyGroupId != slgId)
                        throw new InvalidOperationException(
                            $"Multiple tool-owned sample line groups exist for '{slgName}' (SEC-OWNERSHIP-CONFLICT)");
                    legacyGroupId = slgId;
                    continue;
                }

                if (string.Equals(slg.Name, slgName, StringComparison.OrdinalIgnoreCase))
                {
                    // A same-named group owned by nobody (or another profile) is never
                    // adopted/deleted. Creating another ambiguous group is not safe.
                    throw new InvalidOperationException(
                        $"Sample line group '{slgName}' exists but is not tool-owned (SEC-OWNERSHIP-CONFLICT)");
                }
            }

            if (!exactGroupId.IsNull)
            {
                if (!legacyGroupId.IsNull)
                    throw new InvalidOperationException(
                        $"Both current and legacy tool-owned sample line groups exist for '{slgName}' " +
                        $"({SectionFindingCodes.OwnershipConflict})");
                return exactGroupId;
            }

            if (!legacyGroupId.IsNull)
            {
                // The group is a shared resource: rewriting its ownership metadata (v1 → v2
                // key) is a batch-only normalization. Selected scope validates and blocks.
                if (!allowMetadataRewrite)
                    throw new InvalidOperationException(
                        $"קבוצת הדגימה '{slgName}' נושאת מטא-דאטה ישן (מפתח v1) — החלת חתך נבחר אינה משכתבת " +
                        "משאב משותף; יש להריץ החלת אצווה שתיישר את הקבוצה " +
                        $"({SectionFindingCodes.SharedResourceChangeRequired})");
                var legacy = (CivilDb.SampleLineGroup)tr.GetObject(legacyGroupId, OpenMode.ForWrite);
                SectionOwnershipService.Write(tr, legacy, new OwnershipMetadata
                {
                    Feature = "sections",
                    Role = "sample-line-group",
                    ProjectProfileId = plan.ProjectProfileId,
                    RunId = plan.RunId,
                    SourceClDrawingHash = record.Cl.SourceDrawingHash,
                    SourceClHandle = "GROUP",
                    LogicalKey = slgKey,
                    InputFingerprint = "group",
                    CreatedByToolVersion = SectionPlanService.ToolVersion,
                });
                return legacyGroupId;
            }

            ObjectId newId;
            try
            {
                newId = CivilDb.SampleLineGroup.Create(slgName, alignmentId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"SampleLineGroup.Create failed for '{slgName}': {ex.Message} (SEC-SAMPLELINE-GROUP-CREATE-FAILED)", ex);
            }

            var created = (CivilDb.SampleLineGroup)tr.GetObject(newId, OpenMode.ForWrite);
            SectionOwnershipService.Write(tr, created, new OwnershipMetadata
            {
                Feature = "sections",
                Role = "sample-line-group",
                ProjectProfileId = plan.ProjectProfileId,
                RunId = plan.RunId,
                SourceClDrawingHash = record.Cl.SourceDrawingHash,
                SourceClHandle = "GROUP",
                LogicalKey = slgKey,
                InputFingerprint = "group",
                CreatedByToolVersion = SectionPlanService.ToolVersion,
            });
            return newId;
        }

        private static void RemoveToolOwnedSectionObjects(
            Transaction tr, CivilDb.SampleLineGroup slg, SectionPlanRecord record,
            string projectProfileId)
        {
            var logicalKey = record.LogicalKey!;
            var annotationKeys = new HashSet<string>(StringComparer.Ordinal) { logicalKey };

            foreach (ObjectId slId in slg.GetSampleLineIds())
            {
                var sl = tr.GetObject(slId, OpenMode.ForRead, openErased: false) as
                    CivilDb.SampleLine ?? throw new InvalidOperationException(
                        $"SampleLine {slId} could not be opened while reconciling an owned section.");

                var meta = SectionOwnershipService.Read(tr, sl);
                if (!MatchesCurrentOrLegacy(meta, "sample-line")) continue; // not ours / not this section
                annotationKeys.Add(meta!.LogicalKey);

                // Children preflight BEFORE any erase: every attached view must be ours
                // and the read must succeed. A manual/foreign view drawn from our sample
                // line keeps its parent — the record fails instead (review, 02/09).
                var readable = SectionPlanService.TryEnumerateSectionViewIds(
                    sl, out var childViewIds, out var childError);
                var children = new List<(string Handle, bool Owned)>();
                foreach (var svId in childViewIds)
                {
                    var probe = tr.GetObject(svId, OpenMode.ForRead, openErased: false);
                    var owned = probe != null &&
                                MatchesCurrentOrLegacy(SectionOwnershipService.Read(tr, probe), "section-view");
                    children.Add((svId.Handle.ToString(), owned));
                }
                var childVerdict = OwnedSampleLineChildrenLogic.Decide(readable, children);
                if (!childVerdict.IsSafeToMutate)
                    throw new InvalidOperationException(
                        (childVerdict.Readable
                            ? "קו הדגימה של הכלי נושא חתך שאינו בבעלות הכלי (" +
                              string.Join(", ", childVerdict.ForeignHandles) + ")"
                            : $"לא ניתן לקרוא את חתכי קו הדגימה של הכלי ({childError})") +
                        $" — קו הדגימה לא נמחק ולא נדגם מחדש ({SectionFindingCodes.OwnershipConflict})");

                foreach (var svId in childViewIds)
                {
                    var sv = tr.GetObject(svId, OpenMode.ForRead, openErased: false) ??
                        throw new InvalidOperationException(
                            $"SectionView {svId} could not be opened while reconciling an owned section.");
                    var viewMeta = SectionOwnershipService.Read(tr, sv);
                    if (MatchesCurrentOrLegacy(viewMeta, "section-view"))
                    {
                        annotationKeys.Add(viewMeta!.LogicalKey);
                        if (!sv.IsWriteEnabled) sv.UpgradeOpen();
                        sv.Erase();
                        if (!sv.IsErased)
                            throw new InvalidOperationException(
                                $"Owned SectionView {svId} did not report erased after reconciliation.");
                    }
                }

                if (!sl.IsWriteEnabled) sl.UpgradeOpen();
                sl.Erase();
                if (!sl.IsErased)
                    throw new InvalidOperationException(
                        $"Owned SampleLine {slId} did not report erased after reconciliation.");
            }

            // The annotations drawn for this section go with it; leaving them behind
            // would decorate a section that no longer exists.
            var annoDb = slg.Database ?? throw new InvalidOperationException(
                "Owned SampleLineGroup has no database while reconciling annotations.");
            foreach (var key in annotationKeys)
                SectionAnnotationRegistry.EraseExisting(tr, annoDb, key);

            bool MatchesCurrentOrLegacy(OwnershipMetadata? meta, string role)
                => SectionRecordOwnershipLogic.IsOwnedByRecord(
                    meta, projectProfileId, role, logicalKey, record.Cl.SourceHandle,
                    allowLegacySourceHandle: true);
        }

        private static void ApplyPlannedSampleLineStyle(
            Transaction tr, CivilDocument civilDoc, CivilDb.SampleLine sampleLine,
            SectionPlanRecord record)
        {
            if (!record.PlannedStyles.TryGetValue("sample_line_style", out var styleName) ||
                string.IsNullOrWhiteSpace(styleName))
                throw new InvalidOperationException(
                    "Managed sample line has no planned style (SEC-STYLE-MISSING)");
            try
            {
                var styles = civilDoc.Styles.SampleLineStyles;
                if (!styles.Contains(styleName))
                    throw new InvalidOperationException(
                        $"Planned sample line style '{styleName}' does not exist (SEC-STYLE-MISSING)");
                var expectedId = styles[styleName];
                sampleLine.StyleId = expectedId;
                var actualId = sampleLine.StyleId;
                var actualStyle = actualId.IsNull || actualId.IsErased ? null
                    : tr.GetObject(actualId, OpenMode.ForRead) as CivilDb.Styles.StyleBase;
                if (actualId != expectedId ||
                    !SectionSampleLineStyleLogic.Matches(styleName, actualStyle?.Name))
                    throw new InvalidOperationException(
                        $"SampleLine style read-back differs from '{styleName}' (SEC-STYLE-MISSING)");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"SampleLine style application failed for '{styleName}': {ex.Message} (SEC-STYLE-MISSING)", ex);
            }
        }

        private static void TryApplyStyle(
            Transaction tr, CivilDocument civilDoc, CivilDb.SectionView view, SectionPlanRecord record,
            SectionApplyRecordResult recordResult, ObjectId builtInPresentationStyleId)
        {
            if (!record.PlannedStyles.TryGetValue("section_view_style", out var styleName) ||
                string.IsNullOrWhiteSpace(styleName))
                throw new InvalidOperationException(
                    "Managed section has no planned SectionView style (SEC-STYLE-MISSING)");
            if (styleName.StartsWith("(document default", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "A provisional document-default SectionView style cannot be applied to a managed deliverable (SEC-STYLE-MISSING)");

            try
            {
                if (string.Equals(styleName, SectionViewPresentationStyleService.StyleName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    SectionViewPresentationStyleService.Apply(
                        view, builtInPresentationStyleId);
                }
                else
                {
                    var styles = civilDoc.Styles.SectionViewStyles;
                    if (styles.Contains(styleName))
                    {
                        view.StyleId = styles[styleName];
                    }
                    else
                    {
                        // Configured style absent at APPLY time: explicit failure, no fallback (§7.9).
                        throw new InvalidOperationException(
                            $"Configured section view style '{styleName}' does not exist (SEC-STYLE-MISSING)");
                    }
                }

                var appliedStyle = tr.GetObject(view.StyleId, OpenMode.ForRead)
                    as Autodesk.Civil.DatabaseServices.Styles.StyleBase;
                if (appliedStyle == null || !string.Equals(
                        appliedStyle.Name, styleName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"SectionView style read-back differs from '{styleName}' (SEC-STYLE-MISSING)");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Style application failed for '{styleName}': {ex.Message} (SEC-STYLE-MISSING)", ex);
            }
        }

        private static ObjectId? FindAlignment(Transaction tr, CivilDocument civilDoc, string name)
        {
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment a &&
                        string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
                catch { }
            }
            return null;
        }

        private static T? OpenByHandle<T>(
            Database db, Transaction tr, string? handle, OpenMode mode) where T : DBObject
        {
            if (string.IsNullOrWhiteSpace(handle)) return null;
            if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
            if (!db.TryGetObjectId(new Handle(value), out var id)) return null;
            try { return tr.GetObject(id, mode) as T; }
            catch { return null; }
        }

        /// <summary>
        /// Grid margins between arranged section views, drawing units (metres).
        /// The band-set TITLE box hangs ~45 m left of the graph and is NOT part of
        /// the view's GeometricExtents, so it invaded the neighbouring column until
        /// the margin covered it (engineer screenshot, 31/08).
        /// </summary>
        public const double ArrangeMarginX = 60.0;
        public const double ArrangeMarginY = 45.0;

        /// <summary>
        /// Without band sets nothing hangs 45 m outside a view's extents, so the grid
        /// can close up to the annotation overhang (offsets row, strip names, datum).
        /// </summary>
        internal static (double X, double Y) ArrangeMarginsFor(ProjectProfile profile) =>
            string.IsNullOrWhiteSpace(profile.Sections.Styles.BandSetStyle)
                ? (30.0, 25.0)
                : (ArrangeMarginX, ArrangeMarginY);

        /// <summary>
        /// After every view of the batch exists, measure each one's REAL extents and
        /// re-grid them so no two overlap. PLAN can only guess a view's height (it
        /// depends on the sampled elevation range and the style's vertical
        /// exaggeration); the first real run stacked 25 views 80 m apart when each was
        /// ~200 m tall and their elevation axes printed on top of each other
        /// (2026-08-19, 6422). Moving a view is a pure translation of its Location.
        /// </summary>
        internal static int ArrangeCreatedViews(
            Transaction tr, Database db, ProjectProfile profile,
            IReadOnlyList<SectionPlanRecord> targets, SectionApplyResult result,
            (double X, double Y) origin, StageLog? log)
        {
            var items = new List<(SectionApplyRecordResult Rec, CivilDb.SectionView View, Extents3d Ext, double? Station)>();
            foreach (var target in targets)
            {
                var rec = result.Records.SingleOrDefault(r => r.RecordId == target.RecordId)
                    ?? throw new InvalidOperationException($"No APPLY result exists for layout target {target.RecordId}.");
                if (string.IsNullOrEmpty(rec.Handles.SectionView))
                    throw new InvalidOperationException($"Layout target {target.RecordId} has no SectionView handle.");
                if (!long.TryParse(rec.Handles.SectionView, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var hv))
                    throw new InvalidOperationException($"Layout target {target.RecordId} has an invalid SectionView handle.");
                if (!db.TryGetObjectId(new Handle(hv), out var id))
                    throw new InvalidOperationException($"Layout target {target.RecordId} SectionView cannot be resolved.");
                if (tr.GetObject(id, OpenMode.ForWrite) is not CivilDb.SectionView view)
                    throw new InvalidOperationException($"Layout target {target.RecordId} handle is not a SectionView.");
                Extents3d ext;
                try { ext = view.GeometricExtents; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Layout target {target.RecordId} extents cannot be read.", ex);
                }
                var width = ext.MaxPoint.X - ext.MinPoint.X;
                var height = ext.MaxPoint.Y - ext.MinPoint.Y;
                if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                    throw new InvalidOperationException(
                        $"Layout target {target.RecordId} has invalid extents {width}x{height}.");
                items.Add((rec, view, ext, target.Station));
            }
            if (items.Count != targets.Count)
                throw new InvalidOperationException(
                    $"Only {items.Count}/{targets.Count} target views reached layout.");
            if (items.Count == 0) return 0;

            var columns = profile.Sections.Layout.Columns is > 0 ? profile.Sections.Layout.Columns.Value : 3;

            var ordered = items
                .OrderBy(i => i.Station ?? double.MaxValue)
                .ThenBy(i => i.Rec.RecordId, StringComparer.Ordinal)
                .ToList();

            // The grid maths lives in Core (SectionLayoutPlanner.ArrangeSheet) so it can
            // be proven overlap-free by tests without a Civil host.
            var boxes = ordered
                .Select(i => new SectionLayoutPlanner.Box(
                    i.Ext.MaxPoint.X - i.Ext.MinPoint.X,
                    i.Ext.MaxPoint.Y - i.Ext.MinPoint.Y))
                .ToList();
            var placements = SectionLayoutPlanner.ArrangeSheet(
                boxes, columns, ArrangeMarginsFor(profile).X, ArrangeMarginsFor(profile).Y, origin.X, origin.Y);
            if (placements.Count != ordered.Count ||
                !SectionLayoutPlanner.IsSheetOverlapFree(boxes, placements))
                throw new InvalidOperationException("The computed section layout is incomplete or overlapping.");

            log?.Info($"apply.arrange_views views={ordered.Count} columns={columns} " +
                      $"maxBox={boxes.Max(b => b.Width):F1}x{boxes.Max(b => b.Height):F1} " +
                      $"overlapFree={SectionLayoutPlanner.IsSheetOverlapFree(boxes, placements)} " +
                      $"origin={origin.X:F1},{origin.Y:F1}");

            int moved = 0;
            foreach (var place in placements)
            {
                var item = ordered[place.Index];
                var delta = new Point3d(place.MinX, place.MinY, 0) - item.Ext.MinPoint;
                if (delta.Length >= 1e-6)
                {
                    item.View.Location = item.View.Location + delta;
                    moved++;
                }
                var evidence = LayoutEvidenceContract.Capture(
                    item.Ext.MinPoint.X, item.Ext.MinPoint.Y, item.Ext.MaxPoint.X, item.Ext.MaxPoint.Y,
                    item.View.Location.X, item.View.Location.Y, delta.X, delta.Y);
                item.Rec.LayoutBounds = evidence.Bounds;
                item.Rec.LayoutViewLocation = evidence.Location;
            }
            return moved;
        }

        private static void BlockForNoMutationEvidence(
            SectionApplyResult result,
            IReadOnlyCollection<string> unchangedRecordIds,
            StageLog? log)
        {
            log?.Info($"apply refused before write: unchanged rows have no APPLY evidence ({unchangedRecordIds.Count})");
            result.Committed = false;
            result.Status = DeliveryStatus.Blocked;
            result.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.NoMutationEvidence,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = "לא נוצר בסיס אימות — PLAN ללא שינוי אינו ראיית APPLY",
                Message = "חתך שסווג ללא שינוי לא נקרא מחדש במלואו במסלול זה. " +
                          "אין לסמן אותו כמאומת או לפרסם תוצאת VERIFY ירוקה ללא ראיות APPLY מלאות.",
                AffectedRecordIds = unchangedRecordIds.ToList(),
            });
        }

        private static IReadOnlyList<string> ReadSectionHandlesStrict(
            Transaction tr, CivilDb.SampleLine sampleLine)
        {
            IReadOnlyList<ObjectId> ids;
            try
            {
                ids = sampleLine.GetSectionIds().Cast<ObjectId>().ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"SampleLine {sampleLine.Handle} section children could not be enumerated.", ex);
            }

            var handles = new List<string>(ids.Count);
            foreach (var id in ids)
            {
                try
                {
                    if (id.IsNull || id.IsErased ||
                        tr.GetObject(id, OpenMode.ForRead, openErased: false) is not CivilDb.Section section ||
                        section.IsErased)
                        throw new InvalidOperationException(
                            $"SampleLine {sampleLine.Handle} returned a non-live Section child.");
                    handles.Add(section.Handle.ToString());
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"SampleLine {sampleLine.Handle} Section child {id.Handle} could not be read.", ex);
                }
            }

            if (handles.Count != handles.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidOperationException(
                    $"SampleLine {sampleLine.Handle} returned duplicate Section children.");
            handles.Sort(StringComparer.OrdinalIgnoreCase);
            return handles;
        }

        /// <summary>
        /// Captures the complete final visible envelope (native view plus every
        /// fingerprinted annotation) after decoration. A view-only envelope would let
        /// titles, dimensions, blocks or projected utilities overlap while APPLY and
        /// VERIFY still reported green.
        /// </summary>
        internal static void CaptureManagedVisualLayoutEvidence(
            Transaction tr, Database db,
            IReadOnlyList<SectionPlanRecord> targets, SectionApplyResult result)
        {
            foreach (var target in targets)
            {
                var rec = result.Records.Single(r => r.RecordId == target.RecordId);
                var view = OpenByHandle<CivilDb.SectionView>(
                        db, tr, rec.Handles.SectionView, OpenMode.ForRead)
                    ?? throw new InvalidOperationException(
                        $"Layout evidence target {target.RecordId} SectionView cannot be resolved.");
                if (string.IsNullOrWhiteSpace(target.LogicalKey))
                    throw new InvalidOperationException(
                        $"Layout evidence target {target.RecordId} has no logical key.");
                var envelope = SectionViewOverlapService.ManagedEnvelope(
                    tr, db, view, target.LogicalKey);
                var evidence = LayoutEvidenceContract.Capture(
                    envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY,
                    view.Location.X, view.Location.Y);
                rec.LayoutBounds = evidence.Bounds;
                rec.LayoutViewLocation = evidence.Location;
            }
        }

        /// <summary>
        /// The batch non-overlap proof compares the artifact's own records; for a
        /// selected run that is one view against itself. Prove the retained planned
        /// origin against every other live SectionView before the caller may commit.
        /// </summary>
        internal static void AssertManagedViewsDoNotOverlap(
            Transaction tr, Database db,
            IReadOnlyList<SectionPlanRecord> targets, SectionApplyResult result)
        {
            foreach (var target in targets)
            {
                var rec = result.Records.Single(r => r.RecordId == target.RecordId);
                var view = OpenByHandle<CivilDb.SectionView>(
                        db, tr, rec.Handles.SectionView, OpenMode.ForRead)
                    ?? throw new InvalidOperationException(
                        $"Overlap target {target.RecordId} SectionView cannot be resolved.");
                if (string.IsNullOrWhiteSpace(target.LogicalKey))
                    throw new InvalidOperationException(
                        $"Overlap target {target.RecordId} has no logical key.");
                var report = SectionViewOverlapService.Inspect(
                    tr, db, view, target.LogicalKey);
                if (report.IsClean) continue;
                throw new InvalidOperationException(
                    "חתך מנוהל חופף או אינו ניתן להשוואה מול חתכים חיים בשרטוט " +
                    $"(גבולות החתך עצמו תקינים: {report.SelfValid}; " +
                    $"חופפים: {(report.CollidingIds.Count == 0 ? "—" : string.Join(", ", report.CollidingIds))}; " +
                    $"לא מדידים: {(report.UnmeasurableIds.Count == 0 ? "—" : string.Join(", ", report.UnmeasurableIds))}) " +
                    $"({SectionFindingCodes.LayoutUnresolved})");
            }
        }

        /// <summary>
        /// True when the group holds work outside the exact APPLY scope: a foreign or
        /// omitted sample line, a permitted line carrying a foreign/omitted SectionView,
        /// or anything that cannot be read. A source toggle re-samples the entire group,
        /// so even batch APPLY may converge sampling only when it owns the complete
        /// group population it is about to replace.
        /// </summary>
        private static bool GroupIsSharedWithForeignWork(
            Transaction tr, CivilDb.SampleLineGroup slg,
            IReadOnlyList<SectionPlanRecord> permittedRecords, string projectProfileId)
        {
            foreach (ObjectId slId in slg.GetSampleLineIds())
            {
                if (slId.IsNull || slId.IsErased) return true;
                CivilDb.SampleLine? sl = null;
                try { sl = tr.GetObject(slId, OpenMode.ForRead, openErased: false) as CivilDb.SampleLine; }
                catch { }
                if (sl == null) return true;
                var meta = SectionOwnershipService.Read(tr, sl);
                var ownerRecord = permittedRecords.FirstOrDefault(record =>
                    OwnedByRecord(meta, record, projectProfileId, "sample-line"));
                if (ownerRecord == null) return true;

                if (!SectionPlanService.TryEnumerateSectionViewIds(sl, out var viewIds, out _)) return true;
                var children = new List<(string Handle, bool Owned)>();
                foreach (var svId in viewIds)
                {
                    DBObject? sv = null;
                    try { sv = tr.GetObject(svId, OpenMode.ForRead, openErased: false); }
                    catch { }
                    var owned = sv != null &&
                                OwnedByRecord(SectionOwnershipService.Read(tr, sv), ownerRecord,
                                    projectProfileId, "section-view");
                    children.Add((svId.Handle.ToString(), owned));
                }
                if (!OwnedSampleLineChildrenLogic.Decide(true, children).IsSafeToMutate) return true;
            }
            return false;
        }

        private static bool OwnedByRecord(
            OwnershipMetadata? meta, SectionPlanRecord record, string projectProfileId, string role)
            => SectionRecordOwnershipLogic.IsOwnedByRecord(
                meta, projectProfileId, role, record.LogicalKey ?? string.Empty,
                record.Cl.SourceHandle, allowLegacySourceHandle: true);
    }
}
