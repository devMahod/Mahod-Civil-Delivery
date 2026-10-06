using System;
using System.IO;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class KnownPriceBookDialogTests
{
    private static KnownPriceBookIndex.Offer Offer(string edition) => new(new KnownPriceBookIndex.Entry(
        @"C:\SYNTHETIC-ONLY\profiles\test-project\test-price-book.xlsx", new string('a', 64),
        "מחירון בדיקה בלבד — ללא תוקף הנדסי", edition, 12000,
        new ProjectProfile.EstimateProfile.PriceBookColumnMapping
        { SheetName = "סעיפים לבדיקה", HeaderRow = 3, CodeColumn = "A", DescriptionColumn = "B", UnitColumn = "C", PriceColumn = "E" },
        "TEST-ONLY", new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void NoDefaultChoiceAndConsentResetsOnChangeOrCancel() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var offers = new[] { Offer("דוגמה א"), Offer("דוגמה ב") };
        var d = new KnownPriceBookDialog(offers, "בדיקה בלבד");
        d.Books.SelectedIndex.Should().Be(-1); d.UseButton.IsEnabled.Should().BeFalse(); d.TryAccept().Should().BeFalse();
        d.Books.SelectedIndex = 0; d.TryAccept().Should().BeFalse();
        d.Details.Text.Should().Contain("מחיר: E").And.Contain("סעיפים לבדיקה").And.Contain(new string('a', 64));
        d.Confirm.IsChecked = true; d.TryAccept().Should().BeTrue();
        d.Books.SelectedIndex = 1; d.Accepted.Should().BeNull(); d.Confirm.IsChecked.Should().BeFalse();
        d.Details.Text.Should().Contain("דוגמה ב").And.NotContain("דוגמה א")
            .And.Contain(offers[1].Entry.Publisher!).And.Contain("12,000").And.Contain("סעיפים");
        d.Confirm.IsChecked = true; d.TryAccept().Should().BeTrue();
        d.Accepted.Should().BeSameAs(offers[1]);
        d.Accepted!.Entry.Edition.Should().Be("דוגמה ב");
        d.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); d.Accepted.Should().BeNull();
    });

    [Fact]
    public void AutomaticReadingIsDisclosedAndReturnsExactlySelectedOffer() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var mapped = Offer("אוטומטי לבדיקה");
        var automatic = new KnownPriceBookIndex.Offer(mapped.Entry with { Mapping = null });
        var d = new KnownPriceBookDialog(new[] { automatic }, "בדיקה");
        try
        {
            d.Books.SelectedIndex = 0;
            d.Details.Text.Should().Contain("קריאה אוטומטית").And.NotContain("מחיר: E")
                .And.Contain(automatic.Entry.Publisher!).And.Contain(automatic.Entry.Edition!).And.Contain("12,000");
            d.TryAccept().Should().BeFalse(); d.Confirm.IsChecked = true;
            d.TryAccept().Should().BeTrue(); d.Accepted.Should().BeSameAs(automatic);
        }
        finally { d.Close(); }
    });

    [Fact]
    public void EmptyListCannotApprove() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var d = new KnownPriceBookDialog(Array.Empty<KnownPriceBookIndex.Offer>(), "בדיקה");
        d.Confirm.IsChecked = true; d.TryAccept().Should().BeFalse(); d.UseButton.IsEnabled.Should().BeFalse(); d.Close();
    });

    [Theory]
    [InlineData(750, 620)]
    [InlineData(520, 460)]
    public void RealUnshownDialogKeepsActionsReachableAndDetailsScrollable(int width, int height) => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var d = new KnownPriceBookDialog(new[] { Offer("2026 — דוגמת תצוגה בלבד"), Offer("2025 — דוגמת תצוגה בלבד") }, "פרויקט בדיקה בלבד");
        try
        {
            d.Books.SelectedIndex = 0;
            var root = (FrameworkElement)d.Content; root.FlowDirection = d.FlowDirection;
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, d.FontFamily);
            root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, d.FontSize);
            root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, d.Foreground);
            d.Content = null;
            var frame = new System.Windows.Controls.Border { Background = d.Background, FlowDirection = System.Windows.FlowDirection.LeftToRight, Child = root };
            var size = new System.Windows.Size(width, height); frame.Measure(size); frame.Arrange(new Rect(new System.Windows.Point(), size)); frame.UpdateLayout();
            void Capture(string suffix)
            {
                var dir = Environment.GetEnvironmentVariable("MHD_KNOWN_PB_RENDER_DIR"); if (string.IsNullOrWhiteSpace(dir)) return;
                Directory.CreateDirectory(dir);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(frame);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = new FileStream(Path.Combine(dir, $"known-book-{width}-{suffix}-{Guid.NewGuid():N}.png"), FileMode.CreateNew); encoder.Save(output);
            }
            Capture("top");
            foreach (var control in new FrameworkElement[] { d.Confirm, d.UseButton, d.CancelButton, d.BodyScroll })
            {
                var bounds = control.TransformToAncestor(frame).TransformBounds(new Rect(control.RenderSize));
                bounds.Left.Should().BeGreaterThanOrEqualTo(-.1); bounds.Right.Should().BeLessThanOrEqualTo(width + .1);
                bounds.Top.Should().BeGreaterThanOrEqualTo(-.1); bounds.Bottom.Should().BeLessThanOrEqualTo(height + .1);
            }
            d.BodyScroll.ViewportHeight.Should().BeGreaterThan(180); d.UseButton.ActualHeight.Should().BeGreaterThanOrEqualTo(38);
            if (width == 520) d.BodyScroll.ScrollableHeight.Should().BeGreaterThan(0);
            var needsScroll = d.BodyScroll.ScrollableHeight > 0;
            d.BodyScroll.ScrollToEnd(); frame.UpdateLayout();
            if (needsScroll) Capture("bottom"); // no second bitmap of an unchanged, unshown WPF visual
            d.BodyScroll.VerticalOffset.Should().Be(d.BodyScroll.ScrollableHeight); d.Accepted.Should().BeNull(); d.IsVisible.Should().BeFalse();
        }
        finally { d.Close(); }
    });
}
