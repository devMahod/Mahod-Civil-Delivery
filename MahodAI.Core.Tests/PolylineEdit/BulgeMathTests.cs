using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using MahodAI.Civil3D.Plugin.Tools.PolylineEdit;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.PolylineEdit
{
    /// <summary>
    /// Arc/bulge math for the polyline vertex tools. These are the values every vertex
    /// add/delete/reverse depends on, so they are pinned against hand-computed geometry rather
    /// than against the implementation.
    /// </summary>
    public class BulgeMathTests
    {
        private static readonly Pt2 A = new(0, 0);
        private static readonly Pt2 B = new(1, 0);

        [Fact]
        public void Bulge_one_is_a_ccw_semicircle_bulging_right_of_travel()
        {
            BulgeMath.BulgeToDelta(1.0).Should().BeApproximately(Math.PI, 1e-12);
            BulgeMath.Radius(A, B, 1.0).Should().BeApproximately(0.5, 1e-12);

            var center = BulgeMath.Center(A, B, 1.0);
            center.X.Should().BeApproximately(0.5, 1e-12);
            center.Y.Should().BeApproximately(0.0, 1e-12);

            // Mid-arc sits BELOW the chord: a positive bulge bulges to the right of a→b.
            var mid = BulgeMath.PointOnSegment(A, B, 1.0, 0.5);
            mid.X.Should().BeApproximately(0.5, 1e-12);
            mid.Y.Should().BeApproximately(-0.5, 1e-12);
        }

        [Fact]
        public void Quarter_circle_matches_hand_computed_radius_and_sagitta()
        {
            double bulge = Math.Tan(Math.PI / 8.0);          // Δ = 90°

            BulgeMath.Radius(A, B, bulge).Should().BeApproximately(0.70710678, 1e-7);
            BulgeMath.Sagitta(A, B, bulge).Should().BeApproximately(0.20710678, 1e-7);
            BulgeMath.Center(A, B, bulge).Y.Should().BeApproximately(0.5, 1e-9);
            BulgeMath.SegmentLength(A, B, bulge)
                .Should().BeApproximately(0.70710678 * Math.PI / 2.0, 1e-7);
        }

        [Fact]
        public void Delta_and_bulge_round_trip()
        {
            foreach (double deg in new[] { -350.0, -180.0, -37.5, -1.0, 1.0, 45.0, 180.0, 350.0 })
            {
                double delta = deg * Math.PI / 180.0;
                BulgeMath.BulgeToDelta(BulgeMath.DeltaToBulge(delta))
                    .Should().BeApproximately(delta, 1e-9, $"Δ={deg}°");
            }
        }

        [Fact]
        public void Splitting_a_bulge_reproduces_the_same_arc()
        {
            const double bulge = 1.0;                        // semicircle
            var split = BulgeMath.PointOnSegment(A, B, bulge, 0.3);

            BulgeMath.SplitBulge(bulge, 0.3, out double b1, out double b2);

            // Each half is still an arc of the SAME radius as the original.
            BulgeMath.Radius(A, split, b1).Should().BeApproximately(0.5, 1e-9);
            BulgeMath.Radius(split, B, b2).Should().BeApproximately(0.5, 1e-9);

            // And the two sweeps add back up to the original.
            (BulgeMath.BulgeToDelta(b1) + BulgeMath.BulgeToDelta(b2))
                .Should().BeApproximately(BulgeMath.BulgeToDelta(bulge), 1e-9);

            // Walking the first half then the second lands on the original mid-arc point.
            var viaHalves = BulgeMath.PointOnSegment(split, B, b2, 0.5);
            var direct = BulgeMath.PointOnSegment(A, B, bulge, 0.3 + 0.35);
            viaHalves.DistanceTo(direct).Should().BeLessThan(1e-9);
        }

        [Fact]
        public void Splitting_a_straight_yields_two_straights()
        {
            BulgeMath.SplitBulge(0.0, 0.5, out double b1, out double b2);
            b1.Should().Be(0.0);
            b2.Should().Be(0.0);
        }

        [Fact]
        public void Bulge_from_radius_respects_the_chord_and_refuses_the_impossible()
        {
            // Chord 1.0 with R = 0.5 is exactly a semicircle.
            BulgeMath.BulgeFromRadius(A, B, 0.5, clockwise: false)
                .Should().BeApproximately(1.0, 1e-9);
            BulgeMath.BulgeFromRadius(A, B, 0.5, clockwise: true)
                .Should().BeApproximately(-1.0, 1e-9);

            // A bigger radius gives a flatter (minor) arc.
            double flat = BulgeMath.BulgeFromRadius(A, B, 5.0, clockwise: false)!.Value;
            flat.Should().BeInRange(0.0, 0.2);
            BulgeMath.Radius(A, B, flat).Should().BeApproximately(5.0, 1e-7);

            // The major arc of the same radius is the long way round.
            double major = BulgeMath.BulgeFromRadius(A, B, 5.0, clockwise: false, major: true)!.Value;
            BulgeMath.Radius(A, B, major).Should().BeApproximately(5.0, 1e-7);
            Math.Abs(BulgeMath.BulgeToDelta(major)).Should().BeGreaterThan(Math.PI);

            // Half the chord is the smallest radius that can span it.
            BulgeMath.BulgeFromRadius(A, B, 0.4, clockwise: false).Should().BeNull();
            BulgeMath.BulgeFromRadius(A, B, 0.0, clockwise: false).Should().BeNull();
        }

        [Fact]
        public void Closest_point_on_an_arc_projects_onto_the_arc_not_the_chord()
        {
            // Semicircle below the chord; a pick under the middle must land on the arc.
            var hit = BulgeMath.ClosestPointOnSegment(A, B, 1.0, new Pt2(0.5, -2.0), out double t);

            t.Should().BeApproximately(0.5, 1e-9);
            hit.Y.Should().BeApproximately(-0.5, 1e-9);
        }

        [Fact]
        public void Closest_point_outside_the_sweep_clamps_to_the_nearer_end()
        {
            // A pick ABOVE the chord is outside the (downward) semicircle's sweep entirely.
            // Naive angle arithmetic reads "just behind the start" as "almost at the end".
            BulgeMath.ClosestPointOnSegment(A, B, 1.0, new Pt2(-0.2, 0.5), out double tStart);
            tStart.Should().Be(0.0);

            BulgeMath.ClosestPointOnSegment(A, B, 1.0, new Pt2(1.2, 0.5), out double tEnd);
            tEnd.Should().Be(1.0);
        }

        [Fact]
        public void Closest_point_handles_a_clockwise_arc()
        {
            var hit = BulgeMath.ClosestPointOnSegment(A, B, -1.0, new Pt2(0.5, 2.0), out double t);

            t.Should().BeApproximately(0.5, 1e-9);
            hit.Y.Should().BeApproximately(0.5, 1e-9);       // CW bulges left of travel
        }

        [Theory]
        // mode, value, expected number of straights for a 90° arc of R≈0.7071 (length ≈1.111)
        [InlineData("Count", 5.0, 5)]
        [InlineData("SegmentLength", 0.3, 4)]          // 1.1107 / 0.3 → 4
        [InlineData("ChordLength", 0.25, 5)]           // δ=2·asin(0.25/1.4142)=0.3546 → 1.5708/0.3546 → 5
        [InlineData("ChordDeviation", 0.01, 5)]        // δ=2·acos(1−0.01/0.7071)=0.3374 → 5
        public void Flatten_segment_count_follows_the_requested_mode(string mode, double value, int expected)
        {
            // Enum values inside [InlineData] silently abort sibling test discovery in xUnit —
            // the mode travels as a string and is mapped here (standing repo trap).
            var flattenMode = mode switch
            {
                "Count" => BulgeMath.FlattenMode.Count,
                "SegmentLength" => BulgeMath.FlattenMode.SegmentLength,
                "ChordLength" => BulgeMath.FlattenMode.ChordLength,
                "ChordDeviation" => BulgeMath.FlattenMode.ChordDeviation,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
            double bulge = Math.Tan(Math.PI / 8.0);

            BulgeMath.FlattenSegmentCount(A, B, bulge, flattenMode, value).Should().Be(expected);
        }

        [Fact]
        public void Flatten_never_returns_less_than_one_segment_and_ignores_straights()
        {
            BulgeMath.FlattenSegmentCount(A, B, 0.0, BulgeMath.FlattenMode.Count, 10.0).Should().Be(1);
            BulgeMath.FlattenSegmentCount(A, B, 1.0, BulgeMath.FlattenMode.SegmentLength, 0.0).Should().Be(1);
            BulgeMath.FlattenSegmentCount(A, B, 1.0, BulgeMath.FlattenMode.ChordLength, 99.0).Should().Be(1);
        }

        [Fact]
        public void Flatten_interior_points_lie_on_the_arc()
        {
            var pts = BulgeMath.FlattenArcInteriorPoints(A, B, 1.0, 4);

            pts.Should().HaveCount(3);
            var center = BulgeMath.Center(A, B, 1.0);
            foreach (var p in pts)
                p.DistanceTo(center).Should().BeApproximately(0.5, 1e-9);
        }

        [Fact]
        public void Two_semicircle_segments_have_the_area_of_a_circle()
        {
            // The chord polygon of a circle drawn as two bulge-1 segments has zero area, so the
            // circular-segment term is the only thing that can report an orientation at all.
            double half = BulgeMath.SignedSegmentArea(A, B, 1.0);

            (2.0 * half).Should().BeApproximately(Math.PI * 0.25, 1e-9);
            BulgeMath.SignedSegmentArea(A, B, -1.0).Should().BeApproximately(-half, 1e-12);
        }

        [Fact]
        public void Distance_helpers_measure_line_and_segment_separately()
        {
            // Beyond the segment's end: the LINE distance is the perpendicular, the SEGMENT
            // distance is to the endpoint.
            BulgeMath.DistanceToLine(A, B, new Pt2(3, 2)).Should().BeApproximately(2.0, 1e-12);
            BulgeMath.DistanceToSegment(A, B, new Pt2(3, 2))
                .Should().BeApproximately(Math.Sqrt(4.0 + 4.0), 1e-12);
        }

        [Fact]
        public void Deflection_is_zero_on_a_straight_and_a_right_angle_on_a_corner()
        {
            BulgeMath.Deflection(new Pt2(0, 0), new Pt2(1, 0), new Pt2(2, 0))
                .Should().BeApproximately(0.0, 1e-12);
            BulgeMath.Deflection(new Pt2(0, 0), new Pt2(1, 0), new Pt2(1, 1))
                .Should().BeApproximately(Math.PI / 2.0, 1e-12);
            // Coincident points cannot define a turn.
            BulgeMath.Deflection(new Pt2(0, 0), new Pt2(0, 0), new Pt2(1, 1)).Should().Be(0.0);
        }
    }
}
