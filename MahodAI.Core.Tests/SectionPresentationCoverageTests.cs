using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionPresentationCoverageTests
{
    [Fact]
    public void InteriorStripFragment_CannotProveTheFullClWidth()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (0.0, "sidewalk", "מדרכה"),
                (1.4, "curb", "אבן שפה"),
            },
            requiredLeftOffset: -10,
            requiredRightOffset: 10);

        analysis.Summary.BoundarySource.Should().Be("cl-extents");
        analysis.Summary.OuterBoundariesProven.Should().BeFalse();
        analysis.Summary.WidthSpanCount.Should().Be(1,
            "CL geometry ends are not synthetic dimension anchors");
        analysis.Summary.NamedStripCount.Should().Be(1);
        analysis.Summary.IsComplete.Should().BeFalse(
            "recognizing one valid sidewalk does not name the rest of the section");
    }

    [Fact]
    public void OversizedGap_BreaksTheContinuousWidthChain()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-40.0, "sidewalk", "מדרכה"),
                (0.0, "curb", "אבן שפה"),
                (40.0, "sidewalk", "מדרכה"),
            },
            requiredLeftOffset: -40,
            requiredRightOffset: 40);

        analysis.Summary.BoundarySource.Should().Be("plan-mark-extents");
        analysis.Summary.OuterBoundariesProven.Should().BeTrue();
        analysis.Summary.ContinuousWidthChain.Should().BeFalse(
            "AdjacentWidths deliberately rejects unrelated gaps over 30m");
        analysis.Summary.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void TwoSidedRowEnvelope_IsTheOuterBoundaryAndEverySpanMustBeNamed()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-8.0, "row", "זכות דרך"),
                (-7.0, "strip", "שול"),
                (-6.0, "sidewalk", "מדרכה"),
                (-3.0, "curb", "אבן שפה"),
                ( 0.0, "lane", "קו נתיב"),
                ( 3.0, "lane", "קו נתיב"),
                ( 6.0, "curb", "אבן שפה"),
                ( 7.5, "strip", "שול"),
                ( 9.0, "row", "זכות דרך"),
            },
            requiredLeftOffset: -31,
            requiredRightOffset: 31);

        analysis.Summary.BoundarySource.Should().Be("row");
        analysis.Summary.BoundaryFrom.Should().Be(-8);
        analysis.Summary.BoundaryTo.Should().Be(9);
        analysis.Summary.OuterBoundariesProven.Should().BeTrue();
        analysis.Summary.ContinuousWidthChain.Should().BeTrue();
        analysis.Summary.NamedStripCount.Should().Be(analysis.Summary.WidthSpanCount);
        analysis.Summary.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void TwoSidedActualPlanMarks_TrimUnmarkedClExteriorFromPresentationChain()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-9.67, "sidewalk", "מדרכה"),
                (-6.00, "curb", "אבן שפה"),
                ( 0.00, "lane", "קו נתיב"),
                ( 6.00, "curb", "אבן שפה"),
                (12.73, "sidewalk", "מדרכה"),
            },
            requiredLeftOffset: -31,
            requiredRightOffset: 31);

        analysis.Summary.BoundarySource.Should().Be("plan-mark-extents");
        analysis.Summary.BoundaryFrom.Should().Be(-9.67);
        analysis.Summary.BoundaryTo.Should().Be(12.73);
        analysis.DimensionMarks.Select(mark => mark.Offset)
            .Should().Equal(-9.67, -6.0, 0.0, 6.0, 12.73);
        analysis.WidthSpans.Should().OnlyContain(span =>
            span.From >= -9.67 && span.To <= 12.73);
    }

    [Fact]
    public void OneSidedOrMissingPlanMarks_CannotProvePresentationBoundary()
    {
        var oneSided = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (1.0, "curb", "אבן שפה"),
                (4.0, "sidewalk", "מדרכה"),
            }, -31, 31);
        var missing = SectionProjectionLogic.AnalyzePresentationCoverage(
            Array.Empty<(double Offset, string Kind, string Label)>(), -31, 31);

        foreach (var analysis in new[] { oneSided, missing })
        {
            analysis.Summary.BoundarySource.Should().Be("cl-extents");
            analysis.Summary.OuterBoundariesProven.Should().BeFalse();
            analysis.Summary.IsComplete.Should().BeFalse();
            analysis.DimensionMarks.Should().NotContain(mark => mark.Kind == "extent");
        }
    }

    [Fact]
    public void ActualTwoSidedRowWinsOverPlanMarkExtents()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-12.0, "sidewalk", "מדרכה"),
                ( -8.0, "row", "זכות דרך"),
                ( -4.0, "curb", "אבן שפה"),
                (  4.0, "curb", "אבן שפה"),
                (  9.0, "row", "זכות דרך"),
                ( 14.0, "sidewalk", "מדרכה"),
            }, -31, 31);

        analysis.Summary.BoundarySource.Should().Be("row");
        analysis.Summary.BoundaryFrom.Should().Be(-8);
        analysis.Summary.BoundaryTo.Should().Be(9);
        analysis.DimensionMarks.Should().NotContain(mark =>
            mark.Offset < -8 || mark.Offset > 9);
    }

    [Fact]
    public void PresentationDigest_BindsPlanMarkSourceIdentity()
    {
        static SectionProjectionLogic.PresentationAnalysis Analyze(string leftSource) =>
            SectionProjectionLogic.AnalyzePresentationCoverage(new[]
            {
                new SectionProjectionLogic.PresentationMark(
                    -3, "curb", "אבן שפה", leftSource),
                new SectionProjectionLogic.PresentationMark(
                     3, "curb", "אבן שפה", "sha-b|GM|22"),
            }, -31, 31, approvedOverrides: new[]
            {
                new SectionProjectionLogic.SpanLabelOverride(
                    0, "נתיב נסיעה", "traffic-arrow", "arrow-evidence"),
            });

        var original = Analyze("sha-a|GM|11");
        var replacement = Analyze("sha-c|GM|11");

        original.Summary.BoundarySource.Should().Be("plan-mark-extents");
        replacement.Summary.EvidenceDigest.Should()
            .NotBe(original.Summary.EvidenceDigest);
    }

    [Fact]
    public void LoneRowBoundary_CannotSilentlyFallBackToClExtents()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-8.0, "row", "זכות דרך"),
                (-6.0, "sidewalk", "מדרכה"),
                (-3.0, "curb", "אבן שפה"),
                ( 0.0, "lane", "קו נתיב"),
                ( 3.0, "curb", "אבן שפה"),
            },
            requiredLeftOffset: -10,
            requiredRightOffset: 10);

        analysis.Summary.BoundarySource.Should().Be("row-incomplete");
        analysis.Summary.OuterBoundariesProven.Should().BeFalse();
        analysis.Summary.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ClGeometryExtents_DoNotChangeTwoSidedPlanMarkEvidenceDigest()
    {
        var marks = new[]
        {
            (-3.0, "curb", "אבן שפה"),
            ( 0.0, "lane", "קו נתיב"),
            ( 3.0, "curb", "אבן שפה"),
        };

        var a = SectionProjectionLogic.AnalyzePresentationCoverage(
            marks, -3, 3).Summary.EvidenceDigest;
        var b = SectionProjectionLogic.AnalyzePresentationCoverage(
            marks, -4, 4).Summary.EvidenceDigest;

        b.Should().Be(a,
            "CL geometry extents no longer participate as presentation anchors when real two-sided marks exist");
    }

    [Fact]
    public void ExactGardenBoundaries_NameTheObservedNatalieGardenSpan()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-4.0, "garden", "גבול גינון"),
                ( 4.0, "garden", "גבול גינון"),
            },
            requiredLeftOffset: -4,
            requiredRightOffset: 4);

        analysis.StripLabels.Should().ContainSingle()
            .Which.Label.Should().Be("גינון");
        analysis.Summary.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void ApprovedSpanLabel_ClosesOneCurbToCurbGapWithoutGuessing()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-3.0, "curb", "אבן שפה"),
                ( 3.0, "curb", "אבן שפה"),
            },
            requiredLeftOffset: -3,
            requiredRightOffset: 3,
            approvedOverrides: new[]
            {
                new SectionProjectionLogic.SpanLabelOverride(
                    0, "חניה", "manual-profile"),
            });

        analysis.UnresolvedSpans.Should().BeEmpty();
        analysis.StripLabels.Should().ContainSingle()
            .Which.Label.Should().Be("חניה");
        analysis.Summary.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void CompetingLabelsInsideOneSpan_RemainUnresolvedAndFailClosed()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-3.0, "curb", "אבן שפה"),
                ( 3.0, "curb", "אבן שפה"),
            },
            requiredLeftOffset: -3,
            requiredRightOffset: 3,
            approvedOverrides: new[]
            {
                new SectionProjectionLogic.SpanLabelOverride(0, "חניה", "manual-profile"),
                new SectionProjectionLogic.SpanLabelOverride(0.1, "נתיב נסיעה", "traffic-arrow"),
            });

        analysis.UnresolvedSpans.Should().ContainSingle()
            .Which.Reason.Should().Be("conflicting-strip-label-evidence");
        analysis.Summary.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ArcTessellationHonoursTheRequestedSagitta()
    {
        const double radius = 10;
        const double sweep = Math.PI;
        const double tolerance = 0.005;

        var segments = SectionProjectionLogic.ArcTessellationSegmentCount(
            radius, sweep, tolerance);
        var actualSagitta = radius * (1 - Math.Cos(sweep / segments / 2));

        segments.Should().BeGreaterThan(1);
        actualSagitta.Should().BeLessThanOrEqualTo(tolerance + 1e-12);
    }

    [Fact]
    public void SemicircleBulgeRetainsItsSweepAndRadius()
    {
        SectionProjectionLogic.BulgeSweepRadians(1)
            .Should().BeApproximately(Math.PI, 1e-12);
        SectionProjectionLogic.BulgeRadius(6, 1)
            .Should().BeApproximately(3, 1e-12);
    }

    [Fact]
    public void NearbySameSystemEntitiesMergeEvenWithDistinctRealHandles()
    {
        var rule = new SectionProjectionLogic.ProjectionRuleMatch(
            "utility", "חשמל", 1);
        var crossings = new[]
        {
            new SectionProjectionLogic.Crossing(
                3.0, null, "HASHMAL", "UT-3D", rule, "A1", 100, 200),
            new SectionProjectionLogic.Crossing(
                3.1, 281.5, "hashmal", "ut-3d", rule, "A2", 100.1, 200),
            new SectionProjectionLogic.Crossing(
                3.2, null, "HASHMAL", "UT-3D", rule, "A3", 100.2, 200),
        };

        var merged = SectionProjectionLogic.MergeNearby(crossings);

        merged.Should().ContainSingle();
        merged[0].SourceHandle.Should().Be("A2",
            "the representative with a real elevation remains preferred");
    }

    // 6422 model, STA-12145 / alignment 600, exactly as PLAN read it on 2026-09-03:
    // every curb / island edge is drawn as two faces 0.23-0.47 m apart.
    private static (double, string, string)[] Sta12145Marks() => new[]
    {
        (-14.217133614977874, "sidewalk", "מדרכה"),
        (-11.091003428949964, "curb", "אבן שפה"),
        (-10.779001927376934, "bike", "שביל אופניים"),
        (-8.779011703706388, "bike", "שביל אופניים"),
        (-8.44901268616232, "curb", "אבן שפה"),
        (-4.038656244455503, "curb", "אבן שפה"),
        (-2.029327420932669, "island", "אי תנועה"),
        (-1.79932973808832, "island", "אי תנועה"),
        (0.241, "island", "אי תנועה"),
        (0.471, "island", "אי תנועה"),
        (0.944, "curb", "אבן שפה"),
        (6.721, "curb", "אבן שפה"),
        (8.039, "curb", "אבן שפה"),
        (11.238659413509614, "sidewalk", "מדרכה"),
    };

    [Fact]
    public void CurbFaces_UnderTheNoiseFloor_AreCoveredFurniture_NotChainBreaks()
    {
        var unnamed = SectionProjectionLogic.AnalyzePresentationCoverage(
            Sta12145Marks(), requiredLeftOffset: -24.67, requiredRightOffset: 25.92);

        unnamed.Summary.BoundarySource.Should().Be("plan-mark-extents");
        unnamed.Summary.OuterBoundariesProven.Should().BeTrue();
        unnamed.Summary.DimensionMarkCount.Should().Be(14);
        unnamed.Summary.WidthSpanCount.Should().Be(8);
        unnamed.Summary.NarrowGapCount.Should().Be(5, "five curb / island faces under 0.50 m");
        unnamed.Summary.OversizedGapCount.Should().Be(0);
        unnamed.Summary.ContinuousWidthChain.Should().BeTrue(
            "the 1.2.22-1.2.33 rule counted the curb faces as holes and no real section could ever be Ready");
        unnamed.UnresolvedSpans.Should().HaveCount(4, "curb-to-curb and curb-to-island strips get no guessed name");
        unnamed.Summary.IsComplete.Should().BeFalse("four strips still have no name");

        var named = SectionProjectionLogic.AnalyzePresentationCoverage(
            Sta12145Marks(), requiredLeftOffset: -24.67, requiredRightOffset: 25.92,
            approvedOverrides: new[]
            {
                new SectionProjectionLogic.SpanLabelOverride(-6.24, "נתיב נסיעה", "manual-profile"),
                new SectionProjectionLogic.SpanLabelOverride(-3.03, "מדרכה", "manual-profile"),
                new SectionProjectionLogic.SpanLabelOverride(3.83, "נתיב נסיעה", "manual-profile"),
                new SectionProjectionLogic.SpanLabelOverride(7.38, "מדרכה", "manual-profile"),
            });

        named.UnresolvedSpans.Should().BeEmpty();
        named.Summary.NamedStripCount.Should().Be(8);
        named.Summary.ContinuousWidthChain.Should().BeTrue();
        named.Summary.IsComplete.Should().BeTrue("Arthur's four names make the section Ready");
        named.StripLabels.Should().NotContain(s => s.To - s.From < SectionProjectionLogic.StripNoiseFloorM,
            "a curb face never becomes a named strip");
    }

    [Fact]
    public void OversizedHoles_AreCounted_AndStillBreakTheChain()
    {
        var analysis = SectionProjectionLogic.AnalyzePresentationCoverage(
            new[]
            {
                (-40.0, "sidewalk", "מדרכה"),
                (-37.0, "curb", "אבן שפה"),
                (-36.7, "curb", "אבן שפה"),
                (0.0, "curb", "אבן שפה"),
                (3.0, "sidewalk", "מדרכה"),
            },
            requiredLeftOffset: -40,
            requiredRightOffset: 40);

        analysis.Summary.NarrowGapCount.Should().Be(1);
        analysis.Summary.OversizedGapCount.Should().Be(1, "36.7 m between unrelated marks is a hole");
        analysis.Summary.ContinuousWidthChain.Should().BeFalse();
        analysis.Summary.IsComplete.Should().BeFalse();
    }
}
