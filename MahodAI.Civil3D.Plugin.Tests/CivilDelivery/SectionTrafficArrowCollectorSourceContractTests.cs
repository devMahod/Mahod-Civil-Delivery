using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionTrafficArrowCollectorSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionTrafficArrowCollectorSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        [Fact]
        public void Collector_WalksOrdinaryAndXrefBlocksWithComposedTransforms()
        {
            var source = Read("SectionTrafficArrowCollector.cs");

            source.Should().Contain("outerTransform * reference.BlockTransform")
                .And.Contain("depth + 1")
                .And.Contain("definitionStack")
                .And.Contain("MaxBlockNestingDepth = 6")
                .And.Contain("definition.IsFromExternalReference")
                .And.Contain("definition.IsUnloaded || !definition.IsResolved");
            source.Should().NotContain("if (!definition.IsFromExternalReference)",
                "ordinary nested block definitions contain the actual arrow inserts");
        }

        [Fact]
        public void Heading_UsesTransformedCanonicalForwardAxis_NotSignedOffsetOrRawRotation()
        {
            var source = Read("SectionTrafficArrowCollector.cs");

            source.Should().Contain("Vector3d.YAxis.TransformBy(composedTransform)")
                .And.Contain("Math.Atan2(forward.Y, forward.X)")
                .And.Contain("reference.Position.TransformBy(outerTransform)")
                .And.NotContain("OfficeCarViewForSignedOffset")
                .And.NotContain("reference.Rotation +");
        }

        [Fact]
        public void DynamicNamesAndBikeExclusion_AreDelegatedToPureAllowlist()
        {
            var source = Read("SectionTrafficArrowCollector.cs");
            var core = File.ReadAllText(Path.Combine(
                PluginSourceDir, "..", "MahodAI.CivilDelivery.Core", "Shared",
                "TrafficDirectionEvidenceLogic.cs"));

            source.Should().Contain("reference.DynamicBlockTableRecord")
                .And.Contain("TrafficDirectionEvidenceLogic.ClassifySource")
                .And.Contain("SourceClass.Unapproved");
            core.Should().Contain("ARROW-D")
                .And.Contain("BL-ARW-W-Y")
                .And.Contain("HA-BIKE")
                .And.Contain("ResolveNearestBike")
                .And.Contain("SourceClass.ExcludedBikeArrow")
                .And.Contain("nearest-arrows-conflict")
                .And.Contain("no-approved-arrow-in-range");
        }

        [Fact]
        public void PolylineVertexOrder_IsNeverTreatedAsTrafficDirection()
        {
            var source = Read("SectionTrafficArrowCollector.cs");

            source.Should().Contain("polyline vertex order")
                .And.NotContain("case Polyline")
                .And.NotContain("GetPoint3dAt");
        }

        [Fact]
        public void ArrowXrefEvidence_IsHashedAndParticipatesInPlanIntegrity()
        {
            var source = Read("SectionTrafficArrowCollector.cs");
            var plan = Read("SectionPlanService.cs");

            source.Should().Contain("SectionXrefSnapshotGuard.Cache")
                .And.Contain("xrefSnapshots.Validate(definition, sourcePath)")
                .And.Contain("snapshot.IsFresh")
                .And.Contain("snapshot.Sha256")
                .And.Contain("SectionExternalSourceEvidence")
                .And.Contain("traffic-direction-arrow")
                .And.Contain("SectionFindingCodes.ExternalSourceChanged")
                .And.Contain("SectionFindingCodes.XrefTraversalUnresolved");
            plan.Should().Contain("trafficArrows.ExternalSources")
                .And.Contain("trafficArrows.Findings")
                .And.Contain("ClInstructionReader.AddExternalEvidence");
        }
    }
}
