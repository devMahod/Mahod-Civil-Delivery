using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Offline candidate: cut/fill BEFORE stripping. Caller must prove source identity,
/// units (all coordinates here are metres), absolute elevation and surface-domain
/// continuity. This kernel cannot prove native reads, MK holes between supplied
/// vertices, the chosen scope, or approval. No extrapolation or gap repair.
/// </summary>
public static class CorridorBotSurfaceLogic
{
    public const string Method = "corridor-bot-surface-cutfill-avg-end-area/v1-candidate";
    // CutFill's existing numeric resolution; refuse sub-resolution cells, never erase them.
    private const double Resolution = 1e-9;
    public sealed record Point(double OffsetM, double ElevationM);
    public sealed record Segment(Point A, Point B);
    public sealed record Part(IReadOnlyList<Point> Points);
    public sealed record RegionKey(string CorridorId, string BaselineId, string RegionId);
    public sealed record Schedule(RegionKey Key, double StartM, double EndM,
        IReadOnlyList<double> StationsM);
    public sealed record StationInput(RegionKey Key, double StationM, double OffsetFromM,
        double OffsetToM, IReadOnlyList<Segment> Bot, IReadOnlyList<Part> GroundParts,
        bool ReadSucceeded, string RawReceiptRef, string? ReadFailure = null)
    {
        /// <summary>
        /// Explicit scope declaration: caller proves the complete native Bot inventory.
        /// Measure its domain union, not the convex hull. Internal gaps are out of
        /// this quantity's scope, NOT proven zero earthworks. Default remains strict.
        /// </summary>
        public bool AllowDisconnectedBotDomains { get; init; } = false;
    }
    public sealed record Issue(string Code, RegionKey? Key, double? StationM, string Detail);
    public sealed record StationResult(RegionKey Key, double StationM, bool Complete,
        double? CutAreaM2, double? FillAreaM2, IReadOnlyList<Segment> Envelope,
        string RawReceiptRef)
    {
        public string BotDomainMode { get; init; } = "unproven";
    }
    public sealed record IntegratedPair(RegionKey Key, double FromM, double ToM,
        double CutM3, double FillM3, string FromReceiptRef, string ToReceiptRef);
    public sealed record Result(bool Complete, double? ObservedCutM3, double? ObservedFillM3,
        IReadOnlyList<StationResult> Stations, IReadOnlyList<IntegratedPair> Pairs,
        IReadOnlyList<Issue> Issues)
    {
        public string Status => Complete ? "complete-before-stripping" : "partial-unapproved";
        public IReadOnlyList<string> BotDomainModes => Stations.Select(s => s.BotDomainMode)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public static Result MeasureMetres(IReadOnlyList<Schedule> schedules,
        IReadOnlyList<StationInput> inputs, double maxStationGapM)
    {
        var issues = new List<Issue>();
        var stations = new List<StationResult>();
        var pairs = new List<IntegratedPair>();
        void Fail(string code, RegionKey? key, double? station, string detail) =>
            issues.Add(new(code, key, station, detail));
        if (!double.IsFinite(maxStationGapM) || maxStationGapM <= 0)
            Fail("invalid-max-gap", null, null, "A finite positive station gap limit is required.");
        if (schedules.Count == 0) Fail("no-schedule", null, null, "No declared regions.");
        var duplicateKeys = schedules.GroupBy(s => s.Key).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet();
        foreach (var key in duplicateKeys)
            Fail("duplicate-region", key, null, "Region identity is ambiguous; no integration.");
        var overlappingKeys = new HashSet<RegionKey>();
        for (var i = 0; i < schedules.Count; i++)
            for (var j = i + 1; j < schedules.Count; j++)
            {
                var a = schedules[i]; var b = schedules[j];
                if (a.Key is null || b.Key is null || a.Key == b.Key ||
                    a.Key.CorridorId != b.Key.CorridorId || a.Key.BaselineId != b.Key.BaselineId ||
                    !double.IsFinite(a.StartM) || !double.IsFinite(a.EndM) ||
                    !double.IsFinite(b.StartM) || !double.IsFinite(b.EndM) ||
                    Math.Min(a.EndM, b.EndM) <= Math.Max(a.StartM, b.StartM)) continue;
                overlappingKeys.Add(a.Key); overlappingKeys.Add(b.Key);
                Fail("overlapping-regions", a.Key, null,
                    $"Region interiors overlap '{b.Key.RegionId}' on the same corridor/baseline; neither is integrated.");
            }
        foreach (var input in inputs)
            if (!schedules.Any(s => s.Key == input.Key && s.StationsM.Contains(input.StationM)))
                Fail("unexpected-station", input.Key, FiniteOrNull(input.StationM),
                    "Observation has no exact declared station identity; it is not integrated.");
        foreach (var schedule in schedules)
        {
            var key = schedule.Key;
            if (duplicateKeys.Contains(key) || overlappingKeys.Contains(key)) continue;
            var expected = schedule.StationsM;
            if (key is null || string.IsNullOrWhiteSpace(key.CorridorId) ||
                string.IsNullOrWhiteSpace(key.BaselineId) || string.IsNullOrWhiteSpace(key.RegionId) ||
                !double.IsFinite(schedule.StartM) || !double.IsFinite(schedule.EndM) ||
                schedule.EndM <= schedule.StartM || expected.Count < 2 ||
                expected.Any(s => !double.IsFinite(s)) ||
                expected.Zip(expected.Skip(1), (a, b) => b <= a).Any(b => b))
            {
                Fail("invalid-schedule", key, null,
                    "Finite, strictly increasing stations and nonempty identities are required.");
                continue;
            }
            if (expected[0] != schedule.StartM || expected[^1] != schedule.EndM)
            {
                Fail("missing-region-boundary", key, null,
                    "Declared station schedule must include both actual region boundaries.");
                continue;
            }
            var observed = new List<StationResult>();
            foreach (var st in expected)
            {
                var matches = inputs.Where(s => s.Key == key && s.StationM == st).ToList();
                if (matches.Count != 1)
                {
                    Fail(matches.Count == 0 ? "missing-station" : "duplicate-station", key, st,
                        "Exactly one observation per scheduled station is required.");
                    observed.Add(new(key, st, false, null, null, Array.Empty<Segment>(), ""));
                    continue;
                }
                observed.Add(MeasureStation(matches[0], issues));
            }
            stations.AddRange(observed);
            for (var i = 1; i < observed.Count; i++)
            {
                var a = observed[i - 1]; var b = observed[i];
                var gap = b.StationM - a.StationM;
                if (!double.IsFinite(gap) || !double.IsFinite(maxStationGapM) ||
                    maxStationGapM <= 0 || gap > maxStationGapM)
                {
                    Fail("unintegrated-gap", key, b.StationM, "Adjacent interval exceeds the declared limit.");
                    continue;
                }
                if (!a.Complete || !b.Complete) continue; // Never bridge a failed/missing station.
                var cut = (a.CutAreaM2!.Value / 2 + b.CutAreaM2!.Value / 2) * gap;
                var fill = (a.FillAreaM2!.Value / 2 + b.FillAreaM2!.Value / 2) * gap;
                if (!double.IsFinite(cut) || !double.IsFinite(fill))
                {
                    Fail("volume-overflow", key, b.StationM, "Non-finite interval volume.");
                    continue;
                }
                pairs.Add(new(key, a.StationM, b.StationM, cut, fill, a.RawReceiptRef, b.RawReceiptRef));
            }
        }
        double? totalCut = pairs.Count == 0 ? null : pairs.Sum(p => p.CutM3);
        double? totalFill = pairs.Count == 0 ? null : pairs.Sum(p => p.FillM3);
        if (totalCut.HasValue && (!double.IsFinite(totalCut.Value) || !double.IsFinite(totalFill!.Value)))
        {
            Fail("total-overflow", null, null, "Non-finite aggregate; interval receipts remain available.");
            totalCut = totalFill = null;
        }
        return new(issues.Count == 0 && pairs.Count > 0, totalCut, totalFill, stations, pairs, issues);
    }

    private static StationResult MeasureStation(StationInput input, List<Issue> issues)
    {
        var domainMode = input.AllowDisconnectedBotDomains
            ? "union-of-reported-bot-domains" : "contiguous-declared-offset-interval";
        StationResult Fail(string code, string detail, IReadOnlyList<Segment>? envelope = null)
        {
            issues.Add(new(code, input.Key, input.StationM, detail));
            return new(input.Key, input.StationM, false, null, null,
                envelope ?? Array.Empty<Segment>(), input.RawReceiptRef) { BotDomainMode = domainMode };
        }
        if (!input.ReadSucceeded || !string.IsNullOrEmpty(input.ReadFailure))
            return Fail("read-failed", input.ReadFailure ?? "Caller reported an incomplete native read.");
        if (string.IsNullOrWhiteSpace(input.RawReceiptRef))
            return Fail("missing-receipt", "A stable raw receipt reference is required (caller verifies its hash).");
        if (!double.IsFinite(input.OffsetFromM) || !double.IsFinite(input.OffsetToM) ||
            input.OffsetToM - input.OffsetFromM <= Resolution)
            return Fail("invalid-offset-domain", "A finite nondegenerate declared Bot domain is required.");
        if (input.Bot.Count == 0 || input.Bot.Any(s => !Finite(s.A) || !Finite(s.B)))
            return Fail("invalid-bot", "Bot input is empty or has non-finite coordinates.");
        var bot = input.Bot.Select(s => s.A.OffsetM <= s.B.OffsetM ? s : new Segment(s.B, s.A))
            .ToList();
        if (bot.Any(s => s.A.OffsetM < input.OffsetFromM || s.B.OffsetM > input.OffsetToM))
            return Fail("bot-outside-domain", "Declared domain must include all supplied Bot segments.");
        // Vertical links remain in raw input; they have zero area, not a sloping bridge.
        bot = bot.Where(s => s.B.OffsetM > s.A.OffsetM).ToList();
        if (bot.Count == 0) return Fail("invalid-bot", "Bot has no horizontal offset span.");
        if (input.AllowDisconnectedBotDomains &&
            (bot.Min(s => s.A.OffsetM) != input.OffsetFromM || bot.Max(s => s.B.OffsetM) != input.OffsetToM))
            return Fail("bot-domain-boundary", "Domain union must still reach both declared outer bounds; only internal gaps are permitted.");
        var ground = new List<Segment>();
        foreach (var part in input.GroundParts)
        {
            if (part.Points.Count < 2 || part.Points.Any(p => !Finite(p)) ||
                part.Points.Zip(part.Points.Skip(1), (a, b) => b.OffsetM <= a.OffsetM).Any(x => x))
                return Fail("invalid-ground-part", "Each ground part must be finite and strictly offset-increasing.");
            ground.AddRange(part.Points.Zip(part.Points.Skip(1), (a, b) => new Segment(a, b)));
        }
        for (var i = 0; i < ground.Count; i++)
            for (var j = i + 1; j < ground.Count; j++)
                if (Math.Min(ground[i].B.OffsetM, ground[j].B.OffsetM) >
                    Math.Max(ground[i].A.OffsetM, ground[j].A.OffsetM))
                    return Fail("ambiguous-ground", "Ground parts overlap; no arbitrary branch selection.");

        var xs = new SortedSet<double> { input.OffsetFromM, input.OffsetToM };
        foreach (var s in bot) { xs.Add(s.A.OffsetM); xs.Add(s.B.OffsetM); }
        foreach (var s in ground)
        {
            if (s.A.OffsetM > input.OffsetFromM && s.A.OffsetM < input.OffsetToM) xs.Add(s.A.OffsetM);
            if (s.B.OffsetM > input.OffsetFromM && s.B.OffsetM < input.OffsetToM) xs.Add(s.B.OffsetM);
        }
        // Split every Bot crossing before choosing the lower branch on each open interval.
        for (var i = 0; i < bot.Count; i++)
            for (var j = i + 1; j < bot.Count; j++)
            {
                var lo = Math.Max(bot[i].A.OffsetM, bot[j].A.OffsetM);
                var hi = Math.Min(bot[i].B.OffsetM, bot[j].B.OffsetM);
                if (hi <= lo) continue;
                var d0 = Z(bot[i], lo) - Z(bot[j], lo);
                var d1 = Z(bot[i], hi) - Z(bot[j], hi);
                if (!double.IsFinite(d0) || !double.IsFinite(d1))
                    return Fail("coordinate-overflow", "Bot elevation difference overflow.");
                if ((d0 < 0 && d1 > 0) || (d0 > 0 && d1 < 0))
                {
                    // Stable crossing fraction, avoiding |d0| + |d1| overflow.
                    var scale = Math.Max(Math.Abs(d0), Math.Abs(d1));
                    var t = (Math.Abs(d0) / scale) / (Math.Abs(d0) / scale + Math.Abs(d1) / scale);
                    xs.Add(lo + (hi - lo) * t);
                }
            }
        var breaks = xs.ToArray();
        var envelope = new List<Segment>();
        double cut = 0, fill = 0;
        for (var i = 1; i < breaks.Length; i++)
        {
            var lo = breaks[i - 1]; var hi = breaks[i];
            var middle = lo + (hi - lo) / 2;
            var candidates = bot.Where(s => s.A.OffsetM <= lo && s.B.OffsetM >= hi).ToList();
            if (candidates.Count == 0)
            {
                if (input.AllowDisconnectedBotDomains) continue; // Out of declared union, never a connecting ramp or a known zero.
                return Fail("bot-offset-gap", $"Uncovered Bot interval [{lo:R},{hi:R}].", envelope);
            }
            if (hi - lo <= Resolution)
                return Fail("below-resolution", "Offset cell is below existing CutFill resolution; not silently dropped.", envelope);
            var bottom = candidates.OrderBy(s => Z(s, middle)).First();
            var piece = new Segment(new(lo, Z(bottom, lo)), new(hi, Z(bottom, hi)));
            envelope.Add(piece); // Separate pieces deliberately preserve same-offset elevation jumps.
            var eg = ground.Where(s => s.A.OffsetM <= lo && s.B.OffsetM >= hi).ToList();
            if (eg.Count != 1) return Fail("ground-offset-gap", $"Ground does not cover Bot interval [{lo:R},{hi:R}].", envelope);
            var area = CorridorQuantityLogic.CutFill(
                new[] { (lo, Z(eg[0], lo)), (hi, Z(eg[0], hi)) },
                new[] { (lo, piece.A.ElevationM), (hi, piece.B.ElevationM) });
            cut += area.CutArea; fill += area.FillArea;
            if (!Finite(piece.A) || !Finite(piece.B) || !double.IsFinite(cut) || !double.IsFinite(fill))
                return Fail("area-overflow", "Non-finite geometry or area.", envelope);
        }
        return new(input.Key, input.StationM, true, cut, fill, envelope, input.RawReceiptRef) { BotDomainMode = domainMode };
    }

    private static bool Finite(Point p) => double.IsFinite(p.OffsetM) && double.IsFinite(p.ElevationM);
    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;
    private static double Z(Segment s, double x) => s.A.ElevationM +
        (s.B.ElevationM - s.A.ElevationM) * ((x - s.A.OffsetM) / (s.B.OffsetM - s.A.OffsetM));
}
