using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SurveyPavementMarkerCuratedProposalTests
{
    [Fact]
    public void ExactNative76Marker_LegacyPavingRuleCannotBypassSharedSubjectGuard()
    {
        const string survey = "6422-SP-MEDVA-ALL-2026-MHD";
        var group = new MappingProposalEngine.DiscoveredGroup(
            "layer:S_PAVEMENT_UP|count|block:" + survey + "%7CS_POINT_E%2B",
            survey + "|S_PAVEMENT_UP", "count", "יח'", 2330, 2330);
        var profile = EstimateFixtures.Profile();
        profile.Estimate.QuantitySources.Rules.Add(
            new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = "TEST-ONLY-UNAPPROVED-PAVING",
                LayerPattern = "S_PAVEMENT_UP", MeasurementKind = "count",
                ExpectedUnit = "יח'", CandidateCatalogCode = "U51.06.4520",
            });
        var snapshot = new CatalogSnapshot { SnapshotId = "TEST-ONLY", FileHash = new string('a', 64) };
        snapshot.Items.Add("U51.06.4520", new CatalogItem
        {
            Code = "U51.06.4520", UnitRaw = "יח'",
            Description = "סורג מאלומיניום לפתח ערוגה של עץ קיים נמוך ממפלס ריצוף מתוכנן",
        });

        EstimateWorkflowService.CuratedRuleProposals(new[] { group }, snapshot, profile).Should().BeEmpty();
        // A named paving unit is not a tree grille merely because both mention paving.
        // Preserve the positive workflow with its actual subject, not the old wrong item.
        var pavingUnit = group with { RuleKey = "layer:S_PAVEMENT_UP|count|block:" + survey + "%7CPAVING_UNIT" };
        EstimateWorkflowService.CuratedRuleProposals(new[] { pavingUnit }, snapshot, profile).Should().BeEmpty(
            "the broad legacy paving rule must not turn a paving unit into a tree grille");
        snapshot.Items.Add("TEST-PAVING-UNIT", new CatalogItem
        {
            Code = "TEST-PAVING-UNIT", UnitRaw = "יח'", Description = "יחידת ריצוף",
        });
        profile.Estimate.QuantitySources.Rules.Add(
            new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = "TEST-ONLY-UNAPPROVED-PAVING-UNIT",
                LayerPattern = "S_PAVEMENT_UP", MeasurementKind = "count",
                ExpectedUnit = "יח'", CandidateCatalogCode = "TEST-PAVING-UNIT",
            });
        EstimateWorkflowService.CuratedRuleProposals(new[] { pavingUnit }, snapshot, profile)
            .Should().ContainSingle().Which.Should().Match<MappingProposal>(proposal =>
                proposal.ProposedCode == "TEST-PAVING-UNIT" && proposal.Status == "PROPOSED_UNAPPROVED");
        EstimateWorkflowService.CuratedRuleProposals(new[] { group }, snapshot, profile).Should().BeEmpty();
        group.ObjectCount.Should().Be(2330);
        group.TotalQuantity.Should().Be(2330);
        QuantitySignificance.Classify(new(group.RuleKey, group.Layer, group.MeasuredUnit,
            group.TotalQuantity, group.ObjectCount)).Kind.Should().Be(QuantitySignificance.Kind.Quantity);
    }
}
