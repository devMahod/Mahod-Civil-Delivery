using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using CivilStyles = Autodesk.Civil.DatabaseServices.Styles;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// VERIFY (plan §7.1/§14.3): re-reads the ACTUAL Civil database after APPLY commits
    /// and proves object-level agreement with the approved plan. "API returned
    /// success" is not verification.
    /// </summary>
    public sealed class SectionVerifyService
    {
        private const double GeomTol = 0.01; // meters

        public SectionVerifyResult Verify(
            Database db,
            Transaction tr,
            CivilDocument civilDoc,
            SectionPlan plan,
            SectionApplyResult applied,
            string? runId = null, ProjectProfile? profile = null) =>
            VerifyCore(db, tr, civilDoc, plan, applied,
                selectedScope: false, selectedRecordId: null, runId, profile: profile);

        /// <summary>
        /// Verifies every ordinary contract of one explicitly selected APPLY result:
        /// ownership, exact sources/styles, geometry, annotations, single datum,
        /// slopes, office blocks, direction arrows and projected utilities. Omitted
        /// PLAN records are neither added to the result nor reported as verified.
        /// </summary>
        public SectionVerifyResult VerifySelected(
            Database db,
            Transaction tr,
            CivilDocument civilDoc,
            SectionPlan plan,
            SectionApplyResult applied,
            string selectedRecordId,
            string? runId = null, ProjectProfile? profile = null) =>
            VerifyCore(db, tr, civilDoc, plan, applied,
                selectedScope: true, selectedRecordId, runId, profile: profile);

        internal SectionVerifyResult VerifyRecoveredSelected(
            Database db, Transaction tr, CivilDocument civilDoc, SectionPlan current,
            SectionVerificationRecoveryService.Authority authority, string recordId, ProjectProfile? profile = null)
        {
            // A local selected projection is NOT published as APPLY. New VERIFY
            // links the untouched original producer artifact, including batch scope.
            var original = authority.Applied;
            var selected = original.Records.Single(r => r.RecordId == recordId);
            var projection = new SectionApplyResult
            {
                RunId = original.RunId, Scope = "selected-record", SelectedRecordId = recordId,
                Committed = original.Committed, Status = original.Status,
                PostApplyDatabaseRevision = original.PostApplyDatabaseRevision,
                Records = new List<SectionApplyRecordResult> { selected },
                Findings = original.Findings.ToList(),
            };
            var result = VerifyCore(db, tr, civilDoc, current, projection,
                true, recordId, null, revalidatedInputs: true, profile: profile);
            foreach (var relocation in authority.SourceRelocations)
                result.Findings.Add(new DeliveryFinding
                {
                    Code = "SEC-SOURCE-RELOCATION-PROVEN", Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Info,
                    Title = "מקור חיצוני הועבר לנתיב חדש עם תוכן זהה",
                    Message = $"original={relocation.OriginalPath}; current={relocation.CurrentPath}; " +
                              $"sha256={relocation.Sha256}; source={relocation.SourceName}. " +
                              "One-to-one source proof only; live source and geometry checks remain independently required.",
                    RunId = result.RunId, ProjectProfileId = current.ProjectProfileId,
                    AffectedRecordIds = new List<string> { recordId },
                    EvidenceRefs = new List<string> { authority.ProducerPlan.RunId, current.RunId },
                });
            return result;
        }

        private SectionVerifyResult VerifyCore(
            Database db,
            Transaction tr,
            CivilDocument civilDoc,
            SectionPlan plan,
            SectionApplyResult applied,
            bool selectedScope,
            string? selectedRecordId,
            string? runId,
            bool revalidatedInputs = false, ProjectProfile? profile = null)
        {
            var result = new SectionVerifyResult
            {
                RunId = runId ?? RunManifest.NewRunId("sections", "verify"),
                Scope = selectedScope ? "selected-record" : "batch",
                SelectedRecordId = selectedScope ? selectedRecordId : null,
            };

            // A one-record APPLY artifact is never valid evidence for the batch gate,
            // even when every omitted PLAN row happens to be excluded.  The caller
            // must use VerifySelected so the resulting green state remains explicitly
            // limited to that one record.
            if (!selectedScope &&
                !string.Equals(applied.Scope, "batch", StringComparison.Ordinal))
            {
                result.Status = DeliveryStatus.Blocked;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.VerifyMismatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "אימות אצווה סורב — תוצאת APPLY מוגבלת לחתך נבחר",
                    Message = $"apply_scope={applied.Scope}; expected=batch. " +
                              "יש להשתמש באימות חתך נבחר; אין להציג תוצאה זו כאימות אצווה.",
                });
                return result;
            }

            if (selectedScope)
            {
                var selectedPlanRecords = plan.Records.Where(record => string.Equals(
                        record.RecordId, selectedRecordId, StringComparison.Ordinal))
                    .ToList();
                var selectedApplyRecords = applied.Records.Where(record => string.Equals(
                        record.RecordId, selectedRecordId, StringComparison.Ordinal))
                    .ToList();
                var selectedContractExact =
                    !string.IsNullOrWhiteSpace(selectedRecordId) &&
                    string.Equals(applied.Scope, "selected-record", StringComparison.Ordinal) &&
                    string.Equals(applied.SelectedRecordId, selectedRecordId,
                        StringComparison.Ordinal) &&
                    selectedPlanRecords.Count == 1 &&
                    selectedPlanRecords[0].Status == DeliveryStatus.Ready &&
                    selectedPlanRecords[0].PresentationCoverage.Complete &&
                    selectedPlanRecords[0].PresentationCoverage.UnresolvedSpans.Count == 0 &&
                    selectedPlanRecords[0].TrafficDirections.All(direction => direction.IsResolved) &&
                    applied.Records.Count == 1 &&
                    selectedApplyRecords.Count == 1 &&
                    selectedApplyRecords[0].Status == DeliveryStatus.Applied;
                if (!selectedContractExact)
                {
                    result.Status = DeliveryStatus.Blocked;
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.VerifyMismatch,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "אימות חתך נבחר סורב — תוצאת APPLY אינה תואמת בדיוק לרשומה הנבחרת",
                        Message = $"requested={selectedRecordId}; apply_scope={applied.Scope}; " +
                                  $"apply_selected={applied.SelectedRecordId}; " +
                                  $"apply_records={applied.Records.Count}; plan_matches={selectedPlanRecords.Count}",
                        AffectedRecordIds = string.IsNullOrWhiteSpace(selectedRecordId)
                            ? new List<string>()
                            : new List<string> { selectedRecordId },
                    });
                    return result;
                }
            }

            // APPLY must bind every exact PLAN row, including signed exclusions.
            // Without this preflight, the completeness loop below could reconstruct
            // an omitted exclusion as green from PLAN alone, and duplicate APPLY rows
            // could both verify against the same live object.
            var recordEvidence = SectionRecordEvidenceLogic.Compare(
                (selectedScope
                    ? plan.Records.Where(record => string.Equals(
                        record.RecordId, selectedRecordId, StringComparison.Ordinal))
                    : plan.Records).Select(record => new SectionRecordEvidenceLogic.Identity(
                        record.RecordId, record.LogicalKey)),
                applied.Records.Select(record => new SectionRecordEvidenceLogic.Identity(
                    record.RecordId, record.LogicalKey)));
            if (!recordEvidence.IsExact)
            {
                result.Status = DeliveryStatus.Blocked;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.VerifyMismatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "VERIFY סורב — רשומות APPLY אינן זהות בדיוק לרשומות PLAN",
                    Message =
                        $"missing={string.Join(',', recordEvidence.Missing)}; " +
                        $"extra={string.Join(',', recordEvidence.Extra)}; " +
                        $"duplicate_plan={string.Join(',', recordEvidence.DuplicatePlanIds)}; " +
                        $"duplicate_apply={string.Join(',', recordEvidence.DuplicateApplyIds)}; " +
                        $"logical_key_mismatch={string.Join(',', recordEvidence.LogicalKeyMismatches)}",
                });
                return result;
            }

            // VERIFY accepts only a fully authoritative APPLY artifact. A record-level
            // Error is just as blocking as a global one; otherwise a future producer
            // could leave Status=Applied while attaching an error and VERIFY would
            // silently ignore it. Pending/Ready/ReviewRequired rows are not evidence
            // of a completed APPLY, even when an aggregate status happens to be stale.
            var authoritativeApplyErrors = applied.Findings
                .Concat(applied.Records.SelectMany(record => record.Findings))
                .Where(finding => finding.Severity == FindingSeverity.Error)
                .ToList();
            var authoritativeApplyFailure =
                applied.Status is not (DeliveryStatus.Applied or DeliveryStatus.Verified) ||
                applied.Records.Count == 0 ||
                applied.Records.Any(record =>
                    record.Status is not (DeliveryStatus.Applied or DeliveryStatus.Verified) ||
                    // PLAN's Unchanged classification is not proof that the live
                    // SectionView contract was re-read.  A real batch APPLY may carry
                    // ActionTaken=Unchanged, but it records Status=Applied after
                    // rebuilding and capturing the full object-level evidence.
                    record.ActionTaken == PlanAction.Unchanged &&
                    record.Status != DeliveryStatus.Applied) ||
                authoritativeApplyErrors.Count > 0;
            if (authoritativeApplyFailure)
            {
                result.Status = DeliveryStatus.Blocked;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.VerifyMismatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "VERIFY סורב — תוצאת APPLY אינה ראיה סמכותית",
                    Message = $"apply_status={applied.Status}; error_findings=" +
                              string.Join(",", authoritativeApplyErrors
                                  .Select(finding => finding.Code)
                                  .Distinct(StringComparer.Ordinal)),
                });
                return result;
            }

            var integrity = SectionInputIntegrityService.ValidateCurrent(
                db, plan, revalidatedInputs ? plan.SourceDatabaseRevision :
                    applied.PostApplyDatabaseRevision, "VERIFY", profile);
            var selectedPlanRecord = selectedScope
                ? plan.Records.Single(record => string.Equals(
                    record.RecordId, selectedRecordId, StringComparison.Ordinal))
                : null;
            var selectedPlanningBlockers = selectedPlanRecord == null
                ? Array.Empty<DeliveryFinding>()
                : SectionInputIntegrityService.SelectedPlanningBlockers(
                    plan, selectedPlanRecord).ToArray();
            var planningBlocked = selectedPlanRecord == null
                ? SectionInputIntegrityService.HasPlanningIntegrityBlocker(plan)
                : SectionInputIntegrityService.HasSelectedPlanningIntegrityBlocker(
                    plan, selectedPlanRecord);
            if (!applied.Committed || integrity.Count > 0 ||
                planningBlocked)
            {
                result.Status = DeliveryStatus.Blocked;
                result.Findings.AddRange(integrity);
                result.Findings.AddRange(selectedPlanningBlockers);
                if (!applied.Committed)
                {
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.VerifyMismatch,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "VERIFY סורב — אין ראיה שטרנזקציית APPLY נחתמה",
                        Message = "apply_result.committed=false",
                    });
                }
                if (result.Findings.Count == 0)
                {
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.ExternalSourceChanged,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "VERIFY סורב — PLAN מכיל כשל שלמות מקור",
                    });
                }
                return result;
            }

            var annotationInventory =
                SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db);
            if (!annotationInventory.IsValid)
            {
                result.Status = DeliveryStatus.Blocked;
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.AnnotationInventoryConflict,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "VERIFY נכשל — מלאי הערות החתכים אינו שלם או אינו בבעלות מוכחת",
                    Message = string.Join(" | ", annotationInventory.Problems),
                });
                return result;
            }

            // Civil 3D 2027 requires SampleLineGroup.GetSectionSources to run on a
            // write-open group. Snapshot every distinct managed group before the
            // outer VERIFY transaction opens any group ForRead; repeated records in
            // the same group must never try to upgrade it through a nested
            // transaction (the native API can terminate AutoCAD instead of throwing
            // a safely catchable managed exception).
            var sampledSourcesByGroup = SnapshotSampledSourceNames(db, tr, plan, applied);
            var fingerprintDiagnostics = SectionOfficeBlockFingerprintDiagnostics.TryCreate(db, result.RunId);
            // Diagnostic-only, selected-record VERIFY: receives the detached layout-input
            // receipt of the one selected record. Assignment only; batch VERIFY passes no
            // observer and therefore keeps the original five-argument layout call.
            Action<SectionLayoutInputCaptureRecord>? reportLayoutInputs = selectedScope
                ? capture => result.LayoutInputCapture = capture
                : null;

            foreach (var applyRecord in applied.Records.Where(r => r.Status == DeliveryStatus.Applied))
            {
                var planRecord = plan.Records.FirstOrDefault(r => r.RecordId == applyRecord.RecordId);
                var recordResult = new SectionVerifyRecordResult
                {
                    RecordId = applyRecord.RecordId,
                    LogicalKey = applyRecord.LogicalKey,
                };
                result.Records.Add(recordResult);

                if (planRecord == null)
                {
                    recordResult.Checks.Add(new SectionVerifyCheck
                    {
                        Check = "plan_record_exists",
                        Expected = "exists",
                        Actual = "missing",
                        Pass = false,
                    });
                    recordResult.Status = DeliveryStatus.Failed;
                    continue;
                }

                VerifyOne(db, tr, civilDoc, plan, planRecord, applyRecord, recordResult,
                    sampledSourcesByGroup, fingerprintDiagnostics, reportLayoutInputs);
                if (revalidatedInputs)
                    SectionVerificationRecoveryService.CheckLiveSources(
                        db, tr, planRecord, applyRecord, recordResult);
            }

            // Completeness is part of verification.  A subset such as 27 applied
            // records out of an 84-row PLAN may never become a green result merely
            // because the other 57 were omitted from the verify loop.
            if (!selectedScope)
            {
                foreach (var planRecord in plan.Records.Where(p =>
                             result.Records.All(r => r.RecordId != p.RecordId)))
                {
                    var explicitExclusion = SectionPlanLogic.HasValidExplicitExclusion(planRecord);
                    var applyRecord = applied.Records.FirstOrDefault(r =>
                        r.RecordId == planRecord.RecordId);
                    var resolved = explicitExclusion;
                    var recordResult = new SectionVerifyRecordResult
                    {
                        RecordId = planRecord.RecordId,
                        LogicalKey = planRecord.LogicalKey,
                        Checks =
                        {
                            new SectionVerifyCheck
                            {
                                Check = "plan_record_resolved",
                                Expected = "applied, or explicitly excluded with finding+approver+reason+timestamp",
                                Actual = explicitExclusion
                                    ? $"excluded {planRecord.ExplicitExclusion!.FindingCode} by " +
                                      $"{planRecord.ExplicitExclusion.ApprovedBy}: {planRecord.ExplicitExclusion.Reason}"
                                    : $"status={planRecord.Status}; action={planRecord.Action}",
                                Pass = resolved,
                            }
                        },
                    };
                    result.Records.Add(recordResult);
                }
            }

            if (!selectedScope)
                AddLayoutNonOverlapChecks(db, tr, plan, applied, result);
            else
                AddSelectedNonOverlapChecks(db, tr, plan, applied, result);

            foreach (var recordResult in result.Records)
            {
                recordResult.Status = recordResult.Checks.Count > 0 && recordResult.Checks.All(c => c.Pass)
                    ? DeliveryStatus.Verified
                    : DeliveryStatus.Failed;

                if (recordResult.Status == DeliveryStatus.Failed)
                {
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.VerifyMismatch,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = $"אי-התאמה באימות לרשומה {recordResult.RecordId}",
                        Message = string.Join("; ", recordResult.Checks.Where(c => !c.Pass)
                            .Select(c => $"{c.Check}: expected {c.Expected}, actual {c.Actual}")),
                        AffectedRecordIds = { recordResult.RecordId },
                    });
                }
            }

            result.Status = result.Records.Count == 0
                ? DeliveryStatus.Discovered
                : DeliveryStatusRules.Aggregate(result.Records.Select(r => r.Status).ToList());
            return result;
        }

        private static IReadOnlyDictionary<ObjectId, SectionSourceService.SampledSourceSnapshot>
            SnapshotSampledSourceNames(
            Database db,
            Transaction tr,
            SectionPlan plan,
            SectionApplyResult applied)
        {
            var groupIds = new HashSet<ObjectId>();
            foreach (var applyRecord in applied.Records.Where(record =>
                         record.Status == DeliveryStatus.Applied))
            {
                var planRecord = plan.Records.FirstOrDefault(record =>
                    record.RecordId == applyRecord.RecordId);
                if (planRecord == null || planRecord.ManualSectionReuse != null)
                    continue;

                var sampleLine = OpenByHandle<CivilDb.SampleLine>(
                    db, tr, applyRecord.Handles.SampleLine);
                if (sampleLine == null) continue;

                try
                {
                    var groupId = sampleLine.GroupId;
                    if (!groupId.IsNull && !groupId.IsErased)
                        groupIds.Add(groupId);
                }
                catch
                {
                    // The normal verification checks below will report the missing
                    // linkage. Do not guess a group or invoke the native API.
                }
            }

            var snapshots = new Dictionary<ObjectId, SectionSourceService.SampledSourceSnapshot>();
            foreach (var slgId in groupIds)
            {
                try
                {
                    snapshots[slgId] =
                        SectionSourceService.SampledSourceNamesReadOnly(db, slgId);
                }
                catch (Exception ex)
                {
                    // Preserve the failed-read state explicitly. An empty list could
                    // equal an empty expectation and manufacture a false green.
                    snapshots[slgId] =
                        SectionSourceService.SampledSourceSnapshot.Invalid(ex.Message);
                }
            }

            return snapshots;
        }

        private static void VerifyOne(
            Database db,
            Transaction tr,
            CivilDocument civilDoc,
            SectionPlan plan,
            SectionPlanRecord planRecord,
            SectionApplyRecordResult applyRecord,
            SectionVerifyRecordResult recordResult,
            IReadOnlyDictionary<ObjectId, SectionSourceService.SampledSourceSnapshot>
                sampledSourcesByGroup,
            SectionOfficeBlockFingerprintDiagnostics? fingerprintDiagnostics,
            Action<SectionLayoutInputCaptureRecord>? reportLayoutInputs)
        {
            void Check(string name, string? expected, string? actual, bool pass) =>
                recordResult.Checks.Add(new SectionVerifyCheck
                { Check = name, Expected = expected, Actual = actual, Pass = pass });

            var manualReuse = planRecord.ManualSectionReuse;
            var isManualReuse = manualReuse != null;

            // ------------------------------------------------------- sample line
            var expectedSampleLineHandle = manualReuse?.SampleLineHandle ?? applyRecord.Handles.SampleLine;
            if (manualReuse != null)
            {
                Check("manual_apply_sample_line_handle_exact", manualReuse.SampleLineHandle,
                    applyRecord.Handles.SampleLine,
                    SameHandle(manualReuse.SampleLineHandle, applyRecord.Handles.SampleLine));
                Check("manual_apply_group_handle_exact", manualReuse.SampleLineGroupHandle,
                    applyRecord.Handles.SampleLineGroup,
                    SameHandle(manualReuse.SampleLineGroupHandle, applyRecord.Handles.SampleLineGroup));
                Check("manual_apply_section_view_handle_exact", manualReuse.SectionViewHandle,
                    applyRecord.Handles.SectionView,
                    SameHandle(manualReuse.SectionViewHandle, applyRecord.Handles.SectionView));
            }

            CivilDb.SampleLine? sl = OpenByHandle<CivilDb.SampleLine>(db, tr, expectedSampleLineHandle);
            Check("sample_line_exists", "exists", sl == null ? "missing" : "exists", sl != null);
            if (sl == null) return;

            if (!isManualReuse)
            {
                planRecord.PlannedStyles.TryGetValue("sample_line_style", out var expectedSampleLineStyle);
                string? actualSampleLineStyle = null;
                string? sampleLineStyleError = null;
                try
                {
                    var styleId = sl.StyleId;
                    if (!styleId.IsNull && !styleId.IsErased &&
                        tr.GetObject(styleId, OpenMode.ForRead) is CivilDb.Styles.StyleBase style)
                        actualSampleLineStyle = style.Name;
                }
                catch (Exception ex) { sampleLineStyleError = ex.Message; }
                Check("sample_line_style", expectedSampleLineStyle ?? "planned style required",
                    actualSampleLineStyle ?? sampleLineStyleError ?? "unreadable",
                    SectionSampleLineStyleLogic.Matches(expectedSampleLineStyle, actualSampleLineStyle));
            }

            var sectionChildrenReadable = TryReadSectionChildren(
                tr, sl, out var liveSectionIds, out var liveSectionHandles,
                out var liveSectionSources,
                out var sectionChildrenError);
            Check("section_children_readable", "all Section children readable and unique",
                sectionChildrenReadable
                    ? $"{liveSectionHandles.Count} readable"
                    : sectionChildrenError,
                sectionChildrenReadable);
            var expectedSectionHandles = applyRecord.Handles.Sections
                .Select(handle => handle?.Trim().ToUpperInvariant() ?? string.Empty)
                .ToList();
            var expectedSectionHandlesValid = expectedSectionHandles.Count ==
                                                  expectedSectionHandles
                                                      .Distinct(StringComparer.OrdinalIgnoreCase).Count() &&
                                              expectedSectionHandles.All(handle =>
                                                  !string.IsNullOrWhiteSpace(handle));
            var exactSectionHandleSet = sectionChildrenReadable && expectedSectionHandlesValid &&
                expectedSectionHandles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(liveSectionHandles);
            Check("section_children_apply_evidence_exact",
                expectedSectionHandles.Count == 0
                    ? "(none)"
                    : string.Join(", ", expectedSectionHandles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                liveSectionHandles.Count == 0
                    ? "(none)"
                    : string.Join(", ", liveSectionHandles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                exactSectionHandleSet);

            var meta = SectionOwnershipService.Read(tr, sl);
            if (isManualReuse)
            {
                Check("manual_sample_line_remains_foreign", "no Mahod ownership",
                    meta == null ? "no Mahod ownership" : $"{meta.Feature}/{meta.Role}/{meta.LogicalKey}",
                    meta == null);
            }
            else
            {
                Check("sample_line_ownership", $"sections/sample-line/{planRecord.LogicalKey}",
                    meta == null ? "missing" : $"{meta.Feature}/{meta.Role}/{meta.LogicalKey}",
                    meta != null &&
                    string.Equals(meta.Feature, "sections", StringComparison.Ordinal) &&
                    string.Equals(meta.Role, "sample-line", StringComparison.Ordinal) &&
                    meta.LogicalKey == planRecord.LogicalKey);
                Check("ownership_fingerprint", planRecord.InputFingerprint, meta?.InputFingerprint,
                    meta != null && meta.InputFingerprint == planRecord.InputFingerprint);
            }

            // Geometry: the sample line vertices must be the CL WCS endpoints.
            var vertices = new List<(double X, double Y)>();
            try
            {
                var vcol = sl.Vertices;
                for (int i = 0; i < vcol.Count; i++)
                {
                    var loc = vcol[i].Location;
                    vertices.Add((loc.X, loc.Y));
                }
            }
            catch { }

            // Civil stores a sample line with a vertex AT the alignment crossing, so a
            // two-point CL comes back as three vertices, often in reversed order. The
            // engineering truth is the two ENDS: same extents, same swath. Found live
            // on 6422 (2026-08-19) - 9/10 checks passed and this one failed on form.
            bool geomOk = EndpointsMatch(vertices, planRecord.Cl.WcsEndpoints);
            Check("sample_line_geometry",
                FormatEndpoints(planRecord.Cl.WcsEndpoints),
                string.Join(" ", vertices.Select(v => $"({v.X:F3},{v.Y:F3})")),
                geomOk);

            // Station against plan.
            double actualStation = double.NaN;
            try { actualStation = sl.Station; } catch { }
            bool stationOk = planRecord.Station.HasValue &&
                             Math.Abs(actualStation - planRecord.Station.Value) < 0.05;
            Check("station", planRecord.Station?.ToString("F3"), actualStation.ToString("F3"), stationOk);

            // Group membership.
            ObjectId slgId = ObjectId.Null;
            try { slgId = sl.GroupId; } catch { }

            string? groupHandle = null;
            try { groupHandle = slgId.Handle.ToString(); } catch { }
            var expectedGroupHandle = manualReuse?.SampleLineGroupHandle ??
                                      applyRecord.Handles.SampleLineGroup;
            Check(isManualReuse ? "manual_group_linkage" : "group_membership",
                expectedGroupHandle, groupHandle,
                SameHandle(expectedGroupHandle, groupHandle));

            if (manualReuse == null)
            {
                // The write-open readback was snapshotted once per group before this
                // loop opened any group in the outer transaction. Never call the
                // native API here: the second record in a shared group would attempt
                // a nested write-open while the outer transaction holds it ForRead.
                var sourceSnapshot = !slgId.IsNull &&
                                     sampledSourcesByGroup.TryGetValue(slgId, out var snapshot)
                    ? snapshot
                    : SectionSourceService.SampledSourceSnapshot.Invalid(
                        "Managed SampleLineGroup was not available for strict source read-back.");
                var sampledNames = sourceSnapshot.Names;
                Check("group_sources_readable", "complete typed source snapshot",
                    sourceSnapshot.IsValid
                        ? $"{sourceSnapshot.Entries.Count} complete source identities"
                        : sourceSnapshot.Error ?? "unreadable",
                    sourceSnapshot.IsValid);

                CivilDb.SampleLineGroup? managedGroup = slgId.IsNull
                    ? null
                    : tr.GetObject(slgId, OpenMode.ForRead) as CivilDb.SampleLineGroup;
                var groupMeta = managedGroup == null ? null : SectionOwnershipService.Read(tr, managedGroup);
                var expectedGroupKey = LogicalKeys.ForSectionObject(
                    plan.ProjectProfileId,
                    planRecord.Cl.SourceDrawingHash,
                    "GROUP",
                    planRecord.SelectedAlignment ?? string.Empty,
                    "sample-line-group");
                Check("sample_line_group_ownership",
                    $"sections/sample-line-group/{expectedGroupKey}",
                    groupMeta == null
                        ? "missing"
                        : $"{groupMeta.Feature}/{groupMeta.Role}/{groupMeta.LogicalKey}",
                    groupMeta != null &&
                    string.Equals(groupMeta.Feature, "sections", StringComparison.Ordinal) &&
                    string.Equals(groupMeta.Role, "sample-line-group", StringComparison.Ordinal) &&
                    groupMeta.LogicalKey == expectedGroupKey);

                // Sampling belongs to the managed group, not an individual sample
                // line. Verify the exact alignment-wide union, by name.
                var expectedGroupSampling = SectionPlanLogic.ExpectedGroupSampling(
                    plan, planRecord.SelectedAlignment ?? string.Empty);
                var identityVerdict = CompareExpectedSourceIdentities(
                    plan, planRecord.SelectedAlignment ?? string.Empty,
                    expectedGroupSampling.Names, sourceSnapshot);
                Check("group_source_identity_and_cardinality_exact",
                    identityVerdict.Expected,
                    identityVerdict.Actual,
                    identityVerdict.Pass);

                var expectedChildSources = sourceSnapshot.Entries.Select(entry =>
                        new SectionSourceIntegrityLogic.SourceIdentity(
                            entry.Name, entry.Kind, entry.Handle))
                    .ToList();
                var actualChildSources = liveSectionSources.Select(entry =>
                        new SectionSourceIntegrityLogic.SourceIdentity(
                            entry.Name, entry.Kind, entry.Handle))
                    .ToList();
                var childSourcesExact = sectionChildrenReadable && sourceSnapshot.IsValid &&
                    SectionSourceIntegrityLogic.ExactSourceIdentitySet(
                        expectedChildSources, actualChildSources);
                Check("section_children_source_identity_exact",
                    FormatSourceIdentities(expectedChildSources),
                    sectionChildrenReadable
                        ? FormatSourceIdentities(actualChildSources)
                        : sectionChildrenError,
                    childSourcesExact);
                var missingGroupSources = expectedGroupSampling.Names
                    .Where(name => !sampledNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                Check("expected_group_sources_sampled",
                    expectedGroupSampling.Names.Count == 0
                        ? "(none)"
                        : string.Join(", ", expectedGroupSampling.Names),
                    sampledNames.Count == 0 ? "(none)" : string.Join(", ", sampledNames),
                    sourceSnapshot.IsValid && missingGroupSources.Count == 0);

                var unexpectedSources = sampledNames
                    .Where(name => !expectedGroupSampling.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                Check("unexpected_sources_not_sampled",
                    "(none)",
                    unexpectedSources.Count == 0 ? "(none)" : string.Join(", ", unexpectedSources),
                    sourceSnapshot.IsValid && unexpectedSources.Count == 0);

                var expectedUtilities = planRecord.UtilityCoverage?.Represented ?? new List<string>();
                if (expectedUtilities.Count > 0)
                {
                    var missing = expectedUtilities
                        .Where(u => !sampledNames.Contains(u, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    Check("utilities_sampled_by_name",
                        string.Join(", ", expectedUtilities.OrderBy(x => x, StringComparer.Ordinal)),
                        sampledNames.Count == 0 ? "(none)" : string.Join(", ", sampledNames),
                        sourceSnapshot.IsValid && missing.Count == 0);
                }

                var requiredSourceNames = planRecord.PlannedSources
                    .Where(s => s.Required && s.PlannedState == "sampled")
                    .Select(s => s.SourceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var missingRequiredSources = requiredSourceNames
                    .Where(name => !sampledNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                Check("required_sources_sampled_by_name",
                    requiredSourceNames.Count == 0 ? "(none)" : string.Join(", ", requiredSourceNames),
                    sampledNames.Count == 0 ? "(none)" : string.Join(", ", sampledNames),
                    sourceSnapshot.IsValid && missingRequiredSources.Count == 0);

                var sectionCount = sectionChildrenReadable ? liveSectionIds.Count : -1;
                var requiredSources = requiredSourceNames.Count;
                Check("sections_sampled", $">={requiredSources}", sectionCount.ToString(),
                    sectionChildrenReadable && sectionCount >= requiredSources &&
                    (requiredSources == 0 || sectionCount > 0));
            }

            // The planned surface pair is ordered by PLAN as existing-ground then
            // design. Prove each exact SourceName against the live Section and its
            // visible style; never infer EG/FG from whichever sections happen to be
            // first in Civil's collection.
            var plannedSurfacePair = planRecord.PlannedSources
                .Where(source => source.Required &&
                                 string.Equals(source.PlannedState, "sampled", StringComparison.Ordinal) &&
                                 string.Equals(source.SourceType, "surface", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var plannedSurfacePairValid = plannedSurfacePair.Count == 2 &&
                !string.Equals(plannedSurfacePair[0].SourceName,
                    plannedSurfacePair[1].SourceName, StringComparison.OrdinalIgnoreCase);
            var liveSurfaces = new List<LiveSurfacePresentationEvidence>();
            var liveSurfaceError = string.Empty;
            var liveSurfaceReadable = sectionChildrenReadable &&
                TryReadSurfacePresentationEvidence(
                    tr, liveSectionIds, out liveSurfaces, out liveSurfaceError);
            if (!sectionChildrenReadable)
                liveSurfaceError = sectionChildrenError;
            var exactSurfaceMap = plannedSurfacePairValid && liveSurfaceReadable &&
                SectionSourceIntegrityLogic.ExactNameSet(
                    plannedSurfacePair.Select(source => source.SourceName).ToList(),
                    liveSurfaces.Select(surface => surface.SourceName).ToList());
            Check("planned_surface_source_to_live_section_exact",
                plannedSurfacePairValid
                    ? string.Join(" | ", plannedSurfacePair.Select(source => source.SourceName))
                    : "one distinct ordered EG/FG pair",
                liveSurfaceReadable
                    ? string.Join(" | ", liveSurfaces.Select(surface => surface.SourceName))
                    : liveSurfaceError,
                exactSurfaceMap);

            if (exactSurfaceMap)
            {
                var liveExisting = liveSurfaces.Single(surface => string.Equals(
                    surface.SourceName, plannedSurfacePair[0].SourceName,
                    StringComparison.OrdinalIgnoreCase));
                var liveDesign = liveSurfaces.Single(surface => string.Equals(
                    surface.SourceName, plannedSurfacePair[1].SourceName,
                    StringComparison.OrdinalIgnoreCase));

                if (isManualReuse)
                {
                    var manualDistinctVisible = liveExisting.Visible && liveDesign.Visible &&
                        !string.IsNullOrWhiteSpace(liveExisting.StyleName) &&
                        !string.IsNullOrWhiteSpace(liveDesign.StyleName) &&
                        !string.Equals(liveExisting.StyleName, liveDesign.StyleName,
                            StringComparison.OrdinalIgnoreCase);
                    Check("manual_eg_fg_sources_visible_distinct",
                        "exact planned EG+FG; two readable visible distinct foreign styles",
                        $"EG={liveExisting.SourceName}/{liveExisting.StyleName}/visible={liveExisting.Visible}; " +
                        $"FG={liveDesign.SourceName}/{liveDesign.StyleName}/visible={liveDesign.Visible}",
                        manualDistinctVisible);
                }
                else
                {
                    var existingExact = SectionSurfaceStyleContractLogic.TryValidateLive(
                        SectionSurfaceStyleContractLogic.Existing,
                        liveExisting.StyleName,
                        liveExisting.StyleDescription,
                        liveExisting.ColorIndex,
                        liveExisting.ColorMethod,
                        liveExisting.Visible,
                        liveExisting.Linetype,
                        out var existingError) &&
                        EffectiveLinetypeScaleExact(db, liveExisting.LinetypeScale);
                    var designExact = SectionSurfaceStyleContractLogic.TryValidateLive(
                        SectionSurfaceStyleContractLogic.Design,
                        liveDesign.StyleName,
                        liveDesign.StyleDescription,
                        liveDesign.ColorIndex,
                        liveDesign.ColorMethod,
                        liveDesign.Visible,
                        liveDesign.Linetype,
                        out var designError) &&
                        EffectiveLinetypeScaleExact(db, liveDesign.LinetypeScale);
                    Check("managed_eg_fg_styles_live_exact",
                        $"{SectionDecorationService.ExistingStyleName} / " +
                        SectionDecorationService.DesignStyleName,
                        $"EG={liveExisting.StyleName}: {existingError}; scale={liveExisting.LinetypeScale}; " +
                        $"FG={liveDesign.StyleName}: {designError}; scale={liveDesign.LinetypeScale}",
                        existingExact && designExact &&
                        !string.Equals(liveExisting.StyleName, liveDesign.StyleName,
                            StringComparison.OrdinalIgnoreCase));
                }
            }

            // ------------------------------------------------------ section view
            var expectedSectionViewHandle = manualReuse?.SectionViewHandle ?? applyRecord.Handles.SectionView;
            CivilDb.SectionView? sv = OpenByHandle<CivilDb.SectionView>(db, tr, expectedSectionViewHandle);
            Check("section_view_exists", "exists", sv == null ? "missing" : "exists", sv != null);
            if (sv != null)
            {
                var svMeta = SectionOwnershipService.Read(tr, sv);
                string? refHandle = null;
                try
                {
                    var refSl = tr.GetObject(sv.SampleLineId, OpenMode.ForRead) as CivilDb.SampleLine;
                    refHandle = refSl?.Handle.ToString();
                }
                catch { }

                if (manualReuse != null)
                {
                    Check("manual_section_view_remains_foreign", "no Mahod ownership",
                        svMeta == null
                            ? "no Mahod ownership"
                            : $"{svMeta.Feature}/{svMeta.Role}/{svMeta.LogicalKey}",
                        svMeta == null);
                    Check("manual_view_references_exact_sample_line",
                        manualReuse.SampleLineHandle, refHandle,
                        SameHandle(manualReuse.SampleLineHandle, refHandle));

                    var styleReadable = TryReadSectionViewStyle(tr, sv, out var actualStyle);
                    Check("manual_section_view_style_preserved",
                        manualReuse.PreservedSectionViewStyle ?? "(none)",
                        styleReadable ? actualStyle ?? "(none)" : "(unreadable)",
                        styleReadable && string.Equals(
                            manualReuse.PreservedSectionViewStyle, actualStyle,
                            StringComparison.OrdinalIgnoreCase));

                    var bandsReadable = TryReadBandSetEvidence(
                        tr, sv, out var actualBands, out _, out _);
                    var normalizedActualBands = actualBands
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    var expectedBands = manualReuse.PreservedBandStyles
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    Check("manual_band_styles_preserved",
                        FormatNames(expectedBands),
                        bandsReadable ? FormatNames(normalizedActualBands) : "(unreadable)",
                        bandsReadable && expectedBands.SequenceEqual(
                            normalizedActualBands, StringComparer.OrdinalIgnoreCase));

                    var presentationReadable =
                        SectionViewPresentationStyleService.TryReadSingleDatumCompatibility(
                            tr, sv, out var presentationCompatible,
                            out var presentationEvidence);
                    Check("manual_single_datum_presentation_compatible",
                        "all native display components hidden; zero native band items",
                        presentationEvidence,
                        presentationReadable && presentationCompatible);
                }
                else
                {
                    Check("section_view_ownership",
                        $"sections/section-view/{planRecord.LogicalKey}",
                        svMeta == null
                            ? "missing"
                            : $"{svMeta.Feature}/{svMeta.Role}/{svMeta.LogicalKey}",
                        svMeta != null &&
                        string.Equals(svMeta.Feature, "sections", StringComparison.Ordinal) &&
                        string.Equals(svMeta.Role, "section-view", StringComparison.Ordinal) &&
                        svMeta.LogicalKey == planRecord.LogicalKey &&
                        svMeta.InputFingerprint == planRecord.InputFingerprint);
                    Check("view_references_sample_line", applyRecord.Handles.SampleLine, refHandle,
                        SameHandle(applyRecord.Handles.SampleLine, refHandle));

                    var styleReadable = TryReadSectionViewStyle(tr, sv, out var actualStyle);
                    planRecord.PlannedStyles.TryGetValue("section_view_style", out var expectedStyle);
                    Check("managed_section_view_style_exact", expectedStyle, 
                        styleReadable ? actualStyle : "(unreadable)",
                        styleReadable && !string.IsNullOrWhiteSpace(expectedStyle) &&
                        string.Equals(expectedStyle, actualStyle, StringComparison.OrdinalIgnoreCase));

                    var rangeReadable = TryReadElevationRange(
                        sv, out var automaticRange, out var elevationMin, out var elevationMax);
                    var elevationSpan = elevationMax - elevationMin;
                    var maxPinnedSpan = SectionDecorationService.MaxViewSpanM +
                        SectionTrafficDirectionAnnotationLogic.MaxManagedViewEnvelopePaddingM;
                    Check("managed_elevation_range_pinned",
                        $"automatic=false; finite span <= {maxPinnedSpan:F2}m",
                        rangeReadable
                            ? $"automatic={automaticRange}; {elevationMin:F2}..{elevationMax:F2} ({elevationSpan:F2}m)"
                            : "(unreadable)",
                        rangeReadable && !automaticRange && Finite(elevationMin) &&
                        Finite(elevationMax) && elevationMax > elevationMin &&
                        elevationSpan <= maxPinnedSpan + 0.01);

                    // SEC-B3 (review of 1.3.9): the band must be the drawn content plus
                    // the drafting margin, not a padded tower. The APPLY evidence is
                    // re-derived from its own EG/FG/utility/arrow inputs and the live
                    // view must still carry exactly that band.
                    var bandProven = SectionViewElevationBandLogic.TryParseAndRecompute(
                        applyRecord.ElevationBandEvidence, SectionDecorationService.MaxViewSpanM,
                        out var contentBand, out var contentBandError) && contentBand != null;
                    Check("managed_elevation_range_content_bound",
                        bandProven
                            ? FormattableString.Invariant(
                                $"{contentBand!.Min:F2}..{contentBand.Max:F2} = drawn content ± {SectionViewElevationBandLogic.DraftingMarginM:F2}m")
                            : contentBandError,
                        rangeReadable
                            ? FormattableString.Invariant($"{elevationMin:F2}..{elevationMax:F2}")
                            : "(unreadable)",
                        bandProven && rangeReadable &&
                        Math.Abs(elevationMin - contentBand!.Min) <= 0.01 &&
                        Math.Abs(elevationMax - contentBand.Max) <= 0.01);

                    var bandsReadable = TryReadBandSetEvidence(
                        tr, sv, out var actualBandNames,
                        out var actualBottomBandIds, out var actualTopBandIds);
                    planRecord.PlannedStyles.TryGetValue("band_set_style", out var plannedBandSet);
                    var expectsNoBands = string.IsNullOrWhiteSpace(plannedBandSet) ||
                                         plannedBandSet.StartsWith("(none;", StringComparison.Ordinal);
                    var expectedBottomBandIds = new List<string>();
                    var expectedTopBandIds = new List<string>();
                    var configuredReadable = expectsNoBands ||
                        TryReadConfiguredBandSetEvidence(
                            tr, civilDoc, plannedBandSet!,
                            out expectedBottomBandIds, out expectedTopBandIds);
                    var exactBandContract = bandsReadable && configuredReadable &&
                        (expectsNoBands
                            ? actualBottomBandIds.Count + actualTopBandIds.Count == 0
                            : expectedBottomBandIds.Count + expectedTopBandIds.Count > 0 &&
                              expectedBottomBandIds.SequenceEqual(
                                  actualBottomBandIds, StringComparer.OrdinalIgnoreCase) &&
                              expectedTopBandIds.SequenceEqual(
                                  actualTopBandIds, StringComparer.OrdinalIgnoreCase));
                    Check("managed_band_contract",
                        expectsNoBands ? "(none; single datum required)" : plannedBandSet,
                        bandsReadable
                            ? $"names={FormatNames(actualBandNames)}; " +
                              $"bottom={FormatNames(actualBottomBandIds)}; " +
                              $"top={FormatNames(actualTopBandIds)}"
                            : "(unreadable)",
                        exactBandContract);
                }
            }

            // The registry is the ownership boundary around a foreign manual view and
            // the visible-deliverable contract around a managed view. Read every
            // primary handle; do not infer ownership by scanning model space.
            var annotationRead = string.IsNullOrEmpty(applyRecord.LogicalKey)
                ? new AnnotationRegistryReadResult
                {
                    IsValid = false,
                    Error = "logical key is missing",
                }
                : SectionAnnotationRegistry.ReadAnnotationContractEvidence(
                    tr, db, applyRecord.LogicalKey, fingerprintDiagnostics);
            Check("annotation_registry_readable", "readable existing entry",
                annotationRead.IsValid && annotationRead.EntryExists
                    ? "readable existing entry"
                    : annotationRead.Error ?? "missing entry",
                annotationRead.IsValid && annotationRead.EntryExists);

            SectionAnnotationPlacementContract.SurfaceChains? placementSurfaces = null;
            var placementSurfaceError = string.Empty;
            if (sv != null)
            {
                try
                {
                    placementSurfaces = SectionAnnotationPlacementContract.ReadSurfaceChains(
                        tr, sl, planRecord,
                        sv.OffsetLeft, sv.OffsetRight, sv.ElevationMin, sv.ElevationMax);
                }
                catch (Exception ex)
                {
                    placementSurfaceError = ex.Message;
                }
            }
            else
            {
                placementSurfaceError = "SectionView is missing.";
            }
            Check("annotation_ground_sources_live_exact",
                "one exact PLAN-selected EG+FG child pair with finite sampled chains",
                placementSurfaces == null
                    ? placementSurfaceError
                    : $"EG={placementSurfaces.Existing.Count}; FG={placementSurfaces.Design.Count}",
                placementSurfaces != null);

            SectionAnnotationPlacementContract.MeasuredLabelLayout? measuredLayout = null;
            string? measuredLayoutError = null;
            // Selected-record VERIFY only (reportLayoutInputs != null): the detached
            // solver input lives outside the guarded try, so a refused solver still
            // reports the exact input it refused. The observer is assignment only and
            // its snapshot is never a PASS. Batch VERIFY keeps the five-argument call.
            SectionMeasuredLayoutInputs? capturedInputs = null;
            try
            {
                if (sv == null || placementSurfaces == null || !annotationRead.IsValid ||
                    !annotationRead.EntryExists || annotationRead.Entries.Any(e => !e.IsLive || !e.OwnershipValid))
                    throw new InvalidOperationException("Layout requires readable owned annotations and exact live surfaces.");
                var nativeAnnotations = annotationRead.Entries.Select(entry =>
                    tr.GetObject(entry.EntityId, OpenMode.ForRead) as Entity ??
                    throw new InvalidOperationException("Registered annotation is not a readable entity.")).ToList();
                measuredLayout = reportLayoutInputs == null
                    ? SectionAnnotationPlacementContract.ComputeLabelLayout(
                        sv, planRecord, applyRecord, placementSurfaces, nativeAnnotations)
                    : SectionAnnotationPlacementContract.ComputeLabelLayout(
                        sv, planRecord, applyRecord, placementSurfaces, nativeAnnotations,
                        inputs => capturedInputs = inputs);
                SectionAnnotationPlacementContract.RequirePlacedLabelBounds(measuredLayout, nativeAnnotations);
                SectionAnnotationPlacementContract.RequireLayoutLeaders(measuredLayout, nativeAnnotations);
            }
            catch (Exception ex) { measuredLayoutError = ex.Message; }
            Check("native_measured_label_layout_exact",
                "PLAN-derived anchors, measured native ink, collision-free label placement and exact leaders",
                measuredLayoutError ?? $"{measuredLayout?.ExpectedPositionsByHandle.Count ?? 0} measured labels",
                measuredLayout != null && measuredLayoutError == null);
            // Diagnostic receipt, built after the unchanged check from values already
            // read above (no native re-read). Null-conditional: batch never builds it.
            reportLayoutInputs?.Invoke(SectionLayoutInputCapture.ForRecord(
                applyRecord.RecordId, applyRecord.LogicalKey, sv?.Handle.ToString(),
                annotationRead.IsValid && annotationRead.EntryExists
                    ? SectionLayoutInputCapture.Metadata(
                        annotationRead.Entries, applyRecord.CorePresentationAnnotations)
                    : null,
                annotationRead.Error ?? "missing entry",
                capturedInputs,
                measuredLayout == null ? null : SectionLayoutInputCapture.Output(measuredLayout),
                measuredLayoutError));

            var registeredAnnotations = annotationRead.Entries.Count(e => e.IsLive);
            Check("registered_annotations", $">=1 (apply={applyRecord.AnnotationCount})",
                registeredAnnotations.ToString(),
                annotationRead.IsValid && annotationRead.EntryExists &&
                applyRecord.AnnotationCount > 0 &&
                annotationRead.Entries.Count == applyRecord.AnnotationCount &&
                registeredAnnotations == applyRecord.AnnotationCount);
            Check("registered_annotations_owned_layer", SectionDecorationService.AnnoLayer,
                annotationRead.Entries.Count == 0
                    ? "(none)"
                    : string.Join(", ", annotationRead.Entries
                        .Select(e => e.Layer ?? "(missing)").Distinct(StringComparer.OrdinalIgnoreCase)),
                annotationRead.Entries.Count > 0 && annotationRead.Entries.All(e =>
                    e.IsLive && string.Equals(e.Layer, SectionDecorationService.AnnoLayer,
                        StringComparison.OrdinalIgnoreCase)));

            var primarySemanticsExact = annotationRead.Entries.Count > 0 &&
                annotationRead.Entries.All(entry =>
                    SectionVehicleDirectionPlanner.IsSha256(entry.RegisteredFingerprint) &&
                    SectionVehicleDirectionPlanner.IsSha256(entry.LiveFingerprint) &&
                    string.Equals(entry.RegisteredFingerprint, entry.LiveFingerprint,
                        StringComparison.OrdinalIgnoreCase));
            Check("registered_annotations_unchanged_since_apply",
                $"{annotationRead.Entries.Count} exact APPLY-time SHA-256 fingerprints",
                $"exact={annotationRead.Entries.Count(entry =>
                    SectionVehicleDirectionPlanner.IsSha256(entry.RegisteredFingerprint) &&
                    string.Equals(entry.RegisteredFingerprint, entry.LiveFingerprint,
                        StringComparison.OrdinalIgnoreCase))}",
                annotationRead.IsValid && primarySemanticsExact);
            Check("registered_annotations_ownership_exact",
                "every annotation carries matching Mahod logical-key + semantic fingerprint ownership",
                $"owned={annotationRead.Entries.Count(entry => entry.OwnershipValid)}/" +
                annotationRead.Entries.Count,
                annotationRead.IsValid && annotationRead.Entries.Count > 0 &&
                annotationRead.Entries.All(entry => entry.OwnershipValid));

            // Full resource contracts (1.2.27): the same Core validators APPLY used.
            var layerReadable = TryReadAnnotationLayerState(tr, db, out var layerState, out var layerError);
            IReadOnlyList<string> layerViolations = layerState == null
                ? new[] { layerError }
                : SectionAnnotationResourceContracts.ValidateLayer(layerState);
            Check("annotation_layer_visible_plottable",
                "exists; on; unfrozen globally/in every paper viewport; no viewport overrides; " +
                "plottable; unlocked; visible; color=7 (ByAci); explicit opaque alpha; " +
                "Continuous; default lineweight",
                layerReadable && layerState != null
                    ? $"on={!layerState.IsOff}; unfrozen={!layerState.IsFrozen}; plottable={layerState.IsPlottable}; " +
                      $"unlocked={!layerState.IsLocked}; hidden={layerState.IsHidden}; " +
                      $"vp-default-frozen={layerState.ViewportVisibilityDefault}; vp-overrides={layerState.HasViewportOverrides}; " +
                      $"vp-scan={layerState.ViewportScanComplete}; vp-frozen={layerState.FrozenPaperViewportCount}; " +
                      $"color={layerState.ColorIndex}; alpha-mode={layerState.TransparencyIsByAlpha}; " +
                      $"alpha={layerState.TransparencyAlpha}; linetype={layerState.LinetypeName}; " +
                      $"lineweight={layerState.LineWeight}; annotative={layerState.Annotative}" +
                      (layerViolations.Count == 0 ? string.Empty : " ✗ " + string.Join("; ", layerViolations))
                    : $"(unreadable: {layerError})",
                layerReadable && layerViolations.Count == 0);

            var textStyleReadable = TryReadAnnotationTextStyle(tr, db, out var textStyleState, out var textStyleError);
            IReadOnlyList<string> textStyleViolations = textStyleState == null
                ? new[] { textStyleError }
                : SectionAnnotationResourceContracts.ValidateTextStyle(textStyleState);
            Check("annotation_text_style_arial_truetype",
                $"{SectionDecorationService.AnnoTextStyle}; Arial regular; shape=false; bigfont=(none); " +
                "xscale=1; oblique=0; vertical=false; flags=0; fixed-height=0; non-annotative",
                textStyleReadable && textStyleState != null
                    ? $"{textStyleState.Typeface}; shape={textStyleState.IsShapeFile}; " +
                      $"bigfont={(string.IsNullOrWhiteSpace(textStyleState.BigFontFile) ? "(none)" : textStyleState.BigFontFile)}; " +
                      $"xscale={textStyleState.XScale.ToString("R", CultureInfo.InvariantCulture)}; " +
                      $"oblique={textStyleState.ObliquingAngleRad.ToString("R", CultureInfo.InvariantCulture)}; " +
                      $"vertical={textStyleState.IsVertical}; flags={textStyleState.FlagBits}; " +
                      $"fixed-height={textStyleState.TextSize.ToString("R", CultureInfo.InvariantCulture)}; " +
                      $"bold={textStyleState.Bold}; italic={textStyleState.Italic}; " +
                      $"charset={textStyleState.CharacterSet}; pitch={textStyleState.PitchAndFamily}; " +
                      $"annotative={textStyleState.Annotative}; paper={textStyleState.PaperOrientation}" +
                      (textStyleViolations.Count == 0 ? string.Empty : " ✗ " + string.Join("; ", textStyleViolations))
                    : $"(unreadable: {textStyleError})",
                textStyleReadable && textStyleViolations.Count == 0);

            var linetypesReadable = TryReadInvalidLinetypes(tr, db, out var invalidLinetypes, out var linetypeError);
            Check("annotation_linetypes_semantics_exact",
                string.Join(", ", SectionAnnotationResourceContracts.RequiredLinetypes) +
                " exact protected dash/shape/text contracts",
                linetypesReadable
                    ? invalidLinetypes.Count == 0 ? "all exact" : string.Join(" | ", invalidLinetypes)
                    : $"(unreadable: {linetypeError})",
                linetypesReadable && invalidLinetypes.Count == 0);
            var registeredTexts = annotationRead.Entries
                .Where(entry => string.Equals(
                    entry.EntityType, nameof(DBText), StringComparison.Ordinal))
                .ToList();
            Check("registered_text_uses_owned_truetype_style",
                SectionDecorationService.AnnoTextStyle,
                registeredTexts.Count == 0
                    ? "(none)"
                    : string.Join(", ", registeredTexts.Select(entry =>
                        entry.TextStyleName ?? "(unreadable)").Distinct(
                            StringComparer.OrdinalIgnoreCase)),
                registeredTexts.Count > 0 && registeredTexts.All(entry =>
                    string.Equals(entry.TextStyleName,
                        SectionDecorationService.AnnoTextStyle,
                        StringComparison.OrdinalIgnoreCase)));

            Check("annotation_contract_version",
                $">={SectionAnnotationContractLogic.CurrentVersion}",
                applyRecord.AnnotationContractVersion.ToString(CultureInfo.InvariantCulture),
                applyRecord.AnnotationContractVersion >= SectionAnnotationContractLogic.CurrentVersion);

            // The visible dash length depends on LTSCALE, MSLTSCALE and the annotation
            // scale — none of which live on the entity, so the semantics fingerprint
            // cannot see them drift. APPLY recorded the state it drew against.
            var displayReadable = TryReadLinetypeDisplayContract(db, out var liveDisplay, out var displayError);
            Check("annotation_linetype_display_exact",
                string.IsNullOrWhiteSpace(applyRecord.LinetypeDisplayEvidence)
                    ? "(missing)" : applyRecord.LinetypeDisplayEvidence,
                displayReadable ? liveDisplay : $"(unreadable: {displayError})",
                displayReadable &&
                !string.IsNullOrWhiteSpace(applyRecord.LinetypeDisplayEvidence) &&
                !liveDisplay.EndsWith(";entity_scale=(invalid)", StringComparison.Ordinal) &&
                string.Equals(applyRecord.LinetypeDisplayEvidence, liveDisplay, StringComparison.Ordinal));

            var plannedCoverage = planRecord.PresentationCoverage;
            var appliedCoverage = applyRecord.PresentationCoverage;
            Check("presentation_coverage_complete", "true", appliedCoverage.Complete.ToString(),
                plannedCoverage.Complete && appliedCoverage.Complete);
            Check("presentation_coverage_digest_exact", plannedCoverage.EvidenceDigest,
                appliedCoverage.EvidenceDigest,
                !string.IsNullOrWhiteSpace(plannedCoverage.EvidenceDigest) &&
                string.Equals(plannedCoverage.EvidenceDigest, appliedCoverage.EvidenceDigest,
                    StringComparison.OrdinalIgnoreCase));
            var plannedCoverageCounts =
                $"marks={plannedCoverage.PlanMarkCount};dimensions={plannedCoverage.DimensionMarkCount};" +
                $"widths={plannedCoverage.WidthSpanCount};strips={plannedCoverage.NamedStripCount};" +
                $"vehicles={plannedCoverage.VehicleStripCount};cars={plannedCoverage.OfficeCarStripCount}";
            var appliedCoverageCounts =
                $"marks={appliedCoverage.PlanMarkCount};dimensions={appliedCoverage.DimensionMarkCount};" +
                $"widths={appliedCoverage.WidthSpanCount};strips={appliedCoverage.NamedStripCount};" +
                $"vehicles={appliedCoverage.VehicleStripCount};cars={appliedCoverage.OfficeCarStripCount}";
            Check("presentation_coverage_counts_exact", plannedCoverageCounts,
                appliedCoverageCounts, plannedCoverageCounts == appliedCoverageCounts);
            var plannedBoundary = FormattableString.Invariant(
                $"{plannedCoverage.BoundarySource}:{plannedCoverage.BoundaryFromM:R}..{plannedCoverage.BoundaryToM:R}");
            var appliedBoundary = FormattableString.Invariant(
                $"{appliedCoverage.BoundarySource}:{appliedCoverage.BoundaryFromM:R}..{appliedCoverage.BoundaryToM:R}");
            Check("presentation_outer_boundary_exact", plannedBoundary, appliedBoundary,
                string.Equals(plannedCoverage.BoundarySource, appliedCoverage.BoundarySource,
                    StringComparison.Ordinal) &&
                NullableOffsetExact(plannedCoverage.BoundaryFromM, appliedCoverage.BoundaryFromM) &&
                NullableOffsetExact(plannedCoverage.BoundaryToM, appliedCoverage.BoundaryToM));
            var plannedRowAuthority =
                $"{plannedCoverage.RowAuthorityState}|{plannedCoverage.AuthoritativeRowSourceKey}|" +
                string.Join(",", plannedCoverage.RowCandidateSourceKeys);
            var appliedRowAuthority =
                $"{appliedCoverage.RowAuthorityState}|{appliedCoverage.AuthoritativeRowSourceKey}|" +
                string.Join(",", appliedCoverage.RowCandidateSourceKeys);
            Check("row_authority_plan_apply_exact", plannedRowAuthority, appliedRowAuthority,
                string.Equals(plannedRowAuthority, appliedRowAuthority, StringComparison.Ordinal));
            var plannedOverrides = plannedCoverage.ExplicitSpanOverrides
                .OrderBy(item => item.OffsetM)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .Select(item => FormattableString.Invariant(
                    $"{item.OffsetM:R}:{item.Label}:{item.Source}:{item.Evidence}"));
            var appliedOverrides = appliedCoverage.ExplicitSpanOverrides
                .OrderBy(item => item.OffsetM)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .Select(item => FormattableString.Invariant(
                    $"{item.OffsetM:R}:{item.Label}:{item.Source}:{item.Evidence}"));
            Check("span_label_approvals_plan_apply_exact",
                string.Join("|", plannedOverrides), string.Join("|", appliedOverrides),
                plannedOverrides.SequenceEqual(appliedOverrides, StringComparer.Ordinal) &&
                plannedCoverage.UnresolvedSpans.Count == 0 &&
                appliedCoverage.UnresolvedSpans.Count == 0);

            var plannedOffsets = plannedCoverage.DimensionOffsets;
            var appliedOffsets = appliedCoverage.DimensionOffsets;
            var offsetPlansExact =
                plannedOffsets.Count == plannedCoverage.DimensionMarkCount &&
                appliedOffsets.Count == appliedCoverage.DimensionMarkCount &&
                plannedOffsets.Count == appliedOffsets.Count &&
                plannedOffsets.Zip(appliedOffsets,
                    (planned, applied) => Math.Abs(planned - applied) <= 0.0005).All(x => x);
            Check("dimension_offset_plan_apply_exact",
                string.Join(",", plannedOffsets.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
                string.Join(",", appliedOffsets.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
                offsetPlansExact);
            var plannedDimensionMarks = plannedCoverage.DimensionMarks.Select(mark =>
                SectionCorePresentationContract.Key(
                    "dimension-mark", mark.OffsetM, mark.Kind, mark.Label,
                    mark.ColorIndex, mark.GeometryKey,
                    SectionProjectionLogic.DimensionSourcesCanonical(mark.SourceEvidence ?? new()))).ToList();
            var appliedDimensionMarks = appliedCoverage.DimensionMarks.Select(mark =>
                SectionCorePresentationContract.Key(
                    "dimension-mark", mark.OffsetM, mark.Kind, mark.Label,
                    mark.ColorIndex, mark.GeometryKey,
                    SectionProjectionLogic.DimensionSourcesCanonical(mark.SourceEvidence ?? new()))).ToList();
            var dimensionMarkSemanticsExact =
                plannedDimensionMarks.Count == plannedCoverage.DimensionMarkCount &&
                appliedDimensionMarks.Count == appliedCoverage.DimensionMarkCount &&
                plannedDimensionMarks.SequenceEqual(appliedDimensionMarks, StringComparer.Ordinal) &&
                plannedDimensionMarks.Distinct(StringComparer.Ordinal).Count() ==
                    plannedDimensionMarks.Count;
            Check("dimension_mark_semantics_plan_apply_exact",
                string.Join(";", plannedDimensionMarks),
                string.Join(";", appliedDimensionMarks),
                dimensionMarkSemanticsExact);

            static bool NullableOffsetExact(double? expected, double? actual) =>
                expected.HasValue == actual.HasValue &&
                (!expected.HasValue || Math.Abs(expected.Value - actual!.Value) <= 0.0005);

            var parsedOffsetLabels = new List<SectionAnnotationContractLogic.DimensionOffsetLabelEvidence>();
            var invalidOffsetLabels = new List<string>();
            foreach (var labelText in applyRecord.DimensionOffsetLabels)
            {
                if (SectionAnnotationContractLogic.TryParseDimensionOffsetLabelReference(
                        labelText, out var parsed, out var error) && parsed != null)
                    parsedOffsetLabels.Add(parsed);
                else
                    invalidOffsetLabels.Add($"{labelText}: {error}");
            }
            var offsetLabelHandlesUnique = parsedOffsetLabels.Select(label => label.Handle)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == parsedOffsetLabels.Count;
            // Every label is judged once and the first failing property is named:
            // the first complete live VERIFY (07/09 18:20) reported "14 labels;
            // unique=True" for a rotation persisted as 3π/2 against an expected -π/2.
            var offsetLabelProblems = new List<string>();
            foreach (var label in parsedOffsetLabels)
            {
                if (sv == null)
                {
                    offsetLabelProblems.Add("no live SectionView");
                    break;
                }
                var bounded = label.PlacedOffset >= sv.OffsetLeft - 0.0005 &&
                              label.PlacedOffset <= sv.OffsetRight + 0.0005;
                if (!bounded)
                {
                    offsetLabelProblems.Add(FormattableString.Invariant(
                        $"{label.Handle}: placed offset {label.PlacedOffset:F3} outside the view {sv.OffsetLeft:F3}..{sv.OffsetRight:F3}"));
                    continue;
                }
                if (SectionAnnotationPlacementContract.DimensionLabelPosition(
                        sv, label.PlacedOffset, sv.ElevationMin) is not { } expectedPosition)
                {
                    offsetLabelProblems.Add($"{label.Handle}: the SectionView cannot map its placed offset");
                    continue;
                }
                var anchor = measuredLayout?.ExpectedPositionsByHandle.TryGetValue(
                    label.Handle, out var placedLabel) == true ? placedLabel : expectedPosition;
                var candidates = annotationRead.Entries.Where(entity =>
                    entity.IsLive &&
                    string.Equals(entity.Handle, label.Handle, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count != 1)
                {
                    offsetLabelProblems.Add($"{label.Handle}: {candidates.Count} live registered entities");
                    continue;
                }
                var entity = candidates[0];
                if (!string.Equals(entity.EntityType, nameof(DBText), StringComparison.Ordinal) ||
                    !string.Equals(entity.Layer, SectionDecorationService.AnnoLayer,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entity.Text, label.Text, StringComparison.Ordinal))
                {
                    offsetLabelProblems.Add($"{label.Handle}: {entity.EntityType} '{entity.Text}' on {entity.Layer}");
                    continue;
                }
                var mismatch = TextPlacementMismatch(
                    entity, anchor, height: 0.62,
                    rotation: -Math.PI / 2.0, centered: false, colorIndex: 7);
                if (mismatch != null)
                    offsetLabelProblems.Add($"{label.Handle}: {mismatch}");
            }
            var offsetLabelsExact = invalidOffsetLabels.Count == 0 && offsetPlansExact &&
                parsedOffsetLabels.Count == plannedOffsets.Count && offsetLabelHandlesUnique &&
                plannedOffsets.All(offset => parsedOffsetLabels.Count(label =>
                    Math.Abs(label.AnchorOffset - offset) <= 0.0005) == 1) &&
                offsetLabelProblems.Count == 0;
            Check("dimension_offset_labels_live_exact",
                $"{plannedOffsets.Count} bounded registered offset labels",
                invalidOffsetLabels.Count > 0
                    ? string.Join("; ", invalidOffsetLabels)
                    : $"{parsedOffsetLabels.Count} labels; unique={offsetLabelHandlesUnique}" +
                      (offsetLabelProblems.Count == 0
                          ? string.Empty
                          : "; " + string.Join("; ", offsetLabelProblems.Take(3))),
                offsetLabelsExact);

            IReadOnlyList<SectionCorePresentationContract.Expected> expectedCore;
            string? expectedCoreError = null;
            try
            {
                expectedCore = SectionCorePresentationContract.ExpectedFor(
                    planRecord, plannedCoverage);
            }
            catch (Exception ex)
            {
                expectedCore = Array.Empty<SectionCorePresentationContract.Expected>();
                expectedCoreError = ex.Message;
            }
            var appliedCore = applyRecord.CorePresentationAnnotations;
            var coreHandlesUnique = appliedCore.Select(item => item.Handle)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == appliedCore.Count;
            var coreKeysUnique = appliedCore.Select(item => item.SemanticKey)
                .Distinct(StringComparer.Ordinal).Count() == appliedCore.Count;
            var coreSemanticExact = expectedCoreError == null &&
                appliedCore.Count == expectedCore.Count && coreHandlesUnique && coreKeysUnique &&
                expectedCore.All(expected => appliedCore.Count(actual =>
                    string.Equals(actual.Kind, expected.Kind, StringComparison.Ordinal) &&
                    string.Equals(actual.SemanticKey, expected.SemanticKey,
                        StringComparison.Ordinal)) == 1);
            Check("core_presentation_semantics_exact",
                expectedCoreError ?? $"{expectedCore.Count} unique PLAN-derived marks",
                $"{appliedCore.Count} marks; handles-unique={coreHandlesUnique}; keys-unique={coreKeysUnique}",
                coreSemanticExact);

            var coreLiveExact = coreSemanticExact && sv != null &&
                expectedCore.All(expected =>
                {
                    var evidence = appliedCore.Single(actual =>
                        string.Equals(actual.SemanticKey, expected.SemanticKey,
                            StringComparison.Ordinal));
                    var live = annotationRead.Entries.Where(entry =>
                            entry.IsLive && entry.OwnershipValid &&
                            string.Equals(entry.Handle, evidence.Handle,
                                StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    return live.Count == 1 && CorePresentationMatches(
                        sv, planRecord, expected, live[0], measuredLayout?.ExpectedPositionsByHandle);
                });
            Check("core_presentation_geometry_live_exact",
                "axis/title/ROW/top+bottom ticks/widths/strip labels recomputed from PLAN through live SectionView",
                coreLiveExact ? "exact" : "missing/extra/misplaced/unreadable",
                coreLiveExact);
            Check("vehicle_evidence_complete", "true",
                applyRecord.VehicleEvidenceComplete.ToString(), applyRecord.VehicleEvidenceComplete);
            Check("slope_evidence_complete", "true",
                applyRecord.SlopeEvidenceComplete.ToString(), applyRecord.SlopeEvidenceComplete);

            var datumValid = SectionAnnotationContractLogic.TryParseDatumReference(
                applyRecord.DatumReference, out var datumEvidence, out var datumError);
            Check("datum_evidence_valid", "existing-ground-at-axis|elevation=<finite>|handle=<hex>",
                datumValid ? applyRecord.DatumReference : datumError,
                datumValid);

            var liveDatumElevation = placementSurfaces == null
                ? null
                : MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(
                    placementSurfaces.Existing, 0.0);
            var datumSourceExact = datumEvidence != null && liveDatumElevation.HasValue &&
                Math.Abs(datumEvidence.Elevation - liveDatumElevation.Value) <= 0.0005;
            var expectedDatumPosition = sv == null
                ? null
                : SectionAnnotationPlacementContract.DatumPosition(
                    sv, sv.OffsetLeft, sv.ElevationMin);
            if (datumEvidence != null && measuredLayout?.ExpectedPositionsByHandle.TryGetValue(
                    datumEvidence.Handle, out var placedDatum) == true)
                expectedDatumPosition = placedDatum;

            var datumAnnotations = datumEvidence == null
                ? new List<LiveSectionAnnotationEvidence>()
                : annotationRead.Entries.Where(e =>
                    e.IsLive &&
                    string.Equals(e.Handle, datumEvidence.Handle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.EntityType, nameof(DBText), StringComparison.Ordinal) &&
                    string.Equals(e.Layer, SectionDecorationService.AnnoLayer,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Text,
                        SectionDrawingTextLogic.DatumText(datumEvidence.Elevation),
                        StringComparison.Ordinal) &&
                    expectedDatumPosition is { } expectedPosition &&
                    TextPlacementMatches(
                        e, expectedPosition, height: 0.75, rotation: 0.0,
                        centered: false, colorIndex: 8)).ToList();
            var allDatumTexts = annotationRead.Entries.Where(e =>
                e.IsLive && e.Text != null && e.Text.StartsWith("רום ", StringComparison.Ordinal)).ToList();
            Check("single_registered_datum_reference",
                datumEvidence == null
                    ? "one exact registered datum"
                    : datumEvidence.Handle + ":" + SectionDrawingTextLogic.DatumText(datumEvidence.Elevation),
                allDatumTexts.Count == 0
                    ? "(none)"
                    : string.Join(", ", allDatumTexts.Select(e => $"{e.Handle}:{e.Text}")),
                datumValid && datumSourceExact && datumAnnotations.Count == 1 &&
                allDatumTexts.Count == 1);
            Check("datum_matches_live_existing_ground",
                liveDatumElevation?.ToString("F3", CultureInfo.InvariantCulture) ??
                    "readable EG elevation at axis",
                datumEvidence?.Elevation.ToString("F3", CultureInfo.InvariantCulture) ??
                    "(missing)",
                datumSourceExact);

            var parsedSlopes = new List<SectionAnnotationContractLogic.SlopeAnnotationEvidence>();
            var invalidSlopeEvidence = new List<string>();
            foreach (var evidenceText in applyRecord.SlopeLabels)
            {
                if (SectionAnnotationContractLogic.TryParseSlopeReference(
                        evidenceText, out var slope, out var error) && slope != null)
                    parsedSlopes.Add(slope);
                else
                    invalidSlopeEvidence.Add($"{evidenceText}: {error}");
            }
            Check("slope_evidence_valid", "all entries recompute from finite sampled endpoints",
                invalidSlopeEvidence.Count == 0
                    ? $"{parsedSlopes.Count} valid"
                    : string.Join("; ", invalidSlopeEvidence),
                applyRecord.SlopeEvidenceComplete && invalidSlopeEvidence.Count == 0 &&
                parsedSlopes.Count == applyRecord.SlopeLabels.Count &&
                parsedSlopes.Count == plannedCoverage.WidthSpanCount);

            var liveSlopeLabels = annotationRead.Entries.Where(e =>
                e.IsLive && string.Equals(e.EntityType, nameof(DBText), StringComparison.Ordinal) &&
                string.Equals(e.Layer, SectionDecorationService.AnnoLayer,
                    StringComparison.OrdinalIgnoreCase) &&
                e.Text != null && e.Text.EndsWith("%", StringComparison.Ordinal)).ToList();
            var slopeHandlesUnique = parsedSlopes.Select(s => s.Handle)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == parsedSlopes.Count;
            var slopeSpansExact = parsedSlopes.Count == plannedCoverage.ResolvedSpans.Count &&
                plannedCoverage.ResolvedSpans.All(span => parsedSlopes.Count(slope =>
                    Math.Abs(slope.FromOffset - span.FromOffsetM) <= 0.0005 &&
                    Math.Abs(slope.ToOffset - span.ToOffsetM) <= 0.0005) == 1);
            var slopesLiveExact = slopeHandlesUnique &&
                liveSlopeLabels.Count == parsedSlopes.Count &&
                parsedSlopes.All(s =>
                {
                    var matchingSpans = plannedCoverage.ResolvedSpans.Where(span =>
                        Math.Abs(s.FromOffset - span.FromOffsetM) <= 0.0005 &&
                        Math.Abs(s.ToOffset - span.ToOffsetM) <= 0.0005).ToList();
                    if (matchingSpans.Count != 1) return false;
                    if (!SlopeEvidenceMatchesLiveDesign(
                            sv, s, placementSurfaces?.Design,
                            matchingSpans[0].FromOffsetM, matchingSpans[0].ToOffsetM,
                            out var expectedPosition) || expectedPosition == null)
                        return false;
                    if (measuredLayout?.ExpectedPositionsByHandle.TryGetValue(s.Handle,
                            out var measuredSlopePosition) == true)
                        expectedPosition = measuredSlopePosition;
                    return liveSlopeLabels.Count(e =>
                        string.Equals(e.Handle, s.Handle, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.Text, s.Label, StringComparison.Ordinal) &&
                        TextPlacementMatches(
                            e, expectedPosition.Value, height: 0.62,
                            rotation: 0.0, centered: true,
                            colorIndex: 7)) == 1;
                });
            Check("design_slope_labels_live_exact",
                parsedSlopes.Count == 0
                    ? "(none; no adjacent marked spans)"
                    : string.Join(", ", parsedSlopes.Select(s => $"{s.Handle}:{s.Label}")),
                liveSlopeLabels.Count == 0
                    ? "(none)"
                    : string.Join(", ", liveSlopeLabels.Select(e => $"{e.Handle}:{e.Text}")),
                applyRecord.SlopeEvidenceComplete && invalidSlopeEvidence.Count == 0 &&
                slopeSpansExact && slopesLiveExact);

            string? sourceTrackContractError;
            try { sourceTrackContractError = SectionTrafficPlanContract.Validate(planRecord); }
            catch (Exception error) { sourceTrackContractError = error.Message; }
            var expectedVehicleInstances = SectionTrafficPlanContract.VehicleInstances(planRecord);
            var expectedOfficeCarInstances = SectionTrafficPlanContract.OfficeCarInstances(planRecord);
            var parsedVehicles = new List<SectionAnnotationContractLogic.VehicleEvidence>();
            var invalidVehicleEvidence = new List<string>();
            foreach (var evidenceText in applyRecord.VehicleBlocks)
            {
                if (SectionAnnotationContractLogic.TryParseVehicleEvidence(
                        evidenceText, out var vehicle, out var error) && vehicle != null)
                    parsedVehicles.Add(vehicle);
                else
                    invalidVehicleEvidence.Add($"{evidenceText}: {error}");
            }
            Check("vehicle_evidence_valid", "all entries parse",
                invalidVehicleEvidence.Count == 0
                    ? $"{parsedVehicles.Count} valid"
                    : string.Join("; ", invalidVehicleEvidence),
                applyRecord.VehicleEvidenceComplete && invalidVehicleEvidence.Count == 0 &&
                parsedVehicles.Count == applyRecord.VehicleBlocks.Count &&
                sourceTrackContractError == null && parsedVehicles.Count == expectedVehicleInstances);
            Check("vehicle_direction_provenance_complete",
                "every road/bus/bike vehicle carries flow+source+digest",
                $"{parsedVehicles.Count(v => v.HasStrictDirection)}/{parsedVehicles.Count}",
                sourceTrackContractError == null && parsedVehicles.Count == expectedVehicleInstances &&
                parsedVehicles.All(v => v.HasStrictDirection));

            var officeEvidence = parsedVehicles.Where(v => v.IsOfficeBlock).ToList();
            Check("office_vehicle_evidence_matches_expected_strips",
                applyRecord.ExpectedOfficeCarBlocks.ToString(CultureInfo.InvariantCulture),
                officeEvidence.Count.ToString(CultureInfo.InvariantCulture),
                applyRecord.ExpectedOfficeCarBlocks >= 0 &&
                applyRecord.ExpectedOfficeCarBlocks == expectedOfficeCarInstances &&
                officeEvidence.Count == applyRecord.ExpectedOfficeCarBlocks);
            var liveOfficeBlocks = annotationRead.Entries.Where(e =>
                e.IsLive && string.Equals(e.EntityType, nameof(BlockReference), StringComparison.Ordinal) &&
                e.BlockDefinitionName != null &&
                e.BlockDefinitionName.StartsWith("MHD-CD-CAR-", StringComparison.Ordinal)).ToList();
            var officeBlocksLive = OfficeBlockEvidenceMatches(
                tr, sv, placementSurfaces?.Design, planRecord,
                officeEvidence, liveOfficeBlocks, out var officeBlocksReason);
            Check("office_vehicle_blocks_live_exact",
                officeEvidence.Count.ToString(CultureInfo.InvariantCulture),
                officeBlocksLive
                    ? liveOfficeBlocks.Count.ToString(CultureInfo.InvariantCulture)
                    : $"{liveOfficeBlocks.Count}; {officeBlocksReason}",
                officeBlocksLive);

            var rejectedCarFallbacks = parsedVehicles.Count(v =>
                string.Equals(v.Presentation,
                    SectionAnnotationContractLogic.SchematicFallbackPrefix,
                    StringComparison.Ordinal));
            Check("office_car_fallback_absent", "0 (office blocks required for car strips)",
                rejectedCarFallbacks.ToString(CultureInfo.InvariantCulture),
                rejectedCarFallbacks == 0);

            // Schematic bus/bike furniture is valid because no approved office assets
            // were supplied for those families. A schematic car is diagnostic only
            // and the explicit check above keeps the record Failed.
            var fallbackExpected = parsedVehicles.Any(v => v.IsSchematic);
            var fallbackGeometryLive = SchematicVehicleGeometryMatches(
                tr, sv, placementSurfaces?.Design, planRecord,
                parsedVehicles.Where(vehicle => vehicle.IsSchematic).ToList(),
                annotationRead.Entries, out var fallbackReason);
            Check("vehicle_fallback_style_live",
                fallbackExpected
                    ? "exact mapped registered schematic geometry on sampled design ground"
                    : "no schematic vehicle geometry",
                fallbackGeometryLive ? "exact" : "missing/extra/misplaced: " + fallbackReason,
                fallbackGeometryLive);

            // Every directional strip has a separately visible reference to Nataly's
            // pinned HW-ARRW-01 office block. The artifact binds source bytes, live
            // geometry, flow and one registered handle; no schematic arrow passes.
            var parsedDirectionArrows =
                new List<SectionAnnotationContractLogic.TrafficDirectionArrowEvidence>();
            var invalidDirectionArrows = new List<string>();
            foreach (var evidenceText in applyRecord.TrafficDirectionArrows)
            {
                if (SectionAnnotationContractLogic.TryParseTrafficDirectionArrowReference(
                        evidenceText, out var arrow, out var error) && arrow != null)
                    parsedDirectionArrows.Add(arrow);
                else
                    invalidDirectionArrows.Add($"{evidenceText}: {error}");
            }

            var planDirectionRowsValid =
                sourceTrackContractError == null &&
                planRecord.TrafficDirections.Count == expectedVehicleInstances &&
                planRecord.TrafficDirections.All(direction => direction.IsResolved) &&
                planRecord.TrafficDirections.Select(direction =>
                        Math.Round(direction.LaneMidOffsetM, 3,
                            MidpointRounding.AwayFromZero))
                    .Distinct().Count() == planRecord.TrafficDirections.Count;
            Check("planned_traffic_direction_rows_complete",
                expectedVehicleInstances.ToString(CultureInfo.InvariantCulture),
                planRecord.TrafficDirections.Count.ToString(CultureInfo.InvariantCulture) +
                    (sourceTrackContractError == null ? "" : "; source_tracks=" + sourceTrackContractError),
                planDirectionRowsValid);

            var directionArtifactExact =
                invalidDirectionArrows.Count == 0 &&
                parsedDirectionArrows.Count == applyRecord.TrafficDirectionArrows.Count &&
                parsedDirectionArrows.Count == planRecord.TrafficDirections.Count &&
                planRecord.TrafficDirections.All(planned =>
                    parsedDirectionArrows.Count(rendered =>
                        DirectionEvidenceMatches(planned, rendered)) == 1);
            Check("traffic_direction_arrow_evidence_exact",
                planRecord.TrafficDirections.Count == 0
                    ? "(none)"
                    : string.Join(", ", planRecord.TrafficDirections.Select(direction =>
                        $"{direction.LaneMidOffsetM:F3}:{direction.Flow}:{direction.DirectionDigest}")),
                invalidDirectionArrows.Count > 0
                    ? string.Join("; ", invalidDirectionArrows)
                    : string.Join(", ", parsedDirectionArrows.Select(direction =>
                        $"{direction.Offset:F3}:{direction.DirectionFlow}:{direction.DirectionDigest}")),
                planDirectionRowsValid && directionArtifactExact);

            var directionHandles = parsedDirectionArrows.Select(arrow => arrow.Handle).ToList();
            var directionHandlesUnique = directionHandles
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == directionHandles.Count;
            var directionArrowProblems = new List<string>();
            var liveDirectionArrowsExact = directionHandlesUnique &&
                parsedDirectionArrows.All(arrow =>
                {
                    var matchingPlans = planRecord.TrafficDirections
                        .Where(planned => DirectionEvidenceMatches(planned, arrow))
                        .ToList();
                    if (matchingPlans.Count != 1)
                    {
                        directionArrowProblems.Add($"{arrow.Handle}: {matchingPlans.Count} PLAN rows match its evidence");
                        return false;
                    }
                    if (LiveDirectionArrowMatches(
                            tr, sv, placementSurfaces?.Design,
                            matchingPlans[0], arrow, annotationRead.Entries, out var arrowReason))
                        return true;
                    directionArrowProblems.Add($"{arrow.Handle}: {arrowReason}");
                    return false;
                });
            Check("traffic_direction_arrow_handles_live_exact",
                $"{parsedDirectionArrows.Count} distinct registered HW-ARRW-01 block handles with exact asset/geometry/style/orientation",
                $"{directionHandles.Count} handles; unique={directionHandlesUnique}" +
                (directionArrowProblems.Count == 0
                    ? string.Empty
                    : "; " + string.Join("; ", directionArrowProblems)),
                directionArtifactExact && liveDirectionArrowsExact);

            var vehicleDirectionPairsExact = parsedVehicles.Count == parsedDirectionArrows.Count &&
                parsedVehicles.All(vehicle =>
                    parsedDirectionArrows.Count(arrow =>
                        Math.Abs(arrow.Offset - vehicle.Offset) <= 0.0005 &&
                        string.Equals(arrow.DirectionFlow, vehicle.DirectionFlow,
                            StringComparison.Ordinal) &&
                        string.Equals(arrow.DirectionSource, vehicle.DirectionSource,
                            StringComparison.Ordinal) &&
                        string.Equals(arrow.DirectionDigest, vehicle.DirectionDigest,
                            StringComparison.OrdinalIgnoreCase)) == 1);
            Check("vehicle_and_visible_arrow_direction_pair_exact",
                "one matching visible arrow per directional vehicle strip",
                vehicleDirectionPairsExact ? "exact" : "missing/mismatched pair",
                directionArtifactExact && vehicleDirectionPairsExact);

            var expectedProjectionKeys = planRecord.ProjectedEntities
                .Select(p => p.ProjectionKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var artifactProjectionCounts = applyRecord.ProjectedEntities
                .GroupBy(p => p.ProjectionKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.AnnotationHandles.Count), StringComparer.Ordinal);
            var artifactProjectionComparison = SectionProjectionLogic.CompareProjectionEvidence(
                expectedProjectionKeys, artifactProjectionCounts);
            Check("projected_entity_apply_evidence_exact",
                expectedProjectionKeys.Count == 0 ? "(none)" : string.Join(", ", expectedProjectionKeys),
                artifactProjectionCounts.Count == 0 ? "(none)" : string.Join(", ", artifactProjectionCounts.Keys),
                artifactProjectionComparison.IsExact);

            var artifactFingerprintsComplete = applyRecord.ProjectedEntities.All(p =>
                p.AnnotationHandles.Count > 0 &&
                p.AnnotationHandles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(p.AnnotationFingerprints.Keys) &&
                p.AnnotationFingerprints.Values.All(v => !string.IsNullOrWhiteSpace(v)));
            var artifactSemanticRows = applyRecord.ProjectedEntities
                .SelectMany(p => p.AnnotationFingerprints.Select(kv =>
                    $"{p.ProjectionKey}|{kv.Key.ToUpperInvariant()}|{kv.Value}"))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check("projected_entity_apply_semantics_complete", "complete", SemanticDigest(artifactSemanticRows),
                artifactFingerprintsComplete);

            var registryRead = string.IsNullOrEmpty(applyRecord.LogicalKey)
                ? new ProjectionRegistryReadResult()
                : SectionAnnotationRegistry.ReadProjectionSemanticEvidence(tr, db, applyRecord.LogicalKey);
            Check("projected_entity_registry_readable", "readable",
                registryRead.IsValid ? "readable" : registryRead.Error, registryRead.IsValid);
            var liveProjectionCounts = registryRead.Entries.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Count(v => v.LiveFingerprint != null),
                StringComparer.Ordinal);
            var liveProjectionComparison = SectionProjectionLogic.CompareProjectionEvidence(
                expectedProjectionKeys, liveProjectionCounts);
            Check("projected_entity_registry_evidence_exact",
                expectedProjectionKeys.Count == 0 ? "(none)" : string.Join(", ", expectedProjectionKeys),
                liveProjectionCounts.Count == 0
                    ? "(none)"
                    : string.Join(", ", liveProjectionCounts.Select(kv => $"{kv.Key}={kv.Value}")),
                registryRead.IsValid && liveProjectionComparison.IsExact);

            var registeredSemanticRows = registryRead.Entries
                .SelectMany(kv => kv.Value.Select(v =>
                    $"{kv.Key}|{v.Handle.ToUpperInvariant()}|{v.RegisteredFingerprint ?? "MISSING"}"))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var liveSemanticRows = registryRead.Entries
                .SelectMany(kv => kv.Value.Select(v =>
                    $"{kv.Key}|{v.Handle.ToUpperInvariant()}|{v.LiveFingerprint ?? "MISSING"}"))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check("projected_entity_registered_semantics_exact",
                SemanticDigest(artifactSemanticRows), SemanticDigest(registeredSemanticRows),
                artifactFingerprintsComplete && artifactSemanticRows.SequenceEqual(
                    registeredSemanticRows, StringComparer.Ordinal));
            Check("projected_entity_live_semantics_exact",
                SemanticDigest(artifactSemanticRows), SemanticDigest(liveSemanticRows),
                artifactFingerprintsComplete && artifactSemanticRows.SequenceEqual(
                    liveSemanticRows, StringComparer.Ordinal));

            var expectedProjectedSystems = planRecord.ProjectedSystems
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var actualProjectedSystems = applyRecord.ProjectedSystems
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check("projected_systems_exact",
                expectedProjectedSystems.Count == 0 ? "(none)" : string.Join(", ", expectedProjectedSystems),
                actualProjectedSystems.Count == 0 ? "(none)" : string.Join(", ", actualProjectedSystems),
                expectedProjectedSystems.SequenceEqual(actualProjectedSystems, StringComparer.Ordinal));

            // A reused manual view keeps its native location/extents; it never joins
            // Mahod's managed sheet arrangement and therefore has no layout evidence
            // contract. Managed views retain the strict post-arrangement read-back.
            if (!isManualReuse)
            {
                double[]? actualBounds = null;
                double[]? actualLocation = null;
                if (sv != null)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(applyRecord.LogicalKey))
                            throw new InvalidOperationException(
                                "APPLY record has no logical key for its visual envelope.");
                        var envelope = SectionViewOverlapService.ManagedEnvelope(
                            tr, db, sv, applyRecord.LogicalKey);
                        actualBounds = new[]
                        {
                            envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY,
                        };
                        actualLocation = new[] { sv.Location.X, sv.Location.Y };
                    }
                    catch { }
                }
                // The same contract APPLY captured (batch after arrangement, selected
                // where the view stands) — one Core definition for both directions.
                var layoutVerdict = LayoutEvidenceContract.Verify(
                    applyRecord.LayoutBounds, applyRecord.LayoutViewLocation,
                    actualBounds, actualLocation, 0.01);
                Check("layout_bounds_evidence",
                    FormatArray(applyRecord.LayoutBounds), FormatArray(actualBounds),
                    layoutVerdict.BoundsPass);
                Check("layout_view_location_evidence",
                    FormatArray(applyRecord.LayoutViewLocation), FormatArray(actualLocation),
                    layoutVerdict.LocationPass);
            }
        }

        /// <summary>
        /// Selected scope: the artifact holds one record, so the batch check would
        /// compare the view with itself. Prove it against every live SectionView.
        /// </summary>
        private static void AddSelectedNonOverlapChecks(
            Database db, Transaction tr, SectionPlan plan,
            SectionApplyResult applied, SectionVerifyResult result) =>
            AddAllLiveNonOverlapChecks(db, tr, plan, applied, result);

        private static void AddAllLiveNonOverlapChecks(
            Database db, Transaction tr, SectionPlan plan,
            SectionApplyResult applied, SectionVerifyResult result)
        {
            foreach (var recordResult in result.Records)
            {
                var applyRecord = applied.Records.FirstOrDefault(r => r.RecordId == recordResult.RecordId);
                if (applyRecord == null) continue;
                // Same policy as batch VERIFY and as selected APPLY: a reused manual
                // view keeps the engineer's placement and never joins the overlap contract.
                var planRecord = plan.Records.FirstOrDefault(r => r.RecordId == recordResult.RecordId);
                if (planRecord != null && SectionPlanLogic.HasValidExplicitExclusion(planRecord))
                    continue;
                var view = OpenByHandle<CivilDb.SectionView>(db, tr, applyRecord.Handles.SectionView);
                string actual;
                var pass = false;
                if (view == null)
                {
                    actual = "section view missing";
                }
                else
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(applyRecord.LogicalKey))
                            throw new InvalidOperationException(
                                "APPLY record has no logical key for overlap verification.");
                        var report = SectionViewOverlapService.Inspect(
                            tr, db, view, applyRecord.LogicalKey);
                        pass = report.IsClean;
                        actual = report.IsClean
                            ? "none"
                            : $"self_valid={report.SelfValid}; " +
                              $"overlapping={string.Join(",", report.CollidingIds)}; " +
                              $"unmeasurable={string.Join(",", report.UnmeasurableIds)}";
                    }
                    catch (Exception ex)
                    {
                        actual = "unmeasurable: " + ex.Message;
                    }
                }
                recordResult.Checks.Add(new SectionVerifyCheck
                {
                    Check = "layout_non_overlap",
                    Expected = "no overlapping final visual envelope (views + registered annotations)",
                    Actual = actual,
                    Pass = pass,
                });
            }
        }

        private static void AddLayoutNonOverlapChecks(
            Database db, Transaction tr, SectionPlan plan,
            SectionApplyResult applied, SectionVerifyResult result) =>
            AddAllLiveNonOverlapChecks(db, tr, plan, applied, result);

        private sealed record GroupSourceIdentityVerdict(
            bool Pass, string Expected, string Actual);

        /// <summary>
        /// Names are the operator-facing group contract, but VERIFY also binds every
        /// source for which PLAN captured a handle/type and proves exact cardinality.
        /// A unique name with no available PLAN handle (legacy utility-rule evidence)
        /// remains name-bound; duplicate live names are already rejected by the strict
        /// snapshot and can never satisfy this comparison.
        /// </summary>
        private static GroupSourceIdentityVerdict CompareExpectedSourceIdentities(
            SectionPlan plan,
            string alignmentName,
            IReadOnlyList<string> expectedNames,
            SectionSourceService.SampledSourceSnapshot snapshot)
        {
            var names = expectedNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var expectedPlans = plan.Records
                .Where(record => string.Equals(
                    record.SelectedAlignment, alignmentName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(record => record.PlannedSources)
                .Where(source => string.Equals(
                    source.PlannedState, "sampled", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var expectedRows = new List<string>();
            var expectationValid = true;
            foreach (var name in names)
            {
                var planned = expectedPlans.Where(source => string.Equals(
                        source.SourceName, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var handles = planned.Select(source => source.SourceHandle)
                    .Where(handle => !string.IsNullOrWhiteSpace(handle))
                    .Select(handle => handle!.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var kinds = planned.Select(source => NormalizePlannedSourceKind(source.SourceType))
                    .Where(kind => kind != null)
                    .Select(kind => kind!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (handles.Count > 1 || kinds.Count > 1) expectationValid = false;
                expectedRows.Add(
                    $"{name}|{(kinds.Count == 1 ? kinds[0] : "name-only")}|" +
                    $"{(handles.Count == 1 ? handles[0] : "name-only")}");
            }

            var expected = expectedRows.Count == 0
                ? "(none)"
                : string.Join(", ", expectedRows);
            if (!snapshot.IsValid)
                return new GroupSourceIdentityVerdict(
                    false, expected, snapshot.Error ?? "unreadable snapshot");

            var actualRows = snapshot.Entries.Select(entry =>
                    $"{entry.Name}|{entry.Kind}|{entry.Handle.ToUpperInvariant()}")
                .OrderBy(row => row, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var actual = actualRows.Count == 0 ? "(none)" : string.Join(", ", actualRows);
            if (!expectationValid || snapshot.Entries.Count != names.Count)
                return new GroupSourceIdentityVerdict(false, expected, actual);

            var exact = names.All(name =>
            {
                var live = snapshot.Entries.Where(entry => string.Equals(
                        entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (live.Count != 1) return false;

                var planned = expectedPlans.Where(source => string.Equals(
                        source.SourceName, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var handles = planned.Select(source => source.SourceHandle)
                    .Where(handle => !string.IsNullOrWhiteSpace(handle))
                    .Select(handle => handle!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var kinds = planned.Select(source => NormalizePlannedSourceKind(source.SourceType))
                    .Where(kind => kind != null)
                    .Select(kind => kind!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return handles.Count <= 1 && kinds.Count <= 1 &&
                       (handles.Count == 0 || string.Equals(
                           handles[0], live[0].Handle, StringComparison.OrdinalIgnoreCase)) &&
                       (kinds.Count == 0 || string.Equals(
                           kinds[0], live[0].Kind, StringComparison.Ordinal));
            });
            return new GroupSourceIdentityVerdict(exact, expected, actual);
        }

        private static string? NormalizePlannedSourceKind(string? sourceType)
        {
            if (string.IsNullOrWhiteSpace(sourceType)) return null;
            var token = sourceType.Trim().Replace('_', '-').ToLowerInvariant();
            if (token.Contains("pipe-network", StringComparison.Ordinal) ||
                token.Contains("pipenetwork", StringComparison.Ordinal)) return "pipe-network";
            if (token.Contains("corridor", StringComparison.Ordinal)) return "corridor";
            if (token.Contains("surface", StringComparison.Ordinal)) return "surface";
            return token;
        }

        private sealed record LiveSurfacePresentationEvidence(
            string SourceName,
            string StyleName,
            string? StyleDescription,
            short? ColorIndex,
            string? ColorMethod,
            bool Visible,
            string? Linetype,
            double? LinetypeScale);

        private static bool EffectiveLinetypeScaleExact(Database db, double? actual) =>
            actual is { } value && double.IsFinite(value) &&
            double.IsFinite(db.Ltscale) && db.Ltscale > 1e-9 &&
            Math.Abs(value - 1.0 / db.Ltscale) <= 1e-9;

        private static bool TryReadSurfacePresentationEvidence(
            Transaction tr,
            IReadOnlyList<ObjectId> sectionIds,
            out List<LiveSurfacePresentationEvidence> evidence,
            out string error)
        {
            evidence = new List<LiveSurfacePresentationEvidence>();
            error = string.Empty;
            try
            {
                foreach (ObjectId sectionId in sectionIds)
                {
                    if (tr.GetObject(sectionId, OpenMode.ForRead) is not CivilDb.Section section)
                        throw new InvalidOperationException(
                            $"Section child {sectionId.Handle} is not readable as a Civil Section.");
                    if (section.SourceType != CivilDb.SectionSourceType.TinSurface &&
                        section.SourceType != CivilDb.SectionSourceType.GridSurface)
                        continue;

                    var sourceName = section.SourceName;
                    var styleName = section.StyleName;
                    if (string.IsNullOrWhiteSpace(sourceName) ||
                        string.IsNullOrWhiteSpace(styleName) || section.StyleId.IsNull ||
                        tr.GetObject(section.StyleId, OpenMode.ForRead) is not
                            CivilStyles.SectionStyle style)
                        throw new InvalidOperationException(
                            $"Surface Section '{sourceName ?? "(unnamed)"}' has unreadable style evidence.");

                    var display = style.GetDisplayStyleSection(
                        CivilStyles.SectionDisplayStyleSectionType.Segments);
                    var color = display.Color;
                    evidence.Add(new LiveSurfacePresentationEvidence(
                        sourceName,
                        styleName,
                        style.Description,
                        (short)color.ColorIndex,
                        color.ColorMethod.ToString(),
                        display.Visible,
                        display.Linetype,
                        display.LinetypeScale));
                }
                return true;
            }
            catch (Exception ex)
            {
                evidence = new List<LiveSurfacePresentationEvidence>();
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadSectionChildren(
            Transaction tr,
            CivilDb.SampleLine sampleLine,
            out List<ObjectId> ids,
            out List<string> handles,
            out List<SectionSourceService.SampledSourceIdentity> sources,
            out string error)
        {
            ids = new List<ObjectId>();
            handles = new List<string>();
            sources = new List<SectionSourceService.SampledSourceIdentity>();
            error = string.Empty;
            try
            {
                ids.AddRange(sampleLine.GetSectionIds().Cast<ObjectId>());
                foreach (var id in ids)
                {
                    if (id.IsNull || id.IsErased ||
                        tr.GetObject(id, OpenMode.ForRead, openErased: false) is not CivilDb.Section section ||
                        section.IsErased)
                        throw new InvalidOperationException(
                            $"SampleLine returned non-live Section child {id.Handle}.");
                    handles.Add(section.Handle.ToString());
                    sources.Add(SectionSourceService.ResolveLiveSourceIdentityStrict(
                        tr, section.SourceId, section.SourceType, section.SourceName,
                        $"Section child {section.Handle}"));
                }
                if (handles.Count != handles.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                    throw new InvalidOperationException(
                        "SampleLine returned duplicate Section children.");
                if (sources.Select(source =>
                        $"{source.Name}\u001f{source.Kind}\u001f{source.Handle}")
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
                    throw new InvalidOperationException(
                        "SampleLine returned duplicate source identities on its Section children.");
                return true;
            }
            catch (Exception ex)
            {
                ids = new List<ObjectId>();
                handles = new List<string>();
                sources = new List<SectionSourceService.SampledSourceIdentity>();
                error = ex.Message;
                return false;
            }
        }

        private static string FormatSourceIdentities(
            IReadOnlyList<SectionSourceIntegrityLogic.SourceIdentity> identities) =>
            identities.Count == 0
                ? "(none)"
                : string.Join(", ", identities
                    .Select(identity =>
                        $"{identity.Name}|{identity.Kind}|{identity.Handle.ToUpperInvariant()}")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

        private static bool TryReadAnnotationLayerState(
            Transaction tr,
            Database db,
            out SectionAnnotationResourceContracts.LayerState? state,
            out string error)
        {
            state = null;
            error = string.Empty;
            try
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!table.Has(SectionDecorationService.AnnoLayer)) { error = "missing"; return false; }
                var layer = (LayerTableRecord)tr.GetObject(
                    table[SectionDecorationService.AnnoLayer], OpenMode.ForRead);
                state = SectionDecorationService.ReadLayerState(tr, db, layer);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadAnnotationTextStyle(
            Transaction tr,
            Database db,
            out SectionAnnotationResourceContracts.TextStyleState? state,
            out string error)
        {
            state = null;
            error = string.Empty;
            try
            {
                var table = (TextStyleTable)tr.GetObject(
                    db.TextStyleTableId, OpenMode.ForRead);
                if (!table.Has(SectionDecorationService.AnnoTextStyle)) { error = "missing"; return false; }
                var style = (TextStyleTableRecord)tr.GetObject(
                    table[SectionDecorationService.AnnoTextStyle], OpenMode.ForRead);
                state = SectionDecorationService.ReadTextStyleState(style);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadLinetypeDisplayContract(
            Database db, out string contract, out string error)
        {
            contract = string.Empty;
            error = string.Empty;
            try
            {
                contract = SectionDecorationService.LinetypeDisplayContract(db);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadInvalidLinetypes(
            Transaction tr, Database db, out IReadOnlyList<string> invalid, out string error)
        {
            invalid = Array.Empty<string>();
            error = string.Empty;
            try
            {
                invalid = SectionDecorationService.InvalidLinetypes(tr, db);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadSectionViewStyle(
            Transaction tr, CivilDb.SectionView view, out string? styleName)
        {
            styleName = null;
            try
            {
                if (view.StyleId.IsNull) return true;
                if (tr.GetObject(view.StyleId, OpenMode.ForRead) is not CivilDb.Styles.StyleBase style)
                    return false;
                styleName = style.Name;
                return !string.IsNullOrWhiteSpace(styleName);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadElevationRange(
            CivilDb.SectionView view, out bool automatic, out double min, out double max)
        {
            automatic = true;
            min = double.NaN;
            max = double.NaN;
            try
            {
                automatic = view.IsElevationRangeAutomatic;
                min = view.ElevationMin;
                max = view.ElevationMax;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadBandSetEvidence(
            Transaction tr,
            CivilDb.SectionView view,
            out List<string> names,
            out List<string> bottomIds,
            out List<string> topIds)
        {
            var foundNames = new List<string>();
            names = foundNames;
            bottomIds = new List<string>();
            topIds = new List<string>();
            try
            {
                using var bottomBands = view.Bands.GetBottomBandItems();
                using var topBands = view.Bands.GetTopBandItems();
                if (!Collect(bottomBands, bottomIds) || !Collect(topBands, topIds))
                    return false;
                return true;
            }
            catch
            {
                names = new List<string>();
                bottomIds = new List<string>();
                topIds = new List<string>();
                return false;
            }

            bool Collect(System.Collections.IEnumerable items, List<string> ids)
            {
                foreach (var item in items)
                {
                    if (item == null) return false;
                    var property = item.GetType().GetProperty("BandStyleId");
                    if (property?.GetValue(item) is not ObjectId styleId || styleId.IsNull)
                        return false;
                    if (tr.GetObject(styleId, OpenMode.ForRead) is not CivilDb.Styles.StyleBase style ||
                        string.IsNullOrWhiteSpace(style.Name))
                        return false;
                    ids.Add(styleId.Handle.ToString());
                    foundNames.Add(style.Name);
                }
                return true;
            }
        }

        private static bool TryReadConfiguredBandSetEvidence(
            Transaction tr,
            CivilDocument civilDoc,
            string wanted,
            out List<string> bottomIds,
            out List<string> topIds)
        {
            bottomIds = new List<string>();
            topIds = new List<string>();
            try
            {
                var styles = civilDoc.Styles.SectionViewBandSetStyles;
                if (!styles.Contains(wanted)) return false;
                var id = styles[wanted];
                if (id.IsNull || tr.GetObject(id, OpenMode.ForRead) is not
                        CivilStyles.SectionViewBandSetStyle style ||
                    !string.Equals(style.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    return false;

                using var bottomBands = style.GetBottomBandSetItems();
                using var topBands = style.GetTopBandSetItems();
                return Collect(bottomBands, bottomIds) && Collect(topBands, topIds);
            }
            catch
            {
                bottomIds = new List<string>();
                topIds = new List<string>();
                return false;
            }

            static bool Collect(System.Collections.IEnumerable items, List<string> ids)
            {
                foreach (var item in items)
                {
                    if (item == null) return false;
                    var property = item.GetType().GetProperty("BandStyleId");
                    if (property?.GetValue(item) is not ObjectId styleId || styleId.IsNull)
                        return false;
                    ids.Add(styleId.Handle.ToString());
                }
                return true;
            }
        }

        /// <summary>
        /// Recomputes every office block from the exact PLAN lane midpoint (evidence
        /// offsets are millimetre-rounded) and names the first failing property.
        /// </summary>
        private static bool OfficeBlockEvidenceMatches(
            Transaction tr,
            CivilDb.SectionView? sectionView,
            IReadOnlyList<(double Offset, double Elevation)>? design,
            SectionPlanRecord planRecord,
            IReadOnlyList<SectionAnnotationContractLogic.VehicleEvidence> expected,
            IReadOnlyList<LiveSectionAnnotationEvidence> live,
            out string reason)
        {
            reason = string.Empty;
            if (sectionView == null || design == null)
            {
                reason = "no live SectionView or sampled design ground";
                return false;
            }
            if (expected.Count != live.Count)
            {
                reason = $"office block count evidence={expected.Count} live={live.Count}";
                return false;
            }
            if (expected.Any(item => !item.IsOfficeBlock || !item.HasExactOfficeHandle))
            {
                reason = "office evidence without an exact block handle";
                return false;
            }
            if (expected.Select(item => item.Handle)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Count)
            {
                reason = "duplicate office block handles in evidence";
                return false;
            }

            foreach (var item in expected)
            {
                var matches = live.Where(candidate =>
                        string.Equals(candidate.Handle, item.Handle,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1)
                {
                    reason = $"{item.Handle}: {matches.Count} live registered office blocks carry the handle";
                    return false;
                }
                var candidate = matches[0];
                var officeView = string.Equals(
                    item.ViewOrFamily, "front", StringComparison.Ordinal)
                    ? SectionFurnitureLogic.OfficeCarView.Front
                    : SectionFurnitureLogic.OfficeCarView.Rear;
                if (!candidate.IsLive || candidate.BlockDefinitionId.IsNull)
                {
                    reason = $"{item.Handle}: block reference or its definition is not live";
                    return false;
                }
                if (!string.Equals(candidate.Layer, SectionDecorationService.AnnoLayer,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"{item.Handle}: layer {candidate.Layer}";
                    return false;
                }
                if (!SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(
                        officeView,
                        item.Detail,
                        candidate.BlockDefinitionName,
                        candidate.BlockDefinitionComments,
                        candidate.BlockDefinitionGeometrySha256,
                        candidate.BlockDefinitionMatchesPinnedSource,
                        out var assetError))
                {
                    reason = $"{item.Handle}: {assetError}";
                    return false;
                }

                var exact = SectionAnnotationPlacementLogic.ExactPlannedOffset(
                    planRecord.TrafficDirections.Select(direction => direction.LaneMidOffsetM),
                    item.Offset);
                if (exact is not { } offset)
                {
                    reason = FormattableString.Invariant(
                        $"{item.Handle}: no unique PLAN lane offset within 0.0005 of evidence offset {item.Offset:F3}");
                    return false;
                }
                var ground = MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(
                    design, offset);
                if (!ground.HasValue)
                {
                    reason = FormattableString.Invariant($"{item.Handle}: no sampled design ground at offset {offset:R}");
                    return false;
                }
                if (!SectionVehicleBlockService.TryComputePlacement(
                        tr, sectionView, candidate.BlockDefinitionId,
                        SectionFurnitureLogic.Car, offset, ground.Value,
                        out var placement, out var placementError) || placement == null)
                {
                    reason = $"{item.Handle}: {placementError}";
                    return false;
                }
                if (!BlockPlacementMatches(candidate, placement.Position,
                        placement.ScaleFactors, placement.Rotation, out var mismatch))
                {
                    reason = $"{item.Handle}: {mismatch}";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Rebuilds the schematic bus/bike ink at the exact PLAN lane midpoint and
        /// compares it geometrically (1e-5 drawing units) with the registered live
        /// polylines and circles, one to one.
        /// </summary>
        private static bool SchematicVehicleGeometryMatches(
            Transaction tr,
            CivilDb.SectionView? sectionView,
            IReadOnlyList<(double Offset, double Elevation)>? design,
            SectionPlanRecord planRecord,
            IReadOnlyList<SectionAnnotationContractLogic.VehicleEvidence> expected,
            IReadOnlyList<LiveSectionAnnotationEvidence> live,
            out string reason)
        {
            reason = string.Empty;
            if (sectionView == null || design == null)
            {
                reason = "no live SectionView or sampled design ground";
                return false;
            }
            var expectedShapes = new List<(string Kind, double[] Values)>();
            try
            {
                foreach (var item in expected)
                {
                    var spec = item.ViewOrFamily switch
                    {
                        "bus" => SectionFurnitureLogic.Bus,
                        "bike" => SectionFurnitureLogic.Bike,
                        _ => null,
                    };
                    if (spec == null)
                    {
                        reason = $"schematic family '{item.ViewOrFamily}' has no approved geometry";
                        return false;
                    }
                    var exact = SectionAnnotationPlacementLogic.ExactPlannedOffset(
                        planRecord.TrafficDirections.Select(direction => direction.LaneMidOffsetM),
                        item.Offset);
                    if (exact is not { } offset)
                    {
                        reason = FormattableString.Invariant(
                            $"no unique PLAN lane offset within 0.0005 of evidence offset {item.Offset:F3}");
                        return false;
                    }
                    var ground = MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(
                        design, offset);
                    if (!ground.HasValue)
                    {
                        reason = FormattableString.Invariant($"no sampled design ground at offset {offset:R}");
                        return false;
                    }
                    var entities = SectionDecorationService.BuildSchematicVehicle(
                        sectionView, spec, offset, ground.Value);
                    try
                    {
                        expectedShapes.AddRange(entities.Select(ShapeValues));
                    }
                    finally
                    {
                        foreach (var entity in entities)
                        {
                            try { entity.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "expected schematic geometry could not be rebuilt: " + ex.Message;
                return false;
            }

            var actualShapes = new List<(string Kind, double[] Values)>();
            foreach (var entry in live)
            {
                if (!entry.IsLive || entry.ColorIndex != 8 ||
                    !string.Equals(entry.Layer, SectionDecorationService.AnnoLayer,
                        StringComparison.OrdinalIgnoreCase) ||
                    !(string.Equals(entry.EntityType, nameof(Polyline), StringComparison.Ordinal) ||
                      string.Equals(entry.EntityType, nameof(Circle), StringComparison.Ordinal)))
                    continue;
                try
                {
                    if (tr.GetObject(entry.EntityId, OpenMode.ForRead) is not Entity entity)
                    {
                        reason = $"registered schematic ink {entry.Handle} is unreadable";
                        return false;
                    }
                    actualShapes.Add(ShapeValues(entity));
                }
                catch (Exception ex)
                {
                    reason = $"registered schematic ink {entry.Handle}: {ex.Message}";
                    return false;
                }
            }
            if (actualShapes.Count != expectedShapes.Count)
            {
                reason = $"schematic ink count live={actualShapes.Count} expected={expectedShapes.Count}";
                return false;
            }
            var unmatched = actualShapes.ToList();
            foreach (var shape in expectedShapes)
            {
                var index = unmatched.FindIndex(candidate =>
                    string.Equals(candidate.Kind, shape.Kind, StringComparison.Ordinal) &&
                    candidate.Values.Length == shape.Values.Length &&
                    candidate.Values.Zip(shape.Values, (a, b) => Near(a, b, 0.00001)).All(near => near));
                if (index < 0)
                {
                    reason = FormattableString.Invariant(
                        $"{shape.Kind} at {shape.Values[0]:F5},{shape.Values[1]:F5} has no live counterpart within 0.00001");
                    return false;
                }
                unmatched.RemoveAt(index);
            }
            return true;
        }

        private static (string Kind, double[] Values) ShapeValues(Entity entity) => entity switch
        {
            Polyline polyline => (
                "polyline" + (polyline.Closed ? "-closed-" : "-open-") +
                polyline.NumberOfVertices.ToString(CultureInfo.InvariantCulture),
                Enumerable.Range(0, polyline.NumberOfVertices)
                    .SelectMany(i =>
                    {
                        var point = polyline.GetPoint2dAt(i);
                        return new[] { point.X, point.Y, polyline.GetBulgeAt(i) };
                    })
                    .ToArray()),
            Circle circle => ("circle", new[] { circle.Center.X, circle.Center.Y, circle.Radius }),
            _ => throw new InvalidOperationException(
                $"Schematic vehicle ink of type {entity.GetType().Name} is not measurable."),
        };

        private static bool SlopeEvidenceMatchesLiveDesign(
            CivilDb.SectionView? sectionView,
            SectionAnnotationContractLogic.SlopeAnnotationEvidence evidence,
            IReadOnlyList<(double Offset, double Elevation)>? design,
            double plannedFromOffset,
            double plannedToOffset,
            out Point3d? expectedPosition)
        {
            expectedPosition = null;
            if (sectionView == null ||
                !SectionAnnotationContractLogic.TryVerifySlopeEvidence(design,
                    plannedFromOffset, plannedToOffset, evidence, out var live) || live == null)
                return false;
            expectedPosition = SectionAnnotationPlacementContract.SlopePosition(
                sectionView, live.FromOffset, live.ToOffset,
                live.FromElevation, live.ToElevation);
            return expectedPosition != null;
        }

        private static bool DirectionEvidenceMatches(
            SectionTrafficDirectionPlan planned,
            SectionAnnotationContractLogic.TrafficDirectionArrowEvidence rendered)
        {
            var expectedStyle = planned.StripKind switch
            {
                "bus" => SectionTrafficDirectionAnnotationLogic.BusRedStyle,
                "bike" => SectionTrafficDirectionAnnotationLogic.BikeBlackStyle,
                "road" => SectionTrafficDirectionAnnotationLogic.RoadBlackStyle,
                _ => null,
            };
            return planned.IsResolved && expectedStyle != null &&
                   Math.Abs(planned.LaneMidOffsetM - rendered.Offset) <= 0.0005 &&
                   string.Equals(planned.Flow, rendered.DirectionFlow,
                       StringComparison.Ordinal) &&
                   string.Equals(planned.DirectionSource, rendered.DirectionSource,
                       StringComparison.Ordinal) &&
                   string.Equals(planned.DirectionDigest, rendered.DirectionDigest,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(expectedStyle, rendered.Style, StringComparison.Ordinal) &&
                   string.Equals(
                       rendered.AssetSha256,
                       SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                       StringComparison.OrdinalIgnoreCase) &&
                   SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(
                       rendered.GeometrySha256);
        }

        /// <summary>
        /// Recomputes the arrow from the exact PLAN lane midpoint (the evidence offset
        /// is millimetre-rounded and only identifies the PLAN row) and names the
        /// first failing property.
        /// </summary>
        private static bool LiveDirectionArrowMatches(
            Transaction tr,
            CivilDb.SectionView? sectionView,
            IReadOnlyList<(double Offset, double Elevation)>? design,
            SectionTrafficDirectionPlan planned,
            SectionAnnotationContractLogic.TrafficDirectionArrowEvidence arrow,
            IReadOnlyList<LiveSectionAnnotationEvidence> live,
            out string reason)
        {
            reason = string.Empty;
            if (sectionView == null || design == null)
            {
                reason = "no live SectionView or sampled design ground";
                return false;
            }
            if (!SectionVehicleDirectionPlanner.TryParseFlowToken(
                    arrow.DirectionFlow, out var flow))
            {
                reason = $"flow '{arrow.DirectionFlow}' is unresolved";
                return false;
            }
            var expectedColor = string.Equals(
                arrow.Style, SectionTrafficDirectionAnnotationLogic.BusRedStyle,
                StringComparison.Ordinal) ? (short)1 : (short)7;
            var matches = live.Where(candidate =>
                    string.Equals(candidate.Handle, arrow.Handle,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                reason = $"{matches.Count} live registered entities carry the handle";
                return false;
            }
            var entity = matches[0];
            if (!entity.IsLive ||
                !string.Equals(
                    entity.EntityType, nameof(BlockReference), StringComparison.Ordinal))
            {
                reason = $"{entity.EntityType ?? "(missing)"} is not a live block reference";
                return false;
            }
            if (!string.Equals(entity.Layer, SectionDecorationService.AnnoLayer,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = $"layer {entity.Layer}";
                return false;
            }
            if (entity.ColorIndex != expectedColor)
            {
                reason = $"color {entity.ColorIndex} expected {expectedColor}";
                return false;
            }
            if (entity.BlockDefinitionUsesByBlockColor != true)
            {
                reason = "definition does not use ByBlock display";
                return false;
            }
            if (!SectionTrafficArrowAssetEvidenceLogic.TryValidateLiveEvidence(
                    arrow.AssetSha256,
                    arrow.GeometrySha256,
                    entity.BlockDefinitionName,
                    entity.BlockDefinitionComments,
                    entity.BlockDefinitionGeometrySha256,
                    entity.BlockDefinitionUsesByBlockColor == true,
                    entity.BlockDefinitionMatchesPinnedSource,
                    out var assetError))
            {
                reason = assetError;
                return false;
            }
            if (entity.BlockPosition is not { Length: 3 } position ||
                entity.BlockScaleFactors is not { Length: 3 } scale ||
                entity.BlockRotation is not { } ||
                position.Any(value => !Finite(value)) ||
                scale.Any(value => !Finite(value)))
            {
                reason = "block transform is unreadable";
                return false;
            }
            if (scale[0] <= 1e-9 || scale[1] <= 1e-9 || scale[2] <= 1e-9 ||
                Math.Abs(scale[0] - scale[1]) > Math.Max(1e-9, scale[0] * 1e-6) ||
                Math.Abs(scale[0] - scale[2]) > Math.Max(1e-9, scale[0] * 1e-6))
            {
                reason = FormattableString.Invariant(
                    $"scale {scale[0]:R},{scale[1]:R},{scale[2]:R} is not uniform");
                return false;
            }
            if (entity.BlockDefinitionId.IsNull)
            {
                reason = "no block definition";
                return false;
            }

            var stripKind = planned.StripKind switch
            {
                "bus" => SectionTrafficDirectionAnnotationLogic.StripKind.Bus,
                "bike" => SectionTrafficDirectionAnnotationLogic.StripKind.Bike,
                "road" => SectionTrafficDirectionAnnotationLogic.StripKind.Road,
                _ => (SectionTrafficDirectionAnnotationLogic.StripKind?)null,
            };
            if (!stripKind.HasValue)
            {
                reason = $"strip kind '{planned.StripKind}' is unsupported";
                return false;
            }
            var ground = MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(
                design, planned.LaneMidOffsetM);
            if (!ground.HasValue)
            {
                reason = FormattableString.Invariant(
                    $"no sampled design ground at PLAN offset {planned.LaneMidOffsetM:R}");
                return false;
            }
            if (!SectionTrafficDirectionArrowService.TryComputePlacement(
                    tr, sectionView, entity.BlockDefinitionId,
                    planned.LaneMidOffsetM, ground.Value, stripKind.Value, flow,
                    out var placement, out var placementError) || placement == null)
            {
                reason = placementError;
                return false;
            }
            if (!string.Equals(placement.Layout.StyleToken, arrow.Style,
                    StringComparison.Ordinal))
            {
                reason = $"style {placement.Layout.StyleToken} differs from evidence {arrow.Style}";
                return false;
            }
            if (placement.Layout.ColorIndex != expectedColor)
            {
                reason = $"layout color {placement.Layout.ColorIndex} expected {expectedColor}";
                return false;
            }
            if (!BlockPlacementMatches(
                    entity, placement.Position, placement.ScaleFactors,
                    placement.Rotation, out var mismatch))
            {
                reason = mismatch;
                return false;
            }
            return true;
        }

        private static bool CorePresentationMatches(
            CivilDb.SectionView view,
            SectionPlanRecord record,
            SectionCorePresentationContract.Expected expected,
            LiveSectionAnnotationEvidence entity,
            IReadOnlyDictionary<string, Point3d>? measuredPositions = null)
        {
            Point3d Placed(Point3d canonical) => measuredPositions != null &&
                measuredPositions.TryGetValue(entity.Handle, out var measured) ? measured : canonical;
            var offMin = view.OffsetLeft;
            var offMax = view.OffsetRight;
            var elevMin = view.ElevationMin;
            var elevMax = view.ElevationMax;
            switch (expected.Kind)
            {
                case SectionCorePresentationContract.AxisLine:
                    return SectionAnnotationPlacementContract.AxisLine(view, elevMin, elevMax)
                               is { } axis && LinePlacementMatches(entity, axis, 8);
                case SectionCorePresentationContract.AxisLabel:
                    return expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.AxisLabelPosition(view, elevMax)
                               is { } axisLabel &&
                           TextPlacementMatches(entity, Placed(axisLabel), 0.95, 0, true, 7);
                case SectionCorePresentationContract.Title:
                    return expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.TitlePosition(
                               view, offMin, offMax, elevMax) is { } title &&
                           TextPlacementMatches(entity, Placed(title), 1.0, 0, true, 7);
                case SectionCorePresentationContract.RowLine:
                    return expected.Offset is { } rowOffset &&
                           SectionAnnotationPlacementContract.RowLine(
                               view, rowOffset, elevMin, elevMax) is { } row &&
                           LinePlacementMatches(entity, row, 1,
                               (int)LineWeight.LineWeight050);
                case SectionCorePresentationContract.RowLabel:
                    return expected.Offset is { } rowLabelOffset && expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.RowLabelPosition(
                               view, rowLabelOffset, elevMax) is { } rowLabel &&
                           TextPlacementMatches(entity, Placed(rowLabel), 1.0,
                               Math.PI / 2.0, false, 1);
                case SectionCorePresentationContract.DimensionTopTick:
                    return expected.Offset is { } topOffset &&
                           expected.ColorIndex is { } topColor &&
                           SectionAnnotationPlacementContract.TopTick(
                               view, topOffset, elevMax) is { } topTick &&
                           LinePlacementMatches(entity, topTick, topColor);
                case SectionCorePresentationContract.DimensionBottomTick:
                    return expected.Offset is { } bottomOffset &&
                           expected.ColorIndex is { } bottomColor &&
                           SectionAnnotationPlacementContract.BottomTick(
                               view, bottomOffset, elevMin) is { } bottomTick &&
                           LinePlacementMatches(entity, bottomTick, bottomColor);
                case SectionCorePresentationContract.WidthLabel:
                    return expected.From is { } widthFrom && expected.To is { } widthTo &&
                           expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.WidthLabelPosition(
                               view, widthFrom, widthTo, elevMax) is { } width &&
                           TextPlacementMatches(entity, Placed(width), 0.75, 0, true, 7);
                case SectionCorePresentationContract.StripLabel:
                    return expected.From is { } stripFrom && expected.To is { } stripTo &&
                           expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.StripLabelPosition(
                               view, stripFrom, stripTo, elevMax) is { } strip &&
                           TextPlacementMatches(entity, Placed(strip), 0.8, 0, true, 7);
                case SectionCorePresentationContract.GapWidthLabel:
                    return expected.From is { } gapFrom && expected.To is { } gapTo &&
                           expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.WidthLabelPosition(
                               view, gapFrom, gapTo, elevMax) is { } gap &&
                           TextPlacementMatches(entity, Placed(gap),
                               SectionCorePresentationContract.GapWidthTextHeight, 0, true, 7);
                case SectionCorePresentationContract.DimensionChainLine:
                    return expected.From is { } chainFrom && expected.To is { } chainTo &&
                           SectionAnnotationPlacementContract.DimensionChainLine(
                               view, chainFrom, chainTo, elevMax) is { } chainLine &&
                           LinePlacementMatches(entity, chainLine, 8);
                case SectionCorePresentationContract.OverallWidthLabel:
                    return expected.From is { } overallFrom && expected.To is { } overallTo &&
                           expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.OverallWidthLabelPosition(
                               view, overallFrom, overallTo, elevMax) is { } overall &&
                           TextPlacementMatches(entity, Placed(overall),
                               SectionCorePresentationContract.OverallWidthTextHeight, 0, true, 7);
                case SectionCorePresentationContract.LegendSurfaces:
                case SectionCorePresentationContract.LegendUtilities:
                    return expected.Text != null &&
                           string.Equals(entity.Text, expected.Text, StringComparison.Ordinal) &&
                           SectionAnnotationPlacementContract.LegendPosition(
                               view, offMin, offMax, elevMax) is { } legend &&
                           TextPlacementMatches(entity, Placed(legend),
                               SectionCorePresentationContract.LegendTextHeight, 0, true, 7);
                default:
                    return false;
            }
        }

        private static bool LinePlacementMatches(
            LiveSectionAnnotationEvidence entity,
            SectionAnnotationPlacementContract.LinePlacement expected,
            short? colorIndex,
            int? lineWeight = null) =>
            entity.IsLive && entity.OwnershipValid &&
            string.Equals(entity.EntityType, nameof(Line), StringComparison.Ordinal) &&
            string.Equals(entity.Layer, SectionDecorationService.AnnoLayer,
                StringComparison.OrdinalIgnoreCase) &&
            entity.LineEndpoints is { Length: 6 } line && line.All(Finite) &&
            Near(line[0], expected.Start.X, 0.00001) &&
            Near(line[1], expected.Start.Y, 0.00001) &&
            Near(line[2], expected.Start.Z, 0.00001) &&
            Near(line[3], expected.End.X, 0.00001) &&
            Near(line[4], expected.End.Y, 0.00001) &&
            Near(line[5], expected.End.Z, 0.00001) &&
            (!colorIndex.HasValue ||
             SectionAnnotationPlacementLogic.PresentationColorMatches(
                 colorIndex.Value, entity.ColorIndex)) &&
            (!lineWeight.HasValue || entity.LineWeight == lineWeight.Value);

        private static bool TextPlacementMatches(
            LiveSectionAnnotationEvidence entity,
            Point3d expectedAnchor,
            double height,
            double rotation,
            bool centered,
            short colorIndex) =>
            TextPlacementMismatch(entity, expectedAnchor, height, rotation, centered, colorIndex) == null;

        /// <summary>Null when the live text matches; otherwise the first failing property.</summary>
        private static string? TextPlacementMismatch(
            LiveSectionAnnotationEvidence entity,
            Point3d expectedAnchor,
            double height,
            double rotation,
            bool centered,
            short colorIndex)
        {
            var anchor = centered ? entity.TextAlignmentPoint : entity.TextPosition;
            if (!entity.IsLive) return "not live";
            if (!entity.OwnershipValid) return "ownership invalid";
            if (!string.Equals(entity.EntityType, nameof(DBText), StringComparison.Ordinal))
                return $"type {entity.EntityType}";
            if (!string.Equals(entity.Layer, SectionDecorationService.AnnoLayer,
                    StringComparison.OrdinalIgnoreCase))
                return $"layer {entity.Layer}";
            if (!string.Equals(entity.TextStyleName, SectionDecorationService.AnnoTextStyle,
                    StringComparison.OrdinalIgnoreCase))
                return $"text style {entity.TextStyleName}";
            if (anchor is not { Length: 3 } || !anchor.All(Finite))
                return "anchor unreadable";
            if (!Near(anchor[0], expectedAnchor.X, 0.00001) ||
                !Near(anchor[1], expectedAnchor.Y, 0.00001) ||
                !Near(anchor[2], expectedAnchor.Z, 0.00001))
                return FormattableString.Invariant(
                    $"anchor {anchor[0]:F5},{anchor[1]:F5} expected {expectedAnchor.X:F5},{expectedAnchor.Y:F5}");
            if (entity.TextHeight is not { } actualHeight) return "height unreadable";
            if (entity.TextRotation is not { } actualRotation) return "rotation unreadable";
            if (entity.TextWidthFactor is not { } widthFactor) return "width factor unreadable";
            if (entity.TextOblique is not { } oblique) return "oblique unreadable";
            if (entity.TextHorizontalMode != (int)(centered
                    ? TextHorizontalMode.TextCenter
                    : TextHorizontalMode.TextLeft))
                return $"horizontal mode {entity.TextHorizontalMode}";
            if (entity.TextVerticalMode != (int)TextVerticalMode.TextBase)
                return $"vertical mode {entity.TextVerticalMode}";
            if (entity.TextMirroredInX != false || entity.TextMirroredInY != false)
                return "mirrored";
            if (entity.ColorIndex != colorIndex)
                return $"color {entity.ColorIndex} expected {colorIndex}";
            if (!Near(actualHeight, height, 0.0000001))
                return FormattableString.Invariant($"height {actualHeight:R} expected {height:R}");
            // AutoCAD persists rotation normalized to [0, 2π): -π/2 reads back as 3π/2.
            if (!SectionAnnotationPlacementLogic.RotationsEquivalent(actualRotation, rotation, 0.0000001))
                return FormattableString.Invariant($"rotation {actualRotation:R} expected {rotation:R}");
            if (!Near(widthFactor, 1.0, 0.0000001))
                return FormattableString.Invariant($"width factor {widthFactor:R}");
            if (!Near(oblique, 0.0, 0.0000001))
                return FormattableString.Invariant($"oblique {oblique:R}");
            return null;
        }

        private static bool BlockPlacementMatches(
            LiveSectionAnnotationEvidence entity,
            Point3d expectedPosition,
            Scale3d expectedScale,
            double expectedRotation) =>
            BlockPlacementMatches(entity, expectedPosition, expectedScale, expectedRotation, out _);

        private static bool BlockPlacementMatches(
            LiveSectionAnnotationEvidence entity,
            Point3d expectedPosition,
            Scale3d expectedScale,
            double expectedRotation,
            out string mismatch)
        {
            mismatch = string.Empty;
            if (entity.BlockPosition is not { Length: 3 } position || !position.All(Finite))
            {
                mismatch = "position unreadable";
                return false;
            }
            if (entity.BlockScaleFactors is not { Length: 3 } scale || !scale.All(Finite))
            {
                mismatch = "scale unreadable";
                return false;
            }
            if (entity.BlockRotation is not { } rotation || !Finite(rotation))
            {
                mismatch = "rotation unreadable";
                return false;
            }
            if (!Near(position[0], expectedPosition.X, 0.00001) ||
                !Near(position[1], expectedPosition.Y, 0.00001) ||
                !Near(position[2], expectedPosition.Z, 0.00001))
            {
                mismatch = FormattableString.Invariant(
                    $"position {position[0]:F5},{position[1]:F5},{position[2]:F5} expected {expectedPosition.X:F5},{expectedPosition.Y:F5},{expectedPosition.Z:F5}");
                return false;
            }
            if (!Near(scale[0], expectedScale.X, 0.0000001) ||
                !Near(scale[1], expectedScale.Y, 0.0000001) ||
                !Near(scale[2], expectedScale.Z, 0.0000001))
            {
                mismatch = FormattableString.Invariant(
                    $"scale {scale[0]:R},{scale[1]:R},{scale[2]:R} expected {expectedScale.X:R},{expectedScale.Y:R},{expectedScale.Z:R}");
                return false;
            }
            // AutoCAD persists block rotation normalized to [0, 2π).
            if (!SectionAnnotationPlacementLogic.RotationsEquivalent(rotation, expectedRotation, 0.0000001))
            {
                mismatch = FormattableString.Invariant(
                    $"rotation {rotation:R} expected {expectedRotation:R}");
                return false;
            }
            return true;
        }

        private static bool Near(double actual, double expected, double tolerance) =>
            Finite(actual) && Finite(expected) &&
            Math.Abs(actual - expected) <= tolerance;

        private static bool SameHandle(string? expected, string? actual) =>
            !string.IsNullOrWhiteSpace(expected) &&
            !string.IsNullOrWhiteSpace(actual) &&
            string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static string FormatNames(IReadOnlyCollection<string> names) =>
            names.Count == 0 ? "(none)" : string.Join(", ", names);

        private static bool ArraysNear(double[]? expected, double[]? actual, double tolerance) =>
            expected != null && actual != null && expected.Length == actual.Length &&
            expected.Zip(actual, (a, b) => Math.Abs(a - b) <= tolerance).All(x => x);

        private static string FormatArray(double[]? values) => values == null
            ? "(missing)"
            : string.Join(",", values.Select(v => v.ToString("F3")));

        private static string SemanticDigest(IReadOnlyList<string> rows) => rows.Count == 0
            ? "(none)"
            : $"{rows.Count}:{ArtifactHash.Short(ArtifactHash.Sha256OfText(string.Join("\n", rows)))}";

        /// <summary>
        /// True when the sample line's two ENDS are the CL endpoints (either order).
        /// Interior vertices are Civil's own (the alignment-crossing vertex) and carry
        /// no engineering meaning; a line with fewer than two vertices is never right.
        /// </summary>
        private static bool EndpointsMatch(List<(double X, double Y)> vertices, double[] wcs)
        {
            if (wcs.Length != 4 || vertices.Count < 2) return false;
            var a = (X: wcs[0], Y: wcs[1]);
            var b = (X: wcs[2], Y: wcs[3]);
            var first = vertices[0];
            var last = vertices[^1];

            bool Match((double X, double Y) p, (double X, double Y) q) =>
                Math.Abs(p.X - q.X) < GeomTol && Math.Abs(p.Y - q.Y) < GeomTol;

            return (Match(first, a) && Match(last, b)) ||
                   (Match(first, b) && Match(last, a));
        }

        private static string FormatEndpoints(double[] wcs) =>
            wcs.Length == 4 ? $"({wcs[0]:F3},{wcs[1]:F3}) ({wcs[2]:F3},{wcs[3]:F3})" : "?";

        private static T? OpenByHandle<T>(Database db, Transaction tr, string? handle) where T : DBObject
        {
            if (string.IsNullOrEmpty(handle)) return null;
            try
            {
                var h = new Handle(Convert.ToInt64(handle, 16));
                if (!db.TryGetObjectId(h, out var id)) return null;
                return tr.GetObject(id, OpenMode.ForRead) as T;
            }
            catch
            {
                return null;
            }
        }
    }
}
