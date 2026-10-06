using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// The construction commands of the PLTOOLS set, as pure point math: <c>MPL</c> (mid-line
    /// between two curves), <c>R3P</c> (rectangle through three points) and <c>PL-P90</c> (a chain
    /// of mutually perpendicular segments). The AutoCAD layer supplies the picked points and turns
    /// the returned point list into a polyline.
    /// </summary>
    public static class PolylineBuilders
    {
        /// <summary>MPL's default number of sample points.</summary>
        public const int DefaultMidlineSamples = 100;

        /// <summary>
        /// MPL: the mid-line between two curves, each supplied as a polyline point chain
        /// (the caller flattens arcs/splines into points first).
        ///
        /// Chain A is sampled at <paramref name="samples"/> stations spaced evenly along its
        /// length; each sample is paired with the CLOSEST point on chain B and the midpoint is
        /// taken. Closest-point pairing (rather than pairing by station fraction) is what keeps
        /// the mid-line centred when the two curves have different lengths — the case where a
        /// fraction-based pairing drifts badly.
        /// </summary>
        public static List<Pt2> Midline(IReadOnlyList<Pt2> chainA, IReadOnlyList<Pt2> chainB, int samples = DefaultMidlineSamples)
        {
            if (chainA == null) throw new ArgumentNullException(nameof(chainA));
            if (chainB == null) throw new ArgumentNullException(nameof(chainB));

            var result = new List<Pt2>();
            if (chainA.Count < 2 || chainB.Count < 2) return result;
            if (samples < 2) samples = 2;

            for (int k = 0; k < samples; k++)
            {
                double f = (double)k / (samples - 1);
                var pa = PointAtFraction(chainA, f);
                var pb = ClosestPointOnChain(chainB, pa);
                result.Add(new Pt2((pa.X + pb.X) * 0.5, (pa.Y + pb.Y) * 0.5));
            }
            return result;
        }

        /// <summary>
        /// R3P: rectangle through three points. <paramref name="p1"/>→<paramref name="p2"/> is one
        /// side; <paramref name="p3"/> sets the opposite side's offset (its perpendicular distance
        /// from that line, on its own side). Returns the four corners in order, or an empty list
        /// for a degenerate input.
        /// </summary>
        public static List<Pt2> RectangleFrom3Points(Pt2 p1, Pt2 p2, Pt2 p3)
        {
            var corners = new List<Pt2>();
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) return corners;

            double ux = dx / len, uy = dy / len;
            // Left normal of p1→p2; the signed projection of p3 keeps the rectangle on p3's side.
            double nx = -uy, ny = ux;
            double h = (p3.X - p1.X) * nx + (p3.Y - p1.Y) * ny;
            if (Math.Abs(h) < 1e-12) return corners;         // all three points collinear

            corners.Add(p1);
            corners.Add(p2);
            corners.Add(new Pt2(p2.X + nx * h, p2.Y + ny * h));
            corners.Add(new Pt2(p1.X + nx * h, p1.Y + ny * h));
            return corners;
        }

        /// <summary>
        /// PL-P90: turns a rough click chain into a chain of mutually perpendicular segments,
        /// aligned to <paramref name="baseAngleRad"/> (0 = world X, the original's default; its
        /// "Angle" option supplies another).
        ///
        /// Each click extends the chain along whichever of the two axes the movement is dominant
        /// in, so a hand-drawn staircase comes out exactly orthogonal.
        /// </summary>
        public static List<Pt2> PerpendicularChain(IReadOnlyList<Pt2> picks, double baseAngleRad = 0.0)
        {
            if (picks == null) throw new ArgumentNullException(nameof(picks));
            var result = new List<Pt2>();
            if (picks.Count == 0) return result;

            double ca = Math.Cos(baseAngleRad), sa = Math.Sin(baseAngleRad);
            var current = picks[0];
            result.Add(current);

            for (int i = 1; i < picks.Count; i++)
            {
                double dx = picks[i].X - current.X;
                double dy = picks[i].Y - current.Y;

                // Movement in the rotated frame.
                double along = dx * ca + dy * sa;
                double across = -dx * sa + dy * ca;

                Pt2 next = Math.Abs(along) >= Math.Abs(across)
                    ? new Pt2(current.X + ca * along, current.Y + sa * along)
                    : new Pt2(current.X - sa * across, current.Y + ca * across);

                if (next.DistanceTo(current) < 1e-12) continue;   // duplicate click
                result.Add(next);
                current = next;
            }

            return result;
        }

        /// <summary>Point at a normalised distance <paramref name="fraction"/> along a point chain.</summary>
        public static Pt2 PointAtFraction(IReadOnlyList<Pt2> chain, double fraction)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (chain.Count == 0) return default;
            if (chain.Count == 1) return chain[0];

            if (fraction <= 0.0) return chain[0];
            if (fraction >= 1.0) return chain[^1];

            double total = 0.0;
            for (int i = 1; i < chain.Count; i++) total += chain[i - 1].DistanceTo(chain[i]);
            if (total <= 0.0) return chain[0];

            double target = total * fraction;
            double walked = 0.0;
            for (int i = 1; i < chain.Count; i++)
            {
                double seg = chain[i - 1].DistanceTo(chain[i]);
                if (walked + seg >= target)
                {
                    double t = seg <= 0.0 ? 0.0 : (target - walked) / seg;
                    return new Pt2(
                        chain[i - 1].X + (chain[i].X - chain[i - 1].X) * t,
                        chain[i - 1].Y + (chain[i].Y - chain[i - 1].Y) * t);
                }
                walked += seg;
            }
            return chain[^1];
        }

        /// <summary>Closest point on a point chain to <paramref name="p"/>.</summary>
        public static Pt2 ClosestPointOnChain(IReadOnlyList<Pt2> chain, Pt2 p)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (chain.Count == 0) return default;
            if (chain.Count == 1) return chain[0];

            var best = chain[0];
            double bestD = double.MaxValue;
            for (int i = 1; i < chain.Count; i++)
            {
                var q = BulgeMath.ClosestPointOnSegment(chain[i - 1], chain[i], 0.0, p, out _);
                double d = q.DistanceTo(p);
                if (d < bestD)
                {
                    bestD = d;
                    best = q;
                }
            }
            return best;
        }
    }
}
