using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateRecoveryUiTests
{
    [Fact]
    public void PresentationChangesNotifyAllVisibleBindingsAndLegacyMappingIsNotGreen()
    {
        var row = new QuantityRowViewModel
        {
            RuleKey = "r", Layer = "unknown", EntityType = "LINE", Method = "line-length",
            ObjectCount = 1, Quantity = 8, Unit = "m", MappingState = "מאושר", Price = "10",
        };
        row.Severity.Should().Be("review"); row.StatusDisplay.Should().Be("שיוך מאושר");
        var changed = new List<string>(); row.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        row.Presentation = new("בדיקת מדידה", "Actual source failure", null, "review", true, true, false);
        changed.Should().BeEquivalentTo(new[] { "StatusDisplay", "PriceDisplay", "StatusDetail", "Severity" });
        row.PriceDisplay.Should().BeNull(); row.Price.Should().Be("10"); // no fallback or model mutation
        row.StatusDetail.Should().Be("Actual source failure");
    }

    [Fact]
    public void UnifiedReadableReviewIncludesActualBuiltMissingPriceAndEveryExportReason()
    {
        var line = new EstimateLine
        {
            LineId = "line1", RecordId = "record1", RuleKey = "layer:UNKNOWN|count",
            CatalogCode = "REAL-CODE", PriceStatus = PriceStatus.MissingPrice, Price = null,
            Findings = { new DeliveryFinding { Code = EstimateFindingCodes.MissingPrice, Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired, Title = "Exact missing price", Message = "Exact edition details" } },
        };
        var result = new EstimateResult { RunId = "test", ProjectProfileId = "fixture", Lines = { line } };
        var issues = EstimateReviewPolicy.Collect(Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>(),
            Array.Empty<DeliveryFinding>(), result);
        var text = EstimateGuidedReviewText.BuildUnified("blocked", "fixture.dwg", Array.Empty<(string?, string?)>(), issues);
        text.Should().Contain("Exact missing price").And.Contain("Exact edition details").And.Contain("REAL-CODE")
            .And.Contain("חותמת זמן").And.Contain("אין מחיר חלופי או אפס");
        foreach (var reason in EstimatePreflightPolicy.ExportBlockingReasons(result)) text.Should().Contain(reason);
    }

    [Fact]
    public void ProductionUiWiresResultEvidencePresentationBatchAndReadOnlyMaterialPages()
    {
        var root = typeof(EstimateRecoveryUiTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        var guidance = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.EstimateGuidance.cs"));
        guidance.Should().Contain("_catalogFindings, _estimateResult").And.Contain("RefreshEstimateRowPresentation(estimateScanFresh)")
            .And.Contain("OnApproveProvenMappings(sender, e)").And.Contain("_pendingWorkflowSaveDocument != null")
            .And.NotContain("_pendingEstimateSaveDatabase");
        var xaml = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
        xaml.Should().Contain("Binding StatusDisplay").And.Contain("Binding PriceDisplay").And.Contain("Binding StatusDetail");
        var areas = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.MaterialAreas.cs"));
        areas.Should().Contain("MaterialSectionAreaReviewPolicy.Read").And.Contain("IsReadOnly = true")
            .And.Contain("VerticalScrollBarVisibility = ScrollBarVisibility.Auto")
            .And.NotContain("NeutralQuantityRecord").And.NotContain("SaveApproved").And.NotContain("GetObject(");
    }
}
