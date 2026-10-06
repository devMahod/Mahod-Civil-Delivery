using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public class SectionCappedEndpointInvariantTests
{
    [Theory]
    [InlineData(-100, -100, 100, 100)]
    [InlineData(100, 100, -100, -100)]
    [InlineData(-100, -100, 10, 10)]
    [InlineData(-100, 0, 100, 0)]
    [InlineData(-25, -25, 25, 25)]
    public void CappedOffsetsAndExtentsDescribeTheRetainedEndpoints(
        double ax, double ay, double bx, double by)
    {
        var record = Resolve(ax, ay, bx, by);
        var endpoints = record.Cl.WcsEndpoints;
        var expected = SectionMath.ExtentsFromEndpoints(
            new Pt2(endpoints[0], endpoints[1]),
            new Pt2(endpoints[2], endpoints[3]), new Pt2(0, 0), 90);

        record.EndpointOffsetA.Should().Be(Math.Round(expected.OffsetA, 4));
        record.EndpointOffsetB.Should().Be(Math.Round(expected.OffsetB, 4));
        record.LeftExtent.Should().Be(Math.Round(expected.LeftExtent, 4));
        record.RightExtent.Should().Be(Math.Round(expected.RightExtent, 4));
        Math.Sqrt(endpoints[0] * endpoints[0] + endpoints[1] * endpoints[1])
            .Should().BeLessThanOrEqualTo(31.00000001);
        Math.Sqrt(endpoints[2] * endpoints[2] + endpoints[3] * endpoints[3])
            .Should().BeLessThanOrEqualTo(31.00000001);
        record.Cl.SourceEndpoints.Should().Equal(new[] { ax, ay, bx, by },
            "capping the working cut must preserve the original source evidence");
    }

    [Fact]
    public void CappedPerpendicularMetadataInterpolatesBackToTheSameWorldPoint()
    {
        var record = Resolve(-100, -100, 100, 100);
        var e = record.Cl.WcsEndpoints;
        var laneOffset = 10d;
        var t = (laneOffset - record.EndpointOffsetA!.Value) /
            (record.EndpointOffsetB!.Value - record.EndpointOffsetA.Value);
        var x = e[0] + t * (e[2] - e[0]);
        var y = e[1] + t * (e[3] - e[1]);

        x.Should().BeApproximately(10, 0.0001,
            "perpendicular metadata must still reconstruct the retained source geometry");
        y.Should().BeApproximately(10, 0.0001);
        record.LeftExtent.Should().BeApproximately(31 / Math.Sqrt(2), 0.0001);
        record.RightExtent.Should().BeApproximately(31 / Math.Sqrt(2), 0.0001);
    }

    private static SectionPlanRecord Resolve(double ax, double ay, double bx, double by)
    {
        var record = new SectionPlanRecord
        {
            RecordId = "cap-fixture",
            Cl = new ClSourceRecord
            {
                RecordId = "cap-fixture", SourceHandle = "1A", SourceDrawing = "source.dwg", SourceLayer = "CL",
                SourceDrawingHash = "source-hash", SourceEntityType = "LINE",
                SourceEndpoints = new[] { ax, ay, bx, by },
                WcsEndpoints = new[] { ax, ay, bx, by },
            },
        };
        var profile = new ProjectProfile { ProfileId = "cap-fixture" };
        profile.Sections.Projection.MaxHalfWidthM = 31;
        SectionPlanLogic.ResolveAlignment(record, new[]
        {
            new AlignmentCrossing
            {
                AlignmentName = "axis", Point = new[] { 0d, 0d },
                Station = 100, TangentDeg = 90,
            },
        }, profile);
        return record;
    }
}
