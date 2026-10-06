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
    /// Modifies the sump depth of a structure (manhole, inlet, etc.) in a pipe network.
    /// </summary>
    public class ModifyStructureSumpDepthTool : DrawingToolBase
    {
        public override string Name => "modify_structure_sump_depth";
        public override string Description => "Modifies the sump depth of a structure in a pipe network. The sump is the space below the lowest connected pipe invert.";
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
            var newSumpDepth = GetDoubleParam(parameters, "new_sump_depth");

            if (newSumpDepth == null || newSumpDepth.Value < 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_sump_depth' must be a non-negative number");

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

            double oldSumpDepth = structure.SumpDepth;
            double oldSumpElevation = structure.SumpElevation;
            double rimElevation = structure.RimElevation;

            // Open structure for write
            structure = tr.GetObject(structureObjId, OpenMode.ForWrite) as Structure;
            if (structure == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open structure for write");

            try
            {
                structure.SumpDepth = newSumpDepth.Value;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify sump depth: {ex.Message}");
            }

            cache.RemoveByPattern("get_structure_details:");
            cache.RemoveByPattern("get_pipe_network_info:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                network_name = networkName,
                structure_name = structureName,
                old_sump_depth = Math.Round(oldSumpDepth, 3),
                new_sump_depth = Math.Round(newSumpDepth.Value, 3),
                old_sump_elevation = Math.Round(oldSumpElevation, 3),
                rim_elevation = Math.Round(rimElevation, 3)
            }));
        }
    }
}
