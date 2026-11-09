using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// A road type with typical pavement structures for every surface kind. Used ONLY where neither the drawing's pavement
/// report nor the engineer gives a structure, and always shown as a default — it is never the project's design.
/// </summary>
public sealed record PavementDefaultClass(string Id, string Name, string Traffic, string Source, bool Inferred,
    IReadOnlyList<PavementReportStructure> Structures)
{
    /// <summary>The structure name on every BOQ row: says it is a default, and of which road type.</summary>
    public string StructureName => "ברירת מחדל · " + Name;

    /// <summary>The default structure for a surface kind; a bus lane without its own takes the carriageway's.</summary>
    public PavementReportStructure? StructureFor(RoadSurfaceKind surface)
    {
        PavementReportStructure? Use(string use) => Structures.FirstOrDefault(s => s.Use == use);
        return surface switch
        {
            RoadSurfaceKind.Road => Use("road"),
            RoadSurfaceKind.PublicTransport => Use("bus_lane") ?? Use("road"),
            RoadSurfaceKind.Shoulder => Use("shoulder") ?? Use("road"),
            RoadSurfaceKind.Sidewalk => Use("sidewalk"),
            RoadSurfaceKind.BikePath => Use("bike_path"),
            RoadSurfaceKind.Milling => Use("existing_road_overlay"),
            _ => null,
        };
    }
}

/// <summary>
/// Default pavement structures by road type (coworkers' call, 2026-11-09: the pavement report is optional; without it
/// the BOQ is calculated from defaults and says so everywhere).
///
/// Sources — real Israeli pavement design reports (the national catalogue "הנחיות לתכנון רחובות בערים כרך III A" could
/// not be read for this, so every road type names the report its layers are quoted from, or says it is inferred):
/// <list type="bullet">
/// <item>H — "מודיעין עילית – ציר העדפה, קטע מערבי", דו״ח 1560-25 מהדורה 1, 09/2026, עבור נתיבי ישראל
/// (Flex-Design-2, CBR 7%, 11 שנה): §5.2.1 מבנה 1 (W18 4.0–5.5M), §5.2.2 מבנה 2 (W18 0.8–3M), §5.2.3 מדרכה, §5.2.4 שביל, עמ׳ 16 קרצוף.</item>
/// <item>R — דורון אשל מהנדסים, "ראש העין – מתחם D – המלצות גיאוטכניות", דו״ח 4312-1, 04/2017 (20 שנה, CBR 8%): §6.2
/// עומסים, §7.3.1–7.3.3 מאסף / מקומי, §7.4 מדרכה, §7.5 שול, עמ׳ 14 חניה ושביל.</item>
/// <item>A — מ.נ.מ מהנדסים, "כביש 20 – נתיבים מהירים באיילון", דו״ח תכן מבנה מסעות, 07/2019 גרסה 3: טבלה 2.א, טבלה ב, עמ׳ 14, 19–20.</item>
/// <item>N — חתכים טיפוסיים של נתיבי ישראל, מחלף חפץ חיים (כביש 3933, DD-5002) — מקורב, מטקסט סרוק.</item>
/// </list>
/// Thicknesses are kept to those the NTI unified book (07/2026) prices per m², so every default row finds a priced item.
/// </summary>
public static class PavementDefaults
{
    public const string NoneId = "none";
    /// <summary>The road type a drawing starts from when nobody chose one: an urban collector (W18 ≈ 3M).</summary>
    public const string DefaultId = "urban-collector";

    /// <summary>Tack between asphalt layers, prime on the granular base (all sources' sections; NTI 51.04.1470 / 51.04.1480).</summary>
    public static readonly PavementReportCoats Coats = new(true, true, "ריסוס מאחה בין שכבות האספלט, ריסוס יסוד על המצע");

    // ── layers ──
    private static PavementReportLayer A(double cm, double mm, string binder, bool basalt = false) =>
        new(cm, $"תא\"צ {mm:0.#} {(basalt ? "בזלתי" : "גירי/דולומיטי")} {binder}", "asphalt", mm, binder, basalt ? "בזלת" : "גיר/דולומיט");
    private static PavementReportLayer BaseA(double cm) => new(cm, "מצע סוג א'", "base", BaseGrade: "A");
    private static PavementReportLayer Pavers(double cm) => new(cm, "אבנים משתלבות", "paving");
    private static PavementReportLayer Sand(double cm) => new(cm, "חול להנחה", "sand");

    private static PavementReportStructure S(string cls, string use, params PavementReportLayer[] layers) =>
        new($"default:{cls}:{use}", cls, use, null, use, "full", null, layers, null, layers.Sum(l => l.ThicknessCm), true, null);
    private static PavementReportStructure Overlay(string cls, params PavementReportLayer[] layers) =>
        new($"default:{cls}:overlay", cls, "existing_road_overlay", null, "existing_road_overlay", "overlay", layers.Sum(l => l.ThicknessCm),
            layers, null, layers.Sum(l => l.ThicknessCm), true, null);

    // Shared structures.
    private static PavementReportStructure SidewalkR(string c) => S(c, "sidewalk", Pavers(6), Sand(4), BaseA(20));          // R §7.4
    private static PavementReportStructure SidewalkH(string c) => S(c, "sidewalk", Pavers(6), Sand(3), BaseA(30));          // H §5.2.3
    private static PavementReportStructure SidewalkAsphalt(string c) => S(c, "sidewalk", A(4, 12.5, "PG68-10"), BaseA(20)); // A עמ׳ 14
    private static PavementReportStructure Bike(string c) => S(c, "bike_path", A(3, 12.5, "PG70-10"), A(5, 19, "PG68-10"), BaseA(30)); // H §5.2.4, R עמ׳ 14
    /// <summary>H §5.2.1 structure 1 — also adopted there for the most loaded bus lane.</summary>
    private static PavementReportLayer[] Structure1() => new[] { A(5, 19, "PG70-10"), A(5, 19, "PG70-10"), A(7, 25, "PG68-10"), A(7, 25, "PG68-10"), A(7, 25, "PG68-10") };
    private static PavementReportLayer[] Structure2() => new[] { A(4, 19, "PG70-10"), A(5, 19, "PG70-10"), A(6, 25, "PG68-10"), A(6, 25, "PG68-10"), A(7, 25, "PG68-10") };
    private static PavementReportStructure BusLaneH(string c) => S(c, "bus_lane", Structure1());

    public static IReadOnlyList<PavementDefaultClass> Classes { get; } = new[]
    {
        new PavementDefaultClass("urban-local", "רחוב מקומי / מגורים", "תנועה נמוכה, כ־0.2 מיליון ציריים שקולים של 8.2 ט׳ (W18)",
            "דו״ח ראש העין מתחם D (דורון אשל, 4312-1, 2017) §7.3.3; מדרכה §7.4; שול §7.5 — כמבנה המיסעה; נת״צ ושביל — דו״ח 1560-25 ציר העדפה מודיעין עילית (2026) §5.2.1, §5.2.4; קרצוף — מוסק", false, new[]
            {
                S("urban-local", "road", A(4, 19, "PG70-10"), A(6, 25, "PG68-10"), BaseA(35)),
                BusLaneH("urban-local"), SidewalkR("urban-local"), Bike("urban-local"),
                Overlay("urban-local", A(5, 19, "PG70-10")),
            }),
        new PavementDefaultClass("urban-collector", "רחוב מאסף", "תנועה בינונית, כ־3 מיליון ציריים שקולים (W18)",
            "דו״ח ראש העין מתחם D (דורון אשל, 4312-1, 2017) §7.3.1–7.3.2; מדרכה §7.4; נת״צ, שביל וקרצוף (מבנה 2: 4+5+6) — דו״ח 1560-25 ציר העדפה מודיעין עילית (2026) §5.2.1, §5.2.4, עמ׳ 16", false, new[]
            {
                S("urban-collector", "road", A(4, 19, "PG70-10"), A(5, 25, "PG68-10"), A(6, 25, "PG68-10"), BaseA(40)),
                BusLaneH("urban-collector"), SidewalkR("urban-collector"), Bike("urban-collector"),
                Overlay("urban-collector", A(4, 19, "PG70-10"), A(5, 19, "PG70-10"), A(6, 25, "PG68-10")),
            }),
        new PavementDefaultClass("urban-collector-full-depth", "רחוב מאסף — מבנה אספלט מלא", "כ־0.8–3 מיליון ציריים שקולים (W18), אספלט על תשתית מטופלת (ללא מצע)",
            "דו״ח 1560-25 ציר העדפה מודיעין עילית (נתיבי ישראל, 2026), §5.2.2 מבנה 2 = 28 ס״מ; מדרכה §5.2.3; שביל §5.2.4; קרצוף עמ׳ 16", false, new[]
            {
                S("urban-collector-full-depth", "road", Structure2()),
                BusLaneH("urban-collector-full-depth"), SidewalkH("urban-collector-full-depth"), Bike("urban-collector-full-depth"),
                Overlay("urban-collector-full-depth", A(4, 19, "PG70-10"), A(5, 19, "PG70-10"), A(6, 25, "PG68-10")),
            }),
        new PavementDefaultClass("urban-arterial", "רחוב עורקי ראשי / ציר העדפה", "כ־4–5.5 מיליון ציריים שקולים (W18), אספלט מלא על תשתית מטופלת (ללא מצע)",
            "דו״ח 1560-25 ציר העדפה מודיעין עילית (נתיבי ישראל, 2026), §5.2.1 מבנה 1 = 31 ס״מ (גם לנת״צ); מדרכה §5.2.3; שביל §5.2.4; קרצוף 5+5+7 עמ׳ 16", false, new[]
            {
                S("urban-arterial", "road", Structure1()),
                BusLaneH("urban-arterial"), SidewalkH("urban-arterial"), Bike("urban-arterial"),
                Overlay("urban-arterial", A(5, 19, "PG70-10"), A(5, 19, "PG70-10"), A(7, 25, "PG68-10")),
            }),
        new PavementDefaultClass("urban-industrial", "רחוב באזור תעשייה / תנועת משאיות", "תנועה כבדה עירונית — לא נמצא מקור ייעודי; נלקח מבנה הרחוב העורקי",
            "מוסק: מבנה 1 של דו״ח 1560-25 ציר העדפה מודיעין עילית (2026) §5.2.1; מדרכה מאבנים 8 ס״מ לפי ראש העין 4312-1 עמ׳ 14 (מעברי רכב)", true, new[]
            {
                S("urban-industrial", "road", Structure1()),
                BusLaneH("urban-industrial"), S("urban-industrial", "sidewalk", Pavers(8), Sand(4), BaseA(20)), Bike("urban-industrial"),
                Overlay("urban-industrial", A(5, 19, "PG70-10"), A(5, 19, "PG70-10"), A(7, 25, "PG68-10")),
            }),
        new PavementDefaultClass("urban-paved-street", "רחוב משולב / מרוצף באבנים משתלבות", "תנועה נמוכה, כ־0.2 מיליון ציריים שקולים (W18) — מיסעה מאבנים משתלבות 8 ס״מ",
            "דו״ח ראש העין מתחם D (דורון אשל, 4312-1, 2017) §7.3.3 חלופת אבנים = 41 ס״מ; מדרכה §7.4; שביל — דו״ח 1560-25 §5.2.4; קרצוף — מוסק", false, new[]
            {
                S("urban-paved-street", "road", Pavers(8), Sand(3), BaseA(30)),
                SidewalkR("urban-paved-street"), Bike("urban-paved-street"),
                Overlay("urban-paved-street", A(5, 19, "PG70-10")),
            }),
        new PavementDefaultClass("parking", "חניון / מפרצי חניה מרוצפים", "חניה ושבילי רכב — אבנים משתלבות 8 ס״מ, סה״כ 32 ס״מ",
            "דו״ח ראש העין מתחם D (דורון אשל, 4312-1, 2017) עמ׳ 14 (סה״כ 32 ס״מ; חלוקה 8+4+20 מוסקת); מדרכה §7.4; שביל — דו״ח 1560-25 §5.2.4", true, new[]
            {
                S("parking", "road", Pavers(8), Sand(4), BaseA(20)),
                SidewalkR("parking"), Bike("parking"),
                Overlay("parking", A(5, 19, "PG70-10")),
            }),
        new PavementDefaultClass("regional-low", "דרך אזורית / בינעירונית בתנועה נמוכה", "לא נמצא מקור ייעודי — נלקח מבנה רחוב מאסף, כ־3 מיליון ציריים שקולים (W18)",
            "מוסק: דו״ח ראש העין 4312-1 (2017) §7.3.1; שול — כמבנה המיסעה (שם §7.5); מדרכת אספלט — דו״ח כביש 20 איילון (מ.נ.מ, 2019) עמ׳ 14", true, new[]
            {
                S("regional-low", "road", A(4, 19, "PG70-10"), A(5, 25, "PG68-10"), A(6, 25, "PG68-10"), BaseA(40)),
                SidewalkAsphalt("regional-low"), Bike("regional-low"),
                Overlay("regional-low", A(5, 19, "PG70-10")),
            }),
        new PavementDefaultClass("interurban-main", "דרך בינעירונית ראשית", "חתך טיפוסי של נתיבי ישראל למיסעה חדשה ללא אגו״מ — מקורב",
            "חתכים טיפוסיים נתיבי ישראל, מחלף חפץ חיים כביש 3933 (DD-5002) — שכבה נושאת בזלתית PG70-10, תא״צ 25 PG70-10 / PG68-10, 2×20 מצע א׳ (נקרא מטקסט סרוק, עובי השכבות מקורב); שול — כמבנה המיסעה; מדרכה — איילון עמ׳ 14", true, new[]
            {
                S("interurban-main", "road", A(5, 19, "PG70-10", basalt: true), A(6, 25, "PG70-10"), A(7, 25, "PG68-10"), BaseA(40)),
                SidewalkAsphalt("interurban-main"), Bike("interurban-main"),
                Overlay("interurban-main", A(5, 19, "PG70-10", basalt: true)),
            }),
        new PavementDefaultClass("freeway", "כביש מהיר / תנועה כבדה מאוד", "נתיבים חיצוניים של כביש 20, כ־56–251 מיליון ציריים שקולים (W18)",
            "דו״ח תכן מבנה מסעות כביש 20 – נתיבים מהירים באיילון (מ.נ.מ, 2019) טבלה ב (מיסעה, 4 SMA + 5 תא״מ 25 + 16–20 תא״צ 25 + 57–64 מצע א׳) וטבלה 2.א (נת״צ/נתיב מהיר 9.3M); קרצוף 4 ס״מ עמ׳ 19–20. שכבת SMA מוחלפת כאן בתא״צ 19 בזלתי — שכבה שמתומחרת במחירון", false, new[]
            {
                S("freeway", "road", A(5, 19, "PG76-10", basalt: true), A(5, 25, "PG70-10"), A(7, 25, "PG68-10"), A(7, 25, "PG68-10"), A(6, 25, "PG68-10"), BaseA(60)),
                S("freeway", "bus_lane", A(4, 19, "PG70-10", basalt: true), A(5, 25, "PG70-10"), A(5, 25, "PG68-10"), A(5, 25, "PG68-10"), BaseA(46)),
                SidewalkAsphalt("freeway"), Bike("freeway"),
                Overlay("freeway", A(4, 19, "PG70-10", basalt: true)),
            }),
    };

    /// <summary>The road type of an id: null id (never chosen) = <see cref="DefaultId"/>; <see cref="NoneId"/> = no defaults; an
    /// id this version no longer knows = <see cref="DefaultId"/>.</summary>
    public static PavementDefaultClass? Resolve(string? id) =>
        id == NoneId ? null : Classes.FirstOrDefault(c => c.Id == id) ?? Classes.First(c => c.Id == DefaultId);

    private sealed record Stored(int SchemaVersion, string Class);
    /// <summary>The saved choice of a scope (drawing or marked area), or null when none was saved.</summary>
    public static string? LoadChoice(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is { SchemaVersion: 1, Class.Length: > 0 } ? stored.Class : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    public static void SaveChoice(string path, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(1, id))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
