using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Lets an engineer map ANY scanned layer - whatever it was named - to a catalog
    /// item, by searching the active price book in Hebrew or by code. Proposals come
    /// first; the whole book is one keystroke away. No code has to be remembered.
    ///
    /// The unit verdict is shown before approval: a length layer cannot be mapped to
    /// an area item, and the dialog says so instead of letting the engine refuse later.
    /// </summary>
    public partial class CatalogPickerDialog : Window
    {
        public sealed class Row
        {
            public required string Code { get; init; }
            public required string Description { get; init; }
            public required string Unit { get; init; }
            public decimal? Price { get; init; }
            public string PriceDisplay => Price is { } p ? p.ToString("N2") : "—";
            public required string Why { get; init; }
            public int Rank { get; init; }
            public bool UnitCompatible { get; init; }
        }

        private readonly CatalogSnapshot _catalog;
        private readonly string _measuredUnit;
        private readonly UnitDimension _measuredDimension;
        private readonly List<Row> _proposals;
        private readonly ObservableCollection<Row> _rows = new();

        /// <summary>The chosen catalog code, once the engineer approves.</summary>
        public string? SelectedCode { get; private set; }

        public CatalogPickerDialog(
            string ruleKey, string layer, string measurementKind, string measuredUnit,
            double totalQuantity, int objectCount,
            CatalogSnapshot catalog,
            IEnumerable<MappingProposal> proposals)
        {
            InitializeComponent();
            UiGuard.Attach(this, "שיוך שכבה לסעיף מחירון");
            _catalog = catalog;
            _measuredUnit = measuredUnit;
            _measuredDimension = Units.Parse(measuredUnit).Dimension;

            SubjectTitle.Text = $"שכבה: {Bidi.Ltr(layer)}";
            SubjectDetail.Text =
                $"{KindHe(measurementKind)} · {totalQuantity:N2} {measuredUnit} · {objectCount:N0} עצמים · קבוצה {Bidi.Ltr(ruleKey)}";

            _proposals = proposals
                .OrderByDescending(p => p.Score)
                .Select((p, i) => ToRow(p.ProposedCode, why: "הצעה " + (i + 1), rank: i))
                .Where(r => r != null)
                .Cast<Row>()
                .ToList();

            Grid.ItemsSource = _rows;
            ShowProposalsOnly();
            SearchBox.Focus();
        }

        // ----------------------------------------------------------- population

        private void ShowProposalsOnly()
        {
            _rows.Clear();
            foreach (var r in _proposals) _rows.Add(r);
            ResultCount.Text = _proposals.Count > 0 ? $"{_proposals.Count} הצעות" : "אין הצעות — חפש";
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            var q = SearchBox.Text.Trim();
            if (q.Length < 2) { ShowProposalsOnly(); return; }

            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = new List<Row>();

            foreach (var item in _catalog.Items.Values)
            {
                // Code prefix match (51.01) or every term in the description.
                var byCode = item.Code.Contains(q, StringComparison.OrdinalIgnoreCase);
                var byText = terms.All(t => item.Description.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!byCode && !byText) continue;

                var row = ToRow(item.Code, why: byCode ? "לפי סעיף" : "לפי תיאור", rank: 1000);
                if (row != null) matches.Add(row);
            }

            // Rank the entire matching set before limiting the visible grid. Stopping
            // at the first 400 catalog rows could hide every unit-compatible item.
            // Searching/ordering supplies no mapping approval; selection stays explicit.
            var proposalsByCode = _proposals.GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var ordered = matches.Select(row => proposalsByCode.TryGetValue(row.Code, out var proposal) ? proposal : row)
                .OrderByDescending(row => row.UnitCompatible)
                .ThenByDescending(row => string.Equals(row.Code, q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(row => row.Rank)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .Take(400)
                .ToList();

            _rows.Clear();
            foreach (var r in ordered) _rows.Add(r);
            ResultCount.Text = matches.Count > 400 ? "400+ תוצאות — צמצם את החיפוש" : $"{ordered.Count} תוצאות";
        }

        private Row? ToRow(string code, string why, int rank)
        {
            if (!_catalog.Items.TryGetValue(code, out var item)) return null;
            _catalog.Prices.TryGetValue(code, out var price);
            var compatible = item.Unit.SameUnit(Units.Parse(_measuredUnit));
            return new Row
            {
                Code = item.Code,
                Description = item.Description,
                Unit = item.UnitRaw,
                Price = price?.Price,
                Why = why,
                Rank = rank,
                UnitCompatible = compatible,
            };
        }

        // -------------------------------------------------------------- choice

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Grid.SelectedItem is not Row r)
            {
                BtnOk.IsEnabled = false;
                UnitVerdict.Text = "";
                return;
            }

            if (r.UnitCompatible)
            {
                UnitVerdict.Text = $"✓ יחידה תואמת: נמדד {_measuredUnit}, סעיף {r.Unit}" +
                                   (r.Price is null ? " · אין מחיר במהדורה זו (MISSING_PRICE)" : "");
                UnitVerdict.Foreground = (System.Windows.Media.Brush)FindResource("Ok");
                BtnOk.IsEnabled = true;
            }
            else
            {
                UnitVerdict.Text = $"✗ יחידה לא תואמת: נמדד {_measuredUnit}, הסעיף ב-{r.Unit} — לא ניתן לשייך";
                UnitVerdict.Foreground = (System.Windows.Media.Brush)FindResource("Warn");
                BtnOk.IsEnabled = false;
            }
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BtnOk.IsEnabled) OnOk(sender, e);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (Grid.SelectedItem is Row r && r.UnitCompatible)
            {
                SelectedCode = r.Code;
                DialogResult = true;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        private static string KindHe(string kind) => kind switch
        {
            "length" => "אורך",
            "area" => "שטח",
            "count" => "ספירה",
            "volume" => "נפח",
            _ => kind,
        };
    }
}
