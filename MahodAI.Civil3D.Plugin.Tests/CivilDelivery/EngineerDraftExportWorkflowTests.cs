using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The engineer-draft export lifecycle, host-free: proof before and after the write, a temporary
/// name until the bytes are hashed, a unique final name, and withdrawal only of unchanged output.
/// </summary>
public sealed class EngineerDraftExportWorkflowTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("before-fresh")]
    [InlineData("before-published")]
    [InlineData("writer-failure")]
    [InlineData("after-fresh")]
    [InlineData("after-published")]
    [InlineData("changed-scan-proof")]
    [InlineData("publication-failure")]
    [InlineData("changed-output")]
    [InlineData("name-taken")]
    public void LifecycleRechecksProofWritesAtomicallyAndWithdrawsOnlyUnchangedOutput(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mahod-engineer-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var untouched = Path.Combine(directory, "existing.xlsx");
        File.WriteAllText(untouched, "user workbook");
        if (scenario == "name-taken") File.WriteAllText(Path.Combine(directory, "draft.xlsx"), "earlier draft");
        var calls = new List<string>();
        var fresh = 0; var proofs = 0;
        string? written = null;
        var proof = new PublishedArtifactProof(Path.Combine(directory, "scan.json"), new string('a', 64), "scan", "estimate", "extract");
        void RequireFresh()
        {
            calls.Add("fresh"); fresh++;
            if (scenario == "before-fresh" && fresh == 1 || scenario == "after-fresh" && fresh == 2)
                throw new InvalidOperationException("Source changed");
        }
        PublishedArtifactProof Published()
        {
            calls.Add("proof"); proofs++;
            if (scenario == "before-published" && proofs == 1 || scenario == "after-published" && proofs == 2)
                throw new InvalidDataException("Producer missing");
            return scenario == "changed-scan-proof" && proofs == 2 ? proof with { Hash = new string('b', 64) } : proof;
        }
        EngineerBoqDraftExcelWriter.WriteResult Write(string tempPath)
        {
            calls.Add("write");
            Assert.EndsWith(".tmp", tempPath, StringComparison.Ordinal);
            File.WriteAllText(tempPath, "new draft");
            written = tempPath;
            if (scenario == "writer-failure") throw new IOException("Disk full");
            return new EngineerBoqDraftExcelWriter.WriteResult(tempPath, 1, 1, 1, 10m, 1, 1);
        }
        string? publishedHash = null;
        void Publish(EngineerBoqDraftExcelWriter.WriteResult result, string hash, PublishedArtifactProof usedProof)
        {
            calls.Add("publish");
            publishedHash = hash;
            Assert.Equal(proof, usedProof);
            if (scenario == "changed-output") File.WriteAllText(result.XlsxPath, "concurrent user content");
            if (scenario is "publication-failure" or "changed-output") throw new IOException("Evidence write failed");
        }
        try
        {
            if (scenario is "success" or "name-taken")
            {
                var result = EstimateWorkflowService.ExecuteEngineerDraftExport(RequireFresh, Published, Write, directory, "draft", Publish);
                Assert.Equal(new[] { "fresh", "proof", "write", "fresh", "proof", "publish" }, calls);
                Assert.Equal(Path.Combine(directory, scenario == "name-taken" ? "draft-2.xlsx" : "draft.xlsx"), result.XlsxPath);
                Assert.Equal("new draft", File.ReadAllText(result.XlsxPath));
                Assert.Equal(ArtifactHash.Sha256OfFile(result.XlsxPath), publishedHash);
                if (scenario == "name-taken") Assert.Equal("earlier draft", File.ReadAllText(Path.Combine(directory, "draft.xlsx")));
            }
            else
            {
                var error = Assert.ThrowsAny<Exception>(() =>
                    EstimateWorkflowService.ExecuteEngineerDraftExport(RequireFresh, Published, Write, directory, "draft", Publish));
                var finalPath = Path.Combine(directory, "draft.xlsx");
                Assert.Equal(scenario == "changed-output", File.Exists(finalPath));
                if (scenario.StartsWith("before-", StringComparison.Ordinal)) Assert.DoesNotContain("write", calls);
                if (scenario == "changed-output")
                {
                    Assert.Equal("concurrent user content", File.ReadAllText(finalPath));
                    Assert.Contains("נשמר ללא מחיקה", error.Message);
                }
                if (scenario == "publication-failure")
                {
                    Assert.Contains("רישום הראיות נכשל", error.Message);
                    Assert.Contains("Evidence write failed", error.Message);
                    Assert.DoesNotContain("יש לסרוק מחדש", error.Message);
                }
                if (scenario is "after-fresh" or "after-published" or "changed-scan-proof")
                    Assert.Contains("יש לסרוק מחדש", error.Message);
            }
            // No temporary file survives any outcome, and a user's file is never touched.
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            if (written != null) Assert.False(File.Exists(written));
            Assert.Equal("user workbook", File.ReadAllText(untouched));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExclusionsUseTheApprovedEstimatePolicyExactly()
    {
        var at = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        var profile = new ProjectProfile { ProfileId = "6422" };
        profile.Estimate.IgnoredRuleDecisions.Add(new ProjectProfile.EstimateProfile.IgnoredRuleDecision
            { RuleKey = "layer:A|length", Reason = "annotation", ApprovedBy = "old", ApprovedAtUtc = at });
        profile.Estimate.IgnoredRuleDecisions.Add(new ProjectProfile.EstimateProfile.IgnoredRuleDecision
            { RuleKey = "layer:A|length", Reason = "annotation", ApprovedBy = "latest", ApprovedAtUtc = at.AddDays(1) });
        profile.Estimate.IgnoredRuleDecisions.Add(new ProjectProfile.EstimateProfile.IgnoredRuleDecision
            { RuleKey = "layer:B|length", Reason = null, ApprovedBy = "someone", ApprovedAtUtc = at });
        profile.Estimate.IgnoredRuleKeys.Add("layer:C|length");

        var exclusions = EstimateWorkflowService.AuthoritativeExclusions(profile);

        Assert.Equal(new[] { "layer:A|length" }, exclusions.Keys.ToArray());
        Assert.Equal("latest", exclusions["layer:A|length"]);
        Assert.False(exclusions.ContainsKey("LAYER:A|LENGTH"));
    }

    [Fact]
    public void ProductionWiringUsesTheSeamNativeProofAndTheBusyOverlay()
    {
        var root = EstimateFixtures.RepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/Estimate/EstimateWorkflowService.EngineerDraft.cs"));
        Assert.Contains("return ExecuteEngineerDraftExport(", service);
        Assert.Contains("() => RequireFresh(doc, scan, operation)", service);
        // b24 (Codex 13:21): the unit evidence is checked with the published-scan proof, before the build.
        Assert.Contains("RequireTrustedUnitEvidence(scan, operation);", service);
        Assert.Contains("return RequirePublishedScanEvidence(scan);", service);
        Assert.Contains("DeliveryStatus.ReviewRequired", service);
        Assert.Contains("new RunManifestArtifactInput(written.XlsxPath, xlsxHash)", service);
        Assert.Contains("AuthoritativeExclusions(profile)", service);
        var ui = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/UI/CivilDeliveryControl.EngineerDraft.cs"));
        Assert.Contains("RunBusy(", ui);
        Assert.Contains("scanFresh && _scan is { Records.Count: > 0 }", ui);
        Assert.DoesNotContain("OnApprove", ui);
    }
}
