using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// The road as the corridor built it at one station, for a typical section drawn the way the office draws one
/// (2026-11-02, the office's sheet MHD-HW-000-IC_00000-DD-5001-02): the finished surface, the pavement layers, and what
/// the drawing reads off them - the surface level under each strip, the cross slope of each carriageway, the ratio of
/// each side slope, and whether a strip is paved at all.
///
/// Offsets are signed metres from the axis (right positive, as Civil's offset); levels are relative to the surface at
/// the axis. Pure logic: the plugin reads the corridor's applied assembly and draws what this returns.
/// </summary>
public static class TypicalSheetGeometryLogic
{
    public readonly record struct Pt(double X, double Y);

    /// <summary>One link of the finished surface (code Top) and whether it is paved (code Pave, or a pavement shape under it).</summary>
    public sealed record SurfaceLink(Pt A, Pt B, bool Paved);

    /// <summary>One pavement or fill layer (a corridor shape) as a closed outline, with the corridor's code for it.</summary>
    public sealed record Layer(string Code, IReadOnlyList<Pt> Outline);

    public sealed record SectionGeometry(IReadOnlyList<SurfaceLink> Surface, IReadOnlyList<Layer> Layers, IReadOnlyList<Pt> Ground)
    {
        public double MinX => Surface.Count == 0 ? 0 : Surface.Min(l => Math.Min(l.A.X, l.B.X));
        public double MaxX => Surface.Count == 0 ? 0 : Surface.Max(l => Math.Max(l.A.X, l.B.X));
    }

    /// <summary>The surface level at an offset (the highest link over it), or null beyond the corridor.</summary>
    public static double? LevelAt(IReadOnlyList<SurfaceLink> surface, double x)
    {
        double? best = null;
        foreach (var link in surface)
        {
            var (a, b) = link.A.X <= link.B.X ? (link.A, link.B) : (link.B, link.A);
            if (x < a.X - 1e-9 || x > b.X + 1e-9) continue;
            var y = b.X - a.X < 1e-9 ? Math.Max(a.Y, b.Y) : a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X);
            if (best == null || y > best) best = y;
        }
        return best;
    }

    /// <summary>The continuous offset intervals where the finished surface was actually measured.</summary>
    public static List<(double From, double To)> Coverage(IEnumerable<SurfaceLink> links)
    {
        var result = new List<(double From, double To)>();
        foreach (var (lo, hi) in links.Select(l => (Math.Min(l.A.X, l.B.X), Math.Max(l.A.X, l.B.X)))
                     .Where(v => double.IsFinite(v.Item1) && double.IsFinite(v.Item2) && v.Item2 > v.Item1)
                     .OrderBy(v => v.Item1))
        {
            if (result.Count == 0 || lo > result[^1].To + 1e-6) result.Add((lo, hi));
            else if (hi > result[^1].To) result[^1] = (result[^1].From, hi);
        }
        return result;
    }

    public static bool Covers(IReadOnlyList<(double From, double To)> spans, double from, double to) =>
        spans.Any(s => from >= s.From - 1e-6 && to <= s.To + 1e-6);

    /// <summary>A plan cut also crosses adjacent roads. A modeled panel dimensions only its own
    /// surface. At a clipped edge retain the measured width, but not the identity of a full lane.</summary>
    public static DraftedTypicalSectionLogic.DraftedSection FitToSurface(
        DraftedTypicalSectionLogic.DraftedSection section, IReadOnlyList<SurfaceLink> surface)
    {
        var spans = Coverage(surface);
        bool At(double x) => Covers(spans, x, x);
        var breaks = section.Breaks.Where(b => At(b.OffsetM)).ToList();
        var strips = new List<DraftedTypicalSectionLogic.Strip>();
        var clipped = false;
        foreach (var strip in section.Strips)
        {
            if (Covers(spans, strip.FromM, strip.ToM)) { strips.Add(strip); continue; }
            clipped = true;
            foreach (var span in spans)
            {
                var from = Math.Max(span.From, strip.FromM);
                var to = Math.Min(span.To, strip.ToM);
                if (to - from < 0.01) continue;
                strips.Add(strip with { FromM = from, ToM = to, Name = null });
                foreach (var x in new[] { from, to })
                    if (!breaks.Any(b => Math.Abs(b.OffsetM - x) < 1e-6))
                        breaks.Add(new(x, "surface-limit", "", "corridor"));
            }
        }
        return section with
        {
            Breaks = breaks.OrderBy(b => b.OffsetM).ToList(), Strips = strips,
            Objects = section.Objects.Where(o => At(o.OffsetM)).ToList(),
            Barriers = section.Barriers.Where(o => At(o.OffsetM)).ToList(),
            Fences = section.Fences.Where(o => At(o.OffsetM)).ToList(),
            Zones = section.Zones.Where(z => Covers(spans, z.FromM, z.ToM)).ToList(),
            Groups = section.Groups.Where(z => Covers(spans, z.FromM, z.ToM)).ToList(),
            Findings = clipped ? section.Findings.Append("רצועות התוכנית הוגבלו לתחום פני הדרך של הקורידור בתחנה זו.").ToList() : section.Findings,
        };
    }

    /// <summary>Use both contact points and the complete tyre span. A vehicle cannot straddle a
    /// missing portion of the model. Flat at zero is the convention for an explicitly schematic panel.</summary>
    public static (double Y, double Rotation)? VehicleSeat(IReadOnlyList<SurfaceLink>? surface, double x, double halfTrack)
    {
        if (surface == null) return (0, 0);
        if (!Covers(Coverage(surface), x - halfTrack, x + halfTrack)) return null;
        var left = LevelAt(surface, x - halfTrack);
        var right = LevelAt(surface, x + halfTrack);
        if (left == null || right == null) return null;
        var slope = (right.Value - left.Value) / (2 * halfTrack);
        if (Math.Abs(slope) > 0.25) return null;
        return ((left.Value + right.Value) / 2, Math.Atan(slope));
    }

    /// <summary>The share of [from, to] the paved links cover (0-1).</summary>
    public static double PavedShare(IReadOnlyList<SurfaceLink> surface, double from, double to)
    {
        if (!(to > from)) return 0;
        var covered = new List<(double A, double B)>();
        foreach (var link in surface.Where(l => l.Paved))
        {
            var a = Math.Max(from, Math.Min(link.A.X, link.B.X));
            var b = Math.Min(to, Math.Max(link.A.X, link.B.X));
            if (b > a) covered.Add((a, b));
        }
        var total = 0.0;
        var end = double.NegativeInfinity;
        foreach (var (a, b) in covered.OrderBy(c => c.A))
        {
            var start = Math.Max(a, end);
            if (b > start) total += b - start;
            end = Math.Max(end, b);
        }
        return total / (to - from);
    }

    /// <summary>A strip is paved when the paved links cover most of it (a curb face or a rounding at its edge aside).</summary>
    public static bool IsPaved(IReadOnlyList<SurfaceLink> surface, double from, double to) => PavedShare(surface, from, to) >= 0.8;

    /// <summary>One cross-slope mark: over [FromX, ToX] the surface falls toward <see cref="DownToRight"/> by Percent.</summary>
    public sealed record CrossSlope(double FromX, double ToX, double Percent, bool DownToRight)
    {
        public string Text => Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// The cross slope of each carriageway, as the office marks one arrow per carriageway ("2%" pointing down the slope):
    /// consecutive paved strips on one side of the axis that fall the same way are one carriageway. Strips flatter than
    /// 0.3% or steeper than 12% are no carriageway slope (a curb, a rounding).
    /// </summary>
    public static IReadOnlyList<CrossSlope> CrossSlopes(IReadOnlyList<SurfaceLink> surface, IEnumerable<(double From, double To)> pavedStrips)
    {
        var marks = new List<CrossSlope>();
        (double From, double To, int Sign)? current = null;
        foreach (var (from, to) in pavedStrips.Where(s => s.To > s.From).OrderBy(s => s.From))
        {
            var slope = SlopeOver(from, to);
            if (slope == null || Math.Abs(slope.Value) < 0.003 || Math.Abs(slope.Value) > 0.12) { Flush(); continue; }
            var sign = Math.Sign(slope.Value);
            if (current is { } run && Math.Abs(run.To - from) < 0.05 && (run.To <= 1e-6) == (to <= 1e-6) && run.Sign == sign)
                current = (run.From, to, sign);
            else
            {
                Flush();
                current = (from, to, sign);
            }
        }
        Flush();
        return marks;

        // Measured just inside the ends, so a curb face or a crown point on an end does not count.
        double? SlopeOver(double from, double to)
        {
            if (to - from < 0.2) return null;
            var a = LevelAt(surface, from + 0.05);
            var b = LevelAt(surface, to - 0.05);
            return a == null || b == null ? null : (b.Value - a.Value) / (to - from - 0.1);
        }

        void Flush()
        {
            if (current is not { } run) return;
            if (SlopeOver(run.From, run.To) is { } slope)
                marks.Add(new CrossSlope(run.From, run.To, Math.Round(Math.Abs(slope) * 100.0, 1), slope < 0));
            current = null;
        }
    }

    /// <summary>One side-slope mark ("1:2.5") at the middle of an unpaved surface link steeper than 1:10.</summary>
    public sealed record SideSlope(Pt At, double Run, double AngleRad)
    {
        public string Text => "1:" + Run.ToString(Run < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The side slopes beyond the paved road: unpaved links outside [pavedMin, pavedMax] steeper than 1:10 and at least
    /// 1.5 m long (the office writes "1:2.5" on each embankment and ditch face).
    /// </summary>
    public static IReadOnlyList<SideSlope> SideSlopes(IReadOnlyList<SurfaceLink> surface, double pavedMin, double pavedMax)
    {
        var marks = new List<SideSlope>();
        foreach (var link in surface.Where(l => !l.Paved))
        {
            var dx = Math.Abs(link.B.X - link.A.X);
            var dy = Math.Abs(link.B.Y - link.A.Y);
            if (dx < 1e-6 || dy < 1e-6) continue;
            var mid = new Pt((link.A.X + link.B.X) / 2, (link.A.Y + link.B.Y) / 2);
            if (mid.X > pavedMin && mid.X < pavedMax) continue;
            if (Math.Sqrt(dx * dx + dy * dy) < 1.5 || dx / dy > 10) continue;
            var (a, b) = link.A.X <= link.B.X ? (link.A, link.B) : (link.B, link.A);
            marks.Add(new SideSlope(mid, Math.Round(dx / dy * 2, MidpointRounding.AwayFromZero) / 2, Math.Atan2(b.Y - a.Y, b.X - a.X)));
        }
        return marks;
    }

    /// <summary>
    /// The thickness of each layer under an offset, top down (the office's callout list: "4 ס"מ שכבת אספלט ..."):
    /// where a vertical line at <paramref name="x"/> crosses each layer's outline.
    /// </summary>
    public static IReadOnlyList<(string Code, double Top, double ThicknessM)> LayersAt(IReadOnlyList<Layer> layers, double x)
    {
        var found = new List<(string, double, double)>();
        foreach (var layer in layers)
        {
            var ys = new List<double>();
            var points = layer.Outline;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if ((a.X <= x && b.X > x) || (b.X <= x && a.X > x))
                    ys.Add(a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X));
            }
            if (ys.Count < 2) continue;
            var top = ys.Max();
            var thickness = top - ys.Min();
            if (thickness > 0.005) found.Add((layer.Code, top, thickness));
        }
        return found.OrderByDescending(f => f.Item2).ToList();
    }
}
