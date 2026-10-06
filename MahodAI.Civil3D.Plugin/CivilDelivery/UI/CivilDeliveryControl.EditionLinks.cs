using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MessageBox = System.Windows.MessageBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _editionLinksRunning;

    private void RefreshEditionLinks(bool profileUsable) =>
        BtnEditionLinks.IsEnabled = !_editionLinksRunning && profileUsable && _profile != null && _profileSource != null;

    /// <summary>
    /// Links library recipe codes to the active price-list edition, once per edition: the engineer picks the same item
    /// (or none) with the evidence shown, and the links are saved with approver and reason through the profile CAS.
    /// A scan in progress is rebased onto the saved profile (no geometry changes); the draft and the recognition use the
    /// links at once, the mapping proposals at the next scan.
    /// </summary>
    private void OnEditionLinks(object sender, RoutedEventArgs e)
    {
        var profile = _profile;
        var source = _profileSource;
        if (profile == null || source == null || _editionLinksRunning) return;
        _editionLinksRunning = true;
        BtnEditionLinks.IsEnabled = false;
        var saving = false;
        try
        {
            var load = _estimate.LoadCatalog(profile, source);
            if (load.Snapshot == null)
            {
                RtlMessageBox.Show("אין מחירון פעיל מאומת בפרופיל: " + string.Join("; ", load.Findings.Select(f => f.Title)) +
                                "\nיש לטעון מחירון ולנסות שוב.", "קישור ספרייה למהדורה", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var proposals = EstimateWorkflowService.EditionLinkProposals(profile, load.Snapshot);
            var dialog = new EditionLinksDialog(proposals, load.Snapshot, load.StaleEditionLinks, ApproverContext.Session.Name ?? "");
            if (CivilModalHost.ShowFromPalette(dialog) != true)
            {
                SetStatus("קישור הספרייה למהדורה נסגר בלי שמירה");
                return;
            }
            var profileForSave = CloneProfileForDecision(profile);
            saving = true;
            var saved = _estimate.SaveEditionLinks(profileForSave, load.Snapshot, dialog.Requests, dialog.ApprovedBy, dialog.Reason,
                RequireProfileWriteTarget(), CaptureExpectedProfileState());
            if (_scan != null)
                ContinueEstimateReviewAfterDecision(saved, profileForSave, (IReadOnlyList<string>?)null, string.Empty);
            else
                ReloadProfile();
            var text = $"נשמרו {dialog.Requests.Count} קישורי מהדורה ({string.Join(", ", dialog.Requests.Select(r => r.LibraryCode + "→" + r.CatalogCode))}). " +
                       "הטיוטה וזיהוי השכבות משתמשים בהם מעכשיו; הצעות השיוך יתעדכנו בסריקה הבאה.";
            Log(text);
            SetStatus(text);
        }
        catch (Exception ex)
        {
            if (saving)
            {
                InvalidateEstimateEvidence("שמירת קישורי המהדורה לא הושלמה באופן מאומת — יש לסרוק מחדש");
                ReloadProfile();
            }
            ShowError("קישור ספרייה למהדורה", ex);
        }
        finally
        {
            _editionLinksRunning = false;
            RefreshGates();
        }
    }
}
