using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Deterministic placement of section views on a grid.
    ///
    /// Replaces the previous "stack everything downwards forever" behaviour, which
    /// produced a column an engineer could not work with. Pure geometry, no Autodesk
    /// types, so the ordering and spacing rules are unit-tested directly.
    ///
    /// Ordering is by station along the alignment — the order an engineer reads a
    /// road in — never by discovery order, which is an artifact of how the drawing
    /// happens to be built.
    /// </summary>
    public static class SectionLayoutPlanner
    {
        public sealed record Cell(string RecordId, int Column, int Row, double X, double Y);

        public sealed record Options
        {
            /// <summary>Views per row before wrapping to the next band.</summary>
            public int Columns { get; init; } = 3;

            /// <summary>Horizontal step between view origins, meters.</summary>
            public double SpacingX { get; init; } = 120.0;

            /// <summary>Vertical step between bands, meters.</summary>
            public double SpacingY { get; init; } = 80.0;

            /// <summary>Grid origin (usually clear of the model extents).</summary>
            public double OriginX { get; init; }
            public double OriginY { get; init; }
        }

        public sealed record Input(string RecordId, double? Station, string? SectionId);

        /// <summary>
        /// Places each record on the grid. Rows advance downward, columns advance in
        /// the drawing's positive X so the sheet reads left-to-right, top-to-bottom.
        /// </summary>
        public static List<Cell> Plan(IReadOnlyList<Input> records, Options options)
        {
            if (options.Columns < 1)
                throw new ArgumentOutOfRangeException(nameof(options), "Columns must be at least 1.");

            // Station order first; records without a station keep a stable tail order
            // by id so the layout never shuffles between identical runs.
            var ordered = records
                .OrderBy(r => r.Station.HasValue ? 0 : 1)
                .ThenBy(r => r.Station ?? 0)
                .ThenBy(r => r.RecordId, StringComparer.Ordinal)
                .ToList();

            var cells = new List<Cell>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                int column = i % options.Columns;
                int row = i / options.Columns;
                cells.Add(new Cell(
                    ordered[i].RecordId,
                    column,
                    row,
                    options.OriginX + column * options.SpacingX,
                    options.OriginY - row * options.SpacingY));
            }
            return cells;
        }

        /// <summary>
        /// Minimum spacing that keeps neighbouring views apart given the widest swath
        /// in the plan. A section 40 m wide inside a 30 m grid step would overlap its
        /// neighbour, so the grid adapts to the data instead of hoping.
        /// </summary>
        public static Options FitToContent(
            IReadOnlyList<double> viewWidths,
            IReadOnlyList<double> viewHeights,
            Options baseline,
            double marginX = 20.0,
            double marginY = 25.0)
        {
            var widest = viewWidths.Count == 0 ? 0 : viewWidths.Max();
            var tallest = viewHeights.Count == 0 ? 0 : viewHeights.Max();

            return baseline with
            {
                SpacingX = Math.Max(baseline.SpacingX, widest + marginX),
                SpacingY = Math.Max(baseline.SpacingY, tallest + marginY),
            };
        }

        // ------------------------------------------------------------- sheet layout

        /// <summary>A created view's real bounding-box size, in drawing units.</summary>
        public sealed record Box(double Width, double Height);

        /// <summary>Where a box's lower-left corner must end up.</summary>
        public sealed record Placement(int Index, double MinX, double MinY);

        /// <summary>
        /// Arranges boxes of DIFFERENT sizes into a sheet: one column width for the whole
        /// sheet (columns stay aligned), each row only as tall as its own tallest box,
        /// every box centred in its column and aligned to the top of its row.
        ///
        /// This is the layout APPLY performs after the section views exist, when their
        /// real extents are finally known. PLAN can only guess a section view's height -
        /// it depends on the sampled elevation range and the style's vertical
        /// exaggeration - and the first real batch stacked 25 views 80 m apart while the
        /// deepest was 335 m tall, printing their elevation axes on top of each other
        /// (6422, 2026-08-19).
        /// </summary>
        public static List<Placement> ArrangeSheet(
            IReadOnlyList<Box> boxes, int columns, double marginX, double marginY,
            double originX, double originY)
        {
            if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
            var result = new List<Placement>(boxes.Count);
            if (boxes.Count == 0) return result;

            var cellW = boxes.Max(b => b.Width) + marginX;
            int rows = (boxes.Count + columns - 1) / columns;

            var rowHeights = new double[rows];
            for (int k = 0; k < boxes.Count; k++)
            {
                int r = k / columns;
                if (boxes[k].Height > rowHeights[r]) rowHeights[r] = boxes[k].Height;
            }

            double rowTop = originY;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    int k = r * columns + c;
                    if (k >= boxes.Count) break;
                    result.Add(new Placement(
                        k,
                        originX + c * cellW + (cellW - boxes[k].Width) / 2.0,
                        rowTop - boxes[k].Height));
                }
                rowTop -= rowHeights[r] + marginY;
            }
            return result;
        }

        /// <summary>True when no two arranged boxes overlap. Asserted in tests.</summary>
        public static bool IsSheetOverlapFree(IReadOnlyList<Box> boxes, IReadOnlyList<Placement> placements)
        {
            for (int i = 0; i < placements.Count; i++)
            {
                for (int j = i + 1; j < placements.Count; j++)
                {
                    var a = placements[i]; var b = placements[j];
                    var ba = boxes[a.Index]; var bb = boxes[b.Index];
                    bool sepX = a.MinX + ba.Width <= b.MinX + 1e-9 || b.MinX + bb.Width <= a.MinX + 1e-9;
                    bool sepY = a.MinY + ba.Height <= b.MinY + 1e-9 || b.MinY + bb.Height <= a.MinY + 1e-9;
                    if (!sepX && !sepY) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when no two placed views can overlap for the given view size. Used as
        /// a self-check before APPLY and asserted in tests.
        /// </summary>
        public static bool IsOverlapFree(
            IReadOnlyList<Cell> cells, double viewWidth, double viewHeight)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                for (int j = i + 1; j < cells.Count; j++)
                {
                    var dx = Math.Abs(cells[i].X - cells[j].X);
                    var dy = Math.Abs(cells[i].Y - cells[j].Y);
                    if (dx < viewWidth && dy < viewHeight) return false;
                }
            }
            return true;
        }
    }
}
