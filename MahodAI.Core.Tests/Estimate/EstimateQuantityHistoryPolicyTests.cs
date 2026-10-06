using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateQuantityHistoryPolicyTests
{
    [Theory]
    [InlineData("scope changed")]
    [InlineData("catalog changed")]
    [InlineData("earthworks changed")]
    [InlineData("source changed")]
    [InlineData("replacement scan failed")]
    public void SameDrawingMeasurementsRemainHistoricalButGrantNoAuthority(string reason)
    {
        EstimateQuantityHistoryPolicy.CanRetain("C:/test/host.dwg", "c:/TEST/host.dwg", 5).Should().BeTrue();
        var state = EstimateQuantityHistoryPolicy.Presentation(reason);
        state.Summary.Should().Be("מדידה קודמת");
        state.Detail.Should().Contain("מדידה היסטורית — לא עדכנית").And.Contain(reason).And.Contain("סריקה חדשה");
        state.PriceDisplay.Should().BeNull();
        state.Severity.Should().NotBe("ok");
        state.MappingApproved.Should().BeFalse();
        state.BuiltReady.Should().BeFalse();
        state.MeasurementNeedsReview.Should().BeTrue();
    }

    [Theory]
    [InlineData("C:/one/host.dwg", "C:/two/host.dwg", 5)]
    [InlineData("C:/one/host.dwg", null, 5)]
    [InlineData(null, "C:/one/host.dwg", 5)]
    [InlineData("", "", 5)]
    [InlineData("C:/one/host.dwg", "C:/one/host.dwg", 0)]
    public void DifferentUnknownOrEmptyDrawingCannotRetainHistory(string? source, string? active, int count) =>
        EstimateQuantityHistoryPolicy.CanRetain(source, active, count).Should().BeFalse();
}
