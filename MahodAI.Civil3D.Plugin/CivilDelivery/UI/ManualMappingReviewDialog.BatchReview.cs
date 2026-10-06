using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MahodAI.CivilDelivery.Estimate;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public sealed partial class ManualMappingReviewDialog
{
    internal readonly Button BatchStageButton = ActionButton("הוסף סעיף למסומנות"),
        BatchClearButton = ActionButton("נקה סימון"),
        BatchSelectVisibleButton = ActionButton("סמן הצעות מוצגות");
    internal readonly TextBlock BatchStatus = Text("");
    private bool _updatingBatchMarks;

    private void AddBatchReview(StackPanel filters)
    {
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(GroupRow.MarkedForBatch)) { Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetValue(FrameworkElement.ToolTipProperty, "סימון לשימוש חוזר בסעיף נבחר; אינו שיוך ואינו אישור.");
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        check.SetValue(Control.ForegroundProperty, Brushes.White);
        GroupsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "סמן", Width = 38, CellTemplate = new DataTemplate { VisualTree = check },
        });
        var actions = new WrapPanel();
        BatchStageButton.Padding = BatchClearButton.Padding = BatchSelectVisibleButton.Padding = new Thickness(5, 3, 5, 3);
        actions.Children.Add(BatchSelectVisibleButton);
        actions.Children.Add(BatchStageButton); actions.Children.Add(BatchClearButton);
        BatchStatus.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        // Keep long refusal details reachable without consuming the group table.
        // The staging/clear actions remain outside the bounded text viewport.
        actions.Children.Add(new ScrollViewer { Content = BatchStatus, MaxHeight = 40, MaxWidth = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        filters.Children.Add(actions);
        BatchStageButton.Click += (_, _) => StageMarkedGroups();
        BatchClearButton.Click += (_, _) => ClearBatchMarks();
        BatchSelectVisibleButton.Click += (_, _) => SelectVisibleEligibleGroups();
        foreach (var row in _rows) row.PropertyChanged += BatchMarkChanged;
    }

    private void BatchMarkChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_updatingBatchMarks || args.PropertyName != nameof(GroupRow.MarkedForBatch)) return;
        ResetConfirmation(); RefreshBatchReview();
    }

    private ManualMappingBatchPolicy.Result EvaluateBatch()
    {
        var marked = _rows.Where(row => row.MarkedForBatch).Select(row => row.Subject).ToArray();
        var visible = GroupsGrid.Items.Cast<GroupRow>().Select(row => row.Subject.RuleKey).ToHashSet(StringComparer.Ordinal);
        return ManualMappingBatchPolicy.Evaluate(marked, visible,
            CatalogGrid.SelectedItem as CatalogOption, _catalog, _choices.Values.ToArray());
    }

    private void RefreshBatchReview()
    {
        var count = _rows.Count(row => row.MarkedForBatch);
        var result = EvaluateBatch();
        BatchStageButton.IsEnabled = result.CanStage;
        BatchClearButton.IsEnabled = count > 0;
        var eligible = VisibleEligibleRows();
        BatchSelectVisibleButton.IsEnabled = eligible.Length > 0;
        BatchSelectVisibleButton.ToolTip = "מחליף את כל הסימונים: רק מוצגות שהסעיף הנבחר כבר הוצע להן וביחידה תואמת, ללא שיוך קיים, חלופה או בחירה שכבר נוספה. ההצעה אינה הוכחה להתאמה הנדסית. קבוצות ללא הצעה אפשר לסמן ידנית. אינו מוסיף או שומר.";
        BatchStatus.Text = count == 0 ? $"{eligible.Length:N0} מתוך {GroupsGrid.Items.Count:N0} מוצגות עם הצעת הסעיף; סימון ≠ שמירה" :
            $"{count:N0} מסומנות · {_rows.Where(row => row.MarkedForBatch).Sum(row => (long)row.Subject.ObjectCount):N0} עצמים בקבוצות";
        BatchStatus.ToolTip = BatchStageButton.ToolTip = result.Detail;
        // Keep the refusal visible without requiring a hover or another modal.
        if (count > 0) BatchStatus.Text += result.CanStage ? " · מוכנות להוספה" : " · " + result.Detail;
        BatchStatus.MaxWidth = 330;
    }

    private GroupRow[] VisibleEligibleRows()
    {
        var staged = _choices.Values.ToArray();
        return GroupsGrid.Items.Cast<GroupRow>().Where(row => ManualMappingBatchPolicy.EligibleForVisibleSelection(
            row.Subject, CatalogGrid.SelectedItem as CatalogOption, _catalog, staged)).ToArray();
    }

    internal int SelectVisibleEligibleGroups()
    {
        var eligible = VisibleEligibleRows();
        // This explicit action replaces marks, never staged choices. Hidden marks
        // cannot hitchhike into a later save after a narrower filter is selected.
        _updatingBatchMarks = true;
        try
        {
            foreach (var row in _rows) row.MarkedForBatch = false;
            foreach (var row in eligible) row.MarkedForBatch = true;
        }
        finally { _updatingBatchMarks = false; }
        ResetConfirmation(); RefreshBatchReview();
        return eligible.Length;
    }

    internal bool StageMarkedGroups()
    {
        var result = EvaluateBatch();
        if (!result.CanStage) { RefreshBatchReview(); return false; }
        // Validate the complete set before mutating any staged choice. The existing
        // save callback revalidates source/profile/catalog identities and writes once.
        var byKey = result.Choices.ToDictionary(choice => choice.RuleKey, StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (!byKey.TryGetValue(row.Subject.RuleKey, out var choice)) continue;
            _choices[choice.RuleKey] = choice;
            row.SetDraft(choice);
        }
        ClearBatchMarks(); ResetConfirmation(); RefreshGroups(); RefreshSelection();
        return true;
    }

    internal void ClearBatchMarks()
    {
        _updatingBatchMarks = true;
        try { foreach (var row in _rows) row.MarkedForBatch = false; }
        finally { _updatingBatchMarks = false; }
        ResetConfirmation();
        RefreshBatchReview();
    }
}
