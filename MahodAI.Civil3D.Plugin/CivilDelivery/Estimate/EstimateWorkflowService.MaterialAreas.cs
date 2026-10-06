using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    /// <summary>Measured station areas only. No price/scope approval or native write.</summary>
    public MaterialSectionAreaExcelWriter.WriteResult ExportMaterialAreas(
        Document doc, ScanResult scan, string? outputDir = null)
    {
        RequireFresh(doc, scan, "ייצוא שטחי חתך");
        var proof = RequirePublishedScanEvidence(scan);
        var sources = scan.ExternalSources.Select(s =>
                new MaterialSectionAreaExcelWriter.SourceIdentity(s.DrawingPath, s.DrawingHash ?? ""))
            .ToArray();
        var context = new MaterialSectionAreaExcelWriter.WriteContext(
            scan.ProjectProfileId, scan.RunId, scan.ProjectProfileHash ?? "",
            scan.SourceDrawing, scan.SourceDrawingHash ?? "", proof.Hash, sources);
        var dir = outputDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MahodCivilDelivery", "material-areas");
        var written = MaterialSectionAreaExcelWriter.Write(scan.MaterialAreas, dir,
            $"שטחי-חתך-לפי-תחנה-{DateTime.Now:yyyyMMdd-HHmmss}", context);
        try
        {
            // Files can change during serialization; a report is not released on
            // the strength of only the earlier check. The scan manifest stays intact.
            RequireFresh(doc, scan, "השלמת ייצוא שטחי חתך");
            RequirePublishedScanEvidence(scan);
            var reportRunId = "material-areas-" + Guid.NewGuid().ToString("N");
            SectionsWorkflowService.PersistEvidenceBundle(
                reportRunId, "material_area_export.json",
                new { SourceScanRunId = scan.RunId, SourceScanSha256 = proof.Hash,
                    Scope = "measured-cross-section-area-only-not-boq-or-plan-area",
                    written.XlsxPath, written.XlsxHash, written.ObservationCount },
                (pendingRoot, publishedRoot) => RuntimeRunManifestService.Write(
                    reportRunId, "estimate", "material-areas",
                    scan.ProjectProfileId, scan.ProjectProfileHash, DeliveryStatus.ReviewRequired,
                    new Dictionary<string, int> { ["material_area_observations"] = written.ObservationCount },
                    ManifestFindings(scan),
                    ManifestInputs(scan).Append(new RunManifestInput(proof.Path, proof.Hash)),
                    new[] { new RunManifestArtifactInput(written.XlsxPath, written.XlsxHash) },
                    runsRoot: pendingRoot, publishedRunsRoot: publishedRoot));
            return written;
        }
        catch (Exception error)
        {
            // Withdraw only the new file returned by the no-overwrite writer.
            try
            {
                if (File.Exists(written.XlsxPath))
                {
                    if (!string.Equals(ArtifactHash.Sha256OfFile(written.XlsxPath), written.XlsxHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("הקובץ השתנה לאחר הייצוא; הוא נשמר ללא מחיקה ואינו דו״ח מאומת.");
                    File.Delete(written.XlsxPath);
                }
            }
            catch (Exception cleanup)
            {
                throw new InvalidOperationException(
                    "דו״ח שטחי החתך לא אומת ואין להשתמש בו. הסרת הקובץ נכשלה: " +
                    written.XlsxPath + " — " + cleanup.Message, error);
            }
            throw new InvalidOperationException(
                "דו״ח שטחי החתך לא פורסם: המקור השתנה או שכתיבת הראיות נכשלה; הקובץ החדש הוסר.", error);
        }
    }
}
