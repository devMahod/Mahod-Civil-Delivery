using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Tests.Contracts
{
    /// <summary>
    /// The plugin's side of the protocol, reflected/read out of production code so
    /// the contract tests never restate it by hand:
    ///
    /// <list type="bullet">
    /// <item>which payload DTO models which message type,</item>
    /// <item>which message-type constants exist in <see cref="MessageTypes"/>,</item>
    /// <item>which types the plugin actually dispatches (receive) or emits (send).</item>
    /// </list>
    /// </summary>
    public static class PluginProtocolSurface
    {
        // ── message type → the REAL DTO the plugin deserializes that payload into ──
        //
        // A null value means the plugin models no payload class for the type. That is
        // legitimate for the empty-payload housekeeping messages, and a FINDING for
        // anything else (see NoDtoReason + the coverage theories).
        public static readonly IReadOnlyDictionary<string, Type?> PayloadDtos =
            new Dictionary<string, Type?>(StringComparer.Ordinal)
            {
                // ── client → server (the plugin builds these) ──
                ["connect"] = typeof(ConnectPayload),
                ["heartbeat"] = typeof(HeartbeatPayload),
                ["session_create"] = typeof(SessionCreatePayload),
                ["chat"] = typeof(ChatPayload),
                ["analyze"] = typeof(AnalyzePayload),
                ["fix_request"] = typeof(FixRequestPayload),
                ["fix_approval"] = typeof(FixApprovalPayload),
                ["tool_result"] = typeof(ToolResultPayload),
                ["event"] = typeof(DrawingEventPayload),
                ["cancel_stream"] = typeof(CancelStreamPayload),
                ["file_upload"] = typeof(FileUploadPayload),
                ["message_feedback"] = typeof(MessageFeedbackPayload),
                ["detail_upload"] = typeof(DetailUploadPayload),
                ["disconnect"] = null,
                ["drawing_changed"] = null,
                ["design"] = null,
                ["program_analysis"] = null,

                // ── server → client (the plugin consumes these) ──
                ["connect_ack"] = typeof(ConnectAckPayload),
                ["session_created"] = typeof(SessionCreatedPayload),
                ["status"] = typeof(StatusPayload),
                ["thinking"] = typeof(StatusPayload),
                ["stream_start"] = typeof(StreamStartPayload),
                ["stream_token"] = typeof(StreamTokenPayload),
                ["stream_end"] = typeof(StreamEndPayload),
                ["error"] = typeof(ErrorPayload),
                ["tool_call"] = typeof(ToolCallPayload),
                ["check_start"] = typeof(CheckStartPayload),
                ["check_result"] = typeof(CheckResultPayload),
                ["analysis_complete"] = typeof(AnalysisCompletePayload),
                ["fix_plan"] = typeof(FixPlanPayload),
                ["fix_result"] = typeof(FixResultPayload),
                ["fix_verify_result"] = typeof(FixVerifyResultPayload),
                ["post_fix_validation"] = typeof(PostFixValidationPayload),
                ["feedback_ack"] = typeof(FeedbackAckPayload),
                ["heartbeat_ack"] = null,
                ["drawing_changed_ack"] = null,
                ["detail_upload_ack"] = null,

                // Modelled by NO plugin DTO. Kept explicit so a reader sees the gap.
                ["warning"] = null,
                ["await_input"] = null,
                ["lisp_tools"] = null,
                ["analysis_plan"] = null,
                ["pending_response"] = null,
            };

        /// <summary>Why a type has no DTO — "by contract" (empty payload) vs a real gap.</summary>
        public static readonly IReadOnlyDictionary<string, string> NoDtoReason =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["disconnect"] = "empty payload by contract",
                ["drawing_changed"] = "empty payload by contract (legacy type; the plugin sends `event` instead)",
                ["heartbeat_ack"] = "empty payload by contract",
                ["drawing_changed_ack"] = "empty payload by contract",
                ["detail_upload_ack"] = "empty payload by contract",
                ["analysis_plan"] = "informational, audience is empty — no client is required to consume it",
                ["pending_response"] = "web-only (audience does not include the plugin)",
                ["lisp_tools"] = "web-only since v1.13 (audience does not include the plugin)",
                ["design"] = "audience [] since v1.13 — the agent dispatches the type but no client sends it",
                ["program_analysis"] = "audience [] since v1.13 — the agent dispatches the type but no client sends it",
            };

        // ── MessageTypes constant catalog ──────────────────────────────────────

        private static readonly Lazy<IReadOnlyDictionary<string, string>> ConstantsLazy =
            new(() =>
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var f in typeof(MessageTypes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                    var value = (string?)f.GetRawConstantValue();
                    if (value == null) continue;
                    if (!map.ContainsKey(value)) map[value] = f.Name;
                }
                return map;
            });

        /// <summary>Wire value → the <see cref="MessageTypes"/> constant name declaring it.</summary>
        public static IReadOnlyDictionary<string, string> MessageTypeConstants => ConstantsLazy.Value;

        public static bool HasConstant(string wireType) => MessageTypeConstants.ContainsKey(wireType);

        // ── plugin source (dispatch / emit proof) ──────────────────────────────

        private static readonly Lazy<string> SourceDirLazy = new(ResolveSourceDir);

        private static readonly Lazy<string> DispatcherSourceLazy =
            new(() => ReadSource(Path.Combine("WebSocket", "MessageDispatcher.cs")));

        private static readonly Lazy<string> ClientSourceLazy =
            new(() => ReadSource(Path.Combine("WebSocket", "MahodWebSocketClient.cs")));

        public static string SourceDir => SourceDirLazy.Value;

        public static string DispatcherSource => DispatcherSourceLazy.Value;

        public static string ClientSource => ClientSourceLazy.Value;

        /// <summary>
        /// True when <see cref="MessageDispatcher.DispatchAsync"/> has a switch case
        /// for this type — i.e. the plugin does something with it instead of falling
        /// through to the "Unhandled message type" default branch.
        /// </summary>
        public static bool IsDispatched(string wireType)
        {
            if (!MessageTypeConstants.TryGetValue(wireType, out var constant)) return false;

            var byConstant = new Regex($@"case\s+MessageTypes\.{Regex.Escape(constant)}\s*:");
            var byLiteral = new Regex($@"case\s+""{Regex.Escape(wireType)}""\s*:");
            return byConstant.IsMatch(DispatcherSource) || byLiteral.IsMatch(DispatcherSource);
        }

        /// <summary>
        /// True when <see cref="MahodWebSocketClient"/> can actually build this
        /// client→server message (it references the constant on a send path).
        /// </summary>
        public static bool IsEmittedByClient(string wireType)
        {
            if (!MessageTypeConstants.TryGetValue(wireType, out var constant)) return false;
            return new Regex($@"MessageTypes\.{Regex.Escape(constant)}\b").IsMatch(ClientSource);
        }

        private static string ResolveSourceDir()
        {
            var declared = typeof(PluginProtocolSurface).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "MahodPluginSourceDir")?.Value;

            if (!string.IsNullOrWhiteSpace(declared))
            {
                var full = Path.GetFullPath(declared!);
                if (Directory.Exists(Path.Combine(full, "WebSocket"))) return full;
            }

            // Fallback for a relocated test binary: walk up looking for the project.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "MahodAI.Civil3D.Plugin");
                if (Directory.Exists(Path.Combine(candidate, "WebSocket"))) return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate the MahodAI.Civil3D.Plugin source directory. The contract suite " +
                "reads WebSocket/MessageDispatcher.cs to prove a message type is dispatched; the path " +
                "normally comes from the MahodPluginSourceDir assembly metadata set in the .csproj.");
        }

        private static string ReadSource(string relativePath)
        {
            var path = Path.Combine(SourceDir, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Plugin source file not found for contract scanning: {path}");
            return File.ReadAllText(path);
        }
    }
}
