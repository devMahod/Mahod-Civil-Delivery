using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Creation
{
    /// <summary>
    /// Pure-logic tests for the profile view placement rectangle selection: the chosen
    /// origin must sit clear of ALL modelspace extents (alignments, tables, blocks —
    /// not just existing profile views), honouring the PV stacking convention.
    /// </summary>
    public class ProfileViewPlacementTests
    {
        private static ProfileViewPlacement.Rect R(double minX, double minY, double maxX, double maxY)
            => new(minX, minY, maxX, maxY);

        [Fact]
        public void Choose_NoEntities_FallsBackRelativeToAnchor()
        {
            var placement = ProfileViewPlacement.Choose(
                new List<ProfileViewPlacement.Rect>(),
                new List<ProfileViewPlacement.Rect>(),
                anchorX: 1000, anchorY: 2000,
                estimatedWidth: 500, estimatedHeight: 200);

            placement.Method.Should().Be("no_entities_fallback");
            placement.OriginX.Should().Be(1000);
            placement.OriginY.Should().BeLessThan(2000);
            placement.Warnings.Should().NotBeEmpty();
        }

        [Fact]
        public void Choose_PlacesBelowAllEntities_NotJustProfileViews()
        {
            // An alignment up top and a TABLE far below it — the old logic only looked at
            // ProfileViews and would have landed on the table.
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 800, 1000, 1000),    // alignment area
                R(200, -300, 600, -100),  // a table below the alignment
            };
            var pvs = new List<ProfileViewPlacement.Rect>();

            var placement = ProfileViewPlacement.Choose(
                all, pvs, anchorX: 0, anchorY: 900,
                estimatedWidth: 500, estimatedHeight: 200);

            // Must be below the TABLE's bottom (-300), not just below the alignment.
            placement.OriginY.Should().BeLessThan(-300);

            // The chosen rectangle must not intersect anything.
            var candidate = R(placement.OriginX, placement.OriginY,
                              placement.OriginX + 500, placement.OriginY + 200);
            foreach (var r in all)
                candidate.Intersects(r).Should().BeFalse(
                    $"candidate {candidate} must not collide with {r}");
        }

        [Fact]
        public void Choose_WithExistingProfileViews_StacksBelowLowestEdge()
        {
            // Existing PV bottoms at -600 (the drawing's lowest edge). The new PV's TOP is
            // placed one clearance below that, then its full height extends downward.
            var pv = R(0, -600, 800, -200);
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 0, 1000, 500),   // drawing content
                pv,
            };
            var pvs = new List<ProfileViewPlacement.Rect> { pv };

            var placement = ProfileViewPlacement.Choose(
                all, pvs, anchorX: 0, anchorY: 0,
                estimatedWidth: 800, estimatedHeight: 200, marginM: 50);

            placement.Method.Should().Be("below");
            // top = minY(-600) - clearance(50) = -650; origin = top - estHeight(200) = -850.
            placement.OriginY.Should().BeApproximately(-850, 1e-9);

            // And it must clear the existing PV.
            var candidate = R(placement.OriginX, placement.OriginY,
                              placement.OriginX + 800, placement.OriginY + 200);
            candidate.Intersects(pv).Should().BeFalse();
        }

        [Fact]
        public void Choose_CandidateRectangleIsAlwaysCollisionFree()
        {
            // Estimated height smaller than a giant existing PV — the defensive loop
            // must still deliver a collision-free rectangle.
            var giantPv = R(-100, -2000, 900, -100);
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 0, 1000, 1000),
                giantPv,
            };
            var pvs = new List<ProfileViewPlacement.Rect> { giantPv };

            var placement = ProfileViewPlacement.Choose(
                all, pvs, anchorX: 100, anchorY: 0,
                estimatedWidth: 700, estimatedHeight: 300);

            var candidate = R(placement.OriginX, placement.OriginY,
                              placement.OriginX + 700, placement.OriginY + 300);
            foreach (var r in all)
                candidate.Intersects(r).Should().BeFalse();
        }

        [Fact]
        public void Rect_Intersects_DetectsOverlapAndSeparation()
        {
            R(0, 0, 10, 10).Intersects(R(5, 5, 15, 15)).Should().BeTrue();
            R(0, 0, 10, 10).Intersects(R(20, 20, 30, 30)).Should().BeFalse();
            // Touching edges count as intersecting (conservative for placement).
            R(0, 0, 10, 10).Intersects(R(10, 0, 20, 10)).Should().BeTrue();
        }

        [Fact]
        public void Choose_NormalCase_PlacesBelowDrawing()
        {
            // A single drawing block with clear space beneath it — the conventional
            // "below" strategy applies and is reported as such.
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 0, 1000, 500),
            };

            var placement = ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 800, estimatedHeight: 200);

            placement.Method.Should().Be("below");
            placement.OriginY.Should().BeLessThan(0);
        }

        /// <summary>
        /// Builds a wide band of <paramref name="count"/> stacked obstacle rectangles sitting
        /// entirely below Y=<paramref name="topY"/> (the alignment reference), each spanning
        /// [minX,maxX] horizontally. Together they form a deep, wide band under the drawing.
        /// </summary>
        private static List<ProfileViewPlacement.Rect> Band(
            double minX, double maxX, double topY, double bandHeight, int count)
        {
            var band = new List<ProfileViewPlacement.Rect>();
            double y = topY;
            for (int i = 0; i < count; i++)
            {
                band.Add(R(minX, y - bandHeight, maxX, y));
                y -= bandHeight;
            }
            return band;
        }

        [Fact]
        public void Choose_WideBandBelowDrawing_OffsetsToRightOfAllGeometry()
        {
            // A wide band of obstacles (3 stacked figures) fills the region under the drawing,
            // below the alignment reference line. A "below" placement would be detached far
            // beneath them, so placement must escape sideways to a column to the RIGHT of
            // every entity.
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 0, 2000, 500), // drawing content (alignment band sits above the reference)
            };
            // Band of 3 obstacles, each 400 deep, starting just below the reference (Y=0).
            all.AddRange(Band(minX: -500, maxX: 2500, topY: 0, bandHeight: 400, count: 3));

            var placement = ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 800, estimatedHeight: 200, marginM: 50);

            placement.Method.Should().Be("right_of_drawing");

            // Left edge of the new PV must start past the right edge of all geometry (maxX=2500).
            placement.OriginX.Should().BeGreaterThan(2500);

            // And the rectangle must be collision-free against everything.
            var candidate = R(placement.OriginX, placement.OriginY,
                              placement.OriginX + 800, placement.OriginY + 200);
            foreach (var r in all)
                candidate.Intersects(r).Should().BeFalse(
                    $"candidate {candidate} must not collide with {r}");

            placement.Warnings.Should().Contain(w => w.Contains("right of all geometry"));
        }

        [Fact]
        public void Choose_RightOffsetMargin_IsAdaptiveToDrawingWidth()
        {
            // margin = max(200, (maxX-minX)*0.15). The widest extent across ALL entities here
            // is the 10,000-wide drawing/band horizontal span, so the adaptive term (1500)
            // dominates the 200 floor.
            const double minX = 0, maxX = 10000;
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(minX, 0, maxX, 500), // very wide drawing
            };
            all.AddRange(Band(minX, maxX, topY: 0, bandHeight: 400, count: 3));

            var placement = ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 800, estimatedHeight: 200, marginM: 50);

            placement.Method.Should().Be("right_of_drawing");

            double expectedMargin = (maxX - minX) * 0.15; // 1500, well above the 200 floor
            expectedMargin.Should().BeGreaterThan(200);
            // Origin X is exactly maxX + adaptive margin.
            placement.OriginX.Should().BeApproximately(maxX + expectedMargin, 1e-9);
        }

        [Fact]
        public void Choose_RightOffsetMargin_IsCappedSoAStrayOutlierCannotFlingThePvAway()
        {
            // A huge measured width (e.g. a stray entity at the WCS origin while the survey sits in
            // Israeli ITM coords ~230 km away) would make 15% = ~34 km, flinging the PV off the
            // drawing. The cap keeps the gap sane (≤ 5000) so the PV stays just to the right.
            const double minX = 0, maxX = 230000; // 230 km wide => 0.15*width = 34.5 km, uncapped
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(minX, 0, maxX, 500),
            };
            all.AddRange(Band(minX, maxX, topY: 0, bandHeight: 400, count: 3));

            var placement = ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 800, estimatedHeight: 200, marginM: 50);

            placement.Method.Should().Be("right_of_drawing");
            // Gap is capped at 5000 rather than 34,500 — the PV sits just past the right edge.
            placement.OriginX.Should().BeApproximately(maxX + 5000.0, 1e-9);
        }

        [Fact]
        public void Choose_RightOffset_NarrowDrawing_UsesMarginFloor()
        {
            // Narrow drawing: (maxX-minX)*0.15 = 15 < 200, so the 200 floor applies.
            const double minX = 0, maxX = 100;
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(minX, 0, maxX, 500),
            };
            all.AddRange(Band(minX, maxX, topY: 0, bandHeight: 400, count: 3));

            var placement = ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 80, estimatedHeight: 200, marginM: 50);

            placement.Method.Should().Be("right_of_drawing");
            placement.OriginX.Should().BeApproximately(maxX + 200.0, 1e-9);
        }

        [Fact]
        public void Choose_ShallowBandBelowDrawing_StaysBelow()
        {
            // Only ONE obstacle below the reference (not a "band") — below-placement is still
            // perfectly reasonable, so we must NOT escape to the right.
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 0, 2000, 500),
                R(-500, -300, 2500, -10), // single shallow obstacle below
            };

            var placement = ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 800, estimatedHeight: 200, marginM: 50);

            placement.Method.Should().Be("below");
        }

        [Fact]
        public void Choose_RightOffsetResult_IsDeterministicAndNonOverlapping()
        {
            // Determinism: identical inputs must yield byte-identical placement, and the
            // result must never overlap any entity — including one already to the right.
            var all = new List<ProfileViewPlacement.Rect>
            {
                R(0, 0, 3000, 800),
                R(3400, -1000, 4000, 1000), // an entity already to the right
            };
            all.AddRange(Band(minX: -200, maxX: 3200, topY: 0, bandHeight: 500, count: 4));

            ProfileViewPlacement.Placement First() => ProfileViewPlacement.Choose(
                all, new List<ProfileViewPlacement.Rect>(),
                anchorX: 0, anchorY: 0,
                estimatedWidth: 600, estimatedHeight: 300, marginM: 50);

            var a = First();
            var b = First();

            a.Method.Should().Be("right_of_drawing");
            a.OriginX.Should().Be(b.OriginX);
            a.OriginY.Should().Be(b.OriginY);

            var candidate = R(a.OriginX, a.OriginY,
                              a.OriginX + 600, a.OriginY + 300);
            foreach (var r in all)
                candidate.Intersects(r).Should().BeFalse(
                    $"deterministic placement {candidate} must clear {r}");
        }
    }
}
