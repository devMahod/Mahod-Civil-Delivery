using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Source wiring contracts only. Core policy tests exercise state transitions;
// actual native document opening/setup remains a Civil acceptance test.
public sealed class SectionFirstUseGuidanceSourceContractTests
{
    private static string Source()
    {
        var root = typeof(SectionFirstUseGuidanceSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root!, "CivilDelivery", "UI", "CivilDeliveryControl.SectionGuidance.cs"))
            .Replace("\r\n", "\n");
    }

    [Fact]
    public void NoDocumentButtonIsEnabledAndRoutesToGuardedOpen_NotADisabledPlaceholder()
    {
        var source = Source();
        source.Should().Contain("Doc() != null || _sectionGuidedAction.Action == SectionGuidedActionKind.OpenDrawing")
            .And.Contain("case SectionGuidedActionKind.OpenDrawing: OnOpenSectionDrawing();")
            .And.Contain("if (picker.ShowDialog() != true)")
            .And.Contain("documents.IsApplicationContext")
            .And.Contain("documents.ExecuteInApplicationContext")
            .And.Contain("DocumentCollectionExtension.Open(documents, selectedPath, false)");
        var open = source[source.IndexOf("private void OnOpenSectionDrawing()", StringComparison.Ordinal)..];
        open.Should().Contain("Doc() != null || _pendingWorkflowSaveDocument != null || _busyProgress != null")
            .And.Contain("File.Exists(selectedPath)")
            .And.Contain("finally { _sectionDrawingOpenPending = false; RefreshGates(); }")
            .And.NotContain("SendStringToExecute").And.NotContain("SaveAs(")
            .And.NotContain("CloseAnd").And.NotContain(".Add(documents");
    }

    [Fact]
    public void UnusableProfileAndUnconfiguredSourcesHaveDifferentRealEditors()
    {
        var source = Source();
        source.Should().Contain("NeedsSectionSetup = _profile != null && ProjectSetupService.NeedsSetup(_profile)")
            .And.Contain("case SectionGuidedActionKind.ConfigureProfile:\n                    OnSelectProjectProfile(sender, e); break;")
            .And.Contain("case SectionGuidedActionKind.ConfigureSectionSources: OnSetup(sender, e); break;")
            .And.NotContain("Tabs.SelectedIndex = 2");
    }

    [Fact]
    public void DirectClRecoveryRequiresAllCurrentGlobalFindingsToBeClMissing()
    {
        var source = Source();
        source.Should().Contain("finding.AffectedRecordIds.Count == 0 && IsUnresolvedSectionGuidanceFinding(finding)")
            .And.Contain("CanRecoverClSource = unresolvedGlobalFindings.Length > 0 &&")
            .And.Contain("unresolvedGlobalFindings.All(finding => finding.Code == SectionFindingCodes.ClSourceMissing)")
            .And.Contain("finding.Code == SectionFindingCodes.ClNoIntersection && IsUnresolvedSectionGuidanceFinding(finding)")
            .And.Contain("finding.ResolvedAtUtc == null")
            .And.Contain("string.IsNullOrWhiteSpace(finding.ResolvedBy)")
            .And.Contain("string.IsNullOrWhiteSpace(finding.Resolution)");
    }
}
