using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.Evidence;
using Xunit;
using ColorValue = MahodAI.CivilDelivery.Estimate.Evidence.EffectiveColorPolicy.ColorValue;
using Subject = MahodAI.CivilDelivery.Estimate.Evidence.EffectiveColorPolicy.Subject;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>SYNTHETIC insertion chains; no AutoCAD. ACI values are the standard palette.</summary>
public sealed class EffectiveColorPolicyTests
{
    private static readonly ColorValue Red = ColorValue.FromAci(1);
    private static readonly ColorValue Green = ColorValue.FromAci(3);
    private static readonly ColorValue Blue = ColorValue.FromAci(5);
    private static readonly ColorValue White = ColorValue.FromAci(7);

    private static EvidenceValue Resolve(Subject entity, params Subject[] ancestorsInnermostFirst) =>
        EffectiveColorPolicy.Resolve(entity, ancestorsInnermostFirst);

    [Theory]
    [InlineData(1, "255,0,0")]
    [InlineData(2, "255,255,0")]
    [InlineData(3, "0,255,0")]
    [InlineData(4, "0,255,255")]
    [InlineData(5, "0,0,255")]
    [InlineData(6, "255,0,255")]
    [InlineData(7, "255,255,255")]
    [InlineData(8, "128,128,128")]
    [InlineData(9, "192,192,192")]
    [InlineData(10, "255,0,0")]
    [InlineData(11, "255,127,127")]
    [InlineData(12, "204,0,0")]
    [InlineData(13, "204,102,102")]
    [InlineData(14, "153,0,0")]
    [InlineData(15, "153,76,76")]
    [InlineData(16, "127,0,0")]
    [InlineData(17, "127,63,63")]
    [InlineData(18, "76,0,0")]
    [InlineData(19, "76,38,38")]
    [InlineData(20, "255,63,0")]
    [InlineData(21, "255,159,127")]
    [InlineData(30, "255,127,0")]
    [InlineData(31, "255,191,127")]
    [InlineData(40, "255,191,0")]
    [InlineData(50, "255,255,0")]
    [InlineData(60, "191,255,0")]
    [InlineData(61, "223,255,127")]
    [InlineData(90, "0,255,0")]
    [InlineData(130, "0,255,255")]
    [InlineData(140, "0,191,255")]
    [InlineData(150, "0,127,255")]
    [InlineData(170, "0,0,255")]
    [InlineData(200, "191,0,255")]
    [InlineData(210, "255,0,255")]
    [InlineData(240, "255,0,63")]
    [InlineData(249, "76,38,47")]
    [InlineData(250, "51,51,51")]
    [InlineData(251, "80,80,80")]
    [InlineData(252, "105,105,105")]
    [InlineData(253, "130,130,130")]
    [InlineData(254, "190,190,190")]
    [InlineData(255, "255,255,255")]
    public void AciPaletteMatchesTheStandardTable(int index, string rgb) =>
        EffectiveColorPolicy.AciToRgbText(index).Should().Be(rgb);

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(-1)]
    public void ByBlockAndByLayerIndexesAreNotPaletteColours(int index) =>
        EffectiveColorPolicy.AciToRgbText(index).Should().BeNull();

    [Fact]
    public void ExplicitColoursResolveDirectly()
    {
        Resolve(new Subject(ColorValue.FromRgb(12, 34, 56), "ROAD", Blue)).Should().Be(EvidenceValue.Read("12,34,56"));
        Resolve(new Subject(Red, "ROAD", Blue), new Subject(Green, "X", Green)).Json.Should().Be("255,0,0");
    }

    [Fact]
    public void ByLayerUsesTheEntityLayerAndLayerZeroFollowsTheInsertLayer()
    {
        Resolve(new Subject(ColorValue.ByLayer, "ROAD", Blue)).Json.Should().Be("0,0,255");
        Resolve(new Subject(ColorValue.ByLayer, "0", White)).Json.Should().Be("255,255,255",
            "layer 0 at model-space root is an ordinary layer");

        // Layer 0 ByLayer adopts the INSERT's layer, not the INSERT's own explicit colour.
        Resolve(new Subject(ColorValue.ByLayer, "0", White), new Subject(Red, "ROAD", Blue))
            .Json.Should().Be("0,0,255");
        // ...recursively through an INSERT that is itself on layer 0.
        Resolve(new Subject(ColorValue.ByLayer, "0", White),
                new Subject(ColorValue.ByLayer, "0", White),
                new Subject(Red, "CURB", Green))
            .Json.Should().Be("0,255,0");
        // A named layer inside a block keeps its own colour.
        Resolve(new Subject(ColorValue.ByLayer, "SYNTHETIC-GM|MARKING", ColorValue.FromAci(30)), new Subject(Red, "ROAD", Blue))
            .Json.Should().Be("255,127,0");
    }

    [Fact]
    public void ByBlockFollowsTheInnermostInsertRecursively()
    {
        Resolve(new Subject(ColorValue.ByBlock, "0", White), new Subject(Red, "ROAD", Blue))
            .Json.Should().Be("255,0,0");
        Resolve(new Subject(ColorValue.ByBlock, "SIGN", White), new Subject(ColorValue.ByLayer, "SIGNS", ColorValue.FromRgb(10, 20, 30)))
            .Json.Should().Be("10,20,30");
        Resolve(new Subject(ColorValue.ByBlock, "0", White),
                new Subject(ColorValue.ByBlock, "0", White),
                new Subject(Green, "CURB", Blue))
            .Json.Should().Be("0,255,0");
        Resolve(new Subject(ColorValue.ByBlock, "0", White),
                new Subject(ColorValue.ByLayer, "0", White),
                new Subject(Red, "CURB", Green))
            .Json.Should().Be("0,255,0", "the INSERT is on layer 0, so it follows its own parent's layer");
    }

    [Fact]
    public void ByBlockWithNoInsertAboveIsUnavailableNotAGuessedForeground()
    {
        Resolve(new Subject(ColorValue.ByBlock, "ROAD", Blue))
            .Should().Be(EvidenceValue.Unavailable("byblock-at-root"));
        Resolve(new Subject(ColorValue.ByBlock, "0", White), new Subject(ColorValue.ByBlock, "ROAD", Blue))
            .Status.Should().Be("unavailable:byblock-at-root");
    }

    [Fact]
    public void UnreadableOrUnsupportedColoursAreUnavailable()
    {
        Resolve(new Subject(ColorValue.ByLayer, "ROAD", null)).Status.Should().Be("unavailable:layer-color-unread");
        Resolve(new Subject(ColorValue.ByLayer, "0", White), new Subject(Red, "ROAD", null))
            .Status.Should().Be("unavailable:layer-color-unread");
        Resolve(new Subject(ColorValue.Unsupported("Foreground"), "ROAD", Blue))
            .Status.Should().Be("unavailable:unsupported-color-method:Foreground");
        Resolve(new Subject(ColorValue.FromAci(0), "ROAD", Blue)).Status.Should().Be("unavailable:aci-out-of-range");
        Resolve(new Subject(ColorValue.ByLayer, "ROAD", ColorValue.ByBlock))
            .Status.Should().Be("unavailable:layer-color-not-explicit");
        Resolve(new Subject(ColorValue.ByLayer, "ROAD", Blue)).Json.Should().NotBeNull();
    }
}
