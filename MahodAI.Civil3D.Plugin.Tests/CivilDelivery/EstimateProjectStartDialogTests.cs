using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateProjectStartDialogTests
{
    private static EstimateProjectStartDialog Dialog(bool completing = false) => new("greenfield-test", "Greenfield", @"C:\local\Greenfield.dwg",
        @"C:\local\profiles\greenfield-test\project-profile.yaml", "Host: Greenfield.dwg\nNo external references\nNo CL or section sources",
        completingExistingProfile: completing);

    [Fact]
    public void ApproverStartsEmptyAndOnlyATypedNameStarts() => Sta(() =>
    {
        // b24 (b23 E4 live): the Windows account name was prefilled as the approver.
        var dialog = Dialog();
        dialog.ApproverInput.Text.Should().BeEmpty();
        dialog.ConfirmSources.IsChecked = true;
        dialog.Landscape.IsChecked = true;
        dialog.Start.IsEnabled.Should().BeFalse("nobody has typed an approver yet");
        dialog.Validation.Text.Should().Be("יש להזין את שם המאשר");
        dialog.Start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.ApprovedDecision.Should().BeNull();
        dialog.ApproverInput.Text = "   ";
        dialog.Start.IsEnabled.Should().BeFalse();
        dialog.ApproverInput.Text = "typed reviewer";
        dialog.Start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.ApprovedDecision!.ApprovedBy.Should().Be("typed reviewer");
    });

    [Fact]
    public void TheStartDialogIsNeverGivenTheWindowsAccountAsApprover()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(TestPaths.PluginSourceDir,
            "CivilDelivery", "UI", "CivilDeliveryControl.EstimateProjectStart.cs"));
        var call = source.Substring(source.IndexOf("new EstimateProjectStartDialog(", StringComparison.Ordinal));
        call = call.Substring(0, call.IndexOf(';'));
        call.Should().NotContain("UserName");
    }

    [Fact]
    public void EditingAndCancelDoNotReturnApproval() => Sta(() =>
    {
        var dialog = Dialog();
        dialog.ConfirmSources.IsChecked.Should().BeFalse();
        dialog.Start.IsEnabled.Should().BeFalse();
        dialog.ProjectNameInput.Text = "edited project name";
        dialog.ApproverInput.Text = "edited approver";
        dialog.ConfirmSources.IsChecked = true;
        dialog.Start.IsEnabled.Should().BeFalse("the discipline is declared explicitly, never preselected");
        dialog.Validation.Text.Should().Be(MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateProjectStartService.DisciplineMissing);
        dialog.Roads.IsChecked = true;
        dialog.Start.IsEnabled.Should().BeTrue();
        dialog.ApprovedDecision.Should().BeNull();
        dialog.Cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.ApprovedDecision.Should().BeNull();
    });

    [Fact]
    public void ExplicitStartReturnsEditedFieldsOnlyAfterValidScopeConfirmation() => Sta(() =>
    {
        var dialog = Dialog();
        dialog.ProjectNameInput.Text = "  edited estimate  ";
        dialog.ApproverInput.Text = "  reviewer  ";
        dialog.ConfirmSources.IsChecked = true;
        dialog.ApproverInput.Text = " ";
        dialog.Start.IsEnabled.Should().BeFalse();
        dialog.ApproverInput.Text = " reviewer ";
        dialog.Start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.ApprovedDecision.Should().BeNull("no discipline was chosen");
        dialog.Landscape.IsChecked = true;
        dialog.Start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.ApprovedDecision.Should().NotBeNull();
        dialog.ApprovedDecision!.ProjectName.Should().Be("edited estimate");
        dialog.ApprovedDecision.ApprovedBy.Should().Be("reviewer");
        dialog.ApprovedDecision.ConfirmSourceScope.Should().BeTrue();
        dialog.ApprovedDecision.Discipline.Should().Be("landscape");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NarrowLayoutKeepsApprovalActionsVisibleAndLongBodyScrollable(bool completing) => Sta(() =>
    {
        var dialog = Dialog(completing);
        if (completing)
        {
            dialog.Title.Should().Be("השלמת תחום העבודה לאומדן");
            ((System.Windows.Controls.TextBlock)dialog.ConfirmSources.Content).Text.Should().Contain("ההחרגות הקיימות נשמרות").And.NotContain("כל הגאומטריה");
            dialog.Roads.IsChecked.Should().BeFalse(); dialog.Landscape.IsChecked.Should().BeFalse();
        }
        dialog.Width = 540; dialog.Height = 360;
        var root = (System.Windows.Controls.DockPanel)dialog.Content;
        root.FlowDirection = dialog.FlowDirection;
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, dialog.FontFamily);
        root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, dialog.FontSize);
        root.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, dialog.FontWeight);
        root.SetValue(System.Windows.Documents.TextElement.FontStyleProperty, dialog.FontStyle);
        root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, dialog.Foreground);
        dialog.Content = null;
        var frame = new System.Windows.Controls.Border { Background = dialog.Background,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, Child = root };
        frame.Measure(new System.Windows.Size(510, 300)); frame.Arrange(new Rect(0, 0, 510, 300)); frame.UpdateLayout();
        dialog.Start.ActualHeight.Should().BeGreaterThanOrEqualTo(38);
        dialog.Cancel.ActualHeight.Should().BeGreaterThanOrEqualTo(38);
        dialog.BodyScroll.ViewportHeight.Should().BeGreaterThan(0);
        dialog.BodyScroll.ScrollableHeight.Should().BeGreaterThan(0);
        foreach (var action in new FrameworkElement[] { dialog.Start, dialog.Cancel,
            dialog.ConfirmSources, dialog.Validation, dialog.BodyScroll })
        {
            var bounds = action.TransformToAncestor(frame).TransformBounds(new Rect(action.RenderSize));
            bounds.Left.Should().BeGreaterThanOrEqualTo(-0.1);
            bounds.Top.Should().BeGreaterThanOrEqualTo(-0.1);
            bounds.Right.Should().BeLessThanOrEqualTo(frame.ActualWidth + 0.1);
            bounds.Bottom.Should().BeLessThanOrEqualTo(frame.ActualHeight + 0.1);
        }
        ((System.Windows.Media.SolidColorBrush)dialog.ProjectNameInput.Foreground).Color.Should().Be(System.Windows.Media.Colors.White);
        var directory = Environment.GetEnvironmentVariable("MHD_SEMANTIC_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
            // Render the actual 510x300 content at the 540x360 window minimum;
            // no window is shown and no desktop pixels are captured.
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(510, 300, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = System.IO.File.Create(System.IO.Path.Combine(directory, completing ? "estimate-project-complete-discipline.png" : "estimate-project-start.png"));
            encoder.Save(output);
        }
        dialog.Close();
    });

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
