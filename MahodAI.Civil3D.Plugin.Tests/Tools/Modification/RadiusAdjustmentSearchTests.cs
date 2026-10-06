using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Modification;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Modification
{
    /// <summary>
    /// Pure-logic tests for the binary-search relaxation used by
    /// modify_alignment_curve_radius. The geometry write is injected, so these
    /// run without an AutoCAD runtime. The exact_only Stage 2 contract decides
    /// from <see cref="RadiusAdjustmentSearch.Outcome.ExactApplied"/>:
    /// true → success; false → Fail + achievable_radius diagnostic.
    /// </summary>
    public class RadiusAdjustmentSearchTests
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
            var probes = new List<double>();
            var outcome = RadiusAdjustmentSearch.Run(500, 900, AcceptUpTo(1000, probes));

            outcome.ExactApplied.Should().BeTrue();
            outcome.Achieved.Should().Be(900);
            outcome.Relaxed.Should().BeFalse();
            outcome.Attempts.Should().Be(1);
            outcome.LastError.Should().BeNull();
            probes.Should().Equal(900); // single probe — no extra writes
        }

        [Fact]
        public void AllValuesRejected_AchievedStaysAtCurrent_NoImprovement()
        {
            var outcome = RadiusAdjustmentSearch.Run(500, 900, _ => "Invalid Operation.");

            outcome.ExactApplied.Should().BeFalse();
            outcome.Achieved.Should().Be(500);
            outcome.ImprovedOverCurrent(500).Should().BeFalse();
            outcome.LastError.Should().Be("Invalid Operation.");
        }

        [Fact]
        public void PartiallyAchievable_FindsValueBetweenCurrentAndCap()
        {
            // Geometry accepts up to 750; requested 900 from current 500.
            var outcome = RadiusAdjustmentSearch.Run(500, 900, AcceptUpTo(750));

            outcome.ExactApplied.Should().BeFalse();
            outcome.Relaxed.Should().BeTrue();
            outcome.Achieved.Should().BeGreaterThan(500);
            outcome.Achieved.Should().BeLessOrEqualTo(750);
            outcome.ImprovedOverCurrent(500).Should().BeTrue();
        }

        [Fact]
        public void PartiallyAchievable_ConvergesWithinWindowOfCap()
        {
            var outcome = RadiusAdjustmentSearch.Run(0, 1024, AcceptUpTo(700));

            // Binary search between 0 and 1024 with a 5 m convergence window
            // must land within ~window+midpoint-step of the true cap.
            outcome.Achieved.Should().BeGreaterThan(600);
            outcome.Achieved.Should().BeLessOrEqualTo(700);
        }

        [Fact]
        public void ProbeCount_NeverExceedsMaxRetries()
        {
            var probes = new List<double>();
            RadiusAdjustmentSearch.Run(500, 100000, AcceptUpTo(501, probes), maxRetries: 6);

            probes.Count.Should().BeLessOrEqualTo(6);
        }

        [Fact]
        public void AttemptsReported_NeverExceedsMaxRetries()
        {
            var outcome = RadiusAdjustmentSearch.Run(500, 100000, AcceptUpTo(750), maxRetries: 6);
            outcome.Attempts.Should().BeLessOrEqualTo(6);
        }

        [Fact]
        public void ConvergenceWindow_StopsSearchEarly()
        {
            var probes = new List<double>();
            // Current 896, requested 900: window is 4 m < 5 m convergence —
            // first failed probe converges immediately.
            var outcome = RadiusAdjustmentSearch.Run(896, 900, AcceptUpTo(0, probes));

            probes.Should().HaveCount(1);
            outcome.Achieved.Should().Be(896);
        }

        [Fact]
        public void NullProbe_Throws()
        {
            var act = () => RadiusAdjustmentSearch.Run(500, 900, null!);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ImprovedOverCurrent_UsesMillimeterTolerance()
        {
            var improved = new RadiusAdjustmentSearch.Outcome { Achieved = 500.0005 };
            improved.ImprovedOverCurrent(500.0).Should().BeFalse();

            var reallyImproved = new RadiusAdjustmentSearch.Outcome { Achieved = 500.5 };
            reallyImproved.ImprovedOverCurrent(500.0).Should().BeTrue();
        }

        [Fact]
        public void ExactOnlyFailureMessage_ContainsRequestedAndAchievable_Hebrew()
        {
            var msg = RadiusAdjustmentSearch.BuildExactOnlyFailureMessage(900, 750.26);

            // Format fixed by product decision (2026-06-12).
            msg.Should().Be("הערך המבוקש 900.0 אינו ישים גיאומטרית; הערך המרבי הישים: 750.3");
        }

        [Fact]
        public void ExactOnlyDecision_FirstProbeRejected_ProbesStillDiscoverAchievable()
        {
            // Simulates the exact_only flow: exact value rejected, search still
            // reports the best achievable value for the diagnostic field.
            var outcome = RadiusAdjustmentSearch.Run(500, 900, AcceptUpTo(700));

            outcome.ExactApplied.Should().BeFalse(); // → tool returns Fail (abort rolls probes back)
            outcome.Achieved.Should().BeInRange(500.0, 700.0);
            outcome.ImprovedOverCurrent(500).Should().BeTrue("achievable_radius diagnostic must be meaningful");
        }
    }
}
