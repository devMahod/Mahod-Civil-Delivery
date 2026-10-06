using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateStaleScanGuidanceTests
{
    private static EstimateGuidedActionPolicy.Snapshot ExistingScan() => new(
        Available: true, SavePending: false, SourcesApproved: true,
        EarthworksResolved: false, SaveMayBeRequired: false, HasFreshScan: false,
        QuantityGroups: 1031, PendingReviewGroups: 1030, CatalogReady: true,
        EstimateBuilt: true, ExportReady: true, BlockingFindings: 0,
        HasApprovedMappings: true, PartialPricedDraftReady: true, HasExistingScan: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingScanExplainsRenewalAndNeverRoutesToOldBuildOrExport(bool needsSave)
    {
        var state = EstimateGuidedActionPolicy.Evaluate(ExistingScan() with
        {
            SaveMayBeRequired = needsSave,
            FreshnessReason = "מקור השרטוט השתנה מאז הסריקה",
        });
        state.Next.Should().Be(EstimateGuidedActionPolicy.Action.Scan);
        state.Detail.Should().Contain("הסריקה הקודמת").And.Contain("מקור השרטוט השתנה")
            .And.Contain("לעיון בלבד").And.NotContain("מדוד תחילה");
        state.Caption.Should().Be(needsSave ? "שמור וחדש את הסריקה…" : "חדש את הסריקה");
    }

    [Fact]
    public void MissingReasonDoesNotInventAnAutosaveOrUserCause()
    {
        var state = EstimateGuidedActionPolicy.Evaluate(ExistingScan());
        state.Detail.Should().Contain("לא ניתן לאמת").And.NotContain("אוטומטית")
            .And.NotContain("המשתמש");
    }

    [Fact]
    public void SaveRequiredUsesKnownSavePrerequisiteWhenNoReasonWasCaptured()
    {
        var state = EstimateGuidedActionPolicy.Evaluate(ExistingScan() with
        {
            SaveMayBeRequired = true,
            FreshnessReason = "  ",
        });
        state.Detail.Should().Contain("נדרשת שמירת השרטוט");
    }

    [Fact]
    public void FirstScanStillExplainsMeasurementWithoutCatalogOrEarthworks()
    {
        var state = EstimateGuidedActionPolicy.Evaluate(ExistingScan() with
        {
            HasExistingScan = false, QuantityGroups = 0, CatalogReady = false,
        });
        state.Next.Should().Be(EstimateGuidedActionPolicy.Action.Scan);
        state.Detail.Should().Contain("מדוד תחילה").And.Contain("אינם נדרשים");
    }

    [Fact]
    public void FreshScanDoesNotShowAnOldStaleReasonOrForceAnotherScan()
    {
        var state = EstimateGuidedActionPolicy.Evaluate(ExistingScan() with
        {
            HasFreshScan = true, FreshnessReason = "סיבה ישנה",
        });
        state.Next.Should().Be(EstimateGuidedActionPolicy.Action.ExportPricedDraft);
        state.Detail.Should().NotContain("סיבה ישנה");
    }

    [Fact]
    public void PendingSaveRemainsDisabledEvenWithHistoricalResults()
    {
        var state = EstimateGuidedActionPolicy.Evaluate(ExistingScan() with { SavePending = true });
        state.Next.Should().Be(EstimateGuidedActionPolicy.Action.WaitForSave);
        state.Enabled.Should().BeFalse();
    }
}
