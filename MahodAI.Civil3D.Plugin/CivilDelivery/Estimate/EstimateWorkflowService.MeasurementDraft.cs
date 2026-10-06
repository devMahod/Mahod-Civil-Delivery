using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    internal sealed record MeasurementDraftEvidence(PublishedArtifactProof Scan,
        PublishedArtifactProof? Proposals, IReadOnlyList<MappingProposal> MappingProposals,
        string? ReferenceSource, string? ReferenceHash);

    /// <summary>All captured neutral measurements and findings, never an approved estimate.</summary>
    public MeasurementDraftExcelWriter.WriteResult ExportMeasurementDraft(
        Document doc, ScanResult scan, string? outputDir = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(scan);
        return ExecuteMeasurementDraftExport(
            () => RequireFresh(doc, scan, "ייצוא מדידות לבדיקה"),
            () => CaptureMeasurementDraftEvidence(scan),
            evidence => MeasurementDraftExcelWriter.Write(scan.Records, scan.Findings,
                outputDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "MahodCivilDelivery", "measurement-drafts"),
                $"מדידות-לבדיקה-{DateTime.Now:yyyyMMdd-HHmmss}",
                new MeasurementDraftExcelWriter.WriteContext(scan.ProjectProfileId, scan.RunId,
                    scan.ProjectProfileHash ?? "", scan.SourceDrawing, scan.SourceDrawingHash ?? "",
                    evidence.Scan.Path, evidence.Scan.Hash, evidence.MappingProposals,
                    evidence.Proposals?.Path, evidence.Proposals?.Hash,
                    evidence.ReferenceSource, evidence.ReferenceHash)),
            (written, evidence) => PublishMeasurementDraft(scan, written, evidence));
    }

    // A host-free orchestration seam tests lifecycle ordering and exact-owned-file
    // withdrawal. The public entry point always supplies native freshness checks.
    internal static MeasurementDraftExcelWriter.WriteResult ExecuteMeasurementDraftExport(
        Action requireFresh, Func<MeasurementDraftEvidence> requirePublished,
        Func<MeasurementDraftEvidence, MeasurementDraftExcelWriter.WriteResult> write,
        Action<MeasurementDraftExcelWriter.WriteResult, MeasurementDraftEvidence> publish)
    {
        requireFresh();
        var before = requirePublished();
        var written = write(before);
        try
        {
            requireFresh();
            var after = requirePublished();
            if (!SameProof(before.Scan, after.Scan) || !SameProof(before.Proposals, after.Proposals))
                throw new InvalidDataException("Published measurement or proposal evidence changed during export.");
            publish(written, before);
            return written;
        }
        catch (Exception error)
        {
            try
            {
                if (File.Exists(written.XlsxPath))
                {
                    if (!string.Equals(ArtifactHash.Sha256OfFile(written.XlsxPath), written.XlsxHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("הקובץ השתנה לאחר הייצוא; הוא נשמר ללא מחיקה ואינו טיוטה שפורסמה.");
                    File.Delete(written.XlsxPath);
                }
            }
            catch (Exception cleanup)
            {
                throw new InvalidOperationException("טיוטת המדידות לא פורסמה ואין להשתמש בה. " +
                    "הקובץ נשמר ללא מחיקה: " + written.XlsxPath + " — " + cleanup.Message, error);
            }
            throw new InvalidOperationException(
                "טיוטת המדידות לא פורסמה: המקור או הראיות השתנו, או שכתיבת הראיות נכשלה; הקובץ החדש הוסר.", error);
        }
    }

    private static bool SameProof(PublishedArtifactProof? before, PublishedArtifactProof? after) =>
        before == null || after == null ? before == after :
        string.Equals(Path.GetFullPath(before.Path), Path.GetFullPath(after.Path), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(before.Hash, after.Hash, StringComparison.OrdinalIgnoreCase) &&
        before.ProducerRunId == after.ProducerRunId && before.Feature == after.Feature;

    internal static MeasurementDraftEvidence CaptureMeasurementDraftEvidence(ScanResult scan)
    {
        var proof = RequirePublishedScanEvidence(scan);
        var proposalPath = RuntimeRunManifestService.ArtifactPath(scan.RunId, "mapping_proposals.json");
        var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(
            RuntimeRunManifestService.ArtifactPath(scan.RunId, "run_manifest.json")), RunManifestWriter.JsonOptions)
            ?? throw new InvalidDataException("Scan manifest is unreadable.");
        var declared = manifest.Artifacts.Any(path => string.Equals(Path.GetFullPath(path),
            Path.GetFullPath(proposalPath), StringComparison.OrdinalIgnoreCase));
        if (!declared && !File.Exists(proposalPath))
            return new(proof, null, Array.Empty<MappingProposal>(), null, null);
        using var document = JsonDocument.Parse(File.ReadAllText(proposalPath));
        var root = document.RootElement;
        var proposalProof = RuntimeRunManifestService.RequirePublishedArtifact(scan.RunId,
            "mapping_proposals.json", root, SectionsWorkflowService.Json, "estimate", "propose", "build", "export", "export-partial");
        var proposals = root.GetProperty("proposals").Deserialize<List<MappingProposal>>(SectionsWorkflowService.Json)
            ?? throw new InvalidDataException("Published mapping proposals are unreadable.");
        string? OptionalText(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        return new(proof, proposalProof, proposals, OptionalText("reference_source"), OptionalText("reference_hash"));
    }

    private static void PublishMeasurementDraft(ScanResult scan, MeasurementDraftExcelWriter.WriteResult written,
        MeasurementDraftEvidence evidence)
    {
        var reportRunId = "measurement-draft-" + Guid.NewGuid().ToString("N");
        var inputs = ManifestInputs(scan).Append(new RunManifestInput(evidence.Scan.Path, evidence.Scan.Hash));
        if (evidence.Proposals != null)
            inputs = inputs.Append(new RunManifestInput(evidence.Proposals.Path, evidence.Proposals.Hash));
        SectionsWorkflowService.PersistEvidenceBundle(reportRunId, "measurement_draft_export.json",
            new { SourceScanRunId = scan.RunId, SourceScanSha256 = evidence.Scan.Hash,
                Scope = "all-captured-neutral-measurements-draft-unpriced-not-approved-estimate",
                Status = DeliveryStatus.ReviewRequired, written.XlsxPath, written.XlsxHash,
                written.RecordCount, written.RecordFindingCount, written.ScanFindingCount,
                MappingProposalsPath = evidence.Proposals?.Path, MappingProposalsSha256 = evidence.Proposals?.Hash },
            (pendingRoot, publishedRoot) => RuntimeRunManifestService.Write(reportRunId,
                "estimate", "measurement-draft", scan.ProjectProfileId, scan.ProjectProfileHash,
                DeliveryStatus.ReviewRequired,
                new Dictionary<string, int> { ["neutral_records"] = written.RecordCount,
                    ["record_findings"] = written.RecordFindingCount, ["scan_findings"] = written.ScanFindingCount,
                    ["mapping_proposals"] = evidence.MappingProposals.Count },
                ManifestFindings(scan), inputs,
                new[] { new RunManifestArtifactInput(written.XlsxPath, written.XlsxHash) },
                runsRoot: pendingRoot, publishedRunsRoot: publishedRoot));
    }
}
