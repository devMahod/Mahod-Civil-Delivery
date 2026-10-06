using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Asks which drawing's corridor measurement prices chapters 51.01–51.04 when the profile has measurements of more
    /// than one drawing (review 01/10: a later measurement of an experiment copy silently replaced the estimate's source).
    /// Nothing is preselected — "the latest" is exactly the choice that must not be made for the engineer.
    /// </summary>
    public partial class CorridorSourcePickerDialog : Window
    {
        public sealed class Row
        {
            /// <summary>The drawing path exactly as the corridor runs recorded it.</summary>
            public required string RawPath { get; init; }

            /// <summary>File name and folder, wrapped for display inside an RTL panel.</summary>
            public required string Name { get; init; }
            public required string Folder { get; init; }

            /// <summary>The whole path (tooltip): two copies with the same name and folder start still differ here.</summary>
            public required string FullPath { get; init; }
            public required string Evidence { get; init; }

            /// <summary>What UI Automation (screen readers, UI tests) reads for the row: the file name, not the type name.</summary>
            public override string ToString() => Path.GetFileName(RawPath);
        }

        private readonly ObservableCollection<Row> _rows = new();

        /// <summary>The drawing the engineer chose, once the dialog returns true.</summary>
        public string? SelectedDrawingPath { get; private set; }

        public CorridorSourcePickerDialog(IReadOnlyList<BoqRulesSourceResolver.CorridorSourceOption> options)
        {
            InitializeComponent();
            UiGuard.Attach(this, "בחירת מדידת קורידורים");

            Headline.Text = $"בפרויקט יש מדידות קורידורים מ-{options.Count} שרטוטים";
            foreach (var option in options)
            {
                _rows.Add(new Row
                {
                    RawPath = option.DrawingPath,
                    Name = Bidi.Ltr(Path.GetFileName(option.DrawingPath)),
                    Folder = Bidi.Ltr(Path.GetDirectoryName(option.DrawingPath) ?? ""),
                    FullPath = Bidi.Ltr(option.DrawingPath),
                    Evidence = $"מדידה אחרונה: {option.LatestCompletedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm} · " +
                               (option.RunCount == 1 ? "מדידה אחת" : $"{option.RunCount:N0} מדידות"),
                });
            }
            Candidates.ItemsSource = _rows;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
            BtnOk.IsEnabled = Candidates.SelectedItem is Row;

        private void OnOk(object sender, RoutedEventArgs e) => Approve();

        private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Approve();

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Approve()
        {
            if (Candidates.SelectedItem is not Row row) return;
            SelectedDrawingPath = row.RawPath;
            DialogResult = true;
        }
    }
}
