using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Host wiring contracts: executing Document events/queued commands needs Civil,
/// so these tests pin the production entry points and ordering without loading a
/// drawing. Pure continuation decisions and guide behavior have separate tests.
/// These contracts do not claim to simulate a live QSAVE/Save As cancellation.
/// </summary>
public sealed class WorkflowSaveGuidanceSourceContractTests
{
    private static string PluginSourceDir =>
        typeof(WorkflowSaveGuidanceSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string Read(params string[] parts) =>
        File.ReadAllText(parts.Aggregate(PluginSourceDir, Path.Combine))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Ui(string name) => Read("CivilDelivery", "UI", name);

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the production method {0} must exist", signature);
        var rest = source[(start + signature.Length)..];
        var next = Regex.Match(rest, @"(?m)^[ \t]+(?:private|internal|public)\s");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    private static int At(string source, string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, "the production gate must contain {0}", token);
        return index;
    }

    [Fact]
    public void QueuedSave_TransportsItsUniqueTokenThroughTheDeclaredCommandAndDispatcher()
    {
        var save = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"), "private bool EnsureSavedForAction(");
        save.Should().Contain("Guid.NewGuid().ToString(\"N\")")
            .And.Contain("doc.CommandEnded += OnWorkflowSaveEnded;")
            .And.Contain("doc.SendStringToExecute(\"_.QSAVE\\n\", true, false, false);")
            .And.NotContain("MHD_DELIVERY_AFTER_SAVE\\n");
        At(save, "_pendingWorkflowSaveToken =").Should().BeLessThan(At(save, "doc.SendStringToExecute("));

        var ended = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"), "private void OnWorkflowSaveEnded(");
        ended.Should().Contain("e.GlobalCommandName, \"QSAVE\"")
            .And.Contain("WorkflowSavePhase.AwaitingSaveEnd")
            .And.Contain("WorkflowSavePhase.SaveEndObserved")
            .And.Contain("!ReferenceEquals(Doc(), document)")
            .And.Contain("document.Database.UnmanagedObject != expectedDatabase")
            .And.Contain("!string.IsNullOrWhiteSpace(document.CommandInProgress)")
            .And.Contain("document.SendStringToExecute(CivilDeliveryCommandNames.AfterSave + \"\\n\" + token + \"\\n\",");
        At(ended, "Dispatcher.BeginInvoke(").Should().BeLessThan(At(ended, "document.SendStringToExecute("));
        At(ended, "!SavedDrawingContinuationPolicy.CanConsume(")
            .Should().BeLessThan(At(ended, "document.SendStringToExecute("));
        At(ended, "_workflowSavePhase = WorkflowSavePhase.ContinuationQueued;")
            .Should().BeLessThan(At(ended, "document.SendStringToExecute("));

        var command = Read("CivilDelivery", "Commands", "MhdCivilDeliveryCommand.cs");
        command.Should().Contain("[assembly: CommandClass(typeof(")
            .And.Contain("CommandMethod(CivilDeliveryCommandNames.AfterSave")
            .And.Contain("CommandFlags.Modal | CommandFlags.NoHistory");
        var callback = Method(command, "public void ResumeAfterSave(");
        At(callback, "PromptStatus.OK").Should().BeLessThan(
            At(callback, "ResumeWorkflowAfterExplicitSave(token.StringResult)"));

        var palette = Method(Ui("CivilDeliveryPalette.cs"),
            "internal static void ResumeWorkflowAfterExplicitSave(string token)");
        palette.Should().Contain("Dispatcher.BeginInvoke(")
            .And.Contain("control.ResumeWorkflowAfterExplicitSave(token)");
    }

    [Fact]
    public void InitialSaveRequest_GuardsDisposedSourceAndRechecksDrawingAfterConfirmation()
    {
        var save = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"), "private bool EnsureSavedForAction(");
        At(save, "try\n").Should().BeLessThan(At(save, "doc.Database.UnmanagedObject"));
        At(save, "try\n").Should().BeLessThan(At(save, "DrawingRevisionTracker.CaptureLive(doc)"));
        At(save, "!ReferenceEquals(Doc(), doc)").Should().BeLessThan(At(save, "doc.SendStringToExecute("));
        save.Should().Contain("doc.Database.UnmanagedObject != expectedDatabase")
            .And.Contain("catch (Exception ex)");
        var cancelled = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"), "private void OnWorkflowSaveCancelled(");
        cancelled.Should().Contain("CancelWorkflowSave(sender as Document,");
    }

    [Fact]
    public void ForeignOrLateToken_ReturnsBeforeConsumingPendingStateOrInvokingAContinuation()
    {
        var resume = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"),
            "internal void ResumeWorkflowAfterExplicitSave(string token)");
        Regex.IsMatch(resume,
            @"if\s*\(!SavedDrawingContinuationPolicy\.CanConsume\([\s\S]*?\)\)\s*return;")
            .Should().BeTrue("a foreign queued token must not consume a newer save request");
        resume.Should().Contain("_pendingWorkflowSaveToken, token,")
            .And.Contain("_pendingWorkflowSaveDocument != null && _pendingWorkflowSaveContinuation != null");
        var tokenGate = At(resume, "!SavedDrawingContinuationPolicy.CanConsume(");
        tokenGate.Should().BeLessThan(At(resume, "var continuation = _pendingWorkflowSaveContinuation"));
        tokenGate.Should().BeLessThan(At(resume, "CancelWorkflowSave(expected,"));
        tokenGate.Should().BeLessThan(At(resume, "continuation();"));
    }

    [Fact]
    public void MatchingCallback_ConsumesOnceAndRevalidatesExactDocumentDatabaseAndSavedBytes()
    {
        var resume = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"),
            "internal void ResumeWorkflowAfterExplicitSave(string token)");
        var consume = At(resume, "CancelWorkflowSave(expected,");
        var continuation = At(resume, "continuation();");
        consume.Should().BeLessThan(continuation);
        At(resume, "ReferenceEquals(doc, expected)").Should().BeLessThan(continuation);
        At(resume, "Database.UnmanagedObject == expectedDatabase").Should().BeLessThan(continuation);
        At(resume, "DrawingRevisionTracker.CaptureLive(doc!)").Should().BeLessThan(continuation);
        At(resume, "readiness.IsReady").Should().BeLessThan(continuation);
        At(resume, "try\n").Should().BeLessThan(At(resume, "DrawingRevisionTracker.CaptureLive(doc!)"));
        resume.Should().Contain("finally { _resumingWorkflowSave = false; RefreshGates(); }");
        Regex.Matches(resume, @"\bcontinuation\(\);").Count.Should().Be(1);
    }

    [Fact]
    public void RedirtiedSave_IsRetriedOnceThroughTheSameTokenDisciplineAndNeverLooped()
    {
        var resume = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"),
            "internal void ResumeWorkflowAfterExplicitSave(string token)");
        resume.Should().Contain("SavedDrawingContinuationDecision.SaveAgain")
            .And.Contain("readiness.CanSaveAndResume, attempts")
            .And.Contain("_workflowSaveAttempts = attempts + 1;")
            .And.Contain("_pendingWorkflowSaveToken = Guid.NewGuid().ToString(\"N\");")
            .And.Contain("doc!.CommandEnded += OnWorkflowSaveEnded;")
            .And.Contain("doc.CommandCancelled += OnWorkflowSaveCancelled;")
            .And.Contain("doc.CommandFailed += OnWorkflowSaveCancelled;")
            .And.Contain("_workflowSavePhase = WorkflowSavePhase.AwaitingSaveEnd;")
            .And.Contain("doc.SendStringToExecute(\"_.QSAVE\\n\", true, false, false);")
            .And.NotContain("MHD_DELIVERY_AFTER_SAVE\\n");
        At(resume, "var attempts = _workflowSaveAttempts;").Should().BeLessThan(At(resume, "CancelWorkflowSave(expected,"));
        At(resume, "SavedDrawingContinuationDecision.SaveAgain").Should().BeLessThan(At(resume, "continuation();"));
        var save = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"), "private bool EnsureSavedForAction(");
        save.Should().Contain("_workflowSaveAttempts = 1;");
        var cancel = Method(Ui("CivilDeliveryControl.SaveGuidance.cs"), "private void CancelWorkflowSave(");
        cancel.Should().Contain("_pendingWorkflowSaveOperation = null;");
    }

    [Fact]
    public void SaveCancellationAndDocumentTransitions_ClearOnlyTheOwnedPendingRequest()
    {
        var save = Ui("CivilDeliveryControl.SaveGuidance.cs");
        save.Should().Contain("doc.CommandCancelled += OnWorkflowSaveCancelled;")
            .And.Contain("doc.CommandFailed += OnWorkflowSaveCancelled;");
        var cancel = Method(save, "private void CancelWorkflowSave(");
        var identity = At(cancel, "!ReferenceEquals(document, _pendingWorkflowSaveDocument)");
        identity.Should().BeLessThan(At(cancel, "_pendingWorkflowSaveDocument = null;"));
        cancel.Should().Contain("document.CommandCancelled -= OnWorkflowSaveCancelled;")
            .And.Contain("document.CommandFailed -= OnWorkflowSaveCancelled;")
            .And.Contain("_pendingWorkflowSaveContinuation = null;")
            .And.Contain("_pendingWorkflowSaveToken = null;")
            .And.Contain("_pendingWorkflowSaveDatabase = IntPtr.Zero;");

        var main = Ui("CivilDeliveryControl.xaml.cs");
        var hooks = Method(main, "private void HookDocumentEvents(");
        hooks.Should().Contain("DocumentToBeDestroyed +=")
            .And.Contain("OnObservedDocumentDestroying");
        Ui("CivilDeliveryControl.DrawingSaveContext.cs").Should()
            .Contain("CancelWorkflowSave(e.Document,");
        var changed = Method(main, "private void OnDrawingChanged(");
        changed.Should().Contain("!ReferenceEquals(_pendingWorkflowSaveDocument, activeDocument)")
            .And.Contain("CancelWorkflowSave(_pendingWorkflowSaveDocument,");
    }

    [Theory]
    [InlineData("OnResolveTrafficDirection")]
    [InlineData("OnNameSectionSpans")]
    [InlineData("OnApproveSectionRow")]
    [InlineData("ReviewSectionDecision")]
    public void SectionDecisionHandlers_SaveBeforeOpeningAndNeverSaveAcceptedStaleChoices(string handler)
    {
        var body = Method(Ui("CivilDeliveryControl.xaml.cs"), "private void " + handler + "(");
        var saveGate = At(body, "EnsureSavedForAction(");
        var dialog = At(body, "CivilModalHost.ShowFromPalette(dialog)");
        saveGate.Should().BeLessThan(dialog);
        body.Should().Contain("replan: true");
        var afterDialog = body[dialog..];
        afterDialog.Should().NotContain("EnsureSavedForAction(")
            .And.NotContain("SendStringToExecute(")
            .And.NotContain("QSAVE");
        At(body, "CaptureSectionDecisionScope(").Should().BeLessThan(dialog);
        At(afterDialog, "RequireSectionDecisionScope(")
            .Should().BeLessThan(At(afterDialog, "ProjectProfileWriter.Save("));
        afterDialog.Should().Contain(".ExpectedState");
    }

    [Theory]
    [InlineData("OnPickCl")]
    [InlineData("OnApproveEstimateScope")]
    [InlineData("OnEarthworksDecision")]
    public void OtherSourceDecisionDialogs_AlsoRequestDrawingSaveOnlyBeforeUserChoices(string handler)
    {
        var body = Method(Ui("CivilDeliveryControl.xaml.cs"), "private void " + handler + "(");
        var saveGate = At(body, "EnsureSavedForAction(");
        var firstDialog = At(body, handler == "OnPickCl"
            ? "dialog.ShowDialog()" : "CivilModalHost.ShowFromPalette(dialog)");
        saveGate.Should().BeLessThan(firstDialog);
        body[firstDialog..].Should().NotContain("EnsureSavedForAction(")
            .And.NotContain("SendStringToExecute(")
            .And.NotContain("QSAVE");
    }

    [Fact]
    public void FinalBatchExclusionConfirmation_IsFollowedByAnotherScopeCheckBeforeProfileWrite()
    {
        var body = Method(Ui("CivilDeliveryControl.xaml.cs"), "private void ReviewSectionDecision(");
        var confirmation = body.LastIndexOf("MessageBox.Show(", StringComparison.Ordinal);
        confirmation.Should().BeGreaterThan(At(body, "CivilModalHost.ShowFromPalette(dialog)"));
        var finalScopeCheck = body.LastIndexOf("RequireSectionDecisionScope(", StringComparison.Ordinal);
        finalScopeCheck.Should().BeGreaterThan(confirmation,
            "the final modal confirmation is another opportunity for the source or selection to change");
        finalScopeCheck.Should().BeLessThan(At(body, "ProjectProfileWriter.Save("));
    }

    [Fact]
    public void DialogScopeChecksExactObjectsAndMembershipBeforeSourceAndProfileValidation()
    {
        var scope = Ui("CivilDeliveryControl.SectionDecisionScope.cs");
        var identity = Method(scope, "private void RequireSectionDecisionIdentity(");
        identity.Should().Contain("ReferenceEquals(Doc(), scope.Document)")
            .And.Contain("ReferenceEquals(_plan, scope.Plan)")
            .And.Contain("record.RecordId, scope.RecordId, StringComparison.Ordinal")
            .And.Contain("matchingRecords.Count != 1")
            .And.Contain("ReferenceEquals(matchingRecords[0], scope.Record)")
            .And.Contain("ReferenceEquals(selected.Record, scope.Record)");
        var require = Method(scope, "private SectionDecisionWriteContext RequireSectionDecisionScope(");
        At(require, "RequireSectionDecisionIdentity(scope)")
            .Should().BeLessThan(At(require, "RequireFreshSectionPlan(scope.Stage)"));
        At(require, "RequireFreshSectionPlan(scope.Stage)")
            .Should().BeLessThan(At(require, "CaptureExpectedProfileState(currentProfile)"));
        scope.Should().NotContain("EnsureSavedForAction(")
            .And.NotContain("ProjectProfileWriter.Save(")
            .And.NotContain("SendStringToExecute(");
    }
}
