using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using FlowDirection = System.Windows.FlowDirection;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Saved WPF renders only; no Show, desktop capture, CAD, or saved profile.</summary>
public sealed class ProjectSetupReviewRenderTests
{
    [Theory]
    [InlineData(790, 730, 1)]
    [InlineData(530, 365, 24)]
    public void CompleteSelectionsRemainReviewableIncludingTheLastSourceAfterScrolling(int width, int height, int layers) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var scan = new ProjectSetupScan { RunId = "SYNTHETIC-RENDER-ONLY", ProjectProfileId = "SYNTHETIC-NEW-PROJECT",
                Drawing = @"C:\SYNTHETIC-ONLY\new-model.dwg" };
            for (var index = 1; index <= layers; index++)
                scan.ClLayerCandidates.Add(new() { Layer = "מיקום-חתך-" + index, TwoPointCount = 1, CrossingCount = 1,
                    AlignmentsCrossed = { "ציר תכנון מאושר-A" }, EvidenceScore = 80 });
            scan.Alignments.Add(new() { Name = "ציר תכנון מאושר-A", StartStation = 0, EndStation = 700, Length = 700 });
            scan.Sources.Add(new() { Name = "משטח מדוד-EG", Kind = "surface", Handle = "A1" });
            var profile = new ProjectProfile { ProfileId = scan.ProjectProfileId };
            var dialog = new ProjectSetupReviewDialog(scan, profile);
            try
            {
                dialog.Layers.Values.Last().IsChecked = true;
                dialog.Alignments.Values.Single().IsChecked = true;
                dialog.Sources.Values.Single().IsChecked = true;
                dialog.Confirm.IsChecked = true;
                var frame = UnshownDialogRender.Attach(dialog, width, height);
                if (layers > 1)
                {
                    dialog.BodyScroll.ScrollableHeight.Should().BeGreaterThan(0);
                    dialog.BodyScroll.ScrollToBottom(); frame.UpdateLayout();
                    dialog.BodyScroll.VerticalOffset.Should().BeApproximately(dialog.BodyScroll.ScrollableHeight, 0.5);
                }
                else dialog.BodyScroll.ScrollableHeight.Should().BeLessThanOrEqualTo(0.5,
                    "the one-CL complete input review should fit at the normal client size");
                UnshownDialogRender.Save(frame, "MHD_SETUP_UI_CAPTURE_DIR",
                    layers == 1 ? $"setup-single-complete-{width}" : $"setup-scroll-bottom-{width}");
                foreach (var element in new FrameworkElement[] { dialog.Save, dialog.Cancel, dialog.Confirm, dialog.Validation })
                    UnshownDialogRender.AssertWithin(element, frame);
                // These actual selected source/alignment controls must be visible, not
                // merely present somewhere inside a long ScrollViewer's content tree.
                UnshownDialogRender.AssertWithin(dialog.Sources.Values.Single(), dialog.BodyScroll);
                UnshownDialogRender.AssertWithin(dialog.Alignments.Values.Single(), dialog.BodyScroll);
                dialog.Save.IsEnabled.Should().BeTrue(); dialog.ApprovedSelection.Should().BeNull();
                dialog.IsVisible.Should().BeFalse(); profile.Sections.Cl.LayerPatterns.Should().BeEmpty();
            }
            finally { dialog.Close(); }
        });
}

internal static class UnshownDialogRender
{
    internal static Border Attach(Window dialog, int width, int height)
    {
        var content = (FrameworkElement)dialog.Content;
        var direction = dialog.FlowDirection; var family = dialog.FontFamily; var fontSize = dialog.FontSize;
        var foreground = dialog.Foreground; var background = dialog.Background;
        dialog.Content = null;
        content.FlowDirection = direction;
        TextElement.SetFontFamily(content, family); TextElement.SetFontSize(content, fontSize);
        TextElement.SetForeground(content, foreground);
        var frame = new Border { Background = background, FlowDirection = FlowDirection.LeftToRight, Child = content };
        frame.Measure(new Size(width, height)); frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout();
        return frame;
    }

    internal static void AssertWithin(FrameworkElement control, FrameworkElement frame)
    {
        var bounds = control.TransformToAncestor(frame).TransformBounds(new Rect(control.RenderSize));
        bounds.Width.Should().BeGreaterThan(0); bounds.Height.Should().BeGreaterThan(0);
        bounds.Left.Should().BeGreaterThanOrEqualTo(-0.5); bounds.Top.Should().BeGreaterThanOrEqualTo(-0.5);
        bounds.Right.Should().BeLessThanOrEqualTo(frame.ActualWidth + 0.5);
        bounds.Bottom.Should().BeLessThanOrEqualTo(frame.ActualHeight + 0.5);
    }

    internal static void Save(Border frame, string variable, string label)
    {
        var directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)frame.ActualWidth, (int)frame.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(Path.Combine(directory, label + "-" + Guid.NewGuid().ToString("N") + ".png"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }
}
