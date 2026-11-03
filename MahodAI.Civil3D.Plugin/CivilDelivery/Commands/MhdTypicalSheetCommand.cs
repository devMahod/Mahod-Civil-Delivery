using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdTypicalSheetCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_TYPICAL_SHEET — the typical sections on the office's sheet (2026-11-02, DD-5001): the engineer selects the
    /// typical sections drawn in the office's format (Enter takes every one in the drawing) and gets A0 layouts with them
    /// stacked in 1:100 viewports, the legend and a title. Its own command, not a step of the typical-sections run: the
    /// layout switch it needs took Civil 3D down when it ran right after the firm's section program. The panel runs it
    /// from its "גיליון חתכים" button and from the result window's "צור גיליון A0" (2026-11-03), the latter with the
    /// sections just created queued (<see cref="RunInCommandContext"/>), so nothing is asked.
    /// </summary>
    public class MhdTypicalSheetCommand
    {
        /// <summary>Sections queued by the panel for the next run, by drawing (the native database) and handle.</summary>
        private static (IntPtr Database, IReadOnlyList<string> Handles)? _queued;

        /// <summary>
        /// The panel's report of the next run: the sheets created, none when nothing was there to place, null when the
        /// engineer cancelled the selection or the run failed.
        /// </summary>
        private static Action<IReadOnlyList<string>?>? _done;

        /// <summary>
        /// Runs the command from the panel without typing it (the engineers' up-arrow history stays theirs): in command
        /// context, as AutoCAD would. With <paramref name="handles"/> the sheet takes those sections and asks nothing;
        /// without, the engineer selects (Enter = every typical section in the drawing). Called from application context.
        /// </summary>
        internal static void RunInCommandContext(Document doc, IReadOnlyList<string>? handles, Action<IReadOnlyList<string>?>? done = null)
        {
            _queued = handles is { Count: > 0 } ? (doc.Database.UnmanagedObject, handles) : null;
            _done = done;
            _ = AcadApp.DocumentManager.ExecuteInCommandContextAsync(_ =>
            {
                try { new MhdTypicalSheetCommand().Run(); }
                catch (System.Exception ex)
                {
                    MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("typical sheet failed", ex);
                    try { doc.Editor.WriteMessage("\nגיליון החתכים לא נוצר: " + ex.Message + "\n"); } catch { }
                }
                return System.Threading.Tasks.Task.CompletedTask;
            }, null);
        }

        [CommandMethod(CivilDeliveryCommandNames.TypicalSheet, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var queued = _queued;
            var done = _done;
            _queued = null;
            _done = null;
            IReadOnlyList<string>? sheets = null;
            try { sheets = Place(doc, queued); }
            finally { done?.Invoke(sheets); }
        }

        private static IReadOnlyList<string>? Place(Document doc, (IntPtr Database, IReadOnlyList<string> Handles)? queued)
        {
            var db = doc.Database;
            var ed = doc.Editor;
            List<ObjectId>? given = null;
            if (queued is { } q && q.Database == db.UnmanagedObject)
            {
                given = new List<ObjectId>();
                foreach (var handle in q.Handles)
                    try
                    {
                        if (db.TryGetObjectId(new Handle(Convert.ToInt64(handle, 16)), out var id) && !id.IsNull && !id.IsErased)
                            given.Add(id);
                    }
                    catch (FormatException) { }
            }
            PromptSelectionResult? picked = null;
            if (given == null)
            {
                var options = new PromptSelectionOptions { MessageForAdding = "\nבחר חתכים טיפוסיים לגיליון <Enter = כולם>: " };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
                picked = ed.GetSelection(options, filter);
                if (picked.Status == PromptStatus.Cancel) return null;
            }

            var panels = new List<ObjectId>();
            var roads = new SortedSet<string>();
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                IEnumerable<ObjectId> candidates = given
                    ?? (picked!.Status == PromptStatus.OK
                        ? picked.Value.GetObjectIds()
                        : ((BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead)).Cast<ObjectId>());
                foreach (var id in candidates)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference reference) continue;
                    var name = ((BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead)).Name;
                    if (!name.StartsWith(OfficeTypicalRenderer.BlockPrefix, System.StringComparison.Ordinal)) continue;
                    if (OfficeTypicalRenderer.FrameExtents(tr, id) == null) continue;
                    panels.Add(id);
                    // MTS_<alignment>_<station>_<time>: the road names the sheet
                    var parts = name.Substring(OfficeTypicalRenderer.BlockPrefix.Length).Split('_');
                    if (parts.Length >= 4) roads.Add(OfficeTypicalSheet.RoadName(string.Join("_", parts.Take(parts.Length - 3))));
                }
            }
            if (panels.Count == 0)
            {
                ed.WriteMessage("\nאין בשרטוט חתכים טיפוסיים בפורמט הגיליון — יש ליצור אותם קודם (חתכים טיפוסיים בפאנל של Mahod Civil Delivery).\n");
                return Array.Empty<string>();
            }
            var sheets = OfficeTypicalSheetLayout.Create(db, panels, string.Join(", ", roads.Where(r => r.Length > 0)));
            OfficeTypicalSheetLayout.RedrawWhenShown(doc, sheets);
            ed.WriteMessage($"\nגיליון חתכים טיפוסיים: {panels.Count} חתכים ב-{sheets.Count} גיליונות ({string.Join(", ", sheets)}).\n");
            return sheets;
        }
    }
}
