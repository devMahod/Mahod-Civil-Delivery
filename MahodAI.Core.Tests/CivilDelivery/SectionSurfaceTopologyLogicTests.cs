using MahodAI.CivilDelivery.Shared;
using Xunit;
using Point = MahodAI.CivilDelivery.Shared.SectionSurfaceTopologyLogic.Point;
using Sample = MahodAI.CivilDelivery.Shared.SectionVerificationRecoveryPolicy.Sample;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class SectionSurfaceTopologyLogicTests
{
    [Fact]
    public void IncomingTerminalIsRetainedAndIsolatedSentinelsAreNotGeometry()
    {
        Point[] raw = [new(-5, 8, 0, -1), new(0, 10, 0, 2), new(1, 11, 0, -1), new(5, 9, 0, -1)];
        var original = raw.ToArray();
        Assert.Equal(raw[1..3], SectionSurfaceTopologyLogic.RequireSingleChain(raw));
        Assert.Equal(original, raw);
    }

    [Fact]
    public void NativeReverseIndexOrderIsTraversedAndReturnedLeftToRight()
    {
        Point[] raw = [new(0, 10, 0, -1), new(1, 11, 0, 0), new(2, 12, 0, 1)];
        Assert.Equal(raw, SectionSurfaceTopologyLogic.RequireSingleChain(raw));
    }

    [Fact]
    public void ConnectedVerticalFaceKeepsBothNativeElevations()
    {
        Point[] raw = [new(0, 10, 0, 1), new(1, 11, 0, 2), new(1, 11.2, 0, 3), new(2, 12, 0, -1)];
        Assert.Equal(raw, SectionSurfaceTopologyLogic.RequireSingleChain(raw));
    }

    [Fact]
    public void DescendingVerticalFaceIsCompletelyReversedBeforeStableNormalization()
    {
        Point[] raw = [new(2, 12, 0, 1), new(1, 11.2, 0, 2), new(1, 11, 0, 3), new(0, 10, 0, -1)];
        var chain = SectionSurfaceTopologyLogic.RequireSingleChain(raw);
        Assert.Equal(raw.Reverse(), chain);
        var normalized = SectionFurnitureLogic.NormalizeSectionPoints(
            chain.Select(p => (p.X, p.Y, p.Z)).ToList(), 0, 2, 0, 20);
        Assert.True(SectionFurnitureLogic.TrySlopeEvidence(normalized, 1, 2, out var right));
        Assert.Equal(11.2, right!.FromElevation);
        Assert.Equal(80, right.Percent, 9);
        Assert.True(SectionFurnitureLogic.TrySlopeEvidence(normalized, 0, 1, out var left));
        Assert.Equal(11, left!.ToElevation);
        Assert.Equal(100, left.Percent, 9);
    }

    public static IEnumerable<object[]> InvalidTopologies()
    {
        yield return [Array.Empty<Point>()];
        yield return [new Point[] { new(0, 1, 0, -1), new(1, 2, 0, -1) }];
        yield return [new Point[] { new(0, 1, 0, -2), new(1, 2, 0, -1) }];
        yield return [new Point[] { new(0, 1, 0, 2), new(1, 2, 0, -1) }];
        yield return [new Point[] { new(0, 1, 0, 0), new(1, 2, 0, -1) }];
        yield return [new Point[] { new(0, 1, 0, 1), new(1, 2, 0, 0) }];
        // Two components: never sort them into an invented line through the gap.
        yield return [new Point[] { new(0, 1, 0, 1), new(1, 2, 0, -1), new(2, 3, 0, 3), new(3, 4, 0, -1) }];
        // One apparent start must not hide a disconnected cycle.
        yield return [new Point[] { new(0, 1, 0, 1), new(1, 2, 0, -1), new(2, 3, 0, 3), new(3, 4, 0, 2) }];
        yield return [new Point[] { new(0, 1, 0, 2), new(1, 2, 0, 2), new(2, 3, 0, -1) }];
        // A topologically connected but reversing cut cannot be repaired by sorting.
        yield return [new Point[] { new(0, 1, 0, 1), new(2, 2, 0, 2), new(1, 3, 0, -1) }];
        yield return [new Point[] { new(double.NaN, 1, 0, 1), new(1, 2, 0, -1) }];
        yield return [new Point[] { new(0, double.PositiveInfinity, 0, 1), new(1, 2, 0, -1) }];
        yield return [new Point[] { new(0, 1, double.NegativeInfinity, 1), new(1, 2, 0, -1) }];
        // Malformed orphan data is not silently excused just because it is isolated.
        yield return [new Point[] { new(0, 1, 0, 1), new(1, 2, 0, -1), new(double.NaN, 3, 0, -1) }];
    }

    [Theory, MemberData(nameof(InvalidTopologies))]
    public void MalformedDisconnectedOrAmbiguousTopologyFailsClosed(Point[] raw) =>
        Assert.Throws<InvalidOperationException>(() => SectionSurfaceTopologyLogic.RequireSingleChain(raw));

    [Fact]
    public void Native53RebuiltFgMatchesEverySourceBreakpointWithoutIsolatedOutsideSurfacePoint()
    {
        var raw = NativeFg();
        var source = NativeSource();
        Assert.NotNull(SectionVerificationRecoveryPolicy.SurfaceMismatch(source,
            raw.Select(p => new Sample(p.X, p.Y)).ToArray()));

        var chain = SectionSurfaceTopologyLogic.RequireSingleChain(raw);
        Assert.Equal(16, chain.Count);
        Assert.Equal(raw[15], chain[^1]); // incoming terminal, not all SegmentTo=-1 points discarded
        Assert.DoesNotContain(raw[16], chain); // independently probed outside surface, no incident edge
        var normalized = SectionFurnitureLogic.NormalizeSectionPoints(
            chain.Select(p => (p.X, p.Y, p.Z)).ToList(), -25, 26, 280, 290);
        Assert.Null(SectionVerificationRecoveryPolicy.SurfaceMismatch(source,
            normalized.Select(p => new Sample(p.Offset, p.Elevation)).ToArray()));
        Assert.True(SectionFurnitureLogic.TrySlopeEvidence(normalized,
            -14.217338, -11.091163, out var slope));
        Assert.Equal(2.6500567692389736, slope!.Percent, 9);
    }

    [Fact]
    public void ConnectedUnsupportedTailIsNotClippedToMakeSourceComparisonPass()
    {
        var raw = NativeFg();
        raw[15] = raw[15] with { SegmentTo = 16 };
        var chain = SectionSurfaceTopologyLogic.RequireSingleChain(raw);
        Assert.Equal(17, chain.Count);
        Assert.NotNull(SectionVerificationRecoveryPolicy.SurfaceMismatch(NativeSource(),
            chain.Select(p => new Sample(p.X, p.Y)).ToArray()));
    }

    [Fact]
    public void RealChangedElevationStillFailsOriginalToleranceAfterTopologyExtraction()
    {
        var raw = NativeFg();
        raw[8] = raw[8] with { Y = raw[8].Y + 0.01 };
        var chain = SectionSurfaceTopologyLogic.RequireSingleChain(raw);
        Assert.NotNull(SectionVerificationRecoveryPolicy.SurfaceMismatch(NativeSource(),
            chain.Select(p => new Sample(p.X, p.Y)).ToArray()));
    }

    // Exact native 1.2.53 capture, not generated/reinterpreted terrain:
    // native53-diag-20260909-093431-9a8293f7.json
    // SHA256 b176db579454fc0ca3c795fc3f603a3b663986dfddcceb452eafd68dd9fa8785.
    // New FG Section BD8E44 -> source 80EC18, 600-DESIGN-FINAL. Native source
    // FindElevationAtXY at CL endpoint B and the final tail midpoint both returned
    // PointNotOnEntityException. Their lack of coverage must never be extrapolated.
    private static Point[] NativeFg() =>
    [
        new(-24.67058793308045, 284.5747812408441, 0, 1),
        new(-24.194256667474914, 284.5943250319662, 0, 2),
        new(-20.794370914299275, 284.75644482877976, 0, 3),
        new(-20.24204681956799, 284.7470002902069, 0, 4),
        new(-19.820000845318468, 284.7397834595044, 0, 5),
        new(-19.591780657656134, 284.7358809783608, 0, 6),
        new(-8.771940976451107, 285.0226128722532, 0, 7),
        new(-6.576609997291845, 285.09409383730633, 0, 8),
        new(-1.3245652667111498, 285.2651027782802, 0, 9),
        new(-0.5112061228516335, 285.2648640963896, 0, 10),
        new(-1.4485412869191805E-11, 285.25598467744766, 0, 11),
        new(0.9448776495858918, 285.2395725803965, 0, 12),
        new(2.210397339336233, 285.2578342812101, 0, 13),
        new(2.944907296865371, 285.2673684216126, 0, 14),
        new(15.066571700802893, 285.44220671681893, 0, 15),
        new(22.945141675631874, 285.5446953990706, 0, -1),
        new(25.922746482811547, 285.52175559416736, 0, -1),
    ];

    private static Sample[] NativeSource() =>
    [
        new(-24.670587933015273, 284.574781240846),
        new(-24.19425666743508, 284.5943250319662),
        new(-20.794370914259446, 284.75644482877976),
        new(-20.242046819528156, 284.7470002902069),
        new(-19.82000084527864, 284.7397834595044),
        new(-19.591780657616304, 284.7358809783608),
        new(-8.771940976328722, 285.0226128722502),
        new(-6.576609997118764, 285.09409383730673),
        new(-1.3245652666713164, 285.2651027782802),
        new(-0.5112061228690042, 285.2648640963965),
        new(0.9448776496764208, 285.23957258039735),
        new(2.2103973393760663, 285.2578342812101),
        new(2.944907296905205, 285.2673684216126),
        new(15.066571700893421, 285.4422067168214),
        new(22.945141675697055, 285.5446953990719),
    ];
}
