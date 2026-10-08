using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Control = System.Windows.Controls.Control;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Explicit offer only. No default selection, registration, or project mutation.</summary>
internal sealed class KnownPriceBookDialog : Window
{
    internal readonly ListBox Books = new() { DisplayMemberPath = "Label", MinHeight = 90, MaxHeight = 155 };
    internal readonly TextBox Details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.White };
    internal readonly CheckBox Confirm = new() { Margin = new Thickness(0, 10, 0, 5) };
    internal readonly Button UseButton = new() { Content = "רשום בפרויקט והפעל", MinHeight = 38, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4) };
    internal readonly Button CancelButton = new() { Content = "ביטול — ללא שינוי", IsCancel = true, MinHeight = 38, Margin = new Thickness(4) };
    internal readonly TextBlock Validation = new() { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
    internal readonly ScrollViewer BodyScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal KnownPriceBookIndex.Offer? Accepted { get; private set; }

    internal KnownPriceBookDialog(IReadOnlyList<KnownPriceBookIndex.Offer> offers, string project)
    {
        Title = "מחירונים שנרשמו בעבר במחשב זה"; Width = 790; Height = 670; MinWidth = 550; MinHeight = 480;
        FlowDirection = System.Windows.FlowDirection.RightToLeft; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Background = Brushes.White; Foreground = Brushes.Black; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        Confirm.Content = Text("בדקתי את המהדורה ואת קריאת העמודות; המחירון מתאים לפרויקט ויש לי הרשאה להשתמש בו.");
        footer.Children.Add(Confirm); footer.Children.Add(Validation);
        var actions = new WrapPanel(); actions.Children.Add(UseButton); actions.Children.Add(CancelButton); footer.Children.Add(actions); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(Text("רישום מחירון בפרויקט " + project, 18));
        body.Children.Add(Text("כאן מוצעים מחירונים הכלולים בתוסף ומחירונים שנרשמו בעבר במחשב זה, לאחר אימות זהותם. הרישום יעתיק את המחירון לפרויקט ויפעיל אותו; נדרשת סריקה חדשה ושיוכים ייבדקו מחדש."));
        body.Children.Add(Text(offers.Count == 0 ? "כל המחירונים הזמינים כבר רשומים בפרויקט, או שאין מחירון זמין. למחירון קיים השתמש ברשימת מחירון פעיל; לקובץ נוסף השתמש ב־טען מחירון…." : "בחר מחירון ובדוק את פרטיו:"));
        Books.ItemsSource = offers; Books.SelectedIndex = -1;
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
        Books.ItemContainerStyle = style;
        var template = new DataTemplate(); var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label")); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        template.VisualTree = text; Books.DisplayMemberPath = ""; Books.ItemTemplate = template;
        ScrollViewer.SetHorizontalScrollBarVisibility(Books, ScrollBarVisibility.Disabled);
        body.Children.Add(Books); body.Children.Add(Text("זהות המחירון וקריאתו", 16)); body.Children.Add(Details);
        BodyScroll.Content = body; root.Children.Add(BodyScroll); Content = root;
        Books.SelectionChanged += (_, _) => { Accepted = null; Confirm.IsChecked = false; Validation.Text = ""; RefreshDetails(); };
        Confirm.Unchecked += (_, _) => Accepted = null;
        UseButton.Click += (_, _) => { if (TryAccept()) DialogResult = true; };
        CancelButton.Click += (_, _) => { Accepted = null; Close(); };
        RefreshDetails(); UiGuard.Attach(this, "מחירון מוכר");
    }

    private void RefreshDetails()
    {
        UseButton.IsEnabled = Books.SelectedItem is KnownPriceBookIndex.Offer;
        if (Books.SelectedItem is not KnownPriceBookIndex.Offer offer) { Details.Text = "לא נבחר מחירון."; return; }
        var e = offer.Entry;
        var reading = e.Mapping is { } m
            ? $"גיליון: {m.SheetName}\nשורת כותרת: {m.HeaderRow}\nקוד: {m.CodeColumn}; תיאור: {m.DescriptionColumn ?? "ללא"}; יחידה: {m.UnitColumn}; מחיר: {m.PriceColumn}"
            : "קריאה אוטומטית של גיליון ועמודות; הקובץ ייקרא וייבדק לפני הרישום בפרויקט.";
        Details.Text = $"מוציא: {e.Publisher ?? "לא צוין"}\nמהדורה: {e.Edition ?? "לא צוינה"}\nסעיפים: {e.ItemCount?.ToString("N0") ?? "לא צוין"}\n{reading}\n" +
            $"נרשם בעבר על ידי: {e.RegisteredBy ?? "לא צוין"}\nנתיב: {Bidi.Ltr(e.Path)}\nSHA-256: {Bidi.Ltr(e.Sha256)}\n" +
            "רישום קודם אינו אישור לתוקף המחירים היום או לרישיון בפרויקט הזה. אין כאן אישור כמויות או הנדסה.";
    }

    internal bool TryAccept()
    {
        Accepted = Confirm.IsChecked == true ? Books.SelectedItem as KnownPriceBookIndex.Offer : null;
        Validation.Text = Accepted == null ? "בחר מחירון ואשר במפורש את התאמתו ואת הרשאת השימוש." : "";
        return Accepted != null;
    }

    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
}
