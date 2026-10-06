using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Picks PIs from a raw A* path by detecting **direction-change peaks**.
    /// Replaces Douglas-Peucker for routed centerlines: DP scores PIs by
    /// perpendicular deviation from the chord, which is curvature-blind — a
    /// 90° corner with 0.5m perpendicular deviation looks identical to a 0.5m
    /// wobble on a straight line. CurvatureSimplifier instead computes the
    /// smoothed direction-change angle at each cell (vector from W cells back
    /// vs. W cells ahead) and keeps cells at local maxima above a threshold.
    /// Sharp corners surface as PIs; straight stretches stay PI-free.
    /// Smooth-curve handling is delegated to the deviation/length densifier
    /// downstream, which can grow the PI list further when needed.
    /// </summary>
    public static class CurvatureSimplifier
    {
        /// <summary>
        /// Simplify a raw cell path into "must-keep" PIs at direction-change peaks.
        /// </summary>
        /// <param name="rawPts">Cell-centre world coordinates from A*. First and last entries are the endpoints.</param>
        /// <param name="windowCells">Half-width of the sliding window (in raw cells) used to smooth direction. Default 8 → 16-cell baseline. Reduces automatically for short paths.</param>
        /// <param name="angleThresholdRad">Minimum direction-change angle to register a corner. Default ~0.35 rad ≈ 20°.</param>
        /// <param name="maxPis">Hard cap on the returned PI count (including endpoints). Excess corners are dropped weakest-first.</param>
        public static (Pt2[] kept, int[] rawIdx) Simplify(
            List<Pt2> rawPts,
            int windowCells = 8,
            double angleThresholdRad = 0.35,
            int maxPis = 20)
        {
            if (rawPts == null) throw new ArgumentNullException(nameof(rawPts));
            int n = rawPts.Count;
            if (maxPis < 2) maxPis = 2;
            if (n <= 2)
            {
                var idx = Enumerable.Range(0, n).ToArray();
                return (rawPts.ToArray(), idx);
            }

            // Adaptive window: short paths can't support an 8-cell window on each
            // side, so shrink to (n-1)/2 to give every interior cell a usable
            // forward/backward sample.
            int W = Math.Min(windowCells, (n - 1) / 2);
            if (W < 1) W = 1;

            // Smoothed direction-change angle at each interior cell.
            var angle = new double[n];
            for (int i = W; i <= n - 1 - W; i++)
            {
                double bx = rawPts[i].X - rawPts[i - W].X;
                double by = rawPts[i].Y - rawPts[i - W].Y;
                double fx = rawPts[i + W].X - rawPts[i].X;
                double fy = rawPts[i + W].Y - rawPts[i].Y;
                double bMag = Math.Sqrt(bx * bx + by * by);
                double fMag = Math.Sqrt(fx * fx + fy * fy);
                if (bMag < 1e-9 || fMag < 1e-9) { angle[i] = 0; continue; }
                double dot = (bx * fx + by * fy) / (bMag * fMag);
                if (dot > 1.0) dot = 1.0;
                if (dot < -1.0) dot = -1.0;
                angle[i] = Math.Acos(dot);
            }

            // Local maxima with non-max suppression: a cell is a peak if its
            // angle is > every other angle in [i-W, i+W] (strict on at least
            // one side to break ties between equal values).
            var peaks = new List<(int idx, double angle)>();
            for (int i = W; i <= n - 1 - W; i++)
            {
                if (angle[i] < angleThresholdRad) continue;
                bool isMax = true;
                int lo = Math.Max(0, i - W);
                int hi = Math.Min(n - 1, i + W);
                for (int j = lo; j <= hi; j++)
                {
                    if (j == i) continue;
                    // Strict on the left, non-strict on the right — guarantees
                    // exactly one peak survives in any plateau of equal angles.
                    if (j < i ? angle[j] >= angle[i] : angle[j] > angle[i])
                    { isMax = false; break; }
                }
                if (isMax) peaks.Add((i, angle[i]));
            }

            // Cap at maxPis - 2 corners (endpoints are always kept).
            int corners = Math.Max(0, maxPis - 2);
            if (peaks.Count > corners)
            {
                peaks = peaks.OrderByDescending(p => p.angle).Take(corners).ToList();
            }
            peaks.Sort((a, b) => a.idx.CompareTo(b.idx));

            // Compose result: [A, ...peaks, B].
            var kept = new List<Pt2>(peaks.Count + 2) { rawPts[0] };
            var idxs = new List<int>(peaks.Count + 2) { 0 };
            foreach (var p in peaks) { kept.Add(rawPts[p.idx]); idxs.Add(p.idx); }
            kept.Add(rawPts[n - 1]); idxs.Add(n - 1);

            return (kept.ToArray(), idxs.ToArray());
        }
    }
}
