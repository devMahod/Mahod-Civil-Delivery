using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    /// <summary>The empty price list a family review runs against when none is loaded: families only, no items.</summary>
    public const string NoCatalogSnapshotId = "NO-ACTIVE-CATALOG";

    /// <summary>
    /// The engineer draft of a fresh scan built in memory for the family review: which design groups no rule or
    /// decision covers, what the drawing evidence proposes for them, and which saved decisions went stale.
    /// Writes nothing and approves nothing. Without a loaded price list it still recognises families (see
    /// <see cref="RecognitionCatalog"/>); items and prices then stay out of the review.
    /// </summary>
    public EngineerBoqDraft BuildRecognitionReview(Document doc, ScanResult scan, ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(profile);
        const string operation = "זיהוי שכבות ואישור משפחות";
        RequireFresh(doc, scan, operation);
        RequireTrustedUnitEvidence(scan, operation);
        var catalog = LoadCatalog(profile, scan.ProfileSource);
        var snapshot = RecognitionCatalog(catalog.Snapshot, catalog.Findings.Select(f => f.Title).ToList(), scan.Records);
        var draft = EngineerBoqDraftBuilder.Build(scan.Records, scan.Findings, snapshot,
            catalog.Snapshot == null ? new Dictionary<string, string>() : PriceBookChapterTitles.Read(catalog.CatalogPath, catalog.CatalogSheetName),
            EngineerBoqLibrary.For(profile), DraftContext(profile, scan, snapshot));
        RequireFresh(doc, scan, operation);
        return draft;
    }

    /// <summary>
    /// The price list a family review uses. A family is the meaning of CAD groups, not a price-list item, so a missing
    /// price list does not stop recognition: the review runs against an empty, explicitly named list, which proposes no
    /// item and no price. An item mapping approved against a price list cannot be verified without it, so a scan that
    /// carries one keeps the refusal rather than showing those groups as unassigned.
    /// </summary>
    internal static CatalogSnapshot RecognitionCatalog(CatalogSnapshot? loaded, IReadOnlyList<string> findings,
        IEnumerable<NeutralQuantityRecord> records)
    {
        if (loaded != null) return loaded;
        var mapped = records.Count(r => !string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode) ||
                                        !string.IsNullOrWhiteSpace(r.Classification.ApprovedCatalogId) ||
                                        !string.IsNullOrWhiteSpace(r.Classification.MappingApprovedBy));
        if (mapped > 0)
            throw new InvalidOperationException(
                $"המחירון הפעיל לא נטען, ובסריקה {mapped} רשומות עם שיוך סעיף שאי אפשר לאמת בלעדיו. " +
                "יש לטעון את המחירון ולפתוח שוב: " + string.Join("; ", findings));
        return new CatalogSnapshot
        {
            SnapshotId = NoCatalogSnapshotId,
            FileHash = new string('0', 64),
            PublicationNote = "אין מחירון פעיל: זיהוי משפחות בלבד, בלי סעיפים ומחירים",
        };
    }
}
