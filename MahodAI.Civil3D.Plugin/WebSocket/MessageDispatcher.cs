using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.WebSocket
{
    /// <summary>
    /// Dispatches incoming WebSocket messages to appropriate handlers.
    /// Manages correlation between requests and responses.
    /// </summary>
    public class MessageDispatcher
    {
        private readonly MahodWebSocketClient _client;

        // Active streams being received
        private readonly ConcurrentDictionary<string, StreamBuffer> _activeStreams = new();

        // JSON serialization options — the ONE canonical protocol configuration
        // (see WebSocketJson). Shared with MahodWebSocketClient and the contract tests.
        private static readonly JsonSerializerOptions JsonOptions = WebSocketJson.Options;

        public MessageDispatcher(MahodWebSocketClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Dispatches a received message to the appropriate handler.
        /// </summary>
        public async Task DispatchAsync(WebSocketMessage message)
        {
            switch (message.Type)
            {
                case MessageTypes.HeartbeatAck:
                    HandleHeartbeatAck();
                    break;

                case MessageTypes.ToolCall:
                    await HandleToolCallAsync(message);
                    break;

                case MessageTypes.StreamStart:
                    HandleStreamStart(message);
                    break;

                case MessageTypes.StreamToken:
                    HandleStreamToken(message);
                    break;

                case MessageTypes.StreamEnd:
                    HandleStreamEnd(message);
                    break;

                case MessageTypes.Status:
                    HandleStatus(message);
                    break;

                case MessageTypes.Error:
                    HandleError(message);
                    break;

                case MessageTypes.FeedbackAck:
                    HandleFeedbackAck(message);
                    break;

                case MessageTypes.FixPlan:
                    HandleFixPlan(message);
                    break;

                case MessageTypes.FixResult:
                    HandleFixResult(message);
                    break;

                case MessageTypes.FixVerifyResult:
                    HandleFixVerifyResult(message);
                    break;

                case MessageTypes.PostFixValidation:
                    HandlePostFixValidation(message);
                    break;

                case MessageTypes.CheckStart:
                    HandleCheckStart(message);
                    break;

                case MessageTypes.CheckResult:
                    HandleCheckResult(message);
                    break;

                case MessageTypes.AnalysisComplete:
                    HandleAnalysisComplete(message);
                    break;

                case MessageTypes.Thinking:
                case MessageTypes.CheckProgress:
                    HandleStatus(message);
                    break;

                // Connect/Session responses are handled via correlation in the client
                case MessageTypes.ConnectAck:
                case MessageTypes.SessionCreated:
                    // These are handled by SendAndWaitAsync via correlation
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine($"Unhandled message type: {message.Type}");
                    break;
            }
        }

        #region Message Handlers

        private void HandleHeartbeatAck()
        {
            _client.OnHeartbeatAckReceived();
        }

        private Task HandleToolCallAsync(WebSocketMessage message)
        {
            var payload = DeserializePayload<ToolCallPayload>(message.Payload);
            if (payload == null) return Task.CompletedTask;

            // Invoke the tool call event on the client
            // The ToolExecutor will handle actual execution
            _client.OnToolCallReceived(payload, message.SessionId);
            return Task.CompletedTask;
        }

        private void HandleStreamStart(WebSocketMessage message)
        {
            var payload = DeserializePayload<StreamStartPayload>(message.Payload);
            if (payload == null) return;

            // Create a new stream buffer
            _activeStreams[payload.StreamId] = new StreamBuffer
            {
                StreamId = payload.StreamId,
                ContentType = payload.ContentType,
                StartTime = DateTime.UtcNow
            };

            _client.OnStreamStarted(payload, message.SessionId);
        }

        private void HandleStreamToken(WebSocketMessage message)
        {
            var payload = DeserializePayload<StreamTokenPayload>(message.Payload);
            if (payload == null) return;

            // Buffer the token
            if (_activeStreams.TryGetValue(payload.StreamId, out var buffer))
            {
                buffer.AppendToken(payload.Token, payload.Sequence);
            }

            _client.OnStreamTokenReceived(payload, message.SessionId);
        }

        private void HandleStreamEnd(WebSocketMessage message)
        {
            var payload = DeserializePayload<StreamEndPayload>(message.Payload);
            if (payload == null) return;

            // Remove the stream buffer and finalize
            if (_activeStreams.TryRemove(payload.StreamId, out var buffer))
            {
                buffer.Complete = payload.Complete;
            }

            _client.OnStreamEnded(payload, message.SessionId);
        }

        private void HandleStatus(WebSocketMessage message)
        {
            var payload = DeserializePayload<StatusPayload>(message.Payload);
            if (payload == null) return;

            _client.OnStatusReceived(payload, message.SessionId);
        }

        private void HandleError(WebSocketMessage message)
        {
            var payload = DeserializePayload<ErrorPayload>(message.Payload);
            if (payload == null) return;

            _client.OnError(payload.Code, payload.Message, payload.Details);
        }

        private void HandleFeedbackAck(WebSocketMessage message)
        {
            var payload = DeserializePayload<FeedbackAckPayload>(message.Payload);
            if (payload == null) return;

            _client.OnFeedbackAckReceived(payload, message.SessionId);
        }

        private void HandleFixPlan(WebSocketMessage message)
        {
            var payload = DeserializePayload<FixPlanPayload>(message.Payload);
            if (payload == null) return;

            _client.OnFixPlanReceived(payload, message.SessionId);
        }

        private void HandleFixResult(WebSocketMessage message)
        {
            var payload = DeserializePayload<FixResultPayload>(message.Payload);
            if (payload == null) return;

            _client.OnFixResultReceived(payload, message.SessionId);
        }

        private void HandleFixVerifyResult(WebSocketMessage message)
        {
            var payload = DeserializePayload<FixVerifyResultPayload>(message.Payload);
            if (payload == null) return;

            _client.OnFixVerifyResultReceived(payload, message.SessionId);
        }

        private void HandlePostFixValidation(WebSocketMessage message)
        {
            var payload = DeserializePayload<PostFixValidationPayload>(message.Payload);
            if (payload == null) return;

            _client.OnPostFixValidationReceived(payload, message.SessionId);
        }

        private void HandleCheckStart(WebSocketMessage message)
        {
            var payload = DeserializePayload<CheckStartPayload>(message.Payload);
            if (payload == null) return;

            _client.OnCheckStartReceived(payload, message.SessionId);
        }

        private void HandleCheckResult(WebSocketMessage message)
        {
            var payload = DeserializePayload<CheckResultPayload>(message.Payload);
            if (payload == null) return;

            _client.OnCheckResultReceived(payload, message.SessionId);
        }

        private void HandleAnalysisComplete(WebSocketMessage message)
        {
            var payload = DeserializePayload<AnalysisCompletePayload>(message.Payload);
            if (payload == null) return;

            _client.OnAnalysisCompleteReceived(payload, message.SessionId);
        }

        #endregion

        #region Stream Management

        /// <summary>
        /// Gets the current content of an active stream.
        /// </summary>
        public string? GetStreamContent(string streamId)
        {
            if (_activeStreams.TryGetValue(streamId, out var buffer))
            {
                return buffer.GetContent();
            }
            return null;
        }

        /// <summary>
        /// Checks if a stream is currently active.
        /// </summary>
        public bool IsStreamActive(string streamId)
        {
            return _activeStreams.ContainsKey(streamId);
        }

        #endregion

        #region Helpers

        private static T? DeserializePayload<T>(JsonElement? payload) where T : class
        {
            if (payload == null) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(payload.Value.GetRawText(), JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Buffers streaming tokens and maintains sequence order.
    /// </summary>
    internal class StreamBuffer
    {
        public string StreamId { get; set; } = string.Empty;
        public string ContentType { get; set; } = "text";
        public DateTime StartTime { get; set; }
        public bool Complete { get; set; }

        private readonly ConcurrentDictionary<int, string> _tokens = new();
        private int _expectedSequence = 0;
        private readonly object _lock = new();

        /// <summary>
        /// Appends a token at the specified sequence number.
        /// </summary>
        public void AppendToken(string token, int sequence)
        {
            _tokens[sequence] = token;
        }

        /// <summary>
        /// Gets the current buffered content, reordering by sequence if needed.
        /// </summary>
        public string GetContent()
        {
            var builder = new System.Text.StringBuilder();

            // Get ordered tokens
            var sequences = new System.Collections.Generic.List<int>(_tokens.Keys);
            sequences.Sort();

            foreach (var seq in sequences)
            {
                if (_tokens.TryGetValue(seq, out var token))
                {
                    builder.Append(token);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Gets only new content since last retrieval.
        /// </summary>
        public string GetNewContent()
        {
            var builder = new System.Text.StringBuilder();

            lock (_lock)
            {
                // Get ordered tokens starting from expected sequence
                var sequences = new System.Collections.Generic.List<int>(_tokens.Keys);
                sequences.Sort();

                foreach (var seq in sequences)
                {
                    if (seq >= _expectedSequence && _tokens.TryGetValue(seq, out var token))
                    {
                        builder.Append(token);
                        _expectedSequence = seq + 1;
                    }
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Gets the total number of tokens received.
        /// </summary>
        public int TokenCount => _tokens.Count;
    }
}
