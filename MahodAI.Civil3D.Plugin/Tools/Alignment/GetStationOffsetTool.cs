using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Gets station and offset from X, Y coordinates relative to an alignment.
    /// </summary>
    public class GetStationOffsetTool : DrawingToolBase
    {
        public override string Name => "get_station_offset";
        public override string Description => "Calculates the station and offset for a given X, Y coordinate relative to an alignment.";
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
            var x = GetDoubleParam(parameters, "x");
            var y = GetDoubleParam(parameters, "y");

            if (!x.HasValue || !y.HasValue)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "X and Y coordinates are required");
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

            try
            {
                double station = 0, offset = 0;
                alignment.StationOffset(x.Value, y.Value, ref station, ref offset);

                // Determine if point is within alignment range
                bool withinRange = station >= alignment.StartingStation && station <= alignment.EndingStation;

                var result = new StationOffsetResult
                {
                    AlignmentName = alignment.Name,
                    X = x.Value,
                    Y = y.Value,
                    Station = station,
                    Offset = offset,
                    OffsetSide = offset >= 0 ? "Right" : "Left",
                    AbsoluteOffset = Math.Abs(offset),
                    WithinAlignmentRange = withinRange,
                    AlignmentStartStation = alignment.StartingStation,
                    AlignmentEndStation = alignment.EndingStation
                };

                // Get the element type at this station if within range
                if (withinRange)
                {
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

                            if (station >= startSta && station <= endSta)
                            {
                                result.ElementType = entity.EntityType.ToString();
                                break;
                            }
                        }
                    }
                    catch { }
                }

                return await Task.FromResult(ToolResult.Ok(result));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, $"Failed to calculate station/offset: {ex.Message}");
            }
        }
    }

    #region Result Models

    public class StationOffsetResult
    {
        public string AlignmentName { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Station { get; set; }
        public double Offset { get; set; }
        public string OffsetSide { get; set; } = string.Empty;
        public double AbsoluteOffset { get; set; }
        public bool WithinAlignmentRange { get; set; }
        public double AlignmentStartStation { get; set; }
        public double AlignmentEndStation { get; set; }
        public string? ElementType { get; set; }
    }

    #endregion
}
