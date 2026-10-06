using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>Unit rules of the BoQ rules engine on small synthetic rulesets (no project data).</summary>
public sealed class BoqRulesV2EngineTests
{
    [Fact]
    public void SourceSelectionNoticeIsCarriedIntoMultiDrawingRuleOutputPerSource()
    {
        const string notice = "SYNTHETIC: background.dwg לא נמדד; מספר ישויות מוחרגות לא ידוע";
        var input = BoqNeutralRecordAdapter.Build(BoqRuleset.LoadEmbedded6422(), new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("GM", "SIMULATION-SCOPE", @"C:\simulation\host.dwg", new string('a', 64),
                Array.Empty<NeutralQuantityRecord>(), new[] { new DeliveryFinding
                {
                    Code = EstimateSourceSelectionPolicy.ExcludedScopeCode, Domain = "estimate", Severity = FindingSeverity.Info,
                    Title = "מקורות", Message = notice,
                } }) { Units = ScanUnitEvidence.Legacy("SYNTHETIC empty scan; no native evidence") },
        });
        input.Warnings.Should().Contain(w => w.Contains(notice) && w.Contains("SIMULATION-SCOPE") && w.Contains("מקורות נפרדים"));
        BoqRulesEngine.Run(BoqRuleset.LoadEmbedded6422(), input).Warnings.Should().Contain(w => w.Contains(notice));
    }

    // A small ruleset in the same schema; layer names are synthetic.
    private const string RulesJson = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST",
      "parameters": [
        {"id": "road_class", "label": "road", "value": 1},
        {"id": "stop_w", "label": "stop width", "value": 0.3},
        {"id": "cross_w", "label": "standard crossing width", "value": 3.0},
        {"id": "fill", "label": "painted share", "value": 0.5}
      ],
      "sign_sizes_cm": {
        "circle_d": {"301": [50, 60, 80]},
        "triangle_a": {"302": [65, 90, 120]},
        "rect_wh": {"505": [[50, 60], [50, 60], [70, 90]]}
      },
      "chapters": [{"id": "10.01", "title": "c"}],
      "existing_layers_prefix": "OLD_",
      "layer_sets": {"kerbs": ["KERB"]},
      "source_roles": [{"id": "A", "file_pattern": "-A-"}, {"id": "B", "file_pattern": "-B-"}, {"id": "M", "file_pattern": "-M-"}],
      "ownership": [
        {"id": "b_copy", "src": ["B"], "duplicate_of": {"owner": "A", "same_handle": true}, "reason": "copy of A"},
        {"id": "old", "layer_prefix": "OLD_", "reason": "existing"},
        {"id": "kerb_geo", "src": ["M"], "layer_set": "kerbs", "duplicate_of": {"owner": "A", "same_handle": false}, "reason": "geometric copy"},
        {"id": "kerb_owner", "src": ["B"], "layer_set": "kerbs", "reason": "kerbs belong to A, not {src}"}
      ],
      "crosswalk_geometry": {
        "src": ["M"], "yellow_layers": ["Y"], "white_layers": ["W", "W250"], "hatch_layers": ["W"],
        "standard_width_by_layer": {"W250": 2.5}, "default_standard_width": 3.0,
        "standard_width_param": "cross_w", "fill_param": "fill"
      },
      "boq": [
        {"id": "K", "chapter": "10.01", "item": "10.01.0001", "unit": "m", "parts": [{"label": "kerb", "src": ["A", "B", "M"], "layers": ["KERB"], "kind": "length"}]},
        {"id": "P", "chapter": "10.01", "item": null, "unit": "m2", "parts": [{"label": "paving", "src": ["A"], "layers": ["PAVE"], "kind": "area"}]},
        {"id": "N", "chapter": "10.01", "item": null, "unit": "u", "parts": [{"label": "posts", "src": ["A"], "layers": ["POST"], "kind": "count"}]},
        {"id": "S", "chapter": "10.01", "item_urban": "10.01.0101", "item_rural": "10.01.0102", "unit": "m2", "parts": [
          {"label": "stop", "src": ["M"], "layers": ["STOP"], "kind": "length", "param": "stop_w", "max_len": 30},
          {"label": "crossings", "src": ["M"], "layers": ["Y", "W", "W250"], "kind": "crosswalk_geo"},
          {"label": "signs", "src": ["M"], "signs": ["301", "302", "505"], "kind": "sign_area"}
        ]}
      ]
    }
    """;

    private static BoqRuleset Rules() => BoqRuleset.Parse(RulesJson);

    private static BoqRecord Rec(string src, string layer, string kind, double qty, string handle,
        string etype = "LINE", bool closed = false, string block = "", double[]? bbox = null) =>
        new(src, layer, etype, kind, qty, handle, closed, block, bbox);

    private static BoqEntityGeometry Line(string handle, string layer, double x1, double y1, double x2, double y2) =>
        new("M", handle, layer, false, new[] { new BoqSegment(new BoqPoint(x1, y1), new BoqPoint(x2, y2)) }, null);

    private static BoqEntityGeometry Hatch(string handle, double x, double y) =>
        new("M", handle, "W", true, Array.Empty<BoqSegment>(), new BoqPoint(x, y));

    private static BoqEngineResult Run(BoqInputSet input, BoqRuleset? rules = null) => BoqRulesEngine.Run(rules ?? Rules(), input);

    private static string Bucket(BoqEngineResult result, string handle, string kind = "length")
    {
        var index = result.Input.Records.FindIndex(r => r.Handle == handle && r.Kind == kind);
        return result.BucketLabel(index);
    }

    [Fact]
    public void EmbeddedRulesetLoadsWithEveryLaneSectionAndTheReferenceLines()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        rules.Schema.Should().Be("mahod-boq-rules/2");
        rules.Project.Should().Be("6422");
        // v4: Natali's order — ascending items per chapter, unpriced lines after the priced ones.
        rules.Lines.Select(l => l.Id).Should().Equal("D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "C1", "C3", "C4", "C2",
            "S2", "S3", "S1", "S4", "M1", "M2", "M3", "M4", "G1", "G2", "G3", "A1", "A2", "A3", "A4", "A5", "A6", "N1");
        rules.SourceRoles.Select(r => r.Id).Should().Equal("HA", "DR", "GM", "SM");
        rules.Ownership.Should().HaveCount(8); // 2.2: the bike-symbol arrow (arrow-D) belongs to the 804 unit
        rules.Crosswalk.Should().NotBeNull();
        rules.Pricebook.Items.Should().ContainKeys("51.06.0010", "51.32.1852", "51.32.1862", "51.32.1942",
            "51.05.0755", "51.05.2653", "51.31.5285"); // v4: ק"80, pipe video, pole caps
        rules.SignSizes.Knows("505").Should().BeTrue();
        // v4 keys: not-included chapters, count_of, blocks, alt, review / drawing_note, the lane texts.
        rules.NotIncluded.Select(c => c.Id).Should().Equal("51.01", "51.02", "51.03", "51.04", "51.34", "51.36", "51.37", "51.99");
        rules.Line("S4")!.Parts.Single().Kind.Should().Be("count_of");
        rules.Line("S4")!.Parts.Single().Of.Should().Be("S3");
        rules.Line("D6")!.Parts.Single().Blocks.Should().Equal("shuha140_140");
        rules.Line("D6")!.Review.Should().NotBeNullOrWhiteSpace();
        rules.Line("C2")!.DrawingNote.Should().StartWith("הערת שרטוט:");
        rules.AllParts.Single(p => p.Alt != null).Alt!.Value.Should().Be(4.5);
        rules.Ref("M1").Should().Be("51.32.1852/1862");
        rules.Ref("D5").Should().Be("צינור ניקוז ק\"100 (שכבה DR-PPIPE-100)");
        rules.ControlReasons.Should().ContainKey("M4");
        rules.SignTexts.PolesParam.Should().Be("pole_len");
        rules.SignTexts.CombinedLabels.Should().ContainSingle(c => c.Number == "505" && c.Label == "505/506");
        rules.Workbook.ExplanationComplete.Should().BeTrue();
        rules.Pricebook.DescriptionFixes.Should().Contain(("קוניתוב", "קו ניתוב"));
        rules.Pricebook.Describe(rules.Pricebook.Items["51.05.0755"]).Should().Contain("27 בקוטר 80").And.Contain("למים בעלי");
    }

    [Fact]
    public void AReferenceFormatRulesetWithoutTheLaneSectionsStillLoads()
    {
        using var doc = JsonDocument.Parse(RulesJson);
        var stripped = doc.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("ownership" or "crosswalk_geometry" or "source_roles" or "layer_sets"))
            .ToDictionary(p => p.Name, p => p.Value);
        var rules = BoqRuleset.Parse(JsonSerializer.Serialize(stripped));
        rules.Crosswalk.Should().BeNull();
        rules.Ownership.Should().ContainSingle().Which.LayerPrefix.Should().Be("OLD_", "the schema's existing-layer prefix still applies");
        var result = BoqRulesEngine.Run(rules, new BoqInputSet());
        result.Warnings.Should().Contain(w => w.Contains("crosswalk_geometry"));
    }

    [Fact]
    public void AnUnknownPartKindOrParameterIsRefused()
    {
        var badKind = RulesJson.Replace("\"kind\": \"count\"", "\"kind\": \"volume\"");
        FluentActions.Invoking(() => BoqRuleset.Parse(badKind)).Should().Throw<InvalidDataException>().WithMessage("*volume*");
        var badParam = RulesJson.Replace("\"param\": \"stop_w\"", "\"param\": \"nope\"");
        FluentActions.Invoking(() => BoqRuleset.Parse(badParam)).Should().Throw<InvalidDataException>().WithMessage("*nope*");
    }

    [Fact]
    public void ACopyOfAnOwnerObjectWithTheSameHandleAndGeometryIsCountedOnceByTheOwner()
    {
        var bbox = new[] { 100.04, 200.0, 110.0, 200.0 };
        var input = new BoqInputSet();
        input.Records.Add(Rec("A", "KERB", "length", 10.04, "H1", bbox: bbox));
        input.Records.Add(Rec("B", "KERB", "length", 10.01, "H1", bbox: new[] { 100.0, 200.02, 110.0, 200.0 })); // same after 0.1 rounding
        input.Records.Add(Rec("B", "PAVE", "length", 5.0, "H2", bbox: bbox));  // same handle? no → not a copy
        input.Records.Add(Rec("B", "KERB", "length", 10.04, "H9", bbox: bbox)); // same geometry, different handle → not a copy
        var result = Run(input);
        Bucket(result, "H1").Should().Be("item:K:0");
        result.BucketLabel(1).Should().Be("excl");
        // v4.1 (WI4-01): the copy says what happened to the owner's copy (here: counted in line K, item 10.01.0001).
        result.Reasons[1].Should().Be($"עותק ב-DR של עצם מקובץ GM (אותו מזהה ואותה גאומטריה) — לא נספר מ-DR; העותק ב-GM נספר בסעיף {result.Ref("K")}");
        Bucket(result, "H2").Should().Be("uncl");
        // H9 is not the owner's handle: the copy rule does not apply; the kerb-owner rule excludes it with its role named.
        Bucket(result, "H9").Should().Be("excl");
        result.Reasons[3].Should().Be("kerbs belong to A, not B");
        result.Lines.Single(l => l.Line.Id == "K").Quantity.Should().BeApproximately(10.04, 1e-9);
    }

    [Fact]
    public void AGeometricCopyWithoutTheSameHandleIsExcludedWhenTheRuleSaysSo()
    {
        var bbox = new[] { 1.0, 2.0, 3.0, 4.0 };
        var input = new BoqInputSet();
        input.Records.Add(Rec("A", "KERB", "length", 7.0, "A1", bbox: bbox));
        input.Records.Add(Rec("M", "KERB", "length", 7.02, "M1", bbox: bbox));
        input.Records.Add(Rec("M", "KERB", "length", 9.0, "M2", bbox: bbox));
        var result = Run(input);
        Bucket(result, "M1").Should().Be("excl");
        Bucket(result, "M2").Should().Be("item:K:0", "a different length is a different kerb");
        result.Lines.Single(l => l.Line.Id == "K").Quantity.Should().BeApproximately(16.0, 1e-9);
    }

    [Fact]
    public void ExistingLayersAreNeverNewWork()
    {
        var input = new BoqInputSet();
        input.Records.Add(Rec("A", "OLD_KERB", "length", 50, "E1"));
        var result = Run(input);
        result.BucketLabel(0).Should().Be("excl");
        result.Reasons[0].Should().Be("existing");
    }

    [Fact]
    public void AClosedPolylineCountsOnceWithTheMeasureTheLineAsks()
    {
        var input = new BoqInputSet();
        // Kerb (length line): perimeter taken, its area alternative excluded.
        input.Records.Add(Rec("A", "KERB", "area", 12.0, "C1", "POLYLINE", closed: true));
        input.Records.Add(Rec("A", "KERB", "length", 14.0, "C1", "POLYLINE", closed: true));
        // Paving (area line): area taken, its perimeter alternative excluded.
        input.Records.Add(Rec("A", "PAVE", "area", 30.0, "C2", "POLYLINE", closed: true));
        input.Records.Add(Rec("A", "PAVE", "length", 22.0, "C2", "POLYLINE", closed: true));
        // Count line: a line on the count layer is not a count.
        input.Records.Add(Rec("A", "POST", "count", 1, "C3", "BLOCKREFERENCE"));
        input.Records.Add(Rec("A", "POST", "length", 3.0, "C4"));
        var result = Run(input);
        Bucket(result, "C1", "length").Should().Be("item:K:0");
        Bucket(result, "C1", "area").Should().Be("excl");
        result.Reasons[0].Should().StartWith(result.Ref("K") + ":").And.Contain("חלופת השטח");
        Bucket(result, "C2", "area").Should().Be("item:P:0");
        Bucket(result, "C2", "length").Should().Be("excl");
        Bucket(result, "C3", "count").Should().Be("item:N:0");
        Bucket(result, "C4").Should().Be("excl");
        result.Lines.Single(l => l.Line.Id == "K").Quantity.Should().Be(14.0);
        result.Lines.Single(l => l.Line.Id == "P").Quantity.Should().Be(30.0);
        result.Lines.Single(l => l.Line.Id == "N").Quantity.Should().Be(1.0);
    }

    [Fact]
    public void ObjectsLongerThanThePartLimitAreNotStopLines()
    {
        var input = new BoqInputSet();
        input.Records.Add(Rec("M", "STOP", "length", 12.0, "S1"));
        input.Records.Add(Rec("M", "STOP", "length", 40.0, "S2"));
        input.Geometry.Add(Line("S1", "STOP", 0, 0, 12, 0));
        input.Geometry.Add(Line("S2", "STOP", 0, 10, 40, 10));
        var result = Run(input);
        Bucket(result, "S1").Should().Be("item:S:0");
        Bucket(result, "S2").Should().Be("excl");
        result.LongObjects.Should().ContainKey(("M", "S2"));
        result.Parts.Single(p => p.LineId == "S" && p.Index == 0).Quantity.Should().BeApproximately(12.0 * 0.3, 1e-9);
    }

    [Fact]
    public void ABoundedGeometryUsesTheNativeLengthForTheLongObjectRule()
    {
        var input = new BoqInputSet();
        input.Records.Add(Rec("M", "STOP", "length", 45.0, "S3"));
        input.Geometry.Add(new BoqEntityGeometry("M", "S3", "STOP", false, Array.Empty<BoqSegment>(), null,
            SegmentsComplete: false, FallbackLength: 45.0));
        Bucket(Run(input), "S3").Should().Be("excl");
    }

    [Fact]
    public void CrossingUnion_YellowEdgesWhiteOnTopHatchStripesAndLoneWhiteLinesAreEachCountedOnce()
    {
        var input = new BoqInputSet();
        // 1) Yellow edge lines 3 m apart (a measured crossing 8 m long) with a white dashed line drawn on it.
        input.Geometry.Add(Line("Y1", "Y", 0, 0, 8, 0));
        input.Geometry.Add(Line("Y2", "Y", 0, 3, 8, 3));
        input.Geometry.Add(Line("W1", "W", 0, 1.5, 8, 1.5));
        // 2) A hatched crossing far away: three stripes and a white line at them.
        input.Geometry.Add(Hatch("H1", 100, 0));
        input.Geometry.Add(Hatch("H2", 101, 0));
        input.Geometry.Add(Hatch("H3", 102, 0));
        input.Geometry.Add(Line("W2", "W", 99, 1.5, 103, 1.5));
        // 3) A white-only crossing (standard width) and a 2.5 m-standard one.
        input.Geometry.Add(Line("W3", "W", 200, 0, 207, 0));
        input.Geometry.Add(Line("W4", "W250", 300, 0, 306, 0));
        // 4) A lone yellow line (no parallel partner) behaves like a white single line.
        input.Geometry.Add(Line("Y9", "Y", 400, 0, 405, 0));
        foreach (var g in input.Geometry.Where(x => !x.IsHatch).ToList())
            input.Records.Add(Rec("M", g.Layer!, "length", g.Segments.Sum(s => s.Length), g.Handle));
        foreach (var h in new[] { "H1", "H2" }) input.Records.Add(Rec("M", "W", "area", 1.5, h, "HATCH"));
        input.StrictHatchRecoveries["H3"] = 1.25; // its native area failed; strictly recovered

        var result = Run(input);
        result.Crossings.Select(c => c.WidthSource).Should().Equal("measured", "hatch", "standard", "standard", "standard");
        var measured = result.Crossings[0];
        measured.Length!.Value.Should().BeApproximately(8.0, 1e-9);
        measured.Width!.Value.Should().BeApproximately(3.0, 1e-9);
        measured.Handles.Should().Equal("Y1", "Y2");
        // Cluster order is the reference's LIFO exploration order (H1, then the last pushed neighbour H3, then H2).
        result.Crossings[1].HatchHandles.Should().Equal("H1", "H3", "H2");
        result.Crossings[1].Handles.Should().Equal("H1", "H2", "H3", "W2");
        // v4: without a dashed line near them the hatches are a hatch-only crossing; n = every member object.
        result.Crossings[1].Kind.Should().Be("הצללה בלבד (שטח מדוד)");
        result.Crossings[1].N.Should().Be(4);
        result.Crossings[1].HatchM2!.Value.Should().BeApproximately(1.5 + 1.5 + 1.25, 1e-9);
        result.Crossings.Where(c => c.WidthSource == "standard").Select(c => c.Width).Should().Equal(3.0, 2.5, 3.0);
        // No linetype scale is known here: a white-only crossing is "קו לבן בלבד", not "מקווקו"; a lone yellow line (v4.3, as the
        // reference sm_geometry) is named as such — both at the standard width, one object each.
        result.Crossings.Where(c => c.WidthSource == "standard").Should().OnlyContain(c => c.N == 1);
        result.Crossings.Where(c => c.WidthSource == "standard").Select(c => c.Kind).Should().Equal(
            "קו לבן בלבד (רוחב תקני)", "קו לבן בלבד (רוחב תקני)", "קו צהוב בודד (רוחב תקני)");

        Bucket(result, "W1").Should().Be("excl", "a white line on a yellow crossing is the same crossing");
        Bucket(result, "W2").Should().Be("excl", "a white line at hatch stripes belongs to that hatched crossing");
        Bucket(result, "Y1").Should().Be("item:S:1");
        Bucket(result, "W3").Should().Be("item:S:1");
        Bucket(result, "Y9").Should().Be("item:S:1");
        Bucket(result, "H1", "area").Should().Be("item:S:1");

        // Quantity: 8×3×0.5 + (1.5+1.5+1.25) + 7×3×0.5 + 6×3×0.5 (the parameter width, as the reference) + 5×3×0.5.
        var crossings = result.Parts.Single(p => p.Part.Kind == "crosswalk_geo");
        crossings.Quantity.Should().BeApproximately(12 + 4.25 + 10.5 + 9 + 7.5, 1e-9);
        crossings.Missing.Should().Be(0);
    }

    [Fact]
    public void AHatchStripeWithNeitherAreaNorRecoveryIsMissingNotZero()
    {
        var input = new BoqInputSet();
        input.Geometry.Add(Hatch("H1", 0, 0));
        input.Geometry.Add(Hatch("H2", 1, 0));
        input.Records.Add(Rec("M", "W", "area", 1.5, "H1", "HATCH"));
        input.StrictHatchRecoveries["H2"] = null;
        var result = Run(input);
        var part = result.Parts.Single(p => p.Part.Kind == "crosswalk_geo");
        part.Missing.Should().Be(1);
        result.Missing.Should().ContainSingle(m => m.Handle == "H2" && m.LineId == "S");
        part.Quantity.Should().BeApproximately(1.5, 1e-9);
    }

    [Theory]
    [InlineData(1, 0.19634954084936207, 0.18294786654946266, 0.30)]
    [InlineData(2, 0.28274333882308139, 0.35074028853269765, 0.30)]
    [InlineData(3, 0.50265482457436690, 0.62353829072479582, 0.63)]
    public void SignAreasFollowTheRoadClassOfTableThree(int roadClass, double circle, double triangle, double rect)
    {
        // circle π(d/200)², triangle √3/4·(a/100)², rectangle w·h/10000 (table 3 sizes in cm).
        var rules = Rules();
        rules.SignSizes.AreaM2("301", roadClass).Should().BeApproximately(circle, 1e-9);
        rules.SignSizes.AreaM2("302", roadClass).Should().BeApproximately(triangle, 1e-9);
        rules.SignSizes.AreaM2("505", roadClass).Should().BeApproximately(rect, 1e-9);

        var input = new BoqInputSet();
        input.Records.Add(Rec("M", "SIGNS", "count", 1, "G1", "BLOCKREFERENCE", block: "SIGN-301"));
        input.Records.Add(Rec("M", "SIGNS", "count", 1, "G2", "BLOCKREFERENCE", block: "302_A"));
        input.Records.Add(Rec("M", "SIGNS", "count", 1, "G3", "BLOCKREFERENCE", block: "505"));
        input.Records.Add(Rec("M", "SIGNS", "count", 1, "G4", "BLOCKREFERENCE", block: "811")); // a marking number, never a sign
        input.Records.Add(Rec("M", "SIGNS", "count", 1, "G5", "BLOCKREFERENCE", block: "3011"));  // four digits: no sign number
        var result = BoqRulesEngine.Run(rules, input, new Dictionary<string, double> { ["road_class"] = roadClass });
        var signs = result.Parts.Single(p => p.Part.Kind == "sign_area");
        signs.Objects.Should().Be(3);
        signs.Quantity.Should().BeApproximately(circle + triangle + rect, 1e-9);
        result.SignCounts.Keys.Should().Equal("301", "302", "505");
        result.Lines.Single(l => l.Line.Id == "S").Item.Should().Be(roadClass == 1 ? "10.01.0101" : "10.01.0102");
        Bucket(result, "G4", "count").Should().Be("uncl");
        Bucket(result, "G5", "count").Should().Be("uncl");
    }

    [Theory]
    [InlineData(203647.15, 203647.1)] // stored as 203647.1499999999941…: Math.Round(x, 1) would give 203647.2
    [InlineData(0.15, 0.1)]
    [InlineData(0.25, 0.2)]             // an exact binary tie: half to even
    [InlineData(0.35, 0.3)]
    [InlineData(0.45, 0.5)]
    [InlineData(-0.05, -0.1)]
    [InlineData(649233.5, 649233.5)]
    [InlineData(12.0, 12.0)]
    public void DuplicateKeysRoundExactlyLikeTheReferencePythonRound(double value, double expected) =>
        BoqRulesEngine.PythonRound1(value).Should().Be(expected);

    [Fact]
    public void AKnownUnmeasuredObjectIsMissingInThePartThatOwnsItsLayer()
    {
        var input = new BoqInputSet();
        input.Unmeasured.Add(new BoqUnmeasured("A", "KERB", "Z1", "zero length", "check"));
        input.Unmeasured.Add(new BoqUnmeasured("A", "NOWHERE", "Z2", "zero length", "check"));
        var result = Run(input);
        result.Lines.Single(l => l.Line.Id == "K").Missing.Should().Be(1);
        result.Missing.Select(m => m.LineId).Should().Equal("K", "—");
    }

    [Fact]
    public void GeometryEvidenceRoundTripsAndStaysOutOfSummariesAndDecisionScopes()
    {
        var text = QuantityGeometryEvidence.FormatVertices(new List<(double X, double Y)> { (204168.12341, 649233.5), (1, -2.25) });
        text.Should().Be("204168.12341,649233.5;1,-2.25");
        QuantityGeometryEvidence.TryParseVertices(text, out var vertices).Should().BeTrue();
        vertices.Should().HaveCount(2);
        QuantityGeometryEvidence.IsGeometryKey(QuantityGeometryEvidence.SegmentsKey).Should().BeTrue();
        QuantityGeometryEvidence.IsGeometryKey(QuantityGeometryEvidence.HatchStatusKey).Should().BeTrue();
        QuantityGeometryEvidence.IsGeometryKey("cad_entity_linetype").Should().BeFalse();

        var measurement = new QuantityMeasurement { Kind = "length", Method = "line-length", RawValue = 1, Unit = "m" };
        measurement.Parameters["cad_entity_linetype"] = "DASHED";
        QuantityCadMetadataPolicy.AppendEvidence(measurement, new Dictionary<string, string>
        {
            [QuantityGeometryEvidence.RawSegments] = text,
            [QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.StatusComplete,
        });
        QuantityCadMetadataPolicy.Summarize(new[] { measurement }).Select(f => f.Key).Should().Equal("cad_entity_linetype");

        NeutralQuantityRecord Record(bool withGeometry)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["cad_entity_linetype"] = "DASHED" };
            if (withGeometry) parameters[QuantityGeometryEvidence.SegmentsKey] = text;
            return new NeutralQuantityRecord
            {
                RecordId = "r1", ProjectProfileId = "p", RunId = "run",
                Source = new QuantitySource
                {
                    Drawing = "x.dwg", DrawingPath = @"C:\x.dwg", DrawingHash = new string('a', 64),
                    Handle = "1F", EntityType = "LINE", Layer = "L",
                },
                Measurement = new QuantityMeasurement { Kind = "length", Method = "line-length", RawValue = 1, Unit = "m", Parameters = parameters },
                Classification = new QuantityClassification { RuleKey = "layer:L|length" },
            };
        }
        var hash = new string('a', 64);
        SemanticHintPolicy.Capture("p", @"C:\x.dwg", hash, "layer:L|length", new[] { Record(true) })
            .Should().Be(SemanticHintPolicy.Capture("p", @"C:\x.dwg", hash, "layer:L|length", new[] { Record(false) }),
                "adding bounded geometry to a scan must not invalidate a saved decision");
    }

    [Fact]
    public void NeutralRecordsBecomeEngineInputsLikeTheReferenceNormalisation()
    {
        var rules = BoqRuleset.Parse(RulesJson.Replace("\"kind\": \"area\"}]}", "\"kind\": \"area\"}]},\n" +
            "{\"id\": \"H\", \"chapter\": \"10.01\", \"item\": null, \"unit\": \"m2\", \"parts\": [{\"label\": \"hatch\", \"src\": [\"A\"], \"layers\": [\"HATCHES\"], \"kind\": \"hatch\"}]}"));
        NeutralQuantityRecord R(string handle, string etype, string kind, double value, string layer = "KERB", string? xref = null,
            string method = "line-length", Dictionary<string, string>? parameters = null) => new()
        {
            RecordId = $"q-{handle}-{kind}", ProjectProfileId = "p", RunId = "run",
            Source = new QuantitySource { Drawing = "x-A-.dwg", DrawingHash = new string('b', 64), Handle = handle, EntityType = etype, Layer = layer, Xref = xref },
            Measurement = new QuantityMeasurement
            {
                Kind = kind, Method = method, RawValue = value, Unit = kind == "area" ? "m2" : "m",
                GeometryEvidence = new[] { 1.0, 2.0, 3.0, 4.0 }, Parameters = parameters ?? new Dictionary<string, string>(),
            },
        };
        var geometry = new Dictionary<string, string>
        {
            ["cad_entity_database_insunits"] = "Millimeters",
            [QuantityGeometryEvidence.SegmentsKey] = "0,0;12000,0",
            [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.StatusComplete,
        };
        var records = new List<NeutralQuantityRecord>
        {
            R("10", "POLYLINE", "area", 5, "KERB"), R("10", "POLYLINE", "length", 9, "KERB"),
            R("11", "LINE", "length", 12, "STOP", parameters: geometry),
            R("12", "HATCH", "area", 100, "HATCHES", method: "hatch-area"),
            R("13", "HATCH", "area", 40, "HATCHES", method: "hatch-linear-boundary-area"),
            R("14", "LINE", "length", 3, "KERB", xref: "XR"),
            R("15", "BLOCKREFERENCE", "count", 1, "POST", parameters: new() { ["block_name"] = "*U7", ["cad_block_name_effective"] = "SIGN-301" }),
        };
        var findings = new List<DeliveryFinding>
        {
            new()
            {
                Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error, Title = "failed",
                SourceRefs =
                {
                    new ProvenanceRef { SourceKind = "drawing", SourceHandle = "20", EntityType = "Hatch", Layer = "HATCHES" },
                    new ProvenanceRef { SourceKind = "drawing", SourceHandle = "21", EntityType = "Line", Layer = "KERB", MeasurementMethod = "line-length" },
                },
            },
        };
        var input = BoqNeutralRecordAdapter.Build(rules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("A", "run-a", @"C:\p\x-A-.dwg", new string('b', 64), records, findings) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        input.Records.Select(r => r.Handle).Should().Equal(new[] { "10", "10", "11", "15" },
            "the XREF record and the hatch-total hatches are not records");
        input.Records.Where(r => r.Handle == "10").Should().OnlyContain(r => r.Closed);
        input.Records.Single(r => r.Handle == "15").Block.Should().Be("SIGN-301");
        input.Records.Single(r => r.Handle == "11").Bbox.Should().Equal(1.0, 2.0, 3.0, 4.0);
        var total = input.HatchLayers.Should().ContainSingle().Subject;
        total.Should().BeEquivalentTo(new { Src = "A", Layer = "HATCHES", DirectCount = 1, DirectArea = 100m, RecoveredCount = 1, RecoveredArea = 40m, UnresolvedCount = 1 },
            options => options.ExcludingMissingMembers());
        input.Unmeasured.Should().ContainSingle(u => u.Handle == "21" && u.Layer == "KERB");
        // Geometry is not collected for role A here (no crossing or max_len part reads A).
        input.Geometry.Should().BeEmpty();

        var mRules = Rules();
        var mInput = BoqNeutralRecordAdapter.Build(mRules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("M", "run-m", @"C:\p\x-M-.dwg", new string('c', 64), records.Where(r => r.Source.Handle == "11").ToList(), Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        var line = mInput.Geometry.Should().ContainSingle().Subject;
        line.Segments.Should().ContainSingle();
        line.Segments[0].Length.Should().BeApproximately(12.0, 1e-9, "millimetre drawing units become metres");
    }
}
