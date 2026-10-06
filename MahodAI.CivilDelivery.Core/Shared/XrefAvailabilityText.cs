using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// 1.4.1 D1 (984 live b37; agreed with Codex 06.10 13:01): an XREF the drawing itself keeps unloaded blocked every
    /// section with "אינו טעון/פתור", and the engineer did not know that the fix is a Reload in Civil. The policy does
    /// not change — an unread source still blocks. Only the words do: an explicitly unloaded XREF (a load state the user
    /// chose) is told apart from an unresolved one (the saved path does not resolve), each with its own way out, and the
    /// palette summarises them once per XREF name instead of one line per insertion.
    /// </summary>
    public static class XrefAvailabilityText
    {
        private static readonly Regex MessagePattern = new(
            @"^xref=(?<name>[^;]*); state=(?<state>unloaded|unresolved); path=(?<path>.*)$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        public static string Title(string? name, bool isUnloaded, string consequence) =>
            (isUnloaded
                ? $"ה-XREF '{Bidi.Ltr(name ?? string.Empty)}' לא טעון בשרטוט (Unloaded)"
                // Codex 13:21: !IsResolved proves "not resolved/loaded from the saved path", not that the file is gone.
                : $"ה-XREF '{Bidi.Ltr(name ?? string.Empty)}' לא פתור/לא נטען מהנתיב השמור") + " — " + consequence;

        /// <summary>Machine-readable so the palette can group by name; the path is kept as before.</summary>
        public static string Message(string? name, bool isUnloaded, string? path) =>
            $"xref={name ?? string.Empty}; state={(isUnloaded ? "unloaded" : "unresolved")}; path={path ?? string.Empty}";

        public static string Action(bool isUnloaded) => isUnloaded
            ? "יש לטעון אותו ב-Civil (XREF ‹Reload›), לשמור את השרטוט ולהריץ תכנון שוב."
            : "יש לבדוק ב-XREF Manager את Found At ואת מצב הטעינה, לתקן את הנתיב אם צריך, לטעון, לשמור ולהריץ תכנון שוב.";

        public readonly record struct Parsed(string Name, bool IsUnloaded, string Path);

        public static bool TryParse(DeliveryFinding? finding, out Parsed parsed)
        {
            parsed = default;
            if (finding == null || !string.Equals(finding.Code, SectionFindingCodes.XrefTraversalUnresolved, StringComparison.Ordinal))
                return false;
            var match = MessagePattern.Match(finding.Message ?? string.Empty);
            if (!match.Success) return false;
            parsed = new Parsed(match.Groups["name"].Value, match.Groups["state"].Value == "unloaded", match.Groups["path"].Value);
            return true;
        }

        /// <summary>At most this many names are listed per kind; the rest are counted.</summary>
        public const int MaxListedNames = 8;

        /// <summary>
        /// The palette block per kind (unloaded first), or null when no finding is an XREF-availability finding: a header
        /// line, then one XREF name per line, then the way out. b39 live (Codex 14:49): a comma list of long hyphenated
        /// names broke inside a name across RTL line wraps, so each name is its own LTR line and keeps its exact
        /// spelling for copying. Names are distinct and sorted; the count is of XREF names, not insertions or collectors.
        /// </summary>
        public static string? Summarize(IEnumerable<DeliveryFinding> findings)
        {
            var parsed = findings.Select(f => TryParse(f, out var p) ? p : (Parsed?)null)
                .Where(p => p != null).Select(p => p!.Value).ToList();
            if (parsed.Count == 0) return null;
            List<string> Distinct(IEnumerable<Parsed> items) => items.Select(p => p.Name)
                .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            IEnumerable<string> NameLines(List<string> names) => names.Take(MaxListedNames).Select(n => "• " + Bidi.Ltr(n))
                .Concat(names.Count > MaxListedNames ? new[] { $"• ועוד {names.Count - MaxListedNames}" } : Array.Empty<string>());
            var blocks = new List<string>();
            var unloaded = Distinct(parsed.Where(p => p.IsUnloaded));
            var unresolved = Distinct(parsed.Where(p => !p.IsUnloaded));
            int Count(bool isUnloaded) => parsed.Where(p => p.IsUnloaded == isUnloaded).Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (Count(true) > 0)
                blocks.Add(string.Join("\n", new[] { $"{Count(true)} XREF לא טעונים בשרטוט:" }.Concat(NameLines(unloaded))
                    .Append("כדי לתכנן חתכים יש לטעון אותם ב-Civil (XREF ‹Reload›), לשמור ולהריץ תכנון שוב.")));
            if (Count(false) > 0)
                blocks.Add(string.Join("\n", new[] { $"{Count(false)} XREF לא פתורים/לא נטענו מהנתיב השמור:" }.Concat(NameLines(unresolved))
                    .Append("יש לבדוק ב-XREF Manager את Found At ואת הטעינה, לשמור ולהריץ תכנון שוב.")));
            return string.Join("\n", blocks);
        }

        /// <summary>
        /// One short line for a place that must not repeat the name list (the summary above the sections table, Codex
        /// 15:20): counts per kind and where the names and the way out are shown. Null when nothing is an XREF finding.
        /// </summary>
        public static string? SummarizeCount(IEnumerable<DeliveryFinding> findings)
        {
            var parsed = findings.Select(f => TryParse(f, out var p) ? p : (Parsed?)null)
                .Where(p => p != null).Select(p => p!.Value).ToList();
            if (parsed.Count == 0) return null;
            int Count(bool isUnloaded) => parsed.Where(p => p.IsUnloaded == isUnloaded).Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var parts = new List<string>();
            if (Count(true) > 0) parts.Add($"{Count(true)} XREF לא טעונים בשרטוט");
            if (Count(false) > 0) parts.Add($"{Count(false)} XREF לא פתורים/לא נטענו מהנתיב השמור");
            return string.Join(" ו-", parts) + " — השמות ודרך הטיפול מפורטים בכרטיס הפעולה ובפרטי החסימה";
        }
    }
}
