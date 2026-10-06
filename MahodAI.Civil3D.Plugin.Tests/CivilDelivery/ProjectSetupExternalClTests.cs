using System;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>File-identity and merge tests only; byte fixtures are not Civil DWG geometry.</summary>
public sealed class ProjectSetupExternalClTests
{
    [Fact]
    public void UnavailableExternalClKeepsActionableIncompleteScan_NotEmptySuccess()
    {
        var scan = new ProjectSetupScan { RunId = "test", ProjectProfileId = "new-site" };
        scan.ClLayerCandidates.Add(new ClLayerCandidate { Layer = "host-still-found" });
        ProjectSetupService.RecordExternalClFailure(scan, "missing-cl.dwg", "read failed");
        scan.ScanComplete.Should().BeFalse();
        scan.ClLayerCandidates.Should().ContainSingle();
        scan.Findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Error);
        scan.Findings[0].RecommendedAction.Should().Contain("בחר קובץ CL");
    }

    [Fact]
    public void SeparateClSingleLayerJoinsHostScan_AndChangesAreRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "synthetic-cl-evidence-" + Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(file, "SYNTHETIC NON-DWG SOURCE IDENTITY ONLY");
            var scan = new ProjectSetupScan { RunId = "test", ProjectProfileId = "new-site" };
            var external = new ProjectSetupScanner.ExternalClScan { Path = file, SourceHash = ArtifactHash.Sha256OfFile(file),
                SourceLastWriteUtc = File.GetLastWriteTimeUtc(file), ScanComplete = true };
            external.Candidates.Add(new ClLayerCandidate { Layer = "unknown-name", TwoPointCount = 1 });
            ProjectSetupService.MergeExternalClScan(scan, external);
            scan.ClLayerCandidates.Should().ContainSingle().Which.Layer.Should().Be("unknown-name");
            scan.ExternalClHashes.Should().ContainKey(file);
            ProjectSetupService.RequireExternalSourcesUnchanged(scan);
            File.WriteAllText(file, "CHANGED SOURCE");
            ((Action)(() => ProjectSetupService.RequireExternalSourcesUnchanged(scan))).Should().Throw<InvalidOperationException>();
            ((Action)(() => ProjectSetupService.MergeExternalClScan(
                new ProjectSetupScan { RunId = "new", ProjectProfileId = "new-site" }, external))).Should().Throw<InvalidOperationException>();
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
