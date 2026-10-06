using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>
/// Builds <c>ev_xref_transform</c> from the per-XREF matrices of an insertion chain. Each link is the
/// row-major source→parent matrix of one XREF boundary (ordinary INSERTs since the previous boundary are
/// already multiplied in), outermost first. The builder does not trust its caller: the product of the links
/// must reproduce the composed source→host matrix, the class is derived from that matrix with the existing
/// <see cref="XrefQuantityPolicy.ValidateSimilarity"/> rule, and unknown units make the whole value unavailable.
/// A host entity has an empty chain: <c>{"space":"host","chain":[]}</c>.
/// </summary>
public static class XrefTransformEvidence
{
    public const string HostJson = "{\"space\":\"host\",\"chain\":[]}";
    public const string Rigid = "rigid";
    public const string Uniform = "uniform";
    public const string Nonuniform = "nonuniform";

    /// <summary>|scale − 1| within this is rigid. Tighter than <c>XrefWidthPolicy</c>'s 1e-6 check, so rigid always passes it.</summary>
    public const double RigidTolerance = 1e-9;

    /// <summary>Relative element tolerance for product(links) == composed.</summary>
    public const double CompositionTolerance = 1e-9;

    public const int MaxChainLinks = 32;

    // AutoCAD UnitsValue numeric ABI 0..24 (names as UnitsValue.ToString() prints them). 0 is Undefined.
    private static readonly string[] UnitNames =
    {
        "Undefined", "Inches", "Feet", "Miles", "Millimeters", "Centimeters", "Meters", "Kilometers",
        "MicroInches", "Mils", "Yards", "Angstroms", "Nanometers", "Microns", "Decimeters", "Dekameters",
        "Hectometers", "Gigameters", "Astronomical", "LightYears", "Parsecs", "USSurveyFeet", "USSurveyInch",
        "USSurveyYard", "USSurveyMile",
    };

    /// <summary>The unit name of a supported INSUNITS code (1..24), or null for Undefined/unknown.</summary>
    public static string? UnitName(int? insunitsCode) =>
        insunitsCode is >= 1 and < 25 ? UnitNames[insunitsCode.Value] : null;

    /// <summary>The canonical spelling of a supported unit name, or null (Undefined, empty or unknown).</summary>
    public static string? CanonicalUnit(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        for (var code = 1; code < UnitNames.Length; code++)
            if (string.Equals(UnitNames[code], trimmed, StringComparison.OrdinalIgnoreCase)) return UnitNames[code];
        return null;
    }

    public static EvidenceValue Build(
        IReadOnlyList<(string Xref, double[] RowMajor16)>? links,
        double[]? composedRowMajor16,
        string? sourceUnits,
        string? hostUnits)
    {
        if (links == null || composedRowMajor16 == null) return EvidenceValue.Unavailable("malformed-transform");
        if (!IsMatrix(composedRowMajor16)) return EvidenceValue.Unavailable("non-finite-transform");
        if (links.Count > MaxChainLinks) return EvidenceValue.Unavailable("chain-too-deep");
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Xref)) return EvidenceValue.Unavailable("malformed-chain");
            if (!IsMatrix(link.RowMajor16)) return EvidenceValue.Unavailable("non-finite-transform");
            if (!IsAffine(link.RowMajor16)) return EvidenceValue.Unavailable("projective-transform");
        }
        if (!IsAffine(composedRowMajor16)) return EvidenceValue.Unavailable("projective-transform");

        if (links.Count == 0)
            return Close(Identity(), composedRowMajor16)
                ? EvidenceValue.Read(HostJson)
                : EvidenceValue.Unavailable("chain-composition-mismatch");

        var product = Identity();
        foreach (var link in links) product = Multiply(product, link.RowMajor16);
        if (!Close(product, composedRowMajor16)) return EvidenceValue.Unavailable("chain-composition-mismatch");

        var source = CanonicalUnit(sourceUnits);
        var host = CanonicalUnit(hostUnits);
        if (source == null || host == null) return EvidenceValue.Unavailable("units-unknown");

        var columns = new[] { Column(composedRowMajor16, 0), Column(composedRowMajor16, 1), Column(composedRowMajor16, 2) };
        var check = XrefQuantityPolicy.ValidateSimilarity(columns[0], columns[1], columns[2]);
        string cls;
        if (check.IsSafe) cls = Math.Abs(check.LengthScale - 1) <= RigidTolerance ? Rigid : Uniform;
        else if (check.Failure is "non-uniform-scale" or "sheared-transform") cls = Nonuniform;
        else return EvidenceValue.Unavailable(check.Failure ?? "invalid-transform");

        var scale = columns.Select(Norm).ToArray();
        var mirrored = Determinant(composedRowMajor16) < 0;
        return EvidenceValue.Read(EvidenceJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("space", "host");
            writer.WriteStartArray("chain");
            foreach (var link in links)
            {
                writer.WriteStartObject();
                writer.WriteString("xref", EvidenceReader.Clean(link.Xref) ?? "?");
                writer.WriteStartArray("matrix");
                foreach (var value in link.RowMajor16) EvidenceJson.Exact(writer, value);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("class", cls);
            writer.WriteStartArray("scale");
            foreach (var value in scale) EvidenceJson.Exact(writer, value);
            writer.WriteEndArray();
            writer.WriteBoolean("mirrored", mirrored);
            writer.WriteStartObject("units");
            writer.WriteString("source", source);
            writer.WriteString("host", host);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));
    }

    private static bool IsMatrix(double[]? values) =>
        values is { Length: 16 } && values.All(value => double.IsFinite(value));

    private static bool IsAffine(double[] m) =>
        Math.Abs(m[12]) <= CompositionTolerance && Math.Abs(m[13]) <= CompositionTolerance &&
        Math.Abs(m[14]) <= CompositionTolerance && Math.Abs(m[15] - 1) <= CompositionTolerance;

    private static bool Close(double[] expected, double[] actual)
    {
        for (var i = 0; i < 16; i++)
        {
            var scale = Math.Max(1, Math.Max(Math.Abs(expected[i]), Math.Abs(actual[i])));
            if (!(Math.Abs(expected[i] - actual[i]) <= CompositionTolerance * scale)) return false;
        }
        return true;
    }

    private static double[] Column(double[] m, int column) => new[] { m[column], m[4 + column], m[8 + column] };

    private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    private static double Determinant(double[] m) =>
        m[0] * (m[5] * m[10] - m[6] * m[9]) -
        m[1] * (m[4] * m[10] - m[6] * m[8]) +
        m[2] * (m[4] * m[9] - m[5] * m[8]);

    internal static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    /// <summary>Row-major 4×4 product a·b (the same convention as <c>XrefWidthPolicy</c>).</summary>
    internal static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
            {
                var sum = 0d;
                for (var k = 0; k < 4; k++) sum += a[row * 4 + k] * b[k * 4 + column];
                result[row * 4 + column] = sum;
            }
        return result;
    }
}
