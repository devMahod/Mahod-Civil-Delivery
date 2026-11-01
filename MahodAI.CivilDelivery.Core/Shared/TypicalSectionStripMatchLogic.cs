using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Names the width segments of one firm section block from the plan's strips.
///
/// The firm block (FirmSectionFrameLogic) says how wide every piece of its top chain is
/// and where it sits; the plan (SectionProjectionLogic.AnalyzePresentationCoverage) says
/// which strip lies at which signed offset from the same centreline. A piece gets a name
/// only when exactly one plan strip sits where it sits and is as wide as it is: the
/// overlap must cover at least 80 % of the segment and the widths must agree within
/// max(0.15 m, 5 % of the segment). Anything less stays unnamed with a reason, because a
/// confident wrong name above a lane is worse than a gap the engineer fills by hand:
/// two different names that both fit are a conflict, no strip at all is no-plan-strip.
/// Several plan spans with the same name (one strip split by a drawing mark) count as one.
///
/// Pure and deterministic. Spans and segments both carry signed metres from the
/// centreline, left negative; nothing here assumes a side.
/// </summary>
public static class TypicalSectionStripMatchLogic
{
    /// <summary>Share of the segment the plan strip must cover.</summary>
    public const double MinimumOverlapFraction = 0.80;

    /// <summary>Width agreement floor, metres: the drafting noise of a curb face.</summary>
    public const double MinimumWidthToleranceM = 0.15;

    /// <summary>Width agreement above the floor, relative to the segment width.</summary>
    public const double RelativeWidthTolerance = 0.05;

    /// <summary>Reasons an unmatched segment carries. Plain strings so the panel compares them as data.</summary>
    public static class Reason
    {
        /// <summary>Candidates with different names both fit the segment.</summary>
        public const string Conflict = "conflict";

        /// <summary>No named plan strip covers the segment at its width.</summary>
        public const string NoPlanStrip = "no-plan-strip";

        /// <summary>The segment itself is unusable (non-finite or non-positive width).</summary>
        public const string InvalidSegment = "invalid-segment";
    }

    /// <summary>One decision per segment, by its index in the input list.</summary>
    public sealed record StripMatch(int SegmentIndex, string? Label, string? Reason)
    {
        public bool IsMatched => Label != null;
    }

    public sealed record StripMatchResult(IReadOnlyList<StripMatch> Matches)
    {
        public int MatchedCount => Matches.Count(match => match.IsMatched);

        public int UnmatchedCount => Matches.Count - MatchedCount;
    }

    public static StripMatchResult Match(
        IReadOnlyList<FirmSectionFrameLogic.ChainSegment> segments,
        IReadOnlyList<(double FromOffsetM, double ToOffsetM, string Label)> planSpans)
    {
        if (segments == null) throw new ArgumentNullException(nameof(segments));
        var spans = (planSpans ?? Array.Empty<(double FromOffsetM, double ToOffsetM, string Label)>())
            .Where(span => double.IsFinite(span.FromOffsetM) && double.IsFinite(span.ToOffsetM) &&
                           !string.IsNullOrWhiteSpace(span.Label))
            .Select(span => (
                From: Math.Min(span.FromOffsetM, span.ToOffsetM),
                To: Math.Max(span.FromOffsetM, span.ToOffsetM),
                Label: span.Label.Trim()))
            .ToList();

        var matches = new List<StripMatch>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var from = Math.Min(segment.FromOffsetM, segment.ToOffsetM);
            var to = Math.Max(segment.FromOffsetM, segment.ToOffsetM);
            var length = to - from;
            var width = segment.WidthM;
            if (!double.IsFinite(from) || !double.IsFinite(to) || length <= 0 ||
                !double.IsFinite(width) || width <= 0)
            {
                matches.Add(new StripMatch(index, null, Reason.InvalidSegment));
                continue;
            }

            var widthTolerance = Math.Max(MinimumWidthToleranceM, RelativeWidthTolerance * width);
            var labels = spans
                .Where(span =>
                    Math.Min(to, span.To) - Math.Max(from, span.From) >= MinimumOverlapFraction * length &&
                    Math.Abs((span.To - span.From) - width) <= widthTolerance)
                .Select(span => span.Label)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            matches.Add(labels.Count switch
            {
                1 => new StripMatch(index, labels[0], null),
                0 => new StripMatch(index, null, Reason.NoPlanStrip),
                _ => new StripMatch(index, null, Reason.Conflict),
            });
        }
        return new StripMatchResult(matches);
    }
}
