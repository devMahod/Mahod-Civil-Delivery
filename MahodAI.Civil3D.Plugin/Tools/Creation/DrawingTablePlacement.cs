using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Pure placement selection for report tables (earthwork volumes, sight distance, …).
    /// No AutoCAD dependency — unit-testable.
    ///
    /// The rule the engineer asked for (2026-07-27): the table must line up with the rest of the
    /// deliverable — plan, profile view, section views, other tables — not float in empty space.
    /// So it is placed in a clear column just to the RIGHT of the drawing content and vertically
    /// CENTRED on it, and it stacks DOWNWARDS behind any table already parked in that column.
    /// </summary>
    public static class DrawingTablePlacement
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
            /// <summary>Table insertion point = TOP-LEFT corner (an AutoCAD Table grows downwards).</summary>
            public double X { get; init; }
            public double Y { get; init; }
            /// <summary>"right_of_content" | "none"</summary>
            public string Method { get; init; } = "none";
            public bool Resolved => Method != "none";
            public List<string> Notes { get; } = new();
        }

        /// <summary>Smallest gap kept between the drawing content and the table column.</summary>
        private const double MinMargin = 25.0;

        /// <summary>
        /// Cap on the gap. The gap normally scales with the drawing (3% of its width) so it
        /// stays proportional, but a stray far-away entity would otherwise inflate the measured
        /// width and fling the table kilometres away — the very bug this fixes.
        /// </summary>
        private const double MaxMargin = 2000.0;

        /// <summary>
        /// Chooses the table's top-left insertion point.
        /// </summary>
        /// <param name="contentExtents">
        /// Extents of the drawing content the table must clear (alignments, profile views,
        /// section views, surfaces …) — EXCLUDING report tables. Drives the X column.
        /// </param>
        /// <param name="tableExtents">Extents of report tables already in the drawing (stacking input).</param>
        /// <param name="tableWidth">Estimated width of the new table.</param>
        /// <param name="tableHeight">Estimated height of the new table.</param>
        /// <param name="bandExtents">
        /// The row the table must line up WITH — the stage-3 deliverables (profile views and
        /// section views). Empty ⇒ centre on all content. This is the difference between
        /// "somewhere beside the drawing" and "level with the profile view": the plan sits far
        /// above the profile view, so centring on everything parks the table in empty space
        /// above the deliverables (engineer report, 2026-07-27).
        /// </param>
        public static Placement Choose(
            IReadOnlyList<Rect> contentExtents,
            IReadOnlyList<Rect> tableExtents,
            double tableWidth,
            double tableHeight,
            IReadOnlyList<Rect>? bandExtents = null)
        {
            if (tableWidth <= 0) tableWidth = 100.0;
            if (tableHeight <= 0) tableHeight = 50.0;

            if (contentExtents == null || contentExtents.Count == 0)
                return new Placement { Method = "none" };

            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (var r in contentExtents)
            {
                if (r.MinX < minX) minX = r.MinX;
                if (r.MaxX > maxX) maxX = r.MaxX;
                if (r.MinY < minY) minY = r.MinY;
                if (r.MaxY > maxY) maxY = r.MaxY;
            }

            // The row to line up with: the deliverables when we know them, else all content.
            double bandMinY = minY, bandMaxY = maxY;
            string bandLabel = "the drawing content";
            if (bandExtents != null && bandExtents.Count > 0)
            {
                bandMinY = double.PositiveInfinity;
                bandMaxY = double.NegativeInfinity;
                foreach (var r in bandExtents)
                {
                    if (r.MinY < bandMinY) bandMinY = r.MinY;
                    if (r.MaxY > bandMaxY) bandMaxY = r.MaxY;
                }
                bandLabel = "the profile-view / section-view band";
            }

            double margin = Math.Clamp((maxX - minX) * 0.03, MinMargin, MaxMargin);
            double x = maxX + margin;
            // Top-left such that the table is vertically centred on that band.
            double y = (bandMinY + bandMaxY) / 2.0 + tableHeight / 2.0;

            var placement = new List<string>
            {
                $"Placed right of the drawing content (X={x:F0}, margin {margin:F0}), " +
                $"centred on {bandLabel} (Y {bandMinY:F0}..{bandMaxY:F0}).",
            };

            // Stack below any table already occupying that column.
            double gap = Math.Max(tableHeight * 0.25, 10.0);
            int guard = 0;
            const int maxGuard = 200;
            while (guard++ < maxGuard && tableExtents != null)
            {
                var candidate = new Rect(x, y - tableHeight, x + tableWidth, y);
                bool moved = false;
                foreach (var t in tableExtents)
                {
                    if (!candidate.Intersects(t)) continue;
                    y = t.MinY - gap;
                    moved = true;
                    placement.Add(
                        $"Column already held a table at [{t.MinX:F0},{t.MinY:F0}]..[{t.MaxX:F0},{t.MaxY:F0}]; " +
                        $"stacked below it (Y={y:F0}).");
                    break;
                }
                if (!moved) break;
            }

            var result = new Placement { X = x, Y = y, Method = "right_of_content" };
            result.Notes.AddRange(placement);
            return result;
        }

        /// <summary>
        /// Region worth measuring: the road's bounding box grown by a generous buffer. Entities
        /// outside it are outliers (classically a block left at the WCS origin while the survey
        /// sits in Israeli ITM coordinates ~200 km away) and must not drag the table off the
        /// drawing. The buffer is far larger than any real survey (≥50 km, or 5× the road's own
        /// span) yet far smaller than an origin outlier's distance, so real geometry is kept.
        /// </summary>
        public static Rect KeepRegion(Rect roadExtents)
        {
            double span = Math.Max(roadExtents.Width, roadExtents.Height);
            double buffer = Math.Max(50000.0, span * 5.0);
            return new Rect(
                roadExtents.MinX - buffer, roadExtents.MinY - buffer,
                roadExtents.MaxX + buffer, roadExtents.MaxY + buffer);
        }
    }
}
