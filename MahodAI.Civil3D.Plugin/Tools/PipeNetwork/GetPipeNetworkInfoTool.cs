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
    /// Gets detailed information about a pipe network.
    /// </summary>
    public class GetPipeNetworkInfoTool : DrawingToolBase
    {
        public override string Name => "get_pipe_network_info";
        public override string Description => "Gets detailed statistics and structure for a pipe network including pipe inventory and structure inventory.";
        public override string Category => ToolCategories.PipeNetwork;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var networkName = GetRequiredStringParam(parameters, "network_name");
            var includePipeList = GetBoolParam(parameters, "include_pipe_list", true);
            var includeStructureList = GetBoolParam(parameters, "include_structure_list", true);
            var includeFlowAnalysis = GetBoolParam(parameters, "include_flow_analysis", false);

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the network
            Network? network = null;
            foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Network;
                if (obj != null && obj.Name.Equals(networkName, StringComparison.OrdinalIgnoreCase))
                {
                    network = obj;
                    break;
                }
            }

            if (network == null)
            {
                return ToolResult.NotFound("PipeNetwork", networkName);
            }

            var result = new PipeNetworkInfoResult
            {
                Name = network.Name,
                Description = network.Description
            };

            // Get statistics
            result.Statistics = new NetworkStatistics
            {
                PipeCount = network.GetPipeIds().Count,
                StructureCount = network.GetStructureIds().Count
            };

            // Get pipe inventory
            var pipesBySize = new Dictionary<double, int>();
            var pipesByMaterial = new Dictionary<string, int>();
            double totalLength = 0;

            if (includePipeList)
            {
                result.Pipes = new List<PipeSummary>();
            }

            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                ct.ThrowIfCancellationRequested();

                var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                if (pipe == null) continue;

                totalLength += pipe.Length2D;

                // Track by size
                var size = pipe.InnerDiameterOrWidth;
                if (!pipesBySize.ContainsKey(size))
                    pipesBySize[size] = 0;
                pipesBySize[size]++;

                // Track by material
                var material = pipe.PartDescription ?? pipe.Name ?? "Unspecified";
                if (!pipesByMaterial.ContainsKey(material))
                    pipesByMaterial[material] = 0;
                pipesByMaterial[material]++;

                if (includePipeList && result.Pipes!.Count < 500)
                {
                    var shape = pipe.CrossSectionalShape;
                    bool isCircular = shape != SweptShapeType.Rectangular;

                    var pipeSummary = new PipeSummary
                    {
                        Name = pipe.Name,
                        Size = size,
                        CrossSectionalShape = isCircular ? "circular" : "rectangular",
                        Length = pipe.Length2D,
                        Material = material,
                        Slope = pipe.Slope,
                        StartStructure = GetStructureName(tr, pipe.StartStructureId),
                        EndStructure = GetStructureName(tr, pipe.EndStructureId)
                    };

                    if (!isCircular)
                    {
                        pipeSummary.InnerHeight = pipe.InnerHeight;
                        pipeSummary.OuterHeight = pipe.OuterHeight;
                    }

                    // Calculate cover depth if possible
                    try
                    {
                        var outerDiam = pipe.OuterDiameterOrWidth;
                        var startInvert = pipe.StartPoint.Z;
                        var endInvert = pipe.EndPoint.Z;
                        var startCrown = startInvert + outerDiam;
                        var endCrown = endInvert + outerDiam;

                        // Try to get surface elevation at pipe midpoint
                        var midX = (pipe.StartPoint.X + pipe.EndPoint.X) / 2;
                        var midY = (pipe.StartPoint.Y + pipe.EndPoint.Y) / 2;
                        var surfElev = GetSurfaceElevationAtPoint(tr, midX, midY);
                        if (surfElev.HasValue)
                        {
                            var midCrown = (startCrown + endCrown) / 2;
                            pipeSummary.CoverDepth = Math.Round(surfElev.Value - midCrown, 2);
                        }
                    }
                    catch { }

                    result.Pipes.Add(pipeSummary);
                }
            }

            result.Statistics.TotalPipeLength = totalLength;
            result.Statistics.PipesBySize = pipesBySize;
            result.Statistics.PipesByMaterial = pipesByMaterial;

            // Get structure inventory
            var structuresByType = new Dictionary<string, int>();

            if (includeStructureList)
            {
                result.Structures = new List<StructureSummary>();
            }

            foreach (ObjectId structureId in network.GetStructureIds())
            {
                ct.ThrowIfCancellationRequested();

                var structure = tr.GetObject(structureId, OpenMode.ForRead) as Structure;
                if (structure == null) continue;

                var structureType = structure.PartDescription ?? structure.Name ?? "Unspecified";
                if (!structuresByType.ContainsKey(structureType))
                    structuresByType[structureType] = 0;
                structuresByType[structureType]++;

                if (includeStructureList && result.Structures!.Count < 500)
                {
                    var structSummary = new StructureSummary
                    {
                        Name = structure.Name,
                        StructureType = structureType,
                        RimElevation = structure.RimElevation,
                        SumpElevation = structure.SumpElevation,
                        Depth = Math.Round(structure.RimElevation - structure.SumpElevation, 2),
                        ConnectedPipeCount = structure.ConnectedPipesCount
                    };

                    // Try to get inner diameter
                    try
                    {
                        var innerDiam = structure.InnerDiameterOrWidth;
                        if (innerDiam > 0)
                            structSummary.InnerDiameter = Math.Round(innerDiam * 1000, 0); // m → mm
                    }
                    catch { }

                    result.Structures.Add(structSummary);
                }
            }

            result.Statistics.StructuresByType = structuresByType;

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private string? GetStructureName(Transaction tr, ObjectId structureId)
        {
            if (structureId.IsNull) return null;
            try
            {
                var structure = tr.GetObject(structureId, OpenMode.ForRead) as Structure;
                return structure?.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get surface elevation at a point from the first TIN surface in the drawing.
        /// Used for calculating pipe cover depth.
        /// </summary>
        private double? GetSurfaceElevationAtPoint(Transaction tr, double x, double y)
        {
            try
            {
                var civilDoc = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument;
                foreach (ObjectId surfId in civilDoc.GetSurfaceIds())
                {
                    var surface = tr.GetObject(surfId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.TinSurface;
                    if (surface == null) continue;

                    try
                    {
                        return surface.FindElevationAtXY(x, y);
                    }
                    catch
                    {
                        continue; // Point outside surface bounds
                    }
                }
            }
            catch { }
            return null;
        }
    }

    #region Result Models

    public class PipeNetworkInfoResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public NetworkStatistics? Statistics { get; set; }
        public List<PipeSummary>? Pipes { get; set; }
        public List<StructureSummary>? Structures { get; set; }
    }

    public class NetworkStatistics
    {
        public int PipeCount { get; set; }
        public int StructureCount { get; set; }
        public double TotalPipeLength { get; set; }
        public Dictionary<double, int>? PipesBySize { get; set; }
        public Dictionary<string, int>? PipesByMaterial { get; set; }
        public Dictionary<string, int>? StructuresByType { get; set; }
    }

    public class PipeSummary
    {
        public string Name { get; set; } = string.Empty;
        public double Size { get; set; }
        public string CrossSectionalShape { get; set; } = "circular";
        public double? InnerHeight { get; set; }
        public double? OuterHeight { get; set; }
        public double Length { get; set; }
        public string? Material { get; set; }
        public double Slope { get; set; }
        public string? StartStructure { get; set; }
        public string? EndStructure { get; set; }
        public double? CoverDepth { get; set; }
    }

    public class StructureSummary
    {
        public string Name { get; set; } = string.Empty;
        public string StructureType { get; set; } = string.Empty;
        public double RimElevation { get; set; }
        public double SumpElevation { get; set; }
        public double Depth { get; set; }
        public double? InnerDiameter { get; set; }
        public int ConnectedPipeCount { get; set; }
    }

    #endregion
}
