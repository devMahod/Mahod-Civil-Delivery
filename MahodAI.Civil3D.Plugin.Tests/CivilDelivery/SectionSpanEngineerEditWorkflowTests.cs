using System;
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

public sealed class SectionSpanEngineerEditWorkflowTests
{
    private static readonly DateTime At = new(2026, 9, 9, 17, 0, 0, DateTimeKind.Utc);
    private static SectionPlanRecord Record(string handle = "7CA3") => new()
    {
        RecordId = "cl-" + handle, SectionId = "STA12145", Station = 12145.4295, SelectedAlignment = "600",
        Cl = new ClSourceRecord
        {
            RecordId = "cl-" + handle, SourceDrawing = "CL.dwg", SourceDrawingHash = new string('a', 64),
            SourceHandle = handle, SourceLayer = "GFC111", SourceEntityType = "LINE",
            SourceEndpoints = new[] { -30d, 0d, 30d, 0d }, WcsEndpoints = new[] { -30d, 0d, 30d, 0d },
        },
        PresentationCoverage = new() { RowAuthorityState = "authoritative" },
    };
    private static SectionUnresolvedSpanPlan Span(double from, double to) => new()
    {
        FromOffsetM = from, ToOffsetM = to, WidthM = to - from,
        LeftKind = "curb", RightKind = "curb", Reason = "no-confident-strip-label",
    };
    private static ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision Old(
        SectionPlanRecord record, double from, double to, string label) => new()
    {
        SourceDrawingHash = record.Cl.SourceDrawingHash, SourceHandle = record.Cl.SourceHandle,
        AlignmentName = record.SelectedAlignment, FromOffsetM = from, ToOffsetM = to,
        Label = label, ApprovedBy = "Arthur", ApprovedAtUtc = At.AddDays(-6),
    };
    private static void Resolved(SectionPlanRecord record, double from, double to, string label) =>
        record.PresentationCoverage.ResolvedSpans.Add(new()
        {
            FromOffsetM = from, ToOffsetM = to, WidthM = to - from,
            LeftKind = "curb", RightKind = "curb", Label = label,
            EvidenceSource = "source-mark", EvidenceDigest = new string('b', 64),
        });

    [Fact]
    public void ActualChangedBoundaryWidths_ShowHistoryWithoutTransfer_AndCancelKeepsProfileAndPlan()
    {
        var record = Record();
        record.PresentationCoverage.UnresolvedSpans.AddRange(new[]
        {
            Span(-8.279132296405557, -4.038714345659342),
            Span(6.806031049330396, 8.039481591133603),
        });
        var profile = new ProjectProfile();
        profile.Sections.Decisions.SpanLabels.AddRange(new[]
        {
            Old(record, -8.449, -4.038714345659342, "נתיב נסיעה"),
            Old(record, 6.721038839517879, 8.039481591133603, "מדרכה"),
            Old(Record("BEEF"), -8.449, -4.038714345659342, "שם לא שייך"),
        });
        var before = JsonSerializer.Serialize(profile);
        var plan = JsonSerializer.Serialize(record);
        var model = new SpanLabelDecisionModel(new[] { record }, false, profile.Sections.Decisions.SpanLabels, true);
        model.Approvals.Should().BeEmpty();
        model.Rows[0].PreviousNames.Should().ContainSingle().Which.Label.Should().Be("נתיב נסיעה");
        model.Rows[0].PreviousSummary.Should().Contain("4.410");
        model.Rows[0].CurrentSummary.Should().Contain("4.240");
        model.Rows[0].SelectedPreviousName = model.Rows[0].PreviousNames[0];
        model.Approvals.Should().BeEmpty("choosing history is not reassignment");
        model.ReassignPreviousName(model.Rows[0], model.Rows[0].SelectedPreviousName).Should().BeNull();
        model.Approvals.Should().ContainSingle().Which.Span.FromOffsetM.Should().Be(-8.279132296405557);
        model.Rows[1].IsApproved.Should().BeFalse();
        JsonSerializer.Serialize(profile).Should().Be(before, "closing/canceling this detached draft writes nothing");
        JsonSerializer.Serialize(record).Should().Be(plan);
        new SpanLabelDecisionModel(new[] { record }, false, profile.Sections.Decisions.SpanLabels, true)
            .Approvals.Should().BeEmpty("reopening after cancel does not retain unsaved approval flags");
    }

    [Fact]
    public void CurrentNameEdit_IsExplicit_NewServiceOnly_AndUntouchedSectionIsPreserved()
    {
        var record = Record();
        Resolved(record, -4, 0, "מדרכה");
        SectionSpanPhysicalFixture.Capture(record);
        var other = Record("BEEF");
        var profile = new ProjectProfile();
        profile.Sections.Decisions.SpanLabels.AddRange(new[]
        { Old(record, -4, 0, "מדרכה"), Old(other, -4, 0, "גינון"), Old(record, -4.01, 0, "עבר") });
        var untouched = JsonSerializer.Serialize(profile.Sections.Decisions.SpanLabels[1]);
        var model = new SpanLabelDecisionModel(new[] { record }, false, profile.Sections.Decisions.SpanLabels, true);
        model.Rows.Should().ContainSingle().Which.Label.Should().Be("מדרכה");
        model.Approvals.Should().BeEmpty();
        model.Rows[0].Label = "רצועת בטיחות";
        var oldApi = () => SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(profile,
            new[] { record }, model.Approvals, "Arthur", At);
        oldApi.Should().Throw<InvalidOperationException>();
        SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(profile,
            new[] { record }, model.GetValidatedApprovals(), "Arthur", At).Should().Be(1);
        profile.Sections.Decisions.SpanLabels.Should().HaveCount(4, "keyless history is retained, not silently migrated");
        var saved = profile.Sections.Decisions.SpanLabels.Single(item => item.SourceHandle == "7CA3" && item.FromOffsetM == -4 && item.PhysicalDecisionKey != null);
        saved.Label.Should().Be("רצועת בטיחות");
        saved.AllowSourceLabelOverride.Should().BeTrue();
        profile.Sections.Decisions.SpanLabels.Should().Contain(item =>
            item.SourceHandle == "7CA3" && item.FromOffsetM == -4 && item.PhysicalDecisionKey == null && item.Label == "מדרכה");
        JsonSerializer.Serialize(profile.Sections.Decisions.SpanLabels.Single(item => item.SourceHandle == "BEEF"))
            .Should().Be(untouched);
    }

    [Fact]
    public void ChangedTargetsAndDuplicateSelectionsFailBeforeAnyProfileMutation()
    {
        var record = Record();
        record.PresentationCoverage.UnresolvedSpans.Add(Span(-4, 0));
        SectionSpanPhysicalFixture.Capture(record);
        var profile = new ProjectProfile();
        var before = JsonSerializer.Serialize(profile);
        var stale = new SectionDecisionProfileService.SpanLabelBatchApproval(record, Span(-4.000001, 0), "מדרכה");
        var act = () => SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(profile, new[] { record }, new[] { stale }, "Arthur", At);
        act.Should().Throw<InvalidOperationException>();
        var exact = stale with { Span = record.PresentationCoverage.UnresolvedSpans[0] };
        act = () => SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(profile, new[] { record }, new[] { exact, exact }, "Arthur", At);
        act.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(before);
        var model = new SpanLabelDecisionModel(new[] { record }, false);
        model.Rows[0].Label = "מדרכה";
        record.PresentationCoverage.UnresolvedSpans.Add(Span(1, 2));
        var save = () => model.GetValidatedApprovals();
        save.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SelectionActionsNeverTouchHiddenOrUnselectedRows()
    {
        var record = Record();
        record.PresentationCoverage.UnresolvedSpans.AddRange(new[] { Span(-4, 0), Span(1, 4) });
        foreach (var span in record.PresentationCoverage.UnresolvedSpans)
        { span.SuggestedLabel = "מדרכה"; span.StrongReviewCandidate = true; }
        var model = new SpanLabelDecisionModel(new[] { record }, false);
        model.MarkStrongSuggestions(new[] { model.Rows[0] }).Should().BeNull();
        model.Approvals.Should().ContainSingle();
        model.ClearApprovals(Array.Empty<SpanLabelDecisionModel.SpanRow>()).Should().NotBeNull();
        model.Approvals.Should().ContainSingle();
        model.ClearApprovals(new[] { model.Rows[1] }).Should().BeNull();
        model.Approvals.Should().ContainSingle();
    }

    [Fact]
    public void EditedSourceNameFlowsThroughActualPlanSeamAndSharedReplayWithNewFingerprint()
    {
        var record = Record();
        Resolved(record, -4, 0, "מדרכה");
        var marks = new[]
        {
            new SectionProjectionLogic.PresentationMark(-4, "curb", "", "A"),
            new SectionProjectionLogic.PresentationMark(0, "curb", "", "B"),
            new SectionProjectionLogic.PresentationMark(4, "curb", "", "C"),
            new SectionProjectionLogic.PresentationMark(-2, "strip", "מדרכה", "original-label"),
        };
        var original = SectionProjectionLogic.AnalyzePresentationCoverage(marks);
        SectionSpanPhysicalFixture.Capture(record, original);
        var profile = new ProjectProfile();
        var model = new SpanLabelDecisionModel(new[] { record }, false, includeResolvedSpans: true);
        model.Rows[0].Label = "רצועת בטיחות";
        SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(profile, new[] { record }, model.Approvals, "Arthur", At);
        var seam = typeof(SectionPlanService).GetMethod("BuildManualSpanOverrides", BindingFlags.Static | BindingFlags.NonPublic)!;
        var reviewed = (System.Collections.Generic.List<SectionProjectionLogic.SpanLabelOverride>)
            seam.Invoke(null, new object[] { record, original, profile })!;
        reviewed.Should().ContainSingle().Which.Source.Should().Be(SectionReviewedSpanLabelLogic.Source);
        var next = SectionProjectionLogic.AnalyzePresentationCoverage(marks, approvedOverrides: reviewed);
        next.StripLabels.Should().Contain((-4d, 0d, "רצועת בטיחות"));
        next.DimensionMarks.Should().Equal(original.DimensionMarks);
        next.Summary.EvidenceDigest.Should().NotBe(original.Summary.EvidenceDigest);
    }

    private static SectionProjectionLogic.PresentationAnalysis SourceNamedSpan(
        double from, double to, string? label) => SectionProjectionLogic.AnalyzePresentationCoverage(
        new[]
        {
            new SectionProjectionLogic.PresentationMark(from, "curb", "", "left-source"),
            new SectionProjectionLogic.PresentationMark(to, "curb", "", "right-source"),
        },
        approvedOverrides: label == null ? null : new[]
        {
            new SectionProjectionLogic.SpanLabelOverride((from + to) / 2, label,
                "traffic-arrow", "original-source-evidence"),
        });

    private static System.Collections.Generic.List<SectionProjectionLogic.SpanLabelOverride> PlanOverrides(
        SectionPlanRecord record, SectionProjectionLogic.PresentationAnalysis current, ProjectProfile profile) =>
        (System.Collections.Generic.List<SectionProjectionLogic.SpanLabelOverride>)
            typeof(SectionPlanService).GetMethod("BuildManualSpanOverrides", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { record, current, profile })!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeylessHistoryRequiresReapprovalEvenWhenSourceNameAgrees_AndDoesNotMutateProfile(bool native67Offsets)
    {
        var record = Record();
        var profile = new ProjectProfile();
        var from = native67Offsets ? 0.9438776343591934 : -4;
        var to = native67Offsets ? 6.721038839517879 : 0;
        profile.Sections.Decisions.SpanLabels.Add(Old(record,
            native67Offsets ? 0.9438640556749155 : from,
            native67Offsets ? 6.720942150220831 : to, "נתיב נסיעה"));
        var current = SourceNamedSpan(from, to, "נתיב נסיעה");
        SectionSpanPhysicalFixture.Capture(record, current);
        current.UnresolvedSpans.Should().BeEmpty();
        var beforeProfile = JsonSerializer.Serialize(profile);
        var beforeAnalysis = JsonSerializer.Serialize(current, new JsonSerializerOptions { IncludeFields = true });

        PlanOverrides(record, current, profile).Should().BeEmpty("agreement is not a new source-label override");

        record.Findings.Should().ContainSingle(finding => finding.Code == SectionFindingCodes.SpanLabelDecisionStale,
            "a matching name is not proof of the historical physical identity");
        JsonSerializer.Serialize(profile).Should().Be(beforeProfile);
        JsonSerializer.Serialize(current, new JsonSerializerOptions { IncludeFields = true }).Should().Be(beforeAnalysis);
        profile.Sections.Decisions.SpanLabels.Single().AllowSourceLabelOverride.Should().BeFalse();
    }

    [Theory]
    [InlineData(-8.44901268616232, -8.279132296405557, -4.038714345659342, "נתיב נסיעה")]
    [InlineData(6.720942150220831, 6.806031049330396, 8.039481591133603, "מדרכה")]
    public void ChangedNative67Boundary_RemainsStaleEvenIfCurrentSourceUsesSameName(
        double approvedFrom, double currentFrom, double to, string label)
    {
        var record = Record();
        var profile = new ProjectProfile();
        profile.Sections.Decisions.SpanLabels.Add(Old(record, approvedFrom, to, label));
        var before = JsonSerializer.Serialize(profile);

        PlanOverrides(record, SourceNamedSpan(currentFrom, to, label), profile).Should().BeEmpty();

        record.Findings.Should().ContainSingle(finding => finding.Code == SectionFindingCodes.SpanLabelDecisionStale);
        JsonSerializer.Serialize(profile).Should().Be(before);
    }

    [Theory]
    [InlineData("different-label")]
    [InlineData("duplicate-approval")]
    [InlineData("missing-approver")]
    [InlineData("missing-time")]
    [InlineData("non-utc-time")]
    [InlineData("empty-label")]
    [InlineData("oversized-label")]
    [InlineData("nonfinite-bound")]
    [InlineData("incredible-vehicle-width")]
    [InlineData("duplicate-current-label")]
    [InlineData("duplicate-current-span")]
    public void ResolvedSourceDoesNotHideConflictingAmbiguousOrInvalidLegacyDecision(string scenario)
    {
        var record = Record();
        var profile = new ProjectProfile();
        var from = scenario == "incredible-vehicle-width" ? -20 : -4;
        var current = SourceNamedSpan(from, 0, "נתיב נסיעה");
        var decision = Old(record, from, 0, "נתיב נסיעה");
        profile.Sections.Decisions.SpanLabels.Add(decision);
        switch (scenario)
        {
            case "different-label": decision.Label = "מדרכה"; break;
            case "duplicate-approval": profile.Sections.Decisions.SpanLabels.Add(Old(record, from, 0, "נתיב נסיעה")); break;
            case "missing-approver": decision.ApprovedBy = " "; break;
            case "missing-time": decision.ApprovedAtUtc = null; break;
            case "non-utc-time": decision.ApprovedAtUtc = DateTime.SpecifyKind(At, DateTimeKind.Unspecified); break;
            case "empty-label": decision.Label = " "; break;
            case "oversized-label": decision.Label = new string('x', 81); break;
            case "nonfinite-bound": decision.FromOffsetM = double.NaN; break;
            case "incredible-vehicle-width": break;
            case "duplicate-current-label": current.StripLabels.Add(current.StripLabels.Single()); break;
            case "duplicate-current-span": current.WidthSpans.Add(current.WidthSpans.Single()); break;
            default: throw new InvalidOperationException(scenario);
        }
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var before = JsonSerializer.Serialize(profile, options);

        PlanOverrides(record, current, profile).Should().BeEmpty();

        record.Findings.Count(finding => finding.Code == SectionFindingCodes.SpanLabelDecisionStale)
            .Should().Be(profile.Sections.Decisions.SpanLabels.Count);
        JsonSerializer.Serialize(profile, options).Should().Be(before);
    }

    [Fact]
    public void KeylessUnresolvedHistoryAndInvalidCompetingApprovalAreBothVisibleButNeverApplied()
    {
        var record = Record();
        var profile = new ProjectProfile();
        profile.Sections.Decisions.SpanLabels.Add(Old(record, -4, 0, "מדרכה"));
        var invalid = Old(record, -4, 0, "מדרכה");
        invalid.ApprovedBy = " ";
        profile.Sections.Decisions.SpanLabels.Add(invalid);

        var current = SourceNamedSpan(-4, 0, null);
        SectionSpanPhysicalFixture.Capture(record, current);
        var before = JsonSerializer.Serialize(profile);
        var overrides = PlanOverrides(record, current, profile);

        overrides.Should().BeEmpty();
        record.Findings.Count(finding => finding.Code == SectionFindingCodes.SpanLabelDecisionStale).Should().Be(2,
            "both historical decisions remain visible individually, without manufacturing current keys");
        JsonSerializer.Serialize(profile).Should().Be(before);
    }

    [Fact]
    public void NewExplicitEngineerOverrideStillTakesPriorityOverDifferentlyNamedSource()
    {
        var record = Record();
        var profile = new ProjectProfile();
        var current = SourceNamedSpan(-4, 0, "נתיב נסיעה");
        Resolved(record, -4, 0, "נתיב נסיעה");
        SectionSpanPhysicalFixture.Capture(record, current);
        SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(profile, new[] { record },
            new[] { new SectionDecisionProfileService.SpanLabelBatchApproval(record, Span(-4, 0), "מדרכה") }, "Arthur", At)
            .Should().Be(1);
        profile.Sections.Decisions.SpanLabels.Single().PhysicalDecisionKey.Should().NotBeNullOrEmpty();

        var overrides = PlanOverrides(record, current, profile);

        overrides.Should().ContainSingle().Which.Source.Should().Be(SectionReviewedSpanLabelLogic.Source);
        overrides.Single().Label.Should().Be("מדרכה");
        record.Findings.Should().BeEmpty();
    }
}
