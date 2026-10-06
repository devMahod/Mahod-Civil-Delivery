using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Per-segment geometry edits: PLTOOLS <c>PL-L2A</c> (straight → arc of a given radius),
    /// <c>PL-A2L</c> (arc → straight), <c>PL-NOARC</c> (approximate every arc with straights, in
    /// the five modes the original prompts for) and <c>PL-SgWidth</c> (segment width).
    /// Pure, in-place on a <see cref="PlShape"/>.
    /// </summary>
    public static class SegmentEditor
    {
        /// <summary>
        /// PL-L2A: turns a straight segment into an arc of <paramref name="radius"/>.
        /// Refuses when the chord does not fit the radius (|chord| &gt; 2R) rather than drawing an
        /// arbitrary arc.
        /// </summary>
        public static PlEditResult LineToArc(
            PlShape shape,
            int segmentIndex,
            double radius,
            bool clockwise = false,
            bool major = false)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.Is3d)
                return PlEditResult.NoChange(shape.Count, "a 3D polyline has no arc segments");
            if (segmentIndex < 0 || segmentIndex >= shape.SegmentCount)
                return PlEditResult.NoChange(shape.Count, "segment index is out of range");

            var a = shape.SegmentStart(segmentIndex);
            var b = shape.SegmentEnd(segmentIndex);
            double? bulge = BulgeMath.BulgeFromRadius(a.P, b.P, radius, clockwise, major);
            if (bulge == null)
                return PlEditResult.NoChange(
                    shape.Count,
                    $"radius {radius:0.###} is smaller than half the segment's chord ({a.P.DistanceTo(b.P) / 2.0:0.###}) — no arc can span it");

            int vi = shape.NormalizeIndex(segmentIndex);
            shape.Vertices[vi] = shape.Vertices[vi] with { Bulge = bulge.Value };
            return new PlEditResult
            {
                VerticesBefore = shape.Count,
                VerticesAfter = shape.Count,
                ChangedFlag = true
            };
        }

        /// <summary>PL-A2L: replaces an arc segment with its chord.</summary>
        public static PlEditResult ArcToLine(PlShape shape, int segmentIndex)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (segmentIndex < 0 || segmentIndex >= shape.SegmentCount)
                return PlEditResult.NoChange(shape.Count, "segment index is out of range");

            int vi = shape.NormalizeIndex(segmentIndex);
            if (!BulgeMath.IsArc(shape.Vertices[vi].Bulge))
                return PlEditResult.NoChange(shape.Count, "this segment is already straight");

            shape.Vertices[vi] = shape.Vertices[vi] with { Bulge = 0.0 };
            return new PlEditResult
            {
                VerticesBefore = shape.Count,
                VerticesAfter = shape.Count,
                ChangedFlag = true
            };
        }

        /// <summary>
        /// PL-NOARC: replaces every arc segment with a chain of straights.
        /// <paramref name="minRadius"/> mirrors the original's "Min processed arc radius" prompt —
        /// arcs flatter than that are left alone (flattening a 500 m radius into chords is almost
        /// never what anyone wants).
        /// </summary>
        public static PlEditResult FlattenArcs(
            PlShape shape,
            BulgeMath.FlattenMode mode,
            double value,
            double? minRadius = null)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            var result = new PlEditResult { VerticesBefore = shape.Count, VerticesAfter = shape.Count };
            if (shape.Is3d || !shape.HasArcs)
            {
                result.Notes.Add("no arc segments to flatten");
                return result;
            }

            var rebuilt = new List<PlVertex>(shape.Count * 2);
            int flattened = 0, skipped = 0;

            for (int i = 0; i < shape.Count; i++)
            {
                var v = shape.Vertices[i];
                bool isSegment = i < shape.SegmentCount;
                double bulge = isSegment ? shape.SegmentBulge(i) : 0.0;

                if (!isSegment || !BulgeMath.IsArc(bulge))
                {
                    rebuilt.Add(v);
                    continue;
                }

                var a = v.P;
                var b = shape.SegmentEnd(i).P;
                double radius = BulgeMath.Radius(a, b, bulge);
                if (minRadius.HasValue && radius > minRadius.Value)
                {
                    rebuilt.Add(v);           // flatter than the threshold — keep the arc
                    skipped++;
                    continue;
                }

                int parts = BulgeMath.FlattenSegmentCount(a, b, bulge, mode, value);
                if (parts < 2)
                {
                    rebuilt.Add(v with { Bulge = 0.0 });
                    flattened++;
                    continue;
                }

                var interior = BulgeMath.FlattenArcInteriorPoints(a, b, bulge, parts);
                rebuilt.Add(v with { Bulge = 0.0 });
                for (int k = 0; k < interior.Count; k++)
                {
                    double t = (double)(k + 1) / parts;
                    rebuilt.Add(new PlVertex(
                        interior[k].X,
                        interior[k].Y,
                        v.Z,
                        0.0,
                        Lerp(v.StartWidth, v.EndWidth, t),
                        v.EndWidth));
                    result.Added++;
                }
                flattened++;
            }

            shape.Vertices.Clear();
            shape.Vertices.AddRange(rebuilt);
            result.VerticesAfter = shape.Count;
            result.ChangedFlag = flattened > 0;
            result.Notes.Add($"{flattened} arc segment(s) flattened");
            if (skipped > 0)
                result.Notes.Add($"{skipped} arc segment(s) kept — radius above the minimum of {minRadius:0.###}");
            return result;
        }

        /// <summary>
        /// PL-SgWidth: sets the start/end width of one segment, or of every segment when
        /// <paramref name="segmentIndex"/> is null.
        /// </summary>
        public static PlEditResult SetWidth(PlShape shape, int? segmentIndex, double startWidth, double endWidth)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.Is3d)
                return PlEditResult.NoChange(shape.Count, "a 3D polyline has no segment width");
            if (startWidth < 0.0 || endWidth < 0.0)
                return PlEditResult.NoChange(shape.Count, "width cannot be negative");

            int from = segmentIndex ?? 0;
            int to = segmentIndex ?? shape.SegmentCount - 1;
            if (from < 0 || to >= shape.SegmentCount || from > to)
                return PlEditResult.NoChange(shape.Count, "segment index is out of range");

            for (int s = from; s <= to; s++)
            {
                int vi = shape.NormalizeIndex(s);
                shape.Vertices[vi] = shape.Vertices[vi] with { StartWidth = startWidth, EndWidth = endWidth };
            }

            return new PlEditResult
            {
                VerticesBefore = shape.Count,
                VerticesAfter = shape.Count,
                ChangedFlag = true
            };
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
