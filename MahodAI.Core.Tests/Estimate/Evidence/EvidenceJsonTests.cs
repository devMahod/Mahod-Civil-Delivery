using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>Serialization, status writing and coverage of mahod-evidence/1 values. SYNTHETIC data only.</summary>
public sealed class EvidenceJsonTests
{
    [Fact]
    public void WriteKeepsKeyAndStatusConsistent()
    {
        var parameters = new Dictionary<string, string>();
        EvidenceJson.Write(parameters, EvidenceKeys.Hatch, EvidenceValue.Read("{\"pattern\":\"ANSI31\"}"));
        parameters[EvidenceKeys.Hatch].Should().Be("{\"pattern\":\"ANSI31\"}");
        parameters["ev_hatch_status"].Should().Be("read");
        EvidenceReader.Status(parameters, EvidenceKeys.Hatch).Usable.Should().BeTrue();

        EvidenceJson.Write(parameters, EvidenceKeys.Hatch, EvidenceValue.Absent);
        parameters.Should().NotContainKey(EvidenceKeys.Hatch, "an absent status never keeps a stale value");
        parameters["ev_hatch_status"].Should().Be("absent");

        EvidenceJson.Write(parameters, EvidenceKeys.BlockAttributes, EvidenceValue.Truncated("[{\"tag\":\"A\",\"value\":\"1\"}]", 20));
        EvidenceReader.Status(parameters, EvidenceKeys.BlockAttributes).TruncatedTotal.Should().Be(20);

        EvidenceJson.Write(parameters, EvidenceKeys.NearbyText, EvidenceValue.Read(new string('x', EvidenceJson.MaxValueChars + 1)));
        parameters.Should().NotContainKey(EvidenceKeys.NearbyText);
        parameters["ev_nearby_text_status"].Should().Be("unavailable:value-too-large");

        EvidenceJson.Write(parameters, EvidenceKeys.Closed, EvidenceValue.Read(""));
        parameters["ev_closed_status"].Should().Be("unavailable:internal-no-value");
        EvidenceJson.Write(parameters, EvidenceKeys.Closed, default);
        parameters["ev_closed_status"].Should().Be("unavailable:internal-no-status");
    }

    [Fact]
    public void JsonIsCompactInvariantAndKeepsHebrewReadable()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = EvidenceJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("text", "קו \"ביוב\" <200>");
                EvidenceJson.Number(writer, "d", 1.5);
                EvidenceJson.Number(writer, "r", 1.23456749);
                EvidenceJson.Number(writer, "z", -0.0000001);
                writer.WriteEndObject();
            });
            json.Should().Be("{\"text\":\"קו \\\"ביוב\\\" <200>\",\"d\":1.5,\"r\":1.234567,\"z\":0}");
            EvidenceJson.NumberText(2.5).Should().Be("2.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        FluentActions.Invoking(() => EvidenceJson.Round(double.NaN)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("NullReferenceException", "NullReferenceException")]
    [InlineData("Autodesk.AutoCAD.Runtime.Exception: eInvalidInput", "Autodesk.AutoCAD.Runtime.Exception:-eInvalidInput")]
    [InlineData("  ", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("שגיאה", "unknown")]
    public void StatusReasonsAreSanitized(string? reason, string expected)
    {
        EvidenceJson.Reason(reason).Should().Be(expected);
        EvidenceValue.Unavailable(reason).Status.Should().Be("unavailable:" + expected);
    }

    [Fact]
    public void TextIsCleanedAndCappedWithoutSplittingSurrogates()
    {
        EvidenceJson.Text("  a\tb  ", 80).Should().Be("a b");
        EvidenceJson.Text(new string('x', 79) + "😀", 80).Should().Be(new string('x', 79), "a surrogate pair is never split");
        EvidenceJson.Text("\u0001\u0002", 80).Should().BeNull();
    }

    [Fact]
    public void GeometrySampleIsBoundedAndKeepsBothEnds()
    {
        var points = Enumerable.Range(0, 100).Select(i => ((double)i, (double)-i)).ToList();
        var sample = EvidenceJson.Downsample(points);
        sample.Should().HaveCount(EvidenceJson.MaxSamplePoints);
        sample[0].Should().Be((0d, 0d));
        sample[^1].Should().Be((99d, -99d));
        EvidenceJson.Downsample(points.Take(3).ToList()).Should().Equal(points.Take(3));

        var value = EvidenceJson.GeometrySample(points, closed: false);
        value.Status.Should().Be("read");
        var json = JsonDocument.Parse(value.Json!).RootElement;
        json.GetProperty("space").GetString().Should().Be("host");
        json.GetProperty("units").GetString().Should().Be("m");
        json.GetProperty("closed").GetBoolean().Should().BeFalse();
        json.GetProperty("points").GetArrayLength().Should().Be(EvidenceJson.MaxSamplePoints);
        EvidenceJson.GeometrySample(Array.Empty<(double X, double Y)>(), true).Status.Should().Be("unavailable:no-geometry-sample");
        EvidenceJson.GeometrySample(new[] { (double.PositiveInfinity, 0d) }, true).Status.Should().Be("unavailable:invalid-geometry-sample");
    }

    [Fact]
    public void CoverageCountsStatusesPerKeyAndTruncatedItems()
    {
        var first = new Dictionary<string, string>();
        EvidenceJson.Write(first, EvidenceKeys.Schema, EvidenceValue.Read(EvidenceKeys.SchemaV1));
        EvidenceJson.Write(first, EvidenceKeys.NearbyText, EvidenceValue.Truncated("[{\"text\":\"a\"}]", 9));
        EvidenceJson.Write(first, EvidenceKeys.Hatch, EvidenceValue.Absent);
        EvidenceJson.Write(first, EvidenceKeys.LegendRow, EvidenceValue.Unavailable("legend-detection-not-implemented"));
        var second = new Dictionary<string, string>(first);
        EvidenceJson.Write(second, EvidenceKeys.NearbyText, EvidenceValue.Truncated("[{\"text\":\"b\"}]", 6));
        second["ev_closed"] = "true"; // a value without a status is never trusted

        var coverage = new EvidenceCoverage();
        coverage.Tally(first);
        coverage.Tally(second);
        coverage.Records.Should().Be(2);
        coverage.Statuses[EvidenceKeys.Schema]["read"].Should().Be(2);
        coverage.Statuses[EvidenceKeys.NearbyText]["truncated"].Should().Be(2);
        coverage.TruncatedItems[EvidenceKeys.NearbyText].Should().Be(15);
        coverage.Statuses[EvidenceKeys.Hatch]["absent"].Should().Be(2);
        coverage.Statuses[EvidenceKeys.LegendRow]["unavailable:legend-detection-not-implemented"].Should().Be(2);
        coverage.Statuses[EvidenceKeys.Closed]["missing"].Should().Be(1);
        coverage.Statuses[EvidenceKeys.Closed]["invalid"].Should().Be(1);
        coverage.Statuses.Keys.Should().Equal(EvidenceCoverage.TalliedKeys.OrderBy(key => key, StringComparer.Ordinal));
        coverage.Summary().Should().StartWith("records=2 ");
        JsonSerializer.Serialize(coverage).Should().Contain("\"TruncatedItems\"");
    }

    [Fact]
    public void RecognitionEvidenceNeverEntersTheSavedHintScopeOrTheCadSummary()
    {
        var hash = new string('a', 64);
        NeutralQuantityRecord Record(Action<Dictionary<string, string>> fill)
        {
            var parameters = new Dictionary<string, string> { ["cad_entity_linetype"] = "ByLayer" };
            fill(parameters);
            return new NeutralQuantityRecord
            {
                RecordId = "r1", RunId = "run", ProjectProfileId = "hint-fixture",
                Source = new QuantitySource
                {
                    Drawing = "fixture.dwg", DrawingPath = @"C:\fixture\source.dwg", DrawingHash = hash,
                    Handle = "1", EntityType = "LWPOLYLINE", Layer = "DSFSDF", Xref = "ref-one",
                },
                Measurement = new QuantityMeasurement
                {
                    Kind = "length", Unit = "m", Method = "fixture", RawValue = 12.5, Parameters = parameters,
                },
                Classification = new QuantityClassification { RuleKey = "layer:DSFSDF|length" },
            };
        }
        SemanticHintPolicy.Scope Scope(NeutralQuantityRecord record) => SemanticHintPolicy.Capture(
            "hint-fixture", @"C:\fixture\host.dwg", hash, "layer:DSFSDF|length", new[] { record });

        var saved = Scope(Record(_ => { }));
        Scope(Record(p =>
        {
            EvidenceJson.Write(p, EvidenceKeys.NearbyText, EvidenceValue.Read("[{\"text\":\"a nearby label\"}]"));
            EvidenceJson.Write(p, EvidenceKeys.Schema, EvidenceValue.Read(EvidenceKeys.SchemaV1));
        })).Should().Be(saved, "nearby text depends on unrelated objects and must not invalidate a saved hint");
        Scope(Record(p => EvidenceJson.Write(p, EvidenceKeys.NearbyText, EvidenceValue.Read("[{\"text\":\"another\"}]"))))
            .Should().Be(saved);
        Scope(Record(p => p["cad_entity_linetype"] = "DASHED"))
            .Should().NotBe(saved, "CAD metadata stays part of the source identity");

        var measurement = Record(p => EvidenceJson.Write(p, EvidenceKeys.Closed, EvidenceValue.Read("true"))).Measurement;
        QuantityCadMetadataPolicy.Summarize(new[] { measurement }).Select(field => field.Key)
            .Should().Equal("cad_entity_linetype");
    }
}
