using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.PipeNetwork
{
    /// <summary>
    /// Calculates cover depth for all pipes in a network by comparing pipe crown elevation
    /// with surface elevation above.
    /// </summary>
    public class CalculatePipeCoverDepthTool : DrawingToolBase
    {
        public override string Name => "calculate_pipe_cover_depth";
        public override string Description =>
            "Calculates cover depth for all pipes in a network by comparing pipe crown elevation with surface elevation above.";
        public override string Category => ToolCategories.PipeNetwork;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var networkName = GetRequiredStringParam(parameters, "network_name");
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

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
                return ToolResult.NotFound("PipeNetwork", networkName);

            // Find the surface
            Autodesk.Civil.DatabaseServices.Surface? surface = null;
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                if (obj != null && obj.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    surface = obj;
                    break;
                }
            }

            if (surface == null)
                return ToolResult.NotFound("Surface", surfaceName);

            try
            {
                var pipeResults = new List<object>();
                var allStartCovers = new List<double>();
                var allEndCovers = new List<double>();
                int warningCount = 0;

                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    ct.ThrowIfCancellationRequested();

                    var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                    if (pipe == null) continue;

                    double outerDiameter = pipe.OuterDiameterOrWidth;
                    double halfOuter = outerDiameter / 2.0;

                    // Start point
                    double startX = pipe.StartPoint.X;
                    double startY = pipe.StartPoint.Y;
                    double startInvert = pipe.StartPoint.Z;
                    double startCrown = startInvert + outerDiameter; // invert + full diameter = crown (outside)

                    // End point
                    double endX = pipe.EndPoint.X;
                    double endY = pipe.EndPoint.Y;
                    double endInvert = pipe.EndPoint.Z;
                    double endCrown = endInvert + outerDiameter;

                    double? startSurfaceElev = null;
                    double? endSurfaceElev = null;
                    double? startCover = null;
                    double? endCover = null;
                    string? warning = null;

                    try
                    {
                        startSurfaceElev = surface.FindElevationAtXY(startX, startY);
                        startCover = startSurfaceElev.Value - startCrown;
                        allStartCovers.Add(startCover.Value);
                    }
                    catch (Autodesk.Civil.PointNotOnEntityException)
                    {
                        warning = "Start point outside surface boundary";
                        warningCount++;
                    }

                    try
                    {
                        endSurfaceElev = surface.FindElevationAtXY(endX, endY);
                        endCover = endSurfaceElev.Value - endCrown;
                        allEndCovers.Add(endCover.Value);
                    }
                    catch (Autodesk.Civil.PointNotOnEntityException)
                    {
                        warning = (warning != null ? warning + "; " : "") + "End point outside surface boundary";
                        warningCount++;
                    }

                    double? minCover = null;
                    if (startCover.HasValue && endCover.HasValue)
                        minCover = Math.Min(startCover.Value, endCover.Value);
                    else if (startCover.HasValue)
                        minCover = startCover.Value;
                    else if (endCover.HasValue)
                        minCover = endCover.Value;

                    pipeResults.Add(new
                    {
                        pipe_name = pipe.Name,
                        outer_diameter = Math.Round(outerDiameter, 4),
                        length_2d = Math.Round(pipe.Length2D, 3),
                        start_surface_elev = startSurfaceElev.HasValue ? Math.Round(startSurfaceElev.Value, 3) : (double?)null,
                        start_crown_elev = Math.Round(startCrown, 3),
                        start_cover = startCover.HasValue ? Math.Round(startCover.Value, 3) : (double?)null,
                        end_surface_elev = endSurfaceElev.HasValue ? Math.Round(endSurfaceElev.Value, 3) : (double?)null,
                        end_crown_elev = Math.Round(endCrown, 3),
                        end_cover = endCover.HasValue ? Math.Round(endCover.Value, 3) : (double?)null,
                        min_cover = minCover.HasValue ? Math.Round(minCover.Value, 3) : (double?)null,
                        warning
                    });
                }

                // Aggregate statistics
                var allCovers = allStartCovers.Concat(allEndCovers).ToList();

                return await Task.FromResult(ToolResult.Ok(new
                {
                    network_name = network.Name,
                    surface_name = surfaceName,
                    pipe_count = pipeResults.Count,
                    warning_count = warningCount,
                    statistics = allCovers.Count > 0 ? new
                    {
                        min_cover = Math.Round(allCovers.Min(), 3),
                        max_cover = Math.Round(allCovers.Max(), 3),
                        avg_cover = Math.Round(allCovers.Average(), 3),
                        measurement_count = allCovers.Count
                    } : null,
                    pipes = pipeResults
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to calculate pipe cover depths: {ex.Message}");
            }
        }
    }
}
