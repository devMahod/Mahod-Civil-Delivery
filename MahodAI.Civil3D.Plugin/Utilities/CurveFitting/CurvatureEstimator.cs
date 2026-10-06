using System;

namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    /// <summary>
    /// Signed Menger curvature κ(s) at each interior sample using the 3-point
    /// circumscribed circle. Endpoints are extrapolated. Sign follows the cross
    /// product (positive = CCW). Output is smoothed with a moving average to kill
    /// chord-noise from the imported polyline.
    /// </summary>
    public static class CurvatureEstimator
    {
        public static double[] Estimate(Pt2D[] pts, int smoothingWindow = 7)
        {
            if (pts == null || pts.Length < 3) return new double[pts?.Length ?? 0];
            var raw = new double[pts.Length];
            for (int i = 1; i < pts.Length - 1; i++)
                raw[i] = MengerCurvature(pts[i - 1], pts[i], pts[i + 1]);
            raw[0] = raw[1];
            raw[pts.Length - 1] = raw[pts.Length - 2];
            return Smooth(raw, smoothingWindow);
        }

        private static double MengerCurvature(Pt2D a, Pt2D b, Pt2D c)
        {
            double ax = b.X - a.X, ay = b.Y - a.Y;
            double bx = c.X - b.X, by = c.Y - b.Y;
            double cross = ax * by - ay * bx;          // signed twice-area
            double la = Math.Sqrt(ax * ax + ay * ay);
            double lb = Math.Sqrt(bx * bx + by * by);
            double lc = c.DistanceTo(a);
            double denom = la * lb * lc;
            if (denom < 1e-12) return 0;
            return 2.0 * cross / denom;                 // κ = 2·area / (|AB|·|BC|·|CA|)
        }

        private static double[] Smooth(double[] x, int window)
        {
            if (window <= 1) return (double[])x.Clone();
            int half = window / 2;
            var result = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                int lo = Math.Max(0, i - half);
                int hi = Math.Min(x.Length - 1, i + half);
                double sum = 0;
                for (int k = lo; k <= hi; k++) sum += x[k];
                result[i] = sum / (hi - lo + 1);
            }
            return result;
        }
    }
}
