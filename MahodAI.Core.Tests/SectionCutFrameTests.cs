using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public class SectionCutFrameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkewedCut_UsesActualDistanceAndStableLeftRight(bool reverse)
    {
        var a = new P2(-20, -20); var b = new P2(10, 10);
        SectionCutFrame.TryCreate(reverse ? b : a, reverse ? a : b,
            new(0, 0), 90, out var frame).Should().BeTrue();
        frame!.MinOffset.Should().BeApproximately(-20 * Math.Sqrt(2), 1e-9);
        frame.MaxOffset.Should().BeApproximately(10 * Math.Sqrt(2), 1e-9);
        frame.OffsetOf(new(5, 5)).Should().BeApproximately(5 * Math.Sqrt(2), 1e-9);
        frame.PointAt(5 * Math.Sqrt(2)).X.Should().BeApproximately(5, 1e-9);
        frame.PointAt(5 * Math.Sqrt(2)).Y.Should().BeApproximately(5, 1e-9);
        frame.MatchesNativeLeftEndpoint(a).Should().BeTrue();
        frame.MatchesNativeLeftEndpoint(b).Should().BeFalse();
        frame.MatchesNativeLeftEndpoint(new(-19.9, -20)).Should().BeFalse();
    }

    [Fact]
    public void AlignmentReversalFlipsTheFrame_ButNotTheWorldGeometry()
    {
        SectionCutFrame.TryCreate(new(-20, -20), new(10, 10), new(0, 0), 270, out var frame)
            .Should().BeTrue();
        frame!.LeftEndpoint.Should().Be(new P2(10, 10));
        frame.OffsetOf(new(5, 5)).Should().BeApproximately(-5 * Math.Sqrt(2), 1e-9);
        frame.PointAt(-5 * Math.Sqrt(2)).X.Should().BeApproximately(5, 1e-9);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 90)]
    [InlineData(30, 30, 90)]
    [InlineData(0, 0, 45)]
    public void UnusableOrOffCutCrossingsFailClosed(double x, double y, double tangent)
    {
        // The first case uses a zero-length cut; other cases use a diagonal CL.
        var a = tangent == 0 ? new P2(0, 0) : new P2(-10, -10);
        var b = tangent == 0 ? a : new P2(10, 10);
        SectionCutFrame.TryCreate(a, b, new(x, y), tangent, out var frame).Should().BeFalse();
        frame.Should().BeNull();
    }

    [Fact]
    public void DisplayPaddingIsNotSemanticWidth_ClippingIsRejected()
    {
        SectionCutFrame.TryCreate(new(-3, 0), new(4, 0), new(0, 0), 90, out var frame)
            .Should().BeTrue();
        frame!.IsContainedInDisplay(-3, 4).Should().BeTrue();
        frame.IsContainedInDisplay(-10, 10).Should().BeTrue();
        frame.IsContainedInDisplay(-2.9, 4).Should().BeFalse();
        frame.IsContainedInDisplay(-3, 3.9).Should().BeFalse();
        frame.IsContainedInDisplay(double.NaN, 10).Should().BeFalse();
        frame.MinOffset.Should().Be(-3);
        frame.MaxOffset.Should().Be(4);
        frame.Width.Should().Be(7);
    }

    [Fact]
    public void NearbyArrowProjectionPreservesItsLaneAcrossLongitudinalMovement()
    {
        SectionCutFrame.TryCreate(new(-20, -20), new(20, 20), new(0, 0), 90, out var frame)
            .Should().BeTrue();
        var onCut = frame!.OffsetOf(new(5, 5));
        frame.OffsetAtAlignmentProjection(new(5, 18)).Should().BeApproximately(onCut, 1e-9);
        frame.OffsetAtAlignmentProjection(new(5, -12)).Should().BeApproximately(onCut, 1e-9);
        // Plain dot projection would shift a north/south-displaced arrow into another lane.
        frame.OffsetOf(new(5, 18)).Should().NotBeApproximately(onCut, 1);
    }

    [Fact]
    public void PreviewSamplingSharesTheCutDistanceAndActualSkewedWorldPoints()
    {
        var path = SectionPreviewGeometry.BuildSamplingPath(new(-20, -20), new(10, 10),
            new(0, 0), 90, maximumSamples: 81);
        path.First().Offset.Should().BeApproximately(-20 * Math.Sqrt(2), 1e-9);
        path.Last().Offset.Should().BeApproximately(10 * Math.Sqrt(2), 1e-9);
        path.Should().OnlyContain(p => Math.Abs(p.X - p.Y) < 1e-9);
        path.Should().Contain(p => p.Offset == 0 && p.X == 0 && p.Y == 0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(203700, 649250)]
    [InlineData(1000000000, 2000000000)]
    public void PreviewAxisIsCanonical_WhenRoundedNegativeMidpointGroupsWithExactZero(
        double originX, double originY)
    {
        // This sampling grid computes its midpoint as about -7.1e-15. Sorting
        // must not let that value replace the explicit, actually measured axis.
        var path = SectionPreviewGeometry.BuildSamplingPath(
            new(originX - 53, originY - 1), new(originX + 53, originY + 1),
            new(originX, originY), 90, maximumSamples: 81);

        var axis = path.Where(p => Math.Round(p.Offset, 9) == 0).Should().ContainSingle().Subject;
        axis.Offset.Should().Be(0);
        axis.X.Should().Be(originX);
        axis.Y.Should().Be(originY);
        path.Count.Should().BeLessThanOrEqualTo(81);
    }
}
