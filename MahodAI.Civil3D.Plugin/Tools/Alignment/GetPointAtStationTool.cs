using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Gets coordinates at a specific station along an alignment.
    /// </summary>
    public class GetPointAtStationTool : DrawingToolBase
    {
        public override string Name => "get_point_at_station";
        public override string Description => "Gets X, Y coordinates at a specific station along an alignment, optionally with an offset distance.";
        public override string Category => ToolCategories.Alignment;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var station = GetDoubleParam(parameters, "station");
            var offset = GetDoubleParam(parameters, "offset") ?? 0.0;

            if (!station.HasValue)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Station parameter is required");
            }

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the alignment
            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);

            if (alignment == null)
            {
                return ToolResult.NotFound("Alignment", alignmentName);
            }

            // Validate station is within range
            if (station.Value < alignment.StartingStation || station.Value > alignment.EndingStation)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    $"Station {station.Value} is outside alignment range ({alignment.StartingStation} - {alignment.EndingStation})");
            }

            try
            {
                double x = 0, y = 0;
                alignment.PointLocation(station.Value, offset, ref x, ref y);

                var result = new PointAtStationResult
                {
                    AlignmentName = alignment.Name,
                    Station = station.Value,
                    Offset = offset,
                    X = x,
                    Y = y
                };

                // Try to get the element type at this station using reflection for station properties
                try
                {
                    foreach (AlignmentEntity entity in alignment.Entities)
                    {
                        var entityType = entity.GetType();
                        double startSta = 0, endSta = 0;
                        try
                        {
                            var startProp = entityType.GetProperty("StartStation");
                            var endProp = entityType.GetProperty("EndStation");
                            if (startProp != null) startSta = Convert.ToDouble(startProp.GetValue(entity));
                            if (endProp != null) endSta = Convert.ToDouble(endProp.GetValue(entity));
                        }
                        catch { continue; }

                        if (station.Value >= startSta && station.Value <= endSta)
                        {
                            result.ElementType = entity.EntityType.ToString();
                            if (entity is AlignmentArc arc)
                            {
                                result.Radius = arc.Radius;
                            }
                            break;
                        }
                    }
                }
                catch { }

                return await Task.FromResult(ToolResult.Ok(result));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, $"Failed to get point at station: {ex.Message}");
            }
        }
    }

    #region Result Models

    public class PointAtStationResult
    {
        public string AlignmentName { get; set; } = string.Empty;
        public double Station { get; set; }
        public double Offset { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double DirectionRadians { get; set; }
        public double DirectionDegrees { get; set; }
        public string? ElementType { get; set; }
        public double? Radius { get; set; }
    }

    #endregion
}
