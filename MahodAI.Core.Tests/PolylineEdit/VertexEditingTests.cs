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
    /// Picking, cleaning and densifying vertices — the PLTOOLS behaviours the firm actually uses
    /// (PL-VxDel, PL-VxAdd, PL-VxRdc "weeding", PL-VxOpt "coincident"), plus the Douglas-Peucker
    /// thinning PLTOOLS never had.
    /// </summary>
    public class VertexEditingTests
    {
        private static PlShape Open(params (double X, double Y)[] pts) =>
            new(pts.Select(p => PlVertex.At(p.X, p.Y)));

        private static PlShape Closed(params (double X, double Y)[] pts) =>
            new(pts.Select(p => PlVertex.At(p.X, p.Y)), closed: true);

        // ── picking ───────────────────────────────────────────────────

        [Fact]
        public void Nearest_vertex_uses_distance_not_the_curve_parameter()
        {
            // Segment 0 is 200 units long, segment 1 is 2 units. A click 1 unit past the middle
            // vertex is unmistakably "at" vertex 1 — but resolving by rounding the curve
            // parameter (what PL-VxDel and MAHOD-PL's VDEL both do) lands on vertex 2.
            var shape = Open((0, 0), (200, 0), (202, 0));

            var hit = VertexPicker.NearestVertex(shape, new Pt2(201, 0));

            hit.Should().NotBeNull();
            hit!.Index.Should().Be(1);
            hit.Distance.Should().BeApproximately(1.0, 1e-12);
        }

        [Fact]
        public void Nearest_segment_reports_position_along_the_segment()
        {
            var shape = Open((0, 0), (10, 0), (10, 10));

            var hit = VertexPicker.NearestSegment(shape, new Pt2(10.5, 7.5));

            hit.Should().NotBeNull();
            hit!.SegmentIndex.Should().Be(1);
            hit.T.Should().BeApproximately(0.75, 1e-12);
            hit.Point.X.Should().BeApproximately(10.0, 1e-12);
            hit.Point.Y.Should().BeApproximately(7.5, 1e-12);
        }

        [Fact]
        public void Nearest_segment_of_a_closed_shape_includes_the_closing_segment()
        {
            var shape = Closed((0, 0), (10, 0), (10, 10), (0, 10));

            var hit = VertexPicker.NearestSegment(shape, new Pt2(-0.5, 5));

            hit!.SegmentIndex.Should().Be(3);        // the segment from (0,10) back to (0,0)
        }

        [Fact]
        public void A_pick_on_top_of_a_vertex_is_rejected_for_insertion()
        {
            var shape = Open((0, 0), (10, 0));

            var atVertex = VertexPicker.NearestSegment(shape, new Pt2(0.05, 0))!;
            var atMiddle = VertexPicker.NearestSegment(shape, new Pt2(5, 0))!;

            VertexPicker.IsOnExistingVertex(atVertex).Should().BeTrue();
            VertexPicker.IsOnExistingVertex(atMiddle).Should().BeFalse();
        }

        // ── coincident vertices (PL-VxOpt) ────────────────────────────

        [Fact]
        public void Removes_coincident_vertices_and_keeps_the_arc_that_follows_them()
        {
            // v1 is a duplicate of v2, and v2 starts an arc. PL-VxOpt scans from the END and
            // drops the EARLIER duplicate precisely so the surviving vertex keeps its bulge.
            var shape = new PlShape(new[]
            {
                PlVertex.At(0, 0),
                PlVertex.At(10, 0),
                new PlVertex(10, 0, 0, 0.5),
                PlVertex.At(20, 0),
            });

            var result = VertexCleaner.RemoveCoincident(shape);

            result.Removed.Should().Be(1);
            shape.Count.Should().Be(3);
            shape.Vertices[1].Bulge.Should().Be(0.5, "the arc must survive the de-duplication");
        }

        [Fact]
        public void An_open_polyline_that_ends_where_it_starts_is_closed_properly()
        {
            var shape = Open((0, 0), (10, 0), (10, 10), (0, 0));

            var result = VertexCleaner.RemoveCoincident(shape);

            shape.Closed.Should().BeTrue();
            shape.Count.Should().Be(3);
            result.Removed.Should().Be(1);
            result.Notes.Should().ContainMatch("*closed properly*");
        }

        [Fact]
        public void Coincident_removal_never_goes_below_the_minimum_vertex_count()
        {
            var shape = Open((5, 5), (5, 5));       // both vertices identical

            var result = VertexCleaner.RemoveCoincident(shape);

            shape.Count.Should().Be(2);
            result.Removed.Should().Be(0);
        }

        // ── weeding (PL-VxRdc) ───────────────────────────────────────

        [Fact]
        public void Weeding_collapses_a_straight_run_to_its_endpoints_in_one_pass()
        {
            var pts = new List<(double, double)> { (0, 0) };
            for (int i = 1; i <= 50; i++) pts.Add((i, 0.0));
            var shape = Open(pts.ToArray());

            var result = VertexCleaner.Weed(shape, deviationTolerance: 0.01);

            shape.Count.Should().Be(2);
            result.Removed.Should().Be(49);
        }

        [Fact]
        public void Weeding_keeps_a_vertex_that_deviates_more_than_the_tolerance()
        {
            var shape = Open((0, 0), (10, 0.05), (20, 0), (30, 3.0), (40, 0));

            VertexCleaner.Weed(shape, deviationTolerance: 0.1);

            shape.Count.Should().Be(4);
            shape.Vertices.Should().NotContain(v => Math.Abs(v.Y - 0.05) < 1e-9);
            shape.Vertices.Should().Contain(v => Math.Abs(v.Y - 3.0) < 1e-9);
        }

        [Fact]
        public void Weeding_by_angle_tolerance_drops_gentle_turns_only()
        {
            // ≈2.9° of deflection at (10,0.25); a right angle at (20,0).
            var shape = Open((0, 0), (10, 0.25), (20, 0), (20, -20));

            VertexCleaner.Weed(shape, deviationTolerance: null, angleToleranceDeg: 5.0);

            shape.Count.Should().Be(3);
            shape.Vertices[1].P.Should().Be(new Pt2(20, 0));
        }

        [Fact]
        public void Weeding_without_a_tolerance_does_nothing_and_says_so()
        {
            var shape = Open((0, 0), (1, 0), (2, 0));

            var result = VertexCleaner.Weed(shape, deviationTolerance: null, angleToleranceDeg: null);

            result.Changed.Should().BeFalse();
            result.Notes.Should().ContainMatch("*no tolerance*");
            shape.Count.Should().Be(3);
        }

        [Fact]
        public void Weeding_refuses_to_straighten_an_arc_unless_asked()
        {
            // v1 starts an arc and v2 ends it: both are collinear by position, but dropping
            // either would silently replace the curve with a straight. v3 is redundant and has
            // no arc anywhere near it, so it is the only vertex that may go.
            var shape = new PlShape(new[]
            {
                PlVertex.At(0, 0),
                new PlVertex(10, 0, 0, 0.6),
                PlVertex.At(20, 0),
                PlVertex.At(25, 0),
                PlVertex.At(30, 0),
            });

            var result = VertexCleaner.Weed(shape, deviationTolerance: 0.5);

            result.Removed.Should().Be(1);
            shape.Count.Should().Be(4);
            shape.Vertices.Should().Contain(v => v.Bulge == 0.6, "the arc must survive weeding");
            shape.Vertices.Should().NotContain(v => v.P == new Pt2(25, 0));
        }

        [Fact]
        public void Weeding_a_closed_shape_may_drop_any_vertex_including_the_first()
        {
            // A square with a redundant vertex on its first edge. An implementation that treats
            // vertex 0 and the last vertex as untouchable endpoints (correct for an open
            // polyline) leaves work undone on a closed one.
            var shape = Closed((0, 0), (10, 0), (20, 0), (20, 20), (0, 20));

            var result = VertexCleaner.Weed(shape, deviationTolerance: 0.01);

            result.Removed.Should().Be(1);
            shape.Count.Should().Be(4);
            shape.Vertices.Should().NotContain(v => v.P == new Pt2(10, 0));
        }

        // ── thinning (Douglas-Peucker) ───────────────────────────────

        [Fact]
        public void Thinning_bounds_the_error_of_the_whole_result()
        {
            // A dense sine-ish wiggle around a straight line, amplitude 0.4.
            var pts = new List<(double, double)>();
            for (int i = 0; i <= 100; i++)
                pts.Add((i, 0.4 * Math.Sin(i * Math.PI / 10.0)));
            var shape = Open(pts.ToArray());
            var original = pts.Select(p => new Pt2(p.Item1, p.Item2)).ToList();

            var result = VertexCleaner.Thin(shape, tolerance: 0.5);

            result.Removed.Should().BeGreaterThan(80);
            shape.Vertices[0].P.Should().Be(original[0]);
            shape.Vertices[^1].P.Should().Be(original[^1]);

            // Every original vertex is still within tolerance of the thinned line.
            var kept = shape.Vertices.Select(v => v.P).ToList();
            foreach (var p in original)
                DistanceToChain(kept, p).Should().BeLessThan(0.5 + 1e-9);
        }

        [Fact]
        public void Thinning_keeps_a_real_bend()
        {
            var pts = new List<(double, double)>();
            for (int i = 0; i <= 20; i++) pts.Add((i, 0));
            for (int i = 1; i <= 20; i++) pts.Add((20, i));
            var shape = Open(pts.ToArray());

            VertexCleaner.Thin(shape, tolerance: 0.05);

            shape.Count.Should().Be(3);
            shape.Vertices[1].P.Should().Be(new Pt2(20, 0), "the corner is the one vertex that matters");
        }

        [Fact]
        public void Thinning_a_closed_shape_thins_across_the_seam_too()
        {
            // A square whose FIRST edge is densely sampled: an implementation that ignores the
            // closing segment (MAHOD-PL's VTHIN) leaves the seam dense.
            var pts = new List<(double, double)>();
            for (int i = 0; i <= 20; i++) pts.Add((i, 0));
            pts.Add((20, 20));
            pts.Add((0, 20));
            var shape = Closed(pts.ToArray());

            VertexCleaner.Thin(shape, tolerance: 0.05);

            shape.Count.Should().Be(4);
            shape.Closed.Should().BeTrue();
        }

        [Fact]
        public void Thinning_reports_refusals_instead_of_pretending_to_work()
        {
            VertexCleaner.Thin(Open((0, 0), (1, 1)), 0.1).Notes.Should().ContainMatch("*fewer than 3*");
            VertexCleaner.Thin(Open((0, 0), (1, 0), (2, 0)), 0.0).Notes.Should().ContainMatch("*greater than 0*");
        }

        [Fact]
        public void Deleting_a_vertex_between_two_arcs_straightens_the_merged_segment()
        {
            // Both originals document this: the merged segment cannot keep the old bulge, which
            // described a shorter chord and would bend the new segment somewhere nobody asked.
            var shape = new PlShape(new[]
            {
                new PlVertex(0, 0, 0, 0.4),
                new PlVertex(10, 0, 0, 0.4),
                PlVertex.At(20, 0),
            });

            VertexCleaner.RemoveVertexKeepingGeometry(shape, 1);

            shape.Count.Should().Be(2);
            shape.Vertices[0].Bulge.Should().Be(0.0);
        }

        // ── densifying (PL-DIV / PL-DIVALL / PL-VFI / VDIST) ─────────

        [Fact]
        public void Adds_a_vertex_every_n_units_along_the_polyline()
        {
            var shape = Open((0, 0), (10, 0));

            var result = VertexDensifier.ByDistance(shape, 2.0);

            result.Added.Should().Be(4);                    // 2, 4, 6, 8 — the far end is already a vertex
            shape.Count.Should().Be(6);
            shape.Vertices.Select(v => v.X).Should().BeInAscendingOrder();
        }

        [Fact]
        public void Distance_spacing_crosses_segment_boundaries()
        {
            // Two 5-unit segments: stations at 2,4,6,8 land in both.
            var shape = Open((0, 0), (5, 0), (10, 0));

            var result = VertexDensifier.ByDistance(shape, 2.0);

            result.Added.Should().Be(4);
            shape.Vertices.Select(v => Math.Round(v.X, 6))
                .Should().BeEquivalentTo(new[] { 0.0, 2.0, 4.0, 5.0, 6.0, 8.0, 10.0 });
        }

        [Fact]
        public void Distance_spacing_refuses_a_spacing_larger_than_the_line()
        {
            var shape = Open((0, 0), (10, 0));

            var result = VertexDensifier.ByDistance(shape, 20.0);

            result.Changed.Should().BeFalse();
            result.Notes.Should().ContainMatch("*too large*");
        }

        [Fact]
        public void Splits_one_segment_into_equal_parts()
        {
            var shape = Open((0, 0), (10, 0), (10, 10));

            var result = VertexDensifier.ByCount(shape, parts: 4, segmentIndex: 1);

            result.Added.Should().Be(3);
            shape.Count.Should().Be(6);
            shape.Vertices.Skip(1).Take(4).Select(v => Math.Round(v.Y, 6))
                .Should().BeEquivalentTo(new[] { 0.0, 2.5, 5.0, 7.5 });
        }

        [Fact]
        public void Splits_every_segment_when_no_segment_is_named()
        {
            var shape = Open((0, 0), (10, 0), (10, 10));

            var result = VertexDensifier.ByCount(shape, parts: 2);

            result.Added.Should().Be(2);
            shape.Count.Should().Be(5);
        }

        [Fact]
        public void Inserting_on_an_arc_keeps_the_radius_and_the_width_taper()
        {
            // One 90° arc, tapering 1.0 → 2.0 wide.
            double bulge = Math.Tan(Math.PI / 8.0);
            var shape = new PlShape(new[]
            {
                new PlVertex(0, 0, 0, bulge, 1.0, 2.0),
                PlVertex.At(1, 0),
            });
            double radiusBefore = BulgeMath.Radius(new Pt2(0, 0), new Pt2(1, 0), bulge);

            int index = VertexDensifier.InsertOnSegment(shape, 0, 0.5);

            index.Should().Be(1);
            shape.Count.Should().Be(3);
            BulgeMath.Radius(shape.SegmentStart(0).P, shape.SegmentEnd(0).P, shape.SegmentBulge(0))
                .Should().BeApproximately(radiusBefore, 1e-9);
            BulgeMath.Radius(shape.SegmentStart(1).P, shape.SegmentEnd(1).P, shape.SegmentBulge(1))
                .Should().BeApproximately(radiusBefore, 1e-9);
            shape.Vertices[0].EndWidth.Should().BeApproximately(1.5, 1e-9);
            shape.Vertices[1].StartWidth.Should().BeApproximately(1.5, 1e-9);
            shape.Vertices[1].EndWidth.Should().BeApproximately(2.0, 1e-9);
        }

        [Fact]
        public void Insertion_on_a_3d_polyline_interpolates_the_elevation()
        {
            var shape = new PlShape(new[]
            {
                new PlVertex(0, 0, 100.0),
                new PlVertex(10, 0, 110.0),
            }, is3d: true);

            VertexDensifier.InsertOnSegment(shape, 0, 0.5);

            shape.Vertices[1].Z.Should().BeApproximately(105.0, 1e-9);
        }

        [Fact]
        public void Insertion_next_to_an_existing_vertex_is_skipped_and_reported()
        {
            var shape = Open((0, 0), (10, 0));

            var result = VertexDensifier.InsertMany(shape, new[] { (0, 0.005) });

            result.Added.Should().Be(0);
            result.Notes.Should().ContainMatch("*existing vertex*");
            shape.Count.Should().Be(2);
        }

        [Fact]
        public void Inserts_vertices_at_intersection_points()
        {
            var shape = Open((0, 0), (10, 0), (10, 10));

            var result = VertexDensifier.AtPoints(
                shape,
                new[] { new Pt2(4, 0), new Pt2(10, 6), new Pt2(50, 50) },
                maxOffset: 1e-6);

            result.Added.Should().Be(2);
            result.Notes.Should().ContainMatch("*farther than*");
            shape.Vertices.Should().Contain(v => Math.Abs(v.X - 4.0) < 1e-9 && Math.Abs(v.Y) < 1e-9);
        }

        [Fact]
        public void Several_insertions_on_one_arc_all_keep_the_radius()
        {
            double bulge = 1.0;                              // semicircle
            var shape = new PlShape(new[] { new PlVertex(0, 0, 0, bulge), PlVertex.At(1, 0) });

            var result = VertexDensifier.InsertMany(shape, new[] { (0, 0.25), (0, 0.5), (0, 0.75) });

            result.Added.Should().Be(3);
            for (int s = 0; s < shape.SegmentCount; s++)
                BulgeMath.Radius(shape.SegmentStart(s).P, shape.SegmentEnd(s).P, shape.SegmentBulge(s))
                    .Should().BeApproximately(0.5, 1e-9, $"segment {s}");
        }

        private static double DistanceToChain(List<Pt2> chain, Pt2 p)
        {
            double best = double.MaxValue;
            for (int i = 1; i < chain.Count; i++)
                best = Math.Min(best, BulgeMath.DistanceToSegment(chain[i - 1], chain[i], p));
            return best;
        }
    }
}
