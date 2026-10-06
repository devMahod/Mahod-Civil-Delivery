using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using NetTopologySuite.Geometries;
using Xunit;
using Xunit.Abstractions;
using static MahodAI.CivilDelivery.Shared.SectionHatchBoundaryGeometry;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public class SectionHatchBoundaryFixtureTests(ITestOutputHelper output)
{
    private const string SourceSha = "8F2469EDD9F9D25FCC09C5563FAC1C2BD9B3CE17A9B06D1E9DAE032CF8175315";
    private const string FixtureSha = "F2141DA8DF1675E8E5E68E27744550DF8892E3C5A693B97B11B5AEBB1F2B013C";
    private static readonly GeometryFactory Factory = new();

    [Theory]
    [InlineData("26239C")]
    [InlineData("2623C2")]
    [InlineData("2623C4")]
    [InlineData("2623C5")]
    [InlineData("2623C6")]
    [InlineData("2623CB")]
    [InlineData("2623CE")]
    [InlineData("2623D7")]
    [InlineData("2623D8")]
    [InlineData("2623DB")]
    [InlineData("2623DD")]
    [InlineData("2623DF")]
    public void AllTwelveNativeChordFailures_HaveRealPositiveChordsAndReadableBulgeBoundaries(string handle)
    {
        using var fixture = Fixture();
        var record = Record(fixture, handle);
        var loops = new List<IReadOnlyList<P2>>();
        var straightEdges = new List<IReadOnlyList<StraightEdge>>();
        foreach (var loop in record.GetProperty("loops").EnumerateArray())
        {
            var vertices = ReadPolyline(loop);
            for (var i = 0; i < vertices.Count; i++)
            {
                var next = vertices[(i + 1) % vertices.Count];
                Math.Sqrt(Math.Pow(next.X - vertices[i].X, 2) + Math.Pow(next.Y - vertices[i].Y, 2))
                    .Should().BeGreaterThan(0, "the immutable raw HA fixture has no zero source chords");
            }
            var result = TessellateClosedPolyline(vertices);
            result.HasCurves.Should().BeTrue();
            result.Points.Should().OnlyContain(point => double.IsFinite(point.X) && double.IsFinite(point.Y));
            Polygon(result.Points).IsValid.Should().BeTrue();
            Polygon(result.Points).Area.Should().BeGreaterThan(0);
            loops.Add(result.Points);
            straightEdges.Add(result.SourceStraightEdges);
        }
        if (handle == "2623D7")
        {
            Action ambiguous = () => SectionHatchSpanLabelService.CreateRegion(loops,
                SectionRegionCoverageLogic.FillStyle.Outer, "מדרכה", "HW_HA_SIDEWALK", "HA",
                handle, "6422-HA-MODEL-NATAZ.dwg", SourceSha, 0.005, straightEdges);
            ambiguous.Should().Throw<InvalidOperationException>().WithMessage("*proven disjoint straight seam*",
                "D7 has a real 2.91e-11 m endpoint mismatch; no tolerance-dependent seam is invented");
            return;
        }
        var region = SectionHatchSpanLabelService.CreateRegion(loops,
            SectionRegionCoverageLogic.FillStyle.Outer, "מדרכה", "HW_HA_SIDEWALK", "HA",
            handle, "6422-HA-MODEL-NATAZ.dwg", SourceSha, 0.005, straightEdges);
        region.Loops.Should().HaveCount(record.GetProperty("loops").GetArrayLength());
        region.Evidence.Should().HaveLength(64);
        if (handle == "2623D8")
        {
            var seam = region.FilledSeams.Should().ContainSingle().Which;
            var start = Between(seam.From, seam.To, 0.25);
            var end = Between(seam.From, seam.To, 0.75);
            SectionRegionCoverageLogic.ClassifySegment(start, end, region.Loops, region.FillStyle,
                region.FilledSeams).Should().Be(SectionRegionCoverageLogic.Coverage.Full);
            SectionRegionCoverageLogic.ClassifySegment(start, end, region.Loops, region.FillStyle)
                .Should().Be(SectionRegionCoverageLogic.Coverage.None, "unproven contact is not area evidence");
        }
    }

    [Fact]
    public void Real7D49ArcAndChord_DoesNotCollapseAClosedSliverIntoTwoPoints()
    {
        using var fixture = Fixture();
        var loop = Record(fixture, "7D49").GetProperty("loops")[2];
        var arc = loop.GetProperty("edges")[0];
        var radius = arc.GetProperty("radius").GetDouble();
        var sweep = arc.GetProperty("end_angle").GetDouble() - arc.GetProperty("start_angle").GetDouble();
        ArcTessellationSegmentCount(radius, sweep, 0.005).Should().Be(1,
            "the former region adapter flattened this proven curved boundary to its chord");
        ArcSegmentCount(radius, sweep, 0.005).Should().Be(2);
        var points = ReadCurveLoop(loop);
        points.Should().HaveCount(3);
        Polygon(points).IsValid.Should().BeTrue();
        Polygon(points).Area.Should().BeGreaterThan(1e-8);
    }

    [Theory]
    [InlineData("262379", 1)]
    [InlineData("26237C", 0)]
    [InlineData("26237E", 0)]
    [InlineData("262393", 0)]
    [InlineData("262399", 0)]
    [InlineData("26236C", 8)]
    [InlineData("262391", 0)]
    [InlineData("262398", 0)]
    public void RealDisconnectedOrUnclosedLoops_AreNotRepairedByTheReader(string handle, int loopIndex)
    {
        using var fixture = Fixture();
        var loop = Record(fixture, handle).GetProperty("loops")[loopIndex];
        Action read = () => ReadCurveLoop(loop);
        read.Should().Throw<ArgumentException>("the actual source gap must not be closed by tolerance inflation");
    }

    [Fact]
    public void All27RealFixturesAreReplayed_WithoutDroppingAnyLoop()
    {
        using var fixture = Fixture();
        var records = fixture.RootElement.GetProperty("records");
        records.GetArrayLength().Should().Be(27);
        var outcomes = new Dictionary<string, string>();
        foreach (var record in records.EnumerateArray())
        {
            var handle = record.GetProperty("handle").GetString()!;
            try
            {
                var loops = new List<IReadOnlyList<P2>>();
                var straightEdges = new List<IReadOnlyList<StraightEdge>>();
                foreach (var loop in record.GetProperty("loops").EnumerateArray())
                {
                    if ((loop.GetProperty("flags_code").GetInt32() & (0x20 | 0x40 | 0x100 | 8 | 0x80)) != 0)
                        throw new InvalidOperationException("Native flagged loop remains blocked");
                    if (loop.GetProperty("is_polyline").GetBoolean())
                    {
                        var result = TessellateClosedPolyline(ReadPolyline(loop));
                        loops.Add(result.Points);
                        straightEdges.Add(result.SourceStraightEdges);
                    }
                    else
                    {
                        loops.Add(ReadCurveLoop(loop));
                        straightEdges.Add(loop.GetProperty("edges").EnumerateArray()
                            .Where(edge => edge.GetProperty("type").GetString() == "Line")
                            .Select(edge => new StraightEdge(Point(edge.GetProperty("start")),
                                Point(edge.GetProperty("end")))).ToArray());
                    }
                }
                var layer = record.GetProperty("layer").GetString()!;
                var region = SectionHatchSpanLabelService.CreateRegion(loops,
                    SectionRegionCoverageLogic.FillStyle.Outer, layer == "PL-BIKE" ? "שביל אופניים" : "מדרכה", layer,
                    "HA", handle, "6422-HA-MODEL-NATAZ.dwg", SourceSha, 0.005, straightEdges);
                region.Loops.Should().HaveCount(record.GetProperty("loops").GetArrayLength());
                outcomes.Add(handle, "readable");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                outcomes.Add(handle, ex.Message);
            }
        }
        foreach (var outcome in outcomes) output.WriteLine(outcome.Key + " : " + outcome.Value);
        outcomes.Count.Should().Be(27);
        outcomes.Where(item => item.Value == "readable").Select(item => item.Key).Should()
            .BeEquivalentTo(new[] { "7D49", "26239C", "2623B7", "2623C2", "2623C4", "2623C5", "2623C6", "2623CB",
                "2623CE", "2623D8", "2623DB", "2623DD", "2623DF" });
    }

    [Fact]
    public void ClosureMarkersPreserveSignedBulges_ButInteriorZeroLengthArcsRemainInvalid()
    {
        var raw = new[] { new BulgePoint(0, 0, 1), new BulgePoint(2, 0, 1) };
        var original = TessellateClosedPolyline(raw);
        TessellateClosedPolyline(raw.Append(raw[0]).ToArray()).Points.Should().Equal(original.Points);
        original.Points.Should().Contain(point => point.Y < -0.9).And.Contain(point => point.Y > 0.9);
        Action invalid = () => TessellateClosedPolyline(new[] { raw[0], raw[0], raw[1] });
        invalid.Should().Throw<ArgumentException>().WithMessage("*interior zero-length*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactDisjointStraightSeamIsFilled_AcrossOrAlongIt_WithoutFillingExterior(bool transformed)
    {
        P2 Transform(P2 p) => transformed ? new(203500 - 3 * p.X + 0.5 * p.Y, 649300 + 2 * p.Y) : p;
        var loops = new[] { Box(-2, -2, 0, 2), Box(0, -2, 2, 2) }
            .Select(loop => (IReadOnlyList<P2>)loop.Select(Transform).ToArray()).ToArray();
        var region = MakeRegion(loops);
        region.FilledSeams.Should().ContainSingle();
        SectionRegionCoverageLogic.Coverage Coverage(P2 a, P2 b) => SectionRegionCoverageLogic.ClassifySegment(
            Transform(a), Transform(b), region.Loops, region.FillStyle, region.FilledSeams);
        Coverage(new(0, -1), new(0, 1)).Should().Be(SectionRegionCoverageLogic.Coverage.Full);
        Coverage(new(-1, 0), new(1, 0)).Should().Be(SectionRegionCoverageLogic.Coverage.Full);
        Coverage(new(0, -3), new(0, 3)).Should().Be(SectionRegionCoverageLogic.Coverage.Partial);
        Coverage(new(-1, -2), new(1, -2)).Should().Be(SectionRegionCoverageLogic.Coverage.None);
        var marks = new[]
        {
            new Crossing(-1, null, "curb", "HA", new("curb", "אבן שפה", 7), "a", Transform(new(0, -1)).X, Transform(new(0, -1)).Y),
            new Crossing(1, null, "curb", "HA", new("curb", "אבן שפה", 7), "b", Transform(new(0, 1)).X, Transform(new(0, 1)).Y),
        };
        SectionHatchSpanLabelService.Resolve(new[] { region }, marks,
                new[] { new UnresolvedSpan(-1, 1, 2, "curb", "curb", "missing-label") })
            .Should().ContainSingle().Which.Label.Should().Be("מדרכה");
    }

    [Theory]
    [InlineData("touching-holes")]
    [InlineData("touching-outer-hole")]
    [InlineData("overlapping-interiors")]
    [InlineData("crossing-boundaries")]
    [InlineData("point-contact")]
    [InlineData("near-seam")]
    public void ContactWithoutExactDisjointTopLevelSeamProof_RemainsBlocked(string scenario)
    {
        IReadOnlyList<IReadOnlyList<P2>> loops = scenario switch
        {
            "touching-holes" => new[] { Box(-5, -5, 5, 5), Box(-2, -2, 0, 2), Box(0, -2, 2, 2) },
            "touching-outer-hole" => new[] { Box(-2, -2, 2, 2), Box(-2, -2, 0, 2) },
            "overlapping-interiors" => new[] { Box(-2, -2, 1, 2), Box(0, -2, 2, 2) },
            "crossing-boundaries" => new[] { Box(-2, -1, 2, 1), Box(-1, -2, 1, 2) },
            "point-contact" => new[] { Box(-2, -2, 0, 0), Box(0, 0, 2, 2) },
            "near-seam" => new[] { Box(-2, -2, 0, 2), (IReadOnlyList<P2>)new[] { new P2(0, -2), new P2(2, -2), new P2(2, 2), new P2(1e-11, 2) } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Action create = () => MakeRegion(loops);
        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MatchingTessellationEdgesWithoutOriginalStraightEdgeProof_RemainBlocked()
    {
        var tinyBulge = TessellateClosedPolyline(new[] { new BulgePoint(0, 0, 1e-13),
            new BulgePoint(2, 0, 0), new BulgePoint(2, 2, 0), new BulgePoint(0, 2, 0) });
        tinyBulge.SourceStraightEdges.Should().HaveCount(3,
            "an approximated tiny arc is not an exact source straight edge");
        Action create = () => SectionHatchSpanLabelService.CreateRegion(
            new[] { Box(-2, -2, 0, 2), Box(0, -2, 2, 2) },
            SectionRegionCoverageLogic.FillStyle.Outer, "מדרכה", "approved", "HA", "fixture",
            "fixture.dwg", SourceSha);
        create.Should().Throw<InvalidOperationException>().WithMessage("*proven disjoint straight seam*");
    }

    private static P2 Between(P2 a, P2 b, double fraction) =>
        new(a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction);
    private static IReadOnlyList<P2> Box(double left, double bottom, double right, double top) =>
        new[] { new P2(left, bottom), new P2(right, bottom), new P2(right, top), new P2(left, top) };
    private static SectionHatchSpanLabelService.Region MakeRegion(IReadOnlyList<IReadOnlyList<P2>> loops) =>
        SectionHatchSpanLabelService.CreateRegion(loops, SectionRegionCoverageLogic.FillStyle.Outer,
            "מדרכה", "approved", "HA", "fixture", "fixture.dwg", SourceSha, 0.005,
            loops.Select(loop => (IReadOnlyList<StraightEdge>)loop.Select((point, index) =>
                new StraightEdge(point, loop[(index + 1) % loop.Count])).ToArray()).ToArray());

    private static List<BulgePoint> ReadPolyline(JsonElement loop) =>
        loop.GetProperty("edges")[0].GetProperty("vertices").EnumerateArray()
            .Select(v => new BulgePoint(v.GetProperty("x").GetDouble(), v.GetProperty("y").GetDouble(),
                v.GetProperty("bulge").GetDouble())).ToList();

    private static IReadOnlyList<P2> ReadCurveLoop(JsonElement loop)
    {
        var edges = new List<IReadOnlyList<P2>>();
        foreach (var edge in loop.GetProperty("edges").EnumerateArray())
        {
            if (edge.GetProperty("type").GetString() == "Line")
            {
                edges.Add(new[] { Point(edge.GetProperty("start")), Point(edge.GetProperty("end")) });
                continue;
            }
            edge.GetProperty("type").GetString().Should().Be("Arc");
            var center = Point(edge.GetProperty("center"));
            var radius = edge.GetProperty("radius").GetDouble();
            var from = edge.GetProperty("start_angle").GetDouble();
            var to = edge.GetProperty("end_angle").GetDouble();
            var sign = edge.GetProperty("counter_clockwise").GetBoolean() ? 1 : -1;
            var count = ArcSegmentCount(radius, to - from, 0.005);
            var samples = new List<P2>();
            for (var i = 0; i <= count; i++)
            {
                // Use the fixture's documented signed-angle convention, NOT the
                // offline library's reversed CW PolygonalVertexes convenience API.
                var angle = sign * (from + (to - from) * i / count);
                samples.Add(i == 0 ? Point(edge.GetProperty("signed_angle_start")) :
                    i == count ? Point(edge.GetProperty("signed_angle_end")) :
                    new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
            }
            edges.Add(samples);
        }
        return JoinClosedEdges(edges);
    }

    private static Polygon Polygon(IReadOnlyList<P2> loop) => Factory.CreatePolygon(loop
        .Select(p => new Coordinate(p.X, p.Y)).Append(new Coordinate(loop[0].X, loop[0].Y)).ToArray());
    private static P2 Point(JsonElement point) => new(point[0].GetDouble(), point[1].GetDouble());
    private static JsonElement Record(JsonDocument fixture, string handle) => fixture.RootElement
        .GetProperty("records").EnumerateArray().Single(record => record.GetProperty("handle").GetString() == handle);
    private static JsonDocument Fixture([CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "fixtures",
            "section-hatch-060926", "ha-27-failed-hatches.acadsharp-partial.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(FixtureSha);
        var result = JsonDocument.Parse(bytes);
        result.RootElement.GetProperty("source_sha256_before").GetString().Should().Be(SourceSha);
        result.RootElement.GetProperty("source_sha256_after").GetString().Should().Be(SourceSha);
        return result;
    }
}
