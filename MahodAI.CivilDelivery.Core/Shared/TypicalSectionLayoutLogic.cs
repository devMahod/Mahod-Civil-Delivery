using System;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Where the next typical cross-section block goes when the engineer picks stations one by one on
/// the plan and the firm's program (MahodCivilNet) draws one block per pick. Pure arithmetic, no
/// AutoCAD types: the caller measures each drawn block and hands its extents back here.
/// </summary>
/// <remarks>
/// The firm's code places a run at the origin it is given and knows nothing about the earlier picks,
/// so the blocks of separate runs would land on top of each other. This grid keeps them apart: the
/// first block at the start origin, then each next block to the right of the previous block's
/// extents plus <see cref="GapM"/>, and after <see cref="PerRow"/> blocks a new row above the tallest
/// block of the row just finished, plus the gap. Rows go UP from the start origin, so a run that
/// starts beside the design never grows down into it.
/// <para>
/// A block may extend left of, or below, the origin it was drawn at (band tables, the datum label).
/// The next slot keeps that overhang free: the gap is measured from the extents, and the row's
/// deepest drop below its base is added under the row above it.
/// </para>
/// <para>
/// A pick at which the firm's code drew nothing (no corridor at that station) leaves its slot free:
/// calling <see cref="NextSlot"/> with no extents hands out the same origin again.
/// </para>
/// </remarks>
public static class TypicalSectionLayoutLogic
{
    /// <summary>Clear space between neighbouring blocks, in drawing metres (the firm draws at 1:200).</summary>
    public const double GapM = 10.0;

    /// <summary>Blocks per row before the next row starts above.</summary>
    public const int PerRow = 5;

    /// <summary>
    /// The grid's position between picks. Immutable: every <see cref="NextSlot"/> call returns a new one
    /// and leaves the one it was given untouched, so a failed run can retry from the same state.
    /// </summary>
    /// <param name="StartX">X of the first column; every row starts here.</param>
    /// <param name="StartY">Y of the first row's origins.</param>
    /// <param name="Placed">Origins handed out so far, all rows.</param>
    /// <param name="PlacedInRow">Origins handed out in the current row.</param>
    /// <param name="RowBaseY">Y of the current row's origins.</param>
    /// <param name="RowTopY">Highest extent reported in the current row (the base until a block is reported).</param>
    /// <param name="RowDropM">Deepest reported extent below the current row's base (0 until a block hangs below it).</param>
    /// <param name="LastX">X of the origin handed out last.</param>
    /// <param name="LastY">Y of the origin handed out last.</param>
    public sealed record LayoutState(
        double StartX,
        double StartY,
        int Placed,
        int PlacedInRow,
        double RowBaseY,
        double RowTopY,
        double RowDropM,
        double LastX,
        double LastY)
    {
        /// <summary>The state before any block was placed: the first slot is the start origin itself.</summary>
        public static LayoutState Start(double originX, double originY)
        {
            if (!double.IsFinite(originX) || !double.IsFinite(originY))
                throw new ArgumentOutOfRangeException(nameof(originX), "The start origin must be finite.");
            return new LayoutState(originX, originY, 0, 0, originY, originY, 0.0, originX, originY);
        }

        /// <summary>Zero-based index of the row the last origin belongs to.</summary>
        public int Row => Placed == 0 ? 0 : (Placed - 1) / PerRow;
    }

    /// <summary>The origin to draw the next block at, with the grid state that follows from handing it out.</summary>
    public sealed record Placement(double X, double Y, LayoutState State);

    /// <summary>
    /// The origin for the next block. <paramref name="previousBlockExtents"/> is the drawing extents of the
    /// block drawn at the origin the previous call returned; null when nothing was drawn there (the slot
    /// is then handed out again) or for the very first block.
    /// </summary>
    /// <exception cref="ArgumentException">The extents are not finite or are inverted.</exception>
    public static Placement NextSlot(
        LayoutState state,
        (double MinX, double MinY, double MaxX, double MaxY)? previousBlockExtents)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));

        if (state.Placed == 0)
            return new Placement(state.StartX, state.StartY, state with
            {
                Placed = 1,
                PlacedInRow = 1,
                LastX = state.StartX,
                LastY = state.StartY,
            });

        if (previousBlockExtents is not { } previous)
            return new Placement(state.LastX, state.LastY, state);

        RequireExtents(previous);

        var rowTop = Math.Max(state.RowTopY, previous.MaxY);
        var rowDrop = Math.Max(state.RowDropM, state.RowBaseY - previous.MinY);
        // Whatever hangs left of the previous origin is assumed to hang left of the next one too.
        var leftOverhang = Math.Max(0.0, state.LastX - previous.MinX);

        if (state.PlacedInRow < PerRow)
        {
            var x = previous.MaxX + GapM + leftOverhang;
            return new Placement(x, state.RowBaseY, state with
            {
                Placed = state.Placed + 1,
                PlacedInRow = state.PlacedInRow + 1,
                RowTopY = rowTop,
                RowDropM = rowDrop,
                LastX = x,
                LastY = state.RowBaseY,
            });
        }

        // The row is full: the next row sits above the tallest block of this one, and leaves room for
        // the drop below the base that this row's blocks showed.
        var y = rowTop + GapM + rowDrop;
        return new Placement(state.StartX, y, state with
        {
            Placed = state.Placed + 1,
            PlacedInRow = 1,
            RowBaseY = y,
            RowTopY = y,
            RowDropM = 0.0,
            LastX = state.StartX,
            LastY = y,
        });
    }

    private static void RequireExtents((double MinX, double MinY, double MaxX, double MaxY) e)
    {
        if (!double.IsFinite(e.MinX) || !double.IsFinite(e.MinY) || !double.IsFinite(e.MaxX) || !double.IsFinite(e.MaxY))
            throw new ArgumentException("The block extents must be finite.", nameof(e));
        if (e.MaxX < e.MinX || e.MaxY < e.MinY)
            throw new ArgumentException(FormattableString.Invariant(
                $"The block extents are inverted: ({e.MinX}, {e.MinY}) to ({e.MaxX}, {e.MaxY})."), nameof(e));
    }
}
