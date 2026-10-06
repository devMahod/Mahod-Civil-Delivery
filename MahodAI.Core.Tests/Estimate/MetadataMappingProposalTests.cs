using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class MetadataMappingProposalTests
    {
        // Names below were recovered by the offline ACadSharp partial inventory
        // of 6422-SM-MODEL-NATAZ 1. They test ranking semantics, not native geometry
        // or a quantity takeoff; the catalog codes/text are actual fixture rows.
        [Fact]
        public void RecoveredSmBikeSignSymbol_IsNotARackEvenWhenRackWordingMentionsASign()
        {
            var group = Group("TR-SIGN-STAG-BL", "יח'", Field("cad_block_name_raw", "BIKE"));
            var snapshot = Catalog(
                ("U40.02.1230", "מתקן לנעילת אופניים, דגם קשת, כולל שלט נירוסטה בגריעת סמל אופניים", "יח'"),
                ("U40.02.1255", "מתקן סטנדרט חנייה בודד לאופניים, לרבות שלט עם ציור אופניים", "יח'"),
                ("U51.32.0120", "תמרור 804 -סימון סמל אופניים בצבע חד רכיבי לבן", "מ\"ר"));

            MappingProposalEngine.Propose(new[] { group }, snapshot).Should().BeEmpty();
        }

        [Fact]
        public void RecoveredSmExplicitRackName_OnUnknownBycLayer_FindsExistingRackItems()
        {
            var group = Group("byc", "יח'", Field("cad_block_name_raw",
                "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה"));
            var snapshot = Catalog(
                ("U40.02.1230", "מתקן לנעילת אופניים, דגם קשת, כולל שלט נירוסטה בגריעת סמל אופניים", "יח'"),
                ("U40.02.1240", "מתקן לנעילת אופניים, דגם סלילון, באורך 2.0 מ.", "יח'"));

            var proposals = MappingProposalEngine.Propose(new[] { group }, snapshot);

            proposals.Select(proposal => proposal.ProposedCode).Should().BeEquivalentTo(
                new[] { "U40.02.1230", "U40.02.1240" });
            proposals.Should().OnlyContain(proposal => proposal.Status == "PROPOSED_UNAPPROVED" &&
                proposal.EvidenceKind == "heuristic-cad-metadata");
        }

        [Fact]
        public void RecoveredSmArrowInsertCount_CannotBuyElectricalMarkersOrConvertToPaintArea()
        {
            var group = Group("TR-MARK-ARW-BL", "יח'", Field("cad_block_name_raw", "arrow-D"));
            var snapshot = Catalog(
                ("U08.06.0225", "עמוד סימון לחציית קו חשמל או תקשורת", "יח'"),
                ("U08.06.0590", "שוחת סימון כבל בנויה בלוקים", "יח'"),
                ("U51.32.0090", "צביעת שטחים בצבע חד רכיבי, פסים למעבר חציה, וחיצים", "מ\"ר"));

            MappingProposalEngine.Propose(new[] { group }, snapshot).Should().BeEmpty();
        }

        [Fact]
        public void IndependentlyMeasuredArrowArea_CanStillSuggestSameUnitActualPaintItem()
        {
            var group = new MappingProposalEngine.DiscoveredGroup(
                "layer:TR-MARK-ARW-BL|area", "TR-MARK-ARW-BL", "area", "מ\"ר", 2, 2);
            var snapshot = Catalog(
                ("U08.06.0590", "שוחת סימון כבל בנויה בלוקים", "יח'"),
                ("U51.32.0090", "צביעת שטחים בצבע חד רכיבי, פסים למעבר חציה, וחיצים", "מ\"ר"));

            MappingProposalEngine.Propose(new[] { group }, snapshot)
                .Should().ContainSingle(proposal => proposal.ProposedCode == "U51.32.0090");
        }

        [Fact]
        public void RecoveredSmArrowCount_MayOfferCountedArrowButCannotConfirmItsSizeOrMissingPrice()
        {
            var group = Group("TR-MARK-ARW-BL", "יח'", Field("cad_block_name_raw", "arrow-D"));
            var snapshot = Catalog(("U51.32.0810",
                "סימון חץ בצבע לבן במידות 75X120 ס\"מ כדוגמת BALOO או ש\"ע , ע\"ג שביל אופניים", "יח'"));

            var proposal = MappingProposalEngine.Propose(new[] { group }, snapshot)
                .Should().ContainSingle().Subject;

            proposal.ProposedCode.Should().Be("U51.32.0810");
            proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
            proposal.Reasons.Should().Contain(reason => reason.Contains("MISSING_PRICE"))
                .And.Contain(reason => reason.Contains("מידות החץ") && reason.Contains("טרם אושרו"));
            snapshot.Prices.Should().BeEmpty();
        }

        [Fact]
        public void UnknownLayer_WithSharedEffectiveBlockName_OffersOnlyAnActualCatalogCandidate()
        {
            var snapshot = Catalog(("B1", "ספסל רחוב", "יח'"), ("T1", "נטיעת עץ", "יח'"));
            var group = Group("Q742", "יח'", Field("cad_block_name_effective", "BENCH"));

            var proposal = MappingProposalEngine.Propose(new[] { group }, snapshot).Should().ContainSingle().Subject;

            proposal.ProposedCode.Should().Be("B1");
            proposal.TotalQuantity.Should().Be(group.TotalQuantity);
            proposal.MeasuredUnit.Should().Be(group.MeasuredUnit);
            proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
            proposal.EvidenceKind.Should().Be("heuristic-cad-metadata");
            proposal.Reasons.Should().Contain(reason => reason.Contains("cad_block_name_effective='BENCH'"));
            proposal.Reasons.Should().NotContain(reason => reason.StartsWith("שם השכבה"));
            snapshot.Prices.Should().BeEmpty("a missing price is not invented by proposal generation");
        }

        [Fact]
        public void NamedLineType_CanSearchUnknownLayer_WithoutBypassingSubjectSafety()
        {
            var snapshot = Catalog(("P1", "הנחת צינור מים", "מטר"), ("S1", "אטמי מים", "מטר"));
            var group = Group("Q742", "מטר", Field("cad_layer_linetype", "WATER"));

            MappingProposalEngine.Propose(new[] { group }, snapshot)
                .Should().ContainSingle(proposal => proposal.ProposedCode == "P1");
        }

        [Fact]
        public void UninformativeNamesColorsAndWidths_DoNotManufactureMeaning()
        {
            var snapshot = Catalog(("B1", "ספסל רחוב", "יח'"));
            var group = Group("Q742", "יח'", Field("cad_block_name_effective", "*U123"),
                Field("cad_layer_linetype", "Continuous"), Field("cad_entity_color_index", "301"),
                Field("cad_polyline_constant_width_raw", "0.15"));

            MappingProposalEngine.Propose(new[] { group }, snapshot, new[] { "B1" }).Should().BeEmpty();
        }

        [Fact]
        public void MixedBlockNames_DoNotBorrowTheFirstRecordAsAGroupSubject()
        {
            var measurements = new[] { Measurement("BENCH"), Measurement("TREE") };
            var group = new MappingProposalEngine.DiscoveredGroup("layer:Q742|count", "Q742",
                "count", "יח'", 2, 2, QuantityCadMetadataPolicy.Summarize(measurements));

            MappingProposalEngine.Propose(new[] { group }, Catalog(
                ("B1", "ספסל רחוב", "יח'"), ("T1", "עץ", "יח'"))).Should().BeEmpty();
        }

        [Fact]
        public void IncompleteMetadataCoverage_IsNotEvidenceForEveryObject()
        {
            var group = Group("Q742", "יח'", new QuantityCadMetadataPolicy.FieldSummary(
                "cad_block_name_effective", new[] { "BENCH" }, 1, 2));

            MappingProposalEngine.Propose(new[] { group }, Catalog(("B1", "ספסל רחוב", "יח'")))
                .Should().BeEmpty();
        }

        [Fact]
        public void InconsistentCoverageCount_IsNotACompleteGroupObservation()
        {
            var group = Group("Q742", "יח'", new QuantityCadMetadataPolicy.FieldSummary(
                "cad_block_name_effective", new[] { "BENCH" }, 0, 1));

            MappingProposalEngine.Propose(new[] { group }, Catalog(("B1", "ספסל רחוב", "יח'")))
                .Should().BeEmpty();
        }

        [Fact]
        public void ContradictoryStableSubjects_DoNotPickOneArbitrarily()
        {
            var group = Group("Q742", "יח'", Field("cad_block_name_effective", "BENCH"),
                Field("cad_layer_linetype", "WATER"));

            MappingProposalEngine.Propose(new[] { group }, Catalog(
                ("B1", "ספסל רחוב", "יח'"), ("W1", "מגוף מים", "יח'"))).Should().BeEmpty();
        }

        [Fact]
        public void MetadataCannotOverrideAnInformativeContradictoryLayer()
        {
            var group = Group("WATER", "יח'", Field("cad_block_name_effective", "BENCH"));

            MappingProposalEngine.Propose(new[] { group }, Catalog(("B1", "ספסל רחוב", "יח'")))
                .Should().BeEmpty();
        }

        [Fact]
        public void MetadataSearch_NeverCrossesTheMeasuredUnit()
        {
            var group = Group("Q742", "מטר", Field("cad_block_name_effective", "BENCH"));

            MappingProposalEngine.Propose(new[] { group }, Catalog(("B1", "ספסל רחוב", "יח'")))
                .Should().BeEmpty();
        }

        [Fact]
        public void EffectiveBlockNameIsPreferredToRawDynamicDefinitionName()
        {
            var group = Group("Q742", "יח'", Field("cad_block_name_effective", "BENCH"),
                Field("cad_block_name_raw", "TREE"));

            MappingProposalEngine.Propose(new[] { group }, Catalog(
                ("B1", "ספסל רחוב", "יח'"), ("T1", "עץ", "יח'")))
                .Should().ContainSingle(proposal => proposal.ProposedCode == "B1");
        }

        [Fact]
        public void RawBlockNameCanHelpWhenNoEffectiveNameWasCaptured()
        {
            var group = Group("Q742", "יח'", Field("cad_block_name_raw", "BENCH"));

            MappingProposalEngine.Propose(new[] { group }, Catalog(("B1", "ספסל רחוב", "יח'")))
                .Should().ContainSingle(proposal => proposal.ProposedCode == "B1");
        }

        [Fact]
        public void ExplicitSignNumber_RanksMatchingCatalogText_WithoutUsingColorNumbers()
        {
            var snapshot = Catalog(("A303", "תמרור 303", "יח'"), ("Z301", "תמרור 301", "יח'"));
            var group = Group("Q742", "יח'", Field("cad_block_name_effective", "SIGN-301"),
                Field("cad_entity_color_index", "303"));

            var proposals = MappingProposalEngine.Propose(new[] { group }, snapshot);

            proposals[0].ProposedCode.Should().Be("Z301");
            proposals[0].Score.Should().BeGreaterThan(proposals.Single(p => p.ProposedCode == "A303").Score);
            proposals[0].Reasons.Should().Contain(reason => reason.Contains("(301)"));
        }

        [Fact]
        public void ExistingLayerRemovalDirection_RemainsIntactAfterMetadataNamesAreJoined()
        {
            var group = Group("CURB-EX", "מטר", Field("cad_layer_linetype", "CURB"));

            MappingProposalEngine.Propose(new[] { group }, Catalog(
                ("N1", "אבן שפה", "מטר"), ("R1", "פירוק אבני שפה", "מטר")))
                .Should().ContainSingle(proposal => proposal.ProposedCode == "R1");
        }

        [Fact]
        public void MixedMetadata_DoesNotRemoveIndependentLayerEvidence_ButExplainsItsLimitation()
        {
            var group = Group("BENCH", "יח'", new QuantityCadMetadataPolicy.FieldSummary(
                "cad_block_name_effective", new[] { "BENCH", "TREE" }, 0, 2));
            var proposal = MappingProposalEngine.Propose(new[] { group }, Catalog(("B1", "ספסל רחוב", "יח'")))
                .Should().ContainSingle().Subject;

            proposal.EvidenceKind.Should().Be("heuristic");
            proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
            proposal.Reasons.Should().Contain(reason => reason.Contains("מעורבים או חסרים לא שימשו"));
        }

        private static MappingProposalEngine.DiscoveredGroup Group(string layer, string unit,
            params QuantityCadMetadataPolicy.FieldSummary[] fields) =>
            new($"layer:{layer}|count", layer, unit == "יח'" ? "count" : "length", unit, 2, 2, fields);

        private static QuantityCadMetadataPolicy.FieldSummary Field(string key, string value) =>
            new(key, new[] { value }, 0, 2);

        private static QuantityMeasurement Measurement(string blockName) => new()
        {
            Kind = "count", Method = "block-count", Unit = "יח'", RawValue = 1,
            Parameters = { ["cad_block_name_effective"] = blockName },
        };

        private static CatalogSnapshot Catalog(params (string Code, string Description, string Unit)[] items) => new()
        {
            SnapshotId = "metadata-test-book", FileHash = new string('a', 64),
            Items = items.ToDictionary(item => item.Code, item => new CatalogItem
            {
                Code = item.Code, Description = item.Description, UnitRaw = item.Unit,
            }, StringComparer.OrdinalIgnoreCase),
        };
    }
}
