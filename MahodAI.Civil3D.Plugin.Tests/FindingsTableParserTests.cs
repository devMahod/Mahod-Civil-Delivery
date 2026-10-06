using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Verifies the analysis findings table is parsed into pin-able markers (alignment +
    /// stations + problem text). Pure parsing — no AutoCAD runtime required.
    /// </summary>
    public class FindingsTableParserTests
    {
        private const string FindingsTable =
            "כאן הניתוח:\n\n" +
            "| מיקום | בעיה | ערך בפועל | נדרש | מקור |\n" +
            "| --- | --- | --- | --- | --- |\n" +
            "| ציר Second Street — תחנה 0+035.05 עד 0+175.48 — עקומה 2 | רדיוס 99.1 מ׳ קטן מהמינימום 110 מ׳ | 99.1 מ׳ | 110 m | פרק 5 · עמ׳ 5 |\n" +
            "| ציר Second Street — תחנה 0+772.87 עד 0+859.31 — עקומה 8 | רדיוס 68.6 מ׳ קטן מהמינימום 110 מ׳ | 68.6 מ׳ | 110 m | פרק 5 · עמ׳ 5 |\n";

        [Fact]
        public void Parse_ExtractsAllFindings_WithStationsAndProblem()
        {
            var markers = FindingsTableParser.Parse(FindingsTable);

            markers.Should().HaveCount(2);

            markers[0].Index.Should().Be(1);
            markers[0].Alignment.Should().Be("Second Street");
            markers[0].StationStart.Should().BeApproximately(35.05, 0.001);
            markers[0].StationEnd.Should().BeApproximately(175.48, 0.001);
            markers[0].Problem.Should().Contain("רדיוס 99.1");
            markers[0].ActualValue.Should().Be("99.1 מ׳");
            markers[0].Required.Should().Be("110 m");
            markers[0].Source.Should().Contain("פרק 5");

            markers[1].Index.Should().Be(2);
            markers[1].StationStart.Should().BeApproximately(772.87, 0.001);
        }

        [Fact]
        public void Parse_NoFindingsTable_ReturnsEmpty()
        {
            var markers = FindingsTableParser.Parse("סתם תשובה רגילה בלי טבלה.");
            markers.Should().BeEmpty();
        }

        [Fact]
        public void Parse_TableWithoutLocationColumn_ReturnsEmpty()
        {
            const string table =
                "| פרמטר | ערך |\n" +
                "| --- | --- |\n" +
                "| מהירות תכן | 60 קמ\"ש |\n";

            FindingsTableParser.Parse(table).Should().BeEmpty();
        }

        [Fact]
        public void MidStation_IsAverageOfRange()
        {
            var m = FindingsTableParser.Parse(FindingsTable)[0];
            m.MidStation.Should().BeApproximately((35.05 + 175.48) / 2, 0.001);
        }
    }
}
