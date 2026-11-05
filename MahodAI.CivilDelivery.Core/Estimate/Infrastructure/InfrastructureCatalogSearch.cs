using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

public sealed record InfrastructureCatalogOption(string Code, string Description, string Unit, decimal? Price, string Reason, int Rank)
{
    public string PriceDisplay => Price?.ToString("N2", CultureInfo.CurrentCulture) ?? "חסר מחיר";
    public bool MatchesLayerTopic { get; init; }
    public bool IsRecommended { get; init; }
    public string RecommendationLabel => IsRecommended ? "מומלץ" : "";
}

/// <summary>Local Hebrew search and ranked alternatives, never an invented engineering specification.</summary>
public sealed class InfrastructureCatalogSearch
{
    private sealed record Entry(CatalogItem Item, string Text, string Context, IReadOnlyList<string> ContextTopics, string? Material, decimal? Price);
    private readonly Entry[] _entries;
    private readonly CatalogSnapshot _catalog;
    private readonly bool _usesNtiChapters;
    private readonly InfrastructureCatalogUsage? _usage;
    private static readonly HashSet<string> TopicTerms = InfrastructureBoq.Topics.Where(t => t != InfrastructureBoq.Unknown).Select(Normalize).ToHashSet(StringComparer.Ordinal);
    public InfrastructureCatalogSearch(InfrastructurePriceBook book, InfrastructureCatalogUsage? usage = null)
        : this(book.Catalog, usage, book.Chapters, book.UsesNtiChapters) { }
    public InfrastructureCatalogSearch(CatalogSnapshot catalog, InfrastructureCatalogUsage? usage = null,
        IReadOnlyDictionary<string, string>? chapters = null, bool usesNtiChapters = true)
    {
        _catalog = catalog; _usage = usage; _usesNtiChapters = usesNtiChapters;
        var contexts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        _entries = catalog.Items.Values.Where(i => i.Unit.Dimension is not (UnitDimension.Note or UnitDimension.Unknown)).Select(i =>
        {
            var rawContext = chapters?.GetValueOrDefault(i.Code) ?? i.ChapterPath;
            var context = Normalize(rawContext);
            // A combined parent (water + sewer) cannot override the explicitly named sewer subchapter.
            if (!contexts.TryGetValue(rawContext, out var topic)) contexts[rawContext] = topic = rawContext.Split('>')
                .AsEnumerable().Reverse().Select(part => InfrastructureLayerNames.TopicsInText(part)).FirstOrDefault(topics => topics.Count > 0) ?? Array.Empty<string>();
            return new Entry(i, Normalize(i.Description), context, topic, MaterialOf(i.Description), catalog.Prices.GetValueOrDefault(i.Code)?.Price);
        }).ToArray();
    }

    public IReadOnlyList<InfrastructureCatalogOption> Find(InfrastructureGroup group, string query = "", int limit = 100, BoqCatalogCategory? category = null)
    {
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(term =>
        {
            var topic = InfrastructureLayerNames.Classify(term, "").Topic;
            return topic == InfrastructureBoq.Unknown ? term : Normalize(topic);
        }).Distinct(StringComparer.Ordinal).ToArray();
        var requestedTopics = terms.Where(TopicTerms.Contains).ToArray();
        var demolish = group.Disposition == InfrastructureDisposition.ExistingDemolish;
        // Demolition of an existing element is priced from the demolition chapter, ranked by what is demolished.
        var rankingTopic = requestedTopics.Length == 1 ? requestedTopics[0] : demolish ? Demolition : group.Topic;
        var element = ElementTerms(group);
        // A pavement-structure row names its own layer (תא"צ 19 בעובי 5): that wording is the search.
        var hint = group.GroupPart == RoadPavement.DerivedPart
            ? Normalize(group.Element).Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var diameter = LayerDiameter(group.ShortLayer);
        var material = MaterialOf(group.ShortLayer);
        var hasQuery = terms.Length > 0;
        var focused = category != null && category.Code.Length > 0;
        var auxiliaryLength = Units.Parse(group.MeasurementUnit).Dimension == UnitDimension.Length && InfrastructureLayerNames.IsAuxiliaryLayer(group.ShortLayer);
        if (!hasQuery && !focused && auxiliaryLength) return Array.Empty<InfrastructureCatalogOption>();
        var matches = new List<InfrastructureCatalogOption>();
        foreach (var e in _entries)
        {
            if (!e.Item.Unit.SameUnit(Units.Parse(group.MeasurementUnit)) || (category != null && !category.Contains(e.Item))) continue;
            var codeMatch = hasQuery && e.Item.Code.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
            if (hasQuery && !codeMatch && !terms.All(t => MatchesTerm(e.Text, t) ||
                (TopicTerms.Contains(t) ? e.ContextTopics.Contains(t) : MatchesTerm(e.Context, t)))) continue;
            var topic = TopicRank(rankingTopic, e.Item, e.Text, e.ContextTopics);
            if (!hasQuery && !focused && topic == 0) continue;
            var rank = topic + (focused ? 80 : 0);
            var reasons = new List<string>();
            if (topic > 0) reasons.Add(rankingTopic);
            var elementMatch = element != null && element.IsMatch(e.Text);
            var hintMatch = hint.Length > 0 && hint.All(t => MatchesTerm(e.Text, t));
            if (hintMatch) { rank += 120; reasons.Add("לפי שכבת המבנה: " + group.Element); }
            if (demolish && rankingTopic == Demolition)
            {
                if (_usesNtiChapters && e.Item.SubChapter == "51.01") rank += 60;
                if (elementMatch) { rank += 45; reasons.Add("פירוק של " + (group.Element.Length > 0 ? group.Element : group.Topic)); }
                else if (element != null) rank -= 25;
            }
            else if (group.Surface == RoadSurfaceKind.Milling && e.Text.Contains("קרצוף")) { rank += 60; reasons.Add("קרצוף"); }
            if (e.Item.Code.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)) rank += 10000;
            if (codeMatch) rank += 500;
            if (diameter is { } d && DiameterMatches(e.Item.Description, d.Value, d.Unit))
            {
                rank += 100;
                reasons.Add(d.Unit == null ? $"מספר קוטר {d.Value:G} בשם השכבה; היחידה לא צוינה" : $"קוטר {d.Value:G} {d.Unit}");
            }
            if (Regex.IsMatch(e.Text, "צינור|קו מים|כבל")) rank += 15;
            if (material != null && e.Material != null)
            {
                rank += material == e.Material ? 45 : -45;
                if (material == e.Material) reasons.Add("חומר לפי שם השכבה: " + material);
            }
            if (Regex.IsMatch(e.Text, "תוספת|הגנה|קידוח|שרוול|בדיק") || (rankingTopic != "פירוקים והריסות" && !focused && e.Text.Contains("פירוק"))) rank -= 20;
            if (Regex.IsMatch(e.Text, "הפחת|ניכוי|זיכוי"))
            { rank -= 90; reasons.Add("סעיף הפחתה/זיכוי — בדוק את אופן היישום"); }
            var projects = _usage?.Projects(e.Item) ?? 0;
            if (projects > 0 && (topic > 0 || focused))
            {
                rank += Math.Min(12, projects * 3);
                reasons.Add($"מופיע בכתבי כמויות של {projects} פרויקטים; רמז בלבד");
            }
            if (e.Price != null) rank += 1;
            matches.Add(new(e.Item.Code, e.Item.Description, e.Item.UnitRaw, e.Price,
                hasQuery ? "תוצאת חיפוש" + (reasons.Count > 0 ? " · " + string.Join(" · ", reasons) : "")
                    : string.Join(" · ", reasons) + " · בדוק חומר, עומק וסוג עבודה", rank)
            { MatchesLayerTopic = !auxiliaryLength && group.Role == LayerRole.Quantity && (demolish
                ? TopicRank(Demolition, e.Item, e.Text, e.ContextTopics) > 0 && (element == null || elementMatch)
                : hint.Length > 0 ? hintMatch
                : TopicRank(group.Topic, e.Item, e.Text, e.ContextTopics) > 0 && (group.Surface != RoadSurfaceKind.Milling || e.Text.Contains("קרצוף"))) });
        }
        var options = matches.OrderByDescending(x => x.Rank).ThenBy(x => x.Code, StringComparer.Ordinal).Take(limit).ToArray();
        // A filter or an exact code search can return items unrelated to this layer.
        // Never give one of those a layer recommendation just because it is first.
        var recommended = Array.FindIndex(options, x => x.MatchesLayerTopic);
        if (recommended >= 0) options[recommended] = options[recommended] with { IsRecommended = true };
        return options;
    }

    /// <summary>Read-only planning can run off the UI thread; applying is an explicit user action.</summary>
    public InfrastructureAutoAssignmentBatch PlanAutomaticAssignment(IEnumerable<InfrastructureGroup> groups, BoqCatalogCategory? category = null)
    {
        var proposals = new List<InfrastructureAutoAssignmentBatch.Proposal>();
        var existing = 0; var unmeasured = 0; var unmatched = 0;
        foreach (var group in groups.Distinct())
        {
            if (group.Approved) { existing++; continue; }
            // Existing that stays is never priced; edges and drafting are not pay items on their own.
            if (group.Disposition == InfrastructureDisposition.ExistingKeep || group.Role != LayerRole.Quantity) { unmatched++; continue; }
            if (!double.IsFinite(group.Quantity) || group.Quantity <= 0) { unmeasured++; continue; }
            // A text query belongs to the current layer only, never to the whole batch.
            var recommendation = Find(group, category: category).FirstOrDefault(o => o.IsRecommended);
            if (recommendation == null) { unmatched++; continue; }
            proposals.Add(new(group, recommendation.Code, group.Topic, group.MeasurementUnit, group.Quantity));
        }
        return new(_catalog, proposals, existing, unmeasured, unmatched);
    }

    private const string Demolition = "פירוקים והריסות";

    /// <summary>The element words a demolition item must name (אבני שפה, צינור ניקוז, שוחות…), from the firm convention or the topic.</summary>
    private static Regex? ElementTerms(InfrastructureGroup group)
    {
        var terms = FirmLayerConventions.Match(group.Layer)?.DemolitionTerms;
        if (string.IsNullOrEmpty(terms)) terms = group.Topic switch
        {
            "כבישים ואספלט" => "אספלט", "ריצופים" => "ריצוף|מדרכ", "אבני שפה" => "אבני שפה|אבן שפה", "מסגרות וגדרות" => "גדר|מעקה",
            "ניקוז" => "ניקוז", "מים" => "מים", "ביוב" => "ביוב", "תקשורת" => "תקשורת", "תאורה" => "תאורה|עמוד", "חשמל" => "חשמל|כבל",
            "סימון ושילוט" => "שלט|תמרור", "קירות ומבנים" or "קירות תומכים" => "קיר", "גינון" => "עצים|עקיר", _ => null
        };
        return string.IsNullOrEmpty(terms) ? null : new Regex(string.Join("|", terms.Split('|').Select(t => Regex.Escape(Normalize(t))).Where(t => t.Length > 0)), RegexOptions.CultureInvariant);
    }

    public static string LayerNote(InfrastructureGroup group)
    {
        if (group.Disposition == InfrastructureDisposition.ExistingKeep)
            return "תשתית קיימת שנשארת — אינה נכנסת לכתב הכמויות. אם היא נהרסת או מפורקת, סמן \"קיים — לפירוק\" והיא תתומחר בגיליון הפירוקים.";
        if (group.Disposition == InfrastructureDisposition.ExistingDemolish)
            return "קיים לפירוק — ההצעות מפרק הפירוקים לפי סוג האלמנט. בדוק עובי, קוטר וסוג לפני השיוך.";
        if (group.Role == LayerRole.Boundary) return "קו שפה / גבול שטח. אורכו אינו סעיף בפני עצמו — השטח נמדד מה־Hatch שבתוכו.";
        if (group.Role == LayerRole.Annotation) return "שכבת שרטוט (כיתוב, מידות, חתך או חתך אורך) — לא כמות לביצוע.";
        if (group.MeasuredQuantity <= 0) return "אין מדידה מהשרטוט ביחידה זו. יש להזין כמות במפורש; לא נגזרים משקל, נפח או עובי משטח או מאורך.";
        if (Units.Parse(group.MeasurementUnit).Dimension == UnitDimension.Count) return "ספירת מופעי בלוק לפי שם ושכבה. בדוק שהבלוק מייצג את הפריט שבחרת; קווי המתאר שלו אינם אורך צינור.";
        if (Units.Parse(group.MeasurementUnit).Dimension == UnitDimension.Area) return "שטח גאומטרי של גבולות סגורים או Hatch, כולל חורים שמוגדרים בו. בדוק חפיפות ושהגבולות מייצגים את העבודה.";
        if (Units.Parse(group.MeasurementUnit).Dimension == UnitDimension.Volume) return "נפח גאומטרי של גופי Solid3d. לא חושב נפח משטח דו־ממדי ולא הונח עובי.";
        var leaf = group.ShortLayer;
        if (InfrastructureLayerNames.IsAuxiliaryLayer(leaf))
            return "נראה ככיתוב, חתך, סימון עזר או היקף אביזר. אין הצעת צינור אוטומטית; אפשר לחפש סעיף ידנית אם האורך מתאים.";
        if (Regex.IsMatch(leaf, @"(?:^|[-_\s])EX(?:$|[-_\s])", RegexOptions.IgnoreCase) ||
            group.Layer.Contains("ASMADE", StringComparison.OrdinalIgnoreCase) || group.Source.Contains("ASMADE", StringComparison.OrdinalIgnoreCase))
            return "נראה כמצב קיים לפי שם המקור/השכבה. אפשר לשייך אם העבודה המבוקשת מתאימה לכך.";
        if (Regex.IsMatch(leaf, @"DEFPOINTS|CLOUDS|HATCH|FITTINGS|MNHL|CURB|FRAME|CONFLICT|SIMUN|(?:^|-)BL(?:-|$)|(?:^|-)TX(?:-|$)|^0-(XREF|VIEW)", RegexOptions.IgnoreCase))
            return "ייתכן סימון עזר או היקף של אביזר. בדוק שהאורך מייצג את העבודה שברצונך לתמחר.";
        return LayerDiameter(leaf) is { } d
            ? d.Unit == null ? $"זוהה מספר קוטר {d.Value:G}; היחידה לא צוינה. בדוק יחידה, חומר ועומק מול התוכנית."
                : $"זוהה קוטר {d.Value:G} {d.Unit}. בחר את החומר, העומק וסוג העבודה המתאימים."
            : "ההצעות לפי הנושא שזוהה. בחר תחום או חפש בעברית כדי לדייק את המפרט.";
    }

    public static (double Value, string? Unit)? LayerDiameter(string layer)
    {
        var explicitSize = Regex.Match(layer, @"(?:DN|DIA|קוטר|Ø)[\s_=-]*(\d+(?:[.]\d+)?)\s*(MM|CM|INCH|IN|מ[""׳״']*מ|ס[""׳״']*מ)?", RegexOptions.IgnoreCase);
        if (explicitSize.Success)
        {
            var unit = explicitSize.Groups[2].Value.ToUpperInvariant();
            return (double.Parse(explicitSize.Groups[1].Value, CultureInfo.InvariantCulture),
                unit.StartsWith("CM") || unit.StartsWith("ס") ? "ס״מ" : unit.StartsWith("IN") ? "אינץ׳" :
                unit.Length > 0 || layer.Contains("DN", StringComparison.OrdinalIgnoreCase) ? "מ״מ" : null);
        }
        var labelled = Regex.Match(layer, @"(?<!\d)(\d+(?:[.]\d+)?)\s*(MM|CM|INCH|מ[""׳״']+מ|ס[""׳״']+מ)(?!\p{L})", RegexOptions.IgnoreCase);
        if (labelled.Success)
            return (double.Parse(labelled.Groups[1].Value, CultureInfo.InvariantCulture),
                labelled.Groups[2].Value.StartsWith("CM", StringComparison.OrdinalIgnoreCase) || labelled.Groups[2].Value.StartsWith("ס") ? "ס״מ" :
                labelled.Groups[2].Value.StartsWith("IN", StringComparison.OrdinalIgnoreCase) ? "אינץ׳" : "מ״מ");
        var convention = Regex.Match(layer, @"^(?:W-DE|SW-DE|DR-P?PIPE)[-_](\d+(?:[.]\d+)?)$", RegexOptions.IgnoreCase);
        if (!convention.Success)
            convention = Regex.Match(layer, @"^-?(?:MAIM|MAYIM|BIUV|BIYUV|MEKOROT|MIKOROT)[-_](\d+(?:[.]\d+)?)(?:[-_](?:MUTZA|MESHUAR|MEUTAK|EX|DE|NEW))?$", RegexOptions.IgnoreCase);
        return convention.Success ? (double.Parse(convention.Groups[1].Value, CultureInfo.InvariantCulture), null) : null;
    }

    private static bool DiameterMatches(string text, double value, string? unit)
    {
        foreach (Match m in Regex.Matches(text.Replace("׳", "'").Replace("״", "\""),
            @"(?:ב?קוטר|DN)\s*(?<inch>[""']{1,2})?\s*(?<n>\d+(?:[.]\d+)?)(?![\d./])\s*(?<unit>מ[""']{1,2}מ|ס[""']{1,2}מ|mm\b|cm\b|אינץ)?", RegexOptions.IgnoreCase))
        {
            var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            var u = m.Groups["unit"].Value;
            var actual = u.StartsWith("ס") || u.Equals("cm", StringComparison.OrdinalIgnoreCase) ? "ס״מ" :
                u.StartsWith("מ") || u.Equals("mm", StringComparison.OrdinalIgnoreCase) ? "מ״מ" : m.Groups["inch"].Success || u.StartsWith("אינץ") ? "אינץ׳" : null;
            if (Math.Abs(n - value) < 1e-8 && (unit == null || unit == actual)) return true;
            if (unit == "מ״מ" && actual == "ס״מ" && Math.Abs(n * 10 - value) < 1e-8) return true;
            if (unit == "ס״מ" && actual == "מ״מ" && Math.Abs(n - value * 10) < 1e-8) return true;
        }
        return false;
    }

    private int TopicRank(string topic, CatalogItem i, string text, IReadOnlyList<string> contextTopics)
    {
        // Chapter numbers are publisher-specific. Their inherited descriptions are usable across publishers.
        var contextRank = contextTopics.Contains(topic) && topic != InfrastructureBoq.Unknown ? 60 : 0;
        var semantic = topic switch
        {
            "מים" => text.Contains("מים") && text.Contains("צינור") ? 35 : 0,
            "ביוב" => text.Contains("ביוב") ? 35 : 0,
            "ניקוז" => Regex.IsMatch(text, "ניקוז|נקז") ? 35 : 0,
            "חשמל" => Regex.IsMatch(text, "חשמל|כבל") ? 30 : 0,
            "תאורה" => Regex.IsMatch(text, "תאורה|כבל") ? 30 : 0,
            "תקשורת" => Regex.IsMatch(text, "תקשורת|בזק") ? 35 : 0,
            "רמזורים" => text.Contains("רמזור") ? 60 : 0,
            "גז" => Regex.IsMatch(text, @"(?:^| )גז(?: |$)") ? 60 : 0,
            "השקיה" => text.Contains("השקיה") ? 35 : 0,
            "עבודות עפר" => Regex.IsMatch(text, "חפיר|חציב|עפר|מילוי") ? 35 : 0,
            "בטון" => text.Contains("בטון") ? 35 : 0,
            "איטום" => Regex.IsMatch(text, "איטום|בידוד") ? 35 : 0,
            "פירוקים והריסות" => Regex.IsMatch(text, "פירוק|הריס|עקיר|פינוי") ? 35 : 0,
            "ריצופים" => Regex.IsMatch(text, "ריצוף|ריצופ|מדרכ|משתלב") ? 35 : 0,
            "גינון" => Regex.IsMatch(text, "גינון|נטיע|שתיל|דשא|עצים|צמח") ? 35 : 0,
            "כבישים ואספלט" => Regex.IsMatch(text, "אספלט|כביש|מיסעה|סלילה") ? 35 : 0,
            "אבני שפה" => Regex.IsMatch(text, "אבן שפה|אבני שפה") ? 35 : 0,
            "קירות תומכים" => Regex.IsMatch(text, "קיר תומך|קירות תומכים") ? 35 : 0,
            "קירות ומבנים" => Regex.IsMatch(text, "קיר|מבנה|קירות|מבנים") ? 35 : 0,
            "מסגרות וגדרות" => Regex.IsMatch(text, "גדר|מעקה|מסגרות") ? 35 : 0,
            "סימון ושילוט" => Regex.IsMatch(text, "תמרור|שילוט|סימון") ? 35 : 0,
            _ => 0,
        };
        if (!_usesNtiChapters) return Math.Max(contextRank, semantic);
        var nti = topic switch
        {
        "מים" => i.SubChapter == "57.02" ? 70 : text.Contains("מים") && text.Contains("צינור") ? 30 : 0,
        "ביוב" => i.SubChapter == "57.03" && Regex.IsMatch(text, "צינור|ביוב") ? 70 : text.Contains("ביוב") ? 30 : 0,
        "ניקוז" => i.SubChapter == "51.05" && Regex.IsMatch(text, "צינור|נקז") ? 70 : text.Contains("ניקוז") ? 30 : 0,
        "חשמל" or "תאורה" => i.SubChapter == "08.10" ? 60 : Regex.IsMatch(text, "חשמל|כבל|תאורה") ? 30 : 0,
        "תקשורת" => i.SubChapter is "18.01" or "18.06" && Regex.IsMatch(text, "צינור|כבל|תקשורת|בזק") ? 60 : text.Contains("תקשורת") ? 30 : 0,
        "רמזורים" => text.Contains("רמזור") ? 60 : 0,
        "גז" => Regex.IsMatch(text, @"(?:^| )גז(?: |$)") ? 60 : 0,
        "השקיה" => i.SubChapter == "41.01" && Regex.IsMatch(text, "צינור|טפטוף") ? 70 : text.Contains("השקיה") ? 30 : 0,
        _ => 0,
        };
        return Math.Max(nti, Math.Max(contextRank, semantic));
    }

    private static string? MaterialOf(string text)
    {
        var t = Normalize(text);
        if (Regex.IsMatch(t, @"(?:^| )(?:pvc|פי וי סי)(?: |$)")) return "PVC";
        if (Regex.IsMatch(t, @"(?:^| )(?:פוליאתילן|pe(?: 100| 80)?)(?: |$)")) return "פוליאתילן";
        if (Regex.IsMatch(t, @"(?:^| )(?:בטון|beton|concrete|conc)(?: |$)")) return "בטון";
        if (Regex.IsMatch(t, @"(?:^| )(?:פלדה|steel)(?: |$)")) return "פלדה";
        return null;
    }

    private static bool MatchesTerm(string description, string term) => term.All(char.IsDigit)
        ? Regex.IsMatch(description, @"(?<!\d)" + Regex.Escape(term) + @"(?!\d)")
        : description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
          (term.Length > 2 && term[0] is 'ב' or 'ל' or 'ה' && description.Contains(term[1..], StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string text)
    {
        text = Regex.Replace(text, @"(?<![a-z])p[.\s]*v[.\s]*c(?![a-z])", "PVC", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?<![a-z])h[.\s]*d[.\s]*p[.\s]*e(?![a-z])", "פוליאתילן", RegexOptions.IgnoreCase);
        var b = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '"' or '׳' or '״') continue;
            b.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var normalized = Regex.Replace(b.ToString(), @"(?<=\p{L})(?=\d)|(?<=\d)(?=\p{L})", " ");
        normalized = Regex.Replace(normalized, @"צינורות|צנורות|צנרת|צנור", "צינור");
        normalized = Regex.Replace(normalized, @"פוליאטילן|פוליאיתילן|פוליאתלן|hdpe", "פוליאתילן");
        normalized = Regex.Replace(normalized, @"מזויין|מזוין", "מזוין");
        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
