using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

public sealed class BoqRulesWorkbookCrossingOrderTests
{
    [Fact]
    public void NearEqualCrossingsKeepSourceOrder_WhileDifferentLengthsAndReferencesStayCorrect()
    {
        var rules = BoqRuleset.Parse("""
        {
          "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.3",
          "parameters": [{"id":"cross_w","label":"width","value":3},
                         {"id":"fill","label":"fill","value":0.5}],
          "chapters": [{"id":"10.32","title":"marking"}],
          "crosswalk_geometry": {"src":["M"],"yellow_layers":["Y"],"white_layers":["W"],
              "hatch_layers":["W"],"standard_width_param":"cross_w","fill_param":"fill"},
          "boq": [{"id":"X","chapter":"10.32","item":"10.32.1942","unit":"m2",
              "parts":[{"label":"crossings","src":["M"],"layers":["Y","W"],"kind":"crosswalk_geo"}]}]
        }
        """);
        var result = BoqRulesEngine.Run(rules, new BoqInputSet());
        // Actual headless SM lengths for 204E64/204E63. The reference gives both
        // 6.24813062074827 in this source order; only the first uses boundary recovery.
        var first = Crossing("204E64", "hatch-assembly", 6.248130550608039, fallback: 1);
        var second = Crossing("204E63", "hatch-assembly", 6.248130624298938);
        var crossings = new[] { first, second,
            Crossing("shorter", "hatch-assembly", 6.248),
            Crossing("longer", "hatch-assembly", 6.249),
            Crossing("hatch", "hatch", null),
            Crossing("standard", "standard", 20) };
        result.Parts.Single().Crossings = crossings;
        var originalLengths = crossings.Select(c => c.Length).ToArray();

        var wb = BoqRulesWorkbookWriter.Build(result,
            new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out _);
        var sheet = wb.AdditionalSheets.Single(s => s.SheetName == BoqRulesWorkbookWriter.SheetCross);
        string Cell(string reference) => sheet.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == reference).Value;

        Enumerable.Range(2, 6).Select(r => Cell("I" + r)).Should()
            .Equal("hatch", "longer", "204E64", "204E63", "shorter", "standard");
        Enumerable.Range(2, 6).Select(r => Cell("A" + r)).Should().Equal("1", "2", "3", "4", "5", "6");
        // The recovery notice must identify the first near-tied crossing, not its neighbour.
        Cell("A10").Should().Contain("במעברים #3 שטח ההצללה");
        var detail = wb.AdditionalSheets.Single(s => s.SheetName == BoqRulesWorkbookWriter.SheetDetail);
        detail.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == "J2").Value
            .Should().Contain("#2–#5");
        Cell("C4").Should().Be("6.248");
        Cell("C5").Should().Be("6.248");
        Cell("G4").Should().Be("ROUND(E4*'פרמטרים'!$C$3+N(F4),3)");
        crossings.Select(c => c.Length).Should().Equal(originalLengths,
            "rounding belongs only to the sort key, never to measured inputs");
    }

    private static BoqCrossing Crossing(string handle, string source, double? length, int fallback = 0) => new()
    {
        Kind = source, WidthSource = source, Length = length, Width = 3,
        Handles = new[] { handle }, N = 1, HatchM2 = 2, Fallback = fallback,
    };
}
