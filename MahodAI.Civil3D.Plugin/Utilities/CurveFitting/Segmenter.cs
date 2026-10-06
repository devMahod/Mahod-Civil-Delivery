using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    public enum SegmentKind { Tangent, Curve }

    public sealed class Segment
    {
        public SegmentKind Kind { get; init; }
        public int StartIndex { get; init; }
        public int EndIndex { get; init; }                  // inclusive
        public int Sign { get; init; }                       // for curves: +1 CCW, -1 CW
    }

    /// <summary>
    /// Classifies each sample as Tangent (|κ| below threshold) or Curve and groups
    /// consecutive same-class samples into runs. Runs shorter than the minimum length
    /// are merged into the neighbour to avoid spurious micro-segments from chord noise.
    /// </summary>
    public static class Segmenter
    {
        public static IReadOnlyList<Segment> Segment(
            Pt2D[] pts,
            double[] curvature,
            double tangentCurvatureThreshold = 2e-4,
            double minSegmentLengthM = 5.0)
        {
            if (pts == null || pts.Length < 2 || curvature == null || curvature.Length != pts.Length)
                return Array.Empty<Segment>();

            // Per-sample classification.
            var kinds = new SegmentKind[pts.Length];
            var signs = new int[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                double k = curvature[i];
                if (Math.Abs(k) < tangentCurvatureThreshold) { kinds[i] = SegmentKind.Tangent; signs[i] = 0; }
                else { kinds[i] = SegmentKind.Curve; signs[i] = k > 0 ? +1 : -1; }
            }

            // Build initial runs (also split curves on sign change).
            var runs = new List<Segment>();
            int s = 0;
            for (int i = 1; i <= pts.Length; i++)
            {
                bool boundary = i == pts.Length
                    || kinds[i] != kinds[s]
                    || (kinds[i] == SegmentKind.Curve && signs[i] != signs[s]);
                if (boundary)
                {
                    runs.Add(new Segment { Kind = kinds[s], StartIndex = s, EndIndex = i - 1, Sign = signs[s] });
                    s = i;
                }
            }

            // Merge short runs into the longer neighbour. Iterate until stable.
            bool changed = true;
            while (changed && runs.Count > 1)
            {
                changed = false;
                for (int i = 0; i < runs.Count; i++)
                {
                    double len = ArcLength(pts, runs[i].StartIndex, runs[i].EndIndex);
                    if (len >= minSegmentLengthM) continue;

                    int prev = i - 1, next = i + 1;
                    int target;
                    if (prev < 0) target = next;
                    else if (next >= runs.Count) target = prev;
                    else
                    {
                        double lp = ArcLength(pts, runs[prev].StartIndex, runs[prev].EndIndex);
                        double ln = ArcLength(pts, runs[next].StartIndex, runs[next].EndIndex);
                        target = lp >= ln ? prev : next;
                    }
                    if (target < 0 || target >= runs.Count) continue;

                    var merged = new Segment
                    {
                        Kind = runs[target].Kind,
                        Sign = runs[target].Sign,
                        StartIndex = Math.Min(runs[i].StartIndex, runs[target].StartIndex),
                        EndIndex = Math.Max(runs[i].EndIndex, runs[target].EndIndex),
                    };
                    int lo = Math.Min(i, target);
                    int hi = Math.Max(i, target);
                    runs.RemoveAt(hi);
                    runs.RemoveAt(lo);
                    runs.Insert(lo, merged);
                    changed = true;
                    break;
                }
            }

            // Coalesce adjacent runs of the same kind+sign (post-merge can leave them).
            var coalesced = new List<Segment>();
            foreach (var r in runs)
            {
                if (coalesced.Count > 0
                    && coalesced[coalesced.Count - 1].Kind == r.Kind
                    && coalesced[coalesced.Count - 1].Sign == r.Sign)
                {
                    var prev = coalesced[coalesced.Count - 1];
                    coalesced[coalesced.Count - 1] = new Segment
                    {
                        Kind = prev.Kind,
                        Sign = prev.Sign,
                        StartIndex = prev.StartIndex,
                        EndIndex = r.EndIndex,
                    };
                }
                else coalesced.Add(r);
            }
            return coalesced;
        }

        private static double ArcLength(Pt2D[] pts, int i0, int i1)
        {
            double sum = 0;
            for (int i = i0; i < i1; i++) sum += pts[i].DistanceTo(pts[i + 1]);
            return sum;
        }
    }
}
