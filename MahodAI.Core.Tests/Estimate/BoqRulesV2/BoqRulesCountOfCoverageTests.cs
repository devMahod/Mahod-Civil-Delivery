using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Product regression for Codex's writer/parser fixes of 30.09 (count-of-multipart 1D2DBD54, count-of-coverage D6DF4A93,
/// line-sum-coverage 603D402C, count-source-contract BB5CB73C): a count_of line refers to EVERY part of its source line
/// (59 + 80 = 139, not the first part only); a dependent of an incomplete source is shown 'חלקי' with the source named and
/// its known quantity kept; a complete or unrelated case stays 'מוכן'; count_of may refer only to an earlier count/count_x line.
/// </summary>
public sealed class BoqRulesCountOfCoverageTests
{
    private const string Rules = """
{"schema":"mahod-boq-rules/2","project":"TEST ONLY","version":"count-of-coverage",
"parameters":[{"id":"road_class","label":"road","value":1},{"id":"factor","label":"factor","value":3.3}],
"chapters":[{"id":"10.01","title":"synthetic fixture"}],"boq":[
{"id":"P","chapter":"10.01","item":"10.01.1000","unit":"m","parts":[{"label":"north59","src":["A"],"block_layers":["N"],"kind":"count_x","param":"factor"},{"label":"south80","src":["A"],"block_layers":["S"],"kind":"count_x","param":"factor"}]},
{"id":"U","chapter":"10.01","item":"10.01.1001","unit":"unit","parts":[{"label":"unrelated","src":["A"],"block_layers":["U"],"kind":"count"}]},
{"id":"C","chapter":"10.01","item":"10.01.2000","unit":"unit","parts":[{"label":"dependent","kind":"count_of","line":"P"}]},
{"id":"LS","chapter":"10.01","item":"10.01.2003","unit":"unit","parts":[{"label":"line sum","kind":"line_sum","lines":["C","U"]}]}]}
""";

    private static readonly BoqRulesWorkbookWriter.Context At = new(new DateTime(2026, 9, 30));

    private static BoqInputSet Input(bool measured = true)
    {
        var input = new BoqInputSet();
        if (measured)
            for (var i = 0; i < 139; i++) input.Records.Add(new BoqRecord("A", i < 59 ? "N" : "S", "INSERT", "count", 1, $"H{i}", false, "UNIT", null));
        for (var i = 0; i < 2; i++) input.Records.Add(new BoqRecord("A", "U", "INSERT", "count", 1, $"U{i}", false, "UNIT", null));
        return input;
    }

    private static (BoqEngineResult Result, MiniXlsx.Workbook Book) Run(BoqInputSet input, string rules = Rules)
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(rules), input);
        return (result, BoqRulesWorkbookWriter.Build(result, At, out _));
    }

    private static string Cell(MiniXlsx.Worksheet sheet, string address) =>
        sheet.Rows.SelectMany(r => r.Cells).Where(c => c.Reference == address).Select(c => c.Value).SingleOrDefault() ?? "";

    private static int Row(MiniXlsx.Workbook book, string item) =>
        int.Parse(book.Rows.SelectMany(r => r.Cells).Single(c => c.Reference.StartsWith("A", StringComparison.Ordinal) && c.Value == item).Reference[1..]);

    private static string State(MiniXlsx.Workbook book, string item) => Cell(book, $"G{Row(book, item)}");
    private static string Notes(MiniXlsx.Workbook book, string item) => Cell(book, $"H{Row(book, item)}");
    private static double Quantity(BoqEngineResult result, string id) => result.Lines.Single(l => l.Line.Id == id).Quantity;

    [Fact]
    public void ACountOfLineRefersToEveryPartOfItsSource()
    {
        var (result, book) = Run(Input());
        result.Parts.Where(p => p.LineId == "P").Select(p => p.Base).Should().Equal(59d, 80d);
        Quantity(result, "P").Should().BeApproximately(458.7, 1e-9);
        Quantity(result, "C").Should().Be(139);
        var detail = book.AdditionalSheets.Single(s => s.SheetName == BoqRulesWorkbookWriter.SheetDetail);
        var parts = result.Parts.ToList();
        var dependentRow = parts.FindIndex(p => p.LineId == "C") + 2;   // detail rows follow the parts, header in row 1
        var sourceRows = parts.Select((p, i) => (p, i)).Where(x => x.p.LineId == "P").Select(x => $"F{x.i + 2}");
        Cell(detail, $"F{dependentRow}").Should().Be(string.Join("+", sourceRows), "the dependent formula uses both source rows, not the first part only");
        MiniXlsx.EvaluateFormulas(book).TryGetValue(BoqRulesWorkbookWriter.SheetDetail, $"I{dependentRow}", out var value).Should().BeTrue();
        value.Number.Should().Be(139);
        State(book, "10.01.2000").Should().Be("מוכן");
        State(book, "10.01.2003").Should().Be("מוכן");
        Quantity(result, "LS").Should().Be(141);
    }

    [Fact]
    public void ADependentOfAnIncompleteSourceIsShownPartialWithTheSourceNamed()
    {
        var input = Input();
        input.Unmeasured.Add(new BoqUnmeasured("A", "N", "FAILED", "unread source object", "inspect source"));
        var (result, book) = Run(input);
        Quantity(result, "C").Should().Be(139, "the known quantity is kept");
        result.Missing.Should().ContainSingle().Which.LineId.Should().Be("P", "the missing object is owned by its source line only");
        State(book, "10.01.2000").Should().Be("חלקי");
        Notes(book, "10.01.2000").Should().Contain("10.01.1000").And.Contain("הסך הכולל אינו ידוע");
        Notes(book, "10.01.2000").Should().NotContain("10.01.1001", "an unrelated line is not cited");
        State(book, "10.01.2003").Should().Be("חלקי", "a line_sum over a partial dependency is partial too");
        Quantity(result, "LS").Should().Be(141);
    }

    [Fact]
    public void AnUnrelatedMissingObjectDoesNotContaminateTheDependency()
    {
        var input = Input();
        input.Unmeasured.Add(new BoqUnmeasured("A", "U", "UFAIL", "unread unrelated object", "inspect source"));
        var (_, book) = Run(input);
        State(book, "10.01.2000").Should().Be("מוכן");
        Notes(book, "10.01.2000").Should().NotContain("אינו ידוע");
    }

    [Fact]
    public void TheActual6422PoleCapsStayOnePerPoleFromTheSinglePartPoleLine()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = new BoqInputSet();
        for (var i = 0; i < 3; i++) input.Records.Add(new BoqRecord("SM", "TR-SIGN-POLE-BL", "INSERT", "count", 1, $"P{i}", false, "SIGN", null));
        var result = BoqRulesEngine.Run(rules, input);
        Quantity(result, "S4").Should().Be(3);
        Quantity(result, "S3").Should().BeApproximately(9.9, 1e-9);
    }

    [Fact]
    public void CountOfMayReferOnlyToAnEarlierCountLine()
    {
        var toLineSum = Rules.Replace("""{"label":"dependent","kind":"count_of","line":"P"}""", """{"label":"dependent","kind":"count_of","line":"U"}""")
            .Replace("""{"label":"unrelated","src":["A"],"block_layers":["U"],"kind":"count"}""", """{"label":"unrelated","kind":"line_sum","lines":["P"]}""");
        var act = () => BoqRuleset.Parse(toLineSum);
        act.Should().Throw<InvalidDataException>().WithMessage("*count or count_x*");
        var absent = () => BoqRuleset.Parse(Rules.Replace("\"line\":\"P\"", "\"line\":\"ABSENT\""));
        absent.Should().Throw<InvalidDataException>();
    }
}
