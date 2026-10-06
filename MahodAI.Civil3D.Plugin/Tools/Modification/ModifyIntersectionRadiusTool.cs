using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Modifies the curb return radius at an intersection.
    /// </summary>
    public class ModifyIntersectionRadiusTool : DrawingToolBase
    {
        public override string Name => "modify_intersection_radius";
        public override string Description => "Modifies the curb return radius at an intersection. Changes the fillet radius between two intersecting alignments.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var intersectionName = GetStringParam(parameters, "intersection_name");
            var alignmentName1 = GetStringParam(parameters, "alignment_name_1");
            var alignmentName2 = GetStringParam(parameters, "alignment_name_2");
            var cornerIndex = GetIntParam(parameters, "corner_index") ?? 0;
            var newRadius = GetDoubleParam(parameters, "new_radius");

            if (newRadius == null || newRadius.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_radius' must be a positive number");

            if (intersectionName == null && (alignmentName1 == null || alignmentName2 == null))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Must provide either 'intersection_name' or both 'alignment_name_1' and 'alignment_name_2'");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Try to find the intersection object
            CivilDb.Intersection? intersection = null;
            ObjectId intersectionObjId = ObjectId.Null;

            try
            {
                // Search through intersections in the document
                var intersectionIds = civilDoc.GetIntersectionIds();
                foreach (ObjectId id in intersectionIds)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Intersection;
                    if (obj == null) continue;

                    if (intersectionName != null)
                    {
                        if (obj.Name.Equals(intersectionName, StringComparison.OrdinalIgnoreCase))
                        {
                            intersection = obj;
                            intersectionObjId = id;
                            break;
                        }
                    }
                    else
                    {
                        // Match by alignment names
                        try
                        {
                            var mainAlignIdProp = obj.GetType().GetProperty("AlignmentId");
                            var intAlignIdProp = obj.GetType().GetProperty("IntersectingAlignmentId");
                            if (mainAlignIdProp == null || intAlignIdProp == null) continue;
                            var mainAlignId = (ObjectId)mainAlignIdProp.GetValue(obj)!;
                            var intAlignId = (ObjectId)intAlignIdProp.GetValue(obj)!;
                            var mainAlign = tr.GetObject(mainAlignId, OpenMode.ForRead) as CivilDb.Alignment;
                            var intAlign = tr.GetObject(intAlignId, OpenMode.ForRead) as CivilDb.Alignment;

                            if (mainAlign != null && intAlign != null)
                            {
                                bool match1 = (mainAlign.Name.Equals(alignmentName1, StringComparison.OrdinalIgnoreCase) &&
                                              intAlign.Name.Equals(alignmentName2, StringComparison.OrdinalIgnoreCase));
                                bool match2 = (mainAlign.Name.Equals(alignmentName2, StringComparison.OrdinalIgnoreCase) &&
                                              intAlign.Name.Equals(alignmentName1, StringComparison.OrdinalIgnoreCase));

                                if (match1 || match2)
                                {
                                    intersection = obj;
                                    intersectionObjId = id;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (intersection == null)
            {
                string searchKey = intersectionName ?? $"{alignmentName1} x {alignmentName2}";
                return ToolResult.NotFound("Intersection", searchKey);
            }

            // Open intersection for write and modify curb return radius
            intersection = tr.GetObject(intersectionObjId, OpenMode.ForWrite) as CivilDb.Intersection;
            if (intersection == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open intersection for write");

            double? oldRadius = null;
            bool modified = false;

            try
            {
                // Access curb return parameters via reflection for API compatibility
                var curbReturns = intersection.GetType().GetProperty("CurbReturns");
                if (curbReturns != null)
                {
                    var returns = curbReturns.GetValue(intersection);
                    if (returns != null)
                    {
                        var returnType = returns.GetType();
                        var countProp = returnType.GetProperty("Count");
                        if (countProp != null)
                        {
                            int count = (int)countProp.GetValue(returns)!;
                            if (cornerIndex >= 0 && cornerIndex < count)
                            {
                                var indexer = returnType.GetProperty("Item");
                                var item = indexer?.GetValue(returns, new object[] { cornerIndex });
                                if (item != null)
                                {
                                    var radiusProp = item.GetType().GetProperty("Radius");
                                    if (radiusProp != null)
                                    {
                                        oldRadius = (double)radiusProp.GetValue(item)!;
                                        if (radiusProp.CanWrite)
                                        {
                                            radiusProp.SetValue(item, newRadius.Value);
                                            modified = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify intersection radius: {ex.Message}");
            }

            if (!modified)
            {
                return await Task.FromResult(ToolResult.Ok(new
                {
                    intersection_name = intersection.Name,
                    corner_index = cornerIndex,
                    requested_radius = Math.Round(newRadius.Value, 3),
                    note = "Curb return radius not directly modifiable via API. Use Civil 3D Intersection wizard to modify curb return parameters."
                }));
            }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("find_intersections:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                intersection_name = intersection.Name,
                corner_index = cornerIndex,
                old_radius = oldRadius.HasValue ? Math.Round(oldRadius.Value, 3) : (double?)null,
                new_radius = Math.Round(newRadius.Value, 3)
            }));
        }
    }
}
