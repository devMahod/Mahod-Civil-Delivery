namespace MahodAI.Civil3D.Plugin.Models
{
    /// <summary>
    /// Lifecycle/fix state of a pinned finding. Drives the pin color and popup header.
    /// ALL analysis pins stay on the drawing through the whole fix flow — the state only
    /// changes their color, never their presence:
    /// <list type="bullet">
    /// <item><see cref="None"/> — a fresh analysis finding, or one the current fix plan
    ///   cannot address (severity color: orange/red/blue).</item>
    /// <item><see cref="Fixable"/> — matched to an item in the current fix plan and awaiting
    ///   execution (yellow).</item>
    /// <item><see cref="Fixed"/> — the matched fix was applied to the drawing (green).</item>
    /// <item><see cref="Failed"/> — the matched fix ran but errored (red).</item>
    /// <item><see cref="ManualRequired"/> — the matched fix was skipped / needs manual work,
    ///   no drawing change (yellow).</item>
    /// <item><see cref="VerifyFailed"/> — the fix ran but the post-fix read-back shows the
    ///   wrong value (applied, but not actually correct) — red.</item>
    /// </list>
    /// </summary>
    public enum MarkerFixState
    {
        None,
        Fixable,
        Fixed,
        Failed,
        ManualRequired,
        VerifyFailed,
    }

    /// <summary>
    /// A single analysis finding to be pinned on the drawing overlay. Carries the
    /// parsed location (alignment + station range), the human-readable problem text,
    /// and — once resolved against the live drawing — the world coordinate where the
    /// pin should sit.
    /// </summary>
    public class ProblemMarker
    {
        /// <summary>1-based index used for the pin number and the popup header.</summary>
        public int Index { get; set; }

        public string Alignment { get; set; } = string.Empty;
        public double StationStart { get; set; }
        public double? StationEnd { get; set; }

        /// <summary>Full location text as shown in the report (for the popup subtitle).</summary>
        public string LocationText { get; set; } = string.Empty;

        public string Problem { get; set; } = string.Empty;
        public string ActualValue { get; set; } = string.Empty;
        public string Required { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

        /// <summary>"critical" | "important" | "minor" — drives the pin/card color.</summary>
        public string Severity { get; set; } = "important";

        // --- Resolved against the live drawing (world coordinates). ---
        public bool HasWorld { get; set; }
        public double WorldX { get; set; }
        public double WorldY { get; set; }

        // --- Fix-lifecycle state (set while a fix plan/result is active). ---

        /// <summary>Current fix state; drives pin color and popup header.</summary>
        public MarkerFixState FixState { get; set; } = MarkerFixState.None;

        /// <summary>
        /// Id of the <c>FixPlanItem</c> this marker was matched to when the fix plan
        /// arrived (null when the finding has no auto-fix). Lets the fix-result step
        /// look the marker back up by the result item's <c>ItemId</c>.
        /// </summary>
        public string? FixItemId { get; set; }

        /// <summary>Midpoint station used to place the pin.</summary>
        public double MidStation => StationEnd.HasValue
            ? (StationStart + StationEnd.Value) * 0.5
            : StationStart;
    }
}
