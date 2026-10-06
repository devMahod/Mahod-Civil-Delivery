using System;
using System.Linq;
using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The test suite must never write to, or delete, the engineer's LIVE project
    /// profile under %LOCALAPPDATA%. On 2026-08-19 two tests did exactly that - saved
    /// a fixture over the real 6422 profile and then "cleaned up" by deleting it -
    /// and every full test run silently destroyed an engineer's configuration.
    ///
    /// This class is the tripwire. Nothing else in the suite is allowed to move the
    /// file's hash. It is structural, not a unit test of behaviour.
    /// </summary>
    [Collection("LiveProfileGuard")]
    public class LiveProfileGuardTests
    {
        private static string LivePath => ProjectProfileWriter.RuntimeProfilePath("6422");

        [Fact]
        public void ServiceWritesWhereItIsTold_NotToTheLiveProfileByDefaultInTests()
        {
            // The only writer must accept an explicit target; tests use it.
            var svcSrc = File.ReadAllText(Path.Combine(
                TestPaths.PluginSourceDir, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            svcSrc.Should().Contain("string targetPath",
                "SaveApprovedMappings must take an explicit target so tests can redirect it");
            svcSrc.Should().NotContain("targetPath ?? ProjectProfileWriter.RuntimeProfilePath",
                "a missing target must fail instead of silently selecting another profile file");
        }

        [Fact]
        public void NoTestSourceCallsTheLiveWriterWithoutATargetPath()
        {
            // Source-level: any test that calls SaveApprovedMappings must pass targetPath.
            var testsDir = Path.Combine(TestPaths.PluginSourceDir, "..", "MahodAI.Civil3D.Plugin.Tests");
            foreach (var file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    continue;
                if (Path.GetFileName(file) == nameof(LiveProfileGuardTests) + ".cs") continue;
                var src = File.ReadAllText(file);
                var idx = 0;
                while ((idx = src.IndexOf(".SaveApprovedMappings(", idx, StringComparison.Ordinal)) >= 0)
                {
                    // Look ahead to the end of this call for targetPath:
                    var end = src.IndexOf(");", idx, StringComparison.Ordinal);
                    var call = end > 0 ? src.Substring(idx, end - idx) : src.Substring(idx);
                    call.Should().Contain("targetPath:",
                        $"{Path.GetFileName(file)} calls SaveApprovedMappings without redirecting it away from the live profile");
                    idx = end > 0 ? end : src.Length;
                }
            }
        }

        [Fact]
        public void NoTestSourceDeletesTheLiveProfilePath()
        {
            var testsDir = Path.Combine(TestPaths.PluginSourceDir, "..", "MahodAI.Civil3D.Plugin.Tests");
            foreach (var file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    continue;
                if (Path.GetFileName(file) == nameof(LiveProfileGuardTests) + ".cs") continue;
                var src = File.ReadAllText(file);
                src.Should().NotContain("RuntimeProfilePath(",
                    $"{Path.GetFileName(file)} resolves the LIVE profile path; tests must use temp paths only");
            }
        }
    }

    internal static class TestPaths
    {
        public static string PluginSourceDir =>
            typeof(TestPaths).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
    }
}
