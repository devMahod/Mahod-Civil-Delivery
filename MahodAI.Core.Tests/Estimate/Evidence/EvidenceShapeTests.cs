using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// SYNTHETIC host-metre shapes. The nearby-text query runs on the record's full geometry (every vertex, every
/// hatch ring with its fill style); a stand-in outline is never queried and never previewed.
/// </summary>
public sealed class EvidenceShapeCollectorFixTests
{
    private static (double X, double Y)[] CollectorFixSquare(double x, double y, double size) =>
        new (double X, double Y)[] { (x, y), (x + size, y), (x + size, y + size), (x, y + size) };

    [Fact]
    public void TextInAHatchHoleIsOutsideAndOnlyTheFilledRingTouches()
    {
        // A roundabout ring hatch: outer 100 x 100, island (hole) 40 x 40 in the middle.
        var outer = CollectorFixSquare(0, 0, 100);
        var hole = CollectorFixSquare(30, 30, 40);
        var ring = EvidenceShape.Rings(new[] { outer, hole });
        var index = new NearbyTextIndex();
        index.Add("גינון", 50, 50, "ISLAND", "host");     // island centre, 20 m from the hole edge
        index.Add("near-hole", 50, 68, "NEAR", "host");   // in the hole, 2 m from its edge
        index.Add("on-ring", 50, 85, "RING", "host");     // on the filled ring itself

        index.Find(ring).Hits.Select(hit => (hit.Text, hit.DistanceMetres))
            .Should().Equal(("on-ring", 0d), ("near-hole", 2d));

        // What the old outer-loop-only outline answered: the island label "touching" the ring.
        index.Find(outer, closed: true).Hits.Should().Contain(hit => hit.Text == "גינון" && hit.DistanceMetres == 0);
    }

    [Fact]
    public void SeveralSeparateHatchAreasAreAllInside()
    {
        var shape = EvidenceShape.Rings(new[] { CollectorFixSquare(0, 0, 10), CollectorFixSquare(100, 0, 10) });
        var index = new NearbyTextIndex();
        index.Add("second area", 105, 5, "B", "host");
        index.Find(shape).Hits.Select(hit => (hit.Text, hit.DistanceMetres)).Should().Equal(("second area", 0d));
    }

    [Theory]
    [InlineData(EvidenceFill.Normal, true, false)]
    [InlineData(EvidenceFill.Outer, false, false)]
    [InlineData(EvidenceFill.Ignore, true, true)]
    public void AnIslandInsideAHoleFollowsTheHatchStyle(EvidenceFill fill, bool islandFilled, bool holeFilled)
    {
        var shape = EvidenceShape.Rings(new[]
        {
            CollectorFixSquare(0, 0, 100), CollectorFixSquare(20, 20, 60), CollectorFixSquare(40, 40, 20),
        }, fill);
        shape.Contains(10, 10).Should().BeTrue("the outer area is filled in every style");
        shape.Contains(30, 30).Should().Be(holeFilled);
        shape.Contains(50, 50).Should().Be(islandFilled);
        shape.Contains(150, 50).Should().BeFalse();
        EvidenceShape.Open(CollectorFixSquare(0, 0, 100)).Contains(10, 10).Should().BeFalse("an open path has no interior");
    }

    [Fact]
    public void AStandInOutlineIsNeverQueriedNorPreviewed()
    {
        var index = new NearbyTextIndex();
        index.Add("label of another object", 50, 50, "X", "host");
        var standIn = EvidenceShape.Approximate("region-boundary-not-read");
        standIn.PointCount.Should().Be(0);
        index.Query(standIn).Status.Should().Be("unavailable:approximate-geometry:region-boundary-not-read");
        EvidenceJson.GeometrySample(standIn).Status.Should().Be("unavailable:approximate-geometry:region-boundary-not-read");
        index.Query((EvidenceShape?)null).Status.Should().Be("unavailable:no-geometry-sample");
    }

    [Fact]
    public void TheQueryKeepsEveryVertexWhileThePreviewIsDownsampled()
    {
        // An L-shaped kerb of 300 vertices, 1 m apart: 150 along x to the corner (149,0), then 150 up along y.
        var points = new List<(double X, double Y)>();
        for (var i = 0; i < 150; i++) points.Add((i, 0));
        for (var j = 1; j <= 150; j++) points.Add((149, j));
        var index = new NearbyTextIndex();
        index.Add("אבן שפה", 149, 0, "CORNER", "host");

        var shape = EvidenceShape.Open(points);
        shape.PointCount.Should().Be(300);
        var hit = index.Find(shape).Hits.Single();
        hit.DistanceMetres.Should().BeLessThanOrEqualTo(FamilySignalTable.TouchingDistanceMetres,
            "a label drawn on the corner touches the record");

        // The old query ran on the 64-point preview, which skips the corner vertex (chord 147,0 → 149,2).
        var preview = EvidenceJson.Downsample(points);
        preview.Should().HaveCount(EvidenceJson.MaxSamplePoints).And.NotContain((149d, 0d));
        index.Find(preview, closed: false).Hits.Single().DistanceMetres.Should().BeApproximately(Math.Sqrt(2), 1e-9);

        var sample = JsonDocument.Parse(EvidenceJson.GeometrySample(shape).Json!).RootElement;
        sample.GetProperty("points").GetArrayLength().Should().Be(EvidenceJson.MaxSamplePoints);
        sample.GetProperty("closed").GetBoolean().Should().BeFalse();
        sample.GetProperty("fidelity").GetString().Should().Be("exact");
    }

    [Fact]
    public void ArcsAreSplitSoAKerbTextOnTheArcTouchesIt()
    {
        // A 180 degree arc, R = 20 m. The old sample kept only start, bulge midpoint and end: two chords up to
        // 5.86 m from the arc, so a text on the kerb was not even within the 5 m radius.
        const double radius = 20;
        var segments = EvidenceShape.ArcSegments(radius, Math.PI);
        var sagitta = radius * (1 - Math.Cos(Math.PI / segments / 2));
        sagitta.Should().BeLessThanOrEqualTo(EvidenceShape.ChordToleranceMetres);
        var arc = Enumerable.Range(0, segments + 1)
            .Select(s => (X: radius * Math.Cos(Math.PI * s / segments), Y: radius * Math.Sin(Math.PI * s / segments)))
            .ToList();
        // A text exactly on the arc, between two chord ends (never on a vertex).
        var angle = Math.PI * 0.5 / segments + Math.PI / 4;
        var index = new NearbyTextIndex();
        index.Add("שפה", radius * Math.Cos(angle), radius * Math.Sin(angle), "KERB", "host");

        index.Find(EvidenceShape.Open(arc)).Hits.Single().DistanceMetres
            .Should().BeLessThanOrEqualTo(FamilySignalTable.TouchingDistanceMetres);
        var twoChords = new (double X, double Y)[] { (radius, 0), (0, radius), (-radius, 0) };
        index.Find(twoChords, closed: false).Total.Should().Be(0, "the chord is about 5.86 m from the arc at 45 degrees");

        EvidenceShape.ArcSegments(0, Math.PI).Should().Be(1, "a degenerate arc is one chord");
        EvidenceShape.ArcSegments(radius, 0).Should().Be(1);
    }

    [Fact]
    public void AFineShapeInADenseTextAreaStaysWithinTheQueryBudget()
    {
        // 8000 vertices 2.5 cm apart (a finely split 200 m arc) with 800 texts 1 m beside it.
        var path = Enumerable.Range(0, 8000).Select(i => (X: i * 0.025, Y: 0d)).ToList();
        var index = new NearbyTextIndex();
        for (var i = 0; i < 800; i++) index.Add("t" + i, i * 0.25, 1, "H" + i, "host");
        index.Query(EvidenceShape.Open(path)).Status.Should().Be("truncated:800");
        EvidenceShape.Open(Enumerable.Range(0, EvidenceShape.MaxPoints + 1).Select(i => ((double)i, 0d)).ToList())
            .Failure.Should().Be("query-geometry-too-large");
    }

    [Fact]
    public void TheBandedInsideTestAgreesWithTheFullRingScan()
    {
        var triangle = new (double X, double Y)[] { (-40, -30), (130, 10), (20, 140) };
        foreach (var fill in new[] { EvidenceFill.Normal, EvidenceFill.Outer, EvidenceFill.Ignore })
        {
            var shape = EvidenceShape.Rings(new[]
            {
                CollectorFixSquare(0, 0, 100), CollectorFixSquare(20, 20, 60), CollectorFixSquare(40, 40, 20), triangle,
            }, fill);
            var interior = shape.Interior();
            var random = new Random(7);
            long work = 0;
            var mismatches = 0;
            for (var i = 0; i < 5000; i++)
            {
                var x = random.NextDouble() * 200 - 50;
                var y = random.NextDouble() * 200 - 50;
                if (interior.Contains(x, y, ref work) != shape.Contains(x, y)) mismatches++;
            }
            mismatches.Should().Be(0, "fill {0}", fill);
            work.Should().BeLessThan(5000L * shape.PointCount, "each test visits only the segments of its band");
        }
    }

    [Fact]
    public void ADetailedHatchWithManyTextsInItsBoxStaysWithinTheQueryBudget()
    {
        // A 500 m radius ring of 4000 vertices and 2601 texts on a 20 m grid over its bounding box: a full ring
        // scan per text (2601 x 4000 steps) would exceed the per-query budget and fail closed for no reason.
        const int n = 4000;
        var circle = Enumerable.Range(0, n)
            .Select(k => (X: 500 * Math.Cos(2 * Math.PI * k / n), Y: 500 * Math.Sin(2 * Math.PI * k / n)))
            .ToArray();
        var shape = EvidenceShape.Rings(new[] { circle });
        var index = new NearbyTextIndex();
        var texts = new List<(double X, double Y)>();
        for (var gx = -500; gx <= 500; gx += 20)
            for (var gy = -500; gy <= 500; gy += 20)
            {
                index.Add("t", gx, gy, gx + ":" + gy, "host");
                texts.Add((gx, gy));
            }
        ((long)texts.Count * shape.PointCount).Should().BeGreaterThan(NearbyTextIndex.MaxWorkPerQuery);

        var result = index.Find(shape);
        result.Failure.Should().BeNull();
        result.Total.Should().Be(texts.Count(text => shape.Contains(text.X, text.Y) || Near(text)));

        bool Near((double X, double Y) text)
        {
            for (var k = 0; k < n; k++)
                if (NearbyTextIndex.SegmentDistance(text.X, text.Y, circle[k], circle[(k + 1) % n]) <=
                    NearbyTextIndex.DefaultRadiusMetres)
                    return true;
            return false;
        }
    }

    [Fact]
    public void RingsAreStoredWithoutTheirClosingPointAndNeverClaimMoreThanTheirFidelity()
    {
        var closedTwice = new (double X, double Y)[] { (0, 0), (10, 0), (10, 10), (0, 0), (0, 0) };
        var shape = EvidenceShape.Rings(new[] { closedTwice }, EvidenceFill.Normal, "made-up");
        shape.Paths.Single().Should().Equal((0d, 0d), (10d, 0d), (10d, 10d));
        shape.Fidelity.Should().Be(EvidenceShape.Sampled);
        EvidenceShape.Rings(new[] { new (double X, double Y)[] { (0, 0), (5, 5) } }).Closed
            .Should().BeFalse("a ring of two points encloses nothing");
        EvidenceShape.Rings(new[] { new (double X, double Y)[] { (0, 0), (double.NaN, 5), (1, 1) } })
            .Failure.Should().Be("invalid-geometry-sample");
        FluentActions.Invoking(() => EvidenceShape.Rings(new[] { closedTwice }, EvidenceFill.None))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ThePreviewOfAHatchIsItsOutlineRingMarkedClosed()
    {
        var hole = CollectorFixSquare(30, 30, 40);
        var outer = CollectorFixSquare(0, 0, 100);
        var sample = EvidenceJson.GeometrySample(EvidenceShape.Rings(new[] { hole, outer }));
        sample.Status.Should().Be("read");
        var json = JsonDocument.Parse(sample.Json!).RootElement;
        json.GetProperty("closed").GetBoolean().Should().BeTrue();
        json.GetProperty("paths").GetInt32().Should().Be(2);
        json.GetProperty("fidelity").GetString().Should().Be("exact");
        json.GetProperty("points").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble()))
            .Should().Equal(outer.Select(p => (p.X, p.Y)), "the ring enclosing the largest area is the outline");
        EvidenceReader.Texts(new Dictionary<string, string>
        {
            [EvidenceKeys.GeometrySample] = sample.Json!, [EvidenceKeys.GeometrySample + "_status"] = "read",
        }, EvidenceKeys.GeometrySample).Should().BeEmpty("the preview carries no citable text");
    }

    [Fact]
    public void HatchEdgesJoinIntoOneRingOrNotAtAll()
    {
        var bottom = new (double X, double Y)[] { (0, 0), (10, 0) };
        var right = new (double X, double Y)[] { (10, 0), (10, 10) };
        var topReversed = new (double X, double Y)[] { (0, 10), (10, 10) };
        var left = new (double X, double Y)[] { (0, 10), (0, 0) };

        EvidenceShape.JoinRing(new[] { bottom, right, topReversed, left })
            .Should().Equal((0d, 0d), (10d, 0d), (10d, 10d), (0d, 10d));
        // The first edge stored end-first is turned around as well.
        var bottomReversed = new (double X, double Y)[] { (10, 0), (0, 0) };
        EvidenceShape.JoinRing(new[] { bottomReversed, right, topReversed, left })
            .Should().Equal((0d, 0d), (10d, 0d), (10d, 10d), (0d, 10d));
        // A 3 m gap is not a ring anyone can vouch for.
        var shortRight = new (double X, double Y)[] { (10, 3), (10, 10) };
        EvidenceShape.JoinRing(new[] { bottom, shortRight, topReversed, left }).Should().BeNull();
        EvidenceShape.JoinRing(Array.Empty<IReadOnlyList<(double X, double Y)>>()).Should().BeNull();
    }
}
