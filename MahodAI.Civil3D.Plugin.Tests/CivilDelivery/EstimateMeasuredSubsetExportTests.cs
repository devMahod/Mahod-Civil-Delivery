using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Positive ENGINE -> XLSX regression from one recorded Civil measurement, NOT a
/// successful live scan or a complete estimate for 6422. Mapping/earthworks authority
/// below belongs solely to this simulation and is never persisted to a user profile.
/// </summary>
public sealed class EstimateMeasuredSubsetExportTests
{
    [Fact]
    public void RecordedMazaSubset_WithTestOnlyApproval_ExportsExactQuantityPriceAndTrace()
    {
        // Transcribed from the saved 2026-08-31 run's neutral_quantity_records.json:
        // estimate-extract-20260831-154323-c1ae4366, cq-0008. The original full scan
        // has OTHER unmapped quantities and global blockers. Those are not asserted
        // resolved here; this fixture deliberately contains one measured record only.
        const string drawingHash = "91fc990051fa62c751244a594a850ed73bc8f03e2c0d1e165f7e5838e0a9226e";
        const string catalogCode = "U51.03.0010";
        const string label = "סימולציית תת-קבוצה בלבד — לא אומדן פרויקט 6422";
        const string approver = "SIMULATION-ONLY-NOT-AN-ENGINEER-APPROVAL";
        const double recordedVolume = 7374.120178469701;
        var snapshot = EstimateFixtures.Snapshot();
        var item = snapshot.Items[catalogCode];
        var price = snapshot.Prices[catalogCode].Price!.Value;
        Assert.Equal("m3", item.Unit.Canonical);
        Assert.True(price > 0m);
        Assert.Equal(ArtifactHash.Sha256OfFile(EstimateFixtures.PriceBookPath), snapshot.FileHash);

        var profile = EstimateFixtures.Profile();
        profile.ProjectName = label;
        profile.Estimate.Earthworks.Requested = false;
        profile.Estimate.Earthworks.DecidedBy = approver;
        profile.Estimate.Earthworks.DecidedAtUtc = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
        profile.Estimate.Earthworks.Reason = "Test-only material-volume subset; no cut/fill claim.";
        var record = new NeutralQuantityRecord
        {
            RecordId = "cq-0008",
            ProjectProfileId = "6422",
            RunId = "estimate-extract-20260831-154323-c1ae4366",
            Source = new QuantitySource
            {
                Drawing = "6422-CIVIL-WEST-WORK.run1-2applied.dwg",
                DrawingHash = drawingHash,
                Handle = "127D0",
                EntityType = "CORRIDOR",
                Layer = "corridor:MAZA",
                CivilIdentity = "2000/baseline-001:BL - 2000 - (1)",
                StationFrom = 40160,
                StationTo = 42880,
            },
            Measurement = new QuantityMeasurement
            {
                Kind = "volume",
                Method = "corridor-qto-avg-end-area",
                RawValue = recordedVolume,
                Unit = "מ\"ק",
                Parameters = { ["stations"] = "463", ["max_gap_m"] = "20.0" },
            },
            Classification = new QuantityClassification
            {
                RuleKey = "layer:corridor:MAZA|volume",
                CandidateCatalogCode = catalogCode,
                ApprovedCatalogId = snapshot.SnapshotId,
                ApprovedCatalogHash = snapshot.FileHash,
                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item),
                MappingApprovedBy = approver,
                MappingApprovedAtUtc = profile.Estimate.Earthworks.DecidedAtUtc,
            },
        };
        var estimate = EstimateBuilder.Build(new[] { record }, snapshot, profile,
            runId: "SIMULATION-ONLY-RECORDED-CQ0008-SUBSET");
        var line = Assert.Single(estimate.Lines);
        Assert.True(EstimatePreflightPolicy.CanExport(estimate),
            string.Join(", ", EstimatePreflightPolicy.ExportBlockingReasons(estimate)));
        Assert.Equal(EstimateBuildContext.NeutralRecordSet, estimate.SourceSnapshotKind);
        Assert.NotEqual(EstimateBuildContext.CivilLiveSaved, estimate.SourceSnapshotKind);
        Assert.Null(estimate.SourceDbMod);
        Assert.Equal(recordedVolume, line.RawQuantity);
        Assert.Equal(7374.1202, line.BoqQuantity);
        // Independent decimal calculation: do not call the production BOQ helper.
        var expectedTotal = Math.Round(7374.1202m * price, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedTotal, line.Total);
        Assert.Equal(expectedTotal, estimate.CleanTotal);
        Assert.Equal(drawingHash, line.SourceDrawingHash);
        Assert.Equal("127D0", line.SourceHandle);
        Assert.Equal(record.Source.CivilIdentity, line.SourceCivilIdentity);

        var temp = Path.Combine(Path.GetTempPath(), "mcd-measured-subset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var written = EstimateExcelWriter.Write(estimate, temp, "SIMULATION-ONLY-SUBSET",
                new EstimateExcelWriter.WriteOptions(ProjectTitle: label));
            using var document = SpreadsheetDocument.Open(written.XlsxPath, false);
            var workbook = document.WorkbookPart!;
            var sheets = workbook.Workbook.Sheets!.Elements<Sheet>().ToArray();
            Assert.Equal(new[] { "כתב כמויות", "לא מתומחר", "ממצאים", "עקבה", "זהות ראיות" },
                sheets.Select(candidate => candidate.Name!.Value).ToArray());
            var sheet = sheets.Single(candidate => candidate.Name!.Value == "כתב כמויות");
            var worksheet = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var row = worksheet.Worksheet.Descendants<Row>().Single(candidate =>
                candidate.Elements<Cell>().Any(cell => cell.InlineString?.Text?.Text == catalogCode));
            Cell Column(string column) => row.Elements<Cell>().Single(cell =>
                cell.CellReference!.Value!.StartsWith(column, StringComparison.Ordinal));
            var outputQuantity = decimal.Parse(Column("E").CellValue!.Text, CultureInfo.InvariantCulture);
            var outputPrice = decimal.Parse(Column("F").CellValue!.Text, CultureInfo.InvariantCulture);
            Assert.Equal(7374.1202m, outputQuantity);
            Assert.Equal(price, outputPrice);
            Assert.Equal(expectedTotal, Math.Round(outputQuantity * outputPrice, 2, MidpointRounding.AwayFromZero));
            Assert.Equal($"ROUND(E{row.RowIndex!.Value}*F{row.RowIndex.Value},2)", Column("G").CellFormula!.Text);
            Assert.Contains(label, string.Join("\n", worksheet.Worksheet.Descendants<Text>().Select(text => text.Text)));

            using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
            Assert.Equal(drawingHash, audit.RootElement.GetProperty("source_drawing_hash").GetString());
            Assert.Equal(snapshot.FileHash, audit.RootElement.GetProperty("price_book_hash").GetString());
            Assert.Equal(EstimateBuildContext.NeutralRecordSet,
                audit.RootElement.GetProperty("source_snapshot_kind").GetString());
            Assert.Equal(expectedTotal, audit.RootElement.GetProperty("clean_total").GetDecimal());
            Assert.Contains(approver, audit.RootElement.GetRawText());
            Assert.Equal(ArtifactHash.Sha256OfFile(written.XlsxPath), written.XlsxHash);
            Assert.Equal(ArtifactHash.Sha256OfFile(written.AuditPath), written.AuditHash);
            Assert.Equal(ArtifactHash.Sha256OfFile(written.ManifestPath), written.ManifestHash);
        }
        finally
        {
            // Exact test-owned GUID directory, never a user source or profile path.
            Directory.Delete(temp, true);
        }
    }
}
