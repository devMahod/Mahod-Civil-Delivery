using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// "The tool = the Excel" at the precision the workbook shows (Codex T review 30.09: C1/C3/C2/M2/M4 were inside the golden
/// tolerance but not equal at two decimals — 0.1 mm chord coordinates). Every BoQ line quantity and every part base, rounded
/// to two decimals, equals the reference's value rounded the same way.
/// </summary>
public sealed class BoqRulesV2StrictParityTests
{
    private readonly ITestOutputHelper _output;

    public BoqRulesV2StrictParityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EveryLineQuantityAndPartBaseEqualsTheReferenceAtTwoDecimals()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = BoqGoldenInputReader.Read(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("golden_inputs_6422.json")), rules,
            BoqRulesV2GoldenAcceptanceTests.UnmeasuredLayers);
        var result = BoqRulesEngine.Run(rules, input);
        using var expected = JsonDocument.Parse(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("golden_expected_6422.json")));
        static double R2(double x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
        var failures = new List<string>();
        foreach (var line in expected.RootElement.GetProperty("lines").EnumerateArray())
        {
            var id = line.GetProperty("id").GetString()!;
            if (line.GetProperty("quantity").ValueKind != JsonValueKind.Number) continue;
            var want = R2(line.GetProperty("quantity").GetDouble());
            var got = R2(result.Lines.Single(l => l.Line.Id == id).Quantity);
            if (want != got) failures.Add($"line {id}: reference {want:F2} / engine {got:F2}");
        }
        foreach (var part in expected.RootElement.GetProperty("parts").EnumerateArray())
        {
            var line = part.GetProperty("line").GetString()!;
            var index = part.GetProperty("part").GetInt32();
            if (!part.TryGetProperty("base", out var b) || b.ValueKind != JsonValueKind.Number) continue;
            var mine = result.Parts.Single(p => p.LineId == line && p.Index == index);
            if (R2(b.GetDouble()) != R2(mine.Base)) failures.Add($"part {line}:{index}: reference {R2(b.GetDouble()):F2} / engine {R2(mine.Base):F2}");
        }
        foreach (var f in failures) _output.WriteLine(f);
        failures.Should().BeEmpty();
    }
}
