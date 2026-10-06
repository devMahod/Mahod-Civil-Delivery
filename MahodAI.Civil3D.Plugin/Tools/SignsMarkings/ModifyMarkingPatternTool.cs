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
    /// Changes the pattern (solid, dashed, etc.) of existing road markings.
    /// </summary>
    public class ModifyMarkingPatternTool : DrawingToolBase
    {
        public override string Name => "modify_marking_pattern";
        public override string Description => "Changes the line pattern of road markings (e.g., from solid to dashed). Modifies the linetype of marking entities matching the specified criteria.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

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
            var newPattern = GetRequiredStringParam(parameters, "new_pattern").ToLowerInvariant();
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var startStation = GetDoubleParam(parameters, "start_station");
            var endStation = GetDoubleParam(parameters, "end_station");
            var markingType = GetStringParam(parameters, "marking_type");
            var layerFilter = GetStringParam(parameters, "layer_name");
            var stationTolerance = GetDoubleParam(parameters, "station_tolerance") ?? 5.0;

            if (newPattern != "solid" && newPattern != "dashed" && newPattern != "double_solid" && newPattern != "double_dashed")
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Parameter 'new_pattern' must be one of: solid, dashed, double_solid, double_dashed");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var db = HostApplicationServices.WorkingDatabase;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            // Resolve the target linetype
            var ltypeTbl = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            ObjectId targetLinetypeId = db.ContinuousLinetype; // Default: solid

            if (newPattern == "dashed" || newPattern == "double_dashed")
            {
                if (ltypeTbl.Has("DASHED"))
                    targetLinetypeId = ltypeTbl["DASHED"];
                else if (ltypeTbl.Has("HIDDEN"))
                    targetLinetypeId = ltypeTbl["HIDDEN"];
            }

            // Get alignment for station filtering
            CivilDb.Alignment? filterAlignment = null;
            if (alignmentName != null)
            {
                var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
                if (alignmentId != null)
                    filterAlignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            }

            int modifiedCount = 0;

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

                // Check marking type filter
                if (markingType != null)
                {
                    string upperLayer = entLayer.ToUpperInvariant();
                    if (!upperLayer.Contains(markingType.ToUpperInvariant()))
                        continue;
                }

                // Only modify line-type entities (polylines, lines)
                if (!(ent is Polyline) && !(ent is Line))
                    continue;

                // Check station range filter
                if (filterAlignment != null && startStation.HasValue && endStation.HasValue)
                {
                    bool withinRange = false;
                    try
                    {
                        double px, py;
                        if (ent is Polyline pl && pl.NumberOfVertices >= 1)
                        {
                            var pt = pl.GetPoint3dAt(0);
                            px = pt.X; py = pt.Y;
                        }
                        else if (ent is Line line)
                        {
                            px = line.StartPoint.X; py = line.StartPoint.Y;
                        }
                        else continue;

                        double sta = 0, off = 0;
                        filterAlignment.StationOffset(px, py, ref sta, ref off);
                        if (sta >= startStation.Value - stationTolerance &&
                            sta <= endStation.Value + stationTolerance &&
                            Math.Abs(off) < 30)
                            withinRange = true;
                    }
                    catch { }

                    if (!withinRange)
                        continue;
                }

                // Modify the linetype
                var entWrite = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                if (entWrite != null)
                {
                    entWrite.LinetypeId = targetLinetypeId;
                    modifiedCount++;
                }
            }

            if (modifiedCount == 0)
                return ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                    "No markings found matching the specified criteria");

            cache.RemoveByPattern("list_markings:");
            cache.RemoveByPattern("validate_markings:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                new_pattern = newPattern,
                alignment_name = alignmentName,
                marking_type = markingType,
                modified_count = modifiedCount
            }));
        }
    }
}
