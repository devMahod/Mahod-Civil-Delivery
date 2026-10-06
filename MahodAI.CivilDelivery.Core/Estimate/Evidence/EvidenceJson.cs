using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>
/// One mahod-evidence/1 field: its compact value (present only for <c>read</c> or <c>truncated:&lt;n&gt;</c>)
/// and its contract status. <see cref="Json"/> is the exact string stored in
/// <see cref="QuantityMeasurement.Parameters"/>; a few scalar keys (schema, colour, closed) are plain text.
/// </summary>
public readonly record struct EvidenceValue(string? Json, string Status)
{
    public const string ReadStatus = "read";
    public const string AbsentStatus = "absent";
    public const string UnavailablePrefix = "unavailable:";
    public const string TruncatedPrefix = "truncated:";

    public static EvidenceValue Read(string json) => new(json, ReadStatus);

    /// <summary>A capped but usable value: <paramref name="total"/> is the number observed before the cap.</summary>
    public static EvidenceValue Truncated(string json, int total) =>
        total > 0 ? new(json, TruncatedPrefix + total.ToString(CultureInfo.InvariantCulture)) : Read(json);

    public static EvidenceValue Absent => new(null, AbsentStatus);

    public static EvidenceValue Unavailable(string? reason) => new(null, UnavailablePrefix + EvidenceJson.Reason(reason));

    public bool IsUsable => !string.IsNullOrWhiteSpace(Json) && Status is not null &&
        (Status == ReadStatus || Status.StartsWith(TruncatedPrefix, StringComparison.Ordinal));
}

/// <summary>
/// Deterministic serialization for evidence values: invariant culture, compact JSON, Hebrew kept readable
/// (UnsafeRelaxedJsonEscaping still escapes quotes, backslashes and control characters), measured-looking
/// numbers rounded to <see cref="NumberDecimals"/> decimals and every value size-capped. Evidence text is
/// untrusted data: it is cleaned and capped here and never interpreted.
/// </summary>
public static class EvidenceJson
{
    public const int NumberDecimals = 6;
    public const int MaxValueChars = EvidenceReader.MaxValueChars;
    public const int MaxReasonChars = 80;
    public const int MaxSamplePoints = 64;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>Writes one compact JSON document with the shared evidence options.</summary>
    public static string Build(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Fixed rounding for observed quantities (distances, coordinates, pattern scales). Negative zero becomes zero.</summary>
    public static double Round(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Evidence numbers must be finite.");
        var rounded = Math.Round(value, NumberDecimals, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0d : rounded;
    }

    public static void Number(Utf8JsonWriter writer, double value) => writer.WriteNumberValue(Round(value));

    public static void Number(Utf8JsonWriter writer, string name, double value) => writer.WriteNumber(name, Round(value));

    /// <summary>
    /// Round-trip precision for values that are re-checked mathematically (transform matrices, derived scales):
    /// rounding them would break the consumer's own similarity and composition checks.
    /// </summary>
    public static void Exact(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Evidence numbers must be finite.");
        writer.WriteNumberValue(value == 0 ? 0d : value);
    }

    /// <summary>Invariant text for a rounded number, used where the contract stores a number inside a string.</summary>
    public static string NumberText(double value) => Round(value).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Cleaned (<see cref="EvidenceReader.Clean"/>) and capped text, or null when nothing meaningful is left.</summary>
    public static string? Text(string? value, int maxChars)
    {
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        var clean = EvidenceReader.Clean(value);
        if (clean == null || clean.Length <= maxChars) return clean;
        var length = maxChars;
        // Never split a surrogate pair (Hebrew is BMP, but block names may carry symbols).
        if (char.IsHighSurrogate(clean[length - 1])) length--;
        return length == 0 ? null : clean[..length];
    }

    /// <summary>A status reason limited to <c>[A-Za-z0-9._:-]</c>, at most <see cref="MaxReasonChars"/> characters.</summary>
    public static string Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "unknown";
        var builder = new StringBuilder(Math.Min(reason.Length, MaxReasonChars));
        var dash = false;
        foreach (var c in reason.Trim())
        {
            if (builder.Length >= MaxReasonChars) break;
            var allowed = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or ':' or '-';
            if (!allowed)
            {
                if (!dash && builder.Length > 0) builder.Append('-');
                dash = true;
                continue;
            }
            builder.Append(c);
            dash = c == '-';
        }
        var text = builder.ToString().Trim('-');
        return text.Length == 0 ? "unknown" : text;
    }

    /// <summary>
    /// Writes <paramref name="key"/> and <c>&lt;key&gt;_status</c> consistently: the value is stored only when
    /// usable, an unusable status removes a stale value, and an oversized value becomes
    /// <c>unavailable:value-too-large</c> rather than a silently clipped JSON document.
    /// </summary>
    public static void Write(IDictionary<string, string> parameters, string key, EvidenceValue value)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An evidence key is required.", nameof(key));
        var status = value.Status;
        var json = value.Json;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = EvidenceValue.UnavailablePrefix + "internal-no-status";
            json = null;
        }
        var usable = status == EvidenceValue.ReadStatus || status.StartsWith(EvidenceValue.TruncatedPrefix, StringComparison.Ordinal);
        if (usable && string.IsNullOrWhiteSpace(json))
        {
            status = EvidenceValue.UnavailablePrefix + "internal-no-value";
            usable = false;
        }
        else if (usable && json!.Length > MaxValueChars)
        {
            status = EvidenceValue.UnavailablePrefix + "value-too-large";
            usable = false;
        }
        if (usable) parameters[key] = json!;
        else parameters.Remove(key);
        parameters[key + EvidenceKeys.StatusSuffix] = status;
    }

    /// <summary>
    /// At most <paramref name="max"/> points, evenly spaced by index, always keeping the first and the last.
    /// Deterministic; the input order is preserved.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Downsample(IReadOnlyList<(double X, double Y)> points, int max = MaxSamplePoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (max < 2) throw new ArgumentOutOfRangeException(nameof(max));
        if (points.Count <= max) return points.ToArray();
        var result = new (double X, double Y)[max];
        var last = (long)points.Count - 1;
        for (var i = 0; i < max; i++)
            result[i] = points[(int)(i * last / (max - 1))];
        return result;
    }

    /// <summary>
    /// <c>ev_geometry_sample</c>: host-space points in metres for a group preview (vision) only. It is never a
    /// measurement: curves are sampled, arcs become chords and at most <see cref="MaxSamplePoints"/> points are kept.
    /// </summary>
    public static EvidenceValue GeometrySample(IReadOnlyList<(double X, double Y)>? points, bool closed)
    {
        if (points == null || points.Count == 0) return EvidenceValue.Unavailable("no-geometry-sample");
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return EvidenceValue.Unavailable("invalid-geometry-sample");
        var sample = Downsample(points);
        return EvidenceValue.Read(Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("space", "host");
            writer.WriteString("units", "m");
            writer.WriteBoolean("closed", closed);
            writer.WriteStartArray("points");
            foreach (var point in sample)
            {
                writer.WriteStartArray();
                Number(writer, point.X);
                Number(writer, point.Y);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
    }

    /// <summary>
    /// <c>ev_geometry_sample</c> of a record's host shape: the preview draws one path, at most
    /// <see cref="MaxSamplePoints"/> points. For a closed shape that is the ring enclosing the largest area (the
    /// outline; <c>"paths"</c> says how many rings, holes included, the shape has) and <c>"closed":true</c> tells the
    /// renderer to join the last point back to the first. <c>"fidelity"</c> is the shape's own
    /// (<c>exact</c> or <c>sampled</c>). A stand-in (an extents box, an unread hatch loop, a region) is never
    /// drawn: its failure becomes the status. A preview, never a measurement.
    /// </summary>
    public static EvidenceValue GeometrySample(EvidenceShape? shape)
    {
        if (shape == null) return EvidenceValue.Unavailable("no-geometry-sample");
        if (shape.Failure != null) return EvidenceValue.Unavailable(shape.Failure);
        if (shape.Paths.Count == 0 || shape.Paths.All(candidate => candidate.Count == 0))
            return EvidenceValue.Unavailable("no-geometry-sample");
        var outline = shape.Closed ? LargestRing(shape.Paths) : shape.Paths[0];
        var sample = Downsample(outline);
        return EvidenceValue.Read(Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("space", "host");
            writer.WriteString("units", "m");
            writer.WriteBoolean("closed", shape.Closed);
            writer.WriteString("fidelity", shape.Fidelity);
            writer.WriteNumber("paths", shape.Paths.Count);
            writer.WriteStartArray("points");
            foreach (var point in sample)
            {
                writer.WriteStartArray();
                Number(writer, point.X);
                Number(writer, point.Y);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
    }

    private static IReadOnlyList<(double X, double Y)> LargestRing(IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings)
    {
        IReadOnlyList<(double X, double Y)> largest = rings[0];
        var largestArea = -1d;
        foreach (var ring in rings)
        {
            if (ring.Count == 0) continue;
            // Shoelace in local coordinates: survey-grid magnitudes would cancel otherwise.
            var (ox, oy) = ring[0];
            var twice = 0d;
            for (var i = 0; i < ring.Count; i++)
            {
                var (ax, ay) = ring[i];
                var (bx, by) = ring[(i + 1) % ring.Count];
                twice += (ax - ox) * (by - oy) - (bx - ox) * (ay - oy);
            }
            var area = Math.Abs(twice);
            if (area > largestArea)
            {
                largest = ring;
                largestArea = area;
            }
        }
        return largest;
    }
}

/// <summary>Text-index statistics published with the coverage artifact.</summary>
public sealed record TextIndexCoverage(
    double RadiusMetres, int MaxTexts, int Indexed, int ExcludedOwnLayers, int RejectedEmptyOrInvalid,
    int DroppedByCap, bool Truncated, int CaptureFailures, int SkippedUnsupportedUnits,
    int UnreadLocated = 0, int UnreadUnlocated = 0);

/// <summary>
/// Bounded per-scan summary of what evidence was actually read: per key, the number of records by status
/// (<c>read</c>, <c>absent</c>, <c>truncated</c>, <c>unavailable:&lt;reason&gt;</c>, <c>missing</c>, <c>invalid</c>)
/// and the total number of items observed behind truncated values. One small artifact per scan replaces any
/// per-record finding.
/// </summary>
public sealed class EvidenceCoverage
{
    public const int MaxLabelsPerKey = 64;
    public const string OtherLabel = "other";

    public string Schema { get; init; } = EvidenceKeys.SchemaV1;
    public string Purpose { get; init; } =
        "Recognition evidence coverage. Evidence is observation only: it never measures, maps, prices or approves.";
    public int Records { get; private set; }
    public SortedDictionary<string, SortedDictionary<string, int>> Statuses { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> TruncatedItems { get; } = new(StringComparer.Ordinal);
    public TextIndexCoverage? TextIndex { get; set; }
    public int GeometrySamplesEmitted { get; set; }
    public int GeometrySampleBudgetPerGroup { get; set; }
    public List<string> Notes { get; } = new();

    public static IReadOnlyList<string> TalliedKeys { get; } =
        new[] { EvidenceKeys.Schema }.Concat(EvidenceKeys.All).ToArray();

    public void Tally(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Records++;
        foreach (var key in TalliedKeys)
        {
            var status = EvidenceReader.Status(parameters, key);
            var label = status.State switch
            {
                EvidenceState.Read => "read",
                EvidenceState.Absent => "absent",
                EvidenceState.Truncated => "truncated",
                EvidenceState.Unavailable => "unavailable:" + EvidenceJson.Reason(status.Reason),
                EvidenceState.Missing => "missing",
                _ => "invalid",
            };
            if (status.State == EvidenceState.Truncated && status.TruncatedTotal is { } total)
                TruncatedItems[key] = TruncatedItems.GetValueOrDefault(key) + total;
            if (!Statuses.TryGetValue(key, out var counts))
                Statuses[key] = counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            if (!counts.ContainsKey(label) && counts.Count >= MaxLabelsPerKey) label = OtherLabel;
            counts[label] = counts.GetValueOrDefault(label) + 1;
        }
    }

    /// <summary>One log line: read counts per key, in key order.</summary>
    public string Summary() =>
        $"records={Records.ToString(CultureInfo.InvariantCulture)} " + string.Join(" ", Statuses.Select(pair =>
            pair.Key + "=" + pair.Value.GetValueOrDefault("read").ToString(CultureInfo.InvariantCulture) + "r/" +
            pair.Value.GetValueOrDefault("truncated").ToString(CultureInfo.InvariantCulture) + "t")) +
        (TextIndex == null ? string.Empty
            : $" texts={TextIndex.Indexed.ToString(CultureInfo.InvariantCulture)} text_index_truncated={(TextIndex.Truncated ? "yes" : "no")}");
}
