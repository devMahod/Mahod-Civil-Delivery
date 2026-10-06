using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class PavingMeasurementSubjectProposalTests
{
    // Full saved170520 records: 168 S_POINT_N inserts and 97 3D polylines.
    // Names and ByLayer/Continuous do not identify counted furniture or an edge.
    // These tests do not classify the symbols as noise or authorize exclusion.
    [Theory]
    [InlineData("count", "יח'", 168, 168)]
    [InlineData("length", "מטר", 97, 868.256)]
    public void RecordedPavingWithoutAssetIdentity_AbstainsWithoutDroppingMeasuredRecords(
        string kind, string unit, int count, double quantity)
    {
        var group = Group(kind, unit, count, quantity);
        var before = JsonSerializer.Serialize(group);
        MappingProposalEngine.IsProposalEligible(group).Should().BeTrue();
        MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(group).Should().BeFalse();
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
        QuantitySignificance.Classify(new(group.RuleKey, group.Layer, unit, quantity, count))
            .Kind.Should().Be(QuantitySignificance.Kind.Quantity);
        JsonSerializer.Serialize(group).Should().Be(before);
    }

    [Fact]
    public void GenuinePavingAreaRetainsSameUnitUnapprovedPavingCandidate()
    {
        var group = Group("area", "מ\"ר", 97, 450);
        MappingProposalEngine.Propose(new[] { group }, Catalog())
            .Should().ContainSingle(p => p.ProposedCode == "PAVING-AREA" && p.Status == "PROPOSED_UNAPPROVED");
    }

    [Fact]
    public void ExplicitSharedKerbLinetypeRestoresLinearEdge_NotOpeningSurchargeOrOtherSubject()
    {
        var group = Group("length", "מטר", 97, 868.256) with
        {
            CadMetadata = new[] { Field("cad_layer_linetype", "CURB", 97) },
        };
        MappingProposalEngine.Propose(new[] { group }, Catalog())
            .Should().ContainSingle(p => p.ProposedCode == "U51.06.1800" && p.Status == "PROPOSED_UNAPPROVED");
    }

    [Fact]
    public void ExplicitSharedBenchBlockRestoresCountedBench_NotTreeGrilleMentioningPaving()
    {
        var group = Group("count", "יח'", 168, 168) with
        {
            CadMetadata = new[] { Field("cad_block_name_effective", "BENCH", 168) },
        };
        MappingProposalEngine.Propose(new[] { group }, Catalog())
            .Should().ContainSingle(p => p.ProposedCode == "BENCH" && p.Status == "PROPOSED_UNAPPROVED");
    }

    [Theory]
    [InlineData("Continuous")]
    [InlineData("PAVE")]
    [InlineData("CONC")]
    [InlineData("CURB|Continuous")]
    public void MaterialPavingAndXrefNamesCannotSupplyLinearEdgeIdentity(string name)
    {
        var group = Group("length", "מטר", 97, 868.256) with
        {
            CadMetadata = new[] { Field("cad_layer_linetype", name, 97) },
        };
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
    }

    [Fact]
    public void ValidatedEngineerMeaningCanRestoreMatchingLinearSubjectButNotIncidentalPavingItems()
    {
        var group = Group("length", "מטר", 97, 868.256);
        SemanticMappingAssist.IsCandidateCompatible(group, "אבן שפה מבטון", "זו אבן שפה")
            .Should().BeTrue();
        SemanticMappingAssist.IsCandidateCompatible(group, "תוספת לפתיחת פתח בריצוף", "זו אבן שפה")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("PAVEMENT", "PAVING_UNIT")]
    [InlineData("ACTUAL|PAVEMENT", "ACTUAL|PAVING_UNIT")]
    [InlineData("Parent|Child|PAVEMENT", "PARENT|CHILD|PAVING_UNIT")]
    [InlineData("Parent Source|PAVEMENT", "PARENT_SOURCE|PAVING_UNIT")]
    public void CompleteCanonicalNamedBlockKey_RestoresOnlyCountedPavingUnitWithoutCadMetadata(
        string layer, string block)
    {
        var group = KeyGroup(layer, block);
        var before = JsonSerializer.Serialize(group);
        MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(group).Should().BeTrue();
        var proposal = MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().ContainSingle().Which;
        proposal.ProposedCode.Should().Be("PAVING-UNIT");
        proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
        proposal.TotalQuantity.Should().Be(8);
        JsonSerializer.Serialize(group).Should().Be(before);
    }

    [Fact]
    public void CanonicalNamedBenchBlockKey_RetainsOtherPositiveCountedAssetWithoutCadMetadata()
    {
        MappingProposalEngine.Propose(new[] { KeyGroup("PAVEMENT", "BENCH") }, Catalog())
            .Should().ContainSingle(proposal => proposal.ProposedCode == "BENCH" &&
                proposal.Status == "PROPOSED_UNAPPROVED");
    }

    [Theory]
    [InlineData("layer:OTHER|count|block:PAVING_UNIT", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|length|block:PAVING_UNIT", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|name:PAVING_UNIT", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVING_UNIT|extra", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:%50AVING_UNIT", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVING_UNIT%", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVING_UNIT%20", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVING_UNIT%00", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:OTHER%7CPAVING_UNIT", "ACTUAL|PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:CHILD%7CPAVING_UNIT", "PARENT|CHILD|PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVING_UNIT", "ACTUAL|PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:ACTUAL%7CPAVING_UNIT", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:ACTUAL%7C%7CPAVING_UNIT", "ACTUAL||PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:ACTUAL%257CPAVING_UNIT", "ACTUAL|PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVING_UNIT%7CS_POINT_N", "PAVING_UNIT|PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:PAVE", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:S_POINT_N", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:S_POINT_E", "PAVEMENT")]
    [InlineData("layer:PAVEMENT|count|block:S_POINT_KR", "PAVEMENT")]
    public void MalformedMismatchedOrSubjectlessCountKey_CannotInventPavingUnit(string rule, string layer)
    {
        var group = KeyGroup(layer, "PAVING_UNIT") with { RuleKey = rule };
        MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(group).Should().BeFalse();
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
    }

    [Fact]
    public void NamedKeyDoesNotOverrideObservedBlockMetadataOrIncompleteCoverage()
    {
        foreach (var field in new[]
        {
            Field("cad_block_name_effective", "S_POINT_N", 8),
            Field("cad_block_name_effective", "PAVING_UNIT", 7),
        })
        {
            var group = KeyGroup("PAVEMENT", "PAVING_UNIT") with { CadMetadata = new[] { field } };
            MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(group).Should().BeFalse();
            MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("יחידת ריצוף", true)]
    [InlineData("אספקת יחידת ריצוף", true)]
    [InlineData("ריצוף", false)]
    [InlineData("יחידת ריצופים", false)]
    [InlineData("סורג לעץ כולל יחידת ריצוף", false)]
    public void CountedPavingUnitRequiresTheMatchingPrimarySubject(string description, bool expected)
    {
        MappingProposalEngine.IsAutomaticMeasurementSubjectCompatible(
            KeyGroup("PAVEMENT", "PAVING_UNIT"), description).Should().Be(expected);
    }

    private static MappingProposalEngine.DiscoveredGroup KeyGroup(string layer, string block) => new(
        $"layer:{layer[(layer.LastIndexOf('|') + 1)..]}|count|block:{Uri.EscapeDataString(block)}",
        layer, "count", "יח'", 8, 8);

    private static MappingProposalEngine.DiscoveredGroup Group(string kind, string unit, int count, double quantity) => new(
        "layer:S_PAVED_PRIM|" + kind + (kind == "count" ? "|block:6422-SP-MEDVA-ALL-2026-MHD%7CS_POINT_N" : ""),
        "6422-SP-MEDVA-ALL-2026-MHD|S_PAVED_PRIM", kind, unit, count, quantity,
        kind == "count" ? new[] { Field("cad_block_name_effective", "6422-SP-MEDVA-ALL-2026-MHD|S_POINT_N", count) }
            : new[] { Field("cad_layer_linetype", "Continuous", count) });

    private static QuantityCadMetadataPolicy.FieldSummary Field(string key, string value, int count) =>
        new(key, new[] { value }, 0, count);

    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "paving-subject-test", FileHash = new string('a', 64),
        Items = new[]
        {
            new CatalogItem { Code = "U51.06.4520", Description = "סורג מאלומיניום לפתח ערוגה של עץ קיים במפלס ריצוף מתוכנן", UnitRaw = "יח'" },
            new CatalogItem { Code = "U41.01.0770", Description = "תוספת לפתיחת פתח לשרוול בריצוף", UnitRaw = "מטר" },
            new CatalogItem { Code = "U51.06.1800", Description = "אבן שפה מבטון לתיחום ריצוף", UnitRaw = "מטר" },
            new CatalogItem { Code = "U51.06.4780", Description = "פרופיל אלומיניום לתיחום ריצוף", UnitRaw = "מטר" },
            new CatalogItem { Code = "BENCH", Description = "ספסל רחוב", UnitRaw = "יח'" },
            new CatalogItem { Code = "PAVING-UNIT", Description = "יחידת ריצוף", UnitRaw = "יח'" },
            new CatalogItem { Code = "UNSPECIFIED-PAVING-COUNT", Description = "ריצוף ללא זהות פריט נספר", UnitRaw = "יח'" },
            new CatalogItem { Code = "PAVING-AREA", Description = "ריצוף באבנים משתלבות", UnitRaw = "מ\"ר" },
        }.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase),
    };
}
