using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry
{
    /// <summary>Minimal 2D point for pure, Civil-independent section math.</summary>
    public readonly record struct Pt2(double X, double Y)
    {
        public static Pt2 operator -(Pt2 a, Pt2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Pt2 operator +(Pt2 a, Pt2 b) => new(a.X + b.X, a.Y + b.Y);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public double DistanceTo(Pt2 other) => (other - this).Length;
    }

    /// <summary>
    /// Row-major 3x4 affine transform (rotation/scale 3x3 + translation column),
    /// mirroring an AutoCAD Matrix3d without referencing Autodesk types so the math
    /// stays unit-testable outside Civil.
    /// </summary>
    public readonly struct Affine3
    {
        private readonly double[] _m; // 12 values, rows of [r r r t]

        public Affine3(double[] rowMajor3x4)
        {
            if (rowMajor3x4 == null || rowMajor3x4.Length != 12)
                throw new ArgumentException("Affine3 expects 12 row-major values (3x4).");
            _m = rowMajor3x4;
        }

        public static Affine3 Identity { get; } = new(new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
        });

        public double[] Values => (double[])_m.Clone();

        public (double X, double Y, double Z) Apply(double x, double y, double z)
        {
            return (
                _m[0] * x + _m[1] * y + _m[2] * z + _m[3],
                _m[4] * x + _m[5] * y + _m[6] * z + _m[7],
                _m[8] * x + _m[9] * y + _m[10] * z + _m[11]);
        }

        public Pt2 Apply(Pt2 p)
        {
            var (x, y, _) = Apply(p.X, p.Y, 0);
            return new Pt2(x, y);
        }

        /// <summary>
        /// A transform is invalid for CL purposes when it is singular (collapses
        /// geometry) — the plan treats that as SEC-XREF-TRANSFORM-INVALID.
        /// </summary>
        public bool IsDegenerate()
        {
            // Determinant of the 3x3 linear part.
            double det =
                _m[0] * (_m[5] * _m[10] - _m[6] * _m[9]) -
                _m[1] * (_m[4] * _m[10] - _m[6] * _m[8]) +
                _m[2] * (_m[4] * _m[9] - _m[5] * _m[8]);
            return Math.Abs(det) < 1e-12;
        }
    }

    /// <summary>Pure geometry used by CL interpretation. All angles in degrees.</summary>
    public static class SectionMath
    {
        public const double Eps = 1e-9;

        /// <summary>Direction of a segment in degrees [0, 360).</summary>
        public static double DirectionDeg(Pt2 a, Pt2 b)
        {
            var d = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI;
            return d < 0 ? d + 360.0 : d;
        }

        /// <summary>
        /// Segment-segment intersection. Returns intersection point and parameters
        /// (t on AB, u on CD), both in [0,1] when the crossing is within both
        /// segments. Parallel/collinear segments report no unique intersection.
        /// </summary>
        public static bool TrySegmentIntersection(
            Pt2 a, Pt2 b, Pt2 c, Pt2 d, out Pt2 point, out double t, out double u)
        {
            point = default; t = 0; u = 0;
            double rx = b.X - a.X, ry = b.Y - a.Y;
            double sx = d.X - c.X, sy = d.Y - c.Y;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < Eps) return false; // parallel or collinear — no unique point

            double qpx = c.X - a.X, qpy = c.Y - a.Y;
            t = (qpx * sy - qpy * sx) / denom;
            u = (qpx * ry - qpy * rx) / denom;
            if (t < -Eps || t > 1 + Eps || u < -Eps || u > 1 + Eps) return false;

            t = Math.Clamp(t, 0, 1);
            u = Math.Clamp(u, 0, 1);
            point = new Pt2(a.X + t * rx, a.Y + t * ry);
            return true;
        }

        /// <summary>
        /// All intersections of segment AB with a polyline (ordered vertices).
        /// Consecutive duplicate hits at shared vertices are merged.
        /// </summary>
        public static List<(Pt2 Point, double TOnSegment, int PolySegIndex, double UOnPolySeg)>
            SegmentPolylineIntersections(Pt2 a, Pt2 b, IReadOnlyList<Pt2> poly)
        {
            var hits = new List<(Pt2, double, int, double)>();
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                if (TrySegmentIntersection(a, b, poly[i], poly[i + 1], out var p, out var t, out var u))
                {
                    bool duplicate = false;
                    foreach (var h in hits)
                    {
                        if (h.Item1.DistanceTo(p) < 1e-6) { duplicate = true; break; }
                    }
                    if (!duplicate) hits.Add((p, t, i, u));
                }
            }
            return hits;
        }

        /// <summary>Shortest distance from point P to segment AB.</summary>
        /// <summary>
        /// Largest perpendicular distance of any interior vertex from the chord joining
        /// the first and last vertex. Zero for a straight polyline. A section line drawn
        /// with a vertex at the alignment crossing (as Civil and as 6422's CL.dwg do)
        /// has a tiny sagitta; a road edge has a huge one.
        /// </summary>
        public static double MaxSagitta(IReadOnlyList<Pt2> vertices)
        {
            if (vertices.Count < 3) return 0.0;
            var a = vertices[0];
            var b = vertices[vertices.Count - 1];
            double max = 0.0;
            for (int i = 1; i < vertices.Count - 1; i++)
            {
                var d = DistancePointToSegment(vertices[i], a, b);
                if (d > max) max = d;
            }
            return max;
        }

        public static double DistancePointToSegment(Pt2 p, Pt2 a, Pt2 b)
        {
            var abx = b.X - a.X; var aby = b.Y - a.Y;
            var len2 = abx * abx + aby * aby;
            if (len2 < Eps) return p.DistanceTo(a);
            var t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0, 1);
            return p.DistanceTo(new Pt2(a.X + t * abx, a.Y + t * aby));
        }

        /// <summary>
        /// Skew of the CL relative to the alignment NORMAL at the crossing, in
        /// degrees within (-90, 90]. 0 == exactly perpendicular section. Sign:
        /// positive when the CL is rotated counter-clockwise from the normal.
        /// </summary>
        public static double SkewFromNormalDeg(double clDirectionDeg, double alignmentTangentDeg)
        {
            var normal = alignmentTangentDeg + 90.0;
            var skew = NormalizeSigned180(clDirectionDeg - normal);
            // A CL drawn "the other way" is the same section line: fold onto (-90, 90].
            if (skew > 90.0) skew -= 180.0;
            else if (skew <= -90.0) skew += 180.0;
            return skew;
        }

        public static double NormalizeSigned180(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            else if (deg <= -180.0) deg += 360.0;
            return deg;
        }

        /// <summary>
        /// Signed offset of P from the alignment at crossing point X with unit-length
        /// tangent direction T (degrees). Civil convention: positive = RIGHT of the
        /// direction of increasing station.
        /// </summary>
        public static double SignedOffset(Pt2 p, Pt2 crossing, double tangentDeg)
        {
            var rad = tangentDeg * Math.PI / 180.0;
            double tx = Math.Cos(rad), ty = Math.Sin(rad);
            double vx = p.X - crossing.X, vy = p.Y - crossing.Y;
            // perp-dot: positive when v lies to the right of T.
            return vx * ty - vy * tx;
        }

        /// <summary>
        /// Left/right swath extents encoded by the CL endpoints (plan §7.4: do not
        /// replace endpoint-encoded extents with generic defaults).
        /// Returns signed endpoint offsets plus non-negative left/right extents.
        /// </summary>
        public static (double OffsetA, double OffsetB, double LeftExtent, double RightExtent)
            ExtentsFromEndpoints(Pt2 a, Pt2 b, Pt2 crossing, double tangentDeg)
        {
            double oa = SignedOffset(a, crossing, tangentDeg);
            double ob = SignedOffset(b, crossing, tangentDeg);
            double right = Math.Max(0, Math.Max(oa, ob));
            double left = Math.Max(0, -Math.Min(oa, ob));
            return (oa, ob, left, right);
        }
    }
}
