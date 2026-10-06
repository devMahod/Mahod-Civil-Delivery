using System;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// UI hooks for interactive tools (2026-08-03, "hint one step late" fix).
    ///
    /// Interactive tools run modally on the AutoCAD main thread, so a chat
    /// instruction queued just before them used to render only AFTER the
    /// user's input — every hint appeared one step behind. Two hooks fix the
    /// ordering by construction (no sleeps):
    ///
    /// - <see cref="PostChatHint"/> — fire-and-forget append of an assistant
    ///   bubble; safe to call from the UI thread mid-tool (the modal editor
    ///   loop keeps pumping the dispatcher, so the bubble renders while the
    ///   prompt waits for input).
    /// - <see cref="FlushChat"/> — awaitable barrier that commits every
    ///   already-queued chat render (dispatcher + browser round-trip). The
    ///   ToolExecutor awaits it BEFORE starting an interactive tool.
    ///
    /// Both hooks are optional; when unset (headless tests, self-test
    /// commands) everything degrades to no-ops.
    /// </summary>
    public static class ToolUiNotifier
    {
        /// <summary>Appends an assistant chat bubble (markdown). Wired by NewChatControl.</summary>
        public static Action<string>? PostChatHint { get; set; }

        /// <summary>
        /// Replaces the single live step line at the bottom of the CURRENT
        /// assistant message (markdown). Wired by NewChatControl.
        /// </summary>
        public static Action<string>? SetStepHint { get; set; }

        /// <summary>Removes the live step line. Wired by NewChatControl.</summary>
        public static Action? ClearStepHint { get; set; }

        /// <summary>Commits all pending chat renders. Wired by NewChatControl.</summary>
        public static Func<Task>? FlushChat { get; set; }

        /// <summary>Fire-and-forget chat hint; never throws.</summary>
        public static void Hint(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return;
            try
            {
                PostChatHint?.Invoke(markdown);
            }
            catch
            {
                // A broken UI hook must never take a tool down.
            }
        }

        /// <summary>
        /// Shows the CURRENT step of a multi-pick tool, replacing the previous
        /// one in place.
        ///
        /// A separate bubble per step (the 2026-08-03 shape) turned one road-draw
        /// into a wall of stale instructions the engineer had to read past — the
        /// finished steps stayed on screen looking actionable. One line that is
        /// rewritten as the flow advances keeps exactly the instruction that is
        /// currently true, attached to the message it belongs to.
        /// </summary>
        public static void Step(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return;
            try
            {
                SetStepHint?.Invoke(markdown);
            }
            catch
            {
                // A broken UI hook must never take a tool down.
            }
        }

        /// <summary>Clears the live step line; call when the pick sequence ends
        /// (success, cancel or failure) so no stale instruction survives.</summary>
        public static void ClearStep()
        {
            try
            {
                ClearStepHint?.Invoke();
            }
            catch
            {
                // Never take a tool down on a UI hook.
            }
        }

        /// <summary>Awaitable chat flush; never throws.</summary>
        public static Task FlushChatAsync()
        {
            try
            {
                return FlushChat?.Invoke() ?? Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
            }
        }
    }
}
