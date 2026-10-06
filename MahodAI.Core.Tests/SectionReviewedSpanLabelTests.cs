using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public sealed class SectionReviewedSpanLabelTests
{
    private static readonly DateTime ApprovedAt = new(2026, 9, 9, 17, 0, 0, DateTimeKind.Utc);
    private static PresentationMark[] Marks(string sourceName = "מדרכה") => new[]
    {
        new PresentationMark(-4, "curb", "", "left"),
        new PresentationMark(0, "curb", "", "middle"),
        new PresentationMark(4, "curb", "", "right"),
        new PresentationMark(-2, "strip", sourceName, "source-A"),
        new PresentationMark(2, "strip", "גינון", "source-B"),
    };

    [Fact]
    public void ExplicitNameWinsOnlyItsExactInterval_PreservingGeometryOriginalEvidenceAndReplay()
    {
        var before = AnalyzePresentationCoverage(Marks());
        var edit = SectionReviewedSpanLabelLogic.Create(-4, 0, "רצועת בטיחות", "Arthur", ApprovedAt);
        var plan = AnalyzePresentationCoverage(Marks(), approvedOverrides: new[] { edit });
        var apply = AnalyzePresentationCoverage(Marks(), approvedOverrides: new[] { edit });
        plan.StripLabels.Should().Contain((-4d, 0d, "רצועת בטיחות")).And.Contain((0d, 4d, "גינון"));
        plan.DimensionMarks.Should().Equal(before.DimensionMarks);
        plan.WidthSpans.Should().Equal(before.WidthSpans);
        plan.Summary.PlanMarkCount.Should().Be(before.Summary.PlanMarkCount);
        plan.Summary.EvidenceDigest.Should().NotBe(before.Summary.EvidenceDigest)
            .And.Be(apply.Summary.EvidenceDigest);
        AnalyzePresentationCoverage(Marks("אי תנועה"), approvedOverrides: new[] { edit })
            .Summary.EvidenceDigest.Should().NotBe(plan.Summary.EvidenceDigest,
                "the original source label remains evidence even when the engineer overrides its name");
    }

    [Fact]
    public void OldManualConflictAndNoOverrideKeepTheirPreviousSemantics()
    {
        var old = new[] { new SpanLabelOverride(-2, "גינון", "manual-profile", "old approval") };
        SectionReviewedSpanLabelLogic.ForNameResolution(old, new[] { (-4d, 0d, 4d) }).Should().BeSameAs(old);
        AnalyzePresentationCoverage(Marks(), approvedOverrides: old).UnresolvedSpans
            .Should().ContainSingle(span => span.From == -4 && span.To == 0);
    }

    [Theory]
    [InlineData(-4.000001, 0)]
    [InlineData(-4, 0.000001)]
    public void AChangedBoundaryNeverReusesAnExplicitReview(double from, double to)
    {
        var edit = SectionReviewedSpanLabelLogic.Create(from, to, "גינון", "Arthur", ApprovedAt);
        AnalyzePresentationCoverage(Marks(), approvedOverrides: new[] { edit }).StripLabels
            .Should().Contain((-4d, 0d, "מדרכה"));
    }

    [Fact]
    public void MalformedOrConflictingExplicitReviewsDoNotChooseAName()
    {
        var forged = new SpanLabelOverride(-2, "גינון", SectionReviewedSpanLabelLogic.Source, "not validated evidence");
        AnalyzePresentationCoverage(Marks(), approvedOverrides: new[] { forged }).StripLabels
            .Should().Contain((-4d, 0d, "מדרכה"));
        var first = SectionReviewedSpanLabelLogic.Create(-4, 0, "גינון", "Arthur", ApprovedAt);
        var second = SectionReviewedSpanLabelLogic.Create(-4, 0, "חניה", "Engineer", ApprovedAt);
        AnalyzePresentationCoverage(Marks(), approvedOverrides: new[] { first, second }).UnresolvedSpans
            .Should().ContainSingle(span => span.From == -4 && span.Reason == "conflicting-strip-label-evidence");
    }
}
