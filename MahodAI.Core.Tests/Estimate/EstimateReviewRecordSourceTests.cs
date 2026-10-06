using System;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateReviewRecordSourceTests
{
    private static NeutralQuantityRecord Record(string fault = "", string id = "q-xref-area", string handle = "AB/CD") => new()
    {
        RecordId = id, ProjectProfileId = "TEST-ONLY", RunId = "record-run",
        Source = new() { Drawing = "source.dwg", DrawingPath = @"C:\local\source.dwg",
            DrawingHash = new string('a', 64), Handle = handle, EntityType = "POLYLINE", Layer = "HW-CURB", Xref = "GM > nested" },
        Measurement = new() { Kind = "area", Method = "polyline-area+xref-transform", RawValue = 12, Unit = "m2" },
        Classification = new() { RuleKey = "layer:HW-CURB|area" },
        Status = DeliveryStatus.ReviewRequired,
        Provenance = fault == "missing" ? null : new()
        {
            SourceKind = fault == "kind" ? "drawing" : "xref",
            SourcePathOrUri = fault == "path" ? @"C:\different\source.dwg" : @"C:\local\source.dwg",
            DrawingChecksum = fault == "hash" ? new string('b', 64) : fault == "invalid-hash" ? "invalid" : new string('a', 64),
            SourceHandle = fault == "handle" ? "CD" : handle,
            XrefPath = fault == "xref" ? "other" : "GM > nested",
            RunId = fault == "run" ? "different-run" : "record-run",
            EntityType = fault == "type" ? "Line" : "Polyline", Layer = fault == "layer" ? "other" : "HW-CURB",
            MeasurementMethod = fault == "method" ? "polyline-length" : "polyline-area+xref-transform",
            SourceSubentityPath = fault == "subentity" ? "unproven-face" : null,
            XrefTransform = new[] { 1d, 0, 0, 0, 1, 0, 0, 0, 1, 12, 34, 0 },
        },
    };

    private static DeliveryFinding Finding(params string[] ids) => new()
    {
        FindingId = "native-shaped-mixed-dimension", Domain = "estimate",
        Code = EstimateFindingCodes.MixedDimensionLayer, Title = "Closed area/perimeter alternatives",
        Message = "Choose engineering measurement dimension; neither alternative is approved.",
        Severity = FindingSeverity.Error, AffectedRecordIds = ids.ToList(),
    };

    [Fact]
    public void ExplicitRecordIdsOfferExactCapturedNestedObjectsWithoutChangingDecisions()
    {
        var area = Record();
        var otherInsertion = Record(id: "second-insertion", handle: "EF/CD");
        var finding = Finding(area.RecordId, otherInsertion.RecordId);
        var before = JsonSerializer.Serialize(new { area, otherInsertion, finding });
        var issue = Assert.Single(EstimateReviewPolicy.Collect(new[] { area, otherInsertion }, new[] { finding }, Array.Empty<DeliveryFinding>(), null));
        Assert.True(issue.Blocking);
        Assert.Equal(2, issue.Sources.Count);
        Assert.Same(area.Provenance, issue.Sources[0]);
        Assert.Same(otherInsertion.Provenance, issue.Sources[1]);
        Assert.Same(area.Provenance!.XrefTransform, issue.Sources[0].XrefTransform);
        var targets = FindingSourceLocationPolicy.Collect(new[] { issue });
        Assert.Equal(2, targets.Count);
        Assert.All(targets, target => Assert.Null(target.UnavailableReason));
        Assert.Equal(before, JsonSerializer.Serialize(new { area, otherInsertion, finding }));
        Assert.Empty(finding.SourceRefs);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("invalid-hash")]
    [InlineData("handle")]
    [InlineData("xref")]
    [InlineData("run")]
    [InlineData("type")]
    [InlineData("layer")]
    [InlineData("method")]
    [InlineData("kind")]
    [InlineData("subentity")]
    public void IncompleteOrConflictingProofNeverEnablesRecordNavigation(string fault)
    {
        var record = Record(fault);
        var issue = Assert.Single(EstimateReviewPolicy.Collect(new[] { record }, new[] { Finding(record.RecordId) }, Array.Empty<DeliveryFinding>(), null));
        Assert.Empty(issue.Sources);
        Assert.Equal(record.RecordId, Assert.Single(issue.RecordIds));
        Assert.True(issue.Blocking);
    }

    [Fact]
    public void DuplicateOrMissingRecordIdsRemainVisibleWithoutChoosingAnObject()
    {
        var record = Record();
        var issue = Assert.Single(EstimateReviewPolicy.Collect(new[] { record, Record(handle: "EF/CD") },
            new[] { Finding(record.RecordId, "absent") }, Array.Empty<DeliveryFinding>(), null));
        Assert.Empty(issue.Sources);
        Assert.Equal(2, issue.RecordIds.Count);
        Assert.True(issue.Blocking);
    }

    [Fact]
    public void RecordOwnedFindingCanNavigateButGlobalFindingDoesNotInventAffectedObjects()
    {
        var record = Record();
        var owned = Finding();
        record.Findings.Add(owned);
        var measuredIssue = Assert.Single(EstimateReviewPolicy.Collect(new[] { record }, Array.Empty<DeliveryFinding>(), Array.Empty<DeliveryFinding>(), null));
        Assert.Same(record.Provenance, Assert.Single(measuredIssue.Sources));
        record.Findings.Clear();
        var globalIssue = Assert.Single(EstimateReviewPolicy.Collect(new[] { record }, new[] { Finding() }, Array.Empty<DeliveryFinding>(), null));
        Assert.Empty(globalIssue.Sources);
        Assert.Empty(globalIssue.RecordIds);
    }

    [Fact]
    public void GlobalFindingCopiedOntoBuiltLinesCannotCreateFalseNavigationAssociations()
    {
        var record = Record();
        var global = Finding();
        var result = new EstimateResult
        {
            RunId = "TEST-ONLY", ProjectProfileId = "TEST-ONLY",
            Findings = { global },
            Lines = { new EstimateLine { LineId = "L1", RecordId = record.RecordId, Findings = { global } } },
        };
        var issues = EstimateReviewPolicy.Collect(new[] { record }, new[] { global }, Array.Empty<DeliveryFinding>(), result);
        var issue = Assert.Single(issues.Where(item => item.Code == EstimateFindingCodes.MixedDimensionLayer));
        Assert.Equal(record.RecordId, Assert.Single(issue.RecordIds));
        Assert.Empty(issue.Sources);
        Assert.True(issue.Blocking);
    }

    [Fact]
    public void ExplicitFailureSourcesRemainAuthoritativeEvenWhenInvalid()
    {
        var record = Record();
        var finding = Finding(record.RecordId);
        var invalid = new ProvenanceRef { SourceKind = "xref", SourceHandle = "CD" };
        finding.SourceRefs.Add(invalid);
        var issue = Assert.Single(EstimateReviewPolicy.Collect(new[] { record }, new[] { finding }, Array.Empty<DeliveryFinding>(), null));
        Assert.Same(invalid, Assert.Single(issue.Sources));
        Assert.NotNull(Assert.Single(FindingSourceLocationPolicy.Collect(new[] { issue })).UnavailableReason);
    }
}
