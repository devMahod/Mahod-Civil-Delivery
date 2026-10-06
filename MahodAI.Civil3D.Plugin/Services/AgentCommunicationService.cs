using System;
using System.Text.Json;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.ViewModels;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Services
{
    /// <summary>
    /// Manages WebSocket and REST communication with the MahodAI agent server.
    /// Handles session creation, file uploads, and connection management.
    /// Event subscriptions are NOT managed here — they stay in the code-behind
    /// because they update UI directly.
    /// </summary>
    public class AgentCommunicationService
    {
        private readonly ChatViewModel _vm;

        public AgentCommunicationService(ChatViewModel vm)
        {
            _vm = vm;
        }

        /// <summary>
        /// Initialize the agent service and WebSocket client.
        /// Returns true if initialization succeeded.
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                const string DEFAULT_API_KEY = "your-secure-api-key-here";
                var baseUrl = Config.PluginConstants.ResolveAgentApiUrl();
                var apiKey = Environment.GetEnvironmentVariable("MAHOD_AGENT_API_KEY") ?? DEFAULT_API_KEY;

                _vm.AgentService = new MahodAgentService(baseUrl, apiKey);

                if (!await _vm.AgentService.HealthCheckAsync())
                {
                    throw new Exception("שרת Agent לא זמין");
                }

                _vm.ToolExecutor = new Tools.ToolExecutor();

                // Session→document affinity for tool_calls (2026-08-03): resolve a
                // session id to its tab's bound drawing. The tab maps are only
                // mutated on the UI thread, which is also where the resolver runs
                // (Application.Idle) — no locking needed.
                Tools.ToolExecutor.SessionBindingResolver = sid =>
                {
                    var tab = _vm.FindTabBySessionId(sid);
                    return new Tools.SessionBinding(tab != null, tab?.DrawingPath);
                };

                _vm.AssistantInitialized = true;
                foreach (var t in _vm.DrawingTabs.Values) t.AgentSessionActive = false;
                _vm.CurrentDrawingForSession = null;

                return true;
            }
            catch (Exception ex)
            {
                _vm.AssistantInitialized = false;
                foreach (var t in _vm.DrawingTabs.Values) t.AgentSessionActive = false;
                System.Diagnostics.Debug.WriteLine("Agent init failed: " + ex);
                throw;
            }
        }

        /// <summary>
        /// Get the base URL for the agent service.
        /// </summary>
        public static string GetBaseUrl()
        {
            return Config.PluginConstants.ResolveAgentApiUrl();
        }

        /// <summary>
        /// Get the API key for the agent service.
        /// </summary>
        public static string GetApiKey()
        {
            const string DEFAULT_API_KEY = "your-secure-api-key-here";
            return Environment.GetEnvironmentVariable("MAHOD_AGENT_API_KEY") ?? DEFAULT_API_KEY;
        }

        /// <summary>
        /// Create a new WebSocket client. Does NOT wire up events — caller is responsible
        /// for subscribing to events since they need UI dispatcher access.
        /// </summary>
        public MahodWebSocketClient CreateWebSocketClient(string baseUrl, string apiKey)
        {
            var wsUrl = baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
            return new MahodWebSocketClient(wsUrl, apiKey);
        }

        /// <summary>
        /// Upload a large file via HTTP multipart instead of base64 WebSocket.
        /// Returns extracted text, or null on failure.
        /// </summary>
        public async Task<string?> UploadLargeFileAsync(string filePath, string? sessionId = null)
        {
            try
            {
                // Same default as every other path — see PluginConstants. The literal
                // that used to sit here sent file uploads to dev from a prod build.
                var agentUrl = Config.PluginConstants.ResolveAgentApiUrl();
                var apiKey = Environment.GetEnvironmentVariable("MAHOD_AGENT_API_KEY") ?? "mahod-dev-key-2026";
                // Strip a trailing /api so the route below remains absolute (the agent's
                // upload endpoint is /api/v1/upload at the host root).
                string baseHost = agentUrl.TrimEnd('/');
                if (baseHost.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                    baseHost = baseHost.Substring(0, baseHost.Length - 4);
                var uploadUrl = $"{baseHost}/api/v1/upload";

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

                using var form = new System.Net.Http.MultipartFormDataContent();

                var fileStream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                var streamContent = new System.Net.Http.StreamContent(fileStream);
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(streamContent, "file", System.IO.Path.GetFileName(filePath));

                if (!string.IsNullOrEmpty(sessionId))
                    form.Add(new System.Net.Http.StringContent(sessionId!), "session_id");

                var response = await httpClient.PostAsync(uploadUrl, form);

                fileStream.Dispose();

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"HTTP upload failed: {response.StatusCode} - {errorBody}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("extracted_text", out var textEl))
                {
                    return textEl.GetString();
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HTTP upload error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a lightweight session for chat without requiring a full analysis run.
        /// Extracts the drawing summary and registers it with the agent.
        /// The <paramref name="extractSummaryJson"/> function must run on the AutoCAD dispatcher thread.
        /// Returns true if session was created successfully.
        /// </summary>
        public async Task<bool> InitializeQuickSessionAsync(Func<string> extractSummaryJson)
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return false;

                string drawingName = doc.Name;

                // Always work on the CURRENT active tab. If none exists, create
                // a fresh tab bound to the active drawing.
                DrawingTab? targetTab = null;
                if (_vm.ActiveTabKey != null && _vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey, out var activeTab))
                    targetTab = activeTab;

                if (targetTab == null)
                {
                    targetTab = _vm.CreateTab(drawingName);
                }

                // Skip if THIS tab already has an active session.
                if (targetTab.AgentSessionActive && !string.IsNullOrEmpty(targetTab.SessionId))
                    return true;

                string json = extractSummaryJson();
                if (string.IsNullOrEmpty(json)) return false;

                _vm.LastDwgJson = json;
                targetTab.LastDwgJson = json;

                // Prefer WebSocket session-create — that's the session the streaming
                // pipelines run against. Fall back to REST when WS is offline.
                string? newSessionId = null;
                if (_vm.UseWebSocket && _vm.WsClient != null && _vm.WsClient.IsConnected)
                {
                    var summaryJson = JsonDocument.Parse(json);
                    var drawingId = Guid.NewGuid().ToString("N");
                    var ws = await _vm.WsClient.CreateSessionAsync(drawingId, drawingName, summaryJson.RootElement);
                    newSessionId = ws.SessionId;
                    System.Diagnostics.Debug.WriteLine($"Quick session created (WS): {newSessionId}");
                }
                else if (_vm.AgentService != null)
                {
                    var session = await _vm.AgentService.CreateSessionAsync(json, drawingName: drawingName);
                    newSessionId = session.SessionId;
                    System.Diagnostics.Debug.WriteLine($"Quick session created (REST): {newSessionId}");
                }
                else
                {
                    return false;
                }

                if (string.IsNullOrEmpty(newSessionId)) return false;

                targetTab.SessionId = newSessionId;
                targetTab.AgentSessionActive = true;
                _vm.RegisterSession(newSessionId!, targetTab.TabId);
                _vm.CurrentDrawingForSession = drawingName;

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeQuickSessionAsync failed: {ex.Message}");
                return false;
            }
        }
    }
}
