using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Pure shape gate for CL-layer geometry. A section instruction is one open,
    /// straight cross-alignment chord. A closed polygon cannot express the two ends
    /// of a transverse cut and is therefore drafting annotation. An open bent shape
    /// remains an explicit review item: shape alone is not evidence that it is noise.
    /// </summary>
    public static class SectionClCandidateLogic
    {
        public enum Decision { Straight, CollapseToChord, Degenerate, IgnoreClosedNonInstruction }

        public sealed record Result(Decision Kind, double ChordLength, double MaxSagitta);

        public static Result Analyze(
            IReadOnlyList<(double X, double Y)> vertices,
            double straightSagittaTolerance,
            bool isClosed = false)
        {
            if (vertices == null || vertices.Count < 2 ||
                !double.IsFinite(straightSagittaTolerance) || straightSagittaTolerance < 0)
                return new Result(Decision.Degenerate, 0, double.NaN);

            // A transverse instruction must have two distinct, open ends. This is
            // topology evidence, unlike an observed length or handle convention.
            if (isClosed)
                return new Result(Decision.IgnoreClosedNonInstruction, 0, double.NaN);

            var first = vertices[0];
            var last = vertices[^1];
            var dx = last.X - first.X;
            var dy = last.Y - first.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(chord) || chord < 0.001)
                return new Result(Decision.Degenerate, chord, double.NaN);
            if (vertices.Count == 2)
                return new Result(Decision.Straight, chord, 0);

            var sagitta = vertices.Max(point =>
                Math.Abs(dy * point.X - dx * point.Y +
                         last.X * first.Y - last.Y * first.X) / chord);
            return sagitta <= straightSagittaTolerance
                ? new Result(Decision.CollapseToChord, chord, sagitta)
                : new Result(Decision.Degenerate, chord, sagitta);
        }
    }
}
