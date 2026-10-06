using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Direction and vertex order: PLTOOLS <c>ENTREV</c> / <c>ENTREVS</c> (reverse — the #1 command
    /// in the firm's own MENU.pdf list), <c>PL-CW</c> / <c>PL-CCW</c> (force an orientation) and
    /// <c>PL-Vx1</c> (make a chosen vertex the start).
    ///
    /// Reversal is the operation people get silently wrong. Bulges and widths belong to the
    /// segment that STARTS at a vertex, so reversing the point order alone leaves every arc
    /// attached to the wrong segment and bulging the wrong way, and every taper back-to-front.
    /// The correct transform: the reversed segment j is the original segment traversed backwards,
    /// so it takes the NEGATED bulge and the SWAPPED widths of that original segment.
    /// </summary>
    public static class PolylineOrientation
    {
        /// <summary>
        /// Signed planar area: shoelace over the vertices plus each arc's circular-segment area.
        /// Positive = counter-clockwise. The arc term is what lets a circle drawn as a two-vertex
        /// closed polyline (chord area exactly 0) still report a real orientation.
        /// </summary>
        public static double SignedArea(PlShape shape)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.Count < 2) return 0.0;

            double area = 0.0;
            int n = shape.Count;
            for (int i = 0; i < n; i++)
            {
                var p = shape.Vertices[i].P;
                var q = shape.Vertices[(i + 1) % n].P;
                area += p.X * q.Y - q.X * p.Y;
            }
            area *= 0.5;

            for (int s = 0; s < shape.SegmentCount; s++)
                area += BulgeMath.SignedSegmentArea(
                    shape.SegmentStart(s).P, shape.SegmentEnd(s).P, shape.SegmentBulge(s));

            return area;
        }

        /// <summary>True when the polyline runs clockwise (negative signed area).</summary>
        public static bool IsClockwise(PlShape shape) => SignedArea(shape) < 0.0;

        /// <summary>
        /// Reverses the polyline in place, carrying bulges and widths correctly.
        /// A closed polyline keeps its start vertex (only the travel direction flips), which is
        /// what ENTREVS does and what keeps station 0 where the engineer expects it; an open
        /// polyline swaps its endpoints, which is the whole point of reversing it.
        /// </summary>
        public static PlEditResult Reverse(PlShape shape)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            var result = new PlEditResult { VerticesBefore = shape.Count, VerticesAfter = shape.Count };
            int n = shape.Count;
            if (n < 2)
            {
                result.Notes.Add("fewer than 2 vertices — nothing to reverse");
                return result;
            }

            var old = new List<PlVertex>(shape.Vertices);
            var rebuilt = new List<PlVertex>(n);

            for (int j = 0; j < n; j++)
            {
                int posIndex, dataIndex;
                if (shape.Closed)
                {
                    posIndex = (n - j) % n;
                    dataIndex = (n - j - 1 + n) % n;
                }
                else
                {
                    posIndex = n - 1 - j;
                    dataIndex = n - 2 - j;      // -1 for the new last vertex: no outgoing segment
                }

                var pos = old[posIndex];
                if (dataIndex < 0)
                {
                    rebuilt.Add(pos with { Bulge = 0.0, StartWidth = 0.0, EndWidth = 0.0 });
                    continue;
                }

                var data = old[dataIndex];
                rebuilt.Add(pos with
                {
                    Bulge = -data.Bulge,
                    StartWidth = data.EndWidth,
                    EndWidth = data.StartWidth
                });
            }

            shape.Vertices.Clear();
            shape.Vertices.AddRange(rebuilt);
            result.ChangedFlag = true;
            return result;
        }

        /// <summary>
        /// PL-CW / PL-CCW: reverses only when the polyline does not already run the requested way.
        /// Returns a result whose <see cref="PlEditResult.Changed"/> is false when it was already
        /// correct — "nothing to do" must not report as work done.
        /// </summary>
        public static PlEditResult SetOrientation(PlShape shape, bool clockwise)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.Count < 3)
                return PlEditResult.NoChange(shape.Count, "fewer than 3 vertices — orientation is undefined");

            bool isCw = IsClockwise(shape);
            if (isCw == clockwise)
                return PlEditResult.NoChange(shape.Count, clockwise
                    ? "already clockwise"
                    : "already counter-clockwise");

            return Reverse(shape);
        }

        /// <summary>
        /// PL-Vx1: rotates the vertex list so <paramref name="newStartIndex"/> becomes vertex 0.
        /// Closed polylines only — on an open one the start vertex is an endpoint and moving it
        /// would change the geometry, so this refuses instead of quietly reshaping the line.
        /// </summary>
        public static PlEditResult SetStartVertex(PlShape shape, int newStartIndex)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (!shape.Closed)
                return PlEditResult.NoChange(shape.Count, "the polyline is open — its start vertex is an endpoint and cannot be moved");
            int n = shape.Count;
            if (n < 3)
                return PlEditResult.NoChange(n, "fewer than 3 vertices");

            int k = shape.NormalizeIndex(newStartIndex);
            if (k == 0)
                return PlEditResult.NoChange(n, "this vertex is already the start");

            var old = new List<PlVertex>(shape.Vertices);
            shape.Vertices.Clear();
            for (int j = 0; j < n; j++)
                shape.Vertices.Add(old[(k + j) % n]);

            return new PlEditResult
            {
                VerticesBefore = n,
                VerticesAfter = n,
                ChangedFlag = true
            };
        }

        /// <summary>Read-only description of one segment (PL-SgInfo).</summary>
        /// <param name="SegmentIndex">Index of the segment.</param>
        /// <param name="IsArc">True for an arc segment.</param>
        /// <param name="Length">Arc length for an arc, chord length for a straight.</param>
        /// <param name="ChordLength">Straight distance between the segment's endpoints.</param>
        /// <param name="Radius">Arc radius, or null for a straight.</param>
        /// <param name="Center">Arc centre, or null for a straight.</param>
        /// <param name="DeltaDegrees">Included angle in degrees (signed, CCW positive), or null.</param>
        /// <param name="Sagitta">Mid-arc rise off the chord, or null.</param>
        /// <param name="StartWidth">Width at the segment start.</param>
        /// <param name="EndWidth">Width at the segment end.</param>
        public sealed record SegmentInfo(
            int SegmentIndex,
            bool IsArc,
            double Length,
            double ChordLength,
            double? Radius,
            Pt2? Center,
            double? DeltaDegrees,
            double? Sagitta,
            double StartWidth,
            double EndWidth);

        /// <summary>PL-SgInfo: everything the command prints, as data instead of console text.</summary>
        public static SegmentInfo? Describe(PlShape shape, int segmentIndex)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (segmentIndex < 0 || segmentIndex >= shape.SegmentCount) return null;

            var a = shape.SegmentStart(segmentIndex);
            var b = shape.SegmentEnd(segmentIndex);
            double bulge = shape.SegmentBulge(segmentIndex);
            bool isArc = BulgeMath.IsArc(bulge);

            return new SegmentInfo(
                SegmentIndex: segmentIndex,
                IsArc: isArc,
                Length: BulgeMath.SegmentLength(a.P, b.P, bulge),
                ChordLength: a.P.DistanceTo(b.P),
                Radius: isArc ? BulgeMath.Radius(a.P, b.P, bulge) : null,
                Center: isArc ? BulgeMath.Center(a.P, b.P, bulge) : null,
                DeltaDegrees: isArc ? BulgeMath.BulgeToDelta(bulge) * 180.0 / Math.PI : null,
                Sagitta: isArc ? BulgeMath.Sagitta(a.P, b.P, bulge) : null,
                StartWidth: a.StartWidth,
                EndWidth: a.EndWidth);
        }
    }
}
