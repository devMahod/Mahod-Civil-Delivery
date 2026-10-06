using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Closed top dimension chain of a section (SEC-B4, review of 1.3.9, 30/09).
    ///
    /// 1.3.9 printed a width only above NAMED strips. The narrow pieces between them
    /// (curb faces, island noses: 0.31 / 0.50 / 0.70 m at STA-12145) had no value, so
    /// the widths an engineer adds up (22.95) did not reach the section extent
    /// (25.46) — "מידות לא שלמות". Every consecutive run of marks that is not a named
    /// strip now becomes one gap piece with its own width, and the chain carries one
    /// overall width. Pieces are contiguous from the first to the last dimension mark,
    /// so their widths sum to the extent by construction; the builder proves it.
    ///
    /// No name is invented for a gap piece: it carries a width only. Every internal
    /// break inside a gap remains listed in the bottom offset row.
    /// </summary>
    public static class SectionDimensionChainLogic
    {
        public const double OffsetToleranceM = 1e-6;

        public sealed record Piece(double From, double To, bool IsNamedStrip)
        {
            public double Width => To - From;
        }

        public sealed record Chain(IReadOnlyList<Piece> Pieces, double From, double To)
        {
            public double OverallWidth => To - From;
        }

        public static bool TryBuild(
            IReadOnlyList<double> markOffsets,
            IReadOnlyList<(double From, double To)> namedSpans,
            out Chain? chain,
            out string error)
        {
            chain = null;
            error = string.Empty;
            if (markOffsets == null || markOffsets.Count < 2 ||
                markOffsets.Any(offset => !double.IsFinite(offset)))
            {
                error = "a dimension chain needs at least two finite marks";
                return false;
            }
            var marks = markOffsets.OrderBy(offset => offset).ToList();
            for (var i = 1; i < marks.Count; i++)
            {
                if (marks[i] - marks[i - 1] <= OffsetToleranceM)
                {
                    error = "dimension marks are duplicated or unordered";
                    return false;
                }
            }

            var spans = (namedSpans ?? Array.Empty<(double From, double To)>())
                .OrderBy(span => span.From).ToList();
            var startIndexBySpan = new List<(int Start, int End)>();
            foreach (var span in spans)
            {
                var start = IndexOf(marks, span.From);
                var end = IndexOf(marks, span.To);
                if (start < 0 || end < 0 || end <= start)
                {
                    error = FormattableString.Invariant(
                        $"named strip {span.From:F3}..{span.To:F3} does not start and end on dimension marks");
                    return false;
                }
                if (startIndexBySpan.Count > 0 && start < startIndexBySpan[^1].End)
                {
                    error = FormattableString.Invariant(
                        $"named strip {span.From:F3}..{span.To:F3} overlaps another named strip");
                    return false;
                }
                startIndexBySpan.Add((start, end));
            }

            var pieces = new List<Piece>();
            int? gapStart = null;
            var index = 0;
            var spanCursor = 0;
            while (index < marks.Count - 1)
            {
                if (spanCursor < startIndexBySpan.Count && startIndexBySpan[spanCursor].Start == index)
                {
                    if (gapStart is { } open)
                    {
                        pieces.Add(new Piece(marks[open], marks[index], false));
                        gapStart = null;
                    }
                    var named = startIndexBySpan[spanCursor];
                    pieces.Add(new Piece(marks[named.Start], marks[named.End], true));
                    index = named.End;
                    spanCursor++;
                    continue;
                }
                gapStart ??= index;
                index++;
            }
            if (gapStart is { } tail)
                pieces.Add(new Piece(marks[tail], marks[^1], false));

            var sum = pieces.Sum(piece => piece.Width);
            var extent = marks[^1] - marks[0];
            if (spanCursor != startIndexBySpan.Count || Math.Abs(sum - extent) > 1e-6 ||
                pieces.Zip(pieces.Skip(1), (left, right) => Math.Abs(left.To - right.From) <= OffsetToleranceM)
                    .Any(contiguous => !contiguous))
            {
                error = FormattableString.Invariant(
                    $"dimension chain does not close: pieces sum {sum:F6}, extent {extent:F6}");
                return false;
            }
            chain = new Chain(pieces, marks[0], marks[^1]);
            return true;
        }

        private static int IndexOf(IReadOnlyList<double> marks, double offset)
        {
            for (var i = 0; i < marks.Count; i++)
                if (Math.Abs(marks[i] - offset) <= OffsetToleranceM) return i;
            return -1;
        }
    }
}
