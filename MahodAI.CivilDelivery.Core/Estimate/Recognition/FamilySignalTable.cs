using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>What a vocabulary phrase says about the measured object.</summary>
public enum PhraseRole
{
    /// <summary>The phrase names the subject of one or more library families.</summary>
    Family,
    /// <summary>The phrase says the object exists already or is to be removed: never new work.</summary>
    Existing,
}

/// <summary>One vocabulary phrase and the library families (or markers) it can mean.</summary>
public sealed record FamilyPhrase(string Text, PhraseRole Role, IReadOnlyList<string> Families);

/// <summary>A phrase selected inside one text (longest, non-overlapping).</summary>
public sealed record PhraseHit(FamilyPhrase Phrase, int FirstToken, int TokenCount);

/// <summary>The vocabulary reading of one evidence text. Pure data: nothing here is a decision.</summary>
public sealed record TextReading(string Text, IReadOnlyList<PhraseHit> Hits, string? CatalogCode)
{
    public bool IsExisting => Hits.Any(h => h.Phrase.Role == PhraseRole.Existing);
}

/// <summary>
/// Evidence → family vocabulary for the local classifier. Derived from the RoadsV1 library (rule elements, layer
/// pattern tokens, recipe notes), the proposal vocabulary of <see cref="MappingProposalEngine"/> (whose dictionaries
/// are private and map tokens to catalog wording, so they are re-expressed here per family) and the reuse research.
/// Deliberately small: a phrase maps to a family only when it names the family's measured subject, never an
/// adjacent context. A phrase shared by several families is an ambiguous candidate set, not a decision. Numeric
/// codes alone (the "Netivei" 804/810/811 code map) are never a signal: they contradict the 6422 library.
/// Infrastructure subjects (water, sewer, drainage, electricity, lighting, traffic signals, telecom, gas) map to the
/// library's infrastructure domain families: each word names both the line family and its counted "-structures"
/// family, and the measurement basis keeps the one the group can be. Subjects outside both (walls, trees, ramps, a
/// pipe without a domain) are phrases of <see cref="OtherSubjectMarker"/>, so they count whatever else a text says.
/// </summary>
public static class FamilySignalTable
{
    /// <summary>Set member: the evidence says the object is existing or to be removed.</summary>
    public const string ExistingMarker = "#existing";

    /// <summary>
    /// Set member: the evidence names a subject outside the roads library and the infrastructure domains (a wall, a
    /// tree, a ramp, a pipe or manhole whose domain is not named...).
    /// </summary>
    public const string OtherSubjectMarker = "#other-subject";

    /// <summary>
    /// Set member added by the classifier (never by a phrase): one text or channel names subjects of more than one
    /// domain (a roads element and an infrastructure domain, or two infrastructure domains). It is not a decision and
    /// no other channel narrows it to one of them.
    /// </summary>
    public const string MixedSubjectMarker = "#mixed-subjects";

    /// <summary>Suffix of the counted (structures and fittings) family of an infrastructure domain.</summary>
    public const string StructuresSuffix = "-structures";

    /// <summary>A nearby text this close (host metres) touches or lies inside the element: it outranks farther texts.</summary>
    public const double TouchingDistanceMetres = 0.01;

    /// <summary>A constant polyline width at or below this (metres) is a widthless line.</summary>
    public const double WidthlessMetres = 0.001;

    // RoadsV1 family ids (EngineerBoqLibrary.RoadsV1). A test pins that every id below exists in the library.
    internal const string RoadPavement = "road-pavement", BrtPavement = "brt-pavement", SidewalkPaving = "sidewalk-paving",
        IslandPaving = "island-paving", BikePath = "bike-path", Landscape = "landscape", CurbRoad = "curb-road",
        CurbIsland = "curb-island", CurbIslandHw = "curb-island-hw", CurbInnerIsland = "curb-inner-island",
        CurbGarden = "curb-garden", CurbLowered = "curb-lowered", CurbBike = "curb-bike", PavementEdge = "pavement-edge",
        SidewalkEdge = "sidewalk-edge", CurbPainting = "curb-painting", Dash33 = "marking-dash-3-3",
        Dash315 = "marking-dash-3-15", Dash11 = "marking-dash-1-1", Crossings = "marking-crossings",
        CrossingLines = "marking-crossing-lines", Transverse = "marking-transverse", MarkingLines = "marking-lines",
        Marking804 = "marking-804", BikeArrows = "bike-arrows", BikeSymbols = "bike-symbols",
        MarkingArrows = "marking-arrows", MarkingMBlocks = "marking-m-blocks", SignPlates = "sign-plates",
        SignFaces = "sign-faces", BusStopSigns = "bus-stop-signs", BusShelters = "bus-shelters",
        BusStopBlocks = "bus-stop-blocks", BikeRacks = "bike-racks", PedestrianFence = "pedestrian-fence",
        SafetyBarrier = "safety-barrier";

    private static readonly string[] Pavement = { RoadPavement, BrtPavement };
    private static readonly string[] Asphalt = { RoadPavement, BrtPavement, SidewalkPaving, IslandPaving, BikePath };
    private static readonly string[] Pavers = { SidewalkPaving, IslandPaving, BikePath };
    private static readonly string[] CurbsGeneric = { CurbRoad, CurbIsland, CurbLowered, CurbBike };
    private static readonly string[] Dashes = { Dash33, Dash315, Dash11 };
    private static readonly string[] CrossingsAll = { Crossings, CrossingLines };
    private static readonly string[] MarkingAll =
    {
        Dash33, Dash315, Dash11, Crossings, CrossingLines, Transverse, MarkingLines, Marking804,
        BikeArrows, BikeSymbols, MarkingArrows, MarkingMBlocks,
    };
    private static readonly string[] Painting = MarkingAll.Append(CurbPainting).ToArray();
    private static readonly string[] BikeAll = { BikePath, CurbBike, BikeArrows, BikeSymbols, BikeRacks };
    private static readonly string[] BikeWay = { BikePath, CurbBike };
    private static readonly string[] Arrows = { MarkingArrows, BikeArrows };
    private static readonly string[] Signs = { SignPlates, SignFaces };
    private static readonly string[] BusAll = { BusStopSigns, BusShelters, BusStopBlocks };
    private static readonly string[] Railings = { PedestrianFence, SafetyBarrier };

    // Infrastructure domain families (EngineerBoqLibrary.UtilityDomains). A test pins that each exists in the library
    // and that every domain is reachable from a phrase.
    internal const string UtilityWater = "utility-water", UtilitySewer = "utility-sewer", UtilityDrainage = "utility-drainage",
        UtilityElectric = "utility-electric", UtilityLighting = "utility-lighting", UtilitySignals = "utility-traffic-signals",
        UtilityTelecom = "utility-telecom", UtilityGas = "utility-gas";

    /// <summary>A domain's line family and its counted structures family: the measurement basis keeps one.</summary>
    private static string[] Domains(params string[] lineFamilies) =>
        lineFamilies.SelectMany(id => new[] { id, id + StructuresSuffix }).ToArray();

    private static readonly string[] Water = Domains(UtilityWater);
    private static readonly string[] Sewer = Domains(UtilitySewer);
    private static readonly string[] Drainage = Domains(UtilityDrainage);
    private static readonly string[] Electric = Domains(UtilityElectric);
    private static readonly string[] Lighting = Domains(UtilityLighting);
    private static readonly string[] Signals = Domains(UtilitySignals);
    private static readonly string[] Telecom = Domains(UtilityTelecom);
    private static readonly string[] Gas = Domains(UtilityGas);

    /// <summary>A pipe, manhole or channel whose domain is not named: any domain, or none of them. It can only narrow.</summary>
    private static readonly string[] AnyInfrastructure = Domains(UtilityWater, UtilitySewer, UtilityDrainage, UtilityElectric,
        UtilityLighting, UtilitySignals, UtilityTelecom, UtilityGas).Append(OtherSubjectMarker).ToArray();

    /// <summary>A cable whose domain is not named.</summary>
    private static readonly string[] AnyCable = Domains(UtilityElectric, UtilityLighting, UtilitySignals, UtilityTelecom)
        .Append(OtherSubjectMarker).ToArray();

    private static readonly string[] OtherSubject = { OtherSubjectMarker };

    /// <summary>
    /// Phrase → families. Hebrew and Latin phrases are matched on whole tokens: Hebrew final letters, geresh/gershayim,
    /// niqqud and a doubled yod/vav (חצייה/חציה, אופניים/אופנים, מקווקוו/מקווקו) are normalised. A single dropped
    /// yod/vav is NOT: it makes different words equal (כביש/כבש, ריצוף/רציף), so such spellings are listed
    /// explicitly (מיסעה/מסעה). The first word may carry a one- or two-letter prefix (ה/ו/ב/ל/מ/ש/כ) when at least
    /// three letters remain, or two inside a longer phrase (באי תנועה); alone, לחץ is not חץ. Latin text is split on
    /// separators and CamelCase.
    /// </summary>
    private static readonly (string[] Texts, string[] Families)[] FamilyEntries =
    {
        // --- Pavement (library: road-pavement, brt-pavement; MPE ASPHALT/ASF/MISA/ROAD) -----------------
        (new[] { "מיסעה", "מיסעות", "מסעה", "מסעות", "MISA", "ASF", "MAZA", "SMA", "תא\"צ", "תאמ\"א", "בטון אספלט", "שכבה עליונה" }, Pavement),
        (new[] { "ROADWAY", "CARRIAGEWAY", "HTCH ROAD" }, new[] { RoadPavement }),
        // A bare "road" qualifies curbs, marking and edges as often as it names the pavement: it can only narrow.
        (new[] { "כביש", "כבישים", "ROAD" }, new[] { RoadPavement, OtherSubjectMarker }),
        // ROAD qualifies many roads phrases (ROAD CURB, ROAD MARKING), so its non-pavement contexts are listed instead.
        (new[] { "ROAD RESERVE", "RIGHT OF WAY", "ROAD AXIS", "ROAD CENTERLINE", "ROAD CENTRELINE" }, OtherSubject),
        (new[] { "אספלט", "ASPHALT" }, Asphalt),
        (new[] { "נת\"צ", "נתיב תחבורה ציבורית", "נתיבי תחבורה ציבורית", "נתיב תח\"צ", "נתיב אוטובוס", "נתיב אוטובוסים",
                 "NATAZ", "BRT", "BUS LANE", "BUSWAY", "HTCH NATAZ" }, new[] { BrtPavement }),

        // --- Sidewalks, islands, bike paths, landscape ----------------------------------------------------
        (new[] { "מדרכה", "מדרכות", "SIDEWALK", "SDWK", "SWDK", "MIDRACHA", "MDR", "WALKWAY", "FOOTPATH", "FOOTWAY" }, new[] { SidewalkPaving }),
        (new[] { "אספלט למדרכות", "אספלט למדרכות ואיים" }, new[] { SidewalkPaving, IslandPaving }),
        (new[] { "ריצוף", "מרוצף", "מרוצפת", "אבנים משתלבות", "אבן משתלבת", "אבני ריצוף", "PAVERS", "PAVER", "RITZUF", "INTERLOCKING" }, Pavers),
        (new[] { "גבול מדרכה", "קו גבול מדרכה", "גבול המדרכה", "END MDR", "SIDEWALK EDGE" }, new[] { SidewalkEdge }),
        (new[] { "אי תנועה", "איי תנועה", "עטרה", "ISLAND", "ILND", "TRAFFIC ISLAND", "HATCH ILND" }, new[] { IslandPaving }),
        (new[] { "שביל אופניים", "שבילי אופניים", "נתיב אופניים", "מסלול אופניים", "BIKE LANE", "BIKE PATH", "CYCLEWAY",
                 "CYCLE TRACK", "PL BIKE", "HW BIKE LANE" }, BikeWay),
        (new[] { "ציפוי צבעוני" }, new[] { BikePath }),
        (new[] { "אופניים", "BIKE", "BICYCLE", "CYCLE" }, BikeAll),
        (new[] { "גינון", "שטח גינון", "שטחי גינון", "דשא", "מדשאה", "אדמת גן", "GINUN", "GARDEN", "GRASS", "LAWN",
                 "LANDSCAPE", "PLANTING", "HACTH GINUN" }, new[] { Landscape }),

        // --- Curbs (library curb-*; MPE CURB/KERB/SHAPA and the island/garden subject requirements) ----------
        (new[] { "אבן שפה", "אבני שפה", "CURB", "KERB", "CURBSTONE", "KERBSTONE", "SHAPA" }, CurbsGeneric),
        (new[] { "אבן שפה לכביש", "אבן שפה כביש", "אבני שפה לכביש", "HW CURB", "ROAD CURB" }, new[] { CurbRoad }),
        (new[] { "אבן שפה לאי תנועה", "אבן שפה באי תנועה", "אבן שפה אי תנועה", "אבני שפה לאיי תנועה", "אבן שפה לאי",
                 "אבן שפה לאיים", "אבן שפה לעטרה", "ISLAND CURB", "ISLAND CURBSTONE", "TR ISLAND CURBSTONE" }, new[] { CurbIsland }),
        (new[] { "HW CURB ILND", "CURB ILND" }, new[] { CurbIslandHw }),
        (new[] { "INNER ISLAND", "TR INNER ISLAND CURBSTONE" }, new[] { CurbInnerIsland }),
        (new[] { "אבן גן", "אבני גן", "GRDN STONE", "GARDEN STONE", "GARDEN CURB", "EV GAN", "TR GRDN STONE", "TR INNER GRDN STONE" }, new[] { CurbGarden }),
        (new[] { "אבן שפה מונמכת", "אבני שפה מונמכות", "אבן מונמכת", "מונמכת", "EVEN MUN", "LOWERED CURB", "DROPPED CURB", "DROP CURB" }, new[] { CurbLowered }),
        (new[] { "אבן שפה לשביל אופניים", "אבן שפה לאופניים", "אבן שפה לשביל" }, new[] { CurbBike }),
        (new[] { "צביעת אבני שפה", "צביעת אבן שפה", "אבן שפה צבועה", "אבני שפה צבועות", "CURB PAINT", "CURB PAINTING",
                 "TR CURB", "CURB RED", "CURB YLV", "CURB BLK" }, new[] { CurbPainting }),

        // --- Edge lines -------------------------------------------------------------------------------------
        (new[] { "שפת מיסעה", "קו שפת מיסעה", "שפת כביש", "קצה מיסעה", "גבול מיסעה", "שפת מסעה", "קו שפת מסעה", "קצה מסעה",
                 "גבול מסעה", "TRWY", "HW TRWY", "EOP", "EDGE OF PAVEMENT", "PAVEMENT EDGE" }, new[] { PavementEdge }),
        (new[] { "קו גבול" }, new[] { PavementEdge, SidewalkEdge }),

        // --- Road marking (library marking-*; MPE MARK/SIMUN/CROSS/ZEBRA) ----------------------------------
        (new[] { "סימון", "סימונים", "סימון כבישים", "סימון דרך", "MARK", "MARKING", "SIMUN", "STRIPE", "STRIPING", "TR MARK" }, MarkingAll),
        // Not "צבוע": without yod/vav it reads as "צבע" (a colour attribute), which is not paint work.
        (new[] { "צביעה", "צביעת", "PAINT", "PAINTED" }, Painting),
        (new[] { "קו רציף", "קווים רציפים", "קו רצוף", "קווים רצופים", "קו כפול", "קווים כפולים", "קו שוליים", "קו נתיב",
                 "SOLID LINE", "DOUBLE LINE", "EDGE LINE" }, new[] { MarkingLines }),
        (new[] { "קו הפרדה", "קווי הפרדה", "קו הפרדת נתיבים", "קו אורך", "קווי אורך", "LANE LINE" }, new[] { MarkingLines, Dash33, Dash315, Dash11 }),
        (new[] { "קו מקווקו", "קווים מקווקווים", "מקווקו", "מקווקוו", "DASHED", "DASHED LINE", "DASH LINE" }, Dashes),
        // The paint ratio lives in the library layer patterns (*3-3*, *3-1.5*, *(1-1)*).
        (new[] { "מקווקו 3 3", "קו מקווקו 3 3", "DASHED 3 3", "DASHED LINE 3 3" }, new[] { Dash33 }),
        (new[] { "מקווקו 3 1.5", "קו מקווקו 3 1.5", "DASHED 3 1.5", "DASHED LINE 3 1.5" }, new[] { Dash315 }),
        (new[] { "מקווקו 1 1", "קו מקווקו 1 1", "DASHED 1 1", "DASHED LINE 1 1" }, new[] { Dash11 }),
        (new[] { "מעבר חצייה", "מעבר חציה", "מעברי חצייה", "מעברי חציה", "מעבר הולכי רגל", "מעברי הולכי רגל", "זברה",
                 "CROSSWALK", "ZEBRA", "PEDESTRIAN CROSSING", "CROSSING" }, CrossingsAll),
        (new[] { "קו עצירה", "קווי עצירה", "קו עצור", "סימון רוחבי", "סימונים רוחביים", "קו רוחבי", "קווים רוחביים",
                 "STOP LINE", "STOPLINE", "STOP BAR", "TRANSVERSE", "GIVE WAY LINE" }, new[] { Transverse }),
        (new[] { "ZEVA" }, new[] { Marking804 }),
        (new[] { "חץ", "חצים", "סימון חץ", "סימון חצים", "סימון חיצים", "ARROW", "ARROWS", "ARW", "ARRW" }, Arrows),
        (new[] { "חץ תנועה", "חצי תנועה", "חץ כיוון", "חצי כיוון", "TRAFFIC ARROW", "TR ARW", "TR ARRW" }, new[] { MarkingArrows }),
        (new[] { "TR MARK ARW" }, new[] { MarkingArrows, MarkingMBlocks }),
        (new[] { "חץ אופניים", "חצי אופניים", "BIKE ARROW" }, new[] { BikeArrows }),
        (new[] { "סמל אופניים", "סמלי אופניים", "BIKE SYMBOL", "BICYCLE SYMBOL", "TR SIGN STAG" }, new[] { BikeSymbols }),

        // --- Signs, bus stops, furniture ------------------------------------------------------------------
        (new[] { "תמרור", "תמרורים", "שלט", "שלטים", "שילוט", "שלט תנועה", "SIGN", "SIGNS", "SIGNAGE", "TRAFFIC SIGN", "TAMROR", "TAMRUR" }, Signs),
        (new[] { "עמוד תמרור", "עמודי תמרורים", "עמוד תמרורים", "עמוד שלט", "עמודי שלטים", "SIGN POLE", "SIGN POST", "SIGNPOST",
                 "TR SIGN POLE", "TR POLE" }, new[] { SignPlates }),
        (new[] { "סמל תמרור", "סמלי תמרורים", "סוג תמרור", "TR SIGN", "TR SIGN BL", "SIGN FACE" }, new[] { SignFaces }),
        (new[] { "תחנת אוטובוס", "תחנות אוטובוס", "תחנת אוטובוסים", "BUS STOP", "BUS STATION" }, BusAll),
        (new[] { "שלט תחנה", "שלט תחנת אוטובוס", "שלטי תחנות", "שלטי תחנות אוטובוס", "BUS STOP SIGN" }, new[] { BusStopSigns }),
        (new[] { "סככה", "סככות", "סככת המתנה", "סככת תחנה", "סככת אוטובוס", "BUS SHELTER", "SOCHECHA", "BUS STATION NEW" }, new[] { BusShelters }),
        // A bare SHELTER may be a public (bomb) shelter: it can only narrow.
        (new[] { "SHELTER" }, new[] { BusShelters, OtherSubjectMarker }),
        // A generic staging/waiting area does not establish a bus-stop component.
        // Retain explicit bus-stop vocabulary; ambiguous area names need other CAD evidence.
        (new[] { "TAMRUR BUS ST" }, new[] { BusStopBlocks }),
        (new[] { "מתקן אופניים", "מתקני אופניים", "מתקן חנייה לאופניים", "מתקני חנייה לאופניים", "מתקן חניה לאופניים",
                 "חניית אופניים", "חניה לאופניים", "חנייה לאופניים", "מתקן ל 2 אופניים", "BIKE RACK", "BICYCLE RACK",
                 "BIKE PARKING", "BICYCLE PARKING" }, new[] { BikeRacks }),
        (new[] { "מעקה להולכי רגל", "מעקה הולכי רגל", "מעקות להולכי רגל", "גדר להולכי רגל", "מעקה בטיחות להולכי רגל",
                 "FENC PDST", "PEDESTRIAN FENCE", "PEDESTRIAN RAILING", "PEDESTRIAN GUARDRAIL" }, new[] { PedestrianFence }),
        (new[] { "מעקה", "מעקות", "RAILING", "RAIL" }, Railings),
        (new[] { "גדר", "FENCE", "GADER" }, new[] { PedestrianFence, OtherSubjectMarker }),
        (new[] { "מעקה בטיחות", "מעקות בטיחות", "מחסום בטיחות", "מעקה פלדה", "מעקה בטון", "GUARDRAIL", "GUARD RAIL",
                 "BARI", "SAFETY BARRIER", "CRASH BARRIER", "NEW JERSEY" }, new[] { SafetyBarrier }),
        // A bare BARRIER may be a noise barrier or a parking barrier: it can only narrow.
        (new[] { "BARRIER" }, new[] { SafetyBarrier, OtherSubjectMarker }),

        // --- Infrastructure domains (library utility-*; MPE MAIM/BIUV/NIKUZ/HASHMAL/BEZEQ..., SectionProjectionLogic labels) --
        (new[] { "מים", "קו מים", "קווי מים", "צנרת מים", "קו מקורות", "MAIM", "WATER", "WATER LINE", "WATERLINE", "MEKOROT" }, Water),
        (new[] { "ביוב", "קו ביוב", "קווי ביוב", "קולחין", "שפכים", "מי שפכים", "BIUV", "SEWER", "SEWAGE", "WASTEWATER", "WASTE WATER" }, Sewer),
        // Rain and runoff water is drainage, not the water supply ("ניקוז מים" is one subject, not two).
        (new[] { "ניקוז", "קו ניקוז", "קווי ניקוז", "תעלת ניקוז", "תעלות ניקוז", "ניקוז מים", "תעלת ניקוז מים", "מי גשם", "מי נגר",
                 "קולטן", "קולטנים", "NIKUZ", "DRAIN", "DRAINAGE", "STORM", "STORMWATER", "STORM WATER", "INLET", "KOLTAN" }, Drainage),
        // Bare POWER, LIGHT, HOT and "מקורות" (also "sources") are too generic to name a domain on their own.
        (new[] { "חשמל", "קו חשמל", "קווי חשמל", "HASHMAL", "ELEC", "ELECTRIC", "ELECTRICAL", "ELECTRICITY", "POWER LINE", "POWER CABLE" }, Electric),
        (new[] { "תאורה", "תאורת", "תאורת רחוב", "עמוד תאורה", "עמודי תאורה", "TEURA", "TAURA", "LIGHTING", "STREET LIGHT",
                 "STREETLIGHT" }, Lighting),
        (new[] { "רמזור", "רמזורים", "עמוד רמזור", "RAMZOR", "TRAFFIC SIGNAL", "TRAFFIC LIGHT" }, Signals),
        (new[] { "בזק", "תקשורת", "קו תקשורת", "קווי תקשורת", "BEZEQ", "BEZEK", "TELECOM", "TIKSHORET" }, Telecom),
        (new[] { "גז", "קו גז", "GAS", "GAZ" }, Gas),
        // A carrier without its domain: "צינור ביוב" is sewer, "PIPE" alone is not a decision.
        (new[] { "צינור", "צינורות", "צנרת", "PIPE", "PIPELINE", "TZINOR", "שוחה", "שוחות", "שוחת", "תא בקרה", "תאי בקרה", "MANHOLE",
                 "SHUHA", "SHOHA", "תעלה", "תעלות", "תעלת", "CHANNEL", "TAALA" }, AnyInfrastructure),
        (new[] { "כבל", "כבלים", "CABLE" }, AnyCable),

        // --- Subjects outside the library and the infrastructure domains: never a family, whatever else the text says --
        (new[] { "קיר", "קירות", "קיר תומך", "קירות תומכים", "קיר אקוסטי", "WALL", "KIR", "RETAINING WALL", "NOISE WALL",
                 "NOISE BARRIER", "ACOUSTIC BARRIER", "עץ", "עצים", "נטיעה", "נטיעות", "TREE", "רמפה", "רמפות", "כבש", "כבשים", "RAMP",
                 "ספסל", "ספסלים", "BENCH", "מדרגות", "STAIR", "מעביר מים", "CULVERT", "LIGHT RAIL", "LRT", "רכבת קלה",
                 "GAS STATION" }, OtherSubject),
    };

    /// <summary>Existing / removal wording (MPE DemolitionWords, ExistingLayerPatterns tokens, LANDQ markers).</summary>
    private static readonly string[] ExistingTexts =
    {
        "קיים", "קיימת", "קיימים", "קיימות", "מצב קיים", "פירוק", "לפירוק", "פרוק", "הריסה", "להריסה", "ביטול", "לביטול",
        "EXIST", "EXISTING", "EXST", "EXSIT", "EX", "KAYAM", "DEMO", "DEMOLITION", "DEMOLISH", "PIRUK", "REMOVE", "REMOVAL",
    };

    /// <summary>Paint/gap metres of the library's dashed-marking families (from their layer patterns).</summary>
    public static IReadOnlyDictionary<string, (double Paint, double Gap)> DashRatios { get; } =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            [Dash33] = (3, 3),
            [Dash315] = (3, 1.5),
            [Dash11] = (1, 1),
        };

    /// <summary>Families whose element is a line WITHOUT drawn width ("קווי מעברי חצייה ללא רוחב").</summary>
    public static IReadOnlyList<string> WidthlessFamilies { get; } = new[] { CrossingLines };

    public static IReadOnlyList<FamilyPhrase> Phrases { get; } = BuildPhrases();

    private static readonly IReadOnlyList<Token[]> PhraseTokens = Phrases.Select(p => Tokenize(p.Text).ToArray()).ToList();

    /// <summary>SHA-256 of the canonical tables and matching constants.</summary>
    public static string Fingerprint { get; } = ComputeFingerprint();

    public static string ShortFingerprint => Fingerprint[..12];

    private static IReadOnlyList<FamilyPhrase> BuildPhrases()
    {
        var list = new List<FamilyPhrase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (texts, families) in FamilyEntries)
            foreach (var text in texts)
            {
                if (!seen.Add(text)) throw new InvalidOperationException($"Duplicate recognition phrase '{text}'.");
                list.Add(new FamilyPhrase(text, PhraseRole.Family, families));
            }
        foreach (var text in ExistingTexts)
        {
            if (!seen.Add(text)) throw new InvalidOperationException($"Duplicate recognition phrase '{text}'.");
            list.Add(new FamilyPhrase(text, PhraseRole.Existing, new[] { ExistingMarker }));
        }
        return list;
    }

    private static string ComputeFingerprint()
    {
        // Version 2: doubled-yod/vav spelling only, prefix remainder of 3 letters (2 inside a longer phrase),
        // infrastructure domains and other-subject phrases.
        var builder = new StringBuilder("mahod-family-signals/2").Append('\u001d');
        foreach (var phrase in Phrases)
            builder.Append(phrase.Role).Append('\u001f').Append(phrase.Text).Append('\u001f')
                .Append(string.Join(',', phrase.Families)).Append('\u001e');
        foreach (var (family, (paint, gap)) in DashRatios.OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append("dash=").Append(family).Append(':').Append(paint.ToString("R", CultureInfo.InvariantCulture))
                .Append('/').Append(gap.ToString("R", CultureInfo.InvariantCulture)).Append('\u001e');
        builder.Append("widthless=").Append(string.Join(',', WidthlessFamilies)).Append('\u001e');
        builder.Append("touch=").Append(TouchingDistanceMetres.ToString("R", CultureInfo.InvariantCulture))
            .Append(";widthless-m=").Append(WidthlessMetres.ToString("R", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static readonly Regex CatalogCodeText = new(@"^\s*[A-Za-z]?(?<code>\d{2}\.\d{2}\.\d{4})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads one text against the vocabulary: every phrase occurrence, then the longest (most tokens, then most
    /// characters) non-overlapping ones. A text that is exactly a price-list code (e.g. "U51.06.1900") is returned as
    /// <see cref="TextReading.CatalogCode"/> (without the publisher letter); nothing inside a sentence is read as a code.
    /// </summary>
    public static TextReading Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokenize(text);
        var candidates = new List<(int Start, int Count, int Chars, int Index)>();
        for (var index = 0; index < Phrases.Count; index++)
        {
            var phrase = PhraseTokens[index];
            if (phrase.Length == 0 || phrase.Length > tokens.Count) continue;
            // Existing/removal words match exactly (פירוק must not read "פרק", a price-list chapter).
            var spelling = Phrases[index].Role == PhraseRole.Family;
            for (var start = 0; start + phrase.Length <= tokens.Count; start++)
                if (MatchesAt(tokens, start, phrase, spelling))
                    candidates.Add((start, phrase.Length, Phrases[index].Text.Length, index));
        }
        var covered = new bool[tokens.Count];
        var hits = new List<PhraseHit>();
        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Count).ThenByDescending(c => c.Chars).ThenBy(c => c.Start).ThenBy(c => c.Index))
        {
            if (Enumerable.Range(candidate.Start, candidate.Count).Any(i => covered[i])) continue;
            for (var i = candidate.Start; i < candidate.Start + candidate.Count; i++) covered[i] = true;
            hits.Add(new PhraseHit(Phrases[candidate.Index], candidate.Start, candidate.Count));
        }
        hits.Sort((a, b) => a.FirstToken.CompareTo(b.FirstToken));
        var code = CatalogCodeText.Match(text) is { Success: true } m ? m.Groups["code"].Value : null;
        return new TextReading(text, hits, code);
    }

    /// <summary>The bare form of a catalog code (publisher letter removed) for comparison with library emits.</summary>
    public static string BareCode(string code)
    {
        var trimmed = code.Trim();
        return trimmed.Length > 1 && char.IsAsciiLetter(trimmed[0]) ? trimmed[1..] : trimmed;
    }

    // ------------------------------------------------------------------ tokens

    internal readonly record struct Token(string Exact, string Skeleton, bool Hebrew);

    private static bool MatchesAt(IReadOnlyList<Token> tokens, int start, Token[] phrase, bool spelling)
    {
        for (var i = 0; i < phrase.Length; i++)
            if (!(i == 0 ? FirstTokenMatches(tokens[start], phrase[0], spelling, phrase.Length > 1) : TokenMatches(tokens[start + i], phrase[i], spelling)))
                return false;
        return true;
    }

    private static bool TokenMatches(Token text, Token phrase, bool spelling)
    {
        if (text.Hebrew != phrase.Hebrew) return false;
        if (string.Equals(text.Exact, phrase.Exact, StringComparison.Ordinal)) return true;
        if (phrase.Hebrew)
            // A doubled yod/vav read as one (ktiv male), only for words long enough not to collide. Dropping a single
            // yod/vav is not a spelling variant: כבש (ramp) is not כביש, רציף (platform) is not ריצוף.
            return spelling && phrase.Skeleton.Length >= 3 && string.Equals(text.Skeleton, phrase.Skeleton, StringComparison.Ordinal);
        // Latin plurals: CURBS, ARROWS, CROSSINGS, BARRIERES are the same subject.
        return phrase.Exact.Length >= 3 && char.IsAsciiLetter(phrase.Exact[^1]) &&
               (string.Equals(text.Exact, phrase.Exact + "S", StringComparison.Ordinal) ||
                string.Equals(text.Exact, phrase.Exact + "ES", StringComparison.Ordinal));
    }

    private const string HebrewPrefixLetters = "הובלמשכ";

    private static bool FirstTokenMatches(Token text, Token phrase, bool spelling, bool multiToken)
    {
        if (TokenMatches(text, phrase, spelling)) return true;
        if (!text.Hebrew) return false;
        // One- or two-letter Hebrew prefixes: המדרכה, למדרכה, והכביש; three letters are not attempted. A prefix may
        // leave a two-letter word only inside a longer phrase (באי תנועה): alone, לחץ (pressure) is not חץ (arrow).
        var minimumRest = multiToken ? 2 : 3;
        for (var cut = 1; cut <= 2 && text.Exact.Length - cut >= minimumRest; cut++)
        {
            if (HebrewPrefixLetters.IndexOf(text.Exact[cut - 1]) < 0) break;
            if (TokenMatches(HebrewToken(text.Exact[cut..]), phrase, spelling)) return true;
        }
        return false;
    }

    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var kind = CharKind.Separator;
        void Flush()
        {
            if (current.Length > 0)
                tokens.Add(kind == CharKind.Hebrew ? HebrewToken(current.ToString()) : LatinToken(current.ToString()));
            current.Clear();
            kind = CharKind.Separator;
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsHebrewMark(c)) continue;
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var prev = i > 0 ? text[i - 1] : '\0';
            if (IsQuote(c) && kind == CharKind.Hebrew && IsHebrewLetter(next)) continue; // נת"צ, תאמ"א
            if ((c is '.' or '/') && kind == CharKind.Digit && char.IsAsciiDigit(next)) { current.Append(c); continue; } // 1.5, 23/23
            var charKind = IsHebrewLetter(c) ? CharKind.Hebrew
                : char.IsAsciiDigit(c) ? CharKind.Digit
                : char.IsAsciiLetter(c) ? CharKind.Latin
                : CharKind.Separator;
            if (charKind == CharKind.Separator) { Flush(); continue; }
            // CamelCase and letter/digit boundaries split Latin identifiers: BasicCurb → BASIC CURB, arrow-D1 → ARROW D 1.
            var camel = charKind == CharKind.Latin && kind == CharKind.Latin && char.IsUpper(c) &&
                        (char.IsLower(prev) || (char.IsUpper(prev) && char.IsLower(next)));
            if (charKind != kind || camel) Flush();
            kind = charKind;
            current.Append(c);
        }
        Flush();
        return tokens;
    }

    private enum CharKind { Separator, Hebrew, Latin, Digit }

    private static Token LatinToken(string value)
    {
        var upper = value.ToUpperInvariant();
        return new Token(upper, upper, false);
    }

    private static Token HebrewToken(string value)
    {
        var exact = new StringBuilder(value.Length);
        foreach (var c in value)
            exact.Append(c switch { 'ך' => 'כ', 'ם' => 'מ', 'ן' => 'נ', 'ף' => 'פ', 'ץ' => 'צ', _ => c });
        var normal = exact.ToString();
        // The spelling skeleton collapses a doubled yod or vav only (חצייה → חציה, אופניים → אופנים, מקווקוו → מקוקו).
        var skeleton = new StringBuilder(normal.Length);
        for (var i = 0; i < normal.Length; i++)
            if (!(i > 0 && normal[i] is ('י' or 'ו') && normal[i] == normal[i - 1])) skeleton.Append(normal[i]);
        return new Token(normal, skeleton.ToString(), true);
    }

    private static bool IsHebrewLetter(char c) => c >= 'א' && c <= 'ת';

    /// <summary>Niqqud and cantillation marks (the maqaf U+05BE and sof pasuq U+05C3 separate words).</summary>
    private static bool IsHebrewMark(char c) => c is >= '֑' and <= 'ֽ' or 'ֿ' or 'ׁ' or 'ׂ' or 'ׄ' or 'ׅ' or 'ׇ';

    private static bool IsQuote(char c) => c is '"' or '\'' or '`' or '׳' or '״' or '’' or '”';
}
