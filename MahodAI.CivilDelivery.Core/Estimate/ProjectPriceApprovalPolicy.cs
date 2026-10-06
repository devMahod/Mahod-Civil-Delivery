using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Explicit human price evidence, bound to the exact item shown. No quote generation.</summary>
public static class ProjectPriceApprovalPolicy
{
    public sealed record Context(string ProfileId, string EffectiveProfileHash, string CatalogId,
        string CatalogHash, string ItemCode, string ItemFingerprint, string Description, string Unit);
    public sealed record Approval(Context Context, decimal Price, string Source, string Reason,
        string ApprovedBy, DateTime ApprovedAtUtc);

    public static Context Capture(ProjectProfile profile, CatalogSnapshot catalog, string itemCode, string measuredUnit)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(itemCode) || itemCode.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new InvalidOperationException("נדרשים פרויקט וסעיף מזוהים לאישור מחיר.");
        if (!CatalogIdentity.TryGetActiveProfileIdentity(profile, out var active, out var errors) || active == null ||
            !CatalogIdentity.SnapshotMatches(active, catalog))
            throw new InvalidOperationException("זהות המחירון אינה תואמת לפרופיל: " + string.Join("; ", errors));
        var matches = catalog.Items.Values.Where(item => string.Equals(item.Code.Trim(), itemCode.Trim(),
            StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("נדרש סעיף מחירון יחיד ומזוהה.");
        var item = matches[0];
        if (!Units.Parse(measuredUnit).SameUnit(item.Unit))
            throw new InvalidOperationException("יחידת הכמות אינה תואמת ליחידת המחיר; מחיר פרויקט אינו המרת יחידות.");
        return new(profile.ProfileId, EstimateTraceIdentity.EffectiveProfileHash(profile), active.SnapshotId,
            active.FileHash, item.Code, CatalogIdentity.ItemFingerprint(item), item.Description, item.Unit.Canonical);
    }

    /// <summary>
    /// Captures an item-price decision context for an already classified group, even
    /// before estimate construction or when its catalog price is missing. This does
    /// not approve measurements, check live-source freshness or grant export authority.
    /// The host still verifies current profile mappings and the original decision scope.
    /// </summary>
    public static Context CaptureForRecords(ProjectProfile profile, CatalogSnapshot catalog,
        string itemCode, IReadOnlyList<NeutralQuantityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            throw new InvalidOperationException("נדרשת קבוצת כמויות לא ריקה לאישור מחיר פרויקט.");
        foreach (var record in records)
            if (record == null || record.Classification == null || record.Measurement == null ||
                !CatalogIdentity.IsClassificationCurrent(record.Classification, catalog) ||
                !string.Equals(record.Classification.CandidateCatalogCode, itemCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("יש לאשר תחילה שיוך יחיד ועדכני לסעיף; מחיר אינו מתקן שיוך חסר או מעורב.");

        var context = Capture(profile, catalog, itemCode, records[0].Measurement.Unit);
        foreach (var record in records)
            if (!Units.Parse(record.Measurement.Unit).SameUnit(Units.Parse(context.Unit)))
                throw new InvalidOperationException("בקבוצה יש יחידות שונות; אין לאשר להן מחיר יחידה יחיד.");
        return context;
    }

    public static IReadOnlyList<string> Validate(decimal? price, string? source, string? reason, string? approvedBy,
        DateTime approvedAtUtc)
    {
        var errors = new List<string>();
        if (price is not > 0) errors.Add("יש להזין מחיר חיובי ליחידת הסעיף; מחיר חסר אינו אפס.");
        if (string.IsNullOrWhiteSpace(source)) errors.Add("יש לציין מקור למחיר — למשל הצעת ספק מזוהה ותאריך.");
        if (string.IsNullOrWhiteSpace(reason)) errors.Add("יש לנמק את מחיר הפרויקט.");
        if (string.IsNullOrWhiteSpace(approvedBy)) errors.Add("יש להזין את שם המאשר במפורש.");
        else if (approvedBy.IndexOfAny(new[] { '\r', '\n' }) >= 0) errors.Add("שם המאשר חייב להיות בשורה אחת.");
        if (approvedAtUtc == default || approvedAtUtc.Kind != DateTimeKind.Utc)
            errors.Add("אישור מחייב חותמת זמן UTC.");
        return errors;
    }

    public static Approval? Approve(Context context, decimal? price, string? source, string? reason,
        string? approvedBy, DateTime approvedAtUtc, bool explicitlyApproved)
    {
        if (!explicitlyApproved) return null;
        var errors = Validate(price, source, reason, approvedBy, approvedAtUtc);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return new(context, price!.Value, source!.Trim(), reason!.Trim(), approvedBy!.Trim(), approvedAtUtc);
    }

    public static void RequireUnchanged(Context expected, ProjectProfile profile, CatalogSnapshot catalog)
    {
        var current = Capture(profile, catalog, expected.ItemCode, expected.Unit);
        if (current != expected)
            throw new InvalidOperationException("הפרופיל, המחירון או הסעיף השתנו מאז פתיחת אישור המחיר; הבחירה לא נשמרה.");
    }

    public static bool HasCatalogBinding(ProjectProfile.EstimateProfile.PriceOverride value) =>
        new[] { value.ApprovedCatalogId, value.ApprovedCatalogHash, value.ApprovedCatalogItemFingerprint, value.ExpectedUnit }
            .Any(part => !string.IsNullOrWhiteSpace(part));

    /// <summary>Legacy code-only decisions retain their previous contract; new UI decisions always carry all four pins.</summary>
    public static bool BindingMatches(ProjectProfile.EstimateProfile.PriceOverride value, CatalogSnapshot catalog, CatalogItem item) =>
        !HasCatalogBinding(value) ||
        (string.Equals(value.ApprovedCatalogId, catalog.SnapshotId, StringComparison.OrdinalIgnoreCase) &&
         CatalogIdentity.IsValidSha256(value.ApprovedCatalogHash) &&
         string.Equals(value.ApprovedCatalogHash, catalog.FileHash, StringComparison.OrdinalIgnoreCase) &&
         CatalogIdentity.IsValidSha256(value.ApprovedCatalogItemFingerprint) &&
         string.Equals(value.ApprovedCatalogItemFingerprint, CatalogIdentity.ItemFingerprint(item), StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrWhiteSpace(value.ExpectedUnit) && Units.Parse(value.ExpectedUnit).SameUnit(item.Unit));
}
