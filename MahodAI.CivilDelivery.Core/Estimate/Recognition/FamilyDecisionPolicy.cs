using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using FamilyDecision = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;
using FamilyEvidenceMatch = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyEvidenceMatch;
using FamilyItemApproval = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyItemApproval;
using FamilyParameterOverride = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyParameterOverride;
using FamilySelector = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilySelector;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>How one measured group stands against the recorded family decisions.</summary>
public enum FamilyDecisionState
{
    /// <summary>
    /// An active, current decision covers the group. For an <c>exclude</c> decision the family id is null
    /// (see <see cref="FamilyResolution.IsExclusion"/>); that is the only Applied outcome without a family.
    /// </summary>
    Applied,
    /// <summary>A decision addresses the group but no longer holds. It is kept, not applied and not deleted ("re-approve").</summary>
    Stale,
    /// <summary>No active decision addresses the group.</summary>
    NotCovered,
}

/// <summary>
/// The family decision outcome of one group. Only <see cref="FamilyDecisionState.Applied"/> is authority: on a
/// Stale outcome <see cref="FamilyId"/> names the family the stale decision was about (for the re-approve prompt),
/// never a family to use. <see cref="SelectorIndex"/> is -1 when no single selector decided the outcome.
/// </summary>
public sealed record FamilyResolution(
    string GroupId,
    FamilyDecisionState State,
    string? FamilyId,
    string? DecisionId,
    int SelectorIndex,
    string? StaleReason,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc)
{
    public bool IsApplied => State == FamilyDecisionState.Applied;

    /// <summary>An applied <c>exclude</c> decision. In v1 this is information, not money-exclusion authority (see <see cref="FamilyDecisionPolicy"/>).</summary>
    public bool IsExclusion => State == FamilyDecisionState.Applied && FamilyId == null;
}

/// <summary>
/// Engineer decisions that bind measured groups to a library family, whatever their layer is called.
/// Pure and host-free. A decision is identified by the SHA-256 of its content, is validated structurally when the
/// profile is loaded or written, and is resolved against the current groups and library at run time. A decision
/// whose scope, rule version, source role or cited evidence no longer holds is Stale: kept, never applied, never
/// deleted. History is kept by superseding or revoking, never by removing entries.
///
/// A family approval never approves catalog items; <see cref="FamilyItemApproval"/> entries do, one by one, bound
/// to the price-list identity they were approved against (<see cref="IsItemApprovalCurrent"/>).
///
/// An <c>exclude</c> decision is resolved (Applied with a null family) but in v1 it is not money-exclusion
/// authority: removing measured work from a priced document stays an audited IgnoredRuleDecision.
/// </summary>
public static class FamilyDecisionPolicy
{
    public const string ApproveFamily = "approve_family", Exclude = "exclude", Active = "active", Superseded = "superseded", Revoked = "revoked";

    /// <summary>Stale reasons. Every one leaves the decision in the profile.</summary>
    public const string StaleLibraryMissing = "library-missing", StaleRuleChanged = "rule-changed", StaleBasis = "basis",
        StaleScope = "scope", StaleEvidence = "evidence", StaleRole = "role", StaleConflict = "conflict", StaleInvalid = "invalid",
        StaleContradicted = "contradicted", StaleApprovedItem = "approved-item",
        // The decision was made under another library line (roads vs landscape — Codex 01:27, 02/10). The same rule
        // fingerprint in two libraries is not a portable approval: a discipline change needs a rescan and a fresh review.
        // An edition of the same line (…-v1.3 → …-v1.4) keeps the decision; the rule fingerprint still guards meaning.
        StaleLibraryChanged = "library-changed";

    /// <summary>Local verdict values besides a family id.</summary>
    public const string VerdictAbstained = "abstained", VerdictMixed = "mixed";

    /// <summary>The local recognition verdict of a group: one family, "abstained" or "mixed".</summary>
    public static string LocalVerdict(IReadOnlyList<RecognitionProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count == 0 || proposals.All(p => p.Status == RecognitionStatus.Abstained)) return VerdictAbstained;
        var families = proposals.Select(p => p.Status == RecognitionStatus.Proposed ? p.FamilyId : null).Distinct(StringComparer.Ordinal).ToList();
        return families.Count == 1 && families[0] != null ? families[0]! : VerdictMixed;
    }

    /// <summary>
    /// True when the local recognition of the covered records now says something other than both the approved family and
    /// what it said at approval: new, relevant evidence contradicts the decision. Evidence that does not change the verdict
    /// never does. A decision recorded before verdicts were kept is contradicted only by a different proposed family.
    /// </summary>
    public static bool IsContradicted(string approvedFamily, string? verdictAtApproval, string verdictNow)
    {
        if (string.Equals(verdictNow, approvedFamily, StringComparison.Ordinal)) return false;
        if (string.IsNullOrWhiteSpace(verdictAtApproval)) return verdictNow is not (VerdictAbstained or VerdictMixed);
        return !string.Equals(verdictNow, verdictAtApproval.Trim(), StringComparison.Ordinal);
    }

    public const string ListNullCode = "EST-FAMILY-DECISION-LIST-NULL";
    public const string IncompleteCode = "EST-FAMILY-DECISION-INCOMPLETE";
    public const string DuplicateCode = "EST-FAMILY-DECISION-DUPLICATE";
    public const string IdMismatchCode = "EST-FAMILY-DECISION-ID-MISMATCH";
    public const string ItemApprovalDuplicateCode = "EST-FAMILY-ITEM-APPROVAL-DUPLICATE";
    public const string ParameterOverrideDuplicateCode = "EST-FAMILY-PARAMETER-OVERRIDE-DUPLICATE";

    /// <summary>
    /// The profile schema version written whenever family decisions exist. A build that only knows schema 1
    /// refuses such a profile (SHR-PROFILE-SCHEMA-VERSION) instead of silently dropping the unknown section
    /// on load and erasing it on its next save.
    /// </summary>
    public const int FamilyDecisionsSchemaVersion = 2;

    private const string DecisionSchema = "mahod-family-decision/1";
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    // ------------------------------------------------------------------ identity

    /// <summary>
    /// SHA-256 of the canonical decision content. <see cref="FamilyDecision.DecisionId"/>, status,
    /// superseded-by and revoked-reason do not take part, so superseding or revoking keeps the identity;
    /// any edit of what was approved (selectors, evidence, family, reason, approver, time, overrides,
    /// item approvals) changes it. Times are canonical UTC ticks; numbers use round-trip text.
    /// </summary>
    public static string DecisionId(FamilyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("schema", DecisionSchema);
            Text(w, "decision", decision.Decision);
            Text(w, "library_id", decision.LibraryId);
            Text(w, "family_id", decision.FamilyId);
            Text(w, "rule_version", decision.RuleVersion?.Trim().ToLowerInvariant());
            w.WriteStartArray("selectors");
            foreach (var selector in decision.Selectors ?? new List<FamilySelector>())
            {
                if (selector == null) { w.WriteNullValue(); continue; }
                w.WriteStartObject();
                Text(w, "source", selector.Source);
                Text(w, "layer_leaf", selector.LayerLeaf);
                Text(w, "measurement_kind", selector.MeasurementKind);
                Text(w, "method_class", selector.MethodClass);
                Text(w, "unit", selector.Unit);
                Text(w, "block_name", selector.BlockName);
                w.WriteStartArray("evidence_match");
                foreach (var match in selector.EvidenceMatch ?? new List<FamilyEvidenceMatch>())
                {
                    if (match == null) { w.WriteNullValue(); continue; }
                    w.WriteStartObject();
                    Text(w, "key", match.Key);
                    Text(w, "text", match.Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Text(w, "evidence_fingerprint", selector.EvidenceFingerprint?.Trim().ToLowerInvariant());
                Text(w, "local_verdict", selector.LocalVerdictAtApproval);
                // Omit absent binding to preserve every legacy decision identity exactly.
                if (selector.VisualBinding != null)
                {
                    w.WritePropertyName("visual_binding");
                    JsonSerializer.Serialize(w, selector.VisualBinding);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("evidence_keys");
            foreach (var key in decision.EvidenceKeys ?? new List<string>())
            {
                if (string.IsNullOrEmpty(key)) w.WriteNullValue();
                else w.WriteStringValue(key);
            }
            w.WriteEndArray();
            Text(w, "reason", decision.Reason);
            Text(w, "approved_by", decision.ApprovedBy);
            Time(w, "approved_at_utc", decision.ApprovedAtUtc);
            w.WriteStartArray("parameter_overrides");
            foreach (var parameter in decision.ParameterOverrides ?? new List<FamilyParameterOverride>())
            {
                if (parameter == null) { w.WriteNullValue(); continue; }
                w.WriteStartObject();
                Text(w, "key", parameter.Key);
                Text(w, "value", parameter.Value?.ToString("R", CultureInfo.InvariantCulture));
                Text(w, "reason", parameter.Reason);
                Text(w, "approved_by", parameter.ApprovedBy);
                Time(w, "approved_at_utc", parameter.ApprovedAtUtc);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("item_approvals");
            foreach (var item in decision.ItemApprovals ?? new List<FamilyItemApproval>())
            {
                if (item == null) { w.WriteNullValue(); continue; }
                w.WriteStartObject();
                Text(w, "catalog_code", item.CatalogCode);
                Text(w, "expected_unit", item.ExpectedUnit);
                Text(w, "approved_catalog_id", item.ApprovedCatalogId);
                Text(w, "approved_catalog_hash", item.ApprovedCatalogHash?.Trim().ToLowerInvariant());
                Text(w, "approved_catalog_item_fingerprint", item.ApprovedCatalogItemFingerprint?.Trim().ToLowerInvariant());
                Text(w, "approved_by", item.ApprovedBy);
                Time(w, "approved_at_utc", item.ApprovedAtUtc);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void Text(Utf8JsonWriter w, string name, string? value)
    {
        // YAML may normalize line breaks; the identity must not depend on them.
        if (string.IsNullOrEmpty(value)) w.WriteNull(name);
        else w.WriteString(name, value.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    private static void Time(Utf8JsonWriter w, string name, DateTime? value)
    {
        if (value is not { } time) w.WriteNull(name);
        else w.WriteString(name, ToUtc(time).ToString(UtcFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>A stored timestamp as UTC: Local is converted, Unspecified is read as UTC (the fields are UTC by contract).</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? ToUtc(DateTime? value) => value is { } time ? ToUtc(time) : null;

    /// <summary>
    /// Gives every family-decision timestamp UTC kind after a YAML load, so the loaded profile serializes
    /// (and hashes) exactly like the in-memory profile that was saved. Values are never shifted except a
    /// Local value, which is converted to the same instant in UTC.
    /// </summary>
    public static void NormalizeTimestamps(ProjectProfile.EstimateProfile? estimate)
    {
        if (estimate?.FamilyDecisions == null) return;
        foreach (var decision in estimate.FamilyDecisions)
        {
            if (decision == null) continue;
            decision.ApprovedAtUtc = ToUtc(decision.ApprovedAtUtc);
            foreach (var parameter in decision.ParameterOverrides ?? new List<FamilyParameterOverride>())
                if (parameter != null) parameter.ApprovedAtUtc = ToUtc(parameter.ApprovedAtUtc);
            foreach (var item in decision.ItemApprovals ?? new List<FamilyItemApproval>())
                if (item != null) item.ApprovedAtUtc = ToUtc(item.ApprovedAtUtc);
        }
    }

    // ---------------------------------------------------------- structural checks

    /// <summary>
    /// Structural problems of one entry (empty = complete). Only what can be checked without a scan or a
    /// library: staleness is a run-time state and never a structural problem.
    /// </summary>
    public static IReadOnlyList<string> StructuralProblems(FamilyDecision? decision)
    {
        var problems = new List<string>();
        if (decision == null)
        {
            problems.Add("the entry is empty");
            return problems;
        }
        if (!CatalogIdentity.IsValidSha256(decision.DecisionId)) problems.Add("decision_id must be a 64-hex SHA-256");
        var status = decision.Status?.Trim();
        if (status is not (Active or Superseded or Revoked)) problems.Add("status must be active, superseded or revoked");
        if (status == Superseded && !CatalogIdentity.IsValidSha256(decision.SupersededBy))
            problems.Add("a superseded decision names its successor in superseded_by");
        if (status == Revoked && string.IsNullOrWhiteSpace(decision.RevokedReason))
            problems.Add("a revoked decision keeps its revoked_reason");

        var kind = decision.Decision?.Trim();
        if (kind is not (ApproveFamily or Exclude)) problems.Add("decision must be approve_family or exclude");
        if (string.IsNullOrWhiteSpace(decision.LibraryId)) problems.Add("library_id is required");
        if (kind == ApproveFamily)
        {
            if (string.IsNullOrWhiteSpace(decision.FamilyId)) problems.Add("family_id is required");
            else if (!IsBaseRuleId(decision.FamilyId)) problems.Add("family_id must be a base library rule id (never a width or approved variant)");
            if (!CatalogIdentity.IsValidSha256(decision.RuleVersion)) problems.Add("rule_version must be a 64-hex SHA-256");
        }
        else if (kind == Exclude)
        {
            if (!string.IsNullOrWhiteSpace(decision.FamilyId) || !string.IsNullOrWhiteSpace(decision.RuleVersion))
                problems.Add("an exclusion names no family_id or rule_version");
            if ((decision.ItemApprovals?.Count ?? 0) > 0 || (decision.ParameterOverrides?.Count ?? 0) > 0)
                problems.Add("an exclusion carries no item approvals or parameter overrides");
        }
        if (string.IsNullOrWhiteSpace(decision.Reason)) problems.Add("reason is required");
        if (string.IsNullOrWhiteSpace(decision.ApprovedBy)) problems.Add("approved_by is required");
        if (decision.ApprovedAtUtc == null) problems.Add("approved_at_utc is required");

        var keys = decision.EvidenceKeys;
        if (keys == null) problems.Add("evidence_keys must be a list");
        else if (keys.Any(k => string.IsNullOrWhiteSpace(k) || !string.Equals(k, k.Trim(), StringComparison.Ordinal) ||
                               k.EndsWith(EvidenceKeys.StatusSuffix, StringComparison.Ordinal)))
            problems.Add("evidence_keys contains an empty, padded or status key");
        var cited = new HashSet<string>((keys ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()),
            StringComparer.Ordinal);

        var selectors = decision.Selectors;
        if (selectors == null || selectors.Count == 0) problems.Add("at least one selector is required");
        else
        {
            for (var i = 0; i < selectors.Count; i++)
            {
                var selector = selectors[i];
                if (selector == null)
                {
                    problems.Add($"selector {i} is empty");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(selector.Source) || string.IsNullOrWhiteSpace(selector.MeasurementKind) ||
                    string.IsNullOrWhiteSpace(selector.MethodClass) || string.IsNullOrWhiteSpace(selector.Unit))
                    problems.Add($"selector {i} needs source, measurement_kind, method_class and unit");
                else if (Units.Parse(selector.Unit).Canonical == "?")
                    problems.Add($"selector {i} unit is unknown");
                if (selector.LayerLeaf != null && selector.LayerLeaf.IndexOfAny(new[] { '*', '?' }) >= 0)
                    problems.Add($"selector {i} layer_leaf is an exact layer, not a pattern");
                var matches = selector.EvidenceMatch;
                if (matches == null) problems.Add($"selector {i} evidence_match must be a list");
                else if (matches.Any(m => m == null || string.IsNullOrWhiteSpace(m.Key) || EvidenceReader.Clean(m.Text) == null))
                    problems.Add($"selector {i} has an incomplete evidence match");
                else if (matches.Any(m => !cited.Contains(m.Key!.Trim())))
                    problems.Add($"selector {i} matches evidence the decision does not cite in evidence_keys");
                if (string.IsNullOrWhiteSpace(selector.LayerLeaf) && (matches == null || matches.Count == 0))
                    problems.Add($"selector {i} has neither a layer nor an evidence match");
                if (!CatalogIdentity.IsValidSha256(selector.EvidenceFingerprint))
                    problems.Add($"selector {i} evidence_fingerprint must be a 64-hex SHA-256");
                if (selector.VisualBinding != null && (!FamilyVisualBindingPolicy.IsValid(selector.VisualBinding) ||
                    string.IsNullOrWhiteSpace(selector.LayerLeaf) || (matches?.Count ?? 0) != 0))
                    problems.Add($"selector {i} visual binding must be valid and cover one literal whole group");
            }
        }

        var overrides = decision.ParameterOverrides;
        if (overrides == null) problems.Add("parameter_overrides must be a list");
        else
        {
            for (var i = 0; i < overrides.Count; i++)
            {
                var parameter = overrides[i];
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.Key) || parameter.Value is not { } value ||
                    !double.IsFinite(value) || string.IsNullOrWhiteSpace(parameter.Reason) ||
                    string.IsNullOrWhiteSpace(parameter.ApprovedBy) || parameter.ApprovedAtUtc == null)
                    problems.Add($"parameter override {i} is incomplete or not a finite number");
            }
        }

        var items = decision.ItemApprovals;
        if (items == null) problems.Add("item_approvals must be a list");
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || string.IsNullOrWhiteSpace(item.CatalogCode) || string.IsNullOrWhiteSpace(item.ApprovedCatalogId) ||
                    !CatalogIdentity.IsValidSha256(item.ApprovedCatalogHash) ||
                    !CatalogIdentity.IsValidSha256(item.ApprovedCatalogItemFingerprint) ||
                    string.IsNullOrWhiteSpace(item.ApprovedBy) || item.ApprovedAtUtc == null)
                    problems.Add($"item approval {i} is incomplete (catalog code, catalog id + SHA-256, item fingerprint, approver and UTC time)");
            }
        }
        return problems;
    }

    /// <summary>Structural validation of the family section; see <see cref="Validate(ProjectProfile.EstimateProfile, string?)"/>.</summary>
    public static IReadOnlyList<DeliveryFinding> Validate(ProjectProfile.EstimateProfile estimate) => Validate(estimate, null);

    /// <summary>
    /// Loader/writer-level Error findings (same style as the other profile decisions): a null list, an
    /// incomplete entry, a duplicated active entry, an identity that does not match the content, and duplicate
    /// item approvals or parameter overrides inside one decision. Staleness is never reported here.
    /// </summary>
    public static IReadOnlyList<DeliveryFinding> Validate(ProjectProfile.EstimateProfile estimate, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var findings = new List<DeliveryFinding>();

        void Add(string code, string title, string message) => findings.Add(new DeliveryFinding
        {
            Code = code,
            Domain = "shared",
            Severity = FindingSeverity.Error,
            Title = title,
            Message = message,
            ProjectProfileId = profileId,
        });

        var decisions = estimate.FamilyDecisions;
        if (decisions == null)
        {
            Add(ListNullCode, "estimate.family_decisions must be a list",
                "Use an empty list when no family decisions have been approved.");
            return findings;
        }

        for (var i = 0; i < decisions.Count; i++)
        {
            var decision = decisions[i];
            var label = $"estimate.family_decisions[{i}]";
            var problems = StructuralProblems(decision);
            if (problems.Count > 0)
            {
                Add(IncompleteCode, $"{label} is incomplete",
                    string.Join("; ", problems) + ". A family decision requires its content identity, status, decision, library, " +
                    "base family and rule version, selectors with a fingerprint (and a layer or an evidence match), reason, named approver and UTC time.");
                continue;
            }
            if (!string.Equals(decision!.DecisionId!.Trim(), DecisionId(decision), StringComparison.OrdinalIgnoreCase))
                Add(IdMismatchCode, $"{label} decision_id does not match its content",
                    "The stored identity differs from the SHA-256 of the approved content, so the entry was edited after approval. Re-approve instead of editing an approved decision.");
            foreach (var duplicate in decision.ItemApprovals
                         .GroupBy(item => item.CatalogCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
                Add(ItemApprovalDuplicateCode, $"{label} approves catalog item '{duplicate.Key}' more than once",
                    "Keep one audited approval per catalog item within a family decision.");
            foreach (var duplicate in decision.ParameterOverrides
                         .GroupBy(parameter => parameter.Key!.Trim(), StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
                Add(ParameterOverrideDuplicateCode, $"{label} overrides parameter '{duplicate.Key}' more than once",
                    "Keep one audited value per parameter within a family decision.");
        }

        foreach (var duplicate in decisions
                     .Where(d => d != null && string.Equals(d.Status?.Trim(), Active, StringComparison.Ordinal) &&
                                 CatalogIdentity.IsValidSha256(d.DecisionId))
                     .GroupBy(d => d.DecisionId!.Trim().ToLowerInvariant(), StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            Add(DuplicateCode, "estimate.family_decisions records the same active decision more than once",
                $"Decision '{duplicate.Key}' is active {duplicate.Count()} times; keep one entry.");
        }
        return findings;
    }

    // ------------------------------------------------------------------ resolution

    /// <summary>
    /// Resolves every group against the active decisions. Output order follows <paramref name="groups"/>.
    /// A decision addresses a group when one of its selectors matches the group's source, layer (or evidence
    /// match on every record), block, measurement kind and pre-width method class. An addressing decision is
    /// Applied only when it is structurally complete, the unit matches, the source role is Design/Host, the
    /// library holds the base family with the approved rule fingerprint, the rule's basis accepts the group and
    /// the cited evidence fingerprint is unchanged; otherwise it is Stale with the first failing reason.
    /// Among addressing decisions the most recent approval time wins; two current decisions of different
    /// families (or approve vs exclude) at that time make the group Stale "conflict".
    /// </summary>
    public static IReadOnlyList<FamilyResolution> Resolve(
        IReadOnlyList<FamilyDecision>? decisions,
        IReadOnlyList<RecognitionGroupInput> groups,
        EngineerBoqLibrary library)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(library);
        var prepared = (decisions ?? Array.Empty<FamilyDecision>())
            .Where(d => d != null && string.Equals(d.Status?.Trim(), Active, StringComparison.Ordinal))
            .Select(d => Prepare(d, library))
            .ToList();
        var results = new List<FamilyResolution>(groups.Count);
        foreach (var group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            results.Add(ResolveGroup(group, prepared, library));
        }
        return results;
    }

    private sealed class PreparedDecision
    {
        public required FamilyDecision Decision { get; init; }
        public required bool Valid { get; init; }
        public required bool IsExclusion { get; init; }
        public required string Id { get; init; }
        public required DateTime Time { get; init; }
        public DraftRule? Rule { get; init; }
        public string? RuleProblem { get; init; }
        public required bool LibraryChanged { get; init; }
        public required IReadOnlyList<string> Keys { get; init; }
        public required string KeySet { get; init; }
    }

    private sealed class GroupFacts
    {
        public required IReadOnlyList<NeutralQuantityRecord> Records { get; init; }
        public required string Source { get; init; }
        public required string Layer { get; init; }
        public required string? Block { get; init; }
        public required string Kind { get; init; }
        public required string Method { get; init; }
        public required UnitInfo Unit { get; init; }
        public required bool RoleAllowed { get; init; }
        public required bool Consistent { get; init; }
        public Dictionary<string, string> Fingerprints { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> Matches { get; } = new(StringComparer.Ordinal);
    }

    private static PreparedDecision Prepare(FamilyDecision decision, EngineerBoqLibrary library)
    {
        var valid = StructuralProblems(decision).Count == 0 &&
                    string.Equals(decision.DecisionId!.Trim(), DecisionId(decision), StringComparison.OrdinalIgnoreCase);
        var exclusion = string.Equals(decision.Decision?.Trim(), Exclude, StringComparison.Ordinal);
        DraftRule? rule = null;
        string? ruleProblem = null;
        if (valid && !exclusion)
        {
            var familyId = decision.FamilyId!.Trim();
            var rules = library.Rules.Where(r => string.Equals(r.Id, familyId, StringComparison.Ordinal)).ToList();
            if (rules.Count != 1) ruleProblem = StaleLibraryMissing;
            else if (!string.Equals(LibraryIdentity.RuleFingerprint(rules[0]), decision.RuleVersion!.Trim(), StringComparison.OrdinalIgnoreCase))
                ruleProblem = StaleRuleChanged;
            else rule = rules[0];
        }
        var keys = (decision.EvidenceKeys ?? new List<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new PreparedDecision
        {
            Decision = decision,
            Valid = valid,
            IsExclusion = exclusion,
            Id = decision.DecisionId?.Trim() ?? string.Empty,
            Time = decision.ApprovedAtUtc is { } time ? ToUtc(time) : DateTime.MinValue,
            Rule = rule,
            RuleProblem = ruleProblem,
            LibraryChanged = !string.Equals(EngineerBoqLibrary.Lineage(decision.LibraryId), EngineerBoqLibrary.Lineage(library.Id), StringComparison.Ordinal),
            Keys = keys,
            KeySet = string.Join('\u001f', keys),
        };
    }

    private static GroupFacts FactsOf(RecognitionGroupInput group, EngineerBoqLibrary library)
    {
        var source = NormalizeSource(group.Source);
        var records = group.Records ?? Array.Empty<NeutralQuantityRecord>();
        return new GroupFacts
        {
            Records = records,
            Source = source,
            Layer = (group.LayerLeaf ?? string.Empty).Trim(),
            Block = BlockLeaf(group.Block),
            Kind = NormalizeKind(group.Kind),
            Method = PreWidth(group.MethodClass),
            Unit = Units.Parse(group.Unit),
            RoleAllowed = IsDesignRole(group.SourceRole) && IsDesignRole(EngineerBoqDraftBuilder.SourceRole(source, library)),
            Consistent = GroupFactsProblem(group) == null,
        };
    }

    private static FamilyResolution ResolveGroup(RecognitionGroupInput group, IReadOnlyList<PreparedDecision> prepared, EngineerBoqLibrary library,
        RecognitionGroupInput? wholeGroup = null)
    {
        var facts = FactsOf(group, library);
        var wholeFacts = wholeGroup == null ? facts : FactsOf(wholeGroup, library);
        var outcomes = new List<(PreparedDecision Decision, int Selector, string? Reason)>();
        foreach (var decision in prepared)
        {
            var selectors = decision.Decision.Selectors ?? new List<FamilySelector>();
            var appliedIndex = -1;
            var staleIndex = -1;
            string? staleReason = null;
            for (var i = 0; i < selectors.Count; i++)
            {
                var selector = selectors[i];
                if (selector == null) continue;
                // A pre-existing literal approval was fingerprinted on the whole measured group.
                // Splitting for another evidence rule must not reinterpret that earlier approval.
                var selectorFacts = (selector.EvidenceMatch?.Count ?? 0) == 0 ? wholeFacts : facts;
                if (!Addresses(selector, selectorFacts)) continue;
                var reason = StaleReasonFor(decision, selector, selectorFacts);
                if (reason == null)
                {
                    appliedIndex = i;
                    break;
                }
                if (staleIndex < 0)
                {
                    staleIndex = i;
                    staleReason = reason;
                }
            }
            if (appliedIndex >= 0) outcomes.Add((decision, appliedIndex, null));
            else if (staleIndex >= 0) outcomes.Add((decision, staleIndex, staleReason));
        }

        if (outcomes.Count == 0)
            return new FamilyResolution(group.GroupId, FamilyDecisionState.NotCovered, null, null, -1, null, null, null);

        // The engineer's latest intent for the group decides; an older approval is never a fallback for a newer
        // decision that went stale.
        var newest = outcomes.Max(o => o.Decision.Time);
        var bucket = outcomes.Where(o => o.Decision.Time == newest)
            .OrderBy(o => o.Decision.Id, StringComparer.Ordinal).ToList();
        var applied = bucket.Where(o => o.Reason == null).ToList();
        if (applied.Select(o => Verdict(o.Decision)).Distinct(StringComparer.Ordinal).Count() > 1)
            return new FamilyResolution(group.GroupId, FamilyDecisionState.Stale, null, null, -1, StaleConflict, null, null);
        var staleAt = bucket.FindIndex(o => o.Reason != null);
        var chosen = staleAt >= 0 ? bucket[staleAt] : applied[0];
        var entry = chosen.Decision.Decision;
        return new FamilyResolution(
            group.GroupId,
            chosen.Reason == null ? FamilyDecisionState.Applied : FamilyDecisionState.Stale,
            chosen.Decision.IsExclusion ? null : entry.FamilyId?.Trim(),
            entry.DecisionId?.Trim(),
            chosen.Selector,
            chosen.Reason,
            entry.ApprovedBy?.Trim(),
            ToUtc(entry.ApprovedAtUtc));
    }

    private static string Verdict(PreparedDecision decision) =>
        decision.IsExclusion ? Exclude : "family:" + (decision.Decision.FamilyId?.Trim() ?? string.Empty);

    private static bool Addresses(FamilySelector selector, GroupFacts facts, bool checkEvidence = true)
    {
        if (!string.IsNullOrWhiteSpace(selector.Source) &&
            !string.Equals(NormalizeSource(selector.Source), facts.Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(selector.LayerLeaf) &&
            !string.Equals(selector.LayerLeaf.Trim(), facts.Layer, StringComparison.OrdinalIgnoreCase)) return false;
        // A count selector is bound to its block exactly: a selector recorded without a block covers only groups
        // without a block, never every block later inserted on the same layer.
        var selectorBlock = string.IsNullOrWhiteSpace(selector.BlockName) ? null : BlockLeaf(selector.BlockName);
        var countSelector = string.Equals(NormalizeKind(selector.MeasurementKind), "count", StringComparison.Ordinal) || facts.Kind == "count";
        if (countSelector
                ? !string.Equals(selectorBlock ?? string.Empty, facts.Block ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                : selectorBlock != null && !string.Equals(selectorBlock, facts.Block, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(selector.MeasurementKind) &&
            !string.Equals(NormalizeKind(selector.MeasurementKind), facts.Kind, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(selector.MethodClass) &&
            !string.Equals(PreWidth(selector.MethodClass), facts.Method, StringComparison.Ordinal)) return false;
        if (checkEvidence)
            foreach (var match in selector.EvidenceMatch ?? new List<FamilyEvidenceMatch>())
                if (match == null || !HasEvidenceTextOnEveryRecord(facts, match.Key, match.Text)) return false;
        return true;
    }

    /// <summary>
    /// A proposal seam, not an alternative measurement engine. WholeGroup retains the scan's original
    /// scope. Group is one disjoint contribution bucket. ValidationScope is the scope against which
    /// the selected decision's evidence and local verdict were actually validated.
    /// Consumers must keep GroupId in their accumulation key; regrouping by layer undoes the partition.
    /// </summary>
    public sealed record PartitionResolution(RecognitionGroupInput WholeGroup, RecognitionGroupInput Group,
        RecognitionGroupInput ValidationScope, FamilyResolution Resolution);

    /// <summary>
    /// Resolves evidence rules on disjoint subsets of complete measured groups, without changing
    /// literal approvals' original evidence scope or the existing latest-intent/conflict policy.
    /// All records occur once, including unmatched records. No record, decision or quantity is edited.
    /// This must receive groups rebuilt from the complete scan, never a caller-trimmed list.
    /// </summary>
    public static IReadOnlyList<PartitionResolution> ResolvePartitions(
        IReadOnlyList<FamilyDecision>? decisions, IReadOnlyList<RecognitionGroupInput> wholeGroups,
        EngineerBoqLibrary library, CatalogSnapshot? catalog = null, bool checkLocalContradictions = true)
    {
        ArgumentNullException.ThrowIfNull(wholeGroups);
        ArgumentNullException.ThrowIfNull(library);
        var prepared = (decisions ?? Array.Empty<FamilyDecision>())
            .Where(d => d != null && string.Equals(d.Status?.Trim(), Active, StringComparison.Ordinal))
            .Select(d => Prepare(d, library)).ToList();
        var result = new List<PartitionResolution>();
        foreach (var whole in wholeGroups)
        {
            ArgumentNullException.ThrowIfNull(whole);
            if (GroupFactsProblem(whole) != null)
            {
                // Records that do not share one set of facts (for example an unknown drawing unit) are never partitioned.
                // The group is resolved whole, exactly as Resolve does, where an addressing decision can only be Stale
                // "scope": the draft keeps building and no approval applies to it.
                result.Add(new PartitionResolution(whole, whole, whole, ResolveGroup(whole, prepared, library)));
                continue;
            }
            if (whole.Records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != whole.Records.Count)
                throw new InvalidOperationException("Duplicate record identity in full-scan group.");
            var facts = FactsOf(whole, library);
            var selectors = prepared.Where(p => p.Valid)
                .SelectMany(p => p.Decision.Selectors)
                .Where(s => s.EvidenceMatch.Count > 0 && Addresses(s, facts, checkEvidence: false))
                .GroupBy(SelectorKey, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(SelectorKey, StringComparer.Ordinal).ToList();
            string Mask(NeutralQuantityRecord record) => string.Join("\n", selectors
                .Where(s => s.EvidenceMatch.All(m => HasEvidenceText(record, m.Key!, m.Text!)))
                .Select(SelectorKey));
            var buckets = whole.Records.GroupBy(Mask, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            foreach (var bucket in buckets)
            {
                var group = buckets.Count == 1 ? whole : whole with
                {
                    GroupId = whole.GroupId + "|family-partition:" +
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bucket.Key))).ToLowerInvariant(),
                    Records = bucket.ToList(),
                };
                var resolution = ResolveGroup(group, prepared, library, whole);
                var validationScope = group;
                var chosen = prepared.FirstOrDefault(p => string.Equals(p.Id, resolution.DecisionId, StringComparison.OrdinalIgnoreCase));
                if (chosen != null && resolution.SelectorIndex >= 0 && resolution.SelectorIndex < chosen.Decision.Selectors.Count)
                {
                    var selector = chosen.Decision.Selectors[resolution.SelectorIndex];
                    if (selector.EvidenceMatch.Count == 0) validationScope = whole;
                    if (checkLocalContradictions && resolution.IsApplied && resolution.FamilyId != null)
                    {
                        var now = LocalVerdict(LocalFamilyClassifier.Instance.Classify(validationScope, library, catalog));
                        if (IsContradicted(resolution.FamilyId, selector.LocalVerdictAtApproval, now))
                            resolution = resolution with { State = FamilyDecisionState.Stale, StaleReason = StaleContradicted };
                    }
                }
                result.Add(new PartitionResolution(whole, group, validationScope, resolution));
            }
        }
        return result;
    }

    /// <summary>
    /// The reusable, source-and-layer-bounded meaning of a COMPLETE local classifier partition.
    /// This is not arbitrary object picking: selecting fewer records than the proven partition fails.
    /// The caller must rebuild wholeGroup from its scan before invoking this method.
    /// No decision is created by inspecting the matches (suitable for review/Cancel).
    /// </summary>
    /// <param name="coveredBySameFamily">Records of the whole group an active decision already applies as this same family. The
    /// classifier's partition may extend over them, so the part of a proven partition that is not covered yet (a new object
    /// without an incidental text of an earlier partition rule) is approvable on its own. The cited conjunction must still single
    /// out exactly the selected records in the whole group, so a covered record never changes decision.</param>
    public static IReadOnlyList<FamilyEvidenceMatch> EvidenceMatchesForPartition(
        RecognitionGroupInput wholeGroup, IReadOnlyList<string> partitionRecordIds, string familyId, EngineerBoqLibrary library,
        IReadOnlySet<string>? coveredBySameFamily = null)
    {
        ArgumentNullException.ThrowIfNull(wholeGroup);
        ArgumentNullException.ThrowIfNull(partitionRecordIds);
        var ids = partitionRecordIds.ToHashSet(StringComparer.Ordinal);
        var covered = coveredBySameFamily ?? new HashSet<string>(StringComparer.Ordinal);
        if (GroupFactsProblem(wholeGroup) is { } problem)
            throw new InvalidOperationException("Invalid full-scan group: " + problem);
        if (ids.Count == 0 || ids.Count != partitionRecordIds.Count || ids.Count >= wholeGroup.Records.Count ||
            wholeGroup.Records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != wholeGroup.Records.Count ||
            ids.Any(id => !wholeGroup.Records.Any(r => r.RecordId == id)))
            throw new InvalidOperationException("Expected a non-empty, proper, unambiguous partition of the full scan group.");
        if (ids.Any(covered.Contains))
            throw new InvalidOperationException("Selection includes records an active decision already covers.");
        var proposals = LocalFamilyClassifier.Instance.Classify(wholeGroup, library, null);
        // Exactly the proven partition, less only records already covered as this same family.
        var proposal = proposals.SingleOrDefault(p => p.Status == RecognitionStatus.Proposed && p.FamilyId == familyId &&
            ids.IsSubsetOf(p.RecordIds) && p.RecordIds.All(id => ids.Contains(id) || covered.Contains(id)));
        if (proposal == null)
            throw new InvalidOperationException("Selection is not one complete locally proven semantic partition.");
        var selected = wholeGroup.Records.Where(r => ids.Contains(r.RecordId)).ToList();
        var matches = new List<FamilyEvidenceMatch>();
        foreach (var key in proposal.EvidenceRefs.Select(e => e.Key).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!selected.All(r => EvidenceReader.Status(r.Measurement.Parameters, key).State == EvidenceState.Read &&
                    (!EvidenceKeys.IsEvidenceKey(key) || EvidenceReader.SchemaReadable(r.Measurement.Parameters)))) continue;
            var texts = EvidenceReader.Texts(selected[0].Measurement.Parameters, key).Select(t => t.Text)
                .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal);
            foreach (var text in texts)
                if (selected.All(r => HasEvidenceText(r, key, text))) matches.Add(new FamilyEvidenceMatch { Key = key, Text = text });
        }
        if (matches.Count == 0 || !ids.SetEquals(wholeGroup.Records
                .Where(r => matches.All(m => HasEvidenceText(r, m.Key!, m.Text!))).Select(r => r.RecordId)))
            throw new InvalidOperationException("No complete cited evidence conjunction distinguishes exactly this partition.");
        return matches;
    }

    /// <summary>
    /// Explicit semantic-rule approval: same source, exact layer, measurement basis/block and cited meaning.
    /// New matching records may be covered later, as shown in the review. No item approval is added.
    /// Uses the existing profile schema; selection and full-scan validation remain the adapter's responsibility.
    /// </summary>
    public static FamilyDecision CreatePartitionApproval(string familyId, RecognitionGroupInput wholeGroup,
        IReadOnlyList<string> partitionRecordIds, EngineerBoqLibrary library, string approvedBy, string reason, DateTime approvedAtUtc,
        IReadOnlySet<string>? coveredBySameFamily = null)
    {
        var matches = EvidenceMatchesForPartition(wholeGroup, partitionRecordIds, familyId, library, coveredBySameFamily);
        var ids = partitionRecordIds.ToHashSet(StringComparer.Ordinal);
        var selected = wholeGroup with { Records = wholeGroup.Records.Where(r => ids.Contains(r.RecordId)).ToList() };
        var decision = CreateEvidenceApproval(familyId, new[] { selected }, matches,
            matches.Select(m => m.Key!).Distinct(StringComparer.Ordinal).ToList(), library, approvedBy, reason, approvedAtUtc);
        foreach (var selector in decision.Selectors)
        {
            selector.LayerLeaf = wholeGroup.LayerLeaf.Trim();
            selector.EvidenceFingerprint = PartitionFingerprint(selected.Records, selector.EvidenceMatch);
        }
        decision.DecisionId = DecisionId(decision);
        return decision;
    }

    private static string? StaleReasonFor(PreparedDecision decision, FamilySelector selector, GroupFacts facts)
    {
        if (!decision.Valid) return StaleInvalid;
        if (decision.LibraryChanged) return StaleLibraryChanged;
        if (!facts.Consistent) return StaleScope;
        if (!string.IsNullOrWhiteSpace(selector.Unit) && !Units.Parse(selector.Unit).SameUnit(facts.Unit)) return StaleScope;
        if (!decision.IsExclusion)
        {
            if (!facts.RoleAllowed) return StaleRole;
            if (decision.RuleProblem != null) return decision.RuleProblem;
            if (!BasisAccepts(decision.Rule!, facts.Kind, facts.Method, facts.Unit)) return StaleBasis;
        }
        if (!string.Equals(FingerprintOf(facts, decision, selector), selector.EvidenceFingerprint?.Trim(), StringComparison.OrdinalIgnoreCase))
            return StaleEvidence;
        if (selector.VisualBinding != null && !FamilyVisualBindingPolicy.IsCurrent(selector.VisualBinding, facts.Records))
            return FamilyVisualBindingPolicy.StaleReason;
        return null;
    }

    /// <summary>
    /// A partition rule (<see cref="CreatePartitionApproval"/>) is the only selector bound to both a layer and an evidence
    /// conjunction. It is defined by that conjunction, so it is fingerprinted on it alone: where each cited text was read (field
    /// and tag), and a cited key's unread status. Another text of a cited key that differs per object (a numbered attribute)
    /// takes no part, so a new, renumbered or removed member carrying the conjunction keeps the rule; the local-contradiction
    /// check still judges the meaning of the covered records.
    /// </summary>
    private static bool IsPartitionSelector(FamilySelector selector) =>
        !string.IsNullOrWhiteSpace(selector.LayerLeaf) && (selector.EvidenceMatch?.Count ?? 0) > 0;

    private static string PartitionFingerprint(IEnumerable<NeutralQuantityRecord> records, IEnumerable<FamilyEvidenceMatch?> matches)
    {
        var cited = new HashSet<(string Key, string Text)>();
        foreach (var match in matches)
            if (match?.Key?.Trim() is { Length: > 0 } key && EvidenceReader.Clean(match.Text) is { } text) cited.Add((key, text));
        return EvidenceReader.Fingerprint(records.Select(r => (IReadOnlyDictionary<string, string>)r.Measurement.Parameters),
            cited.Select(c => c.Key), text => cited.Contains((text.Key, text.Text)));
    }

    private static string FingerprintOf(GroupFacts facts, PreparedDecision decision, FamilySelector selector)
    {
        if (IsPartitionSelector(selector))
        {
            var conjunction = "partition\u001d" + string.Join('\u001e', selector.EvidenceMatch.Select(m => m?.Key + "=" + m?.Text));
            if (!facts.Fingerprints.TryGetValue(conjunction, out var partition))
                facts.Fingerprints[conjunction] = partition = PartitionFingerprint(facts.Records, selector.EvidenceMatch);
            return partition;
        }
        if (!facts.Fingerprints.TryGetValue(decision.KeySet, out var fingerprint))
            facts.Fingerprints[decision.KeySet] = fingerprint = EvidenceReader.Fingerprint(
                facts.Records.Select(r => (IReadOnlyDictionary<string, string>)r.Measurement.Parameters), decision.Keys);
        return fingerprint;
    }

    private static bool HasEvidenceTextOnEveryRecord(GroupFacts facts, string? key, string? text)
    {
        var cleanKey = key?.Trim();
        var cleanText = EvidenceReader.Clean(text);
        if (string.IsNullOrWhiteSpace(cleanKey) || cleanText == null || facts.Records.Count == 0) return false;
        var cacheKey = cleanKey + '\u001f' + cleanText;
        if (!facts.Matches.TryGetValue(cacheKey, out var present))
            facts.Matches[cacheKey] = present = facts.Records.All(r => r != null && HasEvidenceText(r, cleanKey, cleanText));
        return present;
    }

    private static bool HasEvidenceText(NeutralQuantityRecord record, string key, string text) =>
        EvidenceReader.Texts(record.Measurement.Parameters, key)
            .Any(t => string.Equals(t.Text, text, StringComparison.Ordinal));

    // ------------------------------------------------------------ group facts

    /// <summary>
    /// Null when every record of the group has the group's declared source, layer leaf, measurement kind,
    /// pre-width method class, unit and (for counts) block; otherwise why not. A decision is never fingerprinted
    /// over records that do not belong to the selector it records.
    /// </summary>
    public static string? GroupFactsProblem(RecognitionGroupInput group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Records == null || group.Records.Count == 0) return "the group has no measured records";
        var source = NormalizeSource(group.Source);
        var layer = (group.LayerLeaf ?? string.Empty).Trim();
        var kind = NormalizeKind(group.Kind);
        var method = PreWidth(group.MethodClass);
        var unit = Units.Parse(group.Unit);
        var block = BlockLeaf(group.Block);
        foreach (var record in group.Records)
        {
            if (record == null) return "the group contains an empty record";
            if (!string.Equals(NormalizeSource(record.Source.Xref), source, StringComparison.OrdinalIgnoreCase))
                return $"record '{record.RecordId}' belongs to another source";
            if (!string.Equals(SectionProjectionLogic.LayerLeaf(record.Source.Layer), layer, StringComparison.OrdinalIgnoreCase))
                return $"record '{record.RecordId}' is on another layer";
            var recordKind = NormalizeKind(record.Measurement.Kind);
            if (!string.Equals(recordKind, kind, StringComparison.Ordinal))
                return $"record '{record.RecordId}' has another measurement kind";
            if (!string.Equals(PreWidthMethodClass(recordKind, record.Measurement.Method), method, StringComparison.Ordinal))
                return $"record '{record.RecordId}' has another measurement method";
            if (!Units.Parse(record.Measurement.Unit).SameUnit(unit))
                return $"record '{record.RecordId}' has another or an unknown unit";
            if (kind == "count")
            {
                record.Measurement.Parameters.TryGetValue(EvidenceKeys.BlockNameEffective, out var rawBlock);
                if (!string.Equals(BlockLeaf(rawBlock), block, StringComparison.OrdinalIgnoreCase))
                    return $"record '{record.RecordId}' is another block";
            }
        }
        return null;
    }

    /// <summary>
    /// The measurement method class before any drawn-width reclassification. Mirrors
    /// EngineerBoqDraftBuilder.MethodClass (internal there, and the builder is not edited by this component).
    /// </summary>
    public static string PreWidthMethodClass(string? kind, string? method)
    {
        var m = (method ?? string.Empty).Trim().ToLowerInvariant();
        return NormalizeKind(kind) switch
        {
            "area" when m.StartsWith("hatch", StringComparison.Ordinal) => "hatch",
            "area" when m.StartsWith("closed-polyline", StringComparison.Ordinal) => "closed-polyline",
            "area" => "other",
            "length" when m.StartsWith("closed-polyline-perimeter", StringComparison.Ordinal) => "closed-perimeter",
            "length" => "open",
            "count" => "block",
            "volume" when m.StartsWith("corridor", StringComparison.Ordinal) => "corridor",
            _ => "other",
        };
    }

    /// <summary>
    /// Whether a rule's basis accepts a group of this kind, method class and unit. Mirrors the draft builder's
    /// private BasisAccepts/Accepts (including the drawn-width painted-area clause); in addition the unit's
    /// dimension must fit the kind, so an unknown unit is never accepted.
    /// </summary>
    public static bool BasisAccepts(DraftRule rule, string? kind, string? methodClass, string? unit)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return BasisAccepts(rule, NormalizeKind(kind), PreWidth(methodClass), Units.Parse(unit));
    }

    private static bool BasisAccepts(DraftRule rule, string kind, string method, UnitInfo unit)
    {
        var byBasis = rule.Basis switch
        {
            DraftQuantityBasis.HatchArea => kind == "area" && method == "hatch",
            DraftQuantityBasis.LengthWithClosedPerimeters => kind == "length" && (IsOpenLine(method) || method == "closed-perimeter"),
            DraftQuantityBasis.OpenLength => kind == "length" && IsOpenLine(method),
            DraftQuantityBasis.Count => kind == "count",
            _ => false,
        };
        if (!byBasis && !(rule.SplitByDrawnWidth && method == "painted-width")) return false;
        var dimension = kind switch
        {
            "area" => UnitDimension.Area,
            "length" => UnitDimension.Length,
            "count" => UnitDimension.Count,
            _ => UnitDimension.Unknown,
        };
        return dimension != UnitDimension.Unknown && unit.Dimension == dimension;
    }

    private static bool IsOpenLine(string methodClass) => methodClass is "open" or "open-w10" or "open-w15" or "open-width-unproven";

    /// <summary>Drawn-width classes of an open line are one pre-width class; a width decides pricing, not the family.</summary>
    private static string PreWidth(string? methodClass)
    {
        var value = (methodClass ?? string.Empty).Trim().ToLowerInvariant();
        return value is "open-w10" or "open-w15" or "open-width-unproven" ? "open" : value;
    }

    private static string NormalizeKind(string? kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The draft's source identity: an empty or "(host)" XREF is the host drawing.</summary>
    public static string NormalizeSource(string? source)
    {
        var value = (source ?? string.Empty).Trim();
        return value.Length == 0 || value.Equals("(host)", StringComparison.OrdinalIgnoreCase) ? EngineerBoqDraftBuilder.HostSource : value;
    }

    private static string? BlockLeaf(string? block) =>
        string.IsNullOrWhiteSpace(block) ? null : SectionProjectionLogic.LayerLeaf(block);

    private static bool IsDesignRole(DraftSourceRole role) => role is DraftSourceRole.Design or DraftSourceRole.Host;

    /// <summary>A library rule id as written in the library, never a draft-time width, approved or profile variant.</summary>
    public static bool IsBaseRuleId(string? familyId) =>
        !string.IsNullOrWhiteSpace(familyId) && familyId.IndexOfAny(new[] { '@', '+' }) < 0 &&
        !familyId.Trim().StartsWith("profile-approved:", StringComparison.Ordinal);

    // ------------------------------------------------------------------ creation

    /// <summary>Attach only after the normal explicit approval gate, using the scan's reconstructed whole group.</summary>
    public static void AttachVisualBinding(FamilyDecision decision, RecognitionGroupInput group, FamilyVisualBinding binding)
    {
        if (decision.Decision != ApproveFamily || decision.Selectors.Count != 1 || StructuralProblems(decision).Count != 0 ||
            !string.Equals(decision.DecisionId, DecisionId(decision), StringComparison.OrdinalIgnoreCase) ||
            !FamilyVisualBindingPolicy.IsCurrent(binding, group.Records) || GroupFactsProblem(group) != null)
            throw new InvalidOperationException("Visual approval does not match one current measured group.");
        var selector = decision.Selectors[0];
        if (selector.VisualBinding != null || selector.EvidenceMatch.Count != 0 ||
            !string.Equals(selector.LayerLeaf, group.LayerLeaf, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeSource(selector.Source), NormalizeSource(group.Source), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeKind(selector.MeasurementKind), NormalizeKind(group.Kind), StringComparison.Ordinal) ||
            !string.Equals(PreWidth(selector.MethodClass), PreWidth(group.MethodClass), StringComparison.Ordinal) ||
            !Units.Parse(selector.Unit).SameUnit(Units.Parse(group.Unit)) ||
            !string.Equals(BlockLeaf(selector.BlockName), NormalizeKind(group.Kind) == "count" ? BlockLeaf(group.Block) : null, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Visual approval cannot broaden its reviewed scope.");
        selector.VisualBinding = FamilyVisualBindingPolicy.Clone(binding);
        decision.DecisionId = DecisionId(decision);
    }

    /// <summary>
    /// A new active decision covering <paramref name="coveredGroups"/> with one literal selector per group
    /// (source + exact layer leaf + kind + pre-width method class + canonical unit + block for counts), each bound
    /// to the fingerprint of the group's records over the cited evidence keys. Refuses (throws) rather than
    /// records anything it cannot prove: an unknown unit, a survey/existing-utilities source, a basis the family
    /// does not measure, records that do not match their group, or a cited key that was never read.
    /// A family approval never approves items: <see cref="FamilyDecision.ItemApprovals"/> starts empty.
    /// </summary>
    public static FamilyDecision CreateApproval(
        string familyId,
        IReadOnlyList<RecognitionGroupInput> coveredGroups,
        IReadOnlyList<string> evidenceKeys,
        EngineerBoqLibrary library,
        string approvedBy,
        string reason,
        DateTime approvedAtUtc,
        string decision = ApproveFamily) =>
        Create(familyId, coveredGroups, null, evidenceKeys, library, approvedBy, reason, approvedAtUtc, decision);

    /// <summary>
    /// Like <see cref="CreateApproval"/>, but the selectors name no layer: they cover any layer of the same
    /// source, kind, method class, unit and block whose every record carries all <paramref name="evidenceMatch"/>
    /// texts, and whose cited evidence has the approved fingerprint. This is how one decision keeps covering
    /// randomly named layers that carry the same meaning. A new source is still never covered.
    /// </summary>
    public static FamilyDecision CreateEvidenceApproval(
        string familyId,
        IReadOnlyList<RecognitionGroupInput> coveredGroups,
        IReadOnlyList<FamilyEvidenceMatch> evidenceMatch,
        IReadOnlyList<string> evidenceKeys,
        EngineerBoqLibrary library,
        string approvedBy,
        string reason,
        DateTime approvedAtUtc,
        string decision = ApproveFamily)
    {
        ArgumentNullException.ThrowIfNull(evidenceMatch);
        return Create(familyId, coveredGroups, evidenceMatch, evidenceKeys, library, approvedBy, reason, approvedAtUtc, decision);
    }

    private static FamilyDecision Create(
        string? familyId,
        IReadOnlyList<RecognitionGroupInput> coveredGroups,
        IReadOnlyList<FamilyEvidenceMatch>? evidenceMatch,
        IReadOnlyList<string> evidenceKeys,
        EngineerBoqLibrary library,
        string approvedBy,
        string reason,
        DateTime approvedAtUtc,
        string decision)
    {
        ArgumentNullException.ThrowIfNull(coveredGroups);
        ArgumentNullException.ThrowIfNull(evidenceKeys);
        ArgumentNullException.ThrowIfNull(library);
        if (decision is not (ApproveFamily or Exclude))
            throw new ArgumentException("A family decision is approve_family or exclude.", nameof(decision));
        var approver = approvedBy?.Trim();
        if (string.IsNullOrWhiteSpace(approver) || approver.Any(char.IsControl))
            throw new ArgumentException("A named approver on one line is required for a family decision.", nameof(approvedBy));
        var why = OneLine(reason);
        if (why == null)
            throw new ArgumentException("An engineering reason is required for a family decision.", nameof(reason));
        if (approvedAtUtc.Kind == DateTimeKind.Unspecified)
            throw new ArgumentException("The approval time must be UTC.", nameof(approvedAtUtc));
        if (coveredGroups.Count == 0)
            throw new ArgumentException("A family decision covers at least one measured group.", nameof(coveredGroups));

        DraftRule? rule = null;
        var family = familyId?.Trim();
        if (decision == ApproveFamily)
        {
            if (!IsBaseRuleId(family))
                throw new ArgumentException("A family approval names one base library rule.", nameof(familyId));
            var rules = library.Rules.Where(r => string.Equals(r.Id, family, StringComparison.Ordinal)).ToList();
            if (rules.Count != 1)
                throw new InvalidOperationException($"Family '{family}' is not one rule of library '{library.Id}'.");
            rule = rules[0];
        }
        else if (!string.IsNullOrWhiteSpace(family))
            throw new ArgumentException("An exclusion names no family.", nameof(familyId));

        var matches = new List<FamilyEvidenceMatch>();
        foreach (var match in evidenceMatch ?? Array.Empty<FamilyEvidenceMatch>())
        {
            var key = match?.Key?.Trim();
            var text = EvidenceReader.Clean(match?.Text);
            if (string.IsNullOrWhiteSpace(key) || text == null || key.EndsWith(EvidenceKeys.StatusSuffix, StringComparison.Ordinal))
                throw new ArgumentException("Every evidence match needs an evidence key and a text.", nameof(evidenceMatch));
            if (!matches.Any(m => m.Key == key && m.Text == text))
                matches.Add(new FamilyEvidenceMatch { Key = key, Text = text });
        }
        if (evidenceMatch != null && matches.Count == 0)
            throw new ArgumentException("An evidence selector needs at least one evidence match.", nameof(evidenceMatch));
        matches = matches.OrderBy(m => m.Key, StringComparer.Ordinal).ThenBy(m => m.Text, StringComparer.Ordinal).ToList();

        var keySet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in evidenceKeys)
        {
            var key = raw?.Trim();
            if (string.IsNullOrWhiteSpace(key) || key.EndsWith(EvidenceKeys.StatusSuffix, StringComparison.Ordinal))
                throw new ArgumentException("Evidence keys are non-empty value keys.", nameof(evidenceKeys));
            keySet.Add(key);
        }
        foreach (var match in matches) keySet.Add(match.Key!);
        var keys = keySet.ToList();

        var selectors = new List<FamilySelector>();
        var covered = new List<NeutralQuantityRecord>();
        foreach (var group in coveredGroups)
        {
            if (group == null) throw new ArgumentException("A covered group is empty.", nameof(coveredGroups));
            var problem = GroupFactsProblem(group);
            if (problem != null)
                throw new InvalidOperationException($"Group '{group.GroupId}' cannot be approved: {problem}.");
            var source = NormalizeSource(group.Source);
            var kind = NormalizeKind(group.Kind);
            var method = PreWidth(group.MethodClass);
            var unit = Units.Parse(group.Unit);
            if (rule != null)
            {
                if (!IsDesignRole(group.SourceRole) || !IsDesignRole(EngineerBoqDraftBuilder.SourceRole(source, library)))
                    throw new InvalidOperationException(
                        $"Group '{group.GroupId}' comes from a survey or existing-utilities source; a family approval never turns existing work into new work.");
                if (!BasisAccepts(rule, kind, method, unit))
                    throw new InvalidOperationException(
                        $"Family '{rule.Id}' does not measure {kind} by '{method}' in {unit.Canonical}; group '{group.GroupId}' cannot be approved as it.");
            }
            foreach (var match in matches)
                if (!group.Records.All(r => HasEvidenceText(r, match.Key!, match.Text!)))
                    throw new InvalidOperationException(
                        $"Evidence '{match.Key}' does not carry the approved text on every record of group '{group.GroupId}'.");
            var layer = (group.LayerLeaf ?? string.Empty).Trim();
            if (matches.Count == 0 && layer.Length == 0)
                throw new InvalidOperationException($"Group '{group.GroupId}' has no layer; approve it through an evidence match.");
            selectors.Add(new FamilySelector
            {
                Source = source,
                LayerLeaf = matches.Count == 0 ? layer : null,
                MeasurementKind = kind,
                MethodClass = method,
                Unit = unit.Canonical,
                BlockName = kind == "count" ? BlockLeaf(group.Block) : null,
                EvidenceMatch = matches.Select(m => new FamilyEvidenceMatch { Key = m.Key, Text = m.Text }).ToList(),
                EvidenceFingerprint = EvidenceReader.Fingerprint(
                    group.Records.Select(r => (IReadOnlyDictionary<string, string>)r.Measurement.Parameters), keys),
                LocalVerdictAtApproval = LocalVerdict(LocalFamilyClassifier.Instance.Classify(group, library, null)),
            });
            covered.AddRange(group.Records);
        }

        // Only read (or read-but-truncated) evidence may be cited.
        foreach (var key in keys)
            if (!covered.Any(r => EvidenceReader.Status(r.Measurement.Parameters, key).Usable))
                throw new InvalidOperationException(
                    $"Evidence '{key}' was not read on the approved records; only read evidence may be cited.");

        var result = new FamilyDecision
        {
            Status = Active,
            Decision = decision,
            LibraryId = library.Id,
            FamilyId = rule?.Id,
            RuleVersion = rule == null ? null : LibraryIdentity.RuleFingerprint(rule),
            Selectors = selectors.GroupBy(SelectorKey, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(SelectorKey, StringComparer.Ordinal).ToList(),
            EvidenceKeys = keys,
            Reason = why,
            ApprovedBy = approver,
            ApprovedAtUtc = approvedAtUtc.ToUniversalTime(),
        };
        result.DecisionId = DecisionId(result);
        return result;
    }

    private static string SelectorKey(FamilySelector selector) => string.Join('\u001f',
        selector.Source ?? string.Empty, selector.LayerLeaf ?? string.Empty, selector.MeasurementKind ?? string.Empty,
        selector.MethodClass ?? string.Empty, selector.Unit ?? string.Empty, selector.BlockName ?? string.Empty,
        string.Join('\u001e', (selector.EvidenceMatch ?? new List<FamilyEvidenceMatch>()).Select(m => m.Key + "=" + m.Text)),
        selector.EvidenceFingerprint ?? string.Empty);

    /// <summary>
    /// One item approval bound to the price-list identity the engineer saw. It is separate authority from the
    /// family approval and becomes invalid when the active price list changes (<see cref="IsItemApprovalCurrent"/>).
    /// </summary>
    public static FamilyItemApproval CreateItemApproval(
        string catalogCode, CatalogSnapshot catalog, string? expectedUnit, string approvedBy, DateTime approvedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var code = catalogCode?.Trim();
        if (string.IsNullOrWhiteSpace(code) || !catalog.Items.TryGetValue(code, out var item))
            throw new InvalidOperationException($"Catalog code '{code}' does not exist in the active price list.");
        if (!CatalogIdentity.IsValidSha256(catalog.FileHash) || string.IsNullOrWhiteSpace(catalog.SnapshotId))
            throw new InvalidOperationException("The active price list has no verified identity.");
        var approver = approvedBy?.Trim();
        if (string.IsNullOrWhiteSpace(approver) || approver.Any(char.IsControl))
            throw new ArgumentException("A named approver on one line is required for an item approval.", nameof(approvedBy));
        if (approvedAtUtc.Kind == DateTimeKind.Unspecified)
            throw new ArgumentException("The approval time must be UTC.", nameof(approvedAtUtc));
        return new FamilyItemApproval
        {
            CatalogCode = item.Code,
            ExpectedUnit = string.IsNullOrWhiteSpace(expectedUnit) ? null : expectedUnit.Trim(),
            ApprovedCatalogId = catalog.SnapshotId,
            ApprovedCatalogHash = catalog.FileHash.Trim().ToLowerInvariant(),
            ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item),
            ApprovedBy = approver,
            ApprovedAtUtc = approvedAtUtc.ToUniversalTime(),
        };
    }

    /// <summary>
    /// Mirrors <see cref="CatalogIdentity.IsClassificationCurrent"/>: complete approval, same snapshot id and
    /// SHA-256, the item still present and its fingerprint unchanged. A family decision never makes an item
    /// approval current; a price-list change invalidates it whatever happens to the family.
    /// </summary>
    public static bool IsItemApprovalCurrent(FamilyItemApproval approval, CatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(approval.CatalogCode) ||
            string.IsNullOrWhiteSpace(approval.ApprovedCatalogId) ||
            string.IsNullOrWhiteSpace(approval.ApprovedBy) ||
            approval.ApprovedAtUtc == null ||
            !CatalogIdentity.IsValidSha256(approval.ApprovedCatalogHash) ||
            !CatalogIdentity.IsValidSha256(approval.ApprovedCatalogItemFingerprint) ||
            !string.Equals(approval.ApprovedCatalogId.Trim(), catalog.SnapshotId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(approval.ApprovedCatalogHash!.Trim(), catalog.FileHash, StringComparison.OrdinalIgnoreCase) ||
            !catalog.Items.TryGetValue(approval.CatalogCode.Trim(), out var item))
            return false;
        return string.Equals(approval.ApprovedCatalogItemFingerprint!.Trim(), CatalogIdentity.ItemFingerprint(item),
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ history

    /// <summary>
    /// A new list in which the one active decision <paramref name="supersededDecisionId"/> is marked superseded by
    /// <paramref name="replacement"/> and the replacement is appended. Nothing is removed. The input list and its
    /// entries are not modified.
    /// </summary>
    public static IReadOnlyList<FamilyDecision> Supersede(
        IReadOnlyList<FamilyDecision> decisions, string supersededDecisionId, FamilyDecision replacement)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(replacement);
        var problems = StructuralProblems(replacement);
        if (problems.Count > 0)
            throw new ArgumentException("The replacement decision is incomplete: " + string.Join("; ", problems) + ".", nameof(replacement));
        if (!string.Equals(replacement.Status?.Trim(), Active, StringComparison.Ordinal) ||
            !string.Equals(replacement.DecisionId!.Trim(), DecisionId(replacement), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The replacement must be an active decision whose identity matches its content.", nameof(replacement));
        var id = supersededDecisionId?.Trim();
        if (string.Equals(id, replacement.DecisionId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A decision cannot supersede itself.");
        if (decisions.Any(d => d != null && string.Equals(d.DecisionId?.Trim(), replacement.DecisionId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(d.Status?.Trim(), Active, StringComparison.Ordinal)))
            throw new InvalidOperationException("The replacement decision is already recorded as active.");
        var result = decisions.Select(d => Clone(d)!).ToList();
        var target = SingleActive(result, id, "superseded");
        target.Status = Superseded;
        target.SupersededBy = replacement.DecisionId.Trim();
        result.Add(Clone(replacement)!);
        return result;
    }

    /// <summary>A new list in which the one active decision <paramref name="decisionId"/> is revoked with a reason. Nothing is removed.</summary>
    public static IReadOnlyList<FamilyDecision> Revoke(IReadOnlyList<FamilyDecision> decisions, string decisionId, string reason)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var why = OneLine(reason) ?? throw new ArgumentException("A reason is required to revoke a family decision.", nameof(reason));
        var result = decisions.Select(d => Clone(d)!).ToList();
        var target = SingleActive(result, decisionId?.Trim(), "revoked");
        target.Status = Revoked;
        target.RevokedReason = why;
        return result;
    }

    private static FamilyDecision SingleActive(List<FamilyDecision> decisions, string? id, string action)
    {
        var matches = decisions.Where(d => d != null && !string.IsNullOrWhiteSpace(id) &&
                                           string.Equals(d.DecisionId?.Trim(), id, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(d.Status?.Trim(), Active, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0
                ? $"No active family decision '{id}' can be {action}."
                : $"Family decision '{id}' is active more than once; resolve the duplicate before it can be {action}.");
        return matches[0];
    }

    /// <summary>A deep copy (the profile's list is never shared with a working copy).</summary>
    public static FamilyDecision? Clone(FamilyDecision? decision) => decision == null ? null : new FamilyDecision
    {
        DecisionId = decision.DecisionId,
        Status = decision.Status,
        Decision = decision.Decision,
        LibraryId = decision.LibraryId,
        FamilyId = decision.FamilyId,
        RuleVersion = decision.RuleVersion,
        Selectors = decision.Selectors?.Select(s => s == null ? null! : new FamilySelector
        {
            Source = s.Source,
            LayerLeaf = s.LayerLeaf,
            MeasurementKind = s.MeasurementKind,
            MethodClass = s.MethodClass,
            Unit = s.Unit,
            BlockName = s.BlockName,
            EvidenceMatch = s.EvidenceMatch?.Select(m => m == null ? null! : new FamilyEvidenceMatch { Key = m.Key, Text = m.Text }).ToList()!,
            EvidenceFingerprint = s.EvidenceFingerprint,
            VisualBinding = FamilyVisualBindingPolicy.Clone(s.VisualBinding),
            LocalVerdictAtApproval = s.LocalVerdictAtApproval,
        }).ToList()!,
        EvidenceKeys = decision.EvidenceKeys?.ToList()!,
        Reason = decision.Reason,
        ApprovedBy = decision.ApprovedBy,
        ApprovedAtUtc = decision.ApprovedAtUtc,
        SupersededBy = decision.SupersededBy,
        RevokedReason = decision.RevokedReason,
        ParameterOverrides = decision.ParameterOverrides?.Select(p => p == null ? null! : new FamilyParameterOverride
        {
            Key = p.Key,
            Value = p.Value,
            Reason = p.Reason,
            ApprovedBy = p.ApprovedBy,
            ApprovedAtUtc = p.ApprovedAtUtc,
        }).ToList()!,
        ItemApprovals = decision.ItemApprovals?.Select(a => a == null ? null! : new FamilyItemApproval
        {
            CatalogCode = a.CatalogCode,
            ExpectedUnit = a.ExpectedUnit,
            ApprovedCatalogId = a.ApprovedCatalogId,
            ApprovedCatalogHash = a.ApprovedCatalogHash,
            ApprovedCatalogItemFingerprint = a.ApprovedCatalogItemFingerprint,
            ApprovedBy = a.ApprovedBy,
            ApprovedAtUtc = a.ApprovedAtUtc,
        }).ToList()!,
    };

    /// <summary>Control characters and runs of whitespace become one space; null when nothing is left.</summary>
    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }
}
