using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Bounded synthetic curated-rule contract; no file writes or engineering approval.</summary>
public sealed class RealWallCountCuratedContractTests
{
    private const string Key = "layer:S_WALL_BT|count|block:6422-SP-MEDVA-ALL-2026-MHD%7CS_POINT_KR";
    private const string Layer = "6422-SP-MEDVA-ALL-2026-MHD|S_WALL_BT";
    private const string Code = "TEST-ONLY-WALL-UNIT";
    private static readonly MappingProposalEngine.DiscoveredGroup Group = new(Key, Layer, "count", "יח'", 2415, 2415);

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC-TEST-ONLY", FileHash = new string('a', 64) };
        catalog.Items.Add(Code, new CatalogItem { Code = Code, UnitRaw = "יח'", Description = "ארון להתקנה על קיר" });
        return catalog;
    }

    private static ProjectProfile Profile(string key, string layer)
    {
        var profile = EstimateFixtures.Profile();
        profile.Estimate.QuantitySources.Rules.Clear();
        profile.Estimate.QuantitySources.Rules.Add(new()
        {
            RuleKey = key, LayerPattern = layer, MeasurementKind = "count", ExpectedUnit = "יח'", CandidateCatalogCode = Code,
        });
        return profile;
    }

    [Fact]
    public void WallPointWithoutAssetIdentity_BroadOrMismatchedCuratedRuleCannotInventSubject()
    {
        foreach (var (key, layer) in new[]
        {
            ("TEST-ONLY-UNAPPROVED-WALL-DEFAULT", "*WALL*"),
            ("TEST-ONLY-UNAPPROVED-WALL-DEFAULT", "S_WALL_BT"),
            (Key, "*WALL*"),
            (Key.Replace("S_POINT_KR", "OTHER_BLOCK"), "S_WALL_BT"),
        })
        {
            var profile = Profile(key, layer);
            EstimateWorkflowService.CuratedRuleProposals(new[] { Group }, Catalog(), profile).Should().BeEmpty(
                "a generic wall/default or a different block is not positive identity for the measured count group");
            profile.Estimate.QuantitySources.Rules.Should().ContainSingle();
            CivilQuantityExtractionService.IsExplicitlyApproved(profile, profile.Estimate.QuantitySources.Rules[0]).Should().BeFalse();
        }
    }

    [Fact]
    public void ExactNamedBlockAndExactLayerCuratedRule_RemainsOnlyAnUnapprovedProposal()
    {
        foreach (var layer in new[] { "S_WALL_BT", Layer })
        {
            var profile = Profile(Key, layer);
            var proposal = EstimateWorkflowService.CuratedRuleProposals(new[] { Group }, Catalog(), profile)
                .Should().ContainSingle().Which;
            proposal.RuleKey.Should().Be(Key); proposal.ProposedCode.Should().Be(Code);
            proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
            proposal.EvidenceKind.Should().Be("profile-rule");
            CivilQuantityExtractionService.IsExplicitlyApproved(profile, profile.Estimate.QuantitySources.Rules[0]).Should().BeFalse();
            Group.ObjectCount.Should().Be(2415); Group.TotalQuantity.Should().Be(2415);
        }
    }
}
