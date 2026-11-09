using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class TypicalSectionPickLogicTests
{
    [Fact]
    public void Endpoints_AreTheAxisPointPlusMinusTheLeftNormal()
    {
        var (a, b) = TypicalSectionPickLogic.Endpoints(new P2(100, 200), tangentDeg: 0, halfWidthM: 10);
        a.X.Should().BeApproximately(100, 1e-9); a.Y.Should().BeApproximately(210, 1e-9);
        b.X.Should().BeApproximately(100, 1e-9); b.Y.Should().BeApproximately(190, 1e-9);
        TypicalSectionPickLogic.Distance(a, b).Should().BeApproximately(20, 1e-9);

        var (a2, b2) = TypicalSectionPickLogic.Endpoints(new P2(0, 0), tangentDeg: 90, halfWidthM: 5);
        a2.X.Should().BeApproximately(-5, 1e-9); a2.Y.Should().BeApproximately(0, 1e-9);
        b2.X.Should().BeApproximately(5, 1e-9); b2.Y.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void Endpoints_RefuseAnUnusableHalfWidth()
    {
        var act = () => TypicalSectionPickLogic.Endpoints(new P2(0, 0), 0, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
        var huge = () => TypicalSectionPickLogic.Endpoints(new P2(0, 0), 0, 250);
        huge.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(double.NaN, false)]
    [InlineData(201.0, false)]
    [InlineData(15.0, true)]
    [InlineData(200.0, true)]
    public void ValidateHalfWidth_AcceptsOnlyPositiveFiniteValuesUpToTheLimit(double value, bool usable)
    {
        (TypicalSectionPickLogic.ValidateHalfWidth(value) == null).Should().Be(usable);
    }

    [Fact]
    public void DefaultHalfWidth_PrefersAUsableProfileValue()
    {
        TypicalSectionPickLogic.DefaultHalfWidth(null).Should().Be(TypicalSectionContracts.DefaultHalfWidthM);
        TypicalSectionPickLogic.DefaultHalfWidth(15).Should().Be(15);
        TypicalSectionPickLogic.DefaultHalfWidth(500).Should().Be(TypicalSectionContracts.DefaultHalfWidthM);
    }

    [Fact]
    public void TooClose_RefusesASecondPickWithinTheMinimumSpacing()
    {
        var taken = new[] { new P2(0, 0), new P2(50, 0) };
        TypicalSectionPickLogic.TooClose(taken, new P2(0.5, 0)).Should().BeTrue();
        TypicalSectionPickLogic.TooClose(taken, new P2(2, 0)).Should().BeFalse();
        TypicalSectionPickLogic.TooClose(Array.Empty<P2>(), new P2(0, 0)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(135, -45)]
    [InlineData(270, 90)]
    [InlineData(300, -60)]
    [InlineData(-30, -30)]
    public void ReadableTextRotation_FoldsIntoTheReadableHalfTurn(double direction, double expected)
    {
        TypicalSectionPickLogic.ReadableTextRotationDeg(direction).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Assess_IsTheOneRuleThePreviewColoursByAndTheClickRefusesBy()
    {
        TypicalSectionPickLogic.Assess("Road34", 420, 3.2, 20, duplicate: false)
            .Should().Be((TypicalSectionPickLogic.PickState.Ready, (string?)null));
        TypicalSectionPickLogic.Assess(null, 420, 0, 20, false).State
            .Should().Be(TypicalSectionPickLogic.PickState.NoAlignment);
        TypicalSectionPickLogic.Assess("Road34", double.NaN, 0, 20, false).State
            .Should().Be(TypicalSectionPickLogic.PickState.NoAlignment);
        var far = TypicalSectionPickLogic.Assess("Road34", 420, -27.4, 20, false);
        far.State.Should().Be(TypicalSectionPickLogic.PickState.TooFar);
        far.Reason.Should().Contain("27.4").And.Contain("Road34");
        var twice = TypicalSectionPickLogic.Assess("Road34", 420, 1, 20, duplicate: true);
        twice.State.Should().Be(TypicalSectionPickLogic.PickState.Duplicate);
        twice.Reason.Should().Contain("420.00");
        // Too far wins over duplicate: the click is not on this road at all.
        TypicalSectionPickLogic.Assess("Road34", 420, 30, 20, duplicate: true).State
            .Should().Be(TypicalSectionPickLogic.PickState.TooFar);
    }

    [Fact]
    public void MarkerLabel_IsTheRawStationWithTwoDecimals_Invariant()
    {
        TypicalSectionContracts.MarkerLabel(420).Should().Be("420.00");
        TypicalSectionContracts.MarkerLabel(1234.5).Should().Be("1234.50");
    }

    [Theory]
    [InlineData(20, 0.6, 20.0 / 6)]
    [InlineData(5, 0.3, 1.5)]
    [InlineData(100, 2.0, 10)]
    [InlineData(double.NaN, 0.6, 20.0 / 6)]
    public void TheMarkerIsABandWideEnoughToSee_WithALabelToMatch(double halfWidth, double width, double textHeight)
    {
        // Live 2026-10-28: a plain line vanished among the plan's linework.
        TypicalSectionContracts.MarkerWidth(halfWidth).Should().BeApproximately(width, 1e-9);
        TypicalSectionContracts.MarkerTextHeight(halfWidth).Should().BeApproximately(textHeight, 1e-9);
    }

    [Fact]
    public void SnapToCorridorStation_TakesTheNearestComputedStation()
    {
        var corridor = new[] { 0.0, 20.0, 40.0, 47.312, 60.0 };
        TypicalSectionPickLogic.SnapToCorridorStation(9.9, corridor).Should().Be(0.0);
        TypicalSectionPickLogic.SnapToCorridorStation(10.1, corridor).Should().Be(20.0);
        TypicalSectionPickLogic.SnapToCorridorStation(46.0, corridor).Should().Be(47.312, "a geometry station counts like a frequency one");
        TypicalSectionPickLogic.SnapToCorridorStation(500, corridor).Should().Be(60.0);
        TypicalSectionPickLogic.SnapToCorridorStation(40.0, corridor).Should().Be(40.0);
    }

    [Fact]
    public void SnapToCorridorStation_IsNullWithoutAComputedStation()
    {
        TypicalSectionPickLogic.SnapToCorridorStation(10, Array.Empty<double>()).Should().BeNull();
        TypicalSectionPickLogic.SnapToCorridorStation(10, null).Should().BeNull();
        TypicalSectionPickLogic.SnapToCorridorStation(double.NaN, new[] { 1.0 }).Should().BeNull();
        TypicalSectionPickLogic.SnapToCorridorStation(10, new[] { double.NaN, 12.0 }).Should().Be(12.0);
    }

    // ------------------------------------------------------------------ no-axis popup (2026-11-09, GM-halufa_A_new2)

    [Fact]
    public void NoAxis_OnADraftedPlan_ExplainsAndOffersToPickTheCentreLine()
    {
        var notice = TypicalSectionPickLogic.NoAxis(Array.Empty<string>(), new[] { "ROAD-*" });

        notice.CanPickAxis.Should().BeTrue();
        notice.Title.Should().Be("לא נמצא ציר לכביש");
        notice.Body.Should().Contain("AX-*, CL או CENTERLINE", "it says which layers were searched")
            .And.Contain("גם בתוך XREF")
            .And.Contain("AX-43", "and how to make the next run find the axis by itself")
            .And.NotContain("AX-<", "a Latin prefix glued to a Hebrew placeholder reads backwards right-to-left");
        notice.Summary.Should().NotContain("\n", "the summary is one line for the command line and the footer");
    }

    [Fact]
    public void NoAxis_WithAlignmentsTheProfileDoesNotAllow_SendsToTheProfile_AndOffersNoPick()
    {
        var names = new[] { "A1", "A2", "A3", "A4", "A5", "A6", "A7" };
        var notice = TypicalSectionPickLogic.NoAxis(names, new[] { "ROAD-40*" });

        notice.CanPickAxis.Should().BeFalse("with Civil alignments in the drawing the profile's list decides");
        notice.Body.Should().Contain("7 תוואים").And.Contain("A1, A2, A3, A4, A5 ועוד 2")
            .And.Contain("(ROAD-40*)").And.Contain("הגדרת פרויקט");
        notice.Summary.Should().Contain("התוואים המותרים");
    }
}
