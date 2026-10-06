using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.PipeNetwork
{
    /// <summary>
    /// Lists all pipe networks with summary information.
    /// </summary>
    public class ListPipeNetworksTool : DrawingToolBase
    {
        public override string Name => "list_pipe_networks";
        public override string Description => "Lists all pipe networks in the drawing with their names, pipe counts, structure counts, and network types.";
        public override string Category => ToolCategories.PipeNetwork;
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

            var networks = new List<PipeNetworkSummary>();

            foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
            {
                ct.ThrowIfCancellationRequested();
                if (networks.Count >= limit) break;

                var network = tr.GetObject(id, OpenMode.ForRead) as Network;
                if (network == null) continue;

                // Apply filter if specified
                if (!string.IsNullOrEmpty(filter) &&
                    !network.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var summary = new PipeNetworkSummary
                {
                    Name = network.Name,
                    Description = network.Description,
                    PipeCount = network.GetPipeIds().Count,
                    StructureCount = network.GetStructureIds().Count
                };

                // Get part list name using reflection
                try
                {
                    if (!network.PartsListId.IsNull)
                    {
                        var partsListObj = tr.GetObject(network.PartsListId, OpenMode.ForRead);
                        var nameProp = partsListObj?.GetType().GetProperty("Name");
                        summary.PartsListName = nameProp?.GetValue(partsListObj)?.ToString();
                    }
                }
                catch { }

                // Analyze pipe sizes
                try
                {
                    var pipeSizes = new HashSet<double>();
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                        if (pipe != null)
                        {
                            pipeSizes.Add(pipe.InnerDiameterOrWidth);
                        }
                    }
                    summary.UniquePipeSizes = pipeSizes.Count;
                    if (pipeSizes.Count > 0)
                    {
                        summary.MinPipeSize = pipeSizes.Min();
                        summary.MaxPipeSize = pipeSizes.Max();
                    }
                }
                catch { }

                networks.Add(summary);
            }

            var result = new ListPipeNetworksResult
            {
                Networks = networks,
                TotalCount = networks.Count
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class ListPipeNetworksResult
    {
        public List<PipeNetworkSummary> Networks { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class PipeNetworkSummary
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int PipeCount { get; set; }
        public int StructureCount { get; set; }
        public string? PartsListName { get; set; }
        public int UniquePipeSizes { get; set; }
        public double? MinPipeSize { get; set; }
        public double? MaxPipeSize { get; set; }
    }

    #endregion
}
