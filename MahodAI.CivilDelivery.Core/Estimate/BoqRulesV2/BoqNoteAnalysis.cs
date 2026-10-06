using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// v4.5: the figures quoted in the BoQ notes, each traceable on the one-object-once sheet (reference
    /// compute_note_figures.py → note_figures.json). Metres, rounded to 0.1 as the reference.
    /// </summary>
    public sealed record BoqNoteFigures(
        double LoweredTotalM, double LoweredBesideC1M, double LoweredBesideC2M, double LoweredBesideBothM, double LoweredBesideAnyM,
        double CurbC1WithC2FaceM,
        int Frame815Lines, double Frame815PlanM, int Frame815AllLines, double Frame815OneObjectM, double Frame815OneObjectAt1852WidthM,
        int LongRect808Lines, double LongRect808PlanM);

    /// <summary>One overlap layer: polygons, the sum of their areas, the area of their union and the difference (m², 0.01).</summary>
    public sealed record BoqHaLayerOverlap(string Layer, int Count, double SumM2, double UnionM2, double OverlapInsideLayerM2);

    /// <summary>The intersection of two layers' unions (m², 0.01) — only pairs above the ruleset's minimum.</summary>
    public sealed record BoqHaBetween(string LayerA, string LayerB, double AreaM2);

    /// <summary>Hatches of a layer whose area Civil did not measure: how many, how many have a boundary polygon, its area.</summary>
    public sealed record BoqHaMissing(string Layer, int Count, int Built, double AreaM2, IReadOnlyList<string> NotBuilt);

    /// <summary>
    /// v4.5 (reference compute_ha_overlap.py → ha_overlap.json): ESTIMATES from the boundary read of the area hatches, never a
    /// measurement and never subtracted — coverage (hatches in the read, polygons built, polygons of measured hatches compared
    /// with Civil's area and how many agree), overlap inside each layer, between layers, and the boundary area of the hatches
    /// Civil did not measure.
    /// </summary>
    public sealed record BoqHaOverlap(
        int Hatches, int Polygons, int CheckedAgainstCivil, int AgreeWithCivil,
        IReadOnlyList<BoqHaLayerOverlap> Layers, IReadOnlyList<BoqHaBetween> Between, IReadOnlyList<BoqHaMissing> Missing);

    /// <summary>The note figures and hatch estimates of the v4.5 reference, computed from the same inputs as the quantities.</summary>
    public static class BoqNoteAnalysis
    {
        private const double GridCellM = 5.0;

        // ---- note figures (compute_note_figures.py) ----

        public static BoqNoteFigures? NoteFigures(BoqRuleset rules, BoqInputSet input)
        {
            var cfg = rules.NoteFigures;
            if (cfg == null) return null;
            var c1 = Chords(input, cfg.Src, cfg.C1Layers, null);
            var c2 = Chords(input, cfg.Src, cfg.C2Layers, null);
            var low = Chords(input, cfg.Src, cfg.LoweredLayers, null);
            var g1 = Grid(c1);
            var g2 = Grid(c2);
            var byC1 = new List<BoqSegment>();
            var byC2 = new List<BoqSegment>();
            var byBoth = new List<BoqSegment>();
            var byAny = new List<BoqSegment>();
            foreach (var (piece, angle) in Pieces(low, cfg.StepM))
            {
                var mid = piece.Mid;
                var b1 = Beside(mid, angle, g1, 0.0, cfg.ReachM, cfg.MaxAngleRad);
                var b2 = Beside(mid, angle, g2, 0.0, cfg.ReachM, cfg.MaxAngleRad);
                if (b1) byC1.Add(piece);
                if (b2) byC2.Add(piece);
                if (b1 && b2) byBoth.Add(piece);
                if (b1 || b2) byAny.Add(piece);
            }
            double Once(IEnumerable<BoqSegment> chords, double width) => R1(BoqObjectMeasure.ObjectLength(chords, width, rules.Measurement).Length);
            var pair = Pieces(c1, cfg.StepM)
                .Where(p => Beside(p.Piece.Mid, p.Angle, g2, cfg.PairLoM, cfg.PairHiM, cfg.MaxAngleRad))
                .Select(p => p.Piece).ToList();

            var frameEntities = Entities(input, cfg.MarkSrc, cfg.FrameLayer, cfg.LineTypes);
            var frameLengths = frameEntities.Select(e => e.Sum(s => s.Length)).ToList();
            var big = frameEntities.Where((_, i) => frameLengths[i] > cfg.FrameMinM).ToList();
            var rectLengths = Entities(input, cfg.MarkSrc, cfg.LongRectLayer, cfg.LineTypes).Select(e => e.Sum(s => s.Length)).ToList();
            return new BoqNoteFigures(
                Once(low, cfg.LoweredWidthM), Once(byC1, cfg.LoweredWidthM), Once(byC2, cfg.LoweredWidthM),
                Once(byBoth, cfg.LoweredWidthM), Once(byAny, cfg.LoweredWidthM),
                Once(pair, cfg.PairWidthM),
                big.Count, R1(frameLengths.Where(x => x > cfg.FrameMinM).Sum()), frameLengths.Count,
                Once(big.SelectMany(e => e), cfg.FrameWidthM), Once(big.SelectMany(e => e), cfg.FrameAltWidthM),
                rectLengths.Count, R1(rectLengths.Sum()));
        }

        /// <summary>Chords of every record of these layers in this file, each (file, handle) once, in record order.</summary>
        private static List<BoqSegment> Chords(BoqInputSet input, string src, IReadOnlyList<string> layers, IReadOnlyList<string>? etypes) =>
            Entities(input, src, layers, etypes).SelectMany(e => e).ToList();

        private static List<IReadOnlyList<BoqSegment>> Entities(BoqInputSet input, string src, string layer, IReadOnlyList<string>? etypes) =>
            Entities(input, src, new[] { layer }, etypes);

        private static List<IReadOnlyList<BoqSegment>> Entities(BoqInputSet input, string src, IReadOnlyList<string> layers, IReadOnlyList<string>? etypes)
        {
            var layerSet = layers.ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<IReadOnlyList<BoqSegment>>();
            foreach (var r in input.Records)
            {
                if (!string.Equals(r.Src, src, StringComparison.Ordinal) || !layerSet.Contains(r.Layer)) continue;
                if (etypes != null && !etypes.Contains(r.Etype, StringComparer.OrdinalIgnoreCase)) continue;
                if (!seen.Add(r.Handle)) continue;
                if (input.LengthGeometry.TryGetValue((r.Src, r.Handle), out var geo)) result.Add(geo.Segments);
            }
            return result;
        }

        private static Dictionary<(long, long), List<BoqSegment>> Grid(IEnumerable<BoqSegment> chords)
        {
            var grid = new Dictionary<(long, long), List<BoqSegment>>();
            foreach (var c in chords)
            {
                var x0 = (long)Math.Floor(Math.Min(c.A.X, c.B.X) / GridCellM);
                var x1 = (long)Math.Floor(Math.Max(c.A.X, c.B.X) / GridCellM);
                var y0 = (long)Math.Floor(Math.Min(c.A.Y, c.B.Y) / GridCellM);
                var y1 = (long)Math.Floor(Math.Max(c.A.Y, c.B.Y) / GridCellM);
                for (var gx = x0; gx <= x1; gx++)
                    for (var gy = y0; gy <= y1; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var list)) grid[(gx, gy)] = list = new List<BoqSegment>();
                        list.Add(c);
                    }
            }
            return grid;
        }

        /// <summary>A chord of the grid runs parallel to <paramref name="angle"/> at lo..hi from p, and p projects inside it.</summary>
        private static bool Beside(BoqPoint p, double angle, Dictionary<(long, long), List<BoqSegment>> grid, double lo, double hi, double maxAngle)
        {
            if (!grid.TryGetValue(((long)Math.Floor(p.X / GridCellM), (long)Math.Floor(p.Y / GridCellM)), out var list)) return false;
            foreach (var c in list)
            {
                var dx = c.B.X - c.A.X;
                var dy = c.B.Y - c.A.Y;
                var l2 = dx * dx + dy * dy;
                if (l2 < 1e-12) continue;
                var t = ((p.X - c.A.X) * dx + (p.Y - c.A.Y) * dy) / l2;
                if (!(t >= -1e-6 && t <= 1 + 1e-6)) continue;
                var d = Math.Abs((p.X - c.A.X) * dy - (p.Y - c.A.Y) * dx) / Math.Sqrt(l2);
                var a2 = Mod(Math.Atan2(dy, dx), Math.PI);
                var da = Math.Abs(angle - a2);
                if (lo <= d && d <= hi && Math.Min(da, Math.PI - da) < maxAngle) return true;
            }
            return false;
        }

        /// <summary>Each chord cut into ceil(length / step) equal pieces, with the chord's direction in [0, π).</summary>
        private static IEnumerable<(BoqSegment Piece, double Angle)> Pieces(IEnumerable<BoqSegment> chords, double step)
        {
            foreach (var c in chords)
            {
                var length = BoqPoint.Distance(c.A, c.B);
                if (length < 1e-6) continue;
                var n = Math.Max(1, (int)Math.Ceiling(length / step));
                var angle = Mod(Math.Atan2(c.B.Y - c.A.Y, c.B.X - c.A.X), Math.PI);
                for (var k = 0; k < n; k++)
                {
                    double a = (double)k / n, b = (double)(k + 1) / n;
                    yield return (new BoqSegment(
                        new BoqPoint(c.A.X + a * (c.B.X - c.A.X), c.A.Y + a * (c.B.Y - c.A.Y)),
                        new BoqPoint(c.A.X + b * (c.B.X - c.A.X), c.A.Y + b * (c.B.Y - c.A.Y))), angle);
                }
            }
        }

        /// <summary>Python's float modulo (the result takes the divisor's sign).</summary>
        private static double Mod(double a, double m)
        {
            var r = a % m;
            return r < 0 ? r + m : r;
        }

        private static double R1(double x) => Math.Round(x, 1, MidpointRounding.ToEven);
        private static double R2(double x) => Math.Round(x, 2, MidpointRounding.ToEven);

        // ---- area hatches (compute_ha_overlap.py) ----

        public static BoqHaOverlap? HaOverlap(BoqRuleset rules, BoqInputSet input)
        {
            var cfg = rules.HaOverlap;
            if (cfg == null || input.HaHatchPolygons.Count == 0) return null;
            var layers = cfg.Layers;
            var inLayers = layers.ToHashSet(StringComparer.Ordinal);
            var reader = new WKTReader();
            var polys = new Dictionary<string, (string Layer, Geometry Geometry)>(StringComparer.Ordinal);
            var polyOrder = new List<string>();
            foreach (var p in input.HaHatchPolygons)
            {
                if (!inLayers.Contains(p.Layer) || string.IsNullOrWhiteSpace(p.Wkt)) continue;
                var g = reader.Read(p.Wkt);
                if (g.IsEmpty) continue;
                // A repaired boundary can be a collection with stray lines / points (shapely make_valid): only its polygons have
                // area, and OverlayNG refuses mixed-dimension input — keep the polygonal part (same area, same union).
                var polygonal = g.Factory.BuildGeometry(PolygonExtracter.GetPolygons(g));
                if (!polys.ContainsKey(p.Handle)) polyOrder.Add(p.Handle);
                polys[p.Handle] = (p.Layer, polygonal);
            }

            // coverage: polygons of measured hatches against Civil's own area
            var native = input.HaHatches.Where(h => h.State == "direct" && h.NativeAreaM2 is > 0 or < 0)
                .GroupBy(h => h.Handle, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().NativeAreaM2!.Value, StringComparer.Ordinal);
            var checkedCount = 0;
            var agree = 0;
            foreach (var h in polyOrder)
            {
                if (!native.TryGetValue(h, out var n)) continue;
                checkedCount++;
                if (Math.Abs(polys[h].Geometry.Area - n) <= Math.Max(cfg.AgreeAbsM2, cfg.AgreeRel * n)) agree++;
            }

            var unions = new Dictionary<string, Geometry?>(StringComparer.Ordinal);
            var layerRows = new List<BoqHaLayerOverlap>();
            foreach (var layer in layers)
            {
                var gs = polyOrder.Where(h => polys[h].Layer == layer).Select(h => polys[h].Geometry).ToList();
                var nonEmpty = gs.Where(g => !g.IsEmpty).ToList();
                var u = gs.Count == 0 ? null : nonEmpty.Count > 0 ? OverlayNGRobust.Union(nonEmpty) : gs[0].Factory.CreateMultiPolygon();
                unions[layer] = u;
                var sum = gs.Sum(g => g.Area);
                layerRows.Add(new BoqHaLayerOverlap(layer, gs.Count, R2(sum), u != null ? R2(u.Area) : 0, R2(sum - (u?.Area ?? 0))));
            }
            var between = new List<BoqHaBetween>();
            for (var i = 0; i < layers.Count; i++)
                for (var j = i + 1; j < layers.Count; j++)
                {
                    if (unions[layers[i]] is not { } a || unions[layers[j]] is not { } b) continue;
                    var x = OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection).Area;
                    if (x > cfg.MinBetweenM2) between.Add(new BoqHaBetween(layers[i], layers[j], R2(x)));
                }

            var measured = cfg.MeasuredStates.ToHashSet(StringComparer.Ordinal);
            var missingOrder = new List<string>();
            var missing = new Dictionary<string, (int Count, int Built, double Area, List<string> NotBuilt)>(StringComparer.Ordinal);
            foreach (var h in input.HaHatches)
            {
                if (!inLayers.Contains(h.Layer) || measured.Contains(h.State)) continue;
                if (!missing.TryGetValue(h.Layer, out var m)) { missingOrder.Add(h.Layer); m = (0, 0, 0.0, new List<string>()); }
                m.Count++;
                if (polys.TryGetValue(h.Handle, out var poly)) { m.Built++; m.Area += poly.Geometry.Area; }
                else m.NotBuilt.Add(h.Handle);
                missing[h.Layer] = m;
            }
            return new BoqHaOverlap(
                input.HaHatchesInRead ?? input.HaHatches.Count(h => inLayers.Contains(h.Layer)), polys.Count, checkedCount, agree,
                layerRows, between,
                // by layer name (the reference's rows come sorted by layer; the live scan has no such order)
                missingOrder.OrderBy(l => l, StringComparer.Ordinal)
                    .Select(l => new BoqHaMissing(l, missing[l].Count, missing[l].Built, R2(missing[l].Area), missing[l].NotBuilt)).ToList());
        }
    }

    /// <summary>
    /// The reference order of the 'לא נכלל' rows (build_boq_v7 _ekey): reason, then file, then larger quantity first; the
    /// held-back long objects ("לבדיקה: עצם באורך N …") sort by N as a number. Stable (LINQ OrderBy).
    /// </summary>
    internal sealed class ExcludedOrder : IComparer<BoqGroup>
    {
        public static readonly ExcludedOrder Instance = new();
        private const string HeldPrefix = "לבדיקה: עצם באורך";
        private static readonly Regex Held = new(@"^לבדיקה: עצם באורך (\d+)", RegexOptions.CultureInvariant);

        private static (string A, long B, string C, double D) Key(BoqGroup g)
        {
            var m = Held.Match(g.Reason);
            return m.Success && long.TryParse(m.Groups[1].Value, out var n) ? (HeldPrefix, n, "", 0.0) : (g.Reason, 0, g.Src, -g.Quantity);
        }

        public int Compare(BoqGroup? x, BoqGroup? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var (a1, b1, c1, d1) = Key(x);
            var (a2, b2, c2, d2) = Key(y);
            var c = string.CompareOrdinal(a1, a2);
            if (c != 0) return c;
            c = b1.CompareTo(b2);
            if (c != 0) return c;
            c = string.CompareOrdinal(c1, c2);
            return c != 0 ? c : d1.CompareTo(d2);
        }
    }
}
