using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Surface
{
    /// <summary>
    /// Gets detailed information about a surface.
    /// </summary>
    public class GetSurfaceInfoTool : DrawingToolBase
    {
        public override string Name => "get_surface_info";
        public override string Description => "Gets detailed statistics and properties for a surface including elevation range, slope analysis, and boundary information.";
        public override string Category => ToolCategories.Surface;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var includeSlopeAnalysis = GetBoolParam(parameters, "include_slope_analysis", false);
            var includeContours = GetBoolParam(parameters, "include_contours", false);

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

            var result = new SurfaceInfoResult
            {
                Name = surface.Name,
                Description = surface.Description,
                SurfaceType = surface.GetType().Name
            };

            // Get properties from geometric extents
            try
            {
                var extents = surface.GeometricExtents;
                result.Statistics = new SurfaceStatistics
                {
                    MinElevation = extents.MinPoint.Z,
                    MaxElevation = extents.MaxPoint.Z,
                    MeanElevation = (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0
                };
                result.Bounds = new SurfaceBounds
                {
                    MinX = extents.MinPoint.X,
                    MaxX = extents.MaxPoint.X,
                    MinY = extents.MinPoint.Y,
                    MaxY = extents.MaxPoint.Y
                };
            }
            catch { }

            // Get TIN-specific properties
            if (surface is TinSurface tinSurface)
            {
                result.TinProperties = new TinSurfaceProperties
                {
                    PointCount = tinSurface.Vertices.Count,
                    TriangleCount = tinSurface.Triangles.Count
                };

                // Get boundary count from BoundariesDefinition
                try
                {
                    result.BoundaryCount = tinSurface.BoundariesDefinition.Count;
                }
                catch { }

                // Get slope analysis if requested
                if (includeSlopeAnalysis)
                {
                    var warnings = new List<string>();
                    result.SlopeAnalysis = CalculateSlopeAnalysis(tinSurface, ct, warnings);
                    if (warnings.Count > 0)
                        result.ExtractionWarnings = warnings;
                }
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private SlopeAnalysisResult CalculateSlopeAnalysis(TinSurface surface, CancellationToken ct, List<string> warnings)
        {
            var analysis = new SlopeAnalysisResult();

            try
            {
                var slopes = new List<double>();
                int sampleCount = 0;
                int maxSamples = 10000;

                foreach (TinSurfaceTriangle triangle in surface.Triangles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (sampleCount++ >= maxSamples) break;

                    // Calculate slope from triangle vertices
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
                        var slopePercent = Math.Tan(Math.Acos(cosAngle)) * 100.0;
                        slopes.Add(slopePercent);
                    }
                }

                if (slopes.Count > 0)
                {
                    slopes.Sort();
                    analysis.MinSlope = slopes[0];
                    analysis.MaxSlope = slopes[slopes.Count - 1];
                    analysis.MeanSlope = slopes.Average();
                    analysis.MedianSlope = slopes[slopes.Count / 2];
                    analysis.SampleCount = slopes.Count;

                    // Calculate slope distribution
                    analysis.SlopeDistribution = new Dictionary<string, int>
                    {
                        ["0-5%"] = slopes.Count(s => s >= 0 && s < 5),
                        ["5-10%"] = slopes.Count(s => s >= 5 && s < 10),
                        ["10-20%"] = slopes.Count(s => s >= 10 && s < 20),
                        ["20-33%"] = slopes.Count(s => s >= 20 && s < 33),
                        [">33%"] = slopes.Count(s => s >= 33)
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Slope analysis error: {ex.Message}");
                warnings.Add($"Slope analysis error (partial data): {ex.Message}");
            }

            return analysis;
        }
    }

    #region Result Models

    public class SurfaceInfoResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string SurfaceType { get; set; } = string.Empty;
        public SurfaceStatistics? Statistics { get; set; }
        public SurfaceBounds? Bounds { get; set; }
        public TinSurfaceProperties? TinProperties { get; set; }
        public int BoundaryCount { get; set; }
        public SlopeAnalysisResult? SlopeAnalysis { get; set; }
        public List<string>? ExtractionWarnings { get; set; }
    }

    public class SurfaceBounds
    {
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
    }

    public class SurfaceStatistics
    {
        public double MinElevation { get; set; }
        public double MaxElevation { get; set; }
        public double MeanElevation { get; set; }
        public double Area2D { get; set; }
        public double Area3D { get; set; }
    }

    public class TinSurfaceProperties
    {
        public int PointCount { get; set; }
        public int TriangleCount { get; set; }
    }

    public class BoundaryInfo
    {
        public string Name { get; set; } = string.Empty;
        public string BoundaryType { get; set; } = string.Empty;
        public int VertexCount { get; set; }
    }

    public class SlopeAnalysisResult
    {
        public double MinSlope { get; set; }
        public double MaxSlope { get; set; }
        public double MeanSlope { get; set; }
        public double MedianSlope { get; set; }
        public int SampleCount { get; set; }
        public Dictionary<string, int>? SlopeDistribution { get; set; }
    }

    #endregion
}
