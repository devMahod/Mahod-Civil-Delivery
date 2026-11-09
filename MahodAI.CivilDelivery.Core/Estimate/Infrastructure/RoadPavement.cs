using System.Globalization;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>One layer of a pavement structure. Asphalt is paid per m² of a stated thickness; bases and fills per m³.</summary>
public sealed record PavementLayer(string Name, double ThicknessCm, string Unit, string SearchHint)
{
    /// <summary>How many times the layer is laid: two identical 5 cm wearing courses are one row ×2, a tack coat under
    /// every asphalt layer but the lowest is one row × (layers − 1).</summary>
    public int Times { get; init; } = 1;
    public bool ByVolume => Units.Parse(Unit).Dimension == UnitDimension.Volume;
    public string Display => (ThicknessCm > 0 ? $"{Name} {ThicknessCm.ToString("0.#", CultureInfo.InvariantCulture)} ס״מ" : Name) + (Times > 1 ? $" ×{Times}" : "");
}

/// <summary>The layers under one surface kind (מיסעה, מדרכה, שביל אופניים…) that the engineer typed in by hand.</summary>
public sealed record PavementStructure(RoadSurfaceKind Surface, IReadOnlyList<PavementLayer> Layers);

/// <summary>
/// Road area and volume from the plan: the area of each surface kind (hatches / closed outlines on road layers) times
/// the thickness of each structure layer. The structure comes from the drawing's pavement report
/// (<see cref="PavementReportPlan"/>), else from layers the engineer typed in, else from the default structures of the
/// chosen road type (<see cref="PavementDefaults"/>), which every row, note and export marks as defaults. With defaults
/// switched off a road surface without a structure is not priced and says so.
/// </summary>
public static class RoadPavement
{
    public const string DerivedSource = "מבנה מיסעה — חישוב שטח × עובי";
    public const string DerivedPart = "מבנה";

    /// <summary>Surfaces whose price IS their pavement structure: without a structure they are not priced at all.</summary>
    public static readonly IReadOnlySet<RoadSurfaceKind> NeedsStructure = new HashSet<RoadSurfaceKind>
    { RoadSurfaceKind.Road, RoadSurfaceKind.PublicTransport, RoadSurfaceKind.Shoulder, RoadSurfaceKind.BikePath, RoadSurfaceKind.Sidewalk, RoadSurfaceKind.Milling };

    public sealed record SurfaceTotal(RoadSurfaceKind Surface, InfrastructureDisposition Disposition, double AreaSquareMetres,
        IReadOnlyList<InfrastructureGroup> Sources)
    {
        public string SurfaceName => FirmLayerConventions.SurfaceName(Surface);
        public string DispositionLabel => InfrastructureCondition.Label(Disposition);
        public string LayersDisplay => string.Join(", ", Sources.Select(s => s.LayerName).Distinct());
    }

    /// <summary>One structure layer over an area. <paramref name="Structure"/> names the report structure it came from
    /// ("" for a structure typed in by hand).</summary>
    public sealed record LayerQuantity(RoadSurfaceKind Surface, PavementLayer Layer, double AreaSquareMetres, string Structure = "")
    {
        /// <summary>Report, hand-typed or a road type's default structure.</summary>
        public PavementReportPlan.Origin Origin { get; init; } = PavementReportPlan.Origin.Manual;
        public bool IsDefault => Origin == PavementReportPlan.Origin.Default;
        public double VolumeCubicMetres => AreaSquareMetres * Layer.ThicknessCm / 100d * Layer.Times;
        public double PaidArea => AreaSquareMetres * Layer.Times;
        public double Quantity => Layer.ByVolume ? VolumeCubicMetres : PaidArea;
        public string SurfaceName => FirmLayerConventions.SurfaceName(Surface);
        public string LayerDisplay => Layer.Display;
        public string StructureName => Structure.Length > 0 ? Structure : "מבנה ידני";
        public string Unit => Layer.Unit;
    }

    /// <summary>What the roads sheet shows: plan areas, the structure built on new surfaces, corridor volumes, and —
    /// with a report — where each structure applies.</summary>
    public sealed record Report(IReadOnlyList<SurfaceTotal> Surfaces, IReadOnlyList<LayerQuantity> Layers, IReadOnlyList<InfrastructureGroup> Corridor)
    {
        public IReadOnlyList<PavementReportPlan.ZoneRow> Zones { get; init; } = Array.Empty<PavementReportPlan.ZoneRow>();
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>No structure until the report (or the engineer) gives one.</summary>
    public static IReadOnlyList<PavementStructure> Defaults { get; } = Enum.GetValues<RoadSurfaceKind>()
        .Where(k => k != RoadSurfaceKind.None).Select(k => new PavementStructure(k, Array.Empty<PavementLayer>())).ToArray();

    /// <summary>Plan area per surface kind and disposition. Excluded groups and groups without a surface kind do not count.</summary>
    public static IReadOnlyList<SurfaceTotal> Surfaces(IEnumerable<InfrastructureGroup> groups, ISet<InfrastructureGroup>? excluded = null) => groups
        .Where(g => g.Surface != RoadSurfaceKind.None && g.Source != DerivedSource && g.AreaSquareMetres > 0 && excluded?.Contains(g) != true)
        .GroupBy(g => (g.Surface, g.Disposition))
        .OrderBy(g => (int)g.Key.Surface).ThenBy(g => (int)g.Key.Disposition)
        .Select(g => new SurfaceTotal(g.Key.Surface, g.Key.Disposition, g.Sum(x => x.AreaSquareMetres), g.ToArray()))
        .ToArray();

    /// <summary>The hand-typed structure quantities of NEW surfaces, skipping <paramref name="skip"/> (surfaces the report prices).</summary>
    public static IReadOnlyList<LayerQuantity> Structure(IReadOnlyList<SurfaceTotal> surfaces, IReadOnlyList<PavementStructure> structures,
        IReadOnlySet<RoadSurfaceKind>? skip = null)
    {
        var bySurface = structures.GroupBy(s => s.Surface).ToDictionary(s => s.Key, s => s.Last());
        return surfaces.Where(s => s.Disposition == InfrastructureDisposition.New && s.AreaSquareMetres > 0 && skip?.Contains(s.Surface) != true)
            .GroupBy(s => s.Surface)
            .SelectMany(s => Usable(bySurface.TryGetValue(s.Key, out var structure) ? structure.Layers : Array.Empty<PavementLayer>())
                .Select(l => new LayerQuantity(s.Key, l, s.Sum(x => x.AreaSquareMetres))))
            .ToArray();
    }

    internal static IEnumerable<PavementLayer> Usable(IEnumerable<PavementLayer> layers) =>
        layers.Where(l => l.Name.Trim().Length > 0 && double.IsFinite(l.ThicknessCm) && l.ThicknessCm >= 0 && l.Times >= 1 && (!l.ByVolume || l.ThicknessCm > 0));

    /// <summary>Surface kinds a hand-typed structure prices.</summary>
    public static IReadOnlySet<RoadSurfaceKind> ManuallyPriced(IReadOnlyList<PavementStructure> structures) =>
        structures.Where(s => Usable(s.Layers).Any()).Select(s => s.Surface).ToHashSet();

    /// <summary>
    /// Marks the new road areas whose surface has a structure: those are priced by the structure rows and are not
    /// pay items themselves (see <see cref="InfrastructureGroup.PricedByStructure"/>). A new area whose surface needs a
    /// structure and has none waits for the report (<see cref="InfrastructureGroup.AwaitingPavementReport"/>) instead of
    /// being priced as a bare area. Returns how many areas a structure prices.
    /// </summary>
    public static int MarkPricedByStructure(IEnumerable<InfrastructureGroup> groups, IReadOnlySet<RoadSurfaceKind> priced)
    {
        var marked = 0;
        foreach (var g in groups.Where(g => g.Source != DerivedSource))
        {
            var newArea = g.Surface != RoadSurfaceKind.None && g.Disposition == InfrastructureDisposition.New && g.AreaSquareMetres > 0;
            g.PricedByStructure = newArea && priced.Contains(g.Surface);
            g.AwaitingPavementReport = newArea && !g.PricedByStructure && NeedsStructure.Contains(g.Surface);
            if (g.PricedByStructure) marked++;
        }
        return marked;
    }

    /// <summary>Compatibility: marks by hand-typed structures only.</summary>
    public static int MarkPricedByStructure(IEnumerable<InfrastructureGroup> groups, IReadOnlyList<PavementStructure> structures) =>
        MarkPricedByStructure(groups, ManuallyPriced(structures));

    /// <summary>Structure quantities as BOQ rows, so they are priced, auto-assigned and exported like any measured layer.</summary>
    public static IReadOnlyList<InfrastructureGroup> DerivedGroups(IReadOnlyList<LayerQuantity> quantities) => quantities
        .Select(q => new InfrastructureGroup
        {
            Source = DerivedSource,
            Layer = q.Structure.Length > 0 && q.Structure != "מבנה ידני" ? $"{q.SurfaceName} · {q.Structure} · {q.Layer.Display}" : $"{q.SurfaceName} · {q.Layer.Display}",
            Topic = Paving(q) ? "ריצופים" : "כבישים ואספלט",
            Count = 1, LengthMetres = 0,
            AreaSquareMetres = q.Layer.ByVolume ? 0 : q.PaidArea,
            VolumeCubicMetres = q.Layer.ByVolume ? q.VolumeCubicMetres : 0,
            GroupPart = DerivedPart,
            MeasurementUnit = q.Layer.Unit,
            Evidence = $"שטח {q.SurfaceName} {q.AreaSquareMetres:N2} מ״ר" +
                       (q.Layer.ByVolume ? $" × עובי {q.Layer.ThicknessCm:0.#} ס״מ" : "") + (q.Layer.Times > 1 ? $" × {q.Layer.Times} שכבות" : "") +
                       (q.IsDefault ? " · " + q.Structure + " — ערך טיפוסי, לא מדו״ח מבנה; יש לאמת מול דו״ח היועץ"
                        : q.Origin == PavementReportPlan.Origin.Report && q.Structure.Length > 0 ? " · מתוך דו״ח המבנה: " + q.Structure
                        : " · מבנה שהוזן ידנית"),
            Element = q.Layer.SearchHint,
            Surface = RoadSurfaceKind.None,
        })
        .ToArray();

    /// <summary>Pavers (on any surface) are paving items; asphalt and sprays on a sidewalk or island are road items.</summary>
    private static bool Paving(LayerQuantity q) =>
        q.Layer.SearchHint.StartsWith("אבן משתלבת", StringComparison.Ordinal) ||
        (q.Surface is RoadSurfaceKind.Sidewalk or RoadSurfaceKind.Island && !q.Layer.ByVolume &&
         !q.Layer.Name.StartsWith("תא״צ", StringComparison.Ordinal) && !q.Layer.Name.StartsWith("ריסוס", StringComparison.Ordinal));

    // ── Per-drawing persistence of the engineer's hand-typed structure ──
    private sealed record Stored(int SchemaVersion, PavementStructure[] Structures);
    /// <summary>The engineer's saved structures. Version 1 files were written by 1.10 and earlier, which saved its placeholder
    /// defaults with every edit: a saved layer list identical to an old placeholder is not the engineer's and is dropped.</summary>
    public static IReadOnlyList<PavementStructure> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Defaults;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is not { SchemaVersion: 1 or 2 } || stored.Structures == null) return Defaults;
            var saved = stored.Structures.Where(s => s?.Layers != null && (stored.SchemaVersion == 2 || !IsOldPlaceholder(s)))
                .GroupBy(s => s.Surface).ToDictionary(s => s.Key, s => s.Last());
            return Defaults.Select(d => saved.GetValueOrDefault(d.Surface) ?? d).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return Defaults; }
    }
    public static void Save(string path, IReadOnlyList<PavementStructure> structures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(2, structures.ToArray()))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>The 1.9–1.10 placeholder layer lists, by surface, as "name|thickness" signatures.</summary>
    private static readonly Dictionary<RoadSurfaceKind, string> OldPlaceholders = new()
    {
        [RoadSurfaceKind.Road] = "שכבה נושאת תא״צ 19|5;שכבה מקשרת תא״צ 25|7;ריסוס מאחה|0;ריסוס יסוד|0;מצע סוג א׳|30",
        [RoadSurfaceKind.PublicTransport] = "שכבה נושאת תא״צ 19|5;שכבה מקשרת תא״צ 25|7;מצע סוג א׳|30",
        [RoadSurfaceKind.Shoulder] = "תא״צ 19|5;מצע סוג א׳|20",
        [RoadSurfaceKind.BikePath] = "תא״צ 12.5|4;מצע סוג א׳|20",
        [RoadSurfaceKind.Sidewalk] = "ריצוף אבנים משתלבות|6;מצע סוג א׳|20",
        [RoadSurfaceKind.Island] = "ריצוף / אבנים משתלבות|6",
        [RoadSurfaceKind.Milling] = "קרצוף אספלט|4;ריבוד תא״צ 19|4",
    };
    private static bool IsOldPlaceholder(PavementStructure s) =>
        OldPlaceholders.TryGetValue(s.Surface, out var signature) &&
        string.Join(";", s.Layers.Select(l => l.Name + "|" + l.ThicknessCm.ToString("0.#", CultureInfo.InvariantCulture))) == signature;
}
