using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Imports the two approved Mahod vehicle elevations carried inside the plug-in
    /// assembly and places a normalized block reference in a SectionView.
    ///
    /// Only references created by SectionDecorationService are returned. They are put
    /// on MHD-SECT-ANNO and registered by SectionAnnotationRegistry with the rest of
    /// that section's annotations. Existing/manual references are never modified or
    /// erased. Complete reference counts and a non-mutating Purge query may prove an
    /// unused tool definition eligible for atomic rename-and-reimport recovery.
    /// </summary>
    internal sealed class SectionVehicleBlockService
    {
        internal sealed record Placement(
            Point3d Position, Scale3d ScaleFactors, double Rotation);

        internal sealed record AssetFailure(
            SectionFurnitureLogic.OfficeCarView View,
            string ResourceName,
            string Error);

        private sealed record Asset(
            SectionFurnitureLogic.OfficeCarView View,
            string ResourceName,
            string Sha256,
            string BlockName);

        private sealed record Definition(Asset Asset, ObjectId BlockId, string? Error)
        {
            internal bool IsAvailable => !BlockId.IsNull && string.IsNullOrWhiteSpace(Error);
        }

        internal const string RearResource =
            "MahodAI.Civil3D.Plugin.assets.sections.HW-CARFRBK-01.dwg";
        internal const string FrontResource =
            "MahodAI.Civil3D.Plugin.assets.sections.HW-CARFRFRW-01.dwg";

        private static readonly IReadOnlyList<Asset> Assets = new[]
        {
            new Asset(
                SectionFurnitureLogic.OfficeCarView.Rear,
                RearResource,
                SectionOfficeVehicleAssetEvidenceLogic.RearSha256,
                SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(
                    SectionFurnitureLogic.OfficeCarView.Rear)),
            new Asset(
                SectionFurnitureLogic.OfficeCarView.Front,
                FrontResource,
                SectionOfficeVehicleAssetEvidenceLogic.FrontSha256,
                SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(
                    SectionFurnitureLogic.OfficeCarView.Front)),
        };

        private readonly IReadOnlyDictionary<SectionFurnitureLogic.OfficeCarView, Definition> _definitions;

        private SectionVehicleBlockService(
            IReadOnlyDictionary<SectionFurnitureLogic.OfficeCarView, Definition> definitions)
        {
            _definitions = definitions;
        }

        internal IReadOnlyList<AssetFailure> Failures => _definitions.Values
            .Where(d => !d.IsAvailable)
            .Select(d => new AssetFailure(d.Asset.View, d.Asset.ResourceName,
                d.Error ?? "Office block definition is unavailable."))
            .ToList();

        /// <summary>
        /// Verifies and imports each embedded source independently. A damaged/missing
        /// office car block is recorded here; any section that requires that view is
        /// then rejected by the caller.  A schematic car is never substituted for an
        /// approved office asset.
        /// </summary>
        internal static SectionVehicleBlockService Load(
            Transaction tr, Database targetDb, StageLog? log, bool allowModify = true)
        {
            var loaded = new Dictionary<SectionFurnitureLogic.OfficeCarView, Definition>();
            var diagnostics = SectionOfficeBlockFingerprintDiagnostics.TryCreate(targetDb, log: log);
            foreach (var asset in Assets)
            {
                var snapshots = diagnostics == null ? null :
                    new List<SectionOfficeBlockFingerprintEvidence.Snapshot>();
                try
                {
                    log?.Begin("decorate.vehicle_block.import", asset.View.ToString());
                    var id = ImportVerifiedEmbeddedDwg(tr, targetDb, asset, allowModify, snapshots);
                    loaded[asset.View] = new Definition(asset, id, null);
                    diagnostics?.Write(asset.View, asset.ResourceName, "load", "validated", snapshots!);
                    log?.End("decorate.vehicle_block.import",
                        $"view={asset.View} block={asset.BlockName} hash={asset.Sha256}");
                }
                catch (Exception ex)
                {
                    diagnostics?.Write(asset.View, asset.ResourceName, "load", "failed", snapshots!, ex);
                    loaded[asset.View] = new Definition(asset, ObjectId.Null, ex.Message);
                    log?.End("decorate.vehicle_block.import",
                        $"FAILED view={asset.View} resource={asset.ResourceName}");
                    log?.Info($"decorate.vehicle_block.unavailable view={asset.View} " +
                              $"resource={asset.ResourceName} error={ex.Message}");
                }
            }
            return new SectionVehicleBlockService(loaded);
        }

        /// <summary>
        /// Creates (but does not append) one office block reference, fitted to the
        /// metric vehicle envelope after the SectionView's own transformation. The
        /// caller owns/persists the returned reference with its normal annotation set.
        /// </summary>
        /// <summary>
        /// Direction-aware placement boundary. The supplied plan must carry either
        /// approved-arrow or durable-manual provenance and must agree with the Civil
        /// viewing convention (along alignment = rear, against = front).
        /// </summary>
        internal bool TryCreateCarReference(
            Transaction tr,
            CivilDb.SectionView view,
            SectionFurnitureLogic.VehicleSpec spec,
            double midOffset,
            double groundElevation,
            SectionVehicleDirectionPlanner.DirectionPlan direction,
            out BlockReference? reference,
            out string evidence,
            out string? error)
        {
            reference = null;
            error = null;
            evidence = string.Empty;
            if (!direction.IsResolved ||
                direction.EvidenceMode !=
                    SectionVehicleDirectionPlanner.ArrowEvidenceMode.MotorTraffic ||
                direction.OfficeCarView is not { } officeView ||
                !SectionFurnitureLogic.TryOfficeCarViewForFlow(direction.Flow, out var expectedView) ||
                officeView != expectedView)
            {
                error = "Vehicle direction evidence is unresolved, malformed, or contradicts the Civil viewing convention.";
                return false;
            }

            evidence = Evidence(officeView, midOffset, direction, placed: false, "not-attempted");

            if (spec.Key != SectionFurnitureLogic.Car.Key)
            {
                error = $"No approved office block is assigned to vehicle family '{spec.Key}'.";
                evidence = Evidence(officeView, midOffset, direction, placed: false, "unsupported-family");
                return false;
            }
            if (!_definitions.TryGetValue(officeView, out var definition) || !definition.IsAvailable)
            {
                error = definition?.Error ?? $"Office {officeView} block was not loaded.";
                evidence = Evidence(officeView, midOffset, direction, placed: false, "asset-unavailable");
                return false;
            }

            try
            {
                if (!TryComputePlacement(
                        tr, view, definition.BlockId, spec, midOffset, groundElevation,
                        out var placement, out var placementError) || placement == null)
                    throw new InvalidOperationException(placementError);

                reference = new BlockReference(placement.Position, definition.BlockId)
                {
                    ScaleFactors = placement.ScaleFactors,
                    Rotation = placement.Rotation,
                    Layer = SectionDecorationService.AnnoLayer,
                };
                evidence = Evidence(officeView, midOffset, direction, placed: true,
                    definition.Asset.Sha256.Substring(0, 16));
                return true;
            }
            catch (Exception ex)
            {
                try { reference?.Dispose(); } catch { }
                reference = null;
                error = ex.Message;
                evidence = Evidence(officeView, midOffset, direction, placed: false, "placement-failed");
                return false;
            }
        }

        /// <summary>
        /// Pure placement calculation used by both APPLY and VERIFY.  The caller
        /// supplies an already validated protected definition; no entity is created.
        /// </summary>
        internal static bool TryComputePlacement(
            Transaction tr,
            CivilDb.SectionView view,
            ObjectId blockDefinitionId,
            SectionFurnitureLogic.VehicleSpec spec,
            double midOffset,
            double groundElevation,
            out Placement? placement,
            out string error)
        {
            placement = null;
            error = string.Empty;
            try
            {
                if (view == null || blockDefinitionId.IsNull || spec == null ||
                    !double.IsFinite(midOffset) || !double.IsFinite(groundElevation))
                    throw new InvalidOperationException(
                        "Office vehicle placement input is incomplete/non-finite.");
                var sourceBounds = DefinitionBounds(tr, blockDefinitionId);
                if (!SectionFurnitureLogic.TryOfficeBlockPhysicalHeight(
                        sourceBounds.Width, sourceBounds.Height, spec.WidthM,
                        out var physicalHeight))
                    throw new InvalidOperationException(
                        "Approved office vehicle block has an implausible aspect ratio.");
                var left = SectionAnnotationPlacementContract.Map(
                    view, midOffset - spec.WidthM / 2.0, groundElevation);
                var right = SectionAnnotationPlacementContract.Map(
                    view, midOffset + spec.WidthM / 2.0, groundElevation);
                var bottom = SectionAnnotationPlacementContract.Map(
                    view, midOffset, groundElevation);
                var top = SectionAnnotationPlacementContract.Map(
                    view, midOffset, groundElevation + physicalHeight);
                if (left == null || right == null || bottom == null || top == null)
                    throw new InvalidOperationException(
                        "SectionView could not map the office vehicle envelope.");

                if (!SectionAnnotationPlacementLogic.TryCarPlacement(
                        new SectionAnnotationPlacementLogic.Bounds(
                            sourceBounds.MinX, sourceBounds.MinY,
                            sourceBounds.MaxX, sourceBounds.MaxY),
                        spec.WidthM,
                        new SectionAnnotationPlacementLogic.Point(
                            left.Value.X, left.Value.Y),
                        new SectionAnnotationPlacementLogic.Point(
                            right.Value.X, right.Value.Y),
                        new SectionAnnotationPlacementLogic.Point(
                            bottom.Value.X, bottom.Value.Y),
                        new SectionAnnotationPlacementLogic.Point(
                            top.Value.X, top.Value.Y),
                        out var purePlacement, out var pureError) || purePlacement == null)
                    throw new InvalidOperationException(pureError);
                placement = new Placement(
                    new Point3d(
                        purePlacement.PositionX,
                        purePlacement.PositionY,
                        0),
                    new Scale3d(purePlacement.ScaleX, purePlacement.ScaleY, 1),
                    purePlacement.Rotation);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static ObjectId ImportVerifiedEmbeddedDwg(
            Transaction tr, Database targetDb, Asset asset, bool allowModify,
            List<SectionOfficeBlockFingerprintEvidence.Snapshot>? snapshots)
        {
            var bytes = ReadAndVerify(asset);

            var temp = Path.Combine(Path.GetTempPath(),
                "mhd_section_vehicle_" + Guid.NewGuid().ToString("N") + ".dwg");
            try
            {
                using (var output = new FileStream(
                           temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 81920, options: FileOptions.WriteThrough))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(flushToDisk: true);
                }

                using var sourceDb = new Database(false, true);
                sourceDb.ReadDwgFile(temp, FileShare.Read, allowCPConversion: true, password: null);
                sourceDb.CloseInput(true);

                string sourceEntityFingerprint;
                string sourceStableFingerprint;
                using (var sourceTr = sourceDb.TransactionManager.StartTransaction())
                {
                    var sourceTable = (BlockTable)sourceTr.GetObject(
                        sourceDb.BlockTableId, OpenMode.ForRead);
                    var sourceModel = (BlockTableRecord)sourceTr.GetObject(
                        sourceTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    sourceEntityFingerprint = EntityFingerprintWithEvidence(sourceTr, sourceModel,
                        SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "source-entities"));
                    sourceStableFingerprint = StableEntityFingerprint(sourceTr, sourceModel);
                    sourceTr.Commit();
                }

                // A SHA-addressed name and a provenance comment alone are not enough:
                // a drawing can retain the comment after somebody edits the block
                // definition.  Recompute the geometry fingerprint on every APPLY and
                // refuse reuse unless it is byte-source-equivalent geometrically.
                var blockTable = (BlockTable)tr.GetObject(
                    targetDb.BlockTableId, OpenMode.ForRead);
                if (blockTable.Has(asset.BlockName))
                {
                    var existingId = blockTable[asset.BlockName];
                    var existing = (BlockTableRecord)tr.GetObject(
                        existingId, OpenMode.ForRead);
                    SectionOfficeBlockFingerprintDiagnostics.Metadata(
                        SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "existing-provenance"), existing);
                    var legacy = Provenance(asset);
                    if (!BlockDefinitionFingerprintLogic.IsToolProvenance(
                            existing.Comments, legacy))
                        throw new InvalidOperationException(
                            $"A non-tool block already uses the protected name '{asset.BlockName}'; it was not touched.");

                    var existingEntityFingerprint = EntityFingerprintWithEvidence(tr, existing,
                        SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "existing-entities"));
                    // Exact equality, or the stable proof when only derived extents drifted in their last bits.
                    var stableGeometryMatches = string.Equals(
                        StableEntityFingerprint(tr, existing), sourceStableFingerprint, StringComparison.Ordinal);
                    if (!string.Equals(
                            existingEntityFingerprint, sourceEntityFingerprint,
                            StringComparison.OrdinalIgnoreCase) && !stableGeometryMatches)
                        throw new InvalidOperationException(
                            $"Protected office block '{asset.BlockName}' was changed after import; it was not reused.");

                    if (!allowModify)
                    {
                        var selectedHeaderMatches = TargetHeaderMatches(existing, BlockScaling.Any);
                        var selectedFingerprint = GeometryFingerprintWithEvidence(tr, existing,
                            SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "selected-old-before"));
                        var selectedAttestationMatches = string.Equals(existing.Comments,
                            Provenance(asset, selectedFingerprint), StringComparison.Ordinal) ||
                            (stableGeometryMatches &&
                             SectionOfficeVehicleAssetEvidenceLogic.IsExactProvenanceGrammar(asset.View, existing.Comments));
                        if (selectedHeaderMatches && selectedAttestationMatches)
                        {
                            SectionOfficeBlockFingerprintDiagnostics.Metadata(
                                SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "load-completed"), existing);
                            return existingId;
                        }

                        // Load catches failures per asset. A recovery therefore needs
                        // its own savepoint: no retired-only definition may survive if
                        // the target happens not to need this vehicle view. The outer
                        // APPLY transaction still owns the final commit/rollback.
                        using var recoveryTr = targetDb.TransactionManager.StartTransaction();
                        RetireUnusedSelectedDefinition(recoveryTr, targetDb, asset, existingId,
                            sourceEntityFingerprint, existingEntityFingerprint,
                            selectedHeaderMatches, selectedAttestationMatches,
                            selectedFingerprint, snapshots);
                        var recoveredId = ImportSourceDefinition(recoveryTr, targetDb, sourceDb,
                            asset, sourceEntityFingerprint, snapshots);
                        recoveryTr.Commit();
                        return recoveredId;
                    }

                    if (!TargetHeaderMatches(existing, BlockScaling.Any))
                    {
                        if (!allowModify)
                            throw new InvalidOperationException(
                                $"Protected office block '{asset.BlockName}' needs deterministic header normalization; " +
                                "selected APPLY does not modify shared block definitions " +
                                $"({SectionFindingCodes.SharedResourceChangeRequired}).");
                        existing.UpgradeOpen();
                        NormalizeTargetHeader(existing, BlockScaling.Any);
                    }

                    var targetFingerprint = GeometryFingerprintWithEvidence(tr, existing,
                        SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "existing-attestation"));
                    var current = Provenance(asset, targetFingerprint);

                    if (!string.Equals(existing.Comments, current, StringComparison.Ordinal))
                    {
                        if (!allowModify)
                            throw new InvalidOperationException(
                                $"Protected office block '{asset.BlockName}' carries legacy provenance; selected APPLY " +
                                $"does not modify shared block definitions ({SectionFindingCodes.SharedResourceChangeRequired}).");
                        existing.UpgradeOpen();
                        existing.Comments = current;
                    }
                    SectionOfficeBlockFingerprintDiagnostics.Metadata(
                        SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "load-completed"), existing);
                    return existingId;
                }

                return ImportSourceDefinition(tr, targetDb, sourceDb, asset,
                    sourceEntityFingerprint, snapshots);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static void RetireUnusedSelectedDefinition(
            Transaction tr, Database targetDb, Asset asset, ObjectId existingId,
            string sourceEntityFingerprint, string existingEntityFingerprint,
            bool headerMatches, bool attestationMatches, string beforeFingerprint,
            List<SectionOfficeBlockFingerprintEvidence.Snapshot>? snapshots)
        {
            var existing = (BlockTableRecord)tr.GetObject(existingId, OpenMode.ForRead);
            var ordinaryLocal = !existing.IsLayout && !existing.IsAnonymous &&
                !existing.IsDynamicBlock && !existing.IsFromExternalReference &&
                !existing.IsFromOverlayReference;
            var blockTable = (BlockTable)tr.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
            var retiredName = asset.BlockName + "-OLD-" + existing.Handle;
            var originalComments = existing.Comments;
            // Autodesk: false includes indirect/nested ACTIVE references, true
            // forces complete validity. No BlockReference is opened or modified.
            // Purge is a non-mutating query, also covering other hard references.
            var referenceCount = ordinaryLocal
                ? existing.GetBlockReferenceIds(false, true).Count : (int?)null;
            bool? purgeRetained = null;
            if (ordinaryLocal && referenceCount == 0)
            {
                var candidates = new ObjectIdCollection(new[] { existingId });
                targetDb.Purge(candidates);
                purgeRetained = candidates.Count == 1 && candidates[0] == existingId;
            }
            var rejection = SectionUnusedOfficeBlockRecoveryPolicy.RetirementRejection(new(
                SelectedScope: true, asset.View, existing.Name, originalComments,
                sourceEntityFingerprint, existingEntityFingerprint, ordinaryLocal,
                headerMatches, attestationMatches, referenceCount, purgeRetained,
                RetirementNameAvailable: !blockTable.Has(retiredName)));
            if (rejection != null)
                throw new InvalidOperationException(
                    $"Protected office block '{asset.BlockName}' cannot be retired: {rejection} " +
                    $"({SectionFindingCodes.SharedResourceChangeRequired}).");
            SectionOfficeBlockFingerprintDiagnostics.Metadata(
                SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots,
                    "selected-unused-proof: active-references=0; purge=singleton-exact"), existing);

            // Rename only. Geometry, header and Comments of the old definition are
            // preserved; new references use a newly imported canonical definition.
            existing.UpgradeOpen();
            existing.Name = retiredName;
            var retiredFingerprint = GeometryFingerprintWithEvidence(tr, existing,
                SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "selected-old-retired"));
            if (!string.Equals(existing.Comments, originalComments, StringComparison.Ordinal) ||
                !string.Equals(retiredFingerprint, beforeFingerprint, StringComparison.Ordinal) ||
                blockTable.Has(asset.BlockName) || !blockTable.Has(retiredName) ||
                blockTable[retiredName] != existingId)
                throw new InvalidOperationException(
                    "Retiring the unused office definition did not preserve its exact content and identity.");
        }

        private static ObjectId ImportSourceDefinition(
            Transaction tr, Database targetDb, Database sourceDb, Asset asset,
            string sourceEntityFingerprint,
            List<SectionOfficeBlockFingerprintEvidence.Snapshot>? snapshots)
        {
            // Database.Insert imports source model space as one named definition.
            // The SHA-addressed name contains the complete source SHA, so a manual
            // block with a normal office name cannot be mistaken for a tool asset.
            var id = targetDb.Insert(asset.BlockName, sourceDb, preserveSourceDatabase: true);
            if (id.IsNull)
                throw new InvalidOperationException(
                    $"Civil returned no block definition for {asset.ResourceName}.");
            var imported = (BlockTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            NormalizeTargetHeader(imported, BlockScaling.Any);
            var importedEntityFingerprint = EntityFingerprintWithEvidence(tr, imported,
                SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "imported-entities"));
            if (!string.Equals(
                    importedEntityFingerprint, sourceEntityFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Imported office block '{asset.BlockName}' does not match its audited source geometry.");
            var importedFingerprint = GeometryFingerprintWithEvidence(tr, imported,
                SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "imported-attestation"));
            imported.Comments = Provenance(asset, importedFingerprint);
            if (!string.Equals(GeometryFingerprintWithEvidence(tr, imported,
                        SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "imported-readback")), importedFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Imported office block '{asset.BlockName}' changed after its final attestation.");
            SectionOfficeBlockFingerprintDiagnostics.Metadata(
                SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "load-completed"), imported);
            return id;
        }

        private static byte[] ReadAndVerify(Asset asset)
        {
            using var input = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(asset.ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded office vehicle block '{asset.ResourceName}' is missing.");
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            if (bytes.Length < 6 ||
                !string.Equals(System.Text.Encoding.ASCII.GetString(bytes, 0, 6),
                    "AC1015", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Embedded office vehicle block '{asset.ResourceName}' is not the approved DWG format.");

            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, asset.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Embedded office vehicle block hash mismatch: expected {asset.Sha256}, actual {actual}.");
            return bytes;
        }

        internal static (double MinX, double MinY, double MaxX, double MaxY, double Width, double Height)
            DefinitionBounds(Transaction tr, ObjectId blockId)
        {
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            var found = false;
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (ObjectId entityId in block)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead) is not Entity entity)
                    throw new InvalidOperationException(
                        $"Protected block contains unreadable non-Entity child {entityId.Handle}.");
                Extents3d ext;
                try { ext = entity.GeometricExtents; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Protected block entity {entity.Handle} has no readable extents.", ex);
                }
                if (!Finite(ext.MinPoint.X) || !Finite(ext.MinPoint.Y) ||
                    !Finite(ext.MaxPoint.X) || !Finite(ext.MaxPoint.Y) ||
                    ext.MaxPoint.X < ext.MinPoint.X || ext.MaxPoint.Y < ext.MinPoint.Y)
                    throw new InvalidOperationException(
                        $"Protected block entity {entity.Handle} has invalid extents.");
                minX = Math.Min(minX, ext.MinPoint.X);
                minY = Math.Min(minY, ext.MinPoint.Y);
                maxX = Math.Max(maxX, ext.MaxPoint.X);
                maxY = Math.Max(maxY, ext.MaxPoint.Y);
                found = true;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            if (!found || !FinitePositive(width) || !FinitePositive(height))
                throw new InvalidOperationException("Approved office vehicle block has no usable 2D extents.");
            return (minX, minY, maxX, maxY, width, height);
        }

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool FinitePositive(double value) => Finite(value) && value > 1e-9;

        private static string Provenance(Asset asset) =>
            SectionOfficeVehicleAssetEvidenceLogic.SourceComment(asset.View);

        private static string Provenance(Asset asset, string geometryFingerprint) =>
            SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(
                asset.View, geometryFingerprint);

        /// <summary>
        /// Stable geometry signature for the approved 2D office assets.  Sorted
        /// entity signatures make the result independent of object ids/handles and
        /// insertion order while sampled Curve points detect edits that preserve the
        /// overall extents. Layer and display color are protected too: changing an
        /// office definition away from its audited layer-0/ByLayer contract must fail
        /// live VERIFY even when every vertex remains unchanged.
        /// </summary>
        internal static string GeometryFingerprint(Transaction tr, BlockTableRecord block) =>
            GeometryFingerprintWithEvidence(tr, block, null);

        // Preserve the two-argument production/reflection API above. This distinct
        // method records the same preimage during the original read, not a replay.
        internal static string GeometryFingerprintWithEvidence(
            Transaction tr, BlockTableRecord block,
            SectionOfficeBlockFingerprintEvidence.Snapshot? snapshot)
        {
            var signatures = EntitySignatures(tr, block, snapshot);
            if (!Finite(block.Origin.X) || !Finite(block.Origin.Y) || !Finite(block.Origin.Z))
                throw new InvalidOperationException("Protected block origin is non-finite.");

            // blockdef-v2 (1.2.27): the definition header participates. Moving the base
            // point (Origin) shifts every reference on screen while the entity list stays
            // identical; units, scaling and explodable change how references insert.
            var header = new BlockDefinitionFingerprintLogic.Header(
                block.Origin.X, block.Origin.Y, block.Origin.Z,
                block.Units.ToString(), block.BlockScaling.ToString(), block.Explodable,
                block.Annotative.ToString(), block.PaperOrientation.ToString());
            var fingerprint = BlockDefinitionFingerprintLogic.Compose(header, signatures);
            if (snapshot != null)
            {
                snapshot.Header = header;
                snapshot.HashKind = BlockDefinitionFingerprintLogic.Version;
                snapshot.Fingerprint = fingerprint;
                snapshot.ReadCompletedUtc = DateTimeOffset.UtcNow;
                SectionOfficeBlockFingerprintDiagnostics.Metadata(snapshot, block);
            }
            return fingerprint;
        }

        internal static string EntityFingerprint(Transaction tr, BlockTableRecord block) =>
            BlockDefinitionFingerprintLogic.ComposeEntities(EntitySignatures(tr, block));

        private static string EntityFingerprintWithEvidence(
            Transaction tr, BlockTableRecord block,
            SectionOfficeBlockFingerprintEvidence.Snapshot? snapshot)
        {
            var fingerprint = BlockDefinitionFingerprintLogic.ComposeEntities(EntitySignatures(tr, block, snapshot));
            if (snapshot != null)
            {
                snapshot.HashKind = BlockDefinitionFingerprintLogic.EntityVersion;
                snapshot.Fingerprint = fingerprint;
                snapshot.ReadCompletedUtc = DateTimeOffset.UtcNow;
                SectionOfficeBlockFingerprintDiagnostics.Metadata(snapshot, block, includeSupplementalHeader: true);
            }
            return fingerprint;
        }

        internal static string ReadFingerprintWithDiagnostics(
            Transaction tr, BlockTableRecord block, SectionOfficeBlockFingerprintDiagnostics diagnostics)
        {
            // Only the exact two protected office names opt into these sidecars.
            // Every other annotation block retains its original read path.
            var asset = Assets.FirstOrDefault(a => string.Equals(a.BlockName, block.Name, StringComparison.Ordinal));
            if (asset == null) return GeometryFingerprint(tr, block);
            var snapshots = new List<SectionOfficeBlockFingerprintEvidence.Snapshot>();
            var snapshot = SectionOfficeBlockFingerprintDiagnostics.Begin(snapshots, "verify-attestation");
            try
            {
                var fingerprint = GeometryFingerprintWithEvidence(tr, block, snapshot);
                var matches = string.Equals(snapshot!.Comments, Provenance(asset, fingerprint), StringComparison.Ordinal);
                // The sidecar reports the verdict VERIFY uses: a drifted-extents car that the
                // stable source proof accepts is not recorded as a mismatch.
                var stable = !matches &&
                             SectionOfficeVehicleAssetEvidenceLogic.IsExactProvenanceGrammar(asset.View, snapshot.Comments) &&
                             MatchesPinnedOfficeSource(tr, block);
                diagnostics.Write(asset.View, asset.ResourceName,
                    matches ? "verify-match" : stable ? "verify-stable-match" : "verify-mismatch",
                    matches ? "comment-matches-live-hash"
                        : stable ? "comment-grammar-exact; stable-source-proof=true"
                        : "comment-does-not-match-live-hash", snapshots);
                // This is observation only. Existing VERIFY logic still owns all
                // validation decisions, including a mismatch or unreadable block.
                return fingerprint;
            }
            catch (Exception ex)
            {
                SectionOfficeBlockFingerprintDiagnostics.Metadata(snapshot, block);
                diagnostics.Write(asset.View, asset.ResourceName, "verify-read-failed", "failed", snapshots, ex);
                throw;
            }
        }

        /// <summary>
        /// Stable entity fingerprint (extents rounded to a micron): equal for the audited source and an unchanged
        /// live definition even after AutoCAD re-derives extents with different last bits.
        /// </summary>
        internal static string StableEntityFingerprint(Transaction tr, BlockTableRecord block) =>
            BlockDefinitionFingerprintLogic.ComposeStableEntities(
                EntitySignatures(tr, block, null, stableExtents: true));

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PinnedStable =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Stable entity fingerprint of a pinned embedded source DWG's model space, once per process and asset.
        /// <paramref name="normalize"/> applies the same display normalization the import applies (arrows: ByBlock).
        /// </summary>
        internal static string PinnedSourceStableFingerprint(
            string assetSha256, Func<byte[]> readVerifiedBytes, Action<Transaction, BlockTableRecord>? normalize)
        {
            return PinnedStable.GetOrAdd(assetSha256, _ =>
            {
                var bytes = readVerifiedBytes();
                var temp = Path.Combine(Path.GetTempPath(),
                    "mhd_pinned_source_" + Guid.NewGuid().ToString("N") + ".dwg");
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    using var sourceDb = new Database(false, true);
                    sourceDb.ReadDwgFile(temp, FileShare.Read, allowCPConversion: true, password: null);
                    sourceDb.CloseInput(true);
                    using var sourceTr = sourceDb.TransactionManager.StartTransaction();
                    var table = (BlockTable)sourceTr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
                    var model = (BlockTableRecord)sourceTr.GetObject(
                        table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    normalize?.Invoke(sourceTr, model);
                    var fingerprint = StableEntityFingerprint(sourceTr, model);
                    sourceTr.Abort(); // read-only proof: the side database is discarded either way
                    return fingerprint;
                }
                finally
                {
                    try { File.Delete(temp); } catch { }
                }
            });
        }

        /// <summary>
        /// True when <paramref name="definition"/> is one of the protected office car definitions, has the canonical
        /// header and is geometrically the audited source (stable fingerprint). Office traffic arrows delegate to
        /// their service. Any read failure is "not proven".
        /// </summary>
        internal static bool MatchesPinnedOfficeSource(Transaction tr, BlockTableRecord definition)
        {
            try
            {
                var asset = Assets.FirstOrDefault(a => string.Equals(a.BlockName, definition.Name, StringComparison.Ordinal));
                if (asset != null)
                    return TargetHeaderMatches(definition, BlockScaling.Any) &&
                           string.Equals(StableEntityFingerprint(tr, definition),
                               PinnedSourceStableFingerprint(asset.Sha256, () => ReadAndVerify(asset), null),
                               StringComparison.Ordinal);
                if (string.Equals(definition.Name, SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName,
                        StringComparison.Ordinal))
                    return SectionTrafficDirectionArrowService.MatchesPinnedSource(tr, definition);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<string> EntitySignatures(
            Transaction tr, BlockTableRecord block,
            SectionOfficeBlockFingerprintEvidence.Snapshot? snapshot = null,
            bool stableExtents = false)
        {
            var signatures = new List<string>();
            foreach (ObjectId entityId in block)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead) is not Entity entity)
                    throw new InvalidOperationException(
                        $"Protected block contains unreadable non-Entity child {entityId.Handle}.");
                var signature = GeometrySignature(entity, stableExtents);
                signatures.Add(signature);
                SectionOfficeBlockFingerprintDiagnostics.Entity(snapshot, entity, signature);
            }
            if (signatures.Count == 0)
                throw new InvalidOperationException("Protected block definition contains no readable entities.");
            return signatures;
        }

        internal static bool TargetHeaderMatches(
            BlockTableRecord block, BlockScaling scaling) =>
            block.Origin.IsEqualTo(Point3d.Origin) &&
            block.Units == UnitsValue.Undefined &&
            block.BlockScaling == scaling &&
            !block.Explodable &&
            block.Annotative != AnnotativeStates.True &&
            block.PaperOrientation != PaperOrientationStates.True;

        internal static void NormalizeTargetHeader(
            BlockTableRecord block, BlockScaling scaling)
        {
            block.Origin = Point3d.Origin;
            block.Units = UnitsValue.Undefined;
            block.BlockScaling = scaling;
            block.Explodable = false;
            block.Annotative = AnnotativeStates.False;
            if (!TargetHeaderMatches(block, scaling))
                throw new InvalidOperationException(
                    $"Protected block header failed deterministic read-back (scaling={scaling}).");
        }

        private static string GeometrySignature(Entity entity) => GeometrySignature(entity, stableExtents: false);

        // stableExtents: derived extents rounded to a micron (see BlockDefinitionFingerprintLogic.StableEntityVersion);
        // every stored value (vertices, bulges, widths, curve samples) keeps full precision.
        private static string GeometrySignature(Entity entity, bool stableExtents)
        {
            var values = new List<string> { entity.GetType().FullName ?? entity.GetType().Name };
            var layer = entity.Layer;
            var linetype = entity.Linetype;
            if (string.IsNullOrWhiteSpace(layer) || string.IsNullOrWhiteSpace(linetype))
                throw new InvalidOperationException(
                    $"Protected block entity {entity.Handle} has an empty layer or linetype contract.");
            values.Add("layer=" + layer);
            values.Add("color-index=" + entity.ColorIndex.ToString(CultureInfo.InvariantCulture));
            values.Add("color-method=" + entity.Color.ColorMethod);
            values.Add("linetype=" + linetype);
            values.Add("linetype-scale=" + F(entity.LinetypeScale));
            values.Add("lineweight=" + entity.LineWeight);
            values.Add("transparency=" + CanonicalTransparency(entity.Transparency));
            values.Add("visible=" + entity.Visible);
            try
            {
                var ext = entity.GeometricExtents;
                if (!Finite(ext.MinPoint.X) || !Finite(ext.MinPoint.Y) || !Finite(ext.MinPoint.Z) ||
                    !Finite(ext.MaxPoint.X) || !Finite(ext.MaxPoint.Y) || !Finite(ext.MaxPoint.Z) ||
                    ext.MaxPoint.X < ext.MinPoint.X || ext.MaxPoint.Y < ext.MinPoint.Y ||
                    ext.MaxPoint.Z < ext.MinPoint.Z)
                    throw new InvalidOperationException("Entity extents are non-finite or inverted.");
                if (stableExtents)
                {
                    foreach (var p in new[] { ext.MinPoint, ext.MaxPoint })
                    {
                        values.Add(BlockDefinitionFingerprintLogic.StableExtentValue(p.X));
                        values.Add(BlockDefinitionFingerprintLogic.StableExtentValue(p.Y));
                        values.Add(BlockDefinitionFingerprintLogic.StableExtentValue(p.Z));
                    }
                }
                else
                {
                    AddPoint(values, ext.MinPoint);
                    AddPoint(values, ext.MaxPoint);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Protected block entity {entity.Handle} extents are unreadable.", ex);
            }

            if (entity is Polyline polyline)
            {
                values.Add("vertices=" + polyline.NumberOfVertices.ToString(CultureInfo.InvariantCulture));
                values.Add("closed=" + polyline.Closed);
                values.Add("elevation=" + F(polyline.Elevation));
                values.Add("normal=" + F(polyline.Normal.X) + "," + F(polyline.Normal.Y) + "," + F(polyline.Normal.Z));
                values.Add("thickness=" + F(polyline.Thickness));
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                {
                    var p = polyline.GetPoint2dAt(i);
                    values.Add(F(p.X));
                    values.Add(F(p.Y));
                    values.Add(F(polyline.GetBulgeAt(i)));
                    values.Add(F(polyline.GetStartWidthAt(i)));
                    values.Add(F(polyline.GetEndWidthAt(i)));
                }
            }
            else if (entity is Solid solid)
            {
                // HW-ARRW-01 is composed of four AutoCAD Solid entities. Extents
                // alone do not protect their quadrilateral shape: vertices can move
                // while the overall box remains unchanged.
                for (short i = 0; i < 4; i++)
                    AddPoint(values, solid.GetPointAt(i));
                values.Add("normal=" + F(solid.Normal.X) + "," + F(solid.Normal.Y) + "," + F(solid.Normal.Z));
                values.Add("thickness=" + F(solid.Thickness));
            }
            else if (entity is Curve curve)
            {
                try
                {
                    var start = curve.StartParam;
                    var end = curve.EndParam;
                    if (!Finite(start) || !Finite(end) || end < start)
                        throw new InvalidOperationException("Curve parameter range is invalid.");
                    values.Add("params=" + F(start) + "," + F(end));
                    for (var i = 0; i <= 8; i++)
                    {
                        var parameter = start + (end - start) * i / 8.0;
                        AddPoint(values, curve.GetPointAtParameter(parameter));
                    }
                    values.Add("length=" + F(curve.GetDistanceAtParameter(end)));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Protected block curve {entity.Handle} geometry is unreadable.", ex);
                }
            }

            return string.Join("|", values);
        }

        // One shared rule with the annotation fingerprint: never read Alpha/IsClear/
        // IsSolid on a ByLayer/ByBlock value (native eInvalidKey, live 07/09).
        private static string CanonicalTransparency(Autodesk.AutoCAD.Colors.Transparency value) =>
            SectionProjectionAnnotationSemantics.TransparencyText(value) ??
            throw new InvalidOperationException(
                "Protected block entity has an invalid transparency contract.");

        private static void AddPoint(List<string> values, Point3d point)
        {
            values.Add(F(point.X));
            values.Add(F(point.Y));
            values.Add(F(point.Z));
        }

        private static string F(double value)
        {
            if (!Finite(value))
                throw new InvalidOperationException(
                    "Protected block geometry contains a non-finite value.");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Evidence(
            SectionFurnitureLogic.OfficeCarView view,
            double offset,
            SectionVehicleDirectionPlanner.DirectionPlan direction,
            bool placed,
            string detail) =>
            string.Join("|",
                placed ? "office-block" : "schematic-fallback",
                view.ToString().ToLowerInvariant(),
                "offset=" + offset.ToString("F3", CultureInfo.InvariantCulture),
                "direction=" + SectionVehicleDirectionPlanner.FlowToken(direction.Flow),
                "direction-source=" + direction.DirectionSource,
                "direction-digest=" + direction.DirectionDigest,
                "detail=" + detail);

        /// <summary>
        /// Binds the semantic office-block row to the exact persisted reference.  The
        /// pre-append seven-field value is never valid VERIFY evidence by itself.
        /// </summary>
        internal static string BindLiveReference(string evidence, BlockReference reference)
        {
            if (reference == null || reference.ObjectId.IsNull ||
                string.IsNullOrWhiteSpace(evidence) ||
                !evidence.StartsWith(
                    SectionAnnotationContractLogic.OfficeBlockPrefix + "|",
                    StringComparison.Ordinal) ||
                evidence.Split('|').Length != 7)
                throw new InvalidOperationException(
                    "Office vehicle evidence cannot be bound before its exact reference is persisted.");
            return evidence + "|handle=" + reference.Handle.ToString().ToUpperInvariant();
        }
    }
}
