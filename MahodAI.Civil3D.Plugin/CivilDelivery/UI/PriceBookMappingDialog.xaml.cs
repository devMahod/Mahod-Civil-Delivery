using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// "טען מחירון" before anything is registered (b15): shows what the reader understood (sheet, header row, columns,
    /// coverage, problems and rejected rows) next to a preview of the sheet, and lets the engineer choose another sheet,
    /// header row or columns. The automatic reading is approved as is; a different choice is approved only after
    /// "בדוק בחירה" ran the reader with exactly that mapping (Codex 15:55: the choice exists even when the automatic
    /// reading is usable, since a usable first sheet can still be the wrong one).
    /// </summary>
    public partial class PriceBookMappingDialog : Window
    {
        private const string RowColumn = "שורה";
        private const int PreviewRows = 120;
        private const int PreviewColumns = 30;

        private readonly string _path;
        private readonly PriceBookXlsxLoader.Inspection _automatic;
        private readonly byte[]? _bytes;
        private List<List<MiniXlsx.CellText>> _sheetRows = new();
        private PriceBookXlsxLoader.Inspection? _shown;
        private PriceBookXlsxLoader.ColumnMapping? _shownMapping;
        private bool _loading;

        /// <summary>The reading the engineer approved (always usable).</summary>
        public PriceBookXlsxLoader.Inspection? ApprovedInspection { get; private set; }

        /// <summary>The explicit choice, or null when the automatic reading was approved unchanged.</summary>
        public PriceBookXlsxLoader.ColumnMapping? ApprovedMapping { get; private set; }

        public PriceBookMappingDialog(string path, PriceBookXlsxLoader.Inspection automatic)
        {
            InitializeComponent();
            UiGuard.Attach(this, "טעינת מחירון");
            _path = path;
            _automatic = automatic;
            Headline.Text = "מחירון: " + Bidi.Ltr(Path.GetFileName(path));

            _loading = true;
            try
            {
                // One snapshot for the preview, bound to the bytes the automatic reading hashed.
                var bytes = File.ReadAllBytes(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), automatic.FileHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("הקובץ השתנה מאז שנקרא. יש לסגור ולבחור שוב \"טען מחירון\".");
                _bytes = bytes;
                var sheets = automatic.AvailableSheets.Count > 0 ? automatic.AvailableSheets : MiniXlsx.ReadSheetNames(bytes);
                SheetBox.ItemsSource = sheets;
                SheetBox.SelectedItem = automatic.SheetName ?? sheets.FirstOrDefault();
                LoadSheet();
                HeaderRowBox.Text = automatic.HeaderRow > 0 ? automatic.HeaderRow.ToString(CultureInfo.InvariantCulture) : "";
                RefreshColumnOptions(automatic.CodeColumn, automatic.DescriptionColumn, automatic.UnitColumn, automatic.PriceColumn);
            }
            catch (Exception ex)
            {
                Disable(ex.Message);
                return;
            }
            finally { _loading = false; }
            Show(automatic, null);
        }

        private string? Selected(ComboBox box) =>
            box.SelectedItem is PriceBookMappingSelection.ColumnOption { Column.Length: > 0 } option ? option.Column : null;

        private void LoadSheet()
        {
            _sheetRows = SheetBox.SelectedItem is string sheet && _bytes != null
                ? MiniXlsx.ReadSheet(_bytes, sheet)
                : new List<List<MiniXlsx.CellText>>();
            var shown = Numbered().Take(PreviewRows).ToList();
            var last = shown.SelectMany(row => row.Cells)
                .Select(cell => PriceBookMappingSelection.ColumnIndex(cell.Column))
                .DefaultIfEmpty(0).Max();
            var table = new DataTable();
            table.Columns.Add(RowColumn, typeof(int));
            var letters = Enumerable.Range(1, Math.Min(last, PreviewColumns)).Select(Letter).ToList();
            foreach (var letter in letters) table.Columns.Add(letter, typeof(string));
            foreach (var (number, cells) in shown)
            {
                var values = new object[letters.Count + 1];
                values[0] = number;
                for (var i = 0; i < letters.Count; i++)
                    values[i + 1] = cells.FirstOrDefault(cell => cell.Column == letters[i]).Text ?? "";
                table.Rows.Add(values);
            }
            Preview.ItemsSource = table.DefaultView;
        }

        /// <summary>Sheet rows with their spreadsheet row number, numbered the way the reader numbers them.</summary>
        private IEnumerable<(int Number, List<MiniXlsx.CellText> Cells)> Numbered()
        {
            var number = 0;
            foreach (var row in _sheetRows)
            {
                number = row.Count > 0 && row[0].Row > 0 ? row[0].Row : number + 1;
                yield return (number, row);
            }
        }

        private static string Letter(int index)
        {
            var letters = "";
            for (; index > 0; index = (index - 1) / 26) letters = (char)('A' + (index - 1) % 26) + letters;
            return letters;
        }

        private void RefreshColumnOptions(string? code, string? description, string? unit, string? price)
        {
            var headerCells = int.TryParse(HeaderRowBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var headerRow)
                ? Numbered().Where(row => row.Number == headerRow).Select(row => row.Cells).FirstOrDefault()
                : null;
            var options = PriceBookMappingSelection.HeaderOptions(headerCells ?? Enumerable.Empty<MiniXlsx.CellText>());
            var wasLoading = _loading;
            _loading = true;
            try
            {
                Fill(CodeBox, options, code);
                Fill(UnitBox, options, unit);
                Fill(PriceBox, options, price);
                var withNone = new List<PriceBookMappingSelection.ColumnOption> { PriceBookMappingSelection.NoDescription };
                withNone.AddRange(options);
                Fill(DescriptionBox, withNone, description ?? "");
            }
            finally { _loading = wasLoading; }
        }

        private static void Fill(ComboBox box, IReadOnlyList<PriceBookMappingSelection.ColumnOption> options, string? column)
        {
            box.ItemsSource = options;
            box.SelectedItem = column == null ? null : options.FirstOrDefault(option => option.Column == column);
        }

        private void Show(PriceBookXlsxLoader.Inspection inspection, PriceBookXlsxLoader.ColumnMapping? mapping)
        {
            _shown = inspection;
            _shownMapping = mapping;
            SummaryBox.Text = PriceBookMappingSelection.Summary(inspection);
            BtnOk.IsEnabled = inspection.IsUsable;
            StatusLine.Text = inspection.IsUsable
                ? ""
                : "הקריאה הזו לא מתאימה לרישום. בחר גיליון, שורת כותרת ועמודות, ולחץ \"בדוק בחירה\".";
        }

        private void MarkStale()
        {
            // A file that changed since it was read stays refused; no instruction that cannot be followed (PBM-5).
            if (_loading || _bytes == null) return;
            _shown = null;
            _shownMapping = null;
            BtnOk.IsEnabled = false;
            StatusLine.Text = "הבחירה השתנתה. לחץ \"בדוק בחירה\" כדי לראות מה ייקרא איתה.";
        }

        private void Disable(string message)
        {
            SummaryBox.Text = message;
            StatusLine.Text = message;
            BtnOk.IsEnabled = false;
            BtnCheck.IsEnabled = false;
        }

        private void OnSheetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            try { LoadSheet(); }
            catch (Exception ex) { Disable("לא ניתן לקרוא את הגיליון: " + ex.Message); return; }
            BtnCheck.IsEnabled = _bytes != null;
            HeaderRowBox.Text = "";
            RefreshColumnOptions(null, null, null, null);
            MarkStale();
        }

        private void OnHeaderRowChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            RefreshColumnOptions(Selected(CodeBox), Selected(DescriptionBox), Selected(UnitBox), Selected(PriceBox));
            MarkStale();
        }

        private void OnColumnChanged(object sender, SelectionChangedEventArgs e) => MarkStale();

        private void OnPreviewRowSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Preview.SelectedItem is not DataRowView row) return;
            HeaderRowBox.Text = Convert.ToString(row[RowColumn], CultureInfo.InvariantCulture) ?? "";
        }

        private void OnCheck(object sender, RoutedEventArgs e)
        {
            // Nothing shown before this check may be approved after it, whatever happens below (Codex 16:25: an IO
            // failure used to leave "אשר" enabled on the previous preview).
            _shown = null;
            _shownMapping = null;
            BtnOk.IsEnabled = false;
            var (mapping, error) = PriceBookMappingSelection.Build(
                _automatic.FileHash, SheetBox.SelectedItem as string, HeaderRowBox.Text,
                Selected(CodeBox), Selected(DescriptionBox), Selected(UnitBox), Selected(PriceBox));
            if (mapping == null)
            {
                StatusLine.Text = error;
                return;
            }
            // Choosing exactly the automatic reading registers the automatic reading, so that is what is shown (PBM-3).
            if (PriceBookMappingSelection.SameAsAutomatic(_automatic, mapping))
            {
                Show(_automatic, null);
                return;
            }
            PriceBookXlsxLoader.Inspection inspection;
            try { inspection = PriceBookXlsxLoader.Inspect(_path, mapping); }
            catch (Exception ex)
            {
                StatusLine.Text = "לא ניתן לקרוא את הקובץ: " + ex.Message;
                return;
            }
            Show(inspection, mapping);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (_shown is not { IsUsable: true } shown) return;
            ApprovedInspection = shown;
            ApprovedMapping = _shownMapping;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
