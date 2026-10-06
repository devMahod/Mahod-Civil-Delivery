using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    public class CurveAttacherTests
    {
        // Stub that simulates Civil 3D's AddFreeCurve / AddFreeSCS feasibility checks.
        //   Arc fits if 2R ≤ shorter-adjacent-tangent length.
        //   SCS fits if 2R + 2L ≤ shorter tangent, where L is the per-side spiral length.
        // Tracks the actual radius and spiral length each call placed so tests can assert
        // both — the "spiral-length relax" path is invisible without that.
        private sealed class StubAlignment
        {
            public List<(Pt2 s, Pt2 e)> Lines { get; } = new();
            public List<(int prev, int next, double radius)> Curves { get; } = new();
            public List<(int prev, int next, double radius, double spiralLengthM)> Scs { get; } = new();

            public int AddLine(Pt2 s, Pt2 e)
            {
                Lines.Add((s, e));
                return Lines.Count - 1;
            }

            public bool TryCurve(int prev, int next, double radius)
            {
                var (s1, e1) = Lines[prev];
                var (s2, e2) = Lines[next];
                double minTan = System.Math.Min(s1.DistanceTo(e1), s2.DistanceTo(e2));
                bool fits = radius * 2.0 <= minTan;
                if (fits) Curves.Add((prev, next, radius));
                return fits;
            }

            public bool TryScs(int prev, int next, double radius, double spiralLengthM)
            {
                var (s1, e1) = Lines[prev];
                var (s2, e2) = Lines[next];
                double minTan = System.Math.Min(s1.DistanceTo(e1), s2.DistanceTo(e2));
                bool fits = (radius * 2.0 + spiralLengthM * 2.0) <= minTan;
                if (fits) Scs.Add((prev, next, radius, spiralLengthM));
                return fits;
            }
        }

        [Fact]
        public void Attach_TightTurn_RelaxesAndWarnsWhenNoFit()
        {
            // 6m tangents at a 90° turn. r=50 needs 100m tangent → fails.
            // Halving down: 50→25→12.5→6.25→3.125. Only r ≤ 3 fits (2*3=6 ≤ 6).
            // With rFloor=5, 5 doesn't fit (2*5=10 > 6) → no curve placed.
            var pis = new[]
            {
                new Pt2(0, 0),
                new Pt2(6, 0),
                new Pt2(6, 6),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 50, rFloor: 5,
                stub.AddLine, stub.TryCurve);

            result.Success.Should().BeTrue();
            stub.Lines.Should().HaveCount(2);
            stub.Curves.Should().BeEmpty();
            result.Warnings.Should().HaveCount(1);
            result.Warnings[0].Reason.Should().Contain("no curve achievable");
        }

        [Fact]
        public void Attach_GenerousTangents_PlacesCurveAtOriginalRadius()
        {
            // 100m tangents, requested R=30 → fits (2R=60 ≤ 100).
            var pis = new[]
            {
                new Pt2(0,   0),
                new Pt2(100, 0),
                new Pt2(100, 100),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve);

            result.Success.Should().BeTrue();
            stub.Curves.Should().ContainSingle();
            stub.Curves[0].radius.Should().Be(30);
            result.Warnings.Should().BeEmpty();
        }

        [Fact]
        public void Attach_RelaxesOnceWhenOriginalDoesntFit()
        {
            // 50m tangents, requested 40 → 2R=80 > 50 fails. r=20 → 2R=40 ≤ 50 fits.
            var pis = new[]
            {
                new Pt2(0,   0),
                new Pt2(50,  0),
                new Pt2(50,  50),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 40, rFloor: 10,
                stub.AddLine, stub.TryCurve);

            result.Success.Should().BeTrue();
            stub.Curves.Should().ContainSingle();
            stub.Curves[0].radius.Should().BeLessThan(40);
            result.Warnings.Should().ContainSingle();
            result.Warnings[0].RequestedM.Should().Be(40);
            result.Warnings[0].AchievedM.Should().BeLessThan(40);
            result.Warnings[0].AchievedM.Should().BeGreaterThan(0);
        }

        [Fact]
        public void Attach_ScsFitsAtFullRadius_PlacesScs()
        {
            // 200m tangents at 90°. R=30 with L=25 needs 60+50=110 ≤ 200 — fits at full
            // radius and full spiral length on the first try, no warnings expected.
            var pis = new[]
            {
                new Pt2(0,   0),
                new Pt2(200, 0),
                new Pt2(200, 200),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve, stub.TryScs, spiralLengthM: 25.0);

            result.Success.Should().BeTrue();
            result.ScsCount.Should().Be(1);
            result.ArcCount.Should().Be(0);
            stub.Scs.Should().ContainSingle();
            stub.Scs[0].radius.Should().Be(30);
            stub.Scs[0].spiralLengthM.Should().Be(25.0);
            stub.Curves.Should().BeEmpty();
            result.Warnings.Should().BeEmpty();
        }

        [Fact]
        public void Attach_FullSpiralFails_HalvedSpiralFits_PlacesScsAtFullRadius()
        {
            // 85m tangents. R=30 with L=25 needs 60+50=110 > 85 (fail).
            // Halve spiral: R=30 with L=12.5 needs 60+25=85 — fits exactly.
            // Expect SCS placed at full radius, with warning noting the shortened spiral.
            var pis = new[]
            {
                new Pt2(0,  0),
                new Pt2(85, 0),
                new Pt2(85, 85),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve, stub.TryScs, spiralLengthM: 25.0);

            result.Success.Should().BeTrue();
            result.ScsCount.Should().Be(1);
            result.ArcCount.Should().Be(0);
            stub.Scs.Should().ContainSingle();
            stub.Scs[0].radius.Should().Be(30);          // full radius preserved
            stub.Scs[0].spiralLengthM.Should().Be(12.5); // spiral halved
            stub.Curves.Should().BeEmpty();
            result.Warnings.Should().ContainSingle();
            result.Warnings[0].PlacedAs.Should().Be("SCS");
            result.Warnings[0].AchievedM.Should().Be(30);
            result.Warnings[0].Reason.Should().Contain("spiral");
        }

        [Fact]
        public void Attach_AllSpiralLengthsFail_FallsBackToArcAtSameRadius()
        {
            // 70m tangents, R=30, spiral=25. Full + halved spirals all fail (need 110, 85, 72.5
            // > 70), so fall back to plain Arc at full radius (60 ≤ 70 fits). Verifies the
            // Arc-fallback path is still reachable when *no* spiral length can host the SCS.
            var pis = new[]
            {
                new Pt2(0,  0),
                new Pt2(70, 0),
                new Pt2(70, 70),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve, stub.TryScs, spiralLengthM: 25.0);

            result.Success.Should().BeTrue();
            result.ScsCount.Should().Be(0);
            result.ArcCount.Should().Be(1);
            stub.Scs.Should().BeEmpty();
            stub.Curves.Should().ContainSingle();
            stub.Curves[0].radius.Should().Be(30);
            result.Warnings.Should().ContainSingle();
            result.Warnings[0].PlacedAs.Should().Be("Arc");
            result.Warnings[0].Reason.Should().Contain("SCS rejected");
        }

        [Fact]
        public void Attach_BothFailAtFullRadius_RelaxesUntilScsFits()
        {
            // 70m tangents. spiralLengthM=5 (so minSpiral floor is 5 — only one spiral length tried per radius).
            // R=50 with L=5: needs 100+10=110 > 70, fail. Arc R=50 needs 100 > 70, fail. Halve R.
            // R=25 with L=5: needs 50+10=60 ≤ 70, fits. SCS placed at relaxed radius.
            var pis = new[]
            {
                new Pt2(0,  0),
                new Pt2(70, 0),
                new Pt2(70, 70),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 50, rFloor: 10,
                stub.AddLine, stub.TryCurve, stub.TryScs, spiralLengthM: 5.0);

            result.Success.Should().BeTrue();
            result.ScsCount.Should().Be(1);
            result.ArcCount.Should().Be(0);
            stub.Scs.Should().ContainSingle();
            stub.Scs[0].radius.Should().Be(25);
            stub.Scs[0].spiralLengthM.Should().Be(5.0);
            result.Warnings.Should().ContainSingle();
            result.Warnings[0].PlacedAs.Should().Be("SCS");
            result.Warnings[0].AchievedM.Should().Be(25);
        }

        // ── P0-03: HorizontalDesignPolicy (strict vs relaxed) ──────────────────

        private static Pt2[] RightAngle(double tangent) => new[]
        {
            new Pt2(0, 0),
            new Pt2(tangent, 0),
            new Pt2(tangent, tangent),
        };

        [Fact]
        public void Strict_ExactRadiusFits_SucceedsWithNoDeviation()
        {
            // 100 m tangents, R=30 → 2R=60 ≤ 100 fits at the requested radius.
            var stub = new StubAlignment();
            var result = CurveAttacher.Attach(RightAngle(100), radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve,
                policy: CurveAttacher.HorizontalDesignPolicy.Strict);

            result.Success.Should().BeTrue();
            result.HasDeviations.Should().BeFalse();
            result.RequiresEngineerApproval.Should().BeFalse();
            stub.Curves.Should().ContainSingle();
            stub.Curves[0].radius.Should().Be(30);
            result.Warnings.Should().BeEmpty();
        }

        [Fact]
        public void Strict_RadiusWouldNeedRelaxation_RejectsInsteadOfHalving()
        {
            // 50 m tangents, R=40 → 2R=80 > 50. Strict may NOT halve → obligatory PI, hard fail.
            var stub = new StubAlignment();
            var result = CurveAttacher.Attach(RightAngle(50), radius: 40, rFloor: 10,
                stub.AddLine, stub.TryCurve,
                policy: CurveAttacher.HorizontalDesignPolicy.Strict);

            result.Success.Should().BeFalse();
            result.HasDeviations.Should().BeTrue();
            result.FailureReason.Should().Contain("obligatory");
            stub.Curves.Should().BeEmpty();
        }

        [Fact]
        public void Strict_ScsNeedsShortenedSpiralOrArc_Rejects()
        {
            // 85 m tangents, R=30, L=25 → SCS needs 60+50=110 > 85 (fail). Strict forbids
            // spiral shortening AND the SCS→arc fallback, even though a plain arc (60 ≤ 85)
            // would fit. Result: reject, nothing placed.
            var stub = new StubAlignment();
            var result = CurveAttacher.Attach(RightAngle(85), radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve, stub.TryScs, spiralLengthM: 25.0,
                policy: CurveAttacher.HorizontalDesignPolicy.Strict);

            result.Success.Should().BeFalse();
            stub.Scs.Should().BeEmpty();
            stub.Curves.Should().BeEmpty();   // arc fallback forbidden in strict mode
        }

        [Fact]
        public void Strict_ScsFitsAtFullRadiusAndSpiral_Succeeds()
        {
            // 200 m tangents, R=30, L=25 → SCS 60+50=110 ≤ 200 fits exactly as requested.
            var stub = new StubAlignment();
            var result = CurveAttacher.Attach(RightAngle(200), radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve, stub.TryScs, spiralLengthM: 25.0,
                policy: CurveAttacher.HorizontalDesignPolicy.Strict);

            result.Success.Should().BeTrue();
            result.HasDeviations.Should().BeFalse();
            result.ScsCount.Should().Be(1);
            stub.Scs[0].radius.Should().Be(30);
            stub.Scs[0].spiralLengthM.Should().Be(25.0);
        }

        [Fact]
        public void Relaxed_Deviation_FlagsRequiresEngineerApproval()
        {
            // 50 m tangents, R=40 → relaxed halving places a curve below the requested radius.
            // The deviation must be exposed: HasDeviations + RequiresEngineerApproval.
            var stub = new StubAlignment();
            var result = CurveAttacher.Attach(RightAngle(50), radius: 40, rFloor: 10,
                stub.AddLine, stub.TryCurve,
                policy: CurveAttacher.HorizontalDesignPolicy.Relaxed);

            result.Success.Should().BeTrue();
            result.HasDeviations.Should().BeTrue();
            result.RequiresEngineerApproval.Should().BeTrue();
            stub.Curves.Should().ContainSingle();
            stub.Curves[0].radius.Should().BeLessThan(40);
        }

        [Fact]
        public void Attach_NullScsDelegate_BehavesLikeBefore()
        {
            // Same setup as Attach_GenerousTangents_PlacesCurveAtOriginalRadius — passing null
            // for tryAddScs must not change the warning shape or the curve placement.
            var pis = new[]
            {
                new Pt2(0,   0),
                new Pt2(100, 0),
                new Pt2(100, 100),
            };
            var stub = new StubAlignment();

            var result = CurveAttacher.Attach(pis, radius: 30, rFloor: 10,
                stub.AddLine, stub.TryCurve, tryAddScs: null);

            result.Success.Should().BeTrue();
            result.ScsCount.Should().Be(0);
            result.ArcCount.Should().Be(1);
            stub.Curves.Should().ContainSingle();
            result.Warnings.Should().BeEmpty();
        }
    }
}
