using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Lists all corridors with summary information.
    /// </summary>
    public class ListCorridorsTool : DrawingToolBase
    {
        public override string Name => "list_corridors";
        public override string Description => "Lists all corridors in the drawing with their names, baseline alignments, and region information.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            var filter = GetStringParam(parameters, "filter");
            var limit = GetIntParam(parameters, "limit") ?? 100;

            var corridors = new List<CorridorSummary>();

            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                ct.ThrowIfCancellationRequested();
                if (corridors.Count >= limit) break;

                var corridor = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                if (corridor == null) continue;

                // Apply filter if specified
                if (!string.IsNullOrEmpty(filter) &&
                    !corridor.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var summary = new CorridorSummary
                {
                    Name = corridor.Name,
                    Description = corridor.Description,
                    BaselineCount = corridor.Baselines.Count
                };

                // Get baseline info
                summary.Baselines = new List<BaselineSummary>();
                foreach (Baseline baseline in corridor.Baselines)
                {
                    var baselineSummary = new BaselineSummary
                    {
                        Name = baseline.Name,
                        StartStation = baseline.StartStation,
                        EndStation = baseline.EndStation
                    };

                    // Get alignment and profile names
                    try
                    {
                        if (!baseline.AlignmentId.IsNull)
                        {
                            var alignment = tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                            baselineSummary.AlignmentName = alignment?.Name;
                        }
                        if (!baseline.ProfileId.IsNull)
                        {
                            var profile = tr.GetObject(baseline.ProfileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                            baselineSummary.ProfileName = profile?.Name;
                        }
                    }
                    catch { }

                    // Count regions
                    baselineSummary.RegionCount = baseline.BaselineRegions.Count;

                    summary.Baselines.Add(baselineSummary);
                }

                corridors.Add(summary);
            }

            var result = new ListCorridorsResult
            {
                Corridors = corridors,
                TotalCount = corridors.Count
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class ListCorridorsResult
    {
        public List<CorridorSummary> Corridors { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class CorridorSummary
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int BaselineCount { get; set; }
        public List<BaselineSummary>? Baselines { get; set; }
    }

    public class BaselineSummary
    {
        public string Name { get; set; } = string.Empty;
        public string? AlignmentName { get; set; }
        public string? ProfileName { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public int RegionCount { get; set; }
    }

    #endregion
}
