using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Interface for drawing analysis tools that can be invoked by the AI agent.
    /// </summary>
    public interface IDrawingTool
    {
        /// <summary>
        /// Unique tool name used for invocation.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Human-readable description for LLM context.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Tool category (Discovery, Alignment, Profile, Surface, Corridor, PipeNetwork, Validation).
        /// </summary>
        string Category { get; }

        /// <summary>
        /// Maximum execution time before timeout.
        /// </summary>
        TimeSpan Timeout { get; }

        /// <summary>
        /// True for tools that prompt the user modally (point picks, keyword
        /// confirms). The executor flushes pending chat renders before running
        /// them so step instructions are visible BEFORE input is expected.
        /// </summary>
        bool IsInteractive => false;

        /// <summary>
        /// JSON schema for tool parameters.
        /// </summary>
        JsonElement? ParameterSchema { get; }

        /// <summary>
        /// Executes the tool and returns the result.
        /// </summary>
        /// <param name="tr">Active AutoCAD transaction</param>
        /// <param name="civilDoc">Civil 3D document</param>
        /// <param name="parameters">Tool parameters as JSON</param>
        /// <param name="cache">Session-level cache for results</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tool execution result</returns>
        Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct);
    }

    /// <summary>
    /// Optional notification delivered by ToolExecutor only after its caller-owned
    /// transaction has genuinely committed. Evidence that depends on the final live
    /// database revision must be recorded here, never optimistically inside ExecuteAsync.
    /// </summary>
    public interface ITransactionCommitObserver
    {
        void OnTransactionCommitted(Database database, ToolResult result);
    }

    /// <summary>
    /// Optional notification delivered by <see cref="ToolExecutor"/> only after a
    /// successful read-only tool transaction has been aborted and disposed.  A tool
    /// that publishes files or advances session state from read-back data must defer
    /// that publication to this callback: an in-memory result observed through an
    /// open (or unsuccessfully closed) host transaction is not authoritative evidence.
    /// </summary>
    public interface IReadOnlyTransactionClosedObserver
    {
        void OnReadOnlyTransactionClosed(Database database, ToolResult result);
    }

    /// <summary>
    /// Explicit outcome of a tool call, decoupled from the legacy boolean
    /// <see cref="ToolResult.Success"/>. Introduced by the P0-01 remediation so the
    /// executor can distinguish a genuine committable success from a cancellation, an
    /// engineer-approval-pending candidate, or a design that failed its hard gates —
    /// none of which may silently commit a production object just because a payload
    /// happened to carry <c>success=true</c>.
    /// </summary>
    public enum ToolOutcome
    {
        /// <summary>The operation completed and its result may commit (subject to hard gates).</summary>
        Succeeded,

        /// <summary>The user cancelled before any production mutation. Neutral in the UI, never committed, not an error.</summary>
        Cancelled,

        /// <summary>
        /// A design candidate was produced but it relaxed a required constraint (radius, spiral, …)
        /// and/or requires explicit engineer approval. Must NOT commit as an approved production
        /// object; may only render preview/transient graphics when explicitly requested.
        /// </summary>
        CandidateGenerated,

        /// <summary>Execution succeeded but the design failed one or more hard engineering gates. Rolled back.</summary>
        Rejected,

        /// <summary>Execution itself failed (exception, missing object, invalid parameters, …). Rolled back.</summary>
        Failed
    }

    /// <summary>
    /// A single engineering finding attached to a tool result (P0-01). Carries enough
    /// location/quantity detail for the agent and the Hebrew UI to explain WHY a design
    /// was rejected or flagged, and — where applicable — the standard it came from.
    /// </summary>
    public sealed class EngineeringViolation
    {
        /// <summary>Stable machine code, e.g. FINAL_GEOMETRY_OUTSIDE_BUILDABLE, RADIUS_BELOW_MINIMUM.</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Human-readable message (may be Hebrew for UI paths).</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>"hard" = blocks commit; "warning" = informational only. Defaults to "hard".</summary>
        public string Severity { get; set; } = "hard";

        /// <summary>Owning entity / PI / PVI index when the finding is geometry-local.</summary>
        public int? Index { get; set; }

        /// <summary>Station (m) of the finding when applicable.</summary>
        public double? Station { get; set; }

        /// <summary>World X of the finding when applicable.</summary>
        public double? X { get; set; }

        /// <summary>World Y of the finding when applicable.</summary>
        public double? Y { get; set; }

        /// <summary>Requested/required value (e.g. required minimum radius).</summary>
        public double? Requested { get; set; }

        /// <summary>Achieved/actual value (e.g. radius actually placed).</summary>
        public double? Achieved { get; set; }

        /// <summary>Source citation for the rule, when the finding is standards-backed.</summary>
        public string? Citation { get; set; }
    }

    /// <summary>
    /// Result of evaluating a tool's engineering hard gates (P0-01). When present on a
    /// <see cref="ToolResult"/>, the executor's commit decision consults
    /// <see cref="HardGatesPassed"/> in addition to <see cref="ToolOutcome"/>.
    /// </summary>
    public sealed class EngineeringGateResult
    {
        /// <summary>True only when every hard gate passed. False blocks the commit.</summary>
        public bool HardGatesPassed { get; set; }

        /// <summary>True when a human engineer must approve before the result is treated as final.</summary>
        public bool RequiresEngineerApproval { get; set; }

        /// <summary>All findings (hard violations and warnings).</summary>
        public List<EngineeringViolation> Violations { get; set; } = new();
    }

    /// <summary>
    /// Result of tool execution.
    /// </summary>
    public class ToolResult
    {
        private ToolOutcome? _outcome;

        /// <summary>
        /// Whether the tool executed successfully.
        /// </summary>
        /// <remarks>
        /// Retained for protocol/back-compat. It is NO LONGER the commit gate — see
        /// <see cref="MayCommitProductionObject"/>. For results created through the
        /// legacy <see cref="Ok"/>/<see cref="Fail"/> factories, <see cref="Outcome"/>
        /// is derived from this flag so existing tools keep working unchanged.
        /// </remarks>
        public bool Success { get; set; }

        /// <summary>
        /// Explicit outcome (P0-01). Defaults to <see cref="ToolOutcome.Succeeded"/> when
        /// <see cref="Success"/> is true and <see cref="ToolOutcome.Failed"/> otherwise,
        /// unless a factory set it explicitly.
        /// </summary>
        public ToolOutcome Outcome
        {
            get => _outcome ?? (Success ? ToolOutcome.Succeeded : ToolOutcome.Failed);
            set => _outcome = value;
        }

        /// <summary>
        /// Engineering hard-gate result. Null means "no engineering gate applies" and the
        /// commit decision falls back to <see cref="Outcome"/> alone.
        /// </summary>
        public EngineeringGateResult? Engineering { get; set; }

        /// <summary>
        /// True when the successful result came from a read-only operation. The
        /// executor must abort its AutoCAD transaction even though the tool call
        /// itself succeeded; transient graphics and external evidence files are not
        /// database mutations and survive that abort independently.
        /// </summary>
        public bool IsReadOnly { get; private set; }

        /// <summary>
        /// THE commit gate (P0-01). A production object may be committed only when the
        /// outcome is <see cref="ToolOutcome.Succeeded"/> AND every engineering hard gate
        /// passed. Cancellations, candidates, rejections and failures never commit.
        /// </summary>
        public bool MayCommitProductionObject =>
            !IsReadOnly &&
            Outcome == ToolOutcome.Succeeded &&
            (Engineering?.HardGatesPassed ?? true);

        /// <summary>
        /// Result data (serializable).
        /// </summary>
        public object? Data { get; set; }

        /// <summary>
        /// Error information if execution failed.
        /// </summary>
        public ToolExecutionError? Error { get; set; }

        /// <summary>
        /// Whether the result was retrieved from cache.
        /// </summary>
        public bool FromCache { get; set; }

        /// <summary>
        /// Cache key used (if cacheable).
        /// </summary>
        public string? CacheKey { get; set; }

        /// <summary>
        /// Additional metadata about the execution.
        /// </summary>
        public ToolResultMetadata? Metadata { get; set; }

        /// <summary>
        /// Creates a successful (committable) result.
        /// </summary>
        public static ToolResult Ok(object? data, bool fromCache = false, string? cacheKey = null)
        {
            return new ToolResult
            {
                Success = true,
                Outcome = ToolOutcome.Succeeded,
                Data = data,
                FromCache = fromCache,
                CacheKey = cacheKey
            };
        }

        /// <summary>
        /// Creates a successful read-only result. The payload remains an ordinary
        /// successful outcome for the agent, while <see cref="MayCommitProductionObject"/>
        /// is false so the executor always aborts the AutoCAD transaction.
        /// </summary>
        public static ToolResult ReadOnly(object? data)
        {
            return new ToolResult
            {
                Success = true,
                Outcome = ToolOutcome.Succeeded,
                Data = data,
                IsReadOnly = true,
            };
        }

        /// <summary>
        /// Creates a failed result (execution error). Rolled back by the executor.
        /// </summary>
        public static ToolResult Fail(string code, string message, string? details = null)
        {
            return new ToolResult
            {
                Success = false,
                Outcome = ToolOutcome.Failed,
                Error = new ToolExecutionError
                {
                    Code = code,
                    Message = message,
                    Details = details
                }
            };
        }

        /// <summary>
        /// Creates a not found result.
        /// </summary>
        public static ToolResult NotFound(string objectType, string identifier)
        {
            return Fail(
                ToolErrorCodes.ObjectNotFound,
                $"{objectType} not found: {identifier}");
        }

        /// <summary>
        /// Creates a neutral cancellation result (P0-01): the user aborted before any
        /// production mutation. It is NOT an error (no red UI) and it NEVER commits —
        /// the executor aborts the (empty) transaction. <c>data</c> may carry a
        /// <c>cancelled=true</c> payload for the agent/UI.
        /// </summary>
        public static ToolResult Cancelled(object? data = null)
        {
            return new ToolResult
            {
                Success = true,
                Outcome = ToolOutcome.Cancelled,
                Data = data
            };
        }

        /// <summary>
        /// Creates a rejected result (P0-01): execution ran but the design failed one or
        /// more hard engineering gates. Rolled back by the executor. Surfaces as a failure
        /// to the agent, carrying structured <paramref name="violations"/>.
        /// </summary>
        public static ToolResult Rejected(
            string code,
            string message,
            IEnumerable<EngineeringViolation>? violations = null,
            object? data = null,
            bool requiresEngineerApproval = false)
        {
            return new ToolResult
            {
                Success = false,
                Outcome = ToolOutcome.Rejected,
                Data = data,
                Error = new ToolExecutionError { Code = code, Message = message },
                Engineering = new EngineeringGateResult
                {
                    HardGatesPassed = false,
                    RequiresEngineerApproval = requiresEngineerApproval,
                    Violations = violations != null
                        ? new List<EngineeringViolation>(violations)
                        : new List<EngineeringViolation>()
                }
            };
        }

        /// <summary>
        /// Creates a candidate result (P0-01): a design was produced but it relaxed a
        /// required constraint and/or needs engineer approval. It does NOT commit as an
        /// approved production object; callers that want a visible preview must render
        /// transient/temporary graphics explicitly.
        /// </summary>
        public static ToolResult Candidate(object? data, EngineeringGateResult engineering)
        {
            return new ToolResult
            {
                Success = true,
                Outcome = ToolOutcome.CandidateGenerated,
                Data = data,
                Engineering = engineering
            };
        }
    }

    /// <summary>
    /// Error details for tool execution.
    /// </summary>
    public class ToolExecutionError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
    }

    /// <summary>
    /// Additional metadata about tool execution.
    /// </summary>
    public class ToolResultMetadata
    {
        /// <summary>
        /// Number of items returned (for list operations).
        /// </summary>
        public int? ItemCount { get; set; }

        /// <summary>
        /// Whether results were truncated.
        /// </summary>
        public bool? Truncated { get; set; }

        /// <summary>
        /// Total available items (if truncated).
        /// </summary>
        public int? TotalAvailable { get; set; }

        /// <summary>
        /// Warnings generated during execution.
        /// </summary>
        public string[]? Warnings { get; set; }
    }

    /// <summary>
    /// Common error codes for tool execution.
    /// </summary>
    public static class ToolErrorCodes
    {
        public const string ObjectNotFound = "OBJECT_NOT_FOUND";
        public const string InvalidParameters = "INVALID_PARAMETERS";
        public const string AccessDenied = "ACCESS_DENIED";
        public const string ExecutionFailed = "EXECUTION_FAILED";
        public const string Timeout = "TIMEOUT";
        public const string NotSupported = "NOT_SUPPORTED";
        public const string TransactionRequired = "TRANSACTION_REQUIRED";
        public const string SubassemblyParameterNotFound = "SUBASSEMBLY_PARAMETER_NOT_FOUND";
    }

    /// <summary>
    /// Tool category constants.
    /// </summary>
    public static class ToolCategories
    {
        public const string Discovery = "Discovery";
        public const string Alignment = "Alignment";
        public const string Profile = "Profile";
        public const string Surface = "Surface";
        public const string Corridor = "Corridor";
        public const string PipeNetwork = "PipeNetwork";
        public const string Validation = "Validation";
        public const string Utility = "Utility";
        public const string Modification = "Modification";
        public const string Creation = "Creation";
    }

    /// <summary>
    /// Base class for drawing tools with common functionality.
    /// </summary>
    public abstract class DrawingToolBase : IDrawingTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string Category { get; }

        public virtual TimeSpan Timeout => TimeSpan.FromSeconds(30);
        public virtual JsonElement? ParameterSchema => null;

        public abstract Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct);

        /// <summary>
        /// Gets a string parameter value.
        /// </summary>
        protected static string? GetStringParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
            return null;
        }

        /// <summary>
        /// Gets a required string parameter value.
        /// </summary>
        protected static string GetRequiredStringParam(JsonElement parameters, string name)
        {
            var value = GetStringParam(parameters, name);
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"Required parameter '{name}' is missing or empty");
            }
            return value;
        }

        /// <summary>
        /// Gets an integer parameter value.
        /// </summary>
        protected static int? GetIntParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                {
                    // GetInt32() throws on decimals like 60.0 from Python/JSON
                    // Fall back to GetDouble() + cast for robustness
                    if (prop.TryGetInt32(out int intVal))
                        return intVal;
                    return (int)prop.GetDouble();
                }
            }
            return null;
        }

        /// <summary>
        /// Gets a double parameter value.
        /// </summary>
        protected static double? GetDoubleParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
            }
            return null;
        }

        /// <summary>
        /// Gets a boolean parameter value.
        /// </summary>
        protected static bool GetBoolParam(JsonElement parameters, string name, bool defaultValue = false)
        {
            if (parameters.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True)
                    return true;
                if (prop.ValueKind == JsonValueKind.False)
                    return false;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets a string array parameter value.
        /// </summary>
        protected static string[]? GetStringArrayParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.Array)
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (val != null)
                            list.Add(val);
                    }
                }
                return list.ToArray();
            }
            return null;
        }

        /// <summary>
        /// Generates a cache key based on tool name and parameters.
        /// </summary>
        protected string GenerateCacheKey(JsonElement parameters)
        {
            return $"{Name}:{parameters.GetRawText().GetHashCode():X8}";
        }
    }
}
