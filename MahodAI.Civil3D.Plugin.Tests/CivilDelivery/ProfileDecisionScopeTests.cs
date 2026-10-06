using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ProfileDecisionScopeTests
{
    private sealed record ValueIdentity(string Name);

    private static ProfileDecisionIdentity Baseline() => new(
        new ValueIdentity("project.dwg"), new ValueIdentity("profile"),
        new string('A', 64), "C:/profiles/source.yaml", "C:/profiles/runtime.yaml",
        new string('B', 64), "C:/drawings/project.dwg", new string('C', 64), "fingerprint:42");

    [Fact]
    public void UnchangedObjectsAndEvidence_AllowTheOriginalDecisionToProceed()
    {
        var expected = Baseline();
        var unchanged = expected with { };

        var act = () => ProfileDecisionIdentityGuard.RequireUnchanged(expected, unchanged);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("document")]
    [InlineData("profile")]
    public void ReplacementObjectsWithEqualNames_CannotReceiveAnEarlierModalDecision(string field)
    {
        var expected = Baseline();
        var current = field == "document"
            ? expected with { Document = new ValueIdentity("project.dwg") }
            : expected with { Profile = new ValueIdentity("profile") };
        // Value equality would accept both replacements, while object identity
        // distinguishes the drawing/profile that actually populated the dialog.
        current.Document.Should().Be(expected.Document);
        current.Profile.Should().Be(expected.Profile);

        var act = () => ProfileDecisionIdentityGuard.RequireUnchanged(expected, current);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("profile_bytes")]
    [InlineData("profile_source")]
    [InlineData("profile_target")]
    [InlineData("in_memory_profile")]
    [InlineData("save_as")]
    [InlineData("drawing_bytes")]
    [InlineData("live_revision")]
    public void ChangesDuringTheModal_AreRejectedEvenWhenObjectReferencesStayTheSame(string change)
    {
        var expected = Baseline();
        var current = change switch
        {
            "profile_bytes" => expected with { ProfileHash = new string('D', 64) },
            "profile_source" => expected with { ProfileSource = "C:/profiles/other.yaml" },
            "profile_target" => expected with { ProfileWriteTarget = "C:/profiles/other-runtime.yaml" },
            "in_memory_profile" => expected with { EffectiveProfileHash = new string('D', 64) },
            "save_as" => expected with { DrawingPath = "C:/drawings/project-renamed.dwg" },
            "drawing_bytes" => expected with { DrawingHash = new string('D', 64) },
            "live_revision" => expected with { DatabaseRevision = "fingerprint:43" },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        var act = () => ProfileDecisionIdentityGuard.RequireUnchanged(expected, current);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MissingOriginalEvidence_CannotBeReplacedByAPostDialogBaseline()
    {
        var expected = Baseline() with { EffectiveProfileHash = "" };

        var act = () => ProfileDecisionIdentityGuard.RequireUnchanged(expected, expected);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WindowsPathAndShaCasing_DoNotInventAChangedDecisionScope()
    {
        var expected = Baseline();
        var current = expected with
        {
            ProfileSource = expected.ProfileSource.ToUpperInvariant(),
            ProfileWriteTarget = expected.ProfileWriteTarget.ToUpperInvariant(),
            DrawingPath = expected.DrawingPath.ToUpperInvariant(),
            ProfileHash = expected.ProfileHash.ToLowerInvariant(),
            EffectiveProfileHash = expected.EffectiveProfileHash.ToLowerInvariant(),
            DrawingHash = expected.DrawingHash.ToLowerInvariant(),
        };

        var act = () => ProfileDecisionIdentityGuard.RequireUnchanged(expected, current);

        act.Should().NotThrow();
    }

    private static string PluginSourceDir =>
        typeof(ProfileDecisionScopeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string Ui(string name) => File.ReadAllText(Path.Combine(
        PluginSourceDir, "CivilDelivery", "UI", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the production method {0} must exist", signature);
        var rest = source[(start + signature.Length)..];
        var next = Regex.Match(rest, @"(?m)^[ \t]+(?:private|internal|public)\s");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    private static int At(string source, string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, "the source contract requires {0}", token);
        return index;
    }

    [Theory]
    [InlineData("OnPickCl", ".ShowDialog()")]
    [InlineData("OnApproveEstimateScope", "CivilModalHost.ShowFromPalette(dialog)")]
    [InlineData("OnEarthworksDecision", "CivilModalHost.ShowFromPalette(dialog)")]
    public void EachModalCapturesBeforeReview_AndRechecksBeforeApplyingAcceptedChoices(
        string handler, string modalToken)
    {
        var body = Method(Ui("CivilDeliveryControl.xaml.cs"), "private void " + handler + "(");
        var capture = At(body, "CaptureProfileDecisionScope(");
        var modal = At(body, modalToken);
        capture.Should().BeLessThan(modal);
        var accepted = body[modal..];
        At(accepted, "RequireProfileDecisionScope(decisionScope)")
            .Should().BeLessThan(At(accepted, "CloneProfileForDecision(decisionScope.Profile)"));
        accepted.Should().Contain("decisionScope.ExpectedState")
            .And.Contain("decisionScope.ExpectedState.TargetPath")
            .And.NotContain("CaptureExpectedProfileState(")
            .And.NotContain("CloneProfileForDecision(_profile)")
            .And.NotContain("EnsureSavedForAction(");
    }

    [Fact]
    public void ClFileAndLayerPickers_BothRecheckTheOriginalScopeBeforeUsingTheirChoices()
    {
        var body = Method(Ui("CivilDeliveryControl.xaml.cs"), "private void OnPickCl(");
        var firstModal = At(body, "dialog.ShowDialog()");
        var secondModal = At(body, "CivilModalHost.ShowFromPalette(picker)");
        var firstGuard = At(body[firstModal..], "RequireProfileDecisionScope(decisionScope)") + firstModal;
        firstGuard.Should().BeLessThan(At(body, "ScanClFile("));
        var secondGuard = At(body[secondModal..], "RequireProfileDecisionScope(decisionScope)") + secondModal;
        secondGuard.Should().BeLessThan(At(body, "CloneProfileForDecision(decisionScope.Profile)"));
        body.Should().Contain("scan.RequireSourceUnchanged();")
            .And.Contain("[scan.Path] = scan.SourceHash");
    }

    [Fact]
    public void ScopeGuard_RechecksOriginalCasAndNeverRecapturesApprovalAuthority()
    {
        var source = Ui("CivilDeliveryControl.ProfileDecisionScope.cs");
        var capture = Method(source, "private ProfileDecisionScope CaptureProfileDecisionScope(");
        capture.Should().Contain("CaptureExpectedProfileState();")
            .And.Contain("CaptureReadySource(document, operation)");
        var require = Method(source, "private void RequireProfileDecisionScope(");
        require.Should().Contain("ReferenceEquals(Doc(), scope.Document)")
            .And.Contain("ReferenceEquals(_profile, scope.Profile)")
            .And.Contain("CaptureReadySource(scope.Document, scope.Operation)")
            .And.Contain("RequireUnchanged(scope.Identity, current)")
            .And.Contain("RequireExpectedStateUnchanged(scope.Profile, scope.ExpectedState)")
            .And.NotContain("CaptureExpectedProfileState(")
            .And.NotContain("ReloadProfile(")
            .And.NotContain("EnsureSavedForAction(");
    }

    [Fact]
    public void EstimateSourceReview_PreservesFullScrollableInventoryAndRequiresAnExplicitApproval()
    {
        var xaml = Ui("EstimateSourceReviewDialog.xaml");
        var code = Ui("EstimateSourceReviewDialog.xaml.cs");
        xaml.Should().Contain("Width=\"940\" Height=\"760\"")
            .And.Contain("MinWidth=\"580\" MinHeight=\"560\"")
            .And.Contain("ResizeMode=\"CanResize\"")
            .And.Contain("FlowDirection=\"RightToLeft\"")
            .And.Contain("IsReadOnly=\"True\"")
            .And.Contain("TextWrapping=\"Wrap\"")
            .And.Contain("VerticalScrollBarVisibility=\"Auto\"")
            .And.Contain("HorizontalScrollBarVisibility=\"Disabled\"")
            .And.Contain("x:Name=\"InventoryText\" MinHeight=\"80\"")
            .And.Contain("תנאי ההיקף — אינו אישור לכמויות או למחירים")
            .And.Contain("<ScrollViewer x:Name=\"ScopeScroll\"")
            .And.Contain("Click=\"OnApprove\" IsDefault=\"False\"")
            .And.Contain("IsCancel=\"True\"")
            .And.Contain("ToolTip=\"{Binding Path}\"")
            .And.Contain("x:Name=\"InventoryExpander\"")
            .And.NotContain("ScrollChanged=");
        code.Should().Contain("InventoryText.Text = inventoryText ?? string.Empty;")
            .And.Contain("ScopeText.Text = scopeText ?? string.Empty;")
            .And.Contain("UiGuard.Attach(this, ")
            .And.NotContain(".Substring(")
            .And.NotContain(".Take(");
        var constructor = Method(code, "public EstimateSourceReviewDialog(");
        constructor.Should().NotContain("DialogResult = true");
        Method(code, "private void OnApprove(").Should().Contain("DialogResult = true");
        Method(code, "private void OnCancel(").Should().Contain("DialogResult = false");
    }
}
