using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahodAI.Civil3D.Plugin.WebSocket
{
    /// <summary>
    /// WebSocket message envelope - wraps all messages with metadata.
    /// </summary>
    public class WebSocketMessage
    {
        /// <summary>
        /// Unique message identifier.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Message type (connect, session_create, chat, analyze, tool_call, tool_result, stream_token, event).
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// ISO 8601 timestamp.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>
        /// Session ID (null for connect messages).
        /// </summary>
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        /// <summary>
        /// Correlation ID for request/response matching.
        /// </summary>
        [JsonPropertyName("correlation_id")]
        public string? CorrelationId { get; set; }

        /// <summary>
        /// Message payload (type-specific).
        /// </summary>
        [JsonPropertyName("payload")]
        public JsonElement? Payload { get; set; }
    }

    #region Message Types

    /// <summary>
    /// WebSocket message type constants.
    /// </summary>
    public static class MessageTypes
    {
        // Connection lifecycle
        public const string Connect = "connect";
        public const string ConnectAck = "connect_ack";
        public const string Disconnect = "disconnect";
        public const string Heartbeat = "heartbeat";
        public const string HeartbeatAck = "heartbeat_ack";

        // Session management
        public const string SessionCreate = "session_create";
        public const string SessionCreated = "session_created";
        public const string SessionClose = "session_close";
        public const string DetailUpload = "detail_upload";
        public const string DetailUploadAck = "detail_upload_ack";

        // User interactions
        public const string Chat = "chat";
        public const string Analyze = "analyze";
        public const string FileUpload = "file_upload";

        // Per-message feedback (protocol v1.14): reaction (👍/👎) and/or free text
        public const string MessageFeedback = "message_feedback";
        public const string FeedbackAck = "feedback_ack";

        // Tool execution
        public const string ToolCall = "tool_call";
        public const string ToolResult = "tool_result";

        // Streaming responses
        public const string StreamStart = "stream_start";
        public const string StreamToken = "stream_token";
        public const string StreamEnd = "stream_end";
        public const string CancelStream = "cancel_stream";

        // Status updates
        public const string Status = "status";

        // Drawing events
        public const string Event = "event";

        // Fix workflow
        public const string FixRequest = "fix_request";
        public const string FixPlan = "fix_plan";
        public const string FixApproval = "fix_approval";
        public const string FixExecute = "fix_execute";
        public const string FixResult = "fix_result";
        public const string FixVerifyResult = "fix_verify_result";
        public const string PostFixValidation = "post_fix_validation";

        // Analysis progress events
        public const string CheckStart = "check_start";
        public const string CheckProgress = "check_progress";
        public const string CheckResult = "check_result";
        public const string AnalysisComplete = "analysis_complete";
        public const string Thinking = "thinking";

        // Errors
        public const string Error = "error";
    }

    #endregion

    #region Connection Payloads

    /// <summary>
    /// Connect request payload (Plugin → Agent).
    /// </summary>
    public class ConnectPayload
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("client_version")]
        public string ClientVersion { get; set; } = "1.0.0";

        [JsonPropertyName("client_type")]
        public string ClientType { get; set; } = Constants.AppConstants.ClientTypePlugin;

        [JsonPropertyName("capabilities")]
        public List<string> Capabilities { get; set; } = new()
        {
            "tool_execution",
            "event_notifications",
            "streaming",
            // v1.16 — advisory only; the agent gates on nothing here yet.
            "image_attachments"
        };
    }

    /// <summary>
    /// Connect acknowledgment payload (Agent → Plugin).
    /// </summary>
    public class ConnectAckPayload
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("connection_id")]
        public string ConnectionId { get; set; } = string.Empty;

        [JsonPropertyName("server_version")]
        public string ServerVersion { get; set; } = string.Empty;

        [JsonPropertyName("available_tools")]
        public List<ToolDefinition> AvailableTools { get; set; } = new();

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Heartbeat payload.
    /// </summary>
    public class HeartbeatPayload
    {
        [JsonPropertyName("client_timestamp")]
        public long ClientTimestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    #endregion

    #region Session Payloads

    /// <summary>
    /// Session create request payload.
    /// </summary>
    public class SessionCreatePayload
    {
        [JsonPropertyName("drawing_id")]
        public string DrawingId { get; set; } = string.Empty;

        [JsonPropertyName("drawing_name")]
        public string DrawingName { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public object? Summary { get; set; }
    }

    /// <summary>
    /// Session created response payload.
    /// </summary>
    public class SessionCreatedPayload
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }
    }

    /// <summary>
    /// Detail upload payload — sends stripped entity data incrementally after session creation.
    /// </summary>
    public class DetailUploadPayload
    {
        [JsonPropertyName("detail_type")]
        public string DetailType { get; set; } = string.Empty;

        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }

    #endregion

    #region Chat/Analyze Payloads

    /// <summary>
    /// Cancel-stream payload — asks the agent to stop generating a specific stream.
    /// </summary>
    public class CancelStreamPayload
    {
        [JsonPropertyName("stream_id")]
        public string StreamId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Chat message payload.
    /// </summary>
    public class ChatPayload
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("context")]
        public ChatContext? Context { get; set; }

        /// <summary>
        /// PROTOCOL v1.16 — images attached to THIS question. Null (and, thanks
        /// to WhenWritingNull, absent from the wire) on an ordinary message, so
        /// a chat without images is byte-identical to what v1.14 sent.
        /// </summary>
        [JsonPropertyName("attachments")]
        public List<ChatAttachment>? Attachments { get; set; }
    }

    /// <summary>
    /// One image attached to a chat message (PROTOCOL v1.16).
    /// Unlike <see cref="FileUploadPayload"/>, this is NOT OCR'd to text on the
    /// server — the bytes reach the answering model as a real image.
    /// </summary>
    public class ChatAttachment
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "image";

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        /// <summary>image/png | image/jpeg | image/webp | image/gif</summary>
        [JsonPropertyName("mime_type")]
        public string MimeType { get; set; } = "image/png";

        /// <summary>Base64 WITHOUT a data: URI prefix.</summary>
        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;
    }

    /// <summary>
    /// Additional context for chat messages.
    /// </summary>
    public class ChatContext
    {
        [JsonPropertyName("selected_objects")]
        public List<string>? SelectedObjects { get; set; }

        [JsonPropertyName("current_view")]
        public ViewInfo? CurrentView { get; set; }
    }

    /// <summary>
    /// View information.
    /// </summary>
    public class ViewInfo
    {
        [JsonPropertyName("center_x")]
        public double CenterX { get; set; }

        [JsonPropertyName("center_y")]
        public double CenterY { get; set; }

        [JsonPropertyName("zoom_level")]
        public double ZoomLevel { get; set; }
    }

    /// <summary>
    /// File upload payload — sends file as base64 for text extraction.
    /// </summary>
    public class FileUploadPayload
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Analyze request payload.
    /// </summary>
    public class AnalyzePayload
    {
        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }

        [JsonPropertyName("focus_areas")]
        public List<string>? FocusAreas { get; set; }

        [JsonPropertyName("selected_alignment_names")]
        public List<string>? SelectedAlignmentNames { get; set; }

        [JsonPropertyName("selected_profile_names")]
        public List<string>? SelectedProfileNames { get; set; }

        [JsonPropertyName("road_type_overrides")]
        public Dictionary<string, string>? RoadTypeOverrides { get; set; }

        [JsonPropertyName("road_classification_overrides")]
        public Dictionary<string, string>? RoadClassificationOverrides { get; set; }

        // Per-alignment cross-section override (added 2026-05-04 per
        // engineer feedback). One of CROSS_SECTION_TYPES keys recognised
        // by the agent: single_carriageway / dual_carriageway_2lane /
        // dual_carriageway_4lane / urban_arterial.
        [JsonPropertyName("cross_section_overrides")]
        public Dictionary<string, string>? CrossSectionOverrides { get; set; }

        [JsonPropertyName("topography")]
        public string? Topography { get; set; }

        /// <summary>
        /// Fresh drawing summary. Sent on every analyze so the backend picks
        /// up entities added after the session was created (new alignment,
        /// new corridor, etc.) without forcing a new session.  Null skips
        /// the refresh on the backend.
        /// </summary>
        [JsonPropertyName("summary")]
        public object? Summary { get; set; }
    }

    #endregion

    #region Tool Payloads

    /// <summary>
    /// Tool definition for available tools list.
    /// </summary>
    public class ToolDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public JsonElement? Parameters { get; set; }

        [JsonPropertyName("timeout_seconds")]
        public int TimeoutSeconds { get; set; } = 0;
    }

    /// <summary>
    /// Tool call request payload (Agent → Plugin).
    /// </summary>
    public class ToolCallPayload
    {
        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; } = string.Empty;

        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public JsonElement? Arguments { get; set; }

        // Default 0 (not 30) so the agent-supplied value flows through unchanged
        // and convenience-wrapper paths fall back to each tool's own Timeout
        // property. Setting a non-zero default silently capped every interactive
        // tool at 30 s regardless of what the tool or the agent requested.
        [JsonPropertyName("timeout_seconds")]
        public int TimeoutSeconds { get; set; } = 0;
    }

    /// <summary>
    /// Tool result response payload (Plugin → Agent).
    /// </summary>
    public class ToolResultPayload
    {
        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Explicit outcome (P0-01): "succeeded" | "cancelled" | "candidate_generated"
        /// | "rejected" | "failed". The agent should prefer this over <see cref="Success"/>
        /// to distinguish a committed success from a neutral cancellation, a relaxed
        /// candidate, or a hard-gate rejection.
        /// </summary>
        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        /// <summary>True only when every engineering hard gate passed (null when no gate applies).</summary>
        [JsonPropertyName("hard_gates_passed")]
        public bool? HardGatesPassed { get; set; }

        /// <summary>True when a human engineer must approve before the result is treated as final.</summary>
        [JsonPropertyName("requires_engineer_approval")]
        public bool? RequiresEngineerApproval { get; set; }

        /// <summary>Structured engineering findings (P0-01), when present.</summary>
        [JsonPropertyName("violations")]
        public List<EngineeringViolationPayload>? Violations { get; set; }

        [JsonPropertyName("result")]
        public object? Result { get; set; }

        [JsonPropertyName("error")]
        public ToolError? Error { get; set; }

        [JsonPropertyName("execution_time_ms")]
        public long ExecutionTimeMs { get; set; }

        [JsonPropertyName("cached")]
        public bool Cached { get; set; }
    }

    /// <summary>
    /// Wire form of an <see cref="Tools.EngineeringViolation"/> (P0-01).
    /// </summary>
    public class EngineeringViolationPayload
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "hard";

        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("station")]
        public double? Station { get; set; }

        [JsonPropertyName("x")]
        public double? X { get; set; }

        [JsonPropertyName("y")]
        public double? Y { get; set; }

        [JsonPropertyName("requested")]
        public double? Requested { get; set; }

        [JsonPropertyName("achieved")]
        public double? Achieved { get; set; }

        [JsonPropertyName("citation")]
        public string? Citation { get; set; }
    }

    /// <summary>
    /// Tool error details.
    /// </summary>
    public class ToolError
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }

    #endregion

    #region Streaming Payloads

    /// <summary>
    /// Stream start payload.
    /// </summary>
    public class StreamStartPayload
    {
        [JsonPropertyName("stream_id")]
        public string StreamId { get; set; } = string.Empty;

        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = "text"; // text, html, json
    }

    /// <summary>
    /// Stream token payload.
    /// </summary>
    public class StreamTokenPayload
    {
        [JsonPropertyName("stream_id")]
        public string StreamId { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("full_text")]
        public string? FullText { get; set; }

        [JsonPropertyName("sequence")]
        public int Sequence { get; set; }
    }

    /// <summary>
    /// Stream end payload.
    /// </summary>
    public class StreamEndPayload
    {
        [JsonPropertyName("stream_id")]
        public string StreamId { get; set; } = string.Empty;

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("complete")]
        public bool Complete { get; set; } = true;

        /// <summary>
        /// Structured RAG references with SharePoint document links.
        /// Each entry has document_name, url, and pages array.
        /// </summary>
        [JsonPropertyName("references")]
        public List<RagReference>? References { get; set; }

        /// <summary>
        /// Post-processed final message text. When present, the client should
        /// replace the accumulated streamed buffer with this string before
        /// re-rendering — typically because inline <c>[RAG-N]</c> citation tags
        /// have been resolved to clickable markdown links server-side.
        /// </summary>
        [JsonPropertyName("final_text")]
        public string? FinalText { get; set; }

        /// <summary>
        /// LISP tool recommendations selected by the first-step router.
        /// Rendered as cards beneath the assistant response.
        /// </summary>
        [JsonPropertyName("lisp_recommendations")]
        public List<LispRecommendation>? LispRecommendations { get; set; }

        /// <summary>
        /// Protocol v1.14: the Supabase <c>question_logs</c> row the agent wrote
        /// for THIS answer. The plugin stamps it on the finalized message so a
        /// 👍/👎 or a written feedback lands on the right analytics row. Absent
        /// when Supabase logging is off, or when the insert had not completed by
        /// the time the answer finished — feedback then travels with the
        /// question/answer text instead and the agent creates the row.
        /// </summary>
        [JsonPropertyName("question_log_id")]
        public string? QuestionLogId { get; set; }
    }

    /// <summary>
    /// Per-message feedback (Plugin → Agent, protocol v1.14): the engineer's
    /// reaction and/or free text about one assistant answer.
    /// </summary>
    public class MessageFeedbackPayload
    {
        /// <summary>Client-side message id — echoed back so the UI can settle the right bubble.</summary>
        [JsonPropertyName("message_id")]
        public string MessageId { get; set; } = string.Empty;

        /// <summary>
        /// The answer's <c>question_logs</c> row (from <see cref="StreamEndPayload.QuestionLogId"/>).
        /// Null for messages the agent never logged (analyze reports, design steps) —
        /// the agent then creates the row from <see cref="Question"/>/<see cref="Answer"/>.
        /// </summary>
        [JsonPropertyName("question_log_id")]
        public string? QuestionLogId { get; set; }

        /// <summary>1 = 👍, -1 = 👎, null = no reaction (feedback text only, or reaction cleared).</summary>
        [JsonPropertyName("rating")]
        public int? Rating { get; set; }

        /// <summary>Free text the engineer typed; null/empty clears it.</summary>
        [JsonPropertyName("feedback")]
        public string? Feedback { get; set; }

        /// <summary>The question this answer replied to — only used to create a missing log row.</summary>
        [JsonPropertyName("question")]
        public string? Question { get; set; }

        /// <summary>The answer text — only used to create a missing log row.</summary>
        [JsonPropertyName("answer")]
        public string? Answer { get; set; }
    }

    /// <summary>
    /// Feedback acknowledgement (Agent → Plugin, protocol v1.14). Always sent,
    /// success or not, so the optimistic UI can settle or roll back.
    /// </summary>
    public class FeedbackAckPayload
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }

        /// <summary>The row the feedback landed on — set even when the agent had to create it.</summary>
        [JsonPropertyName("question_log_id")]
        public string? QuestionLogId { get; set; }

        [JsonPropertyName("rating")]
        public int? Rating { get; set; }

        [JsonPropertyName("has_feedback")]
        public bool HasFeedback { get; set; }

        /// <summary>True when the reaction AND the text were both removed.</summary>
        [JsonPropertyName("cleared")]
        public bool Cleared { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Structured LISP-tool recommendation for display in the chat UI.
    /// Mirrors <c>src/api/schemas/message.py::LispRecommendation</c>.
    /// </summary>
    public class LispRecommendation
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("short_desc")]
        public string ShortDesc { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("subcategory")]
        public string Subcategory { get; set; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("install_instructions")]
        public string InstallInstructions { get; set; } = string.Empty;
    }

    /// <summary>
    /// A structured RAG reference pointing to a SharePoint document.
    /// </summary>
    public class RagReference
    {
        [JsonPropertyName("document_name")]
        public string DocumentName { get; set; } = string.Empty;

        [JsonPropertyName("document_id")]
        public string DocumentId { get; set; } = string.Empty;

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("similarity")]
        public double Similarity { get; set; }

        [JsonPropertyName("chunks_used")]
        public int ChunksUsed { get; set; }

        [JsonPropertyName("pages")]
        public List<int> Pages { get; set; } = new();
    }

    /// <summary>
    /// Status update payload (Agent → Plugin).
    /// Used to communicate processing status during streaming.
    /// </summary>
    public class StatusPayload
    {
        /// <summary>
        /// Status type: thinking, using_tool, rag_lookup, streaming, error.
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable status message (Hebrew).
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Tool name when status is "using_tool".
        /// </summary>
        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; }
    }

    /// <summary>
    /// Status type constants.
    /// </summary>
    public static class StatusTypes
    {
        /// <summary>Agent is processing/thinking.</summary>
        public const string Thinking = "thinking";

        /// <summary>Agent is executing a tool on the drawing.</summary>
        public const string UsingTool = "using_tool";

        /// <summary>Agent is looking up information in knowledge base.</summary>
        public const string RagLookup = "rag_lookup";

        /// <summary>Agent is streaming the response.</summary>
        public const string Streaming = "streaming";

        /// <summary>An error occurred during processing.</summary>
        public const string Error = "error";

        /// <summary>Agent is planning fix operations.</summary>
        public const string PlanningFixes = "planning_fixes";

        /// <summary>Awaiting user approval for fix plan.</summary>
        public const string AwaitingApproval = "awaiting_approval";

        /// <summary>Agent is applying approved fixes.</summary>
        public const string ApplyingFixes = "applying_fixes";

        /// <summary>Agent is validating applied fixes.</summary>
        public const string ValidatingFixes = "validating_fixes";
    }

    #endregion

    #region Event Payloads

    /// <summary>
    /// Drawing event payload (Plugin → Agent).
    /// </summary>
    public class DrawingEventPayload
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = string.Empty; // objects_changed, document_switched, selection_changed

        [JsonPropertyName("changes")]
        public List<ObjectChange>? Changes { get; set; }

        [JsonPropertyName("summary")]
        public ChangeSummary? Summary { get; set; }
    }

    /// <summary>
    /// Individual object change.
    /// </summary>
    public class ObjectChange
    {
        [JsonPropertyName("object_id")]
        public string ObjectId { get; set; } = string.Empty;

        [JsonPropertyName("object_type")]
        public string ObjectType { get; set; } = string.Empty;

        [JsonPropertyName("object_name")]
        public string? ObjectName { get; set; }

        [JsonPropertyName("change_type")]
        public string ChangeType { get; set; } = string.Empty; // Added, Modified, Deleted

        [JsonPropertyName("affected_stations")]
        public StationRange? AffectedStations { get; set; }

        [JsonPropertyName("affected_properties")]
        public List<string>? AffectedProperties { get; set; }
    }

    /// <summary>
    /// Station range for partial updates.
    /// </summary>
    public class StationRange
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }
    }

    /// <summary>
    /// Summary of changes in batch.
    /// </summary>
    public class ChangeSummary
    {
        [JsonPropertyName("added")]
        public int Added { get; set; }

        [JsonPropertyName("modified")]
        public int Modified { get; set; }

        [JsonPropertyName("deleted")]
        public int Deleted { get; set; }
    }

    #endregion

    #region Fix Workflow Payloads

    /// <summary>
    /// Fix request payload (Plugin → Agent).
    /// </summary>
    public class FixRequestPayload
    {
        [JsonPropertyName("recommendation_ids")]
        public List<string>? RecommendationIds { get; set; }

        [JsonPropertyName("user_instructions")]
        public string? UserInstructions { get; set; }
    }

    /// <summary>
    /// Fix plan payload (Agent → Plugin).
    /// </summary>
    public class FixPlanPayload
    {
        [JsonPropertyName("plan_id")]
        public string PlanId { get; set; } = string.Empty;

        [JsonPropertyName("items")]
        public List<FixPlanItem> Items { get; set; } = new();

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("rag_references")]
        public List<string>? RagReferences { get; set; }

        /// <summary>
        /// Hebrew one-liner per violation the proposer couldn't wire a tool
        /// to (e.g. tangent_length has no auto-fix). Surfaced in the fix plan
        /// card so the engineer can see why a violation they saw in the
        /// analysis report is missing from the plan.
        /// </summary>
        [JsonPropertyName("skipped_reasons")]
        public List<string>? SkippedReasons { get; set; }
    }

    /// <summary>
    /// Individual item in a fix plan.
    /// </summary>
    public class FixPlanItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("object_name")]
        public string ObjectName { get; set; } = string.Empty;

        [JsonPropertyName("object_type")]
        public string ObjectType { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The violation's Hebrew location text, verbatim from the analysis report
        /// (e.g. <c>"ציר Second Street — תחנה 0+035.05 עד 0+175.48"</c>). Rendered as
        /// the same clickable 📍 cell the findings table uses so a plan row jumps to
        /// the violation's on-drawing pin. Empty on older agents — the renderer then
        /// falls back to <c>object_name</c> + <c>tool_params.target_station</c>.
        /// </summary>
        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = string.Empty;

        [JsonPropertyName("tool_params")]
        public JsonElement? ToolParams { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "important";

        [JsonPropertyName("current_value")]
        public JsonElement? CurrentValue { get; set; }

        [JsonPropertyName("proposed_value")]
        public JsonElement? ProposedValue { get; set; }

        [JsonPropertyName("editable_fields")]
        public Dictionary<string, FieldMeta>? EditableFields { get; set; }

        [JsonPropertyName("validation_notes")]
        public string? ValidationNotes { get; set; }
    }

    /// <summary>
    /// Metadata for a single editable field on a fix plan item.
    /// Mirrors agent-side Pydantic model under <c>ai_agent/src/api/schemas/fix.py::FieldMeta</c>.
    /// </summary>
    public class FieldMeta
    {
        /// <summary>Field value type: "number", "string", or "boolean".</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        /// <summary>Optional unit display (e.g. "m", "%").</summary>
        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        /// <summary>Optional numeric minimum.</summary>
        [JsonPropertyName("min")]
        public double? Min { get; set; }

        /// <summary>Optional numeric maximum.</summary>
        [JsonPropertyName("max")]
        public double? Max { get; set; }

        /// <summary>Optional fixed set of selectable options (renders as a select).</summary>
        [JsonPropertyName("options")]
        public List<string>? Options { get; set; }
    }

    /// <summary>
    /// Fix approval payload (Plugin → Agent).
    /// </summary>
    public class FixApprovalPayload
    {
        [JsonPropertyName("plan_id")]
        public string PlanId { get; set; } = string.Empty;

        [JsonPropertyName("decision")]
        public string Decision { get; set; } = string.Empty;

        [JsonPropertyName("user_message")]
        public string? UserMessage { get; set; }

        [JsonPropertyName("updates")]
        public List<FixPlanItemUpdate>? Updates { get; set; }

        [JsonPropertyName("selected_item_ids")]
        public List<string>? SelectedItemIds { get; set; }

        /// <summary>
        /// Per-item overrides for tool parameters. Only items whose values were
        /// changed in the UI are included.
        /// </summary>
        [JsonPropertyName("edited_items")]
        public List<FixItemEdit> EditedItems { get; set; } = new();
    }

    /// <summary>
    /// Updated values for a single fix plan item.
    /// </summary>
    public class FixPlanItemUpdate
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("updated_params")]
        public JsonElement? UpdatedParams { get; set; }
    }

    /// <summary>
    /// Engineer-edited tool parameter overrides for a single fix plan item.
    /// </summary>
    public class FixItemEdit
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        /// <summary>
        /// Dictionary of tool_param_name → JSON value (number, string, bool).
        /// Merged over the original <c>tool_params</c> agent-side before execution.
        /// </summary>
        [JsonPropertyName("tool_params")]
        public Dictionary<string, JsonElement> ToolParams { get; set; } = new();
    }

    /// <summary>
    /// Fix result payload (Agent → Plugin).
    /// </summary>
    public class FixResultPayload
    {
        [JsonPropertyName("plan_id")]
        public string PlanId { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("results")]
        public List<FixResultItem> Results { get; set; } = new();

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Server-reported count of items that actually mutated the drawing
        /// (items with <c>status == "applied"</c> only — excludes failed and
        /// soft-skipped items). This is the ONLY trustworthy undo count:
        /// the undo button is disabled when this is null (older agents)
        /// instead of guessing from per-item success flags.
        /// </summary>
        [JsonPropertyName("applied_count")]
        public int? AppliedCount { get; set; }
    }

    /// <summary>
    /// Result for a single fix plan item execution.
    /// </summary>
    public class FixResultItem
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Explicit outcome status from the agent (additive, may be null on
        /// older agents): "applied" (drawing mutated), "failed" (no change),
        /// "skipped" / "manual_required" (soft-skip, no change).
        /// Preferred over the boolean <see cref="Success"/> + description
        /// heuristics when present.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Name of the object the fix targeted, carried through from the plan item.
        /// Empty on older agents (the "אובייקט" column then renders "—").
        /// </summary>
        [JsonPropertyName("object_name")]
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>
        /// The violation's Hebrew location text, carried through from the plan item
        /// so the results table renders the same clickable 📍 cell as the plan.
        /// </summary>
        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        /// <summary>
        /// Structured tool output on success (JSON blob).
        /// </summary>
        [JsonPropertyName("output")]
        public JsonElement? Output { get; set; }

        /// <summary>
        /// The value before the fix was applied (extracted from tool output).
        /// </summary>
        [JsonPropertyName("previous_value")]
        public string? PreviousValue { get; set; }

        /// <summary>
        /// The value after the fix was applied (extracted from tool output).
        /// </summary>
        [JsonPropertyName("new_value")]
        public string? NewValue { get; set; }
    }

    /// <summary>
    /// Post-fix verification payload (Agent → Plugin). Arrives after <c>fix_result</c>
    /// and is merged into the existing fix card in the chat UI.
    /// </summary>
    public class FixVerifyResultPayload
    {
        [JsonPropertyName("plan_id")]
        public string PlanId { get; set; } = string.Empty;

        [JsonPropertyName("items")]
        public List<FixVerifyItem> Items { get; set; } = new();

        /// <summary>
        /// Short Hebrew summary (≤3 sentences) produced by the agent's SMALL-tier LLM.
        /// </summary>
        [JsonPropertyName("summary_text")]
        public string? SummaryText { get; set; }
    }

    /// <summary>
    /// Verification status for a single fix item.
    /// </summary>
    public class FixVerifyItem
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        /// <summary>
        /// Tri-state: true = matches expected, false = mismatch, null = could not verify.
        /// </summary>
        [JsonPropertyName("verified")]
        public bool? Verified { get; set; }

        [JsonPropertyName("actual_value")]
        public JsonElement? ActualValue { get; set; }

        [JsonPropertyName("expected_value")]
        public JsonElement? ExpectedValue { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }

    /// <summary>
    /// Post-fix re-validation payload (Agent → Plugin). Emitted after
    /// <c>fix_verify_result</c>; summarises which original violations are now
    /// clean, which are still open, and which were newly introduced.
    /// Rendered as an additional section inside the existing fix card.
    /// </summary>
    public class PostFixValidationPayload
    {
        [JsonPropertyName("plan_id")]
        public string PlanId { get; set; } = string.Empty;

        [JsonPropertyName("entities")]
        public List<PostFixValidationEntity> Entities { get; set; } = new();

        [JsonPropertyName("totals")]
        public PostFixValidationTotals? Totals { get; set; }
    }

    /// <summary>
    /// Per-entity post-fix re-validation result.
    /// </summary>
    public class PostFixValidationEntity
    {
        [JsonPropertyName("entity_type")]
        public string EntityType { get; set; } = string.Empty;

        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = string.Empty;

        /// <summary>
        /// "validated" | "no_validator" | "error".
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("resolved_count")]
        public int ResolvedCount { get; set; }

        [JsonPropertyName("still_open_count")]
        public int StillOpenCount { get; set; }

        [JsonPropertyName("new_count")]
        public int NewCount { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }

    /// <summary>
    /// Totals across every re-validated entity in the plan.
    /// </summary>
    public class PostFixValidationTotals
    {
        [JsonPropertyName("resolved")]
        public int Resolved { get; set; }

        [JsonPropertyName("still_open")]
        public int StillOpen { get; set; }

        [JsonPropertyName("new")]
        public int New { get; set; }

        [JsonPropertyName("no_validator")]
        public int NoValidator { get; set; }

        [JsonPropertyName("errors")]
        public int Errors { get; set; }
    }

    #endregion

    #region Analysis Progress Payloads

    /// <summary>
    /// Check start payload — signals beginning of a per-object analysis check.
    /// </summary>
    public class CheckStartPayload
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("item_index")]
        public int ItemIndex { get; set; }

        [JsonPropertyName("total_items")]
        public int TotalItems { get; set; }

        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Check result payload — signals completion of a per-object analysis check.
    /// </summary>
    public class CheckResultPayload
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = string.Empty;

        [JsonPropertyName("findings_count")]
        public int FindingsCount { get; set; }
    }

    /// <summary>
    /// Analysis complete payload — signals all checks are done.
    /// </summary>
    public class AnalysisCompletePayload
    {
        [JsonPropertyName("total_checks")]
        public int TotalChecks { get; set; }

        [JsonPropertyName("total_findings")]
        public int TotalFindings { get; set; }
    }

    #endregion

    #region Error Payloads

    /// <summary>
    /// Error payload.
    /// </summary>
    public class ErrorPayload
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public string? Details { get; set; }

        [JsonPropertyName("recoverable")]
        public bool Recoverable { get; set; } = true;
    }

    /// <summary>
    /// Error codes.
    /// </summary>
    public static class ErrorCodes
    {
        public const string AuthenticationFailed = "AUTH_FAILED";
        public const string SessionNotFound = "SESSION_NOT_FOUND";
        public const string SessionExpired = "SESSION_EXPIRED";
        public const string ToolNotFound = "TOOL_NOT_FOUND";
        public const string ToolExecutionFailed = "TOOL_EXECUTION_FAILED";
        public const string ToolTimeout = "TOOL_TIMEOUT";
        public const string InvalidMessage = "INVALID_MESSAGE";
        public const string RateLimited = "RATE_LIMITED";
        public const string ServerError = "SERVER_ERROR";
    }

    #endregion

    #region Connection States

    /// <summary>
    /// WebSocket connection state.
    /// </summary>
    public enum WebSocketConnectionState
    {
        /// <summary>Initial state, not connected.</summary>
        Disconnected,

        /// <summary>Attempting to connect.</summary>
        Connecting,

        /// <summary>Connected, awaiting authentication.</summary>
        Connected,

        /// <summary>Authenticated and ready for communication.</summary>
        Authenticated,

        /// <summary>Active session established.</summary>
        SessionActive,

        /// <summary>Connection lost, attempting reconnection.</summary>
        Reconnecting,

        /// <summary>Connection closed intentionally.</summary>
        Closed,

        /// <summary>Connection failed with error.</summary>
        Error
    }

    #endregion
}
