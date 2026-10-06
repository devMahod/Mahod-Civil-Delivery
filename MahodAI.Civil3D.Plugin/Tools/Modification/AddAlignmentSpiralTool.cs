using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using CivilSpiralType = Autodesk.Civil.SpiralType;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Adds spiral transitions to an alignment curve element. Supports both:
    /// (a) modifying spiral_in / spiral_out lengths on an existing SCS, and
    /// (b) converting a plain Arc to a SpiralCurveSpiral (SCS) by replacing
    ///     the Arc in place with a free SCS that keeps the same radius and
    ///     binds to the same adjacent tangents.
    /// </summary>
    public class AddAlignmentSpiralTool : DrawingToolBase
    {
        public override string Name => "add_alignment_spiral";
        public override string Description =>
            "Adds spiral transitions to an alignment curve. " +
            "On an Arc element, converts it to Spiral-Curve-Spiral (SCS) by removing the arc " +
            "and inserting a free SCS with the same radius between the two adjacent tangents. " +
            "On an existing SCS element, modifies the entry (spiral_in) or exit (spiral_out) length. " +
            "Optional ``target_station`` lets the plugin re-resolve a stale element_index against " +
            "current geometry — earlier fixes in the same batch can renumber entities.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var curveElementIndex = GetIntParam(parameters, "curve_element_index")
                                    ?? GetIntParam(parameters, "element_index");
            var spiralType = GetStringParam(parameters, "spiral_type")?.ToLowerInvariant() ?? "in";
            var length = GetDoubleParam(parameters, "length");
            // Optional station hint. Same pattern as ModifyAlignmentCurveRadiusTool —
            // lets the plugin re-resolve element_index when the agent's index is
            // stale (out of range, or pointing at the wrong entity type).
            var targetStation = GetDoubleParam(parameters, "target_station");

            if (curveElementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'curve_element_index' is missing");
            if (length == null || length.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'length' must be a positive number");
            if (spiralType != "in" && spiralType != "out" && spiralType != "both")
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Parameter 'spiral_type' must be 'in', 'out', or 'both'");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            int idx = curveElementIndex.Value;
            bool resolvedByStation = false;
            string? resolutionNote = null;

            if (targetStation.HasValue)
            {
                bool indexInvalid =
                    idx < 0
                    || idx >= alignment.Entities.Count
                    || !IsSpiralable(alignment.Entities[idx].EntityType);
                if (indexInvalid)
                {
                    int? stationIdx = FindSpiralableAtStation(alignment, targetStation.Value);
                    if (stationIdx.HasValue)
                    {
                        resolutionNote = $"index {idx} was stale; re-resolved to {stationIdx.Value} via station {targetStation.Value:F2}";
                        idx = stationIdx.Value;
                        resolvedByStation = true;
                    }
                }
            }

            if (idx < 0 || idx >= alignment.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element index {idx} out of range (0-{alignment.Entities.Count - 1})" +
                    (targetStation.HasValue ? $" and no spiralable curve found at target_station {targetStation.Value:F2}" : ""));

            var entity = alignment.Entities[idx];

            switch (entity.EntityType)
            {
                case CivilDb.AlignmentEntityType.Arc:
                {
                    // Arc → SCS conversion: capture context, remove the arc,
                    // call AddFreeSCS between the original neighbour tangents.
                    // SAFETY CONTRACT: every failure path below returns
                    // ToolResult.Fail — ToolExecutor aborts the transaction on
                    // Success=false, so the arc removal (and any other partial
                    // mutation) is rolled back and "no geometry was changed" is
                    // guaranteed true. Never convert a solver failure into a
                    // success-ish return here: that would commit the deletion.
                    var arc = entity as CivilDb.AlignmentArc;
                    if (arc == null)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentArc");

                    double radius = arc.Radius;
                    double oldStart = arc.StartStation;
                    double oldEnd = arc.EndStation;

                    // Locate the two adjacent entities. We need their EntityIds
                    // because Entities.Remove(arc) invalidates index-based lookups
                    // before the AddFreeSCS call.
                    //
                    // 2026-06-11: the former "both neighbours must be Line" gate
                    // was a self-imposed restriction, not an API one. Per the
                    // official Civil 3D docs (About Adding Spiral-Curve-Spiral
                    // Groups, 2027 GUID-BCA4F73E), a free SCS group is valid
                    // between two tangents, between a curve and a tangent
                    // (compound spiral on the curve side), AND between two
                    // curves (two compound spirals) — the geometric solver
                    // decides feasibility (no solution for crossing/concentric
                    // curves, in which case Civil 3D throws and we report it
                    // honestly). This unblocks compound-curve chains like
                    // alignment 73 elements 13-17 (2026-06-09 session: 6
                    // plugin-side rejections that were then papered over by a
                    // radius-inflation fallback agent-side — both halves of
                    // that failure mode are now gone).
                    if (idx == 0 || idx >= alignment.Entities.Count - 1)
                    {
                        return ToolResult.Fail(ToolErrorCodes.NotSupported,
                            $"Arc at index {idx} is at the alignment boundary — automatic spiral insertion " +
                            "needs a neighbouring entity on each side. No geometry was changed; " +
                            "add the boundary spiral manually (float spiral on the open end).");
                    }

                    var prev = alignment.Entities[idx - 1];
                    var next = alignment.Entities[idx + 1];
                    string prevType = prev.EntityType.ToString();
                    string nextType = next.EntityType.ToString();

                    int prevLineId = (int)prev.EntityId;
                    int nextLineId = (int)next.EntityId;

                    double spiralIn = length.Value;
                    double spiralOut = length.Value;

                    try
                    {
                        alignment.Entities.Remove(arc);
                    }
                    catch (Exception ex)
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Failed to remove Arc at index {idx} before SCS insertion: {ex.Message}. " +
                            "No geometry was changed.");
                    }

                    try
                    {
                        alignment.Entities.AddFreeSCS(
                            prevLineId, nextLineId,
                            spiralIn, spiralOut,
                            CivilDb.SpiralParamType.Length,
                            radius,
                            isGreaterThan180: false,
                            CivilSpiralType.Clothoid);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception acEx)
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Civil 3D rejected SCS insertion (radius={radius:F1}m, spiral={length.Value:F1}m, " +
                            $"prev={prevType}, next={nextType}) " +
                            $"on alignment '{alignmentName}' element {idx}: {acEx.Message}. " +
                            "The adjacent geometry cannot absorb the requested spiral (curve neighbours " +
                            "must form a compound, non-crossing configuration) — " +
                            "try a shorter spiral length or edit the geometry manually. No geometry was changed.");
                    }
                    catch (Exception ex)
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"SCS insertion failed (radius={radius:F1}m, spiral={length.Value:F1}m, " +
                            $"prev={prevType}, next={nextType}) " +
                            $"on alignment '{alignmentName}': {ex.Message}. No geometry was changed.");
                    }

                    cache.RemoveByPattern("get_alignment_geometry:");
                    cache.RemoveByPattern("validate_alignment:");

                    return await Task.FromResult(ToolResult.Ok(new
                    {
                        alignment_name = alignmentName,
                        element_index = idx,
                        result_type = "arc_converted_to_scs",
                        resolved_by_station = resolvedByStation,
                        resolution_note = resolutionNote,
                        prev_entity_type = prevType,
                        next_entity_type = nextType,
                        old_entity_type = "Arc",
                        new_entity_type = "SpiralCurveSpiral",
                        spiral_in_length = Math.Round(spiralIn, 3),
                        spiral_out_length = Math.Round(spiralOut, 3),
                        spiral_length = Math.Round(length.Value, 3),
                        curve_radius = Math.Round(radius, 3),
                        original_start_station = Math.Round(oldStart, 3),
                        original_end_station = Math.Round(oldEnd, 3),
                    }));
                }

                case CivilDb.AlignmentEntityType.SpiralCurveSpiral:
                {
                    var scs = entity as CivilDb.AlignmentSCS;
                    if (scs == null)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentSCS");

                    double radius = scs.Arc.Radius;
                    double oldIn = scs.SpiralIn.Length;
                    double oldOut = scs.SpiralOut.Length;

                    try
                    {
                        if (spiralType == "in")
                        {
                            scs.SpiralIn.Length = length.Value;
                        }
                        else if (spiralType == "out")
                        {
                            scs.SpiralOut.Length = length.Value;
                        }
                        else // "both"
                        {
                            scs.SpiralIn.Length = length.Value;
                            scs.SpiralOut.Length = length.Value;
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception acEx)
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Civil 3D rejected spiral length {length.Value:F1}m on SCS element {idx} " +
                            $"of alignment '{alignmentName}': {acEx.Message}. " +
                            "The adjacent tangent length cannot absorb this value. No geometry was changed.");
                    }

                    cache.RemoveByPattern("get_alignment_geometry:");
                    cache.RemoveByPattern("validate_alignment:");

                    return await Task.FromResult(ToolResult.Ok(new
                    {
                        alignment_name = alignmentName,
                        element_index = idx,
                        result_type = "spiral_modified_on_scs",
                        resolved_by_station = resolvedByStation,
                        resolution_note = resolutionNote,
                        spiral_type = spiralType,
                        old_spiral_in_length = Math.Round(oldIn, 3),
                        old_spiral_out_length = Math.Round(oldOut, 3),
                        new_spiral_length = Math.Round(length.Value, 3),
                        curve_radius = Math.Round(radius, 3),
                    }));
                }

                default:
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"Element at index {idx} is {entity.EntityType}, not an Arc or SpiralCurveSpiral");
            }
        }

        private static bool IsSpiralable(CivilDb.AlignmentEntityType t)
            => t == CivilDb.AlignmentEntityType.Arc
            || t == CivilDb.AlignmentEntityType.SpiralCurveSpiral;

        /// <summary>
        /// Find the index of an Arc or SCS whose station range contains
        /// <paramref name="station"/>. AlignmentEntity is abstract and doesn't
        /// expose Start/EndStation at that level — we pull them via the
        /// concrete types we care about.
        /// </summary>
        private static int? FindSpiralableAtStation(CivilDb.Alignment alignment, double station)
        {
            const double tolerance = 1.0;
            for (int i = 0; i < alignment.Entities.Count; i++)
            {
                var e = alignment.Entities[i];
                double start, end;
                if (e is CivilDb.AlignmentArc arc)
                {
                    start = arc.StartStation;
                    end = arc.EndStation;
                }
                else if (e is CivilDb.AlignmentSCS scs)
                {
                    start = scs.StartStation;
                    end = scs.EndStation;
                }
                else
                {
                    continue;
                }
                if (start - tolerance <= station && station <= end + tolerance)
                    return i;
            }
            return null;
        }
    }
}
