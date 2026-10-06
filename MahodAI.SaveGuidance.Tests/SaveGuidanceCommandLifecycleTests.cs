using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using System.Windows;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class SaveGuidanceCommandLifecycleTests
{
    private static (CivilDeliveryControl Ui, Document Doc) NewRequest()
    {
        MessageBox.Answer = MessageBoxResult.Yes; MessageBox.BeforeAnswer = null;
        DrawingRevisionTracker.Next = new(null, null, 1);
        DrawingRevisionTracker.CaptureCalls = 0;
        var doc = new Document();
        return (new CivilDeliveryControl { ActiveDocument = doc }, doc);
    }
    private static void Saved() => DrawingRevisionTracker.Next = new(@"C:\test\named.dwg", new string('a', 64), 0);
    private static string CallbackToken(Document doc)
    {
        var text = doc.Sent.Last();
        Assert.StartsWith("MHD_DELIVERY_AFTER_SAVE\n", text);
        return text.Split('\n')[1];
    }

    [Fact]
    public void UntitledFilePromptReceivesOnlyQsave_NotTheContinuationOrToken()
    {
        var (ui, doc) = NewRequest();
        Assert.False(ui.Start());
        // SendStringToExecute is a single sequential native input stream.
        // A FILEDIA=0 filename prompt must have no prefilled following tokens.
        Assert.Equal("_.QSAVE\n", Assert.Single(doc.Sent));
        ui.Dispatcher.Flush();
        Assert.Single(doc.Sent);
        Assert.True(ui.IsPending); Assert.Equal(0, ui.Continuations);
    }

    [Fact]
    public void CompletedSaveDispatchesOnceAfterEventReturns_ThenFreshReadbackResumesProfilePicker()
    {
        var (ui, doc) = NewRequest(); ui.ProfileCanLoad = false; ui.Start(); var token = ui.Token;
        Saved(); doc.End(); doc.End();
        Assert.Single(doc.Sent); // event callback never injects input itself
        Assert.Equal(0, ui.Continuations);
        ui.Dispatcher.Flush();
        Assert.Equal(2, doc.Sent.Count); Assert.Equal(token, CallbackToken(doc));
        ui.ResumeWorkflowAfterExplicitSave(token);
        ui.ResumeWorkflowAfterExplicitSave(token);
        Assert.Equal(1, ui.Continuations); Assert.Equal(0, ui.ProfileReloads);
        Assert.False(ui.IsPending); Assert.Equal(0, doc.Subscriptions);
    }

    [Fact]
    public void EvenMatchingEarlyTokenCannotBypassUnfinishedSave()
    {
        var (ui, doc) = NewRequest(); ui.Start(); Saved();
        ui.ResumeWorkflowAfterExplicitSave(ui.Token);
        Assert.True(ui.IsPending); Assert.Equal(0, ui.Continuations); Assert.Single(doc.Sent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CancelOrFailureDuringFilenamePromptClearsPendingAndAllSubscriptions(bool failed)
    {
        var (ui, doc) = NewRequest(); ui.Start(); var token = ui.Token;
        if (failed) doc.Fail(); else doc.Cancel();
        Saved(); doc.End(); ui.Dispatcher.Flush(); ui.ResumeWorkflowAfterExplicitSave(token);
        Assert.False(ui.IsPending); Assert.Single(doc.Sent); Assert.Equal(0, ui.Continuations);
        Assert.Equal(0, doc.Subscriptions);
    }

    [Fact]
    public void CancelAfterEndedButBeforeDispatcherInvalidatesThatQueuedTicket()
    {
        var (ui, doc) = NewRequest(); ui.Start(); doc.End(); doc.Cancel(); ui.Dispatcher.Flush();
        Assert.Single(doc.Sent); Assert.False(ui.IsPending); Assert.Equal(0, ui.Continuations);
    }

    [Fact]
    public void CancelThenNewRequestCannotBeConsumedByOldEndedCallback()
    {
        var (ui, doc) = NewRequest(); ui.Start(); var old = ui.Token; doc.End(); ui.Stop(); ui.Start();
        var current = ui.Token; Assert.NotEqual(old, current);
        ui.Dispatcher.Flush(); Assert.Equal(2, doc.Sent.Count);
        ui.ResumeWorkflowAfterExplicitSave(old); Assert.True(ui.IsPending);
        Saved(); doc.End(); ui.Dispatcher.Flush(); ui.ResumeWorkflowAfterExplicitSave(CallbackToken(doc));
        Assert.Equal(1, ui.Continuations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChangedDocumentOrDatabaseBeforeDispatchCancelsInsteadOfInjectingIntoOtherDrawing(bool databaseOnly)
    {
        var (ui, doc) = NewRequest(); ui.Start(); doc.End();
        if (databaseOnly) doc.Database.UnmanagedObject = new(99); else ui.ActiveDocument = new Document();
        ui.Dispatcher.Flush(); Assert.Single(doc.Sent); Assert.False(ui.IsPending); Assert.Equal(0, ui.Continuations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RedirtiedSuccessfulSaveGetsAtMostOneSeparateQsaveRetry(bool secondReady)
    {
        var (ui, doc) = NewRequest(); ui.Start(); var first = ui.Token;
        DrawingRevisionTracker.Next = new(@"C:\test\named.dwg", null, 1);
        doc.End(); ui.Dispatcher.Flush(); ui.ResumeWorkflowAfterExplicitSave(CallbackToken(doc));
        Assert.Equal(3, doc.Sent.Count); Assert.Equal("_.QSAVE\n", doc.Sent[2]);
        Assert.NotEqual(first, ui.Token); Assert.Equal(0, ui.Continuations);
        ui.ResumeWorkflowAfterExplicitSave(first); Assert.True(ui.IsPending);
        if (secondReady) Saved();
        doc.End(); ui.Dispatcher.Flush(); ui.ResumeWorkflowAfterExplicitSave(CallbackToken(doc));
        Assert.Equal(4, doc.Sent.Count); Assert.False(ui.IsPending);
        Assert.Equal(secondReady ? 1 : 0, ui.Continuations); Assert.Equal(0, doc.Subscriptions);
    }

    [Theory]
    [InlineData(null, null)] [InlineData("bad-hash", null)] [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "read failure")]
    public void CompletedCommandWithInvalidSavedIdentityNeverResumes(string? hash, string? failure)
    {
        var (ui, doc) = NewRequest(); ui.Start();
        DrawingRevisionTracker.Next = new(@"C:\test\named.dwg", hash, 0, failure);
        doc.End(); ui.Dispatcher.Flush(); ui.ResumeWorkflowAfterExplicitSave(CallbackToken(doc));
        Assert.Equal(0, ui.Continuations); Assert.False(ui.IsPending); Assert.Equal(2, doc.Sent.Count);
    }

    [Fact]
    public void UnrelatedCommandsAndForeignDocumentEventsDoNotAdvanceTheRequest()
    {
        var (ui, doc) = NewRequest(); ui.Start(); doc.End("ZOOM"); new Document().End(); ui.Dispatcher.Flush();
        Assert.Single(doc.Sent); Assert.True(ui.IsPending);
    }

    [Fact]
    public void NewNativeCommandBeforeDispatcherReceivesNoContinuationInput()
    {
        var (ui, doc) = NewRequest(); ui.Start(); doc.End(); doc.CommandInProgress = "LINE";
        ui.Dispatcher.Flush(); Assert.Single(doc.Sent); Assert.False(ui.IsPending);
        Assert.Equal(0, ui.Continuations); Assert.Equal(0, doc.Subscriptions);
    }

    [Fact]
    public void QueueFailureAfterSaveClearsPendingWithoutExecutingContinuation()
    {
        var (ui, doc) = NewRequest(); ui.Start(); doc.End(); doc.ThrowOnSend = true; ui.Dispatcher.Flush();
        Assert.False(ui.IsPending); Assert.Equal(0, ui.Continuations); Assert.Equal(1, ui.Errors);
    }

    [Fact]
    public void DeclinedSaveIssuesNoCommandAndNoPersistentState()
    {
        var (ui, doc) = NewRequest(); MessageBox.Answer = MessageBoxResult.No; ui.Start();
        Assert.Empty(doc.Sent); Assert.False(ui.IsPending); Assert.Equal(0, doc.Subscriptions);
    }

    [Fact]
    public void ExistingSavedDrawingStillReturnsReadyWithoutQueueingOrReloading()
    {
        var (ui, doc) = NewRequest(); Saved(); Assert.True(ui.Start());
        Assert.Empty(doc.Sent); Assert.False(ui.IsPending);
    }
}
