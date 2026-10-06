using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class UnsupportedEntityCoverageProvenanceTests
{
    [Fact]
    public void HostAndXrefEvidenceRetainExactContextWithoutBecomingMeasuredOrApproved()
    {
        var host = Source("AB", null, @"C:\TEST-ONLY\host.dwg", new string('a', 64), "DBPoint", "נקודות");
        var xref = Source("CD/AB", "HA > nested", @"C:\TEST-ONLY\HA.dwg", new string('b', 64), "Entity", "HA|לא ידוע");
        var before = JsonSerializer.Serialize(new[] { host, xref });
        var finding = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(
            new Dictionary<string, int> { ["DBPOINT"] = 1, ["ENTITY"] = 1 }, "6422", new[] { host, xref })!;

        finding.Message.Should().Be("DBPOINT=1, ENTITY=1");
        finding.SourceRefs.Should().Equal(host, xref);
        JsonSerializer.Serialize(finding.SourceRefs).Should().Be(before);
        AssertGlobalBlocker(finding);
        var issue = EstimateReviewPolicy.Collect(Array.Empty<NeutralQuantityRecord>(),
            new[] { finding }, Array.Empty<DeliveryFinding>(), null).Should().ContainSingle().Which;
        issue.Sources.Should().Equal(host, xref);
        issue.Blocking.Should().BeTrue();
        issue.RecordIds.Should().BeEmpty();
        issue.RuleKeys.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateEvidenceIsDeduplicatedButDifferentInstancesHashesAndLayersSurvive()
    {
        var first = Source("CD/AB", "HA", @"C:\TEST-ONLY\HA.dwg", new string('a', 64));
        var identicalCopy = JsonSerializer.Deserialize<ProvenanceRef>(JsonSerializer.Serialize(first))!;
        var anotherInstance = Source("EF/AB", "HA", first.SourcePathOrUri!, first.DrawingChecksum!);
        var anotherChain = Source("CD/AB", "GM > HA", first.SourcePathOrUri!, first.DrawingChecksum!);
        var anotherHash = Source("CD/AB", "HA", first.SourcePathOrUri!, new string('b', 64));
        var anotherLayer = Source("CD/AB", "HA", first.SourcePathOrUri!, first.DrawingChecksum!, layer: "other");
        var finding = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(
            new Dictionary<string, int> { ["ENTITY"] = 5 }, "6422",
            new[] { first, first, identicalCopy, anotherInstance, anotherChain, anotherHash, anotherLayer })!;

        finding.SourceRefs.Should().Equal(first, anotherInstance, anotherChain, anotherHash, anotherLayer);
        finding.Message.Should().Be("ENTITY=5");
        AssertGlobalBlocker(finding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrPartialSourceIdentityDoesNotTurnUnknownCoverageIntoSuccess(bool partialContext)
    {
        var sources = partialContext
            ? new[] { new ProvenanceRef { SourceKind = string.Empty, SourceHandle = "AB", EntityType = "Entity" } }
            : Array.Empty<ProvenanceRef>();
        var finding = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(
            new Dictionary<string, int> { ["ENTITY"] = 1 }, "6422", sources)!;

        finding.Message.Should().Be("ENTITY=1");
        finding.SourceRefs.Should().Equal(sources);
        finding.SourceRefs.Should().NotContain(source => source.SourcePathOrUri != null || source.DrawingChecksum != null);
        AssertGlobalBlocker(finding);
        // An independently recorded read failure must remain distinct and blocking;
        // the known unsupported object cannot lend its identity to an unreadable one.
        var unreadable = MeasurementFailureProvenance.Create("6422", "Failed to open model-space object", "eNotApplicable");
        var issues = EstimateReviewPolicy.Collect(Array.Empty<NeutralQuantityRecord>(),
            new[] { finding, unreadable }, Array.Empty<DeliveryFinding>(), null);
        issues.Should().HaveCount(2).And.OnlyContain(issue => issue.Blocking && issue.RecordIds.Count == 0);
        issues.Single(issue => issue.Code == EstimateFindingCodes.MeasurementFailed).Sources.Should().BeEmpty();
        unreadable.SourceRefs.Should().BeEmpty();
    }

    [Fact]
    public void Native59TypeDistributionKeepsAll437GlobalCountsAndAllSyntheticSourceIdentities()
    {
        // Exact native59 counts, synthetic identities: this does not retrofit
        // missing identities into estimate-extract-20260910-054924-0ce94885.
        var counts = new Dictionary<string, int>
        {
            ["SUBASSEMBLY"] = 282, ["DBPOINT"] = 134, ["ASSEMBLY"] = 19, ["ENTITY"] = 1, ["SITE"] = 1,
        };
        var sources = counts.SelectMany(pair => Enumerable.Range(0, pair.Value).Select(index =>
            Source($"TEST/{pair.Key}/{index:X}", "TEST-XREF", @"C:\TEST-ONLY\source.dwg",
                new string('a', 64), pair.Key))).ToList();
        var finding = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(counts, "6422", sources)!;

        finding.Title.Should().StartWith("437 ");
        finding.Message.Should().Be("SUBASSEMBLY=282, DBPOINT=134, ASSEMBLY=19, ENTITY=1, SITE=1");
        finding.SourceRefs.Should().HaveCount(437);
        foreach (var pair in counts)
        {
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType(pair.Key).Should().BeFalse();
            finding.SourceRefs.Count(source => source.EntityType == pair.Key).Should().Be(pair.Value);
        }
        AssertGlobalBlocker(finding);
        CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(new Dictionary<string, int>(), "6422", sources)
            .Should().BeNull("source evidence alone must not fabricate unsupported counts");
    }

    [Fact]
    public void NativeAdapterWiresOnlyUnsupportedBranchToExistingCapturedTraversalIdentity()
    {
        // The executable tests above cover the pure publication and consumer seam.
        // Native Entity access cannot be executed off-host; this contract checks wiring only.
        var source = File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin",
            "CivilDelivery", "Estimate", "CivilQuantityExtractionService.cs"));
        source.Should().Contain("var knownNonQuantity = IsKnownNonQuantityEntityType(type);")
            .And.Contain("var bucket = knownNonQuantity")
            .And.Contain("bucket[type] = bucket.GetValueOrDefault(type) + 1;");
        source.Should().MatchRegex(@"if \(!knownNonQuantity\)\s*unsupportedEntitySources.Add\(FailureSource\(""unsupported-entity-coverage""\)\);");
        source.Should().Contain("unsupportedEntityTypes, profile.ProfileId, unsupportedEntitySources)")
            .And.Contain("SourcePathOrUri = source.DrawingPath,")
            .And.Contain("DrawingChecksum = source.DrawingHash,")
            .And.Contain("SourceHandle = handlePath,")
            .And.Contain("XrefPath = source.XrefChain,")
            .And.Contain("EntityType = ent.GetType().Name,")
            .And.Contain("Layer = sourceLayer,")
            .And.Contain("MeasurementMethod = operation,");
    }

    private static ProvenanceRef Source(string handle, string? chain, string path, string hash,
        string type = "Entity", string layer = "unknown") => new()
    {
        SourceKind = chain == null ? "drawing" : "xref", SourcePathOrUri = path, DrawingChecksum = hash,
        SourceHandle = handle, XrefPath = chain, EntityType = type, Layer = layer,
        MeasurementMethod = "unsupported-entity-coverage", ToolVersion = "civil-delivery/test-only",
        RunId = "test-only-unsupported-provenance",
    };

    private static void AssertGlobalBlocker(DeliveryFinding finding)
    {
        finding.Code.Should().Be(EstimateFindingCodes.UnsupportedEntityCoverage);
        finding.Severity.Should().Be(FindingSeverity.ReviewRequired);
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        finding.AffectedRecordIds.Should().BeEmpty();
        finding.ResolvedAtUtc.Should().BeNull();
    }
}
