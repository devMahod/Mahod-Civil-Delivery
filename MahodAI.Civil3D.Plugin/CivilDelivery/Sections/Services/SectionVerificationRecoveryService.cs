using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>
/// Explicit-action recovery only. The live owned view names its exact producing
/// run. No directory search, newest-file selection, invented APPLY, or old-green
/// reuse. The original artifacts remain immutable and are linked by new VERIFY.
/// </summary>
internal static class SectionVerificationRecoveryService
{
    internal sealed record Authority(SectionPlan ProducerPlan, SectionApplyResult Applied)
    {
        internal IReadOnlyList<SectionExternalSourceRecoveryPolicy.Relocation> SourceRelocations { get; init; } =
            Array.Empty<SectionExternalSourceRecoveryPolicy.Relocation>();
    }

    internal static Authority RecoverSelected(Document doc, SectionPlan current, string recordId)
    {
        SectionsWorkflowService.RequirePlanEvidence(current);
        var record = current.Records.Single(r => r.RecordId == recordId);
        var lookup = ManagedSectionViewLookupService.Resolve(doc, current, record);
        if (!lookup.Found) throw new InvalidOperationException(lookup.Reason);
        OwnershipMetadata owner;
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var id = Resolve(doc.Database, lookup.SectionViewHandle!);
            var view = tr.GetObject(id, OpenMode.ForRead) as CivilDb.SectionView
                ?? throw new InvalidDataException("The owned section view is not readable.");
            owner = SectionOwnershipService.Read(tr, view)
                ?? throw new InvalidDataException("The section view has no authoritative ownership.");
            tr.Abort();
        }
        SafeRunId(owner.RunId);
        var applied = Read<SectionApplyResult>(RuntimeRunManifestService.ArtifactPath(owner.RunId, "apply_result.json"));
        SectionsWorkflowService.RequireApplyEvidence(applied);
        var manifest = ReadManifest(owner.RunId);
        var planPaths = manifest.Artifacts.Where(path => string.Equals(
            Path.GetFileName(path), "section_plan.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (planPaths.Count != 1) throw new InvalidDataException("APPLY does not bind exactly one producer PLAN.");
        var producerPath = Path.GetFullPath(planPaths[0]);
        var runsRoot = Path.GetFullPath(SectionsWorkflowService.RunsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!producerPath.StartsWith(runsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The producer PLAN is outside the local run-evidence root.");
        var producer = Read<SectionPlan>(producerPath);
        SafeRunId(producer.RunId);
        var proof = SectionsWorkflowService.RequirePlanEvidence(producer);
        var expectedHash = manifest.ArtifactHashes.FirstOrDefault(pair => string.Equals(
            Path.GetFullPath(pair.Key), producerPath, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.Equals(proof.Path, producerPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(proof.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("APPLY's exact producer PLAN digest is missing or changed.");
        var original = producer.Records.Single(r => r.RecordId == recordId);
        var matches = applied.Records.Where(r => r.RecordId == recordId).ToList();
        var unique = matches.Count == 1 && owner.RunId == applied.RunId &&
            owner.ProjectProfileId == current.ProjectProfileId && owner.LogicalKey == record.LogicalKey &&
            owner.InputFingerprint == record.InputFingerprint &&
            string.Equals(matches[0].Handles.SectionView, lookup.SectionViewHandle, StringComparison.OrdinalIgnoreCase);
        var reason = SectionVerificationRecoveryPolicy.Rejection(
            Identity(producer, original), Identity(current, record), true, applied.Committed,
            applied.Status == DeliveryStatus.Applied && matches.Count == 1 &&
            matches[0].Status == DeliveryStatus.Applied &&
            !applied.Findings.Concat(applied.Records.SelectMany(r => r.Findings))
                .Any(f => f.Severity == FindingSeverity.Error), unique);
        if (reason != null) throw new InvalidOperationException(reason);
        var sources = CompareExternalSources(producer, current);
        if (!sources.Equivalent) throw new InvalidOperationException(sources.Reason);
        if (applied.Scope != "batch" && (applied.Scope != "selected-record" ||
            applied.SelectedRecordId != recordId || applied.Records.Count != 1))
            throw new InvalidDataException("The producer APPLY scope does not authorize this record.");
        return new(producer, applied) { SourceRelocations = sources.Relocations };
    }

    internal static SectionExternalSourceRecoveryPolicy.Result CompareExternalSources(SectionPlan producer, SectionPlan current) =>
        SectionExternalSourceRecoveryPolicy.Compare(SourceProofs(producer), SourceProofs(current));

    private static IReadOnlyList<SectionExternalSourceRecoveryPolicy.SourceProof> SourceProofs(SectionPlan plan) =>
        plan.ExternalSources.Select(source => new SectionExternalSourceRecoveryPolicy.SourceProof(
            source.SourcePath, source.SourceName, source.Sha256, source.Roles, source.SourceChain,
            source.RequiresLiveDatabase, source.LiveDatabaseRevision)).ToList();

    internal static SectionVerificationRecoveryPolicy.Identity Identity(SectionPlan plan, SectionPlanRecord record) =>
        new(plan.SourceDrawing ?? "", plan.ProjectProfileId, plan.ProjectProfileHash ?? "",
            record.RecordId, record.LogicalKey ?? "", Contract(plan, record));

    // Selected geometry/presentation remains byte-exact. External source proofs
    // are separately mandatory and permit only explicit one-to-one relocation.
    private static string Contract(SectionPlan plan, SectionPlanRecord record)
    {
        var legacy = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
        {
            record.InputFingerprint,
            Cl = new { record.Cl.RecordId, record.Cl.SourceDrawing, record.Cl.SourceDrawingHash,
                record.Cl.SourceDrawingPath, record.Cl.SourceHandle, record.Cl.SourceXref,
                record.Cl.SourceEntityType, record.Cl.SourceLayer, record.Cl.SourceEndpoints,
                record.Cl.WcsEndpoints, record.Cl.XrefTransform, record.Cl.Length, record.Cl.AngleDeg,
                record.Cl.CandidateSectionNumber,
                Labels = record.Cl.NearbyLabels.OrderBy(label => label, StringComparer.Ordinal) },
            record.SelectedAlignment, record.SelectedCrossing,
            record.Station, record.SkewDeg, record.LeftExtent, record.RightExtent,
            Sources = record.PlannedSources.Select(s => new { s.SourceName, s.SourceType, s.SourceHandle,
                s.PlannedState, s.Required, s.AdapterRequired, s.NativeSampleCapability, s.StyleMapping }),
            record.ProjectedEntities, record.ProjectedSystems, record.TrafficDirections,
            record.PresentationCoverage, record.PlannedStyles,
            plan.SourceUnitCode,
        }, SectionsWorkflowService.Json));
        // Preserve explicit-metre producer compatibility. Unitless approval has
        // no legacy producer and must bind its exact physical-unit authority.
        return plan.SourceUnitCode == PhysicalDrawingUnitPolicy.Unitless ||
               !string.IsNullOrEmpty(plan.PhysicalUnitDeclarationDigest) // b24: a reviewed explicit unit binds too
            ? ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
            {
                legacy, plan.PhysicalUnitCode, plan.PhysicalUnitDeclarationDigest,
                plan.SourceDrawingFingerprint,
            }, SectionsWorkflowService.Json)) : legacy;
    }

    /// <summary>
    /// Compare complete native TIN/Grid samples along the actual CL against each
    /// live Section chain. Unlike matching source names, this detects stale sampled
    /// sections after source geometry edits. Unsupported required sampled kinds
    /// remain explicitly blocked on recovery instead of being waived.
    /// Autodesk API: TinSurface/GridSurface.SampleElevations(Point3d, Point3d).
    /// </summary>
    internal const string SurfaceGeometryCheck = "live_surface_cut_geometry_matches_section";
    internal const string SurfaceSourceProofCheck = "live_surface_source_proof";

    internal static string SurfaceComparisonCheck(SectionVerificationRecoveryPolicy.SurfaceMismatchKind kind) =>
        kind is SectionVerificationRecoveryPolicy.SurfaceMismatchKind.None or
            SectionVerificationRecoveryPolicy.SurfaceMismatchKind.CutIntervalMismatch or
            SectionVerificationRecoveryPolicy.SurfaceMismatchKind.ElevationMismatch
            ? SurfaceGeometryCheck : "live_surface_cut_proof";

    internal static void CheckLiveSources(Database db, Transaction tr, SectionPlanRecord record,
        SectionApplyRecordResult applied, SectionVerifyRecordResult result)
    {
        var unsupported = record.PlannedSources.Where(s => s.Required && s.PlannedState == "sampled" &&
            !string.Equals(s.SourceType, "surface", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var source in unsupported)
            result.Checks.Add(new SectionVerifyCheck { Check = "live_source_geometry_supported",
                Expected = "independent current source-to-output geometry proof",
                Actual = $"{source.SourceType}:{source.SourceName}:{source.SourceHandle} has no recovery geometry adapter", Pass = false });
        try
        {
            var sampleLine = tr.GetObject(Resolve(db, applied.Handles.SampleLine!), OpenMode.ForRead) as CivilDb.SampleLine
                ?? throw new InvalidDataException("SampleLine is unreadable.");
            var view = tr.GetObject(Resolve(db, applied.Handles.SectionView!), OpenMode.ForRead) as CivilDb.SectionView
                ?? throw new InvalidDataException("SectionView is unreadable.");
            var chains = SectionAnnotationPlacementContract.ReadSurfaceChains(tr, sampleLine, record,
                view.OffsetLeft, view.OffsetRight, view.ElevationMin, view.ElevationMax);
            var frame = SectionCutGeometry.RequireFrame(record);
            var pair = record.PlannedSources.Where(s => s.Required && s.PlannedState == "sampled" &&
                string.Equals(s.SourceType, "surface", StringComparison.OrdinalIgnoreCase)).ToList();
            for (var i = 0; i < pair.Count; i++)
            {
                var source = pair[i];
                var surface = tr.GetObject(Resolve(db, source.SourceHandle!), OpenMode.ForRead) as CivilDb.Surface
                    ?? throw new InvalidDataException($"Surface {source.SourceName} is unreadable.");
                if (!string.Equals(surface.Name, source.SourceName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The surface handle/name identity changed.");
                if (surface.IsOutOfDate)
                    throw new InvalidDataException($"Surface {source.SourceName} is out of date; rebuild it explicitly before verification.");
                // A data-referenced surface keeps its TIN inside this drawing, so the
                // native cut sample below reads exactly what the section was sampled
                // from. Its external dependency is bound through the data-shortcut key:
                // the source drawing must exist, the reference must not be stale, and
                // the source bytes are hashed into the check (live 07/09 18:20:
                // 600-DESIGN-FINAL was refused by design; MK proved piecewise-equal).
                var sourceProof = "native surface";
                if (surface.IsReferenceObject)
                {
                    using var key = surface.GetReferenceInfo()
                        ?? throw new InvalidDataException($"Surface {source.SourceName} is a Civil data reference without a readable data-shortcut key.");
                    var sourceDrawing = key.SourceDrawing;
                    if (string.IsNullOrWhiteSpace(sourceDrawing))
                        throw new InvalidDataException($"Surface {source.SourceName} is a Civil data reference whose source drawing is unknown.");
                    if (!key.IsSourceDrawingExistent || !File.Exists(sourceDrawing))
                        throw new InvalidDataException($"Surface {source.SourceName} references '{sourceDrawing}', which does not exist; the reference cannot be proven.");
                    if (surface.IsReferenceStale)
                        throw new InvalidDataException($"Surface {source.SourceName} is a stale data reference of '{sourceDrawing}'; synchronize it explicitly before verification.");
                    sourceProof = $"data-reference source={sourceDrawing} sha256={ArtifactHash.Sha256OfFile(sourceDrawing)}";
                }
                var a = frame.PointAt(frame.MinOffset); var b = frame.PointAt(frame.MaxOffset);
                var p1 = new Point3d(a.X, a.Y, 0); var p2 = new Point3d(b.X, b.Y, 0);
                Point3dCollection sampled = surface switch
                {
                    CivilDb.TinSurface tin => tin.SampleElevations(p1, p2),
                    CivilDb.GridSurface grid => grid.SampleElevations(p1, p2),
                    _ => throw new InvalidDataException($"Surface kind {surface.GetType().Name} has no exact cut-sampling adapter."),
                };
                var sourcePoints = new List<SectionVerificationRecoveryPolicy.Sample>();
                foreach (Point3d p in sampled)
                {
                    var offset = frame.OffsetOf(new(p.X, p.Y));
                    var projected = frame.PointAt(offset);
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                        Math.Abs(projected.X - p.X) > 0.000001 || Math.Abs(projected.Y - p.Y) > 0.000001)
                        throw new InvalidDataException("Native source sampling returned a point outside the exact CL cut.");
                    sourcePoints.Add(new(offset, p.Z));
                }
                // Native triangle edges may repeat the exact same intersection.
                // Only byte-equal coordinate/elevation samples are deduplicated;
                // conflicting elevations at one offset remain an explicit failure.
                sourcePoints = sourcePoints.Distinct().OrderBy(p => p.Offset).ToList();
                var sectionPoints = (i == 0 ? chains.Existing : chains.Design)
                    .Select(p => new SectionVerificationRecoveryPolicy.Sample(p.Offset, p.Elevation)).ToList();
                var mismatch = SectionVerificationRecoveryPolicy.SurfaceMismatch(sourcePoints, sectionPoints, out var mismatchKind);
                result.Checks.Add(new SectionVerifyCheck { Check = SurfaceComparisonCheck(mismatchKind),
                    Expected = $"{source.SourceName}/{source.SourceHandle}: complete native source cut",
                    Actual = mismatch == null
                        ? $"source={sourcePoints.Count}; section={sectionPoints.Count}; piecewise geometry matches; {sourceProof}"
                        : $"{source.SourceName}/{source.SourceHandle}: {mismatch}; {sourceProof}",
                    Pass = mismatch == null });
            }
        }
        catch (Exception ex)
        {
            result.Checks.Add(new SectionVerifyCheck { Check = SurfaceSourceProofCheck,
                Expected = "readable exact current surface cut", Actual = ex.Message, Pass = false });
        }
    }

    private static ObjectId Resolve(Database db, string handle) =>
        long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) &&
        db.TryGetObjectId(new Handle(value), out var id) && !id.IsNull && !id.IsErased ? id :
        throw new InvalidDataException($"Native handle {handle} is missing.");

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), SectionsWorkflowService.Json)
        ?? throw new InvalidDataException($"Evidence is empty: {path}");
    private static RunManifest ReadManifest(string runId) => JsonSerializer.Deserialize<RunManifest>(
        File.ReadAllText(RuntimeRunManifestService.ArtifactPath(runId, "run_manifest.json")), RunManifestWriter.JsonOptions)
        ?? throw new InvalidDataException("APPLY manifest is empty.");
    private static void SafeRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId != Path.GetFileName(runId) ||
            runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || runId is "." or "..")
            throw new InvalidDataException("Invalid producer run identifier.");
    }
}
