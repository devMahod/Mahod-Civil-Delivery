using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools
{
    /// <summary>
    /// Ordering / isolation contract of the queue that serializes all tool
    /// executions across sessions (2026-08-03 parallel-sessions fix).
    /// </summary>
    public class SerialToolQueueTests
    {
        [Fact]
        public async Task Enqueue_RunsStrictlyInOrder_WithoutOverlap()
        {
            var queue = new SerialToolQueue();
            var order = new List<int>();
            var sync = new object();
            int active = 0;
            int maxActive = 0;

            var tasks = Enumerable.Range(0, 25)
                .Select(i => queue.Enqueue(async () =>
                {
                    int now = Interlocked.Increment(ref active);
                    lock (sync)
                    {
                        if (now > maxActive) maxActive = now;
                    }

                    await Task.Delay(1);
                    lock (sync) order.Add(i);

                    Interlocked.Decrement(ref active);
                    return i;
                }))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, maxActive);                          // never interleaved
            Assert.Equal(Enumerable.Range(0, 25), order);        // strict FIFO
            Assert.Equal(Enumerable.Range(0, 25), results);      // each caller got its own result
        }

        [Fact]
        public async Task FaultedItem_DoesNotBlockSubsequentItems()
        {
            var queue = new SerialToolQueue();

            var faulted = queue.Enqueue<int>(() => throw new InvalidOperationException("boom"));
            var healthy = queue.Enqueue(() => Task.FromResult(42));

            await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
            Assert.Equal(42, await healthy);
        }

        [Fact]
        public async Task AsyncFaultedItem_DoesNotBlockSubsequentItems()
        {
            var queue = new SerialToolQueue();

            var faulted = queue.Enqueue<int>(async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("async boom");
            });
            var healthy = queue.Enqueue(() => Task.FromResult(7));

            await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
            Assert.Equal(7, await healthy);
        }

        [Fact]
        public async Task CancelledBeforeItsTurn_SkipsWorkAndCancelsCaller()
        {
            var queue = new SerialToolQueue();
            using var cts = new CancellationTokenSource();
            var blocker = new TaskCompletionSource<bool>();
            bool ran = false;

            var first = queue.Enqueue(async () =>
            {
                await blocker.Task;
                return 1;
            });
            var second = queue.Enqueue(() =>
            {
                ran = true;
                return Task.FromResult(2);
            }, cts.Token);

            cts.Cancel();
            blocker.SetResult(true);

            Assert.Equal(1, await first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.False(ran);
        }

        [Fact]
        public async Task ItemsEnqueuedDuringExecution_RunAfterCurrentItem()
        {
            var queue = new SerialToolQueue();
            var order = new List<string>();
            var sync = new object();
            var innerDone = new TaskCompletionSource<bool>();

            var outer = queue.Enqueue(async () =>
            {
                lock (sync) order.Add("outer-start");
                // Enqueue from INSIDE a running item — must run after it, not deadlock.
                var inner = queue.Enqueue(() =>
                {
                    lock (sync) order.Add("inner");
                    innerDone.SetResult(true);
                    return Task.FromResult(0);
                });
                await Task.Delay(1);
                lock (sync) order.Add("outer-end");
                return 0;
            });

            await outer;
            await innerDone.Task;

            Assert.Equal(new[] { "outer-start", "outer-end", "inner" }, order);
        }
    }
}
