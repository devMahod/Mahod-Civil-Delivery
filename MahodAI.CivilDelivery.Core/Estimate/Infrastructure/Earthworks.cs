using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

public readonly record struct Xyz(double X, double Y, double Z);

/// <summary>
/// A triangulated surface (TIN) with a bucket index, so the elevation at any plan point is one short lookup. Built from a
/// Civil 3D surface's own triangles, or from survey points (Delaunay) when the drawing has no surface.
/// </summary>
public sealed class TinModel
{
    private readonly double[] _v;            // 9 values per triangle: x1 y1 z1 x2 y2 z2 x3 y3 z3
    private readonly int[] _start, _items;   // buckets (CSR)
    private readonly int _nx, _ny;
    private readonly double _bucket;
    public int Triangles { get; }
    public double MinX { get; } public double MinY { get; } public double MaxX { get; } public double MaxY { get; }
    public double MinZ { get; } public double MaxZ { get; }

    public TinModel(IReadOnlyList<(Xyz A, Xyz B, Xyz C)> triangles)
    {
        var good = triangles.Where(t => Finite(t.A) && Finite(t.B) && Finite(t.C) && Math.Abs(Cross(t.A, t.B, t.C)) > 1e-9).ToArray();
        Triangles = good.Length;
        _v = new double[good.Length * 9];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        for (var i = 0; i < good.Length; i++)
        {
            var (a, b, c) = good[i];
            var o = i * 9;
            _v[o] = a.X; _v[o + 1] = a.Y; _v[o + 2] = a.Z; _v[o + 3] = b.X; _v[o + 4] = b.Y; _v[o + 5] = b.Z; _v[o + 6] = c.X; _v[o + 7] = c.Y; _v[o + 8] = c.Z;
            foreach (var p in new[] { a, b, c })
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
        }
        if (good.Length == 0) { MinX = MinY = MaxX = MaxY = MinZ = MaxZ = 0; _nx = _ny = 1; _bucket = 1; _start = new[] { 0, 0 }; _items = Array.Empty<int>(); return; }
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; MinZ = minZ; MaxZ = maxZ;
        var w = Math.Max(maxX - minX, 1e-6); var h = Math.Max(maxY - minY, 1e-6);
        _bucket = Math.Max(Math.Sqrt(w * h / Math.Max(1, good.Length / 2.0)), 1e-3);
        _nx = Math.Max(1, (int)Math.Ceiling(w / _bucket)); _ny = Math.Max(1, (int)Math.Ceiling(h / _bucket));
        while ((long)_nx * _ny > 8_000_000) { _bucket *= 1.5; _nx = Math.Max(1, (int)Math.Ceiling(w / _bucket)); _ny = Math.Max(1, (int)Math.Ceiling(h / _bucket)); }
        var counts = new int[_nx * _ny + 1];
        ForEachBucket((i, b) => counts[b + 1]++);
        for (var i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];
        _start = counts;
        _items = new int[counts[^1]];
        var fill = (int[])counts.Clone();
        ForEachBucket((i, b) => _items[fill[b]++] = i);
    }

    private void ForEachBucket(Action<int, int> visit)
    {
        for (var i = 0; i < Triangles; i++)
        {
            var o = i * 9;
            var x0 = Math.Min(_v[o], Math.Min(_v[o + 3], _v[o + 6])); var x1 = Math.Max(_v[o], Math.Max(_v[o + 3], _v[o + 6]));
            var y0 = Math.Min(_v[o + 1], Math.Min(_v[o + 4], _v[o + 7])); var y1 = Math.Max(_v[o + 1], Math.Max(_v[o + 4], _v[o + 7]));
            var i0 = Col(x0); var i1 = Col(x1); var j0 = Row(y0); var j1 = Row(y1);
            for (var j = j0; j <= j1; j++) for (var k = i0; k <= i1; k++) visit(i, j * _nx + k);
        }
    }
    private int Col(double x) => Math.Clamp((int)((x - MinX) / _bucket), 0, _nx - 1);
    private int Row(double y) => Math.Clamp((int)((y - MinY) / _bucket), 0, _ny - 1);

    /// <summary>The elevation at (x, y), or false outside every triangle.</summary>
    public bool TryElevation(double x, double y, out double z)
    {
        z = double.NaN;
        if (Triangles == 0 || x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
        var b = Row(y) * _nx + Col(x);
        for (var k = _start[b]; k < _start[b + 1]; k++)
        {
            var o = _items[k] * 9;
            double x1 = _v[o], y1 = _v[o + 1], x2 = _v[o + 3], y2 = _v[o + 4], x3 = _v[o + 6], y3 = _v[o + 7];
            var d = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
            var l1 = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / d;
            var l2 = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / d;
            var l3 = 1 - l1 - l2;
            const double eps = -1e-9;
            if (l1 < eps || l2 < eps || l3 < eps) continue;
            z = l1 * _v[o + 2] + l2 * _v[o + 5] + l3 * _v[o + 8];
            return true;
        }
        return false;
    }

    public IEnumerable<(Xyz A, Xyz B, Xyz C)> Each()
    {
        for (var i = 0; i < Triangles; i++)
        {
            var o = i * 9;
            yield return (new Xyz(_v[o], _v[o + 1], _v[o + 2]), new Xyz(_v[o + 3], _v[o + 4], _v[o + 5]), new Xyz(_v[o + 6], _v[o + 7], _v[o + 8]));
        }
    }

    /// <summary>
    /// Delaunay triangles over survey points (1 cm apart at least; the first of two coincident points wins). Triangles
    /// with an edge longer than <paramref name="maxEdge"/> are dropped — they bridge the gaps around and between surveyed
    /// strips, where nothing was measured (0 = automatic: the larger of 100 m and five times the 75th-percentile edge).
    /// </summary>
    public static TinModel FromPoints(IEnumerable<Xyz> points, double maxEdge = 0)
    {
        var unique = new Dictionary<(long, long), Xyz>();
        foreach (var p in points.Where(Finite))
            unique.TryAdd(((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100)), p);
        if (unique.Count < 3) return new TinModel(Array.Empty<(Xyz, Xyz, Xyz)>());
        var z = new Dictionary<(double, double), double>(unique.Count);
        var coords = new List<Coordinate>(unique.Count);
        foreach (var p in unique.Values) { coords.Add(new Coordinate(p.X, p.Y)); z[(p.X, p.Y)] = p.Z; }
        var builder = new DelaunayTriangulationBuilder { Tolerance = 0.005 };
        builder.SetSites(coords);
        var polys = builder.GetTriangles(new GeometryFactory());
        var tris = new List<(Xyz, Xyz, Xyz)>(polys.NumGeometries);
        var longest = new List<double>(polys.NumGeometries);
        for (var i = 0; i < polys.NumGeometries; i++)
        {
            var c = polys.GetGeometryN(i).Coordinates;
            if (c.Length < 3) continue;
            if (!z.TryGetValue((c[0].X, c[0].Y), out var z0) || !z.TryGetValue((c[1].X, c[1].Y), out var z1) || !z.TryGetValue((c[2].X, c[2].Y), out var z2)) continue;
            tris.Add((new Xyz(c[0].X, c[0].Y, z0), new Xyz(c[1].X, c[1].Y, z1), new Xyz(c[2].X, c[2].Y, z2)));
            longest.Add(Math.Max(c[0].Distance(c[1]), Math.Max(c[1].Distance(c[2]), c[2].Distance(c[0]))));
        }
        if (tris.Count == 0) return new TinModel(Array.Empty<(Xyz, Xyz, Xyz)>());
        if (maxEdge <= 0)
        {
            var sorted = longest.OrderBy(x => x).ToArray();
            maxEdge = Math.Max(100, 5 * sorted[(int)(sorted.Length * 0.75)]);
        }
        return new TinModel(tris.Where((t, i) => longest[i] <= maxEdge).ToArray());
    }

    private static bool Finite(Xyz p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static double Cross(Xyz a, Xyz b, Xyz c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}

/// <summary>
/// Survey data read off a drawing that has no ground surface: spot heights, survey points, breaklines and contours on the
/// relief layers of the Survey of Israel (מפ״י) codes — M1501 survey points, M1502 typical spot heights, M1505, M1506/M1507
/// contours, M1508–M1513 breaklines, slopes and embankments — or on layers named TOPO / CONTOUR / SPOT / קווי גובה. Text
/// labels (M1500, M1509) are not ground. Survey files carry blunders: a level of 0 among levels of 20 m, a "level" that is
/// a coordinate (93,436 m); both are dropped here, and isolated spikes after the TIN is built.
/// </summary>
public static class SurveyGround
{
    private static readonly Regex Relief = new(
        @"(?:^|[|_\-\s])(?:M15(?:0[1-8]|1[0-3])|C-TOPO|V-TOPO|TOPO\w*|CONTOURS?|CONT|SPOT\w*|EG|SR-?TOPO|קווי[\s_-]?גובה|נקודות[\s_-]?גובה|נקודת[\s_-]?גובה|גבהים)(?:$|[|_\-\s])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Labels = new(@"(?:^|[|])M15(?:00|09)(?:$|[_\-\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A layer (with or without its XREF namespace) whose objects are ground levels.</summary>
    public static bool IsReliefLayer(string layer)
    {
        var leaf = layer.Split('|').Last();
        return !Labels.IsMatch(leaf) && Relief.IsMatch(leaf);
    }

    /// <summary>Israel's land lies between the Dead Sea shore (−430 m) and the Hermon (2,814 m).</summary>
    public const double LowestPlausible = -440, HighestPlausible = 2850;

    public sealed record Cleaned(IReadOnlyList<Xyz> Points, int Zero, int Implausible, int Spikes);

    /// <summary>Drops impossible levels, zero levels among real ones, then isolated spikes against their TIN neighbours.</summary>
    public static Cleaned Clean(IReadOnlyList<Xyz> raw, ISet<int>? breaklinePoints = null)
    {
        var plausible = raw.Where(p => p.Z > LowestPlausible && p.Z < HighestPlausible).ToList();
        var implausible = raw.Count - plausible.Count;
        var zero = 0;
        if (plausible.Count > 0)
        {
            var median = Median(plausible.Select(p => p.Z));
            if (Math.Abs(median) > 2)
            {
                zero = plausible.RemoveAll(p => Math.Abs(p.Z) < 1e-6);
            }
        }
        // Spikes: a point whose level is far from every neighbour's (more than 3 m and five times their spread).
        var spikes = 0;
        if (plausible.Count > 20)
        {
            var tin = TinModel.FromPoints(plausible, maxEdge: 1e9);
            var neighbours = new Dictionary<(double, double), List<double>>();
            foreach (var (a, b, c) in tin.Each())
            {
                Link(a, b); Link(a, c); Link(b, a); Link(b, c); Link(c, a); Link(c, b);
            }
            void Link(Xyz p, Xyz q)
            {
                if (!neighbours.TryGetValue((p.X, p.Y), out var l)) neighbours[(p.X, p.Y)] = l = new List<double>();
                l.Add(q.Z);
            }
            var bad = new HashSet<(double, double)>();
            foreach (var p in plausible)
            {
                if (!neighbours.TryGetValue((p.X, p.Y), out var l) || l.Count < 3) continue;
                var m = Median(l);
                var spread = Median(l.Select(v => Math.Abs(v - m)));
                if (Math.Abs(p.Z - m) > Math.Max(3, 5 * spread) && l.All(v => Math.Abs(p.Z - v) > 2)) bad.Add((p.X, p.Y));
            }
            spikes = plausible.RemoveAll(p => bad.Contains((p.X, p.Y)));
        }
        return new Cleaned(plausible, zero, implausible, spikes);
    }

    /// <summary>Points along a polyline (contour or breakline) at most <paramref name="step"/> apart.</summary>
    public static IEnumerable<Xyz> Densify(IReadOnlyList<Xyz> vertices, double step = 10)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            yield return vertices[i];
            if (i + 1 >= vertices.Count) yield break;
            var a = vertices[i]; var b = vertices[i + 1];
            var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var n = (int)Math.Floor(len / step);
            for (var k = 1; k <= n && len > 0; k++)
            {
                var t = k * step / len;
                if (t >= 1) break;
                yield return new Xyz(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
            }
        }
    }

    internal static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return s.Length == 0 ? double.NaN : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}

/// <summary>What a surface in the drawing is, from its name, layer and description.</summary>
public enum EarthRole { Unknown, Existing, Design, Subgrade }

/// <summary>
/// Earthworks for the bill of quantities: the ground as it is (a Civil 3D surface, or one built from the survey) against the
/// design — a design or corridor surface, else a level platform (typed, or the level that balances cut and fill) — inside
/// the marked area or the design's own footprint. Follows the Israeli practice the price list is written for (המפרט
/// הכללי פרק 51 as quoted in tender specifications; ministry guidelines for topography and topsoil):
/// <list type="bullet">
/// <item>Volumes are measured in place (bank m³) between the ground before the works and the design lines.</item>
/// <item>Earthworks end at the subgrade (פני השתית): under a road surface the design surface is lowered by the pavement
/// structure the bill already prices (report, typed or road-type defaults), unless the design surface is itself a datum.</item>
/// <item>Topsoil is stripped first (20 cm, priced per m²): cut is measured from the stripped surface, and fill makes up the
/// stripped layer too.</item>
/// <item>Cut used in fill is one item (its haul and spreading are included in the cut price); surplus cut taken away is
/// another; fill compaction is paid per m³; fill the cut cannot supply is imported fill.</item>
/// <item>Cut and fill are summed separately over the cells, never netted inside one cell.</item>
/// </list>
/// </summary>
public static class Earthworks
{
    public const string DerivedSource = "עבודות עפר — ניתוח טופוגרפי";
    public const string DerivedPart = "עפר";
    public const string Topic = "עבודות עפר";
    public const string DefaultMark = "ברירת מחדל";

    // ───────────── which surface is which ─────────────

    private static readonly string[] ExistingWords = { "EG", "EXG", "OG", "NG", "EXIST", "EXISTING", "TOPO", "SURVEY", "NATURAL", "ORIGINAL", "GROUND", "MK", "KAYAM", "KAIAM", "TOPOGRAPHY", "קיים", "קיימת", "טופו", "טופוגרפיה", "מדידה", "טבעי", "טבעית", "מצב" };
    private static readonly string[] SubgradeWords = { "DATUM", "SUBGRADE", "SG", "BOT", "BOTTOM", "FORMATION", "שתית", "תחתית" };
    private static readonly string[] DesignWords = { "FG", "FINISH", "FINISHED", "DESIGN", "PROP", "PROPOSED", "TOP", "PAVE", "PAVEMENT", "GRADING", "CORRIDOR", "PLAN", "FINAL", "תכנון", "מתוכנן", "מתוכננת", "סופי", "סופית", "כביש" };

    public static EarthRole Classify(string name, string layer = "", string description = "")
    {
        var tokens = Regex.Split((name + " " + layer + " " + description).ToUpperInvariant(), @"[^\p{L}\p{Nd}]+").Where(t => t.Length > 0).ToHashSet();
        bool Any(string[] words) => words.Any(tokens.Contains);
        if (Any(SubgradeWords)) return EarthRole.Subgrade;
        if (Any(DesignWords) && !Any(new[] { "EG", "EXIST", "EXISTING", "קיים", "קיימת" })) return EarthRole.Design;
        if (Any(ExistingWords)) return EarthRole.Existing;
        return EarthRole.Unknown;
    }

    // ───────────── settings ─────────────

    public enum DesignKind { Surface, Platform }

    /// <summary>The engineer's choices for one drawing (a marked area uses the same factors). Every default is marked.</summary>
    public sealed record Settings
    {
        /// <summary>Topsoil stripped before the works (cm). The specifications strip 20 cm (one uses 15).</summary>
        public double StripCm { get; init; } = 20;
        /// <summary>Topsoil kept in a stockpile for re-use (MoT topsoil guidance) — else stripped and taken away.</summary>
        public bool StripToStockpile { get; init; } = true;
        /// <summary>m³ of compacted fill one m³ of cut (in place) makes. Sand 0.90–0.95, clay 0.85–0.90.</summary>
        public double Shrink { get; init; } = 0.90;
        /// <summary>Loose m³ (in the truck) per m³ in place. For the haul count only — the bill is in place.</summary>
        public double Swell { get; init; } = 1.25;
        /// <summary>Share of the cut that is rock (%). The specifications include rock in the cut price: 0 = no separate item.</summary>
        public double RockPercent { get; init; }
        /// <summary>Share of the cut not fit for fill (%), taken away.</summary>
        public double UnsuitablePercent { get; init; }
        /// <summary>Side slopes of a platform, horizontal per 1 vertical. 1:2 in fill, 1:1.5 in cut without a soils report.</summary>
        public double FillSlope { get; init; } = 2;
        public double CutSlope { get; init; } = 1.5;
        /// <summary>Lower a finished-grade design surface by the pavement structure under road surfaces.</summary>
        public bool SubtractPavement { get; init; } = true;
        /// <summary>Platform level typed by the engineer; null = the level that balances cut and fill.</summary>
        public double? PlatformLevel { get; init; }
        /// <summary>Use a level platform even when a design surface exists.</summary>
        public bool ForcePlatform { get; init; }
        /// <summary>Chosen surfaces by name; null = automatic.</summary>
        public string? ExistingName { get; init; }
        public string? DesignName { get; init; }

        public bool FactorsAreDefaults => StripCm == 20 && Shrink == 0.90 && RockPercent == 0 && UnsuitablePercent == 0;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings() : new Settings(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new Settings(); }
    }

    public static void Save(string path, Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
    }

    // ───────────── the work area as cells ─────────────

    /// <summary>
    /// The sampled work area: a square grid over it with the ground (after stripping) and the design (subgrade) level at
    /// every cell centre. Built once; volumes for any platform level are then a pass over the arrays.
    /// </summary>
    public sealed class Grid
    {
        public double X0 { get; init; } public double Y0 { get; init; } public double Cell { get; init; }
        public int Nx { get; init; } public int Ny { get; init; }
        /// <summary>Ground at the cell centre (NaN outside the work area or the ground data).</summary>
        public required float[] Ground { get; init; }
        /// <summary>Design at the cell centre already lowered to the subgrade (NaN where there is no design; all NaN for a platform).</summary>
        public required float[] Design { get; init; }
        /// <summary>Pavement depth (m) under the cell (0 off the road).</summary>
        public required float[] Pavement { get; init; }
        public required bool[] Inside { get; init; }
        /// <summary>Points along the work area's edge (for platform side slopes), one per <see cref="Cell"/> metres.</summary>
        public IReadOnlyList<(double X, double Y, double Ground)> Edge { get; init; } = Array.Empty<(double, double, double)>();
        public int InsideCells { get; init; }
        public int GroundCells { get; init; }
        public double CellArea => Cell * Cell;
        public double X(int i) => X0 + (i + 0.5) * Cell;
        public double Y(int j) => Y0 + (j + 0.5) * Cell;
    }

    /// <summary>Cell size for an area: about 400,000 cells at most, never finer than 0.5 m, rounded to a plain number.</summary>
    public static double CellSizeFor(double areaSquareMetres)
    {
        var raw = Math.Sqrt(Math.Max(areaSquareMetres, 1) / 400_000d);
        foreach (var nice in new[] { 0.5, 1, 2, 2.5, 5, 10, 20, 25, 50, 100 }) if (raw <= nice) return nice;
        return 100;
    }

    /// <summary>
    /// Samples the work area. <paramref name="rings"/> is the area (outer rings and holes, even-odd); null = wherever the
    /// design surface is. <paramref name="pavement"/> lists road outlines with their structure depth in metres.
    /// </summary>
    public static Grid Sample(TinModel ground, TinModel? design, IReadOnlyList<IReadOnlyList<(double X, double Y)>>? rings,
        IReadOnlyList<(IReadOnlyList<IReadOnlyList<(double X, double Y)>> Rings, double DepthM)>? pavement, Settings settings,
        double? cell = null, CancellationToken cancel = default)
    {
        double minX, minY, maxX, maxY;
        if (rings is { Count: > 0 })
        {
            var all = rings.SelectMany(r => r).ToArray();
            minX = all.Min(p => p.X); minY = all.Min(p => p.Y); maxX = all.Max(p => p.X); maxY = all.Max(p => p.Y);
        }
        else if (design != null && design.Triangles > 0) { minX = design.MinX; minY = design.MinY; maxX = design.MaxX; maxY = design.MaxY; }
        else throw new InvalidOperationException("אין תחום עבודה: סמנו אזור, או בחרו משטח תכנון.");
        var area = rings is { Count: > 0 } ? Math.Abs(rings.Sum(SignedArea)) : (maxX - minX) * (maxY - minY);
        var cs = cell ?? CellSizeFor(area);
        var nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cs)); var ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cs));
        if ((long)nx * ny > 6_000_000) throw new InvalidOperationException("תחום העבודה גדול מדי לניתוח — סמנו אזור קטן יותר.");
        var inside = rings is { Count: > 0 } ? Rasterize(rings, minX, minY, cs, nx, ny) : Enumerable.Repeat(true, nx * ny).ToArray();
        var pave = new float[nx * ny];
        if (pavement != null && design != null && settings.SubtractPavement)
            foreach (var (pr, depth) in pavement.Where(p => p.DepthM > 0))
            {
                var m = Rasterize(pr, minX, minY, cs, nx, ny);
                for (var k = 0; k < m.Length; k++) if (m[k]) pave[k] = Math.Max(pave[k], (float)depth);
            }
        var g = new float[nx * ny]; var d = new float[nx * ny];
        var strip = settings.StripCm / 100d;
        int insideCells = 0, groundCells = 0;
        for (var j = 0; j < ny; j++)
        {
            if ((j & 31) == 0) cancel.ThrowIfCancellationRequested();
            var y = minY + (j + 0.5) * cs;
            for (var i = 0; i < nx; i++)
            {
                var k = j * nx + i;
                g[k] = d[k] = float.NaN;
                if (!inside[k]) continue;
                var x = minX + (i + 0.5) * cs;
                double dz = double.NaN;
                if (design != null && !design.TryElevation(x, y, out dz)) { inside[k] = false; continue; }   // no design here: not work area
                insideCells++;
                if (!ground.TryElevation(x, y, out var gz)) continue;
                groundCells++;
                g[k] = (float)(gz - strip);
                if (design != null) d[k] = (float)(dz - pave[k]);
            }
        }
        var edge = new List<(double, double, double)>();
        if (rings is { Count: > 0 })
            foreach (var ring in rings)
                for (var e = 0; e < ring.Count; e++)
                {
                    var a = ring[e]; var b = ring[(e + 1) % ring.Count];
                    var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    var n = Math.Max(1, (int)Math.Round(len / cs));
                    for (var s = 0; s < n; s++)
                    {
                        var t = (s + 0.5) / n;
                        var x = a.X + (b.X - a.X) * t; var y = a.Y + (b.Y - a.Y) * t;
                        if (ground.TryElevation(x, y, out var gz)) edge.Add((x, y, gz - strip));
                    }
                }
        return new Grid { X0 = minX, Y0 = minY, Cell = cs, Nx = nx, Ny = ny, Ground = g, Design = d, Pavement = pave, Inside = inside,
            Edge = edge, InsideCells = insideCells, GroundCells = groundCells };
    }

    /// <summary>Even-odd scan-line fill of rings onto the grid's cell centres.</summary>
    public static bool[] Rasterize(IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings, double x0, double y0, double cs, int nx, int ny)
    {
        var mask = new bool[nx * ny];
        var xs = new List<double>();
        for (var j = 0; j < ny; j++)
        {
            var y = y0 + (j + 0.5) * cs;
            xs.Clear();
            foreach (var ring in rings)
                for (var e = 0; e < ring.Count; e++)
                {
                    var a = ring[e]; var b = ring[(e + 1) % ring.Count];
                    if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y)) xs.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (var p = 0; p + 1 < xs.Count; p += 2)
            {
                var i0 = Math.Max(0, (int)Math.Ceiling((xs[p] - x0) / cs - 0.5));
                var i1 = Math.Min(nx - 1, (int)Math.Floor((xs[p + 1] - x0) / cs - 0.5));
                for (var i = i0; i <= i1; i++) mask[j * nx + i] = !mask[j * nx + i];
            }
        }
        return mask;
    }

    public static double SignedArea(IReadOnlyList<(double X, double Y)> ring)
    {
        double s = 0;
        for (var i = 0; i < ring.Count; i++) { var a = ring[i]; var b = ring[(i + 1) % ring.Count]; s += a.X * b.Y - b.X * a.Y; }
        return s / 2;
    }

    // ───────────── volumes ─────────────

    public sealed record Volumes(double Cut, double Fill, double CutSlopes, double FillSlopes, double WorkArea, double SlopeArea,
        double CutArea, double FillArea, double PavedArea, double CoarseCut, double CoarseFill)
    {
        public double TotalCut => Cut + CutSlopes;
        public double TotalFill => Fill + FillSlopes;
        /// <summary>How far a grid twice as coarse lands from this one: an honest bound on the sampling error.</summary>
        public double SamplingSpread => Math.Max(Rel(Cut, CoarseCut), Rel(Fill, CoarseFill));
        private static double Rel(double fine, double coarse) => fine + coarse < 1 ? 0 : Math.Abs(fine - coarse) / Math.Max(fine, 1);
    }

    /// <summary>Cut and fill (m³ in place) between the stripped ground and the design or a platform at <paramref name="platform"/>.</summary>
    public static Volumes Measure(Grid grid, Settings settings, double? platform)
    {
        double cut = 0, fill = 0, cutA = 0, fillA = 0, work = 0, paved = 0, cCut = 0, cFill = 0;
        var a = grid.CellArea;
        for (var j = 0; j < grid.Ny; j++)
            for (var i = 0; i < grid.Nx; i++)
            {
                var k = j * grid.Nx + i;
                var g = grid.Ground[k];
                if (float.IsNaN(g)) continue;
                double target = platform ?? grid.Design[k];
                if (double.IsNaN(target)) continue;
                work += a;
                if (grid.Pavement[k] > 0) paved += a;
                var dz = target - g;
                if (dz < 0) { cut -= dz * a; cutA += a; } else { fill += dz * a; if (dz > 0) fillA += a; }
            }
        // The same work on a grid twice as coarse (2×2 blocks averaged): how much cut and fill cancel inside a cell.
        for (var j = 0; j + 1 < grid.Ny; j += 2)
            for (var i = 0; i + 1 < grid.Nx; i += 2)
            {
                double sum = 0; var n = 0;
                for (var dj = 0; dj < 2; dj++)
                    for (var di = 0; di < 2; di++)
                    {
                        var k = (j + dj) * grid.Nx + i + di;
                        var g = grid.Ground[k];
                        double target = platform ?? grid.Design[k];
                        if (float.IsNaN(g) || double.IsNaN(target)) continue;
                        sum += target - g; n++;
                    }
                if (n == 0) continue;
                var dz = sum / n;
                if (dz < 0) cCut -= dz * a * n; else cFill += dz * a * n;
            }
        double cutSlope = 0, fillSlope = 0, slopeArea = 0;
        if (platform is { } level)
            foreach (var (_, _, ground) in grid.Edge)
            {
                // A platform's side slope as a wedge from its edge down (or up) to the ground: ½·m·h² per metre of edge.
                var h = level - ground;
                var m = h > 0 ? settings.FillSlope : settings.CutSlope;
                var v = 0.5 * m * h * h * grid.Cell;
                if (h > 0) fillSlope += v; else cutSlope += v;
                slopeArea += m * Math.Abs(h) * grid.Cell;
            }
        return new Volumes(cut, fill, cutSlope, fillSlope, work, slopeArea, cutA, fillA, paved, cCut, cFill);
    }

    /// <summary>
    /// The platform level at which the cut, once compacted, makes exactly the fill (side slopes included): no import and no
    /// surplus beyond the rock and unsuitable share. Bisection between the lowest and highest ground in the area.
    /// </summary>
    public static double BalancedLevel(Grid grid, Settings settings)
    {
        var ground = grid.Ground.Where(v => !float.IsNaN(v)).ToArray();
        if (ground.Length == 0) throw new InvalidOperationException("אין נתוני קרקע קיימת בתוך האזור.");
        double lo = ground.Min(), hi = ground.Max();
        var usable = (1 - settings.UnsuitablePercent / 100d) * settings.Shrink;
        double Excess(double level) { var v = Measure(grid, settings, level); return v.TotalCut * usable - v.TotalFill; }
        for (var it = 0; it < 60 && hi - lo > 0.001; it++)
        {
            var mid = (lo + hi) / 2;
            if (Excess(mid) > 0) lo = mid; else hi = mid;
        }
        return Math.Round((lo + hi) / 2, 2);
    }

    // ───────────── balance and bill rows ─────────────

    public sealed record Balance(double CutInPlace, double Rock, double CutToFill, double CutToWaste, double FillCompacted, double ImportFill,
        double StripArea, double StripVolume, double SubgradeArea, double HaulLoose);

    public static Balance Balanced(Volumes v, Settings s)
    {
        var cut = v.TotalCut; var fill = v.TotalFill;
        var stripArea = v.WorkArea + v.SlopeArea;
        // The ground was sampled after stripping, so the cut already lost the topsoil and the fill already makes it up
        // (the specifications: "באזורי חפירה יופחת החישוף מכמות החפירה ובאזורי מילוי יתווסף").
        var usable = cut * (1 - s.UnsuitablePercent / 100d);
        var needInPlace = s.Shrink > 0 ? fill / s.Shrink : fill;
        var toFill = Math.Min(usable, needInPlace);
        var toWaste = cut - toFill;
        var import = Math.Max(0, fill - usable * s.Shrink);
        // A balanced platform is found to the centimetre: what is left either way is rounding, not a pay item.
        var noise = 0.002 * Math.Max(cut, fill);
        if (import < noise) import = 0;
        if (toWaste - cut * s.UnsuitablePercent / 100d < noise) { toFill = Math.Max(0, cut - cut * s.UnsuitablePercent / 100d); toWaste = cut - toFill; }
        return new Balance(cut, cut * s.RockPercent / 100d, toFill, toWaste, fill, import, stripArea, stripArea * s.StripCm / 100d,
            v.WorkArea, toWaste * s.Swell);
    }

    /// <summary>One bill row: what, how much, its NTI item, and how it was worked out.</summary>
    public sealed record Row(string Name, double Amount, string Unit, string Code, string SearchHint, string Formula);

    /// <summary>NTI unified price list, chapter 51.01 (preparation) and 51.02 (earthworks).</summary>
    public static class Items
    {
        public const string StripStockpile = "51.01.1060", StripRemove = "51.01.1061", CutToFill = "51.02.0010", CutToWaste = "51.02.0020",
            Rock = "51.02.0310", Compaction = "51.02.0060", ImportFill = "51.02.0080", Subgrade = "51.02.0040";
    }

    public static IReadOnlyList<Row> Rows(Balance b, Settings s)
    {
        var rows = new List<Row>();
        var soil = 1 - s.RockPercent / 100d;
        if (b.StripArea >= 1)
            rows.Add(s.StripToStockpile
                ? new($"חישוף {s.StripCm:0.#} ס״מ ועירום לשימוש חוזר", b.StripArea, "מ״ר", Items.StripStockpile, "^חישוף השטח ועירום זמני", $"שטח העבודה {b.SubgradeArea:N0} מ״ר + מדרונות {b.StripArea - b.SubgradeArea:N0} מ״ר")
                : new($"חישוף {s.StripCm:0.#} ס״מ וסילוק", b.StripArea, "מ״ר", Items.StripRemove, "^חישוף השטח וסילוקו", $"שטח העבודה {b.SubgradeArea:N0} מ״ר + מדרונות {b.StripArea - b.SubgradeArea:N0} מ״ר"));
        if (b.CutToFill * soil >= 1)
            rows.Add(new("חפירה והובלה לאזורי המילוי", b.CutToFill * soil, "מ״ק", Items.CutToFill, "^חפירה לאורך הדרך והובלת החומר החפור לאזורי מילוי",
                $"מילוי {b.FillCompacted:N0} מ״ק מהודק ÷ מקדם התכווצות {s.Shrink:0.00}, עד כמות החפירה הראויה"));
        if (b.CutToWaste * soil >= 1)
            rows.Add(new("חפירה וסילוק עודפים", b.CutToWaste * soil, "מ״ק", Items.CutToWaste, "^חפירה לאורך הדרך וסילוק העפר",
                $"חפירה {b.CutInPlace:N0} מ״ק פחות {b.CutToFill:N0} מ״ק למילוי · כ־{b.HaulLoose:N0} מ״ק תפוח להובלה (×{s.Swell:0.00})"));
        if (b.Rock >= 1)
            rows.Add(new("חציבה", b.Rock, "מ״ק", Items.Rock, "^חציבה", $"{s.RockPercent:0.#}% מהחפירה {b.CutInPlace:N0} מ״ק"));
        if (b.ImportFill >= 1)
            rows.Add(new("מילוי מובא", b.ImportFill, "מ״ק", Items.ImportFill, "^מילוי מובא", $"מילוי {b.FillCompacted:N0} מ״ק פחות מה שהחפירה מספקת מהודק"));
        if (b.FillCompacted >= 1)
            rows.Add(new("הידוק מבוקר של המילוי", b.FillCompacted, "מ״ק", Items.Compaction, "^הידוק מבוקר", "כל המילוי, בשכבות עד 20 ס״מ"));
        if (b.SubgradeArea >= 1)
            rows.Add(new("הידוק קרקע יסוד (שתית)", b.SubgradeArea, "מ״ר", Items.Subgrade, "^הידוק קרקע יסוד מקורית", "שטח העבודה"));
        return rows;
    }

    public static IReadOnlyList<InfrastructureGroup> DerivedGroups(IReadOnlyList<Row> rows, string basis, bool defaults) => rows
        .Select(r => new InfrastructureGroup
        {
            Source = DerivedSource,
            Layer = r.Name + (defaults ? " · " + DefaultMark : ""),
            Topic = Topic, Count = 1, LengthMetres = 0,
            AreaSquareMetres = r.Unit == "מ״ר" ? r.Amount : 0,
            VolumeCubicMetres = r.Unit == "מ״ק" ? r.Amount : 0,
            GroupPart = DerivedPart, MeasurementUnit = r.Unit,
            Evidence = $"{r.Formula} — {basis}" + (defaults ? " · " + DefaultMark + " — מקדמי עפר טיפוסיים; יש לאמת מול דו״ח קרקע" : ""),
            Element = r.SearchHint,
            Surface = RoadSurfaceKind.None,
        })
        .ToArray();

    // ───────────── topography ─────────────

    public sealed record SlopeClass(string Label, double Share);

    public sealed record Topography(double Area, double MinZ, double MaxZ, double MeanZ, double MeanSlopePercent,
        IReadOnlyList<SlopeClass> Slopes, string Terrain, string TerrainWhy, string Downhill, double SteepFillShare)
    {
        public double Relief => MaxZ - MinZ;
    }

    /// <summary>
    /// The ground of the work area: levels, slopes by class, the way water runs off, and the terrain class of the ministry's
    /// design criteria (Table 2.1, after the 1994 PWD guidelines): flat under 30 m of height difference per km (natural
    /// slopes up to 1:10), hilly 30–70 m, mountainous above 70 m.
    /// </summary>
    public static Topography Analyse(Grid grid, Volumes? volumes = null, double? platform = null)
    {
        var a = grid.CellArea;
        double sum = 0, area = 0, min = double.MaxValue, max = double.MinValue, slopeSum = 0, slopeArea = 0, gx = 0, gy = 0, steepFill = 0, fillArea = 0;
        var bins = new double[5];   // 0–2, 2–5, 5–10, 10–15, >15 %
        for (var j = 0; j < grid.Ny; j++)
            for (var i = 0; i < grid.Nx; i++)
            {
                var k = j * grid.Nx + i;
                var z = grid.Ground[k];
                if (float.IsNaN(z)) continue;
                sum += z * a; area += a; min = Math.Min(min, z); max = Math.Max(max, z);
                var dx = Diff(grid, i, j, 1, 0); var dy = Diff(grid, i, j, 0, 1);
                if (double.IsNaN(dx) || double.IsNaN(dy)) continue;
                var s = Math.Sqrt(dx * dx + dy * dy) * 100;
                slopeSum += s * a; slopeArea += a; gx += dx * a; gy += dy * a;
                bins[s < 2 ? 0 : s < 5 ? 1 : s < 10 ? 2 : s < 15 ? 3 : 4] += a;
                var target = platform ?? grid.Design[k];
                if (!double.IsNaN(target) && target > z) { fillArea += a; if (s > 15) steepFill += a; }
            }
        if (area <= 0) throw new InvalidOperationException("אין נתוני קרקע קיימת בתוך האזור.");
        var mean = slopeArea > 0 ? slopeSum / slopeArea : 0;
        var labels = new[] { "0–2%", "2–5%", "5–10%", "10–15%", ">15%" };
        var classes = labels.Select((l, n) => new SlopeClass(l, slopeArea > 0 ? bins[n] / slopeArea : 0)).ToArray();
        var perKm = mean * 10;   // metres of height per km of ground at the mean slope
        var terrain = perKm < 30 ? "מישורית" : perKm <= 70 ? "גבעית" : "הררית";
        var why = $"שיפוע קרקע ממוצע {mean:0.0}% ≈ {perKm:0} מ׳ הפרש גובה לק״מ ({(perKm < 30 ? "פחות מ־30" : perKm <= 70 ? "30–70" : "יותר מ־70")} מ׳ לק״מ, טבלה 2.1 בתבחיני התכן)";
        return new Topography(area, min, max, sum / area, mean, classes, terrain, why, Downhill(-gx, -gy), fillArea > 0 ? steepFill / fillArea : 0);
    }

    private static double Diff(Grid g, int i, int j, int di, int dj)
    {
        double At(int ii, int jj) => ii < 0 || jj < 0 || ii >= g.Nx || jj >= g.Ny ? double.NaN : g.Ground[jj * g.Nx + ii];
        var p = At(i + di, j + dj); var m = At(i - di, j - dj);
        if (!double.IsNaN(p) && !double.IsNaN(m)) return (p - m) / (2 * g.Cell);
        var c = At(i, j);
        if (!double.IsNaN(p)) return (p - c) / g.Cell;
        if (!double.IsNaN(m)) return (c - m) / g.Cell;
        return double.NaN;
    }

    /// <summary>Compass direction (grid north) of a plan vector, in Hebrew; "—" when the ground is flat.</summary>
    public static string Downhill(double x, double y)
    {
        if (Math.Sqrt(x * x + y * y) < 1e-9) return "—";
        var deg = (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360;   // 0 = north, clockwise
        var names = new[] { "צפון", "צפון־מזרח", "מזרח", "דרום־מזרח", "דרום", "דרום־מערב", "מערב", "צפון־מערב" };
        return names[(int)Math.Round(deg / 45) % 8];
    }

    public static string Format(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
