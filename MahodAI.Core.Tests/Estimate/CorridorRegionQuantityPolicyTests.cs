using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using static MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic;
using static MahodAI.CivilDelivery.Estimate.CorridorRegionQuantityPolicy;
using Region = MahodAI.CivilDelivery.Estimate.CorridorRegionQuantityPolicy.Region;

namespace MahodAI.Core.Tests.Estimate;

public class CorridorRegionQuantityPolicyTests
{
    [Fact]
    public void ShortDisconnectedRegionsDoNotInventMaterialAcrossTheGap()
    {
        var a = new Region(SeriesId("b", 1), 0, 10);
        var b = new Region(SeriesId("b", 2), 20, 30);
        RequireNonOverlappingRegions(new[] { a, b });
        var stations = new[] { 0d, 10d, 20d, 30d };
        var schedules = new[] { RequireSchedule(a, stations, new[] { 0d, 10d }),
            RequireSchedule(b, stations, new[] { 20d, 30d }) };
        var samples = schedules.SelectMany(s => s.Stations.Select(st => new ShapeSample(st, "Base", 2, s.SeriesId))).ToList();
        var volumes = AverageEndArea(samples);
        volumes.Should().HaveCount(2);
        volumes.Sum(v => v.VolumeM3).Should().Be(40, "the unmodelled 10m gap must not become 20 cubic metres");
        MaterialCoverageIssues(samples, schedules, 50).Should().BeEmpty();
    }

    [Fact]
    public void SharedEndpointUsesEachRegionsOwnCrossSectionWithoutDoubleCounting()
    {
        var a = new Region(SeriesId("b", 1), 0, 10);
        var b = new Region(SeriesId("b", 2), 10, 20);
        RequireNonOverlappingRegions(new[] { a, b });
        var stations = new[] { 0d, 10d, 20d };
        RequireSchedule(a, stations, new[] { 0d, 10d });
        RequireSchedule(b, stations, new[] { 10d, 20d });
        var volumes = AverageEndArea(new[] { new ShapeSample(0,"Base",2,a.SeriesId), new ShapeSample(10,"Base",2,a.SeriesId),
            new ShapeSample(10,"Base",4,b.SeriesId), new ShapeSample(20,"Base",4,b.SeriesId) });
        volumes.Sum(v => v.VolumeM3).Should().Be(60, "the boundary jump must not be averaged or summed across regions");
        volumes.Single(v => v.SeriesId == a.SeriesId).VolumeM3.Should().Be(20);
        volumes.Single(v => v.SeriesId == b.SeriesId).VolumeM3.Should().Be(40);
        var evidence = MaterialSectionAreaEvidence.Capture(new[] {
            new ShapeSample(10,"Base",2,a.SeriesId), new ShapeSample(10,"Base",4,b.SeriesId) },
            "run", "test.dwg", new string('a',64), "C1", "Corridor", 1, true, "Meters", true);
        evidence.Should().HaveCount(2, "two sides of a shared endpoint are separate region observations");
        evidence.Select(e=>e.Area).Should().BeEquivalentTo(new[] { 2d, 4d });
        evidence.Select(e=>e.BaselineSeries).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.3048)]
    [InlineData(0.001)]
    public void RegionIsolationPreservesDrawingUnitsUntilExistingMetreConversion(double scale)
    {
        var a = new Region("b/r1", 0, 10/scale);
        var b = new Region("b/r2", 20/scale, 30/scale);
        RequireNonOverlappingRegions(new[] { a, b });
        var stations = new[] { 0, 10/scale, 20/scale, 30/scale };
        var schedules = new[] { RequireSchedule(a, stations, new[] { 0, 10/scale }),
            RequireSchedule(b, stations, new[] { 20/scale, 30/scale }) };
        var samples = new[] { new ShapeSample(0,"Base",2/(scale*scale),"b/r1"),
            new ShapeSample(10/scale,"Base",2/(scale*scale),"b/r1"),
            new ShapeSample(20/scale,"Base",2/(scale*scale),"b/r2"),
            new ShapeSample(30/scale,"Base",2/(scale*scale),"b/r2") };
        MaterialCoverageIssues(samples, schedules, 50/scale).Should().BeEmpty();
        // Existing integration rounds stations to 4 drawing-unit decimals: four
        // endpoints contribute at most 0.00005*scale metres each, times area 2m².
        var roundingBound = 4 * 0.00005 * scale * 2;
        (AverageEndArea(samples, 50/scale).Sum(v=>v.VolumeM3)*scale*scale*scale)
            .Should().BeApproximately(40, roundingBound);
    }

    [Theory]
    [InlineData(0,0)]
    [InlineData(10,0)]
    [InlineData(double.NaN,10)]
    [InlineData(0,double.PositiveInfinity)]
    public void InvalidRegionBoundsAreRefused(double start, double end) =>
        ((Action)(() => RequireNonOverlappingRegions(new[]{new Region("b/r",start,end)}))).Should().Throw<ArgumentException>();

    [Fact]
    public void OverlappingRegionsAreRefusedBeforeAnyQuantityIsPublished() =>
        ((Action)(() => RequireNonOverlappingRegions(new[]{new Region("a",0,15),new Region("b",10,20)})))
        .Should().Throw<ArgumentException>().WithMessage("*overlap*");

    [Fact]
    public void OutOfOrderDisjointRegionsAreAllowed() =>
        ((Action)(() => RequireNonOverlappingRegions(new[]{new Region("b",20,30),new Region("a",0,10)})))
        .Should().NotThrow();

    [Fact]
    public void DuplicateRegionIdentityIsRefused() =>
        ((Action)(() => RequireNonOverlappingRegions(new[]{new Region("b/r",0,10),new Region("B/R",20,30)})))
        .Should().Throw<ArgumentException>().WithMessage("*duplicated*");

    [Fact]
    public void BaselineWithoutRegionsIsNotAnEmptySuccessfulQuantity() =>
        ((Action)(() => RequireNonOverlappingRegions(Array.Empty<Region>())))
        .Should().Throw<ArgumentException>().WithMessage("*no readable regions*");

    [Theory]
    [InlineData(new double[]{0,0,10})]
    [InlineData(new double[]{0,0.00001,10})]
    public void DuplicateAssemblyStationsWithinOneRegionCannotDoubleTheShapeArea(double[] observed) =>
        ((Action)(() => RequireSchedule(new Region("r",0,10),new[]{0d,10d},observed)))
        .Should().Throw<ArgumentException>().WithMessage("*Duplicate*");

    [Theory]
    [InlineData(new double[]{})]
    [InlineData(new double[]{1,10})]
    [InlineData(new double[]{0,9})]
    [InlineData(new double[]{0})]
    public void FullyReadMissingRegionEndpointsAreCoverageGapsAndAreNotInvented(double[] observed) =>
        ((Action)(() => RequireSchedule(new Region("r",0,10),new[]{0d,10d},observed)))
        .Should().Throw<CoverageIncompleteException>().WithMessage("*incomplete endpoint coverage*");

    [Theory]
    [InlineData(new double[]{-1,0,10})]
    [InlineData(new double[]{0,10,11})]
    [InlineData(new double[]{-1})]
    [InlineData(new double[]{11})]
    public void OutOfRangeStationRemainsHardFailureEvenWhenCoverageIsAlsoMissing(double[] observed) =>
        ((Action)(() => RequireSchedule(new Region("r",0,10),new[]{0d,10d},observed)))
        .Should().ThrowExactly<ArgumentException>().WithMessage("*outside its region range*");

    [Fact]
    public void FullyReadMissingKnownScheduledAssemblyIsTypedMissingCoverage() =>
        ((Action)(() => RequireSchedule(new Region("r",0,10),new[]{0d,5d,10d},new[]{0d,10d})))
        .Should().Throw<CoverageIncompleteException>().WithMessage("*missing a scheduled*");

    [Fact]
    public void EmptyReadableRegionDiagnosticPreservesBoundsCountAndNoInventedSample()
    {
        var failure = ((Action)(() => RequireSchedule(new Region("r",42000,42100),
            Array.Empty<double>(),Array.Empty<double>())))
            .Should().Throw<CoverageIncompleteException>().Which;
        failure.Message.Should().Contain("start=42000").And.Contain("end=42100")
            .And.Contain("observed_count=0").And.Contain("first=none").And.Contain("last=none")
            .And.Contain("No missing sample or volume was inferred");
    }

    [Fact]
    public void StationsFromOtherRegionsAreNotDemandedInsideThisRegion()
    {
        var schedule=RequireSchedule(new Region("r",0,10),new[]{0d,10d,20d,30d},new[]{10d,0d});
        schedule.Stations.Should().Equal(0,10);
    }

    [Fact]
    public void ARealAdditionalAssemblyIsRetainedAsMeasuredEvidence()
    {
        var schedule=RequireSchedule(new Region("r",0,10),new[]{0d,10d},new[]{0d,5d,10d});
        schedule.Stations.Should().Equal(0,5,10);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonfiniteObservedOrExpectedStationsAreRefused(bool expected)
    {
        var invalid=new[]{0d,double.NaN,10d}; var valid=new[]{0d,10d};
        ((Action)(() => RequireSchedule(new Region("r",0,10),expected?invalid:valid,expected?valid:invalid)))
            .Should().Throw<ArgumentException>().WithMessage("*non-finite*");
    }
}
