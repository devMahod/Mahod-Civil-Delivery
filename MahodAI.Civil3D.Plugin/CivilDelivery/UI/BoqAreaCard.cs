using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// What a marked area costs, at a glance (owner 2026-11-11, after an "Inspector" reel: "simple, without a lot of info").
/// One number, how many objects it counts, the items it is made of with a bar each, and one line on where the pavement
/// structure came from. Everything else — every layer, its item, the report — is one click away in "full details".
/// The card sits at the side of the screen so the marked area stays visible on the plan.
/// </summary>
internal sealed class BoqAreaCard : Window
{
    internal enum Choice { Close, Details, MarkAnother }
    internal Choice Result { get; private set; } = Choice.Close;

    private const int Shown = 5;
    private static readonly Brush Navy = Frozen(new SolidColorBrush(Color.FromRgb(0x0F, 0x23, 0x3D)));
    private static readonly Brush Green = Frozen(new SolidColorBrush(Color.FromRgb(0x86, 0xE0, 0x5A)));
    private static readonly Brush Money = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E)));
    // Solid green, no fade (owner 2026-11-11).
    private static readonly Brush Bar = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x4A)));

    private readonly StackPanel _body = new(), _actions = new();
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Func<Window, string?> _export;

    internal BoqAreaCard(Func<Task<InfrastructureBoqWindow.AreaEstimate>> estimate, Func<Window, string?> export)
    {
        _export = export;
        Title = "Mahod Delivery — אומדן לאזור";
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        Theme.Apply(this, 13);
        Background = Theme.Canvas; Foreground = Theme.Ink;
        // Beside the plan, not over the area just marked.
        WindowStartupLocation = WindowStartupLocation.Manual;
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 24; Top = work.Top + 64;
        MaxHeight = work.Height - 80;
        // The buttons stay on screen whatever the height; only the list scrolls.
        var root = new DockPanel();
        _actions.Margin = new Thickness(22, 4, 22, 16); DockPanel.SetDock(_actions, Dock.Bottom); root.Children.Add(_actions);
        _body.Margin = new Thickness(22, 18, 22, 4);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        UiGuard.Attach(this, "אומדן לאזור");
        Heading();
        _body.Children.Add(new TextBlock { Text = "מתמחר את האזור…", Foreground = Theme.Muted, Margin = new Thickness(0, 18, 0, 18) });
        Loaded += async (_, _) =>
        {
            try { Fill(await estimate()); }
            catch (System.Exception ex) when (UiGuard.IsRecoverable(ex)) { UiGuard.Report("אומדן לאזור", ex); }
        };
    }

    private void Heading()
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = "אומדן לאזור המסומן", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Theme.Ink });
    }

    private void Fill(InfrastructureBoqWindow.AreaEstimate e)
    {
        Heading();
        var sub = new TextBlock { Text = $"{e.Entities:N0} ישויות באזור · {e.AreaSquareMetres:N0} מ״ר", Foreground = Theme.Muted, Margin = new Thickness(0, 2, 0, 12) };
        sub.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-card-scope");
        _body.Children.Add(sub);

        // The number.
        var band = new DockPanel();
        var label = new TextBlock { Text = "סה״כ", Foreground = Brushes(0xC8, 0xD3, 0xE0), FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        // Right-to-left: Dock.Left / HorizontalAlignment.Right are the visual right / left — "סה״כ" on the right, the number on the left.
        DockPanel.SetDock(label, Dock.Left); band.Children.Add(label);
        var total = new TextBlock { Text = Shekels(e.Total), Foreground = Green, FontSize = 34, FontWeight = FontWeights.Bold,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        total.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-card-total");
        band.Children.Add(total);
        _body.Children.Add(new Border { Background = Navy, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 12, 18, 12), Child = band });
        if (e.Demolition > 0)
            _body.Children.Add(new TextBlock { Text = $"ועוד פירוקים: {Shekels(e.Demolition)}", Foreground = Theme.Muted, FontSize = 12, Margin = new Thickness(2, 6, 0, 0) });

        // What it is made of.
        if (e.Lines.Count > 0)
        {
            _body.Children.Add(new TextBlock { Text = "ממה מורכב האומדן", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 16, 0, 6) });
            var top = e.Lines[0].Total;
            // Items that read alike once shortened (drainage pipes of five diameters) carry their price-list code.
            var alike = e.Lines.GroupBy(l => Short(l.Description)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            string Name(InfrastructureBoqWindow.EstimateLine l) => alike.Contains(Short(l.Description)) ? Short(l.Description, 30) + " · " + l.Code : Short(l.Description);
            foreach (var line in e.Lines.Take(Shown)) _body.Children.Add(Row(line, Name(line), top));
            if (e.Lines.Count > Shown)
            {
                var rest = new StackPanel();
                foreach (var line in e.Lines.Skip(Shown)) rest.Children.Add(Row(line, Name(line), top, compact: true));
                var more = new Expander { Header = $"ועוד {e.Lines.Count - Shown} סעיפים · {Shekels(e.Lines.Skip(Shown).Sum(l => l.Total))}", Content = rest, Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.TextSoft };
                more.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-card-more");
                _body.Children.Add(more);
            }
        }
        else _body.Children.Add(new TextBlock { Text = "לא נמצאו באזור שכבות עם סעיף ומחיר.", Foreground = Theme.Muted, Margin = new Thickness(0, 14, 0, 0) });

        // Where the numbers come from: one line each, only what the engineer must know.
        var notes = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        notes.Children.Add(Note((e.PavementIsDefault ? "⚠ " : "") + e.Pavement, e.PavementIsDefault ? Theme.Review : Theme.Muted,
            e.PavementIsDefault ? "מבנים טיפוסיים, לא תכנון הפרויקט — מסומנים \"ברירת מחדל\" בכל שורה ובייצוא. לצירוף דו״ח: פרטים מלאים › כבישים." : null));
        if (e.ConcreteNote != null) notes.Children.Add(Note("⚠ " + e.ConcreteNote, Theme.Review, "המידות בפרטים מלאים › בטון וקונסטרוקציה."));
        if (e.Unpriced > 0) notes.Children.Add(Note($"{e.Unpriced} שכבות באזור ללא מחיר — בפרטים המלאים", Theme.Muted, null));
        _body.Children.Add(notes);

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var export = Action("ייצא Excel…", "GoButton", "boq-card-export");
        export.Click += (_, _) => UiGuard.Run("ייצוא כתב כמויות", () =>
        {
            var folder = _export(this);
            if (folder != null) { _status.Text = "נשמר: " + folder; _status.Foreground = Theme.Muted; }
        });
        buttons.Children.Add(export);
        var details = Action("פרטים מלאים", "PillButton", "boq-card-details");
        details.ToolTip = "כל שכבה באזור, הסעיף שלה, דו״ח המבנה והבטון — בחלון כתב הכמויות.";
        details.Click += (_, _) => { Result = Choice.Details; Close(); };
        buttons.Children.Add(details);
        var another = Action("סמן אזור אחר", "QuietButton", "boq-card-another");
        another.Click += (_, _) => { Result = Choice.MarkAnother; Close(); };
        buttons.Children.Add(another);
        _actions.Children.Clear();
        _actions.Children.Add(buttons);
        _actions.Children.Add(_status);
    }

    private static UIElement Row(InfrastructureBoqWindow.EstimateLine line, string name, decimal top, bool compact = false)
    {
        var row = new StackPanel { Margin = new Thickness(0, compact ? 3 : 6, 0, compact ? 3 : 6), ToolTip = $"{line.Code} · {line.Description}" };
        var head = new DockPanel();
        var price = new TextBlock { Text = Shekels(line.Total), Foreground = Money, FontWeight = FontWeights.SemiBold, FontSize = compact ? 13 : 15,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, Margin = new Thickness(12, 0, 0, 0) };
        // The window is right-to-left, so Dock.Right is the visual LEFT: the name reads from the right, the price ends the row.
        DockPanel.SetDock(price, Dock.Right); head.Children.Add(price);
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

    /// <summary>The price-list text up to its first comma or 46 characters: "תא״צ 19 בעובי 4 ס״מ", not the whole specification.</summary>
    private static string Short(string description, int max = 46)
    {
        var text = description.Trim();
        var cut = text.IndexOfAny(new[] { ',', ';', '(' });
        if (cut > 12) text = text[..cut].Trim();
        return text.Length > max ? text[..max].TrimEnd() + "…" : text;
    }

    private static string Shekels(decimal value) => "₪" + value.ToString("N0");

    private static TextBlock Note(string text, Brush colour, string? tip) =>
        new() { Text = text, Foreground = colour, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };

    private static Button Action(string text, string style, string id)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 8, 0), MinHeight = 34 };
        if (Theme.Dictionary[style] is Style s) button.Style = s;
        button.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, id);
        return button;
    }

    private static Brush Brushes(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
}
