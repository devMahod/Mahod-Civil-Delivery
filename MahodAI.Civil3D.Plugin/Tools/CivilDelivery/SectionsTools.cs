using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.Tools.CivilDelivery
{
    /// <summary>
    /// MahodAI route for MHD_SECTIONS. Thin wrappers over the SAME deterministic
    /// services the direct command uses (plan §12): the AI may route intent, call
    /// these tools and explain findings — it may not select ambiguous alignments,
    /// invent styles, or bypass REVIEW/BLOCKED. Engineering statuses pass through
    /// verbatim.
    /// </summary>
    public class PlanSectionsTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private SectionPlan? _pendingPlan;
        private ProjectProfile? _pendingProfile;
        private string? _pendingProfileHash;
        private string? _pendingProfileSource;
        private string? _pendingProfileWriteTarget;
        private Autodesk.AutoCAD.ApplicationServices.Document? _pendingDocument;

        public override string Name => "plan_civil_delivery_sections";
        public override string Description =>
            "Runs the read-only Mahod Civil Delivery section PLAN from the project CL configuration: " +
            "resolves CL lines, candidate alignments, station/skew/extents and findings. " +
            "Never modifies the drawing. Returns the plan records with statuses (Ready/ReviewRequired/Blocked).";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""profile_id"": {
                    ""type"": ""string"",
                    ""description"": ""Optional explicit profile id/path; otherwise derived from the active drawing""
                }
            }
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            ClearPending();
            // A failed retry must not leave an older PLAN/APPLY pair available to a
            // later AI APPLY as if it belonged to this attempt.
            CivilDeliverySession.ClearSectionsContext();
            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Civil 3D document required"));

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, "No active drawing"));
            var workflow = new SectionsWorkflowService();
            var loaded = new ActiveProjectProfileService().LoadForDocument(
                doc, workflow, GetStringParam(parameters, "profile_id"));
            if (!loaded.IsUsable || loaded.Profile == null)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Project profile unusable: " +
                    string.Join("; ", loaded.Findings.Select(f => $"{f.Code}: {f.Title}"))));
            }

            var planService = new SectionPlanService();
            var plan = planService.Plan(doc.Database, tr, civilDoc, loaded.Profile, loaded.ProfileHash,
                sourceDrawingIdentity: DrawingScopeIdentity.For(doc));
            _pendingPlan = plan;
            _pendingProfile = loaded.Profile;
            _pendingProfileHash = loaded.ProfileHash;
            _pendingProfileSource = loaded.ProfileSource;
            _pendingProfileWriteTarget = loaded.ProfileWriteTarget;
            _pendingDocument = doc;

            return Task.FromResult(ToolResult.ReadOnly(new Dictionary<string, object?>
            {
                ["run_id"] = plan.RunId,
                ["status"] = plan.Status.ToString(),
                ["record_count"] = plan.Records.Count,
                ["ready"] = plan.Records.Count(r => r.Status == DeliveryStatus.Ready),
                ["review_required"] = plan.Records.Count(r => r.Status == DeliveryStatus.ReviewRequired),
                ["records"] = plan.Records.Select(SectionToolProjections.Project).ToList(),
                ["findings"] = plan.Findings.Select(SectionToolProjections.Project).ToList(),
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult toolResult)
        {
            var plan = _pendingPlan;
            var profile = _pendingProfile;
            var profileWriteTarget = _pendingProfileWriteTarget;
            var document = _pendingDocument;
            try
            {
                if (plan == null || profile == null || document == null ||
                    string.IsNullOrWhiteSpace(profileWriteTarget))
                    throw new InvalidOperationException(
                        "PLAN publication state is unavailable after the read transaction closed.");

                // Lazy Civil reads have now been rolled back and the transaction has
                // disposed. Bind the published PLAN to this actual post-close revision.
                SectionInputIntegrityService.CapturePlanDatabaseState(database, plan, profile);
                SectionsWorkflowService.WriteArtifact(plan.RunId, "section_plan.json", plan);
                SectionsWorkflowService.WritePlanManifest(
                    document, profile, _pendingProfileHash, plan);
                CivilDeliverySession.SetPlan(
                    plan, profile, _pendingProfileHash, profileWriteTarget,
                    _pendingProfileSource);

                if (toolResult.Data is Dictionary<string, object?> payload)
                {
                    payload["source_database_revision"] = plan.SourceDatabaseRevision;
                    payload["evidence_written"] = true;
                }
            }
            finally
            {
                ClearPending();
            }
        }

        private void ClearPending()
        {
            _pendingPlan = null;
            _pendingProfile = null;
            _pendingProfileHash = null;
            _pendingProfileSource = null;
            _pendingProfileWriteTarget = null;
            _pendingDocument = null;
        }
    }

    /// <summary>
    /// Explicit engineer-authorized repair for one unresolved traffic strip. The
    /// decision is persisted to the project profile and PLAN is rerun immediately;
    /// no drawing entity is written by this tool.
    /// </summary>
    public class ApproveSectionTrafficDirectionTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed record PendingDecision(
            SectionPlan ExistingPlan,
            ProjectProfile Profile,
            string? ExistingProfileHash,
            string? ProfileSource,
            string ProfileWriteTarget,
            Autodesk.AutoCAD.ApplicationServices.Document Document,
            string RecordId,
            string SourceDrawingHash,
            string SourceHandle,
            string AlignmentName,
            double LaneMidOffsetM,
            TrafficDirectionEvidenceLogic.RelativeFlow Flow,
            string ApprovedBy,
            DateTime ApprovedAtUtc,
            ToolCache Cache);

        private sealed record ProvenanceSnapshot(
            int Version,
            string? ApprovedBy,
            DateTime? ApprovedAtUtc,
            string? Source,
            Dictionary<string, string> SourceHashes);

        private PendingDecision? _pendingDecision;

        public override string Name => "approve_section_traffic_direction";
        public override string Description =>
            "Persists an explicit per-lane traffic direction for one unresolved section strip, " +
            "with approver and UTC time, then reruns PLAN. Requires exact record_id, lane midpoint, " +
            "and flow=along-alignment|against-alignment. Does not modify the drawing.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""record_id"": { ""type"": ""string"" },
                ""lane_mid_offset_m"": { ""type"": ""number"" },
                ""flow"": { ""type"": ""string"", ""enum"": [""along-alignment"", ""against-alignment""] },
                ""approved_by"": { ""type"": ""string"" }
            },
            ""required"": [""record_id"", ""lane_mid_offset_m"", ""flow"", ""approved_by""]
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters,
            ToolCache cache, CancellationToken ct)
        {
            ClearPending();
            var context = CivilDeliverySession.GetSectionsContext();
            if (context.Plan == null || context.Profile == null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "No section PLAN/profile in session — run plan_civil_delivery_sections first"));

            var recordId = GetStringParam(parameters, "record_id");
            var flowToken = GetStringParam(parameters, "flow");
            var approvedBy = GetStringParam(parameters, "approved_by");
            if (string.IsNullOrWhiteSpace(recordId) ||
                string.IsNullOrWhiteSpace(approvedBy) ||
                !SectionVehicleDirectionPlanner.TryParseFlowToken(flowToken, out var flow) ||
                !parameters.TryGetProperty("lane_mid_offset_m", out var offsetElement) ||
                !offsetElement.TryGetDouble(out var laneOffset) ||
                double.IsNaN(laneOffset) || double.IsInfinity(laneOffset))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    "Exact record_id, finite lane_mid_offset_m, supported flow and approved_by are required"));

            var matches = context.Plan.Records.Where(record =>
                    string.Equals(record.RecordId, recordId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].SelectedAlignment))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    "record_id does not identify one alignment-resolved PLAN row"));
            var record = matches[0];
            var lanes = record.TrafficDirections.Where(direction =>
                    Math.Abs(direction.LaneMidOffsetM - laneOffset) <=
                        SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM)
                .ToList();
            if (lanes.Count != 1 || lanes[0].IsResolved)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    "lane_mid_offset_m must identify exactly one currently unresolved traffic strip"));

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, "No active drawing"));
            if (string.IsNullOrWhiteSpace(context.ProfileWriteTarget))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "Section profile write target is unavailable — re-run PLAN"));

            // No profile/file/session mutation is allowed while the executor-owned
            // read transaction is still open. The callback rechecks freshness, saves,
            // replans in a second abort+dispose scope, then publishes atomically enough
            // to fail closed and restore this decision on any later failure.
            _pendingDecision = new PendingDecision(
                context.Plan,
                context.Profile,
                context.ProfileHash,
                context.ProfileSource,
                context.ProfileWriteTarget,
                doc,
                record.RecordId,
                record.Cl.SourceDrawingHash,
                record.Cl.SourceHandle,
                record.SelectedAlignment!,
                lanes[0].LaneMidOffsetM,
                flow,
                approvedBy!,
                DateTime.UtcNow,
                cache);
            return Task.FromResult(ToolResult.ReadOnly(new Dictionary<string, object?>
            {
                ["record_id"] = record.RecordId,
                ["lane_mid_offset_m"] = lanes[0].LaneMidOffsetM,
                ["publication_pending"] = true,
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult toolResult)
        {
            var pending = _pendingDecision;
            ProjectProfileWriter.SaveResult? saved = null;
            ProjectProfile? profileForSave = null;
            List<ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision>?
                originalDirections = null;
            ProvenanceSnapshot? originalProvenance = null;
            try
            {
                if (pending == null)
                    throw new InvalidOperationException(
                        "Traffic-direction decision state is unavailable after the read transaction closed.");
                if (!ReferenceEquals(database, pending.Document.Database))
                    throw new InvalidOperationException(
                        "Traffic-direction decision drawing changed before publication.");

                var currentProfile = SectionToolProfileFreshness.RequireCurrent(
                    pending.Document, pending.ExistingPlan,
                    pending.ProfileSource, pending.ProfileWriteTarget,
                    "TRAFFIC-DIRECTION-DECISION");
                profileForSave = currentProfile.Profile!;
                var staleReason = SectionInputIntegrityService.StaleReason(
                        database,
                        pending.ExistingPlan,
                        pending.ExistingPlan.SourceDatabaseRevision,
                        "TRAFFIC-DIRECTION-DECISION", profileForSave);
                if (staleReason != null)
                    throw new InvalidOperationException(
                        "Traffic-direction decision refused after transaction close: " + staleReason);

                var expectedProfileState = currentProfile.ProfileWriteState ??
                    throw new InvalidOperationException(
                        "Traffic-direction profile baseline was not captured atomically with the authoritative reload.");
                originalDirections = profileForSave.Sections.Decisions.TrafficDirections
                    .Select(CloneTrafficDirection).ToList();
                originalProvenance = CaptureProvenance(profileForSave);

                SectionDecisionProfileService.ApproveTrafficDirection(
                    profileForSave,
                    pending.SourceDrawingHash,
                    pending.SourceHandle,
                    pending.AlignmentName,
                    pending.LaneMidOffsetM,
                    pending.Flow,
                    pending.ApprovedBy,
                    pending.ApprovedAtUtc);
                var summary = FormattableString.Invariant(
                    $"traffic direction: record={pending.RecordId}; lane={pending.LaneMidOffsetM:F3}; flow={SectionVehicleDirectionPlanner.FlowToken(pending.Flow)}");
                saved = ProjectProfileWriter.Save(
                    profileForSave,
                    currentProfile.ProfileWriteTarget,
                    summary,
                    pending.ApprovedBy,
                    expectedState: expectedProfileState);

                SectionPlan replanned;
                // The decision's new profile hash must be reflected in a fresh Civil
                // PLAN. This second read transaction is also abort+dispose complete
                // before any artifact or session state becomes visible.
                using (var planTr = database.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var civilDoc = CivilDocument.GetCivilDocument(database);
                        replanned = new SectionPlanService().Plan(
                            database,
                            planTr,
                            civilDoc,
                            profileForSave,
                            saved.NewHash,
                            sourceDrawingIdentity: DrawingScopeIdentity.For(pending.Document));
                    }
                    finally
                    {
                        planTr.Abort();
                    }
                }

                SectionInputIntegrityService.CapturePlanDatabaseState(database, replanned, profileForSave);
                SectionsWorkflowService.WriteArtifact(
                    replanned.RunId, "section_plan.json", replanned);
                SectionsWorkflowService.WritePlanManifest(
                    pending.Document, profileForSave, saved.NewHash, replanned);
                CivilDeliverySession.SetPlan(
                    replanned, profileForSave, saved.NewHash, saved.Path, saved.Path);
                pending.Cache.RemoveByPattern("get_drawing_summary:");

                var replannedRecord = replanned.Records.FirstOrDefault(candidate =>
                    string.Equals(candidate.RecordId, pending.RecordId, StringComparison.Ordinal));
                var replannedLane = replannedRecord?.TrafficDirections.FirstOrDefault(direction =>
                    Math.Abs(direction.LaneMidOffsetM - pending.LaneMidOffsetM) <=
                        SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM);

                if (toolResult.Data is Dictionary<string, object?> payload)
                {
                    payload["profile_version"] = saved.NewVersion;
                    payload["profile_hash"] = saved.NewHash;
                    payload["plan_run_id"] = replanned.RunId;
                    payload["plan_status"] = replanned.Status.ToString();
                    payload["direction_resolved"] = replannedLane?.IsResolved == true;
                    payload["direction_source"] = replannedLane?.DirectionSource;
                    payload["direction_digest"] = replannedLane?.DirectionDigest;
                    payload["source_database_revision"] = replanned.SourceDatabaseRevision;
                    payload["evidence_written"] = true;
                    payload["publication_pending"] = false;
                }
            }
            catch (Exception operationError)
            {
                Exception? restoreError = null;
                try
                {
                    if (pending != null && profileForSave != null &&
                        originalDirections != null && originalProvenance != null)
                    {
                        try
                        {
                            if (saved != null)
                            {
                                MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService
                                    .RestoreProfileAfterFailedDecisionEvidence(saved);
                            }
                        }
                        catch (Exception ex)
                        {
                            restoreError = ex;
                        }
                        finally
                        {
                            // Durable rollback can correctly refuse to overwrite newer
                            // external bytes. The in-memory graph and session are still
                            // withdrawn independently and may never retain the decision.
                            try
                            {
                                RestoreProfileObject(
                                    profileForSave, originalDirections, originalProvenance);
                            }
                            catch (Exception ex)
                            {
                                restoreError = restoreError == null
                                    ? ex
                                    : new AggregateException(restoreError, ex);
                            }
                        }
                    }
                }
                finally
                {
                    CivilDeliverySession.ClearSectionsContext();
                }

                if (restoreError != null)
                    throw new AggregateException(
                        "Traffic-direction publication failed and the saved profile could not be safely restored; section delivery remains blocked.",
                        operationError, restoreError);
                throw new InvalidOperationException(
                    "Traffic-direction publication failed; the decision was withdrawn and section delivery state was cleared.",
                    operationError);
            }
            finally
            {
                ClearPending();
            }
        }

        private void ClearPending()
        {
            _pendingDecision = null;
        }

        private static ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision
            CloneTrafficDirection(
                ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision value) =>
            new()
            {
                SourceDrawingHash = value.SourceDrawingHash,
                SourceHandle = value.SourceHandle,
                AlignmentName = value.AlignmentName,
                LaneMidOffsetM = value.LaneMidOffsetM,
                Flow = value.Flow,
                ApprovedBy = value.ApprovedBy,
                ApprovedAtUtc = value.ApprovedAtUtc,
            };

        private static ProvenanceSnapshot CaptureProvenance(ProjectProfile profile) => new(
            profile.Provenance.Version,
            profile.Provenance.ApprovedBy,
            profile.Provenance.ApprovedAtUtc,
            profile.Provenance.Source,
            new Dictionary<string, string>(
                profile.Provenance.SourceHashes, StringComparer.Ordinal));

        private static void RestoreProfileObject(
            ProjectProfile profile,
            IReadOnlyCollection<ProjectProfile.SectionsProfile.DecisionsProfile
                .TrafficDirectionDecision> directions,
            ProvenanceSnapshot provenance)
        {
            profile.Sections.Decisions.TrafficDirections.Clear();
            profile.Sections.Decisions.TrafficDirections.AddRange(
                directions.Select(CloneTrafficDirection));
            profile.Provenance.Version = provenance.Version;
            profile.Provenance.ApprovedBy = provenance.ApprovedBy;
            profile.Provenance.ApprovedAtUtc = provenance.ApprovedAtUtc;
            profile.Provenance.Source = provenance.Source;
            profile.Provenance.SourceHashes.Clear();
            foreach (var pair in provenance.SourceHashes)
                profile.Provenance.SourceHashes[pair.Key] = pair.Value;
        }
    }

    public class PreviewSectionsTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        public override string Name => "preview_civil_delivery_sections";
        public override string Description =>
            "Shows a geometry-only transient cross-section check from the last PLAN by sampling the exact planned " +
            "existing/design surfaces along its CL and plotting projected utilities. This is not the final " +
            "Nataly-facing appearance: persisted strip names, widths, grounded slopes and office car blocks are " +
            "created and verified only by APPLY+VERIFY. Pass record_id to select the row, or omit it for the first " +
            "READY row. Pass clear=true to remove it. Drawing is never modified.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""clear"": { ""type"": ""boolean"", ""description"": ""true to clear the preview"" },
                ""record_id"": { ""type"": ""string"", ""description"": ""exact PLAN record to preview"" }
            }
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        private static readonly SectionPreviewService Preview = new();
        private PreparedSectionPreview? _pendingPreview;
        private bool _pendingClear;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            ClearPendingPreview();
            _pendingClear = false;
            if (GetBoolParam(parameters, "clear") == true)
            {
                // Clearing is a visible side effect too.  Defer it until this
                // caller-owned read transaction has aborted and disposed.
                _pendingClear = true;
                return Task.FromResult(ToolResult.ReadOnly(new Dictionary<string, object?>
                { ["cleared"] = true, ["message"] = "Preview cleared; drawing unchanged." }));
            }

            var context = CivilDeliverySession.GetSectionsContext();
            var plan = context.Plan;
            if (plan == null || context.Profile == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No section plan in session — run plan_civil_delivery_sections first"));

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No active Civil 3D document"));
            ActiveProjectProfileService.ActiveLoadResult currentProfile;
            try
            {
                currentProfile = SectionToolProfileFreshness.RequireCurrent(
                    doc, plan, context.ProfileSource, context.ProfileWriteTarget,
                    "PREVIEW");
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, ex.Message));
            }

            var recordId = GetStringParam(parameters, "record_id");
            _pendingPreview = Preview.PreparePreview(
                doc.Database, tr, currentProfile.Profile!, plan, recordId);
            var preview = _pendingPreview.Display;
            return Task.FromResult(ToolResult.ReadOnly(new Dictionary<string, object?>
            {
                ["record_id"] = preview.RecordId,
                ["drawables"] = preview.DrawableCount,
                ["existing_ground_samples"] = preview.ExistingGroundSamples,
                ["design_samples"] = preview.DesignSamples,
                ["projected_utilities"] = preview.ProjectedUtilityCount,
                ["final_appearance_rendered"] = preview.FinalAppearanceRendered,
                ["scope_notice"] = preview.ScopeNotice,
                ["message"] = "Displaying a geometry-only transient section check (nothing persisted).",
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (_pendingClear)
            {
                _pendingClear = false;
                Preview.ClearPreview();
                return;
            }

            var prepared = _pendingPreview
                ?? throw new InvalidOperationException(
                    "Preview publication failed closed: no prepared preview was retained.");
            _pendingPreview = null;
            try
            {
                Preview.PublishPreview(prepared);
            }
            catch
            {
                // Publish consumes candidates that reached the transient manager;
                // Dispose remains a safe no-op in that case and releases a candidate
                // if publication failed before ownership transfer.
                prepared.Dispose();
                throw;
            }
        }

        private void ClearPendingPreview()
        {
            var pending = _pendingPreview;
            _pendingPreview = null;
            pending?.Dispose();
        }
    }

    public class ApplySectionsTool : DrawingToolBase, ITransactionCommitObserver
    {
        private SectionApplyResult? _pendingCommittedResult;
        private SectionPlan? _pendingPlan;
        private ProjectProfile? _pendingProfile;
        private string? _pendingProfileHash;
        private Autodesk.AutoCAD.ApplicationServices.Document? _pendingDocument;

        public override string Name => "apply_civil_delivery_sections";
        public override string Description =>
            "APPLIES the last section PLAN: creates real native Civil SampleLineGroup/SampleLine/SectionView " +
            "objects only after EVERY CL record is READY/unchanged or explicitly excluded with approval. " +
            "Requires confirm=true. Atomic: any failure rolls the whole batch back.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(300);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""confirm"": {
                    ""type"": ""boolean"",
                    ""description"": ""Must be true; set only after the engineer approved the plan""
                },
                ""record_ids"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Optional approval list; if supplied it must equal the complete managed batch""
                }
            },
            ""required"": [""confirm""]
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Civil 3D document required"));

            if (GetBoolParam(parameters, "confirm") != true)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Apply requires confirm=true after engineer approval of the plan"));

            var context = CivilDeliverySession.GetSectionsContext();
            var plan = context.Plan;
            var profile = context.Profile;
            CivilDeliverySession.ClearSectionsApply();
            if (plan == null || profile == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No section plan in session — run plan_civil_delivery_sections first"));

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No active Civil 3D document"));
            ActiveProjectProfileService.ActiveLoadResult currentProfile;
            try
            {
                currentProfile = SectionToolProfileFreshness.RequireCurrent(
                    doc, plan, context.ProfileSource, context.ProfileWriteTarget,
                    "APPLY");
                profile = currentProfile.Profile!;
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, ex.Message));
            }

            List<string>? approved = null;
            if (parameters.TryGetProperty("record_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                approved = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToList();

            var applyService = new SectionApplyService();
            List<SectionPlanRecord> targets;
            try { targets = SectionApplyService.SelectTargets(plan, approved); }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, ex.Message));
            }
            var result = new SectionApplyResult { RunId = RunManifest.NewRunId("sections", "apply") };

            foreach (var skipped in plan.Records.Where(r => !targets.Contains(r)))
            {
                result.Records.Add(new SectionApplyRecordResult
                {
                    RecordId = skipped.RecordId,
                    LogicalKey = skipped.LogicalKey,
                    ActionTaken = skipped.Action is PlanAction.Unchanged or PlanAction.Excluded
                        ? skipped.Action : PlanAction.ReviewRequired,
                    Status = SectionPlanLogic.HasValidExplicitExclusion(skipped)
                        ? DeliveryStatus.Verified : skipped.Status,
                });
            }

            bool coreOk = applyService.ApplyCore(
                tr, doc.Database, civilDoc, profile, DrawingScopeIdentity.For(doc),
                currentProfile.ProfileHash, plan, targets, result);

            // The executor owns the transaction. Do not claim a commit until its
            // post-commit callback fires.
            result.Committed = false;
            if (result.Status != DeliveryStatus.Blocked)
                result.Status = coreOk
                    ? DeliveryStatusRules.Aggregate(result.Records.Select(r => r.Status).ToList())
                    : DeliveryStatus.Failed;
            if (!coreOk)
            {
                CivilDeliverySession.ClearSectionsApply();
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "APPLY batch failed — transaction must be aborted; findings: " +
                    string.Join("; ", result.Records.SelectMany(r => r.Findings).Select(f => f.Title))));
            }

            cache.RemoveByPattern("get_drawing_summary:");
            _pendingCommittedResult = result;
            _pendingPlan = plan;
            _pendingProfile = profile;
            _pendingProfileHash = currentProfile.ProfileHash;
            _pendingDocument = doc;
            return Task.FromResult(ToolResult.Ok(new Dictionary<string, object?>
            {
                ["run_id"] = result.RunId,
                ["applied"] = result.Records.Count(r => r.Status == DeliveryStatus.Applied),
                ["unchanged"] = result.Records.Count(r => r.ActionTaken == PlanAction.Unchanged),
                ["excluded"] = result.Records.Count(r => r.ActionTaken == PlanAction.Excluded),
                ["skipped_review"] = result.Records.Count(r => r.ActionTaken == PlanAction.ReviewRequired),
                ["handles"] = result.Records
                    .Where(r => r.Status == DeliveryStatus.Applied)
                    .Select(r => new Dictionary<string, string?>
                    {
                        ["record"] = r.RecordId,
                        ["sample_line_group"] = r.Handles.SampleLineGroup,
                        ["sample_line"] = r.Handles.SampleLine,
                        ["section_view"] = r.Handles.SectionView,
                    }).ToList(),
            }));
        }

        public void OnTransactionCommitted(Database database, ToolResult toolResult)
        {
            var result = _pendingCommittedResult;
            var plan = _pendingPlan;
            var profile = _pendingProfile;
            var document = _pendingDocument;
            if (result == null || plan == null || profile == null || document == null)
                return;

            result.Committed = true;
            result.PostApplyDatabaseRevision =
                MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.DrawingRevisionTracker.Capture(database);
            var evidenceWritten = SectionsWorkflowService.PersistApplyEvidence(
                document, profile, _pendingProfileHash, plan, result);
            if (evidenceWritten)
                CivilDeliverySession.SetApply(result);
            else
                CivilDeliverySession.ClearSectionsApply();

            if (toolResult.Data is Dictionary<string, object?> payload)
            {
                payload["post_apply_database_revision"] = result.PostApplyDatabaseRevision;
                payload["evidence_written"] = evidenceWritten;
                payload["status"] = result.Status.ToString();
            }
            if (!evidenceWritten)
            {
                toolResult.Success = false;
                toolResult.Outcome = ToolOutcome.Failed;
                toolResult.Error = new ToolExecutionError
                {
                    Code = ToolErrorCodes.ExecutionFailed,
                    Message = "APPLY committed, but its authoritative evidence bundle could not be published; VERIFY and delivery are blocked.",
                    Details = string.Join("; ", result.Findings
                        .Where(finding => finding.Code == SectionFindingCodes.EvidenceWriteFailed)
                        .Select(finding => finding.Message)),
                };
            }

            _pendingCommittedResult = null;
            _pendingPlan = null;
            _pendingProfile = null;
            _pendingProfileHash = null;
            _pendingDocument = null;
        }
    }

    public class VerifySectionsTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private SectionVerifyResult? _pendingResult;
        private SectionPlan? _pendingPlan;
        private SectionApplyResult? _pendingApply;
        private ProjectProfile? _pendingProfile;
        private string? _pendingProfileHash;
        private Autodesk.AutoCAD.ApplicationServices.Document? _pendingDocument;

        public override string Name => "verify_civil_delivery_sections";
        public override string Description =>
            "Re-reads the ACTUAL Civil database after apply and proves the created objects match the " +
            "approved plan: geometry, station, group membership, view linkage, ownership metadata. " +
            "Returns per-check pass/fail evidence.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        public override JsonElement? ParameterSchema => null;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            ClearPending();
            var context = CivilDeliverySession.GetSectionsContext();
            var plan = context.Plan;
            var apply = context.Apply;
            if (plan == null || apply == null || context.Profile == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Verify requires a completed apply in this session"));

            try
            {
                SectionsWorkflowService.RequirePlanEvidence(plan);
                SectionsWorkflowService.RequireApplyEvidence(apply);
            }
            catch (Exception ex)
            {
                CivilDeliverySession.ClearSectionsApply();
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "VERIFY refused before Civil read-back: PLAN/APPLY evidence is missing, stale, or mismatched.",
                    ex.ToString()));
            }

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No active Civil 3D document"));
            ActiveProjectProfileService.ActiveLoadResult currentProfile;
            try
            {
                currentProfile = SectionToolProfileFreshness.RequireCurrent(
                    doc, plan, context.ProfileSource, context.ProfileWriteTarget,
                    "VERIFY");
            }
            catch (Exception ex)
            {
                CivilDeliverySession.ClearSectionsApply();
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    ex.Message));
            }

            var verifyService = new SectionVerifyService();
            var result = verifyService.Verify(doc.Database, tr, civilDoc, plan, apply, profile: currentProfile.Profile);
            _pendingResult = result;
            _pendingPlan = plan;
            _pendingApply = apply;
            _pendingProfile = currentProfile.Profile;
            _pendingProfileHash = currentProfile.ProfileHash;
            _pendingDocument = doc;

            return Task.FromResult(ToolResult.ReadOnly(new Dictionary<string, object?>
            {
                ["run_id"] = result.RunId,
                ["status"] = result.Status.ToString(),
                ["records"] = result.Records.Select(r => new Dictionary<string, object?>
                {
                    ["record_id"] = r.RecordId,
                    ["status"] = r.Status.ToString(),
                    ["checks_passed"] = r.Checks.Count(c => c.Pass),
                    ["checks_total"] = r.Checks.Count,
                    ["failed_checks"] = r.Checks.Where(c => !c.Pass)
                        .Select(c => $"{c.Check}: expected {c.Expected}, actual {c.Actual}").ToList(),
                    }).ToList(),
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult toolResult)
        {
            var result = _pendingResult;
            var plan = _pendingPlan;
            var apply = _pendingApply;
            var profile = _pendingProfile;
            var document = _pendingDocument;
            try
            {
                if (result == null || plan == null || apply == null ||
                    profile == null || document == null)
                    throw new InvalidOperationException(
                        "VERIFY publication state is unavailable after the read transaction closed.");

                var evidenceWritten = SectionsWorkflowService.PersistVerifyEvidence(
                    document, profile, _pendingProfileHash, plan, apply, result);
                if (toolResult.Data is Dictionary<string, object?> payload)
                {
                    payload["status"] = result.Status.ToString();
                    payload["evidence_written"] = evidenceWritten;
                }
                if (!evidenceWritten)
                {
                    toolResult.Success = false;
                    toolResult.Outcome = ToolOutcome.Failed;
                    toolResult.Error = new ToolExecutionError
                    {
                        Code = ToolErrorCodes.ExecutionFailed,
                        Message = "VERIFY completed in memory, but its authoritative evidence bundle could not be published; no green result exists.",
                        Details = string.Join("; ", result.Findings
                            .Where(finding => finding.Code == SectionFindingCodes.EvidenceWriteFailed)
                            .Select(finding => finding.Message)),
                    };
                }
            }
            finally
            {
                ClearPending();
            }
        }

        private void ClearPending()
        {
            _pendingResult = null;
            _pendingPlan = null;
            _pendingApply = null;
            _pendingProfile = null;
            _pendingProfileHash = null;
            _pendingDocument = null;
        }
    }

    internal static class SectionToolProfileFreshness
    {
        internal static ActiveProjectProfileService.ActiveLoadResult RequireCurrent(
            Autodesk.AutoCAD.ApplicationServices.Document document,
            SectionPlan plan,
            string? profileSource,
            string? profileWriteTarget,
            string stage)
        {
            var current = ActiveProjectProfileService.ReloadForExistingWorkflow(
                document, new SectionsWorkflowService(), plan.ProjectProfileId,
                profileSource, profileWriteTarget);
            if (!current.IsUsable || current.Profile == null ||
                string.IsNullOrWhiteSpace(current.ProfileHash))
                throw new InvalidOperationException(
                    $"{stage} refused: the authoritative project profile is missing, unreadable, or invalid.");
            var staleReason = SectionPlanLogic.ScopeStaleReason(
                plan, DrawingScopeIdentity.For(document),
                current.Profile, current.ProfileHash);
            if (staleReason != null)
                throw new InvalidOperationException(
                    $"{stage} refused: {staleReason}");
            return current;
        }
    }

    internal static class SectionToolProjections
    {
        public static Dictionary<string, object?> Project(SectionPlanRecord r) => new()
        {
            ["record_id"] = r.RecordId,
            ["section_id"] = r.SectionId,
            ["status"] = r.Status.ToString(),
            ["action"] = r.Action.ToString(),
            ["alignment"] = r.SelectedAlignment,
            ["candidates"] = r.CandidateCrossings.Select(c => c.AlignmentName).Distinct().ToList(),
            ["station"] = r.Station,
            ["skew_deg"] = r.SkewDeg,
            ["left_extent"] = r.LeftExtent,
            ["right_extent"] = r.RightExtent,
            ["utility_coverage"] = new Dictionary<string, object?>
            {
                ["represented"] = r.UtilityCoverage.Represented,
                ["present_but_not_configured"] = r.UtilityCoverage.NotConfigured,
                ["configured_but_missing"] = r.UtilityCoverage.Missing,
                ["unsupported"] = r.UtilityCoverage.Unsupported,
                ["complete"] = r.UtilityCoverage.Complete,
                ["summary"] = r.UtilityCoverage.Summary,
                ["projection_scan_state"] = r.UtilityCoverage.ProjectionScanState.ToString(),
                ["projection_drawing_entity_count"] = r.UtilityCoverage.ProjectionDrawingEntityCount,
                ["projection_section_crossing_count"] = r.UtilityCoverage.ProjectionSectionCrossingCount,
            },
            ["traffic_directions"] = r.TrafficDirections.Select(direction =>
                new Dictionary<string, object?>
                {
                    ["strip_label"] = direction.StripLabel,
                    ["strip_kind"] = direction.StripKind,
                    ["lane_mid_offset_m"] = direction.LaneMidOffsetM,
                    ["state"] = direction.State,
                    ["flow"] = direction.Flow,
                    ["direction_source"] = direction.DirectionSource,
                    ["direction_digest"] = direction.DirectionDigest,
                    ["reason"] = direction.Reason,
                }).ToList(),
            ["findings"] = r.Findings.Select(Project).ToList(),
        };

        public static Dictionary<string, object?> Project(DeliveryFinding f) => new()
        {
            ["code"] = f.Code,
            ["severity"] = f.Severity.ToString(),
            ["title"] = f.Title,
        };
    }
}
