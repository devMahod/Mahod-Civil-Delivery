using System;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Pure 2D math used by <see cref="RemoveAlignmentCurveTool"/> to decide
    /// whether a horizontal curve may be removed (tangent-to-tangent deflection
    /// below the Table 5.5 threshold) and where the two adjoining tangents meet
    /// (the PI). No AutoCAD/Civil 3D types — unit-testable outside Civil 3D
    /// (same pattern as <see cref="RadiusAdjustmentSearch"/>).
    /// </summary>
    public static class CurveRemovalGeometry
    {
        /// <summary>
        /// Cross-product denominators below this are treated as parallel lines
        /// (no intersection). sin of the direction difference — 1e-12 rad is
        /// far below any deflection the removal guard accepts.
        /// </summary>
        public const double ParallelDenominatorEpsilon = 1e-12;

        /// <summary>
        /// Absolute deflection between two directions (radians), in degrees,
        /// normalized to [0, 180]. Direction wraparound (e.g. 359.9° vs 0.15°)
        /// yields the small physical angle, not the raw numeric difference.
        /// </summary>
        public static double DeflectionDegrees(double dir1Rad, double dir2Rad)
        {
            double deg = (dir2Rad - dir1Rad) * (180.0 / Math.PI);
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            if (deg > 180.0) deg = 360.0 - deg;
            return Math.Abs(deg);
        }

        /// <summary>
        /// Intersects two infinite 2D lines, each given as a point + direction
        /// (radians). Returns false when the cross-product denominator is below
        /// <see cref="ParallelDenominatorEpsilon"/> (parallel / anti-parallel);
        /// near-parallel-but-valid cases (deflection well under 1°) still
        /// intersect — callers must sanity-check the PI position separately.
        /// </summary>
        public static bool TryIntersect(
            double p1x, double p1y, double dir1Rad,
            double p2x, double p2y, double dir2Rad,
            out double ix, out double iy)
        {
            double d1x = Math.Cos(dir1Rad);
            double d1y = Math.Sin(dir1Rad);
            double d2x = Math.Cos(dir2Rad);
            double d2y = Math.Sin(dir2Rad);

            double denominator = d1x * d2y - d1y * d2x;
            if (Math.Abs(denominator) < ParallelDenominatorEpsilon)
            {
                ix = 0;
                iy = 0;
                return false;
            }

            // Solve p1 + t·d1 = p2 + s·d2 for t via the cross product.
            double t = ((p2x - p1x) * d2y - (p2y - p1y) * d2x) / denominator;
            ix = p1x + t * d1x;
            iy = p1y + t * d1y;
            return true;
        }

        /// <summary>
        /// Whether point q projects forward of point p along direction
        /// <paramref name="dirRad"/> (dot product &gt; 0). Coincident or
        /// perpendicular points are NOT forward.
        /// </summary>
        public static bool IsForwardOf(double px, double py, double dirRad, double qx, double qy)
        {
            double dot = (qx - px) * Math.Cos(dirRad) + (qy - py) * Math.Sin(dirRad);
            return dot > 0;
        }
    }
}
