using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Actual XAML/source wiring plus unshown compiled WPF picker interaction; not native CAD acceptance.</summary>
public sealed class EstimateRowActionsUiTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Source(string name)
    {
        var root = typeof(EstimateRowActionsUiTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", name));
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("BtnApprove")]
    [InlineData("BtnProjectPrice")]
    [InlineData("BtnShowQuantity")]
    [InlineData("BtnBuild")]
    [InlineData("BtnExport")]
    [InlineData("BtnExportPricedDraft")]
    [InlineData("BtnReviewMappings")]
    [InlineData("BtnFilterDrawingNoise")]
    [InlineData("BtnApproveEstimateScope")]
    public void PrimaryEstimateActionsAreNotHiddenInsideCollapsedExpanders(string name)
    {
        var button = Named(XDocument.Parse(Source("CivilDeliveryControl.xaml")), name);
        button.Name.LocalName.Should().Be("Button");
        button.Ancestors().Where(element => element.Name.LocalName == "Expander")
            .Should().NotContain(element => (string?)element.Attribute("IsExpanded") != "True",
                "an expander is collapsed by default and must not conceal a primary action");
        button.AncestorsAndSelf().Should().NotContain(element =>
            (string?)element.Attribute("Visibility") == "Collapsed" ||
            (string?)element.Attribute("Visibility") == "Hidden");
    }

    [Fact]
    public void ExpandedDetailsKeepBoundedSpaceAndCloseTheOtherDetailsPanel()
    {
        var document = XDocument.Parse(Source("CivilDeliveryControl.xaml"));
        var sources = Named(document, "EstimateAdvancedActions");
        var details = Named(document, "EstimateQuantityDetails");
        sources.Attribute("Expanded")!.Value.Should().Be("OnEstimateSourcesExpanded");
        details.Attribute("Expanded")!.Value.Should().Be("OnEstimateDetailsExpanded");
        foreach (var panel in new[] { sources, details })
            panel.Elements().Single(element => element.Name.LocalName == "ScrollViewer")
                .Attribute("MaxHeight")!.Value.Should().Be("110");
        var actions = Source("CivilDeliveryControl.EstimateRowActions.cs");
        actions.Should().Contain("EstimateQuantityDetails.IsExpanded = false")
            .And.Contain("EstimateAdvancedActions.IsExpanded = false");
    }

    [Fact]
    public void ManualBatchButtonKeepsOriginalContextAndOneGuardedSave()
    {
        Named(XDocument.Parse(Source("CivilDeliveryControl.xaml")), "BtnReviewMappings")
            .Attribute("Click")!.Value.Should().Be("OnReviewMappings");
        var handler = Source("CivilDeliveryControl.MappingReview.cs");
        var modal = handler.IndexOf("CivilModalHost.ShowFromPalette(dialog)", StringComparison.Ordinal);
        var scope = handler.IndexOf("CaptureProfileDecisionScope", StringComparison.Ordinal);
        var save = handler.IndexOf("_estimate.SaveReviewedMappings(", StringComparison.Ordinal);
        scope.Should().BeInRange(0, modal);
        save.Should().BeGreaterThan(modal);
        handler[modal..save].Should().Contain("RequireProfileDecisionScope(scope)")
            .And.Contain("ReferenceEquals(_scan, scan)")
            .And.Contain("RequireFreshForDecision")
            .And.Contain("CatalogIdentity.ItemFingerprint(item)");
        handler.Should().Contain("scan.ProfileWriteState")
            .And.Contain("ContinueEstimateReviewAfterDecision")
            .And.NotContain("OnScan(");
        Source("CivilDeliveryControl.EstimateGuidance.cs").Should()
            .Contain("BtnReviewMappings.IsEnabled && _quantityRows.Count(NeedsDecision) > 1")
            .And.Contain("OnReviewMappings(sender, e)");
    }

    [Fact]
    public void DirectPriceButtonDispatchesToExistingGuardedActionWithoutBuildPrerequisite()
    {
        var document = XDocument.Parse(Source("CivilDeliveryControl.xaml"));
        Named(document, "BtnProjectPrice").Attribute("Click")!.Value.Should().Be("OnEditSelectedProjectPrice");
        var actions = Source("CivilDeliveryControl.EstimateRowActions.cs");
        var handlerStart = actions.IndexOf("private void OnEditSelectedProjectPrice", StringComparison.Ordinal);
        handlerStart.Should().BeGreaterThan(0);
        var handler = actions[handlerStart..];
        var refresh = handler.IndexOf("RefreshGates();", StringComparison.Ordinal);
        var enabled = handler.IndexOf("if (BtnProjectPrice.IsEnabled", StringComparison.Ordinal);
        var dispatch = handler.IndexOf("OnApproveProjectPrice(row);", StringComparison.Ordinal);
        refresh.Should().BeGreaterThan(0); enabled.Should().BeGreaterThan(refresh); dispatch.Should().BeGreaterThan(enabled);
        actions.Should().Contain("HasApprovedCatalogMapping(_profile, record)")
            .And.Contain("ProjectPriceApprovalPolicy.CaptureForRecords(")
            .And.Contain("_quantityRows.Contains(row)")
            .And.Contain("!row.IsHistorical")
            .And.NotContain("_estimateResult")
            .And.NotContain("OnBuildEstimate(")
            .And.NotContain("PriceStatus.MissingPrice");

        var approval = Source("CivilDeliveryControl.ProjectPrice.cs");
        var modal = approval.IndexOf("CivilModalHost.ShowFromPalette(dialog)", StringComparison.Ordinal);
        approval.IndexOf("HasApprovedCatalogMapping(scope.Profile, record)", StringComparison.Ordinal).Should().BeInRange(0, modal);
        approval.IndexOf("ProjectPriceApprovalPolicy.CaptureForRecords(", StringComparison.Ordinal).Should().BeInRange(0, modal);
        approval.Should().Contain("RequireFreshForDecision")
            .And.Contain("RequireProfileDecisionScope(scope)")
            .And.Contain("ProjectPriceApprovalPolicy.RequireUnchanged")
            .And.Contain("scan.ProfileWriteState")
            .And.Contain("ProjectPriceApprovalWriter.Save")
            .And.NotContain("OnBuildEstimate(");
    }

    [Fact]
    public void CurrentRowActionsKeepFreshnessAndPreviousPriceDecisionVisible()
    {
        var document = XDocument.Parse(Source("CivilDeliveryControl.xaml"));
        Named(document, "EstimateRowTitle").Should().NotBeNull();
        Named(document, "EstimateRowActionHint").Should().NotBeNull();
        var actions = Source("CivilDeliveryControl.EstimateRowActions.cs");
        actions.Should().Contain("!savePending && scanFresh")
            .And.Contain("row.IsIgnored")
            .And.Contain("ProjectOverrides.Any")
            .And.Contain("ערוך מחיר פרויקט")
            .And.Contain("הסריקה אינה עדכנית")
            .And.Contain("דבר לא אושר אוטומטית");
    }

    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "PICKER-FIXTURE-ONLY", FileHash = new string('a', 64),
        Items =
        {
            ["TEST.LENGTH"] = new() { Code = "TEST.LENGTH", Description = "אבן שפה לבדיקה בלבד", UnitRaw = "m" },
            ["TEST.AREA"] = new() { Code = "TEST.AREA", Description = "ריצוף לבדיקה בלבד", UnitRaw = "m2" },
        },
    };

    private static CatalogPickerDialog Picker(CatalogSnapshot catalog) => new(
        "layer:שם-מותאם-ללא-תקן|length", "שם-מותאם-ללא-תקן", "length", "m", 12.5, 2,
        catalog, Array.Empty<MappingProposal>());

    [Fact]
    public void ActualPickerSearchesCustomLayerWithoutProposalAndAllowsMissingPriceMappingSelection()
    {
        RunSta(() =>
        {
            var catalog = Catalog(); var dialog = Picker(catalog);
            try
            {
                var grid = (DataGrid)dialog.FindName("Grid");
                var search = (TextBox)dialog.FindName("SearchBox");
                var approve = (Button)dialog.FindName("BtnOk");
                ((TextBlock)dialog.FindName("SubjectTitle")).Text.Should().Contain("שם-מותאם-ללא-תקן");
                grid.Items.Count.Should().Be(0); grid.SelectedItem.Should().BeNull();
                approve.IsEnabled.Should().BeFalse(); dialog.SelectedCode.Should().BeNull();

                search.Text = "אבן שפה"; // actual TextChanged handler searches descriptions
                var item = grid.Items.Cast<CatalogPickerDialog.Row>().Should().ContainSingle().Which;
                item.Code.Should().Be("TEST.LENGTH"); item.UnitCompatible.Should().BeTrue(); item.Price.Should().BeNull();
                grid.SelectedItem = item; // actual SelectionChanged handler
                approve.IsEnabled.Should().BeTrue();
                ((TextBlock)dialog.FindName("UnitVerdict")).Text.Should().Contain("MISSING_PRICE");
                dialog.SelectedCode.Should().BeNull("selection is not approval");
                catalog.Prices.Should().BeEmpty();
            }
            finally { dialog.Close(); }
            dialog.SelectedCode.Should().BeNull("closing an unapproved picker makes no decision");
        });
    }

    [Fact]
    public void ActualPickerCodeSearchRejectsDifferentUnitAndCloseNeverApproves()
    {
        RunSta(() =>
        {
            var dialog = Picker(Catalog());
            try
            {
                ((TextBox)dialog.FindName("SearchBox")).Text = "TEST.AREA";
                var grid = (DataGrid)dialog.FindName("Grid");
                var item = grid.Items.Cast<CatalogPickerDialog.Row>().Should().ContainSingle().Which;
                item.UnitCompatible.Should().BeFalse(); grid.SelectedItem = item;
                ((Button)dialog.FindName("BtnOk")).IsEnabled.Should().BeFalse();
                ((TextBlock)dialog.FindName("UnitVerdict")).Text.Should().Contain("יחידה לא תואמת");
                dialog.SelectedCode.Should().BeNull();
            }
            finally { dialog.Close(); }
            dialog.SelectedCode.Should().BeNull();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue("unshown WPF interaction must finish promptly");
        if (failure != null) throw new InvalidOperationException("Headless estimate-row UI test failed.", failure);
    }
}
