using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MessageBox = System.Windows.MessageBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _familyReviewRunning;

    private void RefreshFamilyReview(bool profileUsable, bool scanFresh)
    {
        BtnFamilyReview.IsEnabled = !_familyReviewRunning && !_engineerDraftExporting && profileUsable && _profile != null &&
                                    scanFresh && _scan is { Records.Count: > 0 };
        RefreshEditionLinks(profileUsable);
    }

    /// <summary>
    /// Recognition review of a fresh scan: what the drawing evidence says the unmapped design groups are, one engineer
    /// decision per family (and cited evidence) for every group checked, and the profile's saved decisions, one of which
    /// can be revoked. One action per dialog close. Saving goes through the profile CAS and rebinds the scan.
    /// </summary>
    private void OnFamilyReview(object sender, RoutedEventArgs e)
    {
        var capturedScan = _scan;
        var profile = _profile;
        var doc = Doc();
        if (doc == null || capturedScan == null || profile == null || _familyReviewRunning) return;
        _familyReviewRunning = true;
        BtnFamilyReview.IsEnabled = false;
        var saving = false;
        try
        {
            EngineerBoqDraft? draft = null;
            RunBusy("מזהה שכבות לפי ראיות מהשרטוט…", () => draft = _estimate.BuildRecognitionReview(doc, capturedScan, profile));
            // The saved decisions are listed from the same profile the draft resolved them against.
            var model = FamilyDecisionReviewModel.Create(draft ?? throw new InvalidOperationException("הזיהוי לא החזיר תוצאה."),
                profile.Estimate.FamilyDecisions);
            if (draft!.Catalog.SnapshotId == EstimateWorkflowService.NoCatalogSnapshotId)
            {
                const string noCatalog = "אין מחירון פעיל: הזיהוי מציג משפחות בלבד, בלי סעיפים ומחירים. החלטת משפחה אינה תלויה במחירון; " +
                                         "סעיפים יוצעו אחרי טעינת מחירון.";
                Log(noCatalog);
                SetStatus(noCatalog);
            }
            if (model.Rows.Count == 0 && model.StaleLines.Count == 0 && model.SavedDecisions.Count == 0)
            {
                RtlMessageBox.Show("אין שכבות תכנון שלא שויכו, ואין החלטות משפחה שמורות לבדיקה.", "זיהוי שכבות",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // The assistant is offered only when configured on this machine; it answers for one group at a time on
            // an explicit click, and images need the organisation policy plus the engineer's permit per image.
            FamilyDecisionsDialog.FamilyAssistant? assistant = null;
            if (SemanticMappingAssistantConfiguration.IsConfigured)
            {
                var provider = new FamilyRecognitionAssist(FamilyAssistProviderAdmission.Wrap(SemanticMappingAssistantConfiguration.CreateFamilyProvider()));
                var catalog = draft!.Catalog;
                var library = draft.Library;
                assistant = (group, local, context, vision, token) =>
                    provider.AssistAsync(group, library, catalog, local, context, vision, token);
            }
            var dialog = new FamilyDecisionsDialog(model, draft!.Library, assistant,
                assistant != null && SemanticMappingAssistantConfiguration.IsVisionEnabled);
            var closed = CivilModalHost.ShowFromPalette(dialog);
            if (closed == true && !ReferenceEquals(capturedScan, _scan))
                throw new InvalidOperationException("הסריקה התחלפה בזמן הבדיקה — לא נשמר דבר. יש לפתוח את החלון מחדש.");
            if (closed == true && dialog.ReviewAction == FamilyReviewAction.RevokeDecision)
            {
                // Same boundary and recovery as the approval below: fresh scan, isolated profile copy, CAS write, rebase.
                var target = dialog.RevokeTarget
                    ?? throw new InvalidOperationException("לא נבחרה החלטה שמורה לביטול — לא נשמר דבר.");
                var revokeReason = dialog.RevokeReason;
                EstimateWorkflowService.RequireFreshForDecision(doc, capturedScan, "ביטול החלטת משפחה");
                var profileForRevoke = CloneProfileForDecision(profile);
                saving = true;
                var revoked = _estimate.RevokeFamilyDecision(profileForRevoke, capturedScan, target.DecisionId, revokeReason,
                    dialog.ApprovedBy, RequireProfileWriteTarget(), capturedScan.ProfileWriteState ?? throw new InvalidOperationException(
                        "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"));
                ContinueEstimateReviewAfterDecision(revoked, profileForRevoke, (IReadOnlyList<string>?)null, string.Empty);
                var returned = target.AppliedGroups + target.StaleGroups;
                Log($"בוטלה החלטת המשפחה '{target.Family}' ({target.ShortId}); היא כיסתה {returned} קבוצות בסריקה " +
                    $"(גרסת פרופיל {revoked.NewVersion}).");
                SetStatus($"בוטלה החלטת המשפחה '{target.Family}'. קבוצות שכיסתה ואין להן כלל, שיוך או החלטה אחרת יוצגו שוב בזיהוי השכבות.");
                return;
            }
            if (closed == true && dialog.ReviewAction != FamilyReviewAction.ApproveFamilies)
                throw new InvalidOperationException("החלון נסגר בלי פעולה מוגדרת — לא נשמר דבר.");
            var batches = dialog.Batches;
            var requests = FamilyDecisionRequests(batches, dialog.Reason);
            ProjectProfile? profileForSave = null;
            ProjectProfileWriter.SaveResult? saved = null;
            if (!CommitFamilyApprovals(closed, dialog.ReviewAction, requests, approved =>
                {
                    EstimateWorkflowService.RequireFreshForDecision(doc, capturedScan, "אישור משפחות");
                    profileForSave = CloneProfileForDecision(profile);
                    saving = true;
                    saved = _estimate.SaveFamilyDecisions(profileForSave, draft!.Library, capturedScan, approved,
                        dialog.ApprovedBy, RequireProfileWriteTarget(), capturedScan.ProfileWriteState ?? throw new InvalidOperationException(
                            "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"));
                }))
            {
                SetStatus("זיהוי השכבות נסגר בלי שמירה");
                return;
            }
            var savedResult = saved ?? throw new InvalidOperationException("האישור לא החזיר תוצאת שמירה.");
            ContinueEstimateReviewAfterDecision(savedResult, profileForSave!, (IReadOnlyList<string>?)null, string.Empty);
            var partitions = batches.Count(b => b.Partition != null);
            var scope = $"{requests.Count} החלטות משפחה ל-{batches.Sum(b => b.Groups.Count)} קבוצות" +
                        (partitions > 0 ? $" (מהן {partitions} תת־קבוצות מוכחות של קבוצות מעורבות)" : string.Empty);
            Log($"נשמרו {scope} (גרסת פרופיל {savedResult.NewVersion}).");
            SetStatus($"נשמרו {scope}. הטיוטה הבאה תשתמש בהן.");
        }
        catch (Exception ex)
        {
            if (saving)
            {
                // Same recovery as the other profile decisions: never continue on an optimistic in-memory profile.
                InvalidateEstimateEvidence("לא ניתן להמשיך מאותה סריקה — יש להריץ סריקה חדשה");
                ReloadProfile();
            }
            ShowError("זיהוי שכבות ואישור משפחות", ex);
        }
        finally
        {
            _familyReviewRunning = false;
            RefreshGates();
        }
    }

    /// <summary>
    /// The one place a closed family review becomes a profile write. Only a dialog closed with OK for the approve action
    /// calls <paramref name="save"/>, once, with every request together; a cancelled or dismissed dialog (any result but
    /// true) or another action saves nothing, whatever was checked, chosen or typed before. Returns whether it saved.
    /// </summary>
    internal static bool CommitFamilyApprovals(bool? dialogResult, FamilyReviewAction action,
        IReadOnlyList<EstimateWorkflowService.FamilyDecisionRequest> requests,
        Action<IReadOnlyList<EstimateWorkflowService.FamilyDecisionRequest>> save)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(save);
        if (dialogResult != true || action != FamilyReviewAction.ApproveFamilies) return false;
        if (requests.Count == 0)
            throw new InvalidOperationException("לא סומנו קבוצות לאישור — לא נשמר דבר.");
        save(requests);
        return true;
    }

    /// <summary>
    /// The service requests of the checked rows: one whole-group request per batch, and for a proven partition a request that
    /// names only its whole measured group and its records (the service rebuilds both from the scan and never trusts a group
    /// the dialog trimmed). The audit reason is the engineer's, followed by how the family was chosen.
    /// </summary>
    internal static IReadOnlyList<EstimateWorkflowService.FamilyDecisionRequest> FamilyDecisionRequests(
        IReadOnlyList<FamilyApprovalBatch> batches, string reason) =>
        batches.Select(batch => batch.Partition is { } partition
                ? EstimateWorkflowService.FamilyDecisionRequest.ForPartition(batch.FamilyId, partition.WholeGroupId, partition.RecordIds,
                    reason + ProvenanceNote(batch))
                : new EstimateWorkflowService.FamilyDecisionRequest(batch.FamilyId, batch.Groups, batch.EvidenceKeys,
                    reason + ProvenanceNote(batch)) { VisualBinding = FamilyVisualBindingPolicy.Clone(batch.VisualBinding) })
            .ToList();

    /// <summary>
    /// How one saved decision's family was chosen, for the audit reason. Every decision covers groups of one provenance,
    /// and a decision that cites no CAD evidence says that it binds to the source and layer only. A partition decision
    /// records the scope that was shown before the approval.
    /// </summary>
    private static string ProvenanceNote(FamilyApprovalBatch batch) =>
        (batch.FromAiSuggestion ? " · נבחרה לפי הצעת עוזר ה-AI, אחרי בדיקה"
            : batch.OverridesProposal ? " · המשפחה נבחרה ידנית (שונה מההצעה או ללא הצעה)" : string.Empty) +
        (batch.VisualBinding != null ? " · פירוש חזותי שנבדק: קשור לתמונה, למקור ולהיקף העצמים המדויק" :
            batch.EvidenceKeys.Count == 0 ? " · ללא ראיית CAD יציבה: ההחלטה נקשרת למקור ולשכבה בלבד" : string.Empty) +
        (batch.Partition is { } partition
            ? $" · תת־קבוצה מוכחת: {partition.RecordIds.Count} מתוך {partition.WholeGroupRecords} העצמים בקבוצה; " +
              "הכלל חל גם על עצמים חדשים באותו מקור, שכבה ובסיס מדידה שנושאים את אותן ראיות"
            : string.Empty);
}
