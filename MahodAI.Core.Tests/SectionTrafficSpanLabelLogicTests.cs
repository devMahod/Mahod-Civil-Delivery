using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionTrafficSpanLabelLogicTests
{
    private static SectionTrafficSpanLabelLogic.Evidence Motor(double offset, string id) =>
        new(offset, TrafficDirectionEvidenceLogic.SourceClass.ApprovedTrafficArrow, id);

    private static SectionTrafficSpanLabelLogic.Evidence Bike(double offset, string id) =>
        new(offset, TrafficDirectionEvidenceLogic.SourceClass.ExcludedBikeArrow, id);

    [Fact]
    public void OrdinaryEnvelope_WithAuditedMotorArrow_IsOneTravelLane()
    {
        var result = SectionTrafficSpanLabelLogic.Resolve(3.4, new[] { Motor(1.7, "A") });

        result.Label.Should().Be("נתיב נסיעה");
        result.IsResolved.Should().BeTrue();
    }

    [Fact]
    public void WideEnvelope_RequiresTwoLaterallyDistinctMotorTracks()
    {
        SectionTrafficSpanLabelLogic.Resolve(6.93,
                new[] { Motor(-1.7, "A"), Motor(1.7, "B") })
            .Label.Should().Be("נתיבי נסיעה");

        SectionTrafficSpanLabelLogic.Resolve(6.93,
                new[] { Motor(1.70, "A"), Motor(1.75, "B") })
            .IsResolved.Should().BeFalse(
                "repeated arrows along one lane are not two lane tracks");
    }

    [Fact]
    public void WideEnvelope_DoesNotInventInternalLaneBoundaries()
    {
        var result = SectionTrafficSpanLabelLogic.Resolve(6.93,
            new[] { Motor(-1.7, "A"), Motor(1.7, "B") });

        result.MotorTrackCount.Should().Be(2);
        result.Reason.Should().Be("two-distinct-motor-arrow-tracks");
    }

    [Fact]
    public void MixedBikeAndMotorEvidence_RemainsUnresolved()
    {
        SectionTrafficSpanLabelLogic.Resolve(3.0,
                new[] { Motor(-0.5, "A"), Bike(0.5, "B") })
            .IsResolved.Should().BeFalse();
    }

    [Fact]
    public void GenericOrUnknownArrowClass_IsNeverAccepted()
    {
        var unknown = new SectionTrafficSpanLabelLogic.Evidence(
            0, TrafficDirectionEvidenceLogic.SourceClass.Unapproved, "A");

        SectionTrafficSpanLabelLogic.Resolve(3.0, new[] { unknown })
            .IsResolved.Should().BeFalse();
    }
}
