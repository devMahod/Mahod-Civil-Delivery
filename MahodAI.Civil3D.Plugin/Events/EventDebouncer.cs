using System;
using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.Events
{
    /// <summary>
    /// Debounces change events with a configurable delay window.
    /// Batches multiple rapid changes before notifying the agent.
    /// </summary>
    public class EventDebouncer : IDisposable
    {
        private readonly int _debounceDelayMs;
        private readonly int _maxPendingChanges;
        private readonly Action<ChangeBatch> _onBatchReady;

        private ChangeBatch _pendingBatch = new();
        private CancellationTokenSource? _debounceCts;
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>
        /// Default debounce delay in milliseconds.
        /// </summary>
        public const int DefaultDebounceDelayMs = 500;

        /// <summary>
        /// Default maximum pending changes before force flush.
        /// </summary>
        public const int DefaultMaxPendingChanges = 100;

        /// <summary>
        /// Event raised when a batch is ready.
        /// </summary>
        public event EventHandler<ChangeBatch>? BatchReady;

        /// <summary>
        /// Creates a new event debouncer.
        /// </summary>
        /// <param name="onBatchReady">Callback when batch is ready</param>
        /// <param name="debounceDelayMs">Delay before sending batch (default: 500ms)</param>
        /// <param name="maxPendingChanges">Max changes before force flush (default: 100)</param>
        public EventDebouncer(
            Action<ChangeBatch>? onBatchReady = null,
            int debounceDelayMs = DefaultDebounceDelayMs,
            int maxPendingChanges = DefaultMaxPendingChanges)
        {
            _debounceDelayMs = debounceDelayMs;
            _maxPendingChanges = maxPendingChanges;
            _onBatchReady = onBatchReady ?? (_ => { });
        }

        /// <summary>
        /// Adds a change event to the pending batch.
        /// </summary>
        public void AddChange(ChangeEvent change)
        {
            if (_disposed) return;

            lock (_lock)
            {
                _pendingBatch.AddChange(change);

                // Force flush if too many pending changes
                if (_pendingBatch.Changes.Count >= _maxPendingChanges)
                {
                    FlushInternal();
                    return;
                }

                // Reset/start debounce timer
                _debounceCts?.Cancel();
                _debounceCts = new CancellationTokenSource();

                var cts = _debounceCts;
                Task.Delay(_debounceDelayMs, cts.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && !_disposed)
                    {
                        Flush();
                    }
                });
            }
        }

        /// <summary>
        /// Forces the current batch to be sent immediately.
        /// </summary>
        public void Flush()
        {
            if (_disposed) return;

            lock (_lock)
            {
                FlushInternal();
            }
        }

        private void FlushInternal()
        {
            _debounceCts?.Cancel();
            _debounceCts = null;

            if (!_pendingBatch.HasChanges)
                return;

            var batch = _pendingBatch;
            _pendingBatch = new ChangeBatch();

            // Notify via callback and event
            try
            {
                _onBatchReady(batch);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EventDebouncer callback error: {ex.Message}");
            }

            try
            {
                BatchReady?.Invoke(this, batch);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EventDebouncer BatchReady error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current number of pending changes.
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _pendingBatch.Changes.Count;
                }
            }
        }

        /// <summary>
        /// Checks if there are pending changes.
        /// </summary>
        public bool HasPendingChanges
        {
            get
            {
                lock (_lock)
                {
                    return _pendingBatch.HasChanges;
                }
            }
        }

        /// <summary>
        /// Clears all pending changes without sending.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _debounceCts?.Cancel();
                _debounceCts = null;
                _pendingBatch = new ChangeBatch();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = null;
            }
        }
    }
}
