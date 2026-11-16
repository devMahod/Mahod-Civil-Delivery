using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdCivilDeliveryCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_CIVIL_DELIVERY — the product entry point. Opens the engineer panel.
    ///
    /// The command-line workflows (MHD_SETUP / MHD_SECTIONS / MHD_ESTIMATE) remain
    /// fully supported: both routes call the same deterministic services, so an
    /// engineer can work in the panel, on the command line, or through MahodAI and
    /// get the same engineering result.
    /// </summary>
    public class MhdCivilDeliveryCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.AfterSave, CommandFlags.Modal | CommandFlags.NoHistory)]
        public void ResumeAfterSave()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var token = doc.Editor.GetString("\nמזהה המשך שמירה: ");
            if (token.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                CivilDeliveryPalette.ResumeWorkflowAfterExplicitSave(token.StringResult);
        }

        [CommandMethod(CivilDeliveryCommandNames.Panel, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            if (!CivilRuntime.IsCivilAvailable())
            {
                doc.Editor.WriteMessage(
                    "\nMahod Civil Delivery דורש Civil 3D — הכלי אינו זמין ב-AutoCAD רגיל.\n");
                return;
            }

            // Toggle, like every other AutoCAD palette: pressing the ribbon button a
            // second time closes it rather than re-showing an already-visible panel.
            // "Visible" means drawn on screen, and the message says what really happened.
            var firstOpen = CivilDeliveryPalette.IsFirstOpen;
            var shown = CivilDeliveryPalette.Toggle();
            doc.Editor.WriteMessage(!shown
                ? $"\nMahod Civil Delivery נסגר. להצגה מחדש הקלד שוב {CivilDeliveryCommandNames.Panel}.\n"
                : firstOpen
                    ? "\nMahod Civil Delivery נפתח. בפתיחה הראשונה הכלי קורא את השרטוט ואת קובץ ה-CL — הפאנל יופיע בעוד כמה שניות.\n"
                    : "\nMahod Civil Delivery נפתח.\n");
        }
    }
}
