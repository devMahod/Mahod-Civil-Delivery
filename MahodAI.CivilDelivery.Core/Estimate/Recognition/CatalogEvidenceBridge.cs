using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>
/// Carries the readable CAD evidence that recognition already collected into the existing catalog-candidate search
/// (<see cref="MappingProposalEngine.Propose"/>), so a group on a randomly named layer whose objects say what they are
/// (an attribute, a dynamic-block property, a PropertySet component, a legend row) is offered existing catalog items
/// instead of a manual search. Pure, host-free and offline: it never approves, maps, prices or sends anything.
/// <list type="bullet">
/// <item>Search subjects are object-bound evidence only, and only a text that is complete (<c>read</c>, not cut) and
/// identical on every record of the group, cited by key, field and record count. Nearby text is a spatial candidate,
/// not the object's own statement, and never a subject here.</item>
/// <item>Where local recognition found the group's evidence contradicting — channels naming different families, or one
/// text naming two subjects — the contradiction is returned and automatic candidates are withheld: a CAD name elsewhere
/// on the object does not outvote it.</item>
/// <item>Where local recognition proposed one family for every record, that family's library items are returned as
/// candidate codes with the cited evidence keys. They are still only proposals.</item>
/// </list>
/// </summary>
public static class CatalogEvidenceBridge
{
    /// <summary>Evidence that is a statement of the object itself about what it is.</summary>
    public static IReadOnlyList<string> SubjectKeys { get; } = new[]
    {
        EvidenceKeys.BlockAttributes, EvidenceKeys.BlockProps, EvidenceKeys.PsetComponent, EvidenceKeys.LegendRow,
    };

    public const int MaxSubjects = 8;
    public const int MaxContradictions = 4;

    /// <summary>One identical, complete text of an object-bound evidence key on every record of the group.</summary>
    public sealed record Subject(string Key, string Field, string? Tag, string Value, int RecordCount)
    {
        /// <summary>A short Hebrew label of where the text came from, for the engineer.</summary>
        public string Label => Key switch
        {
            _ when Key == EvidenceKeys.BlockAttributes => "תכונת בלוק" + (Tag is { Length: > 0 } ? " " + Tag : string.Empty),
            _ when Key == EvidenceKeys.BlockProps => "מאפיין בלוק דינמי" + (Tag is { Length: > 0 } ? " " + Tag : string.Empty),
            _ when Key == EvidenceKeys.PsetComponent => "PropertySet" + (Tag is { Length: > 0 } ? " " + Tag : string.Empty),
            _ when Key == EvidenceKeys.LegendRow => "שורת מקרא",
            _ => Key,
        };

        public string Citation => $"{Label}='{Value}' ({Key}, זהה בכל {RecordCount} העצמים)";
    }

    /// <summary>The bridge's answer for one measured group.</summary>
    public sealed record Evidence(
        IReadOnlyList<Subject> Subjects,
        IReadOnlyList<string> Contradictions,
        string? RecognisedFamily,
        IReadOnlyList<string> FamilyCandidateCodes,
        IReadOnlyList<string> FamilyEvidenceKeys)
    {
        public static Evidence None { get; } = new(Array.Empty<Subject>(), Array.Empty<string>(), null,
            Array.Empty<string>(), Array.Empty<string>());

        public bool Contradicted => Contradictions.Count > 0;

        /// <summary>
        /// Why a family local recognition proposed for every record does not supply candidate codes (its cited evidence
        /// was cut on some record, so the unanimity is over a partial list); null when it does or when there is none.
        /// </summary>
        public string? FamilyWithheld { get; init; }

        /// <summary>Validated saved meaning, not catalog-item approval.</summary>
        public IReadOnlyList<string> ConfirmedFamilyDecisionIds { get; init; } = Array.Empty<string>();

        /// <summary>Current decision dispositions, including stale/uncovered partitions.</summary>
        public IReadOnlyList<FamilyResolution> FamilyDecisionResolutions { get; init; } = Array.Empty<FamilyResolution>();
    }

    /// <summary>A detached, read-only snapshot for a review/request; callers cannot mutate its lists while AI is in flight.</summary>
    public static Evidence? Snapshot(Evidence? evidence) => evidence == null ? null : evidence with
    {
        Subjects = Array.AsReadOnly(evidence.Subjects.Select(subject => subject with { }).ToArray()),
        Contradictions = Array.AsReadOnly(evidence.Contradictions.ToArray()),
        FamilyCandidateCodes = Array.AsReadOnly(evidence.FamilyCandidateCodes.ToArray()),
        FamilyEvidenceKeys = Array.AsReadOnly(evidence.FamilyEvidenceKeys.ToArray()),
        ConfirmedFamilyDecisionIds = Array.AsReadOnly(evidence.ConfirmedFamilyDecisionIds.ToArray()),
        FamilyDecisionResolutions = Array.AsReadOnly(evidence.FamilyDecisionResolutions.ToArray()),
    };

    private static readonly Regex NumericOnly = new(@"\A[\d\s.,:;+\-/*x×%()\[\]'""°]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolve saved decisions once against the COMPLETE scan before filtering mapped records. A literal approval
    /// must never be revalidated against a trimmed group; semantic partitions retain their original validation scope.
    /// Only a single currently applied family covering every record can supply group-wide item candidates.
    /// </summary>
    public static IReadOnlyDictionary<string, Evidence> ForProposalGroups(
        IReadOnlyList<NeutralQuantityRecord> completeScan, EngineerBoqLibrary library,
        IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? decisions = null)
    {
        ArgumentNullException.ThrowIfNull(completeScan);
        ArgumentNullException.ThrowIfNull(library);
        var partitions = decisions is { Count: > 0 }
            ? FamilyDecisionPolicy.ResolvePartitions(decisions,
                EngineerBoqDraftBuilder.RecognitionGroups(completeScan, library), library)
            : Array.Empty<FamilyDecisionPolicy.PartitionResolution>();
        var byRecord = partitions.SelectMany(p => p.Group.Records.Select(r => (r.RecordId, Partition: p)))
            .ToDictionary(x => x.RecordId, x => x.Partition, StringComparer.Ordinal);
        return completeScan.Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))
            .GroupBy(r => r.Classification.RuleKey ?? "(none)", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var records = g.ToList();
                var evidence = For(records, library);
                if (partitions.Count == 0) return evidence;
                var covered = records.Where(r => byRecord.ContainsKey(r.RecordId))
                    .Select(r => byRecord[r.RecordId]).Distinct().ToList();
                var resolutions = covered.Select(p => p.Resolution).ToArray();
                evidence = evidence with { FamilyDecisionResolutions = Array.AsReadOnly(resolutions) };
                // A mixed/partially approved group stays mixed. Never expand a partition approval over its siblings.
                if (evidence.Contradicted || records.Any(r => !byRecord.ContainsKey(r.RecordId)) ||
                    resolutions.Length == 0 || resolutions.Any(r => r.State != FamilyDecisionState.Applied || r.FamilyId == null) ||
                    resolutions.Select(r => r.FamilyId).Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    var stale = resolutions.Where(r => r.State == FamilyDecisionState.Stale)
                        .Select(r => $"{r.DecisionId}: {r.StaleReason}").Distinct(StringComparer.Ordinal).ToArray();
                    return stale.Length == 0 ? evidence : evidence with
                    {
                        FamilyWithheld = "החלטת משפחה שמורה אינה תקפה לראיות הנוכחיות ולא שימשה להצעה: " + string.Join("; ", stale),
                    };
                }
                var family = resolutions[0].FamilyId!;
                var rule = library.Rules.SingleOrDefault(r => r.Id == family);
                if (rule == null) return evidence;
                var ids = resolutions.Select(r => r.DecisionId!).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var keys = (decisions ?? Array.Empty<ProjectProfile.EstimateProfile.FamilyDecision>())
                    .Where(d => ids.Contains(d.DecisionId, StringComparer.OrdinalIgnoreCase))
                    .SelectMany(d => d.EvidenceKeys ?? new List<string>()).Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal).ToArray();
                return evidence with
                {
                    RecognisedFamily = family,
                    FamilyCandidateCodes = Array.AsReadOnly(rule.Emits.Select(e => e.Code)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                    FamilyEvidenceKeys = Array.AsReadOnly(keys),
                    ConfirmedFamilyDecisionIds = Array.AsReadOnly(ids),
                    FamilyWithheld = null,
                };
            }, StringComparer.Ordinal);
    }


    /// <summary>
    /// The bridge evidence for the records of one catalog-proposal group (one rule key). Recognition runs per
    /// recognition group (source, layer, kind, unit, method, block) with <paramref name="classifier"/> (the local one
    /// by default) and no catalog: the family's library items are filtered by the catalog in the proposal engine.
    /// </summary>
    public static Evidence For(IReadOnlyList<NeutralQuantityRecord> records, EngineerBoqLibrary library,
        IFamilyClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(library);
        if (records.Count == 0) return Evidence.None;
        var subjects = UniformSubjects(records);

        classifier ??= LocalFamilyClassifier.Instance;
        var proposals = EngineerBoqDraftBuilder.RecognitionGroups(records, library)
            .SelectMany(group => classifier.Classify(group, library, null)).ToList();
        var contradictions = proposals
            .Where(p => p.EvidenceContradicts)
            .SelectMany(p => p.MissingDetails.Take(2))
            .Distinct(StringComparer.Ordinal).Take(MaxContradictions).ToList();

        string? family = null;
        IReadOnlyList<string> codes = Array.Empty<string>();
        IReadOnlyList<string> keys = Array.Empty<string>();
        var covered = proposals.SelectMany(p => p.RecordIds).ToHashSet(StringComparer.Ordinal);
        var families = proposals.Select(p => p.Status == RecognitionStatus.Proposed ? p.FamilyId : null)
            .Distinct(StringComparer.Ordinal).ToList();
        string? withheld = null;
        // One family for every record of the group, or none: a split group is not one subject.
        if (contradictions.Count == 0 && families.Count == 1 && families[0] is { } only &&
            records.All(r => covered.Contains(r.RecordId)))
        {
            var byId = records.GroupBy(r => r.RecordId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            // Cited evidence cut on any cited record: the proposal rests on a partial list, so it gives no automatic
            // candidate (the text that was cut may say something else). Unreadable evidence never reaches a proposal.
            var cut = proposals.SelectMany(p => p.EvidenceRefs)
                .Where(reference => reference.RecordIds.Any(id => byId.TryGetValue(id, out var record) &&
                    EvidenceReader.Status(Parameters(record), reference.Key).State == EvidenceState.Truncated))
                .Select(reference => reference.Key).Distinct(StringComparer.Ordinal).ToList();
            family = only;
            keys = proposals.SelectMany(p => p.EvidenceRefs.Select(r => r.Key)).Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (cut.Count == 0)
                codes = proposals.SelectMany(p => p.CandidateCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            else
                withheld = $"הראיה שעליה נשענת המשפחה '{only}' קטועה ({string.Join(", ", cut)}) — פריטי המשפחה לא הוצעו אוטומטית.";
        }
        return new Evidence(subjects, contradictions, family, codes, keys) { FamilyWithheld = withheld };
    }

    /// <summary>
    /// Texts of the object-bound keys that are complete and identical on every record. A key cut on any record is
    /// skipped (the text that differs may be the one cut); a field with two different texts on one record is not one
    /// subject; purely numeric values (a distance, a number) say nothing about what the object is.
    /// </summary>
    public static IReadOnlyList<Subject> UniformSubjects(IReadOnlyList<NeutralQuantityRecord> records)
    {
        var result = new List<Subject>();
        if (records.Count == 0) return result;
        // The same contract the classifier reads under: a record whose ev_* follow an unknown or unread schema has no
        // readable object evidence, so no text is identical "on every record".
        if (!records.All(r => EvidenceReader.SchemaReadable(Parameters(r)))) return result;
        foreach (var key in SubjectKeys)
        {
            if (!records.All(r => EvidenceReader.Status(Parameters(r), key).State == EvidenceState.Read)) continue;
            Dictionary<(string Field, string Tag), string>? common = null;
            foreach (var record in records)
            {
                var texts = SubjectTexts(Parameters(record), key)
                    .GroupBy(t => (t.Field, Tag: t.Tag ?? string.Empty))
                    .Where(g => g.Select(t => t.Text).Distinct(StringComparer.Ordinal).Count() == 1)
                    .ToDictionary(g => g.Key, g => g.First().Text);
                if (common == null)
                    common = texts;
                else
                    foreach (var field in common.Keys.ToList())
                        if (!texts.TryGetValue(field, out var text) || !string.Equals(text, common[field], StringComparison.Ordinal))
                            common.Remove(field);
                if (common.Count == 0) break;
            }
            if (common == null) continue;
            foreach (var ((field, tag), text) in common
                         .OrderBy(pair => pair.Key.Field, StringComparer.Ordinal)
                         .ThenBy(pair => pair.Key.Tag, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(text) || NumericOnly.IsMatch(text) ||
                    (key == EvidenceKeys.PsetComponent && global::MahodAI.CivilDelivery.Estimate.Evidence.PropertySetEvidence.IsYesNo(text))) continue;
                result.Add(new Subject(key, field, tag.Length == 0 ? null : tag, text, records.Count));
                if (result.Count >= MaxSubjects) return result;
            }
        }
        return result;
    }

    /// <summary>
    /// The texts of one key that can be a subject. A legend row is one only when it was matched by shape (line type,
    /// pattern or block) in a legend found by name: a colour association or a guessed legend only narrows.
    /// </summary>
    private static IReadOnlyList<EvidenceText> SubjectTexts(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (key != EvidenceKeys.LegendRow) return EvidenceReader.Texts(parameters, key);
        if (!EvidenceReader.TryObservation(parameters, key, out var observation)) return Array.Empty<EvidenceText>();
        var rows = observation.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   observation.TryGetProperty("rows", out var list) ? list : observation;
        if (rows.ValueKind != System.Text.Json.JsonValueKind.Array) return Array.Empty<EvidenceText>();
        var result = new List<EvidenceText>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !row.TryGetProperty("match", out var match) || match.ValueKind != System.Text.Json.JsonValueKind.String ||
                match.GetString() is not ("linetype" or "pattern" or "block") ||
                !row.TryGetProperty("text", out var text) || text.ValueKind != System.Text.Json.JsonValueKind.String ||
                EvidenceReader.Clean(text.GetString()) is not { } clean)
                continue;
            result.Add(new EvidenceText(key, "text", clean, null));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> Parameters(NeutralQuantityRecord record) =>
        (IReadOnlyDictionary<string, string>?)record.Measurement?.Parameters ?? new Dictionary<string, string>();
}
