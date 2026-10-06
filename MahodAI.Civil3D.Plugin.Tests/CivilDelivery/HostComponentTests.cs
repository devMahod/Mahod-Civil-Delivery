using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// One package, both hosts.
    ///
    /// Civil 3D 2026 and 2027 run different .NET runtimes (net8.0 / net10.0) and different
    /// Civil APIs, so a single assembly cannot serve both: the bundle declares one component
    /// per host and AutoCAD loads the matching one. An engineer who opens the year we did not
    /// ship gets no ribbon and an unknown command, with nothing to explain it — which is
    /// exactly how 1.0.0 looked at a colleague's desk (2026-08-26).
    ///
    /// The rule these tests hold: the 2026 component is compiled against the REAL 2026 API
    /// (Autodesk's Civil3D.NET 13.8.280 package), never against 2027 assemblies, and the
    /// manifest never promises a module the payload does not carry.
    /// </summary>
    public class HostComponentTests
    {
        private static string RepoRoot
        {
            get
            {
                var dir = new DirectoryInfo(
                    typeof(HostComponentTests).Assembly
                        .GetCustomAttributes<AssemblyMetadataAttribute>()
                        .First(a => a.Key == "MahodPluginSourceDir").Value!);
                return dir.Parent!.FullName;
            }
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

        [Fact]
        public void BundleManifestDeclaresBothHosts()
        {
            var xml = XDocument.Parse(Read("MahodAI.bundle", "PackageContents.xml"));
            var series = xml.Descendants("RuntimeRequirements")
                .Select(r => (string?)r.Attribute("SeriesMin"))
                .ToList();

            series.Should().Contain("R26.0", "Civil 3D 2027");
            series.Should().Contain("R25.1", "Civil 3D 2026");

            var modules = xml.Descendants("ComponentEntry")
                .Select(c => (string?)c.Attribute("ModuleName"))
                .ToList();
            modules.Should().Contain(m => m!.Contains("/Contents/2026/"),
                "the 2026 host must load its own assembly, not the 2027 one");
        }

        [Fact]
        public void TheTwoThirtyTwoSixBuildUsesTheRealTwentyTwentySixApi()
        {
            var csproj = Read("MahodAI.Civil3D.Plugin", "MahodAI.Civil3D.Plugin.csproj");

            csproj.Should().Contain("Civil3D.NET",
                "the 2026 Civil API comes from Autodesk's own reference package");
            csproj.Should().Contain("ExcludeAssets=\"runtime\"",
                "Autodesk assemblies are references only and must never ship inside our bundle");
            csproj.Should().Contain("<ItemGroup Condition=\"'$(AutoCADVersion)' == '2026'\">");
            csproj.Should().Contain("<ItemGroup Condition=\"'$(AutoCADVersion)' != '2026'\">",
                "the 2027 host keeps resolving from the local Civil 3D install");
        }

        [Fact]
        public void TheBundleCopyStepNeverPutsTheTwentyTwentySixBuildInTheTwentyTwentySevenSlot()
        {
            var csproj = Read("MahodAI.Civil3D.Plugin", "MahodAI.Civil3D.Plugin.csproj");

            csproj.Should().Contain(
                "<Target Name=\"CopyToBundle\" AfterTargets=\"Build\" Condition=\"'$(AutoCADVersion)' != '2026'\">",
                "Contents\\ is the 2027 slot; a net8.0 assembly there fails only on someone else's machine");
        }

        [Fact]
        public void AnUnbuiltHostIsRemovedFromTheManifestRatherThanPromised()
        {
            var build = Read("installer", "build-setup.ps1");

            build.Should().Contain("removed $dropped manifest block(s)",
                "a manifest entry with no module makes AutoCAD report a broken plug-in");
            build.Should().Contain("2026 build reported success but produced no assembly",
                "staging must fail loudly rather than ship an empty 2026 folder");
        }

        [Fact]
        public void TheOptionalMapAnalysisIsCompiledOutInsteadOfBlockingTwentyTwentySix()
        {
            var props = Read("Directory.Build.props");
            var analyzer = Read("MahodAI.Civil3D.Plugin", "Services", "Extraction", "Analyzers", "LayersAnalyzer.cs");

            props.Should().Contain("MAHOD_MAP3D");
            analyzer.Should().Contain("#if !MAHOD_MAP3D");
            analyzer.Should().Contain("#endif");
        }
    }
}
