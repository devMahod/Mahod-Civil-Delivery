using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;
using Choice = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService.ReviewedMappingChoice;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Explicit synthetic review only: never reads/writes the active drawing or profile.</summary>
public sealed class ManualMappingCaseScopeTests
{
    private const string Lower = "layer:Road|length", Upper = "layer:ROAD|length", Other = "layer:OTHER|length";
    private const string Reviewer = "SYNTHETIC CASE SCOPE TEST ONLY";

    private static NeutralQuantityRecord Record(string id, string key, string layer, double quantity,
        string unit = "m", string kind = "length", string method = "line-length") => new()
    {
        RecordId = id, ProjectProfileId = "6422", RunId = "SYNTHETIC-CASE-ONLY",
        Source = new() { Drawing = "SYNTHETIC-" + id + ".dwg", DrawingPath = @"C:\synthetic\" + id + ".dwg",
            DrawingHash = new string('a', 64), Handle = id, Layer = layer, EntityType = "LINE" },
        Measurement = new() { RawValue = quantity, Kind = kind, Unit = unit, Method = method,
            GeometryEvidence = new[] { 0d, 0d, 10d, 1d } },
        Classification = new() { RuleKey = key }, Status = DeliveryStatus.ReviewRequired,
        Findings = { new() { FindingId = "unmapped-" + id, Code = EstimateFindingCodes.Unmapped,
            Severity = FindingSeverity.ReviewRequired, Domain = "estimate", Title = "SYNTHETIC undecided",
            AffectedRecordIds = { id } } },
    };

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "mcd-case-scope-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Root, "SYNTHETIC-ONLY-profile.yaml");
        internal readonly CatalogSnapshot Catalog = ManualMappingBatchDialogTests.Catalog();
        internal readonly ProjectProfile Profile = new() { ProfileId = "6422", ProjectName = Reviewer };
        internal readonly List<NeutralQuantityRecord> Records = new()
        {
            Record("A1", Lower, "A|Road", 10), Record("B1", Upper, "B|ROAD", 20), Record("C1", Other, "C|OTHER", 7),
        };
        internal EstimateWorkflowService.ScanResult Scan = null!;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Profile.Estimate.Catalog.CatalogFile = "SYNTHETIC-ONLY-CATALOG.xlsx";
            Profile.Estimate.Catalog.CatalogFileHash = Catalog.FileHash;
            Profile.Estimate.Pricing.PriceBookSnapshotId = Catalog.SnapshotId;
            Profile.Estimate.Pricing.PriceBookHash = Catalog.FileHash;
            Profile.Estimate.PriceBooks.Add(new() { Id = Catalog.SnapshotId,
                File = "SYNTHETIC-ONLY-CATALOG.xlsx", FileHash = Catalog.FileHash });
            RefreshScan();
        }
        internal void RefreshScan() => Scan = new()
        {
            RunId = "SYNTHETIC-CASE-ONLY", ProjectProfileId = Profile.ProfileId, Records = Records,
            ProfileSource = Target, SourceDrawing = @"C:\synthetic\host.dwg", SourceDrawingHash = new string('a', 64),
            SourceDbMod = 0, DatabaseRevision = "SYNTHETIC-REVISION", ProjectProfileHash = new string('b', 64),
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(Profile),
            ProfileWriteState = ProfileCasTest.For(Profile, Target), DiscoveryMode = true,
            Findings = { new() { FindingId = "retain-source", Code = EstimatePreflightPolicy.EarthworksNotAssessedCode,
                Domain = "estimate", Severity = FindingSeverity.ReviewRequired, Title = "SYNTHETIC unresolved scope remains" } },
        };
        internal IReadOnlyList<Dialog.Group> Groups() => CivilDeliveryControl.BuildMappingReviewGroups(Scan,
            Records.GroupBy(record => record.Classification.RuleKey!, StringComparer.Ordinal).Select(group =>
            {
                var record = group.First();
                return new QuantityRowViewModel { RuleKey = group.Key, Layer = SectionProjectionLogic.LayerLeaf(record.Source.Layer),
                    EntityType = record.Source.EntityType, Method = record.Measurement.Kind, ObjectCount = group.Count(),
                    Quantity = group.Sum(item => item.Measurement.RawValue), Unit = record.Measurement.Unit, MappingState = "לבדיקה" };
            }), Array.Empty<MappingProposal>(), Profile);
        internal ProjectProfileWriter.SaveResult Save(params Choice[] choices) => new EstimateWorkflowService()
            .SaveReviewedMappings(Profile, Catalog, Scan, choices, Reviewer, Target, Scan.ProfileWriteState!);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [Fact]
    public void FullScopeShowsThirtyThenExplicitReviewSavesReopensAndRebasesBothButNotOther() =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            using var f = new Fixture();
            var original = JsonSerializer.Serialize(f.Scan);
            var groups = f.Groups(); groups.Should().HaveCount(2);
            var merged = groups.Single(group => group.RuleKey == Lower);
            merged.Quantity.Should().Be(30); merged.ObjectCount.Should().Be(2);
            merged.ReadOnlyReason.Should().BeNull(); merged.Layer.Should().Contain("Road").And.Contain("ROAD");
            merged.FindingsSummary.Should().Contain(Lower).And.Contain(Upper).And.Contain(@"C:\synthetic\A1.dwg")
                .And.Contain(@"C:\synthetic\B1.dwg").And.Contain("היקף שיוך מלא");
            ProjectProfileWriter.SaveResult? saved = null;
            var dialog = new Dialog(groups, f.Catalog, (choices, approvedBy) =>
            {
                approvedBy.Should().Be(Reviewer);
                saved = f.Save(choices.Select(choice => new Choice(choice.RuleKey, choice.CatalogCode,
                    choice.ExcludedAlternativeRuleKey)).ToArray());
            }, initialRuleKey: Lower);
            try
            {
                dialog.ApprovedChoices.Should().BeNull(); File.Exists(f.Target).Should().BeFalse();
                ManualMappingBatchDialogTests.SelectCode(dialog);
                dialog.StageSelection().Should().BeTrue(); dialog.TryConfirm().Should().BeFalse();
                File.Exists(f.Target).Should().BeFalse("staging is not approval");
                dialog.Approver.Text = Reviewer; dialog.Confirm.IsChecked = true;
                dialog.TryConfirm().Should().BeTrue(); saved.Should().NotBeNull();
                var reopened = ProjectProfileLoader.LoadFromFile(saved!.Path).Profile!;
                reopened.Estimate.QuantitySources.Rules.Should().ContainSingle().Which.RuleKey.Should().Be(Lower);
                var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(f.Scan, reopened, saved, new[] { Lower });
                rebased.Records.Take(2).Should().OnlyContain(record => record.Classification.RuleKey == Lower &&
                    record.Classification.CandidateCatalogCode == ManualMappingBatchDialogTests.Code &&
                    record.Classification.MappingApprovedBy == Reviewer);
                rebased.Records.Take(2).Sum(record => record.Measurement.RawValue).Should().Be(30);
                rebased.Records[2].Should().BeSameAs(f.Records[2]);
                rebased.Findings.Should().Contain(f.Scan.Findings[0]);
                rebased.Records.Select(record => record.Source).Should().Equal(f.Records.Select(record => record.Source));
                rebased.Records.Select(record => record.Measurement).Should().Equal(f.Records.Select(record => record.Measurement));
                JsonSerializer.Serialize(f.Scan).Should().Be(original);
                reopened.Estimate.ProjectOverrides.Should().BeEmpty(); reopened.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
            }
            finally { dialog.Close(); }
        });

    [Fact]
    public void FullScopeCancelWritesNothingAndKeepsOriginalChoices() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        using var f = new Fixture(); var before = JsonSerializer.Serialize(f.Profile); var saves = 0;
        var dialog = new Dialog(f.Groups(), f.Catalog, (_, _) => saves++);
        ManualMappingBatchDialogTests.SelectCode(dialog); dialog.StageSelection().Should().BeTrue(); dialog.Close();
        saves.Should().Be(0); File.Exists(f.Target).Should().BeFalse(); JsonSerializer.Serialize(f.Profile).Should().Be(before);
    });

    [Theory]
    [InlineData("unit")]
    [InlineData("kind")]
    [InlineData("closed")]
    [InlineData("profile")]
    [InlineData("overflow")]
    [InlineData("layer")]
    public void UnsafeCaseScopeRejectsWholeTransactionBeforeAnyOtherChoiceIsSaved(string fault)
    {
        using var f = new Fixture();
        if (fault == "unit") f.Records[1] = Record("B1", Upper, "B|ROAD", 20, "cm");
        if (fault == "kind") f.Records[1] = Record("B1", Upper, "B|ROAD", 20, "m", "area");
        if (fault == "closed") f.Records[1] = Record("B1", Upper, "B|ROAD", 20, method: "closed-polyline-perimeter");
        if (fault == "layer") f.Records[1] = Record("B1", Upper, "B|DIFFERENT", 20);
        if (fault == "overflow")
        {
            f.Records[0] = Record("A1", Lower, "A|Road", double.MaxValue);
            f.Records[1] = Record("B1", Upper, "B|ROAD", double.MaxValue);
        }
        if (fault == "profile") foreach (var key in new[] { Lower, Upper })
            f.Profile.Estimate.QuantitySources.Rules.Add(new() { RuleKey = key, LayerPattern = "Road", MeasurementKind = "length" });
        f.RefreshScan(); var before = JsonSerializer.Serialize(f.Profile); var scanBefore = JsonSerializer.Serialize(f.Scan);
        var group = f.Groups().Single(group => string.Equals(group.RuleKey, Lower, StringComparison.OrdinalIgnoreCase));
        group.ReadOnlyReason.Should().NotBeNullOrWhiteSpace();
        if (fault == "overflow")
        {
            ManualMappingCaseScope.Collect(f.Records, f.Profile).First().TotalQuantity.Should().BeNull();
            group.FindingsSummary.Should().Contain("אין ערך אפס"); group.Quantity.Should().NotBe(0);
        }
        Action save = () => f.Save(new Choice(Other, ManualMappingBatchDialogTests.Code), new Choice(Lower, ManualMappingBatchDialogTests.Code));
        save.Should().Throw<InvalidOperationException>();
        File.Exists(f.Target).Should().BeFalse(); JsonSerializer.Serialize(f.Profile).Should().Be(before);
        JsonSerializer.Serialize(f.Scan).Should().Be(scanBefore);
    }

    [Fact]
    public void LegacyGuardRefusesSelectedCaseScopeButLeavesNormalAndUnrelatedScopesUsable()
    {
        using var f = new Fixture();
        Action unsafeSave = () => ManualMappingCaseScope.RequireLegacyScopeIsExact(f.Records, new[] { Lower });
        unsafeSave.Should().Throw<InvalidOperationException>().WithMessage("*שיוך ידני / עריכת שיוכים*");
        ManualMappingCaseScope.RequireLegacyScopeIsExact(f.Records, new[] { Other });
        f.Records.RemoveAt(1); f.RefreshScan();
        ManualMappingCaseScope.RequireLegacyScopeIsExact(f.Records, new[] { Lower });
        f.Groups().Single(group => group.RuleKey == Lower).Quantity.Should().Be(10);
        var saved = f.Save(new Choice(Lower, ManualMappingBatchDialogTests.Code));
        File.Exists(saved.Path).Should().BeTrue(); f.Profile.Estimate.QuantitySources.Rules.Should().ContainSingle();
    }

    [Fact]
    public void StaleProfileRefusesCaseScopeWithoutReplacingTheOtherSavedDecision()
    {
        using var f = new Fixture();
        ProfileCasTest.Save(f.Profile, f.Target, "SYNTHETIC initial baseline", Reviewer);
        f.RefreshScan();
        var other = ProjectProfileLoader.LoadFromFile(f.Target).Profile!;
        other.ProjectName = "SYNTHETIC OTHER WRITER";
        ProfileCasTest.Save(other, f.Target, "SYNTHETIC competing save", Reviewer);
        var bytes = File.ReadAllBytes(f.Target); var before = JsonSerializer.Serialize(f.Profile);
        Action save = () => f.Save(new Choice(Lower, ManualMappingBatchDialogTests.Code));
        save.Should().Throw<InvalidOperationException>();
        File.ReadAllBytes(f.Target).Should().Equal(bytes); JsonSerializer.Serialize(f.Profile).Should().Be(before);
    }

    [Fact]
    public void AllLegacyProductionSaveCallersGuardOrRouteBeforeSaving()
    {
        var root = EstimateFixtures.RepoRoot();
        foreach (var relative in new[] { "MahodAI.Civil3D.Plugin/Tools/CivilDelivery/EstimateTools.cs",
                     "MahodAI.Civil3D.Plugin/CivilDelivery/Commands/MhdEstimateCommand.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            source.IndexOf("ManualMappingCaseScope.RequireLegacyScopeIsExact", StringComparison.Ordinal)
                .Should().BeLessThan(source.IndexOf(
                    $"workflow.{nameof(EstimateWorkflowService.SaveApprovedMappings)}(", StringComparison.Ordinal));
        }
        var ui = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/UI/CivilDeliveryControl.xaml.cs"));
        var single = ui[ui.IndexOf("private void OnApproveMapping(", StringComparison.Ordinal)..ui.IndexOf("private void OnApproveProvenMappings(", StringComparison.Ordinal)];
        single.Should().Contain("ManualMappingCaseScope.RequiresFullReview").And.Contain("OnReviewMappings(sender, e)");
        single.IndexOf("ManualMappingCaseScope.RequiresFullReview", StringComparison.Ordinal).Should()
            .BeLessThan(single.IndexOf("new CatalogPickerDialog", StringComparison.Ordinal));
        var batch = ui[ui.IndexOf("private void OnApproveProvenMappings(", StringComparison.Ordinal)..];
        batch.IndexOf("ManualMappingCaseScope.RequiresFullReview", StringComparison.Ordinal).Should()
            .BeLessThan(batch.IndexOf("new ProvenMappingBatchDecisionDialog", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1150, 740)]
    [InlineData(870, 620)]
    public void FullCaseScopeActualDialogRendersWithoutNativeWindow(int width, int height) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            using var f = new Fixture();
            var dialog = new Dialog(f.Groups(), f.Catalog, initialRuleKey: Lower);
            try
            {
                ManualMappingBatchDialogTests.SelectCode(dialog);
                var root = (System.Windows.FrameworkElement)dialog.Content;
                root.FlowDirection = dialog.FlowDirection;
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, dialog.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, dialog.FontSize);
                root.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, dialog.FontWeight);
                root.SetValue(System.Windows.Documents.TextElement.FontStyleProperty, dialog.FontStyle);
                root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, dialog.Foreground);
                dialog.Content = null;
                var frame = new System.Windows.Controls.Border { Background = dialog.Background, Child = root,
                    FlowDirection = System.Windows.FlowDirection.LeftToRight };
                var size = new System.Windows.Size(width, height);
                frame.Measure(size); frame.Arrange(new System.Windows.Rect(new System.Windows.Point(), size)); frame.UpdateLayout();
                var directory = Environment.GetEnvironmentVariable("MHD_MAPPING_CASE_RENDER_DIR");
                void Capture(string suffix)
                {
                    if (string.IsNullOrWhiteSpace(directory)) return;
                    Directory.CreateDirectory(directory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(frame);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = new FileStream(Path.Combine(directory, $"mapping-case-{width}x{height}{suffix}.png"), FileMode.CreateNew);
                    encoder.Save(output);
                }
                Capture("");
                dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
                dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
                foreach (var control in new System.Windows.FrameworkElement[] { dialog.GroupsGrid, dialog.CatalogGrid,
                    dialog.GroupSearch, dialog.CatalogSearch, dialog.StageButton, dialog.SaveButton, dialog.Approver, dialog.Confirm })
                {
                    var bounds = control.TransformToAncestor(frame).TransformBounds(new System.Windows.Rect(control.RenderSize));
                    bounds.Left.Should().BeGreaterThanOrEqualTo(-0.1); bounds.Top.Should().BeGreaterThanOrEqualTo(-0.1);
                    bounds.Right.Should().BeLessThanOrEqualTo(width + 0.1); bounds.Bottom.Should().BeLessThanOrEqualTo(height + 0.1);
                }
                var sourceDetails = Descendants(frame).OfType<System.Windows.Controls.Expander>()
                    .Single(expander => Equals(expander.Header, "פרטי המקור והממצאים"));
                sourceDetails.IsExpanded = true;
                frame.Measure(size); frame.Arrange(new System.Windows.Rect(new System.Windows.Point(), size)); frame.UpdateLayout();
                var scroll = (System.Windows.Controls.ScrollViewer)sourceDetails.Content;
                var details = (System.Windows.Controls.TextBlock)scroll.Content;
                details.Text.Should().Contain("היקף שיוך מלא").And.Contain(@"C:\synthetic\A1.dwg").And.Contain(@"C:\synthetic\B1.dwg");
                Capture("-sources-top");
                // The expanded, scrollable details pane must leave both displayed rows usable.
                // The original collapsed >=150px assertion above remains unchanged.
                dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
                dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
                var detailBounds = sourceDetails.TransformToAncestor(frame)
                    .TransformBounds(new System.Windows.Rect(sourceDetails.RenderSize));
                detailBounds.Left.Should().BeGreaterThanOrEqualTo(0); detailBounds.Top.Should().BeGreaterThanOrEqualTo(0);
                detailBounds.Right.Should().BeLessThanOrEqualTo(width); detailBounds.Bottom.Should().BeLessThanOrEqualTo(height);
                scroll.ScrollToEnd(); frame.UpdateLayout(); Capture("-sources-bottom");
                scroll.VerticalOffset.Should().Be(scroll.ScrollableHeight);
                dialog.IsVisible.Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull(); File.Exists(f.Target).Should().BeFalse();
            }
            finally { dialog.Close(); }
        });

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
