using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.SignsMarkings
{
    /// <summary>
    /// Removes a road marking from the drawing.
    /// </summary>
    public class RemoveMarkingTool : DrawingToolBase
    {
        public override string Name => "remove_marking";
        public override string Description => "Removes road marking entities from the drawing. Finds and erases markings by type, alignment, and station range.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        // Layer name patterns for marking identification
        private static readonly string[] MarkingLayerPatterns = new[]
        {
            "MARK", "סימון", "STRIPE", "LINE_MARK", "ROAD_MARK",
            "CROSSWALK", "מעבר", "ARROW", "חץ", "PAVEMENT"
        };

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var markingType = GetStringParam(parameters, "marking_type");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var startStation = GetDoubleParam(parameters, "start_station");
            var endStation = GetDoubleParam(parameters, "end_station");
            var layerFilter = GetStringParam(parameters, "layer_name");
            var stationTolerance = GetDoubleParam(parameters, "station_tolerance") ?? 5.0;

            if (markingType == null && layerFilter == null && alignmentName == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Must provide at least one filter: 'marking_type', 'layer_name', or 'alignment_name' with station range");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var db = HostApplicationServices.WorkingDatabase;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            // Get alignment for station filtering
            CivilDb.Alignment? filterAlignment = null;
            if (alignmentName != null)
            {
                var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
                if (alignmentId != null)
                    filterAlignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            }

            int removedCount = 0;
            var removedItems = new List<object>();

            foreach (ObjectId id in ms)
            {
                ct.ThrowIfCancellationRequested();

                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                string entLayer = ent.Layer ?? "";

                // Check if entity is on a marking layer
                bool isMarkingLayer = false;
                if (layerFilter != null)
                {
                    isMarkingLayer = entLayer.Equals(layerFilter, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    string upperLayer = entLayer.ToUpperInvariant();
                    foreach (var pattern in MarkingLayerPatterns)
                    {
                        if (upperLayer.Contains(pattern.ToUpperInvariant()))
                        {
                            isMarkingLayer = true;
                            break;
                        }
                    }
                }

                if (!isMarkingLayer)
                    continue;

                // Check marking type filter via layer name
                if (markingType != null)
                {
                    string upperLayer = entLayer.ToUpperInvariant();
                    string upperType = markingType.ToUpperInvariant();
                    if (!upperLayer.Contains(upperType))
                        continue;
                }

                // Check station range filter
                if (filterAlignment != null && startStation.HasValue && endStation.HasValue)
                {
                    bool withinRange = false;

                    try
                    {
                        if (ent is Polyline pl && pl.NumberOfVertices >= 1)
                        {
                            var pt = pl.GetPoint3dAt(0);
                            double sta = 0, off = 0;
                            filterAlignment.StationOffset(pt.X, pt.Y, ref sta, ref off);
                            if (sta >= startStation.Value - stationTolerance &&
                                sta <= endStation.Value + stationTolerance &&
                                Math.Abs(off) < 30)
                                withinRange = true;
                        }
                        else if (ent is Line line)
                        {
                            double sta = 0, off = 0;
                            filterAlignment.StationOffset(line.StartPoint.X, line.StartPoint.Y, ref sta, ref off);
                            if (sta >= startStation.Value - stationTolerance &&
                                sta <= endStation.Value + stationTolerance &&
                                Math.Abs(off) < 30)
                                withinRange = true;
                        }
                        else if (ent is BlockReference blkRef)
                        {
                            double sta = 0, off = 0;
                            filterAlignment.StationOffset(blkRef.Position.X, blkRef.Position.Y, ref sta, ref off);
                            if (sta >= startStation.Value - stationTolerance &&
                                sta <= endStation.Value + stationTolerance &&
                                Math.Abs(off) < 30)
                                withinRange = true;
                        }
                    }
                    catch { }

                    if (!withinRange)
                        continue;
                }

                // Erase the entity
                var entWrite = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                if (entWrite != null)
                {
                    removedItems.Add(new
                    {
                        entity_type = ent.GetType().Name,
                        layer = entLayer
                    });
                    entWrite.Erase();
                    removedCount++;
                }
            }

            if (removedCount == 0)
                return ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                    "No markings found matching the specified criteria");

            cache.RemoveByPattern("list_markings:");
            cache.RemoveByPattern("validate_markings:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                marking_type = markingType,
                alignment_name = alignmentName,
                removed_count = removedCount,
                removed_items = removedItems
            }));
        }
    }
}
