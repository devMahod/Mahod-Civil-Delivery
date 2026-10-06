using System.IO;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class Host2026BaselineReferenceTests
{
    private static string RepoRoot => Directory.GetParent(
        typeof(Host2026BaselineReferenceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "MahodPluginSourceDir").Value!)!.FullName;

    [Fact]
    public void BaselineIsExactCompileOnlyPackage_NotTheUpdatedInstalledApi()
    {
        var product = XDocument.Load(Path.Combine(RepoRoot,
            "MahodAI.Civil3D.Plugin", "MahodAI.Civil3D.Plugin.csproj"));
        var package = product.Descendants("PackageReference")
            .Single(e => (string?)e.Attribute("Include") == "AutoCAD.NET");
        ((string?)package.Attribute("Version")).Should().Be("[25.1.0]");
        ((string?)package.Attribute("ExcludeAssets")).Should().Be("runtime");
        ((string?)package.Parent!.Attribute("Condition"))
            .Should().Be("'$(AutoCADVersion)' == '2026'");

        foreach (var project in new[] { "MahodAI.Civil3D.Plugin", "MahodAI.Civil3D.Plugin.Tests" })
        {
            var xml = XDocument.Load(Path.Combine(RepoRoot, project, project + ".csproj"));
            foreach (var reference in xml.Descendants("Reference").Where(e =>
                new[] { "acmgd", "acdbmgd", "accoremgd", "AdWindows", "AeccDbMgd" }
                    .Contains((string?)e.Attribute("Include"), StringComparer.OrdinalIgnoreCase)))
            {
                ((string?)reference.Parent!.Attribute("Condition"))
                    .Should().Be("'$(AutoCADVersion)' != '2026'");
            }
        }
    }

    [Fact]
    public void NormalInstallerBuildUsesProjectReferenceContract_NoMachineLocal2026Gate()
    {
        var build = File.ReadAllText(Path.Combine(RepoRoot, "installer", "build-setup.ps1"));
        build.Should().Contain("NuGet AutoCAD.NET [25.1.0] + Civil3D.NET 13.8.280")
            .And.Contain("-p:AutoCADVersion=2026")
            .And.Contain("2026 build failed; no partial single-host setup will be produced")
            .And.Contain("civil_2026_reference_source = $refs2026")
            .And.NotContain("C:\\Program Files\\Autodesk\\AutoCAD 2026\\acmgd.dll");
    }

    [Fact]
    public void BuildOutputContainsNoAutodeskHostAssemblies()
    {
        // This examines real compiled output, not only ExcludeAssets source text.
        // It is an offline packaging check, not a native-host compatibility test.
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AcMgd.dll", "AcCoreMgd.dll", "AcDbMgd.dll", "AcCui.dll", "AcDx.dll",
            "AcMr.dll", "AcSeamless.dll", "AcTcMgd.dll", "AcWindows.dll",
            "AdUIMgd.dll", "AdUiPalettes.dll", "AdWindows.dll", "acdbmgdbrep.dll",
            "AeccDbMgd.dll", "AeccPressurePipesMgd.dll", "AecBaseMgd.dll",
            "AecPropDataMgd.dll", "AeccDataShortcutMgd.dll",
        };
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.AllDirectories)
            .Where(p => forbidden.Contains(Path.GetFileName(p))).Should().BeEmpty(
                "Autodesk APIs are provided by the host and must not be copied into our output");
    }
}
