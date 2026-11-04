using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Road axes rebuilt from EXPLODED Civil 3D alignments (2026-11-04, HW-40XX4X15-TR-M34-TARGET: a traffic plan with no
/// Civil object at all). Exploding an alignment leaves, on its alignment layer (HW-ALGN):
/// <list type="bullet">
/// <item>the axis as polyline pieces that meet end to end (tangents, arcs, spirals);</item>
/// <item>the curves' tangent construction lines (Lines from each PI to its tangent points) - not the axis;</item>
/// <item>the station ticks (AeccTickLine blocks on the axis) with their labels "000", "020", ... - the station within
/// the kilometre, the kilometre itself dropped;</item>
/// <item>the curve labels "Ramp_1 / 0+044.49 / R=60.00 ..." with a leader to the PI: the road's name and a full station.</item>
/// </list>
/// The pieces are chained into one axis per road; its direction and stations come from the ticks (every 20 m), its
/// kilometre and name from the curve labels. An axis no label reaches keeps the layer's name with a number.
/// </summary>
public static class ExplodedAlignmentLogic
{
    /// <summary>A station tick on the plan: where it stands and the value printed beside it ("820" or "545.56").</summary>
    public sealed record Tick(P2 At, double Value);

    /// <summary>A curve label: the road's name, the station it prints, the PI its leader points at, and the far ends of
    /// the tangent construction lines from that PI (they touch the axis).</summary>
    public sealed record Label(string Name, double Station, P2 Pi, IReadOnlyList<P2> Touches);

    /// <summary>A rebuilt axis: its name, its vertices in the stationing direction, the station of the first vertex.</summary>
    public sealed record Axis(string Name, IReadOnlyList<P2> Vertices, double StartStation);

    private const double JoinTolM = 0.01;
    private const double OnAxisTolM = 0.05;

    /// <summary>An alignment layer by the office's naming: "HW-ALGN" (any discipline prefix "*-ALGN"), not its
    /// annotation layers ("HW-ALGN-SEC-NAME").</summary>
    public static bool IsAlignmentLayer(string? layer)
    {
        var leaf = PlanAxis.LayerLeaf(layer);
        return leaf.Equals("ALGN", StringComparison.OrdinalIgnoreCase) ||
               leaf.EndsWith("-ALGN", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith("_ALGN", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tick's printed value ("020", "545.56"), or null when the text is not one.</summary>
    public static double? TickValue(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        // "020" within the first kilometre, "1240" past it (TR-M34's Main-40 prints the kilometre once it has one).
        return Regex.IsMatch(t, @"^\d{1,5}(\.\d+)?$") &&
               double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>A curve label's name and station from its text lines ("Ramp_1", "0+044.49", ...), or null.</summary>
    public static (string Name, double Station)? ParseLabel(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return null;
        var station = Regex.Match(lines[1], @"^(\d+)\+(\d{1,3}(?:\.\d+)?)$");
        if (!station.Success || lines[0].Contains('=')) return null;
        var km = double.Parse(station.Groups[1].Value, CultureInfo.InvariantCulture);
        var m = double.Parse(station.Groups[2].Value, CultureInfo.InvariantCulture);
        return (lines[0], km * 1000 + m);
    }

    /// <summary>Pieces joined end to end (either way round) into chains; each chain's vertices in drawing order.</summary>
    public static List<List<P2>> Chain(IEnumerable<IReadOnlyList<P2>> pieces)
    {
        var open = pieces.Where(p => p.Count >= 2).Select(p => p.ToList()).ToList();
        var chains = new List<List<P2>>();
        while (open.Count > 0)
        {
            var chain = open[0];
            open.RemoveAt(0);
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < open.Count; i++)
                {
                    var p = open[i];
                    List<P2>? joined = null;
                    if (Near(p[0], chain[^1])) joined = chain.Concat(p.Skip(1)).ToList();
                    else if (Near(p[^1], chain[^1])) joined = chain.Concat(Enumerable.Reverse(p).Skip(1)).ToList();
                    else if (Near(p[^1], chain[0])) joined = p.Concat(chain.Skip(1)).ToList();
                    else if (Near(p[0], chain[0])) joined = Enumerable.Reverse(p).Concat(chain.Skip(1)).ToList();
                    if (joined == null) continue;
                    chain = joined;
                    open.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
            chains.Add(chain);
        }
        return chains;
    }

    /// <summary>
    /// The axes: every chain at least <paramref name="minLengthM"/> long, turned to run with its stations, stationed from
    /// its ticks (and the kilometre from its labels), named by its labels.
    /// </summary>
    public static List<Axis> Build(IEnumerable<IReadOnlyList<P2>> pieces, IReadOnlyList<Tick> ticks, IReadOnlyList<Label> labels,
        string fallbackName, double minLengthM)
    {
        var axes = new List<Axis>();
        var unnamed = 0;
        foreach (var raw in Chain(pieces))
        {
            var chain = raw;
            var cumulative = Cumulative(chain);
            if (cumulative[^1] < minLengthM) continue;

            var mine = labels.Where(l => l.Touches.Any(t => Project(chain, cumulative, t).Distance <= OnAxisTolM)).ToList();
            var onTicks = ticks
                .Select(t => (Tick: t, Hit: Project(chain, cumulative, t.At)))
                .Where(x => x.Hit.Distance <= OnAxisTolM)
                .OrderBy(x => x.Hit.S)
                .Select(x => (S: x.Hit.S, x.Tick.Value))
                .ToList();

            // Direction and stations together: station = start + s, so every true station tick implies the same start
            // (modulo the kilometre the value may leave out). Other numbered ticks on the axis - TR-M34's section
            // numbers "18", "19", ... one per 20 m inside a block - imply a different start each and are outvoted. The
            // direction is the one whose ticks agree best.
            var length = cumulative[^1];
            var forward = Agreement(onTicks.Select(t => t.Value - t.S).ToList());
            var backward = Agreement(onTicks.Select(t => t.Value - (length - t.S)).ToList());
            if (backward.Size > forward.Size)
            {
                chain = Enumerable.Reverse(chain).ToList();
                cumulative = Cumulative(chain);
            }
            var start = backward.Size > forward.Size ? backward.Start : forward.Start;
            // A value that leaves out its kilometre can put the start a kilometre low; stations are not negative.
            while (start < -1) start += 1000;
            // The kilometre: the labels' full stations, at the PI's foot on the axis (the PI stands off the axis by
            // up to a tangent length, far less than 500 m).
            if (mine.Count > 0)
            {
                var shifts = mine.Select(l => Math.Round((l.Station - (start + Project(chain, cumulative, l.Pi).S)) / 1000)).ToList();
                var shift = shifts.GroupBy(k => k).OrderByDescending(g => g.Count()).First().Key;
                start += 1000 * shift;
            }

            var name = mine.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                ?? FormattableString.Invariant($"{fallbackName} #{++unnamed}");
            axes.Add(new Axis(name, chain, start));
        }
        // One road drawn as two chains (a gap in the explode): numbered, so a pick names the piece it was on.
        return axes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Count() == 1
                ? g.AsEnumerable()
                : g.OrderByDescending(a => Cumulative(a.Vertices)[^1]).Select((a, i) => i == 0 ? a : a with { Name = FormattableString.Invariant($"{a.Name} #{i + 1}") }))
            .ToList();
    }

    /// <summary>The start most of the ticks agree on (within 1 m, modulo 1000) and how many agree; 0 and 0 for none.</summary>
    private static (double Start, int Size) Agreement(IReadOnlyList<double> starts)
    {
        var best = (Start: 0.0, Size: 0);
        foreach (var candidate in starts)
        {
            var agree = starts.Select(c => Wrap(c - candidate)).Where(d => Math.Abs(d) <= 1.0).ToList();
            if (agree.Count > best.Size) best = (candidate + agree.Average(), agree.Count);
        }
        return best;
    }

    private static double Wrap(double dv)
    {
        var d = dv % 1000;
        if (d > 500) d -= 1000;
        if (d <= -500) d += 1000;
        return d;
    }

    private static bool Near(P2 a, P2 b) => Math.Abs(a.X - b.X) <= JoinTolM && Math.Abs(a.Y - b.Y) <= JoinTolM;

    private static double[] Cumulative(IReadOnlyList<P2> v)
    {
        var s = new double[v.Count];
        for (var i = 1; i < v.Count; i++) s[i] = s[i - 1] + Math.Sqrt(Math.Pow(v[i].X - v[i - 1].X, 2) + Math.Pow(v[i].Y - v[i - 1].Y, 2));
        return s;
    }

    /// <summary>The distance along the chain of the nearest point to <paramref name="p"/>, and how far p is from it.</summary>
    private static (double S, double Distance) Project(IReadOnlyList<P2> v, double[] cumulative, P2 p)
    {
        var best = (S: 0.0, Distance: double.PositiveInfinity);
        for (var i = 0; i + 1 < v.Count; i++)
        {
            var dx = v[i + 1].X - v[i].X;
            var dy = v[i + 1].Y - v[i].Y;
            var lengthSq = dx * dx + dy * dy;
            var t = lengthSq > 0 ? Math.Clamp(((p.X - v[i].X) * dx + (p.Y - v[i].Y) * dy) / lengthSq, 0, 1) : 0;
            var x = v[i].X + t * dx;
            var y = v[i].Y + t * dy;
            var d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
            if (d < best.Distance) best = (cumulative[i] + t * Math.Sqrt(lengthSq), d);
        }
        return best;
    }
}
