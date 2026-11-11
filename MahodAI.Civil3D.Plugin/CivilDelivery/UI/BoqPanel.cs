using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The bill of quantities inside the Civil Delivery window (owner 2026-11-11: "always opened, like we had before, but all in
/// one window: press and select a polygon, or press and do the whole drawing; while it generates show a loader and a
/// cancel button, so users won't guess what is happening"). Two buttons on top; under them the state — what to do, the
/// corners being marked, the loader with what it is reading and a Cancel, or the estimate (one total and what it is made
/// of, like the "Inspector" reel). Every layer and item is one click away in the "advanced" menu.
/// The work itself runs in AutoCAD's command context (<see cref="BoqPanelRunner"/>); this control only shows it.
/// </summary>
internal sealed class BoqPanel : UserControl
{
    internal event Action? MarkArea, WholeDrawing, Export;
    internal event Action<InfrastructureBoqWindow.Sheet>? Details;
    internal bool Busy { get; private set; }

    private const int Shown = 5;
    private static readonly Brush Navy = Frozen(new SolidColorBrush(Color.FromRgb(0x0F, 0x23, 0x3D)));
    private static readonly Brush Green = Frozen(new SolidColorBrush(Color.FromRgb(0x86, 0xE0, 0x5A)));
    private static readonly Brush Money = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E)));
    private static readonly Brush Bar = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x4A)));   // solid, no fade (owner)

    private readonly Button _area, _whole;
    private readonly StackPanel _state = new();
    private readonly Stopwatch _clock = new(), _sincePump = new();
    private TextBlock? _busyTitle, _busyCount, _busyTime;
    private Button? _cancelButton;
    private volatile bool _cancel;
    private InfrastructureBoqWindow.AreaEstimate? _last;
    private bool _lastIsArea;
    private TextBlock? _exportStatus;

    internal BoqPanel()
    {
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = Theme.Font; Background = Theme.Canvas;
        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        body.Children.Add(new TextBlock { Text = "כתב כמויות", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Theme.Ink });
        body.Children.Add(new TextBlock { Text = "מה לתמחר?", Foreground = Theme.Muted, Margin = new Thickness(0, 2, 0, 10) });
        var choose = new UniformGrid { Columns = 2 };
        _area = Action("סמן אזור בתכנית", "GoButton", "boq-panel-area");
        _area.ToolTip = "לוחצים על פינות האזור בתכנית, Enter סוגר. האומדן רק למה שבתוכו.";
        _area.Click += (_, _) => Later(() => MarkArea?.Invoke());
        _whole = Action("כל השרטוט", "PillButton", "boq-panel-whole");
        _whole.ToolTip = "אומדן לכל המודל והמקורות הטעונים.";
        _whole.Click += (_, _) => Later(() => WholeDrawing?.Invoke());
        _area.Margin = new Thickness(0, 0, 0, 0); _whole.Margin = new Thickness(8, 0, 0, 0);
        _area.MinHeight = _whole.MinHeight = 44;
        choose.Children.Add(_area); choose.Children.Add(_whole);
        body.Children.Add(choose);
        _state.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(_state);
        Content = body;   // the Civil Delivery window scrolls; a scroll area of our own kept the wheel from reaching it
        SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-panel");
        // The palette is inactive after any work on the plan, and AutoCAD spends the first press on an inactive palette
        // activating it: "I click and nothing happens" (live 2026-11-11). The mouse reaching the panel activates it first.
        MouseEnter += (_, _) => { if (!IsKeyboardFocusWithin) CivilDeliveryPalette.FocusPanel(); };
        ShowIdle();
    }

    // ───────────────────────────── states ─────────────────────────────

    internal void ShowIdle()
    {
        _state.Children.Clear();
        _state.Children.Add(Hint("סמנו אזור על התכנית — או תמחרו את כל השרטוט. בלי דו״ח מבנה מיסעה התמחור לפי ברירת המחדל של סוג הדרך."));
    }

    /// <summary>Back to the last estimate (after Esc while marking), else the hint.</summary>
    internal void ShowLast()
    {
        if (_last != null) ShowResult(_last, _lastIsArea); else ShowIdle();
    }

    internal void ShowPicking()
    {
        _state.Children.Clear();
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "סמנו את פינות האזור על התכנית", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
        stack.Children.Add(new TextBlock { Text = "לחיצה לכל פינה · Enter סוגר את האזור · Esc מבטל", Foreground = Theme.Muted, Margin = new Thickness(0, 4, 0, 0) });
        card.Child = stack;
        _state.Children.Add(card);
        Pump(force: true);
    }

    internal void ShowMessage(string text, bool error)
    {
        _state.Children.Clear();
        var t = Hint(text); t.Foreground = error ? Theme.Blocked : Theme.Muted;
        _state.Children.Add(t);
        if (_last != null)
        {
            var back = Action("חזרה לאומדן האחרון", "QuietButton", "boq-panel-last");
            back.HorizontalAlignment = HorizontalAlignment.Right;
            back.Click += (_, _) => ShowLast();
            _state.Children.Add(back);
        }
    }

    /// <summary>The loader: a spinner, what is being done, a running count, the time, and Cancel.</summary>
    internal void BeginBusy(string title)
    {
        Busy = true; _cancel = false;
        _area.IsEnabled = _whole.IsEnabled = false;
        _state.Children.Clear();
        var card = Card();
        var row = new DockPanel();
        var spinner = new System.Windows.Shapes.Ellipse
        {
            Width = 34, Height = 34, Stroke = Theme.Accent, StrokeThickness = 4, StrokeDashArray = new DoubleCollection { 12, 6 },
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var turn = new RotateTransform(); spinner.RenderTransform = turn;
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        DockPanel.SetDock(spinner, Dock.Left); row.Children.Add(spinner);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _busyTitle = new TextBlock { Text = title + "…", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink };
        _busyCount = new TextBlock { Foreground = Theme.Muted, Margin = new Thickness(0, 3, 0, 0) };
        _busyTime = new TextBlock { Foreground = Theme.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        _busyTitle.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-panel-busy");
        texts.Children.Add(_busyTitle); texts.Children.Add(_busyCount); texts.Children.Add(_busyTime);
        row.Children.Add(texts);
        var stack = new StackPanel();
        stack.Children.Add(row);
        _cancelButton = Action("בטל", "PillButton", "boq-panel-cancel");
        _cancelButton.HorizontalAlignment = HorizontalAlignment.Left; _cancelButton.Margin = new Thickness(0, 12, 0, 0); _cancelButton.MinWidth = 110;
        _cancelButton.Click += (_, _) => { _cancel = true; if (_busyTitle != null) _busyTitle.Text = "מבטל…"; if (_cancelButton != null) _cancelButton.IsEnabled = false; };
        stack.Children.Add(_cancelButton);
        card.Child = stack;
        _state.Children.Add(card);
        _clock.Restart();
        Pump(force: true);
    }

    internal void Step(string title)
    {
        if (_busyTitle != null && !_cancel) _busyTitle.Text = title + "…";
        if (_busyCount != null) _busyCount.Text = "";
        Pump(force: true);
    }

    /// <summary>Progress from the reading loops: the count, and a chance for the window to repaint and for Cancel to be pressed.</summary>
    internal void Tick(int count, string what)
    {
        if (_sincePump.ElapsedMilliseconds < 90) return;
        if (_busyCount != null) _busyCount.Text = $"{count:N0} {what}";
        Pump(force: false);
    }

    /// <summary>Waits for background work while keeping the window alive; Cancel abandons it.</summary>
    internal T Wait<T>(Task<T> work)
    {
        while (!work.IsCompleted) { Pump(force: false); Thread.Sleep(25); }
        return work.GetAwaiter().GetResult();
    }

    internal void EndBusy()
    {
        Busy = false;
        _area.IsEnabled = _whole.IsEnabled = true;
        _clock.Stop();
    }

    private void Pump(bool force)
    {
        if (!force && _sincePump.IsRunning && _sincePump.ElapsedMilliseconds < 90) return;
        if (_busyTime != null && _clock.IsRunning) _busyTime.Text = $"{_clock.Elapsed:m\\:ss}";
        // The work runs on AutoCAD's thread (it must: the drawing is read there); letting the messages through keeps the
        // loader turning and the Cancel button clickable.
        System.Windows.Forms.Application.DoEvents();
        _sincePump.Restart();
        if (_cancel && Busy) throw new OperationCanceledException();
    }

    // ───────────────────────────── the estimate ─────────────────────────────

    internal void ShowResult(InfrastructureBoqWindow.AreaEstimate e, bool area)
    {
        _last = e; _lastIsArea = area;
        _state.Children.Clear();
        _state.Children.Add(new TextBlock { Text = area ? "אומדן לאזור המסומן" : "אומדן לכל השרטוט", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Theme.Ink });
        var sub = new TextBlock { Text = area ? $"{e.Entities:N0} ישויות באזור · {e.AreaSquareMetres:N0} מ״ר" : $"{e.Entities:N0} ישויות בשרטוט",
            Foreground = Theme.Muted, Margin = new Thickness(0, 2, 0, 10) };
        sub.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-card-scope");
        _state.Children.Add(sub);

        // RightToLeft: Dock.Left / HorizontalAlignment.Right are the visual right / left.
        var band = new DockPanel();
        var label = new TextBlock { Text = "סה״כ", Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xD3, 0xE0))), FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left); band.Children.Add(label);
        var total = new TextBlock { Text = Shekels(e.Total), Foreground = Green, FontSize = 32, FontWeight = FontWeights.Bold,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        total.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-card-total");
        band.Children.Add(total);
        _state.Children.Add(new Border { Background = Navy, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 12, 18, 12), Child = band });
        if (e.Demolition > 0)
            _state.Children.Add(new TextBlock { Text = $"ועוד פירוקים: {Shekels(e.Demolition)}", Foreground = Theme.Muted, FontSize = 12, Margin = new Thickness(2, 6, 0, 0) });
        // What to do with it, right under the number (owner 2026-11-11: "how can I export this to Excel? where is the button" —
        // it sat under the item list, below the fold). The old tables are one menu away.
        _state.Children.Add(Actions());
        _exportStatus = new TextBlock { FontSize = 12, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        _state.Children.Add(_exportStatus);

        if (e.Lines.Count > 0)
        {
            _state.Children.Add(new TextBlock { Text = "ממה מורכב האומדן", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 16, 0, 6) });
            var top = e.Lines[0].Total;
            // Items that read alike once shortened (drainage pipes of five diameters) carry their price-list code.
            var alike = e.Lines.GroupBy(l => Short(l.Description)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            string Name(InfrastructureBoqWindow.EstimateLine l) => alike.Contains(Short(l.Description)) ? Short(l.Description, 30) + " · " + l.Code : Short(l.Description);
            foreach (var line in e.Lines.Take(Shown)) _state.Children.Add(Row(line, Name(line), top));
            if (e.Lines.Count > Shown)
            {
                var rest = new StackPanel();
                foreach (var line in e.Lines.Skip(Shown)) rest.Children.Add(Row(line, Name(line), top, compact: true));
                var more = new Expander { Header = $"ועוד {e.Lines.Count - Shown} סעיפים · {Shekels(e.Lines.Skip(Shown).Sum(l => l.Total))}", Content = rest, Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.TextSoft };
                more.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-card-more");
                _state.Children.Add(more);
            }
        }
        else _state.Children.Add(Hint("לא נמצאו שכבות עם סעיף ומחיר."));

        var notes = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        notes.Children.Add(Note((e.PavementIsDefault ? "⚠ " : "") + e.Pavement, e.PavementIsDefault ? Theme.Review : Theme.Muted,
            e.PavementIsDefault ? "מבנים טיפוסיים, לא תכנון הפרויקט — מסומנים \"ברירת מחדל\" בכל שורה ובייצוא. לצירוף דו״ח או לשינוי סוג הדרך: מתקדם › כבישים." : null));
        if (e.ConcreteNote != null) notes.Children.Add(Note("⚠ " + e.ConcreteNote, Theme.Review, "המידות: מתקדם › בטון וקונסטרוקציה."));
        if (e.Unpriced > 0) notes.Children.Add(Note($"{e.Unpriced} שכבות ללא מחיר — מתקדם › כל השכבות והסעיפים", Theme.Muted, null));
        _state.Children.Add(notes);

    }

    /// <summary>Export, and "advanced": the bill's own tables (every layer and its item, demolition, existing, roads with the
    /// pavement report, concrete), each opening the full bill-of-quantities window on that table.</summary>
    private UIElement Actions()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var export = Action("ייצא ל־Excel…", "GoButton", "boq-card-export");
        export.ToolTip = "כתב כמויות ב־Excel: עבודות חדשות, פירוקים, קיים, כבישים ובטון, ובדיקות המקור; בנוסף שרטוט ביקורת של הקווים שנמדדו.";
        export.MinWidth = 150;
        export.Click += (_, _) => Later(() => Export?.Invoke());
        row.Children.Add(export);
        var advanced = Action("מתקדם ▾", "PillButton", "boq-card-advanced");
        advanced.ToolTip = "הטבלאות המלאות של כתב הכמויות.";
        var menu = new ContextMenu { FlowDirection = System.Windows.FlowDirection.RightToLeft, PlacementTarget = advanced, Placement = PlacementMode.Bottom };
        foreach (var (text, table, id) in new (string, InfrastructureBoqWindow.Sheet, string)[]
        {
            ("כל השכבות והסעיפים — עבודות חדשות", InfrastructureBoqWindow.Sheet.New, "new"),
            ("פירוקים והריסות", InfrastructureBoqWindow.Sheet.Demolish, "demolish"),
            ("קיים — נשאר", InfrastructureBoqWindow.Sheet.Keep, "keep"),
            ("כבישים — שטח, נפח ודו״ח מבנה", InfrastructureBoqWindow.Sheet.Roads, "roads"),
            ("בטון וקונסטרוקציה", InfrastructureBoqWindow.Sheet.Concrete, "concrete"),
        })
        {
            var item = new MenuItem { Header = text };
            item.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-advanced-" + id);
            var chosen = table;
            item.Click += (_, _) => Later(() => Details?.Invoke(chosen));
            menu.Items.Add(item);
        }
        advanced.ContextMenu = menu;
        // Opened after the release: opened on the press, the release outside the new menu closed it at once.
        advanced.Click += (_, _) => Later(() => menu.IsOpen = true);
        row.Children.Add(advanced);
        return row;
    }

    /// <summary>The last estimate's read is gone (a new one started): no "back to it".</summary>
    internal void Forget() => _last = null;

    internal void ShowExported(string folder)
    {
        if (_exportStatus == null) return;
        _exportStatus.Text = "נשמר ונפתחה התיקייה: " + folder;
        _exportStatus.Visibility = Visibility.Visible;
    }

    // ───────────────────────────── building blocks ─────────────────────────────

    private static UIElement Row(InfrastructureBoqWindow.EstimateLine line, string name, decimal top, bool compact = false)
    {
        var row = new StackPanel { Margin = new Thickness(0, compact ? 3 : 6, 0, compact ? 3 : 6), ToolTip = $"{line.Code} · {line.Description}" };
        var head = new DockPanel();
        var price = new TextBlock { Text = Shekels(line.Total), Foreground = Money, FontWeight = FontWeights.SemiBold, FontSize = compact ? 13 : 15,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(price, Dock.Right); head.Children.Add(price);   // RightToLeft: Dock.Right is the visual left
        head.Children.Add(new TextBlock { Text = name, FontWeight = compact ? FontWeights.Normal : FontWeights.SemiBold, Foreground = Theme.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        row.Children.Add(head);
        if (compact) return row;
        var foot = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var quantity = new TextBlock { Text = $"{line.Quantity:N0} {line.Unit}", Foreground = Theme.Muted, FontSize = 11.5, Width = 92, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(quantity, Dock.Right); foot.Children.Add(quantity);
        var share = top > 0 ? (double)(line.Total / top) : 0;
        var track = new Grid { Height = 7, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.02), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
        var back = new Border { Background = Theme.Frame, CornerRadius = new CornerRadius(4) }; Grid.SetColumnSpan(back, 2); track.Children.Add(back);
        track.Children.Add(new Border { Background = Bar, CornerRadius = new CornerRadius(4) });
        foot.Children.Add(track);
        row.Children.Add(foot);
        return row;
    }

    /// <summary>The price-list text up to its first comma or <paramref name="max"/> characters: "תא״צ 19 בעובי 4 ס״מ", not the whole specification.</summary>
    private static string Short(string description, int max = 46)
    {
        var text = description.Trim();
        var cut = text.IndexOfAny(new[] { ',', ';', '(' });
        if (cut > 12) text = text[..cut].Trim();
        return text.Length > max ? text[..max].TrimEnd() + "…" : text;
    }

    private static string Shekels(decimal value) => "₪" + value.ToString("N0");
    private static Border Card() => new() { Background = Theme.Panel, BorderBrush = Theme.Frame, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14) };
    private static TextBlock Hint(string text) => new() { Text = text, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
    private static TextBlock Note(string text, Brush colour, string? tip) =>
        new() { Text = text, Foreground = colour, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };

    private static Button Action(string text, string style, string id)
    {
        // Press, not release: inside AutoCAD's palette the first click on an inactive palette moves focus during the press,
        // the button loses its mouse capture and a release-click never fired — "nothing happens" (live 2026-11-11).
        var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 8, 0), MinHeight = 36, ClickMode = ClickMode.Press };
        if (Theme.Dictionary[style] is Style s) button.Style = s;
        button.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, id);
        return button;
    }

    /// <summary>The buttons react on the press (see <see cref="Action"/>); what they start — a Save dialog, a modal window,
    /// picking on the plan — begins once the mouse is released, never under a held button.</summary>
    private void Later(Action work) => Dispatcher.BeginInvoke(work, System.Windows.Threading.DispatcherPriority.Background);

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
}
