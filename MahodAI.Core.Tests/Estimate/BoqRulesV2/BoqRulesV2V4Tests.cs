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
/// Rules 2.3 / v4 of the 6422 BoQ (reference build_boq_v7.py + sm_geometry.py, 30.09.2026) on synthetic data: a hatch near
/// the stop line with its dashed line is ONE crossing (dashed × standard width + hatch area; boundary polyline and edge
/// line are members; a missing hatch area comes from the boundary polyline), noise lines, block filters, count_of, the
/// item number (REF) in every reason, the zero-length line as information, missing objects listed once, and the
/// workbook texts that follow from them.
/// </summary>
public sealed class BoqRulesV2V4Tests
{
    private const string CrossingRules = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.3 (test)",
      "parameters": [
        {"id": "road_class", "label": "road", "value": 1},
        {"id": "cross_w", "label": "standard crossing width", "value": 3.0},
        {"id": "fill", "label": "painted share", "value": 0.5}
      ],
      "chapters": [{"id": "10.32", "title": "marking"}],
      "crosswalk_geometry": {
        "src": ["M"], "yellow_layers": ["Y"], "white_layers": ["W"], "hatch_layers": ["W"],
        "standard_width_param": "cross_w", "fill_param": "fill",
        "reasons": {"assembly_edge": "edge of an assembly", "assembly_boundary": "boundary of a hatch", "noise": "noise"}
      },
      "boq": [
        {"id": "X", "chapter": "10.32", "item": "10.32.1942", "unit": "m2", "parts": [
          {"label": "crossings", "src": ["M"], "layers": ["Y", "W"], "kind": "crosswalk_geo"}]}
      ]
    }
    """;

    private static BoqSegment Seg(double x1, double y1, double x2, double y2) => new(new BoqPoint(x1, y1), new BoqPoint(x2, y2));

    private static BoqRecord Rec(string layer, string kind, double qty, string handle, string etype = "LINE", bool closed = false) =>
        new("M", layer, etype, kind, qty, handle, closed, "", null);

    /// <summary>
    /// The 6422 pattern (SM 8AB11…): a 4 × 2 m hatch on the stop line, a closed polyline with the hatch's own vertices,
    /// the crossing's dashed line (×0.5) 2 m away and a continuous line 2.5 m beyond the dashed one.
    /// </summary>
    private static BoqInputSet Assembly(bool hatchArea, bool boundary, bool reportUnmeasured = false)
    {
        var input = new BoqInputSet();
        var corners = new[] { new BoqPoint(0, 0), new BoqPoint(4, 0), new BoqPoint(4, 2), new BoqPoint(0, 2) };
        input.Geometry.Add(new BoqEntityGeometry("M", "H1", "W", true, Array.Empty<BoqSegment>(), new BoqPoint(2, 1))
        {
            HatchBoundaryPoints = corners.Concat(corners).ToList(), // each corner is the end of one edge and the start of the next
        });
        if (hatchArea) input.Records.Add(Rec("W", "area", 7.5, "H1", "HATCH"));
        if (reportUnmeasured) input.Unmeasured.Add(new BoqUnmeasured("M", "W", "H1", "hatch area failed", "check"));
        if (boundary)
        {
            input.Geometry.Add(new BoqEntityGeometry("M", "B1", "W", false,
                new[] { Seg(0, 0, 4, 0), Seg(4, 0, 4, 2), Seg(4, 2, 0, 2) }, null) { Vertices = corners });
            input.Records.Add(Rec("W", "length", 12.0, "B1", "POLYLINE", closed: true));
            input.Records.Add(Rec("W", "area", 8.0, "B1", "POLYLINE", closed: true));
        }
        input.Geometry.Add(new BoqEntityGeometry("M", "D1", "W", false, new[] { Seg(0, 3, 6, 3) }, null) { LinetypeScale = 0.5 });
        input.Records.Add(Rec("W", "length", 6.0, "D1"));
        input.Geometry.Add(new BoqEntityGeometry("M", "E1", "W", false, new[] { Seg(0, 5.5, 6, 5.5) }, null) { LinetypeScale = 1.0 });
        input.Records.Add(Rec("W", "length", 6.0, "E1"));
        return input;
    }

    private static int Index(BoqEngineResult result, string handle, string kind) =>
        result.Input.Records.FindIndex(r => r.Handle == handle && r.Kind == kind);

    private static string? Cell(MiniXlsx.Worksheet sheet, string reference) =>
        sheet.Rows.SelectMany(r => r.Cells).Where(c => c.Reference == reference && c.Kind != MiniXlsx.CellKind.Blank)
            .Select(c => c.Value).FirstOrDefault();

    private static MiniXlsx.Worksheet Sheet(MiniXlsx.Workbook wb, string name) =>
        name == BoqRulesWorkbookWriter.SheetBoq ? wb : wb.AdditionalSheets.Single(s => s.SheetName == name);

    [Fact]
    public void AHatchNearTheStopLineWithItsDashedLineIsOneCrossing_BoundaryAndEdgeAreMembers()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(CrossingRules), Assembly(hatchArea: true, boundary: true));
        var crossing = result.Crossings.Should().ContainSingle().Subject;
        crossing.WidthSource.Should().Be("hatch-assembly");
        crossing.Kind.Should().Be("מעבר עם הצללה ליד קו העצירה: זברה לפי הקו המקווקו (רוחב תקני) + שטח ההצללה");
        crossing.Length!.Value.Should().BeApproximately(6.0, 1e-9, "the dashed line's length");
        crossing.Width.Should().Be(3.0);
        crossing.Handles.Should().Equal("B1", "D1", "E1", "H1");
        crossing.N.Should().Be(4);
        crossing.Boundary.Should().ContainKey("H1").WhoseValue.Should().Be("B1");
        crossing.EdgeHandles.Should().Equal("E1");
        crossing.HatchM2.Should().Be(7.5, "the returned hatch area wins");
        crossing.Fallback.Should().Be(0);

        result.Roles["D1"].Should().Be(BoqCrosswalkGeometry.RoleAssemblyDashed);
        result.Roles["B1"].Should().Be(BoqCrosswalkGeometry.RoleAssemblyBoundary);
        result.Roles["E1"].Should().Be(BoqCrosswalkGeometry.RoleAssemblyEdge);
        result.Roles["H1"].Should().Be(BoqCrosswalkGeometry.RoleAssemblyHatch);
        result.BucketLabel(Index(result, "D1", "length")).Should().Be("item:X:0");
        result.BucketLabel(Index(result, "H1", "area")).Should().Be("item:X:0");
        result.Reasons[Index(result, "E1", "length")].Should().Be("edge of an assembly");
        result.Reasons[Index(result, "B1", "length")].Should().Be("boundary of a hatch");
        result.Reasons[Index(result, "B1", "area")].Should().Be("boundary of a hatch", "the hatch area was returned: its boundary is not counted");
        // zebra = dashed length × standard width × painted share, plus the measured hatch area.
        result.Parts.Single().Quantity.Should().BeApproximately(6 * 3.0 * 0.5 + 7.5, 1e-9);
    }

    [Fact]
    public void AMissingHatchAreaComesFromItsBoundaryPolyline_WhichBecomesTheItemsObject()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(CrossingRules), Assembly(hatchArea: false, boundary: true, reportUnmeasured: true));
        var crossing = result.Crossings.Single();
        crossing.HatchM2.Should().Be(8.0);
        crossing.Fallback.Should().Be(1);
        crossing.MissingHatches.Should().Be(0);
        result.BucketLabel(Index(result, "B1", "area")).Should().Be("item:X:0");
        result.Reasons[Index(result, "B1", "area")].Should().BeNull();
        result.BucketLabel(Index(result, "B1", "length")).Should().Be("excl");
        var part = result.Parts.Single();
        part.Objects.Should().Be(2, "the dashed line and the boundary polyline's area");
        part.Missing.Should().Be(0, "measured from the boundary — and the scan's own unmeasured report is not listed again");
        result.Missing.Should().BeEmpty();
        part.Quantity.Should().BeApproximately(9 + 8, 1e-9);

        var wb = BoqRulesWorkbookWriter.Build(result, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out _);
        var cross = Sheet(wb, BoqRulesWorkbookWriter.SheetCross);
        Cell(cross, "B2").Should().EndWith(" · שטח ההצללה חושב מהפוליליין הסגור שבגבולה (שטח ההצללה לא נקרא מהשרטוט)");
        Cell(cross, "F2").Should().Be("8");
        cross.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == "G2").Value.Should().Be("ROUND(E2*'פרמטרים'!$C$4+N(F2),3)");
        Cell(cross, "H2").Should().Be("4");
        Cell(cross, "F1").Should().Be("הצללה מדודה (מ\"ר)");
        cross.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == "G3").Value.Should().Be("SUM(G2:G2)");
        var evaluated = MiniXlsx.EvaluateFormulas(wb);
        evaluated.TryGetValue(BoqRulesWorkbookWriter.SheetCross, "G2", out var g2).Should().BeTrue();
        g2.Number.Should().BeApproximately(17.0, 1e-9);
    }

    [Fact]
    public void AHatchWithNeitherAreaNorBoundaryIsMissingOnce_NotMeasuredNotZero()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(CrossingRules), Assembly(hatchArea: false, boundary: false, reportUnmeasured: true));
        var crossing = result.Crossings.Single();
        crossing.MissingHatches.Should().Be(1);
        result.Missing.Should().ContainSingle(m => m.Handle == "H1", "1.3.9 listed such a hatch twice");
        result.Parts.Single().Missing.Should().Be(1);

        var wb = BoqRulesWorkbookWriter.Build(result, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out _);
        var cross = Sheet(wb, BoqRulesWorkbookWriter.SheetCross);
        Cell(cross, "F2").Should().Be("לא נמדד");
        Cell(cross, "B2").Should().EndWith(" · שטח ההצללה לא נמדד — ראו \"חסרים\"");
        var missing = Sheet(wb, BoqRulesWorkbookWriter.SheetMissing);
        Cell(missing, "A2").Should().Be("10.32.1942", "the item number, not the internal line id");
        MiniXlsx.EvaluateFormulas(wb).TryGetValue(BoqRulesWorkbookWriter.SheetCross, "G2", out var g2).Should().BeTrue();
        g2.Number.Should().BeApproximately(9.0, 1e-9, "N(\"לא נמדד\") adds nothing");
    }

    [Fact]
    public void NoiseLinesAreNoCrossingAndWhiteLinesAreDashedOrSolidByTheirLinetypeScale()
    {
        var input = new BoqInputSet();
        input.Geometry.Add(new BoqEntityGeometry("M", "N1", "W", false, new[] { Seg(0, 100, 0.037, 100) }, null));
        input.Records.Add(Rec("W", "length", 0.037, "N1"));
        input.Geometry.Add(new BoqEntityGeometry("M", "S1", "W", false, new[] { Seg(0, 200, 7, 200) }, null) { LinetypeScale = 0.5 });
        input.Records.Add(Rec("W", "length", 7, "S1"));
        input.Geometry.Add(new BoqEntityGeometry("M", "S2", "W", false, new[] { Seg(0, 300, 7, 300) }, null) { LinetypeScale = 1.0 });
        input.Records.Add(Rec("W", "length", 7, "S2"));
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(CrossingRules), input);
        result.Roles["N1"].Should().Be(BoqCrosswalkGeometry.RoleNoise);
        result.Reasons[Index(result, "N1", "length")].Should().Be("noise");
        result.Crossings.Select(c => c.Kind).Should().Equal("קו לבן מקווקו בלבד (רוחב תקני)", "קו לבן בלבד (רוחב תקני)");
    }

    // ------------------------------------------------------------------ selection, count_of, REF, zero length

    private const string LineRules = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.3 (test)",
      "parameters": [{"id": "road_class", "label": "road", "value": 1}, {"id": "pole_len", "label": "pole", "value": 3.3}],
      "chapters": [{"id": "10.01", "title": "c"}],
      "sign_rules": {"poles_param": "pole_len"},
      "not_included": [{"id": "10.99", "title": "other works", "reason": "engineer's decision"}],
      "control_reasons": {"K": "{ref}: control only"},
      "boq": [
        {"id": "MH", "chapter": "10.01", "item": null, "unit": "u", "label": "manholes", "review": "other blocks on other layers",
         "parts": [{"label": "manholes (block shuha)", "src": ["A"], "layers": ["MNHL"], "blocks": ["shuha"], "kind": "count"}]},
        {"id": "P", "chapter": "10.01", "item": "10.01.2202", "unit": "m", "parts": [
          {"label": "poles", "src": ["A"], "block_layers": ["POLE"], "kind": "count_x", "param": "pole_len"}]},
        {"id": "C", "chapter": "10.01", "item": "10.01.5285", "unit": "u", "parts": [
          {"label": "caps = poles", "kind": "count_of", "line": "P"}]},
        {"id": "K", "chapter": "10.01", "item_urban": "10.01.1852", "item_rural": "10.01.1862", "unit": "m", "drawing_note": "one zero-length line",
         "parts": [{"label": "kerb", "src": ["A"], "layers": ["KERB"], "kind": "length"}],
         "control": {"label": "control lines", "src": ["A"], "layers": ["PAINT"], "kind": "length", "object_width_m": 0.1}}
      ]
    }
    """;

    private static BoqInputSet LineInputs()
    {
        var input = new BoqInputSet();
        input.Records.Add(new BoqRecord("A", "MNHL", "INSERT", "count", 1, "M1", false, "shuha", null));
        input.Records.Add(new BoqRecord("A", "MNHL", "INSERT", "count", 1, "M2", false, "shuha", null));
        input.Records.Add(new BoqRecord("A", "MNHL", "INSERT", "count", 1, "M3", false, "koltan", null));
        input.Records.Add(new BoqRecord("A", "MNHL", "LINE", "length", 4, "M4", false, "", null));
        foreach (var h in new[] { "P1", "P2", "P3" }) input.Records.Add(new BoqRecord("A", "POLE", "INSERT", "count", 1, h, false, "SIGN", null));
        input.Records.Add(new BoqRecord("A", "KERB", "LWPOLYLINE", "length", 20, "K1", true, "", null));
        input.Records.Add(new BoqRecord("A", "KERB", "LWPOLYLINE", "area", 3, "K1", true, "", null));
        input.Records.Add(new BoqRecord("A", "PAINT", "LINE", "length", 5, "Q1", false, "", null));
        input.ZeroLength.Add(new BoqZeroLength("A", "KERB", "Z0"));
        input.Unmeasured.Add(new BoqUnmeasured("A", "KERB", "U1", "failed", "check"));
        input.Unmeasured.Add(new BoqUnmeasured("A", "KERB", "U1", "failed", "check"));
        return input;
    }

    [Fact]
    public void ABlockFilterSelectsOnlyItsBlocks_OtherObjectsOnTheLayerStayVisible()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(LineRules), LineInputs());
        result.Lines.Single(l => l.Line.Id == "MH").Quantity.Should().Be(2);
        result.BucketLabel(Index(result, "M3", "count")).Should().Be("uncl", "another block on the layer is not this line's object");
        result.BucketLabel(Index(result, "M4", "length")).Should().Be("uncl", "the block filter comes before the count alternative rule");
    }

    [Fact]
    public void ACountOfLineCountsTheObjectsOfAnEarlierLine()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(LineRules), LineInputs());
        var caps = result.Parts.Single(p => p.LineId == "C");
        caps.Quantity.Should().Be(3);
        caps.Base.Should().Be(0, "as export_golden: the count_of part reads no object");
        caps.Objects.Should().Be(0);
        result.PolesCount.Should().Be(3);
        result.Lines.Single(l => l.Line.Id == "P").Quantity.Should().BeApproximately(3 * 3.3, 1e-9);
        FluentActions.Invoking(() => BoqRuleset.Parse(LineRules.Replace("\"line\": \"P\"", "\"line\": \"K\"")))
            .Should().Throw<InvalidDataException>().WithMessage("*count_of*earlier line*");
    }

    [Fact]
    public void EveryReasonNamesTheItemNumber_TheZeroLengthLineIsInformationAndMissingIsListedOnce()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(LineRules), LineInputs());
        result.Reasons[Index(result, "K1", "area")].Should().Be("10.01.1852/1862: פוליליין סגור נמדד באורך (היקף); חלופת השטח לא נכללת");
        result.Reasons[Index(result, "Q1", "length")].Should().Be("10.01.1852/1862: control only");
        var zero = result.ExcludedGroups().Last();
        zero.Reason.Should().Be("10.01.1852/1862: קו באורך אפס (נקודת התחלה = נקודת סיום, מזהה Z0) — אין אורך למדידה; אינו משנה את הכמות");
        (zero.Src, zero.Layer, zero.Kind, zero.Count, zero.Quantity).Should().Be(("A", "KERB", "length", 1, 0.0));
        result.Missing.Should().ContainSingle(m => m.Handle == "U1", "one row per file and handle");
        result.Lines.Single(l => l.Line.Id == "K").Missing.Should().Be(1, "the zero-length line is not missing; U1 once");
        result.Ref("MH").Should().Be("manholes");
    }

    [Fact]
    public void TheWorkbookPrintsTheV4Texts()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(LineRules), LineInputs());
        var wb = BoqRulesWorkbookWriter.Build(result, new BoqRulesWorkbookWriter.Context(new DateTime(2026, 9, 30)), out var mapped);
        mapped.Should().Be(3);
        var boq = Sheet(wb, BoqRulesWorkbookWriter.SheetBoq);
        // Unmapped line: no item text, the line's own label, the total formula on every row, review → "· לבדיקה".
        Cell(boq, "A6").Should().BeNull("an unmapped row has no item text (the reference writes nothing)");
        Cell(boq, "B6").Should().Be("manholes — סעיף מחירון לבחירה");
        boq.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == "F6").Value.Should().Be("IF(E6=\"\",\"\",ROUND(D6*E6,2))");
        Cell(boq, "G6").Should().Be("סעיף לבחירה · לבדיקה");
        Cell(boq, "H6").Should().Contain("לבדיקה: other blocks on other layers");
        // Poles line: independently counted signs versus poles; their association is not established.
        Cell(boq, "H7").Should().Contain("בשרטוט 0 תמרורים מול 3 עמודים — לא נבדק אילו עמודים נושאים שני תמרורים — לאישור");
        Cell(boq, "H9").Should().Contain("one zero-length line");
        // Not-included chapters after the lines, then the total over every line row.
        Cell(boq, "B11").Should().Be("פרקים מהדוגמה שלא נכללו בטיוטה זו");
        Cell(boq, "A12").Should().Be("10.99.0000");
        Cell(boq, "G12").Should().Be("לא נכלל");
        Cell(boq, "H12").Should().Be("engineer's decision");
        boq.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == "F14").Value.Should().Be("SUM(F6:F9)");

        var detail = Sheet(wb, BoqRulesWorkbookWriter.SheetDetail);
        Cell(detail, "A2").Should().Be("ללא סעיף");
        Cell(detail, "A5").Should().Be("10.01.1852/1862");
        Cell(detail, "D4").Should().Be("מספר העצמים בסעיף 10.01.2202");
        Cell(detail, "E4").Should().Be("—");
        detail.Rows.SelectMany(r => r.Cells).Single(c => c.Reference == "F4").Value.Should().Be("F3");
        Cell(detail, "J3").Should().Be("בשרטוט 0 תמרורים מול 3 עמודים — לא נבדק אילו עמודים נושאים שני תמרורים");
        Cell(detail, "J2").Should().BeNull("no notes: no empty text");

        var excluded = Sheet(wb, BoqRulesWorkbookWriter.SheetExcluded);
        var lastExcluded = excluded.Rows.Max(r => r.Index);
        Cell(excluded, "A" + lastExcluded).Should().StartWith("10.01.1852/1862: קו באורך אפס");
        var missing = Sheet(wb, BoqRulesWorkbookWriter.SheetMissing);
        missing.Rows.Should().HaveCount(2, "the header and U1 once");
        Cell(missing, "A2").Should().Be("10.01.1852/1862");
        var sources = Sheet(wb, BoqRulesWorkbookWriter.SheetSources);
        sources.Rows.SelectMany(r => r.Cells).Should().Contain(c => c.Value == "כללי החישוב (גרסה 2.3)");
        var parameters = Sheet(wb, BoqRulesWorkbookWriter.SheetParams);
        Cell(parameters, "D1").Should().Be("הערה / מצב");
    }

    [Fact]
    public void TheV4TextsOfTheEmbeddedRulesetAreTheReferences()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        rules.Crosswalk!.ReasonAtHatch.Should().Be("811: קו לבן במעבר שנספר לפי ההצללה שלו");
        rules.Crosswalk.MissingHatchWhat.Should().Be("הצללה ליד קו העצירה ששטחה לא הוחזר וגם לא חושב מהגבול");
        rules.Crosswalk.Note.Should().Contain("\"לא נמדד\" = הצללה ששטחה לא הוחזר מהשרטוט");
        rules.Ownership.Single(o => o.Id == "curbs_owned_by_gm").Reason.Should().StartWith("אבני שפה נלקחות מקובץ GM;");
        // v4.4 = the reference boq_inputs.py text (frozen-d) word for word.
        rules.Ownership.Single(o => o.Id == "bike_arrow_in_bike_unit").Reason.Should().Be(
            "לבדיקה: חץ כיוון בשביל האופניים (בלוק BL-ARW-W-Y) — בלוק נפרד, לא חלק מסמל האופניים: החץ של הסמל עצמו (arrow-D) נמצא 0.3–0.8 מ' "
            + "מהסמל, ואילו 54 חצים אלה במרחק 2.8 מ' ומעלה מסמל אופניים, ו-32 מהם ללא סמל אופניים ברדיוס 40 מ'. לא נספרו — לאישור אם לספור "
            + "ובאיזה שטח ליחידה (לפי 1.2 מ\"ר לחץ: 64.8 מ\"ר; הבלוק מכיל שתי הצללות, בקנה מידה 1 לאורך ו-0.7 לרוחב)");
        rules.Workbook.Title.Should().Contain("טיוטה v4 לפי כללים");
        rules.Workbook.Legend[0].Should().EndWith("לא נכלל = פרק מהדוגמה שלא נמדד בטיוטה זו, עם הסיבה.");
        rules.Workbook.Explanation.Should().NotContain(l => l.Text.StartsWith("מה השתנה", StringComparison.Ordinal),
            "the delivery-specific change block of the reference is never printed by the product");
        BoqRulesEngine.ControlReason(rules, rules.Line("M4")!, 1)
            .Should().Be("51.32.2640: בקרה בלבד — הכמות היא סכום סעיפי אבני השפה (החלטת נטלי 29.09.2026)");
    }

    [Fact]
    public void TheOneObjectOnceSheetSaysHowEachObjectIsDrawn()
    {
        BoqRulesWorkbookWriter.DrawnAs(new Dictionary<int, double> { [1] = 40.2, [2] = 1.9, [3] = 500, [5] = 300.3, [4] = 0.1 })
            .Should().Be(" | אורך העצם לפי מספר הקווים שצוירו באותו מקום: קו אחד: 40 מ'; 2 קווים: 2 מ'; 3 ומעלה: 800 מ'");
        BoqRulesWorkbookWriter.DrawnAs(new Dictionary<int, double> { [2] = 0.3 })
            .Should().Be(" | אורך העצם לפי מספר הקווים שצוירו באותו מקום: ");
    }

    [Fact]
    public void TheLiveAdapterReadsTheLinetypeScaleTheHatchBoundaryAndTheZeroLengthProof()
    {
        var rules = BoqRuleset.Parse(CrossingRules);
        NeutralQuantityRecord Neutral(string handle, string etype, string kind, double value, Dictionary<string, string> parameters) => new()
        {
            RecordId = $"q-{handle}-{kind}", ProjectProfileId = "p", RunId = "run",
            Source = new QuantitySource { Drawing = "x-M-.dwg", DrawingHash = new string('c', 64), Handle = handle, EntityType = etype, Layer = "W" },
            Measurement = new QuantityMeasurement
            {
                Kind = kind, Method = kind == "area" ? "hatch-area" : "polyline-length", RawValue = value, Unit = kind == "area" ? "מ\"ר" : "מטר",
                GeometryEvidence = new[] { 0.0, 0.0, 4.0, 2.0 }, Parameters = parameters,
            },
        };
        var polyline = new Dictionary<string, string>
        {
            ["cad_entity_database_insunits"] = "Meters",
            [QuantityGeometryEvidence.LinetypeScaleKey] = "0.5",
            [QuantityGeometryEvidence.SegmentsKey] = "0,0;4,0;4,2;0,2",
            [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.StatusComplete,
        };
        var hatch = new Dictionary<string, string>
        {
            ["cad_entity_database_insunits"] = "Meters",
            [QuantityGeometryEvidence.HatchCentroidKey] = "2,1",
            [QuantityGeometryEvidence.HatchBoundaryPointsKey] = "0,0;4,0;4,0;4,2;4,2;0,2;0,2;0,0",
        };
        var records = new List<NeutralQuantityRecord>
        {
            Neutral("B1", "LWPOLYLINE", "length", 12, polyline),
            Neutral("H1", "HATCH", "area", 8, hatch),
        };
        var zero = ZeroLengthGeometryProof.CreateFailure(0, ZeroLengthGeometryProof.GeometryKind.Line,
            new[] { new ZeroLengthGeometryProof.Vertex(1, 1, 0), new ZeroLengthGeometryProof.Vertex(1, 1, 0) }, true, true, "p",
            new ProvenanceRef { SourceKind = "drawing", SourceHandle = "10A971", EntityType = "Line", Layer = "W", MeasurementMethod = "line-length" })!;
        var failed = new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error, Title = "failed",
            SourceRefs = { new ProvenanceRef { SourceKind = "drawing", SourceHandle = "L9", EntityType = "Line", Layer = "W", MeasurementMethod = "line-length" } },
        };
        var input = BoqNeutralRecordAdapter.Build(rules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("M", "run-m", @"C:\p\x-M-.dwg", new string('c', 64), records, new[] { zero, failed }) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        var line = input.Geometry.Single(g => g.Handle == "B1");
        line.LinetypeScale.Should().Be(0.5);
        line.Vertices.Should().Equal(new BoqPoint(0, 0), new BoqPoint(4, 0), new BoqPoint(4, 2), new BoqPoint(0, 2));
        input.Geometry.Single(g => g.Handle == "H1").HatchBoundaryPoints.Should().HaveCount(8);
        input.ZeroLength.Should().ContainSingle(z => z.Handle == "10A971" && z.Layer == "W");
        input.Unmeasured.Should().ContainSingle(u => u.Handle == "L9").Which.What.Should().Be("עצם שלא נמדד (אורך)",
            "Hebrew only — 1.3.9 printed \"(line-length)\"");
        input.Unmeasured.Should().NotContain(u => u.Handle == "10A971");
        QuantityGeometryEvidence.IsGeometryKey(QuantityGeometryEvidence.HatchBoundaryPointsKey).Should().BeTrue();
    }

    [Fact]
    public void AFailedHatchKeepsItsDiagnosticBoundaryPoints()
    {
        var json = """
        {"Sources":[{"Source":{"source_handle":"8AB16","layer":"W","xref_path":null,"entity_type":"Hatch"},
          "Boundary":{"Header":"style=Outer; normal=0,0,1; elevation=0","Loops":[{"Index":0,"Evidence":{"Kind":"edge-list","Items":[
            "line=0,0 -> 4,0","line=4,0 -> 4,2","line=4,2 -> 0,2","line=0,2 -> 0,0"]}}]}}]}
        """;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var hatch = BoqNeutralRecordAdapter.FailedHatchCentroids(document.RootElement).Single();
        hatch.Points.Should().HaveCount(8);
        hatch.Points.Distinct().Should().BeEquivalentTo(new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 2.0), (0.0, 2.0) });
    }
}
