using System;
using System.Threading;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// The spike's core proof (MAHOD_SELFTEST_SPIKE), promoted to a scenario.
    ///
    /// A tool invoked from a ThreadPool thread — which is exactly how an agent
    /// <c>tool_call</c> arrives over the WebSocket — must complete by hopping onto
    /// AutoCAD's main thread through <c>Application.Idle</c>. If that hop ever stops
    /// firing, every tool_call silently times out and the product looks "slow" rather
    /// than broken, so this is the first scenario in the list.
    ///
    /// Two independent observations:
    /// 1. <c>Application.Idle</c> fires, and fires on the captured main thread.
    /// 2. A tool that reads the AutoCAD database succeeds while being called from a
    ///    non-main thread — impossible without the marshaling hop, since AutoCAD's
    ///    database is main-thread-only.
    /// </summary>
    public sealed class ToolMarshalingScenario : ISelfTestScenario
    {
        public string Name => "tool_executor.marshals_to_main_thread";
        public string Description => "A tool invoked off-thread executes on the AutoCAD UI thread via the Application.Idle hop.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var scenarioThreadId = Thread.CurrentThread.ManagedThreadId;
            if (scenarioThreadId == ctx.MainThreadId)
            {
                return ScenarioResult.Fail(
                    "the scenario body runs OFF the main thread (as a tool_call does)",
                    $"scenario is running on the main thread ({scenarioThreadId}) — the runner's threading changed");
            }

            var idleFired = 0;
            var idleThreadId = -1;

            void IdleProbe(object? sender, EventArgs e)
            {
                AcadApp.Idle -= IdleProbe;
                idleThreadId = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Exchange(ref idleFired, 1);
            }

            AcadApp.Idle += IdleProbe;
            try
            {
                var result = await ctx.RunToolAsync("list_layers", new { include_entity_counts = false, limit = 10 });

                if (!result.Success)
                {
                    return ScenarioResult.Fail(
                        "a database-reading tool succeeds when called off-thread",
                        "tool failed",
                        result.Error?.Message);
                }

                if (Volatile.Read(ref idleFired) == 0)
                {
                    return ScenarioResult.Fail(
                        "Application.Idle fires during the run",
                        "Idle never fired — the marshaling premise no longer holds in this host");
                }

                if (idleThreadId != ctx.MainThreadId)
                {
                    return ScenarioResult.Fail(
                        $"Idle fires on the main thread ({ctx.MainThreadId})",
                        $"Idle fired on thread {idleThreadId}");
                }

                return ScenarioResult
                    .Pass($"off-thread call ({scenarioThreadId}) executes on main thread ({ctx.MainThreadId})",
                          "tool succeeded and Idle fired on the main thread")
                    .With("scenario_thread_id", scenarioThreadId)
                    .With("main_thread_id", ctx.MainThreadId)
                    .With("idle_thread_id", idleThreadId);
            }
            finally
            {
                AcadApp.Idle -= IdleProbe;
            }
        }
    }
}
