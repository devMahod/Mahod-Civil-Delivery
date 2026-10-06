using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionAnnotationResourceContracts;

namespace MahodAI.Core.Tests;

/// <summary>
/// Live 07/09 15:41: the protected MHD-DASHED2 record written at 11:21 came back
/// after the drawing was saved and reopened with a text-style pointer on its gap
/// element (offline DWG read: both elements persisted with the TEXT flag, empty
/// text, no style). The validator must judge what a dash draws, not the decoration
/// fields AutoCAD rewrites on a round trip.
/// </summary>
public sealed class SectionLinetypeRoundTripContractTests
{
    private static LinetypeElementState Plain(double dash) => new(
        DashLength: dash, ShapeNumber: 0, HasShapeStyle: false, Text: string.Empty,
        OffsetX: 0.0, OffsetY: 0.0, Scale: 1.0, Rotation: 0.0, IsUcsOriented: false, IsUpright: false);

    private static LinetypeState State(LinetypeSpec spec, params LinetypeElementState[] elements) => new(
        Exists: true, Dependent: false, Description: spec.Description, IsScaledToFit: false,
        Annotative: "NotApplicable", PatternLength: spec.PatternLength, Elements: elements);

    [Fact]
    public void ReopenedDrawing_StylePointerOnThePlainGapElement_IsNotADrift()
    {
        var spec = DashedLinetype;
        var live = State(spec, Plain(0.50), Plain(-0.25) with { HasShapeStyle = true });
        ValidateLinetype(spec, live).Should().BeEmpty();
    }

    [Theory]
    [InlineData("style", true, 0.0, 0.0, 1.0, 0.0, false, false)]
    [InlineData("offset", false, 0.1, -0.1, 1.0, 0.0, false, false)]
    [InlineData("scale", false, 0.0, 0.0, 0.0, 0.0, false, false)]
    [InlineData("rotation", false, 0.0, 0.0, 1.0, 0.5, false, false)]
    [InlineData("orientation", false, 0.0, 0.0, 1.0, 0.0, true, true)]
    public void PlainDashDecorationFields_DoNotChangeWhatTheDashDraws(
        string field, bool hasStyle, double offsetX, double offsetY, double scale, double rotation,
        bool ucs, bool upright)
    {
        foreach (var spec in RequiredLinetypeSpecs)
        {
            var elements = spec.DashLengths.Select(dash => Plain(dash) with
            {
                HasShapeStyle = hasStyle, OffsetX = offsetX, OffsetY = offsetY,
                Scale = scale, Rotation = rotation, IsUcsOriented = ucs, IsUpright = upright,
            }).ToArray();
            ValidateLinetype(spec, State(spec, elements)).Should().BeEmpty($"{spec.Name}/{field}");
            elements.Should().OnlyContain(element => !IsEmbeddedShapeOrText(element));
        }
    }

    [Fact]
    public void EmbeddedShapeOrText_StillFailsEvenWhenEveryDecorationFieldIsCanonical()
    {
        var spec = DashedLinetype;
        var shape = State(spec, Plain(0.50) with { ShapeNumber = 132, HasShapeStyle = true }, Plain(-0.25));
        var text = State(spec, Plain(0.50), Plain(-0.25) with { Text = "GAS", HasShapeStyle = true });
        ValidateLinetype(spec, shape).Should().Equal(new[] { "dash[0]-contains-shape-or-text (shape=132, text='' len=0, style=True)" });
        ValidateLinetype(spec, text).Should().Equal(new[] { "dash[1]-contains-shape-or-text (shape=0, text='GAS' len=3, style=True)" });
        IsEmbeddedShapeOrText(Plain(0.5) with { ShapeNumber = 132, HasShapeStyle = true }).Should().BeTrue();
        IsEmbeddedShapeOrText(Plain(0.5) with { Text = "GAS", HasShapeStyle = true }).Should().BeTrue();
    }

    [Fact]
    public void LiveReopenedRecord_ShapeCodeOneWithoutAStyle_IsAPlainGap()
    {
        // 07/09 16:55, working copy reopened, after the 1.2.47 in-place rewrite:
        // "dash[1]-contains-shape-or-text (shape=1, text='' len=0, style=False)".
        var spec = DashedLinetype;
        var live = State(spec, Plain(0.50), Plain(-0.25) with { ShapeNumber = 1 });
        ValidateLinetype(spec, live).Should().BeEmpty();
        IsEmbeddedShapeOrText(Plain(-0.25) with { ShapeNumber = 1 }).Should().BeFalse();
        IsEmbeddedShapeOrText(Plain(-0.25) with { Text = "x" }).Should().BeFalse("no style, nothing can be drawn");
        IsEmbeddedShapeOrText(Plain(-0.25) with { ShapeNumber = 1, HasShapeStyle = true }).Should().BeTrue();
    }

    [Fact]
    public void Evidence_NamesTheExactShapeAndTextAutoCadReturned()
    {
        DescribeText(null).Should().Be("null");
        DescribeText(string.Empty).Should().Be("'' len=0");
        DescribeText("A\u0001\u05D0").Should().Be("'A\\u0001\\u05D0' len=3");
        DescribeText(new string('x', 40)).Should().Be("'" + new string('x', 24) + "…' len=40");
    }

    [Fact]
    public void OnlyTheExactMahodDescriptionOnANonXrefRecord_IsToolProvenance()
    {
        var spec = DashedLinetype;
        var own = State(spec, Plain(0.50), Plain(-0.25));
        IsToolOwnedLinetype(spec, own).Should().BeTrue();
        IsToolOwnedLinetype(spec, own with { Description = "Dashed (.5x)" }).Should().BeFalse();
        IsToolOwnedLinetype(spec, own with { Dependent = true }).Should().BeFalse();
        IsToolOwnedLinetype(spec, own with { Exists = false }).Should().BeFalse();
        IsToolOwnedLinetype(spec, own with { Description = spec.Description.ToUpperInvariant() }).Should().BeFalse();
    }

    [Fact]
    public void DashLengthsAndPatternLength_RemainExact()
    {
        var spec = DashedLinetype;
        ValidateLinetype(spec, State(spec, Plain(0.50), Plain(-0.30)))
            .Should().Equal(new[] { "dash[1]=-0.3 (expected -0.25)" });
        ValidateLinetype(spec, State(spec, Plain(0.50), Plain(-0.25)) with { PatternLength = 0.8 })
            .Should().Equal(new[] { "pattern-length=0.8 (expected 0.75)" });
    }
}
