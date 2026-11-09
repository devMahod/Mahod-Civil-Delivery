using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

// The pavement-structure report of a drawing (דו"ח תכן מבנה מיסעות), as MahodAI's server read it: the model reads the
// consultant's PDF, the server verifies it, and these records carry it. Nothing here was guessed by the tool — a value the
// report does not state is null, and the server's doubts arrive in Problems.

public sealed record PavementReportLayer(
    double ThicknessCm, string Material, string Kind, double? AggregateMm = null, string? Binder = null,
    string? AggregateType = null, string? BaseGrade = null);

public sealed record PavementReportStructure(
    string Id, string Family, string Name, string? Section, string Use, string Variant, double? MillingCm,
    IReadOnlyList<PavementReportLayer> Layers, double? StatedTotalCm, double TotalCm, bool TotalMatches, int? Page)
{
    /// <summary>"מבנה 1 · 5.2.1 · החלפה" — short enough for a BOQ row.</summary>
    [JsonIgnore]
    public string ShortName
    {
        get
        {
            var family = Family.All(char.IsDigit) ? "מבנה " + Family : Name.Length <= 28 ? Name : Name[..28].TrimEnd() + "…";
            var variant = Variant switch { "widening" => "הרחבת נתיב", "overlay" => "קרצוף וריבוד", _ => "" };
            return family + (Section is { Length: > 0 } s ? " · " + s : "") + (variant.Length > 0 ? " · " + variant : "");
        }
    }
    [JsonIgnore] public string LayersDisplay => string.Join(" + ", Layers.Select(l => l.ThicknessCm.ToString("0.#", CultureInfo.InvariantCulture))) +
        " = " + TotalCm.ToString("0.#", CultureInfo.InvariantCulture) + " ס״מ";
}

public sealed record PavementReportSegment(
    string? Segment, string? FromSection, string? ToSection, string? RoadName, string? RoadFamily, string? BusLaneFamily,
    string? BusLaneNote = null, int? Page = null);

public sealed record PavementReportCoats(bool? TackBetweenAsphaltLayers, bool? PrimeOnBase, string? Note);

public sealed record PavementReportDocument(string? Title, string? Project, string? Consultant, string? Date, string? Edition, string? Reference);

public sealed record PavementReportContent(
    PavementReportDocument Document, IReadOnlyList<PavementReportStructure> Structures, IReadOnlyList<PavementReportSegment> Segments,
    PavementReportCoats Coats, IReadOnlyList<string> Notes, IReadOnlyList<string> Problems);

/// <summary>One report attached to a drawing: the file it came from, when and by which model it was read, and its content.</summary>
public sealed record AttachedPavementReport(
    string FileName, string FileSha256, DateTime ReadUtc, string Model, string PromptVersion, int? Pages,
    PavementReportContent Content, IReadOnlyList<string> Warnings)
{
    [JsonIgnore] public string Label => (Content.Document.Title is { Length: > 0 } t ? t : FileName);
}

/// <summary>The server's JSON answer (snake_case) and the per-drawing store of attached reports.</summary>
public static class PavementReportJson
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>The <c>report</c> object of a <c>POST /api/v1/boq/pavement-report</c> answer.</summary>
    public static PavementReportContent ParseContent(JsonElement report)
    {
        var content = report.Deserialize<PavementReportContent>(Wire) ?? throw new InvalidDataException("תשובת השרת ריקה");
        // The server already verified the report; this guards the tool against a server that did not (older, or not ours).
        var structures = (content.Structures ?? Array.Empty<PavementReportStructure>())
            .Where(s => s != null && s.Layers is { Count: > 0 } && s.Layers.All(l => l != null && double.IsFinite(l.ThicknessCm) && l.ThicknessCm > 0 && l.ThicknessCm <= 150))
            .ToArray();
        if (structures.Length == 0) throw new InvalidDataException("לא נמצאו בדו״ח מבני מיסעה עם שכבות ועוביים");
        return content with
        {
            Document = content.Document ?? new PavementReportDocument(null, null, null, null, null, null),
            Structures = structures,
            Segments = (content.Segments ?? Array.Empty<PavementReportSegment>()).Where(s => s != null).ToArray(),
            Coats = content.Coats ?? new PavementReportCoats(null, null, null),
            Notes = content.Notes ?? Array.Empty<string>(),
            Problems = content.Problems ?? Array.Empty<string>(),
        };
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

/// <summary>
/// The reports attached to one drawing and the engineer's choices about them, saved beside the drawing's review
/// (<c>&lt;review&gt;.pavement.json</c>). A drawing may need more than one report: large drawings are split between
/// consultants' reports by area, each with its own segments.
/// </summary>
public sealed record PavementReportSet(
    IReadOnlyList<AttachedPavementReport> Reports,
    IReadOnlyDictionary<string, string> Variants,
    double BandHalfWidthMetres = PavementReportSet.DefaultBandHalfWidth,
    string? FallbackFamily = null)
{
    public const double DefaultBandHalfWidth = 30;
    public static PavementReportSet Empty { get; } = new(Array.Empty<AttachedPavementReport>(), new Dictionary<string, string>());
    [JsonIgnore] public bool HasReport => Reports.Count > 0;

    /// <summary>The road variant the engineer chose for a structure family ("full" — new/full replacement — by default).</summary>
    public string VariantOf(int report, string family) => Variants.TryGetValue(Key(report, family), out var v) ? v : "full";
    public static string Key(int report, string family) => report.ToString(CultureInfo.InvariantCulture) + ":" + family;

    private sealed record Stored(int SchemaVersion, PavementReportSet Set);
    public static PavementReportSet Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is { SchemaVersion: 1, Set.Reports: not null } ? stored.Set with { Variants = stored.Set.Variants ?? new Dictionary<string, string>() } : Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return Empty; }
    }
    public static void Save(string path, PavementReportSet set)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(1, set))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
