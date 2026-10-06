using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The span-label review window comes to the front once when first rendered (it opened behind Civil in the live r9 round)
/// and keeps a taskbar button; the correction is one Activate + Topmost pulse + SetForegroundWindow, never a loop, a timer
/// or a change of owner/modality (those stay with <see cref="CivilModalHost"/>).
/// </summary>
public sealed class DialogActivationTests
{
    [Fact]
    public void TheSpanLabelReviewBringsItselfForwardOnceAndStaysFindable()
    {
        Ui("SectionSpanLabelDecisionDialog.xaml.cs").Should().Contain("DialogActivation.BringForwardOnFirstRender(this);");
        Ui("SectionSpanLabelDecisionDialog.xaml").Should().Contain("ShowInTaskbar=\"True\"");
        var source = Ui("DialogActivation.cs");
        foreach (var forbidden in new[] { "DispatcherTimer", "while (", "for (", "Thread.Sleep", "Task.Delay", ".Owner =", "ShowModalWindow", "DialogResult" })
            source.Should().NotContain(forbidden);
        source.Should().Contain("window.ContentRendered -= OnRendered;", "it runs once");
    }

    [Fact]
    public void BringForwardLeavesTheWindowNotTopmostAndIgnoresAHiddenWindow()
    {
        RunSta(() =>
        {
            var window = new Window { Width = 200, Height = 120, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try
            {
                DialogActivation.BringForward(window);   // not shown yet: nothing happens
                window.Topmost.Should().BeFalse();
                window.Show();
                DialogActivation.BringForward(window);
                window.Topmost.Should().BeFalse("the pulse must not leave the review window always-on-top");
                window.IsVisible.Should().BeTrue();
            }
            finally { window.Close(); }
        });
    }

    private static string Ui(string name)
    {
        var root = typeof(DialogActivationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", name));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue("the window test must complete");
        if (failure != null) throw new InvalidOperationException("Dialog activation test failed.", failure);
    }
}
