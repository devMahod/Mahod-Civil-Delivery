using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.CorridorBoq;

/// <summary>
/// The ground line of one section (live WEST r8, Codex 17:23): the ends are the Bot's outer offsets bit for bit, measured at the
/// exact requested point; a sample is an end only within numeric noise; a real nearby breakpoint stays; an end off the surface
/// is never extended. The line runs along +X from an ITM-sized origin, offsets −1 … 7.
/// </summary>
public sealed class CorridorGroundLineTests
{
    private const double X0 = 200000.0, Y0 = 600000.0, From = -1.0264638849464807, To = 7.223541831316389;
    private static double X(double offset) => X0 + (offset - From);

    [Fact]
    public void AnEndSampleInNumericNoiseIsTheExactEndWithItsMeasuredElevation()
    {
        var c = new CorridorGroundLine.Counters();
        var ulp = System.Math.BitIncrement(Y0) - Y0;
        var samples = new List<CorridorGroundLine.Sample>
        {
            new(X(From), Y0, 251.68),
            new(X(2.0), Y0, 251.57),
            new(X(To) - 2 * ulp, Y0, 251.48),   // the returned end, 2 ULP short (r8: 1 ULP – 5e-11 m)
        };
        var line = CorridorGroundLine.Order(X(From), Y0, X(To), Y0, From, To, 1.0, samples, 251.68, 251.48, c);
        line.Select(p => p.OffsetM).Should().Equal(From, line[1].OffsetM, To);
        line[0].OffsetM.Should().Be(From);
        line[^1].OffsetM.Should().Be(To);
        line[^1].Z.Should().Be(251.48);
        c.Measured.Should().Be(2);
        c.Replaced.Should().Be(2);
        c.Added.Should().Be(0);
        c.MaxEndShiftM.Should().BeGreaterThan(0).And.BeLessThan(1e-9);
    }

    [Fact]
    public void AMeasurableEndTheSamplingMissedIsAddedAndTheNearbySampleStays()
    {
        // r8 3000 @ 61240: the last sample 0.063 mm before a measurable end — a real breakpoint, and the end is measured.
        var c = new CorridorGroundLine.Counters();
        var samples = new List<CorridorGroundLine.Sample> { new(X(From), Y0, 1), new(X(To) - 6.3e-5, Y0, 302.696726) };
        var line = CorridorGroundLine.Order(X(From), Y0, X(To), Y0, From, To, 1.0, samples, 1, 302.696785, c);
        line.Should().HaveCount(3);
        line[1].OffsetM.Should().BeApproximately(To - 6.3e-5, 1e-9);
        line[^1].OffsetM.Should().Be(To);
        line[^1].Z.Should().Be(302.696785);
        c.Added.Should().Be(1);
        c.MaxAddedGapM.Should().BeApproximately(6.3e-5, 1e-9);
    }

    [Fact]
    public void AnEndOffTheSurfaceGetsNoPointAndIsNeverExtended()
    {
        var c = new CorridorGroundLine.Counters();
        var samples = new List<CorridorGroundLine.Sample> { new(X(From), Y0, 1), new(X(5.0), Y0, 2) };
        var line = CorridorGroundLine.Order(X(From), Y0, X(To), Y0, From, To, 1.0, samples, 1, null, c);
        line.Select(p => p.OffsetM).Last().Should().BeLessThan(To, "the ground stops at the surface edge; the kernel reports the gap");
        c.OffSurface.Should().Be(1);
        c.Measured.Should().Be(1);
    }

    [Fact]
    public void InteriorOffsetsComeFromTheProjectionAndOutsideSamplesAreDropped()
    {
        var c = new CorridorGroundLine.Counters();
        var samples = new List<CorridorGroundLine.Sample> { new(X(3.0), Y0 + 0.001, 5), new(X(To) + 0.5, Y0, 9) };
        var line = CorridorGroundLine.Order(X(From), Y0, X(To), Y0, From, To, 1.0, samples, 1, 2, c);
        line.Select(p => p.OffsetM).Should().Equal(From, line[1].OffsetM, To);
        line[1].OffsetM.Should().BeApproximately(3.0, 1e-9);   // the plan distance off the line does not lengthen the offset
        c.Outside.Should().Be(1);
    }

    [Fact]
    public void DrawingUnitsAreConvertedButTheEndsKeepTheBotMetres()
    {
        // millimetre drawing: coordinates in mm, offsets in metres from the Bot
        var c = new CorridorGroundLine.Counters();
        double xm(double o) => (X0 + (o - From)) * 1000;
        var samples = new List<CorridorGroundLine.Sample> { new(xm(2.0), Y0 * 1000, 7) };
        var line = CorridorGroundLine.Order(xm(From), Y0 * 1000, xm(To), Y0 * 1000, From, To, 0.001, samples, 1, 2, c);
        line[0].OffsetM.Should().Be(From);
        line[^1].OffsetM.Should().Be(To);
        line[1].OffsetM.Should().BeApproximately(2.0, 1e-9);
    }
}
