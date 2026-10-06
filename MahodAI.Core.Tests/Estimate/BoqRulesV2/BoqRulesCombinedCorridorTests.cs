using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// One bill for the 4 plan files and the corridors: the golden 6422 plan-file result plus the r9 corridor measurement of
/// 6422-CIVIL-WEST (native run 30.09.2026 18:09, read back from corridor_measures_r9_west.json). Chapters 51.01–51.04 are
/// priced inside the total with the quantities of the corridor workbook, the chapters they cover leave the 'לא נכלל' list,
/// the sources name the run, and every formula of the workbook evaluates.
/// </summary>
public sealed class BoqRulesCombinedCorridorTests
{
    private static readonly BoqRulesWorkbookWriter.Context At = new(new DateTime(2026, 9, 30));

    private static readonly Lazy<BoqEngineResult> Plan = new(() =>
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = BoqGoldenInputReader.Read(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("golden_inputs_6422.json")), rules,
            BoqRulesV2GoldenAcceptanceTests.UnmeasuredLayers);
        return BoqRulesEngine.Run(rules, input);
    });

    private static string FixtureJson() => File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("corridor_measures_r9_west.json"));

    /// <summary>The fixture read under the rules identity it was written with (the writer is under test, not the rules SHA).</summary>
    private static CorridorBoqMeasuresFile.Loaded LoadFixture()
    {
        var json = FixtureJson();
        var sha = JsonNode.Parse(json)!["Header"]!["RulesetSha256"]!.GetValue<string>();
        return CorridorBoqMeasuresFile.Read(json, CorridorBoqRuleset.LoadEmbedded6422(), sha);
    }

    private static BoqRulesWorkbookWriter.CorridorInput Corridor()
    {
        var loaded = LoadFixture();
        return new BoqRulesWorkbookWriter.CorridorInput(loaded.Export, loaded.Context, "r9-native-west-20260930-1809", "fixture", loaded.Header.RulesetSha256);
    }

    private static string? Text(MiniXlsx.Worksheet ws, int row, string col) =>
        ws.Rows.FirstOrDefault(r => r.Index == row)?.Cells.Where(c => c.Reference == col + row).Select(c => c.Value).FirstOrDefault();

    private static (int Row, string Formula) TotalRow(MiniXlsx.Workbook wb) =>
        wb.Rows.SelectMany(r => r.Cells.Where(c => c.Kind == MiniXlsx.CellKind.Formula && c.Reference == "F" + r.Index &&
            c.Value.StartsWith("SUM(", StringComparison.Ordinal)).Select(c => (r.Index, c.Value))).Single();

    // The r9 measurement (2000-DES complete, 3000 partial and outside the total) with the joint decisions of 30.09:
    // WEST-07 no reuse credit; WEST-08 earthworks after stripping by the local slope-equivalent method (DES cut 477.018…,
    // fill 1607.373… — Codex's independent Decimal integration gave 477.018225658710 / 1607.373577834694).
    private static readonly IReadOnlyDictionary<string, double> CorridorQuantities = new Dictionary<string, double>
    {
        ["51.01.1061"] = 11942.778060237244, ["51.02.0010"] = 0, ["51.02.0020"] = 477.01822565871066, ["51.02.0080"] = 1607.3735778346809,
        ["51.03.0010"] = 333.3630807388623, ["51.03.0030"] = 666.4041605476141, ["51.04.0350"] = 8608.987966211153,
        ["51.04.0130"] = 0, ["51.04.0160"] = 0, ["51.04.1480"] = 8608.987966211153, ["51.04.1470"] = 0,
    };

    [Fact]
    public void CombinedBillPricesTheCorridorChaptersInsideTheTotal()
    {
        var plain = BoqRulesWorkbookWriter.Build(Plan.Value, At, out var plainMapped);
        var combined = BoqRulesWorkbookWriter.Build(Plan.Value, At, out var combinedMapped, Corridor());
        combinedMapped.Should().Be(plainMapped + CorridorQuantities.Count);

        var plainEval = MiniXlsx.EvaluateFormulas(plain);
        var eval = MiniXlsx.EvaluateFormulas(combined);
        eval.Failure.Should().BeNull();
        eval.Unevaluable.Should().BeEmpty("every formula of the combined bill, corridor sheets included, evaluates");

        var sheet = BoqRulesWorkbookWriter.SheetBoq;
        double Value(MiniXlsx.Workbook wb, XlsxFormulaEvaluation ev, string reference) =>
            ev.TryGetValue(sheet, reference, out var v) && v.Kind == XlsxValueKind.Number ? v.Number
            : throw new InvalidOperationException($"{reference} has no numeric value");

        var corridorRows = new List<int>();
        foreach (var (code, quantity) in CorridorQuantities)
        {
            var row = combined.Rows.Single(r => Text(combined, r.Index, "A") == code).Index;
            corridorRows.Add(row);
            Value(combined, eval, "D" + row).Should().BeApproximately(quantity, 1e-6, code);
            if (code == "51.02.0020") Value(combined, eval, "D" + row).Should().BeApproximately(477.018225658710, 1e-9, "the independent Decimal result");
            if (code == "51.02.0080") Value(combined, eval, "D" + row).Should().BeApproximately(1607.373577834694, 1e-9, "the independent Decimal result");
            combined.Rows.Single(r => r.Index == row).Cells.Single(c => c.Reference == "F" + row).Value
                .Should().Be($"IF(E{row}=\"\",\"\",ROUND(D{row}*E{row},2))");
            Text(combined, row, "H").Should().StartWith("מהקורידורים: ");
        }

        // The corridor chapters come first and sit inside the total's range; the total grows by exactly their amounts.
        var (totalRow, totalFormula) = TotalRow(combined);
        totalFormula.Should().StartWith($"SUM(F{corridorRows.Min()}:F", "the SUM starts at the first corridor line");
        int.Parse(totalFormula[(totalFormula.IndexOf(":F", StringComparison.Ordinal) + 2)..^1]).Should().BeGreaterThan(corridorRows.Max());
        var corridorAmount = corridorRows.Sum(r => Value(combined, eval, "F" + r));
        Value(combined, eval, "F" + totalRow).Should().BeApproximately(Value(plain, plainEval, "F" + TotalRow(plain).Row) + corridorAmount, 0.005);

        // r9: 3000 is partial and five corridors were not measured, so 51.01–51.04 each keep a 'לא נכלל' row naming them;
        // 51.01 names demolition (only stripping is measured).
        foreach (var chapter in new[] { "51.01", "51.02", "51.03", "51.04" })
        {
            combined.Rows.Count(r => Text(combined, r.Index, "A") == chapter + ".0000").Should().Be(2, chapter);
            var reason = combined.Rows.Where(r => Text(combined, r.Index, "A") == chapter + ".0000" && Text(combined, r.Index, "G") == "לא נכלל")
                .Select(r => Text(combined, r.Index, "H")).Single();
            reason.Should().Contain("3000 (מדידה חלקית)").And.Contain("2000 (מכובה בשרטוט)").And.Contain("750 (מכובה בשרטוט)");
        }
        combined.Rows.Where(r => Text(combined, r.Index, "A") == "51.01.0000" && Text(combined, r.Index, "G") == "לא נכלל")
            .Select(r => Text(combined, r.Index, "B")).Single().Should().EndWith("פירוקים");

        var names = combined.AdditionalSheets.Select(s => s.SheetName).ToList();
        names.Take(plain.AdditionalSheets.Count).Should().Equal(plain.AdditionalSheets.Select(s => s.SheetName));
        names.Skip(plain.AdditionalSheets.Count).Should().Equal(new[]
        {
            CorridorBoqWorkbook.SheetCalc, CorridorBoqWorkbook.SheetMeasure, CorridorBoqWorkbook.SheetCompare, CorridorBoqWorkbook.SheetCoverage,
        }.Select(n => CorridorBoqWorkbook.CombinedPrefix + n));

        var sources = combined.AdditionalSheets.Single(s => s.SheetName == BoqRulesWorkbookWriter.SheetSources);
        var sourceTexts = sources.Rows.SelectMany(r => r.Cells).Select(c => c.Value).ToList();
        sourceTexts.Should().Contain("r9-native-west-20260930-1809");
        sourceTexts.Should().Contain(t => t.Contains("84606791d4ef8ec8df5fdeba78d0ec2526314f22f219e413709ca2a58da3ab29", StringComparison.Ordinal));
        var explain = combined.AdditionalSheets.Single(s => s.SheetName == BoqRulesWorkbookWriter.SheetExplain);
        explain.Rows.SelectMany(r => r.Cells).Should().Contain(c => c.Value.Contains("3000", StringComparison.Ordinal) &&
            c.Value.Contains("לא נכללה בסה\"כ", StringComparison.Ordinal), "the partial corridor is named with the reason it is outside the total");
    }

    [Fact]
    public void FullyMeasuredChaptersLeaveTheNotIncludedListAndTheLineCountIncludesTheCorridorLines()
    {
        // Only the complete corridor and nothing skipped: 51.02–51.04 are fully covered; 51.01 stays for demolition.
        var loaded = LoadFixture();
        var complete = loaded.Export with
        {
            Measures = loaded.Export.Measures.Where(m => m.EarthworksComplete && m.MaterialsComplete).ToList(),
            Skipped = Array.Empty<(string, string)>(),
        };
        var corridor = new BoqRulesWorkbookWriter.CorridorInput(complete, loaded.Context, "r9-complete-only", "fixture", loaded.Header.RulesetSha256);
        var wb = BoqRulesWorkbookWriter.Build(Plan.Value, At, out _, corridor);
        foreach (var chapter in new[] { "51.02", "51.03", "51.04" })
            wb.Rows.Count(r => Text(wb, r.Index, "A") == chapter + ".0000").Should().Be(1, chapter);
        wb.Rows.Where(r => Text(wb, r.Index, "A") == "51.01.0000" && Text(wb, r.Index, "G") == "לא נכלל")
            .Select(r => Text(wb, r.Index, "H")).Single().Should().Contain("הפירוקים לא נמדדו").And.NotContain("לא בסה\"כ");

        var directory = Path.Combine(Path.GetTempPath(), "boq-combined-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plain = BoqRulesWorkbookWriter.Write(Plan.Value, Path.Combine(directory, "plain.xlsx"), At);
            var combined = BoqRulesWorkbookWriter.Write(Plan.Value, Path.Combine(directory, "combined.xlsx"), At, Corridor());
            combined.BoqLineCount.Should().Be(plain.BoqLineCount + CorridorQuantities.Count);
            combined.MappedLineCount.Should().Be(plain.MappedLineCount + CorridorQuantities.Count);
            combined.MappedLineCount.Should().BeLessThanOrEqualTo(combined.BoqLineCount);
            combined.Sheets.Should().Equal(BoqRulesWorkbookWriter.SheetOrder.Concat(new[]
            {
                CorridorBoqWorkbook.SheetCalc, CorridorBoqWorkbook.SheetMeasure, CorridorBoqWorkbook.SheetCompare, CorridorBoqWorkbook.SheetCoverage,
            }.Select(n => CorridorBoqWorkbook.CombinedPrefix + n)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WithoutCorridorsTheBillHasNoCorridorContent()
    {
        var wb = BoqRulesWorkbookWriter.Build(Plan.Value, At, out _);
        wb.AdditionalSheets.Select(s => s.SheetName).Should().Equal(BoqRulesWorkbookWriter.SheetOrder.Skip(1));
        wb.Rows.Concat(wb.AdditionalSheets.SelectMany(s => s.Rows)).SelectMany(r => r.Cells)
            .Should().NotContain(c => c.Value != null && c.Value.Contains("קורידורים", StringComparison.Ordinal));
        wb.Styles.Should().HaveCount(BoqRulesWorkbookWriter.Build(Plan.Value, At, out _, null).Styles.Count);
    }

    [Fact]
    public void MeasuresFileRoundTripsExactly()
    {
        var loaded = LoadFixture();
        var once = CorridorBoqMeasuresFile.Serialize(loaded.Export, loaded.Context, loaded.Header.RulesetSha256, null);
        var again = CorridorBoqMeasuresFile.Read(once, CorridorBoqRuleset.LoadEmbedded6422(), loaded.Header.RulesetSha256);
        CorridorBoqMeasuresFile.Serialize(again.Export, again.Context, loaded.Header.RulesetSha256, null).Should().Be(once);
        again.Export.Measures.Select(m => m.VolCut).Should().Equal(loaded.Export.Measures.Select(m => m.VolCut));
        again.Export.Measures.Single(m => m.CorridorId.StartsWith("3000", StringComparison.Ordinal)).EarthworksComplete.Should().BeFalse();
    }

    [Fact]
    public void MeasuresFileRefusesOtherRulesAndOtherSchemas()
    {
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var json = FixtureJson();
        var act = () => CorridorBoqMeasuresFile.Read(json, rules, new string('0', 64));
        act.Should().Throw<InvalidDataException>().WithMessage("*other corridor rules*");

        var node = JsonNode.Parse(json)!;
        var sha = node["Header"]!["RulesetSha256"]!.GetValue<string>();
        node["Header"]!["RulesetVersion"] = "0";
        var otherVersion = () => CorridorBoqMeasuresFile.Read(node.ToJsonString(), rules, sha);
        otherVersion.Should().Throw<InvalidDataException>();

        node = JsonNode.Parse(json)!;
        node["Header"]!["Schema"] = "mahod-corridor-measures/0";
        var otherSchema = () => CorridorBoqMeasuresFile.Read(node.ToJsonString(), rules, sha);
        otherSchema.Should().Throw<InvalidDataException>().WithMessage("*schema*");
    }

    [Fact]
    public void MeasuresFileRefusesAMissingFieldOrAnotherStrippingDepthOrMethod()
    {
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var json = FixtureJson();
        var sha = JsonNode.Parse(json)!["Header"]!["RulesetSha256"]!.GetValue<string>();
        foreach (var field in new[] { "PostCut", "StripDepthM", "StripMethodId", "EarthworksComplete" })
        {
            var node = JsonNode.Parse(json)!;
            node["Measures"]![0]!.AsObject().Remove(field);
            var missing = () => CorridorBoqMeasuresFile.Read(node.ToJsonString(), rules, sha);
            missing.Should().Throw<InvalidDataException>().WithMessage($"*'{field}'*", "a missing field is never read as zero");
        }
        var depth = JsonNode.Parse(json)!;
        depth["Measures"]![0]!["StripDepthM"] = 0.3;
        var otherDepth = () => CorridorBoqMeasuresFile.Read(depth.ToJsonString(), rules, sha);
        otherDepth.Should().Throw<InvalidDataException>().WithMessage("*depth*");
        var method = JsonNode.Parse(json)!;
        method["Measures"]![0]!["StripMethodId"] = "aggregate-net";
        var otherMethod = () => CorridorBoqMeasuresFile.Read(method.ToJsonString(), rules, sha);
        otherMethod.Should().Throw<InvalidDataException>().WithMessage("*method*");
    }

    [Fact]
    public void MeasuresFileRefusesClaimsItsValuesDoNotBackButKeepsAPartialCorridorReadable()
    {
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var json = FixtureJson();
        var sha = JsonNode.Parse(json)!["Header"]!["RulesetSha256"]!.GetValue<string>();
        int Index(JsonNode root, bool complete) => root["Measures"]!.AsArray().Select((m, i) => (m, i))
            .First(x => x.m!["EarthworksComplete"]!.GetValue<bool>() == complete).i;
        void Refused(Action<JsonNode> edit, string because)
        {
            var node = JsonNode.Parse(json)!;
            edit(node);
            var act = () => CorridorBoqMeasuresFile.Read(node.ToJsonString(), rules, sha);
            act.Should().Throw<InvalidDataException>(because);
        }
        Refused(n => n["Measures"]![Index(n, true)]!["PostCut"] = null, "complete earthworks without the cut after stripping");
        Refused(n => { var m = n["Measures"]![Index(n, true)]!; m["PostCut"] = null; m["PostFill"] = null; m["StripDebit"] = null; m["StripMethodId"] = null; },
            "complete earthworks without any post-stripping value");
        Refused(n => n["Measures"]![Index(n, true)]!["PostCut"] = -1.0, "a negative quantity");
        Refused(n => n["Measures"]![Index(n, true)]!["VolCut"] = -0.5, "a negative quantity");
        Refused(n => n["Measures"]![Index(n, true)]!["CodeVolume"] = null, "a null map is not an empty measurement");
        Refused(n => n["Measures"]![Index(n, true)]!["Issues"] = null, "a null list");
        Refused(n => n["Measures"]![Index(n, false)]!["Complete"] = true, "Complete must equal earthworks && materials");
        Refused(n => n["Skipped"] = null, "a null list");

        // The partial corridor (3000) may lose its post values and still be read — it is shown, never totalled.
        var partial = JsonNode.Parse(json)!;
        var m3000 = partial["Measures"]![Index(partial, false)]!;
        m3000["PostCut"] = null; m3000["PostFill"] = null; m3000["StripDebit"] = null;
        var loaded = CorridorBoqMeasuresFile.Read(partial.ToJsonString(), rules, sha);
        loaded.Export.Measures.Single(m => !m.EarthworksComplete).PostCut.Should().BeNull();
    }

    [Fact]
    public void AnotherDepthInTheWorkbookTurnsTheEarthworksIntoNaInsteadOfAStaleQuantity()
    {
        var wb = BoqRulesWorkbookWriter.Build(Plan.Value, At, out _, Corridor());
        var measure = wb.AdditionalSheets.Single(s => s.SheetName == CorridorBoqWorkbook.CombinedPrefix + CorridorBoqWorkbook.SheetMeasure);
        var c3 = measure.Rows.Single(r => r.Index == 3);
        var cell = c3.Cells.Single(c => c.Reference == "C3");
        c3.Cells[c3.Cells.IndexOf(cell)] = cell with { Value = "0.3" };
        var eval = MiniXlsx.EvaluateFormulas(wb);
        var cutRow = wb.Rows.Single(r => Text(wb, r.Index, "A") == "51.02.0020").Index;
        eval.TryGetValue(BoqRulesWorkbookWriter.SheetBoq, "D" + cutRow, out _).Should().BeFalse("a depth the tool did not compute with must not show a quantity");
        eval.Unevaluable.Should().Contain(u => u.Sheet == BoqRulesWorkbookWriter.SheetBoq && u.Reference == "D" + cutRow);
        var stripRow = wb.Rows.Single(r => Text(wb, r.Index, "A") == "51.01.1061").Index;
        eval.TryGetValue(BoqRulesWorkbookWriter.SheetBoq, "D" + stripRow, out var strip).Should().BeTrue();
        strip.Number.Should().BeApproximately(11942.778060237244, 1e-6, "the stripping AREA does not depend on the depth");
    }

    [Fact]
    public void MeasuresFileRefusesNonFiniteValues()
    {
        var loaded = LoadFixture();
        var broken = loaded.Export with { Measures = loaded.Export.Measures.Select((m, i) => i == 0 ? m with { VolCut = double.NaN } : m).ToList() };
        var act = () => CorridorBoqMeasuresFile.Serialize(broken, loaded.Context, loaded.Header.RulesetSha256, null);
        act.Should().Throw<InvalidDataException>().WithMessage("*not finite*");
    }
}
