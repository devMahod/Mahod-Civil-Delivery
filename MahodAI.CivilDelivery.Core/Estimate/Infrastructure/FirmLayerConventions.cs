using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>What the work on an element is: new construction, existing infrastructure that stays, or existing that is demolished.</summary>
public enum InfrastructureDisposition { New, ExistingKeep, ExistingDemolish }

/// <summary>Road-surface families whose plan area is the base of the pavement structure (שטח ונפח כבישים).</summary>
public enum RoadSurfaceKind { None, Road, PublicTransport, Sidewalk, BikePath, Shoulder, Island, Milling, StructureArea }

/// <summary>
/// What a firm-convention layer name means. The convention is the Mahod / NTI CAD layer standard
/// <c>&lt;discipline&gt;-&lt;element&gt;[-modifier]</c> (HW road, UT utilities, DR drainage, TR traffic, EL electric,
/// LA landscape, ST structures, SR survey), learned from 100 project drawings on 2026-11-05 (docs/BOQ_LAYERS_HE.md).
/// A meaning narrows the catalogue search and the measuring unit; it never chooses a payable specification.
/// </summary>
public sealed record LayerMeaning(string Topic, string Element, string Unit, RoadSurfaceKind Surface,
    string DemolitionTerms, LayerRole Role, string Pattern);

/// <summary>Quantity = a pay item; Boundary = an edge line that bounds an area but is not itself priced; Annotation = text, dimensions, section/profile drafting.</summary>
public enum LayerRole { Quantity, Boundary, Annotation }

public static class FirmLayerConventions
{
    private sealed record Entry(Regex Pattern, LayerMeaning Meaning);
    private const string Road = "כבישים ואספלט", Paving = "ריצופים", Curb = "אבני שפה", Fence = "מסגרות וגדרות",
        Signs = "סימון ושילוט", Water = "מים", Sewer = "ביוב", Drain = "ניקוז", Gas = "גז", Electric = "חשמל",
        Lighting = "תאורה", Telecom = "תקשורת", Irrigation = "השקיה", Walls = "קירות ומבנים", Landscape = "גינון",
        Signals = "רמזורים", Earth = "עבודות עפר";

    private static Entry E(string pattern, string topic, string element, string unit, RoadSurfaceKind surface, string demolition,
        LayerRole role = LayerRole.Quantity)
        => new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new LayerMeaning(topic, element, unit, surface, demolition, role, pattern));

    // Order matters: the first match wins, so specific families come before generic ones.
    private static readonly Entry[] Entries =
    {
        // ── Drafting that carries no quantity: cross-section and profile sheets, labels, dimensions ──
        E(@"(?:_|-)(?:ELEV|NAME|OFFS|LBL|LABEL)$", "", "כיתוב", "", RoadSurfaceKind.None, "", LayerRole.Annotation),
        E(@"^(?:HW|RR|TR|DR|UT)-(?:CS|PR|LS|TYP|DIM|NOTE|TEXT|TX|CONT|MISC-DT|STAG-NOTE|ALGN)(?:-|$|\d)", "", "חתך / חתך אורך / כיתוב", "", RoadSurfaceKind.None, "", LayerRole.Annotation),
        E(@"(?:^|-)(?:TX|TXT|TEXT|NOTE|DIM|T)$", "", "כיתוב", "", RoadSurfaceKind.None, "", LayerRole.Annotation),
        // Contours, slope arrows and section ticks of the CL (centre-line / terrain) drawings; Civil tick blocks.
        E(@"^CL[_-](?:CONT|SLOPE|SECT|TICK|GRID|ELEV)|^TICK$|^DEFPOINTS?$", "", "קווי גובה / סימוני חתך", "", RoadSurfaceKind.None, "", LayerRole.Annotation),

        // ── Pavement surfaces (hatches / closed outlines) ──
        E(@"^(?:CS)_", Road, "שטח מבנה מ־Mahod 3D (Calculate Structure Areas)", "מ״ר", RoadSurfaceKind.StructureArea, "אספלט"),
        E(@"KIRZUF|KIRTZUF|KIRCUF|קרצוף", Road, "קרצוף", "מ״ר", RoadSurfaceKind.Milling, "קרצוף"),
        E(@"^HW-(?:HTCH-NTZ|HTCH-NATAZ|HATCH-NTZ)|NATAZ|נת""?צ", Road, "נתיב תחבורה ציבורית (נת״צ)", "מ״ר", RoadSurfaceKind.PublicTransport, "אספלט"),
        E(@"^HW-BIKE-HA|^HW-HTCH-BIKE|^PL-BIKE|BIKE-H(?:A|ATCH)?$", Road, "שביל אופניים", "מ״ר", RoadSurfaceKind.BikePath, "אספלט"),
        E(@"^HW-(?:SDWK-H|HATCH-SIDEWALK|HA-SIDEWALK|HATCH-PLATOT)|^HW_HA_SIDEWALK|^\d*RI[T]?ZUF(?:-|$)", Paving, "מדרכה / ריצוף", "מ״ר", RoadSurfaceKind.Sidewalk, "ריצוף|מדרכ"),
        E(@"^HW-HATCH-ILND|ISLAND-H|^HW-ILND-H", Paving, "אי תנועה", "מ״ר", RoadSurfaceKind.Island, "ריצוף|אספלט"),
        E(@"SHULIAIM|SHULAIM|^HW-SHLD-H", Road, "שוליים", "מ״ר", RoadSurfaceKind.Shoulder, "אספלט"),
        E(@"^(?:HW-)?(?:ROAD-HA|HTCH-ROAD|HATCH-ROAD|HATCH-KVISH|ASPH-H|ASF-H|TYP-ASPH|DEREH-HAKLAI)|HATCH-[CK]VISH|^ASF-|^ASPH", Road, "מיסעה (אספלט)", "מ״ר", RoadSurfaceKind.Road, "אספלט"),

        // ── Edges that bound an area; their length is not a pay item on its own ──
        E(@"^HW-(?:TRWY|EOP|SHLD|SDWK|BIKE-LANE|ROW|RD-SOFI|SERV|DEREH-SHIRUT)(?:-|$)", Road, "קו שפה / גבול שטח", "מטר", RoadSurfaceKind.None, "", LayerRole.Boundary),

        // ── Linear road elements ──
        E(@"^(?:(?:HW|TR)-)?(?:CURB|KERB|ISLAND-CURBSTONE|GRDN-STONE|EVEN_MUN)|^(?:HW|TR)-CURB\d", Curb, "אבני שפה", "מטר", RoadSurfaceKind.None, "אבני שפה|אבן שפה"),
        // A barrier is drawn with reference lines beside it: the edge of pavement it is set from (EOP), its working width
        // (WW-1.30, W3-1.00) and, for a concrete barrier, its two faces as a "-01-1" copy of the axis layer (130X, 2026-11-10:
        // 12.6 km of such lines were priced as barrier). They are listed, never priced.
        E(@"^(?:(?:HW|TR|ST)-)?BARI-(?:EOP|WW|W\d)(?:[-_]|$)|^(?:(?:HW|TR|ST)-)?BARI-.+-\d{2}-\d$", Fence, "קו עזר של מעקה (שפת מיסעה / רוחב פעיל / פני מעקה)", "מטר", RoadSurfaceKind.None, "", LayerRole.Boundary),
        E(@"^(?:(?:HW|TR|ST)-)?BARI|^H\d+W\d+(?:-|$)", Fence, "מעקה בטיחות", "מטר", RoadSurfaceKind.None, "מעקה"),
        E(@"^(?:(?:HW|TR)-)?FENC", Fence, "גדר", "מטר", RoadSurfaceKind.None, "גדר"),
        E(@"^HW-(?:DTCH|TAALA)", Drain, "תעלה", "מטר", RoadSurfaceKind.None, "תעל"),
        E(@"^(?:HW|ST)-WALL", Walls, "קיר", "מטר", RoadSurfaceKind.None, "קיר"),

        // ── Traffic: marking and signs ──
        E(@"^TR-MARK|^TR-STAG-\d|^TR-STG", Signs, "סימון כבישים", "מטר", RoadSurfaceKind.None, "סימון|מחיקת סימון"),
        E(@"^TR-SIGN|^SN-", Signs, "תמרורים ושילוט", "יח׳", RoadSurfaceKind.None, "שלט|תמרור"),

        // ── Drainage ──
        E(@"^DR-(?:MNHL|MHNL)", Drain, "שוחות / תאי ביקורת", "יח׳", RoadSurfaceKind.None, "שוחות|תאי ביקורת"),
        E(@"^DR-(?:INLT|SLOT|JK)", Drain, "קולטנים", "יח׳", RoadSurfaceKind.None, "קולטן|שוחות"),
        E(@"^DR-CULV", Drain, "מעביר מים", "מטר", RoadSurfaceKind.None, "מעביר"),
        E(@"^DR-|^UT-DR(?:-|_|$)", Drain, "צינור ניקוז", "מטר", RoadSurfaceKind.None, "צינור ניקוז"),

        // ── Utilities (UT-<kind>) ──
        E(@"^UT-(?:W|WS|WATER|MAIM|MAIN|MK|MEK)(?:-|$)", Water, "קו מים", "מטר", RoadSurfaceKind.None, "צינור מים|מים"),
        E(@"^UT-(?:S|SW|SEW|BIUV)(?:-|$)", Sewer, "קו ביוב", "מטר", RoadSurfaceKind.None, "ביוב"),
        E(@"^UT-(?:G|GAS|GAZ|F|FUEL)(?:-|$)", Gas, "קו גז / דלק", "מטר", RoadSurfaceKind.None, "צינור|גז"),
        E(@"^(?:UT|EL)-LT(?:-|$)", Lighting, "תאורה", "מטר", RoadSurfaceKind.None, "תאורה|עמוד"),
        E(@"^UT-(?:TC|TIK)(?:-|$)", Telecom, "תקשורת", "מטר", RoadSurfaceKind.None, "תקשורת|כבל"),
        E(@"^UT-EL(?:-|$)|^EL-", Electric, "חשמל", "מטר", RoadSurfaceKind.None, "כבל|חשמל"),
        E(@"^UT-IR(?:-|$)", Irrigation, "השקיה", "מטר", RoadSurfaceKind.None, "השקיה"),

        // ── Landscape ──
        E(@"^LA-TREE", Landscape, "עצים", "יח׳", RoadSurfaceKind.None, "עקיר|עצים"),
        E(@"^LA-VEGT|^HW-(?:GARDEN|GAN|COLORED-GARDEN)(?:-|$)", Landscape, "גינון", "מ״ר", RoadSurfaceKind.None, "גינון|צמח"),
        E(@"^HW-SLOP|SLOPE-H", Earth, "מדרון", "מ״ר", RoadSurfaceKind.None, "מדרון"),
    };

    /// <summary>The canonical leaf: XREF namespace and bind prefixes removed, separators unified to '-'.</summary>
    public static string Canonical(string layer)
    {
        var leaf = layer.Split('|').Last();
        var bound = leaf.LastIndexOf("$0$", StringComparison.Ordinal);
        if (bound >= 0) leaf = leaf[(bound + 3)..];
        leaf = Regex.Replace(leaf.Trim().ToUpperInvariant(), @"[\s]+", "-");
        // "!UT-…", "0-TR-…", "3D-UT-…", "3P-…" are firm prefixes for frozen copies, XREF overlays and 3D twins.
        leaf = Regex.Replace(leaf, @"^(?:!|0-|3D-|3P-)+", "");
        // As-made deliveries prefix the firm code: ASMADE-DR-PIPE is a DR-PIPE that already exists.
        leaf = Regex.Replace(leaf, @"^AS-?MADE[-_]+", "");
        return leaf;
    }

    public static LayerMeaning? Match(string layer)
    {
        var leaf = Canonical(layer);
        foreach (var e in Entries) if (e.Pattern.IsMatch(leaf)) return e.Meaning;
        return null;
    }

    /// <summary>A surface kind's Hebrew name for the roads screen.</summary>
    public static string SurfaceName(RoadSurfaceKind kind) => kind switch
    {
        RoadSurfaceKind.Road => "מיסעה", RoadSurfaceKind.PublicTransport => "נת״צ", RoadSurfaceKind.Sidewalk => "מדרכה",
        RoadSurfaceKind.BikePath => "שביל אופניים", RoadSurfaceKind.Shoulder => "שוליים", RoadSurfaceKind.Island => "אי תנועה",
        RoadSurfaceKind.Milling => "קרצוף", RoadSurfaceKind.StructureArea => "שטח מבנה (Mahod 3D)", _ => "—"
    };
}

/// <summary>
/// Existing-state detection. As-made (ASMADE) layers are surveyed existing infrastructure: by default they stay,
/// and the engineer may mark them for demolition, which is priced on its own sheet. Words that cancel or remove
/// (CNCL, BITUL, PIRUK, DEMO…) default to demolition. The evidence names the word that decided.
/// </summary>
public static class InfrastructureCondition
{
    private static readonly Regex AsMade = new(@"AS[-_ ]?MADE|ASBUILT|AS[-_ ]BUILT", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExistingToken = new(@"(?<![\p{L}\p{N}])(?:EX|EXST|EXIST|EXISTING|EXSIT|KAYAM|KAYM|KAYEMET|KAYAMIM|קיים|קיימת|קיימים|קיימות)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DemolishToken = new(@"(?<![\p{L}\p{N}])(?:CNCL|CANCEL|CANCELED|CANCELLED|BITUL|ביטול|לביטול|מבוטל|PIRUK|PERUK|PIRUKIM|לפירוק|פירוק|DEMO|DEMOL|DEMOLITION|DEMOLISH|REMOVE|REMOVAL|HARISA|HERISA|הריסה|להריסה)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // SR is the firm's survey discipline code, also after a project number: "6422-SR-WEST-MHD" (live 2026-11-09: its 62.9 km
    // of surveyed S_WALL_BT lines were read as new walls).
    private static readonly Regex SurveySource = new(@"(?:^|[-_ ])SR[-_]|SURVEY|MEDIDA|SEKER|מדידה|GIS-MADE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (InfrastructureDisposition Disposition, string Evidence) Detect(string layer, string source)
    {
        var leaf = layer.Split('|').Last();
        var words = Regex.Replace(leaf, @"[-_.$]+", " ");
        var file = Path.GetFileNameWithoutExtension((source ?? "").Split('\\', '/').Last());
        var demolish = DemolishToken.Match(words);
        if (demolish.Success) return (InfrastructureDisposition.ExistingDemolish, $"שם השכבה: {demolish.Value} ← קיים לפירוק/ביטול");
        if (AsMade.Match(leaf) is { Success: true } asMade) return (InfrastructureDisposition.ExistingKeep, $"שם השכבה: {asMade.Value} ← תשתית קיימת (As-Made)");
        if (ExistingToken.Match(words) is { Success: true } ex) return (InfrastructureDisposition.ExistingKeep, $"שם השכבה: {ex.Value} ← מצב קיים");
        if (AsMade.Match(file) is { Success: true } fileAsMade) return (InfrastructureDisposition.ExistingKeep, $"קובץ המקור: {fileAsMade.Value} ← תשתית קיימת (As-Made)");
        if (ExistingToken.Match(Regex.Replace(file, @"[-_.]+", " ")) is { Success: true } fileEx) return (InfrastructureDisposition.ExistingKeep, $"קובץ המקור: {fileEx.Value} ← מצב קיים");
        if (SurveySource.Match(file) is { Success: true } survey) return (InfrastructureDisposition.ExistingKeep, $"קובץ המקור הוא מדידה ({survey.Value}) ← מצב קיים");
        // A nested XREF names its own file in the layer's namespace ("6422-SR-SURVEY-MD17615-MHD|S_WALL_BT"), read like a file.
        var space = layer.Contains('|') ? layer[..layer.LastIndexOf('|')] : "";
        if (space.Length > 0)
        {
            if (AsMade.Match(space) is { Success: true } spaceAsMade) return (InfrastructureDisposition.ExistingKeep, $"קובץ XREF: {spaceAsMade.Value} ← תשתית קיימת (As-Made)");
            if (ExistingToken.Match(Regex.Replace(space, @"[-_.|]+", " ")) is { Success: true } spaceEx) return (InfrastructureDisposition.ExistingKeep, $"קובץ XREF: {spaceEx.Value} ← מצב קיים");
            if (SurveySource.Match(space) is { Success: true } spaceSurvey) return (InfrastructureDisposition.ExistingKeep, $"קובץ XREF הוא מדידה ({spaceSurvey.Value}) ← מצב קיים");
        }
        return (InfrastructureDisposition.New, "");
    }

    public static string Label(InfrastructureDisposition d) => d switch
    {
        InfrastructureDisposition.ExistingKeep => "קיים — נשאר",
        InfrastructureDisposition.ExistingDemolish => "קיים — לפירוק",
        _ => "עבודה חדשה"
    };
}
