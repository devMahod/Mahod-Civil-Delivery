using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.Tools.CivilDelivery
{
    /// <summary>
    /// MahodAI route for project setup. The AI may present candidates and collect
    /// the engineer's choice; it may NOT choose CL layers, alignments or sources
    /// itself — <see cref="SaveProjectSetupTool"/> requires explicit selections and
    /// an approver, exactly like the direct command.
    /// </summary>
    public class ScanProjectSetupTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed class SetupScanPayload : Dictionary<string, object?>
        {
            internal SetupScanPayload(
                Autodesk.AutoCAD.ApplicationServices.Document document,
                ProjectSetupScan scan,
                ProjectProfile profile,
                string profileHash,
                string profileWriteTarget)
            {
                Document = document;
                Scan = scan;
                Profile = profile;
                ProfileHash = profileHash;
                ProfileWriteTarget = profileWriteTarget;
            }

            internal Autodesk.AutoCAD.ApplicationServices.Document Document { get; }
            internal ProjectSetupScan Scan { get; }
            internal ProjectProfile Profile { get; }
            internal string ProfileHash { get; }
            internal string ProfileWriteTarget { get; }
        }

        public override string Name => "scan_civil_delivery_project_setup";
        public override string Description =>
            "Scans the drawing for project-setup candidates: which layers look like CL section lines " +
            "(with crossing evidence), which alignments exist, and which surfaces/corridors/networks " +
            "can be sampled. Read-only. Use when the project profile is not configured yet or when " +
            "sections come back empty. Returns ranked candidates — it never selects.";
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
            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Civil 3D document required"));

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, "No active drawing"));
            if (!EstimateWorkflowService.TransactionMatches(doc, tr))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "The active drawing changed before the setup scan"));

            var workflow = new SectionsWorkflowService();
            var loaded = new ActiveProjectProfileService().LoadForDocument(
                doc, workflow, GetStringParam(parameters, "profile_id"));
            if (!loaded.IsUsable || loaded.Profile == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Project profile '{loaded.ProfileSource}' is missing or invalid"));

            var source = ProjectSetupService.CaptureReadySource(
                doc, "AI project setup scan");
            CivilDeliverySession.ClearSetupContext();
            var scan = new ProjectSetupScanner().Scan(
                doc.Database, tr, civilDoc, loaded.Profile);
            ProjectSetupService.IncludeConfiguredExternalCl(
                scan, loaded.Profile, source.DrawingPath, civilDoc, tr);
            ProjectSetupService.BindScanEvidence(
                scan, loaded.Profile, loaded.ProfileHash ??
                    throw new InvalidOperationException(
                        "The usable project profile has no exact source SHA-256."),
                loaded.ProfileSource,
                loaded.ProfileWriteTarget, source,
                loaded.ProfileWriteState ?? throw new InvalidOperationException(
                    "The loaded profile has no source/target CAS evidence."));

            var payload = new SetupScanPayload(
                doc, scan, loaded.Profile, loaded.ProfileHash,
                loaded.ProfileWriteTarget)
            {
                ["run_id"] = scan.RunId,
                ["profile_id"] = loaded.Profile.ProfileId,
                ["profile_source"] = loaded.ProfileSource,
                ["profile_is_configured"] = scan.ProfileIsConfigured,
                ["needs_setup"] = ProjectSetupService.NeedsSetup(loaded.Profile),
                ["cl_layer_candidates"] = scan.ClLayerCandidates.Take(15).Select(c => new Dictionary<string, object?>
                {
                    ["layer"] = c.Layer,
                    ["evidence_score"] = c.EvidenceScore,
                    ["two_point_entities"] = c.TwoPointCount,
                    ["crossings"] = c.CrossingCount,
                    ["alignments_crossed"] = c.AlignmentsCrossed,
                    ["median_length"] = c.MedianLength,
                    ["in_xref"] = c.InXref,
                    ["sample_labels"] = c.SampleLabels,
                    ["why"] = c.Why,
                }).ToList(),
                ["alignments"] = scan.Alignments.Select(a => new Dictionary<string, object?>
                {
                    ["name"] = a.Name,
                    ["start_station"] = a.StartStation,
                    ["end_station"] = a.EndStation,
                    ["crossed_by"] = a.CrossedByCandidateLayers.Distinct().ToList(),
                }).ToList(),
                ["sources"] = scan.Sources.Select(s => new { s.Name, s.Kind }).ToList(),
                ["findings"] = scan.Findings.Select(f => new { f.Code, severity = f.Severity.ToString(), f.Title }).ToList(),
            };
            return Task.FromResult(ToolResult.ReadOnly(payload));
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (result.Data is not SetupScanPayload pending ||
                !ReferenceEquals(database, pending.Document.Database))
                throw new InvalidOperationException(
                    "Setup scan completed without matching post-transaction evidence.");
            try
            {
                ProjectSetupService.PublishScanEvidenceAfterReadClose(
                    pending.Document, pending.Scan);
                CivilDeliverySession.SetSetupScan(
                    pending.Scan, pending.Profile, pending.ProfileHash,
                    pending.ProfileWriteTarget);
            }
            catch
            {
                CivilDeliverySession.ClearSetupContext();
                throw;
            }
        }
    }

    public class SaveProjectSetupTool : DrawingToolBase, IReadOnlyTransactionClosedObserver
    {
        private sealed class SetupSavePayload : Dictionary<string, object?>
        {
            internal SetupSavePayload(
                Autodesk.AutoCAD.ApplicationServices.Document document,
                ProjectProfile profile,
                ProjectSetupScan scan,
                ProjectSetupSelection selection,
                string profileHash,
                string profileSource,
                string profileWriteTarget)
            {
                Document = document;
                Profile = profile;
                Scan = scan;
                Selection = selection;
                ProfileHash = profileHash;
                ProfileSource = profileSource;
                ProfileWriteTarget = profileWriteTarget;
            }

            internal Autodesk.AutoCAD.ApplicationServices.Document Document { get; }
            internal ProjectProfile Profile { get; }
            internal ProjectSetupScan Scan { get; }
            internal ProjectSetupSelection Selection { get; }
            internal string ProfileHash { get; }
            internal string ProfileSource { get; }
            internal string ProfileWriteTarget { get; }
        }

        public override string Name => "save_civil_delivery_project_setup";
        public override string Description =>
            "Saves the ENGINEER'S explicit project-setup choices (CL layers, allowed alignments, " +
            "required section sources, intersection tolerance) into the versioned project profile with " +
            "provenance. Every value must come from the engineer — never propose-and-save in one step.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""cl_layers"": {
                    ""type"": ""array"", ""items"": { ""type"": ""string"" },
                    ""description"": ""Layer names the engineer confirmed carry CL section lines""
                },
                ""allowed_alignments"": {
                    ""type"": ""array"", ""items"": { ""type"": ""string"" },
                    ""description"": ""Alignment names the engineer allows (empty = all)""
                },
                ""sampled_sources"": {
                    ""type"": ""object"",
                    ""description"": ""Map of source name -> kind (surface|corridor|pipe-network)"",
                    ""additionalProperties"": { ""type"": ""string"" }
                },
                ""intersection_tolerance_m"": {
                    ""type"": ""number"", ""description"": ""Approved CL/alignment drafting tolerance in meters""
                },
                ""approved_by"": {
                    ""type"": ""string"", ""description"": ""Name of the engineer approving this configuration""
                }
            },
            ""required"": [""cl_layers"", ""approved_by""]
        }").RootElement;
        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !EstimateWorkflowService.TransactionMatches(doc, tr))
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    "The active drawing changed before the setup decision"));
            var setup = CivilDeliverySession.GetSetupContext();
            var scan = setup.Scan;
            var profile = setup.Profile;
            if (scan == null || profile == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Run scan_civil_delivery_project_setup first — configuration must be based on a real scan"));

            var clLayers = ReadStringArray(parameters, "cl_layers");
            if (clLayers.Count == 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "cl_layers is required — the engineer must choose the CL source layers"));

            var approvedBy = GetStringParam(parameters, "approved_by");
            if (string.IsNullOrWhiteSpace(approvedBy))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "approved_by is required — configuration is an engineering decision"));

            // Every named layer/alignment/source must exist in the scan: the AI cannot
            // introduce a value the drawing never offered.
            var knownLayers = scan.ClLayerCandidates.Select(c => c.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownLayer = clLayers.FirstOrDefault(l => !knownLayers.Contains(l));
            if (unknownLayer != null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Layer '{unknownLayer}' is not among the scanned candidates"));

            var alignments = ReadStringArray(parameters, "allowed_alignments");
            var knownAlignments = scan.Alignments.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownAlignment = alignments.FirstOrDefault(a => !knownAlignments.Contains(a));
            if (unknownAlignment != null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Alignment '{unknownAlignment}' does not exist in this drawing"));

            var sourceSelection = ReadSourceSelection(scan, parameters);
            if (sourceSelection.Error != null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters, sourceSelection.Error));

            var selection = new ProjectSetupSelection
            {
                ClLayers = clLayers,
                ClSourceFiles = ClSourceSelection.MergeSources(scan.ExternalClHashes.Keys, scan.Drawing),
                AllowedAlignments = alignments,
                SampledSources = sourceSelection.Sources,
                IntersectionToleranceM = GetDoubleParam(parameters, "intersection_tolerance_m"),
                ApprovedBy = approvedBy,
            };

            var writeTarget = setup.ProfileWriteTarget ??
                throw new InvalidOperationException(
                    "Project profile write target is unavailable — run the setup scan again");
            var profileHash = setup.ProfileHash ?? string.Empty;
            var profileSource = scan.ProfileSource ?? string.Empty;
            // Early read-only refusal for a stale/unpublished scan. The callback repeats
            // this after Abort+Dispose immediately before the durable profile write.
            ProjectSetupService.RequireFreshAndPublished(
                doc, profile, scan, profileHash, profileSource);

            var payload = new SetupSavePayload(
                doc, profile, scan, selection, profileHash, profileSource,
                writeTarget)
            {
                ["pending_post_transaction_save"] = true,
                ["message"] = "Configuration validated; durable save is pending transaction close.",
            };
            return Task.FromResult(ToolResult.ReadOnly(payload));
        }

        internal static (Dictionary<string, string> Sources, string? Error) ReadSourceSelection(
            ProjectSetupScan scan, JsonElement parameters)
        {
            static (Dictionary<string, string>, string?) Refuse(string error) =>
                (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), error);
            if (!parameters.TryGetProperty("sampled_sources", out var sourceObject) ||
                sourceObject.ValueKind != JsonValueKind.Object)
                return Refuse("sampled_sources must contain engineer-confirmed source names and kinds; partial setup is not saved");
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in sourceObject.EnumerateObject())
            {
                var name = property.Name.Trim();
                var kind = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : null;
                if (name.Length == 0 || kind == null ||
                    !new[] { "surface", "corridor", "pipe-network" }.Contains(kind, StringComparer.OrdinalIgnoreCase))
                    return Refuse($"Source '{property.Name}' requires an explicit valid kind (surface|corridor|pipe-network)");
                var matches = scan.Sources.Where(source =>
                    string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    return Refuse($"Source '{name}'/'{kind}' is not one unique scanned source; no source kind was substituted");
                if (!sources.TryAdd(matches[0].Name, matches[0].Kind))
                    return Refuse($"Source '{name}' was requested more than once; the name-to-kind contract cannot choose between duplicates");
            }
            return sources.Count == 0
                ? Refuse("sampled_sources must contain at least one source confirmed by the engineer; partial setup is not saved")
                : (sources, null);
        }

        public void OnReadOnlyTransactionClosed(Database database, ToolResult result)
        {
            if (result.Data is not SetupSavePayload pending ||
                !ReferenceEquals(database, pending.Document.Database))
                throw new InvalidOperationException(
                    "Setup decision completed without matching post-transaction state.");
            try
            {
                var saved = ProjectSetupService.Save(
                    pending.Document, pending.Profile, pending.Selection, pending.Scan,
                    pending.ProfileHash, pending.ProfileSource,
                    pending.ProfileWriteTarget);
                pending.Clear();
                pending["saved_path"] = saved.Path;
                pending["profile_version"] = saved.NewVersion;
                pending["backup_path"] = saved.BackupPath;
                pending["profile_hash"] = saved.NewHash;
                pending["cl_layers"] = pending.Profile.Sections.Cl.LayerPatterns;
                pending["allowed_alignments"] = pending.Profile.Sections.Alignments.AllowedNames;
                pending["sampled_sources"] = pending.Profile.Sections.Sources.SampledSourceRules
                    .Select(rule => new { rule.Name, rule.Kind }).ToList();
                pending["message"] =
                    "Configuration saved with provenance; re-run the section plan to use it.";
                CivilDeliverySession.ClearSetupContext();
            }
            catch
            {
                CivilDeliverySession.ClearSetupContext();
                throw;
            }
        }

        private static List<string> ReadStringArray(JsonElement parameters, string name)
        {
            if (!parameters.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            return arr.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
    }
}
