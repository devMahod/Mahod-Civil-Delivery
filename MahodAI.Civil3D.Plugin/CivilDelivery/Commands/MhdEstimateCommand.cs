using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdEstimateCommand))]
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdEstimateScanAfterSaveCommand))]
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdSmokeEstimateCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_ESTIMATE — deterministic early-estimate workflow without any LLM:
    /// scan → quantity records → preflight → build → Excel + trace (plan §8.12).
    /// </summary>
    public class MhdEstimateCommand
    {
        private static readonly EstimateWorkflowService Workflow = new();

        [CommandMethod(CivilDeliveryCommandNames.Estimate, CommandFlags.Modal)]
        public void Run()
        {
            using var diagnostic = EstimateScanTrace.Start(CivilDeliveryCommandNames.Estimate);
            EstimateScanTrace.Mark("command.entry");
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            var sections = new SectionsWorkflowService();
            var loaded = EstimateScanTrace.Step("profile.load", () => new ActiveProjectProfileService().LoadForDocument(doc, sections));
            if (!loaded.IsUsable || loaded.Profile == null)
            {
                ed.WriteMessage($"\nפרופיל '{loaded.ProfileSource}' לא נטען:\n");
                foreach (var f in loaded.Findings)
                    ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");
                return;
            }
            var profile = loaded.Profile;

            var source = DrawingRevisionTracker.CaptureLive(doc);
            var sourceReadiness = EstimateSourceSnapshotPolicy.ForScan(
                source.DrawingPath, source.DrawingHash, source.DbMod, source.Failure);
            if (!sourceReadiness.IsReady)
            {
                ed.WriteMessage($"\nסריקת האומדן לא התחילה: {sourceReadiness.Title}\n" +
                                $"{sourceReadiness.Detail}\n");
                if (sourceReadiness.CanSaveAndResume)
                    ed.WriteMessage("שמור את השרטוט במפורש והרץ " + CivilDeliveryCommandNames.Estimate + " שוב; הפקודה אינה שומרת קבצים בשקט.\n");
                return;
            }

            // ------------------------------------------------------------- scan
            using var log = new StageLog("estimate");
            Workflow.Log = log;

            ed.WriteMessage("\nסורק מקורות כמויות (קריאה בלבד)...\n");
            var scan = Workflow.Scan(
                doc, profile, loaded.ProfileHash, loaded.ProfileSource,
                loaded.ProfileWriteTarget,
                loaded.ProfileWriteState ?? throw new InvalidOperationException(
                    "The loaded profile has no source/target CAS evidence."));
            ed.WriteMessage($"נסרקו {scan.ScannedEntities} ישויות; נוצרו {scan.Records.Count} רשומות כמות.\n");

            if (scan.DiscoveryMode)
            {
                ed.WriteMessage("\n*** מצב גילוי: אין חוקי מיפוי בפרופיל. ***\n" +
                                "כל כמות נמדדה בפועל ומסומנת 'דרוש מיפוי' — מיפוי חסר אינו כמות אפס.\n");
            }

            var groups = scan.Records
                .GroupBy(r => r.Classification.RuleKey ?? "(ללא חוק)")
                .OrderByDescending(g => g.Count())
                .ToList();

            ed.WriteMessage("\nכמויות שנמדדו:\n");
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var first = g.First();
                ed.WriteMessage($"  [{i + 1}] {g.Key}\n" +
                                $"       {g.Count()} עצמים, סה\"כ {g.Sum(r => r.Measurement.RawValue):F2} {first.Measurement.Unit}" +
                                $"  -> {first.Classification.CandidateCatalogCode ?? "דרוש מיפוי קטלוגי"}\n");
            }
            foreach (var f in scan.Findings)
                ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");

            if (scan.Records.Count == 0)
            {
                ed.WriteMessage("\nלא נמצאה גיאומטריה מדידה בשרטוט הזה (פוליליינים/קווים/האצ'ים/בלוקים).\n");
                return;
            }

            // ---------------------------------------------------------- catalog
            var catalog = Workflow.LoadCatalog(profile, scan.ProfileSource);
            foreach (var f in catalog.Findings)
                ed.WriteMessage($"  [{f.Severity}] {f.Code}: {f.Title}\n");
            if (catalog.Snapshot == null)
            {
                ed.WriteMessage("\nהמחירון לא נטען/לא אומת — אי אפשר לבנות אומדן מתומחר.\n");
                return;
            }
            ed.WriteMessage($"\nמחירון: {catalog.Snapshot.SnapshotId} ({catalog.Snapshot.Items.Count} פריטים, " +
                            $"hash {catalog.Snapshot.FileHash[..12]}...)\n");

            // -------------------------------------------------- mapping approval
            var unmapped = groups.Where(g => g.First().Classification.CandidateCatalogCode == null).ToList();
            if (unmapped.Count > 0)
            {
                var mapNow = ed.GetKeywords(new PromptKeywordOptions(
                    $"\nלמפות עכשיו {unmapped.Count} קבוצות ללא מיפוי לסעיפי קטלוג? [כן/לא]: ", "Yes No"));
                if (mapNow.Status == PromptStatus.OK && mapNow.StringResult == "Yes")
                {
                    // Ranked suggestions from the pinned catalog + the delivered
                    // reference estimate. Suggestions only — approval stays manual.
                    var proposals = Workflow.ProposeMappings(scan, catalog.Snapshot, profile);
                    if (!ApproveMappings(
                            doc, ed, profile, loaded.ProfileWriteTarget,
                            catalog.Snapshot, scan, unmapped, proposals))
                    {
                        ed.WriteMessage("\nאישור המיפויים לא הושלם עם ראיות חתומות — הפקודה נעצרה.\n");
                        return;
                    }
                    else
                    {
                        ed.WriteMessage("\nהמיפויים נשמרו. סורק מחדש עם החוקים המאושרים...\n");
                        var reloaded = ActiveProjectProfileService.ReloadForExistingWorkflow(
                            doc, sections, profile.ProfileId,
                            loaded.ProfileSource, loaded.ProfileWriteTarget);
                        if (!reloaded.IsUsable || reloaded.Profile == null)
                        {
                            ed.WriteMessage("\nהפרופיל שנשמר לא נטען מחדש באופן תקין — האומדן נחסם.\n");
                            return;
                        }
                        profile = reloaded.Profile;
                        scan = Workflow.Scan(
                            doc, profile, reloaded.ProfileHash,
                            reloaded.ProfileSource, reloaded.ProfileWriteTarget,
                            reloaded.ProfileWriteState ?? throw new InvalidOperationException(
                                "The reloaded profile has no source/target CAS evidence."));
                        ed.WriteMessage($"סריקה חוזרת: {scan.Records.Count} רשומות כמות.\n");
                    }
                }
            }

            // ------------------------------------------------------------ build
            var confirm = ed.GetKeywords(new PromptKeywordOptions(
                "\nלבנות אומדן ולייצא ל-Excel? [כן/לא]: ", "Yes No"));
            if (confirm.Status != PromptStatus.OK || confirm.StringResult != "Yes") return;

            var estimate = Workflow.Build(doc, scan, catalog.Snapshot, profile);
            var included = estimate.Lines.Count(l => l.IncludedInTotals);
            ed.WriteMessage($"\nנבנו {estimate.Lines.Count} שורות אומדן; {included} כלולות בסה\"כ " +
                            $"({estimate.CleanTotal:N2} ₪); {estimate.ExcludedLineCount} דורשות טיפול.\n");

            foreach (var line in estimate.Lines.Where(l => !l.IncludedInTotals))
                ed.WriteMessage($"  ! {line.CatalogCode ?? line.RecordId}: " +
                                $"{string.Join("; ", line.Findings.Select(f => f.Code).Distinct())}\n");

            if (!EstimatePreflightPolicy.CanExport(estimate))
            {
                ed.WriteMessage("\nהייצוא נעצר: האומדן הוא טיוטת ביקורת ועדיין יש שורות או ממצאים לא פתורים.\n");
                foreach (var reason in EstimatePreflightPolicy.ExportBlockingReasons(estimate))
                    ed.WriteMessage($"  ! {reason}\n");
                return;
            }

            var written = Workflow.Export(doc, scan, estimate, profile);
            ed.WriteMessage($"\nנוצר: {written.XlsxPath}\n(עם audit.json צמוד; המקור לא נדרס)\n");
            ed.WriteMessage($"יומן שלבים: {log.Path_}\n");
        }

        /// <summary>
        /// Interactive mapping approval: for each discovered group the engineer types
        /// a catalog code (or skips). The code must exist and its unit must match the
        /// measured unit — the workflow refuses anything else.
        /// </summary>
        private static bool ApproveMappings(
            Document doc,
            Editor ed,
            ProjectProfile profile,
            string profileWriteTarget,
            CatalogSnapshot snapshot,
            EstimateWorkflowService.ScanResult scan,
            List<IGrouping<string, NeutralQuantityRecord>> unmapped,
            IReadOnlyList<MappingProposal> proposals)
        {
            var approvals = new List<EstimateWorkflowService.MappingApproval>();
            var exclusions = new List<EstimateWorkflowService.ClosedPolylineAlternativeExclusion>();
            var exactPairs = ClosedPolylineAlternativePolicy.FindUnambiguousExactPairs(scan.Records);
            var lockedAlternatives = new HashSet<string>(StringComparer.Ordinal);

            // If one alternative entered this scan with an approved mapping, the
            // other is not another mapping opportunity.  It needs an explicit
            // audited exclusion (the palette exposes the return/exclude controls).
            foreach (var pair in exactPairs)
            {
                var firstMapped = scan.Records.Any(record =>
                    string.Equals(record.Classification.RuleKey, pair.FirstRuleKey,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(record.Classification.CandidateCatalogCode));
                var secondMapped = scan.Records.Any(record =>
                    string.Equals(record.Classification.RuleKey, pair.SecondRuleKey,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(record.Classification.CandidateCatalogCode));
                if (firstMapped) lockedAlternatives.Add(pair.SecondRuleKey);
                if (secondMapped) lockedAlternatives.Add(pair.FirstRuleKey);
            }

            foreach (var group in unmapped)
            {
                if (lockedAlternatives.Contains(group.Key))
                {
                    ed.WriteMessage($"\n  קבוצה: {group.Key}\n" +
                                    "  דולג — החלופה השנייה לאותם פוליליינים סגורים כבר נבחרה; " +
                                    "אין לאשר area וגם perimeter.\n");
                    continue;
                }

                var first = group.First();
                var unit = first.Measurement.Unit;
                ed.WriteMessage($"\n  קבוצה: {group.Key}\n" +
                                $"  {group.Count()} עצמים · {group.Sum(r => r.Measurement.RawValue):F2} {unit} · " +
                                $"שכבה '{first.Source.Layer}' · {first.Source.EntityType}\n");

                var forGroup = proposals.Where(p => p.RuleKey == group.Key).ToList();
                if (forGroup.Count > 0)
                {
                    ed.WriteMessage("  הצעות (לא מאושרות — הבחירה שלך):\n");
                    foreach (var p in forGroup)
                    {
                        ed.WriteMessage($"    {p.ProposedCode}  [{p.CatalogUnit}]  {Truncate(p.CatalogDescription ?? "", 55)}\n");
                        foreach (var reason in p.Reasons.Take(2))
                            ed.WriteMessage($"        · {reason}\n");
                    }
                }
                else
                {
                    ed.WriteMessage("  אין הצעה מבוססת ראיות לקבוצה הזו — חפש במחירון ידנית.\n");
                }

                var codeRes = ed.GetString(new PromptStringOptions(
                    "  מס' קטלוגי (ENTER לדילוג): ")
                { AllowSpaces = false });
                if (codeRes.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(codeRes.StringResult))
                {
                    ed.WriteMessage("  דולג — יישאר 'דרוש מיפוי'.\n");
                    continue;
                }

                var code = codeRes.StringResult.Trim().ToUpperInvariant();
                if (!snapshot.Items.TryGetValue(code, out var item))
                {
                    ed.WriteMessage($"  '{code}' לא קיים במחירון — לא נשמר.\n");
                    continue;
                }

                var measured = Units.Parse(unit);
                if (!measured.SameUnit(item.Unit))
                {
                    ed.WriteMessage($"  אי-התאמת יחידות: נמדד {measured.Canonical}, קטלוג {item.Unit.Canonical} " +
                                    $"({item.UnitRaw}) — לא נשמר.\n");
                    continue;
                }

                ed.WriteMessage($"  ✓ {code} — {Truncate(item.Description, 60)} [{item.UnitRaw}]\n");
                var approval = new EstimateWorkflowService.MappingApproval(
                    RuleKey: group.Key,
                    CatalogCode: code,
                    LayerPattern: first.Source.Layer ?? "*",
                    EntityType: first.Source.EntityType,
                    MeasurementKind: first.Measurement.Kind,
                    MeasuredUnit: unit);
                approvals.Add(approval);

                var pair = exactPairs.SingleOrDefault(candidate =>
                    string.Equals(candidate.FirstRuleKey, group.Key, StringComparison.Ordinal) ||
                    string.Equals(candidate.SecondRuleKey, group.Key, StringComparison.Ordinal));
                if (pair != null)
                {
                    var alternative = pair.AlternativeOf(group.Key);
                    exclusions.Add(new EstimateWorkflowService.ClosedPolylineAlternativeExclusion(
                        group.Key, alternative));
                    lockedAlternatives.Add(alternative);
                    ed.WriteMessage($"  חלופת {alternative} תוחרג בהחלטה מתועדת באותה שמירה, " +
                                    "כדי שלא לתמחר area ו-perimeter של אותה גיאומטריה.\n");
                }
            }

            if (approvals.Count == 0)
            {
                ed.WriteMessage("\nלא אושרו מיפויים.\n");
                return false;
            }
            var approver = CommandApprover.Require(ed, "אישור המיפויים");
            if (approver == null) return false;

            try
            {
                EstimateWorkflowService.RequireFreshForDecision(
                    doc, scan, "אישור המיפויים בפקודת האומדן");
                var workflow = new EstimateWorkflowService();
                var expectedProfileState = scan.ProfileWriteState ??
                    throw new InvalidOperationException(
                        "The quantity scan has no load-time profile CAS evidence.");
                ManualMappingCaseScope.RequireLegacyScopeIsExact(scan.Records,
                    approvals.Select(approval => approval.RuleKey));
                var saved = exclusions.Count == 0
                    ? workflow.SaveApprovedMappings(
                        profile, snapshot, approvals, approver,
                        profileWriteTarget, expectedProfileState)
                    : workflow.SaveApprovedMappingsWithClosedPolylineExclusions(
                        profile, snapshot, scan, approvals, exclusions,
                        approver, profileWriteTarget,
                        expectedProfileState);
                EstimateWorkflowService.PublishProfileDecisionOrRestore(
                    scan, profile, saved, approvals.Select(approval => approval.RuleKey));
                ed.WriteMessage($"\nנשמרו {approvals.Count} מיפויים: {saved.Path} (גרסה {saved.NewVersion})\n");
                return true;
            }
            catch (System.Exception ex)
            {
                CivilDeliverySession.ClearEstimateContext();
                ed.WriteMessage($"\nשמירת המיפויים נכשלה: {ex.Message}\n");
                return false;
            }
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    /// <summary>
    /// Continuation queued after the user's explicit QSAVE.  It does not assume the
    /// save succeeded: the palette re-checks active-document identity, DBMOD, file
    /// existence and SHA-256 before it allows the scan to start.
    /// </summary>
    public class MhdEstimateScanAfterSaveCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.EstimateScanAfterSave, CommandFlags.Modal | CommandFlags.NoHistory)]
        public void Run() => CivilDeliveryPalette.ResumeEstimateScanAfterExplicitSave();
    }

    /// <summary>MHD_SMOKE_ESTIMATE — non-interactive estimate smoke with JSON evidence (directive §33).</summary>
    public class MhdSmokeEstimateCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.SmokeEstimate, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            var workflow = new EstimateWorkflowService();
            var report = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["command"] = CivilDeliveryCommandNames.SmokeEstimate,
                ["started_utc"] = DateTime.UtcNow.ToString("O"),
                ["drawing"] = doc.Database.Filename,
                ["civil_version"] = AcadApp.Version.ToString(),
            };
            var steps = new List<Dictionary<string, object?>>();
            report["steps"] = steps;
            bool ok = true;

            try
            {
                var loaded = new ActiveProjectProfileService().LoadForDocument(
                    doc, new SectionsWorkflowService());
                steps.Add(Step("load_profile", loaded.IsUsable, loaded.ProfileSource));
                if (!loaded.IsUsable || loaded.Profile == null) throw new InvalidOperationException("profile unusable");

                var scan = workflow.Scan(
                    doc, loaded.Profile, loaded.ProfileHash, loaded.ProfileSource,
                    loaded.ProfileWriteTarget,
                    loaded.ProfileWriteState ?? throw new InvalidOperationException(
                        "The loaded profile has no source/target CAS evidence."));
                steps.Add(Step("scan", scan.Records.Count > 0,
                    $"records={scan.Records.Count} scanned={scan.ScannedEntities} findings={scan.Findings.Count}"));
                ok &= scan.Records.Count > 0;

                var catalog = workflow.LoadCatalog(loaded.Profile, loaded.ProfileSource);
                steps.Add(Step("load_catalog", catalog.Snapshot != null,
                    catalog.Snapshot != null
                        ? $"{catalog.Snapshot.SnapshotId}: {catalog.Snapshot.Items.Count} items"
                        : string.Join("; ", catalog.Findings.Select(f => f.Code))));

                if (catalog.Snapshot != null && scan.Records.Count > 0)
                {
                    var estimate = workflow.Build(doc, scan, catalog.Snapshot, loaded.Profile);
                    var buildProducedLines = estimate.Lines.Count > 0;
                    steps.Add(Step("build", buildProducedLines,
                        $"lines={estimate.Lines.Count} clean_total={estimate.CleanTotal} excluded={estimate.ExcludedLineCount}"));
                    ok &= buildProducedLines;

                    if (EstimatePreflightPolicy.CanExport(estimate))
                    {
                        var written = workflow.Export(doc, scan, estimate, loaded.Profile);
                        var exported = File.Exists(written.XlsxPath) && File.Exists(written.AuditPath) &&
                                       File.Exists(written.ManifestPath);
                        steps.Add(Step("export", exported, written.XlsxPath));
                        report["export_disposition"] = "exported";
                        ok &= exported;
                    }
                    else
                    {
                        // A real production drawing commonly contains unmapped or
                        // unpriced rows. Refusing Excel is the correct fail-closed
                        // result, not a smoke failure. Prove the refusal is backed by
                        // explicit preflight codes and never claim that a workbook was
                        // created on this branch.
                        var blockers = EstimatePreflightPolicy.ExportBlockingReasons(estimate)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(code => code, StringComparer.Ordinal)
                            .ToList();
                        var blockedWithEvidence = blockers.Count > 0;
                        steps.Add(Step(
                            "export_preflight_blocked",
                            blockedWithEvidence,
                            blockedWithEvidence
                                ? "blocked_by=" + string.Join(",", blockers)
                                : "CanExport=false without explicit blocking reasons"));
                        report["export_disposition"] = "correctly_blocked";
                        report["export_blockers"] = blockers;
                        ok &= blockedWithEvidence;
                    }
                }
                else
                {
                    ok = false;
                }
            }
            catch (System.Exception ex)
            {
                steps.Add(Step("exception", false, ex.Message));
                ok = false;
            }

            report["completed_utc"] = DateTime.UtcNow.ToString("O");
            report["result"] = ok ? "pass" : "fail";

            var outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "smoke_estimate_latest.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, SectionsWorkflowService.Json));
            ed.WriteMessage($"\n{CivilDeliveryCommandNames.SmokeEstimate}: {(ok ? "PASS" : "FAIL")}\nדוח: {outPath}\n");
        }

        private static Dictionary<string, object?> Step(string name, bool pass, string summary) => new()
        {
            ["step"] = name,
            ["pass"] = pass,
            ["summary"] = summary,
        };
    }
}
