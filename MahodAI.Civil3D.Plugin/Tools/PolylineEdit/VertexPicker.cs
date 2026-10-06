using System;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Resolves a mouse pick to a vertex or a segment. Pure (no AutoCAD types).
    ///
    /// Deliberate difference from the originals: PLTOOLS' <c>PL-VxDel</c> and MAHOD-PL's
    /// <c>VDEL</c> both resolve "which vertex did the user mean" by rounding the curve
    /// PARAMETER — <c>(fix (+ 0.5 par))</c> — so a click 60 % along a 200 m segment deletes the
    /// far vertex 80 m away rather than the near one. Here the pick resolves by true distance to
    /// the vertices, which is what the engineer means by "click near the vertex".
    /// </summary>
    public static class VertexPicker
    {
        /// <summary>A vertex the user picked.</summary>
        public sealed record VertexHit(int Index, Pt2 Point, double Distance);

        /// <summary>A point on a segment the user picked.</summary>
        /// <param name="SegmentIndex">Index of the segment (= index of its start vertex).</param>
        /// <param name="T">Normalised position along the segment, 0..1.</param>
        /// <param name="Point">The projected point itself.</param>
        /// <param name="Distance">Distance from the pick to <paramref name="Point"/>.</param>
        public sealed record SegmentHit(int SegmentIndex, double T, Pt2 Point, double Distance);

        /// <summary>
        /// Vertex nearest to <paramref name="pick"/> by true planar distance, or null for an
        /// empty shape.
        /// </summary>
        public static VertexHit? NearestVertex(PlShape shape, Pt2 pick)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.Count == 0) return null;

            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < shape.Count; i++)
            {
                double d = shape.Vertices[i].P.DistanceTo(pick);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return new VertexHit(best, shape.Vertices[best].P, bestDist);
        }

        /// <summary>
        /// Segment nearest to <paramref name="pick"/>, with the projected point and its position
        /// along the segment. Arc segments project onto the arc, not onto the chord.
        /// </summary>
        public static SegmentHit? NearestSegment(PlShape shape, Pt2 pick)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.SegmentCount == 0) return null;

            int bestSeg = 0;
            double bestT = 0.0, bestDist = double.MaxValue;
            var bestPt = shape.Vertices[0].P;

            for (int s = 0; s < shape.SegmentCount; s++)
            {
                var a = shape.SegmentStart(s).P;
                var b = shape.SegmentEnd(s).P;
                var p = BulgeMath.ClosestPointOnSegment(a, b, shape.SegmentBulge(s), pick, out double t);
                double d = p.DistanceTo(pick);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestSeg = s;
                    bestT = t;
                    bestPt = p;
                }
            }
            return new SegmentHit(bestSeg, bestT, bestPt, bestDist);
        }

        /// <summary>
        /// True when a segment hit sits so close to one of the segment's own endpoints that
        /// inserting a vertex there would create a zero-length segment. Both originals guard
        /// this with a 2 % band; we keep the same default.
        /// </summary>
        public static bool IsOnExistingVertex(SegmentHit hit, double band = 0.02) =>
            hit.T < band || hit.T > 1.0 - band;
    }
}
