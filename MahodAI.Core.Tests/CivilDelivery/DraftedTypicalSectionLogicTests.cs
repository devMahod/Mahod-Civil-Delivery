using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Crossing = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Crossing;
using Kind = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Kind;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;
using PlacedObject = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.PlacedObject;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class DraftedTypicalSectionLogicTests
{
    // ------------------------------------------------------------------------------------------------ plan axis

    [Fact]
    public void PlanAxis_StationAndSignedOffset_FollowTheDrawnDirection_RightPositiveLikeCivil()
    {
        var axis = new PlanAxis("AX-40", new[] { new P2(0, 0), new P2(100, 0), new P2(100, 50) });

        axis.TryStationOffset(new P2(30, -4), out var station, out var offset, out var tangent).Should().BeTrue();
        station.Should().BeApproximately(30, 1e-9);
        offset.Should().BeApproximately(4, 1e-9, "below an eastward axis is its right side");
        tangent.Should().BeApproximately(0, 1e-9);

        axis.TryStationOffset(new P2(97, 20), out station, out offset, out tangent).Should().BeTrue();
        station.Should().BeApproximately(120, 1e-9);
        offset.Should().BeApproximately(-3, 1e-9, "west of a northward axis is its left side");
        tangent.Should().BeApproximately(90, 1e-9);
    }

    [Fact]
    public void PlanAxis_PointBeyondItsEnds_IsNotOnTheRoad()
    {
        var axis = new PlanAxis("AX", new[] { new P2(0, 0), new P2(100, 0) });
        axis.TryStationOffset(new P2(-5, 1), out _, out _, out _).Should().BeFalse();
        axis.TryStationOffset(new P2(105, 1), out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void PlanAxis_PointAt_IsTheInverseOfStationOffset()
    {
        var axis = new PlanAxis("AX", new[] { new P2(10, 10), new P2(70, 90) });
        var p = axis.PointAt(40, -2.5);
        axis.TryStationOffset(p, out var station, out var offset, out _).Should().BeTrue();
        station.Should().BeApproximately(40, 1e-9);
        offset.Should().BeApproximately(-2.5, 1e-9);
    }

    [Theory]
    [InlineData("HW-DD-TT-CL-MAAKAF|AX-40-st-01-1", true)]
    [InlineData("AX_ROAD", true)]
    [InlineData("GM|cl", true)]
    [InlineData("SM|CENTERLINE1", true)]
    [InlineData("EV|CL_Sect", false)]
    [InlineData("CL_Slope", false)]
    [InlineData("HW-TRWY", false)]
    [InlineData("", false)]
    public void AxisLayers_AreTheOfficeAxes_NotTheEvAnnotation(string layer, bool expected) =>
        PlanAxis.IsAxisLayer(layer).Should().Be(expected);

    // -------------------------------------------------------------------------------------------- classification

    [Theory]
    [InlineData("HW-IC_4NATIV-GM-M44|HW-TRWY", Kind.Edge)]
    [InlineData("HW-IC_4NATIV-GM-M44|HW-TRWY-UP", Kind.Edge)]
    [InlineData("GM|HW-EOP", Kind.Edge)]
    [InlineData("GM|HW-SHLD-UP", Kind.Shoulder)]
    [InlineData("GM|TR-CURB3_MIDRAHA-17", Kind.Curb)]
    [InlineData("GM|HW-CURB-GRDN", Kind.Curb)]
    [InlineData("GM|HW-SDWK", Kind.Sidewalk)]
    [InlineData("GM|HW-GARDEN", Kind.Garden)]
    [InlineData("GM|HW-BIKE-LANE", Kind.Bike)]
    [InlineData("TT|TR-MARK-YLW-807", Kind.EdgeLine)]
    [InlineData("SM|TR-MARK-WHT-801", Kind.Marking)]
    [InlineData("TT|TR-FENCE-T3W2-GADER", Kind.Fence)]
    [InlineData("GM|HW-BARI-FLEX-H1W3", Kind.Barrier)]
    [InlineData("TT|0-TT-S1-WORK-SOFI", Kind.Work)]
    [InlineData("TT|0-TT-S1-NITUV", Kind.Routing)]
    [InlineData("GM|PGVUL", Kind.Row)]
    public void PlanLayers_ReadByTheOfficeConvention(string layer, string expected) =>
        DraftedTypicalSectionLogic.ClassifyLayer(layer).Should().Be(expected);

    [Theory]
    [InlineData("SR-6191-4-MHD|1509_plt")]
    [InlineData("SM|TR-MARK-815")]
    [InlineData("SM|d-811")]
    [InlineData("TT|TR-MARK-WHT-811-ZEBRA")]
    [InlineData("EV|CL_FL_Ditch")]
    [InlineData("GM|HW-DIM")]
    [InlineData("ST|S-GIRDER-D")]
    [InlineData("TT|0-TXT")]
    public void OtherLayers_AreNotPartOfATypicalSection(string layer) =>
        DraftedTypicalSectionLogic.ClassifyLayer(layer).Should().BeNull();

    [Theory]
    [InlineData("H-h_seker tree|0-ST_shimur", "Simbol", "tree")]
    [InlineData("H-h_seker tree|0-ST_krita", "Simbol", "tree")]
    [InlineData("0", "LA-TREECS-02", "tree")]
    [InlineData("EL|TEURA-POLE", "pole", "light")]
    [InlineData("TT|TR-SIGN-BL", "930", "sign")]
    [InlineData("SR|HEZIM", "arrow", null)]
    public void Objects_AreReadByLayerOrBlockName(string layer, string block, string? expected) =>
        DraftedTypicalSectionLogic.ClassifyObject(layer, block).Should().Be(expected);

    // ----------------------------------------------------------------------------------------------- the section

    private static Crossing C(double offset, string kind) => new(offset, kind, kind, "test");

    [Fact]
    public void AnUrbanRoad_ReadsAsLanesCurbsAndSidewalks()
    {
        var crossings = new[]
        {
            C(-12.6, Kind.Sidewalk), C(-9.65, Kind.Curb), C(-9.5, Kind.Edge), C(-6.5, Kind.Marking), C(-3.25, Kind.Marking),
            C(0, Kind.Marking), C(3.25, Kind.Marking), C(6.5, Kind.Marking), C(9.5, Kind.Edge), C(9.65, Kind.Curb),
            C(12.6, Kind.Sidewalk), C(40, Kind.Edge), // beyond the half-width: not in the section
        };

        var section = DraftedTypicalSectionLogic.Build(crossings, null, 20);

        var lane = "נתיב";
        section.Strips.Select(s => s.Name).Should().Equal("מדרכה", null, lane, lane, lane, lane, lane, lane, null, "מדרכה");
        section.Breaks.Should().NotContain(b => b.Kind == "axis", "the centre marking already stands on the axis");
        section.Strips.First().SurfaceKind.Should().Be("raised");
        section.Strips.Single(s => s.FromM == -6.5).SurfaceKind.Should().Be("road");
        section.MaxM.Should().Be(12.6);
        section.NamedCount.Should().Be(8);
        section.Findings.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateLines_MergeAndWorkZonesPair()
    {
        var crossings = new[]
        {
            C(-7.0, Kind.Edge), C(-6.95, Kind.Edge), C(7.0, Kind.Edge),
            C(-5, Kind.Work), C(-1, Kind.Work), C(3, Kind.Work), C(6, Kind.Work),
            C(-8, Kind.Fence), C(-7.9, Kind.Fence), C(8, Kind.Barrier),
        };

        var section = DraftedTypicalSectionLogic.Build(crossings, null, 20);

        section.Breaks.Count(b => b.Kind == Kind.Edge).Should().Be(2, "-7.0 and -6.95 are one line drawn twice");
        section.Strips.Should().ContainSingle().Which.Name.Should().BeNull("two edges with no lane line prove no lanes");
        section.Breaks.Should().NotContain(b => b.Kind == "axis", "the chain runs through the axis, as the office's does");
        section.Findings.Should().Contain(f => f.Contains("שפות מיסעה"));
        section.Zones.Should().HaveCount(2);
        section.Zones[0].Name.Should().Be("תחום עבודה");
        section.Fences.Should().ContainSingle();
        section.Barriers.Should().ContainSingle();
    }

    [Fact]
    public void AnOddWorkLine_IsReportedNotGuessed()
    {
        var section = DraftedTypicalSectionLogic.Build(new[] { C(-5, Kind.Edge), C(5, Kind.Edge), C(2, Kind.Work) }, null, 20);
        section.Zones.Should().BeEmpty();
        section.Findings.Should().ContainSingle(f => f.Contains("תחום העבודה"));
    }

    [Fact]
    public void NoRoadLine_LeavesOnlyTheAxis_AndSaysSo()
    {
        var section = DraftedTypicalSectionLogic.Build(Array.Empty<Crossing>(), null, 20);
        section.Strips.Should().BeEmpty();
        section.Breaks.Should().ContainSingle().Which.Kind.Should().Be("axis");
        section.Findings.Should().ContainSingle();
    }

    [Fact]
    public void ObjectsOutsideTheHalfWidth_AreLeftOut()
    {
        var objects = new[]
        {
            new PlacedObject(-12, DraftedTypicalSectionLogic.ObjectKind.Tree, "0-ST_shimur", "Simbol"),
            new PlacedObject(30, DraftedTypicalSectionLogic.ObjectKind.Tree, "0-ST_shimur", "Simbol"),
        };
        var section = DraftedTypicalSectionLogic.Build(new[] { C(-5, Kind.Edge), C(5, Kind.Edge) }, objects, 20);
        section.Objects.Should().ContainSingle().Which.OffsetM.Should().Be(-12);
    }

    [Theory]
    [InlineData(Kind.Edge, Kind.Edge, 3.5, null)]        // two carriageway edges: a ramp, an island or two roads
    [InlineData(Kind.Edge, Kind.Edge, 5.78, null)]       // 6403 800500: had been called a lane
    [InlineData(Kind.Edge, Kind.Edge, 10.0, null)]
    [InlineData(Kind.Marking, Kind.Marking, 3.6, "נתיב")]
    [InlineData(Kind.Edge, Kind.Marking, 3.0, "נתיב")]
    [InlineData(Kind.Marking, Kind.Marking, 5.78, null)] // wider than a lane: a line is missing
    [InlineData(Kind.Marking, Kind.Marking, 2.2, null)]
    [InlineData(Kind.EdgeLine, Kind.Marking, 3.6, "נתיב")]
    [InlineData(Kind.EdgeLine, Kind.Edge, 3.5, "שול")]   // outside the yellow 807 line
    [InlineData(Kind.EdgeLine, Kind.Routing, 1.1, "שול")]
    [InlineData(Kind.EdgeLine, Kind.Routing, 3.3, "נתיב זמני")]
    [InlineData(Kind.Edge, Kind.Shoulder, 2.5, "שול")]
    [InlineData(Kind.Curb, Kind.Sidewalk, 2.0, "מדרכה")]
    [InlineData(Kind.Curb, Kind.Garden, 3.0, "גינון")]
    [InlineData(Kind.Curb, Kind.Bike, 2.0, "שביל אופניים")]
    [InlineData(Kind.Routing, Kind.Routing, 3.25, "נתיב זמני")]
    [InlineData(Kind.Curb, Kind.Curb, 3.0, null)]
    [InlineData(Kind.Edge, Kind.Curb, 0.1, null)]
    [InlineData(Kind.Fence, Kind.Edge, 3.0, null)]
    public void StripNames_ComeFromTheirBounds(string left, string right, double width, string? expected) =>
        DraftedTypicalSectionLogic.NameOf(left, right, width).Should().Be(expected);

    [Fact]
    public void TwoEdges_AreACarriageway_OnlyWhereTheCorridorPavesThem()
    {
        DraftedTypicalSectionLogic.NameOf(Kind.Edge, Kind.Edge, 10.81, paved: true).Should().Be("מיסעה");
        DraftedTypicalSectionLogic.NameOf(Kind.Edge, Kind.Edge, 10.81, paved: false).Should().BeNull("an island between two roads");
        DraftedTypicalSectionLogic.NameOf(Kind.Edge, Kind.Edge, 3.5, paved: true).Should().Be("מיסעה", "never a lane without lines");
    }

    // --------------------------------------------------------------------------------------------- stage panels

    [Theory]
    [InlineData("HW-IC_EXST_TO4-GM-M44", 0, "מצב קיים")]
    [InlineData("HW-IC_4NATIV-GM-M44", 1, "תכנון")]
    [InlineData("host", 1, "תכנון")]
    [InlineData("TR-PD-TT-GM-S1", 11, "הסדרי תנועה — שלב 1")]
    [InlineData("TR-PD-TT-GM-S1-GEDERA", 11, "הסדרי תנועה — שלב 1")]
    [InlineData("TR-TT-DD-GM-S0-M34-HEFEZ", 10, "הסדרי תנועה — שלב 0")]
    [InlineData("SR-3933-VSC_Survey", 1, "תכנון")]
    public void Stages_ComeFromTheXrefName(string source, int order, string title) =>
        DraftedTypicalSectionLogic.StageOf(source).Should().Be((order, title));

    [Fact]
    public void Panels_KeepEachStagesLinesApart_AndShareTheObjects()
    {
        var crossings = new[]
        {
            new Crossing(-7, Kind.Edge, "HW-TRWY", "HW-IC_4NATIV-GM-M44"), new Crossing(7, Kind.Edge, "HW-TRWY", "HW-IC_4NATIV-GM-M44"),
            new Crossing(-3.3, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"), new Crossing(0, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"),
            new Crossing(3.3, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"),
            new Crossing(9, Kind.Fence, "TR-FENCE", "TR-PD-TT-GM-S1"),
            new Crossing(-2, Kind.Work, "x", "TR-PD-TT-GM-S1"),       // alone in a stage it still counts as content
            new Crossing(50, Kind.Edge, "HW-TRWY", "HW-IC_EXST_TO4-GM-M44"), // outside the half-width: no existing panel
        };
        var tree = new PlacedObject(-12, DraftedTypicalSectionLogic.ObjectKind.Tree, "0-ST_shimur", "Simbol");

        var panels = DraftedTypicalSectionLogic.BuildPanels(crossings, new[] { tree }, 20, designPaved: (_, _) => true);

        panels.Select(p => p.Title).Should().Equal("תכנון", "הסדרי תנועה — שלב 1");
        panels[0].Section.Strips.Select(s => s.Name).Should().Equal("מיסעה");
        panels[1].Section.Strips.Select(s => s.Name).Should().Equal("נתיב זמני", "נתיב זמני");
        panels[1].Section.Fences.Should().ContainSingle();
        panels.Should().OnlyContain(p => p.Section.Objects.Count == 1, "a tree stands in every stage");
    }

    [Fact]
    public void NoLinesAtAll_IsOnePanelThatSaysSo()
    {
        var panels = DraftedTypicalSectionLogic.BuildPanels(Array.Empty<Crossing>(), null, 20);
        panels.Should().ContainSingle().Which.Section.Findings.Should().NotBeEmpty();
    }

    [Fact]
    public void TheOfficesStageOneSectionAt800500_ReadsLanesAndOneMedian()
    {
        // 6403 + TR-PD-TT-GM-S1, 40-st-01-1 at 800500 (2026-11-02): the routing lines the plan cut found. The office's
        // own typical section there: .60 .50 3.30 3.30 1.80 3.30 3.30 .50 .60 (barrier + shoulder = 1.10, no line between).
        var routing = new[] { -8.70, -7.60, -4.30, -0.90, 0.90, 4.20, 7.50, 8.60 }
            .Select(o => new Crossing(o, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"));

        var section = DraftedTypicalSectionLogic.Build(routing, null, 25);

        section.Strips.Select(s => s.WidthM.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal("1.10", "3.30", "3.40", "1.80", "3.30", "3.30", "1.10");
        section.Strips.Select(s => s.Name).Should().Equal(null, "נתיב זמני", "נתיב זמני", "מפרדה", "נתיב זמני", "נתיב זמני", null);
    }

    [Fact]
    public void AWideStripAroundTheAxis_IsNoMedian()
    {
        var section = DraftedTypicalSectionLogic.Build(new[] { C(-3.5, Kind.Edge), C(3.5, Kind.Edge) }, null, 20);
        section.Strips.Should().ContainSingle().Which.Name.Should().BeNull();
        section.Groups.Should().BeEmpty();
    }

    [Fact]
    public void AStripTheWorkZoneFills_IsTheWorkZone_NotAMissingLaneLine()
    {
        // 6403 stage 1 at ROAD-40 79650: routing at -8.76 and 12.18, the work site between work lines at -7.66 and 12.10.
        var section = DraftedTypicalSectionLogic.Build(new[]
        {
            C(-12.07, Kind.Routing), C(-8.76, Kind.Routing), C(-7.66, Kind.Work), C(12.10, Kind.Work), C(12.18, Kind.Routing),
        }, null, 30);

        section.Strips.Select(s => s.Name).Should().Equal("נתיב זמני", "תחום עבודה");
        section.Findings.Should().BeEmpty();
    }

    [Fact]
    public void TheCorridorsRoad_GoesUnderTheStageItBuilt()
    {
        // 6403 at 40-st-01-1 800500: the design's carriageways reach -27.93..5.14, the temporary road of stage 1 (the
        // corridor RD-40-ST-01 there) -8.60..8.60.
        var crossings = new[]
        {
            new Crossing(-27.93, Kind.Edge, "HW-TRWY-UP", "HW-IC_4NATIV-GM-M44"), new Crossing(-13.51, Kind.Edge, "HW-TRWY", "HW-IC_4NATIV-GM-M44"),
            new Crossing(0.04, Kind.Shoulder, "HW-SHLD", "HW-IC_4NATIV-GM-M44"),
            new Crossing(-8.60, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"), new Crossing(-0.90, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"),
            new Crossing(0.90, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"), new Crossing(8.60, Kind.Routing, "0-TT-S1-NITUV", "TR-PD-TT-GM-S1"),
        };
        var panels = DraftedTypicalSectionLogic.BuildPanels(crossings, null, 30);

        DraftedTypicalSectionLogic.PanelBuiltBy(panels, new[] { -8.62, -4.2, -0.91, 0.9, 4.2, 8.58 }).Should().Be(11);
        DraftedTypicalSectionLogic.PanelBuiltBy(panels, new[] { -27.93, -20.0, -13.5, 0.05 }).Should().Be(1);
        DraftedTypicalSectionLogic.PanelBuiltBy(panels, new[] { 50.0 }).Should().BeNull();
    }

    [Fact]
    public void Road40At79650_ReadsShouldersBarrierMedianAndCarriageways_NoLanesItCannotProve()
    {
        // 6403 ROAD-40 at 79650, the design XREF HW-IC_4NATIV-GM-M44 (2026-11-02 plan cut): edges, shoulders, a curb and
        // the bike path; no lane lines. The office's typical for 79560-79740 (DD-5001-02) dimensions 3.50 shoulder, the
        // lanes, "3.20 מפרדה" over 1.20 / 0.80 / 1.20, 3.50 shoulder, 0.40, 3.50 bike path.
        var design = new[]
        {
            C(-16.01, Kind.Shoulder), C(-12.51, Kind.Edge), C(-1.70, Kind.Edge), C(0.05, Kind.Edge), C(0.85, Kind.Edge),
            C(2.55, Kind.Edge), C(12.56, Kind.Edge), C(16.06, Kind.Edge), C(16.06, Kind.Shoulder), C(16.23, Kind.Curb),
            C(16.66, Kind.Bike), C(20.16, Kind.Bike),
        };

        var section = DraftedTypicalSectionLogic.Build(design, null, 30, paved: (from, to) => from >= -16.1 && to <= 16.1);

        section.Strips.Select(s => (s.WidthM.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), s.Name)).Should().Equal(
            ("3.50", "שול"), ("10.81", "מיסעה"), ("1.75", "שול"), ("0.80", (string?)null), ("1.70", "שול"),
            ("10.01", "מיסעה"), ("3.50", "שול"), ("0.17", null), ("0.43", null), ("3.50", "שביל אופניים"));
        section.Groups.Should().ContainSingle().Which.Should().Be(new DraftedTypicalSectionLogic.Zone(-1.70, 2.55, "מפרדה"));
        section.Barriers.Should().ContainSingle().Which.OffsetM.Should().BeApproximately(0.45, 1e-9);
        section.Strips.Should().NotContain(s => s.Name == "נתיב", "the plan has no lane line here");
        section.Findings.Where(f => f.Contains("מספר הנתיבים")).Should().HaveCount(2);
    }
}
