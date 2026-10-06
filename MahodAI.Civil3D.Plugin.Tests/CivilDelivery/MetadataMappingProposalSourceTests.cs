using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public sealed class MetadataMappingProposalSourceTests
    {
        [Fact]
        public void ProductionProposalGroups_ReceiveAllMeasurementsMetadata_NotFirstRecordProperties()
        {
            var root = typeof(MetadataMappingProposalSourceTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(root, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            var start = source.IndexOf("internal MappingProposalPublication ProposeMappingsUnpublished(", StringComparison.Ordinal);
            var end = source.IndexOf("internal static void PublishMappingProposalEvidence(", start, StringComparison.Ordinal);
            var proposalPath = source.Substring(start, end - start);

            proposalPath.Should().Contain("new MappingProposalEngine.DiscoveredGroup(")
                .And.Contain("QuantityCadMetadataPolicy.Summarize(g.Select(r => r.Measurement))")
                .And.Contain("MappingProposalEngine.Propose(groups, snapshot, reference.CatalogCodes)")
                .And.Contain("RequirePublishedScanEvidence(scan)")
                .And.NotContain("first.Measurement.Parameters")
                .And.NotContain("SaveApprovedMappings");
        }
    }
}
