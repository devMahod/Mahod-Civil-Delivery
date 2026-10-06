using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    public class GuideRasterizerTests
    {
        // 50x50m grid at 2m cells → 25x25 cells, all walkable.
        private static (bool[,] walkable, Envelope env, double cellSize, int nx, int ny)
            OpenSquare()
        {
            const double cellSize = 2.0;
            const int nx = 25, ny = 25;
            var w = new bool[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++) w[x, y] = true;
            var env = new Envelope(0, nx * cellSize, 0, ny * cellSize);
            return (w, env, cellSize, nx, ny);
        }

        [Fact]
        public void Build_NoGuides_ReturnsAllOnes()
        {
            var (w, env, cs, nx, ny) = OpenSquare();
            var costMul = GuideRasterizer.Build(
                new GuideDiscovery.DiscoveredGuides(),
                w, env, cs);

            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    costMul[x, y].Should().BeApproximately(1.0, 1e-9,
                        $"cell ({x},{y}) should have no discount");
        }

        [Fact]
        public void Build_GuideCenterline_AppliesRampedDiscount()
        {
            // Horizontal centerline at y=25, the corridor's midline.
            var (w, env, cs, nx, ny) = OpenSquare();
            var guides = new GuideDiscovery.DiscoveredGuides
            {
                Centerlines = { new List<Pt2> { new(0, 25), new(50, 25) } },
            };
            var costMul = GuideRasterizer.Build(guides, w, env, cs,
                guideBandM: 6.0, guideStrength: 0.85);

            // Cell on the line (gy=12 → world y=25) should get the strongest
            // discount: 1 - 0.85 = 0.15.
            costMul[12, 12].Should().BeApproximately(0.15, 0.05,
                $"cell on the centerline should hit minCost; got {costMul[12,12]:F2}");
            // Cell at band edge (~3 cells away → 6m, the band radius).
            costMul[12, 9].Should().BeApproximately(1.0, 0.05,
                $"cell at band edge should be ~no discount; got {costMul[12,9]:F2}");
            // Cell well outside the band → no discount.
            costMul[12, 0].Should().BeApproximately(1.0, 1e-9,
                $"cell far from line should be 1.0; got {costMul[12,0]:F2}");
        }

        [Fact]
        public void Build_ZonePolygon_DiscountsInteriorCells()
        {
            // Square zone covering the upper half of the grid.
            var (w, env, cs, nx, ny) = OpenSquare();
            var guides = new GuideDiscovery.DiscoveredGuides
            {
                Zones = { new List<Pt2> {
                    new(5, 30), new(45, 30), new(45, 45), new(5, 45),
                } },
            };
            var costMul = GuideRasterizer.Build(guides, w, env, cs,
                zoneStrength: 0.30);

            // Cell inside zone → costMul = 1 - 0.3 = 0.7.
            costMul[12, 18].Should().BeApproximately(0.7, 1e-6,
                $"cell inside zone should be 0.7; got {costMul[12,18]:F3}");
            // Cell outside zone → 1.0.
            costMul[12, 5].Should().BeApproximately(1.0, 1e-9,
                $"cell outside zone should be 1.0; got {costMul[12,5]:F3}");
        }

        [Fact]
        public void Build_GuideOverridesZone_WhereTheyOverlap()
        {
            // Zone over the entire grid; a centerline runs through it.
            // Guide gives stronger discount than zone, so cells near the
            // centerline should hit the guide's lower cost rather than the
            // zone's flat 0.7.
            var (w, env, cs, nx, ny) = OpenSquare();
            var guides = new GuideDiscovery.DiscoveredGuides
            {
                Centerlines = { new List<Pt2> { new(0, 25), new(50, 25) } },
                Zones = { new List<Pt2> {
                    new(0, 0), new(50, 0), new(50, 50), new(0, 50),
                } },
            };
            var costMul = GuideRasterizer.Build(guides, w, env, cs,
                guideStrength: 0.85, zoneStrength: 0.30);

            costMul[12, 12].Should().BeApproximately(0.15, 0.05,
                "cell on guide should hit guide cost (0.15), not zone cost (0.7)");
            costMul[12, 5].Should().BeApproximately(0.7, 1e-6,
                "cell inside zone but far from guide should hit zone cost");
        }

        [Fact]
        public void Build_NonWalkableCells_StayAtOne()
        {
            // Carve out a non-walkable region and verify it isn't discounted.
            var (w, env, cs, nx, ny) = OpenSquare();
            for (int x = 0; x < nx; x++) w[x, 5] = false;   // strip at y=5

            var guides = new GuideDiscovery.DiscoveredGuides
            {
                Centerlines = { new List<Pt2> { new(0, 11), new(50, 11) } },
                Zones = { new List<Pt2> {
                    new(0, 0), new(50, 0), new(50, 50), new(0, 50),
                } },
            };
            var costMul = GuideRasterizer.Build(guides, w, env, cs);

            for (int x = 0; x < nx; x++)
                costMul[x, 5].Should().BeApproximately(1.0, 1e-9,
                    $"non-walkable cell ({x},5) must stay at 1.0");
        }

        [Fact]
        public void CombineMin_LayersTwoFields()
        {
            var a = new double[2, 2] { { 0.5, 1.0 }, { 1.0, 0.7 } };
            var b = new double[2, 2] { { 0.9, 0.4 }, { 0.6, 0.8 } };

            var combined = GuideRasterizer.CombineMin(a, b);

            combined!.GetLength(0).Should().Be(2);
            combined[0, 0].Should().Be(0.5);
            combined[0, 1].Should().Be(0.4);
            combined[1, 0].Should().Be(0.6);
            combined[1, 1].Should().Be(0.7);
        }

        [Fact]
        public void CombineMin_NullInput_ReturnsOther()
        {
            var b = new double[1, 1] { { 0.3 } };
            GuideRasterizer.CombineMin(null, b).Should().BeSameAs(b);
            GuideRasterizer.CombineMin(b, null).Should().BeSameAs(b);
            GuideRasterizer.CombineMin(null, null).Should().BeNull();
        }
    }
}
