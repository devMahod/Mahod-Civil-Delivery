using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Definition metadata only. Unknown entity count is deliberately not zero.</summary>
public sealed record EstimateSourceDefinition(
    string Key, string Name, string Path, string Status, bool IsHost = false, bool IsNested = false);

public sealed record EstimateSourceInventory(
    string HostFingerprint, string HostPath, IReadOnlyList<EstimateSourceDefinition> Sources);

/// <summary>Schema 10. Explicit engineer scope, never inferred exclusion authority.</summary>
public sealed class EstimateSourceSelection
{
    public string HostFingerprint { get; set; } = "";
    public string HostPath { get; set; } = "";
    public string InventoryHash { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public DateTime? ApprovedAtUtc { get; set; }
    public List<EstimateSourceChoice> Sources { get; set; } = new();
}

public sealed class EstimateSourceChoice
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Category { get; set; } = EstimateSourceSelectionPolicy.Unclassified;
    public bool Included { get; set; } = true;
}

public static class EstimateSourceSelectionPolicy
{
    public const string HostKey = "host";
    public const string Unclassified = "unclassified";
    public const string InvalidScopeCode = "EST-SOURCE-SELECTION-REVIEW";
    public const string ExcludedScopeCode = "EST-SOURCE-SELECTION-EXCLUDED";
    public static IReadOnlyDictionary<string, string> Categories { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Unclassified] = "לא מסווג", ["roads"] = "כבישים ופיתוח",
            ["markings"] = "סימון ותמרור", ["drainage"] = "ניקוז",
            ["utilities"] = "תשתיות", ["landscape"] = "אדריכלות נוף",
            ["architecture"] = "אדריכלות", ["survey"] = "מדידה ומצב קיים",
        };

    private static string Normalize(string? value) => (value ?? "").Trim().Replace('/', '\\').ToUpperInvariant();
    private static string Hash(object value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    /// <summary>Handle distinguishes a detached/replaced definition; path/name changes do not inherit exclusions.</summary>
    public static string XrefKey(string name, string path, string definitionHandle) =>
        Hash(new[] { Normalize(name), Normalize(path), Normalize(definitionHandle) });

    public static string InventoryHash(EstimateSourceInventory inventory) => Hash(new
    {
        Host = Normalize(inventory.HostFingerprint), Path = Normalize(inventory.HostPath),
        Sources = inventory.Sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new
        {
            s.Key, Name = Normalize(s.Name), Path = Normalize(s.Path), s.Status, s.IsHost, s.IsNested,
        }).ToArray(),
    });

    /// <summary>Name tokens propose a category only; ambiguous/unknown remains unclassified. Never changes Included.</summary>
    public static string SuggestCategory(string name)
    {
        var tokens = Regex.Split(Normalize(name), @"[^\p{L}\p{N}]+").ToHashSet(StringComparer.Ordinal);
        var matches = new List<string>();
        void Match(string category, params string[] words)
        {
            if (words.Any(tokens.Contains)) matches.Add(category);
        }
        Match("markings", "SIMUN", "SM", "MARK", "MARKING", "MARKINGS", "SIGN", "SIGNS", "סימון", "תמרור");
        Match("drainage", "DRAIN", "DRAINAGE", "DR", "NIKUZ", "ניקוז");
        Match("utilities", "WATER", "WTR", "SEWER", "ELECTRIC", "UTILITIES", "EL", "TEURA", "חשמל", "ביוב");
        Match("landscape", "LANDSCAPE", "GARDEN", "PLANTING", "LA", "NOF", "GN", "נוף", "גינון");
        Match("architecture", "ARCH", "ARCHITECTURE", "AR", "אדריכלות");
        Match("survey", "SURVEY", "TOPO", "SR", "SV", "מדידה");
        Match("roads", "ROAD", "ROADS", "PAVEMENT", "כבישים");
        // 984 shows HW/GM as broad container disciplines (also holding drainage/marking).
        // They may suggest roads/development only when no specific profession was found.
        // TR and UT alone are ambiguous in the supplied inventory/working conventions.
        if (matches.Count == 0) Match("roads", "HW", "GM", "RD");
        return matches.Count == 1 ? matches[0] : Unclassified;
    }

    public static bool SameHost(EstimateSourceInventory inventory, EstimateSourceSelection saved) =>
        Guid.TryParse(inventory.HostFingerprint, out var current) && current != Guid.Empty &&
        Guid.TryParse(saved.HostFingerprint, out var old) && current == old &&
        Normalize(inventory.HostPath) == Normalize(saved.HostPath);

    /// <summary>Detached editing copy. Cancel cannot mutate the project. Nested definitions inherit their branch.</summary>
    public static List<EstimateSourceChoice> CreateDraft(EstimateSourceInventory inventory, EstimateSourceSelection? saved)
    {
        EnsureInventory(inventory);
        var previous = saved != null && SameHost(inventory, saved)
            ? (saved.Sources ?? new()).GroupBy(s => s.Key).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal)
            : new Dictionary<string, EstimateSourceChoice>(StringComparer.Ordinal);
        return inventory.Sources.Where(s => !s.IsNested).Select(s =>
        {
            previous.TryGetValue(s.Key, out var choice);
            return new EstimateSourceChoice
            {
                Key = s.Key, Name = s.Name, Path = s.Path, Included = choice?.Included ?? true,
                Category = choice != null && Categories.ContainsKey(choice.Category)
                    ? choice.Category : SuggestCategory(s.Name),
            };
        }).ToList();
    }

    public static EstimateSourceSelection Approve(EstimateSourceInventory inventory,
        IEnumerable<EstimateSourceChoice> draft, string approver, DateTime utcNow)
    {
        EnsureInventory(inventory);
        var result = new EstimateSourceSelection
        {
            HostFingerprint = Guid.Parse(inventory.HostFingerprint).ToString("D"),
            HostPath = inventory.HostPath, InventoryHash = InventoryHash(inventory),
            ApprovedBy = approver.Trim(), ApprovedAtUtc = utcNow,
            Sources = draft.Select(s => new EstimateSourceChoice
            {
                Key = s.Key, Name = s.Name, Path = s.Path, Included = s.Included, Category = s.Category,
            }).ToList(),
        };
        var problems = ValidationProblems(result).Concat(MatchProblems(inventory, result)).ToArray();
        if (problems.Length != 0) throw new InvalidOperationException(string.Join("; ", problems));
        return result;
    }

    public static IReadOnlyList<string> ValidationProblems(EstimateSourceSelection selection)
    {
        var problems = new List<string>();
        if (!Guid.TryParse(selection.HostFingerprint, out var guid) || guid == Guid.Empty ||
            string.IsNullOrWhiteSpace(selection.HostPath)) problems.Add("חסרה זהות מארח תקינה להיקף המקורות");
        if (!CatalogIdentity.IsValidSha256(selection.InventoryHash)) problems.Add("חתימת מצאי המקורות אינה תקינה");
        if (string.IsNullOrWhiteSpace(selection.ApprovedBy) || selection.ApprovedAtUtc?.Kind != DateTimeKind.Utc)
            problems.Add("נדרש מאשר וזמן UTC לבחירת המקורות");
        var sources = selection.Sources;
        if (sources == null || sources.Count == 0 || sources.Any(s => s == null))
        {
            problems.Add("רשימת מקורות הבחירה חסרה");
            return problems;
        }
        if (sources.Count(s => s.Key == HostKey) != 1) problems.Add("נדרשת שורת מארח אחת");
        if (sources.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(s => s.Key != HostKey && !CatalogIdentity.IsValidSha256(s.Key)))
            problems.Add("מזהי מקורות כפולים או חסרים");
        if (sources.Any(s => s.Category == null || !Categories.ContainsKey(s.Category))) problems.Add("קטגוריית מקור לא מוכרת");
        if (!sources.Any(s => s.Included)) problems.Add("יש לבחור לפחות מקור אחד לסריקה");
        return problems;
    }

    public static IReadOnlyList<string> MatchProblems(EstimateSourceInventory inventory, EstimateSourceSelection selection)
    {
        var problems = new List<string>();
        if (!SameHost(inventory, selection) || InventoryHash(inventory) != selection.InventoryHash)
            problems.Add("מצאי המקורות או זהות המארח השתנו — יש לסקור ולאשר את המקורות מחדש");
        var actual = inventory.Sources.Where(s => !s.IsNested).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
        var saved = (selection.Sources ?? new()).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
        if (actual.Length != saved.Length || !actual.Zip(saved).All(pair => pair.First.Key == pair.Second.Key &&
                pair.First.Name == pair.Second.Name && pair.First.Path == pair.Second.Path))
            problems.Add("בחירת המקורות אינה תואמת לרשימת המקורות הנוכחית");
        return problems;
    }

    public static string ScopeSummary(EstimateSourceSelection selection)
    {
        var excluded = selection.Sources.Where(s => !s.Included).ToArray();
        var names = string.Join("; ", excluded.Select(s => $"{s.Name} [{s.Path}]"));
        return $"היקף מקורות מאושר: {selection.Sources.Count(s => s.Included)} כלולים; {excluded.Length} מוחרגים. " +
               (excluded.Length == 0 ? "כל המקורות כלולים." : "לא נמדדו (כולל הענפים המקוננים): " + names + ". מספר ישויות מוחרגות: לא ידוע — לא נמדדו.");
    }

    private static void EnsureInventory(EstimateSourceInventory inventory)
    {
        if (!Guid.TryParse(inventory.HostFingerprint, out var guid) || guid == Guid.Empty ||
            string.IsNullOrWhiteSpace(inventory.HostPath) || inventory.Sources.Count(s => s.IsHost && s.Key == HostKey) != 1 ||
            inventory.Sources.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != inventory.Sources.Count)
            throw new InvalidOperationException("מצאי המקורות אינו שלם או אינו קשור לשרטוט שמור");
    }
}
