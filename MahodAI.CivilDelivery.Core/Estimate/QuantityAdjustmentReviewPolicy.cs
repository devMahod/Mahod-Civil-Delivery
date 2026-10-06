using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>One explicitly reviewed factor for one measured rule key. No mapping, price or geometry authority.</summary>
public static class QuantityAdjustmentReviewPolicy
{
    private const string OwnerMarker = "UI-RULE-ADJUSTMENT-V1\n";
    public sealed record Input(string RecordId, double Raw, string? CatalogCode);
    public sealed record Existing(string Id, double? Factor, string? Formula, string? Source, string? Reason,
        string Status, string? Scope, int Order, string? ApprovedBy, DateTime? ApprovedAtUtc, bool Candidate);
    public sealed record Context(string ProfileId, string ProfileHash, string RuleKey, string RecordsHash,
        string RuleId, string Layer, string EntityTypes, string Kind, string Unit,
        IReadOnlyList<Input> Inputs, IReadOnlyList<Existing> ExistingRules, Existing? Owned, int Order);
    public sealed record Preview(decimal RawTotal, decimal BeforeTotal, decimal AfterTotal, int ObjectCount);
    public sealed record Decision(Context Context, bool Remove, double? Factor, string Basis, string Source,
        string Reason, string ApprovedBy, DateTime ApprovedAtUtc);

    public static string OwnedRuleId(string ruleKey) =>
        "ui-rule-factor-" + ArtifactHash.Sha256OfText(ruleKey.Trim().ToUpperInvariant());
    public static string Basis(Existing? rule) => rule?.Formula?.StartsWith(OwnerMarker, StringComparison.Ordinal) == true
        ? rule.Formula[OwnerMarker.Length..] : string.Empty;

    public static Context Capture(ProjectProfile profile, string ruleKey, IReadOnlyList<NeutralQuantityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(records);
        if (string.IsNullOrWhiteSpace(ruleKey) || ruleKey != ruleKey.Trim() || records.Count == 0 ||
            string.IsNullOrWhiteSpace(profile.ProfileId))
            throw new InvalidOperationException("נדרשת קבוצת כמות מזוהה ולא ריקה.");
        if (records.Any(record => record == null || record.Measurement == null || record.Classification == null ||
                record.Source == null || string.IsNullOrWhiteSpace(record.RecordId) ||
                !string.Equals(record.Classification.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase) ||
                !double.IsFinite(record.Measurement.RawValue) || record.Measurement.RawValue <= 0) ||
            records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new InvalidOperationException("מקדם אינו מתקן מדידה חסרה, לא תקינה או היקף קבוצה מעורב.");
        var unit = Units.Parse(records[0].Measurement.Unit);
        var kind = records[0].Measurement.Kind;
        if (unit.Dimension == UnitDimension.Unknown || string.IsNullOrWhiteSpace(kind) || records.Any(record =>
                !Units.Parse(record.Measurement.Unit).SameUnit(unit) || record.Measurement.Kind != kind))
            throw new InvalidOperationException("לקבוצה חייבים להיות סוג מדידה ויחידה אחידים; מקדם אינו המרת יחידות.");
        if (EstimateConfigurationPolicy.Validate(profile).Any(EstimatePreflightPolicy.IsBlocking))
            throw new InvalidOperationException("בפרופיל קיימות החלטות מתנגשות. יש לפתור אותן לפני שינוי מקדם.");

        var id = OwnedRuleId(ruleKey);
        var owned = profile.Estimate.ApprovedAdjustments.SingleOrDefault(rule =>
            string.Equals(rule.RuleId, id, StringComparison.OrdinalIgnoreCase));
        if (owned != null && (!string.Equals(owned.Scope, "rule:" + ruleKey, StringComparison.OrdinalIgnoreCase) ||
                owned.Formula?.StartsWith(OwnerMarker, StringComparison.Ordinal) != true))
            throw new InvalidOperationException("מזהה המקדם כבר שייך לכלל אחר; העורך לא ידרוס אותו.");
        if (profile.Estimate.CandidateAdjustments.Any(rule => string.Equals(rule.RuleId, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("מועמד קיים משתמש באותו מזהה. אין אישור מועמד או החלפה אוטומטית.");
        var existing = profile.Estimate.ApprovedAdjustments.Select(rule => Detach(rule, false))
            .Concat(profile.Estimate.CandidateAdjustments.Select(rule => Detach(rule, true))).ToArray();
        var order = owned?.Order ?? checked(profile.Estimate.ApprovedAdjustments.Select(rule => rule.Order).DefaultIfEmpty(0).Max() + 1);
        var context = new Context(profile.ProfileId, EstimateTraceIdentity.EffectiveProfileHash(profile), ruleKey,
            ArtifactHash.Sha256OfText(JsonSerializer.Serialize(records)), id,
            string.Join("; ", records.Select(record => record.Source.Layer).Distinct(StringComparer.Ordinal)),
            string.Join("; ", records.Select(record => record.Source.EntityType).Distinct(StringComparer.Ordinal)),
            kind, unit.Canonical, Array.AsReadOnly(records.Select(record => new Input(record.RecordId,
                record.Measurement.RawValue, record.Classification.CandidateCatalogCode)).ToArray()),
            Array.AsReadOnly(existing), owned == null ? null : Detach(owned, false), order);
        // Unknown/malformed existing rules must not disappear from the preview.
        Compute(context, null, replaceOwned: false);
        return context;
    }

    public static Preview PreviewChange(Context context, double? factor, bool remove)
    {
        if (remove && context.Owned == null) throw new InvalidOperationException("אין מקדם בבעלות העורך להסרה.");
        if (!remove && (factor == null || !double.IsFinite(factor.Value) || factor <= 0))
            throw new InvalidOperationException("יש להזין מקדם מספרי סופי וחיובי. אפס אינו מותר.");
        var before = Compute(context, null, replaceOwned: false);
        var after = Compute(context, remove ? null : factor, replaceOwned: true);
        var raw = context.Inputs.Sum(input => EstimateBoqSemantics.CanonicalQuantity(input.Raw));
        return new(raw, before, after, context.Inputs.Count);
    }

    public static Decision? Approve(Context context, double? factor, bool remove, string? basis, string? source,
        string? reason, string? approvedBy, DateTime approvedAtUtc, bool explicitlyApproved)
    {
        if (!explicitlyApproved) return null;
        if (new[] { basis, source, reason, approvedBy }.Any(string.IsNullOrWhiteSpace) ||
            approvedBy!.IndexOfAny(new[] { '\r', '\n' }) >= 0 || approvedAtUtc.Kind != DateTimeKind.Utc || approvedAtUtc == default)
            throw new InvalidOperationException("נדרשים בסיס מדידה, מקור, נימוק ושם מאשר מפורש. האישור יתועד בזמן UTC.");
        PreviewChange(context, factor, remove);
        return new(context, remove, remove ? null : factor, basis!.Trim(), source!.Trim(), reason!.Trim(),
            approvedBy.Trim(), approvedAtUtc);
    }

    public static Context RequireUnchanged(Context expected, ProjectProfile profile, IReadOnlyList<NeutralQuantityRecord> records)
    {
        var current = Capture(profile, expected.RuleKey, records);
        if (current.ProfileId != expected.ProfileId || current.ProfileHash != expected.ProfileHash ||
            current.RecordsHash != expected.RecordsHash || current.RuleId != expected.RuleId)
            throw new InvalidOperationException("הפרופיל או קבוצת המדידות השתנו מאז פתיחת העורך; לא נשמר מקדם.");
        return current;
    }

    public static ProjectProfileWriter.SaveResult Save(ProjectProfile profile, IReadOnlyList<NeutralQuantityRecord> records,
        Decision decision, string targetPath, ProjectProfileWriter.ExpectedProfileState expectedState)
    {
        var current = RequireUnchanged(decision.Context, profile, records);
        var checkedDecision = Approve(current, decision.Factor, decision.Remove, decision.Basis, decision.Source,
            decision.Reason, decision.ApprovedBy, decision.ApprovedAtUtc, true)!;
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);
        var previous = profile.Estimate.ApprovedAdjustments.ToList();
        try
        {
            // Ownership was proved above; never remove any other rule or candidate.
            profile.Estimate.ApprovedAdjustments.RemoveAll(rule => string.Equals(rule.RuleId, current.RuleId, StringComparison.OrdinalIgnoreCase));
            if (!checkedDecision.Remove)
                profile.Estimate.ApprovedAdjustments.Add(new()
                {
                    RuleId = current.RuleId, Scope = "rule:" + current.RuleKey, Order = current.Order,
                    Factor = checkedDecision.Factor, Formula = OwnerMarker + checkedDecision.Basis,
                    Source = checkedDecision.Source, Reason = checkedDecision.Reason, Status = "CONFIRMED",
                    ApprovedBy = checkedDecision.ApprovedBy, ApprovedAtUtc = checkedDecision.ApprovedAtUtc,
                });
            if (EstimateConfigurationPolicy.Validate(profile).Any(EstimatePreflightPolicy.IsBlocking))
                throw new InvalidOperationException("המקדם יוצר התנגשות בפרופיל; השינוי לא נשמר.");
            return ProjectProfileWriter.Save(profile, targetPath,
                (checkedDecision.Remove ? "remove owned quantity factor: " : "explicit rule quantity factor: ") + current.RuleKey +
                "; basis=" + checkedDecision.Basis + "; source=" + checkedDecision.Source + "; reason=" + checkedDecision.Reason,
                checkedDecision.ApprovedBy, expectedState);
        }
        catch
        {
            profile.Estimate.ApprovedAdjustments.Clear(); profile.Estimate.ApprovedAdjustments.AddRange(previous);
            throw;
        }
    }

    private static Existing Detach(ProjectProfile.EstimateProfile.AdjustmentRule value, bool candidate) =>
        new(value.RuleId ?? "", value.Factor, value.Formula, value.Source, value.Reason, value.Status,
            value.Scope, value.Order, value.ApprovedBy, value.ApprovedAtUtc, candidate);
    private static ProjectProfile.EstimateProfile.AdjustmentRule Restore(Existing value) => new()
    {
        RuleId = value.Id, Factor = value.Factor, Formula = value.Formula, Source = value.Source,
        Reason = value.Reason, Status = value.Status, Scope = value.Scope, Order = value.Order,
        ApprovedBy = value.ApprovedBy, ApprovedAtUtc = value.ApprovedAtUtc,
    };
    private static decimal Compute(Context context, double? factor, bool replaceOwned)
    {
        var rules = context.ExistingRules.Where(rule => !rule.Candidate && (!replaceOwned || rule.Id != context.Owned?.Id))
            .Select(Restore).ToList();
        if (factor != null) rules.Add(new()
        {
            RuleId = context.RuleId, Factor = factor, Scope = "rule:" + context.RuleKey, Order = context.Order,
            Status = "CONFIRMED", ApprovedBy = "PREVIEW-ONLY-NOT-SAVED", ApprovedAtUtc = DateTime.UnixEpoch,
        });
        decimal total = 0;
        foreach (var input in context.Inputs)
        {
            var outcome = AdjustmentEngine.Apply(input.Raw, rules, Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                input.RecordId, context.RuleKey, input.CatalogCode);
            if (outcome.Findings.Any(EstimatePreflightPolicy.IsBlocking) || !double.IsFinite(outcome.BoqValue) || outcome.BoqValue <= 0)
                throw new InvalidOperationException("לא ניתן לחשב הצטברות מקדמים תקינה; לא יוסתר כלל קיים ולא תיווצר כמות אפס.");
            total = checked(total + EstimateBoqSemantics.CanonicalQuantity(outcome.BoqValue));
        }
        return total;
    }
}
