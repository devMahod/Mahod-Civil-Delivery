using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A line of Hebrew mixed with numbers or Latin, split for drawing (2026-11-02): a DBText holding both scripts came out
/// scrambled on screen and an MText dropped the space between them ("תחנה79650.00"), so the typical section draws each
/// run as its own text, the first run rightmost, as a right-to-left line reads. Punctuation and spaces between two
/// Hebrew words stay with them ("הסדרי תנועה — שלב", "ס\"מ").
/// </summary>
public static class TypicalSheetText
{
    private static readonly Regex Run = new(
        @"[֐-׿](?:[֐-׿\s""'׳״—–\-:.,()]*[֐-׿""'׳״])?|[^֐-׿]+",
        RegexOptions.Compiled);

    /// <summary>The runs in logical order (draw the first rightmost), each trimmed; empty runs dropped.</summary>
    public static IReadOnlyList<string> Runs(string? text)
    {
        var runs = new List<string>();
        foreach (Match match in Run.Matches(text ?? string.Empty))
        {
            var run = match.Value.Trim();
            if (run.Length > 0) runs.Add(run);
        }
        return runs;
    }
}
