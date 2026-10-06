using System;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateQuantityHistoryContractTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery", "UI", file)).Replace("\r\n", "\n");

    [Fact] public void HistoricalRowKeepsMeasuredValueButSuppressesPriceGreenAndApprovalEvenIfPreviouslyReady()
    {
        var row = new QuantityRowViewModel
        {
            RuleKey = "synthetic:kerb", Layer = "KERB", EntityType = "LINE", Method = "line-length",
            ObjectCount = 3, Quantity = 37.25, Unit = "m", MappingState = "מאושר", CatalogCode = "TEST.1",
            Price = "100.00", Presentation = new("מתומחר ותקין", "previous build", "123.00", "ok", true, false, true),
        };
        row.CanApproveCatalogMapping.Should().BeTrue();
        row.HistoricalReason = "scope changed";
        row.Quantity.Should().Be(37.25);
        row.ObjectCount.Should().Be(3);
        row.Unit.Should().Be("m");
        row.IsHistorical.Should().BeTrue();
        row.StatusDisplay.Should().Be("מדידה קודמת");
        row.StatusDetail.Should().Contain("מדידה היסטורית — לא עדכנית");
        row.StatusDetail.Should().Contain("scope changed");
        row.PriceDisplay.Should().BeNull();
        row.Severity.Should().Be("review");
        row.CanApproveCatalogMapping.Should().BeFalse();
        row.IsBulkNoiseCandidate.Should().BeFalse();
        row.Presentation = new("late stale refresh", "must not restore authority", "999.00", "ok", true, false, true);
        row.PriceDisplay.Should().BeNull();
        row.Severity.Should().Be("review");
    }

    [Fact] public void InvalidationRetainsOriginalScanOnlyForDisplayAndRevokesEveryActionableArtifact()
    {
        var text = Source("CivilDeliveryControl.xaml.cs");
        var start = text.IndexOf("private void InvalidateEstimateEvidence", StringComparison.Ordinal);
        var end = text.IndexOf("private void ContinueEstimateReviewAfterDecision", start, StringComparison.Ordinal);
        var body = text.Substring(start, end - start);
        body.Should().Contain("var previous = _scan ?? _historicalScan;")
            .And.Contain("EstimateQuantityHistoryPolicy.CanRetain(")
            .And.Contain("_scan = null;").And.Contain("_estimateResult = null;")
            .And.Contain("_catalog = null;").And.Contain("_proposals.Clear();")
            .And.Contain("row.HistoricalReason = detail;")
            .And.Contain("row.Presentation = EstimateQuantityHistoryPolicy.Presentation(detail)")
            .And.Contain("_estimateResultsDrawing = _historicalScan?.SourceDrawing;")
            .And.NotContain("new EstimateWorkflowService.ScanResult");
        text.Should().Contain("var estimateScanFresh = _scan != null && !EstimateScanIsKnownStale();")
            .And.Contain("BtnApprove.IsEnabled = profileUsable && estimateScanFresh")
            .And.Contain("BtnBuild.IsEnabled = profileUsable && estimateScanFresh")
            .And.Contain("EstimatePreflightPolicy.CanExport(_estimateResult) &&")
            .And.Contain("if (doc == null || _scan == null) return;");
    }

    [Fact] public void SwitchingDrawingClearsHistoryAndSuccessfulScanReplacesItBeforePublishingRows()
    {
        var text = Source("CivilDeliveryControl.xaml.cs");
        var start = text.IndexOf("if (estimateChanged)", StringComparison.Ordinal);
        var end = text.IndexOf("if (sectionChanged || estimateChanged)", start, StringComparison.Ordinal);
        text.Substring(start, end - start).Should().Contain("_historicalScan = null;")
            .And.Contain("_quantityRows.Clear();").And.Contain("QuantityDetail.Text =");
        start = text.IndexOf("_scan = _estimate.Scan(", StringComparison.Ordinal);
        end = text.IndexOf("RebuildQuantityRows();", start, StringComparison.Ordinal);
        text.Substring(start, end - start).Should().Contain("_historicalScan = null;");
        var vm = Source("CivilDeliveryViewModels.cs");
        vm.Should().Contain("DisplayPresentation?.PriceDisplay ?? (DisplayPresentation == null ? Price : null)")
            .And.Contain("public bool CanApproveCatalogMapping =>\n            !IsHistorical")
            .And.Contain("public string Severity => IsHistorical ? \"review\"");
    }
}
