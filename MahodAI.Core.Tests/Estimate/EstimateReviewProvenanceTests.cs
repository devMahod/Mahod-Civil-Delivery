using System;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateReviewProvenanceTests
{
    [Fact]
    public void FailureWithoutQuantityRecordRetainsEveryDistinctSourceAndRemainsBlocking()
    {
        ProvenanceRef Source(string path, string hash) => new()
        {
            SourceKind = "xref", SourcePathOrUri = path, DrawingChecksum = hash,
            SourceHandle = "8BB299/26236C", XrefPath = "HA > nested", Layer = "H1", MeasurementMethod = "hatch-area",
        };
        var first = Source("local/HA.dwg", new string('a', 64));
        var otherVersion = Source("local/HA.dwg", new string('b', 64));
        var finding = new DeliveryFinding
        {
            FindingId = "failed-area", Domain = "estimate", Code = EstimateFindingCodes.MeasurementFailed,
            Severity = FindingSeverity.Error, Title = "מדידה נכשלה", Message = "eNotApplicable",
            SourceRefs = { first, first, otherVersion },
        };
        var before = JsonSerializer.Serialize(finding);
        var issue = Assert.Single(EstimateReviewPolicy.Collect(Array.Empty<NeutralQuantityRecord>(),
            new[] { finding }, Array.Empty<DeliveryFinding>(), null));
        Assert.True(issue.Blocking); Assert.Empty(issue.RecordIds); Assert.Empty(issue.RuleKeys);
        Assert.Equal(2, issue.Sources.Count);
        Assert.All(issue.Sources, source => Assert.Equal("8BB299/26236C", source.SourceHandle));
        Assert.Equal(before, JsonSerializer.Serialize(finding));
        Assert.Null(finding.ResolvedAtUtc);
    }

    [Fact]
    public void SharedGlobalFindingDoesNotSerializeEverySourceAgainForEveryOccurrence()
    {
        var finding = new DeliveryFinding
        {
            FindingId = "shared-global-failure", Domain = "estimate",
            Code = EstimateFindingCodes.MeasurementFailed, Severity = FindingSeverity.Error,
            Title = "Shared measurement failures",
        };
        for (var index = 0; index < 81; index++)
            finding.SourceRefs.Add(new ProvenanceRef
            {
                SourceKind = "xref", SourcePathOrUri = "local/source-" + index + ".dwg",
                SourceHandle = index.ToString("X"), DrawingChecksum = new string('a', 64),
                XrefPath = "GM > nested", Layer = "H1", MeasurementMethod = "hatch-area",
            });
        // Warm serializer metadata outside the allocation measurement. The old fan-out
        // serialized 810,000 sources here (>500MB); exact shared evidence needs only 81.
        _ = JsonSerializer.Serialize(finding.SourceRefs[0]);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var issue = Assert.Single(EstimateReviewPolicy.Collect(Array.Empty<NeutralQuantityRecord>(),
            Enumerable.Repeat(finding, 10_000), Array.Empty<DeliveryFinding>(), null));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(81, issue.Sources.Count); Assert.True(issue.Blocking);
        Assert.True(allocated < 64_000_000, $"Repeated source traversal allocated {allocated:N0} bytes");
        Assert.Equal(81, finding.SourceRefs.Count); Assert.Null(finding.ResolvedAtUtc);
    }
}
