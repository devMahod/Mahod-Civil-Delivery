using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionHatchBoundaryGeometry;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public sealed class SectionPolylineSegmentSamplingTests
{
    private const string SourceSha = "FAD0ACACA3B064F55E53CC20476B30ACF5542BCAA2F7E4CC462B45B393D88B90";
    private const string ReportSha = "E48CE5AAF7978CD44320AA4472C456BFE1E6CACEC632D2A348F2ED6CD08B8254";
    private const string FixtureSha = "D6006F677AB40A3E0AE08A4AB719120B57CC37D0562E732B4A5E93A6A31955D4";

    private static JsonDocument Fixture([CallerFilePath] string testFile = "")
    {
        var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(testFile)!, "Fixtures", "gm_fad0_pl_bike_11073d.json"));
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(FixtureSha);
        var fixture = JsonDocument.Parse(bytes);
        fixture.RootElement.GetProperty("source_sha256").GetString().Should().BeEquivalentTo(SourceSha);
        fixture.RootElement.GetProperty("source_report_sha256").GetString().Should().Be(ReportSha);
        return fixture;
    }

    private static BulgePoint[] Vertices(JsonDocument fixture, string handle)
    {
        var record = fixture.RootElement.GetProperty("records").EnumerateArray()
            .Single(value => value.GetProperty("handle").GetString() == handle);
        record.GetProperty("layer").GetString().Should().Be("PL-BIKE");
        record.GetProperty("closed_polyline").GetBoolean().Should().BeTrue();
        return record.GetProperty("vertices").EnumerateArray().Select(vertex => new BulgePoint(
            vertex.GetProperty("x").GetDouble(), vertex.GetProperty("y").GetDouble(),
            vertex.GetProperty("bulge").ValueKind == JsonValueKind.String
                ? vertex.GetProperty("bulge").GetString() == "NaN" ? double.NaN
                    : throw new InvalidDataException("Unexpected named scalar in immutable fixture.")
                : vertex.GetProperty("bulge").GetDouble())).ToArray();
    }

    [Fact]
    public void ExactGm11073DAllFortySevenLiveSegmentsArePreservedAndOnlyZeroClosingSegmentIsSkipped()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "11073D");
        source.Should().HaveCount(48);
        source[^1].X.Should().Be(source[0].X); source[^1].Y.Should().Be(source[0].Y);
        double.IsNaN(source[^1].Bulge).Should().BeTrue();
        source.Take(47).Should().OnlyContain(vertex => double.IsFinite(vertex.Bulge));
        var unchanged = source.ToArray();

        var actual = SectionPolylineSegmentSampling.Sample(source, closed: true);
        var expected = SectionPolylineSegmentSampling.Sample(source.Take(47).ToArray(), closed: true);

        actual.SkippedExactClosingSegmentIndex.Should().Be(47);
        expected.SkippedExactClosingSegmentIndex.Should().BeNull();
        actual.Points.Should().Equal(expected.Points,
            "dropping only the repeated last vertex retains the last valid bulge's connection to vertex zero");
        actual.Points.Count.Should().BeGreaterThan(47, "real curved segments must not be replaced by endpoint chords");
        foreach (var vertex in source.Take(47)) actual.Points.Should().Contain(new P2(vertex.X, vertex.Y));
        actual.Points.Should().OnlyContain(point => double.IsFinite(point.X) && double.IsFinite(point.Y));
        source.Should().Equal(unchanged, "sampling must not repair or rewrite the source scalars");
    }

    [Fact]
    public void Recorded92F7TinyButNonzeroClosingGapWithNaNBulgeRemainsBlocked()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "92F7");
        source.Should().HaveCount(37);
        var dx = source[^1].X - source[0].X; var dy = source[^1].Y - source[0].Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        distance.Should().BeGreaterThan(0).And.BeLessThan(1e-8);
        double.IsNaN(source[^1].Bulge).Should().BeTrue();
        Action sample = () => SectionPolylineSegmentSampling.Sample(source, closed: true);
        sample.Should().Throw<ArgumentException>("no tolerance may turn the real closing segment into an unused marker");
    }

    [Theory]
    [InlineData(1.0, -1.0)]
    [InlineData(-1.0, 1.0)]
    public void FinitePositiveAndNegativeBulgesRetainOppositeSemicircularPaths(double bulge, double expectedMidY)
    {
        var source = new[] { new BulgePoint(0, 0, bulge), new BulgePoint(2, 0, 0) };
        var sampled = SectionPolylineSegmentSampling.Sample(source, closed: false);
        sampled.SkippedExactClosingSegmentIndex.Should().BeNull();
        sampled.Points[0].Should().Be(new P2(0, 0)); sampled.Points[^1].Should().Be(new P2(2, 0));
        sampled.Points.Count.Should().BeGreaterThan(2);
        var midpoint = sampled.Points.OrderBy(point => Math.Abs(point.X - 1)).First();
        midpoint.X.Should().BeApproximately(1, .005); midpoint.Y.Should().BeApproximately(expectedMidY, .005);
        sampled.Points.Should().OnlyContain(point => point.Y * expectedMidY >= -1e-12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NaNOnAnyLiveSegmentIsNeverTheClosingMarkerException(int index)
    {
        var source = new[] { new BulgePoint(0, 0, 0), new BulgePoint(2, 0, 0), new BulgePoint(2, 2, 0) };
        source[index] = source[index] with { Bulge = double.NaN };
        Action sample = () => SectionPolylineSegmentSampling.Sample(source, closed: true);
        sample.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteriorRepeatedVertexWithNonfiniteOutgoingBulgeRemainsBlocked(bool closed)
    {
        var source = new[]
        {
            new BulgePoint(0, 0, 0), new BulgePoint(2, 0, double.NaN),
            new BulgePoint(2, 0, 0), new BulgePoint(2, 2, 0), new BulgePoint(0, 0, 0),
        };
        Action sample = () => SectionPolylineSegmentSampling.Sample(source, closed);
        sample.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void NonfiniteClosingEndpointCannotQualifyAsAnExactDuplicate(double x, double y)
    {
        var source = new[] { new BulgePoint(0, 0, 0), new BulgePoint(2, 0, 0), new BulgePoint(x, y, double.NaN) };
        Action sample = () => SectionPolylineSegmentSampling.Sample(source, closed: true);
        sample.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OpenPolylineUnusedTerminalBulgeHasNoOutgoingSpanAndNoClosingSkipDiagnostic()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "11073D");
        var finiteUnusedTerminal = source.ToArray();
        finiteUnusedTerminal[^1] = finiteUnusedTerminal[^1] with { Bulge = 0 };
        var actual = SectionPolylineSegmentSampling.Sample(source, closed: false);
        var expected = SectionPolylineSegmentSampling.Sample(finiteUnusedTerminal, closed: false);
        actual.SkippedExactClosingSegmentIndex.Should().BeNull();
        actual.Points.Should().Equal(expected.Points, "an open terminal vertex has no outgoing segment to evaluate");
    }

    [Fact]
    public void BoundedSamplingRefusesVertexLimitRatherThanReturningTruncatedGeometry()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "11073D");
        Action sample = () => SectionPolylineSegmentSampling.Sample(source, closed: true, maximumVertices: 48);
        sample.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ComposedFrameRetainsTiltedOcsElevationAndNonuniformXrefScales()
    {
        // This represents a supplied composed native frame: OCS normal tilted to -Y,
        // XREF scales2/3/4 and translation. It does not execute Autodesk PlaneToWorld.
        var sampled = SectionPolylineSegmentSampling.Sample(new[]
        {
            new BulgePoint(2, 5, 0), new BulgePoint(4, 6, 0),
        }, closed: false);
        var frame = new SectionPolylineSegmentSampling.Frame(
            new V3(100, 200, 300), new V3(2, 0, 0), new V3(0, 0, 3), new V3(0, -4, 0));
        var mapped = SectionPolylineSegmentSampling.ToWcs(sampled, 7, frame);
        mapped.Should().Equal(new V3(104, 172, 315), new V3(108, 172, 318));
        sampled.Points.Should().Equal(new P2(2, 5), new P2(4, 6));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    public void NonuniformXrefIncreasesSamplesAndKeepsActualWorldSagittaWithinFiveMillimetres(double bulge)
    {
        var source = new[] { new BulgePoint(0, 0, bulge), new BulgePoint(4, 0, 0) }; // radius2 semicircle
        var unscaled = SectionPolylineSegmentSampling.Sample(source, false);
        var scaled = SectionPolylineSegmentSampling.Sample(source, false, maximumLinearScale: 3 * Math.Sqrt(3));
        scaled.Points.Count.Should().BeGreaterThan(unscaled.Points.Count);
        var frame = new SectionPolylineSegmentSampling.Frame(new V3(0, 0, 0),
            new V3(3, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
        var world = SectionPolylineSegmentSampling.ToWcs(scaled, 0, frame);
        var segments = world.Count - 1;
        for (var index = 0; index < segments; index++)
        {
            var angle = Math.PI * (index + .5) / segments;
            // Independent analytic circle midpoint, then diagonal(3,1) mapping.
            var trueX = 3 * (2 - 2 * Math.Cos(angle)); var trueY = -bulge * 2 * Math.Sin(angle);
            var chordX = (world[index].X + world[index + 1].X) / 2;
            var chordY = (world[index].Y + world[index + 1].Y) / 2;
            var error = Math.Sqrt(Math.Pow(trueX - chordX, 2) + Math.Pow(trueY - chordY, 2));
            error.Should().BeLessThanOrEqualTo(.005 + 1e-12);
        }
    }

    [Fact]
    public void AffineMappingRejectsNonfiniteElevationOrFrameInsteadOfReturningPartialPoints()
    {
        var sampled = SectionPolylineSegmentSampling.Sample(new[] { new BulgePoint(0, 0, 0), new BulgePoint(2, 0, 0) }, false);
        var frame = new SectionPolylineSegmentSampling.Frame(new V3(0, 0, 0), new V3(1, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
        Action elevation = () => SectionPolylineSegmentSampling.ToWcs(sampled, double.NaN, frame);
        Action basis = () => SectionPolylineSegmentSampling.ToWcs(sampled, 0, frame with { XAxis = new V3(double.PositiveInfinity, 0, 0) });
        elevation.Should().Throw<ArgumentException>(); basis.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadSource_Real11073DReadsAllEndpointsThenAllFortySevenLiveBulgesButNeverTheThrowingClosingGetter()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "11073D");
        var calls = new List<string>();
        var read = SectionPolylineSegmentSampling.ReadSource(source.Length, true,
            index => { calls.Add("point:" + index); return new P2(source[index].X, source[index].Y); },
            index => {
                calls.Add("bulge:" + index);
                if (index == 47) throw new InvalidOperationException("native closing getter must not be invoked");
                return source[index].Bulge;
            });

        calls.Should().Equal(Enumerable.Range(0, 48).Select(index => "point:" + index)
            .Concat(Enumerable.Range(0, 47).Select(index => "bulge:" + index)));
        read.UnreadClosingSegmentIndex.Should().Be(47);
        read.UnreadOpenTerminalVertexIndex.Should().BeNull();
        double.IsNaN(read.Vertices[47].Bulge).Should().BeTrue("unread is not a fabricated zero measurement");
        read.Vertices.Take(47).Should().Equal(source.Take(47));
        var sampled = SectionPolylineSegmentSampling.Sample(read.Vertices, true);
        sampled.SkippedExactClosingSegmentIndex.Should().Be(47);
        sampled.Points.Should().Equal(SectionPolylineSegmentSampling.Sample(source.Take(47).ToArray(), true).Points);
    }

    [Fact]
    public void ReadSource_Real92F7MustCallItsTinyNonzeroClosingGetterAndPropagateTheOriginalFailure()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "92F7");
        var calls = new List<int>(); var original = new InvalidOperationException("native getter eInvalidInput");
        SectionPolylineSegmentSampling.SourceReadResult? result = null;
        Action read = () => result = SectionPolylineSegmentSampling.ReadSource(source.Length, true,
            index => new P2(source[index].X, source[index].Y),
            index => { calls.Add(index); if (index == 36) throw original; return source[index].Bulge; });
        read.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(original);
        calls.Should().Equal(Enumerable.Range(0, 37));
        result.Should().BeNull("failed native reading cannot publish partial source geometry");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(46)]
    public void ReadSource_AnyLiveGetterFailurePropagatesUnchangedWithoutPartialSuccess(int failingIndex)
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "11073D");
        var calls = new List<int>(); var original = new InvalidOperationException("native live getter failed");
        SectionPolylineSegmentSampling.SourceReadResult? result = null;
        Action read = () => result = SectionPolylineSegmentSampling.ReadSource(source.Length, true,
            index => new P2(source[index].X, source[index].Y),
            index => { calls.Add(index); if (index == failingIndex) throw original; return source[index].Bulge; });
        read.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(original);
        calls.Should().Equal(Enumerable.Range(0, failingIndex + 1));
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSource_InteriorDuplicateDoesNotExcuseItsNonfiniteBulge(bool closed)
    {
        var points = new[] { new P2(0, 0), new P2(2, 0), new P2(2, 0), new P2(0, 0) };
        var calls = new List<int>();
        Action read = () => SectionPolylineSegmentSampling.ReadSource(points.Length, closed, index => points[index],
            index => { calls.Add(index); return index == 1 ? double.NaN : 0; });
        read.Should().Throw<ArgumentException>().WithMessage("*segment 1*non-finite live bulge*");
        calls.Should().Equal(0, 1);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ReadSource_NonfiniteEndpointPreventsEveryBulgeGetter(double invalid)
    {
        var points = new[] { new P2(0, 0), new P2(2, 0), new P2(invalid, 0) };
        var bulgeCalls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(points.Length, true, index => points[index],
            index => { bulgeCalls++; return 0; });
        read.Should().Throw<ArgumentException>().WithMessage("*endpoint 2*non-finite*");
        bulgeCalls.Should().Be(0);
    }

    [Fact]
    public void ReadSource_PointGetterFailurePropagatesBeforeAnyBulgeIsRead()
    {
        var original = new InvalidOperationException("native endpoint getter failed"); var bulgeCalls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(3, true,
            index => index == 2 ? throw original : new P2(index, 0),
            index => { bulgeCalls++; return 0; });
        read.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(original);
        bulgeCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    public void ReadSource_OpenCurveNeverReadsUnusedLastBulgeAndRetainsSignedArc(double bulge)
    {
        var source = new[] { new BulgePoint(0, 0, bulge), new BulgePoint(2, 0, 0) };
        var calls = new List<int>();
        var read = SectionPolylineSegmentSampling.ReadSource(source.Length, false,
            index => new P2(source[index].X, source[index].Y),
            index => { calls.Add(index); if (index == 1) throw new InvalidOperationException("unused open getter"); return bulge; });
        calls.Should().Equal(0);
        read.UnreadClosingSegmentIndex.Should().BeNull();
        read.UnreadOpenTerminalVertexIndex.Should().Be(1);
        double.IsNaN(read.Vertices[1].Bulge).Should().BeTrue();
        SectionPolylineSegmentSampling.Sample(read.Vertices, false).Points.Should()
            .Equal(SectionPolylineSegmentSampling.Sample(source, false).Points);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(-.5)]
    public void ReadSource_ValidClosedCurveReadsEveryBulgeAndLeavesAllValuesAndSamplesUnchanged(double bulge)
    {
        var source = new[] { new BulgePoint(0, 0, bulge), new BulgePoint(2, 0, 0), new BulgePoint(2, 2, 0) };
        var calls = new List<int>();
        var read = SectionPolylineSegmentSampling.ReadSource(source.Length, true,
            index => new P2(source[index].X, source[index].Y),
            index => { calls.Add(index); return source[index].Bulge; });
        calls.Should().Equal(0, 1, 2);
        read.UnreadClosingSegmentIndex.Should().BeNull(); read.UnreadOpenTerminalVertexIndex.Should().BeNull();
        read.Vertices.Should().Equal(source);
        SectionPolylineSegmentSampling.Sample(read.Vertices, true).Points.Should()
            .Equal(SectionPolylineSegmentSampling.Sample(source, true).Points);
    }

    [Fact]
    public void Native57B92F7CoincidentEvidenceSkipsOnly36AndRetainsEveryLiteralLiveSpan()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "92F7");
        // Native57B independently recorded GetSegmentType(36)=Coincident and
        // GetBulgeAt(36)=eInvalidInput. This replays that evidence; it does not run Civil.
        source.Length.Should().Be(37);
        source[0].X.Should().Be(204791.6080628091);
        source[0].Y.Should().Be(649011.6199373894);
        source[^1].X.Should().Be(204791.6080628092);
        source[^1].Y.Should().Be(649011.6199373893);
        var calls = new List<string>();
        var scale = Math.Sqrt(3); // Existing conservative identity-XREF norm bound.
        var read = SectionPolylineSegmentSampling.ReadSource(source.Length, true,
            i => { calls.Add("point:" + i); return new P2(source[i].X, source[i].Y); },
            i => { calls.Add("bulge:" + i); if (i == 36) throw new InvalidOperationException("native eInvalidInput"); return source[i].Bulge; },
            i => { calls.Add("native-type:" + i); i.Should().Be(36); return true; }, scale);
        calls.Should().Equal(Enumerable.Range(0, 37).Select(i => "point:" + i)
            .Concat(Enumerable.Range(0, 36).Select(i => "bulge:" + i)).Append("native-type:36"));
        read.UnreadClosingSegmentIndex.Should().BeNull("this is not the exact-equality exception");
        read.NativeCoincidentClosing.Should().NotBeNull();
        read.Vertices.Should().Equal(source);
        var sampled = SectionPolylineSegmentSampling.Sample(read.Vertices, true, scale,
            nativeCoincidentClosing: read.NativeCoincidentClosing);
        sampled.SkippedExactClosingSegmentIndex.Should().BeNull();
        sampled.SkippedNativeCoincidentClosingSegmentIndex.Should().Be(36);
        sampled.Points.Should().Equal(SectionPolylineSegmentSampling.Sample(source, false, scale).Points);
        sampled.Points[^1].Should().Be(new P2(source[^1].X, source[^1].Y));
        sampled.Points[^1].Should().NotBe(sampled.Points[0]);
        sampled.Points.Count.Should().BeGreaterThan(37, "real preceding arcs remain sampled, not replaced by chords");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void NativeCoincidentNeedsTheIndependentFourUlpBound(int ulps, bool accepted)
    {
        var end = 1d; for (var i = 0; i < ulps; i++) end = Math.BitIncrement(end);
        var points = new[] { new P2(1, 1), new P2(2, 2), new P2(end, 1) };
        var nativeCalls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i],
            i => i == 2 ? throw new InvalidOperationException("live bulge") : 0,
            i => { nativeCalls++; return true; });
        if (accepted) read.Should().NotThrow(); else read.Should().Throw<InvalidOperationException>();
        nativeCalls.Should().Be(accepted ? 1 : 0);
    }

    [Theory]
    [InlineData(.9, true)]
    [InlineData(1.0, true)]
    [InlineData(1.1, false)]
    public void NativeCoincidentAlsoNeedsTheFullXrefWorldCap(double capFactor, bool accepted)
    {
        var end = Math.BitIncrement(1d);
        var scale = capFactor * 1e-9 / (end - 1);
        var points = new[] { new P2(1, 0), new P2(2, 2), new P2(end, 0) };
        var nativeCalls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i],
            i => i == 2 ? throw new InvalidOperationException("live bulge") : 0,
            i => { nativeCalls++; return true; }, scale);
        if (accepted) read.Should().NotThrow(); else read.Should().Throw<InvalidOperationException>();
        nativeCalls.Should().Be(accepted ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeNonCoincidentNeverExcusesARealNearClosingLineOrArc(bool arc)
    {
        var points = new[] { new P2(1, 1), new P2(2, 2), new P2(Math.BitIncrement(1), 1) };
        var calls = new List<int>();
        var read = SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i],
            i => { calls.Add(i); return i == 2 && arc ? .5 : 0; }, i => false);
        read.NativeCoincidentClosing.Should().BeNull(); calls.Should().Equal(0, 1, 2);
        var sampled = SectionPolylineSegmentSampling.Sample(read.Vertices, true);
        sampled.SkippedNativeCoincidentClosingSegmentIndex.Should().BeNull();
        sampled.Points[^1].Should().Be(points[0]);
    }

    [Fact]
    public void NativeCoincidentReadFailurePropagatesWithoutInvokingTheMalformedBulge()
    {
        var points = new[] { new P2(1, 1), new P2(2, 2), new P2(Math.BitIncrement(1), 1) };
        var error = new InvalidOperationException("native segment type unavailable"); var bulges = new List<int>();
        Action read = () => SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i],
            i => { bulges.Add(i); return 0; }, i => throw error);
        read.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(error);
        bulges.Should().Equal(0, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NativeCoincidentCannotBeQueriedWithAnInvalidVertexCount(int count)
    {
        var calls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(count, true,
            i => { calls++; return new P2(); }, i => { calls++; return 0; }, i => { calls++; return true; });
        read.Should().Throw<ArgumentOutOfRangeException>(); calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteriorDuplicateOrOpenTerminalNeverUsesNativeClosingCertificate(bool closed)
    {
        var points = new[] { new P2(1, 1), new P2(2, 2), new P2(2, 2), new P2(Math.BitIncrement(1), 1) };
        var nativeCalls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(4, closed, i => points[i],
            i => i == 1 ? throw new InvalidOperationException("interior bulge") : 0,
            i => { nativeCalls++; return true; });
        read.Should().Throw<InvalidOperationException>(); nativeCalls.Should().Be(0);
        var open = SectionPolylineSegmentSampling.ReadSource(4, false, i => points[i], i => 0,
            i => { nativeCalls++; return true; });
        open.NativeCoincidentClosing.Should().BeNull(); nativeCalls.Should().Be(0);
    }

    [Fact]
    public void OrdinaryAndExactClosingCurvesNeverCallTheOptionalNativeTypeReader()
    {
        foreach (var last in new[] { new P2(2, 3), new P2(1, 1) })
        {
            var points = new[] { new P2(1, 1), new P2(2, 2), last };
            var read = SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i], i => 0,
                i => throw new InvalidOperationException("unnecessary native call"));
            read.NativeCoincidentClosing.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonfiniteEndpointsNeverReachTheNativeCertificateReader(double invalid)
    {
        var calls = 0;
        Action read = () => SectionPolylineSegmentSampling.ReadSource(3, true,
            i => new P2(i == 2 ? invalid : 1, 1), i => { calls++; return 0; }, i => { calls++; return true; });
        read.Should().Throw<ArgumentException>(); calls.Should().Be(0);
    }

    [Fact]
    public void CertificateCannotBeReusedForDifferentVerticesOpenGeometryOrXrefScale()
    {
        var points = new[] { new P2(1, 1), new P2(2, 2), new P2(Math.BitIncrement(1), 1) };
        var read = SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i], i => 0, i => true);
        foreach (var call in new Action[]
        {
            () => SectionPolylineSegmentSampling.Sample(read.Vertices.ToArray(), true, nativeCoincidentClosing: read.NativeCoincidentClosing),
            () => SectionPolylineSegmentSampling.Sample(read.Vertices, false, nativeCoincidentClosing: read.NativeCoincidentClosing),
            () => SectionPolylineSegmentSampling.Sample(read.Vertices, true, 2, nativeCoincidentClosing: read.NativeCoincidentClosing),
            () => SectionPolylineSegmentSampling.Sample(read.Vertices, true),
        }) call.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NativeCoincidentActualWorldOutputRejectsAnUnderreportedTransformScale()
    {
        var points = new[] { new P2(1, 1), new P2(2, 2), new P2(Math.BitIncrement(1), 1) };
        var read = SectionPolylineSegmentSampling.ReadSource(3, true, i => points[i], i => 0, i => true);
        var sampled = SectionPolylineSegmentSampling.Sample(read.Vertices, true,
            nativeCoincidentClosing: read.NativeCoincidentClosing);
        var wrongFrame = new SectionPolylineSegmentSampling.Frame(new V3(0, 0, 0),
            new V3(1e9, 0, 0), new V3(0, 1, 0), new V3(0, 0, 1));
        Action world = () => SectionPolylineSegmentSampling.ToWcs(sampled, 0, wrongFrame);
        world.Should().Throw<ArgumentException>().WithMessage("*world gap bound*");
    }

    [Fact]
    public void Native92F7RetainsTiltedShearedFrameElevationAndLiteralWorldEndpointsWithinCap()
    {
        using var fixture = Fixture(); var source = Vertices(fixture, "92F7");
        var read = SectionPolylineSegmentSampling.ReadSource(source.Length, true,
            i => new P2(source[i].X, source[i].Y), i => source[i].Bulge, i => i == 36, 2);
        var sampled = SectionPolylineSegmentSampling.Sample(read.Vertices, true, 2,
            nativeCoincidentClosing: read.NativeCoincidentClosing);
        var frame = new SectionPolylineSegmentSampling.Frame(new V3(10, 20, 30),
            new V3(1, .25, 0), new V3(0, 0, 1), new V3(0, -1, 0));
        var world = SectionPolylineSegmentSampling.ToWcs(sampled, 7, frame);
        var expected = SectionPolylineSegmentSampling.ToWcs(
            SectionPolylineSegmentSampling.Sample(source, false, 2), 7, frame);
        world.Should().Equal(expected);
        world[^1].Should().NotBe(world[0]);
        world[^1].Should().Be(new V3(10 + source[^1].X,
            20 + source[^1].X * .25 - 7, 30 + source[^1].Y));
    }
}
