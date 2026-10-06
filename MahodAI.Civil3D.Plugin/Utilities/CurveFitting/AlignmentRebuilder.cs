using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    public sealed class FittedSegment
    {
        public SegmentKind Kind { get; init; }
        public double StartArcLengthM { get; init; }
        public double EndArcLengthM { get; init; }
        public double? RadiusM { get; init; }
        public int Sign { get; init; }
        public double RmsResidualM { get; init; }
    }

    public sealed class RebuildPlan
    {
        public Pt2D[] Pis { get; init; } = Array.Empty<Pt2D>();
        public double[] InteriorRadii { get; init; } = Array.Empty<double>();
        public int[] InteriorSigns { get; init; } = Array.Empty<int>();
        public IReadOnlyList<FittedSegment> Diagnostics { get; init; } = Array.Empty<FittedSegment>();
        public double WorstRmsResidualM { get; init; }
        public string OverallQuality { get; init; } = "good";
        public string? FailureReason { get; init; }
    }

    /// <summary>
    /// Drives the full Resample → Curvature → Segment → Fit → PI pipeline and produces
    /// a <see cref="RebuildPlan"/> the tool can hand to the Civil 3D entity API.
    ///
    /// PI walk strategy: process segments in order, building an alternating
    /// Tangent–Curve–Tangent–Curve–…–Tangent token sequence. Tangent tokens come from
    /// fitted tangent runs OR are synthesized at curve boundaries (alignment ends, or
    /// curve-curve joints in S-shapes) using the chord direction at that boundary.
    /// PIs are then the intersections of consecutive tangent lines, with the radius
    /// of the curve token in between. This handles all edge cases: 0 tangent runs
    /// (one big curve), 1 tangent run (curve-tangent-curve S-shapes), reverse curves,
    /// and the standard tangent-curve-tangent-curve-tangent layout.
    /// </summary>
    public static class AlignmentRebuilder
    {
        public sealed class Options
        {
            public double SamplingStepM { get; init; } = 1.0;
            public int SmoothingWindow { get; init; } = 7;
            public double TangentCurvatureThreshold { get; init; } = 2e-4;
            public double MinSegmentLengthM { get; init; } = 5.0;
            public double MinArcRadiusM { get; init; } = 30.0;
        }

        public static RebuildPlan Build(IReadOnlyList<Pt2D> rawPoints, Options options)
        {
            options ??= new Options();
            if (rawPoints == null || rawPoints.Count < 2)
                return new RebuildPlan { FailureReason = "Need at least 2 input points." };

            var pts = Resampler.ToUniform(rawPoints, options.SamplingStepM);
            if (pts.Length < 3)
                return new RebuildPlan { FailureReason = "Resampled point count is too small to fit." };

            var curvature = CurvatureEstimator.Estimate(pts, options.SmoothingWindow);
            var segments = Segmenter.Segment(
                pts, curvature,
                options.TangentCurvatureThreshold,
                options.MinSegmentLengthM);

            if (segments.Count == 0)
                return new RebuildPlan { FailureReason = "Segmenter produced no runs." };

            // Fit each run.
            var fittedLines = new Dictionary<int, FittedLine>();
            var fittedCircles = new Dictionary<int, FittedCircle>();
            var diag = new List<FittedSegment>();

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                double s0 = ArcLengthAt(pts, seg.StartIndex);
                double s1 = ArcLengthAt(pts, seg.EndIndex);

                if (seg.Kind == SegmentKind.Tangent)
                {
                    var line = LineFit.Fit(pts, seg.StartIndex, seg.EndIndex);
                    if (line == null) continue;
                    fittedLines[i] = line;
                    diag.Add(new FittedSegment
                    {
                        Kind = SegmentKind.Tangent,
                        StartArcLengthM = s0,
                        EndArcLengthM = s1,
                        RmsResidualM = line.RmsResidualM,
                    });
                }
                else
                {
                    var circle = CircleFit.Fit(pts, seg.StartIndex, seg.EndIndex, seg.Sign);
                    if (circle == null) continue;
                    fittedCircles[i] = circle;
                    diag.Add(new FittedSegment
                    {
                        Kind = SegmentKind.Curve,
                        StartArcLengthM = s0,
                        EndArcLengthM = s1,
                        RadiusM = circle.Radius,
                        Sign = circle.Sign,
                        RmsResidualM = circle.RmsResidualM,
                    });
                }
            }

            // Walk: build an alternating tangent / curve token list. Starts and ends with
            // a tangent. Synthesize tangents at boundaries (alignment ends, curve-curve joints).
            var tangents = new List<FittedLine>();
            var curveRadii = new List<double>();
            var curveSigns = new List<int>();

            bool needTangent = true;                         // expect a tangent next
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg.Kind == SegmentKind.Tangent)
                {
                    if (!fittedLines.TryGetValue(i, out var line)) continue;
                    if (needTangent)
                    {
                        tangents.Add(line);
                        needTangent = false;
                    }
                    // adjacent T-T after merge is unusual; keep first, drop the rest
                }
                else  // Curve
                {
                    if (!fittedCircles.TryGetValue(i, out var circle)) continue;
                    if (needTangent)
                    {
                        // Synthesize an entry tangent at the curve's start.
                        tangents.Add(SynthesizeTangentAtCurveBoundary(pts, seg, circle, atStart: true));
                        needTangent = false;
                    }
                    curveRadii.Add(circle.Radius);
                    curveSigns.Add(circle.Sign);
                    needTangent = true;                       // need a closing tangent next
                }
            }

            // If we end "needing a tangent" (last token emitted was a curve), synthesize an exit tangent.
            if (needTangent && curveRadii.Count > 0)
            {
                int lastCurveIdx = -1;
                for (int i = segments.Count - 1; i >= 0; i--)
                {
                    if (segments[i].Kind == SegmentKind.Curve && fittedCircles.ContainsKey(i))
                    {
                        lastCurveIdx = i;
                        break;
                    }
                }
                if (lastCurveIdx >= 0)
                {
                    tangents.Add(SynthesizeTangentAtCurveBoundary(
                        pts, segments[lastCurveIdx], fittedCircles[lastCurveIdx], atStart: false));
                }
            }

            // Validate alternation.
            if (tangents.Count == 0)
                return new RebuildPlan { FailureReason = "No tangents could be built — fit produced no usable runs." };
            if (tangents.Count != curveRadii.Count + 1)
                return new RebuildPlan { FailureReason = $"Token-walk inconsistency: {tangents.Count} tangents vs {curveRadii.Count} curves (expected tangents = curves + 1)." };

            // Build PIs.
            var pis = new List<Pt2D>();
            var radii = new List<double>();
            var signs = new List<int>();

            pis.Add(ProjectOntoLine(pts[0], tangents[0]));
            for (int i = 0; i < curveRadii.Count; i++)
            {
                var pi = LineFit.Intersect(tangents[i], tangents[i + 1]);
                if (pi == null)
                {
                    // Parallel tangents — fall back to the midpoint of their origins.
                    pi = new Pt2D(
                        (tangents[i].Origin.X + tangents[i + 1].Origin.X) / 2,
                        (tangents[i].Origin.Y + tangents[i + 1].Origin.Y) / 2);
                }
                pis.Add(pi.Value);
                radii.Add(curveRadii[i]);
                signs.Add(curveSigns[i]);
            }
            pis.Add(ProjectOntoLine(pts[pts.Length - 1], tangents[tangents.Count - 1]));

            double worst = 0;
            foreach (var d in diag) if (d.RmsResidualM > worst) worst = d.RmsResidualM;
            string quality = worst < 0.05 ? "good" : worst < 0.20 ? "acceptable" : "poor";

            return new RebuildPlan
            {
                Pis = pis.ToArray(),
                InteriorRadii = radii.ToArray(),
                InteriorSigns = signs.ToArray(),
                Diagnostics = diag,
                WorstRmsResidualM = worst,
                OverallQuality = quality,
            };
        }

        private static double ArcLengthAt(Pt2D[] pts, int idx)
        {
            double sum = 0;
            for (int i = 0; i < idx; i++) sum += pts[i].DistanceTo(pts[i + 1]);
            return sum;
        }

        private static Pt2D ProjectOntoLine(Pt2D p, FittedLine line)
        {
            double dx = p.X - line.Origin.X;
            double dy = p.Y - line.Origin.Y;
            double t = dx * line.Direction.X + dy * line.Direction.Y;
            return new Pt2D(line.Origin.X + t * line.Direction.X, line.Origin.Y + t * line.Direction.Y);
        }

        /// <summary>
        /// Builds a tangent line at a curve's entry or exit point. Tangent direction is
        /// perpendicular to the circle's radius vector; we pick the perpendicular whose
        /// dot product with the local chord direction (motion along the source polyline
        /// at this boundary) is positive — that selects the correct sign for both CW
        /// and CCW curves and survives noisy radius vectors near zero.
        /// </summary>
        private static FittedLine SynthesizeTangentAtCurveBoundary(
            Pt2D[] pts, Segment seg, FittedCircle c, bool atStart)
        {
            int idx = atStart ? seg.StartIndex : seg.EndIndex;
            if (idx < 0) idx = 0;
            if (idx >= pts.Length) idx = pts.Length - 1;

            // Local chord direction at this boundary (along the source polyline).
            Pt2D chord;
            if (atStart && idx + 1 < pts.Length)
                chord = new Pt2D(pts[idx + 1].X - pts[idx].X, pts[idx + 1].Y - pts[idx].Y);
            else if (!atStart && idx - 1 >= 0)
                chord = new Pt2D(pts[idx].X - pts[idx - 1].X, pts[idx].Y - pts[idx - 1].Y);
            else
                chord = new Pt2D(1, 0);

            Pt2D p = pts[idx];

            // Two perpendiculars to the radius vector — pick the one closer to the chord.
            double rx = p.X - c.Center.X;
            double ry = p.Y - c.Center.Y;
            Pt2D t1 = new(-ry, rx);
            Pt2D t2 = new(ry, -rx);
            double dot1 = t1.X * chord.X + t1.Y * chord.Y;
            double dot2 = t2.X * chord.X + t2.Y * chord.Y;
            Pt2D t = dot1 >= dot2 ? t1 : t2;

            double mag = Math.Sqrt(t.X * t.X + t.Y * t.Y);
            if (mag < 1e-9)
            {
                // Degenerate (point on centre) — fall back to chord direction.
                double cmag = Math.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
                if (cmag < 1e-12) return new FittedLine { Origin = p, Direction = new Pt2D(1, 0), RmsResidualM = 0 };
                return new FittedLine { Origin = p, Direction = new Pt2D(chord.X / cmag, chord.Y / cmag), RmsResidualM = 0 };
            }
            return new FittedLine { Origin = p, Direction = new Pt2D(t.X / mag, t.Y / mag), RmsResidualM = 0 };
        }
    }
}
