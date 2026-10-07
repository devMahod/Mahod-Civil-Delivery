using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdSectionsCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_SECTIONS — the direct, deterministic Section Composer workflow:
    /// PLAN → PREVIEW → APPLY → VERIFY. Works with no chat, no AI service, no
    /// natural language (directive §3). Hebrew-first prompts.
    /// </summary>
    public class MhdSectionsCommand
    {
        private static readonly SectionsWorkflowService Workflow = new();
        private static SectionPlan? _lastPlan;
        private static SectionApplyResult? _lastApply;

        [CommandMethod(CivilDeliveryCommandNames.Sections, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!CivilRuntime.IsCivilAvailable())
            {
                ed.WriteMessage("\n" + CivilDeliveryCommandNames.Sections + " דורש Civil 3D — הפקודה אינה זמינה ב-AutoCAD רגיל.\n");
                return;
            }

            // ---------------------------------------------------------- profile
            var loaded = new ActiveProjectProfileService().LoadForDocument(doc, Workflow);
            if (!loaded.IsUsable || loaded.Profile == null)
            {
                ed.WriteMessage($"\nפרופיל פרויקט '{loaded.ProfileSource}' לא נטען:\n");
                foreach (var f in loaded.Findings)
                    ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");
                return;
            }
            var profile = loaded.Profile;
            ed.WriteMessage($"\nפרופיל נטען: {profile.ProfileId} — {profile.ProjectName}\n");
            foreach (var f in loaded.Findings.Where(f => f.Severity >= FindingSeverity.Warning))
                ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");

            // ------------------------------------------------- first-run setup
            // An unconfigured profile must never surface as "0 sections". Offer the
            // setup flow so the engineer configures from real drawing evidence.
            if (ProjectSetupService.NeedsSetup(profile))
            {
                ed.WriteMessage("\nהפרופיל עדיין לא מוגדר לשרטוט הזה (שכבות CL / מקורות חתך).\n");
                var setupNow = ed.GetKeywords(new PromptKeywordOptions(
                    "להריץ הגדרת פרויקט עכשיו? [כן/לא]: ", "Yes No"));
                if (setupNow.Status != PromptStatus.OK || setupNow.StringResult != "Yes")
                {
                    ed.WriteMessage("\nללא הגדרה אין מה לתכנן. הרץ " + CivilDeliveryCommandNames.Setup + " כשתהיה מוכן.\n");
                    return;
                }

                if (!MhdSetupCommand.RunInteractiveSetup(
                        ed, doc, profile, loaded.ProfileHash ??
                            throw new InvalidOperationException(
                                "The usable project profile has no exact source SHA-256."),
                        loaded.ProfileSource, loaded.ProfileWriteTarget,
                        loaded.ProfileWriteState ?? throw new InvalidOperationException(
                            "The loaded profile has no source/target CAS evidence.")))
                    return;

                // Re-read the saved profile so PLAN runs on exactly what was persisted.
                var reloaded = ActiveProjectProfileService.ReloadForExistingWorkflow(
                    doc, Workflow, profile.ProfileId,
                    loaded.ProfileSource, loaded.ProfileWriteTarget);
                if (reloaded.IsUsable && reloaded.Profile != null)
                {
                    profile = reloaded.Profile;
                    loaded = reloaded;
                }
                else
                {
                    ed.WriteMessage("\nהפרופיל שנשמר לא נטען מחדש באופן תקין — PLAN נחסם.\n");
                    return;
                }
            }

            // ------------------------------------------------------------- PLAN
            ed.WriteMessage("\nמריץ PLAN (קריאה בלבד)...\n");
            if (!TryClearPreview(ed, "PLAN")) return;
            // A fresh PLAN invalidates every prior APPLY baseline, including one left
            // by a previous invocation of this static command workflow.
            _lastApply = null;
            try
            {
                _lastPlan = Workflow.Plan(doc, profile, loaded.ProfileHash);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nPLAN נכשל: {ex.Message}\n");
                return;
            }

            PrintPlanTable(ed, _lastPlan);

            if (_lastPlan.Records.Count == 0)
            {
                ed.WriteMessage("\nלא נמצאו רשומות CL עם ההגדרה הנוכחית " +
                                $"(שכבות: {string.Join(", ", profile.Sections.Cl.LayerPatterns)}).\n" +
                                "הרץ " + CivilDeliveryCommandNames.Setup + " כדי לבחור שכבות CL אחרות לפי מה שקיים בשרטוט.\n");
                return;
            }

            // ------------------------------------------------------ action loop
            while (true)
            {
                // Keywords are built explicitly (global, local, display): parsing them
                // out of a Hebrew message crashed AutoCAD with "Couldn't parse local
                // keyword" on plain input (live, 30/08) - an unhandled exception dialog
                // is never an acceptable answer to a keystroke.
                var opts = new PromptKeywordOptions("\n\u05d1\u05d7\u05e8 \u05e4\u05e2\u05d5\u05dc\u05d4") { AllowNone = true };
                opts.Keywords.Add("Preview");
                opts.Keywords.Add("ClearPreview");
                opts.Keywords.Add("Apply");
                opts.Keywords.Add("Verify");
                opts.Keywords.Add("Exit");

                PromptResult res;
                try { res = ed.GetKeywords(opts); }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n\u05e7\u05dc\u05d8 \u05dc\u05d0 \u05de\u05d6\u05d5\u05d4\u05d4 ({ex.Message}) \u2014 Preview / Apply / Verify / Exit.\n");
                    continue;
                }
                if (res.Status != PromptStatus.OK || res.StringResult == "Exit") break;

                switch (res.StringResult)
                {
                    case "Preview":
                        if (!TryGetFreshProfile(
                                ed, doc, loaded, "PREVIEW",
                                out var previewProfile, out var previewProfileHash))
                            break;
                        var preview = Workflow.Preview(
                            doc, previewProfile, previewProfileHash, _lastPlan);
                        ed.WriteMessage($"\nמוצג חתך {preview.RecordId} מהמשטחים האמיתיים " +
                                        $"({preview.ProjectedUtilityCount} מערכות). {preview.ScopeNotice}. " +
                                        "לא נשמר בשרטוט.\n");
                        break;

                    case "ClearPreview":
                        if (TryClearPreview(ed, "ניקוי תצוגה"))
                            ed.WriteMessage("\nהתצוגה המקדימה נוקתה. השרטוט ללא שינוי.\n");
                        break;

                    case "Apply":
                        RunApply(ed, doc, loaded);
                        break;

                    case "Verify":
                        RunVerify(ed, doc, loaded);
                        break;
                }
            }

            if (!TryClearPreview(ed, "סיום הפקודה")) return;
            ed.WriteMessage($"\nתוצרי הריצה נשמרו תחת: {SectionsWorkflowService.RunsRoot}\n");
        }

        private void RunApply(
            Editor ed, Document doc,
            ActiveProjectProfileService.ActiveLoadResult loaded)
        {
            if (_lastPlan == null) return;

            var ready = _lastPlan.Records.Where(r => r.Status == DeliveryStatus.Ready).ToList();
            var creates = ready.Count(r => r.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace);
            var unchanged = ready.Count(r => r.Action == PlanAction.Unchanged);
            var allSignedExclusions = _lastPlan.Records.Count > 0 &&
                _lastPlan.Records.All(SectionPlanLogic.HasValidExplicitExclusion);

            if (creates == 0 && !allSignedExclusions)
            {
                ed.WriteMessage(unchanged > 0
                    ? $"\nאין שינוי להחלה — {unchanged} חתכים מסומנים Unchanged, אך PLAN לבדו אינו ראיית read-back; הרץ PLAN מחדש לאחר שינוי או בחר חתך לבדיקה.\n"
                    : "\nאין רשומות READY או החרגות חתומות להחלה. בדוק את טבלת ה-PLAN.\n");
                return;
            }

            PromptResult confirm;
            try
            {
                var copts = new PromptKeywordOptions(
                    $"\n\u05dc\u05d4\u05e4\u05d9\u05e7 \u05e8\u05d0\u05d9\u05d9\u05ea APPLY \u05de\u05dc\u05d0\u05d4 \u05dc-{_lastPlan.Records.Count} \u05e8\u05e9\u05d5\u05de\u05d5\u05ea ({creates} \u05dc\u05e9\u05d9\u05e0\u05d5\u05d9, {unchanged} \u05dc\u05d0 \u05d4\u05e9\u05ea\u05e0\u05d5) ?");
                copts.Keywords.Add("Yes");
                copts.Keywords.Add("No");
                confirm = ed.GetKeywords(copts);
            }
            catch (System.Exception)
            {
                ed.WriteMessage("\n\u05d4\u05d4\u05d7\u05dc\u05d4 \u05d1\u05d5\u05d8\u05dc\u05d4. \u05d4\u05e9\u05e8\u05d8\u05d5\u05d8 \u05dc\u05dc\u05d0 \u05e9\u05d9\u05e0\u05d5\u05d9.\n");
                return;
            }
            if (confirm.Status != PromptStatus.OK || confirm.StringResult != "Yes")
            {
                ed.WriteMessage("\nההחלה בוטלה. השרטוט ללא שינוי.\n");
                return;
            }

            // The confirmation dialog can remain open while another process edits
            // the project profile. Re-read the authoritative source/target bytes
            // after the dialog and refuse the old PLAN if its hash no longer matches.
            if (!TryGetFreshProfile(
                    ed, doc, loaded, "APPLY", out var profile, out var profileHash))
                return;

            if (!TryClearPreview(ed, "APPLY")) return;
            // A failed retry must not leave an older successful APPLY available to
            // VERIFY as though it belonged to the attempt the operator just made.
            _lastApply = null;
            try
            {
                _lastApply = Workflow.Apply(doc, profile, profileHash, _lastPlan);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nAPPLY נכשל (בוצע rollback): {ex.Message}\n");
                return;
            }

            var applied = _lastApply.Records.Count(r => r.Status == DeliveryStatus.Applied);
            var failed = _lastApply.Records.Count(r => r.Status == DeliveryStatus.Failed);
            var applyFindings = _lastApply.Findings
                .Concat(_lastApply.Records.SelectMany(r => r.Findings))
                .ToList();
            var authoritativeApply = IsAuthoritativeApply(_lastApply);
            ed.WriteMessage(authoritativeApply
                ? $"\nAPPLY הושלם: {applied} חתכים נוצרו (SampleLine + SectionView אמיתיים). {failed} כשלונות.\n"
                : _lastApply.Committed
                    ? "\nAPPLY נכשל לאחר commit — השרטוט עשוי להשתנות, אך אין תוצאה מוסמכת; VERIFY ומסירה חסומים.\n"
                    : "\nAPPLY בוטל עם rollback מלא — השרטוט ללא חצי-יצירה.\n");
            foreach (var f in applyFindings)
                ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");
        }

        private void RunVerify(
            Editor ed, Document doc,
            ActiveProjectProfileService.ActiveLoadResult loaded)
        {
            if (_lastPlan == null || _lastApply == null)
            {
                ed.WriteMessage("\nVERIFY דורש APPLY קודם באותה ריצה.\n");
                return;
            }

            if (!TryGetFreshProfile(
                    ed, doc, loaded, "VERIFY", out var profile, out var profileHash))
                return;

            SectionVerifyResult verify;
            try
            {
                verify = Workflow.Verify(doc, profile, profileHash, _lastPlan, _lastApply);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nVERIFY נכשל: {ex.Message}\n");
                return;
            }

            var authoritativeVerify = IsAuthoritativeVerify(verify);
            foreach (var r in verify.Records)
            {
                var failedChecks = r.Checks.Where(c => !c.Pass).ToList();
                var rowVerified = authoritativeVerify &&
                                  r.Status == DeliveryStatus.Verified &&
                                  failedChecks.Count == 0;
                ed.WriteMessage($"\n  {r.RecordId}: {(rowVerified ? "VERIFIED ✓" : "FAILED ✗")} " +
                                $"({r.Checks.Count(c => c.Pass)}/{r.Checks.Count} בדיקות)\n");
                foreach (var c in failedChecks)
                    ed.WriteMessage($"      ✗ {c.Check}: צפוי {c.Expected}, בפועל {c.Actual}\n");
            }
            foreach (var finding in verify.Findings)
                ed.WriteMessage($"  [{finding.Severity}] {finding.Code}: {finding.Title}\n");
            ed.WriteMessage(authoritativeVerify
                ? "\nסטטוס VERIFY כולל: VERIFIED ✓\n"
                : $"\nסטטוס VERIFY כולל: FAILED ✗ ({verify.Status})\n");
        }

        private static bool TryGetFreshProfile(
            Editor ed,
            Document doc,
            ActiveProjectProfileService.ActiveLoadResult original,
            string stage,
            out ProjectProfile profile,
            out string? profileHash)
        {
            profile = null!;
            profileHash = null;
            if (_lastPlan == null) return false;
            try
            {
                var current = ActiveProjectProfileService.ReloadForExistingWorkflow(
                    doc, Workflow, _lastPlan.ProjectProfileId,
                    original.ProfileSource, original.ProfileWriteTarget);
                if (!current.IsUsable || current.Profile == null ||
                    string.IsNullOrWhiteSpace(current.ProfileHash))
                    throw new InvalidOperationException(
                        "פרופיל הפרויקט הנוכחי חסר או אינו תקין.");
                var staleReason = SectionPlanLogic.ScopeStaleReason(
                    _lastPlan, DrawingScopeIdentity.For(doc),
                    current.Profile, current.ProfileHash);
                if (staleReason != null)
                    throw new InvalidOperationException(staleReason);
                profile = current.Profile;
                profileHash = current.ProfileHash;
                return true;
            }
            catch (System.Exception ex)
            {
                _lastApply = null;
                var previewCleared = TryClearPreview(
                    ed, stage + " — ביטול תצוגה ישנה");
                if (!previewCleared)
                    ed.WriteMessage(
                        "\nהתצוגה הישנה עדיין פעילה; גם ניקויה חייב להצליח לפני ניסיון חדש.\n");
                ed.WriteMessage(
                    $"\n{stage} סורב — פרופיל הפרויקט השתנה, נעלם או אינו קריא מאז PLAN: " +
                    $"{ex.Message}\nיש להריץ {CivilDeliveryCommandNames.Sections} מחדש.\n");
                return false;
            }
        }

        private static bool TryClearPreview(Editor ed, string stage)
        {
            try
            {
                Workflow.ClearPreview();
                if (Workflow.HasActivePreview)
                    throw new InvalidOperationException(
                        "Section preview handles remain active after cleanup.");
                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage(
                    $"\n✗ {stage} נחסם — התצוגה המקדימה לא נוקתה במלואה: {ex.Message}\n" +
                    "אין להמשיך ל-PLAN/APPLY עד שניסיון ניקוי נוסף מצליח.\n");
                return false;
            }
        }

        private static bool IsAuthoritativeApply(SectionApplyResult apply) =>
            apply.Committed &&
            apply.Status is DeliveryStatus.Applied or DeliveryStatus.Verified &&
            !apply.Findings.Concat(apply.Records.SelectMany(record => record.Findings))
                .Any(finding => string.Equals(finding.Code,
                    SectionFindingCodes.EvidenceWriteFailed, StringComparison.Ordinal)) &&
            !apply.Findings.Concat(apply.Records.SelectMany(record => record.Findings))
                .Any(finding => finding.Severity == FindingSeverity.Error) &&
            apply.Records.Count > 0 &&
            apply.Records.All(record =>
                record.Status is DeliveryStatus.Applied or DeliveryStatus.Verified);

        private static bool IsAuthoritativeVerify(SectionVerifyResult verify) =>
            verify.Status == DeliveryStatus.Verified &&
            !verify.Findings.Any(finding => string.Equals(finding.Code,
                SectionFindingCodes.EvidenceWriteFailed, StringComparison.Ordinal)) &&
            !verify.Findings.Any(finding => finding.Severity == FindingSeverity.Error) &&
            verify.Records.Count > 0 &&
            verify.Records.All(record =>
                record.Status == DeliveryStatus.Verified &&
                record.Checks.All(check => check.Pass));

        internal static void PrintPlanTable(Editor ed, SectionPlan plan)
        {
            ed.WriteMessage($"\n===== תוכנית חתכים — {plan.Records.Count} רשומות CL =====\n");
            ed.WriteMessage("  מזהה               | תוואי           | תחנה      | הטיה   | שמאל  | ימין  | פעולה    | סטטוס\n");
            foreach (var r in plan.Records)
            {
                ed.WriteMessage(string.Format("  {0,-18} | {1,-15} | {2,9} | {3,6} | {4,5} | {5,5} | {6,-8} | {7}\n",
                    Trunc(r.SectionId ?? r.RecordId, 18),
                    Trunc(r.SelectedAlignment ?? "—", 15),
                    r.Station?.ToString("F1") ?? "—",
                    r.SkewDeg?.ToString("F1") ?? "—",
                    r.LeftExtent?.ToString("F1") ?? "—",
                    r.RightExtent?.ToString("F1") ?? "—",
                    r.Action,
                    r.Status));
                var uc = r.UtilityCoverage;
                if (uc.Relevant.Count > 0 || uc.Represented.Count > 0 || uc.Missing.Count > 0)
                {
                    ed.WriteMessage("      מערכות: "
                        + (uc.Represented.Count > 0 ? string.Join(", ", uc.Represented) : "אין")
                        + (uc.NotConfigured.Count > 0 ? " | לא מוגדרות: " + string.Join(", ", uc.NotConfigured) : "")
                        + (uc.Missing.Count > 0 ? " | חסרות: " + string.Join(", ", uc.Missing) : "")
                        + (uc.Unsupported.Count > 0 ? " | לא נתמכות: " + string.Join(", ", uc.Unsupported.Keys) : "")
                        + "\n");
                }
                foreach (var f in r.Findings)
                    ed.WriteMessage($"      -> [{f.Severity}] {f.Code}: {f.Title}\n");
            }
            foreach (var f in plan.Findings)
                ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");
            ed.WriteMessage($"  סטטוס כולל: {plan.Status}\n");
        }

        private static string Trunc(string s, int len) =>
            s.Length <= len ? s : s[..(len - 1)] + "…";
    }

    /// <summary>Civil availability gate (§2A.1 — clear message instead of a fake AutoCAD path).</summary>
    internal static class CivilRuntime
    {
        public static bool IsCivilAvailable()
        {
            try
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return false;
                _ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(doc.Database);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
