using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>
/// <c>ev_pset_component</c> for one entity: the manual values of the AEC PropertySets attached directly to it, as a
/// compact array of <c>{"name":"&lt;set&gt;:&lt;property&gt;","value":"&lt;text&gt;"}</c>. <see cref="EvidenceReader.Texts"/>
/// reads every value as a <c>value</c> text tagged with its <c>set:property</c> name (the name is never text of its own),
/// so a value is the object's own statement and a purely numeric value stays a value (the catalog bridge never makes it a
/// subject). <c>:</c> is AEC's own scope separator (<c>[Set:Property]</c>); a <c>/</c> would read as a path to the
/// assistant's private-text guard. Pure and host-free: the plugin reads, this decides the contract value.
/// <list type="bullet">
/// <item>No set on the entity is <c>absent</c>; a set or property that could not be read makes the value
/// <c>unavailable:pset-read-incomplete</c> — never absent and never a complete read of what was left.</item>
/// <item>Null or blank values say nothing and are left out. A set without a readable name, or a property without one,
/// is a read that failed.</item>
/// <item>Derived properties (automatic, formula, field, increment, linked) are deliberately not read: they restate other
/// data. A set that has one is partial, <c>truncated:&lt;values + derived&gt;</c> — usable, never complete, never absent.</item>
/// <item>Ordered by set name, then property name, then value (ordinal), so the same sets give the same value.</item>
/// <item>At most <see cref="MaxSets"/> sets, <see cref="MaxPropertiesPerSet"/> values per set and <see cref="MaxValues"/>
/// values in all; beyond a cap, or where a name or value was cut to its limit (<c>name_cut</c>/<c>value_cut</c>), the status
/// is <c>truncated:&lt;values observed&gt;</c>: usable, never complete.</item>
/// </list>
/// </summary>
public static class PropertySetEvidence
{
    public const int MaxSets = 8;
    public const int MaxPropertiesPerSet = 32;
    /// <summary>
    /// Values kept for one record. <see cref="EvidenceReader.Texts"/> reads at most 64 text leaves of one value: a longer
    /// list would be read in part while its status still claimed the whole.
    /// </summary>
    public const int MaxValues = 64;
    public const int MaxNameChars = 60;
    public const int MaxValueChars = 80;
    public const string NameSeparator = ":";
    public const string ReadIncomplete = "pset-read-incomplete";

    /// <summary>
    /// The opening of a template value an exporter writes before anyone fills it ("Pay item for asphalt" in Civil's
    /// corridor-solid PropertySets): a placeholder, not a statement. Written with "placeholder": true, which
    /// <see cref="EvidenceReader"/> skips, so it is never evidence, a classifier signal or a catalog subject; the real
    /// values beside it still are.
    /// </summary>
    public const string PlaceholderPrefix = "Pay item for";

    public static bool IsPlaceholder(string? value) =>
        value != null && value.TrimStart().StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A yes/no value (<c>true</c>/<c>false</c>, as <see cref="TryValueText"/> writes one): it says whether, never what the object
    /// is, so it is never a subject (an AI request's admission, a catalog subject). It stays readable evidence.
    /// </summary>
    public static bool IsYesNo(string? value) =>
        value?.Trim() is { } text &&
        (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase));

    /// <summary>One PropertySet as read: its definition name, its (property name, value text) pairs, how many of its
    /// properties could not be read and how many were derived and deliberately not read.</summary>
    public sealed record Set(string? DefinitionName, IReadOnlyList<(string? Name, string? Value)> Properties,
        int UnreadProperties = 0, int DerivedProperties = 0);

    private readonly record struct Item(string Name, string Value, bool NameCut, bool ValueCut);

    /// <summary>
    /// Invariant text of a manual property value: text as is, yes/no as <c>true</c>/<c>false</c>, integers exactly and
    /// reals rounded like every evidence number, in fixed-point notation (never <c>5E-05</c>: an exponent would no longer
    /// read as a number, and could become a catalog subject). A null value is read and says nothing
    /// (<paramref name="text"/> null). Any other value (a non-finite real, an unknown type) is not read: false.
    /// </summary>
    public static bool TryValueText(object? raw, out string? text)
    {
        text = raw switch
        {
            null => null,
            string value => value,
            bool flag => flag ? "true" : "false",
            double number when double.IsFinite(number) => FixedText(number),
            float number when float.IsFinite(number) => FixedText(number),
            sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
                Convert.ToString(raw, CultureInfo.InvariantCulture),
            _ => null,
        };
        return raw == null || text != null;
    }

    /// <summary>
    /// The contract value of an entity's PropertySets. <paramref name="unreadSets"/>: sets that were found on the entity
    /// but could not be read (or were not its own).
    /// </summary>
    public static EvidenceValue Build(IReadOnlyList<Set> sets, int unreadSets = 0)
    {
        ArgumentNullException.ThrowIfNull(sets);
        // What could not be read may say something else: never absent and never a complete read.
        if (unreadSets > 0) return EvidenceValue.Unavailable(ReadIncomplete);
        if (sets.Count == 0) return EvidenceValue.Absent;

        var read = new List<(string Name, bool NameCut, List<Item> Items)>(sets.Count);
        var derived = 0;
        foreach (var set in sets)
        {
            if (set == null || set.Properties == null || set.UnreadProperties > 0) return EvidenceValue.Unavailable(ReadIncomplete);
            var setName = EvidenceJson.Text(set.DefinitionName, MaxNameChars);
            if (setName == null) return EvidenceValue.Unavailable(ReadIncomplete);
            if (set.DerivedProperties > 0) derived += set.DerivedProperties;
            var items = new List<Item>();
            foreach (var (name, value) in set.Properties)
            {
                var propertyName = EvidenceJson.Text(name, MaxNameChars);
                if (propertyName == null) return EvidenceValue.Unavailable(ReadIncomplete);
                var text = EvidenceJson.Text(value, MaxValueChars);
                if (text == null) continue;
                items.Add(new Item(propertyName, text, Cut(name, MaxNameChars), Cut(value, MaxValueChars)));
            }
            if (items.Count == 0) continue;
            items.Sort((left, right) =>
            {
                var byName = string.CompareOrdinal(left.Name, right.Name);
                return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
            });
            read.Add((setName, Cut(set.DefinitionName, MaxNameChars), items));
        }

        // Two sets of one definition are told apart by their own values, so the order never depends on the host's.
        var ordered = read
            .OrderBy(set => set.Name, StringComparer.Ordinal)
            .ThenBy(set => string.Join("\u001e", set.Items.Select(item => item.Name + "\u001f" + item.Value)), StringComparer.Ordinal)
            .ToList();
        var total = ordered.Sum(set => set.Items.Count);
        var kept = new List<(string Tag, string Value, bool NameCut, bool ValueCut)>();
        foreach (var (setName, setNameCut, items) in ordered.Take(MaxSets))
            foreach (var item in items.Take(MaxPropertiesPerSet))
            {
                if (kept.Count >= MaxValues) break;
                kept.Add((setName + NameSeparator + item.Name, item.Value, setNameCut || item.NameCut, item.ValueCut));
            }

        var json = EvidenceJson.Build(writer =>
        {
            writer.WriteStartArray();
            foreach (var (tag, value, nameCut, valueCut) in kept)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tag);
                writer.WriteString("value", value);
                if (IsPlaceholder(value)) writer.WriteBoolean("placeholder", true);
                if (nameCut) writer.WriteBoolean("name_cut", true);
                if (valueCut) writer.WriteBoolean("value_cut", true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        // A capped list, a name or value cut to its limit, or a derived property left unread is usable but incomplete:
        // the total counts what was observed on the object, read or not.
        return kept.Count < total || derived > 0 || kept.Any(entry => entry.NameCut || entry.ValueCut)
            ? EvidenceValue.Truncated(json, total + derived)
            : EvidenceValue.Read(json);
    }

    private static bool Cut(string? raw, int maxChars) =>
        EvidenceReader.Clean(raw) is { } clean && clean.Length > maxChars;

    // EvidenceJson.Round (6 decimals, no negative zero), written without an exponent.
    private static string FixedText(double value) =>
        EvidenceJson.Round(value).ToString("0.######", CultureInfo.InvariantCulture);
}
