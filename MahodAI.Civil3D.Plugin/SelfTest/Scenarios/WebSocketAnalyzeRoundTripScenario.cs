using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// A REAL analyze round trip over a REAL WebSocket, against the agent named by
    /// <c>MAHOD_SELFTEST_AGENT_URL</c>. Skips cleanly when that variable is absent, so
    /// the harness still runs offline.
    ///
    /// What only this can prove: connect → session_create → analyze → the agent's
    /// <c>tool_call</c>s are executed against the live drawing and answered → a stream
    /// completes. Plus the two envelope invariants that matter to a multi-tab client:
    /// every server→client message is session-stamped (v1.1) and every tool_call is
    /// answered exactly once.
    /// </summary>
    public sealed class WebSocketAnalyzeRoundTripScenario : ISelfTestScenario
    {
        public string Name => "protocol.analyze_round_trip";
        public string Description => "Full analyze over a real WebSocket: envelope invariants hold and every tool_call is answered.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromMinutes(6);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            if (string.IsNullOrWhiteSpace(ctx.AgentUrl))
                return ScenarioResult.Skip("set MAHOD_SELFTEST_AGENT_URL to run the WebSocket round trip");

            var summaryResult = await ctx.RunToolAsync("get_drawing_summary", new { include_object_names = true });
            if (!summaryResult.Success || summaryResult.Data == null)
                return ScenarioResult.Fail("a drawing summary to analyze", "get_drawing_summary failed",
                    summaryResult.Error?.Message);

            var drawingName = await ctx.OnMainThreadAsync(SessionDocumentBindingScenario.ActiveDocumentName)
                .ConfigureAwait(false) ?? "selftest.dwg";

            using var client = new MahodWebSocketClient(ctx.AgentUrl!, ctx.AgentApiKey ?? string.Empty);

            var unstamped = new ConcurrentBag<string>();
            int toolCallsReceived = 0, toolCallsAnswered = 0, statusMessages = 0;
            var streamEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var analysisComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? sessionId = null;
            string? serverError = null;

            void CheckStamp(string type, string? stamped)
            {
                if (string.IsNullOrEmpty(stamped)) unstamped.Add(type);
            }

            client.StatusReceived += (_, e) => { Interlocked.Increment(ref statusMessages); CheckStamp("status", e.SessionId); };
            client.StreamStarted += (_, e) => CheckStamp("stream_start", e.SessionId);
            client.StreamEnded += (_, e) =>
            {
                CheckStamp("stream_end", e.SessionId);
                streamEnded.TrySetResult(true);
            };
            client.CheckStartReceived += (_, e) => CheckStamp("check_start", e.SessionId);
            client.AnalysisCompleteReceived += (_, e) =>
            {
                CheckStamp("analysis_complete", e.SessionId);
                analysisComplete.TrySetResult(true);
            };
            client.ErrorOccurred += (_, e) => serverError ??= $"{e.Code}: {e.Message}";

            client.ToolCallReceived += (_, e) =>
            {
                Interlocked.Increment(ref toolCallsReceived);
                CheckStamp("tool_call", e.SessionId);

                // Never block the receive loop — answer on the ThreadPool, exactly as
                // production does (the executor marshals to the UI thread itself).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = await ctx.Executor
                            .ExecuteToolCallAsync(e.ToolCall, e.SessionId)
                            .ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(e.SessionId))
                        {
                            await client.SendToolResultAsync(e.SessionId!, payload).ConfigureAwait(false);
                            Interlocked.Increment(ref toolCallsAnswered);
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Log($"    tool_call {e.ToolCall.ToolName} failed: {ex.Message}");
                    }
                });
            };

            try
            {
                using var cts = new CancellationTokenSource(Timeout);

                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                if (!await WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false))
                    return ScenarioResult.Fail("connected + authenticated", $"never connected to {ctx.AgentUrl}", serverError);

                var created = await client
                    .CreateSessionAsync(drawingName, System.IO.Path.GetFileName(drawingName), summaryResult.Data!, cts.Token)
                    .ConfigureAwait(false);

                sessionId = created?.SessionId;
                if (string.IsNullOrWhiteSpace(sessionId))
                    return ScenarioResult.Fail("session_created carries a session_id", "no session id returned", serverError);

                await client.AnalyzeAsync(sessionId!, summary: summaryResult.Data, ct: cts.Token).ConfigureAwait(false);

                // stream_end is the terminal event of an analyze run; analysis_complete
                // is reported alongside it but is not required to arrive first.
                await Task.WhenAny(streamEnded.Task, Task.Delay(Timeout, cts.Token)).ConfigureAwait(false);

                if (!streamEnded.Task.IsCompleted)
                {
                    return ScenarioResult.Fail(
                        "the analyze stream completes",
                        $"no stream_end within {Timeout.TotalMinutes:0} min " +
                        $"(status={statusMessages}, tool_calls={toolCallsReceived})",
                        serverError);
                }

                if (!unstamped.IsEmpty)
                {
                    return ScenarioResult.Fail(
                        "PROTOCOL v1.1: every server→client message carries session_id",
                        $"unstamped message types: {string.Join(", ", unstamped)}");
                }

                if (toolCallsReceived != toolCallsAnswered)
                {
                    return ScenarioResult.Fail(
                        "every tool_call is answered",
                        $"{toolCallsAnswered} of {toolCallsReceived} tool_calls answered");
                }

                return ScenarioResult
                    .Pass("session-stamped envelopes, all tool_calls answered, stream completed",
                          $"session={sessionId}, tool_calls={toolCallsReceived}, status={statusMessages}")
                    .With("session_id", sessionId)
                    .With("tool_calls", toolCallsReceived)
                    .With("status_messages", statusMessages)
                    .With("analysis_complete", analysisComplete.Task.IsCompleted)
                    .With("server_error", serverError);
            }
            finally
            {
                try { await client.DisconnectAsync().ConfigureAwait(false); } catch { /* best effort */ }
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan budget, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            return condition();
        }
    }
}
