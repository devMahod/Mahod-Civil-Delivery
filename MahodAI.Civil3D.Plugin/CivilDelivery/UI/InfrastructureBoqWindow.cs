using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using Cursors = System.Windows.Input.Cursors;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

internal sealed class InfrastructureBoqWindow : Window
{
    private readonly InfrastructureDrawingService _scan;
    private CatalogSnapshot _catalog;
    private readonly IReadOnlyList<InfrastructurePriceBook> _books;
    private readonly InfrastructureCatalogUsage _usage;
    private IgorPolylineAudit.Result? _igorAudit;
    private readonly List<InfrastructureGroup> _groups;
    private readonly HashSet<InfrastructureGroup> _excluded = new();
    private InfrastructureCatalogSearch _search;
    private readonly string _reviewPath;
    private readonly DataGrid _grid = Table(), _items = Table();
    private readonly ComboBox _filter = new() { MinWidth = 130, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6) };
    private readonly ComboBox _topic = new() { ItemsSource = InfrastructureBoq.Topics, MinWidth = 110 };
    private readonly ComboBox _bookPicker = new() { Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 6), DisplayMemberPath = "Label" };
    private readonly TextBox _layerSearch = Input(), _catalogSearch = Input();
    private readonly ComboBox _measurement = new() { ItemsSource = new[] { "מטר", "מ״ר", "מ״ק", "יח׳", "דונם", "טון", "ק״ג", "קומפ׳" }, Padding = new Thickness(5), MinWidth = 90 };
    private readonly TextBox _manualQuantity = Input();
    private readonly StackPanel _manualPanel = new();
    private readonly TextBlock _message = Text(""), _subject = Text("בחר שכבה"), _note = Text(""), _results = Text(""), _detail = Text("");
    private readonly TextBlock _source = Text("");
    private readonly Button _assign = ActionButton("שייך לשכבה", true), _undo = ActionButton("החזר שורות שהוסרו");
    private readonly Button _autoAssign = ActionButton("שייך אוטומטית", true), _undoAuto = ActionButton("בטל שיוך אוטומטי אחרון");
    private readonly DockPanel _layoutRoot = new() { Margin = new Thickness(18) };
    private InfrastructureAutoAssignmentBatch? _lastAutomaticBatch;
    private readonly System.Windows.Threading.DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _loading, _automaticBusy, _closed;
    internal InfrastructureBoqWindow(InfrastructureDrawingService scan, CatalogSnapshot catalog)
    {
        _scan = scan; _catalog = catalog; _groups = InfrastructureBoq.Group(scan.Measurements.Select(m => m.Line)).ToList();
        _books = new[] { new InfrastructurePriceBook("נתיבי ישראל", "אחוד יולי 2026 · מקור רשמי", catalog, new Dictionary<string,string>(), true) }
            .Concat(InfrastructurePriceBooks.Load(InfrastructurePriceBooks.LocalDirectory, scan.Findings)).ToArray();
        try { _usage = InfrastructureCatalogUsage.Load(Path.Combine(InfrastructurePriceBooks.LocalDirectory, "project-usage.json")); }
        catch (System.Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { _usage = new InfrastructureCatalogUsage(Array.Empty<InfrastructureCatalogUsage.Example>()); scan.Findings.Add("לא נטען ניסיון הפרויקטים: " + ex.Message); }
        _search = new InfrastructureCatalogSearch(_books[0], _usage);
        _reviewPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodCivilDelivery", "infrastructure-reviews", InfrastructureReviewStore.FileName(scan.Drawing));
        string? restoreError = null;
        try { InfrastructureReviewStore.Restore(_reviewPath, scan.DrawingHash, _books.Select(b => b.Catalog).ToArray(), _groups, _excluded); }
        catch (System.Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { restoreError = "לא ניתן לטעון שיוכים קודמים: " + ex.Message; }
        Title = "Mahod Delivery — כתב כמויות"; Width = 1240; Height = 800; MinWidth = 920; MinHeight = 650;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        FlowDirection = System.Windows.FlowDirection.RightToLeft; FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13; Background = Brushes.White; Foreground = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowState = WindowState.Maximized;
        UiGuard.Attach(this, "כתב כמויות");
        var root = _layoutRoot; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var header = new DockPanel(); top.Children.Add(header);
        var export = ActionButton("ייצא Excel ושרטוט…", true); export.Click += (_, _) => UiGuard.Run("ייצוא תשתיות", Export);
        DockPanel.SetDock(export, Dock.Left); header.Children.Add(export);
        _autoAssign.Margin = new Thickness(8, 3, 0, 3);
        _autoAssign.ToolTip = "שייך את הסעיף המומלץ לכל שכבה שמוצגת וטרם שויכה, במחירון ובתחום הנבחרים. שיוכים קיימים נשמרים. חיפוש הסעיפים הידני אינו מוחל על שכבות אחרות.";
        _autoAssign.Click += async (_, _) => await AssignAutomatically();
        DockPanel.SetDock(_autoAssign, Dock.Left); header.Children.Add(_autoAssign);
        header.Children.Add(new TextBlock { Text = "כתב כמויות", FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(Text("שייך אוטומטית לפי המלצות המערכת, או בחר שכבה וסעיף. אפשר לייצא גם כתב כמויות חלקי."));
        var scope = Text($"{_books.Count} מהדורות מחירונים לפי גופים · ניסיון מ־{_usage.ProjectCount} פרויקטים · {scan.MetresPerHostUnit:G} מטר ליחידת שרטוט · כל המודל והמקורות");
        top.Children.Add(scope);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        _undo.Click += (_, _) => UiGuard.Run("החזרת שורות", () => { _excluded.Clear(); Persist(); Refresh(); });
        DockPanel.SetDock(_undo, Dock.Left); bottom.Children.Add(_undo); bottom.Children.Add(_message);
        _undoAuto.Visibility = System.Windows.Visibility.Collapsed;
        _undoAuto.Margin = new Thickness(8, 3, 0, 3);
        _undoAuto.Click += (_, _) => UiGuard.Run("ביטול שיוך אוטומטי", UndoAutomaticAssignment);
        DockPanel.SetDock(_undoAuto, Dock.Left); bottom.Children.Insert(1, _undoAuto);
        var body = new System.Windows.Controls.Grid { Margin = new Thickness(0, 14, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.45, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star) }); root.Children.Add(body);
        var layers = new DockPanel { Margin = new Thickness(0, 0, 14, 0) }; body.Children.Add(layers);
        var layerHead = new StackPanel(); DockPanel.SetDock(layerHead, Dock.Top); layers.Children.Add(layerHead);
        layerHead.Children.Add(Heading("1  שכבות בשרטוט"));
        var filters = new DockPanel { Margin = new Thickness(0, 5, 0, 8) }; layerHead.Children.Add(filters);
        _filter.DisplayMemberPath = "Label"; _filter.MaxDropDownHeight = 440; _filter.MaxWidth = 270;
        _filter.ToolTip = "בחירת תחום מתוך הפרקים ותתי הפרקים במחירון. הסינון חל על הסעיפים בלבד.";
        _filter.ItemsSource = BoqCatalogCategories.For(_books[0]); _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => { if (!_loading) Search(); }; DockPanel.SetDock(_filter, Dock.Right); filters.Children.Add(_filter);
        _layerSearch.ToolTip = "סינון לפי שם שכבה"; _layerSearch.TextChanged += (_, _) => Refresh(); filters.Children.Add(_layerSearch);
        Column(_grid, "שכבה", "ShortLayer", new DataGridLength(1, DataGridLengthUnitType.Star));
        Column(_grid, "כמות", "Quantity", 85, "N2"); Column(_grid, "יחידה", "MeasurementUnit", 55); Column(_grid, "סעיף", "MappingDisplay", 105);
        _grid.SelectionChanged += (_, _) => SelectLayer();
        _grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && _grid.SelectedItem is InfrastructureGroup g) { Remove(g); e.Handled = true; } };
        var menu = new ContextMenu();
        AddMenu(menu, "הסר מהתוצר (אפשר להחזיר)", () => { if (_grid.SelectedItem is InfrastructureGroup g) Remove(g); });
        AddMenu(menu, "בטל שיוך", () => { if (_grid.SelectedItem is InfrastructureGroup g) { g.Withdraw(); Persist(); _grid.Items.Refresh(); SelectLayer(); } });
        _grid.ContextMenu = menu;
        var layerFooter = Text("להסרה: קליק ימני על השורה או Delete. הסינון משנה תצוגה בלבד.");
        DockPanel.SetDock(layerFooter, Dock.Bottom); layers.Children.Add(layerFooter); layers.Children.Add(_grid);

        var editor = new DockPanel(); System.Windows.Controls.Grid.SetColumn(editor, 1); body.Children.Add(editor);
        var editHead = new StackPanel(); DockPanel.SetDock(editHead, Dock.Top); editor.Children.Add(editHead);
        editHead.Children.Add(Heading("2  סעיף מהמחירון"));
        var booksView = new ListCollectionView(_books.ToList()); booksView.GroupDescriptions.Add(new PropertyGroupDescription("Publisher"));
        _bookPicker.ItemsSource = booksView;
        var groupHeader = new FrameworkElementFactory(typeof(TextBlock)); groupHeader.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        groupHeader.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); groupHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(5));
        _bookPicker.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = groupHeader } });
        _bookPicker.SelectedItem = _books[0];
        _bookPicker.SelectionChanged += (_, _) => { if (!_loading && _bookPicker.SelectedItem is InfrastructurePriceBook book) { UseBook(book); Search(); } };
        editHead.Children.Add(_bookPicker);
        var measurementRow = new DockPanel { Margin = new Thickness(0, 3, 0, 4) };
        measurementRow.Children.Add(Text("יחידת מדידה:")); measurementRow.Children.Add(_measurement); editHead.Children.Add(measurementRow);
        _measurement.SelectionChanged += (_, _) => { if (!_loading && _grid.SelectedItem is InfrastructureGroup g && _measurement.SelectedItem is string unit) { g.MeasurementUnit = unit; _loading = true; _manualQuantity.Clear(); _loading = false; UpdateMeasurementInput(g); Persist(); _grid.Items.Refresh(); Search(); UpdateSummary(); } };
        _manualPanel.Children.Add(Text("אין מדידה מהשרטוט ביחידה זו. כמות להזנת המהנדס:")); _manualPanel.Children.Add(_manualQuantity); editHead.Children.Add(_manualPanel);
        _manualQuantity.TextChanged += (_, _) => { if (_loading || _grid.SelectedItem is not InfrastructureGroup g) return;
            g.ManualQuantity = double.TryParse(_manualQuantity.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var n) && double.IsFinite(n) && n > 0 ? n : null;
            UpdateMeasurementInput(g); Persist(); _grid.Items.Refresh(); SelectItem(); UpdateSummary(); };
        _subject.FontWeight = FontWeights.SemiBold; _subject.TextTrimming = TextTrimming.CharacterEllipsis; _subject.TextWrapping = TextWrapping.NoWrap; editHead.Children.Add(_subject);
        _note.Foreground = new SolidColorBrush(Color.FromRgb(133, 84, 15)); editHead.Children.Add(_note);
        editHead.Children.Add(Text("חיפוש בעברית או מספר סעיף, לדוגמה: צינור ביוב 315"));
        _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); UiGuard.Run("חיפוש סעיפים", Search); };
        Closed += (_, _) => { _closed = true; _queryTimer.Stop(); };
        _catalogSearch.TextChanged += (_, _) => { _queryTimer.Stop(); if (!_loading) _queryTimer.Start(); }; editHead.Children.Add(_catalogSearch);
        _results.FontSize = 11; editHead.Children.Add(_results);
        var choice = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(choice, Dock.Bottom); editor.Children.Add(choice);
        _detail.MinHeight = 55; _detail.MaxHeight = 100;
        choice.Children.Add(new ScrollViewer { Content = _detail, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _assign.IsEnabled = false; _assign.Click += (_, _) => UiGuard.Run("שיוך שכבה", Assign); choice.Children.Add(_assign);
        var sourceBody = new StackPanel(); sourceBody.Children.Add(_source); sourceBody.Children.Add(_topic);
        _topic.SelectionChanged += (_, _) => { if (!_loading && _grid.SelectedItem is InfrastructureGroup g) { g.Topic = _topic.SelectedItem as string ?? InfrastructureBoq.Unknown; Persist(); _grid.Items.Refresh(); Search(); } };
        choice.Children.Add(new Expander { Header = "פרטי המקור ושינוי נושא", Content = new ScrollViewer { Content = sourceBody, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 8, 0, 0) });
        RecommendedCodeColumn(_items); Column(_items, "תיאור", "Description", new DataGridLength(1, DataGridLengthUnitType.Star)); Column(_items, "₪ ליחידה", "PriceDisplay", 80);
        _items.SelectionChanged += (_, _) => SelectItem();
        _items.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && FindRow(d) != null && _assign.IsEnabled) UiGuard.Run("שיוך שכבה", Assign); };
        _items.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && _assign.IsEnabled) { UiGuard.Run("שיוך שכבה", Assign); e.Handled = true; } };
        editor.Children.Add(_items); Refresh();
        if (restoreError != null) _message.Text = restoreError;
    }
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(76, 86, 104)), Margin = new Thickness(0, 4, 0, 4) };
    private static TextBlock Heading(string value) => new() { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private static TextBox Input() => new() { Padding = new Thickness(7), VerticalContentAlignment = VerticalAlignment.Center };
    private static Button ActionButton(string text, bool primary = false) => new() { Content = text, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 3, 0, 3),
        Background = primary ? new SolidColorBrush(Color.FromRgb(35, 104, 169)) : new SolidColorBrush(Color.FromRgb(236, 240, 245)), Foreground = primary ? Brushes.White : Brushes.Black, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
    internal static DataGrid Table()
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(231, 236, 242)), BorderBrush = new SolidColorBrush(Color.FromRgb(218, 225, 234)),
            Background = Brushes.White, RowBackground = Brushes.White, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(247, 249, 252)), RowHeight = 49, FontSize = 12 };
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        return table;
    }
    private static void Column(DataGrid grid, string title, string path, DataGridLength width, string? format = null)
    {
        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5)));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path) { StringFormat = format }, Width = width, ElementStyle = style });
    }
    internal static void RecommendedCodeColumn(DataGrid grid)
    {
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Reason")));
        var recommended = new DataTrigger { Binding = new Binding("IsRecommended"), Value = true };
        recommended.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(230, 246, 237))));
        recommended.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        recommended.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        recommended.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(32, 133, 83))));
        recommended.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 1, 0, 1)));
        rowStyle.Triggers.Add(recommended);
        var selected = new MultiDataTrigger();
        selected.Conditions.Add(new Condition(new Binding("IsRecommended"), true));
        selected.Conditions.Add(new Condition(new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }, true));
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(202, 233, 217))));
        rowStyle.Triggers.Add(selected); grid.RowStyle = rowStyle;
        // The theme paints selected cells over the row; keep the recommendation
        // visible both before and after selection, with readable dark text.
        var cellStyle = new Style(typeof(DataGridCell));
        var recommendedCell = new DataTrigger { Binding = new Binding("IsRecommended"), Value = true };
        recommendedCell.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(230, 246, 237))));
        recommendedCell.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black)); cellStyle.Triggers.Add(recommendedCell);
        var selectedCell = new MultiDataTrigger();
        selectedCell.Conditions.Add(new Condition(new Binding("IsRecommended"), true));
        selectedCell.Conditions.Add(new Condition(new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }, true));
        selectedCell.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(202, 233, 217))));
        cellStyle.Triggers.Add(selectedCell); grid.CellStyle = cellStyle;
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 2, 5, 2));
        stack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var code = new FrameworkElementFactory(typeof(TextBlock)); code.SetBinding(TextBlock.TextProperty, new Binding("Code")); stack.AppendChild(code);
        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(32, 133, 83)));
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); badge.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1));
        badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Right);
        var badgeStyle = new Style(typeof(Border)); badgeStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, System.Windows.Visibility.Collapsed));
        var showBadge = new DataTrigger { Binding = new Binding("IsRecommended"), Value = true };
        showBadge.Setters.Add(new Setter(UIElement.VisibilityProperty, System.Windows.Visibility.Visible)); badgeStyle.Triggers.Add(showBadge);
        badge.SetValue(FrameworkElement.StyleProperty, badgeStyle);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding("RecommendationLabel"));
        label.SetValue(TextBlock.ForegroundProperty, Brushes.White); label.SetValue(TextBlock.FontSizeProperty, 10d);
        badge.AppendChild(label); stack.AppendChild(badge);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "סעיף", Width = 95, CellTemplate = new DataTemplate { VisualTree = stack } });
    }
    private static DataGridRow? FindRow(DependencyObject? child)
    { while (child != null && child is not DataGridRow) child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); return child as DataGridRow; }
    private static void AddMenu(ContextMenu menu, string text, Action action)
    { var item = new MenuItem { Header = text }; item.Click += (_, _) => UiGuard.Run(text, action); menu.Items.Add(item); }
    private void Remove(InfrastructureGroup group) { _excluded.Add(group); Persist(); Refresh(); }
    private void Persist() => InfrastructureReviewStore.Save(_reviewPath, _scan.DrawingHash, _books.Select(b => b.Catalog).ToArray(), _groups, _excluded);
    private void UseBook(InfrastructurePriceBook book)
    {
        _catalog = book.Catalog; _search = new InfrastructureCatalogSearch(book, _usage);
        _bookPicker.ToolTip = _catalog.PublicationNote;
        if (!Equals(_bookPicker.SelectedItem, book)) _bookPicker.SelectedItem = book;
        var loading = _loading; _loading = true;
        try { _filter.ItemsSource = BoqCatalogCategories.For(book); _filter.SelectedIndex = 0; }
        finally { _loading = loading; }
    }
    private void Refresh()
    {
        var selected = _grid.SelectedItem as InfrastructureGroup;
        var visible = _groups.Where(g => !_excluded.Contains(g) &&
            g.Layer.Contains(_layerSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _grid.ItemsSource = visible; _grid.SelectedItem = selected != null && visible.Contains(selected) ? selected : visible.FirstOrDefault();
        _undo.Visibility = _excluded.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        UpdateSummary();
    }
    private void UpdateSummary()
    {
        var active = _groups.Where(g => !_excluded.Contains(g)).ToArray();
        _message.Text = $"{active.Length} שכבות פעילות · {active.Count(g => g.Approved)} שויכו · {_excluded.Count} הוסרו · השיוכים נשמרים אוטומטית במחשב זה";
        _autoAssign.IsEnabled = !_automaticBusy && _grid.Items.OfType<InfrastructureGroup>().Any(g => !g.Approved && double.IsFinite(g.Quantity) && g.Quantity > 0);
    }
    private void SelectLayer()
    {
        _loading = true;
        try
        {
            _catalogSearch.Clear();
            if (_grid.SelectedItem is InfrastructureGroup g)
            {
                var mappedBook = _books.FirstOrDefault(b => b.Catalog.FileHash == g.CatalogHash);
                if (mappedBook != null && _catalog.FileHash != mappedBook.Catalog.FileHash) UseBook(mappedBook);
                _subject.Text = g.ShortLayer + $" · {g.Quantity:N2} {g.MeasurementUnit}";
                _measurement.SelectedItem = g.MeasurementUnit; _manualQuantity.Text = g.ManualQuantity?.ToString("G", CultureInfo.CurrentCulture) ?? ""; UpdateMeasurementInput(g);
                _subject.ToolTip = g.Layer; _topic.SelectedItem = g.Topic;
                _note.Text = g.Evidence + "\n" + InfrastructureCatalogSearch.LayerNote(g);
                _source.Text = $"מקור: {g.Source}\nשכבה: {g.Layer}\n{g.Count} קווים · {g.ClosedCount} סגורים\n{g.Evidence}\n{_scan.Findings.Count} ממצאי סריקה — הפירוט נכלל ב־Excel";
            }
            else { _subject.Text = "אין שכבות בתצוגה"; _note.Text = ""; _source.Text = ""; }
        }
        finally { _loading = false; }
        Search(); UpdateSummary();
    }
    private void Search()
    {
        if (_grid.SelectedItem is not InfrastructureGroup g) { _items.ItemsSource = null; _assign.IsEnabled = false; _detail.Text = ""; _results.Text = ""; return; }
        var options = _search.Find(g, _catalogSearch.Text, category: _filter.SelectedItem as BoqCatalogCategory);
        _items.ItemsSource = options;
        _items.SelectedItem = options.FirstOrDefault(o => o.Code == g.Code && g.CatalogHash == _catalog.FileHash)
            ?? options.FirstOrDefault(o => o.IsRecommended);
        if (_items.SelectedItem != null) _items.ScrollIntoView(_items.SelectedItem);
        _results.Text = options.Count == 0 ? "לא נמצאו סעיפים. נסה מילים אחרות או מספר סעיף." :
            $"{options.Count}{(options.Count == 100 ? "+" : "")} {(_catalogSearch.Text.Trim().Length > 0 ? "תוצאות" : "הצעות לפי השכבה")} · {g.MeasurementUnit}" +
            (options.Any(o => o.IsRecommended) ? " · הסעיף המומלץ מסומן בירוק" : " · אפשר לבחור סעיף ידנית");
        SelectItem();
    }
    private void SelectItem()
    {
        _assign.IsEnabled = _grid.SelectedItem is InfrastructureGroup { Quantity: > 0 } && _items.SelectedItem is InfrastructureCatalogOption;
        _detail.Text = _items.SelectedItem is InfrastructureCatalogOption item
            ? $"{(item.IsRecommended ? "מומלץ לשכבה · " : "")}{item.Code} · {item.PriceDisplay} ₪ ל{item.Unit}\n{item.Description}\n{item.Reason}\n{_catalog.SnapshotId}"
            : _grid.SelectedItem is InfrastructureGroup { Code: not null } g ? $"שויך לסעיף {g.Code}: {g.Description}" : "בחר סעיף כדי לראות את התיאור המלא ולשייך אותו לשכבה.";
    }
    private void Assign()
    {
        if (_grid.SelectedItem is not InfrastructureGroup g || _items.SelectedItem is not InfrastructureCatalogOption item) return;
        g.Assign(_catalog, item.Code); Persist(); _grid.Items.Refresh(); UpdateSummary();
        _message.Text = $"נשמר: {g.ShortLayer} ← {item.Code} · {(g.Total is { } total ? total.ToString("N2") + " ₪" : "חסר מחיר במחירון")}";
    }
    private async Task AssignAutomatically()
    {
        if (_automaticBusy) return;
        _automaticBusy = true; _queryTimer.Stop();
        var previousCursor = Cursor;
        try
        {
            var groups = _grid.Items.OfType<InfrastructureGroup>().ToArray();
            var search = _search; var category = _filter.SelectedItem as BoqCatalogCategory;
            _layoutRoot.IsEnabled = false; Cursor = Cursors.Wait;
            _autoAssign.Content = "משייך…"; _message.Text = "מחפש סעיפים מומלצים לשכבות המוצגות…";
            var batch = await Task.Run(() => search.PlanAutomaticAssignment(groups, category));
            if (_closed) return;
            var assigned = batch.Apply();
            if (assigned > 0) { _lastAutomaticBatch = batch; _undoAuto.Visibility = System.Windows.Visibility.Visible; }
            Persist(); _grid.Items.Refresh(); SelectLayer();
            var remaining = groups.Count(g => !g.Approved);
            _message.Text = $"{assigned} שכבות שויכו אוטומטית · {remaining} נשארו לבחירה ידנית · {batch.ExistingCount} שיוכים קיימים נשמרו";
        }
        catch (System.Exception ex) when (UiGuard.IsRecoverable(ex)) { if (!_closed) UiGuard.Report("שיוך אוטומטי", ex); }
        finally
        {
            _automaticBusy = false; _layoutRoot.IsEnabled = true; Cursor = previousCursor; _autoAssign.Content = "שייך אוטומטית";
            _autoAssign.IsEnabled = _grid.Items.OfType<InfrastructureGroup>().Any(g => !g.Approved && double.IsFinite(g.Quantity) && g.Quantity > 0);
        }
    }
    private void UndoAutomaticAssignment()
    {
        var undone = _lastAutomaticBatch?.Undo() ?? 0;
        _lastAutomaticBatch = null; _undoAuto.Visibility = System.Windows.Visibility.Collapsed;
        Persist(); _grid.Items.Refresh(); SelectLayer();
        _message.Text = $"בוטל שיוך אוטומטי של {undone} שכבות. שינויים ידניים שנעשו לאחר השיוך נשמרו.";
    }
    private void UpdateMeasurementInput(InfrastructureGroup g)
    {
        _manualPanel.Visibility = g.MeasuredQuantity <= 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        _subject.Text = g.ShortLayer + $" · {g.Quantity:N2} {g.MeasurementUnit}";
    }
    private void Export()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את מסך התשתיות בשרטוט הנכון");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "כתב כמויות", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var selected = _groups.Where(g => !_excluded.Contains(g)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("אין קבוצות פעילות לייצוא");
        var keys = selected.ToDictionary(g => (g.Source, g.Layer, g.GroupPart));
        var lines = _scan.Measurements.Select(m => m.Line).Where(l => keys.ContainsKey((l.Source, l.Layer, l.GroupPart)))
            .Select(l => InfrastructureBoq.ReviewedLine(l, keys[(l.Source, l.Layer, l.GroupPart)])).ToArray();
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
        InfrastructureBoq.Export(excel, selected, lines, _books.Select(b => b.Catalog).ToArray(), _scan.Findings, _scan.Drawing, _scan.DrawingHash,
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
