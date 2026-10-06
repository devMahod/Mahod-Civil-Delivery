using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Services.Overlay
{
    /// <summary>
    /// Correlates on-drawing <see cref="ProblemMarker"/>s (parsed from the analysis
    /// report: alignment + station + problem text) with the server's fix plan items
    /// (<see cref="FixPlanItem"/>: object_name + tool_params.target_station + description).
    ///
    /// Both sides derive from the SAME violation snapshot, so their stations are
    /// effectively identical (the analyze pipeline writes the location text the report
    /// renders, and s1_propose parses <c>target_station</c> back out of that same text).
    /// That lets us match on (alignment, station) with a tight tolerance, then fall back
    /// to station-only (rescues profile fixes whose object_name is the profile, not the
    /// hosting alignment) and finally to problem/description text overlap.
    ///
    /// Matching is 1:1 and conservative: a marker is only ever tagged when a confident
    /// match exists, so the worst case is "a fixed violation's pin didn't turn green",
    /// never "the wrong pin turned green".
    /// </summary>
    public static class MarkerFixMatcher
    {
        // Stations on both sides come from the same K+SSS text, so a real match is
        // sub-metre. Keep the window small enough that two distinct curves on one
        // alignment never collide.
        private const double StationTolerance = 12.0;

        /// <summary>
        /// Clears any prior fix tagging on <paramref name="markers"/>, then assigns each
        /// fix plan item to its best-matching marker (setting <see cref="ProblemMarker.FixItemId"/>
        /// and <see cref="MarkerFixState.Fixable"/>). Returns the number of items matched.
        ///
        /// Three confidence passes over ALL items (not item-by-item), so a high-confidence
        /// same-alignment match is never stolen by a vaguer station/text match on another item:
        ///   1. same alignment + nearest station within tolerance;
        ///   2. station within tolerance, but ONLY when the in-tolerance markers are all on a
        ///      single alignment (distinct alignments share 0+000 stationing, so a bare
        ///      cross-alignment station match could green the wrong pin — refuse rather than guess);
        ///   3. station-less items only: same alignment, single candidate or best text overlap.
        /// </summary>
        public static int AssignFixItems(
            IReadOnlyList<ProblemMarker> markers,
            IReadOnlyList<FixPlanItem>? items)
        {
            foreach (var m in markers)
            {
                m.FixItemId = null;
                m.FixState = MarkerFixState.None;
            }
            if (items == null || items.Count == 0 || markers.Count == 0)
                return 0;

            var used = new HashSet<ProblemMarker>();
            var matchedItems = new HashSet<FixPlanItem>();
            int matched = 0;

            void Assign(FixPlanItem item, ProblemMarker? m)
            {
                if (m == null) return;
                m.FixItemId = item.Id;
                m.FixState = MarkerFixState.Fixable;
                used.Add(m);
                matchedItems.Add(item);
                matched++;
            }

            // Pass 1 — same alignment + station inside the curve span (highest confidence).
            foreach (var item in items)
            {
                var st = GetTargetStation(item);
                if (st == null) continue;
                Assign(item, BestSameAlignStation(markers, used, item.ObjectName, st.Value, item.Description));
            }

            // Pass 2 — unambiguous station-only match (single hosting alignment). Rescues
            // profile/corridor fixes whose object_name is not the pinned alignment, without
            // ever binding across two alignments that share the same station.
            foreach (var item in items)
            {
                if (matchedItems.Contains(item)) continue;
                var st = GetTargetStation(item);
                if (st == null) continue;
                Assign(item, BestUnambiguousStation(markers, used, st.Value));
            }

            // Pass 3 — station-less items only: same alignment, single or best text overlap.
            // A station-bearing item that reached here had NO marker within tolerance, so it
            // has no confident match — never fall back to a distance-blind heuristic for it.
            foreach (var item in items)
            {
                if (matchedItems.Contains(item)) continue;
                if (GetTargetStation(item) != null) continue;
                Assign(item, BestSameAlignText(markers, used, item.ObjectName, item.Description));
            }

            return matched;
        }

        /// <summary>
        /// Best free marker on the item's alignment whose curve SPAN contains (or is within
        /// tolerance of) the target station. Distance is measured to the marker's
        /// [start, end] span, not its midpoint — the fix's target_station can be the curve
        /// start / a PI / the end, all of which are far from the midpoint on a long curve
        /// (the "only a few pins went green" bug). When several markers tie on station
        /// (a radius row and a missing-spiral row share one curve), the item's description
        /// text breaks the tie so the right row is coloured.
        /// </summary>
        private static ProblemMarker? BestSameAlignStation(
            IReadOnlyList<ProblemMarker> markers, HashSet<ProblemMarker> used,
            string objectName, double station, string description)
        {
            string align = Normalize(objectName);
            ProblemMarker? best = null;
            double bestDist = double.MaxValue;
            double bestOverlap = -1.0;
            foreach (var m in markers)
            {
                if (used.Contains(m) || Normalize(m.Alignment) != align) continue;
                double d = SpanDistance(m, station);
                if (d > StationTolerance) continue;
                double overlap = TextOverlap(m.Problem, description);
                // Nearer span wins; on a tie, higher problem/description overlap wins.
                if (d < bestDist - 0.001 ||
                    (Math.Abs(d - bestDist) <= 0.001 && overlap > bestOverlap))
                {
                    bestDist = d;
                    bestOverlap = overlap;
                    best = m;
                }
            }
            return best;
        }

        /// <summary>Distance from a station to a marker's [start, end] span (0 when inside).</summary>
        private static double SpanDistance(ProblemMarker m, double station)
        {
            double lo = m.StationEnd.HasValue ? Math.Min(m.StationStart, m.StationEnd.Value) : m.StationStart;
            double hi = m.StationEnd.HasValue ? Math.Max(m.StationStart, m.StationEnd.Value) : m.StationStart;
            if (station < lo) return lo - station;
            if (station > hi) return station - hi;
            return 0.0;
        }

        /// <summary>
        /// Closest free marker within the station tolerance — but ONLY when every in-tolerance
        /// marker sits on the same alignment. If the window spans two alignments the match is
        /// ambiguous (each alignment is stationed from 0+000) and we return null rather than
        /// risk tagging an unrelated alignment's pin.
        /// </summary>
        private static ProblemMarker? BestUnambiguousStation(
            IReadOnlyList<ProblemMarker> markers, HashSet<ProblemMarker> used, double station)
        {
            var inTol = markers
                .Where(m => !used.Contains(m) && SpanDistance(m, station) <= StationTolerance)
                .ToList();
            if (inTol.Count == 0) return null;
            if (inTol.Select(m => Normalize(m.Alignment)).Distinct().Count() != 1) return null;
            return inTol.OrderBy(m => SpanDistance(m, station)).First();
        }

        /// <summary>
        /// Same-alignment match for a station-less item: the lone marker on that alignment, or
        /// (when several) the one with the best problem/description text overlap.
        /// </summary>
        private static ProblemMarker? BestSameAlignText(
            IReadOnlyList<ProblemMarker> markers, HashSet<ProblemMarker> used,
            string objectName, string description)
        {
            string align = Normalize(objectName);
            var sameAlign = markers.Where(m => !used.Contains(m) && Normalize(m.Alignment) == align).ToList();
            if (sameAlign.Count == 0) return null;
            if (sameAlign.Count == 1) return sameAlign[0];

            ProblemMarker? best = null;
            double bestScore = 0.0;
            foreach (var m in sameAlign)
            {
                double score = TextOverlap(m.Problem, description);
                if (score > bestScore) { bestScore = score; best = m; }
            }
            return bestScore > 0.0 ? best : null;
        }

        /// <summary>Reads <c>tool_params.target_station</c> (meters) if present and numeric.</summary>
        private static double? GetTargetStation(FixPlanItem item)
        {
            if (item.ToolParams is not JsonElement p || p.ValueKind != JsonValueKind.Object)
                return null;
            if (p.TryGetProperty("target_station", out var ts) && ts.ValueKind == JsonValueKind.Number)
                return ts.GetDouble();
            return null;
        }

        private static string Normalize(string? s) =>
            (s ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>Jaccard overlap of the word sets of two strings (0..1).</summary>
        private static double TextOverlap(string? a, string? b)
        {
            var sa = Tokenize(a);
            var sb = Tokenize(b);
            if (sa.Count == 0 || sb.Count == 0)
                return 0.0;
            int inter = sa.Count(t => sb.Contains(t));
            int union = sa.Count + sb.Count - inter;
            return union == 0 ? 0.0 : (double)inter / union;
        }

        private static HashSet<string> Tokenize(string? s)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(s))
                return set;
            foreach (var raw in s.Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '[', ']',
                        '/', '\\', '-', '—', '–', '"', '\'', '•', '|' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim().ToLowerInvariant();
                if (t.Length >= 2)          // drop single-char noise
                    set.Add(t);
            }
            return set;
        }
    }
}
