using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using Xunit;

using Rect = MahodAI.Civil3D.Plugin.Tools.Creation.DrawingTablePlacement.Rect;

namespace MahodAI.Core.Tests
{
    /// <summary>
    /// Report-table placement: the engineer's rule is "line the table up with the rest of the
    /// deliverable" — right of the drawing content, centred on its vertical band, never flung
    /// kilometres away by a stray entity (2026-07-27).
    /// </summary>
    public class DrawingTablePlacementTests
    {
        private static readonly Rect[] NoTables = System.Array.Empty<Rect>();

        [Fact]
        public void Places_table_right_of_content_and_vertically_centred()
        {
            var content = new[]
            {
                new Rect(0, 0, 1000, 200),      // plan
                new Rect(1200, -100, 2000, 300) // profile view
            };

            var p = DrawingTablePlacement.Choose(content, NoTables, tableWidth: 300, tableHeight: 80);

            p.Resolved.Should().BeTrue();
            p.Method.Should().Be("right_of_content");
            p.X.Should().BeGreaterThan(2000);                 // clear of everything
            // Content band is Y -100..300 → centre 100; top-left sits half a table above it.
            p.Y.Should().BeApproximately(100 + 40, 1e-6);
        }

        [Fact]
        public void Margin_scales_with_the_drawing_but_stays_sane()
        {
            var small = DrawingTablePlacement.Choose(
                new[] { new Rect(0, 0, 100, 50) }, NoTables, 300, 80);
            var large = DrawingTablePlacement.Choose(
                new[] { new Rect(0, 0, 200000, 50) }, NoTables, 300, 80);

            (small.X - 100).Should().BeGreaterOrEqualTo(25);       // floor
            (large.X - 200000).Should().BeLessOrEqualTo(2000);     // cap
        }

        [Fact]
        public void Stacks_below_a_table_already_in_the_column()
        {
            var content = new[] { new Rect(0, 0, 1000, 200) };
            var first = DrawingTablePlacement.Choose(content, NoTables, 300, 80);

            var existing = new[] { new Rect(first.X, first.Y - 80, first.X + 300, first.Y) };
            var second = DrawingTablePlacement.Choose(content, existing, 300, 80);

            second.X.Should().BeApproximately(first.X, 1e-6);
            second.Y.Should().BeLessThan(existing[0].MinY);   // pushed below the sitting table
            new Rect(second.X, second.Y - 80, second.X + 300, second.Y)
                .Intersects(existing[0]).Should().BeFalse();
        }

        [Fact]
        public void Levels_with_the_deliverable_band_not_the_whole_drawing()
        {
            // The plan sits far above the profile view; centring on everything parks the table
            // in empty space above the deliverables — the engineer's "not on the same level".
            var plan = new Rect(0, 5000, 1000, 5200);
            var profileView = new Rect(1200, 0, 2000, 400);
            var content = new[] { plan, profileView };

            var p = DrawingTablePlacement.Choose(
                content, NoTables, 300, 80, bandExtents: new[] { profileView });

            p.Y.Should().BeApproximately(200 + 40, 1e-6);   // centred on the profile view
            p.X.Should().BeGreaterThan(2000);               // still clear of everything
        }

        [Fact]
        public void Reports_unresolved_when_the_drawing_has_no_content()
        {
            var p = DrawingTablePlacement.Choose(System.Array.Empty<Rect>(), NoTables, 300, 80);

            p.Resolved.Should().BeFalse();
            p.Method.Should().Be("none");
        }

        [Fact]
        public void Keep_region_admits_the_survey_and_rejects_a_far_away_origin_block()
        {
            // Israeli ITM: the road sits ~200 km from the WCS origin.
            var road = new Rect(200000, 650000, 200800, 650300);
            var keep = DrawingTablePlacement.KeepRegion(road);

            keep.Intersects(new Rect(199000, 649000, 202000, 651000)).Should().BeTrue();   // survey
            keep.Intersects(new Rect(-1, -1, 1, 1)).Should().BeFalse();                    // origin block
        }

        [Fact]
        public void A_far_away_outlier_kept_in_the_scan_would_move_the_table_far_right()
        {
            // Guards the reason KeepRegion exists: the outlier must be filtered BEFORE Choose,
            // otherwise the column lands past it (the bug the engineer reported).
            var withOutlier = new[] { new Rect(200000, 650000, 200800, 650300), new Rect(0, 0, 1, 1) };
            var clean = new[] { new Rect(200000, 650000, 200800, 650300) };

            var polluted = DrawingTablePlacement.Choose(withOutlier, NoTables, 300, 80);
            var good = DrawingTablePlacement.Choose(clean, NoTables, 300, 80);

            (good.Y - polluted.Y).Should().BeGreaterThan(100000);
        }
    }
}
