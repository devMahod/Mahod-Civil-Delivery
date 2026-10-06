using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Derives the EXISTING road's centerline from survey linework so the routed alignment can be
    /// drawn ON TOP of the old road instead of wandering the survey band.
    ///
    /// WHY. On rebuild/upgrade jobs the drawing already contains the answer: the surveyed asphalt
    /// edges (<c>KAV-ASFALT</c>), kerbs (<c>EVEN-SAFA</c>) and shoulders bracket the old road, and
    /// the engineers' blue corridor loop is drawn as a roughly symmetric buffer around it
    /// (measured on road 73: loop midline within ~4.5 m of the asphalt centerline). Routing
    /// "centered between the SURFACE edges" ignores all of that and wanders wherever the survey
    /// band widens. This class turns the linework into a dense centerline that the priority-path
    /// machinery (<see cref="PriorityPathPlanner"/>) and <see cref="TangentArcFitter"/> consume.
    ///
    /// HOW.
    ///   1. <see cref="ZoneMidline"/>: split the closed corridor loop at the vertices nearest the
    ///      engineer's A/B picks, trim each side to the A/B abeam span, then midpoint-pair the
    ///      sides. A pairing that lands on the OPPOSITE chain's endpoint (a clamped projection)
    ///      is in end-cap territory and is skipped — caps are what bent the midline off the road
    ///      near the ends.
    ///   2. <see cref="Extract"/>: march that spine, cast a perpendicular section at every
    ///      station, intersect it with the road-edge chains, and detect the road as a PAIR of
    ///      hits a carriageway-width apart — anywhere across the section, so a road running well
    ///      off the corridor spine is still found. Pair midpoints converge on a global median
    ///      road-center (side roads and junction flares get gated out), a local-median gate kills
    ///      the flares that mimic a plausible absolute offset, and gap stations interpolate.
    ///      Ends are pinned to the exact A/B picks — the engineer's markers, not the corridor caps.
    ///
    /// Pure geometry (no AutoCAD host types) — unit-tested headless like
    /// <see cref="TangentArcFitter"/>. Host-side entity collection lives in
    /// <see cref="GuideDiscovery.CollectRoadEdges"/>.
    /// </summary>
    public static class RoadEvidenceExtractor
    {
        /// <summary>Sweep station spacing along the corridor spine.</summary>
        public const double SweepStepM = 10.0;

        /// <summary>Half-length of each perpendicular section (edges beyond this are invisible).</summary>
        public const double EdgeReachM = 45.0;

        /// <summary>Two edge hits closer than this are the same painted line, not a road.</summary>
        public const double MinRoadWidthM = 4.0;

        /// <summary>Two edge hits further apart than this are not one carriageway.</summary>
        public const double MaxRoadWidthM = 16.0;

        /// <summary>A road-center candidate may deviate this much from the global median center.</summary>
        public const double GlobalGateM = 6.0;

        /// <summary>…and this much from the local (±15 picks) median — junction flares jump more.</summary>
        public const double LocalGateM = 3.0;

        /// <summary>Fraction of stations that must have a real pair pick for the evidence to count.</summary>
        public const double DefaultMinCoverage = 0.35;

        private const double PairMinWidthM = 5.0;    // side-pairing sanity range: a corridor is at
        private const double PairMaxWidthM = 400.0;  // least a road wide and at most a few hundred m

        /// <summary>One survey polyline (already world-transformed, 2D) with its source layer.</summary>
        public sealed class EvidenceChain
        {
            public EvidenceChain(string layer, List<Pt2> points)
            {
                Layer = layer ?? "";
                Points = points ?? new List<Pt2>();
            }

            public string Layer { get; }
            public List<Pt2> Points { get; }
        }

        public sealed class Result
        {
            /// <summary>Dense centerline (~<see cref="SweepStepM"/> spacing), endpoints = the A/B picks.</summary>
            public List<Pt2> Centerline { get; init; } = new();

            /// <summary>Fraction of sweep stations with a real edge-pair pick (the rest interpolate).</summary>
            public double Coverage { get; init; }

            public double HalfWidthLeft { get; init; }
            public double HalfWidthRight { get; init; }

            /// <summary>Distinct source layers that contributed accepted edge picks.</summary>
            public List<string> LayersUsed { get; init; } = new();
        }

        private readonly struct PairCandidate
        {
            public PairCandidate(double mid, double lo, double hi, int chainLo, int chainHi)
            { Mid = mid; Lo = lo; Hi = hi; ChainLo = chainLo; ChainHi = chainHi; }
            public double Mid { get; }
            public double Lo { get; }
            public double Hi { get; }
            public int ChainLo { get; }
            public int ChainHi { get; }
        }

        /// <summary>
        /// Centerline of the existing road inside the corridor <paramref name="zoneLoop"/>, or null
        /// when the linework doesn't cover enough of the corridor (caller falls back to
        /// <see cref="ZoneMidline"/> and then to plain routing).
        /// </summary>
        public static Result? Extract(
            IReadOnlyList<Pt2> zoneLoop, Pt2 a, Pt2 b,
            IReadOnlyList<EvidenceChain> chains,
            double minCoverage = DefaultMinCoverage)
        {
            var sides = SplitLoop(zoneLoop, a, b);
            if (sides == null) return null;
            return Extract(sides.Value.Side1, sides.Value.Side2, a, b, chains, minCoverage);
        }

        /// <summary>
        /// Same extraction when the corridor arrives as TWO OPEN side polylines — engineers draw
        /// the blue corridor either as one closed box or as two separate parallel lines.
        /// </summary>
        public static Result? Extract(
            IReadOnlyList<Pt2> side1, IReadOnlyList<Pt2> side2, Pt2 a, Pt2 b,
            IReadOnlyList<EvidenceChain> chains,
            double minCoverage = DefaultMinCoverage)
        {
            if (chains == null || chains.Count == 0) return null;
            // The sweep axis is the UNPINNED midline: pinning its ends to the picks would kink
            // the axis when the road runs well off the corridor spine, and the sections near the
            // ends would be cast askew. Only the FINAL centerline gets pinned to the picks.
            var spine = SidesMidlineCore(side1, side2, a, b);
            if (spine == null || spine.Count < 4) return null;

            var axis = Resample(spine, SweepStepM);
            if (axis.Count < 4) return null;

            // Spatial hash of the evidence segments so each section only tests nearby ones.
            const double cell = 50.0;
            var grid = new Dictionary<(int, int), List<(Pt2 A, Pt2 B, int Chain)>>();
            for (int ci = 0; ci < chains.Count; ci++)
            {
                var pts = chains[ci].Points;
                for (int si = 0; si + 1 < pts.Count; si++)
                {
                    Pt2 p = pts[si], q = pts[si + 1];
                    int gx0 = (int)Math.Floor(Math.Min(p.X, q.X) / cell), gx1 = (int)Math.Floor(Math.Max(p.X, q.X) / cell);
                    int gy0 = (int)Math.Floor(Math.Min(p.Y, q.Y) / cell), gy1 = (int)Math.Floor(Math.Max(p.Y, q.Y) / cell);
                    for (int gx = gx0; gx <= gx1; gx++)
                        for (int gy = gy0; gy <= gy1; gy++)
                        {
                            if (!grid.TryGetValue((gx, gy), out var list))
                                grid[(gx, gy)] = list = new List<(Pt2, Pt2, int)>();
                            list.Add((p, q, ci));
                        }
                }
            }

            // Pass 1: all edge hits per station → carriageway-width PAIR candidates. A pair can
            // sit anywhere across the section, so a road far off the corridor spine still counts.
            int nSt = axis.Count;
            var normals = new (double X, double Y)?[nSt];
            var candidates = new List<PairCandidate>[nSt];
            for (int k = 0; k < nSt; k++)
            {
                Pt2 p = axis[k];
                double hx = k + 1 < nSt ? axis[k + 1].X - p.X : p.X - axis[k - 1].X;
                double hy = k + 1 < nSt ? axis[k + 1].Y - p.Y : p.Y - axis[k - 1].Y;
                double hl = Math.Sqrt(hx * hx + hy * hy);
                if (hl < 1e-9) continue;
                double nx = -hy / hl, ny = hx / hl;      // left normal
                normals[k] = (nx, ny);

                var pl = new Pt2(p.X + nx * EdgeReachM, p.Y + ny * EdgeReachM);
                var pr = new Pt2(p.X - nx * EdgeReachM, p.Y - ny * EdgeReachM);
                List<(double Off, int Chain)>? hits = null;
                int gx0 = (int)Math.Floor(Math.Min(pl.X, pr.X) / cell), gx1 = (int)Math.Floor(Math.Max(pl.X, pr.X) / cell);
                int gy0 = (int)Math.Floor(Math.Min(pl.Y, pr.Y) / cell), gy1 = (int)Math.Floor(Math.Max(pl.Y, pr.Y) / cell);
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var list)) continue;
                        foreach (var (sa, sb, ci) in list)
                        {
                            double? t = SegIntersect(pr, pl, sa, sb);
                            if (t == null) continue;
                            double off = t.Value * 2 * EdgeReachM - EdgeReachM;   // signed, + = left
                            (hits ??= new()).Add((off, ci));
                        }
                    }
                if (hits == null || hits.Count < 2) continue;
                hits.Sort((x, y) => x.Off.CompareTo(y.Off));

                var cands = new List<PairCandidate>();
                for (int i = 0; i < hits.Count; i++)
                    for (int j = i + 1; j < hits.Count; j++)
                    {
                        double w = hits[j].Off - hits[i].Off;
                        if (w < MinRoadWidthM) continue;
                        if (w > MaxRoadWidthM) break;   // sorted — wider only from here
                        cands.Add(new PairCandidate(
                            (hits[i].Off + hits[j].Off) / 2.0,
                            hits[i].Off, hits[j].Off, hits[i].Chain, hits[j].Chain));
                    }
                if (cands.Count > 0) candidates[k] = cands;
            }

            // Pass 2: converge on the global road-center. Seed with each station's candidate
            // nearest the spine, then twice re-pick nearest the running global median — this is
            // what separates the main road from a parallel service road inside the corridor.
            double gc = 0.0;
            bool seeded = false;
            for (int round = 0; round < 3; round++)
            {
                var mids = new List<double>();
                for (int k = 0; k < nSt; k++)
                {
                    var cands = candidates[k];
                    if (cands == null) continue;
                    PairCandidate? best = null;
                    double bd = double.MaxValue;
                    foreach (var c in cands)
                    {
                        double d = Math.Abs(c.Mid - (seeded ? gc : 0.0));
                        if (d < bd) { bd = d; best = c; }
                    }
                    if (best != null && (!seeded || bd < GlobalGateM)) mids.Add(best.Value.Mid);
                }
                if (mids.Count == 0) return null;
                gc = Median(mids);
                seeded = true;
            }

            var offsets = new double?[nSt];
            var chosen = new PairCandidate?[nSt];
            for (int k = 0; k < nSt; k++)
            {
                var cands = candidates[k];
                if (cands == null || normals[k] == null) continue;
                PairCandidate? best = null;
                double bd = double.MaxValue;
                foreach (var c in cands)
                {
                    double d = Math.Abs(c.Mid - gc);
                    if (d < bd) { bd = d; best = c; }
                }
                if (best != null && bd < GlobalGateM)
                {
                    offsets[k] = best.Value.Mid;
                    chosen[k] = best;
                }
            }

            // Pass 3: local-median gate — a flare at a plausible absolute offset still jumps
            // relative to its neighbours. Windows read a VALUE SNAPSHOT taken before any
            // rejection: nulling as we go and re-reading the array made the very first rejected
            // flare blow up every later window read ("Nullable object must have a value" on the
            // road-73 field run, 2026-07-28) — and gates must judge against the ORIGINAL
            // neighbourhood anyway, not a partially-emptied one.
            var picked = Enumerable.Range(0, nSt).Where(k => offsets[k] != null).ToList();
            var pickedVals = picked.Select(k => offsets[k]!.Value).ToList();
            for (int idx = 0; idx < picked.Count; idx++)
            {
                var window = new List<double>();
                for (int j = Math.Max(0, idx - 15); j < Math.Min(picked.Count, idx + 16); j++)
                    window.Add(pickedVals[j]);
                if (window.Count >= 5 &&
                    Math.Abs(pickedVals[idx] - Median(window)) > LocalGateM)
                {
                    offsets[picked[idx]] = null;
                    chosen[picked[idx]] = null;
                }
            }

            var known = Enumerable.Range(0, nSt).Where(k => offsets[k] != null).ToList();
            double coverage = (double)known.Count / nSt;
            if (known.Count < 4 || coverage < minCoverage) return null;

            // Interpolate the offset across gaps; extend flat at the ends.
            int prevIdx = -1;
            for (int k = 0; k < nSt; k++)
            {
                if (offsets[k] != null) { prevIdx = k; continue; }
                int next = -1;
                foreach (var kk in known) { if (kk > k) { next = kk; break; } }
                if (prevIdx < 0 && next < 0) offsets[k] = 0.0;
                else if (prevIdx < 0) offsets[k] = offsets[next];
                else if (next < 0) offsets[k] = offsets[prevIdx];
                else
                {
                    double t = (double)(k - prevIdx) / (next - prevIdx);
                    offsets[k] = offsets[prevIdx]!.Value * (1 - t) + offsets[next]!.Value * t;
                }
            }

            var centerline = new List<Pt2>(nSt);
            for (int k = 0; k < nSt; k++)
            {
                if (normals[k] == null) continue;
                var (nx, ny) = normals[k]!.Value;
                centerline.Add(new Pt2(axis[k].X + nx * offsets[k]!.Value,
                                       axis[k].Y + ny * offsets[k]!.Value));
            }
            if (centerline.Count < 4) return null;
            centerline = SmoothChain(centerline, 3);
            centerline = PinEnds(centerline, a, b);
            if (centerline.Count < 2) return null;

            var halfLeft = new List<double>();
            var halfRight = new List<double>();
            var layerIdx = new HashSet<int>();
            foreach (var k in known)
            {
                if (chosen[k] is not { } c) continue;
                halfLeft.Add(c.Hi - c.Mid);
                halfRight.Add(c.Mid - c.Lo);
                layerIdx.Add(c.ChainLo);
                layerIdx.Add(c.ChainHi);
            }

            return new Result
            {
                Centerline = centerline,
                Coverage = coverage,
                HalfWidthLeft = halfLeft.Count > 0 ? Median(halfLeft) : 0.0,
                HalfWidthRight = halfRight.Count > 0 ? Median(halfRight) : 0.0,
                LayersUsed = layerIdx.Select(ci => chains[ci].Layer)
                                     .Where(l => !string.IsNullOrEmpty(l))
                                     .Distinct().OrderBy(l => l).ToList(),
            };
        }

        /// <summary>
        /// Corridor spine: midline of the closed zone loop between the A/B picks, ends pinned to
        /// the exact picks. Null when the loop is degenerate. Used as the sweep axis and as the
        /// routing fallback when no road linework exists (measured ≤ ~4.5 m off the real road on
        /// road 73 — the engineers buffer the road symmetrically).
        /// </summary>
        public static List<Pt2>? ZoneMidline(IReadOnlyList<Pt2> zoneLoop, Pt2 a, Pt2 b)
        {
            var mid = ZoneMidlineCore(zoneLoop, a, b);
            return mid == null ? null : PinEnds(mid, a, b);
        }

        /// <summary>Corridor spine from two OPEN side chains, ends pinned to the exact picks.</summary>
        public static List<Pt2>? SidesMidline(IReadOnlyList<Pt2> side1, IReadOnlyList<Pt2> side2, Pt2 a, Pt2 b)
        {
            var mid = SidesMidlineCore(side1, side2, a, b);
            return mid == null ? null : PinEnds(mid, a, b);
        }

        /// <summary>The midline WITHOUT end pinning — the sweep axis (see <see cref="Extract"/>).</summary>
        private static List<Pt2>? ZoneMidlineCore(IReadOnlyList<Pt2> zoneLoop, Pt2 a, Pt2 b)
        {
            var sides = SplitLoop(zoneLoop, a, b);
            if (sides == null) return null;
            return SidesMidlineCore(sides.Value.Side1, sides.Value.Side2, a, b);
        }

        /// <summary>Splits a closed loop into its two sides at the vertices nearest the picks.</summary>
        private static (List<Pt2> Side1, List<Pt2> Side2)? SplitLoop(
            IReadOnlyList<Pt2> zoneLoop, Pt2 a, Pt2 b)
        {
            if (zoneLoop == null || zoneLoop.Count < 4) return null;

            int n = zoneLoop.Count;
            int ia = NearestVertex(zoneLoop, a);
            int ib = NearestVertex(zoneLoop, b);
            if (ia == ib) return null;

            var side1 = new List<Pt2>();
            for (int i = ib; i != ia; i = (i + 1) % n) side1.Add(zoneLoop[i]);
            side1.Add(zoneLoop[ia]);
            var side2 = new List<Pt2>();
            for (int i = ia; i != ib; i = (i + 1) % n) side2.Add(zoneLoop[i]);
            side2.Add(zoneLoop[ib]);
            side2.Reverse();                       // both sides now run B → A
            return (side1, side2);
        }

        private static List<Pt2>? SidesMidlineCore(
            IReadOnlyList<Pt2> side1In, IReadOnlyList<Pt2> side2In, Pt2 a, Pt2 b)
        {
            if (side1In == null || side1In.Count < 2) return null;
            if (side2In == null || side2In.Count < 2) return null;

            // Cut the shared corners / end-cap stubs (closed-loop case) and anything running
            // past the picks (open-pair case) away: keep each side between the pick abeams.
            var side1 = TrimChainBetween(new List<Pt2>(side1In), b, a);
            var side2 = TrimChainBetween(new List<Pt2>(side2In), b, a);
            if (side1.Count < 2 || side2.Count < 2) return null;

            var mid = new List<Pt2>();
            foreach (var p in Resample(side1, 20.0))
            {
                var (seg, t, q, d) = ProjectOnChain(side2, p);
                if (d <= PairMinWidthM || d >= PairMaxWidthM) continue;
                // A projection clamped to side2's very first or very last point means p sits
                // BEYOND side2's span — end-cap territory. Pairing against it drags the midline
                // toward the cap corner, which is exactly the end-hook bug. Skip it.
                bool clampedStart = seg == 0 && t <= 1e-9;
                bool clampedEnd = seg == side2.Count - 2 && t >= 1 - 1e-9;
                if (clampedStart || clampedEnd) continue;
                mid.Add(new Pt2((p.X + q.X) / 2.0, (p.Y + q.Y) / 2.0));
            }
            if (mid.Count < 4) return null;

            mid = SmoothChain(mid, 2);
            return TrimChainBetween(mid, a, b);
        }

        /// <summary>Even-odd point-in-polygon test against a closed loop.</summary>
        public static bool PointInLoop(IReadOnlyList<Pt2> loop, Pt2 p)
        {
            if (loop == null || loop.Count < 3) return false;
            bool c = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y) &&
                    p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                    c = !c;
            }
            return c;
        }

        // ── Chain helpers ───────────────────────────────────────────────────

        private static int NearestVertex(IReadOnlyList<Pt2> loop, Pt2 p)
        {
            int best = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < loop.Count; i++)
            {
                double d = loop[i].DistanceTo(p);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Cut a chain to the span between the projections of two points (chain order kept).</summary>
        private static List<Pt2> TrimChainBetween(List<Pt2> chain, Pt2 pa, Pt2 pb)
        {
            var (ia, ta, qa, _) = ProjectOnChain(chain, pa);
            var (ib, tb, qb, _) = ProjectOnChain(chain, pb);
            if (ia > ib || (ia == ib && ta > tb))
            {
                (ia, ta, qa, ib, tb, qb) = (ib, tb, qb, ia, ta, qa);
            }
            var outp = new List<Pt2> { qa };
            for (int i = ia + 1; i <= ib && i < chain.Count; i++) outp.Add(chain[i]);
            outp.Add(qb);
            return outp;
        }

        private static (int Seg, double T, Pt2 Point, double Dist) ProjectOnChain(List<Pt2> chain, Pt2 p)
        {
            int bi = 0; double bt = 0; Pt2 bq = chain[0]; double bd = double.MaxValue;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                Pt2 s = chain[i], e = chain[i + 1];
                double dx = e.X - s.X, dy = e.Y - s.Y;
                double l2 = dx * dx + dy * dy;
                if (l2 < 1e-12) continue;
                double t = Math.Clamp(((p.X - s.X) * dx + (p.Y - s.Y) * dy) / l2, 0.0, 1.0);
                var q = new Pt2(s.X + t * dx, s.Y + t * dy);
                double d = q.DistanceTo(p);
                if (d < bd) { bd = d; bi = i; bt = t; bq = q; }
            }
            return (bi, bt, bq, bd);
        }

        private static List<Pt2> Resample(IReadOnlyList<Pt2> pts, double ds)
        {
            var outp = new List<Pt2>();
            if (pts.Count == 0 || ds <= 0) return outp;
            outp.Add(pts[0]);
            double carried = 0.0;
            for (int i = 1; i < pts.Count; i++)
            {
                Pt2 s = pts[i - 1], e = pts[i];
                double seg = s.DistanceTo(e);
                if (seg < 1e-9) continue;
                double ux = (e.X - s.X) / seg, uy = (e.Y - s.Y) / seg;
                double along = ds - carried;
                while (along <= seg + 1e-9)
                {
                    outp.Add(new Pt2(s.X + ux * along, s.Y + uy * along));
                    along += ds;
                }
                carried = seg - (along - ds);
            }
            var last = pts[pts.Count - 1];
            if (outp[outp.Count - 1].DistanceTo(last) > ds * 0.25) outp.Add(last);
            else outp[outp.Count - 1] = last;
            return outp;
        }

        private static List<Pt2> SmoothChain(List<Pt2> chain, int halfWindow)
        {
            var outp = new List<Pt2>(chain.Count);
            for (int i = 0; i < chain.Count; i++)
            {
                double sx = 0, sy = 0; int cnt = 0;
                for (int j = Math.Max(0, i - halfWindow); j <= Math.Min(chain.Count - 1, i + halfWindow); j++)
                {
                    sx += chain[j].X; sy += chain[j].Y; cnt++;
                }
                outp.Add(new Pt2(sx / cnt, sy / cnt));
            }
            return outp;
        }

        /// <summary>Trim to the A/B abeam span and make the exact picks the literal endpoints.</summary>
        private static List<Pt2> PinEnds(List<Pt2> chain, Pt2 a, Pt2 b)
        {
            if (chain.Count < 2) return chain;
            var trimmed = TrimChainBetween(chain, a, b);
            if (trimmed.Count < 2) return chain;
            if (trimmed[0].DistanceTo(a) > trimmed[trimmed.Count - 1].DistanceTo(a))
                trimmed.Reverse();
            if (trimmed[0].DistanceTo(a) > 1.0) trimmed.Insert(0, a);
            else trimmed[0] = a;
            if (trimmed[trimmed.Count - 1].DistanceTo(b) > 1.0) trimmed.Add(b);
            else trimmed[trimmed.Count - 1] = b;
            return trimmed;
        }

        /// <summary>Parametric intersection of p1→p2 with s1→s2; returns t along p1→p2 or null.</summary>
        private static double? SegIntersect(Pt2 p1, Pt2 p2, Pt2 s1, Pt2 s2)
        {
            double d1x = p2.X - p1.X, d1y = p2.Y - p1.Y;
            double d2x = s2.X - s1.X, d2y = s2.Y - s1.Y;
            double den = d1x * d2y - d1y * d2x;
            if (Math.Abs(den) < 1e-12) return null;
            double t = ((s1.X - p1.X) * d2y - (s1.Y - p1.Y) * d2x) / den;
            double u = ((s1.X - p1.X) * d1y - (s1.Y - p1.Y) * d1x) / den;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return null;
            return t;
        }

        private static double Median(List<double> values)
        {
            var s = values.OrderBy(v => v).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : 0.5 * (s[m - 1] + s[m]);
        }
    }
}
