using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionSpanRowAndXrefSourceContractTests
{
    private static string PluginSourceDir => typeof(SectionSpanRowAndXrefSourceContractTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string Read(params string[] parts) =>
        File.ReadAllText(parts.Aggregate(PluginSourceDir, Path.Combine));

    [Fact]
    public void EverySectionXrefWalker_UsesLoadedSnapshotAndOverlayVisibilityPolicies()
    {
        var guard = Read("CivilDelivery", "Sections", "Services",
            "SectionXrefSnapshotGuard.cs");
        guard.Should().Contain("definition.GetXrefDatabase(false)")
            .And.Contain("LoadedSnapshotFailure(")
            .And.Contain("TryReadDiskIdentity(resolved, out var before")
            .And.Contain("HashFileShared(resolved)")
            .And.Contain("TryReadDiskIdentity(resolved, out var after");

        foreach (var file in new[]
                 {
                     "ClInstructionReader.cs",
                     "SectionGeometryCollector.cs",
                     "SectionTrafficArrowCollector.cs",
                 })
        {
            var source = Read("CivilDelivery", "Sections", "Services", file);
            source.Should().Contain("SectionXrefSnapshotGuard.Cache", file)
                .And.Contain("xrefSnapshots.Validate(definition", file)
                .And.Contain("IsExternalReferenceExcludedByOverlay(", file)
                .And.Contain("hasExternalOverlayAncestor", file);
            var overlay = source.IndexOf(
                "IsExternalReferenceExcludedByOverlay(", StringComparison.Ordinal);
            var unresolved = source.IndexOf("IsUnloaded", overlay, StringComparison.Ordinal);
            overlay.Should().BeGreaterThan(0, file);
            unresolved.Should().BeGreaterThan(overlay,
                "non-visible nested overlay branches must be skipped before unresolved-XREF gates");
        }
    }

    [Fact]
    public void PanelProvidesSignedRowAndAllSpanDecisionPaths()
    {
        var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var spanDialog = Read("CivilDelivery", "UI",
            "SectionSpanLabelDecisionDialog.xaml");
        var rowDialog = Read("CivilDelivery", "UI",
            "SectionRowAuthorityDecisionDialog.xaml");

        xaml.Should().Contain("x:Name=\"BtnNameSpans\"")
            .And.Contain("x:Name=\"BtnApproveRow\"");
        ui.Should().Contain("SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(")
            .And.Contain("new[] { selected }")
            .And.Contain("includeResolvedSpans: true")
            .And.Contain("SectionDecisionProfileService.ApproveRowAuthority(")
            .And.Contain("OnPlan(this, new RoutedEventArgs())");
        spanDialog.Should().Contain("שמור שמות מסומנים")
            .And.Contain("Binding IsApproved")
            .And.Contain("Binding Suggestion")
            .And.Contain("Binding Evidence")
            .And.Contain("x:Name=\"ApproverBox\"");
        rowDialog.Should().Contain("SHA-256")
            .And.Contain("x:Name=\"ApproverBox\"");
    }

    [Fact]
    public void ApplyRepeatsRowGateAndUsesThePlannedSpanApprovals()
    {
        var source = Read("CivilDelivery", "Sections", "Services",
            "SectionDecorationService.cs");

        source.Should().Contain("SelectAuthoritativeRowMarks(")
            .And.Contain("plannedCoverage.RowAuthorityState")
            .And.Contain("plannedCoverage.RowCandidateSourceKeys.SequenceEqual(")
            .And.Contain("plannedCoverage.ExplicitSpanOverrides")
            .And.Contain("approvedOverrides: approvedOverrides")
            .And.Contain("presentation.UnresolvedSpans.Count != 0");
    }

    [Fact]
    public void EveryModalSectionDecision_RechecksScopeRevisionAndExternalHashesBeforeSave()
    {
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        ui.Should().Contain("private ActiveProjectProfileService.ActiveLoadResult RequireFreshSectionPlan(")
            .And.Contain("ActiveProjectProfileService.ReloadForExistingWorkflow(")
            .And.Contain("SectionPlanLogic.ScopeStaleReason(")
            .And.Contain("SectionInputIntegrityService.StaleReason(")
            .And.Contain("_plan.SourceDatabaseRevision");

        foreach (var pair in new[]
                 {
                     (Stage: "TRAFFIC-DIRECTION-DECISION", Mutation: "ApproveEditedTrafficDirectionsBatch("),
                     (Stage: "SPAN-LABEL-DECISION", Mutation: "ApproveEditedSpanLabelsBatch("),
                     (Stage: "ROW-AUTHORITY-DECISION", Mutation: "ApproveRowAuthority("),
                     (Stage: "SECTION-DECISION", Mutation: "ApproveCrossing("),
                 })
        {
            var guard = ui.IndexOf($"\"{pair.Stage}\"", StringComparison.Ordinal);
            var mutation = ui.IndexOf(pair.Mutation, guard, StringComparison.Ordinal);
            guard.Should().BeGreaterThan(0, pair.Stage);
            mutation.Should().BeGreaterThan(guard,
                "the live scope gate must run after the dialog and before profile mutation");
            var liveRecheck = ui.IndexOf("RequireSectionDecisionScope(decisionScope)", guard, StringComparison.Ordinal);
            liveRecheck.Should().BeGreaterThan(guard, pair.Stage);
            liveRecheck.Should().BeLessThan(mutation, "the current profile/source scope is checked again after the modal review");
        }
    }

    [Fact]
    public void SelectedSectionApply_IsExplicitAtomicAndDoesNotRelaxBatchApply()
    {
        var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var workflow = Read("CivilDelivery", "Sections", "Services",
            "SectionsWorkflowService.cs");
        var apply = Read("CivilDelivery", "Sections", "Services",
            "SectionApplyService.cs");
        var contracts = Read("CivilDelivery", "Sections", "Contracts",
            "SectionApplyModels.cs");

        xaml.Should().Contain("x:Name=\"BtnApplySelected\"")
            .And.Contain("Click=\"OnApplySelected\"");
        ui.Should().Contain("_sections.ApplySelected(")
            .And.Contain("selectedApply.Status == DeliveryStatus.Ready")
            .And.Contain("selectedApply.PresentationCoverage.Complete")
            .And.Contain("רק החתך הזה ייווצר. שאר השורות לא ישתנו.");
        workflow.Should().Contain("_applyService.ApplySelected(");
        apply.Should().Contain("public SectionApplyResult ApplySelected(")
            .And.Contain("using (var tr = db.TransactionManager.StartTransaction())")
            .And.Contain("DrawingRevisionTracker.Capture(db)")
            .And.Contain("ApplySelectedCore(")
            .And.Contain("selectedScope: true")
            .And.Contain("ReferenceEquals(SelectSingleTarget(plan, targets[0].RecordId), targets[0])")
            .And.Contain("target.PresentationCoverage.Complete")
            .And.Contain("target.PresentationCoverage.UnresolvedSpans.Count != 0")
            .And.Contain("target.TrafficDirections.Any(direction => !direction.IsResolved)")
            .And.Contain("apply-selected.arrange_views skipped; planned position retained")
            // The final evidence is captured after decoration so title/axes/blocks,
            // not just native SectionView extents, participate in overlap proof.
            .And.Contain("CaptureManagedVisualLayoutEvidence(tr, db, managedTargets, result);")
            .And.Contain("AssertManagedViewsDoNotOverlap(tr, db, targets, result);")
            .And.Contain("apply.final_visual_layout")
            .And.Contain("batchGroupScope: !selectedScope")
            .And.Contain("Sample line group logical key")
            .And.Contain("owned.Feature, \"sections\"")
            .And.Contain("owned.Role, \"sample-line-group\"")
            .And.Contain("owned.ProjectProfileId, plan.ProjectProfileId")
            .And.Contain("Multiple exact tool-owned sample line groups")
            .And.Contain("Both current and legacy tool-owned sample line groups")
            .And.Contain("SamplingReconciliationLogic.Mode.SelectedSharedGroup")
            .And.Contain("SamplingReconciliationLogic.Mode.BatchSharedGroup")
            .And.Contain("GroupIsSharedWithForeignWork(")
            .And.Contain("permittedGroupRecords")
            .And.Contain("OwnedSampleLineChildrenLogic.Decide(readable, children)")
            .And.Contain("TryEnumerateSectionViewIds(")
            .And.Contain("SectionViewPresentationStyleService.Ensure(")
            .And.Contain("allowModify: !selectedScope")
            .And.Contain("LayoutEvidenceContract.Capture(")
            .And.Contain("SectionViewOverlapService.Inspect(")
            .And.Contain("target.LogicalKey")
            .And.Contain("selectedScope: false")
            .And.Contain("SectionPlanLogic.UnresolvedBatchRecords(plan)")
            .And.Contain("expectedIds.SetEquals");
        contracts.Should().Contain("public string Scope { get; set; } = \"batch\"")
            .And.Contain("public string? SelectedRecordId { get; set; }");

        var source = Read("CivilDelivery", "Sections", "Services", "SectionSourceService.cs");
        source.Should().Contain("SamplingReconciliationLogic.Mode mode = SamplingReconciliationLogic.Mode.Batch")
            .And.Contain("SamplingReconciliationLogic.Decide(currentSampling, desired, mode)")
            .And.Contain("if (reconciliation.IsBlocked)")
            .And.Contain("SectionFindingCodes.GroupSamplingForeign");
        source.IndexOf("SamplingReconciliationLogic.Decide(", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("source.IsSampled = false;", StringComparison.Ordinal),
                "the refusal must be decided before any source setter runs");
        source.IndexOf("SamplingReconciliationLogic.Decide(", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("source.IsSampled = true;", StringComparison.Ordinal),
                "enabling also re-samples the shared group");
        source.Should().Contain("shared-group no-op");

        var decoration = Read("CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");
        decoration.Should().Contain("result.Scope, \"selected-record\"")
            .And.Contain("EnsureLayer(tr, db, AnnoLayer, allowModify, log)")
            .And.Contain("EnsureTextStyle(tr, db, allowModify)")
            .And.Contain("EnsureSectionStyles(tr, civilDoc, result, allowModify)")
            .And.Contain("SectionVehicleBlockService.Load(tr, db, log, allowModify)")
            .And.Contain("SharedResourceLogic.Mode.CreateOnlyNeverModify")
            .And.Contain("SectionFindingCodes.SharedResourceChangeRequired");
        var presentation = Read("CivilDelivery", "Sections", "Services", "SectionViewPresentationStyleService.cs");
        presentation.Should().Contain("bool allowModify = true")
            .And.Contain("SharedResourceLogic.Decide(true, IsCompliant(style)");
        var arrows = Read("CivilDelivery", "Sections", "Services", "SectionTrafficDirectionArrowService.cs");
        arrows.Should().Contain("if (!UsesByBlockDisplay(tr, existing))")
            .And.Contain("SectionFindingCodes.SharedResourceChangeRequired");
        var workflow2 = Read("CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs");
        workflow2.Should().Contain("PersistApplyEvidence(doc, profile, profileHash, plan, result);")
            .And.Contain("PersistVerifyEvidence(doc, profile, profileHash, plan, applied, result);")
            .And.Contain("SectionFindingCodes.EvidenceWriteFailed");
    }

    [Fact]
    public void SelectedSectionVerify_IsExactScopedFreshAndDoesNotGreenTheBatch()
    {
        var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var workflow = Read("CivilDelivery", "Sections", "Services",
            "SectionsWorkflowService.cs");
        var verify = Read("CivilDelivery", "Sections", "Services",
            "SectionVerifyService.cs");
        var contracts = Read("CivilDelivery", "Sections", "Contracts",
            "SectionApplyModels.cs");

        xaml.Should().Contain("x:Name=\"BtnVerifySelected\"")
            .And.Contain("Click=\"OnVerifySelected\"");
        ui.Should().Contain("_sections.VerifySelectedCurrent(")
            .And.Contain("verify.Scope, \"selected-record\"")
            .And.Contain("אומת חתך נבחר")
            .And.Contain("יתר החתכים לא אומתו");
        workflow.Should().Contain("public SectionVerifyResult VerifySelected(")
            .And.Contain("SectionPlanLogic.ScopeStaleReason(")
            .And.Contain("applied.Scope, \"selected-record\"")
            .And.Contain("_verifyService.VerifySelected(")
            .And.Contain("SectionVerificationRecoveryService.RecoverSelected(doc, current, recordId)")
            .And.Contain("_verifyService.VerifyRecoveredSelected(")
            .And.Contain("PersistVerifyEvidence(doc, profile, profileHash, current, authority.Applied, result)")
            .And.Contain("tr.Abort()");
        verify.Should().Contain("public SectionVerifyResult VerifySelected(")
            .And.Contain("selectedScope: true")
            .And.Contain("!selectedScope &&")
            .And.Contain("applied.Scope, \"batch\"")
            .And.Contain("applied.SelectedRecordId, selectedRecordId")
            .And.Contain("selectedPlanRecords[0].PresentationCoverage.Complete")
            .And.Contain("selectedPlanRecords[0].TrafficDirections.All")
            .And.Contain("SectionInputIntegrityService.ValidateCurrent(")
            .And.Contain("SectionVerificationRecoveryService.CheckLiveSources(")
            .And.Contain("VerifyOne(")
            .And.Contain("if (!selectedScope)")
            .And.Contain("AddLayoutNonOverlapChecks");
        contracts.Should().Contain("public sealed class SectionVerifyResult")
            .And.Contain("public string Scope { get; set; } = \"batch\"")
            .And.Contain("public string? SelectedRecordId { get; set; }");
    }

    [Fact]
    public void SelectedSectionGate_IsRecordScopedWhileGlobalIntegrityRemainsFailClosed()
    {
        var integrity = Read("CivilDelivery", "Sections", "Services",
            "SectionInputIntegrityService.cs");
        var apply = Read("CivilDelivery", "Sections", "Services",
            "SectionApplyService.cs");
        var verify = Read("CivilDelivery", "Sections", "Services",
            "SectionVerifyService.cs");
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var plan = Read("CivilDelivery", "Sections", "Services",
            "SectionPlanService.cs");

        integrity.Should().Contain("HasSelectedPlanningIntegrityBlocker(")
            .And.Contain("finding.AffectedRecordIds.Count == 0")
            .And.Contain("selected.Findings.Any(IsUnresolvedBlocker)")
            .And.Contain("HasStructuralPlanningIntegrityBlocker(plan)");
        apply.Should().Contain("HasSelectedApplyPlanningIntegrityBlocker(")
            .And.Contain("selectedPlanningBlockers")
            .And.Contain("HasPlanningIntegrityBlocker(plan)");
        integrity.Should().Contain("HasSelectedPlanningIntegrityBlocker(plan, selected) ||")
            .And.Contain("SelectedAnnotationRecoveryScopeFinding(plan, selected) != null");
        verify.Should().Contain("HasSelectedPlanningIntegrityBlocker(")
            .And.Contain("HasPlanningIntegrityBlocker(plan)")
            .And.NotContain("HasSelectedApplyPlanningIntegrityBlocker(",
                "APPLY recovery-scope routing must not replace VERIFY's strict inventory/source proof");
        var gateStart = ui.IndexOf("var selectedApplyReady =", StringComparison.Ordinal);
        gateStart.Should().BeGreaterThanOrEqualTo(0);
        var gateEnd = ui.IndexOf("BtnResolveSection.IsEnabled =", gateStart, StringComparison.Ordinal);
        gateEnd.Should().BeGreaterThan(gateStart);
        var selectedApplyGate = ui.Substring(gateStart, gateEnd - gateStart);
        selectedApplyGate.Should().Contain("HasSelectedApplyPlanningIntegrityBlocker(")
            .And.Contain("_plan, selectedApply)")
            .And.Contain("!planStale")
            .And.Contain("selectedApply.Status == DeliveryStatus.Ready")
            .And.Contain("selectedApply.PresentationCoverage.Complete")
            .And.Contain("selectedApply.PresentationCoverage.UnresolvedSpans.Count == 0")
            .And.Contain("selectedApply.TrafficDirections.All(direction => direction.IsResolved)")
            .And.Contain("BtnApplySelected.IsEnabled = selectedApplyReady &&");
        ui.Should().NotContain("!_plan.Findings.Any(finding =>");
        plan.Should().Contain("Complete = s.IsComplete && rowAuthorityReady")
            .And.Contain("if (s.IsComplete && rowAuthorityReady)")
            .And.Contain("Severity = FindingSeverity.ReviewRequired");
    }

    [Fact]
    public void SelectedDirectionAndManifests_RemainExplicitlyOneRecordScoped()
    {
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
        var workflow = Read("CivilDelivery", "Sections", "Services",
            "SectionsWorkflowService.cs");
        var manifest = Read("CivilDelivery", "Shared",
            "RuntimeRunManifestService.cs");

        ui.Should().Contain("var records = new[] { selected }")
            .And.Contain("traffic directions selected record:")
            .And.Contain("selectedUnresolvedDirections")
            .And.NotContain("var records = _plan.Records\n                .Where(record => record.Action != PlanAction.Excluded");
        xaml.Should().Contain("אשר כיוון נסיעה רק בחתך המסומן");
        workflow.Should().Contain("selectedScope ? \"apply-selected\" : \"apply\"")
            .And.Contain("selectedScope ? \"verify-selected\" : \"verify\"")
            .And.Contain("[\"plan_total\"] = plan.Records.Count")
            .And.Contain("[\"omitted_plan_records\"]")
            .And.Contain("scope: result.Scope")
            .And.Contain("selectedRecordId: result.SelectedRecordId");
        manifest.Should().Contain("string? scope = null")
            .And.Contain("SelectedRecordId = selectedRecordId");
    }
}
