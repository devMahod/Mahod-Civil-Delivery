using System;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionAnnotationResourceContracts;

namespace MahodAI.Core.Tests;

/// <summary>
/// Executable false-green tests: every live resource field participating in the
/// annotation contract is mutated independently. A new field cannot silently
/// become documentation-only while the validator continues returning green.
/// </summary>
public sealed class SectionAnnotationResourceContractTests
{
    [Fact]
    public void CurrentAnnotationResources_AreIsolatedFromLegacySharedDrawingResources()
    {
        AnnotationLayerName.Should().Be("MHD-SECT-ANNO-V6");
        AnnotationTextStyleName.Should().Be("MHD-ANNO-V6");
        IsKnownAnnotationLayer(AnnotationLayerName).Should().BeTrue();
        IsKnownAnnotationLayer(LegacyAnnotationLayerName).Should().BeTrue(
            "registered legacy entities remain a deterministic migration input");
        IsKnownAnnotationLayer("C-ROAD-ANNO").Should().BeFalse(
            "unregistered or foreign entities must never be adopted");
    }

    private static LayerState GoodLayer() => new(
        Exists: true,
        Dependent: false,
        IsOff: false,
        IsFrozen: false,
        IsPlottable: true,
        IsLocked: false,
        IsHidden: false,
        ViewportVisibilityDefault: false,
        HasViewportOverrides: false,
        ViewportScanComplete: true,
        FrozenPaperViewportCount: 0,
        ColorIndex: AnnotationLayerColorIndex,
        TransparencyIsByAlpha: true,
        TransparencyAlpha: OpaqueAlpha,
        LinetypeName: "Continuous",
        LineWeight: "ByLineWeightDefault",
        Annotative: "False");

    private static TextStyleState GoodStyle() => new(
        Exists: true,
        Typeface: AnnotationTypeface,
        IsShapeFile: false,
        BigFontFile: string.Empty,
        XScale: 1.0,
        ObliquingAngleRad: 0.0,
        IsVertical: false,
        FlagBits: 0,
        TextSize: 0.0,
        Bold: false,
        Italic: false,
        CharacterSet: 0,
        PitchAndFamily: 0,
        Annotative: "False",
        PaperOrientation: "False");

    private static LinetypeState GoodLinetype(LinetypeSpec spec) => new(
        Exists: true,
        Dependent: false,
        Description: spec.Description,
        IsScaledToFit: false,
        Annotative: "False",
        PatternLength: spec.PatternLength,
        Elements: spec.DashLengths.Select(dash => new LinetypeElementState(
            DashLength: dash,
            ShapeNumber: 0,
            HasShapeStyle: false,
            Text: string.Empty,
            OffsetX: 0.0,
            OffsetY: 0.0,
            Scale: 1.0,
            Rotation: 0.0,
            IsUcsOriented: false,
            IsUpright: false)).ToArray());

    [Fact]
    public void FullyCompliantResources_PassCleanly()
    {
        ValidateLayer(GoodLayer()).Should().BeEmpty();
        ValidateTextStyle(GoodStyle()).Should().BeEmpty();
        foreach (var spec in RequiredLinetypeSpecs)
            ValidateLinetype(spec, GoodLinetype(spec)).Should().BeEmpty(spec.Name);
    }

    [Fact]
    public void LayerContract_EverySingleFieldMutationFailsWithItsOwnClause()
    {
        var mutations = new (string Name, LayerState State, string Error)[]
        {
            (nameof(LayerState.Exists), GoodLayer() with { Exists = false }, "missing"),
            (nameof(LayerState.Dependent), GoodLayer() with { Dependent = true }, "xref-dependent"),
            (nameof(LayerState.IsOff), GoodLayer() with { IsOff = true }, "off"),
            (nameof(LayerState.IsFrozen), GoodLayer() with { IsFrozen = true }, "frozen"),
            (nameof(LayerState.IsPlottable), GoodLayer() with { IsPlottable = false }, "not-plottable"),
            (nameof(LayerState.IsLocked), GoodLayer() with { IsLocked = true }, "locked"),
            (nameof(LayerState.IsHidden), GoodLayer() with { IsHidden = true }, "hidden"),
            (nameof(LayerState.ViewportVisibilityDefault), GoodLayer() with { ViewportVisibilityDefault = true }, "new-viewport-frozen"),
            (nameof(LayerState.HasViewportOverrides), GoodLayer() with { HasViewportOverrides = true }, "viewport-overrides"),
            (nameof(LayerState.ViewportScanComplete), GoodLayer() with { ViewportScanComplete = false }, "viewport-scan-incomplete"),
            (nameof(LayerState.FrozenPaperViewportCount), GoodLayer() with { FrozenPaperViewportCount = 1 }, "frozen-paper-viewports=1"),
            (nameof(LayerState.ColorIndex), GoodLayer() with { ColorIndex = 5 }, "color=5 (expected 7)"),
            (nameof(LayerState.TransparencyIsByAlpha), GoodLayer() with { TransparencyIsByAlpha = false }, "transparency-mode-not-alpha"),
            (nameof(LayerState.TransparencyAlpha), GoodLayer() with { TransparencyAlpha = 25 }, "transparency-alpha=25 (expected opaque 255)"),
            (nameof(LayerState.LinetypeName), GoodLayer() with { LinetypeName = "ByLayer" }, "linetype=ByLayer (expected Continuous)"),
            (nameof(LayerState.LineWeight), GoodLayer() with { LineWeight = "LineWeight050" }, "lineweight=LineWeight050 (expected ByLineWeightDefault)"),
            (nameof(LayerState.Annotative), GoodLayer() with { Annotative = "True" }, "annotative"),
        };

        using var scope = new AssertionScope();
        foreach (var mutation in mutations)
            ValidateLayer(mutation.State).Should().Equal(
                new[] { mutation.Error }, mutation.Name);
    }

    [Fact]
    public void TextStyleContract_EverySingleFieldMutationFailsWithItsOwnClause()
    {
        var mutations = new (string Name, TextStyleState State, string Error)[]
        {
            (nameof(TextStyleState.Exists), GoodStyle() with { Exists = false }, "missing"),
            (nameof(TextStyleState.Typeface), GoodStyle() with { Typeface = "txt" }, "typeface=txt (expected Arial)"),
            (nameof(TextStyleState.IsShapeFile), GoodStyle() with { IsShapeFile = true }, "shape-file"),
            (nameof(TextStyleState.BigFontFile), GoodStyle() with { BigFontFile = "bigfont.shx" }, "bigfont=bigfont.shx"),
            (nameof(TextStyleState.XScale), GoodStyle() with { XScale = 0.01 }, "xscale=0.01 (expected 1)"),
            (nameof(TextStyleState.ObliquingAngleRad), GoodStyle() with { ObliquingAngleRad = 0.5 }, "oblique=0.5 (expected 0)"),
            (nameof(TextStyleState.IsVertical), GoodStyle() with { IsVertical = true }, "vertical"),
            (nameof(TextStyleState.FlagBits), GoodStyle() with { FlagBits = 2 }, "flags=2 (backwards/upside-down; expected 0)"),
            (nameof(TextStyleState.TextSize), GoodStyle() with { TextSize = 2.5 }, "fixed-height=2.5 (expected 0)"),
            (nameof(TextStyleState.Bold), GoodStyle() with { Bold = true }, "bold"),
            (nameof(TextStyleState.Italic), GoodStyle() with { Italic = true }, "italic"),
            (nameof(TextStyleState.CharacterSet), GoodStyle() with { CharacterSet = 177 }, "charset=177 (expected 0)"),
            (nameof(TextStyleState.PitchAndFamily), GoodStyle() with { PitchAndFamily = 34 }, "pitch-family=34 (expected 0)"),
            (nameof(TextStyleState.Annotative), GoodStyle() with { Annotative = "True" }, "annotative"),
            (nameof(TextStyleState.PaperOrientation), GoodStyle() with { PaperOrientation = "True" }, "paper-orientation"),
        };

        using var scope = new AssertionScope();
        foreach (var mutation in mutations)
            ValidateTextStyle(mutation.State).Should().Equal(
                new[] { mutation.Error }, mutation.Name);
    }

    [Fact]
    public void ProtectedLinetypeContract_EveryHeaderFieldMutationFails()
    {
        using var scope = new AssertionScope();
        foreach (var spec in RequiredLinetypeSpecs)
        {
            var good = GoodLinetype(spec);
            var mutations = new (string Name, LinetypeState State, string Error)[]
            {
                (nameof(LinetypeState.Exists), good with { Exists = false }, "missing"),
                (nameof(LinetypeState.Dependent), good with { Dependent = true }, "xref-dependent"),
                (nameof(LinetypeState.Description), good with { Description = "user definition" }, "description/provenance"),
                (nameof(LinetypeState.IsScaledToFit), good with { IsScaledToFit = true }, "scaled-to-fit"),
                (nameof(LinetypeState.Annotative), good with { Annotative = "True" }, "annotative"),
                (nameof(LinetypeState.PatternLength), good with { PatternLength = spec.PatternLength + 0.01 },
                    $"pattern-length={(spec.PatternLength + 0.01):R} (expected {spec.PatternLength:R})"),
                (nameof(LinetypeState.Elements), good with { Elements = good.Elements.Skip(1).ToArray() },
                    $"dash-count={good.Elements.Count - 1} (expected {good.Elements.Count})"),
            };

            foreach (var mutation in mutations)
                ValidateLinetype(spec, mutation.State).Should().Equal(new[] { mutation.Error },
                    $"{spec.Name}.{mutation.Name}");
        }
    }

    [Fact]
    public void ProtectedLinetypeContract_EveryDashElementFieldMutationFails()
    {
        using var scope = new AssertionScope();
        foreach (var spec in RequiredLinetypeSpecs)
        {
            var good = GoodLinetype(spec);
            var element = good.Elements[0];
            var expectedDash = spec.DashLengths[0];
            var mutations = new (string Name, LinetypeElementState Element, string Error)[]
            {
                (nameof(LinetypeElementState.DashLength), element with { DashLength = expectedDash + 0.01 },
                    $"dash[0]={(expectedDash + 0.01):R} (expected {expectedDash:R})"),
                (nameof(LinetypeElementState.ShapeNumber), element with { ShapeNumber = 1, HasShapeStyle = true },
                    "dash[0]-contains-shape-or-text (shape=1, text='' len=0, style=True)"),
                (nameof(LinetypeElementState.Text), element with { Text = "GAS", HasShapeStyle = true },
                    "dash[0]-contains-shape-or-text (shape=0, text='GAS' len=3, style=True)"),
            };

            foreach (var mutation in mutations)
            {
                var elements = good.Elements.ToArray();
                elements[0] = mutation.Element;
                ValidateLinetype(spec, good with { Elements = elements }).Should().Equal(
                    new[] { mutation.Error },
                    $"{spec.Name}.element[0].{mutation.Name}");
            }
        }
    }

    [Fact]
    public void RequiredLinetypes_AreProtectedNames_NotTrustedOfficeAliases()
    {
        MissingLinetypes(new[] { "Continuous", DashedLinetypeName, CenterLinetypeName })
            .Should().BeEmpty();
        MissingLinetypes(new[] { "Continuous", DashedLinetypeName.ToLowerInvariant() })
            .Should().Equal(CenterLinetypeName);
        MissingLinetypes(new[] { "DASHED2", "CENTER" })
            .Should().Equal(DashedLinetypeName, CenterLinetypeName);
    }

    [Fact]
    public void LinetypeFallbackToContinuous_IsAFailure()
    {
        LinetypeApplied(DashedLinetypeName, DashedLinetypeName).Should().BeTrue();
        LinetypeApplied(DashedLinetypeName, DashedLinetypeName.ToLowerInvariant()).Should().BeTrue();
        LinetypeApplied(DashedLinetypeName, "Continuous").Should().BeFalse();
        LinetypeApplied(DashedLinetypeName, "ByLayer").Should().BeFalse();
        LinetypeApplied(DashedLinetypeName, null).Should().BeFalse();
    }

    // -------------------------------------------- block definition fingerprint

    private static readonly string[] Entities =
        { "Polyline|layer=0|0,0|1,0|1,1", "Circle|layer=0|0.5,0.5,0.2" };

    private static BlockDefinitionFingerprintLogic.Header GoodHeader() => new(
        0, 0, 0, "Undefined", "Any", false, "False", "False");

    [Fact]
    public void BlockFingerprint_EveryHeaderFieldParticipates_EntityOrderDoesNot()
    {
        var header = GoodHeader();
        var baseline = BlockDefinitionFingerprintLogic.Compose(header, Entities);
        BlockDefinitionFingerprintLogic.Compose(header, Entities.Reverse()).Should().Be(baseline);

        var mutations = new (string Name, BlockDefinitionFingerprintLogic.Header Header)[]
        {
            (nameof(header.OriginX), header with { OriginX = 0.75 }),
            (nameof(header.OriginY), header with { OriginY = -0.75 }),
            (nameof(header.OriginZ), header with { OriginZ = 2.0 }),
            (nameof(header.Units), header with { Units = "Meters" }),
            (nameof(header.BlockScaling), header with { BlockScaling = "Uniform" }),
            (nameof(header.Explodable), header with { Explodable = true }),
            (nameof(header.Annotative), header with { Annotative = "True" }),
            (nameof(header.PaperOrientation), header with { PaperOrientation = "True" }),
        };

        using var scope = new AssertionScope();
        foreach (var mutation in mutations)
            BlockDefinitionFingerprintLogic.Compose(mutation.Header, Entities)
                .Should().NotBe(baseline, mutation.Name);
    }

    [Fact]
    public void EntityOnlyFingerprint_IsOrderIndependent_ButRejectsAnyEntityMutation()
    {
        var baseline = BlockDefinitionFingerprintLogic.ComposeEntities(Entities);
        BlockDefinitionFingerprintLogic.ComposeEntities(Entities.Reverse()).Should().Be(baseline);
        BlockDefinitionFingerprintLogic.ComposeEntities(new[]
            { Entities[0] + "|visible=False", Entities[1] }).Should().NotBe(baseline);
        BlockDefinitionFingerprintLogic.ComposeEntities(Array.Empty<string>()).Should().NotBe(baseline);
    }

    [Fact]
    public void ToolProvenance_AcceptsSourceCommentWithAnyFingerprintVersion_RejectsForeign()
    {
        const string source = "MahodAI office car FRONT source_sha256=" + "ab12";
        var v1 = source + "; geometry_sha256=" + new string('0', 64);
        BlockDefinitionFingerprintLogic.IsToolProvenance(source, source).Should().BeTrue("pre-fingerprint import");
        BlockDefinitionFingerprintLogic.IsToolProvenance(v1, source).Should().BeTrue("older fingerprint comment");
        BlockDefinitionFingerprintLogic.IsToolProvenance(source + "; geometry_sha256=" + new string('f', 64), source).Should().BeTrue();

        BlockDefinitionFingerprintLogic.IsToolProvenance(null, source).Should().BeFalse();
        BlockDefinitionFingerprintLogic.IsToolProvenance("", source).Should().BeFalse();
        BlockDefinitionFingerprintLogic.IsToolProvenance("Nataly's car block", source).Should().BeFalse();
        BlockDefinitionFingerprintLogic.IsToolProvenance(source + "; geometry_sha256=" + new string('0', 63), source).Should().BeFalse("malformed hash");
        BlockDefinitionFingerprintLogic.IsToolProvenance(source + "; geometry_sha256=" + new string('G', 64), source).Should().BeFalse("non-hex");
        BlockDefinitionFingerprintLogic.IsToolProvenance(source + " tampered", source).Should().BeFalse();
        BlockDefinitionFingerprintLogic.IsToolProvenance(v1, "").Should().BeFalse();
    }

    [Fact]
    public void NonFiniteOrigin_IsRefused()
    {
        var act = () => BlockDefinitionFingerprintLogic.Compose(
            GoodHeader() with { OriginX = double.NaN }, Entities);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
