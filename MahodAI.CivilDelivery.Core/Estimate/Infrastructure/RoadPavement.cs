using System.Globalization;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>One layer of a pavement structure. Asphalt is paid per m² of a stated thickness; bases and fills per m³.</summary>
public sealed record PavementLayer(string Name, double ThicknessCm, string Unit, string SearchHint)
{
    public bool ByVolume => Units.Parse(Unit).Dimension == UnitDimension.Volume;
    public string Display => ThicknessCm > 0 ? $"{Name} {ThicknessCm.ToString("0.#", CultureInfo.InvariantCulture)} ס״מ" : Name;
}

/// <summary>The layers under one surface kind (מיסעה, מדרכה, שביל אופניים…).</summary>
public sealed record PavementStructure(RoadSurfaceKind Surface, IReadOnlyList<PavementLayer> Layers);

/// <summary>
/// Road area and volume from the plan: the area of each surface kind (hatches / closed outlines on road layers)
/// times the thickness of each structure layer. The default structures are editable placeholders until the
/// firm's own pricing method is supplied; nothing here is an approved design.
/// </summary>
public static class RoadPavement
{
    public const string DerivedSource = "מבנה מיסעה — חישוב שטח × עובי";
    public const string DerivedPart = "מבנה";

    public sealed record SurfaceTotal(RoadSurfaceKind Surface, InfrastructureDisposition Disposition, double AreaSquareMetres,
        IReadOnlyList<InfrastructureGroup> Sources)
    {
        public string SurfaceName => FirmLayerConventions.SurfaceName(Surface);
        public string DispositionLabel => InfrastructureCondition.Label(Disposition);
        public string LayersDisplay => string.Join(", ", Sources.Select(s => s.LayerName).Distinct());
    }

    public sealed record LayerQuantity(RoadSurfaceKind Surface, PavementLayer Layer, double AreaSquareMetres)
    {
        public double VolumeCubicMetres => AreaSquareMetres * Layer.ThicknessCm / 100d;
        public double Quantity => Layer.ByVolume ? VolumeCubicMetres : AreaSquareMetres;
        public string SurfaceName => FirmLayerConventions.SurfaceName(Surface);
        public string LayerDisplay => Layer.Display;
        public string Unit => Layer.Unit;
    }

    /// <summary>What the roads sheet shows: plan areas, the structure built on new surfaces, and corridor volumes.</summary>
    public sealed record Report(IReadOnlyList<SurfaceTotal> Surfaces, IReadOnlyList<LayerQuantity> Layers, IReadOnlyList<InfrastructureGroup> Corridor);

    public static IReadOnlyList<PavementStructure> Defaults { get; } = new[]
    {
        new PavementStructure(RoadSurfaceKind.Road, new[]
        {
            new PavementLayer("שכבה נושאת תא״צ 19", 5, "מ״ר", "תא\"צ 19 בעובי 5"),
            new PavementLayer("שכבה מקשרת תא״צ 25", 7, "מ״ר", "תא\"צ 25 בעובי 7"),
            new PavementLayer("ריסוס מאחה", 0, "מ״ר", "ריסוס מאחה"),
            new PavementLayer("ריסוס יסוד", 0, "מ״ר", "ריסוס יסוד"),
            new PavementLayer("מצע סוג א׳", 30, "מ״ק", "מצע סוג א"),
        }),
        new PavementStructure(RoadSurfaceKind.PublicTransport, new[]
        {
            new PavementLayer("שכבה נושאת תא״צ 19", 5, "מ״ר", "תא\"צ 19 בעובי 5"),
            new PavementLayer("שכבה מקשרת תא״צ 25", 7, "מ״ר", "תא\"צ 25 בעובי 7"),
            new PavementLayer("מצע סוג א׳", 30, "מ״ק", "מצע סוג א"),
        }),
        new PavementStructure(RoadSurfaceKind.Shoulder, new[]
        {
            new PavementLayer("תא״צ 19", 5, "מ״ר", "תא\"צ 19 בעובי 5"),
            new PavementLayer("מצע סוג א׳", 20, "מ״ק", "מצע סוג א"),
        }),
        new PavementStructure(RoadSurfaceKind.BikePath, new[]
        {
            new PavementLayer("תא״צ 12.5", 5, "מ״ר", "שבילי אופניים אספלט"),
            new PavementLayer("מצע סוג א׳", 20, "מ״ק", "מצע סוג א"),
        }),
        new PavementStructure(RoadSurfaceKind.Sidewalk, new[]
        {
            new PavementLayer("ריצוף אבנים משתלבות", 6, "מ״ר", "אבנים משתלבות"),
            new PavementLayer("מצע סוג א׳", 20, "מ״ק", "מצע סוג א"),
        }),
        new PavementStructure(RoadSurfaceKind.Island, new[]
        {
            new PavementLayer("ריצוף / אבנים משתלבות", 6, "מ״ר", "אבנים משתלבות"),
        }),
        new PavementStructure(RoadSurfaceKind.Milling, new[]
        {
            new PavementLayer("קרצוף אספלט", 4, "מ״ר", "קרצוף אספלט לעומק"),
            new PavementLayer("ריבוד תא״צ 19", 4, "מ״ר", "תא\"צ 19 בעובי 4"),
        }),
        new PavementStructure(RoadSurfaceKind.StructureArea, Array.Empty<PavementLayer>()),
    };

    /// <summary>Plan area per surface kind and disposition. Excluded groups and groups without a surface kind do not count.</summary>
    public static IReadOnlyList<SurfaceTotal> Surfaces(IEnumerable<InfrastructureGroup> groups, ISet<InfrastructureGroup>? excluded = null) => groups
        .Where(g => g.Surface != RoadSurfaceKind.None && g.Source != DerivedSource && g.AreaSquareMetres > 0 && excluded?.Contains(g) != true)
        .GroupBy(g => (g.Surface, g.Disposition))
        .OrderBy(g => (int)g.Key.Surface).ThenBy(g => (int)g.Key.Disposition)
        .Select(g => new SurfaceTotal(g.Key.Surface, g.Key.Disposition, g.Sum(x => x.AreaSquareMetres), g.ToArray()))
        .ToArray();

    /// <summary>The structure quantities of NEW surfaces. Existing surfaces stay or are demolished as their own rows.</summary>
    public static IReadOnlyList<LayerQuantity> Structure(IReadOnlyList<SurfaceTotal> surfaces, IReadOnlyList<PavementStructure> structures)
    {
        var bySurface = structures.ToDictionary(s => s.Surface);
        return surfaces.Where(s => s.Disposition == InfrastructureDisposition.New && s.AreaSquareMetres > 0)
            .GroupBy(s => s.Surface)
            .SelectMany(s => (bySurface.TryGetValue(s.Key, out var structure) ? structure.Layers : Array.Empty<PavementLayer>())
                .Where(l => l.Name.Trim().Length > 0 && double.IsFinite(l.ThicknessCm) && l.ThicknessCm >= 0 && (!l.ByVolume || l.ThicknessCm > 0))
                .Select(l => new LayerQuantity(s.Key, l, s.Sum(x => x.AreaSquareMetres))))
            .ToArray();
    }

    /// <summary>Structure quantities as BOQ rows, so they are priced, auto-assigned and exported like any measured layer.</summary>
    public static IReadOnlyList<InfrastructureGroup> DerivedGroups(IReadOnlyList<LayerQuantity> quantities) => quantities
        .Select(q => new InfrastructureGroup
        {
            Source = DerivedSource,
            Layer = $"{q.SurfaceName} · {q.Layer.Display}",
            Topic = q.Surface is RoadSurfaceKind.Sidewalk or RoadSurfaceKind.Island && !q.Layer.ByVolume ? "ריצופים" : "כבישים ואספלט",
            Count = 1, LengthMetres = 0,
            AreaSquareMetres = q.Layer.ByVolume ? 0 : q.AreaSquareMetres,
            VolumeCubicMetres = q.Layer.ByVolume ? q.VolumeCubicMetres : 0,
            GroupPart = DerivedPart,
            MeasurementUnit = q.Layer.Unit,
            Evidence = $"שטח {q.SurfaceName} {q.AreaSquareMetres:N2} מ״ר" + (q.Layer.ByVolume ? $" × עובי {q.Layer.ThicknessCm:0.#} ס״מ" : ""),
            Element = q.Layer.SearchHint,
            Surface = RoadSurfaceKind.None,
        })
        .ToArray();

    // ── Per-drawing persistence of the engineer's structure ──
    private sealed record Stored(int SchemaVersion, PavementStructure[] Structures);
    public static IReadOnlyList<PavementStructure> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Defaults;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is not { SchemaVersion: 1 } || stored.Structures == null) return Defaults;
            var saved = stored.Structures.Where(s => s?.Layers != null).ToDictionary(s => s.Surface);
            return Defaults.Select(d => saved.GetValueOrDefault(d.Surface) ?? d).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return Defaults; }
    }
    public static void Save(string path, IReadOnlyList<PavementStructure> structures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(1, structures.ToArray()))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
