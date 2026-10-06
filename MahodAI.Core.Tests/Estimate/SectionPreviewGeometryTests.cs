using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionPreviewGeometry;

namespace MahodAI.Core.Tests.Estimate
{
    public class SectionPreviewGeometryTests
    {
        [Fact]
        public void SamplingPath_UsesCivilSignedOffsets_IncludesAxis_AndIsBounded()
        {
            // Alignment points north.  East is Civil-right (+), west is left (-),
            // regardless of the fact that this CL was drawn from east to west.
            var path = BuildSamplingPath(
                new WcsPoint(30, 100), new WcsPoint(-20, 100),
                new WcsPoint(0, 100), alignmentTangentDeg: 90,
                maximumSpacingM: 0.25, maximumSamples: 21);

            path.Should().HaveCount(21);
            path.First().Offset.Should().BeApproximately(-20, 1e-9);
            path.Last().Offset.Should().BeApproximately(30, 1e-9);
            path.Should().Contain(p => p.Offset == 0 && p.X == 0 && p.Y == 100);
        }

        [Fact]
        public void SamplingPath_FailsClosed_WhenClDoesNotStraddleAlignment()
        {
            BuildSamplingPath(
                    new WcsPoint(5, 0), new WcsPoint(15, 0),
                    new WcsPoint(0, 0), alignmentTangentDeg: 90)
                .Should().BeEmpty();
        }

        [Fact]
        public void ElevationWindow_RequiresTwoRealPointsFromEveryRequiredSurface()
        {
            var eg = new List<SurfacePoint>
            {
                new(-10, 234.1), new(0, 234.3), new(10, 234.0),
            };
            var designMissing = new List<SurfacePoint> { new(0, 234.2) };

            TryCreateElevationWindow(new[] { eg, designMissing }, out var window)
                .Should().BeFalse();
            window.Should().BeNull();
        }

        [Fact]
        public void ElevationWindow_UsesAnInternalPlotFloorAndMapsActualElevation()
        {
            var eg = new List<SurfacePoint> { new(-10, 233.7), new(10, 234.2) };
            var design = new List<SurfacePoint> { new(-5, 234.0), new(5, 234.5) };

            TryCreateElevationWindow(new[] { eg, design }, out var window).Should().BeTrue();
            window.Should().NotBeNull();
            window!.Datum.Should().BeLessThan(233.7);
            window.Top.Should().BeGreaterThan(234.5);

            var mapped = Map(1000, 2000, new SurfacePoint(5, 234.5), window);
            mapped.X.Should().Be(1005);
            mapped.Y.Should().BeApproximately(
                2002 + (234.5 - window.Datum) * window.VerticalScale, 1e-9);
        }

        [Fact]
        public void ExistingGroundAtAxis_IsExactOrInterpolatedInsideOneRealChain()
        {
            TryElevationAtOffset(new[]
            {
                new SurfacePoint(-2, 233.8),
                new SurfacePoint(2, 234.2),
            }, 0, out var interpolated).Should().BeTrue();
            interpolated.Should().BeApproximately(234.0, 1e-9);

            TryElevationAtOffset(new[]
            {
                new SurfacePoint(-2, 233.8),
                new SurfacePoint(0, 234.17),
                new SurfacePoint(2, 234.2),
            }, 0, out var exact).Should().BeTrue();
            exact.Should().BeApproximately(234.17, 1e-9);
        }

        [Fact]
        public void ExistingGroundAtAxis_NeverExtrapolatesPastMeasuredSurface()
        {
            TryElevationAtOffset(new[]
            {
                new SurfacePoint(2, 234.1),
                new SurfacePoint(4, 234.3),
            }, 0, out _).Should().BeFalse();
        }

        [Fact]
        public void SurfaceSegment_RejectsNonFiniteOrSingleOffsetData()
        {
            HasRenderableSegment(new[]
            {
                new SurfacePoint(0, 234), new SurfacePoint(0, 235),
            }).Should().BeFalse();

            HasRenderableSegment(new[]
            {
                new SurfacePoint(-1, 234), new SurfacePoint(1, double.NaN),
            }).Should().BeFalse();
        }
    }
}
