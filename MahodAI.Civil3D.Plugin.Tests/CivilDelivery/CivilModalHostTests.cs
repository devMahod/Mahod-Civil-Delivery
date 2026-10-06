using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;
using Window = System.Windows.Window;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Unshown WPF and a per-call host stub; not native modal lifecycle acceptance.</summary>
public sealed class CivilModalHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void ExactHostResultIsReturnedOnceAndOnlyTrueAllowsContinuation(bool? hostResult)
    {
        RunSta(() =>
        {
            var window = new Window();
            try
            {
                var owner = new IntPtr(1234);
                var calls = 0;
                var approvedContinuations = 0;
                var result = CivilModalHost.ShowFromPalette(window, owner, (actualOwner, dialog, persist) =>
                {
                    calls++;
                    actualOwner.Should().Be(owner);
                    dialog.Should().BeSameAs(window);
                    persist.Should().BeFalse();
                    return hostResult;
                });
                if (result == true) approvedContinuations++;
                result.Should().Be(hostResult);
                calls.Should().Be(1);
                approvedContinuations.Should().Be(hostResult == true ? 1 : 0);
                window.DialogResult.Should().BeNull("the adapter does not assign approval state");
                window.IsVisible.Should().BeFalse("the offline stub never displays a native modal");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void HostFailurePropagatesUnchangedWithoutRetryOrApprovalContinuation()
    {
        RunSta(() =>
        {
            var window = new Window();
            try
            {
                var failure = new InvalidOperationException("synthetic host failure");
                var calls = 0;
                var approved = false;
                Action show = () =>
                {
                    var result = CivilModalHost.ShowFromPalette(window, new IntPtr(1), (_, _, _) =>
                    {
                        calls++;
                        throw failure;
                    });
                    if (result == true) approved = true;
                };
                show.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
                calls.Should().Be(1);
                approved.Should().BeFalse();
                window.DialogResult.Should().BeNull();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MissingNativeOwnerRefusesBeforeCallingHost()
    {
        RunSta(() =>
        {
            var window = new Window();
            try
            {
                var calls = 0;
                Action show = () => CivilModalHost.ShowFromPalette(window, IntPtr.Zero, (_, _, _) =>
                {
                    calls++;
                    return true;
                });
                show.Should().Throw<InvalidOperationException>();
                calls.Should().Be(0);
                window.DialogResult.Should().BeNull();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InvalidArgumentsDoNotCallHost()
    {
        RunSta(() =>
        {
            var calls = 0;
            Action noWindow = () => CivilModalHost.ShowFromPalette(null!, new IntPtr(1), (_, _, _) =>
            {
                calls++;
                return true;
            });
            noWindow.Should().Throw<ArgumentNullException>();
            var window = new Window();
            try
            {
                Action noHost = () => CivilModalHost.ShowFromPalette(window, new IntPtr(1), null!);
                noHost.Should().Throw<ArgumentNullException>();
                calls.Should().Be(0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WrongDispatcherRefusesBeforeCallingHost()
    {
        RunSta(() =>
        {
            var window = new Window();
            try
            {
                Exception? failure = null;
                var calls = 0;
                var other = new Thread(() =>
                {
                    try
                    {
                        CivilModalHost.ShowFromPalette(window, new IntPtr(1), (_, _, _) =>
                        {
                            calls++;
                            return true;
                        });
                    }
                    catch (Exception ex) { failure = ex; }
                });
                other.Start();
                other.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                failure.Should().BeOfType<InvalidOperationException>();
                calls.Should().Be(0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AllEighteenPaletteWpfCallsitesUseHostAndOnlyNativeFilePickersKeepShowDialog()
    {
        var callers = new[]
        {
            ("CivilDeliveryControl.xaml.cs", 13),
            ("CivilDeliveryControl.SectionBatchReview.cs", 1),
            ("CivilDeliveryControl.ProjectPrice.cs", 1),
            ("CivilDeliveryControl.MappingReview.cs", 1),
            ("CivilDeliveryControl.EstimateGuidance.cs", 1),
            ("CivilDeliveryControl.MaterialAreas.cs", 1),
        };
        foreach (var (file, expected) in callers)
        {
            var source = Ui(file);
            source.Split("CivilModalHost.ShowFromPalette(", StringSplitOptions.None).Length.Should().Be(expected + 1, file);
            source.Should().NotContain("Window.GetWindow(this)", file);
            source.Split(".ShowDialog()", StringSplitOptions.None).Length.Should().Be(file.EndsWith(".xaml.cs") ? 3 : 1, file);
        }
        Ui("CivilDeliveryControl.xaml.cs").Split("new Microsoft.Win32.OpenFileDialog", StringSplitOptions.None)
            .Length.Should().Be(3, "the catalog and CL file pickers are deliberately unchanged");
        Ui("CivilDeliveryControl.xaml.cs").Should().Contain("CivilModalHost.ShowFromPalette(review)",
            "the new project-source review must use the native modal owner too");
        Ui("CivilDeliveryControl.xaml.cs").Should().Contain("var review = new PriceBookMappingDialog(dlg.FileName, insp);",
            "the price-book review window (b15) is hosted like every other palette window");
    }

    [Fact]
    public void ProductionAdapterUsesVerifiedCoreHostSignatureWithoutFallbackOrWindowStateHacks()
    {
        var source = Ui("CivilModalHost.cs");
        source.Should().Contain("Autodesk.AutoCAD.ApplicationServices.Core.Application")
            .And.Contain("CivilApplication.MainWindow.Handle")
            .And.Contain("CivilApplication.ShowModalWindow")
            .And.Contain("return showModal(owner, dialog, false);")
            .And.NotContain(".ShowDialog(")
            .And.NotContain("EnableWindow")
            .And.NotContain("PushModal")
            .And.NotContain("PopModal")
            .And.NotContain("DialogResult =")
            .And.NotContain("catch");
    }

    private static string Ui(string name)
    {
        var root = typeof(CivilModalHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", name));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue("the unshown test must complete");
        if (failure != null) throw new InvalidOperationException("Offline modal adapter test failed.", failure);
    }
}
