using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;
using H = MahodAI.CivilDelivery.Shared.StrictHatchCurveAreaRecovery;

namespace MahodAI.Core.Tests;

public sealed class StrictHatchCurveAreaRecoveryTests
{
    private readonly ITestOutputHelper output;
    public StrictHatchCurveAreaRecoveryTests(ITestOutputHelper output) => this.output = output;
    private const double X = 200000, Y = 650000;
    private static H.Point P(double x, double y) => new(X + x, Y + y);
    private static H.Input Input(params H.Edge[] edges) => new(edges, 1, true,
        "External", "Outer", 0, 0, 1, 0, 1, 1);

    private static H.Input Segment(bool clockwise = false, bool major = false)
    {
        var sweep = major ? 3 * Math.PI / 2 : Math.PI / 2;
        var start = P(10, 0);
        var end = major
            ? P(0, clockwise ? 10 : -10)
            : P(0, clockwise ? -10 : 10);
        return Input(new H.ArcEdge(start, end, P(0, 0), 10, 0, sweep, clockwise),
            new H.LineEdge(end, start));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void CircularSegment_IndependentGreenOracle_CwCcwMinorMajor(bool clockwise, bool major)
    {
        var input = Segment(clockwise, major);
        H.TryRecover(input, out var proof, out var refusal).Should().BeTrue(refusal);
        // Independent sector-minus/plus-right-triangle formulas, not helper output.
        var expected = major ? 75 * Math.PI + 50 : 25 * Math.PI - 50;
        Math.Abs(proof!.AreaSourceUnitsSquared - expected)
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared + 1e-12);
        proof.AbsoluteErrorBoundSourceUnitsSquared.Should().BeLessThan(1e-6);
        proof.ArcCount.Should().Be(1);
        proof.BoundaryContract.Should().Be(H.Contract);
    }

    [Theory]
    [InlineData(100, 100, 0)] [InlineData(100, 100, 1)]
    [InlineData(100, 100, 2)] [InlineData(100, 100, 3)]
    [InlineData(200000, 650000, 0)] [InlineData(200000, 650000, 1)]
    [InlineData(200000, 650000, 2)] [InlineData(200000, 650000, 3)]
    public void DiagonalCapsule_SeparatedParallelLinesWithOverlappingBoxes_RecoversWholeArea(
        double originX, double originY, int quarterTurns)
    {
        // Whole synthetic Hatch, not a reshaping of any captured native Hatch.
        // Two diagonal sides have overlapping AABBs, but are sqrt(2) apart.
        H.Point Rotate(double x, double y) => quarterTurns switch
        {
            0 => new(x, y), 1 => new(-y, x), 2 => new(-x, -y), _ => new(y, -x),
        };
        H.Point At(double x, double y)
        {
            var point = Rotate(x, y);
            return new(originX + point.X, originY + point.Y);
        }
        var frame = new H.OriginalArcFrame(Rotate(1, 0), H.FrameOrigin.NativeReferenceVector, "synthetic-capsule");
        var first = new H.LineEdge(At(-.5, .5), At(9.5, 10.5));
        var second = new H.LineEdge(At(10.5, 9.5), At(.5, -.5));
        // Assert the discriminating input instead of relying on the fixture name.
        Math.Max(Math.Min(first.Start.X, first.End.X), Math.Min(second.Start.X, second.End.X))
            .Should().BeLessThan(Math.Min(Math.Max(first.Start.X, first.End.X), Math.Max(second.Start.X, second.End.X)));
        Math.Max(Math.Min(first.Start.Y, first.End.Y), Math.Min(second.Start.Y, second.End.Y))
            .Should().BeLessThan(Math.Min(Math.Max(first.Start.Y, first.End.Y), Math.Max(second.Start.Y, second.End.Y)));
        var radius = Math.Sqrt(.5);
        var input = Input(first,
            new H.ArcEdge(first.End, second.Start, At(10, 10), radius, 5 * Math.PI / 4, 9 * Math.PI / 4, true)
                { OriginalFrame = frame },
            second,
            new H.ArcEdge(second.End, first.Start, At(0, 0), radius, Math.PI / 4, 5 * Math.PI / 4, true)
                { OriginalFrame = frame });
        H.TryRecover(input, out var proof, out var refusal).Should().BeTrue(refusal);
        // Rectangle length 10*sqrt(2), width sqrt(2), plus two radius sqrt(.5) semicircles.
        var independent = 20 + Math.PI / 2;
        Math.Abs(proof!.AreaSourceUnitsSquared - independent)
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared + 1e-12);
        proof.EdgeCount.Should().Be(4); proof.ArcCount.Should().Be(2);
        proof.AbsoluteErrorBoundSourceUnitsSquared.Should().BeLessThan(1e-6);
    }

    [Theory]
    [InlineData("coincident")] [InlineData("reversed")] [InlineData("partial-overlap")]
    [InlineData("endpoint-contact")] [InlineData("near-contact")]
    public void ParallelSeparationProof_DoesNotAcceptCollinearOrUncertainContacts(string scenario)
    {
        var a = new H.LineEdge(P(0, 0), P(10, 10));
        var b = scenario switch
        {
            "coincident" => a,
            "reversed" => new H.LineEdge(a.End, a.Start),
            "partial-overlap" => new H.LineEdge(P(5, 5), P(15, 15)),
            "endpoint-contact" => new H.LineEdge(a.End, P(20, 20)),
            // One ULP apart at survey coordinates is inside the existing four-ULP source bounds.
            _ => new H.LineEdge(new(a.Start.X, Math.BitIncrement(a.Start.Y)),
                new(a.End.X, Math.BitIncrement(a.End.Y))),
        };
        H.LineDomainsStrictlySeparated(a, b).Should().BeFalse(scenario);
    }

    [Fact]
    public void Native76Ha7C26_SeparatedParallelPairDoesNotHideLaterRealIntersection()
    {
        // Unchanged 27 original edge records, native76 capture C567D992...C7C,
        // HA SHA8F2469...5315. Independent 110-digit original-frame oracle:
        // arc13/line14 cross at (204614.6264730459149,649110.0091588506371),
        // 1.066233692 metres from their shared endpoint. No quantity is safe.
        var edges = Parse(Captured7C26);
        edges.Should().HaveCount(27);
        H.LineDomainsStrictlySeparated((H.LineEdge)edges[7], (H.LineEdge)edges[20]).Should().BeTrue();
        H.TryRecover(Input(edges), out var proof, out var refusal).Should().BeFalse();
        proof.Should().BeNull();
        refusal.Should().Contain("edges 13/14").And.Contain("interior curve intersection");
    }

    [Theory]
    [InlineData("missing-capture")] [InlineData("two-loops")] [InlineData("NotClosed")]
    [InlineData("unknown-style")] [InlineData("tilt")] [InlineData("unknown-units")]
    [InlineData("unknown-transform")] [InlineData("nonfinite-elevation")]
    [InlineData("nonfinite-point")] [InlineData("radius")] [InlineData("reverse-angles")]
    [InlineData("wrong-sweep")] [InlineData("circle-endpoint")] [InlineData("full-circle")]
    [InlineData("gap-14m")] [InlineData("over-budget")]
    public void InvalidOrUnsupportedInputNeverPublishesQuantity(string scenario)
    {
        var input = Segment();
        var edges = input.Edges.ToArray();
        var arc = (H.ArcEdge)edges[0];
        switch (scenario)
        {
            case "missing-capture": input = input with { CaptureComplete = false }; break;
            case "two-loops": input = input with { DeclaredLoopCount = 2 }; break;
            case "NotClosed": input = input with { LoopFlags = "External, NotClosed" }; break;
            case "unknown-style": input = input with { HatchStyle = "unknown" }; break;
            case "tilt": input = input with { NormalX = 0.1 }; break;
            case "unknown-units": input = input with { SourceUnitsToMetres = 0 }; break;
            case "unknown-transform": input = input with { MaximumLinearScale = double.NaN }; break;
            case "nonfinite-elevation": input = input with { Elevation = double.PositiveInfinity }; break;
            case "nonfinite-point": edges[0] = arc with { Start = new(double.NaN, Y) }; break;
            case "radius": edges[0] = arc with { Radius = 0 }; break;
            case "reverse-angles": edges[0] = arc with { EndAngle = -1 }; break;
            case "wrong-sweep": edges[0] = arc with { EndAngle = Math.PI / 4 }; break;
            case "circle-endpoint": edges[0] = arc with { Center = P(1, 0) }; break;
            case "full-circle": edges[0] = arc with { EndAngle = 2 * Math.PI }; break;
            case "gap-14m": edges[1] = new H.LineEdge(P(14, 10), arc.Start); break;
            case "over-budget": input = input with { Edges = Enumerable.Repeat<H.Edge>(arc, 513).ToArray() }; break;
        }
        if (scenario is "nonfinite-point" or "radius" or "reverse-angles" or "wrong-sweep" or
            "circle-endpoint" or "full-circle" or "gap-14m") input = input with { Edges = edges };
        H.TryRecover(input, out var proof, out var refusal).Should().BeFalse(scenario);
        proof.Should().BeNull(); refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void InteriorCrossing_IsNotHiddenByAdjacentEndpointRecognition()
    {
        var input = Segment();
        var arc = (H.ArcEdge)input.Edges[0];
        input = Input(arc, new H.LineEdge(arc.End, P(10, 10)),
            new H.LineEdge(P(10, 10), P(0, 0)), new H.LineEdge(P(0, 0), arc.Start));
        H.TryRecover(input, out var proof, out var refusal).Should().BeFalse();
        proof.Should().BeNull(); refusal.Should().Contain("intersection");
    }

    [Fact]
    public void SameCircleSplitArcs_AreExplicitlyUnsupported_NotClaimedAsCircleCoverage()
    {
        var input = Input(new H.ArcEdge(P(10, 0), P(-10, 0), P(0, 0), 10, 0, Math.PI, false),
            new H.ArcEdge(P(-10, 0), P(10, 0), P(0, 0), 10, Math.PI, 2 * Math.PI, false));
        H.TryRecover(input, out _, out var refusal).Should().BeFalse();
        refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FourUlpJoinIsRecognized_ButHostCapAndFiveUlpsAreNotRelaxed()
    {
        var input = Segment();
        var arc = (H.ArcEdge)input.Edges[0];
        var x = arc.End.X;
        for (var i = 0; i < 4; i++) x = Math.BitIncrement(x);
        input = input with { Edges = new H.Edge[] { arc, new H.LineEdge(new(x, arc.End.Y), arc.Start) } };
        H.TryRecover(input, out var proof, out var refusal).Should().BeTrue(refusal);
        proof!.RecognizedJoinCount.Should().Be(1);
        H.TryRecover(input with { MaximumLinearScale = 100 }, out _, out refusal).Should().BeFalse();
        refusal.Should().Contain("nanometre");
        input = input with { Edges = new H.Edge[] { arc,
            new H.LineEdge(new(Math.BitIncrement(x), arc.End.Y), arc.Start) } };
        H.TryRecover(input, out _, out refusal).Should().BeFalse();
        refusal.Should().Contain("ULPs");
    }

    [Fact]
    public void UnitsAndTransformConstrainClosureButDoNotMultiplySourceArea()
    {
        H.TryRecover(Segment(), out var original, out var refusal).Should().BeTrue(refusal);
        H.TryRecover(Segment() with { SourceUnitsToMetres = 0.001, MaximumLinearScale = 2 },
            out var transformed, out refusal).Should().BeTrue(refusal);
        transformed.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AdjacentMinorArcs_WithRoundedJoin_CannotHideA14MicrometreCrossing(bool sameLiteralJoin)
    {
        // Independent high-precision oracle: supporting-circle root at
        // (200000.00000000001455,600000.00001444228948), inside both domains.
        // Strict half-plane derivative signs alone falsely accepted this cusp.
        var a = new H.ArcEdge(new(200122.41791753517,600479.4254161868),
            new(200000,600000), new(201000,599999.999), 1000.0000000005, 0, .5, false);
        var b = new H.ArcEdge(new(sameLiteralJoin ? 200000 : 200000.00000000003,600000),
            new(199877.58208246485,600479.4254161868), new(199000.00000000003,599999.999),
            1000.0000000005, 0, .5, false);
        H.TryRecover(Input(a, b, new H.LineEdge(b.End, a.Start)), out var proof, out var refusal)
            .Should().BeFalse();
        proof.Should().BeNull(); refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CapturedNative67Ha2623B0_MatchesIndependentAnalyticOracle()
    {
        // Raw original scalar strings from native67 ED78C670...89E, source HA
        // SHA8F2469...5315, handle8BB299/2623B0. This is pure replay, not native
        // quantity acceptance or a proof of runtime units/XREF adapter behavior.
        var input = Input(Parse(Captured2623B0));
        H.TryRecover(input, out var proof, out var refusal).Should().BeTrue(refusal);
        output.WriteLine("8BB299/2623B0 " + System.Text.Json.JsonSerializer.Serialize(proof));
        const double independent = 40.5531702231328167272097205491;
        Math.Abs(proof!.AreaSourceUnitsSquared - independent)
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared + 1e-11);
        proof.EdgeCount.Should().Be(11); proof.ArcCount.Should().Be(5);
        proof.AbsoluteErrorBoundSourceUnitsSquared.Should().BeLessThan(1e-6);
        (proof.AbsoluteErrorBoundSourceUnitsSquared / proof.AreaSourceUnitsSquared).Should().BeLessThan(1e-8);
    }

    [Theory]
    [InlineData("78A5")] [InlineData("78BF")] [InlineData("2623AF")]
    public void CapturedNative67SensitiveOrCrossingBoundaries_RemainUnmeasured(string handle)
    {
        // These are exact captured native scalar lists, not fabricated engineering
        // fixes. 78A5 near-retrace;78BF non-adjacent near-contact;2623AF crossing.
        var capture = handle switch { "78A5" => Captured78A5, "78BF" => Captured78BF, _ => Captured2623AF };
        H.TryRecover(Input(Parse(capture)), out var proof, out var refusal).Should().BeFalse();
        proof.Should().BeNull(); refusal.Should().NotBeNullOrWhiteSpace();
        output.WriteLine(handle + " REFUSED: " + refusal);
    }

    [Fact]
    public void NonadjacentArcTangency_RemainsARefusal()
    {
        var a = new H.ArcEdge(P(10,0),P(0,10),P(0,0),10,0,Math.PI/2,false);
        var b = new H.ArcEdge(P(20,-10),P(20,10),P(20,0),10,0,Math.PI,true);
        var input = Input(a,new H.LineEdge(a.End,b.Start),b,new H.LineEdge(b.End,a.Start));
        H.TryRecover(input,out var proof,out var refusal).Should().BeFalse();
        proof.Should().BeNull(); refusal.Should().Contain("touch");
    }

    [Fact]
    public void NonadjacentRetracedLine_RemainsARefusal()
    {
        var a = (H.ArcEdge)Segment().Edges[0];
        var input = Input(a,new H.LineEdge(a.End,P(-10,10)),
            new H.LineEdge(P(-10,10),P(10,10)),new H.LineEdge(P(10,10),a.Start));
        H.TryRecover(input,out var proof,out var refusal).Should().BeFalse();
        proof.Should().BeNull(); refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NearPiAndCancellation_DoNotChooseAFalseMinorOutsideSector()
    {
        H.ClassifySweep(0, Math.BitDecrement(Math.PI)).Should().Be(-1);
        H.ClassifySweep(0, Math.PI).Should().Be(0);
        H.ClassifySweep(0, Math.BitIncrement(Math.PI)).Should().Be(0);
        var definitelyMajor = Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(Math.PI)));
        H.ClassifySweep(0, definitelyMajor).Should().Be(1);
        // Exact difference of these binary64 inputs exceeds true pi, although
        // the ordinary double subtraction rounds DOWN to Math.PI. Choosing the
        // minor wedge here could incorrectly discard an actual intersection.
        var end = Math.PI + 0.2;
        (end - 0.2).Should().Be(Math.PI);
        H.ClassifySweep(0.2, end).Should().Be(0);
        H.ClassifySweep(-0.2, Math.PI - 0.2).Should().Be(0);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)]
    [InlineData(true,false)] [InlineData(true,true)]
    public void MajorArcBoundsRetainInteriorCardinalsAndDoNotHideCrossingOrTangency(bool clockwise,bool tangent)
    {
        var arc=(H.ArcEdge)Segment(clockwise,true).Edges[0];
        var x=tangent ? -10 : -5;
        var direction=clockwise ? -1 : 1;
        var a=P(x,-15*direction);var b=P(x,15*direction);
        var input=Input(arc,new H.LineEdge(arc.End,a),new H.LineEdge(a,b),new H.LineEdge(b,arc.Start));
        H.TryRecover(input,out var proof,out var refusal).Should().BeFalse();
        proof.Should().BeNull();refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CapturedNative67Ha7202_DomainBoundsDoNotLoseExactRootSeparation()
    {
        H.TryRecover(Input(Parse(Captured7202)),out var proof,out var refusal).Should().BeTrue(refusal);
        output.WriteLine("8BB299/7202 "+System.Text.Json.JsonSerializer.Serialize(proof));
        proof!.EdgeCount.Should().Be(67);
        proof.ArcCount.Should().Be(23);
        // Independent 110-digit analytic oracle of the native-captured scalars.
        const double oracle=1344.82481248414769210361959047903524981658520610496548;
        Math.Abs(proof.AreaSourceUnitsSquared-oracle)
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared+1e-11);
    }

    [Fact]
    public void CapturedNative67Ha7196_AllOriginalDisjointLoopsMatchIndependentOracle()
    {
        var loops = new[] { Input(Parse(Captured7196Loop0)), Input(Parse(Captured7196Loop1)) }
            .Select(item => item with { DeclaredLoopCount = 2, LoopFlags = "External, Derived" }).ToArray();
        var expected = new[] { 116.62806484058694829161, 3.290187898319550669507 };
        for (var i = 0; i < loops.Length; i++)
        {
            H.TryRecover(loops[i] with { DeclaredLoopCount = 1 }, out var single, out var reason).Should().BeTrue(reason);
            output.WriteLine($"8BB299/7196 loop{i} " + System.Text.Json.JsonSerializer.Serialize(single));
            Math.Abs(single!.AreaSourceUnitsSquared - expected[i])
                .Should().BeLessThanOrEqualTo(single.AbsoluteErrorBoundSourceUnitsSquared + 1e-12);
        }
        H.TryRecoverDisjointLoops(loops, out var proof, out var refusal).Should().BeTrue(refusal);
        output.WriteLine("8BB299/7196 complete " + System.Text.Json.JsonSerializer.Serialize(proof));
        const double oracle = 119.91825273890649896111807283688463;
        Math.Abs(proof!.AreaSourceUnitsSquared - oracle)
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared + 1e-12);
        proof.LoopCount.Should().Be(2); proof.EdgeCount.Should().Be(13); proof.ArcCount.Should().Be(6);
        proof.BoundaryContract.Should().Be(H.DisjointContract);
        proof.AbsoluteErrorBoundSourceUnitsSquared.Should().BeLessThan(1e-6);
        (proof.AbsoluteErrorBoundSourceUnitsSquared / proof.AreaSourceUnitsSquared).Should().BeLessThan(1e-8);
        loops.Should().OnlyContain(item => item.DeclaredLoopCount == 2);
    }

    [Theory]
    [InlineData("Normal", false)] [InlineData("Normal", true)]
    [InlineData("Outer", false)] [InlineData("Outer", true)]
    [InlineData("Ignore", false)] [InlineData("Ignore", true)]
    public void DisjointComponents_AllStylesAndOppositeDirectionsRetainWholeArea(string style, bool major)
    {
        var loops = new[] { Segment(false, major), Move(Segment(true, major), 50, 0) }
            .Select(item => item with { DeclaredLoopCount = 2, HatchStyle = style }).ToArray();
        H.TryRecoverDisjointLoops(loops, out var proof, out var refusal).Should().BeTrue(refusal);
        var expected = 2 * (major ? 75 * Math.PI + 50 : 25 * Math.PI - 50);
        Math.Abs(proof!.AreaSourceUnitsSquared - expected)
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared + 1e-12);
        proof.LoopCount.Should().Be(2); proof.EdgeCount.Should().Be(4);
    }

    [Theory]
    [InlineData("overlap")] [InlineData("touch")] [InlineData("nested-cardinal")]
    [InlineData("missing-original-loop")] [InlineData("wrong-original-count")]
    [InlineData("partial-loop")] [InlineData("invalid-loop")] [InlineData("metadata")]
    [InlineData("over-loop-budget")] [InlineData("over-edge-budget")]
    public void DisjointContractNeverDropsLoopsOrInfersHoles(string scenario)
    {
        var loops = new[] { Segment(), Move(Segment(), 50, 0) }
            .Select(item => item with { DeclaredLoopCount = 2 }).ToArray();
        switch (scenario)
        {
            case "overlap": loops[1] = Move(loops[0], 1, 0); break;
            case "touch": loops[1] = Move(loops[0], 10, 0); break;
            case "nested-cardinal":
                loops[0] = Segment(false, true) with { DeclaredLoopCount = 2 };
                // The major arc's negative-X cardinal must be retained: the
                // endpoint-only hull would incorrectly call this component disjoint.
                loops[1] = Move(Segment(), -5, 2, .1) with { DeclaredLoopCount = 2 };
                break;
            case "missing-original-loop": loops = new[] { loops[0] }; break;
            case "wrong-original-count": loops[1] = loops[1] with { DeclaredLoopCount = 1 }; break;
            case "partial-loop": loops[1] = loops[1] with { CaptureComplete = false }; break;
            case "invalid-loop": loops[1] = loops[1] with { LoopFlags = "External, NotClosed" }; break;
            case "metadata": loops[1] = loops[1] with { SourceUnitsToMetres = .001 }; break;
            case "over-loop-budget": loops = Enumerable.Repeat(Segment() with { DeclaredLoopCount = 33 }, 33).ToArray(); break;
            case "over-edge-budget":
                loops = Enumerable.Repeat(Segment() with { DeclaredLoopCount = 32,
                    Edges = Enumerable.Repeat(Segment().Edges[0], 17).ToArray() }, 32).ToArray(); break;
        }
        H.TryRecoverDisjointLoops(loops, out var proof, out var refusal).Should().BeFalse(scenario);
        proof.Should().BeNull(); refusal.Should().NotBeNullOrWhiteSpace();
        if (scenario is "overlap" or "touch" or "nested-cardinal") refusal.Should().Contain("AABB-disjoint");
    }

    [Fact]
    public void SingleLoopThroughDisjointEntryRetainsOriginalContractAndProof()
    {
        H.TryRecover(Segment(), out var original, out var refusal).Should().BeTrue(refusal);
        H.TryRecoverDisjointLoops(new[] { Segment() }, out var actual, out refusal).Should().BeTrue(refusal);
        actual.Should().BeEquivalentTo(original);
        actual!.LoopCount.Should().Be(1); actual.BoundaryContract.Should().Be(H.Contract);
    }

    private static H.Input Move(H.Input input, double dx, double dy, double scale = 1)
    {
        H.Point At(H.Point point) => new(X + (point.X - X) * scale + dx, Y + (point.Y - Y) * scale + dy);
        return input with { Edges = input.Edges.Select<H.Edge, H.Edge>(edge => edge is H.ArcEdge arc
            ? new H.ArcEdge(At(arc.Start), At(arc.End), At(arc.Center), arc.Radius * scale,
                arc.StartAngle, arc.EndAngle, arc.Clockwise)
            : new H.LineEdge(At(edge.Start), At(edge.End))).ToArray() };
    }

    private const string Captured7196Loop0 = """
        line=204458.67713447232,647950.10835898505 -> 204456.62182093764,647950.60914020683
        line=204456.62182093764,647950.60914020683 -> 204448.72787901331,647930.56357906619
        arc: start=204448.72787901331,647930.56357906619; end=204445.45812627851,647921.2533250571; centre=204544.43639575402,647891.72154099355; radius=103.28999999197804; angles=2.756064702349843,2.851635302265231; clockwise=False
        line=204445.45812627851,647921.2533250571 -> 204435.8699004245,647889.11757598398
        line=204435.8699004245,647889.11757598398 -> 204437.60434391137,647888.6000764278
        line=204437.60434391137,647888.6000764278 -> 204446.61292372624,647918.79309282533
        arc: start=204446.61292372624,647918.79309282533; end=204455.3915925475,647942.59459432482; centre=204638.48453663729,647861.54506181239; radius=200.22999998988365; angles=3.4315500049204855,3.5583332457334711; clockwise=True
        arc: start=204455.39159254747,647942.59459432482; end=204458.67713447232,647950.10835898505; centre=203626.50230040133,648309.51801655593; radius=906.47132134244123; angles=5.8664447150425989,5.875491582401958; clockwise=False
        """;

    private const string Captured7196Loop1 = """
        line=204457.73070180838,647953.42498804384 -> 204459.87871103123,647952.90335810522
        arc: start=204459.87871103126,647952.90335810522; end=204459.88846724492,647952.92615836218; centre=203626.50230040133,648309.51801655593; radius=906.47132134244123; angles=5.8788478243733975,5.8788751831073505; clockwise=False
        arc: start=204459.88846724492,647952.92615836218; end=204460.06381277501,647953.35438218107; centre=204450.89794297368,647956.85753493884; radius=9.8125046985598683; angles=5.8709595247211483,5.9181213611040864; clockwise=False
        arc: start=204460.06381277501,647953.35438218107; end=204458.01899530995,647954.15706905792; centre=204459.04096847618,647953.75461602921; radius=1.0983613216257719; angles=5.9102057019450722,9.0496278148917391; clockwise=False
        line=204458.01899530995,647954.15706905792 -> 204457.73070180838,647953.42498804384
        """;

    [Theory]
    [InlineData(0,false,false)] [InlineData(0,true,false)] [InlineData(0,false,true)] [InlineData(0,true,true)]
    [InlineData(1,false,false)] [InlineData(1,true,false)] [InlineData(1,false,true)] [InlineData(1,true,true)]
    [InlineData(2,false,false)] [InlineData(2,true,false)] [InlineData(2,false,true)] [InlineData(2,true,true)]
    [InlineData(3,false,false)] [InlineData(3,true,false)] [InlineData(3,false,true)] [InlineData(3,true,true)]
    public void ExplicitOriginalUnitAxisFrame_UsesBothDirectedAnglesWithoutReplacingEndpoints(int axis, bool clockwise, bool major)
    {
        H.Point Rotate(H.Point point) { var x=point.X-X; var y=point.Y-Y; return axis switch {
            0=>P(x,y),1=>P(-y,x),2=>P(-x,-y),_=>P(y,-x) }; }
        var vector=axis switch {0=>new H.Point(1,0),1=>new H.Point(0,1),2=>new H.Point(-1,0),_=>new H.Point(0,-1)};
        var input=Segment(clockwise,major);
        var original=(H.ArcEdge)input.Edges[0];
        var arc=new H.ArcEdge(Rotate(original.Start),Rotate(original.End),P(0,0),10,
            original.StartAngle,original.EndAngle,clockwise) {
            OriginalFrame=new(vector,H.FrameOrigin.NativeReferenceVector,"synthetic-direct-native-getter-fixture") };
        input=Input(arc,new H.LineEdge(arc.End,arc.Start));
        var before=input.Edges.ToArray();
        H.TryRecover(input,out var proof,out var refusal).Should().BeTrue(refusal);
        var oracle=major ? 75*Math.PI+50 : 25*Math.PI-50;
        Math.Abs(proof!.AreaSourceUnitsSquared-oracle).Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared+1e-12);
        proof.ExplicitFrameArcCount.Should().Be(1); proof.BoundaryContract.Should().Be(H.ExplicitFrameContract);
        input.Edges.Should().Equal(before); arc.OriginalFrame.Vector.Should().Be(vector);
    }

    [Theory]
    [InlineData("missing-evidence")] [InlineData("unknown-origin")] [InlineData("zero-vector")]
    [InlineData("nonfinite-vector")] [InlineData("nonunit-vector")] [InlineData("nonaxis-vector")]
    [InlineData("contradictory-native-frame")] [InlineData("contradictory-dwg-frame")]
    public void InvalidExplicitFrameIsNeverSilentlyDiscardedForLegacyRecovery(string scenario)
    {
        var frame=new H.OriginalArcFrame(new(1,0),H.FrameOrigin.NativeReferenceVector,"direct-getter-fixture");
        frame=scenario switch {
            "missing-evidence"=>frame with {EvidenceIdentity=" "},
            "unknown-origin"=>frame with {Origin=(H.FrameOrigin)999},
            "zero-vector"=>frame with {Vector=new(0,0)},
            "nonfinite-vector"=>frame with {Vector=new(double.NaN,0)},
            "nonunit-vector"=>frame with {Vector=new(2,0)},
            "nonaxis-vector"=>frame with {Vector=new(.6,.8)},
            "contradictory-native-frame"=>frame with {Vector=new(0,1)},
            _=>frame with {Vector=new(-1,0),Origin=H.FrameOrigin.OriginalDwgOcsAngles} };
        var valid=Segment();
        H.TryRecover(valid,out _,out _).Should().BeTrue();
        var input=valid with {Edges=new H.Edge[]{(H.ArcEdge)valid.Edges[0] with {OriginalFrame=frame},valid.Edges[1]}};
        H.TryRecover(input,out var proof,out var refusal).Should().BeFalse(scenario);
        proof.Should().BeNull();refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Ha7202_ExplicitOriginalDwgAnglesAreNotAnInferredNativeReferenceVector()
    {
        // Independent source records exported by ACadSharp3.7.1 at commit
        // d7dc111023477d8a9fffc2153139459c95b4f345. Its DWG decoder reads raw
        // centre/radius/start/end/isccw, not endpoints; ToEntity defines the OCS
        // orientation. Inventory SHA A7952C2C8F1165233F6E2CCB92F2360D7C060521C7BA24FABA16E63BD0BFE007.
        // This fixture labels that ORIGINAL DWG convention explicitly. It does
        // NOT assert that a native ReferenceVector getter has been called.
        const string evidence="DWG:8F2469EDD9F9D25FCC09C5563FAC1C2BD9B3CE17A9B06D1E9DAE032CF8175315/7202;ACadSharp:3.7.1;commit:d7dc111023477d8a9fffc2153139459c95b4f345";
        var original=Input(Parse(Captured7202));
        var edges=original.Edges.ToArray();
        using var json=System.Text.Json.JsonDocument.Parse(OriginalDwg7202ArcRecords);
        static double N(System.Text.Json.JsonElement x)=>x.ValueKind==System.Text.Json.JsonValueKind.String
            ? double.Parse(x.GetString()!,CultureInfo.InvariantCulture) : x.GetDouble();
        var matched=0;
        foreach(var item in json.RootElement.EnumerateArray())
        {
            var index=item.GetProperty("index").GetInt32();var arc=(H.ArcEdge)edges[index];
            var centre=item.GetProperty("center");
            arc.Center.Should().Be(new H.Point(N(centre[0]),N(centre[1])));
            arc.Radius.Should().Be(N(item.GetProperty("radius")));
            arc.StartAngle.Should().Be(N(item.GetProperty("start_angle")));
            arc.EndAngle.Should().Be(N(item.GetProperty("end_angle")));
            arc.Clockwise.Should().Be(!bool.Parse(item.GetProperty("counterclockwise").GetString()!));
            edges[index]=arc with {OriginalFrame=new(new(1,0),H.FrameOrigin.OriginalDwgOcsAngles,evidence)};
            matched++;
        }
        matched.Should().Be(23);
        H.TryRecover(original with {Edges=edges},out var proof,out var refusal).Should().BeTrue(refusal);
        output.WriteLine("8BB299/7202 explicit-original-DWG-OCS "+System.Text.Json.JsonSerializer.Serialize(proof));
        // Independent Decimal110 original-angle integral, not a candidate frame
        // fitted to native endpoints. It is an oracle, not itself an error bound.
        const double oracle=1344.82481248209272423895995600792759;
        Math.Abs(proof!.AreaSourceUnitsSquared-oracle).Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared+1e-11);
        proof.ExplicitFrameArcCount.Should().Be(23);proof.EdgeCount.Should().Be(67);
        proof.AbsoluteErrorBoundSourceUnitsSquared.Should().BeLessThan(1e-6);
        (proof.AbsoluteErrorBoundSourceUnitsSquared/proof.AreaSourceUnitsSquared).Should().BeLessThan(1e-8);
        for(var i=0;i<edges.Length;i++){edges[i].Start.Should().Be(original.Edges[i].Start);edges[i].End.Should().Be(original.Edges[i].End);}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitFrame_ClockwiseAppliesToStartAndEndAnglesNotJustSweep(bool clockwise)
    {
        var start=P(0,clockwise ? -10 : 10);var end=P(-10,0);
        var arc=new H.ArcEdge(start,end,P(0,0),10,Math.PI/2,Math.PI,clockwise) {
            OriginalFrame=new(new(1,0),H.FrameOrigin.NativeReferenceVector,"synthetic-nonzero-original-start-angle") };
        H.TryRecover(Input(arc,new H.LineEdge(end,start)),out var proof,out var refusal).Should().BeTrue(refusal);
        Math.Abs(proof!.AreaSourceUnitsSquared-(25*Math.PI-50))
            .Should().BeLessThanOrEqualTo(proof.AbsoluteErrorBoundSourceUnitsSquared+1e-12);
    }

    [Fact]
    public void ExplicitFrame_RetainsHostNanometreCapAndDoesNotMultiplySourceArea()
    {
        var input=Segment();var arc=(H.ArcEdge)input.Edges[0];
        input=input with {Edges=new H.Edge[]{arc with {
            OriginalFrame=new(new(1,0),H.FrameOrigin.NativeReferenceVector,"synthetic-original-frame") },input.Edges[1]}};
        H.TryRecover(input,out var original,out var refusal).Should().BeTrue(refusal);
        H.TryRecover(input with {SourceUnitsToMetres=.001,MaximumLinearScale=2},out var small,out refusal).Should().BeTrue(refusal);
        small.Should().BeEquivalentTo(original);
        H.TryRecover(input with {MaximumLinearScale=100},out var large,out refusal).Should().BeFalse();
        large.Should().BeNull();refusal.Should().Contain("nanometre");
    }

    [Fact]
    public void DisjointMixedFrames_ReportExactExplicitCountWithoutClaimingAllFramesKnown()
    {
        var first=Segment();var arc=(H.ArcEdge)first.Edges[0];
        first=first with {DeclaredLoopCount=2,Edges=new H.Edge[]{arc with {
            OriginalFrame=new(new(1,0),H.FrameOrigin.NativeReferenceVector,"synthetic-direct-frame") },first.Edges[1]}};
        var second=Move(Segment(),50,0) with {DeclaredLoopCount=2};
        H.TryRecoverDisjointLoops(new[]{first,second},out var proof,out var refusal).Should().BeTrue(refusal);
        proof!.LoopCount.Should().Be(2);proof.ArcCount.Should().Be(2);proof.ExplicitFrameArcCount.Should().Be(1);
        proof.BoundaryContract.Should().Be(H.ExplicitFrameDisjointContract);
    }

    private static H.Edge[] Parse(string text)
    {
        var edges = new List<H.Edge>();
        static double N(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        static H.Point Point(string s) { var p = s.Split(','); return new(N(p[0]), N(p[1])); }
        foreach (var raw in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("line=", StringComparison.Ordinal))
            {
                var p = raw[5..].Split(" -> "); edges.Add(new H.LineEdge(Point(p[0]), Point(p[1])));
            }
            else
            {
                var parts = raw[5..].Split(';', StringSplitOptions.TrimEntries)
                    .Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
                var angles = Point(parts["angles"]);
                var arc = new H.ArcEdge(Point(parts["start"]), Point(parts["end"]), Point(parts["centre"]),
                    N(parts["radius"]), angles.X, angles.Y, bool.Parse(parts["clockwise"]));
                if (parts.TryGetValue("reference_vector", out var vector))
                    arc = arc with { OriginalFrame = new(Point(vector), H.FrameOrigin.NativeReferenceVector,
                        "captured-native-frame-edge-" + edges.Count) };
                edges.Add(arc);
            }
        }
        return edges.ToArray();
    }

    private const string Captured7C26 = """
        line=204768.18515864832,648998.65556930506 -> 204766.46526750689,649001.69935732079
        arc: start=204766.46526750689,649001.69935732079; end=204765.94279510411,649002.36438281497; centre=204764.02751634188,649000.3219070473; radius=2.7999999997209275; angles=0.51432412362679403,0.81752578472691484; clockwise=False; reference_vector=1,0
        line=204765.94279510411,649002.36438281497 -> 204761.67750119511,649006.36405173119
        arc: start=204761.67750119511,649006.36405173119; end=204734.5608894391,649028.923437602; centre=204558.29616626067,648789.47452894645; radius=297.32983787959682; angles=0.81752872583351077,0.93623344652324747; clockwise=False; reference_vector=1,0
        line=204734.5608894391,649028.923437602 -> 204663.76491439529,649078.81429802976
        line=204663.76491439529,649078.81429802976 -> 204654.11798862618,649082.18800954998
        arc: start=204654.11798862618,649082.18800954998; end=204651.9657062738,649083.17928143637; centre=204656.01277264644,649089.13421668182; radius=7.2000000001301654; angles=1.8370971358321597,2.1677100229160757; clockwise=True; reference_vector=1,0
        line=204651.9657062738,649083.17928143637 -> 204638.24985941598,649092.50078373658
        arc: start=204638.24985941598,649092.50078373658; end=204636.96820491512,649093.61374274769; centre=204642.29692578828,649098.45571898145; radius=7.1999999994666002; angles=2.1677100229213995,2.4040155529700722; clockwise=True; reference_vector=1,0
        line=204636.96820491512,649093.61374274769 -> 204627.26860414536,649104.28840585751
        arc: start=204627.26860414536,649104.28840585751; end=204626.84134308234,649104.67122876807; centre=204625.19632380555,649102.4054150997; radius=2.8000000001410159; angles=0.73757710062565496,0.94282183455219448; clockwise=False; reference_vector=1,0
        arc: start=204626.84134308234,649104.67122876807; end=204624.73084433877,649106.21666298853; centre=204815.3482906576,649364.31667768909; radius=320.85920343592295; angles=2.1987706518441303,2.2069232565594827; clockwise=True; reference_vector=1,0
        arc: start=204624.73084433877,649106.21666298853; end=204615.33628754845,649109.83883036068; centre=204613.44046248405,649090.92768516124; radius=19.005935005384622; angles=0.93471829443607235,1.4708810583409861; clockwise=False; reference_vector=1,0
        arc: start=204615.33628754845,649109.83883036068; end=204613.72872903434,649110.5844066022; centre=204615.60561013949,649112.52536443574; radius=2.6999999989892447; angles=1.6707115950333513,2.339412558338184; clockwise=True; reference_vector=1,0
        line=204613.72872903434,649110.5844066022 -> 204617.02749567284,649108.47065472277
        arc: start=204617.02749567284,649108.47065472277; end=204624.07735150235,649105.33182000089; centre=204613.44049901108,649090.92777989199; radius=17.905837103573873; angles=4.9140791752861146,5.348465510647773; clockwise=True; reference_vector=1,0
        arc: start=204624.07735150235,649105.33182000089; end=204626.19508550933,649103.7810876841; centre=204815.34827733081,649364.31665948755; radius=321.95918087733634; angles=4.0762620503308824,4.0844146550461407; clockwise=False; reference_vector=1,0
        arc: start=204626.19508550933,649103.7810876841; end=204626.45449401191,649103.54865948844; centre=204625.19632380595,649102.40541509981; radius=1.6999999997882846; angles=5.3403634724710347,5.545608206431984; clockwise=True; reference_vector=1,0
        line=204626.45449401191,649103.54865948844 -> 204636.15409478164,649092.8739963785
        arc: start=204636.15409478164,649092.8739963785; end=204637.63155760907,649091.59100196301; centre=204642.29692578802,649098.4557189818; radius=8.2999999995824432; angles=3.8791697542683434,4.1154752843080882; clockwise=False; reference_vector=1,0
        line=204637.63155760907,649091.59100196301 -> 204651.34740446688,649082.2694996628
        arc: start=204651.34740446688,649082.2694996628; end=204653.78998857382,649081.13739037584; centre=204656.01277264638,649089.13421668194; radius=8.3000000001900744; angles=4.1154752842766378,4.4412745670854612; clockwise=False; reference_vector=1,0
        line=204653.78998857382,649081.13739037584 -> 204663.25681061472,649077.82666451926
        line=204663.25681061472,649077.82666451926 -> 204733.91793318093,649028.03083641699
        arc: start=204733.91793318093,649028.03083641699; end=204760.92507025268,649005.56165053672; centre=204558.29618370809,648789.47454991774; radius=296.22981064743061; angles=5.3469902180969875,5.4656565758856317; clockwise=True; reference_vector=1,0
        line=204760.92507025268,649005.56165053672 -> 204765.19036416171,649001.5619816205
        line=204765.19036416171,649001.5619816205 -> 204768.18515864832,648998.65556930506
        """;

    private const string OriginalDwg7202ArcRecords = """
        [{"index":2,"center":[203972.1265225612,648944.3188959621],"radius":"314.3499999999877","start_angle":"3.051814337856678","end_angle":"3.138504671657325","counterclockwise":"False"},{"index":11,"center":[203675.02856588035,649068.4940867525],"radius":"27.500000000797453","start_angle":"2.9533545830338745","end_angle":"3.060913218609757","counterclockwise":"False"},{"index":13,"center":[203673.99590284622,649081.2658850071],"radius":"27.499999999828134","start_angle":"3.060913218623906","end_angle":"3.2415114872602007","counterclockwise":"False"},{"index":15,"center":[203777.60019200624,649098.1534183491],"radius":"129.53266642804965","start_angle":"3.1428676934449493","end_angle":"3.5054164318327885","counterclockwise":"False"},{"index":16,"center":[203777.2442975789,649098.2531750491],"radius":"129.16457366454105","start_angle":"3.505675157734122","end_angle":"3.612501503680873","counterclockwise":"False"},{"index":17,"center":[203648.2995551485,649164.6456424448],"radius":"15.88127087959304","start_angle":"5.7704350352049305","end_angle":"6.597942922135511","counterclockwise":"True"},{"index":18,"center":[203665.51785883083,649170.0310795746],"radius":"2.1685400884011825","start_angle":"2.9236815649396823","end_angle":"4.144748141080077","counterclockwise":"False"},{"index":21,"center":[203666.79852819417,649169.8754440835],"radius":"5.88493822851002","start_angle":"2.1724957075851234","end_angle":"3.3172428982202042","counterclockwise":"True"},{"index":22,"center":[203648.29955514846,649164.6456424448],"radius":"13.3812708796971","start_angle":"5.963805803544462","end_angle":"6.7959355791544205","counterclockwise":"False"},{"index":23,"center":[203776.50889175822,649098.5580280501],"radius":"130.86881933606432","start_angle":"2.6694111368833324","end_angle":"2.7776858823358737","counterclockwise":"True"},{"index":24,"center":[203777.24429758827,649098.2531750449],"radius":"131.6645736748074","start_angle":"2.7775101494407872","end_angle":"3.1406979903540124","counterclockwise":"True"},{"index":26,"center":[203673.9959028462,649081.2658850072],"radius":"29.999999999799826","start_angle":"3.0416738199228956","end_angle":"3.2222720885620735","counterclockwise":"True"},{"index":28,"center":[203675.02856587924,649068.4940867524],"radius":"29.999999999677847","start_angle":"3.2222720885702003","end_angle":"3.3298307241485507","counterclockwise":"True"},{"index":37,"center":[203972.12652255985,648944.3188959622],"radius":"316.84999999865175","start_angle":"3.144680635522766","end_angle":"3.2314152348917866","counterclockwise":"True"},{"index":45,"center":[204085.5592289094,648764.7160874999],"radius":"412.3499996183821","start_angle":"3.213386012603967","end_angle":"3.2807447430606524","counterclockwise":"True"},{"index":47,"center":[203676.1714499955,648700.4106991257],"radius":"1.9999999999678748","start_angle":"6.144033217741438","end_angle":"6.3659417476879945","counterclockwise":"False"},{"index":49,"center":[203659.7582958921,648687.8273238324],"radius":"17.416040252666313","start_angle":"0.18955299186821062","end_angle":"1.235134900447143","counterclockwise":"False"},{"index":51,"center":[203654.37797535732,648689.2132370473],"radius":"20.0592888826709","start_angle":"1.3161586299012784","end_angle":"2.111030329943131","counterclockwise":"False"},{"index":54,"center":[203653.91314277425,648688.3041089962],"radius":"21.50559261603378","start_angle":"4.169980148423073","end_angle":"4.995945504914756","counterclockwise":"True"},{"index":56,"center":[203661.50554949252,648686.6712625038],"radius":"17.999999999973383","start_angle":"4.967646717553161","end_angle":"4.967650854358021","counterclockwise":"True"},{"index":57,"center":[203661.50555094285,648686.6712628823],"radius":"17.999999999969404","start_angle":"4.967650771085872","end_angle":"6.201160920544955","counterclockwise":"True"},{"index":59,"center":[203631.05703485146,648700.7542714992],"radius":"49.49999996985231","start_angle":"6.201160920537419","end_angle":"6.300106056835585","counterclockwise":"True"},{"index":61,"center":[204085.5592289094,648764.7160875002],"radius":"409.84999961841646","start_angle":"3.0024405641180163","end_angle":"3.0698931093208066","counterclockwise":"False"}]
        """;

    private const string Captured78A5 = """
        line=203805.57415149239,648481.95805776911 -> 203807.33365585154,648477.55178049731
        arc: start=203807.33365585154,648477.55178049731; end=203809.92465028324,648478.31479588884; centre=203808.59336768778,648478.05480568844; radius=1.3564321779070496; angles=3.5215106739607491,6.4760508565112902; clockwise=False
        arc: start=203809.92465028324,648478.31479588884; end=203808.77885420484,648482.52578946389; centre=203780.48087968488,648472.5646329684; radius=30.000000011007515; angles=0.19286554929888339,0.33846391686493577; clockwise=False
        line=203808.77885420484,648482.52578946389 -> 203807.06421443672,648487.39679339668
        arc: start=203807.06421443672,648487.39679339668; end=203804.61784479057,648486.96264100133; centre=203805.87669065685,648486.97877371788; radius=1.2589492363106423; angles=0.33846391702232637,3.1544074340808796; clockwise=False
        arc: start=203804.6178447906,648486.96264100133; end=203805.73723669793,648481.54964807653; centre=203819.16684312909,648487.14909342991; radius=14.550193028325141; angles=3.1544074342234114,3.5366233264689568; clockwise=False
        line=203805.7372366979,648481.54964807653 -> 203805.57415149239,648481.95805776911
        """;

    private const string Captured78BF = """
        arc: start=204620.59970262245,649135.04486565234; end=204642.08186691941,649119.76412344642; centre=203819.39861056686,647985.9497830671; radius=1400.8364282572888; angles=5.3212637278738262,5.3400831606967571; clockwise=True
        arc: start=204642.08186691941,649119.76412344642; end=204642.76262793614,649120.42654629343; centre=204642.46790952983,649120.04840868199; radius=0.47942360415749674; angles=3.7763386001707211,7.1919318596533754; clockwise=False
        line=204642.76262793611,649120.42654629343 -> 204622.03683937108,649136.95227925666
        arc: start=204622.03683937108,649136.95227925666; end=204620.60889079203,649135.03839477454; centre=204621.29211414148,649136.0182802818; radius=1.1945583923059317; angles=0.89767257756065677,4.1035122891330769; clockwise=False
        line=204620.60889079203,649135.03839477454 -> 204620.59970262245,649135.04486565234
        """;

    private const string Captured2623AF = """
        line=203646.24606134021,649194.3889791806 -> 203644.85176037991,649190.2148089268
        line=203644.85176037991,649190.2148089268 -> 203653.14327228253,649183.5609253546
        line=203653.14327228253,649183.5609253546 -> 203658.01436839884,649174.6213547223
        line=203658.01436839884,649174.6213547223 -> 203659.06822161429,649163.65083643992
        arc: start=203659.06822161429,649163.65083643992; end=203651.03308947053,649146.34860106802; centre=203777.24429750213,649098.25317478611; radius=135.06457367509239; angles=2.6361491665666059,2.7775101494415382; clockwise=False
        arc: start=203651.03308947053,649146.34860106802; end=203644.51008005376,649120.34196391888; centre=203765.04523019769,649103.93059542228; radius=121.64725823524778; angles=2.78541241775716,3.0062705456670491; clockwise=False
        arc: start=203644.51008005376,649120.34196391888; end=203640.9024961442,649113.54478981951; centre=203673.35728617775,649100.67543288774; radius=34.913231645874752; angles=2.5432215305836698,2.7640796584934653; clockwise=False
        line=203640.9024961442,649113.54478981951 -> 203638.00135583873,649091.1363868037
        arc: start=203638.00135583873,649091.1363868037; end=203638.68433121286,649081.67650209088; centre=203681.0009988005,649089.48623563326; radius=43.031294353359044; angles=3.1032355485958112,3.3240936765040416; clockwise=False
        line=203638.68433121286,649081.67650209088 -> 203643.99729091491,649081.55447153666
        arc: start=203643.99729091491,649081.55447153666; end=203644.45548131174,649087.30824393278; centre=203714.92423186428,649078.80147323979; radius=70.980349055624131; angles=3.1803877430135223,3.2617282648379589; clockwise=True
        arc: start=203644.45548131174,649087.30824393278; end=203640.87772450119,649088.96425567148; centre=203643.15718259971,649089.19613047666; radius=2.2912213223981794; angles=0.96837349509930171,3.0402177065892526; clockwise=True
        line=203640.87772450116,649088.96425567148 -> 203640.75404321455,649091.75526204112
        line=203640.75404321455,649091.75526204112 -> 203641.23584935506,649101.89619507687
        line=203641.23584935506,649101.89619507687 -> 203642.87741472921,649113.22754143237
        line=203642.87741472921,649113.22754143237 -> 203646.27667907343,649119.40434571099
        arc: start=203646.27667907343,649119.40434571099; end=203653.27577155243,649145.49398005276; centre=203777.2442975016,649098.25317478634; radius=132.66457367447759; angles=3.3017093910069653,3.505675157737711; clockwise=True
        arc: start=203653.27577155241,649145.49398005276; end=203661.61127703069,649163.28311931191; centre=203777.24429750183,649098.25317478587; radius=132.66457367488331; angles=3.5056751577403658,3.6538925713130115; clockwise=True
        arc: start=203661.61127703069,649163.28311931191; end=203661.00414073013,649168.84706020332; centre=203648.02407783686,649164.61558215728; radius=13.652378516844097; angles=6.1854303711199101,6.5983198421055134; clockwise=False
        arc: start=203661.00414073013,649168.84706020332; end=203663.53333359672,649174.74506466824; centre=203666.72976719486,649169.88307552633; radius=5.8186017360814297; angles=2.962585903103911,4.1308059726689574; clockwise=True
        arc: start=203663.53333359672,649174.74506466824; end=203646.24578154759,649194.38909622689; centre=203634.66069837619,649166.76485613326; radius=29.955179733050688; angles=0.26966126924613676,1.1736945796397877; clockwise=False
        line=203646.24578154759,649194.38909622689 -> 203646.24606134021,649194.3889791806
        """;

    private const string Captured7202 = """
        line=203666.04575583746,648839.44013480039 -> 203659.74734982394,648909.17863143503
        line=203659.74734982394,648909.17863143503 -> 203659.0425253149,648916.134979125
        arc: start=203659.0425253149,648916.134979125; end=203657.77802132303,648943.34819038433; centre=203972.12652256119,648944.31889596209; radius=314.34999999998769; angles=3.051814337856678,3.1385046716573251; clockwise=True
        line=203657.77802132303,648943.34819038433 -> 203657.67773590211,648975.82412588969
        line=203657.67773590211,648975.82412588969 -> 203654.68879300117,648986.93666802126
        line=203654.68879300117,648986.93666802126 -> 203654.60812686884,648993.4401156588
        line=203654.60812686884,648993.4401156588 -> 203654.14660886757,649004.52018418559
        line=203654.14660886757,649004.52018418559 -> 203652.8712836171,649024.76743186184
        line=203652.8712836171,649024.76743186184 -> 203652.48901973723,649031.56243546144
        line=203652.48901973723,649031.56243546144 -> 203650.93083866686,649048.03782095131
        line=203650.93083866686,649048.03782095131 -> 203648.01434054453,649063.34805628727
        arc: start=203648.01434054453,649063.34805628727; end=203647.61801844632,649066.27780847345; centre=203675.02856588035,649068.49408675253; radius=27.500000000797453; angles=2.9533545830338745,3.0609132186097572; clockwise=True
        line=203647.61801844632,649066.27780847345 -> 203646.58535541312,649079.04960672848
        arc: start=203646.58535541312,649079.04960672848; end=203646.6330655558,649084.00908303284; centre=203673.99590284622,649081.2658850071; radius=27.499999999828134; angles=3.0609132186239059,3.2415114872602007; clockwise=True
        line=203646.6330655558,649084.00908303284 -> 203648.06763087053,649098.31857761659
        arc: start=203648.06763087053,649098.31857761659; end=203656.5463496749,649144.24765799765; centre=203777.60019200624,649098.1534183491; radius=129.53266642804965; angles=3.1428676934449493,3.5054164318327885; clockwise=True
        arc: start=203656.5463496749,649144.24765799765; end=203662.13847217531,649156.85467714677; centre=203777.2442975789,649098.25317504909; radius=129.16457366454105; angles=3.5056751577341219,3.612501503680873; clockwise=True
        arc: start=203662.13847217531,649156.85467714677; end=203663.4006021519,649169.56226162391; centre=203648.29955514849,649164.64564244484; radius=15.881270879593041; angles=5.7704350352049305,6.5979429221355108; clockwise=False
        arc: start=203663.40060215199,649169.56226162391; end=203664.35195546295,649171.85953122878; centre=203665.51785883083,649170.0310795746; radius=2.1685400884011825; angles=2.9236815649396823,4.1447481410800773; clockwise=True
        line=203664.35195546289,649171.8595312289 -> 203663.61546885362,649174.76697478897
        line=203663.61546885362,649174.76697478897 -> 203663.46739294313,649174.72683933564
        arc: start=203663.46739294313,649174.72683933564; end=203661.00414081663,649168.84706046223; centre=203666.79852819417,649169.87544408347; radius=5.8849382285100198; angles=2.1724957075851234,3.3172428982202042; clockwise=False
        arc: start=203661.0041408166,649168.84706046223; end=203659.95997570432,649158.08111635258; centre=203648.29955514846,649164.64564244484; radius=13.3812708796971; angles=5.9638058035444619,6.7959355791544205; clockwise=True
        arc: start=203659.95997570426,649158.08111635258; end=203654.21022250628,649145.13788822189; centre=203776.50889175822,649098.55802805012; radius=130.86881933606432; angles=2.6694111368833324,2.7776858823358737; clockwise=False
        arc: start=203654.21022250628,649145.13788822189; end=203645.5797766071,649098.37097048271; centre=203777.24429758827,649098.2531750449; radius=131.6645736748074; angles=2.7775101494407872,3.1406979903540124; clockwise=False
        line=203645.5797766071,649098.37097048271 -> 203644.145534893,649084.25846467155
        arc: start=203644.145534893,649084.25846467155; end=203644.09348746465,649078.8481268849; centre=203673.99590284619,649081.26588500722; radius=29.999999999799826; angles=3.0416738199228956,3.2222720885620735; clockwise=False
        line=203644.09348746465,649078.8481268849 -> 203645.12615049785,649066.07632862986
        arc: start=203645.12615049785,649066.07632862986; end=203645.55850187771,649062.88023533591; centre=203675.02856587924,649068.49408675241; radius=29.999999999677847; angles=3.2222720885702003,3.3298307241485507; clockwise=False
        line=203645.55850187771,649062.88023533591 -> 203648.44194506147,649047.80243059248
        line=203648.44194506147,649047.80243059248 -> 203649.99564158596,649031.37446232745
        line=203649.99564158596,649031.37446232745 -> 203650.37570116582,649024.61864167766
        line=203650.37570116582,649024.61864167766 -> 203651.64988158451,649004.3895695156
        line=203651.64988158451,649004.3895695156 -> 203652.10877228939,648993.37257680472
        line=203652.10877228939,648993.37257680472 -> 203652.19288692271,648986.59110485786
        line=203652.19288692271,648986.59110485786 -> 203655.17875578633,648975.48999163951
        line=203655.17875578633,648975.48999163951 -> 203655.27803324256,648943.34047044173
        arc: start=203655.27803324256,648943.34047044173; end=203656.55385154005,648915.89686569374; centre=203972.12652255985,648944.31889596221; radius=316.84999999865175; angles=3.1446806355227661,3.2314152348917866; clockwise=False
        line=203656.55385154005,648915.89686569374 -> 203657.25871004257,648908.94018250122
        line=203657.25871004257,648908.94018250122 -> 203663.55729835064,648839.19966742769
        line=203663.55729835064,648839.19966742769 -> 203667.30311210669,648802.81814645859
        line=203667.30311210669,648802.81814645859 -> 203664.92850528145,648791.70796997147
        line=203664.92850528145,648791.70796997147 -> 203668.35448013729,648758.43291035097
        line=203668.35448013729,648758.43291035097 -> 203672.70702966966,648750.33215731476
        line=203672.70702966966,648750.33215731476 -> 203674.2714579181,648735.13752062642
        arc: start=203674.2714579181,648735.13752062642; end=203677.19502075118,648707.52171993989; centre=204085.55922890941,648764.71608749987; radius=412.34999961838213; angles=3.2133860126039671,3.2807447430606524; clockwise=False
        line=203677.19502075118,648707.52171993989 -> 203678.15211791612,648700.68810602569
        arc: start=203678.15211791612,648700.68810602569; end=203678.1646052748,648700.24537510274; centre=203676.17144999551,648700.41069912573; radius=1.9999999999678748; angles=6.1440332177414376,6.3659417476879945; clockwise=True
        line=203678.1646052748,648700.24537510274 -> 203676.86238975803,648684.54579505639
        arc: start=203676.86238975803,648684.54579505639; end=203665.49503080166,648671.38322669512; centre=203659.7582958921,648687.82732383243; radius=17.416040252666313; angles=0.18955299186821062,1.235134900447143; clockwise=True
        line=203665.49503080166,648671.38322669512 -> 203659.43080601221,648669.80076752766
        arc: start=203659.43080601221,648669.80076752766; end=203644.06074722786,648672.01062462351; centre=203654.37797535732,648689.21323704731; radius=20.059288882670899; angles=1.3161586299012784,2.111030329943131; clockwise=True
        line=203644.06074722786,648672.01062462351 -> 203644.05045542881,648672.01724763995
        line=203644.05045542881,648672.01724763995 -> 203642.81194351925,648669.88526297186
        arc: start=203642.81194351925,648669.88526297186; end=203659.92980351351,648667.65731003566; centre=203653.91314277425,648688.30410899618; radius=21.50559261603378; angles=4.1699801484230727,4.9959455049147561; clockwise=False
        line=203659.92980351351,648667.65731003566 -> 203666.05045594744,648669.25449399429
        arc: start=203666.05045594744,648669.25449399429; end=203666.05052799717,648669.25451279583; centre=203661.50554949252,648686.67126250383; radius=17.999999999973383; angles=4.9676467175531611,4.9676508543580207; clockwise=False
        arc: start=203666.05052799717,648669.25451279583; end=203679.44503288466,648685.19647894625; centre=203661.50555094285,648686.6712628823; radius=17.999999999969404; angles=4.9676507710858724,6.2011609205449547; clockwise=False
        line=203679.44503288469,648685.19647894625 -> 203680.39061016147,648696.69861567719
        arc: start=203680.39061016147,648696.69861567719; end=203680.5499487741,648701.59180863923; centre=203631.05703485146,648700.75427149923; radius=49.499999969852311; angles=6.2011609205374194,6.3001060568355847; clockwise=False
        line=203680.5499487741,648701.59180863923 -> 203679.67085565199,648707.86847856489
        arc: start=203679.67085565199,648707.86847856489; end=203676.76226154392,648735.35520093329; centre=204085.55922890941,648764.71608750022; radius=409.84999961841646; angles=3.0024405641180163,3.0698931093208066; clockwise=True
        line=203676.76226154392,648735.35520093329 -> 203675.06289393181,648751.86044717382
        line=203675.06289393181,648751.86044717382 -> 203670.78627922793,648759.22367700806
        line=203670.78627922793,648759.22367700806 -> 203667.45578158411,648791.57140643592
        line=203667.45578158411,648791.57140643592 -> 203669.83038840935,648802.68158292293
        line=203669.83038840935,648802.68158292293 -> 203666.04575583746,648839.44013480039
        """;

    private const string Captured2623B0 = """
        line=203679.60535412122,648683.11894067202 -> 203679.13779284959,648683.04884399648
        arc: start=203679.13779284959,648683.04884399648; end=203666.05080787692,648669.25340790872; centre=203661.5058308226,648686.67015799519; radius=17.999999999960419; angles=0.202566598456559,1.3155345360937138; clockwise=True
        arc: start=203666.05080787692,648669.25340790872; end=203666.05073582719,648669.25338910718; centre=203661.50582937227,648686.67015761673; radius=17.999999999973387; angles=1.3155344528215656,1.3155385896264251; clockwise=True
        line=203666.05073582719,648669.25338910718 -> 203659.93008339326,648667.65620514855
        arc: start=203659.93008339326,648667.65620514855; end=203642.9439485209,648669.80540558894; centre=203653.91342265395,648688.30300410907; radius=21.505592616061335; angles=1.2872398022622313,2.1060687963817228; clockwise=True
        line=203642.9439485209,648669.80540558894 -> 203642.64422099691,648667.87933559832
        line=203642.64422099691,648667.87933559832 -> 203643.79543255456,648667.53875357285
        arc: start=203643.79543255456,648667.53875357285; end=203659.78987911655,648667.27974614117; centre=203652.25796154456,648696.14320258168; radius=29.83000000008402; angles=4.4247469867922851,4.967646634297445; clockwise=False
        line=203659.78987911655,648667.27974614117 -> 203670.5931109766,648670.09884897608
        arc: start=203670.5931109766,648670.09884897608; end=203679.39632459683,648680.57627575309; centre=203667.6060984096,648681.54553652846; radius=11.829999999881915; angles=4.9676466342916052,6.2011609205767737; clockwise=False
        line=203679.39632459689,648680.57627575309 -> 203679.60535412122,648683.11894067202
        """;
}
