using System;

namespace MahodAI.Civil3D.Plugin.Tools.Profile
{
    /// <summary>
    /// P0-04: pure vertical-curve geometry, independent of any Autodesk API so it is
    /// unit-testable. Computes the algebraic grade difference, crest/sag type, achieved
    /// K-value and parabolic radius for a PVI, and decides whether a required minimum
    /// K and/or R is satisfied.
    ///
    /// Conventions (single, documented unit system — the review flagged that the tool
    /// previously mixed R/K/T2 without definitions):
    ///  • grades are DECIMAL rise/run (0.03 = 3%);
    ///  • A = |gradeOut − gradeIn| is the algebraic grade difference; A% = A×100;
    ///  • K = L / A%  (metres of curve per percent of grade change — the AASHTO/Israeli
    ///    K value); L is the vertical curve length (the tool's per-PVI T2);
    ///  • R = L / A  (the equivalent parabolic vertical radius) = K × 100.
    /// A near-zero A (a straight grade through the PVI) has no vertical curve and imposes
    /// no K/R constraint.
    /// </summary>
    public static class VerticalConstraintEvaluator
    {
        private const double GradeEpsilon = 1e-9;

        public sealed class PviConstraintReport
        {
            public int Index { get; init; }
            public double GradeInDecimal { get; init; }
            public double GradeOutDecimal { get; init; }
            /// <summary>Algebraic grade difference in PERCENT (|gradeOut − gradeIn| × 100).</summary>
            public double AlgebraicGradeDiffPercent { get; init; }
            public bool IsCrest { get; init; }
            public bool IsSag { get; init; }
            /// <summary>Vertical curve length L (m) — the tool's T2.</summary>
            public double CurveLengthM { get; init; }
            /// <summary>Achieved K = L / A% (m per %). Null when the grade is effectively straight.</summary>
            public double? KValue { get; init; }
            /// <summary>Achieved parabolic radius R = L / A (m). Null when straight.</summary>
            public double? RadiusM { get; init; }
            /// <summary>True when there is a real vertical curve (A above epsilon).</summary>
            public bool HasCurve => KValue.HasValue;
        }

        /// <summary>
        /// Evaluates a single PVI given its incoming/outgoing grades (decimal) and vertical
        /// curve length L (m).
        /// </summary>
        public static PviConstraintReport Evaluate(
            int index, double gradeInDecimal, double gradeOutDecimal, double curveLengthM)
        {
            double aDecimal = Math.Abs(gradeOutDecimal - gradeInDecimal);
            bool straight = aDecimal < GradeEpsilon;
            double aPercent = aDecimal * 100.0;

            double? k = straight ? (double?)null : curveLengthM / aPercent;
            double? r = straight ? (double?)null : curveLengthM / aDecimal;

            return new PviConstraintReport
            {
                Index = index,
                GradeInDecimal = gradeInDecimal,
                GradeOutDecimal = gradeOutDecimal,
                AlgebraicGradeDiffPercent = aPercent,
                IsCrest = gradeOutDecimal < gradeInDecimal - GradeEpsilon,
                IsSag = gradeOutDecimal > gradeInDecimal + GradeEpsilon,
                CurveLengthM = curveLengthM,
                KValue = k,
                RadiusM = r,
            };
        }

        /// <summary>
        /// True when the PVI meets the required minimum K and/or minimum radius. A value ≤ 0
        /// means "not required". A straight grade (no vertical curve) always passes.
        /// </summary>
        public static bool SatisfiesCurvature(PviConstraintReport report, double minK, double minRadiusM)
        {
            if (!report.HasCurve)
                return true; // straight grade — no vertical curve to constrain

            if (minK > 0 && report.KValue!.Value < minK - 1e-9)
                return false;
            if (minRadiusM > 0 && report.RadiusM!.Value < minRadiusM - 1e-9)
                return false;
            return true;
        }
    }
}
