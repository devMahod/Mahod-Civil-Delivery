using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// b19 (live b18 finding 2, Codex 03:31): a group the active library maps to a decision with no item gets a visible review
/// and no automatic proposal, for every library; mixed or overlapping groups are a visible review, never a first match.
/// The disposition is read from the engineer draft itself. Records, items and prices are SYNTHETIC.
/// </summary>
public sealed class LibraryProposalGateTests
{
    private static int _handle;

    private static NeutralQuantityRecord Rec(string layer, string kind, string method, double value, string unit, string ruleKey,
        string? block = null, string? xref = null)
    {
        var parameters = new Dictionary<string, string>();
        if (block != null) parameters["cad_block_name_effective"] = block;
        var handle = "G" + System.Threading.Interlocked.Increment(ref _handle).ToString("X");
        return new NeutralQuantityRecord
        {
            RecordId = $"q-{handle}",
            ProjectProfileId = "SYNTHETIC",
            RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('e', 64), Handle = handle, EntityType = "X",
                Layer = xref == null ? layer : $"{xref}|{layer}", Xref = xref,
            },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = parameters },
            Classification = new QuantityClassification { RuleKey = ruleKey },
        };
    }

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('f', 64) };
        void Add(string code, string description, string unit, decimal? price)
        {
            catalog.Items[code] = new CatalogItem { Code = code, Description = description, UnitRaw = unit };
            catalog.Prices[code] = new PriceRecord { Code = code, Price = price, PriceBookId = "SYNTHETIC" };
        }
        // The two wrong word matches seen live on b18 (02/10) for the tree blocks.
        Add("08.10.3304", "עמוד עץ בגובה 10 מ', מותקן בקרקע או בקוביית בטון", "יח'", 720m);
        Add("02.02.2350", "הוצאת לוחות עץ קיימים בבטון ישן וטיפול מקומי", "יח'", 55m);
        Add("51.06.0030", "אבן גן 10/20 ס\"מ בגוון אפור", "מטר", 81m);
        Add("U51.06.1900", "SYNTHETIC road curb", "מטר", 90m);
        return catalog;
    }

    private static readonly EngineerDraftContext Context =
        new("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC catalog", Array.Empty<string>());

    private static IReadOnlyDictionary<string, LibraryProposalDisposition> Gate(EngineerBoqLibrary library, params NeutralQuantityRecord[] records) =>
        LibraryProposalGate.Evaluate(EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(), Catalog(),
            new Dictionary<string, string>(), library, Context), records);

    [Fact]
    public void TheTwoTreeGroupsAreHeldThoughTheHeuristicWouldProposeTheWrongWoodItems()
    {
        var records = new[]
        {
            Rec("LA-TREE-RPL", "count", "block-count", 1, "יח'", "K-RPL", "TreeKayam"),
            Rec("LA-TREE-RPL", "count", "block-count", 1, "יח'", "K-RPL", "TreeKayam"),
            Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "K-ELLA", "BL-TREE-ELLA"),
        };
        var gate = Gate(EngineerBoqLibrary.LandscapeV1, records);
        gate.Should().ContainKeys("K-RPL", "K-ELLA");
        gate.Values.Should().OnlyContain(d => d.State == LibraryProposalState.NoItemDecision && d.Message.Contains("ספריית הנוף"));
        LibraryProposalGate.Refusal(gate, "k-rpl").Should().NotBeNull("the whole case-insensitive closure is held");

        // Without the gate the heuristic proposes the wood items by the word "TREE" → "עץ" (as live on b18, 02/10).
        var heuristic = MappingProposalEngine.Propose(new[]
        {
            new MappingProposalEngine.DiscoveredGroup("K-ELLA", "LA-TREE-Ella", "count", "יח'", 66, 66),
            new MappingProposalEngine.DiscoveredGroup("K-RPL", "LA-TREE-RPL", "count", "יח'", 219, 219),
        }, Catalog());
        heuristic.Should().Contain(p => p.ProposedCode == "02.02.2350" || p.ProposedCode == "08.10.3304");
    }

    [Fact]
    public void ARoadsNoItemDecisionIsHeldToo_AGroupWithItemsOrNoRuleStaysOpen()
    {
        var gate = Gate(EngineerBoqLibrary.RoadsV1,
            Rec("PL-BIKE", "area", "hatch-area", 120, "מ\"ר", "K-BIKE"),            // bike-path: decision, no items
            Rec("HW-CURB", "length", "polyline-length", 80, "מטר", "K-CURB"),       // curb-road: has items
            Rec("QQ-RANDOM-77", "length", "polyline-length", 30, "מטר", "K-OTHER")); // no library rule at all
        gate.Should().ContainKey("K-BIKE").WhoseValue.State.Should().Be(LibraryProposalState.NoItemDecision);
        gate.Should().NotContainKey("K-CURB").And.NotContainKey("K-OTHER");
    }

    [Fact]
    public void ALandscapeCandidateWithItemsIsNotHeldByThisGate() =>
        // La-hatch-SP has candidate items (held back in the draft, not in the total): the item rule path stays as it is.
        Gate(EngineerBoqLibrary.LandscapeV1, Rec("La-hatch-SP", "area", "hatch-area", 500, "מ\"ר", "K-SP")).Should().BeEmpty();

    [Fact]
    public void ABasisMismatchIsNotAVeto() =>
        // A line on the tree layer is not the counted-block element: no hold, the existing path applies.
        Gate(EngineerBoqLibrary.LandscapeV1, Rec("LA-TREE-Ella", "length", "polyline-length", 12, "מטר", "K-LINE")).Should().BeEmpty();

    [Fact]
    public void APartlyHeldGroupIsMixed()
    {
        var gate = Gate(EngineerBoqLibrary.LandscapeV1,
            Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "K-MIX", "BL-TREE-ELLA"),
            Rec("QQ-OTHER", "count", "block-count", 1, "יח'", "K-MIX", "SOMEBLOCK"));
        var mixed = gate.Should().ContainSingle().Which.Value;
        mixed.State.Should().Be(LibraryProposalState.Mixed);
        mixed.Message.Should().Contain("1 מתוך 2");
    }

    [Fact]
    public void ANoItemRuleOverlappingARuleWithItemsIsMixed_NotAFirstMatch()
    {
        var source = EngineerBoqLibrary.LandscapeV1;
        var noItem = new DraftRule("syn-hold", "SYNTHETIC hold", new[] { "SYN-EDGE" }, DraftQuantityBasis.OpenLength,
            DraftConfidence.Decision, Array.Empty<DraftEmit>(), "SYNTHETIC");
        var withItem = new DraftRule("syn-item", "SYNTHETIC item", new[] { "SYN-*" }, DraftQuantityBasis.OpenLength,
            DraftConfidence.Decision, new[] { new DraftEmit("51.06.0030") }, "SYNTHETIC");
        var library = new EngineerBoqLibrary
        {
            Id = source.Id, Title = source.Title, Basis = source.Basis, Parameters = source.Parameters, Rules = new[] { noItem, withItem },
            ScopeItems = source.ScopeItems, SurveySourcePatterns = source.SurveySourcePatterns, UtilitySourcePatterns = source.UtilitySourcePatterns,
            DraftingAidLayerPatterns = source.DraftingAidLayerPatterns, ExistingLayerPatterns = source.ExistingLayerPatterns,
            WidthClassifiedLayerPatterns = source.WidthClassifiedLayerPatterns, WidthParameterKeys = source.WidthParameterKeys, Texts = source.Texts,
        };
        var overlap = Gate(library, Rec("SYN-EDGE", "length", "polyline-length", 40, "מטר", "K-OVER"));
        overlap.Should().ContainKey("K-OVER").WhoseValue.State.Should().Be(LibraryProposalState.Mixed);
        overlap["K-OVER"].Message.Should().Contain("כלל עם סעיפים");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANoItemRuleOverlappingARuleWithItemsIsMixedInEitherRuleOrder(bool noItemFirst)
    {
        // Codex 03:59: with the item rule first the builder assigns the item element; the gate must still see the no-item rule.
        var source = EngineerBoqLibrary.LandscapeV1;
        var noItem = new DraftRule("syn-hold", "SYNTHETIC hold", new[] { "SYN-EDGE" }, DraftQuantityBasis.OpenLength,
            DraftConfidence.Decision, Array.Empty<DraftEmit>(), "SYNTHETIC");
        var withItem = new DraftRule("syn-item", "SYNTHETIC item", new[] { "SYN-*" }, DraftQuantityBasis.OpenLength,
            DraftConfidence.Decision, new[] { new DraftEmit("51.06.0030") }, "SYNTHETIC");
        var library = new EngineerBoqLibrary
        {
            Id = source.Id, Title = source.Title, Basis = source.Basis, Parameters = source.Parameters,
            Rules = noItemFirst ? new[] { noItem, withItem } : new[] { withItem, noItem },
            ScopeItems = source.ScopeItems, SurveySourcePatterns = source.SurveySourcePatterns, UtilitySourcePatterns = source.UtilitySourcePatterns,
            DraftingAidLayerPatterns = source.DraftingAidLayerPatterns, ExistingLayerPatterns = source.ExistingLayerPatterns,
            WidthClassifiedLayerPatterns = source.WidthClassifiedLayerPatterns, WidthParameterKeys = source.WidthParameterKeys, Texts = source.Texts,
        };
        var gate = Gate(library, Rec("SYN-EDGE", "length", "polyline-length", 40, "מטר", "K-OVER"));
        gate.Should().ContainKey("K-OVER").WhoseValue.State.Should().Be(LibraryProposalState.Mixed);
        gate["K-OVER"].Message.Should().Contain("כלל עם סעיפים");
    }

    [Fact]
    public void TheCaseClosureIsDecidedOnceAndGivenToEveryExactKey()
    {
        // Codex 03:59: K held and k another subject — both exact keys get the same mixed review, with the real counts.
        var gate = Gate(EngineerBoqLibrary.LandscapeV1,
            Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "K-CASE", "BL-TREE-ELLA"),
            Rec("QQ-OTHER", "count", "block-count", 1, "יח'", "k-case", "SOMEBLOCK"));
        gate.Keys.Should().BeEquivalentTo(new[] { "K-CASE", "k-case" });
        gate.Values.Should().OnlyContain(d => d.State == LibraryProposalState.Mixed && d.Message.Contains("1 מתוך 2") && d.Message.Contains("2 מפתחות"));

        var held = Gate(EngineerBoqLibrary.LandscapeV1,
            Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "K-ALL", "BL-TREE-ELLA"),
            Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "k-all", "BL-TREE-ELLA"));
        held.Keys.Should().BeEquivalentTo(new[] { "K-ALL", "k-all" });
        held.Values.Should().OnlyContain(d => d.State == LibraryProposalState.NoItemDecision);
    }

    [Fact]
    public void AnApprovedMappingIsUntouchedAndNotAProposalGroup()
    {
        var approved = Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "K-DONE", "BL-TREE-ELLA");
        approved = new NeutralQuantityRecord
        {
            RecordId = approved.RecordId, ProjectProfileId = approved.ProjectProfileId, RunId = approved.RunId, Source = approved.Source,
            Measurement = approved.Measurement,
            Classification = new QuantityClassification { RuleKey = "K-DONE", CandidateCatalogCode = "51.06.0030" },
        };
        Gate(EngineerBoqLibrary.LandscapeV1, approved).Should().BeEmpty("a mapped record is not an unmapped proposal group");
    }
}
