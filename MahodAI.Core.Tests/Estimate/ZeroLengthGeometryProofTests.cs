using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using V = MahodAI.CivilDelivery.Estimate.ZeroLengthGeometryProof.Vertex;
using K = MahodAI.CivilDelivery.Estimate.ZeroLengthGeometryProof.GeometryKind;

namespace MahodAI.Core.Tests.Estimate;

public sealed class ZeroLengthGeometryProofTests
{
    private static readonly V Same = new(203744.050003, 648371.5599979999, 196.25);
    private static ProvenanceRef Source(string handle = "336457/92207E8") => new()
    {
        SourceKind = "xref", SourcePathOrUri = @"C:\local\MEDVA.dwg",
        DrawingChecksum = "e8c396b44342fd594124880c8e6528e7e954f94c168f1f698cb8dd0433236b9a",
        SourceHandle = handle, Layer = "1506", XrefPath = "MEDVA", EntityType = "Polyline2d",
        MeasurementMethod = "polyline2d-length", RunId = "synthetic-test-only",
    };

    [Theory]
    [InlineData(K.Line)] [InlineData(K.Polyline)] [InlineData(K.Polyline2d)] [InlineData(K.Polyline3d)]
    public void ExactCompleteSimpleZeroRemainsAnUnmeasuredBlockingInput(K kind)
    {
        var source = Source();
        var finding = ZeroLengthGeometryProof.CreateFailure(0, kind, new[] { Same, Same }, true, true, "test", source)!;
        AssertFailure(finding, source);
        var roundtrip = JsonSerializer.Deserialize<DeliveryFinding>(JsonSerializer.Serialize(finding))!;
        roundtrip.Should().BeEquivalentTo(finding);
    }

    [Theory]
    [InlineData("positive-native-length")] [InlineData("negative-native-length")]
    [InlineData("nonfinite-length")] [InlineData("incomplete")] [InlineData("fitted")]
    [InlineData("one-vertex")] [InlineData("null")] [InlineData("near-not-equal")]
    [InlineData("closed-endpoints-only")] [InlineData("arc-bulge")]
    [InlineData("nonfinite-bulge")] [InlineData("nonfinite-x")] [InlineData("nonfinite-y")]
    [InlineData("nonfinite-z")] [InlineData("unknown-kind")] [InlineData("line-three-vertices")]
    [InlineData("over-budget")]
    public void NoDiagnosisFromAZeroNumberOrCoincidentEndpointsAlone(string scenario)
    {
        IReadOnlyList<V>? vertices = new[] { Same, Same };
        double length = 0; var complete = true; var simple = true; var kind = K.Polyline2d;
        switch (scenario)
        {
            case "positive-native-length": length = 2; break;
            case "negative-native-length": length = -1; break;
            case "nonfinite-length": length = double.NaN; break;
            case "incomplete": complete = false; break;
            case "fitted": simple = false; break;
            case "one-vertex": vertices = new[] { Same }; break;
            case "null": vertices = null; break;
            case "near-not-equal": vertices = new[] { Same, Same with { X = Math.BitIncrement(Same.X) } }; break;
            case "closed-endpoints-only": vertices = new[] { Same, Same with { X = Same.X + 1 }, Same }; break;
            case "arc-bulge": vertices = new[] { Same with { Bulge = 1 }, Same }; break;
            case "nonfinite-bulge": vertices = new[] { Same, Same with { Bulge = double.NaN } }; break;
            case "nonfinite-x": vertices = new[] { Same with { X = double.PositiveInfinity }, Same }; break;
            case "nonfinite-y": vertices = new[] { Same with { Y = double.NaN }, Same }; break;
            case "nonfinite-z": vertices = new[] { Same with { Z = double.NegativeInfinity }, Same }; break;
            case "unknown-kind": kind = (K)999; break;
            case "line-three-vertices": kind = K.Line; vertices = new[] { Same, Same, Same }; break;
            case "over-budget": vertices = Enumerable.Repeat(Same, ZeroLengthGeometryProof.MaxVertices + 1).ToArray(); break;
        }
        ZeroLengthGeometryProof.CreateFailure(length, kind, vertices, complete, simple, "test", Source()).Should().BeNull();
    }

    [Fact]
    public void SourcePinnedPartialReaderFixtureProvesAllTwentySixLiteralSequencesButIsNotANativeTest()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var root = fixture.RootElement;
        root.GetProperty("purpose").GetString().Should().Contain("not native");
        root.GetProperty("original_report_sha256").GetString().Should().Be(
            "9F688BE39C1E31B59E1112BEFE693BB430FDC112CEFF0334471D3E6DEACB3D68");
        var observed = new HashSet<string>();
        foreach (var report in root.GetProperty("reports").EnumerateArray())
        {
            var hash = report.GetProperty("sha_before").GetString();
            report.GetProperty("sha_after").GetString().Should().Be(hash);
            report.GetProperty("parser_notice_count").GetInt32().Should().BeGreaterThan(0);
            foreach (var record in report.GetProperty("records").EnumerateArray())
            {
                var handle = report.GetProperty("xref_prefix").GetString() + "/" + record.GetProperty("handle").GetString();
                observed.Add(handle).Should().BeTrue();
                record.GetProperty("closed").GetBoolean().Should().BeFalse();
                var vertices = record.GetProperty("vertices").EnumerateArray().Select(vertex =>
                {
                    var xyz = vertex.GetProperty("xyz");
                    return new V(xyz[0].GetDouble(), xyz[1].GetDouble(), xyz[2].GetDouble(), vertex.GetProperty("bulge").GetDouble());
                }).ToArray();
                vertices.Should().HaveCount(2);
                var source = new ProvenanceRef
                {
                    SourceKind = "xref", SourceHandle = handle,
                    SourcePathOrUri = report.GetProperty("path").GetString(), DrawingChecksum = hash,
                    Layer = record.GetProperty("layer").GetString(), EntityType = record.GetProperty("type").GetString(),
                    XrefPath = handle.StartsWith("336457/") ? "MEDVA" : "UT-3D",
                    MeasurementMethod = record.GetProperty("type").GetString() == "Polyline2D" ? "polyline2d-length" : "polyline3d-length",
                };
                var kind = source.EntityType == "Polyline2D" ? K.Polyline2d : K.Polyline3d;
                var failure = ZeroLengthGeometryProof.CreateFailure(0, kind, vertices, true, true, "6422", source)!;
                AssertFailure(failure, source);
            }
        }
        observed.Should().BeEquivalentTo((
            "336457/92207E8,336457/922199A,336457/9223DD9,336457/922463C,336457/9224640,336457/9224644," +
            "336457/9224D18,336457/922ABD0,336457/922B14C,336457/9236C95,336457/9238535,336457/9238B90," +
            "336457/923A963,336457/923D40B,336457/923D450,336457/923E443,336457/923E451," +
            "8776AD/4538,8776AD/4817,8776AD/4D55,8776AD/50C2,8776AD/6D79,8776AD/6EFA,8776AD/6EFF,8776AD/6F2E,8776AD/6F4A").Split(','));
    }

    [Fact]
    public void RealGmZeroLineDoesNotCreateAZeroEstimateLineOrDisappearAtTheBuildBoundary()
    {
        // Pinned GM FAD0... source inventory: 10A971 has these identical endpoints.
        var point = new V(203793.83774996753, 648495.0686701841, 0);
        var source = new ProvenanceRef
        {
            SourceKind = "xref", SourceHandle = "8BB552/10A971",
            SourcePathOrUri = @"C:\local\6422-GM-MODEL-NATAZ.dwg",
            DrawingChecksum = "fad0acaca3b064f55e53cc20476b30acf5542bcaa2f7e4cc462b45b393d88b90",
            XrefPath = "GM", EntityType = "Line", Layer = "TR-ISLAND-CURBSTONE", MeasurementMethod = "line-length",
        };
        var failure = ZeroLengthGeometryProof.CreateFailure(0, K.Line, new[] { point, point }, true, true, "test", source)!;
        var built = EstimateBuilder.Build(Array.Empty<NeutralQuantityRecord>(),
            new CatalogSnapshot { SnapshotId = "synthetic-empty", FileHash = new string('a', 64) },
            new ProjectProfile { ProfileId = "test" }, preflightFindings: new[] { failure });
        built.Lines.Should().BeEmpty();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        EstimatePreflightPolicy.BlockingFindings(built).Should().Contain(failure);
        failure.AffectedRecordIds.Should().BeEmpty();
    }

    private static void AssertFailure(DeliveryFinding failure, ProvenanceRef source)
    {
        failure.Should().NotBeNull(); failure.Code.Should().Be(EstimateFindingCodes.MeasurementFailed);
        failure.Severity.Should().Be(FindingSeverity.Error); failure.AffectedRecordIds.Should().BeEmpty();
        failure.SourceRefs.Should().ContainSingle().Which.Should().BeSameAs(source);
        failure.Message.Should().Contain(ZeroLengthGeometryProof.Contract).And.Contain(source.SourceHandle!)
            .And.Contain(source.Layer!).And.Contain(source.DrawingChecksum!);
        failure.Resolution.Should().BeNull(); failure.ResolvedAtUtc.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(failure).Should().BeTrue();
    }

    private static string FixturePath([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "Fixtures", "zero_geometry_native67_source_partial.json"));
}
