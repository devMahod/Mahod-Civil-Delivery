using System;
using System.IO;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ProjectProfileSelectionDialogTests
{
    private static ProjectProfileSelectionDialog Dialog() => new(new ExistingProjectProfileSelection.Preview(
        @"C:\SYNTHETIC-ONLY\profiles\portable-project\project-profile.yaml", new string('a', 64),
        "SYNTHETIC-PORTABLE", "פרויקט בדיקה בלבד — בחירת פרופיל קיים", "3",
        "כללי שיוך: 2; עם חתימת מאשר: 1\nמחירי פרויקט: 0; מקדמים מאושרים: 0\nהחלטות החרגה: 0\n" +
        "מחירון: SYNTHETIC-CATALOG\nקובץ מחירון: C:\\SYNTHETIC-ONLY\\catalog.xlsx\n" +
        "מדיניות מקורות: נדרשת בדיקה במסלול המדידות הרגיל\nמקורות CL: אין קובצי CL מוגדרים\n" +
        "אלו החלטות קיימות ולא תוצאות מהשרטוט הנוכחי. יחידות, מקור, מחירון וסמכות ייבדקו במסלול הרגיל."),
        @"C:\SYNTHETIC-ONLY\Campus-West.dwg");

    [Fact]
    public void InspectionRequiresExplicitAssociationAndCancelReturnsNoConsent() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = Dialog();
        dialog.Accepted.Should().BeFalse(); dialog.TryAccept().Should().BeFalse();
        dialog.Validation.Text.Should().NotBeNullOrWhiteSpace();
        dialog.Confirm.IsChecked = true; dialog.TryAccept().Should().BeTrue();
        dialog.Confirm.IsChecked = false; dialog.Accepted.Should().BeFalse();
        dialog.Confirm.IsChecked = true;
        dialog.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.Accepted.Should().BeFalse();
    });

    [Theory]
    [InlineData(760, 670)]
    [InlineData(530, 410)]
    public void RealUnshownDialogKeepsConsentAndActionsVisibleAndSourcePreviewScrollable(int width, int height) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var dialog = Dialog();
            try
            {
                var root = (FrameworkElement)dialog.Content;
                root.FlowDirection = dialog.FlowDirection;
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, dialog.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, dialog.FontSize);
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, dialog.Foreground);
                dialog.Content = null;
                var frame = new System.Windows.Controls.Border { Background = dialog.Background,
                    FlowDirection = System.Windows.FlowDirection.LeftToRight, Child = root };
                var size = new System.Windows.Size(width, height);
                frame.Measure(size); frame.Arrange(new Rect(new System.Windows.Point(), size)); frame.UpdateLayout();
                var directory = Environment.GetEnvironmentVariable("MHD_PROFILE_SELECTION_RENDER_DIR");
                void Capture(string suffix)
                {
                    if (string.IsNullOrWhiteSpace(directory)) return;
                    Directory.CreateDirectory(directory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(frame);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = new FileStream(Path.Combine(directory, $"existing-profile-{width}x{height}-{suffix}.png"), FileMode.CreateNew);
                    encoder.Save(output);
                }
                Capture("top");
                dialog.BodyScroll.ViewportHeight.Should().BeGreaterThanOrEqualTo(150);
                foreach (var control in new FrameworkElement[] { dialog.Confirm, dialog.SelectButton, dialog.CancelButton, dialog.BodyScroll })
                {
                    var bounds = control.TransformToAncestor(frame).TransformBounds(new Rect(control.RenderSize));
                    bounds.Left.Should().BeGreaterThanOrEqualTo(-0.1); bounds.Top.Should().BeGreaterThanOrEqualTo(-0.1);
                    bounds.Right.Should().BeLessThanOrEqualTo(width + 0.1); bounds.Bottom.Should().BeLessThanOrEqualTo(height + 0.1);
                }
                dialog.SelectButton.ActualHeight.Should().BeGreaterThanOrEqualTo(38);
                dialog.CancelButton.ActualHeight.Should().BeGreaterThanOrEqualTo(38);
                if (width == 530) dialog.BodyScroll.ScrollableHeight.Should().BeGreaterThan(0);
                dialog.BodyScroll.ScrollToEnd(); frame.UpdateLayout(); Capture("bottom");
                dialog.BodyScroll.VerticalOffset.Should().Be(dialog.BodyScroll.ScrollableHeight);
                dialog.IsVisible.Should().BeFalse(); dialog.Accepted.Should().BeFalse();
            }
            finally { dialog.Close(); }
        });
}
