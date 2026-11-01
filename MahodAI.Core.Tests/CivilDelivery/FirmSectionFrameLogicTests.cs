using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Finding = MahodAI.CivilDelivery.Shared.FirmSectionFrameLogic.Finding;
using Input = MahodAI.CivilDelivery.Shared.FirmSectionFrameLogic.FirmSectionFrameInput;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class FirmSectionFrameLogicTests
{
    // The owner's sample block: top chain ticks (signed metres from the centreline) and
    // the width the firm printed between each consecutive pair. Narrow curb faces and
    // the 3.39 / 1.00 / 3.60 run on the right are the real values.
    private static readonly double[] SampleTickOffsets =
        [-18, -12, -11, -7.4, -5.8, -3.8, -0.5, 0.5, 3.8, 7.19, 8.19, 11.79];

    private static readonly string[] SampleWidthTexts =
        ["6.00", "1.00", "3.60", "1.60", "2.00", "3.30", "1.00", "3.30", "3.39", "1.00", "3.60 מ'"];

    private const double Precision = 1e-6;

    /// <summary>Builds the sample block at a given centreline X and drawing scale.</summary>
    private static Input SampleBlock(
        double? centrelineX = 1000, double horizontalScale = 1, double verticalScale = 1, double datum = 100,
        IReadOnlyList<(double Elevation, double Y)>? ruler = null,
        Func<int, string?>? widthText = null,
        IEnumerable<(double X, string Text)>? extraTexts = null,
        IEnumerable<double>? extraTicks = null)
    {
        var origin = centrelineX ?? 1000;
        var ticks = SampleTickOffsets.Select(offset => origin + offset * horizontalScale).ToList();
        if (extraTicks != null) ticks.AddRange(extraTicks);

        var texts = new List<(double X, string Text)>();
        for (var i = 0; i < SampleWidthTexts.Length; i++)
        {
            var text = widthText != null ? widthText(i) : SampleWidthTexts[i];
            if (text == null) continue;
            texts.Add(((ticks[i] + ticks[i + 1]) / 2, text));
        }
        if (extraTexts != null) texts.AddRange(extraTexts);

        ruler ??= new[] { 281.0, 282.0, 283.0 }
            .Select(elevation => (elevation, datum + elevation * verticalScale)).ToList();
        return new Input(centrelineX, ruler, ticks, texts);
    }

    [Fact]
    public void SampleBlockAtOneToOne_IsDecodedWithMeasuredUnitScalesAndEveryWidth()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock());

        frame.Findings.Should().BeEmpty();
        frame.CentrelineX.Should().Be(1000.0);
        frame.HorizontalScale.Should().BeApproximately(1, Precision);
        frame.VerticalScale.Should().BeApproximately(1, Precision);
        frame.Datum.Should().BeApproximately(100, Precision);
        frame.HasHorizontalFrame.Should().BeTrue();
        frame.HasVerticalFrame.Should().BeTrue();

        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
        for (var i = 0; i < frame.Segments.Count; i++)
        {
            frame.Segments[i].FromOffsetM.Should().BeApproximately(SampleTickOffsets[i], Precision);
            frame.Segments[i].ToOffsetM.Should().BeApproximately(SampleTickOffsets[i + 1], Precision);
            frame.Segments[i].WidthText.Should().Be(SampleWidthTexts[i]);
            frame.Segments[i].WidthTextX.Should().NotBeNull();
        }
        frame.Segments[0].WidthM.Should().Be(6.00);
        frame.Segments[8].WidthM.Should().Be(3.39);
        frame.Segments[9].WidthM.Should().Be(1.00);
        frame.Segments[10].WidthM.Should().Be(3.60, "a Hebrew unit after the number is still that width");
        frame.Segments[8].WidthTextX.Should().BeApproximately(1000 + (3.8 + 7.19) / 2, Precision);

        frame.OffsetOf(1003.8).Should().BeApproximately(3.8, Precision);
        frame.XOf(-0.5).Should().BeApproximately(999.5, Precision);
        frame.ElevationOf(385.5).Should().BeApproximately(285.5, Precision);
        frame.YOf(282).Should().BeApproximately(382, Precision);
    }

    [Fact]
    public void BlockDrawnAtOneToTwoHundred_MeasuresTheScaleInsteadOfAssumingMetres()
    {
        var frame = FirmSectionFrameLogic.Decode(
            SampleBlock(centrelineX: 50_000, horizontalScale: 200, verticalScale: 200, datum: 7));

        frame.Findings.Should().BeEmpty();
        frame.HorizontalScale.Should().BeApproximately(200, 1e-3);
        frame.VerticalScale.Should().BeApproximately(200, 1e-3);
        frame.Datum.Should().BeApproximately(7, 1e-3);

        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
        frame.Segments[2].FromOffsetM.Should().BeApproximately(-11, 1e-4);
        frame.Segments[2].ToOffsetM.Should().BeApproximately(-7.4, 1e-4);
        frame.Segments[2].WidthM.Should().Be(3.60);
        frame.OffsetOf(50_000 + 3.8 * 200).Should().BeApproximately(3.8, 1e-4);
        frame.ElevationOf(7 + 285.5 * 200).Should().BeApproximately(285.5, 1e-4);
    }

    [Fact]
    public void OneWidthTextThatContradictsItsTicks_FailsClosedWithNoScaleAndNoSegments()
    {
        // The firm printed 3.39 above a 3.60 m gap: 6 % off, far beyond rounding.
        var frame = FirmSectionFrameLogic.Decode(
            SampleBlock(widthText: i => i == 2 ? "3.39" : SampleWidthTexts[i]));

        frame.Findings.Should().Contain(Finding.ScaleInconsistent);
        frame.HorizontalScale.Should().BeNull();
        frame.HasHorizontalFrame.Should().BeFalse();
        frame.Segments.Should().BeEmpty();
        frame.OffsetOf(1000).Should().BeNull();
        frame.VerticalScale.Should().BeApproximately(1, Precision, "the ruler is independent of the chain");
    }

    [Fact]
    public void PrintedRounding_IsNotAnInconsistency()
    {
        // "0.31" printed above a 0.3149 m curb face: 1.6 % off, but within the text's own rounding.
        var frame = FirmSectionFrameLogic.Decode(new Input(0,
            [(281, 381), (283, 383)],
            [-3.6, 0, 0.3149, 3.9149],
            [(-1.8, "3.60"), (0.15, "0.31"), (2.1, "3.60")]));

        frame.Findings.Should().BeEmpty();
        frame.HorizontalScale.Should().BeApproximately(1, 1e-3);
        frame.Segments.Should().HaveCount(3);
        frame.Segments[1].WidthM.Should().Be(0.31);
    }

    [Fact]
    public void MissingCentreline_YieldsNoSegmentsEvenThoughTheChainMeasures()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock(centrelineX: null));

        frame.Findings.Should().Contain(Finding.NoCentreline);
        frame.CentrelineX.Should().BeNull();
        frame.HorizontalScale.Should().BeApproximately(1, Precision, "the scale is a diagnostic even without offsets");
        frame.HasHorizontalFrame.Should().BeFalse();
        frame.Segments.Should().BeEmpty();
        frame.OffsetOf(1003.8).Should().BeNull();
        frame.XOf(3.8).Should().BeNull();
    }

    [Fact]
    public void MissingRuler_LeavesTheVerticalFrameNullAndTheChainIntact()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock(ruler: Array.Empty<(double, double)>()));

        frame.Findings.Should().Equal(Finding.NoRuler);
        frame.VerticalScale.Should().BeNull();
        frame.Datum.Should().BeNull();
        frame.HasVerticalFrame.Should().BeFalse();
        frame.ElevationOf(385.5).Should().BeNull();
        frame.YOf(282).Should().BeNull();
        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
    }

    [Fact]
    public void RulerLabelOffTheAxis_FailsTheVerticalFrameClosed()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock(
            ruler: [(281, 381), (282, 382.5), (283, 383)]));

        frame.Findings.Should().Equal(Finding.RulerInconsistent);
        frame.VerticalScale.Should().BeNull();
        frame.Datum.Should().BeNull();
    }

    [Fact]
    public void RulerThatGoesDown_IsNotAnElevationAxis()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock(ruler: [(281, 383), (283, 381)]));

        frame.Findings.Should().Equal(Finding.RulerInconsistent);
        frame.VerticalScale.Should().BeNull();
    }

    [Fact]
    public void TickPairWithoutText_KeepsItsPlaceWithTheMeasuredWidthAndAnEmptyText()
    {
        var frame = FirmSectionFrameLogic.Decode(
            SampleBlock(widthText: i => i == 6 ? null : SampleWidthTexts[i]));

        frame.Findings.Should().Equal(Finding.ChainTextMissing);
        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
        frame.Segments[6].FromOffsetM.Should().BeApproximately(-0.5, Precision);
        frame.Segments[6].ToOffsetM.Should().BeApproximately(0.5, Precision);
        frame.Segments[6].WidthM.Should().BeApproximately(1.0, Precision);
        frame.Segments[6].WidthText.Should().BeEmpty();
        frame.Segments[6].WidthTextX.Should().BeNull();
        frame.Segments[7].WidthText.Should().Be("3.30", "the neighbours are unaffected");
    }

    [Fact]
    public void UnreadableText_IsReportedAndItsPairTreatedAsUnlabelled()
    {
        var frame = FirmSectionFrameLogic.Decode(
            SampleBlock(widthText: i => i == 6 ? "ראה פרט" : SampleWidthTexts[i]));

        frame.Findings.Should().BeEquivalentTo(Finding.ChainTextUnparsable, Finding.ChainTextMissing);
        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
        frame.Segments[6].WidthText.Should().BeEmpty();
    }

    [Fact]
    public void SeveralTextsInOnePair_AreResolvedByTheMeasuredWidthOrReportedAsAmbiguous()
    {
        // A neighbour's narrow "0.31" spilled into the 3.60 m pair: the measured width singles out 3.60.
        var resolved = FirmSectionFrameLogic.Decode(
            SampleBlock(extraTexts: [(1000 - 9.5, "0.31")]));
        resolved.Findings.Should().BeEmpty();
        resolved.Segments[2].WidthText.Should().Be("3.60");

        // Two texts and neither fits the gap: nothing is chosen.
        var ambiguous = FirmSectionFrameLogic.Decode(SampleBlock(
            widthText: i => i == 2 ? "3.50" : SampleWidthTexts[i],
            extraTexts: [(1000 - 9.5, "3.70")]));
        ambiguous.Findings.Should().Equal(Finding.ChainTextAmbiguous);
        ambiguous.Segments[2].WidthText.Should().BeEmpty();
        ambiguous.Segments[2].WidthM.Should().BeApproximately(3.6, 1e-3);
    }

    [Fact]
    public void DuplicateTicksAndTextsOutsideTheChain_DoNotCreateSegments()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock(
            extraTicks: [1000 - 12, 1000 + 3.8 + 1e-9],
            extraTexts: [(1000 - 25, "2.00"), (1000 + 15, "4.00")]));

        frame.Findings.Should().BeEmpty();
        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
    }

    [Fact]
    public void FewerThanTwoTicks_IsNoChain()
    {
        var frame = FirmSectionFrameLogic.Decode(new Input(0, [(281, 381), (283, 383)], [4.5], [(2, "3.60")]));

        frame.Findings.Should().Equal(Finding.NoChain);
        frame.HorizontalScale.Should().BeNull();
        frame.Segments.Should().BeEmpty();
    }

    [Fact]
    public void TicksWithoutAnyReadableWidth_HaveNoScale()
    {
        var frame = FirmSectionFrameLogic.Decode(new Input(0, [(281, 381), (283, 383)], [-3.6, 0, 3.6], []));

        frame.Findings.Should().BeEquivalentTo(Finding.NoChainWidths);
        frame.HorizontalScale.Should().BeNull();
        frame.Segments.Should().BeEmpty();
    }

    [Fact]
    public void NonFiniteInput_IsDroppedAndReported()
    {
        var frame = FirmSectionFrameLogic.Decode(SampleBlock(
            extraTicks: [double.NaN],
            extraTexts: [(double.PositiveInfinity, "1.00")]));

        frame.Findings.Should().Equal(Finding.InvalidInput);
        frame.Segments.Should().HaveCount(SampleWidthTexts.Length);
    }

    [Theory]
    [InlineData("3.60", 3.60, 2)]
    [InlineData(" 3.60 ", 3.60, 2)]
    [InlineData("3.60 m", 3.60, 2)]
    [InlineData("3.60מ'", 3.60, 2)]
    [InlineData("3.6", 3.6, 1)]
    [InlineData("12", 12, 0)]
    [InlineData("0.31", 0.31, 2)]
    public void WidthTexts_ParseInvariantlyWithTheirPrintedPrecision(string text, double expected, int decimals)
    {
        FirmSectionFrameLogic.TryParseWidthM(text, out var width, out var printedDecimals).Should().BeTrue();
        width.Should().Be(expected);
        printedDecimals.Should().Be(decimals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3,60")]
    [InlineData("-3.60")]
    [InlineData("0")]
    [InlineData("3.60x5")]
    [InlineData("ראה פרט")]
    [InlineData(null)]
    public void TextsThatAreNotAWidth_DoNotParse(string? text) =>
        FirmSectionFrameLogic.TryParseWidthM(text, out _, out _).Should().BeFalse();

    // MahodCivilNet 2026 drew this block live (2026-10-28, Road34 station 100, TR_GISHA): the ticks are the vertices
    // of the two chain polylines, a turned number (1.60, 1.91, 1.00) sits at the middle of its narrow width, and its
    // short leader at -6.1 / -11 is not a tick. Text X is the middle of each text's extents.
    private static readonly double[] LiveTicks = [-16.385, -11.5, -10.5, -6.9, -5.3, -3.3, 0, 3.3, 5.3, 7.213, 10.813, 11.813, 20];

    private static readonly (double X, string Text)[] LiveTexts =
    [
        (-13.95, "4.88"), (-11.0, "1.00"), (-8.71, "3.60"), (-6.1, "1.60"), (-4.31, "2.00"), (-1.66, "3.30"),
        (1.64, "3.30"), (4.29, "2.00"), (6.257, "1.91"), (9.0, "3.60"), (11.313, "1.00"), (15.9, "8.19"),
    ];

    private static readonly (double Elevation, double Y)[] LiveRuler = [(93, 0.133), (98, 5.133), (103, 10.133)];

    [Fact]
    public void ALiveFirmBlock_DecodesOneToOne_WithEveryPrintedWidth()
    {
        var frame = FirmSectionFrameLogic.Decode(new Input(0, LiveRuler, LiveTicks, LiveTexts));

        frame.HasHorizontalFrame.Should().BeTrue();
        frame.HorizontalScale!.Value.Should().BeApproximately(1.0, 0.002);
        frame.VerticalScale!.Value.Should().BeApproximately(1.0, 1e-9);
        frame.ElevationOf(5.762)!.Value.Should().BeApproximately(98.629, 0.001, "the road at the axis, 98.76 printed with the band offset");
        frame.Segments.Select(s => s.WidthText).Should().Equal(
            "4.88", "1.00", "3.60", "1.60", "2.00", "3.30", "3.30", "2.00", "1.91", "3.60", "1.00", "8.19");
        frame.Findings.Should().NotContain(Finding.ScaleInconsistent);
    }

    [Fact]
    public void TheLeadersAlone_AsTheFirstReaderTookThem_BreakTheScale_AndNothingIsNamed()
    {
        // The first reader took only two-vertex verticals: the four leaders, never the chain polylines.
        var leaders = new[] { -11.0, -6.1, 6.257, 11.313 };

        var frame = FirmSectionFrameLogic.Decode(new Input(0, LiveRuler, leaders, LiveTexts));

        frame.Findings.Should().Contain(Finding.ScaleInconsistent);
        frame.Segments.Should().BeEmpty("a misplaced name is worse than none");
    }
}
