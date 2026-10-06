using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateMeasureFirstWorkflowTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("rules-only", "host-only")]
    [InlineData("mistyped-source-policy", "mistyped-xref-policy")]
    public void UnapprovedOrMisconfiguredScope_PreservesActualMeasurementsAndFindings(string? source, string? xref)
    {
        var profile = EstimateFixtures.Profile();
        profile.Estimate.QuantitySources.SourceScopePolicy = source;
        profile.Estimate.QuantitySources.XrefPolicy = xref;
        profile.Estimate.Earthworks.Requested = null;
        profile.Estimate.Earthworks.DecidedBy = null;
        profile.Estimate.Earthworks.DecidedAtUtc = null;
        profile.Estimate.Earthworks.Reason = null;
        var records = new[] { EstimateFixtures.Record("measured-host", null!, 37.25, "מטר") };
        var findings = EstimatePreflightPolicy.ValidateSourcePolicies(profile).ToList();
        findings.Add(new DeliveryFinding
        {
            Domain = "estimate", Code = EstimatePreflightPolicy.EarthworksNotAssessedCode,
            Severity = FindingSeverity.ReviewRequired, Title = "Earthworks not assessed",
        });
        var scan = EstimateWorkflowService.AssembleScan("measure-first", profile.ProfileId, "host.dwg",
            new string('a', 64), records, findings, 1, true);

        scan.Records.Should().ContainSingle().Which.Measurement.RawValue.Should().Be(37.25);
        scan.Records.Should().NotContain(record => record.Measurement.Kind == "volume");
        scan.Findings.Should().Contain(findings);
        var reviewCount = EstimateGuidedActionPolicy.SourceReviewFindingCount(scan.Findings, Array.Empty<string>());
        var next = EstimateGuidedActionPolicy.Evaluate(new(true, false, false, false, false,
            true, 1, 1, true, false, false, reviewCount));
        next.Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewQuantities);
        profile.Estimate.Earthworks.Requested.Should().BeNull();
        profile.Estimate.QuantitySources.SourceScopePolicy.Should().Be(source);
        var final = () => EstimateWorkflowService.RequireFinalEstimateScope(profile);
        final.Should().Throw<InvalidOperationException>();
        var draft = EstimateBuilder.Build(scan.Records, EstimateFixtures.Snapshot(), profile,
            scan.RunId, scan.Findings);
        draft.Lines.Should().ContainSingle().Which.BoqQuantity.Should().Be(37.25);
        EstimatePreflightPolicy.CanExport(draft).Should().BeFalse();
    }

    [Fact]
    public void FinalGuard_RequiresCompleteScopeAndDocumentedEarthworksDecision_WithoutApprovingEither()
    {
        var profile = EstimateFixtures.Profile();
        profile.Estimate.QuantitySources.SourceScopePolicy = "discover-all";
        profile.Estimate.QuantitySources.XrefPolicy = "include-xrefs";
        profile.Estimate.Earthworks.Requested = null;
        profile.Estimate.Earthworks.DecidedBy = null;
        profile.Estimate.Earthworks.DecidedAtUtc = null;
        profile.Estimate.Earthworks.Reason = null;
        var guard = () => EstimateWorkflowService.RequireFinalEstimateScope(profile);
        guard.Should().Throw<InvalidOperationException>();
        profile.Estimate.Earthworks.Requested.Should().BeNull();
        profile.Estimate.Earthworks.Requested = false;
        guard.Should().Throw<InvalidOperationException>();
        profile.Estimate.Earthworks.DecidedBy = "SIMULATION-ONLY";
        profile.Estimate.Earthworks.DecidedAtUtc = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
        guard.Should().Throw<InvalidOperationException>("a negative decision needs its explicit reason");
        profile.Estimate.Earthworks.Reason = "Synthetic fixture only; not an engineering decision";
        guard.Should().NotThrow();
        profile.Estimate.Earthworks.Requested = true;
        profile.Estimate.Earthworks.Reason = null;
        guard.Should().NotThrow("included scope still needs actual collector/source evidence before export");
        profile.Estimate.QuantitySources.XrefPolicy = "host-only";
        guard.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PaletteMeasuresBeforeOptionalEnrichment_AndRuntimeFinalEndpointsKeepScopeGuard()
    {
        var root = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery");
        var ui = File.ReadAllText(Path.Combine(root, "UI", "CivilDeliveryControl.xaml.cs"));
        var scan = Slice(ui, "private void OnScan", "private void OfferExplicitSaveAndResume");
        scan.Should().Contain("EstimateSourceSnapshotPolicy.ForScan")
            .And.Contain("RunEstimateScan(doc)")
            .And.NotContain("IsCompleteDiscoveryScopeApproved")
            .And.NotContain("GetEarthworksDecision");
        var measurement = Slice(ui, "private void RunEstimateScan", "private void SelectNextQuantityRow");
        measurement.IndexOf("RebuildQuantityRows();", StringComparison.Ordinal).Should().BeLessThan(
            measurement.IndexOf("_estimate.LoadCatalog(profile, profileSource)", StringComparison.Ordinal));
        measurement.Should().Contain("EST-CATALOG-LOAD-FAILED").And.Contain("EST-MAPPING-PROPOSALS-FAILED");
        var service = File.ReadAllText(Path.Combine(root, "Estimate", "EstimateWorkflowService.cs"));
        Slice(service, "public EstimateResult Build(", "var result = EstimateBuilder.Build(")
            .Should().Contain("RequireFinalEstimateScope(profile)");
        Slice(service, "public EstimateExcelWriter.WriteResult Export(", "RequireFresh(doc, scan")
            .Should().Contain("RequireFinalEstimateScope(profile)");
    }

    private static string Slice(string text, string start, string end)
    {
        var first = text.IndexOf(start, StringComparison.Ordinal);
        first.Should().BeGreaterThanOrEqualTo(0);
        var last = text.IndexOf(end, first, StringComparison.Ordinal);
        last.Should().BeGreaterThan(first);
        return text[first..last];
    }
}
