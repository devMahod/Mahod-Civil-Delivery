using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.EngineerDraft;

/// <summary>Where a scanned source drawing sits in the project: new work, existing survey, or existing utilities.</summary>
public enum DraftSourceRole { Design, Survey, ExistingUtilities, Host }

/// <summary>What a measured layer is, for a new-works roads bill of quantities.</summary>
public enum DraftLayerRole { Design, Existing, Utility, DesignUtility, DraftingAid, CorridorVolume, ExcludedByDecision }

/// <summary>
/// How much engineering judgment a library line carries. Every line in the draft
/// is a proposal; nothing here is an approval.
/// </summary>
public enum DraftConfidence
{
    /// <summary>The only matching catalog item for a clearly named element.</summary>
    Direct,
    /// <summary>A structure, parameter, item choice or layer interpretation the engineer must confirm.</summary>
    Assumption,
    /// <summary>The quantity is measured but the item or structure needs an engineering decision.</summary>
    Decision,
}

/// <summary>Which measured records feed a library element.</summary>
public enum DraftQuantityBasis
{
    /// <summary>Hatch areas (including strictly recovered hatch boundaries).</summary>
    HatchArea,
    /// <summary>Open lines, arcs, polylines and closed-polyline perimeters.</summary>
    LengthWithClosedPerimeters,
    /// <summary>Open lines, arcs and polylines only.</summary>
    OpenLength,
    /// <summary>Block inserts.</summary>
    Count,
}

/// <summary>
/// An editable assumption referenced by BoQ quantity formulas. <see cref="Min"/>..<see cref="Max"/> is the range the
/// workbook accepts: a blank, text or out-of-range value is never read as a number (it makes dependent rows #N/A).
/// </summary>
public sealed record DraftParameter(string Key, string Label, double DefaultValue, string Unit, string Basis, double Min, double Max)
{
    public bool Accepts(double value) => double.IsFinite(value) && value >= Min && value <= Max;
}

/// <summary>
/// One catalog line produced from a base measurement:
/// quantity = base × Factor × P(ParameterKey) × P(SecondParameterKey) × (1 − P(ComplementKey)) × global factor.
/// </summary>
public sealed record DraftEmit(
    string Code,
    double Factor = 1.0,
    string? ParameterKey = null,
    string? Note = null,
    string? SecondParameterKey = null,
    string? ComplementKey = null)
{
    public IEnumerable<string> ParameterKeys =>
        new[] { ParameterKey, SecondParameterKey, ComplementKey }.Where(k => k != null).Select(k => k!);
}

/// <summary>A library element: which design layers (and blocks) it measures and which catalog lines it proposes.</summary>
public sealed record DraftRule(
    string Id,
    string Element,
    IReadOnlyList<string> LayerPatterns,
    DraftQuantityBasis Basis,
    DraftConfidence Confidence,
    IReadOnlyList<DraftEmit> Emits,
    string Note,
    string? PrimarySourcePattern = null,
    IReadOnlyList<string>? BlockPatterns = null,
    bool SeparateBoqRow = false,
    bool IncludedByDefault = true,
    bool SplitByDrawnWidth = false,
    string? Variant = null,
    string? HeldBackNote = null)
{
    // HeldBackNote: why a rule that is not included by default waits (default: suspected double drawing). Explanatory
    // text only — like Note, it takes no part in LibraryIdentity.RuleFingerprint.
    /// <summary>The element name with its drawn-width variant, when the rule was split by width.</summary>
    public string DisplayName => Variant == null ? Element : $"{Element} — {Variant}";
}

/// <summary>A scope item the drawing cannot measure (demolition, milling, earthworks): listed with an input quantity cell.</summary>
public sealed record DraftScopeItem(string Code, string Element, string Note);

/// <summary>
/// The discipline wording of the engineering draft (Codex 01:27, 02/10): a landscape draft never carries roads headings.
/// Explanatory text only — it takes no part in the library hash or in a rule fingerprint.
/// </summary>
public sealed record DraftWorkbookTexts
{
    /// <summary>The BoQ sheet heading in brackets, e.g. "מבנה 01 — כבישים ותנועה".</summary>
    public required string Structure { get; init; }
    /// <summary>The library inside a sentence, e.g. "ספריית הכבישים".</summary>
    public required string LibraryName { get; init; }
    /// <summary>The bill these works belong to, inside a sentence, e.g. "בכתב הכמויות לכבישים".</summary>
    public required string BillName { get; init; }
    /// <summary>The introduction of the drafting-aids sheet.</summary>
    public required string AidsIntro { get; init; }
    /// <summary>Examples of the parameters the engineer confirms (summary, step 2).</summary>
    public required string ParameterExamples { get; init; }
    /// <summary>Examples of the scope rows (used only when the library lists scope items).</summary>
    public required string ScopeExamples { get; init; }
    /// <summary>What the scan does not estimate, after the scope rows sentence.</summary>
    public required string ScopeNote { get; init; }
    /// <summary>What the reference estimate behind the proposals is, and what was not taken from it.</summary>
    public required string ReferenceEstimate { get; init; }
    /// <summary>What the library prices and what it leaves to other bills.</summary>
    public required string OtherSystems { get; init; }
    /// <summary>The cross-layer overlap example, the elements it affects and what to check.</summary>
    public required string OverlapExample { get; init; }
    public required string OverlapAffects { get; init; }
    public required string OverlapAction { get; init; }
    /// <summary>The global factor of the reference estimate, shown as a sensitivity only; null = none.</summary>
    public double? ReferenceGlobalFactor { get; init; }
}

/// <summary>
/// A versioned set of proposals that turns measured design layers into a draft
/// bill of quantities. It never approves a mapping, never writes a profile and never
/// changes a measured quantity; the engineer edits the resulting workbook.
/// </summary>
public sealed class EngineerBoqLibrary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Basis { get; init; }
    public required IReadOnlyList<DraftParameter> Parameters { get; init; }
    public required IReadOnlyList<DraftRule> Rules { get; init; }
    public required IReadOnlyList<DraftScopeItem> ScopeItems { get; init; }
    public required IReadOnlyList<string> SurveySourcePatterns { get; init; }
    public required IReadOnlyList<string> UtilitySourcePatterns { get; init; }
    public required IReadOnlyList<string> DraftingAidLayerPatterns { get; init; }
    public required IReadOnlyList<string> ExistingLayerPatterns { get; init; }
    /// <summary>
    /// Marking layers whose polylines carry their painted width in the drawing. Their lines
    /// are grouped by drawn width: 10 cm, 15 cm, or wider (measured as painted area).
    /// </summary>
    public required IReadOnlyList<string> WidthClassifiedLayerPatterns { get; init; }
    /// <summary>Parameters that stand for a painted width; a drawn width replaces them.</summary>
    public required IReadOnlyList<string> WidthParameterKeys { get; init; }
    /// <summary>The discipline wording of the draft workbook (no default: every library states its own).</summary>
    public required DraftWorkbookTexts Texts { get; init; }

    /// <summary>
    /// The library a profile's declared discipline selects (schema 6, Codex 01:27): none = the roads library exactly as
    /// before; "roads" or "landscape" = that library. Any other value is refused, never read as roads.
    /// </summary>
    public static EngineerBoqLibrary For(ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Estimate?.Discipline switch
        {
            null => RoadsV1,
            "roads" => RoadsV1,
            "landscape" => LandscapeV1,
            var other => throw new InvalidOperationException(
                $"estimate.discipline '{other}' is not a known discipline (roads / landscape); the profile is refused, never read as roads."),
        };
    }

    /// <summary>
    /// The library line without its edition suffix: mahod-roads-nti-urban-v1.4 → mahod-roads-nti-urban. A family decision
    /// stays with its line across editions (the rule fingerprint guards meaning) and is stale in another line.
    /// </summary>
    public static string Lineage(string? id)
    {
        var text = (id ?? string.Empty).Trim();
        var match = Regex.Match(text, @"^(?<line>.+?)-v\d+(?:\.\d+)*$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["line"].Value : text;
    }

    /// <summary>Two-component marking, "tears" texture: 10 cm line per metre, 15 cm line per metre, painted area per m².</summary>
    public const string Line10Code = "U51.32.0210";
    public const string Line15Code = "U51.32.0240";
    public const string PaintedAreaCode = "U51.32.0290";

    /// <summary>Infrastructure domains (family id, Hebrew name, built-in utility labels). Families only — never items.</summary>
    public static readonly IReadOnlyList<(string Id, string Domain, string[] Labels)> UtilityDomains = new[]
    {
        ("utility-water", "מים", new[] { "מים", "מקורות" }),
        ("utility-sewer", "ביוב", new[] { "ביוב", "קולחין" }),
        ("utility-drainage", "ניקוז", new[] { "ניקוז" }),
        ("utility-electric", "חשמל", new[] { "חשמל" }),
        ("utility-lighting", "תאורה", new[] { "תאורה" }),
        ("utility-traffic-signals", "רמזורים", new[] { "רמזור" }),
        ("utility-telecom", "תקשורת", new[] { "בזק", "תקשורת", "בזק/תקשורת", "HOT" }),
        ("utility-gas", "גז", new[] { "גז" }),
    };

    /// <summary>
    /// Drainage names of Mahod's DR design model that the section utility rules do not carry (measured in 6422-DR,
    /// 28.09.2026): pipe layers DR-PIPE-40/50/60/80 and DR-PPIPE-100, and structure blocks on DR-MNHL-BL (koltan3,
    /// shuha140_140). Library only — the sections keep their own rules. They join the drainage decision families:
    /// the size in the name is shown without a unit, and no item, class, depth or price is proposed.
    /// </summary>
    public static readonly IReadOnlyList<string> DrainageModelLinePatterns = new[] { "DR-PIPE-*", "DR-PPIPE-*" };

    /// <summary>Drainage structure layers of the DR model (see <see cref="DrainageModelLinePatterns"/>).</summary>
    public static readonly IReadOnlyList<string> DrainageModelStructurePatterns = new[] { "DR-MNHL*" };

    /// <summary>True for a DR-model name (layer or block, e.g. DR-BL-TX-2, DR-MNHL_120-100): its sizes carry no unit.</summary>
    public static bool IsDrainageModelName(string? name) => Glob(name, "DR-*");

    /// <summary>True for the infrastructure domain families (no items, decision only).</summary>
    public static bool IsUtilityFamily(string? id) => id != null && id.StartsWith("utility-", StringComparison.Ordinal);

    /// <summary>The key of the global quantity factor parameter (the reference estimate used 0.9 on every line; unconfirmed).</summary>
    public const string GlobalFactorKey = "GLOBAL_QTY_FACTOR";

    /// <summary>Case-insensitive glob with * and ?; an exact name is a glob without wildcards.</summary>
    public static bool Glob(string? text, string pattern)
    {
        if (text == null) return false;
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool AnyGlob(string? text, IEnumerable<string> patterns) => patterns.Any(p => Glob(text, p));

    /// <summary>First rule whose layer patterns match; a rule with block patterns also needs a matching block.</summary>
    public DraftRule? RuleFor(string layerLeaf, string? blockLeaf = null) => Rules.FirstOrDefault(rule =>
        AnyGlob(layerLeaf, rule.LayerPatterns) &&
        (rule.BlockPatterns == null || (blockLeaf != null && AnyGlob(blockLeaf, rule.BlockPatterns))));

    /// <summary>
    /// Roads and traffic, NTI urban price list (codes U..). Layer names follow the Mahod
    /// design-model conventions measured in 6422 (HA hatches, GM geometry, SM signs and
    /// marking). Every structure, share and item choice is a proposal for engineering review.
    /// </summary>
    public static EngineerBoqLibrary RoadsV1 { get; } = BuildRoadsV1();

    /// <summary>
    /// Landscape development and planting, NTI unified price list 07/2026 (Codex 01:27, 02/10). Layer names follow the
    /// landscape drawing of project 293. Proposals only: a candidate item learned from that project's landscape BoQ is held
    /// back (not in the total) until an engineer decides; trees, fences and preserved areas carry no item. Neutral global
    /// factor 1 — the roads reference factor does not apply here.
    /// </summary>
    public static EngineerBoqLibrary LandscapeV1 { get; } = BuildLandscapeV1();

    private static EngineerBoqLibrary BuildLandscapeV1()
    {
        // Codex 02:31: the default is 0; switching 'לכלול' to 1 is a scenario for every recipe line, not a recorded approval.
        const string heldBack = "פריט מועמד מכתב הכמויות לנוף של פרויקט 293 — לא אושר הנדסית. ברירת המחדל: מחוץ לסכום (כמות 0). " +
                                "שינוי 'לכלול' ל-1 מוסיף תרחיש לכל פריטי המתכון ואינו אישור מתועד";
        var parameters = new List<DraftParameter>
        {
            new(GlobalFactorKey, "מקדם כמויות לשורות אורך, שטח ונפח", 1.0, "מקדם",
                "ברירת מחדל 1.0 (ללא שינוי). חל על כמויות אורך, שטח ונפח בלבד — לא על פריטים שנספרו. אין מקדם ייחוס לנוף; לא לשנות בלי החלטה הנדסית.", 0.5, 1.5),
        };

        var rules = new List<DraftRule>
        {
            new("la-planting-area", "שטחי גינון (הצללה)", new[] { "La-hatch-SP" }, DraftQuantityBasis.HatchArea,
                DraftConfidence.Decision, new[]
                {
                    new DraftEmit("40.01.0010", Note: "חיפוי אדמה 30 ס\"מ — מועמד מכתב הכמויות של 293, לא אושר"),
                    new DraftEmit("41.01.0020", Note: "הכשרת קרקע לגינון — יישור גנני — מועמד מכתב הכמויות של 293, לא אושר"),
                    new DraftEmit("41.01.5110", Factor: 0.001, Note: "הדברת עשבייה, בדונם (מ\"ר × 0.001) — מועמד מכתב הכמויות של 293, לא אושר"),
                },
                "השטח נמדד מהצללות השכבה. שלושת הפריטים הם מתכון מועמד שנלמד מכתב הכמויות של פרויקט 293 ולא אושר: בברירת המחדל אינם בסכום, " +
                "והכללה בגיליון היא תרחיש ולא אישור. " +
                "שם השכבה אינו אישור לפריט; פוליליינים סגורים באותה שכבה אינם חלק מהשטח.",
                IncludedByDefault: false, HeldBackNote: heldBack),
            new("la-preserve-area", "שטחים לשימור (הצללה)", new[] { "La-hatch-save" }, DraftQuantityBasis.HatchArea,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "השטח נמדד; מה נדרש בשטח לשימור (ללא עבודה, גידור או טיפול) והפריט — להחלטה הנדסית. אין סעיף מוצע."),
            new("la-garden-curb", "אבן גן", new[] { "la-garden-curve" }, DraftQuantityBasis.LengthWithClosedPerimeters,
                DraftConfidence.Decision, new[] { new DraftEmit("51.06.0030", Note: "אבן גן 10/20 ס\"מ אפור — מועמד מכתב הכמויות של 293, לא אושר") },
                "האורך נמדד. הפריט מועמד שנלמד מהדוגמה של פרויקט 293 ולא אושר לכל שרטוט.",
                IncludedByDefault: false, HeldBackNote: heldBack),
            new("la-fence", "גדר (שכבת LA-FENC-TEMP)", new[] { "LA-FENC-TEMP" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "נמדד אורך הקווים הפתוחים. בשכבה גם פוליליינים סגורים (היקפם בגיליון 'מדידות חלופיות'), ובמקרא השכבה מופיעה גם במ\"א וגם במ\"ר — " +
                "הבסיס (קווים פתוחים / היקפים) והפריט להחלטה הנדסית. אין סעיף מוצע."),
            new("la-trees-rpl", "עצים בשכבה LA-TREE-RPL", new[] { "LA-TREE-RPL" }, DraftQuantityBasis.Count,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "נספרו בלוקים. שם הבלוק אינו קובע פעולה: שימור, העתקה או כריתה, וכן מין וגודל — להחלטה הנדסית ולהצהרת מפרט. אין סעיף מוצע."),
            new("la-trees-ella", "עצים בשכבה LA-TREE-Ella", new[] { "LA-TREE-Ella" }, DraftQuantityBasis.Count,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "נספרו בלוקים. מין וגודל לא נמצאו בשדות הבלוק שנקראו — נדרשת הצהרת מפרט לפני סעיף. אין סעיף מוצע."),
        };

        return new EngineerBoqLibrary
        {
            Id = "mahod-landscape-nti-v1.0",
            Title = "ספריית שיוך מהוד — פיתוח נופי וגינון (מחירון נת\"י מאוחד) v1.0",
            Basis = "הצעות בלבד. המקורות: שמות השכבות בשרטוט הנוף של פרויקט 293 וכתב הכמויות לנוף של אותו פרויקט. " +
                    "פריטים מועמדים אינם בסכום עד החלטה הנדסית; עצים, גדר ושטחי שימור ללא סעיף. שום שורה אינה אישור הנדסי.",
            Parameters = parameters,
            Rules = rules,
            ScopeItems = Array.Empty<DraftScopeItem>(),
            // "*-SP-*" is a survey model in the roads projects; in landscape names SP is not a survey mark (la-293-SP-…).
            SurveySourcePatterns = new[] { "*MEDVA*", "*SURVEY*", "*MEDIDA*" },
            UtilitySourcePatterns = new[] { "UT-*" },
            // Text, level and system layers only. La-help / La-ezer carry measured objects and stay visible as unmapped.
            DraftingAidLayerPatterns = new[] { "0", "0-*", "Defpoints", "_*", "*TABL", "*text*", "*TXT*", "La-level" },
            ExistingLayerPatterns = new[] { "*-EX", "*-EX-*", "*EXST*", "*EXIST*", "*EXSIT*", "*KAYAM*" },
            WidthClassifiedLayerPatterns = Array.Empty<string>(),
            WidthParameterKeys = Array.Empty<string>(),
            Texts = new DraftWorkbookTexts
            {
                Structure = "מבנה 01 — פיתוח נופי וגינון",
                LibraryName = "ספריית הנוף",
                BillName = "בכתב הכמויות לנוף",
                AidsIntro = "שכבות שמהנדס סימן 'לא כמות בנייה', ושכבות עזר של השרטוט: טקסטים, מפלסים, טבלאות ושכבות מערכת. לא נכללו בכתב הכמויות.",
                ParameterExamples = "מקדם הכמויות",
                ScopeExamples = "השקיה, שתילה ועבודות עפר",
                ScopeNote = "השקיה, שתילה לפי מין וגודל ועבודות עפר אינם נמדדים מהשכבות, ואין להם שורות בספרייה זו — להוסיף שורות ידניות לפי תכניות הגינון וההשקיה.",
                ReferenceEstimate = "כתב הכמויות לנוף של פרויקט 293. שימש רק כדי להציע פריטים מועמדים לשכבות (חיפוי אדמה, יישור גנני, הדברה, אבן גן). " +
                                    "אף הצעה לא אושרה, והן מחוץ לסכום בברירת המחדל. כמויות ומחירים בטיוטה זו אינם מועתקים ממנו.",
                OtherSystems = "הספרייה מציעה פריטים מועמדים לשטחי גינון ולאבן גן — מחוץ לסכום בברירת המחדל; הכללה בגיליון היא תרחיש, לא אישור. עצים, גדר ושטחי שימור מוצגים כשורות 'הגדרה' " +
                               "עם הכמות שנמדדה, בלי סעיף ובלי מחיר. השקיה ושתילה לפי מין וגודל אינן מתומחרות. תשתיות בתכנון ושכבות אחרות מפורטות בגיליון 'לא שויכו'.",
                OverlapExample = "הצללת גינון מעל הצללת שטח לשימור",
                OverlapAffects = "שטחי גינון ושימור",
                OverlapAction = "לבדוק בשרטוט שהצללות הגינון והשימור אינן מונחות זו על זו.",
                ReferenceGlobalFactor = null,
            },
        };
    }

    private static EngineerBoqLibrary BuildRoadsV1()
    {
        const string fullDepth = "FULL_DEPTH_SHARE";
        const string subbaseRoad = "SUBBASE_ROAD_M";
        const string subbaseSidewalk = "SUBBASE_SIDEWALK_M";
        const string sidewalkPavers = "SIDEWALK_PAVER_SHARE";
        const string signArea = "SIGN_AREA_M2";
        const string poleLength = "POLE_LENGTH_M";
        const string symbolArea = "SYMBOL_AREA_M2";
        const string bikeSymbolArea = "BIKE_SYMBOL_AREA_M2";
        const string dash33 = "DASH_RATIO_3_3";
        const string dash315 = "DASH_RATIO_3_15";
        const string dash11 = "DASH_RATIO_1_1";
        const string transverseWidth = "TRANSVERSE_MARK_WIDTH_M";
        const string crossingWidth = "CROSSING_WIDTH_M";
        const string crossingRatio = "CROSSING_PAINT_RATIO";

        var parameters = new List<DraftParameter>
        {
            new(GlobalFactorKey, "מקדם כמויות לשורות אורך, שטח ונפח", 1.0, "מקדם",
                "ברירת מחדל 1.0 (ללא שינוי). חל על כמויות אורך, שטח ונפח בלבד — לא על פריטים שנספרו (סככות, שלטים, עמודים). באומדן הייחוס של מהוד (פרויקט אחר) הכמויות הוכפלו ב-0.9; לא אושר לפרויקט זה.", 0.5, 1.5),
            new(fullDepth, "חלק המיסעה שנבנה מחדש לכל העומק (שאר השטח: קרצוף ושכבה עליונה)", 0.40, "שבר (0–1)",
                "הנחה מהשרטוט: בקורידורים 700/750 שכבות 6 ו-7 ס\"מ קיימות תחת כ-40% משטח השכבה העליונה (1,650 מתוך 4,111 מ\"ר). " +
                "בפרויקט חדש לגמרי יש להזין 1.0; בשיקום בלבד — 0.", 0, 1),
            new(subbaseRoad, "עובי מצע סוג א' בשטח הנבנה לכל העומק", 0.30, "מטר",
                "הנחה — לא נמדד בשרטוט. יש להזין את העובי מפרט המיסעה.", 0, 1.5),
            new(sidewalkPavers, "חלק המדרכות והאיים המרוצף באבנים משתלבות (השאר — אספלט)", 1.0, "שבר (0–1)",
                "גמר המדרכה אינו מופיע בשרטוט. 1 = אבנים משתלבות; 0 = אספלט למדרכות (כמו באומדן הייחוס).", 0, 1),
            new(subbaseSidewalk, "עובי מצע סוג א' מתחת למדרכות ולאיים", 0.20, "מטר",
                "הנחה — לא נמדד בשרטוט; יש לאשר לפי פרט המדרכה.", 0, 1),
            new(dash33, "יחס צבע נטו בקו מקווקו 3-3", 0.5, "שבר",
                "מתוך שם השכבה: 3 מ' צבע / 3 מ' רווח. הקו בשרטוט רציף עם סוג קו מקווקו.", 0, 1),
            new(dash315, "יחס צבע נטו בקו מקווקו 3-1.5", 0.667, "שבר",
                "מתוך שם השכבה: 3 מ' צבע / 1.5 מ' רווח.", 0, 1),
            new(dash11, "יחס צבע נטו בקו מקווקו 1-1", 0.5, "שבר",
                "מתוך שם השכבה: 1 מ' צבע / 1 מ' רווח.", 0, 1),
            new(transverseWidth, "רוחב קווי עצירה וסימונים רוחביים — רק כשהרוחב אינו משורטט", 0.5, "מטר",
                "גיבוי בלבד: כשלפוליליין יש רוחב בשרטוט, הכמות מחושבת לפי הרוחב המשורטט (אורך × רוחב); קו רוחבי שמשורטט ברוחב 10/15 ס\"מ מתומחר לפי מטר, עם בדיקה. " +
                "בשרטוט 6422 רוב קווי העצירה (810) משורטטים ברוחב 0.5 מ'.", 0.05, 5),
            new(crossingWidth, "רוחב מעבר חצייה — רק כשהרוחב אינו משורטט", 3.0, "מטר",
                "גיבוי בלבד. בשרטוט 6422 מעברי החצייה (TR-MARK-WHT-811) משורטטים כקו ברוחב 3 מ'.", 0.5, 10),
            new(crossingRatio, "חלק צבוע במעבר חצייה (פסים ומרווחים)", 0.5, "שבר",
                "בשרטוט המעבר משורטט בסוג קו DASHED1-1 — פס ומרווח שווים. יש לאשר לפי פרט הסימון.", 0, 1),
            new(symbolArea, "שטח ממוצע לחץ צבוע", 1.0, "מ\"ר לחץ",
                "ממוצע ההצללות של חצים שנמדדו בשרטוט (37 הצללות, כ-37 מ\"ר). לדיוק — לפי טבלת החצים במפרט 51.32.", 0, 20),
            new(bikeSymbolArea, "שטח סמל אופניים צבוע", 0.9, "מ\"ר לסמל",
                "75×120 ס\"מ לפי תיאור הפריט U51.32.0800 במחירון.", 0, 5),
            new(signArea, "שטח ממוצע לשלט", 1.0, "מ\"ר לשלט",
                "הנחה לפי יחס באומדן הייחוס (כ-63 מ\"ר שלטים ל-252 מ' עמודים); יש לאשר לפי סוגי התמרורים.", 0, 10),
            new(poleLength, "אורך עמוד תמרור", 4.0, "מטר לעמוד",
                "הנחה לפי יחס באומדן הייחוס; יש לאשר.", 0, 15),
        };

        var rules = new List<DraftRule>
        {
            // --- Pavement (HA hatches) ---------------------------------------------------------
            new("road-pavement", "מיסעה — כבישים", new[] { "HW-HTCH-ROAD" }, DraftQuantityBasis.HatchArea,
                DraftConfidence.Assumption, Pavement(fullDepth, subbaseRoad),
                "מבנה לפי קודי החומר בקורידורים שבשרטוט (ASF-5-19-70 / ASF-6-25-70 / ASF-7-25-68 / MAZA; S5 טרם שויך). " +
                "שכבה עליונה על כל השטח; שכבות תחתונות, מצע, הידוק וריסוס קל בין שכבות רק על החלק הנבנה לכל העומק; קרצוף וריסוס מאחה על השאר.",
                PrimarySourcePattern: "*-HA-*"),
            new("brt-pavement", "מיסעה — נתיב תחבורה ציבורית (נת\"צ)", new[] { "HW-HTCH-NATAZ" }, DraftQuantityBasis.HatchArea,
                DraftConfidence.Assumption, Pavement(fullDepth, subbaseRoad),
                "אותו מבנה כמו הכבישים. יש לוודא בשרטוט שהצללת הנת\"צ אינה מונחת מעל הצללת הכבישים — אחרת השטח נספר פעמיים.",
                PrimarySourcePattern: "*-HA-*"),
            new("sidewalk-paving", "מדרכות", new[] { "HW_HA_SIDEWALK", "HW-HATCH- SIDEWALK", "HW-HATCH-SIDEWALK" },
                DraftQuantityBasis.HatchArea, DraftConfidence.Assumption, Sidewalk(sidewalkPavers, subbaseSidewalk),
                "גמר המדרכה אינו מופיע בשרטוט — בחירה בגיליון הפרמטרים (אבנים משתלבות / אספלט)."),
            new("island-paving", "איי תנועה — גמר", new[] { "HW-HATCH-ILND" }, DraftQuantityBasis.HatchArea,
                DraftConfidence.Assumption, Sidewalk(sidewalkPavers, subbaseSidewalk),
                "גמר האיים אינו מופיע בשרטוט — אותה בחירה כמו במדרכות."),
            new("bike-path", "שבילי אופניים — מבנה", new[] { "PL-BIKE" }, DraftQuantityBasis.HatchArea,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "השטח נמדד; מבנה השביל (אספלט/ריצוף, מצע, ציפוי צבעוני U51.32.0720) להגדרה הנדסית."),
            new("landscape", "שטחי גינון", new[] { "HACTH-GINUN", "HW-HATCH-GARDEN", "HW-HATCH-GARDEN-PL" },
                DraftQuantityBasis.HatchArea, DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "השטח נמדד; פריטי גינון והשקיה לפי תכנית אדריכלות הנוף."),

            // --- Curbs (GM geometry model is the primary source) -----------------------------
            new("curb-road", "אבן שפה לכביש", new[] { "HW-CURB" }, DraftQuantityBasis.LengthWithClosedPerimeters,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.06.1900", Note: "חלופה: U51.06.2080 (חריש/טבעון 25/20), ששימשה ברוב אבני השפה באומדן הייחוס") },
                "כלל ברירת מחדל שנוצר בפרופיל ולא אושר. יש לבדוק שאבן השפה אינה משורטטת בשני קווים ושאבן מונמכת אינה משורטטת מעליה.",
                PrimarySourcePattern: "*-GM-*"),
            new("curb-island", "אבן שפה לאי תנועה (TR-ISLAND)", new[] { "TR-ISLAND-CURBSTONE" }, DraftQuantityBasis.LengthWithClosedPerimeters,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.06.2460", Note: "אבן 23/23 באורך 100 ס\"מ כמו באומדן הייחוס; חלופה לרדיוסים קטנים: U51.06.2140 (50 ס\"מ)") },
                "כולל היקפי איים סגורים (רוחב ממוצע 2.3–2.8 מ' — קווי מתאר של איים, לא של אבן בודדת).",
                PrimarySourcePattern: "*-GM-*"),
            new("curb-island-hw", "אבן שפה לאי תנועה (HW-CURB-ILND)", new[] { "HW-CURB-ILND" }, DraftQuantityBasis.LengthWithClosedPerimeters,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.06.2460") },
                "מוסכמת שכבה שנייה לאבן שפה של איים; יש לוודא שאינה משרטטת את אותם איים כמו TR-ISLAND-CURBSTONE.",
                PrimarySourcePattern: "*-GM-*", SeparateBoqRow: true),
            new("curb-inner-island", "אבן שפה לאי פנימי (חשד לכפילות)", new[] { "TR-INNER-ISLAND-CURBSTONE" },
                DraftQuantityBasis.LengthWithClosedPerimeters, DraftConfidence.Decision, new[] { new DraftEmit("U51.06.2460") },
                "אורכה כמעט זהה לשכבת אבן השפה של האיים — חשד לשרטוט כפול (פנים וחוץ של אותה אבן). מופיעה בשורה נפרדת " +
                "ואינה נכללת בסכום עד שבדיקה בשרטוט תראה שאלה אבנים נפרדות (לשנות 'לכלול' ל-1 בגיליון מדידות בסיס).",
                PrimarySourcePattern: "*-GM-*", SeparateBoqRow: true, IncludedByDefault: false),
            new("curb-garden", "אבן גן", new[] { "TR-GRDN-STONE", "TR-INNER-GRDN-STONE", "0L-EV-GAN" },
                DraftQuantityBasis.LengthWithClosedPerimeters, DraftConfidence.Assumption, new[] { new DraftEmit("U51.06.3060") },
                "אבן גן 10/20 אפור — הנחה לפי שם השכבה. גם כאן יש לבדוק שאין שרטוט כפול (TR-INNER).",
                PrimarySourcePattern: "*-GM-*"),
            new("curb-lowered", "אבן שפה מונמכת (מעברי חצייה)", new[] { "HW-EVEN_MUN" },
                DraftQuantityBasis.LengthWithClosedPerimeters, DraftConfidence.Assumption, new[] { new DraftEmit("U51.06.2800") },
                "פירוש שם השכבה (EVEN_MUN = אבן מונמכת). אם היא משורטטת מעל קו אבן השפה הרציף — יש להפחית את אורכה מאבן השפה לכביש.",
                PrimarySourcePattern: "*-GM-*"),
            new("curb-bike", "אבן שפה לשביל אופניים", new[] { "HW-BIKE-LANE" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.06.2930") },
                "כלל ברירת מחדל שנוצר בפרופיל ולא אושר, מצומצם לשכבת שפת השביל בלבד.",
                PrimarySourcePattern: "*-GM-*"),
            new("pavement-edge", "קו שפת מיסעה (HW-TRWY)", new[] { "HW-TRWY" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "קו גבול — בדרך כלל לא פריט תשלום. אם יש חיבור לאספלט קיים: ניסור U51.04.2450 או תוספת אבן שפה U51.06.3020.",
                PrimarySourcePattern: "*-GM-*"),
            new("sidewalk-edge", "קו גבול מדרכה (END-MDR-PL)", new[] { "END-MDR-PL" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "קו גבול — אם בגבול המדרכה מתוכננת אבן גן, לתמחר כ-U51.06.3060.",
                PrimarySourcePattern: "*-GM-*"),

            // --- Marking (SM signs-and-marking model is the primary source) ---------------------
            new("curb-painting", "צביעת אבני שפה", new[] { "TR-CURB-RED", "TR-CURB-YLV", "TR-CURB-BLK", "TR-CURB-*" },
                DraftQuantityBasis.LengthWithClosedPerimeters, DraftConfidence.Direct, new[] { new DraftEmit("U51.32.0700") },
                "פריט יחיד במחירון לצביעת אבני שפה.", PrimarySourcePattern: "*-SM-*"),
            new("marking-dash-3-3", "סימון קווים מקווקווים 3-3", new[] { "TR-MARK-*3-3*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Assumption, new[] { new DraftEmit(Line10Code, ParameterKey: dash33, Note: "אורך צבוע נטו = אורך הקו × יחס הצבע") },
                "הכמות היא האורך הצבוע בלבד. הרוחב נקבע לפי רוחב הפוליליין בשרטוט (10 ס\"מ, 15 ס\"מ, או שטח לקו רחב יותר).",
                PrimarySourcePattern: "*-SM-*", SplitByDrawnWidth: true),
            new("marking-dash-3-15", "סימון קווים מקווקווים 3-1.5", new[] { "TR-MARK-*3-1.5*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Assumption, new[] { new DraftEmit(Line10Code, ParameterKey: dash315, Note: "אורך צבוע נטו = אורך הקו × יחס הצבע") },
                "הכמות היא האורך הצבוע בלבד. הרוחב נקבע לפי רוחב הפוליליין בשרטוט.",
                PrimarySourcePattern: "*-SM-*", SplitByDrawnWidth: true),
            new("marking-dash-1-1", "סימון קווים מקווקווים 1-1", new[] { "TR-MARK-*(1-1)*", "TR-MARK-*_1-1*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Assumption, new[] { new DraftEmit(Line10Code, ParameterKey: dash11, Note: "אורך צבוע נטו = אורך הקו × יחס הצבע") },
                "הכמות היא האורך הצבוע בלבד. הרוחב נקבע לפי רוחב הפוליליין בשרטוט.",
                PrimarySourcePattern: "*-SM-*", SplitByDrawnWidth: true),
            new("marking-crossings", "מעברי חצייה (811)", new[] { "TR-MARK-WHT-811*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Assumption,
                new[] { new DraftEmit(PaintedAreaCode, ParameterKey: crossingWidth, SecondParameterKey: crossingRatio, Note: "שטח צבוע = אורך × רוחב המעבר × חלק צבוע") },
                "הרוחב נלקח מרוחב הפוליליין בשרטוט; פרמטר הרוחב משמש רק לקווים בלי רוחב. הצללות באותה שכבה לא נוספו כדי לא לספור פעמיים.",
                PrimarySourcePattern: "*-SM-*", SplitByDrawnWidth: true),
            new("marking-crossing-lines", "קווי מעברי חצייה ללא רוחב (TR-MARK-811)", new[] { "TR-MARK-811*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "קווים ללא רוחב בשכבה נפרדת, לצד מעברי החצייה המשורטטים ברוחב 3 מ' בשכבה TR-MARK-WHT-811 — חשד לייצוג כפול של אותם מעברים. " +
                "לא נכלל בסכום; אם אלה מעברים נוספים — לתמחר כ-U51.32.0290 לפי שטח.",
                PrimarySourcePattern: "*-SM-*"),
            new("marking-transverse", "קווי עצירה וסימונים רוחביים (810/815)", new[] { "TR-MARK-WHT-810*", "TR-MARK-WHT-815*" },
                DraftQuantityBasis.OpenLength, DraftConfidence.Assumption,
                new[] { new DraftEmit(PaintedAreaCode, ParameterKey: transverseWidth, Note: "שטח = אורך × רוחב הסימון") },
                "סימונים רוחביים מתומחרים בשטח. הרוחב נלקח מרוחב הפוליליין בשרטוט; פרמטר הרוחב משמש רק לקווים בלי רוחב.",
                PrimarySourcePattern: "*-SM-*", SplitByDrawnWidth: true),
            new("marking-lines", "סימון קווים", new[] { "TR-MARK-WHT-*", "TR-MARK-YLW-*" },
                DraftQuantityBasis.OpenLength, DraftConfidence.Assumption, new[] { new DraftEmit(Line10Code) },
                "קו ניתוב לפי רוחב הפוליליין בשרטוט: 10 ס\"מ, 15 ס\"מ, או שטח צבוע לקו רחב יותר. קו בלי רוחב משורטט תומחר כ-10 ס\"מ. קווים כפולים (806) — U51.32.0270.",
                PrimarySourcePattern: "*-SM-*", SplitByDrawnWidth: true),
            new("marking-804", "סימון 804 (-ZEVA-804)", new[] { "-ZEVA-*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "סימון 804 הוא סמל אופניים (U51.32.0120, לפי מ\"ר) ולא קו — יש לבדוק מה משורטט בשכבה.",
                PrimarySourcePattern: "*-SM-*"),
            new("bike-arrows", "חצי אופניים צבועים", new[] { "*" }, DraftQuantityBasis.Count,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.32.0810") },
                "בלוקי arrow-D / arrow-D1 / BL-ARW-W-Y. אין מחיר לפריט במהדורת המחירון — נדרשת הצעת מחיר.",
                PrimarySourcePattern: "*-SM-*", BlockPatterns: new[] { "arrow-D*", "BL-ARW-W-Y*" }),
            new("bike-symbols", "סמלי אופניים צבועים", new[] { "TR-SIGN-STAG-BL" }, DraftQuantityBasis.Count,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.32.0120", ParameterKey: bikeSymbolArea, Note: "שטח = מספר סמלים × שטח לסמל") },
                "בלוקי BIKE (202) — כמספר חצי האופניים, כמקובל בזוג סמל-חץ. יש לאשר את פירוש הבלוק.",
                PrimarySourcePattern: "*-SM-*", BlockPatterns: new[] { "BIKE*" }),
            new("marking-arrows", "חצים צבועים על הכביש", new[] { "TR-MARK-ARW*", "BL-TR-ARRW", "BL-TR-MARK-*", "TR-MARK-WHT-810*" },
                DraftQuantityBasis.Count, DraftConfidence.Assumption,
                new[] { new DraftEmit("U51.32.0290", ParameterKey: symbolArea, Note: "שטח = מספר חצים × שטח לחץ") },
                "חצי תנועה (813/814, TR-ARW*).", PrimarySourcePattern: "*-SM-*",
                BlockPatterns: new[] { "813*", "TR-ARW*", "TR-ARRW*" }),
            new("marking-m-blocks", "בלוקי M צהובים", new[] { "TR-MARK-ARW*" }, DraftQuantityBasis.Count,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "477 בלוקים בשם M בשכבת חצים צהובים — משמעותם אינה ידועה; להחלטה.",
                PrimarySourcePattern: "*-SM-*", BlockPatterns: new[] { "M" }),

            // --- Signs, bus stops, furniture ------------------------------------------------------
            new("sign-plates", "שלטים ותמרורים (לפי עמודים)", new[] { "TR-SIGN-POLE-BL", "TR-POLE" }, DraftQuantityBasis.Count,
                DraftConfidence.Assumption,
                new[]
                {
                    new DraftEmit("U51.31.0010", ParameterKey: signArea, Note: "שטח שלטים = מספר עמודים × שטח לשלט (לפחות שלט אחד לעמוד)"),
                    new DraftEmit("U51.31.0410", ParameterKey: poleLength, Note: "אורך עמודים = מספר עמודים × אורך עמוד"),
                },
                "נספרו בלוקי עמוד (SIGN). ייתכנו כמה שלטים על עמוד אחד — לבדוק.", PrimarySourcePattern: "*-SM-*"),
            new("sign-faces", "סמלי תמרורים (לזיהוי סוגים)", new[] { "TR-SIGN-BL" }, DraftQuantityBasis.Count,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "בלוקי סוג תמרור (TR-SIGN-303, 301, 213 וכו') — לעזרה בבחירת השלטים; הכמות לתמחור לפי העמודים.",
                PrimarySourcePattern: "*-SM-*"),
            new("bus-stop-signs", "שלטי תחנות אוטובוס (505/506)", new[] { "505" }, DraftQuantityBasis.Count,
                DraftConfidence.Assumption, new[] { new DraftEmit("U40.02.2430") },
                "בלוקי 505(506). אין מחיר לפריט במהדורת המחירון — נדרשת הצעת מחיר.", PrimarySourcePattern: "*-SM-*"),
            new("bus-shelters", "סככות תחנות אוטובוס", new[] { "BUS-STATION-NEW" }, DraftQuantityBasis.Count,
                DraftConfidence.Assumption, new[] { new DraftEmit("U40.02.2340") },
                "סככה דגם 4 מ' — הנחה. חלופות: U40.02.2320 (8 מ'), U40.02.2500 (סככה חכמה, ללא מחיר). " +
                "בנוסף יש בשכבת 24-TAMRUR-BUS_ST 88 בלוקי 'sh 4-1.7-0.7' — לבדוק אם הם סככות.",
                PrimarySourcePattern: "*-SM-*"),
            new("bus-stop-blocks", "בלוקים בשכבת תחנות (24-TAMRUR-BUS_ST)", new[] { "24-TAMRUR-BUS_ST" }, DraftQuantityBasis.Count,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "88 בלוקי 'sh 4-1.7-0.7' ו-82 'רחבת הערכות' — להחלטה (סככות? רחבות המתנה? אבן שפה לאוטובוסים U51.06.2600–2620?).",
                PrimarySourcePattern: "*-SM-*"),
            new("bike-racks", "מתקני חנייה לאופניים", new[] { "*" }, DraftQuantityBasis.Count,
                DraftConfidence.Assumption, new[] { new DraftEmit("U40.02.1260") },
                "בלוק 'מתקן ל-2 אופניים'; אותם מתקנים מופיעים גם במודל השילוט — נכלל רק המודל הגאומטרי. דגם המתקן הוא הנחה.",
                PrimarySourcePattern: "*-GM-*", BlockPatterns: new[] { "מתקן ל-2 אופניים*" }),
            new("pedestrian-fence", "מעקה להולכי רגל", new[] { "HW-FENC-PDST" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Assumption, new[] { new DraftEmit("U51.33.2330") },
                "מעקה להולכי רגל A2 — הנחה לפי שם השכבה."),
            new("safety-barrier", "מעקה בטיחות", new[] { "HW-BARI-*" }, DraftQuantityBasis.OpenLength,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(),
                "האורך נמדד; סוג המעקה (רמת בלימה/רוחב פעיל) להגדרה הנדסית."),
        };

        // --- Infrastructure outside the roads price list: the domain is recognised, the item is not. -------------
        // Layer patterns come from the same built-in utility rules the sections use (one source of truth). Every
        // family is a decision without an item: the measured quantity is shown, and the engineer enters the item,
        // diameter, material and price. No item, diameter or price is proposed here.
        const string utilityNote = "תשתית מחוץ לספריית הכבישים: התחום זוהה והכמות נמדדה; הסעיף, הקוטר, החומר והעומק להחלטת מהנדס. " +
                                   "אין סעיף מוצע. קוטר שמופיע בשם השכבה או הבלוק מוצג לבדיקה בלבד.";
        foreach (var (id, domain, labels) in UtilityDomains)
        {
            var patterns = SectionProjectionLogic.BuiltInRules
                .Where(r => r.Kind == "utility" && labels.Contains(r.Label, StringComparer.Ordinal))
                .Select(r => r.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // The DR model's pipes are lines and its structures are blocks: a manhole layer's drawn lines and
            // circles stay an alternative measurement of the counted blocks, never pipe length.
            var drainage = id == "utility-drainage";
            rules.Add(new(id, $"תשתית {domain} — קווים", drainage ? patterns.Concat(DrainageModelLinePatterns).ToArray() : patterns,
                DraftQuantityBasis.OpenLength, DraftConfidence.Decision, Array.Empty<DraftEmit>(), utilityNote));
            rules.Add(new(id + "-structures", $"תשתית {domain} — מבנים ואביזרים",
                drainage ? patterns.Concat(DrainageModelStructurePatterns).ToArray() : patterns, DraftQuantityBasis.Count,
                DraftConfidence.Decision, Array.Empty<DraftEmit>(), utilityNote));
        }

        var scope = new List<DraftScopeItem>
        {
            new("U51.01.0250", "פירוק אבני שפה קיימות", "היקף הפירוק אינו משורטט. אורך אבני השפה הקיימות (CURB-EXST / S_CURB) מופיע בגיליון 'מצב קיים ותשתיות' — זהו האורך המרבי; להזין רק את הקטע לפירוק."),
            new("U51.01.0090", "פירוק מיסעה קיימת", "היקף הפירוק אינו משורטט; להזין שטח."),
            new("U51.01.0200", "פירוק מדרכות מרוצפות", "היקף הפירוק אינו משורטט; להזין שטח."),
            new("U51.01.2580", "פירוק תחנות אוטובוס קיימות", "BUS-STATION-EXSIT בשרטוט הגאומטרי — להזין מספר."),
            new("U51.04.2450", "ניסור לחיבור לאספלט קיים", "להזין אורך קו החיבור (ראו קו שפת מיסעה)."),
            new("U51.02.0010", "חפירה/חציבה (עבודות עפר)", "עבודות עפר לא הוערכו בסריקה; להזין נפח מחישוב עפר."),
        };

        return new EngineerBoqLibrary
        {
            Id = "mahod-roads-nti-urban-v1.4",
            Title = "ספריית שיוך מהוד — כבישים ותנועה (מחירון נת\"י עירוני) ותחומי תשתית ללא סעיפים v1.4",
            Basis = "הצעות בלבד. המקורות: שמות השכבות במודלי התכנון (HA/GM/SM), קודי החומר בקורידורים שבשרטוט, כללי ברירת המחדל שבפרופיל " +
                    "(לא אושרו), ומבנה אומדן הייחוס של מהוד — כתב כמויות של פרויקט כבישים אחר. שום שורה אינה אישור הנדסי.",
            Parameters = parameters.All(p => p.Min <= p.Max && p.Accepts(p.DefaultValue))
                ? parameters
                : throw new InvalidOperationException("Every library parameter needs an ordered range that contains its default."),
            Rules = rules,
            ScopeItems = scope,
            SurveySourcePatterns = new[] { "*-SP-*", "*MEDVA*", "*SURVEY*", "*MEDIDA*" },
            UtilitySourcePatterns = new[] { "UT-*" },
            DraftingAidLayerPatterns = new[]
            {
                "0", "0-*", "Defpoints", "_*", "pl-cont", "HELP*", "help*", "*TABL", "MPI_*", "*SYMBOL*",
                "AM-Text", "*text*", "*TXT*", "pcell", "scellno", "CL_*", "*_calc", "TR_PNT", "NEW",
            },
            ExistingLayerPatterns = new[]
            {
                "*-EX", "*-EX-*", "*EXST*", "*EXIST*", "*EXSIT*", "*KAYAM*", "S_*", "MYA-*",
            },
            WidthClassifiedLayerPatterns = new[] { "TR-MARK-*" },
            WidthParameterKeys = new[] { transverseWidth, crossingWidth },
            Texts = new DraftWorkbookTexts
            {
                Structure = "מבנה 01 — כבישים ותנועה",
                LibraryName = "ספריית הכבישים",
                BillName = "בכתב הכמויות לכבישים",
                AidsIntro = "נפחי קורידור (לבדיקת עובי שכבות המיסעה בלבד), שכבות שמהנדס סימן 'לא כמות בנייה', ושכבות עזר של השרטוט: תחנות, טקסטים, טבלאות, קווי חתך ושכבות מערכת. לא נכללו בכתב הכמויות.",
                ParameterExamples = "חלק המיסעה שנבנה לכל העומק, עובי מצעים, גמר מדרכות, יחסי צבע וכו'",
                ScopeExamples = "פירוק, ניסור, חפירה ומילוי",
                ScopeNote = "חפירה ומילוי לא הוערכו בסריקה; הידוק קרקע יסוד מתחת למיסעה ולמדרכות כלול בתת פרק 51.02.",
                ReferenceEstimate = "כתב כמויות של מהוד לפרויקט כבישים אחר. שימש רק כדי להציע מבנה (אילו פריטים מרכיבים מיסעה, מדרכה, סימון ושילוט) ויחסים גסים. " +
                                    "כמויות ומחירים בטיוטה זו אינם מועתקים ממנו.",
                OtherSystems = "הספרייה מתמחרת כבישים, סימון, שילוט, תחנות וריהוט רחוב. תשתיות בתכנון (מים, ביוב, ניקוז, חשמל, תאורה, רמזורים, תקשורת וגז) מזוהות לפי תחום " +
                               "ומוצגות כשורות 'הגדרה' עם הכמות שנמדדה, בלי סעיף ובלי מחיר — נכנסות לסכום רק אחרי שמזינים מק\"ט ומחיר. גינון והשקיה לא נמדדו כתחום.",
                OverlapExample = "הצללת נת\"צ מעל הצללת כביש",
                OverlapAffects = "שטחי מיסעה ומדרכות",
                OverlapAction = "לבדוק בשרטוט שהצללות הנת\"צ, הכבישים והמדרכות אינן מונחות זו על זו.",
                ReferenceGlobalFactor = 0.9,
            },
        };

        static IReadOnlyList<DraftEmit> Pavement(string share, string subbase) => new[]
        {
            new DraftEmit("U51.04.1820", Note: "שכבה עליונה תאמ\"א 19 (SMA) 5 ס\"מ PG70-10 על כל השטח — לפי קוד ASF-5-19-70"),
            new DraftEmit("U51.04.0130", ParameterKey: share, Note: "תא\"צ 25 6 ס\"מ PG70-10 בחלק הנבנה לכל העומק — לפי ASF-6-25-70"),
            new DraftEmit("U51.04.0160", ParameterKey: share, Note: "תא\"צ 25 7 ס\"מ PG68-10 בחלק הנבנה לכל העומק — לפי ASF-7-25-68"),
            new DraftEmit("U51.04.2410", Factor: 2, ParameterKey: share, Note: "ריסוס מאחה קל בין שכבות אספלט חדשות (7→6 ו-6→5 ס\"מ) בחלק הנבנה לכל העומק — פעמיים"),
            new DraftEmit("U51.04.2400", ComplementKey: share, Note: "ריסוס מאחה על המשטח המקורצף, מתחת לשכבה העליונה (שאר השטח)"),
            new DraftEmit("U51.04.2420", ParameterKey: share, Note: "ריסוס יסוד מעל המצע (חלק לכל העומק)"),
            new DraftEmit("U51.03.0010", ParameterKey: share, SecondParameterKey: subbase, Note: "מצע סוג א' = שטח × חלק לכל העומק × עובי (קוד MAZA)"),
            new DraftEmit("U51.02.0110", ParameterKey: share, Note: "הידוק קרקע יסוד (חלק לכל העומק)"),
            new DraftEmit("U51.04.2530", ComplementKey: share, Note: "קרצוף 4.1–8 ס\"מ בשאר השטח (שיקום). קיזוז החומר המקורצף לרשות הקבלן (U51.04.2550) לא נכלל"),
        };

        static IReadOnlyList<DraftEmit> Sidewalk(string pavers, string subbase) => new[]
        {
            new DraftEmit("U51.06.8040", ParameterKey: pavers, Note: "אבנים משתלבות 6 ס\"מ אפור (לפי חלק הריצוף)"),
            new DraftEmit("U51.04.2310", ComplementKey: pavers, Note: "אספלט למדרכות ואיים 4 ס\"מ (שאר השטח) — כמו באומדן הייחוס"),
            new DraftEmit("U51.04.2420", ComplementKey: pavers, Note: "ריסוס יסוד מעל המצע, מתחת לאספלט המדרכות (שאר השטח)"),
            new DraftEmit("U51.03.0010", ParameterKey: subbase, Note: "מצע סוג א' = שטח × עובי"),
            new DraftEmit("U51.02.0110", Note: "הידוק קרקע יסוד מתחת למדרכות ולאיים"),
            new DraftEmit("U51.01.2000", Note: "ריסוס והדברה בשטחי סלילה, כמו באומדן הייחוס"),
        };
    }
}
