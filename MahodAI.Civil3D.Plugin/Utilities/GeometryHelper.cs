using System;

namespace MahodAI.Civil3D.Plugin.Utilities;

/// <summary>
/// 2D geometry helper methods ported from MahodCivilNet Point2 utilities.
/// Used by sight distance, cross-section intersection, and other geometric tools.
/// </summary>
public static class GeometryHelper
{
    /// <summary>Distance between two 2D points.</summary>
    public static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Cross product of vectors (p1→p2) and (p1→p3).
    /// Positive = p3 is left of p1→p2, Negative = right, Zero = collinear.
    /// </summary>
    public static double CrossProduct(double p1x, double p1y, double p2x, double p2y, double p3x, double p3y)
    {
        return (p2x - p1x) * (p3y - p1y) - (p3x - p1x) * (p2y - p1y);
    }

    /// <summary>
    /// Move point along the direction defined by (p1→p2) by given distance.
    /// </summary>
    public static (double X, double Y) MoveAlong(double px, double py, double p1x, double p1y, double p2x, double p2y, double dist)
    {
        double d = Distance(p1x, p1y, p2x, p2y);
        if (d < 1e-10) return (px, py);
        return (px + (p2x - p1x) * dist / d, py + (p2y - p1y) * dist / d);
    }

    /// <summary>
    /// Rotate point around origin by angle (radians).
    /// </summary>
    public static (double X, double Y) Rotate(double px, double py, double originX, double originY, double angle)
    {
        double si = Math.Sin(angle);
        double co = Math.Cos(angle);
        double dx = originX - px;
        double dy = originY - py;
        return (px + dx * co - dy * si, py + dx * si + dy * co);
    }

    /// <summary>
    /// Find intersection point of two infinite lines (p1-p2) and (p3-p4).
    /// Returns null if lines are parallel.
    /// </summary>
    public static (double X, double Y)? LineIntersection(
        double p1x, double p1y, double p2x, double p2y,
        double p3x, double p3y, double p4x, double p4y)
    {
        double d1x = p2x - p1x, d1y = p2y - p1y;
        double d2x = p4x - p3x, d2y = p4y - p3y;
        double denom = d1x * d2y - d1y * d2x;
        if (Math.Abs(denom) < 1e-12) return null;
        double t = ((p3x - p1x) * d2y - (p3y - p1y) * d2x) / denom;
        return (p1x + t * d1x, p1y + t * d1y);
    }

    /// <summary>
    /// Test if two line SEGMENTS (p1-p2) and (p3-p4) intersect.
    /// If they do, returns the intersection point and interpolated Z values.
    /// </summary>
    public static bool SegmentIntersection(
        double p1x, double p1y, double p2x, double p2y,
        double p3x, double p3y, double p4x, double p4y,
        out double ix, out double iy)
    {
        ix = 0; iy = 0;
        double d1x = p2x - p1x, d1y = p2y - p1y;
        double d2x = p4x - p3x, d2y = p4y - p3y;
        double denom = d1x * d2y - d1y * d2x;
        if (Math.Abs(denom) < 1e-12) return false;

        double t = ((p3x - p1x) * d2y - (p3y - p1y) * d2x) / denom;
        double u = ((p3x - p1x) * d1y - (p3y - p1y) * d1x) / denom;

        if (t < -1e-9 || t > 1.0 + 1e-9 || u < -1e-9 || u > 1.0 + 1e-9)
            return false;

        ix = p1x + t * d1x;
        iy = p1y + t * d1y;
        return true;
    }

    /// <summary>
    /// 3D segment intersection: intersects 2D line (p1-p2) with 3D segment (p3-p4),
    /// interpolating Z at the intersection. Used for cross-section sampling.
    /// </summary>
    public static bool SegmentIntersection3D(
        double p1x, double p1y,
        double p2x, double p2y,
        double p3x, double p3y, double p3z,
        double p4x, double p4y, double p4z,
        out double ix, out double iy, out double iz)
    {
        iz = 0;
        if (!SegmentIntersection(p1x, p1y, p2x, p2y, p3x, p3y, p4x, p4y, out ix, out iy))
            return false;

        double segLen = Distance(p3x, p3y, p4x, p4y);
        if (segLen < 1e-10)
        {
            iz = p3z;
            return true;
        }
        double intDist = Distance(p3x, p3y, ix, iy);
        iz = p3z + (p4z - p3z) * intDist / segLen;
        return true;
    }

    /// <summary>
    /// Linear interpolation between two values.
    /// </summary>
    public static double Lerp(double a, double b, double t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Linear interpolation by station: given (sta1, val1) and (sta2, val2), interpolate at targetStation.
    /// </summary>
    public static double InterpolateByStation(double sta1, double val1, double sta2, double val2, double targetStation)
    {
        if (Math.Abs(sta2 - sta1) < 1e-10) return val1;
        return val1 + (val2 - val1) * (targetStation - sta1) / (sta2 - sta1);
    }
}
