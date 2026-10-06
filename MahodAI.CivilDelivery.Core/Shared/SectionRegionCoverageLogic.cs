using System;
using System.Collections.Generic;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Tests an existing section span against one filled source region. All closed
/// loops participate in the requested fill style: holes are never independent labelled
/// regions. Coordinates must already be transformed into the same plan space.
/// This does not invent dimension boundaries or infer a region's semantic label.
/// </summary>
public static class SectionRegionCoverageLogic
{
    public enum FillStyle { Normal, Outer, Ignore }
    public enum Coverage { None, Partial, Full }

    private const double CoordinateToleranceM = 1e-8;
    private const double ParameterTolerance = 1e-12;

    /// <summary>
    /// True only when every positive-length part of a nondegenerate span lies
    /// strictly inside the filled region. Endpoints may meet a region boundary;
    /// running along a boundary is deliberately not sufficient label evidence.
    /// The caller must supply every loop and the actual readable fill style.
    /// Unreadable or unsupported host geometry must not call this with fewer loops.
    /// </summary>
    public static bool CoversSegment(
        P2 start, P2 end, IReadOnlyList<IReadOnlyList<P2>> loops,
        FillStyle fillStyle = FillStyle.Normal) =>
        ClassifySegment(start, end, loops, fillStyle) == Coverage.Full;

    /// <summary>
    /// Distinguishes absent evidence from a real, partial region overlap.
    /// Filled seams are optional: the caller must first prove exact shared source
    /// straight edges between top-level components with disjoint interiors. They
    /// are not a way to waive holes, exterior boundaries, or near-touching loops.
    /// </summary>
    public static Coverage ClassifySegment(
        P2 start, P2 end, IReadOnlyList<IReadOnlyList<P2>> loops,
        FillStyle fillStyle = FillStyle.Normal,
        IReadOnlyList<SectionHatchBoundaryGeometry.StraightEdge>? provenFilledSeams = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        if (!Enum.IsDefined(fillStyle)) throw new ArgumentOutOfRangeException(nameof(fillStyle));
        RequireFinite(start);
        RequireFinite(end);
        if (provenFilledSeams != null)
            foreach (var seam in provenFilledSeams)
            {
                RequireFinite(seam.From);
                RequireFinite(seam.To);
                if (seam.From == seam.To)
                    throw new ArgumentException("A proven filled seam must have positive length.");
            }
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (!double.IsFinite(lengthSquared) ||
            lengthSquared <= CoordinateToleranceM * CoordinateToleranceM)
            throw new ArgumentException("The section span must have finite positive length.");
        if (loops.Count == 0) return Coverage.None;

        var cuts = new List<double> { 0, 1 };
        foreach (var loop in loops)
        {
            ValidateLoop(loop);
            for (var index = 0; index < loop.Count; index++)
            {
                var a = loop[index];
                var b = loop[(index + 1) % loop.Count];
                var ex = b.X - a.X;
                var ey = b.Y - a.Y;
                var qx = a.X - start.X;
                var qy = a.Y - start.Y;
                var denominator = Cross(dx, dy, ex, ey);
                if (Math.Abs(denominator) <= 1e-14)
                {
                    // A collinear boundary may overlap only part of the span.
                    // Split at both ends so its midpoint fails the strict-inside
                    // test instead of accidentally testing a different interval.
                    if (PointOnInfiniteLine(a, start, end))
                    {
                        AddCut(((a.X - start.X) * dx + (a.Y - start.Y) * dy) / lengthSquared);
                        AddCut(((b.X - start.X) * dx + (b.Y - start.Y) * dy) / lengthSquared);
                    }
                    continue;
                }
                var t = Cross(qx, qy, ex, ey) / denominator;
                var u = Cross(qx, qy, dx, dy) / denominator;
                if (u >= -ParameterTolerance && u <= 1 + ParameterTolerance) AddCut(t);
            }
        }

        cuts.Sort();
        var inside = false;
        var outside = false;
        for (var index = 1; index < cuts.Count; index++)
        {
            if (cuts[index] - cuts[index - 1] <= ParameterTolerance) continue;
            var t = (cuts[index] + cuts[index - 1]) / 2;
            var midpoint = new P2(start.X + dx * t, start.Y + dy * t);
            if (StrictlyInside(midpoint, loops, fillStyle) || OnProvenFilledSeam(midpoint))
                inside = true;
            else outside = true;
        }
        return !inside ? Coverage.None : outside ? Coverage.Partial : Coverage.Full;

        void AddCut(double t)
        {
            if (double.IsFinite(t) && t >= -ParameterTolerance && t <= 1 + ParameterTolerance)
                cuts.Add(Math.Clamp(t, 0, 1));
        }

        bool OnProvenFilledSeam(P2 point)
        {
            if (provenFilledSeams == null) return false;
            foreach (var seam in provenFilledSeams)
                if (PointOnInfiniteLine(point, seam.From, seam.To) &&
                    point.X >= Math.Min(seam.From.X, seam.To.X) &&
                    point.X <= Math.Max(seam.From.X, seam.To.X) &&
                    point.Y >= Math.Min(seam.From.Y, seam.To.Y) &&
                    point.Y <= Math.Max(seam.From.Y, seam.To.Y))
                    return true;
            return false;
        }
    }

    private static bool StrictlyInside(P2 point, IReadOnlyList<IReadOnlyList<P2>> loops, FillStyle fillStyle)
    {
        var depth = 0;
        foreach (var loop in loops)
        {
            var insideLoop = false;
            for (var index = 0; index < loop.Count; index++)
            {
                var a = loop[index];
                var b = loop[(index + 1) % loop.Count];
                if (PointOnInfiniteLine(point, a, b) &&
                    point.X >= Math.Min(a.X, b.X) - CoordinateToleranceM &&
                    point.X <= Math.Max(a.X, b.X) + CoordinateToleranceM &&
                    point.Y >= Math.Min(a.Y, b.Y) - CoordinateToleranceM &&
                    point.Y <= Math.Max(a.Y, b.Y) + CoordinateToleranceM)
                    return false;
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                    insideLoop = !insideLoop;
            }
            if (insideLoop) depth++;
        }
        return fillStyle switch
        {
            FillStyle.Normal => depth % 2 == 1,
            FillStyle.Outer => depth == 1,
            FillStyle.Ignore => depth > 0,
            _ => false,
        };
    }

    private static bool PointOnInfiniteLine(P2 point, P2 a, P2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length > CoordinateToleranceM &&
               Math.Abs(Cross(point.X - a.X, point.Y - a.Y, dx, dy)) <=
               CoordinateToleranceM * length;
    }

    private static void ValidateLoop(IReadOnlyList<P2>? loop)
    {
        if (loop == null || loop.Count < 3)
            throw new ArgumentException("Every source-region loop needs at least three vertices.");
        foreach (var point in loop) RequireFinite(point);
        // Use local coordinates to avoid cancellation at survey-grid magnitudes.
        var origin = loop[0];
        var twiceArea = 0.0;
        for (var index = 1; index < loop.Count - 1; index++)
            twiceArea += Cross(loop[index].X - origin.X, loop[index].Y - origin.Y,
                loop[index + 1].X - origin.X, loop[index + 1].Y - origin.Y);
        if (!double.IsFinite(twiceArea) || Math.Abs(twiceArea) <= 1e-12)
            throw new ArgumentException("A source-region loop has no finite nonzero area.");
    }

    private static void RequireFinite(P2 point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("Source-region and span coordinates must be finite.");
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
}
