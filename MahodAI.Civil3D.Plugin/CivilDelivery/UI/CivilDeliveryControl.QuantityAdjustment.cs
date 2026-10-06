using System;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void OnEditQuantityAdjustment(object sender, RoutedEventArgs e)
    {
        RefreshGates();
        if (!BtnQuantityAdjustment.IsEnabled || _scan == null || _profile == null ||
            QuantitiesGrid.SelectedItem is not QuantityRowViewModel row || row.IsHistorical || row.IsIgnored ||
            !_quantityRows.Contains(row)) return;
        var preserveVerifiedScan = false;
        try
        {
            var scan = _scan;
            var scope = CaptureProfileDecisionScope("עריכת מקדם כמות", scan);
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "עריכת מקדם כמות");
            preserveVerifiedScan = true;
            // The engine matches rule scopes case-insensitively. Preview/save must
            // include every such row, not only the case variant selected in the grid.
            var records = scan.Records.Where(record => string.Equals(record.Classification.RuleKey,
                row.RuleKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            var context = QuantityAdjustmentReviewPolicy.Capture(scope.Profile, row.RuleKey, records);
            var dialog = new QuantityAdjustmentReviewDialog(context);
            if (CivilModalHost.ShowFromPalette(dialog) != true || dialog.Decision == null) return;

            preserveVerifiedScan = false;
            RequireProfileDecisionScope(scope);
            if (!ReferenceEquals(_scan, scan) || row.IsHistorical || row.IsIgnored || !_quantityRows.Contains(row))
                throw new InvalidOperationException("קבוצת הכמות השתנתה בזמן העריכה; לא נשמר מקדם.");
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "שמירת מקדם כמות");
            QuantityAdjustmentReviewPolicy.RequireUnchanged(context, scope.Profile,
                scan.Records.Where(record => string.Equals(record.Classification.RuleKey,
                    row.RuleKey, StringComparison.OrdinalIgnoreCase)).ToArray());
            var profileForSave = CloneProfileForDecision(scope.Profile);
            var saved = QuantityAdjustmentReviewPolicy.Save(profileForSave, records, dialog.Decision,
                RequireProfileWriteTarget(), scan.ProfileWriteState ??
                throw new InvalidOperationException("לסריקה אין ראיית CAS מקורית של הפרופיל."));
            ContinueEstimateReviewAfterDecision(saved, profileForSave, mappedRuleKey: null,
                previousRuleKey: row.RuleKey, preservePreviousSelection: true);
            Log($"{(dialog.Decision.Remove ? "הוסר" : "נשמר")} מקדם הקבוצה {row.RuleKey} על ידי {dialog.Decision.ApprovedBy}; המדידות המקוריות לא השתנו.");
            SetStatus("החלטת המקדם נשמרה — נדרשת בניית אומדן מחדש; המדידה המקורית נשמרה");
        }
        catch (Exception ex)
        {
            if (!preserveVerifiedScan)
            {
                InvalidateEstimateEvidence("עריכת המקדם לא הושלמה — יש לאמת מקורות ולסרוק מחדש לפני המשך.");
                ReloadProfile();
            }
            Log("עריכת מקדם נעצרה ללא אישור נוסף: " + ex.Message);
            RtlMessageBox.Show(ex.Message, "מקדם הכמות לא נשמר", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshGates(); }
    }
}
