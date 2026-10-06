using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Services
{
    /// <summary>Outcome bucket for a single executed fix item.</summary>
    public enum FixOutcome
    {
        /// <summary>The drawing was mutated (status == "applied").</summary>
        Applied,

        /// <summary>Soft-skip: skipped / manual_required / "אינו ניתן לתיקון" — no drawing change.</summary>
        Manual,

        /// <summary>The fix ran and errored — no drawing change.</summary>
        Failed,
    }

    /// <summary>
    /// THE single source of truth for classifying a <see cref="FixResultItem"/> into one of the
    /// three outcome buckets. The per-item result table, the fix-summary banner, and the on-drawing
    /// pin colors all route through here so the numbers the engineer sees can never disagree
    /// (bug: three summaries reported different fixed/error/manual counts).
    ///
    /// Preference order mirrors the agent contract: the explicit per-item <c>status</c> wins;
    /// the boolean <see cref="FixResultItem.Success"/> and the legacy Hebrew description marker
    /// are fallbacks for older agents that predate <c>status</c>.
    /// </summary>
    public static class FixResultClassifier
    {
        public static FixOutcome Classify(FixResultItem item)
        {
            // Soft-skip first: an explicit skipped/manual_required status, or (older agents) a
            // success row whose description carries the "cannot be auto-fixed" marker. These
            // touched no geometry, so they are NOT successes.
            bool isSoftSkip =
                string.Equals(item.Status, "skipped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, "manual_required", StringComparison.OrdinalIgnoreCase)
                || (item.Success
                    && !string.IsNullOrEmpty(item.Description)
                    && item.Description.Contains("אינו ניתן לתיקון"));
            if (isSoftSkip)
                return FixOutcome.Manual;

            // "applied" is the authoritative mutated-the-drawing status; older agents omit it,
            // so fall back to the boolean success flag.
            bool applied = string.Equals(item.Status, "applied", StringComparison.OrdinalIgnoreCase)
                || (item.Status == null && item.Success);
            return applied ? FixOutcome.Applied : FixOutcome.Failed;
        }

        /// <summary>Total / applied / manual / failed counts over a result set (all derived from <see cref="Classify"/>).</summary>
        public static FixTally Tally(IEnumerable<FixResultItem>? items)
        {
            int applied = 0, manual = 0, failed = 0;
            if (items != null)
            {
                foreach (var it in items)
                {
                    switch (Classify(it))
                    {
                        case FixOutcome.Applied: applied++; break;
                        case FixOutcome.Manual: manual++; break;
                        default: failed++; break;
                    }
                }
            }
            return new FixTally(applied, manual, failed);
        }
    }

    /// <summary>Immutable count triple with a derived total.</summary>
    public readonly struct FixTally
    {
        public FixTally(int applied, int manual, int failed)
        {
            Applied = applied;
            Manual = manual;
            Failed = failed;
        }

        public int Applied { get; }
        public int Manual { get; }
        public int Failed { get; }
        public int Total => Applied + Manual + Failed;
    }
}
