using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using Group = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog.Group;
using Choice = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog.Choice;
using CatalogOption = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog.CatalogOption;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Explicit same-item reuse only; never chooses semantics, price, or an alternative.</summary>
internal static class ManualMappingBatchPolicy
{
    internal sealed record Result(bool CanStage, string Detail, IReadOnlyList<Choice> Choices);

    internal static bool EligibleForVisibleSelection(Group group, CatalogOption? selected,
        CatalogSnapshot catalog, IReadOnlyList<Choice> staged) =>
        string.IsNullOrWhiteSpace(group.CurrentCode) &&
        selected != null && group.Proposals.Any(proposal =>
            string.Equals(proposal.RuleKey, group.RuleKey, StringComparison.Ordinal) &&
            string.Equals(proposal.ProposedCode, selected.Code, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(proposal.MeasurementKind, group.MeasurementKind, StringComparison.OrdinalIgnoreCase) &&
            Units.Parse(proposal.MeasuredUnit).SameUnit(Units.Parse(group.Unit))) &&
        !staged.Any(choice => choice.RuleKey == group.RuleKey) &&
        Evaluate(new[] { group }, new HashSet<string>(StringComparer.Ordinal) { group.RuleKey },
            selected, catalog, staged).CanStage;

    internal static Result Evaluate(IReadOnlyList<Group> marked, ISet<string> visible,
        CatalogOption? selected, CatalogSnapshot catalog, IReadOnlyList<Choice> staged)
    {
        Result Refuse(string detail) => new(false, detail, Array.Empty<Choice>());
        if (marked.Count == 0) return Refuse("סמן קבוצות ובחר סעיף לשימוש חוזר.");
        if (marked.Any(group => !visible.Contains(group.RuleKey)))
            return Refuse("יש מסומנות מוסתרות; נקה את סינון הקבוצות או את הסימון.");
        if (selected == null) return Refuse("בחר סעיף במחירון לפני הוספה.");
        if (!catalog.Items.TryGetValue(selected.Code, out var item) ||
            !item.Unit.SameUnit(Units.Parse(selected.Unit)))
            return Refuse("הסעיף אינו תואם למחירון הנוכחי; בחר אותו מחדש.");
        if (marked.Any(group => group.ReadOnlyReason != null))
            return Refuse("יש קבוצה לקריאה בלבד; בדוק אותה בנפרד.");
        if (marked.Any(group => !string.IsNullOrWhiteSpace(group.CurrentCode)))
            return Refuse("שיוך קיים נערך בנפרד; שימוש חוזר מרוכז אינו מחליף שיוכים קיימים.");
        var excluded = staged.Where(choice => choice.ExcludedAlternativeRuleKey != null)
            .Select(choice => choice.ExcludedAlternativeRuleKey!).ToHashSet(StringComparer.Ordinal);
        if (marked.Any(group => group.AlternativeRuleKey != null || excluded.Contains(group.RuleKey)))
            return Refuse("חלופות שטח/היקף נבחרות בנפרד, עם אישור מפורש.");
        if (marked.Any(group => !item.Unit.SameUnit(Units.Parse(group.Unit))))
            return Refuse("היחידות אינן תואמות בכל הקבוצות; לא נוספה אף בחירה.");
        if (marked.Any(group => string.IsNullOrWhiteSpace(group.RuleKey)) ||
            marked.Select(group => group.RuleKey).Distinct(StringComparer.Ordinal).Count() != marked.Count)
            return Refuse("זהות קבוצת הכמות אינה ייחודית.");
        return new(true, "הסעיף הנבחר יחליף את הבחירה לשמירה בכל המסומנות. אין אישור מחיר או מדידה.",
            marked.Select(group => new Choice(group.RuleKey, item.Code, null)).ToArray());
    }
}
