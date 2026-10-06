using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public sealed class EstimateCadMetadataSourceContractTests
    {
        private static string Source(string name)
        {
            var sourceRoot = typeof(EstimateCadMetadataSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            return File.ReadAllText(Path.Combine(sourceRoot, "CivilDelivery", "Estimate", name));
        }

        [Fact]
        public void CadPropertiesAreReadAndAttachedBeforeNestedTransformation()
        {
            var source = Source("CivilQuantityExtractionService.cs");
            var consider = source.IndexOf("void Consider(", StringComparison.Ordinal);
            var natural = source.IndexOf("var measurements = NaturalMeasurements", consider, StringComparison.Ordinal);
            var read = source.IndexOf("QuantityCadMetadataReader.Read(ent, tr,", natural, StringComparison.Ordinal);
            // b24 (Codex 12:04 A): the reader receives the host database and the units resolved for it.
            source.IndexOf("new QuantityCadMetadataReader.HostUnits(db, result.PhysicalUnits!)", read, StringComparison.Ordinal)
                .Should().BeGreaterThan(read);
            var append = source.IndexOf("QuantityCadMetadataPolicy.AppendEvidence(measurement, cadEvidence)", read, StringComparison.Ordinal);
            var transform = source.IndexOf("if (transformMeasurement)", append, StringComparison.Ordinal);
            var classify = source.IndexOf("var ruleKey = BuildDiscoveryRuleKey(ent.Layer, measurement)", transform, StringComparison.Ordinal);
            natural.Should().BeGreaterThan(consider);
            read.Should().BeGreaterThan(natural);
            append.Should().BeGreaterThan(read);
            transform.Should().BeGreaterThan(append);
            classify.Should().BeGreaterThan(transform);

            var transformMethod = source.Substring(source.IndexOf(
                "private static QuantityMeasurement? TransformNestedMeasurement(", StringComparison.Ordinal));
            transformMethod.Should().Contain("Parameters = new Dictionary<string, string>(")
                .And.Contain("measurement.Parameters, StringComparer.Ordinal)");
        }

        [Fact]
        public void EffectiveDynamicNameAndStyleEvidenceCannotChangeAnExistingRawBlockRuleKey()
        {
            var measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'",
                Parameters = { ["block_name"] = "*U12" },
            };
            var before = CivilQuantityExtractionService.BuildDiscoveryRuleKey("GM|SIGNS", measurement);
            QuantityCadMetadataPolicy.AppendEvidence(measurement, new Dictionary<string, string>
            {
                ["block_name_effective"] = "SIGN-123",
                ["entity_color_method"] = "ByBlock",
                ["entity_linetype"] = "DASHED",
            });
            CivilQuantityExtractionService.BuildDiscoveryRuleKey("GM|SIGNS", measurement)
                .Should().Be(before);
            CivilQuantityExtractionService.BuildDiscoveryRuleKey("SM|SIGNS", measurement)
                .Should().Be(before);
            measurement.Parameters["block_name"].Should().Be("*U12");
        }

        [Fact]
        public void RawWidthAndInheritedColorsAreNeverAdvertisedAsResolvedPaintedGeometry()
        {
            var reader = Source("QuantityCadMetadataReader.cs");
            reader.Should().Contain("using AcadColor = Autodesk.AutoCAD.Colors.Color;")
                .And.Contain("raw; effective inheritance unresolved")
                .And.Contain("entity database drawing units; untransformed")
                .And.Contain("painted area and dash spacing not inferred")
                .And.Contain("ColorMethod.ByColor")
                .And.Contain("block.DynamicBlockTableRecord, OpenMode.ForRead")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain("AttachXref");
        }
    }
}
