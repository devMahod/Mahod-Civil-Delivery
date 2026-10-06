using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// The pure plan-geometry maths behind the live collector (QuantitySegmentEvidenceReader) for the BoQ one-object rule:
/// bulge / arc / circle tessellation as in natali-boq-2809/boq_geometry.py (0.5 m chords, 0.005 m arc sagitta, radius in
/// metres), at most 8° per chord whatever the drawing unit, exact endpoints, closed chains without a repeated first
/// point, both bounds, and the block insertion point key. Synthetic geometry only.
/// </summary>
public sealed class QuantityGeometryEvidenceTests
{
    private static readonly double QuarterBulge = Math.Tan(Math.PI / 8); // 90° arc

    private static List<(double X, double Y, double Bulge)> Chain(params (double X, double Y, double Bulge)[] vertices) =>
        vertices.ToList();

    private static double Distance((double X, double Y) point, double cx, double cy) =>
        Math.Sqrt((point.X - cx) * (point.X - cx) + (point.Y - cy) * (point.Y - cy));

    private static IEnumerable<double> ChordLengths(List<(double X, double Y)> points) =>
        points.Zip(points.Skip(1), (a, b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));

    [Fact]
    public void StraightChainsKeepExactlyTheirStoredVerticesAndAClosedChainNeverRepeatsItsFirstVertex()
    {
        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 0, 0), (5, 0, 0), (5, 5, 0)), false, 1.0, out var open)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        open.Should().Equal((0.0, 0.0), (5.0, 0.0), (5.0, 5.0));

        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0)), true, 1.0, out var square)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        square.Should().Equal((0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0));

        // An open polyline's last vertex has no outgoing segment: its bulge is ignored.
        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 0, 0), (10, 0, 0.5)), false, 1.0, out var last)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        last.Should().Equal((0.0, 0.0), (10.0, 0.0));
    }

    [Fact]
    public void APositiveBulgeIsTheReferenceCounterClockwiseArcWithExactEndpointsAndHalfMetreChords()
    {
        QuantityGeometryEvidence.BulgeChordCount(10, 0, 0, 10, QuarterBulge).Should().Be(32,
            "ceil(π/2 · 10 m / 0.5 m), like boq_geometry._bulge_points");
        QuantityGeometryEvidence.TessellatePolyline(Chain((10, 0, QuarterBulge), (0, 10, 0)), false, 1.0, out var points)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);

        points.Should().HaveCount(33);
        points[0].Should().Be((10.0, 0.0));
        points[^1].Should().Be((0.0, 10.0));
        for (var k = 0; k <= 32; k++)
        {
            points[k].X.Should().BeApproximately(10 * Math.Cos(Math.PI / 2 * k / 32), 1e-9);
            points[k].Y.Should().BeApproximately(10 * Math.Sin(Math.PI / 2 * k / 32), 1e-9);
        }
        ChordLengths(points).Should().OnlyContain(length => length <= QuantityGeometryEvidence.ArcMaxChordMetres);
    }

    [Fact]
    public void ANegativeBulgeTurnsClockwiseAndAMajorArcKeepsItsCentreOnTheFarSide()
    {
        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 10, -QuarterBulge), (10, 0, 0)), false, 1.0, out var clockwise)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        clockwise.Should().HaveCount(33);
        clockwise.Should().OnlyContain(p => Math.Abs(Distance(p, 0, 0) - 10) < 1e-9);
        clockwise[16].X.Should().BeApproximately(10 * Math.Cos(Math.PI / 4), 1e-9, "the arc bulges away from its centre");
        clockwise[16].Y.Should().BeApproximately(10 * Math.Sin(Math.PI / 4), 1e-9);
        clockwise.Select(p => p.Y).Should().BeInDescendingOrder();

        // 270° CCW from (10,0) to (0,10): centre (10,10), through (20,10) and (10,20).
        var major = Math.Tan(3 * Math.PI / 8);
        QuantityGeometryEvidence.BulgeChordCount(10, 0, 0, 10, major).Should().Be(95, "ceil(3π/2 · 10 m / 0.5 m)");
        QuantityGeometryEvidence.TessellatePolyline(Chain((10, 0, major), (0, 10, 0)), false, 1.0, out var points)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        points.Should().HaveCount(96);
        points.Should().OnlyContain(p => Math.Abs(Distance(p, 10, 10) - 10) < 1e-9);
        points.Max(p => p.X).Should().BeApproximately(20, 0.01);
        points.Max(p => p.Y).Should().BeApproximately(20, 0.01);
    }

    [Fact]
    public void AClosedChainTessellatesItsBulgedClosingSegmentWithoutRepeatingTheFirstVertex()
    {
        // A square whose closing side (0,10) → (0,0) is a half circle bulging west (bulge 1): centre (0,5), r 5.
        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 1)), true, 1.0, out var points)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);

        points.Take(4).Should().Equal((0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0));
        points.Should().HaveCount(4 + 31,
            "ceil(π · 5 m / 0.5 m) = 32 closing chords: 31 interior points; the consumer adds the last chord back to (0,0)");
        points.Skip(4).Should().OnlyContain(p => p.X < 0 && Math.Abs(Distance(p, 0, 5) - 5) < 1e-9);
        points.Count(p => p == (0.0, 0.0)).Should().Be(1, "the first vertex is never repeated");
    }

    [Fact]
    public void TheSameArcDrawnInMillimetresGetsTheSameChordsAndNoChordSpansMoreThanEightDegrees()
    {
        QuantityGeometryEvidence.TessellatePolyline(Chain((10, 0, QuarterBulge), (0, 10, 0)), false, 1.0, out var metres)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        QuantityGeometryEvidence.TessellatePolyline(Chain((10000, 0, QuarterBulge), (0, 10000, 0)), false, 0.001, out var millimetres)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        millimetres.Should().HaveCount(metres.Count);
        for (var i = 0; i < metres.Count; i++)
        {
            (millimetres[i].X / 1000).Should().BeApproximately(metres[i].X, 1e-9);
            (millimetres[i].Y / 1000).Should().BeApproximately(metres[i].Y, 1e-9);
        }

        // A 1 m radius: the reference bound alone gives 4 chords of 22.5°; at most 8° per chord gives 12 of 7.5°.
        QuantityGeometryEvidence.BulgeChordCount(1, 0, 0, 1, QuarterBulge).Should().Be(12);
        QuantityGeometryEvidence.TessellatePolyline(Chain((1, 0, QuarterBulge), (0, 1, 0)), false, 1.0, out var small)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        small.Should().HaveCount(13);
        small.Zip(small.Skip(1), (a, b) => Math.Abs(Math.Atan2(b.Y, b.X) - Math.Atan2(a.Y, a.X)))
            .Should().OnlyContain(step => step <= QuantityGeometryEvidence.MaxChordAngleDegrees * Math.PI / 180 + 1e-12);
    }

    [Theory]
    [InlineData(10.0, 1.0, 126)]      // 0.5 m chords: ceil(2π / 0.05)
    [InlineData(100.0, 1.0, 1257)]    // ceil(2π / 0.005)
    [InlineData(1.0, 1.0, 45)]        // the reference's 0.005 m sagitta gives 32; at most 8° per chord gives 45
    [InlineData(0.004, 1.0, 45)]      // below the sagitta: the reference's π/8 step gives 16; 8° gives 45
    [InlineData(10000.0, 0.001, 126)] // the 10 m circle drawn in millimetres
    public void CircleChordCountsFollowTheReferenceBoundsInMetresWithAtMostEightDegreesPerChord(
        double radius, double metresPerUnit, int expected) =>
        QuantityGeometryEvidence.ArcChordCount(radius, 2 * Math.PI, metresPerUnit).Should().Be(expected);

    [Fact]
    public void AnArcRunsCounterClockwiseFromItsExactStartToItsExactEnd()
    {
        // Centre (100,200), r 10, from 270° to 0°: the sweep wraps to 90° like the reference.
        QuantityGeometryEvidence.ArcChordCount(10, Math.PI / 2).Should().Be(32);
        QuantityGeometryEvidence.TessellateArc(100, 200, 10, 3 * Math.PI / 2, 0, (100, 190), (110, 200), 1.0, out var points)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);

        points.Should().HaveCount(33);
        points[0].Should().Be((100.0, 190.0));
        points[^1].Should().Be((110.0, 200.0));
        points[16].X.Should().BeApproximately(100 + 10 * Math.Cos(7 * Math.PI / 4), 1e-9);
        points[16].Y.Should().BeApproximately(200 + 10 * Math.Sin(7 * Math.PI / 4), 1e-9);
        points.Should().OnlyContain(p => Math.Abs(Distance(p, 100, 200) - 10) < 1e-9);
    }

    [Fact]
    public void ACircleIsOneClosedChainFromAngleZeroThatNeverRepeatsItsFirstPoint()
    {
        QuantityGeometryEvidence.TessellateCircle(5, -3, 10, 1.0, out var points)
            .Should().Be(QuantityGeometryEvidence.StatusComplete);
        points.Should().HaveCount(126);
        points[0].Should().Be((15.0, -3.0));
        points[^1].Should().NotBe(points[0]);
        points.Should().OnlyContain(p => Math.Abs(Distance(p, 5, -3) - 10) < 1e-9);
        ChordLengths(points).Should().OnlyContain(length => length <= QuantityGeometryEvidence.ArcMaxChordMetres);
    }

    [Fact]
    public void AChainAboveTheTessellationBoundKeepsOnlyAnOverLimitStatus()
    {
        // The same quarter circle in a kilometre drawing (r = 10 km) needs 31,416 half-metre chords.
        var chords = QuantityGeometryEvidence.BulgeChordCount(10, 0, 0, 10, QuarterBulge, 1000.0);
        chords.Should().BeGreaterThan(QuantityGeometryEvidence.MaxTessellatedPoints);
        QuantityGeometryEvidence.TessellatePolyline(Chain((10, 0, QuarterBulge), (0, 10, 0)), false, 1000.0, out var points)
            .Should().Be(QuantityGeometryEvidence.OverTessellationLimit(1 + chords))
            .And.StartWith("over-limit:").And.EndWith(">4096");
        points.Should().BeEmpty();

        QuantityGeometryEvidence.TessellateCircle(0, 0, 10, 1000.0, out var circle).Should().StartWith("over-limit:");
        circle.Should().BeEmpty();
        QuantityGeometryEvidence.TessellateArc(0, 0, 10, 0, Math.PI, (10, 0), (-10, 0), 1000.0, out var arc)
            .Should().StartWith("over-limit:");
        arc.Should().BeEmpty();

        // The stored-vertex bound keeps its meaning and text.
        QuantityGeometryEvidence.OverLimit(1025).Should().Be("over-limit:1025>1024");
    }

    [Fact]
    public void NonPlanNormalsAndDegenerateInputsBecomeStatusesWithoutPoints()
    {
        QuantityGeometryEvidence.IsPlanNormal(0, 0, 1).Should().BeTrue();
        QuantityGeometryEvidence.IsPlanNormal(0, 1e-4, 0.999999995).Should().BeTrue();
        QuantityGeometryEvidence.IsPlanNormal(0, 0, -1).Should().BeFalse("a −Z object is not plan geometry in the reference");
        QuantityGeometryEvidence.IsPlanNormal(1, 0, 0).Should().BeFalse();
        QuantityGeometryEvidence.IsPlanNormal(0, 0, double.NaN).Should().BeFalse();

        QuantityGeometryEvidence.TessellatePolyline(Chain((1, 2, 0)), false, 1.0, out var one)
            .Should().Be(QuantityGeometryEvidence.StatusFewerThanTwoVertices);
        one.Should().BeEmpty();
        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 0, 0), (double.NaN, 1, 0)), false, 1.0, out _)
            .Should().Be(QuantityGeometryEvidence.StatusNonFiniteVertex);
        QuantityGeometryEvidence.TessellatePolyline(Chain((0, 0, double.PositiveInfinity), (1, 1, 0)), false, 1.0, out _)
            .Should().Be(QuantityGeometryEvidence.StatusNonFiniteVertex);
        QuantityGeometryEvidence.TessellateArc(0, 0, 0, 0, 1, (0, 0), (0, 0), 1.0, out var arc)
            .Should().Be(QuantityGeometryEvidence.StatusDegenerateArc);
        arc.Should().BeEmpty();
        QuantityGeometryEvidence.TessellateCircle(0, 0, -1, 1.0, out _).Should().Be(QuantityGeometryEvidence.StatusDegenerateArc);
        QuantityGeometryEvidence.TessellateCircle(double.NaN, 0, 1, 1.0, out _).Should().Be(QuantityGeometryEvidence.StatusNonFiniteVertex);
    }

    [Fact]
    public void TheInsertionPointIsGeometryEvidenceOutsideSummariesAndDecisionScopes()
    {
        QuantityGeometryEvidence.InsertPointKey.Should().Be("cad_insert_point");
        QuantityGeometryEvidence.IsGeometryKey(QuantityGeometryEvidence.InsertPointKey).Should().BeTrue();
        QuantityGeometryEvidence.IsGeometryKey(QuantityGeometryEvidence.InsertPointStatusKey).Should().BeTrue();
        QuantityGeometryEvidence.IsGeometryKey("cad_block_name_effective").Should().BeFalse();
        var text = QuantityGeometryEvidence.FormatPoint(204168.12341, 649233.5);
        text.Should().Be("204168.12341,649233.5");
        // v4.5: 0.1 µm, as the golden inputs — at 0.1 mm the plan lengths of C1/C2/C3/M2/M4 drifted 0.02–0.22 m (live 30.09)
        QuantityGeometryEvidence.FormatPoint(204168.123456789, -0.00000004).Should().Be("204168.1234568,0");
        QuantityGeometryEvidence.TryParsePoint(text, out var x, out var y).Should().BeTrue();
        x.Should().Be(204168.12341);
        y.Should().Be(649233.5);

        var measurement = new QuantityMeasurement { Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'" };
        measurement.Parameters["block_name"] = "SIGN-301";
        QuantityCadMetadataPolicy.AppendEvidence(measurement, new Dictionary<string, string>
        {
            ["block_name_effective"] = "SIGN-301",
            [QuantityGeometryEvidence.RawInsertPoint] = text,
        });
        measurement.Parameters[QuantityGeometryEvidence.InsertPointKey].Should().Be(text);
        QuantityCadMetadataPolicy.Summarize(new[] { measurement }).Select(f => f.Key).Should().Equal("cad_block_name_effective");

        NeutralQuantityRecord Record(bool withPoint)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["block_name"] = "SIGN-301", ["cad_block_name_effective"] = "SIGN-301",
            };
            if (withPoint) parameters[QuantityGeometryEvidence.InsertPointKey] = text;
            return new NeutralQuantityRecord
            {
                RecordId = "r1", ProjectProfileId = "p", RunId = "run",
                Source = new QuantitySource
                {
                    Drawing = "x.dwg", DrawingPath = @"C:\x.dwg", DrawingHash = new string('a', 64),
                    Handle = "2A", EntityType = "BLOCKREFERENCE", Layer = "L",
                },
                Measurement = new QuantityMeasurement { Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'", Parameters = parameters },
                Classification = new QuantityClassification { RuleKey = "layer:L|count" },
            };
        }
        var hash = new string('a', 64);
        SemanticHintPolicy.Capture("p", @"C:\x.dwg", hash, "layer:L|count", new[] { Record(true) })
            .Should().Be(SemanticHintPolicy.Capture("p", @"C:\x.dwg", hash, "layer:L|count", new[] { Record(false) }),
                "adding the insertion point to a scan must not invalidate a saved decision");
    }
}
