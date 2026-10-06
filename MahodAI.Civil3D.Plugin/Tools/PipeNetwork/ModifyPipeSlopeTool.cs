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
    /// Modifies the slope of a pipe by adjusting its end invert elevation.
    /// </summary>
    public class ModifyPipeSlopeTool : DrawingToolBase
    {
        public override string Name => "modify_pipe_slope";
        public override string Description => "Modifies the slope of a pipe in a pipe network by adjusting the end invert elevation to achieve the desired slope.";
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
            var newSlope = GetDoubleParam(parameters, "new_slope");

            if (newSlope == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_slope' is missing (as decimal, e.g., 0.02 for 2%)");

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

            double oldSlope = pipe.Slope;
            double length2D = pipe.Length2D;
            double startInvert = pipe.StartPoint.Z;

            // Calculate new end invert elevation based on desired slope
            double newEndInvert = startInvert - (newSlope.Value * length2D);

            // Open pipe for write and modify
            pipe = tr.GetObject(pipeObjId, OpenMode.ForWrite) as Pipe;
            if (pipe == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open pipe for write");

            try
            {
                pipe.EndPoint = new Autodesk.AutoCAD.Geometry.Point3d(
                    pipe.EndPoint.X, pipe.EndPoint.Y, newEndInvert);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify pipe slope: {ex.Message}");
            }

            cache.RemoveByPattern("get_pipe_details:");
            cache.RemoveByPattern("get_pipe_network_info:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                network_name = networkName,
                pipe_name = pipeName,
                old_slope = Math.Round(oldSlope, 6),
                old_slope_percent = Math.Round(oldSlope * 100.0, 3),
                new_slope = Math.Round(newSlope.Value, 6),
                new_slope_percent = Math.Round(newSlope.Value * 100.0, 3),
                start_invert = Math.Round(startInvert, 3),
                new_end_invert = Math.Round(newEndInvert, 3)
            }));
        }
    }
}
