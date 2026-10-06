using System;
using System.IO;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class MaterialAreaScanEvidenceTests
{
    private static MaterialSectionAreaObservation Area() =>
        MaterialSectionAreaEvidence.Capture(
            new[] { new CorridorQuantityLogic.ShapeSample(120, "H1", 3.5, "baseline-1") },
            "area-test", @"C:\local\host.dwg", new string('a', 64), "AB", "Road",
            1, true, "Meters", false).Single();

    [Fact]
    public void StationAreasRemainVisibleWithoutBecomingPayableQuantityRecords()
    {
        var area = Area();
        var scan = EstimateWorkflowService.AssembleScan(
            "area-test", "6422", area.DrawingPath, new string('b', 64),
            Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>(), 0, true,
            materialAreas: new[] { area });
        Assert.Empty(scan.Records);
        Assert.Equal(area, Assert.Single(scan.MaterialAreas));
        Assert.NotEqual(DeliveryStatus.Ready, scan.Status);
        var result = EstimateBuilder.Build(scan.Records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());
        Assert.Empty(result.Lines);
        Assert.Equal(0, result.CleanTotal);
    }

    [Fact]
    public void DecisionRebasePreservesExactAreaLineageButCannotInventCoverage()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "mcd-material-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var profile = EstimateFixtures.Profile();
            var path = Path.Combine(testRoot, "profile.yaml");
            var saved = ProfileCasTest.Save(profile, path, "simulation-only area evidence", "test");
            var area = Area();
            var scan = EstimateWorkflowService.AssembleScan(
                "area-test", profile.ProfileId, area.DrawingPath, new string('b', 64),
                Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>(), 0, true,
                sourceDrawingHash: area.DrawingHash, materialAreas: new[] { area });
            var rebased = EstimateWorkflowService.RebaseAfterProfileDecision(scan, profile, saved);
            Assert.Equal(area, Assert.Single(rebased.MaterialAreas));
            Assert.False(rebased.MaterialAreas[0].CorridorCoverageProven);
            Assert.Empty(rebased.Records);
            Assert.NotSame(scan.MaterialAreas, rebased.MaterialAreas);
        }
        finally { Directory.Delete(testRoot, true); }
    }
}
