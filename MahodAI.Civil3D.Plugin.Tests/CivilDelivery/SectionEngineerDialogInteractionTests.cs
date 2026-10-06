using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionEngineerDialogInteractionTests
{
    private static SectionPlanRecord Record() => new()
    {
        RecordId = "cl-7CA3", SectionId = "STA12145", SelectedAlignment = "600", Station = 12145.43,
        Cl = new ClSourceRecord
        {
            RecordId = "cl-7CA3", SourceDrawing = "CL.dwg", SourceDrawingHash = new string('a', 64),
            SourceHandle = "7CA3", SourceLayer = "GFC111", SourceEntityType = "LINE",
            SourceEndpoints = new[] { 0d, 0d, 10d, 0d }, WcsEndpoints = new[] { 0d, 0d, 10d, 0d },
        },
        PresentationCoverage = new() { RowAuthorityState = "authoritative" },
    };
    private static SectionTrafficDirectionPlan Lane(double from = 0, double to = 4) => new()
    {
        FromOffsetM = from, ToOffsetM = to, LaneMidOffsetM = (from + to) / 2,
        StripLabel = "נתיב נסיעה", StripKind = "road", EvidenceMode = "motor", State = "resolved",
        Flow = SectionVehicleDirectionPlanner.AlongFlowToken, OfficeCarView = "rear",
        DirectionSource = "arrow", DirectionDigest = new string('b', 64), Reason = "resolved-from-approved-arrow",
    };

    [Fact]
    public void CurrentDirectionIsNotAutoApproved_EditNotifiesWithoutGridRefresh_CancelWritesNothing() => RunSta(() =>
    {
        var record = Record(); record.TrafficDirections.AddRange(new[] { Lane(), Lane(5, 9) });
        var snapshot = JsonSerializer.Serialize(record);
        var dialog = new TrafficDirectionDecisionDialog(new[] { record }, includeResolvedDirections: true);
        var grid = (DataGrid)dialog.FindName("DirectionsGrid");
        var row = (TrafficDirectionDecisionDialog.DirectionRow)grid.Items[0];
        dialog.Approvals.Should().BeEmpty();
        row.CurrentDirection.Should().Contain("מחץ במקור");
        var changes = 0; row.PropertyChanged += (_, _) => changes++;
        row.SelectedChoice = row.Choices.Single(choice => choice.Flow == TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
        dialog.Approvals.Should().ContainSingle().Which.Flow.Should().Be(TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
        changes.Should().Be(2);
        row.IsApproved = false; dialog.Approvals.Should().BeEmpty();
        dialog.Close();
        JsonSerializer.Serialize(record).Should().Be(snapshot);
    });

    [Fact]
    public void NewDirectionServiceIsPartialExactAndAtomicWhileLegacyApiRemainsUnresolvedOnly()
    {
        var record = Record(); record.TrafficDirections.AddRange(new[] { Lane(), Lane(5, 9) });
        var profile = new ProjectProfile();
        var approval = new SectionDecisionProfileService.TrafficDirectionBatchApproval(record, record.TrafficDirections[0],
            TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
        var old = () => SectionDecisionProfileService.ApproveTrafficDirectionsBatch(profile, new[] { record }, new[] { approval }, "Arthur", DateTime.UtcNow);
        old.Should().Throw<InvalidOperationException>();
        SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch(profile, new[] { record }, new[] { approval }, "Arthur", DateTime.UtcNow)
            .Should().Be(1);
        var saved = profile.Sections.Decisions.TrafficDirections.Single();
        saved.AllowArrowOverride.Should().BeTrue(); saved.FromOffsetM.Should().Be(0); saved.ToOffsetM.Should().Be(4);
        var snapshot = JsonSerializer.Serialize(profile);
        var invalid = approval with { Direction = Lane(-0.000001, 4) };
        var stale = () => SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch(profile, new[] { record }, new[] { approval, invalid }, "Arthur", DateTime.UtcNow);
        stale.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(snapshot);
    }

    [Fact]
    public void PlanPassesExactBoundsAndMode_ApplyRestoresPlanEvidenceWithoutReResolvingGlobalFlow()
    {
        var source = typeof(SectionEngineerDialogInteractionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(item => item.Key == "MahodPluginSourceDir").Value!;
        var plan = File.ReadAllText(Path.Combine(source, "CivilDelivery", "Sections", "Services", "SectionPlanService.cs"));
        plan.Split("evidenceMode: mode, laneFromOffsetM: strip.From, laneToOffsetM: strip.To").Length.Should().Be(3);
        var apply = File.ReadAllText(Path.Combine(source, "CivilDelivery", "Sections", "Services", "SectionDecorationService.cs"));
        apply.Should().Contain("SectionVehicleDirectionPlanner.TryRestoreResolved(")
            .And.Contain("item.OffsetM, item.Label, item.Source, item.Evidence")
            .And.NotContain("SectionVehicleDirectionPlanner.Resolve(");
    }

    [Theory]
    [InlineData(1120, 660)]
    [InlineData(900, 580)]
    public void NamesEditorCompiledOffscreenNormalAndMinimum(int width, int height) => RunSta(() =>
    {
        var record = Record();
        record.PresentationCoverage.UnresolvedSpans.Add(new()
        {
            FromOffsetM = -8.279132296405557, ToOffsetM = -4.038714345659342, WidthM = 4.240417950746215,
            LeftKind = "curb", RightKind = "curb", Reason = "no-confident-strip-label",
        });
        record.PresentationCoverage.ResolvedSpans.Add(new()
        {
            FromOffsetM = 6.806031049330396, ToOffsetM = 8.039481591133603, WidthM = 1.2334505418032071,
            LeftKind = "curb", RightKind = "curb", Label = "מדרכה", EvidenceSource = "manual-profile", EvidenceDigest = new string('b', 64),
        });
        var previous = new ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision
        {
            SourceDrawingHash = record.Cl.SourceDrawingHash, SourceHandle = record.Cl.SourceHandle, AlignmentName = "600",
            FromOffsetM = -8.449, ToOffsetM = -4.038714345659342, Label = "נתיב נסיעה", ApprovedBy = "Arthur",
            ApprovedAtUtc = new DateTime(2026, 9, 3, 13, 13, 37, DateTimeKind.Utc),
        };
        var dialog = new SectionSpanLabelDecisionDialog(new[] { record }, false, new[] { previous }, true);
        var grid = (DataGrid)dialog.FindName("SpansGrid"); grid.SelectedItem = grid.Items[0];
        Render(dialog, grid, width, height, "names");
        dialog.Approvals.Should().BeEmpty(); dialog.Close();
    });

    [Theory]
    [InlineData(980, 620)]
    [InlineData(820, 500)]
    public void DirectionEditorCompiledOffscreenNormalAndMinimum(int width, int height) => RunSta(() =>
    {
        var record = Record(); record.TrafficDirections.AddRange(new[] { Lane(), Lane(5, 9) });
        var dialog = new TrafficDirectionDecisionDialog(new[] { record }, true);
        Render(dialog, (DataGrid)dialog.FindName("DirectionsGrid"), width, height, "directions");
        dialog.Approvals.Should().BeEmpty(); dialog.Close();
    });

    private static void Render(Window dialog, DataGrid grid, int width, int height, string name)
    {
        var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var save = (Button)dialog.FindName("BtnSave");
        var bounds = save.TransformToAncestor(content).TransformBounds(new Rect(new Point(), save.RenderSize));
        var output = Environment.GetEnvironmentVariable("MHD_SECTION_EDITOR_RENDER_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetFullPath(output));
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(dialog.Background, null, new Rect(0, 0, width, height));
            image.Render(background); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            var path = Path.Combine(Path.GetFullPath(output), $"section-editor-{name}-{width}x{height}-{Guid.NewGuid():N}.png");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); encoder.Save(stream);
        }
        grid.ActualHeight.Should().BeGreaterThanOrEqualTo(100);
        bounds.Bottom.Should().BeLessThanOrEqualTo(height + 0.5);
        bounds.Top.Should().BeGreaterThanOrEqualTo(0);
        dialog.IsVisible.Should().BeFalse("this is compiled offscreen WPF, not native CAD acceptance");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (failure != null) throw new InvalidOperationException("Unshown section editor test failed.", failure);
    }
}
