using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// v4.5: the boundary loops of a hatch as closed rings of points ("x,y;x,y|x,y;…", one ring per loop), arcs cut into
    /// max(2, ⌊sweep·16⌋) equal pieces — the discretisation of the reference compute_ha_overlap.py — for the hatch overlap
    /// and unmeasured-area ESTIMATES (never a quantity). Shared by the Civil reader (native loops) and the BoQ adapter
    /// (failure diagnostics), so both build the same rings.
    /// </summary>
    public static class QuantityHatchLoops
    {
        public const double PiecesPerRadian = 16;
        /// <summary>Points of all loops together; above it the loops are not published ("over-limit").</summary>
        public const int MaxPoints = 20000;

        /// <summary>Points on the arc from <paramref name="startAngle"/> through a signed sweep (+ counter-clockwise).</summary>
        public static List<(double X, double Y)> ArcPoints(double cx, double cy, double radius, double startAngle, double signedSweep)
        {
            var n = Math.Max(2, (int)(Math.Abs(signedSweep) * PiecesPerRadian));
            var points = new List<(double X, double Y)>(n + 1);
            for (var k = 0; k <= n; k++)
            {
                var a = startAngle + signedSweep * k / n;
                points.Add((cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
            }
            return points;
        }

        /// <summary>The same arc, starting at its native start point (the plugin reads native values only; the angle maths is here).</summary>
        public static List<(double X, double Y)> ArcPointsFromStart(double cx, double cy, double radius, double startX, double startY, double signedSweep) =>
            ArcPoints(cx, cy, radius, Math.Atan2(startY - cy, startX - cx), signedSweep);

        /// <summary>The arc of a polyline segment with this bulge (tan of a quarter of the included angle; + = counter-clockwise).</summary>
        public static List<(double X, double Y)> BulgePoints((double X, double Y) a, (double X, double Y) b, double bulge)
        {
            if (Math.Abs(bulge) < 1e-12) return new List<(double X, double Y)> { a, b };
            var chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (chord < 1e-12) return new List<(double X, double Y)> { a, b };
            var sweep = 4 * Math.Atan(bulge);                       // signed included angle
            var radius = chord / (2 * Math.Sin(Math.Abs(sweep) / 2));
            // centre: on the perpendicular bisector, to the left of a→b for a counter-clockwise arc
            var mx = (a.X + b.X) / 2;
            var my = (a.Y + b.Y) / 2;
            var d = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
            var ux = -(b.Y - a.Y) / chord;
            var uy = (b.X - a.X) / chord;
            var side = (Math.Abs(sweep) > Math.PI) == (sweep > 0) ? -1 : 1;
            var cx = mx + side * d * ux;
            var cy = my + side * d * uy;
            return ArcPoints(cx, cy, radius, Math.Atan2(a.Y - cy, a.X - cx), sweep);
        }

        /// <summary>Appends a piece to a ring, turned so that it continues from the ring's last point (the reference rule).</summary>
        public static void Append(List<(double X, double Y)> ring, List<(double X, double Y)> piece)
        {
            if (piece.Count == 0) return;
            if (ring.Count > 0)
            {
                var last = ring[^1];
                if (Dist(last, piece[0]) > Dist(last, piece[^1]) + 1e-6) piece.Reverse();
                ring.AddRange(piece.Skip(1));
            }
            else ring.AddRange(piece);
        }

        public static string Format(IEnumerable<IReadOnlyList<(double X, double Y)>> rings) =>
            string.Join("|", rings.Select(r => QuantityGeometryEvidence.FormatVertices(r.ToList())));

        public static bool TryParse(string? text, out List<List<(double X, double Y)>> rings)
        {
            rings = new List<List<(double X, double Y)>>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var part in text.Split('|'))
            {
                if (!QuantityGeometryEvidence.TryParseVertices(part, out var vertices)) { rings.Clear(); return false; }
                rings.Add(vertices.ToList());
            }
            return rings.Count > 0;
        }

        /// <summary>
        /// One loop of a failure diagnostic ("line=x,y -> x,y", "arc: start=…; end=…; centre=…; radius=…; angles=a0,a1;
        /// clockwise=…") as a ring; null when an item is neither.
        /// </summary>
        public static List<(double X, double Y)>? RingFromDiagnosticItems(IEnumerable<string> items)
        {
            var ring = new List<(double X, double Y)>();
            foreach (var item in items)
            {
                if (item.StartsWith("line=", StringComparison.Ordinal))
                {
                    var ends = item["line=".Length..].Split("->");
                    if (ends.Length != 2 || !QuantityGeometryEvidence.TryParsePoint(ends[0].Trim(), out var x1, out var y1) ||
                        !QuantityGeometryEvidence.TryParsePoint(ends[1].Trim(), out var x2, out var y2)) return null;
                    Append(ring, new List<(double X, double Y)> { (x1, y1), (x2, y2) });
                }
                else if (item.StartsWith("arc:", StringComparison.Ordinal))
                {
                    var fields = item["arc:".Length..].Split(';').Select(f => f.Trim().Split('=', 2))
                        .Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
                    if (!fields.TryGetValue("start", out var s) || !QuantityGeometryEvidence.TryParsePoint(s, out var sx, out var sy) ||
                        !fields.TryGetValue("centre", out var c) || !QuantityGeometryEvidence.TryParsePoint(c, out var cx, out var cy) ||
                        !fields.TryGetValue("radius", out var rText) || !double.TryParse(rText, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ||
                        !fields.TryGetValue("angles", out var angles) || !QuantityGeometryEvidence.TryParsePoint(angles, out var a0, out var a1))
                        return null;
                    var clockwise = fields.TryGetValue("clockwise", out var cw) && string.Equals(cw, "True", StringComparison.OrdinalIgnoreCase);
                    var sweep = a1 - a0;
                    Append(ring, ArcPoints(cx, cy, r, Math.Atan2(sy - cy, sx - cx), clockwise ? -sweep : sweep));
                }
                else return null;
            }
            return ring;
        }

        private static double Dist((double X, double Y) a, (double X, double Y) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }
}
