using System.Collections.Generic;
using System.Text;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Display text of the rules export result (message box and panel notice). Never used for evidence.</summary>
internal static class BoqRulesExportText
{
    private const char Lrm = '‎';

    /// <summary>
    /// One sentence per line, and an LTR-wrapped name after a colon on a line of its own: the export message box is narrow
    /// and Windows broke file names and the 51.01–51.04 range across lines (b13 live 01/10). Text inside an LRM pair
    /// (a file name such as "Road. Phase2.dwg") is never split (Codex 14:34). The notes in the run evidence are unchanged.
    /// </summary>
    internal static string CorridorLines(IEnumerable<string> notes)
    {
        var text = string.Join("\n", notes);
        var lines = new StringBuilder(text.Length + 8);
        var insideLtr = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == Lrm) insideLtr = !insideLtr;
            lines.Append(c);
            if (insideLtr || i + 1 >= text.Length || text[i + 1] != ' ') continue;
            var sentenceEnd = c == '.';
            var nameFollows = c == ':' && i + 2 < text.Length && text[i + 2] == Lrm;
            // "… של ‎name‎": the drawing name starts its own line, so the box never wraps inside it (b14 live 17:31).
            var ofName = i + 5 < text.Length && text[i + 1] == ' ' && text[i + 2] == 'ש' && text[i + 3] == 'ל' &&
                         text[i + 4] == ' ' && text[i + 5] == Lrm;
            if (ofName)
            {
                lines.Append('\n');
                i++;   // the space before "של" becomes the line break
                continue;
            }
            if (!sentenceEnd && !nameFollows) continue;
            lines.Append('\n');
            i++;   // the space becomes the line break
        }
        return lines.ToString();
    }
}
