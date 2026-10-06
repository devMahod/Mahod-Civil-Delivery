using System;
using System.Windows;
using System.Windows.Data;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private ListCollectionView? _quantityReviewView;
    private QuantitySearchDebouncer? _quantitySearchDebouncer;
    private bool _refreshingQuantityFilter;
    private int _quantityFilterFingerprint;
    private QuantityReviewFilter.Mode QuantityFilterMode => QuantityFilterBox.SelectedIndex switch
    {
        1 => QuantityReviewFilter.Mode.Unmapped, 2 => QuantityReviewFilter.Mode.Mapped,
        3 => QuantityReviewFilter.Mode.Ignored, 4 => QuantityReviewFilter.Mode.History,
        5 => QuantityReviewFilter.Mode.Proposed, 6 => QuantityReviewFilter.Mode.WithoutProposal,
        7 => QuantityReviewFilter.Mode.Measurement, 8 => QuantityReviewFilter.Mode.DrawingNoise,
        _ => QuantityReviewFilter.Mode.All,
    };
    private void InitializeQuantityReviewFilter()
    {
        _quantitySearchDebouncer = new QuantitySearchDebouncer(Dispatcher,
            () => RefreshQuantityReviewFilter(force: true));
        Unloaded += (_, _) => _quantitySearchDebouncer.Cancel();
        Loaded += (_, _) => RefreshQuantityReviewFilter(force: true);
        _quantityReviewView = new ListCollectionView(_quantityRows)
        {
            Filter = value => value is QuantityRowViewModel row &&
                QuantityReviewFilter.Matches(row, QuantitySearchBox.Text, QuantityFilterMode),
        };
        QuantitiesGrid.ItemsSource = _quantityReviewView;
        _quantityRows.CollectionChanged += (_, _) => UpdateQuantityFilterCount();
        UpdateQuantityFilterCount();
    }
    private void OnQuantityFilterChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, QuantitySearchBox))
        {
            _quantitySearchDebouncer?.Schedule();
            return;
        }
        // Mode changes are explicit and immediate, including while typing is pending.
        _quantitySearchDebouncer?.Cancel();
        RefreshQuantityReviewFilter(force: true);
    }
    private void OnClearQuantityFilter(object sender, RoutedEventArgs e)
    {
        QuantitySearchBox.Clear(); QuantityFilterBox.SelectedIndex = 0;
        _quantitySearchDebouncer?.Cancel();
        RefreshQuantityReviewFilter(force: true);
    }
    private void RefreshQuantityReviewFilter(bool force = false)
    {
        if (_quantityReviewView == null || _refreshingQuantityFilter) return;
        if (force) _quantitySearchDebouncer?.Cancel();
        // Gate/status refreshes must not turn the debounce back into per-keystroke filtering.
        if (!force && _quantitySearchDebouncer?.IsPending == true) return;
        var fingerprint = new HashCode();
        fingerprint.Add(QuantitySearchBox.Text); fingerprint.Add(QuantityFilterMode);
        foreach (var row in _quantityRows)
        {
            fingerprint.Add(row.RuleKey); fingerprint.Add(row.CatalogCode); fingerprint.Add(row.ProposedCode);
            fingerprint.Add(row.CatalogDescription); fingerprint.Add(row.SourceCategory); fingerprint.Add(row.Findings);
            fingerprint.Add(row.ProposalSearchText); fingerprint.Add(row.Presentation?.MeasurementNeedsReview);
            fingerprint.Add(row.Presentation?.LocalMeasurementNeedsReview); fingerprint.Add(row.Presentation?.ProjectCoverageBlockers);
            fingerprint.Add(row.IsBulkNoiseCandidate); fingerprint.Add(row.IsUnselectedClosedPolylineAlternative);
            fingerprint.Add(row.IsIgnored); fingerprint.Add(row.IsHistorical);
        }
        var current = fingerprint.ToHashCode();
        if (!force && current == _quantityFilterFingerprint) { UpdateQuantityFilterCount(); return; }
        _quantityFilterFingerprint = current; _refreshingQuantityFilter = true;
        try { _quantityReviewView.Refresh(); UpdateQuantityFilterCount(); }
        finally { _refreshingQuantityFilter = false; }
    }
    private void UpdateQuantityFilterCount()
    {
        if (_quantityReviewView == null || QuantityFilterCount == null) return;
        QuantityFilterCount.Text = $"מוצגות {_quantityReviewView.Count:N0} מתוך {_quantityRows.Count:N0} קבוצות · סינון תצוגה בלבד; אינו משנה את האומדן או הייצוא";
    }
    private void SelectQuantityReviewRow(QuantityRowViewModel? row)
    {
        // Guided recovery must expose its exact target, never edit a hidden old selection.
        if (row != null && !QuantityReviewFilter.Matches(row, QuantitySearchBox.Text, QuantityFilterMode))
            OnClearQuantityFilter(this, new RoutedEventArgs());
        _quantitySearchDebouncer?.Cancel();
        RefreshQuantityReviewFilter(force: true);
        QuantitiesGrid.SelectedItem = row;
    }
}
