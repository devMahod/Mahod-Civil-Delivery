using System.Linq;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    public class GridRouterTests
    {
        private static readonly GeometryFactory _factory =
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();

        // Helper: rasterise a polygon into a walkable mask + envelope for the grid router.
        // Mirrors what BuildableRegion.Build does, but for synthetic test polygons (no TIN).
        private static (bool[,] walkable, Envelope env, int nx, int ny) Rasterise(Polygon poly, double cellSize)
        {
            var env = poly.EnvelopeInternal;
            int nx = System.Math.Max(1, (int)System.Math.Ceiling(env.Width / cellSize));
            int ny = System.Math.Max(1, (int)System.Math.Ceiling(env.Height / cellSize));
            var mask = new bool[nx, ny];
            var prepared = PreparedGeometryFactory.Prepare(poly);
            for (int gx = 0; gx < nx; gx++)
            {
                double wx = env.MinX + (gx + 0.5) * cellSize;
                for (int gy = 0; gy < ny; gy++)
                {
                    double wy = env.MinY + (gy + 0.5) * cellSize;
                    if (prepared.Contains(_factory.CreatePoint(new Coordinate(wx, wy))))
                        mask[gx, gy] = true;
                }
            }
            return (mask, env, nx, ny);
        }

        // L-shaped corridor — outer ring CCW.
        private static Polygon LShapedPolygon()
        {
            var coords = new[]
            {
                new Coordinate(0,   0),
                new Coordinate(40,  0),
                new Coordinate(40,  10),
                new Coordinate(120, 10),
                new Coordinate(120, 30),
                new Coordinate(40,  30),
                new Coordinate(40,  40),
                new Coordinate(0,   40),
                new Coordinate(0,   0),
            };
            return _factory.CreatePolygon(coords);
        }

        [Fact]
        public void Route_LShapedCorridor_FindsBendPath()
        {
            var poly = LShapedPolygon();
            const double cellSize = 2.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(20, 35);
            var b = new Pt2(110, 20);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path.Length.Should().BeGreaterOrEqualTo(3,
                "an L-shaped corridor cannot be traversed by a single straight tangent");
            result.Path[0].X.Should().BeApproximately(a.X, 1e-6);
            result.Path[0].Y.Should().BeApproximately(a.Y, 1e-6);
            result.Path[^1].X.Should().BeApproximately(b.X, 1e-6);
            result.Path[^1].Y.Should().BeApproximately(b.Y, 1e-6);

            foreach (var p in result.Path)
            {
                var pt = _factory.CreatePoint(new Coordinate(p.X, p.Y));
                poly.Buffer(0.5).Contains(pt).Should().BeTrue(
                    $"PI ({p.X}, {p.Y}) escaped the L-shaped corridor");
            }
        }

        [Fact]
        public void Route_PolygonWithHole_RoutesAroundHole()
        {
            var shell = _factory.CreateLinearRing(new[]
            {
                new Coordinate(0,   0),
                new Coordinate(100, 0),
                new Coordinate(100, 100),
                new Coordinate(0,   100),
                new Coordinate(0,   0),
            });
            var hole = _factory.CreateLinearRing(new[]
            {
                new Coordinate(20, 20),
                new Coordinate(80, 20),
                new Coordinate(80, 80),
                new Coordinate(20, 80),
                new Coordinate(20, 20),
            });
            var poly = _factory.CreatePolygon(shell, new[] { hole });

            const double cellSize = 2.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(10, 50);
            var b = new Pt2(90, 50);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path.Length.Should().BeGreaterOrEqualTo(3,
                "path must bend around the hole rather than cut straight through");

            var holePoly = _factory.CreatePolygon(hole);
            foreach (var p in result.Path)
            {
                var pt = _factory.CreatePoint(new Coordinate(p.X, p.Y));
                holePoly.Contains(pt).Should().BeFalse(
                    $"PI ({p.X}, {p.Y}) is inside the forbidden hole");
            }
        }

        // ── Phase 1: centre-pull post-pass ──────────────────────────────────

        [Fact]
        public void Route_CenterPullPreservesEndpoints()
        {
            // The user's clicks (A and B) must never be shifted by centre-pull —
            // only interior PIs are eligible. This is the foundational invariant.
            var poly = LShapedPolygon();
            const double cellSize = 1.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(20, 35);
            var b = new Pt2(110, 20);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path[0].X.Should().Be(a.X);
            result.Path[0].Y.Should().Be(a.Y);
            result.Path[^1].X.Should().Be(b.X);
            result.Path[^1].Y.Should().Be(b.Y);
        }

        [Fact]
        public void Route_CenterPullKeepsAllSegmentsInsideMask()
        {
            // Even after PIs shift laterally, every consecutive line segment must
            // stay inside the walkable corridor — that's the LoS guard inside
            // CenterPullPis. Test it on the L-corridor where a sloppy shift could
            // easily push a segment across the inner corner.
            var poly = LShapedPolygon();
            const double cellSize = 1.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(20, 35);
            var b = new Pt2(110, 20);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);

            // Sample 20 points along every segment; each must remain inside the polygon.
            // Buffer(0.5) tolerates the cell-rasterisation half-cell margin like other tests.
            var inflated = poly.Buffer(0.5);
            for (int i = 0; i < result.Path.Length - 1; i++)
            {
                var p0 = result.Path[i];
                var p1 = result.Path[i + 1];
                for (int s = 0; s <= 20; s++)
                {
                    double t = s / 20.0;
                    var sample = new Coordinate(p0.X + t * (p1.X - p0.X), p0.Y + t * (p1.Y - p0.Y));
                    inflated.Contains(_factory.CreatePoint(sample)).Should().BeTrue(
                        $"segment {i}→{i + 1} sample at t={t:F2} ({sample.X:F1}, {sample.Y:F1}) escaped the corridor");
                }
            }
        }

        [Fact]
        public void Route_AsymmetricObstacle_CenterPullsBendPiIntoWiderHalf()
        {
            // 200×100 corridor with a wall jutting up from y=0 to y=50 between
            // x=80..120. A=(10,50)→B=(190,50) must bend over the wall. The
            // wider half of the corridor sits *above* the bend (y∈[50,100]),
            // and centre-pull should push interior PI(s) noticeably above the
            // y=55 boundary-bias minimum, toward the medial axis at y≈75.
            var shell = _factory.CreateLinearRing(new[]
            {
                new Coordinate(0,   0),
                new Coordinate(200, 0),
                new Coordinate(200, 100),
                new Coordinate(0,   100),
                new Coordinate(0,   0),
            });
            var hole = _factory.CreateLinearRing(new[]
            {
                new Coordinate(80,  0),
                new Coordinate(120, 0),
                new Coordinate(120, 50),
                new Coordinate(80,  50),
                new Coordinate(80,  0),
            });
            var poly = _factory.CreatePolygon(shell, new[] { hole });

            const double cellSize = 2.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(10, 50);
            var b = new Pt2(190, 50);

            // anyAngle=false to isolate centre-pull behaviour: octile A* will leave
            // bend PIs near the wall (low dtb), and centre-pull is then expected to
            // lift them into the wider upper half. (Theta* gets its own test below.)
            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: false);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path.Length.Should().BeGreaterOrEqualTo(3,
                "the octile route must bend over the asymmetric obstacle");

            // The bend's apex (max interior PI Y) must be deep in the upper half —
            // well above the wall top (y=50) and the boundary-bias band (~y=55).
            // (Auto-densify may add approach PIs at lower Y on the way in/out;
            // those are correct, so we only assert on the bend apex, not every
            // interior PI.)
            double maxInteriorY = double.NegativeInfinity;
            for (int i = 1; i < result.Path.Length - 1; i++)
                if (result.Path[i].Y > maxInteriorY) maxInteriorY = result.Path[i].Y;
            maxInteriorY.Should().BeGreaterThan(60,
                $"the bend apex should be centre-pulled into the wider upper half; got max interior Y={maxInteriorY:F1}");
        }

        // ── Phase 2: Theta* any-angle routing ───────────────────────────────

        [Fact]
        public void Route_OpenRectangle_AnyAngleReducesToTwoPoints()
        {
            // Open rectangle, A and B at diagonally opposite corners. With Theta*
            // the line of sight from A to B is clear, so the goal's parent is set
            // directly to A and the reconstructed path is just [A, B] — DP and
            // centre-pull then have no interior PIs to touch. Octile A* on the
            // same setup would produce a long staircase that DP retains as
            // multiple PIs. This is the "kills the staircase at the source" test.
            var poly = _factory.CreatePolygon(new[]
            {
                new Coordinate(0,   0),
                new Coordinate(100, 0),
                new Coordinate(100, 100),
                new Coordinate(0,   100),
                new Coordinate(0,   0),
            });

            const double cellSize = 2.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(10, 10);
            var b = new Pt2(90, 90);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path.Length.Should().Be(2,
                "Theta* should reduce a clear-line route to a single straight segment");
            result.Path[0].X.Should().Be(a.X);
            result.Path[0].Y.Should().Be(a.Y);
            result.Path[^1].X.Should().Be(b.X);
            result.Path[^1].Y.Should().Be(b.Y);
        }

        [Fact]
        public void Route_LCorridor_AnyAnglePathIsShorterThanOctile()
        {
            // Same L-corridor as the existing tests, run twice. Theta* paths must
            // be ≤ octile paths (within numerical tolerance) — the staircase only
            // adds length, never saves it.
            var poly = LShapedPolygon();
            const double cellSize = 1.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(20, 35);
            var b = new Pt2(110, 20);

            var theta = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: true);
            var octile = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: false);

            theta.Success.Should().BeTrue(theta.FailureReason);
            octile.Success.Should().BeTrue(octile.FailureReason);

            double thetaLen = SegmentLength(theta.Path);
            double octileLen = SegmentLength(octile.Path);

            thetaLen.Should().BeLessOrEqualTo(octileLen + 1e-6,
                $"Theta* path (len={thetaLen:F2}) should never be longer than octile (len={octileLen:F2})");
            theta.Path.Length.Should().BeLessOrEqualTo(octile.Path.Length,
                "Theta* should produce the same or fewer PIs than octile");
        }

        [Fact]
        public void Route_AnyAngleDisabled_StillProducesValidLPath()
        {
            // Regression guard: anyAngle:false must keep the legacy 8-direction
            // behaviour intact — same shape of result as the existing
            // Route_LShapedCorridor_FindsBendPath test.
            var poly = LShapedPolygon();
            const double cellSize = 2.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(20, 35);
            var b = new Pt2(110, 20);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: false);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path.Length.Should().BeGreaterOrEqualTo(3);
            result.Path[0].X.Should().Be(a.X);
            result.Path[^1].X.Should().Be(b.X);

            foreach (var p in result.Path)
            {
                var pt = _factory.CreatePoint(new Coordinate(p.X, p.Y));
                poly.Buffer(0.5).Contains(pt).Should().BeTrue(
                    $"PI ({p.X}, {p.Y}) escaped the L-shaped corridor");
            }
        }

        private static double SegmentLength(Pt2[] path)
        {
            double total = 0;
            for (int i = 0; i < path.Length - 1; i++)
                total += path[i].DistanceTo(path[i + 1]);
            return total;
        }

        // ── Phase 4: Road-affinity (slope-based) bias ───────────────────────

        // Build a costMul field directly: every cell has multiplier 1.0, except
        // cells inside the band [yMin, yMax] which get the discount. Lets tests
        // express "the existing road is here" without going through a slope field.
        private static double[,] BandCostField(int nx, int ny, double cellSize, Envelope env,
                                               double yMin, double yMax, double mul)
        {
            var costMul = new double[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                {
                    double cy = env.MinY + (y + 0.5) * cellSize;
                    costMul[x, y] = (cy >= yMin && cy <= yMax) ? mul : 1.0;
                }
            return costMul;
        }

        // Build a corridor with a wall (hole) blocking direct A→B at the midline.
        // Returns (mask, env, nx, ny). A and B are placed at y=20 by callers.
        private static (bool[,] mask, Envelope env, int nx, int ny) RasteriseWalledCorridor(double cellSize)
        {
            var shell = _factory.CreateLinearRing(new[]
            {
                new Coordinate(0,   0),
                new Coordinate(200, 0),
                new Coordinate(200, 100),
                new Coordinate(0,   100),
                new Coordinate(0,   0),
            });
            // Wall blocks direct chord at y=20: hole spans y∈[0,30] at x∈[80,120].
            // A→B at y=20 cannot be a straight LoS-clear line, so DP cannot
            // collapse the path to [A,B] — the bend PI(s) survive simplification.
            var hole = _factory.CreateLinearRing(new[]
            {
                new Coordinate(80,  0),
                new Coordinate(120, 0),
                new Coordinate(120, 30),
                new Coordinate(80,  30),
                new Coordinate(80,  0),
            });
            var poly = _factory.CreatePolygon(shell, new[] { hole });
            return Rasterise(poly, cellSize);
        }

        [Fact]
        public void Route_DiscountBandAroundWall_PathClimbsToBand()
        {
            // Corridor with a wall forcing a detour above y=30. Compare biased
            // (band at y∈[80,90]) vs. unbiased: the biased path's bend should
            // climb significantly higher — toward the band — than the unbiased
            // path's bend, which only clears the wall (~y=32-50 with the
            // boundary-bias).
            const double cellSize = 2.0;
            var (mask, env, nx, ny) = RasteriseWalledCorridor(cellSize);
            var a = new Pt2(10, 20);
            var b = new Pt2(190, 20);

            var costMul = BandCostField(nx, ny, cellSize, env, 80, 90, mul: 0.3);
            var biased = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: false, costMul: costMul);
            var unbiased = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: false, costMul: null);

            biased.Success.Should().BeTrue(biased.FailureReason);
            unbiased.Success.Should().BeTrue(unbiased.FailureReason);

            double maxYBiased = biased.Path.Max(p => p.Y);
            double maxYUnbiased = unbiased.Path.Max(p => p.Y);

            maxYBiased.Should().BeGreaterThan(maxYUnbiased + 10,
                $"biased path should climb higher than unbiased; biased peak Y={maxYBiased:F1}, unbiased={maxYUnbiased:F1}");
            maxYBiased.Should().BeGreaterOrEqualTo(70,
                $"biased path should reach into the discount band y∈[80,90]; got max Y={maxYBiased:F1}");
        }

        [Fact]
        public void Route_TwoBandsAroundWall_PicksCloserBand()
        {
            // Same walled corridor; two discount bands both above the wall —
            // closer band y∈[40,50] and farther band y∈[80,90]. Both bands
            // share the same discount, so A* picks the cheaper detour, which
            // is the closer one (shorter climb, less horizontal-via-band cost).
            const double cellSize = 2.0;
            var (mask, env, nx, ny) = RasteriseWalledCorridor(cellSize);
            var a = new Pt2(10, 20);
            var b = new Pt2(190, 20);

            var lower = BandCostField(nx, ny, cellSize, env, 40, 50, mul: 0.4);
            var upper = BandCostField(nx, ny, cellSize, env, 80, 90, mul: 0.4);
            var costMul = new double[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    costMul[x, y] = System.Math.Min(lower[x, y], upper[x, y]);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: false, costMul: costMul);

            result.Success.Should().BeTrue(result.FailureReason);
            double maxY = result.Path.Max(p => p.Y);
            // Path should clearly stay below the upper band's centre (~85). It
            // may briefly swing above the lower band to dodge the wall-edge
            // boundary-bias penalty (boundary bias spikes near the wall corners),
            // but it must NOT commit to the upper band path.
            maxY.Should().BeLessThan(80,
                $"path should not reach the upper band y∈[80,90]; got max Y={maxY:F1}");
            maxY.Should().BeGreaterOrEqualTo(35,
                $"path must clear the wall (y=30) and reach at least the lower band y∈[40,50]; got max Y={maxY:F1}");
            // Mean Y must reflect a lower-band-dominant path (≪ 65, the geometric
            // midpoint between the two bands), not a balanced or upper one.
            double meanY = result.Path.Average(p => p.Y);
            meanY.Should().BeLessThan(50,
                $"path should be closer to lower band y=45 on average; got mean Y={meanY:F1}");
        }

        [Fact]
        public void Route_NoCostMul_BehavesLikeLegacy()
        {
            // Regression guard: passing costMul=null must produce exactly the
            // same PI list as the legacy call (same overload arguments).
            var poly = LShapedPolygon();
            const double cellSize = 1.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(20, 35);
            var b = new Pt2(110, 20);

            var legacy = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None);
            var withNullCost = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                costMul: null);

            legacy.Success.Should().BeTrue();
            withNullCost.Success.Should().BeTrue();
            withNullCost.Path.Length.Should().Be(legacy.Path.Length);
            for (int i = 0; i < legacy.Path.Length; i++)
            {
                withNullCost.Path[i].X.Should().BeApproximately(legacy.Path[i].X, 1e-9);
                withNullCost.Path[i].Y.Should().BeApproximately(legacy.Path[i].Y, 1e-9);
            }
        }

        [Fact]
        public void Route_CurvedCorridorWithDiscountStrip_AutoDensifiesAlongCurve()
        {
            // A quarter-ring corridor (banana) wraps from A=(80,5) up around to
            // B=(5,80). Without a discount band, the corridor itself naturally
            // forces a curved A* path with multiple PIs. With an additional
            // discount band running along the corridor's medial axis, the raw
            // path hugs the band; DP collapses LoS-clear segments, but the
            // always-on deviation densifier should restore enough PIs that the
            // simplified alignment tracks the curve closely (no straight chord
            // cutting the inner corner).
            //
            // The corridor: walkable iff rIn ≤ sqrt(x²+y²) ≤ rOut and x≥0, y≥0.
            // For a 90° arc, the chord midpoint between A=(rOut, 0) and B=(0, rOut)
            // sits at radius rOut/√2. Pick rIn so that rOut/√2 < rIn — then the
            // chord exits the corridor and Theta* CANNOT reduce the path to [A,B].
            const double cellSize = 2.0;
            const double rIn = 60, rOut = 80;     // chord midpoint at 80/√2 ≈ 56.6 < rIn=60
            int nx = (int)System.Math.Ceiling(rOut / cellSize) + 1;
            int ny = nx;
            var mask = new bool[nx, ny];
            for (int gx = 0; gx < nx; gx++)
            {
                double cx = (gx + 0.5) * cellSize;
                for (int gy = 0; gy < ny; gy++)
                {
                    double cy = (gy + 0.5) * cellSize;
                    double r2 = cx * cx + cy * cy;
                    if (r2 >= rIn * rIn && r2 <= rOut * rOut) mask[gx, gy] = true;
                }
            }
            var env = new Envelope(0, nx * cellSize, 0, ny * cellSize);
            var a = new Pt2(70, 3);   // both near the axes, deep enough into corridor
            var b = new Pt2(3, 70);

            // Discount band along the medial axis r=70 ± 3.
            var costMul = new double[nx, ny];
            for (int gx = 0; gx < nx; gx++)
            {
                double cx = (gx + 0.5) * cellSize;
                for (int gy = 0; gy < ny; gy++)
                {
                    double cy = (gy + 0.5) * cellSize;
                    double r = System.Math.Sqrt(cx * cx + cy * cy);
                    costMul[gx, gy] = (r >= 67 && r <= 73) ? 0.4 : 1.0;
                }
            }

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: true, costMul: costMul);

            result.Success.Should().BeTrue(result.FailureReason);

            // The densifier must keep enough interior PIs that no tangent's
            // midpoint cuts deeply across the inner corner — i.e. every tangent
            // midpoint should still be inside the walkable corridor.
            for (int i = 0; i < result.Path.Length - 1; i++)
            {
                var p0 = result.Path[i];
                var p1 = result.Path[i + 1];
                var midX = (p0.X + p1.X) / 2;
                var midY = (p0.Y + p1.Y) / 2;
                double midR = System.Math.Sqrt(midX * midX + midY * midY);
                midR.Should().BeGreaterOrEqualTo(rIn - 2,
                    $"tangent midpoint ({midX:F1},{midY:F1}) at r={midR:F1} cuts across the inner ring (rIn={rIn})");
                midR.Should().BeLessOrEqualTo(rOut + 2,
                    $"tangent midpoint ({midX:F1},{midY:F1}) at r={midR:F1} escapes the outer ring (rOut={rOut})");
            }

            // The auto-densifier must produce more than the bare 2-PI chord.
            result.Path.Length.Should().BeGreaterOrEqualTo(4,
                $"a curved corridor should produce ≥4 PIs after auto-densify; got {result.Path.Length}");
        }

        [Fact]
        public void CurvatureSimplifier_SharpRightAngleCorner_IsKept()
        {
            // Synthetic raw path: 50 cells going east, then a 90° right turn,
            // then 50 cells going south. The corner is at index 50. The
            // direction-change peak there should produce exactly one interior
            // PI; straight stretches contribute no peaks.
            var raw = new System.Collections.Generic.List<Pt2>();
            for (int i = 0; i <= 50; i++) raw.Add(new Pt2(i, 0));
            for (int i = 1; i <= 50; i++) raw.Add(new Pt2(50, -i));

            var (kept, idx) = CurvatureSimplifier.Simplify(raw,
                windowCells: 8, angleThresholdRad: 0.35, maxPis: 20);

            kept.Length.Should().Be(3, "endpoints + one corner peak");
            kept[0].X.Should().Be(0); kept[0].Y.Should().Be(0);
            kept[2].X.Should().Be(50); kept[2].Y.Should().Be(-50);
            kept[1].X.Should().BeApproximately(50, 1e-9);
            kept[1].Y.Should().BeApproximately(0, 1e-9);
            idx[1].Should().Be(50, "corner is at raw cell 50");
        }

        [Fact]
        public void CurvatureSimplifier_StraightLine_NoInteriorPis()
        {
            // 100 collinear cells: no direction change anywhere. Result must
            // collapse to just the endpoints.
            var raw = new System.Collections.Generic.List<Pt2>();
            for (int i = 0; i < 100; i++) raw.Add(new Pt2(i, 0));

            var (kept, idx) = CurvatureSimplifier.Simplify(raw,
                windowCells: 8, angleThresholdRad: 0.35, maxPis: 20);

            kept.Length.Should().Be(2);
            idx[0].Should().Be(0);
            idx[1].Should().Be(99);
        }

        [Fact]
        public void Route_LongStraightTangent_MaxTangentMSplitsIntoMultiplePis()
        {
            // 200×100 open rectangle with A and B at opposite ends along the
            // midline. Theta* will collapse the path to two anchor cells, but
            // with default max_tangent_m=500, this 200m tangent should still
            // be split into pieces sampled from the (inflated) raw cells.
            // This guards the "rawPts-inflate" fix: without inflate, the
            // densifier saw b−a==1 between every pair and couldn't sample.
            // With inflate, it can sample dense cells along the LoS line.
            // For this 200m chord at default 500m, no split. For 50m max:
            // 200/50 = 4 sub-tangents → 5 PIs total (2 endpoints + 3 inserts).
            var poly = _factory.CreatePolygon(new[]
            {
                new Coordinate(0,   0),
                new Coordinate(200, 0),
                new Coordinate(200, 100),
                new Coordinate(0,   100),
                new Coordinate(0,   0),
            });
            const double cellSize = 2.0;
            var (mask, env, _, _) = Rasterise(poly, cellSize);
            var a = new Pt2(10, 50);
            var b = new Pt2(190, 50);

            var result = GridRouter.Route(mask, env, cellSize, a, b, CancellationToken.None,
                anyAngle: true, maxTangentM: 50.0);

            result.Success.Should().BeTrue(result.FailureReason);
            result.Path.Length.Should().BeGreaterOrEqualTo(4,
                $"a 180m tangent at maxTangentM=50 should produce ≥4 PIs (split into ≥3 pieces); got {result.Path.Length}");
            result.Path[0].X.Should().BeApproximately(a.X, 1e-9);
            result.Path[^1].X.Should().BeApproximately(b.X, 1e-9);
        }

        [Fact]
        public void RoadAffinity_RampShape_MatchesSpec()
        {
            // Unit test on RoadAffinity.BuildCostField — verifies the linear ramp.
            var walkable = new bool[3, 1] { { true }, { true }, { false } };
            var slope    = new double[3, 1] { { 0.0 }, { 0.04 }, { 0.0 } };
            const double threshold = 0.08;
            const double strength  = 0.6;

            var costMul = RoadAffinity.BuildCostField(walkable, slope, threshold, strength);

            // Cell 0: walkable, slope=0 → costMul = 1 - strength*1 = 0.4
            costMul[0, 0].Should().BeApproximately(0.4, 1e-9);
            // Cell 1: walkable, slope=threshold/2 → costMul = 1 - strength*(1 - 0.5) = 1 - 0.3 = 0.7
            costMul[1, 0].Should().BeApproximately(0.7, 1e-9);
            // Cell 2: NOT walkable, slope=0 → costMul = 1.0 regardless
            costMul[2, 0].Should().BeApproximately(1.0, 1e-9);

            // Slope ≥ threshold → costMul = 1.0
            var walkable2 = new bool[1, 1] { { true } };
            var slope2    = new double[1, 1] { { 0.20 } };
            var costMul2 = RoadAffinity.BuildCostField(walkable2, slope2, threshold, strength);
            costMul2[0, 0].Should().BeApproximately(1.0, 1e-9);
        }
    }
}
