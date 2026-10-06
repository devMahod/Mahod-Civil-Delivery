using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Pure emission-evidence tests. No native object access, DWG read, mapping or price approval.</summary>
public sealed class ExtentsEvidenceScopeTests
{
    private const string ProfileId = "6422";
    private const string DrawingPath = @"C:\Users\arthurf\MahodCivilDelivery_Work\6422-local-mirror\Drawing\Data Files\HW\PD\6422-HA-MODEL-NATAZ.dwg";
    private const string DrawingHash = "5c23ec4aef9ecc3a6a3cf75edab8b9adca60b7344aa941acf1eb9b1186290539";
    private const string HandlePath = "8BB299/7788";
    private const string Xref = "6422-HA-MODEL-NATAZ";

    private static QuantityMeasurement Measurement(string method = "block-count+xref-transform") => new()
    {
        Kind = "count", Method = method, RawValue = 1, Unit = "יח'", GeometryEvidence = null,
        Parameters = { ["block_name"] = "*U2732" },
    };

    private static DeliveryFinding Failure(string suffix = "native-HA") => new()
    {
        Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
        Title = "Geometric extents unavailable: " + suffix,
        Message = "Unresolved extent failure detail: " + suffix,
        ProjectProfileId = ProfileId, EvidenceRefs = { "SIMULATION-ONLY/extents/" + suffix },
    };

    private static NeutralQuantityRecord Record(QuantityMeasurement measurement,
        string handle = HandlePath, string? mismatch = null) => new()
    {
        // Values mirror native 092731 estimate_scan.json:7409874; this is not a new native measurement.
        RecordId = mismatch == "record-id" ? "q-disc-WRONG-count" : $"q-disc-{handle.Replace('/', '-')}-count",
        ProjectProfileId = mismatch == "profile" ? "OTHER-PROFILE" : ProfileId,
        RunId = "estimate-extract-20260909-092731-3aab4b94",
        Source = new()
        {
            Drawing = "6422-HA-MODEL-NATAZ.dwg",
            DrawingPath = mismatch == "source-path" ? @"C:\different\source.dwg" : DrawingPath,
            DrawingHash = mismatch == "source-hash" ? new string('b', 64) : DrawingHash,
            Handle = mismatch == "source-handle" ? "OTHER/7788" : handle,
            Xref = mismatch == "source-xref" ? "OTHER-XREF" : Xref,
            EntityType = "BLOCKREFERENCE", Layer = "0",
        },
        Measurement = measurement,
        Classification = new() { RuleKey = "layer:0|count|block:%2AU2732" },
        Provenance = mismatch == "missing-provenance" ? null : new()
        {
            SourceKind = "xref",
            SourcePathOrUri = mismatch == "provenance-path" ? @"C:\different\source.dwg" : DrawingPath,
            DrawingChecksum = mismatch == "provenance-hash" ? new string('b', 64) : DrawingHash,
            SourceHandle = mismatch == "provenance-handle" ? "OTHER/7788" : handle,
            XrefPath = mismatch == "provenance-xref" ? "OTHER-XREF" : Xref,
            EntityType = "BlockReference", Layer = "0", MeasurementMethod = measurement.Method,
        },
        Status = DeliveryStatus.Ready,
    };

    private static CivilQuantityExtractionService.ExtentsEvidenceScopes Scopes(
        CivilQuantityExtractionService.ExtractionResult result, string handle = HandlePath) =>
        new(result, ProfileId, DrawingPath, DrawingHash, handle, Xref);

    private static void Capture(CivilQuantityExtractionService.ExtentsEvidenceScopes scopes,
        CivilQuantityExtractionService.ExtractionResult result, QuantityMeasurement measurement, DeliveryFinding failure)
    {
        // Native measurement may already have published the failure before successful measurement returns.
        if (!result.Findings.Contains(failure)) result.Findings.Add(failure);
        scopes.Capture(measurement, failure);
    }

    private static void AssertGlobal(DeliveryFinding failure, NeutralQuantityRecord record)
    {
        failure.AffectedRecordIds.Should().BeEmpty();
        failure.Severity.Should().Be(FindingSeverity.Error); failure.ResolvedAtUtc.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(failure).Should().BeTrue();
        record.Findings.Should().NotContain(failure);
        record.Measurement.GeometryEvidence.Should().BeNull();
    }

    [Fact]
    public void NativeHaCountKeepsMissingBoundsAndUnresolvedErrorButScopesOnlyAfterExactEmission()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult(); var scopes = Scopes(result);
        var measurement = Measurement(); var failure = Failure(); var record = Record(measurement);
        Capture(scopes, result, measurement, failure);
        failure.AffectedRecordIds.Should().BeEmpty(); record.Findings.Should().BeEmpty();
        result.Records.Add(record); scopes.BindEmitted(record);

        failure.AffectedRecordIds.Should().Equal("q-disc-8BB299-7788-count");
        result.Findings.Should().ContainSingle().Which.Should().BeSameAs(failure);
        record.Findings.Should().ContainSingle().Which.Should().BeSameAs(failure);
        failure.Severity.Should().Be(FindingSeverity.Error); failure.ResolvedAtUtc.Should().BeNull();
        failure.Resolution.Should().BeNull(); failure.ResolvedBy.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(failure).Should().BeTrue();
        record.Status.Should().Be(DeliveryStatus.Failed);
        record.Measurement.Should().BeSameAs(measurement);
        record.Measurement.RawValue.Should().Be(1); record.Measurement.Unit.Should().Be("יח'");
        record.Measurement.GeometryEvidence.Should().BeNull();
        record.Classification.CandidateCatalogCode.Should().BeNull();
        record.Provenance!.SourceHandle.Should().Be(HandlePath);
        failure.SourceRefs.Should().ContainSingle().Which.Should().BeSameAs(record.Provenance);

        scopes.BindEmitted(record);
        failure.AffectedRecordIds.Should().ContainSingle(); record.Findings.Should().ContainSingle();
    }

    [Fact]
    public void XrefTransferBindsTransformedMeasurementWithoutInventingBoundsOrChangingCount()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult(); var scopes = Scopes(result);
        var original = Measurement("block-count"); var transformed = Measurement(); var failure = Failure();
        Capture(scopes, result, original, failure); scopes.Transfer(original, transformed);
        var record = Record(transformed); result.Records.Add(record); scopes.BindEmitted(record);
        failure.AffectedRecordIds.Should().Equal(record.RecordId);
        record.Findings.Should().ContainSingle().Which.Should().BeSameAs(failure);
        record.Measurement.Should().BeSameAs(transformed); record.Measurement.RawValue.Should().Be(1);
        original.GeometryEvidence.Should().BeNull(); transformed.GeometryEvidence.Should().BeNull();
        record.Status.Should().Be(DeliveryStatus.Failed);
    }

    [Fact]
    public void EqualValuedButUntransferredMeasurementCannotNarrowFailure()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult(); var scopes = Scopes(result);
        var original = Measurement(); var failure = Failure(); Capture(scopes, result, original, failure);
        var record = Record(Measurement()); result.Records.Add(record); scopes.BindEmitted(record);
        AssertGlobal(failure, record);
    }

    [Theory]
    [InlineData("source-path")]
    [InlineData("source-hash")]
    [InlineData("source-handle")]
    [InlineData("source-xref")]
    [InlineData("profile")]
    [InlineData("record-id")]
    [InlineData("missing-provenance")]
    [InlineData("provenance-path")]
    [InlineData("provenance-hash")]
    [InlineData("provenance-handle")]
    [InlineData("provenance-xref")]
    public void AnyUnprovedSourceOrProvenanceIdentityKeepsFailureGlobal(string mismatch)
    {
        var result = new CivilQuantityExtractionService.ExtractionResult(); var scopes = Scopes(result);
        var measurement = Measurement(); var failure = Failure(); Capture(scopes, result, measurement, failure);
        var record = Record(measurement, mismatch: mismatch); result.Records.Add(record); scopes.BindEmitted(record);
        AssertGlobal(failure, record);
    }

    [Theory]
    [InlineData("not-emitted")]
    [InlineData("different-instance")]
    [InlineData("not-last")]
    [InlineData("no-capture")]
    public void MissingExactEmissionOrCaptureKeepsFailureGlobal(string scenario)
    {
        var result = new CivilQuantityExtractionService.ExtractionResult(); var scopes = Scopes(result);
        var measurement = Measurement(); var failure = Failure(); var record = Record(measurement);
        result.Findings.Add(failure);
        if (scenario != "no-capture") scopes.Capture(measurement, failure);
        if (scenario == "different-instance") result.Records.Add(Record(measurement));
        else if (scenario != "not-emitted") result.Records.Add(record);
        if (scenario == "not-last") result.Records.Add(Record(Measurement(), "OTHER/7788"));
        scopes.BindEmitted(record); AssertGlobal(failure, record);
    }

    [Fact]
    public void FailedNaturalMeasurementWithoutCaptureOrRecordRemainsGlobalAfterConsolidation()
    {
        // A null native measurement never reaches Capture or record emission. The original
        // error must survive even though there is no record to carry an affected-record ID.
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var failure = Failure("null-natural-measurement"); result.Findings.Add(failure);
        _ = Scopes(result);
        CivilQuantityExtractionService.ConsolidateMeasurementFailures(result, ProfileId);
        result.Records.Should().BeEmpty();
        var aggregate = result.Findings.Should().ContainSingle().Which;
        aggregate.AffectedRecordIds.Should().BeEmpty();
        aggregate.Message.Should().Contain(failure.Title).And.Contain(failure.Message);
        aggregate.Severity.Should().Be(FindingSeverity.Error); aggregate.ResolvedAtUtc.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(aggregate).Should().BeTrue();
    }

    [Fact]
    public void ConsolidationNeverUnionsSeventeenScopedExtentFailuresWithSeventyThreeGlobalFailures()
    {
        var result = new CivilQuantityExtractionService.ExtractionResult();
        var scopedFailures = new List<DeliveryFinding>(); var globalFailures = new List<DeliveryFinding>();
        for (var index = 0; index < 17; index++)
        {
            var handle = $"8BB299/{0x7788 + index:X}"; var scopes = Scopes(result, handle);
            var measurement = Measurement(); var failure = Failure($"scoped-{index:000}");
            Capture(scopes, result, measurement, failure);
            var record = Record(measurement, handle); result.Records.Add(record); scopes.BindEmitted(record);
            scopedFailures.Add(failure);
        }
        for (var index = 0; index < 73; index++)
        {
            var failure = Failure($"global-{index:000}"); globalFailures.Add(failure); result.Findings.Add(failure);
        }
        var other = new DeliveryFinding
        {
            Code = EstimateFindingCodes.SourceMissing, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "Separate unresolved source failure must remain unchanged",
        };
        result.Findings.Add(other);
        CivilQuantityExtractionService.ConsolidateMeasurementFailures(result, ProfileId);

        var aggregates = result.Findings.Where(finding => finding.Code == EstimateFindingCodes.MeasurementFailed).ToArray();
        aggregates.Should().HaveCount(2); result.Findings.Should().HaveCount(3);
        result.Findings.Should().Contain(other);
        var scoped = aggregates.Single(finding => finding.AffectedRecordIds.Count != 0);
        var global = aggregates.Single(finding => finding.AffectedRecordIds.Count == 0);
        scoped.AffectedRecordIds.Should().BeEquivalentTo(result.Records.Select(record => record.RecordId));
        scoped.AffectedRecordIds.Should().HaveCount(17);
        scoped.SourceRefs.Should().BeEquivalentTo(result.Records.Select(record => record.Provenance));
        scoped.Message.Should().StartWith("Complete measurement failures (17):\n");
        global.Message.Should().StartWith("Complete measurement failures (73):\n");
        foreach (var pair in new[] { (Aggregate: scoped, Originals: scopedFailures), (Aggregate: global, Originals: globalFailures) })
        {
            pair.Aggregate.Severity.Should().Be(FindingSeverity.Error);
            pair.Aggregate.ResolvedAtUtc.Should().BeNull(); EstimatePreflightPolicy.IsBlocking(pair.Aggregate).Should().BeTrue();
            foreach (var failure in pair.Originals)
            {
                pair.Aggregate.Message.Should().Contain(failure.Title).And.Contain(failure.Message);
                pair.Aggregate.EvidenceRefs.Should().Contain(failure.EvidenceRefs.Single());
            }
        }
        scoped.Message.Should().NotContain("global-"); global.Message.Should().NotContain("scoped-");
        for (var index = 0; index < result.Records.Count; index++)
        {
            result.Records[index].Findings.Should().ContainSingle().Which.Should().BeSameAs(scopedFailures[index]);
            result.Records[index].Status.Should().Be(DeliveryStatus.Failed);
            result.Records[index].Measurement.GeometryEvidence.Should().BeNull();
        }
    }

    [Fact]
    public void NativeBuildConsumerExcludesSeventeenScopedRecordsEvenWithValidMappingAndPrice()
    {
        var fixture = ConsumerFixture(includeGlobalFailures: false);
        // The native Workflow.Build forwards scan.Findings as preflight; passing
        // records alone would not exercise this actual consumer contract.
        var built = EstimateBuilder.Build(fixture.Extraction.Records, fixture.Catalog,
            fixture.Profile, preflightFindings: fixture.Extraction.Findings);

        var affected = built.Lines.Where(line => line.RecordId != fixture.GoodRecordId).ToArray();
        affected.Should().HaveCount(17);
        affected.Should().OnlyContain(line => line.PriceStatus == PriceStatus.Priced && line.Price == 12.50m,
            "missing mapping or price must not be the reason these rows are excluded");
        affected.Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null && line.Status == DeliveryStatus.Failed);
        foreach (var line in affected)
            line.Findings.Should().Contain(finding => finding.Code == EstimateFindingCodes.MeasurementFailed &&
                finding.AffectedRecordIds.Contains(line.RecordId));
        var unaffected = built.Lines.Single(line => line.RecordId == fixture.GoodRecordId);
        unaffected.IncludedInTotals.Should().BeTrue(); unaffected.Total.Should().Be(12.50m);
        built.CleanTotal.Should().Be(12.50m, "none of the 17 failed spatial proofs may contribute money");
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        EstimatePreflightPolicy.ExportBlockingReasons(built).Should().Contain(EstimateFindingCodes.MeasurementFailed);
    }

    [Fact]
    public void NativeBuildConsumerStillAppliesSeventyThreeGlobalFailuresToEveryLine()
    {
        var fixture = ConsumerFixture(includeGlobalFailures: true);
        var built = EstimateBuilder.Build(fixture.Extraction.Records, fixture.Catalog,
            fixture.Profile, preflightFindings: fixture.Extraction.Findings);
        built.Lines.Should().HaveCount(18);
        built.Lines.Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null && line.Status == DeliveryStatus.Failed);
        foreach (var line in built.Lines)
            line.Findings.Should().Contain(finding => finding.Code == EstimateFindingCodes.MeasurementFailed &&
                finding.AffectedRecordIds.Count == 0 && finding.Message.StartsWith("Complete measurement failures (73):"));
        built.CleanTotal.Should().Be(0m);
        var global = EstimatePreflightPolicy.BlockingFindings(built).Single(finding =>
            finding.Code == EstimateFindingCodes.MeasurementFailed && finding.AffectedRecordIds.Count == 0);
        for (var index = 0; index < 73; index++) global.Message.Should().Contain($"global-{index:000}");
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
    }

    private static (CivilQuantityExtractionService.ExtractionResult Extraction, CatalogSnapshot Catalog,
        ProjectProfile Profile, string GoodRecordId) ConsumerFixture(bool includeGlobalFailures)
    {
        // Synthetic, in-memory pricing authority solely to test money propagation.
        // This never writes an approval, catalog, native scan, or project profile.
        var catalog = EstimateFixtures.SnapshotWithPrice("SYNTHETIC.EXTENTS.1", "יח'", 12.50m);
        var profile = EstimateFixtures.Profile();
        var result = new CivilQuantityExtractionService.ExtractionResult();
        string goodId = "";
        for (var index = 0; index < 18; index++)
        {
            var handle = $"8BB299/{0x7788 + index:X}";
            var measurement = Measurement();
            var record = Record(measurement, handle);
            var classification = record.Classification;
            classification.CandidateCatalogCode = "SYNTHETIC.EXTENTS.1";
            classification.ApprovedCatalogId = catalog.SnapshotId;
            classification.ApprovedCatalogHash = catalog.FileHash;
            classification.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items["SYNTHETIC.EXTENTS.1"]);
            classification.MappingApprovedBy = "SYNTHETIC TEST ONLY";
            classification.MappingApprovedAtUtc = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
            result.Records.Add(record);
            if (index < 17)
            {
                var scopes = Scopes(result, handle);
                Capture(scopes, result, measurement, Failure($"scoped-{index:000}"));
                scopes.BindEmitted(record);
            }
            else goodId = record.RecordId;
        }
        if (includeGlobalFailures)
            for (var index = 0; index < 73; index++) result.Findings.Add(Failure($"global-{index:000}"));
        CivilQuantityExtractionService.ConsolidateMeasurementFailures(result, ProfileId);
        return (result, catalog, profile, goodId);
    }
}
