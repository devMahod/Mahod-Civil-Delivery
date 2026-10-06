using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdSmokeSectionsCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_SMOKE_SECTIONS (directive §33): non-interactive real-runtime smoke that
    /// proves the full deterministic chain on the OPEN drawing (use a test copy):
    ///
    ///   PLAN → APPLY(all READY) → VERIFY → PLAN again (idempotency: 0 new creates)
    ///
    /// Emits machine-readable JSON to %LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\
    /// smoke_sections_latest.json so Arthur/Vadim/Cowork can run GUI smoke without
    /// Claude Code present. Never run on an original project drawing.
    /// </summary>
    public class MhdSmokeSectionsCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.SmokeSections, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            var report = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["command"] = CivilDeliveryCommandNames.SmokeSections,
                ["started_utc"] = DateTime.UtcNow.ToString("O"),
                ["drawing"] = doc.Database.Filename,
                ["civil_version"] = AcadApp.Version.ToString(),
                ["tool_version"] = SectionPlanService.ToolVersion,
            };
            var steps = new List<Dictionary<string, object?>>();
            report["steps"] = steps;
            bool ok = true;

            // The stage log is opened FIRST: if Civil hangs anywhere below, the last
            // flushed line names the exact call that never returned (directive §4).
            using var log = new StageLog("smoke_sections");
            report["stage_log"] = log.Path_;
            log.Info($"drawing={doc.Database.Filename}");

            var workflow = new SectionsWorkflowService { Log = log };

            // A shipped smoke command must never surprise an engineer: it APPLIES real
            // sections to the open drawing. Non-interactive by design, so the gate is
            // the drawing's location (the documented writable fixture folder) or an
            // explicit MHD_SMOKE_APPLY_OK=<file name> set by the operator.
            var refusal = SmokeApplyGuard.Refusal(
                doc.Database.Filename,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "MahodCivilDelivery_Fixtures"),
                Environment.GetEnvironmentVariable(SmokeApplyGuard.OverrideVariable));
            if (refusal != null)
            {
                log.Info("smoke_sections refused before any transaction: " + refusal);
                Step(steps, "guard", false, refusal, null);
                ok = false;
            }
            else
            try
            {
                // ------------------------------------------------------ profile
                var loaded = new ActiveProjectProfileService().LoadForDocument(doc, workflow);
                Step(steps, "load_profile", loaded.IsUsable,
                    $"profile={loaded.ProfileSource}", string.Join("; ", loaded.Findings.Select(f => f.Code)));
                if (!loaded.IsUsable || loaded.Profile == null) throw new InvalidOperationException("profile unusable");

                // --------------------------------------------------------- plan
                var plan = workflow.Plan(doc, loaded.Profile, loaded.ProfileHash);
                var ready = plan.Records.Count(r => r.Status == DeliveryStatus.Ready);
                Step(steps, "plan", true,
                    $"records={plan.Records.Count} ready={ready} review={plan.Records.Count(r => r.Status == DeliveryStatus.ReviewRequired)}",
                    plan.RunId, new Dictionary<string, object?>
                    {
                        ["statuses"] = plan.Records
                            .GroupBy(r => r.Status.ToString())
                            .ToDictionary(g => g.Key, g => g.Count()),
                        ["finding_codes"] = plan.Records.SelectMany(r => r.Findings)
                            .Concat(plan.Findings)
                            .GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.Count()),
                        // Nataly's core requirement: prove utilities were accounted for.
                        ["utility_coverage"] = plan.Records.Select(r => new
                        {
                            record = r.RecordId,
                            represented = r.UtilityCoverage.Represented,
                            not_configured = r.UtilityCoverage.NotConfigured,
                            missing = r.UtilityCoverage.Missing,
                            unsupported = r.UtilityCoverage.Unsupported.Keys.ToList(),
                            complete = r.UtilityCoverage.Complete,
                            projection_scan_state = r.UtilityCoverage.ProjectionScanState.ToString(),
                            projection_drawing_entity_count = r.UtilityCoverage.ProjectionDrawingEntityCount,
                            projection_section_crossing_count = r.UtilityCoverage.ProjectionSectionCrossingCount,
                        }).ToList(),
                    });

                if (ready == 0)
                {
                    Step(steps, "apply", false, "no READY records — smoke requires at least one unambiguous CL", plan.RunId);
                    ok = false;
                }
                else
                {
                    // -------------------------------------------------- apply 1
                    var apply = workflow.Apply(doc, loaded.Profile, loaded.ProfileHash, plan);
                    var applied = apply.Records.Count(r => r.Status == DeliveryStatus.Applied);
                    var authoritativeApply = IsAuthoritativeApply(apply) && applied > 0;
                    Step(steps, "apply", authoritativeApply,
                        $"applied={applied} committed={apply.Committed} status={apply.Status}", apply.RunId,
                        new Dictionary<string, object?>
                        {
                            ["handles"] = apply.Records
                                .Where(r => r.Status == DeliveryStatus.Applied)
                                .Select(r => new { r.RecordId, r.Handles.SampleLineGroup, r.Handles.SampleLine, r.Handles.SectionView })
                                .ToList(),
                            ["error_findings"] = apply.Findings
                                .Concat(apply.Records.SelectMany(record => record.Findings))
                                .Where(finding => finding.Severity == FindingSeverity.Error)
                                .Select(finding => finding.Code)
                                .ToList(),
                        });
                    ok &= authoritativeApply;

                    // ------------------------------------------------- verify 1
                    var verify = workflow.Verify(doc, loaded.Profile, loaded.ProfileHash, plan, apply);
                    var verified = verify.Records.Count(r => r.Status == DeliveryStatus.Verified);
                    var authoritativeVerify = authoritativeApply && IsAuthoritativeVerify(verify);
                    Step(steps, "verify", authoritativeVerify,
                        $"verified={verified}/{verify.Records.Count} checks={verify.Records.Sum(r => r.Checks.Count)} status={verify.Status}",
                        verify.RunId, new Dictionary<string, object?>
                        {
                            ["failed_checks"] = verify.Records
                                .SelectMany(r => r.Checks.Where(c => !c.Pass)
                                    .Select(c => $"{r.RecordId}:{c.Check} expected={c.Expected} actual={c.Actual}"))
                                .ToList(),
                            ["error_findings"] = verify.Findings
                                .Where(finding => finding.Severity == FindingSeverity.Error)
                                .Select(finding => finding.Code)
                                .ToList(),
                        });
                    ok &= authoritativeVerify;

                    // -------------------------------------- idempotency: plan 2
                    var plan2 = workflow.Plan(doc, loaded.Profile, loaded.ProfileHash);
                    var unchanged = plan2.Records.Count(r => r.Action == PlanAction.Unchanged);
                    var creates2 = plan2.Records.Count(r =>
                        r.Status == DeliveryStatus.Ready &&
                        r.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace);
                    var authoritativeIdempotency = authoritativeApply && authoritativeVerify &&
                                                   creates2 == 0 && unchanged >= applied;
                    Step(steps, "idempotency_replan", authoritativeIdempotency,
                        $"unchanged={unchanged} pending_creates={creates2} (expected 0)", plan2.RunId);
                    ok &= authoritativeIdempotency;
                }
            }
            catch (System.Exception ex)
            {
                log.Fail("smoke_sections", ex);
                Step(steps, "exception", false, ex.Message, ex.GetType().Name);
                ok = false;
            }

            report["completed_utc"] = DateTime.UtcNow.ToString("O");
            report["result"] = refusal != null ? "refused" : ok ? "pass" : "fail";

            var outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "smoke_sections_latest.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, SectionsWorkflowService.Json));

            ed.WriteMessage(
                $"\n{CivilDeliveryCommandNames.SmokeSections}: {(refusal != null ? "REFUSED — " + refusal : ok ? "PASS" : "FAIL")}" +
                $"\nדוח: {outPath}\nיומן שלבים: {log.Path_}\n");
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

        private static void Step(
            List<Dictionary<string, object?>> steps, string name, bool pass,
            string summary, string? refId, Dictionary<string, object?>? details = null)
        {
            steps.Add(new Dictionary<string, object?>
            {
                ["step"] = name,
                ["pass"] = pass,
                ["summary"] = summary,
                ["ref"] = refId,
                ["details"] = details,
            });
        }
    }
}
