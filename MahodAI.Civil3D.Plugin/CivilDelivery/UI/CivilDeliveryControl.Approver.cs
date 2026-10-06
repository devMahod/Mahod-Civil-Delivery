using System;
using System.Windows;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _approverHooked;

    // b24 (Codex 11:18): the status strip shows the session approver; every decision without its own dialog uses it.
    private void HookApprover()
    {
        if (_approverHooked) return;
        _approverHooked = true;
        ApproverContext.Session.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshApproverLabel));
        RefreshApproverLabel();
    }

    private void RefreshApproverLabel() =>
        ApproverRun.Text = ApproverContext.Session.Name is { } name ? "מאשר נוכחי: " + name : "מאשר נוכחי: לא נקבע";

    private static string? AskApprover(string? action, string? current)
    {
        var dialog = new ApproverPromptDialog(action, current);
        CivilModalHost.ShowFromPalette(dialog);
        return dialog.ConfirmedName;
    }

    private void OnChangeApprover(object sender, RoutedEventArgs e)
    {
        var typed = AskApprover(null, ApproverContext.Session.Name);
        if (ApproverContext.Session.Confirm(typed))
            SetStatus("מאשר נוכחי: " + ApproverContext.Session.Name + " — יוצע בהכרעות הבאות. אישור השם אינו מאשר אף הכרעה.");
    }

    /// <summary>The approver for an action without a dialog of its own; null (nothing is written) when nobody confirmed one.</summary>
    private string? RequireApprover(string action)
    {
        var name = ApproverContext.Session.Require(action, AskApprover);
        if (name == null) SetStatus(action + " — בוטל: לא נקבע מאשר. לא נכתב דבר.");
        return name;
    }
}
