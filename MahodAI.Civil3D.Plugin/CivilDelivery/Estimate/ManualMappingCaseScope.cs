using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Matches the existing rebase comparer; does not approve, convert or exclude anything.</summary>
internal static class ManualMappingCaseScope
{
    internal sealed record Scope(string RuleKey, IReadOnlyList<string> RuleKeys,
        IReadOnlyList<NeutralQuantityRecord> Records, double? TotalQuantity, string? Refusal)
    {
        internal bool HasCaseVariants => RuleKeys.Count > 1;
    }

    internal static IReadOnlyList<Scope> Collect(IEnumerable<NeutralQuantityRecord> records, ProjectProfile profile) =>
        records.Where(record => !string.IsNullOrWhiteSpace(record.Classification.RuleKey))
            .GroupBy(record => record.Classification.RuleKey!, StringComparer.OrdinalIgnoreCase)
            .Select(group => Create(group.ToArray(), profile)).ToArray();

    private static Scope Create(NeutralQuantityRecord[] records, ProjectProfile profile)
    {
        var keys = records.Select(record => record.Classification.RuleKey!).Distinct(StringComparer.Ordinal).ToArray();
        var rules = profile.Estimate.QuantitySources.Rules.Where(rule =>
            string.Equals(rule.RuleKey, keys[0], StringComparison.OrdinalIgnoreCase)).ToArray();
        var key = rules.Length == 1 && keys.Contains(rules[0].RuleKey, StringComparer.Ordinal)
            ? rules[0].RuleKey : keys[0];
        string? refusal = null;
        if (rules.Length > 1 || (rules.Length == 1 && !keys.Contains(rules[0].RuleKey, StringComparer.Ordinal)))
            refusal = "קיימות זהויות כלל מתנגשות בפרופיל. יש להסדיר כלל אחד מדויק ולסרוק מחדש; לא נשמר אף שיוך.";
        if (keys.Length > 1)
        {
            if (records.Select(record => SectionProjectionLogic.LayerLeaf(record.Source.Layer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
                records.Any(record => string.IsNullOrWhiteSpace(record.Source.Layer) ||
                    string.IsNullOrWhiteSpace(record.Source.EntityType)))
                refusal ??= "זהות שכבה או סוג עצם אינה אחידה בהיקף השיוך. בדוק את המקורות לפני אישור.";
            var unit = Units.Parse(records[0].Measurement.Unit);
            if (records.Any(record => string.IsNullOrWhiteSpace(record.Measurement.Kind)) ||
                records.Select(record => record.Measurement.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
                records.Any(record => !Units.Parse(record.Measurement.Unit).SameUnit(unit)))
                refusal ??= "גרסאות שם באותיות שונות כוללות סוגי מדידה או יחידות שונים. בדוק את כל ההיקף; אין המרה או אישור חלקי.";
            if (records.Any(ClosedPolylineAlternativePolicy.IsClosedPolylineMeasurement))
                refusal ??= "גרסאות שם באותיות שונות כוללות גבול סגור. יש להסדיר קבוצת מקור אחידה ולבדוק שטח מול היקף; אין החרגת חלופה אוטומטית.";
            if (records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Length)
                refusal ??= "מזהי הרשומות בהיקף השיוך אינם ייחודיים. בדוק את ראיות הסריקה לפני אישור.";
        }
        var quantity = 0d;
        foreach (var record in records) quantity += record.Measurement.RawValue;
        if (!double.IsFinite(quantity))
            refusal ??= "סכום הכמויות אינו מספר סופי. יש לבדוק את המדידות; לא מוצג אפס ולא נשמר שיוך.";
        return new(key, Array.AsReadOnly(keys), Array.AsReadOnly(records),
            double.IsFinite(quantity) ? quantity : null, refusal);
    }

    internal static bool RequiresFullReview(IEnumerable<NeutralQuantityRecord> records, IEnumerable<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return records.Where(record => record.Classification.RuleKey != null && selected.Contains(record.Classification.RuleKey))
            .Select(record => record.Classification.RuleKey!).Distinct(StringComparer.Ordinal)
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
    }

    internal static void RequireLegacyScopeIsExact(IEnumerable<NeutralQuantityRecord> records, IEnumerable<string> selectedKeys)
    {
        if (RequiresFullReview(records, selectedKeys))
            throw new InvalidOperationException("לשיוך נבחר קיימות גרסאות שם באותיות שונות. פתח ׳שיוך ידני / עריכת שיוכים׳ ובדוק את כל העצמים והכמות לפני אישור; לא נשמר אף שיוך.");
    }
}
