using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>A text found near a record: a candidate only, never a verified fact about the measured object.</summary>
public sealed record NearbyTextHit(string Text, double DistanceMetres, string Handle, string Source, string Kind);

/// <summary>The result of one query. <see cref="Failure"/> is a status reason when the query could not be answered.</summary>
public sealed record NearbyTextResult(IReadOnlyList<NearbyTextHit> Hits, int Total, string? Failure)
{
    /// <summary>
    /// <c>ev_nearby_text</c>: up to <see cref="NearbyTextIndex.MaxHits"/> hits as
    /// <c>[{"text","distance_m","handle","source","kind"}]</c>; <c>truncated:&lt;total&gt;</c> when more were found,
    /// <c>absent</c> when none, <c>unavailable:&lt;reason&gt;</c> when the index or the geometry cannot answer.
    /// </summary>
    public EvidenceValue ToEvidence()
    {
        if (Failure != null) return EvidenceValue.Unavailable(Failure);
        if (Total == 0 || Hits.Count == 0) return EvidenceValue.Absent;
        var json = EvidenceJson.Build(writer =>
        {
            writer.WriteStartArray();
            foreach (var hit in Hits)
            {
                writer.WriteStartObject();
                writer.WriteString("text", hit.Text);
                EvidenceJson.Number(writer, "distance_m", hit.DistanceMetres);
                writer.WriteString("handle", hit.Handle);
                writer.WriteString("source", hit.Source);
                writer.WriteString("kind", hit.Kind);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        return Total > Hits.Count ? EvidenceValue.Truncated(json, Total) : EvidenceValue.Read(json);
    }
}

/// <summary>
/// One spatial index of drawing text per scan, in host metres. A uniform grid (cell = radius) is built once while
/// the traversal runs; every record is then queried with its full-resolution host shape (<see cref="EvidenceShape"/>:
/// every vertex, arcs split to a few millimetres, every hatch ring). A text within the radius of any path is a
/// hit; for a closed shape a text in its filled interior (holes excluded, per fill style) is at distance 0. A shape
/// that is only a stand-in (extents, unread loops) is never queried: its failure is the answer. Hits are ordered
/// by distance rounded to 1 mm, then source, handle and text, and at most <see cref="MaxHits"/> are kept.
/// The index is bounded: once <see cref="MaxTexts"/> is exceeded every query answers
/// <c>unavailable:text-index-truncated</c> rather than a partial "absent". Our own annotation layers
/// (MAHOD_, MAHOD-, MHD-, MCD-, MCDV-) and text inside an INSERT on such a layer are never indexed, so the tool
/// cannot read back its own labels.
/// A drawing text that could not be read is never proof that no text is there (<see cref="ReportUnread"/>): where
/// its position is known, only queries that reach it are affected — no readable hit answers
/// <c>unavailable:text-capture-incomplete</c>, readable hits answer <c>truncated</c> (more text is there than
/// shown); where its position is unknown, every query answers <c>unavailable:text-capture-incomplete</c>.
/// </summary>
public sealed class NearbyTextIndex
{
    public const double DefaultRadiusMetres = 5.0;
    public const int DefaultMaxTexts = 200_000;
    public const int MaxHits = 5;
    public const int MaxTextChars = 80;
    public const int MaxHandleChars = 160;
    /// <summary>Distinct texts examined by one interior (inside-the-shape) pass.</summary>
    public const int MaxCandidatesPerQuery = 200_000;
    /// <summary>Distance checks plus ring-point steps of inside tests for one query; beyond it the query fails closed.</summary>
    public const long MaxWorkPerQuery = 8_000_000;
    public const int MaxSegmentSteps = 100_000;

    // "MAHOD-" is the hyphenated form our section-view tool writes (MAHOD-SV, MAHOD-SV-TEXT, MAHOD-SV-CORR...).
    private static readonly string[] OwnLayerPrefixes = { "MAHOD_", "MAHOD-", "MHD-", "MCD-", "MCDV-" };

    private readonly record struct Entry(string Text, double X, double Y, string Handle, string Source, string Kind, bool Unread = false);

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<(long X, long Y), List<int>> _cells = new();

    public NearbyTextIndex(double radiusMetres = DefaultRadiusMetres, int maxTexts = DefaultMaxTexts)
    {
        if (!double.IsFinite(radiusMetres) || radiusMetres <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMetres));
        if (maxTexts <= 0) throw new ArgumentOutOfRangeException(nameof(maxTexts));
        RadiusMetres = radiusMetres;
        MaxTexts = maxTexts;
    }

    public double RadiusMetres { get; }
    public int MaxTexts { get; }
    /// <summary>Readable texts in the index.</summary>
    public int Count => _entries.Count - UnreadLocated;
    /// <summary>Texts that could not be read but whose host position is known (they mark only nearby queries).</summary>
    public int UnreadLocated { get; private set; }
    /// <summary>Texts that could not be read at an unknown position (they make every query incomplete).</summary>
    public int UnreadUnlocated { get; private set; }
    public bool IsTruncated { get; private set; }
    public int DroppedByCap { get; private set; }
    public int ExcludedOwnLayers { get; private set; }
    public int RejectedEmptyOrInvalid { get; private set; }

    /// <summary>True for the layers our own tools draw on (compared on the XREF leaf name, like the scan's exclusion).</summary>
    public static bool IsOwnAnnotationLayer(string? layer)
    {
        var leaf = SectionProjectionLogic.LayerLeaf(layer).ToUpperInvariant();
        return OwnLayerPrefixes.Any(prefix => leaf.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds one text anchored at host (<paramref name="x"/>, <paramref name="y"/>) metres. Returns false when the
    /// text is excluded, empty, has no identity or would exceed the cap (which truncates the whole index).
    /// <paramref name="insideOwnInsert"/>: the text is a member (or attribute) of an INSERT that is, or lies inside,
    /// an INSERT on one of our own annotation layers; it is ours whatever its own layer (often "0") says.
    /// </summary>
    public bool Add(string? text, double x, double y, string? handle, string? source, string? layer = null, string kind = "text",
        bool insideOwnInsert = false)
    {
        if (insideOwnInsert || IsOwnAnnotationLayer(layer))
        {
            ExcludedOwnLayers++;
            return false;
        }
        var clean = EvidenceJson.Text(text, MaxTextChars);
        var cleanHandle = EvidenceJson.Text(handle, MaxHandleChars);
        if (clean == null || cleanHandle == null || !double.IsFinite(x) || !double.IsFinite(y))
        {
            RejectedEmptyOrInvalid++;
            return false;
        }
        if (Count >= MaxTexts)
        {
            IsTruncated = true;
            DroppedByCap++;
            return false;
        }
        var index = _entries.Count;
        _entries.Add(new Entry(clean, x, y, cleanHandle,
            EvidenceReader.Clean(source) ?? "host", EvidenceJson.Text(kind, 16) ?? "text"));
        var cell = Cell(x, y);
        if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>(2);
        list.Add(index);
        return true;
    }

    /// <summary>
    /// Records a drawing text that could not be read. With a finite host position (metres) it is kept at that
    /// position and marks only the queries that reach it; without one, every query becomes incomplete. The
    /// <paramref name="handle"/>, when known, lets a record's own unread attribute be skipped like a readable one.
    /// </summary>
    public void ReportUnread(double? x, double? y, string? handle = null)
    {
        if (x is not { } px || y is not { } py || !double.IsFinite(px) || !double.IsFinite(py))
        {
            UnreadUnlocated++;
            return;
        }
        var index = _entries.Count;
        _entries.Add(new Entry(string.Empty, px, py, EvidenceJson.Text(handle, MaxHandleChars) ?? string.Empty,
            "host", "unread", Unread: true));
        UnreadLocated++;
        var cell = Cell(px, py);
        if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>(2);
        list.Add(index);
    }

    /// <summary>The evidence value for one polyline through <paramref name="points"/> (see the matching Find).</summary>
    public EvidenceValue Query(IReadOnlyList<(double X, double Y)>? points, bool closed, string? ownHandlePath = null) =>
        Find(points, closed, ownHandlePath).ToEvidence();

    /// <summary>The evidence value for one record's full-resolution host shape (see the matching Find).</summary>
    public EvidenceValue Query(EvidenceShape? shape, string? ownHandlePath = null) => Find(shape, ownHandlePath).ToEvidence();

    /// <summary>
    /// Texts within the radius of the polyline through <paramref name="points"/> (host metres; one point is a
    /// point query). With <paramref name="closed"/> and three or more points the polygon interior counts as
    /// distance 0. Texts under <paramref name="ownHandlePath"/> (the record's own attributes) are skipped.
    /// </summary>
    public NearbyTextResult Find(IReadOnlyList<(double X, double Y)>? points, bool closed, string? ownHandlePath = null)
    {
        if (IsTruncated) return Failed("text-index-truncated");
        if (points == null || points.Count == 0) return Failed("no-geometry-sample");
        var shape = closed && points.Count >= 3
            ? EvidenceShape.Rings(new[] { points })
            : EvidenceShape.Open(points);
        return Find(shape, ownHandlePath);
    }

    /// <summary>
    /// Texts within the radius of any path of <paramref name="shape"/> (host metres; a one-point path is a point
    /// query). For a closed shape a text in its filled interior (per <see cref="EvidenceShape.Fill"/>: holes are
    /// outside, their own boundary still counts for distance) is at distance 0. A shape with a
    /// <see cref="EvidenceShape.Failure"/> (a stand-in outline, an unread loop) is never queried: the failure is the
    /// answer. Texts under <paramref name="ownHandlePath"/> (the record's own attributes) are skipped.
    /// </summary>
    public NearbyTextResult Find(EvidenceShape? shape, string? ownHandlePath = null)
    {
        if (IsTruncated) return Failed("text-index-truncated");
        if (UnreadUnlocated > 0) return Failed(CaptureIncomplete);
        if (shape == null) return Failed("no-geometry-sample");
        if (shape.Failure != null) return Failed(shape.Failure);
        if (shape.Paths.Count == 0) return Failed("no-geometry-sample");

        var best = new Dictionary<int, double>();
        long work = 0;
        var cells = new HashSet<(long X, long Y)>();
        foreach (var path in shape.Paths)
        {
            var segments = path.Count == 1 ? 1 : shape.Closed ? path.Count : path.Count - 1;
            for (var s = 0; s < segments; s++)
            {
                var a = path[s];
                var b = path.Count == 1 ? a : path[(s + 1) % path.Count];
                // A sparse (including empty) text index must not walk thousands of empty
                // grid cells per segment. Keep the same geometry guard and exact distance
                // predicate; unread entries and own attributes still use the common path
                // below for completeness and ordering. This is not a geometry shortcut.
                if (_entries.Count <= 64)
                {
                    if (SegmentSteps(a, b) < 0) return Failed("geometry-sample-too-large");
                    work += _entries.Count;
                    if (work > MaxWorkPerQuery) return Failed("text-query-budget");
                    for (var index = 0; index < _entries.Count; index++)
                    {
                        var entry = _entries[index];
                        var distance = SegmentDistance(entry.X, entry.Y, a, b);
                        if (distance <= RadiusMetres && (!best.TryGetValue(index, out var previous) || distance < previous))
                            best[index] = distance;
                    }
                    continue;
                }
                cells.Clear();
                if (!CollectSegmentCells(a, b, cells)) return Failed("geometry-sample-too-large");
                foreach (var cell in cells)
                {
                    if (!_cells.TryGetValue(cell, out var list)) continue;
                    work += list.Count;
                    if (work > MaxWorkPerQuery) return Failed("text-query-budget");
                    foreach (var index in list)
                    {
                        var entry = _entries[index];
                        var distance = SegmentDistance(entry.X, entry.Y, a, b);
                        if (distance <= RadiusMetres && (!best.TryGetValue(index, out var previous) || distance < previous))
                            best[index] = distance;
                    }
                }
            }
        }

        if (shape.Closed)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var path in shape.Paths)
                foreach (var (px, py) in path)
                {
                    minX = Math.Min(minX, px);
                    minY = Math.Min(minY, py);
                    maxX = Math.Max(maxX, px);
                    maxY = Math.Max(maxY, py);
                }
            var (fromX, fromY) = Cell(minX, minY);
            var (toX, toY) = Cell(maxX, maxY);
            var boxCells = (double)(toX - fromX + 1) * (toY - fromY + 1);
            IEnumerable<List<int>> lists = boxCells <= _cells.Count
                ? BoxCells(fromX, fromY, toX, toY)
                : _cells.Where(pair => pair.Key.X >= fromX && pair.Key.X <= toX && pair.Key.Y >= fromY && pair.Key.Y <= toY)
                    .Select(pair => pair.Value);
            var examined = 0;
            EvidenceShape.InteriorIndex? interior = null;
            foreach (var list in lists)
                foreach (var index in list)
                {
                    if (++examined > MaxCandidatesPerQuery) return Failed("text-query-budget");
                    var entry = _entries[index];
                    if (entry.X < minX || entry.X > maxX || entry.Y < minY || entry.Y > maxY) continue;
                    // Built once per query, only when a text lies in the box; never kept with the shape.
                    interior ??= shape.Interior();
                    var inside = interior.Contains(entry.X, entry.Y, ref work);
                    if (work > MaxWorkPerQuery) return Failed("text-query-budget");
                    if (inside) best[index] = 0d;
                }
        }

        var prefix = string.IsNullOrWhiteSpace(ownHandlePath) ? null : ownHandlePath.Trim() + "/";
        var reached = best
            .Select(pair => (Entry: _entries[pair.Key], Distance: pair.Value))
            .Where(item => prefix == null || !item.Entry.Handle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // An unread text within reach: what was read is still shown, but it is not all there is.
        var unreadNear = reached.Count(item => item.Entry.Unread);
        var hits = reached
            .Where(item => !item.Entry.Unread)
            .OrderBy(item => (long)Math.Round(item.Distance * 1000, MidpointRounding.AwayFromZero))
            .ThenBy(item => item.Entry.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.Handle, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.Text, StringComparer.Ordinal)
            .Select(item => new NearbyTextHit(item.Entry.Text, item.Distance, item.Entry.Handle, item.Entry.Source, item.Entry.Kind))
            .ToList();
        if (unreadNear > 0 && hits.Count == 0) return Failed(CaptureIncomplete);
        return new NearbyTextResult(hits.Take(MaxHits).ToList(), hits.Count + unreadNear, null);
    }

    /// <summary>The status reason when an unread drawing text may be within reach.</summary>
    public const string CaptureIncomplete = "text-capture-incomplete";

    private IEnumerable<List<int>> BoxCells(long fromX, long fromY, long toX, long toY)
    {
        for (var x = fromX; x <= toX; x++)
            for (var y = fromY; y <= toY; y++)
                if (_cells.TryGetValue((x, y), out var list))
                    yield return list;
    }

    /// <summary>
    /// Cells that can hold a text within the radius of segment a–b: the segment is walked in steps of at most one
    /// cell, and every step covers the cells within two of its own (any segment point is within half a step of
    /// a walked point, so a text within the radius is within 1.5 cells of it). A segment longer than
    /// <see cref="MaxSegmentSteps"/> cells is refused rather than walked sparsely.
    /// </summary>
    private bool CollectSegmentCells((double X, double Y) a, (double X, double Y) b, HashSet<(long X, long Y)> cells)
    {
        var steps = SegmentSteps(a, b);
        if (steps < 0) return false;
        for (var step = 0; step <= steps; step++)
        {
            var t = (double)step / steps;
            var (cx, cy) = Cell(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            for (var dx = -2; dx <= 2; dx++)
                for (var dy = -2; dy <= 2; dy++)
                    cells.Add((cx + dx, cy + dy));
        }
        return true;
    }

    private int SegmentSteps((double X, double Y) a, (double X, double Y) b)
    {
        var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var exactSteps = Math.Ceiling(length / RadiusMetres);
        return !double.IsFinite(exactSteps) || exactSteps > MaxSegmentSteps ? -1 : Math.Max(1, (int)exactSteps);
    }

    private (long X, long Y) Cell(double x, double y) =>
        ((long)Math.Floor(x / RadiusMetres), (long)Math.Floor(y / RadiusMetres));

    internal static double SegmentDistance(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared <= 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
        var px = a.X + t * dx - x;
        var py = a.Y + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }

    /// <summary>Even-odd point-in-polygon test; the polygon closes from the last point back to the first.</summary>
    internal static bool Inside(double x, double y, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static NearbyTextResult Failed(string reason) => new(Array.Empty<NearbyTextHit>(), 0, reason);
}
