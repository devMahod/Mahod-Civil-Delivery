using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// b19 (Codex 03:31): the library gate is wired before the heuristic and the curated rules, honoured by the assistant before
/// and after its await, and re-read before a batch of older proposals is saved; manual mapping is not gated. The runtime
/// behaviour of the gate itself is in Core (LibraryProposalGateTests); these are the plugin's wiring and review shape.
/// </summary>
public sealed class LibraryProposalGateWiringTests
{
    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery" }.Concat(parts).ToArray()));

    private static readonly IReadOnlyDictionary<string, LibraryProposalDisposition> Held = new Dictionary<string, LibraryProposalDisposition>
    {
        ["K-TREE"] = new("K-TREE", LibraryProposalState.NoItemDecision, new[] { "עצים בשכבה LA-TREE-Ella" }, "ממתין להחלטה — אין סעיף"),
        ["K-MIX"] = new("K-MIX", LibraryProposalState.Mixed, new[] { "עצים בשכבה LA-TREE-Ella" }, "קבוצה מעורבת"),
    };

    [Fact]
    public void AHeldGroupBecomesAGovernedReviewWithItsLabelAndState()
    {
        var reviews = EstimateWorkflowService.LibraryReviews(Held, new[]
        {
            new MappingProposalEngine.DiscoveredGroup("K-TREE", "LA-TREE-Ella", "count", "יח'", 66, 66),
            new MappingProposalEngine.DiscoveredGroup("K-MIX", "LA-TREE-Ella", "count", "יח'", 2, 2),
            new MappingProposalEngine.DiscoveredGroup("K-OPEN", "QQ", "length", "מטר", 3, 30),
        });
        reviews.Should().HaveCount(2).And.OnlyContain(r => r.Governed);
        reviews.Single(r => r.RuleKey == "K-TREE").Should().Match<EstimateWorkflowService.ProjectRuleReview>(r =>
            r.Label == EstimateWorkflowService.LibraryNoItemLabel && r.State == "library_no_item" && r.Message == "ממתין להחלטה — אין סעיף");
        reviews.Single(r => r.RuleKey == "K-MIX").State.Should().Be("library_mixed");
    }

    [Fact]
    public void BothCaseVariantsOfAHeldKeyGetAReviewAndReachNoHeuristic()
    {
        // Codex 03:59: K on the tree layer and k on another layer — the whole closure is reviewed, and neither key is left
        // for the heuristic (which, without the gate, proposes the wood items by the word "TREE").
        var records = new[]
        {
            Record("LA-TREE-Ella", "K-WOOD", "BL-TREE-ELLA"),
            Record("LA-TREE-Ella", "K-WOOD", "BL-TREE-ELLA"),
            Record("LA-TREE-RPL", "k-wood", "TreeKayam"),
        };
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('a', 64) };
        foreach (var (code, text) in new[] { ("08.10.3304", "עמוד עץ בגובה 10 מ'"), ("02.02.2350", "הוצאת לוחות עץ קיימים") })
        {
            catalog.Items[code] = new CatalogItem { Code = code, Description = text, UnitRaw = "יח'" };
            catalog.Prices[code] = new PriceRecord { Code = code, Price = 100m, PriceBookId = "SYNTHETIC" };
        }
        var library = EngineerBoqLibrary.LandscapeV1;
        var draft = EngineerBoqDraftBuilder.Build(records, Array.Empty<MahodAI.CivilDelivery.Shared.DeliveryFinding>(), catalog,
            new Dictionary<string, string>(), library,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC", Array.Empty<string>()));
        var groups = EstimateWorkflowService.BuildMappingProposalGroups(records, library);
        groups.Select(g => g.RuleKey).Should().BeEquivalentTo(new[] { "K-WOOD", "k-wood" });
        MappingProposalEngine.Propose(groups, catalog).Should().NotBeEmpty("without the gate the heuristic proposes the wood items");

        var reviews = EstimateWorkflowService.LibraryReviews(LibraryProposalGate.Evaluate(draft, records), groups);
        reviews.Select(r => r.RuleKey).Should().BeEquivalentTo(new[] { "K-WOOD", "k-wood" });
        reviews.Should().OnlyContain(r => r.Governed && r.State == "library_no_item");
        var held = reviews.Select(r => r.RuleKey).ToHashSet(StringComparer.Ordinal);
        var left = groups.Where(g => !held.Contains(g.RuleKey)).ToList();
        left.Should().BeEmpty();
        MappingProposalEngine.Propose(left, catalog).Should().BeEmpty("no heuristic or curated proposal reaches a held key");
    }

    private static int _handle;

    private static NeutralQuantityRecord Record(string layer, string ruleKey, string block)
    {
        var handle = "W" + System.Threading.Interlocked.Increment(ref _handle).ToString("X");
        return new NeutralQuantityRecord
        {
            RecordId = "q-" + handle, ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource { Drawing = "synthetic.dwg", DrawingHash = new string('b', 64), Handle = handle, EntityType = "INSERT", Layer = layer },
            Measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'",
                // b24: what a scan under the unit contract writes for an explicit-metre host record.
                Parameters = new Dictionary<string, string>
                {
                    ["cad_block_name_effective"] = block, [QuantityPhysicalUnits.RawUnitsKey] = "Meters",
                    [QuantityPhysicalUnits.AuthorityKey] = "explicit-insunits", [QuantityPhysicalUnits.UnitCodeKey] = "6",
                    [QuantityPhysicalUnits.MetresPerUnitKey] = "1",
                },
            },
            Classification = new QuantityClassification { RuleKey = ruleKey },
        };
    }

    [Fact]
    public void AfterAnotherGroupIsMappedTheHeldTreeKeepsItsReviewAndTheMappedGroupHasNone()
    {
        // Codex 04:04: the review map is rebuilt from L05 and the library after a decision rebases the scan.
        var tree = Record("LA-TREE-Ella", "K-TREE", "BL-TREE-ELLA");
        var mapped = Record("QQ-STONE", "K-STONE", "STONE");
        mapped = new NeutralQuantityRecord
        {
            RecordId = mapped.RecordId, ProjectProfileId = mapped.ProjectProfileId, RunId = mapped.RunId, Source = mapped.Source,
            Measurement = mapped.Measurement,
            Classification = new QuantityClassification { RuleKey = "K-STONE", CandidateCatalogCode = "02.02.2350", MappingApprovedBy = "SYNTHETIC" },
        };
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('a', 64) };
        catalog.Items["02.02.2350"] = new CatalogItem { Code = "02.02.2350", Description = "SYNTHETIC", UnitRaw = "יח'" };
        var profile = new MahodAI.CivilDelivery.Shared.ProjectProfile { ProfileId = "SYNTHETIC" };
        profile.Estimate.Discipline = "landscape";
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "SYNTHETIC-RUN", ProjectProfileId = "SYNTHETIC", ProfileSource = "synthetic.yaml", SourceDrawing = "synthetic.dwg",
            Records = new List<NeutralQuantityRecord> { tree, mapped },
            PhysicalUnits = MahodAI.CivilDelivery.Shared.PhysicalDrawingUnitPolicy.Resolve(6, null, null, null),
            PhysicalUnitsContract = ScanUnitEvidence.Contract,
        };
        var reviews = EstimateWorkflowService.ReviewsWithLibrary(scan, catalog, profile,
            EstimateWorkflowService.ProjectRuleContext.NotApplicable("SYNTHETIC"));
        reviews.Should().ContainSingle().Which.Should().Match<EstimateWorkflowService.ProjectRuleReview>(r =>
            r.RuleKey == "K-TREE" && r.State == "library_no_item" && r.Governed);
        mapped.Classification.CandidateCatalogCode.Should().Be("02.02.2350", "the mapped group stays mapped and gets no review");
        var palette = Source("UI", "CivilDeliveryControl.xaml.cs");
        var refresh = palette[palette.IndexOf("private void RefreshProjectRuleReviewsAfterRebase(", StringComparison.Ordinal)..];
        refresh[..refresh.IndexOf("private void OnBuildEstimate(", StringComparison.Ordinal)]
            .Should().Contain("EstimateWorkflowService.ReviewsWithLibrary(_scan, _catalog, _profile, context)");
    }

    [Fact]
    public void TheAiToolTellsALibraryHoldFromAProjectRuleReview()
    {
        // Codex 04:04: an explicit discriminator and a separate instruction; L05 keeps its own meaning.
        var library = EstimateWorkflowService.LibraryReviews(Held, new[] { new MappingProposalEngine.DiscoveredGroup("K-TREE", "L", "count", "יח'", 1, 1) }).Single();
        EstimateWorkflowService.IsLibraryReview(library).Should().BeTrue();
        var rule = new EstimateWorkflowService.ProjectRuleReview("K-RULE", "L", 1, 1, 0, 0, Array.Empty<string>(), "m", true);
        EstimateWorkflowService.IsLibraryReview(rule).Should().BeFalse();
        var tools = Source("..", "Tools", "CivilDelivery", "EstimateTools.cs");
        tools.Should().Contain("[\"governed_by\"] = EstimateWorkflowService.IsLibraryReview(r) ? \"library\" : \"project_rules\",")
            .And.Contain("[\"governed_by_project_rules\"] = r.Governed && !EstimateWorkflowService.IsLibraryReview(r),")
            .And.Contain("[\"state\"] = r.State,")
            .And.Contain("Groups with governed_by=library are elements the project's BoQ library defines as an engineering");
    }

    [Fact]
    public void ABatchWithAHeldGroupIsRefusedBeforeAnyWrite()
    {
        var held = () => EstimateWorkflowService.RequireNoLibraryHeldCandidates(Held, new[] { "K-OPEN", "k-tree" });
        held.Should().Throw<InvalidOperationException>().WithMessage("*לא נשמר אף מיפוי*");
        var open = () => EstimateWorkflowService.RequireNoLibraryHeldCandidates(Held, new[] { "K-OPEN" });
        open.Should().NotThrow();
    }

    [Fact]
    public void TheGateRunsBeforeTheHeuristicAndTheCuratedRules()
    {
        var service = Source("Estimate", "EstimateWorkflowService.cs");
        var start = service.IndexOf("internal MappingProposalPublication ProposeMappingsUnpublished(", StringComparison.Ordinal);
        var method = service[start..service.IndexOf("internal static List<MappingProposalEngine.DiscoveredGroup> BuildMappingProposalGroups(", start, StringComparison.Ordinal)];
        var gate = method.IndexOf("LibraryReviews(LibraryDispositions(scan, snapshot, profile), groups)", StringComparison.Ordinal);
        gate.Should().BeGreaterThan(method.IndexOf("var governed = ", StringComparison.Ordinal), "after the L05 rules gate");
        gate.Should().BeLessThan(method.IndexOf("MappingProposalEngine.Propose(", StringComparison.Ordinal));
        gate.Should().BeLessThan(method.IndexOf("CuratedRuleProposals(", StringComparison.Ordinal));
        method.Should().Contain("reviews = reviews.Concat(libraryReviews).ToList();", "the reason is published with the run");
    }

    [Fact]
    public void TheAssistantIsGatedBeforeAndAfterItsAwait_ABatchBeforeItsSave_AndManualMappingIsNot()
    {
        var review = Source("UI", "CivilDeliveryControl.MappingReview.cs");
        var awaitAt = review.IndexOf("await assistant.AssistAsync(", StringComparison.Ordinal);
        var gates = AllIndexes(review, "LibraryProposalGate.Refusal(EstimateWorkflowService.LibraryDispositions(scan, catalog, scope.Profile), group.RuleKey)");
        gates.Should().HaveCount(2);
        gates[0].Should().BeLessThan(awaitAt);
        gates[1].Should().BeGreaterThan(awaitAt);

        var palette = Source("UI", "CivilDeliveryControl.xaml.cs");
        var batch = palette[palette.IndexOf("private void OnApproveProvenMappings(", StringComparison.Ordinal)..];
        batch.IndexOf("EstimateWorkflowService.RequireNoLibraryHeldCandidates(", StringComparison.Ordinal).Should()
            // The call text is split so LiveProfileGuardTests (which scans test sources for real writer calls) does not read
            // this search string as a call.
            .BeGreaterThan(0).And.BeLessThan(batch.IndexOf("_estimate." + "SaveApprovedMappings(", StringComparison.Ordinal));

        // Manual mapping (every guard of item, unit, price list, source and approval) stays available for a held group.
        var save = review[review.IndexOf("void SaveChoices(", StringComparison.Ordinal)..];
        save[..Math.Min(save.Length, 4000)].Should().NotContain("LibraryDispositions").And.NotContain("LibraryProposalGate");
    }

    private static List<int> AllIndexes(string text, string value)
    {
        var result = new List<int>();
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            result.Add(i);
        return result;
    }
}
