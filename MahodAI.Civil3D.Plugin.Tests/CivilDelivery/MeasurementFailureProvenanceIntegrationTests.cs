using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class MeasurementFailureProvenanceIntegrationTests
{
    [Fact]
    public void ConsolidationPreservesEveryFailureSourceAndDimensionWithoutNarrowingGlobalFailures()
    {
        // Native57 cardinalities; distinct synthetic contexts test preservation,
        // not a guessed attribution of the 54 old bare-handle Hatch messages.
        var extraction = new CivilQuantityExtractionService.ExtractionResult();
        for (var i = 0; i < 108; i++)
        {
            var kind = i < 54 ? "area" : i < 81 ? "length" : "count";
            var source = new ProvenanceRef
            {
                SourceKind = "xref", SourcePathOrUri = @"C:\TEST-ONLY\source.dwg", DrawingChecksum = new string('a', 64),
                SourceHandle = $"AB/{i + 1:X}", XrefPath = "TEST-SOURCE > NESTED", EntityType = i < 54 ? "Hatch" : "TEST",
                Layer = "TEST-LAYER", MeasurementMethod = kind == "area" ? "hatch-area" : "test-" + kind,
            };
            var failure = MeasurementFailureProvenance.Create("6422", $"failure-{i}",
                i < 54 ? "eNotApplicable" : i < 81 ? "raw=0; factor=1" : "eNullExtents", source, kind);
            if (i >= 81) failure.AffectedRecordIds.Add($"SYNTHETIC-EMITTED-{i}");
            extraction.Findings.Add(failure);
        }

        CivilQuantityExtractionService.ConsolidateMeasurementFailures(extraction, "6422");

        extraction.Records.Should().BeEmpty();
        extraction.Findings.Should().HaveCount(2);
        var global = extraction.Findings.Single(f => f.AffectedRecordIds.Count == 0);
        global.SourceRefs.Should().HaveCount(81);
        global.SourceRefs.Select(s => s.SourceHandle).Distinct().Should().HaveCount(81);
        global.Message.Should().Contain("measurement-kind=area").And.Contain("measurement-kind=length")
            .And.Contain("eNotApplicable").And.Contain("raw=0; factor=1");
        var scoped = extraction.Findings.Single(f => f.AffectedRecordIds.Count > 0);
        scoped.AffectedRecordIds.Should().HaveCount(27); scoped.SourceRefs.Should().HaveCount(27);
        scoped.Message.Should().Contain("measurement-kind=count").And.Contain("eNullExtents");
        extraction.Findings.Should().OnlyContain(f => f.Severity == FindingSeverity.Error && f.ResolvedAtUtc == null);
        extraction.Findings.Should().OnlyContain(f => EstimatePreflightPolicy.IsBlocking(f));
    }

    [Fact]
    public void UnknownLegacyFailureDoesNotAcquireAnotherObjectsSourceDuringConsolidation()
    {
        var extraction = new CivilQuantityExtractionService.ExtractionResult();
        extraction.Findings.Add(MeasurementFailureProvenance.Create("6422", "Hatch ABC", "eNotApplicable"));
        extraction.Findings.Add(MeasurementFailureProvenance.Create("6422", "Hatch ABC", "eNotApplicable",
            new ProvenanceRef { SourceKind = "xref", SourcePathOrUri = @"C:\known\source.dwg",
                SourceHandle = "DEF/ABC", Layer = "known", MeasurementMethod = "hatch-area" }, "area"));
        CivilQuantityExtractionService.ConsolidateMeasurementFailures(extraction, "6422");
        var result = extraction.Findings.Should().ContainSingle().Which;
        result.Message.Should().StartWith("Complete measurement failures (2):");
        result.SourceRefs.Should().ContainSingle().Which.SourceHandle.Should().Be("DEF/ABC");
        result.AffectedRecordIds.Should().BeEmpty();
        EstimatePreflightPolicy.IsBlocking(result).Should().BeTrue();
    }
}
