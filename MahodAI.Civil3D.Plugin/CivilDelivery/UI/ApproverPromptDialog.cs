using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Asks who approves in this session (b24). Starts from the current approver, or empty — never the Windows
/// account. Only "אשר שם" with a non-blank name returns one; closing or cancelling returns nothing.</summary>
internal sealed class ApproverPromptDialog : Window
{
    internal readonly TextBox NameInput = new()
    {
        MinHeight = 32, Padding = new Thickness(7), Margin = new Thickness(0, 4, 0, 8), MaxLength = 200,
        Foreground = Brushes.Black, Background = Brushes.White,
    };
    internal readonly Button Confirm = Action("אשר שם"), Cancel = Action("ביטול");
    internal string? ConfirmedName { get; private set; }

    internal ApproverPromptDialog(string? action, string? current)
    {
        Title = "מאשר נוכחי";
        Width = 520; SizeToContent = SizeToContent.Height; MinWidth = 340;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 33)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(Text(action == null
            ? "מי מאשר בהפעלה הזו?"
            : $"מי מאשר את: {action}?"));
        body.Children.Add(Text("השם נרשם בפרופיל עם כל הכרעה, ומוצע להכרעות הבאות בהפעלה הזו. אפשר לשנות אותו בכל עת בשורת המצב. אישור השם אינו מאשר אף הכרעה."));
        body.Children.Add(NameInput);
        var actions = new WrapPanel(); actions.Children.Add(Confirm); actions.Children.Add(Cancel);
        body.Children.Add(actions);
        Content = body;
        NameInput.Text = current ?? "";
        System.Windows.Automation.AutomationProperties.SetName(NameInput, "שם המאשר הנוכחי");
        void Refresh() => Confirm.IsEnabled = ApproverContext.Normalize(NameInput.Text) != null;
        NameInput.TextChanged += (_, _) => Refresh();
        Confirm.IsDefault = true; Cancel.IsCancel = true;
        Confirm.Click += (_, _) =>
        {
            ConfirmedName = ApproverContext.Normalize(NameInput.Text);
            if (ConfirmedName != null) Close();
        };
        Cancel.Click += (_, _) => { ConfirmedName = null; Close(); };
        Loaded += (_, _) => NameInput.Focus();
        Refresh();
        UiGuard.Attach(this, "מאשר נוכחי");
    }

    private static TextBlock Text(string text) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 4) };

    private static Button Action(string label) => new()
    {
        Content = label, MinHeight = 34, MinWidth = 90, Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(0, 4, 8, 0), Foreground = new SolidColorBrush(Color.FromRgb(20, 24, 33)),
        Background = new SolidColorBrush(Color.FromRgb(232, 239, 247)),
    };
}
