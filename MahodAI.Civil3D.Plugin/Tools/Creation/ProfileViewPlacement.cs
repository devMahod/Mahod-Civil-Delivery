using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Pure placement-rectangle selection for new Profile Views. Scans the extents of ALL
    /// modelspace entities (not just existing ProfileViews) and chooses a free rectangle
    /// below the drawing with a margin, honouring the existing "stack new PVs below the
    /// previous ones" convention. When a wide/deep band of obstacles fills the area beneath
    /// the drawing — so a below-placement would float detached far from the geometry — it
    /// instead offsets the view to a guaranteed-clear column to the RIGHT of all geometry
    /// ("place it further from the drawing"). No AutoCAD dependency — unit-testable.
    /// </summary>
    public static class ProfileViewPlacement
    {
        /// <summary>Axis-aligned rectangle in world coordinates.</summary>
        public readonly record struct Rect(double MinX, double MinY, double MaxX, double MaxY)
        {
            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;

            public bool Intersects(Rect other)
                => MinX <= other.MaxX && MaxX >= other.MinX &&
                   MinY <= other.MaxY && MaxY >= other.MinY;
        }

        public sealed class Placement
        {
            public double OriginX { get; init; }
            public double OriginY { get; init; }
            /// <summary>"below" | "right_of_drawing" | "no_entities_fallback" | "explicit"</summary>
            public string Method { get; init; } = "below";
            public List<string> Warnings { get; } = new();
        }

        /// <summary>
        /// When at least this many obstacles fill the column below the drawing AND the only
        /// clear below-slot is dragged further than <see cref="RightOffsetDropFactor"/>× the
        /// drawing height, the "below" strategy is considered hopeless and placement switches
        /// to a guaranteed-clear column to the RIGHT of all geometry.
        /// </summary>
        private const int DownPushAttemptsBeforeRightOffset = 3;

        /// <summary>
        /// How far below the drawing (as a multiple of the drawing's own height) the clear
        /// slot may sit before the below-placement is judged "detached / lost among objects"
        /// and the view is offset to the right instead.
        /// </summary>
        private const double RightOffsetDropFactor = 1.5;

        /// <summary>
        /// Hard cap (drawing units ≈ metres) on the right-of-drawing gap. The gap is normally
        /// adaptive (15% of the drawing width) so it scales with the drawing, but a single
        /// far-away outlier entity — e.g. a block left at the WCS origin while the survey sits in
        /// Israeli ITM coordinates ~200 km away — would otherwise inflate the measured width and
        /// fling the PV tens of km off the drawing. This caps the gap to a sane "just to the right
        /// with some space" distance. (The caller also discards such outliers before scanning, so
        /// this is defence-in-depth.)
        /// </summary>
        private const double MaxRightMargin = 5000.0;

        /// <summary>
        /// Chooses an insertion origin (bottom-left of the new PV, which extends up-right)
        /// that does not collide with any scanned entity extents.
        /// </summary>
        /// <param name="allExtents">Extents of every modelspace entity (incl. profile views).</param>
        /// <param name="profileViewExtents">Extents of existing ProfileViews only (stacking input).</param>
        /// <param name="anchorX">Preferred X (alignment start X by convention).</param>
        /// <param name="anchorY">
        /// Alignment reference line (alignment start Y). Doubles as (a) the origin when the
        /// drawing has no measurable entities and (b) the divider that separates the drawing
        /// from the "band of obstacles below it" used to decide a right-of-drawing offset.
        /// </param>
        /// <param name="estimatedWidth">Estimated width of the new PV (station range + labels).</param>
        /// <param name="estimatedHeight">Estimated height of the new PV grid.</param>
        /// <param name="marginM">Clear margin kept between the new PV and existing geometry.</param>
        public static Placement Choose(
            IReadOnlyList<Rect> allExtents,
            IReadOnlyList<Rect> profileViewExtents,
            double anchorX,
            double anchorY,
            double estimatedWidth,
            double estimatedHeight,
            double marginM = 50.0,
            bool preferRight = false)
        {
            if (estimatedWidth <= 0) estimatedWidth = 100.0;
            if (estimatedHeight <= 0) estimatedHeight = 200.0;
            if (marginM < 0) marginM = 0;

            if (allExtents == null || allExtents.Count == 0)
            {
                var fallback = new Placement
                {
                    OriginX = anchorX,
                    OriginY = anchorY - estimatedHeight - Math.Max(marginM, 50.0),
                    Method = "no_entities_fallback",
                };
                fallback.Warnings.Add("No measurable modelspace entities found; placed relative to the anchor point.");
                return fallback;
            }

            // Drawing extents across the WHOLE drawing — guarantees the candidate sits below
            // (or to the right of) alignments, tables, blocks, section views and existing
            // profile views alike.
            double minY = double.PositiveInfinity;
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            foreach (var r in allExtents)
            {
                if (r.MinY < minY) minY = r.MinY;
                if (r.MinX < minX) minX = r.MinX;
                if (r.MaxX > maxX) maxX = r.MaxX;
            }
            double drawingWidth = Math.Max(maxX - minX, 0.0);

            // Caller can force a clear column to the RIGHT of all geometry (the longitudinal
            // profile view: the engineer wants it beside the plan with a generous margin,
            // not stacked beneath it). The margin scales with the drawing but stays generous
            // (≥1500 units) so it never "touches" the drawing.
            if (preferRight)
            {
                double rmargin = Math.Clamp(Math.Max(drawingWidth * 0.05, 1500.0), 1500.0, 30000.0);
                return ChooseRightOfDrawing(
                    allExtents, maxX, minY, estimatedWidth, estimatedHeight, rmargin,
                    new List<string>
                    {
                        $"Placed to the right of the drawing with a {rmargin:F0}-unit margin (preferRight).",
                    });
            }

            // Honour the existing stacking convention: gap accounts for the tallest existing
            // PV so a stack of views keeps a consistent rhythm.
            double maxPvHeight = 0;
            if (profileViewExtents != null)
            {
                foreach (var r in profileViewExtents)
                    if (r.Height > maxPvHeight) maxPvHeight = r.Height;
            }

            double gap = Math.Max(Math.Max(maxPvHeight, estimatedHeight) + marginM, 200.0);
            double clearance = Math.Max(marginM, 50.0);
            double x = anchorX;
            double columnMinX = x;
            double columnMaxX = x + estimatedWidth;
            var warnings = new List<string>();

            // The conventional placement: a clear rectangle below the drawing's lowest edge.
            // It is collision-free by construction (no geometry exists below the global minY),
            // so it is the normal, preferred result for an empty area beneath the drawing.
            double belowOriginY = minY - clearance - estimatedHeight;

            // BUT: when a wide/deep BAND of obstacles fills the region under the drawing
            // (survey figures, an existing PV stack, section views), the only clear slot below
            // is dragged far down, leaving the PV detached and floating — effectively "placed
            // on top of / lost among" that band of objects from the engineer's viewpoint.
            //
            // We separate the drawing from the band using the alignment reference line
            // (anchorY = alignment start Y): obstacles that overlap the new PV's X-column and
            // sit BELOW that reference line are the band the below-placement must clear. We
            // measure the band's depth (how far it reaches below the reference) and its
            // member count. If the band is both deep (a long detour relative to the PV height)
            // and made of several obstacles spanning the column, abandon the below convention
            // and OFFSET to the RIGHT of all geometry — a guaranteed-clear column. (Owner
            // instruction: if it would sit on top of other objects, place it further away.)
            int bandObstacleCount = 0;
            double bandLowestY = double.PositiveInfinity;
            foreach (var r in allExtents)
            {
                bool overlapsColumn = r.MinX <= columnMaxX && r.MaxX >= columnMinX;
                bool belowReference = r.MaxY <= anchorY;
                if (overlapsColumn && belowReference)
                {
                    bandObstacleCount++;
                    if (r.MinY < bandLowestY) bandLowestY = r.MinY;
                }
            }
            double bandDepth = double.IsPositiveInfinity(bandLowestY)
                ? 0.0
                : anchorY - bandLowestY;
            double bandDepthThreshold = RightOffsetDropFactor * estimatedHeight;

            bool belowIsHopeless =
                bandObstacleCount >= DownPushAttemptsBeforeRightOffset
                && bandDepth > bandDepthThreshold;

            if (belowIsHopeless)
            {
                warnings.Add(
                    $"Below-drawing column is filled by {bandObstacleCount} obstacle(s) " +
                    $"reaching {bandDepth:F0} units below the alignment reference " +
                    $"(threshold {bandDepthThreshold:F0}); a below-placement would be detached " +
                    "far from the drawing.");
                double margin = Math.Clamp(Math.Max(200.0, drawingWidth * 0.15), 200.0, MaxRightMargin);
                return ChooseRightOfDrawing(
                    allExtents, maxX, minY, estimatedWidth, estimatedHeight, margin, warnings);
            }

            // Normal path: settle into the below slot. A defensive down-push handles the rare
            // case where the estimate is wrong; it cannot loop forever because each step drops
            // a full gap and nothing exists below the global minY.
            double y = belowOriginY;
            int guard = 0;
            const int maxGuard = 1000;
            while (guard++ < maxGuard)
            {
                var candidate = new Rect(x, y, x + estimatedWidth, y + estimatedHeight);
                if (!Collides(candidate, allExtents, out var hit))
                    break;
                warnings.Add(
                    $"Candidate at Y={y:F0} collides with entity extents " +
                    $"[{hit.MinX:F0},{hit.MinY:F0}]..[{hit.MaxX:F0},{hit.MaxY:F0}]; pushed down.");
                y -= gap;
            }

            var below = new Placement { OriginX = x, OriginY = y, Method = "below" };
            below.Warnings.AddRange(warnings);
            return below;
        }

        /// <summary>
        /// Places the new PV in a column immediately to the RIGHT of every scanned entity:
        /// x = maxX + margin. By construction its left edge starts past the drawing's right
        /// edge, so it cannot overlap horizontally. The vertical position walks DOWN from the
        /// drawing's lowest edge until clear, guaranteeing a deterministic non-overlapping
        /// placement.
        /// </summary>
        private static Placement ChooseRightOfDrawing(
            IReadOnlyList<Rect> allExtents,
            double maxX,
            double minY,
            double estimatedWidth,
            double estimatedHeight,
            double margin,
            List<string> warnings)
        {
            double x = maxX + margin;
            double y = minY;
            double step = estimatedHeight + margin;

            int guard = 0;
            const int maxGuard = 200;
            while (guard++ < maxGuard)
            {
                var candidate = new Rect(x, y, x + estimatedWidth, y + estimatedHeight);
                if (!Collides(candidate, allExtents, out _))
                    break;
                y -= step;
            }

            var placement = new Placement { OriginX = x, OriginY = y, Method = "right_of_drawing" };
            placement.Warnings.AddRange(warnings);
            placement.Warnings.Add(
                $"Below-drawing area was blocked; placed PV to the right of all geometry " +
                $"at X={x:F0} (margin {margin:F0}) to avoid overlapping objects.");
            return placement;
        }

        /// <summary>Returns true and the first colliding extent if the candidate overlaps anything.</summary>
        private static bool Collides(Rect candidate, IReadOnlyList<Rect> extents, out Rect hit)
        {
            foreach (var r in extents)
            {
                if (candidate.Intersects(r))
                {
                    hit = r;
                    return true;
                }
            }
            hit = default;
            return false;
        }
    }
}
