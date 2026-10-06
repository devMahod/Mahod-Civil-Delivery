using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic;

namespace MahodAI.Core.Tests.Estimate
{
    public class CorridorQuantityLogicTests
    {
        // ---------------------------------------------------- average end area

        [Fact]
        public void AverageEndArea_IsTheTextbookFormula()
        {
            // A(0)=2, A(10)=4 → V = (2+4)/2 × 10 = 30 m³ — checkable by hand.
            var v = AverageEndArea(new[]
            {
                new ShapeSample(0, "Pave1", 2.0),
                new ShapeSample(10, "Pave1", 4.0),
            });
            v.Should().ContainSingle();
            v[0].VolumeM3.Should().BeApproximately(30.0, 1e-9);
            v[0].StationFrom.Should().Be(0);
            v[0].StationTo.Should().Be(10);
            v[0].StationCount.Should().Be(2);
        }

        [Fact]
        public void SameCodeShapesAtOneStationSumFirst()
        {
            // Left and right travel lanes: two Pave1 shapes per station.
            var v = AverageEndArea(new[]
            {
                new ShapeSample(0, "Pave1", 1.0), new ShapeSample(0, "Pave1", 1.0),
                new ShapeSample(20, "Pave1", 1.0), new ShapeSample(20, "Pave1", 1.0),
            });
            v[0].VolumeM3.Should().BeApproximately(40.0, 1e-9);
        }

        [Fact]
        public void CodesIntegrateIndependently()
        {
            var v = AverageEndArea(new[]
            {
                new ShapeSample(0, "Pave1", 2.0), new ShapeSample(10, "Pave1", 2.0),
                new ShapeSample(0, "Base", 6.0), new ShapeSample(10, "Base", 6.0),
            });
            v.Should().HaveCount(2);
            v.Should().ContainSingle(m => m.Code == "Pave1" && System.Math.Abs(m.VolumeM3 - 20.0) < 1e-9);
            v.Should().ContainSingle(m => m.Code == "Base" && System.Math.Abs(m.VolumeM3 - 60.0) < 1e-9);
        }

        [Fact]
        public void WideGapsSplitTheRunInsteadOfBridgingIt()
        {
            // Material stops at st=100 and resumes at st=400 (a bridge). Bridging
            // 300 m of no-material would invent ~600 m³ of asphalt.
            var v = AverageEndArea(new[]
            {
                new ShapeSample(0, "Pave1", 2.0), new ShapeSample(100, "Pave1", 2.0),
                new ShapeSample(400, "Pave1", 2.0), new ShapeSample(500, "Pave1", 2.0),
            }, maxGapM: 150.0);
            v.Should().HaveCount(2);
            v[0].VolumeM3.Should().BeApproximately(200.0, 1e-9);
            v[1].VolumeM3.Should().BeApproximately(200.0, 1e-9);
        }

        [Fact]
        public void SingleStationRunsProduceNoVolume() =>
            AverageEndArea(new[] { new ShapeSample(0, "Pave1", 2.0) }).Should().BeEmpty();

        [Fact]
        public void EqualStationsOnDifferentBaselines_AreIntegratedIndependently()
        {
            var volumes = AverageEndArea(new[]
            {
                new ShapeSample(0, "Base", 2.0, "baseline-a"),
                new ShapeSample(10, "Base", 2.0, "baseline-a"),
                new ShapeSample(0, "Base", 3.0, "baseline-b"),
                new ShapeSample(10, "Base", 3.0, "baseline-b"),
            });

            volumes.Should().HaveCount(2);
            volumes.Should().ContainSingle(v =>
                v.SeriesId == "baseline-a" && System.Math.Abs(v.VolumeM3 - 20.0) < 1e-9);
            volumes.Should().ContainSingle(v =>
                v.SeriesId == "baseline-b" && System.Math.Abs(v.VolumeM3 - 30.0) < 1e-9);
        }

        [Theory]
        [InlineData(EstimatePreflightPolicy.CorridorMaterialReadFailedCode)]
        [InlineData(EstimatePreflightPolicy.EarthworksQtoFailedCode)]
        public void ReadFailures_BecomeOneGlobalExportBlocker(string blockerCode)
        {
            var finding = BlockingReadFinding(new[]
            {
                new QuantityReadFailure("shape-area", "BL1@10", "bad area"),
                new QuantityReadFailure("shape-area", "BL1@20", "bad area"),
                new QuantityReadFailure("baseline-stations", "BL2", "bad stations"),
            }, blockerCode, "6422", "partial");

            finding.Should().NotBeNull();
            finding!.Severity.Should().Be(FindingSeverity.Error);
            finding.AffectedRecordIds.Should().BeEmpty("the unread object has no safe quantity record id");
            finding.Message.Should().Contain("shape-area=2").And.Contain("baseline-stations=1");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Fact]
        public void EarthworksGroup_RequiresSectionsOwnershipForTheSameProfile()
        {
            var owned = new OwnershipMetadata
            {
                Feature = "sections",
                Role = "sample-line-group",
                ProjectProfileId = "6422",
                RunId = "run",
                LogicalKey = "key",
                InputFingerprint = "fp",
                CreatedByToolVersion = "test",
            };

            IsTrustedSectionGroup(owned, "6422").Should().BeTrue();
            IsTrustedSectionGroup(owned, "other-profile").Should().BeFalse();
            IsTrustedSectionGroup(null, "6422").Should().BeFalse();
            IsTrustedSectionGroup(new OwnershipMetadata
            {
                Feature = "sections",
                Role = "sample-line",
                ProjectProfileId = "6422",
                RunId = "run",
                LogicalKey = "key",
                InputFingerprint = "fp",
                CreatedByToolVersion = "test",
            }, "6422").Should().BeFalse();
        }

        [Fact]
        public void OwnedSectionPoints_AreAcceptedOnlyInsideTheConfiguredSwath()
        {
            var raw = new[]
            {
                (-31.005, 284.2, 0.0),
                (0.0, 285.0, 0.0),
                (31.005, 284.6, 0.0),
            };

            TryNormalizeOwnedSectionPoints(
                    raw, maxHalfWidthDrawingUnits: 31.0,
                    boundaryToleranceDrawingUnits: 0.01,
                    out var chain, out var failure)
                .Should().BeTrue(failure);
            chain.Should().Equal(
                (-31.005, 284.2),
                (0.0, 285.0),
                (31.005, 284.6));
        }

        [Fact]
        public void OwnedSectionPoints_RejectWorldCoordinatesWithoutClippingThem()
        {
            var raw = new[]
            {
                (200000.0, 640000.0, 284.2),
                (200010.0, 640010.0, 285.0),
                (200020.0, 640020.0, 284.6),
            };

            TryNormalizeOwnedSectionPoints(raw, 31.0, 0.01,
                    out var chain, out var failure)
                .Should().BeFalse();
            chain.Should().BeEmpty("a quantity chain may not silently drop outliers");
            failure.Should().Contain("outside").And.Contain("owned");
        }

        [Theory]
        [InlineData(double.NaN, 1.0, 0.0)]
        [InlineData(1.0, double.PositiveInfinity, 0.0)]
        [InlineData(1.0, 2.0, double.NegativeInfinity)]
        public void OwnedSectionPoints_RejectEveryNonFiniteCoordinate(
            double x, double y, double z)
        {
            var raw = new[] { (-1.0, 284.0, 0.0), (x, y, z) };

            TryNormalizeOwnedSectionPoints(raw, 31.0, 0.01,
                    out var chain, out var failure)
                .Should().BeFalse();
            chain.Should().BeEmpty();
            failure.Should().Contain("non-finite");
        }

        [Fact]
        public void OwnedSectionPoints_RequireARealOffsetSpan()
        {
            var raw = new[] { (0.0, 284.0, 0.0), (0.0, 285.0, 0.0) };

            TryNormalizeOwnedSectionPoints(raw, 31.0, 0.01,
                    out var chain, out var failure)
                .Should().BeFalse();
            chain.Should().BeEmpty();
            failure.Should().Contain("offset span");
        }

        // ------------------------------------------------------------- cut/fill

        [Fact]
        public void CutFill_PureCut()
        {
            // Existing flat at 10, design flat at 9, width 20 → 20 m² cut, no fill.
            var eg = new[] { (-10.0, 10.0), (10.0, 10.0) };
            var ds = new[] { (-10.0, 9.0), (10.0, 9.0) };
            var (cut, fill) = CutFill(eg, ds);
            cut.Should().BeApproximately(20.0, 1e-9);
            fill.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void CutFill_SplitsAtTheCrossing()
        {
            // Design tilts through the existing line: half cut, half fill,
            // each a triangle of area (10 × 1) / 2 = 5 m².
            var eg = new[] { (-10.0, 10.0), (10.0, 10.0) };
            var ds = new[] { (-10.0, 9.0), (10.0, 11.0) };
            var (cut, fill) = CutFill(eg, ds);
            cut.Should().BeApproximately(5.0, 1e-9);
            fill.Should().BeApproximately(5.0, 1e-9);
        }

        [Fact]
        public void CutFill_UsesOnlyTheOverlappingDomain()
        {
            // Design known only for the right half — the left half must not count.
            var eg = new[] { (-10.0, 10.0), (10.0, 10.0) };
            var ds = new[] { (0.0, 9.0), (10.0, 9.0) };
            var (cut, fill) = CutFill(eg, ds);
            cut.Should().BeApproximately(10.0, 1e-9);
            fill.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void CutFill_HonoursIntermediateVertices()
        {
            // Existing dips to a V at the axis; design is flat at the rim level:
            // fill = triangle 20 wide × 2 deep / 2 = 20 m².
            var eg = new[] { (-10.0, 10.0), (0.0, 8.0), (10.0, 10.0) };
            var ds = new[] { (-10.0, 10.0), (10.0, 10.0) };
            var (cut, fill) = CutFill(eg, ds);
            fill.Should().BeApproximately(20.0, 1e-9);
            cut.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void ElevationAt_InterpolatesAndRefusesOutside()
        {
            var chain = new[] { (0.0, 100.0), (10.0, 110.0) };
            ElevationAt(chain, 5.0).Should().BeApproximately(105.0, 1e-9);
            ElevationAt(chain, -1.0).Should().BeNull();
            ElevationAt(chain, 11.0).Should().BeNull();
        }

        // ------------------------------------------------------------ vehicles

        [Theory]
        [InlineData("נתיב נסיעה", "car")]
        [InlineData("נת\"צ", "bus")]
        [InlineData("שביל אופניים", "bike")]
        [InlineData("נתיב אופניים", "bike")]
        public void VehicleForStrip_MatchesTheStripFamily(string strip, string vehicle) =>
            SectionFurnitureLogic.VehicleForStrip(strip)!.Key.Should().Be(vehicle);

        [Theory]
        [InlineData("מדרכה")]
        [InlineData("אי תנועה")]
        public void NoVehicleOnPedestrianOrIslandStrips(string strip) =>
            SectionFurnitureLogic.VehicleForStrip(strip).Should().BeNull();

        [Fact]
        public void VehiclesNeedClearanceInsideTheStrip()
        {
            SectionFurnitureLogic.FitsStrip(SectionFurnitureLogic.Bus, 2.6).Should().BeFalse();
            SectionFurnitureLogic.FitsStrip(SectionFurnitureLogic.Bus, 3.3).Should().BeTrue();
            SectionFurnitureLogic.FitsStrip(SectionFurnitureLogic.Car, 2.5).Should().BeTrue();
        }

        [Theory]
        [InlineData("נתיב אופניים")]
        [InlineData("שביל אופניים")]
        public void BothOfferedBicycleLabels_ProduceTheSameGlyphAndWidthContract(string label)
        {
            var vehicle = SectionFurnitureLogic.VehicleForStrip(label);
            vehicle.Should().BeSameAs(SectionFurnitureLogic.Bike);
            SectionFurnitureLogic.FitsStrip(vehicle!, 2.0).Should().BeTrue();
            SectionFurnitureLogic.FitsStrip(vehicle!, 1.5).Should().BeFalse();
            var geometry = SectionFurnitureLogic.VehicleGeometry(vehicle!);
            geometry.Loops.Should().NotBeEmpty();
            geometry.Circles.Should().HaveCount(2, "both offered names retain the bicycle glyph");
        }

        [Theory]
        [InlineData(TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
            SectionFurnitureLogic.OfficeCarView.Rear)]
        [InlineData(TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment,
            SectionFurnitureLogic.OfficeCarView.Front)]
        public void OfficeCarView_FollowsTheExplicitCivilViewingConvention(
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            SectionFurnitureLogic.OfficeCarView expected)
        {
            SectionFurnitureLogic.TryOfficeCarViewForFlow(flow, out var view)
                .Should().BeTrue();
            view.Should().Be(expected);
            SectionFurnitureLogic.OfficeCarViewingConvention.Should()
                .Contain("along-alignment=rear")
                .And.Contain("against-alignment=front");
        }

        [Fact]
        public void UnknownTrafficDirection_HasNoOfficeCarView() =>
            SectionFurnitureLogic.TryOfficeCarViewForFlow(
                    TrafficDirectionEvidenceLogic.RelativeFlow.Unknown, out _)
                .Should().BeFalse("left/right section offset is not direction evidence");

        [Theory]
        [InlineData(16.11974, 16.9828, 1.80, 1.896373)]
        [InlineData(17.763882, 15.593694, 1.80, 1.580097)]
        public void ApprovedOfficeBlock_IsWidthAnchoredWithoutAspectDistortion(
            double sourceWidth, double sourceHeight, double targetWidth, double expectedHeight)
        {
            SectionFurnitureLogic.TryOfficeBlockPhysicalHeight(
                    sourceWidth, sourceHeight, targetWidth, out var height)
                .Should().BeTrue();
            height.Should().BeApproximately(expectedHeight, 0.00001);
        }

        [Theory]
        [InlineData(0.0, 10.0)]
        [InlineData(10.0, 0.0)]
        [InlineData(1.0, 10.0)]
        [InlineData(double.NaN, 1.0)]
        public void ImplausibleOfficeBlockAspect_FailsClosed(
            double sourceWidth, double sourceHeight)
        {
            SectionFurnitureLogic.TryOfficeBlockPhysicalHeight(
                    sourceWidth, sourceHeight, 1.80, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void VehicleSilhouettesStayInsideTheirDeclaredBox()
        {
            foreach (var spec in new[]
            {
                SectionFurnitureLogic.Car, SectionFurnitureLogic.Bus, SectionFurnitureLogic.Bike,
            })
            {
                var (loops, circles) = SectionFurnitureLogic.VehicleGeometry(spec);
                foreach (var loop in loops)
                    foreach (var (x, y) in loop)
                    {
                        System.Math.Abs(x).Should().BeLessThanOrEqualTo(spec.WidthM / 2 + 1e-9, spec.Key);
                        y.Should().BeInRange(0, spec.HeightM + 1e-9, spec.Key);
                    }
                foreach (var (x, y, r) in circles)
                {
                    (System.Math.Abs(x) + r).Should().BeLessThanOrEqualTo(spec.WidthM / 2 + 1e-9, spec.Key);
                    (y - r).Should().BeGreaterThanOrEqualTo(-1e-9, spec.Key);
                }
            }
        }

        // ------------------------------------------------------- label ladder

        [Fact]
        public void LabelLadder_PushesCrowdedAnchorsApart()
        {
            // Three labels within a metre (the lighting-over-digit overlap, 31/08).
            var adjusted = SectionFurnitureLogic.LabelLadder(new[] { 0.0, 0.4, 0.9 });
            adjusted[0].Should().Be(0.0);
            adjusted[1].Should().BeApproximately(1.4, 1e-9);
            adjusted[2].Should().BeApproximately(2.8, 1e-9);
        }

        [Fact]
        public void LabelLadder_LeavesSpacedAnchorsAlone()
        {
            var adjusted = SectionFurnitureLogic.LabelLadder(new[] { -8.0, 0.0, 5.0 });
            adjusted.Should().Equal(-8.0, 0.0, 5.0);
        }

        [Fact]
        public void LabelLadder_PreservesInputIndexOrder()
        {
            // Inputs arrive unsorted (utilities then marks) — each caller must get
            // back the position for ITS anchor.
            var adjusted = SectionFurnitureLogic.LabelLadder(new[] { 5.0, -3.0, 5.3 });
            adjusted[1].Should().Be(-3.0);
            adjusted[0].Should().Be(5.0);
            adjusted[2].Should().BeApproximately(6.4, 1e-9);
        }

        // ------------------------------------------------ design surface picker

        [Fact]
        public void PickDesignSurface_PrefersTheAlignmentsFinalDesign()
        {
            // The real 6422 surface set of one sample line (read live, 31/08).
            var names = new[]
            {
                "MK", "MK-EAST", "2000-DESIGN", "2000-DESIGN-FINAL", "700D@Top@01",
                "700 top- (1)", "700 bot- (2)", "1000D@Top@01", "3000-DESIGN-FINAL",
            };
            SectionFurnitureLogic.PickDesignSurface(names, "2000").Should().Be("2000-DESIGN-FINAL");
            SectionFurnitureLogic.PickDesignSurface(names, "3000").Should().Be("3000-DESIGN-FINAL");
        }

        [Fact]
        public void PickDesignSurface_NeverGrabsBottomOrForeignSurfaces()
        {
            // The old "first non-EG" guess took '700 bot- (2)' and produced
            // 10-million-m³ earthworks. No *DESIGN* name ⇒ null ⇒ no earthworks.
            var names = new[] { "MK", "700 top- (1)", "700 bot- (2)" };
            SectionFurnitureLogic.PickDesignSurface(names, "700").Should().BeNull();
        }

        [Fact]
        public void PickDesignSurface_NeverFallsBackToAnotherAlignmentsDesign()
        {
            var names = new[] { "MK", "2000-DESIGN-FINAL" };
            SectionFurnitureLogic.PickDesignSurface(names, "600").Should().BeNull(
                "a foreign design surface is worse than drawing no vehicle at all");
        }

        [Fact]
        public void BoundedLabelLadder_ShiftsBothWaysButKeepsEveryLabelInside()
        {
            SectionFurnitureLogic.TryBoundedLabelLadder(
                    new[] { 8.7, 9.0, 9.2 },
                    new[] { 0.4, 0.4, 0.4 },
                    -10, 10, 0.2,
                    out var placed, out var error)
                .Should().BeTrue(error);
            placed.Should().BeInAscendingOrder();
            placed[0].Should().BeGreaterThanOrEqualTo(-9.6);
            placed[2].Should().BeLessThanOrEqualTo(9.6);
            (placed[1] - placed[0]).Should().BeGreaterThanOrEqualTo(1.0 - 1e-9);
            (placed[2] - placed[1]).Should().BeGreaterThanOrEqualTo(1.0 - 1e-9);
        }

        [Fact]
        public void BoundedLabelLadder_RejectsCrowdingThatCannotFit()
        {
            SectionFurnitureLogic.TryBoundedLabelLadder(
                    new[] { 0.0, 0.1, 0.2 },
                    new[] { 0.5, 0.5, 0.5 },
                    -1, 1, 0.2,
                    out _, out var error)
                .Should().BeFalse();
            error.Should().Contain("cannot fit");
        }

        // Live 6422 STA-12145 (29.09.2026): 25 exact curb/lane/paint faces in a
        // -24.6702..25.9224 SectionView at 1 drawing unit per offset metre.
        private static readonly double[] Sta12145Offsets =
        {
            -14.217, -11.091, -10.779, -9.779, -8.779, -8.449, -8.364, -8.279, -5.179, -4.879,
            -4.039, -2.029, -1.914, -1.799, 0.241, 0.356, 0.471, 0.944, 3.321, 3.621,
            6.721, 6.806, 6.891, 8.039, 11.239,
        };

        [Fact]
        public void RotatedBottomLadder_FitsTheRealUrbanSectionTheOldSpacingRejected()
        {
            var heights = Sta12145Offsets.Select(_ => 0.62).ToList();
            SectionFurnitureLogic.TryBoundedLabelLadder(
                    Sta12145Offsets, heights.Select(h => h + 0.50).ToList(),
                    -24.6702, 25.9224, 0.20, out _, out _)
                .Should().BeFalse("2.44 m per 0.62 digit is what refused APPLY live");

            SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                    Sta12145Offsets, heights, 1.0, 0.35, -24.6702, 25.9224,
                    out var placed, out var error)
                .Should().BeTrue(error);
            placed.Should().HaveCount(Sta12145Offsets.Length).And.BeInAscendingOrder();
            placed.Should().OnlyContain(p => p >= -24.6702 && p <= 25.9224);
            // Every ink column [p + 0.35 - 0.3h, p + 0.35 + h] stays clear of its neighbour by 0.3h.
            for (var i = 1; i < placed.Length; i++)
                ((placed[i] + 0.35 - 0.3 * 0.62) - (placed[i - 1] + 0.35 + 0.62))
                    .Should().BeGreaterThanOrEqualTo(0.3 * 0.62 - 1e-9);
            (placed[^1] + 0.35 + 0.62).Should().BeLessThanOrEqualTo(25.9224 + 1e-9);
            // A crowded group spreads to both sides of its ticks: the 09.09 layout printed
            // 11.24 at 24.80, a forward-only ladder would print 0.94 at 4.78.
            placed.Zip(Sta12145Offsets, (p, o) => Math.Abs(p - o)).Max()
                .Should().BeLessThan(2.0);
            placed[0].Should().BeApproximately(-14.217, 1e-9);
            placed[^1].Should().BeApproximately(11.239, 1e-9);
        }

        [Fact]
        public void CentredLadder_SpreadsACrowdSymmetricallyAroundItsAnchors()
        {
            SectionFurnitureLogic.TryCentredLabelLadder(
                    new[] { 0.0, 0.0, 0.0 }, new[] { 0.5, 0.5, 0.5 }, -10, 10, 0.0,
                    out var placed, out var error)
                .Should().BeTrue(error);
            placed.Should().Equal(new[] { -1.0, 0.0, 1.0 }, (a, b) => Math.Abs(a - b) < 1e-9);

            SectionFurnitureLogic.TryCentredLabelLadder(
                    new[] { -9.9, -9.9 }, new[] { 0.5, 0.5 }, -10, 10, 0.0,
                    out var clamped, out error)
                .Should().BeTrue(error);
            clamped.Should().Equal(new[] { -9.5, -8.5 }, (a, b) => Math.Abs(a - b) < 1e-9);
        }

        [Fact]
        public void RotatedBottomLadder_KeepsTheTrueOffsetWhenThereIsRoom()
        {
            SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                    new[] { -10.0, 0.0, 10.0 }, new[] { 0.62, 0.7, 0.62 }, 1.0, 0.35, -20, 20,
                    out var placed, out var error)
                .Should().BeTrue(error);
            placed.Should().Equal(new[] { -10.0, 0.0, 10.0 },
                (a, b) => Math.Abs(a - b) < 1e-9);
        }

        [Fact]
        public void RotatedBottomLadder_UsesTheLiveViewScale()
        {
            // At 2 drawing units per offset metre each column is half as wide in metres.
            var offsets = Enumerable.Range(0, 20).Select(i => i * 0.1).ToList();
            var heights = offsets.Select(_ => 0.62).ToList();
            SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                    offsets, heights, 1.0, 0.35, -5, 5, out _, out _)
                .Should().BeFalse("20 columns of 0.99 m cannot share 10 m");
            SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                    offsets, heights, 2.0, 0.35, -5, 5, out var placed, out var error)
                .Should().BeTrue(error);
            placed.Should().OnlyContain(p => p >= -5 && p <= 5);
        }

        [Fact]
        public void RotatedBottomLadder_StillRejectsCrowdingThatCannotFit()
        {
            SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                    new[] { 0.0, 0.1, 0.2 }, new[] { 0.62, 0.62, 0.62 }, 1.0, 0.35, -1, 1,
                    out _, out var error)
                .Should().BeFalse();
            error.Should().Contain("cannot fit");
            SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                    new[] { 0.0 }, new[] { 0.0 }, 1.0, 0.35, -1, 1, out _, out var invalid)
                .Should().BeFalse();
            invalid.Should().Contain("invalid");
        }

        // ----------------------------------------------- section point windows

        [Fact]
        public void SectionPointsInTheViewWindowNormalize()
        {
            var raw = new[] { (-12.0, 284.2, 0.0), (0.0, 285.0, 0.0), (12.0, 284.6, 0.0) };
            var chain = SectionFurnitureLogic.NormalizeSectionPoints(raw, -15, 15, 280, 290);
            chain.Should().HaveCount(3);
            chain[1].Should().Be((0.0, 285.0));
        }

        [Fact]
        public void WorldCoordinatePointsAreRefusedNotMisread()
        {
            // If the host ever hands world XYZ (X≈200000 UTM), drawing them as
            // offsets would smear a vehicle kilometres away. Refuse ⇒ draw nothing.
            var raw = new[] { (200000.0, 640000.0, 284.2), (200010.0, 640010.0, 285.0), (200020.0, 640020.0, 284.6) };
            SectionFurnitureLogic.NormalizeSectionPoints(raw, -15, 15, 280, 290).Should().BeEmpty();
        }

        // ------------------------------------------------------ strip overrides

        [Fact]
        public void AnOverrideMarkNamesTheStripItSitsIn()
        {
            var marks = new[] { (-3.5, "curb"), (0.0, "curb") };     // unnamed by the kind table
            var overrides = new[] { (-1.7, "נת\"צ") };
            var strips = SectionProjectionLogic.StripLabels(marks, overrides);
            strips.Should().ContainSingle();
            strips[0].Should().Be((-3.5, 0.0, "נת\"צ"));
        }

        [Fact]
        public void AnOverrideBeatsTheDerivedName()
        {
            var marks = new[] { (0.0, "lane"), (3.5, "curb") };      // would be נתיב נסיעה
            var strips = SectionProjectionLogic.StripLabels(marks, new[] { (1.5, "נת\"צ") });
            strips[0].Label.Should().Be("נת\"צ");
        }

        [Fact]
        public void AnOverrideOutsideEveryStripChangesNothing()
        {
            var marks = new[] { (0.0, "lane"), (3.5, "curb") };
            var strips = SectionProjectionLogic.StripLabels(marks, new[] { (9.0, "נת\"צ") });
            strips[0].Label.Should().Be("נתיב נסיעה");
        }
    }
}
