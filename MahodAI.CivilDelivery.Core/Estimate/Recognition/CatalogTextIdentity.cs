using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>
/// What makes two price-list descriptions the same item. Price lists name an item by a sentence whose identity lives
/// in small tokens — a thickness ("6 ס"מ" against "5 ס"מ"), a profile ("10/20"), a diameter ("4), a class ("סוג א'"),
/// a grade ("PG70-10"), a type ("A2"), a model ("דגם 'שילובית'"). Shared words say nothing about them: two rows
/// that differ only there are different items. This class normalises text and extracts those identity parameters;
/// it never decides that two items are the same, it only says what differs.
/// </summary>
public static class CatalogTextIdentity
{
    private static readonly Regex Bidi = new("[‎‏‪-‮⁦-⁩]", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Latin = new(@"(?<![A-Za-z0-9])[A-Za-z]+[0-9]+(?:[-.][0-9]+)*(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
    // A number keeps the role word before it (רוחב, גובה, עובי, קוטר, אורך, עומק — with ו/ב/ל prefixes) and the unit
    // after it: "ברוחב 10 ס"מ" is "רוחב:10 ס"מ", never a bare "10" that "10 מ"מ" or "גובה 10" would also produce.
    // "של" between them keeps the role ("בעובי של 2.5מ"מ"), and an "עד"/"מעל" there stays bound to the number
    // ("בגובה של עד 15 ס"מ" is "גובה:עד 15 ס"מ").
    private static readonly Regex Measure = new(
        @"(?:(?<![א-ת])[ובל]{0,2}(?<role>רוחב|גובה|עובי|קוטר|אורך|עומק)\s*(?:של(?![א-ת])\s*)?(?:(?<bound>עד|מעל)(?![א-ת])\s*)?)?(?<inch>"")?\s*" +
        @"(?<num>[0-9]+(?:\.[0-9]+)?(?:\s*[-/xX×]\s*[0-9]+(?:\.[0-9]+)?)*)" +
        @"(?:\s*(?<unit>ס""מ|מ""מ|מ""ר|מ""ק|ק""ג|מטר|מ'|%)|(?<inchAfter>"")(?![א-ת]))?",
        RegexOptions.CultureInvariant);
    private static readonly Regex Class = new(@"סוג\s*([א-ת])'", RegexOptions.CultureInvariant);
    private static readonly Regex Model = new("דגם\\s*\"([^\"]{1,40})\"", RegexOptions.CultureInvariant);
    private static readonly Regex Word = new(@"[א-ת]{2,}|[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    // Words that turn the same number into another item: "עד 4.0 מ"ר" / "מעל 4.0 מ"ר", "עם פאזה" / "ללא פאזה".
    private static readonly Regex Qualifier = new(@"(?<![א-ת])(עד|מעל|מתחת|ללא|בלי|כולל)(?![א-ת])", RegexOptions.CultureInvariant);

    /// <summary>
    /// NFC, without bidi marks, with Hebrew and typographic quotes unified ('' and ״ become ", ׳ becomes '), dashes
    /// unified and whitespace collapsed. Nothing else is changed: words, digits and punctuation keep their meaning.
    /// </summary>
    public static string Normalize(string? text)
    {
        var value = Bidi.Replace((text ?? string.Empty).Normalize(NormalizationForm.FormC), string.Empty);
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(c switch
            {
                '״' or '“' or '”' => '"',
                '׳' or '‘' or '’' or '`' => '\'',
                '–' or '—' or '−' => '-',
                _ => c,
            });
        value = builder.ToString().Replace("''", "\"", StringComparison.Ordinal);
        return Spaces.Replace(value, " ").Trim();
    }

    /// <summary>
    /// The identity parameters of a description: latin codes with digits ("PG70-10", "A2"); numbers and dimension
    /// groups ("6", "10/20", "4.1-8.0", "75x120") with the role word before them and the unit after them when the text
    /// states them ("עובי:6 ס"מ", "קוטר:4 אינץ'"); "סוג" classes, quoted "דגם" models and the qualifiers that change a
    /// number's meaning (עד, מעל, מתחת, ללא, בלי, כולל). A dimension group keeps its order. Case-folded.
    /// </summary>
    public static IReadOnlySet<string> Parameters(string? text)
    {
        var value = Normalize(text);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Model.Matches(value)) result.Add("דגם:" + m.Groups[1].Value.Trim());
        value = Model.Replace(value, " ");
        foreach (Match m in Class.Matches(value)) result.Add("סוג:" + m.Groups[1].Value);
        foreach (Match m in Qualifier.Matches(value)) result.Add(m.Groups[1].Value);
        foreach (Match m in Latin.Matches(value)) result.Add(m.Value.ToUpperInvariant());
        value = Latin.Replace(value, " ");
        foreach (Match m in Measure.Matches(value))
        {
            var number = Spaces.Replace(m.Groups["num"].Value, string.Empty).Replace('X', 'x').Replace('×', 'x');
            var unit = m.Groups["inch"].Success || m.Groups["inchAfter"].Success ? "אינץ'"
                : m.Groups["unit"].Value == "מטר" ? "מ'" : m.Groups["unit"].Value;
            var role = !m.Groups["role"].Success ? string.Empty
                : m.Groups["role"].Value + ":" + (m.Groups["bound"].Success ? m.Groups["bound"].Value + " " : string.Empty);
            result.Add(role + number + (unit.Length > 0 ? " " + unit : string.Empty));
        }
        return result;
    }

    /// <summary>
    /// The same text: equal after <see cref="Normalize"/> once every space is removed. Price lists drop or double spaces
    /// ("הידוקקרקע", "ביטומןPG70-10"); a space never makes another item.
    /// </summary>
    public static bool SameText(string? a, string? b) =>
        string.Equals(Normalize(a).Replace(" ", string.Empty, StringComparison.Ordinal),
            Normalize(b).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>Words (two letters or more), for ranking candidates only — never an identity.</summary>
    public static IReadOnlySet<string> Words(string? text) =>
        Word.Matches(Normalize(text).Replace("\"", string.Empty, StringComparison.Ordinal))
            .Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Unit spelling without quotes, dots and spaces: מ"ר, מ''ר and מר compare equal; מ"ר and מ"ק do not.</summary>
    public static string UnitKey(string? unit) =>
        new string(Normalize(unit).Where(c => c is not ('"' or '\'' or '.' or ' ')).ToArray()).ToLower(CultureInfo.InvariantCulture);

    /// <summary>
    /// A description cut short in its source: the shorter text is a strict prefix of the longer one. The parameters
    /// after the cut are unknown, so such a match is an ambiguity to resolve, not an identity.
    /// </summary>
    public static bool IsTruncatedPrefixOf(string? shorter, string? longer)
    {
        var a = Normalize(shorter).Replace(" ", string.Empty, StringComparison.Ordinal);
        var b = Normalize(longer).Replace(" ", string.Empty, StringComparison.Ordinal);
        return a.Length > 0 && a.Length < b.Length && b.StartsWith(a, StringComparison.Ordinal);
    }
}
