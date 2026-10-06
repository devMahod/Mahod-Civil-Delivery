using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Adding vertices without clicking: PLTOOLS <c>PL-DIV</c> / <c>PL-DIVALL</c> (split a segment,
    /// or every segment, into N parts or by a spacing), <c>PL-VFI</c> (a vertex at every
    /// intersection with other curves) and MAHOD-PL <c>VDIST</c> (a vertex every N metres along
    /// the whole polyline, the Civil-style station spacing).
    ///
    /// All of it funnels through <see cref="InsertMany"/>, which rebuilds the vertex list in ONE
    /// pass. That matters for arcs: k insertions on one arc segment split its included angle into
    /// k+1 sub-arcs of the same radius, and doing that with repeated single inserts accumulates
    /// rounding and makes index bookkeeping fragile. Widths interpolate linearly along the
    /// segment, so a taper survives densification.
    /// </summary>
    public static class VertexDensifier
    {
        /// <summary>
        /// Inserts one vertex on segment <paramref name="segmentIndex"/> at normalised position
        /// <paramref name="t"/>. Returns the index of the new vertex, or -1 when the request is
        /// degenerate (t on an existing vertex).
        /// </summary>
        public static int InsertOnSegment(PlShape shape, int segmentIndex, double t, double band = 0.02)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (t < band || t > 1.0 - band) return -1;

            var result = InsertMany(shape, new[] { (segmentIndex, t) }, band);
            return result.Added > 0 ? shape.NormalizeIndex(segmentIndex + 1) : -1;
        }

        /// <summary>
        /// Inserts every requested (segment, t) position in one rebuild.
        /// Requests on an existing vertex (within <paramref name="band"/>) are skipped and counted
        /// in the notes rather than silently dropped.
        /// </summary>
        public static PlEditResult InsertMany(
            PlShape shape,
            IEnumerable<(int SegmentIndex, double T)> requests,
            double band = 0.02)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (requests == null) throw new ArgumentNullException(nameof(requests));

            var result = new PlEditResult { VerticesBefore = shape.Count, VerticesAfter = shape.Count };
            if (shape.SegmentCount == 0) return result;

            // Group requests per segment, sorted along the segment, with near-vertex and
            // near-duplicate requests dropped.
            var perSegment = new Dictionary<int, List<double>>();
            int skipped = 0;
            foreach (var (seg, t) in requests)
            {
                if (seg < 0 || seg >= shape.SegmentCount) { skipped++; continue; }
                if (t < band || t > 1.0 - band) { skipped++; continue; }
                if (!perSegment.TryGetValue(seg, out var list))
                {
                    list = new List<double>();
                    perSegment[seg] = list;
                }
                list.Add(t);
            }
            if (perSegment.Count == 0)
            {
                if (skipped > 0) result.Notes.Add($"{skipped} position(s) skipped — on or next to an existing vertex");
                return result;
            }

            var rebuilt = new List<PlVertex>(shape.Count + perSegment.Sum(kv => kv.Value.Count));
            for (int s = 0; s < shape.Count; s++)
            {
                var start = shape.Vertices[s];

                // Vertices beyond the last segment of an open polyline just carry over.
                if (!perSegment.TryGetValue(s, out var ts) || s >= shape.SegmentCount)
                {
                    rebuilt.Add(start);
                    continue;
                }

                ts.Sort();
                // Drop duplicates that would create zero-length segments.
                var cleaned = new List<double>(ts.Count);
                foreach (double t in ts)
                {
                    if (cleaned.Count > 0 && t - cleaned[^1] < band) { skipped++; continue; }
                    cleaned.Add(t);
                }
                if (cleaned.Count == 0)
                {
                    rebuilt.Add(start);
                    continue;
                }

                var end = shape.SegmentEnd(s);
                double bulge = shape.SegmentBulge(s);
                double delta = BulgeMath.IsArc(bulge) ? BulgeMath.BulgeToDelta(bulge) : 0.0;

                double prevT = 0.0;
                var current = start;
                foreach (double t in cleaned)
                {
                    var p = BulgeMath.PointOnSegment(start.P, end.P, bulge, t);
                    double z = shape.Is3d ? start.Z + (end.Z - start.Z) * t : start.Z;
                    double widthAtT = Lerp(start.StartWidth, start.EndWidth, t);

                    // The piece we are closing off runs prevT → t.
                    rebuilt.Add(current with
                    {
                        Bulge = delta == 0.0 ? 0.0 : BulgeMath.DeltaToBulge(delta * (t - prevT)),
                        EndWidth = widthAtT
                    });

                    current = new PlVertex(p.X, p.Y, z, 0.0, widthAtT, start.EndWidth);
                    prevT = t;
                    result.Added++;
                }

                // Last piece: current → the segment's original end.
                rebuilt.Add(current with
                {
                    Bulge = delta == 0.0 ? 0.0 : BulgeMath.DeltaToBulge(delta * (1.0 - prevT)),
                    EndWidth = start.EndWidth
                });
            }

            shape.Vertices.Clear();
            shape.Vertices.AddRange(rebuilt);
            result.VerticesAfter = shape.Count;
            if (skipped > 0) result.Notes.Add($"{skipped} position(s) skipped — on or next to an existing vertex");
            return result;
        }

        /// <summary>
        /// MAHOD-PL <c>VDIST</c>: a vertex every <paramref name="spacing"/> units of arc length
        /// measured from the start of the polyline, across segment boundaries.
        /// </summary>
        public static PlEditResult ByDistance(PlShape shape, double spacing, double band = 0.02)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (spacing <= 0.0)
                return PlEditResult.NoChange(shape.Count, "spacing must be greater than 0");

            double total = shape.TotalLength();
            if (total < spacing * 1.5)
                return PlEditResult.NoChange(shape.Count, "spacing is too large for the polyline length");

            var requests = new List<(int, double)>();
            double walked = 0.0;
            for (int s = 0; s < shape.SegmentCount; s++)
            {
                double len = shape.SegmentLength(s);
                if (len <= 0.0) continue;

                // First station strictly inside this segment.
                double firstStation = Math.Ceiling((walked + 1e-9) / spacing) * spacing;
                for (double station = firstStation; station < walked + len - 1e-9; station += spacing)
                {
                    double t = (station - walked) / len;
                    if (t > band && t < 1.0 - band) requests.Add((s, t));
                }
                walked += len;
            }

            return InsertMany(shape, requests, band);
        }

        /// <summary>
        /// PLTOOLS <c>PL-DIV</c> / <c>PL-DIVALL</c>: split one segment (when
        /// <paramref name="segmentIndex"/> is given) or every segment into
        /// <paramref name="parts"/> equal pieces.
        /// </summary>
        public static PlEditResult ByCount(PlShape shape, int parts, int? segmentIndex = null, double band = 0.02)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (parts < 2)
                return PlEditResult.NoChange(shape.Count, "parts must be 2 or more");

            var requests = new List<(int, double)>();
            int from = segmentIndex ?? 0;
            int to = segmentIndex.HasValue ? segmentIndex.Value : shape.SegmentCount - 1;
            if (from < 0 || to >= shape.SegmentCount || from > to)
                return PlEditResult.NoChange(shape.Count, "segment index is out of range");

            for (int s = from; s <= to; s++)
                for (int k = 1; k < parts; k++)
                    requests.Add((s, (double)k / parts));

            return InsertMany(shape, requests, band);
        }

        /// <summary>
        /// PLTOOLS <c>PL-DIV</c>'s distance variant applied per segment, and
        /// <see cref="ByDistance"/>'s single-segment sibling: split a segment every
        /// <paramref name="spacing"/> units measured along that segment.
        /// </summary>
        public static PlEditResult BySegmentSpacing(PlShape shape, double spacing, int? segmentIndex = null, double band = 0.02)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (spacing <= 0.0)
                return PlEditResult.NoChange(shape.Count, "spacing must be greater than 0");

            int from = segmentIndex ?? 0;
            int to = segmentIndex.HasValue ? segmentIndex.Value : shape.SegmentCount - 1;
            if (from < 0 || to >= shape.SegmentCount || from > to)
                return PlEditResult.NoChange(shape.Count, "segment index is out of range");

            var requests = new List<(int, double)>();
            for (int s = from; s <= to; s++)
            {
                double len = shape.SegmentLength(s);
                if (len <= spacing) continue;
                for (double d = spacing; d < len - 1e-9; d += spacing)
                    requests.Add((s, d / len));
            }

            return InsertMany(shape, requests, band);
        }

        /// <summary>
        /// PLTOOLS <c>PL-VFI</c>: a vertex at each of the supplied points (typically the
        /// intersections with other curves), snapped onto the nearest segment. A point farther
        /// than <paramref name="maxOffset"/> from the polyline is reported, not silently moved
        /// onto it.
        /// </summary>
        public static PlEditResult AtPoints(
            PlShape shape,
            IEnumerable<Pt2> points,
            double maxOffset = 1e-6,
            double band = 0.02)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (points == null) throw new ArgumentNullException(nameof(points));

            var requests = new List<(int, double)>();
            int offCurve = 0;
            foreach (var p in points)
            {
                var hit = VertexPicker.NearestSegment(shape, p);
                if (hit == null) continue;
                if (hit.Distance > maxOffset)
                {
                    offCurve++;
                    continue;
                }
                requests.Add((hit.SegmentIndex, hit.T));
            }

            var result = InsertMany(shape, requests, band);
            if (offCurve > 0)
                result.Notes.Add($"{offCurve} point(s) were farther than {maxOffset} from the polyline and were ignored");
            return result;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
