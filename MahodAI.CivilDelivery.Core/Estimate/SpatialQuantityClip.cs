using NetTopologySuite.Geometries;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Exact planar intersections in the host coordinate frame; never modifies CAD geometry.</summary>
public sealed class SpatialQuantityClip
{
    private readonly GeometryFactory _factory = new();
    public Polygon Boundary { get; }
    public SpatialQuantityClip(IEnumerable<(double X, double Y)> vertices)
    {
        var points = vertices.Select(p => new Coordinate(p.X, p.Y)).ToList();
        if (points.Count < 3 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("התחום חייב לכלול לפחות שלוש נקודות תקינות");
        if (!points[0].Equals2D(points[^1])) points.Add(points[0].Copy());
        Boundary = _factory.CreatePolygon(points.ToArray());
        if (!Boundary.IsValid || Boundary.Area <= 0)
            throw new ArgumentException("התחום חייב להיות פוליגון סגור ללא חיתוך עצמי ובעל שטח חיובי");
    }
    public bool Contains(double x, double y) => Boundary.Covers(_factory.CreatePoint(new Coordinate(x, y)));
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
}
