using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MeasurementFailureProvenanceTests
{
    private const string HaHash = "8f2469edd9f9d25fcc09c5563fac1c2bd9b3ce17a9b06d1e9dae032cf8175315";

    private static ProvenanceRef Source(string handle = "8BB299/26236C", string? xref = "6422-HA-MODEL-NATAZ",
        string method = "hatch-area", string? path = @"C:\local\6422-HA-MODEL-NATAZ.dwg") => new()
    {
        SourceKind = xref == null ? "drawing" : "xref", SourcePathOrUri = path, DrawingChecksum = HaHash,
        SourceHandle = handle, XrefPath = xref, EntityType = "Hatch", Layer = "HW_HA_SIDEWALK",
        MeasurementMethod = method, ToolVersion = "civil-delivery/TEST", RunId = "test-failure-provenance",
    };

    [Theory]
    [InlineData("area", "hatch-area", "eNotApplicable")]
    [InlineData("length", "polyline-length+xref-transform", "raw=0; factor=1")]
    [InlineData("count", "geometric-extents", "eNullExtents")]
    public void CapturedSourceAndDimensionRemainLocatableButNeverBecomeQuantityOrApproval(
        string kind, string method, string reason)
    {
        var source = Source(method: method);
        var result = MeasurementFailureProvenance.Create("6422", "Original failure title", reason, source, kind);

        result.SourceRefs.Should().ContainSingle().Which.Should().BeSameAs(source);
        result.Title.Should().Be("Original failure title");
        result.Message.Should().StartWith(reason).And.Contain("measurement-kind=" + kind)
            .And.Contain("handle=8BB299/26236C").And.Contain("layer=HW_HA_SIDEWALK")
            .And.Contain("xref=6422-HA-MODEL-NATAZ").And.Contain("method=" + method).And.Contain(HaHash);
        result.RunId.Should().Be(source.RunId);
        result.Severity.Should().Be(FindingSeverity.Error);
        result.AffectedRecordIds.Should().BeEmpty(); result.SourceBoundsWcs.Should().BeNull();
        result.Resolution.Should().BeNull(); result.ResolvedBy.Should().BeNull(); result.ResolvedAtUtc.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(result).Should().BeTrue();
    }

    [Fact]
    public void MissingSourceStaysExplicitlyUnlocatedInsteadOfInventingAHandleOrHash()
    {
        var result = MeasurementFailureProvenance.Create("6422", "Failed object", "original native error");
        result.Message.Should().Be("original native error"); result.SourceRefs.Should().BeEmpty();
        result.AffectedRecordIds.Should().BeEmpty(); result.RunId.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(result).Should().BeTrue();
    }

    [Fact]
    public void ReaderFailureDoesNotInstructAnEngineerToRepairUnprovenSourceDamage()
    {
        foreach (var source in new ProvenanceRef?[] { Source("8BB299/7202"), null })
        {
            var result = MeasurementFailureProvenance.Create("6422", "Unmeasured Hatch",
                "area enclosure exceeds reader precision", source, "area");
            result.RecommendedAction.Should().Contain("אינו הוכחה שהגאומטריה פגומה")
                .And.Contain("מגבלת קורא").And.Contain("לא נקבעו כמות או החרגה");
            EstimatePreflightPolicy.IsBlocking(result).Should().BeTrue();
            result.Resolution.Should().BeNull();
        }
    }

    [Fact]
    public void SameLeafHandleInDistinctInstancesRetainsFullNestedIdentity()
    {
        var left = MeasurementFailureProvenance.Create("6422", "Hatch 26236C", "eNotApplicable",
            Source("8BB299/AB12/26236C", "HA > BLOCK-A"), "area");
        var right = MeasurementFailureProvenance.Create("6422", "Hatch 26236C", "eNotApplicable",
            Source("BD91EF/AB12/26236C", "SM > BLOCK-A", path: @"C:\local\SM.dwg"), "area");
        left.SourceRefs[0].SourceHandle.Should().NotBe(right.SourceRefs[0].SourceHandle);
        left.SourceRefs[0].SourcePathOrUri.Should().NotBe(right.SourceRefs[0].SourcePathOrUri);
        right.Message.Should().Contain("xref=SM > BLOCK-A");
        left.AffectedRecordIds.Should().BeEmpty(); right.AffectedRecordIds.Should().BeEmpty();
    }

    [Fact]
    public void HostIdentityAndExactSourceStringsSurviveJsonWithoutTruncation()
    {
        var source = Source("505C8FE", null, path: @"C:\מקומי\host.dwg");
        var result = MeasurementFailureProvenance.Create("6422", "Hatch", "native\ninner error", source, "area");
        var roundtrip = JsonSerializer.Deserialize<DeliveryFinding>(JsonSerializer.Serialize(result))!;
        roundtrip.SourceRefs[0].Should().BeEquivalentTo(source);
        roundtrip.Message.Should().Be(result.Message).And.Contain("xref=(host)");
        roundtrip.AffectedRecordIds.Should().BeEmpty();
    }

    [Fact]
    public void RealHa26236CFixtureHasAnUnclosedBoundaryAndStillProducesOnlyFailureEvidence()
    {
        using var fixture = HaFixture();
        var root = fixture.RootElement;
        var record = root.GetProperty("records").EnumerateArray().Single(r => r.GetProperty("handle").GetString() == "26236C");
        var loop = record.GetProperty("loops")[8];
        loop.GetProperty("flags").GetString().Should().Contain("NotClosed");
        var edges = loop.GetProperty("edges");
        var first = edges[0].GetProperty("start");
        var last = edges[edges.GetArrayLength() - 1].GetProperty("end");
        var dx = first[0].GetDouble() - last[0].GetDouble();
        var dy = first[1].GetDouble() - last[1].GetDouble();
        Math.Sqrt(dx * dx + dy * dy).Should().BeGreaterThan(10,
            "native57's failing HA object cannot become an area by silently closing its final source gap");
        var source = new ProvenanceRef
        {
            SourceKind = "xref", SourcePathOrUri = root.GetProperty("source_path").GetString(),
            DrawingChecksum = root.GetProperty("source_sha256_before").GetString(),
            SourceHandle = "8BB299/" + record.GetProperty("handle").GetString(),
            XrefPath = "6422-HA-MODEL-NATAZ", EntityType = record.GetProperty("type").GetString(),
            Layer = record.GetProperty("layer").GetString(), MeasurementMethod = "hatch-area",
        };
        var failure = MeasurementFailureProvenance.Create("6422",
            "Failed to measure supported entity Hatch 26236C", "eNotApplicable", source, "area");
        failure.SourceRefs[0].DrawingChecksum.Should().BeEquivalentTo(HaHash);
        failure.SourceRefs[0].SourceHandle.Should().Be("8BB299/26236C");
        failure.AffectedRecordIds.Should().BeEmpty();
        EstimatePreflightPolicy.IsBlocking(failure).Should().BeTrue();
    }

    private static JsonDocument HaFixture([CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "fixtures",
            "section-hatch-060926", "ha-27-failed-hatches.acadsharp-partial.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be("F2141DA8DF1675E8E5E68E27744550DF8892E3C5A693B97B11B5AEBB1F2B013C");
        var result = JsonDocument.Parse(bytes);
        result.RootElement.GetProperty("source_sha256_before").GetString().Should().BeEquivalentTo(HaHash);
        result.RootElement.GetProperty("source_sha256_after").GetString().Should().BeEquivalentTo(HaHash);
        return result;
    }
}
