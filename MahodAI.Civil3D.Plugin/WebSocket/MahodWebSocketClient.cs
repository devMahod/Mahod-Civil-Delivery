using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.WebSocket
{
    /// <summary>
    /// WebSocket client for bidirectional communication with MahodAI Agent.
    /// Handles connection lifecycle, heartbeat, and automatic reconnection.
    /// </summary>
    public class MahodWebSocketClient : IDisposable
    {
        private ClientWebSocket? _webSocket;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly MessageDispatcher _dispatcher;

        private CancellationTokenSource? _connectionCts;
        private Task? _receiveTask;
        private Task? _heartbeatTask;

        private WebSocketConnectionState _state = WebSocketConnectionState.Disconnected;
        private string? _connectionId;

        // Thread-safety lock for connection operations
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private bool _isReconnecting = false;

        // Serializes disconnect teardown so overlapping drop-detections
        // (receive-loop close frame, receive-loop error, heartbeat timeout) run
        // the socket cleanup + reconnect exactly once.
        private readonly object _disconnectLock = new();
        private bool _isHandlingDisconnect = false;

        // Set once the client is disposed so the indefinite reconnect loop and
        // any in-flight connect attempt stop touching disposed primitives.
        private volatile bool _disposed = false;

        // Reconnection settings. Retries run indefinitely with capped backoff —
        // there is deliberately no maximum attempt count. A >3 min outage (a
        // laptop that slept, a backend restart) must still self-heal instead of
        // forcing the engineer to restart Civil 3D.
        private int _reconnectAttempts = 0;
        private const int InitialReconnectDelayMs = 1000;
        private const int MaxReconnectDelayMs = 30000;

        // Heartbeat settings
        private const int HeartbeatIntervalMs = 30000;
        private DateTime _lastHeartbeatSent;
        private DateTime _lastHeartbeatReceived;

        // Send lock to prevent concurrent WebSocket sends
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // Maximum pending messages to prevent unbounded queue growth
        private const int MaxPendingMessages = 1000;

        // Message queue for offline operation
        private readonly ConcurrentQueue<WebSocketMessage> _pendingMessages = new();

        // Pending requests awaiting responses
        private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketMessage>> _pendingRequests = new();

        // JSON serialization options — the ONE canonical protocol configuration
        // (see WebSocketJson). Shared with MessageDispatcher and the contract tests.
        private static readonly JsonSerializerOptions JsonOptions = WebSocketJson.Options;

        #region Events

        /// <summary>
        /// Raised when connection state changes.
        /// </summary>
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        /// <summary>
        /// Raised when a tool call is received from the agent.
        /// </summary>
        public event EventHandler<ToolCallReceivedEventArgs>? ToolCallReceived;

        /// <summary>
        /// Raised when a streaming token is received.
        /// </summary>
        public event EventHandler<StreamTokenReceivedEventArgs>? StreamTokenReceived;

        /// <summary>
        /// Raised when stream starts.
        /// </summary>
        public event EventHandler<StreamStartedEventArgs>? StreamStarted;

        /// <summary>
        /// Raised when stream ends.
        /// </summary>
        public event EventHandler<StreamEndedEventArgs>? StreamEnded;

        /// <summary>
        /// Raised when a status update is received.
        /// </summary>
        public event EventHandler<StatusReceivedEventArgs>? StatusReceived;

        /// <summary>
        /// Raised when an error occurs.
        /// </summary>
        public event EventHandler<WebSocketErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Raised when the agent acknowledges per-message feedback (v1.14).
        /// </summary>
        public event EventHandler<FeedbackAckReceivedEventArgs>? FeedbackAckReceived;

        /// <summary>
        /// Raised when a fix plan is received from the agent.
        /// </summary>
        public event EventHandler<FixPlanReceivedEventArgs>? FixPlanReceived;

        /// <summary>
        /// Raised when fix execution results are received.
        /// </summary>
        public event EventHandler<FixResultReceivedEventArgs>? FixResultReceived;

        /// <summary>
        /// Raised when post-fix verification results are received.
        /// Fired after <see cref="FixResultReceived"/> so the UI can merge
        /// execute + verify into the same card.
        /// </summary>
        public event EventHandler<FixVerifyResultReceivedEventArgs>? FixVerifyResultReceived;

        /// <summary>
        /// Raised when post-fix re-validation results are received.
        /// Fired after <see cref="FixVerifyResultReceived"/> and summarizes
        /// which original violations are now clean, still open, or newly
        /// introduced. The fix card appends this as an additional section.
        /// </summary>
        public event EventHandler<PostFixValidationReceivedEventArgs>? PostFixValidationReceived;

        /// <summary>
        /// Raised when a check_start event is received (per-object analysis progress).
        /// </summary>
        public event EventHandler<CheckStartReceivedEventArgs>? CheckStartReceived;

        /// <summary>
        /// Raised when a check_result event is received.
        /// </summary>
        public event EventHandler<CheckResultReceivedEventArgs>? CheckResultReceived;

        /// <summary>
        /// Raised when analysis_complete event is received.
        /// </summary>
        public event EventHandler<AnalysisCompleteReceivedEventArgs>? AnalysisCompleteReceived;

        /// <summary>
        /// Raised when a message is received (for debugging/logging).
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        #endregion

        #region Properties

        /// <summary>
        /// Current connection state.
        /// </summary>
        public WebSocketConnectionState State => _state;

        /// <summary>
        /// Connection ID assigned by the server.
        /// </summary>
        public string? ConnectionId => _connectionId;

        /// <summary>
        /// Whether the client is connected and authenticated.
        /// </summary>
        public bool IsConnected => _state == WebSocketConnectionState.Authenticated ||
                                    _state == WebSocketConnectionState.SessionActive;

        #endregion

        /// <summary>
        /// Initializes a new WebSocket client.
        /// </summary>
        /// <param name="baseUrl">Base URL (http/https will be converted to ws/wss)</param>
        /// <param name="apiKey">API key for authentication</param>
        public MahodWebSocketClient(string baseUrl, string apiKey)
        {
            _baseUrl = ConvertToWebSocketUrl(baseUrl);
            _apiKey = apiKey;
            _dispatcher = new MessageDispatcher(this);
        }

        private static string ConvertToWebSocketUrl(string url)
        {
            url = url.TrimEnd('/');
            // Drop a trailing /api so the WebSocket endpoint resolves to /ws at
            // the host root (the agent registers /ws there, not under /api/ws).
            if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 4);
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + url.Substring(8) + "/ws";
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + url.Substring(7) + "/ws";
            if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + url + "/ws";
            // Already a ws/wss URL — append /ws if missing.
            return url.EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ? url : url + "/ws";
        }

        #region Connection Management

        /// <summary>
        /// Connects to the WebSocket server and authenticates. On failure this
        /// does NOT throw; it starts a background reconnect loop that retries
        /// until the server is reachable (or the client is disposed / closed),
        /// so the caller (UI init) keeps WebSocket mode enabled either way.
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return;

            // Already connected — nothing to do.
            if (IsConnected)
            {
                System.Diagnostics.Debug.WriteLine($"ConnectAsync: Already in state {_state}, skipping");
                return;
            }

            bool connected = await TryConnectOnceAsync(cancellationToken);
            if (!connected && !_disposed && _state != WebSocketConnectionState.Closed)
            {
                // Fire-and-forget the reconnect loop so the caller isn't blocked
                // on the (now unbounded) retry schedule. AttemptReconnectAsync is
                // self-guarded, so a second call while one is running is a no-op.
                _ = AttemptReconnectAsync();
            }
        }

        /// <summary>
        /// Performs a single connect + authenticate attempt, serialized through
        /// <see cref="_connectionLock"/>. Returns true on success, false on
        /// failure. Never triggers reconnection itself — callers own that so the
        /// reconnect loop stays flat (no ConnectAsync ↔ reconnect recursion,
        /// which would grow the async call chain unboundedly under indefinite
        /// retry).
        /// </summary>
        private async Task<bool> TryConnectOnceAsync(CancellationToken cancellationToken)
        {
            if (_disposed) return false;

            // Non-blocking: if another connect attempt already holds the lock,
            // don't stack a second one — report the current connection status.
            if (!await _connectionLock.WaitAsync(0))
            {
                System.Diagnostics.Debug.WriteLine("TryConnectOnceAsync: Already connecting, skipping");
                return IsConnected;
            }

            try
            {
                // Someone may have authenticated while we waited on the lock.
                // (Only Authenticated/SessionActive count — a bare Connected is a
                // transient pre-auth state we must not treat as done.)
                if (IsConnected)
                {
                    return true;
                }

                // Dispose any leftover socket from a prior failed/dropped attempt.
                // Done here, under the lock and only when we're NOT connected, so
                // the reconnect loop can never dispose a socket a concurrent
                // ConnectAsync just brought up.
                if (_webSocket != null)
                {
                    try { _webSocket.Dispose(); } catch { }
                    _webSocket = null;
                }

                System.Diagnostics.Debug.WriteLine("TryConnectOnceAsync: Starting connection...");

                SetState(WebSocketConnectionState.Connecting);
                _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("X-API-Key", _apiKey);

                await _webSocket.ConnectAsync(new Uri(_baseUrl), _connectionCts.Token);
                SetState(WebSocketConnectionState.Connected);
                System.Diagnostics.Debug.WriteLine("TryConnectOnceAsync: WebSocket connected");

                // Start receive loop
                _receiveTask = ReceiveLoopAsync(_connectionCts.Token);

                // Send authentication
                await AuthenticateAsync(_connectionCts.Token);
                System.Diagnostics.Debug.WriteLine("TryConnectOnceAsync: Authenticated");

                // Establish a fresh heartbeat baseline. Without this, a reconnect
                // triggered BY a heartbeat timeout inherits a >60s-stale
                // _lastHeartbeatReceived, so the new connection's first heartbeat
                // check would false-fire before its first ack arrives — flapping
                // the connection every ~30s.
                _lastHeartbeatReceived = DateTime.UtcNow;
                _lastHeartbeatSent = DateTime.UtcNow;

                // Start heartbeat
                _heartbeatTask = HeartbeatLoopAsync(_connectionCts.Token);

                // Process any pending messages
                await ProcessPendingMessagesAsync();

                _reconnectAttempts = 0;
                System.Diagnostics.Debug.WriteLine("TryConnectOnceAsync: Connection complete");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TryConnectOnceAsync: Failed - {ex.Message}");
                // Tear down the half-open connection so the next attempt starts
                // clean. This matters on the auth-failure / auth-timeout path:
                // the TCP socket connected and a receive loop was already started,
                // so without this the state would latch at the transient Connected
                // (which the UI reads as "connected") and the orphan receive loop
                // would linger. Cancel + Abort unblock that loop cleanly (OCE);
                // the socket itself is disposed by the next attempt's cleanup.
                try { _connectionCts?.Cancel(); } catch { }
                try { _webSocket?.Abort(); } catch { }
                SetState(WebSocketConnectionState.Disconnected);
                // Never latch Error — we retry indefinitely; the reconnect loop
                // owns the visible Reconnecting state.
                OnError(ErrorCodes.ServerError, "Connection failed", ex.Message);
                return false;
            }
            finally
            {
                // Guard against a disposed semaphore if Dispose() raced in.
                try { _connectionLock.Release(); } catch { }
            }
        }

        /// <summary>
        /// Disconnects from the WebSocket server.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_state == WebSocketConnectionState.Closed)
            {
                return;
            }

            // Mark closed FIRST so the (now indefinite) reconnect loop and any
            // in-flight HandleDisconnectAsync observe the intentional close and
            // stop, instead of racing a fresh reconnect. Note we do NOT early-out
            // on the transient Disconnected state — that occurs mid-reconnect, and
            // an explicit Disconnect must still cancel that reconnect.
            SetState(WebSocketConnectionState.Closed);

            _connectionCts?.Cancel();

            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    // Send disconnect message
                    var disconnectMsg = CreateMessage(MessageTypes.Disconnect, null);
                    await SendMessageInternalAsync(disconnectMsg);

                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client disconnecting",
                        CancellationToken.None);
                }
            }
            catch
            {
                // Ignore errors during disconnect
            }

            // State was already set to Closed above.
            _connectionId = null;
        }

        private async Task AuthenticateAsync(CancellationToken ct)
        {
            var payload = new ConnectPayload
            {
                ApiKey = _apiKey,
                ClientVersion = GetClientVersion(),
                ClientType = Constants.AppConstants.ClientTypePlugin
            };

            var message = CreateMessage(MessageTypes.Connect, payload);
            var response = await SendAndWaitAsync(message, TimeSpan.FromSeconds(30), ct);

            if (response.Type == MessageTypes.ConnectAck)
            {
                var ackPayload = DeserializePayload<ConnectAckPayload>(response.Payload);
                if (ackPayload?.Success == true)
                {
                    _connectionId = ackPayload.ConnectionId;
                    SetState(WebSocketConnectionState.Authenticated);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Authentication failed: {ackPayload?.Error ?? "Unknown error"}");
                }
            }
            else if (response.Type == MessageTypes.Error)
            {
                var errorPayload = DeserializePayload<ErrorPayload>(response.Payload);
                throw new InvalidOperationException(
                    $"Authentication error: {errorPayload?.Message ?? "Unknown error"}");
            }
        }

        private async Task AttemptReconnectAsync()
        {
            // Only one reconnect loop at a time.
            if (_isReconnecting)
            {
                System.Diagnostics.Debug.WriteLine("AttemptReconnectAsync: Already reconnecting, skipping");
                return;
            }

            if (_disposed || _state == WebSocketConnectionState.Closed)
            {
                System.Diagnostics.Debug.WriteLine("AttemptReconnectAsync: Closed/disposed, not reconnecting");
                return;
            }

            _isReconnecting = true;
            SetState(WebSocketConnectionState.Reconnecting);

            try
            {
                // Retry indefinitely. There is deliberately no attempt cap: a
                // backend restart or a laptop that slept longer than the old
                // ~3 min budget must still recover on its own. We only stop when
                // we reconnect, the client is disposed, or the connection is
                // closed intentionally.
                while (!_disposed && _state != WebSocketConnectionState.Closed)
                {
                    _reconnectAttempts++;

                    // Exponential backoff, capped at MaxReconnectDelayMs. Clamp
                    // the exponent BEFORE the cast so the now-unbounded attempt
                    // counter can never overflow int (2^31 ms) and produce a
                    // negative delay that would throw out of Task.Delay.
                    int exponent = Math.Min(_reconnectAttempts - 1, 20);
                    double backoffMs = InitialReconnectDelayMs * Math.Pow(2, exponent);
                    int delay = (int)Math.Min(backoffMs, MaxReconnectDelayMs);

                    System.Diagnostics.Debug.WriteLine($"AttemptReconnectAsync: Attempt {_reconnectAttempts}, waiting {delay}ms");
                    await Task.Delay(delay);

                    if (_disposed || _state == WebSocketConnectionState.Closed)
                        break;

                    bool reconnected;
                    try
                    {
                        reconnected = await TryConnectOnceAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // TryConnectOnceAsync swallows its own failures; this only
                        // guards a stray exception (e.g. a disposed semaphore on a
                        // Dispose race) so it can never kill the loop.
                        System.Diagnostics.Debug.WriteLine($"AttemptReconnectAsync: attempt threw - {ex.Message}");
                        reconnected = false;
                    }

                    if (reconnected)
                    {
                        System.Diagnostics.Debug.WriteLine("AttemptReconnectAsync: Reconnected");
                        return;  // _reconnectAttempts was reset inside TryConnectOnceAsync
                    }

                    // Still down — reflect the retrying state and back off again.
                    if (!_disposed && _state != WebSocketConnectionState.Closed)
                        SetState(WebSocketConnectionState.Reconnecting);
                }
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Creates a new analysis session.
        /// </summary>
        public async Task<SessionCreatedPayload> CreateSessionAsync(
            string drawingId,
            string drawingName,
            object summary,
            CancellationToken ct = default)
        {
            EnsureConnected();

            var payload = new SessionCreatePayload
            {
                DrawingId = drawingId,
                DrawingName = drawingName,
                Summary = summary
            };

            var message = CreateMessage(MessageTypes.SessionCreate, payload);
            var response = await SendAndWaitAsync(message, TimeSpan.FromMinutes(2), ct);

            if (response.Type == MessageTypes.SessionCreated)
            {
                var sessionPayload = DeserializePayload<SessionCreatedPayload>(response.Payload);
                if (sessionPayload != null)
                {
                    SetState(WebSocketConnectionState.SessionActive);
                    return sessionPayload;
                }
            }

            throw new InvalidOperationException("Failed to create session");
        }

        #endregion

        #region Chat & Analysis

        /// <summary>
        /// Sends a chat message and returns the stream ID.
        /// Response tokens will be delivered via StreamTokenReceived event.
        /// </summary>
        /// <param name="sessionId">The originating tab's session id. Required so the
        /// agent routes the message to the correct session even when other tabs are
        /// streaming on the same connection.</param>
        public async Task<string> ChatAsync(string sessionId, string content, ChatContext? context = null, CancellationToken ct = default, List<ChatAttachment>? attachments = null)
        {
            EnsureSessionActive();
            EnsureSessionId(sessionId);

            var payload = new ChatPayload
            {
                Content = content,
                Context = context,
                // Keep null rather than an empty list: WhenWritingNull then drops
                // the key entirely and an image-less chat stays v1.14-shaped.
                Attachments = (attachments != null && attachments.Count > 0) ? attachments : null
            };

            var message = CreateMessage(MessageTypes.Chat, payload);
            message.SessionId = sessionId;

            await SendMessageAsync(message);

            // Return correlation ID - caller can use this to track the response stream
            return message.Id;
        }

        /// <summary>
        /// Sends an analysis request.
        /// </summary>
        /// <param name="sessionId">The originating tab's session id.</param>
        /// <param name="summary">
        /// Fresh drawing summary (JSON). When provided, the backend overwrites
        /// the stored session summary before running the pipeline so roads
        /// added after the plugin opened are seen. Pass null to reuse the
        /// summary stored on session_create.
        /// </param>
        public async Task<string> AnalyzeAsync(
            string sessionId,
            string? instructions = null,
            List<string>? focusAreas = null,
            List<string>? selectedAlignmentNames = null,
            List<string>? selectedProfileNames = null,
            Dictionary<string, string>? roadTypeOverrides = null,
            Dictionary<string, string>? roadClassificationOverrides = null,
            Dictionary<string, string>? crossSectionOverrides = null,
            string? topography = null,
            object? summary = null,
            CancellationToken ct = default)
        {
            EnsureSessionActive();
            EnsureSessionId(sessionId);

            var payload = new AnalyzePayload
            {
                Instructions = instructions,
                FocusAreas = focusAreas,
                SelectedAlignmentNames = selectedAlignmentNames,
                SelectedProfileNames = selectedProfileNames,
                RoadTypeOverrides = roadTypeOverrides,
                RoadClassificationOverrides = roadClassificationOverrides,
                CrossSectionOverrides = crossSectionOverrides,
                Topography = topography,
                Summary = summary
            };

            var message = CreateMessage(MessageTypes.Analyze, payload);
            message.SessionId = sessionId;

            await SendMessageAsync(message);
            return message.Id;
        }

        /// <summary>
        /// Sends a file upload for text extraction and processing.
        /// </summary>
        public async Task<string> SendFileUploadAsync(string sessionId, string filename, string base64Data, string? userMessage = null)
        {
            EnsureSessionActive();
            EnsureSessionId(sessionId);

            var payload = new FileUploadPayload
            {
                Filename = filename,
                Data = base64Data,
                Message = userMessage
            };

            var message = CreateMessage(MessageTypes.FileUpload, payload);
            message.SessionId = sessionId;

            await SendMessageAsync(message);
            return message.Id;
        }

        #endregion

        #region Tool Results

        /// <summary>
        /// Sends the result of a tool execution back to the agent.
        /// </summary>
        /// <param name="sessionId">The originating session id (taken from the
        /// tool_call envelope so we send the result back on the same session
        /// even if the user has since switched tabs).</param>
        public async Task SendToolResultAsync(
            string sessionId,
            string toolCallId,
            bool success,
            object? result,
            ToolError? error = null,
            long executionTimeMs = 0,
            bool cached = false)
        {
            var payload = new ToolResultPayload
            {
                ToolCallId = toolCallId,
                Success = success,
                Result = result,
                Error = error,
                ExecutionTimeMs = executionTimeMs,
                Cached = cached
            };

            var message = CreateMessage(MessageTypes.ToolResult, payload);
            message.SessionId = sessionId;
            message.CorrelationId = toolCallId;

            await SendMessageAsync(message);
        }

        /// <summary>
        /// Sends a fully-populated tool result payload back to the agent,
        /// preserving the P0-01 outcome contract fields
        /// (outcome / hard_gates_passed / requires_engineer_approval / violations)
        /// that the field-by-field overload cannot carry.
        /// </summary>
        public async Task SendToolResultAsync(string sessionId, ToolResultPayload payload)
        {
            var message = CreateMessage(MessageTypes.ToolResult, payload);
            message.SessionId = sessionId;
            message.CorrelationId = payload.ToolCallId;

            await SendMessageAsync(message);
        }

        /// <summary>
        /// Asks the agent to cancel an in-flight generation by stream id.
        /// Best-effort: silently no-ops if the connection is dropped.
        /// </summary>
        public async Task CancelStreamAsync(string? sessionId, string streamId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(streamId)) return;
            if (!IsConnected) return;

            var payload = new CancelStreamPayload { StreamId = streamId };
            var message = CreateMessage(MessageTypes.CancelStream, payload);
            message.SessionId = sessionId;
            try
            {
                await SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] CancelStreamAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends the engineer's feedback on one assistant message (protocol v1.14):
        /// a reaction (1 = 👍, -1 = 👎, null = none) and/or free text. The agent
        /// persists it to Supabase and answers <c>feedback_ack</c>.
        /// </summary>
        public async Task SendMessageFeedbackAsync(string? sessionId, MessageFeedbackPayload payload)
        {
            if (payload == null) return;
            if (string.IsNullOrEmpty(sessionId)) return;

            var message = CreateMessage(MessageTypes.MessageFeedback, payload);
            message.SessionId = sessionId;

            if (!IsConnected)
            {
                // Feedback is not worth failing the UI over, but it IS worth
                // keeping: queue it like a drawing event so a brief drop doesn't
                // silently discard what the engineer wrote.
                if (_pendingMessages.Count < MaxPendingMessages)
                    _pendingMessages.Enqueue(message);
                else
                    System.Diagnostics.Debug.WriteLine("[MahodAI] Pending message queue full, dropping message feedback");
                return;
            }

            try
            {
                await SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SendMessageFeedbackAsync failed: {ex.Message}");
            }
        }

        #endregion

        #region Events (Drawing Changes)

        /// <summary>
        /// Sends a drawing change event to the agent. <paramref name="sessionId"/>
        /// must come from the active tab; null skips delivery.
        /// </summary>
        public async Task SendDrawingEventAsync(string? sessionId, DrawingEventPayload eventPayload)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            var message = CreateMessage(MessageTypes.Event, eventPayload);
            message.SessionId = sessionId;

            if (!IsConnected)
            {
                if (_pendingMessages.Count < MaxPendingMessages)
                    _pendingMessages.Enqueue(message);
                else
                    System.Diagnostics.Debug.WriteLine("[MahodAI] Pending message queue full, dropping drawing event");
                return;
            }

            await SendMessageAsync(message);
        }

        #endregion

        #region Fix Workflow

        /// <summary>
        /// Sends a fix request to the agent.
        /// </summary>
        public async Task SendFixRequestAsync(string sessionId, string[]? recommendationIds = null, string? instructions = null)
        {
            EnsureSessionActive();
            EnsureSessionId(sessionId);

            var payload = new FixRequestPayload
            {
                RecommendationIds = recommendationIds?.ToList(),
                UserInstructions = instructions
            };

            var message = CreateMessage(MessageTypes.FixRequest, payload);
            message.SessionId = sessionId;

            await SendMessageAsync(message);
        }

        /// <summary>
        /// Sends a fix approval decision to the agent. Optional <paramref name="editedItems"/>
        /// carries per-item tool-param overrides captured from the WebView UI.
        /// </summary>
        public async Task SendFixApprovalAsync(
            string sessionId,
            string planId,
            string decision,
            string? userMessage = null,
            List<string>? selectedItemIds = null,
            List<FixItemEdit>? editedItems = null)
        {
            EnsureSessionActive();
            EnsureSessionId(sessionId);

            var payload = new FixApprovalPayload
            {
                PlanId = planId,
                Decision = decision,
                UserMessage = userMessage,
                SelectedItemIds = selectedItemIds,
                EditedItems = editedItems ?? new List<FixItemEdit>()
            };

            var message = CreateMessage(MessageTypes.FixApproval, payload);
            message.SessionId = sessionId;

            await SendMessageAsync(message);
        }

        #endregion

        #region Message Handling

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            // Accumulate RAW BYTES across partial receives and decode once at
            // EndOfMessage. Decoding each chunk separately splits multi-byte
            // UTF-8 sequences (e.g. Hebrew) at the 8 KB frame boundary and
            // turns them into U+FFFD replacement characters.
            var messageBytes = new System.IO.MemoryStream();

            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleDisconnectAsync(fromReceiveLoop: true);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBytes.Write(buffer, 0, result.Count);

                        if (result.EndOfMessage)
                        {
                            var json = Encoding.UTF8.GetString(
                                messageBytes.GetBuffer(), 0, (int)messageBytes.Length);
                            messageBytes.SetLength(0);

                            await ProcessReceivedMessageAsync(json);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    OnError(ErrorCodes.ServerError, "WebSocket error", ex.Message);
                    await HandleDisconnectAsync(fromReceiveLoop: true);
                    break;
                }
            }
        }

        private async Task ProcessReceivedMessageAsync(string json)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebSocketMessage>(json, JsonOptions);
                if (message == null) return;

                // Invoke general message received event
                MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));

                // Check if this is a response to a pending request
                if (message.CorrelationId != null &&
                    _pendingRequests.TryRemove(message.CorrelationId, out var tcs))
                {
                    tcs.TrySetResult(message);
                    return;
                }

                // Dispatch to appropriate handler
                await _dispatcher.DispatchAsync(message);
            }
            catch (JsonException ex)
            {
                OnError(ErrorCodes.InvalidMessage, "Failed to parse message", ex.Message);
            }
        }

        /// <param name="fromReceiveLoop">
        /// True when called from inside <see cref="ReceiveLoopAsync"/> (the loop
        /// breaks right after). We must NOT await the receive task in that case,
        /// or the loop would deadlock waiting on itself.
        /// </param>
        private async Task HandleDisconnectAsync(bool fromReceiveLoop = false)
        {
            // Several paths can detect the same drop (receive-loop close frame,
            // receive-loop exception, heartbeat timeout). Dedupe so only the
            // first runs teardown + reconnect; the rest return immediately. The
            // guard is held across the reconnect so a stale detection can't tear
            // down a freshly re-established socket.
            lock (_disconnectLock)
            {
                if (_isHandlingDisconnect ||
                    _disposed ||
                    _state == WebSocketConnectionState.Closed)
                {
                    return;
                }
                _isHandlingDisconnect = true;
            }

            try
            {
                SetState(WebSocketConnectionState.Disconnected);

                // Capture the doomed connection primitives and tear them down
                // deterministically before a new socket is created. Without this
                // the old ClientWebSocket + its ReceiveLoopAsync survive the
                // reconnect: on the heartbeat-timeout path the socket is still
                // Open and the old loop stays parked in ReceiveAsync, so once a
                // new socket is swapped into the shared _webSocket field there
                // are TWO receive loops racing on it (duplicated/interleaved
                // message handling).
                var oldCts = _connectionCts;
                var oldSocket = _webSocket;
                var oldReceiveTask = _receiveTask;

                // 1. Cancel the token so the parked ReceiveAsync (and the
                //    heartbeat delay) wake up — ReceiveAsync throws
                //    OperationCanceledException, which the loop catches cleanly.
                try { oldCts?.Cancel(); } catch { }

                // 2. Abort to force-unblock a receive that isn't honoring the
                //    token. (Dispose is deferred to step 4 — disposing while a
                //    ReceiveAsync is still in flight throws ObjectDisposedException
                //    out of the loop instead of a clean cancellation.)
                try { oldSocket?.Abort(); } catch { }

                // 3. Drain the old receive loop so it is fully gone before a new
                //    one starts. Skip when we ARE that loop (fromReceiveLoop);
                //    the small timeout is a safety cap in case it doesn't unwind.
                if (!fromReceiveLoop && oldReceiveTask != null)
                {
                    try { await Task.WhenAny(oldReceiveTask, Task.Delay(5000)); }
                    catch { }
                }

                // 4. Now safe to dispose the socket (the loop is out of ReceiveAsync).
                try { oldSocket?.Dispose(); } catch { }

                // Fail all pending requests.
                foreach (var kvp in _pendingRequests)
                {
                    if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
                    {
                        tcs.TrySetException(new InvalidOperationException("Connection lost"));
                    }
                }

                // Reconnect unless the connection was closed intentionally or the
                // client is being disposed.
                if (!_disposed && _state != WebSocketConnectionState.Closed)
                {
                    await AttemptReconnectAsync();
                }
            }
            finally
            {
                lock (_disconnectLock)
                {
                    _isHandlingDisconnect = false;
                }
            }
        }

        #endregion

        #region Heartbeat

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    await Task.Delay(HeartbeatIntervalMs, ct);

                    var payload = new HeartbeatPayload();
                    var message = CreateMessage(MessageTypes.Heartbeat, payload);

                    _lastHeartbeatSent = DateTime.UtcNow;
                    await SendMessageAsync(message);

                    // Check for heartbeat timeout (no response in 60 seconds)
                    if ((DateTime.UtcNow - _lastHeartbeatReceived).TotalSeconds > 60 &&
                        _lastHeartbeatReceived > DateTime.MinValue)
                    {
                        await HandleDisconnectAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore heartbeat errors
                }
            }
        }

        internal void OnHeartbeatAckReceived()
        {
            _lastHeartbeatReceived = DateTime.UtcNow;
        }

        #endregion

        #region Internal Event Invocation

        internal void OnToolCallReceived(ToolCallPayload payload, string? sessionId)
        {
            ToolCallReceived?.Invoke(this, new ToolCallReceivedEventArgs(payload, sessionId));
        }

        internal void OnStreamStarted(StreamStartPayload payload, string? sessionId)
        {
            StreamStarted?.Invoke(this, new StreamStartedEventArgs(payload, sessionId));
        }

        internal void OnStreamTokenReceived(StreamTokenPayload payload, string? sessionId)
        {
            StreamTokenReceived?.Invoke(this, new StreamTokenReceivedEventArgs(payload, sessionId));
        }

        internal void OnStreamEnded(StreamEndPayload payload, string? sessionId)
        {
            StreamEnded?.Invoke(this, new StreamEndedEventArgs(payload, sessionId));
        }

        internal void OnStatusReceived(StatusPayload payload, string? sessionId)
        {
            StatusReceived?.Invoke(this, new StatusReceivedEventArgs(payload, sessionId));
        }

        internal void OnError(string code, string message, string? details)
        {
            ErrorOccurred?.Invoke(this, new WebSocketErrorEventArgs(code, message, details));
        }

        internal void OnFeedbackAckReceived(FeedbackAckPayload payload, string? sessionId)
        {
            FeedbackAckReceived?.Invoke(this, new FeedbackAckReceivedEventArgs(payload, sessionId));
        }

        internal void OnFixPlanReceived(FixPlanPayload payload, string? sessionId)
        {
            FixPlanReceived?.Invoke(this, new FixPlanReceivedEventArgs(payload, sessionId));
        }

        internal void OnFixResultReceived(FixResultPayload payload, string? sessionId)
        {
            FixResultReceived?.Invoke(this, new FixResultReceivedEventArgs(payload, sessionId));
        }

        internal void OnFixVerifyResultReceived(FixVerifyResultPayload payload, string? sessionId)
        {
            FixVerifyResultReceived?.Invoke(this, new FixVerifyResultReceivedEventArgs(payload, sessionId));
        }

        internal void OnPostFixValidationReceived(PostFixValidationPayload payload, string? sessionId)
        {
            PostFixValidationReceived?.Invoke(this, new PostFixValidationReceivedEventArgs(payload, sessionId));
        }

        internal void OnCheckStartReceived(CheckStartPayload payload, string? sessionId)
        {
            CheckStartReceived?.Invoke(this, new CheckStartReceivedEventArgs(payload, sessionId));
        }

        internal void OnCheckResultReceived(CheckResultPayload payload, string? sessionId)
        {
            CheckResultReceived?.Invoke(this, new CheckResultReceivedEventArgs(payload, sessionId));
        }

        internal void OnAnalysisCompleteReceived(AnalysisCompletePayload payload, string? sessionId)
        {
            AnalysisCompleteReceived?.Invoke(this, new AnalysisCompleteReceivedEventArgs(payload, sessionId));
        }

        #endregion

        #region Helpers

        private WebSocketMessage CreateMessage(string type, object? payload)
        {
            // SessionId is intentionally not set here. Each caller stamps it
            // explicitly with the session id of the originating tab so multi-tab
            // sends route to the correct session on the agent side.
            var message = new WebSocketMessage { Type = type };

            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                message.Payload = JsonSerializer.Deserialize<JsonElement>(json);
            }

            return message;
        }

        private async Task SendMessageAsync(WebSocketMessage message)
        {
            if (!IsConnected)
            {
                // User activity while offline: collapse the reconnect backoff so
                // the next attempt fires on the short end of the schedule rather
                // than sitting at the 30s cap after a long outage.
                _reconnectAttempts = 0;

                if (_pendingMessages.Count < MaxPendingMessages)
                    _pendingMessages.Enqueue(message);
                else
                    System.Diagnostics.Debug.WriteLine("[MahodAI] Pending message queue full, dropping message");
                return;
            }

            await SendMessageInternalAsync(message);
        }

        private async Task SendMessageInternalAsync(WebSocketMessage message)
        {
            if (_webSocket?.State != WebSocketState.Open)
                return;

            await _sendLock.WaitAsync();
            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                    return;

                var json = JsonSerializer.Serialize(message, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    _connectionCts?.Token ?? CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task<WebSocketMessage> SendAndWaitAsync(
            WebSocketMessage message,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<WebSocketMessage>();
            message.CorrelationId = message.Id;

            _pendingRequests[message.Id] = tcs;

            await SendMessageInternalAsync(message);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, linkedCts.Token));
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                throw new TimeoutException("Request timed out");
            }
            finally
            {
                _pendingRequests.TryRemove(message.Id, out _);
            }
        }

        private async Task ProcessPendingMessagesAsync()
        {
            while (_pendingMessages.TryDequeue(out var message))
            {
                await SendMessageAsync(message);
            }
        }

        private void SetState(WebSocketConnectionState newState)
        {
            var oldState = _state;
            _state = newState;

            if (oldState != newState)
            {
                ConnectionStateChanged?.Invoke(this,
                    new ConnectionStateChangedEventArgs(oldState, newState));
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("WebSocket is not connected");
            }
        }

        private void EnsureSessionActive()
        {
            // Accept Authenticated OR SessionActive — same semantics as IsConnected.
            // "Authenticated" means the WS is connected and the server knows who we are;
            // if no session_id has been created yet, the backend's ensure_chat_session
            // will auto-create a fallback when the first message arrives, so this is
            // a safe state to send from. Throwing "No active session" here produced the
            // user-visible error after reconnects where the state briefly sits at
            // Authenticated before transitioning back to SessionActive.
            if (_state != WebSocketConnectionState.Authenticated &&
                _state != WebSocketConnectionState.SessionActive)
            {
                throw new InvalidOperationException(
                    $"WebSocket not ready to send (state={_state}). Reconnect and try again.");
            }
        }

        private static void EnsureSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new InvalidOperationException(
                    "sessionId is required — each tab owns its session id and must pass it on every send.");
            }
        }

        private static T? DeserializePayload<T>(JsonElement? payload) where T : class
        {
            if (payload == null) return null;
            return JsonSerializer.Deserialize<T>(payload.Value.GetRawText(), JsonOptions);
        }

        private static string GetClientVersion()
        {
            return typeof(MahodWebSocketClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // Signal first so the indefinite reconnect loop / any in-flight
            // connect attempt bail out before we tear the primitives down.
            _disposed = true;
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _webSocket?.Dispose();
            _connectionLock?.Dispose();
            _sendLock?.Dispose();
        }

        #endregion
    }

    #region Event Args

    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public WebSocketConnectionState OldState { get; }
        public WebSocketConnectionState NewState { get; }

        public ConnectionStateChangedEventArgs(WebSocketConnectionState oldState, WebSocketConnectionState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    public class ToolCallReceivedEventArgs : EventArgs
    {
        public ToolCallPayload ToolCall { get; }
        public string? SessionId { get; }

        public ToolCallReceivedEventArgs(ToolCallPayload toolCall, string? sessionId = null)
        {
            ToolCall = toolCall;
            SessionId = sessionId;
        }
    }

    public class StreamTokenReceivedEventArgs : EventArgs
    {
        public StreamTokenPayload Token { get; }
        public string? SessionId { get; }

        public StreamTokenReceivedEventArgs(StreamTokenPayload token, string? sessionId = null)
        {
            Token = token;
            SessionId = sessionId;
        }
    }

    public class StreamStartedEventArgs : EventArgs
    {
        public StreamStartPayload StreamStart { get; }
        public string? SessionId { get; }

        public StreamStartedEventArgs(StreamStartPayload streamStart, string? sessionId = null)
        {
            StreamStart = streamStart;
            SessionId = sessionId;
        }
    }

    public class StreamEndedEventArgs : EventArgs
    {
        public StreamEndPayload StreamEnd { get; }
        public string? SessionId { get; }

        public StreamEndedEventArgs(StreamEndPayload streamEnd, string? sessionId = null)
        {
            StreamEnd = streamEnd;
            SessionId = sessionId;
        }
    }

    public class StatusReceivedEventArgs : EventArgs
    {
        public StatusPayload Status { get; }
        public string? SessionId { get; }

        public StatusReceivedEventArgs(StatusPayload status, string? sessionId = null)
        {
            Status = status;
            SessionId = sessionId;
        }
    }

    public class FeedbackAckReceivedEventArgs : EventArgs
    {
        public FeedbackAckPayload Ack { get; }
        public string? SessionId { get; }

        public FeedbackAckReceivedEventArgs(FeedbackAckPayload ack, string? sessionId = null)
        {
            Ack = ack;
            SessionId = sessionId;
        }
    }

    public class WebSocketErrorEventArgs : EventArgs
    {
        public string Code { get; }
        public string Message { get; }
        public string? Details { get; }

        public WebSocketErrorEventArgs(string code, string message, string? details)
        {
            Code = code;
            Message = message;
            Details = details;
        }
    }

    public class MessageReceivedEventArgs : EventArgs
    {
        public WebSocketMessage Message { get; }

        public MessageReceivedEventArgs(WebSocketMessage message)
        {
            Message = message;
        }
    }

    public class FixPlanReceivedEventArgs : EventArgs
    {
        public FixPlanPayload Plan { get; }
        public string? SessionId { get; }

        public FixPlanReceivedEventArgs(FixPlanPayload plan, string? sessionId = null)
        {
            Plan = plan;
            SessionId = sessionId;
        }
    }

    public class FixResultReceivedEventArgs : EventArgs
    {
        public FixResultPayload Result { get; }
        public string? SessionId { get; }

        public FixResultReceivedEventArgs(FixResultPayload result, string? sessionId = null)
        {
            Result = result;
            SessionId = sessionId;
        }
    }

    public class FixVerifyResultReceivedEventArgs : EventArgs
    {
        public FixVerifyResultPayload Payload { get; }
        public string? SessionId { get; }

        public FixVerifyResultReceivedEventArgs(FixVerifyResultPayload payload, string? sessionId = null)
        {
            Payload = payload;
            SessionId = sessionId;
        }
    }

    public class PostFixValidationReceivedEventArgs : EventArgs
    {
        public PostFixValidationPayload Payload { get; }
        public string? SessionId { get; }

        public PostFixValidationReceivedEventArgs(PostFixValidationPayload payload, string? sessionId = null)
        {
            Payload = payload;
            SessionId = sessionId;
        }
    }

    public class CheckStartReceivedEventArgs : EventArgs
    {
        public CheckStartPayload CheckStart { get; }
        public string? SessionId { get; }

        public CheckStartReceivedEventArgs(CheckStartPayload checkStart, string? sessionId = null)
        {
            CheckStart = checkStart;
            SessionId = sessionId;
        }
    }

    public class CheckResultReceivedEventArgs : EventArgs
    {
        public CheckResultPayload CheckResult { get; }
        public string? SessionId { get; }

        public CheckResultReceivedEventArgs(CheckResultPayload checkResult, string? sessionId = null)
        {
            CheckResult = checkResult;
            SessionId = sessionId;
        }
    }

    public class AnalysisCompleteReceivedEventArgs : EventArgs
    {
        public AnalysisCompletePayload AnalysisComplete { get; }
        public string? SessionId { get; }

        public AnalysisCompleteReceivedEventArgs(AnalysisCompletePayload analysisComplete, string? sessionId = null)
        {
            AnalysisComplete = analysisComplete;
            SessionId = sessionId;
        }
    }

    #endregion
}
