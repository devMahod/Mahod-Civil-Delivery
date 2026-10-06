using System;
using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Strict FIFO async work queue. Work items enqueued from any thread run
    /// one at a time, in enqueue order; a faulted or cancelled item never
    /// breaks the chain for the items behind it.
    ///
    /// Used to serialize ALL tool executions across every session/tab: two
    /// concurrent tool_calls (e.g. chat A analyzing while chat B draws a road)
    /// must never interleave their main-thread execution (2026-08-03, parallel
    /// sessions bug).
    /// </summary>
    public sealed class SerialToolQueue
    {
        private readonly object _lock = new();
        private Task _tail = Task.CompletedTask;

        /// <summary>
        /// Enqueues a work item. The returned task completes with the item's
        /// result (or its fault/cancellation). If <paramref name="ct"/> is
        /// already cancelled when the item's turn arrives, the item is skipped
        /// and the returned task is cancelled.
        /// </summary>
        public Task<T> Enqueue<T>(Func<Task<T>> work, CancellationToken ct = default)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            lock (_lock)
            {
                var run = _tail.ContinueWith(
                    _ => ct.IsCancellationRequested
                        ? Task.FromCanceled<T>(ct)
                        : work(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();

                // The chain must survive faults/cancellations of individual items.
                _tail = run.ContinueWith(
                    _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                return run;
            }
        }
    }
}
