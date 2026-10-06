using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

// Additive command-class registration. This assembly declares its command classes
// EXPLICITLY (MahodCommands, the self-test commands, the assembly-library commands), and
// once any [assembly: CommandClass] exists AutoCAD scans only the declared classes — a
// command in an undeclared class is silently absent from the command line. Multiple
// attributes are allowed and additive, so this one adds the polyline commands.
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.Tools.PolylineEdit.MahodPolylineCommands))]

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Command-line access to the polyline tools, for the drafting team's actual habit:
    /// type a short command, click, carry on — no chat, no backend, no network. This is the
    /// PLTOOLS replacement in the form people already use, and it works the moment the bundle is
    /// installed.
    ///
    /// Every command runs the SAME tool object the agent calls over WebSocket, so there is exactly
    /// one implementation of each operation and the two entry points can never drift apart. The
    /// only thing that differs is who supplies the parameters: here the command line, there the
    /// pipeline.
    ///
    /// Names are MAHOD_-prefixed on purpose: the original LISP defines bare VDEL/VADD/VDIST/VTHIN,
    /// and a .NET command of the same name would collide on a machine where both are loaded.
    /// </summary>
    public class MahodPolylineCommands
    {
        // ── interactive vertex editing ────────────────────────────────

        [CommandMethod("MAHOD_VDEL", CommandFlags.Modal)]
        public void DeleteVertices() =>
            RunTool(new EditPolylineVerticesTool(), "{\"mode\":\"delete\"}");

        [CommandMethod("MAHOD_VADD", CommandFlags.Modal)]
        public void AddVertices() =>
            RunTool(new EditPolylineVerticesTool(), "{\"mode\":\"add\"}");

        [CommandMethod("MAHOD_VMOVE", CommandFlags.Modal)]
        public void MoveVertex() =>
            RunTool(new EditPolylineVerticesTool(), "{\"mode\":\"move\"}");

        [CommandMethod("MAHOD_VSTART", CommandFlags.Modal)]
        public void SetStartVertex() =>
            RunTool(new EditPolylineVerticesTool(), "{\"mode\":\"set_start\"}");

        // ── parametric cleanup ────────────────────────────────────────

        /// <summary>Weeding + Douglas-Peucker thinning, with the tolerance prompted for.</summary>
        [CommandMethod("MAHOD_VTHIN", CommandFlags.Modal)]
        public void ThinPolyline()
        {
            var handle = PickHandle("בחרי פוליליין לדילול:");
            if (handle == null) return;

            double? tolerance = PromptDistance("סטייה מרבית מהקו המקורי", _lastThinTolerance);
            if (tolerance == null) return;
            _lastThinTolerance = tolerance.Value;

            RunTool(
                new CleanPolylineVerticesTool(),
                $"{{\"entity_handle\":\"{handle}\",\"operations\":[\"coincident\",\"weed\",\"thin\"]," +
                $"\"thin_tolerance\":{Num(tolerance.Value)},\"deviation_tolerance\":{Num(tolerance.Value)}}}");
        }

        /// <summary>Coincident + collinear vertices, at the PLTOOLS defaults.</summary>
        [CommandMethod("MAHOD_VCLEAN", CommandFlags.Modal)]
        public void CleanPolyline()
        {
            var handle = PickHandle("בחרי פוליליין לניקוי קודקודים:");
            if (handle == null) return;

            RunTool(
                new CleanPolylineVerticesTool(),
                $"{{\"entity_handle\":\"{handle}\",\"operations\":[\"coincident\",\"weed\"]}}");
        }

        /// <summary>A vertex every N units along the polyline (the LISP's VDIST).</summary>
        [CommandMethod("MAHOD_VDIST", CommandFlags.Modal)]
        public void VerticesByDistance()
        {
            var handle = PickHandle("בחרי פוליליין להוספת קודקודים במרווח קבוע:");
            if (handle == null) return;

            double? spacing = PromptDistance("מרווח בין קודקודים", _lastSpacing);
            if (spacing == null) return;
            _lastSpacing = spacing.Value;

            RunTool(
                new DensifyPolylineTool(),
                $"{{\"entity_handle\":\"{handle}\",\"by\":\"distance\",\"distance\":{Num(spacing.Value)}}}");
        }

        // ── direction, joining, reporting ─────────────────────────────

        [CommandMethod("MAHOD_PLREV", CommandFlags.Modal)]
        public void ReversePolyline()
        {
            var handle = PickHandle("בחרי פוליליין להיפוך כיוון:");
            if (handle == null) return;

            RunTool(
                new SetPolylineDirectionTool(),
                $"{{\"entity_handles\":[\"{handle}\"],\"direction\":\"reverse\"}}");
        }

        [CommandMethod("MAHOD_PLINFO", CommandFlags.Modal)]
        public void SegmentInfo()
        {
            var handle = PickHandle("בחרי פוליליין לדוח מקטעים:");
            if (handle == null) return;

            RunTool(
                new ModifyPolylineSegmentsTool(),
                $"{{\"entity_handle\":\"{handle}\",\"action\":\"info\"}}");
        }

        [CommandMethod("MAHOD_PLHELP", CommandFlags.Modal)]
        public void Help()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var lines = new[]
            {
                "================ MahodAI — כלי פוליליין ================",
                "MAHOD_VDEL    מחיקת קודקודים בקליק, ברצף (גם על Hatch)",
                "MAHOD_VADD    הוספת קודקודים בקליק על מקטע, ברצף",
                "MAHOD_VMOVE   הזזת קודקוד",
                "MAHOD_VSTART  קביעת נקודת ההתחלה של פוליליין סגור",
                "MAHOD_VTHIN   דילול פוליליין לפי סטייה מרבית",
                "MAHOD_VCLEAN  הסרת קודקודים חופפים וקודקודים על קו ישר",
                "MAHOD_VDIST   הוספת קודקודים במרווח קבוע",
                "MAHOD_PLREV   היפוך כיוון הפוליליין",
                "MAHOD_PLINFO  דוח מקטעים (אורך, רדיוס, מרכז, זווית)",
                "בתוך מחיקה/הוספה: Enter מסיים, U מבטל את הקליק האחרון.",
                "כל פקודה היא פעולת Undo אחת — Ctrl+Z מחזיר את המצב שלפניה.",
                "=======================================================",
            };
            foreach (var line in lines) doc.Editor.WriteMessage("\n" + line);
            doc.Editor.WriteMessage("\n");
        }

        // ── plumbing ──────────────────────────────────────────────────

        private static double _lastThinTolerance = 0.10;
        private static double _lastSpacing = 1.0;

        /// <summary>
        /// Runs a registered tool exactly as the WebSocket executor would: on the UI thread,
        /// inside a document lock and one transaction, committing only when the v1.8 gate allows
        /// it (<c>outcome == succeeded</c> AND every hard gate passed).
        /// </summary>
        private static void RunTool(IDrawingTool tool, string parametersJson)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                using var docLock = doc.LockDocument();
                using var tr = doc.Database.TransactionManager.StartTransaction();

                CivilDocument? civilDoc = null;
                try { civilDoc = CivilDocument.GetCivilDocument(doc.Database); }
                catch { /* plain AutoCAD drawing — these tools do not need Civil objects */ }

                using var parameters = JsonDocument.Parse(parametersJson);

                // The polyline tools are synchronous inside (Task.FromResult), so this never
                // blocks the UI thread on real async work.
                var result = tool
                    .ExecuteAsync(tr, civilDoc!, parameters.RootElement, new ToolCache(), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (result.MayCommitProductionObject)
                {
                    tr.Commit();
                    ReportSuccess(ed, result);
                }
                else
                {
                    tr.Abort();
                    ReportRefusal(ed, result);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nשגיאה: {ex.Message}");
                Utilities.MahodLogger.Info($"[polyline-command] {tool.Name} failed: {ex}");
            }
            finally
            {
                ToolUiNotifier.ClearStep();
            }
        }

        private static void ReportSuccess(Editor ed, ToolResult result)
        {
            // The tools already write their own per-click progress; this is the closing line.
            var data = result.Data;
            if (data == null)
            {
                ed.WriteMessage("\nבוצע.");
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(data);
                using var parsed = JsonDocument.Parse(json);
                var root = parsed.RootElement;

                foreach (var key in new[]
                         {
                             "edits", "vertices_removed", "vertices_added", "polylines_reversed",
                             "converted", "chains_created", "segment_count",
                         })
                {
                    if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number)
                        ed.WriteMessage($"\n{key}: {value}");
                }

                foreach (var key in new[] { "notes", "warnings" })
                {
                    if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var note in array.EnumerateArray())
                        if (note.ValueKind == JsonValueKind.String)
                            ed.WriteMessage($"\n- {note.GetString()}");
                }

                if (root.TryGetProperty("segments", out var segments) &&
                    segments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var segment in segments.EnumerateArray())
                        ed.WriteMessage("\n  " + segment.GetRawText());
                }
            }
            catch (System.Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-command] report failed: {ex.Message}");
                ed.WriteMessage("\nבוצע.");
            }

            ed.WriteMessage("\n");
        }

        private static void ReportRefusal(Editor ed, ToolResult result)
        {
            if (result.Outcome == ToolOutcome.Cancelled)
            {
                ed.WriteMessage("\nבוטל — לא שונה דבר בשרטוט.\n");
                return;
            }

            var message = result.Error?.Message ?? $"outcome={result.Outcome}";
            var code = result.Error?.Code;
            ed.WriteMessage(code != null ? $"\n{message} [{code}]\n" : $"\n{message}\n");
        }

        /// <summary>Asks the engineer to click a polyline and returns its handle, or null.</summary>
        private static string? PickHandle(string prompt)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return null;

            using var pickScope = new Utilities.InteractivePickScope();
            var picked = PolylineToolSupport.PickEntity(doc.Editor, prompt);
            if (picked == null || picked.Status != PromptStatus.OK) return null;

            using var docLock = doc.LockDocument();
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var handle = tr.GetObject(picked.ObjectId, OpenMode.ForRead).Handle.ToString();
            tr.Commit();
            return handle;
        }

        /// <summary>Prompts for a positive distance with a remembered default, LISP-style.</summary>
        private static double? PromptDistance(string label, double current)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return null;

            var opts = new PromptDoubleOptions(
                $"\n{label} <{current.ToString("0.###", CultureInfo.InvariantCulture)}>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = current,
                UseDefaultValue = true,
            };

            var res = doc.Editor.GetDouble(opts);
            if (res.Status == PromptStatus.None) return current;    // Enter = keep the default
            if (res.Status != PromptStatus.OK) return null;         // Esc = abort
            return res.Value;
        }

        private static string Num(double value) =>
            value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
