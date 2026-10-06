using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Reads CL instructions from the host, ordinary nested blocks, attached XREFs
    /// and configured side DWGs. Every external entity retains the identity/hash of
    /// the database that actually owns it and the complete insert transform.
    /// </summary>
    public sealed class ClInstructionReader
    {
        public double LabelSearchRadius { get; init; } = 20.0;
        public const double StraightSagittaToleranceM = 0.5;
        internal const int MaxBlockNestingDepth = 6;

        public sealed class ReadResult
        {
            public List<ClSourceRecord> Records { get; } = new();
            public List<DeliveryFinding> Findings { get; } = new();
            public List<SectionExternalSourceEvidence> ExternalSources { get; } = new();
            internal List<IgnoredCandidate> IgnoredCandidates { get; } = new();
        }

        internal sealed record IgnoredCandidate(
            string SourcePath, string SourceHash, string HandlePath,
            string EntityType, string Layer, int VertexCount, string Reason);

        private sealed record SourceContext(
            string Name, string? Path, string Hash, string? XrefChain, bool IsExternal, string? InstanceKey = null);

        private sealed record Candidate(
            Entity Entity, SourceContext Source, Matrix3d Transform, string HandlePath, string EffectiveLayer);

        private sealed record LabelCandidate(Pt2 Position, string Text, SourceContext Source, string EffectiveLayer);

        public ReadResult Read(Database db, Transaction tr, ProjectProfile profile, StageLog? log = null)
        {
            var result = new ReadResult();
            var drawingPath = ResolvePath(db.Filename, baseDrawingPath: null);
            log?.Begin("cl.hash_drawing", drawingPath);
            var drawingHash = HashFileShared(drawingPath);
            log?.End("cl.hash_drawing");
            var drawingName = string.IsNullOrEmpty(drawingPath)
                ? "(unsaved)" : Path.GetFileName(drawingPath);
            var host = new SourceContext(drawingName, drawingPath, drawingHash,
                XrefChain: null, IsExternal: false);

            var layerPatterns = profile.Sections.Cl.LayerPatterns;
            var allowedTypes = profile.Sections.Cl.AllowedEntityTypes.Count > 0
                ? new HashSet<string>(profile.Sections.Cl.AllowedEntityTypes, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(new[] { "LINE", "LWPOLYLINE", "POLYLINE" }, StringComparer.OrdinalIgnoreCase);

            var labels = new List<LabelCandidate>();
            var candidates = new List<Candidate>();
            var xrefSnapshots = new SectionXrefSnapshotGuard.Cache();

            // 1.4.1 (Codex 11:34): the CL scope is decided BEFORE any host traversal. An explicit scope that is unknown
            // or has no CL file blocks; it never falls back to reading the host. In "source-file" scope the host model
            // space (its XREFs, clip/transform findings and labels) is not read at all.
            var scopeProblems = ProjectProfileSchemaPolicy.ClScopeProblems(profile.Sections.Cl);
            if (scopeProblems.Count > 0)
            {
                foreach (var problem in scopeProblems)
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.ClSourceMissing,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "הגדרת מקור קווי ה-CL אינה תקינה — PLAN חסום",
                        Message = problem,
                        RecommendedAction = "יש להריץ שוב 'בחר קובץ CL נפרד' או הגדרת פרויקט.",
                    });
                return result;
            }
            var readHost = !IsSourceFileScoped(profile.Sections.Cl);

            log?.Begin("cl.scan_modelspace", readHost ? null : "skipped: layer_scope=source-file");
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int scanned = 0;
            foreach (ObjectId id in readHost ? ms.Cast<ObjectId>() : Enumerable.Empty<ObjectId>())
            {
                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead); }
                catch (Exception ex)
                {
                    result.Findings.Add(TraversalFinding(
                        "ישות ב-Model Space אינה ניתנת לקריאה", ex.Message));
                    continue;
                }
                scanned++;
                if (scanned % 20000 == 0)
                    log?.Info($"cl.scan_modelspace progress: {scanned} entities");

                if (obj is BlockReference br)
                {
                    TraverseReference(tr, br, Matrix3d.Identity, host, handlePath: null,
                        depth: 0, new HashSet<ObjectId>(), candidates, labels,
                        result.ExternalSources, result.Findings, xrefSnapshots, log, inheritedLayer: null);
                }
                else if (obj is Entity ent)
                {
                    CollectEntity(ent, host, Matrix3d.Identity, ent.Handle.ToString(),
                        candidates, labels, inheritedLayer: null);
                }
            }
            log?.End("cl.scan_modelspace",
                $"entities={scanned} candidates={candidates.Count} labels={labels.Count}");

            var externalRecords = new List<ClSourceRecord>();
            var missingCl = new List<string>();
            int resolvedCl = 0;
            var seenExternalCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clFile in profile.Sections.Cl.SourceFiles)
            {
                var canonical = ResolvePath(clFile, drawingPath);
                if (!string.IsNullOrWhiteSpace(canonical) && !seenExternalCandidates.Add(canonical))
                {
                    log?.Info($"cl.external_file duplicate candidate ignored: {canonical}");
                    continue;
                }
                var outcome = ReadExternalClFile(clFile, drawingPath ?? string.Empty,
                    layerPatterns, allowedTypes, externalRecords, result, log,
                    profile.Sections.Cl.Numbering.LabelLayerPatterns);
                if (outcome == ExternalClOutcome.Read) resolvedCl++;
                else if (outcome == ExternalClOutcome.NotFound) missingCl.Add(clFile);
            }
            if (profile.Sections.Cl.SourceFiles.Count > 0 && resolvedCl == 0 && missingCl.Count > 0)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ClSourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "אף אחד מקובצי ה-CL שהוגדרו לא נמצא",
                    Message = string.Join(" | ", missingCl),
                    RecommendedAction = "יש למקם את קובץ ה-CL ליד המודל או לתקן את הנתיב בפרופיל, ואז להריץ PLAN מחדש.",
                });
            }

            log?.Begin("cl.build_records", $"candidates={candidates.Count}");
            foreach (var candidate in candidates)
            {
                if (!MatchesLayer(candidate.EffectiveLayer, layerPatterns)) continue;
                if (!allowedTypes.Contains(EntityTypeName(candidate.Entity))) continue;
                result.Records.AddRange(BuildRecords(
                    candidate, tr, result.Findings, result.IgnoredCandidates));
            }
            result.Records.AddRange(externalRecords);
            log?.End("cl.build_records",
                $"records={result.Records.Count} (host={result.Records.Count - externalRecords.Count} external={externalRecords.Count})");

            log?.Begin("cl.attach_labels");
            // Legacy (no scope): unchanged b34 call over every record. A known side effect, reported to Codex 06.10:
            // separate-file records lose the section number their own labels gave them, so 6422 keeps "STA-…" ids.
            // Changing that would rename accepted sections; it is a decision, not part of this fix.
            // "source-file" scope: the host was not read, so its (empty) label set must not reset the file's numbers.
            if (readHost) AttachNearbyLabels(result.Records, labels, profile.Sections.Cl.Numbering.LabelLayerPatterns);
            log?.End("cl.attach_labels");
            AddIgnoredCandidateFinding(result);
            return result;
        }

        private enum ExternalClOutcome { Read, NotFound, IsHost, Unreadable }

        private ExternalClOutcome ReadExternalClFile(
            string clFile,
            string hostDrawingPath,
            IReadOnlyList<string> layerPatterns,
            HashSet<string> allowedTypes,
            List<ClSourceRecord> records,
            ReadResult result,
            StageLog? log,
            IReadOnlyList<string> labelLayerPatterns)
        {
            var resolved = ResolvePath(clFile, hostDrawingPath);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                result.Findings.Add(SourceFinding(
                    "נתיב קובץ ה-CL אינו ניתן לפענוח", clFile));
                return ExternalClOutcome.Unreadable;
            }

            if (!string.IsNullOrEmpty(hostDrawingPath) && SamePath(resolved, hostDrawingPath))
                return ExternalClOutcome.IsHost;

            log?.Begin("cl.external_file", resolved);
            if (!File.Exists(resolved))
            {
                log?.End("cl.external_file", "not found (candidate)");
                return ExternalClOutcome.NotFound;
            }

            var fileName = Path.GetFileName(resolved);
            var fileHash = HashFileShared(resolved);
            if (string.IsNullOrWhiteSpace(fileHash))
            {
                result.Findings.Add(SourceFinding(
                    $"לא ניתן לחשב SHA-256 לקובץ ה-CL: {Bidi.Ltr(fileName)}", resolved));
                return ExternalClOutcome.Unreadable;
            }

            int scanned = 0, matched = 0;
            try
            {
                using var sideDwg = SideDwg.OpenReadOnly(resolved);
                var side = sideDwg.Db;
                log?.Info($"cl.external_file source={sideDwg.Source}");
                using var str = side.TransactionManager.StartTransaction();

                var source = new SourceContext(fileName, resolved, fileHash,
                    XrefChain: null, IsExternal: true);
                var labels = new List<LabelCandidate>();
                var candidates = new List<Candidate>();
                var xrefSnapshots = new SectionXrefSnapshotGuard.Cache();
                var bt = (BlockTable)str.GetObject(side.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)str.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    scanned++;
                    DBObject obj;
                    try { obj = str.GetObject(id, OpenMode.ForRead); }
                    catch (Exception ex)
                    {
                        result.Findings.Add(TraversalFinding(
                            $"ישות בקובץ CL אינה ניתנת לקריאה: {Bidi.Ltr(fileName)}", ex.Message));
                        continue;
                    }

                    if (obj is BlockReference br)
                    {
                        TraverseReference(str, br, Matrix3d.Identity, source, handlePath: null,
                            depth: 0, new HashSet<ObjectId>(), candidates, labels,
                            result.ExternalSources, result.Findings, xrefSnapshots, log, inheritedLayer: null);
                    }
                    else if (obj is Entity ent)
                    {
                        CollectEntity(ent, source, Matrix3d.Identity, ent.Handle.ToString(),
                            candidates, labels, inheritedLayer: null);
                    }
                }

                var local = new List<ClSourceRecord>();
                foreach (var candidate in candidates)
                {
                    if (!MatchesLayer(candidate.EffectiveLayer, layerPatterns)) continue;
                    if (!allowedTypes.Contains(EntityTypeName(candidate.Entity))) continue;
                    local.AddRange(BuildRecords(
                        candidate, str, result.Findings, result.IgnoredCandidates));
                }
                matched = local.Count;
                AttachNearbyLabels(local, labels, labelLayerPatterns);
                records.AddRange(local);
                str.Commit();

                var afterHash = HashFileShared(resolved);
                if (!string.Equals(fileHash, afterHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.Findings.Add(SourceFinding(
                        $"קובץ ה-CL השתנה בזמן PLAN: {Bidi.Ltr(fileName)}", resolved));
                    return ExternalClOutcome.Unreadable;
                }

                var live = string.Equals(sideDwg.Source, "open-document", StringComparison.Ordinal);
                AddExternalEvidence(result.ExternalSources, new SectionExternalSourceEvidence
                {
                    SourcePath = resolved,
                    SourceName = fileName,
                    Sha256 = fileHash,
                    Roles = { "cl-side-file" },
                    LiveDatabaseRevision = live ? DrawingRevisionTracker.Capture(side) : null,
                    RequiresLiveDatabase = live,
                }, result.Findings);
            }
            catch (Exception ex)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ClSourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = $"לא ניתן לקרוא את קובץ ה-CL שהוגדר: {Bidi.Ltr(fileName)}",
                    Message = ex.Message,
                    RecommendedAction = "יש לוודא שהקובץ תקין ונגיש, ואז להריץ PLAN מחדש.",
                });
                log?.End("cl.external_file", "UNREADABLE");
                return ExternalClOutcome.Unreadable;
            }

            log?.End("cl.external_file", $"entities={scanned} cl_records={matched}");
            return ExternalClOutcome.Read;
        }

        private static void TraverseReference(
            Transaction tr,
            BlockReference reference,
            Matrix3d outerTransform,
            SourceContext parentSource,
            string? handlePath,
            int depth,
            HashSet<ObjectId> definitionStack,
            List<Candidate> candidates,
            List<LabelCandidate> labels,
            List<SectionExternalSourceEvidence> externalSources,
            List<DeliveryFinding> findings,
            SectionXrefSnapshotGuard.Cache xrefSnapshots,
            StageLog? log,
            string? inheritedLayer,
            bool isInsideExternalReference = false,
            bool hasExternalOverlayAncestor = false)
        {
            // The reference's own effective layer is what its layer-0 content is drawn on.
            var referenceLayer = ClEffectiveLayer.Resolve(reference.Layer, inheritedLayer);
            var currentHandle = ClLabelSelection.HandlePath(handlePath, reference.Handle.ToString());

            if (depth > MaxBlockNestingDepth)
            {
                findings.Add(TraversalFinding(
                    "עומק בלוקים/XREF חורג מגבול הבטיחות",
                    $"handle_path={currentHandle}; max_depth={MaxBlockNestingDepth}"));
                return;
            }

            BlockTableRecord definition;
            try
            {
                definition = (BlockTableRecord)tr.GetObject(
                    reference.BlockTableRecord, OpenMode.ForRead);
            }
            catch (Exception ex)
            {
                findings.Add(TraversalFinding(
                    "לא ניתן לפתוח הגדרת Block/XREF במהלך חיפוש CL",
                    $"handle_path={currentHandle}; {ex.Message}"));
                return;
            }

            var isXref = definition.IsFromExternalReference || definition.IsFromOverlayReference;
            if (XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isXref, definition.IsFromOverlayReference,
                    isInsideExternalReference, hasExternalOverlayAncestor))
            {
                log?.Info($"cl.xref skipped non-visible overlay branch: {definition.Name}");
                return;
            }

            if (!definitionStack.Add(definition.ObjectId))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.XrefCycle,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "זוהה מחזור בשרשרת Block/XREF — חיפוש ה-CL נעצר ללא ניחוש",
                    Message = $"definition={definition.Name}; handle_path={currentHandle}",
                    RecommendedAction = "יש לתקן את שרשרת ההפניות ולהריץ PLAN מחדש.",
                });
                return;
            }

            try
            {
                var source = parentSource;
                if (isXref)
                {
                    log?.Info($"cl.xref {definition.Name} status={definition.XrefStatus} " +
                              $"unloaded={definition.IsUnloaded} resolved={definition.IsResolved}");
                    if (definition.IsUnloaded || !definition.IsResolved)
                    {
                        findings.Add(UnresolvedXrefFinding(definition));
                        return;
                    }

                    var snapshot = xrefSnapshots.Validate(definition, parentSource.Path);
                    if (!snapshot.IsFresh)
                    {
                        findings.Add(SourceFinding(
                            $"העותק הטעון של ה-XREF '{Bidi.Ltr(definition.Name)}' אינו מוכח כזהה לקובץ — PLAN חסום",
                            $"path={snapshot.ResolvedPath ?? definition.PathName}; " +
                            $"reason={snapshot.Failure}; {snapshot.Detail}"));
                        return;
                    }

                    var chain = AppendChain(parentSource.XrefChain, definition.Name);
                    var resolvedName = Path.GetFileName(snapshot.ResolvedPath!) ?? definition.Name;
                    source = new SourceContext(
                        resolvedName, snapshot.ResolvedPath,
                        snapshot.Sha256!, chain, IsExternal: true,
                        // Two insertions of one XREF share path and chain; their full handle paths do not, also when
                        // the XREF sits in an ordinary block inserted twice (A0/C5 vs B0/C5).
                        InstanceKey: ClLabelSelection.InstanceKeyBelow(parentSource.InstanceKey, enteringXref: true, currentHandle));
                    AddExternalEvidence(externalSources, new SectionExternalSourceEvidence
                    {
                        SourcePath = snapshot.ResolvedPath!,
                        SourceName = resolvedName,
                        Sha256 = snapshot.Sha256!,
                        Roles = { "cl-xref" },
                        SourceChain = chain,
                    }, findings);
                }

                Matrix3d transform;
                try { transform = outerTransform * reference.BlockTransform; }
                catch (Exception ex)
                {
                    findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.XrefTransformInvalid,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "טרנספורמציית Block/XREF אינה ניתנת להרכבה",
                        Message = $"handle_path={currentHandle}; {ex.Message}",
                    });
                    return;
                }

                foreach (ObjectId id in definition)
                {
                    DBObject obj;
                    try { obj = tr.GetObject(id, OpenMode.ForRead); }
                    catch (Exception ex)
                    {
                        findings.Add(TraversalFinding(
                            "ישות בתוך Block/XREF אינה ניתנת לקריאה",
                            $"definition={definition.Name}; handle_path={currentHandle}; {ex.Message}"));
                        continue;
                    }

                    if (obj is BlockReference nested)
                    {
                        TraverseReference(tr, nested, transform, source, currentHandle,
                            depth + 1, definitionStack, candidates, labels,
                            externalSources, findings, xrefSnapshots, log, referenceLayer,
                            isInsideExternalReference: isInsideExternalReference || isXref,
                            hasExternalOverlayAncestor: hasExternalOverlayAncestor ||
                                (isXref && definition.IsFromOverlayReference));
                    }
                    else if (obj is Entity ent)
                    {
                        CollectEntity(ent, source, transform,
                            currentHandle + "/" + ent.Handle, candidates, labels, referenceLayer);
                    }
                }
            }
            finally
            {
                definitionStack.Remove(definition.ObjectId);
            }
        }

        private static void CollectEntity(
            Entity ent,
            SourceContext source,
            Matrix3d transform,
            string handle,
            List<Candidate> candidates,
            List<LabelCandidate> labels,
            string? inheritedLayer)
        {
            switch (ent)
            {
                case DBText text when !string.IsNullOrWhiteSpace(text.TextString):
                    labels.Add(new LabelCandidate(
                        ApplyPt(transform, text.Position), text.TextString.Trim(), source,
                        ClEffectiveLayer.Resolve(text.Layer, inheritedLayer)));
                    break;
                case MText mtext when !string.IsNullOrWhiteSpace(mtext.Contents):
                    labels.Add(new LabelCandidate(
                        ApplyPt(transform, mtext.Location), mtext.Text.Trim(), source,
                        ClEffectiveLayer.Resolve(mtext.Layer, inheritedLayer)));
                    break;
                case Line:
                case Polyline:
                case Polyline2d:
                    candidates.Add(new Candidate(ent, source, transform, handle,
                        ClEffectiveLayer.Resolve(ent.Layer, inheritedLayer)));
                    break;
            }
        }

        private IEnumerable<ClSourceRecord> BuildRecords(
            Candidate candidate,
            Transaction transaction,
            List<DeliveryFinding> findings,
            List<IgnoredCandidate> ignored)
        {
            var entity = candidate.Entity;
            if (!ProjectSetupScanner.IsUsableTransformValues(candidate.Transform.ToArray()))
            {
                var record = Skeleton(candidate, new double[4], new double[4]);
                record.Status = DeliveryStatus.Blocked;
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.XrefTransformInvalid,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "טרנספורמציית ה-XREF מנוונת — אי אפשר לסמוך על גיאומטריית ה-CL",
                    Message = candidate.Source.XrefChain ?? candidate.Source.Path ?? string.Empty,
                    AffectedRecordIds = { record.RecordId },
                });
                yield return record;
                yield break;
            }

            SectionClGeometryContract.Geometry? geometry = null;
            string? readFailure = null;
            try { geometry = SectionClGeometryContract.Read(entity, transaction, candidate.Transform); }
            catch (Exception ex) { readFailure = ex.GetType().Name + ": " + ex.Message; }
            if (readFailure != null)
            {
                var invalid = Skeleton(candidate, new double[4], new double[4]);
                invalid.Status = DeliveryStatus.Blocked;
                invalid.Findings.Add(InvalidGeometryFinding(candidate, invalid.RecordId, readFailure));
                yield return invalid;
                yield break;
            }
            var vertices = geometry!.SourceVertices;
            var wcsVertices = geometry.WorldVertices;
            var shape = SectionClGeometryContract.Analyze(geometry);
            if (shape.Kind == SectionClCandidateLogic.Decision.IgnoreClosedNonInstruction)
            {
                ignored.Add(new IgnoredCandidate(
                    candidate.Source.Path ?? candidate.Source.Name,
                    candidate.Source.Hash,
                    candidate.HandlePath,
                    EntityTypeName(candidate.Entity),
                    candidate.EffectiveLayer,
                    geometry.SourceVertexCount,
                    "closed-polyline"));
                yield break;
            }
            if (shape.Kind == SectionClCandidateLogic.Decision.Degenerate)
            {
                var invalid = Skeleton(candidate, EndpointArray(vertices), EndpointArray(wcsVertices));
                invalid.Status = DeliveryStatus.Blocked;
                invalid.Findings.Add(InvalidGeometryFinding(candidate, invalid.RecordId,
                    $"chord={shape.ChordLength:F3}; sagitta_bound={shape.MaxSagitta:F3}"));
                yield return invalid;
                yield break;
            }

            yield return Skeleton(candidate, EndpointArray(vertices), EndpointArray(wcsVertices));
        }

        private static double[] EndpointArray(IReadOnlyList<Pt2> vertices) => vertices.Count < 2
            ? new double[4] : new[] { vertices[0].X, vertices[0].Y, vertices[^1].X, vertices[^1].Y };

        private static DeliveryFinding InvalidGeometryFinding(Candidate candidate, string recordId, string detail) => new()
        {
            Code = SectionFindingCodes.ClDegenerate,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Error,
            Title = "גאומטריית CL אינה הוראת חתך ישרה ומוכחת",
            Message = $"handle={candidate.HandlePath}; {detail}",
            RecommendedAction = "יש לבחור או לשרטט קו LINE פתוח במיקום חתך שאישר מהנדס, ולתכנן מחדש. אין ליישר קשת או פוליליין שבור ללא החלטה הנדסית.",
            AffectedRecordIds = { recordId },
        };

        private static ClSourceRecord Skeleton(
            Candidate candidate, double[] sourceEndpoints, double[] wcsEndpoints)
        {
            var a = new Pt2(wcsEndpoints.Length == 4 ? wcsEndpoints[0] : 0,
                wcsEndpoints.Length == 4 ? wcsEndpoints[1] : 0);
            var b = new Pt2(wcsEndpoints.Length == 4 ? wcsEndpoints[2] : 0,
                wcsEndpoints.Length == 4 ? wcsEndpoints[3] : 0);
            var safeHandle = candidate.HandlePath
                .Replace(':', '-').Replace('/', '-').Replace('\\', '-');
            return new ClSourceRecord
            {
                RecordId = $"cl-{safeHandle}",
                SourceDrawing = candidate.Source.Name,
                SourceDrawingHash = candidate.Source.Hash,
                SourceDrawingPath = candidate.Source.Path,
                SourceHandle = candidate.HandlePath,
                SourceXref = candidate.Source.XrefChain,
                SourceEntityType = EntityTypeName(candidate.Entity),
                SourceLayer = candidate.EffectiveLayer,
                SourceInstanceKey = candidate.Source.InstanceKey,
                SourceEndpoints = sourceEndpoints,
                WcsEndpoints = wcsEndpoints,
                XrefTransform = candidate.Source.XrefChain != null
                    ? ToAffine(candidate.Transform).Values : null,
                Length = a.DistanceTo(b),
                AngleDeg = a.DistanceTo(b) > SectionMath.Eps
                    ? SectionMath.DirectionDeg(a, b) : 0,
            };
        }

        private void AttachNearbyLabels(
            List<ClSourceRecord> records,
            List<LabelCandidate> labels,
            IReadOnlyList<string> labelLayerPatterns)
        {
            foreach (var record in records)
            {
                if (record.WcsEndpoints.Length != 4) continue;
                var midpoint = new Pt2(
                    (record.WcsEndpoints[0] + record.WcsEndpoints[2]) / 2.0,
                    (record.WcsEndpoints[1] + record.WcsEndpoints[3]) / 2.0);
                var near = labels
                    .Where(label => SameSource(record, label.Source))
                    .Select(label => (label.Text, Distance: label.Position.DistanceTo(midpoint)))
                    .Where(label => label.Distance <= LabelSearchRadius)
                    .OrderBy(label => label.Distance)
                    .Take(5)
                    .ToList();

                // The evidence list keeps the nearest five as before; only the number choice is layer-aware.
                record.NearbyLabels.AddRange(near.Select(label => label.Text));
                var choice = ClLabelSelection.Choose(
                    labels.Where(label => SameSource(record, label.Source))
                        .Select(label => new ClLabelSelection.Label(
                            label.Text, label.Position.DistanceTo(midpoint), label.EffectiveLayer, label.Source.InstanceKey))
                        .Where(label => label.Distance <= LabelSearchRadius),
                    record.SourceLayer, labelLayerPatterns, record.SourceInstanceKey);
                record.CandidateSectionNumber = choice.Number;
                if (choice.Ambiguous)
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = ClLabelSelection.AmbiguousCode,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Warning,
                        Title = "שתי תוויות שונות באותו מרחק מקו ה-CL — מספר החתך לא נבחר",
                        Message = string.Join(" | ", choice.TiedTexts),
                        RecommendedAction = "שם החתך יהיה לפי התחנה. כדי לקבוע שם, יש להגדיר שכבת תוויות בפרויקט או לתקן את התוויות בשרטוט.",
                    });
            }
        }

        private static bool SameSource(ClSourceRecord record, SourceContext source) =>
            SamePathOrValue(record.SourceDrawingPath, source.Path) &&
            string.Equals(record.SourceXref, source.XrefChain, StringComparison.OrdinalIgnoreCase);

        internal static bool IsClosedPolyline(Entity entity) => entity switch
        {
            Polyline polyline => polyline.Closed,
            Polyline2d polyline2d => polyline2d.Closed,
            _ => false,
        };

        public const string LayerScopeSourceFile = ProjectProfile.SectionsProfile.ClProfile.LayerScopeSourceFile;

        /// <summary>True when the CL layers were picked from a separate drawing and belong to it only.</summary>
        internal static bool IsSourceFileScoped(ProjectProfile.SectionsProfile.ClProfile cl) =>
            string.Equals(cl.LayerScope, LayerScopeSourceFile, StringComparison.Ordinal);

        internal static bool MatchesLayer(string layer, IReadOnlyList<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) return true;
            return patterns.Any(pattern => WildcardMatch(layer, pattern));
        }

        // Same expression as before; SectionProjectionLogic keeps one compiled regex per pattern.
        internal static bool WildcardMatch(string text, string pattern) =>
            MahodAI.CivilDelivery.Shared.SectionProjectionLogic.Wildcard(text ?? string.Empty, pattern);

        internal static string? ResolvePath(string? sourcePath, string? baseDrawingPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            try
            {
                if (Path.IsPathFullyQualified(sourcePath)) return Path.GetFullPath(sourcePath);
                var directory = string.IsNullOrWhiteSpace(baseDrawingPath)
                    ? null : Path.GetDirectoryName(Path.GetFullPath(baseDrawingPath));
                return string.IsNullOrWhiteSpace(directory)
                    ? null
                    : Path.GetFullPath(Path.Combine(directory, sourcePath));
            }
            catch { return null; }
        }

        internal static string HashFileShared(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            try
            {
                using var sha = SHA256.Create();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
            catch { return string.Empty; }
        }

        internal static void AddExternalEvidence(
            List<SectionExternalSourceEvidence> target,
            SectionExternalSourceEvidence evidence,
            List<DeliveryFinding> findings)
        {
            var existing = target.FirstOrDefault(x => SamePath(x.SourcePath, evidence.SourcePath));
            if (existing == null)
            {
                target.Add(evidence);
                return;
            }

            if (!string.Equals(existing.Sha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase) ||
                existing.RequiresLiveDatabase != evidence.RequiresLiveDatabase ||
                !string.Equals(existing.LiveDatabaseRevision, evidence.LiveDatabaseRevision,
                    StringComparison.Ordinal))
            {
                findings.Add(SourceFinding(
                    "אותו קובץ חיצוני נקרא עם ראיות סותרות במהלך PLAN",
                    evidence.SourcePath));
                return;
            }

            foreach (var role in evidence.Roles)
                if (!existing.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
                    existing.Roles.Add(role);
        }

        private static string EntityTypeName(Entity entity) => entity switch
        {
            Line => "LINE",
            Polyline => "LWPOLYLINE",
            Polyline2d => "POLYLINE",
            _ => entity.GetType().Name.ToUpperInvariant(),
        };

        private static Affine3 ToAffine(Matrix3d matrix)
        {
            var values = matrix.ToArray();
            return new Affine3(new[]
            {
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
            });
        }

        private static Pt2 ApplyPt(Matrix3d transform, Point3d point)
        {
            var transformed = point.TransformBy(transform);
            return new Pt2(transformed.X, transformed.Y);
        }

        private static (Pt2 A, Pt2 B) Apply(Affine3 affine, (Pt2 A, Pt2 B) segment) =>
            (affine.Apply(segment.A), affine.Apply(segment.B));

        private static double[] SegmentToArray((Pt2 A, Pt2 B) segment) =>
            new[] { segment.A.X, segment.A.Y, segment.B.X, segment.B.Y };

        private static string AppendChain(string? parent, string child) =>
            string.IsNullOrWhiteSpace(parent) ? child : parent + " > " + child;

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool SamePathOrValue(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return SamePath(a, b);
        }

        private static DeliveryFinding UnresolvedXrefFinding(BlockTableRecord definition)
        {
            var unloaded = definition.IsUnloaded || definition.XrefStatus == XrefStatus.Unloaded;
            var notFound = definition.XrefStatus.ToString()
                .Contains("FileNotFound", StringComparison.OrdinalIgnoreCase);
            return new DeliveryFinding
            {
                Code = SectionFindingCodes.XrefTraversalUnresolved,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = unloaded
                    ? $"ה-XREF '{Bidi.Ltr(definition.Name)}' במצב UNLOADED בשרטוט — PLAN חסום"
                    : notFound
                        ? $"ה-XREF '{Bidi.Ltr(definition.Name)}' לא נמצא — הגיאומטריה שלו אינה זמינה ו-PLAN חסום"
                        : $"ה-XREF '{Bidi.Ltr(definition.Name)}' אינו פתור — PLAN חסום",
                Message = definition.PathName ?? string.Empty,
                RecommendedAction = unloaded
                    ? "יש לבצע Reload ב-XREF palette ולהריץ PLAN מחדש."
                    : "יש לתקן את Found At ב-XREF palette ולהריץ PLAN מחדש.",
            };
        }

        private static DeliveryFinding TraversalFinding(string title, string message) => new()
        {
            Code = SectionFindingCodes.XrefTraversalUnresolved,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Error,
            Title = title,
            Message = message,
            RecommendedAction = "יש לתקן/לרענן את מקור ה-Block/XREF ולהריץ PLAN מחדש.",
        };

        private static DeliveryFinding SourceFinding(string title, string message) => new()
        {
            Code = SectionFindingCodes.ExternalSourceChanged,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Error,
            Title = title,
            Message = message,
            RecommendedAction = "יש לוודא שהמקור החיצוני קיים, נגיש ויציב ולהריץ PLAN מחדש.",
        };

        private static void AddIgnoredCandidateFinding(ReadResult result)
        {
            if (result.IgnoredCandidates.Count == 0) return;
            var grouped = result.IgnoredCandidates
                .GroupBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}: {group.Count()}");
            var finding = new DeliveryFinding
            {
                Code = SectionFindingCodes.ClNonInstructionIgnored,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Info,
                Title = $"{result.IgnoredCandidates.Count} פוליגונים סגורים בשכבת CL סווגו כסימוני שרטוט ולא כהוראות חתך",
                Message = string.Join(" | ", grouped) +
                          "; rule=closed_polyline_has_no_two_open_cut_ends; " +
                          "הן נרשמו עם handle ו-SHA אך לא נוצרו עבורן שורות החלטה מלאכותיות.",
                RecommendedAction = "אם פוליגון סגור אמור להיות חתך, יש להחליפו בקו חצייה פתוח וישר ולהריץ PLAN מחדש.",
            };
            foreach (var candidate in result.IgnoredCandidates)
            {
                finding.SourceRefs.Add(new ProvenanceRef
                {
                    SourceKind = "drawing",
                    SourcePathOrUri = candidate.SourcePath,
                    DrawingChecksum = candidate.SourceHash,
                    SourceHandle = candidate.HandlePath,
                    EntityType = candidate.EntityType,
                    Layer = candidate.Layer,
                    MeasurementMethod = $"{candidate.Reason}; closed=true; vertices={candidate.VertexCount}",
                    ToolVersion = SectionPlanService.ToolVersion,
                });
            }
            result.Findings.Add(finding);
        }
    }
}
