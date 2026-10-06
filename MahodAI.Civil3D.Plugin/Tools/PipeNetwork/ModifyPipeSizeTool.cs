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
    /// Modifies the size (diameter) of a pipe in a pipe network.
    /// </summary>
    public class ModifyPipeSizeTool : DrawingToolBase
    {
        public override string Name => "modify_pipe_size";
        public override string Description => "Modifies the inner diameter/width of a pipe in a pipe network. The new size must match an available part size in the parts list.";
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
            var pipeName = GetRequiredStringParam(parameters, "pipe_name");
            var newSize = GetDoubleParam(parameters, "new_size");

            if (newSize == null || newSize.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_size' must be a positive number (inner diameter in mm or drawing units)");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the network
            var network = CivilObjectFinder.FindNetworkByName(tr, civilDoc, networkName);
            if (network == null)
                return ToolResult.NotFound("PipeNetwork", networkName);

            // Find the pipe
            Pipe? pipe = null;
            ObjectId pipeObjId = ObjectId.Null;
            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var p = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                if (p != null && p.Name.Equals(pipeName, StringComparison.OrdinalIgnoreCase))
                {
                    pipe = p;
                    pipeObjId = pipeId;
                    break;
                }
            }

            if (pipe == null)
                return ToolResult.NotFound("Pipe", pipeName);

            double oldInnerSize = pipe.InnerDiameterOrWidth;
            double oldOuterSize = pipe.OuterDiameterOrWidth;

            // Open pipe for write
            pipe = tr.GetObject(pipeObjId, OpenMode.ForWrite) as Pipe;
            if (pipe == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open pipe for write");

            try
            {
                var innerDiamProp = pipe.GetType().GetProperty("InnerDiameterOrWidth");
                if (innerDiamProp != null && innerDiamProp.CanWrite)
                    innerDiamProp.SetValue(pipe, newSize.Value);
                else
                    throw new InvalidOperationException("InnerDiameterOrWidth is read-only in this Civil 3D version — pipe size must be changed by selecting a different part from the parts list");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify pipe size: {ex.Message}. Ensure the size matches an available part in the parts list.");
            }

            cache.RemoveByPattern("get_pipe_details:");
            cache.RemoveByPattern("get_pipe_network_info:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                network_name = networkName,
                pipe_name = pipeName,
                old_inner_size = Math.Round(oldInnerSize, 3),
                old_outer_size = Math.Round(oldOuterSize, 3),
                new_inner_size = Math.Round(newSize.Value, 3)
            }));
        }
    }
}
