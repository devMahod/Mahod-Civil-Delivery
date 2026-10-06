using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Rules 2.2 (engineering review, 30.09.2026) on synthetic data: a bike crossing beside a zebra is one yellow outline
/// around two bands — only the pedestrian band is zebra paint; a bike-symbol arrow block belongs to the bike unit;
/// objects held back as too long are shown on their BoQ row, never silent.
/// </summary>
public sealed class BoqRulesV22ReviewTests
{
    private const string RulesJson = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.2",
      "parameters": [{"id": "road_class", "label": "road", "value": 1},
                     {"id": "cross_w", "label": "w", "value": 3.0}, {"id": "fill_811", "label": "f", "value": 0.5},
                     {"id": "sq_812", "label": "sq", "value": 0.25}, {"id": "arrow_m2", "label": "a", "value": 1.2}],
      "chapters": [{"id": "10.01", "title": "c"}],
      "boq": [
        {"id": "M3", "chapter": "10.01", "item": "10.01.1942", "unit": "m2", "parts": [
          {"label": "811", "src": ["S"], "layers": ["YEL", "WHT"], "kind": "crosswalk_geo"},
          {"label": "812", "src": ["S"], "layers": ["BIKE812"], "kind": "length", "param": "sq_812", "max_len": 30},
          {"label": "arrows", "src": ["S"], "block_layers": ["ARW"], "kind": "count_x", "param": "arrow_m2", "same_point_m": 0.1}]}
      ],
      "source_roles": [{"id": "S", "file_pattern": "-S-", "label": "S"}],
      "ownership": [
        {"id": "bike_symbol_arrow", "src": ["S"], "layers": ["ARW"], "kinds": ["count"], "blocks": ["arrow-D"],
         "reason": "bike-symbol arrow: part of the bike unit"}
      ],
      "crosswalk_geometry": {"src": ["S"], "yellow_layers": ["YEL"], "white_layers": ["WHT"], "hatch_layers": [],
        "bike_layers": ["BIKE812"], "standard_width_param": "cross_w", "fill_param": "fill_811"},
      "long_objects": {"reason": "לבדיקה: עצם באורך {length} מטר בשכבת {layer}"}
    }
    """;

    private static BoqSegment Seg(double x1, double y1, double x2, double y2) => new(new BoqPoint(x1, y1), new BoqPoint(x2, y2));

    private static void Geo(BoqInputSet input, string handle, string layer, params BoqSegment[] segs)
    {
        input.Records.Add(new BoqRecord("S", layer, "LINE", "length", segs.Sum(s => s.Length), handle, false, "", null));
        input.Geometry.Add(new BoqEntityGeometry("S", handle, layer, false, segs, null));
    }

    /// <summary>
    /// Four yellow edges across a 10 m road: a 3.0 m pedestrian band, a 0.3 m gap and a 3.0 m bike band holding two
    /// 812 square rows (the 6422 pattern, e.g. AF2A/AF2D/AF2F/AF43).
    /// </summary>
    private static BoqInputSet ZebraWithBikeCrossing(bool withBikeRows)
    {
        var input = new BoqInputSet();
        Geo(input, "Y1", "YEL", Seg(0, 0.0, 10, 0.0));
        Geo(input, "Y2", "YEL", Seg(0, 3.0, 10, 3.0));
        Geo(input, "Y3", "YEL", Seg(0, 3.3, 10, 3.3));
        Geo(input, "Y4", "YEL", Seg(0, 6.3, 10, 6.3));
        if (withBikeRows)
        {
            Geo(input, "B1", "BIKE812", Seg(0, 3.55, 10, 3.55));
            Geo(input, "B2", "BIKE812", Seg(0, 6.05, 10, 6.05));
        }
        return input;
    }

    [Fact]
    public void ABikeBandBesideAZebraIsPaidIn812_OnlyThePedestrianBandIsZebraPaint()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(RulesJson), ZebraWithBikeCrossing(withBikeRows: true));
        var crossing = result.Crossings.Single();
        crossing.WidthSource.Should().Be("measured");
        crossing.Length!.Value.Should().BeApproximately(10.0, 1e-9);
        crossing.Width!.Value.Should().BeApproximately(3.0, 1e-9);
        crossing.OutlineWidth!.Value.Should().BeApproximately(6.3, 1e-9);
        crossing.BikeBandWidth!.Value.Should().BeApproximately(3.0, 1e-9);
        crossing.RemovedWidth!.Value.Should().BeApproximately(3.3, 1e-9);
        crossing.Kind.Should().Contain("רצועת הולכי רגל");
        // The 812 rows are still counted in their own part: 2 rows × 10 m.
        result.Parts.Single(p => p.Part.Label == "812").Base.Should().BeApproximately(20.0, 1e-9);
    }

    [Fact]
    public void WithoutBikeRowsTheYellowOutlineKeepsItsFullMeasuredWidth()
    {
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(RulesJson), ZebraWithBikeCrossing(withBikeRows: false));
        var crossing = result.Crossings.Single();
        crossing.Width!.Value.Should().BeApproximately(6.3, 1e-9);
        crossing.OutlineWidth.Should().BeNull();
    }

    [Fact]
    public void ABikeOnlyOutlineIsNotZebraPaint()
    {
        var input = new BoqInputSet();
        Geo(input, "Y1", "YEL", Seg(0, 0.0, 10, 0.0));
        Geo(input, "Y2", "YEL", Seg(0, 3.0, 10, 3.0));
        Geo(input, "B1", "BIKE812", Seg(0, 0.25, 10, 0.25));
        var crossing = BoqRulesEngine.Run(BoqRuleset.Parse(RulesJson), input).Crossings.Single();
        crossing.Width!.Value.Should().Be(0.0);
        crossing.Kind.Should().Contain("812");
    }

    [Fact]
    public void AnOwnershipRuleWithBlocksClaimsOnlyThoseBlocks()
    {
        var input = new BoqInputSet();
        input.Records.Add(new BoqRecord("S", "ARW", "INSERT", "count", 1.0, "A1", false, "arrow-D", null));
        input.Records.Add(new BoqRecord("S", "ARW", "INSERT", "count", 1.0, "A2", false, "TR-ARWXTX-XYX", null));
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(RulesJson), input);
        result.Buckets[0].Should().Be(BoqBucket.Excluded);
        result.Reasons[0].Should().Be("bike-symbol arrow: part of the bike unit");
        result.BucketLabel(1).Should().Be("item:M3:2");
        result.Parts.Single(p => p.Part.Label == "arrows").Objects.Should().Be(1);
    }

    [Fact]
    public void AnObjectHeldBackAsTooLongIsNamedOnItsBoqRow()
    {
        var input = ZebraWithBikeCrossing(withBikeRows: true);
        Geo(input, "LONG", "BIKE812", Seg(0, 50, 100, 50));
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(RulesJson), input);
        var line = result.Rules.Lines.Single(l => l.Id == "M3");
        var held = result.HeldLongObjects(line);
        held.Should().NotBeNull();
        held!.Value.Count.Should().Be(1);
        held.Value.Length.Should().BeApproximately(100.0, 1e-9);
        held.Value.Layers.Should().Equal("BIKE812");
        result.Reasons[result.Input.Records.FindIndex(r => r.Handle == "LONG")].Should().StartWith("לבדיקה:");
    }

    // ------------------------------------------------------------------ units (review 30/09)

    private static NeutralQuantityRecord Length(string handle, string unit, string insunits, string vertices) => new()
    {
        RecordId = $"q-{handle}", ProjectProfileId = "p", RunId = "run",
        Source = new QuantitySource { Drawing = "x-S-.dwg", DrawingHash = new string('b', 64), Handle = handle, EntityType = "LINE", Layer = "KERB" },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "line-length", RawValue = 10, Unit = unit, GeometryEvidence = new[] { 0.0, 0.0, 10.0, 0.0 },
            Parameters = new Dictionary<string, string>
            {
                ["cad_entity_database_insunits"] = insunits,
                [QuantityGeometryEvidence.SegmentsKey] = vertices,
                [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.StatusComplete,
                [QuantityGeometryEvidence.PlanChainKey] = vertices,
                [QuantityGeometryEvidence.PlanChainStatusKey] = QuantityGeometryEvidence.StatusComplete,
                [QuantityGeometryEvidence.PlanChainClosedKey] = "false",
            },
        },
    };

    private const string KerbRules = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.2",
      "parameters": [{"id": "road_class", "label": "road", "value": 1}],
      "chapters": [{"id": "10.01", "title": "c"}],
      "boq": [{"id": "K1", "chapter": "10.01", "item": "10.01.0010", "unit": "m", "parts": [
        {"label": "kerb", "src": ["S"], "layers": ["KERB"], "kind": "length", "object_width_m": 0.23, "object_width_note": "two faces"}]}]
    }
    """;

    [Fact]
    public void AUnitlessHostDeclaredInMetresKeepsItsPlanGeometry_TwoFacesCountOnce()
    {
        // An approved "drawn in metres" declaration labels the records in metres while INSUNITS stays Undefined.
        var records = new List<NeutralQuantityRecord>
        {
            Length("1", "מטר", "Undefined", "0,0;10,0"),
            Length("2", "מטר", "Undefined", "0,0.17;10,0.17"),
        };
        var input = BoqNeutralRecordAdapter.Build(BoqRuleset.Parse(KerbRules), new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("S", "run", @"C:\p\x-S-.dwg", new string('b', 64), records, System.Array.Empty<MahodAI.CivilDelivery.Shared.DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        input.LengthGeometry.Should().HaveCount(2);
        var result = BoqRulesEngine.Run(BoqRuleset.Parse(KerbRules), input);
        result.Lines.Single().Quantity.Should().BeApproximately(10.0, 1e-6, "two faces of one stone are one object");
        BoqNeutralRecordAdapter.UnitRefusal(records).Should().BeNull();
    }

    [Fact]
    public void RawDrawingUnitsAreRefused_NotPricedAsMetres()
    {
        var records = new List<NeutralQuantityRecord> { Length("1", "יחידת שרטוט", "Undefined", "0,0;10000,0") };
        BoqNeutralRecordAdapter.UnitRefusal(records).Should().Contain("יחידות השרטוט אינן מאושרות");
    }

    [Theory]
    [InlineData("Yards", 0.9144)]
    [InlineData("Dekameters", 10.0)]
    [InlineData("Microns", 1e-6)]
    [InlineData("USSurveyMile", 6_336_000.0 / 3937.0)]
    [InlineData("Meters", 1.0)]
    public void EveryExplicitUnitTheScanAcceptsAlsoGivesPlanGeometry(string insunits, double metres)
    {
        BoqNeutralRecordAdapter.MetresPerUnit(insunits).Should().Be(metres);
        BoqNeutralRecordAdapter.MetresPerUnit("Undefined").Should().BeNull();
    }

    [Fact]
    public void ATextConstantLongerThanExcelAllowsIsJoinedFromPieces()
    {
        var text = new string('א', 450) + "\"";
        var formula = BoqRulesWorkbookWriter.FormulaText(text);
        formula.Split('&').Should().HaveCount(3);
        formula.Split('&').Should().OnlyContain(piece => piece.Length <= 202 + 2);
        BoqRulesWorkbookWriter.FormulaText("קצר").Should().Be("\"קצר\"");
    }

    [Fact]
    public void AFailedAreaHatchIsPlacedByItsDiagnosticBoundary_ByTheCollectorsCentroidRule()
    {
        // The shape of hatch_area_failure_diagnostics.json (live SM 8AB16, 30/09): one edge-list loop of five lines.
        var json = """
        {"Sources":[{"Source":{"source_handle":"8AB16","layer":"TR-MARK-WHT-811","xref_path":null,"entity_type":"Hatch"},
          "Boundary":{"Header":"style=Outer; normal=0,0,1; elevation=0","Loops":[{"Index":0,"Evidence":{"Kind":"edge-list","Items":[
            "line=0,0 -> 4,0","line=4,0 -> 4,2","line=4,2 -> 0,2","line=0,2 -> 0,0"]}}]}},
          {"Source":{"source_handle":"XR1","layer":"L","xref_path":"C:/x.dwg"},"Boundary":{"Header":"normal=0,0,1","Loops":[]}},
          {"Source":{"source_handle":"UP","layer":"L"},"Boundary":{"Header":"normal=0,0,-1","Loops":[{"Evidence":{"Items":["vertex=1,1; bulge=0"]}}]}},
          {"Source":{"source_handle":"PL","layer":"L"},"Boundary":{"Header":"normal=0,0,1","Loops":[{"Evidence":{"Kind":"polyline","Items":[
            "vertex=10,10; bulge=0","vertex=12,10; bulge=0.5","vertex=12,14; bulge=0"]}}]}}]}
        """;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var hatches = BoqNeutralRecordAdapter.FailedHatchCentroids(document.RootElement);
        hatches.Select(h => h.Handle).Should().Equal("8AB16", "PL");
        hatches[0].X.Should().BeApproximately(2.0, 1e-12);
        hatches[0].Y.Should().BeApproximately(1.0, 1e-12);
        hatches[0].Layer.Should().Be("TR-MARK-WHT-811");
        hatches[1].X.Should().BeApproximately(34.0 / 3, 1e-12);
        hatches[1].Y.Should().BeApproximately(34.0 / 3, 1e-12);
    }

    [Fact]
    public void ANoiseSegmentShorterThanFiveCentimetresNeverWidensACrossing()
    {
        // Live SM 1274DC (30/09): a 2 cm yellow line 3.5 m beyond a 3.0 m crossing made it 6.55 m wide.
        var real = new[] { Seg(0, 0, 10, 0), Seg(0, 3, 10, 3) };
        BoqCrosswalkGeometry.Extent(real.Append(Seg(5, 6.5, 5.02, 6.5)).ToList(), 0.05, 0.1)
            .Across.Should().BeApproximately(3.0, 1e-9);
        BoqCrosswalkGeometry.Extent(real.Append(Seg(5, 6.5, 5.10, 6.5)).ToList(), 0.05, 0.1)
            .Across.Should().BeApproximately(6.5, 1e-9, "a 10 cm piece is real line work");
    }
}
