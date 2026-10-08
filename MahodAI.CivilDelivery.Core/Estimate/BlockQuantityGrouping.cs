using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Groups the nominal type and dimensions of exported structural columns.
/// Original names remain on every source record. Other block naming schemes are left intact.</summary>
public static class BlockQuantityGrouping
{
    private static readonly Regex ExportedColumn = new(
        @"^(?<type>STC_.+?\bPARA\s+\d+(?:[.,]\d+)?_\d+(?:[.,]\d+)?(?:\s+\d+)?)-(?:\d{6,}|V\d+)(?<suffix>-B\d+\s+WK(?:_\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Name(string original)
    {
        // XREF qualification describes the source, rather than the column type.
        var leaf = original[(original.LastIndexOf('|') + 1)..].Trim();
        var match = ExportedColumn.Match(leaf);
        return match.Success
            ? match.Groups["type"].Value + match.Groups["suffix"].Value
            : original.Trim();
    }
}
