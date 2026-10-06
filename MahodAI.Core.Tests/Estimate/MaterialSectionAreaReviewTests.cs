using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MaterialSectionAreaReviewTests
{
    [Fact]
    public void AllStationObservationsAreReachableWithExactSourceAndNoPlanAreaTotal()
    {
        var observations = Enumerable.Range(1, 123).Select(index => new MaterialSectionAreaObservation(
            "SIMULATION", "C:\\Fixture\\source.dwg", new string('a', 64), "10A", "corridor", "baseline",
            "ACTUAL-CODE-" + index, index, "m", index / 10d, "m2", 1, "metres", true, index != 123)).ToArray();
        var first = MaterialSectionAreaReviewPolicy.Read(observations, 0);
        var middle = MaterialSectionAreaReviewPolicy.Read(observations, 50);
        var last = MaterialSectionAreaReviewPolicy.Read(observations, 100);
        first.Count.Should().Be(50); first.HasPrevious.Should().BeFalse(); first.HasNext.Should().BeTrue();
        first.Text.Should().Contain(Bidi.Ltr("1–50"));
        middle.Count.Should().Be(50); middle.HasPrevious.Should().BeTrue(); middle.HasNext.Should().BeTrue();
        last.Count.Should().Be(23); last.HasNext.Should().BeFalse();
        last.Text.Should().Contain(Bidi.Ltr("101–123"));
        foreach (var row in observations)
            (first.Text + middle.Text + last.Text).Should().Contain("קוד: " + row.ShapeCode);
        last.Text.Should().Contain("חלקי/לא מוכח").And.Contain(observations[0].DrawingHash)
            .And.Contain("לא שטח תכניתי").And.Contain("H1/H2/H3 אינם מוסקים");
        MaterialSectionAreaReviewPolicy.Read(observations, int.MaxValue).Offset.Should().Be(100);
        MaterialSectionAreaReviewPolicy.Read(observations, -100).Offset.Should().Be(0);
    }

    [Fact]
    public void NoObservationsIsNotAZeroQuantity()
    {
        var page = MaterialSectionAreaReviewPolicy.Read(Array.Empty<MaterialSectionAreaObservation>(), 0);
        page.Count.Should().Be(0); page.HasNext.Should().BeFalse(); page.HasPrevious.Should().BeFalse();
        page.Text.Should().Contain("אין פירוש הדבר ששטחי החומר הם אפס");
    }
}
