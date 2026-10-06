using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;
using FlowDirection = System.Windows.FlowDirection;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ProjectSetupReviewDialogTests
{
    private static ProjectSetupScan Scan(int layers = 1)
    {
        var scan = new ProjectSetupScan { RunId = "synthetic-setup", ProjectProfileId = "another-project",
            Drawing = @"C:\synthetic-only\model.dwg" };
        for (var i = 0; i < layers; i++)
            scan.ClLayerCandidates.Add(new ClLayerCandidate { Layer = "arbitrary-קו-" + i,
                TwoPointCount = 1, CrossingCount = 1, EvidenceScore = 80, AlignmentsCrossed = { "ציר-A" } });
        scan.Alignments.Add(new AlignmentCandidateSummary { Name = "ציר-A", StartStation = 0, EndStation = 700, Length = 700 });
        scan.Sources.Add(new SourceCandidateSummary { Name = "measured-surface", Kind = "surface" });
        return scan;
    }
    private static ProjectProfile Profile() => new() { ProfileId = "another-project" };
    private static void Choose(ProjectSetupReviewDialog dialog)
    {
        dialog.Layers.Values.First().IsChecked = true;
        dialog.Alignments.Values.First().IsChecked = true;
        dialog.Sources.Values.First().IsChecked = true;
        dialog.Confirm.IsChecked = true;
    }

    [Fact]
    public void SingleUnknownLayerIsSelectable_ExplicitSaveOnly_NoProfileMutation() => Sta(() =>
    {
        var profile = Profile(); var dialog = new ProjectSetupReviewDialog(Scan(), profile);
        dialog.Save.IsEnabled.Should().BeFalse();
        dialog.Layers.Values.Single().IsChecked.Should().BeFalse();
        Choose(dialog); dialog.Save.IsEnabled.Should().BeTrue();
        dialog.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.ApprovedSelection!.ClLayers.Should().Equal("arbitrary-קו-0");
        dialog.ApprovedSelection.AllowedAlignments.Should().Equal("ציר-A");
        dialog.ApprovedSelection.SampledSources.Should().Contain("measured-surface", "surface");
        profile.Sections.Cl.LayerPatterns.Should().BeEmpty();
    });

    [Fact]
    public void MoreThanFifteenCandidates_AllRemainReachable_ChangeInvalidatesConfirmation() => Sta(() =>
    {
        var dialog = new ProjectSetupReviewDialog(Scan(24), Profile());
        dialog.Layers.Should().HaveCount(24); Choose(dialog);
        dialog.Layers.Values.First().IsChecked = false;
        dialog.Layers.Values.Last().IsChecked = true;
        dialog.Save.IsEnabled.Should().BeFalse();
        dialog.Confirm.IsChecked.Should().BeFalse();
        dialog.Confirm.IsChecked = true; dialog.Save.IsEnabled.Should().BeTrue();
        dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.ApprovedSelection.Should().BeNull();
    });

    [Theory]
    [InlineData("no-cl")]
    [InlineData("no-alignment")]
    [InlineData("no-source")]
    [InlineData("incomplete")]
    public void MissingInputOrFailedDiscoveryCannotProduceASelection(string missing) => Sta(() =>
    {
        var scan = Scan();
        if (missing == "no-cl") scan.ClLayerCandidates.Clear();
        if (missing == "no-alignment") scan.Alignments.Clear();
        if (missing == "no-source") scan.Sources.Clear();
        if (missing == "incomplete") scan.ScanComplete = false;
        var dialog = new ProjectSetupReviewDialog(scan, Profile());
        foreach (var box in dialog.Layers.Values.Concat(dialog.Alignments.Values).Concat(dialog.Sources.Values)) box.IsChecked = true;
        dialog.Confirm.IsChecked = true;
        dialog.Save.IsEnabled.Should().BeFalse();
        dialog.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.ApprovedSelection.Should().BeNull();
        dialog.ExternalCl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.PickExternalClRequested.Should().BeTrue();
    });

    [Fact]
    public void SameNamedSurfaceAndCorridorAreDistinct_OneCanBeChosenWithoutCrash() => Sta(() =>
    {
        var scan = Scan();
        scan.Sources.Add(new SourceCandidateSummary { Name = "measured-surface", Kind = "corridor", Handle = "B" });
        var dialog = new ProjectSetupReviewDialog(scan, Profile()); Choose(dialog);
        dialog.Save.IsEnabled.Should().BeTrue();
        dialog.Sources.Values.Last().IsChecked = true; dialog.Confirm.IsChecked = true;
        dialog.Save.IsEnabled.Should().BeFalse();
        dialog.Sources.Values.First().IsChecked = false; dialog.Confirm.IsChecked = true;
        dialog.Save.IsEnabled.Should().BeTrue();
        dialog.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.ApprovedSelection!.SampledSources.Should().ContainSingle().Which.Value.Should().Be("corridor");
    });

    [Fact]
    public void NestedDependencyIsNotPromotedToExplicitClInput() => Sta(() =>
    {
        var scan = Scan();
        scan.ExternalClHashes[@"C:\synthetic-only\cuts.dwg"] = new string('A', 64);
        scan.DiscoverySourceHashes[@"C:\synthetic-only\background.dwg"] = new string('B', 64);
        var dialog = new ProjectSetupReviewDialog(scan, Profile()); Choose(dialog);
        dialog.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.ApprovedSelection!.ClSourceFiles.Should().Contain(@"C:\synthetic-only\cuts.dwg");
        dialog.ApprovedSelection.ClSourceFiles.Should().NotContain(@"C:\synthetic-only\background.dwg");
    });

    [Theory]
    [InlineData(790, 690)]
    [InlineData(530, 365)]
    public void LongListIsScrollableAndActionsRemainVisible(int width, int height) => Sta(() =>
    {
        var dialog = new ProjectSetupReviewDialog(Scan(24), Profile());
        var root = (DockPanel)dialog.Content; root.FlowDirection = dialog.FlowDirection;
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, dialog.FontFamily);
        root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, dialog.FontSize);
        dialog.Content = null;
        var frame = new Border { Background = dialog.Background, FlowDirection = FlowDirection.LeftToRight, Child = root };
        frame.Measure(new Size(width, height)); frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout();
        dialog.BodyScroll.ViewportHeight.Should().BeGreaterThan(0);
        dialog.BodyScroll.ScrollableHeight.Should().BeGreaterThan(0);
        foreach (var element in new FrameworkElement[] { dialog.Save, dialog.Cancel, dialog.Confirm, dialog.Validation, dialog.BodyScroll })
        {
            var bounds = element.TransformToAncestor(frame).TransformBounds(new Rect(element.RenderSize));
            bounds.Left.Should().BeGreaterThanOrEqualTo(-0.1); bounds.Top.Should().BeGreaterThanOrEqualTo(-0.1);
            bounds.Right.Should().BeLessThanOrEqualTo(width + 0.1); bounds.Bottom.Should().BeLessThanOrEqualTo(height + 0.1);
        }
        var output = Environment.GetEnvironmentVariable("MHD_SETUP_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            System.IO.Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(output, $"project-setup-{width}.png")); encoder.Save(file);
        }
        dialog.Close();
    });

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
