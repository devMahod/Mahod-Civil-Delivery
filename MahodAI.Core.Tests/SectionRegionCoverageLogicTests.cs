using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public sealed class SectionRegionCoverageLogicTests
{
    private static IReadOnlyList<P2> Box(double left, double bottom, double right, double top) =>
        new[] { new P2(left, bottom), new P2(right, bottom), new P2(right, top), new P2(left, top) };

    private static bool Covered(P2 a, P2 b, params IReadOnlyList<P2>[] loops) =>
        SectionRegionCoverageLogic.CoversSegment(a, b, loops);

    [Fact]
    public void FullInteriorSpan_UsesTheWholeRegionAndMayEndAtItsBoundaries() =>
        Covered(new(-5, 0), new(5, 0), Box(-5, -2, 5, 2)).Should().BeTrue();

    [Fact]
    public void AnInteriorMidpoint_IsNotEnoughWhenTheSpanExtendsOutsideTheSource() =>
        Covered(new(-6, 0), new(5, 0), Box(-5, -2, 5, 2)).Should().BeFalse();

    [Fact]
    public void ASourceHole_IsNotLabelledAndCannotBeBridgedByItsOuterLoop()
    {
        var outer = Box(-5, -3, 5, 3);
        var hole = Box(-1, -1, 1, 1);
        Covered(new(-4, 0), new(4, 0), outer, hole).Should().BeFalse();
        Covered(new(-0.5, 0), new(0.5, 0), outer, hole).Should().BeFalse();
        Covered(new(-4, 0), new(-1, 0), outer, hole).Should().BeTrue();
    }

    [Fact]
    public void LoopOrderAndWindingDoNotTurnAHoleIntoASolidLabelRegion()
    {
        var outer = Box(-5, -3, 5, 3);
        var hole = Box(-1, -1, 1, 1);
        Covered(new(-4, 0), new(4, 0), hole.Reverse().ToArray(), outer.Reverse().ToArray())
            .Should().BeFalse();
    }

    [Fact]
    public void ASmallOffCentreHole_CannotBeMissedByCheckingOnlyMidpointOrFixedSamples() =>
        Covered(new(-4, 0), new(4, 0), Box(-5, -3, 5, 3), Box(3.123, -0.2, 3.125, 0.2))
            .Should().BeFalse();

    [Fact]
    public void ARealIslandInsideAHole_IsFilledButTheRemainingHoleStaysEmpty()
    {
        var loops = new[] { Box(-6, -4, 6, 4), Box(-4, -3, 4, 3), Box(-1, -1, 1, 1) };
        Covered(new(-0.5, 0), new(0.5, 0), loops).Should().BeTrue();
        Covered(new(-3, 0), new(3, 0), loops).Should().BeFalse();
    }

    [Fact]
    public void OuterStyle_DoesNotRefillANestedIslandButIgnoreStyleFillsItsHoles()
    {
        var loops = new[] { Box(-6, -4, 6, 4), Box(-4, -3, 4, 3), Box(-1, -1, 1, 1) };
        SectionRegionCoverageLogic.CoversSegment(new(-0.5, 0), new(0.5, 0), loops,
            SectionRegionCoverageLogic.FillStyle.Outer).Should().BeFalse();
        SectionRegionCoverageLogic.CoversSegment(new(-3, 0), new(3, 0), loops,
            SectionRegionCoverageLogic.FillStyle.Ignore).Should().BeTrue();
    }

    [Fact]
    public void SeparateFilledIslands_DoNotCoverTheGapBetweenThem() =>
        Covered(new(-4, 0), new(4, 0), Box(-5, -2, -2, 2), Box(2, -2, 5, 2))
            .Should().BeFalse();

    [Fact]
    public void TouchingOrRunningAlongABoundary_IsNotInteriorEvidence()
    {
        Covered(new(-4, -2), new(4, -2), Box(-5, -2, 5, 2)).Should().BeFalse();
        Covered(new(-6, -2), new(-5, -2), Box(-5, -2, 5, 2)).Should().BeFalse();
    }

    [Fact]
    public void ConcaveNotchBetweenCoveredEndpoints_RemainsUnresolved()
    {
        IReadOnlyList<P2> concave = new[]
        {
            new P2(-5, -3), new P2(5, -3), new P2(5, 3), new P2(1, 3),
            new P2(1, -1), new P2(-1, -1), new P2(-1, 3), new P2(-5, 3),
        };
        Covered(new(-4, 0), new(4, 0), concave).Should().BeFalse();
        Covered(new(-4, -2), new(4, -2), concave).Should().BeTrue();
    }

    [Fact]
    public void TransformedMirroredSurveyCoordinates_PreserveCoverageAndHoleSemantics()
    {
        static P2 Transform(P2 p) => new(200000 + 2 * p.Y, 650000 + 3 * p.X);
        var outer = Box(-5, -3, 5, 3).Select(Transform).ToArray();
        var hole = Box(-1, -1, 1, 1).Select(Transform).ToArray();
        Covered(Transform(new(-4, 0)), Transform(new(4, 0)), outer, hole).Should().BeFalse();
        Covered(Transform(new(-4, 2)), Transform(new(4, 2)), outer, hole).Should().BeTrue();
    }

    [Fact]
    public void MissingLoops_ReturnNoEvidenceButUnreadableLoopsNeverBecomePartialEvidence()
    {
        Covered(new(-1, 0), new(1, 0)).Should().BeFalse();
        var invalid = new[] { Box(-5, -3, 5, 3), new[] { new P2(0, double.NaN), new P2(1, 0), new P2(0, 1) } };
        Action act = () => Covered(new(-1, 0), new(1, 0), invalid);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DegenerateSpansAndLoops_AreRejected()
    {
        Action span = () => Covered(new(0, 0), new(0, 0), Box(-2, -2, 2, 2));
        Action loop = () => Covered(new(-1, 0), new(1, 0), new[] { new P2(0, 0), new P2(1, 0), new P2(2, 0) });
        span.Should().Throw<ArgumentException>();
        loop.Should().Throw<ArgumentException>();
    }
}
