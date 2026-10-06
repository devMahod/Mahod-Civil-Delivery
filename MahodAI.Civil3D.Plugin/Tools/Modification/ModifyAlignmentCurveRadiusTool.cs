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
    /// Modifies the radius of an alignment curve (Arc or SCS arc sub-entity).
    /// </summary>
    public class ModifyAlignmentCurveRadiusTool : DrawingToolBase
    {
        public override string Name => "modify_alignment_curve_radius";
        public override string Description =>
            "Modifies the radius of a horizontal curve in an alignment. Supports simple arcs and SCS (Spiral-Curve-Spiral) compound elements. " +
            "By default a rejected radius is relaxed toward the largest achievable value (partial progress). " +
            "Set exact_only=true to forbid relaxation: when the exact value is not achievable nothing is changed " +
            "and the result is success=false with the best achievable value in 'achievable_radius'.";
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
            var newRadius = GetDoubleParam(parameters, "new_radius");
            // Optional station hint. Lets the plugin re-resolve element_index
            // against current geometry when the agent-provided index is stale
            // (out of range, or pointing at a Line/Spiral). Prior fixes in
            // the same batch can renumber entities.
            var targetStation = GetDoubleParam(parameters, "target_station");
            // exact_only (default false for back-compat): the Stage 2 fix flow
            // sends true — NO partial fixes. When the exact value is rejected we
            // only probe to discover the achievable value, then return Fail so
            // the executor's abort-on-fail rolls the probes back.
            var exactOnly = GetBoolParam(parameters, "exact_only", false);

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");
            if (newRadius == null || newRadius.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_radius' must be a positive number");

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

            // Re-resolve when the agent-provided index is stale.
            if (targetStation.HasValue)
            {
                bool indexInvalid =
                    idx < 0
                    || idx >= alignment.Entities.Count
                    || !IsRadiusEditable(alignment.Entities[idx].EntityType);
                if (indexInvalid)
                {
                    int? stationIdx = FindRadiusElementAtStation(alignment, targetStation.Value);
                    if (stationIdx.HasValue)
                    {
                        resolutionNote = $"index {idx} was stale (wrong entity type / out of range); re-resolved to {stationIdx.Value} via station {targetStation.Value:F2}";
                        idx = stationIdx.Value;
                        resolvedByStation = true;
                    }
                }
            }

            if (idx < 0 || idx >= alignment.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, $"Element index {idx} out of range (0-{alignment.Entities.Count - 1})" +
                    (targetStation.HasValue ? $" and no Arc/SCS found at target_station {targetStation.Value:F2}" : ""));

            var entity = alignment.Entities[idx];
            double oldRadius;

            // Civil 3D throws "Invalid Operation." (eInvalidInput) when the
            // requested radius would force a negative tangent length or
            // otherwise invalidate the adjacent geometry. Strategy:
            //   1. Try the requested radius first.
            //   2. On rejection, search BACKWARD toward the current radius
            //      with a binary-style relaxation (see RadiusAdjustmentSearch).
            // Two modes:
            //   - exact_only=false (chat / legacy): the engineer accepts partial
            //     progress — landing R=750 when they asked for R=900 is reported
            //     as relaxed success.
            //   - exact_only=true (Stage 2 fix flow): NO partial fixes. The same
            //     search runs only to DISCOVER the achievable value; the tool
            //     then returns Fail and the executor aborts the transaction, so
            //     the probe writes are rolled back and the drawing is untouched.
            CivilDb.AlignmentArc? arcRef = null;
            CivilDb.AlignmentSCS? scsRef = null;
            switch (entity.EntityType)
            {
                case CivilDb.AlignmentEntityType.Arc:
                    arcRef = entity as CivilDb.AlignmentArc;
                    if (arcRef == null)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentArc");
                    oldRadius = arcRef.Radius;
                    break;
                case CivilDb.AlignmentEntityType.SpiralCurveSpiral:
                    scsRef = entity as CivilDb.AlignmentSCS;
                    if (scsRef == null)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentSCS");
                    oldRadius = scsRef.Arc.Radius;
                    break;
                default:
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"Element at index {idx} is {entity.EntityType}, not an Arc or SpiralCurveSpiral");
            }

            double requested = newRadius.Value;

            // The geometry write, injected into the pure search logic so it is
            // unit-testable. Returns null on success, rejection message on failure.
            string? TryApplyRadius(double value)
            {
                try
                {
                    if (arcRef != null) arcRef.Radius = value;
                    else if (scsRef != null) scsRef.Arc.Radius = value;
                    return null;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception acEx)
                {
                    return acEx.Message;
                }
                catch (InvalidOperationException invEx)
                {
                    return invEx.Message;
                }
            }

            var outcome = RadiusAdjustmentSearch.Run(oldRadius, requested, TryApplyRadius);

            if (!outcome.ExactApplied && exactOnly)
            {
                // Stage 2 contract: no partial fixes. The probes above may have
                // mutated the radius inside this transaction — returning Fail
                // makes ToolExecutor abort the transaction, discarding them.
                double achievable = outcome.Achieved;
                return new ToolResult
                {
                    Success = false,
                    Error = new ToolExecutionError
                    {
                        Code = ToolErrorCodes.ExecutionFailed,
                        Message = RadiusAdjustmentSearch.BuildExactOnlyFailureMessage(requested, achievable),
                        Details =
                            $"exact_only rejection on element {idx} of alignment '{alignmentName}': " +
                            $"requested R={requested:F3}, best achievable R={achievable:F3} " +
                            $"(probe attempts={outcome.Attempts}; Civil 3D: {outcome.LastError ?? "n/a"}). " +
                            "No geometry was changed (transaction aborted).",
                    },
                    // Diagnostic payload — forwarded to the agent even on failure
                    // (ToolExecutor copies Data into the result payload regardless
                    // of Success). The drawing was NOT modified.
                    Data = new
                    {
                        alignment_name = alignmentName,
                        element_index = idx,
                        element_type = entity.EntityType.ToString(),
                        resolved_by_station = resolvedByStation,
                        resolution_note = resolutionNote,
                        old_radius = Math.Round(oldRadius, 3),
                        requested_radius = Math.Round(requested, 3),
                        achievable_radius = Math.Round(achievable, 3),
                        relaxed = false,
                        exact_only = true,
                        probe_attempts = outcome.Attempts,
                    },
                };
            }

            if (!outcome.ImprovedOverCurrent(oldRadius))
            {
                // No value above the original landed. Echo the original
                // rejection message so the agent can surface the right
                // Hebrew error.
                return ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Radius {requested} change rejected by Civil 3D on element {idx} " +
                    $"of alignment '{alignmentName}': {outcome.LastError ?? "no detail"}. " +
                    "The adjacent geometry cannot accommodate any value above the current radius.");
            }

            cache.RemoveByPattern("get_alignment_geometry:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                element_index = idx,
                element_type = entity.EntityType.ToString(),
                resolved_by_station = resolvedByStation,
                resolution_note = resolutionNote,
                old_radius = Math.Round(oldRadius, 3),
                // ``new_radius`` is what the agent's verifier compares against
                // ``item.proposed_value``. We deliberately report the achieved
                // radius here (which may be less than the requested value
                // when relaxation kicked in) so verification reflects the
                // ACTUAL state of the drawing, not the un-attainable ask.
                new_radius = Math.Round(outcome.Achieved, 3),
                requested_radius = Math.Round(requested, 3),
                relaxed = outcome.Relaxed,
                relaxation_attempts = outcome.Attempts,
                relaxation_note = outcome.Relaxed && Math.Abs(outcome.Achieved - requested) > 1.0
                    ? $"requested R={requested:F1}m did not fit; achieved R={outcome.Achieved:F1}m via partial relaxation (Civil 3D: {outcome.LastError ?? "n/a"})"
                    : (string?)null,
            }));
        }

        private static bool IsRadiusEditable(CivilDb.AlignmentEntityType type)
            => type == CivilDb.AlignmentEntityType.Arc
            || type == CivilDb.AlignmentEntityType.SpiralCurveSpiral;

        /// <summary>
        /// Finds the index of the Arc/SCS element whose station range contains
        /// <paramref name="station"/>. Used to recover from stale element_index
        /// values when prior fixes in the batch renumbered entities.
        ///
        /// AlignmentEntity is abstract and doesn't expose StartStation/EndStation
        /// at that level — each concrete subclass (AlignmentArc, AlignmentSCS, …)
        /// declares them. We pull them via the radius-editable concrete types we
        /// care about: AlignmentArc and AlignmentSCS.
        /// </summary>
        private static int? FindRadiusElementAtStation(CivilDb.Alignment alignment, double station)
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
