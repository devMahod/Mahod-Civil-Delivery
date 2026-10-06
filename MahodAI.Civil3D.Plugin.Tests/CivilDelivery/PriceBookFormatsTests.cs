using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Israeli price books share one shape - chapter.sub.item codes, Hebrew units,
    /// header rows without prices - but differ in header wording, column order and
    /// whether codes carry a publisher prefix. The loader must read any of them by
    /// recognising headers, not by assuming the NTI 08/2025 column positions.
    ///
    /// The Dekel rows below are verbatim from the published May-2025 extract
    /// (chapter 68, בדיקות מעבדה), so the format under test is the real one.
    /// </summary>
    public class PriceBookFormatsTests
    {
        // ------------------------------------------------------------ fixtures

        private static string TempXlsx()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mcd_pb_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "book.xlsx");
        }

        /// <summary>Writes a one-sheet workbook from rows of strings.</summary>
        private static string WriteBook(params string[][] rows)
        {
            var path = TempXlsx();
            using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var ws = wb.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            ws.Worksheet = new Worksheet(sheetData);
            var sheets = wb.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "מחירון" });

            uint r = 1;
            foreach (var row in rows)
            {
                var xr = new Row { RowIndex = r };
                for (int c = 0; c < row.Length; c++)
                {
                    var col = ((char)('A' + c)).ToString();
                    xr.Append(new Cell
                    {
                        CellReference = $"{col}{r}",
                        DataType = CellValues.String,
                        CellValue = new CellValue(row[c]),
                    });
                }
                sheetData.Append(xr);
                r++;
            }
            wb.Workbook.Save();
            return path;
        }

        /// <summary>Dekel May-2025 layout: סעיף | תאור | יחידה | כמות | מחיר, no U prefix.</summary>
        private static string DekelBook() => WriteBook(
            new[] { "מהדורה: מחירון דקל לבניה ותשתיות מהדורת מאי 2025" },
            new[] { "פרק: 68 בדיקות מעבדה" },
            new[] { "סעיף", "תאור", "יחידה", "כמות", "מחיר" },
            new[] { "בדיקות תקינות מרחב מוגן" },                                        // sub-chapter header, no unit/price
            new[] { "68.052.0010", "בדיקת אטימות וזיהוי פריטי מסגרות לפי ת\"י 4577", "יח'", "1", "640.00" },
            new[] { "68.052.0020", "בדיקת אטימות וזיהוי פריטי מסגרות לפי ת\"י 4577 עד 10 ממ\"דים", "יח'", "1", "500.00" },
            new[] { "68.052.0090", "בדיקת זיהוי פריטי מסגרות, אטימות ממ\"ד", "קומפ'", "1", "1,200.00" },
            new[] { "בדיקות דלתות אש" },                                                 // another header
            new[] { "68.052.0210", "בדיקת התקנה של דלת אש, עד 10 דלתות", "קומפ'", "1", "1,397.00" },
            new[] { "68.052.0231", "תוספת לבדיקת התקנה של דלת אש", "יח'", "1", "60.00" });

        /// <summary>A hand-made contractor sheet: different header words and column order.</summary>
        private static string ContractorBook() => WriteBook(
            new[] { "מחירון קבלן - עבודות פיתוח 2026" },
            new[] { "תיאור", "קוד", "מחיר יחידה", "יח'" },
            new[] { "אבן שפה טרומית", "51.01.0250", "95", "מ\"א" },
            new[] { "ריצוף אבן משתלבת", "51.02.0120", "180", "מ\"ר" },
            new[] { "חפירה כללית", "01.01.0010", "-", "מ\"ק" });               // missing price

        // --------------------------------------------------------------- tests

        [Fact]
        public void DekelLayout_LoadsAllItems_SkipsHeaders()
        {
            var snap = PriceBookXlsxLoader.Load(DekelBook(), "dekel-test");

            snap.Items.Should().HaveCount(5, "two header rows and the title rows are not items");
            snap.Items.Should().ContainKey("68.052.0010");
            snap.Items["68.052.0010"].UnitRaw.Should().Be("יח'");
            snap.Prices["68.052.0010"].Price.Should().Be(640.00m);
            snap.Prices["68.052.0090"].Price.Should().Be(1200.00m, "thousands separators are not decimal points");
            snap.Items["68.052.0090"].Unit.Dimension.Should().NotBe(UnitDimension.Unknown, "קומפ' is a recognised unit");
        }

        [Fact]
        public void DekelLayout_ChapterAndSubChapter_ComeFromTheCodeWithoutAPrefix()
        {
            var snap = PriceBookXlsxLoader.Load(DekelBook(), "dekel-test");

            snap.Items["68.052.0010"].Chapter.Should().Be("68");
            snap.Items["68.052.0010"].SubChapter.Should().Be("68.052");
        }

        [Fact]
        public void ContractorLayout_ColumnsAreFoundByHeaderName_NotPosition()
        {
            var snap = PriceBookXlsxLoader.Load(ContractorBook(), "contractor-test");

            snap.Items.Should().HaveCount(3);
            snap.Items["51.01.0250"].Description.Should().Be("אבן שפה טרומית");
            snap.Items["51.01.0250"].UnitRaw.Should().Be("מ\"א");
            snap.Prices["51.01.0250"].Price.Should().Be(95m);
            snap.Prices["01.01.0010"].IsMissing.Should().BeTrue("'-' is MISSING_PRICE, never zero");
        }

        [Fact]
        public void NtiLayout_StillLoadsTheRealCatalog()
        {
            // The shipped NTI 08/2025 book must keep loading exactly as before.
            var snap = EstimateFixtures.Snapshot();
            snap.Items.Count.Should().Be(8615);
            snap.Items.Should().ContainKey("U51.01.0250");
            snap.Items["U51.01.0250"].Chapter.Should().Be("51");
        }

        [Fact]
        public void Detect_ReportsWhatItFound_SoTheUiCanPreviewBeforeRegistering()
        {
            var info = PriceBookXlsxLoader.Inspect(DekelBook());

            info.ItemCount.Should().Be(5);
            info.Chapters.Should().Contain("68");
            info.HeaderRow.Should().BeGreaterThan(0);
            info.CodeColumn.Should().NotBeNull();
            info.PriceColumn.Should().NotBeNull();
            info.Publisher.Should().Be("דקל", "the title row names the publisher");
        }

        [Fact]
        public void AWorkbookWithNoRecognisableHeader_IsRejectedWithAClearReason()
        {
            var path = WriteBook(new[] { "a", "b", "c" }, new[] { "1", "2", "3" });

            var act = () => PriceBookXlsxLoader.Load(path, "junk");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*שורת כותרת*", "the engineer must learn WHY the file was not a price book");
        }

        [Fact]
        public void WorkbookWithoutUnitColumn_IsRejected_NotLoadedWithUnknownUnits()
        {
            var path = WriteBook(
                new[] { "סעיף", "תיאור", "מחיר" },
                new[] { "51.01.0250", "אבן שפה", "95" });

            var inspection = PriceBookXlsxLoader.Inspect(path);
            inspection.IsUsable.Should().BeFalse();
            inspection.Problems.Should().Contain(p => p.Contains("לא נמצאה עמודת יחידה"));
            var act = () => PriceBookXlsxLoader.Load(path, "missing-unit");
            act.Should().Throw<InvalidOperationException>().WithMessage("*לא נמצאה עמודת יחידה*");
        }

        [Fact]
        public void ConflictingDuplicateCode_IsRejectedInsteadOfLastRowWinning()
        {
            var path = WriteBook(
                new[] { "סעיף", "תיאור", "יחידה", "מחיר" },
                new[] { "51.01.0250", "אבן שפה", "מטר", "95" },
                new[] { "51.01.0250", "אבן שפה אחרת", "מטר", "125" });

            var inspection = PriceBookXlsxLoader.Inspect(path);
            inspection.IsUsable.Should().BeFalse();
            inspection.Problems.Should().ContainSingle(p =>
                p.Contains("קוד הסעיף '51.01.0250' מופיע בשורות 2 ו-3 עם תיאור, יחידה או מחיר שונים."));
            var act = () => PriceBookXlsxLoader.Load(path, "duplicate-conflict");
            act.Should().Throw<InvalidOperationException>().WithMessage("לא ניתן לטעון את הקובץ כמחירון תקין: *קוד הסעיף '51.01.0250' מופיע בשורות 2 ו-3*");
        }

        [Fact]
        public void ByteEquivalentDuplicateRows_AreDeduplicatedDeterministically()
        {
            var path = WriteBook(
                new[] { "סעיף", "תיאור", "יחידה", "מחיר" },
                new[] { "51.01.0250", "אבן שפה", "מטר", "95" },
                new[] { "51.01.0250", "אבן שפה", "מטר", "95" });

            var snapshot = PriceBookXlsxLoader.Load(path, "duplicate-identical");
            snapshot.Items.Should().ContainSingle();
            snapshot.Prices["51.01.0250"].Price.Should().Be(95m);
        }

        [Theory]
        [InlineData("94,72", 94.72)]
        [InlineData("1,200.00", 1200.00)]
        [InlineData("1.234,56", 1234.56)]
        public void TextMoney_UsesDeterministicIsraeliOrInternationalSeparators(
            string text, double expected)
        {
            var parsed = PriceBookXlsxLoader.ParsePrice(text);

            parsed.Error.Should().BeNull();
            parsed.Price.Should().Be((decimal)expected);
        }

        [Theory]
        [InlineData("1,200")]
        [InlineData("1.200")]
        [InlineData("0")]
        [InlineData("-1")]
        public void AmbiguousOrNonPositiveMoney_IsRejected_NotCoerced(string text)
        {
            var parsed = PriceBookXlsxLoader.ParsePrice(text);

            parsed.Price.Should().BeNull();
            parsed.Error.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public void RejectedPriceCell_LoadsAsMissingPrice_AndCannotBecomeMoney()
        {
            var path = WriteBook(
                new[] { "סעיף", "תיאור", "יחידה", "מחיר" },
                new[] { "51.01.0250", "אבן שפה", "מטר", "1,200" });

            var snapshot = PriceBookXlsxLoader.Load(path, "ambiguous-price");

            snapshot.Prices["51.01.0250"].Price.Should().BeNull();
            PriceBookXlsxLoader.Inspect(path).MissingPriceCount.Should().Be(1);
        }

        [Fact]
        public void Loader_HashesAndParsesOneImmutableWorkbookByteSnapshot()
        {
            var path = WriteBook(
                new[] { "סעיף", "תיאור", "יחידה", "מחיר" },
                new[] { "51.01.0250", "אבן שפה", "מטר", "42.50" });
            var bytes = File.ReadAllBytes(path);
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            MiniXlsx.ReadFirstSheet(bytes).Should().NotBeEmpty();
            PriceBookXlsxLoader.Load(path, "immutable-bytes").FileHash.Should().Be(expectedHash);

            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.CivilDelivery.Core", "Estimate",
                "PriceBookXlsxLoader.cs"));
            source.Should().Contain("var workbookBytes = File.ReadAllBytes(xlsxPath)")
                .And.Contain("SHA256.HashData(workbookBytes)")
                .And.Contain("MiniXlsx.ReadFirstSheet(workbookBytes)")
                .And.NotContain("ArtifactHash.Sha256OfFile(xlsxPath)");
        }
    }
}
