using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Source-wiring checks supplement (not replace) the behavioral Core export tests.
public sealed class PartialPricedDraftWorkflowContractTests
{
    [Fact]
    public void PartialPublicationRevalidatesAfterWritingAndBeforePublishing()
    {
        var path = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery", "Estimate", "EstimateWorkflowService.cs");
        var source = File.ReadAllText(path);
        var method = source[source.IndexOf("private EstimateExcelWriter.WriteResult ExportCore(", StringComparison.Ordinal)..];
        var write = method.IndexOf("EstimateExcelWriter.WritePartialPricedDraft", StringComparison.Ordinal);
        var postWriteFreshness = method.IndexOf("RequireFresh(doc, scan", write, StringComparison.Ordinal);
        var publish = method.IndexOf("SectionsWorkflowService.PersistEvidenceBundle", write, StringComparison.Ordinal);
        postWriteFreshness.Should().BeGreaterThan(write).And.BeLessThan(publish);
        method[postWriteFreshness..publish].Should().Contain("RequirePublishedEstimateBuildEvidence(scan, estimate)")
            .And.Contain("catalogAfterWrite").And.Contain("EffectiveProfileHash(profile)");
        method[publish..].Should().Contain("File.Delete(path)").And.Contain("withdrawn");
    }

    [Fact]
    public void PricedDraftCanBeFollowedByUnpricedMeasurementEvidence()
    {
        var root = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery");
        File.ReadAllText(Path.Combine(root, "Estimate", "EstimateWorkflowService.MeasurementDraft.cs"))
            .Should().Contain("\"propose\", \"build\", \"export\", \"export-partial\"");
        var ui = File.ReadAllText(Path.Combine(root, "UI", "CivilDeliveryControl.PricedDraft.cs"));
        ui.Should().Contain("!profileUsable || !scanFresh").And.Contain("!draft.CanExport")
            .And.Contain("תוצאה קודמת").And.Contain("לא מוצג סכום מאושר");
    }
}
