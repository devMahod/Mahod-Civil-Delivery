using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    /// <summary>
    /// Resamples an ordered polyline to a uniform arc-length step. Linear interpolation
    /// between the input vertices — sufficient for the chord-polyline input we get from
    /// imported alignments, where the source already has plenty of vertices.
    /// </summary>
    public static class Resampler
    {
        public static Pt2D[] ToUniform(IReadOnlyList<Pt2D> points, double stepM)
        {
            if (points == null || points.Count < 2 || stepM <= 0)
                return points == null ? Array.Empty<Pt2D>() : new List<Pt2D>(points).ToArray();

            // Cumulative arc length at each input vertex.
            var cum = new double[points.Count];
            cum[0] = 0;
            for (int i = 1; i < points.Count; i++)
                cum[i] = cum[i - 1] + points[i].DistanceTo(points[i - 1]);

            double total = cum[points.Count - 1];
            if (total <= stepM) return new[] { points[0], points[points.Count - 1] };

            int n = (int)Math.Floor(total / stepM);
            var result = new List<Pt2D>(n + 2) { points[0] };
            int j = 0;
            for (int k = 1; k <= n; k++)
            {
                double s = k * stepM;
                while (j + 1 < points.Count && cum[j + 1] < s) j++;
                if (j + 1 >= points.Count) break;
                double seg = cum[j + 1] - cum[j];
                double t = seg <= 0 ? 0 : (s - cum[j]) / seg;
                result.Add(new Pt2D(
                    points[j].X + t * (points[j + 1].X - points[j].X),
                    points[j].Y + t * (points[j + 1].Y - points[j].Y)));
            }
            // Always end exactly on the last input vertex.
            if (result[result.Count - 1].DistanceTo(points[points.Count - 1]) > 1e-9)
                result.Add(points[points.Count - 1]);
            return result.ToArray();
        }
    }
}
