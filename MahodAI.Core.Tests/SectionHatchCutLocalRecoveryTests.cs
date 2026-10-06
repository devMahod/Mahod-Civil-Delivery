using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionHatchBoundaryGeometry;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public class SectionHatchCutLocalRecoveryTests
{
    private const string SourceSha = "5C23EC4AEF9ECC3A6A3CF75EDAB8B9ADCA60B7344AA941ACF1EB9B1186290539";
    private const string FixtureSha = "DCB2F17980C0E6E211D8C68E8AEB63B9C79F8E382447EE59839045FC330AF6D4";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual60861CutUsesBothRelevantLoops_WhileWhole2623A2RemainsPartial(bool transformed)
    {
        P2 Transform(P2 p) => transformed ? new(-2 * p.X + .25 * p.Y + 1300, 3 * p.Y - 900) : p;
        var source = FromFixture("2623A2", Transform);
        source.Deferred.Should().NotBeNull();
        source.Loops.Should().BeEmpty("the partial source must not masquerade as a complete region");
        source.Deferred!.Loops.Should().HaveCount(8);
        source.Deferred.Loops.Single(loop => loop.Index == 7).Failure.Should().NotBeNull();
        var start = Transform(new(204528.1001137753, 648719.8673357329));
        var end = Transform(new(204575.02111328673, 648679.340556872));
        var local = SectionHatchSpanLabelService.CompleteForSegment(source, start, end);
        local.Should().NotBeNull();
        local!.Deferred.Should().BeNull();
        local.Loops.Should().HaveCount(2, "both source loops 1 and 2 can contribute to the local fill/hole depth");
        local.FillStyle.Should().Be(SectionRegionCoverageLogic.FillStyle.Outer);
        local.Evidence.Should().NotBe(source.Evidence);
        foreach (var fraction in new[] { .1, .3, .5, .7, .9 })
        {
            var a = Interpolate(start, end, fraction - .005);
            var b = Interpolate(start, end, fraction + .005);
            var expected = source.Deferred.Loops.Where(loop => loop.Index is 1 or 2).Select(loop => loop.Points).ToArray();
            SectionRegionCoverageLogic.ClassifySegment(a, b, local.Loops, local.FillStyle)
                .Should().Be(SectionRegionCoverageLogic.ClassifySegment(a, b, expected, local.FillStyle));
        }
        // Re-opening/moving the cut into the bad loop must re-evaluate locality.
        SectionHatchSpanLabelService.CompleteForSegment(source,
            Transform(new(204490, 648570)), Transform(new(204490, 648610))).Should().BeNull();
    }

    [Fact]
    public void Actual262379RetainsClosedComponentAt60179_AndItsOpenLoopRemainsRecorded()
    {
        var source = FromFixture("262379");
        source.Deferred!.Loops.Should().HaveCount(2);
        source.Deferred.Loops[1].Failure.Should().Contain("NotClosed");
        var local = SectionHatchSpanLabelService.CompleteForSegment(source,
            new(204466.79316129026, 648066.2610831442), new(204526.35210702042, 648049.0351729067));
        local.Should().NotBeNull();
        local!.Loops.Should().ContainSingle();
    }

    [Theory]
    [InlineData("26236C", 204466.79316129026, 648066.2610831442, 204526.35210702042, 648049.0351729067)]
    [InlineData("26236C", 204457.22841207663, 648222.8581504324, 204515.78208077708, 648243.24117969)]
    [InlineData("262393", 203531.21382769404, 649272.880521733, 203537.57694867314, 649334.5531306564)]
    [InlineData("262393", 203660.24929710405, 649356.3771400442, 203687.91048292117, 649411.864605288)]
    [InlineData("2623A6", 203691.04015409463, 648367.5107550636, 203753.0072977476, 648369.528949181)]
    [InlineData("2623AF", 203691.5311670952, 649117.3056302564, 203630.87879670764, 649130.1621448062)]
    public void ActualRelevantBrokenOuterLoopsStillRefuse(string handle, double x0, double y0, double x1, double y1)
    {
        var source = FromFixture(handle);
        source.Deferred.Should().NotBeNull();
        SectionHatchSpanLabelService.CompleteForSegment(source, new(x0, y0), new(x1, y1)).Should().BeNull();
    }

    [Fact]
    public void RemoteFailureCannotFillARelevantHole_UnknownOrContainingFailureCannotBeIgnored()
    {
        var outer = Box(0, 0, 10, 10);
        var hole = Box(3, 3, 7, 7);
        var failed = new SectionHatchSpanLabelService.SourceLoop(2, Array.Empty<P2>(),
            Array.Empty<StraightEdge>(), new[] { 20d, 20d, 21d, 21d }, "not closed", 0);
        var source = Source(new[] { Loop(0, outer), Loop(1, hole), failed });
        var region = SectionHatchSpanLabelService.CompleteForSegment(source, new(4, 5), new(6, 5));
        region.Should().NotBeNull();
        region!.Loops.Should().HaveCount(2);
        SectionRegionCoverageLogic.ClassifySegment(new(4, 5), new(6, 5), region.Loops, region.FillStyle)
            .Should().Be(SectionRegionCoverageLogic.Coverage.None);
        var unknown = Source(new[] { Loop(0, outer), failed with { Bounds = null } });
        SectionHatchSpanLabelService.CompleteForSegment(unknown, new(1, 1), new(2, 1)).Should().BeNull();
        var containing = Source(new[] { Loop(0, outer), failed with { Bounds = new[] { -20d, -20d, 20d, 20d } } });
        SectionHatchSpanLabelService.CompleteForSegment(containing, new(1, 1), new(2, 1)).Should().BeNull();
        var crossing = Source(new[] { Loop(0, outer), failed with { Bounds = new[] { 4d, -1d, 6d, 11d } } });
        SectionHatchSpanLabelService.CompleteForSegment(crossing, new(1, 5), new(9, 5)).Should().BeNull();
    }

    [Fact]
    public void CommonPlanApplyResolverUsesDeferredRegion_AndEvidenceChangesWithExactSegment()
    {
        var source = Source(new[] { Loop(0, Box(0, 0, 10, 10)), new SectionHatchSpanLabelService.SourceLoop(1,
            Array.Empty<P2>(), Array.Empty<StraightEdge>(), new[] { 20d, 20d, 21d, 21d }, "not closed", 0) });
        var marks = new[] { new Crossing(0, null, "curb", "HA", new("curb", "אבן שפה", 7), "a", 1, 5),
            new Crossing(8, null, "curb", "HA", new("curb", "אבן שפה", 7), "b", 9, 5) };
        var result = SectionHatchSpanLabelService.Resolve(new[] { source }, marks,
            new[] { new UnresolvedSpan(0, 8, 8, "curb", "curb", "missing-label") });
        result.Should().ContainSingle().Which.Label.Should().Be("מדרכה");
        var first = SectionHatchSpanLabelService.CompleteForSegment(source, new(1, 5), new(9, 5));
        var second = SectionHatchSpanLabelService.CompleteForSegment(source, new(1, 6), new(9, 6));
        first!.Evidence.Should().NotBe(second!.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonResolverCannotFallThroughToGoodLoopsWhenFailedLoopIsRelevantOrUnbounded(bool unknownBounds)
    {
        var failed = new SectionHatchSpanLabelService.SourceLoop(1, Array.Empty<P2>(), Array.Empty<StraightEdge>(),
            unknownBounds ? null : new[] { 4d, 4d, 6d, 6d }, "unreadable loop", 0);
        var source = Source(new[] { Loop(0, Box(0, 0, 10, 10)), failed });
        var marks = new[] { new Crossing(0, null, "curb", "HA", new("curb", "אבן שפה", 7), "a", 1, 5),
            new Crossing(8, null, "curb", "HA", new("curb", "אבן שפה", 7), "b", 9, 5) };
        SectionHatchSpanLabelService.Resolve(new[] { source }, marks,
            new[] { new UnresolvedSpan(0, 8, 8, "curb", "curb", "missing-label") }).Should().BeEmpty();
    }

    private static SectionHatchSpanLabelService.Region FromFixture(string handle, Func<P2, P2>? transform = null,
        [CallerFilePath] string testFile = "")
    {
        transform ??= point => point;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "fixtures", "section-hatch-070926",
            "ha-7-plan44-failures.acadsharp-partial.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(FixtureSha);
        using var fixture = JsonDocument.Parse(bytes);
        fixture.RootElement.GetProperty("source_sha256_before").GetString().Should().Be(SourceSha);
        fixture.RootElement.GetProperty("source_sha256_after").GetString().Should().Be(SourceSha);
        var record = fixture.RootElement.GetProperty("records").EnumerateArray().Single(r => r.GetProperty("handle").GetString() == handle);
        var loops = new List<SectionHatchSpanLabelService.SourceLoop>();
        foreach (var loop in record.GetProperty("loops").EnumerateArray())
        {
            var edges = new List<IReadOnlyList<P2>>();
            var straight = new List<StraightEdge>();
            var curved = false;
            foreach (var edge in loop.GetProperty("edges").EnumerateArray())
            {
                if (edge.GetProperty("type").GetString() == "Line")
                {
                    var a = transform(Point(edge.GetProperty("start"))); var b = transform(Point(edge.GetProperty("end")));
                    edges.Add(new[] { a, b }); straight.Add(new(a, b)); continue;
                }
                edge.GetProperty("type").GetString().Should().Be("Arc"); curved = true;
                var center = Point(edge.GetProperty("center")); var radius = edge.GetProperty("radius").GetDouble();
                var from = edge.GetProperty("start_angle").GetDouble(); var to = edge.GetProperty("end_angle").GetDouble();
                var sign = edge.GetProperty("counter_clockwise").GetBoolean() ? 1 : -1;
                var count = ArcSegmentCount(radius * 5, Math.Abs(to - from), .005);
                edges.Add(Enumerable.Range(0, count + 1).Select(i => {
                    var angle = sign * (from + (to - from) * i / count);
                    return transform(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
                }).ToArray());
            }
            var all = edges.SelectMany(e => e).ToArray(); var pad = curved ? .005001 : .000001;
            var bounds = new[] { all.Min(p => p.X) - pad, all.Min(p => p.Y) - pad, all.Max(p => p.X) + pad, all.Max(p => p.Y) + pad };
            string? failure = (loop.GetProperty("flags_code").GetInt32() & (0x20 | 0x40 | 0x100 | 8 | 0x80)) != 0 ? loop.GetProperty("flags").GetString() : null;
            IReadOnlyList<P2> points = Array.Empty<P2>();
            try { points = JoinClosedEdgesWithEndpointTolerance(edges, endpointToleranceM: 1e-6); }
            catch (ArgumentException error) { failure = (failure ?? "") + error.Message; }
            loops.Add(new(loop.GetProperty("index").GetInt32(), points, straight, bounds, failure, curved ? .005 : 0));
        }
        return Source(loops, handle);
    }
    private static SectionHatchSpanLabelService.Region Source(IReadOnlyList<SectionHatchSpanLabelService.SourceLoop> loops, string handle = "fixture") =>
        SectionHatchSpanLabelService.CreateSourceRegion(loops, SectionRegionCoverageLogic.FillStyle.Outer, "מדרכה", "HW_HA_SIDEWALK", "HA", handle, "local-HA.dwg", SourceSha);
    private static SectionHatchSpanLabelService.SourceLoop Loop(int index, IReadOnlyList<P2> points) => new(index, points,
        points.Select((point, i) => new StraightEdge(point, points[(i + 1) % points.Count])).ToArray(),
        new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y) }, null, 0);
    private static P2[] Box(double x0, double y0, double x1, double y1) => new[] { new P2(x0, y0), new P2(x1, y0), new P2(x1, y1), new P2(x0, y1) };
    private static P2 Interpolate(P2 a, P2 b, double t) => new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
    private static P2 Point(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());
}
