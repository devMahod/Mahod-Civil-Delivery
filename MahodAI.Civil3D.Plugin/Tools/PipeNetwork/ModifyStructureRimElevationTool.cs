using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.PipeNetwork
{
    /// <summary>
    /// Modifies the rim elevation of a structure (manhole, inlet, etc.) in a pipe network.
    /// </summary>
    public class ModifyStructureRimElevationTool : DrawingToolBase
    {
        public override string Name => "modify_structure_rim_elevation";
        public override string Description => "Modifies the rim (top) elevation of a structure in a pipe network. This affects the structure depth and cover calculations.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var networkName = GetRequiredStringParam(parameters, "network_name");
            var structureName = GetRequiredStringParam(parameters, "structure_name");
            var newRimElevation = GetDoubleParam(parameters, "new_rim_elevation");

            if (newRimElevation == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_rim_elevation' is missing");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the network
            var network = CivilObjectFinder.FindNetworkByName(tr, civilDoc, networkName);
            if (network == null)
                return ToolResult.NotFound("PipeNetwork", networkName);

            // Find the structure
            Structure? structure = null;
            ObjectId structureObjId = ObjectId.Null;
            foreach (ObjectId structureId in network.GetStructureIds())
            {
                var s = tr.GetObject(structureId, OpenMode.ForRead) as Structure;
                if (s != null && s.Name.Equals(structureName, StringComparison.OrdinalIgnoreCase))
                {
                    structure = s;
                    structureObjId = structureId;
                    break;
                }
            }

            if (structure == null)
                return ToolResult.NotFound("Structure", structureName);

            double oldRimElevation = structure.RimElevation;
            double sumpElevation = structure.SumpElevation;

            // Open structure for write
            structure = tr.GetObject(structureObjId, OpenMode.ForWrite) as Structure;
            if (structure == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open structure for write");

            try
            {
                structure.RimElevation = newRimElevation.Value;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify rim elevation: {ex.Message}");
            }

            cache.RemoveByPattern("get_structure_details:");
            cache.RemoveByPattern("get_pipe_network_info:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                network_name = networkName,
                structure_name = structureName,
                old_rim_elevation = Math.Round(oldRimElevation, 3),
                new_rim_elevation = Math.Round(newRimElevation.Value, 3),
                sump_elevation = Math.Round(sumpElevation, 3),
                new_depth = Math.Round(newRimElevation.Value - sumpElevation, 3)
            }));
        }
    }
}
