using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.SignsMarkings
{
    /// <summary>
    /// Adds a road marking (line, crosswalk, etc.) to the drawing.
    /// </summary>
    public class AddMarkingTool : DrawingToolBase
    {
        public override string Name => "add_marking";
        public override string Description => "Adds a road marking to the drawing. Creates a polyline on a marking layer along the specified alignment stations.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var markingType = GetRequiredStringParam(parameters, "marking_type");
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var startStation = GetDoubleParam(parameters, "start_station");
            var endStation = GetDoubleParam(parameters, "end_station");
            var side = GetStringParam(parameters, "side")?.ToLowerInvariant() ?? "center";
            var offset = GetDoubleParam(parameters, "offset") ?? 0.0;
            var pattern = GetStringParam(parameters, "pattern") ?? "solid";
            var color = GetStringParam(parameters, "color") ?? "white";
            var widthCm = GetDoubleParam(parameters, "width_cm") ?? 12.0;

            if (startStation == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'start_station' is missing");
            if (endStation == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'end_station' is missing");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment");

            var db = HostApplicationServices.WorkingDatabase;

            // Determine layer name based on marking type
            string layerName = $"MARKING_{markingType.ToUpperInvariant()}";

            // Ensure the layer exists
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var layer = new LayerTableRecord
                {
                    Name = layerName,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                        (short)(color == "yellow" ? 2 : 7))
                };
                lt.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
            }

            // Calculate offset based on side
            double sideOffset = offset;
            if (side == "left" && offset == 0)
                sideOffset = -3.5; // Default lane offset
            else if (side == "right" && offset == 0)
                sideOffset = 3.5;

            // Sample points along alignment to create polyline
            double stationStep = 2.0; // Sample every 2m
            var points = new Point3dCollection();

            try
            {
                for (double sta = startStation.Value; sta <= endStation.Value; sta += stationStep)
                {
                    ct.ThrowIfCancellationRequested();

                    double easting = 0, northing = 0;
                    alignment.PointLocation(sta, sideOffset, ref easting, ref northing);
                    points.Add(new Point3d(easting, northing, 0));
                }

                // Ensure end station is included
                if (points.Count > 0)
                {
                    double easting = 0, northing = 0;
                    alignment.PointLocation(endStation.Value, sideOffset, ref easting, ref northing);
                    points.Add(new Point3d(easting, northing, 0));
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to sample points along alignment: {ex.Message}");
            }

            if (points.Count < 2)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Insufficient points to create marking. Check station range.");

            // Create polyline
            var pl = new Polyline();
            for (int i = 0; i < points.Count; i++)
            {
                pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
            }
            pl.Layer = layerName;
            pl.ConstantWidth = widthCm / 100.0; // Convert cm to m

            // Set linetype for pattern
            if (pattern == "dashed")
            {
                try
                {
                    var ltypeTbl = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (ltypeTbl.Has("DASHED"))
                        pl.LinetypeId = ltypeTbl["DASHED"];
                    else if (ltypeTbl.Has("HIDDEN"))
                        pl.LinetypeId = ltypeTbl["HIDDEN"];
                }
                catch { }
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            cache.RemoveByPattern("list_markings:");
            cache.RemoveByPattern("validate_markings:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                marking_type = markingType,
                alignment_name = alignmentName,
                start_station = Math.Round(startStation.Value, 3),
                end_station = Math.Round(endStation.Value, 3),
                side,
                offset = Math.Round(sideOffset, 3),
                pattern,
                color,
                width_cm = widthCm,
                layer = layerName,
                point_count = points.Count
            }));
        }
    }
}
