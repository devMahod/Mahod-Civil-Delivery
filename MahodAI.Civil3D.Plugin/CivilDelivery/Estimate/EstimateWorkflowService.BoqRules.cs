using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    public sealed record BoqRulesExportResult(
        string XlsxPath,
        string XlsxHash,
        string RunId,
        string ActiveRole,
        IReadOnlyList<string> UsedRoles,
        IReadOnlyList<string> MissingRoles,
        IReadOnlyList<string> Notes,
        int LineCount,
        int MappedLineCount,
        int MissingObjects,
        string Corridor);

    /// <summary>
    /// "כתב כמויות לפי כללים": the rules engine (ruleset 6422 v2, embedded) over the active drawing's fresh published
    /// scan plus the latest unchanged published scan of every other drawing role in the same folder, written as the NTI
    /// workbook into a new evidence run folder (with its manifest). A draft for engineering review: it approves nothing,
    /// writes no profile and changes no record. Missing roles and unmeasured objects stay visible in the workbook.
    /// Chapters 51.01–51.04 (stripping, earthworks, base courses, asphalt) come from the latest corridor measurement
    /// ("כמויות מקורידורים") of ONE drawing: the one the engineer chose when the profile has measurements of several drawings,
    /// or the only one. Taken only when its measures file, rules, raw receipt and drawing all verify. A choice that is missing
    /// (several drawings) or a chosen drawing whose latest measurement does not verify stops the export — no workbook — and
    /// is never answered with another drawing's or an older measurement (review 01/10). Only a profile with no corridor
    /// measurement at all gets a workbook that says chapters 51.01–51.04 were not measured.
    /// </summary>
    /// <param name="selectedCorridorDrawing">The drawing chosen in <see cref="CorridorSourceOptions"/>; null when there was no choice to make.</param>
    public BoqRulesExportResult ExportBoqRules(Document doc, ScanResult scan, string? selectedCorridorDrawing)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(scan);
        const string operation = "כתב כמויות לפי כללים";
        RequireFresh(doc, scan, operation);
        var before = RequirePublishedScanEvidence(scan);

        // The active drawing's scan is refused for the same reasons another role's scan would be (review 30/09).
        if ((BoqNeutralRecordAdapter.LegacyEvidenceRefusal(scan.Records) ?? BoqNeutralRecordAdapter.UnitRefusal(scan.Records) ??
             BoqNeutralRecordAdapter.ScanUnitRefusal(ScanUnitEvidence.OfScan(scan.PhysicalUnitsContract, scan.PhysicalUnits), scan.Records)) is { } refusal)
            throw new InvalidOperationException($"{operation}: {refusal}.");
        var rules = BoqRuleset.LoadEmbedded6422();
        // The same preparation the project-rule context of the proposals uses (L05): one resolution, one engine run.
        var corridor = ResolveCorridor(rules, scan, selectedCorridorDrawing);
        var plan = PrepareBoqPlan(scan, rules, corridor.Input == null ? corridor.Notes : null);
        var resolution = plan.Observed.Resolution;
        var input = plan.Input;
        var result = plan.Result;

        var runId = RunManifest.NewRunId("estimate", "boq-rules");
        var fileName = $"כתב-כמויות-לפי-כללים-{SafeName(rules.Project)}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcd-boq-rules-{Guid.NewGuid():N}.xlsx");
        try
        {
            var written = BoqRulesWorkbookWriter.Write(result, tempPath, new BoqRulesWorkbookWriter.Context(DateTime.Now), corridor.Input);
            // The workbook must describe the drawing and the published scan as they are now.
            RequireFresh(doc, scan, operation);
            var after = RequirePublishedScanEvidence(scan);
            if (!SameProof(before, after))
                throw new InvalidDataException("ראיות הסריקה שפורסמו השתנו במהלך הכתיבה.");
            // ...and the corridor measurement it priced (drawing, measures file, raw receipt) as it is now — also when none was
            // taken: a measurement that appeared, or a source list that changed, during the write is a different bill (Codex 01/10).
            var corridorAfter = ResolveCorridor(rules, scan, selectedCorridorDrawing);
            if (corridorAfter.Input?.RunId != corridor.Input?.RunId || corridorAfter.Input?.MeasuresSha256 != corridor.Input?.MeasuresSha256 ||
                !corridorAfter.Evidence.SequenceEqual(corridor.Evidence) || !corridorAfter.Notes.SequenceEqual(corridor.Notes))
                throw new InvalidDataException("מדידת הקורידורים או השרטוט שלה השתנו במהלך הכתיבה.");

            var inputs = ManifestInputs(scan).Append(new RunManifestInput(before.Path, before.Hash)).ToList();
            foreach (var source in resolution.Scans.Where(s => !string.Equals(s.RunId, scan.RunId, StringComparison.Ordinal)))
            {
                inputs.Add(new RunManifestInput(source.DrawingPath, string.IsNullOrWhiteSpace(source.DrawingHash) ? null : source.DrawingHash));
                inputs.Add(new RunManifestInput(Path.Combine(SectionsWorkflowService.RunsRoot, source.RunId, BoqRulesSourceResolver.RecordsArtifact)));
            }
            foreach (var (path, sha256) in corridor.Evidence) inputs.Add(new RunManifestInput(path, sha256));
            var missingObjects = result.Lines.Sum(line => line.Missing);
            SectionsWorkflowService.PersistEvidenceBundle(runId, "boq_rules_v2_export.json",
                new
                {
                    SourceScanRunId = scan.RunId,
                    SourceScanSha256 = before.Hash,
                    Scope = "boq-rules-v2-draft-not-approved-estimate",
                    Ruleset = rules.Project + " " + rules.Schema,
                    RulesetSha256 = rules.Sha256,
                    ActiveRole = resolution.ActiveRole,
                    Sources = resolution.Scans.Select(s => new { s.Role, s.RunId, s.DrawingPath, s.DrawingHash, Records = s.Records.Count }).ToList(),
                    resolution.MissingRoles,
                    Corridor = corridor.Input == null ? null : new
                    {
                        corridor.Input.RunId,
                        MeasuresSha256 = corridor.Input.MeasuresSha256,
                        RulesetSha256 = corridor.Input.RulesetSha256,
                        Drawing = corridor.Input.Context.DrawingPath,
                        DrawingSha256 = corridor.Input.Context.DrawingSha256,
                        RawReceiptSha256 = corridor.Input.Context.RawReceiptSha256,
                        Lines = written.BoqLineCount - result.Lines.Count,
                    },
                    CorridorNotes = corridor.Notes,
                    CorridorSourceSelected = selectedCorridorDrawing,
                    Notes = result.Warnings,
                    Status = DeliveryStatus.ReviewRequired,
                    XlsxFile = fileName,
                    XlsxHash = written.XlsxSha256,
                    Lines = result.Lines.Select(line => new { line.Line.Id, line.Item, line.Line.Unit, line.Quantity, line.Missing }).ToList(),
                    Buckets = result.BucketCounts(),
                },
                (pendingRoot, publishedRoot) => RuntimeRunManifestService.Write(runId,
                    "estimate", "boq-rules", scan.ProjectProfileId, scan.ProjectProfileHash,
                    DeliveryStatus.ReviewRequired,
                    new Dictionary<string, int>
                    {
                        ["boq_lines"] = written.BoqLineCount,
                        ["corridor_lines"] = written.BoqLineCount - result.Lines.Count,
                        ["mapped_lines"] = written.MappedLineCount,
                        ["source_roles"] = resolution.Scans.Count,
                        ["missing_roles"] = resolution.MissingRoles.Count,
                        ["missing_objects"] = missingObjects,
                        ["input_records"] = input.Records.Count,
                    },
                    ManifestFindings(scan), inputs,
                    runsRoot: pendingRoot, publishedRunsRoot: publishedRoot),
                stageAdditionalArtifacts: pendingRoot =>
                {
                    var staged = Path.Combine(pendingRoot, runId, fileName);
                    File.Copy(tempPath, staged, overwrite: false);
                    if (!string.Equals(ArtifactHash.Sha256OfFile(staged), written.XlsxSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The staged workbook differs from the written workbook.");
                });

            var finalPath = Path.Combine(SectionsWorkflowService.RunsRoot, runId, fileName);
            if (!File.Exists(finalPath) ||
                !string.Equals(ArtifactHash.Sha256OfFile(finalPath), written.XlsxSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("כתב הכמויות לא נמצא בתיקיית הריצה לאחר הפרסום.");
            return new BoqRulesExportResult(finalPath, written.XlsxSha256, runId, resolution.ActiveRole,
                resolution.Scans.Select(s => s.Role).ToList(), resolution.MissingRoles, result.Warnings,
                written.BoqLineCount, written.MappedLineCount, missingObjects,
                (corridor.Input != null ? "עפר, מצעים ואספלט:\n" : "עפר, מצעים ואספלט לא נכללו:\n") + BoqRulesExportText.CorridorLines(corridor.Notes));
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>The drawings the scan's profile has corridor measurements of (one row per drawing, latest first); more than
    /// one means the engineer chooses before <see cref="ExportBoqRules"/>.</summary>
    public IReadOnlyList<BoqRulesSourceResolver.CorridorSourceOption> CorridorSourceOptions(ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return BoqRulesSourceResolver.ListCorridorSources(SectionsWorkflowService.RunsRoot, scan.ProjectProfileId);
    }

    /// <summary>The latest verified corridor measurement of the chosen (or only) drawing of the scan's profile, for a ruleset
    /// of the same project only.</summary>
    private static BoqRulesSourceResolver.CorridorResolution ResolveCorridor(BoqRuleset rules, ScanResult scan, string? selectedDrawing)
    {
        var corridorRules = CorridorBoqRuleset.LoadEmbedded6422();
        if (!string.Equals(corridorRules.Project, rules.Project, StringComparison.Ordinal))
            return new BoqRulesSourceResolver.CorridorResolution(null,
                new[] { $"לפרויקט {rules.Project} אין כללי קורידורים בגרסה זו — פרקים 51.01–51.04 לא נמדדו." }, Array.Empty<(string, string)>());
        return BoqRulesSourceResolver.ResolveCorridor(SectionsWorkflowService.RunsRoot, scan.ProjectProfileId, corridorRules,
            CorridorBoqMeasuresFile.EmbeddedRulesetSha256(), selectedDrawing);
    }
}
