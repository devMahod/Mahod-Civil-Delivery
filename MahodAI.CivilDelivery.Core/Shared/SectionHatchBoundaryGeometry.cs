using System;
using System.Collections.Generic;
using System.Linq;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Hatch-only boundary interpretation in its 2D object plane. Native adapters
/// copy scalar vertices into this helper, then apply the full OCS/WCS transform.
/// No topology is repaired, polygonized or simplified. Curve endpoint recognition
/// is explicitly bounded; it does not permit closing an engineering-scale gap.
/// </summary>
public static class SectionHatchBoundaryGeometry
{
    public readonly record struct BulgePoint(double X, double Y, double Bulge);
    public readonly record struct StraightEdge(P2 From, P2 To);
    public sealed record PolylineLoop(IReadOnlyList<P2> Points, bool HasCurves,
        IReadOnlyList<StraightEdge> SourceStraightEdges);
    public const double ClosureToleranceM = 1e-7;
    // Opt-in for unflagged native Hatch curve loops only, not a general CAD,
    // polygon-validity, seam, width or area tolerance. Current HA 262391 has a
    // 0.229 micrometre arc/arc terminal mismatch; its other joins are <= 3e-11 m.
    // One micrometre bounds endpoint recognition; all loops still undergo the
    // unchanged complete-region topology checks. Default callers stay strict.
    public const double MicrometricEndpointToleranceM = 1e-6;

    public static int ArcSegmentCount(double radiusM, double sweepRadians, double maxSagittaM) =>
        // A curved edge and its chord enclose a real region even when its sagitta
        // is below the sampling error. One segment would destroy that topology.
        Math.Max(2, ArcTessellationSegmentCount(radiusM, sweepRadians, maxSagittaM));

    public static PolylineLoop TessellateClosedPolyline(
        IReadOnlyList<BulgePoint> source, double maximumLinearScale = 1,
        double maxSagittaM = 0.005, int maximumVertices = 100000)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(maximumLinearScale) || maximumLinearScale <= 0 ||
            !double.IsFinite(maxSagittaM) || maxSagittaM <= 0 || maximumVertices < 3)
            throw new ArgumentException("Invalid bounded hatch tessellation parameters.");
        if (source.Count < 2 || source.Count > maximumVertices || source.Any(vertex =>
                !double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Bulge)))
            throw new ArgumentException("Hatch polyline vertices are empty, non-finite or exceed the reader limit.");

        var vertices = source.ToList();
        // GetLoopAt may expose an explicitly repeated terminal vertex. It is a
        // closure marker, not a further zero-length arc to be invented from its
        // unused bulge. This does not remove an interior zero-length bulged edge.
        while (vertices.Count > 1 && SamePoint(vertices[0], vertices[^1]))
            vertices.RemoveAt(vertices.Count - 1);
        if (vertices.Count < 2) throw new ArgumentException("Hatch boundary has no nondegenerate closed path.");

        var points = new List<P2> { new(vertices[0].X, vertices[0].Y) };
        var straightEdges = new List<StraightEdge>();
        var curved = false;
        for (var index = 0; index < vertices.Count; index++)
        {
            var a = vertices[index];
            var b = vertices[(index + 1) % vertices.Count];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(chord)) throw new ArgumentException("Hatch chord is non-finite.");
            var isArc = Math.Abs(a.Bulge) > 1e-12;
            if (chord == 0)
            {
                if (isArc) throw new ArgumentException("An interior zero-length bulged edge cannot define an arc.");
                continue; // A repeated straight vertex contributes no geometry.
            }
            var count = isArc ? ArcSegmentCount(BulgeRadius(chord, a.Bulge) * maximumLinearScale,
                Math.Abs(BulgeSweepRadians(a.Bulge)), maxSagittaM) : 1;
            if (points.Count + count > maximumVertices)
                throw new InvalidOperationException("Hatch tessellation exceeds the bounded reader limit.");
            curved |= isArc;
            // Sampling may approximate an extremely small bulge, but only an
            // exactly zero source bulge can prove a shared straight seam.
            if (a.Bulge == 0) straightEdges.Add(new(new(a.X, a.Y), new(b.X, b.Y)));
            for (var sample = 1; sample <= count; sample++)
                points.Add(sample == count ? new P2(b.X, b.Y) :
                    PointOnBulge(a, b, (double)sample / count));
        }
        if (points.Count < 4 || points[0] != points[^1])
            throw new ArgumentException("Hatch boundary has fewer than three distinct sampled vertices.");
        points.RemoveAt(points.Count - 1);
        return new PolylineLoop(points.AsReadOnly(), curved, straightEdges.AsReadOnly());
    }

    public static P2 PointOnBulge(BulgePoint a, BulgePoint b, double fraction)
    {
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1)
            throw new ArgumentOutOfRangeException(nameof(fraction));
        if (fraction == 0) return new(a.X, a.Y);
        if (fraction == 1) return new(b.X, b.Y);
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (Math.Abs(a.Bulge) <= 1e-12)
            return new(a.X + dx * fraction, a.Y + dy * fraction);
        if (!double.IsFinite(chord) || chord <= 0 || !double.IsFinite(a.Bulge))
            throw new ArgumentException("A bulge arc needs finite distinct endpoints and bulge.");
        var angle = BulgeSweepRadians(a.Bulge) * fraction;
        var halfSine = Math.Sin(angle / 2);
        var oneMinusCosine = 2 * halfSine * halfSine;
        var apothem = chord * (1 - a.Bulge * a.Bulge) / (4 * a.Bulge);
        // Local chord coordinates avoid subtracting large, nearly equal WCS
        // circle centers for small bulges. Positive bulges remain counterclockwise.
        var x = chord / 2 * oneMinusCosine + apothem * Math.Sin(angle);
        var y = apothem * oneMinusCosine - chord / 2 * Math.Sin(angle);
        var result = new P2(a.X + dx / chord * x - dy / chord * y,
            a.Y + dy / chord * x + dx / chord * y);
        if (!double.IsFinite(result.X) || !double.IsFinite(result.Y))
            throw new ArgumentException("Hatch bulge evaluation is non-finite.");
        return result;
    }

    public static IReadOnlyList<P2> JoinClosedEdges(
        IReadOnlyList<IReadOnlyList<P2>> edges, int maximumVertices = 100000) =>
        JoinClosedEdgesWithEndpointTolerance(edges, maximumVertices, ClosureToleranceM);

    public static IReadOnlyList<P2> JoinClosedEdgesWithEndpointTolerance(
        IReadOnlyList<IReadOnlyList<P2>> edges, int maximumVertices = 100000,
        double endpointToleranceM = ClosureToleranceM)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (!double.IsFinite(endpointToleranceM) || endpointToleranceM < 0 ||
            endpointToleranceM > MicrometricEndpointToleranceM)
            throw new ArgumentOutOfRangeException(nameof(endpointToleranceM),
                "Hatch endpoint recognition must remain bounded to one micrometre.");
        var points = new List<P2>();
        foreach (var edge in edges)
        {
            if (edge.Count < 2 || edge.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                throw new ArgumentException("Hatch edge is empty or non-finite.");
            if (points.Count > 0 && Distance(points[^1], edge[0]) > endpointToleranceM)
                throw new ArgumentException("Hatch loop has disconnected directed edges.");
            if (points.Count == 0) points.Add(edge[0]);
            if (points.Count + edge.Count - 1 > maximumVertices)
                throw new InvalidOperationException("Hatch tessellation exceeds the bounded reader limit.");
            points.AddRange(edge.Skip(1));
        }
        if (points.Count < 4) throw new ArgumentException("Hatch loop has fewer than three sampled vertices.");
        if (Distance(points[0], points[^1]) > endpointToleranceM)
            throw new ArgumentException("Hatch loop is not geometrically closed.");
        points.RemoveAt(points.Count - 1);
        return points.AsReadOnly();
    }

    private static bool SamePoint(BulgePoint a, BulgePoint b) => a.X == b.X && a.Y == b.Y;
    private static double Distance(P2 a, P2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
