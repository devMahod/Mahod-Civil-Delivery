using System;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Tests for GridRouter's "centered" (medial-axis / widest-path) routing mode.
    ///
    /// The legacy cost minimises LENGTH with a saturating (1 + alpha/dtb) nudge, so on a
    /// wide band the optimum hugs a border (the boundary nudge cannot overcome the length
    /// term — empirically, doubling boundary_bias produced an identical route). The centered
    /// mode makes distance-to-boundary the OBJECTIVE (non-saturating cost that grows with the
    /// clearance deficit), so the path rides the band's central spine by construction.
    ///
    /// Setup: a wide rectangular walkable band with a 1-cell non-walkable border ring, with
    /// A and B both pinned near the BOTTOM edge. The pure-shortest route runs straight along
    /// the bottom (low clearance); the centered route must bow up toward the spine.
    /// </summary>
    public class CenteredRoutingTests
    {
        private const double Cell = 1.0;
        private const int Nx = 60;
        private const int Ny = 40;

        private static (bool[,] walkable, Envelope env) BuildWideBand()
        {
            var walkable = new bool[Nx, Ny];
            // Interior is walkable; a 1-cell ring stays non-walkable so dtb is meaningful.
            for (int x = 1; x < Nx - 1; x++)
                for (int y = 1; y < Ny - 1; y++)
                    walkable[x, y] = true;
            var env = new Envelope(0, Nx * Cell, 0, Ny * Cell);
            return (walkable, env);
        }

        // Mean and max distance-to-boundary (in cells) sampled along the routed polyline.
        private static (double mean, int max) DtbAlong(Pt2[] path, int[,] dtb, Envelope env, double cell)
        {
            int nx = dtb.GetLength(0), ny = dtb.GetLength(1);
            double sum = 0; int count = 0; int max = 0;
            for (int i = 0; i < path.Length - 1; i++)
            {
                double segLen = path[i].DistanceTo(path[i + 1]);
                int steps = Math.Max(1, (int)(segLen / cell));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    double wx = path[i].X + t * (path[i + 1].X - path[i].X);
                    double wy = path[i].Y + t * (path[i + 1].Y - path[i].Y);
                    int cx = Math.Clamp((int)Math.Floor((wx - env.MinX) / cell), 0, nx - 1);
                    int cy = Math.Clamp((int)Math.Floor((wy - env.MinY) / cell), 0, ny - 1);
                    int d = dtb[cx, cy];
                    sum += d; count++;
                    if (d > max) max = d;
                }
            }
            return (count > 0 ? sum / count : 0.0, max);
        }

        [Fact]
        public void Centered_RidesTheSpine_WhileShortest_HugsTheBorder()
        {
            var (walkable, env) = BuildWideBand();
            var a = new Pt2(3.5, 3.5);    // near the bottom-left
            var b = new Pt2(56.5, 3.5);   // near the bottom-right

            // Pure shortest (boundary_bias 0): straight run along the bottom edge.
            var shortest = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                            boundaryBias: 0.0);
            // Centered: distance-to-boundary is the objective.
            var centered = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                            centered: true, centredness: 6.0);

            shortest.Success.Should().BeTrue();
            centered.Success.Should().BeTrue();
            shortest.DistanceToBoundary.Should().NotBeNull();
            centered.DistanceToBoundary.Should().NotBeNull();

            var (shortMean, shortMax) = DtbAlong(shortest.Path, shortest.DistanceToBoundary!, env, Cell);
            var (centMean, centMax) = DtbAlong(centered.Path, centered.DistanceToBoundary!, env, Cell);

            // The centered route reaches the spine (much higher peak clearance) ...
            centMax.Should().BeGreaterThan(shortMax);
            // ... and is more central on average.
            centMean.Should().BeGreaterThan(shortMean);
            // The shortest route really does hug the bottom edge (peak clearance stays small).
            shortMax.Should().BeLessThan(Ny / 4);
        }

        [Fact]
        public void Centered_BowsOut_SoItIsLongerThanShortest()
        {
            var (walkable, env) = BuildWideBand();
            var a = new Pt2(3.5, 3.5);
            var b = new Pt2(56.5, 3.5);

            var shortest = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                            boundaryBias: 0.0);
            var centered = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                            centered: true, centredness: 6.0);

            double LenOf(Pt2[] p)
            {
                double L = 0;
                for (int i = 0; i < p.Length - 1; i++) L += p[i].DistanceTo(p[i + 1]);
                return L;
            }

            // Riding up to the spine and back costs extra length vs. the straight bottom run —
            // length is the tiebreaker in centered mode, never the driver.
            LenOf(centered.Path).Should().BeGreaterThan(LenOf(shortest.Path));
        }

        [Fact]
        public void Centered_TargetClearanceCap_StopsDivingIntoAMidSpanLobe()
        {
            // A thin base band with a big WIDE room opening off it in the middle. Distance-to-
            // boundary is far higher inside the room, so pure medial centering dives into the
            // room; the target-clearance cap makes the (adequately-clear) base band "good enough",
            // so the route stays straight along it. (A narrow lobe has LOW clearance and would be
            // ignored anyway — the dive only happens toward genuinely WIDER ground.)
            const int gnx = 90, gny = 70;
            var walkable = new bool[gnx, gny];
            for (int x = 1; x < gnx - 1; x++)          // thin base band: rows 29..40 (~12 tall)
                for (int y = 29; y <= 40; y++)
                    walkable[x, y] = true;
            for (int x = 35; x <= 74; x++)             // wide room (40x39) opening upward mid-span
                for (int y = 29; y <= 67; y++)
                    walkable[x, y] = true;
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);

            var a = new Pt2(2.5, 34.5);
            var b = new Pt2(87.5, 34.5);

            var uncapped = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                            centered: true, centredness: 6.0);                  // targetClearanceM=0 → pure medial
            var capped = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                          centered: true, centredness: 6.0, targetClearanceM: 4.0);

            uncapped.Success.Should().BeTrue();
            capped.Success.Should().BeTrue();

            var (_, uncMax) = DtbAlong(uncapped.Path, uncapped.DistanceToBoundary!, env, Cell);
            var (_, capMax) = DtbAlong(capped.Path, capped.DistanceToBoundary!, env, Cell);

            double Len(Pt2[] p) { double L = 0; for (int i = 0; i < p.Length - 1; i++) L += p[i].DistanceTo(p[i + 1]); return L; }

            // Pure medial dives into the lobe (reaches higher clearance) and is longer;
            // the cap keeps the route in the base band → lower peak clearance, shorter path.
            capMax.Should().BeLessThan(uncMax);
            Len(capped.Path).Should().BeLessThan(Len(uncapped.Path));
        }

        [Fact]
        public void ElasticSmooth_StraightensTheDiveIntoAWideRoom_StaysOnMaskAndAtEndpoints()
        {
            // Thin base band + wide room opening off it mid-span (same shape as the cap test).
            // Pure-medial centered routing dives up into the room; the elastic smoother straightens
            // that dive back to the base band (smaller bow, no extra PIs) while staying on the mask.
            const int gnx = 90, gny = 70;
            var w = new bool[gnx, gny];
            for (int x = 1; x < gnx - 1; x++)
                for (int y = 29; y <= 40; y++) w[x, y] = true;       // base band
            for (int x = 35; x <= 74; x++)
                for (int y = 29; y <= 67; y++) w[x, y] = true;       // wide room
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);
            var a = new Pt2(2.5, 34.5);
            var b = new Pt2(87.5, 34.5);

            var raw = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, elasticSmooth: false, minClearanceM: 1.0);
            var smooth = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, elasticFloorM: 4.0, elasticSmooth: true, minClearanceM: 1.0);

            raw.Success.Should().BeTrue();
            smooth.Success.Should().BeTrue();

            double MaxBow(Pt2[] p)
            {
                double m = 0, dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                foreach (var q in p)
                {
                    double d = len < 1e-9 ? q.DistanceTo(a) : Math.Abs((q.X - a.X) * dy - (q.Y - a.Y) * dx) / len;
                    if (d > m) m = d;
                }
                return m;
            }

            // The raw centered path dives up into the room; the smoothed one stays much straighter.
            MaxBow(smooth.Path).Should().BeLessThan(MaxBow(raw.Path));
            // Smoothing never adds PIs.
            smooth.Path.Length.Should().BeLessThanOrEqualTo(raw.Path.Length);
            // Endpoints preserved and every PI on the mask.
            smooth.Path[0].DistanceTo(a).Should().BeLessThan(Cell * 2);
            smooth.Path[^1].DistanceTo(b).Should().BeLessThan(Cell * 2);
            int nx = w.GetLength(0), ny = w.GetLength(1);
            foreach (var q in smooth.Path)
            {
                int cx = Math.Clamp((int)Math.Floor((q.X - env.MinX) / Cell), 0, nx - 1);
                int cy = Math.Clamp((int)Math.Floor((q.Y - env.MinY) / Cell), 0, ny - 1);
                w[cx, cy].Should().BeTrue("every smoothed PI must land on a walkable cell");
            }
        }

        [Fact]
        public void WidthWindow_CentersThroughACurve_MoreThanLegacyGlobalNorm()
        {
            // L-shaped WIDE band: a horizontal arm meeting a vertical arm at a corner. The medial
            // ridge turns the corner; cutting the INSIDE of the corner is shorter but de-centers.
            // With a single GLOBAL centerMax, the centering gradient is diluted (normalised by the
            // global max), so the length term wins and the path cuts the inside corner (hugs). The
            // per-cell width-normalised field (widthWindowK>0) restores the FULL local gradient, so
            // the path rides the ridge around the corner — measurably more centered.
            const int gnx = 70, gny = 92;
            var w = new bool[gnx, gny];
            for (int x = 1; x <= 60; x++) for (int y = 1; y <= 30; y++) w[x, y] = true;   // horizontal arm
            for (int x = 31; x <= 60; x++) for (int y = 1; y <= 86; y++) w[x, y] = true;  // vertical arm
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);
            var a = new Pt2(5.5, 15.5);    // left end of the horizontal arm
            var b = new Pt2(45.5, 81.5);   // top of the vertical arm

            var legacy = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, elasticFloorM: 25.0, elasticSmooth: true,
                minClearanceM: 1.0, widthWindowK: 0.0);
            var windowed = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, elasticFloorM: 25.0, elasticSmooth: true,
                minClearanceM: 1.0, widthWindowK: 0.5);

            legacy.Success.Should().BeTrue();
            windowed.Success.Should().BeTrue();

            var (legMean, _) = DtbAlong(legacy.Path, legacy.DistanceToBoundary!, env, Cell);
            var (winMean, _) = DtbAlong(windowed.Path, windowed.DistanceToBoundary!, env, Cell);

            // Width-normalised centering rides the ridge through the corner instead of cutting the
            // inside — strictly more central on average through the whole (mostly-curved) route.
            winMean.Should().BeGreaterThan(legMean);
        }

        [Fact]
        public void WidthWindow_DoesNotDiveIntoADeadEndLobe()
        {
            // A straight through-channel with a big dead-end ROOM opening off one side mid-span.
            // The per-cell normaliser must NOT pull the spine up into the room (the forbidden dive):
            // the box-max window scales with LOCAL clearance, so a through-channel cell's window is
            // too small to "see" the room interior, and the goal-directed search skirts the mouth.
            const int gnx = 120, gny = 80;
            var w = new bool[gnx, gny];
            for (int x = 1; x <= 118; x++) for (int y = 10; y <= 26; y++) w[x, y] = true;  // through-channel
            for (int x = 50; x <= 80; x++) for (int y = 10; y <= 74; y++) w[x, y] = true;  // dead-end room (tall)
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);
            var a = new Pt2(2.5, 18.5);
            var b = new Pt2(117.5, 18.5);

            var windowed = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, elasticFloorM: 25.0, elasticSmooth: true,
                minClearanceM: 1.0, widthWindowK: 0.5);
            windowed.Success.Should().BeTrue();

            // Over the room's x-span the path must stay in the through-channel (y well below the
            // room ceiling), i.e. it skirts the mouth rather than diving up into the dead end.
            foreach (var p in windowed.Path)
                if (p.X >= 52 && p.X <= 78)
                    p.Y.Should().BeLessThan(34, "the spine must not climb into the dead-end room");
        }

        [Fact]
        public void CenterBalance_LiftsAHuggingPathToTheBandCentre()
        {
            // Wide band, A and B pinned near the BOTTOM edge. Ribbon balancing must march
            // perpendicular (vertically) to both edges and lift the mid-path to the band centre —
            // equal room above and below — even though the cost/endpoints start it near the bottom.
            var (walkable, env) = BuildWideBand();   // 60 x 40, interior y in [1,38]
            var a = new Pt2(3.5, 3.5);
            var b = new Pt2(56.5, 3.5);

            var balanced = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, centerBalance: true, minClearanceM: 1.0);
            balanced.Success.Should().BeTrue();

            var (mean, max) = DtbAlong(balanced.Path, balanced.DistanceToBoundary!, env, Cell);
            // Centre ridge of the interior band sits ~18-19 cells off either edge; the balanced
            // ribbon's peak must reach close to it (a bottom-hugging path peaks at ~3).
            max.Should().BeGreaterThan(Ny / 2 - 5);     // > 15: reaches the vertical centre
            mean.Should().BeGreaterThan(Ny / 4.0);      // > 10: not hugging on average

            // No spaghetti: a clean centered ribbon is barely longer than the straight A→B run.
            double Len(Pt2[] p) { double L = 0; for (int i = 0; i < p.Length - 1; i++) L += p[i].DistanceTo(p[i + 1]); return L; }
            Len(balanced.Path).Should().BeLessThan(1.5 * a.DistanceTo(b));

            int nx = walkable.GetLength(0), ny = walkable.GetLength(1);
            foreach (var p in balanced.Path)
            {
                int cx = Math.Clamp((int)Math.Floor((p.X - env.MinX) / Cell), 0, nx - 1);
                int cy = Math.Clamp((int)Math.Floor((p.Y - env.MinY) / Cell), 0, ny - 1);
                walkable[cx, cy].Should().BeTrue("every balanced PI must land on a walkable cell");
            }
        }

        [Fact]
        public void CenterBalance_CurvedBand_CentersWithoutSpaghetti()
        {
            // L-band with a real bend — the geometry that made the raw ribbon pass oscillate into
            // cell-scale loops (route length blew up ~2.4x on the live surface). The resample +
            // Taubin-smoothed ribbon must center the bend AND stay a clean, bounded-length polyline.
            const int gnx = 70, gny = 92;
            var w = new bool[gnx, gny];
            for (int x = 1; x <= 60; x++) for (int y = 1; y <= 30; y++) w[x, y] = true;   // horizontal arm
            for (int x = 31; x <= 60; x++) for (int y = 1; y <= 86; y++) w[x, y] = true;  // vertical arm
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);
            var a = new Pt2(5.5, 15.5);
            var b = new Pt2(45.5, 81.5);

            var balanced = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, centerBalance: true, minClearanceM: 1.0);
            balanced.Success.Should().BeTrue();

            double Len(Pt2[] p) { double L = 0; for (int i = 0; i < p.Length - 1; i++) L += p[i].DistanceTo(p[i + 1]); return L; }
            // A clean centered L is ~110-140; the spaghetti bug ran 2-3x the ~106 corridor minimum.
            Len(balanced.Path).Should().BeLessThan(190);

            int nx = w.GetLength(0), ny = w.GetLength(1);
            foreach (var p in balanced.Path)
            {
                int cx = Math.Clamp((int)Math.Floor((p.X - env.MinX) / Cell), 0, nx - 1);
                int cy = Math.Clamp((int)Math.Floor((p.Y - env.MinY) / Cell), 0, ny - 1);
                w[cx, cy].Should().BeTrue("every balanced PI must land on a walkable cell");
            }
        }

        [Fact]
        public void CenterBalance_WithDesignRadius_ProducesSmoothPath_NoCusps()
        {
            // L-band with a real 90° bend. With a design radius the curvature cap must flatten the
            // turn into a gentle arc — the final path must contain NO cusp/reversal (the cause of the
            // "radius→0" warnings) while still centering and staying on the surface.
            const int gnx = 70, gny = 110;
            var w = new bool[gnx, gny];
            for (int x = 1; x <= 60; x++) for (int y = 1; y <= 30; y++) w[x, y] = true;
            for (int x = 31; x <= 60; x++) for (int y = 1; y <= 104; y++) w[x, y] = true;
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);
            var a = new Pt2(5.5, 15.5);
            var b = new Pt2(45.5, 99.5);

            var route = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, centerBalance: true, minClearanceM: 1.0,
                designRadiusM: 30.0, centerDeadbandFrac: 0.25);
            route.Success.Should().BeTrue();

            var p = route.Path;
            double worstTurn = 0;
            for (int i = 1; i < p.Length - 1; i++)
            {
                double v1x = p[i].X - p[i - 1].X, v1y = p[i].Y - p[i - 1].Y;
                double v2x = p[i + 1].X - p[i].X, v2y = p[i + 1].Y - p[i].Y;
                double l1 = Math.Sqrt(v1x * v1x + v1y * v1y), l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                if (l1 < 1e-9 || l2 < 1e-9) continue;
                double dot = Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1.0, 1.0);
                worstTurn = Math.Max(worstTurn, Math.Acos(dot));
            }
            // No cusp/reversal anywhere (a cusp approaches π). NOTE: since the tangent-arc fit
            // landed (2026-07-27) the returned path is an ALIGNMENT PI list, not a rounded
            // polyline — a real 90° corridor bend is now expressed as ONE ~90° PI that
            // CurveAttacher fillets, which is the intended engineer-style geometry. So the
            // invariant is "no reversal", not "no right angle".
            worstTurn.Should().BeLessThan(2.6, "a reversal/cusp (→π) is the actual defect");

            // The fit must also be BUILDABLE: few PIs, and each one's curve fits its tangents
            // (T = R·tan(Δ/2)); otherwise CurveAttacher would relax the radius — the old wiggle.
            if (route.PathSource == "tangent_arc_fit")
            {
                p.Length.Should().BeLessThanOrEqualTo(4, "an L-band has one real direction change");
                for (int i = 1; i < p.Length - 1; i++)
                {
                    double v1x = p[i].X - p[i - 1].X, v1y = p[i].Y - p[i - 1].Y;
                    double v2x = p[i + 1].X - p[i].X, v2y = p[i + 1].Y - p[i].Y;
                    double l1 = Math.Sqrt(v1x * v1x + v1y * v1y), l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                    double defl = Math.Acos(Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1.0, 1.0));
                    double t = 30.0 * Math.Tan(defl / 2);
                    l1.Should().BeGreaterThanOrEqualTo(t - 1e-6);
                    l2.Should().BeGreaterThanOrEqualTo(t - 1e-6);
                }
            }

            int nx = w.GetLength(0), ny = w.GetLength(1);
            foreach (var q in route.Path)
            {
                int cx = Math.Clamp((int)Math.Floor((q.X - env.MinX) / Cell), 0, nx - 1);
                int cy = Math.Clamp((int)Math.Floor((q.Y - env.MinY) / Cell), 0, ny - 1);
                w[cx, cy].Should().BeTrue("every PI must land on a walkable cell");
            }
        }

        [Fact]
        public void CenterBalance_TightElbow_NoCuspEvenWhenComfortClearanceWouldVetoTheFlatten()
        {
            // A narrow L-band whose half-width barely exceeds the comfort clearance. At the inside of
            // the 90° elbow, flattening the turn pushes the apex toward the inside corner — below the
            // comfort clearance — so the STRICT (full-clearance) gate vetoes it and, without the relaxed
            // fallback, the sharp vertex (a radius→0 cusp) survives. The cap must escalate to the
            // mask-only gate there and flatten the turn anyway: no cusp, still on the surface.
            const int gnx = 60, gny = 90;
            var w = new bool[gnx, gny];
            for (int x = 1; x <= 48; x++) for (int y = 1; y <= 12; y++) w[x, y] = true;    // horizontal arm (12 wide)
            for (int x = 37; x <= 48; x++) for (int y = 1; y <= 84; y++) w[x, y] = true;   // vertical arm (12 wide)
            var env = new Envelope(0, gnx * Cell, 0, gny * Cell);
            var a = new Pt2(4.5, 6.5);     // mid-height of the horizontal arm
            var b = new Pt2(42.5, 79.5);   // mid-width of the vertical arm

            // minClearanceM=5 → half-width (6) barely clears it; designRadius=30 forces the cap to
            // flatten the elbow into the inside corner where clearance drops below 5.
            var route = GridRouter.Route(w, env, Cell, a, b, CancellationToken.None,
                centered: true, centredness: 6.0, centerBalance: true, minClearanceM: 5.0,
                designRadiusM: 30.0, centerDeadbandFrac: 0.0);
            route.Success.Should().BeTrue();

            var p = route.Path;
            double worstTurn = 0;
            for (int i = 1; i < p.Length - 1; i++)
            {
                double v1x = p[i].X - p[i - 1].X, v1y = p[i].Y - p[i - 1].Y;
                double v2x = p[i + 1].X - p[i].X, v2y = p[i + 1].Y - p[i].Y;
                double l1 = Math.Sqrt(v1x * v1x + v1y * v1y), l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                if (l1 < 1e-9 || l2 < 1e-9) continue;
                double dot = Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1.0, 1.0);
                worstTurn = Math.Max(worstTurn, Math.Acos(dot));
            }
            // No reversal/cusp anywhere — a cusp (the radius→0 cause) approaches π. The flattened
            // elbow keeps every PI turn comfortably below a right angle.
            worstTurn.Should().BeLessThan(Math.PI / 2);

            // Still strictly inside the surface (relaxed gate keeps it on the mask, just closer to the
            // edge at the corner — never off it).
            int nx = w.GetLength(0), ny = w.GetLength(1);
            foreach (var q in route.Path)
            {
                int cx = Math.Clamp((int)Math.Floor((q.X - env.MinX) / Cell), 0, nx - 1);
                int cy = Math.Clamp((int)Math.Floor((q.Y - env.MinY) / Cell), 0, ny - 1);
                w[cx, cy].Should().BeTrue("every PI must land on a walkable cell");
            }
        }

        [Fact]
        public void Centered_StaysInsideTheWalkableMask()
        {
            var (walkable, env) = BuildWideBand();
            var a = new Pt2(3.5, 3.5);
            var b = new Pt2(56.5, 3.5);

            var centered = GridRouter.Route(walkable, env, Cell, a, b, CancellationToken.None,
                                            centered: true, centredness: 6.0);
            centered.Success.Should().BeTrue();

            int nx = walkable.GetLength(0), ny = walkable.GetLength(1);
            foreach (var p in centered.Path)
            {
                int cx = Math.Clamp((int)Math.Floor((p.X - env.MinX) / Cell), 0, nx - 1);
                int cy = Math.Clamp((int)Math.Floor((p.Y - env.MinY) / Cell), 0, ny - 1);
                walkable[cx, cy].Should().BeTrue("every routed PI must land on a walkable cell");
            }
        }
    }
}
