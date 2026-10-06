using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ProjectPriceApprovalUiTests
{
    // Presentation fixture only. No catalog approval, price decision or profile is saved.
    private static QuantityRowViewModel Row(string layer, bool priced = false) => new()
    {
        RuleKey = "layer:" + layer + "|length", Layer = layer, EntityType = "Polyline",
        Method = "polyline-length", ObjectCount = 1, Quantity = 1, Unit = "m",
        CatalogCode = priced ? "TEST.1" : null, Price = priced ? "117.50" : null,
        MappingState = priced ? "מאושר" : "לא ממופה",
    };

    [Fact]
    public void PriceContinuationKeepsRebuiltHwCurbSelectedBeforeAnUnmappedWall()
    {
        RunSta(() =>
        {
            var wall = Row("S_WALL_BT");
            var rebuiltCurb = Row("HW-CURB", priced: true);
            var rows = new[] { wall, rebuiltCurb };
            var before = JsonSerializer.Serialize(rows);
            var grid = new System.Windows.Controls.DataGrid { ItemsSource = rows };
            grid.SelectedItem = EstimateReviewContinuationSelection.Select(rows, null,
                rebuiltCurb.RuleKey, preservePreviousSelection: true);
            grid.SelectedItem.Should().BeSameAs(rebuiltCurb,
                "a successful price edit must not jump to the first unrelated unmapped group");
            rebuiltCurb.PriceDisplay.Should().Be("117.50");
            wall.CatalogCode.Should().BeNull(); wall.Price.Should().BeNull();
            JsonSerializer.Serialize(rows).Should().Be(before, "selection must not edit either row");
            grid.IsLoaded.Should().BeFalse();
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("case-mismatch")]
    [InlineData("historical")]
    [InlineData("ignored")]
    public void PriceContinuationCannotSubstituteAnUnrelatedRowWhenReviewedRowIsUnavailable(string reason)
    {
        var wall = Row("S_WALL_BT");
        var curb = Row("HW-CURB", priced: true);
        if (reason == "historical") curb.HistoricalReason = "TEST ONLY: old scan";
        if (reason == "ignored") curb.IsIgnored = true;
        var rows = reason == "missing" ? new[] { wall } : new[] { wall, curb };
        var before = JsonSerializer.Serialize(rows);
        var key = reason == "case-mismatch" ? curb.RuleKey.ToLowerInvariant() : curb.RuleKey;
        EstimateReviewContinuationSelection.Select(rows, null, key, preservePreviousSelection: true)
            .Should().BeNull();
        JsonSerializer.Serialize(rows).Should().Be(before);
    }

    [Fact]
    public void MappingContinuationStillAdvancesAndPrefersItsUnmappedClosedAlternative()
    {
        var wall = Row("S_WALL_BT");
        var curb = Row("HW-CURB", priced: true);
        var alternative = Row("CURB-ALTERNATIVE");
        var rows = new[] { wall, curb, alternative };
        EstimateReviewContinuationSelection.Select(rows, new[] { curb.RuleKey }, curb.RuleKey)
            .Should().BeSameAs(wall);
        curb.AlternativeRuleKey = alternative.RuleKey;
        EstimateReviewContinuationSelection.Select(rows, new[] { curb.RuleKey }, curb.RuleKey)
            .Should().BeSameAs(alternative);
        alternative.IsIgnored = true;
        EstimateReviewContinuationSelection.Select(rows, new[] { curb.RuleKey }, curb.RuleKey)
            .Should().BeSameAs(wall);
        // Relevance and multi-group mapping retain their former next-unmapped behavior.
        EstimateReviewContinuationSelection.Select(rows, null, curb.RuleKey).Should().BeSameAs(wall);
        EstimateReviewContinuationSelection.Select(rows, new[] { curb.RuleKey, alternative.RuleKey }, curb.RuleKey)
            .Should().BeSameAs(wall);
        wall.CatalogCode = "TEST.OTHER";
        EstimateReviewContinuationSelection.Select(rows, null, curb.RuleKey).Should().BeSameAs(curb);
    }

    [Fact]
    public void ActualDialogStartsWithoutApprovalAndRequiresNamedCompleteDecision()
    {
        RunSta(() =>
        {
            var context = new ProjectPriceApprovalPolicy.Context("fixture", new string('a', 64), "fixture-catalog",
                new string('b', 64), "TEST.1", new string('c', 64), "Synthetic item", "m");
            var dialog = new ProjectPriceApprovalDialog(context, Array.Empty<ProjectProfile.EstimateProfile.PriceOverride>());
            dialog.Approval.Should().BeNull(); dialog.TryApprove().Should().BeFalse();
            Set(dialog, "_price", "1,250"); Set(dialog, "_source", "Q001"); Set(dialog, "_reason", "fixture"); Set(dialog, "_approver", "Explicit Person");
            dialog.TryApprove().Should().BeFalse(); dialog.Approval.Should().BeNull();
            Set(dialog, "_price", "117.50"); Set(dialog, "_approver", "");
            dialog.TryApprove().Should().BeFalse(); dialog.Approval.Should().BeNull();
            Set(dialog, "_approver", "Explicit Person");
            dialog.TryApprove().Should().BeTrue();
            dialog.Approval!.Price.Should().Be(117.50m); dialog.Approval.Context.Should().Be(context);
            dialog.Approval.ApprovedBy.Should().Be("Explicit Person");
            dialog.Approval.ApprovedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
            dialog.Close(); // no Show/ShowDialog/native CAD was used
        });
    }

    [Fact]
    public void ClosingActualDialogWithoutApprovalDoesNotCreateDecision()
    {
        RunSta(() =>
        {
            var context = new ProjectPriceApprovalPolicy.Context("fixture", new string('a', 64), "fixture-catalog",
                new string('b', 64), "TEST.1", new string('c', 64), "Synthetic item", "m");
            var dialog = new ProjectPriceApprovalDialog(context, Array.Empty<ProjectProfile.EstimateProfile.PriceOverride>());
            var curb = Row("HW-CURB", priced: true);
            var rows = new[] { Row("S_WALL_BT"), curb };
            var before = JsonSerializer.Serialize(rows);
            var grid = new System.Windows.Controls.DataGrid { ItemsSource = rows, SelectedItem = curb };
            Set(dialog, "_price", "99"); dialog.Close(); dialog.Approval.Should().BeNull();
            grid.SelectedItem.Should().BeSameAs(curb);
            JsonSerializer.Serialize(rows).Should().Be(before, "cancelling must not publish or reselect");
        });
    }

    [Fact]
    public void ProductionDispatchRechecksOriginalScopeBeforeCasAndUsesTransactionalRebase()
    {
        var root = typeof(ProjectPriceApprovalUiTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        var ui = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.ProjectPrice.cs"));
        var modal = ui.IndexOf("CivilModalHost.ShowFromPalette(dialog)", StringComparison.Ordinal);
        var guard = ui.IndexOf("RequireProfileDecisionScope(scope);", modal, StringComparison.Ordinal);
        var load = ui.IndexOf("_estimate.LoadCatalog(scope.Profile, scan.ProfileSource)", modal, StringComparison.Ordinal);
        var save = ui.IndexOf("ProjectPriceApprovalWriter.Save", StringComparison.Ordinal);
        modal.Should().BeGreaterThan(0); guard.Should().BeGreaterThan(modal); load.Should().BeGreaterThan(guard); save.Should().BeGreaterThan(load);
        ui.Should().Contain("!ReferenceEquals(_scan, scan)").And.Contain("scan.ProfileWriteState")
            .And.Contain("ContinueEstimateReviewAfterDecision(saved, profileForSave, mappedRuleKey: null")
            .And.Contain("previousRuleKey: row.RuleKey, preservePreviousSelection: true")
            .And.NotContain("OnBuildEstimate(");
        var continuation = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
        continuation.Should().Contain("EstimateReviewContinuationSelection.Select(")
            .And.Contain("_quantityRows, mappedRuleKeys, previousRuleKey, preservePreviousSelection)");
        File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.EstimateGuidance.cs"))
            .Should().Contain("OnApproveProjectPrice(selectedRecovery.Value.Row)");
    }

    private static void Set(ProjectPriceApprovalDialog dialog, string name, string value) =>
        ((TextBox)typeof(ProjectPriceApprovalDialog).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!).Text = value;
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue("the headless dialog test must complete");
        if (error != null) throw new InvalidOperationException("Headless WPF approval test failed.", error);
    }
}
