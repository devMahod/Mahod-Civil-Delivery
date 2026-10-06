using System;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Arrow = MahodAI.CivilDelivery.Shared.TrafficDirectionEvidenceLogic.ArrowEvidence;
using Segment = MahodAI.CivilDelivery.Shared.SectionTrafficStraightScopeLogic.Segment;

namespace MahodAI.Core.Tests;

public sealed class SectionTrafficStraightScopeTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static SectionCutFrame Frame(bool skew = false)
    {
        Assert.True(SectionCutFrame.TryCreate(new(-10, skew ? -10 : 0), new(10, skew ? 10 : 0),
            new(0, 0), 90, out var frame));
        return frame!;
    }
    private static Arrow ArrowAt(double x, double y, double heading = Math.PI / 2, string handle = "A") =>
        new(x, y, heading, "BL-TR-ARRW", "TR-ARW", "synthetic", handle);
    private static SectionVehicleDirectionPlanner.DirectionPlan Direction(
        SectionTrafficStraightScopeLogic.Resolution scope,
        ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision[]? decisions = null)
    {
        var frame = Frame();
        var target = frame.PointAt(1.75);
        return SectionVehicleDirectionPlanner.Resolve(Hash, "A", "axis", 1.75, target.X, target.Y, Math.PI / 2,
            scope.Evidence, decisions, laneFromOffsetM: 0, laneToOffsetM: 3.5, laneCutFrame: frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvenNativeLineKeepsValidDirectionsAndCompositeTracks_WhenSegmentIsReversed(bool reverse)
    {
        var start = new SectionProjectionLogic.P2(0, -100);
        var end = new SectionProjectionLogic.P2(0, 100);
        var arrows = new[] { ArrowAt(1.75, 12), ArrowAt(5, -12, handle: "B") };
        var scope = SectionTrafficStraightScopeLogic.Resolve(Frame(), Math.PI / 2,
            new[] { new Segment(reverse ? end : start, reverse ? start : end, "native/line") }, arrows);
        Assert.True(scope.HasProvenSegment);
        Assert.Null(scope.Reason);
        Assert.Equal(arrows, scope.Evidence);
        Assert.True(Direction(scope).IsResolved);
        var tracks = SectionTrafficTrackLogic.Resolve(Frame(), Math.PI / 2, 0, 7, scope.Evidence);
        Assert.True(tracks.IsResolved);
        Assert.Equal(2, tracks.Tracks.Count);
    }

    [Fact]
    public void R100CurveCounterexampleCannotAcquireAutomaticDirectionFromItsTangentProjection()
    {
        // Real geometry of a synthetic right-hand R100 curve: 30 m along its arc,
        // offset -3.5 is still the left lane, but projects to +1.122673 at the cut.
        const double angle = 0.3;
        var arrow = ArrowAt(100 * (1 - Math.Cos(angle)) - 3.5 * Math.Cos(angle),
            103.5 * Math.Sin(angle), Math.PI / 2 - angle + Math.PI);
        Assert.Equal(1.12267337549978, arrow.X, 10);
        Assert.Equal(30.5863413894486, arrow.Y, 10);
        var projectionOnly = new SectionTrafficStraightScopeLogic.Resolution(new[] { arrow }, "unproven-test", 0, null);
        Assert.True(Direction(projectionOnly).IsResolved); // Explicit pre-gate counterexample.

        // Curves are not converted into straight segments by the native adapter.
        var scope = SectionTrafficStraightScopeLogic.Resolve(Frame(), Math.PI / 2,
            Array.Empty<Segment>(), new[] { arrow });
        Assert.False(scope.HasProvenSegment);
        Assert.Equal("cut-not-contained-in-one-native-straight-segment", scope.Reason);
        Assert.Empty(scope.Evidence);
        Assert.False(Direction(scope).IsResolved);
        var tracks = SectionTrafficTrackLogic.Resolve(Frame(), Math.PI / 2, 0, 7, scope.Evidence);
        Assert.False(tracks.IsResolved);
        Assert.Empty(tracks.Tracks); // No made-up composite positions on a curve.

        var manual = new ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision
        {
            SourceDrawingHash = Hash, SourceHandle = "A", AlignmentName = "axis", LaneMidOffsetM = 1.75,
            Flow = SectionVehicleDirectionPlanner.AlongFlowToken, ApprovedBy = "Synthetic approver",
            ApprovedAtUtc = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc)
        };
        Assert.Equal("manual", Direction(scope, new[] { manual }).DirectionSource);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(10.000001)]
    [InlineData(30.5863413894486)]
    public void ArrowAtOrBeyondNativeLineEndCannotBeBorrowedFromFollowingGeometry(double y)
    {
        var scope = SectionTrafficStraightScopeLogic.Resolve(Frame(), Math.PI / 2,
            new[] { new Segment(new(0, -100), new(0, 10), "native/line") }, new[] { ArrowAt(1.75, y) });
        Assert.True(scope.HasProvenSegment);
        Assert.Empty(scope.Evidence);
        Assert.Equal(1, scope.RejectedNearCutCount);
        Assert.Equal("nearby-arrows-outside-proven-straight-segment", scope.Reason);
        Assert.False(Direction(scope).IsResolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CutAtSegmentBoundaryOrSkewedPastItDoesNotProveAStraightScope(bool skew)
    {
        var end = skew ? 5 : 0;
        var scope = SectionTrafficStraightScopeLogic.Resolve(Frame(skew), Math.PI / 2,
            new[] { new Segment(new(0, -100), new(0, end), "native/line") }, new[] { ArrowAt(1.75, -10) });
        Assert.False(scope.HasProvenSegment);
        Assert.Empty(scope.Evidence);
    }

    [Fact]
    public void MissingAmbiguousUnreadableOrOffAxisGeometryCannotBecomeProof()
    {
        var line = new Segment(new(0, -100), new(0, 100), "native/line");
        var arrows = new[] { ArrowAt(1.75, 12) };
        var ambiguous = SectionTrafficStraightScopeLogic.Resolve(Frame(), Math.PI / 2,
            new[] { line, line with { SourceKey = "native/other" } }, arrows);
        Assert.Equal("ambiguous-native-straight-segment", ambiguous.Reason);
        Assert.Empty(ambiguous.Evidence);
        var invalid = SectionTrafficStraightScopeLogic.Resolve(Frame(), Math.PI / 2,
            new[] { line with { Start = new(double.NaN, 0) } }, arrows);
        Assert.Equal("invalid-native-straight-segment-evidence", invalid.Reason);
        Assert.Empty(invalid.Evidence);
        var offAxis = SectionTrafficStraightScopeLogic.Resolve(Frame(), 0, new[] { line }, arrows);
        Assert.False(offAxis.HasProvenSegment);
        Assert.Empty(offAxis.Evidence);
        Assert.False(SectionTrafficStraightScopeLogic.Refuse("native-read-failed").HasProvenSegment);
    }
}
