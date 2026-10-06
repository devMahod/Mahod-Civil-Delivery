using System.Reflection;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateDerivedEvidencePublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcd-derived-head-test-" + Guid.NewGuid().ToString("N"));
    private const string Run = "SYNTHETIC-ONLY-scan";

    [Fact]
    public void BothPricedExportHeadsBelongToTheSharedInvalidationPolicy()
    {
        EstimateWorkflowService.DerivedEstimateArtifacts.Should().BeEquivalentTo(new[]
        {
            "estimate_result.json", "export_result.json", "mapping_proposals.json",
            "partial_priced_export_result.json",
        });
    }

    [Theory]
    [InlineData("estimate_scan.json")]
    [InlineData("mapping_proposals.json")]
    public void SuccessfulReplacementRemovesOldDerivedHeadsButKeepsMeasurementAndHistory(string newHead)
    {
        var previous = Seed();
        var manifestFiles = Array.Empty<string>();
        SectionsWorkflowService.PersistEvidenceBundle(Run, newHead, new { Version = 2 },
            (pendingRoot, publishedRoot) =>
            {
                publishedRoot.Should().Be(_root);
                var pendingRun = Path.Combine(pendingRoot, Run);
                manifestFiles = Directory.GetFiles(pendingRun).Select(Path.GetFileName).ToArray()!;
                File.WriteAllText(Path.Combine(pendingRun, "run_manifest.json"),
                    JsonSerializer.Serialize(new { Files = manifestFiles }));
            }, replaceExisting: true,
            removeArtifacts: EstimateWorkflowService.DerivedEstimateArtifacts,
            runsRoot: _root);

        foreach (var obsolete in EstimateWorkflowService.DerivedEstimateArtifacts.Where(name => name != newHead))
        {
            File.Exists(Path.Combine(_root, Run, obsolete)).Should().BeFalse();
            manifestFiles.Should().NotContain(obsolete);
        }
        File.ReadAllText(Path.Combine(_root, Run, newHead)).Should().Contain("2");
        foreach (var preserved in new[] { "neutral_quantity_records.json", "quantity_preflight.json",
                     "estimate_scan.before-profile-2.json", "hatch_area_failure_diagnostics.json" })
            File.ReadAllBytes(Path.Combine(_root, Run, preserved)).Should().Equal(previous[preserved]);
        File.ReadAllText(Path.Combine(_root, "historical-export.xlsx")).Should().Be("old external package remains history");
    }

    [Fact]
    public void ManifestFailureLeavesEveryOriginalByteAndExportHeadUntouched()
    {
        var previous = Seed();
        Action replace = () => SectionsWorkflowService.PersistEvidenceBundle(Run, "estimate_scan.json",
            new { Version = 2 }, (_, _) => throw new IOException("synthetic manifest failure"),
            replaceExisting: true, removeArtifacts: EstimateWorkflowService.DerivedEstimateArtifacts,
            runsRoot: _root);
        replace.Should().Throw<IOException>().WithMessage("synthetic manifest failure");
        Directory.GetFiles(Path.Combine(_root, Run)).Select(Path.GetFileName).Should().BeEquivalentTo(previous.Keys);
        foreach (var item in previous)
            File.ReadAllBytes(Path.Combine(_root, Run, item.Key)).Should().Equal(item.Value);
        Directory.GetDirectories(_root).Select(Path.GetFileName).Should().Equal(Run);
    }

    [Fact]
    public void ReplacedCanonicalFilesAreNotReadOrCopied_AndHistoryStillSurvives()
    {
        var previous = Seed();
        // A read lock makes unnecessary seeding of these old canonical files fail.
        // Release before the final directory swap: this is a copy-cost probe, not
        // an assertion that Windows can rename a directory containing locked files.
        var locks = new[] { "estimate_scan.json", "neutral_quantity_records.json", "quantity_preflight.json" }
            .Select(name => new FileStream(Path.Combine(_root, Run, name), FileMode.Open,
                FileAccess.Read, FileShare.None)).ToArray();
        try
        {
            SectionsWorkflowService.PersistEvidenceBundle(Run, "estimate_scan.json", new { Version = 2 },
                (pendingRoot, _) =>
                {
                    foreach (var held in locks) held.Dispose();
                    File.WriteAllText(Path.Combine(pendingRoot, Run, "run_manifest.json"), "new manifest");
                }, replaceExisting: true, removeArtifacts: EstimateWorkflowService.DerivedEstimateArtifacts,
                stageAdditionalArtifacts: pendingRoot =>
                {
                    SectionsWorkflowService.WriteArtifact(Run, "neutral_quantity_records.json", new[] { 1, 2 }, pendingRoot);
                    SectionsWorkflowService.WriteArtifact(Run, "quantity_preflight.json", new[] { "still unresolved" }, pendingRoot);
                }, runsRoot: _root,
                replaceArtifacts: new[] { "neutral_quantity_records.json", "quantity_preflight.json" });
        }
        finally { foreach (var held in locks) held.Dispose(); }
        File.ReadAllBytes(Path.Combine(_root, Run, "estimate_scan.before-profile-2.json"))
            .Should().Equal(previous["estimate_scan.before-profile-2.json"]);
        File.ReadAllText(Path.Combine(_root, Run, "quantity_preflight.json")).Should().Contain("still unresolved");
    }

    [Fact]
    public void MissingDeclaredReplacementRefusesPublicationAndKeepsOriginalGeneration()
    {
        var previous = Seed();
        var calledManifest = false;
        Action replace = () => SectionsWorkflowService.PersistEvidenceBundle(Run, "estimate_scan.json",
            new { Version = 2 }, (_, _) => calledManifest = true, replaceExisting: true,
            runsRoot: _root, replaceArtifacts: new[] { "neutral_quantity_records.json" });
        replace.Should().Throw<IOException>().WithMessage("*Replacement evidence was not staged*");
        calledManifest.Should().BeFalse();
        foreach (var item in previous)
            File.ReadAllBytes(Path.Combine(_root, Run, item.Key)).Should().Equal(item.Value);
    }

    [Fact]
    public void CompressedHistoryRetainsEveryOriginalJsonByteIncludingPrecisionAndHebrew()
    {
        var payload = new
        {
            Source = "SYNTHETIC-ONLY מקור עם כיתוב", SourceHash = new string('a', 64),
            Records = Enumerable.Range(0, 1000).Select(i => new
            {
                Id = i, OriginalMeasurement = 0.12345678901234567 + i,
                Layer = "SYNTHETIC-ONLY-HW-CURB", Unit = "מטר", Approved = false,
                Finding = "מקור חסר אינו אפס — כל המידע המקורי נשמר בהיסטוריה",
            }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload, SectionsWorkflowService.Json);
        var file = SectionsWorkflowService.WriteCompressedArtifact(Run,
            "estimate_scan.before-profile-3.json.gz", payload, _root);
        using (var stream = File.OpenRead(file))
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip, System.Text.Encoding.UTF8))
            reader.ReadToEnd().Should().Be(json, "compression must retain the full snapshot, not a lossy summary");
        new FileInfo(file).Length.Should().BeLessThan(System.Text.Encoding.UTF8.GetByteCount(json) / 5,
            "a repetitive synthetic full snapshot should not retain duplicate uncompressed rows per decision");
        var before = File.ReadAllBytes(file);
        Action duplicate = () => SectionsWorkflowService.WriteCompressedArtifact(Run,
            "estimate_scan.before-profile-3.json.gz", new { Different = true }, _root);
        duplicate.Should().Throw<IOException>();
        File.ReadAllBytes(file).Should().Equal(before);
    }

    [Theory]
    [InlineData("WithdrawDerivedEstimateEvidence(", "internal static ScanResult AssembleScan(")]
    [InlineData("PublishRebasedScanEvidence(", "internal static ScanResult PublishProfileDecisionOrRestore(")]
    [InlineData("internal static void PublishMappingProposalEvidence(", "internal static List<MappingProposal> CuratedRuleProposals(")]
    public void AllInvalidatingRoutesUseTheSamePolicy(string startMarker, string endMarker)
    {
        var sourceDirectory = typeof(EstimateDerivedEvidencePublicationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == "MahodPluginSourceDir").Value!;
        var source = File.ReadAllText(Path.Combine(sourceDirectory, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        end.Should().BeGreaterThan(start);
        source[start..end].Should().Contain("removeArtifacts: DerivedEstimateArtifacts");
    }

    private Dictionary<string, byte[]> Seed()
    {
        var run = Path.Combine(_root, Run);
        Directory.CreateDirectory(run);
        foreach (var name in EstimateWorkflowService.DerivedEstimateArtifacts.Concat(new[]
                 { "estimate_scan.json", "neutral_quantity_records.json", "quantity_preflight.json",
                     "estimate_scan.before-profile-2.json", "hatch_area_failure_diagnostics.json", "run_manifest.json" }))
            File.WriteAllText(Path.Combine(run, name), "SYNTHETIC old bytes: " + name);
        File.WriteAllText(Path.Combine(_root, "historical-export.xlsx"), "old external package remains history");
        return Directory.GetFiles(run).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
