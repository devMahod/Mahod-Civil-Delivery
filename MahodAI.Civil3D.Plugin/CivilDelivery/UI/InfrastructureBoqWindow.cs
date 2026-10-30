using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DataGrid = System.Windows.Controls.DataGrid;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

internal sealed class InfrastructureBoqWindow : Window
{
    private readonly InfrastructureDrawingService _scan;
    private CatalogSnapshot _catalog;
    private readonly TextBlock _catalogLabel = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private readonly List<InfrastructureGroup> _groups;
    private readonly HashSet<InfrastructureGroup> _excluded = new();
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly ComboBox _filter = new() { MinWidth = 130 };
    private readonly ComboBox _topic = new() { ItemsSource = InfrastructureBoq.Topics, MinWidth = 120 };
    private readonly ComboBox _status = new() { ItemsSource = new[] { "טרם נבדק", "תכנון לביצוע", "מצב קיים", "קווי עזר" }, MinWidth = 135 };
    private readonly TextBox _spec = new() { MinWidth = 300 };
    private readonly TextBox _approver = new() { MinWidth = 110 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    internal InfrastructureBoqWindow(InfrastructureDrawingService scan, CatalogSnapshot catalog)
    {
        _scan = scan; _catalog = catalog; _groups = InfrastructureBoq.Group(scan.Measurements.Select(m => m.Line)).ToList();
        Title = "Mahod Delivery — כתב כמויות תשתיות"; Width = 1250; Height = 760;
        FlowDirection = System.Windows.FlowDirection.RightToLeft; FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "תשתיות | מדידה ← בדיקת מפרט ← חיפוש סעיף ← Excel ושרטוט", FontSize = 21, Margin = new Thickness(4) });
        top.Children.Add(new TextBlock { Text = "הסריקה כוללת את כל Model Space ומופעי המקורות הטעונים, ללא חיתוך לפוליגון. שמות השכבות מציעים נושא בלבד. קווים סגורים, כפולים, מצב קיים וקווי עזר מחייבים בדיקה.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        top.Children.Add(_catalogLabel); UpdateCatalogLabel();
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) }; top.Children.Add(bar);
        _filter.ItemsSource = new[] { "כל התשתיות" }.Concat(InfrastructureBoq.Topics); _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Refresh(); bar.Children.Add(_filter);
        Button(bar, "בחר מחירון Excel…", ChooseCatalog);
        Button(bar, "בדיקת כלי איגור", AuditIgor);
        Button(bar, "ממצאי הסריקה", () => MessageBox.Show(_scan.Findings.Count == 0 ? "לא נרשמו ממצאים" : string.Join("\n", _scan.Findings), "ממצאי מדידה"));
        Button(bar, "הסר מהרשימה", () => { if (_grid.SelectedItem is InfrastructureGroup g) { _excluded.Add(g); Refresh(); } });
        Button(bar, "החזר מוסרות", () => { _excluded.Clear(); Refresh(); });
        Button(bar, "ייצא Excel ו־DWG…", Export);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "בחר שורה, בדוק נושא ומצב, וכתוב מפרט (קוטר / חומר / עומק לפי הצורך). לאחר מכן חפש סעיף בעברית.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        var edit = new WrapPanel(); bottom.Children.Add(edit);
        Label(edit, "נושא", _topic); Label(edit, "מצב", _status); Label(edit, "מפרט", _spec); Label(edit, "מאשר", _approver);
        Button(edit, "שמור פרטי שורה", SaveDetails);
        Button(edit, "חפש סעיף במחירון…", Assign);
        bottom.Children.Add(_message);
        foreach (var col in new[] { ("נושא","Topic",90d), ("שכבה","Layer",240d), ("מקור","Source",200d),
            ("קווים","Count",65d), ("אורך במטרים","LengthMetres",110d), ("סגורים","ClosedCount",65d),
            ("מצב עבודה","WorkStatus",110d), ("מפרט","Specification",180d), ("סעיף","Code",110d),
            ("תיאור","Description",260d), ("מחיר ₪","Price",100d), ("סכום ₪","Total",120d), ("בדיקה","Status",180d) })
            _grid.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2) {
                StringFormat = col.Item2 is "LengthMetres" or "Price" or "Total" ? "N2" : null }, Width = col.Item3 });
        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is InfrastructureGroup g) { _topic.SelectedItem = g.Topic; _status.SelectedItem = g.WorkStatus; _spec.Text = g.Specification; } };
        root.Children.Add(_grid); Refresh();
    }
    private void UpdateCatalogLabel() => _catalogLabel.Text = $"מחירון פעיל: {_catalog.SnapshotId} · {_catalog.PublicationNote} · {_catalog.Items.Count:N0} סעיפים";
    private void ChooseCatalog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Excel price book (*.xlsx;*.xls)|*.xlsx;*.xls", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var candidate = PriceBookXlsxLoader.Load(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
        if (candidate.Items.Count == 0) throw new InvalidOperationException("לא נמצאו סעיפים במחירון");
        if (MessageBox.Show($"{candidate.SnapshotId}\n{candidate.PublicationNote}\n{candidate.Items.Count:N0} סעיפים.\nודא מקור רשמי ומהדורה מתאימה לפרויקט. החלפה תבטל את כל אישורי התמחור הקיימים. להחליף?", "בחירת מחירון", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (!string.Equals(candidate.FileHash, _catalog.FileHash, StringComparison.OrdinalIgnoreCase))
            foreach (var group in _groups) group.Withdraw();
        _catalog = candidate; UpdateCatalogLabel(); _grid.Items.Refresh();
    }
    private void AuditIgor()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || doc.Name != _scan.Drawing) throw new InvalidOperationException("שרטוט המקור אינו פעיל");
        var audit = IgorPolylineAudit.Capture(doc.Database);
        _message.Text = $"בדיקת איגור: {audit.Status} · {audit.Layers.Count:N0} שכבות · LWPOLYLINE במודל בלבד. {audit.Error}";
        _igorAudit = audit;
    }
    private IgorPolylineAudit.Result? _igorAudit;
    private static void Label(Panel panel, string label, Control input)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(input); }
    private static void Button(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(5) };
        button.Click += (_, _) => { try { action(); } catch (System.Exception ex) { MessageBox.Show(ex.Message, "כתב כמויות תשתיות"); } };
        panel.Children.Add(button);
    }
    private void Refresh()
    {
        var topic = _filter.SelectedItem as string;
        _grid.ItemsSource = _groups.Where(g => !_excluded.Contains(g) && (topic is null or "כל התשתיות" || g.Topic == topic)).ToArray();
        _message.Text = $"{_groups.Count - _excluded.Count:N0} קבוצות פעילות · {_excluded.Count:N0} הוסרו מהתוצר בלבד · {_scan.Findings.Count:N0} ממצאי מדידה · ניתן לייצא גם לפני שכל השורות שויכו";
    }
    private void SaveDetails()
    {
        if (_grid.SelectedItem is not InfrastructureGroup g) throw new InvalidOperationException("בחר שורה בטבלה");
        g.Topic = _topic.SelectedItem as string ?? InfrastructureBoq.Unknown;
        g.WorkStatus = _status.SelectedItem as string ?? "טרם נבדק"; g.Specification = _spec.Text.Trim();
        _grid.Items.Refresh();
    }
    private void Assign()
    {
        SaveDetails(); var g = (InfrastructureGroup)_grid.SelectedItem;
        if (g.Topic == InfrastructureBoq.Unknown || g.WorkStatus != "תכנון לביצוע" || string.IsNullOrWhiteSpace(g.Specification) || string.IsNullOrWhiteSpace(_approver.Text))
            throw new InvalidOperationException("לפני שיוך: בחר סוג תשתית, מצב תכנון לביצוע, מפרט ושם מאשר");
        var picker = new CatalogPickerDialog("infrastructure", g.Layer, "length", "מטר", g.LengthMetres, g.Count, _catalog, Array.Empty<MappingProposal>());
        picker.Owner = this;
        if (picker.ShowDialog() == true && picker.SelectedCode is { } code) g.Approve(_catalog, code, _approver.Text);
        _grid.Items.Refresh();
    }
    private void Export()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את מסך התשתיות בשרטוט הנכון");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "כתב כמויות תשתיות", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var selected = _groups.Where(g => !_excluded.Contains(g)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("אין קבוצות פעילות לייצוא");
        var keys = selected.ToDictionary(g => (g.Source, g.Layer));
        var lines = _scan.Measurements.Select(m => m.Line).Where(l => keys.ContainsKey((l.Source, l.Layer)))
            .Select(l => InfrastructureBoq.ReviewedLine(l, keys[(l.Source, l.Layer)])).ToArray();
        _igorAudit = IgorPolylineAudit.Capture(doc.Database);
        if (_igorAudit.Status == "mismatch")
            throw new InvalidOperationException("מדידת איגור אינה תואמת למדידה העצמאית. יש לבדוק את השרטוט לפני ייצוא.");
        var engine = "AutoCAD Curve.GetDistanceAtParameter; quantities in metres. " +
            (_igorAudit.Status == "matched"
                ? "Original installed MAHOD3D CalcPolylines invoked at export and independently matched. "
                : "Original MAHOD3D comparison status: " + _igorAudit.Status + ". " + _igorAudit.Error + ". ") +
            "Igor scope: " + _igorAudit.Scope + "; Igor receipt lengths are in drawing units. See igor-audit.json.";
        // Each export gets a new directory: never overwrite an existing drawing or its trace.
        var directory = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var excel = Path.Combine(directory, Path.GetFileName(dialog.FileName));
        InfrastructureBoq.Export(excel, selected, lines, _catalog, _scan.Findings, _scan.Drawing, _scan.DrawingHash,
            engine);
        _scan.SaveDrawing(Path.Combine(directory, "Infrastructure-lines.dwg"), selected);
        File.WriteAllText(Path.Combine(directory, "review.json"), JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "igor-audit.json"), JsonSerializer.Serialize(_igorAudit ??
            new IgorPolylineAudit.Result("not-run", "", "Model Space LWPOLYLINE only", Array.Empty<IgorPolylineAudit.Comparison>(), "Run the Igor comparison before relying on it"), new JsonSerializerOptions { WriteIndented = true }));
        _message.Text = "נשמרו Excel, DWG ומדידות מקור בתיקייה: " + directory;
    }
    internal static void Run()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(doc.Name) || !File.Exists(doc.Name)) throw new InvalidOperationException("שמור את השרטוט לפני הסריקה");
            var raw = (int)doc.Database.Insunits;
            var factor = MahodAI.CivilDelivery.Shared.PhysicalDrawingUnitPolicy.StandardMetresPerUnit(raw);
            if (factor == null) throw new InvalidOperationException("יחידות השרטוט אינן מוגדרות. יש לבדוק ולהגדיר INSUNITS לפני מדידה במטרים");
            if (MessageBox.Show($"INSUNITS={doc.Database.Insunits}; מקדם למטרים {factor.Value:G}.\nהאם אלה יחידות הגאומטריה בפועל? הסריקה כוללת את כל המודל והמקורות הטעונים.", "בדיקת יחידות והיקף", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            using var scan = new InfrastructureDrawingService(doc.Database, doc.Name, factor.Value);
            var path = BundledPriceBooks.Offers(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodAI_Civil3D", "civil-delivery", "price-books"),
                new MahodAI.CivilDelivery.Shared.ProjectProfile()).Single().Entry.Path;
            var catalog = PriceBookXlsxLoader.Load(path, "נתיבי ישראל — אחוד יולי 2026");
            CivilModalHost.ShowFromPalette(new InfrastructureBoqWindow(scan, catalog));
        }
        catch (System.Exception ex) { doc.Editor.WriteMessage("\n" + ex.Message + "\n"); }
    }

    internal static void ExportAudit()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mahod Delivery", "Infrastructure", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        try
        {
            var factor = MahodAI.CivilDelivery.Shared.PhysicalDrawingUnitPolicy.StandardMetresPerUnit((int)doc.Database.Insunits)
                ?? throw new InvalidOperationException("Unknown physical drawing units");
            using var scan = new InfrastructureDrawingService(doc.Database, doc.Name, factor);
            var groups = InfrastructureBoq.Group(scan.Measurements.Select(m => m.Line));
            var pricePath = BundledPriceBooks.Offers(Path.Combine(directory, "price-book"), new MahodAI.CivilDelivery.Shared.ProjectProfile()).Single().Entry.Path;
            var catalog = PriceBookXlsxLoader.Load(pricePath, "נתיבי ישראל אחוד יולי 2026 — מחירון כלול בתוסף");
            InfrastructureBoq.Export(Path.Combine(directory, "Infrastructure-review.xlsx"), groups, scan.Measurements.Select(m => m.Line).ToArray(), catalog,
                scan.Findings, doc.Name, scan.DrawingHash, "AutoCAD native curve distance; original Igor audit is separate and limited to host LWPOLYLINE");
            scan.SaveDrawing(Path.Combine(directory, "Infrastructure-lines.dwg"), groups);
            File.WriteAllText(Path.Combine(directory, "igor-audit.json"), JsonSerializer.Serialize(IgorPolylineAudit.Capture(doc.Database), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { scan.Drawing, scan.DrawingHash, Insunits = doc.Database.Insunits.ToString(), MetresPerUnit = factor,
                LineCount = scan.Measurements.Count, GroupCount = groups.Count, Groups = groups, Findings = scan.Findings }, new JsonSerializerOptions { WriteIndented = true }));
            doc.Editor.WriteMessage("\nInfrastructure draft (UNAPPROVED): " + directory + "\n");
        }
        catch (System.Exception ex) { File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); doc.Editor.WriteMessage("\n" + ex + "\n"); }
    }
}
