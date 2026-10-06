using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.EngineerDraft;

/// <summary>What the active library says about an unmapped proposal group.</summary>
public enum LibraryProposalState
{
    /// <summary>No library rule without an item covers the group: the existing proposal path applies.</summary>
    Open,
    /// <summary>Every record is a library element that is an engineering decision with no item (trees, fences, utilities…).</summary>
    NoItemDecision,
    /// <summary>Only part of the group is such an element, or it also matches a rule that has items: a visible review.</summary>
    Mixed,
}

public sealed record LibraryProposalDisposition(string RuleKey, LibraryProposalState State, IReadOnlyList<string> Elements, string Message);

/// <summary>
/// b19 (live b18 finding 2, Codex 03:31 / 03:59): a group the ACTIVE library maps to a rule that is a decision with no items
/// (Confidence Decision, no Emits) gets a visible review and no automatic item proposal — not the heuristic, not the
/// curated rules, not the assistant's Top-3, not a batch of older proposals. Applicability is read from the engineer draft
/// built with the same library and records: only measured groups the draft places in a library element count (so layer,
/// block, basis, source role, primary model and family decisions mean what they mean in the draft), and on each such group
/// BOTH kinds of rule are tested — the element's own and every other rule — so a no-item rule that matches the same group as
/// a rule with items is a mixed review whatever the rule order (never the builder's first match). The decision is taken
/// over the whole case-insensitive closure of a key and given to every exact key in it. Manual mapping with all its guards
/// stays available; approved mappings, measurements and prices are not touched.
/// </summary>
public static class LibraryProposalGate
{
    public const string NoneKey = "(none)";

    private enum Applicability { Other, Held, Overlap }

    public static IReadOnlyDictionary<string, LibraryProposalDisposition> Evaluate(EngineerBoqDraft draft, IReadOnlyList<NeutralQuantityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(records);
        static bool NoItem(DraftRule rule) => rule.Emits.Count == 0 && rule.Confidence == DraftConfidence.Decision;
        // Every measured group the draft placed in a library element: which no-item elements apply to it and whether a rule
        // with items applies too — the element's own rule and every other library rule, independent of rule order.
        var applicability = new Dictionary<string, (Applicability State, string? Element)>(StringComparer.Ordinal);
        foreach (var element in draft.Elements)
            foreach (var source in element.Sources)
            {
                var group = source.Group;
                var noItemRules = draft.Library.Rules.Where(r => NoItem(r) && EngineerBoqDraftBuilder.MatchesAndAccepts(r, group)).ToList();
                var ownNoItem = NoItem(element.Rule);
                var held = ownNoItem || noItemRules.Count > 0;
                var withItems = !ownNoItem || draft.Library.Rules.Any(r => r.Emits.Count > 0 && EngineerBoqDraftBuilder.MatchesAndAccepts(r, group));
                var name = ownNoItem ? element.Rule.DisplayName : noItemRules.FirstOrDefault()?.DisplayName;
                applicability[group.GroupId] = !held ? (Applicability.Other, null)
                    : withItems ? (Applicability.Overlap, name) : (Applicability.Held, name);
            }

        var result = new Dictionary<string, LibraryProposalDisposition>(StringComparer.Ordinal);
        var unmapped = records.Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode)).ToList();
        foreach (var closure in unmapped.GroupBy(r => r.Classification.RuleKey ?? NoneKey, StringComparer.OrdinalIgnoreCase))
        {
            int held = 0, overlap = 0, other = 0;
            var elements = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var record in closure)
            {
                var state = draft.RecordGroupIds.TryGetValue(record.RecordId, out var groupId) && applicability.TryGetValue(groupId, out var a)
                    ? a : (Applicability.Other, null);
                if (state.Item2 is { } element) elements.Add(element);
                switch (state.Item1)
                {
                    case Applicability.Held: held++; break;
                    case Applicability.Overlap: overlap++; break;
                    default: other++; break;
                }
            }
            if (held + overlap == 0) continue;
            var total = closure.Count();
            var keys = closure.Select(r => r.Classification.RuleKey ?? NoneKey).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var names = string.Join(", ", elements.Select(Bidi.Ltr));
            var library = draft.Library.Texts.LibraryName;
            var scope = keys.Count > 1 ? $"בקבוצה (כולל {keys.Count} מפתחות שנבדלים רק באותיות גדולות/קטנות) " : "בקבוצה ";
            var disposition = overlap == 0 && other == 0
                ? (LibraryProposalState.NoItemDecision,
                    $"ב{library} הקבוצה היא רכיב להחלטה הנדסית ללא סעיף ({names}) — לא מוצע סעיף אוטומטית. " +
                    "אפשר לבחור סעיף ידנית אחרי החלטה ('בחר סעיף').")
                : (LibraryProposalState.Mixed,
                    scope + $"{held + overlap} מתוך {total} עצמים שייכים ב{library} לרכיב להחלטה ללא סעיף ({names})" +
                    (overlap > 0 ? $", ו-{overlap} מהם תואמים גם כלל עם סעיפים" : string.Empty) +
                    " — קבוצה מעורבת; לא מוצע סעיף אוטומטית. לבדוק את הקבוצה ולשייך ידנית.");
            foreach (var key in keys)
                result[key] = new LibraryProposalDisposition(key, disposition.Item1, elements.ToList(), disposition.Item2);
        }
        return result;
    }

    /// <summary>Why no automatic proposal may be made for <paramref name="ruleKey"/> and every key equal to it ignoring case; null when open.</summary>
    public static string? Refusal(IReadOnlyDictionary<string, LibraryProposalDisposition> dispositions, string ruleKey)
    {
        ArgumentNullException.ThrowIfNull(dispositions);
        return dispositions.Values.FirstOrDefault(d => string.Equals(d.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase))?.Message;
    }
}
