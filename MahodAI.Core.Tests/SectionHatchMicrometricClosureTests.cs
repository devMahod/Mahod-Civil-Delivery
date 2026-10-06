using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using NetTopologySuite.Geometries;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionHatchBoundaryGeometry;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public class SectionHatchMicrometricClosureTests
{
    private const string SourceSha = "5C23EC4AEF9ECC3A6A3CF75EDAB8B9ADCA60B7344AA941ACF1EB9B1186290539";
    private const string FixtureSha = "DCB2F17980C0E6E211D8C68E8AEB63B9C79F8E382447EE59839045FC330AF6D4";
    private static readonly GeometryFactory Factory = new();

    [Fact]
    public void Current262391_HasOneUnflaggedLoopWithSubMicrometricTerminalMismatch_AndValidCompleteRegion()
    {
        using var fixture = Fixture();
        var record = Record(fixture, "262391");
        record.GetProperty("loops").GetArrayLength().Should().Be(1);
        var loop = record.GetProperty("loops")[0];
        loop.GetProperty("flags").GetString().Should().Be("External");
        var edges = CurveEdges(loop);
        edges.Should().HaveCount(105);
        var gap = Distance(edges[^1][^1], edges[0][0]);
        gap.Should().BeApproximately(2.2887856434147577e-7, 1e-12);
        gap.Should().BeGreaterThan(ClosureToleranceM).And.BeLessThan(MicrometricEndpointToleranceM);
        for (var i = 1; i < edges.Count; i++)
            Distance(edges[i - 1][^1], edges[i][0]).Should().BeLessThan(1e-10);
        Action strict = () => JoinClosedEdges(edges);
        strict.Should().Throw<ArgumentException>().WithMessage("*not geometrically closed*");

        var points = JoinClosedEdgesWithEndpointTolerance(edges, endpointToleranceM: MicrometricEndpointToleranceM);
        points[0].Should().Be(edges[0][0], "recognition retains the actual first endpoint, never averages it");
        Polygon(points).IsValid.Should().BeTrue();
        Polygon(points).Area.Should().BeGreaterThan(1);
        var region = Region(new[] { points });
        region.Loops.Should().ContainSingle();
        region.FilledSeams.Should().BeEmpty();
    }

    [Theory]
    [InlineData("26236C", 8)]
    [InlineData("262379", 1)]
    [InlineData("262393", 0)]
    public void CurrentRealDisconnectedLoops_StillFailMicrometricEndpointRecognition(string handle, int loopIndex)
    {
        using var fixture = Fixture();
        var edges = CurveEdges(Record(fixture, handle).GetProperty("loops")[loopIndex]);
        Action read = () => JoinClosedEdgesWithEndpointTolerance(edges, endpointToleranceM: MicrometricEndpointToleranceM);
        read.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CurrentSevenFailureFixture_DoesNotTurnOtherTopologyFailuresGreen()
    {
        using var fixture = Fixture();
        var readable = new List<string>();
        foreach (var record in fixture.RootElement.GetProperty("records").EnumerateArray())
        {
            try
            {
                var loops = new List<IReadOnlyList<P2>>();
                foreach (var loop in record.GetProperty("loops").EnumerateArray())
                {
                    if ((loop.GetProperty("flags_code").GetInt32() & (0x20 | 0x40 | 0x100 | 8 | 0x80)) != 0)
                        throw new InvalidOperationException("Flagged loop remains rejected before endpoint recognition.");
                    loop.GetProperty("is_polyline").GetBoolean().Should().BeFalse();
                    loops.Add(JoinClosedEdgesWithEndpointTolerance(CurveEdges(loop), endpointToleranceM: MicrometricEndpointToleranceM));
                }
                Region(loops).Loops.Should().HaveCount(record.GetProperty("loops").GetArrayLength());
                readable.Add(record.GetProperty("handle").GetString()!);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        readable.Should().Equal("262391");
    }

    [Fact]
    public void MicrometricRecognition_PreservesHoleAndRejectsSelfIntersection()
    {
        var outer = JoinClosedEdgesWithEndpointTolerance(BoxEdges(0, 0, 10, 10, 0.25e-6), endpointToleranceM: MicrometricEndpointToleranceM);
        var hole = JoinClosedEdgesWithEndpointTolerance(BoxEdges(3, 3, 7, 7, 0.25e-6), endpointToleranceM: MicrometricEndpointToleranceM);
        var region = Region(new[] { outer, hole });
        region.Loops.Should().HaveCount(2);
        SectionRegionCoverageLogic.ClassifySegment(new P2(4, 5), new P2(6, 5), region.Loops, region.FillStyle)
            .Should().Be(SectionRegionCoverageLogic.Coverage.None);
        IReadOnlyList<P2>[] crossed = { new P2[] { new(0, 0), new(10, 10) }, new P2[] { new(10, 10), new(0, 10) },
            new P2[] { new(0, 10), new(10, 0) }, new P2[] { new(10, 0), new(0.25e-6, 0) } };
        var invalid = JoinClosedEdgesWithEndpointTolerance(crossed, endpointToleranceM: MicrometricEndpointToleranceM);
        Action validate = () => Region(new[] { invalid });
        validate.Should().Throw<InvalidOperationException>().WithMessage("*valid simple closed region*");
    }

    [Theory]
    [InlineData(1.01e-6)]
    [InlineData(0.001)]
    [InlineData(1.0)]
    public void AboveBoundClosureAndInteriorGapsRemainRejected(double gap)
    {
        Action terminal = () => JoinClosedEdgesWithEndpointTolerance(BoxEdges(0, 0, 10, 10, gap), endpointToleranceM: MicrometricEndpointToleranceM);
        terminal.Should().Throw<ArgumentException>();
        var edges = BoxEdges(0, 0, 10, 10, 0).ToArray();
        edges[1] = new[] { new P2(10 + gap, 0), new P2(10, 10) };
        Action interior = () => JoinClosedEdgesWithEndpointTolerance(edges, endpointToleranceM: MicrometricEndpointToleranceM);
        interior.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EndpointRecognitionCannotBeExpandedByCaller()
    {
        Action unbounded = () => JoinClosedEdgesWithEndpointTolerance(BoxEdges(0, 0, 10, 10, 0), endpointToleranceM: 0.001);
        unbounded.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static IReadOnlyList<IReadOnlyList<P2>> BoxEdges(double x0, double y0, double x1, double y1, double gap) =>
        new IReadOnlyList<P2>[] { new P2[] { new(x0, y0), new(x1, y0) }, new P2[] { new(x1, y0), new(x1, y1) },
            new P2[] { new(x1, y1), new(x0, y1) }, new P2[] { new(x0, y1), new(x0 + gap, y0) } };
    private static SectionHatchSpanLabelService.Region Region(IReadOnlyList<IReadOnlyList<P2>> loops) =>
        SectionHatchSpanLabelService.CreateRegion(loops, SectionRegionCoverageLogic.FillStyle.Outer,
            "מדרכה", "HW_HA_SIDEWALK", "HA", "262391", "local-HA.dwg", SourceSha, 0.005);
    private static Polygon Polygon(IReadOnlyList<P2> points) => Factory.CreatePolygon(points.Select(p =>
        new Coordinate(p.X, p.Y)).Append(new Coordinate(points[0].X, points[0].Y)).ToArray());
    private static double Distance(P2 a, P2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static P2 Point(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());
    private static List<IReadOnlyList<P2>> CurveEdges(JsonElement loop)
    {
        var result = new List<IReadOnlyList<P2>>();
        foreach (var edge in loop.GetProperty("edges").EnumerateArray())
        {
            if (edge.GetProperty("type").GetString() == "Line")
            {
                result.Add(new[] { Point(edge.GetProperty("start")), Point(edge.GetProperty("end")) });
                continue;
            }
            edge.GetProperty("type").GetString().Should().Be("Arc");
            var center = Point(edge.GetProperty("center"));
            var radius = edge.GetProperty("radius").GetDouble();
            var from = edge.GetProperty("start_angle").GetDouble();
            var to = edge.GetProperty("end_angle").GetDouble();
            var sign = edge.GetProperty("counter_clockwise").GetBoolean() ? 1 : -1;
            var count = ArcSegmentCount(radius, Math.Abs(to - from), 0.005);
            var samples = new List<P2>();
            for (var i = 0; i <= count; i++)
            {
                var angle = sign * (from + (to - from) * i / count);
                samples.Add(i == 0 ? Point(edge.GetProperty("signed_angle_start")) :
                    i == count ? Point(edge.GetProperty("signed_angle_end")) :
                    new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
            }
            result.Add(samples);
        }
        return result;
    }
    private static JsonElement Record(JsonDocument fixture, string handle) => fixture.RootElement.GetProperty("records")
        .EnumerateArray().Single(record => record.GetProperty("handle").GetString() == handle);
    private static JsonDocument Fixture([CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "fixtures",
            "section-hatch-070926", "ha-7-plan44-failures.acadsharp-partial.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(FixtureSha);
        var fixture = JsonDocument.Parse(bytes);
        fixture.RootElement.GetProperty("source_sha256_before").GetString().Should().Be(SourceSha);
        fixture.RootElement.GetProperty("source_sha256_after").GetString().Should().Be(SourceSha);
        fixture.RootElement.GetProperty("records").GetArrayLength().Should().Be(7);
        return fixture;
    }
}
