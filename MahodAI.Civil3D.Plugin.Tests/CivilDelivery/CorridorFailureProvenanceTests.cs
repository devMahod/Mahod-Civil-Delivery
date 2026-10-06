using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Executable pure evidence tests, plus one explicitly source-only adapter contract.
/// Native 62 had four stale and three incomplete corridors with no SourceRefs.
/// Handles here are synthetic; no native quantities or approvals are fabricated.
/// </summary>
public sealed class CorridorFailureProvenanceTests
{
    private static ProvenanceRef Origin(string? handle = "A101", string? layer = "CORRIDOR-LAYER") =>
        CorridorFailureProvenance.Source(@"C:\fixture\host.dwg", new string('a', 64),
            handle, "CORRIDOR", layer, "fixture-run", "fixture-tool");

    private static DeliveryFinding Finding(string code = EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode,
        string title = "Incomplete corridor fixture") => new()
    {
        Code = code, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
        Title = title, Message = "Missing scheduled stations are not zero.",
        ProjectProfileId = "fixture-profile",
    };

    [Theory]
    [InlineData("2000", true)]
    [InlineData("3000", true)]
    [InlineData("600", true)]
    [InlineData("1000", true)]
    [InlineData("2000-DES", false)]
    [InlineData("700", false)]
    [InlineData("750", false)]
    public void Recorded62FailureKindsGainIdentityWithoutPretendingMissingRecordsExist(string corridorName, bool stale)
    {
        var finding = Finding(stale ? EstimatePreflightPolicy.CorridorOutOfDateCode :
            EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode, corridorName);
        var source = Origin();
        var beforeMessage = finding.Message;

        CorridorFailureProvenance.Attach(finding, source).Should().BeSameAs(finding);

        finding.SourceRefs.Should().ContainSingle().Which.Should().BeSameAs(source);
        source.SourcePathOrUri.Should().Be(@"C:\fixture\host.dwg");
        source.DrawingChecksum.Should().Be(new string('a', 64));
        source.SourceHandle.Should().Be("A101"); source.EntityType.Should().Be("CORRIDOR");
        source.Layer.Should().Be("CORRIDOR-LAYER"); source.SourceKind.Should().Be("civil-model");
        source.RunId.Should().Be("fixture-run"); source.ToolVersion.Should().Be("fixture-tool");
        source.MeasurementMethod.Should().Be("civil-model-quantity");
        finding.Message.Should().Be(beforeMessage); finding.Title.Should().Be(corridorName);
        finding.Severity.Should().Be(FindingSeverity.ReviewRequired);
        finding.AffectedRecordIds.Should().BeEmpty(); finding.ResolvedAtUtc.Should().BeNull();
        finding.Resolution.Should().BeNull(); finding.ResolvedBy.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
    }

    [Fact]
    public void EveryReadFailureKeepsItsOwnOriginAndBaselineStationEvenBeyondFiveExamples()
    {
        var origin = Origin();
        var failures = new CorridorFailureProvenance.ReadFailures(() => origin);
        for (var i = 0; i < 7; i++)
        {
            origin = Origin((0xA101 + i).ToString("X"));
            failures.Add(new("shape-area", $"baseline-{i:D3}@{i * 10}:Base", "native read failed"));
        }
        var finding = CorridorQuantityLogic.BlockingReadFinding(failures,
            EstimatePreflightPolicy.CorridorMaterialReadFailedCode, "fixture-profile", "Read failed")!;
        CorridorFailureProvenance.Attach(finding, failures.Sources);

        failures.Should().HaveCount(7); finding.SourceRefs.Should().HaveCount(7);
        finding.SourceRefs.Select(source => source.SourceHandle).Should().Equal(
            Enumerable.Range(0, 7).Select(i => (0xA101 + i).ToString("X")));
        finding.SourceRefs.Last().SourceSubentityPath.Should().Be("baseline-006@60:Base");
        finding.SourceRefs.Should().OnlyContain(source => source.MeasurementMethod == "civil-model-quantity:shape-area");
        finding.AffectedRecordIds.Should().BeEmpty();
        finding.Severity.Should().Be(FindingSeverity.Error);
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
    }

    [Fact]
    public void ExplicitSectionSourceAndUnknownEnumerationOriginAreNotReplacedByLastCorridor()
    {
        var failures = new CorridorFailureProvenance.ReadFailures(() => Origin("B999"));
        var section = CorridorFailureProvenance.Source(@"C:\fixture\host.dwg", new string('a', 64),
            "C555", "SECTION", "section-layer", "fixture-run", "fixture-tool");
        failures.Add(new("section-read", "alignment/group/sample-line", "original exception"), section);
        failures.Add(new("corridor-material-pass", "host.dwg", "enumeration exception"),
            CorridorFailureProvenance.Source(@"C:\fixture\host.dwg", new string('a', 64),
                null, null, null, "fixture-run", "fixture-tool"));

        failures.Sources[0].SourceHandle.Should().Be("C555");
        failures.Sources[0].EntityType.Should().Be("SECTION");
        failures.Sources[1].SourceHandle.Should().BeNull();
        failures.Sources[1].EntityType.Should().BeNull(); failures.Sources[1].Layer.Should().BeNull();
        failures.Select(failure => failure.Message).Should().Equal("original exception", "enumeration exception");
    }

    [Fact]
    public void EqualOriginsDeduplicateButDistinctReadContextsRemainAuditable()
    {
        var finding = Finding();
        CorridorFailureProvenance.Attach(finding, Origin(), Origin(), Origin("A102"));
        finding.SourceRefs.Should().HaveCount(2);
        var failures = new CorridorFailureProvenance.ReadFailures(() => Origin());
        failures.Add(new("shape-area", "baseline-1@0", "failure"));
        failures.Add(new("shape-area", "baseline-1@10", "failure"));
        CorridorFailureProvenance.Attach(finding, failures.Sources);
        finding.SourceRefs.Should().HaveCount(4);
        finding.AffectedRecordIds.Should().BeEmpty();
    }

    [Fact]
    public void ExistingProvenRecordScopeIsPreservedAndNeverBroadenedBySourceAttachment()
    {
        var finding = Finding(EstimateFindingCodes.ConfigurationAmbiguous);
        finding.AffectedRecordIds.Add("cq-0001");
        CorridorFailureProvenance.Attach(finding, Origin(), Origin("A102"));
        finding.AffectedRecordIds.Should().Equal("cq-0001");
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
    }

    [Theory]
    [InlineData(EstimatePreflightPolicy.CorridorOutOfDateCode)]
    [InlineData(EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode)]
    public void ActualBuildConsumerStillBlocksPricedRowsForMissingCorridorCoverage(string code)
    {
        // In-memory test-only authority, never written to a project or catalog.
        const string itemCode = "SYNTHETIC.CORRIDOR.EVIDENCE";
        var catalog = EstimateFixtures.SnapshotWithPrice(itemCode, "מטר", 12.50m);
        var record = EstimateFixtures.Record("fixture-row", itemCode, 2, "מטר");
        record.Classification.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[itemCode]);
        var profile = EstimateFixtures.Profile();
        var before = EstimateBuilder.Build(new[] { record }, catalog, profile);
        before.Lines.Single().IncludedInTotals.Should().BeTrue();
        before.CleanTotal.Should().Be(25m);

        var failure = CorridorFailureProvenance.Attach(Finding(code), Origin());
        var after = EstimateBuilder.Build(new[] { record }, catalog, profile,
            preflightFindings: new[] { failure });
        var line = after.Lines.Single();
        line.PriceStatus.Should().Be(PriceStatus.Priced); line.Price.Should().Be(12.50m);
        line.IncludedInTotals.Should().BeFalse(); line.Total.Should().BeNull();
        after.CleanTotal.Should().Be(0m);
        after.Findings.Should().Contain(failure);
        failure.AffectedRecordIds.Should().BeEmpty();
        EstimatePreflightPolicy.CanExport(after).Should().BeFalse();
    }

    [Fact]
    public void NativeAdapterWiresAllFindingAndReadFailureExitsWithoutHashingOrInventingHandles()
    {
        // Source-only wiring evidence: this does NOT execute Autodesk getters.
        var source = File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin",
            "CivilDelivery", "Estimate", "CorridorQuantityService.cs"));
        source.Should().Contain("DeliveryFinding Finding(FindingSeverity severity")
            .And.Contain("severity, title, message, project, code), origin)")
            .And.Contain("NativeOrigin(drawingOrigin, corridorId, \"CORRIDOR\", corridor)")
            .And.Contain("origin = alignmentOrigin;")
            .And.Contain("materialReadFailures.Sources")
            .And.Contain("earthworksReadFailures.Sources")
            .And.Contain("CorridorFailureProvenance.ReadFailures readFailures")
            .And.Contain("ex.Message), sectionOrigin)")
            .And.Contain("id.Handle.ToString()")
            .And.NotContain("HashFileShared(")
            .And.NotContain("SourceHandle = id.ToString()");
        var scopeStart = source.IndexOf("else if (earthworksDecision.Status !=", StringComparison.Ordinal);
        var scopeEnd = source.IndexOf("else try", scopeStart, StringComparison.Ordinal);
        var scopeDecision = source[scopeStart..scopeEnd];
        scopeDecision.Should().Contain("CorridorQuantityService.Finding(")
            .And.Contain("profile, EstimatePreflightPolicy.EarthworksNotAssessedCode, runId)")
            .And.NotContain("SourceRefs").And.NotContain("CorridorFailureProvenance.Attach(")
            .And.NotContain("findings.Add(Finding(");
    }
}
