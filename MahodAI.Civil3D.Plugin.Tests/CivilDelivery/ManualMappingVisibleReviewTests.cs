using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Binding = System.Windows.Data.Binding;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Unshown WPF only; synthetic input, no Civil, profile mutation or network.</summary>
public sealed class ManualMappingVisibleReviewTests
{
    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "VISIBLE-REVIEW-SYNTHETIC", FileHash = new string('a', 64),
        Items =
        {
            ["TEST.WATER"] = new() { Code = "TEST.WATER", Description = "צינור מים לבדיקה בלבד", UnitRaw = "m" },
            ["TEST.POWER"] = new() { Code = "TEST.POWER", Description = "צינור חשמל לבדיקה בלבד", UnitRaw = "m" },
        },
    };
    private static Dialog.Group Group(string key, string? code = "TEST.WATER", string unit = "m",
        string? current = null, string? readOnly = null, string? alternative = null) => new(
        key, key, "LINE", unit == "m2" ? "area" : "length", unit, 15, 3, current, alternative,
        alternative, "SYNTHETIC: mapping does not approve measurement or price.", code == null ?
            Array.Empty<MappingProposal>() : new[] { new MappingProposal
            {
                RuleKey = key, MeasurementKind = "length", MeasuredUnit = unit, ProposedCode = code, Score = 999,
                Reasons = { "בדיקת משפחה בלבד — אינה החלטה הנדסית" },
            } }, readOnly);
    private static Dialog.GroupRow[] Rows(Dialog dialog) => dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().ToArray();
    private static void SelectCatalog(Dialog dialog)
    {
        dialog.GroupsGrid.SelectedItem = Rows(dialog).First(); dialog.CatalogSearch.Text = "TEST.WATER";
        dialog.CatalogGrid.SelectedItem = dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Single();
    }
    private static void With(Action<Dialog> action, params Dialog.Group[] groups) => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new Dialog(groups, Catalog());
        try { action(dialog); } finally { dialog.Close(); }
    });

    [Fact]
    public void HebrewSearchFindsEnglishLayerByCatalogProposalWithoutApprovingOrCallingAi() =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var calls = 0; var saves = 0;
            var dialog = new Dialog(new[] { Group("WATER-LINE"), Group("POWER-LINE", "TEST.POWER") }, Catalog(),
                (_, _) => saves++, suggestWithAi: (_, _, _) =>
                { calls++; return Task.FromResult(new SemanticMappingAssistResult(Array.Empty<MappingProposal>(), "unused", false)); });
            try
            {
                Rows(dialog)[0].Overview.Should().Contain("TEST.WATER").And.Contain("צינור מים");
                Rows(dialog)[0].OverviewDetail.Should().Contain("הצעות בלבד");
                dialog.GroupsGrid.Columns.Should().Contain(column => (string)column.Header == "שכבה והצעה לבדיקה");
                dialog.GroupSearch.Text = "מים"; Rows(dialog).Should().ContainSingle().Which.Layer.Should().Be("WATER-LINE");
                dialog.GroupSearch.Text = "חשמל"; Rows(dialog).Should().ContainSingle().Which.Layer.Should().Be("POWER-LINE");
                Rows(dialog).Should().OnlyContain(row => !row.MarkedForBatch && row.Draft == "—");
                calls.Should().Be(0); saves.Should().Be(0); dialog.ApprovedChoices.Should().BeNull();
            }
            finally { dialog.Close(); }
        });

    [Fact]
    public void MeasurementAndStateFiltersAreDisplayOnlyAndIncludeUnknownGroups() => With(dialog =>
    {
        dialog.MeasurementFilter.SelectedValue = "area"; Rows(dialog).Should().ContainSingle().Which.Layer.Should().Be("AREA");
        dialog.MeasurementFilter.SelectedValue = "all"; dialog.GroupStateFilter.SelectedValue = "mapped";
        Rows(dialog).Should().ContainSingle().Which.Layer.Should().Be("MAPPED");
        dialog.GroupStateFilter.SelectedValue = "pending"; Rows(dialog).Select(row => row.Layer).Should().BeEquivalentTo("OPEN", "AREA");
        dialog.GroupStateFilter.SelectedValue = "readonly"; Rows(dialog).Should().ContainSingle().Which.Layer.Should().Be("HELD");
        dialog.GroupStateFilter.SelectedValue = "no-proposal"; Rows(dialog).Select(row => row.Layer).Should().Contain("AREA");
        dialog.ApprovedChoices.Should().BeNull(); dialog.SaveButton.IsEnabled.Should().BeFalse();
    }, Group("OPEN"), Group("AREA", null, "m2"), Group("MAPPED", current: "TEST.POWER"), Group("HELD", readOnly: "source held"));

    [Fact]
    public void ExplicitVisibleSelectionReplacesHiddenMarksAndSkipsMappedReadonlyAlternativeAndWrongUnit() => With(dialog =>
    {
        var hidden = Rows(dialog).Single(row => row.Layer == "HIDDEN"); hidden.MarkedForBatch = true;
        dialog.GroupSearch.Text = "VISIBLE"; SelectCatalog(dialog);
        dialog.SelectVisibleEligibleGroups().Should().Be(2);
        hidden.MarkedForBatch.Should().BeFalse(); hidden.Draft.Should().Be("—");
        Rows(dialog).Where(row => row.MarkedForBatch).Select(row => row.Layer).Should().BeEquivalentTo("VISIBLE-A", "VISIBLE-B");
        dialog.BatchStatus.Text.Should().Contain("2").And.Contain("6");
        dialog.ApprovedChoices.Should().BeNull(); dialog.StageMarkedGroups().Should().BeTrue();
        Rows(dialog).Where(row => row.Draft != "—").Should().HaveCount(2);
        dialog.SelectVisibleEligibleGroups().Should().Be(0, "staged choices are not silently replaced");
    }, Group("VISIBLE-A"), Group("VISIBLE-B"), Group("HIDDEN"), Group("VISIBLE-MAPPED", current: "TEST.POWER"),
        Group("VISIBLE-HELD", readOnly: "source held"), Group("VISIBLE-ALTERNATIVE", alternative: "OTHER"), Group("VISIBLE-AREA", unit: "m2"));

    [Fact]
    public void FilterAfterMarkingStillRefusesHiddenBatchUntilExplicitReselection() => With(dialog =>
    {
        SelectCatalog(dialog); dialog.SelectVisibleEligibleGroups().Should().Be(2);
        dialog.GroupSearch.Text = "FIRST"; SelectCatalog(dialog);
        dialog.StageMarkedGroups().Should().BeFalse(); dialog.BatchStatus.Text.Should().Contain("מוסתרות");
        dialog.SelectVisibleEligibleGroups().Should().Be(1); dialog.StageMarkedGroups().Should().BeTrue();
        dialog.GroupSearch.Text = ""; Rows(dialog).Single(row => row.Layer == "SECOND").Draft.Should().Be("—");
    }, Group("FIRST"), Group("SECOND"));

    [Fact]
    public void CatalogQuerySurvivesNavigationButCatalogSelectionDoesNot() => With(dialog =>
    {
        SelectCatalog(dialog); dialog.GroupsGrid.SelectedItem = Rows(dialog).Last();
        dialog.CatalogSearch.Text.Should().Be("TEST.WATER"); dialog.CatalogGrid.SelectedItem.Should().BeNull();
        dialog.StageButton.IsEnabled.Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull();
    }, Group("FIRST"), Group("SECOND"));

    [Fact]
    public void SameUnitAloneNeverMarksUnrelatedOrUnknownGroupsButManualReuseRemainsExplicit() => With(dialog =>
    {
        SelectCatalog(dialog); dialog.SelectVisibleEligibleGroups().Should().Be(1);
        Rows(dialog).Where(row => row.MarkedForBatch).Should().ContainSingle().Which.Layer.Should().Be("WATER");
        Rows(dialog).Single(row => row.Layer == "POWER").MarkedForBatch.Should().BeFalse();
        var unknown = Rows(dialog).Single(row => row.Layer == "UNKNOWN");
        unknown.MarkedForBatch.Should().BeFalse();
        // An engineer can still explicitly mark a verified no-proposal group;
        // automatic selection must not pretend that a shared unit proves meaning.
        unknown.MarkedForBatch = true; dialog.StageMarkedGroups().Should().BeTrue();
        unknown.Draft.Should().Be("TEST.WATER");
        Rows(dialog).Single(row => row.Layer == "POWER").Draft.Should().Be("—");
    }, Group("WATER"), Group("POWER", "TEST.POWER"), Group("UNKNOWN", null));

    [Fact]
    public void MultipleProposalsRemainExplicitAndCompleteInOverviewTooltip() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var first = Group("ENGLISH-LAYER");
        var second = Group("ENGLISH-LAYER", "TEST.POWER");
        var group = first with { Proposals = first.Proposals.Concat(second.Proposals).ToArray() };
        var dialog = new Dialog(new[] { group }, Catalog());
        try
        {
            var row = Rows(dialog).Single(); row.Overview.Should().Contain("מתוך 2");
            row.OverviewDetail.Should().Contain("TEST.WATER").And.Contain("צינור מים").And.Contain("TEST.POWER").And.Contain("צינור חשמל");
            row.MarkedForBatch.Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void LongOverviewUsesThreeBoundedEllipsisLinesWithCompleteTooltip() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var catalog = Catalog();
        catalog.Items["TEST.WATER"] = new CatalogItem { Code = "TEST.WATER", UnitRaw = "m",
            Description = string.Join(" ", Enumerable.Repeat("תיאור מים ארוך לבדיקה", 30)) };
        var original = Group("LAYER-" + new string('X', 150));
        original.Proposals[0].Reasons.Add("FULL-REASON-" + new string('Y', 200));
        var dialog = new Dialog(new[] { original, Group("SECOND") }, catalog);
        try
        {
            var row = Rows(dialog).First();
            row.Overview.Split('\n').Should().HaveCount(3);
            row.Overview.Split('\n')[1].Should().Be("TEST.WATER");
            row.OverviewDetail.Should().Contain(original.Layer).And.Contain(catalog.Items["TEST.WATER"].Description)
                .And.Contain("FULL-REASON-");
            var column = dialog.GroupsGrid.Columns.OfType<DataGridTextColumn>()
                .Single(column => column.Binding is Binding binding && binding.Path.Path == nameof(Dialog.GroupRow.Overview));
            var frame = UnshownDialogRender.Attach(dialog, 900, 620);
            var block = column.GetCellContent(row) as TextBlock;
            block.Should().NotBeNull("the actual rendered overview cell must exist");
            System.Windows.Data.BindingOperations.GetBindingExpression(block!, FrameworkElement.ToolTipProperty)!
                .UpdateTarget();
            block!.TextWrapping.Should().Be(TextWrapping.NoWrap);
            block.TextTrimming.Should().Be(TextTrimming.CharacterEllipsis);
            block.MaxHeight.Should().Be(51);
            block.DesiredSize.Height.Should().BeLessThanOrEqualTo(61, "three 17px lines plus margins must not create a seven-line row");
            block.ToolTip.Should().Be(row.OverviewDetail);
            UnshownDialogRender.Save(frame, "MHD_MAPPING_REVIEW_RENDER_DIR", "preview-long-900x620");
            dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ManyVisibleGroupsStageAndSaveOnceOnlyAfterExplicitConfirmation() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var saves = 0; var saved = 0;
        var dialog = new Dialog(Enumerable.Range(0, 100).Select(index => Group($"LINE-{index:D3}")).ToArray(), Catalog(),
            (choices, _) => { saves++; saved = choices.Count; });
        try
        {
            Rows(dialog).Should().OnlyContain(row => !row.MarkedForBatch);
            SelectCatalog(dialog); dialog.SelectVisibleEligibleGroups().Should().Be(100);
            saves.Should().Be(0); dialog.TryConfirm().Should().BeFalse();
            dialog.StageMarkedGroups().Should().BeTrue(); saves.Should().Be(0);
            dialog.ChosenOnly.IsChecked = true; Rows(dialog).Should().HaveCount(100);
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue(); saves.Should().Be(1); saved.Should().Be(100);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(1180, 780)]
    [InlineData(900, 620)]
    public void VisibleBatchFiltersAndProposalTextRemainReachableInUnshownLayout(double width, double height) => With(dialog =>
    {
        SelectCatalog(dialog); dialog.SelectVisibleEligibleGroups();
        var frame = UnshownDialogRender.Attach(dialog, (int)width, (int)height);
        UnshownDialogRender.Save(frame, "MHD_MAPPING_REVIEW_RENDER_DIR", $"visible-review-{width}x{height}");
        foreach (var control in new FrameworkElement[] { dialog.GroupStateFilter, dialog.MeasurementFilter,
                     dialog.BatchSelectVisibleButton, dialog.BatchStageButton, dialog.SaveButton, dialog.BatchStatus })
            UnshownDialogRender.AssertWithin(control, frame);
        dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
        dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
        dialog.IsVisible.Should().BeFalse();
    }, Group("WATER-LINE-EXAMPLE"), Group("POWER-LINE", "TEST.POWER"));
}
