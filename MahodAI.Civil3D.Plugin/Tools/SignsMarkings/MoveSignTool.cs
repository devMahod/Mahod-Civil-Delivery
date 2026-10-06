using System;
using System.Collections.Generic;
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
    /// Relocates a road sign to a new position.
    /// </summary>
    public class MoveSignTool : DrawingToolBase
    {
        public override string Name => "move_sign";
        public override string Description => "Moves (relocates) a road sign to a new position. Can move to a new station on the same alignment or to explicit X,Y coordinates.";
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
            var currentStation = GetDoubleParam(parameters, "current_station");
            var newStation = GetDoubleParam(parameters, "new_station");
            var newX = GetDoubleParam(parameters, "new_x");
            var newY = GetDoubleParam(parameters, "new_y");
            var side = GetStringParam(parameters, "side")?.ToLowerInvariant() ?? "right";
            var offset = GetDoubleParam(parameters, "offset") ?? 5.0;
            var stationTolerance = GetDoubleParam(parameters, "station_tolerance") ?? 5.0;

            if (newStation == null && (!newX.HasValue || !newY.HasValue))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Must provide either 'new_station' or 'new_x' + 'new_y' for the target position");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var db = HostApplicationServices.WorkingDatabase;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            // Get alignments
            var alignments = new List<CivilDb.Alignment>();
            CivilDb.Alignment? targetAlignment = null;
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment a)
                {
                    alignments.Add(a);
                    if (alignmentName != null && a.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
                        targetAlignment = a;
                }
            }

            // Find the sign to move
            BlockReference? targetBlkRef = null;
            ObjectId targetId = ObjectId.Null;

            foreach (ObjectId id in ms)
            {
                ct.ThrowIfCancellationRequested();

                if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference blkRef))
                    continue;

                string blockName = blkRef.Name ?? "";
                if (!blockName.Contains(signCode, StringComparison.OrdinalIgnoreCase) &&
                    !HasSignCode(tr, blkRef, signCode))
                    continue;

                // If current_station is specified, filter by station
                if (currentStation.HasValue && targetAlignment != null)
                {
                    try
                    {
                        double sta = 0, off = 0;
                        targetAlignment.StationOffset(blkRef.Position.X, blkRef.Position.Y, ref sta, ref off);
                        if (Math.Abs(sta - currentStation.Value) > stationTolerance || Math.Abs(off) > 50)
                            continue;
                    }
                    catch { continue; }
                }

                targetBlkRef = blkRef;
                targetId = id;
                break;
            }

            if (targetBlkRef == null)
                return ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                    $"No sign with code '{signCode}' found matching the specified criteria");

            double oldX = targetBlkRef.Position.X;
            double oldY = targetBlkRef.Position.Y;

            // Calculate new position
            double moveToX, moveToY;
            double rotation = targetBlkRef.Rotation;

            if (newStation.HasValue && targetAlignment != null)
            {
                try
                {
                    double easting = 0, northing = 0;
                    targetAlignment.PointLocation(newStation.Value, 0, ref easting, ref northing);

                    double easting2 = 0, northing2 = 0;
                    double stationDelta = Math.Min(1.0, targetAlignment.Length * 0.001);
                    targetAlignment.PointLocation(newStation.Value + stationDelta, 0, ref easting2, ref northing2);
                    rotation = Math.Atan2(northing2 - northing, easting2 - easting);

                    double perpAngle = rotation + (side == "left" ? Math.PI / 2 : -Math.PI / 2);
                    moveToX = easting + offset * Math.Cos(perpAngle);
                    moveToY = northing + offset * Math.Sin(perpAngle);
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Failed to calculate position from alignment: {ex.Message}");
                }
            }
            else
            {
                moveToX = newX!.Value;
                moveToY = newY!.Value;
            }

            // Move the sign
            var blkRefWrite = tr.GetObject(targetId, OpenMode.ForWrite) as BlockReference;
            if (blkRefWrite == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open sign for write");

            blkRefWrite.Position = new Point3d(moveToX, moveToY, 0);
            blkRefWrite.Rotation = rotation;

            cache.RemoveByPattern("list_signs:");
            cache.RemoveByPattern("validate_signs:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                sign_code = signCode,
                old_x = Math.Round(oldX, 3),
                old_y = Math.Round(oldY, 3),
                new_x = Math.Round(moveToX, 3),
                new_y = Math.Round(moveToY, 3),
                alignment_name = alignmentName,
                new_station = newStation.HasValue ? Math.Round(newStation.Value, 3) : (double?)null,
                side
            }));
        }

        private bool HasSignCode(Transaction tr, BlockReference blkRef, string signCode)
        {
            try
            {
                if (blkRef.AttributeCollection != null)
                {
                    foreach (ObjectId attrId in blkRef.AttributeCollection)
                    {
                        if (tr.GetObject(attrId, OpenMode.ForRead) is AttributeReference attr)
                        {
                            string tag = attr.Tag?.ToUpperInvariant() ?? "";
                            if ((tag.Contains("CODE") || tag.Contains("קוד") || tag.Contains("NUM")) &&
                                attr.TextString == signCode)
                                return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
