using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Brings a palette-opened review window to the front once, when it is first rendered. The window stays owned by Civil's
/// main window and modal through <see cref="CivilModalHost"/>; this only corrects the z-order when Windows refused the
/// foreground change (live r9, 30.09: the span-label review opened behind Civil and had no taskbar button). One Activate,
/// one Topmost pulse and one SetForegroundWindow — no timer, no retry loop, no owner/modality/state change.
/// </summary>
internal static class DialogActivation
{
    internal static void BringForwardOnFirstRender(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        void OnRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnRendered;
            BringForward(window);
        }
        window.ContentRendered += OnRendered;
    }

    internal static void BringForward(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // Pulse: HWND_TOPMOST then HWND_NOTOPMOST leaves the window on top of the normal z-order, above its owner.
        window.Topmost = true;
        window.Topmost = false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetForegroundWindow() != handle) SetForegroundWindow(handle);
        window.Focus();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
