using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
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
    /// layout switch it needs took Civil 3D down when it ran right after the firm's section program.
    /// </summary>
    public class MhdTypicalSheetCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.TypicalSheet, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;
            var options = new PromptSelectionOptions { MessageForAdding = "\nבחר חתכים טיפוסיים לגיליון <Enter = כולם>: " };
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var picked = ed.GetSelection(options, filter);
            if (picked.Status == PromptStatus.Cancel) return;

            var panels = new List<ObjectId>();
            var roads = new SortedSet<string>();
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                IEnumerable<ObjectId> candidates = picked.Status == PromptStatus.OK
                    ? picked.Value.GetObjectIds()
                    : ((BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead)).Cast<ObjectId>();
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
                return;
            }
            var sheets = OfficeTypicalSheetLayout.Create(db, panels, string.Join(", ", roads.Where(r => r.Length > 0)));
            ed.WriteMessage($"\nגיליון חתכים טיפוסיים: {panels.Count} חתכים ב-{sheets.Count} גיליונות ({string.Join(", ", sheets)}).\n");
        }
    }
}
