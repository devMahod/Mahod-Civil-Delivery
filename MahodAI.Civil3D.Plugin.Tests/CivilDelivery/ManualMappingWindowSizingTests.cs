using System;
using System.Windows;
using FluentAssertions;
using Xunit;
using Size = System.Windows.Size;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Pure DIP sizing only, not native HWND/DPI acceptance.</summary>
public sealed class ManualMappingWindowSizingTests
{
    [Fact]
    public void NormalDesktopKeepsPreferredSize() =>
        Dialog.CalculateReviewWindowSize(new Size(1920, 1040), new Size(16, 40))
            .Should().Be(new Size(1180, 780));

    [Fact]
    public void FullHdAt150PercentReservesMeasuredChromeAndTestedClient()
    {
        var work = new Size(1920 / 1.5, 1040 / 1.5);
        var chrome = new Size(16, 40);
        var result = Dialog.CalculateReviewWindowSize(work, chrome)!.Value;
        result.Width.Should().Be(1180);
        result.Height.Should().BeApproximately(work.Height - 16, .001);
        (result.Width - chrome.Width).Should().BeGreaterThanOrEqualTo(900);
        (result.Height - chrome.Height).Should().BeGreaterThanOrEqualTo(620);
    }

    [Fact]
    public void InvalidWorkAreaDoesNotInventScreenDimensions()
    {
        Dialog.CalculateReviewWindowSize(new Size(double.NaN, 800), new Size(16, 40)).Should().BeNull();
        Dialog.CalculateReviewWindowSize(new Size(1200, double.PositiveInfinity), new Size(16, 40)).Should().BeNull();
        Dialog.CalculateReviewWindowSize(new Size(0, 800), new Size(16, 40)).Should().BeNull();
    }

    [Fact]
    public void UnsupportedSmallWorkAreaRefusesInsteadOfHidingFooter()
    {
        Action calculate = () => Dialog.CalculateReviewWindowSize(new Size(1000, 640), new Size(16, 40));
        calculate.Should().Throw<InvalidOperationException>().WithMessage("*לא הוחל שיוך*");
    }
}
