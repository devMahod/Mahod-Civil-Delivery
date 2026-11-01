using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Reason = MahodAI.CivilDelivery.Shared.TypicalSectionStripMatchLogic.Reason;
using Segment = MahodAI.CivilDelivery.Shared.FirmSectionFrameLogic.ChainSegment;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class TypicalSectionStripMatchLogicTests
{
    private static Segment Seg(double from, double to, double? width = null) =>
        new(from, to, width ?? Math.Round(to - from, 2),
            (width ?? (to - from)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

    private static (double, double, string) Plan(double from, double to, string label) => (from, to, label);

    [Fact]
    public void ExactPlanStrips_NameTheirSegmentsAndTheRestStayUnnamedWithAReason()
    {
        // The owner's sample chain, both sides of the centreline.
        var segments = new[]
        {
            Seg(-18, -12), Seg(-12, -11), Seg(-11, -7.4), Seg(-7.4, -5.8), Seg(-5.8, -3.8),
            Seg(-3.8, -0.5), Seg(-0.5, 0.5), Seg(0.5, 3.8), Seg(3.8, 7.19), Seg(7.19, 8.19), Seg(8.19, 11.79),
        };
        var plan = new[]
        {
            Plan(-18, -12, "מדרכה"),
            Plan(-11, -7.4, "נתיב"),
            Plan(-7.4, -5.8, ""),
            Plan(3.8, 7.19, "נתיב"),
            Plan(8.19, 11.79, "שביל אופניים"),
        };

        var result = TypicalSectionStripMatchLogic.Match(segments, plan);

        result.Matches.Should().HaveCount(segments.Length);
        result.Matches.Select(match => match.SegmentIndex).Should().Equal(Enumerable.Range(0, segments.Length));
        result.Matches[0].Label.Should().Be("מדרכה");
        result.Matches[2].Label.Should().Be("נתיב");
        result.Matches[8].Label.Should().Be("נתיב");
        result.Matches[10].Label.Should().Be("שביל אופניים");
        result.Matches[3].Should().Be(new TypicalSectionStripMatchLogic.StripMatch(3, null, Reason.NoPlanStrip),
            "an unnamed plan piece cannot name anything");
        result.Matches.Where(match => !match.IsMatched)
            .Should().OnlyContain(match => match.Reason == Reason.NoPlanStrip);
        result.MatchedCount.Should().Be(4);
        result.UnmatchedCount.Should().Be(7);
    }

    [Fact]
    public void StripWithinTheOverlapAndWidthTolerance_StillNamesTheSegment()
    {
        // Plan lane 3.50 m, shifted 0.30 m: overlap 3.20 of 3.39 (94 %), widths 0.11 apart (< 0.17).
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(3.8, 7.19, 3.39)], [Plan(3.5, 7.0, "נתיב")]);

        result.Matches.Single().Label.Should().Be("נתיב");
        result.Matches.Single().Reason.Should().BeNull();
        result.MatchedCount.Should().Be(1);
    }

    [Fact]
    public void StripOfAnotherWidth_IsNotTheSameStrip()
    {
        // Full overlap, but the plan strip is 0.50 m wider than max(0.15, 5 % of 3.39).
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(3.8, 7.19, 3.39)], [Plan(3.8, 7.69, "נתיב")]);

        result.Matches.Single().Label.Should().BeNull();
        result.Matches.Single().Reason.Should().Be(Reason.NoPlanStrip);
    }

    [Fact]
    public void StripThatCoversLessThanEightyPercent_IsNotTheSameStrip()
    {
        // Same width, but shifted so only 2.19 of 3.39 m (65 %) overlap.
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(3.8, 7.19, 3.39)], [Plan(5.0, 8.39, "נתיב")]);

        result.Matches.Single().Reason.Should().Be(Reason.NoPlanStrip);
        result.UnmatchedCount.Should().Be(1);
    }

    [Fact]
    public void TwoFittingStripsWithDifferentNames_AreAConflictNotAGuess()
    {
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(3.8, 7.19, 3.39)], [Plan(3.8, 7.19, "נתיב"), Plan(3.7, 7.2, "חניה")]);

        result.Matches.Single().Label.Should().BeNull();
        result.Matches.Single().Reason.Should().Be(Reason.Conflict);
        result.MatchedCount.Should().Be(0);
    }

    [Fact]
    public void SeveralFittingStripsWithTheSameName_CountAsOne()
    {
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(3.8, 7.19, 3.39)], [Plan(3.8, 7.19, "נתיב"), Plan(3.75, 7.2, " נתיב ")]);

        result.Matches.Single().Label.Should().Be("נתיב");
    }

    [Fact]
    public void NoStripNearTheSegment_IsNoPlanStrip()
    {
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(-0.5, 0.5, 1.0)], [Plan(3.8, 7.19, "נתיב"), Plan(-11, -7.4, "נתיב")]);

        result.Matches.Single().Reason.Should().Be(Reason.NoPlanStrip);
    }

    [Fact]
    public void LeftSideAndReversedSpans_MatchBySignedOffsetsAlone()
    {
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(-11, -7.4, 3.60), Seg(-7.4, -3.8, 3.60)],
            [Plan(-7.4, -11, "נתיב"), Plan(-3.8, -7.4, "מדרכה")]);

        result.Matches[0].Label.Should().Be("נתיב");
        result.Matches[1].Label.Should().Be("מדרכה");
        result.MatchedCount.Should().Be(2);
    }

    [Fact]
    public void SegmentWithoutAUsableWidth_IsReportedNotMatched()
    {
        var result = TypicalSectionStripMatchLogic.Match(
            [Seg(3.8, 3.8, 0), new Segment(3.8, 7.19, double.NaN, "")],
            [Plan(3.8, 7.19, "נתיב")]);

        result.Matches.Should().OnlyContain(match => match.Reason == Reason.InvalidSegment);
        result.UnmatchedCount.Should().Be(2);
    }

    [Fact]
    public void NoSegments_IsAnEmptyResult()
    {
        var result = TypicalSectionStripMatchLogic.Match([], [Plan(3.8, 7.19, "נתיב")]);

        result.Matches.Should().BeEmpty();
        result.MatchedCount.Should().Be(0);
        result.UnmatchedCount.Should().Be(0);
    }
}
