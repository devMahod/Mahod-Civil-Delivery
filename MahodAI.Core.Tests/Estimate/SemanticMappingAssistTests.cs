using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class SemanticMappingAssistTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Context = "השכבה מייצגת אבן שפה לאורך המדרכה";

    [Fact]
    public async Task UnknownIdentifier_WithExplicitEngineerSubject_FindsExistingSameUnitUnapprovedItem()
    {
        var provider = new FakeProvider();
        var catalog = Catalog();
        var group = Group();
        var before = JsonSerializer.Serialize(catalog);
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, catalog, Context, default);

        result.IsAbstained.Should().BeFalse();
        var proposal = result.Proposals.Should().ContainSingle().Subject;
        proposal.ProposedCode.Should().Be("U40.01.0010");
        proposal.CatalogDescription.Should().Be(catalog.Items[proposal.ProposedCode].Description);
        proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
        proposal.EvidenceKind.Should().Be("ai-semantic-v1");
        proposal.Score.Should().Be(0);
        proposal.Reasons.Should().Contain(SemanticMappingAssist.ReviewWarning);
        result.CatalogId.Should().Be(catalog.SnapshotId);
        result.CatalogHash.Should().Be(Hash);
        CatalogIdentity.IsValidSha256(result.ContextId).Should().BeTrue();
        JsonSerializer.Serialize(catalog).Should().Be(before);
        provider.RankRequest!.Candidates.Should().OnlyContain(item => item.CanonicalUnit == "m");
    }

    [Fact]
    public async Task SemanticTranslation_IsNotRestrictedToAnAliasOrExactSearchTermInSource()
    {
        var provider = new FakeProvider
        {
            Search = request => new(request.ContextId,
                new[] { new SemanticSearchTerm("אבן שפה", "engineer_context", "roadside edging") })
        };
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), "roadside edging", default);
        result.IsAbstained.Should().BeFalse();
        result.Proposals.Should().ContainSingle();
    }

    [Fact]
    public async Task UniformEffectiveBlockName_CanSupplyMeaningOnUnknownLayer()
    {
        var provider = new FakeProvider
        {
            Search = request => new(request.ContextId,
                new[] { new SemanticSearchTerm("אבן שפה", "cad_block_name_effective", "roadside edging") }),
            Rank = request => ValidRank(request, keys: new[] { "cad_block_name_effective" })
        };
        var group = Group(new QuantityCadMetadataPolicy.FieldSummary("cad_block_name_effective", new[] { "roadside edging" }, 0, 297),
            new QuantityCadMetadataPolicy.FieldSummary("cad_block_name_raw", new[] { "*U203" }, 0, 297));
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeFalse();
        provider.SearchRequest!.Evidence.Should().NotContain(value => value.Key == "cad_block_name_raw");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q742")]
    public async Task CrypticIdentifierWithoutMeaning_AbstainsBeforeCallingProvider(string? context)
    {
        var provider = new FakeProvider();
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), context, default);
        result.IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("DSFSDF")]
    [InlineData("DSFSDFDSF")]
    [InlineData("ABCDEF")]
    [InlineData("DSFSDF-EXST")]
    [InlineData("DEMO")]
    [InlineData("PIRUK")]
    [InlineData("קיים")]
    [InlineData("פירוק")]
    [InlineData("Q742-CURBISH")]
    public async Task RawLayerWithoutKnownSubject_AbstainsWithoutInferringFromLetterCountOrState(string layer)
    {
        var provider = new FakeProvider();
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group() with { Layer = layer }, Catalog(), null, default);
        result.IsAbstained.Should().BeTrue();
        result.Message.Should().Contain("מה השכבה מייצגת");
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("HW-CURB", "CURB", "אבן שפה", "אבן שפה לאורך מדרכה")]
    [InlineData("BIUV315", "BIUV", "ביוב", "הנחת צינור ביוב")]
    [InlineData("אבן-שפה", "שפה", "אבן שפה", "אבן שפה לאורך מדרכה")]
    [InlineData("ניקוז", "ניקוז", "ניקוז", "צינור ניקוז")]
    [InlineData("מדרכה", "מדרכה", "מדרכה", "שפת מדרכה")]
    public async Task KnownEngineeringLayerSubject_RemainsEligible(string layer, string quote, string term, string description)
    {
        var catalog = Catalog();
        catalog.Items["U40.01.0010"] = new CatalogItem { Code = "U40.01.0010", Description = description, UnitRaw = "m" };
        var provider = new FakeProvider
        {
            Search = request => new(request.ContextId, new[] { new SemanticSearchTerm(term, "layer", quote) }),
            Rank = request => ValidRank(request, keys: new[] { "layer" })
        };
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group() with { Layer = layer }, catalog, null, default);
        result.IsAbstained.Should().BeFalse();
        result.Proposals.Should().ContainSingle().Which.Status.Should().Be("PROPOSED_UNAPPROVED");
        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AlphabeticUnknownLayer_WithEngineerRole_FindsRealSameUnitUnapprovedCandidate()
    {
        var provider = new FakeProvider();
        var result = await new SemanticMappingAssist(provider).AssistAsync(
            Group() with { Layer = "DSFSDF" }, Catalog(), "אבן שפה", default);
        result.IsAbstained.Should().BeFalse();
        var proposal = result.Proposals.Should().ContainSingle().Subject;
        proposal.ProposedCode.Should().Be("U40.01.0010");
        proposal.CatalogUnit.Should().Be("m");
        proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
        provider.SearchRequest!.Evidence.Should().Contain(value => value.Key == "layer" && value.Value == "DSFSDF");
    }

    [Theory]
    [InlineData("cad_block_name_effective")]
    [InlineData("cad_entity_linetype")]
    [InlineData("cad_layer_linetype")]
    public async Task IndependentUniformCadSubject_CanSupplyMeaningOnAlphabeticUnknownLayer(string key)
    {
        var provider = new FakeProvider
        {
            Search = request => new(request.ContextId,
                new[] { new SemanticSearchTerm("אבן שפה", key, "roadside edging") }),
            Rank = request => ValidRank(request, keys: new[] { key })
        };
        var group = Group(new QuantityCadMetadataPolicy.FieldSummary(key, new[] { "roadside edging" }, 0, 297))
            with { Layer = "DSFSDF" };
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeFalse();
        provider.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData("cad_block_name_effective")]
    [InlineData("cad_block_name_raw")]
    [InlineData("cad_entity_linetype")]
    [InlineData("cad_layer_linetype")]
    public async Task UnknownRawMetadataOnUnknownLayer_DoesNotManufactureSubject(string key)
    {
        var provider = new FakeProvider();
        var group = Group(new QuantityCadMetadataPolicy.FieldSummary(key, new[] { "DSFSDF" }, 0, 297));
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("DSFSDF", "DSFSDF")]
    [InlineData("HW-CURB-DSFSDF", "DSFSDF")]
    [InlineData("Q742", "Q742")]
    public async Task ProviderCannotUseUnknownRawLayerQuoteEvenWithExplicitEngineerContext(string layer, string quote)
    {
        var provider = new FakeProvider
        {
            Search = request => new(request.ContextId,
                new[] { new SemanticSearchTerm("אבן שפה", "layer", quote) })
        };
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group() with { Layer = layer }, Catalog(), Context, default);
        result.IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(1);
        provider.RankRequest.Should().BeNull();
    }

    [Theory]
    [InlineData("layer")]
    [InlineData("cad_block_name_effective")]
    [InlineData("cad_layer_linetype")]
    public async Task ProviderCannotCiteUnknownRawIdentifierAsRankingEvidence(string key)
    {
        var provider = new FakeProvider { Rank = request => ValidRank(request, keys: new[] { "engineer_context", key }) };
        var group = Group(new QuantityCadMetadataPolicy.FieldSummary("cad_block_name_effective", new[] { "DSFSDF" }, 0, 297),
            new QuantityCadMetadataPolicy.FieldSummary("cad_layer_linetype", new[] { "DSFSDF" }, 0, 297)) with { Layer = "DSFSDF" };
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), Context, default);
        result.IsAbstained.Should().BeTrue();
        result.Proposals.Should().BeEmpty();
        provider.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData("C:\\private\\source.dwg")]
    [InlineData("\\\\server\\project")]
    [InlineData("204539.12, 649342.4")]
    [InlineData("כמות 297 אבן שפה")]
    [InlineData("מחיר 20 ש״ח")]
    public async Task ExplicitPrivateOrQuantityPriceText_IsNotSent(string context)
    {
        var provider = new FakeProvider();
        (await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), context, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ContextLimit_IsEnforcedBeforeProvider()
    {
        var provider = new FakeProvider();
        (await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), new string('א', 501), default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 297)]
    [InlineData(1, 297)]
    [InlineData(0, 296)]
    public async Task MixedOrIncompleteSubject_IsNotBorrowedForWholeGroup(int missing, int count)
    {
        var values = missing == 0 && count == 297 ? new[] { "curb", "bench" } : new[] { "curb" };
        var provider = new FakeProvider();
        var group = Group(new QuantityCadMetadataPolicy.FieldSummary("cad_block_name_effective", values, missing, count));
        (await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), Context, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("missing", "אבן שפה")]
    [InlineData("engineer_context", "בטון דרוך")]
    [InlineData("layer", "Q742")]
    public async Task HallucinatedOrMeaninglessQuote_DoesNotReachRanking(string key, string quote)
    {
        var provider = new FakeProvider { Search = request => new(request.ContextId,
            new[] { new SemanticSearchTerm("אבן שפה", key, quote) }) };
        (await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), Context, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task SearchContextMismatch_IsRejected()
    {
        var provider = new FakeProvider { Search = _ => new("stale", new[] { new SemanticSearchTerm("אבן שפה", "engineer_context", "אבן שפה") }) };
        (await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), Context, default)).Proposals.Should().BeEmpty();
        provider.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("fake-code")]
    [InlineData("U40.01.0020")]
    [InlineData("U40.01.0030")]
    public async Task FakeWrongUnitOrSemanticallyRejectedCatalogCode_IsNeverPublished(string code)
    {
        var provider = new FakeProvider { Rank = request => ValidRank(request, code) };
        (await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), Context, default)).Proposals.Should().BeEmpty();
        provider.RankRequest!.Candidates.Select(item => item.Code).Should().Equal("U40.01.0010");
    }

    [Theory]
    [InlineData("context")]
    [InlineData("hash")]
    [InlineData("duplicates")]
    [InlineData("evidence")]
    [InlineData("explanation")]
    [InlineData("price")]
    public async Task InvalidRanking_IsAllOrNothing(string defect)
    {
        var provider = new FakeProvider { Rank = request =>
        {
            var valid = ValidRank(request);
            return defect switch
            {
                "context" => valid with { ContextId = "wrong" },
                "hash" => valid with { CatalogHash = new string('b', 64) },
                "duplicates" => valid with { Ranked = new[] { valid.Ranked[0], valid.Ranked[0] } },
                "evidence" => ValidRank(request, keys: Array.Empty<string>()),
                "explanation" => valid with { Ranked = new[] { valid.Ranked[0] with { Explanation = "" } } },
                _ => valid with { Ranked = new[] { valid.Ranked[0] with { Explanation = "מחיר 42" } } }
            };
        } };
        (await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), Context, default)).Proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task CandidateList_IsBoundedAndContainsFullLocalDescriptions()
    {
        var catalog = Catalog();
        for (var index = 0; index < 120; index++)
        {
            var code = "U41." + index.ToString("D5");
            catalog.Items.Add(code, new CatalogItem { Code = code, Description = "אבן שפה מלאה תיאור " + index, UnitRaw = "מ'" });
        }
        var provider = new FakeProvider();
        await new SemanticMappingAssist(provider).AssistAsync(Group(), catalog, Context, default);
        provider.RankRequest!.Candidates.Should().HaveCount(80);
        provider.RankRequest.Candidates.Should().OnlyContain(item => item.Description == catalog.Items[item.Code].Description);
    }

    [Fact]
    public async Task InputMetadataAndCatalogMutationDuringCall_AbstainsAndDoesNotMutateProviderSnapshot()
    {
        var values = new List<string> { "roadside edging" };
        var catalog = Catalog();
        var group = Group(new QuantityCadMetadataPolicy.FieldSummary("cad_block_name_effective", values, 0, 297));
        var provider = new FakeProvider { OnRank = request =>
        {
            values[0] = "different subject";
            catalog.Items["U40.01.0010"] = new CatalogItem { Code = "U40.01.0010", Description = "different item", UnitRaw = "m" };
            request.Evidence.Single(value => value.Key == "cad_block_name_effective").Value.Should().Be("roadside edging");
            request.Candidates[0].Description.Should().Contain("אבן שפה");
        } };
        (await new SemanticMappingAssist(provider).AssistAsync(group, catalog, Context, default)).Proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownUnitOrBadCatalogIdentity_AbstainsBeforeProvider()
    {
        var provider = new FakeProvider();
        var assistant = new SemanticMappingAssist(provider);
        (await assistant.AssistAsync(Group() with { MeasuredUnit = "?" }, Catalog(), Context, default)).IsAbstained.Should().BeTrue();
        var bad = new CatalogSnapshot { SnapshotId = "bad", FileHash = "bad" };
        (await assistant.AssistAsync(Group(), bad, Context, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task TotalBudget_CancelsMisbehavingProviderWithoutPartialResult()
    {
        var provider = new FakeProvider { DelaySearch = true };
        var result = await new SemanticMappingAssist(provider, TimeSpan.FromMilliseconds(30))
            .AssistAsync(Group(), Catalog(), Context, default);
        result.IsAbstained.Should().BeTrue();
        result.Message.Should().Contain("בזמן");
        result.Proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task UserCancellationBeforeCall_DoesNotCallProviderOrThrow()
    {
        var provider = new FakeProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), Context, cancellation.Token);
        result.Message.Should().Contain("בוטלה");
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ProviderException_IsSanitizedAndManualFallbackRemains()
    {
        var provider = new FakeProvider { Search = _ => throw new InvalidOperationException("SECRET_CANARY body server path") };
        var result = await new SemanticMappingAssist(provider).AssistAsync(Group(), Catalog(), Context, default);
        JsonSerializer.Serialize(result).Should().NotContain("SECRET_CANARY");
        result.Message.Should().Contain("ידני");
        result.IsAbstained.Should().BeTrue();
    }

    [Fact]
    public async Task WireContracts_DoNotExposeMeasuredQuantityPricesRuleKeyOrSourcePrefix()
    {
        var provider = new FakeProvider();
        var group = Group() with { RuleKey = "private-source-rule", Layer = "C:\\private\\model.dwg|Q742", TotalQuantity = 29287.63123 };
        await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), Context, default);
        var wire = JsonSerializer.Serialize(new { provider.SearchRequest, provider.RankRequest });
        wire.Should().NotContain("29287").And.NotContain("private-source-rule").And.NotContain("private").And.NotContain("ObjectCount").And.NotContain("Price");
        provider.SearchRequest!.Evidence.Single(value => value.Key == "layer").Value.Should().Be("Q742");
    }

    [Fact]
    public async Task RandomLayer_UniformObjectEvidence_ReachesSemanticSearchWithoutEngineerDescription()
    {
        var group = BridgeGroup();
        var provider = BridgeProvider();
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeFalse();
        result.Proposals.Should().ContainSingle().Which.Status.Should().Be("PROPOSED_UNAPPROVED");
        result.Proposals[0].Reasons.Should().Contain(reason => reason.Contains("ev_block_attributes") && reason.Contains("DESC"));
        var wire = JsonSerializer.Serialize(new { provider.SearchRequest, provider.RankRequest });
        wire.Should().NotContain("private-model").And.NotContain("DESC").And.NotContain("RecordCount").And.NotContain("42.125");
        provider.SearchRequest!.Evidence.Should().Contain(e => e.Key == "ev_block_attributes:0" && e.Value == "CURB");
        (await new SemanticMappingAssist(new FakeProvider()).AssistAsync(group with { RecognitionEvidence = null }, Catalog(), null, default))
            .IsAbstained.Should().BeTrue("control: neither random layer nor raw measurement supplies the subject");
    }

    [Theory]
    [InlineData("read", "SEWER")]
    [InlineData("truncated:2", "CURB")]
    [InlineData("unavailable:read-failed", "CURB")]
    public async Task MixedTruncatedOrUnavailableObjectEvidence_IsNotInventedByAi(string secondStatus, string secondSubject)
    {
        var provider = BridgeProvider();
        var group = BridgeGroup(secondStatus, secondSubject);
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ContradictoryBridge_BlocksEvenKnownLayerAndExplicitContext()
    {
        var provider = BridgeProvider();
        var group = BridgeGroup() with { Layer = "HW-CURB", RecognitionEvidence = BridgeGroup().RecognitionEvidence! with
            { Contradictions = new[] { "TEST ONLY: water and sewer conflict" } } };
        (await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), Context, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("CURB C:/private-model/a.dwg")]
    [InlineData("CURB person@example.com")]
    [InlineData("CURB +972-50-123-4567")]
    public async Task PrivateObjectText_IsNotSent(string value)
    {
        var group = BridgeGroup();
        group = group with { RecognitionEvidence = group.RecognitionEvidence! with
            { Subjects = new[] { group.RecognitionEvidence.Subjects.Single() with { Value = value } } } };
        var provider = BridgeProvider();
        (await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ChangedEvidenceDuringRequest_IsRejectedAndDetachedSnapshotIsUnchanged()
    {
        var group = BridgeGroup();
        var mutable = group.RecognitionEvidence!.Subjects.ToList();
        group = group with { RecognitionEvidence = group.RecognitionEvidence with { Subjects = mutable } };
        var snapshot = CatalogEvidenceBridge.Snapshot(group.RecognitionEvidence)!;
        var provider = BridgeProvider();
        provider.OnRank = _ => mutable[0] = mutable[0] with { Value = "SEWER" };
        (await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default)).IsAbstained.Should().BeTrue();
        provider.Calls.Should().Be(2);
        snapshot.Subjects.Single().Value.Should().Be("CURB");
        ((IList<CatalogEvidenceBridge.Subject>)snapshot.Subjects).IsReadOnly.Should().BeTrue();
    }

    private static FakeProvider BridgeProvider() => new()
    {
        Search = request => new(request.ContextId, new[] { new SemanticSearchTerm("אבן שפה", "ev_block_attributes:0", "CURB") }),
        Rank = request => ValidRank(request, keys: new[] { "ev_block_attributes:0" }),
    };

    [Fact]
    public async Task CombinedFamilyAuditMetadata_RemainsLocalAndDetached()
    {
        var group = BridgeGroup();
        var ids = new List<string> { "PRIVATE-DECISION-71" };
        var resolutions = new List<FamilyResolution> { new("PRIVATE-GROUP", FamilyDecisionState.Applied,
            "curb", ids[0], 0, "PRIVATE-REASON", "PRIVATE-APPROVER", DateTime.UtcNow) };
        group = group with { RecognitionEvidence = group.RecognitionEvidence! with
            { ConfirmedFamilyDecisionIds = ids, FamilyDecisionResolutions = resolutions } };
        var snapshot = CatalogEvidenceBridge.Snapshot(group.RecognitionEvidence)!;
        var provider = BridgeProvider();
        var result = await new SemanticMappingAssist(provider).AssistAsync(group, Catalog(), null, default);
        result.IsAbstained.Should().BeFalse();
        JsonSerializer.Serialize(new { provider.SearchRequest, provider.RankRequest }).Should().NotContain("PRIVATE-");
        ids.Clear(); resolutions.Clear();
        snapshot.ConfirmedFamilyDecisionIds.Should().ContainSingle();
        snapshot.FamilyDecisionResolutions.Should().ContainSingle();
    }

    private static MappingProposalEngine.DiscoveredGroup BridgeGroup(string secondStatus = "read", string secondSubject = "CURB")
    {
        NeutralQuantityRecord Record(string id, string status, string subject) => new()
        {
            RecordId = id, ProjectProfileId = "SYNTHETIC-ONLY", RunId = "SYNTHETIC-ONLY",
            Source = new() { Drawing = "private-model.dwg", DrawingPath = "C:/private-model.dwg", DrawingHash = Hash,
                Layer = "Q742", Handle = id, EntityType = "LWPOLYLINE" },
            Measurement = new() { Kind = "length", Unit = "m", Method = "polyline-length", RawValue = 42.125,
                Parameters = new()
                {
                    [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
                    [EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new[] { new { tag = "DESC", value = subject, invisible = false } }),
                    [EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = status,
                } },
            Classification = new() { RuleKey = "private-model-rule" },
        };
        var records = new[] { Record("A1", "read", "CURB"), Record("A2", secondStatus, secondSubject) };
        return new("private-model-rule", "Q742", "length", "m", 2, 84.25,
            RecognitionEvidence: CatalogEvidenceBridge.For(records, EngineerBoqLibrary.RoadsV1));
    }

    private static MappingProposalEngine.DiscoveredGroup Group(params QuantityCadMetadataPolicy.FieldSummary[] fields) =>
        new("layer:Q742|length", "Q742", "length", "m", 297, 29287.63, fields);
    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "local-catalog", FileHash = Hash };
        foreach (var item in new[] {
            new CatalogItem { Code = "U40.01.0010", Description = "אבן שפה לאורך מדרכה", UnitRaw = "מ'" },
            new CatalogItem { Code = "U40.01.0020", Description = "אבן שפה ביחידות", UnitRaw = "יח'" },
            new CatalogItem { Code = "U40.01.0030", Description = "פירוק אבן שפה", UnitRaw = "מ'" } }) catalog.Items.Add(item.Code, item);
        return catalog;
    }
    private static SemanticRankResponse ValidRank(SemanticRankRequest request, string code = "U40.01.0010", string[]? keys = null) =>
        new(request.ContextId, request.CatalogHash, new[] { new SemanticRankedCode(code,
            "ייתכן שמתאים לאבן השפה המתוארת; נדרש לבדוק את פרטי הסעיף", keys ?? new[] { "engineer_context" }) });

    private sealed class FakeProvider : ISemanticMappingProvider
    {
        public int Calls;
        public bool DelaySearch;
        public SemanticSearchRequest? SearchRequest;
        public SemanticRankRequest? RankRequest;
        public Func<SemanticSearchRequest, SemanticSearchResponse>? Search;
        public Func<SemanticRankRequest, SemanticRankResponse>? Rank;
        public Action<SemanticRankRequest>? OnRank;
        public Task<SemanticSearchResponse> SuggestSearchTermsAsync(SemanticSearchRequest request, CancellationToken ct)
        {
            Calls++; SearchRequest = request;
            if (DelaySearch) return new TaskCompletionSource<SemanticSearchResponse>().Task;
            return Task.FromResult(Search?.Invoke(request) ?? new(request.ContextId,
                new[] { new SemanticSearchTerm("אבן שפה", "engineer_context", "אבן שפה") }));
        }
        public Task<SemanticRankResponse> RankCandidatesAsync(SemanticRankRequest request, CancellationToken ct)
        {
            Calls++; RankRequest = request; OnRank?.Invoke(request);
            return Task.FromResult(Rank?.Invoke(request) ?? ValidRank(request));
        }
    }
}
