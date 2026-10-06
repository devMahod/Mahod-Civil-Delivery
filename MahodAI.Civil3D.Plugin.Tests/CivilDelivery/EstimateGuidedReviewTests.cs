using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateGuidedReviewTests
{
    private static DeliveryFinding Blocker(string message, string code = "EST-XREF-TRAVERSAL-UNRESOLVED") => new()
    {
        Domain = "estimate", Code = code, Severity = FindingSeverity.Error,
        Title = "הפניה אינה טעונה", Message = message, RecommendedAction = "סרוק מחדש לאחר תיקון",
    };

    [Fact]
    public void ReviewPreservesExactExpectedPathAndDoesNotSubstituteASimilarFilename()
    {
        const string expected = @".\6422-GM-MODEL-NATAZ.dwg";
        var text = EstimateGuidedReviewText.Build("חסום", "HA.dwg",
            Array.Empty<(string?, string?)>(), new[] { Blocker(expected) });

        text.Should().Contain(expected).And.Contain("XREF").And.Contain("Reload")
            .And.Contain("שמור את השרטוט").And.Contain("סריקת אומדן חדשה")
            .And.Contain("שם דומה אינו הוכחה").And.Contain("אינו מקשר, טוען או מאשר")
            .And.NotContain("6422-GM-MODEL-NATAZ 1.dwg");
    }

    [Fact]
    public void EverySourceAndBlockerRemainsAvailablePastThePreviousTwelveAndEightLimits()
    {
        var sources = Enumerable.Range(1, 24)
            .Select(index => ((string?)$@"C:\Refs\Source-{index:D2}.dwg", (string?)$"ROOT > Source-{index:D2}"))
            .ToArray();
        var findings = Enumerable.Range(1, 19)
            .Select(index => Blocker($@"path=C:\Expected\Missing-{index:D2}.dwg; detail=" + new string('x', 2000)))
            .ToArray();
        var text = EstimateGuidedReviewText.Build("blocked", "HOST.dwg", sources, findings);

        foreach (var source in sources) text.Should().Contain(source.Item1).And.Contain(source.Item2);
        foreach (var finding in findings) text.Should().Contain(finding.Message);
        text.Should().Contain("Missing-19.dwg").And.Contain("Source-24.dwg");
    }

    [Fact]
    public void NonXrefFindingKeepsDetailsWithoutMisleadingXrefRepairInstructions()
    {
        var text = EstimateGuidedReviewText.Build("blocked", null,
            Array.Empty<(string?, string?)>(), new[] { Blocker("exact unit evidence", "EST-UNIT-MISMATCH") });

        text.Should().Contain("exact unit evidence").And.NotContain(EstimateGuidedReviewText.XrefRepairInstructions);
    }

    [Fact]
    public void ReviewWindowIsScrollableWrappingReadOnlyAndDoesNotScanOrMutateSources()
    {
        var root = typeof(EstimateGuidedReviewTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        var source = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.EstimateGuidance.cs"));
        var review = source[source.IndexOf("private void ShowEstimateGuidedReview()", StringComparison.Ordinal)..];

        review.Should().Contain("IsReadOnly = true").And.Contain("TextWrapping = TextWrapping.Wrap")
            .And.Contain("VerticalScrollBarVisibility = ScrollBarVisibility.Auto")
            .And.Contain("ResizeMode = ResizeMode.CanResize").And.Contain("UiGuard.Attach(dialog")
            .And.Contain("IsCancel = true").And.Contain("finding.Message")
            .And.NotContain(".Take(").And.NotContain("MessageBox.Show(")
            .And.NotContain("EstimateSourceInventoryService.Capture(")
            .And.NotContain("ReadDwgFile(").And.NotContain("AttachXref(")
            .And.NotContain("ReloadXrefs(").And.NotContain("ResolveXrefs(")
            .And.NotContain("File.Write").And.NotContain("ApproveCompleteDiscoveryScope(");
    }
}
