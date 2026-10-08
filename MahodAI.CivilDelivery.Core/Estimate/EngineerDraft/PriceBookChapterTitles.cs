using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.EngineerDraft;

/// <summary>
/// Reads the chapter and sub-chapter heading rows of an NTI price-list workbook
/// ("פרק 51 - עבודות סלילה", "תת פרק 51.04 - שכבת אספלטיות במיסעות"). The price
/// loader drops these rows; the engineer draft only uses them as display titles.
/// Any read failure returns an empty map: titles are presentation, never authority.
/// </summary>
public static class PriceBookChapterTitles
{
    private static readonly Regex Heading = new(
        @"^\s*(?<kind>תת\s+פרק|פרק)\s+(?<key>\d{2}(?:\.\d{2})?)\s*(?:-|–)?\s*(?<title>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, string> Read(string? xlsxPath) => Read(xlsxPath, null);

    /// <summary>
    /// Titles from <paramref name="sheetName"/> when the book was registered with an explicit sheet choice (b15),
    /// otherwise from the first sheet as before; never from another sheet of the same workbook.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string? xlsxPath, string? sheetName)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xlsxPath)) return titles;
        try
        {
            var rows = PriceBookWorkbook.ReadSheet(System.IO.File.ReadAllBytes(xlsxPath), sheetName);
            foreach (var row in rows)
            {
                var first = row.FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.Text));
                if (string.IsNullOrWhiteSpace(first.Text)) continue;
                var match = Heading.Match(first.Text);
                if (!match.Success) continue;
                var key = match.Groups["key"].Value;
                var title = match.Groups["title"].Value.Trim();
                if (!titles.ContainsKey(key))
                    titles[key] = string.IsNullOrEmpty(title) ? first.Text.Trim() : $"{key} — {title}";
            }
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return titles;
    }
}
