using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Modification;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Modification
{
    /// <summary>
    /// Pure-logic tests for the binary-search relaxation used by
    /// modify_profile_vertical_curve (K-value and curve-length edits). The
    /// geometry write is injected, so these run without an AutoCAD runtime. The
    /// exact_only Stage 2 contract decides from
    /// <see cref="VerticalCurveAdjustmentSearch.Outcome.ExactApplied"/>:
    /// true → commit/success; false → Fail + achievable_* diagnostic (drawing
    /// untouched after the transaction abort).
    ///
    /// Semantics mirror <see cref="RadiusAdjustmentSearch"/> one-to-one; only the
    /// default convergence window (0.5 for the small K/length magnitudes) differs.
    /// </summary>
    public class VerticalCurveAdjustmentSearchTests
    {
        /// <summary>Builds a probe that accepts any value at or below the cap.</summary>
        private static Func<double, string?> AcceptUpTo(double cap, List<double>? probes = null)
            => value =>
            {
                probes?.Add(value);
                return value <= cap ? null : "Invalid Operation.";
            };

        [Fact]
        public void ExactValueAccepted_ReturnsExactApplied_NoRelaxation()
        {
            // Engineer asks K=40 from current K=20; geometry accepts up to 50.
            var probes = new List<double>();
            var outcome = VerticalCurveAdjustmentSearch.Run(20, 40, AcceptUpTo(50, probes));

            outcome.ExactApplied.Should().BeTrue();
            outcome.Achieved.Should().Be(40);
            outcome.Relaxed.Should().BeFalse();
            outcome.Attempts.Should().Be(1);
            outcome.LastError.Should().BeNull();
            probes.Should().Equal(40); // single probe — no extra writes
        }

        [Fact]
        public void AllValuesRejected_AchievedStaysAtCurrent_NoImprovement()
        {
            // Nothing above the current value fits → drawing must be left at K=20
            // (in exact_only the caller returns Fail and the abort rolls back).
            var outcome = VerticalCurveAdjustmentSearch.Run(20, 40, _ => "Invalid Operation.");

            outcome.ExactApplied.Should().BeFalse();
            outcome.Achieved.Should().Be(20);
            outcome.ImprovedOverCurrent(20).Should().BeFalse();
            outcome.LastError.Should().Be("Invalid Operation.");
        }

        [Fact]
        public void PartiallyAchievable_FindsValueBetweenCurrentAndCap()
        {
            // Geometry accepts up to K=33; requested 40 from current 20.
            var outcome = VerticalCurveAdjustmentSearch.Run(20, 40, AcceptUpTo(33));

            outcome.ExactApplied.Should().BeFalse();
            outcome.Relaxed.Should().BeTrue();
            outcome.Achieved.Should().BeGreaterThan(20);
            outcome.Achieved.Should().BeLessOrEqualTo(33);
            outcome.ImprovedOverCurrent(20).Should().BeTrue();
        }

        [Fact]
        public void PartiallyAchievable_ConvergesWithinWindowOfCap()
        {
            // Length edit: search between 0 and 128 with the 0.5 m window must
            // land just under the true cap of 70.
            var outcome = VerticalCurveAdjustmentSearch.Run(0, 128, AcceptUpTo(70));

            outcome.Achieved.Should().BeGreaterThan(60);
            outcome.Achieved.Should().BeLessOrEqualTo(70);
        }

        [Fact]
        public void ProbeCount_NeverExceedsMaxRetries()
        {
            var probes = new List<double>();
            VerticalCurveAdjustmentSearch.Run(20, 100000, AcceptUpTo(21, probes), maxRetries: 6);

            probes.Count.Should().BeLessOrEqualTo(6);
        }

        [Fact]
        public void AttemptsReported_NeverExceedsMaxRetries()
        {
            var outcome = VerticalCurveAdjustmentSearch.Run(20, 100000, AcceptUpTo(50), maxRetries: 6);
            outcome.Attempts.Should().BeLessOrEqualTo(6);
        }

        [Fact]
        public void ConvergenceWindow_StopsSearchEarly()
        {
            var probes = new List<double>();
            // Current 39.8, requested 40: window is 0.2 < 0.5 convergence —
            // first failed probe converges immediately.
            var outcome = VerticalCurveAdjustmentSearch.Run(39.8, 40, AcceptUpTo(0, probes));

            probes.Should().HaveCount(1);
            outcome.Achieved.Should().Be(39.8);
        }

        [Fact]
        public void CurrentEqualsRequested_ExactApplied()
        {
            // Requesting the value already present: the first probe is the
            // current value and the geometry accepts it unchanged.
            var probes = new List<double>();
            var outcome = VerticalCurveAdjustmentSearch.Run(35, 35, AcceptUpTo(100, probes));

            outcome.ExactApplied.Should().BeTrue();
            outcome.Achieved.Should().Be(35);
            outcome.Relaxed.Should().BeFalse();
            probes.Should().Equal(35);
        }

        [Fact]
        public void NullProbe_Throws()
        {
            var act = () => VerticalCurveAdjustmentSearch.Run(20, 40, null!);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ImprovedOverCurrent_UsesMillimeterTolerance()
        {
            var improved = new VerticalCurveAdjustmentSearch.Outcome { Achieved = 35.0005 };
            improved.ImprovedOverCurrent(35.0).Should().BeFalse();

            var reallyImproved = new VerticalCurveAdjustmentSearch.Outcome { Achieved = 35.5 };
            reallyImproved.ImprovedOverCurrent(35.0).Should().BeTrue();
        }

        [Fact]
        public void ExactOnlyFailureMessage_ContainsRequestedAndAchievable_Hebrew()
        {
            // K-value targets surface in radius units in the tool (R = K * 100);
            // the message helper formats whatever pair the caller passes.
            var msg = VerticalCurveAdjustmentSearch.BuildExactOnlyFailureMessage(4000, 3306.4);

            // Format fixed by product decision (2026-06-12).
            msg.Should().Be("הערך המבוקש 4000.0 אינו ישים גיאומטרית; הערך המרבי הישים: 3306.4");
        }

        [Fact]
        public void ExactOnlyDecision_FirstProbeRejected_ProbesStillDiscoverAchievable()
        {
            // Simulates the exact_only flow: exact K rejected, search still
            // reports the best achievable value for the achievable_k_value field.
            var outcome = VerticalCurveAdjustmentSearch.Run(20, 40, AcceptUpTo(30));

            outcome.ExactApplied.Should().BeFalse(); // → tool returns Fail (abort rolls probes back)
            outcome.Achieved.Should().BeInRange(20.0, 30.0);
            outcome.ImprovedOverCurrent(20).Should().BeTrue("achievable diagnostic must be meaningful");
        }

        [Fact]
        public void Floor_NeverDropsBelowOne_WhenCurrentIsZeroOrNegative()
        {
            // Defensive: a current value of 0 (no curve yet) must not let the
            // search probe at or below 0. The lo floor clamps to 1.0, so the
            // achievable value found stays positive.
            var outcome = VerticalCurveAdjustmentSearch.Run(0, 40, AcceptUpTo(5));

            outcome.Achieved.Should().BeGreaterThan(0);
            outcome.Achieved.Should().BeLessOrEqualTo(5);
        }
    }
}
