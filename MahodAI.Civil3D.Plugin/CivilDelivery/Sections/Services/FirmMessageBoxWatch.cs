using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Timer = System.Threading.Timer;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Reads and closes the message boxes the firm's section program raises while Civil Delivery drives it headless.
    /// MahodCivilNet's <c>ProcessCorridor</c> aborts its transaction on any error outside the station loop and shows the
    /// reason in a modal MessageBox on the AutoCAD thread: nothing is drawn, and the box waits for a click while the
    /// panel shows its busy state (live 2026-10-28, a 7-minute call). Created on the AutoCAD thread just before the call,
    /// this watch polls that thread's top-level dialogs from a timer, keeps the text of each new one and presses its OK,
    /// so the call returns and the text becomes the reason the engineer reads.
    /// </summary>
    internal sealed class FirmMessageBoxWatch : IDisposable
    {
        private const string DialogClass = "#32770";
        private const uint WmCommand = 0x0111;
        private const int IdOk = 1;
        private const int PollMs = 250;

        private readonly uint _threadId;
        private readonly HashSet<IntPtr> _seen;
        private readonly List<string> _texts = new();
        private readonly Timer _timer;
        private int _polling;

        public FirmMessageBoxWatch()
        {
            _threadId = GetCurrentThreadId();
            _seen = new HashSet<IntPtr>(DialogsOf(_threadId));
            _timer = new Timer(_ => Poll(), null, PollMs, PollMs);
        }

        /// <summary>The caption and text of each box closed, in order.</summary>
        public IReadOnlyList<string> Texts
        {
            get { lock (_texts) return _texts.ToList(); }
        }

        public void Dispose()
        {
            using var stopped = new ManualResetEvent(false);
            if (_timer.Dispose(stopped)) stopped.WaitOne(2000);
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;
            try
            {
                foreach (var dialog in DialogsOf(_threadId))
                {
                    if (!_seen.Add(dialog)) continue;
                    var text = TextOf(dialog);
                    lock (_texts) _texts.Add(text);
                    PostMessage(dialog, WmCommand, (IntPtr)IdOk, IntPtr.Zero);
                }
            }
            catch
            {
                // A window that closed between listing and reading: nothing to report.
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private static List<IntPtr> DialogsOf(uint threadId)
        {
            var dialogs = new List<IntPtr>();
            EnumThreadWindows(threadId, (window, _) =>
            {
                if (IsWindowVisible(window) && ClassOf(window) == DialogClass) dialogs.Add(window);
                return true;
            }, IntPtr.Zero);
            return dialogs;
        }

        /// <summary>
        /// Caption and static texts, read without sending a message to the AutoCAD thread (InternalGetWindowText), so a
        /// busy thread can never block the timer.
        /// </summary>
        private static string TextOf(IntPtr dialog)
        {
            var parts = new List<string>();
            var caption = WindowText(dialog);
            if (caption.Length > 0) parts.Add(caption);
            EnumChildWindows(dialog, (child, _) =>
            {
                if (ClassOf(child) == "Static")
                {
                    var text = WindowText(child);
                    if (text.Length > 0) parts.Add(text);
                }
                return true;
            }, IntPtr.Zero);
            return string.Join(": ", parts);
        }

        private static string WindowText(IntPtr window)
        {
            var buffer = new StringBuilder(2048);
            InternalGetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString().Trim();
        }

        private static string ClassOf(IntPtr window)
        {
            var buffer = new StringBuilder(64);
            GetClassName(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder buffer, int capacity);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int InternalGetWindowText(IntPtr window, StringBuilder buffer, int capacity);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
