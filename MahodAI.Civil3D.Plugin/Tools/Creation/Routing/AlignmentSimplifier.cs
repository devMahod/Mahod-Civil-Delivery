using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Post-fit straightening of a routed PI list. The router's densifier adds a PI
    /// wherever the raw A* path deviates from its chord (devThreshold ≈ 2 cells), so a
    /// physically straight road arrives as a chain of small grid-quantized segments —
    /// and <see cref="CurveAttacher"/> then fillets every PI independently. Short
    /// tangents force it to halve the radius or drop the curve entirely ("none"),
    /// which is the visible "sharp / wiggly" centerline.
    ///
    /// This pass removes interior PIs that are either:
    ///   (1) near-collinear — a straight run drawn as many segments, or
    ///   (2) a *minor* turn whose curve cannot fit at the design radius because the
    ///       adjacent tangent is too short (it would otherwise degrade in CurveAttacher).
    /// So straight stretches draw as a single tangent and real turns get one clean
    /// curve. It only ever REMOVES PIs (never adds or relocates), and every removal is
    /// gated on:
    ///   (a) the straight bypass chord staying mask-legal — the caller supplies
    ///       <c>chordViable</c>, which checks the SAME integer distance-to-boundary
    ///       field the containment readback uses, so an accepted chord also passes
    ///       containment; and
    ///   (b) the result staying radius-feasible — a merge is rejected if it would push
    ///       a surviving curve under the design radius (never trade one violation for
    ///       another).
    /// Worst case it removes nothing and returns the input unchanged. Pure geometry
    /// (no AutoCAD host types) so it is unit-testable like CurvatureSimplifier.
    /// </summary>
    public static class AlignmentSimplifier
    {
        /// <summary>
        /// Straighten a routed PI list by dropping redundant interior PIs.
        /// </summary>
        /// <param name="pis">PI list in world coordinates; first and last are the route endpoints (never removed).</param>
        /// <param name="designRadiusM">Target curve radius (m). ≤0 disables the min-tangent merge; the collinear merge still runs.</param>
        /// <param name="spiralLenM">Per-side spiral length (m) for SCS tangent accounting; 0 for plain arcs.</param>
        /// <param name="chordViable">Returns true iff the straight chord p0→p1 stays on the walkable mask at the required clearance. A PI is never removed when its bypass chord is not viable.</param>
        /// <param name="maxTangentM">When &gt;0, the same deliberate tangent-split cap the router's densifier honors: a PI is never removed if the resulting bypass chord would exceed this length, so requested straight-tangent splits survive (the pass still collapses finer wobble within the budget). 0 disables the cap.</param>
        /// <param name="collinearEpsRad">Deflection at/below which a PI is treated as a straight-run artifact and dropped (default ≈4.5°).</param>
        /// <param name="mergeThresholdRad">Largest deflection a PI may have to still be a min-tangent merge candidate (default ≈15°). Turns sharper than this are always preserved.</param>
        /// <param name="minTangentM">True straight required BETWEEN two consecutive curves, on top of the two curve tangents (0 = off, the historical behaviour). Two curves that leave less than this between them are broken-back and read as a wiggle, so the gentler PI becomes a merge candidate.</param>
        public static Pt2[] Straighten(
            IReadOnlyList<Pt2> pis,
            double designRadiusM,
            double spiralLenM,
            Func<Pt2, Pt2, bool> chordViable,
            double maxTangentM = 0.0,
            double collinearEpsRad = 0.08,
            double mergeThresholdRad = 0.26,
            double minTangentM = 0.0)
        {
            if (pis == null) throw new ArgumentNullException(nameof(pis));
            if (chordViable == null) throw new ArgumentNullException(nameof(chordViable));
            if (minTangentM < 0) minTangentM = 0;

            var pts = new List<Pt2>(pis);
            if (pts.Count < 3) return pts.ToArray();

            bool changed = true;
            int guard = 0;
            // guard caps the rescan loop; each pass can only shrink the list, so this
            // terminates well before the cap on any real route.
            while (changed && guard++ < 10000)
            {
                changed = false;
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    double defl = Deflection(pts[i - 1], pts[i], pts[i + 1]);
                    bool nearCollinear = defl <= collinearEpsRad;
                    bool minorOverlap = !nearCollinear
                        && designRadiusM > 0
                        && defl <= mergeThresholdRad
                        && ParticipatesInOverlap(pts, i, designRadiusM, spiralLenM, minTangentM);

                    if (!nearCollinear && !minorOverlap) continue;

                    // Honor a requested tangent-split: never merge into a chord longer
                    // than maxTangentM (the densifier added those PIs on purpose).
                    if (maxTangentM > 0 && pts[i - 1].DistanceTo(pts[i + 1]) > maxTangentM)
                        continue;

                    // The straight bypass must stay on the surface at clearance.
                    if (!chordViable(pts[i - 1], pts[i + 1])) continue;

                    // A min-tangent merge must not create a worse (radius-infeasible)
                    // seam. A near-collinear drop only shortens the polyline along an
                    // almost-straight run, so it cannot reduce tangent availability —
                    // skip the (stricter) feasibility guard there.
                    if (!nearCollinear &&
                        !RemovalKeepsRadiusFeasible(pts, i, designRadiusM, spiralLenM))
                        continue;

                    pts.RemoveAt(i);
                    changed = true;
                    i--; // re-examine the seam left by the removal
                }
            }

            return pts.ToArray();
        }

        /// <summary>Absolute turn angle (rad) at b for the polyline a-b-c. 0 = perfectly straight, π = hairpin.</summary>
        private static double Deflection(Pt2 a, Pt2 b, Pt2 c)
        {
            double v1x = b.X - a.X, v1y = b.Y - a.Y;
            double v2x = c.X - b.X, v2y = c.Y - b.Y;
            double m1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double m2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (m1 < 1e-9 || m2 < 1e-9) return 0.0; // coincident points → no turn
            double cos = (v1x * v2x + v1y * v2y) / (m1 * m2);
            if (cos > 1.0) cos = 1.0;
            else if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos);
        }

        /// <summary>
        /// Tangent distance from a PI to its curve's tangent point: T = R·tan(Δ/2) + Ls/2.
        /// This is exactly the run a curve of radius R (plus a per-side spiral Ls) consumes
        /// along each adjacent segment, so two consecutive curves fit iff the shared segment
        /// length ≥ the sum of their tangents.
        /// </summary>
        private static double Tangent(double defl, double radius, double spiralLenM)
        {
            // Clamp Δ away from π so tan stays finite for a (degenerate) near-hairpin.
            double half = Math.Min(defl, Math.PI - 1e-3) * 0.5;
            return radius * Math.Tan(half) + spiralLenM * 0.5;
        }

        /// <summary>
        /// True when the curves at PI i and PI i+1 cannot both fit on the segment between them —
        /// including <paramref name="minTangentM"/> of TRUE straight between the two curves, so a
        /// broken-back pair that technically fits but leaves no tangent still counts as an overlap.
        /// </summary>
        private static bool SegmentOverlaps(List<Pt2> pts, int i, double radius, double spiralLenM,
                                            double minTangentM = 0.0)
        {
            double seg = pts[i].DistanceTo(pts[i + 1]);
            double ti = (i >= 1)
                ? Tangent(Deflection(pts[i - 1], pts[i], pts[i + 1]), radius, spiralLenM)
                : 0.0;
            double tNext = (i + 1 < pts.Count - 1)
                ? Tangent(Deflection(pts[i], pts[i + 1], pts[i + 2]), radius, spiralLenM)
                : 0.0;
            double need = ti + tNext + (ti > 0 && tNext > 0 ? minTangentM : 0.0);
            return seg + 1e-6 < need;
        }

        /// <summary>True when PI i participates in a tangent overlap on either adjacent segment.</summary>
        private static bool ParticipatesInOverlap(List<Pt2> pts, int i, double radius, double spiralLenM,
                                                  double minTangentM = 0.0)
        {
            bool before = i - 1 >= 0 && SegmentOverlaps(pts, i - 1, radius, spiralLenM, minTangentM);
            bool after = i + 1 <= pts.Count - 1 && SegmentOverlaps(pts, i, radius, spiralLenM, minTangentM);
            return before || after;
        }

        /// <summary>
        /// After removing PI i the new adjacent pair is (i-1, i+1). The removal is only
        /// acceptable if the curves that survive at i-1 and i+1 still fit on the new
        /// segment at the design radius — i.e. it does not create a fresh tangent overlap.
        /// </summary>
        private static bool RemovalKeepsRadiusFeasible(List<Pt2> pts, int i, double radius, double spiralLenM)
        {
            if (radius <= 0) return true;
            int p = i - 1, q = i + 1;
            double seg = pts[p].DistanceTo(pts[q]);
            double tp = (p >= 1)
                ? Tangent(Deflection(pts[p - 1], pts[p], pts[q]), radius, spiralLenM)
                : 0.0;
            double tq = (q <= pts.Count - 1 - 1)
                ? Tangent(Deflection(pts[p], pts[q], pts[q + 1]), radius, spiralLenM)
                : 0.0;
            return seg + 1e-6 >= tp + tq;
        }
    }
}
