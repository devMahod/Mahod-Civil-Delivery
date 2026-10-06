using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Read-only collector for plan traffic-direction evidence.  Unlike the section
    /// projection collector, this walker deliberately enters ordinary block
    /// definitions as well as XREF definitions because road arrows are commonly
    /// dynamic or nested blocks.  Every transform in the insert chain is retained.
    /// </summary>
    internal static class SectionTrafficArrowCollector
    {
        internal const int MaxBlockNestingDepth = 6;

        internal sealed record CollectResult(
            List<TrafficDirectionEvidenceLogic.ArrowEvidence> Evidence,
            List<SectionExternalSourceEvidence> ExternalSources,
            List<DeliveryFinding> Findings,
            int ScannedBlockReferences,
            int SkippedUnresolvedXrefs,
            int SkippedCyclicDefinitions);

        internal static CollectResult Collect(
            Transaction tr, Database db, StageLog? log = null)
        {
            var evidence = new List<TrafficDirectionEvidenceLogic.ArrowEvidence>();
            var externalSources = new List<SectionExternalSourceEvidence>();
            var findings = new List<DeliveryFinding>();
            int scanned = 0;
            int skippedXrefs = 0;
            int skippedCycles = 0;
            var xrefSnapshots = new SectionXrefSnapshotGuard.Cache();

            log?.Begin("traffic-arrows.collect");
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in model)
            {
                BlockReference? reference = null;
                try { reference = tr.GetObject(id, OpenMode.ForRead) as BlockReference; }
                catch { /* one unreadable entity cannot turn into direction evidence */ }
                if (reference == null) continue;

                TraverseReference(
                    tr,
                    reference,
                    Matrix3d.Identity,
                    ClInstructionReader.ResolvePath(db.Filename, baseDrawingPath: null),
                    null,
                    sourceChain: null,
                    handlePath: null,
                    depth: 0,
                    definitionStack: new HashSet<ObjectId>(),
                    evidence,
                    externalSources,
                    findings,
                    xrefSnapshots,
                    ref scanned,
                    ref skippedXrefs,
                    ref skippedCycles);
            }

            log?.End("traffic-arrows.collect",
                $"blocks={scanned} evidence={evidence.Count} " +
                $"unresolved_xrefs={skippedXrefs} cycles={skippedCycles}");
            return new CollectResult(
                evidence, externalSources, findings,
                scanned, skippedXrefs, skippedCycles);
        }

        private static void TraverseReference(
            Transaction tr,
            BlockReference reference,
            Matrix3d outerTransform,
            string? sourcePath,
            string? sourceHash,
            string? sourceChain,
            string? handlePath,
            int depth,
            HashSet<ObjectId> definitionStack,
            List<TrafficDirectionEvidenceLogic.ArrowEvidence> evidence,
            List<SectionExternalSourceEvidence> externalSources,
            List<DeliveryFinding> findings,
            SectionXrefSnapshotGuard.Cache xrefSnapshots,
            ref int scanned,
            ref int skippedXrefs,
            ref int skippedCycles,
            bool isInsideExternalReference = false,
            bool hasExternalOverlayAncestor = false)
        {
            if (depth > MaxBlockNestingDepth)
            {
                findings.Add(TraversalFinding(
                    "עומק בלוקים/XREF חורג מגבול הבטיחות של ראיות כיוון נסיעה",
                    $"handle_path={handlePath}; max_depth={MaxBlockNestingDepth}"));
                return;
            }
            scanned++;

            BlockTableRecord definition;
            try
            {
                definition = (BlockTableRecord)tr.GetObject(
                    reference.BlockTableRecord, OpenMode.ForRead);
            }
            catch
            {
                return;
            }

            var isXref = definition.IsFromExternalReference ||
                         definition.IsFromOverlayReference;
            if (XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isXref, definition.IsFromOverlayReference,
                    isInsideExternalReference, hasExternalOverlayAncestor))
                return;
            if (isXref && (definition.IsUnloaded || !definition.IsResolved))
            {
                skippedXrefs++;
                findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.XrefTraversalUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = XrefAvailabilityText.Title(definition.Name, definition.IsUnloaded,
                        "לא ניתן להוכיח חצי כיוון נסיעה"),
                    Message = XrefAvailabilityText.Message(definition.Name, definition.IsUnloaded, definition.PathName),
                    RecommendedAction = XrefAvailabilityText.Action(definition.IsUnloaded),
                });
                return;
            }

            var currentPath = sourcePath;
            var currentHash = sourceHash;
            if (isXref)
            {
                var snapshot = xrefSnapshots.Validate(definition, sourcePath);
                currentPath = snapshot.ResolvedPath;
                currentHash = snapshot.Sha256;
                if (!snapshot.IsFresh)
                {
                    findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.ExternalSourceChanged,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = $"העותק הטעון של ה-XREF '{Bidi.Ltr(definition.Name)}' אינו מוכח כזהה לקובץ — ראיות כיוון חסומות",
                        Message = $"path={snapshot.ResolvedPath ?? definition.PathName}; " +
                                  $"reason={snapshot.Failure}; {snapshot.Detail}",
                        RecommendedAction = "יש לבצע Reload ל-XREF ולוודא שקובץ המקור יציב, ואז להריץ PLAN מחדש.",
                    });
                    return;
                }
            }

            // Same transform order as SectionGeometryCollector: a child transform is
            // expressed in its parent's coordinates, so all outer inserts come first.
            Matrix3d composedTransform;
            try { composedTransform = outerTransform * reference.BlockTransform; }
            catch { return; }

            var currentHandlePath = string.IsNullOrEmpty(handlePath)
                ? reference.Handle.ToString()
                : handlePath + "/" + reference.Handle;
            var currentSource = isXref
                ? AppendSource(sourceChain, definition.Name)
                : sourceChain;

            // An XREF insert names a container, not a traffic symbol.  Its contents
            // are considered below.  Ordinary/dynamic block references are evidence
            // only when the pure allowlist classifies their own layer/name.
            if (!isXref)
            {
                var blockName = EffectiveBlockName(tr, reference, definition);
                var sourceClass = TrafficDirectionEvidenceLogic.ClassifySource(
                    reference.Layer, blockName);
                if (sourceClass != TrafficDirectionEvidenceLogic.SourceClass.Unapproved)
                {
                    if (TryAddEvidence(reference, outerTransform, composedTransform, currentSource,
                            currentHandlePath, blockName, evidence) &&
                        !string.IsNullOrWhiteSpace(currentSource) &&
                        currentPath != null && currentHash != null)
                        AddExternalEvidence(
                            currentPath, currentHash, currentSource,
                            externalSources, findings);
                }
            }

            if (depth == MaxBlockNestingDepth) return;
            if (!definitionStack.Add(definition.ObjectId))
            {
                skippedCycles++;
                findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.XrefCycle,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "זוהה מחזור בשרשרת Block/XREF של ראיות כיוון נסיעה — PLAN חסום",
                    Message = $"definition={definition.Name}; handle_path={currentHandlePath}",
                    RecommendedAction = "יש לתקן את שרשרת ההפניות ולהריץ PLAN מחדש.",
                });
                return;
            }

            try
            {
                foreach (ObjectId childId in definition)
                {
                    BlockReference? child = null;
                    try { child = tr.GetObject(childId, OpenMode.ForRead) as BlockReference; }
                    catch { /* fail closed for this child */ }
                    if (child == null) continue;

                    TraverseReference(
                        tr,
                        child,
                        composedTransform,
                        currentPath,
                        currentHash,
                        currentSource,
                        currentHandlePath,
                        depth + 1,
                        definitionStack,
                        evidence,
                        externalSources,
                        findings,
                        xrefSnapshots,
                        ref scanned,
                        ref skippedXrefs,
                        ref skippedCycles,
                        isInsideExternalReference: isInsideExternalReference || isXref,
                        hasExternalOverlayAncestor: hasExternalOverlayAncestor ||
                            (isXref && definition.IsFromOverlayReference));
                }
            }
            finally
            {
                definitionStack.Remove(definition.ObjectId);
            }
        }

        private static bool TryAddEvidence(
            BlockReference reference,
            Matrix3d outerTransform,
            Matrix3d composedTransform,
            string? source,
            string handlePath,
            string blockName,
            List<TrafficDirectionEvidenceLogic.ArrowEvidence> evidence)
        {
            try
            {
                // The audited Mahod/TR arrow library points along local +Y.  Applying
                // the full transform instead of adding Rotation preserves nested
                // rotations and mirrored inserts.  We intentionally do not infer a
                // heading from polyline vertex order: that order is not a direction
                // contract and would create false confidence.
                // Position is already expressed in the parent definition's coordinate
                // system; only the outer chain is applied to it.  The local block
                // transform is used for the forward axis below.
                var position = reference.Position.TransformBy(outerTransform);
                var forward = Vector3d.YAxis.TransformBy(composedTransform);
                var length = Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
                if (!Finite(position.X) || !Finite(position.Y) ||
                    !Finite(length) || length < 1e-9)
                    return false;

                var heading = TrafficDirectionEvidenceLogic.NormalizeHeading(
                    Math.Atan2(forward.Y, forward.X));
                if (!Finite(heading)) return false;

                evidence.Add(new TrafficDirectionEvidenceLogic.ArrowEvidence(
                    position.X,
                    position.Y,
                    heading,
                    reference.Layer ?? string.Empty,
                    blockName,
                    source,
                    handlePath));
                return true;
            }
            catch
            {
                // A malformed transform is missing evidence, never guessed evidence.
                return false;
            }
        }

        private static void AddExternalEvidence(
            string path,
            string hash,
            string? sourceChain,
            List<SectionExternalSourceEvidence> externalSources,
            List<DeliveryFinding> findings) =>
            ClInstructionReader.AddExternalEvidence(
                externalSources,
                new SectionExternalSourceEvidence
                {
                    SourcePath = path,
                    SourceName = Path.GetFileName(path),
                    Sha256 = hash,
                    Roles = { "traffic-direction-arrow" },
                    SourceChain = sourceChain,
                },
                findings);

        private static DeliveryFinding TraversalFinding(string title, string message) => new()
        {
            Code = SectionFindingCodes.XrefTraversalUnresolved,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Error,
            Title = title,
            Message = message,
            RecommendedAction = "יש לתקן את מבנה ה-Block/XREF ולהריץ PLAN מחדש.",
        };

        private static string EffectiveBlockName(
            Transaction tr, BlockReference reference, BlockTableRecord fallback)
        {
            try
            {
                if (reference.IsDynamicBlock && !reference.DynamicBlockTableRecord.IsNull)
                {
                    var dynamicDefinition = (BlockTableRecord)tr.GetObject(
                        reference.DynamicBlockTableRecord, OpenMode.ForRead);
                    if (!string.IsNullOrWhiteSpace(dynamicDefinition.Name))
                        return dynamicDefinition.Name;
                }
            }
            catch
            {
                // Anonymous name + approved layer remains valid evidence.
            }
            return fallback.Name ?? string.Empty;
        }

        private static string AppendSource(string? outer, string name) =>
            string.IsNullOrWhiteSpace(outer) ? name : outer + ">" + name;

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
