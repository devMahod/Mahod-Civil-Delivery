using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Captured-input replay, not a new native Area call or CAD acceptance.</summary>
public sealed class StrictHatchCurveAreaRecoveryAdapterTests(ITestOutputHelper output)
{
    private const string Failure = "Autodesk.AutoCAD.Runtime.Exception: eNotApplicable";
    private const string Fixture = @"C:\Users\arthurf\AppData\Local\MahodAI_Civil3D\civil-delivery\runs\estimate-extract-20260912-080550-7979b8d4\hatch_area_failure_diagnostics.json";
    private const string Sha = "ED78C670ECF1C2A68EE849E3719BC55AA205B87802F272D5A09DED831E98389E";

    [Fact]
    public void SyntheticNativeVectorRecord_UsesExplicitContractAndPreservesCaptureIdentity()
    {
        // Simulated getter serialization, NOT a new native read or a modification
        // of the frozen 67 capture, which contains no reference-vector evidence.
        var snapshot = WithArcSuffix("; reference_vector=1,0");
        StrictHatchCurveAreaRecoveryAdapter.TryRecover(snapshot, DrawingUnitPolicy.Resolve(6),
            1, Failure, null, out var measured, out var refusal).Should().BeTrue(refusal);
        measured!.Parameters["boundary_contract"].Should().Be(
            MahodAI.CivilDelivery.Shared.StrictHatchCurveAreaRecovery.ExplicitFrameContract);
        measured.Parameters["boundary_explicit_native_frame_arc_count"].Should().Be("1");
        measured.Parameters["boundary_sha256"].Should().Be(
            MahodAI.CivilDelivery.Shared.ArtifactHash.Sha256OfText(JsonSerializer.Serialize(snapshot)));
        var error = double.Parse(measured.Parameters["boundary_si_area_error_bound_before_transform"], CultureInfo.InvariantCulture);
        measured.RawValue.Should().BeApproximately(Math.PI / 2, error);
    }

    [Theory]
    [InlineData("; reference_vector=0,1")]
    [InlineData("; reference_vector=-1,0")]
    [InlineData("; reference_vector=2,0")]
    [InlineData("; reference_vector=0.6,0.8")]
    [InlineData("; reference_vector=NaN,0")]
    [InlineData("; reference_vector=1,Infinity")]
    [InlineData("; reference_vector=1")]
    [InlineData("; reference_vector=")]
    [InlineData("; reference_vector_error=InvalidOperationException")]
    public void ExplicitInvalidContradictoryOrUnreadableFrame_NeverFallsBackToLegacy(string suffix)
    {
        StrictHatchCurveAreaRecoveryAdapter.TryRecover(WithArcSuffix(suffix), DrawingUnitPolicy.Resolve(6),
            1, Failure, null, out var measured, out var refusal).Should().BeFalse();
        measured.Should().BeNull();
        refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HistoricalCaptureWithoutFrame_KeepsLegacyContractAndZeroNativeFrameCount()
    {
        StrictHatchCurveAreaRecoveryAdapter.TryRecover(Semicircle(), DrawingUnitPolicy.Resolve(6),
            1, Failure, null, out var measured, out var refusal).Should().BeTrue(refusal);
        measured!.Parameters["boundary_explicit_native_frame_arc_count"].Should().Be("0");
        measured.Parameters["boundary_contract"].Should().Be(
            MahodAI.CivilDelivery.Shared.StrictHatchCurveAreaRecovery.Contract);
    }

    private static HatchAreaFailureDiagnostic.Snapshot WithArcSuffix(string suffix)
    {
        var original = Semicircle();
        var loop = original.Loops[0];
        return original with { Loops = new[] { loop with { Evidence = loop.Evidence! with
            { Items = new[] { loop.Evidence!.Items[0] + suffix, loop.Evidence.Items[1] } } } } };
    }

    [Theory]
    [InlineData(6, 1.0)]
    [InlineData(4, 1000.0)]
    public void ValidCurvedBoundary_ConvertsAreaOnceAndPreservesPrecisionAndOriginalFailure(int unitsCode, double radius)
    {
        var snapshot = Semicircle(radius);
        var bounds = new[] { -1.0, 0.0, 1.0, 1.0 };
        StrictHatchCurveAreaRecoveryAdapter.TryRecover(snapshot, DrawingUnitPolicy.Resolve(unitsCode),
            3, Failure, bounds, out var measured, out var refusal).Should().BeTrue(refusal);
        var error = double.Parse(measured!.Parameters["boundary_si_area_error_bound_before_transform"], CultureInfo.InvariantCulture);
        measured.RawValue.Should().BeApproximately(Math.PI / 2, error);
        measured.GeometryEvidence.Should().BeSameAs(bounds);
        measured.Method.Should().Be(StrictHatchCurveAreaRecoveryAdapter.Method);
        measured.Parameters["original_area_api_failure"].Should().Be(Failure);
        measured.Parameters["boundary_contract"].Should().Be(MahodAI.CivilDelivery.Shared.StrictHatchCurveAreaRecovery.Contract);
        measured.Parameters["boundary_arc_count"].Should().Be("1");
        measured.Parameters["boundary_transform_maximum_linear_scale"].Should().Be("3");
        double.Parse(measured.Parameters["boundary_host_area_error_upper_bound"], CultureInfo.InvariantCulture)
            .Should().BeInRange(0, 1e-6);
        measured.Parameters["boundary_sha256"].Should().MatchRegex("^[A-Fa-f0-9]{64}$");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnprovenTransform_ProducesNoArea(double maximumScale)
    {
        StrictHatchCurveAreaRecoveryAdapter.TryRecover(Semicircle(), DrawingUnitPolicy.Resolve(6),
            maximumScale, Failure, null, out var measured, out var refusal).Should().BeFalse();
        measured.Should().BeNull();
        refusal.Should().Contain("transform");
    }

    [Fact]
    public void MissingOriginalFailure_UnknownUnits_OrPartialCapture_NeverEmitArea()
    {
        var original = Semicircle();
        foreach (var scenario in new[]
        {
            (original, DrawingUnitPolicy.Resolve(0), Failure),
            (original, DrawingUnitPolicy.Resolve(6), ""),
            (original with { Truncated = true }, DrawingUnitPolicy.Resolve(6), Failure),
            (original with { Error = "read failed" }, DrawingUnitPolicy.Resolve(6), Failure),
            (original with { DeclaredLoops = 2 }, DrawingUnitPolicy.Resolve(6), Failure),
            (original with { CoordinateSystem = "host WCS" }, DrawingUnitPolicy.Resolve(6), Failure),
        })
        {
            StrictHatchCurveAreaRecoveryAdapter.TryRecover(scenario.Item1, scenario.Item2, 1,
                scenario.Item3, null, out var measured, out var refusal).Should().BeFalse();
            measured.Should().BeNull();
            refusal.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void SeparatedOriginalLoops_MeasureEveryLoopAndKeepTheFullCaptureIdentity()
    {
        var snapshot = TwoLoops();
        StrictHatchCurveAreaRecoveryAdapter.TryRecover(snapshot, DrawingUnitPolicy.Resolve(6),
            1, Failure, null, out var measured, out var refusal).Should().BeTrue(refusal);
        var error = double.Parse(measured!.Parameters["boundary_si_area_error_bound_before_transform"], CultureInfo.InvariantCulture);
        measured.RawValue.Should().BeApproximately(Math.PI, error);
        measured.Parameters["boundary_loop_count"].Should().Be("2");
        measured.Parameters["boundary_edge_count"].Should().Be("4");
        measured.Parameters["boundary_contract"].Should().Be(MahodAI.CivilDelivery.Shared.StrictHatchCurveAreaRecovery.DisjointContract);
        measured.Parameters["boundary_sha256"].Should().Be(
            MahodAI.CivilDelivery.Shared.ArtifactHash.Sha256OfText(JsonSerializer.Serialize(snapshot)));
    }

    [Fact]
    public void MultiLoopCapture_NeverDropsAnUnreadableInvalidMissingOrDuplicateLoop()
    {
        var original = TwoLoops();
        foreach (var snapshot in new[]
        {
            original with { DeclaredLoops = 3 },
            original with { Loops = new[] { original.Loops[0] } },
            original with { Loops = new[] { original.Loops[0], original.Loops[0] } },
            original with { Loops = new[] { original.Loops[0], original.Loops[1] with { Error = "read failed" } } },
            original with { Loops = new[] { original.Loops[0], original.Loops[1] with
                { Evidence = original.Loops[1].Evidence! with { Flags = "External, NotClosed" } } } },
            TwoLoops(secondCenterX: 100000), // Coincident envelopes cannot be declared disjoint.
        })
        {
            StrictHatchCurveAreaRecoveryAdapter.TryRecover(snapshot, DrawingUnitPolicy.Resolve(6),
                1, Failure, null, out var measured, out var refusal).Should().BeFalse();
            measured.Should().BeNull();
            refusal.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Native67CapturedCurves_RecoverAnIndependentlyMeasuredOriginalBoundary_AndRefuseAmbiguities()
    {
        File.Exists(Fixture).Should().BeTrue("this explicit native-capture replay requires its fixture");
        var bytes = File.ReadAllBytes(Fixture);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(Sha);
        using var document = JsonDocument.Parse(bytes);
        var outcomes = new Dictionary<string, (QuantityMeasurement? Measurement, string Refusal)>();
        foreach (var item in document.RootElement.GetProperty("Sources").EnumerateArray())
        {
            var handle = item.GetProperty("Source").GetProperty("source_handle").GetString()!;
            var snapshot = item.GetProperty("Boundary").Deserialize<HatchAreaFailureDiagnostic.Snapshot>()!;
            var okay = StrictHatchCurveAreaRecoveryAdapter.TryRecover(snapshot, DrawingUnitPolicy.Resolve(6),
                1, Failure, null, out var measured, out var refusal);
            outcomes.Add(handle, (measured, refusal));
            output.WriteLine($"{handle}: {(okay ? "RECOVERED " + measured!.RawValue.ToString("R", CultureInfo.InvariantCulture) : "REFUSED " + refusal)}");
        }
        var positive = outcomes.Single(pair => pair.Key.EndsWith("/2623B0", StringComparison.OrdinalIgnoreCase)).Value;
        positive.Measurement.Should().NotBeNull(positive.Refusal);
        const double independentArea = 40.553170223132816727;
        var error = double.Parse(positive.Measurement!.Parameters["boundary_source_area_error_bound"], CultureInfo.InvariantCulture);
        Math.Abs(positive.Measurement.RawValue - independentArea).Should().BeLessThanOrEqualTo(error);
        positive.Measurement.Parameters["boundary_edge_count"].Should().Be("11");
        positive.Measurement.Parameters["boundary_arc_count"].Should().Be("5");
        var disjoint = outcomes.Single(pair => pair.Key.EndsWith("/7196", StringComparison.OrdinalIgnoreCase)).Value;
        disjoint.Measurement.Should().NotBeNull(disjoint.Refusal);
        const double independentDisjointArea = 119.91825273890649896;
        var disjointError = double.Parse(disjoint.Measurement!.Parameters["boundary_source_area_error_bound"], CultureInfo.InvariantCulture);
        Math.Abs(disjoint.Measurement.RawValue - independentDisjointArea).Should().BeLessThanOrEqualTo(disjointError);
        disjoint.Measurement.Parameters["boundary_loop_count"].Should().Be("2");
        disjoint.Measurement.Parameters["boundary_edge_count"].Should().Be("13");
        var threeLoops = outcomes.Single(pair => pair.Key.EndsWith("/7893", StringComparison.OrdinalIgnoreCase)).Value;
        threeLoops.Measurement.Should().NotBeNull(threeLoops.Refusal);
        var threeLoopError = double.Parse(threeLoops.Measurement!.Parameters["boundary_source_area_error_bound"], CultureInfo.InvariantCulture);
        Math.Abs(threeLoops.Measurement.RawValue - 248.79049207644834).Should().BeLessThanOrEqualTo(threeLoopError);
        threeLoops.Measurement.Parameters["boundary_loop_count"].Should().Be("3");
        threeLoops.Measurement.Parameters["boundary_edge_count"].Should().Be("14");
        // A topology pass is not a precision pass. Do not mislabel this retained
        // reader limitation as invalid source data or silently weaken its guard.
        var precision = outcomes.Single(pair => pair.Key.EndsWith("/7202", StringComparison.OrdinalIgnoreCase)).Value;
        precision.Measurement.Should().BeNull();
        precision.Refusal.Should().Contain("reader precision");
        foreach (var suffix in new[] { "/78A5", "/78BF", "/2623AF" })
        {
            var refused = outcomes.Single(pair => pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Value;
            refused.Measurement.Should().BeNull(suffix + " must not turn an ambiguous/crossing boundary into a quantity");
            refused.Refusal.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static HatchAreaFailureDiagnostic.Snapshot TwoLoops(double secondCenterX = 100010)
    {
        var first = Semicircle();
        return first with
        {
            DeclaredLoops = 2,
            Loops = new[] { first.Loops[0], Semicircle(centerX: secondCenterX).Loops[0] with { Index = 1 } },
        };
    }

    private static HatchAreaFailureDiagnostic.Snapshot Semicircle(double radius = 1, double centerX = 100000)
    {
        string Number(double n) => n.ToString("R", CultureInfo.InvariantCulture);
        var r = Number(radius);
        // Native computed arc endpoints are rounded survey coordinates. A literal
        // y=0 paired with binary Math.PI would assert an unrealistically exact
        // endpoint (four ULP around zero is subnormal). Translation preserves area.
        var left = Number(centerX - radius);
        var right = Number(centerX + radius);
        return new("curve recovery test", "raw hatch OCS / source drawing units; not host WCS or square metres", 1,
            "style=Outer; normal=0,0,1; elevation=0",
            new[] { new HatchAreaFailureDiagnostic.LoopResult(0,
                new HatchAreaFailureDiagnostic.Loop("edge-list", "External", new[]
                {
                    $"arc: start={right},200000; end={left},200000; centre={Number(centerX)},200000; radius={r}; angles=0,{Number(Math.PI)}; clockwise=False",
                    $"line={left},200000 -> {right},200000",
                }, false), null) }, false, null);
    }
}
