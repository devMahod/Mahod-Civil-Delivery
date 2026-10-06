using System;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionSpanGroupedReviewTests
{
    private static SectionPlanRecord Record(string id, params (double from, double to)[] spans)
    {
        var record = new SectionPlanRecord
        {
            RecordId = id, SectionId = "STA-" + id, SelectedAlignment = "1000",
            Cl = new ClSourceRecord
            {
                RecordId = id, SourceDrawing = "CL.dwg", SourceDrawingHash = new string('a', 64),
                SourceHandle = id, SourceEntityType = "LINE", SourceLayer = "GFC111",
                SourceEndpoints = new[] { 0d, 0d, 10d, 0d }, WcsEndpoints = new[] { 0d, 0d, 10d, 0d },
            },
        };
        foreach (var (from, to) in spans)
            record.PresentationCoverage.UnresolvedSpans.Add(new()
            {
                FromOffsetM = from, ToOffsetM = to, WidthM = to - from,
                LeftKind = "curb", RightKind = "curb", Reason = "no-confident-strip-label",
            });
        return record;
    }

    [Fact]
    public void GroupReview_IsReversibleAndOnlyExplicitSelectedRowsBecomeApprovals()
    {
        var first = Record("A", (1, 4), (5, 8));
        var second = Record("B", (1, 4.2));
        var model = new SpanLabelDecisionModel(new[] { first, second }, false);
        var group = model.ReviewGroups.Skip(1).Single();
        var rows = model.RowsForReview(group);
        rows.Should().HaveCount(3);
        model.Approvals.Should().BeEmpty("group navigation and selection are not engineering decisions");
        model.ApplyLabelToSelected(rows.Take(2).ToArray(), "מדרכה").Should().BeNull();
        model.Approvals.Should().HaveCount(2);
        model.Approvals.Should().OnlyContain(item => ReferenceEquals(item.Record, first));
        model.RowsForReview(model.ReviewGroups[0]).Should().HaveCount(3);
        model.ClearApprovals();
        model.Approvals.Should().BeEmpty();
        first.PresentationCoverage.UnresolvedSpans.Should().OnlyContain(span => span.SuggestedLabel == null);
    }

    [Fact]
    public void ChangedSourceEvidence_InvalidatesExistingGroupAndDoesNotChangeRows()
    {
        var record = Record("A", (1, 4));
        var model = new SpanLabelDecisionModel(new[] { record }, false);
        var group = model.ReviewGroups[1];
        record.PresentationCoverage.ExplicitSpanOverrides.Add(new()
        {
            OffsetM = 2, Label = "מדרכה", Source = "source-hatch-region", Evidence = new string('b', 64),
        });
        var navigate = () => model.RowsForReview(group);
        navigate.Should().Throw<InvalidOperationException>().WithMessage("*השתנו*");
        var apply = () => model.ApplyLabelToSelected(group.Rows, "מדרכה");
        apply.Should().Throw<InvalidOperationException>();
        model.Approvals.Should().BeEmpty();
    }

    [Fact]
    public void GroupedName_ValidatesAllWidthsBeforeMutatingAnyRow()
    {
        var model = new SpanLabelDecisionModel(new[] { Record("A", (1, 4), (5, 5.5)) }, false);
        model.ApplyLabelToSelected(model.RowsForReview(model.ReviewGroups[1]), "נתיב נסיעה")
            .Should().Contain("לא שונו שמות");
        model.Rows.Should().OnlyContain(row => row.Label == "" && !row.IsApproved);
        model.ApplyLabelToSelected(model.Rows.Take(1).ToArray(), "נתיב נסיעה").Should().BeNull();
        model.Approvals.Should().ContainSingle();
    }

    [Fact]
    public void SavedFalseHighConfidence_DoesNotHideLocalConflictOrInvalidWidth()
    {
        var record = Record("A", (1.6122744125413946, 8.11249069961695));
        var wide = record.PresentationCoverage.UnresolvedSpans[0];
        wide.SuggestedLabel = "נתיב נסיעה"; wide.StrongReviewCandidate = true;
        var conflict = new SectionUnresolvedSpanPlan
        {
            FromOffsetM = -0.6917361187231119, ToOffsetM = 0.8488760316700082,
            WidthM = 1.54061215039312, LeftKind = "island", RightKind = "island",
            Reason = "conflicting-strip-label-evidence", SuggestedLabel = "אי תנועה",
            StrongReviewCandidate = true,
        };
        record.PresentationCoverage.UnresolvedSpans.Add(conflict);
        foreach (var label in new[] { "מדרכה", "אי תנועה" })
            record.PresentationCoverage.ExplicitSpanOverrides.Add(new()
            { OffsetM = 0.07856995647344817, Label = label, Source = "source-hatch-partial-conflict", Evidence = label });
        var model = new SpanLabelDecisionModel(new[] { record }, true);
        model.MarkStrongSuggestions();
        model.Approvals.Should().BeEmpty();
        model.Rows.Single(row => row.HasLocalConflict).Evidence.Should().Contain("מדרכה").And.Contain("אי תנועה");
        model.ReviewGroups.Should().Contain(group => group.Title.Contains("סתירת מקור"));
    }
}
