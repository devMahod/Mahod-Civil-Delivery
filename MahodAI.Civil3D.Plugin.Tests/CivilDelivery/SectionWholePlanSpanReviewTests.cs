using System;
using System.IO;
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

public sealed class SectionWholePlanSpanReviewTests
{
    // physical: the synthetic source evidence of SectionSpanPhysicalFixture (CL (-30,0)..(30,0), crossing and pre-manual
    // digest), which a record needs before its spans can be approved; without it approval is refused ("rerun PLAN").
    private static SectionPlanRecord Record(int index, int spanCount = 4, string? sourceHandle = null, double offsetM = 0,
        bool physical = false)
    {
        var id = "CL-" + index;
        var record = new SectionPlanRecord
        {
            RecordId = id,
            SectionId = "STA-" + (12145 + index * 20),
            Station = 12145 + index * 20,
            SelectedAlignment = "600",
            Cl = new ClSourceRecord
            {
                RecordId = id,
                SourceDrawing = "C:/local/CL.dwg",
                SourceDrawingHash = new string('A', 64),
                SourceHandle = sourceHandle ?? (0x100 + index).ToString("X"),
                SourceEntityType = "LINE",
                SourceLayer = "GFC111",
                SourceEndpoints = physical ? new[] { -30d, 0d, 30d, 0d } : new[] { 0d, 0d, 20d, 0d },
                WcsEndpoints = physical ? new[] { -30d, 0d, 30d, 0d } : new[] { 0d, 0d, 20d, 0d },
            },
        };
        record.PresentationCoverage.RowAuthorityState = "authoritative";
        for (var span = 0; span < spanCount; span++)
            record.PresentationCoverage.UnresolvedSpans.Add(new SectionUnresolvedSpanPlan
            {
                FromOffsetM = span * 4d + offsetM,
                ToOffsetM = span * 4d + 3.5 + offsetM,
                WidthM = 3.5,
                LeftKind = "curb",
                RightKind = "curb",
                Reason = "no-confident-strip-label",
            });
        if (physical) SectionSpanPhysicalFixture.Capture(record);
        return record;
    }

    private static SectionPlan Plan(params SectionPlanRecord[] records) => new()
    {
        RunId = "batch-review-fixture",
        ProjectProfileId = "6422",
        SourceDrawing = "C:/local/host.dwg",
        SourceDatabaseRevision = "db:1",
        Records = records.ToList(),
    };

    [Fact]
    public void RecordedWorkload_All108SpansRemainVisible_AndEightSuggestionsStartUnchecked()
    {
        // Recorded workload counts, not a fabricated reconstruction of source geometry.
        var plan = Plan(Enumerable.Range(0, 24).Select(i => Record(i, i < 12 ? 5 : 4)).ToArray());
        foreach (var record in plan.Records.Take(8))
        {
            var span = record.PresentationCoverage.UnresolvedSpans[0];
            span.SuggestedLabel = "מדרכה";
            span.SuggestionConfidence = "high";
            span.StrongReviewCandidate = true;
        }
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false);

        model.SectionCount.Should().Be(24);
        model.Rows.Should().HaveCount(108);
        model.Rows.Count(row => row.Label == "מדרכה").Should().Be(8);
        model.Rows.Count(row => row.Label == "").Should().Be(100);
        model.Approvals.Should().BeEmpty();
        model.CanSave("engineer").Should().BeFalse();
        scope.RequireUnchanged(plan);

        model.MarkStrongSuggestions(); // An explicit user action, never called by the new handler.
        model.Approvals.Should().HaveCount(8);
        scope.RequireUnchanged(plan);
    }

    [Fact]
    public void PartialChoicesAcrossTwoSections_SaveOnlyThoseExactSpansWithoutPropagation()
    {
        var plan = Plan(Record(0, physical: true), Record(1, physical: true), Record(2, physical: true));
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false);
        var profile = new ProjectProfile { ProfileId = "6422" };
        model.Rows[0].Label = "מדרכה";
        model.Rows[5].Label = "גינון";
        scope.RequireUnchanged(plan);

        var count = SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(
            profile, scope.TargetRecords, model.Approvals, "engineer", DateTime.UtcNow);

        count.Should().Be(2);
        profile.Sections.Decisions.SpanLabels.Select(row => row.SourceHandle)
            .Should().Equal("100", "101");
        profile.Sections.Decisions.SpanLabels.Select(row => row.FromOffsetM)
            .Should().Equal(0d, 4d);
        profile.Sections.Decisions.SpanLabels.Select(row => row.Label)
            .Should().Equal("מדרכה", "גינון");
        model.Rows.Count(row => !row.IsApproved).Should().Be(10);
        scope.RequireUnchanged(plan);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.005d)]
    public void OnlyOnePhysicalDuplicateChecked_IsRejectedBeforeAnyProfileDecisionCanBePersisted(double offset)
    {
        var first = Record(0, 1);
        var twin = Record(1, 1, sourceHandle: "100", offsetM: offset); // Distinct RecordId, same persisted decision identity.
        var plan = Plan(first, twin);
        var model = new SpanLabelDecisionModel(plan.Records, initiallyApproveStrongSuggestions: false);
        model.Rows[0].Label = "מדרכה";
        model.Approvals.Should().ContainSingle();
        model.Rows[1].IsApproved.Should().BeFalse();
        var profile = new ProjectProfile { ProfileId = "6422" };
        var beforeProfile = JsonSerializer.Serialize(profile);
        var beforePlan = JsonSerializer.Serialize(plan);

        Action approve = () =>
        {
            // Production captures this scope before even opening the modal. Replay
            // an already chosen partial selection to prove it still cannot persist.
            var scope = SectionSpanBatchReviewScope.Capture(plan);
            scope.RequireUnchanged(plan);
            SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(
                profile, scope.TargetRecords, model.Approvals, "engineer", DateTime.UtcNow);
        };
        approve.Should().Throw<InvalidOperationException>().WithMessage("*אותה רצועת מקור*");
        JsonSerializer.Serialize(profile).Should().Be(beforeProfile);
        JsonSerializer.Serialize(plan).Should().Be(beforePlan);
        profile.Sections.Decisions.SpanLabels.Should().BeEmpty();
    }

    [Fact]
    public void SameSourceWithDistinctMeasuredSpans_RemainsAUsablePartialBatch()
    {
        var first = Record(0, 1, physical: true);
        var other = Record(1, 1, sourceHandle: "100", offsetM: 4d, physical: true);
        var plan = Plan(first, other);
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false);
        model.Rows[1].Label = "גינון";
        var profile = new ProjectProfile { ProfileId = "6422" };

        SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(
            profile, scope.TargetRecords, model.Approvals, "engineer", DateTime.UtcNow).Should().Be(1);
        profile.Sections.Decisions.SpanLabels.Should().ContainSingle().Which.FromOffsetM.Should().Be(4d);
        model.Rows[0].IsApproved.Should().BeFalse();
        scope.RequireUnchanged(plan);
    }

    [Fact]
    public void NoApproval_ChangesNeitherProfileNorPlan()
    {
        var plan = Plan(Record(0), Record(1));
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var model = new SpanLabelDecisionModel(scope.TargetRecords, initiallyApproveStrongSuggestions: false);
        var profile = new ProjectProfile { ProfileId = "6422" };
        var before = JsonSerializer.Serialize(profile);
        var act = () => SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(
            profile, scope.TargetRecords, model.Approvals, "engineer", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(before);
        scope.RequireUnchanged(plan);
    }

    [Theory]
    [InlineData("excluded")]
    [InlineData("alignment")]
    [InlineData("row")]
    [InlineData("resolved")]
    public void EligibilityDoesNotBypassEngineeringPrerequisites(string blocked)
    {
        var eligible = Record(0);
        var other = Record(1);
        switch (blocked)
        {
            case "excluded": other.Action = PlanAction.Excluded; break;
            case "alignment": other.SelectedAlignment = null; break;
            case "row": other.PresentationCoverage.RowAuthorityState = "ambiguous"; break;
            case "resolved": other.PresentationCoverage.UnresolvedSpans.Clear(); break;
        }
        SectionSpanBatchReviewScope.EligibleRecords(Plan(other, eligible))
            .Should().ContainSingle().Which.Should().BeSameAs(eligible);
        eligible.PresentationCoverage.RowAuthorityState = "nocandidates";
        SectionSpanBatchReviewScope.EligibleRecords(Plan(eligible)).Should().ContainSingle();
        SectionSpanBatchReviewScope.EligibleRecords(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("replace-plan")]
    [InlineData("replace-unselected-target")]
    [InlineData("replace-ineligible-row")]
    [InlineData("remove-target")]
    [InlineData("add-row")]
    [InlineData("edit-span")]
    [InlineData("edit-source")]
    [InlineData("edit-row-authority")]
    public void AnyWholePlanIdentityOrContentChange_RejectsAcceptedChoices(string change)
    {
        var plan = Plan(Record(0), Record(1), Record(2));
        plan.Records[2].Action = PlanAction.Excluded;
        var scope = SectionSpanBatchReviewScope.Capture(plan);
        var current = plan;
        switch (change)
        {
            case "replace-plan": current = Plan(plan.Records.ToArray()); break;
            case "replace-unselected-target": plan.Records[1] = Record(1); break;
            case "replace-ineligible-row":
                var replacement = Record(2);
                replacement.Action = PlanAction.Excluded;
                plan.Records[2] = replacement;
                break;
            case "remove-target": plan.Records.RemoveAt(1); break;
            case "add-row": plan.Records.Add(Record(3)); break;
            case "edit-span": plan.Records[1].PresentationCoverage.UnresolvedSpans[0].SuggestedLabel = "גינון"; break;
            case "edit-source": plan.SourceDatabaseRevision = "db:2"; break;
            case "edit-row-authority": plan.Records[1].PresentationCoverage.RowAuthorityState = "ambiguous"; break;
        }
        var act = () => scope.RequireUnchanged(current);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DuplicateRecordIds_AndEmptyTargetBatches_AreRejectedBeforeDialog()
    {
        var duplicate = () => SectionSpanBatchReviewScope.Capture(Plan(Record(0), Record(0)));
        duplicate.Should().Throw<InvalidOperationException>();
        var empty = () => SectionSpanBatchReviewScope.Capture(Plan());
        empty.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ProductionHandler_UsesOriginalWholePlanScope_OneSaveOneReplan_AndCancelReturnsFirst()
    {
        var root = typeof(SectionWholePlanSpanReviewTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        var source = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI",
            "CivilDeliveryControl.SectionBatchReview.cs"));
        var modal = source.IndexOf("if (CivilModalHost.ShowFromPalette(dialog) != true) return;", StringComparison.Ordinal);
        modal.Should().BeGreaterThan(source.IndexOf("CaptureProfileDecisionScope(", StringComparison.Ordinal));
        var accepted = source[modal..];
        accepted.IndexOf("RequireSectionBatchDecisionScope(scope);", StringComparison.Ordinal)
            .Should().BeLessThan(accepted.IndexOf("CloneProfileForDecision(", StringComparison.Ordinal));
        source.Should().Contain("initiallyApproveStrongSuggestions: false")
            .And.Contain("previousDecisions: scope.ProfileScope.Profile.Sections.Decisions.SpanLabels")
            .And.Contain("return SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(")
            .And.Contain("SectionsWorkflowService.RequirePlanEvidence(scope.PlanScope.Plan)")
            .And.Contain("RequireFreshSectionPlan(SpanBatchDecisionStage)")
            .And.Contain("RequireProfileDecisionScope(scope.ProfileScope)")
            .And.Contain("expectedState: scope.ProfileScope.ExpectedState")
            .And.NotContain("SectionsGrid.SelectedItem")
            .And.NotContain("MarkStrongSuggestions()")
            .And.NotContain("CaptureExpectedProfileState(");
        accepted.Should().NotContain("EnsureSavedForAction(");
        source.Split("ProjectProfileWriter.Save(", StringSplitOptions.None).Should().HaveCount(2);
        source.Split("OnPlan(this, new RoutedEventArgs())", StringSplitOptions.None).Should().HaveCount(2);
        accepted.IndexOf("RequireSectionBatchDecisionScope(scope);", accepted.IndexOf("scope.PlanScope.ApproveReviewedLabels(", StringComparison.Ordinal), StringComparison.Ordinal)
            .Should().BeLessThan(accepted.IndexOf("ProjectProfileWriter.Save(", StringComparison.Ordinal));
    }
}
