using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class CorridorMaterialAvailabilityPolicyTests
    {
        [Fact]
        public void GeometryOnlyDrawingReportsUnavailableMaterialWithoutBlockingItsMeasurements()
        {
            var finding = CorridorMaterialAvailabilityPolicy.Evaluate(
                0, false, "6422", "GM.dwg", new string('a', 64));
            finding.Should().NotBeNull();
            finding!.Severity.Should().Be(FindingSeverity.Info);
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeFalse();
            finding.Message.Should().Contain("H/HL").And.Contain("אינו כמות אפס");
            finding.SourceRefs.Should().ContainSingle();
            finding.SourceRefs[0].SourcePathOrUri.Should().Be("GM.dwg");
            finding.SourceRefs[0].DrawingChecksum.Should().Be(new string('a', 64));
        }

        [Fact]
        public void ReadFailureMustNotBeMisreportedAsAnEmptyCorridorCollection()
        {
            CorridorMaterialAvailabilityPolicy.Evaluate(0, true, "6422", "civil.dwg", "hash")
                .Should().BeNull();
        }

        [Fact]
        public void ExistingCorridorUsesItsOwnShapeAndCoverageFindings()
        {
            CorridorMaterialAvailabilityPolicy.Evaluate(1, false, "6422", "civil.dwg", "hash")
                .Should().BeNull();
        }

        [Fact]
        public void RawCadEvidenceCannotReplaceTheBlockIdentityUsedByExistingMappings()
        {
            var measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'",
                Parameters = { ["block_name"] = "*U12" },
            };
            QuantityCadMetadataPolicy.AppendEvidence(measurement, new Dictionary<string, string>
            {
                ["block_name"] = "SIGN-123",
                ["block_name_effective"] = "SIGN-123",
                ["entity_color_method"] = "ByBlock",
                ["entity_color_status"] = "raw; effective inheritance unresolved",
                ["polyline_width_min_raw"] = "0.1",
            });
            measurement.Parameters["block_name"].Should().Be("*U12");
            measurement.Parameters["cad_block_name_effective"].Should().Be("SIGN-123");
            measurement.Parameters.Should().NotContainKey("effective_color");
            measurement.Kind.Should().Be("count");
            measurement.RawValue.Should().Be(1);
            measurement.Unit.Should().Be("יח'");
        }
    }
}
