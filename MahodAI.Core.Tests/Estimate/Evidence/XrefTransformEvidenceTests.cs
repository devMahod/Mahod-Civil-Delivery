using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// ev_xref_transform built from SYNTHETIC matrices. The builder must derive class/scale itself, refuse a chain
/// whose product is not the composed transform, refuse unknown units, and round-trip through the consumer
/// (XrefWidthPolicy): rigid → 1, uniform ×2 → 2, nonuniform → unproven.
/// </summary>
public sealed class XrefTransformEvidenceTests
{
    /// <summary>Row-major: rotation about Z by <paramref name="degrees"/> after axis scales, then translation.</summary>
    private static double[] Matrix(double degrees, double sx, double sy, double sz,
        double tx = 0, double ty = 0, double tz = 0)
    {
        var (c, s) = (Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));
        return new[] { c * sx, -s * sy, 0, tx, s * sx, c * sy, 0, ty, 0, 0, sz, tz, 0, 0, 0, 1 };
    }

    private static double[] Product(double[] a, double[] b)
    {
        var result = new double[16];
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                for (var k = 0; k < 4; k++)
                    result[r * 4 + c] += a[r * 4 + k] * b[k * 4 + c];
        return result;
    }

    private static readonly double[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static EvidenceValue One(double[] matrix, string? source = "Meters", string? host = "Meters") =>
        XrefTransformEvidence.Build(new[] { ("SYNTHETIC-GM", matrix) }, matrix, source, host);

    private static JsonElement Parse(EvidenceValue value)
    {
        value.IsUsable.Should().BeTrue(value.Status);
        return JsonDocument.Parse(value.Json!).RootElement.Clone();
    }

    [Fact]
    public void HostEntityHasTheEmptyHostChainAndNeedsNoUnits()
    {
        var value = XrefTransformEvidence.Build(Array.Empty<(string, double[])>(), Identity, null, null);
        value.Status.Should().Be("read");
        value.Json.Should().Be("{\"space\":\"host\",\"chain\":[]}");

        XrefTransformEvidence.Build(Array.Empty<(string, double[])>(), Matrix(0, 1, 1, 1, 5), "Meters", "Meters")
            .Status.Should().Be("unavailable:chain-composition-mismatch", "an empty chain cannot explain a moved entity");
    }

    [Fact]
    public void RigidRotationAndTranslationRoundTripsThroughTheWidthPolicyAsOne()
    {
        var value = One(Matrix(30, 1, 1, 1, 1000.5, -20, 3));
        var json = Parse(value);
        json.GetProperty("space").GetString().Should().Be("host");
        json.GetProperty("class").GetString().Should().Be("rigid");
        json.GetProperty("mirrored").GetBoolean().Should().BeFalse();
        json.GetProperty("units").GetProperty("source").GetString().Should().Be("Meters");
        json.GetProperty("chain").GetArrayLength().Should().Be(1);
        json.GetProperty("chain")[0].GetProperty("xref").GetString().Should().Be("SYNTHETIC-GM");
        json.GetProperty("chain")[0].GetProperty("matrix").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(Matrix(30, 1, 1, 1, 1000.5, -20, 3), "matrices keep round-trip precision");
        XrefWidthPolicy.UniformScale(value.Json!).Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void UniformScaleTwoRoundTripsAsTwo()
    {
        var value = One(Matrix(30, 2, 2, 2, 1000.5, -20, 3));
        var json = Parse(value);
        json.GetProperty("class").GetString().Should().Be("uniform");
        json.GetProperty("scale").EnumerateArray().Select(v => v.GetDouble())
            .Should().AllSatisfy(v => v.Should().BeApproximately(2, 1e-12));
        XrefWidthPolicy.UniformScale(value.Json!).Should().BeApproximately(2, 1e-12);
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 1, 3)]
    public void NonuniformIsReportedButNeverProvesAWidth(double sx, double sy, double sz)
    {
        var value = One(Matrix(30, sx, sy, sz));
        value.Status.Should().Be("read", "the observation itself is valid evidence");
        Parse(value).GetProperty("class").GetString().Should().Be("nonuniform");
        XrefWidthPolicy.UniformScale(value.Json!).Should().BeNull();
    }

    [Fact]
    public void ShearIsNonuniform()
    {
        var shear = new double[] { 1, 0.5, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        Parse(One(shear)).GetProperty("class").GetString().Should().Be("nonuniform");
        XrefWidthPolicy.UniformScale(One(shear).Json!).Should().BeNull();
    }

    [Fact]
    public void MirrorIsRigidAndFlagged()
    {
        var value = One(Matrix(0, -1, 1, 1, 10, 10));
        var json = Parse(value);
        json.GetProperty("class").GetString().Should().Be("rigid");
        json.GetProperty("mirrored").GetBoolean().Should().BeTrue();
        json.GetProperty("scale").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(1d, 1d, 1d);
        XrefWidthPolicy.UniformScale(value.Json!).Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void NestedChainMustMultiplyToTheComposedTransform()
    {
        var outer = Matrix(30, 1, 1, 1, 1000, 2000);
        var inner = Matrix(-10, 2, 2, 2, 5, -7);
        var links = new[] { ("SYNTHETIC-OUTER", outer), ("SYNTHETIC-INNER", inner) };

        var value = XrefTransformEvidence.Build(links, Product(outer, inner), "Meters", "Meters");
        Parse(value).GetProperty("chain").GetArrayLength().Should().Be(2);
        XrefWidthPolicy.UniformScale(value.Json!).Should().BeApproximately(2, 1e-9);

        XrefTransformEvidence.Build(links, Product(inner, outer), "Meters", "Meters")
            .Status.Should().Be("unavailable:chain-composition-mismatch", "the wrong order is a different transform");
        var moved = Product(outer, inner);
        moved[3] += 0.001;
        XrefTransformEvidence.Build(links, moved, "Meters", "Meters").Should()
            .Be(new EvidenceValue(null, "unavailable:chain-composition-mismatch"));
        XrefTransformEvidence.Build(new[] { ("SYNTHETIC-OUTER", outer) }, Product(outer, inner), "Meters", "Meters")
            .Status.Should().Be("unavailable:chain-composition-mismatch", "a missing link is not guessed");
    }

    [Theory]
    [InlineData(null, "Meters")]
    [InlineData("Meters", null)]
    [InlineData("Undefined", "Meters")]
    [InlineData("Meters", "")]
    [InlineData("Furlongs", "Meters")]
    public void UnknownUnitsMakeTheTransformUnavailable(string? source, string? host)
    {
        var value = One(Matrix(30, 1, 1, 1), source, host);
        value.Status.Should().Be("unavailable:units-unknown");
        value.Json.Should().BeNull();
    }

    [Fact]
    public void DifferentKnownUnitsAreReportedAndTheConsumerStaysUnproven()
    {
        var value = One(Matrix(0, 1, 1, 1), "millimeters", "Meters");
        var units = Parse(value).GetProperty("units");
        units.GetProperty("source").GetString().Should().Be("Millimeters", "names are canonical");
        units.GetProperty("host").GetString().Should().Be("Meters");
        XrefWidthPolicy.UniformScale(value.Json!).Should().BeNull();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(6, "Meters")]
    [InlineData(4, "Millimeters")]
    [InlineData(24, "USSurveyMile")]
    [InlineData(25, null)]
    [InlineData(-1, null)]
    public void UnitNamesFollowTheInsunitsCodes(int code, string? expected) =>
        XrefTransformEvidence.UnitName(code).Should().Be(expected);

    [Fact]
    public void MalformedAndDegenerateMatricesFailClosed()
    {
        One(new double[15]).Status.Should().Be("unavailable:non-finite-transform");
        var nan = Matrix(0, 1, 1, 1);
        nan[5] = double.NaN;
        One(nan).Status.Should().Be("unavailable:non-finite-transform");
        var projective = Matrix(0, 1, 1, 1);
        projective[12] = 0.5;
        One(projective).Status.Should().Be("unavailable:projective-transform");
        One(Matrix(0, 1, 0, 1)).Status.Should().Be("unavailable:degenerate-transform");
        XrefTransformEvidence.Build(new[] { (" ", Identity) }, Identity, "Meters", "Meters")
            .Status.Should().Be("unavailable:malformed-chain");
        XrefTransformEvidence.Build(null, Identity, "Meters", "Meters").Status.Should().Be("unavailable:malformed-transform");
    }

    [Fact]
    public void WrittenEvidenceIsWhatTheDraftWidthPolicyReads()
    {
        var parameters = new Dictionary<string, string>();
        EvidenceJson.Write(parameters, EvidenceKeys.XrefTransform, One(Matrix(30, 2, 2, 2, 7, 8)));
        var record = new NeutralQuantityRecord
        {
            RecordId = "q-SYNTHETIC", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = "1/2", EntityType = "POLYLINE",
                Layer = "SYNTHETIC-GM|ROAD", Xref = "SYNTHETIC-GM",
            },
            Measurement = new QuantityMeasurement
            {
                Kind = "length", Method = "polyline-length+xref-transform", RawValue = 10, Unit = "מטר",
                Parameters = parameters,
            },
            Classification = new QuantityClassification(),
        };
        XrefWidthPolicy.HostWidthScale(record).Should().BeApproximately(2, 1e-12);

        EvidenceJson.Write(parameters, EvidenceKeys.XrefTransform, EvidenceValue.Unavailable("database-mismatch"));
        parameters.Should().NotContainKey(EvidenceKeys.XrefTransform, "an unusable status never keeps a stale value");
        XrefWidthPolicy.HostWidthScale(record).Should().BeNull();
    }
}
