using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionTrafficTrackDecisionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TrackHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision Decision(string? track = TrackHash) => new()
    {
        SourceDrawingHash = Hash, SourceHandle = "7C8F", AlignmentName = "600",
        LaneMidOffsetM = 2, FromOffsetM = -5, ToOffsetM = 5, EvidenceMode = "motor",
        TrackEvidenceDigest = track, AllowArrowOverride = true,
        Flow = SectionVehicleDirectionPlanner.AgainstFlowToken, ApprovedBy = "Explicit test approver",
        ApprovedAtUtc = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc)
    };
    private static SectionVehicleDirectionPlanner.DirectionPlan Resolve(
        ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision[] decisions,
        string? track = TrackHash, bool arrows = true, double from = -5, double offset = 2) =>
        SectionVehicleDirectionPlanner.Resolve(Hash, "7C8F", "600", offset, offset, 0, Math.PI / 2,
            arrows ? new[] { new TrafficDirectionEvidenceLogic.ArrowEvidence(offset, 1, Math.PI / 2,
                "BL-TR-ARRW", "TR-ARW", "SM", "BD91EF/A1") } : Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>(),
            decisions, laneFromOffsetM: from, laneToOffsetM: 5, trackEvidenceDigest: track, laneCutFrame: Frame());

    private static SectionCutFrame Frame()
    {
        SectionCutFrame.TryCreate(new(-10, 0), new(10, 0), new(0, 0), 90, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public void ExactTrackEditAllowsSourceOffsetWithoutInventingBounds_AndRestoresExactDirection()
    {
        var decision = Decision();
        SectionVehicleDirectionPlanner.IsValidManualDecision(decision).Should().BeTrue();
        var result = Resolve(new[] { decision });
        result.IsResolved.Should().BeTrue(); result.DirectionSource.Should().Be("manual");
        result.OfficeCarView.Should().Be(SectionFurnitureLogic.OfficeCarView.Front);
        SectionVehicleDirectionPlanner.TryRestoreResolved(SectionVehicleDirectionPlanner.AgainstFlowToken,
            "motor", result.DirectionSource, result.DirectionDigest, result.Reason, out var restored).Should().BeTrue();
        restored!.DirectionDigest.Should().Be(result.DirectionDigest);
        decision.FromOffsetM.Should().Be(-5); decision.ToOffsetM.Should().Be(5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    public void LegacyOrOtherTrackAuthorityNeverTransfersEvenAtSameOffset(string? otherTrack)
    {
        var decision = Decision(otherTrack);
        if (otherTrack == null) { decision.FromOffsetM = 0; decision.ToOffsetM = 4; }
        Resolve(new[] { decision }).DirectionSource.Should().Be("arrow");
        Resolve(new[] { decision }, arrows: false).IsResolved.Should().BeFalse();
    }

    [Fact]
    public void BoundsOrSourceTrackEvidenceChangeInvalidatesAuthority_AndChangesAutomaticDigest()
    {
        Resolve(new[] { Decision() }, from: -4.999999).DirectionSource.Should().Be("arrow");
        Resolve(Array.Empty<ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision>())
            .DirectionDigest.Should().NotBe(Resolve(Array.Empty<ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision>(),
                new string('c', 64)).DirectionDigest);
    }

    [Fact]
    public void InvalidTrackAuthorityOrDuplicateEditsRefuse_AndLegacyMidpointContractIsUnchanged()
    {
        var invalid = Decision(); invalid.AllowArrowOverride = false;
        SectionVehicleDirectionPlanner.IsValidManualDecision(invalid).Should().BeFalse();
        invalid = Decision(); invalid.LaneMidOffsetM = 5;
        SectionVehicleDirectionPlanner.IsValidManualDecision(invalid).Should().BeFalse();
        SectionVehicleDirectionPlanner.IsValidManualDecision(Decision(null)).Should().BeFalse("a legacy edit must still be the midpoint");
        var legacy = Decision(null); legacy.FromOffsetM = 0; legacy.ToOffsetM = 4;
        SectionVehicleDirectionPlanner.IsValidManualDecision(legacy).Should().BeTrue();
        Resolve(new[] { Decision(), Decision() }).IsResolved.Should().BeFalse();
        Resolve(new[] { Decision() }, track: "invalid").IsResolved.Should().BeFalse();
    }
}
