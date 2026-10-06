using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.SheetQA;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Runs the visual quality checks over ONE printable sheet and marks what it finds.
    ///
    /// One layout per call, deliberately: each call stays a short slice of UI-thread time,
    /// the agent can report progress sheet by sheet, and a sheet that fails to read cannot
    /// take the rest of the drawing down with it.
    ///
    /// The four checks are Ari Bali's specification of 2026-08-11 — text on text, text that
    /// reads upside down, layers that could be unloaded, and agreement between the sheet and
    /// the legend printed on it. All of them are deterministic geometry: no standards
    /// lookup, no retrieval, no model in the loop.
    /// </summary>
    public class RunVisualScanTool : DrawingToolBase
    {
        public override string Name => "run_visual_scan";

        public override string Description =>
            "Runs visual quality checks on one paper-space layout: overlapping text, upside-down text, " +
            "layers that are candidates for unloading, and legend-vs-plan consistency. " +
            "Optionally draws review circles with finding ids on the MAHOD_VISUAL_SCAN layer.";

        public override string Category => ToolCategories.Validation;

        /// <summary>
        /// A dense coordination sheet carries tens of thousands of entities across ~70
        /// xrefs; the default 30 seconds is not enough for it.
        /// </summary>
        public override TimeSpan Timeout => TimeSpan.FromSeconds(300);

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var db = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager
                .MdiActiveDocument?.Database;
            if (db == null)
            {
                return Task.FromResult(ToolResult.Fail(
                    VisualScanErrorCodes.ScanFailed, "No active document."));
            }

            var layoutName = GetStringParam(parameters, "layout_name");
            if (string.IsNullOrWhiteSpace(layoutName))
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    "layout_name is required — the visual scan runs one sheet at a time."));
            }

            var drawMarkup = GetBoolParam(parameters, "draw_markup", true);
            var clearExisting = GetBoolParam(parameters, "clear_existing_markup", true);
            var options = BuildOptions(parameters);

            var styles = new SheetStyleResolver(tr, db);
            var extractor = new SheetSnapshotExtractor(tr, db, styles);

            var layout = extractor.FindLayout(layoutName!);
            if (layout == null)
            {
                var available = string.Join(", ", extractor.GetLayouts().Select(l => l.LayoutName));
                return Task.FromResult(ToolResult.Fail(
                    VisualScanErrorCodes.LayoutNotFound,
                    $"Layout '{layoutName}' was not found.",
                    string.IsNullOrEmpty(available) ? null : $"Available layouts: {available}"));
            }

            SheetSnapshot snapshot;
            LegendReadResult legend;
            try
            {
                snapshot = extractor.Extract(layout);
                legend = new LegendBlockReader(tr, styles).Read(layout);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    VisualScanErrorCodes.ScanFailed,
                    $"Could not read layout '{layoutName}': {ex.Message}",
                    ex.ToString()));
            }

            ct.ThrowIfCancellationRequested();

            var analysis = SheetQaAnalyzer.Analyze(
                layout.LayoutName, snapshot.Texts, snapshot.Curves, legend.Rows, options);

            // Carry the extraction's own counters into the reported stats: they say how much
            // of the sheet was actually looked at, which is what makes a low finding count
            // trustworthy rather than merely quiet.
            analysis.Stats.XrefsWalked = snapshot.Stats.XrefsWalked;
            analysis.Stats.HiddenLayerSkipped = snapshot.Stats.HiddenLayerSkipped;
            analysis.Stats.OutsideViewportSkipped = snapshot.Stats.OutsideViewportSkipped;
            analysis.Stats.UnreadableEntities = snapshot.Stats.UnreadableEntities;

            analysis.Approximations.AddRange(BuildApproximations(snapshot, legend, options));

            var markupDrawn = 0;
            var markupErased = 0;
            if (drawMarkup)
            {
                var writer = new VisualMarkupWriter(tr, db);
                try
                {
                    if (clearExisting) markupErased = writer.EraseAllMarkup();
                    markupDrawn = writer.DrawFindings(layout, analysis.Findings);
                }
                catch (Exception ex)
                {
                    // The findings are still worth reporting even if the drawing could not
                    // be marked; say so rather than failing the whole scan.
                    analysis.Approximations.Add($"Markup could not be drawn: {ex.Message}");
                }
            }

            var result = new VisualScanToolResult
            {
                Layout = layout.LayoutName,
                Findings = analysis.Findings,
                DeclutterCandidates = analysis.DeclutterCandidates,
                Legend = BuildLegendSummary(legend, analysis.Legend),
                Stats = analysis.Stats,
                TotalsByCategory = analysis.TotalsByCategory,
                TruncatedCategories = analysis.TruncatedCategories,
                Approximations = analysis.Approximations,
                Warnings = snapshot.Warnings,
                MarkupDrawn = markupDrawn,
                MarkupErased = markupErased,
                MarkupLayer = VisualMarkupWriter.MarkupLayerName,
                MaxFindingsPerCheck = options.MaxFindingsPerCheck
            };

            return Task.FromResult(ToolResult.Ok(result));
        }

        private static SheetQaOptions BuildOptions(JsonElement parameters)
        {
            var options = new SheetQaOptions();

            var requested = GetStringArrayParam(parameters, "checks");
            if (requested is { Length: > 0 })
            {
                var checks = SheetQaChecks.None;
                foreach (var name in requested)
                {
                    checks |= name.Trim().ToLowerInvariant() switch
                    {
                        "text_overlap" => SheetQaChecks.TextOverlap,
                        "rotated_text" => SheetQaChecks.RotatedText,
                        "declutter" => SheetQaChecks.Declutter,
                        "legend" => SheetQaChecks.Legend,
                        _ => SheetQaChecks.None
                    };
                }
                if (checks != SheetQaChecks.None) options.Checks = checks;
            }

            var cap = GetIntParam(parameters, "max_findings_per_check");
            if (cap is > 0) options.MaxFindingsPerCheck = cap.Value;

            var ratio = GetDoubleParam(parameters, "min_overlap_ratio");
            if (ratio is > 0 and <= 1) options.MinOverlapRatio = ratio.Value;

            var utilityXrefs = GetStringArrayParam(parameters, "utility_xrefs");
            if (utilityXrefs is { Length: > 0 })
            {
                options.UtilityXrefs = new HashSet<string>(utilityXrefs, StringComparer.OrdinalIgnoreCase);
            }

            return options;
        }

        /// <summary>
        /// States the run's own limits alongside its findings. A scan that quietly skipped
        /// half the sheet and a genuinely clean sheet look identical otherwise.
        /// </summary>
        private static List<string> BuildApproximations(
            SheetSnapshot snapshot, LegendReadResult legend, SheetQaOptions options)
        {
            var notes = new List<string>();

            if (!snapshot.HasModelViewport)
            {
                notes.Add("This layout has no model viewport; only its own paper-space content was checked.");
            }

            if (snapshot.Stats.UnreadableEntities > 0)
            {
                notes.Add($"{snapshot.Stats.UnreadableEntities} entities could not be read " +
                          "(proxy or custom objects) and were skipped.");
            }

            if (legend.Rows.Count == 0)
            {
                notes.Add(legend.Note ?? "No legend was read from this sheet, so legend checks are unreliable.");
            }
            else if (!string.IsNullOrEmpty(legend.Note))
            {
                notes.Add(legend.Note!);
            }

            if (options.UtilityXrefs == null && options.Checks.HasFlag(SheetQaChecks.Legend))
            {
                notes.Add("Legend comparison covered every xref, including survey and topographic " +
                          "linework a legend is not expected to list; pass utility_xrefs to scope it.");
            }

            notes.Add("Text collisions are measured on bounding boxes, and text against symbols " +
                      "or dimensions is not yet checked.");
            notes.Add("Blocks nested inside an xref were not descended into.");

            return notes;
        }

        private static VisualScanLegendSummary BuildLegendSummary(
            LegendReadResult legend, LegendDiffResult? diff) => new()
            {
                BlockName = legend.BlockName,
                IdentifiedByName = legend.IdentifiedByName,
                RowCount = legend.Rows.Count,
                Rows = legend.Rows.Select(r => new VisualScanLegendRow
                {
                    ColorIndex = r.ColorIndex,
                    Linetype = r.Linetype,
                    Label = r.Label
                }).ToList(),
                PlanStyleCount = diff?.PlanStyleCount ?? 0,
                MatchedStyleCount = diff?.MatchedStyleCount ?? 0,
                MissingFromLegendCount = diff?.MissingFromLegend.Count ?? 0,
                OrphanRowCount = diff?.OrphanLegendRows.Count ?? 0
            };
    }

    #region Result Models

    public static class VisualScanErrorCodes
    {
        public const string LayoutNotFound = "LAYOUT_NOT_FOUND";
        public const string NoModelViewport = "NO_MODEL_VIEWPORT";
        public const string ScanFailed = "SCAN_FAILED";
    }

    public class VisualScanToolResult
    {
        public string Layout { get; set; } = string.Empty;
        public List<VisualFinding> Findings { get; set; } = new();
        public List<DeclutterCandidate> DeclutterCandidates { get; set; } = new();
        public VisualScanLegendSummary Legend { get; set; } = new();
        public SheetQaStats Stats { get; set; } = new();

        /// <summary>True counts per category, before the per-category cap.</summary>
        public Dictionary<string, int> TotalsByCategory { get; set; } = new();

        /// <summary>Categories where the cap truncated the list.</summary>
        public List<string> TruncatedCategories { get; set; } = new();

        public List<string> Approximations { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        public int MarkupDrawn { get; set; }
        public int MarkupErased { get; set; }
        public string MarkupLayer { get; set; } = string.Empty;
        public int MaxFindingsPerCheck { get; set; }
    }

    public class VisualScanLegendSummary
    {
        public string? BlockName { get; set; }
        public bool IdentifiedByName { get; set; }
        public int RowCount { get; set; }
        public List<VisualScanLegendRow> Rows { get; set; } = new();
        public int PlanStyleCount { get; set; }
        public int MatchedStyleCount { get; set; }
        public int MissingFromLegendCount { get; set; }
        public int OrphanRowCount { get; set; }
    }

    public class VisualScanLegendRow
    {
        public int ColorIndex { get; set; }
        public string Linetype { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    #endregion
}
