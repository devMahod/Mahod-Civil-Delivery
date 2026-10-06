using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
using CivilDb = Autodesk.Civil.DatabaseServices;
using AcadRegion = Autodesk.AutoCAD.DatabaseServices.Region;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// The Civil Quantity Adapter (plan §8): deterministic measurement of drawing
    /// geometry into Neutral Quantity Records according to the profile's
    /// quantity-source rules. Measures; never prices, never maps beyond the rule's
    /// candidate code. Drawing geometry is normalized from an explicitly supported
    /// INSUNITS or an explicit exact-host physical-unit declaration into SI;
    /// undeclared Unitless drawings remain globally blocked from priced export.
    /// </summary>
    public sealed class CivilQuantityExtractionService
    {
        internal const int MaxXrefNestingDepth = 32;

        private sealed record DiscoverySource(
            string DrawingPath,
            string DrawingHash,
            string DrawingName,
            string? XrefChain,
            bool IsExternal,
            Database? VerifiedLoadedDatabase = null,
            string? LoadedLayerPrefix = null,
            string ScopeKey = EstimateSourceSelectionPolicy.HostKey);

        internal sealed record XrefOverlayRecoveryEvidence(
            string SourcePath, string SourceSha256, string FullHandle,
            string LoadedLayer, string Detail);

        /// <summary>One original-reference decision: the logical (loaded) parent, the SHA this scan proved for it, the
        /// local read copy offered (null = none), the path the cache actually read (null when it stopped before reading),
        /// and the cache decision. Kept after the copies are deleted.</summary>
        internal sealed record XrefOriginalReadReceipt(
            string FullHandle, string LogicalSourcePath, string ExpectedSha256,
            string? OfferedReadCopy, string? CacheReadSourcePath, string Decision, string Detail);

        internal const int MaxOriginalReadReceipts = 10_000;

        public sealed class ExtractionResult
        {
            public List<NeutralQuantityRecord> Records { get; } = new();
            public List<DeliveryFinding> Findings { get; } = new();
            public int ScannedEntities { get; set; }
            public PhysicalDrawingUnitPolicy.Resolution? PhysicalUnits { get; set; }
            public List<EstimateExternalSource> ExternalSources { get; } = new();
            // Raw diagnostics are published once in a separate run artifact, never
            // copied into global findings which can appear on thousands of lines.
            internal HatchAreaFailureDiagnostic.Batch HatchDiagnostics { get; } = new();
            internal XrefBlockExtentsFailureDiagnostic.Batch XrefBlockDiagnostics { get; } = new();
            internal List<XrefOverlayRecoveryEvidence> RecoveredOverlayReferences { get; } = new();
            internal XrefReadSnapshots.Ledger XrefReadCopies { get; } = new();
            internal List<XrefOriginalReadReceipt> XrefOriginalReads { get; } = new();
            // Recognition evidence (mahod-evidence/1) coverage: one bounded summary per
            // scan, never a finding per record. Null when the traversal did not complete.
            internal EvidenceCoverage? EvidenceCoverage { get; set; }

            /// <summary>True when no rules were configured and measurement ran in discovery mode.</summary>
            public bool DiscoveryMode { get; set; }
        }

        internal ExtractionResult Extract(
            Database db, Transaction tr, ProjectProfile profile, string runId,
            string drawingPath, string drawingHash, StageLog? log = null,
            DrawingRevisionTracker.LiveSnapshot? savedHost = null)
        {
            var result = new ExtractionResult();
            var rules = profile.Estimate.QuantitySources.Rules ?? new();
            result.Findings.AddRange(EstimatePreflightPolicy.ValidateSourcePolicies(profile));
            result.Findings.AddRange(EstimateConfigurationPolicy.Validate(profile));
            if (profile.Estimate.SourceSelection is { } selection)
            {
                var inventory = EstimateSourceInventoryService.CaptureForScan(db, tr);
                var problems = EstimateSourceSelectionPolicy.ValidationProblems(selection)
                    .Concat(EstimateSourceSelectionPolicy.MatchProblems(inventory, selection)).ToArray();
                if (problems.Length > 0)
                    throw new InvalidOperationException(string.Join("; ", problems));
                result.Findings.Add(new DeliveryFinding
                {
                    Code = EstimateSourceSelectionPolicy.ExcludedScopeCode, Domain = "estimate", Severity = FindingSeverity.Info,
                    Title = "היקף המקורות שנבחר לסריקה", Message = EstimateSourceSelectionPolicy.ScopeSummary(selection),
                    ProjectProfileId = profile.ProfileId,
                });
            }
            result.PhysicalUnits = HostDrawingUnitService.Resolve(db, profile);
            var unitFinding = HostDrawingUnitService.Finding(result.PhysicalUnits, profile.ProfileId);
            if (unitFinding != null) result.Findings.Add(unitFinding);

            // Directive §3: an unconfigured profile must NOT read as "0 quantities".
            // Discovery mode measures every supported geometry deterministically and
            // reports each group as UNMAPPED / REVIEW_REQUIRED, so the engineer sees
            // real measured work waiting for a catalog decision.
            // Always measure everything; approved rules CLASSIFY what they match and the
            // rest stays visible as UNMAPPED. Switching to a rules-only scan after the
            // first approval made 5,679 measured records disappear (6422, 2026-08-19).
            result.DiscoveryMode = true;
            if (rules.Count == 0)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "No quantity-source rules configured — running in discovery mode",
                    Message = "Every measurable object is reported as UNMAPPED until a catalog mapping is approved. " +
                              "Unknown mapping is not zero quantity.",
                    RecommendedAction = "Approve mappings per layer to turn discovered quantities into estimate lines.",
                    ProjectProfileId = profile.ProfileId,
                });
            }
            // Even an invalid/legacy rules-only profile follows this full discovery
            // path. ValidateSourcePolicies blocks export, while extraction preserves
            // all supported host evidence for remediation instead of hiding it.
            return ExtractDiscovery(db, tr, profile, runId, result, drawingPath, drawingHash, log, savedHost);
        }

        /// <summary>
        /// Discovery mode: measure every supported entity with the measurement its
        /// geometry actually supports (closed polylines → auditable area+perimeter
        /// alternatives, open curves → length, blocks/structures → count). Nothing is
        /// mapped to a catalog code; every record is
        /// REVIEW_REQUIRED with a layer-derived rule key the engineer can approve.
        /// </summary>
        private ExtractionResult ExtractDiscovery(
            Database db, Transaction tr, ProjectProfile profile, string runId,
            ExtractionResult result, string drawingPath, string drawingHash, StageLog? log,
            DrawingRevisionTracker.LiveSnapshot? savedHost)
        {
            var drawingName = string.IsNullOrEmpty(drawingPath) ? "(unsaved)" : Path.GetFileName(drawingPath);
            var drawingUnits = HostDrawingUnitService.Scale(result.PhysicalUnits!);
            var hostSource = new DiscoverySource(
                drawingPath, drawingHash, drawingName, null, false);
            var sourceChoices = profile.Estimate.SourceSelection?.Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
            bool Included(string key) => sourceChoices == null ||
                sourceChoices.TryGetValue(key, out var choice) && choice.Included;

            var sourceHashFinding = DrawingSourceHashFinding(
                drawingName, drawingHash, profile.ProfileId);
            if (sourceHashFinding != null) result.Findings.Add(sourceHashFinding);

            int skippedPresentation = 0;
            var skippedNonQuantityTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var unsupportedEntityTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var unsupportedEntitySources = new List<ProvenanceRef>();
            var recordIds = new HashSet<string>(StringComparer.Ordinal);
            var closedAmbiguities = new ClosedPolylineAmbiguityScopes(result);
            var externalHashes = new Dictionary<string,
                (string Hash, XrefQuantityPolicy.SourceSnapshotIdentity Snapshot)>(
                StringComparer.OrdinalIgnoreCase);
            // Declared BEFORE the cache: `using` disposes in reverse order, so the cache releases its read databases
            // before the copies they read are deleted.
            using var readSnapshots = new XrefReadSnapshots(runId, result.XrefReadCopies, log: log);
            using var originalReferences = new XrefOriginalReferenceCache();
            // One collector (and one nearby-text index) per scan. Reads only; never
            // changes counts, rule keys, quantities or findings.
            var evidence = EstimateScanTrace.Step("evidence.setup", () => new CivilEvidenceCollector(db, drawingUnits, result.PhysicalUnits));
            var hostFrame = CivilEvidenceCollector.Frame.Host(db);
            log?.Begin("estimate.discovery_scan");
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            // Rules 2.8 (BOQ-N1): one definition signature per block definition and scan (read only).
            var definitionSignatures = new Dictionary<ObjectId, string?>();

            void Consider(
                Entity ent,
                DiscoverySource source,
                Matrix3d transform,
                string handlePath,
                CivilEvidenceCollector.Frame frame,
                bool transformMeasurement = false,
                IReadOnlyDictionary<string, string>? extraParameters = null)
            {
                if (!Included(source.ScopeKey)) return;
                EstimateScanTrace.Mark("entity.consider", result.ScannedEntities, handlePath, ent.GetType().Name);
                // Drawing text is recognition context for nearby records (index only).
                // Captured before the presentation exclusion so *LABEL* text still counts;
                // it changes neither ScannedEntities nor the type buckets below.
                if (ent is DBText or MText)
                    evidence.CaptureText(ent, transform, handlePath, source.XrefChain);

                // Civil 3D's own presentation geometry is never a construction quantity.
                // Section-view / profile-view graphics, labels and grids live on layers
                // Civil names for them; measuring them would put fake metres of "kerb"
                // into the estimate (6422 scan returned 223 records from *_SectionView
                // layers, 2026-08-19). Excluded by name, reported in the preflight.
                var sourceLayer = ent.Layer;
                if (IsCivilPresentationLayer(sourceLayer) &&
                    !HasExplicitApprovedRuleForEntity(profile, ent))
                {
                    skippedPresentation++;
                    return;
                }
                result.ScannedEntities++;
                if (result.ScannedEntities % 20000 == 0)
                    log?.Info($"estimate.discovery progress: {result.ScannedEntities}");

                if (!IsNaturallySupported(ent))
                {
                    var type = ent.GetType().Name.ToUpperInvariant();
                    var knownNonQuantity = IsKnownNonQuantityEntityType(type);
                    var bucket = knownNonQuantity
                        ? skippedNonQuantityTypes
                        : unsupportedEntityTypes;
                    bucket[type] = bucket.GetValueOrDefault(type) + 1;
                    if (!knownNonQuantity)
                        unsupportedEntitySources.Add(FailureSource("unsupported-entity-coverage"));
                    return;
                }
                var extentsEvidence = new ExtentsEvidenceScopes(result, profile.ProfileId,
                    source.DrawingPath, source.DrawingHash, handlePath, source.XrefChain);
                EstimateScanTrace.Mark("measurement.begin", result.ScannedEntities, handlePath, ent.GetType().Name);
                var measurements = NaturalMeasurements(ent, drawingUnits,
                    result.Findings, profile.ProfileId, extentsEvidence.Capture, FailureSource,
                    snapshot => result.HatchDiagnostics.Add(FailureSource("hatch-area"), () => snapshot),
                    error =>
                    {
                        if (source.IsExternal && ent is BlockReference failedReference)
                        {
                            var origin = FailureSource("geometric-extents");
                            result.XrefBlockDiagnostics.Add(origin, error, () =>
                                XrefBlockExtentsFailureDiagnostic.CaptureNative(
                                    failedReference, tr, origin, source.VerifiedLoadedDatabase));
                        }
                    }, () => transformMeasurement ? BoundaryRecoveryMaximumScale(transform) : 1.0,
                    () => savedHost == null ? null : new StrictHatchExactRetraceInputFactory.HostInput(
                        runId, savedHost.DrawingPath, savedHost.DrawingHash, savedHost.DatabaseRevision,
                        savedHost.DbMod, savedHost.Failure, result.PhysicalUnits!,
                        // Exact-retrace interpretation is intentionally NOT enabled for
                        // nested blocks/XREFs, even if their transformed bounds look alike.
                        ReferenceEquals(source, hostSource) && !source.IsExternal &&
                        source.DrawingPath == drawingPath && source.DrawingHash == drawingHash &&
                        ent.Database == db && ent.OwnerId == ms.ObjectId &&
                        !transformMeasurement && transform.Equals(Matrix3d.Identity)));
                EstimateScanTrace.Mark("measurement.end", measurements.Count, handlePath, ent.GetType().Name);
                if (measurements.Count == 0) return;

                var cadEvidence = EstimateScanTrace.Step("metadata", () => QuantityCadMetadataReader.Read(ent, tr,
                    new QuantityCadMetadataReader.HostUnits(db, result.PhysicalUnits!)), handle: handlePath, type: ent.GetType().Name);
                foreach (var measurement in measurements)
                    QuantityCadMetadataPolicy.AppendEvidence(measurement, cadEvidence);

                if (transformMeasurement)
                {
                    var transformed = new List<QuantityMeasurement>(measurements.Count);
                    foreach (var measurement in measurements)
                    {
                        var adjusted = TransformNestedMeasurement(
                            ent, measurement, transform, drawingUnits,
                            result.Findings, profile.ProfileId, source.XrefChain, handlePath,
                            source.IsExternal ? "xref-transform" : "block-transform", FailureSource);
                        if (adjusted != null)
                        {
                            extentsEvidence.Transfer(measurement, adjusted);
                            transformed.Add(adjusted);
                        }
                    }
                    measurements = transformed;
                    if (measurements.Count == 0) return;
                }

                // Insertion points are in the parent definition's coordinates, not
                // necessarily host WCS. Publish an explicit SI world point after
                // applying the ancestor transform exactly once. Never reinterpret
                // a local raw point as world coordinates or infer physical units.
                if (ent is BlockReference countReference)
                {
                    string? worldPoint = null;
                    var worldStatus = "unknown-host-units";
                    if (drawingUnits.IsSupported)
                    {
                        try
                        {
                            var world = countReference.Position.TransformBy(transform);
                            var x = drawingUnits.Length(world.X);
                            var y = drawingUnits.Length(world.Y);
                            if (double.IsFinite(x) && double.IsFinite(y))
                            {
                                worldPoint = QuantityGeometryEvidence.FormatPoint(x, y);
                                worldStatus = QuantityGeometryEvidence.StatusComplete;
                            }
                            else worldStatus = QuantityGeometryEvidence.StatusNonFinitePoint;
                        }
                        catch (Exception ex) { worldStatus = "unavailable:" + ex.GetType().Name; }
                    }
                    foreach (var measurement in measurements.Where(m => m.Kind == "count"))
                    {
                        measurement.Parameters["cad_insert_point_world_m_status"] = worldStatus;
                        if (worldPoint != null)
                            measurement.Parameters["cad_insert_point_world_m"] = worldPoint;
                    }
                    // v4.1 (back-to-back racks): the rotation of a block placed directly in the host model space, as
                    // AutoCAD stores it (radians, [0, 2π)). Nested blocks carry none (their frame is not the host's).
                    if (!transformMeasurement && transform.IsEqualTo(Matrix3d.Identity) && double.IsFinite(countReference.Rotation))
                    {
                        var rotation = countReference.Rotation % (2 * Math.PI);
                        if (rotation < 0) rotation += 2 * Math.PI;
                        foreach (var measurement in measurements.Where(m => m.Kind == "count"))
                            measurement.Parameters["cad_rotation_rad"] = rotation.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        // Rules 2.8 (BOQ-N1): the rest of the transform and the definition signature, for approved physical
                        // footprints (geometry keys: never summarized, never part of a decision scope).
                        var scale = countReference.ScaleFactors;
                        var normal = countReference.Normal;
                        if (!definitionSignatures.TryGetValue(countReference.BlockTableRecord, out var signature))
                            definitionSignatures[countReference.BlockTableRecord] = signature = BlockDefinitionSignature(countReference.BlockTableRecord, tr);
                        static string Triple(double a, double b, double c) => string.Join(",",
                            new[] { a, b, c }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                        foreach (var measurement in measurements.Where(m => m.Kind == "count"))
                        {
                            measurement.Parameters[QuantityGeometryEvidence.InsertScaleKey] = Triple(scale.X, scale.Y, scale.Z);
                            measurement.Parameters[QuantityGeometryEvidence.InsertNormalKey] = Triple(normal.X, normal.Y, normal.Z);
                            if (signature != null) measurement.Parameters[QuantityGeometryEvidence.InsertBlockSignatureKey] = signature;
                        }
                    }
                }
                if (extraParameters != null)
                    foreach (var measurement in measurements)
                        foreach (var pair in extraParameters)
                            measurement.Parameters[pair.Key] = pair.Value;
                if (sourceChoices != null)
                    foreach (var measurement in measurements)
                    {
                        measurement.Parameters["source_scope_key"] = source.ScopeKey;
                        measurement.Parameters["source_category"] = sourceChoices[source.ScopeKey].Category;
                    }

                // Recognition evidence (ev_* + _status) on the final measurements only.
                // Observation, never a grouping, rule-key, quantity or pricing input.
                EstimateScanTrace.Mark("evidence.entity.begin", handle: handlePath, type: ent.GetType().Name);
                var entityEvidence = evidence.Read(ent, tr, frame, transform, handlePath, source.XrefChain);
                EstimateScanTrace.Mark("evidence.entity.end", handle: handlePath, type: ent.GetType().Name);
                foreach (var measurement in measurements)
                    CivilEvidenceCollector.Append(measurement, entityEvidence);

                var closedChoice = IsAmbiguousClosedPolyline(ent) && measurements.Count == 2
                    ? ClosedPolylineChoice(profile, ent.Layer, measurements)
                    : null;
                // Start unscoped before attempting record creation. Only the two
                // successfully emitted records can prove this boundary's scope.
                var completeClosedAmbiguity = closedChoice is { IsResolved: false }
                    ? closedAmbiguities.Begin(profile.ProfileId, ent.Layer,
                        closedChoice.AreaRuleKey, closedChoice.LengthRuleKey,
                        source.DrawingPath, source.DrawingHash, handlePath, source.XrefChain)
                    : null;

                foreach (var measurement in measurements)
                {
                    // An approved rule for this layer+kind classifies the record; the
                    // RuleKey is the same key the approval was saved under.
                    var ruleKey = BuildDiscoveryRuleKey(ent.Layer, measurement);
                    var resolution = ResolveApprovedRule(profile, ruleKey, ent.Layer, measurement.Kind);
                    var approved = resolution.Rule;
                    if (resolution.IsAmbiguous)
                        AddAmbiguousRuleFinding(result.Findings, profile.ProfileId,
                            ruleKey, ent.Layer, measurement.Kind, resolution.Matches);

                    var recordId = $"q-disc-{handlePath.Replace('/', '-')}-{measurement.Kind}";
                    if (!recordIds.Add(recordId))
                    {
                        result.Findings.Add(new DeliveryFinding
                        {
                            Code = EstimateFindingCodes.DuplicateSource,
                            Domain = "estimate",
                            Severity = FindingSeverity.Error,
                            Title = "אותו מופע מקור חולץ יותר מפעם אחת — האומדן חסום",
                            Message = $"record_id={recordId}; xref={source.XrefChain ?? "host"}",
                            AffectedRecordIds = { recordId },
                            ProjectProfileId = profile.ProfileId,
                        });
                        continue;
                    }

                    var record = new NeutralQuantityRecord
                    {
                    RecordId = recordId,
                    ProjectProfileId = profile.ProfileId,
                    RunId = runId,
                    Source = new QuantitySource
                    {
                        Drawing = source.DrawingName,
                        DrawingPath = source.DrawingPath,
                        DrawingHash = source.DrawingHash,
                        Handle = handlePath,
                        EntityType = ent.GetType().Name.ToUpperInvariant(),
                        Layer = ent.Layer,
                        Xref = source.XrefChain,
                    },
                    Measurement = measurement,
                    Classification = new QuantityClassification
                    {
                        SourceClass = ent.GetType().Name.ToUpperInvariant(),
                        // Layer-derived key: the unit an engineer approves a mapping for.
                        RuleKey = approved?.RuleKey ?? ruleKey,
                        CandidateCatalogCode = approved?.CandidateCatalogCode,
                        ApprovedCatalogId = approved?.ApprovedCatalogId,
                        ApprovedCatalogHash = approved?.ApprovedCatalogHash,
                        ApprovedCatalogItemFingerprint = approved?.ApprovedCatalogItemFingerprint,
                        MappingApprovedBy = approved?.ApprovedBy,
                        MappingApprovedAtUtc = approved?.ApprovedAtUtc,
                        Tags = { approved != null ? "rule-classified" : "discovered" },
                    },
                    Provenance = new ProvenanceRef
                    {
                        SourceKind = source.IsExternal ? "xref" : "drawing",
                        SourcePathOrUri = source.DrawingPath,
                        DrawingChecksum = source.DrawingHash,
                        SourceHandle = handlePath,
                        XrefPath = source.XrefChain,
                        EntityType = ent.GetType().Name,
                        Layer = ent.Layer,
                        MeasurementMethod = measurement.Method,
                        ToolVersion = SectionPlanService.ToolVersion,
                        RunId = runId,
                    },
                    Status = approved != null ? DeliveryStatus.Ready : DeliveryStatus.ReviewRequired,
                    };
                    if (approved == null)
                    {
                        record.Findings.Add(new DeliveryFinding
                        {
                            Code = EstimateFindingCodes.Unmapped,
                            Domain = "estimate",
                            Severity = FindingSeverity.ReviewRequired,
                            Title = $"נמדדו {measurement.RawValue:F2} {measurement.Unit} בשכבה '{Bidi.Ltr(ent.Layer)}' ללא שיוך לסעיף מחירון",
                            RecommendedAction = $"יש לאשר שיוך לשכבה (חוק '{record.Classification.RuleKey}') דרך 'אשר מיפוי'.",
                            AffectedRecordIds = { record.RecordId },
                            ProjectProfileId = profile.ProfileId,
                        });
                    }
                    result.Records.Add(record);
                    extentsEvidence.BindEmitted(record);
                    evidence.Bind(record, entityEvidence);
                }
                completeClosedAmbiguity?.Invoke();

                // Build only on a failure. The traversal identity is available even
                // when no quantity row could be emitted (native57: 54 Hatch errors).
                ProvenanceRef FailureSource(string operation) => new()
                {
                    SourceKind = source.IsExternal ? "xref" : "drawing",
                    SourcePathOrUri = source.DrawingPath,
                    DrawingChecksum = source.DrawingHash,
                    SourceHandle = handlePath,
                    XrefPath = source.XrefChain,
                    // Preserve the actual composed transform for a failed or
                    // analytically recovered input, not just the XREF name.
                    XrefTransform = transformMeasurement ? transform.ToArray() : null,
                    EntityType = ent.GetType().Name,
                    Layer = sourceLayer,
                    MeasurementMethod = operation,
                    ToolVersion = SectionPlanService.ToolVersion,
                    RunId = runId,
                };
            }

            // The item inserts of an associative ARRAY (its anonymous block's BlockReference members, in block order), or
            // null when this reference is no associative array. Identity by the AutoCAD association, never by block name.
            List<BlockReference>? AssociativeArrayItems(BlockReference reference, BlockTableRecord definition)
            {
                try
                {
                    if (!AssocArray.IsAssociativeArray(reference.ObjectId)) return null;
                }
                catch (Exception)
                {
                    return null;
                }
                var items = new List<BlockReference>();
                foreach (ObjectId id in definition)
                    if (tr.GetObject(id, OpenMode.ForRead) is BlockReference item)
                        items.Add(item);
                return items;
            }

            Dictionary<string, string> ArrayContainerParameters(List<BlockReference> items, Matrix3d arrayToWorld)
            {
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cad_array"] = "associative",
                    ["cad_array_items"] = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (items.Count > 0 && drawingUnits.IsSupported)
                {
                    var first = items[0].Position.TransformBy(arrayToWorld);
                    var x = drawingUnits.Length(first.X);
                    var y = drawingUnits.Length(first.Y);
                    if (double.IsFinite(x) && double.IsFinite(y))
                        parameters["cad_array_first_item_world_m"] = QuantityGeometryEvidence.FormatPoint(x, y);
                }
                return parameters;
            }

            // Every block inside every item, counted once at its world point: "<array>/<item #>/<block>" (1-based item
            // number in block order, as the reference). Which of them a ruleset counts is the ruleset's decision.
            void ExpandArrayItems(List<BlockReference> items, DiscoverySource source, string arrayHandle,
                Matrix3d arrayToWorld, CivilEvidenceCollector.Frame arrayFrame)
            {
                var member = new Dictionary<string, string>(StringComparer.Ordinal) { ["cad_array_handle"] = arrayHandle };
                for (var k = 0; k < items.Count; k++)
                {
                    var item = items[k];
                    Matrix3d itemToWorld;
                    BlockTableRecord itemDefinition;
                    try
                    {
                        itemToWorld = arrayToWorld * item.BlockTransform;
                        itemDefinition = (BlockTableRecord)tr.GetObject(item.BlockTableRecord, OpenMode.ForRead);
                    }
                    catch (Exception ex)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            "פריט של מערך (Array) אינו ניתן לקריאה — האומדן חסום",
                            $"handle_path={arrayHandle}/{k + 1}; {ex.Message}");
                        continue;
                    }
                    var itemFrame = arrayFrame.EnterInsert(item, evidence, tr);
                    foreach (ObjectId id in itemDefinition)
                        if (tr.GetObject(id, OpenMode.ForRead) is BlockReference symbol)
                            Consider(symbol, source, itemToWorld,
                                $"{arrayHandle}/{(k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}/{symbol.Handle}",
                                itemFrame, transformMeasurement: true, extraParameters: member);
                }
            }

            void TraverseReference(
                BlockReference reference,
                DiscoverySource parentSource,
                string? parentHandlePath,
                int depth,
                Matrix3d outerTransform,
                HashSet<ObjectId> definitionStack,
                CivilEvidenceCollector.Frame frame,
                bool isModelSpaceReference = false,
                bool countOrdinaryReference = true,
                string? activeAncestorClipHandle = null,
                bool isInsideExternalReference = false,
                bool hasExternalOverlayAncestor = false)
            {
                var currentHandle = AppendReferenceHandlePath(
                    parentHandlePath, reference.Handle.ToString());
                EstimateScanTrace.Mark("reference.entry", depth, currentHandle, "BlockReference");
                if (depth > MaxXrefNestingDepth)
                {
                    AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                        EstimateFindingCodes.XrefTraversalUnresolved,
                        "עומק XREF חורג מגבול הבטיחות — האומדן חסום",
                        $"handle_path={currentHandle}; max_depth={MaxXrefNestingDepth}");
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
                    AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                        EstimateFindingCodes.XrefTraversalUnresolved,
                        "לא ניתן לפתוח הגדרת XREF — האומדן חסום",
                        $"handle_path={currentHandle}; {ex.Message}");
                    return;
                }

                var isExternal = definition.IsFromExternalReference ||
                                 definition.IsFromOverlayReference;
                if (!isExternal && parentSource.IsExternal)
                {
                    // Native 11.09 proved that suppressed nested Overlays may be
                    // redirected to an ordinary empty anonymous BTR. Anonymous/empty
                    // alone never authorizes exclusion: recover the exact INSERT's
                    // metadata from the independently verified source snapshot.
                    // 01/10: the cache reads originals from local drives only. A parent on P: is read from a local copy of
                    // that exact path whose bytes hash to the SHA this scan computed from P: — never a search, never
                    // another file; without that proof no alias is passed and the refusal stays visible.
                    string? readCopy = null;
                    // Guarded like Query itself: a failing Autodesk getter here means "no copy"; Query then records the
                    // exact Refused instead of the whole scan aborting (Codex 12:56).
                    try
                    {
                        if (isInsideExternalReference && XrefOriginalReferenceCache.CheckCandidate(definition.IsAnonymous,
                                definition.IsFromExternalReference, definition.IsFromOverlayReference, definition.IsLayout,
                                () => { foreach (ObjectId _ in definition) return true; return false; }) == null)
                            readCopy = readSnapshots.ReadPathFor(parentSource.DrawingPath, parentSource.DrawingHash);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { readCopy = null; }
                    catch (InvalidOperationException) { readCopy = null; }
                    var original = originalReferences.Query(reference, definition,
                        parentSource.DrawingPath, parentSource.DrawingHash,
                        parentSource.VerifiedLoadedDatabase, reference.Handle.ToString(),
                        parentSource.LoadedLayerPrefix ?? string.Empty,
                        isInsideExternalReference, hasExternalOverlayAncestor, readCopy);
                    if ((readCopy != null || original.Decision != XrefOriginalReferenceCache.Decision.NotApplicable) &&
                        result.XrefOriginalReads.Count < MaxOriginalReadReceipts)
                        result.XrefOriginalReads.Add(new(currentHandle, parentSource.DrawingPath, parentSource.DrawingHash,
                            readCopy, original.ReadSourcePath, original.Decision.ToString(), original.Detail));
                    if (original.Decision == XrefOriginalReferenceCache.Decision.ExcludeByOverlay)
                    {
                        result.RecoveredOverlayReferences.Add(new(
                            parentSource.DrawingPath, parentSource.DrawingHash,
                            currentHandle, reference.Layer, original.Detail));
                        return;
                    }
                    if (original.Decision == XrefOriginalReferenceCache.Decision.Refused)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            "לא ניתן לאמת את סוג ההפניה המקורי — נדרשת בדיקת מקור",
                            $"handle_path={currentHandle}; source={parentSource.DrawingPath}; {original.Detail}" +
                            readSnapshots.NoteFor(parentSource.DrawingPath));
                        return;
                    }
                }
                // Scope selection is checked BEFORE clip, transforms, source files or geometry are read.
                // A top-level XREF reached through an ordinary host block still owns its entire nested branch.
                var scopeKey = parentSource.ScopeKey;
                if (isExternal && !parentSource.IsExternal)
                {
                    scopeKey = EstimateSourceSelectionPolicy.XrefKey(definition.Name, definition.PathName ?? "", definition.Handle.ToString());
                    if (sourceChoices != null && !sourceChoices.ContainsKey(scopeKey))
                        throw new InvalidOperationException("הפניה שלא נכללה במצאי נמצאה — יש לאשר מקורות מחדש");
                    if (!Included(scopeKey)) return;
                }
                if (XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                        isExternal,
                        definition.IsFromOverlayReference,
                        isInsideExternalReference,
                        hasExternalOverlayAncestor))
                {
                    // This branch is not part of the host's visible/reference graph.
                    // In particular, an unresolved child under a top-level Overlay
                    // must not block quantities from that Overlay's direct geometry.
                    return;
                }
                if (!TryInspectActiveSpatialClip(tr, reference,
                        out var hasActiveClip, out var clipFailure))
                {
                    AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                        EstimateFindingCodes.XrefTraversalUnresolved,
                        "לא ניתן לאמת את מצב XCLIP של הפניה — האומדן חסום",
                        $"handle_path={currentHandle}; {clipFailure}");
                    return;
                }
                var effectiveClipHandle = hasActiveClip
                    ? currentHandle
                    : activeAncestorClipHandle;
                if (XrefQuantityPolicy.ActiveClipBlocksExternalTraversal(
                        isExternal, hasActiveClip, activeAncestorClipHandle != null))
                {
                    AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                        EstimateFindingCodes.XrefTraversalUnresolved,
                        "נמצא XCLIP פעיל — האומדן חסום עד למדידה מודעת-חיתוך",
                        $"definition={definition.Name}; handle_path={currentHandle}; clip_handle={effectiveClipHandle}");
                    return;
                }

                Matrix3d composedTransform;
                try { composedTransform = outerTransform * reference.BlockTransform; }
                catch (Exception ex)
                {
                    AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                        EstimateFindingCodes.XrefTransformInvalid,
                        "טרנספורמציית Block/XREF אינה ניתנת להרכבה — האומדן חסום",
                        $"handle_path={currentHandle}; {ex.Message}");
                    return;
                }

                if (!definitionStack.Add(definition.ObjectId))
                {
                    AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                        EstimateFindingCodes.XrefCycle,
                        "זוהה מחזור בשרשרת XREF — האומדן חסום",
                        $"definition={definition.Name}; handle_path={currentHandle}");
                    return;
                }

                try
                {
                    // An ordinary INSERT visible in model space (or directly inside an
                    // XREF) is counted once. Its definition is then traversed solely to
                    // discover an XREF hidden several ordinary-block levels down; neither
                    // raw members nor nested implementation INSERTs are counted again.
                    if (!isExternal)
                    {
                        // Visible attributes are drawing text for nearby records whether or not this INSERT is
                        // counted (a nested implementation INSERT's labels are still on the drawing). Index only.
                        evidence.CaptureAttributes(reference, tr, outerTransform, currentHandle,
                            parentSource.XrefChain, frame.InsideOwnInsert);
                        // v4.5 (reference MARK-V4-01): an associative ARRAY in the host model space draws its items as
                        // inserts inside its anonymous block; the symbols inside each item are drawn objects of their own.
                        var arrayItems = countOrdinaryReference && isModelSpaceReference && !parentSource.IsExternal
                            ? AssociativeArrayItems(reference, definition) : null;
                        if (countOrdinaryReference)
                            Consider(reference, parentSource, outerTransform, currentHandle, frame,
                                transformMeasurement: !isModelSpaceReference,
                                extraParameters: arrayItems == null ? null : ArrayContainerParameters(arrayItems, composedTransform));
                        var insideFrame = frame.EnterInsert(reference, evidence, tr);
                        if (arrayItems != null && Included(parentSource.ScopeKey))
                            ExpandArrayItems(arrayItems, parentSource, currentHandle, composedTransform, insideFrame);
                        foreach (ObjectId id in definition)
                        {
                            DBObject member;
                            try { member = tr.GetObject(id, OpenMode.ForRead); }
                            catch (Exception ex)
                            {
                                AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                                    EstimateFindingCodes.XrefTraversalUnresolved,
                                    "ישות בתוך Block אינה ניתנת לקריאה — האומדן חסום",
                                    $"definition={definition.Name}; handle_path={currentHandle}; {ex.Message}");
                                continue;
                            }
                            if (member is BlockReference nestedReference)
                                TraverseReference(nestedReference, parentSource, currentHandle,
                                    depth + 1, composedTransform, definitionStack, insideFrame,
                                    countOrdinaryReference: false,
                                    activeAncestorClipHandle: effectiveClipHandle,
                                    isInsideExternalReference: isInsideExternalReference,
                                    hasExternalOverlayAncestor: hasExternalOverlayAncestor);
                            // Text drawn inside an ordinary block is visible drawing text for
                            // every instance; indexed only, never counted or measured.
                            else if (member is DBText or MText)
                                evidence.CaptureText((Entity)member, composedTransform,
                                    AppendReferenceHandlePath(currentHandle, member.Handle.ToString()),
                                    parentSource.XrefChain);
                        }
                        return;
                    }

                    if (definition.IsUnloaded || !definition.IsResolved)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            $"ה-XREF '{Bidi.Ltr(definition.Name)}' אינו טעון/פתור — האומדן חסום",
                            definition.PathName ?? string.Empty);
                        return;
                    }

                    var resolved = EstimateScanTrace.Step("xref.resolve", () => ClInstructionReader.ResolvePath(
                        definition.PathName, parentSource.DrawingPath), depth, currentHandle, "BlockReference");
                    if (string.IsNullOrWhiteSpace(resolved))
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            $"לא ניתן לפתור את נתיב ה-XREF '{Bidi.Ltr(definition.Name)}' — האומדן חסום",
                            definition.PathName ?? string.Empty);
                        return;
                    }

                    EstimateScanTrace.Mark("xref.snapshot_before.begin", depth, currentHandle);
                    var identityRead = TryReadDwgSnapshotIdentity(resolved, out var diskBeforeHash,
                            out var diskIdentityFailure);
                    EstimateScanTrace.Mark("xref.snapshot_before.end", depth, currentHandle);
                    if (!identityRead)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            $"לא ניתן לקרוא זהות שמורה של ה-XREF '{Bidi.Ltr(definition.Name)}' — האומדן חסום",
                            $"path={resolved}; {diskIdentityFailure}");
                        return;
                    }

                    string hash;
                    if (externalHashes.TryGetValue(resolved, out var cached))
                    {
                        if (!XrefQuantityPolicy.SameSnapshot(cached.Snapshot, diskBeforeHash))
                        {
                            AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                                EstimateFindingCodes.XrefTraversalUnresolved,
                                $"קובץ ה-XREF '{Bidi.Ltr(definition.Name)}' השתנה במהלך הסריקה — האומדן חסום",
                                resolved);
                            return;
                        }
                        hash = cached.Hash;
                    }
                    else
                    {
                        hash = EstimateScanTrace.Step("xref.hash", () => ClInstructionReader.HashFileShared(resolved), depth, currentHandle);
                        if (!CatalogIdentity.IsValidSha256(hash))
                        {
                            AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                                EstimateFindingCodes.XrefTraversalUnresolved,
                                $"לא ניתן לאמת את קובץ ה-XREF '{Bidi.Ltr(definition.Name)}' — האומדן חסום",
                                resolved);
                            return;
                        }
                    }

                    EstimateScanTrace.Mark("xref.snapshot_after.begin", depth, currentHandle);
                    identityRead = TryReadDwgSnapshotIdentity(resolved, out var diskAfterHash,
                            out diskIdentityFailure);
                    EstimateScanTrace.Mark("xref.snapshot_after.end", depth, currentHandle);
                    if (!identityRead)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            $"לא ניתן לאמת מחדש את זהות ה-XREF '{Bidi.Ltr(definition.Name)}' — האומדן חסום",
                            $"path={resolved}; {diskIdentityFailure}");
                        return;
                    }

                    XrefQuantityPolicy.SourceSnapshotIdentity? loadedSnapshot = null;
                    string? loadedIdentityFailure = null;
                    Database? loadedDatabase = null;
                    try
                    {
                        loadedDatabase = EstimateScanTrace.Step("xref.loaded_database", () => definition.GetXrefDatabase(false), depth, currentHandle);
                        if (loadedDatabase == null)
                            loadedIdentityFailure = "GetXrefDatabase returned null";
                        else
                            loadedSnapshot = SnapshotIdentity(loadedDatabase);
                    }
                    catch (Exception ex)
                    {
                        loadedIdentityFailure = ex.Message;
                    }
                    var freshnessFailure = XrefQuantityPolicy.LoadedSnapshotFailure(
                        loadedSnapshot, diskBeforeHash, diskAfterHash);
                    if (freshnessFailure != null)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTraversalUnresolved,
                            $"העותק הטעון של ה-XREF '{Bidi.Ltr(definition.Name)}' אינו מוכח כזהה לקובץ — האומדן חסום",
                            $"path={resolved}; reason={freshnessFailure}; {loadedIdentityFailure}");
                        return;
                    }
                    externalHashes[resolved] = (hash, diskAfterHash);

                    var transformCheck = ValidateExternalTransform(composedTransform);
                    if (!transformCheck.IsSafe)
                    {
                        AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                            EstimateFindingCodes.XrefTransformInvalid,
                            "טרנספורמציית XREF אינה אחידה/אורתוגונלית — האומדן חסום",
                            $"handle_path={currentHandle}; reason={transformCheck.Failure}");
                        return;
                    }

                    var chain = AppendXrefChain(parentSource.XrefChain, definition.Name);
                    var source = new DiscoverySource(
                        resolved, hash, Path.GetFileName(resolved), chain, true, loadedDatabase,
                        definition.Name, scopeKey);
                    result.ExternalSources.Add(new EstimateExternalSource
                    {
                        DrawingPath = resolved,
                        DrawingHash = hash,
                        XrefChain = chain,
                        ReferenceHandlePath = currentHandle,
                    });
                    // Evidence frame of the XREF's own space: one more chain link (source→parent
                    // matrix) and the loaded XREF database, proven fresh above.
                    var xrefFrame = frame.EnterXref(reference, definition, loadedDatabase, evidence, tr);

                    foreach (ObjectId id in definition)
                    {
                        DBObject nestedObject;
                        try { nestedObject = tr.GetObject(id, OpenMode.ForRead); }
                        catch (Exception ex)
                        {
                            AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                                EstimateFindingCodes.XrefTraversalUnresolved,
                                "ישות בתוך XREF אינה ניתנת לקריאה — האומדן חסום",
                                $"xref={chain}; handle_path={currentHandle}; {ex.Message}");
                            continue;
                        }

                        if (nestedObject is BlockReference nestedReference)
                        {
                            TraverseReference(nestedReference, source, currentHandle,
                                depth + 1, composedTransform, definitionStack, xrefFrame,
                                countOrdinaryReference: true,
                                isInsideExternalReference: true,
                                hasExternalOverlayAncestor:
                                    hasExternalOverlayAncestor ||
                                    definition.IsFromOverlayReference);
                            continue;
                        }
                        if (nestedObject is Entity nestedEntity)
                            Consider(nestedEntity, source, composedTransform,
                                AppendReferenceHandlePath(currentHandle,
                                    nestedEntity.Handle.ToString()),
                                xrefFrame,
                                transformMeasurement: true);
                    }
                }
                finally
                {
                    definitionStack.Remove(definition.ObjectId);
                }
            }

            EstimateScanTrace.Mark("modelspace.begin");
            foreach (ObjectId id in ms)
            {
                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead); }
                catch (Exception ex)
                {
                    result.Findings.Add(MeasurementFailure(
                        profile.ProfileId, $"Failed to open model-space object {id}", ex.Message));
                    continue;
                }

                if (obj is BlockReference reference)
                {
                    TraverseReference(reference, hostSource, null, 0, Matrix3d.Identity,
                        new HashSet<ObjectId>(), hostFrame, isModelSpaceReference: true,
                        countOrdinaryReference: true);
                    continue;
                }
                if (obj is Entity entity)
                    Consider(entity, hostSource, Matrix3d.Identity, entity.Handle.ToString(), hostFrame);
            }
            // Phase 2: nearby-text queries against the one index, then the bounded coverage.
            // The sheets' legends, read once in the same transaction, then matched per record in Complete.
            EstimateScanTrace.Mark("modelspace.end", result.ScannedEntities);
            EstimateScanTrace.Mark("evidence.legends.begin");
            evidence.ReadLegends(tr, db);
            EstimateScanTrace.Mark("evidence.legends.end");
            EstimateScanTrace.Mark("evidence.complete.begin");
            result.EvidenceCoverage = evidence.Complete();
            EstimateScanTrace.Mark("evidence.complete.end");
            log?.Info("estimate.evidence " + result.EvidenceCoverage.Summary());
            EstimateScanTrace.Mark("xref.originals_freshness.begin");
            var recoveredSourcesFailure = originalReferences.ValidateSourcesUnchanged();
            EstimateScanTrace.Mark("xref.originals_freshness.end");
            if (recoveredSourcesFailure != null)
                AddXrefTraversalFinding(result.Findings, profile.ProfileId,
                    EstimateFindingCodes.XrefTraversalUnresolved,
                    "מקור ההפניות השתנה בזמן הסריקה — לא ניתן לאשר את האומדן",
                    recoveredSourcesFailure);
            log?.End("estimate.discovery_scan",
                $"entities={result.ScannedEntities} records={result.Records.Count}");
            if (skippedPresentation > 0)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.SourceMissing,
                    Domain = "estimate",
                    Severity = FindingSeverity.Info,
                    Title = $"{skippedPresentation} עצמי תצוגה של Civil (גרפיקת חתכים/פרופילים, תוויות) לא נמדדו",
                    Message = "Layers named *_SectionView, *_ProfileView, *Label*, *Band*, *Grid* are Civil 3D drawing furniture, not construction quantities.",
                });
            }

            var nonQuantityFinding = KnownNonQuantityCoverageFinding(
                skippedNonQuantityTypes, profile.ProfileId);
            if (nonQuantityFinding != null) result.Findings.Add(nonQuantityFinding);

            var coverageFinding = UnsupportedEntityCoverageFinding(
                unsupportedEntityTypes, profile.ProfileId, unsupportedEntitySources);
            if (coverageFinding != null) result.Findings.Add(coverageFinding);

            closedAmbiguities.Publish();
            ConsolidateMeasurementFailures(result, profile.ProfileId);
            return result;
        }

        /// <summary>The primary measurement a piece of geometry supports — no rule needed.</summary>
        internal static QuantityMeasurement? MeasureNatural(
            Entity ent, List<DeliveryFinding>? findings = null, string? projectProfileId = null,
            DrawingUnitPolicy.Scale? drawingUnits = null, string? preferredKind = null,
            Action<DeliveryFinding>? reportExtentsFailure = null,
            Func<string, ProvenanceRef>? failureSource = null,
            Action<HatchAreaFailureDiagnostic.Snapshot>? captureHatchFailure = null,
            Action<string>? captureExtentsDiagnostic = null,
            Func<double>? curveRecoveryMaximumScale = null,
            Func<StrictHatchExactRetraceInputFactory.HostInput?>? exactRetraceInput = null)
        {
            var units = drawingUnits ?? DrawingUnitPolicy.Resolve(UnitsValue.Meters);
            // Do not query another native geometric property merely to describe a
            // failed one. Closed alternatives supply their exact kind explicitly.
            var measurementKind = preferredKind ?? (ent switch
            {
                Hatch or AcadRegion => "area",
                BlockReference or CivilDb.Structure => "count",
                Polyline or Polyline2d => "area-or-length",
                _ => "length",
            });
            var measurementOperation = ent is Hatch ? "hatch-area" : "natural-" + measurementKind;
            DeliveryFinding Failure(string title, string reason, string operation) =>
                MeasurementFailure(projectProfileId, title, reason,
                    failureSource?.Invoke(operation), measurementKind);
            double[]? bbox = null;
            try
            {
                var ext = ent.GeometricExtents;
                bbox = units.Bounds(new[] { ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y });
            }
            catch (Exception ex)
            {
                var failure = Failure(
                    $"Failed to read extents for {SafeEntityId(ent)}", ex.Message, "geometric-extents");
                findings?.Add(failure);
                reportExtentsFailure?.Invoke(failure);
                captureExtentsDiagnostic?.Invoke(ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                switch (ent)
                {
                    case Hatch h:
                        return new QuantityMeasurement
                        {
                            Kind = "area", Method = "hatch-area",
                            RawValue = units.Area(h.Area), Unit = units.AreaUnit,
                            GeometryEvidence = bbox,
                        };

                    case Polyline { Closed: true } closed:
                        measurementKind = string.Equals(preferredKind, "length", StringComparison.OrdinalIgnoreCase)
                            ? "length" : "area";
                        measurementOperation = "closed-polyline-" + measurementKind;
                        if (string.Equals(preferredKind, "length", StringComparison.OrdinalIgnoreCase))
                            return LengthMeasurement("closed-polyline-perimeter",
                                units.Length(closed.Length), units, bbox);
                        return new QuantityMeasurement
                        {
                            Kind = "area", Method = "closed-polyline-area",
                            RawValue = units.Area(closed.Area), Unit = units.AreaUnit,
                            GeometryEvidence = bbox,
                        };

                    case Polyline open:
                        measurementKind = "length";
                        measurementOperation = "polyline-length";
                        return LengthMeasurement("polyline-length",
                            units.Length(open.Length), units, bbox);

                    case Polyline2d { Closed: true } closed2d:
                        measurementKind = string.Equals(preferredKind, "length", StringComparison.OrdinalIgnoreCase)
                            ? "length" : "area";
                        measurementOperation = "closed-polyline2d-" + measurementKind;
                        if (string.Equals(preferredKind, "length", StringComparison.OrdinalIgnoreCase))
                            return LengthMeasurement("closed-polyline2d-perimeter",
                                units.Length(CurveLength(closed2d)), units, bbox);
                        return new QuantityMeasurement
                        {
                            Kind = "area", Method = "closed-polyline2d-area",
                            RawValue = units.Area(closed2d.Area), Unit = units.AreaUnit,
                            GeometryEvidence = bbox,
                        };

                    case Polyline2d open2d:
                        measurementKind = "length";
                        measurementOperation = "polyline2d-length";
                        return LengthMeasurement("polyline2d-length",
                            units.Length(CurveLength(open2d)), units, bbox);

                    case Polyline3d polyline3d:
                        return LengthMeasurement("polyline3d-length",
                            units.Length(CurveLength(polyline3d)), units, bbox);

                    case Line line:
                        return LengthMeasurement("line-length",
                            units.Length(line.Length), units, bbox);

                    case Arc arc:
                        return LengthMeasurement("arc-length",
                            units.Length(arc.Length), units, bbox);

                    case Circle circle:
                        return LengthMeasurement("circle-circumference",
                            units.Length(2.0 * Math.PI * circle.Radius), units, bbox);

                    case Spline spline:
                        return LengthMeasurement("spline-length",
                            units.Length(CurveLength(spline)), units, bbox);

                    case AcadRegion region:
                        return new QuantityMeasurement
                        {
                            Kind = "area", Method = "region-area",
                            RawValue = units.Area(region.Area), Unit = units.AreaUnit,
                            GeometryEvidence = bbox,
                        };

                    case CivilDb.FeatureLine featureLine:
                        return LengthMeasurement("featureline-length2d",
                            units.Length(featureLine.Length2D), units, bbox);

                    case CivilDb.Pipe pipe:
                        return LengthMeasurement("pipe-length2d",
                            units.Length(pipe.Length2D), units, bbox);

                    case CivilDb.Structure structure:
                        var structureName = structure.Name;
                        if (string.IsNullOrWhiteSpace(structureName))
                            throw new InvalidDataException(
                                $"Structure {SafeEntityId(structure)} has no readable name.");
                        return new QuantityMeasurement
                        {
                            Kind = "count", Method = "structure-count",
                            RawValue = 1, Unit = "יח'",
                            GeometryEvidence = bbox,
                            Parameters = { ["object_name"] = structureName },
                        };

                    case BlockReference br:
                        string blockName;
                        try { blockName = br.Name; }
                        catch (Exception ex)
                        {
                            blockName = "?";
                            findings?.Add(Failure(
                                $"Failed to read block name for {SafeEntityId(br)}", ex.Message, "block-name"));
                        }
                        return new QuantityMeasurement
                        {
                            Kind = "count", Method = "block-count",
                            RawValue = 1, Unit = "יח'",
                            GeometryEvidence = bbox,
                            Parameters = { ["block_name"] = blockName },
                        };

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                if (ent is Hatch failedHatch && measurementOperation == "hatch-area")
                {
                    // Read once in the exact current traversal DB. Preserve the
                    // original API failure, including successful strict recoveries.
                    try
                    {
                        var snapshot = HatchAreaFailureDiagnostic.CaptureNative(failedHatch);
                        captureHatchFailure?.Invoke(snapshot);
                        var recoveredSource = failureSource?.Invoke(StrictHatchLinearAreaRecovery.Method);
                        if (recoveredSource != null &&
                            !string.IsNullOrWhiteSpace(recoveredSource.SourcePathOrUri) &&
                            !string.IsNullOrWhiteSpace(recoveredSource.SourceHandle) &&
                            CatalogIdentity.IsValidSha256(recoveredSource.DrawingChecksum))
                        {
                            if (StrictHatchLinearAreaRecovery.TryRecover(snapshot, units,
                                    ex.GetType().Name + ": " + ex.Message, bbox, out var recovered, out var refusal))
                            {
                                findings?.Add(new DeliveryFinding
                                {
                                    Code = StrictHatchLinearAreaRecovery.FindingCode, Domain = "estimate",
                                    Severity = FindingSeverity.Info,
                                    Title = "שטח HATCH נקרא מגבול ישר סגור ומאומת לאחר כשל Area",
                                    Message = "Original Area API: " + ex.GetType().Name + ": " + ex.Message +
                                        "; boundary_sha256=" + recovered!.Parameters["boundary_sha256"] +
                                        ". Source-plane area recovered; existing unit/XREF and measurement-evidence guards still apply. No mapping or price approved.",
                                    ProjectProfileId = projectProfileId,
                                    SourceRefs = { recoveredSource },
                                });
                                return recovered;
                            }
                            reason += Environment.NewLine + "strict linear boundary recovery refused: " + refusal;
                            var curveSource = failureSource?.Invoke(StrictHatchCurveAreaRecoveryAdapter.Method);
                            var curveIdentityMatches = curveSource != null &&
                                string.Equals(curveSource.SourcePathOrUri, recoveredSource.SourcePathOrUri, StringComparison.Ordinal) &&
                                string.Equals(curveSource.SourceHandle, recoveredSource.SourceHandle, StringComparison.Ordinal) &&
                                string.Equals(curveSource.DrawingChecksum, recoveredSource.DrawingChecksum, StringComparison.OrdinalIgnoreCase);
                            if (!curveIdentityMatches) refusal = "source identity changed or is incomplete";
                            if (curveIdentityMatches &&
                                StrictHatchCurveAreaRecoveryAdapter.TryRecover(snapshot, units,
                                    curveRecoveryMaximumScale?.Invoke() ?? 1.0,
                                    ex.GetType().Name + ": " + ex.Message, bbox, out recovered, out refusal))
                            {
                                findings?.Add(new DeliveryFinding
                                {
                                    Code = StrictHatchCurveAreaRecoveryAdapter.FindingCode, Domain = "estimate",
                                    Severity = FindingSeverity.Info,
                                    Title = "שטח HATCH נקרא מגבול קווים וקשתות מאומת לאחר כשל Area",
                                    Message = "Original Area API: " + ex.GetType().Name + ": " + ex.Message +
                                        "; boundary_sha256=" + recovered!.Parameters["boundary_sha256"] +
                                        "; host_area_error_upper_bound=" + recovered.Parameters["boundary_host_area_error_upper_bound"] +
                                        " m2. Original curves retained; existing unit/XREF guards still apply. No mapping or price approved.",
                                    ProjectProfileId = projectProfileId,
                                    SourceRefs = { curveSource! },
                                });
                                return recovered;
                            }
                            reason += Environment.NewLine + "strict curve boundary recovery refused: " + refusal;
                            var originalSource = failureSource?.Invoke("hatch-area");
                            var originalRetraceHost = exactRetraceInput?.Invoke();
                            if (StrictHatchExactRetraceInputFactory.TryRecover(snapshot, originalSource,
                                    originalRetraceHost, ex.GetType().Name + ": " + ex.Message, bbox,
                                    out recovered, out refusal))
                            {
                                findings?.Add(new DeliveryFinding
                                {
                                    Code = StrictHatchExactRetraceInputFactory.FindingCode, Domain = "estimate",
                                    Severity = FindingSeverity.Info,
                                    Title = "שטח HATCH חושב לאחר ביטול הלוך־חזור ישר וזהה בגבול המקורי",
                                    Message = "Original Area API: " + ex.GetType().Name + ": " + ex.Message +
                                        "; input_receipt_sha256=" + recovered!.Parameters["boundary_input_receipt_sha256"] +
                                        "; original_boundary_sha256=" + recovered.Parameters["boundary_sha256"] +
                                        ". Quantity-only interpretation; original boundary retained; direct-host only. " +
                                        "No drawing edit, mapping, price, section-boundary or engineering approval.",
                                    ProjectProfileId = projectProfileId,
                                    SourceRefs = { originalSource! },
                                });
                                return recovered;
                            }
                            reason += Environment.NewLine + "exact-retrace boundary recovery refused: " + refusal;
                            if (StrictHatchMixedLineRetraceInputFactory.TryRecover(snapshot, originalSource,
                                    originalRetraceHost, ex.GetType().Name + ": " + ex.Message, bbox,
                                    out recovered, out refusal))
                            {
                                findings?.Add(new DeliveryFinding
                                {
                                    Code = StrictHatchMixedLineRetraceInputFactory.FindingCode, Domain = "estimate",
                                    Severity = FindingSeverity.Info,
                                    Title = "שטח HATCH חושב לאחר ביטול זוג קווים ישרים הפוכים ומדויקים; הקשתות נשמרו",
                                    Message = "Original Area API: " + ex.GetType().Name + ": " + ex.Message +
                                        "; input_receipt_sha256=" + recovered!.Parameters["boundary_input_receipt_sha256"] +
                                        "; original_boundary_sha256=" + recovered.Parameters["boundary_sha256"] +
                                        "; residual_boundary_sha256=" + recovered.Parameters["boundary_residual_sha256"] +
                                        ". Quantity-only; original geometry retained; direct-host only. " +
                                        "No drawing edit, mapping, price, section-boundary or engineering approval.",
                                    ProjectProfileId = projectProfileId,
                                    SourceRefs = { originalSource! },
                                });
                                return recovered;
                            }
                            reason += Environment.NewLine + "mixed-line-retrace boundary recovery refused: " + refusal;
                        }
                        else reason += Environment.NewLine + "strict linear boundary recovery refused: source identity is incomplete";
                    }
                    catch (Exception diagnosticError)
                    { reason += Environment.NewLine + "hatch diagnostic unavailable: " + diagnosticError.Message; }
                }
                findings?.Add(Failure(
                    $"Failed to measure supported entity {SafeEntityId(ent)}", reason, measurementOperation));
                return null;
            }
        }

        internal static bool IsNaturallySupported(Entity ent) =>
            ent is Hatch or Polyline or Polyline2d or Polyline3d or Line or Arc or Circle or Spline or AcadRegion or BlockReference or
                CivilDb.FeatureLine or CivilDb.Pipe or CivilDb.Structure;

        private static QuantityMeasurement LengthMeasurement(
            string method, double value, DrawingUnitPolicy.Scale units, double[]? bbox) => new()
        {
            Kind = "length",
            Method = method,
            RawValue = value,
            Unit = units.LengthUnit,
            GeometryEvidence = bbox,
        };

        private static double CurveLength(Curve curve)
        {
            var from = curve.GetDistanceAtParameter(curve.StartParam);
            var to = curve.GetDistanceAtParameter(curve.EndParam);
            return Math.Abs(to - from);
        }

        private static IReadOnlyList<QuantityMeasurement> NaturalMeasurements(
            Entity ent, DrawingUnitPolicy.Scale units,
            List<DeliveryFinding> findings, string projectProfileId,
            Action<QuantityMeasurement, DeliveryFinding>? captureExtentsFailure = null,
            Func<string, ProvenanceRef>? failureSource = null,
            Action<HatchAreaFailureDiagnostic.Snapshot>? captureHatchFailure = null,
            Action<string>? captureExtentsDiagnostic = null,
            Func<double>? curveRecoveryMaximumScale = null,
            Func<StrictHatchExactRetraceInputFactory.HostInput?>? exactRetraceInput = null)
        {
            QuantityMeasurement? MeasureWithEvidence(string? preferredKind = null)
            {
                DeliveryFinding? extentsFailure = null;
                var measured = MeasureNatural(ent, findings, projectProfileId, units, preferredKind,
                    failure => extentsFailure = failure, failureSource, captureHatchFailure,
                    captureExtentsDiagnostic, curveRecoveryMaximumScale, exactRetraceInput);
                if (measured is { Kind: "length", RawValue: 0 } &&
                    TryReadZeroLengthFailure(ent, measured, projectProfileId, failureSource) is { } zeroFailure)
                {
                    // An explanation of an unmeasured input, never a zero output
                    // row. Extents failures also remain global: no row was emitted.
                    findings.Add(zeroFailure);
                    return null;
                }
                if (measured != null && extentsFailure != null)
                    captureExtentsFailure?.Invoke(measured, extentsFailure);
                return measured;
            }
            if (!IsAmbiguousClosedPolyline(ent))
            {
                // The Closed query above already proved these are open curves;
                // preserve their exact dimension even if reading extents fails.
                var single = MeasureWithEvidence(ent is Polyline or Polyline2d ? "length" : null);
                return single == null ? Array.Empty<QuantityMeasurement>() : new[] { single };
            }

            var area = MeasureWithEvidence("area");
            var perimeter = MeasureWithEvidence("length");
            return new[] { area, perimeter }.OfType<QuantityMeasurement>().ToList();
        }

        private static DeliveryFinding? TryReadZeroLengthFailure(
            Entity entity, QuantityMeasurement measurement, string profileId,
            Func<string, ProvenanceRef>? failureSource)
        {
            // Only reached after the native measurement returned zero. Failed or
            // partial reads do not establish degenerate geometry; retain the
            // existing invalid-measurement path in that case. No source is opened.
            try
            {
                var vertices = new List<ZeroLengthGeometryProof.Vertex>();
                ZeroLengthGeometryProof.GeometryKind kind;
                static ZeroLengthGeometryProof.Vertex Point(Point3d p, double bulge = 0) =>
                    new(p.X, p.Y, p.Z, bulge);
                static bool ValidPlane(Vector3d normal, double elevation) =>
                    double.IsFinite(normal.X) && double.IsFinite(normal.Y) && double.IsFinite(normal.Z) &&
                    double.IsFinite(elevation) && (normal.X != 0 || normal.Y != 0 || normal.Z != 0);

                switch (entity)
                {
                    case Line line:
                        kind = ZeroLengthGeometryProof.GeometryKind.Line;
                        vertices.Add(Point(line.StartPoint));
                        vertices.Add(Point(line.EndPoint));
                        break;
                    case Polyline polyline:
                        kind = ZeroLengthGeometryProof.GeometryKind.Polyline;
                        if (!ValidPlane(polyline.Normal, polyline.Elevation) ||
                            polyline.NumberOfVertices > ZeroLengthGeometryProof.MaxVertices) return null;
                        for (var i = 0; i < polyline.NumberOfVertices; i++)
                        {
                            var p = polyline.GetPoint2dAt(i);
                            vertices.Add(new(p.X, p.Y, polyline.Elevation, polyline.GetBulgeAt(i)));
                        }
                        break;
                    case Polyline2d polyline2d:
                        kind = ZeroLengthGeometryProof.GeometryKind.Polyline2d;
                        if (polyline2d.PolyType != Poly2dType.SimplePoly ||
                            !ValidPlane(polyline2d.Normal, polyline2d.Elevation)) return null;
                        foreach (ObjectId vertexId in polyline2d)
                        {
                            if (vertices.Count >= ZeroLengthGeometryProof.MaxVertices) return null;
                            using var vertex = vertexId.GetObject(OpenMode.ForRead) as Vertex2d;
                            if (vertex == null || vertex.VertexType != Vertex2dType.SimpleVertex) return null;
                            vertices.Add(Point(vertex.Position, vertex.Bulge));
                        }
                        break;
                    case Polyline3d polyline3d:
                        kind = ZeroLengthGeometryProof.GeometryKind.Polyline3d;
                        if (polyline3d.PolyType != Poly3dType.SimplePoly) return null;
                        foreach (ObjectId vertexId in polyline3d)
                        {
                            if (vertices.Count >= ZeroLengthGeometryProof.MaxVertices) return null;
                            using var vertex = vertexId.GetObject(OpenMode.ForRead) as PolylineVertex3d;
                            if (vertex == null || vertex.VertexType != Vertex3dType.SimpleVertex) return null;
                            vertices.Add(Point(vertex.Position));
                        }
                        break;
                    default:
                        return null;
                }
                return ZeroLengthGeometryProof.CreateFailure(measurement.RawValue, kind, vertices,
                    complete: true, simple: true, profileId, failureSource?.Invoke(measurement.Method));
            }
            catch
            {
                // A diagnostic getter is not a new measurement authority.
                // The original failed measurement remains unresolved below.
                return null;
            }
        }

        /// <summary>
        /// Missing bounds do not mean that a separately emitted count/length/area was
        /// not measured. Keep the spatial-evidence failure blocking, and narrow it
        /// only after the exact measurement has become an emitted output row.
        /// No emitted row, failed transform, or mismatched source leaves it global.
        /// </summary>
        internal sealed class ExtentsEvidenceScopes(
            ExtractionResult result, string profileId, string drawingPath,
            string drawingHash, string handlePath, string? xref)
        {
            private Dictionary<QuantityMeasurement, DeliveryFinding>? _failures;

            internal void Capture(QuantityMeasurement measurement, DeliveryFinding failure)
            {
                if (failure.Code != EstimateFindingCodes.MeasurementFailed ||
                    failure.Severity != FindingSeverity.Error || failure.AffectedRecordIds.Count != 0 ||
                    !result.Findings.Contains(failure)) return;
                (_failures ??= new())[measurement] = failure;
            }

            internal void Transfer(QuantityMeasurement original, QuantityMeasurement transformed)
            {
                if (_failures != null && _failures.TryGetValue(original, out var failure))
                    _failures[transformed] = failure;
            }

            internal void BindEmitted(NeutralQuantityRecord record)
            {
                if (_failures == null || !_failures.TryGetValue(record.Measurement, out var failure) ||
                    result.Records.Count == 0 || !ReferenceEquals(result.Records[^1], record) ||
                    !CatalogIdentity.IsValidSha256(drawingHash) || string.IsNullOrWhiteSpace(drawingPath) ||
                    string.IsNullOrWhiteSpace(handlePath) ||
                    !string.Equals(record.RecordId, $"q-disc-{handlePath.Replace('/', '-')}-{record.Measurement.Kind}", StringComparison.Ordinal) ||
                    !string.Equals(record.ProjectProfileId, profileId, StringComparison.Ordinal) ||
                    !string.Equals(record.Source.DrawingPath, drawingPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.Source.DrawingHash, drawingHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.Source.Handle, handlePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.Source.Xref ?? "", xref ?? "", StringComparison.Ordinal) ||
                    record.Provenance == null ||
                    !string.Equals(record.Provenance.SourcePathOrUri, drawingPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.Provenance.DrawingChecksum, drawingHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.Provenance.SourceHandle, handlePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.Provenance.XrefPath ?? "", xref ?? "", StringComparison.Ordinal))
                    return;

                if (!failure.AffectedRecordIds.Contains(record.RecordId, StringComparer.Ordinal))
                    failure.AffectedRecordIds.Add(record.RecordId);
                if (!failure.SourceRefs.Contains(record.Provenance)) failure.SourceRefs.Add(record.Provenance);
                if (!record.Findings.Contains(failure)) record.Findings.Add(failure);
                record.Status = DeliveryStatusRules.CapByFindings(record.Status, record.Findings);
            }
        }

        private static bool IsAmbiguousClosedPolyline(Entity ent) =>
            ent is Polyline { Closed: true } or Polyline2d { Closed: true };

        internal sealed record ClosedPolylineResolution(
            bool IsResolved, string AreaRuleKey, string LengthRuleKey);

        internal static ClosedPolylineResolution ClosedPolylineChoice(
            ProjectProfile profile, string layer, IReadOnlyList<QuantityMeasurement> measurements)
        {
            var area = measurements.Single(m => string.Equals(m.Kind, "area", StringComparison.OrdinalIgnoreCase));
            var length = measurements.Single(m => string.Equals(m.Kind, "length", StringComparison.OrdinalIgnoreCase));
            var discoveredAreaKey = BuildDiscoveryRuleKey(layer, area);
            var discoveredLengthKey = BuildDiscoveryRuleKey(layer, length);
            var areaRule = FindApprovedRule(profile, discoveredAreaKey, layer, "area");
            var lengthRule = FindApprovedRule(profile, discoveredLengthKey, layer, "length");
            // Legacy office profiles may use a named rule whose key predates the
            // layer:<leaf>|<kind> discovery convention.  The exclusion decision must
            // target the rule key that the record will actually carry after matching.
            var areaKey = areaRule?.RuleKey ?? discoveredAreaKey;
            var lengthKey = lengthRule?.RuleKey ?? discoveredLengthKey;
            var areaApproved = areaRule != null;
            var lengthApproved = lengthRule != null;
            var ignored = new HashSet<string>(
                IgnoredRulePolicy.ApprovedKeys(profile), StringComparer.Ordinal);

            // A closed boundary becomes priceable only after one dimensional meaning
            // is approved and the alternative is explicitly marked not relevant.
            // An audited ignore is authoritative even when a legacy profile still
            // carries an old mapping for that ignored sibling.  ApplyIgnoredRules
            // removes it from pricing; requiring the stale mapping itself to be
            // deleted would make a documented recovery impossible after reload.
            var resolved = (areaApproved && ignored.Contains(lengthKey) && !ignored.Contains(areaKey)) ||
                           (lengthApproved && ignored.Contains(areaKey) && !ignored.Contains(lengthKey));
            return new ClosedPolylineResolution(resolved, areaKey, lengthKey);
        }

        /// <summary>
        /// Pure accumulation of exact emitted boundary records. Findings remain
        /// unscoped until the complete traversal finishes; any missing/failed emission
        /// keeps its entire aggregate unscoped. No engineering decision is resolved.
        /// </summary>
        internal sealed class ClosedPolylineAmbiguityScopes
        {
            private sealed class Entry(DeliveryFinding finding)
            {
                internal readonly DeliveryFinding Finding = finding;
                internal readonly HashSet<string> RecordIds = new(StringComparer.Ordinal);
                internal int Pending;
                internal bool Failed;
            }

            private readonly ExtractionResult _result;
            private readonly Dictionary<(string Area, string Length), Entry> _entries = new();
            private bool _published;
            internal ClosedPolylineAmbiguityScopes(ExtractionResult result) => _result = result;

            internal Action Begin(string projectProfileId, string layer, string areaRuleKey, string lengthRuleKey,
                string drawingPath, string drawingHash, string handlePath, string? xrefChain)
            {
                if (_published) throw new InvalidOperationException("Closed-boundary scope has already been published.");
                var key = (areaRuleKey, lengthRuleKey);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    var finding = new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.MixedDimensionLayer,
                        Domain = "estimate",
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"פוליליין סגור בשכבה '{Bidi.Ltr(layer)}' יכול להיות שטח או היקף",
                        Message = $"{areaRuleKey} <> {lengthRuleKey}",
                        RecommendedAction = $"יש לאשר מיפוי לאחת החלופות ({areaRuleKey} / {lengthRuleKey}) ולסמן את החלופה השנייה 'לא רלוונטי'.",
                        ProjectProfileId = projectProfileId,
                    };
                    entry = new Entry(finding);
                    _entries.Add(key, entry);
                    _result.Findings.Add(finding);
                }
                entry.Pending++;
                var firstRecord = _result.Records.Count;
                var completed = false;
                return () =>
                {
                    if (_published || completed)
                        throw new InvalidOperationException("Closed-boundary emission cannot be completed twice or after publication.");
                    completed = true;
                    entry.Pending--;
                    if (_result.Records.Count - firstRecord != 2)
                    {
                        entry.Failed = true;
                        return;
                    }
                    var pair = new[] { _result.Records[firstRecord], _result.Records[firstRecord + 1] };
                    var area = pair.FirstOrDefault(record => record.Measurement.Kind == "area");
                    var length = pair.FirstOrDefault(record => record.Measurement.Kind == "length");
                    bool ExactRecord(NeutralQuantityRecord record, string ruleKey, string kind, UnitDimension dimension) =>
                        !string.IsNullOrWhiteSpace(drawingPath) && CatalogIdentity.IsValidSha256(drawingHash) &&
                        !string.IsNullOrWhiteSpace(handlePath) &&
                        string.Equals(record.ProjectProfileId, projectProfileId, StringComparison.Ordinal) &&
                        record.RecordId == $"q-disc-{handlePath.Replace('/', '-')}-{kind}" &&
                        string.Equals(record.Source.DrawingPath, drawingPath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(record.Source.DrawingHash, drawingHash, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(record.Source.Handle, handlePath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(record.Source.Xref, xrefChain, StringComparison.Ordinal) &&
                        string.Equals(record.Classification.RuleKey, ruleKey, StringComparison.Ordinal) &&
                        ClosedPolylineAlternativePolicy.IsClosedPolylineMeasurement(record) &&
                        double.IsFinite(record.Measurement.RawValue) && record.Measurement.RawValue > 0 &&
                        Units.Parse(record.Measurement.Unit).Dimension == dimension;
                    if (area == null || length == null ||
                        !ExactRecord(area, areaRuleKey, "area", UnitDimension.Area) ||
                        !ExactRecord(length, lengthRuleKey, "length", UnitDimension.Length))
                    {
                        entry.Failed = true;
                        return;
                    }
                    entry.RecordIds.Add(area.RecordId);
                    entry.RecordIds.Add(length.RecordId);
                };
            }

            internal void Publish()
            {
                if (_published) return;
                _published = true;
                foreach (var entry in _entries.Values)
                    if (!entry.Failed && entry.Pending == 0 && entry.RecordIds.Count > 0)
                        entry.Finding.AffectedRecordIds.AddRange(entry.RecordIds.OrderBy(id => id, StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// Entity types whose model-space presence is presentation/reference metadata,
        /// not a construction quantity. They are reported as informational coverage,
        /// while any unrecognized geometry remains a global ReviewRequired blocker.
        /// Common measurable construction objects (spline, region, feature line,
        /// pipe and structure) are handled by the natural adapters above.
        /// </summary>
        internal static bool IsKnownNonQuantityEntityType(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return false;
            var type = typeName.Trim().ToUpperInvariant();
            if (type.EndsWith("DIMENSION", StringComparison.Ordinal)) return true;
            return type is
                "DBTEXT" or "MTEXT" or
                "ATTRIBUTEDEFINITION" or "ATTRIBUTEREFERENCE" or
                "LEADER" or "MLEADER" or "TABLE" or
                "WIPEOUT" or "RASTERIMAGE" or "OLE2FRAME" or
                "PDFREFERENCE" or "DGNREFERENCE" or "DWFREFERENCE" or
                "VIEWPORT" or
                // Civil model controllers/presentation objects. Measurable network
                // parts and feature lines are intentionally not in this list.
                "ALIGNMENT" or "PROFILE" or "PROFILEVIEW" or "SECTIONVIEW" or
                "SAMPLELINE" or "SAMPLELINEGROUP" or "CORRIDOR" or
                // Exact Autodesk annotation/display types observed in the native scan;
                // do not infer nonquantity semantics from a Label/Group/View suffix.
                // Sample-line groups still undergo the independent model quantity and
                // earthworks coverage checks in CorridorQuantityService.Collect.
                "PROFILEDATABANDLABELGROUP" or "ALIGNMENTSTATIONLABELGROUP" or
                "HORIZONTALGEOMETRYBANDLABELGROUP" or "VERTICALGEOMETRYBANDLABELGROUP" or
                "PROFILECRESTCURVELABELGROUP" or "PROFILELINELABELGROUP" or
                "PROFILEPVILABELGROUP" or "PROFILESAGCURVELABELGROUP" or
                "SUPERELEVATIONVIEW" or "ALIGNMENTGEOMETRYPOINTLABELGROUP" or
                "ALIGNMENTSTATIONEQUATIONLABELGROUP" or "STATIONELEVATIONLABEL" or
                "TINSURFACE" or "GRIDSURFACE" or "TINVOLUMESURFACE";
        }

        /// <summary>
        /// Discovery approvals are scoped to a stable measured subject. Count groups
        /// include the normalized block name, so BENCH_A and LIGHT_POLE on layer 0-FURN
        /// cannot accidentally share one catalog mapping.
        /// </summary>
        internal static string BuildDiscoveryRuleKey(string layer, QuantityMeasurement measurement)
        {
            // One approval classifies the same office layer in host geometry and in
            // any XREF name. XREF qualification remains in Source/Provenance; approval
            // identity deliberately uses the supplier's leaf layer.
            var key = $"layer:{SectionProjectionLogic.LayerLeaf(layer)}|{measurement.Kind}";
            if (!string.Equals(measurement.Kind, "count", StringComparison.OrdinalIgnoreCase))
                return key;

            var parameter = measurement.Parameters.TryGetValue("block_name", out var blockName) &&
                            !string.IsNullOrWhiteSpace(blockName)
                ? (Prefix: "block", Name: blockName)
                : measurement.Parameters.TryGetValue("object_name", out var objectName) &&
                  !string.IsNullOrWhiteSpace(objectName)
                    ? (Prefix: "name", Name: objectName)
                    : default;
            if (string.IsNullOrWhiteSpace(parameter.Name)) return key;

            var normalized = string.Join("_", parameter.Name.Trim()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();
            return $"{key}|{parameter.Prefix}:{Uri.EscapeDataString(normalized)}";
        }

        internal static DeliveryFinding? UnsupportedEntityCoverageFinding(
            IReadOnlyDictionary<string, int> unsupportedEntityTypes,
            string projectProfileId,
            IEnumerable<ProvenanceRef>? sourceRefs = null)
        {
            if (unsupportedEntityTypes.Count == 0) return null;
            var total = unsupportedEntityTypes.Values.Sum();
            var summary = string.Join(", ", unsupportedEntityTypes
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            return new DeliveryFinding
            {
                Code = EstimateFindingCodes.UnsupportedEntityCoverage,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = $"{total} עצמים מסוגים שאינם נתמכים לא נמדדו",
                Message = summary,
                // Identity is evidence for locating the unmeasured object, not an
                // approval, a quantity or a narrower scope. Missing context never
                // removes its type count or changes this global blocker.
                SourceRefs = (sourceRefs ?? Enumerable.Empty<ProvenanceRef>())
                    .Distinct<ProvenanceRef>(ReferenceEqualityComparer.Instance)
                    .DistinctBy(source => System.Text.Json.JsonSerializer.Serialize(source)).ToList(),
                RecommendedAction = "יש לבדוק אם הסוגים שלא נמדדו מכילים עבודות לתמחור; עד להכרעה האומדן חסום לייצוא.",
                ProjectProfileId = projectProfileId,
            };
        }

        internal static DeliveryFinding? KnownNonQuantityCoverageFinding(
            IReadOnlyDictionary<string, int> skippedEntityTypes,
            string projectProfileId)
        {
            if (skippedEntityTypes.Count == 0) return null;
            var total = skippedEntityTypes.Values.Sum();
            var summary = string.Join(", ", skippedEntityTypes
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            return new DeliveryFinding
            {
                Code = EstimateFindingCodes.NonQuantityEntityTypesSkipped,
                Domain = "estimate",
                Severity = FindingSeverity.Info,
                Title = $"{total} עצמי הערות/תצוגה/ייחוס לא נמדדו ככמויות בנייה",
                Message = summary,
                RecommendedAction = "אין צורך בפעולה; הסוגים מפורטים בקובץ הביקורת.",
                ProjectProfileId = projectProfileId,
            };
        }

        private static bool TryInspectActiveSpatialClip(
            Transaction tr,
            BlockReference reference,
            out bool active,
            out string? failure)
        {
            active = false;
            failure = null;
            try
            {
                if (reference.ExtensionDictionary.IsNull) return true;
                if (tr.GetObject(reference.ExtensionDictionary, OpenMode.ForRead) is not
                    DBDictionary extensionDictionary)
                {
                    failure = "extension dictionary is not a DBDictionary";
                    return false;
                }
                if (!extensionDictionary.Contains("ACAD_FILTER")) return true;
                if (tr.GetObject(extensionDictionary.GetAt("ACAD_FILTER"), OpenMode.ForRead) is not
                    DBDictionary filterDictionary)
                {
                    failure = "ACAD_FILTER is not a DBDictionary";
                    return false;
                }
                if (!filterDictionary.Contains("SPATIAL")) return true;
                if (tr.GetObject(filterDictionary.GetAt("SPATIAL"), OpenMode.ForRead) is not
                    SpatialFilter spatialFilter)
                {
                    failure = "SPATIAL filter is not a SpatialFilter";
                    return false;
                }
                active = spatialFilter.Definition.Enabled;
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        private static XrefQuantityPolicy.SourceSnapshotIdentity SnapshotIdentity(Database database) =>
            new(database.FingerprintGuid, database.VersionGuid,
                database.NumberOfSaves, database.Tduupdate.Ticks);

        private static bool TryReadDwgSnapshotIdentity(
            string drawingPath,
            out XrefQuantityPolicy.SourceSnapshotIdentity identity,
            out string? failure)
        {
            identity = null!;
            failure = null;
            try
            {
                // Header variables only; disposing releases the file without paging
                // every object of the XREF into memory (see SectionXrefSnapshotGuard).
                using var diskDatabase = new Database(false, true);
                diskDatabase.ReadDwgFile(
                    drawingPath, FileShare.ReadWrite, allowCPConversion: false, password: null);
                identity = SnapshotIdentity(diskDatabase);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        private static XrefQuantityPolicy.TransformCheck ValidateExternalTransform(
            Matrix3d transform)
        {
            var x = Vector3d.XAxis.TransformBy(transform);
            var y = Vector3d.YAxis.TransformBy(transform);
            var z = Vector3d.ZAxis.TransformBy(transform);
            return XrefQuantityPolicy.ValidateSimilarity(
                new[] { x.X, x.Y, x.Z },
                new[] { y.X, y.Y, y.Z },
                new[] { z.X, z.Y, z.Z });
        }

        private static double BoundaryRecoveryMaximumScale(Matrix3d transform)
        {
            if (!ValidateExternalTransform(transform).IsSafe) return double.NaN;
            // ||M||2 <= sqrt(||M||1 * ||M||infinity), rounded outward at every
            // operation. Tight for identity/axis scaling; includes the tiny
            // differences admitted by the existing similarity validator. This
            // is only a precision bound, never the quantity conversion factor.
            var x = Vector3d.XAxis.TransformBy(transform);
            var y = Vector3d.YAxis.TransformBy(transform);
            var z = Vector3d.ZAxis.TransformBy(transform);
            static double L1(double a, double b, double c) =>
                Math.BitIncrement(Math.BitIncrement(Math.Abs(a) + Math.Abs(b)) + Math.Abs(c));
            var columns = Math.Max(L1(x.X, x.Y, x.Z), Math.Max(L1(y.X, y.Y, y.Z), L1(z.X, z.Y, z.Z)));
            var rows = Math.Max(L1(x.X, y.X, z.X), Math.Max(L1(x.Y, y.Y, z.Y), L1(x.Z, y.Z, z.Z)));
            return Math.BitIncrement(Math.Sqrt(Math.BitIncrement(columns * rows)));
        }

        private static QuantityMeasurement? TransformNestedMeasurement(
            Entity entity,
            QuantityMeasurement measurement,
            Matrix3d transform,
            DrawingUnitPolicy.Scale drawingUnits,
            List<DeliveryFinding> findings,
            string projectProfileId,
            string? xrefChain,
            string handlePath,
            string transformLabel,
            Func<string, ProvenanceRef>? failureSource = null)
        {
            var check = ValidateExternalTransform(transform);
            if (!check.IsSafe &&
                !string.Equals(measurement.Kind, "count", StringComparison.OrdinalIgnoreCase))
            {
                AddXrefTraversalFinding(findings, projectProfileId,
                    EstimateFindingCodes.XrefTransformInvalid,
                    "טרנספורמציית Block/XREF אינה משמרת מדידת אורך/שטח — האומדן חסום",
                    $"xref={xrefChain}; handle_path={handlePath}; reason={check.Failure}");
                return null;
            }

            var factor = measurement.Kind.ToLowerInvariant() switch
            {
                "length" => check.LengthScale,
                "area" => check.LengthScale * check.LengthScale,
                "volume" => check.LengthScale * check.LengthScale * check.LengthScale,
                "count" => 1.0,
                _ => double.NaN,
            };
            if (!double.IsFinite(factor) || factor <= 0)
            {
                AddXrefTraversalFinding(findings, projectProfileId,
                    EstimateFindingCodes.XrefTransformInvalid,
                    "לא ניתן להחיל טרנספורמציית XREF על סוג המדידה — האומדן חסום",
                    $"xref={xrefChain}; handle_path={handlePath}; kind={measurement.Kind}");
                return null;
            }

            double[]? bounds = null;
            if (measurement.GeometryEvidence != null)
            {
                try
                {
                    var ext = entity.GeometricExtents;
                    var corners = new[]
                    {
                        new Point3d(ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z),
                        new Point3d(ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.Z),
                        new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z),
                        new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z),
                        new Point3d(ext.MaxPoint.X, ext.MinPoint.Y, ext.MinPoint.Z),
                        new Point3d(ext.MaxPoint.X, ext.MinPoint.Y, ext.MaxPoint.Z),
                        new Point3d(ext.MaxPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z),
                        new Point3d(ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z),
                    }.Select(point => point.TransformBy(transform)).ToList();
                    bounds = drawingUnits.Bounds(new[]
                    {
                        corners.Min(point => point.X), corners.Min(point => point.Y),
                        corners.Max(point => point.X), corners.Max(point => point.Y),
                    });
                }
                catch (Exception ex)
                {
                    findings.Add(MeasurementFailure(projectProfileId,
                        $"Failed to transform XREF extents for {handlePath}", ex.Message,
                        failureSource?.Invoke(transformLabel + "-extents"), measurement.Kind));
                    return null;
                }
            }

            var value = measurement.RawValue * factor;
            if (!double.IsFinite(value) || value <= 0)
            {
                findings.Add(MeasurementFailure(projectProfileId,
                    $"Invalid transformed XREF quantity for {handlePath}",
                    $"raw={measurement.RawValue:R}; factor={factor:R}",
                    failureSource?.Invoke(measurement.Method + "+" + transformLabel), measurement.Kind));
                return null;
            }

            return new QuantityMeasurement
            {
                Kind = measurement.Kind,
                Method = measurement.Method + "+" + transformLabel,
                RawValue = value,
                Unit = measurement.Unit,
                GeometryEvidence = bounds,
                Parameters = new Dictionary<string, string>(
                    measurement.Parameters, StringComparer.Ordinal),
            };
        }

        private static string AppendXrefChain(string? parent, string child) =>
            string.IsNullOrWhiteSpace(parent) ? child : parent + " > " + child;

        internal static string AppendReferenceHandlePath(string? parent, string child) =>
            string.IsNullOrWhiteSpace(parent) ? child : parent + "/" + child;

        private static void AddXrefTraversalFinding(
            ICollection<DeliveryFinding> findings,
            string projectProfileId,
            string code,
            string title,
            string message)
        {
            findings.Add(new DeliveryFinding
            {
                Code = code,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = title,
                Message = message,
                RecommendedAction = "יש לתקן/לרענן את שרשרת ה-XREF ולבצע סריקת אומדן חדשה.",
                ProjectProfileId = projectProfileId,
            });
        }

        /// <summary>
        /// Civil 3D names its presentation layers predictably. None of these carry
        /// construction geometry, so none may become an estimate line.
        /// </summary>
        /// <summary>
        /// Finds the approved rule that classifies a discovered object: exact rule key
        /// first (how approvals are saved), then layer pattern + measurement kind.
        /// </summary>
        internal sealed record ApprovedRuleResolution(
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule? Rule,
            IReadOnlyList<ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule> Matches)
        {
            internal bool IsAmbiguous => Matches.Count > 1;
        }

        internal static ApprovedRuleResolution ResolveApprovedRule(
            ProjectProfile profile, string ruleKey, string layer, string kind)
        {
            var rules = profile.Estimate.QuantitySources.Rules ?? new();
            if (rules.Count == 0) return new ApprovedRuleResolution(null, Array.Empty<
                ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule>());
            var matches = rules.Where(r => IsExplicitlyApproved(profile, r) &&
                    string.Equals(r.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // A count approval is for one named block. Falling back to layer+kind here
            // would remap every other block on that layer with the first approval.
            var countIsNameScoped = string.Equals(kind, "count", StringComparison.OrdinalIgnoreCase) &&
                (ruleKey.Contains("|block:", StringComparison.OrdinalIgnoreCase) ||
                 ruleKey.Contains("|name:", StringComparison.OrdinalIgnoreCase));
            if (!countIsNameScoped)
            {
                foreach (var r in rules)
                {
                    if (matches.Contains(r) || !IsExplicitlyApproved(profile, r)) continue;
                    if (!string.Equals(r.MeasurementKind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(r.LayerPattern) &&
                        (ClInstructionReader.WildcardMatch(layer, r.LayerPattern) ||
                         ClInstructionReader.WildcardMatch(
                             SectionProjectionLogic.LayerLeaf(layer), r.LayerPattern))) matches.Add(r);
                }
            }
            return new ApprovedRuleResolution(matches.Count == 1 ? matches[0] : null, matches);
        }

        internal static ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule? FindApprovedRule(
            ProjectProfile profile, string ruleKey, string layer, string kind) =>
            ResolveApprovedRule(profile, ruleKey, layer, kind).Rule;

        private static void AddAmbiguousRuleFinding(
            List<DeliveryFinding> findings, string projectProfileId,
            string ruleKey, string layer, string kind,
            IReadOnlyList<ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule> matches)
        {
            var ids = string.Join(", ", matches.Select(r => r.RuleKey ?? "(missing)"));
            var title = $"יותר מחוק כמות מאושר אחד מתאים ל-{ruleKey}";
            if (findings.Any(f => f.Code == EstimateFindingCodes.ConfigurationAmbiguous &&
                                  string.Equals(f.Title, title, StringComparison.Ordinal))) return;
            findings.Add(new DeliveryFinding
            {
                Code = EstimateFindingCodes.ConfigurationAmbiguous,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = title,
                Message = $"layer='{layer}', kind='{kind}', matching rules: {ids}. No mapping was selected.",
                RecommendedAction = "יש לצמצם את דפוסי השכבות או להסיר אישור מתחרה ולהריץ סריקה חדשה.",
                ProjectProfileId = projectProfileId,
            });
        }

        internal static bool IsExplicitlyApproved(
            ProjectProfile profile,
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule rule) =>
            CatalogIdentity.IsApprovalCurrent(profile, rule);

        internal static bool HasExplicitApprovedRuleForEntity(ProjectProfile profile, Entity ent) =>
            profile.Estimate.QuantitySources.Rules.Any(rule =>
                IsExplicitlyApproved(profile, rule) &&
                !string.IsNullOrWhiteSpace(rule.LayerPattern) &&
                (ClInstructionReader.WildcardMatch(ent.Layer, rule.LayerPattern) ||
                 ClInstructionReader.WildcardMatch(
                     SectionProjectionLogic.LayerLeaf(ent.Layer), rule.LayerPattern)) &&
                EntityMatchesType(ent, rule.EntityType));

        internal static bool IsCivilPresentationLayer(string? layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            var l = SectionProjectionLogic.LayerLeaf(layer).ToUpperInvariant();
            // Mahod's own tools (Intergreen, Parking, this one) draw markers on
            // Mahod_* / MCD* layers - engineering aids, never construction quantities.
            if (l.StartsWith("MAHOD_") || l.StartsWith("MHD-") ||
                l.StartsWith("MCD-") || l.StartsWith("MCDV-")) return true;
            return l.EndsWith("_SECTIONVIEW") || l.EndsWith("_PROFILEVIEW") ||
                   l.Contains("SECTIONVIEW") || l.Contains("PROFILEVIEW") ||
                   l.Contains("_BAND") || l.Contains("-BAND") ||
                   l.Contains("LABEL") || l.Contains("_GRID") || l.Contains("-GRID") ||
                   l.StartsWith("C-ROAD-SCTN") || l.StartsWith("C-ROAD-PROF");
        }

        private static bool EntityMatchesType(Entity ent, string? ruleType)
        {
            if (string.IsNullOrWhiteSpace(ruleType)) return false;
            return ruleType.ToUpperInvariant() switch
            {
                "LWPOLYLINE" or "POLYLINE" => ent is Polyline or Polyline2d or Polyline3d,
                "LINE" => ent is Line,
                "HATCH" => ent is Hatch,
                "BLOCK" or "BLOCKREFERENCE" or "INSERT" => ent is BlockReference,
                "ARC" => ent is Arc,
                "CIRCLE" => ent is Circle,
                "SPLINE" => ent is Spline,
                "REGION" => ent is AcadRegion,
                "FEATURELINE" or "FEATURE LINE" => ent is CivilDb.FeatureLine,
                "PIPE" => ent is CivilDb.Pipe,
                "STRUCTURE" => ent is CivilDb.Structure,
                _ => false,
            };
        }

        private static QuantityMeasurement? Measure(
            Entity ent,
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule rule,
            List<DeliveryFinding> findings,
            string projectProfileId,
            DrawingUnitPolicy.Scale drawingUnits)
        {
            double[]? bbox = null;
            try
            {
                var ext = ent.GeometricExtents;
                bbox = drawingUnits.Bounds(new[] { ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y });
            }
            catch (Exception ex)
            {
                findings.Add(MeasurementFailure(
                    projectProfileId, $"Failed to read extents for {SafeEntityId(ent)}", ex.Message));
            }

            try
            {
                switch (rule.MeasurementKind?.ToLowerInvariant())
                {
                    case "length":
                        double? length = ent switch
                        {
                            Polyline pl => pl.Length,
                            Polyline2d p2 => CurveLength(p2),
                            Polyline3d p3 => CurveLength(p3),
                            Line l => l.Length,
                            Arc a => a.Length,
                            Circle c => 2.0 * Math.PI * c.Radius,
                            Spline s => CurveLength(s),
                            CivilDb.FeatureLine f => f.Length2D,
                            CivilDb.Pipe p => p.Length2D,
                            _ => null,
                        };
                        if (length == null) return null;
                        return new QuantityMeasurement
                        {
                            Kind = "length",
                            Method = $"{ent.GetType().Name.ToLowerInvariant()}-length",
                            RawValue = drawingUnits.Length(length.Value),
                            Unit = drawingUnits.LengthUnit,
                            GeometryEvidence = bbox,
                        };

                    case "area":
                        double? area = ent switch
                        {
                            Hatch h => h.Area,
                            Polyline { Closed: true } pl => pl.Area,
                            Polyline2d { Closed: true } p2 => p2.Area,
                            AcadRegion r => r.Area,
                            _ => null,
                        };
                        if (area == null)
                        {
                            if (ent is Polyline { Closed: false })
                            {
                                findings.Add(new DeliveryFinding
                                {
                                    Code = EstimateFindingCodes.MeasurementFailed,
                                    Domain = "estimate",
                                    Severity = FindingSeverity.ReviewRequired,
                                    Title = $"Area rule '{rule.RuleKey}' matched an OPEN polyline ({ent.Handle}) — area undefined",
                                    ProjectProfileId = projectProfileId,
                                });
                            }
                            return null;
                        }
                        return new QuantityMeasurement
                        {
                            Kind = "area",
                            Method = ent switch
                            {
                                Hatch => "hatch-area",
                                AcadRegion => "region-area",
                                _ => "closed-polyline-area",
                            },
                            RawValue = drawingUnits.Area(area.Value),
                            Unit = drawingUnits.AreaUnit,
                            GeometryEvidence = bbox,
                        };

                    case "count":
                        if (ent is CivilDb.Structure structure)
                        {
                            var structureName = structure.Name;
                            if (string.IsNullOrWhiteSpace(structureName))
                                throw new InvalidDataException(
                                    $"Structure {SafeEntityId(structure)} has no readable name.");
                            return new QuantityMeasurement
                            {
                                Kind = "count",
                                Method = "structure-count",
                                RawValue = 1,
                                Unit = "יח'",
                                GeometryEvidence = bbox,
                                Parameters = { ["object_name"] = structureName },
                            };
                        }
                        if (ent is not BlockReference brf) return null;
                        string blockName;
                        try
                        {
                            blockName = brf.Name;
                            if (rule.Notes != null && rule.Notes.StartsWith("block:") &&
                                !ClInstructionReader.WildcardMatch(blockName, rule.Notes[6..]))
                                return null;
                        }
                        catch (Exception ex)
                        {
                            findings.Add(MeasurementFailure(
                                projectProfileId, $"Failed to read block name for {SafeEntityId(brf)}", ex.Message));
                            return null;
                        }
                        return new QuantityMeasurement
                        {
                            Kind = "count",
                            Method = "block-count",
                            RawValue = 1,
                            Unit = "יח'",
                            GeometryEvidence = bbox,
                            Parameters = { ["block_name"] = blockName },
                        };

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MeasurementFailed,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = $"המדידה נכשלה לעצם {ent.Handle} לפי החוק '{rule.RuleKey}'",
                    Message = ex.Message,
                    ProjectProfileId = projectProfileId,
                });
                return null;
            }
        }

        private static DeliveryFinding MeasurementFailure(
            string? projectProfileId, string title, string? message = null,
            ProvenanceRef? source = null, string? measurementKind = null) =>
            MeasurementFailureProvenance.Create(projectProfileId, title, message, source, measurementKind);

        internal static void ConsolidateMeasurementFailures(
            ExtractionResult result, string projectProfileId)
        {
            var failures = result.Findings
                .Where(f => string.Equals(
                    f.Code, EstimateFindingCodes.MeasurementFailed, StringComparison.Ordinal))
                .ToList();
            if (failures.Count == 0) return;

            result.Findings.RemoveAll(f => string.Equals(
                f.Code, EstimateFindingCodes.MeasurementFailed, StringComparison.Ordinal));
            // An unscoped failure must remain global even when other failures have
            // proven output rows. Unioning all IDs into one aggregate would narrow
            // e.g. 73 unmeasured/zero objects to the 17 extents-only count records.
            foreach (var partition in failures.GroupBy(failure =>
                         failure.AffectedRecordIds.Count > 0 &&
                         failure.AffectedRecordIds.All(id => !string.IsNullOrWhiteSpace(id))))
            {
                var items = partition.ToList();
                var affected = partition.Key
                    ? items.SelectMany(failure => failure.AffectedRecordIds).Distinct(StringComparer.Ordinal).ToList()
                    : new List<string>();
                result.Findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MeasurementFailed,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = partition.Key
                        ? $"{items.Count} measurement-evidence failures affect {affected.Count} emitted quantity records — estimate export is blocked"
                        : $"{items.Count} supported construction objects could not be measured — estimate export is blocked",
                    Message = $"Complete measurement failures ({items.Count}):\n" + string.Join("\n", items
                        .Select(f => string.IsNullOrWhiteSpace(f.Message)
                            ? f.Title
                            : $"{f.Title}: {f.Message}")),
                    SourceRefs = items.SelectMany(f => f.SourceRefs).ToList(),
                    AffectedRecordIds = affected,
                    EvidenceRefs = items.SelectMany(f => f.EvidenceRefs).Distinct(StringComparer.Ordinal).ToList(),
                    RecommendedAction = "Repair the unreadable drawing objects and run a fresh quantity scan.",
                    ProjectProfileId = projectProfileId,
                });
            }
        }

        private static string SafeEntityId(Entity ent)
        {
            try { return $"{ent.GetType().Name} {ent.Handle}"; }
            catch { return ent.GetType().Name; }
        }

        internal static string HashDrawingFile(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) || !File.Exists(path)
                    ? string.Empty : ArtifactHash.Sha256OfFile(path);
            }
            catch { return string.Empty; }
        }

        internal static DeliveryFinding? DrawingSourceHashFinding(
            string drawingName, string? drawingHash, string projectProfileId) =>
            CatalogIdentity.IsValidSha256(drawingHash)
                ? null
                : new DeliveryFinding
                {
                    Code = EstimateFindingCodes.SourceMissing,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "לא ניתן לקשור את הכמויות ל-SHA-256 של קובץ DWG שמור",
                    Message = $"Drawing: {drawingName}. Unsaved drawings and unreadable source bytes are audit-only; a path or live object id is not source identity.",
                    RecommendedAction = "Save the drawing to a readable DWG file and run a fresh quantity scan.",
                    ProjectProfileId = projectProfileId,
                };

        /// <summary>
        /// Rules 2.8 (BOQ-N1): a block definition as the approved physical footprints name it — DXF entity counts, plan envelope
        /// (union of the definition entities' extents, block units), base point and units code, as JSON. Null when any entity has
        /// no extents: an incomplete definition never matches an approved one. Read only; nothing is opened for write.
        /// </summary>
        internal static string? BlockDefinitionSignature(ObjectId definitionId, Transaction tr)
        {
            try
            {
                if (tr.GetObject(definitionId, OpenMode.ForRead, false) is not BlockTableRecord btr) return null;
                var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var descriptors = new List<string?>();
                double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
                static double[] P(Point3d p) => new[] { p.X, p.Y, p.Z };
                static double[] V(Vector3d v) => new[] { v.X, v.Y, v.Z };
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity) continue;
                    var dxf = entity.GetRXClass().DxfName;
                    counts[dxf] = counts.TryGetValue(dxf, out var n) ? n + 1 : 1;
                    // The reference's descriptors (boq_geometry.entity_descriptor), from the stored values; another type: unknown.
                    descriptors.Add(entity switch
                    {
                        Line l => BlockDefinitionDigest.Line(P(l.StartPoint), P(l.EndPoint)),
                        Arc a => BlockDefinitionDigest.Arc(P(a.Center), a.Radius, a.StartAngle, a.EndAngle, V(a.Normal)),
                        Circle c => BlockDefinitionDigest.Circle(P(c.Center), c.Radius, V(c.Normal)),
                        Ellipse e => BlockDefinitionDigest.Ellipse(P(e.Center), V(e.MajorAxis), e.RadiusRatio, e.StartParam, e.EndParam, V(e.Normal)),
                        Polyline pl => BlockDefinitionDigest.LwPolyline(pl.Closed, pl.Elevation, V(pl.Normal), pl.ConstantWidth,
                            Enumerable.Range(0, pl.NumberOfVertices).Select(i => (pl.GetPoint2dAt(i).X, pl.GetPoint2dAt(i).Y, pl.GetBulgeAt(i),
                                pl.GetStartWidthAt(i), pl.GetEndWidthAt(i))).ToList()),
                        _ => null,
                    });
                    var extents = entity.GeometricExtents;
                    x0 = Math.Min(x0, extents.MinPoint.X);
                    y0 = Math.Min(y0, extents.MinPoint.Y);
                    x1 = Math.Max(x1, extents.MaxPoint.X);
                    y1 = Math.Max(y1, extents.MaxPoint.Y);
                }
                if (counts.Count == 0 || !double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1)) return null;
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    dxf_counts = counts,
                    envelope = new[] { x0, y0, x1, y1 },
                    base_point = new[] { btr.Origin.X, btr.Origin.Y, btr.Origin.Z },
                    units_code = (int)btr.Units,
                    geometry_digest = BlockDefinitionDigest.Digest(descriptors),
                });
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
