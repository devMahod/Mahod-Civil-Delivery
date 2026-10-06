using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// PLTOOLS <c>ConvTo2d</c> / <c>ConvTo3d</c> / <c>PL-3d2d</c>: converts linework between a 2D
    /// (lightweight) polyline and a 3D polyline, and converts plain LINE/ARC/CIRCLE/ELLIPSE/SPLINE
    /// into polylines the way the originals do.
    ///
    /// Two lossy directions, both reported instead of hidden:
    ///   • → 3D polyline: a Polyline3d has no bulges, so arc segments must become straights. They
    ///     are flattened to <c>arc_chord_deviation</c> and the result says how many arcs it hit.
    ///   • → 2D polyline: every vertex collapses onto ONE elevation (PLTOOLS uses the first
    ///     vertex's Z; <c>elevation</c> overrides it), and the Z range that was lost is reported.
    /// </summary>
    public class ConvertPolylineTypeTool : DrawingToolBase
    {
        public override string Name => "convert_polyline_type";

        public override string Description =>
            "Converts polylines and curves between types. to='polyline3d' makes a 3D polyline (arcs " +
            "become straights within arc_chord_deviation); to='polyline2d' makes a flat lightweight " +
            "polyline at one elevation (from `elevation`, otherwise the first vertex's Z) and reports " +
            "the Z range that was flattened away. Accepts LWPOLYLINE, 2D/3D POLYLINE, LINE, ARC, " +
            "CIRCLE, ELLIPSE and SPLINE. delete_source (default true) erases the original.";

        public override string Category => ToolCategories.Modification;

        /// <summary>Long enough for the engineer to select the objects when none were named.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Prompts for a selection when the agent named no target.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"" },
                ""entity_handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                ""layer"": { ""type"": ""string"", ""description"": ""Convert every polyline on this layer."" },
                ""to"": { ""type"": ""string"", ""enum"": [""polyline2d"", ""polyline3d""] },
                ""elevation"": { ""type"": ""number"", ""description"": ""to='polyline2d': the single elevation to use. Default = first vertex Z."" },
                ""arc_chord_deviation"": { ""type"": ""number"", ""description"": ""to='polyline3d': max deviation when flattening arcs into straights. Default 0.05."" },
                ""curve_sample_count"": { ""type"": ""integer"", ""description"": ""Sample count for non-polyline curves (circle/ellipse/spline). Default 64."" },
                ""delete_source"": { ""type"": ""boolean"", ""description"": ""Default true."" }
            },
            ""required"": [""to""]
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

            var to = (GetStringParam(parameters, "to") ?? string.Empty).Trim().ToLowerInvariant();
            if (to is not ("polyline2d" or "polyline3d"))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"to לא נתמך: '{to}'. אפשרויות: polyline2d, polyline3d"));

            bool deleteSource = GetBoolParam(parameters, "delete_source", true);
            double chordDeviation = GetDoubleParam(parameters, "arc_chord_deviation") ?? 0.05;
            int sampleCount = GetIntParam(parameters, "curve_sample_count") ?? 64;
            if (sampleCount < 4) sampleCount = 4;
            double? forcedElevation = GetDoubleParam(parameters, "elevation");

            var ids = PolylineToolSupport.ResolveTargetsOrPrompt(
                tr, db, parameters, "בחרי אובייקטים להמרה (Enter לסיום):",
                out var unresolved, out var targetSource);
            if (ids.Count == 0)
            {
                return Task.FromResult(targetSource == PolylineToolSupport.TargetSource.Cancelled
                    ? ToolResult.Cancelled(new { cancelled = true, converted = 0 })
                    : ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                        "לא נמצאו אובייקטים להמרה בשרטוט"));
            }

            var results = new List<object>();
            var warnings = new List<string>();
            foreach (var h in unresolved) warnings.Add($"handle לא נמצא בשרטוט: {h}");

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                string sourceHandle = PolylineToolSupport.HandleOf(tr, id);

                if (!TryReadAsPoints(tr, id, chordDeviation, sampleCount,
                        out var points, out bool closed, out var bulges, out var notes, out var readErr))
                {
                    warnings.Add($"{sourceHandle}: {readErr}");
                    continue;
                }

                if (points.Count < 2)
                {
                    warnings.Add($"{sourceHandle}: פחות משתי נקודות — אין מה להמיר");
                    continue;
                }

                var source = (Entity)tr.GetObject(id, OpenMode.ForRead);
                ObjectId newId;

                if (to == "polyline3d")
                {
                    if (bulges != null)
                    {
                        // A 3D polyline cannot hold bulges — the arcs were already flattened in
                        // TryReadAsPoints; say so rather than losing them quietly.
                        bulges = null;
                    }
                    newId = PolylineToolSupport.CreatePolyline3d(
                        tr, db, points.ConvertAll(p => new Point3d(p.X, p.Y, p.Z)), closed, null);
                }
                else
                {
                    double elevation = forcedElevation ?? points[0].Z;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    foreach (var p in points)
                    {
                        minZ = Math.Min(minZ, p.Z);
                        maxZ = Math.Max(maxZ, p.Z);
                    }
                    if (maxZ - minZ > 1e-9)
                        notes.Add($"טווח גבהים {Math.Round(minZ, 3)}–{Math.Round(maxZ, 3)} שוטח לגובה {Math.Round(elevation, 3)}");

                    newId = PolylineToolSupport.CreatePolyline(
                        tr, db, points.ConvertAll(p => new Pt2(p.X, p.Y)), closed, elevation, null, bulges);
                }

                var created = (Entity)tr.GetObject(newId, OpenMode.ForWrite);
                PolylineToolSupport.CopyEntityProperties(source, created);

                if (deleteSource)
                {
                    var writable = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    writable.Erase();
                }

                results.Add(new
                {
                    source_handle = sourceHandle,
                    source_type = PolylineToolSupport.HebrewTypeName(source),
                    new_handle = created.Handle.ToString(),
                    new_type = to,
                    vertex_count = points.Count,
                    closed,
                    source_deleted = deleteSource,
                    notes,
                });
            }

            if (results.Count == 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "אף אובייקט לא הומר — " + string.Join("; ", warnings)));

            return Task.FromResult(ToolResult.Ok(new
            {
                to,
                converted = results.Count,
                delete_source = deleteSource,
                results,
                warnings,
            }));
        }

        /// <summary>
        /// Reads any supported entity as a 3D point list (plus bulges when they survive the target
        /// type). Polylines come through the adapter exactly; other curves are sampled.
        /// </summary>
        private static bool TryReadAsPoints(
            Transaction tr,
            ObjectId id,
            double chordDeviation,
            int sampleCount,
            out List<Point3d> points,
            out bool closed,
            out List<double>? bulges,
            out List<string> notes,
            out string? error)
        {
            points = new List<Point3d>();
            bulges = null;
            notes = new List<string>();
            closed = false;
            error = null;

            var obj = tr.GetObject(id, OpenMode.ForRead);

            if (obj is Polyline or Polyline2d or Polyline3d)
            {
                if (!PolylineAdapter.TryOpen(tr, id, out var target, out var code, out var msg))
                {
                    error = $"{msg} [{code}]";
                    return false;
                }

                var shape = target!.Shape;
                closed = shape.Closed;

                // Arcs cannot survive as bulges in a 3D polyline: flatten them here so the vertex
                // list itself carries the curve.
                if (shape.HasArcs)
                {
                    int arcs = 0;
                    for (int i = 0; i < shape.Count; i++)
                        if (BulgeMath.IsArc(shape.Vertices[i].Bulge)) arcs++;

                    var flattened = shape.Clone();
                    SegmentEditor.FlattenArcs(flattened, BulgeMath.FlattenMode.ChordDeviation, chordDeviation);
                    notes.Add($"{arcs} קטעי קשת שוטחו לקווים ישרים בסטייה של עד {chordDeviation}");
                    shape = flattened;
                }
                else
                {
                    bulges = new List<double>(shape.Count);
                    for (int i = 0; i < shape.Count; i++) bulges.Add(shape.Vertices[i].Bulge);
                }

                foreach (var v in shape.Vertices)
                {
                    var world = target.ToWorld(v.P, v.Z);
                    points.Add(world);
                }
                return true;
            }

            if (obj is Curve curve)
            {
                try
                {
                    closed = curve.Closed;
                    double startParam = curve.StartParam;
                    double endParam = curve.EndParam;
                    int steps = Math.Max(4, sampleCount);
                    for (int k = 0; k <= steps; k++)
                    {
                        if (closed && k == steps) break;      // the closing point is implicit
                        double param = startParam + (endParam - startParam) * k / steps;
                        points.Add(curve.GetPointAtParameter(param));
                    }
                    notes.Add($"עקומה נדגמה ל-{points.Count} נקודות");
                    return points.Count >= 2;
                }
                catch (Exception ex)
                {
                    error = $"דגימת העקומה נכשלה: {ex.Message}";
                    return false;
                }
            }

            error = $"סוג האובייקט {obj.GetType().Name} אינו נתמך להמרה";
            return false;
        }
    }
}
