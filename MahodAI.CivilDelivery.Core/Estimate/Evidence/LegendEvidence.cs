using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>
/// One captioned row of a legend printed on a sheet of the host drawing: the sample line's style and its caption, with
/// where it was read. <see cref="AciIndex"/> is the sample's effective AutoCAD colour index (-1 when it is not an index);
/// <see cref="Linetype"/> is the effective line type as read. <see cref="IdentifiedByName"/>: the legend block was found by
/// its name (מקרא / mikra / legend), not guessed by shape.
/// </summary>
public sealed record LegendEntry(string Text, int AciIndex, string? Linetype, string Layout, string LegendBlock, bool IdentifiedByName);

/// <summary>
/// <c>ev_legend_row</c> for one record: the legend rows whose sample line has the record's own style. The legend is the
/// sheet's own statement of what a style means, so the comparison is the pair a drafter sees — effective colour and line
/// type — and a row is a candidate, never a verified fact about the object. Pure and host-free.
/// <list type="bullet">
/// <item><c>match:"linetype"</c> (strong for recognition) only when the pair matches, the line type is a distinctive one
/// (not Continuous, ByLayer, ByBlock or unknown) and the legend block was identified by its name;</item>
/// <item><c>match:"color"</c> when the pair matches on a plain line type (colour is what tells such rows apart);</item>
/// <item><c>match:"legend-guess"</c> when the legend block was only guessed by its shape; both last two only narrow.</item>
/// </list>
/// No legend on any sheet is <c>absent</c>; an unreadable legend or record style is <c>unavailable</c>, never absent.
/// </summary>
public static class LegendEvidence
{
    public const int MaxRows = 8;
    public const string MatchLinetype = "linetype";
    public const string MatchColor = "color";
    public const string MatchLegendGuess = "legend-guess";

    /// <summary>
    /// A partial legend is not an empty or fully read legend. Keep an existing specific refusal;
    /// otherwise refuse automatic evidence. Readable rows remain in the reader's result for legacy consumers.
    /// No missing-row count is inferred from the number of failed getters.
    /// </summary>
    public static EvidenceValue ApplyReadCompleteness(EvidenceValue value, bool complete) =>
        complete || value.Status?.StartsWith(EvidenceValue.UnavailablePrefix, StringComparison.Ordinal) == true
            ? value
            : EvidenceValue.Unavailable("legend-read-incomplete");

    private static readonly HashSet<string> PlainLinetypes = new(StringComparer.Ordinal)
    {
        "continuous", "bylayer", "byblock", "?", string.Empty,
    };

    /// <summary>A line type compared on its bare, lower-case name: an XREF prefix ("survey|DASHED") is dropped.</summary>
    public static string NormalizeLinetype(string? linetype)
    {
        if (string.IsNullOrWhiteSpace(linetype)) return "?";
        var name = linetype.Trim().Trim('"');
        var bar = name.LastIndexOf('|');
        if (bar >= 0 && bar < name.Length - 1) name = name[(bar + 1)..];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The record's effective line type from its CAD metadata: the entity's own unless it is ByLayer (then the
    /// layer's); ByBlock or missing is unknown (null).
    /// </summary>
    public static string? RecordLinetype(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue(EvidenceKeys.EntityLinetype, out var own);
        var entity = NormalizeLinetype(own);
        if (entity is "byblock") return null;
        if (entity is not ("bylayer" or "?")) return entity;
        parameters.TryGetValue(EvidenceKeys.LayerLinetype, out var layer);
        var fromLayer = NormalizeLinetype(layer);
        return fromLayer is "?" or "bylayer" or "byblock" ? null : fromLayer;
    }

    /// <summary>The record's effective colour as "R,G,B" when <c>ev_color_effective</c> was read; otherwise null.</summary>
    public static string? RecordColor(IReadOnlyDictionary<string, string> parameters)
    {
        if (!EvidenceReader.Status(parameters, EvidenceKeys.ColorEffective).Usable) return null;
        if (!parameters.TryGetValue(EvidenceKeys.ColorEffective, out var raw) || raw == null) return null;
        var parts = raw.Trim().Trim('"').Split(',');
        if (parts.Length != 3) return null;
        var channels = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out channels[i]) ||
                channels[i] is < 0 or > 255)
                return null;
        return string.Join(",", channels.Select(c => c.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The value for one record. <paramref name="legendFailure"/> non-null: the legends could not be read (every record
    /// is unavailable). An empty <paramref name="legend"/> with no failure: no sheet carries a legend (absent).
    /// </summary>
    public static EvidenceValue ForRecord(IReadOnlyList<LegendEntry> legend, string? legendFailure,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(legend);
        ArgumentNullException.ThrowIfNull(parameters);
        if (legendFailure != null) return EvidenceValue.Unavailable(legendFailure);
        if (legend.Count == 0) return EvidenceValue.Absent;
        var color = RecordColor(parameters);
        var linetype = RecordLinetype(parameters);
        if (color == null || linetype == null) return EvidenceValue.Unavailable("record-style-unread");

        var rows = new List<(LegendEntry Entry, string Match)>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var entry in legend)
        {
            var text = EvidenceJson.Text(entry.Text, EvidenceReader.MaxTextChars);
            if (text == null) continue;
            if (!string.Equals(EffectiveColorPolicy.AciToRgbText(entry.AciIndex), color, StringComparison.Ordinal)) continue;
            var rowLinetype = NormalizeLinetype(entry.Linetype);
            if (!string.Equals(rowLinetype, linetype, StringComparison.Ordinal)) continue;
            var match = !entry.IdentifiedByName ? MatchLegendGuess
                : PlainLinetypes.Contains(rowLinetype) ? MatchColor
                : MatchLinetype;
            // The same caption and style repeated on several sheets is one row.
            if (!seen.Add((text, rowLinetype, match))) continue;
            rows.Add((entry with { Text = text }, match));
        }
        if (rows.Count == 0) return EvidenceValue.Absent;
        var ordered = rows
            .OrderBy(row => row.Match == MatchLinetype ? 0 : row.Match == MatchColor ? 1 : 2)
            .ThenBy(row => row.Entry.Text, StringComparer.Ordinal)
            .ThenBy(row => row.Entry.Layout, StringComparer.Ordinal)
            .ToList();
        var shown = ordered.Take(MaxRows).ToList();
        var json = EvidenceJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("rows");
            foreach (var (entry, match) in shown)
            {
                writer.WriteStartObject();
                writer.WriteString("text", entry.Text);
                writer.WriteString("match", match);
                writer.WriteString("style", color + "/" + linetype);
                writer.WriteString("layout", EvidenceJson.Text(entry.Layout, 80) ?? string.Empty);
                writer.WriteString("legend_block", EvidenceJson.Text(entry.LegendBlock, 80) ?? string.Empty);
                writer.WriteBoolean("legend_by_name", entry.IdentifiedByName);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        return ordered.Count > shown.Count ? EvidenceValue.Truncated(json, ordered.Count) : EvidenceValue.Read(json);
    }
}
