using System;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// 984 (06.10): the CL drawing marks stations with 0.5 m ticks (exploded AeccTickLine). As section lines they gave
/// ±0.25 m sections. The "station-markers" mode is chosen by an engineer with a positive half-width (Codex 11:17:
/// never inferred from length, never defaulted); the ordinary mode only warns about a short line.
/// </summary>
public class SectionStationMarkerTests
{
    // A 0.5 m tick across an axis that runs along +Y (tangent 90°), centred on the crossing at (0,0).
    private static SectionPlanRecord Resolve(ProjectProfile profile, double ax = -0.25, double bx = 0.25)
    {
        var record = new SectionPlanRecord
        {
            RecordId = "tick-fixture",
            Cl = new ClSourceRecord
            {
                RecordId = "tick-fixture", SourceHandle = "AeccTickLine/1", SourceDrawing = "HW-FoorGrd-CL-M30.dwg",
                SourceLayer = "HW-ALGN-SEC-NAME", SourceDrawingHash = "cl-hash", SourceEntityType = "LINE",
                SourceEndpoints = new[] { ax, 0d, bx, 0d },
                WcsEndpoints = new[] { ax, 0d, bx, 0d },
            },
        };
        SectionPlanLogic.ResolveAlignment(record, new[]
        {
            new AlignmentCrossing { AlignmentName = "100", Point = new[] { 0d, 0d }, Station = 25, TangentDeg = 90 },
        }, profile);
        return record;
    }

    private static ProjectProfile StationMarkers(double? halfWidth)
    {
        var profile = new ProjectProfile { ProfileId = "984" };
        profile.Sections.Cl.Mode = SectionStationMarkerLogic.ModeStationMarkers;
        profile.Sections.Cl.StationMarkerHalfWidthM = halfWidth;
        profile.Sections.Cl.StationMarkerApprovedBy = "בודק";
        return profile;
    }

    [Fact]
    public void A_tick_becomes_a_section_of_the_approved_half_width_in_its_own_direction()
    {
        var record = Resolve(StationMarkers(12.5));

        record.Cl.WcsEndpoints.Should().Equal(new[] { -12.5, 0d, 12.5, 0d });
        record.LeftExtent.Should().BeApproximately(12.5, 1e-9);
        record.RightExtent.Should().BeApproximately(12.5, 1e-9);
        record.Cl.SourceEndpoints.Should().Equal(new[] { -0.25, 0d, 0.25, 0d }, "the drawn tick stays the source evidence");
        record.Findings.Should().Contain(f => f.Severity == FindingSeverity.Info && f.Title.Contains("±12.5"));
        record.Status.Should().Be(DeliveryStatus.Ready);
    }

    [Fact]
    public void Extension_is_centred_on_the_alignment_crossing_not_on_the_tick_midpoint()
    {
        // A tick drawn off-centre (crossing at 0, tick from -0.1 to 0.4): the section is still ±W around the axis.
        var record = Resolve(StationMarkers(10), ax: -0.1, bx: 0.4);
        record.Cl.WcsEndpoints.Should().Equal(new[] { -10d, 0d, 10d, 0d });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(250.0)]  // a unit mistake (cm typed as m) is refused, not clamped
    public void A_missing_or_invalid_half_width_blocks_the_record_for_review(double? halfWidth)
    {
        var record = Resolve(StationMarkers(halfWidth));
        record.Status.Should().NotBe(DeliveryStatus.Ready);
        record.Findings.Should().Contain(f => f.Severity == FindingSeverity.ReviewRequired);
        record.Cl.WcsEndpoints.Should().Equal(new[] { -0.25, 0d, 0.25, 0d }, "nothing is extended without a valid width");
    }

    [Fact]
    public void A_degenerate_tick_direction_is_refused()
    {
        SectionStationMarkerLogic.TryExtend(new Pt2(1, 1), new Pt2(1, 1), new Pt2(1, 1), 10,
            out _, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("מנוון");
    }

    [Fact]
    public void The_ordinary_mode_never_extends_a_short_line_and_only_warns()
    {
        var record = Resolve(new ProjectProfile { ProfileId = "984" });
        record.Cl.WcsEndpoints.Should().Equal(new[] { -0.25, 0d, 0.25, 0d });
        record.Findings.Should().Contain(f => f.Severity == FindingSeverity.Warning && f.Message.Contains("סימוני תחנות"));
        record.Findings.Should().NotContain(f => f.Title.Contains("הורחב"));
    }

    [Fact]
    public void A_long_ordinary_line_gets_no_short_line_warning()
    {
        var record = Resolve(new ProjectProfile { ProfileId = "6422" }, ax: -15, bx: 15);
        record.Findings.Should().NotContain(f => f.Title.Contains("קו CL קצר"));
    }

    [Fact]
    public void Setup_saves_the_mode_only_with_a_valid_width_and_clears_it_otherwise()
    {
        var profile = new ProjectProfile { ProfileId = "984" };
        ProjectSetupService.ApplySelection(profile, new ProjectSetupSelection
        {
            ClLayers = { "HW-ALGN-SEC-NAME" }, ClSourceFiles = { "HW-FoorGrd-CL-M30.dwg" },
            ClLayerScope = ClInstructionReader.LayerScopeSourceFile,
            ClMode = SectionStationMarkerLogic.ModeStationMarkers, StationMarkerHalfWidthM = 12.5, ApprovedBy = "מהנדס",
        });
        profile.Sections.Cl.Mode.Should().Be(SectionStationMarkerLogic.ModeStationMarkers);
        profile.Sections.Cl.StationMarkerHalfWidthM.Should().Be(12.5);
        profile.Sections.Cl.StationMarkerApprovedBy.Should().Be("מהנדס");
        profile.Sections.Cl.LayerScope.Should().Be(ClInstructionReader.LayerScopeSourceFile);

        ProjectSetupService.ApplySelection(profile, new ProjectSetupSelection { ClLayers = { "GFC111" } });
        profile.Sections.Cl.Mode.Should().BeNull("setup is a complete snapshot");
        profile.Sections.Cl.StationMarkerHalfWidthM.Should().BeNull();
        profile.Sections.Cl.StationMarkerApprovedBy.Should().BeNull();
        profile.Sections.Cl.LayerScope.Should().BeNull();

        var invalid = () => ProjectSetupService.ApplySelection(profile, new ProjectSetupSelection
        {
            ClLayers = { "HW-ALGN-SEC-NAME" }, ClMode = SectionStationMarkerLogic.ModeStationMarkers,
            StationMarkerHalfWidthM = 0, ApprovedBy = "מהנדס",
        });
        invalid.Should().Throw<InvalidOperationException>();
    }
}
