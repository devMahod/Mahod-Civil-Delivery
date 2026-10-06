using System;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class FindingSourceContextPolicyTests
{
    private static ProvenanceRef Source(string handle = "AB/7112", string path = @"C:\local\HA.dwg",
        string? hash = null, string run = "TEST-RUN") => new()
    {
        SourceKind = "xref", SourcePathOrUri = path, DrawingChecksum = hash ?? new string('a', 64),
        SourceHandle = handle, XrefPath = "HA", Layer = "HA|HW-HTCH-ROAD", EntityType = "Hatch",
        MeasurementMethod = "hatch-area", RunId = run,
    };
    private static string Entry(ProvenanceRef source, string reason)
    {
        var finding = MeasurementFailureProvenance.Create("TEST", "Failed Hatch " + source.SourceHandle, reason, source, "area");
        return finding.Title + ": " + finding.Message;
    }
    private static EstimateReviewPolicy.Issue Issue(string detail, params ProvenanceRef[] sources) =>
        new("scan", EstimateFindingCodes.MeasurementFailed, "Aggregate failures", detail, "Repair source; save and rescan",
            true, EstimateReviewPolicy.Recovery.Inspect, Array.Empty<string>(), Array.Empty<string>()) { Sources = sources };

    [Fact]
    public void FullIdentitySelectsOnlyItsRecordedReasonWithoutAnyQuantityRecord()
    {
        var first = Source(); var second = Source("AC/7112", @"C:\local\OTHER.dwg");
        var issue = Issue("Complete measurement failures (2):\n" + Entry(first, "eNotApplicable") + "\n" +
            Entry(second, "different error"), first, second);
        var targets = FindingSourceLocationPolicy.Collect(new[] { issue });
        targets[0].Contexts.Single().ExactFailure.Should().Contain("eNotApplicable").And.NotContain("different error");
        targets[1].Contexts.Single().ExactFailure.Should().Contain("different error").And.NotContain("eNotApplicable");
        targets[0].Source.Should().BeSameAs(first);
        issue.Blocking.Should().BeTrue(); issue.RecordIds.Should().BeEmpty();
        FindingSourceContextPolicy.Format(targets[0].Contexts).Should().Contain("Repair source; save and rescan");
    }

    [Fact]
    public void SameLeafOrChangedHashNeverBorrowsAnotherSourcesReason()
    {
        var text = Entry(Source(), "eNotApplicable");
        FindingSourceContextPolicy.ExactFailure(text, Source("AC/7112")).Should().BeNull();
        FindingSourceContextPolicy.ExactFailure(text, Source(path: @"C:\other\HA.dwg")).Should().BeNull();
        FindingSourceContextPolicy.ExactFailure(text, Source(hash: new string('b', 64))).Should().BeNull();
    }

    [Fact]
    public void AmbiguousRepeatedOrMultilineContextRemainsCompleteAndExplicitlyAggregated()
    {
        var source = Source();
        foreach (var text in new[] { Entry(source, "first") + "\n" + Entry(source, "second"),
            Entry(source, "first\nunscoped additional error"), "legacy aggregate without source identity" })
        {
            var context = FindingSourceContextPolicy.Capture(Issue(text, source), source);
            context.ExactFailure.Should().BeNull(); context.Detail.Should().Be(text);
            FindingSourceContextPolicy.Format(new[] { context }).Should().Contain("הקשר ממצא מצטבר").And.Contain(text);
        }
    }

    [Fact]
    public void RepeatedSourceKeepsDistinctFailureContextsInsteadOfDroppingSecondReason()
    {
        var source = Source();
        var targets = FindingSourceLocationPolicy.Collect(new[] { Issue(Entry(source, "first"), source),
            Issue(Entry(source, "second"), source), Issue(Entry(source, "first"), source) });
        targets.Should().ContainSingle(); targets[0].Contexts.Should().HaveCount(2);
        FindingSourceContextPolicy.Format(targets[0].Contexts).Should().Contain("first").And.Contain("second");
    }

    [Fact]
    public void DiagnosticRequiresExactSourceAndRunNotJustAnExistingArtifact()
    {
        var source = Source();
        using var diagnostic = JsonDocument.Parse(JsonSerializer.Serialize(new
            { Sources = new[] { new { Source = source, Boundary = new { Purpose = "diagnostic-only" } } } }));
        FindingSourceContextPolicy.ContainsExactDiagnosticSource(diagnostic.RootElement, source).Should().BeTrue();
        FindingSourceContextPolicy.ContainsExactDiagnosticSource(diagnostic.RootElement, Source(run: "OTHER-RUN")).Should().BeFalse();
        FindingSourceContextPolicy.ContainsExactDiagnosticSource(diagnostic.RootElement, Source("AC/7112")).Should().BeFalse();
        FindingSourceContextPolicy.ContainsExactDiagnosticSource(diagnostic.RootElement, Source(hash: new string('b', 64))).Should().BeFalse();
        using var empty = JsonDocument.Parse("{}");
        FindingSourceContextPolicy.ContainsExactDiagnosticSource(empty.RootElement, source).Should().BeFalse();
    }
}
