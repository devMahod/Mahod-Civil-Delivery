using System.Linq;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Crossing = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Crossing;

namespace MahodAI.Core.Tests.CivilDelivery;

/// <summary>
/// The office's typical-section sheet (2026-11-02, MHD-HW-000-IC_00000-DD-500X): its SHX encoding, titles, section
/// numbers and pavement callouts, and the lanes 6403's plan proves at ROAD-40 80160.
/// </summary>
public class OfficeTypicalSheetTests
{
    [Theory]
    [InlineData("שול", "auk")]
    [InlineData("נתיב", "b,hc")]
    [InlineData("מסעה חדשה (כולל אגו\"מ)-כביש 40", "nxgv jsav (fukk tdu\"n)-fcha 04")]
    [InlineData("זכות הדרך", "zfu, vsrl")]
    [InlineData("חת' 3985", "j,w 5893")]
    public void ShxHebrew_WritesTheOfficesOwnEncoding(string logical, string stored) =>
        Assert.Equal(stored, OfficeTypicalSheet.ShxHebrew(logical));

    [Theory]
    [InlineData("ROAD-40", "כביש 40")]
    [InlineData("RD-3933", "כביש 3933")]
    [InlineData("RAMP-13", "רמפה מס' 13")]
    [InlineData("40-st-01-1", "40-st-01-1")]
    public void RoadName_FromTheAlignment(string alignment, string name) => Assert.Equal(name, OfficeTypicalSheet.RoadName(alignment));

    [Fact]
    public void ANewSheet_ContinuesTheNumbering()
    {
        Assert.Equal(1, OfficeTypicalSheet.FirstFreeSheetNumber(new[] { "Model", "Layout1" }, "MHD-TYP"));
        Assert.Equal(2, OfficeTypicalSheet.FirstFreeSheetNumber(new[] { "Layout1", "MHD-TYP-01" }, "MHD-TYP"));
        Assert.Equal(4, OfficeTypicalSheet.FirstFreeSheetNumber(new[] { "MHD-TYP-01", "MHD-TYP-03", "MHD-TYP-01_2", "MHD-TYPX-09" }, "MHD-TYP"));
    }

    [Fact]
    public void RoadTitle_SaysWhetherTheStructureHasAgum()
    {
        Assert.Equal("מסעה חדשה (כולל אגו\"מ)-כביש 40", OfficeTypicalSheet.RoadTitle("ROAD-40", true));
        Assert.Equal("מסעה חדשה (ללא אגו\"מ)-רמפה מס' 13", OfficeTypicalSheet.RoadTitle("RAMP-13", false));
    }

    [Theory]
    [InlineData(79700.0, 3985L)]
    [InlineData(79220.0, 3961L)]
    [InlineData(26017.9, 1300L)]
    [InlineData(800001.0, 40000L)]
    public void SectionNumber_IsTheFirmProgramsNumbering(double station, long number) =>
        Assert.Equal(number, OfficeTypicalSheet.SectionNumber(station));

    [Fact]
    public void Describe_RD40Structure_ReadsTheCodesAndJoinsEqualBaseCourses()
    {
        // 6403 RD-40 at 79650, subassembly 200: thicknesses measured from its shapes
        var rows = OfficeTypicalSheet.Describe(new[]
        {
            ("ASF-TAMA-5-70", 0.05), ("ASF-5-70", 0.05), ("ASF-5-68", 0.05), ("ASF-6-68", 0.06), ("ASF-5-68", 0.05),
            ("AGUM", 0.15), ("MAZA-A", 0.12), ("MAZ-A", 0.12), ("MAZA-B", 0.19),
        });
        Assert.Equal(8, rows.Count);
        Assert.Equal("5 ס\"מ שכבת אספלט מסוג תאמ\"א", rows[0].Up);
        Assert.Equal("ביטומן מסוג PG70-10", rows[0].Down);
        Assert.Equal("6 ס\"מ שכבת אספלט תא\"צ", rows[3].Up);
        Assert.Equal("ביטומן מסוג PG68-10", rows[3].Down);
        Assert.Equal("15 ס\"מ אגו\"מ", rows[5].Up);
        Assert.Equal("2x12 ס\"מ מצע סוג א'", rows[6].Up);
        Assert.Equal(2, rows[6].Count);
        Assert.Equal("19 ס\"מ מצע סוג ב'", rows[7].Up);
        Assert.Equal(("סה\"כ עובי", "מבנה", "84 ס\"מ"), OfficeTypicalSheet.Total(0.84));
    }

    [Fact]
    public void CodedThickness_IsTheCentimetresInTheCode()
    {
        Assert.Equal(0.05, OfficeTypicalSheet.CodedThicknessM("ASF-TAMA-5-70")!.Value, 6);
        Assert.Equal(0.06, OfficeTypicalSheet.CodedThicknessM("kirzuf-ASF-6-68")!.Value, 6);
        Assert.Null(OfficeTypicalSheet.CodedThicknessM("MAZA-A"));
    }

    [Fact]
    public void Symbols_AllParse()
    {
        foreach (var name in TypicalSymbolData.All.Keys)
            Assert.NotEmpty(OfficeTypicalSheet.Symbol(name));
    }

    [Fact]
    public void Road40At80160_ReadsTheOfficesPanel_FourLanesWithTheHovLane()
    {
        // 6403's plan (4NATIV-GM-M44) and its marking plan (SM-M38) cut at ROAD-40 80160, as the tool reads them.
        var c = new[]
        {
            Line(-19.50, "HW-SHLD", "shoulder"), Line(-16.00, "HW-TRWY", "edge"), Line(-16.00, "TR-MARK-YLW-807", "edgeline"),
            Line(-12.55, "TR-MARK-YLW-3-3", "hovline"), Line(-12.25, "TR-MARK-YLW-3-3", "hovline"),
            Line(-8.80, "TR-MARK-WHT-801-3-12", "marking"), Line(-5.20, "TR-MARK-WHT-801-3-12", "marking"),
            Line(-1.70, "TR-MARK-WHT-803", "edgeline"), Line(-1.60, "HW-TRWY-UP", "edge"), Line(-1.50, "TR-MARK-WHT-803", "edgeline"),
            Line(-0.40, "HW-EOP", "edge"), Line(0.40, "HW-EOP", "edge"),
            Line(1.50, "TR-MARK-WHT-803", "edgeline"), Line(1.60, "HW-TRWY-UP", "edge"), Line(1.70, "TR-MARK-WHT-803", "edgeline"),
            Line(5.20, "TR-MARK-WHT-801-3-12", "marking"), Line(8.80, "TR-MARK-WHT-801-3-12", "marking"),
            Line(12.25, "TR-MARK-YLW-3-3", "hovline"), Line(12.55, "TR-MARK-YLW-3-3", "hovline"),
            Line(16.00, "HW-TRWY", "edge"), Line(16.00, "TR-MARK-YLW-807", "edgeline"), Line(19.50, "HW-SHLD", "shoulder"),
            Line(20.10, "HW-BIKE-LANE", "bike"), Line(21.85, "TR-MARK-WHT-1-1", "marking"), Line(23.60, "HW-BIKE-LANE", "bike"),
            Line(24.60, "HW-CURB-GRDN", "curb"),
        };
        var section = DraftedTypicalSectionLogic.Build(c, null, 30);
        var read = string.Join(" | ", section.Strips.Select(s => $"{s.WidthM:0.00} {s.Name}"));
        Assert.Equal(
            "3.50 שול | 3.60 נר\"ת | 3.60 נתיב | 3.60 נתיב | 3.60 נתיב | 1.20 שול | 0.80  | 1.20 שול | 3.60 נתיב | 3.60 נתיב | 3.60 נתיב | 3.60 נר\"ת | 3.50 שול | 0.60  | 3.50 שביל אופניים | 1.00 ",
            read);
        var median = Assert.Single(section.Groups);
        Assert.Equal(3.20, median.ToM - median.FromM, 6);
    }

    [Fact]
    public void LaneKinds_FromTheMarkingLayers()
    {
        Assert.Equal(DraftedTypicalSectionLogic.Kind.HovLine, DraftedTypicalSectionLogic.ClassifyLayer("TR-MARK-YLW-3-3"));
        Assert.Equal(DraftedTypicalSectionLogic.Kind.EdgeLine, DraftedTypicalSectionLogic.ClassifyLayer("TR-MARK-YLW-807"));
        Assert.Equal(DraftedTypicalSectionLogic.Kind.EdgeLine, DraftedTypicalSectionLogic.ClassifyLayer("TR-MARK-WHT-803"));
        Assert.Equal(DraftedTypicalSectionLogic.Kind.Marking, DraftedTypicalSectionLogic.ClassifyLayer("TR-MARK-WHT-801-3-12"));
    }

    private static Crossing Line(double offset, string layer, string kind) => new(offset, kind, layer, "test");
}
