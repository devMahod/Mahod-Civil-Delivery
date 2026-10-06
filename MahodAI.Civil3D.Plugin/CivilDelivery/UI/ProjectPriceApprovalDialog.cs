using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>A quote is entered and explicitly approved by a person, never suggested or accepted automatically.</summary>
public sealed class ProjectPriceApprovalDialog : Window
{
    public ProjectPriceApprovalPolicy.Approval? Approval { get; private set; }
    private readonly ProjectPriceApprovalPolicy.Context _context;
    private readonly TextBox _price = new(), _source = new(), _reason = new(), _approver = new();
    private readonly TextBlock _validation = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed };

    public ProjectPriceApprovalDialog(ProjectPriceApprovalPolicy.Context context,
        IEnumerable<ProjectProfile.EstimateProfile.PriceOverride> previous)
    {
        _context = context;
        Title = "אישור מחיר פרויקט — החלטה מפורשת";
        Width = 760; Height = 740; MinWidth = 540; MinHeight = 460;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = $"סעיף {Bidi.Ltr(context.ItemCode)} · {context.Description}\n" +
                   $"מחיר ליחידת סעיף: {Bidi.Ltr(context.Unit)} · במטבע המחירון הפעיל\n" +
                   $"מחירון: {Bidi.Ltr(context.CatalogId)}\nSHA-256: {Bidi.Ltr(context.CatalogHash)}\n" +
                   "האישור יחול על כל כמויות הפרויקט המשויכות לסעיף הזה, לא רק על השורה המסומנת. " +
                   "אין שינוי בכמות או ביחידה. אישור מחיר אינו אישור מקור או גאומטריה; לאחר השמירה נדרשת בניית אומדן מחדש.",
            TextWrapping = TextWrapping.Wrap,
        });
        var existing = previous.ToArray();
        if (existing.Length > 0)
            body.Children.Add(new TextBox
            {
                Text = "החלטות קודמות לאותו סעיף — האישור החדש יחליף את כולן:\n" + string.Join("\n\n", existing.Select(value =>
                    $"{value.Price?.ToString(CultureInfo.InvariantCulture) ?? "חסר מחיר"} · {value.Source}\n{value.Reason}\n" +
                    $"מאשר: {value.ApprovedBy ?? "חסר"} · {value.ApprovedAtUtc:O}\n" +
                    $"מחירון: {value.ApprovedCatalogId ?? "ללא נעיצת מהדורה ישנה"} · {value.ApprovedCatalogHash}")),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0),
            });
        AddField(body, "מחיר ליחידה (חיובי; ללא מפריד אלפים)", _price);
        _price.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        AddField(body, "מקור למחיר (ספק / מסמך מזוהה ותאריך)", _source);
        AddField(body, "נימוק למחיר הפרויקט", _reason);
        _reason.AcceptsReturn = true; _reason.TextWrapping = TextWrapping.Wrap; _reason.MinHeight = 60;
        AddField(body, "שם המאשר — להזנה מפורשת", _approver);
        body.Children.Add(new TextBlock { Text = "חותמת UTC תירשם בעת לחיצה על אישור. ביטול לא משנה דבר.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) });
        body.Children.Add(_validation);
        var approve = new Button { Content = "אשר ושמור מחיר פרויקט", MinHeight = 40, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(6) };
        var cancel = new Button { Content = "ביטול — ללא שינוי", IsCancel = true, MinHeight = 40, Margin = new Thickness(6) };
        approve.Click += (_, _) => { if (TryApprove()) DialogResult = true; }; cancel.Click += (_, _) => Close();
        var actions = new WrapPanel { Margin = new Thickness(10) }; actions.Children.Add(approve); actions.Children.Add(cancel);
        var root = new DockPanel(); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root; UiGuard.Attach(this, "אישור מחיר פרויקט");
    }

    private static void AddField(Panel panel, string label, TextBox input)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) });
        input.Padding = new Thickness(6); input.MinHeight = 34; panel.Children.Add(input);
    }

    internal bool TryApprove()
    {
        // Decimal only; rejecting grouping avoids silently interpreting 1,250 as 1.25.
        var raw = _price.Text.Trim();
        decimal? price = decimal.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var now = DateTime.UtcNow;
        var errors = ProjectPriceApprovalPolicy.Validate(price, _source.Text, _reason.Text, _approver.Text, now);
        if (errors.Count > 0) { _validation.Text = string.Join("\n", errors); return false; }
        Approval = ProjectPriceApprovalPolicy.Approve(_context, price, _source.Text, _reason.Text, _approver.Text, now, explicitlyApproved: true);
        return true;
    }
}
