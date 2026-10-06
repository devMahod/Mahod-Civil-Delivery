using System;
using System.Collections.Generic;
using System.IO;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// A session's drawing binding as reported by the chat UI
    /// (<c>DrawingTab.DrawingPath</c> is the single source of truth).
    /// </summary>
    public sealed class SessionBinding
    {
        public SessionBinding(bool sessionKnown, string? drawingPath)
        {
            SessionKnown = sessionKnown;
            DrawingPath = drawingPath;
        }

        /// <summary>False when no tab exists for the session id.</summary>
        public bool SessionKnown { get; }

        /// <summary>
        /// Full path of the drawing the session is bound to. Null for a
        /// drawing-less "Chat" tab (legacy active-document behavior applies).
        /// </summary>
        public string? DrawingPath { get; }
    }

    public enum ToolTargetKind
    {
        /// <summary>Run on the currently active document (legacy / unbound).</summary>
        UseActive,

        /// <summary>The bound drawing is open but not active — activate it first.</summary>
        Activate,

        /// <summary>The tool must not run; return the Hebrew error to the agent.</summary>
        Fail,
    }

    /// <summary>Decision produced by <see cref="ToolTargetPlanner.Plan"/>.</summary>
    public sealed class ToolTargetPlan
    {
        private ToolTargetPlan(ToolTargetKind kind, string? targetDocument, string? errorMessageHe)
        {
            Kind = kind;
            TargetDocument = targetDocument;
            ErrorMessageHe = errorMessageHe;
        }

        public ToolTargetKind Kind { get; }

        /// <summary>Full document name to activate (Kind == Activate).</summary>
        public string? TargetDocument { get; }

        /// <summary>Hebrew error message for the agent (Kind == Fail).</summary>
        public string? ErrorMessageHe { get; }

        public static ToolTargetPlan UseActive() => new(ToolTargetKind.UseActive, null, null);
        public static ToolTargetPlan Activate(string targetDocument) => new(ToolTargetKind.Activate, targetDocument, null);
        public static ToolTargetPlan Fail(string errorMessageHe) => new(ToolTargetKind.Fail, null, errorMessageHe);
    }

    /// <summary>
    /// Pure decision logic for "which drawing may this tool touch?" —
    /// no AutoCAD types, fully unit-testable.
    ///
    /// Contract (2026-08-03 parallel-sessions fix): a tool_call from session X
    /// must never touch a drawing other than session X's drawing. When the
    /// bound drawing is closed, the tool fails with a clear Hebrew error
    /// instead of silently running on whatever drawing happens to be active.
    /// </summary>
    public static class ToolTargetPlanner
    {
        /// <summary>Wire error code sent to the agent when the bound drawing is unavailable.</summary>
        public const string ErrorCodeDrawingUnavailable = "DRAWING_UNAVAILABLE";

        public static ToolTargetPlan Plan(
            string? sessionId,
            SessionBinding? binding,
            IReadOnlyList<string> openDocuments,
            string? activeDocument)
        {
            // Legacy envelope without a session id, or no resolver wired:
            // preserve the historical active-document behavior.
            if (string.IsNullOrEmpty(sessionId) || binding == null)
                return ToolTargetPlan.UseActive();

            if (!binding.SessionKnown)
                return ToolTargetPlan.Fail(
                    "לא נמצאה שיחה פעילה עבור בקשת הכלי — ייתכן שהכרטיסייה נסגרה. פתח שיחה חדשה על השרטוט והרץ שוב.");

            return PlanForBoundDrawing(binding.DrawingPath, openDocuments, activeDocument);
        }

        /// <summary>
        /// Resolves a bound drawing to a target, independent of any session.
        ///
        /// Shared by the tool path and by UI actions (analyze / read drawing),
        /// which press against the SAME hazard: the chat tab is bound to one
        /// drawing while another is active. The UI used to read
        /// MdiActiveDocument directly, so pressing "analyse" from a tab whose
        /// drawing was open-but-not-active analysed the WRONG drawing, spawned a
        /// second tab for it, and left the original tab's loader spinning
        /// forever (owner report, 2026-08-04). One policy, one place.
        /// </summary>
        /// <param name="boundDrawing">The tab's bound drawing, or null for a drawing-less chat tab.</param>
        public static ToolTargetPlan PlanForBoundDrawing(
            string? boundDrawing,
            IReadOnlyList<string> openDocuments,
            string? activeDocument)
        {
            // Drawing-less "Chat" tab: no bound drawing, so the active document
            // is the only meaningful target.
            if (string.IsNullOrEmpty(boundDrawing))
                return ToolTargetPlan.UseActive();

            string? match = null;
            foreach (var doc in openDocuments)
            {
                if (string.Equals(doc, boundDrawing, StringComparison.OrdinalIgnoreCase))
                {
                    match = doc;
                    break;
                }
            }

            if (match == null)
                return ToolTargetPlan.Fail(
                    $"השרטוט '{DisplayName(boundDrawing!)}' המשויך לשיחה זו סגור או אינו זמין — פתח אותו מחדש והרץ את הפעולה שוב.");

            if (activeDocument != null &&
                string.Equals(match, activeDocument, StringComparison.OrdinalIgnoreCase))
                return ToolTargetPlan.UseActive();

            return ToolTargetPlan.Activate(match);
        }

        /// <summary>Short display name for Hebrew messages (file name without directories).</summary>
        internal static string DisplayName(string path)
        {
            try
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch (ArgumentException)
            {
                return path;
            }
        }
    }
}
