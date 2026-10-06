using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Tools;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.SelfTest
{
    /// <summary>
    /// One in-host scenario. Implementations are small and independent so adding a
    /// case is a single file + one line in <see cref="SelfTestRunner.DefaultScenarios"/>.
    ///
    /// A scenario body runs on a ThreadPool thread while AutoCAD's main thread pumps
    /// messages — exactly how an agent <c>tool_call</c> arrives in production. Never
    /// block the main thread from inside a scenario.
    /// </summary>
    public interface ISelfTestScenario
    {
        /// <summary>Stable machine-readable id, e.g. <c>discovery.list_layers</c>.</summary>
        string Name { get; }

        /// <summary>One sentence: what this proves that a unit test cannot.</summary>
        string Description { get; }

        /// <summary>True when the scenario changes the drawing (opt-in, see <see cref="SelfTestContext.AllowMutation"/>).</summary>
        bool Mutates { get; }

        /// <summary>Per-scenario budget. The runner cancels and records a failure past it.</summary>
        TimeSpan Timeout { get; }

        Task<ScenarioResult> RunAsync(SelfTestContext ctx);
    }

    /// <summary>pass / fail / skip — string on the wire so the JSON report is script-friendly.</summary>
    public static class ScenarioStatus
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        public const string Skip = "skip";
    }

    /// <summary>Outcome of one scenario, serialized verbatim into the report.</summary>
    public sealed class ScenarioResult
    {
        public string Status { get; private init; } = ScenarioStatus.Fail;

        /// <summary>What the contract says should happen.</summary>
        public string Expected { get; private init; } = string.Empty;

        /// <summary>What actually happened.</summary>
        public string Actual { get; private init; } = string.Empty;

        /// <summary>Failure summary (null on pass/skip).</summary>
        public string? Error { get; private init; }

        /// <summary>Stack trace when the scenario threw.</summary>
        public string? Stack { get; private init; }

        /// <summary>Free-form measurements a human may want to eyeball.</summary>
        public Dictionary<string, object?> Details { get; } = new();

        public static ScenarioResult Pass(string expected, string actual) =>
            new() { Status = ScenarioStatus.Pass, Expected = expected, Actual = actual };

        public static ScenarioResult Fail(string expected, string actual, string? error = null, Exception? ex = null) =>
            new()
            {
                Status = ScenarioStatus.Fail,
                Expected = expected,
                Actual = actual,
                Error = error ?? ex?.Message,
                Stack = ex?.ToString()
            };

        public static ScenarioResult Skip(string reason) =>
            new() { Status = ScenarioStatus.Skip, Expected = "n/a", Actual = reason };

        public ScenarioResult With(string key, object? value)
        {
            Details[key] = value;
            return this;
        }
    }

    /// <summary>
    /// Everything a scenario needs: the real <see cref="ToolExecutor"/>, the captured
    /// main-thread id, the opt-in mutation flag, and a main-thread marshaling helper.
    /// </summary>
    public sealed class SelfTestContext
    {
        public SelfTestContext(
            ToolExecutor executor,
            int mainThreadId,
            bool allowMutation,
            string? agentUrl,
            string? agentApiKey,
            Action<string> log)
        {
            Executor = executor;
            MainThreadId = mainThreadId;
            AllowMutation = allowMutation;
            AgentUrl = agentUrl;
            AgentApiKey = agentApiKey;
            Log = log;
        }

        public ToolExecutor Executor { get; }

        /// <summary>AutoCAD's main (UI) thread, captured inside the command.</summary>
        public int MainThreadId { get; }

        /// <summary><c>MAHOD_SELFTEST_ALLOW_MUTATION=1</c>. Default false — never touch a user's drawing.</summary>
        public bool AllowMutation { get; }

        /// <summary><c>MAHOD_SELFTEST_AGENT_URL</c>. Null ⇒ the WebSocket scenario skips.</summary>
        public string? AgentUrl { get; }

        public string? AgentApiKey { get; }

        public Action<string> Log { get; }

        /// <summary>Serializes an anonymous object into tool arguments.</summary>
        public static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

        /// <summary>Runs a tool through the REAL production path (queue → Idle hop → document lock).</summary>
        public Task<ToolResult> RunToolAsync(
            string toolName,
            object args,
            string? sessionId = null,
            CancellationToken ct = default) =>
            Executor.ExecuteAsync(toolName, Args(args), ct, sessionId);

        /// <summary>
        /// Marshals <paramref name="fn"/> onto AutoCAD's main thread via a one-shot
        /// <c>Application.Idle</c> hop — the same mechanism <see cref="ToolExecutor"/>
        /// uses. Scenarios need it to read document state (which drawing is active,
        /// how many are open) without touching the AutoCAD API off-thread.
        /// </summary>
        public Task<T> OnMainThreadAsync<T>(Func<T> fn, CancellationToken ct = default)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainThreadId)
                return Task.FromResult(fn());

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            void IdleHandler(object? sender, EventArgs e)
            {
                AcadApp.Idle -= IdleHandler;
                try { tcs.TrySetResult(fn()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }

            AcadApp.Idle += IdleHandler;

            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    AcadApp.Idle -= IdleHandler;
                    tcs.TrySetCanceled(ct);
                });
            }

            return tcs.Task;
        }

        /// <summary>Re-reads a tool result as JSON so scenarios can assert on wire shape.</summary>
        public static JsonElement AsJson(object? data) =>
            data == null
                ? JsonDocument.Parse("null").RootElement
                : JsonSerializer.SerializeToElement(data, WebSocket.WebSocketJson.Options);
    }
}
