using System;

namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    public sealed class FittedLine
    {
        public Pt2D Origin { get; init; }                    // a point on the line (centroid)
        public Pt2D Direction { get; init; }                 // unit vector along the line
        public double RmsResidualM { get; init; }            // perpendicular RMS error
    }

    /// <summary>
    /// Total-least-squares 2D line fit via PCA on the centred points. The line passes
    /// through the centroid in the direction of the dominant eigenvector of the
    /// covariance matrix. Robust against the X-axis vs Y-axis case that ordinary
    /// y = mx + b regression handles badly.
    /// </summary>
    public static class LineFit
    {
        public static FittedLine? Fit(Pt2D[] pts, int i0, int i1)
        {
            int n = i1 - i0 + 1;
            if (n < 2 || pts == null) return null;

            double cx = 0, cy = 0;
            for (int i = i0; i <= i1; i++) { cx += pts[i].X; cy += pts[i].Y; }
            cx /= n; cy /= n;

            double sxx = 0, syy = 0, sxy = 0;
            for (int i = i0; i <= i1; i++)
            {
                double dx = pts[i].X - cx;
                double dy = pts[i].Y - cy;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            // Dominant eigenvector of [[sxx, sxy],[sxy, syy]].
            double tr = sxx + syy;
            double det = sxx * syy - sxy * sxy;
            double disc = Math.Max(0, tr * tr / 4 - det);
            double lam = tr / 2 + Math.Sqrt(disc);
            double dxDir, dyDir;
            if (Math.Abs(sxy) > 1e-12)
            {
                dxDir = lam - syy;
                dyDir = sxy;
            }
            else
            {
                // diagonal covariance — pick whichever axis has larger variance
                if (sxx >= syy) { dxDir = 1; dyDir = 0; } else { dxDir = 0; dyDir = 1; }
            }
            double mag = Math.Sqrt(dxDir * dxDir + dyDir * dyDir);
            if (mag < 1e-12) return null;
            dxDir /= mag; dyDir /= mag;

            // Perpendicular distance from each point to the line.
            double sumSq = 0;
            for (int i = i0; i <= i1; i++)
            {
                double rx = pts[i].X - cx, ry = pts[i].Y - cy;
                double perp = rx * dyDir - ry * dxDir;
                sumSq += perp * perp;
            }
            double rms = Math.Sqrt(sumSq / n);

            return new FittedLine
            {
                Origin = new Pt2D(cx, cy),
                Direction = new Pt2D(dxDir, dyDir),
                RmsResidualM = rms,
            };
        }

        /// <summary>Intersection of two infinite lines. Null if parallel.</summary>
        public static Pt2D? Intersect(FittedLine a, FittedLine b)
        {
            // Solve a.Origin + s·a.Direction = b.Origin + t·b.Direction
            double det = a.Direction.X * (-b.Direction.Y) - a.Direction.Y * (-b.Direction.X);
            if (Math.Abs(det) < 1e-9) return null;
            double dx = b.Origin.X - a.Origin.X;
            double dy = b.Origin.Y - a.Origin.Y;
            double s = (dx * (-b.Direction.Y) - dy * (-b.Direction.X)) / det;
            return new Pt2D(a.Origin.X + s * a.Direction.X, a.Origin.Y + s * a.Direction.Y);
        }
    }
}
