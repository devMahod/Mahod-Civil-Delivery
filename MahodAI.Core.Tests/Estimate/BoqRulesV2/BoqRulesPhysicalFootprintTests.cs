using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Rules 2.8 (BOQ-N1; Codex 00:23 / 00:39, 01.10.2026): a block with an approved physical footprint counts again at the same
/// point only when both INSERT transforms are proven, the definition matches the approved signature and the placed bodies
/// are positively separated from every twin already kept. The same transform counts once; anything else counts once and is
/// flagged for review — never silently. A block without an approved footprint keeps the same-point rule unchanged.
/// Synthetic data in the shape of the 6422 rack: block units, scale 0.01, body 0.45–1.55 m in front of the insertion point.
/// </summary>
public sealed class BoqRulesPhysicalFootprintTests
{
    private const string Rules = """
{"schema":"mahod-boq-rules/2","project":"TEST ONLY","version":"physical-footprint",
"parameters":[{"id":"road_class","label":"road","value":1}],
"chapters":[{"id":"10.02","title":"synthetic fixture"}],"boq":[
{"id":"R","chapter":"10.02","item":"10.02.0254","unit":"unit","parts":[{"label":"racks","src":["A","B"],"block_layers":["R"],"kind":"count","same_point_m":0.1}]},
{"id":"O","chapter":"10.02","item":"10.02.0255","unit":"unit","parts":[{"label":"other","src":["A"],"block_layers":["O"],"kind":"count","same_point_m":0.1}]}],
"physical_footprints":[{"block":"RACK","decision":"BOQ-TEST",
 "definition_signature":{"dxf_counts":{"LINE":2,"ARC":1},"envelope":[0,-50,200,50],"base_point":[0,0,0],"units_code":0,"geometry_digest":"abababababababababababababababababababababababababababababababab"},
 "body_envelope":[45,-5,155,5],"body_source":"synthetic","separation_min_m":0.01,
 "same_transform":{"translation_m":0.0001,"rotation_rad":0.000001,"scale_rel":1e-9}}]}
""";

    private const string Digest = "abababababababababababababababababababababababababababababababab";

    private static readonly BoqDefinitionSignature Approved =
        new(new Dictionary<string, int> { ["LINE"] = 2, ["ARC"] = 1 }, new[] { 0.0, -50, 200, 50 }, new[] { 0.0, 0, 0 }, 0, Digest);

    private sealed record Place(string Src, string Handle, double X, double Y, double Rotation, double Scale = 0.01,
        double NormalZ = 1, bool HasTransform = true, string Block = "RACK", string Layer = "R");

    private static BoqEngineResult Run(IEnumerable<Place> places, BoqDefinitionSignature? signature = null)
    {
        var input = new BoqInputSet();
        foreach (var p in places)
        {
            input.Records.Add(new BoqRecord(p.Src, p.Layer, "INSERT", "count", 1, p.Handle, false, p.Block, null));
            input.CountPoints[(p.Src, p.Handle)] = new BoqPoint(p.X, p.Y);
            input.CountRotations[(p.Src, p.Handle)] = p.Rotation;
            if (p.HasTransform)
                input.CountTransforms[(p.Src, p.Handle)] =
                    new BoqInsertTransform(p.X, p.Y, p.Rotation, new[] { p.Scale, p.Scale, p.Scale }, new[] { 0.0, 0, p.NormalZ });
        }
        foreach (var src in new[] { "A", "B" }) input.BlockSignatures[(src, "RACK")] = signature ?? Approved;
        return BoqRulesEngine.Run(BoqRuleset.Parse(Rules), input);
    }

    private static double Quantity(BoqEngineResult result, string id) => result.Lines.Single(l => l.Line.Id == id).Quantity;

    private static string Reason(BoqEngineResult result, string handle) =>
        result.Reasons[result.Input.Records.FindIndex(r => r.Handle == handle)] ?? "";

    [Fact]
    public void ABackToBackPairWithSeparatedBodiesIsTwoRacks()
    {
        var result = Run(new[] { new Place("A", "P1", 100, 200, 0), new Place("A", "P2", 100, 200.033, Math.PI) });
        Quantity(result, "R").Should().Be(2);
        result.Distinct.Should().ContainSingle().Which.Twins.Should().Equal("A:P1");
        result.Distinct[0].BodyGapM.Should().BeApproximately(0.9, 0.01, "bodies 0.45–1.55 m on opposite sides of the base line");
        result.FootprintReview.Should().BeEmpty();
        result.Dedup.Should().BeEmpty();
    }

    [Fact]
    public void TheSameTransformIsOneObject_AlsoInAnotherFile()
    {
        var result = Run(new[] { new Place("A", "P1", 100, 200, 0), new Place("B", "C1", 100 + 4e-8, 200, 0) });
        Quantity(result, "R").Should().Be(1);
        Reason(result, "C1").Should().Be("10.02.0254: אותו בלוק באותה נקודה כמו A:P1 — נספר פעם אחת");
        result.FootprintReview.Should().BeEmpty();
    }

    [Fact]
    public void ACopyIsComparedWithEveryKeptTwin_NotOnlyTheFirst()
    {
        // The GM copy of the flipped rack lies within reach of both racks; it duplicates the flipped one (same transform).
        var result = Run(new[]
        {
            new Place("A", "P1", 100, 200, 0), new Place("A", "P2", 100, 200.033, Math.PI),
            new Place("B", "C1", 100, 200, 0), new Place("B", "C2", 100, 200.033, Math.PI),
        });
        Quantity(result, "R").Should().Be(2);
        result.Dedup.Select(d => (d.Handle, d.Kept)).Should().Equal(("C1", "A:P1"), ("C2", "A:P2"));
        result.Distinct.Should().ContainSingle();
    }

    [Fact]
    public void OverlappingBodiesInAnotherRotationCountOnceForReview()
    {
        var result = Run(new[] { new Place("A", "P1", 100, 200, 0), new Place("A", "P2", 100, 200.02, 0.05) });
        Quantity(result, "R").Should().Be(1);
        Reason(result, "P2").Should().Contain("לא הוכח גוף נפרד (גופי המתקנים חופפים או צמודים)").And.EndWith("— לבדיקה");
        result.FootprintReview.Should().ContainSingle().Which.Kept.Should().Be("A:P1");
        result.Dedup.Single().Kept.Should().Be("A:P1 (גוף נפרד לא הוכח — לבדיקה)");
    }

    [Theory]
    [InlineData("missing", "טרנספורם חסר")]
    [InlineData("normal", "normal שאינו +Z או scale שלילי")]
    [InlineData("mirrored", "normal שאינו +Z או scale שלילי")]
    [InlineData("nan-scale", "טרנספורם חסר")]
    [InlineData("nan-normal", "טרנספורם חסר")]
    public void AnUnprovenTransformCountsOnceForReview(string defect, string why)
    {
        var second = defect switch
        {
            "missing" => new Place("A", "P2", 100, 200.033, Math.PI, HasTransform: false),
            "normal" => new Place("A", "P2", 100, 200.033, Math.PI, NormalZ: -1),
            "nan-scale" => new Place("A", "P2", 100, 200.033, Math.PI, Scale: double.NaN),
            "nan-normal" => new Place("A", "P2", 100, 200.033, Math.PI, NormalZ: double.NaN),
            _ => new Place("A", "P2", 100, 200.033, Math.PI, Scale: -0.01),
        };
        var result = Run(new[] { new Place("A", "P1", 100, 200, 0), second });
        Quantity(result, "R").Should().Be(1, "25 is never reached silently");
        Reason(result, "P2").Should().Contain($"({why})");
        result.FootprintReview.Should().ContainSingle().Which.Reason.Should().Be(why);
    }

    [Fact]
    public void ADifferentDefinitionOrBasePointIsNotTheApprovedBody()
    {
        var changed = new BoqDefinitionSignature(new Dictionary<string, int> { ["LINE"] = 3, ["ARC"] = 1 },
            new[] { 0.0, -50, 200, 50 }, new[] { 0.0, 0, 0 }, 0, Digest);
        var moved = new BoqDefinitionSignature(Approved.DxfCounts, Approved.Envelope, new[] { 10.0, 0, 0 }, 0, Digest);
        // Codex 00:47: the same counts and the same envelope, but the body moved inside it — only the geometry digest tells.
        var bodyMoved = Approved with { GeometryDigest = new string('c', 64) };
        var noDigest = Approved with { GeometryDigest = null };
        foreach (var signature in new[] { changed, moved, bodyMoved, noDigest })
        {
            var result = Run(new[] { new Place("A", "P1", 100, 200, 0), new Place("A", "P2", 100, 200.033, Math.PI) }, signature);
            Quantity(result, "R").Should().Be(1);
            result.FootprintReview.Single().Reason.Should().Be("הגדרת הבלוק שונה מההגדרה המאושרת");
        }
    }

    [Fact]
    public void AnotherScaleAtTheSamePointIsNotTheSameTransformAndItsBodyOverlaps()
    {
        var result = Run(new[] { new Place("A", "P1", 100, 200, 0), new Place("A", "P2", 100, 200, 0, Scale: 0.012) });
        Quantity(result, "R").Should().Be(1);
        result.FootprintReview.Should().ContainSingle();
    }

    [Fact]
    public void ABlockWithoutAnApprovedFootprintKeepsTheSamePointRule()
    {
        var result = Run(new[]
        {
            new Place("A", "O1", 300, 200, 0, Block: "OTHER", Layer: "O"),
            new Place("A", "O2", 300, 200.033, Math.PI, Block: "OTHER", Layer: "O"),
        });
        Quantity(result, "O").Should().Be(1);
        Reason(result, "O2").Should().Be("10.02.0255: בלוק זהה 3.3 ס\"מ מ-A:O1 בכיוון הפוך — נספר פעם אחת; לבדיקה אם זוג גב-אל-גב (ראו הערת השורה)");
        result.Distinct.Should().BeEmpty();
        result.FootprintReview.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void TheLiveAdapterReadsTheHostTransformAndTheDefinitionSignature(bool validSignature, int racks)
    {
        // The real back-to-back pair of 6422 SM (2056D3 / 2056E5): world point, rotation, scale and the definition signature
        // exactly as the collector writes them (geometry keys under cad_insert_point).
        const string rack = "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה";
        var signature = validSignature
            ? """{"dxf_counts":{"ARC":6,"ELLIPSE":4,"LINE":1648,"LWPOLYLINE":4},"envelope":[-9.4587E-10,-50.00000001907756,199.99999999922875,50.00000000777072],"base_point":[0,0,0],"units_code":0,"geometry_digest":"1925614b2ec127e3de1c097811992e7184cc8f56638fc22ca851ed8a463bc079"}"""
            : """{"dxf_counts":{"LINE":1648},"envelope":"not numbers"}""";
        NeutralQuantityRecord Rack(string handle, string world, string rotation) => new()
        {
            RecordId = $"q-{handle}-count", ProjectProfileId = "p", RunId = "run",
            Source = new QuantitySource { Drawing = "6422-SM.dwg", DrawingHash = new string('d', 64), Handle = handle, EntityType = "BLOCKREFERENCE", Layer = "byc" },
            Measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'", GeometryEvidence = new[] { 0.0, 0.0, 1.0, 1.0 },
                Parameters = new Dictionary<string, string>
                {
                    ["cad_entity_database_insunits"] = "Meters", ["block_name"] = rack,
                    ["cad_insert_point_world_m"] = world, ["cad_insert_point_world_m_status"] = QuantityGeometryEvidence.StatusComplete,
                    ["cad_rotation_rad"] = rotation,
                    [QuantityGeometryEvidence.InsertScaleKey] = "0.01,0.01,0.01",
                    [QuantityGeometryEvidence.InsertNormalKey] = "0,0,1",
                    [QuantityGeometryEvidence.InsertBlockSignatureKey] = signature,
                },
            },
        };
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = BoqNeutralRecordAdapter.Build(rules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("SM", "run-sm", "C:/p/6422-SM.dwg", new string('d', 64), new[]
            {
                Rack("2056D3", "204449.95426774948,649253.1840839096", "4.411020848877893"),
                Rack("2056E5", "204449.9241909192,649253.1984551632", "1.271113557110174"),
            }, System.Array.Empty<MahodAI.CivilDelivery.Shared.DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        input.CountTransforms[("SM", "2056D3")].Scale.Should().Equal(0.01, 0.01, 0.01);
        input.CountTransforms[("SM", "2056E5")].Normal.Should().Equal(0, 0, 1);
        (input.BlockSignatures[("SM", rack)] != null).Should().Be(validSignature, "a malformed signature matches no approved footprint");
        var result = BoqRulesEngine.Run(rules, input);
        result.Lines.Single(l => l.Line.Id == "N1").Quantity.Should().Be(racks);
        if (validSignature) result.Distinct.Single().BodyGapM.Should().BeApproximately(0.9, 0.02);
        else result.FootprintReview.Single().Reason.Should().Be("הגדרת הבלוק שונה מההגדרה המאושרת");
    }

    [Fact]
    public void TheDigestIsTheReferencesDigest_AndAMovedBodyChangesIt()
    {
        // The 1,662 entities of the 6422 rack definition from the offline read: C# descriptors = boq_geometry.entity_descriptor.
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("byc_definition_sm.json")));
        static double[] N(System.Text.Json.JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetDouble()).ToArray();
        static string? Descriptor(System.Text.Json.JsonElement e, double shiftX, double width = 0) => e.GetProperty("dxf_name").GetString() switch
        {
            "LINE" => BlockDefinitionDigest.Line(N(e, "start"), N(e, "end")),
            "ARC" => BlockDefinitionDigest.Arc(N(e, "center"), e.GetProperty("radius").GetDouble(), e.GetProperty("start_angle").GetDouble(),
                e.GetProperty("end_angle").GetDouble(), N(e, "normal")),
            "ELLIPSE" => BlockDefinitionDigest.Ellipse(N(e, "ellipse_center"), N(e, "ellipse_major_axis"), e.GetProperty("ellipse_radius_ratio").GetDouble(),
                e.GetProperty("ellipse_start_parameter").GetDouble(), e.GetProperty("ellipse_end_parameter").GetDouble(), N(e, "ellipse_normal")),
            "LWPOLYLINE" => BlockDefinitionDigest.LwPolyline(e.GetProperty("closed_polyline").GetBoolean(), e.GetProperty("elevation").GetDouble(),
                N(e, "lwpolyline_normal"), e.GetProperty("lwpolyline_constant_width").GetDouble(), e.GetProperty("vertices").EnumerateArray()
                    .Select(v => (v.GetProperty("x").GetDouble() + shiftX, v.GetProperty("y").GetDouble(), v.GetProperty("bulge").GetDouble(),
                        v.GetProperty("start_width").GetDouble() + width, v.GetProperty("end_width").GetDouble() + width)).ToList()),
            _ => null,
        };
        var entities = doc.RootElement.GetProperty("entities").EnumerateArray().ToList();
        entities.Should().HaveCount(1662);
        BlockDefinitionDigest.Digest(entities.Select(e => Descriptor(e, 0))).Should().Be(doc.RootElement.GetProperty("geometry_digest").GetString());
        BoqRuleset.LoadEmbedded6422().PhysicalFootprints.Single().Signature.GeometryDigest
            .Should().Be("1925614b2ec127e3de1c097811992e7184cc8f56638fc22ca851ed8a463bc079");
        // Moving the rack's polylines by 1 cm (block units) inside the same envelope changes the digest.
        BlockDefinitionDigest.Digest(entities.Select(e => Descriptor(e, 1))).Should().NotBe(doc.RootElement.GetProperty("geometry_digest").GetString());
        BlockDefinitionDigest.Digest(entities.Select(e => Descriptor(e, 0)).Append(null)).Should().BeNull("an entity without a descriptor");
        // Codex 01:23: the body's polylines given a width (a different body, same counts and envelope) — no digest, no approval.
        BlockDefinitionDigest.Digest(entities.Select(e => Descriptor(e, 0, width: 20))).Should().BeNull("only zero-width polylines are a digest body");
        BlockDefinitionDigest.LwPolyline(false, 0, new[] { 0.0, 0, 1 }, 5, new[] { (0.0, 0.0, 0.0, 0.0, 0.0) }).Should().BeNull("a constant width too");
        BlockDefinitionDigest.Canon(-1e-9).Should().Be("0");
        BlockDefinitionDigest.Angle(-Math.PI / 2).Should().Be(BlockDefinitionDigest.Canon(1.5 * Math.PI));
    }

    [Fact]
    public void TheRulesetRejectsAnIncompleteFootprint()
    {
        var broken = Rules.Replace("\"body_envelope\":[45,-5,155,5]", "\"body_envelope\":[155,-5,45,5]", StringComparison.Ordinal);
        var act = () => BoqRuleset.Parse(broken);
        act.Should().Throw<System.IO.InvalidDataException>().WithMessage("*body_envelope*");
    }
}
