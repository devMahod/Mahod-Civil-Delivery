using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SetupToolSourceSelectionTests
{
    private static ProjectSetupScan Scan() => new()
    {
        RunId = "SYNTHETIC-ONLY", ProjectProfileId = "SYNTHETIC-ONLY",
        Sources =
        {
            new() { Name = "EG", Kind = "surface", Handle = "A1" },
            new() { Name = "EG", Kind = "corridor", Handle = "B2" },
            new() { Name = "Network", Kind = "pipe-network", Handle = "C3" },
        },
    };

    [Theory]
    [InlineData("surface")]
    [InlineData("corridor")]
    public void SameNameDifferentKindsUsesOnlyTheExplicitRequestedKind(string kind)
    {
        using var json = JsonDocument.Parse("{\"sampled_sources\":{\"EG\":\"" + kind + "\"}}");
        var result = SaveProjectSetupTool.ReadSourceSelection(Scan(), json.RootElement);
        result.Error.Should().BeNull(); result.Sources.Should().ContainSingle(); result.Sources["EG"].Should().Be(kind);
    }

    [Theory]
    [InlineData("{\"Network\":\"pipe-network\",\"EG\":\"pipe-network\"}")]
    [InlineData("{\"EG\":\"wrong-kind\"}")]
    [InlineData("{\"EG\":null}")]
    [InlineData("{\"EG\":42}")]
    [InlineData("{\"EG\":\"surface\",\"eg\":\"corridor\"}")]
    [InlineData("{}")]
    public void InvalidOrRepeatedChoiceRefusesWithoutPartialOrSubstitutedSelection(string sources)
    {
        using var json = JsonDocument.Parse("{\"sampled_sources\":" + sources + "}");
        var result = SaveProjectSetupTool.ReadSourceSelection(Scan(), json.RootElement);
        result.Error.Should().NotBeNullOrWhiteSpace(); result.Sources.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateExactNameAndKindRefusesAmbiguityWithoutThrowing()
    {
        var scan = Scan(); scan.Sources.Add(new() { Name = "EG", Kind = "surface", Handle = "D4" });
        using var json = JsonDocument.Parse("{\"sampled_sources\":{\"EG\":\"surface\"}}");
        var result = SaveProjectSetupTool.ReadSourceSelection(scan, json.RootElement);
        result.Error.Should().Contain("unique"); result.Sources.Should().BeEmpty();
    }

    [Fact]
    public void ToolPreservesScannedExternalClSourcesAndUsesExactKindValidation()
    {
        var source = File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "Tools", "CivilDelivery", "SetupTools.cs"));
        source.Should().Contain("ClSourceFiles = ClSourceSelection.MergeSources(scan.ExternalClHashes.Keys, scan.Drawing)");
        source.Should().Contain("var sourceSelection = ReadSourceSelection(scan, parameters)");
        source.Should().NotContain("scan.Sources.ToDictionary(s => s.Name, s => s.Kind");
    }
}
