using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public class SimpleBoqExportTests
{
    [Fact]
    public void SimpleView_PreservesPricesAndPartialTotalsWithoutTurningMissingPriceIntoZero()
    {
        var detailed = new MiniXlsx.Workbook { SheetName = "סיכום" };
        for (int i = 0; i < 20; i++) detailed.Styles.Add(new MiniXlsx.Style());
        var boq = new MiniXlsx.Worksheet { SheetName = EngineerBoqDraftExcelWriter.BoqSheet };
        boq.Rows.Add(new MiniXlsx.OutRow(5).Text("C", "עבודות סימון", 6));
        boq.Rows.Add(new MiniXlsx.OutRow(6).Text("B", "51.32.1725", 18).Text("C", "סימון לבן", 3)
            .Text("D", "מטר", 3).Number("E", 10d, 4).Number("F", 2d, 4).Formula("G", "E6*F6", 4));
        boq.Rows.Add(new MiniXlsx.OutRow(7).Text("B", "51.32.1795", 18).Text("C", "ללא מחיר", 3)
            .Text("D", "מטר", 3).Number("E", 5d, 4).Text("F", "—", 10).Number("G", 0d, 4));
        boq.Rows.Add(new MiniXlsx.OutRow(8).Text("C", "סה״כ", 6).Formula("G", "SUM(G6:G7)", 7));
        detailed.AdditionalSheets.Add(boq);
        var simple = EngineerBoqDraftExcelWriter.SimpleBoq(detailed);
        simple.ColumnWidths.Should().HaveCount(8);
        simple.FreezeTopRows.Should().Be(5);
        simple.Styles.Should().HaveCount(detailed.Styles.Count);
        simple.AdditionalSheets.Should().Contain(boq);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            MiniXlsx.Write(simple, path);
            var rows = MiniXlsx.ReadFirstSheet(path);
            var priced = rows.Single(row => row.Any(cell => cell.Column == "B" && cell.Text == "51.32.1725"));
            priced.Single(c => c.Column == "A").Text.Should().Be("1");
            priced.Single(c => c.Column == "E").Text.Should().Be("10");
            priced.Single(c => c.Column == "G").Text.Should().Be("20");
            var missing = rows.Single(row => row.Any(cell => cell.Column == "B" && cell.Text == "51.32.1795"));
            missing.Single(c => c.Column == "F").Text.Should().BeEmpty();
            missing.Single(c => c.Column == "G").Text.Should().BeEmpty();
            var total = rows.Single(row => row.Any(cell => cell.Text == "סה״כ חלקי לשורות המתומחרות, ₪"));
            total.Single(c => c.Column == "G").Text.Should().Be("20");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
