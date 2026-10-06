using System;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Builds a per-cell A* cost multiplier that biases routing toward the locally
    /// flat strips on the surface. Roads — graded, curb-bounded, with embankment
    /// or cut shoulders — show up in the TIN as a continuous low-slope band; this
    /// gives those cells a cheaper edge cost so A* naturally drifts onto them
    /// without being constrained to them (the user can still leave the strip when
    /// A or B sit off it, or when an obstacle blocks the road).
    /// </summary>
    public static class RoadAffinity
    {
        /// <summary>
        /// Linear slope→cost ramp. For walkable cells: slope=0 → 1−strength
        /// (cheapest), slope≥slopeThreshold → 1.0 (no discount). Non-walkable
        /// cells are 1.0 (irrelevant; A* never visits them).
        /// </summary>
        public static double[,] BuildCostField(
            bool[,] walkable, double[,] slope,
            double slopeThreshold, double strength)
        {
            if (walkable == null) throw new ArgumentNullException(nameof(walkable));
            if (slope == null) throw new ArgumentNullException(nameof(slope));
            if (slopeThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(slopeThreshold));
            if (strength < 0) strength = 0;
            if (strength > 1) strength = 1;

            int nx = walkable.GetLength(0);
            int ny = walkable.GetLength(1);
            if (slope.GetLength(0) != nx || slope.GetLength(1) != ny)
                throw new ArgumentException("slope dimensions must match walkable", nameof(slope));

            var costMul = new double[nx, ny];
            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    if (!walkable[x, y]) { costMul[x, y] = 1.0; continue; }
                    double s = slope[x, y];
                    double t = Math.Max(0.0, 1.0 - s / slopeThreshold);
                    costMul[x, y] = 1.0 - strength * t;
                }
            }
            return costMul;
        }
    }
}
