using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class ExplodedAlignmentLogicTests
{
    // A road drawn right to left as three pieces (one reversed), stationed 1+380 at its east end, growing westward:
    // like TR-M34's Main-40, whose ticks print only "380", "400", ... and whose curve label says "Main-40 1+398.90".
    private static readonly IReadOnlyList<P2>[] Pieces =
    {
        new[] { new P2(300, 0), new P2(200, 0) },
        new[] { new P2(100, 0), new P2(200, 0) },
        new[] { new P2(100, 0), new P2(0, 0) },
    };

    private static List<ExplodedAlignmentLogic.Tick> Ticks() =>
        Enumerable.Range(0, 16).Select(i => new ExplodedAlignmentLogic.Tick(new P2(300 - 20 * i, 0), (380 + 20 * i) % 1000)).ToList();

    [Fact]
    public void ThePieces_ChainIntoOneAxis_WhicheverWayEachWasDrawn()
    {
        var chains = ExplodedAlignmentLogic.Chain(Pieces);
        chains.Should().ContainSingle();
        chains[0].First().Should().Be(new P2(300, 0));
        chains[0].Last().Should().Be(new P2(0, 0));
    }

    [Fact]
    public void TheTicks_GiveTheDirection_AndTheLabels_TheKilometreAndTheName()
    {
        var label = new ExplodedAlignmentLogic.Label("Main-40", 1398.90, new P2(281, 15), new[] { new P2(270, 0), new P2(290, 0) });
        var axis = ExplodedAlignmentLogic.Build(Pieces, Ticks(), new[] { label }, "HW-ALGN", 30).Single();
        axis.Name.Should().Be("Main-40");
        axis.StartStation.Should().BeApproximately(1380, 1e-6);
        var plan = new PlanAxis(axis.Name, axis.Vertices, axis.StartStation);
        plan.TryStationOffset(new P2(100, 5), out var station, out _, out _).Should().BeTrue();
        station.Should().BeApproximately(1580, 1e-6);
        plan.PointAt(1480, 0).X.Should().BeApproximately(200, 1e-6);
    }

    [Fact]
    public void TheValues_RollOverTheKilometre_WithoutBreakingTheStations()
    {
        // 980, 000, 020 ... across 1+000: one start for all of them.
        var ticks = new[] { (0.0, 980.0), (20.0, 0.0), (40.0, 20.0), (60.0, 40.0) }
            .Select(t => new ExplodedAlignmentLogic.Tick(new P2(t.Item1, 0), t.Item2)).ToList();
        var axis = ExplodedAlignmentLogic.Build(new[] { new[] { new P2(0, 0), new P2(100, 0) } }, ticks,
            System.Array.Empty<ExplodedAlignmentLogic.Label>(), "HW-ALGN", 30).Single();
        axis.StartStation.Should().BeApproximately(980, 1e-6);
        axis.Name.Should().Be("HW-ALGN #1");
    }

    [Fact]
    public void SectionNumbers_OnTheSameAxis_DoNotMoveTheStations()
    {
        // Live 2026-11-04, TR-M34 Ramp_2C: station ticks 420..500 every 20 m from s=10.26, and inside a block section
        // numbers 18..22 every 20 m from s=5.61 on the same axis - they had pulled the start to -25.61.
        var ticks = Enumerable.Range(0, 5).SelectMany(i => new[]
        {
            new ExplodedAlignmentLogic.Tick(new P2(10.26 + 20 * i, 0), 420 + 20 * i),
            new ExplodedAlignmentLogic.Tick(new P2(5.61 + 20 * i, 0), 18 + i),
        }).ToList();
        var axis = ExplodedAlignmentLogic.Build(new[] { new[] { new P2(0, 0), new P2(90.26, 0) } }, ticks,
            System.Array.Empty<ExplodedAlignmentLogic.Label>(), "HW-ALGN", 30).Single();
        axis.StartStation.Should().BeApproximately(409.74, 1e-6);
        axis.Vertices[0].Should().Be(new P2(0, 0));
    }

    [Fact]
    public void ShortPieces_AndUnlabelledRoads_AreHandled()
    {
        ExplodedAlignmentLogic.Build(new[] { new[] { new P2(0, 0), new P2(10, 0) } }, Ticks(),
            System.Array.Empty<ExplodedAlignmentLogic.Label>(), "HW-ALGN", 30).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Ramp_1\n0+044.49\nR=60.00 L=24.729", "Ramp_1", 44.49)]
    [InlineData("Main-40\r\n1+398.90\r\nR=1200", "Main-40", 1398.90)]
    public void ACurveLabel_GivesTheRoadAndItsStation(string text, string name, double station)
    {
        var parsed = ExplodedAlignmentLogic.ParseLabel(text);
        parsed.Should().NotBeNull();
        parsed!.Value.Name.Should().Be(name);
        parsed.Value.Station.Should().BeApproximately(station, 1e-9);
    }

    [Theory]
    [InlineData("020", 20.0)]
    [InlineData("545.56", 545.56)]
    public void ATickValue_IsTheStationInItsKilometre(string text, double value) =>
        ExplodedAlignmentLogic.TickValue(text).Should().Be(value);

    [Theory]
    [InlineData("HW-ALGN", true)]
    [InlineData("XR|HW-ALGN", true)]
    [InlineData("HW-ALGN-SEC-NAME", false)]
    [InlineData("HW-TRWY", false)]
    public void TheAlignmentLayer_IsTheAxisLayerOnly(string layer, bool expected) =>
        ExplodedAlignmentLogic.IsAlignmentLayer(layer).Should().Be(expected);

    [Fact]
    public void NotLabels_AreNotRead()
    {
        ExplodedAlignmentLogic.ParseLabel("R=60.00\n0+044.49").Should().BeNull();
        ExplodedAlignmentLogic.ParseLabel("Ramp_1").Should().BeNull();
        ExplodedAlignmentLogic.TickValue("3.50x").Should().BeNull();
    }
}
