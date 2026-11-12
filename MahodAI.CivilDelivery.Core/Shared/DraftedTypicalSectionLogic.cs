using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A typical section read off a DRAFTED plan (2026-11-02, MAAKAF DD-500X): no corridor, no alignment, no surface in the
/// drawing or its XREFs — the design is linework in the GM / SM / TT XREFs, the trees in the survey XREF. The firm's
/// section program cuts a corridor, so it has nothing to cut here; instead the plan is cut along the marked line and
/// every line it crosses is read by the office's layer convention (HW-TRWY lane edge, HW-SHLD shoulder, *CURB* curb,
/// HW-SDWK sidewalk, HW-GARDEN, HW-BIKE-LANE, TR-MARK-* markings, *FENCE*/*GADER* fence, *BARI* barrier,
/// 0-TT-*-NITUV temporary routing, 0-TT-*-WORK work zone…). Lines on other layers (survey codes, structures, EV
/// annotation) are not part of a typical section and are left out.
///
/// What comes out is schematic, as the office's typical sections are: offsets and widths from the plan, no levels
/// (the drawing has no surface to read them from). A strip is named only when its bounds say what it is; anything
/// else keeps its width and no name.
///
/// Pure logic: the caller collects the crossings (signed metres from the axis, right positive, as Civil's offset) and
/// draws what this returns.
/// </summary>
public static class DraftedTypicalSectionLogic
{
    /// <summary>Kinds of plan lines. Plain strings so the drawing code and the tests compare them as data.</summary>
    public static class Kind
    {
        public const string Edge = "edge";
        public const string Track = "track-centre";
        public const string Rail = "rail";
        public const string Platform = "platform";
        public const string Shoulder = "shoulder";
        public const string Curb = "curb";
        public const string Sidewalk = "sidewalk";
        public const string Garden = "garden";
        public const string Bike = "bike";
        public const string Marking = "marking";
        /// <summary>The yellow edge line (807): the travel lanes on one side, a shoulder on the other.</summary>
        public const string EdgeLine = "edgeline";
        /// <summary>A yellow line that is not the 807 edge line - 6403's double yellow 3-3 beside the outer lane: that
        /// lane is the high-occupancy lane ("נר\"ת").</summary>
        public const string HovLine = "hovline";
        public const string Routing = "routing";
        public const string Fence = "fence";
        public const string Barrier = "barrier";
        public const string Work = "work";
        public const string Row = "row";
    }

    /// <summary>Kinds of objects standing on the section line (block references near it).</summary>
    public static class ObjectKind
    {
        public const string Tree = "tree";
        public const string Light = "light";
        public const string Sign = "sign";
    }

    /// <summary>Two crossings of the same kind closer than this are one line drawn twice (or a line and its offset copy).</summary>
    public const double MergeM = 0.2;

    /// <summary>The narrowest bike path bounded by one bike line (the other a curb or an edge).</summary>
    public const double BikeMinM = 1.5;

    /// <summary>The widest painted stripe whose two edge lines are read as one line (a double line, 0.30 apart).</summary>
    public const double StripeM = 0.35;

    /// <summary>Lines of different kinds closer than this are one spot of the road (an edge drawn on its shoulder line);
    /// a curb 0.15 m from the edge is a strip of its own.</summary>
    public const double SameSpotM = 0.05;

    /// <summary>A strip narrower than this carries no name (a curb face, a marking width).</summary>
    public const double MinNamedWidthM = 1.0;

    /// <summary>
    /// A lane is this wide (2026-11-02, owner: "when it says lane, check it is a lane and its width is right"). Israeli
    /// lanes run 3.00-3.75 m, a bus or curb lane up to ~3.9; a wider strip between lane lines is a lane plus something
    /// the plan does not mark (a missing line, a shoulder), and it is not called a lane.
    /// </summary>
    public const double LaneMinM = 2.6;

    public const double LaneMaxM = 4.0;

    /// <summary>A shoulder is at most this wide (the office's outer shoulders: 3.50 m).</summary>
    public const double ShoulderMaxM = 4.0;

    /// <summary>A barrier strip in a median is at most this wide (a concrete barrier's faces: 0.6-0.8 m).</summary>
    public const double BarrierMaxM = 1.2;

    /// <summary>
    /// A barrier at least this far inside the strip beyond the edge line splits it into the shoulder and the berm
    /// behind the barrier (<see cref="Build"/>); closer to a bound it stands on that line.
    /// </summary>
    public const double BarrierSplitMinM = 0.3;

    /// <summary>The berm behind a roadside barrier ("כתף") is at most this wide (road 43: 1.30 m).</summary>
    public const double BermMaxM = 2.5;

    /// <summary>A median the axis runs through is this wide (the office's temporary medians: 1.80 m).</summary>
    public const double MedianMinM = 0.6;

    /// <summary>Wider than this a strip around the axis is a carriageway, not a median.</summary>
    public const double MedianMaxM = 3.0;

    /// <summary>
    /// A strip around the axis with a barrier in it is the median up to this wide (2026-11-04, TR-M34 Main-40: 10.10 m
    /// between the two 803 edge lines, the barrier walls inside it drawn as blocks, no line between).
    /// </summary>
    public const double BarrierMedianMaxM = 12.0;

    /// <summary>
    /// A corridor's piece edge exported to the plan as "&lt;corridor&gt;+&lt;piece&gt;+W" (TR-M34: RD-40+110+W at
    /// 12.40, where the marking plan draws no lane line). Read as a pavement edge on drafted drawings only - where the
    /// corridor itself is in the drawing, it is read instead.
    /// </summary>
    public static bool IsCorridorEdgeLayer(string? layer) =>
        System.Text.RegularExpressions.Regex.IsMatch(PlanAxis.LayerLeaf(layer), @"^[^+]+\+[^+]+\+W$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Ordered: the first pattern that matches the layer leaf wins (a sidewalk CURB is a curb, not a sidewalk).
    private static readonly (string Pattern, string Kind)[] LayerKinds =
    {
        ("*-811*", ""), ("*-815*", ""), ("*-810*", ""), ("*-809*", ""), ("*ZEBRA*", ""), ("TR-MARK-ARW*", ""),
        ("*-TXT*", ""), ("*-TX-*", ""), ("*TEXT*", ""), ("*-DIM*", ""), ("*-NOTE*", ""), ("CL_*", ""),
        ("RR-ALGN", Kind.Track), ("RR-TRACK-CL*", Kind.Track), ("TRACK-CENTER*", Kind.Track),
        ("RR-RAIL", Kind.Rail), ("RR-RAIL-*", Kind.Rail), ("RAIL-LINE*", Kind.Rail),
        ("RR-PLTF*", Kind.Platform), ("PLATFORM-EDGE*", Kind.Platform),
        ("*FENCE*", Kind.Fence), ("*GADER*", Kind.Fence),
        ("*BARI*", Kind.Barrier), ("*MAAKE*", Kind.Barrier), ("מעקה*", Kind.Barrier), ("*GUARD*", Kind.Barrier),
        ("*WORK*", Kind.Work),
        ("*NITUV*", Kind.Routing),
        ("*CURB*", Kind.Curb), ("*SHAFA*", Kind.Curb),
        ("HW-SHLD*", Kind.Shoulder), ("*SHOULDER*", Kind.Shoulder),
        ("HW-TRWY*", Kind.Edge), ("HW-EOP*", Kind.Edge), ("*EDGE*", Kind.Edge),
        ("HW-SDWK*", Kind.Sidewalk), ("*SIDEWALK*", Kind.Sidewalk), ("*MIDRA*", Kind.Sidewalk),
        ("HW-GARDEN*", Kind.Garden), ("HW-GAN*", Kind.Garden), ("HW-COLORED-GARDEN*", Kind.Garden), ("*GRDN*", Kind.Garden),
        ("*BIKE*", Kind.Bike),
        ("*-807*", Kind.EdgeLine), ("*MARK-YLW*", Kind.HovLine), ("*-803*", Kind.EdgeLine),
        ("TR-MARK*", Kind.Marking), ("*MARK-WHT*", Kind.Marking),
        ("*PGVUL*", Kind.Row), ("ROW*", Kind.Row), ("ZCHUT*", Kind.Row), ("*זכות הדרך*", Kind.Row),
    };

    private static readonly (string Pattern, string Kind)[] ObjectKinds =
    {
        ("*TREE*", ObjectKind.Tree), ("*ST_SHIMUR*", ObjectKind.Tree), ("*ST_KRITA*", ObjectKind.Tree),
        ("*ST_DEAD*", ObjectKind.Tree), ("*ST_HAATAKA*", ObjectKind.Tree), ("LA-VEGT*", ObjectKind.Tree), ("PT-EZ*", ObjectKind.Tree), ("EZ-*", ObjectKind.Tree),
        ("*LIGHT*", ObjectKind.Light), ("*TEURA*", ObjectKind.Light), ("*TAURA*", ObjectKind.Light),
        ("TR-SIGN*", ObjectKind.Sign), ("*-SIGN-*", ObjectKind.Sign),
    };

    /// <summary>The kind of a plan line by its layer (leaf name; an XREF prefix is ignored), or null when it is not one.</summary>
    public static string? ClassifyLayer(string? layer)
    {
        var leaf = PlanAxis.LayerLeaf(layer);
        if (leaf.Length == 0) return null;
        foreach (var (pattern, kind) in LayerKinds)
            if (SectionProjectionLogic.Wildcard(leaf, pattern)) return kind.Length == 0 ? null : kind;
        return null;
    }

    /// <summary>The kind of a block on the line, by its layer or else its block name; null when it is none of them.</summary>
    public static string? ClassifyObject(string? layer, string? blockName)
    {
        foreach (var candidate in new[] { PlanAxis.LayerLeaf(layer), PlanAxis.LayerLeaf(blockName) })
        {
            if (candidate.Length == 0) continue;
            foreach (var (pattern, kind) in ObjectKinds)
                if (SectionProjectionLogic.Wildcard(candidate, pattern)) return kind;
        }
        return null;
    }

    public sealed record Crossing(double OffsetM, string Kind, string Layer, string Source);

    public sealed record PlacedObject(double OffsetM, string Kind, string Layer, string Name);

    /// <summary>One piece between two consecutive breaks, left to right. SurfaceKind says how to draw it.</summary>
    public sealed record Strip(double FromM, double ToM, string? Name, string SurfaceKind)
    {
        public double WidthM => ToM - FromM;
    }

    public sealed record Zone(double FromM, double ToM, string Name);

    public sealed record DraftedSection(
        IReadOnlyList<Crossing> Breaks,
        IReadOnlyList<Strip> Strips,
        IReadOnlyList<Zone> Zones,
        IReadOnlyList<Crossing> Fences,
        IReadOnlyList<Crossing> Barriers,
        IReadOnlyList<PlacedObject> Objects,
        IReadOnlyList<string> Findings)
    {
        /// <summary>Strips the office dimensions as one above their own widths: the median (shoulder, barrier, shoulder).</summary>
        public IReadOnlyList<Zone> Groups { get; init; } = Array.Empty<Zone>();

        public int NamedCount => Strips.Count(strip => strip.Name != null);
        public double MinM => Breaks.Count > 0 ? Math.Min(Breaks[0].OffsetM, 0) : 0;
        public double MaxM => Breaks.Count > 0 ? Math.Max(Breaks[^1].OffsetM, 0) : 0;
    }

    /// <summary>One stacked panel of the typical section: one stage of the road (existing, design, a traffic stage).</summary>
    public sealed record Panel(int Order, string Title, DraftedSection Section);

    /// <summary>
    /// Which panel a crossing belongs to, by the XREF it came from — the office keeps each state of the road in its own
    /// drawing and draws one panel per state (MAAKAF: HW-IC_EXST_TO4-GM = existing, HW-IC_4NATIV-GM = the design,
    /// TR-PD-TT-GM-S1 = traffic stage 1). Mixing them in one chain gives widths that exist in no stage.
    /// </summary>
    public static (int Order, string Title) StageOf(string? source)
    {
        var name = (source ?? string.Empty).ToUpperInvariant();
        if (name.Contains("EXST") || name.Contains("EXIST") || name.Contains("_EX_") || name.Contains("-EX-") || name.EndsWith("-EX"))
            return (0, "מצב קיים");
        var stage = System.Text.RegularExpressions.Regex.Match(name, @"(?:^|[-_])S(\d{1,2})(?:$|[-_])");
        if ((name.Contains("-TT-") || name.Contains("_TT_") || name.StartsWith("TT-") || name.Contains("STAGE")) && stage.Success)
        {
            var n = int.Parse(stage.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return (10 + n, $"הסדרי תנועה — שלב {n}");
        }
        return (1, "תכנון");
    }

    /// <summary>
    /// The section as stacked panels, one per stage the crossings came from (<see cref="StageOf"/>), each built on its own
    /// lines with the shared axis; the objects on the line (trees, poles, signs) stand in every panel. With no road line
    /// at all there is one panel that says so.
    /// </summary>
    /// <param name="designPaved">Whether the road is paved between two offsets (from the corridor at the station); only
    /// the panel <paramref name="pavedOrder"/> uses it - the other stages are not what that corridor built.</param>
    public static IReadOnlyList<Panel> BuildPanels(
        IEnumerable<Crossing> crossings, IEnumerable<PlacedObject>? objects, double halfWidthM,
        Func<double, double, bool>? designPaved = null, int pavedOrder = 1)
    {
        if (crossings == null) throw new ArgumentNullException(nameof(crossings));
        var all = crossings.ToList();
        var placed = (objects ?? Array.Empty<PlacedObject>()).ToList();
        var panels = all
            .Where(c => double.IsFinite(c.OffsetM) && Math.Abs(c.OffsetM) <= halfWidthM + 1e-9)
            .GroupBy(c => StageOf(c.Source))
            .Where(g => g.Any(c => SurfaceKinds.Contains(c.Kind) || c.Kind is Kind.Work or Kind.Fence or Kind.Barrier))
            .OrderBy(g => g.Key.Order)
            .Select(g => new Panel(g.Key.Order, g.Key.Title, Build(g, placed, halfWidthM, g.Key.Order == pavedOrder ? designPaved : null)))
            .ToList();
        if (panels.Count == 0) panels.Add(new Panel(1, "תכנון", Build(Array.Empty<Crossing>(), placed, halfWidthM)));
        return panels;
    }

    private static readonly HashSet<string> SurfaceKinds = new(StringComparer.Ordinal)
    {
        Kind.Edge, Kind.Shoulder, Kind.Curb, Kind.Sidewalk, Kind.Garden, Kind.Bike, Kind.Marking, Kind.EdgeLine, Kind.HovLine,
        Kind.Routing, Kind.Row, Kind.Track, Kind.Rail, Kind.Platform,
    };

    /// <summary>
    /// The section within +/-<paramref name="halfWidthM"/>: the width chain from the surface lines, each strip named by
    /// its two bounds (<see cref="NameOf"/>), the median as one group over its shoulders and barrier, the work zones
    /// between pairs of work-zone lines, fences, barriers and the objects on the line. Findings say in Hebrew what could
    /// not be read - a carriageway whose lanes the plan does not mark, a strip too wide for a lane.
    /// </summary>
    public static DraftedSection Build(
        IEnumerable<Crossing> crossings, IEnumerable<PlacedObject>? objects, double halfWidthM,
        Func<double, double, bool>? paved = null)
    {
        if (crossings == null) throw new ArgumentNullException(nameof(crossings));
        var findings = new List<string>();
        var within = crossings
            .Where(c => double.IsFinite(c.OffsetM) && Math.Abs(c.OffsetM) <= halfWidthM + 1e-9 && !string.IsNullOrEmpty(c.Kind))
            .ToList();

        // The axis is no line of the road: the office's chain runs straight through it (6403's "3.20" median). It is a
        // break only when the plan gives nothing else, so the section still has its centre.
        var breaks = new List<Crossing>();
        foreach (var line in Merge(within.Where(c => SurfaceKinds.Contains(c.Kind))).OrderBy(b => b.OffsetM))
        {
            // Two lines on one spot (6403 at 16.06: the carriageway edge and the shoulder line) are one break, read as
            // the one that says more.
            if (breaks.Count > 0 && line.OffsetM - breaks[^1].OffsetM < SameSpotM)
            {
                if (Priority(line.Kind) < Priority(breaks[^1].Kind)) breaks[^1] = line with { OffsetM = breaks[^1].OffsetM };
                continue;
            }
            breaks.Add(line);
        }
        // A painted line inside a bike path is its centre line, not a strip of the road (6403 ROAD-40 80150: the 1-1 line
        // at 21.85 between the bike edges 20.10 and 23.60; the office dimensions the path as one 3.50).
        for (var i = breaks.Count - 2; i >= 1; i--)
            if (breaks[i].Kind is Kind.Marking && breaks[i - 1].Kind == Kind.Bike && breaks[i + 1].Kind == Kind.Bike)
                breaks.RemoveAt(i);
        if (breaks.Count == 0)
        {
            findings.Add("קו החתך לא חוצה בתנוחה אף קו של כביש (שפת מיסעה, שול, אבן שפה, מדרכה, סימון) — נשאר רק הציר.");
            breaks.Add(new Crossing(0, "axis", "axis", "axis"));
        }
        Crossing At(double offset) => breaks.First(b => Math.Abs(b.OffsetM - offset) < 1e-9);

        var strips = new List<Strip>();
        for (var i = 0; i < breaks.Count - 1; i++)
        {
            var left = breaks[i];
            var right = breaks[i + 1];
            var width = right.OffsetM - left.OffsetM;
            if (width < 1e-6) continue;
            var isPaved = paved?.Invoke(left.OffsetM, right.OffsetM);
            strips.Add(new Strip(left.OffsetM, right.OffsetM, NameOf(left.Kind, right.Kind, width, isPaved), SurfaceOf(left.Kind, right.Kind)));
        }

        SplitAtRoadsideBarriers(strips, breaks, Merge(within.Where(c => c.Kind == Kind.Barrier)));

        static bool RoadLine(string kind) => kind is Kind.Edge or Kind.Marking or Kind.EdgeLine or Kind.HovLine or Kind.Routing or Kind.Curb or Kind.Shoulder;
        var groups = new List<Zone>();
        var barriers = Merge(within.Where(c => c.Kind == Kind.Barrier));
        bool InnerShoulder(Strip s, bool leftSide)
        {
            var outer = At(leftSide ? s.FromM : s.ToM);
            return s.WidthM >= 0.3 && s.WidthM <= 2.5 && outer.Kind is Kind.Edge or Kind.Shoulder or Kind.EdgeLine;
        }
        bool BarrierMedian(int i) =>
            i > 0 && i < strips.Count - 1 && strips[i].WidthM <= BarrierMaxM &&
            RoadLine(At(strips[i].FromM).Kind) && RoadLine(At(strips[i].ToM).Kind) &&
            InnerShoulder(strips[i - 1], true) && InnerShoulder(strips[i + 1], false);
        // The barrier need not straddle the axis (6403 ROAD-40: the axis 5 cm outside the barrier's left face).
        var barrierAt = Enumerable.Range(0, strips.Count)
            .Where(i => strips[i].FromM < BarrierMaxM && strips[i].ToM > -BarrierMaxM && BarrierMedian(i))
            .DefaultIfEmpty(-1).First();
        var centre = barrierAt >= 0 ? barrierAt : strips.FindIndex(s => s.FromM < MergeM && s.ToM > -MergeM);
        if (centre >= 0)
        {
            var mid = strips[centre];
            if (barrierAt >= 0)
            {
                // A barrier between two inner shoulders (6403 ROAD-40 79650: edge -1.70, barrier faces 0.05 / 0.85, edge
                // 2.55; the office's typical: 1.20 שול, 0.80, 1.20 שול under one "3.20 מפרדה").
                strips[centre - 1] = strips[centre - 1] with { Name = "שול" };
                strips[centre + 1] = strips[centre + 1] with { Name = "שול" };
                strips[centre] = mid with { Name = null };
                groups.Add(new Zone(strips[centre - 1].FromM, strips[centre + 1].ToM, "מפרדה"));
                if (!barriers.Any(b => b.OffsetM > mid.FromM && b.OffsetM < mid.ToM))
                    barriers.Add(new Crossing((mid.FromM + mid.ToM) / 2, Kind.Barrier, "median", "median"));
            }
            else if (RoadLine(At(mid.FromM).Kind) && RoadLine(At(mid.ToM).Kind) && mid.WidthM > MedianMaxM && mid.WidthM <= BarrierMedianMaxM &&
                     barriers.Any(b => b.OffsetM > mid.FromM && b.OffsetM < mid.ToM))
            {
                // A wide strip around the axis with a barrier in it: the median, its walls drawn as blocks (TR-M34).
                strips[centre] = mid with { Name = "מפרדה" };
            }
            else if (RoadLine(At(mid.FromM).Kind) && RoadLine(At(mid.ToM).Kind) && mid.WidthM >= MedianMinM && mid.WidthM <= MedianMaxM)
            {
                // A narrow strip the axis runs through, between two road lines, is the median (the office's 6403
                // example at 40-st-01-1 800500: "1.80 מפרדה").
                strips[centre] = mid with { Name = "מפרדה" };
            }
        }

        var zones = new List<Zone>();
        var work = Merge(within.Where(c => c.Kind == Kind.Work)).OrderBy(c => c.OffsetM).ToList();
        for (var i = 0; i + 1 < work.Count; i += 2)
            zones.Add(new Zone(work[i].OffsetM, work[i + 1].OffsetM, "תחום עבודה"));
        if (work.Count % 2 == 1)
            findings.Add("קו תחום העבודה נחצה מספר אי-זוגי של פעמים — תחום עבודה אחד לא נסגר בתוך רוחב החתך ולא סומן.");
        // A strip a work zone fills is the work zone (6403 stage 1 at ROAD-40 79650: 20.95 m between the routing lines,
        // the closed site between work lines at -7.66 and 12.10).
        for (var i = 0; i < strips.Count; i++)
        {
            var strip = strips[i];
            if (strip.Name != null) continue;
            var covered = zones.Sum(z => Math.Max(0, Math.Min(z.ToM, strip.ToM) - Math.Max(z.FromM, strip.FromM)));
            if (covered >= 0.7 * strip.WidthM) strips[i] = strip with { Name = "תחום עבודה" };
        }

        // What the plan does not prove is said, not guessed (2026-11-02: two carriageway edges 5.78 m apart had been
        // called a lane).
        foreach (var strip in strips.Where(s => s.Name is null or "מיסעה" && s.WidthM >= LaneMinM))
        {
            var left = At(strip.FromM);
            var right = At(strip.ToM);
            if (!RoadLine(left.Kind) || !RoadLine(right.Kind)) continue;
            var width = strip.WidthM.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var laneLines = left.Kind is Kind.Marking or Kind.Routing or Kind.EdgeLine or Kind.HovLine || right.Kind is Kind.Marking or Kind.Routing or Kind.EdgeLine or Kind.HovLine;
            findings.Add(laneLines
                ? $"רצועה של {width} מ' בין קווי נתיב — רחבה מנתיב (עד {LaneMaxM:0.0} מ'), כנראה חסר בתנוחה קו נתיב; לא סומנה כנתיב."
                : $"רצועה של {width} מ' בין שתי שפות מיסעה, בלי קווי נתיב בתנוחה — מספר הנתיבים בה לא ידוע ולא סומן.");
        }

        return new DraftedSection(
            breaks, strips, zones,
            Merge(within.Where(c => c.Kind == Kind.Fence)),
            barriers.OrderBy(b => b.OffsetM).ToList(),
            (objects ?? Array.Empty<PlacedObject>())
                .Where(o => double.IsFinite(o.OffsetM) && Math.Abs(o.OffsetM) <= halfWidthM + 1e-9)
                .OrderBy(o => o.OffsetM).ToList(),
            findings)
        { Groups = groups };
    }

    /// <summary>
    /// The stage the corridor at the station built: the panel most of whose lines stand where the corridor's links break
    /// (within 0.15 m). 6403 at 40-st-01-1 800500 the corridor is the temporary road of traffic stage 1; at ROAD-40 79650
    /// it is the design (its edges -16.01, -12.51, -1.70, 2.55, 12.56, 16.06, 16.66 are link ends of RD-40). Null when no
    /// panel has a third of its lines on the corridor.
    /// </summary>
    public static int? PanelBuiltBy(IReadOnlyList<Panel> panels, IReadOnlyList<double> corridorBreaksM)
    {
        int? best = null;
        var bestShare = 1.0 / 3.0;
        foreach (var panel in panels)
        {
            var lines = panel.Section.Breaks.Where(b => b.Kind != "axis").Select(b => b.OffsetM).ToList();
            if (lines.Count == 0) continue;
            var share = (double)lines.Count(x => corridorBreaksM.Any(c => Math.Abs(c - x) <= 0.15)) / lines.Count;
            if (share > bestShare) { bestShare = share; best = panel.Order; }
        }
        return best;
    }

    /// <summary>
    /// A roadside barrier splits the strip it stands in, outside the edge line (2026-11-09, GM-halufa_A_new2 road 43:
    /// 807 | 3.00 | W3 barrier | 1.30 | HW-SHLD). The designer dimensions the paved shoulder up to the barrier and the
    /// berm behind it; read as one 4.30 strip it had no name and a note that a lane line was probably missing. Only a
    /// strip that does not hold the axis (medians are <see cref="Build"/>'s), from the 807 edge line out to the shoulder
    /// line or the carriageway edge - or from the carriageway edge to the shoulder line - with exactly one barrier
    /// inside it. The barrier becomes a break of the width chain; the shoulder keeps the strip's surface, the berm is
    /// ground.
    /// </summary>
    private static void SplitAtRoadsideBarriers(List<Strip> strips, List<Crossing> breaks, IReadOnlyList<Crossing> barriers)
    {
        if (barriers.Count == 0) return;
        Crossing At(double offset) => breaks.First(b => Math.Abs(b.OffsetM - offset) < 1e-9);
        var added = false;
        for (var i = strips.Count - 1; i >= 0; i--)
        {
            var strip = strips[i];
            if (strip.FromM < -MergeM && strip.ToM > MergeM) continue;
            var leftSide = strip.ToM <= MergeM;
            var inner = At(leftSide ? strip.ToM : strip.FromM).Kind;
            var outer = At(leftSide ? strip.FromM : strip.ToM).Kind;
            var roadside = (inner == Kind.EdgeLine && outer is Kind.Shoulder or Kind.Edge) || (inner == Kind.Edge && outer == Kind.Shoulder);
            if (!roadside) continue;
            var inside = barriers
                .Where(b => b.OffsetM > strip.FromM + BarrierSplitMinM && b.OffsetM < strip.ToM - BarrierSplitMinM)
                .ToList();
            if (inside.Count != 1) continue;
            var at = inside[0].OffsetM;
            var shoulderWidth = leftSide ? strip.ToM - at : at - strip.FromM;
            var bermWidth = strip.WidthM - shoulderWidth;
            if (shoulderWidth > ShoulderMaxM || bermWidth > BermMaxM) continue;
            var shoulder = new Strip(leftSide ? at : strip.FromM, leftSide ? strip.ToM : at,
                shoulderWidth >= MinNamedWidthM ? "שול" : null, strip.SurfaceKind);
            var berm = new Strip(leftSide ? strip.FromM : at, leftSide ? at : strip.ToM,
                bermWidth >= MinNamedWidthM ? "כתף" : null, "ground");
            strips[i] = leftSide ? berm : shoulder;
            strips.Insert(i + 1, leftSide ? shoulder : berm);
            breaks.Add(inside[0]);
            added = true;
        }
        if (added) breaks.Sort((x, y) => x.OffsetM.CompareTo(y.OffsetM));
    }

    /// <summary>Which of two lines on one spot names the strips beside it: the most specific first.</summary>
    private static int Priority(string kind) => kind switch
    {
        Kind.Shoulder => 0,
        Kind.EdgeLine => 1,
        Kind.Curb => 2,
        Kind.HovLine => 3,
        Kind.Marking => 3,
        Kind.Routing => 4,
        Kind.Bike => 5,
        Kind.Sidewalk => 6,
        Kind.Garden => 7,
        Kind.Edge => 8,
        _ => 9,
    };

    /// <summary>
    /// A strip's name from its two bounds - only what they prove (2026-11-02, owner). A lane ("נתיב", "נתיב זמני" on
    /// temporary routing) needs a lane line (a white marking, the yellow 807 edge line or a routing line) on at least one
    /// side and <see cref="LaneMinM"/>-<see cref="LaneMaxM"/> of width; the strip outside a 807 line, up to the
    /// carriageway edge, is the shoulder. Two carriageway edges with no line between them are a carriageway ("מיסעה")
    /// only when the corridor says it is paved (<paramref name="paved"/>), and never a lane: the edges of two roads, or
    /// of an island, look the same on plan. Sidewalks, gardens and bike paths come from their own lines.
    /// </summary>
    public static string? NameOf(string leftKind, string rightKind, double widthM, bool? paved = null)
    {
        if (!double.IsFinite(widthM) || widthM < MinNamedWidthM) return null;
        bool Either(string kind) => leftKind == kind || rightKind == kind;
        bool Both(Func<string, bool> test) => test(leftKind) && test(rightKind);
        static bool LaneLine(string kind) => kind is Kind.Marking or Kind.EdgeLine or Kind.HovLine or Kind.Routing;
        static bool Outer(string kind) => kind is Kind.Edge or Kind.Curb or Kind.Shoulder;

        if (leftKind == Kind.Platform && rightKind == Kind.Platform) return "רציף";
        if (Either(Kind.Track) || Either(Kind.Rail) || Either(Kind.Platform)) return null;

        if (Either(Kind.Sidewalk) && !Either(Kind.Garden) && !Either(Kind.Bike)) return "מדרכה";
        if (Either(Kind.Garden)) return "גינון";
        // A bike path lies between its two lines, or between one and a curb (MAAKAF: 2.00 m); a narrow strip from its
        // line to a curb or an edge is the margin beside it (6403 ROAD-40 80160: the 1.00 from 23.60 to the curb 24.60).
        if (Either(Kind.Bike)) return Both(k => k == Kind.Bike) || widthM >= BikeMinM ? "שביל אופניים" : null;

        // Outside the yellow edge line, or on a shoulder line: the shoulder.
        if (Either(Kind.Shoulder) && Both(k => Outer(k) || LaneLine(k)))
            return widthM <= ShoulderMaxM ? "שול" : null;
        if (Either(Kind.EdgeLine) && (Outer(leftKind) || Outer(rightKind)))
            return widthM <= ShoulderMaxM ? "שול" : null;

        if ((LaneLine(leftKind) || LaneLine(rightKind)) && Both(k => LaneLine(k) || Outer(k)))
        {
            // Between 807 and a routing line the narrow side is the shoulder (6403 S1: 807 at -7.50, routing at -8.60
            // and -4.20: 1.10 shoulder, 3.30 lane).
            if (widthM < LaneMinM) return Either(Kind.EdgeLine) ? "שול" : null;
            if (widthM > LaneMaxM) return null;
            if (Either(Kind.Routing)) return "נתיב זמני";
            // The lane between the yellow 3-3 and the carriageway's edge line is the high-occupancy lane; the lane on the
            // 3-3's other side is an ordinary one (6403 ROAD-40 80150: 801 | 8.80 | 3-3 | 12.40 | 807 | 16.00).
            return Either(Kind.HovLine) && (Either(Kind.EdgeLine) || Outer(leftKind) || Outer(rightKind)) ? "נר\"ת" : "נתיב";
        }

        if (Both(Outer) && paved == true && widthM >= LaneMinM) return "מיסעה";
        return null;
    }

    /// <summary>How a strip is drawn: "road" (asphalt at grade), "raised" (sidewalk, garden, bike - above the curb) or "ground".</summary>
    public static string SurfaceOf(string leftKind, string rightKind)
    {
        bool Either(string kind) => leftKind == kind || rightKind == kind;
        if (Either(Kind.Sidewalk) || Either(Kind.Garden) || Either(Kind.Bike)) return "raised";
        static bool OnRoad(string kind) => kind is Kind.Edge or Kind.Marking or Kind.EdgeLine or Kind.HovLine or Kind.Shoulder or Kind.Routing or "axis";
        if (OnRoad(leftKind) && OnRoad(rightKind)) return "road";
        if (Either(Kind.Curb) && (Either(Kind.Edge) || Either(Kind.Shoulder) || Either(Kind.Marking) || Either(Kind.EdgeLine))) return "road";
        return "ground";
    }

    /// <summary>Same-kind crossings within <see cref="MergeM"/> of each other become one, at their middle.</summary>
    public static List<Crossing> Merge(IEnumerable<Crossing> crossings)
    {
        var result = new List<Crossing>();
        foreach (var group in crossings.GroupBy(c => c.Kind))
        {
            var cluster = new List<Crossing>();
            foreach (var c in group.OrderBy(c => c.OffsetM))
            {
                if (cluster.Count > 0 && c.OffsetM - cluster[^1].OffsetM >= MergeFor(c.Kind))
                {
                    result.Add(Middle(cluster));
                    cluster.Clear();
                }
                cluster.Add(c);
            }
            if (cluster.Count > 0) result.Add(Middle(cluster));
        }
        return result.OrderBy(c => c.OffsetM).ToList();

        // A painted line is drawn on plan as its stripe's two edges (6403's 803 at 1.50 / 1.70, the double yellow 3-3 at
        // 12.25 / 12.55): one line, at the stripe's centre.
        static double MergeFor(string kind) => kind is Kind.Marking or Kind.EdgeLine or Kind.HovLine ? StripeM : MergeM;
        static Crossing Middle(List<Crossing> cluster) =>
            cluster[0] with { OffsetM = (cluster[0].OffsetM + cluster[^1].OffsetM) / 2.0 };
    }
}
