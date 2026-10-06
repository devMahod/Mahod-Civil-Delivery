using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Synthetic export-contract tests, not CAD measurements or project approvals.
/// Compact worksheets are presentation only: every source line and finding must
/// still survive unchanged in the authoritative audit artifact.
/// </summary>
public sealed class EstimateWorkbookScaleTests : IDisposable
{
    private const string Drawing = @"C:\SYNTHETIC-WORKBOOK-SCALE\GM.dwg";
    private static readonly string DrawingHash = new('a', 64);
    private static readonly string CatalogHash = new('b', 64);
    private static readonly string[] PricedCodes = { "S08.01.0001", "S40.01.0001", "S51.06.1900" };
    private const string MissingPriceCode = "S60.01.0001";
    private static readonly DateTime ApprovedAt = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Mahod-workbook-scale-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void EightyThousandUnmappedRecords_DoNotInflateWorkbook_AndFullAuditIsPreserved()
    {
        var catalog = Catalog();
        var priced = Enumerable.Range(0, 300).Select(i => Record(i, catalog, PricedCodes[i % 3])).ToArray();
        var unmapped = Enumerable.Range(300, 80_000).Select(i => Record(i, catalog, null)).ToArray();
        var estimate = Build(priced.Concat(unmapped).ToArray(), catalog);
        var evaluation = EstimatePartialPricedDraftPolicy.Evaluate(estimate);
        Assert.True(evaluation.CanExport, string.Join(", ", evaluation.BlockingReasons));
        Assert.Equal(300, evaluation.EligibleLineCount);
        Assert.Equal(80_000, evaluation.UnresolvedLineCount);
        Assert.False(EstimatePreflightPolicy.CanExport(estimate));
        var expectedAudit = EstimateResultArtifact.From(estimate);
        var written = EstimateExcelWriter.WritePartialPricedDraft(estimate, directory, "SYNTHETIC-80300");
        var sheets = ReadSheets(written.XlsxPath);
        AssertSheetNames(sheets);
        Assert.InRange(Rows(sheets["כתב כמויות"]).Count, 1, 80);
        Assert.InRange(sheets.Values.Sum(sheet => Rows(sheet).Count), 1, 2499);

        // Presentation row count is independent of how many objects need mapping.
        var smallEstimate = Build(priced.Append(Record(300, catalog, null)).ToArray(), catalog);
        var small = ReadSheets(EstimateExcelWriter.WritePartialPricedDraft(smallEstimate, directory, "SYNTHETIC-301").XlsxPath);
        Assert.Equal(Rows(small["כתב כמויות"]).Count, Rows(sheets["כתב כמויות"]).Count);
        AssertPricedSheet(estimate, sheets["כתב כמויות"]);
        AssertTrace(estimate, sheets["עקבה"]);
        Assert.All(sheets.Values, AssertFormulaReferencesExist);

        var unpricedText = Text(sheets["לא מתומחר"]);
        Assert.Contains(sheets["לא מתומחר"].Descendants(Main + "v"), value => value.Value == "80000");
        Assert.Contains("UNKNOWN-SYNTHETIC", unpricedText);
        var findings = sheets["ממצאים"];
        var summary = Rows(findings).Where(row => Cells(row).Any(cell => CellText(cell) == EstimateFindingCodes.Unmapped)).ToArray();
        Assert.Single(summary);
        Assert.Contains(summary, row => row.Descendants(Main + "v").Any(value => value.Value == "80000"));
        // At most twenty examples, never eighty thousand per-object titles.
        Assert.InRange(Rows(findings).Count(row => CellText(Cell(row, "A")) == "דוגמה"
            && Text(row).Contains("אין שיוך לסעיף מחירון לרשומה", StringComparison.Ordinal)), 0, 20);

        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        Assert.Equal(3, audit.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(80_300, audit.RootElement.GetProperty("lines").GetArrayLength());
        AssertExactJsonSequence(expectedAudit.Lines, audit.RootElement.GetProperty("lines"));
        AssertExactJsonSequence(expectedAudit.Findings, audit.RootElement.GetProperty("findings"));
        // Builder-produced EST-UNMAPPED findings are line-local; global findings
        // live in the registry. Do not require an audit schema/layout migration.
        Assert.Equal(80_000, audit.RootElement.GetProperty("findings").EnumerateArray()
            .Concat(audit.RootElement.GetProperty("lines").EnumerateArray()
                .SelectMany(line => line.GetProperty("findings").EnumerateArray()))
            .Count(finding => finding.GetProperty("code").GetString() == EstimateFindingCodes.Unmapped));
        Assert.Equal(estimate.CleanTotal, audit.RootElement.GetProperty("clean_total").GetDecimal());
        Assert.Contains(CatalogHash, Text(sheets["זהות ראיות"]));
        Assert.Contains(DrawingHash, Text(sheets["זהות ראיות"]));
        Assert.Contains(Drawing, Text(sheets["זהות ראיות"]));
        Assert.Contains(Path.GetFileName(written.ManifestPath), Text(sheets["זהות ראיות"]));
        Assert.Equal(written.XlsxHash, ArtifactHash.Sha256OfFile(written.XlsxPath));
        Assert.Equal(written.AuditHash, ArtifactHash.Sha256OfFile(written.AuditPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothExportModes_HaveFiveSheets_AndTotalsCoverEveryPricedChapterExactlyOnce(bool partialDraft)
    {
        var catalog = Catalog();
        var records = Enumerable.Range(0, 12).Select(i => Record(i, catalog, PricedCodes[i % 3])).ToArray();
        var estimate = Build(records, catalog);
        Assert.True(EstimatePreflightPolicy.CanExport(estimate));
        var written = partialDraft
            ? EstimateExcelWriter.WritePartialPricedDraft(estimate, directory, "SYNTHETIC-READY-PARTIAL")
            : EstimateExcelWriter.Write(estimate, directory, "SYNTHETIC-READY-FULL");
        var sheets = ReadSheets(written.XlsxPath);
        AssertSheetNames(sheets);
        AssertPricedSheet(estimate, sheets["כתב כמויות"]);
        AssertTrace(estimate, sheets["עקבה"]);
        Assert.All(sheets.Values, AssertFormulaReferencesExist);
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        Assert.Equal(partialDraft ? 3 : 2, audit.RootElement.GetProperty("schema_version").GetInt32());
        AssertExactJsonSequence(estimate.Findings, audit.RootElement.GetProperty("findings"));
        if (partialDraft)
            AssertExactJsonSequence(EstimateResultArtifact.From(estimate).Lines, audit.RootElement.GetProperty("lines"));
        else
            AssertExactJsonSequence(estimate.Lines, audit.RootElement.GetProperty("lines"));
    }

    [Fact]
    public void UnpricedGroups_PreserveUnitsAndStatus_ButContainNoPricesOrTotals()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Record(0, catalog, PricedCodes[0]),
            Record(1, catalog, MissingPriceCode),
            Record(2, catalog, PricedCodes[0], unit: "מ\"ר"), // known catalog item, incompatible measured dimension
            Record(3, catalog, null),
            Record(4, catalog, null, unit: "מ\"ר"), // same layer, different physical dimension must remain separate
        };
        var estimate = Build(records, catalog);
        Assert.Equal(1, estimate.Lines.Count(line => line.IncludedInTotals));
        Assert.Contains(estimate.Lines, line => line.PriceStatus == PriceStatus.MissingPrice);
        Assert.Contains(estimate.Lines.SelectMany(line => line.Findings), finding => finding.Code == EstimateFindingCodes.UnitMismatch);
        var verdict = EstimatePartialPricedDraftPolicy.Evaluate(estimate);
        Assert.True(verdict.CanExport, string.Join(", ", verdict.BlockingReasons));
        var written = EstimateExcelWriter.WritePartialPricedDraft(estimate, directory, "SYNTHETIC-STATUSES");
        var sheets = ReadSheets(written.XlsxPath);
        AssertPricedSheet(estimate, sheets["כתב כמויות"]);
        var unpriced = sheets["לא מתומחר"];
        Assert.Empty(unpriced.Descendants(Main + "f"));
        var expected = EstimateBoqSemantics.GroupLines(estimate.Lines).Where(group => !group.IncludedInTotals).ToArray();
        Assert.Equal(4, expected.Length);
        var allText = Text(unpriced);
        Assert.Contains("חסר מחיר", allText);
        Assert.Contains("אי-התאמת יחידות", allText);
        foreach (var group in expected)
        {
            var rows = Rows(unpriced).Where(row => Cells(row).Any(cell => CellText(cell) == group.Unit)
                && Text(row).Contains(group.Status, StringComparison.Ordinal)
                && Text(row).Contains(group.CatalogCode ?? "UNKNOWN-SYNTHETIC", StringComparison.Ordinal)).ToArray();
            Assert.Single(rows);
            // The appendix has status and object count, not a price/total pair.
            Assert.Null(Cell(rows[0], "F")?.Element(Main + "v"));
            Assert.Equal(group.ObjectCount, Number(Cell(rows[0], "G")!));
            Assert.Equal(group.Quantity, Number(Cell(rows[0], "E")!));
            Assert.Null(Cell(rows[0], "H")?.Element(Main + "v"));
        }
        AssertTrace(estimate, sheets["עקבה"]);
        Assert.All(sheets.Values, AssertFormulaReferencesExist);
    }

    [Fact]
    public void FindingsSummary_DeduplicatesRegistryReferences_ButSeparatesSeverityAndResolution()
    {
        const string code = "EST-SYNTHETIC-SUMMARY-ONLY";
        var catalog = Catalog();
        var records = new[] { Record(0, catalog, PricedCodes[0]), Record(1, catalog, null) };
        var findings = Enumerable.Range(0, 32).Select(index => new DeliveryFinding
        {
            FindingId = "SYNTHETIC-SUMMARY-" + index.ToString("D3", CultureInfo.InvariantCulture),
            Code = code, Domain = "estimate", Severity = index >= 28 ? FindingSeverity.Info : FindingSeverity.Warning,
            Title = "Synthetic example " + index, Message = "Synthetic evidence, not an engineering decision.",
            AffectedRecordIds = new() { records[1].RecordId },
            ResolvedAtUtc = index is >= 24 and < 28 ? ApprovedAt : null,
            ResolvedBy = index is >= 24 and < 28 ? "SYNTHETIC-TEST-ONLY" : null,
            Resolution = index is >= 24 and < 28 ? "SYNTHETIC resolution for display-contract test" : null,
        }).ToArray();
        var estimate = Build(records, catalog, findings);
        // The same finding is represented in both the registry and an affected line.
        estimate.Lines.Single(line => line.RecordId == records[1].RecordId).Findings.Add(findings[0]);
        var written = EstimateExcelWriter.WritePartialPricedDraft(estimate, directory, "SYNTHETIC-SUMMARY-STATES");
        var sheet = ReadSheets(written.XlsxPath)["ממצאים"];
        var groups = Rows(sheet).Where(row => CellText(Cell(row, "A")) == code).ToArray();
        Assert.Equal(3, groups.Length);
        Assert.Equal(new[] { 4m, 4m, 24m }, groups.Select(row => Number(Cell(row, "D")!)).OrderBy(count => count));
        Assert.All(groups, row => Assert.Equal(1m, Number(Cell(row, "E")!)));
        Assert.Contains(groups, row => CellText(Cell(row, "B")).Contains("הוכרע", StringComparison.Ordinal));
        Assert.Contains(groups, row => CellText(Cell(row, "B")).Contains(nameof(FindingSeverity.Info), StringComparison.Ordinal));
        Assert.Equal(28, Rows(sheet).Count(row => CellText(Cell(row, "A")) == "דוגמה"
            && CellText(Cell(row, "C")).StartsWith("Synthetic example ", StringComparison.Ordinal)));
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        AssertExactJsonSequence(EstimateResultArtifact.From(estimate).Findings, audit.RootElement.GetProperty("findings"));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidQuantitySentinel_IsNotPrintedAsZero_OrHiddenInValidUnpricedPeer(double invalidValue)
    {
        var catalog = Catalog();
        var records = new[]
        {
            Record(0, catalog, PricedCodes[0]),
            Record(1, catalog, null, quantity: 12.25),
            Record(2, catalog, null, quantity: invalidValue),
        };
        var estimate = Build(records, catalog);
        var invalid = estimate.Lines.Single(line => line.RecordId == records[2].RecordId);
        Assert.Equal(DeliveryStatus.Failed, invalid.Status);
        Assert.Equal(0d, invalid.BoqQuantity); // storage sentinel, explicitly not measured zero
        Assert.False(invalid.IncludedInTotals);
        Assert.Single(EstimateBoqSemantics.GroupLines(estimate.Lines.Where(line => !line.IncludedInTotals)));
        var verdict = EstimatePartialPricedDraftPolicy.Evaluate(estimate);
        Assert.True(verdict.CanExport, string.Join(", ", verdict.BlockingReasons));
        var written = EstimateExcelWriter.WritePartialPricedDraft(estimate, directory, "SYNTHETIC-INVALID-QUANTITY");
        var sheet = ReadSheets(written.XlsxPath)["לא מתומחר"];
        var groups = Rows(sheet).Where(row => CellText(Cell(row, "C")) == "UNKNOWN-SYNTHETIC").ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Single(groups.Where(row => Cell(row, "E")?.Element(Main + "v")?.Value == "12.25"));
        var unavailable = Assert.Single(groups.Where(row => CellText(Cell(row, "E")) == "לא נמדד / לא תקין"));
        Assert.Null(Cell(unavailable, "E")!.Element(Main + "v"));
        Assert.All(groups, row => Assert.Equal(1m, Number(Cell(row, "G")!)));
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        var expected = EstimateResultArtifact.From(estimate);
        AssertExactJsonSequence(expected.Lines, audit.RootElement.GetProperty("lines"));
        AssertExactJsonSequence(expected.Findings, audit.RootElement.GetProperty("findings"));
    }

    [Fact]
    public void TinyPositiveUnpricedMeasurement_IsDistinctFromInvalidAndOrdinaryQuantity_WithoutChangingAudit()
    {
        var catalog = Catalog();
        const double tiny = 0.00002319711931338026;
        var records = new[]
        {
            Record(0, catalog, PricedCodes[0]),
            Record(1, catalog, null, quantity: 12.25),
            Record(2, catalog, null, quantity: 0),
            Record(3, catalog, null, quantity: tiny),
        };
        var estimate = Build(records, catalog);
        var tinyLine = estimate.Lines.Single(line => line.RecordId == records[3].RecordId);
        Assert.Equal(tiny, tinyLine.RawQuantity);
        Assert.Equal(0d, tinyLine.BoqQuantity);
        Assert.DoesNotContain(tinyLine.Findings, finding => finding.Code == EstimateFindingCodes.MeasurementFailed);
        Assert.Single(EstimateBoqSemantics.GroupLines(estimate.Lines.Where(line => !line.IncludedInTotals)));
        var verdict = EstimatePartialPricedDraftPolicy.Evaluate(estimate);
        Assert.True(verdict.CanExport, string.Join(", ", verdict.BlockingReasons));
        var expectedAudit = EstimateResultArtifact.From(estimate);
        var written = EstimateExcelWriter.WritePartialPricedDraft(estimate, directory, "SYNTHETIC-TINY-QUANTITY");
        var sheet = ReadSheets(written.XlsxPath)["לא מתומחר"];
        var groups = Rows(sheet).Where(row => CellText(Cell(row, "C")) == "UNKNOWN-SYNTHETIC").ToArray();
        Assert.Equal(3, groups.Length);
        Assert.Single(groups.Where(row => Cell(row, "E")?.Element(Main + "v")?.Value == "12.25"));
        var invalid = Assert.Single(groups.Where(row => CellText(Cell(row, "E")) == "לא נמדד / לא תקין"));
        var rounded = Assert.Single(groups.Where(row => CellText(Cell(row, "E")) == "כמות חיובית שעוגלה ל־0; ראה מדידה מקורית"));
        Assert.Null(Cell(invalid, "E")!.Element(Main + "v"));
        Assert.Null(Cell(rounded, "E")!.Element(Main + "v"));
        Assert.All(groups, row => Assert.Equal(1m, Number(Cell(row, "G")!)));
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        AssertExactJsonSequence(expectedAudit.Lines, audit.RootElement.GetProperty("lines"));
        AssertExactJsonSequence(expectedAudit.Findings, audit.RootElement.GetProperty("findings"));
    }

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC-WORKBOOK-NOT-PROJECT-AUTHORITY", FileHash = CatalogHash };
        foreach (var (code, index) in PricedCodes.Append(MissingPriceCode).Select((code, index) => (code, index)))
        {
            catalog.Items[code] = new() { Code = code, Description = "SYNTHETIC independent length " + code, UnitRaw = "מטר" };
            if (code != MissingPriceCode)
                catalog.Prices[code] = new() { Code = code, Price = 12.345678m + index, PriceBookId = catalog.SnapshotId, SourceHash = catalog.FileHash };
        }
        return catalog;
    }

    private static NeutralQuantityRecord Record(int index, CatalogSnapshot catalog, string? code, string unit = "מטר", double? quantity = null)
    {
        var area = unit == "מ\"ר";
        var layer = code == null ? "UNKNOWN-SYNTHETIC" : "SYNTHETIC-" + code + (area ? "-AREA" : "");
        return new()
        {
            RecordId = "SYNTHETIC-" + index.ToString("D6", CultureInfo.InvariantCulture),
            ProjectProfileId = "SYNTHETIC-WORKBOOK-ONLY", RunId = "SYNTHETIC-SCALE-SCAN",
            Source = new()
            {
                Drawing = "GM.dwg", DrawingPath = Drawing, DrawingHash = DrawingHash,
                Handle = "A/" + (index + 0x1000).ToString("X", CultureInfo.InvariantCulture),
                Xref = "GM", EntityType = area ? "HATCH" : "LINE", Layer = layer,
            },
            Measurement = new()
            {
                Kind = area ? "area" : "length", Method = area ? "hatch-area+xref-transform" : "line-length+xref-transform",
                RawValue = quantity ?? 0.3333 + index % 7, Unit = unit,
            },
            Classification = new()
            {
                RuleKey = "layer:" + layer + (area ? "|area" : "|length"), CandidateCatalogCode = code,
                ApprovedCatalogId = code == null ? null : catalog.SnapshotId,
                ApprovedCatalogHash = code == null ? null : CatalogHash,
                ApprovedCatalogItemFingerprint = code == null ? null : CatalogIdentity.ItemFingerprint(catalog.Items[code]),
                MappingApprovedBy = code == null ? null : "SYNTHETIC-TEST-NOT-ENGINEER-APPROVAL",
                MappingApprovedAtUtc = code == null ? null : ApprovedAt,
            },
        };
    }

    private static EstimateResult Build(IReadOnlyList<NeutralQuantityRecord> records, CatalogSnapshot catalog,
        IEnumerable<DeliveryFinding>? findings = null)
    {
        var profile = new ProjectProfile
        {
            ProfileId = "SYNTHETIC-WORKBOOK-ONLY", ProjectName = "SIMULATION — not a live project estimate",
            Estimate = new()
            {
                QuantitySources = new()
                {
                    SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy,
                    XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy,
                },
            },
        };
        var context = EstimateTraceIdentity.InferHeadless(records, profile) with
        {
            ExternalSources = new[] { new EstimateExternalSource { DrawingPath = Drawing, DrawingHash = DrawingHash, XrefChain = "GM", ReferenceHandlePath = "A" } },
        };
        return EstimateBuilder.Build(records, catalog, profile, "SYNTHETIC-WORKBOOK-EXPORT", findings ?? Array.Empty<DeliveryFinding>(), context);
    }

    private static void AssertPricedSheet(EstimateResult estimate, XDocument sheet)
    {
        var expected = EstimateBoqSemantics.GroupLines(estimate.Lines).Where(group => group.IncludedInTotals).ToArray();
        var rows = Rows(sheet).Where(row => PricedCodes.Contains(CellText(Cell(row, "B")))).ToArray();
        Assert.Equal(expected.Length, rows.Length);
        foreach (var group in expected)
        {
            var row = Assert.Single(rows.Where(row => CellText(Cell(row, "B")) == group.CatalogCode));
            Assert.Equal(group.Unit, CellText(Cell(row, "D")));
            Assert.Equal(group.Quantity, Number(Cell(row, "E")!));
            Assert.Equal(group.Price, Number(Cell(row, "F")!));
            Assert.Equal(group.Status, CellText(Cell(row, "H")));
            var index = (string)row.Attribute("r")!;
            Assert.Equal($"ROUND(E{index}*F{index},2)", Cell(row, "G")!.Element(Main + "f")!.Value);
        }
        var detailCells = rows.Select(row => "G" + (string)row.Attribute("r")!).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var sumCells = sheet.Descendants(Main + "c").Where(cell => cell.Element(Main + "f")?.Value.StartsWith("SUM(", StringComparison.Ordinal) == true).ToArray();
        var totals = sumCells.Where(cell => ExpandSum(cell.Element(Main + "f")!.Value).OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(detailCells)).ToArray();
        Assert.NotEmpty(totals);
        var allCells = sheet.Descendants(Main + "c").ToDictionary(cell => (string)cell.Attribute("r")!, StringComparer.Ordinal);
        foreach (var total in totals) Assert.Equal(estimate.CleanTotal, Evaluate((string)total.Attribute("r")!, allCells));
        // Each chapter SUM contains only detail cells, not other chapter sums.
        foreach (var sum in sumCells) Assert.All(ExpandSum(sum.Element(Main + "f")!.Value), reference => Assert.Contains(reference, detailCells));
        Assert.DoesNotContain("UNKNOWN-SYNTHETIC", Text(sheet));
    }

    private static void AssertTrace(EstimateResult estimate, XDocument trace)
    {
        var expected = estimate.Lines.Where(line => line.IncludedInTotals).Select(line => $"{line.LineId} / {line.RecordId}").OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var actual = Rows(trace).Select(row => CellText(Cell(row, "A"))).Where(value => value.Contains(" / SYNTHETIC-", StringComparison.Ordinal)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    private static void AssertFormulaReferencesExist(XDocument sheet)
    {
        var cells = sheet.Descendants(Main + "c").ToDictionary(cell => (string)cell.Attribute("r")!, StringComparer.Ordinal);
        Assert.DoesNotContain(cells.Values, cell => (string?)cell.Attribute("t") == "e");
        foreach (var formula in sheet.Descendants(Main + "f"))
        {
            Assert.DoesNotContain("#REF!", formula.Value);
            Assert.DoesNotContain("[", formula.Value); // no external workbook dependency
            var references = formula.Value.StartsWith("SUM(", StringComparison.Ordinal)
                ? ExpandSum(formula.Value)
                : Regex.Matches(formula.Value, @"\b[A-Z]+[1-9][0-9]*\b").Select(match => match.Value);
            foreach (var reference in references) Assert.True(cells.ContainsKey(reference), "Missing formula reference: " + reference);
        }
    }

    // Independent evaluator for the writer's deliberately small formula vocabulary.
    // It checks actual OOXML formulas, not just the in-memory estimate total.
    private static decimal Evaluate(string reference, IReadOnlyDictionary<string, XElement> cells, int depth = 0)
    {
        Assert.InRange(depth, 0, 20);
        var cell = cells[reference];
        var formula = cell.Element(Main + "f")?.Value;
        if (formula == null) return Number(cell);
        if (formula.StartsWith("SUM(", StringComparison.Ordinal)) return ExpandSum(formula).Sum(part => Evaluate(part, cells, depth + 1));
        var round = Regex.Match(formula, @"^ROUND\(([A-Z]+[0-9]+)\*([A-Z]+[0-9]+),2\)$");
        if (round.Success) return Math.Round(Evaluate(round.Groups[1].Value, cells, depth + 1) * Evaluate(round.Groups[2].Value, cells, depth + 1), 2, MidpointRounding.AwayFromZero);
        Assert.Matches(@"^[A-Z]+[1-9][0-9]*$", formula);
        return Evaluate(formula, cells, depth + 1);
    }

    private static IEnumerable<string> ExpandSum(string formula)
    {
        Assert.StartsWith("SUM(", formula); Assert.EndsWith(")", formula);
        foreach (var part in formula[4..^1].Split(','))
        {
            var range = Regex.Match(part, @"^([A-Z]+)([1-9][0-9]*):\1([1-9][0-9]*)$");
            if (!range.Success) { Assert.Matches(@"^[A-Z]+[1-9][0-9]*$", part); yield return part; continue; }
            var first = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            var last = int.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
            Assert.True(last >= first);
            for (var row = first; row <= last; row++) yield return range.Groups[1].Value + row.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AssertExactJsonSequence<T>(IReadOnlyList<T> expected, JsonElement actual)
    {
        Assert.Equal(expected.Count, actual.GetArrayLength());
        var index = 0;
        foreach (var value in actual.EnumerateArray())
            AssertJsonEqual(JsonSerializer.SerializeToElement(expected[index++]), value);
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = expected.EnumerateObject().ToArray();
                Assert.Equal(properties.Length, actual.EnumerateObject().Count());
                foreach (var property in properties) AssertJsonEqual(property.Value, actual.GetProperty(property.Name));
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                var index = 0;
                foreach (var value in expected.EnumerateArray()) AssertJsonEqual(value, actual[index++]);
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    private static Dictionary<string, XDocument> ReadSheets(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var relationships = Xml(zip, "xl/_rels/workbook.xml.rels").Descendants(PackageRel + "Relationship")
            .ToDictionary(node => (string)node.Attribute("Id")!, node => (string)node.Attribute("Target")!, StringComparer.Ordinal);
        return Xml(zip, "xl/workbook.xml").Descendants(Main + "sheet").ToDictionary(
            node => (string)node.Attribute("name")!,
            node => Xml(zip, "xl/" + relationships[(string)node.Attribute(Rel + "id")!]), StringComparer.Ordinal);
    }

    private static void AssertSheetNames(IReadOnlyDictionary<string, XDocument> sheets) =>
        Assert.Equal(new[] { "כתב כמויות", "לא מתומחר", "ממצאים", "עקבה", "זהות ראיות" }, sheets.Keys);
    private static XDocument Xml(ZipArchive zip, string path) { using var stream = zip.GetEntry(path)!.Open(); return XDocument.Load(stream); }
    private static List<XElement> Rows(XDocument sheet) => sheet.Descendants(Main + "row").ToList();
    private static IEnumerable<XElement> Cells(XElement row) => row.Elements(Main + "c");
    private static XElement? Cell(XElement row, string column) => Cells(row).SingleOrDefault(cell => (string?)cell.Attribute("r") == column + (string?)row.Attribute("r"));
    private static string CellText(XElement? cell) => cell == null ? "" : string.Concat(cell.Descendants(Main + "t").Select(text => text.Value));
    private static string Text(XContainer node) => string.Join("\n", node.Descendants(Main + "t").Select(text => text.Value));
    private static decimal Number(XElement cell) => decimal.Parse(cell.Element(Main + "v")!.Value, CultureInfo.InvariantCulture);
}
