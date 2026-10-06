using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateRecoveryPresentationTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static NeutralQuantityRecord Record(string id = "r1", string key = "layer:KERB|length") => new()
    {
        RecordId = id, ProjectProfileId = "test-only", RunId = "synthetic-review",
        Source = new() { Drawing = "SIMULATION.dwg", DrawingHash = Hash, Handle = id, EntityType = "LINE", Layer = "KERB" },
        Measurement = new() { Kind = "length", Method = "line-length", RawValue = 37.25, Unit = "m" },
        Classification = new() { RuleKey = key }, Status = DeliveryStatus.Ready,
    };
    private static DeliveryFinding Finding(string code, string id = "f1") => new()
    {
        FindingId = id, Code = code, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
        Title = "Exact blocker", Message = "Exact source detail", AffectedRecordIds = { "r1" },
    };
    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "synthetic-catalog", FileHash = Hash,
        Items = { ["TEST.1"] = new() { Code = "TEST.1", Description = "Test kerb", UnitRaw = "m" } },
        Prices = { ["TEST.1"] = new() { Code = "TEST.1", Price = 10m, PriceBookId = "synthetic-catalog", SourceHash = Hash } },
    };
    private static EstimateLine Line(string id = "r1", decimal? price = 10m) => new()
    {
        LineId = "line-" + id, RecordId = id, RuleKey = "layer:KERB|length", CatalogCode = "TEST.1",
        Price = price, PriceStatus = price == null ? PriceStatus.MissingPrice : PriceStatus.Priced,
        Status = price == null ? DeliveryStatus.ReviewRequired : DeliveryStatus.Ready,
        RawQuantity = 37.25, BoqQuantity = 37.25, Unit = "m",
        IncludedInTotals = price != null, Total = price * 37.25m,
        SourceDrawingPath = "SIMULATION.dwg", SourceHandle = id,
    };
    private static EstimateResult Result(params EstimateLine[] lines) => new()
    {
        RunId = "synthetic-review", ProjectProfileId = "test-only", Lines = lines.ToList(),
    };

    [Fact]
    public void ReviewIncludesCatalogBuildAndLineFailuresEvenWhenScanHasNoFindings()
    {
        var line = Line(price: null);
        var missing = Finding(EstimateFindingCodes.MissingPrice);
        line.Findings.Add(missing);
        var result = Result(line);
        result.Findings.Add(Finding(EstimateFindingCodes.AdjustmentUnverified, "adjustment"));
        var catalog = Finding("EST-CATALOG-LOAD-FAILED", "catalog");
        var issues = EstimateReviewPolicy.Collect(new[] { Record() }, Array.Empty<DeliveryFinding>(), new[] { catalog }, result);
        issues.Should().Contain(issue => issue.Code == EstimateFindingCodes.MissingPrice &&
            issue.Action == EstimateReviewPolicy.Recovery.Price && issue.RuleKeys.Contains("layer:KERB|length"));
        issues.Should().Contain(issue => issue.Code == EstimateFindingCodes.AdjustmentUnverified);
        issues.Should().Contain(issue => issue.Code == catalog.Code && issue.Action == EstimateReviewPolicy.Recovery.Catalog);
        issues.Should().Contain(issue => issue.Code == "trace:source-drawing-hash-missing");
        issues.Should().Contain(issue => issue.Code == "line:line-r1:unpriced" && issue.RecordIds.Contains("r1"));
        line.Price.Should().BeNull(); result.Lines.Should().ContainSingle(); // presentation never repairs authority
    }

    [Fact]
    public void SharedFindingIsDeduplicatedWithoutLosingItsStagesOrAffectedRecords()
    {
        var shared = Finding(EstimateFindingCodes.MeasurementFailed);
        var first = Line(); first.Findings.Add(shared);
        var second = Line("r2"); second.Findings.Add(shared);
        var result = Result(first, second); result.Findings.Add(shared);
        var issues = EstimateReviewPolicy.Collect(new[] { Record(), Record("r2", "layer:OTHER|length") },
            new[] { shared }, Array.Empty<DeliveryFinding>(), result);
        var issue = issues.Single(value => value.Code == shared.Code);
        issue.RecordIds.Should().BeEquivalentTo(new[] { "r1", "r2" });
        issue.RuleKeys.Should().HaveCount(2); issue.Stage.Should().Contain("סריקה").And.Contain("שורת אומדן");
        shared.AffectedRecordIds.Should().Equal("r1");
    }

    [Fact]
    public void SameFindingIdWithConflictingContentDoesNotHideEitherExplanation()
    {
        var first = Finding("EST-ONE"); var second = Finding("EST-TWO");
        EstimateReviewPolicy.Collect(new[] { Record() }, new[] { first, second }, Array.Empty<DeliveryFinding>(), null)
            .Should().HaveCount(2);
    }

    [Fact]
    public void ApprovedMappingPlusMeasurementFailureIsNeverGreenOrSilentlyRemoved()
    {
        var record = Record(); var failure = Finding(EstimateFindingCodes.MeasurementFailed);
        var state = EstimateQuantityPresentationPolicy.Evaluate(new[] { record }, new[] { failure }, "TEST.1",
            Catalog(), null, false, true, false);
        state.MappingApproved.Should().BeTrue(); state.MeasurementNeedsReview.Should().BeTrue();
        state.Severity.Should().Be("review"); state.BuiltReady.Should().BeFalse();
        record.Measurement.RawValue.Should().Be(37.25); failure.ResolvedAtUtc.Should().BeNull();
    }

    [Fact]
    public void CatalogPriceBeforeBuildIsExplicitlyNotFinalAndNeverGreen()
    {
        var state = EstimateQuantityPresentationPolicy.Evaluate(new[] { Record() }, Array.Empty<DeliveryFinding>(),
            "TEST.1", Catalog(), null, false, true, false);
        state.MappingApproved.Should().BeTrue(); state.PriceDisplay.Should().Be(10m.ToString("N2"));
        state.Detail.Should().Contain("מחירון בלבד").And.Contain("טרם נבנה"); state.Severity.Should().Be("review");
    }

    [Fact]
    public void ActualBuiltProjectPriceOverridesCatalogDisplayButDoesNotHideGlobalExportBlock()
    {
        var line = Line(price: 17.5m); line.PriceStatus = PriceStatus.ProjectOverride;
        var state = EstimateQuantityPresentationPolicy.Evaluate(new[] { Record() }, Array.Empty<DeliveryFinding>(),
            "TEST.1", Catalog(), Result(line), false, true, false);
        state.PriceDisplay.Should().Be(17.5m.ToString("N2"));
        state.Detail.Should().Contain("מחיר פרויקט מהבנייה").And.Contain("האומדן הכולל חסום");
        state.Severity.Should().Be("review"); state.BuiltReady.Should().BeFalse();
    }

    [Fact]
    public void MixedBuiltPricesAndMissingPriceNeverSelectTheFirstPriceOrFallbackToCatalog()
    {
        var records = new[] { Record(), Record("r2") };
        var mixed = EstimateQuantityPresentationPolicy.Evaluate(records, Array.Empty<DeliveryFinding>(),
            "TEST.1", Catalog(), Result(Line(), Line("r2", 20m)), false, true, false);
        mixed.PriceDisplay.Should().Be("מעורב"); mixed.Severity.Should().Be("review");
        var missing = EstimateQuantityPresentationPolicy.Evaluate(new[] { Record() }, Array.Empty<DeliveryFinding>(),
            "TEST.1", Catalog(), Result(Line(price: null)), false, true, false);
        missing.PriceDisplay.Should().BeNull(); missing.Detail.Should().Contain("חסר מחיר");
    }

    [Fact]
    public void MissingBuiltRecordAndStaleSourceCannotBeReadyEvenWhenCallerPreviouslyHadExportReady()
    {
        var records = new[] { Record(), Record("r2") };
        var partial = EstimateQuantityPresentationPolicy.Evaluate(records, Array.Empty<DeliveryFinding>(),
            "TEST.1", Catalog(), Result(Line()), false, true, true);
        partial.BuiltReady.Should().BeFalse(); partial.PriceDisplay.Should().BeNull();
        var stale = EstimateQuantityPresentationPolicy.Evaluate(new[] { Record() }, Array.Empty<DeliveryFinding>(),
            "TEST.1", Catalog(), Result(Line()), false, false, true);
        stale.BuiltReady.Should().BeFalse(); stale.Severity.Should().Be("review");
    }

    [Fact]
    public void WrongUnitAndUnknownRowsRetainMeasuredValuesWithoutPriceAuthority()
    {
        var catalog = Catalog(); catalog.Items["TEST.1"] = new() { Code = "TEST.1", Description = "area", UnitRaw = "m2" };
        var record = Record();
        var mismatch = EstimateQuantityPresentationPolicy.Evaluate(new[] { record }, Array.Empty<DeliveryFinding>(),
            "TEST.1", catalog, null, false, true, false);
        mismatch.MappingApproved.Should().BeFalse(); mismatch.PriceDisplay.Should().BeNull();
        var unknown = EstimateQuantityPresentationPolicy.Evaluate(new[] { record }, Array.Empty<DeliveryFinding>(),
            null, catalog, null, false, true, false);
        unknown.PriceDisplay.Should().BeNull(); unknown.BuiltReady.Should().BeFalse();
        record.Measurement.RawValue.Should().Be(37.25);
    }

    [Fact]
    public void DeferredEarthworksIsNotMislabeledAsFailedMeasuredGeometryOrMadeExportable()
    {
        var finding = Finding(EstimatePreflightPolicy.EarthworksNotAssessedCode);
        var state = EstimateQuantityPresentationPolicy.Evaluate(new[] { Record() }, new[] { finding },
            "TEST.1", Catalog(), null, false, true, false);
        state.MeasurementNeedsReview.Should().BeFalse(); state.BuiltReady.Should().BeFalse();
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
    }

    [Fact]
    public void ExplicitBatchReviewIsSuggestedOnlyAfterEligibilityAndNeverBeforeFirstMeasurement()
    {
        var snapshot = new EstimateGuidedActionPolicy.Snapshot(true, false, true, false, false, true,
            10, 10, true, false, false, 0, ProvenMappingGroups: 3);
        EstimateGuidedActionPolicy.Evaluate(snapshot).Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewProvenMappings);
        EstimateGuidedActionPolicy.Evaluate(snapshot with { SourcesApproved = false }).Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewQuantities);
        EstimateGuidedActionPolicy.Evaluate(snapshot with { HasFreshScan = false }).Next.Should().Be(EstimateGuidedActionPolicy.Action.Scan);
        EstimateGuidedActionPolicy.Evaluate(snapshot with { SavePending = true }).Enabled.Should().BeFalse();
        // Cancel does not alter the input or silently approve a mapping.
        EstimateGuidedActionPolicy.Evaluate(snapshot).Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewProvenMappings);
        snapshot.PendingReviewGroups.Should().Be(10);
    }
}
