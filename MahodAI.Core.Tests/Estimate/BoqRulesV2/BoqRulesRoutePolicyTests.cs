using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// 30.09 (Natali's CIVIL-WEST file): the rules route of the palette applies only to a drawing that is one declared source
/// of the project's rules (HA / DR / GM / SM for 6422). A corridor drawing of the same project keeps the standard route,
/// instead of a primary button that the rules export then refuses.
/// </summary>
public sealed class BoqRulesRoutePolicyTests
{
    private static readonly BoqRuleset Rules = BoqRuleset.LoadEmbedded6422();

    [Theory]
    [InlineData(@"C:\w\6422-boq-live-2909\6422-HA-MODEL-NATAZ_1.dwg")]
    [InlineData(@"C:\w\6422-boq-live-2909\6422-DR-MODEL-NATAZ.dwg")]
    [InlineData(@"C:\w\6422-boq-live-2909\6422-GM-MODEL-NATAZ_2.dwg")]
    [InlineData(@"C:\w\6422-boq-live-2909\6422-SM-MODEL-NATAZ_2.dwg")]
    public void TheFourDesignModelsOfTheProjectUseTheRulesRoute(string drawing) =>
        BoqRulesRoutePolicy.AppliesTo(Rules, Rules.Project, drawing).Should().BeTrue();

    [Theory]
    [InlineData(@"C:\w\civil-west-3009\6422-CIVIL-WEST-2026-08-04-UT.claude-copy.dwg")]
    [InlineData(@"C:\Users\arthurf\Downloads\6422-CIVIL-WEST-2026-08-04-UT.dwg")]
    [InlineData(null)]
    [InlineData("")]
    public void AnotherDrawingOfTheSameProjectKeepsTheStandardRoute(string? drawing) =>
        BoqRulesRoutePolicy.AppliesTo(Rules, Rules.Project, drawing).Should().BeFalse();

    [Fact]
    public void AnotherProjectKeepsTheStandardRouteEvenForAMatchingFileName()
    {
        BoqRulesRoutePolicy.AppliesTo(Rules, "9999", @"C:\w\6422-HA-MODEL-NATAZ_1.dwg").Should().BeFalse();
        BoqRulesRoutePolicy.AppliesTo(Rules, null, @"C:\w\6422-HA-MODEL-NATAZ_1.dwg").Should().BeFalse();
    }
}
