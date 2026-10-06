using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// The mask pinhole fill must close rasterisation artifacts WITHOUT ever bridging a genuine
    /// void. Field case (road 73, 2026-07-28): 489 needle-thin triangles dropped by the sliver
    /// filter became single unwalkable cells under the existing road at the auto-scaled 7.7 m
    /// cell size — every tangent-fit chord through one was rejected and the containment gate
    /// called them "no ground data", rolling back an axis that sat on perfectly good TIN.
    /// </summary>
    public class MaskHoleFillTests
    {
        private static bool[,] Solid(int nx, int ny)
        {
            var m = new bool[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    m[x, y] = true;
            return m;
        }

        [Fact]
        public void Single_cell_pinhole_is_closed()
        {
            var mask = Solid(9, 9);
            mask[4, 4] = false;

            int filled = BuildableRegion.FillIsolatedHoles(mask, 9, 9);

            filled.Should().Be(1);
            mask[4, 4].Should().BeTrue();
        }

        [Fact]
        public void Two_cell_crack_is_closed()
        {
            var mask = Solid(9, 9);
            mask[4, 4] = false;
            mask[5, 4] = false;   // each cell still sees 7 walkable neighbours

            int filled = BuildableRegion.FillIsolatedHoles(mask, 9, 9);

            filled.Should().Be(2);
            mask[4, 4].Should().BeTrue();
            mask[5, 4].Should().BeTrue();
        }

        [Fact]
        public void A_real_void_is_never_bridged()
        {
            // 3×3 void: every cell sees at most 5 walkable neighbours → survives every pass.
            // This is the shape a genuine off-surface pocket takes, and the containment gate
            // depends on it staying a hole.
            var mask = Solid(11, 11);
            for (int x = 4; x <= 6; x++)
                for (int y = 4; y <= 6; y++)
                    mask[x, y] = false;

            int filled = BuildableRegion.FillIsolatedHoles(mask, 11, 11);

            filled.Should().Be(0);
            mask[5, 5].Should().BeFalse("the centre of a real void must stay unwalkable");
        }

        [Fact]
        public void A_concave_bay_open_to_the_outside_is_not_filled()
        {
            // The bay the sliver filter legitimately opens: a 2-wide channel cutting in from the
            // edge. Its cells have too few walkable neighbours, and the outside beyond the mask
            // counts as non-walkable, so nothing closes.
            var mask = Solid(12, 12);
            for (int x = 0; x <= 6; x++)
            {
                mask[x, 5] = false;
                mask[x, 6] = false;
            }

            int filled = BuildableRegion.FillIsolatedHoles(mask, 12, 12);

            filled.Should().Be(0);
            mask[3, 5].Should().BeFalse();
        }

        [Fact]
        public void Fully_walkable_mask_is_untouched()
        {
            var mask = Solid(6, 6);

            BuildableRegion.FillIsolatedHoles(mask, 6, 6).Should().Be(0);
        }

        [Fact]
        public void Diagonal_pinhole_pair_closes_in_two_passes()
        {
            // Two pinholes that touch diagonally: each sees 6 walkable neighbours, so both
            // close — still bounded, since neither grows the mask outward.
            var mask = Solid(10, 10);
            mask[4, 4] = false;
            mask[5, 5] = false;

            int filled = BuildableRegion.FillIsolatedHoles(mask, 10, 10);

            filled.Should().Be(2);
            mask[4, 4].Should().BeTrue();
            mask[5, 5].Should().BeTrue();
        }
    }
}
