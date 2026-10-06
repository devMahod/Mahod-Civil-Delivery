using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// First-run project setup scan: reports what the drawing actually contains so
    /// the engineer can configure an empty ProjectProfile from real evidence.
    ///
    /// A product that returns "0 sections" because a YAML field is empty is broken
    /// UX, not correct engineering. This scanner makes the empty-profile case
    /// actionable — while still never choosing anything on the engineer's behalf.
    /// </summary>
    public sealed class ProjectSetupScanner
    {
        /// <summary>Entities per layer actually tested for alignment crossings (cost bound on huge drawings).</summary>
        public int CrossingProbeSampleSize { get; init; } = 40;

        /// <summary>Layers with fewer two-point entities than this are not offered as CL candidates.</summary>
        public int MinTwoPointEntities { get; init; } = 1;

        internal const string NoAlignmentGuidance =
            "פתח את מודל Civil שמכיל את הצירים, צור בו הפניית נתונים לציר (Data Shortcut), " +
            "או צור ציר Civil מגאומטריה שבחר ואישר מהנדס. ציר שמופיע רק ב־XREF אינו ציר זמין במודל הפעיל. " +
            "בחירת קובץ CL מגדירה מיקומי חתכים; היא אינה יוצרת ציר.";

        public ProjectSetupScan Scan(
            Database db,
            Transaction tr,
            CivilDocument civilDoc,
            ProjectProfile profile,
            StageLog? log = null)
        {
            var scan = new ProjectSetupScan
            {
                RunId = RunManifest.NewRunId("sections", "setup-scan"),
                ProjectProfileId = profile.ProfileId,
                Drawing = db.Filename,
                DrawingFingerprint = db.FingerprintGuid,
                DrawingHash = SafeHash(db.Filename),
                ProfileIsConfigured = profile.Sections.Cl.LayerPatterns.Count > 0,
            };

            // ---------------------------------------------------------- alignments
            var alignments = CollectAlignments(civilDoc, tr, log, scan);

            if (alignments.Count == 0)
            {
                scan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "No alignments in this drawing — section locations cannot be resolved here",
                    Message = NoAlignmentGuidance,
                    ProjectProfileId = profile.ProfileId,
                });
            }

            // ------------------------------------------------- candidate geometry
            // 1.4.1 C3 (984, Codex 12:34): a CL layer chosen from a separate drawing belongs to that drawing only —
            // the same boundary as ClInstructionReader. The host is not walked for CL candidates, so its unloaded
            // background XREFs cannot block the chosen CL; alignments, surfaces and corridors are still read below.
            var hostClScoped = !ClInstructionReader.IsSourceFileScoped(profile.Sections.Cl);
            var geometry = hostClScoped
                ? AccumulateModelSpace(db, tr, log, scan)
                : SkippedHostGeometry(log);
            if (!hostClScoped)
                scan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Info,
                    Title = "קווי ה-CL נלקחים מקובץ ה-CL הנפרד שנבחר בלבד",
                    Message = "השרטוט הפתוח לא נסרק לקווי CL (גם לא ה-XREF שלו). תוואים, משטחים וקורידורים נקראים ממנו כרגיל.",
                    RecommendedAction = "כדי לחזור לקווי CL מתוך השרטוט הפתוח, יש לבחור מחדש את מקור ה-CL.",
                    ProjectProfileId = profile.ProfileId,
                });
            var perLayer = geometry.PerLayer;
            var labels = geometry.Labels;
            var scanned = geometry.Scanned;

            // ------------------------------------------------- crossing evidence
            scan.ClLayerCandidates.AddRange(BuildCandidates(
                perLayer, labels, alignments, log, scan));
            scan.Alignments.AddRange(alignments.Select(a => a.Summary));

            // ------------------------------------------------------------ sources
            log?.Begin("setup.list_sources");
            try
            {
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                    AddSource(tr, id, "surface", scan.Sources, scan);
            }
            catch (Exception ex)
            {
                MarkIncomplete(scan, "surface enumeration", ex);
            }
            try
            {
                foreach (ObjectId id in civilDoc.CorridorCollection)
                    AddSource(tr, id, "corridor", scan.Sources, scan);
            }
            catch (Exception ex)
            {
                MarkIncomplete(scan, "corridor enumeration", ex);
            }
            try
            {
                foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
                    AddSource(tr, id, "pipe-network", scan.Sources, scan);
            }
            catch (Exception ex)
            {
                MarkIncomplete(scan, "pipe-network enumeration", ex);
            }
            log?.End("setup.list_sources", $"n={scan.Sources.Count}");

            if (scan.ClLayerCandidates.Count == 0)
            {
                scan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.ClSourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "אף שכבה בשרטוט לא נראית כמקור למיקומי חתכים (CL)",
                    Message = $"Scanned {scanned} entities. Expected two-point lines/polylines crossing an alignment. " +
                              "\u05d0\u05dd \u05e7\u05d5\u05d5\u05d9 \u05d4\u05d7\u05ea\u05da \u05e0\u05de\u05e6\u05d0\u05d9\u05dd \u05d1\u05e9\u05e8\u05d8\u05d5\u05d8 \u05e0\u05e4\u05e8\u05d3 - " +
                              "\u05d9\u05e9 \u05dc\u05d1\u05d7\u05d5\u05e8 \u05d0\u05d5\u05ea\u05d5 \u05d1\u05db\u05e4\u05ea\u05d5\u05e8 \"\u05d1\u05d7\u05e8 \u05e7\u05d5\u05d1\u05e5 CL\" \u05e9\u05d1\u05e4\u05d0\u05e0\u05dc.",
                    ProjectProfileId = profile.ProfileId,
                });
            }

            return scan;
        }

        /// <summary>
        /// The same CL-layer evidence scan, run against a CL drawing that is NOT open and
        /// NOT attached: geometry comes from the picked file (read-only side database),
        /// while the crossing evidence is probed against the alignments of the model the
        /// engineer has open. That pairing is the whole point - a section line means
        /// something only where it crosses a design axis.
        /// </summary>
        public ExternalClScan ScanClFile(
            string clPath,
            CivilDocument hostCivilDoc,
            Transaction hostTr,
            StageLog? log = null)
        {
            if (string.IsNullOrWhiteSpace(clPath) || !File.Exists(clPath))
                throw new FileNotFoundException("CL drawing not found", clPath);

            var fullPath = Path.GetFullPath(clPath);
            var sourceHash = ArtifactHash.Sha256OfFile(fullPath);
            var sourceLastWriteUtc = File.GetLastWriteTimeUtc(fullPath);

            var alignments = CollectAlignments(hostCivilDoc, hostTr, log);

            log?.Begin("setup.scan_cl_file", clPath);
            // Survives the CL drawing being open in another tab or another session —
            // picking a file that is open in the editor is the NORMAL case, not an error.
            using var sideDwg = SideDwg.OpenReadOnly(clPath);
            var side = sideDwg.Db;
            log?.Info($"setup.scan_cl_file source={sideDwg.Source}");

            GeometryScan geometry;
            List<ClLayerCandidate> candidates;
            using (var sideTr = side.TransactionManager.StartTransaction())
            {
                geometry = AccumulateModelSpace(side, sideTr, log);
                candidates = BuildCandidates(
                    geometry.PerLayer, geometry.Labels, alignments, log);
                // A side-database discovery pass is read-only. Publish nothing and
                // return no candidate until Abort and Dispose both succeed.
                sideTr.Abort();
            }
            var endHash = ArtifactHash.Sha256OfFile(fullPath);
            var endLastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            if (!string.Equals(sourceHash, endHash, StringComparison.OrdinalIgnoreCase) ||
                sourceLastWriteUtc != endLastWriteUtc)
                throw new InvalidOperationException(
                    "The external CL drawing changed while it was being scanned; select it again.");
            log?.End("setup.scan_cl_file",
                $"entities={geometry.Scanned} layers={geometry.PerLayer.Count} candidates={candidates.Count}");

            var result = new ExternalClScan
            {
                Path = fullPath,
                SourceHash = sourceHash,
                SourceLastWriteUtc = sourceLastWriteUtc,
                ScanComplete = true,
                Entities = geometry.Scanned,
                LayersScanned = geometry.PerLayer.Count,
                AlignmentsProbed = alignments.Count,
            };
            foreach (var (path, hash) in geometry.DiscoverySourceHashes)
                result.DiscoverySourceHashes.Add(path, hash);
            result.Candidates.AddRange(candidates);
            result.RequireSourceUnchanged();
            return result;
        }

        /// <summary>What a picked CL drawing turned out to contain.</summary>
        public sealed class ExternalClScan
        {
            public required string Path { get; init; }
            public required string SourceHash { get; init; }
            public DateTime SourceLastWriteUtc { get; init; }
            public bool ScanComplete { get; init; }
            public int Entities { get; init; }
            public int LayersScanned { get; init; }
            public int AlignmentsProbed { get; init; }
            public List<ClLayerCandidate> Candidates { get; } = new();
            // Dependencies read while discovering CL geometry, not additional CL inputs.
            public Dictionary<string, string> DiscoverySourceHashes { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public void RequireSourceUnchanged()
            {
                if (!ScanComplete || !File.Exists(Path) ||
                    File.GetLastWriteTimeUtc(Path) != SourceLastWriteUtc ||
                    !string.Equals(ArtifactHash.Sha256OfFile(Path), SourceHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The external CL scan is incomplete or its DWG changed; select and scan it again.");
                RequireDiscoverySourcesUnchanged(DiscoverySourceHashes);
            }
        }

        // ------------------------------------------------------------- internals

        private List<(CivilDb.Alignment Alignment, AlignmentCandidateSummary Summary)> CollectAlignments(
            CivilDocument civilDoc, Transaction tr, StageLog? log,
            ProjectSetupScan? scan = null)
        {
            log?.Begin("setup.list_alignments");
            var alignments = new List<(CivilDb.Alignment Alignment, AlignmentCandidateSummary Summary)>();
            ObjectIdCollection ids;
            try { ids = civilDoc.GetAlignmentIds(); }
            catch (Exception ex)
            {
                if (scan == null) throw;
                MarkIncomplete(scan, "alignment enumeration", ex);
                return alignments;
            }
            foreach (ObjectId id in ids)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Alignment a) continue;
                    var geometry = AlignmentGeometryGuard.Read(a);
                    if (geometry.IsEmpty)
                    {
                        // 984 (06.10): an empty alignment made every crossing probe throw eDegenerateGeometry.
                        log?.Info($"setup.list_alignments skipped empty alignment: {a.Name} ({geometry.Text})");
                        if (scan != null) AlignmentGeometryGuard.Report(scan.Findings, a.Name, scan.ProjectProfileId, geometry);
                        continue;
                    }
                    alignments.Add((a, new AlignmentCandidateSummary
                    {
                        Name = a.Name,
                        Handle = a.Handle.ToString(),
                        StartStation = Math.Round(a.StartingStation, 3),
                        EndStation = Math.Round(a.EndingStation, 3),
                        Length = Math.Round(a.Length, 3),
                    }));
                }
                catch (Exception ex)
                {
                    if (scan == null) throw;
                    MarkIncomplete(scan, $"alignment open {id}", ex);
                }
            }
            log?.End("setup.list_alignments", $"n={alignments.Count}");
            return alignments;
        }

        private sealed class GeometryScan
        {
            public required Dictionary<string, LayerAccumulator> PerLayer { get; init; }
            public required List<(Pt2 Pos, string Text, string Layer)> Labels { get; init; }
            public int Scanned { get; init; }
            public required Dictionary<string, string> DiscoverySourceHashes { get; init; }
        }

        private static GeometryScan SkippedHostGeometry(StageLog? log)
        {
            log?.Info("setup.scan_geometry skipped: cl layer_scope=source-file");
            return new GeometryScan
            {
                PerLayer = new Dictionary<string, LayerAccumulator>(StringComparer.OrdinalIgnoreCase),
                Labels = new List<(Pt2 Pos, string Text, string Layer)>(),
                Scanned = 0,
                DiscoverySourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
        }

        private GeometryScan AccumulateModelSpace(
            Database db, Transaction tr, StageLog? log,
            ProjectSetupScan? scan = null)
        {
            log?.Begin("setup.scan_geometry");
            var perLayer = new Dictionary<string, LayerAccumulator>(StringComparer.OrdinalIgnoreCase);
            var labels = new List<(Pt2 Pos, string Text, string Layer)>();

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            int scanned = 0;
            var snapshots = new SectionXrefSnapshotGuard.Cache();
            var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<(string Stage, Exception Error)>();

            DiscoveryNode<ObjectId, DiscoveryFrame> Describe(ObjectId id)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                if (++scanned % 20000 == 0) log?.Info($"setup.scan progress: {scanned}");
                if (obj is not BlockReference reference)
                    return new DiscoveryNode<ObjectId, DiscoveryFrame>
                    {
                        Handle = obj.Handle.ToString(),
                        Visit = (frame, inXref) =>
                        {
                            if (obj is not Entity entity) return;
                            CollectLabel(entity, frame.Transform,
                                ClEffectiveLayer.Resolve(entity.Layer, frame.InheritedLayer), labels);
                            Accumulate(entity, tr, frame.Transform, inXref, perLayer,
                                ClEffectiveLayer.Resolve(entity.Layer, frame.InheritedLayer));
                        },
                    };

                var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                var isExternal = definition.IsFromExternalReference || definition.IsFromOverlayReference;
                return new DiscoveryNode<ObjectId, DiscoveryFrame>
                {
                    Handle = reference.Handle.ToString(),
                    IsReference = true,
                    Definition = definition.ObjectId,
                    // The reference's own layer; composition resolves it against the outer frame.
                    Transform = new DiscoveryFrame(reference.BlockTransform, reference.Layer),
                    IsExternal = isExternal,
                    IsOverlay = definition.IsFromOverlayReference,
                    IsUnavailable = isExternal && (definition.IsUnloaded || !definition.IsResolved),
                    Name = definition.Name,
                    ReadChildren = () => definition.Cast<ObjectId>(),
                    ResolveSource = parentPath =>
                    {
                        var snapshot = snapshots.Validate(definition, parentPath);
                        if (!snapshot.IsFresh || string.IsNullOrWhiteSpace(snapshot.ResolvedPath))
                            throw new InvalidOperationException(
                                $"XREF '{definition.Name}': {snapshot.Failure}; {snapshot.Detail}");
                        if (sourceHashes.TryGetValue(snapshot.ResolvedPath, out var priorHash) &&
                            !string.Equals(priorHash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Conflicting XREF identity: {snapshot.ResolvedPath}");
                        sourceHashes[snapshot.ResolvedPath] = snapshot.Sha256!;
                        return snapshot.ResolvedPath;
                    },
                };
            }

            WalkDiscovery(ms.Cast<ObjectId>(), Describe, new DiscoveryFrame(Matrix3d.Identity, null),
                (outer, inner) => new DiscoveryFrame(outer.Transform * inner.Transform,
                    ClEffectiveLayer.Resolve(inner.InheritedLayer, outer.InheritedLayer)),
                frame => IsUsableTransform(frame.Transform), db.Filename,
                (stage, error) => failures.Add((stage, error)));
            try { RequireDiscoverySourcesUnchanged(sourceHashes); }
            catch (Exception ex) { failures.Add(("XREF identity after discovery", ex)); }
            foreach (var (stage, error) in failures)
            {
                if (scan == null)
                    throw new InvalidOperationException($"Incomplete CL discovery: {stage}", error);
                MarkIncomplete(scan, stage, error);
            }
            if (scan != null)
                foreach (var (path, hash) in sourceHashes)
                    scan.DiscoverySourceHashes[path] = hash;
            log?.End("setup.scan_geometry", $"entities={scanned} layers={perLayer.Count}");

            return new GeometryScan { PerLayer = perLayer, Labels = labels, Scanned = scanned,
                DiscoverySourceHashes = sourceHashes };
        }

        private List<ClLayerCandidate> BuildCandidates(
            Dictionary<string, LayerAccumulator> perLayer,
            List<(Pt2 Pos, string Text, string Layer)> labels,
            List<(CivilDb.Alignment Alignment, AlignmentCandidateSummary Summary)> alignments,
            StageLog? log,
            ProjectSetupScan? scan = null)
        {
            log?.Begin("setup.probe_crossings", $"layers={perLayer.Count}");
            var candidates = new List<ClLayerCandidate>();
            foreach (var (layer, acc) in perLayer)
            {
                if (!OffersCandidate(acc.TwoPointSegments.Count, MinTwoPointEntities)) continue;

                var candidate = new ClLayerCandidate
                {
                    Layer = layer,
                    // The caller of a host scan passes its scan; a separate CL file is scanned without one.
                    FromHost = scan != null,
                    LineCount = acc.LineCount,
                    PolylineCount = acc.PolylineCount,
                    TwoPointCount = acc.TwoPointSegments.Count,
                    InXref = acc.InXref,
                    MedianLength = Median(acc.TwoPointSegments.Select(s => s.A.DistanceTo(s.B)).ToList()),
                };

                var probe = acc.TwoPointSegments.Take(CrossingProbeSampleSize).ToList();
                foreach (var (alignment, summary) in alignments)
                {
                    int hits = 0;
                    foreach (var seg in probe)
                    {
                        try
                        {
                            if (CrossesAlignment(alignment, seg)) hits++;
                        }
                        catch (Exception ex)
                        {
                            // A separate CL drawing has no scan to mark: name the alignment and layer instead of
                            // surfacing a bare Civil error status such as eDegenerateGeometry.
                            if (scan == null)
                                throw new InvalidOperationException(
                                    $"בדיקת החציה של שכבה {Bidi.Ltr(layer)} מול התוואי {Bidi.Ltr(alignment.Name)} " +
                                    $"נכשלה ({ex.Message}). לא נשמר דבר; יש לבדוק את התוואי ב-Civil ולבחור שוב.", ex);
                            MarkIncomplete(scan,
                                $"crossing probe {alignment.Name}/{layer}", ex);
                        }
                    }
                    if (hits > 0)
                    {
                        candidate.CrossingCount += hits;
                        candidate.AlignmentsCrossed.Add(alignment.Name);
                        summary.CrossedByCandidateLayers.Add(layer);
                    }
                }

                ScoreCandidate(candidate, probe.Count);
                AttachSampleLabels(candidate, acc, labels);
                candidates.Add(candidate);
            }
            candidates.Sort((x, y) => y.EvidenceScore.CompareTo(x.EvidenceScore));
            log?.End("setup.probe_crossings", $"candidates={candidates.Count}");
            return candidates;
        }

        private sealed class LayerAccumulator
        {
            public int LineCount;
            public int PolylineCount;
            public bool InXref;
            public List<(Pt2 A, Pt2 B)> TwoPointSegments { get; } = new();
        }

        internal static bool OffersCandidate(int twoPointCount, int minimum = 1) =>
            twoPointCount >= Math.Max(1, minimum);

        // The real discovery traversal is separated from native object opening, so
        // ordinary/nested blocks, transform order, source paths and refusal can be
        // exercised without substituting a different test-only traversal algorithm.
        internal sealed class DiscoveryNode<TId, TTransform> where TId : notnull
        {
            public required string Handle { get; init; }
            public bool IsReference { get; init; }
            public TId Definition { get; init; } = default!;
            public TTransform Transform { get; init; } = default!;
            public bool IsExternal { get; init; }
            public bool IsOverlay { get; init; }
            public bool IsUnavailable { get; init; }
            /// <summary>The block/XREF definition name, for a readable finding (null for plain entities).</summary>
            public string? Name { get; init; }
            public Func<IEnumerable<TId>>? ReadChildren { get; init; }
            public Func<string?, string>? ResolveSource { get; init; }
            public Action<TTransform, bool>? Visit { get; init; }
        }

        internal static void WalkDiscovery<TId, TTransform>(
            IEnumerable<TId> roots,
            Func<TId, DiscoveryNode<TId, TTransform>> describe,
            TTransform identity,
            Func<TTransform, TTransform, TTransform> compose,
            Func<TTransform, bool> isUsableTransform,
            string? sourcePath,
            Action<string, Exception> incomplete) where TId : notnull
        {
            var definitionStack = new HashSet<TId>();

            void Walk(IEnumerable<TId> ids, TTransform outer, string? parentSource,
                string parentHandle, int depth, bool insideExternal, bool overlayAncestor)
            {
                try
                {
                    foreach (var id in ids)
                    {
                        var handlePath = string.IsNullOrEmpty(parentHandle)
                            ? id.ToString()! : parentHandle + "/" + id;
                        try
                        {
                            var node = describe(id);
                            handlePath = string.IsNullOrEmpty(parentHandle)
                                ? node.Handle : parentHandle + "/" + node.Handle;
                            if (!node.IsReference)
                            {
                                node.Visit?.Invoke(outer, insideExternal);
                                continue;
                            }
                            if (depth > ClInstructionReader.MaxBlockNestingDepth)
                                throw new InvalidOperationException(
                                    $"Block nesting exceeds {ClInstructionReader.MaxBlockNestingDepth}.");
                            // The same visibility rule as ClInstructionReader: an
                            // overlay nested inside an XREF is not host geometry.
                            if (XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                                    node.IsExternal, node.IsOverlay, insideExternal, overlayAncestor))
                                continue;
                            if (!definitionStack.Add(node.Definition))
                                throw new InvalidOperationException("Cyclic block reference.");
                            try
                            {
                                var currentSource = parentSource;
                                if (node.IsExternal)
                                {
                                    if (node.IsUnavailable)
                                        throw new XrefUnavailableException(node.Name ?? node.Handle);
                                    currentSource = node.ResolveSource?.Invoke(parentSource);
                                    if (string.IsNullOrWhiteSpace(currentSource))
                                        throw new InvalidOperationException("XREF source identity is unavailable.");
                                }
                                var transform = compose(outer, node.Transform);
                                if (!isUsableTransform(transform))
                                    throw new InvalidOperationException("Non-finite or degenerate block transform.");
                                if (node.ReadChildren == null)
                                    throw new InvalidOperationException("Block contents are unavailable.");
                                Walk(node.ReadChildren(), transform, currentSource, handlePath, depth + 1,
                                    insideExternal || node.IsExternal,
                                    overlayAncestor || (node.IsExternal && node.IsOverlay));
                            }
                            finally { definitionStack.Remove(node.Definition); }
                        }
                        catch (Exception ex) { incomplete($"discovery {parentSource}; handle_path={handlePath}", ex); }
                    }
                }
                catch (Exception ex)
                {
                    incomplete($"discovery enumeration {parentSource}; handle_path={parentHandle}", ex);
                }
            }

            if (!isUsableTransform(identity))
            {
                incomplete("discovery root transform", new InvalidOperationException("Invalid root transform."));
                return;
            }
            Walk(roots, identity, sourcePath, string.Empty, 0, false, false);
        }

        private static bool IsUsableTransform(Matrix3d matrix)
            => IsUsableTransformValues(matrix.ToArray());

        internal static bool IsUsableTransformValues(IReadOnlyList<double> values)
        {
            if (values.Count != 16 || values.Any(value => !double.IsFinite(value))) return false;
            var determinant =
                values[0] * (values[5] * values[10] - values[6] * values[9]) -
                values[1] * (values[4] * values[10] - values[6] * values[8]) +
                values[2] * (values[4] * values[9] - values[5] * values[8]);
            return double.IsFinite(determinant) && Math.Abs(determinant) >= 1e-12;
        }

        private static void RequireDiscoverySourcesUnchanged(IReadOnlyDictionary<string, string> hashes)
        {
            foreach (var (path, expected) in hashes)
                if (string.IsNullOrWhiteSpace(expected) || !File.Exists(path) ||
                    !string.Equals(ArtifactHash.Sha256OfFile(path), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Discovery XREF changed or became unavailable: {path}");
        }

        private static void CollectLabel(
            Entity ent, Matrix3d xform, string effectiveLayer, List<(Pt2, string, string)> labels)
        {
            switch (ent)
            {
                case DBText t when !string.IsNullOrWhiteSpace(t.TextString):
                    labels.Add((Transform(xform, t.Position), t.TextString.Trim(), effectiveLayer));
                    break;
                case MText mt when !string.IsNullOrWhiteSpace(mt.Contents):
                    labels.Add((Transform(xform, mt.Location), mt.Text.Trim(), effectiveLayer));
                    break;
            }
        }

        /// <summary>
        /// A discovery step: the composed transform and the effective layer that layer-0 content of the current
        /// reference is drawn on (null in model space). See <see cref="ClEffectiveLayer"/>.
        /// </summary>
        internal readonly record struct DiscoveryFrame(Matrix3d Transform, string? InheritedLayer);

        private static void Accumulate(
            Entity ent, Transaction transaction, Matrix3d xform, bool inXref, Dictionary<string, LayerAccumulator> perLayer,
            string effectiveLayer)
        {
            if (ent is not (Line or Polyline or Polyline2d)) return;
            SectionClGeometryContract.Geometry geometry;
            try { geometry = SectionClGeometryContract.Read(ent, transaction, xform); }
            catch (NotSupportedException) { return; } // Fitted geometry is not a CL discovery candidate.
            var shape = SectionClGeometryContract.Analyze(geometry);
            if (shape.Kind is not (SectionClCandidateLogic.Decision.Straight or SectionClCandidateLogic.Decision.CollapseToChord)) return;

            if (!perLayer.TryGetValue(effectiveLayer, out var acc))
                perLayer[effectiveLayer] = acc = new LayerAccumulator();

            if (ent is Line) acc.LineCount++;
            else acc.PolylineCount++;
            if (inXref) acc.InXref = true;
            acc.TwoPointSegments.Add((geometry.WorldVertices[0], geometry.WorldVertices[^1]));
        }

        private static bool CrossesAlignment(CivilDb.Alignment alignment, (Pt2 A, Pt2 B) seg)
        {
            using var probe = new Line(
                new Point3d(seg.A.X, seg.A.Y, 0),
                new Point3d(seg.B.X, seg.B.Y, 0));
            var points = new Point3dCollection();
            alignment.IntersectWith(probe, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
            return points.Count > 0;
        }

        /// <summary>
        /// Ranking only — it orders what the engineer sees. Crossing evidence
        /// dominates, because a section line that crosses an alignment is the whole
        /// point; layer naming is a weak hint and never decisive.
        /// </summary>
        private static void ScoreCandidate(ClLayerCandidate c, int probed)
        {
            int score = 0;

            if (c.CrossingCount > 0 && probed > 0)
            {
                var ratio = (double)c.CrossingCount / probed;
                score += (int)Math.Round(60 * Math.Min(1.0, ratio));
                c.Why.Add($"{c.CrossingCount}/{probed} probed entities cross an alignment");
            }

            if (c.TwoPointCount >= 3)
            {
                score += 10;
                c.Why.Add($"{c.TwoPointCount} two-point entities (section-line shape)");
            }

            if (c.MedianLength is > 5 and < 120)
            {
                score += 10;
                c.Why.Add($"median length {c.MedianLength:F1} m fits a cross-section swath");
            }

            var name = c.Layer.ToUpperInvariant();
            if (name.Contains("CL") || name.Contains("SEC") || name.Contains("CUT") || name.Contains("חתך"))
            {
                score += 10;
                c.Why.Add("layer name hints at section/CL (weak evidence only)");
            }

            if (c.SampleLabels.Count > 0) score += 5;
            if (c.AlignmentsCrossed.Count == 1)
            {
                score += 5;
                c.Why.Add($"crosses exactly one alignment ({c.AlignmentsCrossed[0]})");
            }

            c.EvidenceScore = Math.Min(100, score);
        }

        private static void AttachSampleLabels(
            ClLayerCandidate candidate, LayerAccumulator acc, List<(Pt2 Pos, string Text, string Layer)> labels)
        {
            foreach (var seg in acc.TwoPointSegments.Take(5))
            {
                var mid = new Pt2((seg.A.X + seg.B.X) / 2, (seg.A.Y + seg.B.Y) / 2);
                var near = SampleLabel(mid, labels, candidate.Layer);
                if (near != null && !candidate.SampleLabels.Contains(near))
                    candidate.SampleLabels.Add(near);
            }
        }

        public const double SampleLabelRadiusM = 20.0;

        /// <summary>
        /// 1.4.1 (b38 live, 984): the picker showed the station elevations 6.53/6.56/6.58 as samples for
        /// HW-ALGN-SEC-NAME. A sample uses the layer and tie rules PLAN uses for section numbers
        /// (<see cref="ClLabelSelection"/>): a text on the line's own effective layer first, else the nearest text with
        /// a digit; two different texts at the same distance give no sample. Not the full PLAN evidence: setup discovery
        /// has no XREF-instance key and no project label_layer_patterns yet. The samples do not change the ranking only
        /// because BuildCandidates scores a candidate before it attaches samples (ScoreCandidate's sample term sees none).
        /// </summary>
        internal static string? SampleLabel(Pt2 mid, IEnumerable<(Pt2 Pos, string Text, string Layer)> labels, string layer) =>
            ClLabelSelection.Choose(
                labels.Select(l => new ClLabelSelection.Label(l.Text, l.Pos.DistanceTo(mid), l.Layer))
                    .Where(l => l.Distance <= SampleLabelRadiusM),
                layer, explicitPatterns: null).Number;

        private static void AddSource(
            Transaction tr, ObjectId id, string kind,
            List<SourceCandidateSummary> sources,
            ProjectSetupScan? scan = null)
        {
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                var name = obj switch
                {
                    CivilDb.Surface s => s.Name,
                    CivilDb.Corridor c => c.Name,
                    CivilDb.Network n => n.Name,
                    _ => null,
                };
                if (name == null)
                {
                    if (scan == null)
                        throw new InvalidOperationException(
                            $"Civil source {id} is not a readable {kind} object.");
                    MarkIncomplete(scan, $"{kind} source type {id}",
                        new InvalidOperationException(
                            "The enumerated source opened as an unexpected object type."));
                    return;
                }
                sources.Add(new SourceCandidateSummary
                {
                    Name = name,
                    Kind = kind,
                    Handle = obj.Handle.ToString(),
                });
            }
            catch (Exception ex)
            {
                if (scan == null) throw;
                MarkIncomplete(scan, $"{kind} source open {id}", ex);
            }
        }

        /// <summary>An unloaded or unresolved XREF met during discovery; reported once per definition.</summary>
        internal sealed class XrefUnavailableException : InvalidOperationException
        {
            public XrefUnavailableException(string xrefName)
                : base($"XREF '{xrefName}' לא טעון או לא פתור.") => XrefName = xrefName;

            public string XrefName { get; }
        }

        private static void MarkIncomplete(
            ProjectSetupScan scan, string stage, Exception error)
        {
            scan.ScanComplete = false;
            if (error is XrefUnavailableException xref)
            {
                // One finding per XREF definition (984: dozens of insertions of six unloaded backgrounds).
                var title = $"XREF לא טעון או לא פתור: {Bidi.Ltr(xref.XrefName)} — מקורות החתך בו לא נבדקו";
                if (scan.Findings.Any(f => f.Code == SectionFindingCodes.SetupScanIncomplete &&
                        string.Equals(f.Title, title, StringComparison.Ordinal)))
                    return;
                scan.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SetupScanIncomplete,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = title,
                    Message = "קווי CL, תוויות ומקורות שנמצאים רק ב-XREF הזה לא נקראו, ולכן אי אפשר לקבוע שהם חסרים.",
                    RecommendedAction = "אם קווי ה-CL נמצאים בו — יש לטעון אותו ב-Civil (XREF ‹Reload›) ולסרוק שוב. " +
                        "אם קווי ה-CL נמצאים בשרטוט נפרד — יש לבחור אותו ב'בחר קובץ CL נפרד', ואז ה-XREF הזה לא נדרש לחתכים.",
                    ProjectProfileId = scan.ProjectProfileId,
                });
                return;
            }
            var message = $"{stage}: {error.GetType().Name}: {error.Message}";
            if (scan.Findings.Any(finding =>
                    finding.Code == SectionFindingCodes.SetupScanIncomplete &&
                    string.Equals(finding.Message, message, StringComparison.Ordinal)))
                return;
            scan.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.SetupScanIncomplete,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = "Project setup discovery was incomplete; missing systems/candidates cannot be treated as absent",
                Message = message,
                RecommendedAction =
                    "Resolve the Civil object/XREF read failure and run setup scan again before saving configuration.",
                ProjectProfileId = scan.ProjectProfileId,
            });
        }

        private static Pt2 Transform(Matrix3d m, Point3d p)
        {
            var t = p.TransformBy(m);
            if (!double.IsFinite(t.X) || !double.IsFinite(t.Y) || !double.IsFinite(t.Z))
                throw new InvalidOperationException("Discovery geometry has non-finite world coordinates.");
            return new Pt2(t.X, t.Y);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            var mid = values.Count / 2;
            return values.Count % 2 == 1
                ? Math.Round(values[mid], 3)
                : Math.Round((values[mid - 1] + values[mid]) / 2, 3);
        }

        private static string SafeHash(string? path)
        {
            try
            {
                return string.IsNullOrEmpty(path) || !File.Exists(path)
                    ? string.Empty : ArtifactHash.Sha256OfFile(path);
            }
            catch { return string.Empty; }
        }
    }
}
