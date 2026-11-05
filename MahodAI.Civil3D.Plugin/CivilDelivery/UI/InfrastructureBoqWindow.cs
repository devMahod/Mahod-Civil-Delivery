using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using CheckBox = System.Windows.Controls.CheckBox;
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
using Grid = System.Windows.Controls.Grid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Visibility = System.Windows.Visibility;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The bill of quantities: every measured layer, what happens to it (new work / existing that stays / existing
/// for demolition), its price-list item, and the road area and pavement-structure volumes. Four tiles on top are
/// both the summary and the way between the views.
/// </summary>
internal sealed class InfrastructureBoqWindow : Window
{
    private enum View { New, Demolish, Keep, Roads }
    internal sealed record SurfaceChoice(RoadSurfaceKind Kind, string Label);
    /// <summary>An editable pavement-structure row (the records are immutable).</summary>
    internal sealed class StructureRow
    {
        public string Name { get; set; } = "";
        public double ThicknessCm { get; set; }
        public string Unit { get; set; } = "מ״ר";
        public string SearchHint { get; set; } = "";
    }

    private readonly InfrastructureDrawingService _scan;
    private CatalogSnapshot _catalog;
    private readonly IReadOnlyList<InfrastructurePriceBook> _books;
    private readonly InfrastructureCatalogUsage _usage;
    private IgorPolylineAudit.Result? _igorAudit;
    private readonly List<InfrastructureGroup> _groups;
    private readonly HashSet<InfrastructureGroup> _excluded = new();
    private InfrastructureCatalogSearch _search;
    private readonly string _reviewPath, _roadsPath;
    private readonly DataGrid _grid = Table(), _items = Table();
    private readonly ComboBox _filter = new() { MinWidth = 130, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6) };
    private readonly ComboBox _topic = new() { ItemsSource = InfrastructureBoq.Topics, MinWidth = 110 };
    private readonly ComboBox _bookPicker = new() { Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 6), DisplayMemberPath = "Label" };
    private readonly TextBox _layerSearch = Input(), _catalogSearch = Input();
    private readonly ComboBox _measurement = new() { ItemsSource = new[] { "מטר", "מ״ר", "מ״ק", "יח׳", "דונם", "טון", "ק״ג", "קומפ׳" }, Padding = new Thickness(5), MinWidth = 90 };
    private readonly TextBox _manualQuantity = Input();
    private readonly StackPanel _manualPanel = new();
    private readonly TextBlock _message = Text(""), _subject = Text("בחר שכבה"), _note = Text(""), _results = Text(""), _detail = Text("");
    private readonly TextBlock _source = Text(""), _viewTitle = Heading(""), _viewHint = Text("");
    private readonly Button _assign = ActionButton("שייך לשכבה", "GoButton", "check"), _undo = ActionButton("החזר שורות שהוסרו", "QuietButton");
    private readonly Button _autoAssign = ActionButton("שייך אוטומטית", "PillPrimaryButton", "play"), _undoAuto = ActionButton("בטל שיוך אוטומטי אחרון", "QuietButton");
    private readonly CheckBox _showDrafting = new() { Content = "הצג גם כיתובים, חתכים וקווי שפה", Margin = new Thickness(0, 6, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly DockPanel _layoutRoot = new() { Margin = new Thickness(16) };
    private InfrastructureAutoAssignmentBatch? _lastAutomaticBatch;
    private readonly System.Windows.Threading.DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _loading, _automaticBusy, _closed;
    private View _view = View.New;
    private readonly Dictionary<View, (Border Tile, TextBlock Value, TextBlock Detail)> _tiles = new();
    private readonly ContentControl _body = new();
    private UIElement _boqBody = null!, _roadsBody = null!;
    // What happens to the selected layer
    private readonly Button _dNew = ActionButton("עבודה חדשה", "PillButton"), _dKeep = ActionButton("קיים — נשאר", "PillButton"), _dDemolish = ActionButton("קיים — לפירוק", "PillButton");
    private readonly TextBlock _dispositionEvidence = Text("");
    private readonly StackPanel _catalogPanel = new();
    // Roads
    private IReadOnlyList<PavementStructure> _structures;
    private List<InfrastructureGroup> _derived = new(), _corridor = new();
    private readonly DataGrid _areaGrid = Table(), _surfaceGrid = Table(), _resultGrid = Table(), _corridorGrid = Table();
    private readonly DataGrid _structureGrid = EditableTable();
    private readonly ComboBox _surfaceKind = new() { DisplayMemberPath = "Label", MinWidth = 170, Padding = new Thickness(6) };
    private readonly ComboBox _structureSurface = new() { DisplayMemberPath = "Label", MinWidth = 170, Padding = new Thickness(6) };
    private readonly ObservableCollection<StructureRow> _structureRows = new();
    private readonly TextBlock _roadsSummary = Text(""), _corridorStatus = Text("");
    private readonly Button _readCorridors = ActionButton("קרא נפחים מקורידורים", "PillPrimaryButton", "scan");
    private static readonly SurfaceChoice[] SurfaceChoices = new[] { RoadSurfaceKind.None, RoadSurfaceKind.Road, RoadSurfaceKind.PublicTransport,
        RoadSurfaceKind.Shoulder, RoadSurfaceKind.BikePath, RoadSurfaceKind.Sidewalk, RoadSurfaceKind.Island, RoadSurfaceKind.Milling, RoadSurfaceKind.StructureArea }
        .Select(k => new SurfaceChoice(k, k == RoadSurfaceKind.None ? "— לא משטח כביש" : FirmLayerConventions.SurfaceName(k))).ToArray();

    internal InfrastructureBoqWindow(InfrastructureDrawingService scan, CatalogSnapshot catalog)
    {
        _scan = scan; _catalog = catalog; _groups = InfrastructureBoq.Group(scan.Measurements.Select(m => m.Line)).ToList();
        _books = new[] { new InfrastructurePriceBook("נתיבי ישראל", "אחוד יולי 2026 · מקור רשמי", catalog, new Dictionary<string,string>(), true) }
            .Concat(InfrastructurePriceBooks.Load(InfrastructurePriceBooks.LocalDirectory, scan.Findings)).ToArray();
        try { _usage = InfrastructureCatalogUsage.Load(Path.Combine(InfrastructurePriceBooks.LocalDirectory, "project-usage.json")); }
        catch (System.Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { _usage = new InfrastructureCatalogUsage(Array.Empty<InfrastructureCatalogUsage.Example>()); scan.Findings.Add("לא נטען ניסיון הפרויקטים: " + ex.Message); }
        _search = new InfrastructureCatalogSearch(_books[0], _usage);
        var reviews = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodCivilDelivery", "infrastructure-reviews");
        _reviewPath = Path.Combine(reviews, InfrastructureReviewStore.FileName(scan.Drawing));
        _roadsPath = Path.ChangeExtension(_reviewPath, ".roads.json");
        _structures = RoadPavement.Load(_roadsPath);
        string? restoreError = null;
        try
        {
            InfrastructureReviewStore.Restore(_reviewPath, scan.DrawingHash, _books.Select(b => b.Catalog).ToArray(), _groups, _excluded);
            RebuildRoads(persist: false);
            InfrastructureReviewStore.Restore(_reviewPath, scan.DrawingHash, _books.Select(b => b.Catalog).ToArray(), _derived, _excluded);
        }
        catch (System.Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { restoreError = "לא ניתן לטעון שיוכים קודמים: " + ex.Message; }

        Title = "Mahod Delivery — כתב כמויות"; Width = 1280; Height = 820; MinWidth = 980; MinHeight = 660;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        Theme.Apply(this, 13);
        Background = Theme.Canvas; Foreground = Theme.Ink;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowState = WindowState.Maximized;
        UiGuard.Attach(this, "כתב כמויות");
        var root = _layoutRoot; Content = root;
        root.Children.Add(Header());
        root.Children.Add(TilesRow());
        var bottom = new DockPanel { Margin = new Thickness(2, 10, 2, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        _undo.Click += (_, _) => UiGuard.Run("החזרת שורות", () => { _excluded.Clear(); Persist(); RebuildRoads(); Refresh(); });
        DockPanel.SetDock(_undo, Dock.Left); bottom.Children.Add(_undo);
        _undoAuto.Visibility = Visibility.Collapsed; _undoAuto.Margin = new Thickness(8, 0, 0, 0);
        _undoAuto.Click += (_, _) => UiGuard.Run("ביטול שיוך אוטומטי", UndoAutomaticAssignment);
        DockPanel.SetDock(_undoAuto, Dock.Left); bottom.Children.Add(_undoAuto);
        _message.VerticalAlignment = VerticalAlignment.Center; bottom.Children.Add(_message);
        _boqBody = BoqBody(); _roadsBody = RoadsBody();
        _body.Margin = new Thickness(0, 12, 0, 0); root.Children.Add(_body);
        Closed += (_, _) => { _closed = true; _queryTimer.Stop(); };
        SetView(View.New);
        if (restoreError != null) _message.Text = restoreError;
    }

    // ───────────────────────────── layout ─────────────────────────────

    private UIElement Header()
    {
        var header = new DockPanel();
        var export = ActionButton("ייצא Excel ושרטוט…", "GoButton", "sheet"); export.Click += (_, _) => UiGuard.Run("ייצוא כתב כמויות", Export);
        export.ToolTip = "כתב כמויות לעבודות חדשות, גיליון פירוקים, קיים שנשאר, כבישים ומדידות מקור; בנוסף קובץ פירוקים נפרד ושרטוט קווי המקור.";
        DockPanel.SetDock(export, Dock.Left); header.Children.Add(export);
        _autoAssign.Margin = new Thickness(8, 0, 0, 0);
        _autoAssign.ToolTip = "שייך את הסעיף המומלץ לכל שכבה שמוצגת וטרם שויכה. שיוכים קיימים נשמרים; קיים שנשאר, כיתובים וקווי שפה אינם משויכים.";
        _autoAssign.Click += async (_, _) => await AssignAutomatically();
        DockPanel.SetDock(_autoAssign, Dock.Left); header.Children.Add(_autoAssign);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "כתב כמויות", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
        var file = Path.GetFileName(_scan.Drawing);
        titles.Children.Add(Text($"{file} · {_books.Count} מחירונים · {_scan.MetresPerHostUnit:G} מטר ליחידת שרטוט · כל המודל והמקורות הטעונים"));
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    private UIElement TilesRow()
    {
        var row = new UniformGrid { Columns = 4, Margin = new Thickness(-5, 10, -5, 0) };
        row.Children.Add(Tile(View.New, "עבודות חדשות", Theme.Accent));
        row.Children.Add(Tile(View.Demolish, "פירוקים והריסות", Theme.Blocked));
        row.Children.Add(Tile(View.Keep, "קיים — נשאר", Theme.Muted));
        row.Children.Add(Tile(View.Roads, "כבישים — שטח ונפח", Theme.Ink));
        DockPanel.SetDock(row, Dock.Top);
        return row;
    }

    private Border Tile(View view, string label, Brush accent)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = accent, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left); head.Children.Add(dot);
        head.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextSoft });
        stack.Children.Add(head);
        var value = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 4, 0, 0) };
        var detail = new TextBlock { Foreground = Theme.Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(value); stack.Children.Add(detail);
        var tile = new Border { Child = stack, Background = Theme.Panel, BorderBrush = Theme.Frame, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(5, 0, 5, 0), Cursor = Cursors.Hand };
        tile.MouseLeftButtonUp += (_, _) => UiGuard.Run("מעבר לתצוגה", () => SetView(view));
        tile.MouseEnter += (_, _) => { if (_view != view) tile.Background = Theme.Hover; };
        tile.MouseLeave += (_, _) => tile.Background = Theme.Panel;
        _tiles[view] = (tile, value, detail);
        return tile;
    }

    private UIElement BoqBody()
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.46, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.54, GridUnitType.Star) });

        // 1 — layers
        var layers = new DockPanel();
        var layerHead = new StackPanel(); DockPanel.SetDock(layerHead, Dock.Top); layers.Children.Add(layerHead);
        layerHead.Children.Add(_viewTitle);
        _viewHint.Margin = new Thickness(0, 0, 0, 6); layerHead.Children.Add(_viewHint);
        var filters = new DockPanel { Margin = new Thickness(0, 2, 0, 4) }; layerHead.Children.Add(filters);
        _filter.DisplayMemberPath = "Label"; _filter.MaxDropDownHeight = 440; _filter.MaxWidth = 260;
        _filter.ToolTip = "תחום מהמחירון — מצמצם את הסעיפים המוצעים בלבד.";
        _filter.ItemsSource = BoqCatalogCategories.For(_books[0]); _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => { if (!_loading) Search(); }; DockPanel.SetDock(_filter, Dock.Right); filters.Children.Add(_filter);
        _layerSearch.ToolTip = "סינון לפי שם שכבה"; _layerSearch.TextChanged += (_, _) => Refresh(); filters.Children.Add(_layerSearch);
        _showDrafting.Checked += (_, _) => Refresh(); _showDrafting.Unchecked += (_, _) => Refresh();
        layerHead.Children.Add(_showDrafting);
        DispositionChipColumn(_grid);
        Column(_grid, "שכבה", "ShortLayer", new DataGridLength(1, DataGridLengthUnitType.Star), ltr: true);
        Column(_grid, "אלמנט", "ElementDisplay", 120);
        Column(_grid, "כמות", "Quantity", 85, "N2"); Column(_grid, "יחידה", "MeasurementUnit", 52); Column(_grid, "סעיף", "MappingDisplay", 100);
        _grid.SelectionChanged += (_, _) => SelectLayer();
        _grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && _grid.SelectedItem is InfrastructureGroup g) { Remove(g); e.Handled = true; } };
        var menu = new ContextMenu();
        AddMenu(menu, "עבודה חדשה", () => SetDisposition(InfrastructureDisposition.New));
        AddMenu(menu, "קיים — נשאר", () => SetDisposition(InfrastructureDisposition.ExistingKeep));
        AddMenu(menu, "קיים — לפירוק", () => SetDisposition(InfrastructureDisposition.ExistingDemolish));
        menu.Items.Add(new Separator());
        AddMenu(menu, "הסר מהתוצר (אפשר להחזיר)", () => { if (_grid.SelectedItem is InfrastructureGroup g) Remove(g); });
        AddMenu(menu, "בטל שיוך", () => { if (_grid.SelectedItem is InfrastructureGroup g) { g.Withdraw(); Persist(); _grid.Items.Refresh(); SelectLayer(); UpdateSummary(); } });
        _grid.ContextMenu = menu;
        var layerFooter = Text("קליק ימני על שורה: מצב (חדש / נשאר / לפירוק) או הסרה. Delete מסיר את השורה.");
        DockPanel.SetDock(layerFooter, Dock.Bottom); layers.Children.Add(layerFooter); layers.Children.Add(_grid);
        var layersCard = Theme.Card(layers, 14); layersCard.Margin = new Thickness(0, 0, 0, 0);
        body.Children.Add(layersCard);

        // 2 — what happens to it, 3 — price-list item
        var editor = new Grid();
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // No minimum: the item list scrolls, the assign row below it must not. With a 110 px minimum the assign
        // button was cut off on a 1280×800 (200 %) screen — only its top edge showed (live 2026-11-05).
        editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var editHead = new StackPanel(); editor.Children.Add(editHead);
        _subject.FontWeight = FontWeights.SemiBold; _subject.FontSize = 15; _subject.Foreground = Theme.Ink;
        _subject.TextTrimming = TextTrimming.CharacterEllipsis; _subject.TextWrapping = TextWrapping.NoWrap; editHead.Children.Add(_subject);
        editHead.Children.Add(Heading("מה קורה בשכבה הזו?", 14));
        var dispositions = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (button, d) in new[] { (_dNew, InfrastructureDisposition.New), (_dKeep, InfrastructureDisposition.ExistingKeep), (_dDemolish, InfrastructureDisposition.ExistingDemolish) })
        {
            button.Style = SegmentStyle(); button.Margin = new Thickness(0, 0, 6, 0);
            button.Click += (_, _) => UiGuard.Run("מצב השכבה", () => SetDisposition(d));
            dispositions.Children.Add(button);
        }
        _dNew.ToolTip = "עבודה לביצוע — נכנסת לכתב הכמויות הראשי.";
        _dKeep.ToolTip = "תשתית קיימת שנשארת — לא מתומחרת; מופיעה לידיעה בגיליון \"קיים — נשאר\".";
        _dDemolish.ToolTip = "תשתית קיימת לפירוק / הריסה — מתומחרת מפרק הפירוקים, בגיליון ובקובץ נפרדים.";
        editHead.Children.Add(dispositions);
        _dispositionEvidence.FontSize = 12; editHead.Children.Add(_dispositionEvidence);
        var sourceBody = new StackPanel(); sourceBody.Children.Add(_source); sourceBody.Children.Add(_topic);
        _topic.SelectionChanged += (_, _) => { if (!_loading && _grid.SelectedItem is InfrastructureGroup g) { g.Topic = _topic.SelectedItem as string ?? InfrastructureBoq.Unknown; Persist(); _grid.Items.Refresh(); Search(); } };
        editHead.Children.Add(new Expander { Header = "פרטי המקור ושינוי נושא", Content = new ScrollViewer { Content = sourceBody, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 2, 0, 0) });

        _catalogPanel.Margin = new Thickness(0, 8, 0, 0);
        _catalogPanel.Children.Add(Heading("סעיף מהמחירון", 14));
        var booksView = new ListCollectionView(_books.ToList()); booksView.GroupDescriptions.Add(new PropertyGroupDescription("Publisher"));
        _bookPicker.ItemsSource = booksView;
        var groupHeader = new FrameworkElementFactory(typeof(TextBlock)); groupHeader.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        groupHeader.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); groupHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(5));
        _bookPicker.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = groupHeader } });
        _bookPicker.SelectedItem = _books[0];
        _bookPicker.SelectionChanged += (_, _) => { if (!_loading && _bookPicker.SelectedItem is InfrastructurePriceBook book) { UseBook(book); Search(); } };
        var bookRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        _measurement.ToolTip = "יחידת המדידה של השכבה"; _measurement.Margin = new Thickness(8, 4, 0, 6);
        DockPanel.SetDock(_measurement, Dock.Right); bookRow.Children.Add(_measurement); bookRow.Children.Add(_bookPicker);
        _catalogPanel.Children.Add(bookRow);
        _measurement.SelectionChanged += (_, _) => { if (!_loading && _grid.SelectedItem is InfrastructureGroup g && _measurement.SelectedItem is string unit) { g.MeasurementUnit = unit; _loading = true; _manualQuantity.Clear(); _loading = false; UpdateMeasurementInput(g); Persist(); _grid.Items.Refresh(); Search(); UpdateSummary(); } };
        _manualPanel.Children.Add(Text("אין מדידה מהשרטוט ביחידה זו. כמות להזנת המהנדס:")); _manualPanel.Children.Add(_manualQuantity); _catalogPanel.Children.Add(_manualPanel);
        _manualQuantity.TextChanged += (_, _) => { if (_loading || _grid.SelectedItem is not InfrastructureGroup g) return;
            g.ManualQuantity = double.TryParse(_manualQuantity.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var n) && double.IsFinite(n) && n > 0 ? n : null;
            UpdateMeasurementInput(g); Persist(); _grid.Items.Refresh(); SelectItem(); UpdateSummary(); };
        _note.Foreground = Theme.Review; _note.FontSize = 12; _catalogPanel.Children.Add(_note);
        _catalogSearch.ToolTip = "חיפוש בעברית או מספר סעיף, לדוגמה: פירוק אבני שפה · צינור ביוב 315";
        _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); UiGuard.Run("חיפוש סעיפים", Search); };
        _catalogSearch.TextChanged += (_, _) => { _queryTimer.Stop(); if (!_loading) _queryTimer.Start(); };
        if (Theme.Dictionary["SearchBox"] is Style searchStyle) _catalogSearch.Style = searchStyle;
        _catalogPanel.Children.Add(_catalogSearch);
        _results.FontSize = 11; _catalogPanel.Children.Add(_results);
        editHead.Children.Add(_catalogPanel);

        // The item's description beside the button that assigns it: always on screen, whatever the window height.
        var choice = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(choice, 2); editor.Children.Add(choice);
        _assign.IsEnabled = false; _assign.VerticalAlignment = VerticalAlignment.Center; _assign.Margin = new Thickness(10, 0, 0, 0);
        _assign.Click += (_, _) => UiGuard.Run("שיוך שכבה", Assign);
        DockPanel.SetDock(_assign, Dock.Left); choice.Children.Add(_assign);
        _detail.FontSize = 12;
        choice.Children.Add(new ScrollViewer { Content = _detail, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        RecommendedCodeColumn(_items); Column(_items, "תיאור", "Description", new DataGridLength(1, DataGridLengthUnitType.Star)); Column(_items, "₪ ליחידה", "PriceDisplay", 80);
        _items.SelectionChanged += (_, _) => SelectItem();
        _items.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && FindRow(d) != null && _assign.IsEnabled) UiGuard.Run("שיוך שכבה", Assign); };
        _items.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && _assign.IsEnabled) { UiGuard.Run("שיוך שכבה", Assign); e.Handled = true; } };
        Grid.SetRow(_items, 1); _items.Margin = new Thickness(0, 4, 0, 0); editor.Children.Add(_items);
        var editorCard = Theme.Card(editor, 14); editorCard.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(editorCard, 1); body.Children.Add(editorCard);
        return body;
    }

    private UIElement RoadsBody()
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.45, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star) });

        // Areas
        var areas = new DockPanel();
        var areaHead = new StackPanel(); DockPanel.SetDock(areaHead, Dock.Top); areas.Children.Add(areaHead);
        areaHead.Children.Add(Heading("1  שטחים בשרטוט"));
        areaHead.Children.Add(Text("שכבות עם Hatch או גבול סגור. סוג המשטח מזוהה משם השכבה; לשינוי — בחר שכבה ובחר סוג."));
        var kindRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var kindLabel = Text("סוג המשטח של השכבה:"); kindLabel.Margin = new Thickness(0, 4, 8, 4); kindLabel.VerticalAlignment = VerticalAlignment.Center;
        kindRow.Children.Add(kindLabel); _surfaceKind.ItemsSource = SurfaceChoices; kindRow.Children.Add(_surfaceKind);
        _surfaceKind.SelectionChanged += (_, _) => { if (_loading || _areaGrid.SelectedItem is not InfrastructureGroup g || _surfaceKind.SelectedItem is not SurfaceChoice c) return;
            UiGuard.Run("סוג משטח", () => { g.Surface = c.Kind; RebuildRoads(); _areaGrid.Items.Refresh(); UpdateSummary(); }); };
        areaHead.Children.Add(kindRow);
        DispositionChipColumn(_areaGrid);
        Column(_areaGrid, "שכבה", "LayerName", new DataGridLength(1, DataGridLengthUnitType.Star), ltr: true);
        Column(_areaGrid, "סוג משטח", "SurfaceName", 110); Column(_areaGrid, "שטח מ״ר", "AreaSquareMetres", 95, "N2");
        _areaGrid.RowHeight = 36; _areaGrid.MinHeight = 160;
        _areaGrid.SelectionChanged += (_, _) => { _loading = true; try { _surfaceKind.SelectedItem = _areaGrid.SelectedItem is InfrastructureGroup g ? SurfaceChoices.First(c => c.Kind == g.Surface) : null; } finally { _loading = false; } };
        var totalsHead = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(totalsHead, Dock.Bottom);
        totalsHead.Children.Add(Heading("סיכום לפי סוג משטח", 14));
        Column(_surfaceGrid, "משטח", "SurfaceName", 120); Column(_surfaceGrid, "מצב", "DispositionLabel", 110);
        Column(_surfaceGrid, "שטח מ״ר", "AreaSquareMetres", 100, "N2"); Column(_surfaceGrid, "שכבות", "LayersDisplay", new DataGridLength(1, DataGridLengthUnitType.Star), ltr: true);
        _surfaceGrid.MaxHeight = 190; _surfaceGrid.RowHeight = 32;
        totalsHead.Children.Add(_surfaceGrid);
        areas.Children.Add(totalsHead);
        areas.Children.Add(_areaGrid);
        body.Children.Add(Theme.Card(areas, 14));

        // Structure, results, corridors
        var right = new StackPanel();
        var structure = new StackPanel(); right.Children.Add(structure);
        structure.Children.Add(Heading("2  מבנה המיסעה ← נפחים"));
        structure.Children.Add(Text("נפח = שטח × עובי. אספלט משולם במ״ר בעובי נתון, מצעים במ״ק. ערכי ברירת המחדל לעריכה — עד שנקבל את שיטת התמחור של המשרד. שכבות המבנה נוספות לכתב הכמויות ומשויכות שם לסעיף כמו כל שכבה."));
        var surfaceRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var sLabel = Text("משטח:"); sLabel.Margin = new Thickness(0, 4, 8, 4); sLabel.VerticalAlignment = VerticalAlignment.Center;
        surfaceRow.Children.Add(sLabel);
        _structureSurface.ItemsSource = SurfaceChoices.Where(c => c.Kind is not (RoadSurfaceKind.None or RoadSurfaceKind.StructureArea)).ToArray();
        _structureSurface.SelectionChanged += (_, _) => LoadStructureRows();
        surfaceRow.Children.Add(_structureSurface);
        var add = ActionButton("הוסף שכבה", "PillButton"); add.Margin = new Thickness(8, 0, 0, 0);
        add.Click += (_, _) => UiGuard.Run("הוספת שכבה", () => { _structureRows.Add(new StructureRow { Name = "שכבה חדשה", ThicknessCm = 10, Unit = "מ״ק", SearchHint = "" }); SaveStructureRows(); });
        var remove = ActionButton("הסר שכבה", "QuietButton"); remove.Margin = new Thickness(6, 0, 0, 0);
        remove.Click += (_, _) => UiGuard.Run("הסרת שכבה", () => { if (_structureGrid.SelectedItem is StructureRow r) { _structureRows.Remove(r); SaveStructureRows(); } });
        var reset = ActionButton("ברירת מחדל", "QuietButton"); reset.Margin = new Thickness(6, 0, 0, 0);
        reset.Click += (_, _) => UiGuard.Run("ברירת מחדל", ResetStructure);
        foreach (var b in new[] { reset, remove, add }) { DockPanel.SetDock(b, Dock.Right); surfaceRow.Children.Insert(0, b); }
        structure.Children.Add(surfaceRow);
        _structureGrid.ItemsSource = _structureRows;
        _structureGrid.Columns.Add(new DataGridTextColumn { Header = "שכבה", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _structureGrid.Columns.Add(new DataGridTextColumn { Header = "עובי ס״מ", Binding = new Binding("ThicknessCm") { StringFormat = "0.#" }, Width = 80 });
        _structureGrid.Columns.Add(new DataGridComboBoxColumn { Header = "תשלום לפי", SelectedItemBinding = new Binding("Unit"), ItemsSource = new[] { "מ״ר", "מ״ק" }, Width = 85 });
        _structureGrid.Columns.Add(new DataGridTextColumn { Header = "מילות חיפוש במחירון", Binding = new Binding("SearchHint"), Width = 170 });
        _structureGrid.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => UiGuard.Run("מבנה מיסעה", SaveStructureRows)), System.Windows.Threading.DispatcherPriority.Background);
        _structureGrid.MaxHeight = 220;
        structure.Children.Add(_structureGrid);
        structure.Children.Add(Heading("כמויות מבנה — עבודה חדשה", 14));
        Column(_resultGrid, "משטח", "SurfaceName", 95); Column(_resultGrid, "שכבה", "LayerDisplay", new DataGridLength(1, DataGridLengthUnitType.Star));
        Column(_resultGrid, "שטח מ״ר", "AreaSquareMetres", 95, "N2"); Column(_resultGrid, "נפח מ״ק", "VolumeCubicMetres", 90, "N2");
        Column(_resultGrid, "לתשלום", "Quantity", 90, "N2"); Column(_resultGrid, "יחידה", "Unit", 50);
        _resultGrid.RowHeight = 34; _resultGrid.MaxHeight = 230;
        structure.Children.Add(_resultGrid);
        _roadsSummary.FontWeight = FontWeights.SemiBold; structure.Children.Add(_roadsSummary);

        var corridor = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        corridor.Children.Add(Heading("3  קורידורים ב־Civil 3D", 14));
        var count = RoadCorridorVolumes.CorridorCount(AcadApp.DocumentManager.MdiActiveDocument);
        _corridorStatus.Text = count > 0
            ? $"בשרטוט {count} קורידורים. נפח לכל קוד מבנה (Pave, Base, SubBase…) לפי ממוצע שטחי קצה לאורך התחנות, וחפירה/מילוי מחתכי Sample Lines."
            : "אין קורידורים בשרטוט — החישוב לעיל מבוסס על שטחי התכנית.";
        corridor.Children.Add(_corridorStatus);
        _readCorridors.IsEnabled = count > 0; _readCorridors.HorizontalAlignment = HorizontalAlignment.Left; _readCorridors.Margin = new Thickness(0, 4, 0, 6);
        _readCorridors.Click += (_, _) => UiGuard.Run("נפחים מקורידורים", ReadCorridors);
        corridor.Children.Add(_readCorridors);
        Column(_corridorGrid, "קוד / חומר", "ShortLayer", 150, ltr: true); Column(_corridorGrid, "נפח מ״ק", "VolumeCubicMetres", 100, "N2");
        Column(_corridorGrid, "שיטה", "Evidence", new DataGridLength(1, DataGridLengthUnitType.Star));
        _corridorGrid.RowHeight = 34; _corridorGrid.MaxHeight = 170; _corridorGrid.Visibility = Visibility.Collapsed;
        corridor.Children.Add(_corridorGrid);
        var mahod3d = Text("כלי Mahod 3D לאותה מטרה: Corridor ‹ Calculate Asfalts by Sections (Cr_CalcAsfaltsBySection) — שכבות אספלט וקרצוף לפי חתכים; " +
            "Calculate Structure Areas (Cr_CalcStructureAreas) — שטחי מבנה בשכבות CS_<קוד>, שנקראות כאן אוטומטית כ\"שטח מבנה\".");
        mahod3d.FontSize = 11.5; corridor.Children.Add(mahod3d);
        right.Children.Add(corridor);
        var rightCard = Theme.Card(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 14);
        rightCard.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(rightCard, 1); body.Children.Add(rightCard);
        _structureSurface.SelectedIndex = 0;
        return body;
    }

    // ───────────────────────────── views ─────────────────────────────

    private void SetView(View view)
    {
        _view = view;
        foreach (var (v, t) in _tiles)
        {
            t.Tile.BorderBrush = v == view ? Theme.Accent : Theme.Frame;
            t.Tile.BorderThickness = new Thickness(v == view ? 2 : 1);
            t.Tile.Background = v == view ? Theme.AccentTint : Theme.Panel;
        }
        if (view == View.Roads)
        {
            _body.Content = _roadsBody;
            RefreshRoads();
        }
        else
        {
            _body.Content = _boqBody;
            (_viewTitle.Text, _viewHint.Text) = view switch
            {
                View.Demolish => ("פירוקים והריסות", "תשתיות קיימות שסומנו לפירוק. הסעיפים מוצעים מפרק הפירוקים (51.01) לפי סוג האלמנט. בייצוא: גיליון נפרד וקובץ פירוקים נפרד."),
                View.Keep => ("קיים — נשאר", "ASMADE, ‎-EX, KAYAM וקבצי מדידה. לא מתומחרים. אם אלמנט נהרס — סמן \"קיים — לפירוק\"."),
                _ => ("עבודות חדשות", "שכבות התכנון ושכבות מבנה המיסעה. שייך אוטומטית, או בחר שכבה וסעיף."),
            };
            Refresh();
        }
        UpdateSummary();
    }

    private IEnumerable<InfrastructureGroup> Active => _groups.Where(g => !_excluded.Contains(g));
    private static bool PayItem(InfrastructureGroup g) => g.IsPayItem;

    private void Refresh()
    {
        if (_view == View.Roads) { RefreshRoads(); return; }
        var selected = _grid.SelectedItem as InfrastructureGroup;
        var want = _view switch { View.Demolish => InfrastructureDisposition.ExistingDemolish, View.Keep => InfrastructureDisposition.ExistingKeep, _ => InfrastructureDisposition.New };
        var text = _layerSearch.Text.Trim();
        var visible = Active.Where(g => g.Disposition == want && (_showDrafting.IsChecked == true || PayItem(g)) &&
            (g.Layer.Contains(text, StringComparison.OrdinalIgnoreCase) || g.ElementDisplay.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        _grid.ItemsSource = visible; _grid.SelectedItem = selected != null && visible.Contains(selected) ? selected : visible.FirstOrDefault();
        if (_grid.SelectedItem == null) SelectLayer();
        _undo.Visibility = _excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var active = Active.ToArray();
        void Fill(View v, InfrastructureDisposition d)
        {
            var rows = active.Where(g => g.Disposition == d && PayItem(g)).ToArray();
            var total = rows.Sum(g => g.Total ?? 0m);
            _tiles[v].Value.Text = d == InfrastructureDisposition.ExistingKeep ? $"{rows.Length} שכבות" : total > 0 ? $"₪ {total:N0}" : $"{rows.Length} שכבות";
            _tiles[v].Detail.Text = d == InfrastructureDisposition.ExistingKeep ? "לידיעה — ללא תמחור"
                : $"{rows.Length} שכבות · {rows.Count(g => g.Approved)} שויכו" + (rows.Any(g => g.Approved && g.Price == null) ? " · חסר מחיר" : "");
        }
        Fill(View.New, InfrastructureDisposition.New); Fill(View.Demolish, InfrastructureDisposition.ExistingDemolish); Fill(View.Keep, InfrastructureDisposition.ExistingKeep);
        var surfaces = RoadPavement.Surfaces(_groups, _excluded);
        var road = surfaces.Where(s => s.Disposition == InfrastructureDisposition.New).Sum(s => s.AreaSquareMetres);
        _tiles[View.Roads].Value.Text = $"{road:N0} מ״ר";
        _tiles[View.Roads].Detail.Text = $"{surfaces.Count(s => s.Disposition == InfrastructureDisposition.New)} סוגי משטח חדשים · {_derived.Count} שכבות מבנה" + (_corridor.Count > 0 ? $" · {_corridor.Count} קודי קורידור" : "");
        _message.Text = $"{active.Length} שכבות פעילות · {active.Count(g => g.Approved)} שויכו · {_excluded.Count} הוסרו · השיוכים נשמרים אוטומטית במחשב זה";
        _autoAssign.IsEnabled = !_automaticBusy && _view != View.Roads && _grid.Items.OfType<InfrastructureGroup>().Any(g => !g.Approved && double.IsFinite(g.Quantity) && g.Quantity > 0 && PayItem(g) && g.Disposition != InfrastructureDisposition.ExistingKeep);
    }

    // ───────────────────────────── layer editing ─────────────────────────────

    private void SetDisposition(InfrastructureDisposition d)
    {
        if (_grid.SelectedItem is not InfrastructureGroup g || g.Disposition == d) return;
        var wasApproved = g.Approved;
        g.Disposition = d; Persist();
        if (g.Surface != RoadSurfaceKind.None) RebuildRoads();
        var to = d switch { InfrastructureDisposition.ExistingDemolish => "פירוקים והריסות", InfrastructureDisposition.ExistingKeep => "קיים — נשאר", _ => "עבודות חדשות" };
        Refresh();
        _message.Text = $"{g.ShortLayer} עבר ל\"{to}\"" + (wasApproved ? " — השיוך הקודם בוטל, יש לבחור סעיף מתאים" : "");
    }

    private void Remove(InfrastructureGroup group) { _excluded.Add(group); Persist(); if (group.Surface != RoadSurfaceKind.None) RebuildRoads(); Refresh(); }
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
                _measurement.SelectedItem = g.MeasurementUnit; _manualQuantity.Text = g.ManualQuantity?.ToString("G", CultureInfo.CurrentCulture) ?? ""; UpdateMeasurementInput(g);
                _subject.ToolTip = g.Layer; _topic.SelectedItem = g.Topic;
                _note.Text = g.Evidence + "\n" + InfrastructureCatalogSearch.LayerNote(g);
                _source.Text = $"מקור: {g.Source}\nשכבה: {g.Layer}\n{g.Count} עצמים · {g.ClosedCount} סגורים\n{g.Evidence}\n{_scan.Findings.Count} ממצאי סריקה — הפירוט נכלל ב־Excel";
                PaintSegments(g.Disposition);
                _dispositionEvidence.Text = g.ConditionEvidence.Length > 0
                    ? "זוהה אוטומטית: " + g.ConditionEvidence + (g.DispositionChangedByEngineer ? " · שונה על ידך" : "")
                    : g.DispositionChangedByEngineer ? "שונה על ידך" : "לא זוהה סימן למצב קיים — עבודה חדשה";
                _dispositionEvidence.Foreground = g.Disposition == InfrastructureDisposition.ExistingDemolish ? Theme.Blocked : Theme.Muted;
                _catalogPanel.IsEnabled = g.Disposition != InfrastructureDisposition.ExistingKeep;
                _items.IsEnabled = _catalogPanel.IsEnabled;
                _dNew.IsEnabled = _dKeep.IsEnabled = _dDemolish.IsEnabled = g.Source != RoadPavement.DerivedSource && g.GroupPart != RoadCorridorVolumes.Part;
            }
            else
            {
                _subject.Text = "אין שכבות בתצוגה"; _note.Text = ""; _source.Text = ""; _dispositionEvidence.Text = "";
                _dNew.IsEnabled = _dKeep.IsEnabled = _dDemolish.IsEnabled = false;
            }
        }
        finally { _loading = false; }
        Search(); UpdateSummary();
    }
    private void Search()
    {
        if (_grid.SelectedItem is not InfrastructureGroup g || g.Disposition == InfrastructureDisposition.ExistingKeep)
        {
            _items.ItemsSource = null; _assign.IsEnabled = false; _results.Text = "";
            _detail.Text = _grid.SelectedItem is InfrastructureGroup keep ? "קיים שנשאר — לא מתומחר. מופיע בייצוא בגיליון \"קיים — נשאר\"." : "";
            return;
        }
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
            if (assigned > 0) { _lastAutomaticBatch = batch; _undoAuto.Visibility = Visibility.Visible; }
            Persist(); _grid.Items.Refresh(); SelectLayer();
            var remaining = groups.Count(g => !g.Approved && PayItem(g) && g.Disposition != InfrastructureDisposition.ExistingKeep);
            _message.Text = $"{assigned} שכבות שויכו אוטומטית · {remaining} נשארו לבחירה ידנית · {batch.ExistingCount} שיוכים קיימים נשמרו";
        }
        catch (System.Exception ex) when (UiGuard.IsRecoverable(ex)) { if (!_closed) UiGuard.Report("שיוך אוטומטי", ex); }
        finally
        {
            _automaticBusy = false; _layoutRoot.IsEnabled = true; Cursor = previousCursor; _autoAssign.Content = "שייך אוטומטית";
            UpdateSummary();
        }
    }
    private void UndoAutomaticAssignment()
    {
        var undone = _lastAutomaticBatch?.Undo() ?? 0;
        _lastAutomaticBatch = null; _undoAuto.Visibility = Visibility.Collapsed;
        Persist(); _grid.Items.Refresh(); SelectLayer();
        _message.Text = $"בוטל שיוך אוטומטי של {undone} שכבות. שינויים ידניים שנעשו לאחר השיוך נשמרו.";
    }
    private void UpdateMeasurementInput(InfrastructureGroup g)
    {
        _manualPanel.Visibility = g.MeasuredQuantity <= 0 ? Visibility.Visible : Visibility.Collapsed;
        _subject.Text = Ltr(g.ShortLayer) + $" · {g.Quantity:N2} {g.MeasurementUnit}";
    }

    // ───────────────────────────── roads ─────────────────────────────

    /// <summary>Re-derives the pavement-structure rows from the current areas and structure; keeps their price choices.</summary>
    private void RebuildRoads(bool persist = true)
    {
        var old = _derived.ToDictionary(g => g.Layer);
        _groups.RemoveAll(g => g.Source == RoadPavement.DerivedSource);
        // The structure rows price the area; the area row itself must not be priced a second time.
        RoadPavement.MarkPricedByStructure(_groups, _structures);
        var surfaces = RoadPavement.Surfaces(_groups, _excluded);
        var layers = RoadPavement.Structure(surfaces, _structures);
        _derived = RoadPavement.DerivedGroups(layers).ToList();
        foreach (var d in _derived)
        {
            if (!old.TryGetValue(d.Layer, out var o)) continue;
            if (_excluded.Remove(o)) _excluded.Add(d);
            if (!o.Approved) continue;
            var book = _books.FirstOrDefault(b => b.Catalog.FileHash == o.CatalogHash);
            if (book != null && d.Quantity > 0 && book.Catalog.Items.TryGetValue(o.Code!, out var item) && item.Unit.SameUnit(Units.Parse(d.MeasurementUnit)))
                d.Assign(book.Catalog, o.Code!, o.AutomaticallyAssigned);
        }
        foreach (var gone in old.Values) _excluded.Remove(gone);
        _groups.AddRange(_derived);
        if (persist) Persist();
        if (_view == View.Roads) RefreshRoads();
    }

    private void RefreshRoads()
    {
        var selected = _areaGrid.SelectedItem;
        _areaGrid.ItemsSource = Active.Where(g => g.AreaSquareMetres > 0 && g.Source != RoadPavement.DerivedSource && g.GroupPart != RoadCorridorVolumes.Part)
            .OrderBy(g => g.Surface == RoadSurfaceKind.None).ThenBy(g => (int)g.Surface).ThenByDescending(g => g.AreaSquareMetres).ToArray();
        if (selected != null) _areaGrid.SelectedItem = selected;
        var surfaces = RoadPavement.Surfaces(_groups, _excluded);
        _surfaceGrid.ItemsSource = surfaces;
        var layers = RoadPavement.Structure(surfaces, _structures);
        _resultGrid.ItemsSource = layers;
        var asphalt = layers.Where(l => !l.Layer.ByVolume).Sum(l => l.AreaSquareMetres);
        var volume = layers.Where(l => l.Layer.ByVolume).Sum(l => l.VolumeCubicMetres);
        var newArea = surfaces.Where(s => s.Disposition == InfrastructureDisposition.New).Sum(s => s.AreaSquareMetres);
        _roadsSummary.Text = newArea > 0
            ? $"שטח חדש {newArea:N2} מ״ר · נפח שכבות המשולמות במ״ק {volume:N2} מ״ק · {_derived.Count} שורות בכתב הכמויות (תחת \"עבודות חדשות\")"
            : "לא נמצאו שטחי משטח חדשים. סמן סוג משטח לשכבת Hatch משמאל, או קרא נפחים מקורידורים.";
        UpdateSummary();
    }

    private void LoadStructureRows()
    {
        if (_structureSurface.SelectedItem is not SurfaceChoice c) return;
        var s = _structures.FirstOrDefault(x => x.Surface == c.Kind);
        _loading = true;
        try
        {
            _structureRows.Clear();
            foreach (var l in s?.Layers ?? Array.Empty<PavementLayer>())
                _structureRows.Add(new StructureRow { Name = l.Name, ThicknessCm = l.ThicknessCm, Unit = l.Unit, SearchHint = l.SearchHint });
        }
        finally { _loading = false; }
    }

    private void SaveStructureRows()
    {
        if (_loading || _structureSurface.SelectedItem is not SurfaceChoice c) return;
        var layers = _structureRows.Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new PavementLayer(r.Name.Trim(), double.IsFinite(r.ThicknessCm) && r.ThicknessCm >= 0 ? r.ThicknessCm : 0, r.Unit == "מ״ק" ? "מ״ק" : "מ״ר", r.SearchHint?.Trim() ?? ""))
            .ToArray();
        _structures = _structures.Select(s => s.Surface == c.Kind ? s with { Layers = layers } : s).ToArray();
        if (_structures.All(s => s.Surface != c.Kind)) _structures = _structures.Append(new PavementStructure(c.Kind, layers)).ToArray();
        RoadPavement.Save(_roadsPath, _structures);
        RebuildRoads();
        _message.Text = $"מבנה {c.Label} נשמר לשרטוט זה · {layers.Length} שכבות";
    }

    private void ResetStructure()
    {
        if (_structureSurface.SelectedItem is not SurfaceChoice c) return;
        var d = RoadPavement.Defaults.FirstOrDefault(s => s.Surface == c.Kind);
        if (d == null) return;
        _structures = _structures.Select(s => s.Surface == c.Kind ? d : s).ToArray();
        RoadPavement.Save(_roadsPath, _structures);
        LoadStructureRows(); RebuildRoads();
    }

    private void ReadCorridors()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את כתב הכמויות בשרטוט הנכון");
        var previous = Cursor; Cursor = Cursors.Wait;
        try
        {
            var (groups, findings) = RoadCorridorVolumes.Read(doc, _scan.DrawingHash);
            _groups.RemoveAll(g => g.GroupPart == RoadCorridorVolumes.Part);
            _corridor = groups.ToList();
            InfrastructureReviewStore.Restore(_reviewPath, _scan.DrawingHash, _books.Select(b => b.Catalog).ToArray(), _corridor, _excluded);
            _groups.AddRange(_corridor);
            foreach (var f in findings) if (!_scan.Findings.Contains(f)) _scan.Findings.Add(f);
            _corridorGrid.ItemsSource = _corridor; _corridorGrid.Visibility = _corridor.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Persist(); RefreshRoads();
            _message.Text = _corridor.Count > 0
                ? $"נקראו {_corridor.Count} קודי נפח מקורידורים ({_corridor.Sum(g => g.VolumeCubicMetres):N1} מ״ק) — נוספו ל\"עבודות חדשות\" לשיוך סעיף"
                : "לא נמצאו נפחי מבנה בקורידורים (אין Shapes עם קודים)" + (findings.Count > 0 ? " · " + findings[0] : "");
        }
        finally { Cursor = previous; }
    }

    // ───────────────────────────── export ─────────────────────────────

    private void Export()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את מסך התשתיות בשרטוט הנכון");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "כתב כמויות", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var selected = Active.ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("אין קבוצות פעילות לייצוא");
        var keys = selected.GroupBy(g => (g.Source, g.Layer, g.GroupPart)).ToDictionary(g => g.Key, g => g.First());
        var lines = _scan.Measurements.Select(m => m.Line).Where(l => keys.ContainsKey((l.Source, l.Layer, l.GroupPart)))
            .Select(l => InfrastructureBoq.ReviewedLine(l, keys[(l.Source, l.Layer, l.GroupPart)]) with { Disposition = keys[(l.Source, l.Layer, l.GroupPart)].Disposition }).ToArray();
        _igorAudit = IgorPolylineAudit.Capture(doc.Database);
        if (_igorAudit.Status == "mismatch")
            throw new InvalidOperationException("מדידת איגור אינה תואמת למדידה העצמאית. יש לבדוק את השרטוט לפני ייצוא.");
        var engine = "AutoCAD Curve.GetDistanceAtParameter; quantities in metres. " +
            (_igorAudit.Status == "matched"
                ? "Original installed MAHOD3D CalcPolylines invoked at export and independently matched. "
                : "Original MAHOD3D comparison status: " + _igorAudit.Status + ". " + _igorAudit.Error + ". ") +
            "Igor scope: " + _igorAudit.Scope + "; Igor receipt lengths are in drawing units. See igor-audit.json. " +
            "Road structure rows = plan area × layer thickness (editable structure); corridor rows = Civil 3D shape areas, average end area.";
        // Each export gets a new directory: never overwrite an existing drawing or its trace.
        var directory = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var excel = Path.Combine(directory, Path.GetFileName(dialog.FileName));
        var surfaces = RoadPavement.Surfaces(_groups, _excluded);
        var roads = new RoadPavement.Report(surfaces, RoadPavement.Structure(surfaces, _structures), _corridor.Where(g => !_excluded.Contains(g)).ToArray());
        var catalogs = _books.Select(b => b.Catalog).ToArray();
        InfrastructureBoq.Export(excel, selected, lines, catalogs, _scan.Findings, _scan.Drawing, _scan.DrawingHash, engine, roads);
        var demolition = selected.Count(g => g.Disposition == InfrastructureDisposition.ExistingDemolish);
        if (demolition > 0)
            InfrastructureBoq.ExportDemolition(Path.Combine(directory, Path.GetFileNameWithoutExtension(dialog.FileName) + " - פירוקים.xlsx"),
                selected, catalogs, _scan.Findings, _scan.Drawing, _scan.DrawingHash, engine);
        var notCopied = _scan.SaveDrawing(Path.Combine(directory, "Infrastructure-lines.dwg"), selected.Where(g => g.Source != RoadPavement.DerivedSource && g.GroupPart != RoadCorridorVolumes.Part));
        File.WriteAllText(Path.Combine(directory, "review.json"), JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "igor-audit.json"), JsonSerializer.Serialize(_igorAudit ??
            new IgorPolylineAudit.Result("not-run", "", "Model Space LWPOLYLINE only", Array.Empty<IgorPolylineAudit.Comparison>(), "Run the Igor comparison before relying on it"), new JsonSerializerOptions { WriteIndented = true }));
        _message.Text = "נשמרו בתיקייה: " + directory + (demolition > 0 ? " · כולל קובץ פירוקים נפרד" : "") +
            (notCopied > 0 ? $" · {notCopied} עצמים לא הועתקו לשרטוט הביקורת (רשימה ב־not-copied.txt; הכמויות ב־Excel שלמות)" : "");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = directory, UseShellExecute = true }); } catch (System.Exception) { }
    }

    // ───────────────────────────── building blocks ─────────────────────────────

    /// <summary>A flat segment; the selected one is painted in its state's colour by <see cref="PaintSegments"/>.</summary>
    private static Style SegmentStyle()
    {
        var style = new Style(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "b");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Panel));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Frame));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextSoft));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45)); style.Triggers.Add(disabled);
        return style;
    }
    private void PaintSegments(InfrastructureDisposition current)
    {
        foreach (var (b, d, fill) in new[] { (_dNew, InfrastructureDisposition.New, Theme.Accent), (_dKeep, InfrastructureDisposition.ExistingKeep, Theme.Muted),
            (_dDemolish, InfrastructureDisposition.ExistingDemolish, Theme.Blocked) })
        {
            var on = d == current;
            b.Background = on ? fill : Theme.Panel; b.BorderBrush = on ? fill : Theme.Frame; b.Foreground = on ? Brushes.White : Theme.TextSoft;
        }
    }

    /// <summary>Layer names are Latin codes: shown left-to-right inside the right-to-left window (a leading "!" otherwise jumps to the end).</summary>
    private static string Ltr(string text) => "\u202A" + text + "\u202C";

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextSoft, Margin = new Thickness(0, 3, 0, 3) };
    private static TextBlock Heading(string value, double size = 17) => new() { Text = value, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 6, 0, 4) };
    private static TextBox Input() => new() { Padding = new Thickness(7), VerticalContentAlignment = VerticalAlignment.Center };
    private static Button ActionButton(string text, string style, string? icon = null)
    {
        var button = new Button { Content = text, Cursor = Cursors.Hand, Tag = icon, Padding = new Thickness(14, 8, 14, 8) };
        if (Theme.Dictionary[style] is Style s) button.Style = s;
        return button;
    }
    internal static DataGrid Table()
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(231, 236, 242)), BorderBrush = new SolidColorBrush(Color.FromRgb(218, 225, 234)),
            Background = Brushes.White, RowBackground = Brushes.White, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(247, 249, 252)), RowHeight = 46, FontSize = 12 };
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled);
        return table;
    }
    private static DataGrid EditableTable()
    {
        var table = Table(); table.IsReadOnly = false; table.RowHeight = 34; table.CanUserAddRows = false;
        table.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        return table;
    }
    private static void Column(DataGrid grid, string title, string path, DataGridLength width, string? format = null, bool ltr = false)
    {
        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        if (ltr)
        {
            style.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, System.Windows.FlowDirection.LeftToRight));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, System.Windows.TextAlignment.Right));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        }
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path) { StringFormat = format }, Width = width, ElementStyle = style });
    }
    /// <summary>A coloured chip: green = new work, grey = existing that stays, red = existing for demolition.</summary>
    internal static void DispositionChipColumn(DataGrid grid)
    {
        var chip = new FrameworkElementFactory(typeof(Border));
        chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); chip.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
        chip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); chip.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Theme.AccentTint));
        foreach (var (value, brush) in new[] { (InfrastructureDisposition.ExistingKeep, Theme.Panel2), (InfrastructureDisposition.ExistingDemolish, Theme.BlockedTint) })
        { var t = new DataTrigger { Binding = new Binding("Disposition"), Value = value }; t.Setters.Add(new Setter(Border.BackgroundProperty, brush)); style.Triggers.Add(t); }
        chip.SetValue(FrameworkElement.StyleProperty, style);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding("DispositionShort"));
        label.SetValue(TextBlock.FontSizeProperty, 11d); label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var labelStyle = new Style(typeof(TextBlock)); labelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Accent));
        foreach (var (value, brush) in new[] { (InfrastructureDisposition.ExistingKeep, Theme.Muted), (InfrastructureDisposition.ExistingDemolish, Theme.Blocked) })
        { var t = new DataTrigger { Binding = new Binding("Disposition"), Value = value }; t.Setters.Add(new Setter(TextBlock.ForegroundProperty, brush)); labelStyle.Triggers.Add(t); }
        label.SetValue(FrameworkElement.StyleProperty, labelStyle);
        chip.AppendChild(label);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "מצב", Width = 66, CellTemplate = new DataTemplate { VisualTree = chip } });
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
        badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        var badgeStyle = new Style(typeof(Border)); badgeStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        var showBadge = new DataTrigger { Binding = new Binding("IsRecommended"), Value = true };
        showBadge.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible)); badgeStyle.Triggers.Add(showBadge);
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
            var surfaces = RoadPavement.Surfaces(groups);
            InfrastructureBoq.Export(Path.Combine(directory, "Infrastructure-review.xlsx"), groups, scan.Measurements.Select(m => m.Line).ToArray(), new[] { catalog },
                scan.Findings, doc.Name, scan.DrawingHash, "AutoCAD native curve distance; original Igor audit is separate and limited to host LWPOLYLINE",
                new RoadPavement.Report(surfaces, RoadPavement.Structure(surfaces, RoadPavement.Defaults), Array.Empty<InfrastructureGroup>()));
            scan.SaveDrawing(Path.Combine(directory, "Infrastructure-lines.dwg"), groups);
            File.WriteAllText(Path.Combine(directory, "igor-audit.json"), JsonSerializer.Serialize(IgorPolylineAudit.Capture(doc.Database), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { scan.Drawing, scan.DrawingHash, Insunits = doc.Database.Insunits.ToString(), MetresPerUnit = factor,
                LineCount = scan.Measurements.Count, GroupCount = groups.Count, Groups = groups, Findings = scan.Findings,
                Existing = groups.Count(g => g.Disposition != InfrastructureDisposition.New), RoadSurfaces = surfaces }, new JsonSerializerOptions { WriteIndented = true }));
            doc.Editor.WriteMessage("\nInfrastructure draft (UNAPPROVED): " + directory + "\n");
        }
        catch (System.Exception ex) { File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); doc.Editor.WriteMessage("\n" + ex + "\n"); }
    }
}
