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
        new("מים", "WATER", new[] { "מים", "MAIM", "MAYIM", "MAYM", "MEYIM", "MEKOROT", "MIKOROT", "WATER", "WATERLINE", "WATER LINE", "WATER SUPPLY", "POTABLE WATER", "WTR", "WAT", "WT", "UT W", "W DE", "W EX", "W CNCL", "W FIRE", "W DRINK", "W HIDRANT", "W HYDRANT", "MEK LINE" }),
        new("ביוב", "SEWER", new[] { "ביוב", "שפכים", "מי שפכים", "BIUV", "BIYUV", "BIYOV", "BIYUB", "SEWER", "SEWAGE", "SAWAGE", "SANITARY", "WASTEWATER", "WASTE WATER", "SEW", "SWR", "SW", "UT S" }),
        new("ניקוז", "DRAINAGE", new[] { "ניקוז", "ניקוז מים", "מי גשם", "מי נגר", "NIKUZ", "NIKOUZ", "NIKUS", "NIQUZ", "NIKKUZ", "NIKOOZ", "DRAINAGE", "DRAIN", "DRENAG", "DRENAGE", "DRANAGE", "STORM", "STORMWATER", "STORM WATER", "STRM", "STM", "DR" }),
        new("גז", "GAS", new[] { "גז", "GAS", "GAZ" }),
        new("חשמל", "ELECTRIC", new[] { "חשמל", "HASHMAL", "HASHMALI", "CHASHMAL", "KHASHMAL", "HASHMEL", "CHASHMEL", "HASMAL", "HASHMALL", "ELECTRIC", "ELECTRICAL", "ELECTRICITY", "ELEC", "ELC", "EL", "POWER LINE", "POWER CABLE", "מתח נמוך", "מתח גבוה", "LOW VOLTAGE", "HIGH VOLTAGE", "LV CABLE", "HV CABLE" }),
        new("תאורה", "LIGHTING", new[] { "תאורה", "תאורת", "TEURA", "TEORA", "TAURA", "TAORA", "TEURAH", "LIGHTING", "STREETLIGHT", "STREET LIGHT", "STREET LIGHTING" }),
        new("תקשורת", "TELECOM", new[] { "תקשורת", "בזק", "סלקום", "פרטנר", "אורנג", "BEZEQ", "BEZEK", "BEZEC", "BZQ", "TIKSHORET", "TIKSHORRET", "TAKSHORET", "TAKSHORAT", "TIQSHORET", "TIKSHHORET", "TIKSORET", "TKSORET", "TIKSHOR", "TIK RAKEVET", "TELECOM", "COMMUNICATION", "COMMUNICATIONS", "COMM", "TC", "CELLCOM", "PARTNER", "PARTENER", "ORANGE", "HOT CABLE", "HOT COMM", "M4015 HOT", "4 HOT", "HOT NEW", "HOT EX", "HOT D", "KAV HOT" }),
        new("רמזורים", "SIGNALS", new[] { "רמזור", "רמזורים", "RAMZOR", "RAMZORIM", "REMZOR", "REMZORIM", "TRAFFIC SIGNAL", "TRAFFIC SIGNALS", "TRAFFIC LIGHT", "TRAFFIC LIGHTS" }),
        new("השקיה", "IRRIGATION", new[] { "השקיה", "השקייה", "השקית", "השקיית", "מי השקיה", "מים להשקיה", "השקיית מים", "HASHKAY", "HASHKAYA", "HASHKAYAH", "HASHKAIA", "HASHKAA", "HASHKIA", "IRRIGATION", "IRRIG" }),
    };
    private static readonly Rule[] Rules = Domains.SelectMany(domain => domain.Aliases.Select(alias =>
        new Rule(domain, alias, new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(Normalize(alias)).Replace("\\ ", @"\s+") + @"(?![\p{L}\p{N}])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled)))).ToArray();

    public static (string Topic, string Evidence, string Code) Classify(string layer, string source, string? description = null)
    {
        var pieces = layer.Split('|');
        var matches = Find(pieces.Last()); var origin = "שם השכבה";
        if (matches.Length == 0)
        {
            matches = Find(description ?? ""); origin = "תיאור השכבה בשרטוט";
        }
        if (matches.Length == 0)
        {
            // A model containing water/gas also contains grids, profiles and drawing annotations.
            // Its filename is context only, and can narrow an explicitly named pipe/line, not every entity.
            if (IsAuxiliaryLayer(pieces.Last()) || !Regex.IsMatch(Normalize(pieces.Last()), @"(?:^| )(PIPE|LINE|KAV|כבל|צינור|קו)(?: |$)"))
                return (InfrastructureBoq.Unknown, "ללא מילת תשתית מזוהה בשם או בתיאור השכבה", "UNCLASSIFIED");
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

    public static bool IsAuxiliaryLayer(string layer) => Regex.IsMatch(Normalize(layer.Split('|').Last()),
        @"(?:^| )(TEXT|TXT|TX|NOTE|NOTES|LABEL|DIM|MIDOT|GRID|FRAME|LEGEND|HATCH|DEFPOINTS|FITTINGS|MNHL|MANHOLE|HYDRANT|HIDRANT|CONFLICT|SIMUN|BL|PROF|PROFILE|ELEV|כיתוב|הערות|מידות)(?: |$)|^(?:HW|RR|DR) (?:PR|CS|ALGN|SEC)(?: |$)|^(?:DR|N) PIPE T$", RegexOptions.CultureInvariant);

    /// <summary>Catalogue chapter headings can legitimately contain several subjects; a layer still needs one.</summary>
    public static IReadOnlyList<string> TopicsInText(string text) => Find(Regex.Replace(text,
        @"(?<!\p{L})ו(?=[\u0590-\u05FF])", "")).Select(h => h.Rule.Domain.Topic).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Only explicit aliases, with letter/digit boundaries and CamelCase support; no fuzzy substring guessing.</summary>
    private static Hit[] Find(string text)
    {
        var normalized = Normalize(text);
        var hits = Rules.SelectMany(rule => rule.Pattern.Matches(normalized).Cast<Match>()
            .Select(m => new Hit(rule, m.Index, m.Length))).ToArray();
        // Compound meanings such as WASTE WATER or ניקוז מים supersede their contained generic word WATER/מים.
        var specific = hits.Where(h => !hits.Any(other => other.Rule.Domain.Topic != h.Rule.Domain.Topic &&
            other.Start <= h.Start && other.Start + other.Length >= h.Start + h.Length && other.Length > h.Length)).ToArray();
        // Electrical-discipline files also carry telecom: EL-TIKSHORET names the telecom subject explicitly.
        // A full HASHMAL + TIKSHORET conflict is still kept open.
        if (normalized.StartsWith("EL ", StringComparison.Ordinal) && specific.Any(h => h.Rule.Domain.Topic == "תקשורת" && h.Length >= 5))
            specific = specific.Where(h => !(h.Start == 0 && h.Rule.Alias == "EL")).ToArray();
        return specific;
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
