using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>Drawing-specific choices; stale geometry or catalogue identities never inherit a price.</summary>
public static class InfrastructureReviewStore
{
    public sealed record Choice(string Key, string? Code, bool Excluded);
    public sealed record Review(string DrawingHash, string CatalogHash, DateTime SavedAtUtc, Choice[] Choices);
    public static string FileName(string drawing) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(drawing).ToUpperInvariant()))) + ".json";
    private static string Key(InfrastructureGroup g) => JsonSerializer.Serialize(new { g.Source, g.Layer, g.Topic, g.Count, g.LengthMetres, g.ClosedCount });
    public static void Save(string path, string drawingHash, CatalogSnapshot catalog, IReadOnlyList<InfrastructureGroup> groups,
        ISet<InfrastructureGroup> excluded)
    {
        var review = new Review(drawingHash, catalog.FileHash, DateTime.UtcNow,
            groups.Select(g => new Choice(Key(g), g.CatalogHash == catalog.FileHash ? g.Code : null, excluded.Contains(g))).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(review)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static int Restore(string path, string drawingHash, CatalogSnapshot catalog, IReadOnlyList<InfrastructureGroup> groups,
        ISet<InfrastructureGroup> excluded)
    {
        if (!File.Exists(path)) return 0;
        var saved = JsonSerializer.Deserialize<Review>(File.ReadAllText(path));
        if (saved == null || saved.DrawingHash != drawingHash || saved.CatalogHash != catalog.FileHash) return 0;
        var choices = saved.Choices.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var restored = 0;
        foreach (var group in groups)
        {
            if (!choices.TryGetValue(Key(group), out var choice)) continue;
            if (choice.Code != null && catalog.Items.TryGetValue(choice.Code, out var item) && item.Unit.SameUnit(Units.Parse("מטר")))
            { group.Assign(catalog, choice.Code); restored++; }
            if (choice.Excluded) excluded.Add(group);
        }
        return restored;
    }
}
