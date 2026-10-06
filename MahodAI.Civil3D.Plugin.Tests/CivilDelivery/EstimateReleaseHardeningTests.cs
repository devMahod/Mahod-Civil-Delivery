using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class EstimateDrawingUnitPolicyTests
    {
        [Theory]
        [InlineData(6, 1.0)]
        [InlineData(4, 0.001)]
        [InlineData(5, 0.01)]
        [InlineData(2, 0.3048)]
        public void SupportedInsunits_NormalizeLengthAreaAndVolume(int value, double factor)
        {
            var scale = DrawingUnitPolicy.Resolve(value);

            scale.IsSupported.Should().BeTrue();
            scale.Length(2).Should().BeApproximately(2 * factor, 1e-12);
            scale.Area(2).Should().BeApproximately(2 * factor * factor, 1e-12);
            scale.Volume(2).Should().BeApproximately(2 * factor * factor * factor, 1e-12);
            scale.LengthUnit.Should().Be("מטר");
        }

        [Fact]
        public void UnitlessDrawing_IsAGlobalErrorAndNeverLabelledMetres()
        {
            var scale = DrawingUnitPolicy.Resolve(0, "Undefined");
            var finding = DrawingUnitPolicy.Validate(0, "Undefined", "6422");

            scale.IsSupported.Should().BeFalse();
            scale.LengthUnit.Should().Contain("שרטוט");
            finding.Should().NotBeNull();
            finding!.Code.Should().Be(EstimateFindingCodes.UnitUnknown);
            finding.Severity.Should().Be(FindingSeverity.Error);
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }
    }

    public class EstimateFreshnessContractTests
    {
        [Fact]
        public void ScanScope_RejectsAChangedLiveDatabaseRevision()
        {
            var scan = EstimateWorkflowService.AssembleScan(
                "run", "6422", @"C:\\drawings\\A.dwg", "profile-hash",
                Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>(),
                0, false, databaseRevision: "drawing-guid:7");

            scan.StaleReason(@"C:\\drawings\\A.dwg", "6422", "profile-hash", "drawing-guid:7")
                .Should().BeNull();
            scan.StaleReason(@"C:\\drawings\\A.dwg", "6422", "profile-hash", "drawing-guid:8")
                .Should().Contain("השרטוט השתנה");
        }

        [Fact]
        public void EveryBuildAndExportBoundary_RequiresTheActiveDocumentAndScan()
        {
            var workflow = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "EstimateWorkflowService.cs"));
            var tracker = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "DrawingRevisionTracker.cs"));
            var ui = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.xaml.cs"));
            var draftUi = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.PricedDraft.cs"));

            string Boundary(string startText, string endText)
            {
                var start = workflow.IndexOf(startText, StringComparison.Ordinal);
                start.Should().BeGreaterThan(0);
                var end = workflow.IndexOf(endText, start, StringComparison.Ordinal);
                end.Should().BeGreaterThan(start);
                return workflow[start..end];
            }

            // Follow delegation, rather than accepting an unrelated freshness
            // call somewhere in this file as proof for every public endpoint.
            Boundary("public EstimateResult Build(", "public EstimateResult BuildForReview(")
                .Should().Contain("RequireFinalEstimateScope(profile)")
                .And.Contain("return BuildForReview(doc, scan, snapshot, profile)");
            var build = Boundary("public EstimateResult BuildForReview(", "internal sealed record IgnoredRuleApplication(");
            build.Should().Contain("RequireFresh(doc, scan, \"בניית האומדן\")")
                .And.Contain("RequirePublishedScanEvidence(scan)")
                .And.Contain("scan.ProjectProfileEffectiveHash");
            build.IndexOf("RequireFresh(doc, scan", StringComparison.Ordinal).Should()
                .BeLessThan(build.IndexOf("EstimateBuilder.Build(", StringComparison.Ordinal));
            Boundary("public EstimateExcelWriter.WriteResult Export(", "public EstimateExcelWriter.WriteResult ExportPartialPricedDraft(")
                .Should().Contain("RequireFinalEstimateScope(profile)")
                .And.Contain("return ExportCore(doc, scan, estimate, profile, outputDir, drawingName, partialDraft: false)");
            Boundary("public EstimateExcelWriter.WriteResult ExportPartialPricedDraft(", "private EstimateExcelWriter.WriteResult ExportCore(")
                .Should().Contain("RequireReviewedSourceScope(profile)")
                .And.Contain("return ExportCore(doc, scan, estimate, profile, outputDir, drawingName, partialDraft: true)");
            var export = Boundary("private EstimateExcelWriter.WriteResult ExportCore(", "internal static void RequirePublishedEstimateBuildEvidence(");
            export.Should().Contain("RequireFresh(doc, scan, \"ייצוא האומדן\")")
                .And.Contain("estimate.RunId, scan.RunId")
                .And.Contain("ExternalSourcesEqual(estimate.ExternalSources, scan.ExternalSources)")
                .And.Contain("RequirePublishedEstimateBuildEvidence(scan, estimate)");
            export.IndexOf("RequirePublishedEstimateBuildEvidence(scan, estimate)", StringComparison.Ordinal).Should()
                .BeLessThan(export.IndexOf("EstimateExcelWriter.WritePartialPricedDraft(", StringComparison.Ordinal));

            workflow.Should().Contain("RequireFresh(doc, scan, \"בניית האומדן\")")
                .And.Contain("RequireFresh(doc, scan, \"ייצוא האומדן\")");
            tracker.Should().Contain("db.ObjectAppended +=")
                .And.Contain("db.ObjectModified +=")
                .And.Contain("db.ObjectErased +=");
            ui.Should().Contain("!VerifyEstimateSourcesForAction(doc, \"בניית האומדן\")")
                .And.Contain("!VerifyEstimateSourcesForAction(doc, \"ייצוא האומדן\")")
                .And.Contain("EstimateWorkflowService.FreshnessReason(doc, _scan)")
                .And.Contain("_estimate.BuildForReview(doc, _scan, _catalog, _profile)")
                .And.Contain("_estimate.Export(doc, _scan, _estimateResult, _profile",
                    "export must receive and verify the exact active profile rather than make profile identity optional");
            draftUi.Should().Contain("!VerifyEstimateSourcesForAction(doc, \"ייצוא טיוטה מתומחרת\")")
                .And.Contain("!ReferenceEquals(scan, _scan)")
                .And.Contain("!ReferenceEquals(result, _estimateResult)")
                .And.Contain("!ReferenceEquals(profile, _profile)")
                .And.Contain("_estimate.ExportPartialPricedDraft(doc, scan, result, profile,");
        }
    }

    public class EarthworksCoverageHardGateTests
    {
        [Fact]
        public void CorridorMaterialCoverage_BlocksSingletonGapMissingAndUnprovedBoundary()
        {
            var expected = new[]
            {
                new CorridorQuantityLogic.StationSeries("BASE-A", new double[] { 0, 10, 20, 100 }),
                new CorridorQuantityLogic.StationSeries("BASE-EMPTY", new double[] { 0, 10 }),
            };
            var samples = new List<CorridorQuantityLogic.ShapeSample>
            {
                new(0, "BASE", 2, "BASE-A"),
                new(10, "BASE", 2, "BASE-A"),
                new(100, "BASE", 2, "BASE-A"),
                new(10, "PAVE", 1, "BASE-A"),
            };

            var issues = CorridorQuantityLogic.MaterialCoverageIssues(samples, expected, 50);

            issues.Should().Contain(x => x.Contains("unintegrated gap"));
            issues.Should().Contain(x => x.Contains("missing 1 scheduled"));
            issues.Should().Contain(x => x.Contains("singleton"));
            issues.Should().Contain(x => x.Contains("starts inside"));
            issues.Should().Contain(x => x.Contains("ends inside"));
            issues.Should().Contain(x => x.Contains("no readable material shapes"));
        }

        [Fact]
        public void CorridorMaterialCoverage_AcceptsCompleteSeriesWithExplicitZeroEndpoints()
        {
            var expected = new[]
            {
                new CorridorQuantityLogic.StationSeries("BASE-A", new double[] { 0, 10, 20 }),
            };
            var samples = new List<CorridorQuantityLogic.ShapeSample>
            {
                new(0, "PAVE", 0, "BASE-A"),
                new(10, "PAVE", 1, "BASE-A"),
                new(20, "PAVE", 0, "BASE-A"),
            };

            CorridorQuantityLogic.MaterialCoverageIssues(samples, expected, 50)
                .Should().BeEmpty();
        }

        [Fact]
        public void CorridorMaterialCoverage_WithoutAnyBaselineSeries_IsIncomplete()
        {
            CorridorQuantityLogic.MaterialCoverageIssues(
                    Array.Empty<CorridorQuantityLogic.ShapeSample>(),
                    Array.Empty<CorridorQuantityLogic.StationSeries>(), 50)
                .Should().ContainSingle(x => x.Contains("no readable baseline"));
        }

        [Fact]
        public void SingletonGapAndDuplicateStationCode_AreBlockingCoverageIssues()
        {
            var samples = new List<CorridorQuantityLogic.ShapeSample>
            {
                new(0, "cut", 1, "MCD-A"),
                new(0, "cut", 2, "MCD-A"),
                new(0, "fill", 0, "MCD-A"),
                new(300, "cut", 1, "MCD-A"),
                new(300, "fill", 0, "MCD-A"),
                new(10, "cut", 1, "MCD-SINGLE"),
                new(10, "fill", 0, "MCD-SINGLE"),
            };

            var issues = CorridorQuantityService.EarthworksCoverageIssues(samples, 3, 200);

            issues.Should().Contain(x => x.Contains("unintegrated gap"));
            issues.Should().Contain(x => x.Contains("singleton"));
            issues.Should().Contain(x => x.Contains("duplicate/multi-source"));
        }

        [Fact]
        public void NotRequested_IsExplicitOnlyWithDecisionAuthorityTimeAndReason()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.Earthworks.Requested = false;
            CorridorQuantityService.EarthworksExplicitlyNotRequested(profile).Should().BeFalse();

            profile.Estimate.Earthworks.DecidedBy = "nataly";
            profile.Estimate.Earthworks.DecidedAtUtc = DateTime.UtcNow;
            profile.Estimate.Earthworks.Reason = "outside preliminary estimate scope";
            CorridorQuantityService.EarthworksExplicitlyNotRequested(profile).Should().BeTrue();
        }

        [Fact]
        public void RuntimeSource_UsesZeroEndpointsAndBlocksEveryCoverageOmission()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CorridorQuantityService.cs"));

            source.Should().Contain("mcdGroupCount == 0")
                .And.Contain("allowedAlignments.Contains(alignment.Name)")
                .And.Contain("trustedGroupCount > 1")
                .And.Contain("FindingSeverity.ReviewRequired")
                .And.Contain("EarthworksCoverageIssues(")
                .And.Contain("new CorridorQuantityLogic.ShapeSample(station, \"cut\", cut, seriesId)")
                .And.Contain("new CorridorQuantityLogic.ShapeSample(station, \"fill\", fill, seriesId)")
                .And.Contain("shape-code-ambiguity")
                .And.Contain("MaterialCoverageIssues(")
                .And.Contain("CorridorMaterialCoverageIncompleteCode");
        }
    }
}
