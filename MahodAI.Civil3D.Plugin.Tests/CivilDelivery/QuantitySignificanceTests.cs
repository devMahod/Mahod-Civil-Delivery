using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Reading order and noise classification, pinned against the REAL 6422 scan
    /// (2026-08-20): 192 discovered groups, 135 of them station-named layers, one "_Hidden"
    /// hatch reporting 8.6 million m², and 99.4% of the measured quantity in the top 20.
    /// </summary>
    public class QuantitySignificanceTests
    {
        [Theory]
        [InlineData("MYA-4801-Biuv-line", "ביוב")]
        [InlineData("MYA-4602-Water-line", "מים")]
        [InlineData("MAIM-EXST", "מים")]
        [InlineData("BIUV315-EX", "ביוב")]
        public void SurveyedUtilitiesAreInfrastructureNotWorkItems(string layer, string label)
        {
            // Survey conventions (MYA-*) and existing markers are infrastructure, not
            // pending mapping noise (engineer feedback, 31/08).
            var verdict = QuantitySignificance.Classify(
                new QuantitySignificance.Group("k", layer, "m", 60.0, 5));

            verdict.Kind.Should().Be(QuantitySignificance.Kind.ExistingUtility);
            verdict.Reason.Should().Contain(label);
            verdict.IsLikelyQuantity.Should().BeFalse();
        }

        [Theory]
        [InlineData("BIUV315")]
        [InlineData("MAIM 24Z")]
        [InlineData("MEKOROT 30Z")]
        public void DesignedUtilityLayersRemainPriceableQuantities(string layer)
        {
            // A water/sewer layer WITHOUT an existing marker may be designed pipe to
            // build — BIUV315 matched a real catalog item on 6422. The engineer
            // decides; the tool must not bury it.
            QuantitySignificance.Classify(
                new QuantitySignificance.Group("k", layer, "m", 812.4, 3))
                .IsLikelyQuantity.Should().BeTrue();
        }

        [Theory]
        [InlineData("HELP-750+LINK-R+W")]
        [InlineData("HW-CS-TABL")]
        [InlineData("MPI_Symbol")]
        public void HelperAndTableLayersNeverWaitForMapping(string layer)
        {
            QuantitySignificance.Classify(
                new QuantitySignificance.Group("k", layer, "m", 100.0, 2))
                .Kind.Should().Be(QuantitySignificance.Kind.Auxiliary);
        }

        [Theory]
        [InlineData("MHD-SECT-ANNO")]
        [InlineData("MHD-SECT-ANNO-V6")]
        [InlineData("mhd-sect-anno")]
        [InlineData("MCD-STA-60358")]
        [InlineData("MCDV-STA-60358")]
        public void TheToolNeverPricesItsOwnDrawings(string layer)
        {
            // The estimate once measured the tool's own section annotations as 1,758 m
            // of "work" (live, 30/08). Tool-owned layers classify as auxiliary always.
            var verdict = QuantitySignificance.Classify(
                new QuantitySignificance.Group("k", layer, "m", 1758.09, 242));

            verdict.Kind.Should().Be(QuantitySignificance.Kind.Auxiliary);
            verdict.Reason.Should().Contain("Mahod Civil Delivery");
        }

        [Theory]
        [InlineData("SM-MODEL|0")]
        [InlineData("SM-MODEL|Defpoints")]
        [InlineData("SM-MODEL|HELP-750+LINK-R+W")]
        [InlineData("SM-MODEL|MHD-SECT-ANNO")]
        [InlineData("SM-MODEL|MHD-SECT-ANNO-V6")]
        public void XrefQualificationCannotTurnDrawingFurnitureIntoAQuantity(string layer)
        {
            QuantitySignificance.Classify(
                    new QuantitySignificance.Group("k", layer, "m", 100, 2))
                .Kind.Should().Be(QuantitySignificance.Kind.Auxiliary);
        }

        [Fact]
        public void XrefQualifiedStationLayerRemainsStationGeometry()
        {
            QuantitySignificance.Classify(
                    new QuantitySignificance.Group(
                        "k", "SM-MODEL|2000+120-2+W", "m", 100, 2))
                .Kind.Should().Be(QuantitySignificance.Kind.StationGeometry);
        }

        [Fact]
        public void SurveyPointBlockOnKerbLayer_IsVisibleAuxiliary_NotAProposedKerbCount()
        {
            // Exact evidence from the 6422 artifact (31/08): all 3,889 count records
            // on CURB-EXST carried block_name=S_POINT_E.  The block subject, not its
            // incidental layer, proves these are survey markers.
            var group = new QuantitySignificance.Group(
                "layer:CURB-EXST|count|block:S_POINT_E",
                "CURB-EXST", "יח'", 3889, 3889);

            var verdict = QuantitySignificance.Classify(group);

            verdict.Kind.Should().Be(QuantitySignificance.Kind.Auxiliary);
            verdict.Reason.Should().Contain("S_POINT_E");
            MappingProposalEngine.IsProposalEligible(new MappingProposalEngine.DiscoveredGroup(
                group.RuleKey, group.Layer, "count", group.Unit,
                group.ObjectCount, group.Quantity)).Should().BeFalse();
        }

        [Fact]
        public void GenuineCountBlockOnSameKerbLayer_RemainsEngineerVisibleQuantity()
        {
            QuantitySignificance.Classify(new QuantitySignificance.Group(
                    "layer:CURB-EXST|count|block:KERB_UNIT",
                    "CURB-EXST", "יח'", 25, 25))
                .IsLikelyQuantity.Should().BeTrue(
                    "only the evidenced S_POINT_E survey marker is auxiliary");
        }

        [Fact]
        public void SurveyPointMarker_RemainsAReviewGateUntilEngineerOverridesIt()
        {
            var record = EstimateFixtures.Record(
                "survey-point", null!, 1, "יח'", kind: "count",
                handle: "CB34",
                ruleKey: "layer:CURB-EXST|count|block:S_POINT_E",
                layer: "CURB-EXST", method: "block-count");

            var finding = QuantitySignificance.DetectReviewFindings(new[] { record })
                .Should().ContainSingle().Subject;
            finding.Code.Should().Be(EstimateFindingCodes.QuantitySignificanceReview);
            finding.AffectedRecordIds.Should().Equal("survey-point");

            QuantitySignificance.DetectReviewFindings(
                    new[] { record }, new[] { record.Classification.RuleKey! })
                .Should().BeEmpty("an audited engineer exclusion remains authoritative");
        }


        private static QuantitySignificance.Group G(string layer, string unit, double q, int n) =>
            new($"layer:{layer}|x", layer, unit, q, n);

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(0d)]
        [InlineData(-1d)]
        public void InvalidMeasurement_PrecedesEveryNoiseClassification(double value)
        {
            var verdict = QuantitySignificance.Classify(
                G("MCD-2000+120+W", "מטר", value, 1));

            verdict.Kind.Should().Be(QuantitySignificance.Kind.InvalidMeasurement);
            verdict.IsLikelyQuantity.Should().BeFalse();
            QuantitySignificance.IsValidMeasurement(value).Should().BeFalse();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveObjectCount_IsInvalidBeforeLayerClassification(int objectCount)
        {
            var verdict = QuantitySignificance.Classify(new QuantitySignificance.Group(
                "layer:0-EZER|length", "0-EZER", "מטר", 12, objectCount));

            verdict.Kind.Should().Be(QuantitySignificance.Kind.InvalidMeasurement);
            QuantitySignificance.IsValidMeasurement(12, objectCount).Should().BeFalse();
        }

        [Fact]
        public void InvalidMeasurement_IsAnErrorEvenWhenMappedAndIgnored()
        {
            const string key = "layer:2000+120+W|length";
            var record = EstimateFixtures.Record(
                "invalid-station", "U51.01.0250", double.NaN, "מטר",
                handle: "S0", ruleKey: key, layer: "2000+120+W");

            var finding = QuantitySignificance.DetectReviewFindings(
                    new[] { record }, new[] { key })
                .Should().ContainSingle().Subject;

            finding.Code.Should().Be(EstimateFindingCodes.MeasurementFailed);
            finding.Severity.Should().Be(FindingSeverity.Error);
            finding.AffectedRecordIds.Should().Equal(record.RecordId);
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Theory]
        // Straight from the 6422 scan.
        [InlineData("2000+120-2+W")]
        [InlineData("2000+230-2+W")]
        [InlineData("2000-DES+220+W")]
        [InlineData("2000+110+W")]
        [InlineData("750+260819_101600")]
        public void StationNamedLayers_AreSectionGeometry_NotWork(string layer)
        {
            var v = QuantitySignificance.Classify(G(layer, "מטר", 2881.6, 30));
            v.Kind.Should().Be(QuantitySignificance.Kind.StationGeometry);
            v.IsLikelyQuantity.Should().BeFalse();
            v.Reason.Should().NotBeEmpty("the engineer is told why, never just filtered");
        }

        [Theory]
        [InlineData("_Hidden")]
        [InlineData("Defpoints")]
        [InlineData("0-EZER")]
        [InlineData("pl-cont")]
        public void TemplateAndHelperLayers_AreAuxiliary(string layer)
        {
            QuantitySignificance.Classify(G(layer, "מ\"ר", 1234, 10))
                .Kind.Should().Be(QuantitySignificance.Kind.Auxiliary);
        }

        [Fact]
        public void TwoObjectsReportingMillionsOfSquareMetres_AreFlagged()
        {
            // The real one: _Hidden, 2 objects, 8,606,375.74 m².
            QuantitySignificance.Classify(G("SOME-HATCH", "מ\"ר", 8_606_375.74, 2))
                .Kind.Should().Be(QuantitySignificance.Kind.ImplausibleMagnitude);

            // A real area from many objects is NOT flagged, however large.
            QuantitySignificance.Classify(G("ASPHALT", "מ\"ר", 600_000, 900))
                .IsLikelyQuantity.Should().BeTrue();
        }

        [Fact]
        public void OneSyntheticRecordReportingTwentyMillionCubicMetres_IsFlagged()
        {
            QuantitySignificance.Classify(G("earthworks:cut", "מ\"ק", 20_000_000, 1))
                .Kind.Should().Be(QuantitySignificance.Kind.ImplausibleMagnitude);
        }

        [Fact]
        public void TwentyMillionCubicMetresAcrossManyRecords_IsStillFlagged()
        {
            QuantitySignificance.Classify(G("earthworks:cut", "מ\"ק", 20_000_000, 120))
                .Kind.Should().Be(QuantitySignificance.Kind.ImplausibleMagnitude,
                    "bad surface width/station sampling may spread the inflated total over many records");
        }

        [Fact]
        public void SuspiciousUnresolvedGroups_BlockExportUntilEngineerDecides()
        {
            var record = EstimateFixtures.Record(
                "noise", null!, 8_606_375.74, "מ\"ר", kind: "area",
                handle: "H1", ruleKey: "layer:_Hidden|area", layer: "_Hidden",
                method: "hatch-area");

            var finding = QuantitySignificance.DetectReviewFindings(new[] { record })
                .Should().ContainSingle().Subject;

            finding.Code.Should().Be(EstimateFindingCodes.QuantitySignificanceReview);
            finding.AffectedRecordIds.Should().Equal("noise");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Fact]
        public void ExplicitIgnoreOrApprovedMapping_ResolvesSignificanceGate()
        {
            var unresolved = EstimateFixtures.Record(
                "noise", null!, 8_606_375.74, "מ\"ר", kind: "area",
                ruleKey: "layer:_Hidden|area", layer: "_Hidden", method: "hatch-area");
            var approved = EstimateFixtures.Record(
                "approved", "U51.01.0090", 600_000, "מ\"ר", kind: "area",
                ruleKey: "layer:_Hidden|area", layer: "_Hidden", method: "hatch-area");

            QuantitySignificance.DetectReviewFindings(
                    new[] { unresolved }, new[] { "layer:_Hidden|area" })
                .Should().BeEmpty("the engineer explicitly marked the group not relevant");
            QuantitySignificance.DetectReviewFindings(new[] { approved })
                .Should().BeEmpty("an explicitly approved mapping is the engineer's decision");
        }

        [Fact]
        public void SameLayerInLengthAndArea_IsAReviewGate_NotSilentDoubleMeaning()
        {
            var length = EstimateFixtures.Record(
                "curb-l", null!, 100, "מטר", kind: "length", handle: "L1",
                ruleKey: "layer:CURB-EXST|length", layer: "CURB-EXST");
            var area = EstimateFixtures.Record(
                "curb-a", null!, 50, "מ\"ר", kind: "area", handle: "A1",
                ruleKey: "layer:CURB-EXST|area", layer: "CURB-EXST",
                method: "closed-polyline-area");

            var finding = QuantitySignificance.DetectReviewFindings(new[] { length, area })
                .Should().ContainSingle(f => f.Code == EstimateFindingCodes.MixedDimensionLayer)
                .Subject;
            finding.AffectedRecordIds.Should().BeEquivalentTo("curb-l", "curb-a");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Fact]
        public void ApprovingOnlyOneOfTwoLayerDimensions_DoesNotResolveTheGate()
        {
            var approvedLength = EstimateFixtures.Record(
                "curb-l", "U51.01.0250", 100, "מטר", kind: "length", handle: "L1",
                ruleKey: "layer:CURB-EXST|length", layer: "CURB-EXST");
            var unresolvedArea = EstimateFixtures.Record(
                "curb-a", null!, 50, "מ\"ר", kind: "area", handle: "A1",
                ruleKey: "layer:CURB-EXST|area", layer: "CURB-EXST",
                method: "closed-polyline-area");

            var finding = QuantitySignificance.DetectReviewFindings(
                    new[] { approvedLength, unresolvedArea })
                .Should().ContainSingle(f => f.Code == EstimateFindingCodes.MixedDimensionLayer)
                .Subject;

            finding.AffectedRecordIds.Should().BeEquivalentTo(
                new[] { "curb-l", "curb-a" },
                "the unresolved interpretation makes the whole layer ambiguous");
        }

        [Fact]
        public void BuildBoundary_MandatorilyBlocksImplausibleUnapprovedVolume()
        {
            var records = Enumerable.Range(1, 120)
                .Select(i => EstimateFixtures.Record(
                    $"earth-{i}", null!, 20_000_000d / 120d, "מ\"ק", kind: "volume",
                    handle: $"E{i}", ruleKey: "earthworks:cut|volume", layer: "earthworks:cut",
                    method: "section-average-end-area"))
                .ToList();

            var result = EstimateBuilder.Build(
                records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            result.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.QuantitySignificanceReview);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals && l.Total == null);
            result.CleanTotal.Should().Be(0m);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();

            var output = Path.Combine(Path.GetTempPath(), "mcd-significance-" + Guid.NewGuid().ToString("N"));
            var export = () => EstimateExcelWriter.Write(result, output, "must-not-exist");
            export.Should().Throw<InvalidOperationException>()
                .WithMessage("*EST-QUANTITY-SIGNIFICANCE-REVIEW*");
            Directory.Exists(output).Should().BeFalse("the export gate runs before creating output artifacts");
        }

        [Fact]
        public void BuildBoundary_MandatorilyBlocksMixedDimensionLayer()
        {
            var length = EstimateFixtures.Record(
                "curb-l", null!, 100, "מטר", kind: "length", handle: "L1",
                ruleKey: "layer:CURB-EXST|length", layer: "CURB-EXST");
            var area = EstimateFixtures.Record(
                "curb-a", null!, 50, "מ\"ר", kind: "area", handle: "A1",
                ruleKey: "layer:CURB-EXST|area", layer: "CURB-EXST",
                method: "closed-polyline-area");

            var result = EstimateBuilder.Build(
                new[] { length, area }, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            result.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.MixedDimensionLayer);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals && l.Total == null);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void ExplicitIgnoreOrApprovedMappings_ResolveBuildBoundaryGates()
        {
            var ignoredProfile = EstimateFixtures.Profile();
            ignoredProfile.Estimate.IgnoredRuleDecisions.Add(new()
            {
                RuleKey = "earthworks:cut|volume",
                Reason = "נבדק הנדסית ומחוץ לתכולת אומדן זה",
                ApprovedBy = "nataly",
                ApprovedAtUtc = System.DateTime.UtcNow,
            });
            var ignored = EstimateFixtures.Record(
                "earth", null!, 20_000_000, "מ\"ק", kind: "volume", handle: "E1",
                ruleKey: "earthworks:cut|volume", layer: "earthworks:cut",
                method: "section-average-end-area");
            var ignoredResult = EstimateBuilder.Build(
                new[] { ignored }, EstimateFixtures.Snapshot(), ignoredProfile);
            ignoredResult.Findings.Should().NotContain(f =>
                f.Code == EstimateFindingCodes.QuantitySignificanceReview);

            // Synthetic, genuinely distinct native-shaped subjects. The generic
            // fixture's LWPOLYLINE label and nonhex L1 are not proof of independence.
            NeutralQuantityRecord NativePolyline(NeutralQuantityRecord template) => new()
            {
                RecordId = template.RecordId, ProjectProfileId = template.ProjectProfileId,
                RunId = template.RunId, Measurement = template.Measurement,
                Classification = template.Classification,
                Source = new QuantitySource
                {
                    Drawing = "synthetic-distinct-subjects.dwg",
                    DrawingPath = @"C:\SYNTHETIC-ONLY\synthetic-distinct-subjects.dwg",
                    DrawingHash = new string('a', 64), Handle = template.Source.Handle,
                    EntityType = "Polyline", Layer = template.Source.Layer,
                },
            };
            var approvedLength = NativePolyline(EstimateFixtures.Record(
                "curb-l", "U51.01.0250", 100, "מטר", kind: "length", handle: "A11",
                ruleKey: "layer:CURB-EXST|length", layer: "CURB-EXST", method: "polyline-length"));
            var approvedArea = NativePolyline(EstimateFixtures.Record(
                "curb-a", "U51.01.0090", 50, "מ\"ר", kind: "area", handle: "A12",
                ruleKey: "layer:CURB-EXST|area", layer: "CURB-EXST",
                method: "closed-polyline-area"));
            var approvedResult = EstimateBuilder.Build(
                new[] { approvedLength, approvedArea },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());
            approvedResult.Findings.Should().NotContain(f =>
                f.Code == EstimateFindingCodes.MixedDimensionLayer);
        }

        [Fact]
        public void BothApprovedDimensions_FromExactClosedPolylineSources_RemainExportBlocked()
        {
            var length = EstimateFixtures.Record(
                "boundary-l", "U51.01.0250", 30, "מטר", kind: "length", handle: "CLOSED-1",
                ruleKey: "layer:PAVING|length", layer: "PAVING",
                method: "closed-polyline-perimeter");
            var area = EstimateFixtures.Record(
                "boundary-a", "U51.01.0090", 50, "מ\"ר", kind: "area", handle: "CLOSED-1",
                ruleKey: "layer:PAVING|area", layer: "PAVING",
                method: "closed-polyline-area");

            var result = EstimateBuilder.Build(
                new[] { length, area }, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var finding = result.Findings.Should().ContainSingle(candidate =>
                candidate.Code == EstimateFindingCodes.MixedDimensionLayer).Subject;
            finding.AffectedRecordIds.Should().BeEquivalentTo("boundary-l", "boundary-a");
            result.Lines.Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse(
                "catalog approval cannot turn one exact boundary into two additive BOQ quantities");
        }

        [Fact]
        public void ClosedPolylineAlternative_DoesNotAutoPairARuleContainingOtherAreaSources()
        {
            var perimeter = EstimateFixtures.Record(
                "boundary-l", null!, 30, "מטר", kind: "length", handle: "CLOSED-1",
                ruleKey: "layer:PAVING|length", layer: "PAVING",
                method: "closed-polyline-perimeter");
            var boundaryArea = EstimateFixtures.Record(
                "boundary-a", null!, 50, "מ\"ר", kind: "area", handle: "CLOSED-1",
                ruleKey: "layer:PAVING|area", layer: "PAVING",
                method: "closed-polyline-area");
            var hatchArea = EstimateFixtures.Record(
                "hatch-a", null!, 20, "מ\"ר", kind: "area", handle: "HATCH-2",
                ruleKey: "layer:PAVING|area", layer: "PAVING",
                method: "hatch-area");

            ClosedPolylineAlternativePolicy.FindExactPairs(
                    new[] { perimeter, boundaryArea, hatchArea })
                .Should().BeEmpty(
                    "an audited ignore applies to the whole rule key and must not remove an unrelated hatch");
        }

        [Theory]
        [InlineData("CURB-EXST", "מטר", 33476.09, 395)]
        [InlineData("WALL-EX", "מטר", 9137.67, 377)]
        [InlineData("BIUV315", "מטר", 812.4, 3)]
        [InlineData("MAIM24Z", "יח'", 1, 1)]
        public void RealConstructionLayers_StayQuantities(string layer, string unit, double q, int n)
        {
            QuantitySignificance.Classify(G(layer, unit, q, n))
                .IsLikelyQuantity.Should().BeTrue($"{layer} is work someone builds");
        }

        [Fact]
        public void Order_PutsTheMoneyFirst_AndNoiseLast()
        {
            var groups = new List<QuantitySignificance.Group>
            {
                G("2000+120-2+W", "מטר", 2881.60, 30),     // station geometry
                G("_Hidden", "מ\"ר", 8_606_375.74, 2),     // auxiliary
                G("CURB-EXST", "מטר", 33476.09, 395),      // the big real one
                G("WALL-EX", "מטר", 9137.67, 377),
                G("CURB-EXST-AREA", "מ\"ר", 11943.16, 42),
                G("SIGNS", "יח'", 3889, 3889),
            };

            var ordered = QuantitySignificance.Order(groups).Select(g => g.Layer).ToList();

            ordered[0].Should().Be("CURB-EXST", "longest length first");
            ordered[1].Should().Be("WALL-EX");
            ordered[2].Should().Be("CURB-EXST-AREA", "areas come after lengths");
            ordered[3].Should().Be("SIGNS", "counts after areas");
            ordered.Skip(4).Should().BeEquivalentTo(new[] { "2000+120-2+W", "_Hidden" },
                "everything the engineer should not have to read is at the bottom");
        }

        [Fact]
        public void Order_IsStableAndTotal()
        {
            var groups = new List<QuantitySignificance.Group>
            {
                G("A", "מטר", 10, 1), G("B", "מטר", 10, 1), G("C", "מ\"ר", 99, 5),
            };
            var a = QuantitySignificance.Order(groups).Select(g => g.RuleKey).ToList();
            var b = QuantitySignificance.Order(groups).Select(g => g.RuleKey).ToList();
            a.Should().Equal(b, "the table must not reshuffle between scans");
            a.Should().HaveCount(groups.Count, "no group is ever dropped from the list");
        }
    }
}
