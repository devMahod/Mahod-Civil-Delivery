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
    /// Gets detailed information about a specific structure (manhole, inlet, etc.).
    /// </summary>
    public class GetStructureDetailsTool : DrawingToolBase
    {
        public override string Name => "get_structure_details";
        public override string Description => "Gets detailed properties for a specific structure (manhole, inlet, etc.) including connected pipes and elevations.";
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
            var structureName = GetRequiredStringParam(parameters, "structure_name");

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

            // Find the structure
            Structure? structure = null;
            foreach (ObjectId structureId in network.GetStructureIds())
            {
                var s = tr.GetObject(structureId, OpenMode.ForRead) as Structure;
                if (s != null && s.Name.Equals(structureName, StringComparison.OrdinalIgnoreCase))
                {
                    structure = s;
                    break;
                }
            }

            if (structure == null)
            {
                return ToolResult.NotFound("Structure", structureName);
            }

            var result = new StructureDetailsResult
            {
                Name = structure.Name,
                Description = structure.Description,
                NetworkName = network.Name,
                PartDescription = structure.PartDescription,
                Location = new StructureLocation
                {
                    X = structure.Location.X,
                    Y = structure.Location.Y
                },
                RimElevation = structure.RimElevation,
                SumpElevation = structure.SumpElevation,
                SumpDepth = structure.SumpDepth,
                ConnectedPipeCount = structure.ConnectedPipesCount
            };

            // Get connected pipes
            result.ConnectedPipes = new List<ConnectedPipeInfo>();

            try
            {
                // Get all pipes in network and check which connect to this structure
                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    ct.ThrowIfCancellationRequested();

                    var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                    if (pipe == null) continue;

                    string? connectionType = null;
                    double? invertElevation = null;

                    if (pipe.StartStructureId == structure.ObjectId)
                    {
                        connectionType = "Outlet";
                        invertElevation = pipe.StartPoint.Z;
                    }
                    else if (pipe.EndStructureId == structure.ObjectId)
                    {
                        connectionType = "Inlet";
                        invertElevation = pipe.EndPoint.Z;
                    }

                    if (connectionType != null)
                    {
                        result.ConnectedPipes.Add(new ConnectedPipeInfo
                        {
                            PipeName = pipe.Name,
                            ConnectionType = connectionType,
                            InvertElevation = invertElevation ?? 0,
                            PipeSize = pipe.InnerDiameterOrWidth,
                            PipeSlope = pipe.Slope
                        });
                    }
                }
            }
            catch { }

            // Reference info
            try
            {
                if (!structure.RefAlignmentId.IsNull)
                {
                    var alignment = tr.GetObject(structure.RefAlignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (alignment != null)
                    {
                        result.ReferenceAlignmentName = alignment.Name;

                        // Get station offset
                        double station = 0, offset = 0;
                        alignment.StationOffset(structure.Location.X, structure.Location.Y, ref station, ref offset);
                        result.Station = station;
                        result.Offset = offset;
                    }
                }
            }
            catch { }

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class StructureDetailsResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string NetworkName { get; set; } = string.Empty;
        public string? PartDescription { get; set; }
        public StructureLocation? Location { get; set; }
        public double RimElevation { get; set; }
        public double SumpElevation { get; set; }
        public double SumpDepth { get; set; }
        public int ConnectedPipeCount { get; set; }
        public List<ConnectedPipeInfo>? ConnectedPipes { get; set; }
        public string? ReferenceAlignmentName { get; set; }
        public double? Station { get; set; }
        public double? Offset { get; set; }
    }

    public class StructureLocation
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class ConnectedPipeInfo
    {
        public string PipeName { get; set; } = string.Empty;
        public string ConnectionType { get; set; } = string.Empty;
        public double InvertElevation { get; set; }
        public double PipeSize { get; set; }
        public double PipeSlope { get; set; }
    }

    #endregion
}
