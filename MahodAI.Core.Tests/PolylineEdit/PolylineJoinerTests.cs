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
    /// Endpoint chaining for PL-JOIN / PL-JOIN3D / PL-CSE. The fuzz tolerance is the whole point:
    /// AutoCAD's own join needs exact coincidence, and real survey linework misses by millimetres.
    /// </summary>
    public class PolylineJoinerTests
    {
        private static PlShape Open(params (double X, double Y)[] pts) =>
            new(pts.Select(p => PlVertex.At(p.X, p.Y)));

        [Fact]
        public void Joins_two_pieces_that_meet_end_to_start()
        {
            var chains = PolylineJoiner.Chain2d(
                new[] { Open((0, 0), (10, 0)), Open((10, 0), (20, 0)) }, fuzz: 1e-6);

            chains.Should().HaveCount(1);
            chains[0].SourceIndices.Should().BeEquivalentTo(new[] { 0, 1 });
            chains[0].Shape.Count.Should().Be(3);
            chains[0].Shape.Vertices[^1].P.Should().Be(new Pt2(20, 0));
        }

        [Fact]
        public void Reverses_a_piece_whose_far_end_is_the_one_that_matches()
        {
            // Second piece runs backwards: (20,0) → (10,0).
            var chains = PolylineJoiner.Chain2d(
                new[] { Open((0, 0), (10, 0)), Open((20, 0), (10, 0)) }, fuzz: 1e-6);

            chains.Should().HaveCount(1);
            chains[0].Shape.Count.Should().Be(3);
            chains[0].Shape.Vertices[^1].P.Should().Be(new Pt2(20, 0));
        }

        [Fact]
        public void Attaches_a_piece_that_meets_the_chains_start()
        {
            var chains = PolylineJoiner.Chain2d(
                new[] { Open((10, 0), (20, 0)), Open((0, 0), (10, 0)) }, fuzz: 1e-6);

            chains.Should().HaveCount(1);
            chains[0].Shape.Vertices[0].P.Should().Be(new Pt2(0, 0));
            chains[0].Shape.Vertices[^1].P.Should().Be(new Pt2(20, 0));
        }

        [Fact]
        public void The_fuzz_decides_whether_a_small_gap_counts_as_joined()
        {
            var pieces = new[] { Open((0, 0), (10, 0)), Open((10.005, 0), (20, 0)) };

            PolylineJoiner.Chain2d(pieces, fuzz: 0.01).Should().HaveCount(1);
            PolylineJoiner.Chain2d(pieces, fuzz: 0.001).Should().HaveCount(2);
        }

        [Fact]
        public void A_chain_whose_ends_meet_comes_out_closed()
        {
            var chains = PolylineJoiner.Chain2d(new[]
            {
                Open((0, 0), (10, 0)),
                Open((10, 0), (10, 10)),
                Open((10, 10), (0, 0)),
            }, fuzz: 1e-6);

            chains.Should().HaveCount(1);
            chains[0].Shape.Closed.Should().BeTrue();
            chains[0].ClosedUp.Should().BeTrue();
            chains[0].Shape.Count.Should().Be(3, "the duplicate closing vertex is dropped");
        }

        [Fact]
        public void An_arc_survives_the_seam()
        {
            // The joint vertex is where the second piece's arc starts: the chain's last vertex has
            // to inherit that bulge, or the arc is lost in the concatenation.
            var withArc = new PlShape(new[]
            {
                new PlVertex(10, 0, 0, 0.5),
                PlVertex.At(20, 0),
            });

            var chains = PolylineJoiner.Chain2d(new[] { Open((0, 0), (10, 0)), withArc }, fuzz: 1e-6);

            chains.Should().HaveCount(1);
            chains[0].Shape.Vertices[1].Bulge.Should().Be(0.5);
        }

        [Fact]
        public void A_piece_that_matches_nothing_comes_back_on_its_own()
        {
            var chains = PolylineJoiner.Chain2d(new[]
            {
                Open((0, 0), (10, 0)),
                Open((100, 100), (110, 100)),
            }, fuzz: 1e-6);

            chains.Should().HaveCount(2);
            chains.Should().OnlyContain(c => c.SourceIndices.Count == 1);
            PolylineJoiner.TotalVertices(chains).Should().Be(4, "nothing may be silently dropped");
        }

        [Fact]
        public void Two_and_three_dimensional_pieces_are_never_mixed()
        {
            var flat = Open((0, 0), (10, 0));
            var spatial = new PlShape(new[]
            {
                new PlVertex(10, 0, 5.0),
                new PlVertex(20, 0, 7.0),
            }, is3d: true);

            var chains = PolylineJoiner.Chain2d(new[] { flat, spatial }, fuzz: 1e-6);

            chains.Should().HaveCount(2, "joining them would have to invent elevations");
        }

        [Fact]
        public void Chains_three_pieces_across_both_ends()
        {
            // Seeded from the middle piece: one attaches at its end, one at its start.
            var chains = PolylineJoiner.Chain2d(new[]
            {
                Open((10, 0), (20, 0)),
                Open((20, 0), (30, 0)),
                Open((0, 0), (10, 0)),
            }, fuzz: 1e-6);

            chains.Should().HaveCount(1);
            chains[0].SourceIndices.Should().HaveCount(3);
            chains[0].Shape.Vertices[0].P.Should().Be(new Pt2(0, 0));
            chains[0].Shape.Vertices[^1].P.Should().Be(new Pt2(30, 0));
            chains[0].Shape.Count.Should().Be(4);
        }

        [Fact]
        public void A_closed_piece_is_left_alone()
        {
            var closed = new PlShape(
                new[] { PlVertex.At(0, 0), PlVertex.At(10, 0), PlVertex.At(10, 10) }, closed: true);

            var chains = PolylineJoiner.Chain2d(new[] { closed, Open((10, 10), (20, 20)) }, fuzz: 1e-6);

            chains.Should().HaveCount(2);
        }
    }
}
