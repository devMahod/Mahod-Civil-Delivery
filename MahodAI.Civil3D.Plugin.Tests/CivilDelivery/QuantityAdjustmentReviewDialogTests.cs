using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class QuantityAdjustmentReviewDialogTests
{
    private static QuantityAdjustmentReviewPolicy.Context Context()
    {
        var profile = new ProjectProfile { ProfileId = "TEST-ONLY" };
        profile.Estimate.CandidateAdjustments.Add(new() { RuleId = "TEST-UNCONFIRMED", Factor = .9, Status = "UNCONFIRMED", Reason = "לא מאושר ולא מוחל" });
        return QuantityAdjustmentReviewPolicy.Capture(profile, "layer:TEST-ONLY-HW-CURB|length", new[]
        {
            new NeutralQuantityRecord { RecordId = "TEST-A1", ProjectProfileId = "TEST-ONLY", RunId = "SYNTHETIC-RENDER-ONLY",
                Source = new() { Drawing = "TEST-ONLY.dwg", DrawingHash = new string('a', 64), Handle = "A1", Layer = "TEST-ONLY-HW-CURB", EntityType = "Polyline" },
                Measurement = new() { RawValue = 20, Unit = "m", Kind = "length", Method = "polyline-length" },
                Classification = new() { RuleKey = "layer:TEST-ONLY-HW-CURB|length" } },
        });
    }
    private static void Fill(QuantityAdjustmentReviewDialog dialog)
    {
        dialog.Factor.Text = "1.2"; dialog.Basis.Text = "בדיקה סינתטית בלבד — אורך גאומטרי";
        dialog.Source.Text = "מסמך בדיקה TEST ONLY"; dialog.Reason.Text = "תרחיש בדיקה, לא הנחיה לפרויקט";
        dialog.Approver.Text = "TEST REVIEWER"; dialog.Confirm.IsChecked = true;
    }

    [Fact]
    public void ActualDialogRequiresExplicitConfirmationAndCancelNeverCreatesDecision() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new QuantityAdjustmentReviewDialog(Context());
        dialog.Decision.Should().BeNull(); dialog.Factor.Text.Should().BeEmpty();
        dialog.Save.IsEnabled.Should().BeFalse(); dialog.Remove.IsEnabled.Should().BeFalse();
        Fill(dialog); dialog.Preview.Text.Should().Contain("24.0000 מטר").And.Contain("20.0000 · לאחר השינוי").And.NotContain("→");
        dialog.Factor.Text = "1,2"; dialog.Confirm.IsChecked.Should().BeFalse();
        dialog.TryAccept(false).Should().BeFalse(); dialog.Decision.Should().BeNull();
        Fill(dialog); dialog.TryAccept(false).Should().BeTrue();
        dialog.Decision!.Factor.Should().Be(1.2); dialog.Decision.Context.RuleKey.Should().Be("layer:TEST-ONLY-HW-CURB|length");
        dialog.Close();
        var cancel = new QuantityAdjustmentReviewDialog(Context()); Fill(cancel);
        cancel.Close(); cancel.Decision.Should().BeNull(); cancel.IsVisible.Should().BeFalse();
    });

    [Theory]
    [InlineData(770, 770)] [InlineData(510, 470)]
    public void NormalAndNarrowActualUnshownDialogKeepPreviewConfirmationAndActionsVisible(int width, int height) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var dialog = new QuantityAdjustmentReviewDialog(Context()); Fill(dialog);
            var frame = UnshownDialogRender.Attach(dialog, width, height);
            UnshownDialogRender.Save(frame, "MHD_QUANTITY_FACTOR_RENDER_DIR", $"quantity-factor-top-{width}x{height}");
            dialog.BodyScroll.ScrollToBottom(); frame.UpdateLayout();
            UnshownDialogRender.Save(frame, "MHD_QUANTITY_FACTOR_RENDER_DIR", $"quantity-factor-{width}x{height}");
            foreach (var control in new System.Windows.FrameworkElement[] { dialog.Preview, dialog.Confirm, dialog.Save, dialog.Remove, dialog.Cancel, dialog.Validation })
                if (control.ActualHeight > 0) UnshownDialogRender.AssertWithin(control, frame);
            UnshownDialogRender.AssertWithin(dialog.Approver, dialog.BodyScroll);
            dialog.Save.IsEnabled.Should().BeTrue(); dialog.Decision.Should().BeNull();
            dialog.IsVisible.Should().BeFalse(); dialog.Close();
        });

    [Fact]
    public void ProductionUsesOriginalScanCasAndTransactionalRebaseNotFreshBaselineOrDirectBuild()
    {
        var source = typeof(QuantityAdjustmentReviewDialogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        var code = File.ReadAllText(Path.Combine(source, "CivilDelivery", "UI", "CivilDeliveryControl.QuantityAdjustment.cs"));
        var modal = code.IndexOf("CivilModalHost.ShowFromPalette(dialog)", StringComparison.Ordinal);
        var guard = code.IndexOf("RequireProfileDecisionScope(scope);", modal, StringComparison.Ordinal);
        var save = code.IndexOf("QuantityAdjustmentReviewPolicy.Save", StringComparison.Ordinal);
        guard.Should().BeGreaterThan(modal); save.Should().BeGreaterThan(guard);
        code.Should().Contain("!ReferenceEquals(_scan, scan)").And.Contain("RequireFreshForDecision")
            .And.Contain("scan.ProfileWriteState").And.Contain("QuantityAdjustmentReviewPolicy.RequireUnchanged")
            .And.Contain("ContinueEstimateReviewAfterDecision(saved, profileForSave, mappedRuleKey: null")
            .And.Contain("preservePreviousSelection: true").And.Contain("InvalidateEstimateEvidence")
            .And.NotContain("CaptureExpectedState(").And.NotContain("OnBuildEstimate(");
        File.ReadAllText(Path.Combine(source, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"))
            .Should().Contain("Click=\"OnEditQuantityAdjustment\"");
    }
}
