using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Native-boundary supplements to the executable Core recovery-policy tests.
/// These checks do not claim an AutoCAD transaction was executed offline.</summary>
public sealed class SectionDeadAnnotationRecoveryWiringTests
{
    private static string Source(string name)
    {
        var root = typeof(SectionDeadAnnotationRecoveryWiringTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", name + ".cs"));
    }

    [Fact]
    public void RecoveryRunsAfterScopeAndFreshnessButBeforeStyleAndGeometryMutations()
    {
        var source = Source("SectionApplyService");
        var start = source.IndexOf("private bool ApplyCoreScoped(");
        source = source[start..];
        var repair = source.IndexOf("SectionAnnotationRegistry.RepairDeadEntries(");
        repair.Should().BeGreaterThan(source.IndexOf("SectionsWorkflowService.RequirePlanEvidence(plan)"));
        repair.Should().BeGreaterThan(source.IndexOf("SectionPlanLogic.ScopeStaleReason("));
        repair.Should().BeGreaterThan(source.IndexOf("SectionInputIntegrityService.ValidateCurrent("));
        repair.Should().BeGreaterThan(source.IndexOf("ReferenceEquals(SelectSingleTarget"));
        repair.Should().BeLessThan(source.IndexOf("SectionViewPresentationStyleService.Ensure("));
        source.Should().Contain("SectionFindingCodes.AnnotationRegistryRepairable")
            .And.Contain("EligibleDeadAnnotationRecoveryKeys(")
            .And.Contain("annotationInventory = SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db)");
    }

    [Fact]
    public void RegistryCleanupWritesOnlyPreparedRegistryObjectsAndReprovesStrictInventory()
    {
        var source = Source("SectionAnnotationRegistry");
        source = source[source.IndexOf("internal static int RepairDeadEntries(")..source.IndexOf("internal static string RegistryKeyFor(")];
        source.Should().Contain("ProbeHandleState(tr, db, hex)")
            .And.Contain("prepared.Add(")
            .And.Contain("item.Record.Erase()")
            .And.Contain("item.Record.Data = new ResultBuffer(item.Remaining)")
            .And.Contain("ValidateOwnedLayerInventory(tr, db)")
            .And.NotContain("entity.Erase(")
            .And.NotContain("Commit(");
        source.IndexOf("prepared.Add(").Should().BeLessThan(source.IndexOf("item.Record.UpgradeOpen()"));
    }

    [Fact]
    public void PlanOnlyProposesEligibleCurrentOwnedUpdatesAndVerifyRemainsStrict()
    {
        var plan = Source("SectionPlanService");
        plan.Should().Contain("r.Action == PlanAction.Update && r.ManualSectionReuse == null")
            .And.Contain("SectionOwnedStateLogic.CanRepairDeadAnnotations(")
            .And.Contain("SectionFindingCodes.AnnotationRegistryRepairable")
            .And.NotContain("RepairDeadEntries(");
        var verify = Source("SectionVerifyService");
        verify.Should().Contain("ValidateOwnedLayerInventory(tr, db)")
            .And.NotContain("RepairDeadEntries(")
            .And.NotContain("EligibleDeadAnnotationRecoveryKeys(");
    }
}
