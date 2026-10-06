using System;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool NeedsEstimateProjectStart => EstimateProjectStartService.NeedsStart(_profile, _profileWriteState);

    /// <summary>Review first-profile identity or a missing discipline. Existing source decisions are preserved.</summary>
    private bool EnsureEstimateProjectForScan(Document document)
    {
        if (!NeedsEstimateProjectStart) return true;
        var writeAttempted = false;
        try
        {
            var scope = CaptureProfileDecisionScope("התחלת אומדן בפרויקט חדש");
            if (!ReferenceEquals(document, scope.Document))
                throw new InvalidOperationException("השרטוט הפעיל השתנה לפני התחלת האומדן");
            var inventory = EstimateSourceInventoryService.Capture(document);
            ScanDatabaseChangeProbe.SampleDbmod(document, "inventory.capture");
            if (!inventory.IsComplete)
                throw new InvalidOperationException("לא ניתן להציג רשימת מקורות מלאה: " + inventory.ReadFailure);
            var dialog = new EstimateProjectStartDialog(scope.Profile.ProfileId, scope.Profile.ProjectName,
                scope.Identity.DrawingPath, scope.ExpectedState.TargetPath, inventory.Text, ApproverContext.Session.Name,
                completingExistingProfile: scope.ExpectedState.SourceExisted);
            CivilModalHost.ShowFromPalette(dialog);
            if (dialog.ApprovedDecision == null)
            {
                SetStatus("תחילת האומדן בוטלה — לא נשמר פרופיל ולא התחילה מדידה");
                return false;
            }
            RequireProfileDecisionScope(scope);
            writeAttempted = true;
            var saved = EstimateProjectStartService.Save(_estimate, scope.Profile, scope.ExpectedState, dialog.ApprovedDecision)
                ?? throw new InvalidOperationException("לא התקבל אישור להתחלת האומדן");
            PublishSavedProfile(saved);
            if (!ReferenceEquals(document, Doc()))
                throw new InvalidOperationException("הפרופיל נשמר אך השרטוט הפעיל השתנה; לא התחילה מדידה");
            InvalidateEstimateEvidence("פרופיל האומדן נשמר — מתחיל סריקת כמויות; טרם בוצע שיוך או תמחור");
            Log($"נשמר פרופיל אומדן {scope.Profile.ProfileId} באישור {dialog.ApprovedDecision.ApprovedBy}; " +
                $"תחום: {(dialog.ApprovedDecision.Discipline == "landscape" ? "פיתוח נופי וגינון" : "כבישים ותנועה")}; ללא הגדרת CL/חתכים.");
            return true;
        }
        catch (Exception ex)
        {
            if (writeAttempted)
            {
                InvalidateEstimateEvidence("תחילת האומדן לא הושלמה — יש לבדוק את פרופיל הפרויקט לפני המשך");
                ReloadProfile();
            }
            ShowError("תחילת האומדן לא הושלמה", ex);
            return false;
        }
        finally { RefreshGates(); }
    }
}
