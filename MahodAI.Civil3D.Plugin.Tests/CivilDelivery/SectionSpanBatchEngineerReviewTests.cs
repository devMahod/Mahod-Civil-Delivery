using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionSpanBatchEngineerReviewTests
{
    private static readonly DateTime At = new(2026, 9, 22, 12, 14, 26, DateTimeKind.Utc);
    // Exact cl-7D23 interval from native76 PLAN; other fixtures are deliberately synthetic.
    private const double From = -13.584604118516921;
    private const double To = -10.128232740555044;

    private static SectionUnresolvedSpanPlan Span(double from, double to) => new()
    {
        FromOffsetM = from, ToOffsetM = to, WidthM = to - from,
        LeftKind = "curb", RightKind = "curb", Reason = "conflicting-strip-label-evidence",
    };

    private static SectionPlanRecord Record(string handle = "7D23") => WithPhysicalSource(new()
    {
        RecordId = "cl-" + handle, SectionId = "STA-12264", SelectedAlignment = "600",
        Cl = new()
        {
            RecordId = "cl-" + handle, SourceDrawing = "CL.dwg", SourceDrawingHash = new string('a', 64),
            SourceHandle = handle, SourceLayer = "GFC111", SourceEntityType = "LINE",
            SourceEndpoints = new[] { -30d, 0d, 30d, 0d }, WcsEndpoints = new[] { -30d, 0d, 30d, 0d },
        },
        PresentationCoverage = new()
        {
            RowAuthorityState = "authoritative",
            UnresolvedSpans = { Span(From, To), Span(To, To + 3.5) },
        },
    });

    private static SectionPlanRecord WithPhysicalSource(SectionPlanRecord record)
    {
        SectionSpanPhysicalFixture.Capture(record);
        return record;
    }

    private static SectionPlan Plan(params SectionPlanRecord[] records) => new()
    {
        RunId = "whole-plan-engineer-review", ProjectProfileId = "6422",
        SourceDrawing = "C:/local/host.dwg", SourceDatabaseRevision = "db:1", Records = records.ToList(),
    };

    private static ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision Previous(
        SectionPlanRecord record, double from, double to, string label) => new()
    {
        SourceDrawingHash = record.Cl.SourceDrawingHash, SourceHandle = record.Cl.SourceHandle,
        AlignmentName = record.SelectedAlignment, FromOffsetM = from, ToOffsetM = to,
        Label = label, ApprovedBy = "Arthur", ApprovedAtUtc = At.AddDays(-1),
    };

    private static List<SectionProjectionLogic.SpanLabelOverride> PlanOverrides(
        SectionPlanRecord record, SectionProjectionLogic.PresentationAnalysis analysis, ProjectProfile profile) =>
        (List<SectionProjectionLogic.SpanLabelOverride>)typeof(SectionPlanService)
            .GetMethod("BuildManualSpanOverrides", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { record, analysis, profile })!;

    [Theory]
    [InlineData("נתיב נסיעה")]
    [InlineData("מדרכה")]
    public void ExplicitWholePlanConflictReview_MatchesSelectedEditor_AndReplanResolvesOnlyCheckedName(string label)
    {
        var record = Record();
        var other = Record("BEEF");
        var marks = new[]
        {
            new SectionProjectionLogic.PresentationMark(From, "curb", "", "left"),
            new SectionProjectionLogic.PresentationMark(To, "curb", "", "middle"),
            new SectionProjectionLogic.PresentationMark(To + 3.5, "curb", "", "right"),
            new SectionProjectionLogic.PresentationMark((From + To) / 2, "strip", "מדרכה", "label-A"),
            new SectionProjectionLogic.PresentationMark(To + 1.75, "strip", "מדרכה", "label-B"),
        };
        var source = new[]
        {
            new SectionProjectionLogic.SpanLabelOverride((From + To) / 2, "נתיב נסיעה", "traffic-arrow", "arrow-A"),
            new SectionProjectionLogic.SpanLabelOverride(To + 1.75, "נתיב נסיעה", "traffic-arrow", "arrow-B"),
        };
        var original = SectionProjectionLogic.AnalyzePresentationCoverage(marks, approvedOverrides: source);
        SectionSpanPhysicalFixture.Capture(record, original);
        SectionSpanPhysicalFixture.Capture(other, original);
        var plan = Plan(record, other);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var profile = new ProjectProfile();
        profile.Sections.Decisions.SpanLabels.Add(Previous(record, From, To, "מדרכה"));
        profile.Sections.Decisions.SpanLabels.Add(Previous(other, From, To, "גינון"));
        var otherBefore = JsonSerializer.Serialize(profile.Sections.Decisions.SpanLabels[1]);
        var selectedProfile = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(profile))!;
        var model = new SpanLabelDecisionModel(scope.TargetRecords, false, profile.Sections.Decisions.SpanLabels);
        model.Approvals.Should().BeEmpty("neither a conflicting name nor history is automatically approved");
        model.Rows.Single(row => ReferenceEquals(row.Record, record) && row.Span.FromOffsetM == From).Label = label;
        var approvals = model.GetValidatedApprovals();
        original.UnresolvedSpans.Should().HaveCount(2);
        var legacyProfile = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(profile))!;
        SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(legacyProfile, scope.TargetRecords, approvals, "Arthur", At);
        var legacy = SectionProjectionLogic.AnalyzePresentationCoverage(marks,
            approvedOverrides: source.Concat(PlanOverrides(record, original, legacyProfile)).ToArray());
        legacy.UnresolvedSpans.Should().HaveCount(2, "the previous batch path reproduced the false completion");

        scope.ApproveReviewedLabels(plan, profile, approvals, "Arthur", At).Should().Be(1);
        SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(selectedProfile,
            new[] { record }, approvals, "Arthur", At).Should().Be(1);
        JsonSerializer.Serialize(profile).Should().Be(JsonSerializer.Serialize(selectedProfile));
        var reviewed = PlanOverrides(record, original, profile);
        reviewed.Should().ContainSingle().Which.Source.Should().Be(SectionReviewedSpanLabelLogic.Source);
        var next = SectionProjectionLogic.AnalyzePresentationCoverage(marks,
            approvedOverrides: source.Concat(reviewed).ToArray());

        next.StripLabels.Should().Contain((From, To, label));
        next.UnresolvedSpans.Should().ContainSingle().Which.From.Should().Be(To);
        next.DimensionMarks.Should().Equal(original.DimensionMarks);
        next.WidthSpans.Should().Equal(original.WidthSpans);
        next.Summary.EvidenceDigest.Should().NotBe(original.Summary.EvidenceDigest);
        JsonSerializer.Serialize(profile.Sections.Decisions.SpanLabels.Single(item => item.SourceHandle == "BEEF"))
            .Should().Be(otherBefore);
        record.PresentationCoverage.UnresolvedSpans.Should().HaveCount(2, "the captured PLAN itself is immutable");
    }

    [Fact]
    public void HistoryAtOldBounds_IsDisplayOnly_UntilExplicitReassignmentToCurrentSpan()
    {
        var record = Record();
        var plan = Plan(record);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var profile = new ProjectProfile();
        profile.Sections.Decisions.SpanLabels.Add(Previous(record, From - 0.15, To, "מדרכה"));
        var before = JsonSerializer.Serialize(profile);
        var planBefore = JsonSerializer.Serialize(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, false, profile.Sections.Decisions.SpanLabels);
        var row = model.Rows[0];
        row.PreviousNames.Should().ContainSingle().Which.FromOffsetM.Should().Be(From - 0.15);
        row.SelectedPreviousName = row.PreviousNames[0];
        model.Approvals.Should().BeEmpty();
        model.ReassignPreviousName(row, row.SelectedPreviousName).Should().BeNull();
        model.GetValidatedApprovals().Should().ContainSingle().Which.Span.FromOffsetM.Should().Be(From);
        JsonSerializer.Serialize(profile).Should().Be(before, "editing then cancelling is detached from persisted choices");
        JsonSerializer.Serialize(plan).Should().Be(planBefore);
        new SpanLabelDecisionModel(scope.TargetRecords, false, profile.Sections.Decisions.SpanLabels)
            .Approvals.Should().BeEmpty("reopening after cancellation does not apply the cancelled name");

        scope.ApproveReviewedLabels(plan, profile, model.GetValidatedApprovals(), "Arthur", At).Should().Be(1);
        profile.Sections.Decisions.SpanLabels.Should().HaveCount(2, "old bounds remain history, not reassigned silently");
        profile.Sections.Decisions.SpanLabels.Single(item => item.AllowSourceLabelOverride)
            .FromOffsetM.Should().Be(From);
    }

    [Theory]
    [InlineData("boundary")]
    [InlineData("source")]
    [InlineData("alignment")]
    [InlineData("row-authority")]
    [InlineData("replacement-plan")]
    public void SourceOrPlanChangeAfterReview_RejectsBeforeAnyProfileMutation(string mutation)
    {
        var record = Record();
        var plan = Plan(record);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var profile = new ProjectProfile();
        var before = JsonSerializer.Serialize(profile);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, false);
        model.Rows[0].Label = "מדרכה";
        var approvals = model.GetValidatedApprovals();
        switch (mutation)
        {
            case "boundary": record.PresentationCoverage.UnresolvedSpans[0] = Span(From + 0.000001, To); break;
            case "source": record.Cl.SourceEndpoints[0] += 0.1; break;
            case "alignment": record.SelectedAlignment = "700"; break;
            case "row-authority": record.PresentationCoverage.RowAuthorityState = "unconfirmed"; break;
            case "replacement-plan": plan = Plan(record); break;
            default: throw new InvalidOperationException(mutation);
        }
        var act = () => scope.ApproveReviewedLabels(plan, profile, approvals, "Arthur", At);
        act.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(before);
    }

    [Fact]
    public void UndisplayedResolvedSpan_CannotPiggybackOnWholePlanUnresolvedReview()
    {
        var record = Record();
        record.PresentationCoverage.ResolvedSpans.Add(new()
        {
            FromOffsetM = 2, ToOffsetM = 5, WidthM = 3, LeftKind = "curb", RightKind = "curb",
            Label = "מדרכה", EvidenceSource = "source-mark", EvidenceDigest = new string('b', 64),
        });
        SectionSpanPhysicalFixture.Capture(record);
        var plan = Plan(record);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var profile = new ProjectProfile();
        var before = JsonSerializer.Serialize(profile);
        var approvals = new[] { new SectionDecisionProfileService.SpanLabelBatchApproval(record, Span(2, 5), "גינון") };
        var act = () => scope.ApproveReviewedLabels(plan, profile, approvals, "Arthur", At);
        act.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(before);
    }
}
