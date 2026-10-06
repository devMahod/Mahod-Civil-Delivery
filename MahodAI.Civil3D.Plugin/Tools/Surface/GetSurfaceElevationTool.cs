using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Surface
{
    /// <summary>
    /// Gets the surface elevation at a specific point.
    /// </summary>
    public class GetSurfaceElevationTool : DrawingToolBase
    {
        public override string Name => "get_surface_elevation";
        public override string Description => "Gets the surface elevation at a specific X, Y coordinate.";
        public override string Category => ToolCategories.Surface;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var x = GetDoubleParam(parameters, "x");
            var y = GetDoubleParam(parameters, "y");

            if (!x.HasValue || !y.HasValue)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "X and Y coordinates are required");
            }

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

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
            {
                return ToolResult.NotFound("Surface", surfaceName);
            }

            try
            {
                var elevation = surface.FindElevationAtXY(x.Value, y.Value);

                var result = new SurfaceElevationResult
                {
                    SurfaceName = surface.Name,
                    X = x.Value,
                    Y = y.Value,
                    Elevation = elevation,
                    WithinBounds = true
                };

                // Try to get slope at this point for TIN surfaces
                if (surface is TinSurface tinSurface)
                {
                    try
                    {
                        var triangle = tinSurface.FindTriangleAtXY(x.Value, y.Value);
                        if (triangle != null)
                        {
                            // Calculate slope from triangle
                            var v1 = triangle.Vertex1.Location;
                            var v2 = triangle.Vertex2.Location;
                            var v3 = triangle.Vertex3.Location;

                            // Calculate normal vector
                            var u = new double[] { v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z };
                            var v = new double[] { v3.X - v1.X, v3.Y - v1.Y, v3.Z - v1.Z };

                            var n = new double[]
                            {
                                u[1] * v[2] - u[2] * v[1],
                                u[2] * v[0] - u[0] * v[2],
                                u[0] * v[1] - u[1] * v[0]
                            };

                            var magnitude = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                            if (magnitude > 0)
                            {
                                var cosAngle = Math.Abs(n[2]) / magnitude;
                                result.Slope = Math.Tan(Math.Acos(cosAngle)) * 100.0;

                                // Calculate aspect (direction of steepest descent)
                                result.Aspect = Math.Atan2(n[1], n[0]) * (180.0 / Math.PI);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] GetSurfaceElevation slope calculation: {ex.Message}");
                    }
                }

                return await Task.FromResult(ToolResult.Ok(result));
            }
            catch (Autodesk.Civil.PointNotOnEntityException)
            {
                return await Task.FromResult(ToolResult.Ok(new SurfaceElevationResult
                {
                    SurfaceName = surface.Name,
                    X = x.Value,
                    Y = y.Value,
                    WithinBounds = false,
                    Error = "Point is outside surface boundary"
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, $"Failed to get elevation: {ex.Message}");
            }
        }
    }

    #region Result Models

    public class SurfaceElevationResult
    {
        public string SurfaceName { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double? Elevation { get; set; }
        public double? Slope { get; set; }
        public double? Aspect { get; set; }
        public bool WithinBounds { get; set; }
        public string? Error { get; set; }
    }

    #endregion
}
