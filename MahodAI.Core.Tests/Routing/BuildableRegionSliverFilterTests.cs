using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Pure-math tests for the TIN sliver filter that keeps the walkable mask honest.
    /// An unconstrained Civil 3D surface triangulates to the convex hull of its points,
    /// bridging concave bays with long "sliver" triangles that are not real ground; the
    /// filter drops triangles whose longest edge exceeds a data-driven threshold so the
    /// router (and the containment readback) only ever see the real footprint.
    /// These cover the Civil-3D-free helpers; the rasterisation itself needs a TinSurface.
    /// </summary>
    public class BuildableRegionSliverFilterTests
    {
        // ── TriangleLongestEdgeXY ───────────────────────────────────────────
        [Fact]
        public void LongestEdge_ReturnsLongestOfThreeSides()
        {
            // 3-4-5 right triangle: hypotenuse 5 is the longest edge.
            double e = BuildableRegion.TriangleLongestEdgeXY(0, 0, 3, 0, 0, 4);
            e.Should().BeApproximately(5.0, 1e-9);
        }

        [Fact]
        public void LongestEdge_DetectsBayBridgeSliver()
        {
            // A thin sliver bridging a 500 m concave gap: two short edges, one ~500 m edge.
            double e = BuildableRegion.TriangleLongestEdgeXY(0, 0, 500, 0, 250, 3);
            e.Should().BeApproximately(500.0, 0.05);
        }

        // ── MedianOf ────────────────────────────────────────────────────────
        [Fact]
        public void Median_OddCount_IsMiddleValue()
        {
            BuildableRegion.MedianOf(new[] { 5.0, 1.0, 3.0 }).Should().Be(3.0);
        }

        [Fact]
        public void Median_EvenCount_IsAverageOfMiddlePair()
        {
            BuildableRegion.MedianOf(new[] { 1.0, 2.0, 3.0, 4.0 }).Should().Be(2.5);
        }

        [Fact]
        public void Median_DoesNotMutateInput()
        {
            var input = new[] { 9.0, 1.0, 5.0 };
            var copy = (double[])input.Clone();
            BuildableRegion.MedianOf(input);
            input.Should().Equal(copy);
        }

        [Fact]
        public void Median_EmptyInput_IsZero()
        {
            BuildableRegion.MedianOf(Array.Empty<double>()).Should().Be(0.0);
        }

        // ── ComputeAutoMaxEdge ──────────────────────────────────────────────
        [Fact]
        public void AutoMaxEdge_IsMedianTimesFactor_WhenAboveFloor()
        {
            // median 20 × factor 8 = 160 (> floor 30).
            var edges = new[] { 18.0, 20.0, 22.0 };
            BuildableRegion.ComputeAutoMaxEdge(edges, 8.0, 30.0)
                .Should().BeApproximately(160.0, 1e-9);
        }

        [Fact]
        public void AutoMaxEdge_ClampsToFloor_OnVeryDenseSurvey()
        {
            // Tiny median (2 m) × 8 = 16 < floor 30 → floor wins, so ordinary
            // triangle variation and small interior gaps are still tolerated.
            var edges = new[] { 1.5, 2.0, 2.5 };
            BuildableRegion.ComputeAutoMaxEdge(edges, 8.0, 30.0).Should().Be(30.0);
        }

        [Fact]
        public void AutoMaxEdge_EmptyInput_ReturnsFloor()
        {
            BuildableRegion.ComputeAutoMaxEdge(Array.Empty<double>(), 8.0, 30.0).Should().Be(30.0);
        }

        [Fact]
        public void AutoMaxEdge_MedianRobustToSmallSliverTail()
        {
            // 100 dense real triangles (~15 m edges) + 5 huge bay bridges (~900 m).
            // The median stays in the dense cluster, so the threshold (median×8 ≈ 120 m)
            // sits ABOVE every real edge and BELOW every sliver — separating the two cleanly.
            var edges = new List<double>();
            for (int i = 0; i < 100; i++) edges.Add(14.0 + (i % 5)); // 14..18 m
            for (int i = 0; i < 5; i++) edges.Add(880.0 + i * 10);   // ~900 m slivers

            double threshold = BuildableRegion.ComputeAutoMaxEdge(edges, 8.0, 30.0);

            edges.Take(100).Should().OnlyContain(e => e <= threshold);   // all real kept
            edges.Skip(100).Should().OnlyContain(e => e > threshold);    // all slivers dropped
        }

        [Fact]
        public void AutoMaxEdge_UniformlyCoarseSurvey_ScalesUp_KeepsRealTriangles()
        {
            // A sparse rural survey where the REAL triangles are genuinely ~120 m.
            // The threshold must scale with the data (median×4 ≈ 480 m) so it does not
            // punch holes in legitimately coarse ground.
            var edges = Enumerable.Range(0, 50).Select(i => 110.0 + i).ToList(); // 110..159 m
            double threshold = BuildableRegion.ComputeAutoMaxEdge(edges, BuildableRegion.AutoEdgeFactor, 30.0);
            threshold.Should().BeGreaterThan(159.0);
            edges.Should().OnlyContain(e => e <= threshold);
        }

        // ── Threshold placement: the length gate sits between dense ground and long bridges ──
        // (Whether a long triangle is actually dropped is then decided by the thinness test —
        // see the IsSliver_* cases — so this only checks the length gate lands sensibly.)
        [Fact]
        public void AutoMaxEdge_Factor4_GateLandsBetweenDenseAndLongTriangles()
        {
            // 200 dense real triangles (median ~20 m) + a handful of 120-180 m long triangles.
            var edges = new List<double>();
            for (int i = 0; i < 200; i++) edges.Add(16.0 + (i % 9));   // 16..24 m, median ~20
            double[] longTris = { 120, 140, 160, 180 };
            edges.AddRange(longTris);

            double threshold = BuildableRegion.ComputeAutoMaxEdge(edges, BuildableRegion.AutoEdgeFactor, BuildableRegion.MinEdgeFloorM);

            threshold.Should().BeInRange(60.0, 110.0);                 // median(~20)×4 ≈ 80
            edges.Take(200).Should().OnlyContain(e => e <= threshold); // dense real below gate
            longTris.Should().OnlyContain(e => e > threshold);         // long triangles above gate
        }

        // ── TriangleEdgesXY ─────────────────────────────────────────────────
        [Fact]
        public void TriangleEdges_ReturnsLongestAndShortest()
        {
            // 3-4-5 right triangle.
            var (longest, shortest) = BuildableRegion.TriangleEdgesXY(0, 0, 3, 0, 0, 4);
            longest.Should().BeApproximately(5.0, 1e-9);
            shortest.Should().BeApproximately(3.0, 1e-9);
        }

        // ── IsSliver (the actual per-triangle drop decision: drop iff mega OR long-AND-thin) ──
        // Signature: IsSliver(longestEdge, shortestEdge, lengthThresholdM, maxAspectRatio, hugeEdgeCapM)
        private const double THR = 80.0;   // length gate
        private const double ASP = 8.0;    // max aspect ratio
        private const double CAP = 2000.0; // mega-triangle cap

        [Fact]
        public void IsSliver_KeepsLargeFatOpenGround()
        {
            // THE over-trim fix: a large FAT triangle (longest 200 m, shortest 180 m, aspect ~1.1)
            // is legitimately coarse / sparsely-surveyed OPEN GROUND — kept so the road can use it
            // instead of hugging the dense-data edge. Long, but not thin and not mega → KEPT.
            BuildableRegion.IsSliver(200, 180, THR, ASP, CAP).Should().BeFalse();
        }

        [Fact]
        public void IsSliver_KeepsMidLengthFatTriangle()
        {
            // A 150 m bay-fill triangle that is FAT (shortest 90 m, aspect ~1.7) is real ground → KEPT.
            BuildableRegion.IsSliver(150, 90, THR, ASP, CAP).Should().BeFalse();
        }

        [Fact]
        public void IsSliver_DropsLongThinNeedleBridge()
        {
            // Long (200 m) AND thin (shortest 5 m, aspect 40 > 8) → a hull/bay needle bridge → dropped.
            BuildableRegion.IsSliver(200, 5, THR, ASP, CAP).Should().BeTrue();
        }

        [Fact]
        public void IsSliver_DropsMidLengthThinBridge()
        {
            // 150 m and thin (shortest 10 m, aspect 15 > 8) → needle bridge → dropped.
            BuildableRegion.IsSliver(150, 10, THR, ASP, CAP).Should().BeTrue();
        }

        [Fact]
        public void IsSliver_DropsMegaTriangle_EvenWhenFat()
        {
            // A hull-spanning mega-triangle (longest 3000 m > cap 2000) is dropped regardless of shape.
            BuildableRegion.IsSliver(3000, 2500, THR, ASP, CAP).Should().BeTrue();
        }

        [Fact]
        public void IsSliver_KeepsShortThinTriangle_BelowLengthGate()
        {
            // Thin but short (longest 20 m < gate 80) → tiny fine detail, harmless → KEPT.
            BuildableRegion.IsSliver(20, 1, THR, ASP, CAP).Should().BeFalse();
        }

        [Fact]
        public void IsSliver_KeepsNearEquilateralTriangle()
        {
            BuildableRegion.IsSliver(22, 18, THR, ASP, CAP).Should().BeFalse();
        }

        // ── PercentileOf ────────────────────────────────────────────────────
        [Fact]
        public void Percentile_Median_MatchesMedianOf()
        {
            var vals = new[] { 1.0, 2.0, 3.0, 4.0 };
            BuildableRegion.PercentileOf(vals, 0.50).Should().Be(BuildableRegion.MedianOf(vals));
        }

        [Fact]
        public void Percentile_ZeroAndOne_AreMinAndMax()
        {
            var vals = new[] { 5.0, 1.0, 9.0, 3.0 };
            BuildableRegion.PercentileOf(vals, 0.0).Should().Be(1.0);
            BuildableRegion.PercentileOf(vals, 1.0).Should().Be(9.0);
        }

        [Fact]
        public void Percentile_P90_IsNearTopOfRange()
        {
            var vals = Enumerable.Range(1, 100).Select(i => (double)i).ToList(); // 1..100
            BuildableRegion.PercentileOf(vals, 0.90).Should().BeApproximately(90.1, 0.5);
        }

        [Fact]
        public void Percentile_EmptyInput_IsZero()
        {
            BuildableRegion.PercentileOf(Array.Empty<double>(), 0.9).Should().Be(0.0);
        }
    }
}
