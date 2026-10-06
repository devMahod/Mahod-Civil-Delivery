using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public sealed class SectionAnnotationPlacementLogicTests
    {
        [Fact]
        public void LiveRotationAndOffsetEvidence_AreComparedTheWayAutoCadPersistsThem()
        {
            // 07/09 18:20, first complete live VERIFY (sections-verify-20260907-152012):
            // bottom labels written with -π/2 read back Rotation=4.71238898038469.
            SectionAnnotationPlacementLogic.RotationsEquivalent(4.71238898038469, -Math.PI / 2.0, 0.0000001)
                .Should().BeTrue();
            SectionAnnotationPlacementLogic.RotationsEquivalent(Math.PI, -Math.PI, 0.0000001).Should().BeTrue();
            SectionAnnotationPlacementLogic.RotationsEquivalent(0.0, 0.0, 0.0000001).Should().BeTrue();
            SectionAnnotationPlacementLogic.RotationsEquivalent(0.0, Math.PI / 2.0, 0.0000001).Should().BeFalse();
            SectionAnnotationPlacementLogic.RotationsEquivalent(0.001, 0.0, 0.0000001).Should().BeFalse();
            SectionAnnotationPlacementLogic.RotationsEquivalent(double.NaN, 0.0, 0.0000001).Should().BeFalse();

            // The same run: the bike arrow sits at the exact PLAN lane midpoint while its
            // evidence string says "offset=-9.779" — 0.15 mm away, outside 0.01 mm.
            var planned = new[] { -9.779147498985779, -6.244147498985779, 3.831980594237266 };
            SectionAnnotationPlacementLogic.ExactPlannedOffset(planned, -9.779)
                .Should().Be(-9.779147498985779);
            SectionAnnotationPlacementLogic.ExactPlannedOffset(planned, 3.832)
                .Should().Be(3.831980594237266);
            SectionAnnotationPlacementLogic.ExactPlannedOffset(planned, -9.780).Should().BeNull();
            SectionAnnotationPlacementLogic.ExactPlannedOffset(new[] { 1.0002, 1.0004 }, 1.0)
                .Should().BeNull("two PLAN rows within tolerance are ambiguous");
            SectionAnnotationPlacementLogic.ExactPlannedOffset(planned, double.NaN).Should().BeNull();
        }

        [Fact]
        public void CarPlacement_PinsCenterAndBottomToMappedDesignGroundEnvelope()
        {
            var source = new SectionAnnotationPlacementLogic.Bounds(-1, -0.5, 1, 1);

            SectionAnnotationPlacementLogic.TryCarPlacement(
                    source,
                    physicalWidth: 2,
                    new SectionAnnotationPlacementLogic.Point(10, 20),
                    new SectionAnnotationPlacementLogic.Point(14, 20),
                    new SectionAnnotationPlacementLogic.Point(12, 20),
                    new SectionAnnotationPlacementLogic.Point(12, 23),
                    out var placement,
                    out var error)
                .Should().BeTrue(error);

            placement.Should().NotBeNull();
            placement!.PositionX.Should().Be(12);
            placement.PositionY.Should().Be(21);
            placement.ScaleX.Should().Be(2);
            placement.ScaleY.Should().Be(2);
            (placement.PositionY + source.MinY * placement.ScaleY)
                .Should().Be(20, "the block bottom must stand on sampled design ground");
            (placement.PositionX + (source.MinX + source.MaxX) / 2 * placement.ScaleX)
                .Should().Be(12, "the block center must remain at the lane midpoint");
        }

        [Theory]
        [InlineData(true, 20, 0)]
        [InlineData(false, 26, Math.PI)]
        public void ArrowPlacement_PinsExactHeadroomAndDirection(
            bool pointsUp, double expectedY, double expectedRotation)
        {
            var source = new SectionAnnotationPlacementLogic.Bounds(-0.5, 0, 0.5, 3);

            SectionAnnotationPlacementLogic.TryArrowPlacement(
                    source,
                    new SectionAnnotationPlacementLogic.Point(10, 20),
                    new SectionAnnotationPlacementLogic.Point(10, 26),
                    pointsUp,
                    out var placement,
                    out var error)
                .Should().BeTrue(error);

            placement!.PositionX.Should().Be(10);
            placement.PositionY.Should().Be(expectedY);
            placement.ScaleX.Should().Be(2);
            placement.ScaleY.Should().Be(2);
            placement.Rotation.Should().Be(expectedRotation);
        }

        [Fact]
        public void DatumSlopeAndDimension_UsePinnedMappedAnchors()
        {
            SectionAnnotationPlacementLogic.DatumPosition(
                    new SectionAnnotationPlacementLogic.Point(4, 10))
                .Should().Be(new SectionAnnotationPlacementLogic.Point(4, 8.4));
            SectionAnnotationPlacementLogic.SlopePosition(
                    new SectionAnnotationPlacementLogic.Point(7, 15))
                .Should().Be(new SectionAnnotationPlacementLogic.Point(7, 15.7));
            SectionAnnotationPlacementLogic.DimensionPosition(
                    new SectionAnnotationPlacementLogic.Point(9, 5))
                .Should().Be(new SectionAnnotationPlacementLogic.Point(9.35, 3.9));
        }

        [Fact]
        public void CorePresentation_UsesPinnedNatalyOffsets_NotCircularArtifactValues()
        {
            var bottom = new SectionAnnotationPlacementLogic.Point(100, 200);
            var top = new SectionAnnotationPlacementLogic.Point(100, 260);

            SectionAnnotationPlacementLogic.AxisLine(bottom, top)
                .Should().Be(new SectionAnnotationPlacementLogic.Segment(bottom, top));
            SectionAnnotationPlacementLogic.AxisLabelPosition(top)
                .Should().Be(new SectionAnnotationPlacementLogic.Point(100, 264.6));
            SectionAnnotationPlacementLogic.TitlePosition(top)
                .Should().Be(new SectionAnnotationPlacementLogic.Point(100, 266.6));
            SectionAnnotationPlacementLogic.RowLine(bottom, top)
                .Should().Be(new SectionAnnotationPlacementLogic.Segment(bottom, top));
            SectionAnnotationPlacementLogic.RowLabelPosition(top)
                .Should().Be(new SectionAnnotationPlacementLogic.Point(100, 260.6));
            SectionAnnotationPlacementLogic.TopTick(top)
                .Should().Be(new SectionAnnotationPlacementLogic.Segment(
                    new SectionAnnotationPlacementLogic.Point(100, 258), top));
            SectionAnnotationPlacementLogic.BottomTick(bottom)
                .Should().Be(new SectionAnnotationPlacementLogic.Segment(
                    new SectionAnnotationPlacementLogic.Point(100, 199.3), bottom));
            SectionAnnotationPlacementLogic.WidthLabelPosition(top)
                .Should().Be(new SectionAnnotationPlacementLogic.Point(100, 261.3));
            SectionAnnotationPlacementLogic.StripLabelPosition(top)
                .Should().Be(new SectionAnnotationPlacementLogic.Point(100, 262.9));
        }

        [Fact]
        public void DimensionTick_WrongLiveColorFailsEvenWhenGeometryIsExact()
        {
            SectionAnnotationPlacementLogic.PresentationColorMatches(5, 5).Should().BeTrue();
            SectionAnnotationPlacementLogic.PresentationColorMatches(5, 1).Should().BeFalse();
            SectionAnnotationPlacementLogic.PresentationColorMatches(0, 0).Should().BeFalse();
        }

        [Fact]
        public void NonOrthogonalOrNonFiniteMapping_FailsClosed()
        {
            var source = new SectionAnnotationPlacementLogic.Bounds(-1, 0, 1, 1);
            SectionAnnotationPlacementLogic.TryCarPlacement(
                    source, 2,
                    new SectionAnnotationPlacementLogic.Point(0, 0),
                    new SectionAnnotationPlacementLogic.Point(10, 1),
                    new SectionAnnotationPlacementLogic.Point(5, 0),
                    new SectionAnnotationPlacementLogic.Point(5, 2),
                    out _, out _)
                .Should().BeFalse();
            SectionAnnotationPlacementLogic.TryArrowPlacement(
                    source,
                    new SectionAnnotationPlacementLogic.Point(double.NaN, 0),
                    new SectionAnnotationPlacementLogic.Point(0, 2),
                    true, out _, out _)
                .Should().BeFalse();
        }
    }
}
