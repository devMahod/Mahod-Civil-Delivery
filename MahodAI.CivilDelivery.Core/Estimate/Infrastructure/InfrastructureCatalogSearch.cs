using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

public sealed record InfrastructureCatalogOption(string Code, string Description, string Unit, decimal? Price, string Reason, int Rank)
{
    public string PriceDisplay => Price?.ToString("N2", CultureInfo.CurrentCulture) ?? "חסר מחיר";
}

/// <summary>Local Hebrew search and ranked alternatives, never an invented engineering specification.</summary>
public sealed class InfrastructureCatalogSearch
{
    private sealed record Entry(CatalogItem Item, string Text, decimal? Price);
    private readonly Entry[] _entries;
    public InfrastructureCatalogSearch(CatalogSnapshot catalog) => _entries = catalog.Items.Values
        .Where(i => i.Unit.SameUnit(Units.Parse("מטר")))
        .Select(i => new Entry(i, Normalize(i.Description), catalog.Prices.GetValueOrDefault(i.Code)?.Price)).ToArray();

    public IReadOnlyList<InfrastructureCatalogOption> Find(InfrastructureGroup group, string query = "", int limit = 100)
    {
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(term =>
        {
            var topic = InfrastructureLayerNames.Classify(term, "").Topic;
            return topic == InfrastructureBoq.Unknown ? term : Normalize(topic);
        }).Distinct(StringComparer.Ordinal).ToArray();
        var diameter = LayerDiameter(group.ShortLayer);
        var hasQuery = terms.Length > 0;
        var matches = new List<InfrastructureCatalogOption>();
        foreach (var e in _entries)
        {
            var codeMatch = hasQuery && e.Item.Code.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
            if (hasQuery && !codeMatch && !terms.All(t => MatchesTerm(e.Text, t))) continue;
            var topic = TopicRank(group.Topic, e.Item, e.Text);
            if (!hasQuery && topic == 0) continue;
            var rank = topic;
            var reasons = new List<string>();
            if (topic > 0) reasons.Add(group.Topic);
            if (e.Item.Code.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)) rank += 10000;
            if (codeMatch) rank += 500;
            if (diameter is { } d && DiameterMatches(e.Item.Description, d.Value, d.Unit))
            {
                rank += 100;
                reasons.Add(d.Unit == null ? $"מספר קוטר {d.Value:G} בשם השכבה; היחידה לא צוינה" : $"קוטר {d.Value:G} {d.Unit}");
            }
            if (Regex.IsMatch(e.Text, "צינור|קו מים|כבל")) rank += 15;
            if (Regex.IsMatch(e.Text, "תוספת|פירוק|הגנה|קידוח|שרוול|בדיק")) rank -= 20;
            if (e.Price != null) rank += 1;
            matches.Add(new(e.Item.Code, e.Item.Description, e.Item.UnitRaw, e.Price,
                hasQuery ? "תוצאת חיפוש" + (reasons.Count > 0 ? " · " + string.Join(" · ", reasons) : "")
                    : string.Join(" · ", reasons) + " · בדוק חומר, עומק וסוג עבודה", rank));
        }
        return matches.OrderByDescending(x => x.Rank).ThenBy(x => x.Code, StringComparer.Ordinal).Take(limit).ToArray();
    }

    public static string LayerNote(InfrastructureGroup group)
    {
        var leaf = group.ShortLayer;
        if (Regex.IsMatch(leaf, @"(?:^|[-_\s])EX(?:$|[-_\s])", RegexOptions.IgnoreCase) ||
            group.Layer.Contains("ASMADE", StringComparison.OrdinalIgnoreCase) || group.Source.Contains("ASMADE", StringComparison.OrdinalIgnoreCase))
            return "נראה כמצב קיים לפי שם המקור/השכבה. אפשר לשייך אם העבודה המבוקשת מתאימה לכך.";
        if (Regex.IsMatch(leaf, @"DEFPOINTS|CLOUDS|HATCH|FITTINGS|MNHL|CURB|FRAME|CONFLICT|SIMUN|(?:^|-)BL(?:-|$)|(?:^|-)TX(?:-|$)|^0-(XREF|VIEW)", RegexOptions.IgnoreCase))
            return "ייתכן סימון עזר או היקף של אביזר. בדוק שהאורך מייצג את העבודה שברצונך לתמחר.";
        return LayerDiameter(leaf) is { } d
            ? d.Unit == null ? $"זוהה מספר קוטר {d.Value:G}; היחידה לא צוינה. בדוק יחידה, חומר ועומק מול התוכנית."
                : $"זוהה קוטר {d.Value:G} {d.Unit}. בחר את החומר, העומק וסוג העבודה המתאימים."
            : "ההצעות לפי נושא התשתית. חפש בעברית כדי לדייק חומר, קוטר ועומק.";
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
        var convention = Regex.Match(layer, @"^(?:W-DE|SW-DE|DR-P?PIPE)[-_](\d+(?:[.]\d+)?)$", RegexOptions.IgnoreCase);
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

    private static int TopicRank(string topic, CatalogItem i, string text) => topic switch
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

    private static bool MatchesTerm(string description, string term) => term.All(char.IsDigit)
        ? Regex.IsMatch(description, @"(?<!\d)" + Regex.Escape(term) + @"(?!\d)")
        : description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
          (term.Length > 2 && term[0] is 'ב' or 'ל' or 'ה' && description.Contains(term[1..], StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string text)
    {
        var b = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '"' or '׳' or '״') continue;
            b.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var normalized = Regex.Replace(b.ToString(), @"(?<=\p{L})(?=\d)|(?<=\d)(?=\p{L})", " ");
        normalized = Regex.Replace(normalized, @"צינורות|צנורות|צנרת|צנור", "צינור");
        normalized = Regex.Replace(normalized, @"פוליאטילן|hdpe", "פוליאתילן");
        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
