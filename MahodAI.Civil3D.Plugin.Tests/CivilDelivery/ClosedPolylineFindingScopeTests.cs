using System;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ClosedPolylineFindingScopeTests
{
    private const string HostPath = @"C:\Users\arthurf\MahodCivilDelivery_Work\6422-local-mirror\Civil3d\PD\6422-CIVIL-WEST-WORK.run1-2applied.localtest.dwg";
    private const string HostHash = "83cd003c69f11c227a2e195701da0034a975d8d12d87dd5a63a8e595bf205b81";
    private const string AreaKey = "layer:HW-CS-TABL|area";
    private const string LengthKey = "layer:HW-CS-TABL|length";

    private static NeutralQuantityRecord Record(string handle, string kind, string? method = null,
        string path = HostPath, string hash = HostHash, string? xref = null, double? value = null,
        string? id = null, string? key = null) => new()
    {
        RecordId = id ?? $"q-disc-{handle.Replace('/', '-')}-{kind}", ProjectProfileId = "6422",
        RunId = "estimate-extract-20260909-092731-3aab4b94",
        Source = new()
        {
            Drawing = "fixture.dwg", DrawingPath = path, DrawingHash = hash, Handle = handle,
            EntityType = "POLYLINE", Layer = "HW-CS-TABL", Xref = xref,
        },
        Measurement = new()
        {
            Kind = kind, Method = method ?? (kind == "area" ? "closed-polyline-area" : "closed-polyline-perimeter"),
            RawValue = value ?? (kind == "area" ? 240 : 128), Unit = kind == "area" ? "מ\"ר" : "מטר",
        },
        Classification = new() { RuleKey = key ?? (kind == "area" ? AreaKey : LengthKey) },
        Status = DeliveryStatus.ReviewRequired,
    };

    private static Action Begin(CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes scopes,
        string handle = "850C64", string path = HostPath, string hash = HostHash, string? xref = null) =>
        scopes.Begin("6422", "HW-CS-TABL", AreaKey, LengthKey, path, hash, handle, xref);

    private static void EmitPair(CivilQuantityExtractionService.ExtractionResult result,
        CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes scopes, string handle = "850C64",
        string path = HostPath, string hash = HostHash, string? xref = null)
    {
        var complete = Begin(scopes, handle, path, hash, xref);
        result.Records.Add(Record(handle, "area", path: path, hash: hash, xref: xref));
        result.Records.Add(Record(handle, "length", path: path, hash: hash, xref: xref));
        complete();
    }

    [Fact]
    public void NativeHwCsTablBoundaryScopesExactlyTwoRecordsNotTheSeparateOpenPolyline()
    {
        // Exact native neutral records at lines 7206144, 7206251 and 7206358:
        // handle850C64 area240/perimeter128; handle850C65 open length60.
        // This fixture does not approve or classify the layer as procurement/noise.
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        EmitPair(result, scopes);
        result.Records.Add(Record("850C65", "length", "polyline-length", value: 60));
        result.Findings.Single().AffectedRecordIds.Should().BeEmpty("publication waits for traversal completion");
        scopes.Publish();

        var finding = result.Findings.Single();
        finding.Code.Should().Be(EstimateFindingCodes.MixedDimensionLayer);
        finding.Severity.Should().Be(FindingSeverity.ReviewRequired);
        finding.AffectedRecordIds.Should().Equal("q-disc-850C64-area", "q-disc-850C64-length");
        finding.ResolvedAtUtc.Should().BeNull(); EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        result.Records.Select(record => record.Measurement.RawValue).Should().Equal(240, 128, 60);
    }

    [Fact]
    public void CorePropagationBlocksTheProvedPairButNotAnUnrelatedLineWithoutClaimingExportReady()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        EmitPair(result, scopes); scopes.Publish();
        EstimateResult PricedFixture() => new()
        {
            RunId = "SIMULATION-ONLY", ProjectProfileId = "6422",
            Lines = new[] { "q-disc-850C64-area", "q-disc-850C64-length", "q-disc-850C65-length" }
                .Select(id => new EstimateLine
                {
                    LineId = "line-" + id, RecordId = id, RawQuantity = 1, BoqQuantity = 1,
                    Price = 10m, Total = 10m, IncludedInTotals = true, Status = DeliveryStatus.Ready,
                    PriceStatus = PriceStatus.Priced,
                }).ToList(),
        };
        var old = PricedFixture();
        EstimatePreflightPolicy.Apply(old, new[] { new DeliveryFinding
        {
            Code = EstimateFindingCodes.MixedDimensionLayer, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
            Title = "Unscoped recorded boundary ambiguity",
        } });
        old.Lines.Should().OnlyContain(line => line.Total == null && !line.IncludedInTotals);

        var current = PricedFixture();
        EstimatePreflightPolicy.Apply(current, result.Findings);
        current.Lines.Take(2).Should().OnlyContain(line => line.Total == null && !line.IncludedInTotals);
        current.Lines[2].Total.Should().Be(10m); current.Lines[2].IncludedInTotals.Should().BeTrue();
        EstimatePreflightPolicy.CanExport(current).Should().BeFalse();
    }

    [Fact]
    public void MultipleBoundariesOnSameLayerAggregateEveryPairWithoutDuplicatingTheFinding()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        EmitPair(result, scopes, "850C64"); EmitPair(result, scopes, "850C66"); scopes.Publish(); scopes.Publish();
        result.Findings.Should().ContainSingle().Which.AffectedRecordIds.Should().Equal(
            "q-disc-850C64-area", "q-disc-850C64-length", "q-disc-850C66-area", "q-disc-850C66-length");
    }

    [Fact]
    public void TransformedXrefInstancesUseFullHandlePathNotSharedLeafHandleOrLayer()
    {
        const string path = @"C:\local\design.dwg";
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        foreach (var instance in new[] { "A1", "A2" })
        {
            var handle = instance + "/850C64";
            var complete = Begin(scopes, handle, path, HostHash, instance);
            result.Records.Add(Record(handle, "area", "closed-polyline-area+xref-transform", path, HostHash, instance, 960));
            result.Records.Add(Record(handle, "length", "closed-polyline-perimeter+xref-transform", path, HostHash, instance, 256));
            complete();
        }
        result.Records.Add(Record("A3/850C64", "length", "polyline-length+xref-transform", path, HostHash, "A3", 120));
        scopes.Publish();
        result.Findings.Single().AffectedRecordIds.Should().Equal(
            "q-disc-A1-850C64-area", "q-disc-A1-850C64-length", "q-disc-A2-850C64-area", "q-disc-A2-850C64-length");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void IncompleteOrUnexpectedEmissionRetainsGlobalScope(int count)
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        var complete = Begin(scopes);
        if (count > 0) result.Records.Add(Record("850C64", "area"));
        if (count > 1) result.Records.Add(Record("850C64", "length"));
        if (count > 2) result.Records.Add(Record("850C65", "length", "polyline-length"));
        complete(); scopes.Publish();
        result.Findings.Single().AffectedRecordIds.Should().BeEmpty();
        EstimatePreflightPolicy.IsBlocking(result.Findings.Single()).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FailedBoundaryCannotBeMaskedBySuccessfulSameLayerBoundary(bool failFirst)
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        void Fail()
        {
            var complete = Begin(scopes, "FAILED");
            result.Records.Add(Record("FAILED", "area")); complete();
        }
        if (failFirst) Fail();
        EmitPair(result, scopes);
        if (!failFirst) Fail();
        scopes.Publish(); result.Findings.Single().AffectedRecordIds.Should().BeEmpty();
    }

    [Fact]
    public void ExceptionBeforeCompletionLeavesFindingGlobalEvenIfTwoRecordsWereAppended()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        _ = Begin(scopes);
        result.Records.Add(Record("850C64", "area")); result.Records.Add(Record("850C64", "length"));
        // No completion callback: models an exception before successful emission returns.
        scopes.Publish(); result.Findings.Single().AffectedRecordIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("drawing")]
    [InlineData("hash")]
    [InlineData("handle")]
    [InlineData("xref")]
    [InlineData("record-id")]
    [InlineData("rule-key")]
    [InlineData("not-closed")]
    [InlineData("invalid-value")]
    public void UnprovenPairIdentityCannotNarrowTheFinding(string failure)
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        var complete = Begin(scopes);
        result.Records.Add(Record("850C64", "area"));
        result.Records.Add(Record(failure == "handle" ? "OTHER" : "850C64", "length",
            method: failure == "not-closed" ? "polyline-length" : null,
            path: failure == "drawing" ? @"C:\different.dwg" : HostPath,
            hash: failure == "hash" ? new string('b', 64) : HostHash,
            xref: failure == "xref" ? "OTHER-XREF" : null,
            value: failure == "invalid-value" ? 0 : null,
            id: failure == "record-id" ? "OTHER-ID" : null,
            key: failure == "rule-key" ? "another-rule" : null));
        complete(); scopes.Publish(); result.Findings.Single().AffectedRecordIds.Should().BeEmpty();
    }

    [Fact]
    public void OtherGlobalAndLayerFindingsRemainExactlyAsRecorded()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var global = new DeliveryFinding { Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error, Title = "Other source failure" };
        var layer = new DeliveryFinding
        {
            FindingId = "9ca72f311c2243b78fd1ce8b8186a421", Code = EstimateFindingCodes.MixedDimensionLayer,
            Domain = "estimate", Severity = FindingSeverity.ReviewRequired, Title = "Recorded whole-layer dimension ambiguity",
            AffectedRecordIds = { "q-disc-850C64-area", "q-disc-850C64-length", "q-disc-850C65-length" },
        };
        result.Findings.Add(global); result.Findings.Add(layer);
        var scopes = new CivilQuantityExtractionService.ClosedPolylineAmbiguityScopes(result);
        EmitPair(result, scopes); scopes.Publish();
        result.Findings.Should().HaveCount(3);
        result.Findings[0].Should().BeSameAs(global); global.AffectedRecordIds.Should().BeEmpty();
        result.Findings[1].Should().BeSameAs(layer);
        layer.AffectedRecordIds.Should().Equal("q-disc-850C64-area", "q-disc-850C64-length", "q-disc-850C65-length");
        EstimatePreflightPolicy.IsBlocking(global).Should().BeTrue(); EstimatePreflightPolicy.IsBlocking(layer).Should().BeTrue();
    }
}
