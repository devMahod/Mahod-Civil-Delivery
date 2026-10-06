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
    /// Places a new road sign block reference in the drawing at a specified location.
    /// </summary>
    public class AddSignTool : DrawingToolBase
    {
        public override string Name => "add_sign";
        public override string Description => "Places a new road sign in the drawing. Inserts a block reference for the sign at the specified station and side of an alignment, or at explicit X,Y coordinates.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var signCode = GetRequiredStringParam(parameters, "sign_code");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var station = GetDoubleParam(parameters, "station");
            var side = GetStringParam(parameters, "side")?.ToLowerInvariant() ?? "right";
            var offset = GetDoubleParam(parameters, "offset") ?? 5.0; // Default 5m offset from alignment
            var x = GetDoubleParam(parameters, "x");
            var y = GetDoubleParam(parameters, "y");
            var layerName = GetStringParam(parameters, "layer_name") ?? "SIGN";

            // Must have either alignment+station or x+y
            if (alignmentName == null && (!x.HasValue || !y.HasValue))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Must provide either 'alignment_name' + 'station' or 'x' + 'y' coordinates");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var db = HostApplicationServices.WorkingDatabase;

            double insertX, insertY;
            double rotation = 0;

            // Calculate insertion point from alignment station
            if (alignmentName != null && station.HasValue)
            {
                var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
                if (alignmentId == null)
                    return ToolResult.NotFound("Alignment", alignmentName);

                var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
                if (alignment == null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment");

                try
                {
                    double easting = 0, northing = 0;
                    alignment.PointLocation(station.Value, 0, ref easting, ref northing);

                    // Get direction at station for rotation
                    double easting2 = 0, northing2 = 0;
                    double stationOffset = Math.Min(1.0, alignment.Length * 0.001);
                    alignment.PointLocation(station.Value + stationOffset, 0, ref easting2, ref northing2);
                    rotation = Math.Atan2(northing2 - northing, easting2 - easting);

                    // Apply offset (positive = right side)
                    double perpAngle = rotation + (side == "left" ? Math.PI / 2 : -Math.PI / 2);
                    insertX = easting + offset * Math.Cos(perpAngle);
                    insertY = northing + offset * Math.Sin(perpAngle);
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Failed to calculate position from alignment: {ex.Message}");
                }
            }
            else
            {
                insertX = x!.Value;
                insertY = y!.Value;
            }

            // Create or find the sign block
            string blockName = $"SIGN_{signCode}";
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            // If the block definition doesn't exist, create a simple placeholder
            ObjectId blockDefId;
            if (!bt.Has(blockName))
            {
                // Create a simple block definition as placeholder
                var btr = new BlockTableRecord { Name = blockName };
                bt.UpgradeOpen();
                blockDefId = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);

                // Add a simple circle as the sign symbol
                var circle = new Circle(Point3d.Origin, Vector3d.ZAxis, 0.3);
                btr.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
            }
            else
            {
                blockDefId = bt[blockName];
            }

            // Ensure the layer exists
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var layer = new LayerTableRecord { Name = layerName };
                lt.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
            }

            // Insert block reference
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var blkRef = new BlockReference(new Point3d(insertX, insertY, 0), blockDefId)
            {
                Layer = layerName,
                Rotation = rotation
            };

            ms.AppendEntity(blkRef);
            tr.AddNewlyCreatedDBObject(blkRef, true);

            cache.RemoveByPattern("list_signs:");
            cache.RemoveByPattern("validate_signs:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                sign_code = signCode,
                block_name = blockName,
                x = Math.Round(insertX, 3),
                y = Math.Round(insertY, 3),
                rotation_degrees = Math.Round(rotation * 180.0 / Math.PI, 1),
                alignment_name = alignmentName,
                station = station.HasValue ? Math.Round(station.Value, 3) : (double?)null,
                side,
                layer = layerName
            }));
        }
    }
}
