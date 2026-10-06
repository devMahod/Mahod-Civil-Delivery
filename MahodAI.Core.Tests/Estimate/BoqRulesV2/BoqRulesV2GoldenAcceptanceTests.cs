using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Acceptance against the frozen reference (natali-boq-2809: golden_inputs_6422.json → golden_expected_6422.json, produced
/// by the Python reference build_boq_v7.py / export_golden.py, v4 of 30.09.2026). Every BoQ line quantity within
/// max(0.05, 0.05%), the same item numbers, every part's base (and drawn / plan sums and object width) within the same
/// tolerance, the same bucket (item / excluded / unclassified) for every record, the same removed duplicate blocks, the
/// same curb-painting control, the same crossings (hatch assemblies with their hatch area, boundary fallbacks and edges)
/// and the v4 workbook texts.
/// </summary>
public sealed class BoqRulesV2GoldenAcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public BoqRulesV2GoldenAcceptanceTests(ITestOutputHelper output) => _output = output;

    internal static string FixturePath(string name, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Fixtures", "boq_v2", name));

    // The frozen inputs carry the handle of the one zero-length GM line but not its layer; the reference
    // (build_boq_v7.py EXTRA_EXCL) attributes it to TR-ISLAND-CURBSTONE / line C2.
    internal static readonly IReadOnlyDictionary<string, string> UnmeasuredLayers =
        new Dictionary<string, string> { ["10A971"] = "TR-ISLAND-CURBSTONE" };

    private static readonly Lazy<(BoqEngineResult Result, JsonDocument Expected)> Golden = new(() =>
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = BoqGoldenInputReader.Read(File.ReadAllText(FixturePath("golden_inputs_6422.json")), rules, UnmeasuredLayers);
        var result = BoqRulesEngine.Run(rules, input);
        var expected = JsonDocument.Parse(File.ReadAllText(FixturePath("golden_expected_6422.json")));
        return (result, expected);
    });

    private static bool Within(double actual, double expected) =>
        Math.Abs(actual - expected) <= Math.Max(0.05, 0.0005 * Math.Abs(expected));

    /// <summary>The curb-painting line: the line_sum with a control (v4 also has the pipe-video line_sum, without one).</summary>
    private static BoqPartResult Painting(BoqEngineResult result) =>
        result.Parts.Single(p => p.Part.Kind == "line_sum" && p.Line.Control != null);

    [Fact]
    public void EveryBoqLineMatchesTheGoldenQuantityAndItem()
    {
        var (result, expected) = Golden.Value;
        var lines = expected.RootElement.GetProperty("lines").EnumerateArray().ToList();
        result.Lines.Select(l => l.Line.Id).Should().Equal(lines.Select(l => l.GetProperty("id").GetString()));
        var failures = new List<string>();
        foreach (var line in lines)
        {
            var id = line.GetProperty("id").GetString()!;
            var mine = result.Lines.Single(l => l.Line.Id == id);
            var quantity = line.GetProperty("quantity").GetDouble();
            var item = line.GetProperty("item").ValueKind == JsonValueKind.String ? line.GetProperty("item").GetString() : null;
            _output.WriteLine($"{id,-3} {item ?? "—",-11} golden {quantity,14:F4}  engine {mine.Quantity,14:F4}  Δ {mine.Quantity - quantity,10:F4}");
            if (!Within(mine.Quantity, quantity)) failures.Add($"{id}: engine {mine.Quantity:F4} vs golden {quantity:F4}");
            if (!string.Equals(mine.Item, item, StringComparison.Ordinal)) failures.Add($"{id}: item {mine.Item} vs golden {item}");
            if (mine.Line.Unit != line.GetProperty("unit").GetString()) failures.Add($"{id}: unit");
        }
        failures.Should().BeEmpty();
    }

    [Fact]
    public void EveryPartMatchesTheGoldenObjectsBaseMissingAndQuantity()
    {
        var (result, expected) = Golden.Value;
        var parts = expected.RootElement.GetProperty("parts").EnumerateArray().ToList();
        result.Parts.Should().HaveCount(parts.Count);
        var failures = new List<string>();
        foreach (var part in parts)
        {
            var line = part.GetProperty("line").GetString();
            var index = part.GetProperty("part").GetInt32();
            var mine = result.Parts.Single(p => p.LineId == line && p.Index == index);
            var tag = $"{line}.{index} {mine.Part.Label}";
            if (mine.Objects != part.GetProperty("objects").GetInt32()) failures.Add($"{tag}: objects {mine.Objects} vs {part.GetProperty("objects").GetInt32()}");
            if (!Within(mine.Base, part.GetProperty("base").GetDouble())) failures.Add($"{tag}: base {mine.Base} vs {part.GetProperty("base").GetDouble()}");
            if (!Within(mine.Quantity, part.GetProperty("quantity").GetDouble())) failures.Add($"{tag}: quantity {mine.Quantity} vs {part.GetProperty("quantity").GetDouble()}");
            if (mine.Missing != part.GetProperty("missing").GetInt32()) failures.Add($"{tag}: missing {mine.Missing} vs {part.GetProperty("missing").GetInt32()}");
            var goldenLayers = part.GetProperty("layers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            var myLayers = mine.Layers.Items.ToDictionary(p => p.Key, p => p.Value);
            if (!goldenLayers.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(myLayers.OrderBy(p => p.Key, StringComparer.Ordinal)))
                failures.Add($"{tag}: layers {string.Join(",", myLayers)} vs {string.Join(",", goldenLayers)}");
            var goldenSigns = part.GetProperty("blocks_or_signs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            var mySigns = mine.Signs.Items.ToDictionary(p => p.Key, p => p.Value);
            if (!goldenSigns.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(mySigns.OrderBy(p => p.Key, StringComparer.Ordinal)))
                failures.Add($"{tag}: blocks/signs {string.Join(",", mySigns)} vs {string.Join(",", goldenSigns)}");

            // Rules 2.1: every length part is measured one object once, in plan; the drawn sum is kept beside it.
            if (part.TryGetProperty("drawn_sum", out var drawn))
            {
                _output.WriteLine($"{tag,-40} drawn {drawn.GetDouble(),12:F3}/{mine.DrawnSum,12:F3}  plan {part.GetProperty("plan_sum").GetDouble(),12:F3}/{mine.PlanSum,12:F3}  once {part.GetProperty("base").GetDouble(),12:F3}/{mine.Base,12:F3}");
                if (mine.DrawnSum is not { } myDrawn || !Within(myDrawn, drawn.GetDouble())) failures.Add($"{tag}: drawn_sum {mine.DrawnSum} vs {drawn.GetDouble()}");
                var plan = part.GetProperty("plan_sum").GetDouble();
                if (mine.PlanSum is not { } myPlan || !Within(myPlan, plan)) failures.Add($"{tag}: plan_sum {mine.PlanSum} vs {plan}");
                var width = part.GetProperty("object_width_m").GetDouble();
                if (mine.Width is not { } myWidth || Math.Abs(myWidth - width) > 1e-12) failures.Add($"{tag}: object width {mine.Width} vs {width}");
            }
            else if (mine.DrawnSum != null) failures.Add($"{tag}: measured one object once, the reference did not");
            if (part.TryGetProperty("lines", out var sumOf))
            {
                if (!mine.Part.Lines.SequenceEqual(sumOf.EnumerateArray().Select(l => l.GetString()!))) failures.Add($"{tag}: line_sum lines");
                if (mine.Part.Kind != "line_sum") failures.Add($"{tag}: kind {mine.Part.Kind}");
            }
            // v4: a count_of part counts another line (pole caps = poles).
            if (part.TryGetProperty("of", out var of))
            {
                if (mine.Part.Kind != "count_of" || mine.Part.Of != of.GetString()) failures.Add($"{tag}: count_of {mine.Part.Of} vs {of.GetString()}");
            }
        }
        failures.Should().BeEmpty();
    }

    [Fact]
    public void CurbPaintingIsTheSumOfTheRoundedCurbAndIslandCurbLines()
    {
        // Natali, 29.09.2026: 51.32.2640 must equal 51.06.0010 + 51.06.0120 — the ruleset's line_sum, never a drawing measure.
        var (result, expected) = Golden.Value;
        var painting = Painting(result);
        var summed = painting.Part.Lines.Select(id => result.Lines.Single(l => l.Line.Id == id)).ToList();
        summed.Select(l => l.Item).Should().Equal("51.06.0010", "51.06.0120");
        result.Lines.Single(l => ReferenceEquals(l.Line, painting.Line)).Item.Should().Be("51.32.2640");
        var rounded = summed.Sum(l => Math.Round(l.Quantity, 2, MidpointRounding.AwayFromZero));
        painting.Quantity.Should().BeApproximately(rounded, 1e-9);
        painting.Base.Should().BeApproximately(rounded, 1e-9);
        painting.Objects.Should().Be(0, "a line_sum reads no drawing object");
        var golden = expected.RootElement.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("id").GetString() == painting.LineId).GetProperty("quantity").GetDouble();
        Within(painting.Quantity, golden).Should().BeTrue($"{painting.LineId}: {painting.Quantity} vs golden {golden}");
        painting.Notes.Should().Contain(painting.Part.DecidedBy!);
    }

    [Fact]
    public void DuplicateBlocksAtOnePointAreRemovedExactlyAsTheReference()
    {
        var (result, expected) = Golden.Value;
        static string Key(string line, string part, string src, string layer, string block, string handle, string kept) =>
            string.Join("|", line, part, src, layer, block, handle, kept);
        var golden = expected.RootElement.GetProperty("dedup_blocks").EnumerateArray()
            .Select(d => Key(d.GetProperty("line").GetString()!, d.GetProperty("part").GetString()!, d.GetProperty("src").GetString()!,
                d.GetProperty("layer").GetString()!, d.GetProperty("block").GetString()!, d.GetProperty("handle").GetString()!,
                d.GetProperty("kept").GetString()!))
            .ToList();
        var mine = result.Dedup.Select(d => Key(d.LineId, d.PartLabel, d.Src, d.Layer, d.Block, d.Handle, d.Kept)).ToList();
        golden.Should().HaveCount(25, "rules 2.8 reference: 25 duplicate blocks (16 GM copies of racks, 8 M3, 1 S3)");
        foreach (var line in mine.Except(golden).Take(10)) _output.WriteLine("extra   " + line);
        foreach (var line in golden.Except(mine).Take(10)) _output.WriteLine("missing " + line);
        mine.Should().BeEquivalentTo(golden);
        // Every removed block is excluded with the twin it duplicates, named by the item (v4 REF). v4.1: a twin in the opposite
        // direction (back-to-back racks) says so with its distance, measured here from the frozen count points.
        const string flipSuffix = " (בכיוון הפוך — לבדיקה)";
        var flips = 0;
        foreach (var d in result.Dedup)
        {
            var index = result.Input.Records.FindIndex(r => r.Src == d.Src && r.Handle == d.Handle && r.Kind == "count");
            result.Buckets[index].Should().Be(BoqBucket.Excluded);
            if (!d.Kept.EndsWith(flipSuffix, StringComparison.Ordinal))
            {
                result.Reasons[index].Should().Be($"{result.Ref(d.LineId)}: אותו בלוק באותה נקודה כמו {d.Kept} — נספר פעם אחת");
                continue;
            }
            flips++;
            var kept = d.Kept[..^flipSuffix.Length];
            var twin = kept.Split(':', 2);
            var a = result.Input.CountPoints[(d.Src, d.Handle)];
            var b = result.Input.CountPoints[(twin[0], twin[1])];
            var gap = (double.Hypot(a.X - b.X, a.Y - b.Y) * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            result.Reasons[index].Should().Be(
                $"{result.Ref(d.LineId)}: בלוק זהה {gap} ס\"מ מ-{kept} בכיוון הפוך — נספר פעם אחת; לבדיקה אם זוג גב-אל-גב (ראו הערת השורה)");
        }
        flips.Should().Be(0, "rules 2.8: the back-to-back racks have an approved footprint and are counted, not flagged as flips");

        // Rules 2.8 (BOQ-N1): the approved-footprint placements counted again, with their twins and the body gap.
        var distinctGolden = expected.RootElement.GetProperty("distinct_blocks").EnumerateArray()
            .Select(d => (Key: string.Join("|", d.GetProperty("line").GetString(), d.GetProperty("part").GetString(), d.GetProperty("src").GetString(),
                    d.GetProperty("handle").GetString(), string.Join(",", d.GetProperty("twins").EnumerateArray().Select(t => t.GetString()))),
                Gap: d.GetProperty("body_gap_m").GetDouble()))
            .ToList();
        distinctGolden.Should().HaveCount(10);
        var distinctMine = result.Distinct.Select(d => (Key: string.Join("|", d.LineId, d.PartLabel, d.Src, d.Handle, string.Join(",", d.Twins)),
            Gap: d.BodyGapM)).ToList();
        distinctMine.Select(d => d.Key).Should().Equal(distinctGolden.Select(d => d.Key));
        distinctMine.Zip(distinctGolden).Should().OnlyContain(p => Math.Abs(p.First.Gap - p.Second.Gap) < 1e-6);
        distinctMine.Should().OnlyContain(d => d.Gap > 0.89, "the rack bodies are ~0.9 m apart (Codex lower bound 0.898)");
        expected.RootElement.GetProperty("footprint_review").GetArrayLength().Should().Be(0);
        result.FootprintReview.Should().BeEmpty();
        result.Lines.Single(l => l.Line.Id == "N1").Quantity.Should().Be(25);
    }

    [Fact]
    public void TheCurbPaintingControlIsMeasuredReportedAndExcludedNeverAQuantity()
    {
        var (result, expected) = Golden.Value;
        var golden = expected.RootElement.GetProperty("controls");
        result.Controls.Keys.Should().BeEquivalentTo(golden.EnumerateObject().Select(p => p.Name));
        foreach (var entry in golden.EnumerateObject())
        {
            var mine = result.Controls[entry.Name];
            mine.Label.Should().Be(entry.Value.GetProperty("label").GetString());
            mine.Objects.Should().Be(entry.Value.GetProperty("n").GetInt32());
            Within(mine.Length, entry.Value.GetProperty("length").GetDouble()).Should().BeTrue($"control length {mine.Length} vs {entry.Value.GetProperty("length").GetDouble()}");
            Within(mine.Drawn, entry.Value.GetProperty("drawn").GetDouble()).Should().BeTrue($"control drawn {mine.Drawn} vs {entry.Value.GetProperty("drawn").GetDouble()}");
            _output.WriteLine($"{entry.Name}: {mine.Label} n={mine.Objects} length={mine.Length:F3} drawn={mine.Drawn:F3}");

            // Its records are excluded as a control (never an item), and its length is not in any quantity.
            var line = result.Rules.Lines.Single(l => l.Id == entry.Name);
            var reason = BoqRulesEngine.ControlReason(result.Rules, line, result.RoadClass);
            reason.Should().Be("51.32.2640: בקרה בלבד — הכמות היא סכום סעיפי אבני השפה (החלטת נטלי 29.09.2026)");
            result.Reasons.Count(x => x == reason).Should().Be(mine.Objects);
            result.Lines.Single(l => l.Line.Id == entry.Name).Quantity.Should().NotBeApproximately(mine.Length, 1.0);
        }
    }

    [Fact]
    public void EveryRecordLandsInTheGoldenBucket()
    {
        var (result, expected) = Golden.Value;
        var root = expected.RootElement;
        // Same key semantics as the golden dict (src:handle:kind, a later duplicate overwrites an earlier one).
        var mine = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < result.Input.Records.Count; i++)
            mine[result.Input.Records[i].BucketKey] = result.BucketLabel(i);
        var golden = root.GetProperty("record_bucket_by_handle").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        // Set differences (not BeEquivalentTo, which matches 10,000+ elements pairwise).
        golden.Keys.Except(mine.Keys, StringComparer.Ordinal).Take(20).Should().BeEmpty("every golden record key is an engine record");
        mine.Keys.Except(golden.Keys, StringComparer.Ordinal).Take(20).Should().BeEmpty("the engine has no record the golden inputs lack");
        mine.Count.Should().Be(golden.Count);
        var wrong = golden.Where(pair => mine[pair.Key] != pair.Value).Select(pair => $"{pair.Key}: engine {mine[pair.Key]} vs golden {pair.Value}").ToList();
        foreach (var line in wrong.Take(40)) _output.WriteLine(line);
        wrong.Should().BeEmpty();

        var buckets = root.GetProperty("buckets").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        result.BucketCounts().Should().BeEquivalentTo(buckets);
        result.Buckets.Count(b => b == BoqBucket.Excluded).Should().Be(root.GetProperty("excluded_records").GetInt32());
        result.Buckets.Count(b => b == BoqBucket.Unclassified).Should().Be(root.GetProperty("unclassified_records").GetInt32());
    }

    [Fact]
    public void CrossingsMatchTheGoldenCountPerKindAndTheirMeasures()
    {
        var (result, expected) = Golden.Value;
        var golden = expected.RootElement.GetProperty("crosswalks").EnumerateArray().ToList();
        var counts = result.Crossings.GroupBy(c => c.WidthSource).ToDictionary(g => g.Key, g => g.Count());
        golden.GroupBy(c => c.GetProperty("width_source").GetString()!).ToDictionary(g => g.Key, g => g.Count())
            .Should().BeEquivalentTo(counts);
        // v4: the 10 hatches near the stop line are assemblies (dashed line + hatch area), none a hatch-only crossing.
        counts.Should().ContainKey("hatch-assembly").WhoseValue.Should().Be(10);
        counts.Should().NotContainKey("hatch");

        // The same crossings in the same order (the reference output order), with the same handles and measures.
        result.Crossings.Should().HaveCount(golden.Count);
        var failures = new List<string>();
        for (var i = 0; i < golden.Count; i++)
        {
            var g = golden[i];
            var c = result.Crossings[i];
            c.WidthSource.Should().Be(g.GetProperty("width_source").GetString(), $"crossing {i}");
            c.Kind.Should().Be(g.GetProperty("kind").GetString(), $"crossing {i}");
            c.N.Should().Be(g.GetProperty("n").GetInt32(), $"crossing {i}");
            c.Handles.Should().Equal(g.GetProperty("handles").EnumerateArray().Select(h => h.GetString()), $"crossing {i}");
            if (g.GetProperty("length").ValueKind == JsonValueKind.Number) c.Length!.Value.Should().BeApproximately(g.GetProperty("length").GetDouble(), 1e-6);
            if (g.GetProperty("width").ValueKind == JsonValueKind.Number) c.Width!.Value.Should().BeApproximately(g.GetProperty("width").GetDouble(), 1e-6);
            if (g.TryGetProperty("hatch_m2", out var hatch) && hatch.ValueKind == JsonValueKind.Number)
                c.HatchM2!.Value.Should().BeApproximately(hatch.GetDouble(), 1e-6, $"crossing {i} hatch area");
            if (g.TryGetProperty("miss", out var miss)) c.MissingHatches.Should().Be(miss.GetInt32(), $"crossing {i}");
            if (g.TryGetProperty("fallback", out var fallback)) c.Fallback.Should().Be(fallback.GetInt32(), $"crossing {i}");
            if (g.TryGetProperty("edge_handles", out var edges))
                c.EdgeHandles.Should().Equal(edges.EnumerateArray().Select(h => h.GetString()), $"crossing {i}");
            if (g.TryGetProperty("hatch_handles", out var hatches))
                c.HatchHandles.Should().Equal(hatches.EnumerateArray().Select(h => h.GetString()), $"crossing {i}");
            if (g.TryGetProperty("boundary", out var boundary))
            {
                var goldenBoundary = boundary.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
                c.Boundary.Keys.Should().BeEquivalentTo(goldenBoundary.Keys, $"crossing {i}: the hatches with a boundary polyline");
                // The reference picks one of two identical copies by Python set order (not deterministic): report only.
                foreach (var pair in goldenBoundary.Where(pair => c.Boundary.TryGetValue(pair.Key, out var mineValue) && mineValue != pair.Value))
                    failures.Add($"crossing {i}: boundary of {pair.Key} is {c.Boundary[pair.Key]}, reference {pair.Value}");
            }
        }
        foreach (var line in failures) _output.WriteLine(line);
    }

    [Fact]
    public void SignsExclusionsUnclassifiedAndMissingMatchTheReferenceSummary()
    {
        var (result, _) = Golden.Value;
        result.SignCounts.Items.ToDictionary(p => p.Key, p => p.Value).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["505"] = 115, ["303"] = 8, ["213"] = 7, ["227"] = 2, ["229"] = 3, ["301"] = 7, ["228"] = 1, ["214"] = 4,
        });
        result.SignLabels["505"].Should().Be("505/506", "block 505(506) covers both signs");
        result.SignsTotal.Should().Be(147);
        result.PolesCount.Should().Be(140);
        result.GuideHits.Count.Should().Be(0, "no block or layer name of the four files carries 613–640");
        // The v4.5 workbook (frozen-d v7_summary.json): 156 rows in 'לא נכלל' (incl. the zero-length GM line), 87 in 'לא סווג'
        // (incl. 2 unused HA hatch layers and the non-block records of DR-MNHL-BL). The sheet's row order is the writer's.
        // Rules 2.8 (b3, BOQ-N1): 139 — the 10 back-to-back racks are counted, and the GM copies name their same-transform twin.
        result.ExcludedGroups().Should().HaveCount(139);
        result.ExcludedGroups().Select(g => g.Reason).Should().Contain(
            "51.06.0120: קו באורך אפס (נקודת התחלה = נקודת סיום, מזהה 10A971) — אין אורך למדידה; אינו משנה את הכמות");
        result.UnclassifiedGroups().Should().HaveCount(87);
        // v4 missing: only the unresolved HA hatches (the frozen HA layer totals carry counts only: one row per layer).
        // The three crossing hatches are measured by their boundary polylines; the zero-length GM line is information.
        result.Missing.Count(m => m.Handle.Length <= 8).Should().Be(0);
        result.Parts.Sum(p => p.Missing).Should().Be(39);
        result.Warnings.Should().BeEmpty("every SM geometry handle of the frozen inputs has a known layer");
    }

    [Fact]
    public void WorkbookHasTheReferenceSheetsAndItsFormulasReproduceTheEngineQuantities()
    {
        var (result, expected) = Golden.Value;
        var directory = Path.Combine(Path.GetTempPath(), "boq-rules-v2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "boq_6422_v2.xlsx");
            var written = BoqRulesWorkbookWriter.Write(result, path, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)));
            written.Sheets.Should().Equal(BoqRulesWorkbookWriter.SheetOrder);
            // Maintainer hook: keep a copy to regenerate the Excel oracle fixture (recalculated by Excel itself).
            if (Environment.GetEnvironmentVariable("MHD_BOQ_V2_WORKBOOK_COPY") is { Length: > 0 } copyTo)
                File.Copy(path, copyTo, overwrite: false);
            var book = XlsxFormulaReader.Load(path);
            book.SheetNames.Should().Equal(BoqRulesWorkbookWriter.SheetOrder);

            // Evaluate every quantity cell (column D) of the BoQ sheet from the workbook's own formulas.
            var goldenLines = expected.RootElement.GetProperty("lines").EnumerateArray()
                .ToDictionary(l => l.GetProperty("id").GetString()!, l => l.GetProperty("quantity").GetDouble());
            var built = BoqRulesWorkbookWriter.Build(result, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out _);
            var evaluated = 0;
            foreach (var line in result.Lines)
            {
                // Writer bidi E6D00254 (Codex 12:53/13:30): only the sign_block_area range is shown LTR-wrapped in the detail
                // sheet; every other label stays exact.
                static string Displayed(string label) => label == "שלטים 613–640" ? "שלטים ‎613–640‎" : label;
                // Locate a line by its exact detail-cell dependencies, not presentation text shortened for long lines.
                var partRows = line.Parts.Select(p => book.FindRow(BoqRulesWorkbookWriter.SheetDetail, "B", text => text == Displayed(p.Part.Label))).ToList();
                partRows.Should().OnlyContain(r => r != null, $"every part of {line.Line.Id} has a detail row");
                var quantityFormula = string.Join("+", partRows.Select(r => $"'{BoqRulesWorkbookWriter.SheetDetail}'!I{r}"));
                var row = built.Rows.Single(r => r.Cells.Any(c => c.Reference == "D" + r.Index &&
                    c.Kind == MiniXlsx.CellKind.Formula && c.Value == quantityFormula)).Index;
                book.Formula(BoqRulesWorkbookWriter.SheetBoq, "D" + row).Should().Be(quantityFormula);
                var value = book.Evaluate(BoqRulesWorkbookWriter.SheetBoq, "D" + row);
                // Excel rounds each part to 0.01 m (0.001 per crossing, 0.01 per sign row): allow that on top of the tolerance.
                var allowance = Math.Max(0.05, 0.0005 * Math.Abs(goldenLines[line.Line.Id])) + 0.01 * line.Parts.Count +
                                0.0005 * (line.Parts.Sum(p => p.Crossings?.Count ?? 0));
                Math.Abs(value - goldenLines[line.Line.Id]).Should().BeLessThanOrEqualTo(allowance, $"{line.Line.Id} evaluated from the workbook formulas");
                // v4: every line row carries the total formula (a price typed into a "סעיף לבחירה" row flows into the total).
                book.Formula(BoqRulesWorkbookWriter.SheetBoq, "F" + row).Should().Be($"IF(E{row}=\"\",\"\",ROUND(D{row}*E{row},2))");
                evaluated++;
            }
            evaluated.Should().Be(result.Lines.Count);
            // Billing is the sum of rounded parts, not ROUND(raw line quantity, 2): M2 differs by 0.01 legitimately.
            // Check every written formula against Excel's frozen-G oracle, beyond the legacy raw-geometry allowance.
            using var oracle = JsonDocument.Parse(File.ReadAllText(FixturePath("v7_workbook_cells_excel_oracle.json")));
            var formulaValues = MiniXlsx.EvaluateFormulas(built);
            var formulaCount = 0;
            foreach (var sheet in oracle.RootElement.GetProperty("excel_values").EnumerateObject())
            foreach (var cell in sheet.Value.EnumerateObject())
            {
                book.Formula(sheet.Name, cell.Name).Should().NotBeNull($"{sheet.Name}!{cell.Name} remains a formula");
                formulaValues.TryGetValue(sheet.Name, cell.Name, out var actual).Should().BeTrue();
                if (cell.Value.ValueKind == JsonValueKind.Number)
                {
                    // The legacy independent reader uses binary Math.Round: a sum of crossing rows just below
                    // 2552.805 rounds down. Excel ROUND first applies 15-significant-digit decimal semantics.
                    // Keep the independent quantity checks above, but use the Excel-compatible evaluator here.
                    actual.Kind.Should().Be(XlsxValueKind.Number);
                    actual.Number.Should().BeApproximately(cell.Value.GetDouble(), 1e-7,
                        $"{sheet.Name}!{cell.Name} computes the frozen-G Excel value");
                }
                else
                {
                    actual.Kind.Should().Be(XlsxValueKind.Text);
                    actual.Text.Should().Be(cell.Value.GetString(), $"{sheet.Name}!{cell.Name} preserves the formula's text result");
                }
                formulaCount++;
            }
            // Rules 2.8 (b3): D7 has a second part (the southern koltan3 blocks), so one more detail-row formula.
            formulaCount.Should().Be(540);

            // The curb-painting line (line_sum) is the sum of its lines' detail cells, each rounded to 0.01; column A is the item.
            var detailSheet = BoqRulesWorkbookWriter.SheetDetail;
            var painting = Painting(result);
            var paintingRow = book.FindRow(detailSheet, "B", text => text == painting.Part.Label);
            paintingRow.Should().NotBeNull();
            var summedRows = painting.Part.Lines.Select(id => book.FindRow(detailSheet, "A", text => text == result.Ref(id))).ToList();
            summedRows.Should().OnlyContain(x => x != null);
            book.Formula(detailSheet, "F" + paintingRow).Should().Be(string.Join("+", summedRows.Select(x => "I" + x)));
            book.Formula(detailSheet, "I" + paintingRow).Should().Be($"ROUND(F{paintingRow},2)");
            book.Text(detailSheet, "D" + paintingRow).Should().Be("סכום סעיפים " + string.Join(" + ", painting.Part.Lines.Select(result.Ref)));
            book.Evaluate(detailSheet, "I" + paintingRow).Should().BeApproximately(painting.Quantity, 0.011);
            book.Text(detailSheet, "J" + paintingRow).Should().Contain(painting.Part.DecidedBy!).And.Contain(result.Controls[painting.LineId].Label);

            // The one-object-once sheet: one row per length part (drawn sum, plan length, one object once), the removed
            // duplicate blocks, and how the rule works.
            var objectsSheet = BoqRulesWorkbookWriter.SheetObjects;
            var measuredParts = result.Parts.Where(p => p.DrawnSum != null).ToList();
            measuredParts.Should().HaveCount(expected.RootElement.GetProperty("parts").EnumerateArray().Count(p => p.TryGetProperty("drawn_sum", out _)));
            foreach (var part in measuredParts)
            {
                var row = book.FindRow(objectsSheet, "B", text => text == part.Part.Label);
                row.Should().NotBeNull($"{part.LineId} {part.Part.Label} is on the one-object-once sheet");
                book.Evaluate(objectsSheet, "F" + row).Should().BeApproximately(part.DrawnSum!.Value, 0.006);
                book.Evaluate(objectsSheet, "G" + row).Should().BeApproximately(part.PlanSum!.Value, 0.006);
                book.Evaluate(objectsSheet, "H" + row).Should().BeApproximately(part.Base, 0.006);
                book.Evaluate(objectsSheet, "I" + row).Should().Be(part.Width!.Value);
            }
            book.FindRow(objectsSheet, "A", text => text == BoqRulesWorkbookWriter.ObjectsDuplicateBlocksTitle).Should().NotBeNull();
            book.ColumnTexts(objectsSheet, "A").Should().Contain(t => t.StartsWith("הגאומטריה: ", StringComparison.Ordinal));

            // The explanation says how the curb painting is computed (its item = the sum of the two curb items) and that one
            // object counts once.
            var explanation = book.ColumnTexts(BoqRulesWorkbookWriter.SheetExplain, "A");
            var items = painting.Part.Lines.Select(id => result.Lines.Single(l => l.Line.Id == id).Item!)
                .Append(result.Lines.Single(l => l.Line.Id == painting.LineId).Item!).ToList();
            explanation.Should().Contain(t => items.All(item => t.Contains(item, StringComparison.Ordinal)));
            explanation.Should().Contain(BoqRulesWorkbookWriter.ExplainOneObjectOnce);
            _output.WriteLine($"workbook {written.XlsxPath} sha256 {written.XlsxSha256}; {evaluated} quantity formulas evaluated");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The v4 texts of the reference workbook (natali-boq-2809 כתב_כמויות_6422_v7.xlsx of 30.09.2026) that the golden data
    /// decides: the tool prints them identically (only the delivery-specific "מה השתנה" block of הסבר is omitted).
    /// </summary>
    [Fact]
    public void TheWorkbookPrintsTheV4ReferenceTextsForProject6422()
    {
        var (result, _) = Golden.Value;
        var wb = BoqRulesWorkbookWriter.Build(result, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out _);
        MiniXlsx.Worksheet Sheet(string name) => name == BoqRulesWorkbookWriter.SheetBoq ? wb : wb.AdditionalSheets.Single(s => s.SheetName == name);
        IEnumerable<MiniXlsx.OutCell> Cells(string name) => Sheet(name).Rows.SelectMany(r => r.Cells).Where(c => c.Kind != MiniXlsx.CellKind.Blank);
        string? At(string name, string reference) => Cells(name).FirstOrDefault(c => c.Reference == reference).Value;
        IEnumerable<string> Column(string name, string column) =>
            Cells(name).Where(c => new string(c.Reference.TakeWhile(char.IsLetter).ToArray()) == column).Select(c => c.Value);
        int RowOf(string name, string column, string text) =>
            Sheet(name).Rows.Single(r => r.Cells.Any(c => c.Reference.StartsWith(column, StringComparison.Ordinal) &&
                                                         char.IsDigit(c.Reference[column.Length]) && c.Value == text)).Index;

        var boq = BoqRulesWorkbookWriter.SheetBoq;
        At(boq, "A1").Should().Be("כתב כמויות — פרויקט 6422 (נת\"צ מודיעין) — טיוטה v4 לפי כללים, 30.09.2026");
        At(boq, "A2").Should().Be("פרקים, כותרות וסדר הסעיפים כמו בדוגמה של נטלי. מחירים: המחירון האחוד של נתיבי ישראל 07-2026 (מחיר בסיס). " +
                                  "טיוטה — המיפוי והמחירים לאישור; לא אומדן מאושר. פרקים וסעיפים מהדוגמה שלא נמדדו מפורטים בסוף הגיליון.");
        At(boq, "B5").Should().Be("עבודות ניקוז ומניעת סחף");
        At(boq, "B6").Should().Be("צינורות ניקוז מבטון מזוין דרג 5 אטומים למים בעלי תו תקן ת''י 27 בקוטר 40 ס''מ בעומק עד 2.0 מ'.");
        // The range is isolated left-to-right so it does not read 640–613 in the RTL sheet (Codex 22:57, 87A67B11).
        Column(boq, "H").Should().Contain(t => t.StartsWith("כמות 0: נבדקו שמות הבלוקים והשכבות בארבעת הקבצים (SM, GM, DR, HA) — לא נמצאו שלטים \u200E613–640\u200E;", StringComparison.Ordinal));
        Column(boq, "H").Should().Contain(t => t.Contains("בשרטוט 147 תמרורים מול 140 עמודים — לא נבדק אילו עמודים נושאים שני תמרורים — לאישור", StringComparison.Ordinal));
        Column(boq, "B").Should().Contain("פרקים מהדוגמה שלא נכללו בטיוטה זו");
        Column(boq, "G").Count(t => t == "לא נכלל").Should().Be(13, "frozen-G retains 8 unmeasured chapters plus 5 example-item chapter rows");
        var m3 = RowOf(boq, "A", "51.32.1942");
        At(boq, "G" + m3).Should().Be("לאישור · לבדיקה");

        var signs = BoqRulesWorkbookWriter.SheetSigns;
        Column(signs, "A").Should().Contain("505/506").And.NotContain("505");

        var cross = BoqRulesWorkbookWriter.SheetCross;
        At(cross, "F1").Should().Be("הצללה מדודה (מ\"ר)");
        Column(cross, "B").Count(t => t.StartsWith("מעבר עם הצללה ליד קו העצירה", StringComparison.Ordinal)).Should().Be(10);
        Column(cross, "B").Count(t => t.EndsWith("שטח ההצללה חושב מהפוליליין הסגור שבגבולה (שטח ההצללה לא נקרא מהשרטוט)", StringComparison.Ordinal))
            .Should().Be(3, "8AB16 / 8AB18 / 204E66: measured by their boundary polylines");
        Column(cross, "F").Should().NotContain("לא נמדד");

        var detail = BoqRulesWorkbookWriter.SheetDetail;
        Column(detail, "A").Should().Contain("ללא סעיף").And.Contain("51.32.1852/1862").And.NotContain("M3");
        Column(detail, "J").Should().Contain("2 בלוקי תמרור צוירו בשכבת TR-MARK-WHT-810-STOP ונספרו כתמרור בלבד (לא בקו העצירה)");
        Column(detail, "J").Should().Contain(t => t.Contains("בגיליון \"דוגמא\" של נטלי 4.5 ליחידה) — לאישור", StringComparison.Ordinal) &&
            t.EndsWith("לפי 4.5 מ\"ר ליחידה: 2,146.50 מ\"ר (כאן 1,073.25 מ\"ר)", StringComparison.Ordinal));
        Column(detail, "J").Should().Contain(t => t.Contains("כולל מעבר אופניים בלבד אחד (0 מ\"ר — נספר ב-812)", StringComparison.Ordinal));
        Column(detail, "J").Should().Contain("בשרטוט 147 תמרורים מול 140 עמודים — לא נבדק אילו עמודים נושאים שני תמרורים");
        Column(detail, "D").Should().Contain("מספר העמודים בסעיף 51.31.2202");

        var excluded = BoqRulesWorkbookWriter.SheetExcluded;
        Column(excluded, "A").Should().Contain("811: גבול ההצללה ליד קו העצירה (או עותק שלו) — השטח נספר פעם אחת")
            .And.Contain("811: קו רציף לאורך מעבר עם הצללה, כ-2.5 מ' מהקו המקווקו של אותו מעבר — חלק מאותו מעבר, לא נספר שוב")
            .And.Contain("אבני שפה נלקחות מקובץ GM; העצם בקובץ SM לא נכלל — לבדיקה אם הוא ייחודי")
            .And.Contain(t => t.StartsWith("לבדיקה: עצם באורך 540 מטר בשכבת TR-MARK-WHT-812-BIKE", StringComparison.Ordinal));

        var unclassified = BoqRulesWorkbookWriter.SheetUnclassified;
        At(unclassified, "G1").Should().Be("סוגי עצמים / בלוקים");
        At(unclassified, "H1").Should().Be("הערה");
        Column(unclassified, "H").Should().Contain(t => t.StartsWith("קווים (LINE/POLYLINE) בשכבת העמודים — לא נמדדו. העמודים נספרו כבלוקים (140) בסעיף 51.31.2202", StringComparison.Ordinal) &&
            t.Contains("למשל 207030", StringComparison.Ordinal) && t.Contains("אורך בתוכנית 882.87", StringComparison.Ordinal));

        var missing = BoqRulesWorkbookWriter.SheetMissing;
        Column(missing, "D").Should().NotContain(new[] { "8AB16", "8AB18", "204E66", "10A971" });
        Column(missing, "A").Skip(1).Should().OnlyContain(t => !t.StartsWith("A", StringComparison.Ordinal), "the line label, never the internal id");
        using var inputJson = JsonDocument.Parse(File.ReadAllText(FixturePath("golden_inputs_6422.json")));
        var unresolvedHaHandles = inputJson.RootElement.GetProperty("ha_hatch_rows").EnumerateArray()
            .Where(h => h.GetProperty("state").GetString() == "unresolved")
            .Select(h => h.GetProperty("handle").GetString()!).ToList();
        unresolvedHaHandles.Should().HaveCount(39, "unknown hatch areas are retained, not omitted or treated as zero");
        Column(missing, "D").Skip(1).Should().BeEquivalentTo(unresolvedHaHandles,
            "each unresolved HA hatch remains individually traceable, not replaced by an aggregate");
        Column(missing, "E").Skip(1).Should().OnlyContain(t => t == "הצללה שלא ניתן היה לקרוא את שטחה מהשרטוט — השטח לא ידוע (לא אפס)");
        Column(missing, "F").Skip(1).Should().OnlyContain(t => t == "לבדוק את ההצללה בשרטוט (מאפייני ההצללה / גבול סגור)");

        Column(BoqRulesWorkbookWriter.SheetSources, "A").Should().Contain(t => t.StartsWith("כללי החישוב (גרסה ", StringComparison.Ordinal));
        Column(BoqRulesWorkbookWriter.SheetExplain, "A").Should().NotContain(t => t.StartsWith("מה השתנה", StringComparison.Ordinal));
    }
}
