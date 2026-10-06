using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class MeasurementDraftWorkflowTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("before-fresh")]
    [InlineData("before-published")]
    [InlineData("after-fresh")]
    [InlineData("after-published")]
    [InlineData("changed-scan-proof")]
    [InlineData("proposal-appeared")]
    [InlineData("publication-failure")]
    [InlineData("changed-output")]
    public void LifecycleRechecksProofAndWithdrawsOnlyItsUnchangedOutput(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mahod-measurement-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "new.xlsx");
        var untouched = Path.Combine(directory, "existing.xlsx");
        File.WriteAllText(untouched, "user workbook");
        var calls = new List<string>();
        var fresh = 0; var proofCount = 0;
        var scanProof = new PublishedArtifactProof(Path.Combine(directory, "scan.json"), new string('a', 64), "scan", "estimate", "extract");
        void RequireFresh()
        {
            calls.Add("fresh"); fresh++;
            if (scenario == "before-fresh" && fresh == 1 || scenario == "after-fresh" && fresh == 2)
                throw new InvalidOperationException("Source changed");
        }
        EstimateWorkflowService.MeasurementDraftEvidence Evidence()
        {
            calls.Add("proof"); proofCount++;
            if (scenario == "before-published" && proofCount == 1 || scenario == "after-published" && proofCount == 2)
                throw new InvalidDataException("Producer missing or modified");
            return new(scenario == "changed-scan-proof" && proofCount == 2 ? scanProof with { Hash = new string('b', 64) } : scanProof,
                scenario == "proposal-appeared" && proofCount == 2 ? scanProof with { Path = Path.Combine(directory, "proposal.json") } : null,
                Array.Empty<MappingProposal>(), null, null);
        }
        MeasurementDraftExcelWriter.WriteResult Write(EstimateWorkflowService.MeasurementDraftEvidence _)
        {
            calls.Add("write"); File.WriteAllText(destination, "new draft");
            return new(destination, ArtifactHash.Sha256OfFile(destination), 1, 0, 0);
        }
        void Publish(MeasurementDraftExcelWriter.WriteResult _, EstimateWorkflowService.MeasurementDraftEvidence __)
        {
            calls.Add("publish");
            if (scenario == "changed-output") File.WriteAllText(destination, "concurrent user content");
            if (scenario is "publication-failure" or "changed-output") throw new IOException("Publication failed");
        }
        try
        {
            if (scenario == "success")
            {
                var result = EstimateWorkflowService.ExecuteMeasurementDraftExport(RequireFresh, Evidence, Write, Publish);
                Assert.Equal(destination, result.XlsxPath);
                Assert.Equal(new[] { "fresh", "proof", "write", "fresh", "proof", "publish" }, calls);
                Assert.True(File.Exists(destination));
            }
            else
            {
                Assert.ThrowsAny<Exception>(() => EstimateWorkflowService.ExecuteMeasurementDraftExport(RequireFresh, Evidence, Write, Publish));
                Assert.Equal(scenario == "changed-output", File.Exists(destination));
                if (scenario.StartsWith("before-", StringComparison.Ordinal)) Assert.DoesNotContain("write", calls);
                if (scenario == "changed-output") Assert.Equal("concurrent user content", File.ReadAllText(destination));
            }
            Assert.Equal("user workbook", File.ReadAllText(untouched));
        }
        finally { Directory.Delete(directory, true); }
    }

    [ActualMeasurementDraftFact]
    public void Actual74891ScanAndProposalPublicationAreConsumedExactlyWithoutReadingDwgSources()
    {
        var scan = JsonSerializer.Deserialize<EstimateWorkflowService.ScanResult>(
            File.ReadAllText(ActualMeasurementDraftFactAttribute.ScanPath), SectionsWorkflowService.Json)!;
        Assert.Equal(74891, scan.Records.Count);
        var proof = EstimateWorkflowService.CaptureMeasurementDraftEvidence(scan);
        Assert.Equal(ArtifactHash.Sha256OfFile(ActualMeasurementDraftFactAttribute.ScanPath), proof.Scan.Hash);
        Assert.NotNull(proof.Proposals);
        Assert.NotEmpty(proof.MappingProposals);
        Assert.Equal("judgment2-golden.xlsx", proof.ReferenceSource);
        Assert.All(proof.MappingProposals, proposal => Assert.Equal("PROPOSED_UNAPPROVED", proposal.Status));
        scan.Status = scan.Status == DeliveryStatus.Ready ? DeliveryStatus.Failed : DeliveryStatus.Ready;
        Assert.ThrowsAny<Exception>(() => EstimateWorkflowService.CaptureMeasurementDraftEvidence(scan));
    }

    [Fact]
    public void ConsolidationPreservesEveryOneOfNinetyFailuresAndDoesNotRelaxItsError()
    {
        var extraction = new CivilQuantityExtractionService.ExtractionResult();
        for (var i = 0; i < 90; i++) extraction.Findings.Add(new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = $"object-{i:D3}", Message = $"inner failure {i:D3}", AffectedRecordIds = new() { $"record-{i:D3}" },
            EvidenceRefs = new() { $"evidence-{i:D3}" },
        });
        CivilQuantityExtractionService.ConsolidateMeasurementFailures(extraction, "6422");
        var finding = Assert.Single(extraction.Findings);
        Assert.StartsWith("Complete measurement failures (90):\n", finding.Message);
        for (var i = 0; i < 90; i++) Assert.Contains($"object-{i:D3}: inner failure {i:D3}", finding.Message);
        Assert.Equal(90, finding.AffectedRecordIds.Count);
        Assert.Equal(90, finding.EvidenceRefs.Count);
        Assert.True(EstimatePreflightPolicy.IsBlocking(finding));
    }

    [Fact]
    public void ProductionWiringKeepsNativeFreshnessAndReviewRequiredSeparateFromApprovedExport()
    {
        var root = EstimateFixtures.RepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/Estimate/EstimateWorkflowService.MeasurementDraft.cs"));
        Assert.Contains("() => RequireFresh(doc, scan,", service);
        Assert.Contains("RequirePublishedScanEvidence(scan)", service);
        Assert.Contains("DeliveryStatus.ReviewRequired", service);
        Assert.Contains("new RunManifestArtifactInput(written.XlsxPath, written.XlsxHash)", service);
        Assert.DoesNotContain("EstimateExcelWriter.Write(", service);
        Assert.DoesNotContain("WriteEstimateManifest(scan", service);
        var xaml = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/UI/CivilDeliveryControl.xaml"));
        // The caption is user-facing and can change; the actual accessibility
        // contract is a visible sibling action group before advanced controls.
        var tree = System.Xml.Linq.XDocument.Parse(xaml);
        System.Xml.Linq.XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var export = tree.Descendants().Single(element =>
            (string?)element.Attribute(names + "Name") == "BtnExportMeasurementDraft");
        var advanced = tree.Descendants().Single(element =>
            (string?)element.Attribute(names + "Name") == "EstimateAdvancedActions");
        Assert.Equal("Button", export.Name.LocalName);
        Assert.Equal("WrapPanel", export.Parent!.Name.LocalName);
        Assert.Same(advanced.Parent, export.Parent.Parent);
        Assert.Contains(advanced, export.Parent.ElementsAfterSelf());
        Assert.DoesNotContain(export.Ancestors(), element => element.Name.LocalName == "Expander");
        Assert.DoesNotContain(export.AncestorsAndSelf(), element =>
            (string?)element.Attribute("Visibility") is "Collapsed" or "Hidden");
        var ui = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/UI/CivilDeliveryControl.MeasurementDraft.cs"));
        Assert.Contains("scanFresh && _scan is { Records.Count: > 0 }", ui);
        Assert.DoesNotContain("_estimateResult", ui);
        Assert.DoesNotContain("_historicalScan", ui);
        Assert.DoesNotContain("OnApprove", ui);
    }
}

public sealed class ActualMeasurementDraftFactAttribute : FactAttribute
{
    public static string ScanPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodAI_Civil3D/civil-delivery/runs/estimate-extract-20260909-080304-0dcf329b/estimate_scan.json");
    public ActualMeasurementDraftFactAttribute()
    {
        if (!File.Exists(ScanPath)) Skip = "Actual 1.2.52 74,891-record local scan is unavailable; not native export acceptance.";
    }
}
