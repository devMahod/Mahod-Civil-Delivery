using System;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Pure binary-search relaxation logic used by <see cref="ModifyAlignmentCurveRadiusTool"/>.
    /// Civil 3D throws "Invalid Operation." (eInvalidInput) when a requested radius
    /// would force a negative tangent length or otherwise invalidate the adjacent
    /// geometry. This helper searches between the requested value (ceiling) and the
    /// current value (floor) for the largest value the geometry accepts.
    ///
    /// Extracted from the tool so the decision logic is unit-testable without an
    /// AutoCAD runtime — the geometry write is injected as <c>tryApply</c>.
    /// </summary>
    public static class RadiusAdjustmentSearch
    {
        /// <summary>Default maximum number of probe attempts.</summary>
        public const int DefaultMaxRetries = 6;

        /// <summary>Default convergence window in meters.</summary>
        public const double DefaultConvergenceMeters = 5.0;

        /// <summary>Result of a relaxation search.</summary>
        public sealed class Outcome
        {
            /// <summary>True when the FIRST attempt (the exact requested value) was accepted.</summary>
            public bool ExactApplied { get; init; }

            /// <summary>
            /// The largest value the geometry accepted. Equals the current radius
            /// when no value above it was accepted (every probe was rejected).
            /// </summary>
            public double Achieved { get; init; }

            /// <summary>True when the search had to relax below the requested value.</summary>
            public bool Relaxed { get; init; }

            /// <summary>Number of probe attempts performed.</summary>
            public int Attempts { get; init; }

            /// <summary>Last rejection message from the geometry engine (null if never rejected).</summary>
            public string? LastError { get; init; }

            /// <summary>True when at least one probe landed above the current radius.</summary>
            public bool ImprovedOverCurrent(double currentRadius) =>
                Math.Abs(Achieved - currentRadius) >= 1e-3;
        }

        /// <summary>
        /// Runs the search. <paramref name="tryApply"/> attempts to write the candidate
        /// radius and returns <c>null</c> on success or the rejection message on failure.
        /// NOTE: successful probes mutate the drawing inside the ambient transaction.
        /// Callers that return <see cref="ToolResult.Fail"/> rely on
        /// <see cref="ToolExecutor"/> aborting the transaction (abort-on-fail) so the
        /// probe writes are rolled back and never committed.
        /// </summary>
        public static Outcome Run(
            double currentRadius,
            double requestedRadius,
            Func<double, string?> tryApply,
            int maxRetries = DefaultMaxRetries,
            double convergenceMeters = DefaultConvergenceMeters)
        {
            if (tryApply == null) throw new ArgumentNullException(nameof(tryApply));

            double hi = requestedRadius;
            double lo = currentRadius;
            double tryValue = requestedRadius;
            double achieved = currentRadius;
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
                    if (next - lo < convergenceMeters) break; // converged
                    tryValue = next;
                    relaxed = true;
                }
                else
                {
                    lastError = error;
                    // Failed at this value. Search lower.
                    hi = tryValue;
                    double next = 0.5 * (lo + hi);
                    if (hi - lo < convergenceMeters) break; // converged
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
        /// </summary>
        public static string BuildExactOnlyFailureMessage(double requestedRadius, double achievableRadius)
        {
            return $"הערך המבוקש {requestedRadius:F1} אינו ישים גיאומטרית; " +
                   $"הערך המרבי הישים: {achievableRadius:F1}";
        }
    }
}
