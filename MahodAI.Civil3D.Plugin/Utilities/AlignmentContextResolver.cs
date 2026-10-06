using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MahodAI.Civil3D.Plugin.Tools;

namespace MahodAI.Civil3D.Plugin.Utilities
{
    /// <summary>
    /// Per-alignment context: speed-segment lookup + nearest-junction
    /// proximity check. Built once per tool invocation from a
    /// DrawingSummary (engine-side ExtractSummary) so the per-element
    /// annotation costs are negligible.
    ///
    /// Both checks are deliberately tiny — the goal is to feed the
    /// AI planner enough geometric truth to choose the right standards
    /// query (e.g. "junction vertical-DSD radius" vs "open-road crest"),
    /// not to do compliance work plugin-side.
    /// </summary>
    public sealed class AlignmentContextResolver
    {
        /// <summary>
        /// Default proximity buffer (m) for the near-junction check —
        /// matches the engineer-validated 150 m approach-zone window
        /// used in the legacy deterministic DSD checker.
        /// </summary>
        public const double DefaultProximityBufferM = 150.0;

        private readonly List<JunctionRef> _junctions;
        private readonly List<SpeedSegmentRef> _speedSegments;
        private readonly double? _primaryDesignSpeedKph;

        private AlignmentContextResolver(
            List<JunctionRef> junctions,
            List<SpeedSegmentRef> speedSegments,
            double? primaryDesignSpeedKph)
        {
            _junctions = junctions;
            _speedSegments = speedSegments;
            _primaryDesignSpeedKph = primaryDesignSpeedKph;
        }

        /// <summary>
        /// Cache key for the per-document drawing summary. Held in
        /// <see cref="ToolCache"/> so every per-call resolver shares one
        /// extraction — a full extract takes 5–20 s and there are 5 such
        /// calls in an analysis batch; without the cache they exhaust the
        /// 30 s tool timeout in parallel and the analyzer ends up with
        /// empty tool data.
        /// </summary>
        private const string DrawingSummaryCacheKey = "alignment_context:drawing_summary";

        /// <summary>
        /// Build a resolver for one alignment. Reuses a cached drawing
        /// summary from <paramref name="cache"/> when present — only the
        /// first caller in a batch pays the extraction cost. Safe to call
        /// with a null cache (falls back to an unshared extraction).
        /// </summary>
        public static AlignmentContextResolver For(string alignmentName, ToolCache? cache)
        {
            if (string.IsNullOrEmpty(alignmentName))
                return Empty();

            DrawingSummaryForAI? summary = TryGetCachedSummary(cache);
            return summary == null ? Empty() : FromSummary(summary, alignmentName);
        }

        /// <summary>
        /// Convenience overload that runs an unshared extraction. Avoid
        /// in hot paths — prefer the <see cref="ToolCache"/> overload.
        /// </summary>
        public static AlignmentContextResolver For(string alignmentName) =>
            For(alignmentName, cache: null);

        private static DrawingSummaryForAI? TryGetCachedSummary(ToolCache? cache)
        {
            if (cache == null)
            {
                try
                {
                    return new DrawingSummaryExtractor().ExtractSummary();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"AlignmentContextResolver: unshared summary extraction failed: {ex.Message}");
                    return null;
                }
            }

            try
            {
                return cache.GetOrCreate(
                    DrawingSummaryCacheKey,
                    () => new DrawingSummaryExtractor().ExtractSummary(),
                    expiration: TimeSpan.FromMinutes(5));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"AlignmentContextResolver: cached summary extraction failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Build a resolver from an already-extracted summary. Useful when
        /// the caller already has it on hand (avoids the second extraction).
        /// </summary>
        public static AlignmentContextResolver FromSummary(
            DrawingSummaryForAI summary, string alignmentName)
        {
            var junctions = new List<JunctionRef>();
            var speedSegments = new List<SpeedSegmentRef>();
            double? primary = null;

            if (summary?.Intersections != null)
            {
                foreach (var ix in summary.Intersections)
                {
                    if (ix?.Approaches == null) continue;
                    var ap = ix.Approaches.FirstOrDefault(
                        a => string.Equals(a?.AlignmentName, alignmentName,
                            StringComparison.OrdinalIgnoreCase));
                    if (ap == null) continue;
                    var others = (ix.AlignmentNames ?? new List<string>())
                        .Where(n => !string.IsNullOrEmpty(n) &&
                                    !string.Equals(n, alignmentName,
                                        StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    junctions.Add(new JunctionRef
                    {
                        Station = ap.Station,
                        OtherAlignments = others,
                        IntersectionType = ix.IntersectionType ?? "",
                        IntersectionAngleDeg = ix.IntersectionAngle
                    });
                }
            }

            if (summary?.Alignments != null)
            {
                var thisAlign = summary.Alignments.FirstOrDefault(
                    a => string.Equals(a?.Name, alignmentName,
                        StringComparison.OrdinalIgnoreCase));
                if (thisAlign != null)
                {
                    primary = thisAlign.DesignSpeedKph;
                    if (thisAlign.SpeedSegments != null)
                    {
                        foreach (var seg in thisAlign.SpeedSegments)
                        {
                            speedSegments.Add(new SpeedSegmentRef
                            {
                                StartStation = seg.StartStation,
                                EndStation = seg.EndStation,
                                SpeedKph = seg.SpeedKph
                            });
                        }
                        // Defensive sort — extractor already sorts but keep
                        // the resolver self-contained.
                        speedSegments.Sort((a, b) =>
                            a.StartStation.CompareTo(b.StartStation));
                    }
                }
            }

            return new AlignmentContextResolver(junctions, speedSegments, primary);
        }

        public static AlignmentContextResolver Empty() =>
            new(new List<JunctionRef>(), new List<SpeedSegmentRef>(), null);

        /// <summary>
        /// Return the design speed (km/h) that applies at <paramref name="station"/>.
        /// Falls back to the alignment's primary <c>DesignSpeedKph</c> when no
        /// per-segment overrides exist; returns null when neither is known.
        /// </summary>
        public double? ResolveSpeedKph(double station)
        {
            if (_speedSegments.Count == 0)
                return _primaryDesignSpeedKph;

            // Last segment whose start_station <= station wins (matches the
            // legacy Python get_speed_at_station semantics).
            var found = _speedSegments[0];
            foreach (var seg in _speedSegments)
            {
                if (station >= seg.StartStation)
                    found = seg;
                else
                    break;
            }
            return found.SpeedKph;
        }

        /// <summary>
        /// Return the nearest junction to the element's station range, OR
        /// null when no junction sits within the proximity buffer. Buffer
        /// applies symmetrically on either side of the range so the check
        /// catches curves that end *just before* (or start *just after*) a
        /// detected junction — the case the engineer flagged for profile
        /// 73's crest at 540–661 with junction 700.6 (39 m past curve end).
        /// </summary>
        public NearIntersection? NearestJunction(
            double startStation,
            double endStation,
            double bufferM = DefaultProximityBufferM)
        {
            if (_junctions.Count == 0) return null;

            var lo = Math.Min(startStation, endStation);
            var hi = Math.Max(startStation, endStation);

            JunctionRef? best = null;
            double bestDist = double.MaxValue;
            double bestOffset = 0;

            foreach (var j in _junctions)
            {
                // Signed offset: 0 when contained, positive when junction is
                // past the curve end, negative when before the start. The
                // sign matters for the report ("39 m past the curve end" is
                // more meaningful than just "39 m away").
                double offset;
                if (j.Station < lo) offset = j.Station - lo;       // negative
                else if (j.Station > hi) offset = j.Station - hi;  // positive
                else offset = 0.0;

                var dist = Math.Abs(offset);
                if (dist > bufferM) continue;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestOffset = offset;
                    best = j;
                }
            }

            if (best == null) return null;

            return new NearIntersection
            {
                JunctionStation = Math.Round(best.Station, 2),
                OffsetM = Math.Round(bestOffset, 2),
                OtherAlignments = best.OtherAlignments,
                IntersectionType = best.IntersectionType,
                IntersectionAngleDeg = best.IntersectionAngleDeg
            };
        }

        private sealed class JunctionRef
        {
            public double Station;
            public List<string> OtherAlignments = new();
            public string IntersectionType = "";
            public double? IntersectionAngleDeg;
        }

        private sealed class SpeedSegmentRef
        {
            public double StartStation;
            public double EndStation;
            public double SpeedKph;
        }
    }

    /// <summary>
    /// Per-element junction proximity tag attached to profile and
    /// alignment elements. Serialised onto the tool result so the AI
    /// planner can choose junction-aware retrieval queries.
    /// </summary>
    public sealed class NearIntersection
    {
        [JsonPropertyName("junction_station")]
        public double JunctionStation { get; set; }

        /// <summary>
        /// Signed offset from the element's station range to the junction.
        /// 0 — junction lies inside the range.
        /// + — junction is past the element's end station (curve ends, junction follows).
        /// − — junction is before the element's start station (junction first, curve follows).
        /// </summary>
        [JsonPropertyName("offset_m")]
        public double OffsetM { get; set; }

        [JsonPropertyName("other_alignments")]
        public List<string> OtherAlignments { get; set; } = new();

        [JsonPropertyName("intersection_type")]
        public string IntersectionType { get; set; } = "";

        [JsonPropertyName("intersection_angle_deg")]
        public double? IntersectionAngleDeg { get; set; }
    }
}
