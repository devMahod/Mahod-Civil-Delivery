using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Bounded plan geometry captured beside the raw CAD properties (<c>cad_*</c>): the vertex chain of a LINE,
    /// LWPOLYLINE, POLYLINE2D or POLYLINE3D (bulged segments tessellated), the tessellated chain of an ARC or CIRCLE, the
    /// insertion point of a block reference and the boundary centroid of a HATCH, in raw, untransformed drawing units (the
    /// same space as the other <c>cad_*</c> evidence). Observation only: it never changes a quantity, a rule key, a
    /// mapping, a price or a saved decision. The BoQ rules engine uses it to classify (one crossing drawn several ways, an
    /// over-long "stop line") and to count one physical object once in plan (parallel lines of one object, one block drawn
    /// twice at one point); the native measurement of every record stays beside it.
    /// </summary>
    public static class QuantityGeometryEvidence
    {
        /// <summary>
        /// Raw vertex bound per entity (vertices as stored in the polyline, before any arc tessellation). Longer chains
        /// record only an over-limit status (no coordinates). 1024 since BoQ rules 2.1: an object is measured once from its
        /// plan geometry, and in project 6422 two garden-stone polylines (69 vertices, 2,166 m — half the item) exceeded
        /// the former bound of 64 and would have been counted twice (both faces) without it.
        /// </summary>
        public const int MaxVertices = 1024;

        /// <summary>
        /// Point bound of the emitted chain after arc tessellation. Longer chains record only an over-limit status (no
        /// coordinates); the engine then counts the object's original length.
        /// </summary>
        public const int MaxTessellatedPoints = 4096;

        /// <summary>Unit-free density bound: no chord spans more than this angle of its arc.</summary>
        public const double MaxChordAngleDegrees = 8.0;

        /// <summary>Reference density bounds (natali-boq-2809/boq_geometry.py ARC_MAX_CHORD_M / ARC_SAGITTA_M), in metres.</summary>
        public const double ArcMaxChordMetres = 0.5;
        public const double ArcSagittaMetres = 0.005;

        // Names as returned by the plugin reader (QuantityCadMetadataPolicy.AppendEvidence adds the "cad_" prefix).
        public const string RawSegments = "segments";
        public const string RawSegmentsStatus = "segments_status";
        public const string RawSegmentsClosed = "segments_closed";
        public const string RawHatchCentroid = "hatch_boundary_centroid";
        public const string RawHatchStatus = "hatch_boundary_status";
        /// <summary>
        /// BoQ rules 2.3 (v4): the hatch's boundary points ("x,y;x,y;…", line-edge endpoints and polyline-loop vertices,
        /// the centroid's own points), at most <see cref="MaxHatchBoundaryPoints"/>; the engine finds the closed polyline
        /// drawn with the same vertices (the hatch's boundary) by them. Same name as the golden key hatch_boundary_points.
        /// </summary>
        public const string RawHatchBoundaryPoints = "hatch_boundary_points";
        public const string RawHatchBoundaryPointsStatus = "hatch_boundary_points_status";
        /// <summary>v4.5: every boundary loop as a closed ring (QuantityHatchLoops) — the hatch overlap estimates.</summary>
        public const string RawHatchLoops = "hatch_boundary_loops";
        public const string RawHatchLoopsStatus = "hatch_boundary_loops_status";
        public const string RawInsertPoint = "insert_point";
        public const string RawInsertPointStatus = "insert_point_status";
        // The measuring chain (arcs tessellated) is kept apart from the raw vertex chain above: classification
        // (crossings, over-long stop lines) reads the raw vertices exactly as the reference read them (29.09.2026 live:
        // tessellated arcs in the classification chain moved 20 SM records between crossing roles).
        public const string RawPlanChain = "plan_chain";
        public const string RawPlanChainStatus = "plan_chain_status";
        public const string RawPlanChainClosed = "plan_chain_closed";

        public const string SegmentsKey = "cad_" + RawSegments;
        public const string SegmentsStatusKey = "cad_" + RawSegmentsStatus;
        public const string SegmentsClosedKey = "cad_" + RawSegmentsClosed;
        public const string HatchCentroidKey = "cad_" + RawHatchCentroid;
        public const string HatchStatusKey = "cad_" + RawHatchStatus;
        public const string HatchBoundaryPointsKey = "cad_" + RawHatchBoundaryPoints;
        public const string HatchBoundaryPointsStatusKey = "cad_" + RawHatchBoundaryPointsStatus;
        public const string HatchLoopsKey = "cad_" + RawHatchLoops;
        public const string HatchLoopsStatusKey = "cad_" + RawHatchLoopsStatus;
        /// <summary>
        /// The entity's own linetype scale, written by the raw CAD metadata read (QuantityCadMetadataReader
        /// "entity_linetype_scale", "R" format). BoQ rules 2.3 (v4): a crossing line with scale 0.5 is the crossing's dashed
        /// line. Not a geometry key: it was captured (and summarized) before the BoQ rules existed.
        /// </summary>
        public const string LinetypeScaleKey = "cad_entity_linetype_scale";
        /// <summary>Boundary points kept per hatch; larger boundaries record only an over-limit status (stop-line hatches have 4–5).</summary>
        public const int MaxHatchBoundaryPoints = 64;
        public const string InsertPointKey = "cad_" + RawInsertPoint;
        public const string InsertPointStatusKey = "cad_" + RawInsertPointStatus;
        /// <summary>
        /// Rules 2.8 (BOQ-N1): the rest of a host model-space INSERT's transform ("sx,sy,sz", "nx,ny,nz", round-trip) and its
        /// block definition signature (JSON: dxf_counts, envelope in block units, base_point, units_code; absent when an
        /// entity of the definition has no extents), for approved physical footprints. Under the insert-point prefix: geometry
        /// keys, never summarized and never part of a decision scope.
        /// </summary>
        public const string InsertScaleKey = InsertPointKey + "_scale";
        public const string InsertNormalKey = InsertPointKey + "_normal";
        public const string InsertBlockSignatureKey = InsertPointKey + "_block_signature";
        public const string PlanChainKey = "cad_" + RawPlanChain;
        public const string PlanChainStatusKey = "cad_" + RawPlanChainStatus;
        public const string PlanChainClosedKey = "cad_" + RawPlanChainClosed;

        public const string StatusComplete = "complete";
        public const string StatusCentroid = "line-edge-endpoints-mean";
        public const string StatusFewerThanTwoVertices = "fewer-than-two-vertices";
        public const string StatusNonFiniteVertex = "non-finite-vertex";
        /// <summary>A polyline / arc / circle whose normal is not +Z: its plan projection is not the stored geometry.</summary>
        public const string StatusNonPlanNormal = "non-plan-normal";
        public const string StatusDegenerateArc = "degenerate-arc";
        public const string StatusNonFinitePoint = "non-finite-point";

        private const double MaxChordAngleRadians = MaxChordAngleDegrees * Math.PI / 180.0;
        // Counts are computed before any point is generated; anything above the point bound is only reported.
        private const long ChordCountCeiling = 1_000_000_000L;

        /// <summary>
        /// True for the geometry keys above. They are excluded from the metadata summary shown to the engineer and from
        /// persistent decision scopes, so adding them to a scan never invalidates a decision saved before they existed.
        /// </summary>
        public static bool IsGeometryKey(string? key) => key != null &&
            (key.StartsWith(SegmentsKey, StringComparison.Ordinal) ||
             key.StartsWith("cad_hatch_boundary", StringComparison.Ordinal) ||
             key.StartsWith(InsertPointKey, StringComparison.Ordinal) ||
             key.StartsWith(PlanChainKey, StringComparison.Ordinal));

        /// <summary>Raw vertex count above <see cref="MaxVertices"/>.</summary>
        public static string OverLimit(int count) =>
            "over-limit:" + count.ToString(CultureInfo.InvariantCulture) + ">" + MaxVertices.ToString(CultureInfo.InvariantCulture);

        /// <summary>Emitted (tessellated) point count above <see cref="MaxTessellatedPoints"/>.</summary>
        public static string OverTessellationLimit(long count) =>
            "over-limit:" + count.ToString(CultureInfo.InvariantCulture) + ">" + MaxTessellatedPoints.ToString(CultureInfo.InvariantCulture);

        /// <summary>The reference's plan test (boq_geometry.py): a +Z normal, |z − 1| ≤ 1e-6.</summary>
        public static bool IsPlanNormal(double x, double y, double z) =>
            double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) && Math.Abs(z - 1.0) <= 1e-6;

        /// <summary>
        /// A polyline drawn face-down (normal (0,0,-1), e.g. after MIRROR3D or in a flipped UCS) is still a plan object:
        /// its WCS vertices are exact and each bulge is mirrored, so the reader negates it (review 30/09).
        /// </summary>
        public static bool IsFaceDownPlanNormal(double x, double y, double z) =>
            double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) && Math.Abs(z + 1.0) <= 1e-6;

        /// <summary>
        /// Chords for one polyline segment from (x1,y1) to (x2,y2) with the given bulge: the reference count
        /// ceil(|θ|·r / 0.5 m) (boq_geometry._bulge_points, radius in metres) and at least ceil(|θ| / 8°), so the density
        /// does not depend on the drawing unit. 1 for a straight or zero-length segment.
        /// </summary>
        public static long BulgeChordCount(double x1, double y1, double x2, double y2, double bulge, double metresPerUnit = 1.0)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(bulge) < 1e-12 || chord < 1e-12) return 1;
            var theta = Math.Abs(4 * Math.Atan(bulge));
            var radius = chord / (2 * Math.Sin(theta / 2));
            return Math.Max(Count(theta * (radius * Scale(metresPerUnit)) / ArcMaxChordMetres), AngleCount(theta));
        }

        /// <summary>
        /// Chords for a CCW arc of the given radius and sweep (radians): the reference step
        /// min(sagitta 0.005 m, chord 0.5 m) (boq_geometry._arc_points, radius in metres) and at least ceil(sweep / 8°).
        /// </summary>
        public static long ArcChordCount(double radius, double sweep, double metresPerUnit = 1.0)
        {
            var r = radius * Scale(metresPerUnit);
            var step = Math.Min(r > ArcSagittaMetres ? 2 * Math.Acos(Math.Max(-1.0, 1 - ArcSagittaMetres / r)) : Math.PI / 8,
                ArcMaxChordMetres / r);
            return Math.Max(Count(sweep / Math.Max(step, 1e-6)), AngleCount(sweep));
        }

        /// <summary>
        /// The plan chain of a LINE / LWPOLYLINE / POLYLINE2D / POLYLINE3D: every stored vertex exactly, each bulged
        /// segment tessellated like the reference (endpoints exact, <see cref="BulgeChordCount"/> chords). An open chain
        /// ignores the last vertex's bulge. A closed chain tessellates its closing segment (last → first vertex, with the
        /// last vertex's bulge) but never repeats the first vertex: the consumer adds the closing chord itself.
        /// Returns <see cref="StatusComplete"/> with the points, or a status and no points.
        /// </summary>
        public static string TessellatePolyline(IReadOnlyList<(double X, double Y, double Bulge)> vertices, bool closed,
            double metresPerUnit, out List<(double X, double Y)> points)
        {
            ArgumentNullException.ThrowIfNull(vertices);
            points = new List<(double X, double Y)>();
            if (vertices.Count < 2) return StatusFewerThanTwoVertices;
            var segments = closed ? vertices.Count : vertices.Count - 1;
            for (var i = 0; i < vertices.Count; i++)
                if (!double.IsFinite(vertices[i].X) || !double.IsFinite(vertices[i].Y) ||
                    (i < segments && !double.IsFinite(vertices[i].Bulge)))
                    return StatusNonFiniteVertex;

            var counts = new long[segments];
            long total = closed ? 0 : 1;
            for (var i = 0; i < segments; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                counts[i] = BulgeChordCount(a.X, a.Y, b.X, b.Y, a.Bulge, metresPerUnit);
                total += counts[i];
            }
            if (total > MaxTessellatedPoints) return OverTessellationLimit(total);

            points.Capacity = (int)total;
            for (var i = 0; i < segments; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                AppendBulge(points, a.X, a.Y, b.X, b.Y, a.Bulge, counts[i]);
            }
            if (!closed) points.Add((vertices[vertices.Count - 1].X, vertices[vertices.Count - 1].Y));
            return Finish(points);
        }

        /// <summary>
        /// The plan chain of a CCW ARC from <paramref name="startAngle"/> to <paramref name="endAngle"/> (radians; a
        /// non-positive difference wraps by 2π like the reference): <see cref="ArcChordCount"/> chords, the first and last
        /// point exactly <paramref name="start"/> and <paramref name="end"/>. Open chain.
        /// </summary>
        public static string TessellateArc(double cx, double cy, double radius, double startAngle, double endAngle,
            (double X, double Y) start, (double X, double Y) end, double metresPerUnit, out List<(double X, double Y)> points)
        {
            points = new List<(double X, double Y)>();
            if (!Finite(cx, cy, radius, startAngle, endAngle, start.X, start.Y, end.X, end.Y)) return StatusNonFiniteVertex;
            if (!(radius > 0)) return StatusDegenerateArc;
            var sweep = endAngle - startAngle;
            if (sweep <= 0) sweep += Math.Ceiling(-sweep / (2 * Math.PI)) * 2 * Math.PI;
            if (sweep <= 0) sweep += 2 * Math.PI; // equal angles (or a whole number of turns) = a full turn, like the reference
            if (!(sweep > 0) || !double.IsFinite(sweep)) return StatusDegenerateArc;
            var n = ArcChordCount(radius, sweep, metresPerUnit);
            if (n + 1 > MaxTessellatedPoints) return OverTessellationLimit(n + 1);

            points.Capacity = (int)n + 1;
            points.Add(start);
            for (var k = 1; k < n; k++)
            {
                var angle = startAngle + sweep * k / n;
                points.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
            points.Add(end);
            return Finish(points);
        }

        /// <summary>
        /// The plan chain of a CIRCLE from angle 0 (the reference's start): <see cref="ArcChordCount"/> points of the
        /// full turn, closed, without repeating the first point (the consumer adds the closing chord itself).
        /// </summary>
        public static string TessellateCircle(double cx, double cy, double radius, double metresPerUnit,
            out List<(double X, double Y)> points)
        {
            points = new List<(double X, double Y)>();
            if (!Finite(cx, cy, radius)) return StatusNonFiniteVertex;
            if (!(radius > 0)) return StatusDegenerateArc;
            var sweep = 2 * Math.PI;
            var n = ArcChordCount(radius, sweep, metresPerUnit);
            if (n > MaxTessellatedPoints) return OverTessellationLimit(n);

            points.Capacity = (int)n;
            for (var k = 0; k < n; k++)
            {
                var angle = sweep * k / n;
                points.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
            return Finish(points);
        }

        /// <summary>"x,y;x,y;…" with at most four decimals, invariant culture.</summary>
        public static string FormatVertices(IReadOnlyList<(double X, double Y)> vertices)
        {
            ArgumentNullException.ThrowIfNull(vertices);
            var sb = new StringBuilder(vertices.Count * 24);
            for (var i = 0; i < vertices.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(F(vertices[i].X)).Append(',').Append(F(vertices[i].Y));
            }
            return sb.ToString();
        }

        public static string FormatPoint(double x, double y) => F(x) + "," + F(y);

        public static bool TryParseVertices(string? text, out List<(double X, double Y)> vertices)
        {
            vertices = new List<(double X, double Y)>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParsePoint(pair, out var x, out var y)) { vertices.Clear(); return false; }
                vertices.Add((x, y));
            }
            return vertices.Count > 0;
        }

        public static bool TryParsePoint(string? text, out double x, out double y)
        {
            x = y = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            return parts.Length == 2 &&
                   double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                   double.IsFinite(x) && double.IsFinite(y);
        }

        /// <summary>
        /// The start vertex (exact) and the interior points of one segment; the end vertex belongs to the next segment.
        /// The reference's centre construction (boq_geometry._bulge_points), CCW for a positive bulge.
        /// </summary>
        private static void AppendBulge(List<(double X, double Y)> points, double x1, double y1, double x2, double y2,
            double bulge, long n)
        {
            points.Add((x1, y1));
            if (n <= 1) return;
            var dx = x2 - x1;
            var dy = y2 - y1;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            var theta = 4 * Math.Atan(bulge);
            var radius = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
            var h = Math.Sqrt(Math.Max(radius * radius - (chord / 2) * (chord / 2), 0.0));
            // The centre lies left of the chord for a CCW arc shorter than a half circle, mirrored otherwise.
            var side = (theta > 0) == (Math.Abs(theta) < Math.PI) ? 1.0 : -1.0;
            var cx = (x1 + x2) / 2 + side * h * (-dy / chord);
            var cy = (y1 + y2) / 2 + side * h * (dx / chord);
            var a0 = Math.Atan2(y1 - cy, x1 - cx);
            for (var k = 1; k < n; k++)
            {
                var angle = a0 + theta * k / n;
                points.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
        }

        private static string Finish(List<(double X, double Y)> points)
        {
            foreach (var (x, y) in points)
                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    points.Clear();
                    return StatusNonFiniteVertex;
                }
            return StatusComplete;
        }

        private static long Count(double chords) =>
            double.IsNaN(chords) || chords >= ChordCountCeiling ? ChordCountCeiling
            : chords <= 1 ? 1
            : (long)Math.Ceiling(chords);

        // The tiny epsilon keeps exact multiples of 8° (a full circle = 45 chords) from rounding up.
        private static long AngleCount(double sweep) => Count(sweep / MaxChordAngleRadians - 1e-9);

        private static double Scale(double metresPerUnit) =>
            double.IsFinite(metresPerUnit) && metresPerUnit > 0 ? metresPerUnit : 1.0;

        private static bool Finite(params double[] values) => values.All(double.IsFinite);

        // v4.5: 0.1 µm. At 0.1 mm a plan length summed over thousands of chords drifted 0.02–0.22 m from the reference
        // (C1/C2/C3/M2/M4 at two decimals, headless Civil 2027 run 30.09.2026).
        private static string F(double value)
        {
            var text = value.ToString("0.#######", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;  // a coordinate within 0.05 µm of zero is zero, not "-0"
        }
    }
}
