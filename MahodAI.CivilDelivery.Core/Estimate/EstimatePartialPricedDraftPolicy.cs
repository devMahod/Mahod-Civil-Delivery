using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Explicit incomplete priced output; never changes full-export eligibility.</summary>
public static class EstimatePartialPricedDraftPolicy
{
    public const string PackageKind = "mahod-estimate-partial-priced-draft";
    public const string DraftNotice = "PARTIAL PRICED DRAFT — טיוטה מתומחרת חלקית בלבד; לא אומדן מאושר ולא סכום הפרויקט. כל השורות והממצאים הלא פתורים נשמרו.";
    public const string SubtotalNotice = "סכום ביניים — שורות עצמאיות מתומחרות בלבד; לא סה״כ הפרויקט";

    public sealed record Evaluation(bool CanExport, int EligibleLineCount, int UnresolvedLineCount,
        decimal Subtotal, int ProjectBlockingFindingCount, IReadOnlyList<string> BlockingReasons);

    public static Evaluation Evaluate(EstimateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var fullReasons = EstimatePreflightPolicy.ExportBlockingReasons(result);
        var reasons = fullReasons.Where(reason => reason.StartsWith("trace:", StringComparison.Ordinal) ||
            reason is "source-scope-untrusted" or "xref-scope-untrusted" or "scope-notice-missing" or "no-estimate-lines").ToList();
        if (string.IsNullOrWhiteSpace(result.PriceBookId) || !CatalogIdentity.IsValidSha256(result.PriceBookHash))
            reasons.Add("draft:catalog-identity-missing");
        if (result.SourceSnapshotKind is not (EstimateBuildContext.CivilLiveSaved or EstimateBuildContext.NeutralRecordSet))
            reasons.Add("draft:unknown-snapshot-kind");
        if (result.Lines.Select(line => line.RecordId).Distinct(StringComparer.Ordinal).Count() != result.Lines.Count ||
            result.Lines.Select(line => line.LineId).Distinct(StringComparer.Ordinal).Count() != result.Lines.Count)
            reasons.Add("draft:duplicate-line-identity");
        if (result.Lines.Any(line => string.IsNullOrWhiteSpace(line.LineId) || line.LineId.Contains(':')))
            reasons.Add("draft:ambiguous-line-identity");

        var invalidLineIds = fullReasons.Where(reason => reason.StartsWith("line:", StringComparison.Ordinal))
            .Select(reason => reason.Split(':')[1]).ToHashSet(StringComparer.Ordinal);
        var impacts = EstimateFindingImpactPolicy.CreateIndex(result.Findings);
        var eligible = result.Lines.Where(line => !invalidLineIds.Contains(line.LineId) &&
            line.Status == DeliveryStatus.Ready && line.IncludedInTotals && line.Price is > 0 && line.Total != null &&
            double.IsFinite(line.RawQuantity) && line.RawQuantity > 0 && double.IsFinite(line.BoqQuantity) &&
            (line.BoqQuantity > 0 || IsProvenCanonicalRoundedZero(line)) &&
            !line.Findings.Any(EstimatePreflightPolicy.IsBlocking) && !impacts.BlocksLine(line)).ToList();

        // An apparently included row must not silently vanish at the export boundary.
        if (eligible.Count != result.Lines.Count(line => line.IncludedInTotals)) reasons.Add("draft:included-line-not-eligible");
        if (result.ExcludedLineCount != result.Lines.Count(line => !line.IncludedInTotals))
            reasons.Add("draft:unresolved-line-count-inconsistent");
        decimal subtotal = 0;
        try
        {
            subtotal = EstimateBoqSemantics.CalculateCleanTotal(eligible);
            if (subtotal != result.CleanTotal) reasons.Add("draft:subtotal-inconsistent");
            foreach (var line in eligible)
                if (EstimateBoqSemantics.RoundMoney(EstimateBoqSemantics.StoredQuantity(line.BoqQuantity) * line.Price!.Value) != line.Total)
                    reasons.Add("draft:line-total-inconsistent:" + line.LineId);
        }
        catch (OverflowException) { reasons.Add("draft:monetary-range-exceeded"); }
        if (eligible.Count == 0) reasons.Add("draft:no-independent-priced-lines");
        return new(reasons.Count == 0, eligible.Count, result.Lines.Count - eligible.Count, subtotal,
            result.Findings.Count(EstimatePreflightPolicy.IsBlocking), reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool IsProvenCanonicalRoundedZero(EstimateLine line)
    {
        // Positive measured geometry below the established four-decimal BOQ
        // precision is not a failed/zero measurement. Preserve its raw trace and
        // zero monetary contribution, but never trust a substituted BOQ zero.
        if (line.BoqQuantity != 0 || !double.IsFinite(line.RawQuantity) || line.RawQuantity <= 0)
            return false;
        var value = line.RawQuantity;
        int? previousOrder = null;
        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in line.Adjustments)
        {
            if (string.IsNullOrWhiteSpace(step.RuleId) || !ruleIds.Add(step.RuleId) ||
                string.IsNullOrWhiteSpace(step.ApprovedBy) || step.ApprovedAtUtc == null ||
                step.Factor is not { } factor || !double.IsFinite(factor) || factor <= 0 ||
                !double.IsFinite(step.Input) || step.Input != value ||
                !double.IsFinite(step.Output) || step.Output <= 0 || step.Output != step.Input * factor ||
                (previousOrder.HasValue && step.Order < previousOrder.Value))
                return false;
            value = step.Output;
            previousOrder = step.Order;
        }
        try { return EstimateBoqSemantics.CanonicalQuantity(value) == 0; }
        catch (OverflowException) { return false; }
    }
}
