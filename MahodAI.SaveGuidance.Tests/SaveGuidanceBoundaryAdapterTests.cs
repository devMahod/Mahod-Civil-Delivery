using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using System.Windows;
using Xunit;
using HostApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

public sealed class SaveGuidanceBoundaryAdapterTests : IDisposable
{
    public SaveGuidanceBoundaryAdapterTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        HostApplication.ResetIdle();
        MessageBox.Answer = MessageBoxResult.Yes;
        MessageBox.BeforeAnswer = null;
        MessageBox.LastDefaultResult = MessageBoxResult.None;
        MessageBox.LastOptions = MessageBoxOptions.None;
        DrawingRevisionTracker.Next = new(null, null, 1);
    }

    [Fact]
    public void SavePromptUsesRealRtlHelperAndForwardsDeclinedAnswerExactlyOnce()
    {
        var doc = new Document();
        var ui = new CivilDeliveryControl { ActiveDocument = doc };
        var answered = 0;
        MessageBox.Answer = MessageBoxResult.No;
        MessageBox.BeforeAnswer = () => answered++;
        Assert.False(ui.Start());
        Assert.Equal(1, answered);
        Assert.Equal(MessageBoxResult.None, MessageBox.LastDefaultResult);
        Assert.Equal(MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign, MessageBox.LastOptions);
        Assert.Empty(doc.Sent);
        Assert.False(ui.IsPending);
    }

    [Fact]
    public void ActivationDefersOneGateRefreshUntilIdleAndUnsubscribes()
    {
        var doc = new Document();
        var ui = new CivilDeliveryControl { ActiveDocument = doc };
        ui.Activate(doc);
        Assert.Equal(1, HostApplication.IdleSubscriptions);
        Assert.Equal(0, ui.Refreshes);
        HostApplication.RaiseIdle();
        Assert.Equal(0, HostApplication.IdleSubscriptions);
        Assert.Equal(0, ui.Refreshes);
        ui.Dispatcher.Flush();
        Assert.Equal(1, ui.Refreshes);
        HostApplication.RaiseIdle(); ui.Dispatcher.Flush();
        Assert.Equal(1, ui.Refreshes);
    }

    [Fact]
    public void ClosingOwnerClearsNoticeUsingDatabaseFilename()
    {
        var doc = new Document { Name = "display-name.dwg" };
        doc.Database.Filename = @"C:\test\owned.dwg";
        var ui = new CivilDeliveryControl { ActiveDocument = doc };
        ui.SeedNotice("exported result", doc.Database.Filename);
        ui.Destroy(doc);
        Assert.Empty(ui.NoticeText);
        Assert.Empty(ui.MeasurementDraftNotice.Text);
    }

    [Fact]
    public void ClosingOtherDrawingPreservesExistingNotice()
    {
        var doc = new Document { Name = @"C:\test\owned.dwg" };
        var other = new Document { Name = @"C:\test\other.dwg" };
        var ui = new CivilDeliveryControl { ActiveDocument = doc };
        ui.SeedNotice("exported result", doc.Name);
        ui.Destroy(other);
        Assert.Equal("exported result", ui.NoticeText);
        Assert.Equal("exported result", ui.MeasurementDraftNotice.Text);
    }

    [Fact]
    public void ClosingOwnerFallsBackToDocumentNameWhenDatabaseFilenameIsBlank()
    {
        var doc = new Document { Name = @"C:\test\owned.dwg" };
        doc.Database.Filename = " ";
        var ui = new CivilDeliveryControl { ActiveDocument = doc };
        ui.SeedNotice("exported result", doc.Name);
        ui.Destroy(doc);
        Assert.Empty(ui.NoticeText);
        Assert.Empty(ui.MeasurementDraftNotice.Text);
    }
}
