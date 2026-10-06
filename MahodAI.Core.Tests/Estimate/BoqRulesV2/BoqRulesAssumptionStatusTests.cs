using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Rules field 'assumption' (line and part): a jointly decided estimating assumption is shown as 'הנחת אומדן' with its
/// note; it never hides an open 'confirm' ('לאישור' wins), missing objects ('· חלקי') or a review ('· לבדיקה').
/// </summary>
public sealed class BoqRulesAssumptionStatusTests
{
    private static string Rules(string lineExtra = "", string partExtra = "") => $$"""
{"schema":"mahod-boq-rules/2","project":"TEST ONLY","version":"assumption-status",
"parameters":[{"id":"road_class","label":"road","value":1}],
"chapters":[{"id":"10.01","title":"synthetic fixture"}],"boq":[
{"id":"P","chapter":"10.01","item":"10.01.1000","unit":"unit"{{lineExtra}},"parts":[{"label":"objects","src":["A"],"block_layers":["N"],"kind":"count"{{partExtra}}}]}]}
""";

    private static (string State, string Notes) Row(string rules, bool missing = false)
    {
        var input = new BoqInputSet();
        for (var i = 0; i < 3; i++) input.Records.Add(new BoqRecord("A", "N", "INSERT", "count", 1, $"H{i}", false, "UNIT", null));
        if (missing) input.Unmeasured.Add(new BoqUnmeasured("A", "N", "FAILED", "unread source object", "inspect source"));
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(rules), input);
        var book = BoqRulesWorkbookWriter.Build(result, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out _);
        var cells = book.Rows.SelectMany(r => r.Cells).ToList();
        var row = cells.Single(c => c.Reference.StartsWith("A", StringComparison.Ordinal) && c.Value == "10.01.1000").Reference[1..];
        string Cell(string col) => cells.Where(c => c.Reference == col + row).Select(c => c.Value).SingleOrDefault() ?? "";
        return (Cell("G"), Cell("H"));
    }

    [Fact]
    public void ALineAssumptionIsShownAsAnEstimateAssumptionWithItsNote()
    {
        Row(Rules()).State.Should().Be("מוכן");
        var (state, notes) = Row(Rules(",\"assumption\":\"proxy item chosen by Claude+Codex; alternative X\""));
        state.Should().Be("הנחת אומדן");
        notes.Should().Contain("הנחת אומדן: proxy item chosen by Claude+Codex; alternative X");
    }

    [Fact]
    public void AnOpenConfirmStillWinsAndMissingObjectsStillShowPartial()
    {
        Row(Rules(",\"assumption\":\"a\",\"confirm\":\"b\"")).State.Should().Be("לאישור");
        Row(Rules(",\"assumption\":\"a\""), missing: true).State.Should().Be("הנחת אומדן · חלקי");
        Row(Rules(",\"assumption\":\"a\",\"review\":\"check\"")).State.Should().Be("הנחת אומדן · לבדיקה");
    }

    [Fact]
    public void APartAssumptionNamesItsPart()
    {
        var (state, notes) = Row(Rules(partExtra: ",\"assumption\":\"factor 6 from the engineer's sheet; alternative 1.26\""));
        state.Should().Be("הנחת אומדן");
        notes.Should().Contain("objects: הנחת אומדן: factor 6 from the engineer's sheet; alternative 1.26");
    }
}
