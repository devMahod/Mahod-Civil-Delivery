using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class TypicalPlanReferenceLogicTests
{
    private const string Pd = @"P:\data\6403\Drawing\Data Files\HW\PD\";

    [Theory]
    [InlineData("HW-IC_4NATIV-GM-M44", true)]
    [InlineData("HW-IC_00000-SM-M38", true)]
    [InlineData("TR-PD-TT-GM-S1", true)]
    [InlineData("H-h_seker tree 07.10.2024-MHD", true)]
    [InlineData("HW-IC_4NATIV-TX-M38", false)]
    [InlineData("HW-IC_00000-CL-M38", false)]
    [InlineData("HW-IC_000EV-M38", false)]
    [InlineData("SR-6191-4-MHD", false)]
    [InlineData("ST-KMR-ST-S01-ICHHMC00-PD-7021_DATA_BR-01_ALT_02-MHD", false)]
    public void TheReferencesACutReads_AreTheRoadsOwn(string name, bool carries) =>
        TypicalPlanReferenceLogic.Carries(name).Should().Be(carries);

    [Fact]
    public void AnUnloadedReference_IsLookedForAtItsPath_ThenBesideTheDrawing()
    {
        TypicalPlanReferenceLogic.Candidates(Pd + "HW-IC_4NATIV-GM-M44.dwg", @"C:\Users\x\Downloads")
            .Should().Equal(Pd + "HW-IC_4NATIV-GM-M44.dwg", @"C:\Users\x\Downloads\HW-IC_4NATIV-GM-M44.dwg",
                @"C:\Users\x\Downloads\PD\HW-IC_4NATIV-GM-M44.dwg");
        TypicalPlanReferenceLogic.Candidates(@"..\PD\A-GM-1.dwg", @"C:\p\DD")
            .Should().Equal(@"C:\p\PD\A-GM-1.dwg", @"C:\p\DD\A-GM-1.dwg", @"C:\p\DD\PD\A-GM-1.dwg");
        TypicalPlanReferenceLogic.Candidates("", @"C:\p").Should().BeEmpty();
    }

    [Fact]
    public void The6403Drawing_GetsTheMarkingPlanFromBesideItsGeometryPlan()
    {
        // Live 2026-11-04: 6403 references GM-M44 (unloaded) and no marking plan; the office's PD folder holds both.
        var folder = new[] { Pd + "HW-IC_4NATIV-GM-M44.dwg", Pd + "HW-IC_00000-SM-M38.dwg", Pd + "HW-IC_4NATIV-TX-M38.dwg", Pd + "TR-XX_1-SM-M1.dwg" };
        var refs = new (string, string?)[] { ("HW-IC_4NATIV-GM-M44", Pd + "HW-IC_4NATIV-GM-M44.dwg"), ("HW-IC_4NATIV-TX-M38", Pd + "HW-IC_4NATIV-TX-M38.dwg") };
        TypicalPlanReferenceLogic.MarkingPlans(refs, f => folder.Where(x => x.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .Should().Equal(Pd + "HW-IC_00000-SM-M38.dwg");
    }

    [Fact]
    public void AReferencedMarkingPlan_OrNoGeometryFile_AddsNothing()
    {
        var folder = new[] { Pd + "HW-IC_00000-SM-M38.dwg" };
        TypicalPlanReferenceLogic.MarkingPlans(
                new (string, string?)[] { ("HW-IC_4NATIV-GM-M44", Pd + "HW-IC_4NATIV-GM-M44.dwg"), ("HW-IC_00000-SM-M38", null) }, _ => folder)
            .Should().BeEmpty();
        TypicalPlanReferenceLogic.MarkingPlans(new (string, string?)[] { ("HW-IC_4NATIV-GM-M44", null) }, _ => folder)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheNotes_SayWhatWasOpenedAndThatTheDrawingIsBack()
    {
        TypicalPlanReferenceLogic.OpenedNoteHe(Array.Empty<string>(), Array.Empty<string>()).Should().BeNull();
        var note = TypicalPlanReferenceLogic.OpenedNoteHe(new[] { "HW-IC_4NATIV-GM-M44" }, new[] { "HW-IC_00000-SM-M38" });
        note.Should().Contain("HW-IC_4NATIV-GM-M44").And.Contain("HW-IC_00000-SM-M38").And.Contain("כפי שהיה");
        TypicalPlanReferenceLogic.NotFoundNoteHe(new List<string> { "HW-IC_4NATIV-GM-M44" }).Should().Contain("External References");
        TypicalPlanReferenceLogic.ProjectPrefix("HW-IC_00000-SM-M38.dwg").Should().Be("HW-IC");
    }
}
