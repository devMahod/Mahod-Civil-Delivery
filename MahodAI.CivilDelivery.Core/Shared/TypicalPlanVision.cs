using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;
using Kind = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Kind;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>Local plan vision. Rasterize a station-aligned strip of classified DWG linework, one
/// channel per feature and construction stage. Distances in the image are in metres.</summary>
public sealed class TypicalPlanVision
{
    private sealed record Stroke(P2 A, P2 B, string Kind, int Stage);
    private readonly STRtree<Stroke> _lines = new();
    private const int Reach = 6;
    private const double PixelM = 0.1;
    public const string UnclassifiedStroke = "unclassified-stroke";
    public const string InferredLaneMark = "inferred-lane-mark";
    public const string CivilTrack = "civil-track-centre";

    public void Add(P2 a, P2 b, string kind, int stage)
    {
        if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y) || a == b) return;
        _lines.Insert(new Envelope(a.X, b.X, a.Y, b.Y), new Stroke(a, b, kind, stage));
    }

    public sealed record Feature(int Stage, string Kind, double Offset);

    // Datum points, numeric depth tags and daylight-to-terrain points do not define traffic lanes.
    public static bool IsSectionPointCode(string code)
    {
        var c = code.ToUpperInvariant();
        return c is "TOP" or "HINGE" or "CROWN" || c.StartsWith("EOP", StringComparison.Ordinal) ||
            c.StartsWith("EPS", StringComparison.Ordinal) || c.StartsWith("ETW", StringComparison.Ordinal) ||
            c.Contains("LANE") || c.Contains("SHOULDER") || c.Contains("MEDIAN") ||
            c.Contains("TRACK") || c.Contains("RAIL") || c.Contains("PLATFORM") || c.Contains("CURB");
    }

    /// <summary>On a built road, markings supplement the actual cross-section. A marking on an
    /// adjacent road outside the modeled top must not create a change on this alignment.</summary>
    public static IReadOnlyList<Feature> CombineWithModel(IReadOnlyList<Feature> plan, IReadOnlyList<Feature> model)
    {
        if (model.Count == 0) return plan;
        var top = model.Where(f => f.Kind == "TOP").Select(f => f.Offset).ToArray();
        if (top.Length < 2) return plan.Concat(model).ToList();
        var lo = top.Min(); var hi = top.Max();
        return plan.Where(f => f.Offset >= lo - PixelM && f.Offset <= hi + PixelM).Concat(model).ToList();
    }

    public IReadOnlyList<Feature> Read(P2 axis, double tangentDeg, double halfWidth)
    {
        var angle = tangentDeg * Math.PI / 180;
        var dx = Math.Cos(angle);
        var dy = Math.Sin(angle);
        var rx = Math.Abs(dx) * Reach + Math.Abs(dy) * halfWidth;
        var ry = Math.Abs(dy) * Reach + Math.Abs(dx) * halfWidth;
        var window = new Envelope(axis.X - rx, axis.X + rx, axis.Y - ry, axis.Y + ry);
        // Sparse binary mask. Only occupied columns allocate memory, even for a wide section.
        var channels = new Dictionary<(int Stage, string Kind), SortedDictionary<int, int>>();
        foreach (var line in _lines.Query(window))
        {
            double Along(P2 p) => (p.X - axis.X) * dx + (p.Y - axis.Y) * dy;
            double Across(P2 p) => (p.X - axis.X) * dy - (p.Y - axis.Y) * dx;
            var start = Along(line.A);
            var end = Along(line.B);
            var left = Across(line.A);
            var right = Across(line.B);
            if (Math.Max(start, end) < -Reach || Math.Min(start, end) > Reach) continue;
            // Cross-street markings and transverse platform ends do not define a longitudinal strip.
            if (Math.Abs(end - start) <= Math.Abs(right - left) || Math.Abs(end - start) < 1e-8) continue;
            var first = (int)Math.Ceiling(Math.Max(-Reach, Math.Min(start, end) - 1e-7));
            var last = (int)Math.Floor(Math.Min(Reach, Math.Max(start, end) + 1e-7));
            for (var row = first; row <= last; row++)
            {
                var offset = left + (row - start) / (end - start) * (right - left);
                if (Math.Abs(offset) > halfWidth) continue;
                var key = (line.Stage, line.Kind);
                if (!channels.TryGetValue(key, out var pixels)) channels[key] = pixels = new();
                var column = (int)Math.Round(offset / PixelM);
                pixels.TryGetValue(column, out var mask);
                pixels[column] = mask | (1 << (row + Reach));
            }
        }
        var features = new List<Feature>();
        foreach (var (channel, pixels) in channels)
        {
            if (channel.Kind == UnclassifiedStroke)
            {
                foreach (var (column, mask) in pixels)
                {
                    // Two distinct runs of ink in a longitudinal column, separated by
                    // visible white space. A continuous utility or boundary line fails.
                    var starts = mask & ~(mask << 1);
                    if (System.Numerics.BitOperations.PopCount((uint)starts) >= 2 &&
                        System.Numerics.BitOperations.PopCount((uint)mask) >= 4)
                        features.Add(new Feature(channel.Stage, InferredLaneMark, column * PixelM));
                }
                continue;
            }
            var dashed = channel.Kind is Kind.Marking or Kind.EdgeLine or Kind.HovLine;
            // Prefer the centre scanline. Close a gap in a broken marking with the nearest
            // occupied row, without counting the same taper in every row of the image.
            var centres = pixels.Where(p => (p.Value & (1 << Reach)) != 0).Select(p => p.Key).ToList();
            if (dashed)
            {
                for (int distance = 1; distance <= Reach; distance++)
                {
                    int rows = (1 << (Reach - distance)) | (1 << (Reach + distance));
                    foreach (var (column, mask) in pixels)
                        if ((mask & rows) != 0 && !centres.Any(c => Math.Abs(c - column) * PixelM <= 0.35 + distance * 0.12))
                            centres.Add(column);
                }
            }
            int? previous = null;
            foreach (var column in centres.OrderBy(c => c))
            {
                if (previous == null || (column - previous.Value) * PixelM > 0.25)
                    features.Add(new Feature(channel.Stage, channel.Kind, column * PixelM));
                previous = column;
            }
        }
        var bounds = features.Where(f => f.Kind is Kind.Edge or Kind.EdgeLine or Kind.Curb or Kind.Marking).ToList();
        features.RemoveAll(f => f.Kind == InferredLaneMark && !InsideLane(f, bounds));
        return features.OrderBy(f => f.Stage).ThenBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Offset).ToList();
    }

    private static bool InsideLane(Feature candidate, List<Feature> bounds)
    {
        var offsets = bounds.Where(b => b.Stage == candidate.Stage).Select(b => b.Offset - candidate.Offset).ToArray();
        if (offsets.Any(d => Math.Abs(d) < 0.35)) return false;
        var left = -offsets.Where(d => d < 0).DefaultIfEmpty(double.NegativeInfinity).Max();
        var right = offsets.Where(d => d > 0).DefaultIfEmpty(double.PositiveInfinity).Min();
        static bool LaneWidth(double width) => width >= DraftedTypicalSectionLogic.LaneMinM && width <= DraftedTypicalSectionLogic.LaneMaxM;
        return LaneWidth(left) && LaneWidth(right);
    }

    public sealed record Sample(double Station, IReadOnlyList<Feature> Features, string Assembly = "")
    {
        public bool HasEvidence => Assembly.Length > 0 || Features.Any(f => f.Kind is Kind.Track or CivilTrack) ||
            Features.Where(f => f.Kind is Kind.Edge or Kind.EdgeLine or Kind.Curb or Kind.Marking or Kind.Routing or Kind.Rail or Kind.HovLine)
                .GroupBy(f => f.Stage).Any(g => g.Max(f => f.Offset) - g.Min(f => f.Offset) >= 0.8);
    }

    public sealed record Stretch(double From, double To, Sample Representative);

    /// <summary>Samples arrive in station order for one continuous axis/region. Unknown coverage
    /// breaks a run. Isolated visual noise can be bridged only between two matching observations.</summary>
    public static List<Stretch> Select(IReadOnlyList<Sample> samples, bool modelRegion = false)
    {
        var result = new List<Stretch>();
        for (int i = 0; i < samples.Count;)
        {
            var first = samples[i++];
            if (!first.HasEvidence) continue;
            var startIndex = i - 1;
            var matching = new List<Sample> { first };
            while (i < samples.Count && samples[i].HasEvidence)
            {
                if (Same(first, samples[i])) matching.Add(samples[i++]);
                else if (i + 1 < samples.Count && Same(first, samples[i + 1]))
                {
                    matching.Add(samples[i + 1]);
                    i += 2;
                }
                else break;
            }
            if (matching.Count < 3 && !(modelRegion && first.Assembly.Length > 0 &&
                (matching.Count >= 2 || samples.Count == 1))) continue;
            if (matching.Count * 4 < (i - startIndex) * 3) continue;
            var last = matching[^1].Station;
            if (modelRegion && matching.Count < samples.Count && last - first.Station < 5.0 - 1e-6) continue;
            var middle = first.Station + (last - first.Station) / 2;
            // Prefer positions away from a taper. A steady lateral migration of the
            // boundaries must not generate one section for every metre of widening.
            IEnumerable<Sample> representatives = matching;
            if (!modelRegion && matching.Count >= 3)
                representatives = Enumerable.Range(1, matching.Count - 2)
                    .Where(j => Same(matching[j - 1], matching[j + 1], 0.35)).Select(j => matching[j]);
            var representative = representatives.OrderBy(s => Math.Abs(s.Station - middle)).FirstOrDefault();
            if (representative != null) result.Add(new Stretch(first.Station, last, representative));
        }
        return result;
    }

    private static bool Same(Sample a, Sample b, double offsetTolerance = 1.0)
    {
        if (a.Assembly != b.Assembly || a.Features.Count != b.Features.Count) return false;
        for (var i = 0; i < a.Features.Count; i++)
        {
            var x = a.Features[i];
            var y = b.Features[i];
            if (x.Kind != y.Kind || x.Stage != y.Stage || Math.Abs(x.Offset - y.Offset) > offsetTolerance) return false;
        }
        return true;
    }
}
