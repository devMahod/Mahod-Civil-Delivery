using System;
using System.Text;
using System.Threading;

namespace MahodAI.Civil3D.Plugin.Models
{
    /// <summary>
    /// Holds per-tab conversation state. A tab is bound to at most one drawing
    /// (immutable on creation). Multiple tabs may share the same DrawingPath.
    /// DrawingPath == null means a drawing-less "Chat" tab.
    /// </summary>
    public class DrawingTab
    {
        /// <summary>
        /// Unique tab identifier (GUID). Stable across renames / Save-As.
        /// </summary>
        public string TabId { get; }

        /// <summary>
        /// Display name for the tab (e.g. "road1", "road1 1", "Chat", "Chat 2").
        /// Derived from the bound drawing's filename plus a per-drawing suffix.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Full drawing file path the tab is bound to. Null = drawing-less chat.
        /// Immutable for the lifetime of the tab.
        /// </summary>
        public string? DrawingPath { get; set; }

        /// <summary>
        /// Agent session ID for this tab.
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Whether an agent session is active for this tab.
        /// </summary>
        public bool AgentSessionActive { get; set; }

        /// <summary>
        /// Accumulated conversation HTML for this tab.
        /// </summary>
        public StringBuilder ConversationHtml { get; } = new();

        /// <summary>
        /// Whether the welcome message has been shown in this tab.
        /// </summary>
        public bool WelcomeShown { get; set; }

        /// <summary>
        /// Whether the user has sent any message in this tab.
        /// </summary>
        public bool HasUserSentMessage { get; set; }

        /// <summary>
        /// Last analysis report HTML (for re-display).
        /// </summary>
        public string? LastReportHtml { get; set; }

        /// <summary>
        /// True once an analysis has actually COMPLETED for this tab's drawing
        /// (set on the agent's <c>analysis_complete</c>).
        ///
        /// Deliberately separate from <see cref="LastReportHtml"/>, which every
        /// assistant message overwrites and therefore says nothing about whether
        /// an analysis ran — the fix flow needs the real thing.
        /// </summary>
        public bool HasCompletedAnalysis { get; set; }

        /// <summary>
        /// Last operations summary text.
        /// </summary>
        public string? LastOperationsSummary { get; set; }

        /// <summary>
        /// Last DWG JSON data extracted for the bound drawing.
        /// </summary>
        public string? LastDwgJson { get; set; }

        /// <summary>
        /// Scope card HTML start position for removal.
        /// </summary>
        public int ScopeCardHtmlStart { get; set; } = -1;

        /// <summary>
        /// Current fix plan for this tab.
        /// </summary>
        public WebSocket.FixPlanPayload? CurrentFixPlan { get; set; }

        /// <summary>
        /// Most recent fix_result payload (pre-verification) for this tab.
        /// </summary>
        public WebSocket.FixResultPayload? LastFixResult { get; set; }

        /// <summary>
        /// Per-plan edited-but-not-submitted tool_param overrides captured from
        /// the WebView before the last tab switch.
        /// </summary>
        public Dictionary<string, List<WebSocket.FixItemEdit>> PendingFixEdits { get; set; } = new();

        public bool HasShownDwgLayersReport { get; set; }
        public bool HasAnalyzedShapefile { get; set; }

        // === Per-tab streaming state ===

        /// <summary>True while this tab has a chat / analyze in flight.</summary>
        public bool IsSending { get; set; }

        /// <summary>Stream id assigned by the agent for the currently in-flight response, or null.</summary>
        public string? CurrentStreamId { get; set; }

        /// <summary>
        /// Set true when the user presses Stop for this tab's in-flight request.
        /// Guards the stream event handlers so a late stream_start/token/end that
        /// arrives after a cancel can't resurrect the thinking indicator or render
        /// stale content. Reset to false at the start of every new send.
        /// </summary>
        public bool CancelRequested { get; set; }

        /// <summary>
        /// Cancellation source for the HTTP-fallback request path. Pressing Stop
        /// cancels it to abort the in-flight POST. Null / unused on the WebSocket
        /// streaming path (which is cancelled via CancelStreamAsync instead).
        /// </summary>
        public CancellationTokenSource? RequestCts { get; set; }

        /// <summary>Accumulated streaming text for the in-flight response.</summary>
        public StringBuilder CurrentStreamContent { get; } = new();

        /// <summary>
        /// The last request this tab sent, in the engineer's own words (or the
        /// Hebrew label of a button-driven request). Used only as the "question"
        /// for per-message feedback (v1.14) when the answer has no
        /// <c>question_logs</c> row for the agent to attach the feedback to.
        /// </summary>
        public string? LastUserMessage { get; set; }

        /// <summary>When the current send was initiated (used for elapsed-time loaders).</summary>
        public DateTime SendStartedAt { get; set; }

        /// <summary>Loader id displayed for the current send, if any.</summary>
        public string? CurrentLoaderId { get; set; }

        public DateTime CreatedAt { get; } = DateTime.Now;
        public DateTime LastActiveAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Creates a new tab. Auto-generates a TabId if not supplied.
        /// </summary>
        public DrawingTab(string? drawingPath, string displayName, string? tabId = null)
        {
            TabId = tabId ?? Guid.NewGuid().ToString("N");
            DrawingPath = drawingPath;
            DisplayName = displayName;
        }
    }
}
