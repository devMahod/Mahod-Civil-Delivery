using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.Tools.CivilDelivery
{
    /// <summary>
    /// MahodAI route for MHD_ESTIMATE. The AI may scan, build, export and explain
    /// traces; it may not invent quantities, catalog codes, prices or approve
    /// adjustments (plan §12.2). Unmapped/missing-price/unit findings pass through.
    /// </summary>
    public class ScanEstimateQuantitiesTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed class ScanPublicationPayload : Dictionary<string, object?>
        {
            internal ScanPublicationPayload(
                Database database,
                EstimateWorkflowService.ScanResult scan,
                ProjectProfile profile,
                string profileHash,
                string profileWriteTarget)
            {
                Database = database;
                Scan = scan;
                Profile = profile;
                ProfileHash = profileHash;
                ProfileWriteTarget = profileWriteTarget;
            }

            internal Database Database { get; }
            internal EstimateWorkflowService.ScanResult Scan { get; }
            internal ProjectProfile Profile { get; }
            internal string ProfileHash { get; }
            internal string ProfileWriteTarget { get; }
        }

        public override string Name => "scan_civil_delivery_quantities";
        public override string Description =>
            "Scans the drawing for quantity sources per the project profile rules and produces " +
            "Neutral Quantity Records (measurement + provenance, no pricing). Read-only. " +
            "Reports duplicate/double-count risks and unit findings. This AI route does not " +
            "create quantity exclusions; exclusions require the palette's audited reason/approver decision.";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(180);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""profile_id"": { ""type"": ""string"", ""description"": ""Optional explicit profile id/path; otherwise derived from the active drawing"" }
            }
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            using var diagnostic = EstimateScanTrace.Start("ai-scan");
            EstimateScanTrace.Mark("ai.tool.entry");
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, "No active drawing"));

            var loaded = EstimateScanTrace.Step("profile.load", () => new ActiveProjectProfileService().LoadForDocument(
                doc, new SectionsWorkflowService(), GetStringParam(parameters, "profile_id")));
            if (!loaded.IsUsable || loaded.Profile == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Project profile unusable: " +
                    string.Join("; ", loaded.Findings.Select(f => f.Code))));

            if (!EstimateWorkflowService.TransactionMatches(doc, tr))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "The active drawing changed before the quantity scan — run the scan again"));

            // Use the complete production workflow, including corridor/earthworks
            // quantities and their stale/read/ownership preflight gates.
            // Invalidate any older session before measurement. The replacement is not
            // installed until ToolExecutor proves Abort+Dispose succeeded.
            CivilDeliverySession.ClearEstimateContext();
            var profileHash = loaded.ProfileHash ??
                throw new InvalidOperationException(
                    "The usable project profile has no exact source SHA-256.");
            EstimateScanTrace.Mark("ai.workflow.begin");
            var scan = new EstimateWorkflowService()
                .ScanUnpublished(
                    doc, tr, civilDoc, loaded.Profile, profileHash,
                    loaded.ProfileSource, loaded.ProfileWriteTarget,
                    loaded.ProfileWriteState ?? throw new InvalidOperationException(
                        "The project profile load has no source/target CAS evidence."));
            EstimateScanTrace.Mark("ai.workflow.end", scan.Records.Count);

            var payload = new ScanPublicationPayload(
                doc.Database, scan, loaded.Profile, profileHash,
                loaded.ProfileWriteTarget)
            {
                ["run_id"] = scan.RunId,
                ["record_count"] = scan.Records.Count,
                ["scanned_entities"] = scan.ScannedEntities,
                ["source_scope_policy"] = loaded.Profile.Estimate.QuantitySources.SourceScopePolicy,
                ["xref_policy"] = loaded.Profile.Estimate.QuantitySources.XrefPolicy,
                ["scope_notice"] = EstimatePreflightPolicy.CompleteScopeNotice,
                ["external_source_count"] = scan.ExternalSources.Count,
                ["external_sources"] = scan.ExternalSources,
                ["decision_route_notice"] =
                    "AI scan does not approve quantity exclusions; use the palette for an audited engineering reason, approver and UTC decision.",
                ["by_rule"] = scan.Records
                    .GroupBy(r => r.Classification.RuleKey ?? "(none)")
                    .ToDictionary(g => g.Key, g => new
                    {
                        count = g.Count(),
                        total = EstimateToolJson.TraceNumber(
                            Math.Round(g.Sum(r => r.Measurement.RawValue), 3)),
                        unit = g.First().Measurement.Unit,
                        candidate_code = g.First().Classification.CandidateCatalogCode,
                    }),
                ["findings"] = scan.Findings.Select(f => new { f.Code, severity = f.Severity.ToString(), f.Title }).ToList(),
            };
            return Task.FromResult(ToolResult.ReadOnly(payload));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (result.Data is not ScanPublicationPayload pending)
                throw new InvalidOperationException(
                    "Quantity scan completed without its post-transaction publication payload.");
            if (!ReferenceEquals(database, pending.Database))
                throw new InvalidOperationException(
                    "Quantity scan transaction closed for a different drawing database.");

            try
            {
                EstimateWorkflowService.PublishScanEvidence(pending.Scan);
                CivilDeliverySession.SetScan(
                    pending.Scan, pending.Profile, pending.ProfileHash,
                    pending.ProfileWriteTarget);
            }
            catch
            {
                CivilDeliverySession.ClearEstimateContext();
                throw;
            }
        }
    }

    public class ProposeEstimateMappingsTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed class ProposalPublicationPayload : Dictionary<string, object?>
        {
            internal ProposalPublicationPayload(
                Database database,
                EstimateWorkflowService.ScanResult scan,
                EstimateWorkflowService.MappingProposalPublication publication,
                ProjectProfile profile,
                CatalogSnapshot catalogSnapshot)
            {
                Database = database;
                Scan = scan;
                Publication = publication;
                Profile = profile;
                CatalogSnapshot = catalogSnapshot;
            }

            internal Database Database { get; }
            internal EstimateWorkflowService.ScanResult Scan { get; }
            internal EstimateWorkflowService.MappingProposalPublication Publication { get; }
            internal ProjectProfile Profile { get; }
            internal CatalogSnapshot CatalogSnapshot { get; }
        }

        public override string Name => "propose_civil_delivery_mappings";
        public override string Description =>
            "Returns RANKED CATALOG SUGGESTIONS for the unmapped quantity groups in the last scan, " +
            "using the pinned price book plus the codes seen in the supplied reference estimate. " +
            "Suggestions never cross units and are always PROPOSED_UNAPPROVED — presenting them is " +
            "allowed, approving them is not: the engineer must confirm via save_civil_delivery_mappings.";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        public override JsonElement? ParameterSchema => null;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            if (!EstimateToolSessionScope.TryResolve(out var context, out var scopeError))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    scopeError));
            var scan = context!.Scan;
            var profile = context.Profile;
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !EstimateWorkflowService.TransactionMatches(doc, tr))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "The active drawing changed before estimate build — run the quantity scan again"));

            var workflow = new EstimateWorkflowService();
            var catalog = workflow.LoadCatalog(profile, scan.ProfileSource);
            if (catalog.Snapshot == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Catalog/price snapshot unavailable or unverified: " +
                    string.Join("; ", catalog.Findings.Select(f => f.Title))));

            // L05: the same project-rule context as the palette; read-only file I/O inside the read transaction,
            // re-proven by the observer before anything is published.
            var rulesContext = workflow.PrepareProjectRuleContext(scan, catalog.Snapshot);
            var publication = workflow.ProposeMappingsUnpublished(
                scan, catalog.Snapshot, profile, rulesContext);
            var proposals = publication.Proposals;

            return Task.FromResult(ToolResult.ReadOnly(new ProposalPublicationPayload(
                doc.Database, scan, publication, profile, catalog.Snapshot)
            {
                ["run_id"] = scan.RunId,
                ["proposal_count"] = proposals.Count,
                ["groups_without_proposal"] = scan.Records
                    .Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))
                    .Select(r => r.Classification.RuleKey)
                    .Distinct()
                    .Where(k => !proposals.Any(p => p.RuleKey == k))
                    .ToList(),
                ["proposals"] = proposals.Select(p => new Dictionary<string, object?>
                {
                    ["rule_key"] = p.RuleKey,
                    ["layer"] = p.Layer,
                    ["measured_unit"] = p.MeasuredUnit,
                    ["object_count"] = p.ObjectCount,
                    ["total_quantity"] = EstimateToolJson.TraceNumber(p.TotalQuantity),
                    ["proposed_code"] = p.ProposedCode,
                    ["catalog_description"] = p.CatalogDescription,
                    ["catalog_unit"] = p.CatalogUnit,
                    ["score"] = p.Score,
                    ["evidence_kind"] = p.EvidenceKind,
                    ["reasons"] = p.Reasons,
                    ["status"] = p.Status,
                }).ToList(),
                ["project_rules"] = new Dictionary<string, object?>
                {
                    ["state"] = rulesContext.State.ToString(),
                    ["reason"] = rulesContext.Reason,
                },
                ["project_rule_reviews"] = (publication.ProjectRuleReviews ?? Array.Empty<EstimateWorkflowService.ProjectRuleReview>())
                    .Select(r => new Dictionary<string, object?>
                    {
                        ["rule_key"] = r.RuleKey,
                        ["layer"] = r.Layer,
                        ["governed_by"] = EstimateWorkflowService.IsLibraryReview(r) ? "library" : "project_rules",
                        ["governed_by_project_rules"] = r.Governed && !EstimateWorkflowService.IsLibraryReview(r),
                        ["state"] = r.State,
                        ["label"] = r.Label,
                        ["message"] = r.Message,
                    }).ToList(),
                ["message"] = "These are suggestions only. Present them to the engineer and ask which to approve; " +
                              "never save a mapping the engineer did not choose. Groups in project_rule_reviews with " +
                              "governed_by_project_rules=true are computed by the project's quantity rules: do not propose " +
                              "or save a catalog code for their raw measured sum — point the engineer to the rules-based BoQ. " +
                              "Groups with governed_by=library are elements the project's BoQ library defines as an engineering " +
                              "decision with no item (for example trees awaiting a specification): do not propose a catalog code; " +
                              "tell the engineer the item or specification is an engineering decision, and that a manual mapping " +
                              "with explicit approval remains possible.",
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (result.Data is not ProposalPublicationPayload pending)
                throw new InvalidOperationException(
                    "Mapping proposals completed without their post-transaction publication payload.");
            if (!ReferenceEquals(database, pending.Database))
                throw new InvalidOperationException(
                    "Mapping proposals closed for a different drawing database.");

            var doc = EstimateToolSessionScope.RequireActiveDocument(
                database, "mapping proposal publication");
            EstimateWorkflowService.RequireFreshForDecision(
                doc, pending.Scan, "פרסום הצעות מיפוי באמצעות כלי AI");
            var currentCatalog = new EstimateWorkflowService().LoadCatalog(pending.Profile, pending.Scan.ProfileSource);
            if (currentCatalog.Snapshot == null ||
                !string.Equals(currentCatalog.Snapshot.SnapshotId,
                    pending.CatalogSnapshot.SnapshotId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentCatalog.Snapshot.FileHash,
                    pending.CatalogSnapshot.FileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The active price-book bytes changed before mapping proposals could be published.");
            EstimateWorkflowService.PublishMappingProposalEvidence(
                pending.Scan, pending.Publication, pending.CatalogSnapshot);
            if (pending.Publication.GroupCount > 0)
                CivilDeliverySession.ClearEstimateResult();
        }
    }

    public class SaveEstimateMappingsTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed class MappingSavePayload : Dictionary<string, object?>
        {
            internal MappingSavePayload(
                Database database,
                EstimateWorkflowService.ScanResult scan,
                ProjectProfile profile,
                string profileWriteTarget,
                IReadOnlyList<EstimateWorkflowService.MappingApproval> approvals,
                string approvedBy)
            {
                Database = database;
                Scan = scan;
                Profile = profile;
                ProfileWriteTarget = profileWriteTarget;
                Approvals = approvals;
                ApprovedBy = approvedBy;
            }

            internal Database Database { get; }
            internal EstimateWorkflowService.ScanResult Scan { get; }
            internal ProjectProfile Profile { get; }
            internal string ProfileWriteTarget { get; }
            internal IReadOnlyList<EstimateWorkflowService.MappingApproval> Approvals { get; }
            internal string ApprovedBy { get; }
        }

        public override string Name => "save_civil_delivery_mappings";
        public override string Description =>
            "Saves ENGINEER-APPROVED catalog mappings into the project profile with provenance. " +
            "Each mapping is re-validated: the catalog code must exist in the pinned snapshot and its " +
            "unit must equal the measured unit. Requires an approver name.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""mappings"": {
                    ""type"": ""array"",
                    ""description"": ""Engineer-chosen mappings"",
                    ""items"": {
                        ""type"": ""object"",
                        ""properties"": {
                            ""rule_key"": { ""type"": ""string"" },
                            ""catalog_code"": { ""type"": ""string"" }
                        },
                        ""required"": [""rule_key"", ""catalog_code""]
                    }
                },
                ""approved_by"": { ""type"": ""string"", ""description"": ""Name of the approving engineer"" }
            },
            ""required"": [""mappings"", ""approved_by""]
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            if (!EstimateToolSessionScope.TryResolve(out var context, out var scopeError))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    scopeError));
            var scan = context!.Scan;
            var profile = context.Profile;
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !EstimateWorkflowService.TransactionMatches(doc, tr))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "The active drawing changed before mapping approval — run the quantity scan again"));

            var approvedBy = GetStringParam(parameters, "approved_by");
            if (string.IsNullOrWhiteSpace(approvedBy))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "approved_by is required — a mapping is an engineering decision"));

            if (!parameters.TryGetProperty("mappings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, "mappings array is required"));

            var approvals = new List<EstimateWorkflowService.MappingApproval>();
            foreach (var element in arr.EnumerateArray())
            {
                var ruleKey = element.TryGetProperty("rule_key", out var rk) ? rk.GetString() : null;
                var code = element.TryGetProperty("catalog_code", out var cc) ? cc.GetString() : null;
                if (string.IsNullOrWhiteSpace(ruleKey) || string.IsNullOrWhiteSpace(code))
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        "each mapping needs rule_key and catalog_code"));

                // The rule key must come from the real scan — the AI cannot invent a group.
                var sample = scan.Records.FirstOrDefault(r => r.Classification.RuleKey == ruleKey);
                if (sample == null)
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"rule_key '{ruleKey}' does not exist in the last scan"));

                approvals.Add(new EstimateWorkflowService.MappingApproval(
                    RuleKey: ruleKey!,
                    CatalogCode: code!.Trim().ToUpperInvariant(),
                    LayerPattern: sample.Source.Layer ?? "*",
                    EntityType: sample.Source.EntityType,
                    MeasurementKind: sample.Measurement.Kind,
                    MeasuredUnit: sample.Measurement.Unit));
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ToolResult.ReadOnly(new MappingSavePayload(
                doc.Database, scan, profile, context.ProfileWriteTarget,
                approvals.ToArray(), approvedBy!.Trim())
            {
                ["mapping_count"] = approvals.Count,
                ["status"] = "pending_post_transaction_close",
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (result.Data is not MappingSavePayload pending)
                throw new InvalidOperationException(
                    "Mapping approval completed without its post-transaction save payload.");
            if (!ReferenceEquals(database, pending.Database))
                throw new InvalidOperationException(
                    "Mapping approval closed for a different drawing database.");

            try
            {
                var doc = EstimateToolSessionScope.RequireActiveDocument(
                    database, "mapping approval");
                EstimateWorkflowService.RequireFreshForDecision(
                    doc, pending.Scan, "אישור מיפוי באמצעות כלי AI");

                // Load and parse the immutable price-book byte snapshot again after
                // transaction close. A changed file cannot inherit a pre-close green.
                var workflow = new EstimateWorkflowService();
                var catalog = workflow.LoadCatalog(pending.Profile, pending.Scan.ProfileSource);
                if (catalog.Snapshot == null)
                    throw new InvalidOperationException(
                        "Catalog snapshot unavailable or unverified: " +
                        string.Join("; ", catalog.Findings.Select(f => f.Title)));

                ManualMappingCaseScope.RequireLegacyScopeIsExact(pending.Scan.Records,
                    pending.Approvals.Select(approval => approval.RuleKey));
                var saved = workflow.SaveApprovedMappings(
                    pending.Profile, catalog.Snapshot, pending.Approvals,
                    pending.ApprovedBy, pending.ProfileWriteTarget,
                    expectedProfileState: pending.Scan.ProfileWriteState ??
                        throw new InvalidOperationException(
                            "The quantity scan has no captured profile compare-and-swap state."));
                var rebased = EstimateWorkflowService.PublishProfileDecisionOrRestore(
                    pending.Scan, pending.Profile, saved,
                    pending.Approvals.Select(approval => approval.RuleKey));
                CivilDeliverySession.SetScan(
                    rebased, pending.Profile, saved.NewHash,
                    pending.ProfileWriteTarget);

                pending["saved_path"] = saved.Path;
                pending["profile_version"] = saved.NewVersion;
                pending["status"] = "saved_and_published";
                pending["message"] =
                    "Mappings saved with published rebased scan evidence. Build may continue from this session.";
            }
            catch
            {
                CivilDeliverySession.ClearEstimateContext();
                throw;
            }
        }
    }

    public class BuildEstimateTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed class BuildPublicationPayload : Dictionary<string, object?>
        {
            internal BuildPublicationPayload(
                Database database,
                EstimateWorkflowService.ScanResult scan,
                ProjectProfile profile)
            {
                Database = database;
                Scan = scan;
                Profile = profile;
            }

            internal Database Database { get; }
            internal EstimateWorkflowService.ScanResult Scan { get; }
            internal ProjectProfile Profile { get; }
        }

        public override string Name => "build_civil_delivery_estimate";
        public override string Description =>
            "Builds the deterministic early-design estimate from the last quantity scan: catalog identity, " +
            "price snapshot (missing price is MISSING_PRICE, never zero), ordered approved adjustments only, " +
            "unit gate, and exports the RTL Excel workbook with audit.json. Previously approved exclusions " +
            "are disclosed with source evidence; this AI route cannot create them. Requires confirm=true.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(180);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""confirm"": { ""type"": ""boolean"", ""description"": ""Must be true to export the workbook"" }
            },
            ""required"": [""confirm""]
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            if (GetBoolParam(parameters, "confirm") != true)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Building/exporting the estimate requires confirm=true"));

            if (!EstimateToolSessionScope.TryResolve(out var context, out var scopeError))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    scopeError));
            var scan = context!.Scan;
            var profile = context.Profile;

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !EstimateWorkflowService.TransactionMatches(doc, tr))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "The active drawing changed before estimate build — run the quantity scan again"));

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ToolResult.ReadOnly(new BuildPublicationPayload(
                doc.Database, scan, profile)
            {
                ["status"] = "pending_post_transaction_close",
            }));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (result.Data is not BuildPublicationPayload pending)
                throw new InvalidOperationException(
                    "Estimate build completed without its post-transaction publication payload.");
            if (!ReferenceEquals(database, pending.Database))
                throw new InvalidOperationException(
                    "Estimate build closed for a different drawing database.");

            try
            {
                var doc = EstimateToolSessionScope.RequireActiveDocument(
                    database, "estimate build/export");
                var workflow = new EstimateWorkflowService();
                var catalog = workflow.LoadCatalog(pending.Profile, pending.Scan.ProfileSource);
                if (catalog.Snapshot == null)
                    throw new InvalidOperationException(
                        "Catalog/price snapshot unavailable or unverified: " +
                        string.Join("; ", catalog.Findings.Select(f => f.Title)));

                CivilDeliverySession.ClearEstimateResult();
                var estimate = workflow.Build(
                    doc, pending.Scan, catalog.Snapshot, pending.Profile);

                pending["run_id"] = estimate.RunId;
                pending["estimate_status"] = estimate.Status.ToString();
                pending["line_count"] = estimate.Lines.Count;
                pending["clean_total"] = estimate.CleanTotal;
                pending["excluded_lines"] = estimate.ExcludedLineCount;
                pending["audited_exclusion_count"] = estimate.Exclusions.Count;
                pending["audited_exclusions"] = estimate.Exclusions.Select(x => new
                {
                    rule_key = x.RuleKey,
                    reason = x.Reason,
                    approved_by = x.ApprovedBy,
                    approved_at_utc = x.ApprovedAtUtc,
                    sources = x.Sources.Select(s => new
                    {
                        record_id = s.RecordId,
                        drawing = s.Drawing,
                        drawing_hash = s.DrawingHash,
                        handle = s.Handle,
                        layer = s.Layer,
                        measurement_kind = s.MeasurementKind,
                        unit = s.Unit,
                        raw_value = EstimateToolJson.TraceNumber(s.RawValue),
                    }).ToList(),
                }).ToList();
                pending["source_scope_policy"] = estimate.SourceScopePolicy;
                pending["xref_policy"] = estimate.XrefPolicy;
                pending["scope_notice"] = estimate.ScopeNotice;
                pending["attention"] = estimate.Lines
                    .Where(l => !l.IncludedInTotals)
                    .Select(l => new
                    {
                        l.RecordId,
                        code = l.CatalogCode,
                        reasons = l.Findings.Select(f => f.Code).Distinct().ToList(),
                    }).ToList();

                var blockers = EstimatePreflightPolicy.ExportBlockingReasons(estimate)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToList();
                if (blockers.Count == 0)
                {
                    var written = workflow.Export(
                        doc, pending.Scan, estimate, profile: pending.Profile);
                    pending["status"] = "Exported";
                    pending["export_disposition"] = "exported";
                    pending["export_blockers"] = Array.Empty<string>();
                    pending["xlsx_path"] = written.XlsxPath;
                    pending["audit_path"] = written.AuditPath;
                    pending["manifest_path"] = written.ManifestPath;
                    pending["xlsx_hash"] = written.XlsxHash;
                    pending["audit_hash"] = written.AuditHash;
                    pending["manifest_hash"] = written.ManifestHash;
                }
                else
                {
                    // A fail-closed preflight is a correct build outcome on a real
                    // discovery drawing, not an exception and never an Excel claim.
                    pending["status"] = "Blocked";
                    pending["export_disposition"] = "correctly_blocked";
                    pending["export_blockers"] = blockers;
                    pending["message"] =
                        "Estimate evidence was built, but export is correctly blocked until every listed finding is resolved.";
                }

                // The session advances only after BUILD evidence and either a complete
                // export package or a proven, explicit export-blocked disposition.
                CivilDeliverySession.SetEstimate(estimate);
            }
            catch (Exception operationError)
            {
                CivilDeliverySession.ClearEstimateResult();
                // BUILD may have published immediately before EXPORT failed. Restore
                // the run head to the already-published scan so a failed tool call
                // cannot leave derived green evidence behind.
                try
                {
                    EstimateWorkflowService.WithdrawDerivedEstimateEvidence(pending.Scan);
                }
                catch (Exception withdrawalError)
                {
                    throw new AggregateException(
                        "Estimate build/export failed and its derived evidence could not be safely withdrawn; estimate state remains blocked.",
                        operationError, withdrawalError);
                }
                throw;
            }
        }
    }

    public class GetEstimateTraceTool : DrawingToolBase
    {
        public override string Name => "get_civil_delivery_estimate_trace";
        public override string Description =>
            "Explains where an estimate quantity came from: full trace for one line " +
            "(source object handle → measurement → raw quantity → adjustments → BOQ → catalog code → " +
            "price source → total). Answers 'תראה לי מאיפה הגיעה הכמות'.";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""line_id"": { ""type"": ""string"", ""description"": ""Estimate line id (e.g. L0003)"" },
                ""catalog_code"": { ""type"": ""string"", ""description"": ""Alternative lookup by catalog code"" }
            }
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            if (!EstimateToolSessionScope.TryResolve(out var context, out var scopeError))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    scopeError));
            var estimate = context!.Estimate;
            var scan = context.Scan;
            if (estimate == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No estimate in session — run build_civil_delivery_estimate first"));

            try
            {
                EstimateWorkflowService.RequirePublishedEstimateBuildEvidence(
                    scan, estimate);
            }
            catch (Exception ex)
            {
                CivilDeliverySession.ClearEstimateResult();
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "Estimate trace evidence is missing or no longer matches the session: " + ex.Message));
            }

            var lineId = GetStringParam(parameters, "line_id");
            var code = GetStringParam(parameters, "catalog_code");

            var lines = estimate.Lines
                .Where(l => (lineId == null || l.LineId == lineId) &&
                            (code == null || string.Equals(l.CatalogCode, code, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (lines.Count == 0)
                return Task.FromResult(ToolResult.NotFound("EstimateLine", lineId ?? code ?? "?"));

            var traces = lines.Select(line =>
            {
                var record = scan.Records.FirstOrDefault(r => r.RecordId == line.RecordId);
                return new Dictionary<string, object?>
                {
                    ["line_id"] = line.LineId,
                    ["catalog_code"] = line.CatalogCode,
                    ["source"] = record == null ? null : new
                    {
                        record.Source.Drawing,
                        record.Source.DrawingHash,
                        record.Source.Handle,
                        record.Source.EntityType,
                        record.Source.Layer,
                        record.Source.Xref,
                    },
                    ["measurement"] = record == null ? null : new
                    {
                        record.Measurement.Kind,
                        record.Measurement.Method,
                        RawValue = EstimateToolJson.TraceNumber(record.Measurement.RawValue),
                        record.Measurement.Unit,
                    },
                    ["raw_quantity"] = EstimateToolJson.TraceNumber(line.RawQuantity),
                    ["adjustments"] = line.Adjustments.Select(a => new Dictionary<string, object?>
                    {
                        ["rule_id"] = a.RuleId,
                        ["factor"] = EstimateToolJson.TraceNumber(a.Factor),
                        ["input"] = EstimateToolJson.TraceNumber(a.Input),
                        ["output"] = EstimateToolJson.TraceNumber(a.Output),
                        ["reason"] = a.Reason,
                        ["approved_by"] = a.ApprovedBy,
                        ["order"] = a.Order,
                    }).ToList(),
                    ["boq_quantity"] = line.BoqQuantity,
                    ["price"] = line.Price,
                    ["price_status"] = line.PriceStatus.ToString(),
                    ["price_book_id"] = line.PriceBookId,
                    ["total"] = line.Total,
                    ["included_in_totals"] = line.IncludedInTotals,
                    ["findings"] = line.Findings.Select(f => new { f.Code, f.Title }).ToList(),
                };
            }).ToList();

            return Task.FromResult(ToolResult.ReadOnly(new Dictionary<string, object?>
            {
                ["traces"] = traces,
            }));
        }
    }

    /// <summary>
    /// JSON-safe representation for trace-only floating-point values. BOQ quantities
    /// and money remain numeric; only NaN/Infinity evidence becomes a named string.
    /// </summary>
    internal static class EstimateToolJson
    {
        internal static object TraceNumber(double value) =>
            double.IsFinite(value)
                ? value
                : double.IsNaN(value)
                    ? "NaN"
                    : value > 0 ? "Infinity" : "-Infinity";

        internal static object? TraceNumber(double? value) =>
            value == null ? null : TraceNumber(value.Value);
    }

    /// <summary>
    /// One fail-closed resolver for every post-scan AI estimate stage. It reloads the
    /// profile and compares the active drawing/profile to the immutable scan scope.
    /// </summary>
    internal static class EstimateToolSessionScope
    {
        internal sealed record ResolvedContext(
            EstimateWorkflowService.ScanResult Scan,
            EstimateResult? Estimate,
            ProjectProfile Profile,
            string ProfileWriteTarget);

        internal static string? ValidateScope(
            EstimateWorkflowService.ScanResult scan,
            string? activeDrawing,
            string? activeProfileId,
            string? activeProfileHash,
            string? activeDatabaseRevision = null) =>
            scan.StaleReason(activeDrawing, activeProfileId, activeProfileHash, activeDatabaseRevision);

        internal static bool TryResolve(
            out ResolvedContext? resolved, out string error)
        {
            resolved = null;
            var session = CivilDeliverySession.GetEstimateContext();
            var scan = session.Scan;
            if (scan == null)
            {
                error = "No quantity scan in session — run scan_civil_delivery_quantities first";
                return false;
            }

            if (session.Profile == null ||
                string.IsNullOrWhiteSpace(session.ProfileWriteTarget) ||
                !string.Equals(session.Profile.ProfileId, scan.ProjectProfileId, StringComparison.Ordinal) ||
                !string.Equals(session.ProfileHash, scan.ProjectProfileHash, StringComparison.Ordinal))
            {
                error = "Estimate session scope is inconsistent — run scan_civil_delivery_quantities again";
                return false;
            }

            // Reload the exact file/selector used by the scan. Looking it up again by
            // profile id could silently select a different same-id profile.
            var loaded = new SectionsWorkflowService().LoadProfile(scan.ProfileSource);
            if (!loaded.IsUsable || loaded.Profile == null)
            {
                error = "Project profile is unavailable or invalid — run the quantity scan again after fixing it";
                return false;
            }

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                error = "No active drawing";
                return false;
            }

            var stale = EstimateWorkflowService.FreshnessReason(doc, scan);
            if (stale != null)
            {
                error = stale;
                return false;
            }

            if (session.Estimate != null &&
                !string.Equals(session.Estimate.RunId, scan.RunId, StringComparison.Ordinal))
            {
                error = "The estimate belongs to another scan — build it again from the current scan";
                return false;
            }

            resolved = new ResolvedContext(
                scan, session.Estimate, loaded.Profile, session.ProfileWriteTarget!);
            error = string.Empty;
            return true;
        }

        internal static Autodesk.AutoCAD.ApplicationServices.Document RequireActiveDocument(
            Database database, string operation)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !ReferenceEquals(doc.Database, database))
                throw new InvalidOperationException(
                    $"The active drawing changed before {operation}; run the quantity scan again.");
            return doc;
        }
    }
}
