using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Hebrew wording for the traffic-direction evidence an engineer reads in the directions dialog and
    /// the section detail. Display only: the stored reason codes and every decision stay unchanged, and a
    /// code without wording is shown as is rather than hidden (live 30.09.2026: the dialog showed
    /// "resolved · automatic-traffic-scope=cut-not-contained-in-one-native-straight-segment;…").
    /// </summary>
    public static class TrafficDirectionReasonText
    {
        private static readonly IReadOnlyDictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cut-not-contained-in-one-native-straight-segment"] = "החתך אינו על קטע ישר אחד של התוואי, לכן אין זיהוי אוטומטי מחצים",
            ["ambiguous-native-straight-segment"] = "החתך מתאים ליותר מקטע ישר אחד של התוואי, לכן אין זיהוי אוטומטי מחצים",
            ["invalid-native-straight-segment-evidence"] = "נתוני קטע התוואי אינם שלמים, לכן אין זיהוי אוטומטי מחצים",
            ["nearby-arrows-outside-proven-straight-segment"] = "חצים סמוכים מחוץ לקטע הישר לא נספרו",
            ["resolved-from-explicit-current-lane-edit"] = "נקבע באישור ידני לנתיב זה",
            ["explicit-current-lane-direction-v1"] = "אישור ידני לנתיב",
            ["resolved-from-approved-arrow"] = "נקבע מחץ תנועה מאושר בשרטוט",
            ["nearest-approved-arrows-agree"] = "החצים המאושרים הקרובים מסכימים",
            ["no-approved-arrow-in-range"] = "אין חץ תנועה מאושר בטווח — נדרש אישור ידני",
            ["nearest-arrows-conflict"] = "החצים הקרובים סותרים זה את זה — נדרש אישור ידני",
            ["nearest-arrow-heading-cancels"] = "כיוון החץ הקרוב אינו חד-משמעי — נדרש אישור ידני",
            ["nearest-approved-bike-arrows-agree"] = "חצי האופניים המאושרים הקרובים מסכימים",
            ["no-approved-bike-arrow-in-range"] = "אין חץ אופניים מאושר בטווח — נדרש אישור ידני",
            ["nearest-bike-arrows-conflict"] = "חצי האופניים הקרובים סותרים זה את זה — נדרש אישור ידני",
            ["nearest-bike-arrow-heading-cancels"] = "כיוון חץ האופניים הקרוב אינו חד-משמעי — נדרש אישור ידני",
            ["duplicate-explicit-direction-edits"] = "נמצא יותר מאישור ידני אחד לאותו נתיב — יש להשאיר אישור אחד",
            ["duplicate-manual-direction-decisions"] = "נמצאו החלטות ידניות כפולות לאותו נתיב — יש להשאיר החלטה אחת",
            ["restored-from-plan"] = "שוחזר מהתכנון הקודם",
            ["plan-evidence-restored"] = "שוחזר מהתכנון הקודם",
            ["invalid-lane-cut-scope"] = "תחום הנתיב בחתך אינו תקין",
            ["invalid-arrow-evidence-mode"] = "סוג ראיית החצים אינו תקין",
            ["invalid-lane-identity"] = "זהות הנתיב אינה תקינה",
            ["invalid-query"] = "בקשת הזיהוי אינה תקינה",
        };

        /// <summary>The plan stores the state as the lower-case enum name ("resolved", "unknown", "ambiguous").</summary>
        public static string State(string? state) => state switch
        {
            "resolved" => "נקבע",
            "ambiguous" => "סותר",
            "unknown" or null or "" => "לא נקבע",
            _ => state!,
        };

        public static string Flow(string? flow) => flow switch
        {
            SectionVehicleDirectionPlanner.AlongFlowToken => "עם כיוון הציר",
            SectionVehicleDirectionPlanner.AgainstFlowToken => "נגד כיוון הציר",
            null or "" => "לא נקבע",
            _ => flow!,
        };

        public static string Describe(string? state, string? reason)
        {
            var text = Reason(reason);
            return text.Length == 0 ? State(state) : State(state) + " · " + text;
        }

        /// <summary>Each ';' part in order; "automatic-traffic-scope=X" and "code:detail" keep their detail.</summary>
        public static string Reason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "";
            var parts = reason!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).Select(Part).Distinct(StringComparer.Ordinal);
            return string.Join(" · ", parts);
        }

        private static string Part(string part)
        {
            const string scopeKey = "automatic-traffic-scope=";
            if (part.StartsWith(scopeKey, StringComparison.Ordinal))
                return Part(part.Substring(scopeKey.Length));
            var colon = part.IndexOf(':');
            var code = colon < 0 ? part : part.Substring(0, colon);
            if (!Codes.TryGetValue(code, out var text)) return part;
            return colon < 0 ? text : $"{text} ({part.Substring(colon + 1)})";
        }
    }
}
