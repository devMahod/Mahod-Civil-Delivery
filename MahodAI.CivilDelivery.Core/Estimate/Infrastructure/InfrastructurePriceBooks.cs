using System.Security.Cryptography;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>Separate edition identities: identical item numbers from different publishers never share a price.</summary>
public sealed record InfrastructurePriceBook(string Publisher, string Edition, CatalogSnapshot Catalog,
    IReadOnlyDictionary<string, string> Chapters, bool UsesNtiChapters = false)
{
    public string Label => Publisher + " · " + Edition;
}

/// <summary>Offline, private snapshots. No database credentials or network connection is shipped with the tool.</summary>
public static class InfrastructurePriceBooks
{
    public sealed record Row(string Code, string Description, string Unit, decimal? Price, string Chapter);
    public sealed record Document(int SchemaVersion, string Publisher, string Edition, string SourceSystem,
        string SourceEditionId, DateTime ImportedAtUtc, Row[] Items);
    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodCivilDelivery", "price-books", "ktavkam");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<InfrastructurePriceBook> Load(string directory, ICollection<string>? findings = null)
    {
        if (!Directory.Exists(directory)) return Array.Empty<InfrastructurePriceBook>();
        var books = new List<InfrastructurePriceBook>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.pricebook.json").Order(StringComparer.Ordinal).Take(100))
        {
            try
            {
                if (new FileInfo(file).Length > 100_000_000) throw new InvalidDataException("מחירון גדול מדי");
                var bytes = File.ReadAllBytes(file);
                var d = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new InvalidDataException("מחירון ריק");
                if (d.SchemaVersion != 1 || string.IsNullOrWhiteSpace(d.Publisher) || string.IsNullOrWhiteSpace(d.Edition) || d.Items == null || d.Items.Length > 200000)
                    throw new InvalidDataException("מבנה מחירון לא נתמך");
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var catalog = new CatalogSnapshot { SnapshotId = d.Publisher + " · " + d.Edition, FileHash = hash,
                    PublicationNote = $"{d.SourceSystem} · מהדורה {d.SourceEditionId} · העתק מקומי מתאריך {d.ImportedAtUtc:dd.MM.yyyy}; זו המהדורה השמורה במקור, לא בהכרח מחירון עדכני" };
                var chapters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in d.Items)
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Description) || row.Price < 0)
                        throw new InvalidDataException("סעיף לא תקין");
                    if (!catalog.Items.TryAdd(row.Code, new CatalogItem { Code = row.Code, Description = row.Description, UnitRaw = row.Unit ?? "" }))
                        throw new InvalidDataException("מספר סעיף כפול במהדורה: " + row.Code);
                    catalog.Prices[row.Code] = new PriceRecord { Code = row.Code, Price = row.Price, PriceBookId = catalog.SnapshotId, SourceHash = hash };
                    chapters[row.Code] = row.Chapter ?? "";
                }
                books.Add(new(d.Publisher, d.Edition, catalog, chapters));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { findings?.Add("מחירון מקומי לא נטען: " + Path.GetFileName(file) + " — " + ex.Message); }
        }
        return books;
    }
}

/// <summary>Project BOQ use is a modest retrieval prior, never a labelled layer/item mapping or a price source.</summary>
public sealed class InfrastructureCatalogUsage
{
    public sealed record Example(string Description, string Unit, string[] Projects);
    public sealed record Document(int SchemaVersion, DateTime ImportedAtUtc, string EvidenceKind, Example[] Items);
    private readonly Dictionary<(string Text, string Unit), int> _counts = new();
    public InfrastructureCatalogUsage(IEnumerable<Example> examples)
    {
        foreach (var e in examples)
        {
            var key = (InfrastructureCatalogSearch.Normalize(e.Description), Units.Parse(e.Unit).Canonical);
            if (key.Item2 == "?" || e.Projects == null) continue;
            _counts[key] = Math.Max(_counts.GetValueOrDefault(key), e.Projects.Distinct(StringComparer.Ordinal).Count());
        }
    }
    public int Projects(CatalogItem item) => _counts.GetValueOrDefault((InfrastructureCatalogSearch.Normalize(item.Description), item.Unit.Canonical));
    public static InfrastructureCatalogUsage Load(string path)
    {
        if (!File.Exists(path)) return new(Array.Empty<Example>());
        if (new FileInfo(path).Length > 30_000_000) throw new InvalidDataException("קובץ ניסיון הפרויקטים גדול מדי");
        var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (data?.SchemaVersion != 1 || data.EvidenceKind != "project-boq-context" || data.Items == null ||
            data.Items.Any(e => e == null || e.Description == null || e.Unit == null || e.Projects == null))
            throw new InvalidDataException("מבנה ניסיון הפרויקטים אינו נתמך");
        return new(data.Items);
    }
}
