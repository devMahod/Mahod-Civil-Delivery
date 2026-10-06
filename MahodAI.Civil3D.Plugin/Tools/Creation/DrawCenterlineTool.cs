using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Draws an alignment (centerline) interactively: prompts the user to pick a start
    /// and end point in the drawing, then creates a real Civil 3D Alignment between them.
    ///
    /// v1: straight A→B only. Multi-PI routing with auto-curves and obstacle skirting
    /// is deferred until the obstacle layer/source and grade limit are defined.
    /// </summary>
    public class DrawCenterlineTool : DrawingToolBase
    {
        public override string Name => "draw_centerline";

        public override string Description =>
            "Interactively draws a road centerline (Civil 3D Alignment) between two points " +
            "the user clicks in the drawing. Agent supplies the alignment name and an optional " +
            "default curve radius derived from road class / standards. The tool prompts the user " +
            "twice (start, end) with OSNAP active; ESC at either prompt cancels cleanly. " +
            "Result is a true Alignment visible in Toolspace → Prospector → Alignments. " +
            "v1 is straight A→B; intermediate-PI routing is a future enhancement.";

        public override string Category => ToolCategories.Creation;

        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Modal point picks — the executor flushes chat renders first.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Alignment name. Use English only — Hebrew breaks Civil 3D. Example: 'TestRoad1'.""
                },
                ""radius"": {
                    ""type"": ""number"",
                    ""description"": ""Default curve radius (m) for intermediate PI curves, derived from road-class standards. Unused in v1 (straight A→B); kept on the schema so v2 picks it up without an agent-side change.""
                },
                ""layer"": {
                    ""type"": ""string"",
                    ""description"": ""Layer for the alignment (default: '0').""
                }
            },
            ""required"": [""name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            string name;
            try { name = GetRequiredStringParam(parameters, "name"); }
            catch (Exception ex) { return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, ex.Message)); }

            var layer = GetStringParam(parameters, "layer") ?? "0";
            // radius is parsed but unused in v1 — see FIXME below.
            _ = GetDoubleParam(parameters, "radius");

            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document"));

            if (ObjectFinder.FindAlignment(civilDoc, tr, name) != null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    $"Alignment '{name}' already exists."));

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active document"));

            // Intersection snapping on a dense survey base makes picking impossible
            // ("Too many objects selected for INTERSECT") — suspend it while we prompt.
            using var pickScope = new Utilities.InteractivePickScope();

            // Prompt 1: start point
            ToolUiNotifier.Step("🖱️ **בחר נקודת התחלה** בשרטוט (Esc לביטול).");
            PromptPointResult startRes;
            try
            {
                startRes = doc.Editor.GetPoint(new PromptPointOptions("\nבחר נקודת התחלה:") { AllowNone = false });
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, ex.Message, ex.ToString()));
            }
            if (startRes.Status != PromptStatus.OK)
            {
                return Task.FromResult(ToolResult.Cancelled(new
                {
                    success = true,
                    status = "cancel",
                    message = "User cancelled at start point. No alignment created."
                }));
            }

            // Prompt 2: end point — a plain single-point pick, same as the start point. The
            // rubber-band was dropped 2026-07-28: on a dense survey base it drags a line across
            // the whole drawing under the cursor and makes aiming harder, not clearer.
            ToolUiNotifier.Step("🖱️ **בחר נקודת סיום** (Esc לביטול).");
            PromptPointResult endRes;
            try
            {
                endRes = doc.Editor.GetPoint(
                    new PromptPointOptions("\nבחר נקודת סיום:") { AllowNone = false });
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, ex.Message, ex.ToString()));
            }
            if (endRes.Status != PromptStatus.OK)
            {
                return Task.FromResult(ToolResult.Cancelled(new
                {
                    success = true,
                    status = "cancel",
                    message = "User cancelled at end point. No alignment created."
                }));
            }

            // Civil 3D's AddFixedLine takes Point3d; use Z=0 for the alignment plane.
            var start3d = new Point3d(startRes.Value.X, startRes.Value.Y, 0.0);
            var end3d = new Point3d(endRes.Value.X, endRes.Value.Y, 0.0);
            if (start3d.DistanceTo(end3d) < 1e-6)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    "Start and end points are identical. Pick two distinct points."));
            }

            // Resolve drawing defaults — same pattern as CreateAlignmentTool.cs:376-395.
            ObjectId styleId = ObjectId.Null;
            try
            {
                foreach (ObjectId id in civilDoc.Styles.AlignmentStyles) { styleId = id; break; }
            }
            catch { }

            ObjectId labelSetId = ObjectId.Null;
            try
            {
                foreach (ObjectId id in civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles) { labelSetId = id; break; }
            }
            catch { }

            var db = HostApplicationServices.WorkingDatabase;
            ObjectId layerId = db.Clayer;
            try
            {
                var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (lt != null && lt.Has(layer))
                    layerId = lt[layer];
            }
            catch { }

            // Create the empty centerline alignment, then add a single fixed-line tangent.
            //
            // FIXME(routing): v1 draws a straight A→B only. v2 must insert intermediate PIs
            //   that follow the active surface within max-grade limits and skirt obstacles
            //   on excluded layers (source TBD), then call
            //   alignment.Entities.AddFreeCurve(prev.EntityId, next.EntityId, radius,
            //       CurveParamType.Radius, false, CurveType.Compound)
            //   between consecutive AddFixedLine entities so every PI has a smooth curve.
            ObjectId alignmentId;
            try
            {
                alignmentId = CivilDb.Alignment.Create(
                    civilDoc,
                    name,
                    ObjectId.Null,
                    layerId,
                    styleId,
                    labelSetId,
                    CivilDb.AlignmentType.Centerline);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Alignment.Create failed: {ex.Message}",
                    ex.ToString()));
            }

            CivilDb.Alignment alignment;
            try
            {
                alignment = (CivilDb.Alignment)tr.GetObject(alignmentId, OpenMode.ForWrite);
                alignment.Entities.AddFixedLine(start3d, end3d);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"AddFixedLine failed: {ex.Message}",
                    ex.ToString()));
            }

            cache.RemoveByPattern("list_alignments:");
            cache.RemoveByPattern("get_drawing_summary:");

            return Task.FromResult(ToolResult.Ok(new
            {
                success = true,
                alignment_name = alignment.Name,
                length_m = Math.Round(alignment.Length, 2),
                start_point = new { x = Math.Round(start3d.X, 3), y = Math.Round(start3d.Y, 3) },
                end_point = new { x = Math.Round(end3d.X, 3), y = Math.Round(end3d.Y, 3) },
                message = $"Alignment '{alignment.Name}' drawn. Length {alignment.Length:F1}m."
            }));
        }
    }
}
