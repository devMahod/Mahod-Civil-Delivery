using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Lets the engineer choose which layer of a picked CL drawing carries the section
    /// locations — from drawing evidence, not from a remembered name.
    ///
    /// The layer name is arbitrary and changes per project (6422 uses GFC111, the next
    /// project will use something else), so nothing here depends on it: the ranking is
    /// how many straight two-point lines the layer holds, how many of them actually
    /// cross a design alignment, and whether their length looks like a cross-section.
    /// The name is a weak hint and never decides.
    /// </summary>
    public partial class ClLayerPickerDialog : Window
    {
        public sealed class Row
        {
            /// <summary>The AutoCAD layer name exactly as it is in the drawing.</summary>
            public required string RawLayer { get; init; }

            /// <summary>Same name, wrapped for display inside an RTL panel.</summary>
            public required string Layer { get; init; }
            public required string ScoreDisplay { get; init; }
            public required string Evidence { get; init; }
        }

        private readonly ObservableCollection<Row> _rows = new();

        /// <summary>The layer the engineer approved, once the dialog returns true.</summary>
        public string? SelectedLayer { get; private set; }

        public ClLayerPickerDialog(
            string clPath, int entities, int layersScanned, int alignmentsProbed,
            IEnumerable<ClLayerCandidate> candidates)
        {
            InitializeComponent();
            UiGuard.Attach(this, "בחירת שכבת קווי חתך");

            FileTitle.Text = Bidi.Ltr(Path.GetFileName(clPath));
            ScanSummary.Text =
                $"נסרקו {entities:N0} עצמים ב{Layers(layersScanned)}, ונבדקו מול {alignmentsProbed:N0} תוואים בשרטוט הפתוח.";

            foreach (var c in candidates)
                _rows.Add(ToRow(c));

            Candidates.ItemsSource = _rows;
            if (_rows.Count > 0) Candidates.SelectedIndex = 0;
            BtnOk.IsEnabled = _rows.Count > 0;
        }

        /// <summary>Evidence in the engineer's language — the numbers, not the score.</summary>
        private static Row ToRow(ClLayerCandidate c)
        {
            var parts = new List<string> { $"{c.TwoPointCount:N0} קווים ישרים" };

            if (c.CrossingCount > 0)
            {
                // CrossingCount is the number of CROSSINGS, not of lines: one section line
                // that crosses three axes counts three times. Saying "63 cross an alignment"
                // next to "27 straight lines" reads as more lines than the layer holds.
                var withWhat = c.AlignmentsCrossed.Count == 1
                    ? $"עם התוואי {Bidi.Ltr(c.AlignmentsCrossed[0])}"
                    : $"עם {c.AlignmentsCrossed.Count} תוואים";
                parts.Add($"{c.CrossingCount:N0} חיתוכים {withWhat}");
            }
            else
            {
                parts.Add("ללא חיתוך עם אף תוואי");
            }

            if (c.MedianLength > 0) parts.Add($"אורך חציוני {c.MedianLength:N1} מ'");
            if (c.InXref) parts.Add("מגיע דרך XREF");
            if (c.SampleLabels.Count > 0)
                parts.Add("תוויות לדוגמה: " + Bidi.Ltr(string.Join(", ", c.SampleLabels.Take(3))));

            return new Row
            {
                RawLayer = c.Layer,
                Layer = Bidi.Ltr(c.Layer),
                ScoreDisplay = $"התאמה {c.EvidenceScore}%",
                Evidence = string.Join(" · ", parts),
            };
        }

        private static string Layers(int n) => n == 1 ? "שכבה אחת" : $"-{n:N0} שכבות";

        private void OnOk(object sender, RoutedEventArgs e) => Approve();

        private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Approve();

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Approve()
        {
            if (Candidates.SelectedItem is not Row row) return;
            // The row displays the layer wrapped in LRM marks so it reads correctly in an
            // RTL panel; the profile must receive the raw AutoCAD layer name.
            SelectedLayer = row.RawLayer;
            DialogResult = true;
        }
    }
}
