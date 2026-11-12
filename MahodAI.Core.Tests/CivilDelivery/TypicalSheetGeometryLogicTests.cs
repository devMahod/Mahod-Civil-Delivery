using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.TypicalSheetGeometryLogic;

namespace MahodAI.Core.Tests.CivilDelivery;

public class TypicalSheetGeometryLogicTests
{
    private static SurfaceLink L(double x1, double y1, double x2, double y2, bool paved = true) => new(new Pt(x1, y1), new Pt(x2, y2), paved);

    // A crowned two-carriageway road at 2% each way, a curb step, then a 1:2.5 embankment on the right.
    private static readonly SurfaceLink[] Road =
    {
        L(-7, -0.14, 0, 0), L(0, 0, 7, -0.14), L(7, -0.14, 7, 0.01, paved: false), L(7, 0.01, 8, 0.01, paved: false),
        L(8, 0.01, 13, -1.99, paved: false),
    };

    [Fact]
    public void LevelAt_ReadsTheSurfaceUnderAnOffset()
    {
        LevelAt(Road, 0).Should().BeApproximately(0, 1e-9);
        LevelAt(Road, 3.5).Should().BeApproximately(-0.07, 1e-9);
        LevelAt(Road, 7).Should().BeApproximately(0.01, 1e-9, "on the curb face the top of the curb is the surface");
        LevelAt(Road, 20).Should().BeNull();
    }

    [Fact]
    public void VehicleNeedsMeasuredSurfaceAtBothWheelsAndBetweenThem()
    {
        VehicleSeat(Road, -9, .635).Should().BeNull();
        VehicleSeat(new[] { L(-2, 0, -.1, 0), L(.1, 0, 2, 0) }, 0, .85).Should().BeNull();
        VehicleSeat(Road, 10, .85).Should().BeNull();
        var seat = VehicleSeat(Road, 3.5, .85)!.Value;
        seat.Y.Should().BeApproximately(-.07, 1e-9);
        System.Math.Tan(seat.Rotation).Should().BeApproximately(-.02, 1e-9);
        VehicleSeat(null, 3.5, .85)!.Value.Y.Should().Be(0);
    }

    [Fact]
    public void AdjacentRoadAndHolesAreExcludedWithoutCallingPartialWidthsLanes()
    {
        var section = DraftedTypicalSectionLogic.Build(new[] { -7.0, -3.5, 0, 3.5, 7 }
            .Select(x => new DraftedTypicalSectionLogic.Crossing(x, DraftedTypicalSectionLogic.Kind.Marking, "TR", "host")), null, 20);
        var fitted = FitToSurface(section, new[] { L(-2, 0, 2, 0), L(2.5, 0, 7, 0) });
        fitted.Breaks.Should().OnlyContain(b => b.OffsetM >= -2);
        fitted.Strips.Should().NotContain(s => s.FromM < 2.5 && s.ToM > 2);
        fitted.Strips.Where(s => s.FromM < 3.5).Should().OnlyContain(s => s.Name == null);
        fitted.Strips.Last().Name.Should().Be("נתיב");
        fitted.Findings.Should().Contain(s => s.Contains("לתחום פני הדרך"));
    }

    [Theory]
    [InlineData("נר\"ת", "BusFront")]
    [InlineData("נר״ת", "BusBack")]
    [InlineData("נת\"צ", "BusFront")]
    public void TransitLanesUseGroundedMetricBusElevations(string name, string symbol)
    {
        SectionFurnitureLogic.VehicleForStrip(name).Should().Be(SectionFurnitureLogic.Bus);
        var loops = OfficeTypicalSheet.Symbol(symbol);
        loops.Should().OnlyContain(l => l.Closed && l.Type == "P");
        var xs = loops.SelectMany(l => l.P.Where((_, i) => i % 3 == 0)).ToList();
        var ys = loops.SelectMany(l => l.P.Where((_, i) => i % 3 == 1)).ToList();
        (xs.Max() - xs.Min()).Should().BeApproximately(2.55, 1e-9);
        ys.Min().Should().Be(0);
        ys.Max().Should().BeApproximately(3.1, 1e-9);
    }

    [Fact]
    public void CrossSlopes_AreOnePerCarriageway_PointingDownhill()
    {
        var slopes = CrossSlopes(Road, new[] { (-7.0, -3.5), (-3.5, 0.0), (0.0, 3.5), (3.5, 7.0) });

        slopes.Should().HaveCount(2);
        slopes[0].Should().Match<CrossSlope>(s => s.FromX == -7 && s.ToX == 0 && !s.DownToRight && s.Text == "2.0%");
        slopes[1].Should().Match<CrossSlope>(s => s.FromX == 0 && s.ToX == 7 && s.DownToRight && s.Text == "2.0%");
    }

    [Fact]
    public void SideSlopes_AreTheUnpavedFacesBeyondTheRoad()
    {
        var marks = SideSlopes(Road, -7, 7);
        marks.Should().ContainSingle().Which.Text.Should().Be("1:2.5", "5 m across for 2 m down");
    }

    [Fact]
    public void Paved_IsWhereThePavedLinksCoverTheStrip()
    {
        IsPaved(Road, -7, 0).Should().BeTrue();
        IsPaved(Road, 6.5, 8).Should().BeFalse("the curb and the verge are not paved");
        PavedShare(Road, 0, 14).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void LayersAt_ListsTheLayersUnderAnOffset_TopDown()
    {
        var layers = new[]
        {
            new Layer("MAZA-A", new[] { new Pt(-5, -0.09), new Pt(5, -0.09), new Pt(5, -0.29), new Pt(-5, -0.29) }),
            new Layer("ASF-5-70", new[] { new Pt(-5, -0.04), new Pt(5, -0.04), new Pt(5, -0.09), new Pt(-5, -0.09) }),
            new Layer("ASF-TAMA", new[] { new Pt(-5, 0), new Pt(5, 0), new Pt(5, -0.04), new Pt(-5, -0.04) }),
        };

        var at = LayersAt(layers, 2);

        at.Select(l => l.Code).Should().Equal("ASF-TAMA", "ASF-5-70", "MAZA-A");
        at.Select(l => System.Math.Round(l.ThicknessM, 2)).Should().Equal(0.04, 0.05, 0.20);
        LayersAt(layers, 6).Should().BeEmpty();
    }
}
