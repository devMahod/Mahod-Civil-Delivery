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
    [Fact] public void LargeCivilCoordinatesKeepTheClippedLength()
    {
        var clip=new SpatialQuantityClip(new[]{(200000d,600000d),(200010d,600000d),(200010d,600010d),(200000d,600010d)});
        Assert.Equal(10,clip.SegmentLength((199000d,600005d,0d),(201000d,600005d,0d)),8);
    }
}
