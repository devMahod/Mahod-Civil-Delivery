using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>
    /// Turns the Hebrew text stored in legacy coordination drawings into readable Unicode.
    ///
    /// Real project sheets carry Hebrew in THREE different shapes, verified against
    /// MHD-UT-000-000RD383-P1-30XX.dwg on 2026-08-12:
    ///
    /// 1. **Proper Unicode** (U+05D0–U+05EA) — returned untouched.
    ///
    /// 2. **Keyboard-mapped ASCII**, used by the Hebrew SHX fonts (mirym, miryl.shx,
    ///    mvt-mirym). Each Hebrew letter is stored as the Latin key that types it on an
    ///    Israeli keyboard: "np,j dkhubu," is מפתח גליונות. Logical order, no reversal.
    ///
    /// 3. **CP862 bytes read through CP1255** — used by TTF styles (arial.ttf) in the
    ///    survey xref. The bytes are DOS-Hebrew (CP862: 0x80=א … 0x9A=ת) but the reader
    ///    decoded them as Windows-Hebrew, so the bytes CP1255 defines became typographic
    ///    punctuation (0x92 → U+2019 ') while the ones it leaves undefined survived as raw
    ///    C1 characters (0x8E stayed 0x8E). Mapping each character back to its byte and
    ///    then through CP862 recovers the letters; that text is stored in VISUAL order,
    ///    so it also needs a bidi-aware reversal ("ם,י,מ" → מים).
    ///
    /// Decoding is best-effort and presentation-only: every detector works on geometry, so
    /// a mis-decode can never change which findings are reported — only how they read.
    /// </summary>
    public static class HebrewCadTextDecoder
    {
        private const char HebrewFirst = 'א';
        private const char HebrewLast = 'ת';

        /// <summary>Israeli keyboard layout: Latin key → the Hebrew letter it types.</summary>
        private static readonly Dictionary<char, char> KeyboardMap = new()
        {
            ['t'] = 'א', ['c'] = 'ב', ['d'] = 'ג', ['s'] = 'ד', ['v'] = 'ה',
            ['u'] = 'ו', ['z'] = 'ז', ['j'] = 'ח', ['y'] = 'ט', ['h'] = 'י',
            ['f'] = 'כ', ['l'] = 'ך', ['k'] = 'ל', ['n'] = 'מ', ['o'] = 'ם',
            ['b'] = 'נ', ['i'] = 'ן', ['x'] = 'ס', ['g'] = 'ע', ['p'] = 'פ',
            [';'] = 'ף', ['m'] = 'צ', ['.'] = 'ץ', ['e'] = 'ק', ['r'] = 'ר',
            ['a'] = 'ש', [','] = 'ת', ['q'] = '/', ['w'] = '\'', ['/'] = '.',
            ['\''] = ','
        };

        /// <summary>CP862 high bytes 0x80–0x9A, in order: א ב ג ד ה ו ז ח ט י ך כ ל ם מ ן נ ס ע ף פ ץ צ ק ר ש ת.</summary>
        private const string Cp862High = "אבגדהוזחטיךכלםמןנסעףפץצקרשת";

        /// <summary>
        /// CP1255's C1 punctuation mappings, inverted: the Unicode character the reader
        /// produced → the original byte. Bytes CP1255 leaves undefined never appear here
        /// because they survived unchanged.
        /// </summary>
        private static readonly Dictionary<char, int> Cp1255PunctuationToByte = new()
        {
            ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84,
            ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
            ['‰'] = 0x89, ['‹'] = 0x8B, ['‘'] = 0x91, ['’'] = 0x92,
            ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96,
            ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['›'] = 0x9B
        };

        /// <summary>
        /// Font-file stems known to be Hebrew SHX faces in these projects. A font on this
        /// list is proof that ASCII content is keyboard-mapped Hebrew rather than English.
        /// </summary>
        private static readonly string[] HebrewShxStems =
        {
            "mirym", "miryl", "mvt", "hebrew", "david", "aharoni", "gilam",
            "narkis", "frank", "hadassah", "hatzvi", "koren", "ktav", "sharon", "heb"
        };

        /// <summary>
        /// Short, very common English words. Their presence as whole tokens means the text
        /// is English written in a non-Unicode font, not keyboard-mapped Hebrew.
        /// </summary>
        private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "to", "be", "of", "in", "for", "is", "by", "on", "at", "with",
            "new", "existing", "line", "pipe", "water", "road", "sheet", "plan", "scale",
            "drawing", "project", "detail", "section", "notes", "legend", "type", "no"
        };

        /// <summary>Hebrew final forms; they may only legally end a word.</summary>
        private const string FinalForms = "ךםןףץ";

        /// <summary>
        /// Decodes one CAD string. <paramref name="fontFile"/> is the text style's font file
        /// (e.g. "mirym", "arial.ttf") and is what separates keyboard-mapped Hebrew from
        /// genuine English; pass null when it is unknown and the conservative statistical
        /// guard is used instead.
        /// </summary>
        public static string Decode(string? raw, string? fontFile = null)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;

            // Already real Hebrew — nothing to do.
            if (raw.Any(IsHebrewLetter)) return raw;

            if (raw.Any(ch => ch > 0x7F)) return DecodeCp862ViaCp1255(raw);

            return LooksKeyboardMapped(raw, fontFile) ? DecodeKeyboard(raw) : raw;
        }

        /// <summary>True when the character is a Unicode Hebrew letter.</summary>
        public static bool IsHebrewLetter(char c) => c >= HebrewFirst && c <= HebrewLast;

        /// <summary>
        /// Decides whether pure-ASCII text is keyboard-mapped Hebrew. A known Hebrew SHX
        /// font settles it; otherwise the text must be lowercase-dominated, fully covered
        /// by the keyboard map, and free of common English words.
        /// </summary>
        internal static bool LooksKeyboardMapped(string raw, string? fontFile)
        {
            // Digit-dominated strings ("57.56") must never be decoded — '.' maps to ץ and
            // would turn a chainage into gibberish. This was a real bug in the spike.
            int letters = raw.Count(ch => ch is >= 'a' and <= 'z');
            if (letters < 2) return false;
            if (raw.Count(char.IsDigit) > letters) return false;

            // Uppercase content is English (Hebrew keyboard mapping only produces lowercase).
            if (raw.Count(ch => ch is >= 'A' and <= 'Z') > letters) return false;

            foreach (var token in raw.Split(' ', '-', '/', ':', '(', ')'))
            {
                if (EnglishStopWords.Contains(token.Trim())) return false;
            }

            if (IsHebrewShxFont(fontFile)) return true;

            // Unknown font: require near-total coverage by the keyboard map.
            int mappable = raw.Count(ch => KeyboardMap.ContainsKey(char.ToLowerInvariant(ch)));
            int considered = raw.Count(ch => !char.IsWhiteSpace(ch) && !char.IsDigit(ch));
            return considered > 0 && (double)mappable / considered >= 0.9;
        }

        internal static bool IsHebrewShxFont(string? fontFile)
        {
            if (string.IsNullOrWhiteSpace(fontFile)) return false;
            var stem = fontFile.Trim().ToLowerInvariant();
            if (stem.EndsWith(".ttf", StringComparison.Ordinal) ||
                stem.EndsWith(".otf", StringComparison.Ordinal))
            {
                return false;
            }
            return HebrewShxStems.Any(s => stem.Contains(s, StringComparison.Ordinal));
        }

        private static string DecodeKeyboard(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                var lower = char.ToLowerInvariant(ch);
                sb.Append(KeyboardMap.TryGetValue(lower, out var heb) ? heb : ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps each character back to the byte it came from, re-reads that byte as CP862,
        /// and restores logical order. Returns the input unchanged when the result does not
        /// look like Hebrew, so genuinely accented Latin text is never mangled.
        /// </summary>
        private static string DecodeCp862ViaCp1255(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            int converted = 0;

            foreach (var ch in raw)
            {
                int b;
                if (Cp1255PunctuationToByte.TryGetValue(ch, out var mapped)) b = mapped;
                else if (ch <= 0xFF) b = ch;
                else { sb.Append(ch); continue; }

                if (b >= 0x80 && b < 0x80 + Cp862High.Length)
                {
                    sb.Append(Cp862High[b - 0x80]);
                    converted++;
                }
                else
                {
                    sb.Append(ch);
                }
            }

            if (converted == 0) return raw;

            // Text stored this way comes from DOS-era CAD, which wrote Hebrew in visual
            // order, so reversal is the rule rather than the exception here. Only step
            // aside when the letters are demonstrably already logical — a final form
            // closing a word — because for short labels such as ע.ת (עמוד תאורה) there is
            // no final form to detect and guessing "already logical" would leave every one
            // of them backwards.
            var decoded = sb.ToString();
            return LooksLogicalOrder(decoded) ? decoded : ReverseRuns(decoded);
        }

        /// <summary>
        /// Reverses visually-stored Hebrew back to logical order when it can be shown to be
        /// reversed; text that is already logical is returned untouched.
        /// </summary>
        internal static string RestoreLogicalOrder(string text) =>
            LooksVisuallyReversed(text) ? ReverseRuns(text) : text;

        /// <summary>
        /// Reverses the character order, keeping digit and Latin runs internally intact so
        /// "250" does not become "052".
        /// </summary>
        internal static string ReverseRuns(string text)
        {
            var runs = new List<string>();
            var current = new StringBuilder();
            bool? currentIsNeutralRun = null;

            foreach (var ch in text)
            {
                // Digits and Latin letters keep their internal order; everything else
                // (Hebrew, punctuation, spaces) is reversed character by character.
                bool isNeutral = char.IsDigit(ch) || (ch is >= 'A' and <= 'Z') || (ch is >= 'a' and <= 'z');
                if (currentIsNeutralRun is null || isNeutral == currentIsNeutralRun)
                {
                    current.Append(ch);
                    currentIsNeutralRun = isNeutral;
                    continue;
                }

                runs.Add(currentIsNeutralRun.Value ? current.ToString() : Reverse(current.ToString()));
                current.Clear();
                current.Append(ch);
                currentIsNeutralRun = isNeutral;
            }

            if (current.Length > 0)
            {
                runs.Add(currentIsNeutralRun == true ? current.ToString() : Reverse(current.ToString()));
            }

            runs.Reverse();
            return string.Concat(runs);
        }

        /// <summary>
        /// Visual storage is detectable structurally: a Hebrew final form (ךםןףץ) may only
        /// end a word, so finding one at the START of a multi-letter word means the letters
        /// were written out backwards.
        /// </summary>
        internal static bool LooksVisuallyReversed(string text)
        {
            foreach (var word in text.Split(' ', '\t', '\n'))
            {
                var letters = word.Where(IsHebrewLetter).ToArray();
                if (letters.Length < 2) continue;

                if (FinalForms.Contains(letters[0])) return true;
                if (FinalForms.Contains(letters[^1])) return false;
            }
            return false;
        }

        /// <summary>
        /// The opposite evidence: a final form closing a word proves the letters are already
        /// in logical order. Absence of proof is not proof of absence — most short labels
        /// contain no final form at all — so callers decide what the undecided case means.
        /// </summary>
        internal static bool LooksLogicalOrder(string text)
        {
            foreach (var word in text.Split(' ', '\t', '\n'))
            {
                var letters = word.Where(IsHebrewLetter).ToArray();
                if (letters.Length < 2) continue;

                if (FinalForms.Contains(letters[^1])) return true;
                if (FinalForms.Contains(letters[0])) return false;
            }
            return false;
        }

        private static string Reverse(string s)
        {
            var arr = s.ToCharArray();
            Array.Reverse(arr);
            return new string(arr);
        }
    }
}
