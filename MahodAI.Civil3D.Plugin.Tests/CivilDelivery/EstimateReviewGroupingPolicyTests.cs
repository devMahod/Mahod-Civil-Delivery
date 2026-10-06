using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateReviewGroupingPolicyTests
{
    private static NeutralQuantityRecord Record(int index, string rule = "layer:HW-CURB|length",
        string kind = "length", string unit = "מטר") => new()
    {
        RecordId = "record-" + index, ProjectProfileId = "GROUPING-ONLY", RunId = "presentation",
        Source = new() { Drawing = "local.dwg", DrawingHash = new string('a', 64), Handle = index.ToString(), EntityType = "Polyline" },
        Measurement = new() { Kind = kind, Method = "fixture", RawValue = 12, Unit = unit },
        Classification = new() { RuleKey = rule },
    };

    private static EstimateReviewPolicy.Issue Issue(NeutralQuantityRecord record, string? detail = null) => new(
        "מדידה", "EST-UNMAPPED", "שיוך חסר " + record.RecordId, detail ?? "exact-detail-" + record.RecordId,
        "בדוק שיוך", true, EstimateReviewPolicy.Recovery.Mapping,
        new[] { record.RecordId }, new[] { record.Classification.RuleKey! });

    [Fact]
    public void FullScaleUnmappedReviewHasActionableGroupsWithoutReenumeratingRecordsOrLosingIssues()
    {
        var records = Enumerable.Range(0, 79197).Select(i => Record(i,
            "layer:group-" + i % 1041 + "|length")).ToArray();
        var issues = records.Select(record => Issue(record)).ToArray();
        var input = new SingleEnumeration<NeutralQuantityRecord>(records);
        var groups = EstimateReviewGroupingPolicy.Group(issues, input);
        input.Enumerations.Should().Be(1);
        groups.Should().HaveCount(1041);
        groups.Sum(group => group.Count).Should().Be(79197);
        groups.Sum(group => group.BlockingCount).Should().Be(79197);
        var retained = groups.SelectMany(group => group.Issues)
            .ToDictionary(issue => issue.RecordIds.Single(), StringComparer.Ordinal);
        retained.Count.Should().Be(issues.Length);
        foreach (var issue in issues) ReferenceEquals(retained[issue.RecordIds[0]], issue).Should().BeTrue();
        groups.Should().OnlyContain(group => group.Caption.Length < 300 &&
            group.Caption.Contains("בדיקת שיוך") && group.Caption.Contains("ממצאים"));
        issues.Should().OnlyContain(issue => issue.Blocking);
        records.Should().OnlyContain(record => record.Classification.MappingApprovedBy == null);
    }

    [Fact]
    public void CodeStageRuleKindUnitAndBlockingBoundariesRemainSeparate()
    {
        var baseline = Record(0);
        var records = new[] { baseline, Record(1, unit: "m"), Record(2, "layer:other|length"),
            Record(3, kind: "area", unit: "מ\"ר"), Record(4, unit: "דונם") };
        var baseIssue = Issue(baseline);
        var issues = records.Select(record => Issue(record)).Concat(new[]
        {
            baseIssue with { Code = "EST-MISSING-PRICE", Action = EstimateReviewPolicy.Recovery.Price },
            baseIssue with { Stage = "אומדן" },
            baseIssue with { Blocking = false },
        }).ToArray();
        var groups = EstimateReviewGroupingPolicy.Group(issues, records);
        groups.Should().HaveCount(7);
        groups.Single(group => group.Count == 2).Issues.Should().Equal(issues.Take(2));
        groups.SelectMany(group => group.Issues).Should().HaveCount(8);
    }

    [Fact]
    public void IndependentGlobalMissingUnknownAndAmbiguousEvidenceNeverCollapses()
    {
        var record = Record(0);
        var issue = Issue(record);
        var issues = new[]
        {
            issue with { RecordIds = Array.Empty<string>(), RuleKeys = Array.Empty<string>(), Detail = "source A" },
            issue with { RecordIds = Array.Empty<string>(), RuleKeys = Array.Empty<string>(), Detail = "source B" },
            issue with { RecordIds = new[] { "missing" } }, issue with { RecordIds = new[] { "missing" } },
            issue, issue,
        };
        // The same record ID has conflicting dimensions; do not infer safe grouping.
        var groups = EstimateReviewGroupingPolicy.Group(issues, new[] { record, Record(0, kind: "area", unit: "מ\"ר") });
        groups.Should().HaveCount(6).And.OnlyContain(group => group.Count == 1);
        var unknown = Record(9, unit: "unrecognized");
        EstimateReviewGroupingPolicy.Group(new[] { Issue(unknown), Issue(unknown) }, new[] { unknown })
            .Should().HaveCount(2);
    }

    [Fact]
    public void GroupRetainsExactSourceReferencesAndDetailsThroughAllCharacterAndIssuePages()
    {
        var records = Enumerable.Range(0, 47).Select(index => Record(index)).ToArray();
        var source = new ProvenanceRef { SourceKind = "drawing", SourcePathOrUri = "exact-local-source" };
        var issues = records.Select(record => Issue(record,
            record.RecordId == "record-0" ? new string('x', 25000) + "complete-tail" : null)
            with { Sources = new[] { source } }).ToList();
        var group = EstimateReviewGroupingPolicy.Group(issues, records).Single();
        group.Issues[0].Should().BeSameAs(issues[0]);
        group.Issues[0].Sources[0].Should().BeSameAs(source);
        var originals = issues.ToArray();
        issues.Clear(); // Caller list mutation cannot remove the group's retained references.
        group.Count.Should().Be(47);
        var pager = EstimateReviewPager.ForGroup("still blocked", "host.dwg", Array.Empty<(string?, string?)>(), group);
        var text = new StringBuilder();
        do
        {
            pager.CurrentBatch.Count.Should().BeLessThanOrEqualTo(20);
            pager.Text.Length.Should().BeLessThanOrEqualTo(12000);
            text.Append(pager.Text);
        } while (pager.MoveNext());
        foreach (var original in originals)
            text.ToString().Should().Contain(original.Detail).And.Contain(original.RecordIds[0]);
        group.BlockingCount.Should().Be(47);
        while (pager.MovePrevious()) { }
        pager.CurrentBatch[0].Should().BeSameAs(originals[0]);
    }

    [Fact]
    public void BuiltLineExportReasonsGroupInto326DecisionsWithoutChangingOriginalCodes()
    {
        var records = Enumerable.Range(0, 79197).Select(i => Record(i,
            "layer:group-" + i % 326 + "|length")).ToArray();
        var issues = records.Select((record, i) => Issue(record) with
        {
            Stage = "ייצוא", Code = $"line:L{i + 1:D4}:unmapped",
        }).ToArray();
        var groups = EstimateReviewGroupingPolicy.Group(issues, records);
        groups.Should().HaveCount(326).And.OnlyContain(group => group.Code == "unmapped");
        groups.Sum(group => group.Count).Should().Be(79197);
        var retained = groups.SelectMany(group => group.Issues)
            .ToDictionary(issue => issue.RecordIds.Single(), StringComparer.Ordinal);
        for (var index = 0; index < issues.Length; index++)
        {
            ReferenceEquals(retained[records[index].RecordId], issues[index]).Should().BeTrue();
            issues[index].Code.Should().Be($"line:L{index + 1:D4}:unmapped");
        }
    }

    [Fact]
    public void ExportCodeNormalizationDoesNotInferMissingLineageOrChangeNonExportSourceReasons()
    {
        var record = Record(0);
        var issue = Issue(record) with { Stage = "ייצוא", Code = "line:L0001:unmapped" };
        var issues = new[]
        {
            issue with { RecordIds = new[] { "missing" } },
            issue with { RecordIds = Array.Empty<string>(), RuleKeys = Array.Empty<string>() },
            issue with { Stage = "סריקה" },
            issue with { Code = "line:unknown:unmapped" },
            issue with { Code = "line:L0002:" },
        };
        var groups = EstimateReviewGroupingPolicy.Group(issues, new[] { record });
        groups.Should().HaveCount(5);
        groups.Select(group => group.Code).Should().Equal(issues.Select(item => item.Code));
    }

    [Fact]
    public void EmptyReviewHasNoSyntheticGroupOrApproval()
    {
        EstimateReviewGroupingPolicy.Group(Array.Empty<EstimateReviewPolicy.Issue>(), Array.Empty<NeutralQuantityRecord>())
            .Should().BeEmpty();
    }

    private sealed class SingleEnumeration<T>(IEnumerable<T> source) : IEnumerable<T>
    {
        internal int Enumerations { get; private set; }
        public IEnumerator<T> GetEnumerator()
        {
            if (++Enumerations > 1) throw new InvalidOperationException("Record input must be indexed only once.");
            return source.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
