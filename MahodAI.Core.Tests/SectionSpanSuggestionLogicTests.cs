using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionSpanSuggestionLogicTests
{
    private static SectionSpanSuggestionLogic.Observation Span(
        string record, double station, double from, double to,
        bool resolved = false, string? label = null,
        string? source = null, string? digest = null,
        string alignment = "2000", string left = "curb", string right = "curb") =>
        new(record, alignment, station, from, to, left, right,
            resolved, label, source, digest);

    [Fact]
    public void TwoIndependentSourceBackedPeers_ProduceHighReviewSuggestion()
    {
        var suggestions = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, 3.0, 6.0, true, "נתיב נסיעה", "source-mark", new string('A', 64)),
            Span("B", 120, 3.1, 6.2, true, "נתיב נסיעה", "traffic-arrow", new string('B', 64)),
            Span("C", 140, 3.0, 6.1),
        });

        suggestions.Should().ContainSingle();
        var suggestion = suggestions.Single();
        suggestion.Label.Should().Be("נתיב נסיעה");
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.High);
        suggestion.IsStrongReviewCandidate.Should().BeTrue();
        suggestion.SupportingRecordCount.Should().Be(2);
        suggestion.EvidenceSources.Should().BeEquivalentTo("source-mark", "traffic-arrow");
    }

    [Fact]
    public void TwoSourceHatchPeers_HaveSourceMarkStrengthButRemainReviewOnly()
    {
        var target = Span("C", 140, 3.0, 6.1);
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, 3.0, 6.0, true, "שביל אופניים", "source-hatch-region", new string('A', 64)),
            Span("B", 120, 3.1, 6.2, true, "שביל אופניים", "source-hatch-region", new string('B', 64)),
            target,
        }).Single();

        suggestion.Label.Should().Be("שביל אופניים");
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.High);
        suggestion.IsStrongReviewCandidate.Should().BeTrue();
        suggestion.SupportingRecordCount.Should().Be(2);
        suggestion.EvidenceSources.Should().BeEquivalentTo("source-hatch-region");
        target.IsResolved.Should().BeFalse("source evidence only seeds a review suggestion, not an approval");
        target.Label.Should().BeNull();
    }

    [Fact]
    public void ConflictingHatchAndSourceMarkEvidence_RemainsUnapproved()
    {
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, 3, 6, true, "שביל אופניים", "source-hatch-region", new string('A', 64)),
            Span("B", 120, 3, 6, true, "מדרכה", "source-mark", new string('B', 64)),
            Span("C", 140, 3, 6),
        }).Single();

        suggestion.Label.Should().BeNull();
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.Conflict);
        suggestion.IsStrongReviewCandidate.Should().BeFalse();
    }

    [Fact]
    public void ConflictingHomologousEvidence_ProducesNoLabelAndNamesTheConflict()
    {
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, -6.0, -3.0, true, "חניה", "source-mark", "A"),
            Span("B", 120, -6.1, -3.0, true, "נת\"צ", "traffic-arrow", "B"),
            Span("C", 140, -6.0, -3.1),
        }).Single();

        suggestion.Label.Should().BeNull();
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.Conflict);
        suggestion.ConflictingLabelCount.Should().Be(2);
        suggestion.IsStrongReviewCandidate.Should().BeFalse();
    }

    [Fact]
    public void UnknownOrOtherAlignmentEvidence_IsNeverReused()
    {
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, 3, 6, true, "חניה", "guessed", "A"),
            Span("B", 120, 3, 6, true, "חניה", "source-mark", "B", alignment: "3000"),
            Span("C", 140, 3, 6),
        }).Single();

        suggestion.Label.Should().BeNull();
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.None);
        suggestion.SupportingRecordCount.Should().Be(0);
    }

    [Fact]
    public void DifferentLateralRankOrWidth_DoesNotLeakIntoTarget()
    {
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            // In record A this is the second strip from the axis.
            Span("A", 100, 3, 6),
            Span("A", 100, 7, 10, true, "חניה", "source-mark", "A"),
            // Target is first from the axis and much wider than the resolved peer.
            Span("B", 120, 3, 7),
        }).Single(item => item.RecordKey == "B");

        suggestion.Label.Should().BeNull();
        suggestion.HomologousSpanCount.Should().Be(1);
    }

    [Fact]
    public void OneReviewedManualPeer_IsLowConfidenceAndNeverPreselected()
    {
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, -6, -3, true, "חניה", "manual-profile", "A"),
            Span("B", 120, -6.1, -3.0),
        }).Single();

        suggestion.Label.Should().Be("חניה");
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.Low);
        suggestion.IsStrongReviewCandidate.Should().BeFalse();
    }

    [Fact]
    public void GroupKeys_AreStableAndExposeAlignmentSideRankKindsAndWidthBucket()
    {
        var suggestions = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, -6.1, -3.0),
            Span("B", 120, -6.0, -3.0),
        });

        suggestions.Select(item => item.HomologyGroupKey).Distinct()
            .Should().ContainSingle()
            .Which.Should().Be("2000|left|0|curb>curb|w~3.0");
    }

    [Fact]
    public void LocalSourceConflict_IsNotOverruledByStrongPeers()
    {
        var target = Span("cl-7C9E", 40759, -0.6917361187231119, 0.8488760316700082,
            left: "island", right: "island") with { HasConflictingLocalEvidence = true };
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 40480, -0.7, 0.85, true, "אי תנועה", "boundary-rule", "A", left: "island", right: "island"),
            Span("B", 41140, -0.7, 0.85, true, "אי תנועה", "boundary-rule", "B", left: "island", right: "island"),
            target,
        }).Single();
        suggestion.Confidence.Should().Be(SectionSpanSuggestionLogic.Confidence.Conflict);
        suggestion.Label.Should().BeNull();
        suggestion.EvidenceRecords.Should().HaveCount(2, "peer evidence remains reviewable");
    }

    [Theory]
    [InlineData(6.500216287075556, "נתיב נסיעה", 6.3)]
    [InlineData(0.500431331984645, "שביל אופניים", 0.5)]
    public void RecordedTargetWidth_CannotReceiveAnUnsaveableVehicleSuggestion(double width, string label, double peerWidth)
    {
        var suggestion = SectionSpanSuggestionLogic.Suggest(new[]
        {
            Span("A", 100, 1, 1 + peerWidth, true, label, "boundary-rule", "A"),
            Span("B", 120, 1, 1 + peerWidth, true, label, "boundary-rule", "B"),
            Span("C", 140, 1, 1 + width),
        }).Single();
        suggestion.Label.Should().BeNull();
        suggestion.IsStrongReviewCandidate.Should().BeFalse();
        suggestion.SupportingRecordCount.Should().Be(2);
    }
}
