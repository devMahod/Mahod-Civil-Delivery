using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.EngineerDraft;

/// <summary>
/// Decides whether a polyline width stored in a record's own drawing is a width in host space.
/// A host entity needs no transform. An XREF entity needs <c>ev_xref_transform</c> evidence (status
/// <c>read</c>) whose matrix chain is rigid or uniformly scaled, with equal source and host units.
/// Anything else is unproven: the caller must not price a width-dependent quantity from it.
/// </summary>
public static class XrefWidthPolicy
{
    public const string TransformKey = "ev_xref_transform";
    private const double Tolerance = 1e-6;

    /// <summary>The factor from source-local width to host width, or null when it is not proven.</summary>
    public static double? HostWidthScale(NeutralQuantityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var xref = (record.Source.Xref ?? string.Empty).Trim();
        var method = record.Measurement.Method ?? string.Empty;
        var hostEntity = (xref.Length == 0 || xref.Equals("(host)", StringComparison.OrdinalIgnoreCase)) &&
                         !method.Contains("xref-transform", StringComparison.OrdinalIgnoreCase);
        if (hostEntity) return 1.0;
        var parameters = record.Measurement.Parameters;
        if (!parameters.TryGetValue(TransformKey + "_status", out var status) ||
            !string.Equals(status?.Trim(), "read", StringComparison.Ordinal) ||
            !parameters.TryGetValue(TransformKey, out var json) || string.IsNullOrWhiteSpace(json))
            return null;
        // An empty chain proves only a host entity. An XREF record needs the chain of its own insertion:
        // one link per XREF boundary, named as the record's source chain ("A > B"), outermost first.
        if (!ChainMatches(json, xref)) return null;
        return UniformScale(json);
    }

    /// <summary>True when the evidence chain is non-empty and its XREF names equal the record's source chain, in order.</summary>
    public static bool ChainMatches(string json, string recordXrefChain)
    {
        var expected = recordXrefChain.Split(" > ", StringSplitOptions.None)
            .Select(name => Recognition.EvidenceReader.Clean(name)).ToList();
        if (expected.Count == 0 || expected.Any(name => name == null)) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("chain", out var chain) || chain.ValueKind != JsonValueKind.Array)
                return false;
            var names = chain.EnumerateArray()
                .Select(link => link.ValueKind == JsonValueKind.Object && link.TryGetProperty("xref", out var name) &&
                                name.ValueKind == JsonValueKind.String ? Recognition.EvidenceReader.Clean(name.GetString()) : null)
                .ToList();
            return names.Count == expected.Count &&
                   names.Zip(expected).All(pair => pair.First != null && string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Uniform scale of a proven transform, or null (nonuniform, inconsistent, unknown units or malformed).</summary>
    public static double? UniformScale(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Object ||
                !units.TryGetProperty("source", out var sourceUnits) || !units.TryGetProperty("host", out var hostUnits) ||
                sourceUnits.ValueKind != JsonValueKind.String || hostUnits.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(sourceUnits.GetString()) ||
                !string.Equals(sourceUnits.GetString(), hostUnits.GetString(), StringComparison.OrdinalIgnoreCase))
                return null;
            if (!root.TryGetProperty("chain", out var chain) || chain.ValueKind != JsonValueKind.Array) return null;
            var combined = Identity();
            foreach (var link in chain.EnumerateArray())
            {
                if (!link.TryGetProperty("matrix", out var matrix) || matrix.ValueKind != JsonValueKind.Array ||
                    matrix.GetArrayLength() != 16)
                    return null;
                var m = matrix.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToArray();
                if (m.Any(v => !double.IsFinite(v))) return null;
                combined = Multiply(combined, m);
            }
            // Column norms of the linear part: the scale applied to each axis.
            double Norm(int column) => Math.Sqrt(Enumerable.Range(0, 3).Sum(row => combined[row * 4 + column] * combined[row * 4 + column]));
            var (sx, sy, sz) = (Norm(0), Norm(1), Norm(2));
            if (sx <= Tolerance || Math.Abs(sx - sy) > Tolerance * Math.Max(1, sx) || Math.Abs(sx - sz) > Tolerance * Math.Max(1, sx))
                return null;
            // A similarity also keeps right angles: equal column norms with a shear are not a uniform scale.
            double Dot(int a, int b) => Enumerable.Range(0, 3).Sum(row => combined[row * 4 + a] * combined[row * 4 + b]);
            if (Math.Abs(Dot(0, 1)) > Tolerance * sx * sx || Math.Abs(Dot(0, 2)) > Tolerance * sx * sx || Math.Abs(Dot(1, 2)) > Tolerance * sx * sx)
                return null;
            // An affine chain only: no projective terms.
            if (Math.Abs(combined[12]) > Tolerance || Math.Abs(combined[13]) > Tolerance || Math.Abs(combined[14]) > Tolerance ||
                Math.Abs(combined[15] - 1) > Tolerance)
                return null;
            // Declared scale/class, when present, must agree with the matrices.
            if (root.TryGetProperty("scale", out var declared) && declared.ValueKind == JsonValueKind.Array)
            {
                var values = declared.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToArray();
                if (values.Length != 3 || values.Any(v => !double.IsFinite(v) || Math.Abs(Math.Abs(v) - sx) > Tolerance * Math.Max(1, sx)))
                    return null;
            }
            if (root.TryGetProperty("class", out var declaredClass) && declaredClass.ValueKind == JsonValueKind.String &&
                declaredClass.GetString() is { } cls &&
                (!(cls is "rigid" or "uniform") || (cls == "rigid" && Math.Abs(sx - 1) > Tolerance)))
                return null;
            return sx;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                result[row * 4 + column] = Enumerable.Range(0, 4).Sum(k => a[row * 4 + k] * b[k * 4 + column]);
        return result;
    }
}
