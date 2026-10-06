using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Source-boundary regressions for logic that can only execute inside AutoCAD.
/// Pure hash/unit decisions are covered by MahodAI.Core.Tests; these checks ensure
/// every host entry point is wired to those gates and cannot drift into a bypass.
/// </summary>
public sealed class SectionSourceIntegrityContractTests
{
    private static string PluginSourceDir =>
        typeof(SectionSourceIntegrityContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string PluginFile(params string[] parts) =>
        File.ReadAllText(parts.Aggregate(PluginSourceDir, Path.Combine))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SectionService(string name) => PluginFile(
        "CivilDelivery", "Sections", "Services", name);

    [Fact]
    public void PlanPreviewAndApply_ShareTheMetresRevisionAndExternalSourceGate()
    {
        var plan = SectionService("SectionPlanService.cs");
        var preview = SectionService("SectionPreviewService.cs");
        var apply = SectionService("SectionApplyService.cs");

        plan.Should().Contain("CapturePlanDatabaseState(db, plan, profile)")
            .And.Contain("ValidatePlanUnits(")
            .And.Contain("db, profile.ProfileId, profile);")
            .And.Contain("plan.ExternalSources")
            .And.Contain("BlockDuplicateSourceRecordIds")
            .And.Contain("BlockDuplicateLogicalIdentities");
        preview.Should().Contain("HasPlanningIntegrityBlocker(plan)")
            .And.Contain("plan.SourceDatabaseRevision")
            .And.Contain("db, plan, plan.SourceDatabaseRevision, \"PREVIEW\", profile);")
            .And.Contain("ClearPreview()",
                "the implementation has a transient cleanup boundary");
        preview.IndexOf("plan.SourceDatabaseRevision", StringComparison.Ordinal)
            .Should().BeLessThan(preview.IndexOf("ClearPreview();", StringComparison.Ordinal),
                "a stale preview is rejected before changing the visible transient set");
        apply.Should().Contain("ValidateCurrent(")
            .And.Contain("plan.SourceDatabaseRevision")
            .And.Contain("doc.Database, plan, plan.SourceDatabaseRevision, \"APPLY\", profile);")
            .And.Contain("ValidateExternalSourcesCurrent(plan, \"APPLY\")",
                "source files must be rehashed after expensive Civil work and before commit");
    }

    [Fact]
    public void Verify_UsesACommittedPostApplyRevision_NotThePrePlanRevision()
    {
        var verify = SectionService("SectionVerifyService.cs");
        var apply = SectionService("SectionApplyService.cs");

        verify.Should().Contain("applied.PostApplyDatabaseRevision")
            .And.Contain("!applied.Committed");
        var integrityCall = verify.IndexOf("SectionInputIntegrityService.ValidateCurrent(",
            StringComparison.Ordinal);
        var firstRecordRead = verify.IndexOf("foreach (var applyRecord", StringComparison.Ordinal);
        integrityCall.Should().BeGreaterThan(-1).And.BeLessThan(firstRecordRead,
            "VERIFY must refuse stale evidence before reading Civil section objects");
        apply.Should().Contain("tr.Commit();")
            .And.Contain("using (doc.LockDocument())")
            .And.Contain("using (var tr = db.TransactionManager.StartTransaction())")
            .And.Contain("No database fingerprint/evidence may run while a committed Civil")
            .And.Contain("result.PostApplyDatabaseRevision =\n                DrawingRevisionTracker.Capture(db);");

        foreach (var method in new[] { "public SectionApplyResult Apply(", "public SectionApplyResult ApplySelected(" })
        {
            var start = apply.IndexOf(method, StringComparison.Ordinal);
            start.Should().BeGreaterThanOrEqualTo(0);
            var next = apply.IndexOf("public SectionApplyResult", start + method.Length,
                StringComparison.Ordinal);
            var body = apply.Substring(start, (next < 0 ? apply.Length : next) - start);
            var transaction = body.IndexOf(
                "using (var tr = db.TransactionManager.StartTransaction())", StringComparison.Ordinal);
            var postState = body.IndexOf("DrawingRevisionTracker.Capture(db)", StringComparison.Ordinal);
            transaction.Should().BeGreaterThanOrEqualTo(0);
            postState.Should().BeGreaterThan(transaction);
            body.Substring(transaction, postState - transaction)
                .Should().Contain("tr.Commit();")
                .And.Contain("\n                }\n            }",
                    "revision evidence must be captured after transaction and document-lock Dispose");
        }
    }

    [Fact]
    public void AiApply_ClaimsCommittedOnlyFromTheExecutorsPostCommitCallback()
    {
        var tool = PluginFile("Tools", "CivilDelivery", "SectionsTools.cs");
        var executor = PluginFile("Tools", "ToolExecutor.cs");
        var contract = PluginFile("Tools", "IDrawingTool.cs");

        tool.Should().Contain("ApplySectionsTool : DrawingToolBase, ITransactionCommitObserver")
            .And.Contain("result.Committed = false")
            .And.Contain("void OnTransactionCommitted(")
            .And.Contain("result.Committed = true")
            .And.Contain("PostApplyDatabaseRevision");
        contract.Should().Contain("interface ITransactionCommitObserver");
        executor.Should().Contain("tr.Commit();")
            .And.Contain("if (committed)\n                                NotifyTransactionCommitted(tool, db, result);")
            .And.Contain("foreach (var observer in commitObservers)")
            .And.Contain("NotifyTransactionCommitted(observer.Tool, db, observer.Result);");
    }

    [Fact]
    public void ReadOnlyPlanAndVerify_PublishOnlyAfterExecutorAbortAndDispose()
    {
        var contract = PluginFile("Tools", "IDrawingTool.cs");
        var executor = PluginFile("Tools", "ToolExecutor.cs");
        var tools = PluginFile("Tools", "CivilDelivery", "SectionsTools.cs");

        contract.Should().Contain("interface IReadOnlyTransactionClosedObserver")
            .And.Contain("void OnReadOnlyTransactionClosed(Database database, ToolResult result);");

        var transactionScope = executor.IndexOf(
            "using (var tr = db.TransactionManager.StartTransaction())", StringComparison.Ordinal);
        var disposedBoundary = executor.IndexOf(
            "if (cancelled)", transactionScope, StringComparison.Ordinal);
        var callback = executor.IndexOf(
            "NotifyReadOnlyTransactionClosed(tool, db, result);", transactionScope,
            StringComparison.Ordinal);
        transactionScope.Should().BeGreaterThan(-1);
        disposedBoundary.Should().BeGreaterThan(transactionScope);
        callback.Should().BeGreaterThan(disposedBoundary,
            "publication may begin only after the transaction using scope closed successfully");
        executor.Substring(transactionScope, disposedBoundary - transactionScope)
            .Should().NotContain("NotifyReadOnlyTransactionClosed(");
        executor.Should().Contain("result.IsReadOnly &&")
            .And.Contain("result.Success &&")
            .And.Contain("result.Outcome == ToolOutcome.Succeeded")
            .And.Contain("Post-close read-only evidence callback failed")
            .And.Contain("result.Outcome = ToolOutcome.Failed");

        foreach (var className in new[] { "PlanSectionsTool", "VerifySectionsTool" })
            tools.Should().Contain(
                $"class {className} : DrawingToolBase, IReadOnlyTransactionClosedObserver");

        var planStart = tools.IndexOf("class PlanSectionsTool", StringComparison.Ordinal);
        var planCallback = tools.IndexOf("public void OnReadOnlyTransactionClosed(",
            planStart, StringComparison.Ordinal);
        var planExecute = tools.Substring(planStart, planCallback - planStart);
        planExecute.Should().NotContain("SectionsWorkflowService.WriteArtifact(")
            .And.NotContain("SectionsWorkflowService.WritePlanManifest(")
            .And.NotContain("CivilDeliverySession.SetPlan(");
        var planEnd = tools.IndexOf("public class ApproveSectionTrafficDirectionTool",
            planCallback, StringComparison.Ordinal);
        var planPublication = tools.Substring(planCallback, planEnd - planCallback);
        planPublication.Should().Contain("CapturePlanDatabaseState(database, plan, profile)")
            .And.Contain("SectionsWorkflowService.WriteArtifact(")
            .And.Contain("SectionsWorkflowService.WritePlanManifest(")
            .And.Contain("CivilDeliverySession.SetPlan(")
            .And.Contain("finally")
            .And.Contain("ClearPending();");

        var verifyStart = tools.IndexOf("class VerifySectionsTool", StringComparison.Ordinal);
        var verifyCallback = tools.IndexOf("public void OnReadOnlyTransactionClosed(",
            verifyStart, StringComparison.Ordinal);
        var verifyExecute = tools.Substring(verifyStart, verifyCallback - verifyStart);
        verifyExecute.Should().NotContain("PersistVerifyEvidence(");
        var verifyEnd = tools.IndexOf("internal static class SectionToolProjections",
            verifyCallback, StringComparison.Ordinal);
        var verifyPublication = tools.Substring(verifyCallback, verifyEnd - verifyCallback);
        verifyPublication.Should().Contain("PersistVerifyEvidence(")
            .And.Contain("toolResult.Success = false")
            .And.Contain("toolResult.Outcome = ToolOutcome.Failed")
            .And.Contain("finally")
            .And.Contain("ClearPending();");
    }

    [Fact]
    public void DirectPlan_PublishesOnlyAfterAbortAndTransactionDispose()
    {
        var workflow = SectionService("SectionsWorkflowService.cs");
        var start = workflow.IndexOf("public SectionPlan Plan(", StringComparison.Ordinal);
        var end = workflow.IndexOf("public SectionPreviewDisplay Preview(", start,
            StringComparison.Ordinal);
        var plan = workflow.Substring(start, end - start);

        plan.Should().Contain("using (var tr = db.TransactionManager.StartTransaction())")
            .And.Contain("tr.Abort(); // PLAN is a read-only contract")
            .And.Contain("tr.Abort(); // PLAN is a read-only contract; never persist lazy host changes\n            }\n            SectionInputIntegrityService.CapturePlanDatabaseState(db, plan, profile);");
        var disposeBoundary = plan.IndexOf(
            "SectionInputIntegrityService.CapturePlanDatabaseState(db, plan, profile);",
            StringComparison.Ordinal);
        var publication = plan.IndexOf("WriteArtifact(plan.RunId", StringComparison.Ordinal);
        publication.Should().BeGreaterThan(disposeBoundary,
            "the post-abort revision and artifact may be authoritative only after Dispose succeeds");
    }

    [Fact]
    public void AtomicBatch_PublishesCommitEvidenceOnlyAfterTransactionDispose()
    {
        var executor = PluginFile("Tools", "ToolExecutor.cs");
        var start = executor.IndexOf("private async Task<List<FixItemResult>> ExecuteBatchAtomicAsync(",
            StringComparison.Ordinal);
        var end = executor.IndexOf("private static void NotifyTransactionCommitted(", start,
            StringComparison.Ordinal);
        var batch = executor.Substring(start, end - start);

        var transactionScope = batch.IndexOf(
            "using (var tr = db.TransactionManager.StartTransaction())", StringComparison.Ordinal);
        var disposeBoundary = batch.IndexOf("if (transactionFailure != null)",
            transactionScope, StringComparison.Ordinal);
        var callback = batch.IndexOf(
            "NotifyTransactionCommitted(observer.Tool, db, observer.Result);",
            transactionScope, StringComparison.Ordinal);
        transactionScope.Should().BeGreaterThan(-1);
        disposeBoundary.Should().BeGreaterThan(transactionScope);
        callback.Should().BeGreaterThan(disposeBoundary,
            "a committed batch must not publish evidence until transaction Dispose succeeds");
        batch.Substring(transactionScope, disposeBoundary - transactionScope)
            .Should().NotContain("NotifyTransactionCommitted(")
            .And.NotContain("return completedResults");
        batch.Should().Contain("Atomic batch lock/transaction close failed")
            .And.Contain("Failed to lock or close drawing transaction");
    }

    [Fact]
    public void AtomicBatch_RejectsDuplicateObserverSingletonsBeforeOpeningTransaction()
    {
        var executor = PluginFile("Tools", "ToolExecutor.cs");
        var start = executor.IndexOf("private async Task<List<FixItemResult>> ExecuteBatchAtomicAsync(",
            StringComparison.Ordinal);
        var end = executor.IndexOf("private static void NotifyTransactionCommitted(", start,
            StringComparison.Ordinal);
        var batch = executor.Substring(start, end - start);

        var duplicateGuard = batch.IndexOf(
            "duplicate post-commit observer tool", StringComparison.Ordinal);
        var transactionStart = batch.IndexOf(
            "db.TransactionManager.StartTransaction()", StringComparison.Ordinal);
        duplicateGuard.Should().BeGreaterThan(-1).And.BeLessThan(transactionStart,
            "the duplicate singleton must be refused before its first invocation can mutate pending state");
        batch.Should().Contain("new HashSet<string>(StringComparer.Ordinal)")
            .And.Contain("ReferenceEqualityComparer.Instance")
            .And.Contain("Atomic batch rejected before transaction");
    }

    [Fact]
    public void TrafficDirectionApproval_DefersEveryDecisionSideEffectAndReplanUntilPostClose()
    {
        var tools = PluginFile("Tools", "CivilDelivery", "SectionsTools.cs");
        var start = tools.IndexOf("class ApproveSectionTrafficDirectionTool",
            StringComparison.Ordinal);
        var callback = tools.IndexOf("public void OnReadOnlyTransactionClosed(", start,
            StringComparison.Ordinal);
        var end = tools.IndexOf("public class PreviewSectionsTool", callback,
            StringComparison.Ordinal);
        var execute = tools.Substring(start, callback - start);
        var publication = tools.Substring(callback, end - callback);

        execute.Should().Contain("_pendingDecision = new PendingDecision(")
            .And.NotContain("SectionDecisionProfileService.ApproveTrafficDirection(")
            .And.NotContain("ProjectProfileWriter.Save(")
            .And.NotContain("new SectionPlanService().Plan(")
            .And.NotContain("SectionsWorkflowService.WriteArtifact(")
            .And.NotContain("CivilDeliverySession.SetPlan(");
        publication.Should().Contain("SectionToolProfileFreshness.RequireCurrent(")
            .And.Contain("SectionInputIntegrityService.StaleReason(")
            .And.Contain("SectionDecisionProfileService.ApproveTrafficDirection(")
            .And.Contain("currentProfile.ProfileWriteState ??")
            .And.NotContain("ProjectProfileWriter.CaptureExpectedState(")
            .And.Contain("ProjectProfileWriter.Save(")
            .And.Contain("expectedState: expectedProfileState")
            .And.Contain("using (var planTr = database.TransactionManager.StartTransaction())")
            .And.Contain("planTr.Abort();")
            .And.Contain("SectionsWorkflowService.WriteArtifact(")
            .And.Contain("CivilDeliverySession.SetPlan(")
            .And.Contain("RestoreProfileAfterFailedDecisionEvidence(saved)")
            .And.Contain("RestoreProfileObject(")
            .And.Contain("finally")
            .And.Contain("CivilDeliverySession.ClearSectionsContext()");

        var planTransaction = publication.IndexOf("using (var planTr", StringComparison.Ordinal);
        var planPublication = publication.IndexOf(
            "SectionInputIntegrityService.CapturePlanDatabaseState(database, replanned, profileForSave);",
            StringComparison.Ordinal);
        planPublication.Should().BeGreaterThan(planTransaction);
        publication.Substring(planTransaction, planPublication - planTransaction)
            .Should().NotContain("WriteArtifact(")
            .And.NotContain("SetPlan(");
    }

    [Fact]
    public void DirectCommand_AllSignedExclusionsReachApplyEvidence_WhileUnchangedOnlyStaysBlocked()
    {
        var command = PluginFile("CivilDelivery", "Commands", "MhdSectionsCommand.cs");
        var start = command.IndexOf("private void RunApply(", StringComparison.Ordinal);
        var end = command.IndexOf("private void RunVerify(", start, StringComparison.Ordinal);
        var apply = command.Substring(start, end - start);

        apply.Should().Contain("allSignedExclusions")
            .And.Contain("_lastPlan.Records.All(SectionPlanLogic.HasValidExplicitExclusion)")
            .And.Contain("if (creates == 0 && !allSignedExclusions)")
            .And.Contain("_lastApply = Workflow.Apply(");
        apply.IndexOf("allSignedExclusions", StringComparison.Ordinal)
            .Should().BeLessThan(apply.IndexOf("_lastApply = Workflow.Apply(",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_RequiresExactPlanApplyRows_IncludingEverySignedExclusion()
    {
        var verify = SectionService("SectionVerifyService.cs");
        var comparison = verify.IndexOf("SectionRecordEvidenceLogic.Compare(",
            StringComparison.Ordinal);
        var firstCivilSnapshot = verify.IndexOf("SnapshotSampledSourceNames(",
            StringComparison.Ordinal);

        comparison.Should().BeGreaterThan(-1).And.BeLessThan(firstCivilSnapshot,
            "missing/extra/duplicate APPLY rows must block before Civil object traversal");
        verify.Should().Contain("recordEvidence.Missing")
            .And.Contain("recordEvidence.Extra")
            .And.Contain("recordEvidence.DuplicatePlanIds")
            .And.Contain("recordEvidence.DuplicateApplyIds")
            .And.Contain("recordEvidence.LogicalKeyMismatches")
            .And.Contain("result.Status = DeliveryStatus.Blocked");
    }

    [Fact]
    public void AiVerify_RequiresPublishedPlanAndApply_AndApplySessionAdvancesOnlyAfterEvidence()
    {
        var tools = PluginFile("Tools", "CivilDelivery", "SectionsTools.cs");
        var verifyStart = tools.IndexOf("class VerifySectionsTool", StringComparison.Ordinal);
        var verifyEnd = tools.IndexOf("internal static class SectionToolProjections",
            verifyStart, StringComparison.Ordinal);
        var verify = tools.Substring(verifyStart, verifyEnd - verifyStart);
        var evidence = verify.IndexOf("SectionsWorkflowService.RequirePlanEvidence(plan);",
            StringComparison.Ordinal);
        var civilRead = verify.IndexOf("verifyService.Verify(", StringComparison.Ordinal);
        evidence.Should().BeGreaterThan(-1).And.BeLessThan(civilRead);
        verify.Should().Contain("SectionsWorkflowService.RequireApplyEvidence(apply);")
            .And.Contain("CivilDeliverySession.ClearSectionsApply();")
            .And.Contain("VERIFY refused before Civil read-back");

        var applyStart = tools.IndexOf("class ApplySectionsTool", StringComparison.Ordinal);
        var applyEnd = tools.IndexOf("class VerifySectionsTool", applyStart,
            StringComparison.Ordinal);
        var apply = tools.Substring(applyStart, applyEnd - applyStart);
        var persist = apply.IndexOf("PersistApplyEvidence(", StringComparison.Ordinal);
        var set = apply.IndexOf("CivilDeliverySession.SetApply(result);", persist,
            StringComparison.Ordinal);
        persist.Should().BeGreaterThan(-1);
        set.Should().BeGreaterThan(persist);
        apply.Substring(persist, set - persist)
            .Should().Contain("if (evidenceWritten)");
        apply.Should().Contain("else\n                CivilDeliverySession.ClearSectionsApply();")
            .And.NotContain("CivilDeliverySession.SetApply(result);\n                return Task.FromResult(ToolResult.Fail");

        var planStart = tools.IndexOf("class PlanSectionsTool", StringComparison.Ordinal);
        var planEnd = tools.IndexOf("class ApproveSectionTrafficDirectionTool", planStart,
            StringComparison.Ordinal);
        var plan = tools.Substring(planStart, planEnd - planStart);
        plan.Should().Contain("CivilDeliverySession.ClearSectionsContext();")
            .And.Contain("CivilDeliverySession.SetPlan(");
        plan.IndexOf("ClearSectionsContext", StringComparison.Ordinal)
            .Should().BeLessThan(plan.IndexOf("new SectionPlanService", StringComparison.Ordinal));
    }

    [Fact]
    public void ClAndProjectionTraversal_RetainResolvedPathsHashesChainsAndComposedTransforms()
    {
        var cl = SectionService("ClInstructionReader.cs");
        var projection = SectionService("SectionGeometryCollector.cs");

        foreach (var source in new[] { cl, projection })
        {
            source.Should().Contain("MaxBlockNestingDepth")
                .And.Contain("definitionStack")
                .And.Contain("SectionFindingCodes.XrefCycle")
                .And.Contain("outerTransform *")
                .And.Contain(".BlockTransform")
                .And.Contain("SectionXrefSnapshotGuard.Cache")
                .And.Contain("xrefSnapshots.Validate(definition, parentSource.Path)")
                .And.Contain("snapshot.IsFresh")
                .And.Contain("snapshot.ResolvedPath")
                .And.Contain("snapshot.Sha256");
        }
        cl.Should().Contain("seenExternalCandidates")
            .And.Contain("SourceDrawingPath = candidate.Source.Path")
            .And.Contain("SourceDrawingHash = candidate.Source.Hash")
            .And.Contain("RequiresLiveDatabase = live")
            .And.Contain("HashFileShared(resolved)");
        projection.Should().Contain("SourceDrawingPath")
            .And.Contain("SourceDrawingHash")
            .And.Contain("AddEvidenceForMatchedExternal");
    }

    [Fact]
    public void ClosedClDraftingGeometry_IsAggregatedButOpenBentGeometryFailsClosed()
    {
        var cl = SectionService("ClInstructionReader.cs");

        SectionService("SectionClGeometryContract.cs").Should().Contain("SectionClCandidateLogic.Analyze")
            .And.Contain("IsClosedPolyline(entity)");
        cl.Should().Contain("SectionClGeometryContract.Analyze(geometry)")
            .And.Contain("IgnoreClosedNonInstruction")
            .And.Contain("shape.Kind == SectionClCandidateLogic.Decision.Degenerate")
            .And.Contain("yield break;")
            .And.Contain("SectionFindingCodes.ClNonInstructionIgnored")
            .And.Contain("finding.SourceRefs.Add")
            .And.Contain("closed_polyline_has_no_two_open_cut_ends")
            .And.Contain("StraightSagittaToleranceM");
    }

    [Fact]
    public void DurableExclusion_RequiresTheExactFindingToBeReproducedFirst()
    {
        var logic = SectionService("SectionPlanLogic.cs");
        var plan = SectionService("SectionPlanService.cs");

        logic.Should().Contain("exclusion.FindingCode")
            .And.Contain("IsExcludableFinding(f)")
            .And.Contain("finding.Severity is FindingSeverity.ReviewRequired or FindingSeverity.Error")
            .And.Contain("SectionFindingCodes.ExclusionStale")
            .And.Contain("FindingCode = exclusion.FindingCode!");
        plan.Should().NotContain("// Existing persisted exclusions are resolved before",
            "an exclusion must not skip alignment/source/style/presentation evaluation");
    }
}
