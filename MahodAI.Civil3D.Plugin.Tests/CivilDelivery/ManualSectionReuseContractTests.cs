using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Host-free source contracts for the Civil half of SEC-02.  The geometry choice
    /// itself is covered by ManualSectionReuseResolverTests; these assertions prevent
    /// the plugin wiring from silently taking ownership or mutating a foreign view.
    /// </summary>
    public class ManualSectionReuseContractTests
    {
        private static string PluginSourceDir =>
            typeof(ManualSectionReuseContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Between(string source, string start, string end)
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            from.Should().BeGreaterThanOrEqualTo(0);
            to.Should().BeGreaterThan(from);
            return source.Substring(from, to - from);
        }

        [Fact]
        public void Plan_CollectsOnlyForeignPairs_AndResolvesBeforeCreate()
        {
            var plan = Read("SectionPlanService.cs");
            var inventory = Between(
                plan,
                "internal static List<ManualSectionReuseResolver.Candidate> ScanManualSectionCandidates",
                "internal static IReadOnlyCollection<string> EligibleDeadAnnotationRecoveryKeys(");

            plan.Should().Contain("ManualSectionReuseResolver.Resolve")
                .And.Contain("record.ManualSectionReuse = new ManualSectionReusePlan")
                .And.Contain("ManualSectionAmbiguous")
                .And.Contain("ManualSectionGeometryMismatch");
            inventory.Should().Contain("SectionOwnershipService.Read(tr, sampleLine) != null")
                .And.Contain("SectionOwnershipService.Read(tr, view) != null")
                .And.Contain("TryReadSingleDatumCompatibility")
                .And.Contain("continue;")
                .And.Contain("OpenMode.ForRead")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain("UpgradeOpen")
                .And.NotContain("SectionOwnershipService.Write")
                .And.NotContain("RepairDeadEntries(")
                .And.NotContain(".Erase(")
                .And.NotContain("SectionView.Create");
        }

        [Fact]
        public void Apply_RevalidatesExactLiveHandles_AndKeepsForeignObjectsReadOnly()
        {
            var apply = Read("SectionApplyService.cs");
            var manual = Between(
                apply,
                "private static void ApplyExistingManualSection",
                "private static string UniqueSampleLineName");

            manual.Should().Contain("ScanManualSectionCandidates")
                .And.Contain("ManualSectionReuseResolver.Resolve")
                .And.Contain("selected.SectionViewHandle")
                .And.Contain("OpenMode.ForRead")
                .And.Contain("SectionOwnershipService.Read(tr, sampleLine)")
                .And.Contain("SectionOwnershipService.Read(tr, view)")
                .And.NotContain("SectionOwnershipService.Write")
                .And.NotContain(".Erase()")
                .And.NotContain("StyleId =")
                .And.NotContain("EnableSampling")
                .And.NotContain("ClampElevationRange")
                .And.NotContain("TryAttachBandSet")
                .And.NotContain("SampleLine.Create")
                .And.NotContain("SectionView.Create");
        }

        [Fact]
        public void ManualView_IsNotMoved_AndAnnotationsRemainIdempotent()
        {
            var apply = Read("SectionApplyService.cs");
            var decoration = Read("SectionDecorationService.cs");

            apply.Should().Contain("targets.Where(r => r.ManualSectionReuse == null)")
                .And.Contain("var moved = ArrangeCreatedViews(")
                .And.Contain("tr, db, profile, arrangedTargets, result, layoutOrigin, _log);");
            decoration.Should().Contain("SectionAnnotationRegistry.EraseExisting")
                .And.Contain("record.ManualSectionReuse == null")
                .And.Contain("OpenMode.ForRead")
                .And.Contain("!hasManagedTargets || EnsureSectionStyles");
        }

        [Fact]
        public void MissingApprovedStyle_UsesOwnedCleanStyle_AndBlankBandMeansNoStack()
        {
            var plan = Read("SectionPlanService.cs");

            plan.Should().Contain("SectionViewPresentationStyleService.StyleName")
                .And.Contain("PresentationStyleBuiltIn")
                .And.Contain("CommonManualSectionViewStyle(manualSections)")
                .And.Contain("Distinct(StringComparer.OrdinalIgnoreCase)")
                .And.Contain("(none; single datum required)")
                .And.NotContain("record.PlannedStyles[\"band_set_style\"] = available[0]");
        }
    }
}
