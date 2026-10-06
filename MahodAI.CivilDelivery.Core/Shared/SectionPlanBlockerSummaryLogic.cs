using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Engineer-facing Hebrew wording for plan-level blockers and for the section
    /// boundary (review of 1.3.9, 30/09).
    ///
    /// SEC-m1: the palette printed "התכנון חסום במקור: SEC-PROJECTION-GEOMETRY-UNSUPPORTED —
    /// גאומטריה בשכבה תואמת אינה ניתנת לקריאה" three times, naming no file, layer,
    /// section or action, right beside two rows marked «מוכן». Findings are now grouped
    /// by source file, layer and reason, worded from their own evidence, and the codes,
    /// handles and coordinates stay in the details panel.
    ///
    /// SEC-B1: a section whose approved ROW source has no ROW line at the cut must SAY
    /// so instead of silently ending at the last plan mark.
    /// </summary>
    public static class SectionPlanBlockerSummaryLogic
    {
        public const string GeometryUnsupportedCode = "SEC-PROJECTION-GEOMETRY-UNSUPPORTED";

        private static readonly Regex LayerPattern = new(@"(?:^|;\s*)layer=([^;]+)", RegexOptions.CultureInvariant);
        private static readonly Regex PointPattern = new(
            @"Self-intersection at or near point \(([-0-9.]+),\s*([-0-9.]+)\)", RegexOptions.CultureInvariant);

        /// <summary>
        /// One Hebrew line for the palette. <paramref name="sectionIdForRecord"/> maps a
        /// PLAN record id to the id the engineer sees in the table. Ready rows are said
        /// to be unaffected, so "blocked" and «מוכן» no longer contradict each other.
        /// </summary>
        public static string Describe(
            IReadOnlyList<DeliveryFinding> blockers,
            Func<string, string?> sectionIdForRecord,
            bool anyRecordReady,
            bool xrefNamesShownElsewhere = false)
        {
            if (blockers == null || blockers.Count == 0) return string.Empty;
            var lead = anyRecordReady ? "חלק מהחתכים חסומים: " : "התכנון חסום במקור: ";
            var parts = new List<string>();

            var geometry = blockers.Where(f => string.Equals(f.Code, GeometryUnsupportedCode, StringComparison.Ordinal))
                .ToList();
            foreach (var group in geometry
                         .GroupBy(f => (File: SourceFile(f), Layer: ShortLayer(f), Reason: ReasonKey(f.Message)))
                         .OrderBy(g => g.Key.File, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(g => g.Key.Layer, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(g => g.Key.Reason, StringComparer.Ordinal))
            {
                var count = group.Count();
                var what = HatchWord(group.First(), count);
                var where = (group.Key.Layer.Length > 0 ? " בשכבה " + group.Key.Layer : string.Empty) +
                            (group.Key.File.Length > 0 ? " בקובץ " + group.Key.File : string.Empty);
                var reason = ReasonText(group.Key.Reason, group.Select(f => f.Message));
                var sections = group.SelectMany(f => f.AffectedRecordIds)
                    .Select(id => sectionIdForRecord?.Invoke(id) ?? id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                var affected = sections.Count == 0
                    ? "החתכים המושפעים חסומים"
                    : "החתכים " + string.Join(", ", sections) + " חסומים";
                var notValid = count == 1 ? "אינה אזור סגור תקין" : "אינן אזור סגור תקין";
                parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {what}{where} {notValid} ({reason}) — {affected}");
            }
            if (geometry.Count > 0)
                parts.Add("יש לבדוק את ההצללה בקובץ המקור; ההחלטה אם לתקן את הקובץ היא של המהנדס/ת");

            // 1.4.1 D1: unavailable XREFs once per name, with the way out (Reload vs. fix the path).
            // r11: where the action card already lists the names, this line only counts them (no duplicate list).
            if ((xrefNamesShownElsewhere ? XrefAvailabilityText.SummarizeCount(blockers)
                    : XrefAvailabilityText.Summarize(blockers)) is { } xrefs) parts.Add(xrefs);

            // Other plan-level blockers: their Hebrew title only. The code stays in details.
            parts.AddRange(blockers
                .Where(f => !string.Equals(f.Code, GeometryUnsupportedCode, StringComparison.Ordinal))
                .Where(f => !XrefAvailabilityText.TryParse(f, out _))
                .Select(f => f.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.Ordinal)
                .Take(3));

            // 1.4.1: the XREF block is a vertical name list; then the parts go one per line instead of " · ".
            var separator = parts.Any(part => part.Contains('\n')) ? "\n" : " · ";
            var line = lead + string.Join(separator, parts);
            if (anyRecordReady)
                line += (separator == "\n" ? "\n" : " · ") + "החתכים המסומנים «מוכן» אינם מושפעים";
            return line;
        }

        /// <summary>
        /// SEC-B1 disclosure for one PLAN record. Null when a two-sided ROW envelope was
        /// found. Never invents an offset: it only says which evidence ends the section.
        /// </summary>
        public static string? DescribeBoundary(string? boundarySource, string? rowAuthorityState)
        {
            var authority = (rowAuthorityState ?? string.Empty).Trim().ToLowerInvariant();
            var approvedOrNone = authority is "authoritative" or "nocandidates";
            return (boundarySource ?? string.Empty) switch
            {
                "row" => null,
                "row-incomplete" =>
                    "זכות דרך: נמצא קו ROW בצד אחד בלבד במקור המאושר — החתך אינו מוצג עם זכות דרך מלאה.",
                "plan-mark-extents" when authority == "authoritative" =>
                    "זכות דרך לא נמצאה במקור המאושר בחתך זה — החתך מסתיים בסימון התכנית האחרון (לא בקו זכות דרך).",
                "plan-mark-extents" when approvedOrNone =>
                    "זכות דרך: אין מקור ROW בשרטוט — החתך מסתיים בסימון התכנית האחרון (לא בקו זכות דרך).",
                "plan-mark-extents" =>
                    "זכות דרך: מקור ROW טרם אושר ע\"י המהנדס/ת (כפתור «אשר מקור ROW») — החתך מסתיים בסימון התכנית האחרון.",
                "cl-extents" =>
                    "זכות דרך וסימוני תכנית לא נמצאו משני צדי הציר — גבולות החתך הם קצות קו ה-CL בלבד.",
                _ => "גבולות החתך טרם הוכחו (אין ROW ואין סימוני תכנית משני צדי הציר).",
            };
        }

        private static string SourceFile(DeliveryFinding finding)
        {
            var path = finding.SourceRefs?.Select(r => r.SourcePathOrUri)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        private static string ShortLayer(DeliveryFinding finding)
        {
            var layer = finding.SourceRefs?.Select(r => r.Layer).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (string.IsNullOrWhiteSpace(layer))
            {
                var match = LayerPattern.Match(finding.Message ?? string.Empty);
                layer = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            }
            var bar = layer!.LastIndexOf('|');
            return bar >= 0 ? layer[(bar + 1)..] : layer;
        }

        /// <summary>open | self-intersection | other</summary>
        public static string ReasonKey(string? message)
        {
            var text = message ?? string.Empty;
            if (text.Contains("Self-intersection", StringComparison.OrdinalIgnoreCase)) return "self-intersection";
            if (text.Contains("NotClosed", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not geometrically closed", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("disconnected", StringComparison.OrdinalIgnoreCase)) return "open";
            return "other";
        }

        private static string ReasonText(string key, IEnumerable<string?> messages) => key switch
        {
            "open" => "לולאה פתוחה",
            "self-intersection" => "חיתוך עצמי" + NearPoints(messages),
            _ => "גאומטריה שאינה ניתנת לקריאה",
        };

        private static string NearPoints(IEnumerable<string?> messages)
        {
            var points = messages.Select(m => PointPattern.Match(m ?? string.Empty))
                .Where(m => m.Success)
                .Select(m => FormatPoint(m.Groups[1].Value, m.Groups[2].Value))
                .Where(p => p != null)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return points.Count == 0 ? string.Empty : " ליד " + string.Join(", ", points);
        }

        private static string? FormatPoint(string x, string y) =>
            double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
            double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py)
                ? "(" + px.ToString("F2", CultureInfo.InvariantCulture) + ", " +
                  py.ToString("F2", CultureInfo.InvariantCulture) + ")"
                : null;

        private static string HatchWord(DeliveryFinding finding, int count)
        {
            var isHatch = finding.SourceRefs?.Any(r => string.Equals(r.EntityType, "Hatch", StringComparison.OrdinalIgnoreCase)) == true ||
                          (finding.Message ?? string.Empty).Contains("entity=Hatch", StringComparison.OrdinalIgnoreCase);
            var isSidewalk = (finding.Message ?? string.Empty).Contains("kind=sidewalk", StringComparison.OrdinalIgnoreCase);
            if (isHatch)
                return isSidewalk ? (count == 1 ? "הצללת מדרכה" : "הצללות מדרכה") : (count == 1 ? "הצללה" : "הצללות");
            return count == 1 ? "צורה" : "צורות";
        }
    }
}
