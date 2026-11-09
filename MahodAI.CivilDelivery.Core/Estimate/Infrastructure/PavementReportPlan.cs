using System.Globalization;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// From the drawing's pavement reports to BOQ layers: which report structure applies to each surface kind in each
/// segment, and how its printed layers become priced rows (asphalt per m² of its thickness, bases per m³, tack and prime
/// coats per asphalt interface). Every thickness is the report's own; nothing is defaulted.
/// </summary>
public static class PavementReportPlan
{
    /// <summary>A zone of the drawing a report speaks about: one segment of its table, or — for a report without a
    /// segment table — the whole drawing (<see cref="Segment"/> null).</summary>
    public sealed record Zone(int Report, PavementReportSegment? Segment)
    {
        /// <summary>Plan area that no segment of any attached report covers.</summary>
        public static Zone Outside { get; } = new(-1, null);
        /// <summary>The whole drawing (or marked area) when no report is attached.</summary>
        public static Zone NoReport { get; } = new(-2, null);
        public string Label => Report == -2 ? "כל השטח" : Report < 0 ? "מחוץ למקטעי הדו״ח" : Segment is { } s
            ? "מקטע " + (s.Segment ?? "?") + (s.FromSection != null || s.ToSection != null ? $" ({s.FromSection}–{s.ToSection})" : "")
            : "כל השרטוט";
    }

    /// <summary>The new area of one surface kind inside one zone.</summary>
    public sealed record ZoneArea(Zone Zone, RoadSurfaceKind Surface, double AreaSquareMetres);

    /// <summary>Where a priced structure came from. A default is never the project's design — it is always said so.</summary>
    public enum Origin { None, Report, Manual, Default }

    /// <summary>How every defaults note starts — the screen keeps it even when it shortens the other notes.</summary>
    public const string DefaultsNote = "ערכי ברירת מחדל";

    /// <summary>A line of the zones table (screen and Excel): where a structure applies, and on how much area.</summary>
    public sealed record ZoneRow(string Zone, string Road, string SurfaceName, double AreaSquareMetres, string Structure, string Layers)
    {
        public Origin Origin { get; init; } = Origin.Report;
        public bool Priced => Layers.Length > 0;
        public bool IsDefault => Origin == Origin.Default;
        public string OriginLabel => Origin switch
        {
            Origin.Report => "דו״ח מבנה", Origin.Manual => "ידני", Origin.Default => "ברירת מחדל", _ => "לא תומחר",
        };
    }

    /// <summary>The structure rows, the zones table and its notes. <see cref="DefaultArea"/> is the m² priced by default
    /// values (no report structure and no hand-typed one) — the screen and the Excel say so whenever it is above zero.</summary>
    public sealed record Result(IReadOnlyList<RoadPavement.LayerQuantity> Layers, IReadOnlyList<ZoneRow> Zones,
        IReadOnlySet<RoadSurfaceKind> Priced, IReadOnlyList<string> Notes)
    {
        public double DefaultArea { get; init; }
        public PavementDefaultClass? Defaults { get; init; }
        public double UnpricedArea => Zones.Where(z => !z.Priced).Sum(z => z.AreaSquareMetres);
    }

    private static readonly Dictionary<RoadSurfaceKind, string[]> UsesBySurface = new()
    {
        [RoadSurfaceKind.Road] = new[] { "road", "road_and_bus_lane" },
        [RoadSurfaceKind.PublicTransport] = new[] { "bus_lane", "road_and_bus_lane", "road" },
        [RoadSurfaceKind.Sidewalk] = new[] { "sidewalk" },
        [RoadSurfaceKind.BikePath] = new[] { "bike_path" },
        [RoadSurfaceKind.Shoulder] = new[] { "shoulder" },
        [RoadSurfaceKind.Island] = new[] { "island" },
        [RoadSurfaceKind.Milling] = new[] { "existing_road_overlay" },
    };
    /// <summary>Surfaces whose structure depends on the segment's structure family (the road's traffic).</summary>
    private static bool ByFamily(RoadSurfaceKind s) => s is RoadSurfaceKind.Road or RoadSurfaceKind.PublicTransport or RoadSurfaceKind.Milling;

    /// <summary>Road structure families the report declares (for the variant choice and the whole-drawing fallback).</summary>
    public static IReadOnlyList<string> RoadFamilies(PavementReportContent report) => report.Structures
        .Where(s => s.Use is "road" or "road_and_bus_lane" or "bus_lane").Select(s => s.Family).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The variants a road family offers ("full", "widening"…), in report order.</summary>
    public static IReadOnlyList<PavementReportStructure> Variants(PavementReportContent report, string family) => report.Structures
        .Where(s => s.Family == family && s.Use is "road" or "road_and_bus_lane" or "bus_lane").ToArray();

    /// <summary>
    /// The report structure for <paramref name="surface"/> in <paramref name="zone"/>, or null when the report has none.
    /// Road-traffic surfaces follow the segment's family (the bus lane its own family when the table gives one); a report
    /// without segments uses its only road family, or the one the engineer chose.
    /// </summary>
    public static PavementReportStructure? StructureFor(PavementReportSet set, Zone zone, RoadSurfaceKind surface)
    {
        if (zone.Report < 0 || zone.Report >= set.Reports.Count || !UsesBySurface.TryGetValue(surface, out var uses)) return null;
        var report = set.Reports[zone.Report].Content;
        IEnumerable<PavementReportStructure> candidates = report.Structures.Where(s => uses.Contains(s.Use));
        if (ByFamily(surface))
        {
            var family = zone.Segment is { } segment
                ? surface == RoadSurfaceKind.PublicTransport ? segment.BusLaneFamily ?? segment.RoadFamily : segment.RoadFamily
                : RoadFamilies(report) is { Count: 1 } only ? only[0] : set.FallbackFamily;
            if (family == null) return null;
            candidates = candidates.Where(s => s.Family == family);
            var variant = set.VariantOf(zone.Report, family);
            return candidates.OrderBy(s => Array.IndexOf(uses, s.Use)).ThenBy(s => s.Variant == variant ? 0 : s.Variant == "full" ? 1 : 2).FirstOrDefault();
        }
        return candidates.OrderBy(s => s.Variant is "full" or "minimum" ? 0 : 1).FirstOrDefault();
    }

    /// <summary>The structure's layers as BOQ layers, top to bottom, plus its sprays.</summary>
    public static IReadOnlyList<PavementLayer> Layers(PavementReportStructure structure, PavementReportCoats? coats = null)
    {
        var rows = new List<PavementLayer>();
        var asphalt = structure.Layers.Count(l => l.Kind == "asphalt");
        var overlay = structure.Variant == "overlay" || structure.Use == "existing_road_overlay";
        if (overlay)
        {
            // Milling to the depth the report states, else to the depth of the layers laid back.
            var depth = structure.MillingCm ?? structure.Layers.Where(l => l.Kind == "asphalt").Sum(l => l.ThicknessCm);
            rows.Add(new PavementLayer("קרצוף אספלט", depth, "מ״ר", MillingHint(depth)));
        }
        foreach (var layer in structure.Layers)
        {
            var row = Layer(layer);
            // Identical layers (two 5 cm תא"צ 19 PG70-10 courses) are one priced item laid twice.
            var same = rows.FindIndex(r => r.Name == row.Name && r.ThicknessCm == row.ThicknessCm && r.Unit == row.Unit && r.SearchHint == row.SearchHint);
            if (same >= 0) rows[same] = rows[same] with { Times = rows[same].Times + 1 };
            else rows.Add(row);
        }
        if (asphalt > 0)
        {
            // Under every asphalt layer a spray (report §"ריסוסים", NTI 51.04): tack between new asphalt layers (51.04.1470,
            // 0.25-0.5 kg/m²), prime under the lowest one (51.04.1480, 0.8-1.2) — on a milled road the old asphalt below takes
            // tack too, its rate left to the engineer.
            var tack = overlay ? asphalt : asphalt - 1;
            if (tack > 0 && coats?.TackBetweenAsphaltLayers != false)
                rows.Add(new PavementLayer("ריסוס ציפוי מאחה", 0, "מ״ר", overlay ? "ריסוס ציפוי מאחה" : "ריסוס ציפוי מאחה 0.25") { Times = tack });
            if (!overlay && coats?.PrimeOnBase != false)
                rows.Add(new PavementLayer("ריסוס ציפוי יסוד", 0, "מ״ר", "ריסוס ציפוי יסוד 1.2"));
        }
        return rows;
    }

    /// <summary>NTI pays milling by depth band (51.04.1520 up to 2 cm … 51.04.1564 30.1–40 cm): the hint names the band.</summary>
    public static string MillingHint(double depthCm)
    {
        var bands = new (double To, string Text)[] { (2, "עד 2.0"), (4, "2.1 - 4.0"), (8, "4.1 - 8.0"), (10, "8.1 - 10.0"), (12, "10.1 - 12.0"),
            (15, "12.1 - 15.0"), (20, "15.1 - 20.0"), (30, "20.1 - 30.0"), (40, "30.1 - 40.0") };
        var band = bands.FirstOrDefault(b => depthCm <= b.To + 1e-9);
        return band.Text == null ? "קרצוף אספלט" : "קרצוף אספלט לעומק " + band.Text;
    }

    /// <summary>One printed layer as a BOQ layer: its name, how NTI pays it, and the words that find its item.</summary>
    public static PavementLayer Layer(PavementReportLayer l)
    {
        static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var material = Regex.Replace(l.Material ?? "", @"\s+", " ").Trim();
        switch (l.Kind)
        {
            case "asphalt":
            {
                var binder = l.Binder is { Length: > 0 } b ? Regex.Replace(b, @"\s+", "").ToUpperInvariant() : null;
                var stone = AggregateWord(l.AggregateType ?? material);
                if (l.AggregateMm is { } mm)
                {
                    var name = $"תא״צ {N(mm)}" + (binder != null ? " " + binder : "");
                    var hint = $"תא\"צ {N(mm)} בעובי {N(l.ThicknessCm)}" + (binder != null ? " " + binder : "") + (stone != null ? " " + stone : "");
                    return new PavementLayer(name, l.ThicknessCm, "מ״ר", hint);
                }
                return new PavementLayer(Short(material), l.ThicknessCm, "מ״ר", "");
            }
            case "base":
            case "subbase":
            {
                var grade = l.BaseGrade switch { "A" => "א", "B" => "ב", "C" => "ג", _ => null };
                return grade != null
                    ? new PavementLayer($"מצע סוג {grade}׳", l.ThicknessCm, "מ״ק", $"מצע סוג {grade}")
                    : new PavementLayer(Short(material), l.ThicknessCm, "מ״ק", "מצע");
            }
            case "sand": return new PavementLayer("שכבת חול", l.ThicknessCm, "מ״ק", "חול");
            case "paving":
                return new PavementLayer(Short(material.Length > 0 ? material : "ריצוף"), l.ThicknessCm, "מ״ר",
                    Regex.IsMatch(material, "אספלט") ? "" : $"אבן משתלבת {N(l.ThicknessCm)}");
            case "concrete": return new PavementLayer(Short(material), l.ThicknessCm, "מ״ק", "בטון");
            case "fill": return new PavementLayer(Short(material), l.ThicknessCm, "מ״ק", "מילוי");
            case "geotextile": return new PavementLayer(Short(material), 0, "מ״ר", "יריעה");
            default: return new PavementLayer(Short(material), l.ThicknessCm, "מ״ק", "");
        }
    }

    private static string? AggregateWord(string text) =>
        Regex.IsMatch(text, "בזלת") ? "בזלתי" : Regex.IsMatch(text, "גיר|דולומיט") ? "גירי" : null;
    private static string Short(string text) => text.Length <= 48 ? text : text[..48].TrimEnd() + "…";

    /// <summary>
    /// The structure layers over the zones' areas. One row per (surface, structure, layer) summed over every zone that uses
    /// the same structure; the zones table keeps where each one applies. For each area, in order: the report's structure;
    /// else the structure the engineer typed in for that surface (<paramref name="manual"/>); else the default structure
    /// of the chosen road type (<paramref name="defaults"/>, always marked as a default); else it is listed and not priced.
    /// </summary>
    public static Result Quantities(PavementReportSet set, IEnumerable<ZoneArea> areas,
        IReadOnlyList<PavementStructure>? manual = null, PavementDefaultClass? defaults = null)
    {
        var typed = (manual ?? Array.Empty<PavementStructure>()).Where(s => RoadPavement.Usable(s.Layers).Any())
            .GroupBy(s => s.Surface).ToDictionary(g => g.Key, g => RoadPavement.Usable(g.Last().Layers).ToArray());
        var zones = new List<ZoneRow>();
        // Key: surface, origin, report index (−1 for manual / default), structure id.
        var used = new Dictionary<(RoadSurfaceKind Surface, Origin Origin, int Report, string Structure), (IReadOnlyList<PavementLayer> Layers, string Name, double Area)>();
        var areas0 = new List<ZoneArea>();
        foreach (var a in areas.Where(a => a.AreaSquareMetres > 0))
        {
            var structure = StructureFor(set, a.Zone, a.Surface);
            IReadOnlyList<PavementLayer>? layers = null; string name, display; Origin origin;
            if (structure != null)
            {
                origin = Origin.Report; name = Name(set, a.Zone.Report, structure); display = structure.LayersDisplay;
                layers = RoadPavement.Usable(Layers(structure, set.Reports[a.Zone.Report].Content.Coats)).ToArray();
            }
            else if (typed.TryGetValue(a.Surface, out var mine))
            {
                origin = Origin.Manual; name = "מבנה ידני"; layers = mine;
                display = string.Join(" + ", mine.Select(l => l.Display));
            }
            else if (defaults?.StructureFor(a.Surface) is { } d)
            {
                origin = Origin.Default; name = defaults.StructureName; display = d.LayersDisplay;
                layers = RoadPavement.Usable(Layers(d, PavementDefaults.Coats)).ToArray();
                structure = d;
            }
            else
            {
                // A surface that is not a pavement (an island, a structure area) keeps its own area row when nothing gives it
                // a structure: it is priced, just not here.
                if (!RoadPavement.NeedsStructure.Contains(a.Surface)) continue;
                origin = Origin.None; name = set.HasReport ? "אין מבנה בדו״ח" : "ללא דו״ח מבנה"; display = "";
            }
            areas0.Add(a);
            var road = a.Zone.Segment?.RoadName ?? "";
            zones.Add(new ZoneRow(a.Zone.Label + (set.Reports.Count > 1 && a.Zone.Report >= 0 ? $" · דו״ח {a.Zone.Report + 1}" : ""), road,
                FirmLayerConventions.SurfaceName(a.Surface), a.AreaSquareMetres, name, display) { Origin = origin });
            if (layers == null) continue;
            var key = (a.Surface, origin, origin == Origin.Report ? a.Zone.Report : -1, structure?.Id ?? "manual");
            used[key] = used.TryGetValue(key, out var u) ? u with { Area = u.Area + a.AreaSquareMetres } : (layers, name, a.AreaSquareMetres);
        }
        var quantities = used.OrderBy(u => (int)u.Key.Surface).ThenBy(u => (int)u.Key.Origin).ThenBy(u => u.Key.Report).ThenBy(u => u.Key.Structure, StringComparer.Ordinal)
            .SelectMany(u => u.Value.Layers.Select(l => new RoadPavement.LayerQuantity(u.Key.Surface, l, u.Value.Area, u.Value.Name) { Origin = u.Key.Origin }))
            .ToArray();
        var priced = used.Keys.Select(k => k.Surface).ToHashSet();
        // Table order: report, segment number, surface; the area outside every segment last.
        var ordered = zones.Select((z, i) => (z, a: areas0[i]))
            .OrderBy(x => x.a.Zone.Report < 0 ? int.MaxValue : x.a.Zone.Report)
            .ThenBy(x => int.TryParse(x.a.Zone.Segment?.Segment, out var n) ? n : int.MaxValue).ThenBy(x => x.a.Zone.Segment?.Segment, StringComparer.Ordinal)
            .ThenBy(x => (int)x.a.Surface).Select(x => x.z).ToArray();
        var notes = new List<string>();
        var defaultRows = ordered.Where(z => z.IsDefault).ToArray();
        if (defaultRows.Length > 0 && defaults != null)
            notes.Add($"{DefaultsNote} — {defaults.Name}: " +
                      string.Join(", ", defaultRows.GroupBy(z => z.SurfaceName).Select(g => $"{g.Key} {g.Sum(z => z.AreaSquareMetres):N0} מ״ר")) +
                      (set.HasReport ? " — שטחים שאין להם מבנה בדו״ח" : " — לא צורף דו״ח מבנה") +
                      ". אלה מבנים טיפוסיים ולא תכנון הפרויקט; יש לאמת מול דו״ח יועץ המיסעות לפני הגשה. מקור: " + defaults.Source);
        var outside = PavementReportPlan.Zone.Outside.Label;
        foreach (var g in zones.Where(z => !z.Priced && z.Zone == outside).GroupBy(z => z.SurfaceName))
            notes.Add($"{g.Key}: {g.Sum(z => z.AreaSquareMetres):N0} מ״ר מחוץ למקטעי הדו״ח (שרטוט המחולק בין דו״חות — צרף את הדו״ח השני) — לא תומחרו לפי מבנה");
        foreach (var g in zones.Where(z => !z.Priced && z.Zone != outside).GroupBy(z => z.SurfaceName))
            notes.Add(set.HasReport
                ? $"{g.Key}: {g.Sum(z => z.AreaSquareMetres):N0} מ״ר — לדו״ח אין מבנה ל{g.Key} — לא תומחרו לפי מבנה"
                : $"{g.Key}: {g.Sum(z => z.AreaSquareMetres):N0} מ״ר — לא צורף דו״ח מבנה ולא נבחרו ערכי ברירת מחדל — לא תומחרו");
        return new Result(quantities, ordered, priced, notes) { DefaultArea = defaultRows.Sum(z => z.AreaSquareMetres), Defaults = defaultRows.Length > 0 ? defaults : null };
    }

    private static string Name(PavementReportSet set, int report, PavementReportStructure s) =>
        s.ShortName + (set.Reports.Count > 1 ? $" (דו״ח {report + 1})" : "");
}
