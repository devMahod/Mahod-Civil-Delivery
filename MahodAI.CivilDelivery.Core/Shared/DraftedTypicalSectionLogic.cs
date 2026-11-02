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
        public const string Shoulder = "shoulder";
        public const string Curb = "curb";
        public const string Sidewalk = "sidewalk";
        public const string Garden = "garden";
        public const string Bike = "bike";
        public const string Marking = "marking";
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

    /// <summary>A strip narrower than this carries no name (a curb face, a marking width).</summary>
    public const double MinNamedWidthM = 1.0;

    /// <summary>A median the axis runs through is this wide (the office's temporary medians: 1.80 m).</summary>
    public const double MedianMinM = 0.6;

    /// <summary>Wider than this a strip around the axis is a carriageway, not a median.</summary>
    public const double MedianMaxM = 3.0;

    // Ordered: the first pattern that matches the layer leaf wins (a sidewalk CURB is a curb, not a sidewalk).
    private static readonly (string Pattern, string Kind)[] LayerKinds =
    {
        ("*-811*", ""), ("*-815*", ""), ("*-810*", ""), ("*-809*", ""), ("*ZEBRA*", ""), ("TR-MARK-ARW*", ""),
        ("*-TXT*", ""), ("*-TX-*", ""), ("*TEXT*", ""), ("*-DIM*", ""), ("*-NOTE*", ""), ("CL_*", ""),
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
        ("TR-MARK*", Kind.Marking), ("*MARK-WHT*", Kind.Marking), ("*MARK-YLW*", Kind.Marking),
        ("*PGVUL*", Kind.Row), ("ROW*", Kind.Row), ("ZCHUT*", Kind.Row),
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
    public static IReadOnlyList<Panel> BuildPanels(
        IEnumerable<Crossing> crossings, IEnumerable<PlacedObject>? objects, double halfWidthM)
    {
        if (crossings == null) throw new ArgumentNullException(nameof(crossings));
        var all = crossings.ToList();
        var placed = (objects ?? Array.Empty<PlacedObject>()).ToList();
        var panels = all
            .Where(c => double.IsFinite(c.OffsetM) && Math.Abs(c.OffsetM) <= halfWidthM + 1e-9)
            .GroupBy(c => StageOf(c.Source))
            .Where(g => g.Any(c => SurfaceKinds.Contains(c.Kind) || c.Kind is Kind.Work or Kind.Fence or Kind.Barrier))
            .OrderBy(g => g.Key.Order)
            .Select(g => new Panel(g.Key.Order, g.Key.Title, Build(g, placed, halfWidthM)))
            .ToList();
        if (panels.Count == 0) panels.Add(new Panel(1, "תכנון", Build(Array.Empty<Crossing>(), placed, halfWidthM)));
        return panels;
    }

    private static readonly HashSet<string> SurfaceKinds = new(StringComparer.Ordinal)
    {
        Kind.Edge, Kind.Shoulder, Kind.Curb, Kind.Sidewalk, Kind.Garden, Kind.Bike, Kind.Marking, Kind.Routing, Kind.Row,
    };

    /// <summary>
    /// The section within ±<paramref name="halfWidthM"/>: the width chain from the surface lines (and the axis at 0),
    /// each strip named by its two bounds, the work zones between pairs of work-zone lines, fences, barriers and the
    /// objects on the line. Findings say in Hebrew what could not be read.
    /// </summary>
    public static DraftedSection Build(
        IEnumerable<Crossing> crossings, IEnumerable<PlacedObject>? objects, double halfWidthM)
    {
        if (crossings == null) throw new ArgumentNullException(nameof(crossings));
        var findings = new List<string>();
        var within = crossings
            .Where(c => double.IsFinite(c.OffsetM) && Math.Abs(c.OffsetM) <= halfWidthM + 1e-9 && !string.IsNullOrEmpty(c.Kind))
            .ToList();

        var surface = Merge(within.Where(c => SurfaceKinds.Contains(c.Kind)));
        if (surface.Count == 0)
            findings.Add("קו החתך לא חוצה בתנוחה אף קו של כביש (שפת מיסעה, שול, אבן שפה, מדרכה, סימון) — נשאר רק הציר.");

        // The axis is a break of its own: the lanes are measured from it, as the office's sections do.
        var breaks = surface.ToList();
        if (!breaks.Any(b => Math.Abs(b.OffsetM) < MergeM))
            breaks.Add(new Crossing(0, "axis", "axis", "axis"));
        breaks = breaks.OrderBy(b => b.OffsetM).ToList();

        var strips = new List<Strip>();
        for (var i = 0; i < breaks.Count - 1; i++)
        {
            var left = breaks[i];
            var right = breaks[i + 1];
            var width = right.OffsetM - left.OffsetM;
            if (width < 1e-6) continue;
            strips.Add(new Strip(left.OffsetM, right.OffsetM, NameOf(left.Kind, right.Kind, width), SurfaceOf(left.Kind, right.Kind)));
        }

        // A narrow strip the axis runs through, between two road lines, is the median (the office's 6403 example at
        // 40-st-01-1 800500: "1.80 מפרדה" where the axis split it into two unnamed .90): one strip, one name.
        var axisIndex = breaks.FindIndex(b => b.Kind == "axis");
        if (axisIndex > 0 && axisIndex < breaks.Count - 1)
        {
            var left = breaks[axisIndex - 1];
            var right = breaks[axisIndex + 1];
            var width = right.OffsetM - left.OffsetM;
            static bool RoadLine(string kind) => kind is Kind.Edge or Kind.Marking or Kind.Routing or Kind.Curb;
            if (RoadLine(left.Kind) && RoadLine(right.Kind) && width >= MedianMinM && width <= MedianMaxM)
            {
                var at = strips.FindIndex(s => Math.Abs(s.FromM - left.OffsetM) < 1e-9);
                if (at >= 0 && at + 1 < strips.Count)
                {
                    strips.RemoveRange(at, 2);
                    strips.Insert(at, new Strip(left.OffsetM, right.OffsetM, "מפרדה", "road"));
                }
            }
        }

        var zones = new List<Zone>();
        var work = Merge(within.Where(c => c.Kind == Kind.Work)).OrderBy(c => c.OffsetM).ToList();
        for (var i = 0; i + 1 < work.Count; i += 2)
            zones.Add(new Zone(work[i].OffsetM, work[i + 1].OffsetM, "תחום עבודה"));
        if (work.Count % 2 == 1)
            findings.Add("קו תחום העבודה נחצה מספר אי-זוגי של פעמים — תחום עבודה אחד לא נסגר בתוך רוחב החתך ולא סומן.");

        return new DraftedSection(
            breaks, strips, zones,
            Merge(within.Where(c => c.Kind == Kind.Fence)),
            Merge(within.Where(c => c.Kind == Kind.Barrier)),
            (objects ?? Array.Empty<PlacedObject>())
                .Where(o => double.IsFinite(o.OffsetM) && Math.Abs(o.OffsetM) <= halfWidthM + 1e-9)
                .OrderBy(o => o.OffsetM).ToList(),
            findings);
    }

    /// <summary>
    /// A strip's name from its two bounds. Conservative like the plan-strip table: between road lines a 2.5–6.5 m strip
    /// is a lane and a wider one a carriageway; a strip touching a sidewalk, garden or bike line is that; anything the
    /// bounds do not settle stays unnamed.
    /// </summary>
    public static string? NameOf(string leftKind, string rightKind, double widthM)
    {
        if (!double.IsFinite(widthM) || widthM < MinNamedWidthM) return null;
        bool Either(string kind) => leftKind == kind || rightKind == kind;
        bool Road(string kind) => kind is Kind.Edge or Kind.Marking or "axis";

        if (Either(Kind.Sidewalk) && !Either(Kind.Garden) && !Either(Kind.Bike)) return "מדרכה";
        if (Either(Kind.Garden)) return "גינון";
        if (Either(Kind.Bike)) return "שביל אופניים";
        if (Either(Kind.Shoulder) && (Road(leftKind) || Road(rightKind) || Either(Kind.Shoulder))) return "שול";
        if (Either(Kind.Routing) && (Road(leftKind) || Road(rightKind) || Either(Kind.Routing)))
            return widthM is >= 2.5 and <= 6.5 ? "נתיב זמני" : null;
        if (Road(leftKind) && Road(rightKind))
            return widthM is >= 2.5 and <= 6.5 ? "נתיב נסיעה" : widthM > 6.5 && widthM <= 30 ? "מיסעה" : null;
        return null;
    }

    /// <summary>How a strip is drawn: "road" (asphalt at grade), "raised" (sidewalk, garden, bike — above the curb) or "ground".</summary>
    public static string SurfaceOf(string leftKind, string rightKind)
    {
        bool Either(string kind) => leftKind == kind || rightKind == kind;
        if (Either(Kind.Sidewalk) || Either(Kind.Garden) || Either(Kind.Bike)) return "raised";
        if (leftKind is Kind.Edge or Kind.Marking or Kind.Shoulder or Kind.Routing or "axis" &&
            rightKind is Kind.Edge or Kind.Marking or Kind.Shoulder or Kind.Routing or "axis") return "road";
        if (Either(Kind.Curb) && (Either(Kind.Edge) || Either(Kind.Shoulder) || Either(Kind.Marking))) return "road";
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
                if (cluster.Count > 0 && c.OffsetM - cluster[^1].OffsetM >= MergeM)
                {
                    result.Add(Middle(cluster));
                    cluster.Clear();
                }
                cluster.Add(c);
            }
            if (cluster.Count > 0) result.Add(Middle(cluster));
        }
        return result.OrderBy(c => c.OffsetM).ToList();

        static Crossing Middle(List<Crossing> cluster) =>
            cluster[0] with { OffsetM = (cluster[0].OffsetM + cluster[^1].OffsetM) / 2.0 };
    }
}
