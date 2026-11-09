using System;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public class SpatialQuantityClipTests
{
    private static SpatialQuantityClip Box() => new(new[]{(0d,0d),(10d,0d),(10d,10d),(0d,10d)});
    [Fact] public void LongGasLineCountsOnlyTenMetresInside()
        => Assert.Equal(10,Box().SegmentLength((-100d,5d,0d),(100d,5d,0d)),8);
    [Fact] public void OutsideLineIsNotCounted()
        => Assert.Equal(0,Box().SegmentLength((-100d,20d,0d),(100d,20d,0d)));
    [Fact] public void VerticalLineUsesItsThreeDimensionalLength()
        => Assert.Equal(12,Box().SegmentLength((5d,5d,0d),(5d,5d,12d)));
    [Fact] public void SlopedLinePreservesThreeDimensionalLength()
        => Assert.Equal(Math.Sqrt(200),Box().SegmentLength((-10d,5d,-10d),(20d,5d,20d)),8);
    [Fact] public void AreaIsIntersectedNotScaledByBoundingBox()
        => Assert.Equal(50,Box().Area(new[]{(-10d,0d),(5d,0d),(5d,10d),(-10d,10d)}),8);
    [Fact] public void BlockInsertionOnBoundaryIsIncluded()
    { Assert.True(Box().Contains(0,5)); Assert.False(Box().Contains(-0.1,5)); }
    [Fact] public void SelfIntersectingBoundaryIsRejected()
        => Assert.Throws<ArgumentException>(()=>new SpatialQuantityClip(new[]{(0d,0d),(10d,10d),(0d,10d),(10d,0d)}));
    [Fact] public void DegenerateBoundaryIsRejected()
        => Assert.Throws<ArgumentException>(()=>new SpatialQuantityClip(new[]{(0d,0d),(1d,0d),(2d,0d)}));
    [Fact] public void ConcaveBoundaryExcludesTheGap()
    {
        var clip=new SpatialQuantityClip(new[]{(0d,0d),(10d,0d),(10d,10d),(7d,10d),(7d,3d),(3d,3d),(3d,10d),(0d,10d)});
        Assert.Equal(6,clip.SegmentLength((-5d,5d,0d),(15d,5d,0d)),8);
        Assert.False(clip.Contains(5,5));
    }
    [Fact] public void TouchingAtOnePointDoesNotCreateLength()
        => Assert.Equal(0,Box().SegmentLength((-5d,5d,0d),(5d,-5d,0d)),8);
    // ── marked-area bill of quantities (2026-11-09) ──
    [Fact] public void BoxesAreInsideOutsideOrCrossing()
    {
        Assert.Equal(SpatialQuantityClip.Relation.Inside, Box().Classify(1, 1, 9, 9));
        Assert.Equal(SpatialQuantityClip.Relation.Outside, Box().Classify(11, 11, 20, 20));
        Assert.Equal(SpatialQuantityClip.Relation.Crossing, Box().Classify(5, 5, 15, 15));
        // A concave area: the box sits in the notch, between the arms — outside, although the envelopes overlap.
        var u = new SpatialQuantityClip(new[]{(0d,0d),(10d,0d),(10d,10d),(7d,10d),(7d,3d),(3d,3d),(3d,10d),(0d,10d)});
        Assert.Equal(SpatialQuantityClip.Relation.Outside, u.Classify(4, 4, 6, 9));
    }
    [Fact] public void PathCrossingTwiceGivesOnePieceWithInterpolatedElevation()
    {
        var pieces = Box().ClipPath(new[] { (-10d, 5d, 0d), (20d, 5d, 30d) });
        var piece = Assert.Single(pieces);
        Assert.Equal((0d, 5d, 10d), piece[0]);
        Assert.Equal((10d, 5d, 20d), piece[^1]);
        Assert.Equal(Math.Sqrt(200), SpatialQuantityClip.Length3d(piece), 9);
    }
    [Fact] public void PathLeavingAndReturningGivesTwoPieces()
    {
        var u = new SpatialQuantityClip(new[]{(0d,0d),(10d,0d),(10d,10d),(7d,10d),(7d,3d),(3d,3d),(3d,10d),(0d,10d)});
        var pieces = u.ClipPath(new[] { (-5d, 5d, 0d), (15d, 5d, 0d) });
        Assert.Equal(2, pieces.Count);
        Assert.Equal(6, pieces.Sum(SpatialQuantityClip.Length3d), 9);
    }
    [Fact] public void PolylinePiecesStayJoinedAcrossInsideVertices()
    {
        // In at x=0, a bend inside, out at y=10: one continuous piece, not one per segment.
        var piece = Assert.Single(Box().ClipPath(new[] { (-5d, 2d, 0d), (5d, 2d, 0d), (5d, 15d, 0d) }));
        Assert.Equal(3, piece.Count);
        Assert.Equal(5 + 8, SpatialQuantityClip.Length3d(piece), 9);
    }
    [Fact] public void PathOutsideGivesNothingAndInsideStaysWhole()
    {
        Assert.Empty(Box().ClipPath(new[] { (-5d, 20d, 0d), (15d, 20d, 0d) }));
        var piece = Assert.Single(Box().ClipPath(new[] { (1d, 1d, 0d), (9d, 1d, 0d), (9d, 9d, 0d) }));
        Assert.Equal(16, SpatialQuantityClip.Length3d(piece), 9);
    }
    [Fact] public void RingAreaIsCutAndSelfTouchingRingsAreRepaired()
    {
        Assert.Equal(25, Box().ClipRing(new[] { (5d, 5d), (15d, 5d), (15d, 15d), (5d, 15d) }).Area, 9);
        Assert.Equal(0, Box().ClipRing(new[] { (20d, 20d), (30d, 20d), (30d, 30d) }).Area, 9);
    }
    [Fact] public void ShapeIsCutToTheArea()
    {
        var factory = new NetTopologySuite.Geometries.GeometryFactory();
        var shape = factory.CreatePolygon(new[] { new NetTopologySuite.Geometries.Coordinate(-5, -5), new(5, -5), new(5, 5), new(-5, 5), new(-5, -5) });
        Assert.Equal(25, Box().ClipShape(shape).Area, 9);
    }
    [Fact] public void LargeCivilCoordinatesKeepTheClippedLength()
    {
        var clip=new SpatialQuantityClip(new[]{(200000d,600000d),(200010d,600000d),(200010d,600010d),(200000d,600010d)});
        Assert.Equal(10,clip.SegmentLength((199000d,600005d,0d),(201000d,600005d,0d)),8);
    }
}
