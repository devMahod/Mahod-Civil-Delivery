using System;
using System.Collections.Generic;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free placement and styling contract for Nataly's approved office arrow
    /// block above every directional strip.  The host imports the pinned DWG and
    /// fits it inside this envelope; no schematic arrow geometry is substituted.
    /// </summary>
    public static class SectionTrafficDirectionAnnotationLogic
    {
        public const string RoadBlackStyle = "road-black";
        public const string BusRedStyle = "bus-red";
        public const string BikeBlackStyle = "bike-black";
        public const double ArrowHeightM = 1.40;
        public const double ManagedViewBottomMarginM = 2.0;

        /// <summary>
        /// Additional range above the highest sampled elevation in a managed view.
        /// It covers the tallest (bus) arrow plus a full metre of drafting air.
        /// </summary>
        public static double ManagedViewTopMarginM =>
            Math.Ceiling(RequiredHeadroomM(StripKind.Bus)) + 1.0;

        /// <summary>
        /// Conservative bound for floor/ceiling rounding plus managed margins.
        /// The strict inequality in the real geometry is intentionally rounded up.
        /// </summary>
        public static double MaxManagedViewEnvelopePaddingM =>
            ManagedViewBottomMarginM + ManagedViewTopMarginM + 2.0;

        public enum StripKind
        {
            Road,
            Bus,
            Bike,
        }

        public sealed record ArrowLayout(
            TrafficDirectionEvidenceLogic.RelativeFlow Flow,
            StripKind Kind,
            string StyleToken,
            short ColorIndex,
            double LaneMidOffsetM,
            double BottomElevation,
            double TopElevation,
            bool PointsUp);

        /// <summary>
        /// Builds the exact placement envelope for the office arrow block.
        /// Along-alignment points upward and against-alignment points downward; this
        /// convention is independent of left/right offset and is paired with the
        /// rear/front Civil viewing convention for vehicles.
        /// </summary>
        public static bool TryBuild(
            double laneMidOffsetM,
            double groundElevation,
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            StripKind kind,
            out ArrowLayout? layout,
            out string error)
        {
            layout = null;
            error = string.Empty;
            if (!Finite(laneMidOffsetM) || !Finite(groundElevation))
            {
                error = "direction-arrow anchor is non-finite";
                return false;
            }
            if (flow != TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment &&
                flow != TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment)
            {
                error = "direction-arrow flow is unresolved";
                return false;
            }
            if (kind != StripKind.Road && kind != StripKind.Bus && kind != StripKind.Bike)
            {
                error = "direction-arrow strip kind is unsupported";
                return false;
            }

            var clearance = ClearanceAboveGroundM(kind);
            var bottom = groundElevation + clearance;
            var top = bottom + ArrowHeightM;
            var style = StyleToken(kind);
            layout = new ArrowLayout(
                flow,
                kind,
                style,
                kind == StripKind.Bus ? (short)1 : (short)7,
                laneMidOffsetM,
                bottom,
                top,
                flow == TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment);
            return true;
        }

        public static string StyleToken(StripKind kind) => kind switch
        {
            StripKind.Bus => BusRedStyle,
            StripKind.Bike => BikeBlackStyle,
            StripKind.Road => RoadBlackStyle,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>Minimum elevation headroom above sampled strip ground.</summary>
        public static double RequiredHeadroomM(StripKind kind) =>
            ClearanceAboveGroundM(kind) + ArrowHeightM;

        public static bool IsStyleToken(string? token) =>
            string.Equals(token, RoadBlackStyle, StringComparison.Ordinal) ||
            string.Equals(token, BusRedStyle, StringComparison.Ordinal) ||
            string.Equals(token, BikeBlackStyle, StringComparison.Ordinal);

        private static double ClearanceAboveGroundM(StripKind kind) => kind switch
        {
            StripKind.Bus => 3.50,
            StripKind.Bike => 1.55,
            StripKind.Road => 2.10,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
