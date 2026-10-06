using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The "טען מחירון" sheet/column choice (b15). The selection only builds an explicit mapping bound to the previewed
    /// bytes; every count still comes from the reader. Workbooks here are TEST-ONLY synthetic sheets, not native evidence.
    /// </summary>
    public class PriceBookMappingSelectionTests : IDisposable
    {
        private const string Code = "51.01.0250";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_pbm_" + Guid.NewGuid().ToString("N")[..8]);

        public PriceBookMappingSelectionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Write(string name, string sheet, params string[][] rows)
        {
            var book = new MiniXlsx.Workbook { SheetName = sheet };
            for (var r = 0; r < rows.Length; r++)
            {
                var row = new MiniXlsx.OutRow(r + 1);
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c].Length > 0) row.Text(((char)('A' + c)).ToString(), rows[r][c], 0);
                book.Rows.Add(row);
            }
            var path = Path.Combine(_dir, name);
            MiniXlsx.Write(book, path);
            return path;
        }

        /// <summary>Two price columns: D is the previous price (30), E the current one (95).</summary>
        private string TwoPrices() => Write("two-prices.xlsx", "TEST-ONLY",
            new[] { "קוד", "תיאור", "יחידה", "מחיר קודם", "מחיר" },
            new[] { Code, "TEST-ONLY curb", "מטר", "30", "95" });

        private string OnePrice() => Write("one-price.xlsx", "TEST-ONLY",
            new[] { "קוד", "תיאור", "יחידה", "מחיר" },
            new[] { Code, "TEST-ONLY curb", "מטר", "95" });

        [Theory]
        [InlineData("A", 1)]
        [InlineData("Z", 26)]
        [InlineData("AA", 27)]
        [InlineData("XFD", 16384)]
        [InlineData("", 0)]
        [InlineData("a", 0)]
        [InlineData("ABCD", 0)]
        public void ColumnIndexFollowsSpreadsheetOrder(string column, int index) =>
            PriceBookMappingSelection.ColumnIndex(column).Should().Be(index);

        [Fact]
        public void HeaderOptionsAreTheNonEmptyCellsInSheetOrder()
        {
            var cells = new[]
            {
                new MiniXlsx.CellText("AA", 3, "מחיר"),
                new MiniXlsx.CellText("B", 3, "קוד"),
                new MiniXlsx.CellText("C", 3, "   "),
                new MiniXlsx.CellText("B", 3, "duplicate cell ignored"),
            };
            PriceBookMappingSelection.HeaderOptions(cells).Select(o => (o.Column, o.Header))
                .Should().Equal(("B", "קוד"), ("AA", "מחיר"));
        }

        [Fact]
        public void AnIncompleteOrRepeatedChoiceBuildsNoMappingAndSaysWhyInHebrew()
        {
            var hash = new string('a', 64);
            PriceBookMappingSelection.Build(hash, null, "1", "A", null, "C", "D").Error.Should().Be("יש לבחור גיליון.");
            PriceBookMappingSelection.Build(hash, "S", "0", "A", null, "C", "D").Error.Should().Contain("שורת כותרת");
            PriceBookMappingSelection.Build(hash, "S", "x", "A", null, "C", "D").Error.Should().Contain("שורת כותרת");
            PriceBookMappingSelection.Build(hash, "S", "1", null, null, "C", "D").Error.Should().Be("יש לבחור עמודת סעיף.");
            PriceBookMappingSelection.Build(hash, "S", "1", "A", null, null, "D").Error.Should().Be("יש לבחור עמודת יחידה.");
            PriceBookMappingSelection.Build(hash, "S", "1", "A", null, "C", null).Error.Should().Be("יש לבחור עמודת מחיר.");
            var repeated = PriceBookMappingSelection.Build(hash, "S", "1", "A", "C", "C", "D");
            repeated.Error.Should().Contain("פעמיים");
            repeated.Mapping.Should().BeNull("no column may stand for two roles");
        }

        [Fact]
        public void AnEmptyDescriptionChoiceIsAnExplicitNoDescription()
        {
            var (mapping, error) = PriceBookMappingSelection.Build(new string('b', 64), "S", " 4 ", "A", "", "C", "D");
            error.Should().BeNull();
            mapping.Should().Be(new PriceBookXlsxLoader.ColumnMapping(new string('b', 64), "S", 4, "A", null, "C", "D"));
        }

        [Fact]
        public void TwoPriceColumns_AutoRefuses_AndTheExplicitChoiceReadsExactlyTheChosenColumn()
        {
            var path = TwoPrices();
            var automatic = PriceBookXlsxLoader.Inspect(path);
            automatic.IsUsable.Should().BeFalse();
            automatic.HeaderCandidates.Where(c => c.Role == "price").Select(c => c.Column).Should().Equal("D", "E");

            foreach (var (price, expected) in new[] { ("E", 95m), ("D", 30m) })
            {
                var (mapping, error) = PriceBookMappingSelection.Build(
                    automatic.FileHash, automatic.SheetName, "1", "A", "B", "C", price);
                error.Should().BeNull();
                var chosen = PriceBookXlsxLoader.Inspect(path, mapping!);
                chosen.IsUsable.Should().BeTrue();
                PriceBookMappingSelection.SameAsAutomatic(automatic, mapping!).Should().BeFalse();
                PriceBookXlsxLoader.Load(path, "TEST-ONLY", mapping!).Prices[Code].Price.Should().Be(expected);
            }
        }

        [Fact]
        public void ChoosingExactlyWhatTheAutomaticReadingAppliedStaysAutomatic()
        {
            var path = OnePrice();
            var automatic = PriceBookXlsxLoader.Inspect(path);
            automatic.IsUsable.Should().BeTrue();
            var same = PriceBookMappingSelection.Build(automatic.FileHash.ToUpperInvariant(), automatic.SheetName, "1", "A", "B", "C", "D").Mapping!;
            PriceBookMappingSelection.SameAsAutomatic(automatic, same).Should().BeTrue();
            var withoutDescription = PriceBookMappingSelection.Build(automatic.FileHash, automatic.SheetName, "1", "A", null, "C", "D").Mapping!;
            PriceBookMappingSelection.SameAsAutomatic(automatic, withoutDescription).Should().BeFalse();
        }

        [Fact]
        public void TheSummaryShowsTheReadersOwnCoverageAndNamesRejectedRowsInHebrew()
        {
            var path = Write("coverage.xlsx", "TEST-ONLY",
                new[] { "קוד", "תיאור", "יחידה", "מחיר" },
                new[] { Code, "TEST-ONLY curb", "מטר", "95" },
                new[] { "ABC-123", "TEST-ONLY odd code", "מטר", "10" },
                new[] { "", "", "", "" });
            var inspection = PriceBookXlsxLoader.Inspect(path);
            var summary = PriceBookMappingSelection.Summary(inspection);
            summary.Should().Contain("גיליון: TEST-ONLY");
            summary.Should().Contain($"נקלטו {inspection.Coverage.ImportedRows:N0}");
            summary.Should().Contain($"נדחו {inspection.Coverage.RejectedRows:N0}");
            inspection.Coverage.RejectedRows.Should().Be(1);
            summary.Should().Contain("קוד בפורמט לא נתמך");
            summary.Should().Contain("ABC-123");
        }

        [Fact]
        public void AHeaderChosenBelowItemRowsIsRefusedWithAReason_AndTheRightHeaderReadsEveryItem()
        {
            // Review PBM-1 (Codex 3C2E8CE5): an explicit header row must not silently drop the items above it.
            var path = Write("items-above.xlsx", "TEST-ONLY",
                new[] { "קוד", "תיאור", "יחידה", "מחיר" },
                new[] { Code, "TEST-ONLY curb", "מטר", "95" },
                new[] { "קוד", "תיאור", "יחידה", "מחיר" },
                new[] { "51.01.0260", "TEST-ONLY kerb 2", "מטר", "80" });
            var automatic = PriceBookXlsxLoader.Inspect(path);
            var wrong = PriceBookMappingSelection.Build(automatic.FileHash, "TEST-ONLY", "3", "A", "B", "C", "D").Mapping!;
            var refused = PriceBookXlsxLoader.Inspect(path, wrong);
            refused.IsUsable.Should().BeFalse();
            refused.Problems.Should().Contain(p => p.Contains("מעל שורת הכותרת שנבחרה"));
            var right = PriceBookMappingSelection.Build(automatic.FileHash, "TEST-ONLY", "1", "A", "B", "C", "D").Mapping!;
            var read = PriceBookXlsxLoader.Inspect(path, right);
            read.IsUsable.Should().BeTrue(string.Join(" ", read.Problems));
            read.ItemCount.Should().Be(2, "the repeated header row is a non-item, both items are read");
        }

        [Fact]
        public void AnUnknownReasonCodeIsShownAsItIs() =>
            PriceBookMappingSelection.Reason("some_new_reason").Should().Be("some_new_reason");
    }
}
