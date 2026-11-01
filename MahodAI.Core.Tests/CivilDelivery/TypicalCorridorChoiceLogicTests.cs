using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Candidate = MahodAI.CivilDelivery.Shared.TypicalCorridorChoiceLogic.Candidate;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class TypicalCorridorChoiceLogicTests
{
    private static Candidate C(string name, bool outOfDate = false, params (double, double)[] regions) =>
        new(name, regions, outOfDate, new[] { name + " - Top" });

    [Fact]
    public void LiveGisha_TheCorridorWithoutRegionsLoses_ToTheOneThatCoversThePicks()
    {
        // Live 2026-10-26: Road34 had "Road34_Corridor" (no regions, a failed first build) and
        // "Road34_Corridor (2)" (0–691.81). The second one is the evidence, not a question.
        var candidates = new[] { C("Road34_Corridor (2)", false, (0, 691.81)), C("Road34_Corridor") };
        var choice = TypicalCorridorChoiceLogic.Choose(candidates, new[] { 120.5, 400, 691.81 }, null, null);
        choice.Chosen.Should().Be("Road34_Corridor (2)");
        choice.Reason.Should().Be(TypicalCorridorChoiceLogic.Reason.CoversStations);
        choice.AskAmong.Should().BeEmpty();
    }

    [Fact]
    public void SessionChoice_ThenProfile_ThenTheOnlyCorridor()
    {
        var candidates = new[] { C("A", false, (0, 100)), C("B", false, (0, 100)) };
        TypicalCorridorChoiceLogic.Choose(candidates, new[] { 50.0 }, "A", "b").Chosen.Should().Be("B");
        TypicalCorridorChoiceLogic.Choose(candidates, new[] { 50.0 }, "a", null).Chosen.Should().Be("A");
        TypicalCorridorChoiceLogic.Choose(candidates, new[] { 50.0 }, "missing", null).Reason
            .Should().Be(TypicalCorridorChoiceLogic.Reason.Ambiguous);
        TypicalCorridorChoiceLogic.Choose(new[] { C("Only") }, new[] { 50.0 }, null, null).Reason
            .Should().Be(TypicalCorridorChoiceLogic.Reason.OnlyCorridor);
    }

    [Fact]
    public void TwoCoveringCorridors_PreferTheUpToDateOne_ElseAskTheEngineer()
    {
        var stale = new[] { C("A", true, (0, 100)), C("B", false, (0, 100)) };
        TypicalCorridorChoiceLogic.Choose(stale, new[] { 10.0 }, null, null).Chosen.Should().Be("B");

        var both = new[] { C("A", false, (0, 100)), C("B", false, (0, 100)) };
        var ask = TypicalCorridorChoiceLogic.Choose(both, new[] { 10.0 }, null, null);
        ask.Chosen.Should().BeNull();
        ask.AskAmong.Should().Equal("A", "B");
    }

    [Fact]
    public void NoCoveringCorridor_IsReported_NotGuessed()
    {
        var candidates = new[] { C("A", false, (0, 100)), C("B", false, (200, 300)) };
        var choice = TypicalCorridorChoiceLogic.Choose(candidates, new[] { 50.0, 250.0 }, null, null);
        choice.Chosen.Should().BeNull();
        choice.AskAmong.Should().BeEmpty();
        choice.Reason.Should().Be(TypicalCorridorChoiceLogic.Reason.NoneCovers);
        TypicalCorridorChoiceLogic.Choose(Array.Empty<Candidate>(), new[] { 1.0 }, null, null).Reason
            .Should().Be(TypicalCorridorChoiceLogic.Reason.NoCorridor);
    }

    [Fact]
    public void Surfaces_AreTheCorridorsOwn_NotANameThatContainsTheCorridorName()
    {
        var tins = new[] { "MK", "Road34_Corridor (2) - Top", "Road34_Corridor - Top" };
        var (top, bottom) = TypicalCorridorChoiceLogic.Surfaces(new[] { "Road34_Corridor - Top" }, tins, null, null);
        top.Should().Be("Road34_Corridor - Top");
        bottom.Should().Be("Road34_Corridor - Top");

        var withDatum = TypicalCorridorChoiceLogic.Surfaces(
            new[] { "C1 - Top", "C1 - Datum" }, new[] { "C1 - Top", "C1 - Datum" }, null, null);
        withDatum.Top.Should().Be("C1 - Top");
        withDatum.Bottom.Should().Be("C1 - Datum");

        TypicalCorridorChoiceLogic.Surfaces(new[] { "C1 - Top" }, tins, "MK", null).Top.Should().Be("MK");
        TypicalCorridorChoiceLogic.Surfaces(Array.Empty<string>(), tins, null, null).Top.Should().BeNull();
    }

    private static bool Wildcard(string name, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(name,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static TypicalCorridorChoiceLogic.SurfaceChoice Ground(
        string[] tins, string? profile = null, string? session = null, string[]? patterns = null) =>
        TypicalCorridorChoiceLogic.ExistingGround(
            tins, new[] { "AXZ - Top", "MIKTA4 - Top" }, new[] { "AXZ", "MIKTA4" },
            patterns ?? new[] { "MK*", "*EG*" }, Wildcard, profile, session);

    [Fact]
    public void Live6426_TwoGroundSurfaces_AreAskedInTheWindow_NotAProfileEdit()
    {
        var choice = Ground(new[] { "MK-2025-11-18", "MK-2025-11-10", "AXZ - Top", "MIKTA4 - Top" });

        choice.Chosen.Should().BeNull();
        choice.AskAmong.Should().Equal("MK-2025-11-18", "MK-2025-11-10");
        choice.Problem.Should().BeNull();
    }

    [Fact]
    public void TheSessionChoice_ThenTheProfile_DecideAmongSeveral_WhenTheyExist()
    {
        var tins = new[] { "MK-2025-11-18", "MK-2025-11-10" };

        Ground(tins, profile: "MK-2025-11-18", session: "mk-2025-11-10").Chosen.Should().Be("MK-2025-11-10");
        Ground(tins, profile: "MK-2025-11-18").Chosen.Should().Be("MK-2025-11-18");
        Ground(tins, profile: "Gone", session: "Also gone").AskAmong.Should().HaveCount(2, "a name not in the drawing decides nothing");
    }

    [Fact]
    public void OneCandidate_IsTaken_PatternsFirst_AndNoneIsAProblem()
    {
        Ground(new[] { "MK", "Design", "AXZ - Top" }).Chosen.Should().Be("MK", "MK matches the pattern, Design does not");
        Ground(new[] { "Ground 2026", "AXZ - Top" }).Chosen.Should().Be("Ground 2026", "the only TIN that is no corridor's");
        Ground(new[] { "AXZ - Top", "AXZ datum Bot" }).Problem.Should().NotBeNullOrWhiteSpace();
    }
}
