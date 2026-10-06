using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionMathTests
    {
        [Fact]
        public void SegmentIntersection_CrossingSegments_FindsPoint()
        {
            SectionMath.TrySegmentIntersection(
                new Pt2(0, -10), new Pt2(0, 10),
                new Pt2(-10, 0), new Pt2(10, 0),
                out var p, out var t, out var u).Should().BeTrue();
            p.X.Should().BeApproximately(0, 1e-9);
            p.Y.Should().BeApproximately(0, 1e-9);
            t.Should().BeApproximately(0.5, 1e-9);
            u.Should().BeApproximately(0.5, 1e-9);
        }

        [Fact]
        public void SegmentIntersection_ParallelSegments_NoUniquePoint()
        {
            SectionMath.TrySegmentIntersection(
                new Pt2(0, 0), new Pt2(10, 0),
                new Pt2(0, 1), new Pt2(10, 1),
                out _, out _, out _).Should().BeFalse();
        }

        [Fact]
        public void SegmentIntersection_CrossingOutsideSegmentBounds_NotReported()
        {
            // Lines cross at (0,0) but segment CD lives far to the right.
            SectionMath.TrySegmentIntersection(
                new Pt2(0, -10), new Pt2(0, 10),
                new Pt2(5, 0), new Pt2(15, 0),
                out _, out _, out _).Should().BeFalse();
        }

        [Fact]
        public void SegmentPolylineIntersections_DoubleCrossing_ReportsBoth()
        {
            // U-shaped polyline crossed twice by one vertical segment.
            var poly = new List<Pt2> { new(-10, 5), new(10, 5), new(10, -5), new(-10, -5) };
            var hits = SectionMath.SegmentPolylineIntersections(new Pt2(0, 10), new Pt2(0, -10), poly);
            hits.Should().HaveCount(2);
        }

        [Theory]
        [InlineData(0.0, 90.0, 0.0)]      // alignment east, CL exactly north (normal) => no skew
        [InlineData(0.0, 120.0, 30.0)]    // CL rotated 30° CCW from normal
        [InlineData(0.0, 60.0, -30.0)]    // CL rotated 30° CW from normal
        [InlineData(0.0, 270.0, 0.0)]     // CL drawn the opposite way is the same section
        [InlineData(45.0, 135.0, 0.0)]    // diagonal alignment, perpendicular CL
        public void SkewFromNormal_MatchesConvention(double tangent, double clDir, double expected)
        {
            SectionMath.SkewFromNormalDeg(clDir, tangent).Should().BeApproximately(expected, 1e-9);
        }

        [Fact]
        public void SignedOffset_RightOfIncreasingStation_IsPositive()
        {
            // Alignment heading east (tangent 0°): south of it is the right side.
            var crossing = new Pt2(0, 0);
            SectionMath.SignedOffset(new Pt2(0, -7), crossing, 0).Should().BeApproximately(7, 1e-9);
            SectionMath.SignedOffset(new Pt2(0, 4), crossing, 0).Should().BeApproximately(-4, 1e-9);
        }

        [Fact]
        public void ExtentsFromEndpoints_EncodeSwathFromClGeometry()
        {
            // CL from 12m left to 18m right of an east-heading alignment.
            var (oa, ob, left, right) = SectionMath.ExtentsFromEndpoints(
                new Pt2(0, 12), new Pt2(0, -18), new Pt2(0, 0), 0);
            oa.Should().BeApproximately(-12, 1e-9);
            ob.Should().BeApproximately(18, 1e-9);
            left.Should().BeApproximately(12, 1e-9);
            right.Should().BeApproximately(18, 1e-9);
        }

        [Fact]
        public void ExtentsFromEndpoints_SkewedCl_UsesPerpendicularOffsets()
        {
            // 45°-skewed CL still reports perpendicular (offset) extents, not CL length.
            var (_, _, left, right) = SectionMath.ExtentsFromEndpoints(
                new Pt2(-10, 10), new Pt2(10, -10), new Pt2(0, 0), 0);
            left.Should().BeApproximately(10, 1e-9);
            right.Should().BeApproximately(10, 1e-9);
        }

        [Fact]
        public void DistancePointToSegment_HandlesInteriorAndEndpoints()
        {
            SectionMath.DistancePointToSegment(new Pt2(5, 3), new Pt2(0, 0), new Pt2(10, 0))
                .Should().BeApproximately(3, 1e-9);
            SectionMath.DistancePointToSegment(new Pt2(-4, 0), new Pt2(0, 0), new Pt2(10, 0))
                .Should().BeApproximately(4, 1e-9);
        }

        [Fact]
        public void Affine3_AppliesTranslationAndRotation()
        {
            // 90° CCW rotation + translate (100, 50).
            var m = new Affine3(new double[]
            {
                0, -1, 0, 100,
                1,  0, 0, 50,
                0,  0, 1, 0,
            });
            var p = m.Apply(new Pt2(10, 0));
            p.X.Should().BeApproximately(100, 1e-9);
            p.Y.Should().BeApproximately(60, 1e-9);
            m.IsDegenerate().Should().BeFalse();
        }

        [Fact]
        public void Affine3_SingularTransform_IsDegenerate()
        {
            var m = new Affine3(new double[]
            {
                1, 0, 0, 0,
                2, 0, 0, 0,   // collapses Y
                0, 0, 1, 0,
            });
            m.IsDegenerate().Should().BeTrue();
        }
    }
}
