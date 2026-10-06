using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Explicit repair authority, never an edit to immutable PLAN.Action.</summary>
internal static class SectionSelectedRebuildPolicy
{
    private static readonly HashSet<string> RebuildableChecks = new(StringComparer.Ordinal)
    {
        "native_measured_label_layout_exact", "core_presentation_geometry_live_exact",
        "single_registered_datum_reference", "design_slope_labels_live_exact",
        "office_vehicle_blocks_live_exact", "vehicle_fallback_style_live",
        "traffic_direction_arrow_handles_live_exact", SectionVerificationRecoveryService.SurfaceGeometryCheck,
        "dimension_offset_labels_live_exact",
    };

    internal sealed record Authority(SectionPlan Plan, SectionVerifyResult FailedVerification,
        SectionVerificationRecoveryService.Authority Producer);

    // Presentation admission only. Execution additionally requires exact immutable
    // artifacts and native ownership again under the mutation transaction.
    internal static string? Rejection(SectionPlan? plan, string recordId,
        SectionVerifyResult? failed, bool planFresh)
    {
        if (!planFresh || plan == null) return "נדרש PLAN טרי לפני בנייה מחדש.";
        var rows = plan.Records.Where(r => r.RecordId == recordId).ToList();
        if (rows.Count != 1 || rows[0].Action != PlanAction.Unchanged || rows[0].ManualSectionReuse != null ||
            rows[0].Status != DeliveryStatus.Ready || !rows[0].PresentationCoverage.Complete ||
            rows[0].PresentationCoverage.UnresolvedSpans.Count != 0 || rows[0].TrafficDirections.Any(d => !d.IsResolved))
            return "בנייה מחדש דורשת חתך מנוהל יחיד, READY וללא שינוי, עם החלטות מלאות.";
        if (failed == null || failed.Status != DeliveryStatus.Failed || failed.Scope != "selected-record" ||
            failed.SelectedRecordId != recordId || failed.Records.Count != 1 ||
            failed.Records[0].RecordId != recordId || failed.Records[0].LogicalKey != rows[0].LogicalKey ||
            failed.Records[0].Status != DeliveryStatus.Failed ||
            failed.VerifiedDatabaseRevision != plan.SourceDatabaseRevision)
            return "נדרש אימות שנכשל עבור החתך הנבחר וגרסת השרטוט הנוכחית.";
        var failures = failed.Records[0].Checks.Where(c => !c.Pass).ToList();
        if (failures.Count == 0 || failures.Any(c => !RebuildableChecks.Contains(c.Check) && !LegacyGeometryMismatch(c, rows[0])) ||
            failed.Findings.Any(f => f.Severity == FindingSeverity.Error && f.Code != SectionFindingCodes.VerifyMismatch))
            return "כשל זה דורש טיפול במקור/בעלות/ראיות ואינו מתיר בנייה מחדש של התוצר.";
        return null;
    }

    // 1.2.52 used the same check name for source-proof exceptions and geometry.
    // Admit only its exact known valid-chain interval rejection, bound to a
    // planned required surface. Generic same-name errors never confer repair authority.
    private static bool LegacyGeometryMismatch(SectionVerifyCheck check, SectionPlanRecord record) =>
        check.Check == "live_surface_cut_matches_section" &&
        check.Actual == "Surface and Section do not cover the same cut interval." &&
        record.PlannedSources.Any(s => s.Required && s.PlannedState == "sampled" &&
            string.Equals(s.SourceType, "surface", StringComparison.OrdinalIgnoreCase) &&
            check.Expected == $"{s.SourceName}/{s.SourceHandle}: complete native source cut");

    internal static void RequirePublished(Authority authority)
    {
        var plan = authority.Plan;
        var failed = authority.FailedVerification;
        var reason = Rejection(plan, failed.SelectedRecordId ?? "", failed, true);
        if (reason != null) throw new InvalidOperationException(reason);
        var planProof = SectionsWorkflowService.RequirePlanEvidence(plan);
        var applyProof = SectionsWorkflowService.RequireApplyEvidence(authority.Producer.Applied);
        RuntimeRunManifestService.RequirePublishedArtifact(failed.RunId, "verify_result.json", failed,
            SectionsWorkflowService.Json, "sections", "verify-selected");
        var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(
            RuntimeRunManifestService.ArtifactPath(failed.RunId, "run_manifest.json")), RunManifestWriter.JsonOptions)
            ?? throw new InvalidDataException("Failed VERIFY manifest is unreadable.");
        foreach (var proof in new[] { planProof, applyProof })
        {
            var exact = manifest.ArtifactHashes.Where(p => string.Equals(Path.GetFullPath(p.Key),
                Path.GetFullPath(proof.Path), StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count != 1 || !string.Equals(exact[0].Value, proof.Hash, StringComparison.OrdinalIgnoreCase) ||
                !manifest.Artifacts.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(proof.Path),
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Failed VERIFY does not bind this exact current PLAN and producer APPLY.");
        }
    }

    internal static string? OwnershipRejection(Authority authority, SectionPlan plan, SectionPlanRecord target,
        OwnershipMetadata? viewOwner, OwnershipMetadata? lineOwner, string viewHandle, string lineHandle,
        string referencedLineHandle)
    {
        if (!ReferenceEquals(authority.Plan, plan) || target.RecordId != authority.FailedVerification.SelectedRecordId)
            return "Rebuild authority belongs to another PLAN or selected record.";
        var applied = authority.Producer.Applied;
        var producerRows = applied.Records.Where(r => r.RecordId == target.RecordId).ToList();
        if (producerRows.Count != 1 || !applied.Committed || applied.Status != DeliveryStatus.Applied ||
            producerRows[0].Status != DeliveryStatus.Applied)
            return "Rebuild requires one original committed APPLY record.";
        var handles = producerRows[0].Handles;
        if (!string.Equals(handles.SectionView, viewHandle, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(handles.SampleLine, lineHandle, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(referencedLineHandle, lineHandle, StringComparison.OrdinalIgnoreCase))
            return "The selected native view/sample-line relationship changed.";
        bool Owned(OwnershipMetadata? owner, string role) => owner != null && owner.Feature == "sections" &&
            owner.Role == role && owner.ProjectProfileId == plan.ProjectProfileId &&
            owner.LogicalKey == target.LogicalKey && owner.InputFingerprint == target.InputFingerprint &&
            owner.RunId == applied.RunId;
        return Owned(viewOwner, "section-view") && Owned(lineOwner, "sample-line")
            ? null : "The selected view/sample-line ownership is foreign, changed or ambiguous.";
    }

    internal static void RequireLiveOwnership(Database db, Transaction tr, Authority authority,
        SectionPlan plan, SectionPlanRecord target)
    {
        RequirePublished(authority);
        var applied = authority.Producer.Applied.Records.Single(r => r.RecordId == target.RecordId);
        ObjectId Resolve(string? handle) => long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out var value) && db.TryGetObjectId(new Handle(value), out var id) && !id.IsNull && !id.IsErased
            ? id : throw new InvalidDataException("Selected rebuild handle is missing.");
        var view = tr.GetObject(Resolve(applied.Handles.SectionView), OpenMode.ForRead) as CivilDb.SectionView
            ?? throw new InvalidDataException("Selected rebuild view is unreadable.");
        var line = tr.GetObject(Resolve(applied.Handles.SampleLine), OpenMode.ForRead) as CivilDb.SampleLine
            ?? throw new InvalidDataException("Selected rebuild sample line is unreadable.");
        var reason = OwnershipRejection(authority, plan, target,
            SectionOwnershipService.Read(tr, view), SectionOwnershipService.Read(tr, line),
            view.Handle.ToString(), line.Handle.ToString(), view.SampleLineId.Handle.ToString());
        if (reason != null) throw new InvalidOperationException(reason);
    }
}
