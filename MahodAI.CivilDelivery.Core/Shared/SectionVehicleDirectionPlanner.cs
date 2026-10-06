using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free, fail-closed planner for the direction and office elevation of a
    /// car in a section. Approved arrows retain priority over historical decisions.
    /// A new explicit editor decision may override a flow at the exact current
    /// CL/alignment/bounds/midpoint/mode; old manual decisions only fill missing or
    /// conflicting arrows. The sign of the lane offset is never
    /// interpreted as traffic direction.
    /// </summary>
    public static class SectionVehicleDirectionPlanner
    {
        public const double ManualLaneOffsetToleranceM = 0.005;
        public const string ArrowSource = "arrow";
        public const string ManualSource = "manual";
        public const string AlongFlowToken = "along-alignment";
        public const string AgainstFlowToken = "against-alignment";

        public enum ArrowEvidenceMode
        {
            MotorTraffic,
            Bicycle,
        }

        public sealed record DirectionPlan(
            TrafficDirectionEvidenceLogic.ResolutionState State,
            TrafficDirectionEvidenceLogic.RelativeFlow Flow,
            ArrowEvidenceMode EvidenceMode,
            SectionFurnitureLogic.OfficeCarView? OfficeCarView,
            string? DirectionSource,
            string? DirectionDigest,
            string Reason,
            TrafficDirectionEvidenceLogic.DirectionResolution ArrowResolution,
            ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision?
                ManualDecision)
        {
            public bool IsResolved =>
                State == TrafficDirectionEvidenceLogic.ResolutionState.Resolved &&
                Flow != TrafficDirectionEvidenceLogic.RelativeFlow.Unknown &&
                (EvidenceMode == ArrowEvidenceMode.MotorTraffic ||
                 EvidenceMode == ArrowEvidenceMode.Bicycle) &&
                OfficeCarView.HasValue &&
                IsSupportedDirectionSource(DirectionSource) &&
                IsSha256(DirectionDigest);
        }

        /// <summary>
        /// New explicit current-lane edits are checked before the nearest audited
        /// arrow. Without that marker, the previous arrow-first/manual-fallback
        /// contract remains unchanged for the immutable CL/alignment/lane identity.
        /// Duplicate manual decisions are Ambiguous, even when they happen to agree.
        /// Bounded strips require their actual CL frame: a nearby arrow from another
        /// carriageway cannot establish this strip's direction. Projection is local
        /// to the alignment tangent at the cut, not a proof of lane topology along
        /// a curved road; uncertain source membership still requires review.
        /// </summary>
        public static DirectionPlan Resolve(
            string? sourceDrawingHash,
            string? sourceHandle,
            string? alignmentName,
            double laneMidOffsetM,
            double laneTargetX,
            double laneTargetY,
            double alignmentHeadingRadians,
            IEnumerable<TrafficDirectionEvidenceLogic.ArrowEvidence>? arrows,
            IEnumerable<ProjectProfile.SectionsProfile.DecisionsProfile
                .TrafficDirectionDecision>? manualDecisions,
            TrafficDirectionEvidenceLogic.ResolveOptions? options = null,
            ArrowEvidenceMode evidenceMode = ArrowEvidenceMode.MotorTraffic,
            double? laneFromOffsetM = null,
            double? laneToOffsetM = null,
            string? trackEvidenceDigest = null,
            SectionCutFrame? laneCutFrame = null)
        {
            options ??= new TrafficDirectionEvidenceLogic.ResolveOptions();
            var hasStripScope = laneFromOffsetM.HasValue || laneToOffsetM.HasValue || laneCutFrame != null;
            if (hasStripScope)
            {
                if (laneCutFrame == null || laneFromOffsetM is not { } from ||
                    laneToOffsetM is not { } to || !Finite(from) || !Finite(to) ||
                    to <= from || !Finite(laneMidOffsetM) || laneMidOffsetM <= from || laneMidOffsetM >= to ||
                    from < laneCutFrame.MinOffset - 1e-6 || to > laneCutFrame.MaxOffset + 1e-6 ||
                    !Finite(laneTargetX) || !Finite(laneTargetY) ||
                    Math.Abs(laneCutFrame.PointAt(laneMidOffsetM).X - laneTargetX) > 1e-6 ||
                    Math.Abs(laneCutFrame.PointAt(laneMidOffsetM).Y - laneTargetY) > 1e-6)
                    return Unresolved(TrafficDirectionEvidenceLogic.ResolutionState.Unknown,
                        "invalid-lane-cut-scope",
                        TrafficDirectionEvidenceLogic.ResolveNearest(double.NaN, double.NaN,
                            alignmentHeadingRadians, null, options), evidenceMode);

                // Match the same along-cut coordinates as the strip. Plain dot
                // projection would move an arrow to another lane on a skewed CL.
                // The micrometre guard leaves boundary points unassigned instead
                // of allowing WCS roundoff to select either neighbouring strip.
                arrows = (arrows ?? Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>())
                    .Where(item => item != null && Finite(item.X) && Finite(item.Y))
                    .Where(item =>
                    {
                        var offset = laneCutFrame.OffsetAtAlignmentProjection(new(item.X, item.Y));
                        return Finite(offset) && offset > from + 1e-6 && offset < to - 1e-6;
                    }).ToArray();
            }
            var arrow = evidenceMode switch
            {
                ArrowEvidenceMode.MotorTraffic =>
                    TrafficDirectionEvidenceLogic.ResolveNearest(
                        laneTargetX, laneTargetY, alignmentHeadingRadians, arrows, options),
                ArrowEvidenceMode.Bicycle =>
                    TrafficDirectionEvidenceLogic.ResolveNearestBike(
                        laneTargetX, laneTargetY, alignmentHeadingRadians, arrows, options),
                _ => TrafficDirectionEvidenceLogic.ResolveNearest(
                    double.NaN, laneTargetY, alignmentHeadingRadians, arrows, options),
            };

            if (evidenceMode != ArrowEvidenceMode.MotorTraffic &&
                evidenceMode != ArrowEvidenceMode.Bicycle)
                return Unresolved(
                    TrafficDirectionEvidenceLogic.ResolutionState.Unknown,
                    "invalid-arrow-evidence-mode",
                    arrow,
                    evidenceMode);

            if (!IsSha256(sourceDrawingHash) || !IsHexHandle(sourceHandle) ||
                string.IsNullOrWhiteSpace(alignmentName) || !Finite(laneMidOffsetM) ||
                (trackEvidenceDigest != null && (!IsSha256(trackEvidenceDigest) ||
                    laneFromOffsetM is not { } trackFrom || laneToOffsetM is not { } trackTo ||
                    !Finite(trackFrom) || !Finite(trackTo) || laneMidOffsetM <= trackFrom || laneMidOffsetM >= trackTo)))
            {
                return Unresolved(
                    TrafficDirectionEvidenceLogic.ResolutionState.Unknown,
                    "invalid-lane-identity",
                    arrow,
                    evidenceMode);
            }

            var decisions = (manualDecisions ?? Array.Empty<ProjectProfile.SectionsProfile
                .DecisionsProfile.TrafficDirectionDecision>())
                .Where(decision => decision != null && string.Equals(decision.TrackEvidenceDigest,
                    trackEvidenceDigest, StringComparison.Ordinal)).ToArray();
            var explicitEdits = decisions.Where(decision => decision != null && decision.AllowArrowOverride &&
                IsValidManualDecision(decision) &&
                SameIdentity(decision, sourceDrawingHash!, sourceHandle!, alignmentName!, laneMidOffsetM) &&
                decision.LaneMidOffsetM == laneMidOffsetM && laneFromOffsetM.HasValue && laneToOffsetM.HasValue &&
                decision.FromOffsetM == laneFromOffsetM && decision.ToOffsetM == laneToOffsetM &&
                decision.EvidenceMode == EvidenceModeToken(evidenceMode)).ToArray();
            if (explicitEdits.Length > 1)
                return Unresolved(TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous,
                    "duplicate-explicit-direction-edits", arrow, evidenceMode);
            if (explicitEdits.Length == 1 && Finite(laneTargetX) && Finite(laneTargetY) && Finite(alignmentHeadingRadians) &&
                TryParseFlowToken(explicitEdits[0].Flow, out var editedFlow) &&
                SectionFurnitureLogic.TryOfficeCarViewForFlow(editedFlow, out var editedView))
            {
                var provenance = string.Join("\n", new[]
                {
                    "explicit-current-lane-direction-v1",
                    ManualDigest(explicitEdits[0], editedFlow, editedView, evidenceMode),
                    F(laneFromOffsetM!.Value), F(laneToOffsetM!.Value),
                    F(laneTargetX), F(laneTargetY), F(alignmentHeadingRadians),
                    arrow.State.ToString(), Field(arrow.Reason),
                    string.Join("\n", arrow.Contenders.Select(ArrowCanonical).OrderBy(value => value, StringComparer.Ordinal)),
                });
                return new DirectionPlan(TrafficDirectionEvidenceLogic.ResolutionState.Resolved,
                    editedFlow, evidenceMode, editedView, ManualSource,
                    BindTrack(ArtifactHash.Sha256OfText(provenance), trackEvidenceDigest),
                    "resolved-from-explicit-current-lane-edit", arrow, explicitEdits[0]);
            }

            if (arrow.IsResolved)
            {
                var flow = TrafficDirectionEvidenceLogic.RelativeToAlignment(
                    arrow.HeadingRadians!.Value,
                    alignmentHeadingRadians,
                    options.AxisToleranceDegrees);
                if (SectionFurnitureLogic.TryOfficeCarViewForFlow(flow, out var view))
                {
                    return new DirectionPlan(
                        TrafficDirectionEvidenceLogic.ResolutionState.Resolved,
                        flow,
                        evidenceMode,
                        view,
                        ArrowSource,
                        BindTrack(ArrowDigest(
                            sourceDrawingHash!, sourceHandle!, alignmentName!,
                            laneMidOffsetM, laneTargetX, laneTargetY,
                            alignmentHeadingRadians, flow, view, evidenceMode, arrow), trackEvidenceDigest),
                        "resolved-from-approved-arrow",
                        arrow,
                        null);
                }
            }

            var matches = decisions.Where(decision => decision != null && !decision.AllowArrowOverride)
                .Where(IsValidManualDecision)
                .Where(d => SameIdentity(
                    d, sourceDrawingHash!, sourceHandle!, alignmentName!, laneMidOffsetM))
                .ToList();

            if (matches.Count == 1 &&
                TryParseFlowToken(matches[0].Flow, out var manualFlow) &&
                SectionFurnitureLogic.TryOfficeCarViewForFlow(manualFlow, out var manualView))
            {
                return new DirectionPlan(
                    TrafficDirectionEvidenceLogic.ResolutionState.Resolved,
                    manualFlow,
                    evidenceMode,
                    manualView,
                    ManualSource,
                    ManualDigest(matches[0], manualFlow, manualView, evidenceMode),
                    "resolved-from-approved-manual-decision-after-" + arrow.State
                        .ToString().ToLowerInvariant(),
                    arrow,
                    matches[0]);
            }

            if (matches.Count > 1)
            {
                return Unresolved(
                    TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous,
                    "duplicate-manual-direction-decisions",
                    arrow,
                    evidenceMode);
            }

            var state = arrow.State == TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous
                ? TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous
                : TrafficDirectionEvidenceLogic.ResolutionState.Unknown;
            return Unresolved(
                state,
                arrow.Reason + ";no-valid-manual-decision",
                arrow,
                evidenceMode);
        }

        public static bool IsValidManualDecision(
            ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision? decision)
        {
            if (decision == null || !IsSha256(decision.SourceDrawingHash) ||
                !IsHexHandle(decision.SourceHandle) ||
                string.IsNullOrWhiteSpace(decision.AlignmentName) ||
                decision.LaneMidOffsetM is not { } laneOffset || !Finite(laneOffset) ||
                !TryParseFlowToken(decision.Flow, out _) ||
                string.IsNullOrWhiteSpace(decision.ApprovedBy) ||
                decision.ApprovedAtUtc is null || decision.ApprovedAtUtc.Value == default)
                return false;

            if (decision.AllowArrowOverride &&
                (decision.FromOffsetM is not { } from || decision.ToOffsetM is not { } to ||
                 !Finite(from) || !Finite(to) || to <= from ||
                 (decision.TrackEvidenceDigest == null ? laneOffset != (from + to) / 2 :
                     !IsSha256(decision.TrackEvidenceDigest) || laneOffset <= from || laneOffset >= to) ||
                 !TryParseEvidenceModeToken(decision.EvidenceMode, out _)))
                return false;
            if (decision.TrackEvidenceDigest != null && !decision.AllowArrowOverride) return false;
            return true;
        }

        private static string BindTrack(string digest, string? trackDigest) => trackDigest == null ? digest :
            ArtifactHash.Sha256OfText("source-track-direction-v1\n" + trackDigest + "\n" + digest);

        public static bool TryParseFlowToken(
            string? token,
            out TrafficDirectionEvidenceLogic.RelativeFlow flow)
        {
            if (string.Equals(token?.Trim(), AlongFlowToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                flow = TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment;
                return true;
            }
            if (string.Equals(token?.Trim(), AgainstFlowToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                flow = TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment;
                return true;
            }

            flow = TrafficDirectionEvidenceLogic.RelativeFlow.Unknown;
            return false;
        }

        public static string FlowToken(TrafficDirectionEvidenceLogic.RelativeFlow flow) =>
            flow switch
            {
                TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment => AlongFlowToken,
                TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment => AgainstFlowToken,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(flow), flow, "Only a resolved traffic flow has an evidence token."),
            };

        public static string EvidenceModeToken(ArrowEvidenceMode mode) => mode switch
        {
            ArrowEvidenceMode.MotorTraffic => "motor",
            ArrowEvidenceMode.Bicycle => "bicycle",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        public static bool TryParseEvidenceModeToken(
            string? token, out ArrowEvidenceMode mode)
        {
            if (string.Equals(token, "motor", StringComparison.Ordinal))
            {
                mode = ArrowEvidenceMode.MotorTraffic;
                return true;
            }
            if (string.Equals(token, "bicycle", StringComparison.Ordinal))
            {
                mode = ArrowEvidenceMode.Bicycle;
                return true;
            }
            mode = default;
            return false;
        }

        /// <summary>
        /// Rehydrates an already-resolved PLAN row for APPLY. The office elevation
        /// is re-derived from flow, so a serialized front/rear value can never steer
        /// placement. This accepts no Unknown/Ambiguous or unsigned evidence.
        /// </summary>
        public static bool TryRestoreResolved(
            string? flowToken,
            string? evidenceModeToken,
            string? directionSource,
            string? directionDigest,
            string? reason,
            out DirectionPlan? direction)
        {
            direction = null;
            if (!TryParseFlowToken(flowToken, out var flow) ||
                !TryParseEvidenceModeToken(evidenceModeToken, out var mode) ||
                !IsSupportedDirectionSource(directionSource) ||
                !IsSha256(directionDigest) ||
                !SectionFurnitureLogic.TryOfficeCarViewForFlow(flow, out var view))
                return false;

            direction = new DirectionPlan(
                TrafficDirectionEvidenceLogic.ResolutionState.Resolved,
                flow,
                mode,
                view,
                directionSource,
                directionDigest!.ToLowerInvariant(),
                string.IsNullOrWhiteSpace(reason) ? "restored-from-plan" : reason!,
                new TrafficDirectionEvidenceLogic.DirectionResolution(
                    TrafficDirectionEvidenceLogic.ResolutionState.Unknown,
                    null,
                    null,
                    Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>(),
                    0,
                    0,
                    0,
                    "plan-evidence-restored"),
                null);
            return direction.IsResolved;
        }

        public static bool IsSupportedDirectionSource(string? source) =>
            string.Equals(source, ArrowSource, StringComparison.Ordinal) ||
            string.Equals(source, ManualSource, StringComparison.Ordinal);

        public static bool IsSha256(string? value) =>
            value?.Length == 64 && value.All(Uri.IsHexDigit);

        private static DirectionPlan Unresolved(
            TrafficDirectionEvidenceLogic.ResolutionState state,
            string reason,
            TrafficDirectionEvidenceLogic.DirectionResolution arrow,
            ArrowEvidenceMode evidenceMode) =>
            new(
                state,
                TrafficDirectionEvidenceLogic.RelativeFlow.Unknown,
                evidenceMode,
                null,
                null,
                null,
                reason,
                arrow,
                null);

        private static bool SameIdentity(
            ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision decision,
            string sourceDrawingHash,
            string sourceHandle,
            string alignmentName,
            double laneMidOffsetM) =>
            string.Equals(decision.SourceDrawingHash, sourceDrawingHash,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(decision.SourceHandle, sourceHandle,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(decision.AlignmentName?.Trim(), alignmentName.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            decision.LaneMidOffsetM is { } offset &&
            Math.Abs(offset - laneMidOffsetM) <= ManualLaneOffsetToleranceM;

        private static string ArrowDigest(
            string sourceDrawingHash,
            string sourceHandle,
            string alignmentName,
            double laneMidOffsetM,
            double laneTargetX,
            double laneTargetY,
            double alignmentHeadingRadians,
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            SectionFurnitureLogic.OfficeCarView view,
            ArrowEvidenceMode evidenceMode,
            TrafficDirectionEvidenceLogic.DirectionResolution resolution)
        {
            var contenders = resolution.Contenders
                .Select(ArrowCanonical)
                .OrderBy(x => x, StringComparer.Ordinal);
            var canonical = string.Join("\n", new[]
            {
                "traffic-direction-" + ModeToken(evidenceMode) + "-arrow-v1",
                SectionFurnitureLogic.OfficeCarViewingConvention,
                Field(sourceDrawingHash.ToLowerInvariant()),
                Field(sourceHandle.ToUpperInvariant()),
                Field(alignmentName.Trim().ToUpperInvariant()),
                F(laneMidOffsetM),
                F(laneTargetX),
                F(laneTargetY),
                F(TrafficDirectionEvidenceLogic.NormalizeHeading(alignmentHeadingRadians)),
                FlowToken(flow),
                view.ToString().ToLowerInvariant(),
                F(resolution.HeadingRadians!.Value),
                string.Join("\n", contenders),
            });
            return ArtifactHash.Sha256OfText(canonical);
        }

        private static string ManualDigest(
            ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision decision,
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            SectionFurnitureLogic.OfficeCarView view,
            ArrowEvidenceMode evidenceMode)
        {
            var canonical = string.Join("\n", new[]
            {
                "traffic-direction-" + ModeToken(evidenceMode) + "-manual-v1",
                SectionFurnitureLogic.OfficeCarViewingConvention,
                Field(decision.SourceDrawingHash!.ToLowerInvariant()),
                Field(decision.SourceHandle!.ToUpperInvariant()),
                Field(decision.AlignmentName!.Trim().ToUpperInvariant()),
                F(decision.LaneMidOffsetM!.Value),
                FlowToken(flow),
                view.ToString().ToLowerInvariant(),
                Field(decision.ApprovedBy!.Trim()),
                Utc(decision.ApprovedAtUtc!.Value).ToString("O", CultureInfo.InvariantCulture),
            });
            return ArtifactHash.Sha256OfText(canonical);
        }

        private static string ArrowCanonical(
            TrafficDirectionEvidenceLogic.ArrowEvidence arrow) =>
            string.Join("|", new[]
            {
                F(arrow.X),
                F(arrow.Y),
                F(TrafficDirectionEvidenceLogic.NormalizeHeading(arrow.HeadingRadians)),
                Field(arrow.Layer ?? string.Empty),
                Field(arrow.BlockName ?? string.Empty),
                Field(arrow.Source ?? string.Empty),
                Field(arrow.HandlePath ?? string.Empty),
            });

        private static string Field(string value) =>
            value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

        private static string ModeToken(ArrowEvidenceMode mode) =>
            mode is ArrowEvidenceMode.MotorTraffic or ArrowEvidenceMode.Bicycle
                ? EvidenceModeToken(mode)
                : "invalid";

        private static string F(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static DateTime Utc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        private static bool IsHexHandle(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.All(Uri.IsHexDigit);

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
