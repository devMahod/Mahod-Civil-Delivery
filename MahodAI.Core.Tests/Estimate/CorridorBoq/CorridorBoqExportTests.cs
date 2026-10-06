using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.CorridorBoq;

/// <summary>
/// The corridor bill end to end on a synthetic corridor with hand-computed answers: flat ground at 0 from offset −10 to +10,
/// a design bottom at −1 from −5 to 0 and at +1 from 0 to +5 (5 m cut, 5 m fill per section), three stations 0/10/20 in one
/// region; asphalt ASF-5 (width 6, area 0.3) and ASF-6 (width 4, area 0.24) and MAZA (area 2) at every station.
/// </summary>
public sealed class CorridorBoqExportTests
{
    private static readonly CorridorBotSurfaceLogic.RegionKey Key = new("3000 [ABC]", "baseline-001", "region-001");

    private static (List<CorridorBotSurfaceLogic.Schedule>, List<CorridorBotSurfaceLogic.StationInput>, List<CorridorShapeStation>) Corridor(
        bool failMiddle = false)
    {
        var stations = new[] { 0.0, 10.0, 20.0 };
        var schedules = new List<CorridorBotSurfaceLogic.Schedule> { new(Key, 0, 20, stations) };
        var inputs = new List<CorridorBotSurfaceLogic.StationInput>();
        var shapes = new List<CorridorShapeStation>();
        foreach (var st in stations)
        {
            var ok = !(failMiddle && st == 10);
            inputs.Add(new(Key, st, -5, 5,
                new List<CorridorBotSurfaceLogic.Segment>
                {
                    new(new(-5, -1), new(0, -1)), new(new(0, 1), new(5, 1)),
                },
                new List<CorridorBotSurfaceLogic.Part> { new(new List<CorridorBotSurfaceLogic.Point> { new(-10, 0), new(10, 0) }) },
                ok, $"raw#{st}", ok ? null : "synthetic failure"));
            shapes.Add(new CorridorShapeStation(Key.CorridorId, Key.BaselineId, Key.RegionId, st, true, null, new List<CorridorShapeSample>
            {
                new("ASF-5-19-70", 0.3, Rect(-3, 3, -0.05, 0)),
                new("ASF-6-25-70", 0.24, Rect(-2, 2, -0.11, -0.05)),
                new("MAZA", 2.0, Rect(-5, 5, -0.31, -0.11)),
            }));
        }
        return (schedules, inputs, shapes);
    }

    /// <summary>A closed rectangular shape as Civil returns it: four straight boundary links.</summary>
    private static List<CorridorLink> Rect(double x0, double x1, double z0, double z1) => new()
    {
        new(x0, z1, x1, z1), new(x1, z1, x1, z0), new(x1, z0, x0, z0), new(x0, z0, x0, z1),
    };

    [Fact]
    public void ACompleteCorridorGivesTheHandComputedQuantities()
    {
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor();
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        ew.Complete.Should().BeTrue(string.Join(" | ", ew.Issues.Select(i => i.Code + " " + i.Detail)));
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>());
        var m = export.Measures.Single();
        m.Complete.Should().BeTrue(string.Join(" | ", m.Issues));
        m.InterfaceStatus.Should().Be("consistent");
        m.AsphaltPlanArea.Should().BeApproximately(6 * 20, 1e-9);   // union of ASF-5 (6 m) and ASF-6 (4 m, inside it)
        m.LengthM.Should().BeApproximately(20, 1e-9);
        m.VolCut.Should().BeApproximately(5 * 1 * 20, 1e-9);     // 5 m² cut per section × 20 m
        m.VolFill.Should().BeApproximately(5 * 1 * 20, 1e-9);
        m.PlanCut.Should().BeApproximately(5 * 20, 1e-9);        // 5 m wide × 20 m
        m.PlanFill.Should().BeApproximately(5 * 20, 1e-9);
        m.Plan3DCut.Should().BeApproximately(100, 1e-9);         // flat ground: 3D factor 1
        m.CodeVolume["ASF-5-19-70"].Should().BeApproximately(0.3 * 20, 1e-9);
        m.CodePlanArea["ASF-5-19-70"].Should().BeApproximately(6 * 20, 1e-9);
        m.CodePlanArea["ASF-6-25-70"].Should().BeApproximately(4 * 20, 1e-9);
        m.CodeVolume["MAZA"].Should().BeApproximately(40, 1e-9);

        var path = Path.Combine(Path.GetTempPath(), $"corridor-boq-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            CorridorBoqExport.WriteWorkbook(export, path, new CorridorBoqExport.Context(new DateTime(2026, 9, 30), "x.dwg", "0", "raw.json", "0"));
            var values = MiniXlsx.EvaluateFormulas(CorridorBoqWorkbook.Create(export,
                new CorridorBoqExport.Context(new DateTime(2026, 9, 30), "x.dwg", "0", "raw.json", "0")));
            // strip 200 m²; hisuf cut = 100 × 0.2 = 20 → net cut = 100 − 20 = 80; fill = 100 + 20 = 120;
            // decision WEST-07 (no reuse credit): reuse 0, remove all 80, import all 120; MAZA 40; ASF-5 120, ASF-6 80;
            // prime 120, tack 80.
            var expected = new Dictionary<string, double>
            {
                ["51.01.1061"] = 200, ["51.02.0010"] = 0, ["51.02.0020"] = 80, ["51.02.0080"] = 120, ["51.03.0010"] = 40,
                ["51.04.0350"] = 120, ["51.04.0130"] = 80, ["51.04.1480"] = 120, ["51.04.1470"] = 80,
            };
            var rows = MiniXlsx.ReadFirstSheet(path);
            foreach (var (item, qty) in expected)
            {
                var row = rows.Single(r => r.Any(c => c.Column == "A" && c.Text == item));
                double.Parse(row.Single(c => c.Column == "D").Text, System.Globalization.CultureInfo.InvariantCulture)
                    .Should().BeApproximately(qty, 1e-6, item);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AFailedStationIsNeverBridgedAndTheCorridorIsNotTotalled()
    {
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor(failMiddle: true);
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        ew.Complete.Should().BeFalse();
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>());
        var m = export.Measures.Single();
        m.Complete.Should().BeFalse();
        m.LengthM.Should().Be(0);   // no interval with two good stations
        export.Complete.Should().BeFalse();
    }

    [Fact]
    public void AnEarthworksFailureDoesNotZeroFullyMeasuredMaterials()
    {
        // Codex 16:40 (a): an MK/Bot failure at the middle station leaves the earthworks partial (not totalled) while the
        // asphalt and base courses, read at every station, are still totalled.
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor(failMiddle: true);
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>());
        var m = export.Measures.Single();
        m.EarthworksComplete.Should().BeFalse();
        m.MaterialsComplete.Should().BeTrue(string.Join(" | ", m.Issues));
        var path = Path.Combine(Path.GetTempPath(), $"corridor-boq-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            CorridorBoqExport.WriteWorkbook(export, path, new CorridorBoqExport.Context(new DateTime(2026, 9, 30), "x.dwg", "0", "raw.json", "0"));
            var rows = MiniXlsx.ReadFirstSheet(path);
            double Qty(string item) => double.Parse(rows.Single(r => r.Any(c => c.Column == "A" && c.Text == item))
                .Single(c => c.Column == "D").Text, System.Globalization.CultureInfo.InvariantCulture);
            Qty("51.04.0350").Should().BeApproximately(120, 1e-6);   // asphalt still counted
            Qty("51.03.0010").Should().BeApproximately(40, 1e-6);    // MAZA still counted
            // earthworks measured in no corridor: 'לא נמדד', no number and no price (Codex r7), never a formula 0
            string Cell(string item, string col) => rows.Single(r => r.Any(c => c.Column == "A" && c.Text == item)).Single(c => c.Column == col).Text;
            Cell("51.01.1061", "D").Should().Be("לא נמדד");
            Cell("51.02.0010", "D").Should().Be("לא נמדד");
            Cell("51.02.0010", "F").Should().Be("—");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ARegionWithAnUnreadableStationMakesItsCorridorIncomplete()
    {
        // Codex 16:33: the collector drops the region's schedule and reports it; the corridor must not read as complete.
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor();
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>(),
            new List<(string, string)> { (Key.CorridorId, "region-station-unreadable: baseline-001/region-002 — 1 of 7") });
        export.Measures.Single().Complete.Should().BeFalse();
        export.Complete.Should().BeFalse();
    }

    private static (string? Status, string? Note) StatusOf(MiniXlsx.Workbook wb, string item)
    {
        var row = wb.Rows.Single(r => r.Cells.Any(c => c.Reference == $"A{r.Index}" && c.Value == item));
        return (row.Cells.FirstOrDefault(c => c.Reference == $"G{row.Index}").Value, row.Cells.FirstOrDefault(c => c.Reference == $"H{row.Index}").Value);
    }

    [Fact]
    public void AMaterialLineWhoseTotalledQuantityIsZeroIsNotReady()
    {
        // Codex 22:57 (87A67B11): 0 in the corridors totalled by this run is "כמות 0 בריצה זו" with the scope note — not "מוכן",
        // and not a claim that the layer is absent from the project. The fixture has no ASF-7 and no S5 in its one complete corridor.
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor();
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>());
        var wb = CorridorBoqWorkbook.Create(export, new CorridorBoqExport.Context(new DateTime(2026, 9, 30), "x.dwg", "0", "raw.json", "0"));

        StatusOf(wb, "51.04.0350").Status.Should().Be(CorridorBoqWorkbook.Ready);        // ASF-5: 120 m²
        StatusOf(wb, "51.04.0130").Status.Should().Be(CorridorBoqWorkbook.Ready);        // ASF-6: 80 m²
        StatusOf(wb, "51.03.0010").Status.Should().Be(CorridorBoqWorkbook.Assumption);   // MAZA: 40 m³
        foreach (var zero in new[] { "51.04.0160", "51.03.0030" })                         // ASF-7, S5: 0 in the totalled corridor
        {
            var (status, note) = StatusOf(wb, zero);
            status.Should().Be(CorridorBoqWorkbook.ZeroInRun, zero);
            // Standalone workbook: the coverage sheet is 'כיסוי' (no prefix) — the note names the sheet that exists.
            note.Should().EndWith(" " + CorridorBoqWorkbook.ZeroInRunNote(""), zero).And.EndWith("בגיליון כיסוי.").And.NotContain("אין אספלט");
            wb.AdditionalSheets.Should().Contain(s => s.SheetName == CorridorBoqWorkbook.SheetCoverage);
        }
        // The quantity stays the calc-sheet formula, which evaluates to 0.
        var values = MiniXlsx.EvaluateFormulas(wb);
        foreach (var zero in new[] { "51.04.0160", "51.03.0030" })
        {
            var row = wb.Rows.Single(r => r.Cells.Any(c => c.Reference == $"A{r.Index}" && c.Value == zero)).Index;
            values.TryGetValue(wb.SheetName, $"D{row}", out var quantity).Should().BeTrue(zero);
            quantity.Number.Should().Be(0, zero);
        }
    }

    [Fact]
    public void WithNoCorridorTotalledNoLineClaimsAZeroForTheRun()
    {
        // Materials unreadable at one station: the corridor is not totalled, so a 0 would be a measurement gap, not a run total.
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor();
        shapes[1] = new CorridorShapeStation(Key.CorridorId, Key.BaselineId, Key.RegionId, 10, false, "synthetic failure", new List<CorridorShapeSample>());
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>());
        export.Measures.Single().MaterialsComplete.Should().BeFalse();
        var wb = CorridorBoqWorkbook.Create(export, new CorridorBoqExport.Context(new DateTime(2026, 9, 30), "x.dwg", "0", "raw.json", "0"));
        wb.Rows.SelectMany(r => r.Cells).Select(c => c.Value).Should().NotContain(CorridorBoqWorkbook.ZeroInRun);
    }

    [Fact]
    public void ACorridorNameIsIsolatedLeftToRightForDisplayOnly()
    {
        // "2000-DES" read "DES-2000" in the RTL sheets (Codex 22:57): display cells carry LRM; matching keeps the bare name.
        var rules = CorridorBoqRuleset.LoadEmbedded6422();
        var (schedules, inputs, shapes) = Corridor();
        var ew = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, 50);
        var export = CorridorBoqExport.Build(rules, shapes, schedules, inputs, ew, new List<(string, string)>(),
            new List<CorridorVolumeTable>(), new List<string>());
        var wb = CorridorBoqWorkbook.Create(export, new CorridorBoqExport.Context(new DateTime(2026, 9, 30), "x.dwg", "0", "raw.json", "0"));
        var calc = wb.AdditionalSheets.Single(s => s.SheetName.EndsWith(CorridorBoqWorkbook.SheetCalc, StringComparison.Ordinal));
        calc.Rows.SelectMany(r => r.Cells).Should().Contain(c => c.Reference == "A2" && c.Value == "\u200E3000\u200E");
    }

    [Fact]
    public void SegmentWidthsSplitACrossingLikeGetArea()
    {
        var ground = new List<(double X, double Z)> { (-10, 0), (10, 0) };
        var (cut, fill) = CorridorBoqExport.SegmentWidths(ground, -5, -1, 5, 1);
        cut.Should().BeApproximately(5, 1e-9);
        fill.Should().BeApproximately(5, 1e-9);
    }
}
