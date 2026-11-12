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
internal sealed partial class InfrastructureBoqWindow : Window
{
    private enum View { New, Demolish, Keep, Roads, Concrete }
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
    private readonly TextBlock _source = Text(""), _viewTitle = Heading(""), _recoveredNote = Text("");
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

    internal InfrastructureBoqWindow(InfrastructureDrawingService scan, CatalogSnapshot catalog, IReadOnlyList<string>? pendingReports = null, string? defaultClass = null)
    {
        _scan = scan; _catalog = catalog; _groups = InfrastructureBoq.Group(scan.Measurements.Select(m => m.Line)).ToList();
        _books = new[] { new InfrastructurePriceBook("נתיבי ישראל", "אחוד יולי 2026 · מקור רשמי", catalog, new Dictionary<string,string>(), true) }
            .Concat(InfrastructurePriceBooks.Load(InfrastructurePriceBooks.LocalDirectory, scan.Findings)).ToArray();
        try { _usage = InfrastructureCatalogUsage.Load(Path.Combine(InfrastructurePriceBooks.LocalDirectory, "project-usage.json")); }
        catch (System.Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { _usage = new InfrastructureCatalogUsage(Array.Empty<InfrastructureCatalogUsage.Example>()); scan.Findings.Add("לא נטען ניסיון הפרויקטים: " + ex.Message); }
        _search = new InfrastructureCatalogSearch(_books[0], _usage);
        var reviews = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodCivilDelivery", "infrastructure-reviews");
        var wholeReview = Path.Combine(reviews, InfrastructureReviewStore.FileName(scan.Drawing));
        _roadsPath = Path.ChangeExtension(wholeReview, ".roads.json");
        // An area's own choices live beside the drawing's; the drawing's per-layer choices (item, state, topic) carry in.
        _reviewPath = scan.Area == null ? wholeReview : Path.ChangeExtension(wholeReview, ".area.json");
        _structures = RoadPavement.Load(_roadsPath);
        InitPavement(pendingReports, defaultClass);
        InitConcrete(wholeReview);
        string? restoreError = null;
        try
        {
            var catalogs = _books.Select(b => b.Catalog).ToArray();
            if (scan.Area != null) InfrastructureReviewStore.RestoreByLayer(wholeReview, scan.DrawingHash, catalogs, _groups, _excluded);
            InfrastructureReviewStore.Restore(_reviewPath, scan.DrawingHash, catalogs, _groups, _excluded);
            if (_reports.HasReport && _pendingReports == null)
            {
                try { ComputeZones(popups: false); }
                catch (System.Exception ex) when (UiGuard.IsRecoverable(ex)) { scan.Findings.Add("דו״ח מבנה: " + ex.Message); }
            }
            RebuildRoads(persist: false);
            if (scan.Area != null) InfrastructureReviewStore.RestoreByLayer(wholeReview, scan.DrawingHash, catalogs, _derived, _excluded);
            InfrastructureReviewStore.Restore(_reviewPath, scan.DrawingHash, catalogs, _derived, _excluded);
            if (scan.Area != null) InfrastructureReviewStore.RestoreByLayer(wholeReview, scan.DrawingHash, catalogs, _concreteDerived, _excluded);
            InfrastructureReviewStore.Restore(_reviewPath, scan.DrawingHash, catalogs, _concreteDerived, _excluded);
            if (RefreshStaleAutomatic(_groups.Concat(_derived)) is > 0 and var refreshed)
            { Persist(); restoreError = $"{refreshed} שיוכים אוטומטיים מגרסה קודמת עודכנו להצעה הנוכחית (שיוכים ידניים לא שונו)"; }
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
        if (AreaWarning() is { } areaWarning) root.Children.Add(areaWarning);
        root.Children.Add(_pavementBanner);
        var bottom = new DockPanel { Margin = new Thickness(2, 10, 2, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        _undo.Click += (_, _) => UiGuard.Run("החזרת שורות", () => { _excluded.Clear(); Persist(); RebuildRoads(); Refresh(); });
        DockPanel.SetDock(_undo, Dock.Left); bottom.Children.Add(_undo);
        _undoAuto.Visibility = Visibility.Collapsed; _undoAuto.Margin = new Thickness(8, 0, 0, 0);
        _undoAuto.Click += (_, _) => UiGuard.Run("ביטול שיוך אוטומטי", UndoAutomaticAssignment);
        DockPanel.SetDock(_undoAuto, Dock.Left); bottom.Children.Add(_undoAuto);
        _recoveredNote.FontSize = 12; _recoveredNote.Foreground = Theme.Muted; _recoveredNote.VerticalAlignment = VerticalAlignment.Center; _recoveredNote.Cursor = Cursors.Help;
        _recoveredNote.TextWrapping = TextWrapping.NoWrap; _recoveredNote.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(_recoveredNote, Dock.Right); bottom.Children.Add(_recoveredNote);
        _message.VerticalAlignment = VerticalAlignment.Center; bottom.Children.Add(_message);
        _boqBody = BoqBody(); _roadsBody = RoadsBody(); _concreteBody = ConcreteBody();
        _body.Margin = new Thickness(0, 12, 0, 0); root.Children.Add(_body);
        Closed += (_, _) => { _closed = true; _queryTimer.Stop(); };
        SetView(View.New);
        if (restoreError != null) _message.Text = restoreError;
    }

    /// <summary>
    /// An automatic assignment is the program's proposal, saved with the drawing. When the ranking improves, a saved
    /// proposal follows it; the engineer's own choices never change. Live 2026-11-06: 1.9.0 saved a drainage pipe on every
    /// curb layer, and reopening the drawing after the fix kept it, because auto-assign skips rows that already have an item.
    /// </summary>
    private int RefreshStaleAutomatic(IEnumerable<InfrastructureGroup> groups)
    {
        var book = _books[0].Catalog; var changed = 0;
        foreach (var g in groups.Where(g => g.Approved && g.AutomaticallyAssigned && g.CatalogHash == book.FileHash).ToArray())
        {
            var current = _search.Find(g).FirstOrDefault(o => o.IsRecommended)?.Code;
            if (current == g.Code) continue;
            if (current != null) g.Assign(book, current, automatic: true); else g.Withdraw();
            changed++;
        }
        return changed;
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
        // Like the "mark an area, get its estimate" flow engineers asked for (2026-11-09): the window steps aside, the
        // engineer draws the area on the plan, and the bill comes back for what is inside it only.
        var markArea = ActionButton(_scan.Area == null ? "כתב כמויות לאזור…" : "סמן אזור אחר…", "PillButton", "scan");
        markArea.ToolTip = "סמן פוליגון על השרטוט (נקודה אחר נקודה, או בחירת פוליליין סגור) — כתב הכמויות יחושב רק למה שבתוכו. " +
                           "קווים נחתכים בגבול, שטחים ונפחים לפי החלק שבתוכו, בלוקים לפי נקודת ההכנסה. השרטוט אינו משתנה.";
        markArea.Margin = new Thickness(8, 0, 0, 0);
        markArea.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-mark-area");
        markArea.Click += (_, _) => { NextStep = Step.MarkArea; Close(); };
        DockPanel.SetDock(markArea, Dock.Left); header.Children.Add(markArea);
        if (_scan.Area != null)
        {
            var whole = ActionButton("כל השרטוט", "QuietButton");
            whole.ToolTip = "חזרה לכתב הכמויות לכל המודל והמקורות הטעונים.";
            whole.Margin = new Thickness(8, 0, 0, 0);
            whole.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-whole-drawing");
            whole.Click += (_, _) => { NextStep = Step.WholeDrawing; Close(); };
            DockPanel.SetDock(whole, Dock.Left); header.Children.Add(whole);
        }
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = _scan.Area == null ? "כתב כמויות" : "כתב כמויות — אזור מסומן", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
        var file = Path.GetFileName(_scan.Drawing);
        // One short line; the rest is a tooltip (owner 2026-11-11: the windows carried too much text).
        var scope = _scan.Area == null ? "כל השרטוט" : $"אזור מסומן · {_scan.Area.Boundary.Area * _scan.MetresPerHostUnit * _scan.MetresPerHostUnit:N0} מ״ר";
        var subtitle = Text($"{file} · {scope}");
        subtitle.ToolTip = $"{_books.Count} מחירונים · {_scan.MetresPerHostUnit:G} מטר ליחידת שרטוט · " + (_scan.Area == null ? "כל המודל והמקורות הטעונים"
            : $"רק בתוך האזור: {_scan.Measurements.Count:N0} מדידות, {_scan.CutAtEdge:N0} נחתכו בגבול");
        subtitle.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-scope");
        titles.Children.Add(subtitle);
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    /// <summary>
    /// Hatches AutoCAD gives no area for, on the first screen. Before 2026-11-06 they were one handle-less line in the last
    /// Excel sheet, and ~59,000 m² of a real carriageway went missing without anyone seeing it.
    /// </summary>
    private UIElement? AreaWarning()
    {
        var recovered = _scan.RecoveredAreas; var missing = _scan.UnmeasuredAreas;
        if (recovered.Count == 0 && missing.Count == 0) return null;
        static string Layers(IEnumerable<InfrastructureDrawingService.AreaIssue> items) => string.Join(" · ", items
            .GroupBy(i => i.LayerName).OrderByDescending(g => g.Sum(i => i.AreaSquareMetres)).ThenByDescending(g => g.Count())
            .Select(g => g.Sum(i => i.AreaSquareMetres) > 0 ? $"{g.Key} ({g.Count()}, {g.Sum(i => i.AreaSquareMetres):N0} מ״ר)" : $"{g.Key} ({g.Count()})"));
        // Owner 2026-11-11: keep the details hidden by default. Hatches measured from their boundary ARE in the bill — that is
        // information, so it is one small line in the status bar (layers in its tooltip). Only hatches that could not be
        // measured at all (a real gap) get a banner, one line, its layer list closed under it.
        if (recovered.Count > 0)
        {
            _recoveredNote.Text = $"ⓘ {recovered.Count:N0} שטחי Hatch נמדדו מגבולותיהם ({recovered.Sum(r => r.AreaSquareMetres):N0} מ״ר)";
            _recoveredNote.ToolTip = "AutoCAD אינו מחשב להם שטח; השטח חושב מהגבול ונכלל בכמות. הרשימה המלאה בגיליון \"בדיקות ומקור המחירון\" של ה־Excel.\n" + Layers(recovered);
            _recoveredNote.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-hatch-recovered");
        }
        if (missing.Count == 0) return null;
        var stack = new StackPanel();
        stack.Children.Add(Details("hatch-missing", $"⚠ {missing.Count} שטחי Hatch לא נמדדו — יש להשלים כמות ידנית", Text(Layers(missing)), Theme.Blocked));
        var banner = new Border { Child = new ScrollViewer { Content = stack, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Background = Theme.BlockedTint, BorderBrush = Theme.Blocked, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "הרשימה המלאה (שכבה, Handle, שטח) בגיליון \"בדיקות ומקור המחירון\" של קובץ ה־Excel." };
        banner.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-area-warning");
        DockPanel.SetDock(banner, Dock.Top);
        return banner;
    }

    private UIElement TilesRow()
    {
        var row = new UniformGrid { Columns = 5, Margin = new Thickness(-5, 10, -5, 0) };
        row.Children.Add(Tile(View.New, "עבודות חדשות", Theme.Accent));
        row.Children.Add(Tile(View.Demolish, "פירוקים והריסות", Theme.Blocked));
        row.Children.Add(Tile(View.Keep, "קיים — נשאר", Theme.Muted));
        row.Children.Add(Tile(View.Roads, "כבישים — שטח ונפח", Theme.Ink));
        row.Children.Add(Tile(View.Concrete, "בטון וקונסטרוקציה", Theme.Review));
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
        // The view's explanation and the right-click hint live in the title's tooltip, not as two lines of text.
        _viewTitle.Cursor = Cursors.Help; layerHead.Children.Add(_viewTitle);
        var filters = new DockPanel { Margin = new Thickness(0, 2, 0, 4) }; layerHead.Children.Add(filters);
        _filter.DisplayMemberPath = "Label"; _filter.MaxDropDownHeight = 440; _filter.MaxWidth = 260;
        _filter.ToolTip = "תחום מהמחירון — מצמצם את הסעיפים המוצעים בלבד.";
        _filter.ItemsSource = BoqCatalogCategories.For(_books[0]); _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => { if (!_loading) Search(); }; DockPanel.SetDock(_filter, Dock.Right); filters.Children.Add(_filter);
        _layerSearch.ToolTip = "סינון לפי שם שכבה"; _layerSearch.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-layer-search"); _layerSearch.TextChanged += (_, _) => Refresh(); filters.Children.Add(_layerSearch);
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
        layers.Children.Add(_grid);
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
        // How the layer was read (evidence, the layer note) sits with its source, closed: it was two orange lines on every layer.
        _note.Foreground = Theme.Review; _note.FontSize = 12;
        var sourceBody = new StackPanel(); sourceBody.Children.Add(_note); sourceBody.Children.Add(_source); sourceBody.Children.Add(_topic);
        _topic.SelectionChanged += (_, _) => { if (!_loading && _grid.SelectedItem is InfrastructureGroup g) { g.Topic = _topic.SelectedItem as string ?? InfrastructureBoq.Unknown; Persist(); _grid.Items.Refresh(); Search(); } };
        editHead.Children.Add(new Expander { Header = "פרטי השכבה ושינוי נושא", Content = new ScrollViewer { Content = sourceBody, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 2, 0, 0) });

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
        // Code, price and the description's first lines; why it is recommended and the price book are in the tooltip.
        _detail.FontSize = 12; _detail.TextTrimming = TextTrimming.CharacterEllipsis; _detail.MaxHeight = 52; _detail.VerticalAlignment = VerticalAlignment.Center;
        choice.Children.Add(_detail);
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
        var areasTitle = Heading("1  שטחים בשרטוט"); areasTitle.Cursor = Cursors.Help;
        areasTitle.ToolTip = "שכבות עם Hatch או גבול סגור. סוג המשטח מזוהה משם השכבה; לשינוי — בחר שכבה ובחר סוג.";
        areaHead.Children.Add(areasTitle);
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
        // The per-surface totals repeat the areas table on the right: closed, so the layers table keeps its height.
        Column(_surfaceGrid, "משטח", "SurfaceName", 120); Column(_surfaceGrid, "מצב", "DispositionLabel", 110);
        Column(_surfaceGrid, "שטח מ״ר", "AreaSquareMetres", 100, "N2"); Column(_surfaceGrid, "שכבות", "LayersDisplay", new DataGridLength(1, DataGridLengthUnitType.Star), ltr: true);
        _surfaceGrid.MaxHeight = 190; _surfaceGrid.RowHeight = 32;
        var totals = Details("roads-surfaces", "סיכום לפי סוג משטח", _surfaceGrid, Theme.Ink);
        totals.Margin = new Thickness(0, 8, 0, 0); DockPanel.SetDock(totals, Dock.Bottom);
        areas.Children.Add(totals);
        areas.Children.Add(_areaGrid);
        body.Children.Add(Theme.Card(areas, 14));

        // Structure, results, corridors
        // Report → results → (closed) hand-typed structure and corridors: the screen opens on what was calculated, and the
        // rarely used tools stay one click away (owner 2026-11-11: too much text, details hidden by default).
        var right = new StackPanel();
        right.Children.Add(ReportCard());
        var structure = new StackPanel();
        var manualHint = Text("נפח = שטח × עובי. אספלט משולם במ״ר, מצעים במ״ק. מבנה שמוזן כאן חל על שטחי המשטח שאין להם מבנה בדו״ח, במקום ערכי ברירת המחדל (אחריות המהנדס).");
        manualHint.FontSize = 11.5; structure.Children.Add(manualHint);
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
        var reset = ActionButton("נקה", "QuietButton"); reset.Margin = new Thickness(6, 0, 0, 0);
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
        var results = new StackPanel(); right.Children.Add(results);
        results.Children.Add(Heading("כמויות מבנה — עבודה חדשה", 14));
        Column(_resultGrid, "משטח", "SurfaceName", 85); Column(_resultGrid, "מבנה", "StructureName", 130); Column(_resultGrid, "שכבה", "LayerDisplay", new DataGridLength(1, DataGridLengthUnitType.Star));
        Column(_resultGrid, "שטח מ״ר", "AreaSquareMetres", 90, "N1"); Column(_resultGrid, "נפח מ״ק", "VolumeCubicMetres", 85, "N1");
        Column(_resultGrid, "לתשלום", "Quantity", 90, "N1"); Column(_resultGrid, "יחידה", "Unit", 50);
        _resultGrid.RowHeight = 34; PassWheelToPage(_resultGrid);
        _resultGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-layers");
        results.Children.Add(_resultGrid);
        _roadsSummary.FontWeight = FontWeights.SemiBold; results.Children.Add(_roadsSummary);
        var manual = Details("roads-manual", "מבנה ידני — למשטח שאין לו מבנה בדו״ח", structure, Theme.Ink, open: _structures.Any(s => s.Layers.Count > 0));
        manual.Margin = new Thickness(0, 10, 0, 0); right.Children.Add(manual);

        var corridor = new StackPanel();
        var count = RoadCorridorVolumes.CorridorCount(AcadApp.DocumentManager.MdiActiveDocument);
        _corridorStatus.Text = count > 0
            ? $"בשרטוט {count} קורידורים. נפח לכל קוד מבנה (Pave, Base, SubBase…) לפי ממוצע שטחי קצה לאורך התחנות, וחפירה/מילוי מחתכי Sample Lines."
            : "אין קורידורים בשרטוט — החישוב לעיל מבוסס על שטחי התכנית.";
        corridor.Children.Add(_corridorStatus);
        if (_scan.Area != null)
        {
            // Corridor volumes come from Civil 3D's own stations, not from plan geometry: they are not cut by an area.
            _corridorStatus.Text = "בכתב כמויות לאזור, נפחי קורידורים אינם נחתכים לפי האזור ולכן אינם נקראים — קרא אותם בכתב הכמויות לכל השרטוט.";
            count = 0;
        }
        _readCorridors.IsEnabled = count > 0; _readCorridors.HorizontalAlignment = HorizontalAlignment.Left; _readCorridors.Margin = new Thickness(0, 4, 0, 6);
        _readCorridors.Click += (_, _) => UiGuard.Run("נפחים מקורידורים", ReadCorridors);
        corridor.Children.Add(_readCorridors);
        Column(_corridorGrid, "קוד / חומר", "ShortLayer", 150, ltr: true); Column(_corridorGrid, "נפח מ״ק", "VolumeCubicMetres", 100, "N2");
        Column(_corridorGrid, "שיטה", "Evidence", new DataGridLength(1, DataGridLengthUnitType.Star));
        _corridorGrid.RowHeight = 34; _corridorGrid.MaxHeight = 170; _corridorGrid.Visibility = Visibility.Collapsed;
        corridor.Children.Add(_corridorGrid);
        _readCorridors.ToolTip = "כלי Mahod 3D לאותה מטרה: Corridor ‹ Calculate Asfalts by Sections (Cr_CalcAsfaltsBySection) — שכבות אספלט וקרצוף לפי חתכים; " +
            "Calculate Structure Areas (Cr_CalcStructureAreas) — שטחי מבנה בשכבות CS_<קוד>, שנקראות כאן אוטומטית כ\"שטח מבנה\".";
        var corridors = Details("roads-corridors", count > 0 ? $"קורידורים ב־Civil 3D ({count})" : "קורידורים ב־Civil 3D", corridor, Theme.Ink);
        corridors.Margin = new Thickness(0, 6, 0, 0); right.Children.Add(corridors);
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
        else if (view == View.Concrete)
        {
            _body.Content = _concreteBody;
            RefreshConcrete();
        }
        else
        {
            _body.Content = _boqBody;
            string hint;
            (_viewTitle.Text, hint) = view switch
            {
                View.Demolish => ("פירוקים והריסות", "תשתיות קיימות שסומנו לפירוק. הסעיפים מוצעים מפרק הפירוקים (51.01) לפי סוג האלמנט. בייצוא: גיליון נפרד וקובץ פירוקים נפרד."),
                View.Keep => ("קיים — נשאר", "ASMADE, ‎-EX, KAYAM וקבצי מדידה. לא מתומחרים. אם אלמנט נהרס — סמן \"קיים — לפירוק\"."),
                _ => ("עבודות חדשות", "שכבות התכנון ושכבות מבנה המיסעה. שייך אוטומטית, או בחר שכבה וסעיף."),
            };
            _viewTitle.ToolTip = hint + "\nקליק ימני על שורה: מצב (חדש / נשאר / לפירוק) או הסרה. Delete מסיר את השורה.";
            Refresh();
        }
        UpdateSummary();
    }

    private IEnumerable<InfrastructureGroup> Active => _groups.Where(g => !_excluded.Contains(g));
    private static bool PayItem(InfrastructureGroup g) => g.IsPayItem;

    private void Refresh()
    {
        if (_view == View.Roads) { RefreshRoads(); return; }
        if (_view == View.Concrete) { RefreshConcrete(); return; }
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
        // Where the structure comes from, in a few words; the counts are in the tooltip.
        _tiles[View.Roads].Detail.Text = _reports.HasReport ? "לפי דו״ח מבנה" + (_plan?.DefaultArea > 0 ? " + ברירת מחדל" : "")
            : _plan?.DefaultArea > 0 && _defaultClass != null ? "⚠ ברירת מחדל · " + _defaultClass.Name
            : _derived.Count > 0 ? "מבנה ידני" : "ללא מבנה — לא תומחר";
        _tiles[View.Roads].Tile.ToolTip = $"{_derived.Count} שכבות מבנה · {surfaces.Count(s => s.Disposition == InfrastructureDisposition.New)} סוגי משטח" +
            (_corridor.Count > 0 ? $" · {_corridor.Count} קודי קורידור" : "");
        var concrete = _concretePlan;
        var concreteTotal = _concreteDerived.Where(g => !_excluded.Contains(g)).Sum(g => g.Total ?? 0m);
        _tiles[View.Concrete].Value.Text = concrete.Elements.Count == 0 ? "—" : concreteTotal > 0 ? $"₪ {concreteTotal:N0}" : $"{concrete.ConcreteM3:N0} מ״ק";
        _tiles[View.Concrete].Detail.Text = concrete.Elements.Count == 0 ? "לא זוהו אלמנטי בטון"
            : $"{concrete.Elements.Count} אלמנטים" + (concrete.Defaults > 0 ? " · ⚠ ברירת מחדל" : "") + (concrete.Unpriced > 0 ? " · חסרות מידות" : "");
        _tiles[View.Concrete].Tile.ToolTip = concrete.Elements.Count == 0 ? null : $"בטון {concrete.ConcreteM3:N1} מ״ק · זיון {concrete.RebarTon:N1} טון";
        _message.Text = $"{active.Length} שכבות · {active.Count(g => g.Approved)} שויכו" + (_excluded.Count > 0 ? $" · {_excluded.Count} הוסרו" : "");
        _message.ToolTip = "השיוכים נשמרים אוטומטית במחשב זה";
        _autoAssign.IsEnabled = !_automaticBusy && _view != View.Roads && _view != View.Concrete && _grid.Items.OfType<InfrastructureGroup>().Any(g => !g.Approved && double.IsFinite(g.Quantity) && g.Quantity > 0 && PayItem(g) && g.Disposition != InfrastructureDisposition.ExistingKeep);
    }

    // ───────────────────────────── layer editing ─────────────────────────────

    private void SetDisposition(InfrastructureDisposition d)
    {
        if (_grid.SelectedItem is not InfrastructureGroup g || g.Disposition == d) return;
        var wasApproved = g.Approved;
        g.Disposition = d; Persist();
        RebuildRoads();
        var to = d switch { InfrastructureDisposition.ExistingDemolish => "פירוקים והריסות", InfrastructureDisposition.ExistingKeep => "קיים — נשאר", _ => "עבודות חדשות" };
        Refresh();
        _message.Text = $"{g.ShortLayer} עבר ל\"{to}\"" + (wasApproved ? " — השיוך הקודם בוטל, יש לבחור סעיף מתאים" : "");
    }

    private void Remove(InfrastructureGroup group) { _excluded.Add(group); Persist(); RebuildRoads(); Refresh(); }
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
                // Only when something was detected or changed: "no sign of existing — new work" on every layer was noise.
                _dispositionEvidence.Text = g.ConditionEvidence.Length > 0
                    ? "זוהה אוטומטית: " + g.ConditionEvidence + (g.DispositionChangedByEngineer ? " · שונה על ידך" : "")
                    : g.DispositionChangedByEngineer ? "שונה על ידך" : "";
                _dispositionEvidence.Visibility = _dispositionEvidence.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _dispositionEvidence.Foreground = g.Disposition == InfrastructureDisposition.ExistingDemolish ? Theme.Blocked : Theme.Muted;
                _catalogPanel.IsEnabled = g.Disposition != InfrastructureDisposition.ExistingKeep;
                _items.IsEnabled = _catalogPanel.IsEnabled;
                _dNew.IsEnabled = _dKeep.IsEnabled = _dDemolish.IsEnabled = g.Source != RoadPavement.DerivedSource && g.Source != StructureConcrete.DerivedSource && g.Source != Earthworks.DerivedSource && g.GroupPart != RoadCorridorVolumes.Part;
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
        // A line only when it says something: no results, or how many a typed search found. The green row is self-explaining.
        _results.Text = options.Count == 0 ? "לא נמצאו סעיפים. נסה מילים אחרות או מספר סעיף."
            : _catalogSearch.Text.Trim().Length > 0 ? $"{options.Count}{(options.Count == 100 ? "+" : "")} תוצאות" : "";
        _results.Visibility = _results.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _items.ToolTip = options.Any(o => o.IsRecommended) ? "הסעיף המומלץ לשכבה מסומן בירוק" : null;
        SelectItem();
    }
    private void SelectItem()
    {
        _assign.IsEnabled = _grid.SelectedItem is InfrastructureGroup { Quantity: > 0 } && _items.SelectedItem is InfrastructureCatalogOption;
        _detail.Text = _items.SelectedItem is InfrastructureCatalogOption item
            ? $"{(item.IsRecommended ? "מומלץ · " : "")}{item.Code} · {item.PriceDisplay} ₪ ל{item.Unit}\n{item.Description}"
            : _grid.SelectedItem is InfrastructureGroup { Code: not null } g ? $"שויך לסעיף {g.Code}: {g.Description}" : "בחר סעיף ושייך אותו לשכבה.";
        _detail.ToolTip = _items.SelectedItem is InfrastructureCatalogOption chosen
            ? $"{chosen.Code} · {chosen.PriceDisplay} ₪ ל{chosen.Unit}\n{chosen.Description}\n{chosen.Reason}\n{_catalog.SnapshotId}" : null;
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
        var old = _derived.GroupBy(g => g.Layer).ToDictionary(g => g.Key, g => g.First());
        _groups.RemoveAll(g => g.Source == RoadPavement.DerivedSource);
        if (ZonesStale() && !_readingReport) ComputeZones(popups: false);
        _plan = PlanFromReport();
        var layers = _plan.Layers;
        // The structure rows price the area; the area row itself must not be priced a second time. A surface without any
        // structure (no report, no hand-typed one, defaults switched off) is not priced at all.
        RoadPavement.MarkPricedByStructure(_groups, _plan.Priced);
        _derived = RoadPavement.DerivedGroups(layers).GroupBy(g => g.Layer).Select(g => g.First()).ToList();
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
        RebuildConcrete();
        if (persist) Persist();
        UpdatePavementUi();
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
        var layers = _plan?.Layers ?? Array.Empty<RoadPavement.LayerQuantity>();
        _resultGrid.ItemsSource = layers;
        var asphalt = layers.Where(l => !l.Layer.ByVolume).Sum(l => l.AreaSquareMetres);
        var volume = layers.Where(l => l.Layer.ByVolume).Sum(l => l.VolumeCubicMetres);
        var newArea = surfaces.Where(s => s.Disposition == InfrastructureDisposition.New).Sum(s => s.AreaSquareMetres);
        var asphaltVolume = layers.Where(l => !l.Layer.ByVolume && l.Layer.ThicknessCm > 0).Sum(l => l.VolumeCubicMetres);
        var unpriced = _plan?.UnpricedArea ?? 0;
        var byDefault = _plan?.DefaultArea ?? 0;
        _roadsSummary.Text = newArea <= 0 ? "לא נמצאו שטחי משטח חדשים — סמן סוג משטח לשכבת Hatch משמאל."
            : layers.Count == 0 ? $"שטח חדש {newArea:N0} מ״ר — ללא מבנה, לא תומחר. צרף דו״ח או בחר סוג דרך לברירת מחדל."
            : $"שטח חדש {newArea:N0} מ״ר · אספלט וריצוף {asphaltVolume:N0} מ״ק · מצעים {volume:N0} מ״ק" +
              (byDefault > 0 ? $" · ⚠ {byDefault:N0} מ״ר ברירת מחדל" : "") +
              (unpriced > 0 ? $" · {unpriced:N0} מ״ר לא תומחרו" : "");
        _roadsSummary.ToolTip = layers.Count > 0 ? $"{_derived.Count} שורות בכתב הכמויות, תחת \"עבודות חדשות\". שטח {newArea:N2} מ״ר · אספלט {asphaltVolume:N1} מ״ק · מצעים וחול {volume:N1} מ״ק" : null;
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
            // Cut and fill from the corridor's sections replace the topographic estimate of the same works: never both.
            var sectionsEarth = _corridor.Any(g => g.Layer.StartsWith("earthworks:", StringComparison.OrdinalIgnoreCase));
            if (sectionsEarth) foreach (var e in _earthDerived) _excluded.Add(e);
            foreach (var f in findings) if (!_scan.Findings.Contains(f)) _scan.Findings.Add(f);
            _corridorGrid.ItemsSource = _corridor; _corridorGrid.Visibility = _corridor.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Persist(); RefreshRoads();
            _message.Text = _corridor.Count > 0
                ? $"נקראו {_corridor.Count} קודי נפח מקורידורים ({_corridor.Sum(g => g.VolumeCubicMetres):N1} מ״ק) — נוספו ל\"עבודות חדשות\" לשיוך סעיף" +
                  (sectionsEarth && _earthDerived.Count > 0 ? " · חפירה ומילוי מהחתכים מחליפים את חישוב העפר הטופוגרפי (הוסר מהאומדן)" : "")
                : "לא נמצאו נפחי מבנה בקורידורים (אין Shapes עם קודים)" + (findings.Count > 0 ? " · " + findings[0] : "");
        }
        finally { Cursor = previous; }
    }

    // ───────────────────────────── export ─────────────────────────────

    private void Export() => ExportBoq(this);

    /// <summary>The Excel, the demolition file and the audit drawing; the folder, or null when the engineer cancelled.
    /// <paramref name="owner"/> is the window on screen (the area card shows this window's bill without showing the window).</summary>
    /// <summary>
    /// The Save dialog, owned by the window on screen — or, from the Civil Delivery panel (no WPF window of ours), by AutoCAD's
    /// main window. Ownerless, it opened BEHIND AutoCAD and the export looked dead (live 2026-11-11).
    /// </summary>
    private static string? AskWhereToSave(Window? owner, string name)
    {
        if (owner != null)
        {
            var wpf = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = name, AddExtension = true };
            return wpf.ShowDialog(owner) == true ? wpf.FileName : null;
        }
        using var dialog = new System.Windows.Forms.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = name, AddExtension = true };
        return dialog.ShowDialog(new HostWindow(AcadApp.MainWindow.Handle)) == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>AutoCAD's main window as a dialog owner — a handle only, nothing subclassed.</summary>
    private sealed class HostWindow : System.Windows.Forms.IWin32Window
    {
        internal HostWindow(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    internal string? ExportBoq(Window? owner)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את מסך התשתיות בשרטוט הנכון");
        var file = AskWhereToSave(owner, _scan.Area == null ? "כתב כמויות" : "כתב כמויות - אזור");
        if (file == null) return null;
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
            "Concrete rows = plan length/area/count × the element's section (typed by the engineer, read from the drawing, or a TYPICAL default marked on every row); piles in metres by NTI diameter and depth band, rebar = concrete m³ × kg/m³. " +
            "Road structure rows = plan area × layer thickness × identical layers; the structure comes from the attached pavement report (read by MahodAI, verified in code) per segment located on the centre lines, else typed in by the engineer, else the DEFAULT structure of the chosen road type (marked as a default on every row); corridor rows = Civil 3D shape areas, average end area." +
            (_scan.Area == null ? "" : " AREA ONLY: every quantity is cut by the marked polygon (host XY; lines by length inside, " +
                "hatches/outlines by area inside, solids by the polygon prism, blocks by insertion point); the polygon is layer " +
                InfrastructureDrawingService.AreaLayer + " in the audit DWG.");
        // Each export gets a new directory: never overwrite an existing drawing or its trace.
        var directory = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var excel = Path.Combine(directory, Path.GetFileName(file));
        var surfaces = RoadPavement.Surfaces(_groups, _excluded);
        var roads = new RoadPavement.Report(surfaces, _plan?.Layers ?? Array.Empty<RoadPavement.LayerQuantity>(),
            _corridor.Where(g => !_excluded.Contains(g)).ToArray())
        { Zones = _plan?.Zones ?? Array.Empty<PavementReportPlan.ZoneRow>(), Notes = PavementNotes() };
        var catalogs = _books.Select(b => b.Catalog).ToArray();
        InfrastructureBoq.Export(excel, selected, lines, catalogs, _scan.Findings.Concat(PavementProvenance()).Concat(_concretePlan.Notes).ToArray(), _scan.Drawing, _scan.DrawingHash, engine, roads,
            _concretePlan);
        var demolition = selected.Count(g => g.Disposition == InfrastructureDisposition.ExistingDemolish);
        if (demolition > 0)
            InfrastructureBoq.ExportDemolition(Path.Combine(directory, Path.GetFileNameWithoutExtension(file) + " - פירוקים.xlsx"),
                selected, catalogs, _scan.Findings, _scan.Drawing, _scan.DrawingHash, engine);
        var notCopied = _scan.SaveDrawing(Path.Combine(directory, "Infrastructure-lines.dwg"), selected.Where(g => g.Source != RoadPavement.DerivedSource && g.Source != StructureConcrete.DerivedSource && g.Source != Earthworks.DerivedSource && g.GroupPart != RoadCorridorVolumes.Part));
        File.WriteAllText(Path.Combine(directory, "review.json"), JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "igor-audit.json"), JsonSerializer.Serialize(_igorAudit ??
            new IgorPolylineAudit.Result("not-run", "", "Model Space LWPOLYLINE only", Array.Empty<IgorPolylineAudit.Comparison>(), "Run the Igor comparison before relying on it"), new JsonSerializerOptions { WriteIndented = true }));
        _message.Text = "נשמרו בתיקייה: " + directory + (demolition > 0 ? " · כולל קובץ פירוקים נפרד" : "") +
            (notCopied > 0 ? $" · {notCopied} עצמים לא הועתקו לשרטוט הביקורת (רשימה ב־not-copied.txt; הכמויות ב־Excel שלמות)" : "");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = directory, UseShellExecute = true }); } catch (System.Exception) { }
        return directory;
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
    /// <summary>Which closed sections the engineer opened: the report card is rebuilt on every change and must not slam them shut.</summary>
    private readonly Dictionary<string, bool> _openSections = new();
    /// <summary>A one-line header with its details closed under it (owner 2026-11-11: details hidden by default).</summary>
    private Expander Details(string key, string header, UIElement content, Brush? headerColour = null, bool open = false)
    {
        var expander = new Expander
        {
            Header = new TextBlock { Text = header, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold, Foreground = headerColour ?? Theme.Ink, ToolTip = header },
            Content = content, IsExpanded = _openSections.TryGetValue(key, out var was) ? was : open, Margin = new Thickness(0, 2, 0, 2),
        };
        expander.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-details-" + key);
        expander.Expanded += (_, _) => _openSections[key] = true;
        expander.Collapsed += (_, _) => _openSections[key] = false;
        return expander;
    }
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
    /// <summary>A table inside a scrolling card shows all its rows and lets the mouse wheel scroll the card: a DataGrid
    /// swallows the wheel even when it has nothing to scroll (the roads screen could not be scrolled past its tables).</summary>
    internal static void PassWheelToPage(DataGrid grid)
    {
        grid.PreviewMouseWheel += (sender, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            var forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender };
            ((sender as FrameworkElement)?.Parent as UIElement)?.RaiseEvent(forward);
        };
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

    internal enum Step { Close, MarkArea, WholeDrawing }
    /// <summary>The tables of this window, for the BOQ panel's "advanced" menu (same order as <see cref="View"/>).</summary>
    internal enum Sheet { New, Demolish, Keep, Roads, Concrete }
    internal void Open(Sheet sheet) => SetView((View)(int)sheet);
    /// <summary>What the engineer asked for when the window closed: the command loop marks an area or returns to the whole drawing.</summary>
    internal Step NextStep { get; private set; } = Step.Close;

    /// <summary>MHD_BOQ: the whole drawing's estimate in the Civil Delivery window's BOQ tab (owner 2026-11-11: one window,
    /// always open — mark a polygon or take the whole drawing there; this window is "full details").</summary>
    internal static void Run() => BoqPanelRunner.FromCommand(area: false);
    /// <summary>MHD_BOQ_AREA: mark the area on the plan, then its estimate, in the same tab.</summary>
    internal static void RunArea() => BoqPanelRunner.FromCommand(area: true);

    /// <summary>The price book every bill uses.</summary>
    internal static CatalogSnapshot LoadCatalog()
    {
        var path = BundledPriceBooks.Offers(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodAI_Civil3D", "civil-delivery", "price-books"),
            new MahodAI.CivilDelivery.Shared.ProjectProfile()).Single().Entry.Path;
        return PriceBookXlsxLoader.Load(path, "נתיבי ישראל — אחוד יולי 2026");
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
