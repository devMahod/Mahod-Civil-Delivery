using System.IO;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Adapter wiring evidence only, not native getter execution.</summary>
public class CorridorRegionQuantitySourceTests
{
    [Fact]
    public void RegionSpecificAssemblyReaderDoesNotChooseTheOtherSideOfASharedBoundary()
    {
        var source=Source();
        source.Should().Contain("baseline.BaselineRegions")
            .And.Contain("region.Native.AppliedAssemblies")
            .And.Contain("region.StartStation, region.EndStation")
            .And.Contain("assembly.Points[0].StationOffsetElevationToBaseline.X")
            .And.NotContain("baseline.GetAppliedAssemblyAtStation(");
    }

    [Fact]
    public void ScheduleIsValidatedBeforePublishingRegionSamples()
    {
        var source=Source();
        source.IndexOf("CorridorRegionQuantityPolicy.RequireSchedule(")
            .Should().BeLessThan(source.IndexOf("samples.AddRange(regionSamples)"));
        source.Should().Contain("CorridorRegionQuantityPolicy.RequireNonOverlappingRegions(")
            .And.Contain("CorridorRegionQuantityPolicy.SeriesId(baselineName, ++regionOrdinal)")
            .And.Contain("new CorridorQuantityLogic.ShapeSample(st, code, area, seriesId)")
            .And.Contain("expectedStationSeries.Add(schedule)");
    }

    [Fact]
    public void RegionReadFailuresPreserveBlockingProvenanceAndExistingUnits()
    {
        Source().Should().Contain("materialReadFailures.Add(new(\"baseline-regions\", baselineName, ex.Message))")
            .And.Contain("materialReadFailures.Add(new(\"region-assemblies\", seriesId, ex.Message))")
            .And.Contain("materialReadFailures.Add(new(\"region-assembly-station\", seriesId, ex.Message))")
            .And.Contain("materialReadFailures.Sources")
            .And.Contain("50.0 / drawingUnits.LinearToMetres")
            .And.Contain("MaterialSectionAreaEvidence.Capture(");
    }

    [Fact]
    public void OnlyFullyReadMissingCoverageUsesCorridorScopedCompletenessFinding()
    {
        var source=Source();
        var readGuard=source.IndexOf("if (materialReadFailures.Count != readFailuresBeforeRegion) continue;");
        readGuard.Should().BeGreaterThan(source.IndexOf("foreach (CivilDb.AppliedAssembly assembly"));
        readGuard.Should().BeLessThan(source.IndexOf("CorridorRegionQuantityPolicy.RequireSchedule("));
        var typedCatch=source.IndexOf("catch (CorridorRegionQuantityPolicy.CoverageIncompleteException ex)");
        typedCatch.Should().BeGreaterThan(source.IndexOf("samples.AddRange(regionSamples)"));
        var hardCatch=source.IndexOf("materialReadFailures.Add(new(\"region-assemblies\", seriesId, ex.Message))");
        hardCatch.Should().BeGreaterThan(typedCatch);
        var coverageBody=source.Substring(typedCatch,source.IndexOf("catch (Exception ex)",typedCatch)-typedCatch);
        coverageBody.Should().Contain("regionCoverageIssues.Add(ex.Message)")
            .And.NotContain("samples.AddRange").And.NotContain("expectedStationSeries.Add")
            .And.NotContain("materialReadFailures.Add");
        source.Should().Contain("materialCoverageIssues.AddRange(regionCoverageIssues)")
            .And.Contain("EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode");
    }

    private static string Source() => File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(),
        "MahodAI.Civil3D.Plugin", "CivilDelivery", "Estimate", "CorridorQuantityService.cs"));
}
