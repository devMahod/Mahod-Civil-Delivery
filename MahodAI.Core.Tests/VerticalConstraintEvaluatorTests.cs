using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Profile;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Pure-math tests for the P0-04 vertical-curve constraint evaluator: K/R computation,
    /// crest/sag classification, and the min-K / min-R feasibility gate that
    /// OptimizeProfilePvisTool now actually enforces.
    /// </summary>
    public class VerticalConstraintEvaluatorTests
    {
        [Fact]
        public void Crest_SymmetricGrades_ComputesKandR()
        {
            // +2% into -2% over L=100 m: A = 0.04 (4%). K = 100/4 = 25 m/%, R = 100/0.04 = 2500 m.
            var r = VerticalConstraintEvaluator.Evaluate(1, gradeInDecimal: 0.02, gradeOutDecimal: -0.02, curveLengthM: 100);

            r.IsCrest.Should().BeTrue();
            r.IsSag.Should().BeFalse();
            r.AlgebraicGradeDiffPercent.Should().BeApproximately(4.0, 1e-9);
            r.KValue.Should().BeApproximately(25.0, 1e-9);
            r.RadiusM.Should().BeApproximately(2500.0, 1e-6);
            r.HasCurve.Should().BeTrue();
            // R = K * 100 always holds.
            (r.RadiusM!.Value / r.KValue!.Value).Should().BeApproximately(100.0, 1e-6);
        }

        [Fact]
        public void Sag_RisingGradeDifference_ClassifiedAsSag()
        {
            // -3% into +1%: gradeOut > gradeIn → sag. A = 0.04.
            var r = VerticalConstraintEvaluator.Evaluate(2, -0.03, 0.01, 80);

            r.IsSag.Should().BeTrue();
            r.IsCrest.Should().BeFalse();
            r.AlgebraicGradeDiffPercent.Should().BeApproximately(4.0, 1e-9);
            r.KValue.Should().BeApproximately(20.0, 1e-9); // 80 / 4
        }

        [Fact]
        public void StraightGrade_NoCurve_NullKR_AlwaysSatisfies()
        {
            var r = VerticalConstraintEvaluator.Evaluate(1, 0.03, 0.03, 50);

            r.HasCurve.Should().BeFalse();
            r.KValue.Should().BeNull();
            r.RadiusM.Should().BeNull();
            // A straight grade has no vertical curve, so any minimum passes.
            VerticalConstraintEvaluator.SatisfiesCurvature(r, minK: 100, minRadiusM: 10000).Should().BeTrue();
        }

        [Fact]
        public void SatisfiesCurvature_EnforcesMinK()
        {
            var r = VerticalConstraintEvaluator.Evaluate(1, 0.02, -0.02, 100); // K = 25
            VerticalConstraintEvaluator.SatisfiesCurvature(r, minK: 20, minRadiusM: 0).Should().BeTrue();
            VerticalConstraintEvaluator.SatisfiesCurvature(r, minK: 30, minRadiusM: 0).Should().BeFalse();
        }

        [Fact]
        public void SatisfiesCurvature_EnforcesMinRadius()
        {
            var r = VerticalConstraintEvaluator.Evaluate(1, 0.02, -0.02, 100); // R = 2500
            VerticalConstraintEvaluator.SatisfiesCurvature(r, minK: 0, minRadiusM: 2000).Should().BeTrue();
            VerticalConstraintEvaluator.SatisfiesCurvature(r, minK: 0, minRadiusM: 3000).Should().BeFalse();
        }

        [Fact]
        public void SatisfiesCurvature_NoConstraint_AlwaysPasses()
        {
            var r = VerticalConstraintEvaluator.Evaluate(1, 0.05, -0.05, 10); // sharp: K = 1
            VerticalConstraintEvaluator.SatisfiesCurvature(r, minK: 0, minRadiusM: 0).Should().BeTrue();
        }

        [Fact]
        public void ShorterCurve_LowerK_FailsWhereLongerPasses()
        {
            // Same grade break, half the length → half the K. Enforcement must catch it.
            var longCurve = VerticalConstraintEvaluator.Evaluate(1, 0.02, -0.02, 100); // K=25
            var shortCurve = VerticalConstraintEvaluator.Evaluate(1, 0.02, -0.02, 50); // K=12.5

            VerticalConstraintEvaluator.SatisfiesCurvature(longCurve, minK: 20, minRadiusM: 0).Should().BeTrue();
            VerticalConstraintEvaluator.SatisfiesCurvature(shortCurve, minK: 20, minRadiusM: 0).Should().BeFalse();
        }
    }
}
