using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Compiled, unshown WPF; synthetic choices only, without profile writes, network or Civil.</summary>
public sealed class ManualMappingAdvanceDialogTests
{
    private const string CodeA = "TEST.001", CodeB = "TEST.002";
    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "ADVANCE-FIXTURE-ONLY", FileHash = new string('a', 64),
        Items =
        {
            [CodeA] = new() { Code = CodeA, Description = "אבן שפה לבדיקה בלבד", UnitRaw = "m" },
            [CodeB] = new() { Code = CodeB, Description = "אבן שפה חלופית לבדיקה בלבד", UnitRaw = "m" },
        },
    };

    private static Dialog.Group Group(string key, string? current = null, string? alternative = null,
        string? readOnly = null) => new(key, key, "LWPOLYLINE", "length", "m", 12.5, 2,
        current, alternative, alternative, "דוגמה סינתטית בלבד; שיוך אינו אישור מדידה או מחיר.",
        new[] { new MappingProposal { RuleKey = key, MeasurementKind = "length", MeasuredUnit = "m", ProposedCode = CodeA, Score = 90 },
            new MappingProposal { RuleKey = key, MeasurementKind = "length", MeasuredUnit = "m", ProposedCode = CodeB, Score = 80 } }, readOnly);

    private static Dialog.GroupRow Row(Dialog dialog, string key) =>
        dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Single(row => row.Subject.RuleKey == key);

    private static void Select(Dialog dialog, string key, string code = CodeA)
    {
        dialog.GroupsGrid.SelectedItem = Row(dialog, key);
        dialog.CatalogGrid.SelectedItem = dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Single(item => item.Code == code);
    }

    private static void ClickStage(Dialog dialog)
    {
        dialog.StageButton.IsEnabled.Should().BeTrue();
        dialog.StageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [Fact]
    public void ActualAddAndNextSkipsApprovedFirstGroupAndNeverSavesImplicitly() => Sta(() =>
    {
        var saves = 0;
        var dialog = new Dialog(new[] { Group("approved-first", CodeA), Group("later-unmapped"), Group("next-unmapped") },
            Catalog(), (_, _) => saves++, initialRuleKey: "later-unmapped");
        try
        {
            Select(dialog, "later-unmapped", CodeB); ClickStage(dialog);
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "next-unmapped"));
            Row(dialog, "approved-first").Draft.Should().Be("—");
            Row(dialog, "later-unmapped").Draft.Should().Be(CodeB);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            dialog.ApprovedChoices.Should().BeNull(); saves.Should().Be(0);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ExistingMappingRemainsExplicitlyEditableAndVisibleSeparatelyFromItsDraft() => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("approved", CodeA) }, Catalog());
        try
        {
            var row = Row(dialog, "approved");
            row.MappingDisplay.Should().Contain("קיים:").And.Contain(CodeA).And.NotContain("לשמירה:");
            var changed = new List<string?>(); row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            Select(dialog, "approved", CodeB); ClickStage(dialog);
            row.CurrentCode.Should().Be(CodeA); row.Draft.Should().Be(CodeB);
            row.MappingDisplay.Should().Contain("קיים:").And.Contain(CodeA).And.Contain("לשמירה:").And.Contain(CodeB);
            changed.Should().Contain(nameof(Dialog.GroupRow.MappingDisplay));
            dialog.GroupsGrid.SelectedItem.Should().BeNull("all groups are already mapped; no unrelated group is substituted");
            dialog.ApprovedChoices.Should().BeNull();
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue();
            dialog.ApprovedChoices.Should().ContainSingle().Which.CatalogCode.Should().Be(CodeB);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoEligibleUnresolvedGroupClearsSelectionWithoutRestagingOrApproving(bool allResolved) => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("approved", CodeA),
            allResolved ? Group("other-approved", CodeB) : Group("held", readOnly: "חסימת מקור לבדיקה"),
            Group("explicit", allResolved ? CodeA : null) }, Catalog(), initialRuleKey: "explicit");
        try
        {
            Select(dialog, "explicit", CodeB); ClickStage(dialog);
            dialog.GroupsGrid.SelectedItem.Should().BeNull();
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Count(row => row.Draft != "—").Should().Be(1);
            dialog.CatalogGrid.Items.Count.Should().Be(0); dialog.ApprovedChoices.Should().BeNull();
            dialog.SaveButton.IsEnabled.Should().BeFalse();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void AdvanceSkipsStagedAndExcludedAlternativesButLeavesOtherAlternativeExplicit() => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("staged"), Group("area", alternative: "perimeter"),
            Group("perimeter", alternative: "area"), Group("held", readOnly: "מקור חסום"),
            Group("next-pair", alternative: "other-pair"), Group("other-pair", alternative: "next-pair") }, Catalog());
        try
        {
            Select(dialog, "staged"); dialog.StageSelection().Should().BeTrue();
            Select(dialog, "area"); dialog.ConfirmAlternative.IsChecked = true; ClickStage(dialog);
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "next-pair"));
            Row(dialog, "perimeter").Draft.Should().Be("—");
            dialog.ConfirmAlternative.IsChecked.Should().NotBe(true);
            dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageButton.IsEnabled.Should().BeFalse("automatic navigation does not approve the next measurement alternative");
            dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ActualAdvancePreservesIndependentSemanticAndMappingDraftsAcrossRoundTrip() => Sta(() =>
    {
        var calls = 0;
        var dialog = new Dialog(new[] { Group("approved", CodeA), Group("A"), Group("B") }, Catalog(),
            suggestWithAi: (_, _, _) => { calls++; return Task.FromResult(new SemanticMappingAssistResult(
                Array.Empty<MappingProposal>(), "not requested", false)); }, initialRuleKey: "A");
        try
        {
            Select(dialog, "A", CodeB); dialog.SemanticRole.SelectedValue = "curb"; dialog.AiContext.Text = "אבני שפה";
            ClickStage(dialog);
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "B"));
            dialog.SemanticRole.SelectedValue = "water"; dialog.AiContext.Text = "קו מים";
            Select(dialog, "B"); dialog.StageSelection().Should().BeTrue();
            Select(dialog, "A");
            dialog.SemanticRole.SelectedValue.Should().Be("curb"); dialog.AiContext.Text.Should().Be("אבני שפה");
            Row(dialog, "A").Draft.Should().Be(CodeB);
            Select(dialog, "B");
            dialog.SemanticRole.SelectedValue.Should().Be("water"); dialog.AiContext.Text.Should().Be("קו מים");
            Row(dialog, "B").Draft.Should().Be(CodeA); calls.Should().Be(0);
            dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ThousandGroupAdvanceFindsOnlyEligibleUnmappedGroupWithoutChangingOthers() => Sta(() =>
    {
        var groups = Enumerable.Range(0, 1031).Select(index => Group("group-" + index,
            index is 800 or 1030 ? null : CodeA)).ToArray();
        var dialog = new Dialog(groups, Catalog(), initialRuleKey: "group-800");
        try
        {
            Select(dialog, "group-800", CodeB); ClickStage(dialog);
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "group-1030"));
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Count(row => row.Draft != "—").Should().Be(1);
            dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(870, 620)]
    [InlineData(1180, 780)]
    public void ExistingAndStagedMappingsRenderReadablyAtBothSizes(int width, int height) => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("קיים בלבד", CodeA), Group("שינוי מפורש", CodeA),
            Group("בחירה חדשה"), Group("טרם טופל") }, Catalog(),
            suggestWithAi: (_, _, _) => throw new InvalidOperationException("No API call expected"),
            saveSemanticHint: (_, _, _) => { });
        try
        {
            Select(dialog, "שינוי מפורש", CodeB); dialog.StageSelection().Should().BeTrue();
            Select(dialog, "בחירה חדשה", CodeB); dialog.StageSelection().Should().BeTrue();
            Select(dialog, "טרם טופל"); dialog.Approver.Text = "בודק לדוגמה בלבד";
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new System.Windows.Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
            dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
            dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
            foreach (var element in new FrameworkElement[] { dialog.StageButton, dialog.SaveButton, dialog.ReviewerLabel })
            {
                var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                bounds.Left.Should().BeGreaterThanOrEqualTo(0); bounds.Right.Should().BeLessThanOrEqualTo(content.ActualWidth + .5);
                bounds.Top.Should().BeGreaterThanOrEqualTo(0); bounds.Bottom.Should().BeLessThanOrEqualTo(content.ActualHeight + .5);
            }
            var mappingColumn = dialog.GroupsGrid.Columns.OfType<DataGridTextColumn>().Single(column => (string)column.Header == "שיוך ובחירה");
            ((System.Windows.Data.Binding)mappingColumn.Binding).Path.Path.Should().Be(nameof(Dialog.GroupRow.MappingDisplay));
            foreach (var row in dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>())
            {
                var cell = mappingColumn.GetCellContent(row);
                cell.Should().NotBeNull(); ((TextBlock)cell).Text.Should().Be(row.MappingDisplay);
            }
            var directory = Environment.GetEnvironmentVariable("MHD_MAPPING_ADVANCE_RENDER_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(dialog.Background, null, new Rect(0, 0, width, height));
                image.Render(background); image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new FileStream(Path.Combine(directory, $"mapping-advance-{width}x{height}-{Guid.NewGuid():N}.png"), FileMode.CreateNew);
                encoder.Save(stream);
            }
            dialog.IsVisible.Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(900, 620)]
    [InlineData(1180, 780)]
    public void FullBatchRefusalRemainsReachableWithoutDisplacingTableOrActions(int width, int height) => Sta(() =>
    {
        var first = ManualMappingBatchDialogTests.First; var second = ManualMappingBatchDialogTests.Second;
        var dialog = new Dialog(new[] { ManualMappingBatchDialogTests.Group(first),
            ManualMappingBatchDialogTests.Group(second, "m2") }, ManualMappingBatchDialogTests.Catalog(),
            suggestWithAi: (_, _, _) => throw new InvalidOperationException("No API call expected"),
            saveSemanticHint: (_, _, _) => { });
        try
        {
            Row(dialog, first).MarkedForBatch = Row(dialog, second).MarkedForBatch = true;
            ManualMappingBatchDialogTests.SelectCode(dialog);
            var frame = UnshownDialogRender.Attach(dialog, width, height);
            UnshownDialogRender.Save(frame, "MHD_MAPPING_ADVANCE_RENDER_DIR", $"batch-refusal-top-{width}x{height}");
            dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
            dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
            var scroll = dialog.BatchStatus.Parent as ScrollViewer;
            scroll.Should().NotBeNull(); scroll!.ActualHeight.Should().BeLessThanOrEqualTo(40);
            dialog.BatchStatus.Text.Should().Contain("היחידות אינן תואמות בכל הקבוצות; לא נוספה אף בחירה.");
            UnshownDialogRender.AssertWithin(scroll, frame);
            UnshownDialogRender.AssertWithin(dialog.ChosenOnly, frame);
            UnshownDialogRender.AssertWithin(dialog.BatchStageButton, frame);
            UnshownDialogRender.AssertWithin(dialog.BatchClearButton, frame);
            dialog.BatchClearButton.IsEnabled.Should().BeTrue(); dialog.BatchStageButton.IsEnabled.Should().BeFalse();
            scroll.ScrollToBottom(); frame.UpdateLayout();
            if (scroll.ExtentHeight > scroll.ViewportHeight) scroll.VerticalOffset.Should().BeGreaterThan(0);
            UnshownDialogRender.Save(frame, "MHD_MAPPING_ADVANCE_RENDER_DIR", $"batch-refusal-bottom-{width}x{height}");
            dialog.StageMarkedGroups().Should().BeFalse();
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == "—");
            dialog.ApprovedChoices.Should().BeNull(); dialog.IsVisible.Should().BeFalse();
        }
        finally { dialog.Close(); }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("unshown WPF tests must not start a modal loop");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
