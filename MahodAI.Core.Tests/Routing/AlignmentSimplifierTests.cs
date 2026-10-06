using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Pure-geometry tests for the post-fit straightening pass. No AutoCAD host types,
    /// so they run in the normal unit-test pass (unlike the Acdbmgd-dependent tools).
    /// </summary>
    public class AlignmentSimplifierTests
    {
        private static readonly Func<Pt2, Pt2, bool> AlwaysViable = (_, _) => true;
        private static readonly Func<Pt2, Pt2, bool> NeverViable = (_, _) => false;

        [Fact]
        public void Straightens_a_near_collinear_run_to_its_endpoints()
        {
            // A straight road the densifier emitted as a chain of small wobbles
            // (each deflection ≈ 3°, below the collinear threshold).
            var pis = new[]
            {
                new Pt2(0, 0), new Pt2(10, 0.3), new Pt2(20, 0.0),
                new Pt2(30, 0.3), new Pt2(40, 0.0),
            };

            var result = AlignmentSimplifier.Straighten(pis, designRadiusM: 0, spiralLenM: 0, AlwaysViable);

            result.Should().HaveCount(2);
            result[0].Should().Be(new Pt2(0, 0));
            result[^1].Should().Be(new Pt2(40, 0.0));
        }

        [Fact]
        public void Preserves_a_real_corner()
        {
            // A 90° turn is a genuine PI — never dropped.
            var pis = new[] { new Pt2(0, 0), new Pt2(100, 0), new Pt2(100, 100) };

            var result = AlignmentSimplifier.Straighten(pis, designRadiusM: 200, spiralLenM: 0, AlwaysViable);

            result.Should().HaveCount(3);
            result.Should().Contain(new Pt2(100, 0));
        }

        [Fact]
        public void Never_removes_a_pi_whose_bypass_chord_is_off_mask()
        {
            // Even a near-collinear PI stays when the straight bypass would leave the
            // surface (chordViable=false) — straightening must not push geometry off-mask.
            var pis = new[] { new Pt2(0, 0), new Pt2(10, 0.3), new Pt2(20, 0.0) };

            var result = AlignmentSimplifier.Straighten(pis, designRadiusM: 0, spiralLenM: 0, NeverViable);

            result.Should().HaveCount(3);
        }

        [Fact]
        public void Keeps_minor_turns_when_no_radius_constraint_but_merges_them_under_one()
        {
            // Two ~10° turns 10 m apart. They are NOT near-collinear (10° > ~4.5°), so
            // with no design radius they survive; with a large radius their curves can't
            // both fit on the 10 m segment, so the redundant minor PIs are merged out.
            double a = 10.0 * Math.PI / 180.0;
            var p2 = new Pt2(100 + 10 * Math.Cos(a), 10 * Math.Sin(a));
            var p3 = new Pt2(p2.X + 100, p2.Y);
            var pis = new[] { new Pt2(0, 0), new Pt2(100, 0), p2, p3 };

            var keepAll = AlignmentSimplifier.Straighten(pis, designRadiusM: 0, spiralLenM: 0, AlwaysViable);
            keepAll.Should().HaveCount(4, "minor turns are not collinear and there is no radius to violate");

            var merged = AlignmentSimplifier.Straighten(pis, designRadiusM: 200, spiralLenM: 0, AlwaysViable);
            merged.Length.Should().BeLessThan(4, "the design-radius curves can't both fit the short tangent");
            merged[0].Should().Be(new Pt2(0, 0));
            merged[^1].Should().Be(p3);
        }

        [Fact]
        public void Never_merges_a_sharp_turn_even_when_its_tangent_is_too_short()
        {
            // A sharp (~72°) turn whose curve cannot fit at R=500 must still be preserved —
            // only MINOR turns (≤ ~15°) are merge candidates. Better a relaxed-radius curve
            // than a deleted real bend.
            var pis = new[] { new Pt2(0, 0), new Pt2(100, 0), new Pt2(110, 30), new Pt2(210, 30) };

            var result = AlignmentSimplifier.Straighten(pis, designRadiusM: 500, spiralLenM: 0, AlwaysViable);

            result.Should().Contain(new Pt2(110, 30));
        }

        [Fact]
        public void Honors_maxTangentM_split_on_a_straight_run()
        {
            // 19 collinear PIs along a 180 m straight. With maxTangentM=50 the pass
            // collapses fine wobble but must never merge into a chord > 50 m, so the
            // deliberate tangent split survives (≥4 PIs) and no tangent exceeds 50 m.
            var pts = new System.Collections.Generic.List<Pt2>();
            for (int x = 0; x <= 180; x += 10) pts.Add(new Pt2(x, 0));
            var pis = pts.ToArray();

            var result = AlignmentSimplifier.Straighten(
                pis, designRadiusM: 0, spiralLenM: 0, AlwaysViable, maxTangentM: 50);

            result.Length.Should().BeGreaterThanOrEqualTo(4);
            result.Length.Should().BeLessThan(pis.Length);
            for (int i = 0; i < result.Length - 1; i++)
                result[i].DistanceTo(result[i + 1]).Should().BeLessThanOrEqualTo(50.0 + 1e-6);
        }

        [Fact]
        public void Returns_short_inputs_unchanged()
        {
            var two = new[] { new Pt2(0, 0), new Pt2(50, 50) };
            AlignmentSimplifier.Straighten(two, 200, 0, AlwaysViable).Should().Equal(two);
        }
    }
}
