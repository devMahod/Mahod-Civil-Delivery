using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The popup of the typical-sections pick when the drawing gives no road to mark on (2026-11-09, owner, after
/// GM-halufa_A_new2: the button "did nothing" - its one-line reason went to the panel's footer only). It says why and
/// what to do (<see cref="TypicalSectionPickLogic.NoAxis"/>); on a drawing with no Civil alignment it offers to pick
/// the centre line on the plan. Hosted by Civil like the panel's other dialogs.
/// </summary>
internal sealed class TypicalNoticeDialog : Window
{
    /// <summary>True when the engineer chose to pick the centre line now.</summary>
    internal bool PickAxis { get; private set; }

    internal TypicalNoticeDialog(TypicalSectionPickLogic.NoAxisNotice notice)
    {
        Title = "חתכים טיפוסיים - " + notice.Title;
        Width = 520; SizeToContent = SizeToContent.Height; MinWidth = 360;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = CivilDeliveryTheme.Canvas; Foreground = CivilDeliveryTheme.Ink;
        CivilDeliveryTheme.Apply(this, 13);

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = notice.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
            Foreground = CivilDeliveryTheme.Ink,
        });
        body.Children.Add(new TextBlock
        {
            Text = notice.Body, TextWrapping = TextWrapping.Wrap, Foreground = CivilDeliveryTheme.Ink,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var actions = new WrapPanel();
        if (notice.CanPickAxis)
        {
            var pick = Action("בחר קו ציר בשרטוט", primary: true);
            pick.IsDefault = true;
            pick.Click += (_, _) => { PickAxis = true; DialogResult = true; };
            System.Windows.Automation.AutomationProperties.SetAutomationId(pick, "TypicalNoticePickAxis");
            actions.Children.Add(pick);
        }
        var close = Action(notice.CanPickAxis ? "ביטול" : "סגור", primary: !notice.CanPickAxis);
        close.IsCancel = true;
        if (!notice.CanPickAxis) close.IsDefault = true;
        close.Click += (_, _) => { PickAxis = false; DialogResult = false; };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "TypicalNoticeClose");
        actions.Children.Add(close);
        body.Children.Add(actions);
        Content = body;
        UiGuard.Attach(this, Title);
    }

    /// <summary>Shows the notice modally over Civil; true when the engineer chose to pick the centre line.</summary>
    internal static bool Ask(TypicalSectionPickLogic.NoAxisNotice notice)
    {
        var dialog = new TypicalNoticeDialog(notice);
        CivilModalHost.ShowFromPalette(dialog);
        return dialog.PickAxis;
    }

    private static Button Action(string label, bool primary) => new()
    {
        Content = label, MinHeight = 34, MinWidth = 90, Padding = new Thickness(14, 4, 14, 4),
        Margin = new Thickness(0, 4, 8, 0),
        Foreground = primary ? System.Windows.Media.Brushes.White : CivilDeliveryTheme.Ink,
        Background = primary ? CivilDeliveryTheme.Accent : CivilDeliveryTheme.Panel,
    };
}
