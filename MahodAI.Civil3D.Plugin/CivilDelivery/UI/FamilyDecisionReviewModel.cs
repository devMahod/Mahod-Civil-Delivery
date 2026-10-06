using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using SavedDecisionEntry = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;
using PartitionEvidenceMatch = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyEvidenceMatch;
using Bidi = MahodAI.CivilDelivery.Shared.Bidi;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    public sealed record FamilyOption(string FamilyId, string Label);

    /// <summary>
    /// One family decision to save: the chosen family and the checked groups that share it, the same cited evidence keys
    /// and the same provenance (kept proposal, assistant suggestion or manual choice). A <see cref="Partition"/> batch
    /// approves one locally proven partition of a mixed measured group (its <see cref="Groups"/> holds only that partition,
    /// for display); the service rebuilds the whole group from the scan.
    /// </summary>
    public sealed record FamilyApprovalBatch(string FamilyId, IReadOnlyList<RecognitionGroupInput> Groups, IReadOnlyList<string> EvidenceKeys,
        bool OverridesProposal, bool FromAiSuggestion = false, FamilyPartitionSelection? Partition = null)
    {
        public FamilyVisualBinding? VisualBinding { get; init; }
    }

    /// <summary>The whole measured group (id and object count) and the exact records of one proven partition checked in it.</summary>
    public sealed record FamilyPartitionSelection(string WholeGroupId, IReadOnlyList<string> RecordIds, int WholeGroupRecords);

    /// <summary>What closing the family review asks the palette to do. One action per close.</summary>
    public enum FamilyReviewAction
    {
        None,
        ApproveFamilies,
        RevokeDecision,
    }

    /// <summary>
    /// One active family decision of the profile as the review shows it: the family, the groups of this scan it covers
    /// (applied or stale, with the reason), the evidence it cites, and who approved it when. <see cref="Coverage"/> says how
    /// many objects of its measured groups it applies to when that is only part of them (a partition rule, or a literal
    /// decision a later partition rule took part of).
    /// </summary>
    public sealed record SavedFamilyDecision(
        string DecisionId,
        string? FamilyId,
        string Family,
        int AppliedGroups,
        int StaleGroups,
        string State,
        string Layers,
        string Evidence,
        string ApprovedBy,
        DateTime? ApprovedAtUtc,
        string Reason,
        string? Coverage = null)
    {
        public string ShortId => DecisionId.Length > 10 ? DecisionId[..10] : DecisionId;

        public string Groups => (StaleGroups == 0 ? $"{AppliedGroups} בתוקף" : $"{AppliedGroups} בתוקף · {StaleGroups} לא בתוקף") +
                                (Coverage == null ? string.Empty : $" ({Coverage})");

        public string Approval => ApprovedAtUtc is { } at
            ? $"{ApprovedBy} · {at.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} UTC"
            : ApprovedBy;
    }

    /// <summary>
    /// One recognition proposal (a measured group, or the part of it one family was proposed for) in the family review.
    /// Nothing is chosen for the engineer: a row starts unchecked, and an abstained row starts without a family.
    /// A row that covers only part of its measured group can be checked only when it is one complete partition the local
    /// classifier proves in the whole group (<see cref="IsPartition"/>, previewed without creating a decision); it then keeps
    /// the proven family and shows the rule's scope (<see cref="ScopeText"/>). Any other part of a group is never approvable.
    /// </summary>
    public sealed partial class FamilyDecisionRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private FamilyOption? _chosenFamily;
        private RecognitionProposal? _chosenAiProposal;
        private string _aiStatus = string.Empty;

        /// <param name="group">The draft's recognition group of the proposal (one partition of the measured group when an evidence rule split it).</param>
        /// <param name="wholeGroup">The whole measured group of the scan the records belong to; <paramref name="group"/> when omitted.</param>
        /// <param name="decidedFamilyOfRecord">What the draft applies to each record a saved decision governs: the family id, or
        /// null for an exclusion.</param>
        internal FamilyDecisionRow(RecognitionGroupInput group, RecognitionProposal proposal, EngineerBoqLibrary library,
            RecognitionGroupInput? wholeGroup = null, IReadOnlyDictionary<string, string?>? decidedFamilyOfRecord = null)
        {
            var ids = proposal.RecordIds.ToHashSet(StringComparer.Ordinal);
            Group = group with { Records = group.Records.Where(r => ids.Contains(r.RecordId)).ToList() };
            Proposal = proposal;
            WholeGroup = wholeGroup ?? group;
            IsPartialGroup = Group.Records.Count != WholeGroup.Records.Count;
            string Label(string id) => library.Rules.FirstOrDefault(r => r.Id == id)?.Element ?? id;
            var proposed = proposal.Status == RecognitionStatus.Proposed ? proposal.FamilyId : null;
            var compatible = library.Rules
                .Where(r => EngineerBoqDraftBuilder.BasisAcceptsMeasurement(r.Basis, group.Kind, group.MethodClass))
                .Select(r => r.Id).ToList();
            var ordered = new[] { proposed }.Concat(proposal.Alternatives.Select(a => a.FamilyId))
                .Where(id => id != null && compatible.Contains(id)).Select(id => id!)
                .Concat(compatible.OrderBy(Label, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).ToList();
            FamilyOptions = ordered.Select(id => new FamilyOption(id, id == proposed ? $"{Label(id)} (מוצע)" : Label(id))).ToList();
            ProposedFamily = proposed == null ? null : FamilyOptions.FirstOrDefault(o => o.FamilyId == proposed);
            // The other records of the whole group that a saved decision already governs; this row's approval never changes them.
            var decidedOthers = WholeGroup.Records
                .Where(r => !ids.Contains(r.RecordId) && decidedFamilyOfRecord?.ContainsKey(r.RecordId) == true).ToList();
            if (IsPartialGroup && ProposedFamily != null)
            {
                // A pure preview on the whole measured group: no decision is created, nothing is written. The rest of the proven
                // partition may already be covered as the same family by an earlier decision.
                var coveredAsFamily = decidedOthers.Where(r => decidedFamilyOfRecord![r.RecordId] == ProposedFamily.FamilyId)
                    .Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal);
                try
                {
                    PartitionMatches = FamilyDecisionPolicy.EvidenceMatchesForPartition(WholeGroup, Group.Records.Select(r => r.RecordId).ToList(),
                        ProposedFamily.FamilyId, library, coveredAsFamily);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    PartitionMatches = null;
                }
            }
            if (IsPartition)
            {
                // The proven partition is approved as the proven family only.
                FamilyOptions = new[] { ProposedFamily! };
            }
            // A part of a group that is not a proven partition is never approvable, so nothing is shown as chosen for it.
            _chosenFamily = IsPartialGroup && !IsPartition ? null : ProposedFamily;
            BatchLabel = IsPartition
                ? $"תת־קבוצה מוכחת בשכבה מעורבת — מוצע: {Label(proposed!)}"
                : IsPartialGroup
                    ? "קבוצות מעורבות — לא ניתנות לאישור בחלון הזה"
                    : proposed == null ? "ללא הכרעה — לבחור משפחה ידנית" : $"מוצע: {Label(proposed)}";
            Source = EngineerBoqDraftBuilder.DisplaySource(group.Source);
            Layer = group.LayerLeaf;
            Kind = EngineerBoqDraftBuilder.KindLabel(group.Kind) + (group.Block == null ? string.Empty : $" · {group.Block}");
            ObjectCount = Group.Records.Count;
            Quantity = Group.Records.Sum(r => r.Measurement.RawValue).ToString("#,##0.##", CultureInfo.InvariantCulture) + " " + group.Unit;
            var part = $"{Group.Records.Count} מתוך {WholeGroup.Records.Count}";
            Evidence = (IsPartition ? $"תת־קבוצה מוכחת ({part} העצמים בקבוצה) · "
                           : IsPartialGroup ? "קבוצה מעורבת — החלק הזה אינו ניתן לאישור · " : string.Empty) +
                       string.Join(" · ", proposal.Observed);
            Interpretation = string.Join(" · ", proposal.Inferred.Concat(proposal.Alternatives
                .Select(a => $"חלופה: {Label(a.FamilyId)}" + (string.IsNullOrWhiteSpace(a.Why) ? string.Empty : $" ({a.Why})"))));
            var decided = proposed != null ? $"הצעה: {Label(proposed)}" : "ללא הכרעה";
            // Records a saved decision governs as something other than the proposed family (the same family is handled above).
            var otherDecisionCount = ProposedFamily == null ? 0
                : decidedOthers.Count(r => decidedFamilyOfRecord![r.RecordId] != ProposedFamily.FamilyId);
            var notProven = ProposedFamily == null
                ? "לחלק הזה אין הצעת משפחה מראיות השרטוט"
                : "הראיות המצוטטות אינן מבדילות בדיוק את העצמים האלה משאר הקבוצה, ולכן אין כאן תת־קבוצה מוכחת";
            Missing = IsPartialGroup && !IsPartition
                ? $"קבוצה מעורבת: השורה הזו ({decided}) חלה על {part} העצמים בקבוצה, ולשאר העצמים יש הכרעה אחרת. " +
                  $"אי אפשר לאשר כאן את החלק הזה: {notProven}, ולא נבחרה משפחה. " +
                  "לבדוק בשרטוט מה מבדיל בין העצמים; אחרי תיקון בשרטוט וסריקה חוזרת הקבוצה תוצג שוב לאישור." +
                  (otherDecisionCount > 0
                      ? $" {otherDecisionCount} מהעצמים האחרים בקבוצה מכוסים בהחלטה שמורה אחרת; אם לדעתך הם מאותה משפחה, " +
                        "אפשר לבטל אותה ב'החלטות שמורות' ולאשר מחדש."
                      : string.Empty) +
                  (proposal.MissingDetails.Any() ? " · " + string.Join(" · ", proposal.MissingDetails) : string.Empty)
                : string.Join(" · ", proposal.MissingDetails);
            // Latin names and key=text citations are anchored left-to-right, as everywhere in Hebrew text (Bidi.Ltr).
            ScopeText = IsPartition
                ? $"תחולת האישור: {Label(proposed!)} ל-{part} העצמים בשכבה {Bidi.Ltr(WholeGroup.LayerLeaf)} " +
                  $"(מקור: {Bidi.Ltr(EngineerBoqDraftBuilder.DisplaySource(WholeGroup.Source))}; {Kind}), לפי הראיות " +
                  string.Join("; ", PartitionMatches!.Select(m => Bidi.Ltr($"{m.Key}={m.Text}"))) + ". " +
                  "הכלל יחול גם על עצמים חדשים באותו מקור, באותה שכבה ובאותו בסיס מדידה שיישאו את אותן ראיות. " +
                  OthersText(WholeGroup.Records.Count - Group.Records.Count, decidedOthers.Count) +
                  "אישור המשפחה אינו מאשר סעיפים או מחירים."
                : string.Empty;
            Origin = proposal.Origin == RecognitionProposal.OriginAi ? "AI" : "ראיות CAD";
        }

        /// <summary>The rest of a partition's measured group: covered by an earlier decision (unchanged here) or still open.</summary>
        private static string OthersText(int others, int decided)
        {
            var open = others - decided;
            if (decided == 0) return $"{open} העצמים האחרים בקבוצה אינם מאושרים ונשארים לבדיקה. ";
            return $"מהעצמים האחרים בקבוצה: {decided} כבר מכוסים בהחלטה קודמת והאישור הזה אינו משנה אותם" +
                   (open > 0 ? $", ו-{open} אינם מאושרים ונשארים לבדיקה. " : "; אין עצמים אחרים שנשארים לבדיקה. ");
        }

        public RecognitionGroupInput Group { get; }
        public RecognitionProposal Proposal { get; }

        /// <summary>The whole measured group of the scan this row's records belong to.</summary>
        public RecognitionGroupInput WholeGroup { get; }

        public bool IsPartialGroup { get; }

        /// <summary>The cited evidence that singles out exactly this row's records in its whole group; null unless a proven partition.</summary>
        public IReadOnlyList<PartitionEvidenceMatch>? PartitionMatches { get; }

        /// <summary>Part of a mixed measured group that the local classifier proves as one complete partition: approvable as a rule.</summary>
        public bool IsPartition => IsPartialGroup && PartitionMatches is { Count: > 0 };

        /// <summary>What approving a partition row covers, shown before the approval; empty for other rows.</summary>
        public string ScopeText { get; }

        public IReadOnlyList<FamilyOption> FamilyOptions { get; }
        public FamilyOption? ProposedFamily { get; }
        public string BatchLabel { get; }
        public string Source { get; }
        public string Layer { get; }
        public string Kind { get; }
        public int ObjectCount { get; }
        public string Quantity { get; }
        public string Evidence { get; }
        public string Interpretation { get; }
        public string Missing { get; }
        public string Origin { get; }
        public bool CanSelect => (!IsPartialGroup || IsPartition) && FamilyOptions.Count > 0;

        /// <summary>The assistant is only asked about whole groups the local recognition could not decide.</summary>
        public bool CanAskAi => !IsPartialGroup && FamilyOptions.Count > 0 && ProposedFamily == null;

        /// <summary>The assistant's grounded suggestion for this group, if any. Shown, never chosen for the engineer.</summary>
        public RecognitionProposal? AiProposal { get; private set; }

        public string AiStatus
        {
            get => _aiStatus;
            private set { if (_aiStatus == value) return; _aiStatus = value; Changed(); }
        }

        public bool ChosenFromAi => _chosenAiProposal != null && ReferenceEquals(_chosenAiProposal, AiProposal) &&
                                    ChosenFamily != null && AiProposal?.FamilyId == ChosenFamily.FamilyId;

        /// <summary>
        /// Records the assistant's answer for this group. A proposal counts only when it covers exactly this group's records
        /// and names a family offered for the group; nothing is selected or checked here.
        /// </summary>
        internal void ApplyAssistant(IReadOnlyList<RecognitionProposal> results, Func<string, string> label)
        {
            var ids = Group.Records.Select(r => r.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var proposal = results.FirstOrDefault(p => p.GroupId == Group.GroupId);
            if (proposal == null) { AiStatus = "העוזר לא נדרש לקבוצה הזו."; return; }
            // A checked interpretation belongs to the exact proposal the engineer chose.
            // A fresh/abstained answer cannot turn it into an unchecked-provenance manual rule.
            // Unrelated manual choices remain untouched and never become AI choices by family-name equality.
            if (ChosenFromAi && !ReferenceEquals(AiProposal, proposal)) { IsSelected = false; ChosenFamily = null; }
            var covers = proposal.RecordIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ids, StringComparer.Ordinal);
            if (proposal.Status == RecognitionStatus.Proposed && proposal.FamilyId != null && covers &&
                FamilyOptions.Any(o => o.FamilyId == proposal.FamilyId))
            {
                AiProposal = proposal;
                var explanation = proposal.Inferred.FirstOrDefault(i => !i.StartsWith("ai_context", StringComparison.Ordinal) &&
                                                                        !i.StartsWith("library_hash", StringComparison.Ordinal) &&
                                                                        !i.StartsWith("rule_fingerprint", StringComparison.Ordinal)) ?? string.Empty;
                AiStatus = $"הצעת עוזר: {label(proposal.FamilyId)} — לפי {string.Join(", ", proposal.Observed.Take(2))}. {explanation}".Trim() +
                           " לבחור אותה ידנית ברשימה אם היא נכונה.";
            }
            else
            {
                AiProposal = null;
                // A rejected answer names only its fixed public class, never what the assistant wrote.
                var rejection = FamilyRecognitionAssist.PublicRejectionCode(proposal);
                AiStatus = "העוזר לא הציע משפחה: " + string.Join(" · ", proposal.MissingDetails.DefaultIfEmpty("אין פירוט")) +
                           (rejection == null ? string.Empty : " (קוד אבחון: " + rejection + ")");
            }
            Changed(nameof(ChosenFromAi));
        }
        public bool ChangedFromProposal => ChosenFamily != null && ProposedFamily != null && ChosenFamily.FamilyId != ProposedFamily.FamilyId;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                var next = value && CanSelect;
                if (_isSelected == next) return;
                _isSelected = next;
                Changed();
            }
        }

        public FamilyOption? ChosenFamily
        {
            get => _chosenFamily;
            set
            {
                if (value != null && !FamilyOptions.Contains(value)) return;
                if (value != null && IsPartialGroup && !IsPartition) return;
                var chosenAi = value != null && AiProposal?.FamilyId == value.FamilyId ? AiProposal : null;
                if (Equals(_chosenFamily, value) && ReferenceEquals(_chosenAiProposal, chosenAi)) return;
                _chosenFamily = value;
                _chosenAiProposal = chosenAi;
                Changed();
                Changed(nameof(ChangedFromProposal));
                Changed(nameof(ChosenFromAi));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// The family review of one scan: rows from the draft's recognition proposals, the stale decisions to re-approve,
    /// the profile's active decisions (with what they cover now, for review and revocation) and the batches to save.
    /// One saved decision covers every checked group of one family that shares the same cited evidence and provenance.
    /// </summary>
    public sealed class FamilyDecisionReviewModel
    {
        /// <summary>
        /// The CAD evidence that can say what a group is and is stable across rescans. A family the engineer picks by hand
        /// cites the keys of this list that are read on every record of the group, so a later change of that evidence makes
        /// the decision stale. Nearby text (a spatial candidate), geometry, transforms and colour are never cited this way.
        /// </summary>
        internal static readonly IReadOnlyList<string> ManualCitationKeys = new[]
        {
            EvidenceKeys.BlockAttributes, EvidenceKeys.BlockProps, EvidenceKeys.PsetComponent, EvidenceKeys.LegendRow,
            EvidenceKeys.Hatch, EvidenceKeys.BlockNameEffective,
        };

        private FamilyDecisionReviewModel(IReadOnlyList<FamilyDecisionRow> rows, IReadOnlyList<string> staleLines, int appliedGroups,
            IReadOnlyList<SavedFamilyDecision> savedDecisions, EngineerBoqLibrary library)
        {
            Library = library;
            Rows = rows;
            StaleLines = staleLines;
            AppliedGroups = appliedGroups;
            SavedDecisions = savedDecisions;
        }

        /// <summary>The library the draft behind this review was built with; the window and its save use this one.</summary>
        public EngineerBoqLibrary Library { get; }
        public IReadOnlyList<FamilyDecisionRow> Rows { get; }
        public IReadOnlyList<string> StaleLines { get; }
        public int AppliedGroups { get; }

        /// <summary>Active family decisions, decisions that no longer hold first. Only these can be revoked.</summary>
        public IReadOnlyList<SavedFamilyDecision> SavedDecisions { get; }

        /// <summary>
        /// Builds the review from a draft. <paramref name="savedDecisions"/> is the profile's decision list the draft was
        /// resolved against (estimate.family_decisions); without it only decisions that address a group of this scan are listed.
        /// </summary>
        public static FamilyDecisionReviewModel Create(EngineerBoqDraft draft, IReadOnlyList<SavedDecisionEntry>? savedDecisions = null)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var groups = draft.RecognitionGroups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
            // A draft group may be one partition of a measured group (an evidence rule split it): a row is whole, partial or a
            // proven partition relative to the whole measured group of the scan, never relative to such a partition.
            var wholeOfRecord = new Dictionary<string, RecognitionGroupInput>(StringComparer.Ordinal);
            foreach (var whole in draft.WholeRecognitionGroups.Count > 0 ? draft.WholeRecognitionGroups : draft.RecognitionGroups)
                foreach (var record in whole.Records) wholeOfRecord.TryAdd(record.RecordId, whole);
            RecognitionGroupInput WholeOf(RecognitionProposal proposal, RecognitionGroupInput group)
            {
                var wholes = proposal.RecordIds.Select(id => wholeOfRecord.TryGetValue(id, out var whole) ? whole : null)
                    .Distinct().ToList();
                return wholes.Count == 1 && wholes[0] != null ? wholes[0]! : group;
            }
            // What the draft applies to each record a saved decision governs (a family id, or null for an exclusion).
            var decidedFamilyOfRecord = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var resolution in draft.FamilyResolutions.Where(r => r.State == FamilyDecisionState.Applied))
                if (groups.TryGetValue(resolution.GroupId, out var decided))
                    foreach (var record in decided.Records) decidedFamilyOfRecord.TryAdd(record.RecordId, resolution.FamilyId);
            var rows = draft.RecognitionProposals
                .Where(p => groups.ContainsKey(p.GroupId))
                .Select(p => new FamilyDecisionRow(groups[p.GroupId], p, draft.Library, WholeOf(p, groups[p.GroupId]), decidedFamilyOfRecord))
                .OrderBy(r => r.IsPartition ? 1 : r.IsPartialGroup ? 3 : r.ProposedFamily == null ? 2 : 0)
                .ThenBy(r => r.BatchLabel, StringComparer.Ordinal)
                .ThenBy(r => r.Layer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Group.GroupId, StringComparer.Ordinal)
                .ToList();
            string Family(string? id) => draft.Library.Rules.FirstOrDefault(r => r.Id == id)?.Element ?? id ?? "?";
            var stale = draft.FamilyResolutions.Where(r => r.State == FamilyDecisionState.Stale)
                .GroupBy(r => (r.DecisionId, r.FamilyId, r.StaleReason))
                .Select(g => $"{Family(g.Key.FamilyId)}: {g.Count()} קבוצות — {StaleReason(g.Key.StaleReason)} (אושר ע\"י {g.First().ApprovedBy})")
                .ToList();
            var applied = draft.FamilyResolutions.Count(r => r.State == FamilyDecisionState.Applied);
            return new FamilyDecisionReviewModel(rows, stale, applied, SavedDecisionsOf(draft, savedDecisions, Family), draft.Library);
        }

        private static IReadOnlyList<SavedFamilyDecision> SavedDecisionsOf(EngineerBoqDraft draft,
            IReadOnlyList<SavedDecisionEntry>? decisions, Func<string?, string> family)
        {
            var layers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in draft.RecognitionGroups) layers[group.GroupId] = group.LayerLeaf;
            var byDecision = draft.FamilyResolutions
                .Where(r => r.State != FamilyDecisionState.NotCovered && !string.IsNullOrWhiteSpace(r.DecisionId))
                .GroupBy(r => r.DecisionId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<FamilyResolution>)g.ToList(), StringComparer.OrdinalIgnoreCase);
            // What the draft actually applies to each group: one resolution per group, including a conflict between
            // decisions (no decision id) and a later decision that took the group over.
            var effective = draft.FamilyResolutions
                .Where(r => r.State != FamilyDecisionState.NotCovered)
                .GroupBy(r => r.GroupId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            // When an evidence rule split measured groups, each draft group is one partition of a whole group. A literal
            // decision was approved and fingerprinted on the whole group, so it is judged there (as the draft does through
            // ValidationScope); an evidence rule is judged on the partition itself.
            var wholeOf = draft.FamilyDecisionPartitions
                .GroupBy(p => p.Group.GroupId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().WholeGroup, StringComparer.Ordinal);
            var recordCount = draft.RecognitionGroups.GroupBy(g => g.GroupId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Records.Count, StringComparer.Ordinal);

            var result = new List<SavedFamilyDecision>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decision in decisions ?? Array.Empty<SavedDecisionEntry>())
            {
                if (decision == null || string.IsNullOrWhiteSpace(decision.DecisionId) ||
                    !string.Equals(decision.Status?.Trim(), FamilyDecisionPolicy.Active, StringComparison.Ordinal))
                    continue;
                var id = decision.DecisionId.Trim();
                if (!listed.Add(id)) continue;
                var exclusion = string.Equals(decision.Decision?.Trim(), FamilyDecisionPolicy.Exclude, StringComparison.Ordinal);
                // The groups this decision addresses, found on its own (a newer decision does not hide them), each shown
                // with what the draft really does there: this decision applied or stopped (contradicting evidence, role,
                // an approved item), a conflict with another decision, or a later decision that governs the group now.
                var alone = Addressed(decision, draft, wholeOf)
                    .Where(r => r.State != FamilyDecisionState.NotCovered).ToList();
                IReadOnlyList<FamilyResolution> covered = alone.Select(r =>
                {
                    if (!effective.TryGetValue(r.GroupId, out var now)) return r;
                    if (string.Equals(now.DecisionId?.Trim(), id, StringComparison.OrdinalIgnoreCase)) return now;
                    return r with
                    {
                        State = FamilyDecisionState.Stale,
                        StaleReason = now.DecisionId == null ? now.StaleReason ?? FamilyDecisionPolicy.StaleConflict : StaleOverridden,
                    };
                }).ToList();
                result.Add(Saved(id, exclusion ? null : decision.FamilyId?.Trim(), covered, decision.EvidenceKeys,
                    decision.ApprovedBy, decision.ApprovedAtUtc, decision.Reason, layers, family,
                    Coverage(covered, wholeOf, recordCount)));
            }
            // A decision the draft resolved but the caller did not pass is still shown (without its reason and keys).
            foreach (var pair in byDecision)
            {
                if (!listed.Add(pair.Key)) continue;
                var first = pair.Value[0];
                result.Add(Saved(pair.Key, first.FamilyId, pair.Value, null, first.ApprovedBy, first.ApprovedAtUtc, null, layers, family,
                    Coverage(pair.Value, wholeOf, recordCount)));
            }
            return result
                .OrderBy(s => s.StaleGroups > 0 ? 0 : s.AppliedGroups > 0 ? 1 : 2)
                .ThenBy(s => s.Family, StringComparer.Ordinal)
                .ThenByDescending(s => s.ApprovedAtUtc ?? DateTime.MinValue)
                .ThenBy(s => s.DecisionId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// One decision resolved on its own over the draft's groups, keyed by the draft's group ids. Without partitions this is
        /// Resolve over the draft's groups. With them, a literal decision (no evidence selector) is resolved on the whole measured
        /// group of each partition, where it was approved; a decision with evidence selectors on the partition itself.
        /// </summary>
        private static IReadOnlyList<FamilyResolution> Addressed(SavedDecisionEntry decision, EngineerBoqDraft draft,
            IReadOnlyDictionary<string, RecognitionGroupInput> wholeOf)
        {
            var single = new[] { decision };
            var literal = decision.Selectors == null || decision.Selectors.All(s => s == null || (s.EvidenceMatch?.Count ?? 0) == 0);
            if (!literal || wholeOf.Count == 0)
                return FamilyDecisionPolicy.Resolve(single, draft.RecognitionGroups, draft.Library);
            var scopes = draft.RecognitionGroups.Select(g => wholeOf.TryGetValue(g.GroupId, out var whole) ? whole : g).ToList();
            return FamilyDecisionPolicy.Resolve(single, scopes, draft.Library)
                .Select((r, i) => r with { GroupId = draft.RecognitionGroups[i].GroupId }).ToList();
        }

        /// <summary>
        /// "N מתוך M עצמים" when the decision applies to only part of the measured groups it addresses (a partition rule, or a
        /// literal decision a later partition rule took part of); null when none of the groups it addresses is split into
        /// partitions (the text is then exactly as before partitions existed) or when it applies to all of them.
        /// </summary>
        private static string? Coverage(IReadOnlyList<FamilyResolution> covered, IReadOnlyDictionary<string, RecognitionGroupInput> wholeOf,
            IReadOnlyDictionary<string, int> recordCount)
        {
            if (wholeOf.Count == 0 || covered.Count == 0) return null;
            // An evidence rule anywhere in the profile builds partitions for every group, most of them unsplit (the partition is
            // the whole group). Only a split group can give a decision partial coverage.
            if (!covered.Any(r => wholeOf.TryGetValue(r.GroupId, out var scope) &&
                                  !string.Equals(scope.GroupId, r.GroupId, StringComparison.Ordinal)))
                return null;
            var applied = covered.Where(r => r.State == FamilyDecisionState.Applied)
                .Sum(r => recordCount.TryGetValue(r.GroupId, out var count) ? count : 0);
            var total = covered.Select(r => wholeOf.TryGetValue(r.GroupId, out var whole) ? whole : null)
                .Where(whole => whole != null).Select(whole => whole!).DistinctBy(whole => whole.GroupId, StringComparer.Ordinal)
                .Sum(whole => whole.Records.Count);
            return total > 0 && applied < total ? $"{applied} מתוך {total} עצמים" : null;
        }

        private static SavedFamilyDecision Saved(string id, string? familyId, IReadOnlyList<FamilyResolution>? covered,
            IReadOnlyList<string>? evidenceKeys, string? approvedBy, DateTime? approvedAtUtc, string? reason,
            IReadOnlyDictionary<string, string> layers, Func<string?, string> family, string? coverage = null)
        {
            var resolutions = covered ?? Array.Empty<FamilyResolution>();
            var applied = resolutions.Count(r => r.State == FamilyDecisionState.Applied);
            var stale = resolutions.Where(r => r.State == FamilyDecisionState.Stale).ToList();
            var state = stale.Count > 0
                ? "לא בתוקף — " + string.Join(", ", stale.Select(r => StaleReason(r.StaleReason)).Distinct(StringComparer.Ordinal)) +
                  (applied > 0 ? $" (ב-{stale.Count} מתוך {resolutions.Count} קבוצות)" : string.Empty) + ". לאשר מחדש או לבטל."
                : applied > 0 ? "בתוקף" : "לא חלה על אף קבוצה בסריקה הזו";
            var layerNames = resolutions.Select(r => layers.TryGetValue(r.GroupId, out var layer) ? layer : r.GroupId)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            var layerText = layerNames.Count == 0
                ? "—"
                : string.Join(", ", layerNames.Take(4)) + (layerNames.Count > 4 ? $" ועוד {layerNames.Count - 4}" : string.Empty);
            var keys = evidenceKeys?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
            var evidence = keys == null ? "—" : keys.Count == 0 ? "ללא ראיות CAD — לפי מקור ושכבה בלבד" : string.Join(", ", keys);
            return new SavedFamilyDecision(
                id,
                familyId,
                familyId == null ? "החרגה — לא כמות בנייה" : family(familyId),
                applied,
                stale.Count,
                state,
                layerText,
                evidence,
                string.IsNullOrWhiteSpace(approvedBy) ? "?" : approvedBy.Trim(),
                approvedAtUtc is { } at ? FamilyDecisionPolicy.ToUtc(at) : (DateTime?)null,
                string.IsNullOrWhiteSpace(reason) ? "—" : reason.Trim(),
                coverage);
        }

        public string Summary
        {
            get
            {
                var whole = Rows.Where(r => !r.IsPartialGroup).ToList();
                var proposed = whole.Where(r => r.ProposedFamily != null).ToList();
                var partitions = Rows.Count(r => r.IsPartition);
                var mixedGroups = Rows.Where(r => r.IsPartialGroup && !r.IsPartition).Select(r => r.Group.GroupId)
                    .Distinct(StringComparer.Ordinal).Count();
                var groupCount = Rows.Select(r => r.Group.GroupId).Distinct(StringComparer.Ordinal).Count();
                return $"{groupCount} קבוצות שלא שויכו: {proposed.Count} עם הצעת משפחה " +
                       $"({proposed.Select(r => r.ProposedFamily!.FamilyId).Distinct(StringComparer.Ordinal).Count()} משפחות), " +
                       $"{whole.Count - proposed.Count} ללא הכרעה" +
                       (partitions > 0
                           ? $", {partitions} תת־קבוצות מוכחות בקבוצות מעורבות שאפשר לאשר (תחולת הכלל בפרטי השורה)"
                           : string.Empty) +
                       (mixedGroups > 0
                           ? $", {mixedGroups} קבוצות מעורבות שאי אפשר לאשר בחלון הזה (הסבר בפרטי השורה ובעמודה 'מה חסר להכרעה')"
                           : string.Empty) +
                       ". " +
                       (AppliedGroups > 0 ? $"{AppliedGroups} קבוצות כבר מכוסות בהחלטות שמורות. " : string.Empty) +
                       (StaleLines.Count > 0 ? $"{StaleLines.Count} החלטות שמורות אינן בתוקף ודורשות אישור מחדש. " : string.Empty) +
                       (SavedDecisions.Count > 0 ? $"לבדיקה ולביטול של {SavedDecisions.Count} ההחלטות השמורות: 'החלטות שמורות'." : string.Empty);
            }
        }

        /// <summary>
        /// Checks every whole-group proposal whose family is still the proposed one. A family the engineer chose (or a row without
        /// a chosen family) is never overwritten and never checked by this shortcut, and neither is a proven partition: its
        /// approval is a rule whose scope is shown per row, so it is checked explicitly.
        /// </summary>
        public void SelectAllProposed()
        {
            foreach (var row in Rows.Where(r => r.ProposedFamily != null && r.CanSelect && !r.IsPartition && !r.IsSelected &&
                                                r.ChosenFamily != null && r.ChosenFamily.FamilyId == r.ProposedFamily.FamilyId))
                row.IsSelected = true;
        }

        /// <summary>
        /// One batch per (chosen family, provenance, cited evidence keys). Evidence keys are the citations behind the chosen
        /// family: the local proposal's when the engineer kept it, the assistant's CAD citations read on the group when the
        /// engineer chose the assistant's family, and otherwise (a manual choice) the <see cref="ManualCitationKeys"/> read
        /// on every record of the group. A decision therefore fingerprints only the keys its own groups were approved on.
        /// Every checked proven partition is its own batch (one partition decision per whole group partition), after the others.
        /// </summary>
        public IReadOnlyList<FamilyApprovalBatch> SelectedBatches()
        {
            var checkedRows = Rows.Where(r => r.IsSelected && r.ChosenFamily != null && r.CanSelect).ToList();
            var batches = checkedRows.Where(r => !r.IsPartition)
                .Select(r => (Row: r, Provenance: Provenance(r), Visual: VisualBindingFor(r),
                    Keys: (IReadOnlyList<string>)CitedKeys(r).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList()))
                .GroupBy(x => (Family: x.Row.ChosenFamily!.FamilyId, x.Provenance, KeySet: string.Join("\u001f", x.Keys),
                    VisualGroup: x.Visual == null ? string.Empty : x.Row.Group.GroupId))
                .OrderBy(g => g.Key.Family, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Provenance)
                .ThenBy(g => g.Key.KeySet, StringComparer.Ordinal)
                .Select(g => new FamilyApprovalBatch(
                    g.Key.Family,
                    g.Select(x => x.Row.Group).ToList(),
                    g.First().Keys,
                    g.Key.Provenance != KeptProposal,
                    g.Key.Provenance == AssistantChoice) { VisualBinding = FamilyVisualBindingPolicy.Clone(g.First().Visual) })
                .ToList();
            batches.AddRange(checkedRows.Where(r => r.IsPartition)
                .OrderBy(r => r.WholeGroup.GroupId, StringComparer.Ordinal)
                .ThenBy(r => r.ChosenFamily!.FamilyId, StringComparer.Ordinal)
                .Select(r => new FamilyApprovalBatch(
                    r.ChosenFamily!.FamilyId,
                    new[] { r.Group },
                    CitedKeys(r).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                    false,
                    false,
                    new FamilyPartitionSelection(r.WholeGroup.GroupId,
                        r.Group.Records.Select(record => record.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                        r.WholeGroup.Records.Count))));
            return batches;
        }

        /// <summary>
        /// A caution shown before the attestation when proven partitions are checked: each is saved as a rule on its source and
        /// layer that also covers new objects carrying the same evidence, and the rest of its measured group stays unapproved.
        /// </summary>
        public string? PartitionNotice()
        {
            var partitions = Rows.Where(r => r.IsSelected && r.ChosenFamily != null && r.IsPartition).ToList();
            if (partitions.Count == 0) return null;
            return $"לתשומת לב: {partitions.Count} השורות המסומנות מסוג תת־קבוצה מוכחת יישמרו ככלל לפי ראיות במקור ובשכבה שלהן — " +
                   "הכלל יחול גם על עצמים חדשים שיישאו את אותן ראיות, ושאר העצמים בקבוצה נשארים לא מאושרים (פירוט בפרטי השורה).";
        }

        private const int KeptProposal = 0, ManualChoice = 1, AssistantChoice = 2;

        private static FamilyVisualBinding? VisualBindingFor(FamilyDecisionRow row) =>
            Provenance(row) == AssistantChoice ? row.AiProposal?.VisualBinding : null;

        private static int Provenance(FamilyDecisionRow row) =>
            row.ProposedFamily != null && !row.ChangedFromProposal ? KeptProposal : row.ChosenFromAi ? AssistantChoice : ManualChoice;

        private static IEnumerable<string> CitedKeys(FamilyDecisionRow row)
        {
            // A proven partition cites exactly the evidence that singles it out in its measured group.
            if (row.IsPartition) return row.PartitionMatches!.Select(m => m.Key!);
            switch (Provenance(row))
            {
                case KeptProposal:
                    return row.Proposal.EvidenceRefs.Select(e => e.Key);
                case AssistantChoice:
                    // Only evidence actually read on the group's records can bind the decision.
                    return row.AiProposal!.EvidenceRefs.Select(e => e.Key)
                        .Where(key => row.Group.Records.Any(r => EvidenceReader.Status(r.Measurement.Parameters, key).Usable));
                default:
                    // Cite only evidence that says the same thing on every record: a per-instance value (a dynamic-block
                    // distance, a numbered attribute) would make the decision stale whenever one instance changes.
                    return ManualCitationKeys.Where(key => row.Group.Records.Count > 0 &&
                        row.Group.Records.All(r => EvidenceReader.Status(r.Measurement.Parameters, key).Usable) &&
                        UniformEvidence(row.Group.Records, key));
            }
        }

        private static bool UniformEvidence(IReadOnlyList<NeutralQuantityRecord> records, string key)
        {
            string Canonical(NeutralQuantityRecord record) => string.Join("\u001e", EvidenceReader.Texts(record.Measurement.Parameters, key)
                .Select(t => string.Join("\u001f", t.Field, t.Tag ?? string.Empty, t.Text)).OrderBy(x => x, StringComparer.Ordinal));
            var first = Canonical(records[0]);
            return first.Length > 0 && records.All(r => string.Equals(Canonical(r), first, StringComparison.Ordinal));
        }

        /// <summary>
        /// A caution (not a block) when a checked group would be saved without any cited CAD evidence: such a decision binds
        /// to the source, layer and measurement only, so a change of the layer's content cannot make it stale.
        /// </summary>
        public string? CitationNotice()
        {
            var visualCount = Rows.Count(r => r.IsSelected && r.ChosenFamily != null && r.CanSelect && VisualBindingFor(r) != null);
            var visualNotice = visualCount == 0 ? string.Empty :
                $"{visualCount} קבוצות יאושרו לפי פירוש חזותי ובהיקף העצמים שנבדק בלבד. שינוי במקור, בעצמים או בגאומטריה יחייב בדיקה מחדש. ";
            var layers = Rows.Where(r => r.IsSelected && r.ChosenFamily != null && r.CanSelect && VisualBindingFor(r) == null && !CitedKeys(r).Any())
                .Select(r => r.Layer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (layers.Count == 0) return visualCount == 0 ? null : visualNotice;
            return visualNotice + $"לתשומת לב: ל-{layers.Count} קבוצות מסומנות ({string.Join(", ", layers.Take(3))}{(layers.Count > 3 ? " …" : string.Empty)}) " +
                   "אין ראיית CAD יציבה (בלוק, תכונות, PropertySet, מקרא או הצללה). ההחלטה תיקשר למקור, לשכבה ולסוג המדידה בלבד, " +
                   "ושינוי בתוכן השכבה לא יסמן אותה 'לא בתוקף'.";
        }

        /// <summary>Why the save button is disabled, or null when the approval can be saved.</summary>
        public string? ValidationError(string? approver, string? reason, bool confirmed)
        {
            var selected = Rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return "לא סומנו קבוצות לאישור";
            if (selected.Any(r => r.ChosenFamily == null)) return "לכל קבוצה מסומנת צריך לבחור משפחה";
            if (string.IsNullOrWhiteSpace(reason)) return "יש לכתוב על מה מבוסס האישור";
            if (string.IsNullOrWhiteSpace(approver)) return "שם המאשר הוא שדה חובה";
            if (!confirmed) return "יש לסמן שנבדקו הראיות של כל הקבוצות המסומנות";
            return null;
        }

        /// <summary>
        /// Why revoking <paramref name="target"/> is not possible yet, or null. One action per close: nothing may be checked
        /// for approval at the same time, and the engineer names a reason and confirms explicitly.
        /// </summary>
        public string? RevokeValidationError(SavedFamilyDecision? target, string? approver, string? reason, bool confirmed)
        {
            if (target == null) return "לא נבחרה החלטה שמורה לביטול";
            if (!SavedDecisions.Contains(target)) return "ההחלטה שנבחרה אינה ברשימת ההחלטות השמורות";
            if (Rows.Any(r => r.IsSelected))
                return "סומנו קבוצות לאישור. פעולה אחת בכל פעם: לאשר משפחות או לבטל החלטה אחת — יש לנקות את הסימון קודם.";
            if (string.IsNullOrWhiteSpace(reason)) return "יש לכתוב את סיבת הביטול";
            if (string.IsNullOrWhiteSpace(approver)) return "שם המאשר הוא שדה חובה";
            if (!confirmed) return "יש לאשר במפורש את ביטול ההחלטה המסומנת";
            return null;
        }

        /// <summary>A later decision governs the group now; this one no longer applies there.</summary>
        internal const string StaleOverridden = "overridden";

        internal static string StaleReason(string? reason) => reason switch
        {
            "library-missing" => "המשפחה אינה בספרייה הפעילה",
            "library-changed" => "ההחלטה ניתנה בספריית תחום אחר",
            "rule-changed" => "הגדרת הכלל בספרייה השתנתה",
            "basis" => "סוג המדידה אינו מתאים לכלל",
            "scope" => "המקור אינו בתחולת ההחלטה",
            "evidence" => "הראיות השתנו",
            "role" => "המקור אינו תכנון (מדידה, מצב קיים או תשתית קיימת)",
            "conflict" => "שתי החלטות סותרות לאותה קבוצה",
            "contradicted" => "ראיה חדשה מצביעה על משפחה אחרת",
            "approved-item" => "לקבוצה יש שיוך סעיף מאושר",
            "invalid" => "ההחלטה השמורה אינה תקינה",
            StaleOverridden => "החלטה מאוחרת יותר היא הקובעת לקבוצה (גם אם היא עצמה אינה בתוקף)",
            _ => "ההחלטה אינה תואמת עוד",
        };
    }
}
