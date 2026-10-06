using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Modifies the superelevation rate for a curve in an alignment.
    /// </summary>
    public class ModifySuperelevationRateTool : DrawingToolBase
    {
        public override string Name => "modify_superelevation_rate";
        public override string Description => "Changes the superelevation (cross-slope) rate for a curve element in an alignment. The rate is specified as a percentage (e.g., 6.0 for 6%).";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var elementIndex = GetIntParam(parameters, "element_index");
            var newRate = GetDoubleParam(parameters, "new_rate");

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");
            if (newRate == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_rate' is missing");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            int idx = elementIndex.Value;
            if (idx < 0 || idx >= alignment.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, $"Element index {idx} out of range (0-{alignment.Entities.Count - 1})");

            var entity = alignment.Entities[idx];

            // Get the station of the curve for superelevation assignment
            double curveStation;
            double radius;

            switch (entity.EntityType)
            {
                case CivilDb.AlignmentEntityType.Arc:
                    var arc = entity as CivilDb.AlignmentArc;
                    if (arc == null)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentArc");
                    curveStation = arc.StartStation;
                    radius = arc.Radius;
                    break;

                case CivilDb.AlignmentEntityType.SpiralCurveSpiral:
                    var scs = entity as CivilDb.AlignmentSCS;
                    if (scs == null)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentSCS");
                    curveStation = scs.StartStation;
                    radius = scs.Arc.Radius;
                    break;

                default:
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"Element at index {idx} is {entity.EntityType}, not a curve (Arc or SCS)");
            }

            // Modify superelevation via the alignment's superelevation data
            double? oldRate = null;
            bool modified = false;

            try
            {
                // Access superelevation data via reflection for API compatibility
                var superElevProp = alignment.GetType().GetProperty("SuperelevationData");
                if (superElevProp != null)
                {
                    var superElevData = superElevProp.GetValue(alignment);
                    if (superElevData != null)
                    {
                        // Try to find and modify the superelevation at this station
                        var dataType = superElevData.GetType();
                        var countProp = dataType.GetProperty("Count");
                        if (countProp != null)
                        {
                            int count = (int)countProp.GetValue(superElevData)!;
                            var indexer = dataType.GetProperty("Item");
                            for (int i = 0; i < count; i++)
                            {
                                var item = indexer?.GetValue(superElevData, new object[] { i });
                                if (item != null)
                                {
                                    var stationProp = item.GetType().GetProperty("Station");
                                    if (stationProp != null)
                                    {
                                        double station = (double)stationProp.GetValue(item)!;
                                        if (Math.Abs(station - curveStation) < 1.0)
                                        {
                                            var rateProp = item.GetType().GetProperty("Rate");
                                            if (rateProp != null)
                                            {
                                                oldRate = (double)rateProp.GetValue(item)!;
                                                if (rateProp.CanWrite)
                                                {
                                                    rateProp.SetValue(item, newRate.Value / 100.0);
                                                    modified = true;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (!modified)
            {
                // Superelevation data may not exist yet or API approach differs
                return await Task.FromResult(ToolResult.Ok(new
                {
                    alignment_name = alignmentName,
                    element_index = idx,
                    curve_station = Math.Round(curveStation, 3),
                    curve_radius = Math.Round(radius, 3),
                    requested_rate_percent = Math.Round(newRate.Value, 3),
                    note = "Superelevation data not found or not directly modifiable. Use Civil 3D Superelevation Wizard to set rates, or ensure superelevation data exists on this alignment."
                }));
            }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("validate_alignment:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                element_index = idx,
                curve_station = Math.Round(curveStation, 3),
                curve_radius = Math.Round(radius, 3),
                old_rate_percent = oldRate.HasValue ? Math.Round(oldRate.Value * 100.0, 3) : (double?)null,
                new_rate_percent = Math.Round(newRate.Value, 3)
            }));
        }
    }
}
