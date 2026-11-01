using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdTypicalSectionsCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_TYPICAL_SECTIONS — the pick step of the typical-sections flow (2026-10). The palette starts it with a
    /// token, exactly like the after-save resume: a command owns the document lock, the undo group and the Esc
    /// handling that a modeless palette prompt would not. While the cursor moves, a live preview shows the section
    /// line a click would mark (<see cref="TypicalPickJig"/>); a click marks it as a LINE plus the station text on
    /// <see cref="TypicalSectionContracts.MarkerLayer"/>. Enter ends the session, Esc erases what this session drew,
    /// the Undo keyword removes the last marker and Width changes the half-width. Whatever happens, the command
    /// reports back to the palette in <c>finally</c>, so no pending request outlives it.
    /// </summary>
    public class MhdTypicalSectionsCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.TypicalSections, CommandFlags.Modal | CommandFlags.NoHistory)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var token = ed.GetString("\nמזהה סימון חתכים: ");
            if (token.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(token.StringResult)) return;
            var request = TypicalSectionPickRequests.TryTake(token.StringResult);
            if (request == null)
            {
                ed.WriteMessage("\nסימון חתכים טיפוסיים מופעל מהפאנל של Mahod Civil Delivery (הכפתור 'חתכים טיפוסיים').\n");
                return;
            }

            var result = new TypicalSectionPickResult(request.Token);
            try
            {
                if (doc.Database.UnmanagedObject != request.Database)
                    result.Error = "השרטוט הפעיל אינו השרטוט שממנו הופעל הסימון — לא סומן דבר.";
                else if (!CivilRuntime.IsCivilAvailable())
                    result.Error = "סימון חתכים דורש Civil 3D.";
                else
                    Pick(doc, ed, request, result);
            }
            catch (System.Exception ex)
            {
                result.Error = ex.Message;
                ed.WriteMessage($"\nסימון החתכים נכשל: {ex.Message}\n");
            }
            finally
            {
                CivilDeliveryPalette.ResumeTypicalSectionPicks(result);
            }
        }

        internal static void Pick(Document doc, Editor ed, TypicalSectionPickRequest request, TypicalSectionPickResult result)
        {
            // Straight to the drawing, no prompt to confirm first (the engineer's ask, 2026-10-28): the half-width is the
            // last one used in this session, else the profile's, else 20 m; the רוחב keyword changes it while marking.
            double? halfWidth = TypicalSectionPickLogic.DefaultHalfWidth(request.DefaultHalfWidthM);
            result.HalfWidthM = halfWidth;

            using var picker = TypicalSectionPicker.Open(doc.Database, CivilApplication.ActiveDocument, request.AllowedAlignmentNames);
            if (picker.AlignmentNames.Count == 0)
            {
                result.Error = "לא נמצא תוואי מתאים בשרטוט (לפי התוואים המותרים בפרופיל הפרויקט).";
                return;
            }
            var names = string.Join(", ", picker.AlignmentNames);
            ed.WriteMessage(picker.CorridorOnly
                ? FormattableString.Invariant($"\nתוואים עם קורידור: {names}. הקו הירוק עוקב אחרי העכבר — לחיצה מסמנת חתך. חצי רוחב {halfWidth.Value:0.##} מ' (לשינוי: רוחב).\n")
                : $"\nתוואים: {names} — לאף אחד מהם אין קורידור; היצירה תיחסם עד שיהיה.\n");

            var existing = picker.ExistingMarkerMidpoints();
            var session = new List<(ObjectId Line, ObjectId Text, TypicalSectionPick Pick)>();
            var textHeight = PreviewTextHeight();
            using var sysvars = new PickSysvarScope();
            while (true)
            {
                var taken = existing.Concat(session.Select(s => new P2(s.Pick.AxisX, s.Pick.AxisY))).ToList();
                var message = session.Count == 0
                    ? "\nלחץ על הכביש במיקום החתך"
                    : $"\nלחץ על הכביש במיקום החתך הבא ({session.Count} סומנו), או Enter לסיום";
                var jig = new TypicalPickJig(picker, halfWidth.Value, taken, message, canFinish: session.Count > 0, textHeight);

                PromptResult dragged;
                try { dragged = ed.Drag(jig); }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\nקלט לא מזוהה ({ex.Message}) — לחץ בשרטוט, Enter לסיום או Esc לביטול.\n");
                    continue;
                }

                if (dragged.Status == PromptStatus.Keyword)
                {
                    if (string.Equals(dragged.StringResult, TypicalPickJig.UndoKeyword, StringComparison.OrdinalIgnoreCase) && session.Count > 0)
                    {
                        var last = session[^1];
                        session.RemoveAt(session.Count - 1);
                        picker.Erase(last.Line, last.Text);
                        ed.WriteMessage($"\nהסימון האחרון בוטל ({session.Count} נותרו).\n");
                    }
                    else if (string.Equals(dragged.StringResult, TypicalPickJig.WidthKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        var changed = PromptHalfWidth(ed, halfWidth.Value);
                        if (changed != null)
                        {
                            halfWidth = changed;
                            result.HalfWidthM = changed;
                            ed.WriteMessage($"\nחצי-רוחב החתכים הבאים: {changed:0.##} מ'.\n");
                        }
                    }
                    continue;
                }
                if (dragged.Status == PromptStatus.None) break; // Enter
                if (dragged.Status != PromptStatus.OK)
                {
                    // Esc (PromptStatus.Cancel) or an input error: this session leaves nothing behind.
                    // (A native U after the command does the same.)
                    var escaped = dragged.Status == PromptStatus.Cancel;
                    foreach (var (line, text, _) in session) picker.Erase(line, text);
                    session.Clear();
                    result.Cancelled = true;
                    ed.WriteMessage(escaped
                        ? "\nסימון החתכים בוטל — הקווים שסומנו בסבב הזה נמחקו.\n"
                        : $"\nסימון החתכים הופסק ({dragged.Status}) — הקווים שסומנו בסבב הזה נמחקו.\n");
                    return;
                }

                // The click commits exactly what the preview showed under the cursor.
                var resolution = jig.Current ?? picker.Resolve(new P2(jig.LastPoint.X, jig.LastPoint.Y), halfWidth.Value, taken);
                if (!resolution.IsReady)
                {
                    ed.WriteMessage("\n" + resolution.Reason + "\n");
                    continue;
                }
                var added = picker.Commit(resolution, halfWidth.Value);
                session.Add(added);
                ed.WriteMessage($"\nחתך {session.Count}: תוואי {added.Pick.AlignmentName}, תחנה {added.Pick.Station:0.00}\n");
                ed.UpdateScreen();
            }

            result.Picks.AddRange(session.Select(s => s.Pick));
            ed.WriteMessage(session.Count == 0
                ? "\nלא סומן אף חתך.\n"
                : $"\n{session.Count} חתכים סומנו — חזור לפאנל ליצירה.\n");
        }

        internal static double? PromptHalfWidth(Editor ed, double defaultValue)
        {
            while (true)
            {
                var options = new PromptDoubleOptions("\nחצי-רוחב החתך לכל צד מהציר (מטר)")
                {
                    DefaultValue = defaultValue,
                    UseDefaultValue = true,
                    AllowNegative = false,
                    AllowZero = false,
                    AllowNone = false,
                };
                var width = ed.GetDouble(options);
                if (width.Status != PromptStatus.OK) return null;
                var reason = TypicalSectionPickLogic.ValidateHalfWidth(width.Value);
                if (reason == null) return width.Value;
                ed.WriteMessage("\n" + reason + "\n");
            }
        }

        /// <summary>A preview label readable at the current zoom: a fixed share of the view height.</summary>
        private static double PreviewTextHeight()
        {
            try
            {
                var viewSize = Convert.ToDouble(AcadApp.GetSystemVariable("VIEWSIZE"), System.Globalization.CultureInfo.InvariantCulture);
                return viewSize > 0 && double.IsFinite(viewSize) ? viewSize / 55.0 : 2.5;
            }
            catch { return 2.5; }
        }

        /// <summary>Snaps off while the engineer clicks (a click must land where it was made), restored afterwards.</summary>
        internal sealed class PickSysvarScope : IDisposable
        {
            private static readonly string[] Names = { "OSMODE", "SNAPMODE", "ORTHOMODE" };
            private readonly List<(string Name, object Value)> _saved = new();

            public PickSysvarScope()
            {
                foreach (var name in Names)
                {
                    try
                    {
                        var value = AcadApp.GetSystemVariable(name);
                        if (value == null) continue;
                        _saved.Add((name, value));
                        AcadApp.SetSystemVariable(name, (short)0);
                    }
                    catch (System.Exception) { /* a missing sysvar is not worth refusing the pick */ }
                }
            }

            public void Dispose()
            {
                foreach (var (name, value) in _saved)
                {
                    try { AcadApp.SetSystemVariable(name, value); }
                    catch (System.Exception) { /* restored on a best-effort basis */ }
                }
            }
        }
    }
}
