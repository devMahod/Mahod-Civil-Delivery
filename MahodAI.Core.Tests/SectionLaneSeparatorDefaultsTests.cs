using System.Collections.Generic;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

/// <summary>
/// Live 29/09: STA-42676 showed one 6.36 m "נתיב נסיעה" between the island and the
/// NATAZ line. SM draws TR-MARK-WHT-3-1.5-808 at +2.70 there (two lanes), but no
/// plan-mark rule recognised the Netivei Israel lane separators.
/// </summary>
public class SectionLaneSeparatorDefaultsTests
{
    // The live 6422 profile rules (29/09), so the defaults are tested behind them.
    private static readonly List<ProjectionRuleConfig> ProfileRules = new()
    {
        new("*TR-ISLAND*", null, "אי תנועה", "island", null),
        new("*BIKE*", null, "שביל אופניים", "bike", null),
        new("END-MDR*", null, "מדרכה", "sidewalk", null),
        new("HW-TRWY*", null, "שפת מיסעה", "lane", null),
        new("*TR-MARK-YLW-3-3*", null, "קו נת\"צ", "lane", null),
        new("*CURB*", null, "אבן שפה", "curb", null),
    };

    [Theory]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-3-1.5-808")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-808-3-3")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-808")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-801")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-3-3-801")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-801-250")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-803")]
    public void NetiveiIsraelLaneSeparators_AreLaneMarks(string layer)
    {
        var match = Classify(layer, "6422-SM-MODEL-NATAZ", WithObservedPlanMarkDefaults(ProfileRules));
        Assert.NotNull(match);
        Assert.Equal("lane", match!.Kind);
    }

    [Theory]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-809")]      // turn guide inside a junction
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-810")]      // stop line
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-810-STOP")]
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-811")]      // crossing stripes
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-WHT-815")]      // painted island hatching
    [InlineData("6422-SM-MODEL-NATAZ|TR-MARK-YLW-512")]      // bus-stop line
    public void TransverseOrInJunctionMarks_AreNotLaneBoundaries(string layer)
    {
        var match = Classify(layer, "6422-SM-MODEL-NATAZ", WithObservedPlanMarkDefaults(ProfileRules));
        Assert.True(match == null || match.Kind != "lane", layer + " must not split lanes");
    }

    [Fact]
    public void AnExplicitProfileRuleStillWinsOverTheDefault()
    {
        var rules = new List<ProjectionRuleConfig>(ProfileRules)
        {
            new("TR-MARK-WHT-808-3-3", null, "סימון", "mark", null),
        };
        var match = Classify("6422-SM-MODEL-NATAZ|TR-MARK-WHT-808-3-3", null, WithObservedPlanMarkDefaults(rules));
        Assert.Equal("mark", match!.Kind);
    }
}
