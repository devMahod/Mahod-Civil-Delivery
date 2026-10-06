using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CheckBox = System.Windows.Controls.CheckBox;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Detached editable source review; cancellation never modifies the project profile.</summary>
public partial class EstimateSourceReviewDialog : Window
{
    public EstimateSourceReviewDialog(string inventoryText, string scopeText)
    {
        InitializeComponent();
        UiGuard.Attach(this, "אישור מקורות האומדן");
        // Preserve the complete inventory and its existing Bidi.Ltr filename runs.
        InventoryText.Text = inventoryText ?? string.Empty;
        ScopeText.Text = scopeText ?? string.Empty;
    }

    public IReadOnlyList<SourceRow> Rows { get; private set; } = Array.Empty<SourceRow>();
    public IEnumerable<EstimateSourceChoice> Choices => Rows.Select(r => r.Choice);

    public EstimateSourceReviewDialog(EstimateSourceInventory inventory, EstimateSourceSelection? saved,
        string inventoryText, string scopeText) : this(inventoryText, scopeText)
    {
        Rows = EstimateSourceSelectionPolicy.CreateDraft(inventory, saved).Select(choice =>
            new SourceRow(choice, inventory.Sources.Single(s => s.Key == choice.Key).Status)).ToArray();
        foreach (var row in Rows) row.PropertyChanged += (_, _) => QueueRefresh();
        var view = new ListCollectionView(Rows.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SourceRow.CategoryLabel)));
        SourcesGrid.ItemsSource = view;
        SourcesGrid.Visibility = Visibility.Visible;
        InventoryExpander.IsExpanded = false;
        RefreshSummary();
    }

    private bool _refreshQueued;
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            if (SourcesGrid.ItemsSource is ICollectionView view) view.Refresh();
            RefreshSummary();
        }));
    }

    private void RefreshSummary()
    {
        if (Rows.Count == 0) return;
        var included = Rows.Count(r => r.Included);
        SelectionSummary.Text = $"{included} מקורות כלולים; {Rows.Count - included} מוחרגים. " +
            "הסימון כולל את הענף המקונן. מקור שנבחר ואינו זמין ימשיך לחסום את האומדן.";
        BtnApprove.IsEnabled = included > 0;
    }

    private void OnRowCheck(object sender, RoutedEventArgs e) => QueueRefresh();
    private void OnGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check && check.DataContext is CollectionViewGroup group)
        {
            // Clicking a mixed group includes all; clicking an all-included group excludes all.
            var rows = group.Items.Cast<SourceRow>().ToArray();
            var include = !rows.All(r => r.Included);
            foreach (var row in rows) row.Included = include;
            check.IsChecked = include;
            QueueRefresh();
        }
    }

    public sealed class SourceRow : INotifyPropertyChanged
    {
        public SourceRow(EstimateSourceChoice choice, string status)
        {
            Choice = choice;
            Status = status switch { "Resolved" => "טעון / פתור", "Unloaded" => "לא טעון", "Unresolved" => "לא פתור", "FileNotFound" => "קובץ חסר", _ => status };
        }
        public EstimateSourceChoice Choice { get; }
        public string Name => Choice.Key == EstimateSourceSelectionPolicy.HostKey ? "מארח · " + Choice.Name : Choice.Name;
        public string Path => Choice.Path;
        public string Status { get; }
        public IReadOnlyDictionary<string, string> CategoryOptions => EstimateSourceSelectionPolicy.Categories;
        public bool Included { get => Choice.Included; set { if (value == Choice.Included) return; Choice.Included = value; Changed(nameof(Included)); } }
        public string Category { get => Choice.Category; set { if (value == null || value == Choice.Category) return; Choice.Category = value; Changed(nameof(Category)); Changed(nameof(CategoryLabel)); } }
        public string CategoryLabel => EstimateSourceSelectionPolicy.Categories[Category];
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void OnApprove(object sender, RoutedEventArgs e) =>
        UiGuard.Run("אישור מקורות האומדן", () => DialogResult = true);

    private void OnCancel(object sender, RoutedEventArgs e) =>
        UiGuard.Run("ביטול אישור מקורות", () => DialogResult = false);
}

public sealed class SourceGroupCheckConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not CollectionViewGroup group) return null;
        var rows = group.Items.Cast<EstimateSourceReviewDialog.SourceRow>().ToArray();
        return rows.All(r => r.Included) ? true : rows.All(r => !r.Included) ? false : null;
    }
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
