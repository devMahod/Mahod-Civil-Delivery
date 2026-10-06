using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>UI dispatch wiring contract only; behavioral persistence and WPF tests are separate.</summary>
public sealed class EstimateProjectStartRoutingTests
{
    [Fact]
    public void SavedDrawingCheckPrecedesFirstProfileAndMeasurementContinuesOnlyAfterSuccessfulReview()
    {
        var path = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var text = File.ReadAllText(path);
        var begin = text.IndexOf("private void OnScan(", StringComparison.Ordinal);
        var end = text.IndexOf("private void OfferExplicitSaveAndResume(", begin, StringComparison.Ordinal);
        var scan = text[begin..end];
        scan.Should().Contain("if (!EnsureEstimateProjectForScan(doc)) return;");
        scan.IndexOf("EstimateSourceSnapshotPolicy.ForScan", StringComparison.Ordinal).Should()
            .BeLessThan(scan.IndexOf("EnsureEstimateProjectForScan", StringComparison.Ordinal));
        scan.IndexOf("EnsureEstimateProjectForScan", StringComparison.Ordinal).Should()
            .BeLessThan(scan.IndexOf("RunEstimateScan(doc)", StringComparison.Ordinal));
        scan.Should().NotContain("OnProjectSetup").And.NotContain("ClLayers").And.NotContain("SampledSources");
    }

    [Fact]
    public void FirstProfileUsesOneDialogAndProductionScopeWriterWithoutSectionSetup()
    {
        var root = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery");
        var ui = File.ReadAllText(Path.Combine(root, "UI", "CivilDeliveryControl.EstimateProjectStart.cs"));
        ui.Split("CivilModalHost.ShowFromPalette", StringSplitOptions.None).Length.Should().Be(2);
        ui.Should().Contain("dialog.ApprovedDecision == null").And.Contain("RequireProfileDecisionScope(scope)")
            .And.Contain("PublishSavedProfile(saved)").And.NotContain("ProjectSetupService.Save");
        var service = File.ReadAllText(Path.Combine(root, "Estimate", "EstimateProjectStartService.cs"));
        service.Should().Contain("ApproveCompleteDiscoveryScope").And.NotContain("SaveEarthworksDecision")
            .And.NotContain("SaveApprovedMappings");
    }
}
