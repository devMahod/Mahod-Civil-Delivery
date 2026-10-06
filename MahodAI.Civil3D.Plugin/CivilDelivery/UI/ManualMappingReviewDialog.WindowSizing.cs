using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public sealed partial class ManualMappingReviewDialog
{
    private const double ReviewWorkAreaMargin = 16;

    private void FitReviewWindowToMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var target = HwndSource.FromHwnd(handle)?.CompositionTarget;
        if (handle == IntPtr.Zero || target == null) return;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        if (!GetWindowRect(handle, out var windowRect) || !GetClientRect(handle, out var clientRect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "לא ניתן למדוד את מסגרת חלון בדיקת השיוכים.");
        var transform = target.TransformFromDevice;
        var workVector = transform.Transform(new Vector(work.Width, work.Height));
        var chromeVector = transform.Transform(new Vector(
            windowRect.Right - windowRect.Left - (clientRect.Right - clientRect.Left),
            windowRect.Bottom - windowRect.Top - (clientRect.Bottom - clientRect.Top)));
        if (chromeVector.X < 0 || chromeVector.Y < 0) return;
        var workSize = new Size(workVector.X, workVector.Y);
        var chromeSize = new Size(chromeVector.X, chromeVector.Y);
        if (CalculateReviewWindowSize(workSize, chromeSize) is not { } fitted) return;
        // Preserve CenterOwner and host-owned modality. The measured non-client
        // frame is reserved so the already-tested 900x620 content is never clipped.
        MinWidth = 900 + chromeSize.Width; MinHeight = 620 + chromeSize.Height;
        MaxWidth = workSize.Width - ReviewWorkAreaMargin;
        MaxHeight = workSize.Height - ReviewWorkAreaMargin;
        Width = fitted.Width; Height = fitted.Height;
    }

    internal static Size? CalculateReviewWindowSize(Size workArea, Size chrome)
    {
        if (!double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            !double.IsFinite(chrome.Width) || !double.IsFinite(chrome.Height) ||
            workArea.Width <= 0 || workArea.Height <= 0) return null;
        var availableWidth = workArea.Width - ReviewWorkAreaMargin;
        var availableHeight = workArea.Height - ReviewWorkAreaMargin;
        if (availableWidth < 900 + chrome.Width || availableHeight < 620 + chrome.Height)
            throw new InvalidOperationException("מרחב העבודה קטן מדי לבדיקת שיוכים מלאה. יש להגדיל את מרחב העבודה או להקטין את קנה המידה של התצוגה; לא הוחל שיוך.");
        return new Size(Math.Min(1180, availableWidth), Math.Min(780, availableHeight));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReviewWindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out ReviewWindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out ReviewWindowRect rect);
}
