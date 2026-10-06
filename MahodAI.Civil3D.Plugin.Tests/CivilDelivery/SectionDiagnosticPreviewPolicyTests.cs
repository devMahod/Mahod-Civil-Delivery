using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionDiagnosticPreviewPolicyTests
{
    [Theory]
    [InlineData(SectionFindingCodes.PresentationCoverageMissing)]
    [InlineData(SectionFindingCodes.TrafficDirectionUnresolved)]
    public void PresentationOnlyReview_IsEligibleAfterDeterministicLayout(string findingCode)
    {
        var record = ReviewRecord(findingCode);
        var plan = Plan(record);

        SectionDiagnosticPreviewPolicy.CanAssignLayout(record).Should().BeTrue();
        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeFalse(
            "the transient still needs a deterministic PLAN position");

        record.PlannedLayoutPosition = new[] { 1200.0, 3400.0 };

        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeTrue();
        SectionDiagnosticPreviewPolicy.PreviewBlockReason(plan, record).Should().BeNull();
        record.Status.Should().Be(DeliveryStatus.ReviewRequired,
            "diagnostic preview must never approve the engineering record");
        record.Action.Should().Be(PlanAction.ReviewRequired);
    }

    [Theory]
    [InlineData(SectionFindingCodes.ClNoIntersection)]
    [InlineData(SectionFindingCodes.SourceMissing)]
    [InlineData(SectionFindingCodes.AlignmentAmbiguous)]
    [InlineData("SEC-FUTURE-UNKNOWN-REVIEW")]
    public void NonPresentationReview_IsDeniedByClosedWhitelist(string findingCode)
    {
        var record = ReviewRecord(findingCode);
        record.PlannedLayoutPosition = new[] { 100.0, 200.0 };
        var plan = Plan(record);

        SectionDiagnosticPreviewPolicy.CanAssignLayout(record).Should().BeFalse();
        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeFalse();
        SectionDiagnosticPreviewPolicy.PreviewBlockReason(plan, record)
            .Should().Contain("נחסמה");
    }

    [Fact]
    public void RecordError_IsDeniedEvenWhenReviewCodeIsWhitelisted()
    {
        var record = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing);
        record.Findings.Add(Finding(
            SectionFindingCodes.UtilityProjectionFailed, FindingSeverity.Error));
        record.PlannedLayoutPosition = new[] { 100.0, 200.0 };
        var plan = Plan(record);

        SectionDiagnosticPreviewPolicy.CanAssignLayout(record).Should().BeFalse();
        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeFalse();
    }

    [Fact]
    public void PlanErrorOrBlockedStatus_IsDeniedBeforeRecordPreview()
    {
        var record = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing);
        record.PlannedLayoutPosition = new[] { 100.0, 200.0 };
        var plan = Plan(record);
        plan.Findings.Add(Finding(
            SectionFindingCodes.ProjectionGeometryUnsupported, FindingSeverity.Error));

        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeFalse();
        SectionDiagnosticPreviewPolicy.PreviewBlockReason(plan, record)
            .Should().Contain("התוכנית חסומה");

        plan.Findings.Clear();
        plan.Status = DeliveryStatus.Blocked;
        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeFalse();
    }

    [Fact]
    public void MissingCrossingOrExactSurfaceEvidence_IsDenied()
    {
        var noCrossing = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing);
        noCrossing.SelectedCrossing = null;
        SectionDiagnosticPreviewPolicy.CanAssignLayout(noCrossing).Should().BeFalse();

        var oneSurface = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing);
        oneSurface.PlannedSources.RemoveAt(1);
        SectionDiagnosticPreviewPolicy.CanAssignLayout(oneSurface).Should().BeFalse();

        var duplicateSurface = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing);
        duplicateSurface.PlannedSources[1] = Surface("EG", "A1");
        SectionDiagnosticPreviewPolicy.CanAssignLayout(duplicateSurface).Should().BeFalse();
    }

    [Fact]
    public void PreviewEligibleReview_RemainsAnUnresolvedBatchAndApplyTargetSelectionFailsClosed()
    {
        var record = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing);
        record.PlannedLayoutPosition = new[] { 100.0, 200.0 };
        var plan = Plan(record);

        SectionDiagnosticPreviewPolicy.CanPreview(plan, record).Should().BeTrue();
        SectionPlanLogic.UnresolvedBatchRecords(plan).Should().ContainSingle()
            .Which.Should().BeSameAs(record);

        var gate = WorkflowGate.From(
            profileUsable: true,
            plan,
            planStale: false,
            apply: null,
            previewShown: false);
        gate.CanPreview.Should().BeTrue();
        gate.CanApply.Should().BeFalse();

        // SelectTargets is host-coupled through its containing Civil service and is
        // source-contract tested below. The two host-free gates already prove that
        // this record cannot reach that boundary.
        record.Status.Should().NotBe(DeliveryStatus.Ready);
    }

    [Fact]
    public void PreviewLayoutCandidates_AreStableAndExcludeUnsafeReviewRows()
    {
        var safe = ReviewRecord(SectionFindingCodes.PresentationCoverageMissing, "safe", 200.0);
        var traffic = ReviewRecord(SectionFindingCodes.TrafficDirectionUnresolved, "traffic", 100.0);
        var unsafeRecord = ReviewRecord(SectionFindingCodes.ClNoIntersection, "unsafe", 50.0);
        var inputs = new[] { safe, traffic, unsafeRecord }
            .Where(SectionDiagnosticPreviewPolicy.CanAssignLayout)
            .Select(record => new SectionLayoutPlanner.Input(
                record.RecordId, record.Station, record.SectionId))
            .ToList();
        var options = new SectionLayoutPlanner.Options
        {
            Columns = 3,
            SpacingX = 120,
            SpacingY = 80,
            OriginX = 1000,
            OriginY = 2000,
        };

        var first = SectionLayoutPlanner.Plan(inputs, options);
        var second = SectionLayoutPlanner.Plan(inputs, options);

        first.Select(cell => cell.RecordId).Should().Equal("traffic", "safe");
        second.Should().BeEquivalentTo(first, config => config.WithStrictOrdering());
    }

    private static SectionPlan Plan(SectionPlanRecord record)
    {
        var plan = new SectionPlan
        {
            RunId = "preview-plan",
            ProjectProfileId = "6422",
            SourceDatabaseRevision = "drawing-guid:12",
            SourceUnitCode = SectionSourceIntegrityLogic.MetresUnitCode,
            Status = DeliveryStatus.ReviewRequired,
        };
        plan.Records.Add(record);
        return plan;
    }

    private static SectionPlanRecord ReviewRecord(
        string findingCode,
        string id = "review-1",
        double station = 100.0)
    {
        var crossing = new AlignmentCrossing
        {
            AlignmentName = "MAIN",
            Point = new[] { 0.0, 0.0 },
            Station = station,
            TangentDeg = 0.0,
        };
        var record = new SectionPlanRecord
        {
            RecordId = id,
            SectionId = id,
            Cl = new ClSourceRecord
            {
                RecordId = id,
                SourceDrawing = "CL.dwg",
                SourceDrawingHash = "hash-cl",
                SourceHandle = id,
                SourceEntityType = "LINE",
                SourceLayer = "CL",
                SourceEndpoints = new[] { 0.0, 20.0, 0.0, -20.0 },
                WcsEndpoints = new[] { 0.0, 20.0, 0.0, -20.0 },
            },
            SelectedAlignment = crossing.AlignmentName,
            SelectedCrossing = crossing,
            Station = station,
            LeftExtent = 20.0,
            RightExtent = 20.0,
            Status = DeliveryStatus.ReviewRequired,
            Action = PlanAction.ReviewRequired,
        };
        record.PlannedSources.Add(Surface("EG", "A1"));
        record.PlannedSources.Add(Surface("DESIGN", "B2"));
        record.Findings.Add(Finding(findingCode, FindingSeverity.ReviewRequired));
        record.Findings.Add(Finding(
            SectionFindingCodes.RowAuthorityUnresolved, FindingSeverity.Warning));
        return record;
    }

    private static SectionSourcePlan Surface(string name, string handle) => new()
    {
        SourceName = name,
        SourceType = "surface",
        SourceHandle = handle,
        NativeSampleCapability = true,
        PlannedState = "sampled",
        AdapterRequired = false,
        Required = true,
        Status = DeliveryStatus.Ready,
    };

    private static DeliveryFinding Finding(string code, FindingSeverity severity) => new()
    {
        Code = code,
        Domain = SectionPlanLogic.Domain,
        Severity = severity,
        Title = code,
    };
}
