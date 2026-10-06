using System;
using System.Windows;
using CivilApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Hosts palette-originated WPF dialogs in Civil's modal window lifecycle.</summary>
internal static class CivilModalHost
{
    internal static bool? ShowFromPalette(Window dialog) =>
        ShowFromPalette(dialog, CivilApplication.MainWindow.Handle,
            CivilApplication.ShowModalWindow);

    // A per-call seam permits offline return/failure tests without loading AutoCAD.
    // It owns no approval, document, profile or global override state.
    internal static bool? ShowFromPalette(Window dialog, IntPtr owner,
        Func<IntPtr, Window, bool, bool?> showModal)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(showModal);
        dialog.Dispatcher.VerifyAccess();
        if (owner == IntPtr.Zero)
            throw new InvalidOperationException("Civil main window is unavailable.");

        // Let the host own modality and native HWND ownership. Keep the existing
        // XAML startup placement instead of restoring a stale off-screen position.
        // Return false/null unchanged; an exception must never become approval.
        return showModal(owner, dialog, false);
    }
}
