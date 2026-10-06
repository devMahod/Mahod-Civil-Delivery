using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// End-to-end geometry-quality tests at REAL road scale (3 m cells, kilometre-long band,
    /// R = 400 m, 50 m spirals) — the configuration Stage 3 actually runs.
    ///
    /// These guard the 2026-07-27 fix for "the drawn road is a mess of small turns". Before it,
    /// <see cref="GridRouter"/> densified the routed path into a PI wherever it bowed ~2 cells
    /// (6 m) off its chord — on a gentle bend that is a PI every √(48R) ≈ 138 m — and
    /// <see cref="CurveAttacher"/> filleted each one separately, halving radii and shortening
    /// spirals whenever two curves fought over the same tangent. The router now fits long
    /// straight tangents to the routed path and places ONE curve per real direction change.
    ///
    /// The assertions are the two things an engineer actually looks at: how many curves there
    /// are, and whether each one has the tangent length its radius needs (a curve that does not
    /// is exactly what triggers the radius-relaxation cascade).
    /// </summary>
    public class RoutedGeometryQualityTests
    {
        private const double Cell = 3.0;          // Stage 3 default grid
        private const double Radius = 400.0;      // comfort radius for ~80 km/h
        private const double Spiral = 50.0;       // Israeli interurban convention
        private const double Clearance = 9.0;     // half a road cross-section

        /// <summary>
        /// Builds a walkable band of the given half-width around a centerline function — a survey
        /// surface shaped like a real road corridor.
        /// </summary>
        private static (bool[,] w, Envelope env) BuildBand(
            Func<double, double> centerY, double halfWidthM, double lengthM, double maxYM)
        {
            int nx = (int)Math.Ceiling(lengthM / Cell);
            int ny = (int)Math.Ceiling(maxYM / Cell);
            var w = new bool[nx, ny];
            for (int ix = 0; ix < nx; ix++)
            {
                double x = (ix + 0.5) * Cell;
                double cy = centerY(x);
                for (int iy = 0; iy < ny; iy++)
                {
                    double y = (iy + 0.5) * Cell;
                    if (Math.Abs(y - cy) <= halfWidthM) w[ix, iy] = true;
                }
            }
            return (w, new Envelope(0, nx * Cell, 0, ny * Cell));
        }

        private static double Deflection(Pt2 a, Pt2 b, Pt2 c)
        {
            double v1x = b.X - a.X, v1y = b.Y - a.Y, v2x = c.X - b.X, v2y = c.Y - b.Y;
            double l1 = Math.Sqrt(v1x * v1x + v1y * v1y), l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (l1 < 1e-9 || l2 < 1e-9) return 0;
            return Math.Acos(Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1, 1));
        }

        /// <summary>
        /// Every interior PI must have room for its curve on BOTH adjacent segments:
        /// T = R·tan(Δ/2) + Ls/2. A PI that fails this is one CurveAttacher would have to relax —
        /// the root of the "different radius at every bend" look.
        /// </summary>
        private static void AssertEveryCurveFits(Pt2[] pis)
        {
            for (int i = 1; i < pis.Length - 1; i++)
            {
                double t = Radius * Math.Tan(Deflection(pis[i - 1], pis[i], pis[i + 1]) / 2) + Spiral / 2;
                double before = pis[i - 1].DistanceTo(pis[i]);
                double after = pis[i].DistanceTo(pis[i + 1]);
                // Two adjacent curves share a segment, so each may claim only its own share.
                double tPrev = i >= 2
                    ? Radius * Math.Tan(Deflection(pis[i - 2], pis[i - 1], pis[i]) / 2) + Spiral / 2 : 0;
                double tNext = i + 2 <= pis.Length - 1
                    ? Radius * Math.Tan(Deflection(pis[i], pis[i + 1], pis[i + 2]) / 2) + Spiral / 2 : 0;
                before.Should().BeGreaterThanOrEqualTo(t + tPrev - 1e-6,
                    $"PI {i} needs {t:F0} m of tangent behind it (neighbour takes {tPrev:F0} m)");
                after.Should().BeGreaterThanOrEqualTo(t + tNext - 1e-6,
                    $"PI {i} needs {t:F0} m of tangent ahead of it (neighbour takes {tNext:F0} m)");
            }
        }

        private static GridRouter.RouteResult RouteStage3(bool[,] w, Envelope env, Pt2 a, Pt2 b)
            => GridRouter.Route(
                w, env, Cell, a, b, CancellationToken.None,
                minClearanceM: Clearance,
                centered: true, centredness: 6.0, centerBalance: true,
                designRadiusM: Radius, spiralLenM: Spiral);

        [Fact]
        public void A_wide_straight_corridor_draws_as_one_tangent()
        {
            // The road has room to go straight → it must be ONE tangent with no curves at all.
            var (w, env) = BuildBand(_ => 150.0, halfWidthM: 60, lengthM: 1500, maxYM: 300);

            var route = RouteStage3(w, env, new Pt2(30, 150), new Pt2(1470, 150));

            route.Success.Should().BeTrue();
            route.PathSource.Should().Be("tangent_arc_fit");
            route.Path.Should().HaveCount(2, "a straight corridor is one tangent, not a curve chain");
        }

        [Fact]
        public void A_gentle_S_corridor_draws_as_a_handful_of_curves_not_a_chain()
        {
            // A 1.5 km corridor sweeping through a full S at ~500 m radius — the shape that used
            // to arrive as a PI every ~140 m (≈10 curves, each with its own spirals and a relaxed
            // radius). It is geometrically 2 direction changes; allow a little slack for the
            // sinusoid's continuously-varying curvature.
            const double amp = 114.0, wave = 1500.0;
            var (w, env) = BuildBand(x => 250.0 + amp * Math.Sin(2 * Math.PI * x / wave),
                                     halfWidthM: 60, lengthM: 1500, maxYM: 500);

            var route = RouteStage3(w, env, new Pt2(30, 250 + amp * Math.Sin(2 * Math.PI * 30 / wave)),
                                             new Pt2(1470, 250 + amp * Math.Sin(2 * Math.PI * 1470 / wave)));

            route.Success.Should().BeTrue();
            // Measured 2026-07-27: 5 PIs. This band is only 120 m wide, so a straighter fit would
            // leave the surface and is (correctly) refused — the improved fallback still gets it
            // down from the old PI-every-~140 m chain.
            route.Path.Length.Should().BeLessThanOrEqualTo(8,
                "an S is a few direction changes — the old densifier produced a PI every ~140 m");
            AssertEveryCurveFits(route.Path);
        }

        [Fact]
        public void A_wide_S_corridor_fits_one_curve_per_direction_change()
        {
            // Same S, but in a corridor wide enough that straighter geometry stays on the surface.
            // This is the case the tangent-arc fit exists for: it must return exactly the two real
            // direction changes (4 PIs = start, turn, turn, end), not a chain.
            const double amp = 114.0, wave = 1500.0;
            var (w, env) = BuildBand(x => 250.0 + amp * Math.Sin(2 * Math.PI * x / wave),
                                     halfWidthM: 130, lengthM: 1500, maxYM: 640);

            var route = RouteStage3(w, env, new Pt2(30, 250 + amp * Math.Sin(2 * Math.PI * 30 / wave)),
                                             new Pt2(1470, 250 + amp * Math.Sin(2 * Math.PI * 1470 / wave)));

            route.Success.Should().BeTrue();
            route.PathSource.Should().Be("tangent_arc_fit");
            route.Path.Should().HaveCount(4, "an S has exactly two real direction changes");
            AssertEveryCurveFits(route.Path);
        }

        [Fact]
        public void Every_pi_of_a_curved_corridor_lands_inside_the_buildable_area()
        {
            // Straightening must never buy smoothness with containment: the post-creation
            // readback is a hard gate that would reject the whole alignment.
            const double amp = 114.0, wave = 1500.0;
            var (w, env) = BuildBand(x => 250.0 + amp * Math.Sin(2 * Math.PI * x / wave),
                                     halfWidthM: 60, lengthM: 1500, maxYM: 500);

            var route = RouteStage3(w, env, new Pt2(30, 250 + amp * Math.Sin(2 * Math.PI * 30 / wave)),
                                             new Pt2(1470, 250 + amp * Math.Sin(2 * Math.PI * 1470 / wave)));

            route.Success.Should().BeTrue();
            int nx = w.GetLength(0), ny = w.GetLength(1);
            foreach (var p in route.Path)
            {
                int cx = (int)Math.Floor((p.X - env.MinX) / Cell);
                int cy = (int)Math.Floor((p.Y - env.MinY) / Cell);
                cx.Should().BeInRange(0, nx - 1);
                cy.Should().BeInRange(0, ny - 1);
                w[cx, cy].Should().BeTrue("every PI must sit on the buildable surface");
            }

            // And the geometry BETWEEN the PIs — the tangents — must stay on the surface too.
            foreach (var (p0, p1) in Segments(route.Path))
            {
                int steps = Math.Max(1, (int)(p0.DistanceTo(p1) / Cell));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    int cx = (int)Math.Floor((p0.X + t * (p1.X - p0.X) - env.MinX) / Cell);
                    int cy = (int)Math.Floor((p0.Y + t * (p1.Y - p0.Y) - env.MinY) / Cell);
                    w[Math.Clamp(cx, 0, nx - 1), Math.Clamp(cy, 0, ny - 1)]
                        .Should().BeTrue("a fitted tangent must not leave the buildable area");
                }
            }
        }

        private static IEnumerable<(Pt2, Pt2)> Segments(Pt2[] p)
        {
            for (int i = 0; i < p.Length - 1; i++) yield return (p[i], p[i + 1]);
        }
    }
}
