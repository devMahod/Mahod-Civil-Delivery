using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free annotation placement math. Civil is responsible only for mapping an
    /// offset/elevation anchor into drawing coordinates; these formulas then place the
    /// protected artwork. APPLY and VERIFY both call this code.
    /// </summary>
    public static class SectionAnnotationPlacementLogic
    {
        public const double DatumBelowGrid = 1.6;
        public const double SlopeAboveGround = 0.7;
        public const double DimensionRightShift = 0.35;
        public const double DimensionBelowGrid = 1.1;
        public const double AxisLabelAboveGrid = 4.6;
        public const double TitleAboveGrid = 6.6;
        public const double RowLabelAboveGrid = 0.6;
        public const double TopTickBelowGrid = 2.0;
        public const double BottomTickBelowGrid = 0.7;
        public const double WidthLabelAboveGrid = 1.3;
        public const double StripLabelAboveGrid = 2.9;
        /// <summary>SEC-B4 overall width; the measured solver stacks it above strip names.</summary>
        public const double OverallWidthAboveGrid = 3.8;
        /// <summary>SEC-m3 legend lines; the measured solver stacks them between axis and title.</summary>
        public const double LegendAboveGrid = 5.6;

        public sealed record Point(double X, double Y);
        public sealed record Bounds(double MinX, double MinY, double MaxX, double MaxY)
        {
            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;
        }
        public sealed record BlockPlacement(
            double PositionX,
            double PositionY,
            double ScaleX,
            double ScaleY,
            double Rotation);
        public sealed record Segment(Point Start, Point End);

        /// <summary>Measured ink bounds at the canonical semantic anchor, not a character-count estimate.</summary>
        public sealed record LabelBox(string Id, int Band, Point Anchor, Bounds Ink);
        public sealed record PlacedLabel(string Id, Point Anchor, Bounds Ink, double Rise);
        public sealed record LabelLayout(IReadOnlyList<PlacedLabel> Labels, Bounds? OverallBounds);
        public sealed record InkBox(string Id, Bounds Ink, bool IsText);
        public sealed record InkConflict(string First, string Second);

        public static IReadOnlyList<InkConflict> FindTextInkConflicts(IReadOnlyList<InkBox> items)
        {
            if (items.Any(item => string.IsNullOrWhiteSpace(item.Id) || !ValidBounds(item.Ink)) ||
                items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
                throw new ArgumentException("Text collision gate requires unique ids and finite measured ink.");
            var conflicts = new List<InkConflict>();
            for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if ((items[i].IsText || items[j].IsText) && BoxesConflict(items[i].Ink, items[j].Ink))
                    conflicts.Add(new InkConflict(items[i].Id, items[j].Id));
            return conflicts;
        }

        public static Point MirrorY(Point p) => new(p.X, -p.Y);
        public static Bounds MirrorY(Bounds b) => new(b.MinX, -b.MaxY, b.MaxX, -b.MinY);

        /// <summary>The same measured-box solver mirrored below the grid; negative Rise means down.</summary>
        public static bool TryLayoutLabelsDown(IReadOnlyList<LabelBox> labels,
            IReadOnlyList<Bounds> obstacles, double clearance, double maximumDrop,
            out LabelLayout? layout, out string error)
        {
            layout = null;
            if (!TryLayoutLabels(labels.Select(l => l with { Anchor = MirrorY(l.Anchor), Ink = MirrorY(l.Ink) }).ToList(),
                    obstacles.Select(MirrorY).ToList(), clearance, maximumDrop, out var mirrored, out error))
                return false;
            layout = new LabelLayout(mirrored!.Labels.Select(l => l with
                { Anchor = MirrorY(l.Anchor), Ink = MirrorY(l.Ink), Rise = -l.Rise }).ToList(),
                mirrored.OverallBounds == null ? null : MirrorY(mirrored.OverallBounds));
            return true;
        }

        public static Bounds Translate(Bounds box, double x, double y) =>
            new(box.MinX + x, box.MinY + y, box.MaxX + x, box.MaxY + y);

        public static bool BoxesConflict(Bounds a, Bounds b, double clearance = 0) =>
            a.MinX < b.MaxX + clearance && b.MinX < a.MaxX + clearance &&
            a.MinY < b.MaxY + clearance && b.MinY < a.MaxY + clearance;

        /// <summary>Two labels sharing a line of text (their ink overlaps vertically) closer than the word gap.</summary>
        public static bool SameRowTooClose(Bounds a, Bounds b, double horizontalGap) =>
            a.MinY < b.MaxY && b.MinY < a.MaxY &&
            a.MinX < b.MaxX + horizontalGap && b.MinX < a.MaxX + horizontalGap;

        public static Bounds? Envelope(IEnumerable<Bounds> boxes)
        {
            var list = boxes.ToList();
            return list.Count == 0 ? null : new Bounds(list.Min(b => b.MinX),
                list.Min(b => b.MinY), list.Max(b => b.MaxX), list.Max(b => b.MaxY));
        }

        /// <summary>Leader stems retain the semantic X. Ink/furniture knock-outs prevent a stem striking through another label.</summary>
        public static IReadOnlyList<Segment> ClearVerticalLeader(Point anchor, double targetY,
            IReadOnlyList<Bounds> ink, double clearance)
        {
            var result = new List<Segment>();
            if (!Finite(anchor) || !double.IsFinite(targetY) || targetY <= anchor.Y ||
                !Positive(clearance)) return result;
            var cursor = anchor.Y;
            foreach (var box in ink.Where(b => anchor.X >= b.MinX - clearance &&
                         anchor.X <= b.MaxX + clearance && b.MaxY + clearance > anchor.Y &&
                         b.MinY - clearance < targetY).OrderBy(b => b.MinY))
            {
                var end = Math.Min(targetY, box.MinY - clearance);
                if (end - cursor > clearance)
                    result.Add(new Segment(new Point(anchor.X, cursor), new Point(anchor.X, end)));
                cursor = Math.Max(cursor, box.MaxY + clearance);
            }
            if (targetY - cursor > clearance)
                result.Add(new Segment(new Point(anchor.X, cursor), new Point(anchor.X, targetY)));
            return result;
        }

        /// <summary>
        /// Keeps every semantic X anchor, font size and measured ink shape. Adjacent
        /// narrow strips share a baseline when possible and use measured-height tiers
        /// otherwise. Bands separate slopes, dimensions, names and headers. Fixed
        /// furniture never moves. Failure is explicit rather than shrinking text.
        /// <paramref name="horizontalGap"/> is the drafting word gap between two labels on one row (Arthur 04.10 on the
        /// STA-42676 print: labels 0.3 text heights apart read as one phrase); null or smaller than
        /// <paramref name="clearance"/> keeps the clearance, so the plain solver is unchanged.
        /// <paramref name="bandGapBelow"/> widens, for the listed bands only, the ink gap between the top of the band
        /// below and the bottom of that band (Codex 04.10 03:45: strip names, overall width and axis read as one block);
        /// a missing band or a value below the clearance keeps the clearance.
        /// </summary>
        public static bool TryLayoutLabels(IReadOnlyList<LabelBox> labels,
            IReadOnlyList<Bounds> obstacles, double clearance, double maximumRise,
            out LabelLayout? layout, out string error, double? horizontalGap = null,
            IReadOnlyDictionary<int, double>? bandGapBelow = null)
        {
            layout = null;
            error = string.Empty;
            if (horizontalGap.HasValue && !double.IsFinite(horizontalGap.Value) ||
                bandGapBelow != null && bandGapBelow.Values.Any(g => !double.IsFinite(g)))
            {
                error = "Annotation layout requires a finite horizontal label gap and finite band gaps.";
                return false;
            }
            var rowGap = Math.Max(clearance, horizontalGap ?? clearance);
            if (!Positive(clearance) || !Positive(maximumRise) || labels.Count > 256 ||
                labels.Any(l => string.IsNullOrWhiteSpace(l.Id) || l.Band < 0 ||
                    !Finite(l.Anchor) || !ValidBounds(l.Ink)) ||
                labels.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != labels.Count ||
                obstacles.Any(b => !ValidBounds(b)))
            {
                error = "Annotation layout requires unique labels and finite positive measured bounds/clearance.";
                return false;
            }
            var placed = new List<PlacedLabel>();
            var occupied = obstacles.ToList();
            double? previousBandTop = null;
            foreach (var band in labels.GroupBy(l => l.Band).OrderBy(g => g.Key))
            {
                var members = band.ToList();
                // DBText alignment points need not be at the bottom of their ink.
                // A common anchor baseline and pitch preserve those measured offsets.
                var below = members.Min(l => l.Ink.MinY - l.Anchor.Y);
                var above = members.Max(l => l.Ink.MaxY - l.Anchor.Y);
                // Positive separation guard, matching the existing obstacle jump.
                // It never relaxes BoxesConflict; it prevents representable sums
                // from landing a fraction of an ULP inside the required gap.
                var pitch = above - below + clearance + 1e-8;
                if (!Positive(pitch))
                {
                    error = "Measured band pitch is invalid.";
                    return false;
                }

                // Interval partitioning by the left ink edge uses the minimum
                // number of rows under the supplied horizontal word gap.
                var rowEnds = new List<double>();
                var rowById = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var label in members.OrderBy(l => l.Ink.MinX)
                             .ThenBy(l => l.Ink.MaxX).ThenBy(l => l.Id, StringComparer.Ordinal))
                {
                    var row = rowEnds.FindIndex(end => end + rowGap <= label.Ink.MinX);
                    if (row < 0)
                    {
                        row = rowEnds.Count;
                        rowEnds.Add(label.Ink.MaxX);
                    }
                    else rowEnds[row] = label.Ink.MaxX;
                    rowById.Add(label.Id, row);
                }

                // Never move a label down. Respect semantic band ordering without
                // treating a remote obstacle as a global top boundary.
                var baseline = members.Max(l => l.Anchor.Y - rowById[l.Id] * pitch);
                var bandGap = bandGapBelow != null && bandGapBelow.TryGetValue(band.Key, out var wider)
                    ? Math.Max(clearance, wider) : clearance;
                if (previousBandTop.HasValue)
                    baseline = Math.Max(baseline, previousBandTop.Value + bandGap + 1e-8 -
                        members.Min(l => rowById[l.Id] * pitch + l.Ink.MinY - l.Anchor.Y));

                // Raising the entire band preserves every common baseline/pitch.
                // Each jump exits at least one finite label/obstacle forbidden
                // interval; monotonic motion cannot enter that interval again.
                var maximumJumps = (long)members.Count * occupied.Count;
                for (long attempt = 0; attempt <= maximumJumps; attempt++)
                {
                    var nextBaseline = baseline;
                    foreach (var label in members)
                    {
                        var relativeBottom = rowById[label.Id] * pitch + label.Ink.MinY - label.Anchor.Y;
                        var rise = baseline + rowById[label.Id] * pitch - label.Anchor.Y;
                        var box = Translate(label.Ink, 0, rise);
                        foreach (var obstacle in occupied)
                            if (BoxesConflict(box, obstacle, clearance))
                                nextBaseline = Math.Max(nextBaseline,
                                    obstacle.MaxY + clearance + 1e-8 - relativeBottom);
                    }
                    if (nextBaseline == baseline) break;
                    baseline = nextBaseline;
                }

                var bandPlaced = new List<PlacedLabel>();
                foreach (var label in members.OrderBy(l => l.Anchor.X).ThenBy(l => l.Id, StringComparer.Ordinal))
                {
                    var rise = baseline + rowById[label.Id] * pitch - label.Anchor.Y;
                    var finalBox = Translate(label.Ink, 0, rise);
                    if (!double.IsFinite(rise) || rise < 0 || rise > maximumRise ||
                        !ValidBounds(finalBox) ||
                        occupied.Any(b => BoxesConflict(finalBox, b, clearance)) ||
                        bandPlaced.Any(p => BoxesConflict(finalBox, p.Ink, clearance)) ||
                        bandPlaced.Any(p => SameRowTooClose(finalBox, p.Ink, rowGap)))
                    {
                        error = $"Measured label '{label.Id}' cannot be placed within the {maximumRise:R} drawing-unit height budget.";
                        return false;
                    }
                    bandPlaced.Add(new PlacedLabel(label.Id,
                        new Point(label.Anchor.X, label.Anchor.Y + rise), finalBox, rise));
                }
                placed.AddRange(bandPlaced);
                occupied.AddRange(bandPlaced.Select(l => l.Ink));
                previousBandTop = bandPlaced.Max(l => l.Ink.MaxY);
            }
            layout = new LabelLayout(placed, Envelope(occupied));
            return true;
        }

        /// <summary>
        /// The measured solver plus one drafting rule for the slope band (band 0; Codex 03.10 14:40 on the STA-42676
        /// 1:200 print): no tool line — the axis, a dimension tick or a ROW line — may strike a slope label, and no slope
        /// label may straddle the frame top. A label the plain solver leaves crossing one (a narrow strip whose label
        /// overflows into a second row, or a label the axis passes through) is laid out again in its own band above the
        /// frame top, between the slopes and the widths; its leader keeps the semantic X. Every other slope label keeps
        /// the solver's position for the remaining slopes, and when no slope label crosses a line the result is exactly
        /// the plain solver's.
        /// </summary>
        public static bool TryLayoutLabelsClearOfLines(IReadOnlyList<LabelBox> labels,
            IReadOnlyList<Bounds> obstacles, IReadOnlyList<Segment> lines, double frameTopY,
            double clearance, double maximumRise, out LabelLayout? layout,
            out IReadOnlyList<string> raisedAboveFrame, out string error, double? horizontalGap = null,
            IReadOnlyDictionary<int, double>? bandGapBelow = null)
        {
            layout = null;
            raisedAboveFrame = Array.Empty<string>();
            if (!double.IsFinite(frameTopY) || lines.Any(s => !Finite(s.Start) || !Finite(s.End)))
            {
                error = "Line-clear annotation layout requires finite lines and a finite frame top.";
                return false;
            }
            if (!TryLayoutLabels(labels, obstacles, clearance, maximumRise, out var plain, out error, horizontalGap,
                    bandGapBelow))
                return false;
            var slopes = labels.Where(l => l.Band == 0).ToList();
            var raised = new SortedSet<string>(Crossing(plain!.Labels), StringComparer.Ordinal);
            if (raised.Count == 0)
            {
                layout = plain;
                return true;
            }

            // Fixed point: a slope label left below may only move by losing a neighbour, so at most one pass per label.
            LabelLayout? kept = null;
            for (var pass = 0; pass <= slopes.Count; pass++)
            {
                var remaining = slopes.Where(l => !raised.Contains(l.Id)).ToList();
                kept = null;
                if (remaining.Count > 0 &&
                    !TryLayoutLabels(remaining, obstacles, clearance, maximumRise, out kept, out error, horizontalGap))
                    return false;
                var more = kept == null ? new List<string>() : Crossing(kept.Labels).ToList();
                if (more.Count == 0) break;
                foreach (var id in more) raised.Add(id);
            }

            var occupied = obstacles.ToList();
            var placed = new List<PlacedLabel>();
            if (kept != null)
            {
                placed.AddRange(kept.Labels);
                occupied.AddRange(kept.Labels.Select(l => l.Ink));
            }
            // Below the frame top, inside each raised label's own width, nothing may hold it: the solver lifts it clear.
            occupied.AddRange(slopes.Where(l => raised.Contains(l.Id)).Select(l =>
                new Bounds(l.Ink.MinX, Math.Min(l.Ink.MinY, frameTopY) - 1e-3, l.Ink.MaxX, frameTopY + 1e-3)));
            var upper = slopes.Where(l => raised.Contains(l.Id)).Select(l => l with { Band = 0 })
                .Concat(labels.Where(l => l.Band > 0).Select(l => l with { Band = l.Band + 1 })).ToList();
            // The raised slopes become band 0 of the upper layout, so every other band (and its gap) moves up by one.
            if (!TryLayoutLabels(upper, occupied, clearance, maximumRise, out var above, out error, horizontalGap,
                    bandGapBelow?.ToDictionary(g => g.Key + 1, g => g.Value)))
                return false;
            placed.AddRange(above!.Labels);
            if (Crossing(placed).Any())
            {
                error = "A slope label is still struck by a line or straddles the frame after the line-clear layout.";
                return false;
            }
            layout = new LabelLayout(placed, above.OverallBounds);
            raisedAboveFrame = raised.ToList();
            return true;

            IEnumerable<string> Crossing(IEnumerable<PlacedLabel> candidates) => candidates
                .Where(p => slopes.Any(s => s.Id == p.Id) &&
                    (p.Ink.MinY < frameTopY && p.Ink.MaxY > frameTopY || lines.Any(s => Strikes(s, p.Ink))))
                .Select(p => p.Id);
        }

        /// <summary>True when the segment passes through the interior of the box (Liang–Barsky clipping).</summary>
        public static bool Strikes(Segment line, Bounds box)
        {
            double t0 = 0, t1 = 1;
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;
            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q > 0;
                var r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
                return true;
            }
            return Clip(-dx, line.Start.X - box.MinX) && Clip(dx, box.MaxX - line.Start.X) &&
                   Clip(-dy, line.Start.Y - box.MinY) && Clip(dy, box.MaxY - line.Start.Y) && t1 - t0 > 1e-12;
        }

        public static Point DatumPosition(Point gridCorner) =>
            new(gridCorner.X, gridCorner.Y - DatumBelowGrid);

        public static Point SlopePosition(Point mappedGroundMidpoint) =>
            new(mappedGroundMidpoint.X, mappedGroundMidpoint.Y + SlopeAboveGround);

        public static Point DimensionPosition(Point mappedGridBottom) =>
            new(mappedGridBottom.X + DimensionRightShift,
                mappedGridBottom.Y - DimensionBelowGrid);

        public static Segment AxisLine(Point mappedBottom, Point mappedTop) =>
            new(mappedBottom, mappedTop);

        public static Point AxisLabelPosition(Point mappedTop) =>
            new(mappedTop.X, mappedTop.Y + AxisLabelAboveGrid);

        public static Point TitlePosition(Point mappedTopAtCenter) =>
            new(mappedTopAtCenter.X, mappedTopAtCenter.Y + TitleAboveGrid);

        public static Segment RowLine(Point mappedBottom, Point mappedTop) =>
            new(mappedBottom, mappedTop);

        public static Point RowLabelPosition(Point mappedTop) =>
            new(mappedTop.X, mappedTop.Y + RowLabelAboveGrid);

        public static Segment TopTick(Point mappedTop) =>
            new(new Point(mappedTop.X, mappedTop.Y - TopTickBelowGrid), mappedTop);

        public static Segment BottomTick(Point mappedBottom) =>
            new(new Point(mappedBottom.X, mappedBottom.Y - BottomTickBelowGrid), mappedBottom);

        public static Point WidthLabelPosition(Point mappedTopAtMidpoint) =>
            new(mappedTopAtMidpoint.X, mappedTopAtMidpoint.Y + WidthLabelAboveGrid);

        public static Point StripLabelPosition(Point mappedTopAtMidpoint) =>
            new(mappedTopAtMidpoint.X, mappedTopAtMidpoint.Y + StripLabelAboveGrid);

        public static Point OverallWidthLabelPosition(Point mappedTopAtChainMidpoint) =>
            new(mappedTopAtChainMidpoint.X, mappedTopAtChainMidpoint.Y + OverallWidthAboveGrid);

        public static Point LegendPosition(Point mappedTopAtCenter) =>
            new(mappedTopAtCenter.X, mappedTopAtCenter.Y + LegendAboveGrid);

        public static bool PresentationColorMatches(short expected, short? actual) =>
            expected >= 1 && expected <= 255 && actual == expected;

        public static bool TryCarPlacement(
            Bounds source,
            double physicalWidth,
            Point left,
            Point right,
            Point bottom,
            Point top,
            out BlockPlacement? placement,
            out string error)
        {
            placement = null;
            error = string.Empty;
            if (!ValidBounds(source) || !Positive(physicalWidth) ||
                !Finite(left) || !Finite(right) || !Finite(bottom) || !Finite(top))
            {
                error = "vehicle placement inputs are non-finite/empty";
                return false;
            }
            var targetWidth = Math.Abs(right.X - left.X);
            var targetHeight = Math.Abs(top.Y - bottom.Y);
            if (!Positive(targetWidth) || !Positive(targetHeight))
            {
                error = "mapped vehicle envelope is empty";
                return false;
            }
            if (Math.Abs(right.Y - left.Y) > Math.Max(0.01, targetWidth * 0.01) ||
                Math.Abs(top.X - bottom.X) > Math.Max(0.01, targetHeight * 0.01))
            {
                error = "mapped vehicle axes are not orthogonal";
                return false;
            }
            var scaleX = targetWidth / source.Width;
            var scaleY = targetHeight / source.Height;
            if (!Positive(scaleX) || !Positive(scaleY))
            {
                error = "vehicle scale is invalid";
                return false;
            }
            var targetCenterX = (left.X + right.X) / 2.0;
            var targetBottomY = Math.Min(bottom.Y, top.Y);
            var sourceCenterX = (source.MinX + source.MaxX) / 2.0;
            placement = new BlockPlacement(
                targetCenterX - sourceCenterX * scaleX,
                targetBottomY - source.MinY * scaleY,
                scaleX,
                scaleY,
                0.0);
            return true;
        }

        public static bool TryArrowPlacement(
            Bounds source,
            Point bottom,
            Point top,
            bool pointsUp,
            out BlockPlacement? placement,
            out string error)
        {
            placement = null;
            error = string.Empty;
            if (!ValidBounds(source) || !Finite(bottom) || !Finite(top) ||
                source.Height / source.Width < 2.0 ||
                source.Height / source.Width > 4.0)
            {
                error = "traffic-arrow source/mapping envelope is invalid";
                return false;
            }
            var targetHeight = top.Y - bottom.Y;
            if (!Positive(targetHeight) ||
                Math.Abs(top.X - bottom.X) > Math.Max(0.01, targetHeight * 0.01))
            {
                error = "mapped arrow elevation axis is invalid";
                return false;
            }
            var scale = targetHeight / source.Height;
            if (!Positive(scale))
            {
                error = "traffic-arrow scale is invalid";
                return false;
            }
            var centerX = (bottom.X + top.X) / 2.0;
            var sourceCenterX = (source.MinX + source.MaxX) / 2.0;
            placement = pointsUp
                ? new BlockPlacement(
                    centerX - sourceCenterX * scale,
                    bottom.Y - source.MinY * scale,
                    scale, scale, 0.0)
                : new BlockPlacement(
                    centerX + sourceCenterX * scale,
                    top.Y + source.MinY * scale,
                    scale, scale, Math.PI);
            return true;
        }

        /// <summary>
        /// AutoCAD persists text and block rotation normalized to [0, 2π): a bottom
        /// offset label written with -π/2 read back 4.71238898038469 (offline DWG
        /// read after the first complete live VERIFY, 07/09 18:20). Angles are
        /// compared on the circle, never as raw doubles.
        /// </summary>
        public static bool RotationsEquivalent(double actual, double expected, double tolerance)
        {
            if (!Finite(actual) || !Finite(expected) || !Finite(tolerance) || tolerance < 0)
                return false;
            var delta = Math.IEEERemainder(actual - expected, 2.0 * Math.PI);
            return Math.Abs(delta) <= tolerance;
        }

        /// <summary>
        /// Evidence strings carry lane offsets rounded to millimetres ("offset=-9.779")
        /// while APPLY placed the furniture at the exact PLAN lane midpoint
        /// (-9.779147498985779). A recomputed expectation therefore starts from the one
        /// PLAN offset within <paramref name="tolerance"/> of the evidence value; an
        /// ambiguous or missing PLAN row yields null and the caller fails closed.
        /// </summary>
        public static double? ExactPlannedOffset(
            IEnumerable<double> plannedOffsets, double evidenceOffset, double tolerance = 0.0005)
        {
            if (plannedOffsets == null || !Finite(evidenceOffset) || !Finite(tolerance) || tolerance < 0)
                return null;
            var matches = plannedOffsets
                .Where(offset => Finite(offset) && Math.Abs(offset - evidenceOffset) <= tolerance)
                .Distinct()
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool ValidBounds(Bounds value) =>
            Finite(value.MinX) && Finite(value.MinY) &&
            Finite(value.MaxX) && Finite(value.MaxY) &&
            Positive(value.Width) && Positive(value.Height);

        private static bool Finite(Point value) => Finite(value.X) && Finite(value.Y);
        private static bool Finite(double value) => double.IsFinite(value);
        private static bool Positive(double value) => Finite(value) && value > 1e-9;
    }
}
