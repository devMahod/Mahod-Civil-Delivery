using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>How the interior of a closed shape is read from its rings: the three AutoCAD hatch styles.</summary>
public enum EvidenceFill
{
    /// <summary>Open paths or points: the shape has no interior.</summary>
    None,
    /// <summary>Alternating: a point inside an odd number of rings is inside (holes, islands in holes).</summary>
    Normal,
    /// <summary>Only the outermost area: a point inside exactly one ring.</summary>
    Outer,
    /// <summary>Everything inside any ring.</summary>
    Ignore,
}

/// <summary>
/// The host geometry, in metres, that a record's nearby-text query runs against. Unlike the preview sample
/// (ev_geometry_sample, at most <see cref="EvidenceJson.MaxSamplePoints"/> points) it keeps every vertex:
/// arcs are split so no chord strays more than <see cref="ChordToleranceMetres"/>
/// from the arc (<see cref="Exact"/>); splines and ellipses are sampled by parameter (<see cref="Sampled"/>).
/// A closed shape keeps all of its rings (outer loops, holes, islands) with its fill style.
/// A shape the collector could only stand in for (an extents box, a hatch loop it could not read, a region whose
/// boundary is not read) is never built: it carries a <see cref="Failure"/>, so no inside test and no distance is
/// ever answered from a rough outline. Pure and host-free.
/// </summary>
public sealed class EvidenceShape
{
    /// <summary>The section tool's chord tolerance (SectionGeometryCollector.MaxCurveSagittaM), in host metres.</summary>
    public const double ChordToleranceMetres = 0.005;
    /// <summary>Per-record cap on stored query points; a larger shape answers <c>unavailable:query-geometry-too-large</c>.</summary>
    public const int MaxPoints = 8192;
    public const int MaxPaths = 256;
    /// <summary>Samples per spline or elliptical hatch edge.</summary>
    public const int CurveSamplesPerEdge = 64;
    /// <summary>Consecutive edges of one loop must meet within this distance (host metres) to form a ring.</summary>
    public const double EdgeGapToleranceMetres = 0.05;
    public const string Exact = "exact";
    public const string Sampled = "sampled";
    public const string ApproximateGeometry = "approximate-geometry";

    private static readonly IReadOnlyList<IReadOnlyList<(double X, double Y)>> NoPaths =
        Array.Empty<IReadOnlyList<(double X, double Y)>>();

    private EvidenceShape(IReadOnlyList<IReadOnlyList<(double X, double Y)>> paths, EvidenceFill fill, string fidelity,
        string? failure)
    {
        Paths = paths;
        Fill = fill;
        Fidelity = fidelity;
        Failure = failure;
        var count = 0;
        foreach (var path in paths) count += path.Count;
        PointCount = count;
    }

    /// <summary>Host metres. For a closed shape every path is a ring; the closing point is not repeated.</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Paths { get; }
    public EvidenceFill Fill { get; }
    public bool Closed => Fill != EvidenceFill.None;
    /// <summary><see cref="Exact"/> or <see cref="Sampled"/>; empty when <see cref="Failure"/> is set.</summary>
    public string Fidelity { get; }
    /// <summary>A status reason when there is no trustworthy geometry; never answered from points.</summary>
    public string? Failure { get; }
    public int PointCount { get; }

    public static EvidenceShape Unavailable(string? reason) =>
        new(NoPaths, EvidenceFill.None, string.Empty, EvidenceJson.Reason(reason));

    /// <summary>A shape that could only have been stood in for: <c>approximate-geometry[:detail]</c>.</summary>
    public static EvidenceShape Approximate(string? detail) =>
        Unavailable(string.IsNullOrWhiteSpace(detail) ? ApproximateGeometry : ApproximateGeometry + ":" + detail);

    /// <summary>An open path (two or more points) or a single point. It has no interior.</summary>
    public static EvidenceShape Open(IReadOnlyList<(double X, double Y)>? points, string fidelity = Exact)
    {
        if (points == null) return Unavailable("no-geometry-sample");
        var failure = Check(new[] { points });
        if (failure != null) return Unavailable(failure);
        return new EvidenceShape(new[] { Copy(points, unclose: false) }, EvidenceFill.None, KnownFidelity(fidelity), null);
    }

    /// <summary>
    /// A closed shape: every ring closes from its last point back to its first, and the interior follows
    /// <paramref name="fill"/>. A single ring of fewer than three points encloses nothing and stays an open path.
    /// </summary>
    public static EvidenceShape Rings(IReadOnlyList<IReadOnlyList<(double X, double Y)>>? rings,
        EvidenceFill fill = EvidenceFill.Normal, string fidelity = Exact)
    {
        if (fill == EvidenceFill.None || !Enum.IsDefined(fill)) throw new ArgumentOutOfRangeException(nameof(fill));
        var failure = Check(rings);
        if (failure != null) return Unavailable(failure);
        var copies = new IReadOnlyList<(double X, double Y)>[rings!.Count];
        for (var i = 0; i < copies.Length; i++) copies[i] = Copy(rings[i], unclose: true);
        if (copies.Length == 1 && copies[0].Count < 3)
            return new EvidenceShape(copies, EvidenceFill.None, KnownFidelity(fidelity), null);
        return new EvidenceShape(copies, fill, KnownFidelity(fidelity), null);
    }

    /// <summary>Whether (<paramref name="x"/>, <paramref name="y"/>) lies in the filled interior (never for an open shape).</summary>
    public bool Contains(double x, double y)
    {
        if (!Closed || Failure != null) return false;
        var depth = 0;
        foreach (var ring in Paths)
            if (ring.Count >= 3 && NearbyTextIndex.Inside(x, y, ring)) depth++;
        return Filled(depth);
    }

    private bool Filled(int depth) => Fill switch
    {
        EvidenceFill.Normal => depth % 2 == 1,
        EvidenceFill.Outer => depth == 1,
        EvidenceFill.Ignore => depth > 0,
        _ => false,
    };

    /// <summary>A crossing index for many inside tests against this shape (built per query, never kept).</summary>
    internal InteriorIndex Interior() => new(this);

    /// <summary>
    /// The same answer as <see cref="Contains"/>, but each test only visits the segments filed under the horizontal
    /// band of its y: a detailed hatch whose bounding box holds thousands of texts stays cheap. When filing would
    /// exceed <see cref="MaxEntries"/> the index falls back to the full ring scan (and reports that cost).
    /// </summary>
    internal sealed class InteriorIndex
    {
        internal const int MaxRows = 4096;
        internal const long MaxEntries = 4_000_000;

        private readonly EvidenceShape _shape;
        private readonly List<(int Ring, int From)>?[]? _rows;
        private readonly double _minY;
        private readonly double _maxY;
        private readonly double _height;

        internal InteriorIndex(EvidenceShape shape)
        {
            _shape = shape;
            if (!shape.Closed || shape.Failure != null) return;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (var ring in shape.Paths)
            {
                if (ring.Count < 3) continue;
                foreach (var point in ring)
                {
                    minY = Math.Min(minY, point.Y);
                    maxY = Math.Max(maxY, point.Y);
                }
            }
            if (!(maxY >= minY)) return;
            var rows = Math.Clamp(shape.PointCount, 1, MaxRows);
            var height = (maxY - minY) / rows;
            if (!(height > 0) || !double.IsFinite(height))
            {
                rows = 1;
                height = 1;
            }
            _minY = minY;
            _maxY = maxY;
            _height = height;
            var table = new List<(int Ring, int From)>?[rows];
            long entries = 0;
            for (var r = 0; r < shape.Paths.Count; r++)
            {
                var ring = shape.Paths[r];
                if (ring.Count < 3) continue;
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    // A horizontal segment never changes the crossing parity.
                    if (a.Y == b.Y) continue;
                    var firstRow = Row(Math.Min(a.Y, b.Y), rows);
                    var lastRow = Row(Math.Max(a.Y, b.Y), rows);
                    entries += lastRow - firstRow + 1;
                    if (entries > MaxEntries) return;
                    for (var row = firstRow; row <= lastRow; row++)
                        (table[row] ??= new List<(int Ring, int From)>(4)).Add((r, i));
                }
            }
            _rows = table;
        }

        /// <summary>Inside test; <paramref name="work"/> grows by the number of segments visited.</summary>
        internal bool Contains(double x, double y, ref long work)
        {
            if (!_shape.Closed || _shape.Failure != null) return false;
            if (_rows == null)
            {
                work += _shape.PointCount;
                return _shape.Contains(x, y);
            }
            work++;
            if (y < _minY || y > _maxY) return false;
            var list = _rows[Row(y, _rows.Length)];
            if (list == null) return false;
            work += list.Count;
            Span<bool> inside = stackalloc bool[_shape.Paths.Count];
            inside.Clear();
            foreach (var (ring, start) in list)
            {
                var path = _shape.Paths[ring];
                // The even-odd crossing rule of NearbyTextIndex.Inside, per ring, with its operand order
                // (i = segment end, j = segment start), so both answer identically.
                var (xi, yi) = path[(start + 1) % path.Count];
                var (xj, yj) = path[start];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside[ring] = !inside[ring];
            }
            var depth = 0;
            foreach (var flag in inside)
                if (flag) depth++;
            return _shape.Filled(depth);
        }

        private int Row(double y, int rows) =>
            (int)Math.Clamp(Math.Floor((y - _minY) / _height), 0, rows - 1);
    }

    /// <summary>
    /// Chords for a circular arc so that none strays more than <see cref="ChordToleranceMetres"/> from it. A
    /// degenerate arc is one chord. Throws <see cref="InvalidOperationException"/> above <see cref="MaxPoints"/>.
    /// </summary>
    public static int ArcSegments(double radiusMetres, double sweepRadians)
    {
        if (!double.IsFinite(radiusMetres) || !double.IsFinite(sweepRadians))
            throw new ArgumentOutOfRangeException(nameof(radiusMetres), "Arc radius and sweep must be finite.");
        if (radiusMetres <= 0 || sweepRadians <= 0) return 1;
        return SectionProjectionLogic.ArcTessellationSegmentCount(radiusMetres, Math.Min(sweepRadians, 2 * Math.PI),
            ChordToleranceMetres, MaxPoints);
    }

    /// <summary>
    /// Joins the sampled edges of one closed loop, in their stored order, into a ring (the closing point is not
    /// repeated). An edge stored end-first is turned around. Null when consecutive edges, or the last and the
    /// first, do not meet within <paramref name="gapToleranceMetres"/>: such a loop is not a ring anyone can vouch for.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)>? JoinRing(IReadOnlyList<IReadOnlyList<(double X, double Y)>> edges,
        double gapToleranceMetres = EdgeGapToleranceMetres)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (!double.IsFinite(gapToleranceMetres) || gapToleranceMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(gapToleranceMetres));
        if (edges.Count == 0) return null;
        foreach (var edge in edges)
            if (edge == null || edge.Count == 0) return null;
        return Join(reverseFirst: false) ?? Join(reverseFirst: true);

        IReadOnlyList<(double X, double Y)>? Join(bool reverseFirst)
        {
            var ring = new List<(double X, double Y)>();
            for (var e = 0; e < edges.Count; e++)
            {
                var edge = edges[e];
                var forward = true;
                if (e == 0) forward = !reverseFirst;
                else if (Gap(ring[^1], edge[0]) > gapToleranceMetres)
                {
                    if (Gap(ring[^1], edge[edge.Count - 1]) > gapToleranceMetres) return null;
                    forward = false;
                }
                // The point shared with the previous edge is kept once.
                for (var i = e == 0 ? 0 : 1; i < edge.Count; i++)
                    ring.Add(forward ? edge[i] : edge[edge.Count - 1 - i]);
                if (ring.Count > MaxPoints + 1) return null;
            }
            if (Gap(ring[^1], ring[0]) > gapToleranceMetres) return null;
            while (ring.Count > 1 && ring[^1] == ring[0]) ring.RemoveAt(ring.Count - 1);
            return ring;
        }
    }

    private static string? Check(IReadOnlyList<IReadOnlyList<(double X, double Y)>>? paths)
    {
        if (paths == null || paths.Count == 0) return "no-geometry-sample";
        if (paths.Count > MaxPaths) return ApproximateGeometry + ":too-many-loops";
        long total = 0;
        foreach (var path in paths)
        {
            if (path == null || path.Count == 0) return "no-geometry-sample";
            total += path.Count;
        }
        if (total > MaxPoints) return "query-geometry-too-large";
        foreach (var path in paths)
            foreach (var point in path)
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return "invalid-geometry-sample";
        return null;
    }

    private static IReadOnlyList<(double X, double Y)> Copy(IReadOnlyList<(double X, double Y)> source, bool unclose)
    {
        var count = source.Count;
        if (unclose)
            while (count > 1 && source[count - 1] == source[0]) count--;
        var copy = new (double X, double Y)[count];
        for (var i = 0; i < count; i++) copy[i] = source[i];
        return copy;
    }

    // Never claims more than the caller proved: anything but "exact" is "sampled".
    private static string KnownFidelity(string? fidelity) => fidelity == Exact ? Exact : Sampled;

    private static double Gap((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
