using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Review one factor, never an inferred paint ratio, catalog choice or measurement approval.</summary>
public sealed class QuantityAdjustmentReviewDialog : Window
{
    private readonly QuantityAdjustmentReviewPolicy.Context _context;
    public QuantityAdjustmentReviewPolicy.Decision? Decision { get; private set; }
    internal TextBox Factor { get; } = new();
    internal TextBox Basis { get; } = new();
    internal TextBox Source { get; } = new();
    internal TextBox Reason { get; } = new();
    internal TextBox Approver { get; } = new();
    internal CheckBox Confirm { get; } = new() { Content = "בדקתי את ההיקף וההצטברות; אני מאשר את השינוי המפורש בלבד." };
    internal TextBlock Preview { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal TextBlock Validation { get; } = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
    internal Button Save { get; } = new() { Content = "אשר ושמור מקדם" };
    internal Button Remove { get; } = new() { Content = "הסר מקדם זה" };
    internal Button Cancel { get; } = new() { Content = "ביטול — ללא שינוי", IsCancel = true };
    internal ScrollViewer BodyScroll { get; } = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public QuantityAdjustmentReviewDialog(QuantityAdjustmentReviewPolicy.Context context)
    {
        _context = context;
        Title = "מקדם כמות לקבוצה — סקירה ואישור";
        Width = 800; Height = 810; MinWidth = 540; MinHeight = 510;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Background = Brushes.White; Foreground = Brushes.Black;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(14) };
        var contextHeading = Text($"הקבוצה בלבד: {Bidi.Ltr(context.RuleKey)}\n" +
            $"שכבות מקור: {Bidi.Ltr(context.Layer)}\nסוג עצם: {Bidi.Ltr(context.EntityTypes)} · " +
            $"מדידה: {KindDisplay(context.Kind)} · יחידה: {UnitDisplay(context.Unit)} · {context.Inputs.Count:N0} עצמים");
        contextHeading.ToolTip = $"Kind: {context.Kind}; Unit: {context.Unit}";
        body.Children.Add(contextHeading);
        body.Children.Add(Text("אין שינוי במדידה המקורית, ביחידה או במחיר. המקדם חל על כל המפתח הזה ללא הבדל אותיות גדולות/קטנות, כולל עצמים עתידיים שיימדדו תחת אותו מפתח בפרופיל. " +
            "אין הסקת יחס מקווקו או אישור מועמד. התצוגה להלן היא חישוב כמות בלבד — חסמי מקור, שיוך ואומדן נשארים בתוקף."));
        body.Children.Add(Text("כל מקדמי הפרופיל — היקפים אחרים עשויים להצטבר על אותם עצמים; מועמדים אינם מוחלים:", true));
        body.Children.Add(new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 54, MaxHeight = 140, Margin = new Thickness(0, 4, 0, 6),
            Text = context.ExistingRules.Count == 0 ? "אין מקדמים קיימים." : string.Join("\n\n", context.ExistingRules.Select(rule =>
                $"{(rule.Candidate ? "מועמד — לא מוחל" : "כלל מאושר")}: {Bidi.Ltr(rule.Id)} · ×{rule.Factor?.ToString("R", CultureInfo.InvariantCulture)} · סדר {rule.Order}\n" +
                $"היקף: {Bidi.Ltr(rule.Scope ?? "חסר")}\nמקור: {rule.Source} · נימוק: {rule.Reason}\n" +
                $"מאשר: {rule.ApprovedBy ?? "אין"} · {rule.ApprovedAtUtc:O}")),
        });
        AddField(body, "מקדם חיובי (נקודה עשרונית; ללא מפריד אלפים)", Factor);
        Factor.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        AddField(body, "בסיס המדידה — מה נמדד ולמה נדרשת ההתאמה", Basis);
        AddField(body, "מקור מוסמך (מסמך / כלל מדידה מזוהה ותאריך)", Source);
        AddField(body, "נימוק לשינוי או להסרה", Reason);
        AddField(body, "שם המאשר — להזנה מפורשת", Approver);
        if (context.Owned != null)
        {
            Factor.Text = context.Owned.Factor?.ToString("R", CultureInfo.InvariantCulture) ?? "";
            Basis.Text = QuantityAdjustmentReviewPolicy.Basis(context.Owned);
            Source.Text = context.Owned.Source ?? ""; Reason.Text = context.Owned.Reason ?? "";
        }
        BodyScroll.Content = body;
        var footer = new StackPanel { Margin = new Thickness(14, 6, 14, 10) };
        footer.Children.Add(Preview);
        Confirm.Content = new TextBlock { Text = Confirm.Content.ToString(), TextWrapping = TextWrapping.Wrap };
        Confirm.Margin = new Thickness(0, 8, 0, 6); footer.Children.Add(Confirm);
        footer.Children.Add(Validation);
        var actions = new WrapPanel();
        foreach (var button in new[] { Save, Remove, Cancel })
        { button.MinHeight = 38; button.Padding = new Thickness(10, 6, 10, 6); button.Margin = new Thickness(3); actions.Children.Add(button); }
        footer.Children.Add(actions);
        var root = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(BodyScroll);
        Content = root;
        foreach (var input in new[] { Factor, Basis, Source, Reason, Approver })
            input.TextChanged += (_, _) => { Confirm.IsChecked = false; Refresh(); };
        Confirm.Checked += (_, _) => Refresh(); Confirm.Unchecked += (_, _) => Refresh();
        Save.Click += (_, _) => { if (TryAccept(false)) DialogResult = true; };
        Remove.Click += (_, _) => { if (TryAccept(true)) DialogResult = true; };
        Cancel.Click += (_, _) => { Decision = null; Close(); };
        UiGuard.Attach(this, "מקדם כמות לקבוצה");
        Refresh();
    }

    private static TextBlock Text(string text, bool bold = false) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 8) };
    private static string KindDisplay(string kind) => kind switch
    { "length" => "אורך", "area" => "שטח", "count" => "ספירה", "volume" => "נפח", _ => Bidi.Ltr(kind) };
    private static string UnitDisplay(string unit) => unit switch
    { "m" => "מטר", "m2" => "מ״ר", "m3" => "מ״ק", "unit" => "יח׳", _ => Bidi.Ltr(unit) };
    private static void AddField(StackPanel body, string label, TextBox input)
    { body.Children.Add(Text(label)); input.MinHeight = 32; input.Padding = new Thickness(5); input.Margin = new Thickness(0, 0, 0, 8); body.Children.Add(input); }
    private double? ParsedFactor() => double.TryParse(Factor.Text.Trim(),
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
        CultureInfo.InvariantCulture, out var value) ? value : null;
    private bool CompleteFields => new[] { Basis.Text, Source.Text, Reason.Text, Approver.Text }.All(value => !string.IsNullOrWhiteSpace(value));
    private void Refresh()
    {
        Save.IsEnabled = false; Remove.IsEnabled = _context.Owned != null && CompleteFields && Confirm.IsChecked == true;
        Validation.Text = "";
        try
        {
            var value = QuantityAdjustmentReviewPolicy.PreviewChange(_context, ParsedFactor(), false);
            Preview.Text = $"מדידה מקורית ({KindDisplay(_context.Kind)}, ללא שינוי): {value.RawTotal:N4} {UnitDisplay(_context.Unit)} · {value.ObjectCount:N0} עצמים\n" +
                $"כמות נגזרת נוכחית: {value.BeforeTotal:N4} · לאחר השינוי: {value.AfterTotal:N4} {UnitDisplay(_context.Unit)}";
            if (_context.Owned != null)
                Preview.Text += $"\nלאחר הסרת המקדם הזה בלבד: {QuantityAdjustmentReviewPolicy.PreviewChange(_context, null, true).AfterTotal:N4} {UnitDisplay(_context.Unit)}";
            Save.IsEnabled = CompleteFields && Confirm.IsChecked == true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        { Preview.Text = "תצוגה מקדימה אינה אישור מדידה או אומדן."; Validation.Text = ex.Message; }
    }
    internal bool TryAccept(bool remove)
    {
        try
        {
            Decision = QuantityAdjustmentReviewPolicy.Approve(_context, ParsedFactor(), remove, Basis.Text,
                Source.Text, Reason.Text, Approver.Text, DateTime.UtcNow, Confirm.IsChecked == true);
            return Decision != null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        { Decision = null; Validation.Text = ex.Message; return false; }
    }
}
