using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class SurveyPavementMarkerProposalTests
{
    private const string Survey = "6422-SP-MEDVA-ALL-2026-MHD";
    private const string Rule = "layer:S_PAVEMENT_UP|count|block:" + Survey + "%7CS_POINT_E%2B";

    [Fact]
    public void Native76Exact2330Group_AbstainsWithoutChangingQuantityClassificationOrSource()
    {
        // estimate-extract-20260922-122204-e0ed46fb: q-disc-336457-22A3B-count.
        // This is evidence for proposal abstention, NOT permission to exclude a quantity.
        var group = ActualGroup();
        var before = JsonSerializer.Serialize(group);
        var significance = new QuantitySignificance.Group(group.RuleKey, group.Layer,
            group.MeasuredUnit, group.TotalQuantity, group.ObjectCount);

        QuantitySignificance.Classify(significance).Kind.Should().Be(QuantitySignificance.Kind.Quantity);
        MappingProposalEngine.IsProposalEligible(group).Should().BeFalse();
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
        JsonSerializer.Serialize(group).Should().Be(before);
        group.ObjectCount.Should().Be(2330);
        group.TotalQuantity.Should().Be(2330);
        group.RuleKey.Should().Be(Rule);
    }

    [Fact]
    public async Task Native76Marker_IsNotSentToSemanticProviderEvenWithPavingContext()
    {
        var provider = new UnexpectedProvider();
        var group = ActualGroup();
        var before = JsonSerializer.Serialize(group);
        var result = await new SemanticMappingAssist(provider).AssistAsync(
            group, Catalog(), "ריצוף", default);
        result.IsAbstained.Should().BeTrue();
        result.Proposals.Should().BeEmpty();
        provider.Calls.Should().Be(0);
        JsonSerializer.Serialize(group).Should().Be(before);
    }

    [Theory]
    [InlineData("PARENT|CHILD|S_PAVEMENT_UP", "PARENT|CHILD|S_POINT_E+")]
    [InlineData("Parent|Child|s_pavement_up", "PARENT|CHILD|s_point_e+")]
    public void CompleteMatchingNamespaceRetainsTheExactObservedSubjectGuard(string layer, string block)
    {
        var leaf = layer[(layer.LastIndexOf('|') + 1)..];
        var group = ActualGroup() with
        {
            Layer = layer, RuleKey = $"layer:{leaf}|count|block:{Uri.EscapeDataString(block)}",
        };
        MappingProposalEngine.IsProposalEligible(group).Should().BeFalse();
    }

    [Theory]
    [InlineData("S_PAVEMENT_UP", "S_POINT_E+")]
    [InlineData("ACTUAL|S_PAVEMENT_UP", "OTHER|S_POINT_E+")]
    [InlineData("PARENT|CHILD|S_PAVEMENT_UP", "CHILD|S_POINT_E+")]
    [InlineData("PARENT||CHILD|S_PAVEMENT_UP", "PARENT||CHILD|S_POINT_E+")]
    [InlineData("PARENT| CHILD|S_PAVEMENT_UP", "PARENT| CHILD|S_POINT_E+")]
    [InlineData("ACTUAL|S_PAVEMENT_UP", "ACTUAL|S_POINT_EG")]
    [InlineData("ACTUAL|S_PAVEMENT_UP", "ACTUAL|S_POINT_E++")]
    [InlineData("ACTUAL|S_PAVEMENT_UP", "ACTUAL|PAVING_UNIT")]
    [InlineData("ACTUAL|S_PAVEMENT_OTHER", "ACTUAL|S_POINT_E+")]
    public void UnevidencedFamiliesAndDifferentNamespacesAreNotSilentlyReclassified(string layer, string block)
    {
        var leaf = layer[(layer.LastIndexOf('|') + 1)..];
        var group = ActualGroup() with
        {
            Layer = layer, RuleKey = $"layer:{leaf}|count|block:{Uri.EscapeDataString(block)}",
        };
        MappingProposalEngine.IsProposalEligible(group).Should().BeTrue();
        QuantitySignificance.Classify(new(group.RuleKey, group.Layer, group.MeasuredUnit,
            group.TotalQuantity, group.ObjectCount)).Kind.Should().Be(QuantitySignificance.Kind.Quantity);
    }

    [Theory]
    [InlineData("layer:OTHER|count|block:" + Survey + "%7CS_POINT_E%2B")]
    [InlineData("layer:S_PAVEMENT_UP|area|block:" + Survey + "%7CS_POINT_E%2B")]
    [InlineData(Rule + "|extra")]
    public void ContradictoryKeysDoNotTriggerThisNarrowSubjectGuard(string rule)
    {
        MappingProposalEngine.IsProposalEligible(ActualGroup() with { RuleKey = rule }).Should().BeTrue();
    }

    [Fact]
    public void RealConstructionSubjectsStillGetUnapprovedSameUnitSuggestions()
    {
        var curb = new MappingProposalEngine.DiscoveredGroup(
            "layer:HW-CURB|length", "HW-CURB", "length", "מטר", 297, 29287.62954513264);
        var paving = new MappingProposalEngine.DiscoveredGroup(
            "layer:PAVEMENT|count|block:PAVING_UNIT", "PAVEMENT", "count", "יח'", 8, 8);
        MappingProposalEngine.Propose(new[] { curb }, Catalog()).Should().ContainSingle()
            .Which.ProposedCode.Should().Be("U51.06.1900");
        MappingProposalEngine.Propose(new[] { paving }, Catalog()).Should().Contain(p =>
            p.ProposedCode == "TEST-PAVING-UNIT" && p.Status == "PROPOSED_UNAPPROVED");
    }

    private static MappingProposalEngine.DiscoveredGroup ActualGroup() =>
        new(Rule, Survey + "|S_PAVEMENT_UP", "count", "יח'", 2330, 2330);

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "TEST-ONLY", FileHash = new string('a', 64) };
        foreach (var item in new[]
        {
            new CatalogItem { Code = "U51.06.4520", UnitRaw = "יח'",
                Description = "סורג מאלומיניום לפתח ערוגה של עץ קיים במצב בו עץ קיים נמוך ממפלס ריצוף מתוכנן. לפי פרט אדריכלי." },
            new CatalogItem { Code = "U51.06.1900", UnitRaw = "מטר", Description = "אבן שפה" },
            new CatalogItem { Code = "TEST-PAVING-UNIT", UnitRaw = "יח'", Description = "יחידת ריצוף" },
        }) catalog.Items.Add(item.Code, item);
        return catalog;
    }

    private sealed class UnexpectedProvider : ISemanticMappingProvider
    {
        public int Calls;
        public Task<SemanticSearchResponse> SuggestSearchTermsAsync(SemanticSearchRequest request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Survey marker must abstain locally");
        }
        public Task<SemanticRankResponse> RankCandidatesAsync(SemanticRankRequest request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Survey marker must abstain locally");
        }
    }
}
