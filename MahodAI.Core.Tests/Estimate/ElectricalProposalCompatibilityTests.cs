using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using System.Text.Json;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class ElectricalProposalCompatibilityTests
{
    // Full descriptions read via the unchanged PriceBookXlsxLoader from the real
    // NTI urban Aug2025 workbook, SHA256:
    // 90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c.
    // This is catalog vocabulary, not approval of any work or measurement.
    private const string SignalDescription = "קו הזנת חשמל למנגנון לרמזורים ממרכזת תאורה ו/או עמוד חח\"י הגשת תוכניות לחח\"י, כבל 5X10 N2XY מושחל בצינור בקוטר 100 מ\"מ,";
    private const string HighDescription = "השחלה או הנחה בלבד של כבלי חשמל מתח גבוה, מנחושת או אלומיניום אשר מסופקים ע\"י חברת חשמל לרבות הובלה ממחסני חברת החשמל , תאום עם חברת החשמל ופיקוח מטעמה, 3 גידים בדידים ימדדו ככבל קומפלט";
    private const string LowDescription = "השחלה או הנחה בלבד של כבלי חשמל מתח נמוך בחתכים שונים, מנחושת או אלומיניום אשר מסופקים ע\"י חברת חשמל לרבות הובלה ממחסני חברת החשמל ,  תאום עם חברת החשמל ופיקוח מטעמה";

    [Theory]
    [InlineData("HASHMAL-HV", "כבלי חשמל מתח נמוך", false)]
    [InlineData("HASHMAL-LV", "כבלי חשמל מתח גבוה", false)]
    [InlineData("HASHMAL-HV", "כבלי חשמל מתח גבוה", true)]
    [InlineData("HASHMAL-LV", "כבלי חשמל מתח נמוך", true)]
    [InlineData("HASHMAL-HV-LV", "כבלי חשמל מתח גבוה", false)]
    [InlineData("HASHMAL", "כבלי חשמל מתח נמוך", true)]
    [InlineData("HASHMAL-HV", "קו הזנת חשמל למנגנון לרמזורים", false)]
    [InlineData("HASHMAL-RAMZOR", "קו הזנת חשמל למנגנון לרמזורים", true)]
    [InlineData("HV|HASHMAL-LV", "כבלי חשמל מתח נמוך", true)]
    [InlineData("RAMZOR|HASHMAL-HV", "קו הזנת חשמל למנגנון לרמזורים", false)]
    [InlineData("HASHMAL-SHV123", "כבלי חשמל מתח נמוך", true)]
    [InlineData("HASHMAL-LVISH", "כבלי חשמל מתח גבוה", true)]
    [InlineData("HV", "כבלי חשמל מתח נמוך", true)]
    [InlineData("HASHMAL-HV", "כבלי מתח נמוך", false)]
    [InlineData("HASHMAL-HV", "כבלי מתח גבוה ונמוך", false)]
    [InlineData("HASHMAL-HV", "כבלי מתח גבוה ומתח נמוך", false)]
    [InlineData("HASHMAL-HV", "כבלי חשמל במתח נמוך", false)]
    [InlineData("HASHMAL-LV", "כבלי חשמל למתח גבוה", false)]
    [InlineData("HASHMAL-HV", "כבלי חשמל למתח גבוה ולמתח נמוך", false)]
    [InlineData("HASHMAL-LV", "כבלי חשמל במתח נמוך ובמתח גבוה", false)]
    [InlineData("HASHMAL-HV", "כבלי חשמל במתח גבוה", true)]
    [InlineData("HASHMAL-LV", "כבלי חשמל למתח נמוך", true)]
    [InlineData("HASHMAL-HV", "כבלי חשמל אבמתח נמוך", true)]
    [InlineData("HASHMAL-LV", "כבלי חשמל אלמתח גבוה", true)]
    public void ExplicitVoltageAndRoleEvidenceConstrainsSuggestionsOnly(string layer, string description, bool compatible)
    {
        MappingProposalEngine.IsSemanticallyCompatible(layer, description).Should().Be(compatible);
    }

    [Theory]
    [InlineData("ELEC")]
    [InlineData("ELECTRIC")]
    [InlineData("ELECTRICITY")]
    [InlineData("HASHMAL")]
    [InlineData("חשמל")]
    public void ElectricalAliasesAreKnownSubjects(string layer) =>
        MappingProposalEngine.HasKnownSubjectVocabulary(layer).Should().BeTrue();

    [Theory]
    [InlineData("DSFSDFDSF")]
    [InlineData("SELECTRICISH")]
    [InlineData("ELECTRICITYISH")]
    public void ArbitraryNamesAreNotElectricalSubjects(string layer) =>
        MappingProposalEngine.HasKnownSubjectVocabulary(layer).Should().BeFalse();

    [Fact]
    public void ElectricalRoleSuppliesContextWithoutChangingEngineeringData()
    {
        SemanticHintPolicy.TryContext(new("electricity", ""), out var context, out _).Should().BeTrue();
        context.Should().Be("חשמל");
        SemanticHintPolicy.Roles.Single(role => role.Id == "electricity").Label.Should().Be("חשמל");
    }

    [Theory]
    [InlineData("UT-3D|Hashmal-HV-line", "U08.06.6790")]
    [InlineData("UT-3D|HASHMAL-LV", "U08.06.6800")]
    public void LiveNamesCannotReceiveOppositeVoltageOrSignalFeed(string layer, string expectedCode)
    {
        var catalog = Catalog(); var before = JsonSerializer.Serialize(catalog);
        var result = MappingProposalEngine.Propose(new[] { Group(layer) }, catalog);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.ProposedCode.Should().Be(expectedCode);
        proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
        proposal.Reasons.Should().Contain(reason => reason.Contains("טרם אושרו"));
        JsonSerializer.Serialize(catalog).Should().Be(before);
    }

    [Theory]
    [InlineData("ELEC")]
    [InlineData("ELECTRIC")]
    [InlineData("ELECTRICITY")]
    [InlineData("HASHMAL")]
    [InlineData("חשמל")]
    public void UnknownVoltageRemainsAmbiguousInsteadOfInventingOne(string layer)
    {
        MappingProposalEngine.Propose(new[] { Group(layer) }, Catalog()).Select(proposal => proposal.ProposedCode)
            .Should().BeEquivalentTo(new[] { "U08.06.6790", "U08.06.6800" });
        MappingProposalEngine.Propose(new[] { Group(layer) }, Catalog())
            .Should().OnlyContain(proposal => proposal.Reasons.Any(reason => reason.Contains("לא אומתו")));
    }

    [Fact]
    public void MismatchedUnitsAndContradictoryVoltageAreNotCandidates()
    {
        MappingProposalEngine.Propose(new[] { Group("HASHMAL-HV") with { MeasuredUnit = "m2" } }, Catalog()).Should().BeEmpty();
        MappingProposalEngine.Propose(new[] { Group("HASHMAL-HV-LV") }, Catalog()).Should().BeEmpty();
        MappingProposalEngine.Propose(new[] { Group("SELECTRICISH") }, Catalog()).Should().BeEmpty();
        ElectricalProposalCompatibility.ReviewHint("HASHMAL-HV-LV").Should().Contain("סותרים");
        ElectricalProposalCompatibility.ReviewHint("DSFSDFDSF").Should().BeNull();
    }

    [Fact]
    public void CompleteCadMetadataCannotOverrideConflictingLayerVoltage()
    {
        var group = Group("HASHMAL-HV") with { CadMetadata = new[] {
            new QuantityCadMetadataPolicy.FieldSummary("cad_layer_linetype", new[] { "HASHMAL-LV" }, 0, 7) } };
        MappingProposalEngine.Propose(new[] { group }, Catalog()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("U08.06.6790", false)]
    [InlineData("U08.06.6800", true)]
    [InlineData("U08.01.0170", true)]
    public async Task AiReceivesOnlyCompatibleCandidatesAndCannotReturnAnExcludedCode(string returnedCode, bool abstained)
    {
        var provider = new ElectricalProvider(returnedCode);
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group("Hashmal-HV-line"), Catalog(), null, default);
        provider.Candidates.Should().Equal("U08.06.6790");
        result.IsAbstained.Should().Be(abstained);
        if (abstained) result.Proposals.Should().BeEmpty();
        else result.Proposals.Should().ContainSingle().Which.Status.Should().Be("PROPOSED_UNAPPROVED");
    }

    [Fact]
    public async Task AiMetadataVoltageConflictDoesNotReachRankStage()
    {
        var provider = new ElectricalProvider("U08.06.6790");
        var group = Group("Hashmal-HV-line") with { CadMetadata = new[] {
            new QuantityCadMetadataPolicy.FieldSummary("cad_layer_linetype", new[] { "HASHMAL-LV" }, 0, 7) } };
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeTrue();
        provider.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ExplicitEngineerElectricalContextOnUnknownLayerStillWorks()
    {
        var provider = new ElectricalProvider("U08.06.6790");
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group("DSFSDFDSF"), Catalog(), "חשמל מתח גבוה", default);
        result.IsAbstained.Should().BeFalse();
        provider.Candidates.Should().Equal("U08.06.6790");
        // The existing editor's stricter layer-only defense still admits this response.
        MappingProposalEngine.IsSemanticallyCompatible("DSFSDFDSF", HighDescription).Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitSignalContextIsAllowedButCannotOverrideVoltageConflict()
    {
        var provider = new ElectricalProvider("U08.01.0170");
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group("HASHMAL"), Catalog(), "חשמל לרמזורים", default);
        result.IsAbstained.Should().BeFalse();
        provider.Candidates.Should().Contain("U08.01.0170");
        MappingProposalEngine.IsSemanticallyCompatible("HASHMAL", SignalDescription, "חשמל לרמזורים").Should().BeTrue();
        MappingProposalEngine.IsSemanticallyCompatible("HASHMAL", SignalDescription).Should().BeFalse();
        MappingProposalEngine.IsSemanticallyCompatible("HASHMAL-HV", LowDescription, "חשמל מתח נמוך").Should().BeFalse();
        var conflicting = await new SemanticMappingAssist(new ElectricalProvider("U08.06.6800"))
            .AssistAsync(Group("HASHMAL-HV"), Catalog(), "חשמל מתח נמוך", default);
        conflicting.IsAbstained.Should().BeTrue();
    }

    [Fact]
    public void EditorUsesSameAdmissibleContextAsAiWithoutInventingAnUnknownSubject()
    {
        SemanticMappingAssist.IsCandidateCompatible(Group("HASHMAL"), SignalDescription, "חשמל לרמזורים").Should().BeTrue();
        SemanticMappingAssist.IsCandidateCompatible(Group("HASHMAL"), SignalDescription, null).Should().BeFalse();
        SemanticMappingAssist.IsCandidateCompatible(Group("HASHMAL-HV"), LowDescription, "חשמל מתח נמוך").Should().BeFalse();
        SemanticMappingAssist.IsCandidateCompatible(Group("DSFSDFDSF"), HighDescription, null).Should().BeFalse();
        SemanticMappingAssist.IsCandidateCompatible(Group("DSFSDFDSF"), HighDescription, "חשמל מתח גבוה").Should().BeTrue();
        var group = Group("HASHMAL") with { CadMetadata = new[] {
            new QuantityCadMetadataPolicy.FieldSummary("cad_block_name_effective", new[] { "HASHMAL-RAMZOR" }, 0, 7) } };
        SemanticMappingAssist.IsCandidateCompatible(group, SignalDescription, null).Should().BeTrue();
    }

    [Theory]
    [InlineData("HASHMAL-HV", "חשמל במתח נמוך")]
    [InlineData("HASHMAL-HV", "חשמל למתח נמוך")]
    [InlineData("HASHMAL-HV", "חשמל במתח גבוה ובמתח נמוך")]
    [InlineData("HASHMAL-LV", "חשמל במתח גבוה")]
    [InlineData("HASHMAL-LV", "חשמל למתח גבוה")]
    [InlineData("HASHMAL-LV", "חשמל למתח נמוך ולמתח גבוה")]
    public async Task NaturalHebrewVoltageContextCannotOverrideTheSource(string layer, string context)
    {
        SemanticMappingAssist.IsCandidateCompatible(Group(layer), HighDescription, context).Should().BeFalse();
        SemanticMappingAssist.IsCandidateCompatible(Group(layer), LowDescription, context).Should().BeFalse();
        var provider = new ElectricalProvider("U08.06.6790");
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group(layer), Catalog(), context, default);
        result.IsAbstained.Should().BeTrue();
        provider.Candidates.Should().BeEmpty();
    }

    private static MappingProposalEngine.DiscoveredGroup Group(string layer) =>
        new("layer:" + layer.Split('|').Last() + "|length", layer, "length", "מטר", 7, 844.957);

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "nti-urban-082025-fixture",
            FileHash = "90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c" };
        foreach (var pair in new[] { ("U08.01.0170", SignalDescription), ("U08.06.6790", HighDescription), ("U08.06.6800", LowDescription) })
            catalog.Items.Add(pair.Item1, new CatalogItem { Code = pair.Item1, Description = pair.Item2, UnitRaw = "מטר" });
        return catalog;
    }

    private sealed class ElectricalProvider(string returnedCode) : ISemanticMappingProvider
    {
        internal string[] Candidates { get; private set; } = Array.Empty<string>();
        private string _evidenceKey = "layer";
        public Task<SemanticSearchResponse> SuggestSearchTermsAsync(SemanticSearchRequest request, CancellationToken ct)
        {
            var source = request.Evidence.FirstOrDefault(value => value.Key == "engineer_context") ??
                request.Evidence.Single(value => value.Key == "layer");
            _evidenceKey = source.Key;
            return Task.FromResult(new SemanticSearchResponse(request.ContextId,
                new[] { new SemanticSearchTerm("חשמל", source.Key, source.Value) }));
        }
        public Task<SemanticRankResponse> RankCandidatesAsync(SemanticRankRequest request, CancellationToken ct)
        {
            Candidates = request.Candidates.Select(item => item.Code).ToArray();
            return Task.FromResult(new SemanticRankResponse(request.ContextId, request.CatalogHash,
                new[] { new SemanticRankedCode(returnedCode, "התאמת נושא חשמל מהראיה בלבד", new[] { _evidenceKey }) }));
        }
    }
}
