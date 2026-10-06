using System;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MaterialSectionAreaEvidenceTests
{
    private static readonly string Hash = new('a', 64);
    private static CorridorQuantityLogic.ShapeSample S(double st, double area,
        string code = "H1", string series = "baseline-1") => new(st, code, area, series);

    private static MaterialSectionAreaObservation[] Read(
        CorridorQuantityLogic.ShapeSample[] samples, double scale = 1, bool metric = true,
        bool coverage = true) => MaterialSectionAreaEvidence.Capture(samples, "run-1",
        @"C:\local\host.dwg", Hash, "AB12", "Road", scale, metric,
        metric && scale == .001 ? "Millimeters" : metric ? "Meters" : "Unitless",
        coverage).ToArray();

    [Fact]
    public void SameCodeSumsOnlyShapesAtSameStationAndBaseline()
    {
        var rows = Read(new[] { S(0, 2), S(0, 3), S(10, 7), S(0, 11, series: "baseline-2") });
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { 5d, 7d, 11d }, rows.Select(x => x.Area));
        Assert.Equal(new[] { 2, 1, 1 }, rows.Select(x => x.ShapeCount));
        Assert.All(rows, row => Assert.Contains("not-plan-area", row.QuantityMeaning));
    }

    [Fact]
    public void CloseStationsAndZeroTaperArePreserved()
    {
        var rows = Read(new[] { S(10, 0), S(10.000001, 4) });
        Assert.Equal(2, rows.Length);
        Assert.Equal(0, rows[0].Area);
        Assert.NotEqual(rows[0].Station, rows[1].Station);
    }

    [Fact]
    public void MillimetresConvertAreaSquaredAndStationLinearly()
    {
        var row = Assert.Single(Read(new[] { S(12000, 1500000) }, .001));
        Assert.Equal(12, row.Station);
        Assert.Equal(1.5, row.Area);
        Assert.Equal("מ\"ר", row.AreaUnit);
        Assert.True(row.MetricUnitsProven);
    }

    [Fact]
    public void UnknownUnitsAreNotLabelledMetresAndPartialCoverageStaysPartial()
    {
        var row = Assert.Single(Read(new[] { S(12, 4) }, metric: false, coverage: false));
        Assert.Equal("יחידת שרטוט²", row.AreaUnit);
        Assert.False(row.MetricUnitsProven);
        Assert.False(row.CorridorCoverageProven);
        Assert.Equal(4, row.Area);
    }

    [Fact]
    public void CodeIsNotGuessedFromThicknessAndSourceLineageRoundTrips()
    {
        var row = Assert.Single(Read(new[] { S(1, 3, "Pave1") }));
        var copy = JsonSerializer.Deserialize<MaterialSectionAreaObservation>(JsonSerializer.Serialize(row));
        Assert.Equal(row, copy);
        Assert.Equal("Pave1", copy!.ShapeCode);
        Assert.Equal(Hash, copy.DrawingHash);
        Assert.Equal("AB12", copy.CorridorHandle);
        Assert.Equal("run-1", copy.RunId);
        Assert.False(typeof(NeutralQuantityRecord).IsAssignableFrom(row.GetType()));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidAreaIsAnErrorNotZero(double area) =>
        Assert.Throws<ArgumentException>(() => Read(new[] { S(0, area) }));

    [Fact]
    public void OverflowAndUnprovenConversionAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Read(new[] { S(0, double.MaxValue) }, 1000));
        Assert.Throws<ArgumentException>(() => Read(new[] { S(0, 1) }, .001, false));
        Assert.Throws<ArgumentException>(() => Read(new[] { S(double.NaN, 1) }));
    }

    [Fact]
    public void MissingSourceIdentityCannotPublishEvidence() =>
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaEvidence.Capture(
            new[] { S(0, 1) }, "run", @"C:\a.dwg", "not-sha", "1", "road", 1, true, "Meters", true));
}
