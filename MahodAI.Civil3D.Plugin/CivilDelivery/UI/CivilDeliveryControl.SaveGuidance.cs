using System;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MessageBox = System.Windows.MessageBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private Document? _pendingWorkflowSaveDocument;
    private Action? _pendingWorkflowSaveContinuation;
    private bool _resumingWorkflowSave;
    private string? _pendingWorkflowSaveToken;
    private IntPtr _pendingWorkflowSaveDatabase;
    private string? _pendingWorkflowSaveOperation;
    private int _workflowSaveAttempts;
    private WorkflowSavePhase _workflowSavePhase;

    private enum WorkflowSavePhase { None, AwaitingSaveEnd, SaveEndObserved, ContinuationQueued }

    // Runs BEFORE a decision dialog. No engineering choice from before a save is
    // carried forward: a section decision always re-plans and reopens for review.
    private bool EnsureSavedForAction(
        string operation, RoutedEventHandler continuation, bool replan = false,
        bool requiresLoadedProfile = true)
    {
        var doc = Doc();
        if (doc == null) return false;
        if (_pendingWorkflowSaveDocument != null)
        {
            SetStatus("ממתין לשמירה שכבר אושרה — לא נשלחה פקודה נוספת");
            return false;
        }
        try
        {
            var expectedDatabase = doc.Database.UnmanagedObject;
            var source = DrawingRevisionTracker.CaptureLive(doc);
            var readiness = EstimateSourceSnapshotPolicy.ForScan(
                source.DrawingPath, source.DrawingHash, source.DbMod, source.Failure);
            if (readiness.IsReady) return true;
            if (!readiness.CanSaveAndResume || _resumingWorkflowSave)
            {
                RtlMessageBox.Show("לא ניתן להמשיך ל" + operation + ":\n\n" + readiness.Detail +
                    "\n\nשמור את השרטוט ובדוק שהשמירה הסתיימה לפני ניסיון נוסף.",
                    "הפעולה טרם התחילה", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            // b24 (b23 live 10:37, the ninth path message): the drawing path goes to its own left-to-right field, like
            // the other path messages; a drawing without a saved path keeps the plain question.
            var saveQuestion = "לשמור עכשיו באמצעות השמירה הרגילה של Civil ולהמשיך לאחר אימות השמירה?" +
                (replan ? "\nתכנון החתכים יחושב מחדש, ואז תיפתח ההכרעה לבדיקה מחדש. שום בחירה לא תאושר אוטומטית." : "");
            var saveAnswer = string.IsNullOrWhiteSpace(source.DrawingPath)
                ? RtlMessageBox.Show("לפני " + operation + " יש לשמור את השרטוט הפתוח.\n\n" + saveQuestion,
                    "שמירה והמשך", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : RtlMessageBox.ShowPath("לפני " + operation + " יש לשמור את השרטוט הפתוח.", source.DrawingPath!,
                    saveQuestion, "שמירה והמשך", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    System.IO.Path.GetFileName(source.DrawingPath));
            if (saveAnswer != MessageBoxResult.Yes)
            {
                SetStatus(operation + " לא הופעלה — השמירה לא אושרה");
                return false;
            }

            if (!ReferenceEquals(Doc(), doc) || doc.Database.UnmanagedObject != expectedDatabase)
            {
                SetStatus("השרטוט הוחלף בזמן בקשת השמירה — לא נשלחה פקודה. פתח את הפעולה מחדש בשרטוט הרצוי.");
                return false;
            }
            var selectedId = (SectionsGrid.SelectedItem as SectionRowViewModel)?.Record.RecordId;
            _pendingWorkflowSaveDocument = doc;
            _pendingWorkflowSaveDatabase = expectedDatabase;
            _pendingWorkflowSaveToken = Guid.NewGuid().ToString("N");
            _pendingWorkflowSaveOperation = operation;
            _workflowSaveAttempts = 1;
            _pendingWorkflowSaveContinuation = () =>
            {
                // Choosing an existing profile is itself recovery from a missing
                // or broken profile. Its picker must not depend on that profile
                // loading first. Replanning and all ordinary actions still do.
                if (requiresLoadedProfile || replan)
                {
                    ReloadProfile();
                    if (_profile == null) return;
                }
                if (replan)
                {
                    OnPlan(this, new RoutedEventArgs());
                    var selected = System.Linq.Enumerable.FirstOrDefault(
                        _sectionRows, row => row.Record.RecordId == selectedId);
                    if (_plan == null || selected == null || PlanIsStale())
                    {
                        SetStatus("השרטוט נשמר — יש לבחור חתך מתכנון עדכני כדי להמשיך");
                        return;
                    }
                    SectionsGrid.SelectedItem = selected;
                    SectionsGrid.ScrollIntoView(selected);
                }
                continuation(this, new RoutedEventArgs());
            };
            _workflowSavePhase = WorkflowSavePhase.AwaitingSaveEnd;
            doc.CommandEnded += OnWorkflowSaveEnded;
            doc.CommandCancelled += OnWorkflowSaveCancelled;
            doc.CommandFailed += OnWorkflowSaveCancelled;
            SetStatus("ממתין לשמירת השרטוט — לאחריה: " + operation +
                ". אם Civil מבקש נתיב בשורת הפקודה, הקלד ~ כדי לפתוח בורר קבצים, או Esc לביטול.");
            // QSAVE may still be asking for the first filename (notably FILEDIA=0).
            // Never append another command or token to that native input stream.
            doc.SendStringToExecute("_.QSAVE\n", true, false, false);
            RefreshGates();
        }
        catch (Exception ex)
        {
            CancelWorkflowSave(doc, "הפעלת השמירה נכשלה");
            ShowError("בדיקת שמירה והמשך", ex);
        }
        return false;
    }

    private void OnWorkflowSaveEnded(object sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, "QSAVE", StringComparison.OrdinalIgnoreCase) ||
            sender is not Document document ||
            !ReferenceEquals(document, _pendingWorkflowSaveDocument) ||
            _workflowSavePhase != WorkflowSavePhase.AwaitingSaveEnd) return;
        var token = _pendingWorkflowSaveToken;
        var expectedDatabase = _pendingWorkflowSaveDatabase;
        _workflowSavePhase = WorkflowSavePhase.SaveEndObserved;
        try
        {
            // Leave the native CommandEnded callback before sending any input.
            // A cancellation/new request before dispatch invalidates this ticket.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_workflowSavePhase != WorkflowSavePhase.SaveEndObserved ||
                    !SavedDrawingContinuationPolicy.CanConsume(
                        _pendingWorkflowSaveToken, token,
                        ReferenceEquals(document, _pendingWorkflowSaveDocument) &&
                        _pendingWorkflowSaveContinuation != null)) return;
                try
                {
                    if (!ReferenceEquals(Doc(), document) ||
                        document.Database.UnmanagedObject != expectedDatabase)
                    {
                        CancelWorkflowSave(document, "השרטוט הוחלף לאחר השמירה — הפעולה הבאה לא הופעלה");
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(document.CommandInProgress))
                    {
                        CancelWorkflowSave(document,
                            "פקודה אחרת עדיין פעילה — ההמשך האוטומטי בוטל. סיים אותה ופתח את הפעולה מחדש.");
                        return;
                    }
                    _workflowSavePhase = WorkflowSavePhase.ContinuationQueued;
                    document.SendStringToExecute(CivilDeliveryCommandNames.AfterSave + "\n" + token + "\n",
                        true, false, false);
                }
                catch (Exception ex)
                {
                    CancelWorkflowSave(document, "לא ניתן להמשיך לאחר השמירה");
                    ShowError("תזמון המשך לאחר שמירה", ex);
                }
            }));
        }
        catch (Exception ex)
        {
            CancelWorkflowSave(document, "לא ניתן לתזמן המשך לאחר השמירה");
            ShowError("תזמון המשך לאחר שמירה", ex);
        }
    }

    private void OnWorkflowSaveCancelled(object sender, CommandEventArgs e)
    {
        if (string.Equals(e.GlobalCommandName, "QSAVE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.GlobalCommandName, CivilDeliveryCommandNames.AfterSave, StringComparison.OrdinalIgnoreCase))
            CancelWorkflowSave(sender as Document, "השמירה בוטלה — הפעולה הבאה לא הופעלה");
    }

    private void CancelWorkflowSave(Document? document, string reason)
    {
        if (document == null || !ReferenceEquals(document, _pendingWorkflowSaveDocument)) return;
        try
        {
            document.CommandEnded -= OnWorkflowSaveEnded;
            document.CommandCancelled -= OnWorkflowSaveCancelled;
            document.CommandFailed -= OnWorkflowSaveCancelled;
        }
        catch { /* A destroying document can no longer expose events. No continuation survives. */ }
        _pendingWorkflowSaveDocument = null;
        _pendingWorkflowSaveContinuation = null;
        _pendingWorkflowSaveToken = null;
        _pendingWorkflowSaveDatabase = IntPtr.Zero;
        _pendingWorkflowSaveOperation = null;
        _workflowSavePhase = WorkflowSavePhase.None;
        SetStatus(reason);
        // CommandCancelled/Failed is not followed by the normal continuation.
        // Refresh the palette after the native event completes so WaitForSave
        // does not survive a cancelled request in the same document.
        Dispatcher.BeginInvoke(new Action(RefreshGates));
    }

    internal void ResumeWorkflowAfterExplicitSave(string token)
    {
        // An old queued command cannot consume a newer explicit save request.
        if (!SavedDrawingContinuationPolicy.CanConsume(
                _pendingWorkflowSaveToken, token,
                _pendingWorkflowSaveDocument != null && _pendingWorkflowSaveContinuation != null &&
                _workflowSavePhase == WorkflowSavePhase.ContinuationQueued)) return;
        var expected = _pendingWorkflowSaveDocument;
        var continuation = _pendingWorkflowSaveContinuation;
        var expectedDatabase = _pendingWorkflowSaveDatabase;
        var operation = _pendingWorkflowSaveOperation ?? "הפעולה הבאה";
        var attempts = _workflowSaveAttempts;
        CancelWorkflowSave(expected, "בודק את השמירה לפני המשך העבודה…");
        var doc = Doc();
        if (expected == null || continuation == null) return;
        try
        {
            var sameDocument = ReferenceEquals(doc, expected) &&
                doc!.Database.UnmanagedObject == expectedDatabase;
            if (!sameDocument)
            {
                SetStatus("השרטוט הוחלף בזמן השמירה — הפעולה בוטלה. חזור לשרטוט הרצוי כדי להמשיך.");
                return;
            }
            var source = DrawingRevisionTracker.CaptureLive(doc!);
            var readiness = EstimateSourceSnapshotPolicy.ForScan(
                source.DrawingPath, source.DrawingHash, source.DbMod, source.Failure);
            var decision = SavedDrawingContinuationPolicy.Decide(
                true, sameDocument, readiness.IsReady, readiness.CanSaveAndResume, attempts);
            if (decision == SavedDrawingContinuationDecision.SaveAgain)
            {
                // Civil re-dirties the database while QSAVE completes (live 07/09 17:29:
                // the first save after a committed APPLY wrote 51,910,949 bytes and
                // DBMOD read 1; the second explicit save read 0). Exactly one more
                // explicit save under the same token discipline; a second dirty
                // read-back is reported below, never looped.
                _pendingWorkflowSaveDocument = doc;
                _pendingWorkflowSaveDatabase = expectedDatabase;
                _pendingWorkflowSaveContinuation = continuation;
                _pendingWorkflowSaveOperation = operation;
                _workflowSaveAttempts = attempts + 1;
                _pendingWorkflowSaveToken = Guid.NewGuid().ToString("N");
                _workflowSavePhase = WorkflowSavePhase.AwaitingSaveEnd;
                doc!.CommandEnded += OnWorkflowSaveEnded;
                doc.CommandCancelled += OnWorkflowSaveCancelled;
                doc.CommandFailed += OnWorkflowSaveCancelled;
                SetStatus("השמירה הסתיימה אך Civil סימן שינויים נוספים (DBMOD=" +
                    (source.DbMod?.ToString() ?? "?") + ") — שומר פעם נוספת, ואז: " + operation);
                doc.SendStringToExecute("_.QSAVE\n", true, false, false);
                return;
            }
            if (decision != SavedDrawingContinuationDecision.Resume)
            {
                SetStatus("השמירה בוטלה או לא הושלמה — לא בוצעה הפעולה הבאה");
                RtlMessageBox.Show(readiness.Detail, "השמירה לא הושלמה",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _resumingWorkflowSave = true;
            continuation();
        }
        catch (Exception ex) { ShowError("המשך לאחר שמירה", ex); }
        finally { _resumingWorkflowSave = false; RefreshGates(); }
    }
}
