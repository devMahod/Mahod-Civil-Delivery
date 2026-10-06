using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MiniXlsxMultiSheetTests : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Content = "http://schemas.openxmlformats.org/package/2006/content-types";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Mahod-multisheet-" + Guid.NewGuid().ToString("N"));
    private string Output(string name = "book.xlsx") { Directory.CreateDirectory(directory); return Path.Combine(directory, name); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private static XDocument Xml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open(); return XDocument.Load(stream);
    }

    private static MiniXlsx.Workbook FiveSheets()
    {
        var wb = new MiniXlsx.Workbook { SheetName = "סיכום", RightToLeft = true, FreezeTopRows = 1 };
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 11.5, NumFmtId: MiniXlsx.NumFmtThousands2));
        wb.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "סיכום", 1));
        wb.Rows.Add(new MiniXlsx.OutRow(2).Formula("A", "'Data O''Brien'!B2", 1));
        foreach (var name in new[] { "Data O'Brien", "Measurements", "Findings", "Sources" })
        {
            var sheet = new MiniXlsx.Worksheet { SheetName = name, RightToLeft = name == "Findings",
                ShowGridLines = false, FreezeTopRows = 2, FreezeLeftColumns = 1, AutoFilterRange = "A2:B3" };
            sheet.ColumnWidths.Add((1, 32)); sheet.ColumnWidths.Add((2, 18));
            sheet.Rows.Add(new MiniXlsx.OutRow(1).Text("A", name, 1));
            sheet.SingleRowMerges.Add("A1:B1");
            sheet.Rows.Add(new MiniXlsx.OutRow(2).Text("A", "=not a formula", 0).Number("B", 12.3456m, 1));
            sheet.Rows.Add(new MiniXlsx.OutRow(3).Text("A", "source & evidence", 0).Number("B", 0m, 1));
            wb.AdditionalSheets.Add(sheet);
        }
        return wb;
    }

    [Fact]
    public void FiveSheetsHaveOrderedUniquePartsRelationshipsAndSharedStyles()
    {
        var wb = FiveSheets(); var path = Output(); MiniXlsx.Write(wb, path);
        using var zip = ZipFile.OpenRead(path);
        var sheets = Xml(zip, "xl/workbook.xml").Descendants(Main + "sheet").ToArray();
        Assert.Equal(new[] { "סיכום", "Data O'Brien", "Measurements", "Findings", "Sources" }, sheets.Select(s => (string?)s.Attribute("name")));
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, sheets.Select(s => (string?)s.Attribute("sheetId")));
        var relationships = Xml(zip, "xl/_rels/workbook.xml.rels").Descendants(PackageRel + "Relationship").ToArray();
        Assert.Equal(6, relationships.Length);
        Assert.Equal(6, relationships.Select(r => (string?)r.Attribute("Id")).Distinct().Count());
        var overrides = Xml(zip, "[Content_Types].xml").Descendants(Content + "Override").ToArray();
        for (var i = 0; i < sheets.Length; i++)
        {
            var rid = (string?)sheets[i].Attribute(Rel + "id");
            var target = (string?)relationships.Single(r => (string?)r.Attribute("Id") == rid).Attribute("Target");
            Assert.Equal($"worksheets/sheet{i + 1}.xml", target);
            Assert.Contains(overrides, o => (string?)o.Attribute("PartName") == "/xl/" + target);
            var sheet = Xml(zip, "xl/" + target);
            Assert.All(sheet.Descendants(Main + "c"), c => Assert.InRange((int?)c.Attribute("s") ?? 0, 0, 1));
        }
        Assert.Single(zip.Entries, e => e.FullName == "xl/styles.xml");
        var styles = Xml(zip, "xl/styles.xml").Root!.Element(Main + "cellXfs")!;
        Assert.Equal("2", (string?)styles.Attribute("count"));
        Assert.Equal("4", (string?)styles.Elements().Last().Attribute("numFmtId"));
        Assert.Equal("'Data O''Brien'!B2", Xml(zip, "xl/worksheets/sheet1.xml").Descendants(Main + "f").Single().Value);
        Assert.Equal("auto", (string?)Xml(zip, "xl/workbook.xml").Root!.Element(Main + "calcPr")!.Attribute("calcMode"));
        var second = Xml(zip, "xl/worksheets/sheet2.xml");
        Assert.Equal("B3", (string?)second.Descendants(Main + "pane").Single().Attribute("topLeftCell"));
        Assert.Equal("1", (string?)second.Descendants(Main + "pane").Single().Attribute("xSplit"));
        Assert.Equal("2", (string?)second.Descendants(Main + "pane").Single().Attribute("ySplit"));
        Assert.Equal("A2:B3", (string?)second.Descendants(Main + "autoFilter").Single().Attribute("ref"));
        Assert.Equal("A1:B1", (string?)second.Descendants(Main + "mergeCell").Single().Attribute("ref"));
        Assert.Equal("32", (string?)second.Descendants(Main + "col").First().Attribute("width"));
        Assert.Null(second.Descendants(Main + "sheetView").Single().Attribute("rightToLeft"));
        Assert.Equal("1", (string?)Xml(zip, "xl/worksheets/sheet4.xml").Descendants(Main + "sheetView").Single().Attribute("rightToLeft"));
        Assert.Equal(5, MiniXlsx.ReadAllSheets(path).Count);
        Assert.Equal("12.3456", MiniXlsx.ReadAllSheets(path)[1][1].Single(c => c.Column == "B").Text);
    }

    [Fact]
    public void ExistingSingleSheetApiKeepsFirstSheetAndStyleRelationship()
    {
        var wb = new MiniXlsx.Workbook { SheetName = "Old", RightToLeft = true };
        wb.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "legacy", 0).Number("B", 7m, 0));
        var path = Output(); MiniXlsx.Write(wb, path);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(6, zip.Entries.Count);
        Assert.Single(Xml(zip, "xl/workbook.xml").Descendants(Main + "sheet"));
        Assert.Equal("styles.xml", (string?)Xml(zip, "xl/_rels/workbook.xml.rels").Descendants(PackageRel + "Relationship")
            .Single(r => (string?)r.Attribute("Id") == "rId2").Attribute("Target"));
        Assert.Equal("legacy", MiniXlsx.ReadFirstSheet(path)[0][0].Text);
        Assert.Single(MiniXlsx.ReadAllSheets(path));
    }

    [Fact]
    public void SameWorkbookBytesAreDeterministicAcrossWritesAndNumericCultures()
    {
        var wb = FiveSheets(); var first = Output("first.xlsx"); var second = Output("second.xlsx");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); MiniXlsx.Write(wb, first);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); MiniXlsx.Write(wb, second);
        }
        finally { CultureInfo.CurrentCulture = original; }
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using var zip = ZipFile.OpenRead(second);
        Assert.Contains(Xml(zip, "xl/styles.xml").Descendants(Main + "sz"), el => (string?)el.Attribute("val") == "11.5");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a]b")]
    [InlineData("'start")]
    [InlineData("end'")]
    [InlineData("a\nb")]
    [InlineData("History")]
    public void InvalidAdditionalNameIsRefusedWithoutTruncatingExistingOutput(string name)
    {
        var wb = FiveSheets(); var path = Output(); MiniXlsx.Write(wb, path); var before = File.ReadAllBytes(path);
        wb.AdditionalSheets[3].SheetName = name;
        Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("סיכום")]
    [InlineData("MEASUREMENTS")]
    public void NamesMustBeUniqueAcrossPrimaryAndAdditionalSheetsIgnoringCase(string name)
    {
        var wb = FiveSheets(); wb.AdditionalSheets[3].SheetName = name; var path = Output();
        Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, path)); Assert.False(File.Exists(path));
    }

    [Fact]
    public void MaximumNameLengthAndInternalApostropheAreValid()
    {
        var wb = FiveSheets(); wb.AdditionalSheets[3].SheetName = new string('x', 31);
        MiniXlsx.Write(wb, Output());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("nested")]
    [InlineData("same")]
    [InlineData("freeze")]
    [InlineData("width")]
    [InlineData("duplicate-row")]
    [InlineData("style")]
    [InlineData("cell-row")]
    [InlineData("duplicate-cell")]
    [InlineData("numeric")]
    [InlineData("filter")]
    [InlineData("merge")]
    public void InvalidAdditionalSheetStateFailsBeforeCreatingFile(string state)
    {
        var wb = FiveSheets(); var sheet = wb.AdditionalSheets[0];
        switch (state)
        {
            case "null": wb.AdditionalSheets.Add(null!); break;
            case "nested": wb.AdditionalSheets.Add(new MiniXlsx.Workbook { SheetName = "Nested" }); break;
            case "same": wb.AdditionalSheets.Add(wb); break;
            case "freeze": sheet.FreezeLeftColumns = 16384; break;
            case "width": sheet.ColumnWidths.Add((3, double.NaN)); break;
            case "duplicate-row": sheet.Rows.Add(new MiniXlsx.OutRow(1)); break;
            case "style": sheet.Rows[1].Text("C", "bad", 10); break;
            case "cell-row": sheet.Rows[1].Cells.Add(new MiniXlsx.OutCell("C3", MiniXlsx.CellKind.Text, "bad", 0)); break;
            case "duplicate-cell": sheet.Rows[1].Text("A", "bad", 0); break;
            case "numeric": sheet.Rows[1].Number("C", double.NaN, 0); break;
            case "filter": sheet.AutoFilterRange = "A2:XFE3"; break;
            case "merge": sheet.SingleRowMerges.Add("A2:B2"); break;
        }
        var path = Output(); Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, path)); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExactCellLengthBoundaryIsRetainedOnEverySheet(bool formula, bool additional)
    {
        var wb = FiveSheets();
        var sheet = additional ? wb.AdditionalSheets[3] : wb;
        var value = formula ? "1" + new string(' ', 8191) : new string('א', 32767);
        var row = new MiniXlsx.OutRow(10);
        if (formula) row.Formula("C", value, 0); else row.Text("C", value, 0);
        sheet.Rows.Add(row);
        var path = Output(); MiniXlsx.Write(wb, path);
        using var zip = ZipFile.OpenRead(path);
        var cell = Xml(zip, $"xl/worksheets/sheet{(additional ? 5 : 1)}.xml")
            .Descendants(Main + "c").Single(node => (string?)node.Attribute("r") == "C10");
        Assert.Equal(value, formula ? cell.Element(Main + "f")!.Value : cell.Descendants(Main + "t").Single().Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OversizedCellFailsBeforeCreatingOrTruncatingOutput(bool formula, bool additional)
    {
        var wb = FiveSheets(); var existing = Output(); MiniXlsx.Write(wb, existing);
        var before = File.ReadAllBytes(existing);
        var sheet = additional ? wb.AdditionalSheets[3] : wb;
        var value = formula ? "1" + new string(' ', 8192) : new string('א', 32768);
        var row = new MiniXlsx.OutRow(10);
        if (formula) row.Formula("C", value, 0); else row.Text("C", value, 0);
        sheet.Rows.Add(row);
        var missing = Output("must-not-exist.xlsx");
        Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, missing));
        Assert.False(File.Exists(missing));
        Assert.Throws<ArgumentException>(() => MiniXlsx.Write(wb, existing));
        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.Equal(value, row.Cells.Single().Value);
    }
}
