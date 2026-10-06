using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using C = MahodAI.CivilDelivery.Estimate.ProjectRuleRecordContext;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Pure proposal eligibility and review guidance over the complete active record set.
/// Producer receipts/freshness remain external. No resolver, ranking, price, allocation,
/// approval, file access or mutation. Snapshot.Digest is tracing, not authenticity.
/// Caller owns stable inputs throughout Capture/Evaluate, as required by ED1.
/// </summary>
public static class ProjectRuleProposalScope
{
    public enum Disposition { GenericUnchanged, RuleReview, RecordReview, MixedReview, PendingContext }

    public sealed class Prepared
    {
        internal Prepared(C.Snapshot snapshot, string recordsDigest)
        { Snapshot = snapshot; RecordsDigest = recordsDigest; }
        public C.Snapshot Snapshot { get; }
        public string RecordsDigest { get; }
    }

    public sealed record Group(string? RuleKey, Disposition State, string ReasonCode, string Message,
        IReadOnlyList<string> MemberIds, IReadOnlyList<string> UnmappedMemberIds,
        IReadOnlyList<string> FullReviewMemberIds, IReadOnlyList<C.Guidance> Guidance)
    {
        // Eligibility to invoke the existing rankers only, never permission to save.
        public bool GenericProposalEligible => State == Disposition.GenericUnchanged && UnmappedMemberIds.Count > 0;
        public bool IsApproval => false;
        public bool IsPrice => false;
    }

    public sealed record Evaluation(bool ContextMatches, string? ContextReason, string? ContextMessage,
        IReadOnlyList<Group> Groups, IReadOnlyList<string> CapturedMemberIds,
        IReadOnlyList<string> CurrentMemberIds);

    private static readonly JsonSerializerOptions Json = new()
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// Call immediately after ED1 Bind on the same stable full scan. An internally
    /// inconsistent handoff is a programmer-input error, not a usable context.
    /// Capture does not prove that Bind/Run executed or that artifacts are current.
    /// </summary>
    public static Prepared Capture(C.Snapshot snapshot, IReadOnlyList<NeutralQuantityRecord> completeRecords)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Stamp);
        ArgumentNullException.ThrowIfNull(snapshot.Records);
        var records = Freeze(completeRecords);
        if (!Unique(records) || snapshot.Records.Any(g => g == null || string.IsNullOrWhiteSpace(g.RecordId)) ||
            snapshot.Records.Select(g => g.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Length)
            throw new ArgumentException("Binding and source members must be unique and complete.");
        var guidance = snapshot.Records.OrderBy(g => g.RecordId, StringComparer.Ordinal)
            .Select(g => g with { CrossingIndices = g.CrossingIndices == null ? null : Array.AsReadOnly(g.CrossingIndices.ToArray()) })
            .ToArray();
        if (guidance.Length != records.Length || !guidance.Zip(records).All(pair => Matches(pair.First, pair.Second)) ||
            records.Any(r => r.ProjectProfileId != snapshot.Stamp.ProjectId))
            throw new ArgumentException("Binding is not the supplied complete source snapshot.");
        return new Prepared(new C.Snapshot(snapshot.Stamp, Array.AsReadOnly(guidance), snapshot.Digest), RecordsDigest(records));
    }

    /// <summary>
    /// Emits every exact RuleKey group, including already mapped members. A separate
    /// case-insensitive full-review closure prevents an exact-key subset from inheriting
    /// eligibility across an incompatible sibling. No key or saved scope is rewritten.
    /// Current must come from actual owner revalidation, not a copy of Captured.
    /// </summary>
    public static Evaluation Evaluate(Prepared prepared, C.Stamp current,
        IReadOnlyList<NeutralQuantityRecord> completeCurrentRecords)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(current);
        var records = Freeze(completeCurrentRecords);
        var captured = prepared.Snapshot;
        var reason = current != captured.Stamp ? "stale_context" :
            !Unique(records) ? "invalid_members" :
            RecordsDigest(records) != prepared.RecordsDigest ? "records_changed" : null;
        var byId = captured.Records.ToDictionary(g => g.RecordId, StringComparer.Ordinal);
        var reviewScopes = records.ToLookup(r => r.Classification.RuleKey, StringComparer.OrdinalIgnoreCase);
        var groups = new List<Group>();
        foreach (var exact in records.GroupBy(r => r.Classification.RuleKey, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = exact.ToArray();
            var scope = reviewScopes[exact.Key].ToArray();
            var guidance = scope.Where(r => byId.ContainsKey(r.RecordId)).Select(r => byId[r.RecordId]).ToArray();
            var state = Disposition.PendingContext;
            var code = reason ?? "missing_rule_key";
            var message = reason == "stale_context" ? "המקור, הכללים, הפרופיל או המחירון השתנו; יש להכין הקשר חדש." :
                reason != null ? "תוכן או היקף הרשומות השתנה; ההנחיה הקודמת אינה תקפה. לא נוצר שיוך." :
                "חסרה זהות קבוצה; נדרשת בדיקת הרשומות לפני הצעה.";
            if (reason == null && !string.IsNullOrWhiteSpace(exact.Key))
            {
                bool mixedMeasurement = scope.Any(r => string.IsNullOrWhiteSpace(r.Measurement.Kind) || Units.Parse(r.Measurement.Unit).Canonical == "?") ||
                    scope.Select(r => r.Measurement.Kind).Distinct(StringComparer.Ordinal).Count() != 1 ||
                    scope.Select(r => Units.Parse(r.Measurement.Unit).Canonical).Distinct(StringComparer.Ordinal).Count() != 1;
                if (guidance.Any(g => g.State == C.State.PendingLineage))
                { code = "pending_lineage"; message = "יש בהיקף רשומות ללא הצמדה מוכחת; ההנחיות מוצגות לבדיקה בלבד."; }
                else if (mixedMeasurement || guidance.Select(g => g.State).Distinct().Count() != 1 ||
                         (guidance.All(g => g.State == C.State.RuleReview) && guidance.Select(PartIdentity).Distinct().Count() != 1))
                { state = Disposition.MixedReview; code = "mixed_scope"; message = "בהיקף המלא יש יחידות, תפקידים או תוצאות כללים שונות; אין הצעה משותפת לכמות הגולמית."; }
                else if (guidance.All(g => g.State == C.State.GenericUnchanged))
                { state = Disposition.GenericUnchanged; code = "generic_unchanged"; message = "המסלול הכללי נשאר ללא שינוי; כל הצעה עדיין דורשת אישור מפורש."; }
                else if (guidance.All(g => g.State == C.State.RuleReview))
                { state = Disposition.RuleReview; code = "physical_rule_review"; message = "הנחיה מכללי הפרויקט: יש לבדוק את מסלול כתב הכמויות לפי כללים; אין לתמחר את הסכום הגולמי באמצעות קוד ההנחיה."; }
                else
                { state = Disposition.RecordReview; code = "record_rule_review"; message = "תוצאת הכללים דורשת סקירת הרשומות; אין מכאן אישור לשיוך או להחרגה."; }
            }
            groups.Add(new Group(exact.Key, state, code, message,
                Ids(members), Ids(members.Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))),
                Ids(scope), Array.AsReadOnly(guidance)));
        }
        return new Evaluation(reason == null, reason,
            reason == null ? null : "ההקשר או היקף הרשומות השתנה; יש להכין הקשר חדש לפני הצעות. לא נוצר שיוך.",
            Array.AsReadOnly(groups.ToArray()),
            Array.AsReadOnly(captured.Records.Select(g => g.RecordId).ToArray()), Ids(records));
    }

    private static object PartIdentity(C.Guidance g) =>
        (g.Role, g.LineId, g.PartIndex, g.RuleCatalogCode, g.RuleUnit, g.PartKind, g.ObjectWidth, g.Confirmation, g.Review);
    private static IReadOnlyList<string> Ids(IEnumerable<NeutralQuantityRecord> records) =>
        Array.AsReadOnly(records.Select(r => r.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    private static string RecordsDigest(IEnumerable<NeutralQuantityRecord> records) =>
        C.RecordDigest(records.OrderBy(r => r.RecordId, StringComparer.Ordinal).ToArray());
    private static bool Unique(NeutralQuantityRecord[] records) =>
        records.All(r => !string.IsNullOrWhiteSpace(r.RecordId) && double.IsFinite(r.Measurement.RawValue)) &&
        records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() == records.Length;
    private static bool Matches(C.Guidance g, NeutralQuantityRecord r) =>
        g.RecordId == r.RecordId && g.RuleKey == r.Classification.RuleKey &&
        g.ExistingCatalogCode == r.Classification.CandidateCatalogCode &&
        g.OriginalUnit == r.Measurement.Unit &&
        BitConverter.DoubleToInt64Bits(g.OriginalRaw) == BitConverter.DoubleToInt64Bits(r.Measurement.RawValue);
    private static NeutralQuantityRecord[] Freeze(IReadOnlyList<NeutralQuantityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Any(r => r == null || r.Source == null || r.Measurement == null || r.Classification == null))
            throw new ArgumentException("Malformed source records.");
        // Preserve all fields, not just IDs/quantities. No caller-owned collection survives.
        return JsonSerializer.Deserialize<NeutralQuantityRecord[]>(JsonSerializer.Serialize(records, Json), Json)!
            .OrderBy(r => r.RecordId, StringComparer.Ordinal).ToArray();
    }
}
