using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.CivilDelivery;

/// <summary>
/// Pure row/policy behavior plus explicit host wiring checks. No WPF interaction or
/// Civil execution: the observed 61 topology was two strips with four resolved arrows.
/// </summary>
public sealed class SectionGuidanceDirectionRegressionTests
{
    [Fact]
    public void ReadyUpdateWithFourResolvedArrowDirections_OffersApplyWithoutAnOverride()
    {
        var row = Row();
        var before = JsonSerializer.Serialize(row.Record);
        Assert.True(row.CanEditTrafficDirections);
        Assert.False(row.CanResolveTrafficDirection);
        Assert.Equal(2, row.Record.PresentationCoverage.VehicleStripCount);
        Assert.Equal(4, row.Record.TrafficDirections.Count);
        Assert.Equal("4/4 מוכרעים", row.TrafficDirections);

        var result = SectionGuidedActionPolicy.Evaluate(Project(row, editorEnabled: true, applyEnabled: true));

        Assert.Equal(SectionGuidedActionKind.ApplySelected, result.Action);
        Assert.Equal("החל חתך נבחר", result.ButtonText);
        Assert.Equal(before, JsonSerializer.Serialize(row.Record));
    }

    [Theory]
    [InlineData(true, SectionGuidedActionKind.ResolveDirection)]
    [InlineData(false, SectionGuidedActionKind.InspectIssues)]
    public void ActualUnresolvedDirection_RemainsAPrerequisiteOnlyWhenItsEditorIsAdmitted(
        bool editorEnabled, SectionGuidedActionKind expected)
    {
        var row = Row(unresolved: true);
        Assert.True(row.CanResolveTrafficDirection);
        Assert.Equal(expected, SectionGuidedActionPolicy.Evaluate(
            Project(row, editorEnabled, applyEnabled: false)).Action);
    }

    [Theory]
    [InlineData("stale", SectionGuidedActionKind.Plan)]
    [InlineData("evidence", SectionGuidedActionKind.InspectIssues)]
    [InlineData("apply-disabled", SectionGuidedActionKind.InspectIssues)]
    public void ResolvedDirectionsDoNotBypassExistingFreshnessEvidenceOrApplyGates(
        string blocker, SectionGuidedActionKind expected)
    {
        var state = Project(Row(), editorEnabled: true, applyEnabled: blocker != "apply-disabled") with
        { StalePlan = blocker == "stale", EvidenceBlocked = blocker == "evidence" };
        Assert.Equal(expected, SectionGuidedActionPolicy.Evaluate(state).Action);
    }

    [Fact]
    public void AuthoritativelyVerifiedView_OffersShowEvenWhileReverifyAndEditingRemainAvailable()
    {
        var state = Project(Row(), editorEnabled: true, applyEnabled: false) with
        { HasCreatedView = true, SelectedVerified = true, CanVerifySelected = true };
        var result = SectionGuidedActionPolicy.Evaluate(state);
        Assert.Equal(SectionGuidedActionKind.ShowVerifiedView, result.Action);
        Assert.Equal("הצג חתך מאומת", result.ButtonText);
    }

    [Fact]
    public void HostProjection_RequiresUnresolvedDirectionAsWellAsEditorAvailability()
    {
        // This catches the actual regression: policy tests alone cannot prove the
        // palette did not confuse CanEditTrafficDirections with a missing decision.
        var source = GuidanceSource();
        Assert.Contains("CanResolveDirection = BtnResolveDirection.IsEnabled && row?.CanResolveTrafficDirection == true,", source);
        Assert.DoesNotContain("CanResolveDirection = BtnResolveDirection.IsEnabled,", source);
        Assert.Contains("CanApplySelected = BtnApplySelected.IsEnabled,", source);
        Assert.Contains("CanNameSpans = BtnNameSpans.IsEnabled && row?.CanNameSpans == true,", source);
    }

    [Fact]
    public void VerifiedShowRoute_IsNotDisabledByApprovalStateAndStillHonorsDrawingAndPendingSave()
    {
        // Only OpenDrawing is allowed without a document. Verified-view navigation
        // still needs a document, authoritative evidence, and no pending/busy action.
        var source = GuidanceSource().Replace("\r\n", "\n");
        Assert.Contains("BtnSectionNext.IsEnabled = !_sectionDrawingOpenPending && _busyProgress == null &&\n" +
            "            _pendingWorkflowSaveDocument == null &&\n" +
            "            (Doc() != null || _sectionGuidedAction.Action == SectionGuidedActionKind.OpenDrawing);", source);
        Assert.Contains("if (!BtnSectionNext.IsEnabled || _sectionGuidedAction == null) return;", source);
        Assert.Contains("case SectionGuidedActionKind.ShowVerifiedView:", source);
        Assert.Contains("case SectionGuidedActionKind.ShowExistingView: OnShow(sender, e); break;", source);
        Assert.Contains("status == DeliveryStatus.Verified && IsAuthoritativeVerify(_lastVerifyResult)", source);
        Assert.Contains("_apply is { Committed: true } apply", source);
        var verified = Project(Row(), editorEnabled: true, applyEnabled: false) with
        { HasCreatedView = true, SelectedVerified = true, CanVerifySelected = true };
        Assert.Equal(SectionGuidedActionKind.ShowVerifiedView, SectionGuidedActionPolicy.Evaluate(verified).Action);
        Assert.Equal(SectionGuidedActionKind.OpenDrawing,
            SectionGuidedActionPolicy.Evaluate(verified with { HasDrawing = false }).Action);
    }

    private static SectionGuidedActionSnapshot Project(SectionRowViewModel row, bool editorEnabled, bool applyEnabled) => new()
    {
        HasDrawing = true, ProfileUsable = true, HasPlan = true, SelectedRecord = true, PlanRecordCount = 28,
        CanResolveDirection = editorEnabled && row.CanResolveTrafficDirection,
        CanApplySelected = applyEnabled
    };

    private static SectionRowViewModel Row(bool unresolved = false)
    {
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var record = new SectionPlanRecord
        {
            RecordId = "synthetic-cl-7C8F", SelectedAlignment = "synthetic-alignment", Station = 42675.8492,
            Status = unresolved ? DeliveryStatus.ReviewRequired : DeliveryStatus.Ready, Action = PlanAction.Update,
            Cl = new ClSourceRecord
            {
                RecordId = "synthetic-cl-7C8F", SourceDrawing = "synthetic-local.dwg", SourceDrawingHash = digest,
                SourceHandle = "7C8F", SourceEntityType = "Line", SourceLayer = "CL",
                SourceEndpoints = new[] { 0d, 0, 20, 0 }, WcsEndpoints = new[] { 0d, 0, 20, 0 }
            },
            PresentationCoverage = new() { Complete = true, VehicleStripCount = 2, OfficeCarStripCount = 2 }
        };
        var offsets = new[] { -5.7, 1.3, 4.2, 7.7 };
        for (var index = 0; index < offsets.Length; index++) record.TrafficDirections.Add(new()
        {
            TrackEvidenceDigest = index == 0 ? null : digest,
            FromOffsetM = index == 0 ? -9 : -0.6, ToOffsetM = index == 0 ? -2.5 : 9.3,
            LaneMidOffsetM = offsets[index], StripLabel = index == 0 ? "נתיב נסיעה" : "נתיבי נסיעה",
            StripKind = "road", EvidenceMode = "motor", State = unresolved && index == 3 ? "unknown" : "resolved",
            Flow = "along-alignment", OfficeCarView = "rear", DirectionSource = "arrow",
            DirectionDigest = digest, Reason = "synthetic resolved-arrow fixture"
        });
        return new() { Record = record };
    }

    private static string GuidanceSource([CallerFilePath] string testFile = "") => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "MahodAI.Civil3D.Plugin",
            "CivilDelivery", "UI", "CivilDeliveryControl.SectionGuidance.cs")));
}
