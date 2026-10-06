using System;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private SectionGuidedActionDecision? _sectionGuidedAction;
    private string? _sectionRecoveryTargetRecordId;
    private bool _applyAwaitsFreshPlan;
    private bool _sectionDrawingOpenPending;

    // A rolled-back APPLY leaves handles of objects that no longer exist (live 07/09:
    // "הצג חתך קיים" was offered for STA-12145 after its transaction aborted and then
    // answered "החתך לא נמצא"). Only a committed apply proves a view.
    private string? SelectedSectionViewHandle(SectionRowViewModel row) =>
        (_apply is { Committed: true } apply
            ? apply.Records.FirstOrDefault(result => result.RecordId == row.Record.RecordId)?.Handles.SectionView
            : null)
        ?? row.Record.ManualSectionReuse?.SectionViewHandle;

    private void RefreshSectionGuidance(bool profileUsable, bool stalePlan)
    {
        var row = SectionsGrid.SelectedItem as SectionRowViewModel;
        var hasView = row != null && !string.IsNullOrWhiteSpace(SelectedSectionViewHandle(row));
        var canLocate = row?.Record.Action == PlanAction.Unchanged &&
                        row.Record.ManualSectionReuse == null && !hasView;
        var globalBlock = WorkflowGate.From(profileUsable, _plan, stalePlan,
            _apply, _previewShown, _verifySummary).GlobalPlanningBlockReason;
        var recoveryRows = _sectionRows.Where(item => _apply is not { Committed: true } && item.Record.Findings.Any(finding =>
            finding.Code == SectionFindingCodes.AnnotationRegistryRepairable)).ToList();
        var unresolvedGlobalFindings = _plan?.Findings.Where(finding =>
            finding.AffectedRecordIds.Count == 0 && IsUnresolvedSectionGuidanceFinding(finding)).ToArray()
            ?? Array.Empty<DeliveryFinding>();
        _sectionRecoveryTargetRecordId = recoveryRows.Count == 1 ? recoveryRows[0].Record.RecordId : null;
        _sectionGuidedAction = SectionGuidedActionPolicy.Evaluate(new SectionGuidedActionSnapshot
        {
            HasDrawing = Doc() != null,
            ProfileUsable = profileUsable,
            NeedsSectionSetup = _profile != null && ProjectSetupService.NeedsSetup(_profile),
            CanRecoverClSource = unresolvedGlobalFindings.Length > 0 &&
                unresolvedGlobalFindings.All(finding => finding.Code == SectionFindingCodes.ClSourceMissing),
            SelectedHasNoAlignmentCrossing = row?.Record.Findings.Any(finding =>
                finding.Code == SectionFindingCodes.ClNoIntersection && IsUnresolvedSectionGuidanceFinding(finding)) == true,
            HasPlan = _plan != null,
            StalePlan = stalePlan,
            PreviewCleanupBlocked = _previewCleanupBlockingStatus != null,
            EvidenceBlocked = _evidenceBlockingStatus != null,
            GlobalPlanningBlockReason = globalBlock,
            CanNavigateToAnnotationRecovery = row?.Record.Status == DeliveryStatus.Ready &&
                _sectionRecoveryTargetRecordId != null && _sectionRecoveryTargetRecordId != row.Record.RecordId,
            RequiresBatchAnnotationRecovery = row?.Record.Status == DeliveryStatus.Ready && recoveryRows.Count > 1,
            RepairingDeadAnnotations = row != null && recoveryRows.Contains(row),
            SelectedRecord = row != null,
            CanChooseCrossing = BtnResolveSection.IsEnabled,
            CanApproveRow = BtnApproveRow.IsEnabled,
            CanNameSpans = BtnNameSpans.IsEnabled && row?.CanNameSpans == true,
            // Editing an already-resolved arrow direction is optional, not a missing prerequisite.
            CanResolveDirection = BtnResolveDirection.IsEnabled && row?.CanResolveTrafficDirection == true,
            CanApplySelected = BtnApplySelected.IsEnabled,
            ApplyAwaitsFreshPlan = _applyAwaitsFreshPlan,
            CanRebuildSelected = BtnApplySelected.IsEnabled && row != null &&
                row.Record.Action == PlanAction.Unchanged,
            CanVerifySelected = BtnVerifySelected.IsEnabled,
            CanRevalidateCreatedView = BtnVerifySelected.IsEnabled,
            HasCreatedView = hasView,
            ManagedViewLookupAvailable = canLocate,
            SelectedVerified = row != null && !stalePlan &&
                _sectionDisplayStatuses.TryGetValue(row.Record.RecordId, out var status) &&
                status == DeliveryStatus.Verified && IsAuthoritativeVerify(_lastVerifyResult),
            PlanRecordCount = _plan?.Records.Count ?? 0,
        });
        SectionStepTitle.Text = (row == null || !string.IsNullOrWhiteSpace(globalBlock)
            ? "" : "חתך " + row.SectionId + " · ") + _sectionGuidedAction.Title;
        SectionStepDetail.Text = _sectionGuidedAction.Detail;
        BtnSectionNext.Content = _sectionGuidedAction.ButtonText;
        BtnSectionNext.IsEnabled = !_sectionDrawingOpenPending && _busyProgress == null &&
            _pendingWorkflowSaveDocument == null &&
            (Doc() != null || _sectionGuidedAction.Action == SectionGuidedActionKind.OpenDrawing);
        BtnShow.Content = hasView ? "הצג חתך קיים" : canLocate ? "אתר חתך קיים" : "הצג מיקום בתכנית";
        BtnShow.ToolTip = hasView
            ? "התמקד בתצוגת החתך הקיימת; ההצגה לבדה אינה אימות."
            : canLocate ? "אתר תצוגה קיימת לפי זהות בעלות ומקור; אין בכך אישור אימות."
            : "מציג את קו ה-CL בתכנית, לא תצוגת חתך.";
        if (_pendingWorkflowSaveDocument != null)
        {
            SectionStepTitle.Text = "ממתין לשמירת השרטוט";
            SectionStepDetail.Text = "לאחר סיום השמירה נחזור לפעולה שאישרת. ביטול השמירה מבטל את ההמשך.";
        }
    }

    private void OnSectionNext(object sender, RoutedEventArgs e)
    {
        RefreshGates();
        if (!BtnSectionNext.IsEnabled || _sectionGuidedAction == null) return;
        try
        {
            switch (_sectionGuidedAction.Action)
            {
                case SectionGuidedActionKind.OpenDrawing: OnOpenSectionDrawing(); break;
                case SectionGuidedActionKind.Plan: OnPlan(sender, e); break;
                case SectionGuidedActionKind.RecoverPreview: OnClearPreview(sender, e); break;
                case SectionGuidedActionKind.ConfigureProfile:
                    OnSelectProjectProfile(sender, e); break;
                case SectionGuidedActionKind.ConfigureSectionSources: OnSetup(sender, e); break;
                case SectionGuidedActionKind.ChooseRecord:
                    SelectBestSectionRow(null);
                    break;
                case SectionGuidedActionKind.ChooseCrossing: OnResolveSection(sender, e); break;
                case SectionGuidedActionKind.ApproveRow: OnApproveSectionRow(sender, e); break;
                case SectionGuidedActionKind.NameSpans: OnNameSectionSpans(sender, e); break;
                case SectionGuidedActionKind.ResolveDirection: OnResolveTrafficDirection(sender, e); break;
                case SectionGuidedActionKind.ApplySelected: OnApplySelected(sender, e); break;
                case SectionGuidedActionKind.VerifySelected: OnVerifySelected(sender, e); break;
                case SectionGuidedActionKind.ShowVerifiedView:
                case SectionGuidedActionKind.LocateExistingView:
                case SectionGuidedActionKind.ShowExistingView: OnShow(sender, e); break;
                case SectionGuidedActionKind.InspectIssues:
                    // Keep the drawing and row visible. Navigation must not open
                    // another modal that merely repeats the same generic message.
                    SectionDiagnostics.IsExpanded = true;
                    SectionDiagnostics.BringIntoView();
                    SectionDetail.Focus();
                    break;
                case SectionGuidedActionKind.ChooseAnnotationRecoveryRecord:
                    if (_sectionRecoveryTargetRecordId != null)
                        SelectBestSectionRow(_sectionRecoveryTargetRecordId);
                    break;
            }
        }
        catch (Exception ex) { ShowError("המשך חתכים", ex); }
        finally { RefreshGates(); }
    }

    private static bool IsUnresolvedSectionGuidanceFinding(DeliveryFinding finding) =>
        finding.Severity >= FindingSeverity.ReviewRequired &&
        (finding.ResolvedAtUtc == null || string.IsNullOrWhiteSpace(finding.ResolvedBy) ||
         string.IsNullOrWhiteSpace(finding.Resolution));

    private void OnOpenSectionDrawing()
    {
        if (_sectionDrawingOpenPending || _pendingWorkflowSaveDocument != null || _busyProgress != null || Doc() != null) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "פתח את מודל Civil לעבודה",
            Filter = "שרטוטי AutoCAD (*.dwg)|*.dwg",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog() != true) { SetStatus("פתיחת השרטוט בוטלה"); return; }
        var selectedPath = picker.FileName;
        var documents = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager;
        _sectionDrawingOpenPending = true;
        RefreshGates();
        void OpenSelectedDrawing()
        {
            try
            {
                // The picker/callback may outlive the no-document state. Never
                // close, save or replace a drawing which appeared in the meantime.
                if (Doc() != null || _pendingWorkflowSaveDocument != null || _busyProgress != null)
                {
                    SetStatus("המצב השתנה — פתיחת השרטוט בוטלה; השרטוט הפעיל לא השתנה");
                    return;
                }
                if (!System.IO.File.Exists(selectedPath))
                    throw new InvalidOperationException("השרטוט שנבחר אינו זמין עוד; בחר אותו מחדש.");
                Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension.Open(documents, selectedPath, false);
            }
            catch (Exception ex) { ShowError("פתיחת שרטוט", ex); }
            finally { _sectionDrawingOpenPending = false; RefreshGates(); }
        }
        try
        {
            if (documents.IsApplicationContext) OpenSelectedDrawing();
            else documents.ExecuteInApplicationContext(_ => OpenSelectedDrawing(), null);
        }
        catch
        {
            _sectionDrawingOpenPending = false;
            throw;
        }
    }

    private string GlobalSectionDiagnosticDetail()
    {
        var findings = _plan?.Findings.Where(finding =>
            ((finding.AffectedRecordIds.Count == 0 && finding.Severity >= FindingSeverity.ReviewRequired) ||
             finding.Code == SectionFindingCodes.AnnotationRegistryRepairable) &&
            (finding.ResolvedAtUtc == null || string.IsNullOrWhiteSpace(finding.ResolvedBy) ||
             string.IsNullOrWhiteSpace(finding.Resolution)));
        var detail = findings == null ? "" : string.Join("\n\n", findings.Select(finding =>
            (finding.Code == SectionFindingCodes.AnnotationRegistryRepairable ? "שחזור נדרש: " : "חסימה כללית: ") + Bidi.FindingLine(finding) +
            (string.IsNullOrWhiteSpace(finding.RecommendedAction) ? "" : "\n" + finding.RecommendedAction)));
        return (_evidenceBlockingStatus == null ? "" : _evidenceBlockingStatus + "\n\n") + detail;
    }
}
