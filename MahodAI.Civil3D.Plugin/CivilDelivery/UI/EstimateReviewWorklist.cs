using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Display-only accounting. Raw findings are not individual pricing decisions.
/// Never feeds a filtered subset back to measurement, approval, totals or export.</summary>
internal static class EstimateReviewWorklist
{
    internal sealed record Summary(int TotalGroups, int PendingGroups, int ProposedGroups,
        int ApprovedGroups, int DrawingNoiseGroups, int RawBlockingFindings, int FindingKinds,
        string Text, string Detail);

    internal static Summary Build(IReadOnlyList<QuantityRowViewModel> rows,
        IEnumerable<DeliveryFinding> findings, bool sourceFresh)
    {
        var pending = rows.Where(row => row.CanApproveCatalogMapping &&
            string.IsNullOrWhiteSpace(row.CatalogCode)).ToList();
        var proposed = pending.Count(row => !string.IsNullOrWhiteSpace(row.ProposedCode));
        var approved = rows.Count(row => !row.IsHistorical && !row.IsIgnored &&
            !string.IsNullOrWhiteSpace(row.CatalogCode));
        var noise = rows.Count(row => row.IsBulkNoiseCandidate);
        var blockers = findings.Where(EstimatePreflightPolicy.IsBlocking).ToList();
        // Same rule as the row status: identity-less, blocking and not one of the decision-handled exempt codes.
        var projectWide = blockers.Where(finding => EstimateQuantityPresentationPolicy.IsProjectWide(finding) &&
            !EstimateQuantityPresentationPolicy.IsReviewExempt(finding)).ToList();
        var families = blockers.GroupBy(finding => finding.Code, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToList();
        var freshness = sourceFresh ? "" : "סריקה קודמת — לעיון בלבד. ";
        var text = freshness + $"{rows.Count:N0} קבוצות מדידה · {approved:N0} עם שיוך שמור · {proposed:N0} עם הצעות לבדיקה";
        if (noise > 0) text += $" · {noise:N0} קבוצות עזר לבדיקה יחד";
        if (blockers.Count > 0)
            text += $"\n{blockers.Count:N0} ממצאי מקור/מדידה ב־{families.Count:N0} סוגי בדיקה — לא מספר שיוכי המחירון" +
                (projectWide.Count == 0 ? "."
                    : $"; מתוכם {projectWide.Count:N0} ממצאים כלליים שדורשים טיפול לפני אומדן מלא — אינם כשל מדידה של הקבוצות.");
        var detail = $"{pending.Count:N0} קבוצות ללא שיוך; מתוכן {proposed:N0} עם הצעות ו־{pending.Count - proposed:N0} ללא הצעה.\n" +
            "הצעה אינה אישור. שיוך שנשמר בפרופיל משמש בסריקות הבאות; הסריקה עדיין בודקת מחדש את המקורות והכמויות.\n" +
            "קבוצות מדידה כוללות גם חלופות שטח/היקף וסימוני רקע. אין לחבר חלופות או להחריג מקורות בלי בדיקה.\n" +
            (projectWide.Count == 0 ? ""
                : $"כיסוי פרויקט: {projectWide.Count:N0} ממצאים כלליים (בלי קבוצה או מקור מזוהה) דורשים טיפול לפני אומדן מלא; " +
                  "הם אינם כשל מדידה של הקבוצות. פירוט ההשפעה על כל שורה — בחלון הקבוצה. " +
                  "אפשר להמשיך בינתיים 'שיוך קבוצות יחד'; השיוך נשמר.\n") +
            "פירוט הממצאים (לפי קוד, לא טענה שניתן לפתור את כולם בהחלטה אחת):\n" +
            string.Join("\n", families.Select(group => $"{group.Count():N0} · {group.First().Title} · {group.Key}"));
        return new(rows.Count, pending.Count, proposed, approved, noise, blockers.Count, families.Count, text, detail);
    }
}
