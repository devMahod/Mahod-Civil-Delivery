using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Exact planar intersections in the host coordinate frame; never modifies CAD geometry.</summary>
public sealed class SpatialQuantityClip
{
    public enum Relation { Outside, Inside, Crossing }

    private readonly GeometryFactory _factory = new();
    private readonly IPreparedGeometry _prepared;
    public Polygon Boundary { get; }
    public IReadOnlyList<(double X, double Y)> Vertices { get; }

    public SpatialQuantityClip(IEnumerable<(double X, double Y)> vertices)
    {
        var points = vertices.Select(p => new Coordinate(p.X, p.Y)).ToList();
        if (points.Count < 3 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("התחום חייב לכלול לפחות שלוש נקודות תקינות");
        if (!points[0].Equals2D(points[^1])) points.Add(points[0].Copy());
        Boundary = _factory.CreatePolygon(points.ToArray());
        if (!Boundary.IsValid || Boundary.Area <= 0)
            throw new ArgumentException("התחום חייב להיות פוליגון סגור ללא חיתוך עצמי ובעל שטח חיובי");
        _prepared = PreparedGeometryFactory.Prepare(Boundary);
        Vertices = points.Take(points.Count - 1).Select(c => (c.X, c.Y)).ToArray();
    }
    public bool Contains(double x, double y) => _prepared.Covers(_factory.CreatePoint(new Coordinate(x, y)));

    /// <summary>
    /// Where a plan box lies: wholly inside (counted as is), wholly outside (dropped) or crossing (must be cut).
    /// A box is only a fast first test; crossing never means "partly counted" until the geometry itself is clipped.
    /// </summary>
    public Relation Classify(double minX, double minY, double maxX, double maxY)
    {
        var envelope = new Envelope(minX, maxX, minY, maxY);
        if (!Boundary.EnvelopeInternal.Intersects(envelope)) return Relation.Outside;
        var box = _factory.ToGeometry(envelope);
        if (_prepared.Covers(box)) return Relation.Inside;
        return _prepared.Intersects(box) ? Relation.Crossing : Relation.Outside;
    }

    public double SegmentLength((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        var dx = b.X-a.X; var dy = b.Y-a.Y; var dz = b.Z-a.Z;
        var planar = Math.Sqrt(dx*dx+dy*dy);
        if (planar == 0) return Contains(a.X,a.Y) ? Math.Abs(dz) : 0;
        var line = _factory.CreateLineString(new[] {new Coordinate(a.X,a.Y),new Coordinate(b.X,b.Y)});
        return line.Intersection(Boundary).Length / planar * Math.Sqrt(dx*dx+dy*dy+dz*dz);
    }
    public double Area(IEnumerable<(double X, double Y)> vertices)
    {
        var points = vertices.Select(p => new Coordinate(p.X,p.Y)).ToList();
        if (!points[0].Equals2D(points[^1])) points.Add(points[0].Copy());
        var polygon = _factory.CreatePolygon(points.ToArray());
        if (!polygon.IsValid) throw new ArgumentException("גבול השטח לחיתוך אינו תקין");
        return polygon.Intersection(Boundary).Area;
    }

    /// <summary>The part of a plan shape inside the boundary (an empty geometry when nothing is inside).</summary>
    public Geometry ClipShape(Geometry shape) => shape.Intersection(Boundary);

    /// <summary>A closed ring's plan polygon clipped to the boundary; self-touching rings are repaired by a zero buffer.</summary>
    public Geometry ClipRing(IReadOnlyList<(double X, double Y)> ring)
    {
        var points = ring.Select(p => new Coordinate(p.X, p.Y)).ToList();
        if (points.Count < 3) return _factory.CreatePolygon();
        if (!points[0].Equals2D(points[^1])) points.Add(points[0].Copy());
        Geometry polygon = _factory.CreatePolygon(points.ToArray());
        if (!polygon.IsValid) polygon = polygon.Buffer(0);
        return polygon.Intersection(Boundary);
    }

    /// <summary>
    /// The parts of a 3D path whose plan lies inside the boundary (edges included), each a run of points with the
    /// elevation interpolated where the path crosses the boundary. The 3D length of the pieces is the clipped length.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>> ClipPath(IReadOnlyList<(double X, double Y, double Z)> path)
    {
        var pieces = new List<IReadOnlyList<(double X, double Y, double Z)>>();
        List<(double X, double Y, double Z)>? current = null;
        void Close() { if (current is { Count: >= 2 }) pieces.Add(current); current = null; }
        void Append((double X, double Y, double Z) p)
        {
            current ??= new List<(double X, double Y, double Z)>();
            if (current.Count == 0 || !Same(current[^1], p)) current.Add(p);
        }
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i]; var b = path[i + 1];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var planar2 = dx * dx + dy * dy;
            if (planar2 == 0)
            {
                // A vertical (or zero) step: inside or outside as a whole.
                if (Contains(a.X, a.Y)) { Append(a); Append(b); } else Close();
                continue;
            }
            var cuts = new List<double> { 0, 1 };
            var segment = _factory.CreateLineString(new[] { new Coordinate(a.X, a.Y), new Coordinate(b.X, b.Y) });
            foreach (var c in segment.Intersection(Boundary.Boundary).Coordinates)
            {
                var t = ((c.X - a.X) * dx + (c.Y - a.Y) * dy) / planar2;
                if (t > 0 && t < 1) cuts.Add(t);
            }
            cuts.Sort();
            for (var k = 0; k + 1 < cuts.Count; k++)
            {
                var t0 = cuts[k]; var t1 = cuts[k + 1];
                if (t1 - t0 <= 1e-15) continue;
                var mid = At(a, b, (t0 + t1) / 2);
                if (Contains(mid.X, mid.Y)) { Append(At(a, b, t0)); Append(At(a, b, t1)); }
                else Close();
            }
        }
        Close();
        return pieces;
    }

    public static double Length3d(IReadOnlyList<(double X, double Y, double Z)> piece)
    {
        double length = 0;
        for (var i = 0; i + 1 < piece.Count; i++)
        {
            var dx = piece[i + 1].X - piece[i].X; var dy = piece[i + 1].Y - piece[i].Y; var dz = piece[i + 1].Z - piece[i].Z;
            length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return length;
    }

    private static (double X, double Y, double Z) At((double X, double Y, double Z) a, (double X, double Y, double Z) b, double t)
        => t <= 0 ? a : t >= 1 ? b : (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    private static bool Same((double X, double Y, double Z) p, (double X, double Y, double Z) q)
        => p.X == q.X && p.Y == q.Y && p.Z == q.Z;
}
