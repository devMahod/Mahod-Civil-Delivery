using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// The area of a hatch AutoCAD cannot measure (Hatch.Area throws), taken from its boundary loops. Live 2026-11-06:
/// 15 such hatches on the 4NATIVIM interchange plan held ~59,000 m² of carriageway that the bill of quantities dropped.
/// </summary>
public sealed class HatchBoundaryAreaTests
{
    private static IReadOnlyList<(double X, double Y)> Rect(double x0, double y0, double x1, double y1) =>
        new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };

    private static double Measure(string style, params IReadOnlyList<(double X, double Y)>[] loops)
    {
        Assert.True(HatchBoundaryArea.TryMeasure(loops, style, out var result, out var refusal), refusal);
        return result!.Area;
    }

    [Fact]
    public void A_plain_loop_is_its_polygon_area() => Assert.Equal(200, Measure("Normal", Rect(0, 0, 20, 10)), 9);

    [Fact]
    public void An_island_is_cut_out() => Assert.Equal(200 - 16, Measure("Normal", Rect(0, 0, 20, 10), Rect(2, 2, 6, 6)), 9);

    [Fact]
    public void Separate_outer_loops_add_up() => Assert.Equal(200 + 100, Measure("Normal", Rect(0, 0, 20, 10), Rect(100, 0, 110, 10)), 9);

    [Fact]
    public void A_self_intersecting_loop_counts_each_lobe_once()
    {
        // A bow-tie: two triangles of 25 m² meeting at (5,5). Its signed shoelace area is 0; AutoCAD fills both lobes.
        var bowTie = new[] { (0d, 0d), (10d, 10d), (10d, 0d), (0d, 10d) };
        Assert.Equal(50, Measure("Normal", bowTie), 6);
    }

    [Fact]
    public void A_loop_that_doubles_back_on_itself_is_measured_not_dropped()
    {
        // Rectangle whose boundary retraces part of an edge — the kind of loop Hatch.Area refuses.
        var loop = new[] { (0d, 0d), (20d, 0d), (20d, 10d), (10d, 10d), (15d, 10d), (0d, 10d) };
        Assert.Equal(200, Measure("Normal", loop), 6);
    }

    [Theory]
    [InlineData("Normal", 400 - 100 + 4)]   // outer filled, island cut, island-in-island filled again
    [InlineData("Outer", 400 - 100)]        // only the first level of islands is cut
    [InlineData("Ignore", 400)]             // islands ignored
    public void Nesting_follows_the_hatch_style(string style, double expected) =>
        Assert.Equal(expected, Measure(style, Rect(0, 0, 20, 20), Rect(5, 5, 15, 15), Rect(9, 9, 11, 11)), 9);

    [Fact]
    public void Degenerate_loops_are_refused_not_counted_as_zero()
    {
        Assert.False(HatchBoundaryArea.TryMeasure(new[] { (IReadOnlyList<(double, double)>)new[] { (0d, 0d), (1d, 1d) } }, "Normal", out _, out var refusal));
        Assert.NotEmpty(refusal);
    }

    [Fact]
    public void Out_and_back_loops_are_collapsed_but_a_real_or_open_ring_is_not()
    {
        IReadOnlyList<(double, double)> tick = new[] { (203510.40, 647174.49), (203511.09, 647174.60), (203510.40, 647174.49) };
        IReadOnlyList<(double, double)> twoPoints = new[] { (0d, 0d), (1d, 1d) };
        Assert.True(HatchBoundaryArea.IsCollapsed(new[] { tick }));
        Assert.True(HatchBoundaryArea.IsCollapsed(new[] { tick, twoPoints, tick }));
        Assert.False(HatchBoundaryArea.IsCollapsed(new[] { tick, Rect(0, 0, 2, 2) }));
        Assert.False(HatchBoundaryArea.IsCollapsed(Array.Empty<IReadOnlyList<(double, double)>>()));
        Assert.False(HatchBoundaryArea.TryMeasure(new[] { tick }, "Normal", out _, out _));   // still never a silent zero
    }

    [Fact]
    public void The_shape_comes_back_as_outer_rings_with_their_holes()
    {
        Assert.True(HatchBoundaryArea.TryMeasure(new[] { Rect(0, 0, 20, 10), Rect(2, 2, 6, 6) }, "Normal", out var result, out _));
        var polygon = Assert.Single(HatchBoundaryArea.Polygons(result!.Shape));
        Assert.Equal(4, polygon.Outer.Count);
        Assert.Single(polygon.Holes);
    }
}
