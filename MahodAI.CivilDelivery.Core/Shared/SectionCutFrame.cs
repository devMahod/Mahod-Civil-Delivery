using System;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Coordinates on one straight, endpoint-bounded CL cut. Distances are along the
/// cut, not perpendicular alignment offsets. Increasing station establishes LEFT
/// and RIGHT; reversing the source entity never reverses the coordinate frame.
/// This is the application's geometry contract, not a claim about an undocumented
/// Civil API. The host must independently check Civil's readable left endpoint.
/// </summary>
public sealed class SectionCutFrame
{
    private readonly double rightX;
    private readonly double rightY;
    private readonly double tangentX;
    private readonly double tangentY;
    private readonly double perpendicularScale;

    private SectionCutFrame(P2 a, P2 b, P2 origin, double tx, double ty,
        double rx, double ry, double oa, double ob)
    {
        EndpointA = a; EndpointB = b; Origin = origin;
        tangentX = tx; tangentY = ty; rightX = rx; rightY = ry;
        perpendicularScale = rx * ty - ry * tx;
        OffsetA = oa; OffsetB = ob;
        MinOffset = Math.Min(oa, ob); MaxOffset = Math.Max(oa, ob);
        LeftEndpoint = oa < ob ? a : b;
    }

    public P2 EndpointA { get; }
    public P2 EndpointB { get; }
    public P2 Origin { get; }
    public P2 LeftEndpoint { get; }
    public double OffsetA { get; }
    public double OffsetB { get; }
    public double MinOffset { get; }
    public double MaxOffset { get; }
    public double Width => MaxOffset - MinOffset;

    public static bool TryCreate(P2 a, P2 b, P2 origin, double tangentDeg,
        out SectionCutFrame? frame)
    {
        frame = null;
        if (!Finite(a) || !Finite(b) || !Finite(origin) || !double.IsFinite(tangentDeg))
            return false;
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < 0.01) return false;
        dx /= length; dy /= length;
        // A crossing off the actual cut must not be silently projected onto it.
        if (Math.Abs((origin.X - a.X) * dy - (origin.Y - a.Y) * dx) > 1e-6)
            return false;
        var radians = tangentDeg * Math.PI / 180;
        var tx = Math.Cos(radians); var ty = Math.Sin(radians);
        var right = dx * ty - dy * tx;
        if (Math.Abs(right) < 1e-8) return false;
        if (right < 0) { dx = -dx; dy = -dy; }
        var oa = (a.X - origin.X) * dx + (a.Y - origin.Y) * dy;
        var ob = (b.X - origin.X) * dx + (b.Y - origin.Y) * dy;
        if (Math.Min(oa, ob) >= -0.01 || Math.Max(oa, ob) <= 0.01) return false;
        frame = new SectionCutFrame(a, b, origin, tx, ty, dx, dy, oa, ob);
        return true;
    }

    /// <summary>Along-cut coordinate of a point already intersected with the CL.</summary>
    public double OffsetOf(P2 point) =>
        (point.X - Origin.X) * rightX + (point.Y - Origin.Y) * rightY;

    public P2 PointAt(double offset) =>
        new(Origin.X + offset * rightX, Origin.Y + offset * rightY);

    /// <summary>
    /// Projects a nearby plan arrow parallel to the alignment tangent onto the CL.
    /// This preserves its lateral lane position without treating its longitudinal
    /// distance from the cut as a lane change on a skewed section.
    /// </summary>
    public double OffsetAtAlignmentProjection(P2 point) =>
        ((point.X - Origin.X) * tangentY - (point.Y - Origin.Y) * tangentX) /
        perpendicularScale;

    public bool IsContainedInDisplay(double displayMin, double displayMax) =>
        double.IsFinite(displayMin) && double.IsFinite(displayMax) &&
        displayMin <= MinOffset + 0.0005 && displayMax >= MaxOffset - 0.0005;

    public bool MatchesNativeLeftEndpoint(P2 nativeLeft) => Finite(nativeLeft) &&
        Math.Abs(nativeLeft.X - LeftEndpoint.X) <= 1e-6 &&
        Math.Abs(nativeLeft.Y - LeftEndpoint.Y) <= 1e-6;

    private static bool Finite(P2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
