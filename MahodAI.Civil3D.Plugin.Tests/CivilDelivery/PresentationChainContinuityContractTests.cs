using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// 1.2.22-1.2.33 counted every curb face under the 0.50 m noise floor as a break in
    /// the width chain, so no section of the real 6422 plan could reach Ready after its
    /// strips were named (live, 2026-09-03). These contracts pin the corrected rule and
    /// the shared vehicle-width constants used by PLAN, the profile writer and the dialog.
    /// </summary>
    public class PresentationChainContinuityContractTests
    {
        private static string PluginSourceDir =>
            typeof(PresentationChainContinuityContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Core(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "..", "MahodAI.CivilDelivery.Core", "Shared", name));

        private static string Plugin(params string[] parts) => File.ReadAllText(Path.Combine(
            new[] { PluginSourceDir, "CivilDelivery" }.Concat(parts).ToArray()));

        [Fact]
        public void NarrowGaps_AreCounted_AndOnlyHolesBreakTheChain()
        {
            var logic = Core("SectionProjectionLogic.cs");
            logic.Should().Contain("var narrowGaps = gaps.Count(gap => gap < StripNoiseFloorM);")
                .And.Contain("var oversizedGaps = gaps.Count(gap => gap > StripWidthCeilingM);")
                .And.Contain("widths.Count + narrowGaps == gaps.Count &&")
                .And.Contain("NarrowGapCount = narrowGaps,")
                .And.NotContain("widths.Count == dimensions.Count - 1");

            Plugin("Sections", "Services", "SectionPlanService.cs")
                .Should().Contain("narrow_gaps={s.NarrowGapCount}; oversized_gaps={s.OversizedGapCount}");
        }

        [Fact]
        public void UnreadableAreaOnANamedCut_WarnsInPlanAndApply_WithOneSharedRule()
        {
            var plan = Plugin("Sections", "Services", "SectionPlanService.cs");
            plan.Should().Contain("SectionProjectionFailureScope.BlocksCreation(f, record.Cl.WcsEndpoints, unresolvedSpanCount)")
                .And.Contain("SectionProjectionFailureScope.ForNamedCut(failure, record.RecordId)")
                .And.Contain("UnresolvedSpanCountForFailureScope(r))).ToList();")
                .And.Contain("coverage.NamedStripCount == coverage.WidthSpanCount &&");
            var apply = Plugin("Sections", "Services", "SectionDecorationService.cs");
            apply.Should().Contain("SectionProjectionFailureScope.BlocksCreation(f, target.Cl.WcsEndpoints, unresolvedSpanCount)")
                .And.Contain("SectionPlanService.UnresolvedSpanCountForFailureScope(target)")
                .And.Contain("SectionProjectionFailureScope.ForNamedCut(f, target.RecordId)");
            // The apply-side warning is recorded before the blocking decision, never after a throw.
            apply.IndexOf("ForNamedCut(f, target.RecordId)", StringComparison.Ordinal)
                .Should().BeLessThan(apply.IndexOf("if (failures.Count == 0) continue;", StringComparison.Ordinal));
        }

        [Fact]
        public void VehicleWidthRule_IsOneSharedContract()
        {
            Core("SectionFurnitureLogic.cs")
                .Should().Contain("public const double MaxSingleVehicleStripWidthM = 6.5;")
                .And.Contain("stripWidthM >= MinimumStripWidthM(spec);");
            Plugin("Sections", "Services", "SectionDecisionProfileService.cs")
                .Should().Contain("SectionFurnitureLogic.MaxSingleVehicleStripWidthM")
                .And.NotContain("WidthM > 6.5");
            Plugin("UI", "SpanLabelDecisionModel.cs")
                .Should().Contain("SectionFurnitureLogic.FitsStrip(vehicle, width)")
                .And.Contain("ProblemApprovedCount == 0");
        }
    }
}
