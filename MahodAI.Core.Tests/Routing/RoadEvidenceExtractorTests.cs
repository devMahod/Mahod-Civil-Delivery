using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Pure-geometry tests for the road-evidence extraction: given a corridor loop and surveyed
    /// road-edge chains, the extractor must recover the OLD road's centerline (not the corridor
    /// spine, not a side road, not a junction flare) with the ends pinned to the A/B picks.
    /// Mirrors the validated prototype run against the real road-73 survey (2026-07-28).
    /// </summary>
    public class RoadEvidenceExtractorTests
    {
        // ── Geometry builders ───────────────────────────────────────────────

        /// <summary>Polyline sampled every <paramref name="step"/> m along straight legs.</summary>
        private static List<Pt2> Chain(double step, params Pt2[] anchors)
        {
            var pts = new List<Pt2> { anchors[0] };
            for (int i = 1; i < anchors.Length; i++)
            {
                Pt2 s = anchors[i - 1], e = anchors[i];
                double len = s.DistanceTo(e);
                int n = Math.Max(1, (int)Math.Round(len / step));
                for (int k = 1; k <= n; k++)
                    pts.Add(new Pt2(s.X + (e.X - s.X) * k / n, s.Y + (e.Y - s.Y) * k / n));
            }
            return pts;
        }

        /// <summary>Per-vertex normal offset of a chain (adequate for gentle test geometry).</summary>
        private static List<Pt2> Offset(IReadOnlyList<Pt2> chain, double off)
        {
            var outp = new List<Pt2>(chain.Count);
            for (int i = 0; i < chain.Count; i++)
            {
                Pt2 p0 = chain[Math.Max(0, i - 1)], p1 = chain[Math.Min(chain.Count - 1, i + 1)];
                double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) { outp.Add(chain[i]); continue; }
                outp.Add(new Pt2(chain[i].X - dy / len * off, chain[i].Y + dx / len * off));
            }
            return outp;
        }

        /// <summary>Closed corridor loop: one side offset left, the other offset right (reversed).</summary>
        private static List<Pt2> Loop(IReadOnlyList<Pt2> center, double halfWidth)
        {
            var left = Offset(center, halfWidth);
            var right = Offset(center, -halfWidth);
            right.Reverse();
            return left.Concat(right).ToList();
        }

        /// <summary>Survey linework arrives chopped into short pieces — mimic that.</summary>
        private static List<RoadEvidenceExtractor.EvidenceChain> Chop(
            IReadOnlyList<Pt2> chain, string layer, int chunk = 15, int gap = 2)
        {
            var chains = new List<RoadEvidenceExtractor.EvidenceChain>();
            for (int i = 0; i < chain.Count; i += chunk + gap)
            {
                var piece = chain.Skip(i).Take(chunk).ToList();
                if (piece.Count >= 2)
                    chains.Add(new RoadEvidenceExtractor.EvidenceChain(layer, piece));
            }
            return chains;
        }

        private static double DistToChain(Pt2 p, IReadOnlyList<Pt2> chain)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                Pt2 s = chain[i], e = chain[i + 1];
                double dx = e.X - s.X, dy = e.Y - s.Y;
                double l2 = dx * dx + dy * dy;
                double t = l2 < 1e-12 ? 0 : Math.Clamp(((p.X - s.X) * dx + (p.Y - s.Y) * dy) / l2, 0, 1);
                double qx = s.X + t * dx, qy = s.Y + t * dy;
                best = Math.Min(best, Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy)));
            }
            return best;
        }

        // ── Tests ───────────────────────────────────────────────────────────

        [Fact]
        public void Straight_road_offset_from_the_corridor_spine_is_recovered()
        {
            // Corridor is 240 m wide; the old road runs OFF-CENTRE, 40 m left of the spine.
            // Routing on the corridor spine would miss the road by 40 m — the evidence must not.
            var spine = Chain(10, new Pt2(0, 0), new Pt2(2000, 0));
            var road = Chain(10, new Pt2(0, 40), new Pt2(2000, 40));
            var zone = Loop(spine, 120);
            var a = new Pt2(0, 40);
            var b = new Pt2(2000, 40);

            var chains = Chop(Offset(road, 4.0), "KAV-ASFALT")
                .Concat(Chop(Offset(road, -4.0), "KAV-ASFALT")).ToList();

            var result = RoadEvidenceExtractor.Extract(zone, a, b, chains);

            result.Should().NotBeNull();
            result!.Coverage.Should().BeGreaterThan(0.6);
            result.Centerline[0].Should().Be(a);
            result.Centerline[^1].Should().Be(b);
            result.LayersUsed.Should().Contain("KAV-ASFALT");
            // Interior points must sit on the ROAD (y = 40), not the corridor spine (y = 0).
            foreach (var p in result.Centerline.Skip(3).SkipLast(3))
                Math.Abs(p.Y - 40.0).Should().BeLessThan(1.5, $"at x={p.X:F0}");
        }

        [Fact]
        public void Side_road_and_junction_flare_are_gated_out()
        {
            var road = Chain(10, new Pt2(0, 0), new Pt2(2000, 0));
            var zone = Loop(road, 120);
            var a = new Pt2(0, 0);
            var b = new Pt2(2000, 0);

            var chains = Chop(Offset(road, 4.0), "KAV-ASFALT")
                .Concat(Chop(Offset(road, -4.0), "KAV-ASFALT")).ToList();
            // A parallel service road 18 m to the left — same layer, inside the corridor.
            chains.AddRange(Chop(Chain(10, new Pt2(300, 18), new Pt2(1200, 18)), "KAV-ASFALT"));
            chains.AddRange(Chop(Chain(10, new Pt2(300, 26), new Pt2(1200, 26)), "KAV-ASFALT"));
            // A junction flare cutting diagonally away near x=1000.
            chains.Add(new RoadEvidenceExtractor.EvidenceChain(
                "KAV-ASFALT", Chain(5, new Pt2(1000, 4), new Pt2(1040, 28))));

            var result = RoadEvidenceExtractor.Extract(zone, a, b, chains);

            result.Should().NotBeNull();
            foreach (var p in result!.Centerline.Skip(3).SkipLast(3))
                Math.Abs(p.Y).Should().BeLessThan(2.0, $"at x={p.X:F0}");
        }

        [Fact]
        public void A_bent_road_is_followed_through_the_bend()
        {
            // 800 m straight, a gentle bend, 800 m straight at ~30°.
            var truth = Chain(10,
                new Pt2(0, 0), new Pt2(800, 0), new Pt2(1000, 30), new Pt2(1800, 480));
            var zone = Loop(truth, 130);
            var a = truth[0];
            var b = truth[^1];

            var chains = Chop(Offset(truth, 4.0), "EVEN-SAFA")
                .Concat(Chop(Offset(truth, -4.0), "EVEN-SAFA")).ToList();

            var result = RoadEvidenceExtractor.Extract(zone, a, b, chains);

            result.Should().NotBeNull();
            result!.Coverage.Should().BeGreaterThan(0.5);
            // The synthetic truth has a HARD vertex at the bend anchors (real roads don't);
            // smoothing legitimately rounds it, so the tolerance is looser than the straight legs.
            foreach (var p in result.Centerline)
                DistToChain(p, truth).Should().BeLessThan(3.5);
        }

        [Fact]
        public void No_linework_yields_null_but_the_zone_midline_still_works()
        {
            var center = Chain(10, new Pt2(0, 0), new Pt2(2000, 0));
            var zone = Loop(center, 100);
            var a = new Pt2(0, 0);
            var b = new Pt2(2000, 0);

            RoadEvidenceExtractor.Extract(zone, a, b, new List<RoadEvidenceExtractor.EvidenceChain>())
                .Should().BeNull();

            var midline = RoadEvidenceExtractor.ZoneMidline(zone, a, b);
            midline.Should().NotBeNull();
            midline![0].Should().Be(a);
            midline[^1].Should().Be(b);
            foreach (var p in midline)
                Math.Abs(p.Y).Should().BeLessThan(2.0);
        }

        [Fact]
        public void Open_side_pair_corridor_works_like_a_closed_loop()
        {
            // Engineers sometimes draw the blue corridor as TWO separate open polylines, not a
            // closed box (the road-73 sibling drawing that exposed this). Same extraction must
            // work when the sides are handed over directly.
            var road = Chain(10, new Pt2(0, 0), new Pt2(2000, 0));
            var left = Offset(road, 120);
            var right = Offset(road, -120);
            var a = new Pt2(0, 0);
            var b = new Pt2(2000, 0);

            var chains = Chop(Offset(road, 4.0), "KAV-ASFALT")
                .Concat(Chop(Offset(road, -4.0), "KAV-ASFALT")).ToList();

            var result = RoadEvidenceExtractor.Extract(left, right, a, b, chains);
            result.Should().NotBeNull();
            result!.Centerline[0].Should().Be(a);
            result.Centerline[^1].Should().Be(b);
            foreach (var p in result.Centerline.Skip(3).SkipLast(3))
                Math.Abs(p.Y).Should().BeLessThan(2.0, $"at x={p.X:F0}");

            var midline = RoadEvidenceExtractor.SidesMidline(left, right, a, b);
            midline.Should().NotBeNull();
            midline![0].Should().Be(a);
            midline[^1].Should().Be(b);
            foreach (var p in midline)
                Math.Abs(p.Y).Should().BeLessThan(2.0);
        }

        [Fact]
        public void Local_gate_rejections_do_not_poison_later_windows()
        {
            // THE FIELD CRASH (road 73, 2026-07-28): the local-median gate nulled a rejected
            // flare and later windows re-read the nulled slot → "Nullable object must have a
            // value" → whole cascade fell back to plain routing. Reproduce: a junction span
            // where the true edges VANISH and a shifted pair (a ramp mouth) takes over — its
            // picks pass the global gate but jump locally and must be rejected, plural.
            var road = Chain(10, new Pt2(0, 0), new Pt2(2000, 0));
            var zone = Loop(road, 120);
            var a = new Pt2(0, 0);
            var b = new Pt2(2000, 0);

            var leftEdge = Offset(road, 4.0).Where(p => p.X < 950 || p.X > 1150).ToList();
            var rightEdge = Offset(road, -4.0).Where(p => p.X < 950 || p.X > 1150).ToList();
            var chains = Chop(leftEdge, "KAV-ASFALT").Concat(Chop(rightEdge, "KAV-ASFALT")).ToList();
            // Ramp mouth inside the gap: a valid-width pair centred at +5 m (inside the global
            // gate around 0) that the LOCAL gate must throw out station after station.
            chains.AddRange(Chop(Chain(10, new Pt2(950, 9), new Pt2(1150, 9)), "KAV-ASFALT"));
            chains.AddRange(Chop(Chain(10, new Pt2(950, 1), new Pt2(1150, 1)), "KAV-ASFALT"));

            var result = RoadEvidenceExtractor.Extract(zone, a, b, chains);

            result.Should().NotBeNull("a rejected flare must never crash the cascade");
            foreach (var p in result!.Centerline.Skip(3).SkipLast(3))
                Math.Abs(p.Y).Should().BeLessThan(2.5, $"at x={p.X:F0}");
        }

        [Fact]
        public void Real_road73_survey_data_extracts_an_engineer_grade_centerline()
        {
            // The genuine field data this feature was built on: blue corridor loop + asphalt/kerb
            // chains dumped from EG (2).dwg. If this test fails, the feature is broken in the
            // field regardless of what the synthetic cases say.
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "road73_linework.json");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var loop = new List<Pt2>();
            foreach (var v in root.GetProperty("loop").EnumerateArray())
                loop.Add(new Pt2(v[0].GetDouble(), v[1].GetDouble()));
            var a = new Pt2(root.GetProperty("circles")[0][0].GetDouble(),
                            root.GetProperty("circles")[0][1].GetDouble());
            var b = new Pt2(root.GetProperty("circles")[1][0].GetDouble(),
                            root.GetProperty("circles")[1][1].GetDouble());
            var chains = new List<RoadEvidenceExtractor.EvidenceChain>();
            foreach (var seg in root.GetProperty("segments").EnumerateArray())
            {
                var pts = new List<Pt2>();
                foreach (var v in seg.GetProperty("pts").EnumerateArray())
                    pts.Add(new Pt2(v[0].GetDouble(), v[1].GetDouble()));
                chains.Add(new RoadEvidenceExtractor.EvidenceChain(
                    seg.GetProperty("layer").GetString() ?? "", pts));
            }

            var result = RoadEvidenceExtractor.Extract(loop, a, b, chains);

            result.Should().NotBeNull();
            result!.Coverage.Should().BeGreaterThan(0.35);
            result.Centerline[0].Should().Be(a);
            result.Centerline[^1].Should().Be(b);
            result.LayersUsed.Should().Contain("11KAV-ASFALT");

            // The fitter must turn it into engineer geometry: a handful of PIs, not dozens.
            var fit = TangentArcFitter.Fit(result.Centerline, designRadiusM: 400, spiralLenM: 50,
                                           minTangentM: 60, (_, _) => true, _ => true);
            fit.Should().NotBeNull();
            fit!.Pis.Length.Should().BeLessThan(16, "10.8 km of road 73 fits in ~10 real bends");
            fit.Pis.Length.Should().BeGreaterThan(4);
        }

        [Fact]
        public void Ends_are_pinned_to_the_picks_not_the_corridor_caps()
        {
            // The corridor loop runs 200 m PAST the picks on both sides (real corridors do) —
            // the old prototype bug: the midline chased the end caps and the road grew a hook.
            var spine = Chain(10, new Pt2(-200, 0), new Pt2(2200, 0));
            var zone = Loop(spine, 100);
            var a = new Pt2(0, 0);
            var b = new Pt2(2000, 0);

            var midline = RoadEvidenceExtractor.ZoneMidline(zone, a, b);

            midline.Should().NotBeNull();
            midline![0].Should().Be(a);
            midline[^1].Should().Be(b);
            midline.Min(p => p.X).Should().BeGreaterThanOrEqualTo(-1.0);
            midline.Max(p => p.X).Should().BeLessThanOrEqualTo(2001.0);
        }
    }
}
