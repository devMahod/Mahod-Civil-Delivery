using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Autodesk-free geometry for the transient section preview.  The Civil host is
    /// responsible only for obtaining real surface elevations; this class decides
    /// where to sample and how to map those measurements into a legible plot.
    /// </summary>
    public static class SectionPreviewGeometry
    {
        public readonly record struct WcsPoint(double X, double Y);
        public readonly record struct PathSample(double Offset, double X, double Y);
        public readonly record struct SurfacePoint(double Offset, double Elevation);
        public readonly record struct PlotPoint(double X, double Y);

        public sealed record ElevationWindow(
            double Datum,
            double Top,
            double VerticalScale)
        {
            public double Height => Top - Datum;
        }

        /// <summary>
        /// Produces bounded, deterministic sample locations along the actual CL.  The
        /// horizontal coordinate is signed distance along the cut (positive to the
        /// right of increasing station), not perpendicular alignment distance or the
        /// source polyline's draw direction. This shares PLAN/APPLY semantics.
        /// Offset zero is always included so the axis elevation is a measured point.
        /// </summary>
        public static List<PathSample> BuildSamplingPath(
            WcsPoint endpointA,
            WcsPoint endpointB,
            WcsPoint crossing,
            double alignmentTangentDeg,
            double maximumSpacingM = 1.0,
            int maximumSamples = 81)
        {
            if (!Finite(endpointA) || !Finite(endpointB) || !Finite(crossing) ||
                !double.IsFinite(alignmentTangentDeg) || maximumSpacingM <= 0 ||
                maximumSamples < 3)
                return new List<PathSample>();

            if (!SectionCutFrame.TryCreate(
                    new(endpointA.X, endpointA.Y), new(endpointB.X, endpointB.Y),
                    new(crossing.X, crossing.Y), alignmentTangentDeg, out var frame))
                return new List<PathSample>();
            var offsetA = frame!.OffsetA;
            var offsetB = frame.OffsetB;
            var span = Math.Abs(offsetB - offsetA);
            if (span < 0.01 || offsetA * offsetB >= -1e-8)
                return new List<PathSample>();

            var count = Math.Clamp((int)Math.Ceiling(span / maximumSpacingM) + 1,
                3, maximumSamples);
            var offsets = new List<double>(count + 1);
            for (var i = 0; i < count; i++)
                offsets.Add(offsetA + (offsetB - offsetA) * i / (count - 1));
            offsets.Add(0.0);

            // Keep the axis while respecting the hard bound.  Removing the closest
            // non-axis interior point changes sampling density by less than one slot.
            offsets = offsets
                .OrderBy(x => x)
                .GroupBy(x => Math.Round(x, 9))
                // A computed midpoint can be a tiny negative number and sort
                // before the explicit zero. Keep the actual axis canonical.
                .Select(g => g.Key == 0 ? 0.0 : g.First())
                .ToList();
            while (offsets.Count > maximumSamples)
            {
                var remove = Enumerable.Range(1, offsets.Count - 2)
                    .Where(i => Math.Abs(offsets[i]) > 1e-9)
                    .OrderBy(i => Math.Abs(offsets[i]))
                    .First();
                offsets.RemoveAt(remove);
            }

            var result = new List<PathSample>(offsets.Count);
            foreach (var offset in offsets)
            {
                if (offset == 0)
                {
                    // The axis is an actual measured crossing, not a rounded
                    // interpolation of the endpoints (especially on skewed CLs).
                    result.Add(new PathSample(0, crossing.X, crossing.Y));
                    continue;
                }
                var t = (offset - offsetA) / (offsetB - offsetA);
                result.Add(new PathSample(
                    offset,
                    endpointA.X + (endpointB.X - endpointA.X) * t,
                    endpointA.Y + (endpointB.Y - endpointA.Y) * t));
            }
            return result;
        }

        /// <summary>Perpendicular alignment offset, not the along-cut plot coordinate.</summary>
        public static double SignedOffset(
            WcsPoint point, WcsPoint crossing, double alignmentTangentDeg)
        {
            var radians = alignmentTangentDeg * Math.PI / 180.0;
            var tx = Math.Cos(radians);
            var ty = Math.Sin(radians);
            var vx = point.X - crossing.X;
            var vy = point.Y - crossing.Y;
            return vx * ty - vy * tx;
        }

        /// <summary>
        /// A surface is drawable only when it supplied two distinct, finite measured
        /// locations.  A single successful API query is a point, not a ground profile.
        /// </summary>
        public static bool HasRenderableSegment(IReadOnlyList<SurfacePoint> points) =>
            points.Count >= 2 &&
            points.All(p => double.IsFinite(p.Offset) && double.IsFinite(p.Elevation)) &&
            points.Max(p => p.Offset) - points.Min(p => p.Offset) > 0.01;

        /// <summary>
        /// Returns a measured/interpolated elevation only when one continuous sampled
        /// chain brackets the requested offset.  It never extrapolates past a surface
        /// edge and never bridges a sampling gap.
        /// </summary>
        public static bool TryElevationAtOffset(
            IReadOnlyList<SurfacePoint> continuousChain,
            double targetOffset,
            out double elevation)
        {
            elevation = 0;
            if (!double.IsFinite(targetOffset) || !HasRenderableSegment(continuousChain))
                return false;

            var ordered = continuousChain.OrderBy(p => p.Offset).ToList();
            var exactIndex = ordered.FindIndex(p => Math.Abs(p.Offset - targetOffset) <= 1e-8);
            if (exactIndex >= 0 && double.IsFinite(ordered[exactIndex].Elevation))
            {
                elevation = ordered[exactIndex].Elevation;
                return true;
            }

            for (var i = 1; i < ordered.Count; i++)
            {
                var left = ordered[i - 1];
                var right = ordered[i];
                if (targetOffset < left.Offset || targetOffset > right.Offset) continue;
                var span = right.Offset - left.Offset;
                if (span <= 1e-9) return false;
                var fraction = (targetOffset - left.Offset) / span;
                elevation = left.Elevation + (right.Elevation - left.Elevation) * fraction;
                return double.IsFinite(elevation);
            }
            return false;
        }

        /// <summary>
        /// Builds one shared elevation window for EG and design.  Every required chain
        /// must contain real data; otherwise the caller fails closed instead of drawing
        /// a schematic substitute.  The vertical scale is presentation-only and is
        /// clamped so unusually flat or steep sections remain honest and readable.
        /// </summary>
        public static bool TryCreateElevationWindow(
            IReadOnlyList<IReadOnlyList<SurfacePoint>> requiredChains,
            out ElevationWindow? window)
        {
            window = null;
            if (requiredChains.Count == 0 || requiredChains.Any(c => !HasRenderableSegment(c)))
                return false;

            var elevations = requiredChains.SelectMany(c => c).Select(p => p.Elevation).ToList();
            var minimum = elevations.Min();
            var maximum = elevations.Max();
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum)) return false;

            var measuredRange = Math.Max(0.0, maximum - minimum);
            var padding = Math.Max(0.5, measuredRange * 0.15);
            var datum = Math.Floor((minimum - padding) * 2.0) / 2.0;
            var top = Math.Ceiling((maximum + padding) * 2.0) / 2.0;
            if (top - datum < 1.0) top = datum + 1.0;

            var scale = Math.Clamp(18.0 / (top - datum), 1.0, 4.0);
            window = new ElevationWindow(datum, top, scale);
            return true;
        }

        /// <summary>Maps a measured offset/elevation into preview WCS.</summary>
        public static PlotPoint Map(
            double axisX,
            double frameBottomY,
            SurfacePoint point,
            ElevationWindow window,
            double bottomInset = 2.0) =>
            new(axisX + point.Offset,
                frameBottomY + bottomInset + (point.Elevation - window.Datum) * window.VerticalScale);

        private static bool Finite(WcsPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    }
}
