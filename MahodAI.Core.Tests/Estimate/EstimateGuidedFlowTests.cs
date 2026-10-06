using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class EstimateScanSourceReadinessTests
    {
        private const string Hash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [Fact]
        public void LiveDbmod16_IsRecoverableOnlyByExplicitSaveThenScan()
        {
            var value = EstimateSourceSnapshotPolicy.ForScan(
                @"C:\work\road.dwg", drawingHash: null, dbMod: 16);

            value.Action.Should().Be(EstimateSourceSnapshotPolicy.ScanSourceAction.SaveAndScan);
            value.CanSaveAndResume.Should().BeTrue();
            value.Detail.Should().Contain("DBMOD=16").And.Contain("SHA-256");
        }

        [Fact]
        public void NewUnsavedDrawing_RequiresSaveAsNotAnInventedPath()
        {
            var value = EstimateSourceSnapshotPolicy.ForScan(
                drawingPath: null, drawingHash: null, dbMod: 1);

            value.Action.Should().Be(EstimateSourceSnapshotPolicy.ScanSourceAction.SaveAsAndScan);
        }

        [Fact]
        public void CleanSavedAndHashedDrawing_IsReady()
        {
            EstimateSourceSnapshotPolicy.ForScan(@"C:\work\road.dwg", Hash, 0)
                .IsReady.Should().BeTrue();
        }

        [Fact]
        public void HashFailureOnCleanDrawing_RemainsFailClosed()
        {
            var value = EstimateSourceSnapshotPolicy.ForScan(
                @"C:\work\road.dwg", null, 0, "file access denied");

            value.Action.Should().Be(EstimateSourceSnapshotPolicy.ScanSourceAction.Blocked);
            value.Detail.Should().Contain("file access denied");
        }
    }

    public sealed class EstimateFlowPolicyTests
    {
        private static EstimateFlowPolicy.Snapshot ReadyForScan() => new(
            SourcesApproved: true,
            EarthworksResolved: true,
            DrawingReady: true,
            SaveCanRepairDrawing: false,
            HasFreshScan: false,
            QuantityGroups: 0,
            PendingReviewGroups: 0,
            CatalogReady: true,
            EstimateBuilt: false,
            ExportReady: false,
            BlockingFindings: 0);

        [Fact]
        public void SaveAndMeasurementPrecedeFinalScopeDecisions()
        {
            var state = EstimateFlowPolicy.Evaluate(ReadyForScan() with
            {
                SourcesApproved = false,
                EarthworksResolved = false,
                DrawingReady = false,
                SaveCanRepairDrawing = true,
            });

            state.Step.Should().Be(1);
            state.NextAction.Should().Contain("שמור").And.Contain("סריקה");
            EstimateFlowPolicy.Evaluate(ReadyForScan() with
                { SourcesApproved = false, EarthworksResolved = false }).Step.Should().Be(1);
        }

        [Fact]
        public void DirtyDrawingPointsToSaveAndAutomaticResume()
        {
            var state = EstimateFlowPolicy.Evaluate(ReadyForScan() with
            {
                DrawingReady = false,
                SaveCanRepairDrawing = true,
            });

            state.Step.Should().Be(1);
            state.NextAction.Should().Contain("שמור").And.Contain("אוטומטית");
        }

        [Fact]
        public void PendingMappingsStayInEngineerReview()
        {
            var state = EstimateFlowPolicy.Evaluate(ReadyForScan() with
            {
                HasFreshScan = true,
                QuantityGroups = 12,
                PendingReviewGroups = 4,
            });

            state.Step.Should().Be(2);
            state.NextAction.Should().Contain("4").And.Contain("אשר מיפוי");
        }

        [Fact]
        public void OnlyCleanBuiltEstimateReachesExcel()
        {
            var state = EstimateFlowPolicy.Evaluate(ReadyForScan() with
            {
                HasFreshScan = true,
                QuantityGroups = 12,
                CatalogReady = true,
                EstimateBuilt = true,
                ExportReady = true,
            });

            state.Step.Should().Be(5);
            state.Progress.Should().Contain("5 Excel ←");
        }
    }
}
