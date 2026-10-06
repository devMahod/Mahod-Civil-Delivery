using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Separate mapping, measured evidence and built money; never grants export permission. Four display axes
/// (Codex 8D19CD93 §6): local measurement, project coverage, mapping and pricing. A blocking finding without any
/// record or source identity (e.g. an unresolved XREF traversal) is project coverage: it still blocks every line
/// exactly as before (<see cref="State.MeasurementNeedsReview"/>), but it is not shown as each group's own failure.
/// </summary>
public static class EstimateQuantityPresentationPolicy
{
    /// <param name="MeasurementNeedsReview">Local review OR project coverage: unchanged gate meaning.</param>
    /// <param name="LocalMeasurementNeedsReview">This group's own records or record/source-bound findings.</param>
    /// <param name="ProjectCoverageBlockers">Distinct identity-less blocking findings that also block this group.</param>
    public sealed record State(string Summary, string Detail, string? PriceDisplay, string Severity,
        bool MappingApproved, bool MeasurementNeedsReview, bool BuiltReady,
        bool LocalMeasurementNeedsReview = false, int ProjectCoverageBlockers = 0);

    /// <summary>
    /// Codes that never make a group "בדיקת מדידה": the unmapped marker and the scope / XREF-policy / earthworks
    /// decisions, which are handled by their own decisions and gates.
    /// </summary>
    public static bool IsReviewExempt(DeliveryFinding finding) =>
        finding.Code == EstimateFindingCodes.Unmapped ||
        finding.Code == EstimateFindingCodes.SourceScopePolicyUnapproved ||
        finding.Code == EstimateFindingCodes.XrefPolicyUnapproved ||
        finding.Code == EstimatePreflightPolicy.EarthworksNotAssessedCode;

    /// <summary>The XREF source-coverage codes: an unreadable reference, not a measurement of any one group.</summary>
    private static readonly HashSet<string> SourceCoverageCodes = new(StringComparer.Ordinal)
    {
        EstimateFindingCodes.XrefTraversalUnresolved, EstimateFindingCodes.XrefCycle, EstimateFindingCodes.XrefTransformInvalid,
    };

    /// <summary>
    /// Project source coverage: an XREF coverage finding that names no record and no source. Any other identity-less
    /// blocker (units, configuration, measurement) stays a local, fail-closed review — review wf_4f034e59 AX-1.
    /// </summary>
    public static bool IsProjectWide(DeliveryFinding finding) =>
        finding.AffectedRecordIds.Count == 0 && finding.SourceRefs.Count == 0 && SourceCoverageCodes.Contains(finding.Code);

    public static State Evaluate(IReadOnlyList<NeutralQuantityRecord> records,
        IEnumerable<DeliveryFinding> scanFindings, string? approvedCode,
        CatalogSnapshot? catalog, EstimateResult? result, bool ignored, bool sourceFresh,
        bool estimateExportReady, IReadOnlyList<EstimateLine>? groupLines = null,
        EstimateFindingImpactPolicy.Index? findingImpactIndex = null)
    {
        if (ignored) return new("לא רלוונטי", "החרגה מפורשת; הכמות נשמרת במעקב ואינה מתומחרת.",
            null, "muted", false, false, false);
        var ids = records.Select(record => record.RecordId).ToHashSet(StringComparer.Ordinal);
        var impacts = findingImpactIndex ?? EstimateFindingImpactPolicy.CreateIndex(scanFindings);
        var applicable = records.SelectMany(record => record.Findings)
            .Concat(records.SelectMany(impacts.BlockingFindings)).ToList();
        var recordChecks = records.Count == 0 || records.Any(record =>
            !double.IsFinite(record.Measurement.RawValue) || record.Measurement.RawValue <= 0 ||
            !CatalogIdentity.IsValidSha256(record.Source.DrawingHash) || string.IsNullOrWhiteSpace(record.Source.Handle) ||
            record.Status >= DeliveryStatus.ReviewRequired &&
                !record.Findings.Any(finding => finding.Code == EstimateFindingCodes.Unmapped));
        var blocking = applicable.Where(finding => EstimatePreflightPolicy.IsBlocking(finding) &&
                !IsReviewExempt(finding)).ToList();
        var ownFindings = records.SelectMany(record => record.Findings).ToHashSet();
        var coverage = blocking.Where(finding => IsProjectWide(finding) && !ownFindings.Contains(finding))
            .Distinct().ToList();
        var localReview = recordChecks || blocking.Any(finding => !coverage.Contains(finding));
        var measurementReview = localReview || coverage.Count > 0;
        CatalogItem? item = null;
        var mapped = !string.IsNullOrWhiteSpace(approvedCode) && catalog != null &&
            catalog.Items.TryGetValue(approvedCode, out item);
        var unitMismatch = mapped && records.Any(record => !Units.Parse(record.Measurement.Unit).SameUnit(item!.Unit));
        var mappingText = !mapped ? "דרוש שיוך" : unitMismatch ? "יחידות שונות" : "שיוך מאושר";
        var lines = result == null ? null : groupLines?.ToList() ?? result.Lines.Where(line => ids.Contains(line.RecordId)).ToList();
        string? price = null;
        var pricing = "טרם נבנה";
        var builtReady = false;
        if (result != null)
        {
            var complete = lines!.Count == records.Count && lines.Select(line => line.RecordId).Distinct().Count() == records.Count;
            var prices = lines.Select(line => line.Price).Distinct().ToList();
            var priceKinds = lines.Select(line => line.PriceStatus).Distinct().ToList();
            price = complete && prices.Count == 1 && prices[0] is > 0
                ? prices[0]!.Value.ToString("N2") : prices.Count > 1 ? "מעורב" : null;
            builtReady = complete && lines.Count > 0 && prices.Count == 1 && priceKinds.Count == 1 &&
                lines.All(line => line.Status == DeliveryStatus.Ready &&
                line.IncludedInTotals && line.Total != null && line.Price > 0 &&
                line.PriceStatus is PriceStatus.Priced or PriceStatus.ProjectOverride &&
                !line.Findings.Any(EstimatePreflightPolicy.IsBlocking));
            pricing = !complete ? "תוצאת בנייה חלקית" : prices.Count > 1 || priceKinds.Count > 1 ? "תמחור מעורב — בדוק פירוט" :
                lines.Any(line => line.PriceStatus == PriceStatus.MissingPrice || line.Price == null) ? "חסר מחיר" :
                priceKinds.Contains(PriceStatus.ProjectOverride) ? "מחיר פרויקט מהבנייה" : "מחיר מהבנייה";
            if (!builtReady) pricing += " · חסום";
        }
        else if (mapped && !unitMismatch)
        {
            if (catalog!.Prices.TryGetValue(approvedCode!, out var catalogPrice) && catalogPrice.Price is > 0)
            {
                price = catalogPrice.Price.Value.ToString("N2");
                pricing = "מחירון בלבד — טרם נבנה";
            }
            else pricing = "חסר מחיר במחירון";
            if (coverage.Count > 0) pricing += " · תמחור חסום בכיסוי המקורות";
        }
        var measurementText = localReview ? "דרושה בדיקת מקור/מדידה"
            : coverage.Count > 0 ? "לא דווח כשל מקומי; כיסוי המקורות טרם הושלם" : "נמדדה; אינה אישור אומדן";
        // Shared blockers that are not source coverage are named, never hidden behind "no general blocker" (AX-2).
        var otherShared = applicable.Where(finding => EstimatePreflightPolicy.IsBlocking(finding) &&
                finding.Code != EstimateFindingCodes.Unmapped && finding.AffectedRecordIds.Count == 0 &&
                !ownFindings.Contains(finding) && !coverage.Contains(finding))
            .Select(finding => finding.Code).Distinct(StringComparer.Ordinal).ToList();
        var coverageText = coverage.Count == 0
            ? otherShared.Count > 0
                ? "חסמים שאינם כיסוי XREF החלים על קבוצה זו (" + string.Join(", ", otherShared) + ") — ראה דוח הממצאים והחלטות ההיקף"
                : "לא דווח חסם כללי"
            : $"{coverage.Count:N0} ממצאים כלליים (" +
              string.Join(", ", coverage.Select(finding => finding.Code).Distinct(StringComparer.Ordinal)) +
              ") — תמחור ואומדן מלא חסומים עד שהמקור יושלם; אין זה כשל מדידה של הקבוצה";
        var detail = $"שיוך: {mappingText}\nמדידה מקומית: {measurementText}\nכיסוי פרויקט: {coverageText}\nתמחור: {pricing}";
        if (!sourceFresh) detail += "\nהראיות אינן עדכניות — אין אישור פעולה על סמך שורה זו.";
        else if (result != null && !estimateExportReady) detail += "\nהאומדן הכולל חסום; בדוק את דוח הממצאים המלא.";
        var pricedDraft = sourceFresh && mapped && !unitMismatch && !measurementReview && builtReady;
        var ready = pricedDraft && estimateExportReady;
        // Only a local problem hides the catalog price; a coverage-only block keeps it visible with its note above.
        if (localReview || !sourceFresh || result != null && !builtReady && price != "מעורב") price = null;
        var summary = !sourceFresh ? "מדידה לא עדכנית" : localReview ? "בדיקת מדידה" :
            !mapped ? "דרוש שיוך" : unitMismatch ? "יחידות שונות" :
            coverage.Count > 0 ? "שויך · כיסוי חסר" : ready ? "מתומחר ותקין" :
            pricedDraft ? "מתומחר לטיוטה" :
            result != null ? "בדיקת אומדן" : pricing.StartsWith("חסר", StringComparison.Ordinal) ? "חסר מחיר" : "שיוך מאושר";
        return new(summary, detail, price, ready ? "ok" : "review", mapped && !unitMismatch, measurementReview, ready,
            localReview, coverage.Count);
    }
}
