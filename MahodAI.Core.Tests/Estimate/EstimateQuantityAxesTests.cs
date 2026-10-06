using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Four display axes (Codex 8D19CD93 §6): an identity-less blocking finding (the 6422 shape of the 7 unresolved XREF
/// traversals) is project coverage, not each group's measurement failure. The gate meaning is unchanged: the group is
/// still MeasurementNeedsReview, never priced for a draft and never ready. SYNTHETIC data.
/// </summary>
public sealed class EstimateQuantityAxesTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static NeutralQuantityRecord Record(string id = "r1") => new()
    {
        RecordId = id, ProjectProfileId = "test-only", RunId = "synthetic-axes",
        Source = new() { Drawing = "SIMULATION.dwg", DrawingHash = Hash, Handle = id, EntityType = "LINE", Layer = "KERB" },
        Measurement = new() { Kind = "length", Method = "line-length", RawValue = 37.25, Unit = "m" },
        Classification = new() { RuleKey = "layer:KERB|length" }, Status = DeliveryStatus.Ready,
    };

    /// <summary>The exact 6422 shape: Error, no affected records, no source refs.</summary>
    private static DeliveryFinding Xref(string id = "x1") => new()
    {
        FindingId = id, Code = EstimateFindingCodes.XrefTraversalUnresolved, Domain = "estimate",
        Severity = FindingSeverity.Error, Title = "XREF traversal unresolved", Message = "SYNTHETIC",
    };

    private static DeliveryFinding Local(string recordId = "r1") => new()
    {
        FindingId = "local-" + recordId, Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate",
        Severity = FindingSeverity.ReviewRequired, Title = "Local failure", Message = "SYNTHETIC",
        AffectedRecordIds = { recordId },
    };

    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "synthetic-catalog", FileHash = Hash,
        Items = { ["TEST.1"] = new() { Code = "TEST.1", Description = "Test kerb", UnitRaw = "m" } },
        Prices = { ["TEST.1"] = new() { Code = "TEST.1", Price = 10m, PriceBookId = "synthetic-catalog", SourceHash = Hash } },
    };

    private static EstimateQuantityPresentationPolicy.State Evaluate(string? code, params DeliveryFinding[] findings) =>
        EstimateQuantityPresentationPolicy.Evaluate(new[] { Record() }, findings, code, Catalog(), null, false, true, false);

    [Fact]
    public void CoverageOnly_KeepsTheGate_ButShowsMappingAndCatalogPriceInsteadOfAMeasurementFailure()
    {
        var state = Evaluate("TEST.1", Xref(), Xref("x2"));
        state.MeasurementNeedsReview.Should().BeTrue("the guard meaning is unchanged: the group is still blocked");
        state.LocalMeasurementNeedsReview.Should().BeFalse();
        state.ProjectCoverageBlockers.Should().Be(2);
        state.BuiltReady.Should().BeFalse();
        state.Severity.Should().Be("review");
        state.Summary.Should().Be("שויך · כיסוי חסר");
        state.PriceDisplay.Should().Be(10m.ToString("N2"), "a coverage-only block does not hide the catalog price");
        state.Detail.Should().Contain("מדידה מקומית: לא דווח כשל מקומי; כיסוי המקורות טרם הושלם")
            .And.Contain($"כיסוי פרויקט: 2 ממצאים כלליים ({EstimateFindingCodes.XrefTraversalUnresolved})")
            .And.Contain("אין זה כשל מדידה של הקבוצה")
            .And.Contain("מחירון בלבד").And.Contain("תמחור חסום בכיסוי המקורות");
    }

    [Fact]
    public void CoverageOnly_Unmapped_AsksForMappingNotForMeasurement()
    {
        var state = Evaluate(null, Xref());
        state.Summary.Should().Be("דרוש שיוך");
        state.MeasurementNeedsReview.Should().BeTrue();
        state.LocalMeasurementNeedsReview.Should().BeFalse();
        state.PriceDisplay.Should().BeNull();
    }

    [Fact]
    public void ALocalFailureStillShowsMeasurementReviewAndHidesThePrice_EvenWithCoverageBlockers()
    {
        var state = Evaluate("TEST.1", Xref(), Local());
        state.Summary.Should().Be("בדיקת מדידה");
        state.LocalMeasurementNeedsReview.Should().BeTrue();
        state.ProjectCoverageBlockers.Should().Be(1);
        state.PriceDisplay.Should().BeNull();
        state.Detail.Should().Contain("מדידה מקומית: דרושה בדיקת מקור/מדידה");
    }

    [Fact]
    public void WithoutAnyBlockerNothingChanges()
    {
        var state = Evaluate("TEST.1");
        state.MeasurementNeedsReview.Should().BeFalse();
        state.ProjectCoverageBlockers.Should().Be(0);
        state.Summary.Should().Be("שיוך מאושר");
        state.Detail.Should().Contain("כיסוי פרויקט: לא דווח חסם כללי").And.Contain("מדידה מקומית: נמדדה; אינה אישור אומדן");
    }

    [Fact]
    public void AnIdentityLessBlockerThatIsNotXrefCoverageStaysALocalReview_AndIsNamed()
    {
        // Review AX-1/AX-2: units, configuration or measurement problems without ids are not "coverage".
        var unit = new DeliveryFinding
        {
            FindingId = "u1", Code = "EST-UNIT-UNKNOWN", Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "Unknown unit", Message = "SYNTHETIC",
        };
        EstimateQuantityPresentationPolicy.IsProjectWide(unit).Should().BeFalse();
        var state = Evaluate("TEST.1", unit);
        state.Summary.Should().Be("בדיקת מדידה");
        state.LocalMeasurementNeedsReview.Should().BeTrue();
        state.ProjectCoverageBlockers.Should().Be(0);
        state.PriceDisplay.Should().BeNull();
        state.Detail.Should().Contain("חסמים שאינם כיסוי XREF החלים על קבוצה זו (EST-UNIT-UNKNOWN)").And.NotContain("לא דווח חסם כללי");
    }

    [Fact]
    public void AFindingBoundToARecordOrSourceIsNeverCalledProjectWide()
    {
        EstimateQuantityPresentationPolicy.IsProjectWide(Xref()).Should().BeTrue();
        EstimateQuantityPresentationPolicy.IsProjectWide(Local()).Should().BeFalse();
        var sourced = Xref();
        sourced.SourceRefs.Add(new ProvenanceRef { SourceKind = "xref" });
        EstimateQuantityPresentationPolicy.IsProjectWide(sourced).Should().BeFalse();
    }
}
