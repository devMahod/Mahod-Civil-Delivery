using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Text diagnostics for a routed centerline — answers, per stretch of the road, the one
    /// question that explains a "why did it bow off the straight line" complaint WITHOUT a
    /// screenshot: for each window of the raw path, compare the ACTUAL bowed path against the
    /// STRAIGHT chord across that window:
    ///   • does the straight chord stay ON the walkable mask?
    ///   • does it keep at least the required clearance?
    ///   • how much longer is the bowed path, and how much extra clearance did the bow buy?
    /// If the chord is viable (on-mask + adequate clearance) but the path bowed for only a
    /// little extra clearance, the bow is an UNNECESSARY medial-axis dive — exactly what the
    /// "centered mode dives into side-lobes" complaint is. If the chord leaves the mask or
    /// drops below clearance, the bow was FORCED (the band genuinely curves there).
    /// Pure geometry over the walkable mask + dtb; no Civil 3D dependency.
    /// </summary>
    public static class RouteDiagnostics
    {
        /// <summary>Approximate window length (m) each comparison line covers.</summary>
        private const double WindowM = 800.0;

        public static List<string> Report(
            bool[,] walkable,
            Envelope env,
            double cellSize,
            Pt2[] rawPath,
            int[,]? dtb,
            double minClearanceM)
        {
            var lines = new List<string>();
            if (rawPath == null || rawPath.Length < 2 || dtb == null)
            {
                lines.Add("[routed-diag] (no raw path / dtb available)");
                return lines;
            }
            int nx = walkable.GetLength(0), ny = walkable.GetLength(1);

            // Cumulative station along the raw path.
            var sta = new double[rawPath.Length];
            for (int i = 1; i < rawPath.Length; i++)
                sta[i] = sta[i - 1] + rawPath[i - 1].DistanceTo(rawPath[i]);
            double total = sta[^1];

            lines.Add($"[routed-diag] === CHORD-vs-PATH per ~{WindowM:F0} m window " +
                      $"(clr=clearance m; chord ON=stays on mask; reqClr={minClearanceM:F1} m) ===");
            lines.Add("[routed-diag] sta0..sta1 | chordLen pathLen extra% bow_m | chordOnMask chordMinClr | pathMinClr clrGain | bandW ctrOff kind | VERDICT   (kind: opn=open lobe/skirt, bnd=corridor, nck=narrow neck)");

            int i0 = 0;
            int worstUnneededIdx = -1;
            double worstUnneededBow = 0;
            // Track the most off-centre forced bend: a wide band where the path hugs one side
            // (large ctrOff) is a "raise the floor" lever; a symmetric narrow neck is not tunable.
            int worstHugIdx = -1;
            double worstHugOff = 0;
            double worstHugBandW = 0;
            double worstHugNearSide = 0;
            while (i0 < rawPath.Length - 1)
            {
                int i1 = i0;
                while (i1 < rawPath.Length - 1 && sta[i1] - sta[i0] < WindowM) i1++;
                if (i1 <= i0) i1 = i0 + 1;

                Pt2 p0 = rawPath[i0], p1 = rawPath[i1];
                double chordLen = p0.DistanceTo(p1);
                double pathLen = sta[i1] - sta[i0];

                // Sample the straight chord across this window.
                bool chordOnMask = true;
                double chordMinClr = double.MaxValue;
                int steps = Math.Max(2, (int)(chordLen / cellSize));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    double wx = p0.X + t * (p1.X - p0.X);
                    double wy = p0.Y + t * (p1.Y - p0.Y);
                    double clr = ClearanceAt(wx, wy, walkable, dtb, env, cellSize, nx, ny, out bool onMask);
                    if (!onMask) { chordOnMask = false; chordMinClr = 0; break; }
                    if (clr < chordMinClr) chordMinClr = clr;
                }
                if (chordMinClr == double.MaxValue) chordMinClr = 0;

                // Clearance + bow along the ACTUAL path in this window.
                double pathMinClr = double.MaxValue;
                double bowMax = 0;
                int minClrK = i0;
                for (int k = i0; k <= i1; k++)
                {
                    double clr = ClearanceAt(rawPath[k].X, rawPath[k].Y, walkable, dtb, env, cellSize, nx, ny, out _);
                    if (clr < pathMinClr) { pathMinClr = clr; minClrK = k; }
                    double dev = PerpDistance(rawPath[k], p0, p1);
                    if (dev > bowMax) bowMax = dev;
                }
                if (pathMinClr == double.MaxValue) pathMinClr = 0;

                // At the tightest spot, march perpendicular to the local path heading to BOTH mask
                // edges. bandW = full local width; ctrOff = how far the path sits off the band centre.
                // CAUTION: a far ray that runs hundreds of metres before hitting an edge is NOT a band
                // wall — it has escaped into an open LOBE (a wide bulge in the surface). Skirting one
                // side of an open lobe is CORRECT (you never want the road swinging into a lobe centre),
                // so such a window is NOT a tunable hug. Only a moderate band with both edges close
                // (a genuine corridor the path sits off-centre in) is the raise-the-floor lever.
                var (dLeft, dRight) = BandCrossSection(rawPath, minClrK, walkable, dtb, env, cellSize, nx, ny);
                double bandW = dLeft + dRight;
                double ctrOff = Math.Abs(dLeft - dRight) / 2.0;       // distance from band centre
                double nearSide = Math.Min(dLeft, dRight);            // clearance toward the closer edge
                double farSide = Math.Max(dLeft, dRight);
                double openThresh = 10.0 * minClearanceM;             // far edge beyond this = open lobe, not a wall
                bool openSide = farSide >= openThresh;
                // kind: opn = far side opens into a lobe (skirting is correct); bnd = genuine corridor;
                //       nck = narrow neck (both edges close, little room either way).
                string kind = bandW < 1e-6 ? "?"
                            : openSide ? "opn"
                            : (bandW < 6.0 * minClearanceM ? "nck" : "bnd");

                double extraPct = chordLen > 1e-6 ? (pathLen - chordLen) / chordLen * 100.0 : 0;
                double clrGain = pathMinClr - chordMinClr;

                // Verdict: was the bow needed?
                bool chordViable = chordOnMask && chordMinClr >= minClearanceM;
                string verdict;
                if (bowMax < cellSize * 1.5) verdict = "straight";
                else if (!chordOnMask) verdict = "BOW-FORCED(chord off-mask)";
                else if (chordMinClr < minClearanceM) verdict = $"BOW-FORCED(chord clr {chordMinClr:F0}<req)";
                else
                {
                    verdict = $"BOW-UNNEEDED(+{clrGain:F0}m clr for +{extraPct:F0}% len)";
                    if (bowMax > worstUnneededBow) { worstUnneededBow = bowMax; worstUnneededIdx = i0; }
                }

                // A genuine tunable hug: a MODERATE band (both edges are real walls, not an open lobe)
                // whose tightest spot sits well off-centre, with the near edge tighter than comfort.
                // Open-lobe skirts and symmetric necks are deliberately excluded — neither is tunable.
                bool tunableHug = !openSide
                                  && nearSide < 4.0 * minClearanceM
                                  && farSide > nearSide * 2.5
                                  && bandW < openThresh;
                if (tunableHug && ctrOff > worstHugOff)
                {
                    worstHugOff = ctrOff; worstHugIdx = minClrK;
                    worstHugBandW = bandW; worstHugNearSide = nearSide;
                }

                lines.Add($"[routed-diag] {sta[i0],7:F0}..{sta[i1]:F0} | " +
                          $"{chordLen,7:F0} {pathLen,7:F0} {extraPct,5:F0}% bow={bowMax,5:F0} | " +
                          $"{(chordOnMask ? "ON " : "OFF"),3} {chordMinClr,5:F0} | " +
                          $"{pathMinClr,5:F0} {clrGain,6:F0} | {bandW,5:F0} {ctrOff,6:F0} {kind,3} | {verdict}");

                i0 = i1;
            }

            if (worstUnneededIdx >= 0)
                lines.Add($"[routed-diag] >>> WORST UNNEEDED BOW: {worstUnneededBow:F0} m off the straight line " +
                          $"near station {sta[worstUnneededIdx]:F0} m at ({rawPath[worstUnneededIdx].X:F1},{rawPath[worstUnneededIdx].Y:F1}). " +
                          $"A straight line there stays on-surface with adequate clearance — the bow is a medial-axis dive.");
            else
                lines.Add("[routed-diag] >>> No unneeded bows detected: every bow is forced by the band shape/clearance.");

            // Centred-vs-hugging verdict on the forced bends. The only tunable case is a MODERATE
            // corridor the path sits off-centre in; open-lobe skirts and narrow necks are not tunable.
            if (worstHugIdx >= 0)
            {
                // Re-centring target: move toward the corridor centre, but never demand more than the
                // corridor can give (half its width). Clamped so it cannot blow up into a lobe.
                double target = Math.Min(worstHugBandW / 2.0, worstHugNearSide + worstHugOff);
                lines.Add($"[routed-diag] >>> OFF-CENTRE CORRIDOR: at ({rawPath[worstHugIdx].X:F1},{rawPath[worstHugIdx].Y:F1}) " +
                          $"a {worstHugBandW:F0} m corridor (both edges are real walls) holds the path only {worstHugNearSide:F0} m " +
                          $"from the near edge ({worstHugOff:F0} m off centre). This IS a tunable hug — " +
                          $"raise elastic_floor_m toward ~{Math.Ceiling(target / 5.0) * 5.0:F0} m to re-centre.");
            }
            else
                lines.Add("[routed-diag] >>> No tunable hug: every turn is either centred in a narrow neck (nck) " +
                          "or correctly skirting one side of an open lobe (opn). Pushing the path off these (a higher " +
                          "elastic_floor_m) would force it INTO lobe centres — the old diving regression. Leave the floor as is.");

            lines.Add($"[routed-diag] === route total {total:F0} m, {rawPath.Length} raw pts ===");
            return lines;
        }

        // Clearance (m) at a world point = dtb(cell) * cellSize; 0 and onMask=false when off the mask.
        private static double ClearanceAt(double wx, double wy, bool[,] walkable, int[,] dtb,
                                          Envelope env, double cellSize, int nx, int ny, out bool onMask)
        {
            int cx = (int)Math.Floor((wx - env.MinX) / cellSize);
            int cy = (int)Math.Floor((wy - env.MinY) / cellSize);
            if (cx < 0 || cy < 0 || cx >= nx || cy >= ny || !walkable[cx, cy]) { onMask = false; return 0; }
            onMask = true;
            return dtb[cx, cy] * cellSize;
        }

        private static double PerpDistance(Pt2 p, Pt2 a, Pt2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return p.DistanceTo(a);
            return Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / len;
        }

        // Local band cross-section at path index k: march perpendicular to the path heading to BOTH
        // mask edges and return the (left, right) distances in metres. "Left"/"right" are the two
        // perpendicular half-rays; which is which is arbitrary but consistent within a window, so
        // their difference is a faithful measure of how off-centre the path sits.
        private static (double left, double right) BandCrossSection(
            Pt2[] path, int k, bool[,] walkable, int[,] dtb, Envelope env,
            double cellSize, int nx, int ny)
        {
            // Local heading from neighbouring PIs (central difference where possible).
            int ka = Math.Max(0, k - 1), kb = Math.Min(path.Length - 1, k + 1);
            double hx = path[kb].X - path[ka].X, hy = path[kb].Y - path[ka].Y;
            double hlen = Math.Sqrt(hx * hx + hy * hy);
            if (hlen < 1e-9) return (0, 0);
            // Perpendicular unit vector.
            double px = -hy / hlen, py = hx / hlen;

            // Cap the march so a near-open mask edge cannot run away (≈ 1 km either side).
            double step = cellSize * 0.5;
            int maxSteps = (int)Math.Ceiling(1000.0 / step);

            double March(double sx, double sy)
            {
                double d = 0;
                for (int s = 1; s <= maxSteps; s++)
                {
                    double wx = path[k].X + sx * (s * step);
                    double wy = path[k].Y + sy * (s * step);
                    int cx = (int)Math.Floor((wx - env.MinX) / cellSize);
                    int cy = (int)Math.Floor((wy - env.MinY) / cellSize);
                    if (cx < 0 || cy < 0 || cx >= nx || cy >= ny || !walkable[cx, cy]) break;
                    d = s * step;
                }
                return d;
            }

            return (March(px, py), March(-px, -py));
        }
    }
}
