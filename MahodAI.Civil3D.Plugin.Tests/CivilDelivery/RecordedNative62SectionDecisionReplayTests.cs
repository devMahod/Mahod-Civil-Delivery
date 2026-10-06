using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Explicit local acceptance lane against the saved real PLAN, not a new Civil run.
/// Synthetic approvals exist only in a detached test profile; no DWG or active
/// project profile is read for mutation or written. Missing evidence fails this lane.
/// </summary>
public sealed class RecordedNative62SectionDecisionReplayTests
{
    private static SectionPlan LoadRecordedPlan()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "runs",
            "sections-plan-20260910-100321-e3a563ad", "section_plan.json");
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(
            "16747D39F51AB1375359E4A52F32123068D0780FDF0D40A9AB7580BCD9FABF03");
        var plan = JsonSerializer.Deserialize<SectionPlan>(bytes, SectionsWorkflowService.Json)!;
        plan.Records.Should().HaveCount(28);
        plan.Records.Count(r => r.Status == DeliveryStatus.Ready).Should().Be(1);
        return plan;
    }

    [Fact]
    [Trait("Category", "RecordedNativeSectionReview")]
    public void WholeRecordedPlan_ReviewsEveryEligibleSpan_CancelDoesNotApproveOrChangePlan()
    {
        var plan = LoadRecordedPlan();
        var before = JsonSerializer.Serialize(plan, SectionsWorkflowService.Json);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false);
        model.SectionCount.Should().Be(scope.TargetRecords.Count);
        model.Rows.Should().HaveCount(scope.TargetRecords.Sum(r => r.PresentationCoverage.UnresolvedSpans.Count));
        model.Rows.Count.Should().BeGreaterThan(1);
        model.Approvals.Should().BeEmpty("source suggestions are not engineering approval");
        model.CanSave("SYNTHETIC TEST ONLY").Should().BeFalse();
        foreach (var group in model.ReviewGroups)
            model.RowsForReview(group).Should().OnlyContain(row => model.Rows.Contains(row));
        model.Rows[0].Label = "רצועת בדיקה סינתטית";
        model.GetValidatedApprovals().Should().ContainSingle();
        scope.RequireUnchanged(plan);
        JsonSerializer.Serialize(plan, SectionsWorkflowService.Json).Should().Be(before);
        new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false)
            .Approvals.Should().BeEmpty("discarding the editor draft must not keep unsaved choices");
    }

    [Fact]
    [Trait("Category", "RecordedNativeSectionReview")]
    public void TwoExplicitRecordedSpanEdits_AreRefusedWithoutPhysicalIdentity_AndChangedPlanRefusesStaleApproval()
    {
        // The recorded PLAN (10.09) predates the physical span key (WP3, 30.09): it has no pre-manual evidence digest
        // (none of its 28 records; 27 of them do carry a crossing), so an approval cannot be bound to the section's
        // physical identity and is refused ("rerun PLAN") —
        // an old record never gains authority retroactively. Nothing is written to the profile or the plan.
        var plan = LoadRecordedPlan();
        var before = JsonSerializer.Serialize(plan, SectionsWorkflowService.Json);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false);
        var first = model.Rows[0];
        var second = model.Rows.First(row => row.Record.RecordId != first.Record.RecordId);
        first.Label = "רצועת בדיקה סינתטית א";
        second.Label = "רצועת בדיקה סינתטית ב";
        var approvals = model.GetValidatedApprovals();
        approvals.Should().HaveCount(2);
        var profile = new ProjectProfile { ProfileId = "SYNTHETIC-OFFLINE-SECTION-REPLAY" };
        var emptyProfile = JsonSerializer.Serialize(profile);
        scope.RequireUnchanged(plan);
        Action approve = () => SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(profile, scope.TargetRecords,
            approvals, "SYNTHETIC TEST ONLY", new DateTime(2026, 9, 10, 13, 0, 0, DateTimeKind.Utc));
        approve.Should().Throw<InvalidOperationException>().WithMessage("*pre-manual physical identity*rerun PLAN*");
        JsonSerializer.Serialize(profile).Should().Be(emptyProfile);
        profile.Sections.Decisions.SpanLabels.Should().BeEmpty();
        JsonSerializer.Serialize(plan, SectionsWorkflowService.Json).Should().Be(before);
        plan.SourceDatabaseRevision += ":changed-after-editor-open";
        Action stale = () => scope.RequireUnchanged(plan);
        stale.Should().Throw<InvalidOperationException>();
    }
}
