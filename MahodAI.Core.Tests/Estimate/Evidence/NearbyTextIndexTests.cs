using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>SYNTHETIC texts in host metres. Nearby text is a bounded, deterministic candidate list.</summary>
public sealed class NearbyTextIndexTests
{
    private static readonly (double X, double Y)[] Origin = { (0, 0) };

    [Fact]
    public void PointQueryKeepsTextsWithinTheRadiusOrderedByDistanceThenSourceThenHandle()
    {
        var index = new NearbyTextIndex();
        index.Add("far", 6, 0, "F", "host").Should().BeTrue();
        index.Add("three-b", 0, 3, "B", "host");
        index.Add("three-a", -3, 0, "A", "host");
        index.Add("three-xref", 3, 0, "0", "SYNTHETIC-GM");
        index.Add("one", 1, 0, "Z", "host");

        var result = index.Find(Origin, closed: false);
        result.Failure.Should().BeNull();
        result.Total.Should().Be(4, "6 m is outside the 5 m radius");
        // Equal distances: ordinal source ("SYNTHETIC-GM" < "host"), then handle.
        result.Hits.Select(hit => hit.Text).Should().Equal("one", "three-xref", "three-a", "three-b");
        result.Hits[0].DistanceMetres.Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void DistancesEqualToTheMillimetreTieOnSourceAndHandle()
    {
        var index = new NearbyTextIndex();
        index.Add("second", 2.0004, 0, "2", "host");
        index.Add("first", 2.0001, 0, "1", "host");
        index.Add("closer", 1.9990, 0, "9", "host");
        index.Find(Origin, false).Hits.Select(hit => hit.Text).Should().Equal("closer", "first", "second");
    }

    [Fact]
    public void TextInsideAClosedShapeIsAtDistanceZeroEvenFarFromItsBoundary()
    {
        var square = new (double X, double Y)[] { (0, 0), (100, 0), (100, 100), (0, 100) };
        var index = new NearbyTextIndex();
        index.Add("שטח ירוק", 50, 50, "IN", "host");
        index.Add("edge", 103, 50, "OUT", "host");

        var closed = index.Find(square, closed: true);
        closed.Hits.Select(hit => (hit.Text, hit.DistanceMetres)).Should().Equal(("שטח ירוק", 0d), ("edge", 3d));

        var open = index.Find(square, closed: false);
        open.Hits.Select(hit => hit.Text).Should().Equal(new[] { "edge" }, "an open polyline has no interior");
    }

    [Fact]
    public void LongAndDiagonalSegmentsFindTextsAlongTheirWholeLength()
    {
        var index = new NearbyTextIndex();
        index.Add("mid", 500, 4, "M", "host");
        index.Add("beyond", 700, 5.01, "B", "host");
        index.Add("diag", 353.5, 360.5, "D", "host"); // 4.95 m off the 45° line, ~505 m along it
        index.Find(new (double X, double Y)[] { (0, 0), (1000, 0) }, false).Hits.Select(hit => hit.Text)
            .Should().Equal("mid");
        var diagonal = index.Find(new (double X, double Y)[] { (0, 0), (1000, 1000) }, false);
        diagonal.Hits.Select(hit => hit.Text).Should().Equal("diag");
        diagonal.Hits[0].DistanceMetres.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void MoreThanFiveHitsAreTruncatedWithTheirTotal()
    {
        var index = new NearbyTextIndex();
        for (var i = 0; i < 8; i++) index.Add("t" + i, i * 0.5, 0, "H" + i, "host");

        var value = index.Query(Origin, false);
        value.Status.Should().Be("truncated:8");
        var parameters = new Dictionary<string, string>();
        EvidenceJson.Write(parameters, EvidenceKeys.NearbyText, value);
        var status = EvidenceReader.Status(parameters, EvidenceKeys.NearbyText);
        status.State.Should().Be(EvidenceState.Truncated);
        status.TruncatedTotal.Should().Be(8);
        EvidenceReader.TryObservation(parameters, EvidenceKeys.NearbyText, out var observation).Should().BeTrue();
        observation.GetArrayLength().Should().Be(NearbyTextIndex.MaxHits);
        observation[0].GetProperty("text").GetString().Should().Be("t0");
        observation[0].GetProperty("distance_m").GetDouble().Should().Be(0);
        observation[0].GetProperty("source").GetString().Should().Be("host");
        observation[0].GetProperty("kind").GetString().Should().Be("text");
    }

    [Fact]
    public void NoHitIsAbsentAndBadGeometryIsUnavailable()
    {
        var index = new NearbyTextIndex();
        index.Add("far away", 1000, 1000, "1", "host");
        index.Query(Origin, false).Should().Be(new EvidenceValue(null, "absent"));
        index.Query(Array.Empty<(double X, double Y)>(), false).Status.Should().Be("unavailable:no-geometry-sample");
        index.Query(null, false).Status.Should().Be("unavailable:no-geometry-sample");
        index.Query(new (double X, double Y)[] { (double.NaN, 0) }, false).Status.Should().Be("unavailable:invalid-geometry-sample");
    }

    [Fact]
    public void ExceedingTheCapTruncatesTheIndexAndNoQueryClaimsAbsence()
    {
        var index = new NearbyTextIndex(maxTexts: 2);
        index.Add("a", 0, 0, "1", "host").Should().BeTrue();
        index.Add("b", 1000, 0, "2", "host").Should().BeTrue();
        index.Add("c", 2000, 0, "3", "host").Should().BeFalse();
        index.IsTruncated.Should().BeTrue();
        index.DroppedByCap.Should().Be(1);
        index.Query(new (double X, double Y)[] { (5000, 5000) }, false).Status
            .Should().Be("unavailable:text-index-truncated", "a partial index cannot prove that no text is near");
        index.Query(Origin, false).Status.Should().Be("unavailable:text-index-truncated");
    }

    [Theory]
    [InlineData("MAHOD_LABELS")]
    [InlineData("mhd-estimate")]
    [InlineData("SYNTHETIC-GM|MCD-TEXT")]
    [InlineData("MCDV-VIEW")]
    // Written by our own section-view tool (CreateSectionViewsTool): hyphenated, not MAHOD_.
    [InlineData("MAHOD-SV-TEXT")]
    [InlineData("SYNTHETIC-GM|MAHOD-SV-CORR")]
    [InlineData("mahod-sv")]
    public void OurOwnAnnotationLayersAreNeverIndexed(string layer)
    {
        var index = new NearbyTextIndex();
        index.Add("our label", 0, 0, "1", "host", layer).Should().BeFalse();
        index.ExcludedOwnLayers.Should().Be(1);
        index.Count.Should().Be(0);
        index.Add("C-ROAD label", 0, 0, "2", "host", "C-ROAD-LABEL").Should().BeTrue("Civil label layers are drawing text");
        NearbyTextIndex.IsOwnAnnotationLayer("MAHODROAD").Should().BeFalse("only the MAHOD_ / MAHOD- prefixes are ours");
    }

    [Fact]
    public void TextOnLayerZeroInsideOurOwnInsertIsNeverIndexed()
    {
        // 'STA 12145' drawn on layer 0 inside a MahodSV_* block inserted on MAHOD-SV: ours, whatever its own layer.
        var index = new NearbyTextIndex();
        index.Add("STA 12145", 0, 0, "1F/2A", "host", "0", insideOwnInsert: true).Should().BeFalse();
        index.Add("2.0%", 1, 0, "1F/2B", "host", "0", "attribute", insideOwnInsert: true).Should().BeFalse();
        index.ExcludedOwnLayers.Should().Be(2);
        index.Add("אבן שפה", 2, 0, "3C/4D", "host", "0").Should().BeTrue("an ordinary block's text stays drawing text");
        index.Find(Origin, false).Hits.Select(hit => hit.Text).Should().Equal("אבן שפה");
    }

    [Fact]
    public void TheRecordsOwnAttributesAreNotItsNeighbours()
    {
        var index = new NearbyTextIndex();
        index.Add("own tag", 0, 0, "1A/2B", "host", kind: "attribute");
        index.Add("other", 1, 0, "1AB/3", "host");
        index.Find(Origin, false, ownHandlePath: "1A").Hits.Select(hit => hit.Text).Should().Equal("other");
        index.Find(Origin, false).Hits.Select(hit => hit.Text).Should().Equal("own tag", "other");
    }

    [Fact]
    public void TextIsCleanedCappedAndStoredAsReadableHebrew()
    {
        var index = new NearbyTextIndex();
        index.Add("  אבן‏ שפה\n\"ראשית\"  ", 0, 0, "1", "host").Should().BeTrue();
        index.Add(new string('א', 120), 1, 0, "2", "host");
        index.Add("   ", 2, 0, "3", "host").Should().BeFalse();
        index.Add("no handle", 2, 0, " ", "host").Should().BeFalse();
        index.RejectedEmptyOrInvalid.Should().Be(2);

        var value = index.Query(Origin, false);
        value.Json.Should().Contain("אבן שפה \\\"ראשית\\\"", "Hebrew stays readable and quotes stay escaped");
        var hits = JsonDocument.Parse(value.Json!).RootElement;
        hits[1].GetProperty("text").GetString().Should().HaveLength(NearbyTextIndex.MaxTextChars);
    }

    [Fact]
    public void InsertionOrderDoesNotChangeTheEvidence()
    {
        var texts = new[] { ("a", 1.0, 1.0, "1"), ("b", -1.0, 1.0, "2"), ("c", 1.0, -1.0, "3"), ("d", 0.5, 0.5, "4") };
        string Json(IEnumerable<(string Text, double X, double Y, string Handle)> order)
        {
            var index = new NearbyTextIndex();
            foreach (var (text, x, y, handle) in order) index.Add(text, x, y, handle, "host");
            return index.Query(new (double X, double Y)[] { (-2, 0), (2, 0) }, false).Json!;
        }
        Json(texts).Should().Be(Json(Enumerable.Reverse(texts)));
    }

    [Fact]
    public void GeometryBudgetsFailClosedInsteadOfWalkingSparsely()
    {
        var index = new NearbyTextIndex();
        index.Add("x", 0, 0, "1", "host");
        index.Query(new (double X, double Y)[] { (0, 0), (1e9, 0) }, false).Status
            .Should().Be("unavailable:geometry-sample-too-large");
    }

    [Fact]
    public void AnUnreadTextAtAnUnknownPositionMeansNoQueryCanClaimAbsenceOrCompleteness()
    {
        var index = new NearbyTextIndex();
        index.Add("DRAIN", 1, 0, "1", "host");
        index.ReportUnread(null, null);
        index.UnreadUnlocated.Should().Be(1);
        index.Count.Should().Be(1);
        index.Query(new (double X, double Y)[] { (5000, 5000) }, false).Status
            .Should().Be("unavailable:text-capture-incomplete", "a text that could not be read is not proof that no text is there");
        index.Query(Origin, false).Status.Should().Be("unavailable:text-capture-incomplete");
    }

    [Fact]
    public void AnUnreadTextAtAKnownPositionMarksOnlyTheQueriesThatReachIt()
    {
        var index = new NearbyTextIndex();
        index.ReportUnread(2, 0, "U1");
        index.UnreadLocated.Should().Be(1);
        index.Count.Should().Be(0, "an unread text is not a readable text");
        index.Query(Origin, false).Status.Should().Be("unavailable:text-capture-incomplete");
        index.Query(new (double X, double Y)[] { (100, 0) }, false).Status
            .Should().Be("absent", "the unread text is 98 m away: this query's absence is still proven");
    }

    [Fact]
    public void ReadableHitsNextToAnUnreadTextAreShownAsIncomplete()
    {
        var index = new NearbyTextIndex();
        index.Add("SEWER", 1, 0, "1", "host");
        index.ReportUnread(2, 0, "U1");
        var result = index.Find(Origin, false);
        result.Failure.Should().BeNull();
        result.Hits.Select(hit => hit.Text).Should().Equal("SEWER");
        result.Total.Should().Be(2);
        var status = EvidenceReader.Status(new Dictionary<string, string>
        {
            ["ev_nearby_text_status"] = index.Query(Origin, false).Status,
            ["ev_nearby_text"] = index.Query(Origin, false).Json ?? string.Empty,
        }, "ev_nearby_text");
        status.State.Should().Be(EvidenceState.Truncated, "more text is there than was read");
    }

    [Fact]
    public void ARecordsOwnUnreadAttributeDoesNotMarkItsOwnQuery()
    {
        var index = new NearbyTextIndex();
        index.ReportUnread(0.5, 0, "R1/A9");
        index.Query(Origin, false, ownHandlePath: "R1").Status.Should().Be("absent");
        index.Query(Origin, false, ownHandlePath: "R2").Status.Should().Be("unavailable:text-capture-incomplete");
    }

    [Fact]
    public void UnreadTextsNeverCountTowardsTheReadableCap()
    {
        var index = new NearbyTextIndex(maxTexts: 1);
        index.ReportUnread(500, 500);
        index.Add("a", 0, 0, "1", "host").Should().BeTrue();
        index.IsTruncated.Should().BeFalse();
    }
}
