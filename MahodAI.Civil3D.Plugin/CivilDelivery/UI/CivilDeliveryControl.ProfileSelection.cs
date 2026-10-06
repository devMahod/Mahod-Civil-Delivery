using System;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void OnSelectProjectProfile(object sender, RoutedEventArgs e)
    {
        var document = Doc();
        if (document == null) return;
        if (_pendingWorkflowSaveDocument != null || _busyProgress != null)
        {
            SetStatus("בחירת פרופיל ממתינה לסיום הפעולה הפעילה; לא בוטלה שמירה או פעולה ממתינה.");
            return;
        }
        try
        {
            // A template-backed Drawing1 must be saved before the profile picker,
            // not bound to the DWT and rejected after the engineer has reviewed it.
            var identity = DrawingRevisionTracker.CaptureSavedDrawingIdentity(document);
            if (identity.Failure != null) throw new InvalidOperationException(identity.Failure);
            if (!identity.IsSaved)
            {
                EnsureSavedForAction("בחירת פרופיל קיים", OnSelectProjectProfile,
                    requiresLoadedProfile: false);
                return;
            }
            var drawing = identity.DrawingPath;
            var revision = DrawingRevisionTracker.Capture(document.Database);
            var prior = ExistingProjectProfileSelection.Current(document);
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "בחר פרופיל עבודה מקומי קיים — לא מועתק ולא נוצר פרופיל",
                Filter = "Project profile (*.yaml;*.yml)|*.yaml;*.yml", CheckFileExists = true, Multiselect = false,
            };
            if (picker.ShowDialog() != true) return;
            var preview = ExistingProjectProfileSelection.Inspect(picker.FileName);
            var review = new ProjectProfileSelectionDialog(preview, drawing);
            CivilModalHost.ShowFromPalette(review);
            if (!review.Accepted) return;
            var currentIdentity = DrawingRevisionTracker.CaptureSavedDrawingIdentity(document);
            if (!ReferenceEquals(document, Doc()) || _pendingWorkflowSaveDocument != null || _busyProgress != null ||
                !currentIdentity.IsSaved ||
                !string.Equals(drawing, currentIdentity.DrawingPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(revision, DrawingRevisionTracker.Capture(document.Database), StringComparison.Ordinal))
                throw new InvalidOperationException("השרטוט או הפעולה הפעילה השתנו בזמן הבדיקה. לא נבחר פרופיל אחר.");
            ExistingProjectProfileSelection.RequireUnchanged(preview);
            if (!ReferenceEquals(prior, ExistingProjectProfileSelection.Current(document)))
                throw new InvalidOperationException("בחירת הפרופיל השתנתה בזמן הבדיקה; פתח את הבחירה מחדש.");
            if (!ResetForExplicitProjectProfileSelection()) return;
            ExistingProjectProfileSelection.Bind(document, drawing, preview, prior);
            ReloadProfile();
            if (_profile?.ProfileId != preview.ProfileId ||
                !string.Equals(_profileHash, preview.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("לא ניתן לאמת שהפרופיל שנטען זהה לקובץ שנבדק; יש לבדוק ולבחור מחדש. אין תוצאות תקפות מהבחירה הזאת.");
            RefreshDashboard(); RefreshGates();
            Log($"נבחר פרופיל קיים {preview.ProfileId} עבור המסמך הנוכחי בלבד: {preview.Path}");
            SetStatus("הפרופיל הקיים נבחר — נדרשת סריקה או תכנון חדשים; לא אושרו מדידות או מחירים חדשים.");
        }
        catch (Exception ex)
        {
            ShowError("בחירת פרופיל לא הושלמה", ex);
        }
        finally { RefreshGates(); }
    }
}
