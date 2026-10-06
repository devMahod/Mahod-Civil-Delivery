using System;

namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    public sealed class FittedCircle
    {
        public Pt2D Center { get; init; }
        public double Radius { get; init; }
        public double RmsResidualM { get; init; }
        public int Sign { get; init; }                        // +1 CCW, -1 CW (carried from segmenter)
    }

    /// <summary>
    /// Algebraic least-squares circle fit using Pratt's variant. Robust for partial
    /// arcs (typical road curve subtends ≪ 360°), which is where Kasa's classical
    /// fit biases toward small circles. Reference: V. Pratt, "Direct least-squares
    /// fitting of algebraic surfaces", SIGGRAPH 1987; see Chernov §5.
    /// </summary>
    public static class CircleFit
    {
        public static FittedCircle? Fit(Pt2D[] pts, int i0, int i1, int sign)
        {
            int n = i1 - i0 + 1;
            if (n < 3 || pts == null) return null;

            // Centre data for numerical stability.
            double cx = 0, cy = 0;
            for (int i = i0; i <= i1; i++) { cx += pts[i].X; cy += pts[i].Y; }
            cx /= n; cy /= n;

            double Mxx = 0, Myy = 0, Mxy = 0, Mxz = 0, Myz = 0, Mzz = 0;
            for (int i = i0; i <= i1; i++)
            {
                double xi = pts[i].X - cx;
                double yi = pts[i].Y - cy;
                double zi = xi * xi + yi * yi;
                Mxx += xi * xi; Myy += yi * yi; Mxy += xi * yi;
                Mxz += xi * zi; Myz += yi * zi; Mzz += zi * zi;
            }
            Mxx /= n; Myy /= n; Mxy /= n; Mxz /= n; Myz /= n; Mzz /= n;

            // Pratt's characteristic polynomial coefficients.
            double Mz = Mxx + Myy;
            double covXY = Mxx * Myy - Mxy * Mxy;
            double A3 = 4 * Mz;
            double A2 = -3 * Mz * Mz - Mzz;
            double A1 = Mzz * Mz + 4 * covXY * Mz - Mxz * Mxz - Myz * Myz - Mz * Mz * Mz;
            double A0 = Mxz * Mxz * Myy + Myz * Myz * Mxx - Mzz * covXY - 2 * Mxz * Myz * Mxy + Mz * Mz * covXY;
            double A22 = A2 + A2;
            double A33 = A3 + A3 + A3;

            // Newton iteration for the smallest non-negative root. Start at zero.
            double x = 0, y = A0;
            for (int iter = 0; iter < 99; iter++)
            {
                double Dy = A1 + x * (A22 + A33 * x);
                if (Math.Abs(Dy) < 1e-18) break;
                double xnew = x - y / Dy;
                if (!(xnew < x) && !(xnew > x)) break;
                double ynew = A0 + xnew * (A1 + xnew * (A2 + xnew * A3));
                if (Math.Abs(ynew) >= Math.Abs(y)) break;
                x = xnew; y = ynew;
            }

            double det = x * x - x * Mz + covXY;
            if (Math.Abs(det) < 1e-18) return null;
            double centerXc = (Mxz * (Myy - x) - Myz * Mxy) / det / 2.0;
            double centerYc = (Myz * (Mxx - x) - Mxz * Mxy) / det / 2.0;
            double radius = Math.Sqrt(centerXc * centerXc + centerYc * centerYc + Mz + 2 * x);
            var center = new Pt2D(centerXc + cx, centerYc + cy);

            double sumSq = 0;
            for (int i = i0; i <= i1; i++)
            {
                double r = pts[i].DistanceTo(center) - radius;
                sumSq += r * r;
            }
            double rms = Math.Sqrt(sumSq / n);

            return new FittedCircle { Center = center, Radius = radius, RmsResidualM = rms, Sign = sign };
        }
    }
}
