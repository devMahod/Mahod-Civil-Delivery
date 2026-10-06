using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MappingProposalRoleSafetyTests
{
    // Saved full scan estimate-extract-20260924-170520-0aebd0fe:
    // 62 objects / 2,851.678 m had a score-1,000,001 profile-default kerb
    // followed by two score-125 road markings. This tests subject compatibility,
    // not the unverified paint width/material or a native quantity takeoff.
    [Fact]
    public void RecordedBikeMarking_RejectsPreferredKerbAndRetainsAmbiguousSameUnitMarkings()
    {
        var group = new MappingProposalEngine.DiscoveredGroup(
            "layer:TR-MARK-WHT-812-BIKE|length",
            "6422-SM-MODEL-NATAZ|TR-MARK-WHT-812-BIKE", "length", "מטר", 62, 2851.678);
        var before = JsonSerializer.Serialize(group);
        var catalog = Catalog(
            ("U51.06.2930", "אבן שפה לשביל אופניים במידות 50/30/25 ס\"מ", "מטר"),
            ("U51.32.0210", "קו ניתוב ברוחב 10 ס\"מ בחומר סימון ארוך קיים דו רכיבי", "מטר"),
            ("U51.32.0270", "קו ניתוב כפול ברוחב 10 ס\"מ כל אחד בחומר סימון ארוך קיים", "מטר"));

        // CuratedRuleProposals invokes this same public guard before assigning
        // its million-point preference. A profile default is not an exemption.
        MappingProposalEngine.IsSemanticallyCompatible(group.Layer,
            catalog.Items["U51.06.2930"].Description).Should().BeFalse();
        var proposals = MappingProposalEngine.Propose(new[] { group }, catalog,
            new[] { "U51.06.2930", "U51.32.0210", "U51.32.0270" });

        proposals.Select(proposal => proposal.ProposedCode).Should().BeEquivalentTo(
            new[] { "U51.32.0210", "U51.32.0270" });
        proposals.Should().OnlyContain(proposal => proposal.Status == "PROPOSED_UNAPPROVED" &&
            proposal.ObjectCount == 62 && proposal.TotalQuantity == 2851.678 &&
            proposal.MeasuredUnit == "מטר");
        catalog.Prices.Should().BeEmpty();
        JsonSerializer.Serialize(group).Should().Be(before);
    }

    [Theory]
    [InlineData("TR-MARK-WHT-812-BIKE")]
    [InlineData("PARENT|CHILD|tr-mark-wht-812-bike")]
    [InlineData("WATER\nXREF|TR-MARK-WHT-812-BIKE")]
    public void ExplicitMarkingRole_RejectsBothKerbWordingsEvenWithIncidentalMarkingText(string source)
    {
        MappingProposalEngine.IsSemanticallyCompatible(source,
            "אבן שפה לשביל אופניים כולל סימון").Should().BeFalse();
        MappingProposalEngine.IsSemanticallyCompatible(source,
            "אבני שפה לשביל אופניים כולל סימון").Should().BeFalse();
    }

    [Theory]
    [InlineData("HW-BIKE-LANE")]
    [InlineData("PL-BIKE")]
    [InlineData("TR-MARK-WHT-812-BIKE|HW-BIKE-LANE")]
    [InlineData("XREF|TR-MARKET-BIKE")]
    public void RealBikeEdgesAndUnevidencedNames_AreNotSuppressed(string source)
    {
        MappingProposalEngine.IsSemanticallyCompatible(source,
            "אבן שפה לשביל אופניים").Should().BeTrue();
    }

    [Fact]
    public void RecordedKerbPainting_IsStillMarkingWorkNotPhysicalKerbSupply()
    {
        var group = new MappingProposalEngine.DiscoveredGroup(
            "layer:TR-MARK-WHT-811|length", "6422-SM-MODEL-NATAZ|TR-MARK-WHT-811",
            "length", "מטר", 10, 100);
        const string painting = "צביעת אבני שפה בצבע בגוונים שונים";
        MappingProposalEngine.IsSemanticallyCompatible(group.Layer, painting).Should().BeTrue();
        MappingProposalEngine.Propose(new[] { group }, Catalog(
            ("U51.32.0700", painting, "מטר"),
            ("CURB", "אבן שפה כולל צביעת אבני שפה וסימון", "מטר")))
            .Should().ContainSingle(proposal => proposal.ProposedCode == "U51.32.0700");
    }

    [Theory]
    [InlineData("layer:CURB-EXST|count|block:S_POINT_E", "CURB-EXST", "count", "יח'")]
    [InlineData("layer:S_CURB|count|block:SURVEY%7CS_POINT_E", "SURVEY|S_CURB", "count", "יח'")]
    [InlineData("layer:1000+225-EX+W|length", "1000+225-EX+W", "length", "מטר")]
    [InlineData("layer:HW-CS-TABL|length", "HW-CS-TABL", "length", "מטר")]
    public void ProvenSurveyAndReferenceGeometry_StillAbstainsWithoutChangingMeasurements(
        string key, string layer, string kind, string unit)
    {
        var group = new MappingProposalEngine.DiscoveredGroup(key, layer, kind, unit, 12, 12);
        var before = JsonSerializer.Serialize(group);
        MappingProposalEngine.IsProposalEligible(group).Should().BeFalse();
        MappingProposalEngine.Propose(new[] { group }, Catalog(
            ("C1", "פירוק אבני שפה", unit), ("C2", "אבן שפה", unit)))
            .Should().BeEmpty();
        JsonSerializer.Serialize(group).Should().Be(before);
    }

    [Theory]
    [InlineData("WATER 8Z", "W1", "הנחת צינור מים בקוטר 100 מ\"מ", "W2", "הנחת צינור מים בקוטר 200 מ\"מ")]
    [InlineData("S_ELEC", "E1", "כבל חשמל בחתך 16 ממ\"ר", "E2", "כבל חשמל בחתך 25 ממ\"ר")]
    public void VagueUtilities_RetainUsefulAmbiguityWithoutInventingDimensionsOrPrices(
        string layer, string firstCode, string firstDescription, string secondCode, string secondDescription)
    {
        var group = new MappingProposalEngine.DiscoveredGroup(
            $"layer:{layer}|length", layer, "length", "מטר", 10, 100);
        var catalog = Catalog((firstCode, firstDescription, "מטר"), (secondCode, secondDescription, "מטר"));
        var proposals = MappingProposalEngine.Propose(new[] { group }, catalog);
        proposals.Select(proposal => proposal.ProposedCode).Should().BeEquivalentTo(new[] { firstCode, secondCode });
        proposals.Select(proposal => proposal.Score).Distinct().Should().ContainSingle();
        proposals.Should().OnlyContain(proposal => proposal.Status == "PROPOSED_UNAPPROVED" &&
            proposal.TotalQuantity == 100 && proposal.MeasuredUnit == "מטר");
        catalog.Prices.Should().BeEmpty();
    }

    private static CatalogSnapshot Catalog(params (string Code, string Description, string Unit)[] items) => new()
    {
        SnapshotId = "role-safety-test", FileHash = new string('a', 64),
        Items = items.ToDictionary(item => item.Code, item => new CatalogItem
        {
            Code = item.Code, Description = item.Description, UnitRaw = item.Unit,
        }, StringComparer.OrdinalIgnoreCase),
    };
}
