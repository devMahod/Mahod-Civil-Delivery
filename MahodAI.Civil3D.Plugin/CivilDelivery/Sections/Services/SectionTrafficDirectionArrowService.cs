using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Imports Nataly's third approved office block (HW-ARRW-01.dwg) from the
    /// assembly, verifies its pinned bytes and live geometry, normalizes only color
    /// inheritance to ByBlock, and creates one reference above each directional
    /// strip. No schematic arrow is an allowed fallback.
    /// </summary>
    internal sealed class SectionTrafficDirectionArrowService
    {
        internal const string ResourceName =
            "MahodAI.Civil3D.Plugin.assets.sections.HW-ARRW-01.dwg";

        internal sealed record RenderedArrow(
            double LaneMidOffsetM,
            string DirectionSource,
            string DirectionDigest,
            SectionTrafficDirectionAnnotationLogic.ArrowLayout Layout,
            string AssetSha256,
            string GeometrySha256,
            BlockReference Reference)
        {
            internal IReadOnlyList<Entity> Entities => new Entity[] { Reference };
        }

        internal sealed record Placement(
            Point3d Position,
            Scale3d ScaleFactors,
            double Rotation,
            SectionTrafficDirectionAnnotationLogic.ArrowLayout Layout);

        private sealed record Definition(
            ObjectId BlockId,
            string? GeometrySha256,
            string? Error)
        {
            internal bool IsAvailable =>
                !BlockId.IsNull &&
                SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(
                    GeometrySha256) &&
                string.IsNullOrWhiteSpace(Error);
        }

        private readonly Definition _definition;

        private SectionTrafficDirectionArrowService(Definition definition) =>
            _definition = definition;

        internal string? Failure => _definition.IsAvailable
            ? null
            : _definition.Error ?? "Approved office traffic-arrow block is unavailable.";

        internal static SectionTrafficDirectionArrowService Load(
            Transaction tr, Database targetDb, StageLog? log, bool allowModify = true)
        {
            try
            {
                log?.Begin("decorate.traffic_arrow.import", ResourceName);
                var definition = ImportVerifiedEmbeddedDwg(tr, targetDb, allowModify);
                log?.End("decorate.traffic_arrow.import",
                    $"block={SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName} " +
                    $"source_sha={SectionTrafficArrowAssetEvidenceLogic.SourceSha256} " +
                    $"geometry_sha={definition.GeometrySha256}");
                return new SectionTrafficDirectionArrowService(definition);
            }
            catch (Exception ex)
            {
                log?.End("decorate.traffic_arrow.import", "FAILED");
                log?.Info($"decorate.traffic_arrow.unavailable error={ex.Message}");
                return new SectionTrafficDirectionArrowService(
                    new Definition(ObjectId.Null, null, ex.Message));
            }
        }

        internal bool TryCreate(
            Transaction tr,
            CivilDb.SectionView view,
            double laneMidOffsetM,
            double groundElevation,
            SectionTrafficDirectionAnnotationLogic.StripKind stripKind,
            SectionVehicleDirectionPlanner.DirectionPlan direction,
            string annotationLayer,
            out RenderedArrow? rendered,
            out string error)
        {
            rendered = null;
            error = string.Empty;
            if (!_definition.IsAvailable)
            {
                error = Failure!;
                return false;
            }
            if (direction == null || !direction.IsResolved ||
                direction.DirectionSource == null || direction.DirectionDigest == null)
            {
                error = "traffic-arrow office block was not placed because direction is unresolved";
                return false;
            }
            if ((stripKind == SectionTrafficDirectionAnnotationLogic.StripKind.Bike) !=
                (direction.EvidenceMode ==
                    SectionVehicleDirectionPlanner.ArrowEvidenceMode.Bicycle))
            {
                error = "traffic-arrow strip kind contradicts motor/bicycle evidence mode";
                return false;
            }
            if (view == null || string.IsNullOrWhiteSpace(annotationLayer))
                return false;

            try
            {
                if (!TryComputePlacement(
                        tr, view, _definition.BlockId, laneMidOffsetM, groundElevation,
                        stripKind, direction.Flow, out var placement, out error) ||
                    placement == null)
                    return false;

                var reference = new BlockReference(placement.Position, _definition.BlockId)
                {
                    ScaleFactors = placement.ScaleFactors,
                    Rotation = placement.Rotation,
                    Layer = annotationLayer,
                    ColorIndex = placement.Layout.ColorIndex,
                };
                rendered = new RenderedArrow(
                    laneMidOffsetM,
                    direction.DirectionSource,
                    direction.DirectionDigest.ToLowerInvariant(),
                    placement.Layout,
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                    _definition.GeometrySha256!,
                    reference);
                return true;
            }
            catch (Exception ex)
            {
                try { rendered?.Reference.Dispose(); } catch { }
                rendered = null;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Deterministic placement used by APPLY and VERIFY.  It validates the live
        /// SectionView transform and protected definition but creates no entity.
        /// </summary>
        internal static bool TryComputePlacement(
            Transaction tr,
            CivilDb.SectionView view,
            ObjectId blockDefinitionId,
            double laneMidOffsetM,
            double groundElevation,
            SectionTrafficDirectionAnnotationLogic.StripKind stripKind,
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            out Placement? placement,
            out string error)
        {
            placement = null;
            error = string.Empty;
            try
            {
                if (view == null || blockDefinitionId.IsNull ||
                    !SectionTrafficDirectionAnnotationLogic.TryBuild(
                        laneMidOffsetM, groundElevation, flow, stripKind,
                        out var layout, out error) || layout == null)
                    return false;
                var bottom = SectionAnnotationPlacementContract.Map(
                    view, laneMidOffsetM, layout.BottomElevation);
                var top = SectionAnnotationPlacementContract.Map(
                    view, laneMidOffsetM, layout.TopElevation);
                if (bottom == null || top == null)
                    throw new InvalidOperationException(
                        "SectionView could not map the complete office traffic-arrow envelope.");

                var bounds = SectionVehicleBlockService.DefinitionBounds(tr, blockDefinitionId);
                if (!SectionAnnotationPlacementLogic.TryArrowPlacement(
                        new SectionAnnotationPlacementLogic.Bounds(
                            bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY),
                        new SectionAnnotationPlacementLogic.Point(
                            bottom.Value.X, bottom.Value.Y),
                        new SectionAnnotationPlacementLogic.Point(
                            top.Value.X, top.Value.Y),
                        layout.PointsUp,
                        out var purePlacement, out var pureError) || purePlacement == null)
                    throw new InvalidOperationException(pureError);
                placement = new Placement(
                    new Point3d(
                        purePlacement.PositionX, purePlacement.PositionY, 0),
                    new Scale3d(
                        purePlacement.ScaleX, purePlacement.ScaleY,
                        purePlacement.ScaleX),
                    purePlacement.Rotation, layout);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Call only after the returned BlockReference was appended.</summary>
        internal static string Evidence(RenderedArrow rendered)
        {
            if (rendered == null) throw new ArgumentNullException(nameof(rendered));
            if (rendered.Reference.ObjectId.IsNull)
                throw new InvalidOperationException(
                    "Traffic-arrow evidence requested before its office block reference was appended.");
            return SectionAnnotationContractLogic.FormatTrafficDirectionArrowReference(
                rendered.LaneMidOffsetM,
                rendered.Layout.Flow,
                rendered.DirectionSource,
                rendered.DirectionDigest,
                rendered.Layout.StyleToken,
                rendered.AssetSha256,
                rendered.GeometrySha256,
                rendered.Reference.Handle.ToString());
        }

        private static Definition ImportVerifiedEmbeddedDwg(
            Transaction tr, Database targetDb, bool allowModify)
        {
            var bytes = ReadAndVerify();
            var temp = Path.Combine(Path.GetTempPath(),
                "mhd_section_traffic_arrow_" + Guid.NewGuid().ToString("N") + ".dwg");
            try
            {
                using (var output = new FileStream(
                           temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 81920, options: FileOptions.WriteThrough))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(flushToDisk: true);
                }

                string sourceEntityFingerprint;
                string sourceStableFingerprint;
                using var sourceDb = new Database(false, true);
                sourceDb.ReadDwgFile(temp, FileShare.Read,
                    allowCPConversion: true, password: null);
                sourceDb.CloseInput(true);
                using (var sourceTr = sourceDb.TransactionManager.StartTransaction())
                {
                    var sourceTable = (BlockTable)sourceTr.GetObject(
                        sourceDb.BlockTableId, OpenMode.ForRead);
                    var sourceModel = (BlockTableRecord)sourceTr.GetObject(
                        sourceTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    // Arrow display is deliberately normalized to layer 0 / ByBlock
                    // so each reference can inherit the approved direction color.
                    // Fingerprint that normalized contract, not the raw DWG display,
                    // because the same hash is persisted and re-read by VERIFY.
                    NormalizeByBlockDisplay(sourceTr, sourceModel);
                    sourceEntityFingerprint = SectionVehicleBlockService.EntityFingerprint(
                        sourceTr, sourceModel);
                    // From the side database already open, as the car path does: a second
                    // read of the same verified bytes was a new, uncaught failure path.
                    sourceStableFingerprint = SectionVehicleBlockService.StableEntityFingerprint(
                        sourceTr, sourceModel);
                    sourceTr.Commit();
                }

                var table = (BlockTable)tr.GetObject(
                    targetDb.BlockTableId, OpenMode.ForRead);
                ObjectId blockId;
                if (table.Has(SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName))
                {
                    blockId = table[
                        SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName];
                    var existing = (BlockTableRecord)tr.GetObject(
                        blockId, OpenMode.ForRead);
                    var legacy = SectionTrafficArrowAssetEvidenceLogic.SourceComment;
                    if (!BlockDefinitionFingerprintLogic.IsToolProvenance(
                            existing.Comments, legacy))
                        throw new InvalidOperationException(
                            "A non-tool block already uses the protected office traffic-arrow name; it was not touched.");
                    if (!UsesByBlockDisplay(tr, existing))
                    {
                        if (!allowModify)
                            throw new InvalidOperationException(
                                "Protected office traffic-arrow definition needs display normalization; selected APPLY " +
                                $"does not modify shared block definitions ({SectionFindingCodes.SharedResourceChangeRequired}).");
                        NormalizeByBlockDisplay(tr, existing);
                    }
                    var liveEntityFingerprint = SectionVehicleBlockService.EntityFingerprint(
                        tr, existing);
                    // Exact equality, or the stable proof when only derived extents drifted in their last bits.
                    var stableGeometryMatches = string.Equals(
                        SectionVehicleBlockService.StableEntityFingerprint(tr, existing),
                        sourceStableFingerprint,
                        StringComparison.Ordinal);
                    if (!string.Equals(
                            liveEntityFingerprint, sourceEntityFingerprint,
                            StringComparison.OrdinalIgnoreCase) && !stableGeometryMatches)
                        throw new InvalidOperationException(
                            "Protected office traffic-arrow geometry changed after import; it was not reused.");
                    if (!SectionVehicleBlockService.TargetHeaderMatches(
                            existing, BlockScaling.Uniform))
                    {
                        if (!allowModify)
                            throw new InvalidOperationException(
                                "Protected office traffic-arrow definition needs deterministic header normalization; " +
                                "selected APPLY does not modify shared block definitions " +
                                $"({SectionFindingCodes.SharedResourceChangeRequired}).");
                        existing.UpgradeOpen();
                        SectionVehicleBlockService.NormalizeTargetHeader(
                            existing, BlockScaling.Uniform);
                    }
                    var targetFingerprint = SectionVehicleBlockService.GeometryFingerprint(
                        tr, existing);
                    var current = SectionTrafficArrowAssetEvidenceLogic.ProvenanceComment(
                        targetFingerprint);
                    if (!string.Equals(existing.Comments, current, StringComparison.Ordinal) &&
                        !(!allowModify && stableGeometryMatches &&
                          SectionTrafficArrowAssetEvidenceLogic.IsExactProvenanceGrammar(existing.Comments)))
                    {
                        if (!allowModify)
                            throw new InvalidOperationException(
                                "Protected office traffic-arrow definition carries legacy provenance; selected APPLY " +
                                $"does not modify shared block definitions ({SectionFindingCodes.SharedResourceChangeRequired}).");
                        existing.UpgradeOpen();
                        existing.Comments = current;
                    }
                }
                else
                {
                    blockId = targetDb.Insert(
                        SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName,
                        sourceDb,
                        preserveSourceDatabase: true);
                    if (blockId.IsNull)
                        throw new InvalidOperationException(
                            "Civil returned no definition for the approved office traffic-arrow asset.");
                    var imported = (BlockTableRecord)tr.GetObject(
                        blockId, OpenMode.ForWrite);
                    NormalizeByBlockDisplay(tr, imported);
                    SectionVehicleBlockService.NormalizeTargetHeader(
                        imported, BlockScaling.Uniform);
                    var importedEntityFingerprint =
                        SectionVehicleBlockService.EntityFingerprint(tr, imported);
                    if (!string.Equals(
                            importedEntityFingerprint, sourceEntityFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Imported office traffic-arrow geometry does not match its audited source.");
                    var importedFingerprint =
                        SectionVehicleBlockService.GeometryFingerprint(tr, imported);
                    imported.Comments =
                        SectionTrafficArrowAssetEvidenceLogic.ProvenanceComment(
                            importedFingerprint);
                    if (!string.Equals(
                            SectionVehicleBlockService.GeometryFingerprint(tr, imported),
                            importedFingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Imported office traffic-arrow changed after its final attestation.");
                }

                var definition = (BlockTableRecord)tr.GetObject(
                    blockId, OpenMode.ForRead);
                if (!UsesByBlockDisplay(tr, definition))
                    throw new InvalidOperationException(
                        "Office traffic-arrow definition did not retain ByBlock color inheritance.");
                SectionVehicleBlockService.DefinitionBounds(tr, blockId);
                var finalFingerprint = SectionVehicleBlockService.GeometryFingerprint(
                    tr, definition);
                return new Definition(blockId, finalFingerprint, null);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static byte[] ReadAndVerify()
        {
            using var input = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    "Embedded approved office traffic-arrow DWG is missing.");
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            if (bytes.Length < 6 ||
                !string.Equals(
                    System.Text.Encoding.ASCII.GetString(bytes, 0, 6),
                    "AC1015", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Embedded office traffic-arrow is not the approved DWG format.");
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(
                    actual,
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Embedded office traffic-arrow hash mismatch: expected " +
                    $"{SectionTrafficArrowAssetEvidenceLogic.SourceSha256}, actual {actual}.");
            return bytes;
        }

        /// <summary>
        /// Stable proof that a live arrow definition is the audited, ByBlock-normalized source with the canonical
        /// header (extents compared to a micron — see BlockDefinitionFingerprintLogic.StableEntityVersion).
        /// </summary>
        internal static bool MatchesPinnedSource(Transaction tr, BlockTableRecord definition) =>
            SectionVehicleBlockService.TargetHeaderMatches(definition, BlockScaling.Uniform) &&
            UsesByBlockDisplay(tr, definition) &&
            string.Equals(
                SectionVehicleBlockService.StableEntityFingerprint(tr, definition),
                SectionVehicleBlockService.PinnedSourceStableFingerprint(
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256, ReadAndVerify, NormalizeByBlockDisplay),
                StringComparison.Ordinal);

        private static void NormalizeByBlockDisplay(
            Transaction tr, BlockTableRecord definition)
        {
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
                    throw new InvalidOperationException(
                        $"Protected traffic-arrow block contains non-Entity child {id.Handle}.");
                if (entity.ColorIndex == 0 &&
                    string.Equals(entity.Layer, "0", StringComparison.OrdinalIgnoreCase))
                    continue;
                entity.UpgradeOpen();
                entity.Layer = "0";
                entity.ColorIndex = 0;
            }
        }

        internal static bool UsesByBlockDisplay(
            Transaction tr, BlockTableRecord definition)
        {
            var count = 0;
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
                    throw new InvalidOperationException(
                        $"Protected traffic-arrow block contains non-Entity child {id.Handle}.");
                count++;
                if (entity.ColorIndex != 0 ||
                    !string.Equals(entity.Layer, "0", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return count > 0;
        }

        private static bool FinitePositive(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value > 1e-9;
    }
}
