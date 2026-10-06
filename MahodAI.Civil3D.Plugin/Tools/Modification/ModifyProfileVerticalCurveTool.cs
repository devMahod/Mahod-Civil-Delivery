using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Modifies a vertical curve in a profile (K-value or curve length).
    /// </summary>
    public class ModifyProfileVerticalCurveTool : DrawingToolBase
    {
        public override string Name => "modify_profile_vertical_curve";
        public override string Description =>
            "Modifies a vertical curve in a layout profile. Can change the K-value (or radius, via K=R/100) or curve length for parabolic/circular vertical curves. " +
            "By default a rejected value is relaxed toward the largest achievable value (partial progress) and committed. " +
            "Set exact_only=true to forbid relaxation: when the exact value is not achievable nothing is changed " +
            "and the result is success=false with the best achievable value in 'achievable_radius'/'achievable_k_value'/'achievable_length'.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var elementIndex = GetIntParam(parameters, "element_index");
            var newKValue = GetDoubleParam(parameters, "new_k_value");
            var newLength = GetDoubleParam(parameters, "new_length");
            var newRadius = GetDoubleParam(parameters, "new_radius");
            // exact_only (default false for back-compat): the Stage 2 fix flow
            // sends true — NO partial fixes. When the exact value is rejected we
            // only probe to discover the achievable value, then return Fail so
            // the executor's abort-on-fail rolls the probe writes back and the
            // drawing is left untouched. exact_only=false (chat / legacy) keeps
            // the historical behaviour: commit the best-fit relaxed value.
            var exactOnly = GetBoolParam(parameters, "exact_only", false);
            // Track whether the agent expressed the K-value target in radius
            // units (new_radius) so the achievable diagnostic can echo the same
            // units the violation text quotes.
            var requestedInRadiusUnits = newKValue == null && newRadius != null && newRadius.Value > 0;
            // Optional station hint. When present, lets the tool re-resolve
            // ``element_index`` against the CURRENT entity collection if the
            // index is stale (out of range, or points at a Tangent). Earlier
            // fixes in the same batch can renumber/replace entities, so an
            // index that was correct at plan time often isn't valid by
            // execute time. The station hint is authoritative — it's the
            // violation's location and doesn't move when other curves change.
            var targetStation = GetDoubleParam(parameters, "target_station");

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");

            // Accept new_radius as a synonym for new_k_value. The standards
            // tables publish vertical-curve thresholds as radii while the
            // Civil 3D API only writes K. Convert via the parabolic-curve
            // identity ``K = R / 100``. Doing the conversion here (not on the
            // agent) keeps the engineer's editable value in radius units —
            // matching the violation description — without an agent-side
            // shim that's easy to forget.
            if (newKValue == null && newRadius != null && newRadius.Value > 0)
                newKValue = newRadius.Value / 100.0;

            if (newKValue == null && newLength == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "At least one of 'new_k_value', 'new_radius', or 'new_length' must be provided");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var profileId = ObjectFinder.FindProfile(civilDoc, tr, profileName, alignmentName);
            if (profileId == null)
                return ToolResult.NotFound("Profile", profileName);

            var profile = tr.GetObject(profileId.Value, OpenMode.ForWrite) as CivilDb.Profile;
            if (profile == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open profile for write");

            if (!ProfileTypeGuard.IsModifiable(profile.ProfileType))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Profile '{profileName}' is a {profile.ProfileType} profile. Only layout/design/FG profiles can be modified.");

            int idx = elementIndex.Value;
            bool resolvedByStation = false;
            string? resolutionNote = null;

            // Re-resolve by ``target_station`` when the agent-provided index
            // is stale. This guards against the in-batch entity-renumbering
            // problem we hit on 2026-06-08: profile 73 elem 7's length change
            // dropped the entity count from 19 to 18, so the index-18 fix
            // for the next violation suddenly pointed past the end, and
            // index-14 (originally a parabola) became a Tangent.
            if (targetStation.HasValue)
            {
                bool indexInvalid =
                    idx < 0
                    || idx >= profile.Entities.Count
                    || profile.Entities[idx].EntityType.ToString() == "Tangent";
                if (indexInvalid)
                {
                    int? stationIdx = FindCurveAtStation(profile, targetStation.Value);
                    if (stationIdx.HasValue)
                    {
                        resolutionNote = $"index {idx} was stale (not a curve / out of range); re-resolved to {stationIdx.Value} via station {targetStation.Value:F2}";
                        idx = stationIdx.Value;
                        resolvedByStation = true;
                    }
                }
            }

            if (idx < 0 || idx >= profile.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element index {idx} out of range (0-{profile.Entities.Count - 1})" +
                    (targetStation.HasValue ? $" and no curve found at target_station {targetStation.Value:F2}" : ""));

            var entity = profile.Entities[idx];
            var entityTypeName = entity.EntityType.ToString();

            // Verify it's a curve type (not tangent)
            if (entityTypeName == "Tangent")
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element at index {idx} is a Tangent, not a vertical curve" +
                    (targetStation.HasValue ? $" and no curve found at target_station {targetStation.Value:F2}" : ""));

            double? oldKValue = null;
            double? oldLength = null;

            // Read current values via reflection (handles Parabolic, Circular, etc.)
            // CurveLength is preferred but some entity types only expose Length;
            // try both so the result payload always carries old_length when the
            // engineer asked for a length change.
            try
            {
                var entityType = entity.GetType();
                var kProp = entityType.GetProperty("K");
                if (kProp != null)
                    oldKValue = Convert.ToDouble(kProp.GetValue(entity));

                var curveLengthProp = entityType.GetProperty("CurveLength");
                if (curveLengthProp != null)
                {
                    try { oldLength = Convert.ToDouble(curveLengthProp.GetValue(entity)); }
                    catch { }
                }
                if (oldLength == null)
                {
                    var lengthProp = entityType.GetProperty("Length");
                    if (lengthProp != null)
                    {
                        try { oldLength = Convert.ToDouble(lengthProp.GetValue(entity)); }
                        catch { }
                    }
                }
            }
            catch { }

            // Apply modifications. Same binary-relaxation strategy as
            // ModifyAlignmentCurveRadiusTool: if Civil 3D rejects the
            // requested K / length (most often because the adjacent PVI
            // spacing can't fit ``L = K * |Δg|``), search backward toward
            // the current value and land the largest value that fits. The
            // pure decision logic lives in VerticalCurveAdjustmentSearch so
            // it is unit-testable without an AutoCAD runtime.
            //
            // Two modes:
            //   - exact_only=false (chat / legacy): the engineer accepts partial
            //     progress — landing the best-fit value below the request is
            //     reported as relaxed success and COMMITTED.
            //   - exact_only=true (Stage 2 fix flow): NO partial fixes. The same
            //     search runs only to DISCOVER the achievable value; the tool
            //     then returns Fail and the executor aborts the transaction, so
            //     the probe writes are rolled back and the drawing is untouched.
            var writeType = entity.GetType();
            var writeCurveLengthProp = writeType.GetProperty("CurveLength");
            var writeLengthProp = writeType.GetProperty("Length");
            var writeKProp = writeType.GetProperty("K");

            double achievedK = oldKValue ?? 0.0;
            double achievedLength = oldLength ?? 0.0;
            string? lastError = null;
            bool relaxed = false;
            int attempts = 0;
            bool modified = false;

            VerticalCurveAdjustmentSearch.Outcome? kOutcome = null;
            VerticalCurveAdjustmentSearch.Outcome? lengthOutcome = null;

            // ── K-value relaxation ─────────────────────────────────
            if (newKValue.HasValue && newKValue.Value > 0)
            {
                // The geometry write, injected into the pure search logic.
                // Returns null on success, the rejection message on failure.
                string? TryApplyK(double value)
                {
                    return TryWriteDouble(writeKProp, entity, value, out var err) ? null : err;
                }

                kOutcome = VerticalCurveAdjustmentSearch.Run(
                    oldKValue ?? 0.0, newKValue.Value, TryApplyK);
                achievedK = kOutcome.Achieved;
                if (kOutcome.LastError != null) lastError = kOutcome.LastError;
                if (kOutcome.Relaxed) relaxed = true;
                attempts = kOutcome.Attempts;
                if (kOutcome.ImprovedOverCurrent(oldKValue ?? 0.0) || kOutcome.ExactApplied)
                    modified = true;
            }

            // ── Length relaxation ──────────────────────────────────
            if (newLength.HasValue && newLength.Value > 0)
            {
                // CurveLength is preferred but some entity types only expose
                // Length; try both so either property accepts the write.
                string? TryApplyLength(double value)
                {
                    if (TryWriteDouble(writeCurveLengthProp, entity, value, out var err1))
                        return null;
                    if (TryWriteDouble(writeLengthProp, entity, value, out var _))
                        return null;
                    return err1;
                }

                lengthOutcome = VerticalCurveAdjustmentSearch.Run(
                    oldLength ?? 0.0, newLength.Value, TryApplyLength);
                achievedLength = lengthOutcome.Achieved;
                if (lengthOutcome.LastError != null) lastError = lengthOutcome.LastError;
                if (lengthOutcome.Relaxed) relaxed = true;
                attempts = lengthOutcome.Attempts;
                if (lengthOutcome.ImprovedOverCurrent(oldLength ?? 0.0) || lengthOutcome.ExactApplied)
                    modified = true;
            }

            // ── exact_only contract (Stage 2 fix flow): NO partial fixes ──
            // If any edited quantity did not land its EXACT requested value,
            // return Fail with the best achievable value as a diagnostic. The
            // probes above may have mutated the entity inside this transaction —
            // ToolExecutor aborts on Fail, discarding them, so the drawing is
            // left untouched.
            if (exactOnly)
            {
                bool kExact = kOutcome == null || kOutcome.ExactApplied;
                bool lengthExact = lengthOutcome == null || lengthOutcome.ExactApplied;
                if (!kExact || !lengthExact)
                {
                    // Pick the requested/achievable pair for the message from
                    // whichever quantity failed (K first, then length).
                    double requestedForMsg;
                    double achievableForMsg;
                    if (!kExact && kOutcome != null)
                    {
                        // Report K-value targets in the units the agent sent
                        // (radius when it used new_radius) so the message matches
                        // the violation text.
                        requestedForMsg = requestedInRadiusUnits
                            ? newKValue!.Value * 100.0 : newKValue!.Value;
                        achievableForMsg = requestedInRadiusUnits
                            ? kOutcome.Achieved * 100.0 : kOutcome.Achieved;
                    }
                    else
                    {
                        requestedForMsg = newLength!.Value;
                        achievableForMsg = lengthOutcome!.Achieved;
                    }

                    double achievableK = kOutcome?.Achieved ?? (oldKValue ?? 0.0);
                    double achievableLength = lengthOutcome?.Achieved ?? (oldLength ?? 0.0);

                    return new ToolResult
                    {
                        Success = false,
                        Error = new ToolExecutionError
                        {
                            Code = ToolErrorCodes.ExecutionFailed,
                            Message = VerticalCurveAdjustmentSearch.BuildExactOnlyFailureMessage(
                                requestedForMsg, achievableForMsg),
                            Details =
                                $"exact_only rejection on element {idx} of profile '{profileName}': " +
                                (kOutcome != null && !kOutcome.ExactApplied
                                    ? $"requested K={newKValue!.Value:F3}, best achievable K={achievableK:F3}; "
                                    : "") +
                                (lengthOutcome != null && !lengthOutcome.ExactApplied
                                    ? $"requested length={newLength!.Value:F3}, best achievable length={achievableLength:F3}; "
                                    : "") +
                                $"(Civil 3D: {lastError ?? "n/a"}). No geometry was changed (transaction aborted).",
                        },
                        // Diagnostic payload — forwarded to the agent even on
                        // failure (ToolExecutor copies Data into the result
                        // payload regardless of Success). The agent's
                        // _extract_achievable_value reads these in precedence
                        // order achievable_radius → achievable_k_value →
                        // achievable_length. The drawing was NOT modified.
                        Data = new
                        {
                            profile_name = profileName,
                            element_index = idx,
                            entity_type = entityTypeName,
                            resolved_by_station = resolvedByStation,
                            resolution_note = resolutionNote,
                            old_k_value = newKValue.HasValue && oldKValue.HasValue ? Math.Round(oldKValue.Value, 3) : (double?)null,
                            requested_k_value = newKValue.HasValue ? Math.Round(newKValue.Value, 3) : (double?)null,
                            // Match whichever units the agent edited in. The
                            // agent's _extract_achievable_value reads keys in
                            // precedence order achievable_radius → achievable_k_value
                            // → achievable_length and surfaces the first positive
                            // one for display, so we emit achievable_radius ONLY
                            // when the target came in radius units (new_radius);
                            // a pure new_k_value edit surfaces K via
                            // achievable_k_value (achievable_radius stays null and
                            // is skipped). This keeps the displayed "max achievable"
                            // number in the same units the violation text quotes.
                            achievable_radius = newKValue.HasValue && requestedInRadiusUnits ? Math.Round(achievableK * 100.0, 3) : (double?)null,
                            achievable_k_value = newKValue.HasValue ? Math.Round(achievableK, 3) : (double?)null,
                            old_length = newLength.HasValue && oldLength.HasValue ? Math.Round(oldLength.Value, 3) : (double?)null,
                            requested_length = newLength.HasValue ? Math.Round(newLength.Value, 3) : (double?)null,
                            achievable_length = newLength.HasValue ? Math.Round(achievableLength, 3) : (double?)null,
                            relaxed = false,
                            exact_only = true,
                            probe_attempts = attempts,
                        },
                    };
                }
            }

            if (!modified)
            {
                var requested = newKValue.HasValue
                    ? $"K={Math.Round(newKValue.Value, 3)} (R={Math.Round(newKValue.Value * 100.0, 1)} m)"
                    : newLength.HasValue
                        ? $"length={Math.Round(newLength.Value, 3)} m"
                        : "the requested change";
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Civil 3D rejected {requested} on profile element {idx}: {lastError ?? "no detail"}. " +
                    "The adjacent grades or PVI spacing cannot accommodate any value above the current value " +
                    "— try modifying nearby PVIs (move them apart, or change their elevations to reduce grade change) " +
                    "before editing the curve.");
            }

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                element_index = idx,
                entity_type = entityTypeName,
                resolved_by_station = resolvedByStation,
                resolution_note = resolutionNote,
                old_k_value = oldKValue.HasValue ? Math.Round(oldKValue.Value, 3) : (double?)null,
                new_k_value = newKValue.HasValue ? Math.Round(achievedK, 3) : (double?)null,
                requested_k_value = newKValue.HasValue ? Math.Round(newKValue.Value, 3) : (double?)null,
                // Echo radius equivalents so callers that think in radii
                // (the analyze pipeline, the engineer reading the result)
                // can compare without re-doing the K*100 multiplication.
                old_radius = oldKValue.HasValue ? Math.Round(oldKValue.Value * 100.0, 3) : (double?)null,
                new_radius = newKValue.HasValue ? Math.Round(achievedK * 100.0, 3) : (double?)null,
                requested_radius = newKValue.HasValue ? Math.Round(newKValue.Value * 100.0, 3) : (double?)null,
                old_length = oldLength.HasValue ? Math.Round(oldLength.Value, 3) : (double?)null,
                new_length = newLength.HasValue ? Math.Round(achievedLength, 3) : (double?)null,
                requested_length = newLength.HasValue ? Math.Round(newLength.Value, 3) : (double?)null,
                relaxed = relaxed,
                relaxation_attempts = attempts,
                relaxation_note = relaxed
                    ? $"requested value did not fit on first try; landed best-fit after partial relaxation (Civil 3D: {lastError ?? "n/a"})"
                    : (string?)null,
            }));
        }

        /// <summary>
        /// Try to write a double via reflection. Unwraps the
        /// ``TargetInvocationException`` Civil 3D throws inside the
        /// setter so callers can read the real cause.
        /// </summary>
        private static bool TryWriteDouble(
            PropertyInfo? prop, object target, double value, out string? errorMessage)
        {
            errorMessage = null;
            if (prop == null || !prop.CanWrite) { errorMessage = "property not writable"; return false; }
            try
            {
                prop.SetValue(target, value);
                return true;
            }
            catch (Exception ex)
            {
                var root = ex;
                while (root is TargetInvocationException && root.InnerException != null)
                    root = root.InnerException;
                errorMessage = root.Message;
                return false;
            }
        }

        /// <summary>
        /// Finds the index of the curve (non-Tangent) entity whose station
        /// range contains <paramref name="station"/>. Returns null when no
        /// such entity exists. Used to recover from stale element_index
        /// values when prior fixes in the batch have renumbered entities.
        /// </summary>
        private static int? FindCurveAtStation(CivilDb.Profile profile, double station)
        {
            const double tolerance = 1.0;
            for (int i = 0; i < profile.Entities.Count; i++)
            {
                var e = profile.Entities[i];
                if (e.EntityType.ToString() == "Tangent") continue;
                if (e.StartStation - tolerance <= station && station <= e.EndStation + tolerance)
                    return i;
            }
            return null;
        }
    }
}
