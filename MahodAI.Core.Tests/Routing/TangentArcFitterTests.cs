using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Pure-geometry tests for the engineer-style alignment fit: long straight tangents joined by
    /// ONE curve per real direction change. No AutoCAD host types, so these run headless.
    /// </summary>
    public class TangentArcFitterTests
    {
        private static readonly Func<Pt2, Pt2, bool> AnyChord = (_, _) => true;
        private static readonly Func<Pt2, bool> AnyPoint = _ => true;

        /// <summary>Straight run from a to b sampled every <paramref name="step"/> metres (b included).</summary>
        private static void AddLine(List<Pt2> into, Pt2 a, Pt2 b, double step = 5.0)
        {
            double len = a.DistanceTo(b);
            int n = Math.Max(1, (int)Math.Round(len / step));
            for (int i = 1; i <= n; i++)
                into.Add(new Pt2(a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n));
        }

        /// <summary>Circular arc from the current heading, used to build a realistic routed path.</summary>
        private static void AddArc(List<Pt2> into, Pt2 center, double radius, double from, double to,
                                   double step = 5.0)
        {
            double sweep = to - from;
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) * radius / step));
            for (int i = 1; i <= n; i++)
            {
                double phi = from + sweep * i / n;
                into.Add(new Pt2(center.X + radius * Math.Cos(phi), center.Y + radius * Math.Sin(phi)));
            }
        }

        private static double Deflection(Pt2 a, Pt2 b, Pt2 c)
        {
            double v1x = b.X - a.X, v1y = b.Y - a.Y, v2x = c.X - b.X, v2y = c.Y - b.Y;
            double m1 = Math.Sqrt(v1x * v1x + v1y * v1y), m2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            return Math.Acos(Math.Clamp((v1x * v2x + v1y * v2y) / (m1 * m2), -1, 1));
        }

        [Fact]
        public void A_straight_route_becomes_a_single_tangent()
        {
            // A dead-straight road with grid-scale noise must draw as ONE tangent, no PIs.
            var pts = new List<Pt2> { new Pt2(0, 0) };
            for (int x = 10; x <= 1200; x += 10) pts.Add(new Pt2(x, (x % 20 == 0) ? 0.4 : -0.4));
            pts.Add(new Pt2(1210, 0));

            var fit = TangentArcFitter.Fit(pts, designRadiusM: 400, spiralLenM: 50,
                                           minTangentM: 100, AnyChord, AnyPoint);

            fit.Should().NotBeNull();
            fit!.Pis.Should().HaveCount(2, "a straight road has no intersection points");
            fit.Pis[0].Should().Be(new Pt2(0, 0));
            fit.Pis[^1].Should().Be(new Pt2(1210, 0));
        }

        [Fact]
        public void One_smooth_bend_becomes_one_pi()
        {
            // THE REGRESSION THIS CLASS EXISTS FOR: 600 m straight, a single 400 m-radius 60° bend,
            // 600 m straight. The old densifier emitted a PI roughly every 140 m through the bend
            // (mid-ordinate L²/8R > 6 m) and CurveAttacher filleted each one separately. The fit
            // must produce exactly ONE PI, and its deflection must equal the bend's 60°.
            var pts = new List<Pt2> { new Pt2(0, 0) };
            AddLine(pts, new Pt2(0, 0), new Pt2(600, 0));
            // Left-hand 60° arc of radius 400 starting at (600,0) heading east: centre is at
            // (600, 400); the arc runs from φ=-90° to φ=-30°.
            AddArc(pts, new Pt2(600, 400), 400, -Math.PI / 2, -Math.PI / 2 + Math.PI / 3);
            var end = pts[^1];
            AddLine(pts, end, new Pt2(end.X + 600 * Math.Cos(Math.PI / 3),
                                      end.Y + 600 * Math.Sin(Math.PI / 3)));

            var fit = TangentArcFitter.Fit(pts, designRadiusM: 400, spiralLenM: 50,
                                           minTangentM: 100, AnyChord, AnyPoint);

            fit.Should().NotBeNull();
            fit!.Pis.Should().HaveCount(3, "start, one PI at the bend, end");
            Deflection(fit.Pis[0], fit.Pis[1], fit.Pis[2])
                .Should().BeApproximately(Math.PI / 3, 0.05, "the fitted PI carries the whole 60° bend");
        }

        [Fact]
        public void Chain_of_small_wobbles_collapses_instead_of_becoming_many_curves()
        {
            // A "straight" road the router drew as a 12-wobble chain (±6 m every 100 m) — each
            // wobble used to clear the 6 m deviation trigger and become its own curve.
            var pts = new List<Pt2> { new Pt2(0, 0) };
            for (int i = 1; i <= 24; i++)
                AddLine(pts, pts[^1], new Pt2(i * 50, (i % 2 == 0) ? 6.0 : -6.0));

            var fit = TangentArcFitter.Fit(pts, designRadiusM: 400, spiralLenM: 50,
                                           minTangentM: 100, AnyChord, AnyPoint);

            fit.Should().NotBeNull();
            fit!.Pis.Length.Should().BeLessThanOrEqualTo(3,
                "grid wobble is not a road bend — it must not survive as a chain of PIs");
        }

        [Fact]
        public void Two_real_turns_stay_two_pis()
        {
            // A genuine S: 500 m east, 45° left, 500 m, 45° right, 500 m. Both turns are real and
            // separated by a full tangent, so both must survive — straightening must not eat them.
            var pts = new List<Pt2> { new Pt2(0, 0) };
            AddLine(pts, new Pt2(0, 0), new Pt2(500, 0));
            AddArc(pts, new Pt2(500, 400), 400, -Math.PI / 2, -Math.PI / 4);
            var p = pts[^1];
            double h = Math.PI / 4;
            var q = new Pt2(p.X + 500 * Math.Cos(h), p.Y + 500 * Math.Sin(h));
            AddLine(pts, p, q);
            // Right-hand 45° arc back to due east.
            var c2 = new Pt2(q.X + 400 * Math.Cos(h - Math.PI / 2), q.Y + 400 * Math.Sin(h - Math.PI / 2));
            AddArc(pts, c2, 400, h + Math.PI / 2, h + Math.PI / 2 - Math.PI / 4);
            AddLine(pts, pts[^1], new Pt2(pts[^1].X + 500, pts[^1].Y));

            var fit = TangentArcFitter.Fit(pts, designRadiusM: 400, spiralLenM: 50,
                                           minTangentM: 100, AnyChord, AnyPoint);

            fit.Should().NotBeNull();
            fit!.Pis.Should().HaveCount(4, "start, two real turns, end");
        }

        [Fact]
        public void Never_returns_a_fit_whose_chord_leaves_the_buildable_area()
        {
            // chordViable=false everywhere: no straight tangent is legal, so the fitter must give
            // up (null) and let the caller keep its corridor-tracking fallback — it must never
            // hand back geometry the containment gate would reject.
            var pts = new List<Pt2> { new Pt2(0, 0) };
            AddLine(pts, new Pt2(0, 0), new Pt2(400, 0));
            AddArc(pts, new Pt2(400, 300), 300, -Math.PI / 2, 0);
            AddLine(pts, pts[^1], new Pt2(pts[^1].X, pts[^1].Y + 400));

            var fit = TangentArcFitter.Fit(pts, designRadiusM: 300, spiralLenM: 50,
                                           minTangentM: 100, (_, _) => false, AnyPoint);

            fit.Should().BeNull();
        }

        [Fact]
        public void Never_returns_a_fit_whose_curve_bulge_leaves_the_buildable_area()
        {
            // Chords are legal but the curve interior is not (pointLegal=false) — the arc bulge
            // would fail the post-creation containment gate, so the fit must be refused.
            var pts = new List<Pt2> { new Pt2(0, 0) };
            AddLine(pts, new Pt2(0, 0), new Pt2(600, 0));
            AddArc(pts, new Pt2(600, 400), 400, -Math.PI / 2, -Math.PI / 2 + Math.PI / 3);
            AddLine(pts, pts[^1], new Pt2(pts[^1].X + 300, pts[^1].Y + 520));

            var fit = TangentArcFitter.Fit(pts, designRadiusM: 400, spiralLenM: 50,
                                           minTangentM: 100, AnyChord, _ => false);

            fit.Should().BeNull();
        }

        [Fact]
        public void Every_kept_pi_has_room_for_its_curve_and_the_minimum_tangent()
        {
            // Accepted geometry must be buildable: for each interior PI the tangent run
            // T = R·tan(Δ/2) + Ls/2 must fit, with minTangentM of true straight between curves.
            // Two 30° turns only 200 m apart cannot both host a 400 m curve — one must be merged.
            const double R = 400, Ls = 50, MinTan = 100;
            var pts = new List<Pt2> { new Pt2(0, 0) };
            AddLine(pts, new Pt2(0, 0), new Pt2(700, 0));
            AddArc(pts, new Pt2(700, R), R, -Math.PI / 2, -Math.PI / 2 + Math.PI / 6);
            AddLine(pts, pts[^1], new Pt2(pts[^1].X + 200 * Math.Cos(Math.PI / 6),
                                          pts[^1].Y + 200 * Math.Sin(Math.PI / 6)));
            var s = pts[^1];
            var c2 = new Pt2(s.X + R * Math.Cos(Math.PI / 6 - Math.PI / 2),
                             s.Y + R * Math.Sin(Math.PI / 6 - Math.PI / 2));
            AddArc(pts, c2, R, Math.PI / 6 + Math.PI / 2, Math.PI / 6 + Math.PI / 2 - Math.PI / 6);
            AddLine(pts, pts[^1], new Pt2(pts[^1].X + 700, pts[^1].Y));

            var fit = TangentArcFitter.Fit(pts, R, Ls, MinTan, AnyChord, AnyPoint);

            fit.Should().NotBeNull();
            var pis = fit!.Pis;
            for (int i = 0; i < pis.Length - 1; i++)
            {
                double t0 = i >= 1
                    ? R * Math.Tan(Deflection(pis[i - 1], pis[i], pis[i + 1]) / 2) + Ls / 2 : 0;
                double t1 = i + 2 <= pis.Length - 1
                    ? R * Math.Tan(Deflection(pis[i], pis[i + 1], pis[i + 2]) / 2) + Ls / 2 : 0;
                double need = t0 + t1 + (t0 > 0 && t1 > 0 ? MinTan : 0);
                pis[i].DistanceTo(pis[i + 1]).Should().BeGreaterThanOrEqualTo(need - 1e-6,
                    $"the curves around segment {i} must fit on it");
            }
        }

        [Fact]
        public void Endpoints_are_never_moved()
        {
            var pts = new List<Pt2> { new Pt2(17.5, -3.25) };
            AddLine(pts, pts[^1], new Pt2(617.5, -3.25));
            AddArc(pts, new Pt2(617.5, 396.75), 400, -Math.PI / 2, -Math.PI / 4);
            AddLine(pts, pts[^1], new Pt2(pts[^1].X + 400, pts[^1].Y + 400));

            var fit = TangentArcFitter.Fit(pts, 400, 50, 100, AnyChord, AnyPoint);

            fit.Should().NotBeNull();
            fit!.Pis[0].Should().Be(pts[0]);
            fit.Pis[^1].Should().Be(pts[^1]);
        }

        [Fact]
        public void Degenerate_inputs_are_rejected_not_thrown()
        {
            TangentArcFitter.Fit(new List<Pt2> { new Pt2(0, 0), new Pt2(1, 1) },
                                 400, 50, 100, AnyChord, AnyPoint).Should().BeNull();
            TangentArcFitter.Fit(new List<Pt2> { new Pt2(0, 0), new Pt2(1, 1), new Pt2(2, 2) },
                                 designRadiusM: 0, 50, 100, AnyChord, AnyPoint).Should().BeNull();
        }
    }
}
