using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Unshown WPF actions, explicit synthetic decisions only; no CAD or live profiles.</summary>
public sealed class ManualMappingBatchDialogTests
{
    internal const string First = "layer:ARBITRARY-ALPHA|length", Second = "layer:UNKNOWN-BETA|length";
    internal const string Code = "TEST.LENGTH", Replacement = "TEST.OTHER";
    internal static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "SYNTHETIC-BATCH-ONLY", FileHash = new string('a', 64),
        Items =
        {
            [Code] = new() { Code = Code, Description = "סעיף אורך לניסוי בלבד", UnitRaw = "m" },
            [Replacement] = new() { Code = Replacement, Description = "סעיף חלופי לניסוי בלבד", UnitRaw = "m" },
        },
        Prices = { [Code] = new() { Code = Code, Price = 12.5m, PriceBookId = "SYNTHETIC-BATCH-ONLY", SourceHash = new string('a', 64) } },
    };
    internal static Dialog.Group Group(string key, string unit = "m", string? alternative = null, string? readOnly = null) =>
        new(key, key.Split(':')[1].Split('|')[0], "LINE", unit == "m2" ? "area" : "length", unit,
            20, 1, null, alternative, alternative, "SYNTHETIC ONLY: source identity A1; quantities are not approved by mapping.",
            Array.Empty<MappingProposal>(), readOnly);
    internal static Dialog.GroupRow Row(Dialog dialog, string key) => dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>()
        .Single(row => row.Subject.RuleKey == key);
    internal static void SelectCode(Dialog dialog, string code = Code)
    {
        dialog.CatalogSearch.Text = code;
        dialog.CatalogGrid.SelectedItem = dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Single(item => item.Code == code);
    }
    internal static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue("batch actions never open a modal or CAD document");
        if (failure != null) throw new InvalidOperationException("Unshown synthetic batch workflow failed.", failure);
    }
    private static void WithDialog(Action<Dialog> action, params Dialog.Group[] groups) => RunSta(() =>
    {
        var catalog = Catalog(); var before = JsonSerializer.Serialize(groups); var catalogBefore = JsonSerializer.Serialize(catalog);
        var dialog = new Dialog(groups, catalog);
        try { action(dialog); JsonSerializer.Serialize(groups).Should().Be(before); JsonSerializer.Serialize(catalog).Should().Be(catalogBefore); }
        finally { dialog.Close(); }
    });

    [Fact]
    public void UnknownLayersWithoutProposalsCanExplicitlyReuseOneItemThenEditOneAndSaveOnce() => RunSta(() =>
    {
        var saves = 0; IReadOnlyList<Dialog.Choice>? written = null;
        var dialog = new Dialog(new[] { Group(First), Group(Second) }, Catalog(), (choices, reviewer) =>
        { saves++; written = choices; reviewer.Should().Be("SYNTHETIC TEST ONLY"); });
        try
        {
            dialog.CatalogGrid.Items.Count.Should().Be(0, "unknown layers need no seeded proposal");
            Row(dialog, First).MarkedForBatch = Row(dialog, Second).MarkedForBatch = true;
            dialog.StageMarkedGroups().Should().BeFalse("marking alone is not a catalog choice");
            SelectCode(dialog); dialog.StageMarkedGroups().Should().BeTrue();
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == Code && !row.MarkedForBatch);
            saves.Should().Be(0); dialog.ApprovedChoices.Should().BeNull(); dialog.TryConfirm().Should().BeFalse();
            dialog.GroupsGrid.SelectedItem = Row(dialog, Second); SelectCode(dialog, Replacement);
            dialog.StageSelection().Should().BeTrue();
            Row(dialog, First).Draft.Should().Be(Code); Row(dialog, Second).Draft.Should().Be(Replacement);
            dialog.Approver.Text = "SYNTHETIC TEST ONLY"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue(); saves.Should().Be(1);
            written.Should().HaveCount(2); written.Should().OnlyContain(choice => choice.ExcludedAlternativeRuleKey == null);
            dialog.IsVisible.Should().BeFalse();
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("m2", null, null)]
    [InlineData("cm", null, null)]
    [InlineData("?", null, null)]
    [InlineData("m", "layer:UNKNOWN-BETA|area", null)]
    [InlineData("m", null, "SYNTHETIC source is read-only")]
    public void OneUnsafeMarkedGroupRejectsEntireBatchWithoutSilentSubset(string unit, string? alternative, string? readOnly) =>
        WithDialog(dialog =>
        {
            Row(dialog, First).MarkedForBatch = Row(dialog, Second).MarkedForBatch = true;
            SelectCode(dialog); dialog.StageMarkedGroups().Should().BeFalse();
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == "—");
            dialog.BatchStatus.Text.Should().NotBeNullOrWhiteSpace(); dialog.TryConfirm().Should().BeFalse();
        }, Group(First), Group(Second, unit, alternative, readOnly));

    [Fact]
    public void HiddenMarkedGroupsAreNotSilentlyIncludedAndFilteringNeverLosesTheirIdentity() => WithDialog(dialog =>
    {
        var second = Row(dialog, Second);
        Row(dialog, First).MarkedForBatch = second.MarkedForBatch = true;
        dialog.GroupSearch.Text = "ALPHA"; dialog.GroupsGrid.SelectedItem = Row(dialog, First); SelectCode(dialog);
        dialog.StageMarkedGroups().Should().BeFalse(); dialog.BatchStatus.Text.Should().Contain("מוסתרות");
        second.MarkedForBatch.Should().BeTrue(); second.Draft.Should().Be("—");
        dialog.GroupSearch.Text = ""; dialog.GroupsGrid.SelectedItem = Row(dialog, First); SelectCode(dialog);
        dialog.StageMarkedGroups().Should().BeTrue(); Row(dialog, Second).Draft.Should().Be(Code);
    }, Group(First), Group(Second));

    [Fact]
    public void PendingMarksInvalidatePriorConfirmationAndCancelNeverInvokesSave() => RunSta(() =>
    {
        var saves = 0; var dialog = new Dialog(new[] { Group(First), Group(Second) }, Catalog(), (_, _) => saves++);
        try
        {
            SelectCode(dialog); dialog.StageSelection().Should().BeTrue();
            dialog.Approver.Text = "SYNTHETIC TEST ONLY"; dialog.Confirm.IsChecked = true;
            Row(dialog, Second).MarkedForBatch = true;
            dialog.Confirm.IsChecked.Should().BeFalse(); dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeFalse("marked but unstaged groups must not be mistaken for saved choices");
            dialog.ClearBatchMarks(); dialog.Confirm.IsChecked = true;
            dialog.SaveButton.IsEnabled.Should().BeTrue();
        }
        finally { dialog.Close(); }
        saves.Should().Be(0);
    });

    [Fact]
    public void SaveRefusalRetainsBatchForCorrectionAndRetry() => RunSta(() =>
    {
        var attempts = 0; var dialog = new Dialog(new[] { Group(First), Group(Second) }, Catalog(), (_, _) =>
        { if (++attempts == 1) throw new InvalidOperationException("SYNTHETIC stale source refusal"); });
        try
        {
            Row(dialog, First).MarkedForBatch = Row(dialog, Second).MarkedForBatch = true;
            SelectCode(dialog); dialog.StageMarkedGroups().Should().BeTrue();
            dialog.Approver.Text = "SYNTHETIC TEST ONLY"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull();
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == Code);
            dialog.TryConfirm().Should().BeTrue(); attempts.Should().Be(2);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(1180, 780)]
    [InlineData(900, 620)]
    public void BatchActionsAndReadableGridsFitUnshownMinimumClient(double width, double height) => WithDialog(dialog =>
    {
        Row(dialog, First).MarkedForBatch = Row(dialog, Second).MarkedForBatch = true;
        SelectCode(dialog);
        var frame = UnshownDialogRender.Attach(dialog, (int)width, (int)height);
        UnshownDialogRender.Save(frame, "MHD_MAPPING_REVIEW_RENDER_DIR", $"batch-active-{width}x{height}");
        dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
        dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
        foreach (var control in new FrameworkElement[] { dialog.BatchStageButton, dialog.BatchClearButton, dialog.BatchStatus, dialog.SaveButton })
            UnshownDialogRender.AssertWithin(control, frame);
        dialog.BatchStageButton.IsEnabled.Should().BeTrue(); dialog.SaveButton.IsEnabled.Should().BeFalse();
        dialog.IsVisible.Should().BeFalse();
    }, Group(First), Group(Second));

    [Fact]
    public void ActiveBatchUnitRefusalRemainsVisibleAtMinimumClientWithoutStagingSubset() => WithDialog(dialog =>
    {
        Row(dialog, First).MarkedForBatch = Row(dialog, Second).MarkedForBatch = true;
        SelectCode(dialog);
        var frame = UnshownDialogRender.Attach(dialog, 900, 620);
        UnshownDialogRender.Save(frame, "MHD_MAPPING_REVIEW_RENDER_DIR", "batch-unit-refusal-900x620");
        UnshownDialogRender.AssertWithin(dialog.BatchStatus, frame);
        UnshownDialogRender.AssertWithin(dialog.BatchClearButton, frame);
        dialog.BatchStatus.Text.Should().Contain("היחידות"); dialog.BatchStageButton.IsEnabled.Should().BeFalse();
        dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
        dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == "—");
        dialog.ApprovedChoices.Should().BeNull(); dialog.IsVisible.Should().BeFalse();
    }, Group(First), Group(Second, "m2"));
}
