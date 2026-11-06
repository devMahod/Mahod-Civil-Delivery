using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// Area of a hatch measured from its boundary loops, for the hatches whose own area AutoCAD cannot compute
/// (<c>Hatch.Area</c> throws on self-touching or self-intersecting loops). Live 2026-11-06, the 4NATIVIM interchange plan:
/// 15 such hatches held about 59,000 m² of the main carriageway, and the bill of quantities silently left them out.
/// <para>
/// Each loop is a closed ring in the hatch's own plane. A self-intersecting ring is repaired the way AutoCAD fills it
/// (every lobe counts once). Loops are combined by nesting, as the hatch style fills them: Normal alternates fill and
/// hole at every level, Outer fills the outermost loops and cuts their first islands only, Ignore fills the outermost
/// loops only. The result is a quantity for review, never a silent number: the caller reports every recovered area.
/// </para>
/// </summary>
public static class HatchBoundaryArea
{
    public sealed record Result(double Area, Geometry Shape, int LoopsUsed, int LoopsRepaired);

    private static readonly GeometryFactory Factory = new();

    /// <summary>Recovers the filled region of the hatch. Coordinates are in drawing units; the area is in drawing units².</summary>
    public static bool TryMeasure(IReadOnlyList<IReadOnlyList<(double X, double Y)>> loops, string style, out Result? result, out string refusal)
    {
        result = null; refusal = "";
        if (loops == null || loops.Count == 0) { refusal = "ל־Hatch אין לולאות גבול"; return false; }
        var rings = new List<Geometry>();
        var repaired = 0;
        foreach (var loop in loops)
        {
            var points = Distinct(loop);
            if (points.Count < 3) continue;   // a degenerate sliver loop fills nothing
            var coordinates = points.Select(p => new Coordinate(p.X, p.Y)).Append(new Coordinate(points[0].X, points[0].Y)).ToArray();
            Geometry polygon;
            try { polygon = Factory.CreatePolygon(coordinates); }
            catch (ArgumentException) { continue; }
            if (!polygon.IsValid) { polygon = GeometryFixer.Fix(polygon); repaired++; }
            if (polygon.IsEmpty || !(polygon.Area > 0)) continue;
            rings.Add(polygon);
        }
        if (rings.Count == 0) { refusal = "לולאות הגבול אינן סוגרות שטח"; return false; }

        // Nesting depth of a loop = how many other loops contain a point inside it.
        var depth = rings.Select((ring, i) =>
        {
            var inside = ring.InteriorPoint;
            return rings.Where((other, j) => j != i && other.Area > ring.Area * (1 - 1e-12) && other.Contains(inside)).Count();
        }).ToArray();
        var maximumDepth = style switch { "Ignore" => 0, "Outer" => 1, _ => int.MaxValue };
        Geometry shape = Factory.CreatePolygon();
        var used = 0;
        for (var i = 0; i < rings.Count; i++)
        {
            if (depth[i] > maximumDepth) continue;
            shape = shape.IsEmpty ? rings[i] : shape.SymmetricDifference(rings[i]);
            used++;
        }
        shape = GeometryFixer.Fix(shape);
        var area = shape.Area;
        if (!double.IsFinite(area) || area <= 0 || shape is not (Polygon or MultiPolygon or GeometryCollection))
        { refusal = "השטח שחושב מהגבול אינו חיובי"; return false; }
        result = new Result(area, shape, used, repaired);
        return true;
    }

    /// <summary>The polygons of a recovered shape: each as its outer ring and its holes, ready to rebuild in the drawing.</summary>
    public static IEnumerable<(IReadOnlyList<(double X, double Y)> Outer, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Holes)> Polygons(Geometry shape)
    {
        for (var i = 0; i < shape.NumGeometries; i++)
            if (shape.GetGeometryN(i) is Polygon p && !p.IsEmpty && p.Area > 0)
                yield return (Ring(p.ExteriorRing), p.InteriorRings.Select(Ring).ToArray());
    }

    private static IReadOnlyList<(double X, double Y)> Ring(LineString ring) =>
        ring.Coordinates.Take(ring.NumPoints - 1).Select(c => (c.X, c.Y)).ToArray();

    private static List<(double X, double Y)> Distinct(IReadOnlyList<(double X, double Y)> loop)
    {
        var points = new List<(double X, double Y)>(loop.Count);
        foreach (var p in loop)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            if (points.Count > 0 && points[^1].X == p.X && points[^1].Y == p.Y) continue;
            points.Add(p);
        }
        while (points.Count > 1 && points[^1].X == points[0].X && points[^1].Y == points[0].Y) points.RemoveAt(points.Count - 1);
        return points;
    }
}
