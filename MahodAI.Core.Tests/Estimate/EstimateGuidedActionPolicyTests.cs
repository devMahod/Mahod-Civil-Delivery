using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class EstimateGuidedActionPolicyTests
    {
        private static EstimateGuidedActionPolicy.Snapshot ReadyToScan() => new(
            Available: true, SavePending: false, SourcesApproved: true,
            EarthworksResolved: true, SaveMayBeRequired: false, HasFreshScan: false,
            QuantityGroups: 0, PendingReviewGroups: 0, CatalogReady: false,
            EstimateBuilt: false, ExportReady: false, BlockingFindings: 0);

        [Fact]
        public void ClassifiedDrawingNoiseHasOneExplicitReviewBeforeHundredsOfMappings()
        {
            var input = ReadyToScan() with { HasFreshScan = true, QuantityGroups = 1031,
                PendingReviewGroups = 1031, CatalogReady = true, NoiseReviewGroups = 170, BlockingFindings = 455 };
            EstimateGuidedActionPolicy.Evaluate(input).Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewDrawingNoise);
            EstimateGuidedActionPolicy.Evaluate(input with { SourcesApproved = false }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ApproveSources);
            EstimateGuidedActionPolicy.Evaluate(input with { HasFreshScan = false }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Scan);
            // Cancelling changes nothing; mapping and source evidence are never cleared by guidance.
            EstimateGuidedActionPolicy.Evaluate(input).Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewDrawingNoise);
            input.BlockingFindings.Should().Be(455); input.PendingReviewGroups.Should().Be(1031);
            EstimateGuidedActionPolicy.Evaluate(input with { NoiseReviewGroups = 0 }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewQuantities);
        }

        [Fact]
        public void FirstMeasurementPrecedesScopeEarthworksAndCatalogDecisions()
        {
            var input = ReadyToScan() with
            {
                SourcesApproved = false, EarthworksResolved = false, SaveMayBeRequired = true,
            };
            EstimateGuidedActionPolicy.Evaluate(input).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Scan);
            EstimateGuidedActionPolicy.Evaluate(input with { SaveMayBeRequired = false }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Scan);
            input.SourcesApproved.Should().BeFalse();
            input.EarthworksResolved.Should().BeFalse();
            input.CatalogReady.Should().BeFalse();
        }

        [Fact]
        public void MeasurementWithoutCatalogOrKnownMappingsIsReachable()
        {
            EstimateGuidedActionPolicy.Evaluate(ReadyToScan()).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Scan);
        }

        [Fact]
        public void SaveInProgressCannotDispatchAnotherOperation()
        {
            var state = EstimateGuidedActionPolicy.Evaluate(ReadyToScan() with { SavePending = true });
            state.Next.Should().Be(EstimateGuidedActionPolicy.Action.WaitForSave);
            state.Enabled.Should().BeFalse();
        }

        [Fact]
        public void DirtyOrStaleDrawingCannotRouteToOldBuildOrExport()
        {
            var old = ReadyToScan() with
            {
                HasFreshScan = true, QuantityGroups = 4, CatalogReady = true,
                EstimateBuilt = true, ExportReady = true,
            };
            EstimateGuidedActionPolicy.Evaluate(old with { SaveMayBeRequired = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Scan);
            EstimateGuidedActionPolicy.Evaluate(old with { HasFreshScan = false }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Scan);
        }

        [Fact]
        public void UnmappedMeasuredGroupsLoadCatalogBeforeManualMapping()
        {
            var measured = ReadyToScan() with
            {
                HasFreshScan = true, QuantityGroups = 3, PendingReviewGroups = 3,
            };
            EstimateGuidedActionPolicy.Evaluate(measured).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.LoadCatalog);
            EstimateGuidedActionPolicy.Evaluate(measured with { CatalogReady = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewQuantities);
        }

        [Fact]
        public void MissingSourceDoesNotPreventReviewOfMeasuredRowsButStillPrecedesBuild()
        {
            var measured = ReadyToScan() with
            {
                HasFreshScan = true, QuantityGroups = 3, PendingReviewGroups = 3,
                BlockingFindings = 1,
            };
            EstimateGuidedActionPolicy.Evaluate(measured).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.LoadCatalog);
            EstimateGuidedActionPolicy.Evaluate(measured with { CatalogReady = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewQuantities);
            EstimateGuidedActionPolicy.Evaluate(measured with
                { CatalogReady = true, PendingReviewGroups = 0 }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewFindings);
            measured.QuantityGroups.Should().Be(3);
            measured.BlockingFindings.Should().Be(1);
        }

        [Fact]
        public void UnresolvedFinalScopeAllowsMappingReviewButCannotBuildOrExport()
        {
            var measured = ReadyToScan() with
            {
                SourcesApproved = false, EarthworksResolved = false,
                HasFreshScan = true, QuantityGroups = 2, PendingReviewGroups = 2, CatalogReady = true,
            };
            EstimateGuidedActionPolicy.Evaluate(measured).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewQuantities);
            var reviewed = measured with { PendingReviewGroups = 0 };
            EstimateGuidedActionPolicy.Evaluate(reviewed).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ApproveSources);
            // Cancelling cannot advance to Build, even if an old result claimed readiness.
            EstimateGuidedActionPolicy.Evaluate(reviewed with { EstimateBuilt = true, ExportReady = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ApproveSources);
            EstimateGuidedActionPolicy.Evaluate(reviewed with { SourcesApproved = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.DecideEarthworks);
            EstimateGuidedActionPolicy.Evaluate(reviewed with
                { SourcesApproved = true, EarthworksResolved = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Build);
        }

        [Fact]
        public void DeferredScopeFindingsStayBlockingButAreNotMistakenForFailedGeometry()
        {
            var findings = new[]
            {
                EstimateFindingCodes.SourceScopePolicyUnapproved,
                EstimateFindingCodes.XrefPolicyUnapproved,
                EstimatePreflightPolicy.EarthworksNotAssessedCode,
            }.Select(code => new DeliveryFinding
            {
                Code = code, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
                Title = "Scope undecided; measurement is still available",
            }).ToList();
            EstimateGuidedActionPolicy.SourceReviewFindingCount(findings, Array.Empty<string>()).Should().Be(0);
            findings.Should().HaveCount(3).And.OnlyContain(finding => EstimatePreflightPolicy.IsBlocking(finding));
            findings.Add(new DeliveryFinding
            {
                Code = EstimatePreflightPolicy.EarthworksSourceUnverifiedCode,
                Domain = "estimate", Severity = FindingSeverity.Error, Title = "Included earthworks source failed",
            });
            EstimateGuidedActionPolicy.SourceReviewFindingCount(findings, Array.Empty<string>()).Should().Be(1);
            findings.Should().HaveCount(4).And.OnlyContain(finding => EstimatePreflightPolicy.IsBlocking(finding));
        }

        [Fact]
        public void EmptyScanPointsAtSourcesInsteadOfCatalog()
        {
            EstimateGuidedActionPolicy.Evaluate(ReadyToScan() with { HasFreshScan = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewSources);
        }

        [Fact]
        public void UnknownLayerAndExactAreaPerimeterDecisionDoNotSendEngineerBackToRescan()
        {
            var findings = new[]
            {
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped, Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired, Title = "An unknown CAD layer is measured without a mapping",
                },
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MixedDimensionLayer, Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired, Title = "Unknown CAD layer area or perimeter",
                    Message = "layer:UNCLASSIFIED|area <> layer:UNCLASSIFIED|length",
                },
            };
            EstimateGuidedActionPolicy.SourceReviewFindingCount(findings,
                new[] { "layer:UNCLASSIFIED|area <> layer:UNCLASSIFIED|length" }).Should().Be(0);
            EstimateGuidedActionPolicy.SourceReviewFindingCount(findings,
                new[] { "layer:OTHER|area <> layer:OTHER|length" }).Should().Be(1);
        }

        [Fact]
        public void BuildDoesNotImplyExportAuthority()
        {
            var priced = ReadyToScan() with
            {
                HasFreshScan = true, QuantityGroups = 3, CatalogReady = true,
            };
            EstimateGuidedActionPolicy.Evaluate(priced).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Build);
            EstimateGuidedActionPolicy.Evaluate(priced with { EstimateBuilt = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.ReviewFindings);
            EstimateGuidedActionPolicy.Evaluate(priced with { EstimateBuilt = true, ExportReady = true }).Next.Should()
                .Be(EstimateGuidedActionPolicy.Action.Export);
        }

        [Fact]
        public void MissingProjectOrDrawingDisablesEvenPreviouslyExportableResult()
        {
            var state = EstimateGuidedActionPolicy.Evaluate(ReadyToScan() with
            {
                Available = false, HasFreshScan = true, EstimateBuilt = true, ExportReady = true,
            });
            state.Enabled.Should().BeFalse();
            state.Next.Should().Be(EstimateGuidedActionPolicy.Action.Unavailable);
        }

        [Fact]
        public void TheRulesBillIsNotCalledCompleteAndPointsToTheMissingSheet()
        {
            // 01/10 (Codex): the panel said "the full bill was built" while known objects were unmeasured.
            EstimateGuidedActionPolicy.BoqRulesDetail.Should().NotContain("המלא")      // "the full"
                .And.Contain("חסרים");                                              // "missing" sheet
        }
    }
}
