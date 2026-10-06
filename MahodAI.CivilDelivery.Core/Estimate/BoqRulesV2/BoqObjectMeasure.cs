using System;
using System.Collections.Generic;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// One physical object, one measurement — exact port of the reference boq_geometry.py (object_length and the chord
    /// helpers of entity_chords). Designers draw one object with several parallel lines on the same layer: a drainage
    /// pipe = axis + 2 walls + 2 encasement lines; a double routing line = 2 lines; a curb / island / garden stone = its 2
    /// faces; a painted stripe sometimes = its 2 edges; and some lines are simply duplicated. Summing every line
    /// multiplied the quantities.
    /// <para>
    /// Rule: every chord is cut into n = ceil(L / piece) equal pieces; a piece counts 1/m of its length, where m is the
    /// number of chords (itself included) that are parallel to it (|u × v| ≤ parallel_sin), whose span covers the piece
    /// midpoint (projection within [-1e-9, L + 1e-9]) and whose line lies within the object width + drafting tolerance of
    /// it. So an object drawn with k parallel lines counts once, wherever k changes along it (an axis that runs on into a
    /// manhole counts alone there). Lengths are PLAN lengths (Z ignored). Chords of 1e-6 m or less are dropped.
    /// </para>
    /// </summary>
    public static class BoqObjectMeasure
    {
        public const double ToleranceM = 0.03;
        public const double PieceM = 0.25;
        public const double ParallelSin = 0.05;   // ~2.9 degrees
        public const double ArcSagittaM = 0.005;
        public const double ArcMaxChordM = 0.5;

        /// <summary>
        /// At most 8° per chord (reference boq_geometry.ARC_MAX_ANGLE, 29.09.2026 live check): on a tight curve 0.5 m
        /// chords of the two faces of one stone differ by more than the parallel tolerance and the stone counted twice.
        /// </summary>
        public const double ArcMaxAngleRadians = 8 * Math.PI / 180;

        // The tiny epsilon keeps exact multiples of 8° (a full circle = 45 chords) from rounding up.
        private static int AngleCount(double sweep) => Math.Max(1, (int)Math.Ceiling(sweep / ArcMaxAngleRadians - 1e-9));
        public const double MinChordM = 1e-6;

        /// <summary>
        /// A chord whose bounding box spans more grid cells than this is checked against every piece instead of being
        /// registered cell by cell. Equivalent (a chord that qualifies for a piece is always in a cell near it); it only
        /// bounds memory for very long diagonal chords.
        /// </summary>
        private const long MaxCellsPerChord = 4096;

        /// <summary>Total (each object once), raw (sum of chord lengths) and the length counted at each multiplicity m.</summary>
        public sealed record Result(double Length, double Raw, IReadOnlyDictionary<int, double> ByMultiplicity);

        /// <summary>object_length with the ruleset's measurement settings (reference constants when absent).</summary>
        public static Result ObjectLength(IEnumerable<BoqSegment> chords, double widthM, BoqMeasurement? settings = null) =>
            ObjectLength(chords, widthM,
                settings?.ToleranceM ?? ToleranceM, settings?.PieceM ?? PieceM, settings?.ParallelSin ?? ParallelSin);

        public static Result ObjectLength(IEnumerable<BoqSegment> chords, double widthM, double toleranceM, double pieceM, double parallelSin)
        {
            ArgumentNullException.ThrowIfNull(chords);
            if (!(pieceM > 0) || !double.IsFinite(pieceM)) throw new ArgumentOutOfRangeException(nameof(pieceM), "piece_m must be a positive length.");
            if (!double.IsFinite(widthM) || !double.IsFinite(toleranceM)) throw new ArgumentOutOfRangeException(nameof(widthM), "The object width and tolerance must be finite.");

            var segs = new List<Chord>();
            foreach (var c in chords)
            {
                var dx = c.B.X - c.A.X;
                var dy = c.B.Y - c.A.Y;
                var length = double.Hypot(dx, dy);
                if (!(length > MinChordM) || !double.IsFinite(length)) continue; // also drops NaN / infinite chords
                segs.Add(new Chord(c.A.X, c.A.Y, c.B.X, c.B.Y, length, dx / length, dy / length));
            }

            var reach = widthM + toleranceM;
            var cell = Math.Max(2.0, 4 * reach);
            var grid = new Dictionary<(long, long), List<int>>();
            var everywhere = new List<int>();
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                var gx0 = CellOf(Math.Min(s.X1, s.X2), cell);
                var gx1 = CellOf(Math.Max(s.X1, s.X2), cell);
                var gy0 = CellOf(Math.Min(s.Y1, s.Y2), cell);
                var gy1 = CellOf(Math.Max(s.Y1, s.Y2), cell);
                if ((gx1 - gx0 + 1) * (gy1 - gy0 + 1) > MaxCellsPerChord)
                {
                    everywhere.Add(i);
                    continue;
                }
                for (var gx = gx0; gx <= gx1; gx++)
                    for (var gy = gy0; gy <= gy1; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var list)) grid[(gx, gy)] = list = new List<int>();
                        list.Add(i);
                    }
            }

            double total = 0.0, raw = 0.0;
            var byM = new SortedDictionary<int, double>();
            var stamp = new int[segs.Count];
            var visit = 0;
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                raw += s.L;
                var n = Math.Max(1, (int)Math.Ceiling(s.L / pieceM));
                var piece = s.L / n;
                for (var k = 0; k < n; k++)
                {
                    var px = s.X1 + s.Ux * piece * (k + 0.5);
                    var py = s.Y1 + s.Uy * piece * (k + 0.5);
                    visit++;
                    var m = 0;
                    var gx0 = CellOf(px - reach, cell);
                    var gx1 = CellOf(px + reach, cell);
                    var gy0 = CellOf(py - reach, cell);
                    var gy1 = CellOf(py + reach, cell);
                    for (var gx = gx0; gx <= gx1; gx++)
                        for (var gy = gy0; gy <= gy1; gy++)
                        {
                            if (!grid.TryGetValue((gx, gy), out var list)) continue;
                            foreach (var j in list)
                            {
                                if (stamp[j] == visit) continue;
                                stamp[j] = visit;
                                if (Covers(s, segs[j], px, py, reach, parallelSin)) m++;
                            }
                        }
                    foreach (var j in everywhere)
                        if (Covers(s, segs[j], px, py, reach, parallelSin)) m++;
                    m = Math.Max(m, 1);
                    total += piece / m;
                    byM[m] = (byM.TryGetValue(m, out var sofar) ? sofar : 0.0) + piece / m;
                }
            }
            return new Result(total, raw, byM);
        }

        /// <summary>
        /// Chords of a plan vertex chain; a closed chain INCLUDES its closing chord (last → first). Arcs must already be
        /// tessellated into the chain (the collector does it); see <see cref="PolylineChords"/> for bulged vertices.
        /// </summary>
        public static List<BoqSegment> VertexChainChords(IReadOnlyList<(double X, double Y)> vertices, bool closed)
        {
            ArgumentNullException.ThrowIfNull(vertices);
            var withBulges = new List<(double X, double Y, double Bulge)>(vertices.Count);
            foreach (var (x, y) in vertices) withBulges.Add((x, y, 0.0));
            return PolylineChords(withBulges, closed);
        }

        /// <summary>
        /// entity_chords of an LWPOLYLINE / POLYLINE2D with a +Z normal: the vertex chain, a closed one including its closing
        /// segment, bulged segments tessellated (chord ≤ 0.5 m, endpoints exact).
        /// </summary>
        public static List<BoqSegment> PolylineChords(IReadOnlyList<(double X, double Y, double Bulge)> vertices, bool closed)
        {
            ArgumentNullException.ThrowIfNull(vertices);
            var v = new List<(double X, double Y, double Bulge)>(vertices);
            if (closed && v.Count > 1) v.Add((v[0].X, v[0].Y, 0.0));
            var output = new List<BoqSegment>();
            for (var i = 0; i + 1 < v.Count; i++)
            {
                var q = BulgePoints(v[i].X, v[i].Y, v[i + 1].X, v[i + 1].Y, v[i].Bulge);
                for (var k = 0; k + 1 < q.Count; k++)
                    output.Add(new BoqSegment(new BoqPoint(q[k].X, q[k].Y), new BoqPoint(q[k + 1].X, q[k + 1].Y)));
            }
            return output;
        }

        /// <summary>entity_chords of an ARC (a0 → a1, CCW, radians) or a CIRCLE (0 → 2π) with a +Z normal.</summary>
        public static List<BoqSegment> ArcChords(double cx, double cy, double r, double a0, double a1)
        {
            var pts = ArcPoints(cx, cy, r, a0, a1);
            var output = new List<BoqSegment>(Math.Max(0, pts.Count - 1));
            for (var k = 0; k + 1 < pts.Count; k++)
                output.Add(new BoqSegment(new BoqPoint(pts[k].X, pts[k].Y), new BoqPoint(pts[k + 1].X, pts[k + 1].Y)));
            return output;
        }

        /// <summary>Points along a CCW arc a0 → a1 (radians), chords bounded by sagitta (0.005 m) and length (0.5 m).</summary>
        public static List<(double X, double Y)> ArcPoints(double cx, double cy, double r, double a0, double a1)
        {
            var output = new List<(double X, double Y)>();
            var sweep = a1 - a0;
            if (!double.IsFinite(sweep) || !double.IsFinite(r)) return output;
            while (sweep <= 0) sweep += 2 * Math.PI;
            if (r <= 0) return output;
            var step = Math.Min(r > ArcSagittaM ? 2 * Math.Acos(Math.Max(-1.0, 1 - ArcSagittaM / r)) : Math.PI / 8, ArcMaxChordM / r);
            var n = Math.Max(Math.Max(1, (int)Math.Ceiling(sweep / Math.Max(step, 1e-6))), AngleCount(sweep));
            for (var k = 0; k <= n; k++)
                output.Add((cx + r * Math.Cos(a0 + sweep * k / n), cy + r * Math.Sin(a0 + sweep * k / n)));
            return output;
        }

        /// <summary>
        /// Points of one polyline segment with a bulge (tan of a quarter of the included angle, CCW positive): the chord
        /// endpoints when straight, else the arc tessellated with chords ≤ 0.5 m whose first and last points are exactly the
        /// segment endpoints.
        /// </summary>
        public static List<(double X, double Y)> BulgePoints(double x1, double y1, double x2, double y2, double bulge)
        {
            var c = double.Hypot(x2 - x1, y2 - y1);
            if (Math.Abs(bulge) < 1e-12 || c < 1e-12) return new List<(double X, double Y)> { (x1, y1), (x2, y2) };
            var theta = 4 * Math.Atan(bulge); // signed included angle, CCW positive
            var r = c / (2 * Math.Sin(Math.Abs(theta) / 2));
            double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
            var h = Math.Sqrt(Math.Max(r * r - (c / 2) * (c / 2), 0.0));
            // The centre lies to the left of the chord for a CCW arc shorter than a half circle, mirrored otherwise.
            double lx = -(y2 - y1) / c, ly = (x2 - x1) / c;
            var side = (theta > 0) == (Math.Abs(theta) < Math.PI) ? 1 : -1;
            double cx = mx + side * h * lx, cy = my + side * h * ly;
            var a0 = Math.Atan2(y1 - cy, x1 - cx);
            var n = Math.Max(Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) * r / ArcMaxChordM)), AngleCount(Math.Abs(theta)));
            var pts = new List<(double X, double Y)>(n + 1);
            for (var k = 0; k <= n; k++)
                pts.Add((cx + r * Math.Cos(a0 + theta * k / n), cy + r * Math.Sin(a0 + theta * k / n)));
            pts[0] = (x1, y1);
            pts[pts.Count - 1] = (x2, y2);
            return pts;
        }

        private readonly record struct Chord(double X1, double Y1, double X2, double Y2, double L, double Ux, double Uy);

        private static long CellOf(double value, double cell) => (long)Math.Floor(value / cell);

        /// <summary>Chord o counts at piece midpoint P of chord s: parallel, P projects inside o, and o's line is within reach.</summary>
        private static bool Covers(in Chord s, in Chord o, double px, double py, double reach, double parallelSin)
        {
            if (Math.Abs(s.Ux * o.Uy - s.Uy * o.Ux) > parallelSin) return false;
            var t = (px - o.X1) * o.Ux + (py - o.Y1) * o.Uy;
            if (t < -1e-9 || t > o.L + 1e-9) return false;
            return !(Math.Abs((px - o.X1) * -o.Uy + (py - o.Y1) * o.Ux) > reach);
        }
    }
}
