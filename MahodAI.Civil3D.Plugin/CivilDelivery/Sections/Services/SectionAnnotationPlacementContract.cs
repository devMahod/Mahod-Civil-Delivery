using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// One deterministic placement boundary shared by APPLY and VERIFY.  It reads the
    /// exact PLAN-selected EG/FG children and maps semantic offset/elevation anchors
    /// through the live SectionView.  Callers must not recreate these formulas.
    /// </summary>
    internal static class SectionAnnotationPlacementContract
    {
        internal sealed record LinePlacement(Point3d Start, Point3d End);
        internal sealed record SurfaceChains(
            IReadOnlyList<(double Offset, double Elevation)> Existing,
            IReadOnlyList<(double Offset, double Elevation)> Design);

        internal sealed record MeasuredLabelLayout(
            IReadOnlyDictionary<string, Point3d> ExpectedPositionsByHandle,
            IReadOnlyDictionary<string, SectionAnnotationPlacementLogic.Bounds> FinalBoundsByHandle,
            SectionAnnotationPlacementLogic.Bounds OverallBounds,
            IReadOnlyList<LinePlacement> Leaders,
            double Clearance);

        /// <summary>
        /// Both APPLY and VERIFY rebuild anchors from PLAN/live sampled FG. Only ink
        /// shape (relative to alignment point) is read from a native text entity. A
        /// moved entity therefore cannot redefine its own expected position.
        /// </summary>
        internal static MeasuredLabelLayout ComputeLabelLayout(
            CivilDb.SectionView view, SectionPlanRecord record, SectionApplyRecordResult applied,
            SurfaceChains surfaces, IReadOnlyList<Entity> registeredEntities) =>
            ComputeLabelLayout(view, record, applied, surfaces, registeredEntities, observeInputs: null);

        // Explicit diagnostic seam. The existing five-argument entry point stays intact.
        // The observer receives detached values, never live Entity/Transaction objects.
        internal static MeasuredLabelLayout ComputeLabelLayout(
            CivilDb.SectionView view, SectionPlanRecord record, SectionApplyRecordResult applied,
            SurfaceChains surfaces, IReadOnlyList<Entity> registeredEntities,
            Action<SectionMeasuredLayoutInputs>? observeInputs)
        {
            var entities = registeredEntities.ToDictionary(e => e.Handle.ToString(),
                StringComparer.OrdinalIgnoreCase);
            var labels = new List<SectionAnnotationPlacementLogic.LabelBox>();
            SectionAnnotationPlacementLogic.LabelBox Measure(string handle, int band, Point3d? canonical)
            {
                if (canonical is not { } anchor || !entities.TryGetValue(handle, out var entity) ||
                    entity is not DBText text || text.IsErased)
                    throw new InvalidOperationException($"Measured layout text/anchor '{handle}' is unreadable.");
                var actualAnchor = TextAnchor(text);
                var bounds = ReadInkBounds(text);
                return new SectionAnnotationPlacementLogic.LabelBox(handle, band, ToPoint(anchor),
                    SectionAnnotationPlacementLogic.Translate(bounds,
                        anchor.X - actualAnchor.X, anchor.Y - actualAnchor.Y));
            }
            void Add(string handle, int band, Point3d? canonical) => labels.Add(Measure(handle, band, canonical));
            foreach (var expected in SectionCorePresentationContract.ExpectedFor(record, record.PresentationCoverage))
            {
                // ROW text is vertical and belongs to its exact boundary. Keep it a
                // fixed measured obstacle, not a global-height band that lifts the
                // entire title by the height of a remote boundary label.
                if (expected.Text == null || expected.Kind == SectionCorePresentationContract.RowLabel) continue;
                var evidence = applied.CorePresentationAnnotations.Where(e =>
                    e.Kind == expected.Kind && e.SemanticKey == expected.SemanticKey).ToList();
                if (evidence.Count != 1)
                    throw new InvalidOperationException("Measured layout requires exact PLAN-bound core text handles.");
                // Bands stack upward: slopes, widths (named strips and gap pieces share
                // one row), strip names, overall width, axis, legend, title on top.
                var (band, anchor) = expected.Kind switch
                {
                    SectionCorePresentationContract.WidthLabel => (1, WidthLabelPosition(view,
                        expected.From!.Value, expected.To!.Value, view.ElevationMax)),
                    SectionCorePresentationContract.GapWidthLabel => (1, WidthLabelPosition(view,
                        expected.From!.Value, expected.To!.Value, view.ElevationMax)),
                    SectionCorePresentationContract.StripLabel => (2, StripLabelPosition(view,
                        expected.From!.Value, expected.To!.Value, view.ElevationMax)),
                    SectionCorePresentationContract.OverallWidthLabel => (3, OverallWidthLabelPosition(view,
                        expected.From!.Value, expected.To!.Value, view.ElevationMax)),
                    SectionCorePresentationContract.AxisLabel => (4, AxisLabelPosition(view, view.ElevationMax)),
                    SectionCorePresentationContract.LegendUtilities => (5, LegendPosition(view,
                        view.OffsetLeft, view.OffsetRight, view.ElevationMax)),
                    SectionCorePresentationContract.LegendSurfaces => (6, LegendPosition(view,
                        view.OffsetLeft, view.OffsetRight, view.ElevationMax)),
                    SectionCorePresentationContract.Title => (7, TitlePosition(view,
                        view.OffsetLeft, view.OffsetRight, view.ElevationMax)),
                    _ => throw new InvalidOperationException("Unknown measured core text kind: " + expected.Kind),
                };
                Add(evidence[0].Handle, band, anchor);
            }
            foreach (var encoded in applied.SlopeLabels)
            {
                SectionFurnitureLogic.SlopeEvidence? live = null;
                var parsed = SectionAnnotationContractLogic.TryParseSlopeReference(encoded, out var slope, out var error);
                var matchingSpans = record.PresentationCoverage.ResolvedSpans.Where(span =>
                    slope != null && Math.Abs(slope.FromOffset - span.FromOffsetM) <= 0.0005 &&
                    Math.Abs(slope.ToOffset - span.ToOffsetM) <= 0.0005).ToList();
                if (!parsed || slope == null || matchingSpans.Count != 1 ||
                    !SectionAnnotationContractLogic.TryVerifySlopeEvidence(surfaces.Design,
                        matchingSpans[0].FromOffsetM, matchingSpans[0].ToOffsetM, slope, out live) || live == null)
                    throw new InvalidOperationException("Measured layout slope is not proven from live FG: " +
                        SectionVerificationRecoveryPolicy.DescribeMeasuredSlopeFailure(slope, live, surfaces.Design, error));
                Add(slope.Handle, 0, SlopePosition(view, live.FromOffset, live.ToOffset,
                    live.FromElevation, live.ToElevation));
            }
            if (applied.SlopeLabels.Count != record.PresentationCoverage.WidthSpanCount)
                throw new InvalidOperationException("Measured layout slope inventory is incomplete.");
            var bottom = new List<SectionAnnotationPlacementLogic.LabelBox>();
            foreach (var encoded in applied.DimensionOffsetLabels)
            {
                if (!SectionAnnotationContractLogic.TryParseDimensionOffsetLabelReference(encoded,
                        out var dimension, out var error) || dimension == null ||
                    record.PresentationCoverage.DimensionOffsets.Count(offset =>
                        Math.Abs(offset - dimension.AnchorOffset) <= 0.0005) != 1 ||
                    dimension.PlacedOffset < view.OffsetLeft - 0.0005 || dimension.PlacedOffset > view.OffsetRight + 0.0005)
                    throw new InvalidOperationException("Bottom offset layout is not PLAN-bound: " + error);
                bottom.Add(Measure(dimension.Handle, 0,
                    DimensionLabelPosition(view, dimension.PlacedOffset, view.ElevationMin)));
            }
            if (bottom.Count != record.PresentationCoverage.DimensionMarkCount)
                throw new InvalidOperationException("Measured bottom offset inventory is incomplete.");
            if (!SectionAnnotationContractLogic.TryParseDatumReference(applied.DatumReference,
                    out var datum, out var datumError) || datum == null)
                throw new InvalidOperationException("Measured datum evidence is invalid: " + datumError);
            var liveDatum = MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(surfaces.Existing, 0);
            if (!liveDatum.HasValue || Math.Abs(liveDatum.Value - datum.Elevation) > 0.0005)
                throw new InvalidOperationException("Measured datum is not the live existing-ground elevation at the axis.");
            bottom.Add(Measure(datum.Handle, 1, DatumPosition(view, view.OffsetLeft, view.ElevationMin)));
            var movable = labels.Concat(bottom).Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Stems/ticks are not solid rectangles. Native blocks, schematic outlines,
            // circles, and all remaining text are real fixed ink obstacles.
            var obstacles = registeredEntities.Where(e => !movable.Contains(e.Handle.ToString()) &&
                    e is DBText or BlockReference or Polyline or Circle)
                .Select(ReadObstacleBounds).Where(b => b != null).Select(b => b!).ToList();
            var textHeight = registeredEntities.OfType<DBText>().Select(t => t.Height)
                .OrderBy(h => h).ToList();
            if (textHeight.Count == 0 || !double.IsFinite(textHeight[textHeight.Count / 2]) ||
                textHeight[textHeight.Count / 2] <= 0)
                throw new InvalidOperationException("Measured text height is unavailable.");
            // Drafting spacing (Arthur 04.10 on STA-42676): above the section, 0.45 text heights between rows and a word gap
            // of 1.5 text heights between two labels on one row, so neighbouring strip names and widths never read as one
            // phrase. The rotated offset labels below keep their 0.30 clearance (Codex 02:47 replay: unchanged 24/24).
            var clearance = textHeight[textHeight.Count / 2] * 0.45;
            var wordGap = textHeight[textHeight.Count / 2] * 1.5;
            var bottomClearance = textHeight[textHeight.Count / 2] * 0.30;
            // Header hierarchy (Codex 04.10 03:45): one text height of ink between the strip names and the overall width
            // (band 3) and between the overall width and the axis label (band 4); rows inside a band keep 0.45h.
            var bandGap = new Dictionary<int, double>
            {
                [3] = textHeight[textHeight.Count / 2] * 1.0,
                [4] = textHeight[textHeight.Count / 2] * 1.0,
            };
            observeInputs?.Invoke(SectionMeasuredLayoutInputs.Capture(
                labels, bottom, obstacles, textHeight[textHeight.Count / 2],
                clearance, textHeight[textHeight.Count / 2] * 60));
            // Lines that may not strike a slope label (Codex 03.10 14:40): the axis, the top dimension ticks and the ROW
            // lines — the exact PLAN-bound core lines, read like every other input, never the leaders drawn after layout.
            var strikeLines = applied.CorePresentationAnnotations
                .Where(e => e.Kind is SectionCorePresentationContract.AxisLine or
                    SectionCorePresentationContract.DimensionTopTick or SectionCorePresentationContract.RowLine)
                .Select(e => entities.TryGetValue(e.Handle, out var entity) && entity is Line line && !line.IsErased
                    ? new SectionAnnotationPlacementLogic.Segment(ToPoint(line.StartPoint), ToPoint(line.EndPoint))
                    : throw new InvalidOperationException($"Measured layout line '{e.Handle}' is unreadable."))
                .ToList();
            var frameTop = Map(view, (view.OffsetLeft + view.OffsetRight) / 2.0, view.ElevationMax) ??
                           throw new InvalidOperationException("SectionView could not map the frame top.");
            // Top positions never depend on a previously moved bottom label. Their
            // canonical measured boxes make APPLY and repeat VERIFY deterministic.
            if (!SectionAnnotationPlacementLogic.TryLayoutLabelsClearOfLines(labels, obstacles.Concat(bottom.Select(l => l.Ink)).ToList(),
                    strikeLines, frameTop.Y, clearance, textHeight[textHeight.Count / 2] * 60,
                    out var layout, out _, out var layoutError, wordGap, bandGap) || layout?.OverallBounds == null)
                throw new InvalidOperationException(layoutError);
            if (!SectionAnnotationPlacementLogic.TryLayoutLabelsDown(bottom,
                    obstacles.Concat(layout.Labels.Select(l => l.Ink)).ToList(), bottomClearance,
                    textHeight[textHeight.Count / 2] * 60, out var bottomLayout, out var bottomError) || bottomLayout == null)
                throw new InvalidOperationException("Bottom annotation layout: " + bottomError);
            var placedLabels = layout.Labels.Concat(bottomLayout.Labels).ToList();
            var boxes = placedLabels.Select(l => l.Ink).Concat(obstacles).ToList();
            var leaders = new List<LinePlacement>();
            foreach (var placed in layout.Labels.Where(l => l.Rise > clearance))
            {
                var original = labels.Single(l => l.Id == placed.Id);
                leaders.AddRange(SectionAnnotationPlacementLogic.ClearVerticalLeader(
                    original.Anchor, placed.Ink.MinY - clearance, boxes, clearance)
                    .Select(ToLine));
            }
            foreach (var placed in bottomLayout.Labels.Where(l => l.Rise < -bottomClearance))
            {
                var original = bottom.Single(l => l.Id == placed.Id);
                leaders.AddRange(SectionAnnotationPlacementLogic.ClearVerticalLeader(
                    SectionAnnotationPlacementLogic.MirrorY(original.Anchor), -placed.Ink.MaxY - bottomClearance,
                    boxes.Select(SectionAnnotationPlacementLogic.MirrorY).ToList(), bottomClearance)
                    .Select(segment => new LinePlacement(
                        ToPoint3d(SectionAnnotationPlacementLogic.MirrorY(segment.Start)),
                        ToPoint3d(SectionAnnotationPlacementLogic.MirrorY(segment.End)))));
            }
            return new MeasuredLabelLayout(
                placedLabels.ToDictionary(l => l.Id, l => ToPoint3d(l.Anchor), StringComparer.OrdinalIgnoreCase),
                placedLabels.ToDictionary(l => l.Id, l => l.Ink, StringComparer.OrdinalIgnoreCase),
                SectionAnnotationPlacementLogic.Envelope(placedLabels.Select(l => l.Ink).Concat(
                    registeredEntities.Where(e => !movable.Contains(e.Handle.ToString())).Select(ReadEntityBounds))
                    .Concat(leaders.Select(line => new SectionAnnotationPlacementLogic.Bounds(
                        Math.Min(line.Start.X, line.End.X), Math.Min(line.Start.Y, line.End.Y),
                        Math.Max(line.Start.X, line.End.X), Math.Max(line.Start.Y, line.End.Y)))))!,
                leaders.Distinct().ToList(), clearance);
        }

        internal static Point3d TextAnchor(DBText text) =>
            text.HorizontalMode == TextHorizontalMode.TextLeft && text.VerticalMode == TextVerticalMode.TextBase
                ? text.Position : text.AlignmentPoint;

        private static SectionAnnotationPlacementLogic.Bounds? ReadObstacleBounds(Entity entity)
        {
            // A schematic handlebar may be a two-vertex horizontal polyline, not a
            // solid area. Do not invent its height; its nondegenerate surrounding
            // silhouette/wheels are measured independently. Unreadable extents still throw.
            if (entity is Polyline)
            {
                var extents = entity.GeometricExtents;
                if (extents.MinPoint.X == extents.MaxPoint.X || extents.MinPoint.Y == extents.MaxPoint.Y)
                    return null;
            }
            return ReadInkBounds(entity);
        }

        internal static SectionAnnotationPlacementLogic.Bounds ReadInkBounds(Entity entity)
        {
            var bounds = ReadEntityBounds(entity);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException($"Native ink bounds for {entity.Handle} are empty.");
            return bounds;
        }

        private static SectionAnnotationPlacementLogic.Bounds ReadEntityBounds(Entity entity)
        {
            var extents = entity.GeometricExtents; // No guessed text metrics / swallowed eInvalidExtents.
            var bounds = new SectionAnnotationPlacementLogic.Bounds(extents.MinPoint.X, extents.MinPoint.Y,
                extents.MaxPoint.X, extents.MaxPoint.Y);
            if (!double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinY) ||
                !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxY) ||
                bounds.Width < 0 || bounds.Height < 0)
                throw new InvalidOperationException($"Native ink bounds for {entity.Handle} are empty/non-finite.");
            return bounds;
        }

        internal static void RequirePlacedLabelBounds(MeasuredLabelLayout layout,
            IReadOnlyList<Entity> registeredEntities)
        {
            var entities = registeredEntities.ToDictionary(e => e.Handle.ToString(), StringComparer.OrdinalIgnoreCase);
            foreach (var expected in layout.FinalBoundsByHandle)
            {
                var actual = ReadInkBounds(entities[expected.Key]);
                if (Math.Abs(actual.MinX - expected.Value.MinX) > 0.00001 ||
                    Math.Abs(actual.MinY - expected.Value.MinY) > 0.00001 ||
                    Math.Abs(actual.MaxX - expected.Value.MaxX) > 0.00001 ||
                    Math.Abs(actual.MaxY - expected.Value.MaxY) > 0.00001)
                    throw new InvalidOperationException("Native text bounds differ from measured layout: " + expected.Key);
            }
            var allInk = registeredEntities.Where(e => e is DBText or BlockReference or Polyline or Circle)
                .Select(e => (Entity: e, Bounds: ReadObstacleBounds(e))).Where(e => e.Bounds != null)
                .Select(e => new SectionAnnotationPlacementLogic.InkBox(e.Entity.Handle.ToString(), e.Bounds!, e.Entity is DBText))
                .ToList();
            var conflicts = SectionAnnotationPlacementLogic.FindTextInkConflicts(allInk);
            if (conflicts.Count != 0)
                throw new InvalidOperationException("Native annotation ink still overlaps (text/text or text/furniture): " +
                    string.Join("; ", conflicts.Select(c => c.First + " vs " + c.Second)));
        }

        internal static void RequireLayoutLeaders(MeasuredLabelLayout layout,
            IReadOnlyList<Entity> registeredEntities)
        {
            var available = registeredEntities.OfType<Line>().ToList();
            foreach (var expected in layout.Leaders)
            {
                var matching = available.Where(line => line.ColorIndex == 8 &&
                    line.StartPoint.DistanceTo(expected.Start) <= 0.00001 &&
                    line.EndPoint.DistanceTo(expected.End) <= 0.00001).ToList();
                if (matching.Count != 1)
                    throw new InvalidOperationException("Measured annotation leader is missing, duplicate or moved.");
                available.Remove(matching[0]);
            }
        }

        internal static Point3d? Map(
            CivilDb.SectionView view, double offset, double elevation)
        {
            if (view == null || !double.IsFinite(offset) || !double.IsFinite(elevation))
                return null;
            double x = 0, y = 0;
            try
            {
                if (!view.FindXYAtOffsetAndElevation(offset, elevation, ref x, ref y) ||
                    !double.IsFinite(x) || !double.IsFinite(y))
                    return null;
                return new Point3d(x, y, 0);
            }
            catch
            {
                return null;
            }
        }

        internal static Point3d? DatumPosition(
            CivilDb.SectionView view, double leftOffset, double minimumElevation)
        {
            var anchor = Map(view, leftOffset, minimumElevation);
            if (anchor is not { } point) return null;
            var placed = SectionAnnotationPlacementLogic.DatumPosition(
                new SectionAnnotationPlacementLogic.Point(point.X, point.Y));
            return new Point3d(placed.X, placed.Y, 0);
        }

        internal static Point3d? SlopePosition(
            CivilDb.SectionView view, double fromOffset, double toOffset,
            double fromElevation, double toElevation)
        {
            var anchor = Map(view, (fromOffset + toOffset) / 2.0,
                (fromElevation + toElevation) / 2.0);
            if (anchor is not { } point) return null;
            var placed = SectionAnnotationPlacementLogic.SlopePosition(
                new SectionAnnotationPlacementLogic.Point(point.X, point.Y));
            return new Point3d(placed.X, placed.Y, 0);
        }

        internal static Point3d? DimensionLabelPosition(
            CivilDb.SectionView view, double placedOffset, double minimumElevation)
        {
            var anchor = Map(view, placedOffset, minimumElevation);
            if (anchor is not { } point) return null;
            var placed = SectionAnnotationPlacementLogic.DimensionPosition(
                new SectionAnnotationPlacementLogic.Point(point.X, point.Y));
            return new Point3d(placed.X, placed.Y, 0);
        }

        internal static LinePlacement? AxisLine(
            CivilDb.SectionView view, double minimumElevation, double maximumElevation)
        {
            var bottom = Map(view, 0, minimumElevation);
            var top = Map(view, 0, maximumElevation);
            return bottom is { } b && top is { } t
                ? ToLine(SectionAnnotationPlacementLogic.AxisLine(ToPoint(b), ToPoint(t)))
                : null;
        }

        internal static Point3d? AxisLabelPosition(
            CivilDb.SectionView view, double maximumElevation) =>
            Map(view, 0, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.AxisLabelPosition(ToPoint(top)))
                : null;

        internal static Point3d? TitlePosition(
            CivilDb.SectionView view, double leftOffset, double rightOffset,
            double maximumElevation) =>
            Map(view, (leftOffset + rightOffset) / 2.0, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.TitlePosition(ToPoint(top)))
                : null;

        internal static LinePlacement? RowLine(
            CivilDb.SectionView view, double offset, double minimumElevation,
            double maximumElevation)
        {
            var bottom = Map(view, offset, minimumElevation);
            var top = Map(view, offset, maximumElevation);
            return bottom is { } b && top is { } t
                ? ToLine(SectionAnnotationPlacementLogic.RowLine(ToPoint(b), ToPoint(t)))
                : null;
        }

        internal static Point3d? RowLabelPosition(
            CivilDb.SectionView view, double offset, double maximumElevation) =>
            Map(view, offset, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.RowLabelPosition(ToPoint(top)))
                : null;

        internal static LinePlacement? TopTick(
            CivilDb.SectionView view, double offset, double maximumElevation) =>
            Map(view, offset, maximumElevation) is { } top
                ? ToLine(SectionAnnotationPlacementLogic.TopTick(ToPoint(top)))
                : null;

        internal static LinePlacement? BottomTick(
            CivilDb.SectionView view, double offset, double minimumElevation) =>
            Map(view, offset, minimumElevation) is { } bottom
                ? ToLine(SectionAnnotationPlacementLogic.BottomTick(ToPoint(bottom)))
                : null;

        internal static Point3d? WidthLabelPosition(
            CivilDb.SectionView view, double fromOffset, double toOffset,
            double maximumElevation) =>
            Map(view, (fromOffset + toOffset) / 2.0, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.WidthLabelPosition(ToPoint(top)))
                : null;

        internal static Point3d? StripLabelPosition(
            CivilDb.SectionView view, double fromOffset, double toOffset,
            double maximumElevation) =>
            Map(view, (fromOffset + toOffset) / 2.0, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.StripLabelPosition(ToPoint(top)))
                : null;

        /// <summary>SEC-B4: overall width, centred over the complete dimension chain.</summary>
        internal static Point3d? OverallWidthLabelPosition(
            CivilDb.SectionView view, double fromOffset, double toOffset,
            double maximumElevation) =>
            Map(view, (fromOffset + toOffset) / 2.0, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.OverallWidthLabelPosition(ToPoint(top)))
                : null;

        /// <summary>SEC-B4: one dimension line joining the tops of every chain tick.</summary>
        internal static LinePlacement? DimensionChainLine(
            CivilDb.SectionView view, double fromOffset, double toOffset,
            double maximumElevation)
        {
            var start = Map(view, fromOffset, maximumElevation);
            var end = Map(view, toOffset, maximumElevation);
            return start is { } s && end is { } e ? new LinePlacement(s, e) : null;
        }

        /// <summary>SEC-m3/SEC-M3: legend lines, centred under the title.</summary>
        internal static Point3d? LegendPosition(
            CivilDb.SectionView view, double leftOffset, double rightOffset,
            double maximumElevation) =>
            Map(view, (leftOffset + rightOffset) / 2.0, maximumElevation) is { } top
                ? ToPoint3d(SectionAnnotationPlacementLogic.LegendPosition(ToPoint(top)))
                : null;

        private static SectionAnnotationPlacementLogic.Point ToPoint(Point3d point) =>
            new(point.X, point.Y);

        private static Point3d ToPoint3d(SectionAnnotationPlacementLogic.Point point) =>
            new(point.X, point.Y, 0);

        private static LinePlacement ToLine(SectionAnnotationPlacementLogic.Segment segment) =>
            new(ToPoint3d(segment.Start), ToPoint3d(segment.End));

        /// <summary>
        /// Reads exactly the two surface children selected by PLAN.  The PLAN contract
        /// writes existing first and design second.  Name, typed source identity,
        /// source handle, child cardinality and every sampled point are fail-closed.
        /// </summary>
        internal static SurfaceChains ReadSurfaceChains(
            Transaction tr,
            CivilDb.SampleLine sampleLine,
            SectionPlanRecord record,
            double offMin,
            double offMax,
            double elevMin,
            double elevMax)
        {
            var planned = record.PlannedSources
                .Where(source => source.Required &&
                                 string.Equals(source.PlannedState, "sampled",
                                     StringComparison.Ordinal) &&
                                 string.Equals(source.SourceType, "surface",
                                     StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (planned.Count != 2 ||
                planned.Any(source => string.IsNullOrWhiteSpace(source.SourceName) ||
                                      string.IsNullOrWhiteSpace(source.SourceHandle)) ||
                string.Equals(planned[0].SourceName, planned[1].SourceName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(planned[0].SourceHandle, planned[1].SourceHandle,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Record '{record.RecordId}' does not carry one exact, distinct PLAN-selected EG/FG source pair.");

            var rawByIndex = new[]
            {
                new List<SectionSurfaceTopologyLogic.Point>(),
                new List<SectionSurfaceTopologyLogic.Point>(),
            };
            var counts = new int[2];
            var seenChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId sectionId in sampleLine.GetSectionIds())
            {
                if (sectionId.IsNull || sectionId.IsErased ||
                    tr.GetObject(sectionId, OpenMode.ForRead, openErased: false)
                        is not CivilDb.Section section || section.IsErased)
                    throw new InvalidOperationException(
                        $"SampleLine returned unreadable surface child {sectionId.Handle}.");
                if (!seenChildren.Add(section.Handle.ToString()))
                    throw new InvalidOperationException(
                        $"SampleLine repeats Section child {section.Handle}.");
                // Corridor-surface Sections are surfaces too (the resolver maps them to the surface).
                if (!SectionSourceService.IsSurfaceSection(section.SourceType))
                    continue;

                var identity = SectionSourceService.ResolveLiveSourceIdentityStrict(
                    tr, section.SourceId, section.SourceType, section.SourceName,
                    $"Surface Section child {section.Handle}");
                var matches = planned
                    .Select((source, index) => (source, index))
                    .Where(item => string.Equals(item.source.SourceName, identity.Name,
                                       StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(item.source.SourceHandle, identity.Handle,
                                       StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 0) continue;
                if (matches.Count != 1 ||
                    !string.Equals(matches[0].source.SourceName, identity.Name,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(matches[0].source.SourceHandle, identity.Handle,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Surface child {section.Handle} does not match one exact PLAN source identity.");

                var target = matches[0].index;
                counts[target]++;
                foreach (CivilDb.SectionPoint sectionPoint in section.SectionPoints)
                {
                    var point = sectionPoint.Location;
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                        !double.IsFinite(point.Z))
                        throw new InvalidOperationException(
                            $"Surface Section '{identity.Name}' contains a non-finite point.");
                    rawByIndex[target].Add(new(point.X, point.Y, point.Z, sectionPoint.SegmentTo));
                }
            }

            if (counts[0] != 1 || counts[1] != 1)
                throw new InvalidOperationException(
                    $"PLAN-selected EG/FG surface child cardinality is not exact " +
                    $"(EG={counts[0]}, FG={counts[1]}).");

            // Civil retains isolated SectionPoint entries beyond a surface boundary.
            // Only native segment participants form sampled geometry. In particular,
            // retain an incoming terminal point with SegmentTo=-1, but never join it
            // to an isolated endpoint (native FG proof, 09/09) or across an inner gap.
            var existingRaw = SectionSurfaceTopologyLogic.RequireSingleChain(rawByIndex[0]);
            var designRaw = SectionSurfaceTopologyLogic.RequireSingleChain(rawByIndex[1]);
            var existing = SectionFurnitureLogic.NormalizeSectionPoints(
                existingRaw.Select(point => (point.X, point.Y, point.Z)).ToList(),
                offMin, offMax, elevMin, elevMax);
            var design = SectionFurnitureLogic.NormalizeSectionPoints(
                designRaw.Select(point => (point.X, point.Y, point.Z)).ToList(),
                offMin, offMax, elevMin, elevMax);
            if (existing.Count < 2 || design.Count < 2)
                throw new InvalidOperationException(
                    $"PLAN-selected EG/FG chains are incomplete inside the live SectionView " +
                    $"(EG={existing.Count}, FG={design.Count}).");
            return new SurfaceChains(existing, design);
        }
    }
}
