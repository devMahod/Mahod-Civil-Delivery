namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// A wall, footing or beam drawn in plan as its two faces: two parallel lines a thickness apart. Measuring the layer's
/// line length counts the element twice (ST-RW05, 2026-11-09: RW-05 is 34.4 m of wall drawn as two 34.4 m faces 0.34 m
/// apart). Each straight piece is paired with the nearest parallel piece within the thickness range; a paired piece counts
/// half towards the axis and gives the thickness, an unpaired one counts in full.
/// </summary>
public static class FacePairs
{
    public readonly record struct Segment(double X1, double Y1, double X2, double Y2)
    {
        public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
    }

    /// <summary>Axis length, the share of the drawn length that has a face partner, and the length-weighted median spacing.</summary>
    public sealed record Result(double AxisM, double DrawnM, double PairedShare, double WidthM)
    {
        /// <summary>Most of the element is drawn as face pairs: the axis and the width are the drawing's own.</summary>
        public bool IsFaces => PairedShare >= 0.6 && WidthM > 0;
    }

    private const double ParallelCos = 0.966;   // within 15°

    public static Result Measure(IReadOnlyList<Segment> segments, double minWidthM, double maxWidthM)
    {
        var parts = segments.Where(s => s.Length > 1e-6).ToArray();
        var drawn = parts.Sum(s => s.Length);
        if (parts.Length < 2 || drawn <= 0) return new(drawn, drawn, 0, 0);
        var widths = new List<(double Width, double Length)>();
        var paired = 0.0;
        var pairedIndex = new HashSet<int>();
        for (var i = 0; i < parts.Length; i++)
        {
            var a = parts[i]; var la = a.Length;
            var ux = (a.X2 - a.X1) / la; var uy = (a.Y2 - a.Y1) / la;
            var mx = (a.X1 + a.X2) / 2; var my = (a.Y1 + a.Y2) / 2;
            var best = double.MaxValue;
            for (var j = 0; j < parts.Length; j++)
            {
                if (j == i) continue;
                var b = parts[j]; var lb = b.Length;
                var vx = (b.X2 - b.X1) / lb; var vy = (b.Y2 - b.Y1) / lb;
                if (Math.Abs(ux * vx + uy * vy) < ParallelCos) continue;
                // The midpoint must project inside b (a little slack for staggered ends) and lie off b's line by a thickness.
                var t = (mx - b.X1) * vx + (my - b.Y1) * vy;
                if (t < -0.25 * lb || t > 1.25 * lb) continue;
                var d = Math.Abs((mx - b.X1) * -vy + (my - b.Y1) * vx);
                if (d >= minWidthM && d <= maxWidthM && d < best) best = d;
            }
            if (best == double.MaxValue) continue;
            paired += la; widths.Add((best, la)); pairedIndex.Add(i);
        }
        var width = 0.0;
        if (widths.Count > 0)
        {
            var sorted = widths.OrderBy(w => w.Width).ToArray(); var half = sorted.Sum(w => w.Length) / 2; var run = 0.0;
            foreach (var w in sorted) { run += w.Length; if (run >= half) { width = w.Width; break; } }
        }
        // The short closing lines across the ends of a face pair (0.34 m caps of a 0.34 m wall) are not wall length.
        var caps = width > 0 ? parts.Where((s, i) => s.Length <= 1.2 * width && !pairedIndex.Contains(i)).Sum(s => s.Length) : 0;
        var share = paired / Math.Max(1e-9, drawn - caps);
        return new(drawn - caps - paired / 2, drawn, share, width);
    }
}
