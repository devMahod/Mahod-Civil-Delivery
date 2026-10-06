using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class FindingSourceLocationDialogTests
{
    private static FindingSourceLocationPolicy.Target Target(int number, string? hash = null) => new(
        EstimateFindingCodes.MeasurementFailed, "unreadable Hatch", new ProvenanceRef
        {
            SourceKind = "xref", SourcePathOrUri = @"C:\local\source.dwg",
            DrawingChecksum = hash ?? new string('a', 64), SourceHandle = $"AB/{number:X}",
            XrefPath = "source", Layer = "HATCH", MeasurementMethod = "hatch-area",
        });

    [Fact]
    public void ChoosingOrSearchingDoesNotDispatchAndCancellationLeavesNoChosenTarget() => Sta(() =>
    {
        var targets = Enumerable.Range(1, 81).Select(number => Target(number)).ToArray();
        var dialog = new FindingSourceLocationDialog(targets);
        dialog.Targets.Items.Count.Should().Be(81);
        dialog.ChosenTarget.Should().BeNull();
        dialog.Locate.IsEnabled.Should().BeFalse();
        dialog.Search.Text = "AB/51";
        dialog.Targets.Items.Count.Should().Be(1);
        dialog.Targets.SelectedIndex = 0;
        dialog.Locate.IsEnabled.Should().BeTrue();
        dialog.Details.Text.Should().Contain(new string('a', 64)).And.Contain("AB/51");
        dialog.ChosenTarget.Should().BeNull();
        dialog.Close();
        dialog.ChosenTarget.Should().BeNull();
        targets.Should().HaveCount(81);
    });

    [Fact]
    public void OnlyExplicitLocateReturnsChosenEvidenceAndInvalidIdentityStaysVisible() => Sta(() =>
    {
        var target = Target(1);
        var invalid = Target(2, "not-a-hash");
        var dialog = new FindingSourceLocationDialog(new[] { target, invalid });
        dialog.Targets.SelectedIndex = 1;
        dialog.Locate.IsEnabled.Should().BeFalse();
        dialog.Details.Text.Should().Contain("SHA-256");
        dialog.Targets.SelectedIndex = 0;
        dialog.Locate.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        dialog.ChosenTarget.Should().BeSameAs(target);
    });

    [Fact]
    public void SelectedFailureDisplaysExactReasonAndNextStepWithoutReturningLocateApproval() => Sta(() =>
    {
        var source = Target(1).Source;
        var failure = MeasurementFailureProvenance.Create("TEST", "Failed Hatch", "eNotApplicable", source, "area");
        var issue = new EstimateReviewPolicy.Issue("scan", failure.Code, "aggregate", failure.Title + ": " + failure.Message,
            "Save repaired source and rescan", true, EstimateReviewPolicy.Recovery.Inspect,
            Array.Empty<string>(), Array.Empty<string>()) { Sources = new[] { source } };
        var dialog = new FindingSourceLocationDialog(FindingSourceLocationPolicy.Collect(new[] { issue }));
        dialog.Search.Text = "eNotApplicable";
        dialog.Targets.Items.Count.Should().Be(1);
        dialog.Targets.SelectedIndex = 0;
        dialog.Details.Text.Should().Contain("eNotApplicable").And.Contain("Save repaired source and rescan")
            .And.Contain("AB/1").And.Contain("לא חושב שטח חלופי");
        dialog.ChosenTarget.Should().BeNull();
        RenderOffline(dialog, 820, 590, "finding-context-820x590.png");
        dialog.Close(); dialog.ChosenTarget.Should().BeNull();
        issue.Blocking.Should().BeTrue();
    });

    [Fact]
    public void RecordBackedMixedFindingShowsNextStepAndExactObjectBeforeOptionalContextAndCancelDoesNothing() => Sta(() =>
    {
        var record = new NeutralQuantityRecord
        {
            RecordId = "record-area", ProjectProfileId = "TEST-ONLY", RunId = "test-run",
            Source = new() { Drawing = "source.dwg", DrawingPath = @"C:\local\source.dwg", DrawingHash = new string('a', 64),
                Handle = "AB/1", EntityType = "Polyline", Layer = "HW-CURB", Xref = "GM > nested" },
            Measurement = new() { Kind = "area", Method = "polyline-area+xref-transform", RawValue = 12, Unit = "m2" },
            Provenance = new() { SourceKind = "xref", SourcePathOrUri = @"C:\local\source.dwg", DrawingChecksum = new string('a', 64),
                SourceHandle = "AB/1", XrefPath = "GM > nested", EntityType = "Polyline", Layer = "HW-CURB",
                MeasurementMethod = "polyline-area+xref-transform", RunId = "test-run" },
        };
        var finding = new DeliveryFinding
        {
            FindingId = "mixed", Domain = "estimate", Code = EstimateFindingCodes.MixedDimensionLayer,
            Title = "גבול סגור — בחירת שטח או היקף", Message = "נמדדו שתי חלופות; לא אושרה כמות.",
            RecommendedAction = "בדוק את העצם ובחר יחידת מדידה בהתאם לתכנון.", Severity = FindingSeverity.Error,
            AffectedRecordIds = { record.RecordId },
        };
        var issue = EstimateReviewPolicy.Collect(new[] { record }, new[] { finding }, Array.Empty<DeliveryFinding>(), null).Single();
        var dialog = new FindingSourceLocationDialog(FindingSourceLocationPolicy.Collect(new[] { issue }));
        dialog.Targets.SelectedIndex = 0;
        dialog.Locate.IsEnabled.Should().BeTrue();
        dialog.Details.Text.Should().Contain(finding.RecommendedAction).And.Contain("AB/1").And.Contain("GM > nested")
            .And.Contain("שיטת מדידה מתועדת").And.NotContain("פעולה שנכשלה");
        dialog.Details.Text.Should().StartWith("הצעד הבא: " + finding.RecommendedAction)
            .And.Contain(finding.Title).And.Contain(finding.Code);
        dialog.Details.Text.IndexOf(finding.RecommendedAction, StringComparison.Ordinal).Should().BeLessThan(
            dialog.Details.Text.IndexOf("SHA-256", StringComparison.Ordinal));
        RenderOffline(dialog, 510, 300, "record-source-review-510x300.png");
        dialog.Search.Text = "no-matching-source";
        dialog.Targets.Items.Count.Should().Be(0);
        dialog.Locate.IsEnabled.Should().BeFalse();
        dialog.ChosenTarget.Should().BeNull();
        dialog.Close();
        record.Classification.MappingApprovedBy.Should().BeNull();
        issue.Blocking.Should().BeTrue();
    });

    [Fact]
    public void NarrowWindowRetainsSearchSelectionDetailsAndActionControls() => Sta(() =>
    {
        var dialog = new FindingSourceLocationDialog(new[] { Target(1) }) { Width = 540, Height = 360 };
        dialog.Targets.SelectedIndex = 0;
        var root = (System.Windows.Controls.Grid)dialog.Content;
        root.Measure(new System.Windows.Size(510, 300));
        root.Arrange(new Rect(0, 0, 510, 300));
        dialog.Search.ActualHeight.Should().BeGreaterThanOrEqualTo(30);
        dialog.Targets.ActualHeight.Should().BeGreaterThanOrEqualTo(70);
        dialog.Details.ActualHeight.Should().BeGreaterThan(0);
        dialog.Locate.ActualHeight.Should().BeGreaterThanOrEqualTo(38);
        var foreground = ((System.Windows.Media.SolidColorBrush)dialog.Search.Foreground).Color;
        var background = ((System.Windows.Media.SolidColorBrush)dialog.Search.Background).Color;
        foreground.Should().Be(System.Windows.Media.Colors.White);
        background.Should().Be(System.Windows.Media.Color.FromRgb(29, 35, 47));
        dialog.Details.Foreground.Should().BeSameAs(dialog.Search.Foreground);
        dialog.Targets.Foreground.Should().BeSameAs(dialog.Search.Foreground);
        RenderOffline(dialog, 510, 300, "finding-context-510x300.png");
        dialog.Close();
    });

    private static void RenderOffline(FindingSourceLocationDialog dialog, int width, int height, string name)
    {
        var output = Environment.GetEnvironmentVariable("MHD_PALETTE_RENDER_DIR");
        var root = (System.Windows.Controls.Grid)dialog.Content;
        var direction = dialog.FlowDirection;
        var font = dialog.FontFamily;
        var foreground = dialog.Foreground;
        dialog.Content = null;
        root.FlowDirection = direction;
        System.Windows.Documents.TextElement.SetFontFamily(root, font);
        System.Windows.Documents.TextElement.SetFontSize(root, dialog.FontSize);
        System.Windows.Documents.TextElement.SetForeground(root, foreground);
        var frame = new System.Windows.Controls.Border { Background = dialog.Background, Child = root,
            FlowDirection = System.Windows.FlowDirection.LeftToRight };
        frame.Measure(new System.Windows.Size(width, height));
        frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout();
        foreach (var control in new FrameworkElement[] { dialog.Search, dialog.Targets, dialog.Details, dialog.Locate })
        {
            var bounds = control.TransformToAncestor(frame).TransformBounds(new Rect(control.RenderSize));
            bounds.Left.Should().BeGreaterThanOrEqualTo(0);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0);
            bounds.Right.Should().BeLessThanOrEqualTo(width);
            bounds.Bottom.Should().BeLessThanOrEqualTo(height);
        }
        if (string.IsNullOrWhiteSpace(output)) return;
        System.IO.Directory.CreateDirectory(output);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new System.IO.FileStream(System.IO.Path.Combine(output, name), System.IO.FileMode.Create);
        encoder.Save(stream);
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
