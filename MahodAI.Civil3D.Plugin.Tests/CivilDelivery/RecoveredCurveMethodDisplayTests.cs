using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class RecoveredCurveMethodDisplayTests
{
    [Theory]
    [InlineData("hatch-line-arc-boundary-area", "שטח")]
    [InlineData("hatch-line-arc-boundary-area+xref-transform", "שטח")]
    [InlineData("hatch-linear-boundary-area+block-transform", "שטח")]
    [InlineData("polyline3d-length+xref-transform", "אורך")]
    [InlineData("block-count+xref-transform", "ספירה")]
    [InlineData("future-method+xref-transform", "future-method+xref-transform")]
    public void CompactCaption_PreservesFullTechnicalMethodInDetails(string method, string caption)
    {
        var row = new QuantityRowViewModel
        {
            RuleKey = "TEST|area", Layer = "TEST", EntityType = "HATCH", Method = method,
            ObjectCount = 1, Quantity = 1, Unit = "m2", MappingState = "UNMAPPED",
        };
        row.MethodDisplay.Should().Be(caption);
        row.Method.Should().Be(method);
    }
}
