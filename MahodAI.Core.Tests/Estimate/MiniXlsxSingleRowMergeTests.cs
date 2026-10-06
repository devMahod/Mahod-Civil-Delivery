using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MiniXlsxSingleRowMergeTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static string Output() => Path.Combine(Path.GetTempPath(), "Mahod-scope-layout-" + Guid.NewGuid().ToString("N") + ".xlsx");
    private static MiniXlsx.Workbook Workbook()
    {
        var wb = new MiniXlsx.Workbook { RightToLeft = true };
        wb.Styles.Add(new MiniXlsx.Style(WrapText: true) { RightToLeft = true });
        wb.Rows.Add(new MiniXlsx.OutRow(2) { HeightPoints = 30 }.Text("A", "NOTICE — אזהרה", 1));
        wb.Rows.Add(new MiniXlsx.OutRow(3).Number("A", 0m, 0).Number("B", 94.720012860000011m, 0).Formula("C", "A3*B3", 0));
        return wb;
    }

    [Fact]
    public void ExplicitMergeKeepsNumericCellsAndUsesOptInRtlAlignment()
    {
        var wb = Workbook(); wb.SingleRowMerges.Add("A2:H2"); var path = Output(); MiniXlsx.Write(wb, path);
        using var zip = ZipFile.OpenRead(path);
        using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var sheet = XDocument.Load(sheetStream);
        Assert.Equal("A2:H2", sheet.Descendants(Ns + "mergeCell").Single().Attribute("ref")!.Value);
        Assert.Equal("0", sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "A3").Element(Ns + "v")!.Value);
        Assert.Equal("94.720012860000011", sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "B3").Element(Ns + "v")!.Value);
        Assert.Equal("A3*B3", sheet.Descendants(Ns + "f").Single().Value);
        using var styleStream = zip.GetEntry("xl/styles.xml")!.Open(); var styles = XDocument.Load(styleStream);
        var xfs = styles.Root!.Element(Ns + "cellXfs")!.Elements().ToArray();
        Assert.Null(xfs[0].Element(Ns + "alignment"));
        Assert.Equal("2", (string?)xfs[1].Element(Ns + "alignment")!.Attribute("readingOrder"));
        Assert.Equal("right", (string?)xfs[1].Element(Ns + "alignment")!.Attribute("horizontal"));
        Assert.Equal("1", (string?)xfs[1].Element(Ns + "alignment")!.Attribute("wrapText"));
    }

    [Theory]
    [InlineData("A2:H3")]
    [InlineData("H2:A2")]
    [InlineData("A2:A2")]
    [InlineData("A0:H0")]
    [InlineData("A1048577:H1048577")]
    [InlineData("A2:XFE2")]
    [InlineData("A2:H2\"/>")]
    [InlineData("A4:H4")]
    public void InvalidMergeIsRefusedBeforeCreatingOutput(string range)
    {
        var wb = Workbook(); wb.SingleRowMerges.Add(range); var path = Output();
        Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, path)); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergeCannotHideEvenZeroOrFormulaData(bool formula)
    {
        var wb = Workbook(); wb.SingleRowMerges.Add("A2:H2"); var row = wb.Rows[0];
        if (formula) row.Formula("H", "0", 0); else row.Number("H", 0m, 0);
        var path = Output(); Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, path)); Assert.False(File.Exists(path));
    }

    [Fact]
    public void DuplicateOrOverlappingMergesAreRefusedAndDefaultAddsNone()
    {
        var wb = Workbook(); var path = Output(); MiniXlsx.Write(wb, path);
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()) Assert.Empty(XDocument.Load(stream).Descendants(Ns + "mergeCell"));
        wb.SingleRowMerges.AddRange(new[] { "A2:H2", "A2:H2" });
        Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, Output()));
    }
}
