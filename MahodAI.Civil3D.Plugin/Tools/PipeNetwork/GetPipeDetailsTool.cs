using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.PipeNetwork
{
    /// <summary>
    /// Gets detailed information about a specific pipe.
    /// </summary>
    public class GetPipeDetailsTool : DrawingToolBase
    {
        public override string Name => "get_pipe_details";
        public override string Description => "Gets detailed properties for a specific pipe including geometry, slope, and connected structures.";
        public override string Category => ToolCategories.PipeNetwork;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var networkName = GetRequiredStringParam(parameters, "network_name");
            var pipeName = GetRequiredStringParam(parameters, "pipe_name");

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

            // Find the pipe
            Pipe? pipe = null;
            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var p = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                if (p != null && p.Name.Equals(pipeName, StringComparison.OrdinalIgnoreCase))
                {
                    pipe = p;
                    break;
                }
            }

            if (pipe == null)
            {
                return ToolResult.NotFound("Pipe", pipeName);
            }

            var shape = pipe.CrossSectionalShape;
            bool isCircular = shape != SweptShapeType.Rectangular;

            var result = new PipeDetailsResult
            {
                Name = pipe.Name,
                Description = pipe.Description,
                NetworkName = network.Name,
                PartDescription = pipe.PartDescription,
                CrossSectionalShape = isCircular ? "circular" : "rectangular",
                InnerDiameter = pipe.InnerDiameterOrWidth,
                OuterDiameter = pipe.OuterDiameterOrWidth,
                Length2D = pipe.Length2D,
                Length3D = pipe.Length3D,
                Slope = pipe.Slope,
                SlopePercent = pipe.Slope * 100.0,
                Material = pipe.PartDescription
            };

            if (!isCircular)
            {
                result.InnerHeight = pipe.InnerHeight;
                result.OuterHeight = pipe.OuterHeight;
            }

            // Start point
            result.StartPoint = new PipeEndpoint
            {
                X = pipe.StartPoint.X,
                Y = pipe.StartPoint.Y,
                InvertElevation = pipe.StartPoint.Z
            };

            // End point
            result.EndPoint = new PipeEndpoint
            {
                X = pipe.EndPoint.X,
                Y = pipe.EndPoint.Y,
                InvertElevation = pipe.EndPoint.Z
            };

            // Connected structures
            try
            {
                if (!pipe.StartStructureId.IsNull)
                {
                    var startStruct = tr.GetObject(pipe.StartStructureId, OpenMode.ForRead) as Structure;
                    if (startStruct != null)
                    {
                        result.StartStructure = new ConnectedStructure
                        {
                            Name = startStruct.Name,
                            RimElevation = startStruct.RimElevation,
                            SumpElevation = startStruct.SumpElevation
                        };
                    }
                }

                if (!pipe.EndStructureId.IsNull)
                {
                    var endStruct = tr.GetObject(pipe.EndStructureId, OpenMode.ForRead) as Structure;
                    if (endStruct != null)
                    {
                        result.EndStructure = new ConnectedStructure
                        {
                            Name = endStruct.Name,
                            RimElevation = endStruct.RimElevation,
                            SumpElevation = endStruct.SumpElevation
                        };
                    }
                }
            }
            catch { }

            // Reference info
            try
            {
                if (!pipe.RefAlignmentId.IsNull)
                {
                    var alignment = tr.GetObject(pipe.RefAlignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    result.ReferenceAlignmentName = alignment?.Name;
                }
            }
            catch { }

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class PipeDetailsResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string NetworkName { get; set; } = string.Empty;
        public string? PartDescription { get; set; }
        public string CrossSectionalShape { get; set; } = "circular";
        public double InnerDiameter { get; set; }
        public double OuterDiameter { get; set; }
        public double? InnerHeight { get; set; }
        public double? OuterHeight { get; set; }
        public double Length2D { get; set; }
        public double Length3D { get; set; }
        public double Slope { get; set; }
        public double SlopePercent { get; set; }
        public string? Material { get; set; }
        public PipeEndpoint? StartPoint { get; set; }
        public PipeEndpoint? EndPoint { get; set; }
        public ConnectedStructure? StartStructure { get; set; }
        public ConnectedStructure? EndStructure { get; set; }
        public string? ReferenceAlignmentName { get; set; }
    }

    public class PipeEndpoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double InvertElevation { get; set; }
    }

    public class ConnectedStructure
    {
        public string Name { get; set; } = string.Empty;
        public double RimElevation { get; set; }
        public double SumpElevation { get; set; }
    }

    #endregion
}
