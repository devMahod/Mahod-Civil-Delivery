using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>Drawing-specific choices; stale geometry or catalogue identities never inherit a price.</summary>
public static class InfrastructureReviewStore
{
    public sealed record Choice(string Key, string? Code, bool Excluded, string? CatalogHash = null, string? Topic = null,
        string? MeasurementUnit = null, double? ManualQuantity = null, bool AutomaticallyAssigned = false);
    public sealed record Review(string DrawingHash, string CatalogHash, DateTime SavedAtUtc, Choice[] Choices);
    public static string FileName(string drawing) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(drawing).ToUpperInvariant()))) + ".json";
    private static string Key(InfrastructureGroup g) => JsonSerializer.Serialize(new { g.Source, g.Layer, g.Topic, g.Count, g.LengthMetres, g.ClosedCount });
    private static string LegacyKey(InfrastructureGroup g) => JsonSerializer.Serialize(new { g.Source, g.Layer, g.Count, g.LengthMetres, g.ClosedCount });
    private static string StableKey(InfrastructureGroup g) => JsonSerializer.Serialize(new { g.Source, g.Layer, g.Count, g.LengthMetres, g.ClosedCount, g.AreaSquareMetres, g.VolumeCubicMetres, g.CountUnits, g.GroupPart });
    public static void Save(string path, string drawingHash, CatalogSnapshot catalog, IReadOnlyList<InfrastructureGroup> groups,
        ISet<InfrastructureGroup> excluded)
        => Save(path, drawingHash, new[] { catalog }, groups, excluded);
    public static void Save(string path, string drawingHash, IReadOnlyList<CatalogSnapshot> catalogs, IReadOnlyList<InfrastructureGroup> groups,
        ISet<InfrastructureGroup> excluded)
    {
        var hashes = catalogs.Select(c => c.FileHash).ToHashSet(StringComparer.Ordinal);
        var review = new Review(drawingHash, catalogs.Count == 1 ? catalogs[0].FileHash : "multiple-editions", DateTime.UtcNow,
            groups.Select(g => new Choice(StableKey(g), g.CatalogHash != null && hashes.Contains(g.CatalogHash) ? g.Code : null,
                excluded.Contains(g), g.CatalogHash, g.Topic, g.MeasurementUnit, g.ManualQuantity, g.AutomaticallyAssigned)).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(review)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static int Restore(string path, string drawingHash, CatalogSnapshot catalog, IReadOnlyList<InfrastructureGroup> groups,
        ISet<InfrastructureGroup> excluded)
        => Restore(path, drawingHash, new[] { catalog }, groups, excluded);
    public static int Restore(string path, string drawingHash, IReadOnlyList<CatalogSnapshot> catalogs, IReadOnlyList<InfrastructureGroup> groups,
        ISet<InfrastructureGroup> excluded)
    {
        if (!File.Exists(path)) return 0;
        var saved = JsonSerializer.Deserialize<Review>(File.ReadAllText(path));
        if (saved == null || saved.DrawingHash != drawingHash) return 0;
        var books = catalogs.GroupBy(c => c.FileHash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var choices = saved.Choices.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var restored = 0;
        foreach (var group in groups)
        {
            if (!choices.TryGetValue(StableKey(group), out var choice) &&
                !(group.GroupPart.Length == 0 && (choices.TryGetValue(LegacyKey(group), out choice) || choices.TryGetValue(Key(group), out choice)))) continue;
            if (choice.Topic != null && InfrastructureBoq.Topics.Contains(choice.Topic)) group.Topic = choice.Topic;
            if (choice.MeasurementUnit != null) group.MeasurementUnit = choice.MeasurementUnit;
            if (choice.ManualQuantity is > 0 && double.IsFinite(choice.ManualQuantity.Value)) group.ManualQuantity = choice.ManualQuantity;
            if (choice.Code != null && books.TryGetValue(choice.CatalogHash ?? saved.CatalogHash, out var catalog) &&
                catalog.Items.TryGetValue(choice.Code, out var item) && item.Unit.SameUnit(Units.Parse(group.MeasurementUnit)) && group.Quantity > 0)
            { group.Assign(catalog, choice.Code, choice.AutomaticallyAssigned); restored++; }
            if (choice.Excluded) excluded.Add(group);
        }
        return restored;
    }
}
