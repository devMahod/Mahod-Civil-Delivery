using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Bulge / arc-segment mathematics for polyline editing. Pure (no AutoCAD types).
    ///
    /// AutoCAD stores an arc segment as a <c>bulge</c> on its start vertex, where
    /// <c>bulge = tan(Δ/4)</c> for included angle Δ, positive counter-clockwise. Every operation
    /// that adds, removes or re-parameterises a vertex has to carry that value correctly:
    /// inserting a vertex in the middle of an arc must split Δ into two arcs of the SAME radius
    /// (bulge halves are tan of a quarter of each part, not half the bulge), and reversing a
    /// polyline must negate it. The LISP originals do this with `(/ (sin a) (cos a))` inline;
    /// here it is one tested place.
    ///
    /// Sign / geometry conventions verified against AutoCAD:
    ///   a=(0,0), b=(1,0), bulge=+1 → Δ=π (CCW semicircle), centre (0.5,0), R=0.5, and the arc passes
    ///   through (0.5,-0.5) — i.e. a POSITIVE bulge bulges to the RIGHT of travel.
    /// </summary>
    public static class BulgeMath
    {
        /// <summary>Bulges below this are treated as straight.</summary>
        public const double BulgeEpsilon = 1e-12;

        /// <summary>Included angle Δ (rad, signed) of a bulge arc.</summary>
        public static double BulgeToDelta(double bulge) => 4.0 * Math.Atan(bulge);

        /// <summary>Bulge of an arc with included angle Δ (rad, signed).</summary>
        public static double DeltaToBulge(double delta) => Math.Tan(delta / 4.0);

        /// <summary>True when the segment is an arc rather than a straight.</summary>
        public static bool IsArc(double bulge) => Math.Abs(bulge) > BulgeEpsilon;

        /// <summary>Arc radius from the chord endpoints and bulge. Returns 0 for a straight.</summary>
        public static double Radius(Pt2 a, Pt2 b, double bulge)
        {
            if (!IsArc(bulge)) return 0.0;
            double half = a.DistanceTo(b) * 0.5;
            if (half <= 0.0) return 0.0;
            double ab = Math.Abs(bulge);
            return half * (1.0 + ab * ab) / (2.0 * ab);
        }

        /// <summary>
        /// Sagitta (mid-arc rise off the chord). This is the "chord deviation" PL-NOARC prompts for.
        /// </summary>
        public static double Sagitta(Pt2 a, Pt2 b, double bulge) =>
            a.DistanceTo(b) * 0.5 * Math.Abs(bulge);

        /// <summary>Arc centre. Returns the chord midpoint for a straight (harmless, never used).</summary>
        public static Pt2 Center(Pt2 a, Pt2 b, double bulge)
        {
            double c = a.DistanceTo(b);
            var mid = new Pt2((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
            if (!IsArc(bulge) || c <= 0.0) return mid;

            double delta = BulgeToDelta(bulge);
            double half = c * 0.5;
            // Apothem = h / tan(Δ/2); the centre sits on the LEFT normal of a→b, so a positive
            // (CCW) bulge — whose centre is on the left — bulges to the right of travel.
            double t = Math.Tan(delta * 0.5);
            if (Math.Abs(t) < 1e-15) return mid;
            double apothem = half / t;

            double ux = (b.X - a.X) / c, uy = (b.Y - a.Y) / c;
            return new Pt2(mid.X - uy * apothem, mid.Y + ux * apothem);
        }

        /// <summary>Planar length of a segment: chord for a straight, arc length for an arc.</summary>
        public static double SegmentLength(Pt2 a, Pt2 b, double bulge)
        {
            if (!IsArc(bulge)) return a.DistanceTo(b);
            double r = Radius(a, b, bulge);
            return r * Math.Abs(BulgeToDelta(bulge));
        }

        /// <summary>
        /// Point at normalised position <paramref name="t"/> (0 = start, 1 = end) along a segment.
        /// For an arc the parameter is a fraction of the SWEEP, so equal t steps are equal arc
        /// lengths — which is what a "vertex every N metres" densify needs.
        /// </summary>
        public static Pt2 PointOnSegment(Pt2 a, Pt2 b, double bulge, double t)
        {
            if (!IsArc(bulge))
                return new Pt2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

            var c = Center(a, b, bulge);
            double delta = BulgeToDelta(bulge);
            double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
            double r = Radius(a, b, bulge);
            double ang = a0 + delta * t;
            return new Pt2(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang));
        }

        /// <summary>
        /// Splits an arc's bulge at normalised position <paramref name="t"/> into the two bulges
        /// that reproduce the SAME arc: Δ₁ = tΔ, Δ₂ = (1−t)Δ. A straight splits into two straights.
        /// </summary>
        public static void SplitBulge(double bulge, double t, out double first, out double second)
        {
            if (!IsArc(bulge))
            {
                first = 0.0;
                second = 0.0;
                return;
            }
            double delta = BulgeToDelta(bulge);
            first = DeltaToBulge(delta * t);
            second = DeltaToBulge(delta * (1.0 - t));
        }

        /// <summary>
        /// Bulge that turns the straight a→b into an arc of the given radius.
        /// Returns null when the radius is too small to span the chord (|ab| &gt; 2R) — the caller
        /// reports that as an invalid parameter instead of drawing something arbitrary.
        /// </summary>
        /// <param name="clockwise">Which of the two arcs through a and b to build.</param>
        /// <param name="major">True for the major (&gt;180°) arc.</param>
        public static double? BulgeFromRadius(Pt2 a, Pt2 b, double radius, bool clockwise, bool major = false)
        {
            double c = a.DistanceTo(b);
            if (radius <= 0.0 || c <= 0.0) return null;
            double ratio = c / (2.0 * radius);
            if (ratio > 1.0 + 1e-12) return null;
            if (ratio > 1.0) ratio = 1.0;

            double delta = 2.0 * Math.Asin(ratio);      // minor arc, 0..π
            if (major) delta = 2.0 * Math.PI - delta;
            if (clockwise) delta = -delta;
            return DeltaToBulge(delta);
        }

        /// <summary>
        /// Closest point on a segment to <paramref name="p"/>.
        /// </summary>
        /// <param name="t">Normalised position of the returned point along the segment (0..1).</param>
        /// <returns>The closest point itself.</returns>
        public static Pt2 ClosestPointOnSegment(Pt2 a, Pt2 b, double bulge, Pt2 p, out double t)
        {
            if (!IsArc(bulge))
            {
                double abx = b.X - a.X, aby = b.Y - a.Y;
                double denom = abx * abx + aby * aby;
                t = denom < 1e-18 ? 0.0 : ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / denom;
                if (t < 0.0) t = 0.0;
                else if (t > 1.0) t = 1.0;
                return new Pt2(a.X + abx * t, a.Y + aby * t);
            }

            var c = Center(a, b, bulge);
            double delta = BulgeToDelta(bulge);
            double r = Radius(a, b, bulge);
            double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
            double ap = Math.Atan2(p.Y - c.Y, p.X - c.X);

            double tt = SweepFraction(ap - a0, delta);
            t = tt;
            double ang = a0 + delta * tt;
            return new Pt2(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang));
        }

        /// <summary>
        /// Position along an arc's sweep, as a fraction clamped to [0,1], for a point whose
        /// direction from the centre differs from the start direction by <paramref name="diff"/>.
        ///
        /// The subtlety: a point just "behind" the start of a CCW arc has a raw angle difference
        /// close to 2π, which naively reads as "almost at the end". So anything outside the sweep
        /// is resolved to whichever END of the arc is angularly nearer — the arc's own gap is
        /// split down the middle.
        /// </summary>
        private static double SweepFraction(double diff, double delta)
        {
            const double twoPi = 2.0 * Math.PI;
            if (Math.Abs(delta) < 1e-15) return 0.0;

            if (delta > 0.0)
            {
                diff %= twoPi;
                if (diff < 0.0) diff += twoPi;               // → [0, 2π)
                if (diff <= delta) return diff / delta;
                return diff < (delta + twoPi) * 0.5 ? 1.0 : 0.0;
            }

            diff %= twoPi;
            if (diff > 0.0) diff -= twoPi;                    // → (−2π, 0]
            if (diff >= delta) return diff / delta;
            return diff > (delta - twoPi) * 0.5 ? 1.0 : 0.0;
        }

        /// <summary>How an arc is broken into straight segments (PL-NOARC's five modes).</summary>
        public enum FlattenMode
        {
            /// <summary>Fixed number of straight segments per arc.</summary>
            Count,

            /// <summary>Target length of each straight segment (along the arc).</summary>
            SegmentLength,

            /// <summary>Maximum allowed sagitta between the arc and each chord.</summary>
            ChordDeviation,

            /// <summary>Target straight-line chord length.</summary>
            ChordLength
        }

        /// <summary>
        /// Number of straight segments an arc must be split into to satisfy a flatten mode.
        /// Always ≥ 1; a straight always answers 1.
        /// </summary>
        public static int FlattenSegmentCount(Pt2 a, Pt2 b, double bulge, FlattenMode mode, double value)
        {
            if (!IsArc(bulge)) return 1;
            double r = Radius(a, b, bulge);
            double delta = Math.Abs(BulgeToDelta(bulge));
            if (r <= 0.0 || delta <= 0.0) return 1;

            switch (mode)
            {
                case FlattenMode.Count:
                    return Math.Max(1, (int)Math.Round(value));

                case FlattenMode.SegmentLength:
                {
                    if (value <= 0.0) return 1;
                    double len = r * delta;
                    return Math.Max(1, (int)Math.Ceiling(len / value - 1e-9));
                }

                case FlattenMode.ChordDeviation:
                {
                    // s = R(1 − cos(δ/2)) → δ = 2·acos(1 − s/R)
                    if (value <= 0.0) return 1;
                    double ratio = 1.0 - value / r;
                    if (ratio <= -1.0) return 1;               // one chord already satisfies it
                    double perChord = 2.0 * Math.Acos(Math.Min(1.0, ratio));
                    if (perChord <= 1e-12) return 1;
                    return Math.Max(1, (int)Math.Ceiling(delta / perChord - 1e-9));
                }

                case FlattenMode.ChordLength:
                {
                    if (value <= 0.0) return 1;
                    double ratio = value / (2.0 * r);
                    if (ratio >= 1.0) return 1;                 // a single chord spans the arc
                    double perChord = 2.0 * Math.Asin(ratio);
                    if (perChord <= 1e-12) return 1;
                    return Math.Max(1, (int)Math.Ceiling(delta / perChord - 1e-9));
                }

                default:
                    return 1;
            }
        }

        /// <summary>
        /// Interior points that turn an arc into <paramref name="segments"/> straights.
        /// Endpoints are NOT included (the caller already has them as vertices).
        /// </summary>
        public static List<Pt2> FlattenArcInteriorPoints(Pt2 a, Pt2 b, double bulge, int segments)
        {
            var pts = new List<Pt2>();
            if (!IsArc(bulge) || segments < 2) return pts;
            for (int k = 1; k < segments; k++)
                pts.Add(PointOnSegment(a, b, bulge, (double)k / segments));
            return pts;
        }

        /// <summary>
        /// Signed area contribution of one arc segment relative to its chord: +R²/2·(Δ − sinΔ)
        /// for a positive bulge. Used by the CW/CCW test so a shape whose chord polygon has zero
        /// area (a circle drawn as two semicircle segments) still reports a real orientation.
        /// </summary>
        public static double SignedSegmentArea(Pt2 a, Pt2 b, double bulge)
        {
            if (!IsArc(bulge)) return 0.0;
            double r = Radius(a, b, bulge);
            double delta = Math.Abs(BulgeToDelta(bulge));
            double area = 0.5 * r * r * (delta - Math.Sin(delta));
            return bulge > 0.0 ? area : -area;
        }

        /// <summary>Perpendicular distance from p to the infinite line through a and b.</summary>
        public static double DistanceToLine(Pt2 a, Pt2 b, Pt2 p)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len = Math.Sqrt(abx * abx + aby * aby);
            if (len < 1e-15) return a.DistanceTo(p);
            return Math.Abs((p.X - a.X) * aby - (p.Y - a.Y) * abx) / len;
        }

        /// <summary>Perpendicular distance from p to the SEGMENT a→b (endpoints clamped).</summary>
        public static double DistanceToSegment(Pt2 a, Pt2 b, Pt2 p)
        {
            var closest = ClosestPointOnSegment(a, b, 0.0, p, out _);
            return closest.DistanceTo(p);
        }

        /// <summary>Absolute turn angle (rad) at b for the chain a→b→c. 0 = straight.</summary>
        public static double Deflection(Pt2 a, Pt2 b, Pt2 c)
        {
            double v1x = b.X - a.X, v1y = b.Y - a.Y;
            double v2x = c.X - b.X, v2y = c.Y - b.Y;
            double m1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double m2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (m1 < 1e-12 || m2 < 1e-12) return 0.0;
            double cos = (v1x * v2x + v1y * v2y) / (m1 * m2);
            if (cos > 1.0) cos = 1.0;
            else if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos);
        }
    }
}
