using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Wiring checks supplement the serializer behavior tests, not native CAD acceptance.</summary>
public sealed class MaterialAreaExportWorkflowContractTests
{
    [Fact]
    public void ExportRequiresFreshPublishedScanBeforeAndAfterSerializationAndKeepsScanManifest()
    {
        var source = File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(),
            "MahodAI.Civil3D.Plugin", "CivilDelivery", "Estimate", "EstimateWorkflowService.MaterialAreas.cs"));
        var before = source.IndexOf("var written =", StringComparison.Ordinal);
        source[..before].Should().Contain("RequireFresh(doc, scan,")
            .And.Contain("RequirePublishedScanEvidence(scan)");
        source[before..].Should().Contain("RequireFresh(doc, scan,")
            .And.Contain("RequirePublishedScanEvidence(scan)")
            .And.Contain("reportRunId, \"material_area_export.json\"")
            .And.Contain("new RunManifestInput(proof.Path, proof.Hash)")
            .And.Contain("DeliveryStatus.ReviewRequired")
            .And.Contain("File.Delete(written.XlsxPath)")
            .And.Contain("ArtifactHash.Sha256OfFile(written.XlsxPath), written.XlsxHash")
            .And.NotContain("WriteEstimateManifest(scan")
            .And.NotContain("EstimateExcelWriter.Write(");
    }

    [Fact]
    public void ViewExportsAllCapturedRowsWithoutAuthorizingHistoricalEvidenceOrAutomaticOpen()
    {
        var source = File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(),
            "MahodAI.Civil3D.Plugin", "CivilDelivery", "UI", "CivilDeliveryControl.MaterialAreas.cs"));
        source.Should().Contain("var capturedScan = _scan;")
            .And.Contain("ReferenceEquals(capturedScan, _scan)")
            .And.Contain("_estimate.ExportMaterialAreas(doc, capturedScan)")
            .And.Contain("open.Click +=")
            .And.Contain("ללא תמחור")
            .And.NotContain("_historicalScan")
            .And.NotContain("MessageBox.Show");
    }
}
