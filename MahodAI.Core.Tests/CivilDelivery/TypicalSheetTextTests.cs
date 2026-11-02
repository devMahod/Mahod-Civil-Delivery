using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.CivilDelivery;

public class TypicalSheetTextTests
{
    [Theory]
    [InlineData("תחנה 79650.00", new[] { "תחנה", "79650.00" })]
    [InlineData("הסדרי תנועה — שלב 1", new[] { "הסדרי תנועה — שלב", "1" })]
    [InlineData("5 ס\"מ ASF-5-70", new[] { "5", "ס\"מ", "ASF-5-70" })]
    [InlineData("חתך אופייני", new[] { "חתך אופייני" })]
    [InlineData("ROAD-40", new[] { "ROAD-40" })]
    public void MixedLines_SplitIntoOneScriptRuns_InLogicalOrder(string text, string[] runs) =>
        TypicalSheetText.Runs(text).Should().Equal(runs);
}
