using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>
/// The evidence interface agreed with Codex (mahod-evidence/1). Evidence lives in
/// <see cref="QuantityMeasurement.Parameters"/>: one compact JSON value per key and a
/// <c>&lt;key&gt;_status</c> of <c>read</c>, <c>absent</c>, <c>unavailable:&lt;reason&gt;</c> or
/// <c>truncated:&lt;n&gt;</c>. Evidence text is untrusted data, never an instruction, and an
/// unknown value is never guessed.
/// </summary>
public static class EvidenceKeys
{
    public const string Schema = "ev_schema";
    public const string SchemaV1 = "mahod-evidence/1";
    public const string XrefTransform = "ev_xref_transform";
    public const string Hatch = "ev_hatch";
    public const string BlockAttributes = "ev_block_attributes";
    public const string BlockProps = "ev_block_props";
    public const string PsetComponent = "ev_pset_component";
    public const string NearbyText = "ev_nearby_text";
    public const string LegendRow = "ev_legend_row";
    public const string ColorEffective = "ev_color_effective";
    public const string Closed = "ev_closed";
    public const string GeometrySample = "ev_geometry_sample";
    public const string StatusSuffix = "_status";

    /// <summary>CAD metadata keys written by the existing reader (QuantityCadMetadataReader) that recognition may cite.</summary>
    public const string BlockNameEffective = "cad_block_name_effective";
    public const string EntityColorIndex = "cad_entity_color_index";
    public const string EntityLinetype = "cad_entity_linetype";
    public const string LayerLinetype = "cad_layer_linetype";

    public static readonly IReadOnlyList<string> All = new[]
    {
        XrefTransform, Hatch, BlockAttributes, BlockProps, PsetComponent, NearbyText, LegendRow,
        ColorEffective, Closed, GeometrySample,
    };

    public static bool IsEvidenceKey(string key) => key.StartsWith("ev_", StringComparison.Ordinal) && !key.EndsWith(StatusSuffix, StringComparison.Ordinal);
}

public enum EvidenceState
{
    /// <summary>Neither the value nor a status is present.</summary>
    Missing,
    Read,
    Absent,
    Unavailable,
    /// <summary>Read, but capped: the value is usable and incomplete.</summary>
    Truncated,
    /// <summary>A status that is not part of the contract. Never usable.</summary>
    Invalid,
}

public readonly record struct EvidenceStatus(EvidenceState State, string? Reason, int? TruncatedTotal)
{
    /// <summary>Only read (or read-but-truncated) evidence may be cited or used.</summary>
    public bool Usable => State is EvidenceState.Read or EvidenceState.Truncated;

    public string Label => State switch
    {
        EvidenceState.Read => "read",
        EvidenceState.Absent => "absent",
        EvidenceState.Unavailable => "unavailable:" + (Reason ?? string.Empty),
        EvidenceState.Truncated => "truncated:" + (TruncatedTotal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        EvidenceState.Invalid => "invalid",
        _ => "missing",
    };
}

/// <summary>One text leaf of an evidence value (an attribute value, a legend text, a PropertySet field...).</summary>
public sealed record EvidenceText(string Key, string Field, string Text, string? Tag);

/// <summary>Reads evidence values defensively. Pure: never throws on malformed input.</summary>
public static class EvidenceReader
{
    public const int MaxValueChars = 64 * 1024;
    public const int MaxTextChars = 200;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 16 };

    /// <summary>
    /// The status of one key. <c>ev_*</c> keys need an explicit status. Legacy <c>cad_*</c> keys
    /// (written before statuses existed) count as read when the value is present and no status says otherwise.
    /// </summary>
    public static EvidenceStatus Status(IReadOnlyDictionary<string, string> parameters, string key)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.TryGetValue(key + EvidenceKeys.StatusSuffix, out var raw);
        var hasValue = parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!hasValue) return new EvidenceStatus(EvidenceState.Missing, null, null);
            return key.StartsWith("ev_", StringComparison.Ordinal)
                ? new EvidenceStatus(EvidenceState.Invalid, "no status", null)
                : new EvidenceStatus(EvidenceState.Read, null, null);
        }
        var status = raw.Trim();
        if (status == "read") return hasValue ? new EvidenceStatus(EvidenceState.Read, null, null) : new EvidenceStatus(EvidenceState.Invalid, "read without value", null);
        if (status == "absent") return new EvidenceStatus(EvidenceState.Absent, null, null);
        if (status.StartsWith("unavailable:", StringComparison.Ordinal))
            return new EvidenceStatus(EvidenceState.Unavailable, status["unavailable:".Length..], null);
        if (status.StartsWith("truncated:", StringComparison.Ordinal) &&
            int.TryParse(status["truncated:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0)
            return hasValue ? new EvidenceStatus(EvidenceState.Truncated, null, total) : new EvidenceStatus(EvidenceState.Invalid, "truncated without value", null);
        return new EvidenceStatus(EvidenceState.Invalid, status.Length > 40 ? status[..40] : status, null);
    }

    /// <summary>
    /// The observation JSON of a usable key. A synthetic envelope <c>{"observation": ...}</c> is unwrapped.
    /// A plain (non-JSON) string value is returned as a JSON string.
    /// </summary>
    public static bool TryObservation(IReadOnlyDictionary<string, string> parameters, string key, out JsonElement observation)
    {
        observation = default;
        if (!Status(parameters, key).Usable || !parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxValueChars)
            return false;
        var trimmed = value.Trim();
        if (trimmed[0] is not ('{' or '[' or '"'))
        {
            observation = JsonSerializer.SerializeToElement(trimmed);
            return true;
        }
        try
        {
            using var document = JsonDocument.Parse(trimmed, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("observation", out var inner))
                root = inner;
            observation = root.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Text leaves of a usable evidence value, by key convention. Identity fields (handles, record ids, xref
    /// names, spaces, schema versions, association labels, distances) are never returned as text.
    /// </summary>
    /// <summary>
    /// Whether a record's ev_* values follow the contract this reader knows: <c>ev_schema</c> absent altogether (a legacy
    /// record; any ev_* there is read under the current rules), or present, read and exactly <c>mahod-evidence/1</c>. A
    /// present schema with a missing or invalid status, or another version, means no ev_* value may be read.
    /// </summary>
    public static bool SchemaReadable(IReadOnlyDictionary<string, string> parameters)
    {
        var status = Status(parameters, EvidenceKeys.Schema);
        return status.State == EvidenceState.Missing ||
               (status.Usable && TryObservation(parameters, EvidenceKeys.Schema, out var schema) &&
                schema.ValueKind == JsonValueKind.String && schema.GetString() == EvidenceKeys.SchemaV1);
    }

    public static IReadOnlyList<EvidenceText> Texts(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!TryObservation(parameters, key, out var observation)) return Array.Empty<EvidenceText>();
        var texts = new List<EvidenceText>();
        Collect(key, observation, texts, null, 0);
        return texts.GroupBy(t => (t.Field, t.Tag, t.Text)).Select(g => g.First()).ToList();
    }

    private static readonly HashSet<string> TextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "text", "component", "subassembly", "catalog_code", "name", "pattern", "description", "label",
    };

    private static readonly HashSet<string> ListFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "attributes", "properties", "props", "texts", "rows", "items",
    };

    private static void Collect(string key, JsonElement element, List<EvidenceText> texts, string? tag, int depth)
    {
        if (depth > 6 || texts.Count >= 64) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (depth == 0 && Clean(element.GetString()) is { } top) texts.Add(new EvidenceText(key, "value", top, tag));
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(key, item, texts, tag, depth + 1);
                break;
            case JsonValueKind.Object:
                // A placeholder PropertySet value ("Pay item for …") is a template, not evidence.
                if (element.TryGetProperty("placeholder", out var placeholder) && placeholder.ValueKind == JsonValueKind.True) return;
                var itemTag = tag;
                foreach (var name in new[] { "tag", "name" })
                    if (element.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String &&
                        element.TryGetProperty("value", out _))
                        itemTag = Clean(t.GetString()) ?? itemTag;
                foreach (var property in element.EnumerateObject())
                {
                    if (ListFields.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        Collect(key, property.Value, texts, itemTag, depth + 1);
                        continue;
                    }
                    if (!TextFields.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String) continue;
                    // "name" is the tag of a name/value pair, not text of its own.
                    if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) && element.TryGetProperty("value", out _)) continue;
                    if (Clean(property.Value.GetString()) is { } text) texts.Add(new EvidenceText(key, property.Name.ToLowerInvariant(), text, itemTag));
                }
                break;
        }
    }

    /// <summary>Strips control characters, collapses whitespace and caps the length. Null when nothing meaningful is left.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder(Math.Min(text.Length, MaxTextChars));
        var space = false;
        foreach (var c in text)
        {
            if (builder.Length >= MaxTextChars) break;
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '‎' or '‏' or '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩')
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// A stable fingerprint of the cited evidence of a set of records: SHA-256 over the sorted distinct
    /// (key, status, field, tag, text) tuples. Handles, distances, quantities and the drawing hash do not
    /// take part, so re-measuring or re-saving the drawing keeps the fingerprint; a changed meaning does not.
    /// </summary>
    public static string Fingerprint(IEnumerable<IReadOnlyDictionary<string, string>> recordParameters, IEnumerable<string> keys) =>
        Fingerprint(recordParameters, keys, null);

    /// <summary>
    /// Like <see cref="Fingerprint(IEnumerable{IReadOnlyDictionary{string, string}}, IEnumerable{string})"/>, but only the read
    /// texts <paramref name="cited"/> accepts take part; a key's unread status always does. A rule defined by a conjunction of
    /// texts fingerprints that conjunction, so another text of the same key that differs per object never makes it stale.
    /// </summary>
    public static string Fingerprint(IEnumerable<IReadOnlyDictionary<string, string>> recordParameters, IEnumerable<string> keys,
        Func<EvidenceText, bool>? cited)
    {
        var keyList = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var tuples = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var parameters in recordParameters)
            foreach (var key in keyList)
            {
                var status = Status(parameters, key);
                if (!status.Usable)
                {
                    tuples.Add($"{key}\u001f{status.State}");
                    continue;
                }
                foreach (var text in Texts(parameters, key))
                    if (cited == null || cited(text))
                        tuples.Add(string.Join('\u001f', key, "read", text.Field, text.Tag ?? string.Empty, text.Text));
            }
        var bytes = Encoding.UTF8.GetBytes(string.Join('\u001e', keyList) + '\u001d' + string.Join('\u001e', tuples));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
