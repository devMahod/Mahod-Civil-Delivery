using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Pure boundary-policy tests and a SHA-pinned replay of native diagnostic evidence.
/// These do not invoke Hatch.Area, open a DWG, or establish native acceptance.
/// </summary>
public sealed class StrictHatchLinearAreaRecoveryTests
{
    private const string AreaFailure = "Autodesk.AutoCAD.Runtime.Exception: eNotApplicable";
    private const string FixturePath =
        @"C:\Users\arthurf\AppData\Local\MahodAI_Civil3D\civil-delivery\runs\estimate-extract-20260910-101153-80274c7f\hatch_area_failure_diagnostics.json";
    private const string FixtureSha256 =
        "0DBFEACCCDD4E381EE00C648F35683DD30767DB0C16E50B98FD5EFF219746FF5";

    [Theory]
    [InlineData("Normal", 1)]
    [InlineData("Outer", 1)]
    [InlineData("Ignore", 1)]
    [InlineData("Normal", -1)]
    [InlineData("Outer", -1)]
    [InlineData("Ignore", -1)]
    public void ValidSingleLinearSquare_ProducesPositiveAreaAndOriginalFailureEvidence(string style, int normalZ)
    {
        var snapshot = Square() with { Header = $"style={style}; normal=0,0,{normalZ}; elevation=479.25" };
        var measurement = Recover(snapshot);

        measurement.Kind.Should().Be("area");
        measurement.Method.Should().Be("hatch-linear-boundary-area");
        measurement.RawValue.Should().Be(12);
        measurement.Unit.Should().Be(Metres().AreaUnit);
        measurement.Parameters["original_area_api_failure"].Should().Be(AreaFailure);
        measurement.Parameters["boundary_source_area"].Should().Be("12");
        measurement.Parameters["source_insunits"].Should().Be("Meters");
        measurement.Parameters["boundary_sha256"].Should().MatchRegex("^[0-9a-fA-F]{64}$");
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("External")]
    [InlineData("Derived")]
    [InlineData("Outermost")]
    [InlineData("External, Derived")]
    [InlineData("External, Outermost")]
    public void OnlyPermittedLoopFlags_DoNotPreventSingleBoundaryArea(string flags)
        => Recover(WithLoop(Square(), loop => loop with { Flags = flags })).RawValue.Should().Be(12);

    [Fact]
    public void LargeTranslationAndReversedWinding_DoNotCauseCancellationOrNegativeArea()
    {
        var translated = Polygon((1e12, 1e12), (1e12 + 4, 1e12),
            (1e12 + 4, 1e12 + 3), (1e12, 1e12 + 3));
        var reversed = Polygon((1e12, 1e12 + 3), (1e12 + 4, 1e12 + 3),
            (1e12 + 4, 1e12), (1e12, 1e12));

        Recover(translated).RawValue.Should().Be(12);
        Recover(reversed).RawValue.Should().Be(12);
        Recover(translated).Parameters["boundary_sha256"].Should()
            .Be(Recover(translated).Parameters["boundary_sha256"]);
        Recover(translated).Parameters["boundary_sha256"].Should()
            .NotBe(Recover(Square()).Parameters["boundary_sha256"]);
    }

    [Theory]
    [InlineData(4, "Millimeters", 3000, 4000, 12)]
    [InlineData(5, "Centimeters", 300, 400, 12)]
    [InlineData(2, "Feet", 3, 4, 1.11483648)]
    public void NonMetreDrawingArea_IsConvertedToSquareMetresExactlyOnce(
        int code, string sourceName, double height, double width, double expected)
    {
        var units = DrawingUnitPolicy.Resolve(code, sourceName);
        var snapshot = Polygon((0, 0), (width, 0), (width, height), (0, height));
        StrictHatchLinearAreaRecovery.TryRecover(snapshot, units, AreaFailure, null,
            out var measurement, out var refusal).Should().BeTrue(refusal);

        measurement.Should().NotBeNull();
        measurement!.RawValue.Should().BeApproximately(expected, 1e-12);
        double.Parse(measurement.Parameters["boundary_source_area"], CultureInfo.InvariantCulture)
            .Should().Be(width * height);
        measurement.Parameters["source_insunits"].Should().Be(sourceName);
        measurement.Unit.Should().Be(units.AreaUnit);
    }

    [Fact]
    public void UnitlessDrawing_IsRefusedWithoutInventingMetres()
        => AssertRefused(Square(), DrawingUnitPolicy.Resolve(0, "Undefined"));

    [Fact]
    public void BoundsAlreadyInSi_AreRetainedWithoutApplyingDrawingUnitsAgain()
    {
        var siBounds = new[] { 0.0, 0.0, 4.0, 3.0 };
        var source = Polygon((0, 0), (4000, 0), (4000, 3000), (0, 3000));
        StrictHatchLinearAreaRecovery.TryRecover(source, DrawingUnitPolicy.Resolve(4, "Millimeters"),
            AreaFailure, siBounds, out var measurement, out var refusal).Should().BeTrue(refusal);

        measurement!.RawValue.Should().Be(12);
        measurement.GeometryEvidence.Should().Equal(siBounds);
        siBounds.Should().Equal(0, 0, 4, 3);
    }

    [Theory]
    [InlineData("capture-error")]
    [InlineData("capture-truncated")]
    [InlineData("no-declared-count")]
    [InlineData("wrong-declared-count")]
    [InlineData("no-loops")]
    [InlineData("two-loops-with-hole")]
    [InlineData("loop-index-not-zero")]
    [InlineData("loop-error")]
    [InlineData("missing-loop-evidence")]
    [InlineData("loop-truncated")]
    [InlineData("polyline-kind")]
    [InlineData("arc-edge")]
    [InlineData("unexpanded-edge")]
    [InlineData("unparseable-edge")]
    [InlineData("empty-boundary")]
    [InlineData("only-two-edges")]
    public void PartialOrUnsupportedEvidence_IsRefusedWithoutMeasurement(string scenario)
    {
        var original = Square();
        var snapshot = scenario switch
        {
            "capture-error" => original with { Error = "header: InvalidOperationException: failed" },
            "capture-truncated" => original with { Truncated = true },
            "no-declared-count" => original with { DeclaredLoops = null },
            "wrong-declared-count" => original with { DeclaredLoops = 2 },
            "no-loops" => original with { Loops = Array.Empty<HatchAreaFailureDiagnostic.LoopResult>() },
            "two-loops-with-hole" => original with { DeclaredLoops = 2,
                Loops = new[] { original.Loops[0], original.Loops[0] with { Index = 1 } } },
            "loop-index-not-zero" => original with { Loops = new[] { original.Loops[0] with { Index = 1 } } },
            "loop-error" => original with { Loops = new[] { original.Loops[0] with { Error = "eNotApplicable" } } },
            "missing-loop-evidence" => original with { Loops = new[] { original.Loops[0] with { Evidence = null } } },
            "loop-truncated" => WithLoop(original, loop => loop with { Truncated = true }),
            "polyline-kind" => WithLoop(original, loop => loop with { Kind = "polyline" }),
            "arc-edge" => ReplaceEdge(original, 0,
                "arc: start=0,0; end=4,0; centre=2,0; radius=2; angles=0,3.141592653589793; clockwise=False"),
            "unexpanded-edge" => ReplaceEdge(original, 0, "unexpanded-edge=Autodesk.AutoCAD.Geometry.EllipticalArc2d"),
            "unparseable-edge" => ReplaceEdge(original, 0, "line=0,0 -> 4,0 trailing-data"),
            "empty-boundary" => WithLoop(original, loop => loop with { Items = Array.Empty<string>() }),
            "only-two-edges" => Polygon((0, 0), (4, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        AssertRefused(snapshot);
    }

    [Theory]
    [InlineData("External, NotClosed")]
    [InlineData("Textbox")]
    [InlineData("External, Textbox, Outermost")]
    [InlineData("SelfIntersecting")]
    [InlineData("Duplicate")]
    [InlineData("123456")]
    [InlineData("FutureUnknownFlag")]
    [InlineData("")]
    public void UnsafeOrUnknownFlags_AreNeverIgnored(string flags)
        => AssertRefused(WithLoop(Square(), loop => loop with { Flags = flags }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("style=Normal")]
    [InlineData("style=Future; normal=0,0,1; elevation=0")]
    [InlineData("style=Normal; normal=0,1,0; elevation=0")]
    [InlineData("style=Normal; normal=1e-12,0,1; elevation=0")]
    [InlineData("style=Normal; normal=0,0,0.999999999999; elevation=0")]
    [InlineData("style=Normal; normal=0,0,NaN; elevation=0")]
    [InlineData("style=Normal; normal=0,0,1; elevation=Infinity")]
    [InlineData("style=Normal; normal=0,0,1; elevation=NaN")]
    public void MissingNonfiniteOrNonHorizontalHeader_IsRefused(string? header)
        => AssertRefused(Square() with { Header = header });

    [Theory]
    [InlineData("line=1e-12,0 -> 4,0")]
    [InlineData("line=NaN,0 -> 4,0")]
    [InlineData("line=Infinity,0 -> 4,0")]
    [InlineData("line=0,0 -> 0,0")]
    public void ExactClosureAndFiniteNonzeroEdges_AreMandatory(string firstEdge)
        => AssertRefused(ReplaceEdge(Square(), 0, firstEdge));

    [Fact]
    public void TinyInternalGap_IsNotSilentlySnapped()
        => AssertRefused(ReplaceEdge(Square(), 1, "line=4,1e-12 -> 4,3"));

    [Fact]
    public void SelfIntersectionWithNonzeroSignedArea_IsRefused()
        => AssertRefused(Polygon((0, 0), (4, 0), (0, 3), (3, 3)));

    [Fact]
    public void ExactBacktrackAndRepeatedVertex_AreRefused()
        => AssertRefused(Polygon((0, 0), (3, 0), (2, 0), (3, 0), (3, 2), (0, 2)));

    [Fact]
    public void CollinearZeroAreaBoundary_IsRefused()
        => AssertRefused(Polygon((0, 0), (1, 0), (2, 0)));

    [Fact]
    public void AreaOverflow_IsRefusedInsteadOfPublishingInfinity()
        => AssertRefused(Polygon((0, 0), (1e200, 0), (1e200, 1e200), (0, 1e200)));

    [Fact]
    public void Native62PinnedDiagnosticReplay_RecoversExactlySixCompleteLinearBoundaries()
    {
        // Deliberately mandatory operator fixture: absence is failure, never an optional PASS/SKIP.
        File.Exists(FixturePath).Should().BeTrue("the immutable native62 operator fixture is required: {0}", FixturePath);
        var bytes = File.ReadAllBytes(FixturePath);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(FixtureSha256);
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("OmittedByDiagnosticLimit").GetInt32().Should().Be(0);
        var sources = json.RootElement.GetProperty("Sources").EnumerateArray().ToArray();
        sources.Should().HaveCount(54);
        var accepted = new List<string>();
        var refused = new List<string>();
        foreach (var entry in sources)
        {
            var handle = entry.GetProperty("Source").GetProperty("source_handle").GetString()!;
            var snapshot = entry.GetProperty("Boundary").Deserialize<HatchAreaFailureDiagnostic.Snapshot>()!;
            var result = StrictHatchLinearAreaRecovery.TryRecover(snapshot, Metres(), AreaFailure, null,
                out var measurement, out var refusal);
            if (result)
            {
                accepted.Add(handle);
                measurement.Should().NotBeNull(handle);
                measurement!.RawValue.Should().BeGreaterThan(0, handle);
                double.IsFinite(measurement.RawValue).Should().BeTrue(handle);
                measurement.Method.Should().Be("hatch-linear-boundary-area", handle);
                measurement.Parameters["original_area_api_failure"].Should().Be(AreaFailure, handle);
                refusal.Should().BeNullOrEmpty(handle);
            }
            else
            {
                refused.Add(handle);
                measurement.Should().BeNull(handle);
                refusal.Should().NotBeNullOrWhiteSpace(handle);
            }
        }
        accepted.Should().BeEquivalentTo(new[]
        {
            "8BB299/7204", "BD91EF/462E4", "BD91EF/462E6",
            "BD91EF/200401", "BD91EF/200403", "BD91EF/204E68",
        });
        refused.Should().HaveCount(48);
        // A metres scale here is an explicit replay input, not evidence of source DWG INSUNITS.
    }

    [Fact]
    public void SourceOnlyWiring_RecoveryUsesExistingSiThenXrefAreaTransformPipeline()
    {
        // Source-only contract: no Autodesk call or live XREF transform is executed here.
        var directory = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin",
            "CivilDelivery", "Estimate");
        var source = File.ReadAllText(Path.Combine(directory, "CivilQuantityExtractionService.cs"));
        var helper = File.ReadAllText(Path.Combine(directory, "StrictHatchLinearAreaRecovery.cs"));
        source.Should().Contain("bbox = units.Bounds(")
            .And.Contain("StrictHatchLinearAreaRecovery.TryRecover(snapshot, units,")
            .And.Contain("bbox, out var recovered, out var refusal)");
        var natural = source.IndexOf("var measurements = NaturalMeasurements(ent, drawingUnits,", StringComparison.Ordinal);
        var transformed = source.IndexOf("var adjusted = TransformNestedMeasurement(", StringComparison.Ordinal);
        natural.Should().BeGreaterOrEqualTo(0);
        transformed.Should().BeGreaterThan(natural);
        source.Should().Contain("\"area\" => check.LengthScale * check.LengthScale,")
            .And.Contain("var value = measurement.RawValue * factor;")
            .And.Contain("Method = measurement.Method + \"+\" + transformLabel,");
        Regex.Matches(helper, @"units\.Area\(").Count.Should().Be(1,
            "the strict source-plane helper performs precisely one source-unit area conversion");
        helper.Should().NotContain("Matrix3d").And.NotContain("TransformBy(")
            .And.NotContain("LengthScale").And.NotContain("units.Bounds(");
    }

    private static DrawingUnitPolicy.Scale Metres() => DrawingUnitPolicy.Resolve(6, "Meters");

    private static QuantityMeasurement Recover(HatchAreaFailureDiagnostic.Snapshot snapshot)
    {
        StrictHatchLinearAreaRecovery.TryRecover(snapshot, Metres(), AreaFailure, null,
            out var measurement, out var refusal).Should().BeTrue(refusal);
        measurement.Should().NotBeNull();
        refusal.Should().BeNullOrEmpty();
        return measurement!;
    }

    private static void AssertRefused(HatchAreaFailureDiagnostic.Snapshot snapshot, DrawingUnitPolicy.Scale? units = null)
    {
        StrictHatchLinearAreaRecovery.TryRecover(snapshot, units ?? Metres(), AreaFailure, null,
            out var measurement, out var refusal).Should().BeFalse();
        measurement.Should().BeNull();
        refusal.Should().NotBeNullOrWhiteSpace();
    }

    private static HatchAreaFailureDiagnostic.Snapshot Square() => Polygon((0, 0), (4, 0), (4, 3), (0, 3));

    private static HatchAreaFailureDiagnostic.Snapshot Polygon(params (double X, double Y)[] vertices)
    {
        static string Point((double X, double Y) p) =>
            p.X.ToString("G17", CultureInfo.InvariantCulture) + "," + p.Y.ToString("G17", CultureInfo.InvariantCulture);
        var edges = vertices.Select((point, index) =>
            "line=" + Point(point) + " -> " + Point(vertices[(index + 1) % vertices.Length])).ToArray();
        return new("diagnostic-only; original area failure remains blocking; no quantity inferred",
            "raw hatch OCS / source drawing units; not host WCS or square metres", 1,
            "style=Normal; normal=0,0,1; elevation=0",
            new[] { new HatchAreaFailureDiagnostic.LoopResult(0, new("edge-list", "External", edges, false), null) },
            false, null);
    }

    private static HatchAreaFailureDiagnostic.Snapshot WithLoop(HatchAreaFailureDiagnostic.Snapshot snapshot,
        Func<HatchAreaFailureDiagnostic.Loop, HatchAreaFailureDiagnostic.Loop> change)
        => snapshot with { Loops = new[] { snapshot.Loops[0] with { Evidence = change(snapshot.Loops[0].Evidence!) } } };

    private static HatchAreaFailureDiagnostic.Snapshot ReplaceEdge(HatchAreaFailureDiagnostic.Snapshot snapshot,
        int index, string edge)
        => WithLoop(snapshot, loop => loop with { Items = loop.Items.Select((old, i) => i == index ? edge : old).ToArray() });
}
