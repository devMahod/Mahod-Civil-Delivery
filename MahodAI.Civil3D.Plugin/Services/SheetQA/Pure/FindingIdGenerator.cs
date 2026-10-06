using System;
using System.Linq;
using System.Text;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>
    /// Issues the ids that tie a row in the chat report to the circle drawn on the sheet
    /// ("VS-3001-01-007"). The engineer reads the id off the drawing and finds the row, so
    /// it has to survive being written as plain DBText in a Hebrew-configured drawing:
    /// ASCII only, no spaces, no characters that a text style might not carry.
    /// </summary>
    public sealed class FindingIdGenerator
    {
        private const string Prefix = "VS";
        private const int MaxLayoutTokenLength = 16;

        private readonly string _layoutToken;
        private int _sequence;

        public FindingIdGenerator(string layoutName)
        {
            _layoutToken = SanitizeLayoutName(layoutName);
        }

        /// <summary>Returns the next id in sequence, starting at 001.</summary>
        public string Next()
        {
            _sequence++;
            return $"{Prefix}-{_layoutToken}-{_sequence:D3}";
        }

        /// <summary>How many ids have been issued.</summary>
        public int Count => _sequence;

        /// <summary>
        /// The short label drawn beside the circle on the sheet: the id's trailing sequence,
        /// without leading zeros ("VS-3001-01-007" → "7").
        /// </summary>
        /// <remarks>
        /// The full id is what the payload and any later fix pass use, but it is not what
        /// gets drawn. On a real coordination sheet "VS-LAYOUT1-125" came out as
        /// "521-1??????-??", because these drawings make a Hebrew SHX font current: it has
        /// no Latin glyphs and lays text out right-to-left. Digits survive both problems,
        /// and ids are numbered per sheet while the report groups by sheet, so the number
        /// on its own still points at exactly one row.
        /// </remarks>
        public static string MarkerLabel(string? findingId)
        {
            if (string.IsNullOrWhiteSpace(findingId)) return string.Empty;

            var tail = findingId!.Substring(findingId.LastIndexOf('-') + 1).TrimStart('0');
            if (tail.Length == 0) tail = "0";

            // An id in some other shape is better shown whole than cut down to nonsense.
            return tail.All(char.IsDigit) ? tail : findingId;
        }

        /// <summary>
        /// Reduces a layout name to an ASCII token. Layout tabs in these projects are named
        /// "3001-01" or "PL102", but they can also be Hebrew or carry punctuation, so any
        /// character that is not a letter or digit becomes a dash and non-ASCII is dropped.
        /// A name that reduces to nothing falls back to "SHEET".
        /// </summary>
        public static string SanitizeLayoutName(string? layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName)) return "SHEET";

            var sb = new StringBuilder(layoutName.Length);
            char? previous = null;
            foreach (var ch in layoutName.Trim())
            {
                char mapped;
                if (ch is >= '0' and <= '9' or >= 'A' and <= 'Z') mapped = ch;
                else if (ch is >= 'a' and <= 'z') mapped = char.ToUpperInvariant(ch);
                else mapped = '-';

                // Collapse runs of dashes so "sheet   01" does not become "SHEET---01".
                if (mapped == '-' && previous == '-') continue;

                sb.Append(mapped);
                previous = mapped;
            }

            var token = sb.ToString().Trim('-');
            if (token.Length == 0) return "SHEET";
            return token.Length > MaxLayoutTokenLength ? token[..MaxLayoutTokenLength].TrimEnd('-') : token;
        }
    }
}
