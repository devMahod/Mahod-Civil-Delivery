using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Capture = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.HatchAreaFailureDiagnostic;
using Factory = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.StrictHatchMixedLineRetraceInputFactory;
using HostInput = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.StrictHatchExactRetraceInputFactory.HostInput;
using Recovery = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.StrictHatchMixedLineRetraceAreaRecovery;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Product-lane port of Codex's HA mixed LINE retrace candidate (handoff 443F6FC9, root review 920CA9B8) on the saved
/// inputs of the BoQ HA 2BD0BB71 scan (run estimate-extract-20261001-083114-52e5b5b0). 7115/7118 are recovered as
/// quantity-only areas; 262376/71B6 stay refused. Never a direct native Area, a mapping, a price or section evidence;
/// 38→36 is claimed only after a native scan.
/// </summary>
public sealed class StrictHatchMixedLineRetraceIntegrationTests
{
    private const string FixtureSha = "36D3E5D4756B1D05";

    private sealed record Saved(Capture.Snapshot Snapshot, ProvenanceRef Source);

    private static (Dictionary<string, Saved> Inputs, HostInput Host, Dictionary<string, double> Expected) Fixture(
        [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "fixtures",
            "ha-mixed-retrace-0110", "saved_inputs.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().StartWith(FixtureSha);
        var root = JsonDocument.Parse(bytes).RootElement;
        root.GetProperty("hatch_area_failure_diagnostics_sha256").GetString()
            .Should().Be("52EAEB53BE6CB8F32F4A0B198B9FA93C7ED4F1686A05695C0C836947FBAC6195");
        var host = root.GetProperty("host");
        var inputs = root.GetProperty("inputs").EnumerateObject().ToDictionary(p => p.Name, p => new Saved(
            p.Value.GetProperty("Boundary").Deserialize<Capture.Snapshot>()!,
            p.Value.GetProperty("Source").Deserialize<ProvenanceRef>()!));
        var expected = root.GetProperty("expected_area_m2").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        return (inputs, new HostInput(host.GetProperty("RunId").GetString()!, host.GetProperty("SourceDrawing").GetString()!,
                host.GetProperty("SourceDrawingHash").GetString()!, host.GetProperty("DatabaseRevision").GetString()!,
                host.GetProperty("SourceDbMod").GetInt32(), null,
                host.GetProperty("PhysicalUnits").Deserialize<PhysicalDrawingUnitPolicy.Resolution>()!, true),
            expected);
    }

    [Theory]
    [InlineData("7115")]
    [InlineData("7118")]
    public void TheTwoProvenInputsAreRecoveredAsQuantityOnly_AndTheirBoundaryIsNeverMutated(string handle)
    {
        var (inputs, host, expected) = Fixture();
        host.DrawingSha256.Should().BeEquivalentTo("2BD0BB713CAAA9E4EE52271DD1E306A95DD7965BCEF55B830709BC2CDBF55FD1");
        var saved = inputs[handle];
        var original = JsonSerializer.Serialize(saved.Snapshot);
        Factory.TryRecover(saved.Snapshot, saved.Source, host, "Exception: eNotApplicable", null, out var recovered, out var refusal)
            .Should().BeTrue(refusal);
        recovered!.Method.Should().Be(Recovery.Method);
        recovered.RawValue.Should().BeApproximately(expected[handle], 1e-9);
        JsonSerializer.Serialize(saved.Snapshot).Should().Be(original);
        recovered.Parameters.Should().ContainKey("boundary_residual_sha256");
        ArtifactHash.Sha256OfText(recovered.Parameters["boundary_input_receipt_json"])
            .Should().Be(recovered.Parameters["boundary_input_receipt_sha256"]);
    }

    [Theory]
    [InlineData("262376")]
    [InlineData("71B6")]
    public void TheNegativeInputsStayRefused(string handle)
    {
        var (inputs, host, _) = Fixture();
        var saved = inputs[handle];
        Factory.TryRecover(saved.Snapshot, saved.Source, host, "Exception: eNotApplicable", null, out var recovered, out var refusal)
            .Should().BeFalse();
        recovered.Should().BeNull();
        refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ADirtyOrForeignHostIsRefusedBeforeAnyCancellation()
    {
        var (inputs, host, _) = Fixture();
        var saved = inputs["7115"];
        foreach (var bad in new[] { host with { DbMod = 1 }, host with { DrawingSha256 = new string('F', 64) },
                     host with { DirectHostModelSpaceIdentity = false } })
            Factory.TryRecover(saved.Snapshot, saved.Source, bad, "Exception: eNotApplicable", null, out var q, out _)
                .Should().BeFalse();
    }

    [Fact]
    public void ClassificationIsRecoveredNeverDirect_DirectHostOnly_AndTheUiDisclosesTheMethod()
    {
        BoqNeutralRecordAdapter.RecoveredHatchMethods.Should().Contain(Recovery.Method);
        var (inputs, host, _) = Fixture();
        var saved = inputs["7115"];
        Factory.TryRecover(saved.Snapshot, saved.Source, host, "Exception: eNotApplicable", null, out var recovered, out _).Should().BeTrue();
        var unrelated = new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "test", Message = "separate failed source",
            SourceRefs = { Other(saved.Source) },
        };
        EstimateFindingImpactPolicy.BlocksRecord(unrelated, Record(host, saved.Source, "7115", recovered!)).Should().BeFalse();
        EstimateFindingImpactPolicy.BlocksRecord(unrelated, Record(host, saved.Source, "AA/7115",
            new QuantityMeasurement { Kind = "area", Method = Recovery.Method, RawValue = 7, Unit = "מ\"ר" }, "xref"))
            .Should().BeTrue("no XREF whitelist for the recovered method");
        new QuantityRowViewModel
        {
            RuleKey = "test", Layer = saved.Source.Layer!, EntityType = "HATCH", Method = Recovery.Method, ObjectCount = 1,
            Quantity = 719.04, Unit = "מ\"ר", MappingState = "לא משויך",
        }.MethodDisplay.Should().Be("שטח (שחזור מדויק)");
    }

    [Fact]
    public void TheExtractionServiceTriesTheMixedPathOnlyAfterTheExactRetraceRefusal_WithOneHostCapture()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "Estimate", "CivilQuantityExtractionService.cs"));
        var capture = source.IndexOf("var originalRetraceHost = exactRetraceInput?.Invoke();", StringComparison.Ordinal);
        var exact = source.IndexOf("StrictHatchExactRetraceInputFactory.TryRecover(snapshot, originalSource,", StringComparison.Ordinal);
        var exactRefused = source.IndexOf("exact-retrace boundary recovery refused: ", StringComparison.Ordinal);
        var mixed = source.IndexOf("StrictHatchMixedLineRetraceInputFactory.TryRecover(snapshot, originalSource,", StringComparison.Ordinal);
        capture.Should().BePositive();
        capture.Should().BeLessThan(exact);
        exact.Should().BeLessThan(exactRefused);
        exactRefused.Should().BeLessThan(mixed);
        source.Split("exactRetraceInput?.Invoke()", StringSplitOptions.None).Length.Should().Be(2, "the host is captured once for both paths");
        source.Should().Contain("mixed-line-retrace boundary recovery refused: ");
    }

    /// <summary>The same drawing and layer, another handle: a separate failed source.</summary>
    private static ProvenanceRef Other(ProvenanceRef source)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(source))!;
        node["source_handle"] = "FFFF";
        return node.Deserialize<ProvenanceRef>()!;
    }

    private static NeutralQuantityRecord Record(HostInput host, ProvenanceRef source, string handle,
        QuantityMeasurement measurement, string? xref = null) => new()
    {
        RecordId = "test-" + handle, ProjectProfileId = "6422", RunId = host.RunId,
        Source = new() { Drawing = Path.GetFileName(host.DrawingPath), DrawingPath = host.DrawingPath,
            DrawingHash = host.DrawingSha256, Handle = handle, EntityType = "HATCH", Layer = source.Layer, Xref = xref },
        Measurement = measurement,
    };
}
