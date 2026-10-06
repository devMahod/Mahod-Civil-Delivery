using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class WallMountingSubjectProposalTests
{
    private static readonly MappingProposalEngine.DiscoveredGroup Recorded = new(
        "layer:S_WALL_BT|count|block:6422-SP-MEDVA-ALL-2026-MHD%7CS_POINT_KR",
        "6422-SP-MEDVA-ALL-2026-MHD|S_WALL_BT", "count", "יח'", 2415, 2415);

    [Fact]
    public void Recorded2415WallPoints_AbstainBeforeAnyIncidentalWallMountedSubjectIsRanked()
    {
        var before = JsonSerializer.Serialize(Recorded);
        var catalog = Catalog();
        MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(Recorded).Should().BeFalse();
        MappingProposalEngine.Propose(new[] { Recorded }, catalog).Should().BeEmpty();
        MappingProposalEngine.IsProposalEligible(Recorded).Should().BeTrue(
            "the wrong subject does not establish survey-noise or authorize exclusion");
        QuantitySignificance.Classify(new(Recorded.RuleKey, Recorded.Layer, Recorded.MeasuredUnit,
            Recorded.TotalQuantity, Recorded.ObjectCount)).Kind.Should().Be(QuantitySignificance.Kind.Quantity);
        JsonSerializer.Serialize(Recorded).Should().Be(before);
    }

    [Theory]
    [InlineData("CABINET", "U08.06.5510")]
    [InlineData("POLE", "U08.10.2400")]
    [InlineData("LIGHT", "U08.10.3121")]
    [InlineData("BENCH", "TEST-BENCH")]
    public void ExplicitCompleteAssetMetadata_CanIdentifyActualWallMountedWork(string asset, string expected)
    {
        var group = Recorded with
        {
            CadMetadata = new[] { new QuantityCadMetadataPolicy.FieldSummary(
                "cad_block_name_effective", new[] { asset }, 0, Recorded.ObjectCount) },
        };
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should()
            .Contain(proposal => proposal.ProposedCode == expected && proposal.Status == "PROPOSED_UNAPPROVED");
        if (asset == "CABINET") MappingProposalEngine.Propose(new[] { group }, Catalog())
            .Should().ContainSingle(proposal => proposal.ProposedCode == expected);
    }

    [Fact]
    public async Task ExplicitEngineerAssetContext_RestoresUnapprovedAiSuggestionsWithMatchingPrimarySubject()
    {
        var provider = new CabinetProvider();
        var before = JsonSerializer.Serialize(Recorded);
        var result = await new SemanticMappingAssist(provider).AssistAsync(
            Recorded, Catalog(), "זה ארון חשמל", default);
        result.IsAbstained.Should().BeFalse();
        result.Proposals.Should().ContainSingle(proposal => proposal.ProposedCode == "U08.06.5510" &&
            proposal.Status == "PROPOSED_UNAPPROVED");
        provider.Calls.Should().Be(2);
        JsonSerializer.Serialize(Recorded).Should().Be(before);
        SemanticMappingAssist.IsCandidateCompatible(Recorded, "גוף תאורה על קיר", "זה ארון חשמל").Should().BeFalse();
    }

    [Theory]
    [InlineData(2414, new[] { "CABINET" })]
    [InlineData(2415, new[] { "CABINET", "S_POINT_KR" })]
    public void IncompleteOrMixedAssetHintsCannotOverrideWallOnlySubject(int count, string[] names)
    {
        var group = Recorded with
        {
            CadMetadata = new[] { new QuantityCadMetadataPolicy.FieldSummary(
                "cad_block_name_effective", names, 0, count) },
        };
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("cad_entity_linetype", "LIGHT")]
    [InlineData("cad_layer_linetype", "CABINET")]
    [InlineData("cad_block_name_effective", "CABINET|S_POINT_KR")]
    [InlineData("cad_block_name_effective", "WALL")]
    [InlineData("cad_block_name_effective", "CONC")]
    public void LineMaterialOrNamespaceHints_DoNotIdentifyTheCountedAsset(string key, string name)
    {
        var group = Recorded with { CadMetadata = new[] {
            new QuantityCadMetadataPolicy.FieldSummary(key, new[] { name }, 0, Recorded.ObjectCount) } };
        MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(group).Should().BeFalse();
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
    }

    [Fact]
    public void DirectAssetLayerIdentityAndActualWallLengthRemainUseful()
    {
        MappingProposalEngine.Propose(new[] { Recorded with { Layer = "S_WALL_LIGHT" } }, Catalog())
            .Should().Contain(proposal => proposal.ProposedCode == "U08.10.3121");
        var wall = Recorded with { MeasurementKind = "length", MeasuredUnit = "מטר" };
        var catalog = Catalog();
        catalog.Items.Add("TEST-WALL", new CatalogItem {
            Code = "TEST-WALL", Description = "קיר תומך מבטון", UnitRaw = "מטר" });
        MappingProposalEngine.Propose(new[] { wall }, catalog)
            .Should().ContainSingle(proposal => proposal.ProposedCode == "TEST-WALL");
    }

    [Fact]
    public async Task WallOnlyCount_AssistanceAbstainsBeforeProviderWithoutInventingAnAsset()
    {
        var provider = new UnexpectedProvider();
        var result = await new SemanticMappingAssist(provider).AssistAsync(
            Recorded, Catalog(), "קיר", default);
        result.IsAbstained.Should().BeTrue();
        result.Proposals.Should().BeEmpty();
        provider.Calls.Should().Be(0);
        SemanticMappingAssist.IsCandidateCompatible(Recorded, "גוף תאורה על קיר", "קיר")
            .Should().BeFalse();
    }

    [Fact]
    public void WallConstructionAndUnknownUtilityBlocksRemainAvailableWithoutInventedSurveyFact()
    {
        MappingProposalEngine.IsSemanticallyCompatible("S_WALL_BT", "קיר תומך מבטון כולל עמוד חיזוק").Should().BeTrue();
        MappingProposalEngine.IsSemanticallyCompatible("S_ELEC", "עמוד חשמל מותקן על קיר").Should().BeTrue();
        MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(Recorded with { Layer = "CABINET|S_WALL_BT" }).Should().BeFalse(
            "an XREF name is not positive asset evidence");
        MappingProposalEngine.IsProposalEligible(Recorded with
        {
            RuleKey = "layer:S_ELEC|count|block:S_POINT_KR", Layer = "S_ELEC",
        }).Should().BeTrue();
    }

    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "wall-subject-test", FileHash = new string('a', 64),
        Items = new[]
        {
            new CatalogItem { Code = "U08.06.5510", Description = "ארון דגם C-64 מותקן על גבי קיר או בתוך גומחת בטון", UnitRaw = "יח'" },
            new CatalogItem { Code = "U08.10.2400", Description = "עמוד מפלדה מיוחד מותאם להתקנה על גבי מעקה קיר בטון מזויין", UnitRaw = "יח'" },
            new CatalogItem { Code = "U08.10.2401", Description = "עמוד מפלדה מיוחד מותקן על קיר בטון בגובה 11.2 מ'", UnitRaw = "יח'" },
            new CatalogItem { Code = "U08.10.3121", Description = "גוף תאורה מוגן מים להתקנה על קיר", UnitRaw = "יח'" },
            new CatalogItem { Code = "U08.10.3157", Description = "גוף תאורה להתקנה על קיר עם נורת לד", UnitRaw = "יח'" },
            new CatalogItem { Code = "U08.10.3158", Description = "גוף תאורה להתקנה על קיר או תקרה", UnitRaw = "יח'" },
            new CatalogItem { Code = "TEST-UNKNOWN-ASSET", Description = "אביזר עתידי כלשהו שמוזכר בו קיר", UnitRaw = "יח'" },
            new CatalogItem { Code = "TEST-BENCH", Description = "ספסל מחובר על קיר", UnitRaw = "יח'" },
        }.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase),
    };

    private sealed class UnexpectedProvider : ISemanticMappingProvider
    {
        public int Calls;
        public Task<SemanticSearchResponse> SuggestSearchTermsAsync(SemanticSearchRequest request, CancellationToken ct)
        {
            Calls++; throw new InvalidOperationException("Unknown counted asset must abstain locally");
        }
        public Task<SemanticRankResponse> RankCandidatesAsync(SemanticRankRequest request, CancellationToken ct)
        {
            Calls++; throw new InvalidOperationException("Unknown counted asset must abstain locally");
        }
    }

    private sealed class CabinetProvider : ISemanticMappingProvider
    {
        public int Calls;
        public Task<SemanticSearchResponse> SuggestSearchTermsAsync(SemanticSearchRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SemanticSearchResponse(request.ContextId,
                new[] { new SemanticSearchTerm("ארון", "engineer_context", "ארון חשמל") }));
        }
        public Task<SemanticRankResponse> RankCandidatesAsync(SemanticRankRequest request, CancellationToken ct)
        {
            Calls++;
            request.Candidates.Should().ContainSingle(item => item.Code == "U08.06.5510");
            return Task.FromResult(new SemanticRankResponse(request.ContextId, request.CatalogHash,
                new[] { new SemanticRankedCode("U08.06.5510", "ארון לפי התיאור המפורש", new[] { "engineer_context" }) }));
        }
    }
}
