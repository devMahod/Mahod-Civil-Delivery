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
    /// Modifies the elevation of a point on a TIN surface.
    /// </summary>
    public class ModifySurfacePointElevationTool : DrawingToolBase
    {
        public override string Name => "modify_surface_point_elevation";
        public override string Description => "Modifies the elevation of a specific point on a TIN surface. Finds the nearest surface vertex to the given X,Y coordinates and adjusts its elevation.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

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
            var newElevation = GetDoubleParam(parameters, "new_elevation");

            if (!x.HasValue || !y.HasValue)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "X and Y coordinates are required");
            if (newElevation == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_elevation' is missing");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the surface
            TinSurface? tinSurface = null;
            ObjectId surfaceObjId = ObjectId.Null;
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                if (obj != null && obj.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    tinSurface = obj as TinSurface;
                    surfaceObjId = id;
                    break;
                }
            }

            if (tinSurface == null)
            {
                // Check if surface exists but is not TIN
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (obj != null && obj.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResult.Fail(ToolErrorCodes.NotSupported,
                            $"Surface '{surfaceName}' is not a TIN surface. Only TIN surfaces support point elevation modification.");
                    }
                }
                return ToolResult.NotFound("Surface", surfaceName);
            }

            // Get current elevation at the point
            double? oldElevation = null;
            try
            {
                oldElevation = tinSurface.FindElevationAtXY(x.Value, y.Value);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ModifySurfacePointElevation get old elevation: {ex.Message}");
            }

            // Open surface for write and modify
            tinSurface = tr.GetObject(surfaceObjId, OpenMode.ForWrite) as TinSurface;
            if (tinSurface == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open surface for write");

            try
            {
                // Find the nearest vertex and modify its elevation
                var triangle = tinSurface.FindTriangleAtXY(x.Value, y.Value);
                if (triangle == null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "Point is outside surface boundary. Cannot find triangle at the specified coordinates.");

                // Find the closest vertex of the triangle
                var v1 = triangle.Vertex1;
                var v2 = triangle.Vertex2;
                var v3 = triangle.Vertex3;

                double d1 = Math.Sqrt(Math.Pow(v1.Location.X - x.Value, 2) + Math.Pow(v1.Location.Y - y.Value, 2));
                double d2 = Math.Sqrt(Math.Pow(v2.Location.X - x.Value, 2) + Math.Pow(v2.Location.Y - y.Value, 2));
                double d3 = Math.Sqrt(Math.Pow(v3.Location.X - x.Value, 2) + Math.Pow(v3.Location.Y - y.Value, 2));

                TinSurfaceVertex nearestVertex;
                double nearestDist;

                if (d1 <= d2 && d1 <= d3)
                {
                    nearestVertex = v1;
                    nearestDist = d1;
                }
                else if (d2 <= d3)
                {
                    nearestVertex = v2;
                    nearestDist = d2;
                }
                else
                {
                    nearestVertex = v3;
                    nearestDist = d3;
                }

                double vertexOldElevation = nearestVertex.Location.Z;

                // Modify vertex elevation by adding a point operation
                var newPoint3d = new Autodesk.AutoCAD.Geometry.Point3d(
                    nearestVertex.Location.X, nearestVertex.Location.Y, newElevation.Value);
                var addPointMethod = tinSurface.GetType().GetMethod("AddPoint",
                    new[] { typeof(Autodesk.AutoCAD.Geometry.Point3d) });
                if (addPointMethod != null)
                    addPointMethod.Invoke(tinSurface, new object[] { newPoint3d });
                else
                    throw new InvalidOperationException("AddPoint method not available on TinSurface in this Civil 3D version");

                cache.RemoveByPattern("get_surface_elevation:");
                cache.RemoveByPattern("get_surface_info:");
                cache.RemoveByPattern("sample_surface_profile:");

                return await Task.FromResult(ToolResult.Ok(new
                {
                    surface_name = surfaceName,
                    x = Math.Round(x.Value, 3),
                    y = Math.Round(y.Value, 3),
                    nearest_vertex_x = Math.Round(nearestVertex.Location.X, 3),
                    nearest_vertex_y = Math.Round(nearestVertex.Location.Y, 3),
                    nearest_vertex_distance = Math.Round(nearestDist, 3),
                    old_elevation = vertexOldElevation,
                    interpolated_old_elevation = oldElevation.HasValue ? Math.Round(oldElevation.Value, 3) : (double?)null,
                    new_elevation = Math.Round(newElevation.Value, 3)
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify surface point: {ex.Message}");
            }
        }
    }
}
