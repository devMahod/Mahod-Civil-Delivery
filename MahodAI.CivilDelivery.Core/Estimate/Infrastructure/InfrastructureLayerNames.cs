using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>Layer names supply retrieval hints. They do not choose a payable specification.</summary>
public static class InfrastructureLayerNames
{
    private sealed record Domain(string Topic, string Code, string[] Aliases);
    private sealed record Rule(Domain Domain, string Alias, Regex Pattern);
    private sealed record Hit(Rule Rule, int Start, int Length);
    private static readonly Domain[] Domains =
    {
        new("מים", "WATER", new[] { "מים", "MAIM", "MAYIM", "MAYM", "MEYIM", "MEKOROT", "WATER", "WATERLINE", "WATER LINE", "WATER SUPPLY", "POTABLE WATER", "WTR", "WAT", "WT", "UT W", "W DE", "W EX" }),
        new("ביוב", "SEWER", new[] { "ביוב", "שפכים", "מי שפכים", "BIUV", "BIYUV", "BIYOV", "BIYUB", "SEWER", "SEWAGE", "SANITARY", "WASTEWATER", "WASTE WATER", "SEW", "SWR", "SW", "UT S" }),
        new("ניקוז", "DRAINAGE", new[] { "ניקוז", "ניקוז מים", "מי גשם", "מי נגר", "NIKUZ", "NIKOUZ", "NIKUS", "NIQUZ", "NIKKUZ", "NIKOOZ", "DRAINAGE", "DRAIN", "STORM", "STORMWATER", "STORM WATER", "STRM", "STM", "DR" }),
        new("גז", "GAS", new[] { "גז", "GAS", "GAZ" }),
        new("חשמל", "ELECTRIC", new[] { "חשמל", "HASHMAL", "CHASHMAL", "KHASHMAL", "HASHMEL", "CHASHMEL", "HASMAL", "HASHMALL", "ELECTRIC", "ELECTRICAL", "ELECTRICITY", "ELEC", "ELC", "EL", "POWER LINE", "POWER CABLE" }),
        new("תאורה", "LIGHTING", new[] { "תאורה", "תאורת", "TEURA", "TEORA", "TAURA", "TAORA", "TEURAH", "LIGHTING", "STREETLIGHT", "STREET LIGHT", "STREET LIGHTING" }),
        new("תקשורת", "TELECOM", new[] { "תקשורת", "בזק", "סלקום", "BEZEQ", "BEZEK", "BEZEC", "BZQ", "TIKSHORET", "TIKSHORRET", "TAKSHORET", "TAKSHORAT", "TIQSHORET", "TELECOM", "COMMUNICATION", "COMMUNICATIONS", "COMM", "TC", "CELLCOM", "HOT CABLE", "HOT COMM", "M4015 HOT" }),
        new("רמזורים", "SIGNALS", new[] { "רמזור", "רמזורים", "RAMZOR", "RAMZORIM", "REMZOR", "REMZORIM", "TRAFFIC SIGNAL", "TRAFFIC SIGNALS", "TRAFFIC LIGHT", "TRAFFIC LIGHTS" }),
        new("השקיה", "IRRIGATION", new[] { "השקיה", "השקייה", "השקית", "השקיית", "מי השקיה", "מים להשקיה", "השקיית מים", "HASHKAY", "HASHKAYA", "HASHKAYAH", "HASHKAIA", "HASHKAA", "HASHKIA", "IRRIGATION", "IRRIG" }),
    };
    private static readonly Rule[] Rules = Domains.SelectMany(domain => domain.Aliases.Select(alias =>
        new Rule(domain, alias, new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(Normalize(alias)).Replace("\\ ", @"\s+") + @"(?![\p{L}\p{N}])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled)))).ToArray();

    public static (string Topic, string Evidence, string Code) Classify(string layer, string source)
    {
        var pieces = layer.Split('|');
        var matches = Find(pieces.Last()); var origin = "שם השכבה";
        if (matches.Length == 0)
        {
            // Directory names and XREF namespaces must not override an explicitly named leaf.
            var filename = Path.GetFileNameWithoutExtension(source.Split('\\', '/').Last());
            matches = Find(filename); origin = "שם קובץ המקור";
            if (matches.Length == 0 && pieces.Length > 1)
            { matches = Find(string.Join(" ", pieces[..^1])); origin = "שם הפניית המקור"; }
        }
        var topics = matches.Select(h => h.Rule.Domain.Topic).Distinct(StringComparer.Ordinal).ToArray();
        if (topics.Length != 1)
            return (InfrastructureBoq.Unknown, topics.Length == 0 ? "ללא מילת תשתית מזוהה בשם" :
                $"{origin}: סימנים סותרים ({string.Join(", ", topics)}) — בחר נושא", "UNCLASSIFIED");
        var best = matches.OrderByDescending(h => h.Length).First();
        return (best.Rule.Domain.Topic, $"{origin}: {best.Rule.Alias} ← {best.Rule.Domain.Topic}", best.Rule.Domain.Code);
    }

    /// <summary>Only explicit aliases, with letter/digit boundaries and CamelCase support; no fuzzy substring guessing.</summary>
    private static Hit[] Find(string text)
    {
        var normalized = Normalize(text);
        var hits = Rules.SelectMany(rule => rule.Pattern.Matches(normalized).Cast<Match>()
            .Select(m => new Hit(rule, m.Index, m.Length))).ToArray();
        // Compound meanings such as WASTE WATER or ניקוז מים supersede their contained generic word WATER/מים.
        return hits.Where(h => !hits.Any(other => other.Rule.Domain.Topic != h.Rule.Domain.Topic &&
            other.Start <= h.Start && other.Start + other.Length >= h.Start + h.Length && other.Length > h.Length)).ToArray();
    }

    public static string Normalize(string text)
    {
        text = Regex.Replace(text, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        text = Regex.Replace(text, @"(?<=\p{L})(?=\d)|(?<=\d)(?=\p{L})", " ");
        var b = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format) continue;
            b.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }
        return string.Join(" ", b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
