using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Creates slope/daylight lines from corridor cross-sections for visualization.
    /// Inspired by Igor's CreateSlopes.cs — extracts cut/fill slope boundaries.
    ///
    /// Constants from Igor:
    /// - csMinSlope = 0.12 (12%) — minimum slope to detect
    /// - csMinSlopeWidth = 0.2m — minimum width filter
    /// </summary>
    public class CreateSlopesTool : DrawingToolBase
    {
        public override string Name => "create_slopes";
        public override string Description =>
            "Creates slope/daylight lines from corridor showing cut and fill boundaries. " +
            "Draws polylines along the corridor edges where earthwork transitions occur.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

        private const double MIN_SLOPE = 0.12;
        private const double MIN_WIDTH = 0.2;
        private const string CUT_LAYER = "C-ROAD-SLOPE-CUT";
        private const string FILL_LAYER = "C-ROAD-SLOPE-FILL";
        private const short CUT_COLOR = 1;  // Red
        private const short FILL_COLOR = 3; // Green

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""corridor_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the corridor to extract slopes from""
                },
                ""side"": {
                    ""type"": ""string"",
                    ""enum"": [""both"", ""left"", ""right""],
                    ""description"": ""Which side to process (default: both)""
                }
            },
            ""required"": [""corridor_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var side = GetStringParam(parameters, "side") ?? "both";

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find corridor
            CivilDb.Corridor? corridor = null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Corridor;
                if (c != null && c.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridor = c;
                    break;
                }
            }

            if (corridor == null)
                return ToolResult.NotFound("Corridor", corridorName);

            var db = HostApplicationServices.WorkingDatabase;
            ObjectId cutLayerId = GetOrCreateLayer(tr, db, CUT_LAYER, CUT_COLOR);
            ObjectId fillLayerId = GetOrCreateLayer(tr, db, FILL_LAYER, FILL_COLOR);

            var cutPoints = new List<Point3d>();
            var fillPoints = new List<Point3d>();
            int slopeCount = 0;

            // Process each baseline
            foreach (Baseline baseline in corridor.Baselines)
            {
                ct.ThrowIfCancellationRequested();

                foreach (BaselineRegion region in baseline.BaselineRegions)
                {
                    var assemblies = region.AppliedAssemblies;
                    if (assemblies == null || assemblies.Count == 0) continue;

                    foreach (AppliedAssembly asm in assemblies)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Get subassemblies via reflection
                        var appliedSubs = GetAppliedSubassemblies(asm);
                        if (appliedSubs == null) continue;

                        foreach (var appliedSub in appliedSubs)
                        {
                            // Get origin offset to determine side
                            double offset = GetSubassemblyOffset(appliedSub);
                            bool isLeft = offset < 0;

                            if (side == "left" && !isLeft) continue;
                            if (side == "right" && isLeft) continue;

                            // Get links via reflection
                            var links = GetLinks(appliedSub);
                            if (links == null) continue;

                            foreach (var link in links)
                            {
                                var points = GetLinkPoints(link);
                                if (points.Count < 2) continue;

                                var first = points[0];
                                var last = points[points.Count - 1];

                                double hDist = Math.Sqrt(
                                    Math.Pow(last.X - first.X, 2) +
                                    Math.Pow(last.Y - first.Y, 2));

                                if (hDist < MIN_WIDTH) continue;

                                double dy = last.Z - first.Z;
                                double slope = Math.Abs(dy / Math.Max(hDist, 0.001));

                                if (slope < MIN_SLOPE) continue;

                                slopeCount++;
                                bool isCut = dy < 0;

                                // Collect the outermost point of the slope
                                if (isCut) cutPoints.Add(last);
                                else fillPoints.Add(last);
                            }
                        }
                    }
                }
            }

            // Create polylines from collected points
            var btr = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
            int cutLines = 0, fillLines = 0;
            if (btr != null)
            {
                cutLines = CreatePolylineFromPoints(tr, btr, cutPoints, cutLayerId);
                fillLines = CreatePolylineFromPoints(tr, btr, fillPoints, fillLayerId);
            }

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["corridor_name"] = corridorName,
                ["slope_segments_found"] = slopeCount,
                ["cut_lines"] = cutLines,
                ["fill_lines"] = fillLines,
                ["cut_layer"] = CUT_LAYER,
                ["fill_layer"] = FILL_LAYER,
                ["message"] = $"Found {slopeCount} slope segments. Created {cutLines} cut (red) and " +
                    $"{fillLines} fill (green) slope lines for corridor '{corridorName}'."
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        // ── Reflection helpers (Civil 3D API varies by version) ──

        private static IEnumerable? GetAppliedSubassemblies(AppliedAssembly asm)
        {
            // Try method first (Civil 3D 2026+)
            var method = asm.GetType().GetMethod("GetAppliedSubassemblies");
            if (method != null)
            {
                var result = method.Invoke(asm, null) as IEnumerable;
                if (result != null) return result;
            }
            // Fallback: property
            var prop = asm.GetType().GetProperty("AppliedSubassemblies");
            return prop?.GetValue(asm) as IEnumerable;
        }

        private static double GetSubassemblyOffset(object appliedSub)
        {
            try
            {
                var originProp = appliedSub.GetType().GetProperty("OriginStationOffsetElevationToBaseline");
                if (originProp != null)
                {
                    var origin = originProp.GetValue(appliedSub);
                    if (origin != null)
                    {
                        var yProp = origin.GetType().GetProperty("Y");
                        if (yProp != null) return Convert.ToDouble(yProp.GetValue(origin));
                    }
                }
            }
            catch { }
            return 0;
        }

        private static IEnumerable? GetLinks(object appliedSub)
        {
            var prop = appliedSub.GetType().GetProperty("Links")
                     ?? appliedSub.GetType().GetProperty("CalculatedLinks");
            if (prop != null)
                return prop.GetValue(appliedSub) as IEnumerable;

            var method = appliedSub.GetType().GetMethod("GetLinks")
                       ?? appliedSub.GetType().GetMethod("GetCalculatedLinks");
            return method?.Invoke(appliedSub, null) as IEnumerable;
        }

        private static List<Point3d> GetLinkPoints(object link)
        {
            var points = new List<Point3d>();
            var pointsProp = link.GetType().GetProperty("CalculatedPoints")
                           ?? link.GetType().GetProperty("Points");
            if (pointsProp == null) return points;

            var raw = pointsProp.GetValue(link);
            if (raw is IEnumerable enumerable)
            {
                foreach (var pt in enumerable)
                {
                    var xyzProp = pt.GetType().GetProperty("XYZ");
                    if (xyzProp != null)
                    {
                        var xyz = xyzProp.GetValue(pt);
                        if (xyz is Point3d p3d) points.Add(p3d);
                    }
                }
            }
            return points;
        }

        private static int CreatePolylineFromPoints(
            Transaction tr, BlockTableRecord btr, List<Point3d> points, ObjectId layerId)
        {
            if (points.Count < 2) return 0;

            // Keep original station order (chainage order) — do NOT sort by X/Y
            // as that breaks curved/north-south alignments
            var sorted = points;

            var poly = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(), false);
            poly.LayerId = layerId;
            btr.AppendEntity(poly);
            tr.AddNewlyCreatedDBObject(poly, true);

            foreach (var pt in sorted)
            {
                var vertex = new PolylineVertex3d(pt);
                poly.AppendVertex(vertex);
                tr.AddNewlyCreatedDBObject(vertex, true);
            }

            return 1;
        }

        private static ObjectId GetOrCreateLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            try
            {
                var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (lt == null) return db.Clayer;
                if (lt.Has(name)) return lt[name];

                var ltr = new LayerTableRecord
                {
                    Name = name,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
                };
                var ltWrite = tr.GetObject(db.LayerTableId, OpenMode.ForWrite) as LayerTable;
                if (ltWrite != null)
                {
                    ObjectId layerId = ltWrite.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    return layerId;
                }
            }
            catch { }
            return db.Clayer;
        }
    }
}
