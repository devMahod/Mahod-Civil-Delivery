using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void OnApproveProjectPrice(QuantityRowViewModel row)
    {
        if (_scan == null || _profile == null || _catalog == null || row.IsHistorical || row.IsIgnored ||
            !_quantityRows.Contains(row))
            return;
        try
        {
            var scan = _scan;
            var scope = CaptureProfileDecisionScope("אישור מחיר פרויקט", scan);
            var catalog = _catalog;
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "אישור מחיר פרויקט");
            var records = scan.Records.Where(record => record.Classification.RuleKey == row.RuleKey).ToArray();
            if (string.IsNullOrWhiteSpace(row.CatalogCode) ||
                records.Any(record => !EstimateWorkflowService.HasApprovedCatalogMapping(scope.Profile, record)))
                throw new InvalidOperationException("יש לאשר תחילה שיוך יחיד ועדכני לסעיף; מחיר אינו מתקן שיוך חסר או מעורב.");
            var context = ProjectPriceApprovalPolicy.CaptureForRecords(scope.Profile, catalog, row.CatalogCode, records);
            var dialog = new ProjectPriceApprovalDialog(context, scope.Profile.Estimate.ProjectOverrides.Where(value =>
                string.Equals(value.ItemCode?.Trim(), context.ItemCode.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (CivilModalHost.ShowFromPalette(dialog) != true || dialog.Approval == null) return;

            // Never capture a replacement baseline after the modal. Every original
            // document/profile/scan/catalog identity remains part of this decision.
            RequireProfileDecisionScope(scope);
            if (!ReferenceEquals(_scan, scan) || !ReferenceEquals(_catalog, catalog) ||
                row.IsHistorical || !_quantityRows.Contains(row))
                throw new InvalidOperationException("סריקת הכמויות השתנתה בזמן אישור המחיר; לא נשמר שינוי.");
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "אישור מחיר פרויקט");
            var verified = _estimate.LoadCatalog(scope.Profile, scan.ProfileSource);
            if (verified.Snapshot == null || verified.Findings.Any(finding => finding.Severity >= FindingSeverity.ReviewRequired))
                throw new InvalidOperationException("לא ניתן לאמת מחדש את קובץ המחירון; המחיר לא נשמר.");
            ProjectPriceApprovalPolicy.RequireUnchanged(context, scope.Profile, verified.Snapshot);
            // Recheck document/profile after catalog I/O as well, before publication.
            RequireProfileDecisionScope(scope);
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "שמירת מחיר פרויקט");
            var profileForSave = CloneProfileForDecision(scope.Profile);
            var saved = ProjectPriceApprovalWriter.Save(profileForSave, verified.Snapshot, dialog.Approval,
                RequireProfileWriteTarget(), scan.ProfileWriteState ??
                throw new InvalidOperationException("לסריקה אין ראיית CAS מקורית של הפרופיל."));
            // Existing transactional evidence publication preserves measured geometry,
            // resets the old built result, and restores this exact save on failure.
            ContinueEstimateReviewAfterDecision(saved, profileForSave, mappedRuleKey: null,
                previousRuleKey: row.RuleKey, preservePreviousSelection: true);
            Log($"מחיר פרויקט אושר לסעיף {context.ItemCode} על ידי {dialog.Approval.ApprovedBy}; גרסת פרופיל {saved.NewVersion}.");
            SetStatus("מחיר הפרויקט נשמר — נדרשת בניית אומדן מחדש; הכמויות לא נמדדו מחדש");
        }
        catch (Exception ex)
        {
            InvalidateEstimateEvidence("אישור המחיר לא הושלם — יש לאמת מקורות ולסרוק מחדש לפני המשך.");
            ReloadProfile();
            Log("אישור מחיר הפרויקט נעצר: " + ex.Message);
            RtlMessageBox.Show(ex.Message, "מחיר הפרויקט לא אושר", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshGates(); }
    }
}

/// <summary>Presentation only: choose the rebuilt row without changing any decision or quantity.</summary>
internal static class EstimateReviewContinuationSelection
{
    internal static QuantityRowViewModel? Select(
        IReadOnlyList<QuantityRowViewModel> rows, IReadOnlyList<string>? mappedRuleKeys,
        string previousRuleKey, bool preservePreviousSelection = false)
    {
        var previous = rows.FirstOrDefault(candidate =>
            string.Equals(candidate.RuleKey, previousRuleKey, StringComparison.Ordinal));
        // A price edit belongs to the reviewed group. If that exact live row is
        // unavailable, clear the selection rather than implying another row was priced.
        if (preservePreviousSelection)
            return previous is { IsHistorical: false, IsIgnored: false } ? previous : null;

        // Preserve the existing mapping/relevance continuation: an unresolved
        // closed alternative first, then the first unmapped row, then the prior row.
        var next = mappedRuleKeys?.Count == 1 && previous?.AlternativeRuleKey != null
            ? rows.FirstOrDefault(candidate =>
                string.Equals(candidate.RuleKey, previous.AlternativeRuleKey, StringComparison.Ordinal) &&
                !candidate.IsIgnored && string.IsNullOrWhiteSpace(candidate.CatalogCode))
            : null;
        next ??= rows.FirstOrDefault(candidate =>
            !candidate.IsIgnored && string.IsNullOrWhiteSpace(candidate.CatalogCode));
        return next ?? previous;
    }
}
