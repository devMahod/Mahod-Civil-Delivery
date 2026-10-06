using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.AnnotationLinetypeDisplayLogic;

namespace MahodAI.Core.Tests;

/// <summary>
/// The protected 0.50 m dash must LOOK like 0.50 m under every LTSCALE / MSLTSCALE /
/// annotation-scale combination, and the evidence string must change whenever any of
/// those inputs changes (Claude review of 1.2.28, finding 2).
/// </summary>
public sealed class AnnotationLinetypeDisplayLogicTests
{
    private const double Dash = 0.50;

    private static DrawingState State(double ltscale, bool msltscale, double paper, double drawing) =>
        new(ltscale, msltscale, $"{paper}:{drawing}", paper, drawing, paper / drawing);

    [Theory]
    [InlineData(1.0, false, 1, 1)]
    [InlineData(1000.0, false, 1, 1)]      // office habit: LTSCALE = plot scale, MSLTSCALE off
    [InlineData(1.0, true, 1, 100)]        // template default: MSLTSCALE on, 1:100
    [InlineData(1.0, true, 1, 1000)]       // 1:1000 plan scale left active in model space
    [InlineData(0.5, true, 1, 200)]
    [InlineData(1.0, true, 1, 1)]
    [InlineData(2.0, true, 2, 1)]          // 2:1 detail scale
    public void EntityScale_CancelsEveryFactor_SoTheDashIsAlwaysNominal(
        double ltscale, bool msltscale, double paper, double drawing)
    {
        var state = State(ltscale, msltscale, paper, drawing);
        var entityScale = EntityScale(state);
        VisibleLength(Dash, state, entityScale).Should().BeApproximately(Dash, 1e-9);
    }

    [Fact]
    public void TheOldFormula_RendersFiftyMetresAtOneToHundred_WithMsltscaleOn()
    {
        // 1/LTSCALE (the 1.2.28 formula) ignores the annotation scale: at 1:100 the
        // "deterministic" 0.5 m dash is 50 m long — a visually continuous line.
        var state = State(1.0, true, 1, 100);
        VisibleLength(Dash, state, 1.0 / state.Ltscale).Should().BeApproximately(50.0, 1e-9);
        EntityScale(state).Should().BeApproximately(0.01, 1e-12);
    }

    [Fact]
    public void MsltscaleOff_UsesOnlyLtscale()
    {
        EntityScale(State(1000.0, false, 1, 100)).Should().BeApproximately(0.001, 1e-15);
        // Annotation-scale garbage is irrelevant while MSLTSCALE is off.
        Validate(new DrawingState(1.0, false, null, double.NaN, double.NaN, double.NaN)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0.0, true, 1, 100, 0.01, "ltscale=")]
    [InlineData(double.NaN, false, 1, 1, 1, "ltscale=")]
    [InlineData(1.0, true, 0, 100, 0.01, "cannoscale-paper-units=")]
    [InlineData(1.0, true, 1, 0, 0.01, "cannoscale-drawing-units=")]
    [InlineData(1.0, true, 1, 100, 0.0, "cannoscale-value=")]
    [InlineData(1.0, true, 1, 100, double.NaN, "cannoscale-value=")]
    public void UnusableState_FailsClosedWithItsOwnClause(
        double ltscale, bool msltscale, double paper, double drawing, double value, string clause)
    {
        var state = new DrawingState(ltscale, msltscale, "x", paper, drawing, value);
        Validate(state).Should().Contain(e => e.StartsWith(clause));
        var act = () => EntityScale(state);
        act.Should().Throw<InvalidOperationException>().WithMessage("*" + clause + "*");
    }

    [Fact]
    public void HostValueThatContradictsItsOwnUnits_IsRefused()
    {
        // If the API reported drawing/paper (100) instead of paper/drawing (0.01) the
        // entity scale would be 10,000× off; the contract must refuse, not draw.
        var inverted = new DrawingState(1.0, true, "1:100", 1, 100, 100.0);
        Validate(inverted).Should().ContainSingle(e => e.Contains("inconsistent with paper/drawing=0.01"));
        Describe(inverted).Should().EndWith(";entity_scale=(invalid)");
    }

    [Fact]
    public void Describe_IsCanonical_AndChangesWithEveryInput()
    {
        var baseline = State(1.0, true, 1, 100);
        var text = Describe(baseline);
        text.Should().Be("ltdisplay-v1;ltscale=1;msltscale=1;cannoscale=1:100;paper=1;drawing=100;value=0.01;entity_scale=0.01");

        Describe(baseline with { Ltscale = 2.0 }).Should().NotBe(text);
        Describe(baseline with { Msltscale = false }).Should().NotBe(text);
        Describe(State(1.0, true, 1, 1000)).Should().NotBe(text);
        Describe(baseline with { ScaleName = "1:100 (custom)" }).Should().NotBe(text);
        Describe(baseline with { ScaleName = null }).Should().Contain(";cannoscale=(none);");

        // MSLTSCALE off: the annotation scale is not part of the contract, so changing
        // it later cannot fail VERIFY for a section whose dashes did not move.
        var off = State(1000.0, false, 1, 100);
        Describe(off).Should().Be("ltdisplay-v1;ltscale=1000;msltscale=0;cannoscale=(n/a);paper=(n/a);drawing=(n/a);value=(n/a);entity_scale=0.001");
        Describe(State(1000.0, false, 1, 500)).Should().Be(Describe(off));
        Describe(new DrawingState(1000.0, false, null, double.NaN, double.NaN, double.NaN)).Should().Be(Describe(off));
    }
}
