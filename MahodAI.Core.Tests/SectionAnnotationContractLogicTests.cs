using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionAnnotationContractLogicTests
    {
        [Fact]
        public void DatumEvidence_RequiresExistingGroundFiniteElevationAndExactHandle()
        {
            SectionAnnotationContractLogic.TryParseDatumReference(
                    "existing-ground-at-axis|elevation=234.183|handle=7AcE",
                    out var evidence, out var error)
                .Should().BeTrue(error);
            evidence.Should().NotBeNull();
            evidence!.Elevation.Should().Be(234.183);
            evidence.Handle.Should().Be("7ACE");

            SectionAnnotationContractLogic.TryParseDatumReference(
                    "grid-minimum|elevation=234.183|handle=7ACE", out _, out _)
                .Should().BeFalse("the grid minimum is not an engineering datum");
            SectionAnnotationContractLogic.TryParseDatumReference(
                    "existing-ground-at-axis|elevation=NaN|handle=7ACE", out _, out _)
                .Should().BeFalse();
            SectionAnnotationContractLogic.TryParseDatumReference(
                    "existing-ground-at-axis|elevation=234.183|handle=not-a-handle", out _, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void OfficeBlockEvidence_RequiresDirectionSourceDigestAndConsistentView()
        {
            var directionDigest = new string('a', 64);
            var value = "office-block|rear|offset=-4.250|direction=along-alignment|" +
                        "direction-source=arrow|direction-digest=" + directionDigest +
                        "|detail=e2823d50d85e2f66|handle=7ace";

            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value, out var evidence, out var error)
                .Should().BeTrue(error);
            evidence.Should().NotBeNull();
            evidence!.IsOfficeBlock.Should().BeTrue();
            evidence.ViewOrFamily.Should().Be("rear");
            evidence.DirectionFlow.Should().Be("along-alignment");
            evidence.DirectionSource.Should().Be("arrow");
            evidence.DirectionDigest.Should().Be(directionDigest);
            evidence.HasStrictDirection.Should().BeTrue();
            evidence.Handle.Should().Be("7ACE");
            evidence.HasExactOfficeHandle.Should().BeTrue();

            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value.Replace("office-block|rear", "office-block|front"),
                    out _, out _)
                .Should().BeFalse("along-alignment is rear under the Civil viewing convention");
            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value.Replace(directionDigest, "short"), out _, out _)
                .Should().BeFalse();
            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value.Substring(0, value.LastIndexOf("|handle=", System.StringComparison.Ordinal)),
                    out _, out _)
                .Should().BeFalse("an office block must bind one exact live reference");
        }

        [Theory]
        [InlineData("schematic-fallback|front|offset=-3.250|direction=against-alignment|direction-source=manual|direction-digest=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|detail=asset-unavailable")]
        [InlineData("schematic|bus|offset=2.000|direction=along-alignment|direction-source=arrow|direction-digest=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|approved-office-block-not-supplied")]
        [InlineData("schematic|bike|offset=-1.500|direction=against-alignment|direction-source=manual|direction-digest=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|approved-office-block-not-supplied")]
        public void ExplicitTechnicalFallbackEvidence_IsParseable(string value)
        {
            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value, out var evidence, out var error)
                .Should().BeTrue(error);
            evidence!.IsSchematic.Should().BeTrue();
            evidence.HasStrictDirection.Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData("office-block|front")]
        [InlineData("office-block|front|offset=4.250|basis=civil-signed-offset-presentation-only;traffic-direction-not-inferred|detail=412e0e6f226271aa")]
        [InlineData("schematic|car|offset=1.0|approved-office-block-not-supplied")]
        [InlineData("schematic|bus|offset=2.000|approved-office-block-not-supplied")]
        [InlineData("schematic|bike|offset=-1.500|approved-office-block-not-supplied")]
        [InlineData("unknown|front|offset=1")]
        public void MissingOrUnsupportedVehicleEvidence_FailsClosed(string value)
        {
            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value, out _, out _)
                .Should().BeFalse();
        }

        [Theory]
        [InlineData("bus", "motor")]
        [InlineData("bike", "bicycle")]
        public void SchematicFormatter_BindsFamilySpecificDirection(
            string family, string mode)
        {
            SectionVehicleDirectionPlanner.TryRestoreResolved(
                    "along-alignment", mode, "manual", new string('c', 64),
                    "test", out var direction)
                .Should().BeTrue();

            var value = SectionAnnotationContractLogic.FormatSchematicVehicleReference(
                family, 2.25, direction!);

            SectionAnnotationContractLogic.TryParseVehicleEvidence(
                    value, out var evidence, out var error)
                .Should().BeTrue(error);
            evidence!.ViewOrFamily.Should().Be(family);
            evidence.DirectionDigest.Should().Be(new string('c', 64));
        }

        [Theory]
        [InlineData(TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
            SectionTrafficDirectionAnnotationLogic.StripKind.Road,
            "road-black", 7, true)]
        [InlineData(TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment,
            SectionTrafficDirectionAnnotationLogic.StripKind.Bus,
            "bus-red", 1, false)]
        [InlineData(TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
            SectionTrafficDirectionAnnotationLogic.StripKind.Bike,
            "bike-black", 7, true)]
        public void OfficeStripArrow_HasExactEnvelopeFlowAndStyle(
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            SectionTrafficDirectionAnnotationLogic.StripKind kind,
            string style,
            int color,
            bool pointsUp)
        {
            SectionTrafficDirectionAnnotationLogic.TryBuild(
                    4.25, 234.18, flow, kind, out var arrow, out var error)
                .Should().BeTrue(error);

            arrow!.LaneMidOffsetM.Should().Be(4.25);
            arrow.StyleToken.Should().Be(style);
            arrow.ColorIndex.Should().Be((short)color);
            arrow.PointsUp.Should().Be(pointsUp);
            arrow.TopElevation
                .Should().BeApproximately(
                    234.18 + SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(kind),
                    1e-9);
            (arrow.TopElevation - arrow.BottomElevation)
                .Should().BeApproximately(
                    SectionTrafficDirectionAnnotationLogic.ArrowHeightM, 1e-9);
        }

        [Fact]
        public void UnresolvedFlow_CannotProduceAVisibleArrow()
        {
            SectionTrafficDirectionAnnotationLogic.TryBuild(
                    4.25, 234.18,
                    TrafficDirectionEvidenceLogic.RelativeFlow.Unknown,
                    SectionTrafficDirectionAnnotationLogic.StripKind.Road,
                    out _, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void ManagedViewTopMargin_ClearsEveryVisibleDirectionArrow()
        {
            var required = new[]
            {
                SectionTrafficDirectionAnnotationLogic.StripKind.Road,
                SectionTrafficDirectionAnnotationLogic.StripKind.Bus,
                SectionTrafficDirectionAnnotationLogic.StripKind.Bike,
            }.Max(SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM);

            SectionTrafficDirectionAnnotationLogic.ManagedViewTopMarginM
                .Should().BeGreaterThan(required);
            SectionTrafficDirectionAnnotationLogic.MaxManagedViewEnvelopePaddingM
                .Should().Be(
                    SectionTrafficDirectionAnnotationLogic.ManagedViewBottomMarginM +
                    SectionTrafficDirectionAnnotationLogic.ManagedViewTopMarginM + 2.0);
        }

        [Fact]
        public void VisibleDirectionArrowEvidence_BindsPinnedOfficeAssetAndOneHandle()
        {
            var digest = new string('b', 64);
            var geometry = new string('c', 64);
            var value = SectionAnnotationContractLogic.FormatTrafficDirectionArrowReference(
                4.25,
                TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment,
                SectionVehicleDirectionPlanner.ManualSource,
                digest,
                SectionTrafficDirectionAnnotationLogic.BusRedStyle,
                SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                geometry,
                "A1");

            SectionAnnotationContractLogic.TryParseTrafficDirectionArrowReference(
                    value, out var evidence, out var error)
                .Should().BeTrue(error);
            evidence!.DirectionFlow.Should().Be("against-alignment");
            evidence.DirectionSource.Should().Be("manual");
            evidence.DirectionDigest.Should().Be(digest);
            evidence.Style.Should().Be("bus-red");
            evidence.AssetSha256.Should().Be(
                SectionTrafficArrowAssetEvidenceLogic.SourceSha256);
            evidence.GeometrySha256.Should().Be(geometry);
            evidence.Handle.Should().Be("A1");

            SectionAnnotationContractLogic.TryParseTrafficDirectionArrowReference(
                    value.Replace("handle=A1", "handle=not-hex"), out _, out _)
                .Should().BeFalse("the rendered office block needs one exact registered handle");
            SectionAnnotationContractLogic.TryParseTrafficDirectionArrowReference(
                    value.Replace(
                        SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                        new string('d', 64)), out _, out _)
                .Should().BeFalse("a different arrow DWG is not Nataly's approved third block");
        }

        [Fact]
        public void SlopeEvidence_RecomputesPercentAndRequiresRegisteredHandle()
        {
            var slope = new SectionFurnitureLogic.SlopeEvidence(
                -3.0, 1.0, 234.20, 234.12, -2.0);
            var value = SectionAnnotationContractLogic.FormatSlopeReference(slope, "7AcE");

            SectionAnnotationContractLogic.TryParseSlopeReference(
                    value, out var evidence, out var error)
                .Should().BeTrue(error);
            evidence.Should().NotBeNull();
            evidence!.Handle.Should().Be("7ACE");
            evidence.Label.Should().Be("-2.00%");

            SectionAnnotationContractLogic.TryParseSlopeReference(
                    value.Replace("percent=-2.000000", "percent=2.000000"),
                    out _, out _)
                .Should().BeFalse("the displayed grade must derive from the sampled endpoints");
            SectionAnnotationContractLogic.TryParseSlopeReference(
                    value.Replace("handle=7AcE", "handle=not-hex"),
                    out _, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void DimensionOffsetEvidence_BindsAnchorPlacedPositionTextAndHandle()
        {
            var value = SectionAnnotationContractLogic.FormatDimensionOffsetLabelReference(
                -4.25, -3.80, "a1");
            SectionAnnotationContractLogic.TryParseDimensionOffsetLabelReference(
                    value, out var evidence, out var error)
                .Should().BeTrue(error);
            evidence!.AnchorOffset.Should().Be(-4.25);
            evidence.PlacedOffset.Should().Be(-3.8);
            evidence.Text.Should().Be("-4.25");
            evidence.Handle.Should().Be("A1");

            SectionAnnotationContractLogic.TryParseDimensionOffsetLabelReference(
                    value.Replace("text=-4.25", "text=4.25"), out _, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void CurrentAnnotationContract_CoversAllCorePresentationGeometryVersionSix()
        {
            SectionAnnotationContractLogic.CurrentVersion.Should().Be(6);
        }
    }
}
