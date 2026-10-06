using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>
/// Runs a classifier over Codex's synthetic recognition benchmark (codex-recognition-input-v1) and writes the
/// prediction contract (codex-recognition-predictions-v1). It reads only the input bytes it is given, never a
/// ground-truth file, and claims no score. Group method classes follow the draft builder, extended for the
/// benchmark's synthetic methods: count → block; length → closed-perimeter when the method names a closed-polyline
/// perimeter, else open; area → hatch only when the method names a hatch, else closed-polyline.
/// </summary>
public static class RecognitionBenchmarkAdapter
{
    public const string InputSchema = "codex-recognition-input-v1";
    public const string OutputSchema = "codex-recognition-predictions-v1";

    public sealed record BenchmarkGroup(string CaseId, string GroupId, IReadOnlyList<NeutralQuantityRecord> Records);

    public sealed record BenchmarkInput(string BenchmarkId, IReadOnlyList<BenchmarkGroup> Groups);

    /// <summary>One prediction row of the output contract.</summary>
    public sealed record Prediction(string CaseId, RecognitionProposal Proposal);

    public static BenchmarkInput Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) utf8 = utf8[3..];
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 64 });
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The benchmark input is not a JSON object.");
        var schema = Text(root, "schema_version");
        if (schema != null && schema != InputSchema)
            throw new InvalidDataException($"Unsupported benchmark input schema '{schema}' (expected {InputSchema}).");
        var benchmarkId = Text(root, "benchmark_id") ?? string.Empty;
        var groups = new List<BenchmarkGroup>();
        if (Property(root, "Cases") is { ValueKind: JsonValueKind.Array } cases)
            foreach (var @case in cases.EnumerateArray())
            {
                var caseId = Text(@case, "CaseId") ?? string.Empty;
                if (Property(@case, "Groups") is not { ValueKind: JsonValueKind.Array } caseGroups) continue;
                foreach (var group in caseGroups.EnumerateArray())
                {
                    var groupId = Text(group, "GroupId") ?? string.Empty;
                    var records = new List<NeutralQuantityRecord>();
                    if (Property(group, "Records") is { ValueKind: JsonValueKind.Array } items)
                        foreach (var item in items.EnumerateArray())
                            records.Add(Record(item, benchmarkId));
                    groups.Add(new BenchmarkGroup(caseId, groupId, records));
                }
            }
        return new BenchmarkInput(benchmarkId, groups);
    }

    /// <summary>
    /// The classifier inputs of one benchmark group. A group whose records differ in source, kind, unit or method
    /// class is handed over in homogeneous parts that keep the same GroupId.
    /// </summary>
    public static IReadOnlyList<RecognitionGroupInput> ToGroupInputs(BenchmarkGroup group, EngineerBoqLibrary library)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(library);
        return group.Records
            .GroupBy(r => (Source: SourceOf(r), Kind: KindOf(r), Unit: (r.Measurement.Unit ?? string.Empty).Trim(),
                Class: MethodClassFor(KindOf(r), r.Measurement.Method)))
            .Select(part =>
            {
                var records = part.ToList();
                var blocks = records.Select(BlockLeaf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new RecognitionGroupInput(group.GroupId, part.Key.Source, EngineerBoqDraftBuilder.SourceRole(part.Key.Source, library),
                    SectionProjectionLogic.LayerLeaf(records[0].Source.Layer), part.Key.Kind, part.Key.Unit, part.Key.Class,
                    part.Key.Kind == "count" && blocks.Count == 1 ? blocks[0] : null, records);
            }).ToList();
    }

    /// <summary>The draft builder's method class, with the benchmark's synthetic method names mapped as documented above.</summary>
    public static string MethodClassFor(string kind, string? method)
    {
        var cls = EngineerBoqDraftBuilder.MethodClass(kind, method);
        var m = (method ?? string.Empty).Trim().ToLowerInvariant();
        return kind switch
        {
            "count" => "block",
            "area" when cls == "other" => m.Contains("hatch", StringComparison.Ordinal) ? "hatch" : "closed-polyline",
            "length" when m.Contains("closed-polyline-perimeter", StringComparison.Ordinal) => "closed-perimeter",
            _ => cls,
        };
    }

    /// <summary>
    /// Classifies every group and returns the predictions in input order. A group whose record ids are missing or
    /// repeated is refused whole before it is split: one abstention covering each distinct id once, never a
    /// classification of its parts (which would repeat an id) and never an exception that stops the run.
    /// </summary>
    public static IReadOnlyList<Prediction> Predict(BenchmarkInput input, IFamilyClassifier classifier, EngineerBoqLibrary library,
        CatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(classifier);
        var predictions = new List<Prediction>();
        foreach (var group in input.Groups)
        {
            if (IdRefusal(group) is { } refusal)
            {
                predictions.Add(new Prediction(group.CaseId, refusal));
                continue;
            }
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < group.Records.Count; i++) order.TryAdd(group.Records[i].RecordId, i);
            var proposals = ToGroupInputs(group, library)
                .SelectMany(part => classifier.Classify(part, library, catalog))
                .OrderBy(p => p.RecordIds.Count == 0 ? int.MaxValue : p.RecordIds.Min(id => order.TryGetValue(id, out var at) ? at : int.MaxValue))
                .ThenBy(p => p.FamilyId ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            predictions.AddRange(proposals.Select(p => new Prediction(group.CaseId, p)));
        }
        return predictions;
    }

    /// <summary>The whole-group refusal for missing or repeated record ids (as the classifier's own duplicate-id refusal), or null.</summary>
    private static RecognitionProposal? IdRefusal(BenchmarkGroup group)
    {
        var ids = group.Records.Select(r => r.RecordId ?? string.Empty).ToList();
        var missing = ids.Any(string.IsNullOrWhiteSpace);
        var repeated = ids.Distinct(StringComparer.Ordinal).Count() != ids.Count;
        if (!missing && !repeated) return null;
        var reason = missing && repeated ? "מזהי רשומה חסרים וכפולים בקבוצה"
            : missing ? "לרשומה בקבוצה אין מזהה"
            : "מזהי רשומה כפולים בקבוצה";
        return new RecognitionProposal(group.GroupId, ids.Distinct(StringComparer.Ordinal).ToList(), RecognitionStatus.Abstained, null,
            Array.Empty<string>(), Array.Empty<RecognitionEvidenceRef>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<RecognitionAlternative>(), new[] { reason + " — הקבוצה לא סווגה; יש לתקן את הקלט." }, RecognitionProposal.OriginLocal);
    }

    /// <summary>Reads the exact input bytes, classifies and returns the prediction JSON (UTF-8, readable Hebrew).</summary>
    public static string Run(byte[] inputBytes, IFamilyClassifier classifier, EngineerBoqLibrary library, CatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(inputBytes);
        ArgumentNullException.ThrowIfNull(library);
        var input = Parse(inputBytes);
        var predictions = Predict(input, classifier, library, catalog);
        var identity = $"{classifier.Identity}; library={library.Id}@{LibraryIdentity.LibraryHash(library)[..12]}; " +
                       $"catalog={(catalog == null ? "none" : catalog.SnapshotId)}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", OutputSchema);
            writer.WriteString("benchmark_id", input.BenchmarkId);
            writer.WriteString("inputs_sha256", Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant());
            writer.WriteString("execution_status", "completed");
            writer.WriteString("classifier_identity", identity);
            writer.WriteStartArray("Predictions");
            foreach (var (caseId, proposal) in predictions)
            {
                writer.WriteStartObject();
                writer.WriteString("CaseId", caseId);
                writer.WriteString("GroupId", proposal.GroupId);
                Strings(writer, "RecordIds", proposal.RecordIds);
                writer.WriteString("Status", proposal.Status == RecognitionStatus.Proposed ? "proposed" : "abstained");
                if (proposal.Status == RecognitionStatus.Proposed && proposal.FamilyId != null) writer.WriteString("FamilyId", proposal.FamilyId);
                else writer.WriteNull("FamilyId");
                Strings(writer, "CandidateCodes", proposal.Status == RecognitionStatus.Proposed ? proposal.CandidateCodes : Array.Empty<string>());
                writer.WriteStartArray("EvidenceRefs");
                foreach (var reference in proposal.EvidenceRefs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", reference.Key);
                    Strings(writer, "recordIds", reference.RecordIds);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                Strings(writer, "Alternatives", proposal.Alternatives.Select(a => a.FamilyId).ToList());
                Strings(writer, "MissingDetails", proposal.MissingDetails);
                writer.WriteString("Origin", proposal.Origin);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static NeutralQuantityRecord Record(JsonElement item, string runId)
    {
        var recordId = Text(item, "RecordId") ?? string.Empty;
        var source = Property(item, "Source") is { ValueKind: JsonValueKind.Object } s ? s : default;
        var measurement = Property(item, "Measurement") is { ValueKind: JsonValueKind.Object } m ? m : default;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (measurement.ValueKind == JsonValueKind.Object && Property(measurement, "Parameters") is { ValueKind: JsonValueKind.Object } values)
            foreach (var parameter in values.EnumerateObject())
                switch (parameter.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        parameters[parameter.Name] = parameter.Value.GetString()!;
                        break;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        break;
                    default:
                        // A structured value is kept as its exact JSON text; the evidence reader parses it.
                        parameters[parameter.Name] = parameter.Value.GetRawText();
                        break;
                }
        var raw = measurement.ValueKind == JsonValueKind.Object && Property(measurement, "RawValue") is { ValueKind: JsonValueKind.Number } number &&
                  number.TryGetDouble(out var value) ? value : 0;
        string? Field(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object ? Text(element, name) : null;
        return new NeutralQuantityRecord
        {
            RecordId = recordId,
            ProjectProfileId = "benchmark",
            RunId = runId,
            Source = new QuantitySource
            {
                Drawing = Field(source, "Drawing") ?? string.Empty,
                DrawingHash = Field(source, "DrawingHash") ?? string.Empty,
                Handle = Field(source, "Handle") ?? recordId,
                EntityType = Field(source, "EntityType") ?? string.Empty,
                Layer = Field(source, "Layer"),
                Xref = Field(source, "Xref"),
            },
            Measurement = new QuantityMeasurement
            {
                Kind = Field(measurement, "Kind") ?? string.Empty,
                Method = Field(measurement, "Method") ?? string.Empty,
                RawValue = raw,
                Unit = Field(measurement, "Unit") ?? string.Empty,
                Parameters = parameters,
            },
        };
    }

    private static string KindOf(NeutralQuantityRecord record) => (record.Measurement.Kind ?? string.Empty).Trim().ToLowerInvariant();

    private static string SourceOf(NeutralQuantityRecord record)
    {
        var value = (record.Source.Xref ?? string.Empty).Trim();
        return value.Length == 0 || value.Equals("(host)", StringComparison.OrdinalIgnoreCase) ? EngineerBoqDraftBuilder.HostSource : value;
    }

    private static string? BlockLeaf(NeutralQuantityRecord record)
    {
        var parameters = record.Measurement.Parameters;
        return EvidenceReader.Status(parameters, EvidenceKeys.BlockNameEffective).Usable &&
               parameters.TryGetValue(EvidenceKeys.BlockNameEffective, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? SectionProjectionLogic.LayerLeaf(raw)
            : null;
    }

    /// <summary>A property by exact name, else case-insensitively; an undefined element when absent.</summary>
    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (element.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
