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
                {
                    Pick(doc, ed, request, result);
                    // The palette shows this in its footer, which a docked or tall panel can hide (2026-11-09: the
                    // button "did nothing" on a drawing with no axis); the command line always shows it.
                    if (result.Error != null) ed.WriteMessage("\n" + result.Error + "\n");
                }
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

            var civilDoc = CivilApplication.ActiveDocument;
            var opened = TypicalSectionPicker.Open(doc.Database, civilDoc, request.AllowedAlignmentNames);
            if (opened.AlignmentNames.Count == 0)
            {
                opened.Dispose();
                // A drafted plan with no axis at all (2026-11-09, GM-halufa_A_new2: road 43 drawn as markings, its centre
                // line a TR-MARK-808 polyline): the engineer shows the axis instead of the tool guessing which marking
                // is the centre. Not where Civil alignments exist - there the profile's list decides.
                // The engineer is told in a popup, not only in the command line (owner, 2026-11-09): why nothing can be
                // marked, and what to do - pick the centre line now, or fix the profile's allowed alignments.
                var notice = TypicalSectionPickLogic.NoAxis(AlignmentNamesIn(doc.Database, civilDoc), request.AllowedAlignmentNames);
                var pickAxis = TypicalNoticeDialog.Ask(notice);
                var axisLayer = notice.CanPickAxis && pickAxis ? PromptAxisFromPlan(doc, ed) : null;
                if (axisLayer == null)
                {
                    result.Error = notice.CanPickAxis
                        ? "לא נבחר קו ציר — לא סומן דבר. אפשר גם להעתיק את קו הציר לשכבה ששמה מתחיל ב-AX (למשל AX-43) ולהריץ שוב."
                        : notice.Summary;
                    return;
                }
                opened = TypicalSectionPicker.Open(doc.Database, civilDoc, Array.Empty<string>());
                if (opened.AlignmentNames.Count == 0)
                {
                    opened.Dispose();
                    result.Error = $"הקו שהועתק לשכבה {axisLayer} לא נקרא כציר — לא סומן דבר.";
                    return;
                }
            }
            using var picker = opened;
            var names = string.Join(", ", picker.AlignmentNames);
            ed.WriteMessage(picker.CorridorOnly
                ? FormattableString.Invariant($"\nתוואים עם קורידור: {names}. הקו הירוק עוקב אחרי העכבר — לחיצה מסמנת חתך. חצי רוחב {halfWidth.Value:0.##} מ' (לשינוי: רוחב).\n")
                : picker.PlanOnly
                    ? FormattableString.Invariant($"\nאין בשרטוט תוואי Civil — צירים מהתנוחה: {names}. החתך ייקרא מקווי התנוחה. הקו הירוק עוקב אחרי העכבר — לחיצה מסמנת חתך. חצי רוחב {halfWidth.Value:0.##} מ' (לשינוי: רוחב).\n")
                    : $"\nתוואים: {names} — לאף אחד מהם אין קורידור; היצירה תיחסם עד שיהיה.\n");

            var existing = picker.ExistingMarkerMidpoints();
            var session = new List<(ObjectId Line, ObjectId Text, TypicalSectionPick Pick)>();
            using var sysvars = new PickSysvarScope();
            while (true)
            {
                var taken = existing.Concat(session.Select(s => new P2(s.Pick.AxisX, s.Pick.AxisY))).ToList();
                var message = session.Count == 0
                    ? "\nלחץ על הכביש במיקום החתך"
                    : $"\nלחץ על הכביש במיקום החתך הבא ({session.Count} סומנו), או Enter לסיום";
                var jig = new TypicalPickJig(picker, halfWidth.Value, taken, message, canFinish: session.Count > 0, PreviewTextHeight);

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

        /// <summary>Every Civil alignment's name in the drawing (none when there is no Civil document).</summary>
        private static List<string> AlignmentNamesIn(Database db, CivilDocument civilDoc)
        {
            var names = new List<string>();
            try
            {
                using var tr = db.TransactionManager.StartOpenCloseTransaction();
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Alignment alignment)
                        names.Add(alignment.Name);
            }
            catch { /* no Civil document: no alignments */ }
            return names;
        }

        /// <summary>
        /// No Civil alignment and no axis line on the plan: asks the engineer to click the road's centre line (a line,
        /// polyline or arc - inside an XREF too) and copies it, in world coordinates, to an axis layer named after what
        /// they type (<see cref="PlanAxis.AxisLayerFor"/>), where every later run finds it by itself. The copy is part of
        /// this command, so one Undo takes it away. Stations run from the line's first vertex. Null when the engineer
        /// cancels.
        /// </summary>
        internal static string? PromptAxisFromPlan(Document doc, Editor ed)
        {
            ed.WriteMessage("\nלא נמצא בשרטוט תוואי Civil או קו ציר בתנוחה (שכבות AX-*, CL, CENTERLINE).\n");
            var db = doc.Database;
            while (true)
            {
                var picked = ed.GetNestedEntity(new PromptNestedEntityOptions(
                    "\nבחר את קו הציר של הכביש (קו / פוליליין / קשת, גם בתוך XREF), או Esc לביטול: ") { AllowNone = false });
                if (picked.Status != PromptStatus.OK) return null;

                using var tr = db.TransactionManager.StartTransaction();
                if (tr.GetObject(picked.ObjectId, OpenMode.ForRead) is not Curve curve)
                {
                    ed.WriteMessage("\nזה אינו קו — בחר קו, פוליליין או קשת שעוברים לאורך הכביש.\n");
                    continue;
                }
                Entity copy;
                try { copy = curve.GetTransformedCopy(picked.Transform); }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\nאי אפשר להעתיק את הקו הזה ({ex.Message}) — בחר קו אחר.\n");
                    continue;
                }
                double length = 0;
                if (copy is Curve copied)
                    try { length = copied.GetDistanceAtParameter(copied.EndParam) - copied.GetDistanceAtParameter(copied.StartParam); }
                    catch { length = 0; }
                if (!(length >= DraftedTypicalSectionService.MinAxisLengthM))
                {
                    copy.Dispose();
                    ed.WriteMessage(FormattableString.Invariant(
                        $"\nהקו קצר מדי לציר ({length:0.#} מ', לפחות {DraftedTypicalSectionService.MinAxisLengthM:0} מ') — בחר את קו הציר לכל אורך הכביש.\n"));
                    continue;
                }

                var typed = ed.GetString(new PromptStringOptions("\nשם הציר (יופיע בכותרת החתך, למשל 43), Enter לשם אוטומטי: ") { AllowSpaces = false });
                if (typed.Status == PromptStatus.Cancel) { copy.Dispose(); return null; }
                var layers = new List<string>();
                foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
                    layers.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                var layer = PlanAxis.AxisLayerFor(typed.Status == PromptStatus.OK ? typed.StringResult : null, layers);
                TypicalSectionPicker.EnsureLayer(tr, db, layer, 1);
                copy.Layer = layer;
                copy.ColorIndex = 256;
                copy.Linetype = "ByLayer";
                copy.LineWeight = LineWeight.ByLayer;
                var modelSpace = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                modelSpace.AppendEntity(copy);
                tr.AddNewlyCreatedDBObject(copy, true);
                tr.Commit();
                ed.WriteMessage(FormattableString.Invariant(
                    $"\nקו הציר הועתק לשכבה {layer} ({length:0.#} מ', תחנה 0 בתחילת הקו) — בהרצה הבאה הכלי ימצא אותו לבד.\n"));
                return layer;
            }
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
