using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Collects utility/plan-mark geometry through ordinary blocks and XREFs while
    /// retaining the owning external DWG hash and full insert-handle path. An
    /// unresolved, cyclic or unhashable traversal becomes a PLAN blocker.
    /// </summary>
    internal static class SectionGeometryCollector
    {
        internal const int MaxBlockNestingDepth = 6;
        internal const double MaxCurveSagittaM = 0.005;

        internal sealed record CollectedLine(
            string Layer,
            string? Xref,
            string SourceHandle,
            string? SourceDrawingPath,
            string? SourceDrawingHash,
            List<V3> Verts,
            bool Closed,
            ProjectionRuleMatch Rule,
            string Kind);

        internal sealed record CollectResult(
            List<CollectedLine> Utilities,
            List<CollectedLine> PlanMarks,
            List<SectionExternalSourceEvidence> ExternalSources,
            List<DeliveryFinding> Findings)
        {
            public List<SectionHatchSpanLabelService.Region> PlanRegions { get; } = new();
            /// <summary>b7: finding id → the local-cut source of exactly that topology finding.</summary>
            public Dictionary<string, SectionHatchLocalCut.Source> LocalCutSources { get; } = new(StringComparer.Ordinal);
            public int Total => Utilities.Count + PlanMarks.Count;
        }

        private sealed record SourceContext(
            string? Path, string? Hash, string? XrefChain, bool IsExternal);

        internal static CollectResult Collect(
            Transaction tr, Database db, ProjectProfile profile, StageLog? log = null)
        {
            var utilityRules = ToConfigs(profile.Sections.Projection.UtilityRules);
            var markRules = WithObservedPlanMarkDefaults(
                ToConfigs(profile.Sections.Projection.PlanMarkRules), profile.Provenance);
            var result = new CollectResult(new(), new(), new(), new());
            var xrefSnapshots = new SectionXrefSnapshotGuard.Cache();
            var hostPath = ClInstructionReader.ResolvePath(db.Filename, baseDrawingPath: null);
            var host = new SourceContext(hostPath,
                ClInstructionReader.HashFileShared(hostPath), null, IsExternal: false);

            log?.Begin("project.collect");
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int scanned = 0;
            foreach (ObjectId id in model)
            {
                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead); }
                catch (Exception ex)
                {
                    result.Findings.Add(TraversalFinding(
                        "ישות ב-Model Space אינה ניתנת לקריאה עבור חתכים", ex.Message));
                    continue;
                }
                if (++scanned % 50000 == 0)
                    log?.Info($"project.collect progress: {scanned}");

                if (obj is BlockReference reference)
                {
                    TraverseReference(tr, reference, host,
                        handlePath: null, depth: 0, Matrix3d.Identity,
                        new HashSet<ObjectId>(),
                        utilityRules, markRules, result, xrefSnapshots);
                }
                else if (obj is Entity entity)
                {
                    Consider(entity, host, Matrix3d.Identity, entity.Handle.ToString(),
                        utilityRules, markRules, result);
                }
            }
            log?.End("project.collect",
                $"entities={scanned} utilities={result.Utilities.Count} marks={result.PlanMarks.Count} " +
                $"regions={result.PlanRegions.Count} external_sources={result.ExternalSources.Count} findings={result.Findings.Count}");
            return result;
        }

        private static void TraverseReference(
            Transaction tr,
            BlockReference br,
            SourceContext parentSource,
            string? handlePath,
            int depth,
            Matrix3d outerTransform,
            HashSet<ObjectId> definitionStack,
            List<ProjectionRuleConfig> utilityRules,
            List<ProjectionRuleConfig> markRules,
            CollectResult result,
            SectionXrefSnapshotGuard.Cache xrefSnapshots,
            bool isInsideExternalReference = false,
            bool hasExternalOverlayAncestor = false)
        {
            var currentHandle = string.IsNullOrWhiteSpace(handlePath)
                ? br.Handle.ToString()
                : handlePath + "/" + br.Handle;
            if (depth > MaxBlockNestingDepth)
            {
                result.Findings.Add(TraversalFinding(
                    "עומק בלוקים/XREF חורג מגבול הבטיחות של הקרנת החתך",
                    $"handle_path={currentHandle}; max_depth={MaxBlockNestingDepth}"));
                return;
            }

            BlockTableRecord definition;
            try
            {
                definition = (BlockTableRecord)tr.GetObject(
                    br.BlockTableRecord, OpenMode.ForRead);
            }
            catch (Exception ex)
            {
                result.Findings.Add(TraversalFinding(
                    "לא ניתן לפתוח הגדרת Block/XREF עבור הקרנת החתך",
                    $"handle_path={currentHandle}; {ex.Message}"));
                return;
            }

            var isXref = definition.IsFromExternalReference || definition.IsFromOverlayReference;
            if (XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isXref, definition.IsFromOverlayReference,
                    isInsideExternalReference, hasExternalOverlayAncestor))
                return;

            if (!definitionStack.Add(definition.ObjectId))
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.XrefCycle,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "זוהה מחזור בשרשרת Block/XREF של מקורות ההקרנה — PLAN חסום",
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
                    if (definition.IsUnloaded || !definition.IsResolved)
                    {
                        result.Findings.Add(new DeliveryFinding
                        {
                            Code = SectionFindingCodes.XrefTraversalUnresolved,
                            Domain = SectionPlanLogic.Domain,
                            Severity = FindingSeverity.Error,
                            Title = XrefAvailabilityText.Title(definition.Name, definition.IsUnloaded,
                                "לא ניתן להוכיח כיסוי הקרנה מלא"),
                            Message = XrefAvailabilityText.Message(definition.Name, definition.IsUnloaded, definition.PathName),
                            RecommendedAction = XrefAvailabilityText.Action(definition.IsUnloaded),
                        });
                        return;
                    }

                    var snapshot = xrefSnapshots.Validate(definition, parentSource.Path);
                    if (!snapshot.IsFresh)
                    {
                        result.Findings.Add(new DeliveryFinding
                        {
                            Code = SectionFindingCodes.ExternalSourceChanged,
                            Domain = SectionPlanLogic.Domain,
                            Severity = FindingSeverity.Error,
                            Title = $"העותק הטעון של ה-XREF '{Bidi.Ltr(definition.Name)}' אינו מוכח כזהה לקובץ — הקרנת החתך חסומה",
                            Message = $"path={snapshot.ResolvedPath ?? definition.PathName}; " +
                                      $"reason={snapshot.Failure}; {snapshot.Detail}",
                            RecommendedAction = "יש לבצע Reload ל-XREF ולוודא שקובץ המקור יציב, ואז להריץ PLAN מחדש.",
                        });
                        return;
                    }
                    source = new SourceContext(snapshot.ResolvedPath, snapshot.Sha256,
                        AppendChain(parentSource.XrefChain, definition.Name), IsExternal: true);
                }

                Matrix3d xform;
                try { xform = ComposeTransform(outerTransform, br); }
                catch (Exception ex)
                {
                    result.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.XrefTransformInvalid,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "טרנספורמציית Block/XREF אינה ניתנת להרכבה עבור הקרנת החתך",
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
                        result.Findings.Add(TraversalFinding(
                            "ישות בתוך Block/XREF אינה ניתנת לקריאה עבור הקרנת החתך",
                            $"definition={definition.Name}; handle_path={currentHandle}; {ex.Message}"));
                        continue;
                    }

                    if (obj is BlockReference nested)
                    {
                        TraverseReference(tr, nested, source, currentHandle,
                            depth + 1, xform, definitionStack,
                            utilityRules, markRules, result, xrefSnapshots,
                            isInsideExternalReference: isInsideExternalReference || isXref,
                            hasExternalOverlayAncestor: hasExternalOverlayAncestor ||
                                (isXref && definition.IsFromOverlayReference));
                    }
                    else if (obj is Entity entity)
                    {
                        Consider(entity, source, xform,
                            currentHandle + "/" + entity.Handle,
                            utilityRules, markRules, result);
                    }
                }
            }
            finally
            {
                definitionStack.Remove(definition.ObjectId);
            }
        }

        private static void Consider(
            Entity entity,
            SourceContext source,
            Matrix3d transform,
            string handlePath,
            List<ProjectionRuleConfig> utilityRules,
            List<ProjectionRuleConfig> markRules,
            CollectResult result)
        {
            var mark = Classify(entity.Layer, source.XrefChain, markRules);
            var isExplicitMark = mark != null && markRules.Count > 0 &&
                mark.Kind is "curb" or "lane" or "sidewalk" or "island" or
                    "bike" or "garden" or "parking" or "shoulder" or
                    "strip" or "row" or "mark";
            var selected = isExplicitMark
                ? mark
                : Classify(entity.Layer, source.XrefChain, utilityRules);
            if (selected == null) return;

            // A Leader is drafting annotation even when it inherits a utility layer.
            // Treating its arrow/landing geometry as a utility centreline would either
            // invent a crossing or block the entire drawing as unsupported geometry.
            if (entity is Leader) return;

            // Only an already recognized semantic area enters this region
            // path. Its loops are area evidence, not independent dimension marks;
            // interpreting every inner loop as filled would erase source holes.
            if (entity is Hatch hatch && isExplicitMark &&
                SectionHatchSpanLabelService.IsSupportedSemanticRule(selected))
            {
                try
                {
                    var region = SectionHatchRegionReader.Read(
                        hatch, transform, entity.Layer, source.XrefChain, handlePath,
                        source.Path, source.Hash, selected);
                    result.PlanRegions.Add(region);
                    if (region.Deferred is { } partial)
                    {
                        foreach (var loop in partial.Loops.Where(loop => loop.Failure != null))
                        {
                            var finding = UnsupportedGeometryFinding(
                                entity, source, transform, handlePath, "plan-region", selected,
                                $"source-region=partial; loop={loop.Index}; {loop.Failure}", loop.Bounds);
                            result.Findings.Add(finding);
                            // b7: eligibility of the separate local cut proof, never a repair.
                            if (partial.LocalCut != null && partial.Loops.Count == 1 && partial.GlobalFailure == null)
                            {
                                result.LocalCutSources[finding.FindingId] = partial.LocalCut;
                                finding.EvidenceRefs.Add($"{SectionHatchLocalCut.Method}:eligible:{partial.LocalCut.CanonicalSha256}");
                            }
                            else if (partial.LocalCutRefusal != null)
                                finding.EvidenceRefs.Add($"{SectionHatchLocalCut.Method}:refused:{partial.LocalCutRefusal}");
                        }
                        if (partial.GlobalFailure != null)
                            result.Findings.Add(UnsupportedGeometryFinding(
                                entity, source, transform, handlePath, "plan-region", selected,
                                $"source-region=partial; {partial.GlobalFailure}"));
                    }
                    AddEvidenceForMatchedExternal(source, "plan-region", result);
                }
                catch (Exception ex)
                {
                    result.Findings.Add(UnsupportedGeometryFinding(
                        entity, source, transform, handlePath, "plan-region", selected, ex.Message));
                    AddEvidenceForMatchedExternal(source, "projection-unsupported", result);
                }
                return;
            }

            var selectedIsMark = isExplicitMark ||
                selected.Kind is "curb" or "lane" or "sidewalk" or "island" or
                    "bike" or "garden" or "parking" or "shoulder" or
                    "strip" or "row" or "mark";
            var role = selectedIsMark ? "plan-mark" : "projected-utility";

            if (!TryExtractVerticesWithDiagnostic(entity, transform, out var vertices, out var closed,
                    out var extractionError, out var extractionDiagnostic) || vertices.Count < 2)
            {
                if (IsPotentialProjectionGeometry(entity))
                {
                    result.Findings.Add(UnsupportedGeometryFinding(
                        entity, source, transform, handlePath, role, selected, extractionError));
                    AddEvidenceForMatchedExternal(source, "projection-unsupported", result);
                }
                return;
            }

            if (extractionDiagnostic != null)
            {
                var finding = new DeliveryFinding
                {
                    Code = "SEC-PROJECTION-EXACT-CLOSURE-SENTINEL",
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Info,
                    Title = "נקודת סגירה כפולה נקראה ללא מקטע נוסף",
                    Message = $"role={role}; handle={handlePath}; {extractionDiagnostic}; source geometry was not modified.",
                    ProjectionRole = role,
                };
                finding.SourceRefs.Add(new ProvenanceRef
                {
                    SourceKind = source.IsExternal ? "xref" : "drawing", SourcePathOrUri = source.Path,
                    DrawingChecksum = source.Hash, SourceHandle = handlePath, EntityType = entity.GetType().Name,
                    Layer = entity.Layer, MeasurementMethod = "polyline-exact-zero-closing-segment-ignored",
                    ToolVersion = SectionPlanService.ToolVersion,
                });
                result.Findings.Add(finding);
            }

            var line = new CollectedLine(
                entity.Layer, source.XrefChain, handlePath,
                source.Path, source.Hash, vertices, closed, selected, selected.Kind);
            (selectedIsMark ? result.PlanMarks : result.Utilities).Add(line);
            AddEvidenceForMatchedExternal(source, role, result);
        }

        private static void AddEvidenceForMatchedExternal(
            SourceContext source, string role, CollectResult result)
        {
            if (!source.IsExternal || string.IsNullOrWhiteSpace(source.Path) ||
                string.IsNullOrWhiteSpace(source.Hash)) return;
            ClInstructionReader.AddExternalEvidence(
                result.ExternalSources,
                new SectionExternalSourceEvidence
                {
                    SourcePath = source.Path,
                    SourceName = Path.GetFileName(source.Path),
                    Sha256 = source.Hash,
                    Roles = { role },
                    SourceChain = source.XrefChain,
                },
                result.Findings);
        }

        internal static bool TryExtractVertices(
            Entity entity,
            Matrix3d transform,
            out List<V3> vertices,
            out bool closed,
            out string? error)
            => TryExtractVerticesWithDiagnostic(entity, transform, out vertices, out closed, out error, out _);

        private static bool TryExtractVerticesWithDiagnostic(
            Entity entity,
            Matrix3d transform,
            out List<V3> vertices,
            out bool closed,
            out string? error,
            out string? diagnostic)
        {
            vertices = new List<V3>();
            closed = false;
            error = null;
            diagnostic = null;
            string? readStage = null;
            try
            {
                switch (entity)
                {
                    case Line line:
                    {
                        var a = line.StartPoint.TransformBy(transform);
                        var b = line.EndPoint.TransformBy(transform);
                        vertices.Add(new V3(a.X, a.Y, a.Z));
                        vertices.Add(new V3(b.X, b.Y, b.Z));
                        return true;
                    }
                    case Arc arc:
                    {
                        // Curve parameter units are host-defined; TotalAngle is the
                        // geometric sweep and therefore the honest sagitta input.
                        var sweep = arc.TotalAngle;
                        var segments = ArcTessellationSegmentCount(
                            arc.Radius * MaxLinearScale(transform), sweep, MaxCurveSagittaM);
                        AddCurveSamples(vertices, arc, transform,
                            arc.StartParam, arc.EndParam, segments, includeFirst: true);
                        return true;
                    }
                    case Circle circle:
                    {
                        closed = true;
                        const double sweep = 2.0 * Math.PI;
                        var segments = ArcTessellationSegmentCount(
                            circle.Radius * MaxLinearScale(transform), sweep, MaxCurveSagittaM);
                        AddCurveSamples(vertices, circle, transform,
                            circle.StartParam, circle.EndParam, segments, includeFirst: true,
                            includeLast: false);
                        return true;
                    }
                    case Polyline polyline:
                    {
                        readStage = "Polyline.Closed";
                        var sourceClosed = polyline.Closed;
                        closed = sourceClosed;
                        readStage = "Polyline.NumberOfVertices";
                        var vertexCount = polyline.NumberOfVertices;
                        if (vertexCount < 2)
                        {
                            error = "LWPOLYLINE has fewer than two vertices.";
                            return false;
                        }

                        // Read finite endpoints before any bulge getter. An exactly
                        // repeated terminal point has no outgoing closed span; even
                        // asking the native getter for that unused bulge can throw.
                        readStage = "Polyline.MaxLinearScale";
                        var maxScale = MaxLinearScale(transform);
                        var sourceRead = SectionPolylineSegmentSampling.ReadSource(vertexCount, closed,
                            i =>
                            {
                                readStage = $"Polyline.GetPoint2dAt({i})";
                                var point = polyline.GetPoint2dAt(i);
                                return new P2(point.X, point.Y);
                            },
                            i =>
                            {
                                readStage = $"Polyline.GetBulgeAt({i})";
                                return polyline.GetBulgeAt(i);
                            },
                            i =>
                            {
                                // GetSegmentType returns Coincident even for an invalid index.
                                // This callback is requested only for a numerically bounded,
                                // non-exact closing candidate after all finite endpoints were read.
                                readStage = "Polyline.ValidateCoincidentClosingIndex";
                                if (!sourceClosed || i < 0 || i != vertexCount - 1 ||
                                    polyline.NumberOfVertices != vertexCount || !polyline.Closed)
                                    throw new InvalidOperationException("Polyline closing segment identity changed while reading.");
                                readStage = $"Polyline.GetSegmentType({i})";
                                return polyline.GetSegmentType(i) == SegmentType.Coincident;
                            }, maxScale);
                        var sourceVertices = sourceRead.Vertices;
                        readStage = "Polyline.SampleOcsSegments";
                        var sampled = SectionPolylineSegmentSampling.Sample(sourceVertices, closed,
                            maxScale, MaxCurveSagittaM, nativeCoincidentClosing: sourceRead.NativeCoincidentClosing);
                        readStage = "Polyline.Normal";
                        var normal = polyline.Normal;
                        if (!double.IsFinite(normal.X) || !double.IsFinite(normal.Y) ||
                            !double.IsFinite(normal.Z) || normal.Length <= 1e-12)
                            throw new InvalidOperationException("Polyline OCS normal is non-finite or degenerate.");
                        readStage = "Polyline.PlaneToWorld";
                        var ocsToWorld = transform * Matrix3d.PlaneToWorld(normal);
                        readStage = "Polyline.TransformBasis";
                        var origin = Point3d.Origin.TransformBy(ocsToWorld);
                        var x = Vector3d.XAxis.TransformBy(ocsToWorld);
                        var y = Vector3d.YAxis.TransformBy(ocsToWorld);
                        var z = Vector3d.ZAxis.TransformBy(ocsToWorld);
                        readStage = "Polyline.Elevation";
                        var elevation = polyline.Elevation;
                        readStage = "Polyline.ToWcs";
                        vertices.AddRange(SectionPolylineSegmentSampling.ToWcs(sampled, elevation,
                            new(new(origin.X, origin.Y, origin.Z), new(x.X, x.Y, x.Z),
                                new(y.X, y.Y, y.Z), new(z.X, z.Y, z.Z))));
                        if (sampled.SkippedExactClosingSegmentIndex is { } skipped)
                            diagnostic = $"segment={skipped}; exact finite last/first OCS endpoints coincide; " +
                                "closed zero-length terminal span ignored; preceding arc retained; " +
                                "unused closing bulge was not read";
                        if (sampled.SkippedNativeCoincidentClosingSegmentIndex is { } nativeSkipped)
                            diagnostic = $"segment={nativeSkipped}; native Coincident on validated closed terminal index; " +
                                "finite OCS endpoints differ by at most 4 ULP per coordinate and world gap <= 1e-9m; " +
                                "unused closing bulge was not read; literal last endpoint and preceding live spans retained";
                        readStage = "Polyline.RemoveClosingDuplicate";
                        if (sampled.SkippedNativeCoincidentClosingSegmentIndex == null)
                            RemoveClosingDuplicate(vertices, closed);
                        return true;
                    }
                    case Polyline3d polyline3d:
                    {
                        closed = polyline3d.Closed;
                        foreach (ObjectId vertexId in polyline3d)
                        {
                            if (vertexId.Database == null) continue;
                            using var vertex = (PolylineVertex3d)vertexId.GetObject(OpenMode.ForRead);
                            var point = vertex.Position.TransformBy(transform);
                            vertices.Add(new V3(point.X, point.Y, point.Z));
                        }
                        return true;
                    }
                    case Polyline2d polyline2d:
                    {
                        closed = polyline2d.Closed;
                        var points = new List<Point3d>();
                        var bulges = new List<double>();
                        foreach (ObjectId vertexId in polyline2d)
                        {
                            if (vertexId.Database == null) continue;
                            using var vertex = (Vertex2d)vertexId.GetObject(OpenMode.ForRead);
                            // Vertex2d.Position is OCS. VertexPosition converts it using
                            // the owning polyline's normal/elevation before XREF transform.
                            points.Add(polyline2d.VertexPosition(vertex));
                            bulges.Add(vertex.Bulge);
                        }

                        if (points.Count < 2)
                        {
                            error = "Legacy Polyline2d has fewer than two vertices.";
                            return false;
                        }

                        Add(vertices, points[0].TransformBy(transform));
                        var segmentCount = closed ? points.Count : points.Count - 1;
                        var maxScale = MaxLinearScale(transform);
                        for (var i = 0; i < segmentCount; i++)
                        {
                            var next = (i + 1) % points.Count;
                            var start = points[i];
                            var end = points[next];
                            var bulge = bulges[i];
                            if (Math.Abs(bulge) <= 1e-12)
                            {
                                Add(vertices, end.TransformBy(transform));
                                continue;
                            }

                            var radius = BulgeRadius(start.DistanceTo(end), bulge) * maxScale;
                            var sweep = Math.Abs(BulgeSweepRadians(bulge));
                            var segments = ArcTessellationSegmentCount(
                                radius, sweep, MaxCurveSagittaM);
                            for (var sample = 1; sample <= segments; sample++)
                            {
                                Point3d point;
                                if (sample == segments)
                                {
                                    point = end;
                                }
                                else
                                {
                                    // Polyline2d exposes each vertex-to-vertex span as
                                    // [i,i+1]. Sampling the live Curve keeps OCS/elevation
                                    // and signed bulge semantics inside AutoCAD's kernel.
                                    var parameter = i + (double)sample / segments;
                                    point = polyline2d.GetPointAtParameter(parameter);
                                }
                                Add(vertices, point.TransformBy(transform));
                            }
                        }
                        RemoveClosingDuplicate(vertices, closed);
                        return true;
                    }
                    default:
                        error = $"Entity type {entity.GetType().Name} is not a supported linear/circular projection source.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                vertices.Clear();
                closed = false;
                diagnostic = null;
                error = $"{entity.GetType().Name}: {ex.GetType().Name}: {ex.Message}" +
                    (readStage == null ? string.Empty : $"; read_stage={readStage}");
                return false;
            }
        }

        private static void AddCurveSamples(
            List<V3> vertices,
            Curve curve,
            Matrix3d transform,
            double startParameter,
            double endParameter,
            int segments,
            bool includeFirst,
            bool includeLast = true)
        {
            var first = includeFirst ? 0 : 1;
            var last = includeLast ? segments : segments - 1;
            for (var sample = first; sample <= last; sample++)
            {
                var parameter = startParameter +
                    (endParameter - startParameter) * sample / segments;
                Add(vertices, curve.GetPointAtParameter(parameter).TransformBy(transform));
            }
        }

        private static void Add(List<V3> vertices, Point3d point) =>
            vertices.Add(new V3(point.X, point.Y, point.Z));

        private static void RemoveClosingDuplicate(List<V3> vertices, bool closed)
        {
            if (!closed || vertices.Count < 2) return;
            var first = vertices[0];
            var last = vertices[^1];
            var dx = first.X - last.X;
            var dy = first.Y - last.Y;
            var dz = first.Z - last.Z;
            if (dx * dx + dy * dy + dz * dz <= 1e-18)
                vertices.RemoveAt(vertices.Count - 1);
        }

        internal static double MaxLinearScale(Matrix3d transform)
        {
            var maxColumn = new[]
            {
                Vector3d.XAxis.TransformBy(transform).Length,
                Vector3d.YAxis.TransformBy(transform).Length,
                Vector3d.ZAxis.TransformBy(transform).Length,
            }.Max();
            if (!double.IsFinite(maxColumn) || maxColumn <= 1e-12)
                throw new InvalidOperationException("Projection transform has no finite linear scale.");
            // sqrt(3) bounds the operator norm by the largest column norm even when
            // nested non-uniform inserts introduce shear; over-tessellation is safe.
            return maxColumn * Math.Sqrt(3.0);
        }

        private static bool IsPotentialProjectionGeometry(Entity entity) =>
            entity is Curve ||
            entity.GetType().Name.Contains("FeatureLine", StringComparison.OrdinalIgnoreCase) ||
            entity.GetType().Name.Contains("Proxy", StringComparison.OrdinalIgnoreCase);

        private static DeliveryFinding UnsupportedGeometryFinding(
            Entity entity,
            SourceContext source,
            Matrix3d transform,
            string handlePath,
            string role,
            ProjectionRuleMatch rule,
            string? reason,
            double[]? loopBounds = null)
        {
            var finding = new DeliveryFinding
            {
                Code = SectionFindingCodes.ProjectionGeometryUnsupported,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = "גאומטריה בשכבה תואמת אינה ניתנת לקריאה — החתכים המושפעים חסומים",
                ProjectionRole = role,
                SourceBoundsWcs = loopBounds ?? TryFailureBounds(entity, transform),
                Message = $"role={role}; kind={rule.Kind}; label={rule.Label}; " +
                          $"entity={entity.GetType().Name}; layer={entity.Layer}; " +
                          $"handle={handlePath}; reason={reason ?? "unknown"}",
                RecommendedAction = role == "plan-region"
                    ? "יש לתקן את כל לולאות ה-HATCH או לספק מקור אזור קריא, בלי לאבד חורים פנימיים. " +
                      "אין להחליף אזור חלקי באישור אוטומטי; לאחר תיקון המקור יש להריץ PLAN מחדש."
                    : "יש להמיר את הישות ל-Line, Arc או LWPOLYLINE תקין ללא אובדן גאומטריה, " +
                      "או להסיר את התאמת השכבה המוטעית, ואז להריץ PLAN מחדש.",
            };
            finding.SourceRefs.Add(new ProvenanceRef
            {
                SourceKind = source.IsExternal ? "xref" : "drawing",
                SourcePathOrUri = source.Path,
                DrawingChecksum = source.Hash,
                SourceHandle = handlePath,
                EntityType = entity.GetType().Name,
                Layer = entity.Layer,
                MeasurementMethod = "projection-geometry-fail-closed",
                ToolVersion = SectionPlanService.ToolVersion,
            });
            return finding;
        }

        private static double[]? TryFailureBounds(Entity entity, Matrix3d transform)
        {
            try
            {
                var extents = entity.GeometricExtents;
                var min = extents.MinPoint; var max = extents.MaxPoint;
                if (!double.IsFinite(min.X) || !double.IsFinite(min.Y) || !double.IsFinite(min.Z) ||
                    !double.IsFinite(max.X) || !double.IsFinite(max.Y) || !double.IsFinite(max.Z) ||
                    min.X > max.X || min.Y > max.Y || min.Z > max.Z) return null;
                // Transform all eight corners, not only min/max: rotations, shear
                // and mirrored/nested blocks must never shrink the failure envelope.
                var points = (from x in new[] { extents.MinPoint.X, extents.MaxPoint.X }
                              from y in new[] { extents.MinPoint.Y, extents.MaxPoint.Y }
                              from z in new[] { extents.MinPoint.Z, extents.MaxPoint.Z }
                              select new Point3d(x, y, z).TransformBy(transform)).ToArray();
                if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                                    !double.IsFinite(p.Z))) return null;
                var bounds = new[] { points.Min(p => p.X), points.Min(p => p.Y),
                                     points.Max(p => p.X), points.Max(p => p.Y) };
                return SectionProjectionFailureScope.HasUsableBounds(bounds) ? bounds : null;
            }
            catch (Exception)
            {
                // The finding itself is retained. No trustworthy native envelope
                // means a global failure, never a skipped entity or an empty area.
                return null;
            }
        }

        private static List<ProjectionRuleConfig> ToConfigs(
            IEnumerable<ProjectProfile.SectionsProfile.ProjectionProfile.ProjectionRule> rules) =>
            rules.Select(rule => new ProjectionRuleConfig(
                rule.LayerPattern, rule.XrefPattern, rule.Label,
                rule.Kind, rule.ColorIndex) { Origin = rule.Origin }).ToList();

        private static string AppendChain(string? parent, string child) =>
            string.IsNullOrWhiteSpace(parent) ? child : parent + " > " + child;

        private static Matrix3d ComposeTransform(
            Matrix3d outerTransform, BlockReference br)
        {
            var xform = outerTransform * br.BlockTransform;
            return xform;
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
    }
}
