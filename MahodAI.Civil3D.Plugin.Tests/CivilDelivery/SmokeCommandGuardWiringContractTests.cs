using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The shipped smoke commands are fail-closed (final offline sweep, 1.2.30):
    /// MHD_SMOKE_SECTIONS refuses to APPLY outside the fixture folder before any
    /// transaction opens, and MHD_SMOKE_DISCOVER aborts its read-only transaction.
    /// </summary>
    public class SmokeCommandGuardWiringContractTests
    {
        private static string PluginSourceDir =>
            typeof(SmokeCommandGuardWiringContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Command(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Commands", name));

        [Fact]
        public void SmokeSections_RefusesBeforeAnyWorkflowCall_UnlessFixtureOrExplicitOverride()
        {
            var smoke = Command("MhdSmokeSectionsCommand.cs");
            smoke.Should().Contain("SmokeApplyGuard.Refusal(")
                .And.Contain("\"MahodCivilDelivery_Fixtures\"")
                .And.Contain("Environment.GetEnvironmentVariable(SmokeApplyGuard.OverrideVariable)")
                .And.Contain("Step(steps, \"guard\", false, refusal, null);")
                .And.Contain("report[\"result\"] = refusal != null ? \"refused\" : ok ? \"pass\" : \"fail\";");

            var guard = smoke.IndexOf("SmokeApplyGuard.Refusal(", StringComparison.Ordinal);
            var firstWorkflowCall = smoke.IndexOf("workflow.Plan(", StringComparison.Ordinal);
            var apply = smoke.IndexOf("workflow.Apply(", StringComparison.Ordinal);
            guard.Should().BeGreaterThan(0);
            guard.Should().BeLessThan(firstWorkflowCall, "the guard must precede the first workflow call");
            guard.Should().BeLessThan(apply);
            // The guarded branch is the only way into the workflow.
            smoke.Replace("\r\n", "\n").Should().Contain("else\n            try\n");
        }

        [Fact]
        public void SmokeDiscover_IsReadOnly()
        {
            var discover = Command("MhdSmokeDiscoverCommand.cs");
            discover.Should().NotContain("tr.Commit()")
                .And.Contain("tr.Abort();");
        }
    }
}
