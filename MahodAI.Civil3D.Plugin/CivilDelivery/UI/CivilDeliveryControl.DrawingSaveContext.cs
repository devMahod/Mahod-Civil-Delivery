using System;
using Autodesk.AutoCAD.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private Document? _drawingSaveContextDocument;
    private IntPtr _drawingSaveContextDatabase;
    private string? _drawingSaveContextName;
    private long _drawingSaveContextGeneration;

    private void OnObservedDocumentActivated(object sender, DocumentCollectionEventArgs e)
    {
        // b26 (Codex 16:10 (ג), landscape copies turned DBMOD 0→1 on activation): opt-in only. Without the scan-diagnostics
        // switch every call below is a pass-through; with it, DBMOD around the palette's own activation work and the first
        // database objects modified meanwhile are journaled — read-only, nothing reset or saved.
        using (Shared.EstimateScanTrace.Start("activation"))
        using (Shared.ScanDatabaseChangeProbe.Attach(e.Document))
        {
            Shared.ScanDatabaseChangeProbe.SampleDbmod(e.Document, "activation.enter");
            Shared.EstimateScanTrace.Mark("activation.drawing-changed.begin");
            Dispatcher.Invoke(new Action(OnDrawingChanged));
            Shared.EstimateScanTrace.Mark("activation.drawing-changed.end");
            Shared.ScanDatabaseChangeProbe.SampleDbmod(e.Document, "activation.after-drawing-changed");
        }
        QueueIdleGateRefresh();
    }

    /// <summary>
    /// A drawing that is still opening can report DBMOD != 0 when it is activated, so the scan button offered
    /// "שמור וסרוק" for a clean drawing (live 30.09.2026). The buttons are recomputed once more when Civil is idle.
    /// </summary>
    private void QueueIdleGateRefresh()
    {
        void Handler(object? idleSender, EventArgs idleArgs)
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= Handler;
            try { Dispatcher.BeginInvoke(new Action(RefreshGatesAfterActivation)); } catch { }
        }
        try { Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle += Handler; } catch { }
    }

    // b26: the idle refresh after an activation, under the same opt-in trace (a pass-through without it).
    private void RefreshGatesAfterActivation()
    {
        var document = Doc();
        using (Shared.EstimateScanTrace.Start("activation-idle"))
        using (Shared.ScanDatabaseChangeProbe.Attach(document))
        {
            Shared.ScanDatabaseChangeProbe.SampleDbmod(document, "idle.enter");
            Shared.EstimateScanTrace.Step("idle.refresh-gates", RefreshGates);
            Shared.ScanDatabaseChangeProbe.SampleDbmod(document, "idle.after-refresh-gates");
        }
    }

    private void OnObservedDocumentDeactivating(object sender, DocumentCollectionEventArgs e)
    {
        UnhookDrawingSaveContext(e.Document);
        ClearPreviewBeforeDocumentTransition(e.Document);
    }

    private void OnObservedDocumentDestroying(object sender, DocumentCollectionEventArgs e)
    {
        UnhookDrawingSaveContext(e.Document);
        CancelWorkflowSave(e.Document, "השרטוט נסגר — הפעולה הממתינה בוטלה");
        ClearPreviewBeforeDocumentTransition(e.Document);
        ClearExportNoticeOf(e.Document);
    }

    /// <summary>
    /// Closing the drawing that owns the export notice clears it: with no other drawing to activate, a later reopen of the
    /// same path must not show the closed session's result. Closing another drawing leaves a valid notice as it is.
    /// </summary>
    private void ClearExportNoticeOf(Document? document)
    {
        string? identity;
        try { identity = document == null ? null : Estimate.EstimateWorkflowService.DrawingIdentity(document); }
        catch { return; }
        if (!_exportNotice.ClearIfOwnedBy(identity)) return;
        try { Dispatcher.Invoke(new Action(() => MeasurementDraftNotice.Text = string.Empty)); } catch { }
    }

    private void HookDrawingSaveContext()
    {
        var document = Doc();
        if (document == null) { UnhookDrawingSaveContext(); return; }
        try
        {
            var database = document.Database.UnmanagedObject;
            if (ReferenceEquals(document, _drawingSaveContextDocument) &&
                database == _drawingSaveContextDatabase) return;
            UnhookDrawingSaveContext();
            _drawingSaveContextDocument = document;
            _drawingSaveContextDatabase = database;
            _drawingSaveContextName = document.Name;
            document.CommandEnded += OnDrawingSaveContextEnded;
            document.CommandCancelled += OnDrawingSaveContextCancelled;
            document.CommandFailed += OnDrawingSaveContextCancelled;
        }
        catch { UnhookDrawingSaveContext(); }
    }

    private void UnhookDrawingSaveContext(Document? document = null)
    {
        if (document != null && !ReferenceEquals(document, _drawingSaveContextDocument)) return;
        var previous = _drawingSaveContextDocument;
        _drawingSaveContextDocument = null;
        _drawingSaveContextDatabase = IntPtr.Zero;
        _drawingSaveContextName = null;
        _drawingSaveContextGeneration++;
        try
        {
            if (previous != null)
            {
                previous.CommandEnded -= OnDrawingSaveContextEnded;
                previous.CommandCancelled -= OnDrawingSaveContextCancelled;
                previous.CommandFailed -= OnDrawingSaveContextCancelled;
            }
        }
        catch { /* A destroying document may no longer expose its events. */ }
    }

    private static bool IsDrawingSaveCommand(string name) =>
        string.Equals(name, "QSAVE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SAVEAS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SAVE", StringComparison.OrdinalIgnoreCase);

    private void OnDrawingSaveContextCancelled(object sender, CommandEventArgs e)
    {
        if (ReferenceEquals(sender, _drawingSaveContextDocument) &&
            IsDrawingSaveCommand(e.GlobalCommandName)) _drawingSaveContextGeneration++;
    }

    private void OnDrawingSaveContextEnded(object sender, CommandEventArgs e)
    {
        if (sender is not Document document ||
            !ReferenceEquals(document, _drawingSaveContextDocument) ||
            !IsDrawingSaveCommand(e.GlobalCommandName)) return;
        var generation = _drawingSaveContextGeneration;
        var database = _drawingSaveContextDatabase;
        // No hashing, profile reload, dashboard read or UI traversal inside the
        // native event. SaveGuidance's separate token continuation stays owned.
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _drawingSaveContextGeneration ||
                    !ReferenceEquals(document, _drawingSaveContextDocument) ||
                    !ReferenceEquals(document, Doc())) return;
                try
                {
                    if (document.Database.UnmanagedObject != database) return;
                    var currentName = document.Name;
                    if (!string.Equals(currentName, _drawingSaveContextName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _drawingSaveContextName = currentName;
                        // Same Document, new filename: existing scope logic clears
                        // old results and reloads the now-saved profile, without
                        // cancelling this document's pending save continuation.
                        OnDrawingChanged();
                    }
                    else
                    {
                        // Same-path QSAVE must not replace plan/scan/profile refs.
                        // Existing revision/freshness guards remain authoritative.
                        RefreshDrawingLabel();
                    }
                }
                catch (Exception ex) { ShowError("עדכון הקשר השרטוט אחרי שמירה", ex); }
            }));
        }
        catch { /* Dispatcher shutdown must not inject commands into Civil. */ }
    }
}
