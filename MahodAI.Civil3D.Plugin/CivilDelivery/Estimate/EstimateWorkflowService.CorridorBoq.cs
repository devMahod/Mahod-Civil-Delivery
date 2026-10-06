using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    public sealed record CorridorBoqExportResult(string XlsxPath, string XlsxHash, string RunId, int Corridors, int Stations,
        int FailedStations, bool Complete, IReadOnlyList<string> Notes);

    /// <summary>
    /// "כמויות מקורידורים": excavation, fill, stripping, base courses and asphalt measured by the tool itself from the corridors
    /// of the open drawing (Natali 30.09.2026) — the native read (CorridorBoqCollector, read-only), the cut/fill kernel
    /// (CorridorBotSurfaceLogic), the material quantities and the NTI workbook (Core), written into a new evidence run folder
    /// with the raw receipt. A draft for engineering review: assumptions stay on their rows as "לאישור"; a failed or partial
    /// read is reported and never priced as zero.
    /// </summary>
    public CorridorBoqExportResult ExportCorridorBoq(Document doc, ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(profile);
        var rules = CorridorBoqExportGuard.LoadEmbeddedRules(out var rulesetSha256);
        CorridorBoqExportGuard.RequireIdentity(profile.ProfileId, rules, rulesetSha256);
        // Like the quantity scan: the tracker subscribes before DBMOD/hash, so quantities are bound to a saved, unchanged DWG.
        var startSource = DrawingRevisionTracker.CaptureLive(doc);
        var sourceFailure = startSource.Failure ?? EstimateSourceSnapshotPolicy.InitialFailure(startSource.DrawingHash, startSource.DbMod);
        if (sourceFailure != null)
            throw new InvalidOperationException("המדידה נעצרה: לא ניתן לקשור כמויות לגרסת DWG שמורה — " + sourceFailure + ". יש לשמור את השרטוט ולנסות שוב.");
        var drawingPath = startSource.DrawingPath;
        var drawingHash = startSource.DrawingHash;
        var runId = RunManifest.NewRunId("estimate", "corridor-boq");
        var pending = Path.Combine(Path.GetTempPath(), $"mcd-corridor-boq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pending);
        try
        {
            CorridorBoqCollector.Collected collected;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var units = HostDrawingUnitService.Scale(HostDrawingUnitService.Resolve(doc.Database, profile));
                if (!units.IsSupported)
                    throw new InvalidDataException("יחידות השרטוט אינן מוכחות — אי אפשר למדוד כמויות מקורידורים במטרים.");
                collected = CorridorBoqCollector.Collect(tr, doc.Database, CivilApplication.ActiveDocument, rules,
                    units.LinearToMetres, pending, drawingPath, drawingHash);
                tr.Abort(); // read-only: nothing is committed to the drawing
            }
            var endSource = DrawingRevisionTracker.CaptureLive(doc);
            var endFailure = endSource.Failure ?? EstimateSourceSnapshotPolicy.FreshnessFailure(
                startSource.DrawingHash, startSource.DatabaseRevision, endSource.DrawingHash, endSource.DatabaseRevision, endSource.DbMod);
            if (endFailure != null)
                throw new InvalidOperationException("המדידה בוטלה: השרטוט השתנה או אינו שמור באופן מלא — " + endFailure);
            var earthworks = CorridorBotSurfaceLogic.MeasureMetres(collected.Schedules, collected.Inputs, maxStationGapM: 50.0);
            var export = CorridorBoqExport.Build(rules, collected.Shapes, collected.Schedules, collected.Inputs, earthworks,
                collected.Skipped, collected.DrawingTables, collected.Evidence, collected.RegionIssues, collected.CorridorBlocks);
            var fileName = $"כמויות-מקורידורים-{SafeName(rules.Project)}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
            var tempXlsx = Path.Combine(pending, fileName);
            var context = new CorridorBoqExport.Context(DateTime.Now, drawingPath, drawingHash, collected.RawReceiptPath, collected.RawReceiptSha256);
            var xlsxHash = CorridorBoqExport.WriteWorkbook(export, tempXlsx, context);
            // The typed measurement record the combined bill ("כתב כמויות לפי כללים") prices chapters 51.01–51.04 from.
            var tempMeasures = Path.Combine(pending, CorridorBoqMeasuresFile.FileName);
            File.WriteAllText(tempMeasures, CorridorBoqMeasuresFile.Serialize(export, context, rulesetSha256, profile.ProfileId), new System.Text.UTF8Encoding(false));

            // The receipt/workbook must still describe the saved, unchanged host at publication time.
            var publishSource = DrawingRevisionTracker.CaptureLive(doc);
            var publishFailure = publishSource.Failure ?? EstimateSourceSnapshotPolicy.FreshnessFailure(
                startSource.DrawingHash, startSource.DatabaseRevision, publishSource.DrawingHash, publishSource.DatabaseRevision, publishSource.DbMod);
            if (publishFailure != null)
                throw new InvalidOperationException("הקובץ לא פורסם: השרטוט השתנה בזמן יצירתו — " + publishFailure + ". יש לשמור ולמדוד מחדש.");

            SectionsWorkflowService.PersistEvidenceBundle(runId, "corridor_boq_export.json",
                new
                {
                    Scope = "corridor-boq-draft-not-approved-estimate",
                    Drawing = drawingPath,
                    DrawingSha256 = drawingHash,
                    Ruleset = rules.Project + " corridor v" + rules.Version,
                    RulesetSha256 = rulesetSha256,
                    PricebookId = rules.Pricebook.Id,
                    PricebookEdition = rules.Pricebook.Edition,
                    PricebookSourceSha256 = rules.Pricebook.SourceSha256,
                    Surface = collected.SurfaceName,
                    LinkCode = collected.LinkCode,
                    RawReceipt = Path.GetFileName(collected.RawReceiptPath),
                    RawReceiptSha256 = collected.RawReceiptSha256,
                    EarthworksStatus = earthworks.Status,
                    Issues = earthworks.Issues.Select(i => new { i.Code, Corridor = i.Key?.CorridorId, Region = i.Key?.RegionId, i.StationM, i.Detail }),
                    // Every corridor's measures and all its findings (earthworks, materials, notes) — the coverage sheet shows the first few.
                    Measures = export.Measures.Select(m => new
                    {
                        m.CorridorId, m.EarthworksComplete, m.MaterialsComplete, m.InterfaceStatus, m.Stations, m.LengthM, m.VolCut, m.VolFill,
                        m.PlanCut, m.PlanFill, m.Plan3DCut, m.Plan3DFill, m.AsphaltPlanArea, m.CodeVolume, m.CodePlanArea,
                        m.PostCut, m.PostFill, m.StripDebit, m.StripDepthM, m.StripMethodId,
                        IssueCount = m.Issues.Count, m.Issues,
                    }),
                    Skipped = collected.Skipped.Select(s => new { s.Corridor, s.Reason }),
                    XlsxFile = fileName,
                    XlsxHash = xlsxHash,
                    Notes = export.Notes,
                },
                (pendingRoot, publishedRoot) => RuntimeRunManifestService.Write(runId,
                    "estimate", "corridor-boq", profile.ProfileId, null,
                    export.Complete ? DeliveryStatus.ReviewRequired : DeliveryStatus.Blocked,
                    new Dictionary<string, int>
                    {
                        ["corridors"] = export.Corridors,
                        ["stations"] = collected.Inputs.Count,
                        ["failed_stations"] = collected.Inputs.Count(i => !i.ReadSucceeded),
                        ["issues"] = earthworks.Issues.Count,
                    },
                    new List<DeliveryFinding>(),
                    new List<RunManifestInput> { new(drawingPath, string.IsNullOrWhiteSpace(drawingHash) ? null : drawingHash) },
                    runsRoot: pendingRoot, publishedRunsRoot: publishedRoot),
                stageAdditionalArtifacts: pendingRoot =>
                {
                    File.Copy(tempXlsx, Path.Combine(pendingRoot, runId, fileName), overwrite: false);
                    File.Copy(collected.RawReceiptPath, Path.Combine(pendingRoot, runId, Path.GetFileName(collected.RawReceiptPath)), overwrite: false);
                    File.Copy(tempMeasures, Path.Combine(pendingRoot, runId, CorridorBoqMeasuresFile.FileName), overwrite: false);
                });
            var finalPath = Path.Combine(SectionsWorkflowService.RunsRoot, runId, fileName);
            if (!File.Exists(finalPath) || !string.Equals(ArtifactHash.Sha256OfFile(finalPath), xlsxHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("קובץ הכמויות מקורידורים לא נמצא בתיקיית הריצה לאחר הפרסום.");
            return new CorridorBoqExportResult(finalPath, xlsxHash, runId, export.Corridors, collected.Inputs.Count,
                collected.Inputs.Count(i => !i.ReadSucceeded), export.Complete, export.Notes);
        }
        finally
        {
            try { Directory.Delete(pending, recursive: true); } catch { /* temp only */ }
        }
    }
}
