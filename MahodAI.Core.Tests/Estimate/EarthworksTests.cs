using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public class EarthworksTests
{
    /// <summary>Ground z = base + slope·x on a 1 m point lattice (exact under linear interpolation).</summary>
    private static TinModel Plane(double baseZ, double slopeX, double slopeY = 0, double size = 100, double step = 5)
    {
        var pts = new List<Xyz>();
        for (var x = -20.0; x <= size + 20; x += step)
            for (var y = -20.0; y <= size + 20; y += step)
                pts.Add(new Xyz(x, y, baseZ + slopeX * x + slopeY * y));
        return TinModel.FromPoints(pts);
    }

    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Square(double a, double b) =>
        new[] { (IReadOnlyList<(double, double)>)new[] { (a, a), (b, a), (b, b), (a, b) } };

    private static readonly Earthworks.Settings NoStrip = new() { StripCm = 0, Shrink = 1, FillSlope = 0, CutSlope = 0 };

    [Fact]
    public void TinFromPointsInterpolatesAPlaneExactly()
    {
        var tin = Plane(100, 0.1);
        Assert.True(tin.TryElevation(37.3, 61.9, out var z));
        Assert.Equal(103.73, z, 6);
        Assert.False(tin.TryElevation(500, 500, out _));
    }

    [Fact]
    public void PlatformOnATiltedPlaneCutsAndFillsTheTwoTriangles()
    {
        // z = 100 + 0.1x over 100×100 m; a platform at 105 cuts the east half and fills the west half, 12,500 m³ each.
        var grid = Earthworks.Sample(Plane(100, 0.1), null, Square(0, 100), null, NoStrip, cell: 1);
        var v = Earthworks.Measure(grid, NoStrip, 105);
        Assert.Equal(12_500, v.Cut, 0);
        Assert.Equal(12_500, v.Fill, 0);
        Assert.Equal(10_000, v.WorkArea, 6);
        Assert.True(v.SamplingSpread < 0.01);
    }

    [Fact]
    public void BalancedLevelOfASymmetricSlopeIsItsMiddle()
    {
        var grid = Earthworks.Sample(Plane(100, 0.1), null, Square(0, 100), null, NoStrip, cell: 1);
        Assert.Equal(105, Earthworks.BalancedLevel(grid, NoStrip), 2);
    }

    [Fact]
    public void ShrinkageLowersTheBalancedLevelSoTheCutMakesTheFill()
    {
        var s = NoStrip with { Shrink = 0.9 };
        var grid = Earthworks.Sample(Plane(100, 0.1), null, Square(0, 100), null, s, cell: 1);
        var level = Earthworks.BalancedLevel(grid, s);
        Assert.True(level < 105);
        var b = Earthworks.Balanced(Earthworks.Measure(grid, s, level), s);
        Assert.True(b.ImportFill < 30, $"import {b.ImportFill}");
        Assert.True(b.CutToWaste < 40, $"waste {b.CutToWaste}");
    }

    [Fact]
    public void StrippingIsTakenOffTheCutAndMadeUpInTheFill()
    {
        // Flat ground at 100, platform at 99: 1 m cut over 100×100 = 10,000 m³; after 20 cm stripping only 8,000 m³ remain.
        var s = NoStrip with { StripCm = 20 };
        var grid = Earthworks.Sample(Plane(100, 0), null, Square(0, 100), null, s, cell: 1);
        Assert.Equal(8_000, Earthworks.Measure(grid, s, 99).Cut, 0);
        // Platform at 101: 1 m of fill plus the 20 cm stripped.
        Assert.Equal(12_000, Earthworks.Measure(grid, s, 101).Fill, 0);
    }

    [Fact]
    public void PavementStructureLowersTheDesignToTheSubgrade()
    {
        // Ground and finished grade both at 100: no earthworks — except the box under the road, 50×100 m × 0.6 m.
        var design = Plane(100, 0);
        var road = new[] { ((IReadOnlyList<IReadOnlyList<(double X, double Y)>>)new[] { (IReadOnlyList<(double, double)>)new[] { (0d, 0d), (50d, 0d), (50d, 100d), (0d, 100d) } }, 0.6) };
        var grid = Earthworks.Sample(Plane(100, 0), design, Square(0, 100), road, NoStrip, cell: 1);
        var v = Earthworks.Measure(grid, NoStrip, null);
        Assert.Equal(3_000, v.Cut, 0);
        Assert.Equal(0, v.Fill, 6);
        Assert.Equal(5_000, v.PavedArea, 6);
    }

    [Fact]
    public void WithoutAMarkedAreaTheDesignSurfaceIsTheWorkArea()
    {
        var design = TinModel.FromPoints(new[] { new Xyz(10, 10, 103), new Xyz(30, 10, 103), new Xyz(30, 30, 103), new Xyz(10, 30, 103) });
        var grid = Earthworks.Sample(Plane(100, 0), design, null, null, NoStrip, cell: 1);
        var v = Earthworks.Measure(grid, NoStrip, null);
        Assert.Equal(400, v.WorkArea, 6);
        Assert.Equal(1_200, v.Fill, 0);
    }

    [Fact]
    public void PlatformSideSlopesAddTheirWedges()
    {
        // Platform 2 m above flat ground, 1:2 fill slopes: ½·2·2² = 4 m³ per metre of the 400 m edge.
        var s = NoStrip with { FillSlope = 2 };
        var grid = Earthworks.Sample(Plane(100, 0), null, Square(0, 100), null, s, cell: 1);
        var v = Earthworks.Measure(grid, s, 102);
        Assert.Equal(1_600, v.FillSlopes, 0);
        Assert.Equal(1_600, v.SlopeArea, 0);
    }

    [Fact]
    public void BalanceSendsSurplusAwayAndImportsTheShortfall()
    {
        var s = new Earthworks.Settings { Shrink = 0.9 };
        var surplus = Earthworks.Balanced(new Earthworks.Volumes(10_000, 4_500, 0, 0, 10_000, 0, 6_000, 4_000, 0, 10_000, 4_500), s);
        Assert.Equal(5_000, surplus.CutToFill, 6);
        Assert.Equal(5_000, surplus.CutToWaste, 6);
        Assert.Equal(0, surplus.ImportFill, 6);
        var shortfall = Earthworks.Balanced(new Earthworks.Volumes(1_000, 4_500, 0, 0, 10_000, 0, 2_000, 8_000, 0, 1_000, 4_500), s);
        Assert.Equal(1_000, shortfall.CutToFill, 6);
        Assert.Equal(3_600, shortfall.ImportFill, 6);
    }

    [Fact]
    public void RowsCarryTheNtiEarthworksItems()
    {
        var s = new Earthworks.Settings();
        var b = Earthworks.Balanced(new Earthworks.Volumes(10_000, 4_500, 0, 0, 10_000, 0, 6_000, 4_000, 0, 10_000, 4_500), s);
        var codes = Earthworks.Rows(b, s).Select(r => r.Code).ToArray();
        Assert.Equal(new[] { "51.01.1060", "51.02.0010", "51.02.0020", "51.02.0060", "51.02.0040" }, codes);
        var groups = Earthworks.DerivedGroups(Earthworks.Rows(b, s), "test", defaults: true);
        Assert.All(groups, g => Assert.Contains(Earthworks.DefaultMark, g.Layer));
        Assert.Equal(Earthworks.DerivedPart, groups[0].GroupPart);
    }

    [Theory]
    [InlineData("MK", "C-TOPO", "Existing")]
    [InlineData("EG", "", "Existing")]
    [InlineData("קיים", "", "Existing")]
    [InlineData("FG", "", "Design")]
    [InlineData("Corridor (1) Top", "", "Design")]
    [InlineData("Corridor (1) Datum", "", "Subgrade")]
    [InlineData("Surface1", "0", "Unknown")]
    public void SurfacesAreRecognisedByName(string name, string layer, string role)
        => Assert.Equal(Enum.Parse<EarthRole>(role), Earthworks.Classify(name, layer));

    [Theory]
    [InlineData("M1502", true)]
    [InlineData("SR-3933-VSC|M1506", true)]
    [InlineData("M1513", true)]
    [InlineData("M1500", false)]
    [InlineData("M1509", false)]
    [InlineData("C-TOPO", true)]
    [InlineData("HW-ROAD-EOP", false)]
    public void ReliefLayersFollowTheSurveyOfIsraelCodes(string layer, bool relief)
        => Assert.Equal(relief, SurveyGround.IsReliefLayer(layer));

    [Fact]
    public void SurveyBlundersAreDropped()
    {
        var pts = new List<Xyz>();
        for (var x = 0; x <= 100; x += 10) for (var y = 0; y <= 100; y += 10) pts.Add(new Xyz(x, y, 20 + x * 0.02));
        pts.Add(new Xyz(55, 55, 0));          // level left at zero
        pts.Add(new Xyz(45, 45, 93_436.13));   // a coordinate typed as a level
        pts.Add(new Xyz(35, 65, 31));          // a spike 10 m above its neighbours
        var clean = SurveyGround.Clean(pts);
        Assert.Equal(1, clean.Zero);
        Assert.Equal(1, clean.Implausible);
        Assert.Equal(1, clean.Spikes);
        Assert.Equal(121, clean.Points.Count);
    }

    [Theory]
    [InlineData(0.01, "מישורית")]
    [InlineData(0.05, "גבעית")]
    [InlineData(0.12, "הררית")]
    public void TerrainClassFollowsHeightDifferencePerKilometre(double slope, string terrain)
    {
        var grid = Earthworks.Sample(Plane(100, slope), null, Square(0, 100), null, NoStrip, cell: 2);
        Assert.Equal(terrain, Earthworks.Analyse(grid).Terrain);
    }

    [Fact]
    public void WaterRunsDownTheSlope()
    {
        var grid = Earthworks.Sample(Plane(100, 0.06), null, Square(0, 100), null, NoStrip, cell: 2);
        var t = Earthworks.Analyse(grid);
        Assert.Equal("מערב", t.Downhill);
        Assert.Equal(6, t.MeanSlopePercent, 3);
        Assert.Equal(1.0, t.Slopes.Single(c => c.Label == "5–10%").Share, 3);
    }

    [Fact]
    public void ScanLineFillHonoursHoles()
    {
        var rings = new IReadOnlyList<(double X, double Y)>[] { new[] { (0d, 0d), (10d, 0d), (10d, 10d), (0d, 10d) }, new[] { (3d, 3d), (7d, 3d), (7d, 7d), (3d, 7d) } };
        var mask = Earthworks.Rasterize(rings, 0, 0, 1, 10, 10);
        Assert.Equal(84, mask.Count(m => m));
    }

    [Fact]
    public void CellSizeKeepsTheGridBounded()
    {
        Assert.Equal(0.5, Earthworks.CellSizeFor(10_000));
        Assert.Equal(1, Earthworks.CellSizeFor(300_000));
        Assert.Equal(5, Earthworks.CellSizeFor(8_000_000));
    }
}
