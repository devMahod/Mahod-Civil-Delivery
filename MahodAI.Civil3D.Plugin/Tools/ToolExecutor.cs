using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Utilities;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Executes drawing tools with proper AutoCAD thread marshaling,
    /// timeout handling, and caching.
    /// </summary>
    public class ToolExecutor
    {
        private readonly ToolRegistry _registry;
        private readonly ToolCache _cache;

        /// <summary>
        /// Serializes ALL main-thread tool executions across every executor
        /// instance and every session — concurrent tool_calls must never
        /// interleave (2026-08-03 parallel-sessions fix).
        /// </summary>
        private static readonly SerialToolQueue MainThreadQueue = new();

        /// <summary>
        /// Resolves a session id to its bound drawing (wired once at plugin
        /// init from the chat UI's tab map). Null resolver or null session id
        /// preserves the legacy active-document behavior.
        /// </summary>
        public static Func<string, SessionBinding>? SessionBindingResolver { get; set; }

        /// <summary>
        /// Max Idle ticks spent waiting for a bound document activation to
        /// take effect before the tool fails with drawing_unavailable.
        /// </summary>
        private const int MaxActivationAttempts = 60;

        /// <summary>
        /// Event raised when tool execution starts.
        /// </summary>
        public event EventHandler<ToolExecutionEventArgs>? ExecutionStarted;

        /// <summary>
        /// Event raised when tool execution completes.
        /// </summary>
        public event EventHandler<ToolExecutionEventArgs>? ExecutionCompleted;

        /// <summary>
        /// Gets the tool cache for external invalidation (e.g., drawing change events).
        /// </summary>
        public ToolCache Cache => _cache;

        public ToolExecutor(ToolCache? cache = null)
        {
            _registry = ToolRegistry.Instance;
            _cache = cache ?? new ToolCache();
        }

        /// <summary>
        /// Executes a tool call received from the agent.
        /// </summary>
        public async Task<ToolResultPayload> ExecuteToolCallAsync(
            ToolCallPayload toolCall,
            string? sessionId = null,
            CancellationToken ct = default)
        {
            var stopwatch = Stopwatch.StartNew();
            MahodLogger.Info($"Executing tool: {toolCall.ToolName}");

            try
            {
                var tool = _registry.GetTool(toolCall.ToolName);
                if (tool == null)
                {
                    return CreateErrorResult(
                        toolCall.ToolCallId,
                        ErrorCodes.ToolNotFound,
                        $"Tool not found: {toolCall.ToolName}",
                        stopwatch.ElapsedMilliseconds);
                }

                // Create a timeout cancellation token
                var toolTimeout = tool.Timeout;
                if (toolCall.TimeoutSeconds > 0)
                {
                    toolTimeout = TimeSpan.FromSeconds(toolCall.TimeoutSeconds);
                }

                // Create the timeout source in a NON-armed state. The budget is
                // started (CancelAfter) only once execution actually begins on
                // AutoCAD's main thread — time spent waiting for the Idle event
                // must not be charged against the tool's execution budget.
                using var timeoutCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                OnExecutionStarted(toolCall.ToolCallId, toolCall.ToolName);

                // Execute on AutoCAD's main thread. Every execution goes through
                // one strict FIFO queue so tool_calls from concurrent sessions
                // never interleave; queue wait time is not charged against the
                // tool's timeout budget (armed only when execution begins).
                var result = await MainThreadQueue.Enqueue(
                    async () =>
                    {
                        // Interactive tools (modal picks/confirms) must not start
                        // before previously queued chat renders are committed —
                        // otherwise the instruction for a step appears only after
                        // the step's input (the "hint one step late" bug).
                        if (tool.IsInteractive)
                        {
                            await ToolUiNotifier.FlushChatAsync();
                        }

                        try
                        {
                            return await ExecuteOnMainThreadAsync(
                                tool,
                                toolCall.Arguments ?? JsonDocument.Parse("{}").RootElement,
                                linkedCts.Token,
                                timeoutCts,
                                toolTimeout,
                                sessionId);
                        }
                        finally
                        {
                            // The live step line belongs to a running pick sequence.
                            // Clearing it HERE covers every exit path of every
                            // interactive tool — success, Esc, timeout or throw —
                            // so a finished flow can never leave a stale
                            // "בחר נקודת סיום" sitting on screen.
                            if (tool.IsInteractive)
                            {
                                ToolUiNotifier.ClearStep();
                            }
                        }
                    },
                    linkedCts.Token);

                stopwatch.Stop();
                MahodLogger.ToolExecution(toolCall.ToolName, result.Success, stopwatch.ElapsedMilliseconds);

                OnExecutionCompleted(toolCall.ToolCallId, toolCall.ToolName, result.Success, stopwatch.ElapsedMilliseconds);

                return new ToolResultPayload
                {
                    ToolCallId = toolCall.ToolCallId,
                    Success = result.Success,
                    Outcome = OutcomeWireValue(result.Outcome),
                    HardGatesPassed = result.Engineering?.HardGatesPassed,
                    RequiresEngineerApproval = result.Engineering?.RequiresEngineerApproval,
                    Violations = MapViolations(result.Engineering),
                    Result = result.Data,
                    Error = result.Error != null ? new ToolError
                    {
                        Code = result.Error.Code,
                        Message = result.Error.Message,
                        Details = result.Error.Details
                    } : null,
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                    Cached = result.FromCache
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                MahodLogger.ToolExecution(toolCall.ToolName, false, stopwatch.ElapsedMilliseconds, "Timeout/cancelled");
                return CreateErrorResult(
                    toolCall.ToolCallId,
                    ErrorCodes.ToolTimeout,
                    $"Tool execution timed out: {toolCall.ToolName}",
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                MahodLogger.ToolExecution(toolCall.ToolName, false, stopwatch.ElapsedMilliseconds, ex.Message);
                return CreateErrorResult(
                    toolCall.ToolCallId,
                    ErrorCodes.ToolExecutionFailed,
                    $"Tool execution failed: {ex.Message}",
                    stopwatch.ElapsedMilliseconds,
                    ex.ToString());
            }
        }

        /// <summary>
        /// Marshals tool execution to AutoCAD's main thread via Application.Idle,
        /// ensuring all database access happens synchronously on the correct thread.
        /// </summary>
        /// <remarks>
        /// Timeout/cancellation is coordinated so that a tool the agent has
        /// already recorded as failed/timed-out can never leave a committed
        /// mutation behind (the "a failed tool is side-effect-free" contract):
        /// <list type="bullet">
        /// <item>The timeout budget is armed (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>)
        /// only once the Idle handler actually starts running, so waiting for
        /// the Idle event does not consume it.</item>
        /// <item>A <c>gate</c> latch ensures that once main-thread execution has
        /// begun, ONLY the main thread completes the task. The timeout callback
        /// stands down instead of racing the document-lock code to report
        /// cancellation while the commit still goes through.</item>
        /// <item>Cancellation is re-checked immediately before
        /// <c>Transaction.Commit</c>; if the deadline passed while executing
        /// under the document lock, the transaction is aborted rather than
        /// committed.</item>
        /// </list>
        /// </remarks>
        private Task<ToolResult> ExecuteOnMainThreadAsync(
            IDrawingTool tool,
            JsonElement parameters,
            CancellationToken ct,
            CancellationTokenSource timeoutCts,
            TimeSpan toolTimeout,
            string? sessionId = null)
        {
            var tcs = new TaskCompletionSource<ToolResult>();
            int activationAttempts = 0;

            // Gate coordinating who is allowed to complete <paramref name="tcs"/>.
            // Once the main thread flips <c>executing</c> to true (under the lock),
            // the timeout/cancellation callback must stand down: from that point
            // the Idle handler alone completes the task. This closes the race
            // where the timer thread reported cancellation while the main thread
            // proceeded to commit the mutation anyway.
            var gate = new object();
            bool executing = false;

            // Register a one-shot Idle handler to run on AutoCAD's main thread
            void IdleHandler(object? sender, EventArgs e)
            {
                Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= IdleHandler;

                try
                {
                    // If cancellation was already requested (e.g. the caller's
                    // token fired before Idle got a chance to run), do not begin.
                    lock (gate)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled(ct);
                            return;
                        }
                    }

                    // Session→document affinity (2026-08-03): decide which drawing
                    // this session's tool may touch BEFORE claiming execution, so a
                    // pending activation wait can still be cancelled cleanly. A tool
                    // from session X must never run against another session's drawing.
                    var docMgr = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager;
                    var plan = PlanTarget(sessionId, docMgr);
                    if (plan.Kind == ToolTargetKind.Fail)
                    {
                        tcs.TrySetResult(ToolResult.Fail(
                            ToolTargetPlanner.ErrorCodeDrawingUnavailable,
                            plan.ErrorMessageHe!));
                        return;
                    }
                    if (plan.Kind == ToolTargetKind.Activate)
                    {
                        if (activationAttempts++ < MaxActivationAttempts)
                        {
                            TryActivateDocument(docMgr, plan.TargetDocument!);
                            // The switch completes on a later message pump — re-check
                            // on the next Idle tick. The timeout budget is NOT armed
                            // during activation waits.
                            Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle += IdleHandler;
                            return;
                        }
                        tcs.TrySetResult(ToolResult.Fail(
                            ToolTargetPlanner.ErrorCodeDrawingUnavailable,
                            $"לא ניתן להפעיל את השרטוט '{ToolTargetPlanner.DisplayName(plan.TargetDocument!)}' עבור השיחה — עבור אליו ידנית והרץ את הפעולה שוב."));
                        return;
                    }

                    // Claim the outcome for the main thread.
                    lock (gate)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled(ct);
                            return;
                        }

                        executing = true;
                    }

                    // Execution is genuinely beginning now — start the timeout
                    // budget. Time spent waiting for the Idle event / document
                    // activation above is not charged against the tool.
                    try { timeoutCts.CancelAfter(toolTimeout); }
                    catch (ObjectDisposedException) { /* caller already tore down */ }

                    var doc = docMgr.MdiActiveDocument;
                    if (doc == null)
                    {
                        tcs.TrySetResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active document"));
                        return;
                    }

                    var db = doc.Database;
                    CivilDocument? civilDoc = null;
                    try { civilDoc = CivilDocument.GetCivilDocument(db); }
                    catch { /* Not a Civil 3D document */ }

                    using (doc.LockDocument())
                    {
                        ToolResult? result = null;
                        bool cancelled = false;
                        bool committed = false;
                        bool notifyReadOnlyClosed = false;
                        try
                        {
                            // Keep every callback outside this scope. Leaving the using
                            // block is the proof that Commit/Abort AND Dispose succeeded;
                            // files/session state derived from a read transaction may not
                            // be published merely because Abort() returned.
                            using (var tr = db.TransactionManager.StartTransaction())
                            {
                                try
                                {
                                    // Execute synchronously on the main thread to avoid
                                    // thread-switching issues with await inside a document lock
                                    result = tool.ExecuteAsync(tr, civilDoc!, parameters, _cache, ct)
                                        .GetAwaiter().GetResult();

                                    // Re-check cancellation immediately before committing.
                                    // If the timeout fired while the tool executed under the
                                    // document lock, the agent has already recorded this call
                                    // as failed/timed-out — committing here would mutate the
                                    // drawing behind its back. Abort instead.
                                    if (ct.IsCancellationRequested)
                                    {
                                        tr.Abort();
                                        cancelled = true;
                                    }
                                    // Commit ONLY when the result may commit a production
                                    // object (P0-01). Read-only successes are aborted, then
                                    // may publish evidence only after this transaction scope
                                    // has also disposed successfully.
                                    else if (result.MayCommitProductionObject)
                                    {
                                        tr.Commit();
                                        committed = true;
                                    }
                                    else
                                    {
                                        tr.Abort();
                                        notifyReadOnlyClosed =
                                            result.IsReadOnly &&
                                            result.Success &&
                                            result.Outcome == ToolOutcome.Succeeded;
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    tr.Abort();
                                    cancelled = true;
                                }
                                catch (Exception ex)
                                {
                                    tr.Abort();
                                    result = ToolResult.Fail(
                                        ToolErrorCodes.ExecutionFailed,
                                        ex.Message,
                                        ex.ToString());
                                }
                            }

                            if (cancelled)
                            {
                                tcs.TrySetCanceled(ct);
                                return;
                            }

                            if (result == null)
                                throw new InvalidOperationException(
                                    "Tool execution completed without a result.");

                            if (committed)
                                NotifyTransactionCommitted(tool, db, result);
                            else if (notifyReadOnlyClosed)
                                NotifyReadOnlyTransactionClosed(tool, db, result);

                            tcs.TrySetResult(result);
                        }
                        catch (OperationCanceledException)
                        {
                            tcs.TrySetCanceled(ct);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetResult(ToolResult.Fail(
                                ToolErrorCodes.ExecutionFailed,
                                ex.Message,
                                ex.ToString()));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(ct);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(ToolResult.Fail(
                        ToolErrorCodes.ExecutionFailed,
                        ex.Message,
                        ex.ToString()));
                }
            }

            Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle += IdleHandler;

            // After registering idle handler, trigger an idle cycle
            try
            {
                // Force screen update to trigger idle event sooner
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { }));
            }
            catch { /* dispatcher may not be available */ }

            // Cancellation/timeout callback. It may run on a ThreadPool timer
            // thread concurrently with the Idle handler, so it only cancels the
            // task while execution has NOT yet begun. Once <c>executing</c> is
            // set, the Idle handler owns the outcome (and performs its own
            // pre-commit cancellation check), so this callback stands down —
            // preventing a "reported failed but committed anyway" outcome.
            ct.Register(() =>
            {
                lock (gate)
                {
                    if (executing)
                    {
                        return;
                    }
                }

                Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= IdleHandler;
                tcs.TrySetCanceled(ct);
            });

            return tcs.Task;
        }

        /// <summary>
        /// Resolves the drawing a session-bound tool may touch. Runs on the
        /// main thread (Idle). Any resolver failure fails CLOSED — never fall
        /// back to "whatever drawing is active" for a bound session.
        /// </summary>
        private static ToolTargetPlan PlanTarget(
            string? sessionId,
            Autodesk.AutoCAD.ApplicationServices.DocumentCollection docMgr)
        {
            var resolver = SessionBindingResolver;
            if (resolver == null || string.IsNullOrEmpty(sessionId))
            {
                return ToolTargetPlan.UseActive();
            }

            SessionBinding binding;
            try
            {
                binding = resolver(sessionId!);
            }
            catch (Exception ex)
            {
                MahodLogger.Warning($"Session binding resolver failed for {sessionId}: {ex.Message}");
                return ToolTargetPlan.Fail(
                    "לא ניתן לאמת לאיזה שרטוט שייכת השיחה — נסה שוב או פתח שיחה חדשה על השרטוט.");
            }

            var openDocs = new List<string>();
            string? active = null;
            try
            {
                foreach (Autodesk.AutoCAD.ApplicationServices.Document d in docMgr)
                {
                    openDocs.Add(d.Name);
                }
                active = docMgr.MdiActiveDocument?.Name;
            }
            catch (Exception ex)
            {
                MahodLogger.Warning($"Enumerating open documents failed: {ex.Message}");
            }

            return ToolTargetPlanner.Plan(sessionId, binding, openDocs, active);
        }

        /// <summary>
        /// Makes the named document active. Verified (and retried) by the
        /// caller on the next Idle tick.
        /// </summary>
        private static void TryActivateDocument(
            Autodesk.AutoCAD.ApplicationServices.DocumentCollection docMgr,
            string targetName)
        {
            try
            {
                foreach (Autodesk.AutoCAD.ApplicationServices.Document d in docMgr)
                {
                    if (string.Equals(d.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        docMgr.MdiActiveDocument = d;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MahodLogger.Warning($"Activating document '{targetName}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes a tool by name with arguments JsonElement.
        /// This is a convenience wrapper around ExecuteToolCallAsync.
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            string toolName,
            JsonElement arguments,
            CancellationToken ct = default,
            string? sessionId = null)
        {
            var toolCall = new ToolCallPayload
            {
                ToolCallId = Guid.NewGuid().ToString("N"),
                ToolName = toolName,
                Arguments = arguments
            };

            var result = await ExecuteToolCallAsync(toolCall, sessionId, ct);

            // Convert ToolResultPayload back to ToolResult, preserving the P0-01 outcome
            // contract so callers of this wrapper see the same commit semantics.
            var roundTrip = new ToolResult
            {
                Success = result.Success,
                Outcome = ParseOutcome(result.Outcome, result.Success),
                Data = result.Result,
                Error = result.Error != null ? new ToolExecutionError
                {
                    Code = result.Error.Code,
                    Message = result.Error.Message,
                    Details = result.Error.Details
                } : null,
                FromCache = result.Cached
            };

            if (result.HardGatesPassed.HasValue
                || result.RequiresEngineerApproval.HasValue
                || (result.Violations != null && result.Violations.Count > 0))
            {
                roundTrip.Engineering = new EngineeringGateResult
                {
                    HardGatesPassed = result.HardGatesPassed ?? true,
                    RequiresEngineerApproval = result.RequiresEngineerApproval ?? false,
                    Violations = result.Violations?.ConvertAll(v => new EngineeringViolation
                    {
                        Code = v.Code,
                        Message = v.Message,
                        Severity = v.Severity,
                        Index = v.Index,
                        Station = v.Station,
                        X = v.X,
                        Y = v.Y,
                        Requested = v.Requested,
                        Achieved = v.Achieved,
                        Citation = v.Citation
                    }) ?? new List<EngineeringViolation>()
                };
            }

            return roundTrip;
        }

        /// <summary>
        /// Parses a wire outcome string back to <see cref="ToolOutcome"/> (P0-01),
        /// falling back to the legacy boolean when the field is absent (older payloads).
        /// </summary>
        internal static ToolOutcome ParseOutcome(string? wire, bool success) => wire switch
        {
            "succeeded" => ToolOutcome.Succeeded,
            "cancelled" => ToolOutcome.Cancelled,
            "candidate_generated" => ToolOutcome.CandidateGenerated,
            "rejected" => ToolOutcome.Rejected,
            "failed" => ToolOutcome.Failed,
            _ => success ? ToolOutcome.Succeeded : ToolOutcome.Failed
        };

        /// <summary>
        /// Executes a tool directly (for testing or when already on main thread).
        /// </summary>
        public async Task<ToolResult> ExecuteDirectAsync(
            string toolName,
            JsonElement parameters,
            Transaction tr,
            CivilDocument civilDoc,
            CancellationToken ct = default)
        {
            var tool = _registry.GetTool(toolName);
            if (tool == null)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.ObjectNotFound,
                    $"Tool not found: {toolName}");
            }

            return await tool.ExecuteAsync(tr, civilDoc, parameters, _cache, ct);
        }

        /// <summary>
        /// Executes a batch of tool calls. When <paramref name="atomic"/> is <c>false</c>
        /// (default), each item gets its own <c>._UNDO _BEGIN / _END</c> group — execution
        /// continues on failure so partial fixes are possible and each item can be undone
        /// individually. When <paramref name="atomic"/> is <c>true</c>, all items run inside
        /// a single transaction — if any item fails the entire transaction is aborted and
        /// no changes are committed.
        /// </summary>
        /// <remarks>
        /// AutoCAD's managed API does not expose a typed "UndoMark" primitive suitable
        /// for our needs; the reliable cross-version approach is to drive the
        /// <c>._UNDO</c> command via the <see cref="Autodesk.AutoCAD.EditorInput.Editor"/>.
        /// Each <c>_BEGIN/_END</c> pair creates a group that the user can roll back as a
        /// unit with a single Ctrl+Z, matching the product requirement of "one Undo
        /// reverts one fix".
        /// </remarks>
        public async Task<List<FixItemResult>> ExecuteBatchAsync(
            List<ToolCallPayload> toolCalls,
            bool atomic = false,
            CancellationToken ct = default)
        {
            MahodLogger.Info($"Executing fix batch: {toolCalls.Count} items (atomic={atomic})");

            var results = new List<FixItemResult>();

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                foreach (var tc in toolCalls)
                {
                    results.Add(new FixItemResult
                    {
                        ItemId = tc.ToolCallId,
                        Success = false,
                        Error = "No active document",
                    });
                }
                return results;
            }

            if (atomic)
            {
                return await ExecuteBatchAtomicAsync(toolCalls, doc, ct);
            }

            for (int i = 0; i < toolCalls.Count; i++)
            {
                var toolCall = toolCalls[i];
                MahodLogger.Info($"Fix item {i + 1}/{toolCalls.Count}: {toolCall.ToolName}");

                if (ct.IsCancellationRequested)
                {
                    results.Add(new FixItemResult
                    {
                        ItemId = toolCall.ToolCallId,
                        Success = false,
                        Error = "Batch cancelled",
                    });
                    continue;
                }

                bool undoOpen = false;
                try
                {
                    // Open an UNDO group scoped to this single item.
                    try
                    {
                        using (doc.LockDocument())
                        {
                            doc.Editor.Command("._UNDO", "_BEGIN");
                        }
                        undoOpen = true;
                    }
                    catch (Exception ex)
                    {
                        MahodLogger.Warning($"UNDO BEGIN failed for item {toolCall.ToolCallId}: {ex.Message}");
                    }

                    ToolResultPayload payload;
                    try
                    {
                        payload = await ExecuteToolCallAsync(toolCall, sessionId: null, ct);
                    }
                    catch (Exception ex)
                    {
                        // Defensive — ExecuteToolCallAsync already catches, but never trust it.
                        payload = new ToolResultPayload
                        {
                            ToolCallId = toolCall.ToolCallId,
                            Success = false,
                            Error = new ToolError
                            {
                                Code = ErrorCodes.ToolExecutionFailed,
                                Message = ex.Message,
                                Details = ex.ToString()
                            }
                        };
                    }

                    results.Add(new FixItemResult
                    {
                        ItemId = toolCall.ToolCallId,
                        Success = payload.Success,
                        Output = payload.Success ? payload.Result : null,
                        Error = payload.Success ? null : (payload.Error?.Message ?? "Unknown error"),
                    });
                }
                finally
                {
                    if (undoOpen)
                    {
                        try
                        {
                            using (doc.LockDocument())
                            {
                                doc.Editor.Command("._UNDO", "_END");
                            }
                        }
                        catch (Exception ex)
                        {
                            MahodLogger.Warning($"UNDO END failed for item {toolCall.ToolCallId}: {ex.Message}");
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Atomic batch execution: all items run in a single document lock and transaction.
        /// If any item fails, the entire transaction is aborted and all items are marked failed.
        /// </summary>
        private async Task<List<FixItemResult>> ExecuteBatchAtomicAsync(
            List<ToolCallPayload> toolCalls,
            Document doc,
            CancellationToken ct)
        {
            var results = new List<FixItemResult>();
            var completedResults = new List<FixItemResult>();

            try
            {
                using (doc.LockDocument())
                {
                    var db = doc.Database;
                    CivilDocument? civilDoc = null;
                    try { civilDoc = CivilDocument.GetCivilDocument(db); }
                    catch { /* Not a Civil 3D document */ }

                    // Observer tools keep per-invocation pending publication state.
                    // The registry returns singleton instances, so invoking the same
                    // observer twice in one atomic transaction would let the second
                    // call overwrite the first callback's payload. Reject the entire
                    // batch before opening a transaction or executing either item.
                    var resolvedCalls = new List<(ToolCallPayload Call, IDrawingTool Tool)>();
                    var observerNames = new HashSet<string>(StringComparer.Ordinal);
                    var observerInstances = new HashSet<IDrawingTool>(
                        ReferenceEqualityComparer.Instance);
                    foreach (var toolCall in toolCalls)
                    {
                        var resolvedTool = _registry.GetTool(toolCall.ToolName);
                        if (resolvedTool == null)
                        {
                            var message = $"Tool not found: {toolCall.ToolName}";
                            return toolCalls.Select(call => new FixItemResult
                            {
                                ItemId = call.ToolCallId,
                                Success = false,
                                Error = "Atomic batch rejected before transaction: " + message,
                            }).ToList();
                        }
                        if (resolvedTool is ITransactionCommitObserver &&
                            (!observerNames.Add(resolvedTool.Name) ||
                             !observerInstances.Add(resolvedTool)))
                        {
                            var message =
                                $"duplicate post-commit observer tool '{resolvedTool.Name}'";
                            return toolCalls.Select(call => new FixItemResult
                            {
                                ItemId = call.ToolCallId,
                                Success = false,
                                Error = "Atomic batch rejected before transaction: " + message,
                            }).ToList();
                        }
                        resolvedCalls.Add((toolCall, resolvedTool));
                    }

                    // Bind every post-commit callback to the exact public batch item
                    // it can invalidate. Observer tools are only a subset of a mixed
                    // atomic batch, so their list index is not the result-list index.
                    var commitObservers = new List<(
                        IDrawingTool Tool,
                        ToolResult Result,
                        FixItemResult CompletedResult)>();
                    Exception? transactionFailure = null;

                    // Do not publish or return success from inside this scope. A
                    // successful Commit is not the complete host boundary: Dispose
                    // must also finish before post-commit evidence becomes public.
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            for (int i = 0; i < resolvedCalls.Count; i++)
                            {
                                var (toolCall, tool) = resolvedCalls[i];
                                MahodLogger.Info($"Atomic fix item {i + 1}/{toolCalls.Count}: {toolCall.ToolName}");

                                if (ct.IsCancellationRequested)
                                {
                                    throw new OperationCanceledException(ct);
                                }

                                var stopwatch = Stopwatch.StartNew();
                                var result = await tool.ExecuteAsync(tr, civilDoc!, toolCall.Arguments ?? JsonDocument.Parse("{}").RootElement, _cache, ct);
                                stopwatch.Stop();

                                MahodLogger.ToolExecution(toolCall.ToolName, result.Success, stopwatch.ElapsedMilliseconds);

                                // Fail-closed on the same gate as the single-tool path
                                // (P0-01): a cancelled, candidate or rejected item must
                                // abort the whole atomic batch, not just an execution
                                // Fail. Any item that may not commit a production object
                                // rolls the batch back.
                                if (!result.MayCommitProductionObject)
                                {
                                    var errorMsg = result.Error?.Message
                                        ?? $"outcome={result.Outcome}";
                                    throw new InvalidOperationException(
                                        $"Atomic batch aborted at item {i + 1}/{toolCalls.Count} ({toolCall.ToolName}): {errorMsg}");
                                }

                                var completedResult = new FixItemResult
                                {
                                    ItemId = toolCall.ToolCallId,
                                    Success = true,
                                    Output = result.Data,
                                };
                                completedResults.Add(completedResult);
                                if (tool is ITransactionCommitObserver)
                                    commitObservers.Add((tool, result, completedResult));
                            }

                            tr.Commit();
                        }
                        catch (Exception ex)
                        {
                            tr.Abort();
                            transactionFailure = ex;
                        }
                    }

                    if (transactionFailure != null)
                    {
                        MahodLogger.Error(
                            $"Atomic batch aborted: {transactionFailure.Message}",
                            transactionFailure);

                        // All items fail when atomic batch aborts
                        foreach (var tc in toolCalls)
                        {
                            results.Add(new FixItemResult
                            {
                                ItemId = tc.ToolCallId,
                                Success = false,
                                Error = $"Atomic batch aborted: {transactionFailure.Message}",
                            });
                        }
                        return results;
                    }

                    // The transaction has committed AND disposed successfully here.
                    foreach (var observer in commitObservers)
                        NotifyTransactionCommitted(observer.Tool, db, observer.Result);
                    foreach (var observer in commitObservers)
                    {
                        var postCommit = observer.Result;
                        if (postCommit.Success && postCommit.Outcome == ToolOutcome.Succeeded)
                            continue;
                        observer.CompletedResult.Success = false;
                        observer.CompletedResult.Output = null;
                        observer.CompletedResult.Error = postCommit.Error?.Message ??
                            "Post-commit evidence publication failed.";
                    }
                    MahodLogger.Info($"Atomic batch committed: {toolCalls.Count} items");
                    return completedResults;
                }
            }
            catch (Exception ex)
            {
                MahodLogger.Error($"Atomic batch lock/transaction close failed: {ex.Message}", ex);

                foreach (var tc in toolCalls)
                {
                    results.Add(new FixItemResult
                    {
                        ItemId = tc.ToolCallId,
                        Success = false,
                        Error = $"Failed to lock or close drawing transaction: {ex.Message}",
                    });
                }
                return results;
            }
        }

        private static void NotifyTransactionCommitted(
            IDrawingTool tool, Database database, ToolResult result)
        {
            if (tool is not ITransactionCommitObserver observer) return;
            try { observer.OnTransactionCommitted(database, result); }
            catch (Exception ex)
            {
                // The production transaction is already durable. Keep the tool result
                // honest and make any later VERIFY fail closed on missing evidence.
                MahodLogger.Error(
                    $"Post-commit evidence callback failed for {tool.Name}: {ex.Message}", ex);
                result.Success = false;
                result.Outcome = ToolOutcome.Failed;
                result.Error = new ToolExecutionError
                {
                    Code = ToolErrorCodes.ExecutionFailed,
                    Message = "The drawing transaction committed, but post-commit evidence publication failed; delivery is blocked.",
                    Details = ex.ToString(),
                };
            }
        }

        private static void NotifyReadOnlyTransactionClosed(
            IDrawingTool tool, Database database, ToolResult result)
        {
            if (tool is not IReadOnlyTransactionClosedObserver observer) return;
            try { observer.OnReadOnlyTransactionClosed(database, result); }
            catch (Exception ex)
            {
                // No drawing mutation was committed, but evidence/session publication
                // is part of the public result. Never return a stale in-memory green if
                // post-close publication failed.
                MahodLogger.Error(
                    $"Post-close read-only evidence callback failed for {tool.Name}: {ex.Message}", ex);
                result.Success = false;
                result.Outcome = ToolOutcome.Failed;
                result.Error = new ToolExecutionError
                {
                    Code = ToolErrorCodes.ExecutionFailed,
                    Message = "The read-only drawing transaction closed, but authoritative evidence publication failed; delivery is blocked.",
                    Details = ex.ToString(),
                };
            }
        }

        /// <summary>
        /// Maps a <see cref="ToolOutcome"/> to its stable wire string (P0-01).
        /// </summary>
        internal static string OutcomeWireValue(ToolOutcome outcome) => outcome switch
        {
            ToolOutcome.Succeeded => "succeeded",
            ToolOutcome.Cancelled => "cancelled",
            ToolOutcome.CandidateGenerated => "candidate_generated",
            ToolOutcome.Rejected => "rejected",
            ToolOutcome.Failed => "failed",
            _ => "failed"
        };

        /// <summary>
        /// Projects an <see cref="EngineeringGateResult"/>'s violations onto the wire payload.
        /// Returns null when there is nothing to send (keeps the envelope lean for the
        /// overwhelming majority of tools that carry no engineering gate).
        /// </summary>
        private static List<EngineeringViolationPayload>? MapViolations(EngineeringGateResult? engineering)
        {
            if (engineering == null || engineering.Violations.Count == 0)
            {
                return null;
            }

            var list = new List<EngineeringViolationPayload>(engineering.Violations.Count);
            foreach (var v in engineering.Violations)
            {
                list.Add(new EngineeringViolationPayload
                {
                    Code = v.Code,
                    Message = v.Message,
                    Severity = v.Severity,
                    Index = v.Index,
                    Station = v.Station,
                    X = v.X,
                    Y = v.Y,
                    Requested = v.Requested,
                    Achieved = v.Achieved,
                    Citation = v.Citation
                });
            }
            return list;
        }

        private static ToolResultPayload CreateErrorResult(
            string toolCallId,
            string code,
            string message,
            long executionTimeMs,
            string? details = null)
        {
            return new ToolResultPayload
            {
                ToolCallId = toolCallId,
                Success = false,
                Error = new ToolError
                {
                    Code = code,
                    Message = message,
                    Details = details
                },
                ExecutionTimeMs = executionTimeMs
            };
        }

        private void OnExecutionStarted(string toolCallId, string toolName)
        {
            ExecutionStarted?.Invoke(this, new ToolExecutionEventArgs
            {
                ToolCallId = toolCallId,
                ToolName = toolName
            });
        }

        private void OnExecutionCompleted(string toolCallId, string toolName, bool success, long elapsedMs)
        {
            ExecutionCompleted?.Invoke(this, new ToolExecutionEventArgs
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                Success = success,
                ExecutionTimeMs = elapsedMs
            });
        }
    }

    /// <summary>
    /// Event args for tool execution events.
    /// </summary>
    public class ToolExecutionEventArgs : EventArgs
    {
        public string ToolCallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public long ExecutionTimeMs { get; set; }
    }

    /// <summary>
    /// Result of a single fix-plan item executed by <see cref="ToolExecutor.ExecuteBatchAsync"/>.
    /// Carries enough detail for the agent-side verifier and the Hebrew UI summary.
    /// </summary>
    public class FixItemResult
    {
        /// <summary>Plan item id (matches <c>FixPlanItem.id</c>).</summary>
        public string ItemId { get; set; } = string.Empty;

        /// <summary>True when the tool returned <c>success=true</c>.</summary>
        public bool Success { get; set; }

        /// <summary>Tool output data on success (may be null).</summary>
        public object? Output { get; set; }

        /// <summary>Human-readable error message on failure (null on success).</summary>
        public string? Error { get; set; }
    }
}
