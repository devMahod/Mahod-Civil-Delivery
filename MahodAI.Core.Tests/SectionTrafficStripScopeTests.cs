using System;
using System.Collections.Generic;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Arrow = MahodAI.CivilDelivery.Shared.TrafficDirectionEvidenceLogic.ArrowEvidence;
using Decision = MahodAI.CivilDelivery.Shared.ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision;

namespace MahodAI.Core.Tests;

public sealed class SectionTrafficStripScopeTests
{
    // Actual PLAN 89: sections-plan-20260924-165428-179c8a4d, cl-7C8F / STA-42676.
    // This is the selected right-carriageway arrow retained in the composite
    // source tracks, not a claim that the entire original extraction is here.
    private const string ClHash = "eae8f807734b04bba2497570ec27da431ac7b39f96456e3d50e2ac56b38f1574";
    private const double TangentDeg = 79.762554;
    private const double LeftFrom = -8.95256638464856, LeftTo = -2.570912445959134;
    private const double LeftMid = -5.761739415303847;
    private const string WrongLeftDigest = "696cb1d0d44096252e5914a3579f628f76d818ec5fa8b0424cbb4445ae9789e8";
    private static readonly Arrow RecordedRightArrow = new(
        203540.06426920343, 649507.0765636888, 1.2358765296369094,
        "6422-SM-MODEL-NATAZ|BL-TR-ARRW", "6422-SM-MODEL-NATAZ|813(814)",
        "6422-SM-MODEL-NATAZ", "BD91EF/1FFFD2");

    private static SectionCutFrame ActualFrame(bool reverse = false)
    {
        var a = new SectionProjectionLogic.P2(203507.8647477932, 649510.5467041765);
        var b = new SectionProjectionLogic.P2(203568.36383101201, 649496.987171379);
        Assert.True(SectionCutFrame.TryCreate(reverse ? b : a, reverse ? a : b,
            new(203538.1142894026, 649503.7669377777), TangentDeg, out var frame));
        return frame!;
    }

    private static SectionVehicleDirectionPlanner.DirectionPlan ResolveActual(
        IEnumerable<Arrow> arrows, IEnumerable<Decision>? decisions = null,
        double from = LeftFrom, double to = LeftTo, double offset = LeftMid,
        string? trackDigest = null, bool reverse = false)
    {
        var frame = ActualFrame(reverse);
        var target = frame.PointAt(offset);
        return SectionVehicleDirectionPlanner.Resolve(ClHash, "7C8F", "2000", offset,
            target.X, target.Y, TangentDeg * Math.PI / 180, arrows, decisions,
            laneFromOffsetM: from, laneToOffsetM: to, trackEvidenceDigest: trackDigest, laneCutFrame: frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualSta42676_WrongCarriagewayArrowCannotReproduceAcceptedLeftDirection(bool reverse)
    {
        var frame = ActualFrame(reverse);
        var target = frame.PointAt(LeftMid);
        var oldUnbounded = SectionVehicleDirectionPlanner.Resolve(ClHash, "7C8F", "2000", LeftMid,
            target.X, target.Y, TangentDeg * Math.PI / 180, new[] { RecordedRightArrow }, null);
        Assert.Equal(WrongLeftDigest, oldUnbounded.DirectionDigest);
        Assert.Equal(SectionFurnitureLogic.OfficeCarView.Rear, oldUnbounded.OfficeCarView);
        Assert.Equal(1.3318862919273018,
            frame.OffsetAtAlignmentProjection(new(RecordedRightArrow.X, RecordedRightArrow.Y)), 8);

        var scoped = ResolveActual(new[] { RecordedRightArrow }, reverse: reverse);
        Assert.False(scoped.IsResolved);
        Assert.Null(scoped.DirectionDigest);
        Assert.Null(scoped.OfficeCarView);
        Assert.Empty(scoped.ArrowResolution.Contenders);
    }

    [Fact]
    public void ActualRightCompositeTrackRetainsItsOwnDirectionAndDigest()
    {
        var result = ResolveActual(new[] { RecordedRightArrow }, from: -0.6444394570903852,
            to: 9.314069968312523, offset: 1.3318862919273018,
            trackDigest: "93b8cc9697838ca110eabed3733ebaebf6365bfcf4b897b16f9147e6172e2933");
        Assert.True(result.IsResolved);
        Assert.Equal(SectionFurnitureLogic.OfficeCarView.Rear, result.OfficeCarView);
        Assert.Equal("d4a7f265eb635a3790e097a2ae619aabeb8ec4ae3a5d85ae7fea816b2cf673eb", result.DirectionDigest);
    }

    [Fact]
    public void SyntheticEligibleArrowWinsEvenWhenOtherCarriagewayArrowIsCloser()
    {
        // Deliberately synthetic; this does not assert the real left lane's flow.
        var frame = ActualFrame();
        var target = frame.PointAt(LeftMid);
        var heading = TangentDeg * Math.PI / 180;
        var inside = new Arrow(target.X + 15 * Math.Cos(heading), target.Y + 15 * Math.Sin(heading),
            heading + Math.PI, "BL-TR-ARRW", "TR-ARW", "synthetic-only", "TEST-LEFT");
        var result = ResolveActual(new[] { RecordedRightArrow, inside });
        Assert.True(result.IsResolved);
        Assert.Equal("TEST-LEFT", result.ArrowResolution.Primary!.HandlePath);
        Assert.Equal(TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment, result.Flow);
        Assert.Equal(SectionFurnitureLogic.OfficeCarView.Front, result.OfficeCarView);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkewedCutKeepsParallelProjectedArrowInItsStripAndExcludesBoundary(bool reverse)
    {
        var a = new SectionProjectionLogic.P2(-10, -10);
        var b = new SectionProjectionLogic.P2(10, 10);
        Assert.True(SectionCutFrame.TryCreate(reverse ? b : a, reverse ? a : b, new(0, 0), 90, out var frame));
        var target = frame!.PointAt(2);
        var inside = new Arrow(target.X, target.Y + 15, Math.PI / 2, "BL-TR-ARRW", "TR-ARW", "synthetic", "IN");
        var boundary = frame.PointAt(4);
        var outside = inside with { X = boundary.X, Y = boundary.Y, HeadingRadians = -Math.PI / 2, HandlePath = "BOUNDARY" };
        var result = SectionVehicleDirectionPlanner.Resolve(ClHash, "7C8F", "2000", 2,
            target.X, target.Y, Math.PI / 2, new[] { outside, inside }, null,
            laneFromOffsetM: 0, laneToOffsetM: 4, laneCutFrame: frame);
        Assert.True(result.IsResolved);
        Assert.Equal("IN", result.ArrowResolution.Primary!.HandlePath);
        Assert.True(frame.OffsetOf(new(inside.X, inside.Y)) > 4, "plain dot projection would reject this valid arrow");
    }

    [Fact]
    public void ExactApprovedManualFallbackRemainsAvailableWithoutBorrowedArrow()
    {
        var manual = new Decision
        {
            SourceDrawingHash = ClHash, SourceHandle = "7C8F", AlignmentName = "2000", LaneMidOffsetM = LeftMid,
            Flow = SectionVehicleDirectionPlanner.AgainstFlowToken, ApprovedBy = "Synthetic regression approver",
            ApprovedAtUtc = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc)
        };
        var result = ResolveActual(new[] { RecordedRightArrow }, new[] { manual });
        Assert.True(result.IsResolved);
        Assert.Equal("manual", result.DirectionSource);
        Assert.Empty(result.ArrowResolution.Contenders);
        Assert.Same(manual, result.ManualDecision);
    }

    [Fact]
    public void BoundedResolutionCannotSilentlyOmitFrameOrUseAnotherTarget()
    {
        var frame = ActualFrame();
        var target = frame.PointAt(LeftMid);
        var missing = SectionVehicleDirectionPlanner.Resolve(ClHash, "7C8F", "2000", LeftMid,
            target.X, target.Y, TangentDeg * Math.PI / 180, new[] { RecordedRightArrow }, null,
            laneFromOffsetM: LeftFrom, laneToOffsetM: LeftTo);
        Assert.False(missing.IsResolved);
        Assert.Equal("invalid-lane-cut-scope", missing.Reason);
        var displaced = SectionVehicleDirectionPlanner.Resolve(ClHash, "7C8F", "2000", LeftMid,
            target.X + 1, target.Y, TangentDeg * Math.PI / 180, new[] { RecordedRightArrow }, null,
            laneFromOffsetM: LeftFrom, laneToOffsetM: LeftTo, laneCutFrame: frame);
        Assert.False(displaced.IsResolved);
        Assert.Equal("invalid-lane-cut-scope", displaced.Reason);
    }
}
