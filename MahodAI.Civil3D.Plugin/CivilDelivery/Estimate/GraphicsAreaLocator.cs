using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Candidate screen rectangles of the AutoCAD drawing canvas (the graphics area of one
/// document). Seen live 2026-09-24 on Civil 3D 2027: the canvas is the client area of the
/// document's MDI child window (Document.Window, 1912x788 at (4,207)); its drawing view is
/// the child ACADDM_CHILD_DXGI_FLIP_MODE_VIEW_CLASS, and no window is named AfxFrameOrView
/// or OpenGL, so the 1.2.86 name-based search never found the canvas. With several tiled
/// viewports SCREENSIZE describes only the current viewport (1475x788 inside 1912x788).
/// Nothing here is trusted on its own: the caller validates every candidate against
/// SCREENSIZE and the view aspect, and keeps plain framing when none agrees.
/// Read-only Win32 queries.
/// </summary>
internal static class GraphicsAreaLocator
{
    /// <summary>
    /// Canvas candidates, most trustworthy first: the document window's client area, then
    /// the main window's visible children chosen by <see cref="ViewZoomPlan.ChooseCanvasCandidate"/>,
    /// then any other plausible child, smallest first. <paramref name="diagnostic"/> says
    /// what was seen, for the run log.
    /// </summary>
    internal static bool TryGetCanvasCandidates(Document? doc, out List<System.Drawing.Rectangle> candidates,
        out string diagnostic)
    {
        candidates = new List<System.Drawing.Rectangle>();
        var diag = new StringBuilder();
        try
        {
            if (!TryGetScreenSize(out var sx, out var sy))
            {
                diagnostic = "SCREENSIZE unavailable";
                return false;
            }
            diag.Append("screensize=").Append(sx).Append('x').Append(sy);
            var docWindow = doc?.Window?.Handle ?? IntPtr.Zero;
            if (docWindow != IntPtr.Zero && IsWindowVisible(docWindow) && TryClientScreenRect(docWindow, out var client))
            {
                diag.Append(" docwindow=").Append(Describe(client));
                if (ViewZoomPlan.IsCanvasCandidate(client.Width, client.Height, sx, sy)) candidates.Add(client);
            }
            else diag.Append(" docwindow=none");

            var main = AcadApp.MainWindow?.Handle ?? IntPtr.Zero;
            if (main != IntPtr.Zero)
            {
                var rects = new List<System.Drawing.Rectangle>();
                EnumChildWindows(main, (h, _) =>
                {
                    if (IsWindowVisible(h) && TryClientScreenRect(h, out var r) && r.Width >= 40 && r.Height >= 40)
                        rects.Add(r);
                    return true;
                }, IntPtr.Zero);
                var sizes = rects.Select(r => ((double)r.Width, (double)r.Height)).ToList();
                var picked = ViewZoomPlan.ChooseCanvasCandidate(sizes, sx, sy);
                diag.Append(" children=").Append(rects.Count).Append(" picked=")
                    .Append(picked >= 0 ? Describe(rects[picked]) : "none");
                if (picked >= 0) candidates.Add(rects[picked]);
                foreach (var r in rects.Where((r, i) => i != picked && ViewZoomPlan.IsCanvasCandidate(r.Width, r.Height, sx, sy))
                             .OrderBy(r => (long)r.Width * r.Height))
                    candidates.Add(r);
            }
            else diag.Append(" mainwindow=none");

            candidates = candidates.Distinct().ToList();
            diagnostic = diag.ToString();
            return candidates.Count > 0;
        }
        catch (Exception ex)
        {
            diagnostic = diag + " canvas lookup failed: " + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>Current viewport size in pixels (AutoCAD SCREENSIZE), or false.</summary>
    internal static bool TryGetScreenSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            var screen = (Point2d)AcadApp.GetSystemVariable("SCREENSIZE");
            width = (int)Math.Round(screen.X);
            height = (int)Math.Round(screen.Y);
            return width > 40 && height > 40;
        }
        catch
        {
            return false;
        }
    }

    internal static string Describe(System.Drawing.Rectangle r) => $"{r.Left},{r.Top}-{r.Right},{r.Bottom}";

    private static bool TryClientScreenRect(IntPtr hwnd, out System.Drawing.Rectangle rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var client)) return false;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return false;
        rect = new System.Drawing.Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        return rect.Width > 0 && rect.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
