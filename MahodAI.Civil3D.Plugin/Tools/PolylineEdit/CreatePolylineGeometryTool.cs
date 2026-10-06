using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// The construction commands of the PLTOOLS set: <c>MPL</c> (mid-line between two curves),
    /// <c>R3P</c> (rectangle through three points), <c>PL-P90</c> (a chain of mutually perpendicular
    /// segments) and <c>PL-CLONE</c> (a new polyline built from chosen segments of an existing one).
    ///
    /// Points may be supplied by the agent (<c>points</c>) or picked in the drawing — with no
    /// points the tool prompts, which is how the originals work.
    /// </summary>
    public class CreatePolylineGeometryTool : DrawingToolBase
    {
        public override string Name => "create_polyline_geometry";

        public override string Description =>
            "Creates a new polyline from geometry. mode='midline' draws the centre line between two " +
            "curves (curve_handles, sample_count); 'rectangle_3points' draws a rectangle from three " +
            "points (two define one side, the third its width); 'perpendicular_chain' snaps a click " +
            "chain into mutually perpendicular segments (base_angle_deg to rotate the axes); " +
            "'clone_segments' rebuilds a polyline from chosen segments of source_handle " +
            "(segment_indices). Points can be given in `points` as [[x,y],…] or picked by the user " +
            "when omitted. Returns the new polyline's handle.";

        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromMinutes(10);

        /// <summary>Point picks are modal when `points` is not supplied.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""midline"", ""rectangle_3points"", ""perpendicular_chain"", ""clone_segments""]
                },
                ""curve_handles"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""mode='midline': the two curves to run between.""
                },
                ""sample_count"": { ""type"": ""integer"", ""description"": ""mode='midline': number of points along the result. Default 100 (the PLTOOLS default)."" },
                ""points"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
                    ""description"": ""Explicit points as [[x,y],…]. Omit to have the user pick them.""
                },
                ""base_angle_deg"": { ""type"": ""number"", ""description"": ""mode='perpendicular_chain': angle of the reference axis. Default 0."" },
                ""source_handle"": { ""type"": ""string"", ""description"": ""mode='clone_segments': the polyline to copy segments from."" },
                ""segment_indices"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""mode='clone_segments': 0-based segments to copy, in order."" },
                ""layer"": { ""type"": ""string"", ""description"": ""Layer for the new polyline. Created if missing."" },
                ""closed"": { ""type"": ""boolean"", ""description"": ""Close the new polyline. rectangle_3points is always closed."" }
            },
            ""required"": [""mode""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            var mode = (GetStringParam(parameters, "mode") ?? string.Empty).Trim().ToLowerInvariant();
            var layer = GetStringParam(parameters, "layer");
            var notes = new List<string>();

            switch (mode)
            {
                case "midline":
                    return Task.FromResult(BuildMidline(tr, db, parameters, layer, notes));

                case "rectangle_3points":
                    return Task.FromResult(BuildRectangle(tr, db, parameters, layer, notes));

                case "perpendicular_chain":
                    return Task.FromResult(BuildPerpendicularChain(tr, db, parameters, layer, notes));

                case "clone_segments":
                    return Task.FromResult(BuildCloneSegments(tr, db, parameters, layer, notes));

                default:
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"mode לא נתמך: '{mode}'. אפשרויות: midline, rectangle_3points, perpendicular_chain, clone_segments"));
            }
        }

        // ── MPL ──────────────────────────────────────────────────────

        private ToolResult BuildMidline(
            Transaction tr, Database db, JsonElement parameters, string? layer, List<string> notes)
        {
            var handles = GetStringArrayParam(parameters, "curve_handles");
            if (handles == null || handles.Length < 2)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "mode='midline' דורש curve_handles של שתי עקומות");

            var chains = new List<List<Pt2>>();
            int samples = GetIntParam(parameters, "sample_count") ?? PolylineBuilders.DefaultMidlineSamples;

            for (int i = 0; i < 2; i++)
            {
                if (!PolylineToolSupport.TryResolveHandle(db, handles[i], out var id))
                    return ToolResult.NotFound("Curve", handles[i]);
                if (tr.GetObject(id, OpenMode.ForRead) is not Curve curve)
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters, $"{handles[i]}: אינו עקומה");

                var chain = SampleCurve(curve, Math.Max(samples, 32));
                if (chain.Count < 2)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, $"{handles[i]}: דגימת העקומה נכשלה");
                chains.Add(chain);
            }

            var mid = PolylineBuilders.Midline(chains[0], chains[1], samples);
            if (mid.Count < 2)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נוצר קו אמצע");

            bool closed = GetBoolParam(parameters, "closed");
            var newId = PolylineToolSupport.CreatePolyline(tr, db, mid, closed, 0.0, layer);
            notes.Add($"קו האמצע נדגם ב-{mid.Count} נקודות");

            return ToolResult.Ok(new
            {
                mode = "midline",
                new_handle = PolylineToolSupport.HandleOf(tr, newId),
                vertex_count = mid.Count,
                source_handles = handles,
                sample_count = samples,
                notes,
            });
        }

        // ── R3P ──────────────────────────────────────────────────────

        private ToolResult BuildRectangle(
            Transaction tr, Database db, JsonElement parameters, string? layer, List<string> notes)
        {
            if (!TryGetPoints(parameters, 3, new[]
                {
                    "לחצי על הפינה הראשונה של המלבן:",
                    "לחצי על הפינה השנייה (אורך הצלע):",
                    "לחצי על נקודה שקובעת את רוחב המלבן:",
                }, out var picks, out var cancelled, out var error))
            {
                return cancelled
                    ? ToolResult.Cancelled(new { cancelled = true, mode = "rectangle_3points" })
                    : ToolResult.Fail(ToolErrorCodes.InvalidParameters, error ?? "נדרשות שלוש נקודות");
            }

            var corners = PolylineBuilders.RectangleFrom3Points(picks[0], picks[1], picks[2]);
            if (corners.Count != 4)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "שלוש הנקודות על קו אחד (או חופפות) — אי אפשר לבנות מלבן");

            var newId = PolylineToolSupport.CreatePolyline(tr, db, corners, closed: true, 0.0, layer);
            return ToolResult.Ok(new
            {
                mode = "rectangle_3points",
                new_handle = PolylineToolSupport.HandleOf(tr, newId),
                vertex_count = 4,
                closed = true,
                notes,
            });
        }

        // ── PL-P90 ───────────────────────────────────────────────────

        private ToolResult BuildPerpendicularChain(
            Transaction tr, Database db, JsonElement parameters, string? layer, List<string> notes)
        {
            double baseAngleDeg = GetDoubleParam(parameters, "base_angle_deg") ?? 0.0;
            var explicitPoints = ReadPoints(parameters);

            List<Pt2> picks;
            if (explicitPoints.Count >= 2)
            {
                picks = explicitPoints;
            }
            else
            {
                picks = new List<Pt2>();
                var doc = PolylineToolSupport.ActiveDocument;
                if (doc == null) return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "אין שרטוט פעיל");
                var ed = doc.Editor;

                using var pickScope = new Utilities.InteractivePickScope();
                while (true)
                {
                    string prompt = picks.Count == 0
                        ? "לחצי על נקודת ההתחלה:"
                        : "לחצי על הנקודה הבאה — Enter לסיום:";
                    ToolUiNotifier.Step($"🖱️ {prompt}");

                    var opts = new PromptPointOptions("\n" + prompt) { AllowNone = picks.Count > 0 };
                    if (picks.Count > 0)
                    {
                        opts.UseBasePoint = true;
                        opts.BasePoint = new Point3d(picks[^1].X, picks[^1].Y, 0.0);
                    }

                    var res = ed.GetPoint(opts);
                    if (res.Status != PromptStatus.OK) break;
                    var world = PolylineToolSupport.UcsToWorld(ed, res.Value);
                    picks.Add(new Pt2(world.X, world.Y));
                }

                if (picks.Count < 2)
                    return ToolResult.Cancelled(new { cancelled = true, mode = "perpendicular_chain" });
            }

            var chain = PolylineBuilders.PerpendicularChain(picks, baseAngleDeg * Math.PI / 180.0);
            if (chain.Count < 2)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נוצרה שרשרת מקטעים");

            bool closed = GetBoolParam(parameters, "closed");
            var newId = PolylineToolSupport.CreatePolyline(tr, db, chain, closed, 0.0, layer);
            notes.Add($"{picks.Count} נקודות יושרו ל-{chain.Count} קודקודים בזוויות ישרות");

            return ToolResult.Ok(new
            {
                mode = "perpendicular_chain",
                new_handle = PolylineToolSupport.HandleOf(tr, newId),
                vertex_count = chain.Count,
                base_angle_deg = baseAngleDeg,
                closed,
                notes,
            });
        }

        // ── PL-CLONE ─────────────────────────────────────────────────

        private ToolResult BuildCloneSegments(
            Transaction tr, Database db, JsonElement parameters, string? layer, List<string> notes)
        {
            var sourceHandle = GetStringParam(parameters, "source_handle");
            if (string.IsNullOrEmpty(sourceHandle))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "mode='clone_segments' דורש source_handle");
            if (!PolylineToolSupport.TryResolveHandle(db, sourceHandle!, out var id))
                return ToolResult.NotFound("Polyline", sourceHandle!);
            if (!PolylineAdapter.TryOpen(tr, id, out var target, out var code, out var msg))
                return ToolResult.Fail(code ?? ToolErrorCodes.ExecutionFailed, msg ?? "לא ניתן לקרוא את הפוליליין");

            var indices = new List<int>();
            if (parameters.TryGetProperty("segment_indices", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number) continue;
                    indices.Add(item.TryGetInt32(out int v) ? v : (int)item.GetDouble());
                }
            }
            if (indices.Count == 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "mode='clone_segments' דורש segment_indices");

            var shape = target!.Shape;
            var points = new List<Pt2>();
            var bulges = new List<double>();

            foreach (int segment in indices)
            {
                if (segment < 0 || segment >= shape.SegmentCount)
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"מקטע {segment} מחוץ לתחום (0..{shape.SegmentCount - 1})");

                var a = shape.SegmentStart(segment);
                var b = shape.SegmentEnd(segment);

                // Consecutive segments share a vertex — do not duplicate it.
                if (points.Count == 0 || Math.Abs(points[^1].X - a.X) > 1e-9 || Math.Abs(points[^1].Y - a.Y) > 1e-9)
                {
                    points.Add(a.P);
                    bulges.Add(shape.SegmentBulge(segment));
                }
                else
                {
                    bulges[^1] = shape.SegmentBulge(segment);
                }

                points.Add(b.P);
                bulges.Add(0.0);
            }

            bool closed = GetBoolParam(parameters, "closed");
            var newId = PolylineToolSupport.CreatePolyline(
                tr, db, points, closed, shape.Elevation, layer, bulges);

            var source = (Entity)tr.GetObject(id, OpenMode.ForRead);
            var created = (Entity)tr.GetObject(newId, OpenMode.ForWrite);
            if (string.IsNullOrEmpty(layer)) PolylineToolSupport.CopyEntityProperties(source, created);

            notes.Add($"{indices.Count} מקטעים הועתקו מהפוליליין {sourceHandle}");
            return ToolResult.Ok(new
            {
                mode = "clone_segments",
                new_handle = created.Handle.ToString(),
                source_handle = sourceHandle,
                segments_copied = indices.Count,
                vertex_count = points.Count,
                closed,
                notes,
            });
        }

        // ── helpers ──────────────────────────────────────────────────

        /// <summary>Explicit points from the parameters, as planar points.</summary>
        private static List<Pt2> ReadPoints(JsonElement parameters)
        {
            var result = new List<Pt2>();
            if (!parameters.TryGetProperty("points", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                double[] xy = new double[2];
                int k = 0;
                foreach (var component in item.EnumerateArray())
                {
                    if (k >= 2) break;
                    if (component.ValueKind == JsonValueKind.Number) xy[k++] = component.GetDouble();
                }
                if (k == 2) result.Add(new Pt2(xy[0], xy[1]));
            }
            return result;
        }

        /// <summary>
        /// Either takes the required number of points from the parameters, or prompts for them.
        /// </summary>
        private static bool TryGetPoints(
            JsonElement parameters,
            int required,
            string[] prompts,
            out List<Pt2> points,
            out bool cancelled,
            out string? error)
        {
            cancelled = false;
            error = null;
            points = ReadPoints(parameters);
            if (points.Count >= required)
            {
                points = points.GetRange(0, required);
                return true;
            }

            var doc = PolylineToolSupport.ActiveDocument;
            if (doc == null)
            {
                error = "אין שרטוט פעיל ולא סופקו נקודות";
                return false;
            }
            var ed = doc.Editor;

            points = new List<Pt2>();
            using var pickScope = new Utilities.InteractivePickScope();
            for (int i = 0; i < required; i++)
            {
                string prompt = i < prompts.Length ? prompts[i] : "לחצי על נקודה:";
                ToolUiNotifier.Step($"🖱️ {prompt}");

                var opts = new PromptPointOptions("\n" + prompt) { AllowNone = false };
                if (points.Count > 0)
                {
                    opts.UseBasePoint = true;
                    opts.BasePoint = new Point3d(points[^1].X, points[^1].Y, 0.0);
                }

                var res = ed.GetPoint(opts);
                if (res.Status != PromptStatus.OK)
                {
                    cancelled = true;
                    return false;
                }
                var world = PolylineToolSupport.UcsToWorld(ed, res.Value);
                points.Add(new Pt2(world.X, world.Y));
            }
            return true;
        }

        /// <summary>Samples any curve into a planar point chain.</summary>
        private static List<Pt2> SampleCurve(Curve curve, int steps)
        {
            var pts = new List<Pt2>();
            try
            {
                double start = curve.StartParam, end = curve.EndParam;
                for (int k = 0; k <= steps; k++)
                {
                    double param = start + (end - start) * k / steps;
                    var p = curve.GetPointAtParameter(param);
                    pts.Add(new Pt2(p.X, p.Y));
                }
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-edit] curve sample failed: {ex.Message}");
            }
            return pts;
        }
    }
}
