using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MeasurementDraftExcelWriterTests
{
    private readonly ITestOutputHelper _output;
    public MeasurementDraftExcelWriterTests(ITestOutputHelper output) => _output = output;
    internal const string ActualScanPath = @"C:\Users\arthurf\AppData\Local\MahodAI_Civil3D\civil-delivery\runs\estimate-extract-20260909-080304-0dcf329b\estimate_scan.json";
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static int FirstDataRow => MeasurementDraftExcelWriter.HeaderRow + 1;
    private static MeasurementDraftExcelWriter.WriteContext Context() => new("6422", "scan", new string('a', 64),
        @"C:\local\host.dwg", new string('b', 64), @"C:\local\estimate_scan.json", new string('c', 64));
    private static DeliveryFinding Finding(string message = "unmapped") => new()
    {
        FindingId = "finding-1", Code = EstimateFindingCodes.Unmapped, Domain = "estimate",
        Severity = FindingSeverity.Warning, Title = "Unmapped", Message = message,
        SourceRefs = new() { new() { SourceKind = "drawing", SourceHandle = "AB", InputHash = "proof" } },
        AffectedRecordIds = new() { "record-1" }, EvidenceRefs = new() { "evidence-path" },
    };
    private static NeutralQuantityRecord Record(string id = "record-1", double value = 1.2345678901234567,
        string unit = "מטר", string kind = "length", string? text = null) => new()
    {
        RecordId = id, ProjectProfileId = "6422", RunId = "scan", Status = DeliveryStatus.Discovered,
        Source = new() { Drawing = "host.dwg", DrawingPath = @"C:\local\host.dwg", DrawingHash = new string('b', 64),
            Handle = "AB", EntityType = "Polyline", Layer = text ?? "layer", StationFrom = 0, StationTo = .0000001 },
        Measurement = new() { Kind = kind, Method = "source-method", RawValue = value, Unit = unit,
            GeometryEvidence = new[] { 0d, 1d, 2d, 3d }, Parameters = new() { ["source"] = text ?? "literal" } },
        Classification = new() { RuleKey = "group", CandidateCatalogCode = "U51.000", SourceClass = "draft",
            Tags = new() { "unapproved" } }, Findings = new() { Finding() },
        Provenance = new() { SourceKind = "drawing", SourceHandle = "AB", InputHash = "proof", RunId = "scan" },
    };

    [Fact]
    public void EveryRecordIsPreservedInInputOrderWithExactTypedMeasurementsAndBlankPrices()
    {
        var records = new[] { Record("z", 0), Record("a", .0000001, "מ\"ר", "area"), Record("a", -5, "?", "volume") };
        var workbook = MeasurementDraftExcelWriter.Build(records, new[] { Finding("global evidence") }, Context());
        var rows = workbook.Rows.Where(r => r.Index > MeasurementDraftExcelWriter.HeaderRow)
            .Take(records.Length).ToArray();
        Assert.Equal(new[] { "z", "a", "a" }, rows.Select(r => Cell(r, "B").Value));
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(records[i].Measurement.RawValue, double.Parse(Cell(rows[i], "G").Value, CultureInfo.InvariantCulture));
            Assert.Equal(MiniXlsx.CellKind.Number, Cell(rows[i], "G").Kind);
            Assert.Equal(records[i].Measurement.Unit, Cell(rows[i], "H").Value);
            Assert.Equal(records[i].Measurement.Kind, Cell(rows[i], "F").Value);
            Assert.Equal("", Cell(rows[i], "J").Value); Assert.Equal("", Cell(rows[i], "K").Value);
        }
        Assert.Equal(3, Findings(workbook).Rows.Count(r => r.Cells.Any(c => c.Value == "record_finding")));
        Assert.Equal(1, Findings(workbook).Rows.Count(r => r.Cells.Any(c => c.Value == "scan_finding")));
        Assert.Contains(workbook.Rows.SelectMany(r => r.Cells), c => c.Value == MeasurementDraftExcelWriter.DraftNotice);
        Assert.DoesNotContain(AllRows(workbook).SelectMany(r => r.Cells), c => c.Kind == MiniXlsx.CellKind.Formula);
        Assert.Equal(records.Length, workbook.Rows.Count(r => r.Index > MeasurementDraftExcelWriter.HeaderRow));
        Assert.Equal(new[] { MeasurementDraftExcelWriter.FindingsSheetName, MeasurementDraftExcelWriter.ProposalsSheetName,
            MeasurementDraftExcelWriter.SourcesSheetName, MeasurementDraftExcelWriter.TextPartsSheetName }, workbook.AdditionalSheets.Select(s => s.SheetName));
    }

    [Fact]
    public void SavedDraftTitleStartsInHebrewOnTheRtlSheetAndRetainsAllApprovalWarnings()
    {
        void Verify(string directory)
        {
            // Small synthetic data, written by the actual production exporter for optional native Excel review.
            var records = new[] { Record("title-review-length", 12.3456789, text: "דוגמת בדיקה — אורך"),
                Record("title-review-area", 2.3456789, "מ\"ר", "area", "דוגמת בדיקה — שטח") };
            var result = MeasurementDraftExcelWriter.Write(records, new[] { Finding("דוגמה סינתטית לבדיקת תצוגה בלבד") },
                directory, "measurement-draft-title-review", Context());
            var sheet = Sheet(result.XlsxPath);
            Assert.Equal("1", sheet.Descendants(Ns + "sheetView").Single().Attribute("rightToLeft")?.Value);
            var titleCell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "A2");
            Assert.Equal("inlineStr", titleCell.Attribute("t")?.Value);
            var title = titleCell.Descendants(Ns + "t").Single().Value;
            Assert.Equal(MeasurementDraftExcelWriter.DraftNotice, title);
            Assert.StartsWith("טיוטת מדידה", title);
            Assert.InRange(title[0], '\u0590', '\u05ff');
            Assert.Contains("DRAFT", title);
            Assert.Contains("לא אומדן מאושר", title);
            Assert.Contains("לא אישור שלמות המדידה", title);
            Assert.Empty(sheet.Descendants(Ns + "f"));
            for (var i = 0; i < records.Length; i++)
            {
                var row = FirstDataRow + i;
                var quantity = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "G" + row);
                Assert.Equal(records[i].Measurement.RawValue,
                    double.Parse(quantity.Element(Ns + "v")!.Value, CultureInfo.InvariantCulture));
                foreach (var column in new[] { "J", "K" })
                    Assert.Equal("", string.Concat(sheet.Descendants(Ns + "c")
                        .Single(c => (string?)c.Attribute("r") == column + row).Descendants(Ns + "t").Select(t => t.Value)));
            }
            Assert.Equal(result.XlsxHash, ArtifactHash.Sha256OfFile(result.XlsxPath));
            _output.WriteLine($"Actual-writer synthetic title review: {result.XlsxPath}; SHA256={result.XlsxHash}. Native Excel visual review is separate.");
        }
        var evidenceDirectory = Environment.GetEnvironmentVariable("MAHOD_MEASUREMENT_DRAFT_TITLE_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) InSandbox(Verify); else Verify(evidenceDirectory);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://invalid\",\"x\")")]
    [InlineData("+SUM(A1:A2)")]
    [InlineData("@SUM(A1:A2)")]
    [InlineData("-1+2")]
    public void FormulaLikeSourceTextIsLiteralThroughSavedWorkbook(string hostile)
    {
        InSandbox(directory =>
        {
            var result = MeasurementDraftExcelWriter.Write(new[] { Record(text: hostile) }, new[] { Finding(hostile) }, directory, "draft", Context());
            var sheet = Sheet(result.XlsxPath);
            Assert.Empty(sheet.Descendants(Ns + "f"));
            Assert.Equal("inlineStr", sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "D" + FirstDataRow).Attribute("t")?.Value);
            Assert.Contains(sheet.Descendants(Ns + "t"), element => element.Value == hostile);
            Assert.Equal(result.XlsxHash, ArtifactHash.Sha256OfFile(result.XlsxPath));
        });
    }

    [Fact]
    public void LongSourceAndCompleteFailureTextAreLosslesslyLinkedToOrderedParts()
    {
        var source = "=" + new string('א', 32768) + "🧭tail";
        var finding = Finding("Complete measurement failures (90):\n" + source);
        var workbook = MeasurementDraftExcelWriter.Build(new[] { Record(text: source) }, new[] { finding }, Context());
        var recordRow = workbook.Rows.Single(r => r.Index == FirstDataRow);
        Assert.Equal("TEXT_PARTS:" + Qualified(workbook, "D" + FirstDataRow), Cell(recordRow, "D").Value);
        Assert.Equal(source, Reconstruct(workbook, Qualified(workbook, "D" + FirstDataRow)));
        var findingRow = Findings(workbook).Rows.Single(r => r.Cells.Any(c => c.Value == "scan_finding"));
        Assert.Equal(finding.Message, Reconstruct(workbook, Qualified(Findings(workbook), "K" + findingRow.Index)));
        Assert.All(AllRows(workbook).SelectMany(r => r.Cells), cell => Assert.True(cell.Value.Length <= 32767));
        Assert.DoesNotContain(AllRows(workbook).SelectMany(r => r.Cells), cell => cell.Kind == MiniXlsx.CellKind.Formula);
    }

    [Fact]
    public void AllMappingProposalsAndReferenceProvenanceRemainUnapprovedIncludingUnmatchedGroups()
    {
        var proposals = new[] { new MappingProposal { RuleKey = "unmatched-group", MeasurementKind = "length",
            MeasuredUnit = "מטר", ProposedCode = "001.02", Reasons = new() { "original reason" }, EvidenceKind = "reference" } };
        var context = Context() with { MappingProposals = proposals, MappingProposalsEvidencePath = "mapping_proposals.json",
            MappingProposalsEvidenceSha256 = new string('d', 64), ProposalReferenceSource = "golden.xlsx", ProposalReferenceHash = new string('e', 64) };
        var workbook = MeasurementDraftExcelWriter.Build(new[] { Record() }, Array.Empty<DeliveryFinding>(), context);
        var proposal = workbook.AdditionalSheets.Single(s => s.SheetName == MeasurementDraftExcelWriter.ProposalsSheetName)
            .Rows.Single(r => r.Index > MeasurementDraftExcelWriter.HeaderRow);
        Assert.Contains(proposal.Cells, c => c.Value == "PROPOSED_UNAPPROVED");
        Assert.Contains(proposal.Cells, c => c.Value.Contains("original reason"));
        Assert.Contains(AllRows(workbook).SelectMany(r => r.Cells), c => c.Value.Contains("golden.xlsx"));
        Assert.Equal("", Cell(workbook.Rows.Single(r => r.Index == FirstDataRow), "J").Value);
    }

    [Fact]
    public void LegacyConsolidationLimitIsVisibleButCompleteFutureEvidenceIsNotMislabelled()
    {
        DeliveryFinding Failure(string message) => new() { Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate",
            Severity = FindingSeverity.Error, Title = "90 supported construction objects could not be measured — estimate export is blocked", Message = message };
        Assert.True(MeasurementDraftExcelWriter.HasLegacyTruncatedFailure(Failure("only ten details")));
        Assert.False(MeasurementDraftExcelWriter.HasLegacyTruncatedFailure(Failure("Complete measurement failures (90):\nall details")));
        var workbook = MeasurementDraftExcelWriter.Build(new[] { Record() }, new[] { Failure("only ten details") }, Context());
        Assert.Contains(AllRows(workbook).SelectMany(r => r.Cells), c => c.Value == MeasurementDraftExcelWriter.LegacyFailureNotice);
        Assert.Equal(6, MeasurementDraftExcelWriter.HeaderRow);
        Assert.Contains(workbook.Rows.Single(r => r.Index == MeasurementDraftExcelWriter.HeaderRow - 1).Cells, c => c.Value.Contains("LEGACY_NOTICE"));
        Assert.True(workbook.Rows.Where(r => r.Index <= MeasurementDraftExcelWriter.HeaderRow)
            .Sum(r => r.HeightPoints ?? 15) + 15 < 300);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteSourceValuesRemainExplicitLiteralEvidenceNotNumericZero(double value)
    {
        var workbook = MeasurementDraftExcelWriter.Build(new[] { Record(value: value) }, Array.Empty<DeliveryFinding>(), Context());
        var measured = Cell(workbook.Rows.Single(r => r.Index == FirstDataRow), "G");
        Assert.Equal(MiniXlsx.CellKind.Text, measured.Kind);
        Assert.Equal(value.ToString("R", CultureInfo.InvariantCulture), measured.Value);
        Assert.Equal(6, measured.Style);
    }

    [Fact]
    public void MeasurementRowsStayCompactWhileLongNamesAndJsonRemainFullyRecoverable()
    {
        var longName = "שכבת מקור " + new string('א', 350);
        var multilineName = string.Join("\n", Enumerable.Range(1, 9).Select(number => "source line " + number));
        var records = new[] { Record("normal"), Record("long-name", text: longName), Record("multiline", text: multilineName) };
        records[1].Classification.Tags.Add(new string('ב', 500));
        var workbook = MeasurementDraftExcelWriter.Build(records, Array.Empty<DeliveryFinding>(), Context());
        var rows = workbook.Rows.Where(row => row.Index >= FirstDataRow && row.Index < FirstDataRow + records.Length).ToArray();
        Assert.Equal(records.Length, rows.Length);
        Assert.All(rows, row => Assert.InRange(row.HeightPoints ?? 0, 1d, 80d));
        // One compact line per measurement record; evidence stays complete, not wrapped tall.
        Assert.All(rows, row => Assert.Equal(MeasurementDraftExcelWriter.CompactRecordRowHeight, row.HeightPoints));
        Assert.All(rows.SelectMany(row => row.Cells).Where(cell => cell.Kind == MiniXlsx.CellKind.Text && cell.Style == 4),
            cell => Assert.Fail("Main-sheet record text must use the compact non-wrapping style: " + cell.Reference));
        Assert.StartsWith("TEXT_PARTS:", Cell(rows[1], "D").Value);
        Assert.StartsWith("TEXT_PARTS:", Cell(rows[1], "Y").Value);
        Assert.StartsWith("TEXT_PARTS:", Cell(rows[1], "Z").Value);
        Assert.StartsWith("TEXT_PARTS:", Cell(rows[2], "D").Value);
        for (var i = 0; i < records.Length; i++)
        {
            Assert.Equal(records[i].Source.Layer, FullText(workbook, rows[i], "D"));
            using var parameters = JsonDocument.Parse(FullText(workbook, rows[i], "Z"));
            Assert.Equal(records[i].Measurement.Parameters["source"], parameters.RootElement.GetProperty("source").GetString());
            using var classification = JsonDocument.Parse(FullText(workbook, rows[i], "Y"));
            Assert.Equal(records[i].Classification.Tags, classification.RootElement.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!));
            Assert.Equal(records[i].Measurement.RawValue.ToString("R", CultureInfo.InvariantCulture), Cell(rows[i], "G").Value);
        }
    }

    [Fact]
    public void SameCellAddressAcrossSheetsHasDistinctLosslessOverflowReferencesAndSavedParts()
    {
        var longId = "record:" + new string('א', 4000);
        var longCode = "code:" + new string('ב', 4000);
        var longProfile = "profile:" + new string('ג', 4000);
        var proposals = new[] { new MappingProposal { RuleKey = "group", MeasurementKind = "length",
            MeasuredUnit = "מטר", ProposedCode = longCode, Reasons = new() { "=literal" } } };
        var context = Context() with { ProjectProfileId = longProfile, MappingProposals = proposals,
            MappingProposalsEvidencePath = "proposals.json", MappingProposalsEvidenceSha256 = new string('d', 64) };
        var workbook = MeasurementDraftExcelWriter.Build(new[] { Record(longId) }, new[] { Finding("global") }, context);
        var sourceSheet = workbook.AdditionalSheets.Single(s => s.SheetName == MeasurementDraftExcelWriter.SourcesSheetName);
        var proposalSheet = workbook.AdditionalSheets.Single(s => s.SheetName == MeasurementDraftExcelWriter.ProposalsSheetName);
        var qualified = new[] { Qualified(workbook, "B7"), Qualified(Findings(workbook), "B7"),
            Qualified(proposalSheet, "B7"), Qualified(sourceSheet, "B7") };
        Assert.Equal(4, qualified.Distinct().Count());
        Assert.Equal(longId, Reconstruct(workbook, qualified[0]));
        Assert.Equal(longId, Reconstruct(workbook, qualified[1]));
        Assert.Equal(longCode, Reconstruct(workbook, qualified[2]));
        Assert.Equal(longProfile + " / scan", Reconstruct(workbook, qualified[3]));
        var allSheets = new[] { (MiniXlsx.Worksheet)workbook }.Concat(workbook.AdditionalSheets).ToArray();
        foreach (var sheet in allSheets)
        foreach (var row in sheet.Rows)
        foreach (var cell in row.Cells.Where(c => c.Value.StartsWith("TEXT_PARTS:", StringComparison.Ordinal)))
        {
            Assert.Equal("TEXT_PARTS:" + Qualified(sheet, cell.Reference), cell.Value);
            Assert.NotEmpty(Reconstruct(workbook, cell.Value["TEXT_PARTS:".Length..]));
        }
        InSandbox(directory =>
        {
            var result = MeasurementDraftExcelWriter.Write(new[] { Record(longId) }, new[] { Finding("global") }, directory, "qualified", context);
            var saved = MiniXlsx.ReadAllSheets(result.XlsxPath);
            Assert.Equal(5, saved.Count);
            Assert.Equal(1, saved[0].Count(row => row.Count > 0 && row[0].Row > MeasurementDraftExcelWriter.HeaderRow));
            foreach (var field in qualified)
            {
                var chunks = saved[4].Where(row => row.Any(cell => cell.Column == "C" && cell.Text == field))
                    .OrderBy(row => int.Parse(row.Single(cell => cell.Column == "D").Text, CultureInfo.InvariantCulture))
                    .Select(row => row.Single(cell => cell.Column == "E").Text);
                Assert.Equal(Reconstruct(workbook, field), string.Concat(chunks));
            }
            using var zip = ZipFile.OpenRead(result.XlsxPath);
            for (var i = 1; i <= 5; i++)
            {
                using var stream = zip.GetEntry($"xl/worksheets/sheet{i}.xml")!.Open();
                var sheet = XDocument.Load(stream);
                Assert.Empty(sheet.Descendants(Ns + "f"));
                Assert.Equal("1", (string?)sheet.Descendants(Ns + "sheetView").Single().Attribute("rightToLeft"));
                Assert.Single(sheet.Descendants(Ns + "autoFilter"));
                Assert.Single(sheet.Descendants(Ns + "pane"));
            }
            Assert.Equal(result.XlsxHash, ArtifactHash.Sha256OfFile(result.XlsxPath));
        });
    }

    [Fact]
    public void MixedHebrewCadLayerFindingGetsWrappingRoomWithoutLosingItsTitle()
    {
        // Actual title from recorded scan row75320: the old 36pt estimate clipped
        // this mixed-direction identifier in the rendered findings worksheet.
        const string title = "השכבה '‎6422-GM-MODEL-NATAZ|PDF_tnua$305$EVEN_SAFA‎' נמדדה ביותר מממד אחד";
        var finding = new DeliveryFinding { FindingId = "mixed-cad-title", Title = title,
            Code = EstimateFindingCodes.Unmapped, Domain = "estimate", Severity = FindingSeverity.Warning };
        var workbook = MeasurementDraftExcelWriter.Build(new[] { Record() }, new[] { finding }, Context());
        var row = Findings(workbook).Rows.Single(r => r.Cells.Any(c => c.Value == "scan_finding"));
        Assert.Equal(title, Cell(row, "F").Value);
        Assert.True(row.HeightPoints >= 51, "Mixed Hebrew/CAD text must not use the clipped 36pt row height.");
        var json = Cell(row, "L");
        using var document = JsonDocument.Parse(json.Value.StartsWith("TEXT_PARTS:", StringComparison.Ordinal)
            ? Reconstruct(workbook, json.Value["TEXT_PARTS:".Length..]) : json.Value);
        Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void MissingEvidenceEmptyInputAndUnsafeNameFailWhileExistingWorkbooksArePreserved()
    {
        InSandbox(directory =>
        {
            Assert.Throws<InvalidOperationException>(() => MeasurementDraftExcelWriter.Write(Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>(), directory, "empty", Context()));
            Assert.Throws<ArgumentException>(() => MeasurementDraftExcelWriter.Write(new[] { Record() }, Array.Empty<DeliveryFinding>(), directory, "../escape", Context()));
            Assert.Throws<ArgumentException>(() => MeasurementDraftExcelWriter.Write(new[] { Record() }, Array.Empty<DeliveryFinding>(), directory, "invalid", Context() with { PublishedScanSha256 = "" }));
            Assert.Empty(Directory.GetFiles(directory));
            var first = MeasurementDraftExcelWriter.Write(new[] { Record() }, Array.Empty<DeliveryFinding>(), directory, "draft", Context());
            var second = MeasurementDraftExcelWriter.Write(new[] { Record(value: 7) }, Array.Empty<DeliveryFinding>(), directory, "draft", Context());
            Assert.NotEqual(first.XlsxPath, second.XlsxPath);
            Assert.Equal(first.XlsxHash, ArtifactHash.Sha256OfFile(first.XlsxPath));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        });
    }

    [ActualMeasurementDraftFact]
    public void Actual74891RecordScanExportsEveryIdMeasurementAndFindingWithoutPricing()
    {
        var stopwatch = Stopwatch.StartNew();
        using var input = File.OpenRead(ActualScanPath);
        var fixture = JsonSerializer.Deserialize<ActualScan>(input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(74891, fixture.Records.Count);
        var proposalPath = Path.Combine(Path.GetDirectoryName(ActualScanPath)!, "mapping_proposals.json");
        using var proposalDocument = JsonDocument.Parse(File.ReadAllBytes(proposalPath));
        var proposalRoot = proposalDocument.RootElement;
        var proposals = proposalRoot.GetProperty("proposals").Deserialize<List<MappingProposal>>()!;
        var context = new MeasurementDraftExcelWriter.WriteContext(fixture.ProjectProfileId, fixture.RunId, fixture.ProjectProfileHash,
            fixture.SourceDrawing, fixture.SourceDrawingHash, ActualScanPath, ArtifactHash.Sha256OfFile(ActualScanPath),
            proposals, proposalPath, ArtifactHash.Sha256OfFile(proposalPath),
            proposalRoot.GetProperty("reference_source").GetString(), proposalRoot.GetProperty("reference_hash").GetString());
        // Explicit environment override keeps a real product-writer replay output
        // for independent rendered QA. Otherwise tests clean their private sandbox.
        var evidenceDirectory = Environment.GetEnvironmentVariable("MAHOD_MEASUREMENT_DRAFT_REPLAY_DIR");
        void Replay(string directory)
        {
            var result = MeasurementDraftExcelWriter.Write(fixture.Records, fixture.Findings, directory, "measurement-draft-74891", context);
            var writtenAt = stopwatch.Elapsed;
            Assert.Equal(fixture.Records.Count, result.RecordCount);
            Assert.Equal(fixture.Records.Sum(r => r.Findings.Count), result.RecordFindingCount);
            Assert.Equal(fixture.Findings.Count, result.ScanFindingCount);
            using var zip = ZipFile.OpenRead(result.XlsxPath);
            var recordIndex = 0; var recordFindings = 0; var scanFindings = 0;
            var maxMeasurementRowHeight = 0d;
            Assert.Equal(5, zip.Entries.Count(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)));
            for (var sheetIndex = 1; sheetIndex <= 5; sheetIndex++)
            {
            using var sheet = zip.GetEntry($"xl/worksheets/sheet{sheetIndex}.xml")!.Open();
            using var reader = XmlReader.Create(sheet, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                using var subtree = reader.ReadSubtree();
                var row = XElement.Load(subtree);
                var number = int.Parse(row.Attribute("r")!.Value, CultureInfo.InvariantCulture);
                string Value(string column) => row.Elements(Ns + "c").SingleOrDefault(c => (string?)c.Attribute("r") == column + number)?.Value ?? "";
                if (sheetIndex == 1 && number > MeasurementDraftExcelWriter.HeaderRow && recordIndex < fixture.Records.Count)
                {
                    var original = fixture.Records[recordIndex++];
                    Assert.NotNull(row.Attribute("ht"));
                    var height = double.Parse(row.Attribute("ht")!.Value, CultureInfo.InvariantCulture);
                    Assert.InRange(height, 1d, 80d);
                    maxMeasurementRowHeight = Math.Max(maxMeasurementRowHeight, height);
                    Assert.Equal(original.RecordId, Value("B")); Assert.Equal(original.Measurement.Unit, Value("H"));
                    Assert.Equal(original.Measurement.Kind, Value("F"));
                    Assert.Equal(original.Measurement.RawValue.ToString("R", CultureInfo.InvariantCulture), Value("G"));
                    Assert.Equal("", Value("J")); Assert.Equal("", Value("K"));
                }
                if (sheetIndex == 2 && Value("A") == "record_finding") recordFindings++;
                if (sheetIndex == 2 && Value("A") == "scan_finding") scanFindings++;
                Assert.Empty(row.Descendants(Ns + "f"));
            }
            }
            Assert.Equal(74891, recordIndex); Assert.Equal(result.RecordFindingCount, recordFindings); Assert.Equal(result.ScanFindingCount, scanFindings);
            _output.WriteLine($"Actual scan replay: {recordIndex} records; {recordFindings} record findings; {scanFindings} scan findings; maximum measurement row height: {maxMeasurementRowHeight.ToString("R", CultureInfo.InvariantCulture)} pt. Export including input load: {writtenAt}. Total: {stopwatch.Elapsed}. XLSX: {result.XlsxPath}; SHA256: {result.XlsxHash}");
        }
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) InSandbox(Replay); else Replay(evidenceDirectory);
    }

    public sealed class ActualScan
    {
        public string RunId { get; set; } = "";
        public string ProjectProfileId { get; set; } = "";
        public string ProjectProfileHash { get; set; } = "";
        public string SourceDrawing { get; set; } = "";
        public string SourceDrawingHash { get; set; } = "";
        public List<NeutralQuantityRecord> Records { get; set; } = new();
        public List<DeliveryFinding> Findings { get; set; } = new();
    }
    public sealed class ActualMeasurementDraftFactAttribute : FactAttribute
    {
        public ActualMeasurementDraftFactAttribute()
        {
            if (!File.Exists(ActualScanPath)) Skip = "The exact local 2026-09-09 native scan is not available.";
        }
    }
    private static MiniXlsx.OutCell Cell(MiniXlsx.OutRow row, string column) => row.Cells.Single(c => c.Reference == column + row.Index);
    private static string FullText(MiniXlsx.Workbook workbook, MiniXlsx.OutRow row, string column)
    {
        var cell = Cell(row, column);
        return cell.Value.StartsWith("TEXT_PARTS:", StringComparison.Ordinal) ? Reconstruct(workbook, cell.Value["TEXT_PARTS:".Length..]) : cell.Value;
    }
    private static MiniXlsx.Worksheet Findings(MiniXlsx.Workbook workbook) =>
        workbook.AdditionalSheets.Single(s => s.SheetName == MeasurementDraftExcelWriter.FindingsSheetName);
    private static IEnumerable<MiniXlsx.OutRow> AllRows(MiniXlsx.Workbook workbook) =>
        workbook.Rows.Concat(workbook.AdditionalSheets.SelectMany(sheet => sheet.Rows));
    private static string Qualified(MiniXlsx.Worksheet sheet, string cell) => "'" + sheet.SheetName.Replace("'", "''") + "'!" + cell;
    private static string Reconstruct(MiniXlsx.Workbook workbook, string field) => string.Concat(workbook.AdditionalSheets
        .Single(s => s.SheetName == MeasurementDraftExcelWriter.TextPartsSheetName).Rows
        .Where(r => r.Index > MeasurementDraftExcelWriter.HeaderRow && Cell(r, "C").Value == field)
        .OrderBy(r => double.Parse(Cell(r, "D").Value, CultureInfo.InvariantCulture)).Select(r => Cell(r, "E").Value));
    private static XDocument Sheet(string path)
    {
        using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); return XDocument.Load(stream);
    }
    private static void InSandbox(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "measurement-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, recursive: true); }
    }
}
