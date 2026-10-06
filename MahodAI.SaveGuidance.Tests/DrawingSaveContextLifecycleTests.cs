using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using System.Windows;
using Xunit;

public sealed class DrawingSaveContextLifecycleTests
{
    private static (CivilDeliveryControl Ui, Document Doc) Open(string name = @"C:\test\first.dwg")
    {
        MessageBox.Answer = MessageBoxResult.Yes; MessageBox.BeforeAnswer = null;
        DrawingRevisionTracker.Next = new(null, null, 1);
        var doc = new Document { Name = name };
        var ui = new CivilDeliveryControl { ActiveDocument = doc };
        ui.SeedContext(); ui.Observe();
        return (ui, doc);
    }

    [Theory]
    [InlineData("QSAVE")] [InlineData("SAVEAS")] [InlineData("SAVE")]
    public void SaveAsDefersContextChangeAndRefreshesNewName(string command)
    {
        var (ui, doc) = Open(); doc.Name = @"C:\test\second.dwg"; doc.End(command);
        Assert.Equal(0, ui.DrawingChanges); Assert.Equal(0, ui.ProfileReloads);
        ui.Dispatcher.Flush();
        Assert.Equal(1, ui.DrawingChanges); Assert.Equal(doc.Name, ui.Label);
        Assert.Null(ui.PlanReference); Assert.Null(ui.ScanReference); Assert.Equal(1, ui.ProfileReloads);
        Assert.Empty(doc.Sent);
    }

    [Fact]
    public void SamePathQsavePreservesPlanScanAndProfileReferences()
    {
        var (ui, doc) = Open(); var plan = ui.PlanReference; var scan = ui.ScanReference; var profile = ui.ProfileReference;
        doc.End(); ui.Dispatcher.Flush();
        Assert.Same(plan, ui.PlanReference); Assert.Same(scan, ui.ScanReference); Assert.Same(profile, ui.ProfileReference);
        Assert.Equal(0, ui.DrawingChanges); Assert.Equal(0, ui.ProfileReloads);
        Assert.Equal(doc.Name, ui.Label); Assert.Empty(doc.Sent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstSaveKeepsContinuationOnceRegardlessOfEventSubscriptionOrder(bool observerLast)
    {
        var (ui, doc) = Open("Drawing1.dwg");
        if (observerLast) ui.Deactivate(doc);
        ui.Start(); var token = ui.Token;
        if (observerLast) ui.Observe();
        Assert.Equal("_.QSAVE\n", Assert.Single(doc.Sent));
        doc.Name = @"C:\test\named.dwg";
        DrawingRevisionTracker.Next = new(doc.Name, new string('a', 64), 0);
        doc.End(); ui.Dispatcher.Flush();
        Assert.Equal(1, ui.DrawingChanges); Assert.True(ui.IsPending);
        Assert.Equal("MHD_DELIVERY_AFTER_SAVE\n" + token + "\n", doc.Sent[1]);
        ui.ResumeWorkflowAfterExplicitSave(token); ui.ResumeWorkflowAfterExplicitSave(token);
        Assert.Equal(1, ui.Continuations); Assert.False(ui.IsPending); Assert.Equal(doc.Name, ui.Label);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CancelOrFailedSaveDropsQueuedContextCallback(bool failed)
    {
        var (ui, doc) = Open(); doc.Name = @"C:\test\second.dwg"; doc.End("SAVEAS");
        if (failed) doc.Fail("SAVEAS"); else doc.Cancel("SAVEAS");
        ui.Dispatcher.Flush(); Assert.Equal(0, ui.DrawingChanges); Assert.Equal(0, ui.LabelRefreshes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SwitchedDocumentOrDatabaseDropsQueuedCallback(bool databaseOnly)
    {
        var (ui, doc) = Open(); doc.Name = @"C:\test\second.dwg"; doc.End("SAVEAS");
        if (databaseOnly) doc.Database.UnmanagedObject = new(91); else ui.ActiveDocument = new Document();
        ui.Dispatcher.Flush(); Assert.Equal(0, ui.DrawingChanges); Assert.Equal(0, ui.LabelRefreshes);
    }

    [Fact]
    public void RepeatedObserveHasOneSubscriptionSetAndDestroyDetachesIt()
    {
        var (ui, doc) = Open(); ui.Observe(); ui.Observe(); Assert.Equal(3, doc.Subscriptions);
        doc.Name = @"C:\test\second.dwg"; doc.End("SAVEAS"); ui.Destroy(doc);
        Assert.Equal(0, doc.Subscriptions); ui.Dispatcher.Flush(); Assert.Equal(0, ui.DrawingChanges);
    }

    [Fact]
    public void SwitchAwayAndBackInvalidatesOldQueuedSaveCallback()
    {
        var (ui, doc) = Open(); doc.Name = @"C:\test\second.dwg"; doc.End("SAVEAS");
        ui.Deactivate(doc); ui.Observe(); ui.Dispatcher.Flush(); Assert.Equal(0, ui.DrawingChanges);
    }
}
