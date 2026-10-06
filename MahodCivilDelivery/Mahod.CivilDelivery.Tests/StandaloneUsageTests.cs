using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using Xunit;

namespace Mahod.CivilDelivery.Tests;

/// <summary>
/// Mahod Impact usage in the separate plugin (1.4.2): it carries the MahodAI recorder, bakes the products' key only
/// at build time, reports its own VERSION, and its typed MCD_* commands use the standalone door — decided by the
/// facade, never by an assembly-name test (this repository's MahodAI fork shares MahodAI's assembly name).
/// </summary>
public sealed class StandaloneUsageTests
{
    private static string Source(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", relative)));

    [Fact]
    public void ThePluginCarriesTheRecorderAndReportsItsOwnVersion()
    {
        var plugin = typeof(CivilDeliveryCommandNames).Assembly;
        plugin.GetName().Name.Should().Be("Mahod.CivilDelivery");
        plugin.GetType("MahodAI.Civil3D.Plugin.Utilities.MahodUsage").Should().NotBeNull();
        plugin.GetType("MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services.CivilDeliveryUsage").Should().NotBeNull();

        var version = Source("VERSION").Trim();
        var v = plugin.GetName().Version!;
        $"{v.Major}.{v.Minor}.{v.Build}".Should().Be(version, "the records' product version (p.v) is the assembly version");
    }

    [Fact]
    public void TheKeyIsBakedOnlyFromTheBuildEnvironment_NeverFromTheRepository()
    {
        var csproj = Source("Mahod.CivilDelivery/Mahod.CivilDelivery.csproj");
        csproj.Should().Contain("<MahodUsageKeyed>true</MahodUsageKeyed>")
            .And.Contain(@"<Compile Include=""..\..\MahodAI.Civil3D.Plugin\Utilities\MahodUsage.cs"" Link=""Shared\MahodUsage.cs"" />");
        var props = Source("../Directory.Build.props");
        props.Should().Contain("<Target Name=\"MahodUsageKey\" BeforeTargets=\"CoreCompile\"")
            .And.Contain("Condition=\"'$(MahodUsageKeyed)' == 'true' AND '$(MAHOD_CAD_KEY)' != ''\"")
            .And.Contain("$(IntermediateOutputPath)MahodUsageKey.g.cs");
        // The generated key file lives in obj/, which git ignores.
        Source("../.gitignore").Should().Contain("obj/");
    }

    [Fact]
    public void TypedCommandsUseTheStandaloneDoor_ThePaletteKeepsItsOwn()
    {
        var facade = Source("Mahod.CivilDelivery/Runtime/CivilDeliveryCommandFacade.cs");
        facade.Should().Contain("if (typedFlow) MahodUsage.AmbientDoor = MahodUsage.Standalone;")
            .And.Contain("MahodUsage.AmbientDoor = outer;")
            .And.Contain("Typed(CivilDeliveryUsage.OpenAction, typedFlow: false, DeliveryBody)")
            .And.Contain("Typed(CivilDeliveryUsage.SectionsAction, typedFlow: true, SectionsBody)")
            .And.Contain("Typed(CivilDeliveryUsage.SetupAction, typedFlow: true, SetupBody)")
            .And.Contain("Typed(null, typedFlow: true, EstimateBody)")
            .And.NotContain("GetName()");
        // The after-save continuations resume the palette: no typed door, no action of their own.
        facade.Should().Contain("public void DeliveryAfterSave() { if (Ready()) DeliveryAfterSaveBody(); }")
            .And.Contain("public void EstimateAfterSave() { if (Ready()) EstimateAfterSaveBody(); }");
    }
}
