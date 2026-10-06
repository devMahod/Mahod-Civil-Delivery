using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class MixedDimensionObjectScopeTests
{
    [Fact]
    public void ClosedPairInLargerLengthRule_DoesNotPoisonApprovedIndependentOpenGeometry()
    {
        var records = new[] { Record("open", "10/21"), Record("perimeter", "10/22", closed: true),
            Record("area", "10/22", area: true) };
        MixedIds(records).Should().BeEquivalentTo("perimeter", "area");
        records[2].Classification.CandidateCatalogCode = "SYNTHETIC-AREA";
        MixedIds(records).Should().BeEquivalentTo(new[] { "perimeter", "area" },
            "approving both dimensions cannot make one boundary additive");
    }

    [Theory]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("handle")]
    [InlineData("type")]
    [InlineData("method")]
    [InlineData("transform")]
    public void IncompleteIdentityOrMeasurement_KeepsTheConservativeLayerGate(string defect)
    {
        MixedIds(new[] { Record("open", "10/21"), Record("perimeter", "10/22", closed: true),
            Record("area", "10/22", area: true, defect: defect) })
            .Should().BeEquivalentTo("open", "perimeter", "area");
    }

    [Fact]
    public void MappingBothDimensionsCannotBypassIncompleteIdentityInALargerLengthRule()
    {
        foreach (var defect in new[] { "path", "hash", "handle", "type", "method", "transform" })
        {
            var records = new[] { Record("open", "10/21"), Record("perimeter", "10/22", closed: true),
                Record("area", "10/22", area: true, defect: defect) };
            records[2].Classification.CandidateCatalogCode = "SYNTHETIC-AREA";
            MixedIds(records).Should().BeEquivalentTo(new[] { "open", "perimeter", "area" },
                "all mappings cannot repair missing or contradictory geometry evidence: " + defect);
        }
    }

    [Fact]
    public void ConflictingValidHashesAtOneCanonicalPathCannotProveIndependentObjects()
    {
        foreach (var approveArea in new[] { false, true })
        {
            var records = new[] { Record("open", "10/21"), Record("perimeter", "10/22", closed: true),
                Record("area", "10/22", area: true,
                    drawingPath: @"c:\SYNTHETIC-ONLY\sub\..\GM.dwg", drawingHash: new string('b', 64)) };
            if (approveArea) records[2].Classification.CandidateCatalogCode = "SYNTHETIC-AREA";
            MixedIds(records).Should().BeEquivalentTo("open", "perimeter", "area");
        }
        // Canonical aliases and SHA letter casing are not a source conflict.
        MixedIds(new[] { Record("open", "10/21"), Record("perimeter", "10/22", closed: true),
            Record("area", "10/22", area: true,
                drawingPath: @"c:\SYNTHETIC-ONLY\sub\..\GM.dwg", drawingHash: new string('A', 64)) })
            .Should().BeEquivalentTo("perimeter", "area");
    }

    [Fact]
    public void SameObjectWithAnotherDimension_IsNotIndependentEvenIfLengthMethodSaysOpen()
    {
        MixedIds(new[] { Record("length", "10/22"), Record("area", "10/22", area: true) })
            .Should().BeEquivalentTo("length", "area");
    }

    [Fact]
    public void FullInsertionIdentity_DistinguishesInstancesButNeverUsesLeafHandleAlone()
    {
        MixedIds(new[] { Record("other-instance", "11/22"),
            Record("perimeter", "10/22", closed: true), Record("area", "10/22", area: true) })
            .Should().BeEquivalentTo("perimeter", "area");
    }

    [Fact]
    public void UnapprovedIndependentOpenGeometry_StillRequiresAnEngineeringDecision()
    {
        var open = Record("open", "10/21");
        open.Classification.CandidateCatalogCode = null;
        MixedIds(new[] { open, Record("perimeter", "10/22", closed: true), Record("area", "10/22", area: true) })
            .Should().BeEquivalentTo("open", "perimeter", "area");
    }

    [Fact]
    public void DecisionRebase_ReplacesOnlyDerivedFindingsAndPreservesNativeCoverageEvidence()
    {
        var coverage = new DeliveryFinding { Code = EstimateFindingCodes.MeasurementFailed,
            Domain = "estimate", Title = "Original source evidence", Severity = FindingSeverity.Error };
        var oldBroad = new DeliveryFinding { Code = EstimateFindingCodes.MixedDimensionLayer,
            Domain = "estimate", Title = "Old blanket gate", Severity = FindingSeverity.ReviewRequired,
            AffectedRecordIds = new() { "open", "perimeter", "area" } };
        var rebased = QuantitySignificance.RecomputeDecisionFindings(new[] {
            Record("open", "10/21"), Record("perimeter", "10/22", closed: true), Record("area", "10/22", area: true)
        }, new[] { coverage, oldBroad });
        rebased.Should().Contain(coverage).And.NotContain(oldBroad);
        rebased.Where(f => f.Code == EstimateFindingCodes.MixedDimensionLayer)
            .SelectMany(f => f.AffectedRecordIds).Distinct().Should().BeEquivalentTo("perimeter", "area");
    }

    private static string[] MixedIds(NeutralQuantityRecord[] records) => QuantitySignificance.DetectReviewFindings(records)
        .Where(finding => finding.Code == EstimateFindingCodes.MixedDimensionLayer)
        .SelectMany(finding => finding.AffectedRecordIds).Distinct().ToArray();

    private static NeutralQuantityRecord Record(string id, string handle, bool area = false, bool closed = false,
        string? defect = null, string? drawingPath = null, string? drawingHash = null) => new()
    {
        RecordId = id, ProjectProfileId = "SYNTHETIC-ONLY", RunId = "SYNTHETIC-ONLY",
        Source = new QuantitySource
        {
            Drawing = "GM.dwg", DrawingPath = defect == "path" ? "GM.dwg" : drawingPath ?? @"C:\SYNTHETIC-ONLY\GM.dwg",
            DrawingHash = defect == "hash" ? "unknown" : drawingHash ?? new string('a', 64),
            Handle = defect == "handle" ? "unknown" : handle, Xref = "GM",
            EntityType = defect == "type" ? "Unknown" : "Polyline", Layer = "GM|HW-CURB",
        },
        Measurement = new QuantityMeasurement
        {
            Kind = area ? "area" : "length", Unit = area ? "מ\"ר" : "מטר", RawValue = area ? 50 : 30,
            Method = defect == "method" ? "unknown+xref-transform" :
                (area ? "closed-polyline-area" : closed ? "closed-polyline-perimeter" : "polyline-length") +
                (defect == "transform" ? "" : "+xref-transform"),
        },
        Classification = new QuantityClassification
        {
            RuleKey = "layer:HW-CURB|" + (area ? "area" : "length"),
            CandidateCatalogCode = area ? null : "SYNTHETIC-LENGTH",
        },
    };
}
