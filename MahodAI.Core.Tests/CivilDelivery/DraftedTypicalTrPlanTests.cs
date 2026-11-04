using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using C = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Crossing;
using K = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Kind;

namespace MahodAI.Core.Tests.CivilDelivery;

/// <summary>TR-M34 (2026-11-04): a traffic plan with the corridor's piece edges exported and the median's walls as blocks.</summary>
public sealed class DraftedTypicalTrPlanTests
{
    [Theory]
    [InlineData("RD-40+110+W", true)]
    [InlineData("XR|R-2A+205+W", true)]
    [InlineData("RD-40+BIKE-R+W", true)]
    [InlineData("HW-TRWY", false)]
    [InlineData("RD-40+110", false)]
    public void ACorridorPieceEdge_IsKnownByItsLayer(string layer, bool expected) =>
        DraftedTypicalSectionLogic.IsCorridorEdgeLayer(layer).Should().Be(expected);

    [Fact]
    public void Main40_TheLaneToThePieceEdge_IsALane_AndTheWalledMedian_IsTheMedian()
    {
        // Live cut across Main-40: 803 at +-5.05, 3-3 at +-8.80, piece edges at +-12.40, barrier walls at +-0.40 / +-2.70.
        var crossings = new[]
        {
            new C(-12.40, K.Edge, "RD-40+210+W", "host"), new C(-8.80, K.Marking, "TR-MARK-WHT-801_3-3", "host"),
            new C(-5.05, K.EdgeLine, "TR-MARK-WHT-803", "host"), new C(-2.70, K.Barrier, "HW-BARI-WW", "host"),
            new C(-0.40, K.Barrier, "HW-BARI-WW", "host"), new C(0.40, K.Barrier, "HW-BARI-WW", "host"),
            new C(2.70, K.Barrier, "HW-BARI-WW", "host"), new C(5.05, K.EdgeLine, "TR-MARK-WHT-803", "host"),
            new C(8.80, K.Marking, "TR-MARK-WHT-801_3-3", "host"), new C(12.40, K.Edge, "RD-40+110+W", "host"),
        };
        var section = DraftedTypicalSectionLogic.Build(crossings, null, 25);
        section.Strips.Select(s => (s.WidthM.ToString("0.00"), s.Name)).Should().Equal(
            ("3.60", "נתיב"), ("3.75", "נתיב"), ("10.10", "מפרדה"), ("3.75", "נתיב"), ("3.60", "נתיב"));
        section.Findings.Should().BeEmpty();
    }

    [Fact]
    public void AWideStripAroundTheAxis_WithoutABarrier_IsNotCalledAMedian()
    {
        var crossings = new[]
        {
            new C(-5.05, K.EdgeLine, "TR-MARK-WHT-803", "host"), new C(5.05, K.EdgeLine, "TR-MARK-WHT-803", "host"),
        };
        DraftedTypicalSectionLogic.Build(crossings, null, 25).Strips.Single().Name.Should().BeNull();
    }
}
