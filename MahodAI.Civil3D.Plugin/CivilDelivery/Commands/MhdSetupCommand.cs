using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdSetupCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_SETUP — "הגדרת פרויקט": the first-run flow that makes an empty
    /// ProjectProfile usable. Scans the drawing for real CL/alignment/source
    /// candidates, shows the engineer what was found with the evidence behind it,
    /// records their explicit choices, and saves them to the profile with
    /// provenance. Nothing is ever auto-selected.
    /// </summary>
    public class MhdSetupCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.Setup, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!CivilRuntime.IsCivilAvailable())
            {
                ed.WriteMessage("\n" + CivilDeliveryCommandNames.Setup + " דורש Civil 3D.\n");
                return;
            }

            var workflow = new SectionsWorkflowService();
            var loaded = new ActiveProjectProfileService().LoadForDocument(doc, workflow);
            if (!loaded.IsUsable || loaded.Profile == null)
            {
                ed.WriteMessage($"\nפרופיל '{loaded.ProfileSource}' לא נמצא או אינו תקין:\n");
                foreach (var f in loaded.Findings)
                    ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");
                return;
            }

            RunInteractiveSetup(
                ed, doc, loaded.Profile, loaded.ProfileHash ??
                    throw new InvalidOperationException(
                        "The usable project profile has no exact source SHA-256."),
                loaded.ProfileSource, loaded.ProfileWriteTarget,
                loaded.ProfileWriteState ?? throw new InvalidOperationException(
                    "The loaded profile has no source/target CAS evidence."));
        }

        /// <summary>
        /// The shared setup conversation. Returns true when the profile was
        /// configured and saved, false when the engineer cancelled.
        /// </summary>
        public static bool RunInteractiveSetup(
            Editor ed,
            Document doc,
            ProjectProfile profile,
            string profileHash,
            string profileSource,
            string profileWriteTarget,
            ProjectProfileWriter.ExpectedProfileState profileWriteState)
        {
            using var log = new StageLog("setup");
            var service = new ProjectSetupService();

            ed.WriteMessage("\n=== הגדרת פרויקט — סריקת השרטוט לאיתור מועמדים ===\n");
            ProjectSetupScan scan;
            try
            {
                scan = service.Scan(
                    doc, profile, profileHash, profileSource,
                    profileWriteTarget, profileWriteState, log);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nהסריקה נכשלה: {ex.Message}\n");
                return false;
            }

            foreach (var f in scan.Findings)
                ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n      {f.Message}\n");

            // ------------------------------------------------------- CL layers
            if (scan.ClLayerCandidates.Count == 0)
            {
                ed.WriteMessage("\nלא נמצאו שכבות מועמדות ל-CL בשרטוט הזה. " +
                                "פתח את מודל Civil שמכיל את התוואים ומקורות החתך, או הישאר בו אם הוא כבר פתוח.\n" +
                                "אם ה-CL בקובץ נפרד: בפאנל Civil Delivery בחר 'בחר קובץ CL…', " +
                                "בחר את הקובץ ואשר את שכבת ה-CL, ואז לחץ 'תכנון'. " +
                                "אין צורך לפתוח את קובץ ה-CL לבדו או לצרף אותו כ-XREF.\n" +
                                "אם קווי ה-CL נמצאים במודל עצמו או ב-XREF שכבר מצורף אליו, " +
                                "ודא שהם זמינים וחוצים תוואי, ואז הרץ שוב 'הגדרת פרויקט' (" + CivilDeliveryCommandNames.Setup + ") באותו מודל.\n");
                return false;
            }

            ed.WriteMessage("\nשכבות מועמדות ל-CL (מדורג לפי ראיות — הבחירה שלך):\n");
            var shown = scan.ClLayerCandidates.Take(15).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                var c = shown[i];
                ed.WriteMessage($"  [{i + 1}] {c.Layer}  (ציון {c.EvidenceScore})\n");
                ed.WriteMessage($"       קווים={c.LineCount} פוליליינים={c.PolylineCount} " +
                                $"דו-נקודתיים={c.TwoPointCount} חוצים={c.CrossingCount} " +
                                $"אורך חציוני={c.MedianLength:F1} מ'{(c.InXref ? " [XREF]" : "")}\n");
                if (c.AlignmentsCrossed.Count > 0)
                    ed.WriteMessage($"       חוצה תוואים: {string.Join(", ", c.AlignmentsCrossed)}\n");
                foreach (var why in c.Why)
                    ed.WriteMessage($"       · {why}\n");
                if (c.SampleLabels.Count > 0)
                    ed.WriteMessage($"       תוויות סמוכות: {string.Join(" | ", c.SampleLabels.Take(4))}\n");
            }

            var clPick = PromptIndexList(ed, "\nבחר מספרי שכבות CL (למשל 1 או 1,3): ", shown.Count);
            if (clPick.Count == 0)
            {
                ed.WriteMessage("\nההגדרה בוטלה — לא נשמר דבר.\n");
                return false;
            }
            var chosenLayers = clPick.Select(i => shown[i - 1].Layer).ToList();

            // ------------------------------------------------------ alignments
            var chosenAlignments = new List<string>();
            if (scan.Alignments.Count == 0)
            {
                ed.WriteMessage("\nאין תוואים בשרטוט — לא ניתן להשלים הגדרה.\n");
                return false;
            }
            if (scan.Alignments.Count == 1)
            {
                chosenAlignments.Add(scan.Alignments[0].Name);
                ed.WriteMessage($"\nתוואי יחיד בשרטוט: {scan.Alignments[0].Name} (ייבחר כמותר).\n");
            }
            else
            {
                ed.WriteMessage("\nתוואים בשרטוט:\n");
                for (int i = 0; i < scan.Alignments.Count; i++)
                {
                    var a = scan.Alignments[i];
                    ed.WriteMessage($"  [{i + 1}] {a.Name}  תחנות {a.StartStation:F0}–{a.EndStation:F0} " +
                                    $"(אורך {a.Length:F0} מ')" +
                                    (a.CrossedByCandidateLayers.Count > 0
                                        ? $"  ← נחצה ע\"י {string.Join(",", a.CrossedByCandidateLayers.Distinct())}"
                                        : "") + "\n");
                }
                ed.WriteMessage("  [0] כל התוואים מותרים (לא ממליץ — משאיר רב-משמעות ל-PLAN)\n");
                var alignPick = PromptIndexList(ed, "\nבחר תוואים מותרים: ", scan.Alignments.Count, allowZero: true);
                if (alignPick.Count == 0)
                {
                    ed.WriteMessage("\nההגדרה בוטלה.\n");
                    return false;
                }
                if (!alignPick.Contains(0))
                    chosenAlignments.AddRange(alignPick.Select(i => scan.Alignments[i - 1].Name));
            }

            // --------------------------------------------------------- sources
            var chosenSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (scan.Sources.Count == 0)
            {
                ed.WriteMessage(
                    "\nהגדרה חלקית לא נשמרה: אין משטחים/קורידורים/רשתות זמינים לדגימה. " +
                    "טען לפחות מקור חתך אחד והרץ " + CivilDeliveryCommandNames.Setup + " שוב.\n");
                return false;
            }
            else
            {
                ed.WriteMessage("\nמקורות זמינים לדגימה בחתך:\n");
                for (int i = 0; i < scan.Sources.Count; i++)
                {
                    var s = scan.Sources[i];
                    ed.WriteMessage($"  [{i + 1}] {s.Name}  ({s.Kind})\n");
                }
                var srcPick = PromptIndexList(ed, "\nבחר מקורות נדרשים לחתך: ", scan.Sources.Count);
                if (srcPick.Count == 0)
                {
                    ed.WriteMessage(
                        "\nהגדרה חלקית לא נשמרה — יש לבחור לפחות מקור חתך אחד. " +
                        "אפשר להריץ " + CivilDeliveryCommandNames.Setup + " שוב לאחר טעינת המשטח/קורידור/הרשת הנדרשים.\n");
                    return false;
                }
                foreach (var i in srcPick)
                {
                    var s = scan.Sources[i - 1];
                    chosenSources[s.Name] = s.Kind;
                }
            }

            // ------------------------------------------------------- tolerance
            double? tolerance = null;
            var tolOpts = new PromptDoubleOptions(
                "\nסבילות חיתוך CL↔תוואי במטרים (0 = חיתוך גיאומטרי מדויק בלבד): ")
            { AllowNegative = false, AllowNone = true, DefaultValue = 0.0, UseDefaultValue = true };
            var tolRes = ed.GetDouble(tolOpts);
            if (tolRes.Status == PromptStatus.OK && tolRes.Value > 0)
            {
                if (tolRes.Value > 5)
                    ed.WriteMessage("  סבילות מעל 5 מ' אינה מתקבלת — נשמר חיתוך מדויק.\n");
                else
                    tolerance = tolRes.Value;
            }

            // ------------------------------------------------------------ save
            var approver = CommandApprover.Require(ed, "הגדרת הפרויקט");
            if (approver == null) return false;
            var selection = new ProjectSetupSelection
            {
                ClLayers = chosenLayers,
                // Keep whatever CL drawing is already configured (the engineer may have
                // picked an external CL.dwg in the panel) and add this drawing, rather
                // than replacing the list and silently emptying her sections.
                ClSourceFiles = ClSourceSelection.MergeSources(
                    profile.Sections.Cl.SourceFiles, scan.Drawing),
                AllowedAlignments = chosenAlignments,
                SampledSources = chosenSources,
                IntersectionToleranceM = tolerance,
                ApprovedBy = approver,
            };

            ed.WriteMessage("\n--- סיכום לאישור ---\n");
            ed.WriteMessage($"  שכבות CL      : {string.Join(", ", selection.ClLayers)}\n");
            ed.WriteMessage($"  שרטוט CL      : {string.Join(", ", selection.ClSourceFiles)}\n");
            ed.WriteMessage($"  תוואים מותרים : {(selection.AllowedAlignments.Count == 0 ? "כולם" : string.Join(", ", selection.AllowedAlignments))}\n");
            ed.WriteMessage($"  מקורות חתך    : {(selection.SampledSources.Count == 0 ? "ללא" : string.Join(", ", selection.SampledSources.Select(kv => $"{kv.Key} ({kv.Value})")))}\n");
            ed.WriteMessage($"  סבילות        : {(tolerance?.ToString("F2") ?? "חיתוך מדויק")}\n");
            ed.WriteMessage($"  מאשר          : {selection.ApprovedBy}\n");

            var confirm = ed.GetKeywords(new PromptKeywordOptions("\nלשמור לפרופיל הפרויקט? [כן/לא]: ", "Yes No"));
            if (confirm.Status != PromptStatus.OK || confirm.StringResult != "Yes")
            {
                ed.WriteMessage("\nלא נשמר.\n");
                return false;
            }

            try
            {
                var saved = ProjectSetupService.Save(
                    doc, profile, selection, scan, profileHash,
                    profileSource, profileWriteTarget);
                ed.WriteMessage($"\nנשמר: {saved.Path}\n  גרסת פרופיל: {saved.NewVersion}\n");
                if (!string.IsNullOrEmpty(saved.BackupPath))
                    ed.WriteMessage($"  גיבוי קודם: {saved.BackupPath}\n");
                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nשמירת הפרופיל נכשלה: {ex.Message}\n");
                return false;
            }
        }

        /// <summary>Reads a comma/space separated list of 1-based indices from the command line.</summary>
        private static List<int> PromptIndexList(Editor ed, string prompt, int max, bool allowZero = false)
        {
            var res = ed.GetString(new PromptStringOptions(prompt) { AllowSpaces = true });
            if (res.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(res.StringResult))
                return new List<int>();

            var picks = new List<int>();
            foreach (var token in res.StringResult.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token.Trim(), out var n)) continue;
                if (n == 0 && allowZero) { picks.Add(0); continue; }
                if (n >= 1 && n <= max && !picks.Contains(n)) picks.Add(n);
            }
            return picks;
        }
    }
}
