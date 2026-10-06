using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Modification;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Modification
{
    /// <summary>
    /// Pure-math tests for the deflection / PI-intersection helpers used by
    /// remove_alignment_curve. No AutoCAD runtime needed.
    /// </summary>
    public class CurveRemovalGeometryTests
    {
        private static double Rad(double deg) => deg * Math.PI / 180.0;

        // ── DeflectionDegrees ────────────────────────────────────────────────

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]           // identical directions
        [InlineData(0.0, 0.25, 0.25)]         // the Table 5.5 motivating case
        [InlineData(0.0, 1.0, 1.0)]           // loosest Table 5.5 threshold
        [InlineData(0.0, 90.0, 90.0)]
        [InlineData(0.0, 179.75, 179.75)]     // near-U-turn stays near 180
        [InlineData(0.0, 180.0, 180.0)]       // exact anti-parallel
        [InlineData(45.0, 44.75, 0.25)]       // order/sign independence
        [InlineData(359.9, 0.15, 0.25)]       // wraparound across 0°/360°
        [InlineData(0.15, 359.9, 0.25)]       // wraparound, reversed order
        [InlineData(0.0, 181.0, 179.0)]       // normalized into [0, 180]
        [InlineData(0.0, 359.75, 0.25)]       // 359.75° difference → 0.25°
        [InlineData(720.0, 0.25, 0.25)]       // multiple full turns collapse
        [InlineData(-90.0, 90.0, 180.0)]      // negative input directions
        public void DeflectionDegrees_NormalizesToZeroTo180(double dir1Deg, double dir2Deg, double expectedDeg)
        {
            CurveRemovalGeometry.DeflectionDegrees(Rad(dir1Deg), Rad(dir2Deg))
                .Should().BeApproximately(expectedDeg, 1e-9);
        }

        [Fact]
        public void DeflectionDegrees_IsSymmetric()
        {
            double a = Rad(12.34);
            double b = Rad(347.9);
            CurveRemovalGeometry.DeflectionDegrees(a, b)
                .Should().BeApproximately(CurveRemovalGeometry.DeflectionDegrees(b, a), 1e-12);
        }

        // ── TryIntersect ─────────────────────────────────────────────────────

        [Fact]
        public void TryIntersect_ParallelSameDirection_ReturnsFalse()
        {
            bool ok = CurveRemovalGeometry.TryIntersect(
                0, 0, Rad(30), 10, 20, Rad(30), out _, out _);
            ok.Should().BeFalse();
        }

        [Fact]
        public void TryIntersect_AntiParallel_ReturnsFalse()
        {
            bool ok = CurveRemovalGeometry.TryIntersect(
                0, 0, Rad(30), 10, 20, Rad(210), out _, out _);
            ok.Should().BeFalse();
        }

        [Fact]
        public void TryIntersect_DirectionDifferenceBelowEpsilon_ReturnsFalse()
        {
            // sin(1e-13 rad) ≈ 1e-13 < ParallelDenominatorEpsilon (1e-12).
            bool ok = CurveRemovalGeometry.TryIntersect(
                0, 0, 0.5, 100, 100, 0.5 + 1e-13, out _, out _);
            ok.Should().BeFalse();
        }

        [Fact]
        public void TryIntersect_Perpendicular_FindsIntersection()
        {
            bool ok = CurveRemovalGeometry.TryIntersect(
                0, 0, Rad(0),     // east through the origin
                5, -5, Rad(90),   // north through (5, -5)
                out double ix, out double iy);
            ok.Should().BeTrue();
            ix.Should().BeApproximately(5.0, 1e-9);
            iy.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void TryIntersect_TinyDeflection_FindsFarButValidPi()
        {
            // Realistic curve-removal case: two nearly-collinear tangents with a
            // 0.25° deflection (Table 5.5 "curve unnecessary"). The prev tangent
            // runs east from (0,0); the next tangent leaves the design PI at
            // (100, 0) with bearing 0.25° and its outer end 100 m further on.
            double nextDir = Rad(0.25);
            double nextEndX = 100.0 + 100.0 * Math.Cos(nextDir);
            double nextEndY = 100.0 * Math.Sin(nextDir);

            bool ok = CurveRemovalGeometry.TryIntersect(
                0, 0, 0,
                nextEndX, nextEndY, nextDir,
                out double ix, out double iy);

            ok.Should().BeTrue("a 0.25° deflection is near-parallel but still has a well-defined PI");
            ix.Should().BeApproximately(100.0, 1e-6);
            iy.Should().BeApproximately(0.0, 1e-6);
        }

        [Fact]
        public void TryIntersect_TinyDeflectionPi_PassesForwardSanityChecks()
        {
            // The PI computed for a valid tiny-deflection case must satisfy the
            // same sanity predicates the tool applies: forward of the prev
            // tangent's outer start, and behind the next tangent's outer end.
            double nextDir = Rad(0.25);
            double nextEndX = 100.0 + 100.0 * Math.Cos(nextDir);
            double nextEndY = 100.0 * Math.Sin(nextDir);

            CurveRemovalGeometry.TryIntersect(
                0, 0, 0, nextEndX, nextEndY, nextDir,
                out double ix, out double iy).Should().BeTrue();

            CurveRemovalGeometry.IsForwardOf(0, 0, 0, ix, iy)
                .Should().BeTrue("the PI lies forward of the prev tangent's outer start");
            CurveRemovalGeometry.IsForwardOf(ix, iy, nextDir, nextEndX, nextEndY)
                .Should().BeTrue("the next tangent's outer end lies forward of the PI");
        }

        // ── IsForwardOf ──────────────────────────────────────────────────────

        [Fact]
        public void IsForwardOf_PointAhead_ReturnsTrue()
        {
            CurveRemovalGeometry.IsForwardOf(0, 0, Rad(0), 5, 3).Should().BeTrue();
        }

        [Fact]
        public void IsForwardOf_PointBehind_ReturnsFalse()
        {
            CurveRemovalGeometry.IsForwardOf(0, 0, Rad(0), -5, 3).Should().BeFalse();
        }

        [Fact]
        public void IsForwardOf_PerpendicularPoint_ReturnsFalse()
        {
            // Dot product exactly zero — not strictly forward.
            CurveRemovalGeometry.IsForwardOf(0, 0, Rad(0), 0, 7).Should().BeFalse();
        }

        [Fact]
        public void IsForwardOf_RespectsDirection_NotJustPosition()
        {
            // Same points, reversed direction flips the answer.
            CurveRemovalGeometry.IsForwardOf(10, 10, Rad(180), 3, 10).Should().BeTrue();
            CurveRemovalGeometry.IsForwardOf(10, 10, Rad(0), 3, 10).Should().BeFalse();
        }
    }
}
