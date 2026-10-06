using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class QuantityCadMetadataSummaryTests
    {
        private static QuantityMeasurement Row(string? color) => new()
        {
            Kind = "length", Method = "line-length", RawValue = 10, Unit = "m",
            Parameters = color == null ? new() : new() { ["cad_entity_color_index"] = color },
        };

        [Fact]
        public void DifferentValuesUnderOneQuantityGroupAreExplicitlyMixed()
        {
            var summary = QuantityCadMetadataPolicy.Summarize(new[] { Row("1"), Row("7") }).Single();
            summary.IsMixed.Should().BeTrue();
            summary.DisplayValue.Should().Contain("מעורב").And.Contain("1").And.Contain("7");
        }

        [Fact]
        public void MissingMetadataCannotBorrowAnotherRecordsValue()
        {
            var summary = QuantityCadMetadataPolicy.Summarize(new[] { Row("1"), Row(null) }).Single();
            summary.IsMixed.Should().BeTrue();
            summary.MissingCount.Should().Be(1);
            summary.DisplayValue.Should().Contain("מעורב").And.Contain("חסר ב-1 מתוך 2");
        }

        [Fact]
        public void UniformObservationIsShownWithoutAnInventedDifference()
        {
            var summary = QuantityCadMetadataPolicy.Summarize(new[] { Row("7"), Row("7") }).Single();
            summary.IsMixed.Should().BeFalse();
            summary.DisplayValue.Should().Be("7");
            QuantityCadMetadataPolicy.Summarize(new[] { Row(null) }).Should().BeEmpty();
        }
    }
}
