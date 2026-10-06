using System;
using System.Windows;
using System.Windows.Controls;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Reviews an existing project association; does not approve any engineering decision.</summary>
internal sealed class ProjectProfileSelectionDialog : Window
{
    internal bool Accepted { get; private set; }
    internal readonly CheckBox Confirm = new() { Content = "בדקתי: השרטוט הזה שייך לפרויקט הנבחר, והבנתי אילו החלטות קיימות בו.", Margin = new Thickness(0, 12, 0, 8) };
    internal readonly Button SelectButton = new() { Content = "השתמש בפרופיל לשרטוט הזה", MinHeight = 38, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(5) };
    internal readonly Button CancelButton = new() { Content = "ביטול — ללא שינוי", IsCancel = true, MinHeight = 38, Margin = new Thickness(5) };
    internal readonly TextBlock Validation = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed };
    internal readonly ScrollViewer BodyScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    internal ProjectProfileSelectionDialog(ExistingProjectProfileSelection.Preview preview, string drawingPath)
    {
        Title = "בחירת פרופיל פרויקט קיים";
        Width = 790; Height = 730; MinWidth = 560; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 14;
        Background = System.Windows.Media.Brushes.White; Foreground = System.Windows.Media.Brushes.Black;
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        Confirm.Content = Text("בדקתי: השרטוט הזה שייך לפרויקט הנבחר, והבנתי אילו החלטות קיימות בו.");
        footer.Children.Add(Confirm); footer.Children.Add(Validation);
        var actions = new WrapPanel(); actions.Children.Add(SelectButton); actions.Children.Add(CancelButton);
        footer.Children.Add(actions); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(Text($"פרויקט {preview.ProfileId} — {preview.ProjectName}\nגרסת פרופיל {preview.Version}", 18));
        body.Children.Add(Text("הבחירה היא לשימוש הנוכחי ב־Civil ולמסמך הזה בלבד. שינוי נתיב השרטוט דורש בחירה מחדש. לא נשמר קישור בשרטוט ולא מועתק פרופיל."));
        AddPath(body, "השרטוט הנוכחי", drawingPath);
        AddPath(body, "קובץ הפרופיל הנבחר — גם יעד עריכות עתידיות שאותן תאשר", preview.Path);
        body.Children.Add(Text("SHA-256: " + Bidi.Ltr(preview.Hash)));
        body.Children.Add(Text("החלטות קיימות בפרופיל", 16));
        body.Children.Add(Text(preview.Decisions));
        body.Children.Add(Text("תוצאות וטיוטות מהפרופיל הקודם יוסרו מהתצוגה לאחר הבחירה. תידרש סריקה או תכנון חדשים. הבחירה אינה אישור למדידות, למקורות או לאומדן, ואינה מתקנת החלטה לא תקפה."));
        BodyScroll.Content = body; root.Children.Add(BodyScroll);
        Content = root;
        SelectButton.Click += (_, _) => { if (TryAccept()) DialogResult = true; };
        CancelButton.Click += (_, _) => { Accepted = false; Close(); };
        Confirm.Unchecked += (_, _) => Accepted = false;
        UiGuard.Attach(this, "בחירת פרופיל קיים");
    }

    internal bool TryAccept()
    {
        Accepted = Confirm.IsChecked == true;
        Validation.Text = Accepted ? "" : "יש לבדוק ולאשר במפורש את הקישור לפרויקט הנבחר.";
        return Accepted;
    }

    private static TextBlock Text(string value, double size = 14) => new()
    { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 5, 0, 5) };
    private static void AddPath(Panel body, string label, string path)
    {
        body.Children.Add(Text(label));
        body.Children.Add(new TextBox { Text = path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, Padding = new Thickness(5), BorderThickness = new Thickness(1) });
    }
}
