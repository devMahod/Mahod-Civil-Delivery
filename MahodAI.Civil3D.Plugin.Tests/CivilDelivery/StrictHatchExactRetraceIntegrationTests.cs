using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Capture = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.HatchAreaFailureDiagnostic;
using Factory = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.StrictHatchExactRetraceInputFactory;
using Recovery = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.StrictHatchExactRetraceAreaRecovery;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Product-lane port of Codex's isolated 40/40 review (handoff 3A8FEACF, manifest DB812C94) on the saved 71B4 input
/// of the BoQ HA 2BD0BB71 scan (run estimate-extract-20260930-225523-7e5a8ff9; scan 8D888268, diagnostics A864A01B).
/// Quantity-only interpretation; never a direct native Area, mapping, price or section evidence.
/// </summary>
public sealed class StrictHatchExactRetraceIntegrationTests
{
    private const string FixtureSha = "C413669E04D14077";

    private static (Capture.Snapshot Snapshot, ProvenanceRef Source, Factory.HostInput Host, double Expected) Fixture(
        [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "fixtures",
            "ha-exact-retrace-0110", "71B4_saved_input.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().StartWith(FixtureSha);
        var root = JsonDocument.Parse(bytes).RootElement;
        root.GetProperty("estimate_scan_sha256").GetString().Should().Be("8D888268B2E589C32E3BA6ABE21E3B81EC252FAD3C8DE397D04772227A917EE6");
        root.GetProperty("hatch_area_failure_diagnostics_sha256").GetString().Should().Be("A864A01BB891D0D14F2BAA9CA49068E70F29B88862B2BCBA2183263E162B75C2");
        var host = root.GetProperty("host");
        return (root.GetProperty("boundary").Deserialize<Capture.Snapshot>()!,
            root.GetProperty("source").Deserialize<ProvenanceRef>()!,
            new Factory.HostInput(host.GetProperty("RunId").GetString()!, host.GetProperty("SourceDrawing").GetString()!,
                host.GetProperty("SourceDrawingHash").GetString()!, host.GetProperty("DatabaseRevision").GetString()!,
                host.GetProperty("SourceDbMod").GetInt32(), null,
                host.GetProperty("PhysicalUnits").Deserialize<PhysicalDrawingUnitPolicy.Resolution>()!, true),
            root.GetProperty("expected_area_m2").GetDouble());
    }

    [Fact]
    public void Saved71B4Input_IsRecoveredAsQuantityOnly_WithAnInputOnlyReceipt()
    {
        var (snapshot, source, host, expected) = Fixture();
        host.DrawingSha256.Should().BeEquivalentTo("2BD0BB713CAAA9E4EE52271DD1E306A95DD7965BCEF55B830709BC2CDBF55FD1",
            "the BoQ HA source, not the sections HA 8F2469ED");
        var original = JsonSerializer.Serialize(snapshot);
        Factory.TryRecover(snapshot, source, host, "Exception: eNotApplicable", null, out var recovered, out var refusal)
            .Should().BeTrue(refusal);
        recovered!.RawValue.Should().BeApproximately(expected, 1e-12);
        recovered.Method.Should().Be(Recovery.Method);
        JsonSerializer.Serialize(snapshot).Should().Be(original, "the original boundary is never mutated");
        var receipt = recovered.Parameters["boundary_input_receipt_json"];
        ArtifactHash.Sha256OfText(receipt).Should().Be(recovered.Parameters["boundary_input_receipt_sha256"]);
        JsonDocument.Parse(receipt).RootElement.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { "Contract", "Host", "Source", "Boundary", "OriginalAreaError" }, "input only, no result/proof");
    }

    public static TheoryData<string> RefusalCases() => new()
    {
        "dirty", "unknown-dbmod", "source-failure", "no-revision", "nested-or-xref", "sha", "path", "run", "units",
        "no-loops", "truncated", "loop-error", "edge-truncated", "xref-source",
    };

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public void EveryGuardRefuses(string name)
    {
        var (snapshot, source, host, _) = Fixture();
        var loop = snapshot.Loops[0];
        var sourceNode = JsonNode.Parse(JsonSerializer.Serialize(source))!;
        sourceNode["xref_path"] = "unproved-reference";
        (Capture.Snapshot s, ProvenanceRef o, Factory.HostInput h) input = name switch
        {
            "dirty" => (snapshot, source, host with { DbMod = 1 }),
            "unknown-dbmod" => (snapshot, source, host with { DbMod = null }),
            "source-failure" => (snapshot, source, host with { Failure = "source changed" }),
            "no-revision" => (snapshot, source, host with { DatabaseRevision = "" }),
            "nested-or-xref" => (snapshot, source, host with { DirectHostModelSpaceIdentity = false }),
            "sha" => (snapshot, source, host with { DrawingSha256 = new string('F', 64) }),
            "path" => (snapshot, source, host with { DrawingPath = "C:/other.dwg" }),
            "run" => (snapshot, source, host with { RunId = "other-run" }),
            "units" => (snapshot, source, host with { Units = host.Units with { IsSupported = false } }),
            "no-loops" => (snapshot with { Loops = Array.Empty<Capture.LoopResult>() }, source, host),
            "truncated" => (snapshot with { Truncated = true }, source, host),
            "loop-error" => (snapshot with { Loops = new[] { loop with { Error = "getter failed" } } }, source, host),
            "edge-truncated" => (snapshot with { Loops = new[] { loop with { Evidence = loop.Evidence! with { Truncated = true } } } }, source, host),
            "xref-source" => (snapshot, sourceNode.Deserialize<ProvenanceRef>()!, host),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        Factory.TryRecover(input.s, input.o, input.h, "Exception: eNotApplicable", null, out var q, out var why).Should().BeFalse(name);
        q.Should().BeNull();
        why.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Adapter_CountsExactRetraceAsRecovered_AndAnUnknownMethodStaysUnresolved()
    {
        var (snapshot, source, host, _) = Fixture();
        Factory.TryRecover(snapshot, source, host, "Exception: eNotApplicable", null, out var recovered, out _).Should().BeTrue();
        var rules = BoqRuleset.LoadEmbedded6422();
        BoqInputSet Adapt(params NeutralQuantityRecord[] records) => BoqNeutralRecordAdapter.Build(rules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("HA", host.RunId, host.DrawingPath, host.DrawingSha256, records, Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
        });
        var known = Adapt(Record(host, source, "A1", Measurement("hatch-area")), Record(host, source, "A2", Measurement("hatch-linear-boundary-area")),
            Record(host, source, "A3", Measurement("hatch-line-arc-boundary-area")), Record(host, source, "A4", recovered!));
        known.HatchLayers.Sum(t => t.DirectCount).Should().Be(1);
        known.HatchLayers.Sum(t => t.RecoveredCount).Should().Be(3, "linear, curve and exact-retrace are all recovered, never direct");
        var unknown = Adapt(Record(host, source, "A5", Measurement("hatch-unapproved-new-method", 999)));
        unknown.HatchLayers.Sum(t => t.UnresolvedCount).Should().Be(1);
        unknown.HatchLayers.Sum(t => t.DirectArea + t.RecoveredArea).Should().Be(0m, "an unknown method never becomes a direct area");
        unknown.Warnings.Should().Contain(w => w.Contains("hatch-unapproved-new-method"));
    }

    [Fact]
    public void ImpactPolicy_AcceptsExactRetraceOnlyOnTheDirectHost_AndUiDisclosesTheMethod()
    {
        var (snapshot, source, host, _) = Fixture();
        Factory.TryRecover(snapshot, source, host, "Exception: eNotApplicable", null, out var recovered, out _).Should().BeTrue();
        var unrelatedNode = JsonNode.Parse(JsonSerializer.Serialize(source))!;
        unrelatedNode["source_handle"] = "FFFF";
        var unrelated = new DeliveryFinding { Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "test", Message = "separate failed source", SourceRefs = { unrelatedNode.Deserialize<ProvenanceRef>()! } };
        var same = new DeliveryFinding { Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "test", Message = "same source failed", SourceRefs = { source } };
        EstimateFindingImpactPolicy.BlocksRecord(unrelated, Record(host, source, "71B4", recovered!)).Should().BeFalse();
        EstimateFindingImpactPolicy.BlocksRecord(same, Record(host, source, "71B4", recovered!)).Should().BeTrue();
        EstimateFindingImpactPolicy.BlocksRecord(unrelated, Record(host, source, "71B4", Measurement("hatch-unapproved-new-method"))).Should().BeTrue();
        EstimateFindingImpactPolicy.BlocksRecord(unrelated, Record(host, source, "AA/71B4", Measurement(Recovery.Method + "+xref-transform"), "xref"))
            .Should().BeTrue("exact-retrace has no XREF whitelist");
        Record(host, source, "71B4", recovered!).Classification.MappingApprovedAtUtc.HasValue.Should().BeFalse("measurement never approves a mapping");
        QuantityRowViewModel Row(string method) => new() { RuleKey = "test", Layer = source.Layer!, EntityType = "HATCH",
            Method = method, ObjectCount = 1, Quantity = 35.17, Unit = "מ\"ר", MappingState = "לא משויך" };
        Row(Recovery.Method).MethodDisplay.Should().Be("שטח (שחזור מדויק)");
        Row(Recovery.Method).Method.Should().Be(Recovery.Method);
        Row("hatch-area").MethodDisplay.Should().Be("שטח");
        Row("unknown").MethodDisplay.Should().Be("unknown");
    }

    private static NeutralQuantityRecord Record(Factory.HostInput host, ProvenanceRef source, string handle,
        QuantityMeasurement measurement, string? xref = null) => new()
    {
        RecordId = "test-" + handle, ProjectProfileId = "6422", RunId = host.RunId,
        Source = new() { Drawing = Path.GetFileName(host.DrawingPath), DrawingPath = host.DrawingPath,
            DrawingHash = host.DrawingSha256, Handle = handle, EntityType = "HATCH", Layer = source.Layer, Xref = xref },
        Measurement = measurement,
    };

    private static QuantityMeasurement Measurement(string method, double value = 7) =>
        new() { Kind = "area", Method = method, RawValue = value, Unit = "מ\"ר" };
}
