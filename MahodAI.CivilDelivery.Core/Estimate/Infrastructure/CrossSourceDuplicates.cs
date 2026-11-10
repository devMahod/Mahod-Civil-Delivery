namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// The same element drawn twice: one attached drawing carries a copy of another's linework on the same layer. On the 130X
/// road plan (live 2026-11-10) the landscape XREF held 32 km of the traffic plan's island curbs (TR-ISLAND-CURBSTONE) line
/// for line, so every curb was priced twice. A piece of line lying on a line of the SAME layer name from ANOTHER source
/// counts once — the first source read keeps it; blocks of the same name and layer at the same point count once. Lines
/// of different layers are never merged (a curb and the edge of pavement beside it are both real).
/// </summary>
public static class CrossSourceDuplicates
{
    /// <summary>One measured object: a path in plan (one point for a block), its source drawing and its layer leaf.</summary>
    public sealed record Item(string Source, string Leaf, string Part, IReadOnlyList<(double X, double Y)> Path);

    private readonly record struct Seg(int Item, double X1, double Y1, double X2, double Y2);

    /// <summary>For each item, the length (drawing units) lying on an earlier item of the same leaf from another source;
    /// for a one-point item, 1 when an earlier block of the same leaf and part stands at the same point.</summary>
    public static double[] Measure(IReadOnlyList<Item> items, double tolerance, double piece)
    {
        var result = new double[items.Count];
        // Only a layer present in two or more sources can be duplicated across them.
        var shared = items.GroupBy(i => (i.Leaf, i.Part)).Where(g => g.Select(i => i.Source).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(g => g.Key).ToHashSet();
        if (shared.Count == 0) return result;
        var cell = Math.Max(tolerance * 4, piece);
        var grid = new Dictionary<(string Leaf, string Part, long X, long Y), List<Seg>>();
        (long, long) Cell(double x, double y) => ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!shared.Contains((it.Leaf, it.Part)) || it.Path.Count == 0) continue;
            var pieces = Pieces(it.Path, piece);
            if (it.Path.Count == 1)
            {
                var (x, y) = it.Path[0];
                if (Near(x, y)) result[i] = 1;
            }
            else
                foreach (var (x1, y1, x2, y2) in pieces)
                    if (Near((x1 + x2) / 2, (y1 + y2) / 2)) result[i] += Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            foreach (var (x1, y1, x2, y2) in pieces)
            {
                var (cx, cy) = Cell((x1 + x2) / 2, (y1 + y2) / 2);
                var key = (it.Leaf, it.Part, cx, cy);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Seg>();
                list.Add(new Seg(i, x1, y1, x2, y2));
            }

            bool Near(double px, double py)
            {
                var (gx, gy) = Cell(px, py);
                for (var a = gx - 1; a <= gx + 1; a++)
                for (var b = gy - 1; b <= gy + 1; b++)
                    if (grid.TryGetValue((it.Leaf, it.Part, a, b), out var list))
                        foreach (var s in list)
                            if (!string.Equals(items[s.Item].Source, it.Source, StringComparison.OrdinalIgnoreCase) && Distance(px, py, s) <= tolerance)
                                return true;
                return false;
            }
        }
        return result;
    }

    /// <summary>The path cut into pieces no longer than <paramref name="piece"/>; a one-point path is one zero-length piece.</summary>
    private static List<(double X1, double Y1, double X2, double Y2)> Pieces(IReadOnlyList<(double X, double Y)> path, double piece)
    {
        var list = new List<(double, double, double, double)>();
        if (path.Count == 1) { list.Add((path[0].X, path[0].Y, path[0].X, path[0].Y)); return list; }
        for (var k = 1; k < path.Count; k++)
        {
            var (x1, y1) = path[k - 1]; var (x2, y2) = path[k];
            var len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            var n = Math.Max(1, (int)Math.Ceiling(len / piece));
            for (var j = 0; j < n; j++)
                list.Add((x1 + (x2 - x1) * j / n, y1 + (y2 - y1) * j / n, x1 + (x2 - x1) * (j + 1) / n, y1 + (y2 - y1) * (j + 1) / n));
        }
        return list;
    }

    private static double Distance(double px, double py, Seg s)
    {
        var dx = s.X2 - s.X1; var dy = s.Y2 - s.Y1; var l = dx * dx + dy * dy;
        var t = l == 0 ? 0 : Math.Clamp(((px - s.X1) * dx + (py - s.Y1) * dy) / l, 0, 1);
        var x = s.X1 + t * dx - px; var y = s.Y1 + t * dy - py;
        return Math.Sqrt(x * x + y * y);
    }
}
