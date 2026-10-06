using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.Services.Extraction;
using MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;
using MahodAI.Civil3D.Plugin.Services.State;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.ViewModels
{
    /// <summary>
    /// ViewModel for the chat control. Manages state, session, tabs, and orchestrates services.
    /// </summary>
    public class ChatViewModel
    {
        // === Tab management ===
        public Dictionary<string, DrawingTab> DrawingTabs { get; } = new();
        public string? ActiveTabKey { get; set; }

        // === Session ===
        public MahodAgentService? AgentService { get; set; }
        public MahodWebSocketClient? WsClient { get; set; }
        public ToolExecutor? ToolExecutor { get; set; }
        public string? CurrentDrawingForSession { get; set; }
        public bool AssistantInitialized { get; set; }
        public bool UseWebSocket { get; set; } = true;

        // === Streaming ===
        public SemaphoreSlim WsLock { get; } = new(1, 1);

        /// <summary>
        /// session_id → tab_id. Established at session_create time and used by
        /// inbound WS event handlers to route stream/tool events to the correct
        /// tab regardless of which tab is currently active.
        /// </summary>
        public Dictionary<string, string> SessionIdToTabId { get; } = new();

        /// <summary>
        /// Returns the active tab if any, else null.
        /// </summary>
        public DrawingTab? ActiveTab =>
            ActiveTabKey != null && DrawingTabs.TryGetValue(ActiveTabKey, out var t) ? t : null;

        /// <summary>True iff the active tab has an in-flight send. UI gating uses this.</summary>
        public bool ActiveTabSending => ActiveTab?.IsSending == true;

        /// <summary>True iff an agent session is active for the active tab.</summary>
        public bool ActiveTabAgentSessionActive => ActiveTab?.AgentSessionActive == true;

        // === Fix workflow ===
        public FixPlanPayload? CurrentFixPlan { get; set; }
        public FixResultPayload? LastFixResult { get; set; }
        public FixVerifyResultPayload? LastFixVerify { get; set; }
        public Dictionary<string, List<FixItemEdit>> PendingFixEditsByPlan { get; set; } = new();
        public HighlightManager HighlightManager { get; } = new();

        // === Extraction ===
        public DataExtractionEngine? ExtractionEngine { get; set; }
        public DrawingStateStore? StateStore { get; set; }

        // === UI state ===
        public bool IsDarkMode { get; set; }
        public bool WelcomeShown { get; set; }
        public bool HasUserSentMessage { get; set; }
        public bool IsWebViewInitialized { get; set; }
        public StringBuilder ConversationHtml { get; } = new();
        public string? LastReportHtml { get; set; }
        public string? LastOperationsSummary { get; set; }
        public string? CurrentLoaderId { get; set; }
        public int ScopeCardHtmlStart { get; set; } = -1;
        public List<string> PendingAttachmentPaths { get; } = new();
        /// <summary>
        /// Images attached to the next question (PROTOCOL v1.16). Separate from
        /// <see cref="PendingAttachmentPaths"/> because these can come from the
        /// clipboard or a drop and therefore have no file path — and because they
        /// travel as real image bytes rather than being extracted to text.
        /// </summary>
        public List<Utilities.PendingImage> PendingImages { get; } = new();
        /// <summary>
        /// PDFs / text files attached to the next question (PROTOCOL v1.17).
        /// These travel whole; <see cref="PendingAttachmentPaths"/> is the older
        /// route where the server extracts text (DOCX/XLS, or anything too big).
        /// </summary>
        public List<Utilities.PendingDocument> PendingDocuments { get; } = new();
        public DateTime SendStartedAt { get; set; }

        // === Cached data ===
        public object? LastSummary { get; set; }
        public ShapefileSummary? LastShapefileSummary { get; set; }
        public string? LastDwgJson { get; set; }
        public string? LastShpJson { get; set; }
        public bool HasShownDwgLayersReport { get; set; }
        public bool HasAnalyzedShapefile { get; set; }
        public DrawingDataModel? LastDrawingData { get; set; }

        // === Tab management methods ===

        /// <summary>
        /// Save the current UI state into the active DrawingTab.
        /// Per-tab streaming state (IsSending, CurrentStreamId, CurrentStreamContent,
        /// SessionId, AgentSessionActive) is owned by the DrawingTab itself and is
        /// not mirrored here — the inbound WS event handlers update it directly.
        /// </summary>
        public void SaveCurrentTabState()
        {
            if (ActiveTabKey == null || !DrawingTabs.TryGetValue(ActiveTabKey, out var tab))
                return;

            // If this tab is mid-stream, bake the accumulated streamed text into the
            // saved HTML so that switching back later still shows the partial output.
            // The active stream-block in ConversationHtml only contains the thinking
            // indicator; the actual streamed text lives in the DOM and in tab.CurrentStreamContent.
            string html = ConversationHtml.ToString();
            if (tab.IsSending && tab.CurrentStreamContent.Length > 0)
            {
                html = ReplaceStreamPlaceholderWithPartial(html, tab.CurrentStreamContent.ToString());
            }
            tab.ConversationHtml.Clear();
            tab.ConversationHtml.Append(html);

            tab.WelcomeShown = WelcomeShown;
            tab.HasUserSentMessage = HasUserSentMessage;
            tab.LastReportHtml = LastReportHtml;
            tab.LastOperationsSummary = LastOperationsSummary;
            tab.LastDwgJson = LastDwgJson;
            tab.ScopeCardHtmlStart = ScopeCardHtmlStart;
            tab.CurrentFixPlan = CurrentFixPlan;
            tab.LastFixResult = LastFixResult;
            tab.PendingFixEdits = new Dictionary<string, List<FixItemEdit>>(PendingFixEditsByPlan);
            tab.HasShownDwgLayersReport = HasShownDwgLayersReport;
            tab.HasAnalyzedShapefile = HasAnalyzedShapefile;
            tab.LastActiveAt = DateTime.Now;
        }

        /// <summary>
        /// Inside an in-progress stream-block (the one with the thinking-indicator),
        /// inject the accumulated streamed text so that re-rendering the tab shows
        /// the partial response. The block remains marked as a stream-block so that
        /// when the user returns, subsequent token events can keep mutating its DOM.
        /// </summary>
        private static string ReplaceStreamPlaceholderWithPartial(string html, string partialText)
        {
            const string marker = "assistant-stream-content";
            int idx = html.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx == -1) return html;
            int contentOpen = html.IndexOf('>', idx);
            if (contentOpen == -1) return html;
            int contentClose = html.IndexOf("</div>", contentOpen, StringComparison.Ordinal);
            if (contentClose == -1) return html;

            string safe = System.Net.WebUtility.HtmlEncode(partialText).Replace("\n", "<br/>");
            string newInner = "<span class='stream-partial'>" + safe + "</span>";
            return html.Substring(0, contentOpen + 1) + newInner + html.Substring(contentClose);
        }

        /// <summary>
        /// Restore UI state from a DrawingTab. The tab's session id is NOT bound to
        /// any shared client state — every outbound send takes the session id of the
        /// originating tab as a parameter, so multiple tabs run independently.
        /// </summary>
        public void RestoreTabState(DrawingTab tab)
        {
            ConversationHtml.Clear();
            ConversationHtml.Append(tab.ConversationHtml.ToString());
            CurrentDrawingForSession = tab.DrawingPath;
            WelcomeShown = tab.WelcomeShown;
            HasUserSentMessage = tab.HasUserSentMessage;
            LastReportHtml = tab.LastReportHtml;
            LastOperationsSummary = tab.LastOperationsSummary;
            LastDwgJson = tab.LastDwgJson;
            ScopeCardHtmlStart = tab.ScopeCardHtmlStart;
            CurrentFixPlan = tab.CurrentFixPlan;
            LastFixResult = tab.LastFixResult;
            PendingFixEditsByPlan = new Dictionary<string, List<FixItemEdit>>(tab.PendingFixEdits);
            HasShownDwgLayersReport = tab.HasShownDwgLayersReport;
            HasAnalyzedShapefile = tab.HasAnalyzedShapefile;
            CurrentLoaderId = tab.CurrentLoaderId;
            SendStartedAt = tab.SendStartedAt;

            ActiveTabKey = tab.TabId;
            tab.LastActiveAt = DateTime.Now;
        }

        /// <summary>
        /// Bind a session id to a tab id and remember the reverse mapping so inbound
        /// WS events can route to the correct tab. Replaces any prior binding for the
        /// same session id.
        /// </summary>
        public void RegisterSession(string sessionId, string tabId)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(tabId)) return;
            SessionIdToTabId[sessionId] = tabId;
        }

        /// <summary>Drop all session→tab mappings for a closed tab.</summary>
        public void UnregisterTabSessions(string tabId)
        {
            var keys = SessionIdToTabId.Where(kv => kv.Value == tabId).Select(kv => kv.Key).ToList();
            foreach (var k in keys) SessionIdToTabId.Remove(k);
        }

        /// <summary>Resolve the tab that owns a given session id, or null.</summary>
        public DrawingTab? FindTabBySessionId(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            if (!SessionIdToTabId.TryGetValue(sessionId!, out var tabId)) return null;
            DrawingTabs.TryGetValue(tabId, out var tab);
            return tab;
        }

        /// <summary>
        /// Switch the UI to an existing tab. Returns true if switched, false if already active or unknown.
        /// </summary>
        public bool SwitchToTab(string tabId)
        {
            if (ActiveTabKey == tabId) return false;
            if (!DrawingTabs.TryGetValue(tabId, out var tab)) return false;

            SaveCurrentTabState();
            RestoreTabState(tab);
            System.Diagnostics.Debug.WriteLine($"Switched to tab: {tab.DisplayName} ({DrawingTabs.Count} tabs open)");
            return true;
        }

        /// <summary>
        /// Create a new tab bound to <paramref name="drawingPath"/> (null = drawing-less),
        /// allocate its display name, insert it, and switch to it. Returns the new tab.
        /// </summary>
        public DrawingTab CreateTab(string? drawingPath)
        {
            SaveCurrentTabState();

            string displayName = AllocateDisplayName(drawingPath);
            var tab = new DrawingTab(drawingPath, displayName);
            DrawingTabs[tab.TabId] = tab;
            RestoreTabState(tab);
            System.Diagnostics.Debug.WriteLine($"Created tab: {displayName} ({DrawingTabs.Count} tabs open)");
            return tab;
        }

        /// <summary>
        /// Allocate a display name for a new tab. For drawing-bound tabs the base is
        /// the file name without extension; for drawing-less tabs the base is "Chat".
        /// First tab uses bare base, subsequent ones append " 1", " 2", … picking the
        /// lowest unused suffix among existing tabs that share the same base.
        /// </summary>
        public string AllocateDisplayName(string? drawingPath)
        {
            string baseName = string.IsNullOrEmpty(drawingPath)
                ? "Chat"
                : Path.GetFileNameWithoutExtension(drawingPath!);
            if (string.IsNullOrEmpty(baseName)) baseName = "Chat";

            // Collect existing display names sharing this base
            var pattern = new Regex(@"^" + Regex.Escape(baseName) + @"(?: (\d+))?$",
                RegexOptions.CultureInvariant);
            var usedSuffixes = new HashSet<int>();
            bool baseUsed = false;
            foreach (var existing in DrawingTabs.Values)
            {
                var m = pattern.Match(existing.DisplayName);
                if (!m.Success) continue;
                if (!m.Groups[1].Success) baseUsed = true;
                else if (int.TryParse(m.Groups[1].Value, out var n)) usedSuffixes.Add(n);
            }

            if (!baseUsed) return baseName;
            for (int i = 1; ; i++)
            {
                if (!usedSuffixes.Contains(i)) return baseName + " " + i;
            }
        }

        /// <summary>
        /// Find the most-recently-active tab bound to <paramref name="drawingPath"/>, or null.
        /// </summary>
        public DrawingTab? FindMostRecentTabFor(string drawingPath)
        {
            return DrawingTabs.Values
                .Where(t => string.Equals(t.DrawingPath, drawingPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.LastActiveAt)
                .FirstOrDefault();
        }

        /// <summary>
        /// Close a tab by id. Returns the next tab to restore, or null if no tabs remain.
        /// </summary>
        public DrawingTab? CloseDrawingTab(string tabId)
        {
            if (!DrawingTabs.ContainsKey(tabId))
                return null;

            DrawingTabs.Remove(tabId);

            if (ActiveTabKey == tabId)
            {
                var nextTab = DrawingTabs.Values
                    .OrderByDescending(t => t.LastActiveAt)
                    .FirstOrDefault();

                if (nextTab != null)
                {
                    ActiveTabKey = null; // Force restore
                    RestoreTabState(nextTab);
                }
                else
                {
                    ActiveTabKey = null;
                    ClearAllState();
                }

                System.Diagnostics.Debug.WriteLine($"Closed tab: {tabId} ({DrawingTabs.Count} tabs remaining)");
                return nextTab;
            }

            System.Diagnostics.Debug.WriteLine($"Closed tab: {tabId} ({DrawingTabs.Count} tabs remaining)");
            return DrawingTabs.TryGetValue(ActiveTabKey ?? "", out var current) ? current : null;
        }

        /// <summary>
        /// Reset all state to clean defaults.
        /// </summary>
        public void ClearAllState()
        {
            ConversationHtml.Clear();
            CurrentDrawingForSession = null;
            WelcomeShown = false;
            HasUserSentMessage = false;
            LastReportHtml = null;
            LastOperationsSummary = null;
            LastDwgJson = null;
            LastShpJson = null;
            ScopeCardHtmlStart = -1;
            CurrentFixPlan = null;
            LastFixResult = null;
            SessionIdToTabId.Clear();
        }

        /// <summary>
        /// Remove the scope card from the conversation HTML.
        /// </summary>
        public bool RemoveScopeCardFromChat()
        {
            if (ScopeCardHtmlStart >= 0 && ScopeCardHtmlStart < ConversationHtml.Length)
            {
                ConversationHtml.Length = ScopeCardHtmlStart;
                ScopeCardHtmlStart = -1;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Initialize the modular extraction services.
        /// </summary>
        public void InitializeExtractionServices()
        {
            if (ExtractionEngine != null)
                return;

            try
            {
                ExtractionEngine = new DataExtractionEngine();
                StateStore = new DrawingStateStore();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeExtractionServices error: {ex.Message}");
                ExtractionEngine = null;
                StateStore = null;
            }
        }
    }
}
