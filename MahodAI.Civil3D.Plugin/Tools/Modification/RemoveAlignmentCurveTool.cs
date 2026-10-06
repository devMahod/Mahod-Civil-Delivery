using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Removes an unnecessary horizontal curve — an Arc or a full
    /// Spiral-Curve-Spiral (SCS) group — so the two adjoining tangents meet at
    /// the PI (a plain angle point; valid alignment state, the inverse of
    /// <see cref="InsertAlignmentCurveTool"/>). Paired with the analyze step's
    /// Table 5.5 minimum-deflection check ("curve unnecessary").
    /// </summary>
    public class RemoveAlignmentCurveTool : DrawingToolBase
    {
        // Tangent endpoints closer than this already meet at the PI
        // (free-curve case: fixed lines are defined PI-to-PI and the free
        // curve only trimmed them — Remove alone restores the angle point).
        // Float-noise scale only: any REAL residual chord must take the
        // reconstruction branch, or the tool would commit an alignment whose
        // tangents never actually join.
        private const double GapToleranceMeters = 1e-4;

        // A reconstructed PI farther than this from either outer endpoint is
        // rejected — with near-parallel tangents a small direction error can
        // throw the intersection kilometres away.
        private const double MaxPiDistanceMeters = 10_000.0;

        public override string Name => "remove_alignment_curve";
        public override string Description =>
            "Removes an unnecessary horizontal curve from an alignment — a plain Arc or a full " +
            "Spiral-Curve-Spiral (SCS) group flagged by the Table 5.5 minimum-deflection check — " +
            "and extends the two adjacent tangents so they meet at the PI (plain angle point). " +
            "Refuses when the tangent-to-tangent deflection exceeds ``max_deflection_deg`` " +
            "(default 1.0°, the loosest Table 5.5 value): removing a genuinely needed curve " +
            "would create an illegal angle break. " +
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
            var elementIndex = GetIntParam(parameters, "element_index");
            // Optional station hint. Same pattern as ModifyAlignmentCurveRadiusTool —
            // lets the plugin re-resolve element_index when the agent's index is
            // stale (out of range, or pointing at the wrong entity type).
            var targetStation = GetDoubleParam(parameters, "target_station");
            // Safety guard: a curve whose tangent-to-tangent deflection exceeds
            // this is geometrically required — removal is refused. 1.0° is the
            // loosest value in Table 5.5.
            var maxDeflectionDeg = GetDoubleParam(parameters, "max_deflection_deg") ?? 1.0;

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");
            if (maxDeflectionDeg <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Parameter 'max_deflection_deg' must be a positive number");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            int idx = elementIndex.Value;
            bool resolvedByStation = false;
            string? resolutionNote = null;

            if (targetStation.HasValue)
            {
                bool indexInvalid =
                    idx < 0
                    || idx >= alignment.Entities.Count
                    || !IsRemovableCurve(alignment.Entities[idx].EntityType);
                if (indexInvalid)
                {
                    int? stationIdx = FindRemovableCurveAtStation(alignment, targetStation.Value);
                    if (stationIdx.HasValue)
                    {
                        resolutionNote = $"index {idx} was stale (wrong entity type / out of range); re-resolved to {stationIdx.Value} via station {targetStation.Value:F2}";
                        idx = stationIdx.Value;
                        resolvedByStation = true;
                    }
                }
            }

            if (idx < 0 || idx >= alignment.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element index {idx} out of range (0-{alignment.Entities.Count - 1})" +
                    (targetStation.HasValue ? $" and no Arc/SCS found at target_station {targetStation.Value:F2}" : ""));

            var entity = alignment.Entities[idx];
            if (!IsRemovableCurve(entity.EntityType))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element at index {idx} is {entity.EntityType}, not an Arc or SpiralCurveSpiral");

            // SAFETY CONTRACT: every failure path below returns ToolResult.Fail —
            // ToolExecutor commits the transaction ONLY on Success=true and
            // aborts it otherwise, so any partial mutation (curve removed,
            // tangents removed but not re-added) is rolled back and "no geometry
            // was changed" stays true. Never convert a solver failure into a
            // success-ish return here: that would commit a broken alignment.

            if (idx == 0 || idx >= alignment.Entities.Count - 1)
                return ToolResult.Fail(ToolErrorCodes.NotSupported,
                    $"Curve at index {idx} is at the alignment boundary — removal needs a " +
                    "tangent on each side to re-join at the PI. No geometry was changed.");

            var prev = alignment.Entities[idx - 1];
            var next = alignment.Entities[idx + 1];
            if (prev.EntityType != CivilDb.AlignmentEntityType.Line
                || next.EntityType != CivilDb.AlignmentEntityType.Line)
                return ToolResult.Fail(ToolErrorCodes.NotSupported,
                    $"Curve at index {idx} (prev={prev.EntityType}, next={next.EntityType}) " +
                    "is not flanked by two tangents — compound curve chains are not supported " +
                    "for removal. No geometry was changed.");

            var prevLine = prev as CivilDb.AlignmentLine;
            var nextLine = next as CivilDb.AlignmentLine;
            if (prevLine == null || nextLine == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast neighbour entities to AlignmentLine");

            // AlignmentLine.Direction is radians — same convention
            // ValidateAlignmentTool converts to degrees for tangent bearings.
            double prevDir = prevLine.Direction;
            double nextDir = nextLine.Direction;
            double deflectionDeg = CurveRemovalGeometry.DeflectionDegrees(prevDir, nextDir);
            // 1e-6° slack: the flagship case sits EXACTLY at the Table 5.5
            // threshold (0.25° cap vs 0.25° measured) — a strict compare
            // would refuse it on accumulated double noise.
            if (deflectionDeg > maxDeflectionDeg + 1e-6)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Tangent-to-tangent deflection {deflectionDeg:F3}° exceeds max_deflection_deg {maxDeflectionDeg:F3}° " +
                    $"on alignment '{alignmentName}' element {idx} — the curve is geometrically " +
                    "required and cannot be removed. No geometry was changed.");

            // Neighbour EntityIds and outer endpoints captured BEFORE Remove:
            // Entities.Remove(curve) invalidates index-based lookups, and the
            // re-added tangents need the original outer endpoints.
            int prevLineId = (int)prev.EntityId;
            int nextLineId = (int)next.EntityId;
            double prevOuterStartX = prevLine.StartPoint.X;
            double prevOuterStartY = prevLine.StartPoint.Y;
            double nextOuterEndX = nextLine.EndPoint.X;
            double nextOuterEndY = nextLine.EndPoint.Y;

            string removedType;
            double oldRadius;
            double curveLengthRemoved;
            if (entity is CivilDb.AlignmentArc arcEntity)
            {
                removedType = "Arc";
                oldRadius = arcEntity.Radius;
                curveLengthRemoved = arcEntity.Length;
            }
            else if (entity is CivilDb.AlignmentSCS scsEntity)
            {
                removedType = "SpiralCurveSpiral";
                oldRadius = scsEntity.Arc.Radius;
                curveLengthRemoved = scsEntity.Length;
            }
            else
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to cast entity at index {idx} ({entity.EntityType}) to AlignmentArc/AlignmentSCS");
            }

            int entityCountBefore = alignment.Entities.Count;
            double? oldLength = null;
            try { oldLength = alignment.Length; } catch { }

            // SCS is ONE compound sub-entity — a single Remove drops the whole
            // spiral-curve-spiral group.
            try
            {
                alignment.Entities.Remove(entity);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to remove {removedType} at index {idx} on alignment '{alignmentName}': {ex.Message}. " +
                    "No geometry was changed.");
            }

            var prevAfter = FindLineByEntityId(alignment, prevLineId);
            var nextAfter = FindLineByEntityId(alignment, nextLineId);
            if (prevAfter == null || nextAfter == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"An adjacent tangent vanished when the {removedType} was removed (cascade removal) — " +
                    $"alignment '{alignmentName}' cannot be re-joined automatically. " +
                    "No geometry was changed (transaction aborted).");

            bool reconstructedTangents = false;
            CivilDb.AlignmentLine piLine = prevAfter;

            double gapX = nextAfter.StartPoint.X - prevAfter.EndPoint.X;
            double gapY = nextAfter.StartPoint.Y - prevAfter.EndPoint.Y;
            double gap = Math.Sqrt(gapX * gapX + gapY * gapY);

            if (gap > GapToleranceMeters)
            {
                // Fixed-arc case: the tangents end at the old PC/PT and must be
                // replaced by two lines meeting at the PI.
                // AlignmentLine.StartPoint/EndPoint/Length are READ-ONLY in the
                // managed API (see ModifyAlignmentTangentLengthTool) — tangent
                // extension goes through Remove + AddFixedLine, never property
                // writes.
                if (!CurveRemovalGeometry.TryIntersect(
                        prevOuterStartX, prevOuterStartY, prevDir,
                        nextOuterEndX, nextOuterEndY, nextDir,
                        out double piX, out double piY))
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"The adjacent tangents are parallel (deflection {deflectionDeg:F4}°) — no PI exists " +
                        $"on alignment '{alignmentName}'. No geometry was changed (transaction aborted).");

                // With deflection ≤ max_deflection_deg (~1°) the tangents are
                // near-parallel — the intersection exists but must lie between
                // the two outer endpoints (forward of prev's start, behind
                // next's end) and within MaxPiDistanceMeters of each.
                bool forwardOfPrevStart = CurveRemovalGeometry.IsForwardOf(
                    prevOuterStartX, prevOuterStartY, prevDir, piX, piY);
                bool behindNextEnd = CurveRemovalGeometry.IsForwardOf(
                    piX, piY, nextDir, nextOuterEndX, nextOuterEndY);
                double distFromPrevStart = Distance(prevOuterStartX, prevOuterStartY, piX, piY);
                double distToNextEnd = Distance(piX, piY, nextOuterEndX, nextOuterEndY);
                if (!forwardOfPrevStart || !behindNextEnd
                    || distFromPrevStart > MaxPiDistanceMeters || distToNextEnd > MaxPiDistanceMeters)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Computed PI ({piX:F3}, {piY:F3}) fails the position sanity check " +
                        $"(forward_of_prev={forwardOfPrevStart}, behind_next={behindNextEnd}, " +
                        $"dist_prev={distFromPrevStart:F1}m, dist_next={distToNextEnd:F1}m, " +
                        $"limit={MaxPiDistanceMeters:F0}m) on alignment '{alignmentName}'. " +
                        "No geometry was changed (transaction aborted).");

                try
                {
                    alignment.Entities.Remove(prevAfter);
                    alignment.Entities.Remove(nextAfter);
                    piLine = alignment.Entities.AddFixedLine(
                        new Point3d(prevOuterStartX, prevOuterStartY, 0),
                        new Point3d(piX, piY, 0));
                    alignment.Entities.AddFixedLine(
                        new Point3d(piX, piY, 0),
                        new Point3d(nextOuterEndX, nextOuterEndY, 0));
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Tangent reconstruction through the PI failed on alignment '{alignmentName}': {ex.Message}. " +
                        "No geometry was changed (transaction aborted).");
                }
                reconstructedTangents = true;
            }

            // Reading Length/EndingStation forces a geometry solve — a broken
            // chain surfaces here as Fail (and abort) instead of at commit time.
            double newLength;
            try
            {
                newLength = alignment.Length;
                _ = alignment.EndingStation;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Alignment '{alignmentName}' failed to solve after curve removal: {ex.Message}. " +
                    "No geometry was changed (transaction aborted).");
            }

            // For a tiny deflection the two tangent legs through the PI are
            // near-equal to the removed curve length (2·R·tan(Δ/2) ≈ R·Δ), so
            // the alignment length must not move by more than the removed
            // curve's own length — a bigger jump means Civil 3D re-chained
            // the reconstructed lines wrong (e.g. out of sequence).
            if (oldLength.HasValue
                && Math.Abs(newLength - oldLength.Value) > Math.Max(1.0, curveLengthRemoved))
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Alignment length jumped {oldLength.Value:F2} -> {newLength:F2} after curve removal " +
                    $"on '{alignmentName}' — geometry re-chained incorrectly. " +
                    "No geometry was changed (transaction aborted).");

            // Net entity delta must be exactly -1: curve removed (tangents
            // already meeting), or curve + 2 tangents removed and 2 re-added.
            int entityCountAfter = alignment.Entities.Count;
            if (entityCountAfter != entityCountBefore - 1)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Entity count after removal is {entityCountAfter}, expected {entityCountBefore - 1} " +
                    $"on alignment '{alignmentName}' — unexpected cascade change. " +
                    "No geometry was changed (transaction aborted).");

            double? piStation = null;
            try { piStation = piLine.EndStation; } catch { }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("validate_alignment:");
            cache.RemoveByPattern("list_alignments:");
            cache.RemoveByPattern("get_drawing_summary:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                element_index = idx,
                resolved_by_station = resolvedByStation,
                resolution_note = resolutionNote,
                removed_entity_type = removedType,
                old_radius = Math.Round(oldRadius, 3),
                curve_length_removed = Math.Round(curveLengthRemoved, 3),
                deflection_deg = Math.Round(deflectionDeg, 3),
                reconstructed_tangents = reconstructedTangents,
                old_length = oldLength.HasValue ? Math.Round(oldLength.Value, 3) : (double?)null,
                new_length = Math.Round(newLength, 3),
                entity_count_before = entityCountBefore,
                entity_count_after = entityCountAfter,
                pi_station = piStation.HasValue ? Math.Round(piStation.Value, 3) : (double?)null,
            }));
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool IsRemovableCurve(CivilDb.AlignmentEntityType t)
            => t == CivilDb.AlignmentEntityType.Arc
            || t == CivilDb.AlignmentEntityType.SpiralCurveSpiral;

        /// <summary>
        /// Re-locates a Line sub-entity by EntityId after a Remove — index-based
        /// lookups are stale at that point.
        /// </summary>
        private static CivilDb.AlignmentLine? FindLineByEntityId(CivilDb.Alignment alignment, int entityId)
        {
            foreach (CivilDb.AlignmentEntity e in alignment.Entities)
            {
                if (e is CivilDb.AlignmentLine line && (int)e.EntityId == entityId)
                    return line;
            }
            return null;
        }

        /// <summary>
        /// Finds the index of the Arc/SCS element whose station range contains
        /// <paramref name="station"/>. AlignmentEntity is abstract and doesn't
        /// expose StartStation/EndStation at that level — pull them via the
        /// concrete types this tool removes.
        /// </summary>
        private static int? FindRemovableCurveAtStation(CivilDb.Alignment alignment, double station)
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
