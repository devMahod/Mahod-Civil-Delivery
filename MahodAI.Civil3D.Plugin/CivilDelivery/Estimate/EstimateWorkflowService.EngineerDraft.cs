using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    /// <summary>
    /// The fast path an engineer actually needs after a scan: an editable, priced BoQ
    /// draft built from the design models with the Mahod roads library, plus every
    /// mapping an engineer already approved in the profile. It approves nothing, writes
    /// no profile, changes no record and never replaces the approved estimate; the
    /// workbook itself says so on its first sheet.
    /// </summary>
    public EngineerBoqDraftExcelWriter.WriteResult ExportEngineerDraft(
        Document doc, ScanResult scan, ProjectProfile profile, string? outputDir = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(profile);
        const string operation = "טיוטת כתב כמויות להנדסה";
        var directory = outputDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MahodCivilDelivery", "engineer-drafts");
        var stem = $"טיוטת-כתב-כמויות-{SafeName(profile.ProfileId)}-{DateTime.Now:yyyyMMdd-HHmmss}";
        EngineerBoqDraft? draft = null;
        CatalogLoadResult? catalog = null;
        return ExecuteEngineerDraftExport(
            () => RequireFresh(doc, scan, operation),
            () =>
            {
                RequireTrustedUnitEvidence(scan, operation);
                return RequirePublishedScanEvidence(scan);
            },
            tempPath =>
            {
                catalog = LoadCatalog(profile, scan.ProfileSource);
                if (catalog.Snapshot == null)
                    throw new InvalidOperationException("המחירון הפעיל לא נטען, ולכן אין מחירים לטיוטה: " +
                        string.Join("; ", catalog.Findings.Select(f => f.Title)));
                var snapshot = catalog.Snapshot;
                var context = DraftContext(profile, scan, snapshot);
                draft = EngineerBoqDraftBuilder.Build(scan.Records, scan.Findings, snapshot,
                    PriceBookChapterTitles.Read(catalog.CatalogPath, catalog.CatalogSheetName), EngineerBoqLibrary.For(profile), context);
                return EngineerBoqDraftExcelWriter.Write(draft, tempPath, simpleView: true);
            },
            directory, stem,
            (written, hash, proof) => PublishEngineerDraft(scan, written, hash, draft!, proof, catalog!.CatalogPath, catalog.Snapshot!));
    }

    /// <summary>
    /// Host-free export lifecycle (tested): freshness and published-scan proof before and after the write,
    /// the workbook written to a temporary name, hashed, then moved to a unique final name, and on any
    /// later failure withdrawn only when its bytes are still exactly what was written.
    /// </summary>
    internal static EngineerBoqDraftExcelWriter.WriteResult ExecuteEngineerDraftExport(
        Action requireFresh, Func<PublishedArtifactProof> requirePublished,
        Func<string, EngineerBoqDraftExcelWriter.WriteResult> write, string directory, string stem,
        Action<EngineerBoqDraftExcelWriter.WriteResult, string, PublishedArtifactProof> publish)
    {
        requireFresh();
        var before = requirePublished();
        Directory.CreateDirectory(directory);
        // Write beside the target and rename: a half-written workbook never carries the final name.
        var tempPath = Path.Combine(directory, $"~engineer-draft-{Guid.NewGuid():N}.tmp");
        EngineerBoqDraftExcelWriter.WriteResult written;
        string xlsxHash, finalPath;
        try
        {
            written = write(tempPath);
            xlsxHash = ArtifactHash.Sha256OfFile(tempPath);
            finalPath = MoveToUniqueName(tempPath, directory, stem);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        written = written with { XlsxPath = finalPath };
        var publishing = false;
        try
        {
            // The draft must describe the drawing and the published scan as they are now,
            // not a state that changed while the workbook was being written.
            requireFresh();
            var after = requirePublished();
            if (!SameProof(before, after))
                throw new InvalidDataException("ראיות הסריקה שפורסמו השתנו במהלך הכתיבה.");
            publishing = true;
            publish(written, xlsxHash, before);
            return written;
        }
        catch (Exception error)
        {
            var removed = false;
            try
            {
                if (File.Exists(finalPath))
                {
                    if (!string.Equals(ArtifactHash.Sha256OfFile(finalPath), xlsxHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("הקובץ השתנה לאחר הכתיבה; הוא נשמר ללא מחיקה ואינו טיוטה שפורסמה.");
                    File.Delete(finalPath);
                    removed = true;
                }
            }
            catch (Exception cleanup)
            {
                throw new InvalidOperationException($"טיוטת כתב הכמויות לא פורסמה ({error.Message}) ואין להשתמש בקובץ. " +
                    "הקובץ קיים ונשמר ללא מחיקה: " + finalPath + " — " + cleanup.Message + " " +
                    (publishing ? "לנסות לייצא שוב; אם זה חוזר — לשלוח חבילת תמיכה." : "יש לסרוק מחדש ולייצא שוב."), error);
            }
            var file = removed ? "הקובץ החדש הוסר." : "הקובץ החדש לא נמצא להסרה.";
            throw new InvalidOperationException(publishing
                ? $"טיוטת כתב הכמויות לא פורסמה: רישום הראיות נכשל ({error.Message}). {file} אין צורך לסרוק מחדש — לנסות שוב, ואם זה חוזר לשלוח חבילת תמיכה."
                : $"טיוטת כתב הכמויות לא פורסמה: השרטוט או ראיות הסריקה השתנו בזמן הכתיבה ({error.Message}). {file} יש לסרוק מחדש ולייצא שוב.", error);
        }
    }

    private static string MoveToUniqueName(string tempPath, string directory, string stem)
    {
        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, n == 1 ? stem + ".xlsx" : $"{stem}-{n}.xlsx");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try
            {
                File.Move(tempPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // Taken between the check and the move: try the next name.
            }
        }
        throw new IOException("לא נמצא שם פנוי לקובץ הטיוטה בתיקייה " + directory);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The draft context of one scan: profile identity, the active price list, the engineer's recorded decisions
    /// (approved mappings, "not a construction quantity", family decisions) and the local recognition classifier.
    /// </summary>
    internal static EngineerDraftContext DraftContext(ProjectProfile profile, ScanResult scan, CatalogSnapshot snapshot) =>
        new(
            profile.ProjectName ?? profile.ProfileId,
            profile.ProfileId,
            scan.RunId,
            scan.SourceDrawing,
            string.IsNullOrWhiteSpace(profile.Estimate.Catalog.CatalogVersion)
                ? snapshot.SnapshotId
                : profile.Estimate.Catalog.CatalogVersion!,
            profile.Sections.Cl.LayerPatterns.ToArray(),
            SectionPlanService.ToolVersion,
            RecordedEstimateDecisions(profile),
            $"{snapshot.SnapshotId} · SHA-256 {ShortHash(snapshot.FileHash)}",
            AuthoritativeExclusions(profile),
            profile.Estimate.FamilyDecisions,
            RecognitionClassifier);

    /// <summary>The recognition classifier the draft and the family review use (local, deterministic, no network).</summary>
    internal static IFamilyClassifier RecognitionClassifier => LocalFamilyClassifier.Instance;

    /// <summary>Engineer decisions saved in the profile: approved mappings and "not a construction quantity" decisions.</summary>
    internal static int RecordedEstimateDecisions(ProjectProfile profile)
    {
        var estimate = profile.Estimate;
        return estimate.QuantitySources.Rules.Count(rule => !string.IsNullOrWhiteSpace(rule.ApprovedBy)) +
               estimate.IgnoredRuleDecisions.Count(decision => !string.IsNullOrWhiteSpace(decision.ApprovedBy)) +
               estimate.IgnoredRuleKeys.Count(key => !string.IsNullOrWhiteSpace(key)) +
               (estimate.FamilyDecisions?.Count(decision => !string.IsNullOrWhiteSpace(decision.ApprovedBy)) ?? 0);
    }

    /// <summary>
    /// "Not a construction quantity" decisions with rule key, approver and UTC time. Legacy bare keys
    /// stay visible for migration but are not exclusion authority (see the profile contract).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> AuthoritativeExclusions(ProjectProfile profile) =>
        // Exactly the authority the approved estimate uses: rule key, reason, approver and time;
        // duplicates resolved to the latest decision; ordinal keys. Bare legacy keys are not authority.
        IgnoredRulePolicy.ApprovedDecisions(profile)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ApprovedBy!.Trim(), StringComparer.Ordinal);

    private static string ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? "?" : hash.Trim()[..Math.Min(12, hash.Trim().Length)];

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? "project").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "project" : cleaned;
    }

    private static void PublishEngineerDraft(ScanResult scan, EngineerBoqDraftExcelWriter.WriteResult written, string xlsxHash,
        EngineerBoqDraft draft, PublishedArtifactProof scanProof, string? catalogPath, CatalogSnapshot snapshot)
    {
        var reportRunId = "engineer-draft-" + Guid.NewGuid().ToString("N");
        var inputs = ManifestInputs(scan).Append(new RunManifestInput(scanProof.Path, scanProof.Hash));
        if (!string.IsNullOrWhiteSpace(catalogPath))
            inputs = inputs.Append(new RunManifestInput(catalogPath, snapshot.FileHash));
        SectionsWorkflowService.PersistEvidenceBundle(reportRunId, "engineer_draft_export.json",
            new
            {
                SourceScanRunId = scan.RunId,
                SourceScanSha256 = scanProof.Hash,
                Scope = "engineer-draft-unapproved-proposals-design-sources-and-profile-approved-mappings",
                Library = draft.Library.Id,
                LibraryHash = LibraryIdentity.LibraryHash(draft.Library),
                draft.ClassifierIdentity,
                RecognitionProposed = draft.RecognitionProposals.Count(p => p.Status == RecognitionStatus.Proposed),
                RecognitionAbstained = draft.RecognitionProposals.Count(p => p.Status == RecognitionStatus.Abstained),
                FamilyDecisionsApplied = draft.FamilyResolutions.Count(r => r.State == FamilyDecisionState.Applied),
                FamilyDecisionsStale = draft.FamilyResolutions.Count(r => r.State == FamilyDecisionState.Stale),
                CatalogSnapshotId = snapshot.SnapshotId,
                CatalogSha256 = snapshot.FileHash,
                CatalogPath = catalogPath,
                Status = DeliveryStatus.ReviewRequired,
                written.XlsxPath,
                XlsxHash = xlsxHash,
                written.LineCount,
                written.ElementCount,
                written.BoqRowCount,
                written.PricedLineCount,
                written.PricedTotalAtDefaults,
                written.AccountedRecords,
                draft.RecordCount,
                draft.UnmeasuredObjectsTotal,
                draft.UnmeasuredDesignObjects,
                RecordedEstimateDecisions = draft.Context.RecordedEstimateDecisions,
                ProfileApprovedElements = draft.Elements.Count(e => e.Rule.Id.StartsWith("profile-approved:", StringComparison.Ordinal)),
            },
            (pendingRoot, publishedRoot) => RuntimeRunManifestService.Write(reportRunId,
                "estimate", "engineer-draft", scan.ProjectProfileId, scan.ProjectProfileHash,
                DeliveryStatus.ReviewRequired,
                new Dictionary<string, int>
                {
                    ["neutral_records"] = draft.RecordCount,
                    ["draft_elements"] = written.ElementCount,
                    ["draft_lines"] = written.LineCount,
                    ["boq_rows"] = written.BoqRowCount,
                    ["priced_rows"] = written.PricedLineCount,
                    ["unmapped_design_groups"] = draft.UnmappedDesign.Count,
                    ["unmeasured_design_objects"] = draft.UnmeasuredDesignObjects,
                },
                ManifestFindings(scan), inputs,
                new[] { new RunManifestArtifactInput(written.XlsxPath, xlsxHash) },
                runsRoot: pendingRoot, publishedRunsRoot: publishedRoot));
    }
}
