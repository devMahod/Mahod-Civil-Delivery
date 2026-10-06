using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Creation
{
    /// <summary>
    /// Pure-logic tests for the PVI pre-validation/clamping used by create_profile's
    /// strict success contract (added PVI count must equal requested count).
    /// </summary>
    public class ProfilePviPlannerTests
    {
        [Fact]
        public void Plan_AllPointsInRange_AllValidNoErrors()
        {
            var points = new List<(double, double)> { (0, 100), (250, 105), (480, 102) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500);

            plan.Valid.Should().HaveCount(3);
            plan.Errors.Should().BeEmpty();
            plan.ClampedCount.Should().Be(0);
        }

        [Fact]
        public void Plan_StationAtExactEnd_ClampedNotRejected()
        {
            var points = new List<(double, double)> { (0, 100), (500, 104) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500, endClamp: 0.1);

            plan.Valid.Should().HaveCount(2);
            plan.Errors.Should().BeEmpty();
            plan.ClampedCount.Should().Be(1);
            plan.Valid[1].Station.Should().BeApproximately(499.9, 1e-9);
            plan.Valid[1].Elevation.Should().Be(104);
        }

        [Fact]
        public void Plan_StationBeforeStart_RejectedWithHebrewError()
        {
            var points = new List<(double, double)> { (-50, 100), (200, 105) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500);

            plan.Valid.Should().HaveCount(1);
            plan.Errors.Should().HaveCount(1);
            plan.Errors[0].Should().Contain("לפני תחילת הציר");
        }

        [Fact]
        public void Plan_StationPastEnd_RejectedWithHebrewError()
        {
            var points = new List<(double, double)> { (0, 100), (600, 105) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500);

            plan.Valid.Should().HaveCount(1);
            plan.Errors.Should().HaveCount(1);
            plan.Errors[0].Should().Contain("אחרי סוף הציר");
        }

        [Fact]
        public void Plan_DuplicateStations_SecondRejected()
        {
            var points = new List<(double, double)> { (0, 100), (250, 105), (250.005, 106) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500);

            plan.Valid.Should().HaveCount(2);
            plan.Errors.Should().HaveCount(1);
            plan.Errors[0].Should().Contain("כפולה");
        }

        [Fact]
        public void Plan_TwoEndPointsBothClamped_BecomeDuplicates()
        {
            // Both points clamp onto endStation-0.1 → the second becomes a duplicate.
            var points = new List<(double, double)> { (499.95, 100), (500, 101) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500, endClamp: 0.1);

            plan.Valid.Should().HaveCount(1);
            plan.Errors.Should().HaveCount(1);
        }

        [Fact]
        public void Plan_NaNValues_Rejected()
        {
            var points = new List<(double, double)> { (double.NaN, 100), (200, 105) };

            var plan = ProfilePviPlanner.Plan(points, 0, 500);

            plan.Valid.Should().HaveCount(1);
            plan.Errors.Should().HaveCount(1);
        }

        [Fact]
        public void Plan_EmptyInput_EmptyResult()
        {
            var plan = ProfilePviPlanner.Plan(new List<(double, double)>(), 0, 500);

            plan.Valid.Should().BeEmpty();
            plan.Errors.Should().BeEmpty();
        }
    }
}
