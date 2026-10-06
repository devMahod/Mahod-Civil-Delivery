using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// v4.5 engine data of the notes (slice E) against the frozen reference natali-boq-2809/frozen-v4-3009-e: the note figures
/// (note_figures.json), the hatch overlap / unmeasured-area estimates (ha_overlap.json), and the rows of 'לא נכלל' and
/// 'לא סווג' — order, counts, quantities, plan lengths and wrong-Z figures — as Excel holds them in the reference workbook
/// (v7_workbook_cells_excel_oracle.json).
/// </summary>
public sealed class BoqRulesV2NoteAnalysisTests
{
    private readonly ITestOutputHelper _output;

    public BoqRulesV2NoteAnalysisTests(ITestOutputHelper output) => _output = output;

    private static readonly Lazy<BoqEngineResult> Golden = new(() =>
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = BoqGoldenInputReader.Read(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("golden_inputs_6422.json")), rules,
            BoqRulesV2GoldenAcceptanceTests.UnmeasuredLayers);
        return BoqRulesEngine.Run(rules, input);
    });

    private static JsonElement Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath(name))).RootElement;

    private static Dictionary<string, JsonElement> Sheet(string name) =>
        Fixture("v7_workbook_cells_excel_oracle.json").GetProperty("sheets").GetProperty(name).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    private static string? S(Dictionary<string, JsonElement> sheet, string cell) =>
        sheet.TryGetValue(cell, out var v) && v.TryGetProperty("s", out var s) ? s.GetString() : null;

    private static double? N(Dictionary<string, JsonElement> sheet, string cell) =>
        sheet.TryGetValue(cell, out var v) && v.TryGetProperty("n", out var n) ? n.GetDouble() : null;

    private static double R2(double x) => Math.Round(x, 2, MidpointRounding.ToEven);

    [Fact]
    public void TheRulesDataOfTheWriterIsLoaded_BriefsHeldNoteLayersAndExampleItems()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        rules.Line("M1")!.HeldNoteLayers.Should().Equal("TR-MARK-WHT-812-BIKE");
        rules.Line("M3")!.Parts[3].Brief.Should().StartWith("808 מלבנים קצרים: 0.15 מ\"ר/מ'");
        rules.AllParts.Count(p => !string.IsNullOrEmpty(p.Brief)).Should().BeGreaterThan(4);
        rules.ExampleItems.Should().NotBeNull();
        rules.ExampleItems!.Title.Should().Be("סעיפים מהדוגמה בפרקים שנמדדו — לא נמדדו ולא נבדקו בשרטוט בטיוטה זו");
        rules.ExampleItems.Rows.Select(r => r.Chapter).Should().Equal("51.05", "51.06", "51.31", "51.32", "51.33");
        rules.ExampleItems.Rows.Should().OnlyContain(r => r.Status == "לא נכלל" && r.Action.StartsWith("לאישור אם נדרשים", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNoteFiguresAreTheReferenceFigures()
    {
        var f = Golden.Value.NoteFigures;
        f.Should().NotBeNull("the 6422 lane ruleset carries note_figures");
        var reference = Fixture("note_figures.json");
        f!.LoweredTotalM.Should().Be(reference.GetProperty("lowered_total_m").GetDouble());
        f.LoweredBesideC1M.Should().Be(reference.GetProperty("lowered_beside_c1_m").GetDouble());
        f.LoweredBesideC2M.Should().Be(reference.GetProperty("lowered_beside_c2_m").GetDouble());
        f.LoweredBesideBothM.Should().Be(reference.GetProperty("lowered_beside_both_m").GetDouble());
        f.LoweredBesideAnyM.Should().Be(reference.GetProperty("lowered_beside_any_m").GetDouble());
        f.CurbC1WithC2FaceM.Should().Be(reference.GetProperty("curb_c1_with_c2_face_m").GetDouble(),
            "v4.5: ±5 mm around 0.17–0.26 m makes the figure independent of the 0.1 mm rounding of the chords");
        var frame = reference.GetProperty("frame_815");
        f.Frame815Lines.Should().Be(frame.GetProperty("lines").GetInt32());
        f.Frame815PlanM.Should().Be(frame.GetProperty("plan_m").GetDouble());
        f.Frame815AllLines.Should().Be(frame.GetProperty("all_lines").GetInt32());
        f.Frame815OneObjectM.Should().Be(frame.GetProperty("one_object_m").GetDouble());
        f.Frame815OneObjectAt1852WidthM.Should().Be(frame.GetProperty("one_object_at_1852_width_m").GetDouble());
        var rect = reference.GetProperty("long_rect_808");
        f.LongRect808Lines.Should().Be(rect.GetProperty("lines").GetInt32());
        f.LongRect808PlanM.Should().Be(rect.GetProperty("plan_m").GetDouble());
    }

    [Fact]
    public void TheHatchOverlapAndUnmeasuredAreaEstimatesAreTheReferenceEstimates()
    {
        var h = Golden.Value.HaOverlap;
        h.Should().NotBeNull("the golden carries the boundary polygons and the 6422 lane ruleset ha_overlap");
        var reference = Fixture("ha_overlap.json");
        h!.Hatches.Should().Be(reference.GetProperty("hatches").GetInt32());
        h.Polygons.Should().Be(reference.GetProperty("polygons").GetInt32());
        h.CheckedAgainstCivil.Should().Be(reference.GetProperty("checked_against_civil").GetInt32());
        h.AgreeWithCivil.Should().Be(reference.GetProperty("agree_within_1pct").GetInt32());

        var layers = reference.GetProperty("layers").EnumerateObject().ToList();
        h.Layers.Select(l => l.Layer).Should().Equal(layers.Select(l => l.Name));
        foreach (var (mine, theirs) in h.Layers.Zip(layers))
        {
            mine.Count.Should().Be(theirs.Value.GetProperty("count").GetInt32(), mine.Layer);
            mine.SumM2.Should().BeApproximately(theirs.Value.GetProperty("sum_m2").GetDouble(), 0.011, mine.Layer);
            mine.UnionM2.Should().BeApproximately(theirs.Value.GetProperty("union_m2").GetDouble(), 0.011, mine.Layer);
            mine.OverlapInsideLayerM2.Should().BeApproximately(theirs.Value.GetProperty("overlap_inside_layer_m2").GetDouble(), 0.011, mine.Layer);
        }
        var between = reference.GetProperty("between").EnumerateObject().ToList();
        h.Between.Select(b => $"{b.LayerA} × {b.LayerB}").Should().Equal(between.Select(b => b.Name));
        foreach (var (mine, theirs) in h.Between.Zip(between))
            mine.AreaM2.Should().BeApproximately(theirs.Value.GetDouble(), 0.011, theirs.Name);

        var missing = reference.GetProperty("missing_estimate").EnumerateObject().ToList();
        h.Missing.Select(m => m.Layer).Should().Equal(missing.Select(m => m.Name));
        foreach (var (mine, theirs) in h.Missing.Zip(missing))
        {
            mine.Count.Should().Be(theirs.Value.GetProperty("count").GetInt32(), mine.Layer);
            mine.Built.Should().Be(theirs.Value.GetProperty("built").GetInt32(), mine.Layer);
            mine.AreaM2.Should().BeApproximately(theirs.Value.GetProperty("area_m2").GetDouble(), 0.011, mine.Layer);
            mine.NotBuilt.Should().Equal(theirs.Value.GetProperty("not_built").EnumerateArray().Select(x => x.GetString()));
        }
        // What the workbook prints (whole m²) is the same number either way.
        foreach (var (mine, theirs) in h.Missing.Zip(missing))
            Math.Round(mine.AreaM2).Should().Be(Math.Round(theirs.Value.GetProperty("area_m2").GetDouble()));
    }

    private const string HaLineReason = "קו/פוליליין בשכבת הצללה ב-HA שלא נכלל כמדידת שטח נפרדת. יחסו להצללות ולכמויות שכבר נכללו דורש בירור; אין כאן קביעה שזה גבול הצללה";
    private const string ReferenceHaLineReason = "קו/פוליליין בשכבת הצללה ב-HA — הכמות נלקחת מההצללה; ייתכן שזה גבול ההצללה";

    [Fact]
    public void TheExcludedRowsComeInTheReferenceOrderWithTheReferenceCountsAndQuantities()
    {
        var groups = Golden.Value.ExcludedGroups();
        var sheet = Sheet("לא נכלל");
        var failures = new List<string>();
        for (var i = 0; i < groups.Count; i++)
        {
            var row = i + 2;
            var g = groups[i];
            var want = (S(sheet, $"A{row}"), S(sheet, $"B{row}"), S(sheet, $"C{row}"), N(sheet, $"E{row}"), N(sheet, $"F{row}"));
            // The one reason deliberately reworded (Codex 22:57, 87A67B11): the reference's "maybe the hatch boundary" was not proven,
            // so the rules say only that the relation to the hatches needs clarifying. Order, sources, counts and quantities stay the reference's.
            var reason = g.Reason == HaLineReason ? ReferenceHaLineReason : g.Reason;
            var got = (reason, g.Src, g.Layer, (double?)g.Count, (double?)R2(g.Quantity));
            if (want != got) failures.Add($"row {row}: reference {want} / engine {got}");
        }
        S(sheet, $"A{groups.Count + 2}").Should().BeNull("the reference sheet has exactly as many rows");
        foreach (var f in failures.Take(15)) _output.WriteLine(f);
        failures.Should().BeEmpty();
    }

    [Fact]
    public void TheUnclassifiedRowsComeInTheReferenceOrderWithPlanLengthAndTheWrongZFigures()
    {
        var groups = Golden.Value.UnclassifiedGroups();
        var sheet = Sheet("לא סווג");
        var failures = new List<string>();
        var zRows = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var row = i + 2;
            var g = groups[i];
            var length = g.Kind == "length";
            // The golden chords carry 0.1 mm coordinates: a plan length summed over many chords may round to the next cent
            // (GM oltext: 282.15 here, 282.14 from the full-precision read) — numbers within 0.011, texts and counts exact.
            var want = (S(sheet, $"A{row}"), S(sheet, $"B{row}"), N(sheet, $"D{row}"), N(sheet, $"E{row}"), N(sheet, $"F{row}"));
            var got = (g.Src, g.Layer, (double?)g.Count, (double?)R2(length ? g.Plan!.Value : g.Quantity), length ? (double?)R2(g.Quantity) : null);
            static bool Near(double? a, double? b) => a == null ? b == null : b != null && Math.Abs(a.Value - b.Value) <= 0.011;
            if (want.Item1 != got.Src || want.Item2 != got.Layer || want.Item3 != got.Item3 || !Near(want.Item4, got.Item4) || !Near(want.Item5, got.Item5))
                failures.Add($"row {row}: reference {want} / engine {got}");
            var note = S(sheet, $"H{row}") ?? "";
            if (g.ZCount > 0)
            {
                zRows++;
                var inv = CultureInfo.InvariantCulture;
                var expect = $"{g.ZExcess.ToString("N2", inv)} מ' נובעים מ-";
                if (!note.Contains(expect, StringComparison.Ordinal) || !note.Contains($"למשל {g.ZExampleHandle})", StringComparison.Ordinal))
                    failures.Add($"row {row}: Z {g.ZCount} / {g.ZExcess:N2} / {g.ZExampleHandle} not in the reference note '{note}'");
            }
            else if (note.Contains("גובה Z שגוי", StringComparison.Ordinal))
                failures.Add($"row {row}: the reference has a wrong-Z note, the engine none");
        }
        S(sheet, $"A{groups.Count + 2}").Should().BeNull("the reference sheet has exactly as many rows");
        zRows.Should().BeGreaterThan(0, "DR-BL-TX has 46 records with a wrong Z in the reference");
        foreach (var f in failures.Take(15)) _output.WriteLine(f);
        failures.Should().BeEmpty();
    }
}
