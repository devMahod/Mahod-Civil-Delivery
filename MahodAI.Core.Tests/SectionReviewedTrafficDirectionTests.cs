using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionReviewedTrafficDirectionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision Decision(bool explicitEdit = true) => new()
    {
        SourceDrawingHash = Hash, SourceHandle = "7CA3", AlignmentName = "600",
        LaneMidOffsetM = 2, Flow = SectionVehicleDirectionPlanner.AgainstFlowToken,
        ApprovedBy = "Arthur", ApprovedAtUtc = new DateTime(2026, 9, 9, 17, 0, 0, DateTimeKind.Utc),
        AllowArrowOverride = explicitEdit, FromOffsetM = 0, ToOffsetM = 4, EvidenceMode = "motor",
    };
    private static SectionVehicleDirectionPlanner.DirectionPlan Resolve(
        ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision[] decisions,
        double from = 0, double to = 4, string handle = "7CA3", double arrowHeading = 0) =>
        SectionVehicleDirectionPlanner.Resolve(Hash, handle, "600", 2, 0, -2, 0,
            new[] { new TrafficDirectionEvidenceLogic.ArrowEvidence(1, -2, arrowHeading, "BL-TR-ARRW", "TR-ARW", "SM.dwg", "A") },
            decisions, evidenceMode: SectionVehicleDirectionPlanner.ArrowEvidenceMode.MotorTraffic,
            laneFromOffsetM: from, laneToOffsetM: to, laneCutFrame: Frame());

    private static SectionCutFrame Frame()
    {
        SectionCutFrame.TryCreate(new(0, 10), new(0, -10), new(0, 0), 0, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public void ExplicitCurrentLaneEditWins_OriginalArrowRemainsEvidence_AndApplyRestoresExactFlow()
    {
        var original = Resolve(Array.Empty<ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision>());
        var edited = Resolve(new[] { Decision() });
        original.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Rear);
        edited.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Front);
        edited.ArrowResolution.IsResolved.Should().BeTrue();
        edited.DirectionDigest.Should().NotBe(original.DirectionDigest);
        Resolve(new[] { Decision() }, arrowHeading: Math.PI).DirectionDigest.Should().NotBe(edited.DirectionDigest);
        SectionVehicleDirectionPlanner.TryRestoreResolved(SectionVehicleDirectionPlanner.AgainstFlowToken,
            "motor", edited.DirectionSource, edited.DirectionDigest, edited.Reason, out var replay).Should().BeTrue();
        replay!.OfficeCarView.Should().Be(edited.OfficeCarView);
        replay.DirectionDigest.Should().Be(edited.DirectionDigest);
    }

    [Fact]
    public void LegacyManualDecisionDoesNotAcquireArrowOverridePriority()
    {
        Resolve(new[] { Decision(false) }).DirectionSource.Should().Be("arrow");
    }

    [Theory]
    [InlineData(-0.000001, 4, "7CA3")]
    [InlineData(0, 4.000001, "7CA3")]
    [InlineData(0, 4, "BEEF")]
    public void ChangedBoundsOrSourceNeverReuseExplicitEdit(double from, double to, string handle)
    {
        Resolve(new[] { Decision() }, from, to, handle).DirectionSource.Should().Be("arrow");
    }

    [Fact]
    public void WrongEvidenceModeDoesNotOverrideAndDuplicatesRemainAmbiguous()
    {
        var wrong = Decision(); wrong.EvidenceMode = "bicycle";
        Resolve(new[] { wrong }).DirectionSource.Should().Be("arrow");
        Resolve(new[] { Decision(), Decision() }).IsResolved.Should().BeFalse();
        var invalid = Decision(); invalid.ToOffsetM = double.NaN;
        SectionVehicleDirectionPlanner.IsValidManualDecision(invalid).Should().BeFalse();
    }
}
