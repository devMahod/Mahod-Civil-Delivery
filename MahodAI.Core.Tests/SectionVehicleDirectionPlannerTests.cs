using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionVehicleDirectionPlannerTests
    {
        private const string ClHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Theory]
        [InlineData(-7.0)]
        [InlineData(7.0)]
        public void AlongArrow_AlwaysSelectsRear_IndependentOfOffsetSign(double laneOffset)
        {
            var plan = Resolve(
                laneOffset,
                new[] { Arrow(laneOffset, 2, 0, "A") },
                new[] { Manual(laneOffset, SectionVehicleDirectionPlanner.AgainstFlowToken) });

            plan.IsResolved.Should().BeTrue();
            plan.Flow.Should().Be(TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment);
            plan.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Rear);
            plan.DirectionSource.Should().Be(SectionVehicleDirectionPlanner.ArrowSource);
            plan.DirectionDigest.Should().MatchRegex("^[0-9a-f]{64}$");
            plan.ManualDecision.Should().BeNull("approved arrow evidence has priority");
        }

        [Theory]
        [InlineData(-7.0)]
        [InlineData(7.0)]
        public void AgainstArrow_AlwaysSelectsFront_IndependentOfOffsetSign(double laneOffset)
        {
            var plan = Resolve(
                laneOffset,
                new[] { Arrow(laneOffset, 2, Math.PI, "A") });

            plan.IsResolved.Should().BeTrue();
            plan.Flow.Should().Be(TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
            plan.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Front);
        }

        [Fact]
        public void EachLaneUsesItsNearestArrow_NotOneGlobalClDirection()
        {
            var arrows = new[]
            {
                Arrow(-6, 1, Math.PI, "LEFT"),
                Arrow(6, 1, 0, "RIGHT"),
            };

            Resolve(-6, arrows).OfficeCarView.Should()
                .Be(SectionFurnitureLogic.OfficeCarView.Front);
            Resolve(6, arrows).OfficeCarView.Should()
                .Be(SectionFurnitureLogic.OfficeCarView.Rear);
        }

        [Fact]
        public void MissingArrow_UsesOneExactApprovedManualDecision()
        {
            var decision = Manual(4.25, SectionVehicleDirectionPlanner.AgainstFlowToken);

            var plan = Resolve(4.25, Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>(),
                new[] { decision });

            plan.IsResolved.Should().BeTrue();
            plan.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Front);
            plan.DirectionSource.Should().Be(SectionVehicleDirectionPlanner.ManualSource);
            plan.DirectionDigest.Should().MatchRegex("^[0-9a-f]{64}$");
            plan.ManualDecision.Should().BeSameAs(decision);
        }

        [Fact]
        public void ConflictingNearestArrows_MayOnlyBeResolvedByExactManualApproval()
        {
            var arrows = new[]
            {
                Arrow(3, 0, 0, "A"),
                Arrow(3.5, 0, Math.PI, "B"),
            };

            var withoutDecision = Resolve(3, arrows);
            withoutDecision.State.Should().Be(
                TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous);
            withoutDecision.IsResolved.Should().BeFalse();

            var withDecision = Resolve(3, arrows,
                new[] { Manual(3, SectionVehicleDirectionPlanner.AlongFlowToken) });
            withDecision.IsResolved.Should().BeTrue();
            withDecision.DirectionSource.Should().Be(SectionVehicleDirectionPlanner.ManualSource);
            withDecision.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Rear);
        }

        [Fact]
        public void MissingEvidenceAndWrongLaneDecision_ReturnUnknown()
        {
            var plan = Resolve(4.25,
                Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>(),
                new[] { Manual(5.00, SectionVehicleDirectionPlanner.AlongFlowToken) });

            plan.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Unknown);
            plan.IsResolved.Should().BeFalse();
            plan.DirectionDigest.Should().BeNull();
            plan.Reason.Should().Contain("no-valid-manual-decision");
        }

        [Fact]
        public void DuplicateExactManualApprovals_AreAmbiguousEvenWhenTheyAgree()
        {
            var decisions = new[]
            {
                Manual(4.25, SectionVehicleDirectionPlanner.AlongFlowToken),
                Manual(4.252, SectionVehicleDirectionPlanner.AlongFlowToken),
            };

            var plan = Resolve(4.25,
                Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>(), decisions);

            plan.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous);
            plan.Reason.Should().Be("duplicate-manual-direction-decisions");
            plan.IsResolved.Should().BeFalse();
        }

        [Theory]
        [InlineData(null, "A1", "ROAD", 4.25, "along-alignment", "Natalie")]
        [InlineData("short", "A1", "ROAD", 4.25, "along-alignment", "Natalie")]
        [InlineData(ClHash, "not-handle", "ROAD", 4.25, "along-alignment", "Natalie")]
        [InlineData(ClHash, "A1", "ROAD", 4.25, "unknown", "Natalie")]
        [InlineData(ClHash, "A1", "ROAD", 4.25, "along-alignment", "")]
        public void PartialManualDecision_IsNeverValid(
            string? hash,
            string handle,
            string alignment,
            double lane,
            string flow,
            string approver)
        {
            var decision = new ProjectProfile.SectionsProfile.DecisionsProfile
                .TrafficDirectionDecision
            {
                SourceDrawingHash = hash,
                SourceHandle = handle,
                AlignmentName = alignment,
                LaneMidOffsetM = lane,
                Flow = flow,
                ApprovedBy = approver,
                ApprovedAtUtc = DateTime.Parse("2026-09-01T06:00:00Z").ToUniversalTime(),
            };

            SectionVehicleDirectionPlanner.IsValidManualDecision(decision).Should().BeFalse();
        }

        [Fact]
        public void DirectionDigest_IsStableButChangesWithEvidence()
        {
            var first = Resolve(4.25, new[] { Arrow(4.25, 1, 0, "A") });
            var repeated = Resolve(4.25, new[] { Arrow(4.25, 1, 0, "A") });
            var changed = Resolve(4.25, new[] { Arrow(4.25, 1, Math.PI, "A") });

            repeated.DirectionDigest.Should().Be(first.DirectionDigest);
            changed.DirectionDigest.Should().NotBe(first.DirectionDigest);
        }

        [Fact]
        public void BicycleMode_UsesBikeEvidenceWithoutLettingItOrientRoadTraffic()
        {
            var bikeArrow = new TrafficDirectionEvidenceLogic.ArrowEvidence(
                4.25, 1, Math.PI, "HA-BIKE", "BL-ARW-W-Y", "SM.dwg", "BIKE");

            var motor = Resolve(4.25, new[] { bikeArrow });
            var bicycle = SectionVehicleDirectionPlanner.Resolve(
                ClHash,
                "A1",
                "ROAD",
                4.25,
                4.25,
                0,
                0,
                new[] { bikeArrow },
                manualDecisions: null,
                evidenceMode: SectionVehicleDirectionPlanner.ArrowEvidenceMode.Bicycle);

            motor.IsResolved.Should().BeFalse();
            bicycle.IsResolved.Should().BeTrue();
            bicycle.EvidenceMode.Should().Be(
                SectionVehicleDirectionPlanner.ArrowEvidenceMode.Bicycle);
            bicycle.Flow.Should().Be(
                TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
            bicycle.DirectionDigest.Should().MatchRegex("^[0-9a-f]{64}$");
        }

        [Theory]
        [InlineData("along-alignment", "motor", SectionFurnitureLogic.OfficeCarView.Rear)]
        [InlineData("against-alignment", "bicycle", SectionFurnitureLogic.OfficeCarView.Front)]
        public void RestoreResolved_ReDerivesCivilViewAndKeepsEvidenceMode(
            string flow, string mode, SectionFurnitureLogic.OfficeCarView expectedView)
        {
            SectionVehicleDirectionPlanner.TryRestoreResolved(
                    flow, mode, "arrow", new string('d', 64), "planned",
                    out var restored)
                .Should().BeTrue();

            restored!.IsResolved.Should().BeTrue();
            restored.OfficeCarView.Should().Be(expectedView);
            SectionVehicleDirectionPlanner.EvidenceModeToken(restored.EvidenceMode)
                .Should().Be(mode);
        }

        [Theory]
        [InlineData("unknown", "motor", "arrow")]
        [InlineData("along-alignment", "legacy", "arrow")]
        [InlineData("along-alignment", "motor", "signed-offset")]
        public void RestoreResolved_FailsClosedForMalformedPlanEvidence(
            string flow, string mode, string source)
        {
            SectionVehicleDirectionPlanner.TryRestoreResolved(
                    flow, mode, source, new string('e', 64), "planned", out _)
                .Should().BeFalse();
        }

        private static SectionVehicleDirectionPlanner.DirectionPlan Resolve(
            double laneOffset,
            IEnumerable<TrafficDirectionEvidenceLogic.ArrowEvidence> arrows,
            IEnumerable<ProjectProfile.SectionsProfile.DecisionsProfile
                .TrafficDirectionDecision>? decisions = null) =>
            SectionVehicleDirectionPlanner.Resolve(
                ClHash,
                "A1",
                "ROAD",
                laneOffset,
                laneOffset,
                0,
                0,
                arrows,
                decisions);

        private static TrafficDirectionEvidenceLogic.ArrowEvidence Arrow(
            double x, double y, double heading, string handle) =>
            new(x, y, heading, "BL-TR-ARRW", "TR-ARW", "SM.dwg", handle);

        private static ProjectProfile.SectionsProfile.DecisionsProfile
            .TrafficDirectionDecision Manual(double lane, string flow) => new()
            {
                SourceDrawingHash = ClHash,
                SourceHandle = "A1",
                AlignmentName = "ROAD",
                LaneMidOffsetM = lane,
                Flow = flow,
                ApprovedBy = "Natalie",
                ApprovedAtUtc = DateTime.Parse("2026-09-01T06:00:00Z").ToUniversalTime(),
            };
    }
}
