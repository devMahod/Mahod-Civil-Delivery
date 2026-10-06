using System;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Windows;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Dockable host for the Civil Delivery panel, using the same PaletteSet pattern
    /// as the existing MahodAI chat window so the product feels native to Civil 3D
    /// and docks/snaps like every other palette the engineer already uses.
    /// </summary>
    public static class CivilDeliveryPalette
    {
        private static PaletteSet? _paletteSet;
        private static CivilDeliveryControl? _control;
        private static ElementHost? _host;

        public static void Show()
        {
            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet(
                    "MahodCivilDelivery",
                    "Mahod Civil Delivery",
                    new Guid("C1D2E3F4-5A6B-4C7D-8E9F-0A1B2C3D4E5F"))
                {
                    // AutoCAD restores the last size from the registry and clamps it to the
                    // minimum; the minimum therefore IS the size most engineers see. 620 px
                    // left two table rows under the detail panel (seen live 2026-08-19).
                    MinimumSize = new System.Drawing.Size(600, 760),
                    Size = new System.Drawing.Size(720, 900),
                    DockEnabled = DockSides.Left | DockSides.Right,
                    // No auto-hide button: the rolled-up state persists in the AutoCAD
                    // profile, and a palette that restores rolled-up looks like a broken
                    // install — the command says "נפתח" and nothing appears (live, 30/08).
                    Style = PaletteSetStyles.ShowCloseButton |
                            PaletteSetStyles.Snappable,
                };

                _control = new CivilDeliveryControl();
                _host = new ElementHost
                {
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    Child = _control,
                };
                _paletteSet.Add("Civil Delivery", _host);
            }

            var wasVisible = _paletteSet.Visible;
            _paletteSet.Visible = true;
            if (wasVisible)
            {
                // Re-showing an already-visible palette left the hosted WPF content blank
                // until the next resize (seen live 2026-08-19). Force the host to lay out
                // and repaint instead of leaving the engineer with an empty panel.
                try
                {
                    _host?.PerformLayout();
                    _host?.Refresh();
                    _control?.InvalidateVisual();
                    _control?.UpdateLayout();
                }
                catch { }
            }
            EnsureOnScreen();
            EnsureUsableSize();
            // AutoCAD applies the size it saved in the profile AFTER the palette becomes
            // visible, so an immediate resize is overwritten (seen live 2026-08-19: the
            // palette came back 225 px wide from the previous session and the panel
            // rendered clipped). Re-apply once on the next idle, when the restore is done.
            DeferEnsureUsableSize();
        }

        /// <summary>Smallest palette the two tables remain readable in.</summary>
        internal static readonly System.Drawing.Size MinUsable = new(600, 760);

        /// <summary>What a fresh install opens at.</summary>
        internal static readonly System.Drawing.Size Preferred = new(720, 900);

        internal static void EnsureUsableSize()
        {
            if (_paletteSet == null) return;
            try
            {
                // A docked palette's width belongs to the dock splitter, so setting Size is
                // ignored (seen live 2026-08-19: docked at 225 px, both tables unreadable
                // behind a scrollbar). A dock that narrow is not a preference, it is a
                // broken panel - float it back to a usable size. A dock wide enough to work
                // is left exactly as the engineer arranged it.
                if (_paletteSet.Dock != DockSides.None)
                {
                    if (_paletteSet.Size.Width >= MinUsable.Width) return;
                    _paletteSet.Dock = DockSides.None;
                }

                var size = _paletteSet.Size;
                if (size.Width >= MinUsable.Width && size.Height >= MinUsable.Height) return;
                _paletteSet.Size = new System.Drawing.Size(
                    Math.Max(size.Width, Preferred.Width),
                    Math.Max(size.Height, Preferred.Height));
            }
            catch { }
        }

        /// <summary>
        /// A palette whose saved state is rolled-up or whose saved location is outside
        /// every monitor (projector unplugged, resolution changed) restores invisible.
        /// Un-roll it and pull it back into the working area.
        /// </summary>
        internal static void EnsureOnScreen()
        {
            if (_paletteSet == null) return;
            try { _paletteSet.AutoRollUp = false; } catch { }
            try
            {
                if (_paletteSet.Dock != DockSides.None) return; // docked = visible by definition

                // The title bar must be on a real monitor. The virtual-screen box also covers the
                // gaps of monitors with different sizes or offsets, where nothing is drawn.
                var titleBar = new System.Drawing.Rectangle(_paletteSet.Location, new System.Drawing.Size(
                    Math.Max(_paletteSet.Size.Width, 200), 40));
                if (!OnSomeMonitor(titleBar, 200, 30))
                {
                    var work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                               ?? new System.Drawing.Rectangle(0, 0, 1600, 900);
                    _paletteSet.Location = new System.Drawing.Point(
                        Math.Max(work.Left + 40, work.Right - Preferred.Width - 60),
                        work.Top + 60);
                }
            }
            catch { }
        }

        private static void DeferEnsureUsableSize()
        {
            try
            {
                void Handler(object? sender, EventArgs e)
                {
                    Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= Handler;
                    EnsureOnScreen();
                    EnsureUsableSize();
                    if (IsShownOnScreen(out var state))
                    {
                        MahodLogger.Info("palette shown: " + state);
                        return;
                    }
                    // Seen live 30.09.2026: the first MCD_CIVIL_DELIVERY of a session reported
                    // Visible while nothing was drawn; a second show of the same PaletteSet worked.
                    // Do that second show here once, so one command is always enough.
                    try
                    {
                        _paletteSet!.Visible = false;
                        _paletteSet.Visible = true;
                    }
                    catch { }
                    EnsureOnScreen();
                    EnsureUsableSize();
                    var shown = IsShownOnScreen(out var after);
                    MahodLogger.Info($"palette not on screen after show ({state}); shown again: {(shown ? "on screen" : "still not on screen")} ({after})");
                }
                Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle += Handler;
            }
            catch { }
        }

        /// <summary>
        /// Screen rectangle of the palette while it floats over the drawing area, so
        /// navigation can frame a section beside it instead of under it (seen live
        /// 2026-09-24). A docked or hidden palette does not cover the canvas.
        /// </summary>
        internal static bool TryGetFloatingScreenRect(out System.Drawing.Rectangle rect) =>
            TryGetFloatingScreenRect(out rect, out _);

        /// <summary>
        /// Screen rectangle of the floating palette window (the occluder). The top-level Win32
        /// window that hosts the WPF control is measured directly (seen live 2026-09-24:
        /// 591,126-1311,1026, which is also what PaletteSet.Size 720x900 implies); the
        /// PaletteSet-reported Location/Size is the fallback. Both appear in the diagnostic.
        /// A docked palette already shrinks the canvas and a hidden one needs nothing.
        /// </summary>
        internal static bool TryGetFloatingScreenRect(out System.Drawing.Rectangle rect, out string diagnostic)
        {
            rect = default;
            diagnostic = "";
            try
            {
                if (_paletteSet == null || !_paletteSet.Visible)
                {
                    diagnostic = "palette hidden";
                    return false;
                }
                if (_paletteSet.Dock != DockSides.None)
                {
                    diagnostic = "palette docked " + _paletteSet.Dock;
                    return false;
                }
                var reported = new System.Drawing.Rectangle(_paletteSet.Location, _paletteSet.Size);
                var reportedText = $"reported {reported.Left},{reported.Top}-{reported.Right},{reported.Bottom}";
                if (_host != null && _host.IsHandleCreated)
                {
                    var root = GetAncestor(_host.Handle, GA_ROOT);
                    var main = Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow?.Handle ?? IntPtr.Zero;
                    if (root != IntPtr.Zero && root != main && IsWindowVisible(root) && GetWindowRect(root, out var r) &&
                        r.Right > r.Left && r.Bottom > r.Top)
                    {
                        rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        diagnostic = $"win32 root {rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}; {reportedText}";
                        return true;
                    }
                    diagnostic = root == main ? "win32 root is the main window; " : "win32 root unavailable; ";
                }
                rect = reported;
                diagnostic += reportedText;
                return rect.Width > 0 && rect.Height > 0;
            }
            catch (Exception ex)
            {
                diagnostic = "palette rect failed: " + ex.GetType().Name;
                return false;
            }
        }

        private const uint GA_ROOT = 2;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public static void Hide()
        {
            if (_paletteSet != null) _paletteSet.Visible = false;
        }

        /// <summary>
        /// Invoked by the modal command queued immediately after QSAVE.  QSAVE owns
        /// Civil's normal Save/Save As UI; only after it ends do we return to WPF and
        /// re-validate DBMOD, path and SHA-256 before scanning.
        /// </summary>
        internal static void ResumeEstimateScanAfterExplicitSave()
        {
            var control = _control;
            if (control == null) return;
            control.Dispatcher.BeginInvoke(
                new Action(control.ResumeEstimateScanAfterExplicitSave));
        }

        internal static void ResumeWorkflowAfterExplicitSave(string token)
        {
            var control = _control;
            if (control == null) return;
            control.Dispatcher.BeginInvoke(new Action(() => control.ResumeWorkflowAfterExplicitSave(token)));
        }

        /// <summary>
        /// Opens the panel unless it is really on screen, then closes it. Returns true when the
        /// panel is (being) shown. The PaletteSet flag alone decided this before, and a panel that
        /// reported Visible without being drawn was "closed" by the next command (live 30.09.2026).
        /// </summary>
        public static bool Toggle()
        {
            if (IsShownOnScreen(out var state))
            {
                // The first opening reads the drawing and the CL file before the panel paints (live 30.09.2026:
                // several seconds). A second command in that window means "where is it", not "close it".
                if (DateTime.UtcNow - _openedAtUtc < RepeatKeepsOpen)
                {
                    MahodLogger.Info("palette kept open: command repeated while it was opening; " + state);
                    Show();
                    return true;
                }
                Hide();
                MahodLogger.Info("palette closed by command: " + state);
                return false;
            }
            MahodLogger.Info("palette opened by command: " + state);
            _openedAtUtc = DateTime.UtcNow;
            Show();
            return true;
        }

        /// <summary>True until the panel has been created once in this Civil session (its first load is the slow one).</summary>
        public static bool IsFirstOpen => _paletteSet == null;

        /// <summary>A repeated command this soon after opening re-shows the panel instead of closing it.</summary>
        internal static readonly TimeSpan RepeatKeepsOpen = TimeSpan.FromSeconds(30);
        private static DateTime _openedAtUtc = DateTime.MinValue;

        /// <summary>
        /// True only when the hosted panel is drawn: PaletteSet visible, host window visible
        /// (which includes every parent), not rolled up to a sliver, and on a real monitor.
        /// </summary>
        internal static bool IsShownOnScreen(out string state)
        {
            try
            {
                if (_paletteSet == null)
                {
                    state = "not created";
                    return false;
                }
                state = $"visible={_paletteSet.Visible} dock={_paletteSet.Dock} " +
                        $"location={_paletteSet.Location.X},{_paletteSet.Location.Y} " +
                        $"size={_paletteSet.Size.Width}x{_paletteSet.Size.Height}";
                if (!_paletteSet.Visible) return false;
                if (_host == null || !_host.IsHandleCreated)
                {
                    state += " host=no window";
                    return false;
                }
                if (!IsWindowVisible(_host.Handle))
                {
                    state += " host=hidden";
                    return false;
                }
                if (!GetWindowRect(_host.Handle, out var r))
                {
                    state += " host=no rect";
                    return false;
                }
                var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                state += $" host={rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}";
                if (rect.Width < 100 || rect.Height < 100)
                {
                    state += " (rolled up)";
                    return false;
                }
                if (!OnSomeMonitor(rect, 100, 100))
                {
                    state += " (off every monitor)";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                state = "palette state failed: " + ex.GetType().Name;
                return false;
            }
        }

        private static bool OnSomeMonitor(System.Drawing.Rectangle rect, int minWidth, int minHeight)
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var visible = System.Drawing.Rectangle.Intersect(screen.WorkingArea, rect);
                if (visible.Width >= minWidth && visible.Height >= minHeight) return true;
            }
            return false;
        }
    }
}
