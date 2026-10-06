using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

/// <summary>
/// Pure logic behind the section findings of the independent 1.3.9 audit (30/09):
/// SEC-B3 content band, SEC-B4 closed chain, SEC-M2/M3/m3 drawing texts, SEC-M5
/// bus-lane naming, SEC-m1 blocker wording and SEC-B1 ROW disclosure.
/// </summary>
public class SectionReadabilityLogicTests
{
    // ------------------------------------------------------------ SEC-B3 band

    [Fact]
    public void Band_IsDrawnContentPlusOneMargin_NotTheOldPaddedTower()
    {
        // STA-42676-like: EG 291.4..292.5, FG 291.3..292.4, deepest utility 290.26,
        // road arrows only (3.50 m headroom).
        var inputs = new SectionViewElevationBandLogic.Inputs(
            291.4, 292.5, 291.3, 292.4, 290.26, 292.11,
            SectionViewElevationBandLogic.ArrowHeadroomFor(new[] { "road", "road" }));

        SectionViewElevationBandLogic.TryCompute(inputs, 40, out var band, out var error)
            .Should().BeTrue(error);
        band!.Min.Should().BeApproximately(289.7, 1e-9, "290.26 - 0.5 rounded down to 0.1");
        band.Max.Should().BeApproximately(296.5, 1e-9, "292.5 + 3.5 + 0.5");
        band.Capped.Should().BeFalse();

        // 1.3.9: floor(291.4 - 5) - 2 .. ceil(292.5 + 5) + 6 = 284 .. 304 (20 m).
        (band.Max - band.Min).Should().BeLessThan(7.0);
    }

    [Fact]
    public void Band_AddsArrowHeadroomOnlyWhenArrowsAreDrawn_ForTheTallestKindPresent()
    {
        SectionViewElevationBandLogic.ArrowHeadroomFor(Array.Empty<string>()).Should().Be(0);
        SectionViewElevationBandLogic.ArrowHeadroomFor(new[] { "bike" })
            .Should().Be(SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(
                SectionTrafficDirectionAnnotationLogic.StripKind.Bike));
        SectionViewElevationBandLogic.ArrowHeadroomFor(new[] { "road", "bus" })
            .Should().Be(SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(
                SectionTrafficDirectionAnnotationLogic.StripKind.Bus));
        SectionViewElevationBandLogic.ArrowHeadroomFor(new[] { "unknown-kind" })
            .Should().Be(SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(
                SectionTrafficDirectionAnnotationLogic.StripKind.Bus),
                "an unknown kind must never clip an arrow");

        var noArrows = new SectionViewElevationBandLogic.Inputs(100.2, 101.0, null, null, null, null, 0);
        SectionViewElevationBandLogic.TryCompute(noArrows, 40, out var band, out _).Should().BeTrue();
        band!.Min.Should().BeApproximately(99.7, 1e-9);
        band.Max.Should().BeApproximately(101.5, 1e-9);
    }

    [Fact]
    public void Band_NeverInventsADepth_AndStillCapsAtTheMaximumSpan()
    {
        var inputs = new SectionViewElevationBandLogic.Inputs(100, 101, 100, 101, 40, 40, 3.5);
        SectionViewElevationBandLogic.TryCompute(inputs, 40, out var band, out _).Should().BeTrue();
        band!.Capped.Should().BeTrue();
        (band.Max - band.Min).Should().BeApproximately(40, 1e-9);

        new Action(() => SectionViewElevationBandLogic.TryCompute(
                inputs with { LowestUtility = double.NaN }, 40, out _, out _))
            .Should().NotThrow();
        SectionViewElevationBandLogic.TryCompute(
                inputs with { LowestUtility = double.NaN }, 40, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("utility");
    }

    [Fact]
    public void BandEvidence_RoundTripsAndRejectsTamperingOrLegacyRecords()
    {
        var inputs = new SectionViewElevationBandLogic.Inputs(
            285.123456789, 286.3, 285.0, 286.25, 284.27, 284.27, 3.5);
        SectionViewElevationBandLogic.TryCompute(inputs, 40, out var band, out _).Should().BeTrue();

        SectionViewElevationBandLogic.TryParseAndRecompute(band!.Evidence, 40, out var parsed, out var error)
            .Should().BeTrue(error);
        parsed!.Min.Should().Be(band.Min);
        parsed.Max.Should().Be(band.Max);

        var tampered = band.Evidence.Replace("|max=", "|max=1");
        SectionViewElevationBandLogic.TryParseAndRecompute(tampered, 40, out _, out error).Should().BeFalse();

        SectionViewElevationBandLogic.TryParseAndRecompute(null, 40, out _, out error).Should().BeFalse();
        error.Should().Contain("re-apply");
    }

    // ------------------------------------------------------------ SEC-B4 chain

    [Fact]
    public void Chain_Sta12145_GapsBetweenNamedStripsCloseToTheExtent()
    {
        // The dialog offsets of STA-12145 (live-2909/06): 11 named strips, 7 gaps,
        // internal breaks at -8.45/-8.36 and 0.36/0.47.
        var marks = new[]
        {
            -14.22, -11.09, -10.78, -9.78, -8.78, -8.45, -8.36, -8.28, -5.18, -4.88, -4.04,
            -2.03, -1.80, 0.24, 0.36, 0.47, 0.94, 3.32, 3.62, 6.72, 6.89, 8.04, 11.24,
        };
        var named = new (double, double)[]
        {
            (-14.22, -11.09), (-10.78, -9.78), (-9.78, -8.78), (-8.28, -5.18), (-4.88, -4.04),
            (-4.04, -2.03), (-1.80, 0.24), (0.94, 3.32), (3.62, 6.72), (6.89, 8.04), (8.04, 11.24),
        };

        SectionDimensionChainLogic.TryBuild(marks, named, out var chain, out var error).Should().BeTrue(error);
        chain!.OverallWidth.Should().BeApproximately(25.46, 1e-9);
        chain.Pieces.Sum(p => p.Width).Should().BeApproximately(25.46, 1e-9);
        chain.Pieces.Where(p => !p.IsNamedStrip)
            .Select(p => SectionDrawingTextLogic.GapWidthText(p.Width))
            .Should().Equal("0.31", "0.50", "0.30", "0.23", "0.70", "0.30", "0.17");
        chain.Pieces.Zip(chain.Pieces.Skip(1), (a, b) => b.From - a.To)
            .Should().OnlyContain(gap => Math.Abs(gap) < 1e-9, "the chain is contiguous");
    }

    [Fact]
    public void Chain_RefusesSpansThatDoNotSitOnMarksOrOverlap()
    {
        var marks = new[] { 0.0, 1.0, 4.0, 4.3 };
        SectionDimensionChainLogic.TryBuild(marks, new[] { (1.0, 3.9) }, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("dimension marks");
        SectionDimensionChainLogic.TryBuild(marks, new[] { (0.0, 4.0), (1.0, 4.3) }, out _, out error)
            .Should().BeFalse();
        error.Should().Contain("overlaps");
        SectionDimensionChainLogic.TryBuild(new[] { 1.0 }, Array.Empty<(double, double)>(), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void NewHeaderRows_StartBetweenStripNamesAndTitle()
    {
        var top = new SectionAnnotationPlacementLogic.Point(10, 100);
        SectionAnnotationPlacementLogic.OverallWidthLabelPosition(top).Y
            .Should().BeGreaterThan(SectionAnnotationPlacementLogic.StripLabelPosition(top).Y);
        SectionAnnotationPlacementLogic.LegendPosition(top).Y
            .Should().BeGreaterThan(SectionAnnotationPlacementLogic.AxisLabelPosition(top).Y)
            .And.BeLessThan(SectionAnnotationPlacementLogic.TitlePosition(top).Y);
        SectionAnnotationPlacementLogic.LegendPosition(top).X.Should().Be(10);
    }

    // ------------------------------------------------------ SEC-M2 / M3 / m3

    [Fact]
    public void DrawingTexts_SayWhatThePointIs_AndNeverClaimADiameterOrLevelType()
    {
        SectionDrawingTextLogic.DatumText(285.354).Should().Be("רום קרקע קיימת בציר: 285.35");
        SectionDrawingTextLogic.IsDatumText("רום קרקע קיימת בציר: 285.35").Should().BeTrue();
        SectionDrawingTextLogic.UtilityLabel("תאורה", 284.271).Should().Be("תאורה רום 284.27");
        SectionDrawingTextLogic.UtilityLabel("ביוב", null).Should().Be("ביוב (עומק לא מוגדר)");
        SectionDrawingTextLogic.LegendUtilities.Should().Contain("לא קוטר").And.Contain("לא ידוע");
        SectionDrawingTextLogic.LegendSurfaces.Should().Contain("ירוק מקווקו").And.Contain("אדום רציף");
        SectionDrawingTextLogic.UtilityMarkerRadius.Should().BeLessThan(0.6, "the 1.2 m circle read as a pipe");
    }

    [Theory]
    [InlineData(12145.43, "12+145.43")]
    [InlineData(42675.85, "42+675.85")]
    [InlineData(999.999, "1+000.00")]
    [InlineData(5.2, "0+005.20")]
    public void Chainage_UsesTheIsraeliFormat(double station, string expected)
    {
        SectionDrawingTextLogic.Chainage(station).Should().Be(expected);
    }

    [Fact]
    public void Title_NamesAlignmentAndChainage_OnlyFromGivenValues()
    {
        SectionDrawingTextLogic.Title("STA-12145", "2000", 12145.43)
            .Should().Be("חתך STA-12145 · תוואי 2000 · 12+145.43");
        SectionDrawingTextLogic.Title("CL-7CFD", null, null).Should().Be("חתך CL-7CFD");
    }

    // ------------------------------------------------------------ SEC-M5

    private static readonly (double, string, string)[] BusLaneMarks =
    {
        (-8.95, "curb", "אבן שפה"),
        (-5.75, "lane", "קו נת\"צ"),
        (-5.45, "lane", "קו נת\"צ"),
        (-2.20, "lane", "קו נתיב"),
        (1.00, "lane", "קו נתיב"),
    };

    // 30.09 (Arthur): a lane beside a bus-lane line is named like any lane — never a block for 73 strips in 28 sections —
    // and listed for the engineer's review (it may be the bus lane itself; the strip-names dialog renames it).
    [Fact]
    public void StripNextToABusLaneLine_IsNamedAndListedForReview()
    {
        var analysis = AnalyzePresentationCoverage(BusLaneMarks);

        analysis.StripLabels.Should().Equal((-8.95, -5.75, "נתיב נסיעה"), (-5.45, -2.20, "נתיב נסיעה"), (-2.20, 1.00, "נתיב נסיעה"));
        analysis.UnresolvedSpans.Should().BeEmpty();
        analysis.BusLaneEdgeStrips.Should().Equal((-8.95, -5.75, "נתיב נסיעה"), (-5.45, -2.20, "נתיב נסיעה"));
        IsBusLaneLineMark("lane", "קו נתיב").Should().BeFalse();
        IsBusLaneLineMark("curb", "קו נת\"צ").Should().BeFalse();
    }

    [Fact]
    public void StripNextToABusLaneLine_KeepsAnEngineerApprovedName()
    {
        var analysis = AnalyzePresentationCoverage(BusLaneMarks,
            approvedOverrides: new[] { new SpanLabelOverride(-7.0, "נת\"צ", "manual-profile", "approved") });

        analysis.StripLabels.Should().Contain((-8.95, -5.75, "נת\"צ"));
        analysis.UnresolvedSpans.Should().BeEmpty();
        analysis.BusLaneEdgeStrips.Should().Equal((-5.45, -2.20, "נתיב נסיעה"));
    }

    // ------------------------------------------------------------ SEC-m1 / SEC-B1

    [Fact]
    public void BlockerSummary_UsesHebrewWordingAndKeepsCodesOut()
    {
        var finding = new DeliveryFinding
        {
            Code = SectionPlanBlockerSummaryLogic.GeometryUnsupportedCode,
            Domain = "sections",
            Severity = FindingSeverity.Error,
            Title = "x",
            Message = "role=plan-region; kind=sidewalk; entity=Hatch; layer=6422-HA-MODEL-NATAZ|HW_HA_SIDEWALK; " +
                      "handle=8BB299/2623A6; reason=Ring Self-intersection at or near point (203737.06, 648459.91).",
            AffectedRecordIds = { "cl-1" },
        };
        var text = SectionPlanBlockerSummaryLogic.Describe(
            new[] { finding }, id => id == "cl-1" ? "STA-41398" : null, anyRecordReady: false);

        text.Should().StartWith("התכנון חסום במקור: ")
            .And.Contain("1 הצללת מדרכה בשכבה HW_HA_SIDEWALK אינה אזור סגור תקין")
            .And.Contain("חיתוך עצמי ליד (203737.06, 648459.91)")
            .And.Contain("החתכים STA-41398 חסומים")
            .And.Contain("יש לבדוק את ההצללה")
            .And.NotContain("SEC-")
            .And.NotContain("2623A6");
        SectionPlanBlockerSummaryLogic.ReasonKey("Hatch loop 0 has unsupported flags External, NotClosed.")
            .Should().Be("open");
    }

    [Theory]
    [InlineData("row", "authoritative", null)]
    [InlineData("plan-mark-extents", "authoritative", "לא נמצאה במקור המאושר")]
    [InlineData("plan-mark-extents", "nocandidates", "אין מקור ROW")]
    [InlineData("plan-mark-extents", "ambiguous", "טרם אושר")]
    [InlineData("cl-extents", "nocandidates", "קצות קו ה-CL")]
    public void BoundaryDisclosure_NeverLetsASectionEndSilentlyAtTheSidewalk(
        string boundary, string authority, string? expected)
    {
        var text = SectionPlanBlockerSummaryLogic.DescribeBoundary(boundary, authority);
        if (expected == null) text.Should().BeNull();
        else text.Should().Contain(expected);
    }
}
