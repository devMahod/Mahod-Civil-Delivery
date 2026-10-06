using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Rules 2.1 "one physical object once" (Natali, 29.09.2026): the port of boq_geometry.object_length / entity_chords, the
/// engine steps (plan length per length part, same block at the same point once per line, line_sum, line control), the
/// native adapter's chords / block points and the workbook rows — on synthetic data (no project data).
/// </summary>
public sealed class BoqRulesV2OneObjectOnceTests
{
    // A small 2.1 ruleset in the same schema; layer names and items are synthetic.
    private const string RulesJson = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.1",
      "parameters": [{"id": "road_class", "label": "road", "value": 1}],
      "chapters": [{"id": "10.01", "title": "c"}],
      "measurement": {"length_basis": "plan", "tolerance_m": 0.03, "piece_m": 0.25, "parallel_sin": 0.05},
      "boq": [
        {"id": "K1", "chapter": "10.01", "item": "10.01.0010", "unit": "m", "parts": [
          {"label": "kerb", "src": ["A", "B"], "layers": ["KERB"], "kind": "length", "object_width_m": 0.23, "object_width_note": "two faces"}]},
        {"id": "K2", "chapter": "10.01", "item": "10.01.0120", "unit": "m", "parts": [
          {"label": "island kerb", "src": ["A"], "layers": ["ISLAND"], "kind": "length", "object_width_m": 0.23, "object_width_note": "two faces"}]},
        {"id": "KP", "chapter": "10.01", "item": "10.01.2640", "unit": "m", "parts": [
          {"label": "kerb painting = kerb + island kerb", "kind": "line_sum", "lines": ["K1", "K2"], "decided_by": "engineer, 29.09"}],
         "control": {"label": "control: painted kerb lines", "src": ["M"], "layers": ["PAINT"], "kind": "length", "object_width_m": 0.1}},
        {"id": "N1", "chapter": "10.01", "item": null, "unit": "u", "parts": [
          {"label": "racks (M)", "src": ["M"], "block_layers": ["RACK"], "kind": "count", "same_point_m": 0.1},
          {"label": "racks (A)", "src": ["A"], "layers": ["RACK"], "kind": "count", "same_point_m": 0.1}]}
      ]
    }
    """;

    private static BoqRuleset Rules() => BoqRuleset.Parse(RulesJson);

    private static BoqSegment Seg(double x1, double y1, double x2, double y2) => new(new BoqPoint(x1, y1), new BoqPoint(x2, y2));

    private static void AddLine(BoqInputSet input, string src, string layer, string handle, double x1, double y1, double x2, double y2)
    {
        var seg = Seg(x1, y1, x2, y2);
        input.Records.Add(new BoqRecord(src, layer, "LINE", "length", seg.Length, handle, false, "", null));
        input.LengthGeometry[(src, handle)] = new BoqEntityGeometry(src, handle, layer, false, new[] { seg }, null);
    }

    private static void AddBlock(BoqInputSet input, string src, string layer, string handle, string block, double? x = null, double? y = null)
    {
        input.Records.Add(new BoqRecord(src, layer, "INSERT", "count", 1.0, handle, false, block, null));
        if (x is { } px && y is { } py) input.CountPoints[(src, handle)] = new BoqPoint(px, py);
    }

    private static int IndexOf(BoqEngineResult result, string src, string handle, string kind) =>
        result.Input.Records.FindIndex(r => r.Src == src && r.Handle == handle && r.Kind == kind);

    // ------------------------------------------------------------------ object_length (port of boq_geometry.py)

    [Fact]
    public void AFiveLinePipeCountsOnce()
    {
        // axis + two walls (±0.20) + two encasement lines (±0.27): one pipe 10 m long.
        var chords = new[] { 0.0, 0.20, -0.20, 0.27, -0.27 }.Select(dy => Seg(100, 50 + dy, 110, 50 + dy)).ToList();
        var measured = BoqObjectMeasure.ObjectLength(chords, 0.54);
        measured.Raw.Should().BeApproximately(50.0, 1e-9);
        measured.Length.Should().BeApproximately(10.0, 1e-9);
        measured.ByMultiplicity.Keys.Should().Equal(5);
        measured.ByMultiplicity[5].Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void TwoFacesOfOneStoneCountOnce()
    {
        // A curb drawn as its two faces 0.17 apart (object width 0.23 + 0.03 tolerance).
        var measured = BoqObjectMeasure.ObjectLength(new[] { Seg(0, 0, 20, 0), Seg(0, 0.17, 20, 0.17) }, 0.23);
        measured.Length.Should().BeApproximately(20.0, 1e-9);
        measured.Raw.Should().BeApproximately(40.0, 1e-9);
        measured.ByMultiplicity.Keys.Should().Equal(2);
    }

    [Fact]
    public void TwoSeparateCurbsTwoMetresApartCountTwice()
    {
        var measured = BoqObjectMeasure.ObjectLength(new[] { Seg(0, 0, 20, 0), Seg(0, 2, 20, 2) }, 0.23);
        measured.Length.Should().BeApproximately(40.0, 1e-9);
        measured.ByMultiplicity.Keys.Should().Equal(1);
    }

    [Fact]
    public void AnAxisThatRunsBeyondItsSideLinesCountsAloneThere()
    {
        // A 12 m axis; walls and encasement stop at 10 m (the axis runs on into the manhole).
        var chords = new List<BoqSegment> { Seg(0, 0, 12, 0) };
        foreach (var dy in new[] { 0.20, -0.20, 0.27, -0.27 }) chords.Add(Seg(0, dy, 10, dy));
        var measured = BoqObjectMeasure.ObjectLength(chords, 0.54);
        measured.Raw.Should().BeApproximately(52.0, 1e-9);
        measured.Length.Should().BeApproximately(12.0, 1e-9);
        measured.ByMultiplicity.Keys.Should().Equal(1, 5);
        measured.ByMultiplicity[1].Should().BeApproximately(2.0, 1e-9);
        measured.ByMultiplicity[5].Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void AnExactDuplicateLineCountsOnceInEitherDirectionAndTinyChordsAreDropped()
    {
        var line = Seg(100, 100, 130, 140); // 50 m, diagonal
        var reversed = Seg(130, 140, 100, 100);
        var measured = BoqObjectMeasure.ObjectLength(new[] { line, line, reversed, Seg(0, 0, 1e-7, 0) }, 0.1);
        measured.Raw.Should().BeApproximately(150.0, 1e-9, "a chord of 1e-6 m or less is dropped");
        measured.Length.Should().BeApproximately(50.0, 1e-9);
        measured.ByMultiplicity.Keys.Should().Equal(3);
    }

    [Fact]
    public void ALongChordAcrossManyGridCellsIsStillFoundByItsNeighbours()
    {
        // A 3 km diagonal duplicated: registered for every piece instead of cell by cell; still counted once.
        var chord = Seg(0, 0, 2400, 1800);
        var measured = BoqObjectMeasure.ObjectLength(new[] { chord, chord }, 0.1);
        measured.Length.Should().BeApproximately(3000.0, 1e-6);
        measured.ByMultiplicity.Keys.Should().Equal(2);
    }

    [Fact]
    public void ChordHelpersCloseTheChainAndTessellateBulgesWithExactEndpoints()
    {
        var square = BoqObjectMeasure.VertexChainChords(new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) }, closed: true);
        square.Should().HaveCount(4, "a closed chain includes its closing chord");
        square[3].Should().Be(Seg(0, 10, 0, 0));
        BoqObjectMeasure.VertexChainChords(new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) }, closed: false)
            .Should().HaveCount(3);

        // bulge 1 = a half circle (radius 5 around (105, 0)); chords ≤ 0.5 m; first and last points exact.
        var points = BoqObjectMeasure.BulgePoints(100, 0, 110, 0, 1.0);
        points.Should().HaveCount(33);
        points[0].Should().Be((100.0, 0.0));
        points[^1].Should().Be((110.0, 0.0));
        points.Should().OnlyContain(p => Math.Abs(Math.Sqrt((p.X - 105) * (p.X - 105) + p.Y * p.Y) - 5) < 1e-9);
        var arc = BoqObjectMeasure.PolylineChords(new List<(double X, double Y, double Bulge)> { (100, 0, 1.0), (110, 0, 0.0) }, closed: false);
        arc.Should().HaveCount(32);
        arc.Sum(s => s.Length).Should().BeApproximately(32 * 10 * Math.Sin(Math.PI / 64), 1e-9);
        arc.Should().OnlyContain(s => s.Length <= 0.5);

        // A full circle: chords bounded by the 0.5 m length and the 5 mm sagitta; the perimeter within the sagitta error.
        var circle = BoqObjectMeasure.ArcChords(0, 0, 10, 0, 2 * Math.PI);
        circle.Should().OnlyContain(s => s.Length <= 0.5 + 1e-12);
        circle.Sum(s => s.Length).Should().BeApproximately(2 * Math.PI * 10, 0.01);
    }

    [Fact]
    public void ACounterEntryThatReachesZeroIsRemoved()
    {
        var counter = new BoqCounter();
        counter.Add("a");
        counter.Add("b", 2);
        counter.Subtract("a");
        counter.Subtract("b");
        counter.Subtract("missing");
        counter.Keys.Should().Equal("b");
        counter["b"].Should().Be(1);
        counter["a"].Should().Be(0);
    }

    // ------------------------------------------------------------------ ruleset 2.1

    [Fact]
    public void TheEmbeddedRulesetCarriesTheOneObjectOnceKeys()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        rules.Measurement.Present.Should().BeTrue();
        rules.Measurement.LengthBasis.Should().Be("plan");
        rules.Measurement.ToleranceM.Should().Be(0.03);
        rules.Measurement.PieceM.Should().Be(0.25);
        rules.Measurement.ParallelSin.Should().Be(0.05);
        rules.AllParts.Where(p => p.Kind == "length").Should().OnlyContain(p => p.ObjectWidth != null && !string.IsNullOrEmpty(p.ObjectWidthNote));
        rules.AllParts.Where(p => p.Kind is "count" or "count_x").Should().OnlyContain(p => p.SamePoint == 0.1);
        // v4: two line_sum lines — pipe video (D8 = D1..D5) and curb painting (M4 = C1 + C2, with its control).
        rules.AllParts.Where(p => p.Kind == "line_sum").Should().HaveCount(2);
        var sum = rules.Lines.Where(l => l.Control != null).SelectMany(l => l.Parts).Should().ContainSingle(p => p.Kind == "line_sum").Subject;
        sum.Lines.Should().Equal("C1", "C2");
        sum.DecidedBy.Should().NotBeNullOrWhiteSpace();
        var line = rules.Lines.Single(l => l.Parts.Contains(sum));
        line.Control.Should().NotBeNull();
        line.Control!.ObjectWidth.Should().Be(0.1);
        line.Control.Layers.Should().NotBeEmpty();
    }

    [Fact]
    public void ALineSumMustNameOtherLinesAndAControlItsWidth()
    {
        FluentActions.Invoking(() => BoqRuleset.Parse(RulesJson.Replace("\"lines\": [\"K1\", \"K2\"]", "\"lines\": [\"K1\", \"ZZ\"]")))
            .Should().Throw<InvalidDataException>().WithMessage("*ZZ*");
        FluentActions.Invoking(() => BoqRuleset.Parse(RulesJson.Replace("\"lines\": [\"K1\", \"K2\"]", "\"lines\": [\"KP\"]")))
            .Should().Throw<InvalidDataException>().WithMessage("*KP*");
        FluentActions.Invoking(() => BoqRuleset.Parse(RulesJson.Replace(", \"kind\": \"length\", \"object_width_m\": 0.1}", ", \"kind\": \"length\"}")))
            .Should().Throw<InvalidDataException>().WithMessage("*object_width_m*");
        FluentActions.Invoking(() => BoqRuleset.Parse(RulesJson.Replace("\"length_basis\": \"plan\"", "\"length_basis\": \"3d\"")))
            .Should().Throw<InvalidDataException>().WithMessage("*3d*");
        // A line_sum reads lines already complete: an earlier line without a line_sum of its own (review 30/09).
        FluentActions.Invoking(() => BoqRuleset.Parse(RulesJson.Replace("\"lines\": [\"K1\", \"K2\"]", "\"lines\": [\"K1\", \"N1\"]")))
            .Should().Throw<InvalidDataException>().WithMessage("*N1*earlier line*");
    }

    [Fact]
    public void ALengthPartWithoutAnObjectWidthKeepsTheSumOfItsRecords()
    {
        var rules = BoqRuleset.Parse(RulesJson.Replace(
            "\"layers\": [\"KERB\"], \"kind\": \"length\", \"object_width_m\": 0.23, \"object_width_note\": \"two faces\"}",
            "\"layers\": [\"KERB\"], \"kind\": \"length\"}"));
        rules.Lines.Single(l => l.Id == "K1").Parts[0].ObjectWidth.Should().BeNull("the replacement above removed K1's width");
        var input = new BoqInputSet();
        AddLine(input, "A", "KERB", "F1", 0, 0, 20, 0);
        AddLine(input, "A", "KERB", "F2", 0, 0.17, 20, 0.17);
        var kerb = BoqRulesEngine.Run(rules, input).Parts.Single(p => p.LineId == "K1");
        kerb.Base.Should().BeApproximately(40.0, 1e-9, "a 2.0 length part is the sum of its records");
        kerb.DrawnSum.Should().BeNull();
    }

    // ------------------------------------------------------------------ engine

    private static BoqInputSet CurbInputs()
    {
        var input = new BoqInputSet();
        AddLine(input, "A", "KERB", "K1", 0, 0, 20, 0);          // face 1
        AddLine(input, "A", "KERB", "K2", 0, 0.17, 20, 0.17);    // face 2 of the same curb
        AddLine(input, "B", "KERB", "K3", 0, 2, 20, 2);          // another curb, 2 m away
        input.Records.Add(new BoqRecord("A", "KERB", "ARC", "length", 5.0, "K4", false, "", null)); // no plan geometry
        AddLine(input, "A", "ISLAND", "I1", 100, 0, 110, 0);
        AddLine(input, "A", "ISLAND", "I2", 100, 0, 110, 0);     // exact duplicate
        AddLine(input, "M", "PAINT", "P1", 0, 0.05, 20, 0.05);   // painted kerb lines (control only)
        AddLine(input, "M", "PAINT", "P2", 0, 0.05, 20, 0.05);
        input.Records.Add(new BoqRecord("M", "PAINT", "HATCH", "area", 3.0, "P3", false, "", null)); // not the control's kind
        return input;
    }

    [Fact]
    public void LengthPartsCountEveryPhysicalObjectOnceInPlanAndKeepTheDrawnSum()
    {
        var result = BoqRulesEngine.Run(Rules(), CurbInputs());
        var kerb = result.Parts.Single(p => p.LineId == "K1");
        kerb.Objects.Should().Be(4);
        kerb.DrawnSum.Should().BeApproximately(65.0, 1e-9);
        kerb.PlanSum.Should().BeApproximately(65.0, 1e-9);
        kerb.Base.Should().BeApproximately(45.0, 1e-9, "two faces once (20) + the curb 2 m away (20) + the record without geometry (5)");
        kerb.Quantity.Should().BeApproximately(45.0, 1e-9);
        kerb.NoGeometry.Should().Be(1);
        kerb.Width.Should().Be(0.23);
        kerb.WidthNote.Should().Be("two faces");
        // v4: the note quotes the plan length (the 3D drawn sum stays on the one-object-once sheet).
        kerb.Notes[0].Should().Be("נמדדו 4 קווים שאורכם בתוכנית 65.0 מ'; עצם אחד נספר פעם אחת: 45.0 מ' (two faces)");
        kerb.Notes.Should().Contain("1 עצמים בלי גאומטריה בתוכנית נמדדו לפי האורך המקורי");
        kerb.ByMultiplicity!.Keys.Should().Equal(1, 2);

        var island = result.Parts.Single(p => p.LineId == "K2");
        island.Base.Should().BeApproximately(10.0, 1e-9, "an exact duplicate line counts once");
        island.DrawnSum.Should().BeApproximately(20.0, 1e-9);
    }

    [Fact]
    public void ALineSumIsTheSumOfItsLinesRoundedQuantitiesAndTheControlIsReportedButExcluded()
    {
        var result = BoqRulesEngine.Run(Rules(), CurbInputs());
        var painting = result.Parts.Single(p => p.LineId == "KP");
        painting.Quantity.Should().BeApproximately(55.0, 1e-9, "round(45, 2) + round(10, 2)");
        painting.Base.Should().BeApproximately(55.0, 1e-9);
        painting.Objects.Should().Be(0);
        painting.Notes.Should().Contain("engineer, 29.09");
        result.Lines.Single(l => l.Line.Id == "KP").Quantity.Should().BeApproximately(55.0, 1e-9);

        var control = result.Controls.Should().ContainKey("KP").WhoseValue;
        control.Label.Should().Be("control: painted kerb lines");
        control.Objects.Should().Be(2);
        control.Length.Should().BeApproximately(20.0, 1e-9, "the two painted lines are one line");
        control.Drawn.Should().BeApproximately(40.0, 1e-9);
        var reason = "10.01.2640: בקרה בלבד — הכמות היא סכום הסעיפים 10.01.0010 + 10.01.0120 (engineer, 29.09)";  // v4: named by its item (REF)
        BoqRulesEngine.ControlReason(result.Rules, result.Rules.Lines.Single(l => l.Id == "KP"), result.RoadClass).Should().Be(reason);
        foreach (var handle in new[] { "P1", "P2" })
        {
            var index = IndexOf(result, "M", handle, "length");
            result.Buckets[index].Should().Be(BoqBucket.Excluded);
            result.Reasons[index].Should().Be(reason);
        }
        result.Buckets[IndexOf(result, "M", "P3", "area")].Should().Be(BoqBucket.Unclassified, "the control reads its own kind only");
    }

    [Fact]
    public void TheSameBlockAtTheSamePointCountsOncePerLineAlsoAcrossFiles()
    {
        var input = new BoqInputSet();
        AddBlock(input, "M", "RACK", "R1", "RACK-2", 0, 0);
        AddBlock(input, "M", "RACK", "R2", "RACK-2", 0.05, 0);      // same block 5 cm away: the same object
        AddBlock(input, "M", "RACK", "R3", "RACK-2", 0.5, 0);       // 50 cm away: another object
        AddBlock(input, "M", "RACK", "R4", "OTHER", 0, 0);          // another block at the same point
        AddBlock(input, "A", "RACK", "R9", "RACK-2", 0.02, 0.03);   // the same rack drawn again in file A
        AddBlock(input, "A", "RACK", "R10", "", 0, 0);              // no block name: keyed by its layer
        AddBlock(input, "A", "RACK", "R11", "RACK-2");              // no point: never merged
        var result = BoqRulesEngine.Run(Rules(), input);

        var inM = result.Parts.Single(p => p.LineId == "N1" && p.Index == 0);
        inM.Objects.Should().Be(3);
        inM.Base.Should().Be(3.0);
        inM.Signs.Items.ToDictionary(p => p.Key, p => p.Value).Should().BeEquivalentTo(new Dictionary<string, int> { ["RACK-2"] = 2, ["OTHER"] = 1 });
        var inA = result.Parts.Single(p => p.LineId == "N1" && p.Index == 1);
        inA.Objects.Should().Be(2);
        inA.Layers.Items.ToDictionary(p => p.Key, p => p.Value).Should().BeEquivalentTo(new Dictionary<string, int> { ["RACK"] = 2 });
        inA.Srcs.Items.ToDictionary(p => p.Key, p => p.Value).Should().BeEquivalentTo(new Dictionary<string, int> { ["A"] = 2 });
        result.Lines.Single(l => l.Line.Id == "N1").Quantity.Should().Be(5.0);

        result.Dedup.Should().Equal(
            new BoqDedupRow("N1", "racks (M)", "M", "RACK", "RACK-2", "R2", "M:R1"),
            new BoqDedupRow("N1", "racks (A)", "A", "RACK", "RACK-2", "R9", "M:R1"));
        foreach (var (src, handle) in new[] { ("M", "R2"), ("A", "R9") })
        {
            var index = IndexOf(result, src, handle, "count");
            result.Buckets[index].Should().Be(BoqBucket.Excluded);
            // v4: named by REF — N1 has no item, so its part labels (as the reference REF()).
            result.Reasons[index].Should().Be($"{result.Ref("N1")}: אותו בלוק באותה נקודה כמו M:R1 — נספר פעם אחת");
            result.Ref("N1").Should().Be("racks (M) / racks (A)");
            result.Items[index].Should().BeNull();
        }
        result.BucketLabel(IndexOf(result, "A", "R10", "count")).Should().Be("item:N1:1");
        result.BucketLabel(IndexOf(result, "A", "R11", "count")).Should().Be("item:N1:1");
    }

    [Fact]
    public void TheWorkbookSumsTheLineCellsAndShowsTheOneObjectOnceSheet()
    {
        var result = BoqRulesEngine.Run(Rules(), CurbInputs());
        var directory = Path.Combine(Path.GetTempPath(), "boq-rules-v21-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "boq.xlsx");
            BoqRulesWorkbookWriter.Write(result, path, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 29)));
            var book = XlsxFormulaReader.Load(path);
            book.SheetNames.Should().Equal(BoqRulesWorkbookWriter.SheetOrder);
            book.SheetNames.IndexOf(BoqRulesWorkbookWriter.SheetObjects)
                .Should().Be(book.SheetNames.IndexOf(BoqRulesWorkbookWriter.SheetDetail) + 1);

            // v4: column A is the item number the engineer knows (REF), never the internal line id.
            var detail = BoqRulesWorkbookWriter.SheetDetail;
            var k1 = book.FindRow(detail, "A", t => t == "10.01.0010");
            var k2 = book.FindRow(detail, "A", t => t == "10.01.0120");
            var kp = book.FindRow(detail, "A", t => t == "10.01.2640");
            book.Formula(detail, "F" + kp).Should().Be($"I{k1}+I{k2}");
            book.Formula(detail, "I" + kp).Should().Be($"ROUND(F{kp},2)");
            book.Text(detail, "D" + kp).Should().Be("סכום סעיפים 10.01.0010 + 10.01.0120");
            book.Text(detail, "E" + kp).Should().Be("—");
            book.Evaluate(detail, "I" + kp).Should().BeApproximately(55.0, 1e-9);
            book.Text(detail, "J" + kp).Should().Be("engineer, 29.09 | control: painted kerb lines: 2 קווים, 20.0 מ' (עצם אחד פעם אחת) — להשוואה בלבד, לא נכנס לכמות");
            book.Text(detail, "J" + k1).Should().StartWith("נמדדו 4 קווים שאורכם בתוכנית 65.0 מ'");
            book.FindRow(detail, "A", t => t == "ללא סעיף").Should().NotBeNull("the racks line has no item");

            var objects = BoqRulesWorkbookWriter.SheetObjects;
            var row = book.FindRow(objects, "B", t => t == "kerb");
            book.Text(objects, "A" + row).Should().Be("10.01.0010");
            book.Evaluate(objects, "F" + row).Should().BeApproximately(65.0, 1e-9);
            book.Evaluate(objects, "G" + row).Should().BeApproximately(65.0, 1e-9);
            book.Evaluate(objects, "H" + row).Should().BeApproximately(45.0, 1e-9);
            book.Evaluate(objects, "I" + row).Should().Be(0.23);
            // v4: how the object is drawn — the length at one line (the curb 2 m away) and at two lines (both faces).
            book.Text(objects, "J" + row).Should().Be("two faces | אורך העצם לפי מספר הקווים שצוירו באותו מקום: קו אחד: 20 מ'; 2 קווים: 20 מ'");
            book.Text(objects, "I1").Should().Be("מרחק מרבי בין קווי אותו עצם (מ')");
            book.FindRow(objects, "A", t => t == BoqRulesWorkbookWriter.ObjectsDuplicateBlocksTitle).Should().NotBeNull();

            var explanation = book.ColumnTexts(BoqRulesWorkbookWriter.SheetExplain, "A");
            explanation.Should().Contain(t => t.Contains("(סעיף 10.01.2640)", StringComparison.Ordinal) && t.Contains("engineer, 29.09", StringComparison.Ordinal));
            explanation.Should().Contain(BoqRulesWorkbookWriter.ExplainOneObjectOnce);
            explanation.Should().Contain(t => t.Contains("0 כפילויות הוסרו", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ native adapter

    private static NeutralQuantityRecord Neutral(string handle, string etype, string kind, double value, string layer,
        Dictionary<string, string>? parameters = null, double[]? bbox = null) => new()
    {
        RecordId = $"q-{handle}-{kind}", ProjectProfileId = "p", RunId = "run",
        Source = new QuantitySource { Drawing = "x-A-.dwg", DrawingHash = new string('b', 64), Handle = handle, EntityType = etype, Layer = layer },
        Measurement = new QuantityMeasurement
        {
            Kind = kind, Method = kind == "count" ? "block-count" : "line-length", RawValue = value, Unit = kind == "count" ? "יח'" : "m",
            GeometryEvidence = bbox ?? new[] { 1.0, 2.0, 3.0, 4.0 }, Parameters = parameters ?? new Dictionary<string, string>(),
        },
    };

    // What the collector (1.3.4+) writes for a straight chain: the raw vertices and the same plan measuring chain.
    private static Dictionary<string, string> Segments(string vertices, bool closed, string units = "Meters") => new()
    {
        ["cad_entity_database_insunits"] = units,
        [QuantityGeometryEvidence.SegmentsKey] = vertices,
        [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.StatusComplete,
        [QuantityGeometryEvidence.SegmentsClosedKey] = closed ? "true" : "false",
        [QuantityGeometryEvidence.PlanChainKey] = vertices,
        [QuantityGeometryEvidence.PlanChainStatusKey] = QuantityGeometryEvidence.StatusComplete,
        [QuantityGeometryEvidence.PlanChainClosedKey] = closed ? "true" : "false",
    };

    [Fact]
    public void LengthReadsTheMeasuringChainWhileClassificationKeepsTheRawVertices()
    {
        // Live 29.09.2026: tessellated arcs in the classification chain moved 20 SM records between crossing roles.
        // The collector now writes both: raw vertices (classification) and the tessellated plan chain (length).
        var arc = QuantityGeometryEvidence.FormatVertices(BoqObjectMeasure.BulgePoints(0, 0, 10, 0, 1.0));
        var parameters = Segments("0,0;10,0", closed: false);
        parameters[QuantityGeometryEvidence.PlanChainKey] = arc;
        parameters[QuantityGeometryEvidence.PlanChainStatusKey] = QuantityGeometryEvidence.StatusComplete;
        parameters[QuantityGeometryEvidence.PlanChainClosedKey] = "false";
        var records = new List<NeutralQuantityRecord> { Neutral("60", "LWPOLYLINE", "length", Math.PI * 5, "KERB", parameters) };
        var input = BoqNeutralRecordAdapter.Build(Rules(), new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("A", "run-a", @"C:\p\x-A-.dwg", new string('b', 64), records, Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });

        input.LengthGeometry[("A", "60")].Segments.Should().HaveCountGreaterThan(20, "the measuring chain follows the arc");
        input.Geometry.Single(g => g.Handle == "60").Segments.Should().ContainSingle(
            "classification keeps the raw vertex chain, exactly as the reference classified it");
        QuantityGeometryEvidence.IsGeometryKey(QuantityGeometryEvidence.PlanChainKey).Should().BeTrue();

        // A scan written before the plan chain existed (before 1.3.4) is refused: its raw chain is never a measuring
        // chain (review 30/09 — a 1.3.3 scan brought back the old arc lengths and crossing roles without any warning).
        var legacyParameters = Segments("0,0;10,0", closed: false);
        legacyParameters.Remove(QuantityGeometryEvidence.PlanChainKey);
        legacyParameters.Remove(QuantityGeometryEvidence.PlanChainStatusKey);
        legacyParameters.Remove(QuantityGeometryEvidence.PlanChainClosedKey);
        var legacyRecords = new List<NeutralQuantityRecord> { Neutral("61", "LWPOLYLINE", "length", 10, "KERB", legacyParameters) };
        BoqNeutralRecordAdapter.LegacyEvidenceRefusal(legacyRecords).Should().Contain("גרסה קודמת");
        BoqNeutralRecordAdapter.LegacyEvidenceRefusal(records).Should().BeNull();
        var legacy = BoqNeutralRecordAdapter.Build(Rules(), new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("A", "run-a", @"C:\p\x-A-.dwg", new string('b', 64), legacyRecords,
                Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        legacy.LengthGeometry.Should().NotContainKey(("A", "61"));
    }

    [Fact]
    public void ArcsAreTessellatedAtMostEightDegreesPerChord()
    {
        // r = 1000 m quarter circle: 0.5 m chords would be ~0.03°, so the length rule decides; r = 2 m: the angle rule.
        BoqObjectMeasure.BulgePoints(2, 0, 0, 2, Math.Tan(Math.PI / 8)).Should().HaveCount(12 + 1, "π/2 over 8° = 11.25 → 12 chords");
        BoqObjectMeasure.ArcPoints(0, 0, 2, 0, Math.PI / 2).Count.Should().BeGreaterThanOrEqualTo(12 + 1);
        BoqObjectMeasure.ArcPoints(0, 0, 1, 0, 2 * Math.PI).Should().HaveCount(45 + 1, "a full circle is exactly 45 chords of 8°");
    }

    [Fact]
    public void TheAdapterBuildsClosedAndBulgedPlanChordsAndBlockPointsWithoutGuessingUnits()
    {
        var arcText = QuantityGeometryEvidence.FormatVertices(BoqObjectMeasure.BulgePoints(100, 0, 110, 0, 1.0));
        QuantityGeometryEvidence.TryParseVertices(arcText, out var arcVertices).Should().BeTrue();
        var arcChords = 0.0;
        for (var i = 0; i + 1 < arcVertices.Count; i++)
            arcChords += Math.Sqrt(Math.Pow(arcVertices[i + 1].X - arcVertices[i].X, 2) + Math.Pow(arcVertices[i + 1].Y - arcVertices[i].Y, 2));
        var records = new List<NeutralQuantityRecord>
        {
            Neutral("40", "LWPOLYLINE", "length", 40.0, "KERB", Segments("0,0;10,0;10,10;0,10", closed: true)),
            Neutral("41", "LWPOLYLINE", "length", Math.PI * 5, "KERB", Segments(arcText, closed: false)),
            Neutral("42", "LINE", "length", 7.0, "KERB", new Dictionary<string, string>
            {
                ["cad_entity_database_insunits"] = "Meters",
                [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.OverLimit(70),
            }),
            Neutral("43", "LINE", "length", 12.0, "KERB", Segments("0,50000;12000,50000", closed: false, units: "Millimeters")),
            Neutral("44", "LINE", "length", 3.0, "KERB", Segments("0,0;3,0", closed: false, units: "Unitless")),
            Neutral("50", "BLOCKREFERENCE", "count", 1, "RACK", new Dictionary<string, string>
            {
                ["cad_entity_database_insunits"] = "Millimeters", ["block_name"] = "RACK-2",
                [BoqNeutralRecordAdapter.InsertPointKey] = "1000,2000",
            }, bbox: new[] { 0.0, 0.0, 10.0, 10.0 }),
            Neutral("51", "BLOCKREFERENCE", "count", 1, "RACK", new Dictionary<string, string> { ["block_name"] = "RACK-2" },
                bbox: new[] { 4.0, 6.0, 8.0, 10.0 }),
        };
        var input = BoqNeutralRecordAdapter.Build(Rules(), new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("A", "run-a", @"C:\p\x-A-.dwg", new string('b', 64), records, Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });

        var square = input.LengthGeometry[("A", "40")];
        square.Segments.Should().HaveCount(4, "a closed polyline includes its closing chord");
        square.Segments[3].Should().Be(Seg(0, 10, 0, 0));
        input.Geometry.Single(g => g.Handle == "40").Segments.Should().HaveCount(3, "classification geometry keeps the reference's open chain");
        input.LengthGeometry[("A", "41")].Segments.Should().HaveCount(32);
        input.LengthGeometry[("A", "41")].Segments.Sum(s => s.Length).Should().BeApproximately(arcChords, 1e-9);
        input.LengthGeometry.Should().NotContainKey(("A", "42"), "a bounded vertex list is not complete geometry");
        input.LengthGeometry[("A", "43")].Segments.Should().ContainSingle().Which.Should().Be(Seg(0, 50, 12, 50));
        input.LengthGeometry.Should().NotContainKey(("A", "44"), "unknown units are never guessed for a quantity");
        input.Warnings.Should().Contain(w => w.Contains("A:44") && w.Contains("האורך נלקח מהמדידה המקורית"));
        input.CountPoints[("A", "50")].Should().Be(new BoqPoint(1.0, 2.0));
        input.CountPoints[("A", "51")].Should().Be(new BoqPoint(6.0, 8.0), "without an insertion point the centre of the extents");
        // The engineer reads Hebrew, not evidence key names (30.09 text patch).
        input.ObjectGeometryReader.Should().Contain("קווים פתוחים וסגורים").And.NotContain("cad_");

        var kerb = BoqRulesEngine.Run(Rules(), input).Parts.Single(p => p.LineId == "K1");
        kerb.Objects.Should().Be(5);
        kerb.NoGeometry.Should().Be(2);
        kerb.DrawnSum.Should().BeApproximately(40 + Math.PI * 5 + 7 + 12 + 3, 1e-9);
        kerb.Base.Should().BeApproximately(40 + arcChords + 12 + 7 + 3, 1e-6);
    }
}
