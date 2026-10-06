using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class TrafficDirectionEvidenceLogicTests
    {
        [Theory]
        [InlineData("BL-TR-ARRW", "*U70")]
        [InlineData("BL-TR-ARRW-2", "*U169")]
        [InlineData("TR-MARK-ARW-BL", "*U71")]
        [InlineData("TR-MARK-ARW-YLW-926", "*U81")]
        [InlineData("6422-SM|BL-TR-MARK-2", "anonymous")]
        [InlineData("6422-SM$0$TR-MARK-ARW-BL", "anonymous")]
        [InlineData("0", "TR-ARWXTX-XYX")]
        [InlineData("0", "TR-ARWLXX-WXX")]
        public void AuditedTrafficArrowLayersAndNames_AreApproved(
            string layer, string blockName)
        {
            TrafficDirectionEvidenceLogic.ClassifySource(layer, blockName).Should().Be(
                TrafficDirectionEvidenceLogic.SourceClass.ApprovedTrafficArrow);
        }

        [Theory]
        [InlineData("TR-MARK-ARW-BL", "arrow-D")]
        [InlineData("TR-MARK-ARW-BL", "arrow-D1")]
        [InlineData("HA-BIKE", "BL-ARW-W-Y")]
        [InlineData("TR-MARK-WHT-812-BIKE", "TR-ARWXTX-XYX")]
        [InlineData("PL-BIKE", "*U12")]
        [InlineData("0", "BL-ARW-W-Y")]
        public void BikeArrows_AreExcludedBeforeTrafficAllowlist(
            string layer, string blockName)
        {
            TrafficDirectionEvidenceLogic.ClassifySource(layer, blockName).Should().Be(
                TrafficDirectionEvidenceLogic.SourceClass.ExcludedBikeArrow);
        }

        [Theory]
        [InlineData("0", "ARROW")]
        [InlineData("UTIL-FLOW", "HW-ARRW-01")]
        [InlineData("C-ANNO", "NorthArrow")]
        [InlineData("TR-MARK-WHT-808", "*U70")]
        public void GenericArrowNames_AreNotDirectionEvidence(
            string layer, string blockName)
        {
            TrafficDirectionEvidenceLogic.ClassifySource(layer, blockName).Should().Be(
                TrafficDirectionEvidenceLogic.SourceClass.Unapproved);
        }

        [Fact]
        public void NoApprovedArrowInRange_ReturnsUnknown()
        {
            var evidence = new[]
            {
                Arrow(2, 0, 0, "HA-BIKE", "BL-ARW-W-Y"),
                Arrow(3, 0, 0, "0", "ARROW"),
                Arrow(100, 0, 0, "BL-TR-ARRW", "*U70")
            };

            var result = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);

            result.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Unknown);
            result.HeadingRadians.Should().BeNull();
            result.Reason.Should().Be("no-approved-arrow-in-range");
            result.ExcludedBikeCount.Should().Be(1);
            result.UnapprovedCount.Should().Be(1);
        }

        [Fact]
        public void NearestApprovedArrow_ResolvesItsDirectedHeading()
        {
            var evidence = new[]
            {
                Arrow(8, 0, -0.1, "BL-TR-ARRW", "*U70", "A"),
                Arrow(25, 0, Math.PI, "TR-MARK-ARW-BL", "TR-ARWXXR-XXW", "B")
            };

            var result = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);

            result.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Resolved);
            result.Primary!.HandlePath.Should().Be("A");
            result.HeadingRadians.Should().BeApproximately(Math.PI * 2 - 0.1, 1e-9);
            result.Contenders.Should().ContainSingle();
            result.ApprovedWithinRadius.Should().Be(2);
        }

        [Fact]
        public void NearbyArrowsAcrossZero_AverageCircularly()
        {
            var evidence = new[]
            {
                Arrow(5, 0, Degrees(355), "BL-TR-ARRW", "*U70", "A"),
                Arrow(6, 0, Degrees(5), "BL-TR-ARRW", "*U71", "B")
            };

            var result = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);

            result.IsResolved.Should().BeTrue();
            Math.Min(result.HeadingRadians!.Value,
                Math.PI * 2 - result.HeadingRadians.Value).Should().BeLessThan(1e-9);
            result.Contenders.Should().HaveCount(2);
        }

        [Fact]
        public void EquallyNearOpposingArrows_ReturnAmbiguous()
        {
            var evidence = new[]
            {
                Arrow(5, 0, 0, "BL-TR-ARRW", "*U70", "A"),
                Arrow(5.5, 0, Math.PI, "BL-TR-ARRW", "*U71", "B")
            };

            var result = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);

            result.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous);
            result.HeadingRadians.Should().BeNull();
            result.Reason.Should().Be("nearest-arrows-conflict");
            result.Contenders.Should().HaveCount(2);
        }

        [Fact]
        public void FartherOpposingArrow_DoesNotOverrideNearestLaneEvidence()
        {
            var evidence = new[]
            {
                Arrow(4, 0, 0, "BL-TR-ARRW", "*U70", "A"),
                Arrow(12, 0, Math.PI, "BL-TR-ARRW", "*U71", "B")
            };

            var result = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);

            result.IsResolved.Should().BeTrue();
            result.Primary!.HandlePath.Should().Be("A");
            result.Contenders.Should().ContainSingle();
        }

        [Fact]
        public void SideRoadArrow_IsNotEvidenceForAlignmentAxis()
        {
            var evidence = new[]
            {
                Arrow(3, 0, Math.PI / 2, "BL-TR-ARRW", "*U70")
            };

            var result = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);

            result.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Unknown);
        }

        [Fact]
        public void BicycleResolver_AcceptsOnlyBikeArrows_WithoutOrientingMotorTraffic()
        {
            var evidence = new[]
            {
                Arrow(2, 0, Math.PI, "HA-BIKE", "BL-ARW-W-Y", "BIKE"),
                Arrow(3, 0, 0, "BL-TR-ARRW", "TR-ARW", "CAR"),
            };

            var motor = TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence);
            var bicycle = TrafficDirectionEvidenceLogic.ResolveNearestBike(0, 0, 0, evidence);

            motor.IsResolved.Should().BeTrue();
            motor.Primary!.HandlePath.Should().Be("CAR");
            bicycle.IsResolved.Should().BeTrue();
            bicycle.Primary!.HandlePath.Should().Be("BIKE");
            bicycle.HeadingRadians.Should().BeApproximately(Math.PI, 1e-9);
        }

        [Fact]
        public void ConflictingBikeArrows_AreAmbiguousForBikeStripOnly()
        {
            var evidence = new[]
            {
                Arrow(2, 0, 0, "HA-BIKE", "BL-ARW-W-Y", "A"),
                Arrow(2.5, 0, Math.PI, "TR-MARK-ARW-BL", "arrow-D", "B"),
            };

            var bicycle = TrafficDirectionEvidenceLogic.ResolveNearestBike(0, 0, 0, evidence);

            bicycle.State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Ambiguous);
            bicycle.Reason.Should().Be("nearest-bike-arrows-conflict");
            TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence)
                .State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Unknown);
        }

        [Fact]
        public void InvalidCoordinatesOrHeadings_NeverResolve()
        {
            var evidence = new List<TrafficDirectionEvidenceLogic.ArrowEvidence>
            {
                Arrow(double.NaN, 0, 0, "BL-TR-ARRW", "*U70"),
                Arrow(0, 0, double.PositiveInfinity, "BL-TR-ARRW", "*U71")
            };

            TrafficDirectionEvidenceLogic.ResolveNearest(0, 0, 0, evidence)
                .State.Should().Be(TrafficDirectionEvidenceLogic.ResolutionState.Unknown);
            TrafficDirectionEvidenceLogic.ResolveNearest(double.NaN, 0, 0, evidence)
                .Reason.Should().Be("invalid-query");
        }

        [Theory]
        [InlineData(0, 0, TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment)]
        [InlineData(180, 0, TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment)]
        [InlineData(90, 0, TrafficDirectionEvidenceLogic.RelativeFlow.Unknown)]
        [InlineData(355, 5, TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment)]
        public void RelativeFlow_IsDirectedButAxisGated(
            double arrowDegrees,
            double axisDegrees,
            TrafficDirectionEvidenceLogic.RelativeFlow expected)
        {
            TrafficDirectionEvidenceLogic.RelativeToAlignment(
                    Degrees(arrowDegrees), Degrees(axisDegrees))
                .Should().Be(expected);
        }

        private static TrafficDirectionEvidenceLogic.ArrowEvidence Arrow(
            double x,
            double y,
            double heading,
            string layer,
            string block,
            string handle = "1") =>
            new(x, y, heading, layer, block, "SM.dwg", handle);

        private static double Degrees(double value) => value * Math.PI / 180.0;
    }
}
