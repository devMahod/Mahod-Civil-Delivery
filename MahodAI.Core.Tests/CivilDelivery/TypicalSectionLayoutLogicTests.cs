using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using LayoutState = MahodAI.CivilDelivery.Shared.TypicalSectionLayoutLogic.LayoutState;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class TypicalSectionLayoutLogicTests
{
    private const double Gap = TypicalSectionLayoutLogic.GapM;

    /// <summary>A block drawn at (x, y) that grows right and up from its origin.</summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) Block(double x, double y, double width, double height) =>
        (x, y, x + width, y + height);

    [Fact]
    public void TheFirstBlockGoesToTheStartOrigin()
    {
        var first = TypicalSectionLayoutLogic.NextSlot(LayoutState.Start(1000, 2000), null);

        first.X.Should().Be(1000);
        first.Y.Should().Be(2000);
        first.State.Placed.Should().Be(1);
        first.State.Row.Should().Be(0);
    }

    [Fact]
    public void TheSecondBlockGoesRightOfTheFirstPlusTheGap()
    {
        var first = TypicalSectionLayoutLogic.NextSlot(LayoutState.Start(1000, 2000), null);
        var second = TypicalSectionLayoutLogic.NextSlot(first.State, Block(first.X, first.Y, 90, 40));

        second.X.Should().Be(1000 + 90 + Gap);
        second.Y.Should().Be(2000, "the row keeps one base line");
        second.State.Placed.Should().Be(2);
        second.State.Row.Should().Be(0);
    }

    [Fact]
    public void TheSixthBlockStartsANewRowAboveTheTallestBlockOfTheFirst()
    {
        var state = LayoutState.Start(1000, 2000);
        var placement = TypicalSectionLayoutLogic.NextSlot(state, null);
        double[] heights = { 40, 65, 30, 50, 45 };
        for (var i = 0; i < TypicalSectionLayoutLogic.PerRow - 1; i++)
            placement = TypicalSectionLayoutLogic.NextSlot(placement.State, Block(placement.X, placement.Y, 90, heights[i]));
        placement.State.PlacedInRow.Should().Be(TypicalSectionLayoutLogic.PerRow);

        var sixth = TypicalSectionLayoutLogic.NextSlot(placement.State, Block(placement.X, placement.Y, 90, heights[4]));

        sixth.X.Should().Be(1000, "every row starts at the first column");
        sixth.Y.Should().Be(2000 + 65 + Gap, "the new row sits above the tallest block of the row below, plus the gap");
        sixth.State.Row.Should().Be(1);
        sixth.State.PlacedInRow.Should().Be(1);

        var seventh = TypicalSectionLayoutLogic.NextSlot(sixth.State, Block(sixth.X, sixth.Y, 80, 20));
        seventh.X.Should().Be(1000 + 80 + Gap);
        seventh.Y.Should().Be(sixth.Y);
    }

    [Fact]
    public void APickThatDrewNothingKeepsItsSlotForTheNextPick()
    {
        var first = TypicalSectionLayoutLogic.NextSlot(LayoutState.Start(0, 0), null);
        var second = TypicalSectionLayoutLogic.NextSlot(first.State, Block(0, 0, 50, 30));

        var again = TypicalSectionLayoutLogic.NextSlot(second.State, null);

        again.X.Should().Be(second.X);
        again.Y.Should().Be(second.Y);
        again.State.Should().Be(second.State);
    }

    [Fact]
    public void ABlockHangingLeftOfOrBelowItsOriginKeepsThatRoomFree()
    {
        var first = TypicalSectionLayoutLogic.NextSlot(LayoutState.Start(100, 100), null);
        // The firm's block reaches 8 m left of its origin and 6 m below it.
        var second = TypicalSectionLayoutLogic.NextSlot(first.State, (92, 94, 150, 140));
        second.X.Should().Be(150 + Gap + 8);

        var placement = second;
        for (var i = 0; i < TypicalSectionLayoutLogic.PerRow - 1; i++)
            placement = TypicalSectionLayoutLogic.NextSlot(placement.State, Block(placement.X, placement.Y, 50, 40));

        placement.State.Row.Should().Be(1);
        placement.Y.Should().Be(140 + Gap + 6, "the next row also clears the drop below the first row's base");
    }

    [Fact]
    public void TheStateHandedInIsNeverChanged()
    {
        var start = LayoutState.Start(5, 7);
        var first = TypicalSectionLayoutLogic.NextSlot(start, null);
        var second = TypicalSectionLayoutLogic.NextSlot(first.State, Block(5, 7, 10, 10));

        start.Should().Be(LayoutState.Start(5, 7));
        first.State.Placed.Should().Be(1);
        second.State.Placed.Should().Be(2);
    }

    [Fact]
    public void InvertedOrNonFiniteExtentsAreRefused()
    {
        var first = TypicalSectionLayoutLogic.NextSlot(LayoutState.Start(0, 0), null);

        ((Action)(() => TypicalSectionLayoutLogic.NextSlot(first.State, (10, 0, 0, 5)))).Should().Throw<ArgumentException>();
        ((Action)(() => TypicalSectionLayoutLogic.NextSlot(first.State, (0, 0, double.NaN, 5)))).Should().Throw<ArgumentException>();
        ((Action)(() => LayoutState.Start(double.NaN, 0))).Should().Throw<ArgumentException>();
    }
}
