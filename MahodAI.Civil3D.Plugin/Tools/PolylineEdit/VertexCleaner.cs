using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// The three vertex-reduction passes, all in-place on a <see cref="PlShape"/> and all pure:
    ///
    ///   • <see cref="RemoveCoincident"/> — PLTOOLS <c>PL-VxOpt</c> ("removing coincident
    ///     vertices", one of the five commands the firm actually uses).
    ///   • <see cref="Weed"/> — PLTOOLS <c>PL-VxRdc</c> ("weeding"), by chord deviation or by
    ///     deflection angle.
    ///   • <see cref="Thin"/> — Douglas-Peucker, which PLTOOLS does NOT have; it is Olga's
    ///     "polyline came out of a 3D conversion with thousands of points" case, and unlike
    ///     weeding it bounds the deviation of the WHOLE result, not of each single removal.
    ///
    /// Every pass honours <see cref="PlShape.MinVertices"/> and, when asked, refuses to touch a
    /// vertex adjacent to an arc — merging across a bulge silently straightens a curve, which is
    /// the one destructive thing a "cleanup" must never do behind the engineer's back.
    /// </summary>
    public static class VertexCleaner
    {
        /// <summary>PL-VxRdc's default deviation tolerance (drawing units).</summary>
        public const double DefaultWeedDeviation = 0.15;

        /// <summary>PL-VxOpt's coincidence tolerance.</summary>
        public const double DefaultCoincidentTolerance = 1e-6;

        /// <summary>
        /// Removes vertices that coincide with their neighbour within <paramref name="tolerance"/>.
        ///
        /// Scanned from the END, and of each coincident pair the EARLIER vertex is dropped. That
        /// asymmetry is not cosmetic: bulges and widths live on the segment's start vertex, so
        /// dropping the earlier duplicate keeps the surviving vertex's outgoing arc intact. The
        /// original's comment makes the same point ("проход с конца … кривизна не страдает").
        ///
        /// A polyline whose first and last vertex coincide is closed properly instead — the
        /// duplicate is removed and <see cref="PlShape.Closed"/> is set, exactly like PL-VxOpt.
        /// </summary>
        public static PlEditResult RemoveCoincident(PlShape shape, double tolerance = DefaultCoincidentTolerance)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            var result = new PlEditResult { VerticesBefore = shape.Count, VerticesAfter = shape.Count };
            if (shape.Count < 2) return result;
            if (tolerance < 0.0) tolerance = 0.0;

            // An open polyline that ends where it starts is a closed polyline drawn by hand.
            if (!shape.Closed && shape.Count > 3 && Coincident(shape.Vertices[0], shape.Vertices[^1], tolerance, shape.Is3d))
            {
                shape.Vertices.RemoveAt(shape.Count - 1);
                shape.Closed = true;
                result.Removed++;
                result.Notes.Add("first and last vertex coincided — the polyline was closed properly");
            }

            for (int i = shape.Count - 2; i >= 0; i--)
            {
                if (shape.Count <= shape.MinVertices) break;
                if (!Coincident(shape.Vertices[i], shape.Vertices[i + 1], tolerance, shape.Is3d)) continue;

                // Keep the later vertex (it owns the outgoing segment), drop the earlier one.
                shape.Vertices.RemoveAt(i);
                result.Removed++;
            }

            // On a closed shape the wrap-around pair is a duplicate too.
            if (shape.Closed && shape.Count > shape.MinVertices &&
                Coincident(shape.Vertices[^1], shape.Vertices[0], tolerance, shape.Is3d))
            {
                shape.Vertices.RemoveAt(shape.Count - 1);
                result.Removed++;
            }

            result.VerticesAfter = shape.Count;
            return result;
        }

        /// <summary>
        /// PL-VxRdc: drops a vertex whose offset from the straight line between its neighbours is
        /// within tolerance. Two independent criteria, either or both:
        ///
        ///   • <paramref name="deviationTolerance"/> — perpendicular distance (drawing units).
        ///     PL-VxRdc's H mode, default 0.15.
        ///   • <paramref name="angleToleranceDeg"/> — deflection angle at the vertex. PL-VxRdc's
        ///     A mode (the LISP smuggles it in as a negative tolerance; here it is its own
        ///     parameter, because a negative length is not a thing).
        ///
        /// Greedy forward scan with UPDATED neighbours, so a straight run of a thousand vertices
        /// collapses to its two ends in a single pass.
        /// </summary>
        public static PlEditResult Weed(
            PlShape shape,
            double? deviationTolerance = DefaultWeedDeviation,
            double? angleToleranceDeg = null,
            bool preserveArcs = true)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            var result = new PlEditResult { VerticesBefore = shape.Count, VerticesAfter = shape.Count };
            if (shape.Count < 3) return result;

            double devTol = deviationTolerance ?? -1.0;
            double angTol = angleToleranceDeg.HasValue ? angleToleranceDeg.Value * Math.PI / 180.0 : -1.0;
            if (devTol < 0.0 && angTol < 0.0)
            {
                result.Notes.Add("no tolerance given — nothing to weed by");
                return result;
            }

            // Closed shapes may drop any vertex; open shapes must keep their two endpoints.
            int i = shape.Closed ? 0 : 1;
            while (i < (shape.Closed ? shape.Count : shape.Count - 1))
            {
                if (shape.Count <= shape.MinVertices) break;
                if (preserveArcs && TouchesArc(shape, i))
                {
                    i++;
                    continue;
                }

                var prev = shape.Vertices[shape.NormalizeIndex(i - 1 < 0 ? shape.Count - 1 : i - 1)].P;
                var here = shape.Vertices[i].P;
                var next = shape.Vertices[shape.NormalizeIndex(i + 1)].P;

                bool drop = false;
                if (devTol >= 0.0 && BulgeMath.DistanceToSegment(prev, next, here) <= devTol) drop = true;
                if (!drop && angTol >= 0.0 && BulgeMath.Deflection(prev, here, next) <= angTol) drop = true;

                if (drop)
                {
                    RemoveVertexKeepingGeometry(shape, i);
                    result.Removed++;
                    // Re-examine the seam: the previous vertex now has a new successor. On a
                    // closed shape stepping back below 0 is legal (wraps), but re-testing index 0
                    // forever is not — so only step back when there is a real earlier vertex.
                    if (i > (shape.Closed ? 0 : 1)) i--;
                    continue;
                }
                i++;
            }

            result.VerticesAfter = shape.Count;
            return result;
        }

        /// <summary>
        /// Douglas-Peucker thinning: keeps the vertices needed to stay within
        /// <paramref name="tolerance"/> of the ORIGINAL line everywhere, so unlike weeding the
        /// error cannot accumulate across removals. This is the "leave one point on each bend"
        /// request.
        ///
        /// Arc vertices are pinned when <paramref name="preserveArcs"/> is set, and each straight
        /// run between two pinned vertices is thinned independently. A closed shape is split at
        /// vertex 0 and at the vertex farthest from it, and both halves are thinned — otherwise
        /// the closing segment is never considered and the seam stays dense (the shortcut
        /// MAHOD-PL's VTHIN takes).
        /// </summary>
        public static PlEditResult Thin(PlShape shape, double tolerance, bool preserveArcs = true)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            var result = new PlEditResult { VerticesBefore = shape.Count, VerticesAfter = shape.Count };
            if (shape.Count < 3)
            {
                result.Notes.Add("fewer than 3 vertices — nothing to thin");
                return result;
            }
            if (tolerance <= 0.0)
            {
                result.Notes.Add("tolerance must be greater than 0");
                return result;
            }

            var keep = new bool[shape.Count];
            keep[0] = true;
            keep[shape.Count - 1] = true;

            // Pin arc vertices: both ends of an arc segment must survive or the arc is lost.
            if (preserveArcs)
            {
                for (int i = 0; i < shape.Count; i++)
                {
                    if (!BulgeMath.IsArc(shape.Vertices[i].Bulge)) continue;
                    keep[i] = true;
                    keep[shape.NormalizeIndex(i + 1)] = true;
                }
            }

            if (shape.Closed)
            {
                // Split the loop so both halves are open chains DP can handle.
                keep[FarthestFrom(shape, 0)] = true;
            }

            var pts = new List<Pt2>(shape.Count);
            for (int i = 0; i < shape.Count; i++) pts.Add(shape.Vertices[i].P);

            // Thin each run between consecutive pinned vertices.
            int runStart = 0;
            for (int i = 1; i < shape.Count; i++)
            {
                if (!keep[i]) continue;
                DouglasPeucker(pts, runStart, i, tolerance, keep);
                runStart = i;
            }
            if (shape.Closed)
            {
                // The closing run wraps from the last pinned vertex through vertex 0.
                var wrapped = new List<Pt2>(pts.Count + 1);
                for (int i = runStart; i < shape.Count; i++) wrapped.Add(pts[i]);
                wrapped.Add(pts[0]);
                var wrappedKeep = new bool[wrapped.Count];
                wrappedKeep[0] = true;
                wrappedKeep[^1] = true;
                DouglasPeucker(wrapped, 0, wrapped.Count - 1, tolerance, wrappedKeep);
                for (int k = 1; k < wrapped.Count - 1; k++)
                    if (wrappedKeep[k]) keep[runStart + k] = true;
            }

            for (int i = shape.Count - 1; i >= 0; i--)
            {
                if (keep[i]) continue;
                if (shape.Count <= shape.MinVertices) break;
                RemoveVertexKeepingGeometry(shape, i);
                result.Removed++;
            }

            result.VerticesAfter = shape.Count;
            return result;
        }

        /// <summary>
        /// Removes vertex <paramref name="index"/> and repairs the seam: the segment that now
        /// spans the gap must be a straight, so the preceding vertex's bulge is cleared. Both
        /// originals do the same ("deleting a vertex between arc segments straightens the merged
        /// segment") — silently keeping the old bulge would bend the new, longer segment by the
        /// old angle and move the line somewhere nobody asked for.
        /// </summary>
        public static void RemoveVertexKeepingGeometry(PlShape shape, int index)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.Count == 0) return;
            index = shape.NormalizeIndex(index);

            int prev = index - 1;
            if (prev < 0) prev = shape.Closed ? shape.Count - 1 : -1;

            if (prev >= 0 && prev != index)
            {
                var p = shape.Vertices[prev];
                if (BulgeMath.IsArc(p.Bulge) || BulgeMath.IsArc(shape.Vertices[index].Bulge))
                    shape.Vertices[prev] = p with { Bulge = 0.0 };
            }

            shape.Vertices.RemoveAt(index);
        }

        /// <summary>True when the vertex or the segment arriving at it is an arc.</summary>
        private static bool TouchesArc(PlShape shape, int index)
        {
            if (shape.Is3d) return false;
            if (BulgeMath.IsArc(shape.Vertices[index].Bulge)) return true;
            int prev = index - 1;
            if (prev < 0) prev = shape.Closed ? shape.Count - 1 : -1;
            return prev >= 0 && BulgeMath.IsArc(shape.Vertices[prev].Bulge);
        }

        private static bool Coincident(PlVertex a, PlVertex b, double tolerance, bool use3d)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            double d2 = dx * dx + dy * dy;
            if (use3d)
            {
                double dz = a.Z - b.Z;
                d2 += dz * dz;
            }
            return d2 <= tolerance * tolerance;
        }

        private static int FarthestFrom(PlShape shape, int index)
        {
            var origin = shape.Vertices[index].P;
            int best = index;
            double bestD = -1.0;
            for (int i = 0; i < shape.Count; i++)
            {
                double d = origin.DistanceTo(shape.Vertices[i].P);
                if (d > bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Classic recursive Douglas-Peucker over an index range, marking survivors.</summary>
        private static void DouglasPeucker(List<Pt2> pts, int first, int last, double tolerance, bool[] keep)
        {
            if (last - first < 2) return;

            double maxDist = -1.0;
            int maxIdx = first;
            for (int i = first + 1; i < last; i++)
            {
                double d = BulgeMath.DistanceToSegment(pts[first], pts[last], pts[i]);
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxDist <= tolerance) return;

            keep[maxIdx] = true;
            DouglasPeucker(pts, first, maxIdx, tolerance, keep);
            DouglasPeucker(pts, maxIdx, last, tolerance, keep);
        }
    }
}
