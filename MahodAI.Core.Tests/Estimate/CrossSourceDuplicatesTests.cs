using System.Collections.Generic;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>130X, 2026-11-10: the landscape XREF carried 32 km of the traffic plan's island curbs line for line.</summary>
public sealed class CrossSourceDuplicatesTests
{
    private static CrossSourceDuplicates.Item L(string source, string leaf, params (double, double)[] path) => new(source, leaf, "", path);

    [Fact]
    public void A_copy_of_another_drawings_line_on_the_same_layer_counts_once()
    {
        var items = new[]
        {
            L("LA.dwg", "TR-ISLAND-CURBSTONE", (0, 0), (100, 0)),
            L("TR.dwg", "TR-ISLAND-CURBSTONE", (0, 0.01), (100, 0.01)),   // 1 cm off: the same curb
        };
        var d = CrossSourceDuplicates.Measure(items, 0.05, 1);
        Assert.Equal(0, d[0], 9);
        Assert.Equal(100, d[1], 6);
    }

    [Fact]
    public void Only_the_overlapping_part_is_removed()
    {
        var items = new[] { L("A.dwg", "HW-CURB", (0, 0), (100, 0)), L("B.dwg", "HW-CURB", (60, 0), (160, 0)) };
        Assert.Equal(40, CrossSourceDuplicates.Measure(items, 0.05, 1)[1], 6);
    }

    [Theory]
    [InlineData("A.dwg", "HW-CURB", 0.01)]     // same source: two lines one drawing draws are both its own
    [InlineData("B.dwg", "HW-EOP", 0.01)]      // other layer: the edge of pavement beside a curb is real
    [InlineData("B.dwg", "HW-CURB", 0.30)]     // 30 cm apart: a second curb, not a copy
    public void Not_a_duplicate(string source, string leaf, double offset)
    {
        var items = new[] { L("A.dwg", "HW-CURB", (0, 0), (50, 0)), L(source, leaf, (0, offset), (50, offset)) };
        Assert.Equal(0, CrossSourceDuplicates.Measure(items, 0.05, 1)[1], 9);
    }

    [Fact]
    public void A_block_at_the_same_point_in_another_drawing_counts_once()
    {
        var items = new List<CrossSourceDuplicates.Item>
        {
            new("DR.dwg", "DR-MNHL-BL", "בלוק MH", new[] { (10.0, 10.0) }),
            new("LA.dwg", "DR-MNHL-BL", "בלוק MH", new[] { (10.0, 10.02) }),
            new("LA.dwg", "DR-MNHL-BL", "בלוק MH", new[] { (30.0, 10.0) }),
            new("LA.dwg", "DR-MNHL-BL", "בלוק OTHER", new[] { (10.0, 10.0) }),
        };
        var d = CrossSourceDuplicates.Measure(items, 0.05, 1);
        Assert.Equal(new double[] { 0, 1, 0, 0 }, d);
    }
}
