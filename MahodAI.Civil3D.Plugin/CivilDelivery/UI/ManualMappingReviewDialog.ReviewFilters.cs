using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public sealed partial class ManualMappingReviewDialog
{
    internal sealed record ReviewFilter(string Id, string Label);
    internal readonly ComboBox GroupStateFilter = ReviewCombo(), MeasurementFilter = ReviewCombo();

    private static ComboBox ReviewCombo() => new()
    {
        DisplayMemberPath = nameof(ReviewFilter.Label), SelectedValuePath = nameof(ReviewFilter.Id),
        MinWidth = 110, Margin = new Thickness(0, 3, 6, 3),
        Foreground = System.Windows.Media.Brushes.Black, Background = System.Windows.Media.Brushes.White,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private void InitializeGroupOverviews()
    {
        foreach (var row in _rows)
        {
            var proposals = row.Subject.Proposals.OrderByDescending(proposal => proposal.Score)
                .ThenBy(proposal => proposal.ProposedCode, StringComparer.Ordinal).ToArray();
            row.ProposalSearch = string.Join(" ", proposals.Select(proposal =>
                proposal.ProposedCode + " " + proposal.CatalogDescription + " " +
                (_catalog.Items.TryGetValue(proposal.ProposedCode, out var item) ? item.Description : "") +
                " " + string.Join(" ", proposal.Reasons)));
            var candidates = proposals.Where(proposal =>
                _catalog.Items.TryGetValue(proposal.ProposedCode, out var item) &&
                item.Unit.SameUnit(Units.Parse(row.Subject.Unit)))
                .DistinctBy(proposal => proposal.ProposedCode, StringComparer.OrdinalIgnoreCase).ToArray();
            var best = candidates.FirstOrDefault();
            row.HasCatalogProposal = best != null;
            row.OverviewDetail = row.Layer + "\n" + (best == null ? "אין הצעה תואמת במחירון" :
                string.Join("\n\n", candidates.Select(proposal => proposal.ProposedCode + " · " +
                    _catalog.Items[proposal.ProposedCode].Description + "\n" + string.Join("; ", proposal.Reasons)))) +
                "\nהצעות בלבד — נדרשת בדיקה מפורשת; אין אישור מדידה או מחיר.";
            // Keep browsing dense even for long real layer names/descriptions.
            // Full alternatives and reasons remain in the tooltip and selected details.
            row.Overview = ShortReviewText(row.Layer, 65) + (best == null ? "\nאין הצעה תואמת\nחיפוש ידני במחירון" :
                "\n" + best.ProposedCode + (candidates.Length > 1 ? $" (מתוך {candidates.Length})" : "") +
                "\n" + ShortReviewText(_catalog.Items[best.ProposedCode].Description, 90));
        }
    }

    private static string ShortReviewText(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + "…";

    private void AddGroupReviewFilters(StackPanel panel)
    {
        GroupStateFilter.ItemsSource = new[]
        {
            new ReviewFilter("all", "כל המצבים"), new ReviewFilter("pending", "טרם שויכו"),
            new ReviewFilter("mapped", "שיוך קיים"), new ReviewFilter("readonly", "לקריאה בלבד"),
            new ReviewFilter("proposed", "יש הצעה לבדיקה"), new ReviewFilter("no-proposal", "ללא הצעה תואמת"),
        };
        MeasurementFilter.ItemsSource = new[] { new ReviewFilter("all", "כל סוגי המדידה") }.Concat(
            _rows.Select(row => row.Subject.MeasurementKind).Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal).Select(kind => new ReviewFilter(kind, kind switch
                { "length" => "אורך", "area" => "שטח", "volume" => "נפח", "count" => "ספירה", _ => kind }))).ToArray();
        GroupStateFilter.Width = 125; MeasurementFilter.Width = 115;
        GroupStateFilter.SelectedValue = "all"; MeasurementFilter.SelectedValue = "all";
        GroupStateFilter.ToolTip = "סינון תצוגה בלבד — אינו מחריג, משייך או מאשר קבוצה.";
        MeasurementFilter.ToolTip = "סוג המדידה אינו מחליף בדיקת יחידה והתאמת סעיף.";
        var filters = new WrapPanel(); filters.Children.Add(GroupStateFilter); filters.Children.Add(MeasurementFilter);
        ChosenOnly.Content = Text("בחירות לשמירה");
        ChosenOnly.Margin = new Thickness(0);
        ChosenOnly.ToolTip = "הצג רק בחירות שנוספו ולא נשמרו; ניתן לבדוק ולבטל אותן לפני האישור. חיפוש וסוג מדידה עדיין מסננים את התצוגה.";
        filters.Children.Add(ChosenOnly);
        panel.Children.Add(filters);
        GroupStateFilter.SelectionChanged += (_, _) => RefreshGroups();
        MeasurementFilter.SelectionChanged += (_, _) => RefreshGroups();
    }

    private bool MatchesReviewFilters(GroupRow row)
    {
        if (MeasurementFilter.SelectedValue is string kind && kind != "all" && row.Subject.MeasurementKind != kind)
            return false;
        if (ChosenOnly.IsChecked == true) return true;
        return (GroupStateFilter.SelectedValue as string) switch
        {
            "pending" => string.IsNullOrWhiteSpace(row.Subject.CurrentCode) && row.Subject.ReadOnlyReason == null &&
                !_choices.ContainsKey(row.Subject.RuleKey),
            "mapped" => !string.IsNullOrWhiteSpace(row.Subject.CurrentCode),
            "readonly" => row.Subject.ReadOnlyReason != null,
            "proposed" => row.HasCatalogProposal,
            "no-proposal" => !row.HasCatalogProposal,
            _ => true,
        };
    }
}
