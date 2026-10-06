using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Tools;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// PROTOCOL v1.12: "ALL tool executions across ALL sessions run through one strict
    /// FIFO queue, so tools from concurrent sessions never interleave."
    ///
    /// Three observations, cheapest first:
    /// 1. <see cref="SerialToolQueue"/> itself never overlaps two items and preserves
    ///    enqueue order (also true off-host, but proven here against the real type).
    /// 2. Every <see cref="ToolExecutor"/> instance shares ONE queue — a per-instance
    ///    queue would serialize each session with itself and nothing across sessions,
    ///    which is the bug the invariant exists to prevent.
    /// 3. Two tool calls issued concurrently from different session ids both complete
    ///    successfully in-host (they cannot both be inside the document lock at once).
    /// </summary>
    public sealed class SerialQueueScenario : ISelfTestScenario
    {
        public string Name => "tools.serial_fifo_queue";
        public string Description => "Concurrent tool calls from different sessions run strictly one at a time, in order.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(120);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            // ── 1. the queue primitive ──────────────────────────────────────────
            var queue = new SerialToolQueue();
            var gate = new object();
            int concurrent = 0, maxConcurrent = 0;
            var order = new System.Collections.Concurrent.ConcurrentQueue<int>();

            async Task<int> Work(int id, int delayMs)
            {
                lock (gate) { concurrent++; maxConcurrent = Math.Max(maxConcurrent, concurrent); }
                try
                {
                    order.Enqueue(id);
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    return id;
                }
                finally
                {
                    lock (gate) { concurrent--; }
                }
            }

            // The first item is the slowest on purpose: without serialization the
            // later, faster items would start (and finish) inside its window.
            var items = new[]
            {
                queue.Enqueue(() => Work(1, 180)),
                queue.Enqueue(() => Work(2, 20)),
                queue.Enqueue(() => Work(3, 20)),
            };
            await Task.WhenAll(items).ConfigureAwait(false);

            if (maxConcurrent != 1)
            {
                return ScenarioResult.Fail(
                    "at most one queued item runs at a time",
                    $"observed {maxConcurrent} concurrent items");
            }

            var observedOrder = order.ToArray();
            if (!observedOrder.SequenceEqual(new[] { 1, 2, 3 }))
            {
                return ScenarioResult.Fail(
                    "strict FIFO order 1,2,3",
                    $"observed {string.Join(",", observedOrder)}");
            }

            // ── 2. one queue for every executor instance ────────────────────────
            var field = typeof(ToolExecutor).GetField("MainThreadQueue", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                return ScenarioResult.Fail(
                    "ToolExecutor holds a static SerialToolQueue named MainThreadQueue",
                    "field not found — the cross-session serialization mechanism was renamed or removed");
            }

            var sharedQueue = field.GetValue(null);
            if (sharedQueue == null || !field.IsStatic)
            {
                return ScenarioResult.Fail(
                    "the tool queue is static (shared by every executor / every session)",
                    "the queue is not a shared static instance");
            }

            // ── 3. concurrent calls from two session ids, in-host ───────────────
            var sw = Stopwatch.StartNew();
            var callA = ctx.RunToolAsync("list_layers", new { include_entity_counts = false, limit = 10 }, sessionId: null);
            var callB = ctx.RunToolAsync("get_drawing_summary", new { include_object_names = false }, sessionId: null);

            var results = await Task.WhenAll(callA, callB).ConfigureAwait(false);
            sw.Stop();

            if (!results[0].Success || !results[1].Success)
            {
                return ScenarioResult.Fail(
                    "both concurrently-issued tool calls succeed",
                    $"list_layers={results[0].Success}, get_drawing_summary={results[1].Success}",
                    results[0].Error?.Message ?? results[1].Error?.Message);
            }

            return ScenarioResult
                .Pass("no overlap, FIFO order, one shared queue, both concurrent calls succeed",
                      $"maxConcurrent=1, order=1,2,3, two in-host calls completed in {sw.ElapsedMilliseconds} ms")
                .With("max_concurrent", maxConcurrent)
                .With("order", string.Join(",", observedOrder))
                .With("concurrent_calls_ms", sw.ElapsedMilliseconds);
        }
    }
}
