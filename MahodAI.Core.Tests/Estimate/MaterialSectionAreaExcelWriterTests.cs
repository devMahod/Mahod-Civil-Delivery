using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MaterialSectionAreaExcelWriterTests
{
    private const string HostPath = @"C:\local\host.dwg";
    private static readonly string HostHash = new('a', 64);
    private static readonly string ProfileHash = new('b', 64);
    private static readonly string ScanHash = new('c', 64);
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static MaterialSectionAreaExcelWriter.WriteContext Context() =>
        new("project", "scan-current", ProfileHash, HostPath, HostHash, ScanHash);

    private static MaterialSectionAreaObservation Row(double station = 0, double area = 1,
        string code = "H1", string series = "baseline-1", bool metric = true, bool complete = true) =>
        new("measurement-original", HostPath, HostHash, "AB", "קורידור לדוגמה", series,
            code, station, metric ? "מטר" : "יחידת שרטוט", area,
            metric ? "מ\"ר" : "יחידת שרטוט²", 1, metric ? "Meters" : "Unitless", metric, complete);

    [Fact]
    public void ExportsEveryObservationWithoutSummingStationsOrCreatingFormulas()
    {
        var rows = Enumerable.Range(0, 151).Select(i => Row(i, i == 0 ? 0 : i + .125)).ToArray();
        InSandbox(directory =>
        {
            var result = MaterialSectionAreaExcelWriter.Write(rows, directory, "material-areas", Context());
            var cells = MiniXlsx.ReadFirstSheet(result.XlsxPath).SelectMany(r => r).ToList();
            var measures = cells.Where(c => c.Column == "G" && c.Row > MaterialSectionAreaExcelWriter.HeaderRow).ToList();
            Assert.Equal(151, result.ObservationCount);
            Assert.Equal(151, measures.Count);
            Assert.All(measures, c => Assert.True(c.IsNumeric));
            Assert.Equal("0", measures[0].Text);
            Assert.Equal("150.125", measures[^1].Text);
            Assert.Equal(result.XlsxHash, ArtifactHash.Sha256OfFile(result.XlsxPath));
            var sheet = Sheet(result.XlsxPath);
            Assert.Empty(sheet.Descendants(Ns + "f"));
            Assert.Contains(MaterialSectionAreaEvidence.ScopeNotice, cells.Select(c => c.Text));
            Assert.Single(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public void ExactCloseStationsTinyPositiveAreaAndLiteralFormulaLikeCodesSurvive()
    {
        InSandbox(directory =>
        {
            var rows = new[] { Row(10, 0, "=HYPERLINK(\"https://invalid\",\"not a formula\")"), Row(10.000001, .0000001) };
            var result = MaterialSectionAreaExcelWriter.Write(rows, directory, "precision", Context());
            var cells = MiniXlsx.ReadFirstSheet(result.XlsxPath).SelectMany(r => r).ToList();
            Assert.Equal(new[] { 10d, 10.000001 }, cells.Where(c => c.Column == "E" && c.Row > 10)
                .Select(c => double.Parse(c.Text, CultureInfo.InvariantCulture)));
            Assert.Equal(.0000001, double.Parse(cells.Single(c => c.Column == "G" && c.Row == 12).Text, CultureInfo.InvariantCulture));
            Assert.Equal(rows[0].ShapeCode, cells.Single(c => c.Column == "D" && c.Row == 11).Text);
            Assert.False(cells.Single(c => c.Column == "D" && c.Row == 11).IsNumeric);
            Assert.Empty(Sheet(result.XlsxPath).Descendants(Ns + "f"));
        });
    }

    [Fact]
    public void NativeSourceMayDifferFromHostOnlyWhenExplicitlyBoundToPublishedScan()
    {
        var native = Row() with { DrawingPath = @"C:\local\xref-corridor.dwg", DrawingHash = new string('d', 64) };
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { native }, Context()));
        var context = Context() with
        {
            AllowedMeasuredSources = new[] { new MaterialSectionAreaExcelWriter.SourceIdentity(native.DrawingPath, native.DrawingHash) },
        };
        var workbook = MaterialSectionAreaExcelWriter.Build(new[] { native }, context);
        Assert.Contains(workbook.Rows.SelectMany(r => r.Cells), c => c.Reference == "A5" && c.Value.Contains(HostPath));
        Assert.Equal(native.DrawingPath, workbook.Rows.Single(r => r.Index == 11).Cells.Single(c => c.Reference == "N11").Value);
        Assert.Equal(native.DrawingHash, workbook.Rows.Single(r => r.Index == 11).Cells.Single(c => c.Reference == "O11").Value);
        // Source handles repeat across DWGs; they must not collapse their observations.
        Assert.Equal(2, MaterialSectionAreaExcelWriter.Build(new[] { Row(), native }, context).Rows.Count(r => r.Index > 10));
    }

    [Fact]
    public void PartialAndUnknownUnitMeasurementsStayExplicitAndNeverBecomeApprovedBoq()
    {
        var workbook = MaterialSectionAreaExcelWriter.Build(new[] { Row(metric: false, complete: false) }, Context());
        var cells = workbook.Rows.SelectMany(r => r.Cells).ToList();
        Assert.Contains(cells, c => c.Reference == "A2" && c.Value.Contains("ללא אישור כתב כמויות"));
        Assert.Contains(cells, c => c.Reference == "J11" && c.Value == "חלקי / לא מוכח" && c.Style == 6);
        Assert.Contains(cells, c => c.Reference == "H11" && c.Value == "יחידת שרטוט²");
        Assert.DoesNotContain(cells, c => c.Value == "מ\"ר");
        Assert.Contains(cells, c => c.Reference == "A8" && c.Value.Contains("לא מוכחות: 1"));
        Assert.Contains(cells, c => c.Reference == "P11" && c.Value == "measurement-original");
        Assert.Contains(cells, c => c.Reference == "A4" && c.Value.Contains("scan-current"));
    }

    [Fact]
    public void DuplicateIdentityInvalidMeasurementAndMissingProvenanceFailBeforeWriting()
    {
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row(), Row() }, Context()));
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row(area: -1) }, Context()));
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row(double.NaN) }, Context()));
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row(area: double.PositiveInfinity) }, Context()));
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row() with { AreaUnit = "מ\"ק" } }, Context()));
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row() }, Context() with { PublishedScanSha256 = "missing" }));
        Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Build(new[] { Row() }, Context() with
        {
            AllowedMeasuredSources = new[] { new MaterialSectionAreaExcelWriter.SourceIdentity(HostPath, new string('e', 64)) },
        }));
        InSandbox(directory =>
        {
            Assert.Throws<InvalidOperationException>(() => MaterialSectionAreaExcelWriter.Write(
                Array.Empty<MaterialSectionAreaObservation>(), directory, "empty", Context()));
            Assert.Empty(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public void NoOverwriteAndPathTraversalIsRejected()
    {
        InSandbox(directory =>
        {
            var original = MaterialSectionAreaExcelWriter.Write(new[] { Row() }, directory, "areas", Context());
            var second = MaterialSectionAreaExcelWriter.Write(new[] { Row(area: 5) }, directory, "areas", Context());
            Assert.NotEqual(original.XlsxPath, second.XlsxPath);
            Assert.Equal(original.XlsxHash, ArtifactHash.Sha256OfFile(original.XlsxPath));
            Assert.Throws<ArgumentException>(() => MaterialSectionAreaExcelWriter.Write(new[] { Row() }, directory, "../escape", Context()));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        });
    }

    [Fact]
    public void ExplicitMultilineNativeNamesGetEnoughHeightWithoutChangingTheirText()
    {
        var name = "ראשון\r\nשני\nשלישי\rרביעי\nחמישי";
        var workbook = MaterialSectionAreaExcelWriter.Build(new[] { Row() with { CorridorName = name } }, Context());
        var row = workbook.Rows.Single(r => r.Index == 11);
        Assert.True(row.HeightPoints >= 88);
        Assert.Equal(name, row.Cells.Single(c => c.Reference == "B11").Value);
    }

    [Fact]
    public void RtlHeadersFilterAndIdentifyingColumnsStayAvailableOnLongReports()
    {
        InSandbox(directory =>
        {
            var file = MaterialSectionAreaExcelWriter.Write(new[] { Row() }, directory, "layout", Context()).XlsxPath;
            var sheet = Sheet(file);
            var view = Assert.Single(sheet.Descendants(Ns + "sheetView"));
            Assert.Equal("1", (string?)view.Attribute("rightToLeft"));
            Assert.Equal("0", (string?)view.Attribute("showGridLines"));
            var pane = Assert.Single(sheet.Descendants(Ns + "pane"));
            Assert.Equal("10", (string?)pane.Attribute("ySplit"));
            Assert.Equal("4", (string?)pane.Attribute("xSplit"));
            Assert.Equal("E11", (string?)pane.Attribute("topLeftCell"));
            Assert.Equal("A10:R11", (string?)Assert.Single(sheet.Descendants(Ns + "autoFilter")).Attribute("ref"));

            var legacy = Path.Combine(directory, "legacy.xlsx");
            MiniXlsx.Write(new MiniXlsx.Workbook(), legacy);
            Assert.Empty(Sheet(legacy).Descendants(Ns + "pane"));
            Assert.Empty(Sheet(legacy).Descendants(Ns + "autoFilter"));
            Assert.Null(Assert.Single(Sheet(legacy).Descendants(Ns + "sheetView")).Attribute("showGridLines"));
        });
    }

    private static XDocument Sheet(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }

    private static void InSandbox(Action<string> work)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcd-material-xlsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { work(root); }
        finally { Directory.Delete(root, true); }
    }
}
