using System;
using System.Collections.Generic;
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
    /// Returns station equation information for an alignment, including raw-to-published station mappings.
    /// </summary>
    public class GetStationEquationInfoTool : DrawingToolBase
    {
        public override string Name => "get_station_equation_info";
        public override string Description =>
            "Returns station equation information for an alignment, including raw-to-published station mappings.";
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

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the alignment
            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);
            if (alignment == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            try
            {
                bool hasEquations = StationHelper.HasStationEquations(alignment);
                var equations = new List<object>();

                if (hasEquations)
                {
                    try
                    {
                        foreach (StationEquation eq in alignment.StationEquations)
                        {
                            ct.ThrowIfCancellationRequested();

                            equations.Add(new
                            {
                                raw_station_back = Math.Round(eq.RawStationBack, 3),
                                station_ahead = Math.Round(eq.StationAhead, 3),
                                equation_type = eq.EquationType.ToString()
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI] GetStationEquationInfo: Error reading equations: {ex.Message}");
                    }
                }

                // Raw start/end stations
                double rawStart = alignment.StartingStation;
                double rawEnd = alignment.EndingStation;

                // Published start/end stations
                double publishedStart = StationHelper.RawToStation(alignment, rawStart);
                double publishedEnd = StationHelper.RawToStation(alignment, rawEnd);

                // Formatted station strings
                string formattedStart = StationHelper.FormatStation(alignment, rawStart);
                string formattedEnd = StationHelper.FormatStation(alignment, rawEnd);

                return await Task.FromResult(ToolResult.Ok(new
                {
                    alignment_name = alignment.Name,
                    has_equations = hasEquations,
                    equation_count = equations.Count,
                    equations,
                    raw_start_station = Math.Round(rawStart, 3),
                    raw_end_station = Math.Round(rawEnd, 3),
                    published_start_station = Math.Round(publishedStart, 3),
                    published_end_station = Math.Round(publishedEnd, 3),
                    formatted_start_station = formattedStart,
                    formatted_end_station = formattedEnd,
                    alignment_length = Math.Round(alignment.Length, 3)
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to get station equation info: {ex.Message}");
            }
        }
    }
}
