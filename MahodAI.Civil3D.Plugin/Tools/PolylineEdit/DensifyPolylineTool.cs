using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Adds vertices to a polyline without clicking: PLTOOLS <c>PL-DIV</c> (split one segment),
    /// <c>PL-DIVALL</c> (split every segment), <c>PL-VFI</c> (a vertex at every intersection with
    /// other curves) and MAHOD-PL <c>VDIST</c> (a vertex every N metres along the whole polyline —
    /// the Civil-style station spacing, without needing Civil objects).
    /// </summary>
    public class DensifyPolylineTool : DrawingToolBase
    {
        public override string Name => "densify_polyline";

        public override string Description =>
            "Adds vertices to a polyline. by='distance' puts a vertex every `distance` units of length " +
            "along the whole polyline; by='count' splits each segment into `count` equal parts (pass " +
            "segment_index to split just one segment); by='segment_spacing' splits each segment every " +
            "`distance` units; by='intersections' inserts a vertex wherever the polyline crosses the " +
            "curves named in intersect_with_handles. Arc segments keep their radius (the bulge is split " +
            "correctly) and width tapers are interpolated. Returns how many vertices were added.";

        public override string Category => ToolCategories.Modification;

        /// <summary>Long enough for the engineer to pick the polyline when none was named.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Prompts for a pick when the agent named no target.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"", ""description"": ""Hex handle of the polyline."" },
                ""by"": {
                    ""type"": ""string"",
                    ""enum"": [""distance"", ""count"", ""segment_spacing"", ""intersections""],
                    ""description"": ""How to place the new vertices.""
                },
                ""distance"": { ""type"": ""number"", ""description"": ""Spacing for by='distance' / by='segment_spacing'."" },
                ""count"": { ""type"": ""integer"", ""description"": ""Number of equal parts for by='count' (2 or more)."" },
                ""segment_index"": { ""type"": ""integer"", ""description"": ""0-based segment to work on. Omit to apply to every segment."" },
                ""intersect_with_handles"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Hex handles of the curves to intersect with (by='intersections').""
                },
                ""max_offset"": { ""type"": ""number"", ""description"": ""by='intersections': how far off the polyline an intersection point may be and still count. Default 1e-6."" }
            },
            ""required"": [""entity_handle"", ""by""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            // No handle from the agent → use what the engineer selected, or ask.
            var id = PolylineToolSupport.ResolveSingleTargetOrPrompt(
                tr, db, parameters, "בחרי פוליליין להוספת קודקודים:", out var targetSource);
            if (id.IsNull)
            {
                return Task.FromResult(targetSource == PolylineToolSupport.TargetSource.Cancelled
                    ? ToolResult.Cancelled(new { cancelled = true, vertices_added = 0 })
                    : ToolResult.Fail(ToolErrorCodes.ObjectNotFound, "לא נבחר פוליליין"));
            }
            var handle = PolylineToolSupport.HandleOf(tr, id);

            var by = (GetStringParam(parameters, "by") ?? string.Empty).Trim().ToLowerInvariant();
            if (by is not ("distance" or "count" or "segment_spacing" or "intersections"))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"by לא נתמך: '{by}'. אפשרויות: distance, count, segment_spacing, intersections"));

            if (!PolylineAdapter.TryOpen(tr, id, out var target, out var errCode, out var errMsg))
                return Task.FromResult(ToolResult.Fail(errCode ?? ToolErrorCodes.ExecutionFailed,
                    errMsg ?? "לא ניתן לערוך את האובייקט"));

            var shape = target!.Shape;
            int before = shape.Count;
            int? segmentIndex = GetIntParam(parameters, "segment_index");
            PlEditResult result;
            var warnings = new List<string>();

            switch (by)
            {
                case "distance":
                {
                    var distance = GetDoubleParam(parameters, "distance");
                    if (!distance.HasValue || distance.Value <= 0)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק distance גדול מאפס"));
                    result = VertexDensifier.ByDistance(shape, distance.Value);
                    break;
                }

                case "segment_spacing":
                {
                    var distance = GetDoubleParam(parameters, "distance");
                    if (!distance.HasValue || distance.Value <= 0)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק distance גדול מאפס"));
                    result = VertexDensifier.BySegmentSpacing(shape, distance.Value, segmentIndex);
                    break;
                }

                case "count":
                {
                    var count = GetIntParam(parameters, "count");
                    if (!count.HasValue || count.Value < 2)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק count של 2 ומעלה"));
                    result = VertexDensifier.ByCount(shape, count.Value, segmentIndex);
                    break;
                }

                default:
                {
                    var others = GetStringArrayParam(parameters, "intersect_with_handles");
                    if (others == null || others.Length == 0)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק intersect_with_handles"));

                    var points = CollectIntersections(tr, db, id, others, target, warnings);
                    if (points.Count == 0)
                        return Task.FromResult(ToolResult.Ok(new
                        {
                            entity_handle = handle,
                            by,
                            vertices_before = before,
                            vertices_after = before,
                            vertices_added = 0,
                            intersections_found = 0,
                            notes = new[] { "לא נמצאו נקודות חיתוך" },
                            warnings,
                        }));

                    double maxOffset = GetDoubleParam(parameters, "max_offset") ?? 1e-6;
                    result = VertexDensifier.AtPoints(shape, points, maxOffset);
                    break;
                }
            }

            if (!result.Changed)
            {
                return Task.FromResult(ToolResult.Ok(new
                {
                    entity_handle = handle,
                    by,
                    vertices_before = before,
                    vertices_after = shape.Count,
                    vertices_added = 0,
                    notes = result.Notes,
                    warnings,
                }));
            }

            if (!PolylineAdapter.TryWrite(tr, target, out var writeErr))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    writeErr ?? "עדכון הפוליליין נכשל"));

            return Task.FromResult(ToolResult.Ok(new
            {
                entity_handle = handle,
                entity_type = target.HebrewKind,
                by,
                vertices_before = before,
                vertices_after = shape.Count,
                vertices_added = result.Added,
                notes = result.Notes,
                warnings,
            }));
        }

        /// <summary>
        /// PL-VFI's core: every intersection between the target curve and the named curves,
        /// converted into the target's own planar coordinates.
        /// </summary>
        private static List<Pt2> CollectIntersections(
            Transaction tr,
            Database db,
            ObjectId targetId,
            IReadOnlyList<string> otherHandles,
            PolylineTarget target,
            List<string> warnings)
        {
            var result = new List<Pt2>();
            if (tr.GetObject(targetId, OpenMode.ForRead) is not Curve targetCurve)
            {
                warnings.Add("האובייקט אינו עקומה שניתן לחתוך");
                return result;
            }

            foreach (var h in otherHandles)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                if (!PolylineToolSupport.TryResolveHandle(db, h, out var otherId))
                {
                    warnings.Add($"handle לא נמצא: {h}");
                    continue;
                }
                if (tr.GetObject(otherId, OpenMode.ForRead) is not Curve other)
                {
                    warnings.Add($"{h}: אינו עקומה");
                    continue;
                }

                var pts = new Point3dCollection();
                try
                {
                    targetCurve.IntersectWith(other, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    warnings.Add($"{h}: חישוב החיתוך נכשל ({ex.Message})");
                    continue;
                }

                foreach (Point3d p in pts) result.Add(target.ToLocal(p));
            }

            return result;
        }
    }
}
