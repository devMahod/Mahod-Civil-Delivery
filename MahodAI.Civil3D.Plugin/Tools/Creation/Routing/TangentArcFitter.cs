using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Fits an engineer-style horizontal alignment (long straight tangents joined by ONE curve
    /// per real direction change) to a routed centerline.
    ///
    /// WHY THIS EXISTS. The legacy chain turned the routed path into PIs by *densification*:
    /// <see cref="GridRouter"/> split any tangent whose underlying path bowed more than ~2 cells
    /// (6 m at the default 3 m grid) off its chord, and <see cref="CurveAttacher"/> then filleted
    /// every resulting PI independently. On a gentle bend the mid-ordinate m = L²/8R reaches 6 m
    /// after only L = √(48R) ≈ 138 m at R = 400 m — so ONE clean 400 m curve arrived as a chain of
    /// PIs roughly every 140 m, each getting its own spiral-curve-spiral, each competing for
    /// tangent length, each relaxing radius/spiral when it lost. That is the "many small turns +
    /// mismatched radii" look an engineer reads as unprofessional.
    ///
    /// WHAT THIS DOES INSTEAD. The routed path is treated as *evidence of where the road may go*,
    /// not as geometry to trace:
    ///   1. Resample to uniform spacing and measure the smoothed per-sample heading change.
    ///   2. Classify each sample as STRAIGHT (curvature below 1/(factor·R) — i.e. so gentle that a
    ///      straight tangent represents it) or TURN, and group into runs. Straight runs shorter
    ///      than <c>minTangentM</c> are absorbed into the turn beside them (that is what fuses a
    ///      broken-back pair of curves into one turn); turn runs that bend less than
    ///      <see cref="MinDeflectionRad"/> in total are absorbed into the straight beside them.
    ///   3. Fit a line (total least squares) through each surviving straight run — these are the
    ///      tangents. Consecutive tangents INTERSECT at a PI: one PI per real direction change.
    ///   4. Validate, and only then accept.
    ///
    /// SAFETY CONTRACT. A fit is returned only when it is provably drawable:
    ///   • every tangent chord is mask-legal (<c>chordViable</c> — the same integer distance-to-
    ///     boundary field the post-creation containment readback uses);
    ///   • every curve at the design radius is sampled and every sample is <c>pointLegal</c>, so
    ///     the arc bulge cannot leave the buildable area (the spiral of an SCS lies between the
    ///     arc and the tangents, so checking the arc bounds the whole envelope);
    ///   • adjacent curves fit on their shared tangent (T = R·tan(Δ/2) + Ls/2 per side, plus
    ///     <c>minTangentM</c> of true straight between them), or the offending PI is merged out.
    /// Attempts run from aggressive (straightest) to conservative (tracks the corridor closest);
    /// the first legal one wins. If none is legal the fitter returns <c>null</c> and the caller
    /// keeps its legacy densified PI list — worst case is exactly today's behaviour, never worse.
    ///
    /// Pure geometry (no AutoCAD host types), so it is unit-testable like
    /// <see cref="AlignmentSimplifier"/> and <see cref="CurvatureSimplifier"/>.
    /// </summary>
    public static class TangentArcFitter
    {
        /// <summary>A turn run bending less than this in total is not a real curve — absorb it into the tangent.</summary>
        public const double MinDeflectionRad = 0.035;   // ≈2°

        /// <summary>Beyond this deflection a single PI is not a road bend any more — reject the fit.</summary>
        public const double MaxDeflectionRad = 2.09;    // ≈120°

        /// <summary>Straightness ladder: κ &lt; 1/(factor·R) counts as straight. Aggressive → conservative.</summary>
        private static readonly double[] StraightnessLadder = { 2.5, 4.0, 6.0, 9.0 };

        public sealed class FitResult
        {
            /// <summary>Alignment PIs: route start, one per real direction change, route end.</summary>
            public Pt2[] Pis { get; init; } = Array.Empty<Pt2>();

            /// <summary>Number of straight tangents the fit is built from.</summary>
            public int TangentCount { get; init; }

            /// <summary>Which rung of the straightness ladder produced the accepted fit (lower = straighter).</summary>
            public double StraightnessFactor { get; init; }

            /// <summary>PIs dropped by the tangent-feasibility merge loop (curves that could not share a tangent).</summary>
            public int MergedPis { get; init; }

            /// <summary>True when a PI kept a tangent shorter than the curves need — CurveAttacher will relax there.</summary>
            public bool HasTightTangent { get; init; }
        }

        /// <param name="path">Routed centerline (dense polyline). First/last points are the pinned route endpoints.</param>
        /// <param name="designRadiusM">Design (comfort) curve radius in metres. ≤0 disables fitting.</param>
        /// <param name="spiralLenM">Per-side transition spiral length in metres (0 for plain arcs).</param>
        /// <param name="minTangentM">Minimum true straight required between two consecutive curves.</param>
        /// <param name="chordViable">True iff the straight chord p0→p1 is buildable (on-mask at clearance).</param>
        /// <param name="pointLegal">True iff a single world point is buildable — used to sample curve bulges.</param>
        /// <returns>The accepted fit, or null when no rung of the ladder produced a legal alignment.</returns>
        public static FitResult? Fit(
            IReadOnlyList<Pt2> path,
            double designRadiusM,
            double spiralLenM,
            double minTangentM,
            Func<Pt2, Pt2, bool> chordViable,
            Func<Pt2, bool> pointLegal)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (chordViable == null) throw new ArgumentNullException(nameof(chordViable));
            if (pointLegal == null) throw new ArgumentNullException(nameof(pointLegal));
            if (designRadiusM <= 0 || path.Count < 3) return null;
            if (spiralLenM < 0) spiralLenM = 0;
            if (minTangentM < 0) minTangentM = 0;

            // Sample fine enough to resolve a design-radius curve (≈25 samples per radian of turn)
            // but never below 5 m (noise) or above 20 m (misses short bends).
            double ds = Math.Clamp(designRadiusM / 25.0, 5.0, 20.0);
            var pts = Resample(path, ds);
            if (pts.Count < 4) return null;

            Pt2 a = path[0], b = path[path.Count - 1];

            // The whole route is one straight shot — the ideal outcome, and the ladder below
            // cannot express it (it needs two tangents to intersect). Take it directly.
            if (chordViable(a, b) && IsWithinCorridorOf(pts, a, b, designRadiusM))
            {
                return new FitResult
                {
                    Pis = new[] { a, b },
                    TangentCount = 1,
                    StraightnessFactor = 0.0,
                };
            }

            foreach (double factor in StraightnessLadder)
            {
                var fit = TryFit(pts, a, b, designRadiusM, spiralLenM, minTangentM, ds, factor,
                                 chordViable, pointLegal);
                if (fit != null) return fit;
            }
            return null;
        }

        // ── One rung of the ladder ──────────────────────────────────────────

        private static FitResult? TryFit(
            List<Pt2> pts, Pt2 a, Pt2 b,
            double radius, double spiralLenM, double minTangentM, double ds, double factor,
            Func<Pt2, Pt2, bool> chordViable, Func<Pt2, bool> pointLegal)
        {
            var runs = Segment(pts, ds, radius, factor, minTangentM);
            var tangents = new List<Line>();
            foreach (var r in runs)
                if (r.Straight) tangents.Add(FitLine(pts, r.Start, r.End));

            // No usable straight run (e.g. the route is one continuous bend): synthesise tangents
            // from the two ends, which is exactly the "tangent-in, one curve, tangent-out" shape.
            if (tangents.Count < 2)
            {
                int k = Math.Max(3, (int)Math.Round(Math.Max(minTangentM, 3 * ds) / ds));
                k = Math.Min(k, pts.Count / 3);
                if (k < 2) return null;
                tangents.Clear();
                tangents.Add(FitLine(pts, 0, k));
                tangents.Add(FitLine(pts, pts.Count - 1 - k, pts.Count - 1));
            }

            // Intersect consecutive tangents → one PI per direction change. Near-parallel pairs
            // are the same tangent seen twice (a wobble the classifier split) — fuse them.
            var pis = new List<Pt2>();
            int i = 0;
            while (i < tangents.Count - 1)
            {
                Line l1 = tangents[i], l2 = tangents[i + 1];
                double defl = Math.Abs(Norm(l2.Heading - l1.Heading));
                if (defl < MinDeflectionRad || !TryIntersect(l1, l2, out Pt2 pi))
                {
                    tangents[i + 1] = FuseCollinear(l1, l2);
                    tangents.RemoveAt(i);
                    continue;
                }
                if (defl > MaxDeflectionRad) return null;

                // The PI must lie AHEAD of the first run and BEHIND the second, otherwise the two
                // fitted lines meet somewhere the road never goes (a bad segmentation).
                if (Project(pi, l1) <= 0 || Project(pi, l2) >= 0) return null;

                pis.Add(pi);
                i++;
            }

            var all = new List<Pt2> { a };
            foreach (var p in pis)
                if (all[all.Count - 1].DistanceTo(p) > 1e-3) all.Add(p);
            if (all[all.Count - 1].DistanceTo(b) > 1e-3) all.Add(b);
            if (all.Count < 2) return null;

            // The first/last leg must actually run forward from A / into B.
            if (all.Count >= 3)
            {
                if (Dot(Sub(all[1], all[0]), tangents[0].Dir) <= 0) return null;
                if (Dot(Sub(all[all.Count - 1], all[all.Count - 2]),
                        tangents[tangents.Count - 1].Dir) <= 0) return null;
            }

            // ── Validation ──────────────────────────────────────────────────
            if (!ChordsViable(all, chordViable)) return null;

            // Tangent feasibility: two curves sharing a tangent need T1 + T2 + minTangent of it.
            // When they do not fit, drop the weaker (smaller-deflection) PI — but only if the
            // straightened result is still mask-legal. Otherwise keep it and let CurveAttacher
            // relax there (reported as a deviation, never silent).
            int merged = 0;
            bool tight = false;
            for (int guard = 0; guard < all.Count + 4; guard++)
            {
                int offender = FindTangentViolation(all, radius, spiralLenM, minTangentM);
                if (offender < 0) break;
                var trial = new List<Pt2>(all);
                trial.RemoveAt(offender);
                if (trial.Count >= 2 && ChordsViable(trial, chordViable))
                {
                    all = trial;
                    merged++;
                }
                else
                {
                    tight = true;
                    break;
                }
            }

            // Curve bulges must stay inside the buildable area — the containment readback that
            // runs after creation is a HARD gate, so prove it here rather than be rejected later.
            if (!CurvesLegal(all, radius, pointLegal)) return null;

            return new FitResult
            {
                Pis = all.ToArray(),
                TangentCount = all.Count - 1,
                StraightnessFactor = factor,
                MergedPis = merged,
                HasTightTangent = tight,
            };
        }

        // ── Segmentation ────────────────────────────────────────────────────

        private readonly struct Run
        {
            public Run(int start, int end, bool straight) { Start = start; End = end; Straight = straight; }
            public int Start { get; }
            public int End { get; }
            public bool Straight { get; }
        }

        /// <summary>
        /// Splits the resampled path into alternating STRAIGHT / TURN runs, then applies the two
        /// engineering rules that decide what counts as a real curve: a straight shorter than
        /// <paramref name="minTangentM"/> cannot separate two curves (absorb it), and a turn that
        /// bends less than <see cref="MinDeflectionRad"/> in total is not a curve (absorb it).
        /// </summary>
        private static List<Run> Segment(List<Pt2> pts, double ds, double radius, double factor,
                                         double minTangentM)
        {
            int n = pts.Count;
            var heading = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
                heading[i] = Math.Atan2(pts[i + 1].Y - pts[i].Y, pts[i + 1].X - pts[i].X);

            // Per-vertex turn, smoothed over a ~60 m window so grid noise cannot fake a corner.
            var turn = new double[n];
            for (int i = 1; i < n - 1; i++) turn[i] = Norm(heading[i] - heading[i - 1]);
            int w = Math.Clamp((int)Math.Round(60.0 / ds), 1, 5);
            var smooth = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0; int cnt = 0;
                for (int j = i - w; j <= i + w; j++)
                {
                    if (j < 1 || j > n - 2) continue;
                    sum += turn[j]; cnt++;
                }
                smooth[i] = cnt > 0 ? sum / cnt : 0.0;
            }

            // κ = Δheading / ds; straight ⇔ κ < 1/(factor·R) ⇔ |Δheading| < ds/(factor·R).
            double turnLimit = ds / (factor * radius);
            var isStraight = new bool[n];
            for (int i = 0; i < n; i++) isStraight[i] = Math.Abs(smooth[i]) <= turnLimit;
            isStraight[0] = isStraight[Math.Min(1, n - 1)];
            isStraight[n - 1] = isStraight[Math.Max(0, n - 2)];

            var runs = BuildRuns(isStraight, n);

            for (int pass = 0; pass < 10; pass++)
            {
                bool changed = false;
                for (int r = 0; r < runs.Count; r++)
                {
                    var run = runs[r];
                    double len = (run.End - run.Start) * ds;
                    bool interior = r > 0 && r < runs.Count - 1;

                    if (run.Straight && len < minTangentM && interior)
                    {
                        // Too short to be a real tangent between two curves — the two curves it
                        // separates are one turn (this is the broken-back fuse).
                        for (int k = run.Start; k <= run.End; k++) isStraight[k] = false;
                        changed = true;
                    }
                    else if (!run.Straight)
                    {
                        double total = 0;
                        for (int k = run.Start; k <= run.End; k++) total += turn[k];
                        if (Math.Abs(total) < MinDeflectionRad)
                        {
                            for (int k = run.Start; k <= run.End; k++) isStraight[k] = true;
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
                runs = BuildRuns(isStraight, n);
            }

            return runs;
        }

        private static List<Run> BuildRuns(bool[] isStraight, int n)
        {
            var runs = new List<Run>();
            int start = 0;
            for (int i = 1; i <= n; i++)
            {
                if (i == n || isStraight[i] != isStraight[start])
                {
                    runs.Add(new Run(start, i - 1, isStraight[start]));
                    start = i;
                }
            }
            return runs;
        }

        // ── Line fitting ────────────────────────────────────────────────────

        private readonly struct Line
        {
            public Line(Pt2 point, Pt2 dir) { Point = point; Dir = dir; }
            public Pt2 Point { get; }
            public Pt2 Dir { get; }
            public double Heading => Math.Atan2(Dir.Y, Dir.X);
        }

        /// <summary>
        /// Total-least-squares line through the samples of one straight run (the principal axis of
        /// their covariance), oriented along travel. Falls back to the run's chord when the samples
        /// are degenerate.
        /// </summary>
        private static Line FitLine(List<Pt2> pts, int from, int to)
        {
            from = Math.Clamp(from, 0, pts.Count - 1);
            to = Math.Clamp(to, 0, pts.Count - 1);
            if (to <= from) to = Math.Min(pts.Count - 1, from + 1);

            double cx = 0, cy = 0;
            int cnt = to - from + 1;
            for (int i = from; i <= to; i++) { cx += pts[i].X; cy += pts[i].Y; }
            cx /= cnt; cy /= cnt;

            double sxx = 0, sxy = 0, syy = 0;
            for (int i = from; i <= to; i++)
            {
                double dx = pts[i].X - cx, dy = pts[i].Y - cy;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }

            Pt2 chord = Sub(pts[to], pts[from]);
            Pt2 dir;
            if (sxx + syy < 1e-12)
            {
                dir = Normalize(chord);
            }
            else
            {
                double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
                dir = new Pt2(Math.Cos(theta), Math.Sin(theta));
            }
            if (Dot(dir, chord) < 0) dir = new Pt2(-dir.X, -dir.Y);
            if (Math.Abs(dir.X) + Math.Abs(dir.Y) < 1e-9) dir = new Pt2(1, 0);
            return new Line(new Pt2(cx, cy), dir);
        }

        private static Line FuseCollinear(Line l1, Line l2)
        {
            // Average heading (they are near-parallel by construction) through the midpoint of the
            // two anchors, so the fused tangent represents both runs.
            double h = l1.Heading + 0.5 * Norm(l2.Heading - l1.Heading);
            var mid = new Pt2((l1.Point.X + l2.Point.X) * 0.5, (l1.Point.Y + l2.Point.Y) * 0.5);
            return new Line(mid, new Pt2(Math.Cos(h), Math.Sin(h)));
        }

        private static bool TryIntersect(Line l1, Line l2, out Pt2 hit)
        {
            hit = default;
            double denom = l1.Dir.X * l2.Dir.Y - l1.Dir.Y * l2.Dir.X;
            if (Math.Abs(denom) < 1e-9) return false;
            double dx = l2.Point.X - l1.Point.X, dy = l2.Point.Y - l1.Point.Y;
            double t = (dx * l2.Dir.Y - dy * l2.Dir.X) / denom;
            hit = new Pt2(l1.Point.X + t * l1.Dir.X, l1.Point.Y + t * l1.Dir.Y);
            return true;
        }

        /// <summary>Signed distance of p from the line's anchor, measured along the line direction.</summary>
        private static double Project(Pt2 p, Line l) => Dot(Sub(p, l.Point), l.Dir);

        // ── Validation helpers ──────────────────────────────────────────────

        private static bool ChordsViable(List<Pt2> pis, Func<Pt2, Pt2, bool> chordViable)
        {
            for (int i = 0; i < pis.Count - 1; i++)
                if (!chordViable(pis[i], pis[i + 1])) return false;
            return true;
        }

        /// <summary>
        /// Index of the interior PI to drop when two curves cannot share their tangent, or -1 when
        /// every curve fits. T = R·tan(Δ/2) + Ls/2 is the run each curve consumes along the shared
        /// segment; <paramref name="minTangentM"/> is the true straight required between them.
        /// </summary>
        private static int FindTangentViolation(List<Pt2> pis, double radius, double spiralLenM,
                                                double minTangentM)
        {
            int n = pis.Count;
            for (int i = 0; i < n - 1; i++)
            {
                double seg = pis[i].DistanceTo(pis[i + 1]);
                double t0 = i >= 1 ? Tangent(Deflection(pis[i - 1], pis[i], pis[i + 1]), radius, spiralLenM) : 0.0;
                double t1 = i + 2 <= n - 1 ? Tangent(Deflection(pis[i], pis[i + 1], pis[i + 2]), radius, spiralLenM) : 0.0;
                double need = t0 + t1 + (t0 > 0 && t1 > 0 ? minTangentM : 0.0);
                if (seg + 1e-6 >= need) continue;

                // Drop the gentler of the two competing curves — it is the one whose removal
                // changes the road least.
                if (t0 > 0 && t1 > 0)
                    return Deflection(pis[i - 1], pis[i], pis[i + 1]) <= Deflection(pis[i], pis[i + 1], pis[i + 2])
                        ? i : i + 1;
                return t0 > 0 ? i : i + 1;
            }
            return -1;
        }

        /// <summary>
        /// Samples every curve at the design radius and checks each sample is buildable. The SCS
        /// spiral lies between the arc and the tangent lines, so an arc that clears also bounds the
        /// spiral case. A PI whose tangents are too short to host the arc is skipped (already
        /// reported by <see cref="FindTangentViolation"/>; CurveAttacher relaxes it).
        /// </summary>
        private static bool CurvesLegal(List<Pt2> pis, double radius, Func<Pt2, bool> pointLegal)
        {
            for (int i = 1; i < pis.Count - 1; i++)
            {
                Pt2 u = Normalize(Sub(pis[i], pis[i - 1]));
                Pt2 v = Normalize(Sub(pis[i + 1], pis[i]));
                double defl = Math.Abs(Norm(Math.Atan2(v.Y, v.X) - Math.Atan2(u.Y, u.X)));
                if (defl < 1e-6) continue;

                double t = radius * Math.Tan(Math.Min(defl, Math.PI - 1e-3) * 0.5);
                if (t > pis[i - 1].DistanceTo(pis[i]) || t > pis[i].DistanceTo(pis[i + 1])) continue;

                var ts = new Pt2(pis[i].X - u.X * t, pis[i].Y - u.Y * t);
                double sign = (u.X * v.Y - u.Y * v.X) >= 0 ? 1.0 : -1.0;
                var center = new Pt2(ts.X - u.Y * sign * radius, ts.Y + u.X * sign * radius);

                double phi0 = Math.Atan2(ts.Y - center.Y, ts.X - center.X);
                int steps = Math.Max(2, (int)Math.Ceiling(radius * defl / 5.0));
                for (int k = 0; k <= steps; k++)
                {
                    double phi = phi0 + sign * defl * k / steps;
                    var p = new Pt2(center.X + radius * Math.Cos(phi), center.Y + radius * Math.Sin(phi));
                    if (!pointLegal(p)) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when the straight A→B chord actually represents the routed path — no sample bows
        /// further off it than a single design-radius curve could absorb. Guards the "one straight
        /// shot" shortcut against collapsing a real detour (e.g. a route around an obstacle whose
        /// chord happens to be mask-legal) into a line the router deliberately avoided.
        /// </summary>
        private static bool IsWithinCorridorOf(List<Pt2> pts, Pt2 a, Pt2 b, double radius)
        {
            // A curve of radius R spanning a chord L has mid-ordinate L²/8R; allow that much bow
            // (capped) before declaring the path "not really straight".
            double span = a.DistanceTo(b);
            if (span < 1e-6) return false;
            double tol = Math.Min(span * span / (8.0 * radius), 0.05 * span);
            foreach (var p in pts)
                if (PerpDistance(p, a, b) > tol) return false;
            return true;
        }

        // ── Small geometry helpers ──────────────────────────────────────────

        private static double Tangent(double defl, double radius, double spiralLenM)
        {
            double half = Math.Min(defl, Math.PI - 1e-3) * 0.5;
            return radius * Math.Tan(half) + spiralLenM * 0.5;
        }

        private static double Deflection(Pt2 a, Pt2 b, Pt2 c)
        {
            Pt2 u = Sub(b, a), v = Sub(c, b);
            double m1 = Math.Sqrt(u.X * u.X + u.Y * u.Y), m2 = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (m1 < 1e-9 || m2 < 1e-9) return 0.0;
            double cos = Math.Clamp((u.X * v.X + u.Y * v.Y) / (m1 * m2), -1.0, 1.0);
            return Math.Acos(cos);
        }

        private static double PerpDistance(Pt2 p, Pt2 a, Pt2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12) return p.DistanceTo(a);
            double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0.0, 1.0);
            double px = a.X + t * dx, py = a.Y + t * dy;
            return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
        }

        private static List<Pt2> Resample(IReadOnlyList<Pt2> pts, double ds)
        {
            var outp = new List<Pt2>();
            if (pts.Count == 0 || ds <= 0) return outp;
            outp.Add(pts[0]);
            double carried = 0.0;
            for (int i = 1; i < pts.Count; i++)
            {
                Pt2 a = pts[i - 1], b = pts[i];
                double seg = a.DistanceTo(b);
                if (seg < 1e-9) continue;
                double dirx = (b.X - a.X) / seg, diry = (b.Y - a.Y) / seg;
                double along = ds - carried;
                while (along <= seg + 1e-9)
                {
                    outp.Add(new Pt2(a.X + dirx * along, a.Y + diry * along));
                    along += ds;
                }
                carried = seg - (along - ds);
            }
            var last = pts[pts.Count - 1];
            if (outp[outp.Count - 1].DistanceTo(last) > ds * 0.25) outp.Add(last);
            else outp[outp.Count - 1] = last;
            return outp;
        }

        private static double Norm(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }

        private static Pt2 Sub(Pt2 p, Pt2 q) => new Pt2(p.X - q.X, p.Y - q.Y);
        private static double Dot(Pt2 p, Pt2 q) => p.X * q.X + p.Y * q.Y;

        private static Pt2 Normalize(Pt2 p)
        {
            double m = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            return m < 1e-12 ? new Pt2(1, 0) : new Pt2(p.X / m, p.Y / m);
        }
    }
}
