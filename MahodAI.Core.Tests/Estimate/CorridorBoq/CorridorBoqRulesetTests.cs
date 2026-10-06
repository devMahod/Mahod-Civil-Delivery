using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.CorridorBoq;

/// <summary>The embedded corridor rules of project 6422: codes, derived items and the 13 NTI 07-2026 prices as printed in the
/// official pricebook file (checked against המחירון-האחוד-יולי-2026-אתר-נתיבי-ישראל.xlsx on 30.09.2026).</summary>
public sealed class CorridorBoqRulesetTests
{
    [Fact]
    public void TheEmbeddedRulesLoadWithEveryItemPriced()
    {
        var r = CorridorBoqRuleset.LoadEmbedded6422();
        r.Project.Should().Be("6422");
        r.LinkCode.Should().Be("Bot");
        r.ExistingGroundSurface.Should().Be("MK");
        r.HisufDepthM.Should().Be(0.20);
        r.Pricebook.Id.Should().Be("nti-unified-2026-07");
        r.Pricebook.SourceSha256.Should().HaveLength(64);
        r.Codes.Keys.Should().BeEquivalentTo("ASF-5-19-70", "ASF-6-25-70", "ASF-7-25-68", "MAZA", "S5");
        r.Codes["ASF-5-19-70"].ThicknessM.Should().Be(0.05);
        r.Codes["ASF-7-25-68"].Item.Should().Be("51.04.0160");
        var prices = r.Pricebook.Items.ToDictionary(p => p.Key, p => p.Value.BasePrice);
        prices.Should().Contain(new System.Collections.Generic.Dictionary<string, decimal>
        {
            ["51.01.1061"] = 2m, ["51.02.0010"] = 21m, ["51.02.0020"] = 22m, ["51.02.0080"] = 49m, ["51.03.0010"] = 153m,
            ["51.03.0030"] = 87m, ["51.04.0350"] = 45m, ["51.04.0130"] = 50m, ["51.04.0160"] = 58m, ["51.04.1480"] = 1m,
            ["51.04.1470"] = 1m,
        });
    }

    [Fact]
    public void AnItemMissingFromThePricebookStopsTheRules()
    {
        using var stream = typeof(CorridorBoqRuleset).Assembly.GetManifestResourceStream(CorridorBoqRuleset.EmbeddedResource6422)!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(stream)!;
        node["pricebook"]!["items"]!.AsObject().Remove("51.04.1470");
        FluentActions.Invoking(() => CorridorBoqRuleset.Parse(node.ToJsonString())).Should()
            .Throw<System.IO.InvalidDataException>().WithMessage("*51.04.1470*");
    }
}
