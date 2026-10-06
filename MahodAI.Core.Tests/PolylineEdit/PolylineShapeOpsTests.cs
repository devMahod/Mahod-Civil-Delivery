using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using MahodAI.Civil3D.Plugin.Tools.PolylineEdit;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.PolylineEdit
{
    /// <summary>
    /// Whole-shape operations: reverse / CW / CCW / start vertex (PLTOOLS ENTREV, ENTREVS, PL-CW,
    /// PL-CCW, PL-Vx1), per-segment edits (PL-L2A, PL-A2L, PL-NOARC, PL-SgWidth, PL-SgInfo) and the
    /// construction commands (MPL, R3P, PL-P90).
    /// </summary>
    public class PolylineShapeOpsTests
    {
        private static PlShape Open(params (double X, double Y)[] pts) =>
            new(pts.Select(p => PlVertex.At(p.X, p.Y)));

        private static PlShape ClosedShape(params (double X, double Y)[] pts) =>
            new(pts.Select(p => PlVertex.At(p.X, p.Y)), closed: true);

        /// <summary>Dense sample of the whole shape — the ground truth for "same geometry".</summary>
        private static List<Pt2> Sample(PlShape s, int perSegment = 8)
        {
            var pts = new List<Pt2>();
            for (int i = 0; i < s.SegmentCount; i++)
            {
                var a = s.SegmentStart(i).P;
                var b = s.SegmentEnd(i).P;
                double bulge = s.SegmentBulge(i);
                for (int k = 0; k <= perSegment; k++)
                    pts.Add(BulgeMath.PointOnSegment(a, b, bulge, (double)k / perSegment));
            }
            return pts;
        }

        // ── orientation ──────────────────────────────────────────────

        [Fact]
        public void Signed_area_is_positive_counter_clockwise_and_negative_clockwise()
        {
            PolylineOrientation.SignedArea(ClosedShape((0, 0), (10, 0), (10, 10), (0, 10)))
                .Should().BeApproximately(100.0, 1e-9);
            PolylineOrientation.SignedArea(ClosedShape((0, 0), (0, 10), (10, 10), (10, 0)))
                .Should().BeApproximately(-100.0, 1e-9);
        }

        [Fact]
        public void A_circle_drawn_as_two_bulge_segments_still_has_an_orientation()
        {
            // Chord area is exactly zero here; only the arc term can decide.
            var circle = new PlShape(new[]
            {
                new PlVertex(0, 0, 0, 1.0),
                new PlVertex(1, 0, 0, 1.0),
            }, closed: true);

            PolylineOrientation.SignedArea(circle).Should().BeApproximately(Math.PI * 0.25, 1e-9);
            PolylineOrientation.IsClockwise(circle).Should().BeFalse();
        }

        [Fact]
        public void Reversing_an_open_polyline_swaps_the_ends_and_keeps_the_geometry()
        {
            // Straight, arc, straight — with a width taper on the arc.
            var shape = new PlShape(new[]
            {
                PlVertex.At(0, 0),
                new PlVertex(10, 0, 0, 0.5, 1.0, 2.0),
                PlVertex.At(20, 5),
                PlVertex.At(30, 5),
            });
            var before = Sample(shape);

            var result = PolylineOrientation.Reverse(shape);

            result.Changed.Should().BeTrue();
            shape.Vertices[0].P.Should().Be(new Pt2(30, 5));
            shape.Vertices[^1].P.Should().Be(new Pt2(0, 0));

            // The arc moved to the segment that now spans the same two points, negated.
            shape.Vertices[1].Bulge.Should().BeApproximately(-0.5, 1e-12);
            shape.Vertices[1].StartWidth.Should().Be(2.0);
            shape.Vertices[1].EndWidth.Should().Be(1.0);

            // Same line on the ground, walked the other way.
            var after = Sample(shape);
            after.Reverse();
            for (int i = 0; i < before.Count; i++)
                before[i].DistanceTo(after[i]).Should().BeLessThan(1e-9, $"sample {i}");
        }

        [Fact]
        public void Reversing_twice_returns_the_original()
        {
            var shape = new PlShape(new[]
            {
                new PlVertex(0, 0, 0, 0.3, 0.5, 1.5),
                new PlVertex(10, 2, 0, -0.7, 1.5, 0.5),
                PlVertex.At(20, 0),
            });
            var original = shape.Vertices.ToList();

            PolylineOrientation.Reverse(shape);
            PolylineOrientation.Reverse(shape);

            for (int i = 0; i < original.Count; i++)
            {
                shape.Vertices[i].P.Should().Be(original[i].P);
                shape.Vertices[i].Bulge.Should().BeApproximately(original[i].Bulge, 1e-12);
                shape.Vertices[i].StartWidth.Should().Be(original[i].StartWidth);
                shape.Vertices[i].EndWidth.Should().Be(original[i].EndWidth);
            }
        }

        [Fact]
        public void Reversing_a_closed_polyline_keeps_its_start_vertex_and_its_geometry()
        {
            var shape = new PlShape(new[]
            {
                PlVertex.At(0, 0),
                new PlVertex(10, 0, 0, 0.4),
                PlVertex.At(10, 10),
                PlVertex.At(0, 10),
            }, closed: true);
            double areaBefore = PolylineOrientation.SignedArea(shape);

            PolylineOrientation.Reverse(shape);

            shape.Vertices[0].P.Should().Be(new Pt2(0, 0), "the start vertex stays put");
            shape.Vertices[1].P.Should().Be(new Pt2(0, 10), "travel direction flipped");
            PolylineOrientation.SignedArea(shape).Should().BeApproximately(-areaBefore, 1e-9);
        }

        [Fact]
        public void Forcing_an_orientation_is_a_no_op_when_it_already_matches()
        {
            var ccw = ClosedShape((0, 0), (10, 0), (10, 10), (0, 10));

            var noop = PolylineOrientation.SetOrientation(ccw, clockwise: false);
            noop.Changed.Should().BeFalse();
            noop.Notes.Should().ContainMatch("*already counter-clockwise*");

            var flipped = PolylineOrientation.SetOrientation(ccw, clockwise: true);
            flipped.Changed.Should().BeTrue();
            PolylineOrientation.IsClockwise(ccw).Should().BeTrue();
        }

        [Fact]
        public void Setting_the_start_vertex_rotates_a_closed_polyline_and_refuses_an_open_one()
        {
            var closed = new PlShape(new[]
            {
                PlVertex.At(0, 0),
                new PlVertex(10, 0, 0, 0.25),
                PlVertex.At(10, 10),
                PlVertex.At(0, 10),
            }, closed: true);

            var result = PolylineOrientation.SetStartVertex(closed, 2);

            result.Changed.Should().BeTrue();
            closed.Vertices[0].P.Should().Be(new Pt2(10, 10));
            closed.Vertices[^1].P.Should().Be(new Pt2(10, 0));
            closed.Vertices[^1].Bulge.Should().Be(0.25, "each vertex carries its own outgoing arc");

            var open = Open((0, 0), (10, 0), (20, 0));
            var refused = PolylineOrientation.SetStartVertex(open, 1);
            refused.Changed.Should().BeFalse();
            refused.Notes.Should().ContainMatch("*open*");
        }

        [Fact]
        public void Setting_the_start_to_the_current_start_changes_nothing()
        {
            var closed = ClosedShape((0, 0), (10, 0), (10, 10));

            PolylineOrientation.SetStartVertex(closed, 0).Changed.Should().BeFalse();
        }

        // ── segment info (PL-SgInfo) ─────────────────────────────────

        [Fact]
        public void Segment_info_reports_arc_geometry_and_nulls_for_a_straight()
        {
            var shape = new PlShape(new[]
            {
                new PlVertex(0, 0, 0, 1.0),
                PlVertex.At(1, 0),
                PlVertex.At(2, 0),
            });

            var arc = PolylineOrientation.Describe(shape, 0)!;
            arc.IsArc.Should().BeTrue();
            arc.Radius!.Value.Should().BeApproximately(0.5, 1e-12);
            arc.Center!.Value.X.Should().BeApproximately(0.5, 1e-12);
            arc.DeltaDegrees!.Value.Should().BeApproximately(180.0, 1e-9);
            arc.Length.Should().BeApproximately(Math.PI * 0.5, 1e-9);
            arc.ChordLength.Should().BeApproximately(1.0, 1e-12);
            arc.Sagitta!.Value.Should().BeApproximately(0.5, 1e-12);

            var straight = PolylineOrientation.Describe(shape, 1)!;
            straight.IsArc.Should().BeFalse();
            straight.Radius.Should().BeNull();
            straight.Center.Should().BeNull();
            straight.Length.Should().BeApproximately(1.0, 1e-12);

            PolylineOrientation.Describe(shape, 5).Should().BeNull();
        }

        // ── segment edits ────────────────────────────────────────────

        [Fact]
        public void Line_to_arc_sets_a_bulge_of_the_requested_radius()
        {
            var shape = Open((0, 0), (10, 0), (20, 0));

            var result = SegmentEditor.LineToArc(shape, 0, radius: 8.0);

            result.Changed.Should().BeTrue();
            BulgeMath.Radius(new Pt2(0, 0), new Pt2(10, 0), shape.Vertices[0].Bulge)
                .Should().BeApproximately(8.0, 1e-9);
            shape.Vertices[1].Bulge.Should().Be(0.0, "only the requested segment changes");
        }

        [Fact]
        public void Line_to_arc_refuses_a_radius_that_cannot_span_the_chord()
        {
            var shape = Open((0, 0), (10, 0));

            var result = SegmentEditor.LineToArc(shape, 0, radius: 4.0);

            result.Changed.Should().BeFalse();
            result.Notes.Should().ContainMatch("*smaller than half*");
            shape.Vertices[0].Bulge.Should().Be(0.0);
        }

        [Fact]
        public void Arc_to_line_clears_the_bulge_and_reports_a_straight_segment_untouched()
        {
            var shape = new PlShape(new[] { new PlVertex(0, 0, 0, 0.8), PlVertex.At(10, 0) });

            SegmentEditor.ArcToLine(shape, 0).Changed.Should().BeTrue();
            shape.Vertices[0].Bulge.Should().Be(0.0);

            SegmentEditor.ArcToLine(shape, 0).Notes.Should().ContainMatch("*already straight*");
        }

        [Fact]
        public void Flatten_arcs_replaces_an_arc_with_straights_on_the_arc()
        {
            var shape = new PlShape(new[] { new PlVertex(0, 0, 0, 1.0), PlVertex.At(1, 0) });
            var center = BulgeMath.Center(new Pt2(0, 0), new Pt2(1, 0), 1.0);

            var result = SegmentEditor.FlattenArcs(shape, BulgeMath.FlattenMode.Count, 4);

            result.Added.Should().Be(3);
            shape.HasArcs.Should().BeFalse();
            foreach (var v in shape.Vertices)
                v.P.DistanceTo(center).Should().BeApproximately(0.5, 1e-9);
        }

        [Fact]
        public void Flatten_arcs_keeps_arcs_flatter_than_the_minimum_radius()
        {
            // R ≈ 0.5 (sharp) and R ≈ 5 (flat). With min_radius = 1 only the sharp one flattens.
            double flatBulge = BulgeMath.BulgeFromRadius(new Pt2(2, 0), new Pt2(3, 0), 5.0, false)!.Value;
            var shape = new PlShape(new[]
            {
                new PlVertex(0, 0, 0, 1.0),
                new PlVertex(1, 0),
                new PlVertex(2, 0, 0, flatBulge),
                PlVertex.At(3, 0),
            });

            var result = SegmentEditor.FlattenArcs(shape, BulgeMath.FlattenMode.Count, 3, minRadius: 1.0);

            result.Notes.Should().ContainMatch("*1 arc segment(s) flattened*");
            result.Notes.Should().ContainMatch("*kept*");
            shape.Vertices.Should().Contain(v => Math.Abs(v.Bulge - flatBulge) < 1e-12);
        }

        [Fact]
        public void Flatten_arcs_on_a_polyline_without_arcs_says_so()
        {
            var shape = Open((0, 0), (10, 0));

            var result = SegmentEditor.FlattenArcs(shape, BulgeMath.FlattenMode.Count, 5);

            result.Changed.Should().BeFalse();
            result.Notes.Should().ContainMatch("*no arc segments*");
        }

        [Fact]
        public void Set_width_applies_to_one_segment_or_to_all_of_them()
        {
            var shape = Open((0, 0), (10, 0), (20, 0));

            SegmentEditor.SetWidth(shape, 1, 0.5, 0.5).Changed.Should().BeTrue();
            shape.Vertices[0].HasWidth.Should().BeFalse();
            shape.Vertices[1].StartWidth.Should().Be(0.5);

            SegmentEditor.SetWidth(shape, null, 0.25, 1.0);
            shape.Vertices[0].StartWidth.Should().Be(0.25);
            shape.Vertices[0].EndWidth.Should().Be(1.0);
            shape.Vertices[1].StartWidth.Should().Be(0.25);
        }

        [Fact]
        public void Set_width_refuses_negative_values_and_3d_polylines()
        {
            SegmentEditor.SetWidth(Open((0, 0), (10, 0)), null, -1.0, 0.0)
                .Notes.Should().ContainMatch("*negative*");

            var poly3d = new PlShape(new[] { new PlVertex(0, 0, 1), new PlVertex(10, 0, 2) }, is3d: true);
            SegmentEditor.SetWidth(poly3d, null, 1.0, 1.0).Notes.Should().ContainMatch("*3D polyline*");
        }

        // ── builders ─────────────────────────────────────────────────

        [Fact]
        public void Midline_of_two_parallel_lines_runs_halfway_between_them()
        {
            var a = new[] { new Pt2(0, 0), new Pt2(100, 0) };
            var b = new[] { new Pt2(0, 10), new Pt2(100, 10) };

            var mid = PolylineBuilders.Midline(a, b, samples: 11);

            mid.Should().HaveCount(11);
            foreach (var p in mid) p.Y.Should().BeApproximately(5.0, 1e-9);
            mid[0].X.Should().BeApproximately(0.0, 1e-9);
            mid[^1].X.Should().BeApproximately(100.0, 1e-9);
        }

        [Fact]
        public void Midline_stays_centred_when_the_two_curves_have_different_lengths()
        {
            // B is half as long: pairing by station fraction would drift, closest-point does not.
            var a = new[] { new Pt2(0, 0), new Pt2(100, 0) };
            var b = new[] { new Pt2(0, 10), new Pt2(50, 10) };

            var mid = PolylineBuilders.Midline(a, b, samples: 6);

            mid[0].Should().Be(new Pt2(0, 5));
            mid[2].Y.Should().BeApproximately(5.0, 1e-9);      // x=40 → closest point on B is (40,10)
            mid[^1].Y.Should().BeApproximately(5.0, 1e-9);     // x=100 → closest point on B is its end
            mid[^1].X.Should().BeApproximately(75.0, 1e-9);
        }

        [Fact]
        public void Midline_needs_two_real_curves()
        {
            PolylineBuilders.Midline(new[] { new Pt2(0, 0) }, new[] { new Pt2(0, 1), new Pt2(1, 1) })
                .Should().BeEmpty();
        }

        [Fact]
        public void Rectangle_from_three_points_is_a_rectangle_on_the_third_point_side()
        {
            var corners = PolylineBuilders.RectangleFrom3Points(new Pt2(0, 0), new Pt2(10, 0), new Pt2(3, -4));

            corners.Should().HaveCount(4);
            corners[2].Should().Be(new Pt2(10, -4));
            corners[3].Should().Be(new Pt2(0, -4));

            // Right angle at every corner.
            for (int i = 0; i < 4; i++)
            {
                var p = corners[i];
                var q = corners[(i + 1) % 4];
                var r = corners[(i + 2) % 4];
                BulgeMath.Deflection(p, q, r).Should().BeApproximately(Math.PI / 2.0, 1e-9);
            }
        }

        [Fact]
        public void Rectangle_from_three_collinear_points_is_refused()
        {
            PolylineBuilders.RectangleFrom3Points(new Pt2(0, 0), new Pt2(10, 0), new Pt2(5, 0))
                .Should().BeEmpty();
            PolylineBuilders.RectangleFrom3Points(new Pt2(0, 0), new Pt2(0, 0), new Pt2(5, 5))
                .Should().BeEmpty();
        }

        [Fact]
        public void Perpendicular_chain_snaps_a_rough_click_chain_to_right_angles()
        {
            var picks = new[]
            {
                new Pt2(0, 0),
                new Pt2(10, 0.7),      // mostly along X
                new Pt2(10.4, 8),      // mostly along Y
                new Pt2(20, 8.3),      // along X again
            };

            var chain = PolylineBuilders.PerpendicularChain(picks);

            chain.Should().HaveCount(4);
            chain[1].Should().Be(new Pt2(10, 0));
            chain[2].Should().Be(new Pt2(10, 8));
            chain[3].Should().Be(new Pt2(20, 8));
            for (int i = 2; i < chain.Count; i++)
                BulgeMath.Deflection(chain[i - 2], chain[i - 1], chain[i])
                    .Should().BeApproximately(Math.PI / 2.0, 1e-9);
        }

        [Fact]
        public void Perpendicular_chain_honours_a_rotated_base_angle()
        {
            // Base axis at 45°: a click along that diagonal stays on it.
            var picks = new[] { new Pt2(0, 0), new Pt2(7, 7.5) };

            var chain = PolylineBuilders.PerpendicularChain(picks, Math.PI / 4.0);

            chain.Should().HaveCount(2);
            chain[1].X.Should().BeApproximately(chain[1].Y, 1e-9);
        }

        [Fact]
        public void Perpendicular_chain_drops_duplicate_clicks()
        {
            var picks = new[] { new Pt2(0, 0), new Pt2(0, 0), new Pt2(5, 0) };

            PolylineBuilders.PerpendicularChain(picks).Should().HaveCount(2);
        }
    }
}
