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
    /// Lists all surfaces with summary information.
    /// </summary>
    public class ListSurfacesTool : DrawingToolBase
    {
        public override string Name => "list_surfaces";
        public override string Description => "Lists all surfaces in the drawing with their names, types, point counts, and elevation ranges.";
        public override string Category => ToolCategories.Surface;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            var filter = GetStringParam(parameters, "filter");
            var limit = GetIntParam(parameters, "limit") ?? 100;

            var surfaces = new List<SurfaceSummary>();

            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                ct.ThrowIfCancellationRequested();
                if (surfaces.Count >= limit) break;

                var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                if (surface == null) continue;

                // Apply filter if specified
                if (!string.IsNullOrEmpty(filter) &&
                    !surface.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var summary = new SurfaceSummary
                {
                    Name = surface.Name,
                    Description = surface.Description,
                    SurfaceType = surface.GetType().Name
                };

                // Get statistics using GeometricExtents
                try
                {
                    var extents = surface.GeometricExtents;
                    summary.MinElevation = extents.MinPoint.Z;
                    summary.MaxElevation = extents.MaxPoint.Z;
                    summary.MeanElevation = (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0;
                }
                catch { }

                if (surface is TinSurface tinSurface)
                {
                    try
                    {
                        summary.PointCount = tinSurface.Vertices.Count;
                        summary.TriangleCount = tinSurface.Triangles.Count;
                    }
                    catch { }
                }
                else if (surface is TinVolumeSurface)
                {
                    summary.SurfaceType = "TinVolumeSurface";
                }
                else if (surface is GridSurface)
                {
                    summary.SurfaceType = "GridSurface";
                }

                surfaces.Add(summary);
            }

            var result = new ListSurfacesResult
            {
                Surfaces = surfaces,
                TotalCount = surfaces.Count
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class ListSurfacesResult
    {
        public List<SurfaceSummary> Surfaces { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class SurfaceSummary
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string SurfaceType { get; set; } = string.Empty;
        public int? PointCount { get; set; }
        public int? TriangleCount { get; set; }
        public double? MinElevation { get; set; }
        public double? MaxElevation { get; set; }
        public double? MeanElevation { get; set; }
        public double? Area2D { get; set; }
        public double? Area3D { get; set; }
    }

    #endregion
}
