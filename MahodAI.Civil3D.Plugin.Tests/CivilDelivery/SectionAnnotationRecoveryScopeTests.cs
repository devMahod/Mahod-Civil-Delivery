using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionAnnotationRecoveryScopeTests
{
    private static SectionPlan Plan(params string[] repairIds)
    {
        var plan = new SectionPlan { RunId = "fixture-run", ProjectProfileId = "6422",
            SourceUnitCode = 6, SourceDatabaseRevision = "revision", SourceDrawing = "fixture.dwg" };
        foreach (var id in new[] { "A", "B", "C" })
            plan.Records.Add(new SectionPlanRecord
            {
                RecordId = id, Status = DeliveryStatus.Ready, Action = PlanAction.Update,
                Cl = new ClSourceRecord
                {
                    RecordId = id, SourceDrawing = "fixture.dwg", SourceDrawingHash = new string('a', 64),
                    SourceHandle = id, SourceEntityType = "Line", SourceLayer = "CL",
                    SourceEndpoints = new[] { -1d, 0, 1, 0 }, WcsEndpoints = new[] { -1d, 0, 1, 0 },
                },
            });
        foreach (var id in repairIds)
            plan.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.AnnotationRegistryRepairable, Domain = "sections",
                Title = "Registered annotations require scoped recovery",
                Severity = FindingSeverity.Warning, AffectedRecordIds = { id },
            });
        return plan;
    }

    [Fact]
    public void SelectOtherRowReportsExactSingleRepairTargetBeforeMutation()
    {
        var plan = Plan("A");
        var selected = plan.Records[1];
        var finding = SectionInputIntegrityService.SelectedAnnotationRecoveryScopeFinding(plan, selected);
        finding.Should().NotBeNull();
        finding!.Code.Should().Be(SectionFindingCodes.AnnotationRepairScopeRequired);
        finding.AffectedRecordIds.Should().Equal("A");
        finding.Message.Should().Contain("outside_selected_scope=A").And.Contain("requires_batch=False");
        SectionInputIntegrityService.HasSelectedApplyPlanningIntegrityBlocker(plan, selected).Should().BeTrue();
        SectionInputIntegrityService.SelectedApplyPlanningBlockers(plan, selected).Should().ContainSingle();
        SectionInputIntegrityService.HasSelectedApplyPlanningIntegrityBlocker(plan, plan.Records[0]).Should().BeFalse();
        plan.Records.Should().OnlyContain(r => r.Status == DeliveryStatus.Ready);
    }

    [Fact]
    public void MultipleRepairTargetsExplainCompleteBatchAndNeverRelaxBatchGate()
    {
        var plan = Plan("A", "B");
        var finding = SectionInputIntegrityService.SelectedAnnotationRecoveryScopeFinding(plan, plan.Records[0]);
        finding!.AffectedRecordIds.Should().Equal("A", "B");
        finding.Message.Should().Contain("outside_selected_scope=B").And.Contain("requires_batch=True");
        SectionInputIntegrityService.AnnotationRecoveryScope(plan, new[] { "A", "B", "C" }).CanProceed.Should().BeTrue();
        SectionInputIntegrityService.HasPlanningIntegrityBlocker(plan).Should().BeFalse();
    }

    [Fact]
    public void OriginalVerifyGateDoesNotMistakeHistoricPlanRepairWarningsForCurrentInventoryFailure()
    {
        var plan = Plan("A", "B");
        SectionInputIntegrityService.HasSelectedPlanningIntegrityBlocker(plan, plan.Records[0]).Should().BeFalse();
        SectionInputIntegrityService.HasSelectedApplyPlanningIntegrityBlocker(plan, plan.Records[0]).Should().BeTrue();
        SectionInputIntegrityService.SelectedPlanningBlockers(plan, plan.Records[0]).Should().BeEmpty();
        // VERIFY independently calls strict live ValidateOwnedLayerInventory; this
        // test proves only that already-resolved PLAN warnings do not preempt it.
    }
}
