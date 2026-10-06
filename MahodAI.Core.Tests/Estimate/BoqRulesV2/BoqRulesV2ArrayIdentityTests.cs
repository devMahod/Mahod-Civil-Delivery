using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Path-array identity (Codex T review 30.09, CODEX_T_FROZEN_C_REVIEW_HE.md): the frozen 6422 inputs plus one synthetic
/// record each. A member is compared only with model symbols of its own file (the reference compares with the SM model
/// space), and only the array insert itself — by (file, handle) — is the array container, never another insert that happens
/// to share its anonymous block name.
/// </summary>
public sealed class BoqRulesV2ArrayIdentityTests
{
    private static (BoqRuleset Rules, BoqInputSet Input) Frozen()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var input = BoqGoldenInputReader.Read(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("golden_inputs_6422.json")), rules,
            BoqRulesV2GoldenAcceptanceTests.UnmeasuredLayers);
        return (rules, input);
    }

    private static string Bucket(BoqEngineResult result, string src, string handle) =>
        result.BucketLabel(result.Input.Records.FindIndex(r => r.Src == src && r.Handle == handle && r.Kind == "count"));

    [Fact]
    public void ASymbolOfAnotherFileAtAMembersPointDoesNotRemoveTheMember()
    {
        var (rules, input) = Frozen();
        const string member = "2070F8/1/2070FB";
        BoqRulesEngine.Run(rules, input).Should().Match<BoqEngineResult>(r => Bucket(r, "SM", member) == "item:M3:9");
        var at = input.CountPoints[("SM", member)];
        input.Records.Add(new BoqRecord("DR", "DR-TEST", "BLOCKREFERENCE", "count", 1, "DR-BIKE-TEST", false, "BIKE", null));
        input.CountPoints[("DR", "DR-BIKE-TEST")] = at;
        var result = BoqRulesEngine.Run(rules, input);
        Bucket(result, "SM", member).Should().Be("item:M3:9", "a DR symbol is no evidence that the SM array member is the same object");
    }

    [Fact]
    public void OnlyTheArrayInsertItselfIsTheContainer_NotAnotherInsertOfTheSameAnonymousBlock()
    {
        var (rules, input) = Frozen();
        input.Records.Add(new BoqRecord("SM", "helpline", "BLOCKREFERENCE", "count", 1, "NOT-THE-ARRAY", false, "*U269", null));
        var result = BoqRulesEngine.Run(rules, input);
        var index = result.Input.Records.FindIndex(r => r.Handle == "NOT-THE-ARRAY");
        (result.Reasons[index] ?? "").Should().NotContain("מערך (Array)", "the same *U name is not the same array");
        var container = result.Input.Records.FindIndex(r => r.Src == "SM" && r.Handle == "2070F8" && r.Kind == "count");
        result.BucketLabel(container).Should().Be("excl");
        result.Reasons[container].Should().StartWith("מערך (Array) של 48 סמלי אופניים לאורך הקו 2070F0 בשכבת -ZEVA-804");
    }
    [Fact]
    public void TheLiveAdapterBuildsTheArrayFromTheCollectorsItemSymbolsAndReadsTheRotation()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        NeutralQuantityRecord Neutral(string handle, string etype, string kind, double value, string layer, Dictionary<string, string> parameters) => new()
        {
            RecordId = $"q-{handle.Replace('/', '-')}-{kind}", ProjectProfileId = "p", RunId = "run",
            Source = new QuantitySource { Drawing = "6422-SM.dwg", DrawingHash = new string('d', 64), Handle = handle, EntityType = etype, Layer = layer },
            Measurement = new QuantityMeasurement
            {
                Kind = kind, Method = kind == "count" ? "block-count" : "polyline-length", RawValue = value, Unit = kind == "count" ? "יח'" : "מטר",
                GeometryEvidence = new[] { 0.0, 0.0, 1.0, 1.0 }, Parameters = parameters,
            },
        };
        Dictionary<string, string> Block(string name, string world, params (string Key, string Value)[] extra)
        {
            var d = new Dictionary<string, string>
            {
                ["cad_entity_database_insunits"] = "Meters", ["block_name"] = name,
                ["cad_insert_point_world_m"] = world, ["cad_insert_point_world_m_status"] = QuantityGeometryEvidence.StatusComplete,
            };
            foreach (var (k, v) in extra) d[k] = v;
            return d;
        }
        var records = new List<NeutralQuantityRecord>
        {
            Neutral("A1", "BLOCKREFERENCE", "count", 1, "helpline",
                Block("*U269", "100,200", ("cad_array", "associative"), ("cad_array_items", "1"), ("cad_array_first_item_world_m", "100,200"))),
            Neutral("A1/1/B1", "BLOCKREFERENCE", "count", 1, "TR-SIGN-STAG-BL", Block("BIKE", "101,200", ("cad_array_handle", "A1"))),
            Neutral("A1/1/D1", "BLOCKREFERENCE", "count", 1, "TR-SIGN-STAG-BL", Block("arrow-D", "101.5,200", ("cad_array_handle", "A1"))),
            Neutral("R1", "BLOCKREFERENCE", "count", 1, "TR-SIGN-STAG-BL", Block("BIKE", "500,500", ("cad_rotation_rad", "1.5"))),
            Neutral("P1", "POLYLINE", "length", 50, "-ZEVA-804", new Dictionary<string, string>
            {
                ["cad_entity_database_insunits"] = "Meters",
                [QuantityGeometryEvidence.SegmentsKey] = "100,200;150,200",
                [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.StatusComplete,
                [QuantityGeometryEvidence.SegmentsClosedKey] = "false",
                [QuantityGeometryEvidence.PlanChainKey] = "100,200;150,200",
                [QuantityGeometryEvidence.PlanChainStatusKey] = QuantityGeometryEvidence.StatusComplete,
                [QuantityGeometryEvidence.PlanChainClosedKey] = "false",
            }),
        };
        var input = BoqNeutralRecordAdapter.Build(rules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("SM", "run-sm", "C:/p/6422-SM.dwg", new string('d', 64), records, System.Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        var array = input.Arrays.Should().ContainSingle().Which;
        array.Src.Should().Be("SM");
        array.Handle.Should().Be("A1");
        array.Block.Should().Be("*U269");
        array.FirstItemPoint.Should().Be(new BoqPoint(100, 200));
        array.PathLine.Should().Be("P1");
        array.PathLayer.Should().Be("-ZEVA-804");
        array.MemberHandles.Should().Equal("A1/1/B1");
        input.Records.Select(r => r.Handle).Should().Contain("A1/1/B1").And.NotContain("A1/1/D1", "only the ruleset's member block is a record");
        input.CountPoints[("SM", "A1/1/B1")].Should().Be(new BoqPoint(101, 200));
        input.CountRotations[("SM", "R1")].Should().Be(1.5);
        input.CountRotations.Should().NotContainKey(("SM", "A1/1/B1"), "a nested symbol carries no host rotation");

        var result = BoqRulesEngine.Run(rules, input);
        var container = result.Input.Records.FindIndex(r => r.Handle == "A1");
        result.Reasons[container].Should().StartWith("מערך (Array) של 1 סמלי אופניים לאורך הקו P1 בשכבת -ZEVA-804");
        result.BucketLabel(result.Input.Records.FindIndex(r => r.Handle == "A1/1/B1")).Should().Be("item:M3:9");
    }
}
