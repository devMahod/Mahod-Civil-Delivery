using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Native wiring contracts supplement (not replace) pure recovery/geometry behavioral tests.</summary>
public sealed class SectionVerificationRecoveryContractTests
{
    [Fact] public void ApplyAndVerifyReadActualNativeSegmentTopologyBeforeNormalizingSurfacePoints()
    {
        var text = Source("Sections", "Services", "SectionAnnotationPlacementContract.cs");
        text.Should().Contain("sectionPoint.SegmentTo")
            .And.Contain("SectionSurfaceTopologyLogic.RequireSingleChain(rawByIndex[0])")
            .And.Contain("SectionSurfaceTopologyLogic.RequireSingleChain(rawByIndex[1])")
            .And.Contain("existingRaw.Select(point => (point.X, point.Y, point.Z))")
            .And.Contain("designRaw.Select(point => (point.X, point.Y, point.Z))");
        Source("Sections", "Services", "SectionDecorationService.cs")
            .Should().Contain("SectionAnnotationPlacementContract.ReadSurfaceChains(");
        Source("Sections", "Services", "SectionVerifyService.cs")
            .Should().Contain("SectionAnnotationPlacementContract.ReadSurfaceChains(");
        Source("Sections", "Services", "SectionVerificationRecoveryService.cs")
            .Should().Contain("SectionAnnotationPlacementContract.ReadSurfaceChains(");
    }

    private static string Source(params string[] parts)
    {
        var root = typeof(SectionVerificationRecoveryContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(new[] { root, "CivilDelivery" }.Concat(parts).ToArray()));
    }

    [Fact] public void ActualApplyRunOwnsNewViewAndSampleLineNotPlanRun()
    {
        var text = Source("Sections", "Services", "SectionApplyService.cs");
        // 1.3.10: the projection sources are collected once before any view and passed in (SEC-B3 content band)
        text.Should().Contain("batchGroupScope: !selectedScope, applyRunId: result.RunId,").And.Contain("collected: collected);");
        foreach (var role in new[] { "sample-line", "section-view" })
        {
            var start = text.IndexOf("Role = \"" + role + "\"", StringComparison.Ordinal);
            var body = text.Substring(start, text.IndexOf("CreatedByToolVersion", start, StringComparison.Ordinal) - start);
            body.Should().Contain("RunId = applyRunId,").And.NotContain("RunId = plan.RunId,");
        }
    }

    [Fact] public void RecoveryFollowsExactOwnedProducerAndDoesNotSearchHistory()
    {
        var text = Source("Sections", "Services", "SectionVerificationRecoveryService.cs");
        text.Should().Contain("SafeRunId(owner.RunId)")
            .And.Contain("SectionsWorkflowService.RequireApplyEvidence(applied)")
            .And.Contain("SectionsWorkflowService.RequirePlanEvidence(producer)")
            .And.Contain("manifest.ArtifactHashes")
            .And.Contain("owner.RunId == applied.RunId")
            .And.NotContain("Directory.GetFiles")
            .And.NotContain("Directory.EnumerateDirectories")
            .And.NotContain("OrderByDescending");
    }

    [Fact] public void SourceProofSamplesRealNativeCutAndNeverUsesNamesAlone()
    {
        var text = Source("Sections", "Services", "SectionVerificationRecoveryService.cs");
        text.Should().Contain("tin.SampleElevations(p1, p2)")
            .And.Contain("grid.SampleElevations(p1, p2)")
            .And.Contain("SectionVerificationRecoveryPolicy.SurfaceMismatch")
            .And.Contain("surface.IsOutOfDate")
            .And.Contain("surface.IsReferenceObject")
            .And.Contain("surface.GetReferenceInfo()")
            .And.Contain("key.IsSourceDrawingExistent")
            .And.Contain("surface.IsReferenceStale")
            .And.Contain("ArtifactHash.Sha256OfFile(sourceDrawing)")
            .And.NotContain("not yet bound by the recovery source-proof adapter")
            .And.Contain("live_source_geometry_supported");
    }

    [Fact] public void RelocationProofDoesNotReplaceSelectedContractOrMandatoryLiveVerification()
    {
        var recovery = Source("Sections", "Services", "SectionVerificationRecoveryService.cs");
        recovery.Should().Contain("SectionVerificationRecoveryPolicy.Rejection(")
            .And.Contain("Identity(producer, original), Identity(current, record)")
            .And.Contain("var sources = CompareExternalSources(producer, current);")
            .And.Contain("if (!sources.Equivalent) throw new InvalidOperationException(sources.Reason);");
        var verify = Source("Sections", "Services", "SectionVerifyService.cs");
        verify.Should().Contain("true, recordId, null, revalidatedInputs: true, profile: profile)")
            .And.Contain("if (revalidatedInputs)")
            .And.Contain("SectionVerificationRecoveryService.CheckLiveSources(")
            .And.Contain("foreach (var relocation in authority.SourceRelocations)")
            .And.Contain("Code = \"SEC-SOURCE-RELOCATION-PROVEN\"")
            .And.Contain("Severity = FindingSeverity.Info");
    }

    [Fact] public void SelectedRouteDoesNotRequireAnInMemoryApplyAndShowUsesCommittedHandles()
    {
        var text = Source("UI", "CivilDeliveryControl.xaml.cs");
        var start = text.IndexOf("private void OnVerifySelected", StringComparison.Ordinal);
        var body = text.Substring(start, text.IndexOf("private void InvalidateEstimateEvidence", start, StringComparison.Ordinal) - start);
        body.Should().Contain("VerifySelectedCurrent(").And.NotContain("|| _apply == null");
        text.Should().Contain("var handle = SelectedSectionViewHandle(row);");
    }

    [Fact] public void FailedRecoveryPublishesFreshPlanInPaletteAndWithdrawsOldApplyAndGreen()
    {
        var workflow = Source("Sections", "Services", "SectionsWorkflowService.cs");
        workflow.Should().Contain("throw new VerificationRefreshRequiredException(current, ex)");
        var ui = Source("UI", "CivilDeliveryControl.xaml.cs");
        var start = ui.IndexOf("catch (SectionsWorkflowService.VerificationRefreshRequiredException ex)", StringComparison.Ordinal);
        var body = ui.Substring(start, ui.IndexOf("catch (Exception ex)", start, StringComparison.Ordinal) - start);
        body.Should().Contain("_plan = ex.CurrentPlan;")
            .And.Contain("_apply = null;").And.Contain("_lastVerifyResult = null;")
            .And.Contain("_sectionDisplayStatuses.Clear();")
            .And.Contain("RebuildSectionRows(row.Record.RecordId)")
            .And.Contain("currentRecord?.Action == PlanAction.Unchanged")
            .And.Contain("SetBlockingStatus(");
    }

    [Fact] public void DatumAndOffsetPositionsUseMeasuredLayoutWithoutDroppingTheirSemanticChecks()
    {
        var text = Source("Sections", "Services", "SectionVerifyService.cs");
        text.Should().Contain("label.Handle, out var placedLabel)")
            .And.Contain("datumEvidence.Handle, out var placedDatum)")
            .And.Contain("datumSourceExact && datumAnnotations.Count == 1")
            .And.Contain("string.Equals(entity.Text, label.Text, StringComparison.Ordinal)")
            .And.Contain("RequirePlacedLabelBounds(measuredLayout, nativeAnnotations)");
    }
}
