using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Names are retrieval evidence, not measured voltage or an engineering approval.
/// Reject explicit conflicts before ranking; never infer cable size or payment basis.
/// </summary>
internal static class ElectricalProposalCompatibility
{
    private enum Voltage { Unknown, High, Low, Conflict }
    private sealed record Subject(bool Electrical, Voltage Voltage, bool TrafficSignal);
    private static readonly string[] ElectricalNames = { "HASHMAL", "ELEC", "ELECTRIC", "ELECTRICITY", "חשמל" };
    private static readonly Regex High = Token("HV|HIGH[ _-]+VOLTAGE|ו?[בל]?מתח[ _-]+(?:נמוך[ _-]+ו)?גבוה");
    private static readonly Regex Low = Token("LV|LOW[ _-]+VOLTAGE|ו?[בל]?מתח[ _-]+(?:גבוה[ _-]+ו)?נמוך");
    private static readonly Regex Signals = Token("TRAFFIC[ _-]+SIGNALS?|RAMZOR|ל?רמזור(?:ים)?");

    private static Regex Token(string alternatives) => new(
        @"(?<![A-Za-zא-ת0-9])(?:" + alternatives + @")(?![A-Za-zא-ת0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Subject Read(string? evidence, bool source)
    {
        // Strip each source namespace independently: an XREF named HV or RAMZOR
        // must not become voltage/role evidence for a differently named layer.
        var text = source ? string.Join("\n", (evidence ?? "").Split('\n')
            .Select(line => line.Split('|').Last())) : evidence ?? "";
        var electrical = ElectricalNames.Any(name => MappingProposalEngine.ContainsWord(text, name));
        var signal = Signals.IsMatch(text);
        if (source && !electrical && !signal) return new(false, Voltage.Unknown, false);
        var high = High.IsMatch(text); var low = Low.IsMatch(text);
        return new(true, high && low ? Voltage.Conflict : high ? Voltage.High : low ? Voltage.Low : Voltage.Unknown, signal);
    }

    internal static bool IsCompatible(string? evidence, string description)
    {
        var source = Read(evidence, true);
        if (!source.Electrical) return true;
        if (source.Voltage == Voltage.Conflict) return false;
        var item = Read(description, false);
        if (item.TrafficSignal && !source.TrafficSignal) return false;
        return source.Voltage switch
        {
            Voltage.High => item.Voltage is not (Voltage.Low or Voltage.Conflict),
            Voltage.Low => item.Voltage is not (Voltage.High or Voltage.Conflict),
            _ => true,
        };
    }

    internal static string? MatchingVoltageReason(string? evidence, string description)
    {
        var source = Read(evidence, true); var item = Read(description, false);
        if (!IsCompatible(evidence, description) || source.Voltage != item.Voltage) return null;
        return source.Voltage switch
        {
            Voltage.High => "רמז מפורש בשם המקור למתח גבוה תואם לנוסח הסעיף; חומר, חתך כבל וסוג העבודה טרם אושרו",
            Voltage.Low => "רמז מפורש בשם המקור למתח נמוך תואם לנוסח הסעיף; חומר, חתך כבל וסוג העבודה טרם אושרו",
            _ => null,
        };
    }

    internal static string? ReviewHint(string? evidence)
    {
        var source = Read(evidence, true);
        if (!source.Electrical) return null;
        return source.Voltage switch
        {
            Voltage.Conflict => "בשם המקור מופיעים רמזים סותרים למתח גבוה ולמתח נמוך. יש לבדוק את משמעות הקבוצה; לא נבחר סעיף אוטומטית.",
            Voltage.High => "זוהה רמז לחשמל במתח גבוה בשם המקור. זהו רמז לחיפוש, לא אימות מתח; יש לבדוק חומר, חתך כבל וסוג עבודה לפני אישור סעיף.",
            Voltage.Low => "זוהה רמז לחשמל במתח נמוך בשם המקור. זהו רמז לחיפוש, לא אימות מתח; יש לבדוק חומר, חתך כבל וסוג עבודה לפני אישור סעיף.",
            _ => "זוהתה משפחת חשמל בשם המקור, אך המתח והמפרט לא אומתו. ניתן לבחור מועמד או לחפש ידנית; יש לבדוק חומר, חתך כבל וסוג עבודה לפני אישור סעיף.",
        };
    }
}
