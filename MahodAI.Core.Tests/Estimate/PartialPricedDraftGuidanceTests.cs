using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public class PartialPricedDraftGuidanceTests
{
    private static EstimateGuidedActionPolicy.Snapshot Fixture() => new(
        Available: true, SavePending: false, SourcesApproved: true, EarthworksResolved: false,
        SaveMayBeRequired: false, HasFreshScan: true, QuantityGroups: 1041, PendingReviewGroups: 1040,
        CatalogReady: true, EstimateBuilt: false, ExportReady: false, BlockingFindings: 10,
        HasApprovedMappings: true);

    [Fact]
    public void OneApprovedGroupCanReachCalculationWithoutFakingCompleteProject()
    {
        var state = EstimateGuidedActionPolicy.Evaluate(Fixture());
        state.Next.Should().Be(EstimateGuidedActionPolicy.Action.Build);
        state.Detail.Should().Contain("טיוטה חלקית");
    }

    [Fact]
    public void OnlyExplicitEligibleDraftRoutesToPartialExport()
    {
        var built = Fixture() with { EstimateBuilt = true };
        EstimateGuidedActionPolicy.Evaluate(built).Next.Should().NotBe(EstimateGuidedActionPolicy.Action.ExportPricedDraft);
        EstimateGuidedActionPolicy.Evaluate(built with { PartialPricedDraftReady = true }).Next
            .Should().Be(EstimateGuidedActionPolicy.Action.ExportPricedDraft);
    }

    [Fact]
    public void StalenessSaveAndUnapprovedScopeCannotBeBypassed()
    {
        var built = Fixture() with { EstimateBuilt = true, PartialPricedDraftReady = true };
        EstimateGuidedActionPolicy.Evaluate(built with { HasFreshScan = false }).Next.Should().Be(EstimateGuidedActionPolicy.Action.Scan);
        EstimateGuidedActionPolicy.Evaluate(built with { SavePending = true }).Next.Should().Be(EstimateGuidedActionPolicy.Action.WaitForSave);
        EstimateGuidedActionPolicy.Evaluate(built with { SourcesApproved = false }).Next.Should().NotBe(EstimateGuidedActionPolicy.Action.ExportPricedDraft);
    }
}
