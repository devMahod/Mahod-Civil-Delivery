using System;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Pure binary-search relaxation logic used by
    /// <see cref="ModifyProfileVerticalCurveTool"/>. Civil 3D rejects a requested
    /// vertical-curve K-value (or curve length) when the adjacent PVI spacing can't
    /// fit <c>L = K · |Δg|</c> or otherwise invalidates the parabola. This helper
    /// searches between the requested value (ceiling) and the current value (floor)
    /// for the largest value the geometry accepts.
    ///
    /// Semantics are identical to <see cref="RadiusAdjustmentSearch"/> so the caller
    /// logic matches one-to-one: the geometry write is injected as <c>tryApply</c>
    /// (returns <c>null</c> on success, the rejection message on failure), and the
    /// caller decides from <see cref="Outcome.ExactApplied"/> whether to commit
    /// (exact_only=false) or to Fail and let the transaction abort roll the probe
    /// writes back (exact_only=true).
    ///
    /// Extracted from the tool so the decision logic is unit-testable without an
    /// AutoCAD runtime.
    /// </summary>
    public static class VerticalCurveAdjustmentSearch
    {
        /// <summary>Default maximum number of probe attempts.</summary>
        public const int DefaultMaxRetries = 6;

        /// <summary>
        /// Default convergence window. K and length are both small-magnitude
        /// quantities (a K of a few tens, a length of tens of meters), so the
        /// inline loop this replaces used a 0.5 window — preserved here.
        /// </summary>
        public const double DefaultConvergenceWindow = 0.5;

        /// <summary>Result of a relaxation search.</summary>
        public sealed class Outcome
        {
            /// <summary>True when the FIRST attempt (the exact requested value) was accepted.</summary>
            public bool ExactApplied { get; init; }

            /// <summary>
            /// The largest value the geometry accepted. Equals the current value
            /// when no value above it was accepted (every probe was rejected).
            /// </summary>
            public double Achieved { get; init; }

            /// <summary>True when the search had to relax below the requested value.</summary>
            public bool Relaxed { get; init; }

            /// <summary>Number of probe attempts performed.</summary>
            public int Attempts { get; init; }

            /// <summary>Last rejection message from the geometry engine (null if never rejected).</summary>
            public string? LastError { get; init; }

            /// <summary>True when at least one probe landed above the current value.</summary>
            public bool ImprovedOverCurrent(double currentValue) =>
                Math.Abs(Achieved - currentValue) >= 1e-3;
        }

        /// <summary>
        /// Runs the search for a single quantity (K-value OR curve length).
        /// <paramref name="tryApply"/> attempts to write the candidate value and
        /// returns <c>null</c> on success or the rejection message on failure.
        /// NOTE: successful probes mutate the drawing inside the ambient transaction.
        /// Callers that return <see cref="ToolResult.Fail"/> rely on
        /// <see cref="ToolExecutor"/> aborting the transaction (abort-on-fail) so the
        /// probe writes are rolled back and never committed.
        /// </summary>
        public static Outcome Run(
            double currentValue,
            double requestedValue,
            Func<double, string?> tryApply,
            int maxRetries = DefaultMaxRetries,
            double convergenceWindow = DefaultConvergenceWindow)
        {
            if (tryApply == null) throw new ArgumentNullException(nameof(tryApply));

            // Floor at a positive value: K and length must stay > 0, and the
            // inline loop this replaces clamped lo to max(current, 1.0).
            double hi = requestedValue;
            double lo = Math.Max(currentValue, 1.0);
            double tryValue = requestedValue;
            double achieved = currentValue;
            string? lastError = null;
            bool relaxed = false;
            int attempts;

            for (attempts = 1; attempts <= maxRetries; attempts++)
            {
                string? error = tryApply(tryValue);
                if (error == null)
                {
                    achieved = tryValue;
                    if (attempts == 1)
                    {
                        // Exact match — no further search.
                        return new Outcome
                        {
                            ExactApplied = true,
                            Achieved = achieved,
                            Relaxed = false,
                            Attempts = attempts,
                            LastError = null,
                        };
                    }

                    // Try to push higher (we landed below the ceiling).
                    lo = tryValue;
                    double next = 0.5 * (lo + hi);
                    if (next - lo < convergenceWindow) break; // converged
                    tryValue = next;
                    relaxed = true;
                }
                else
                {
                    lastError = error;
                    // Failed at this value. Search lower.
                    hi = tryValue;
                    double next = 0.5 * (lo + hi);
                    if (hi - lo < convergenceWindow) break; // converged
                    tryValue = next;
                    relaxed = true;
                }
            }

            return new Outcome
            {
                ExactApplied = false,
                Achieved = achieved,
                Relaxed = relaxed,
                Attempts = Math.Min(attempts, maxRetries),
                LastError = lastError,
            };
        }

        /// <summary>
        /// Builds the Hebrew user-facing error for an exact_only rejection.
        /// Format fixed by product decision (2026-06-12): no partial fixes in the
        /// fix flow — report the best achievable value as a diagnostic instead.
        /// Mirrors <see cref="RadiusAdjustmentSearch.BuildExactOnlyFailureMessage"/>.
        /// </summary>
        public static string BuildExactOnlyFailureMessage(double requestedValue, double achievableValue)
        {
            return $"הערך המבוקש {requestedValue:F1} אינו ישים גיאומטרית; " +
                   $"הערך המרבי הישים: {achievableValue:F1}";
        }
    }
}
