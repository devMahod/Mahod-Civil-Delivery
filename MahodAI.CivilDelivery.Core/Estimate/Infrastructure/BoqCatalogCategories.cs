namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>Publisher-owned chapter folders, derived from the selected edition rather than another publisher's numbering.</summary>
public sealed record BoqCatalogCategory(string Code, string Title)
{
    public string Label => Code.Length == 0 ? "כל התחומים" : Code.StartsWith('$') ? Title : Code + " · " + Title;
    public bool Contains(CatalogItem item)
    {
        if (Code.Length == 0) return true;
        if (!Code.StartsWith('$')) return item.Chapter == Code || item.SubChapter == Code;
        var text = InfrastructureCatalogSearch.Normalize(item.Description + " " + item.ChapterPath);
        return Code switch
        {
            "$demolition" => System.Text.RegularExpressions.Regex.IsMatch(text, "פירוק|הריס|עקיר|פינוי"),
            "$paving" => System.Text.RegularExpressions.Regex.IsMatch(text, "ריצוף|ריצופ|משתלב|מדרכ"),
            "$earth" => System.Text.RegularExpressions.Regex.IsMatch(text, "עבודות עפר|חפיר|חציב|מילוי"),
            "$concrete" => text.Contains("בטון"),
            "$landscape" => System.Text.RegularExpressions.Regex.IsMatch(text, "גינון|נטיע|שתיל|דשא|עצים|צמח"),
            "$infrastructure" => System.Text.RegularExpressions.Regex.IsMatch(text, @"ביוב|ניקוז|חשמל|רמזור|תאורה|תקשורת|השקיה|(?<!\p{L})(מים|גז)(?!\p{L})"),
            _ => item.Chapter == Code || item.SubChapter == Code
        };
    }
}

public static class BoqCatalogCategories
{
    public static IReadOnlyList<BoqCatalogCategory> For(InfrastructurePriceBook book)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in book.Catalog.Items.Values)
        {
            var path = (book.Chapters.GetValueOrDefault(item.Code) ?? item.ChapterPath).Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            folders.TryAdd(item.Chapter, path.FirstOrDefault() ?? "פרק " + item.Chapter);
            if (path.Length > 1) folders.TryAdd(item.SubChapter, path[1]);
        }
        var focus = new[] { new BoqCatalogCategory("$demolition", "פירוקים והריסות"), new BoqCatalogCategory("$paving", "ריצופים"),
            new BoqCatalogCategory("$earth", "עבודות עפר"), new BoqCatalogCategory("$concrete", "בטון"),
            new BoqCatalogCategory("$landscape", "גינון ופיתוח נוף"), new BoqCatalogCategory("$infrastructure", "תשתיות") };
        return new[] { new BoqCatalogCategory("", "כל התחומים") }.Concat(focus).Concat(folders.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new BoqCatalogCategory(p.Key, p.Value))).ToArray();
    }
}
