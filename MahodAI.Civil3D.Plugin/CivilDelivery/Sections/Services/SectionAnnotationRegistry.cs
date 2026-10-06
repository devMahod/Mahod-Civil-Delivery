using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    internal sealed record LiveProjectionAnnotationEvidence(
        string Handle, string? RegisteredFingerprint, string? LiveFingerprint);

    internal sealed class ProjectionRegistryReadResult
    {
        internal bool IsValid { get; set; } = true;
        internal string? Error { get; set; }
        internal Dictionary<string, List<LiveProjectionAnnotationEvidence>> Entries { get; } =
            new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Read-only evidence for one entity in the primary (non-projection) annotation
    /// registry.  VERIFY uses this list as the boundary of what Mahod owns around a
    /// reused manual SectionView: the foreign Civil objects are never treated as
    /// annotations and no model-space scan is used to infer ownership.
    /// </summary>
    internal sealed record LiveSectionAnnotationEvidence(
        string Handle,
        ObjectId EntityId,
        bool IsLive,
        string? EntityType,
        string? Layer,
        string? RegisteredFingerprint,
        string? LiveFingerprint,
        string? Text,
        string? TextStyleName,
        double[]? TextPosition,
        double[]? TextAlignmentPoint,
        double? TextHeight,
        double? TextRotation,
        double? TextWidthFactor,
        double? TextOblique,
        int? TextHorizontalMode,
        int? TextVerticalMode,
        bool? TextMirroredInX,
        bool? TextMirroredInY,
        string? BlockDefinitionName,
        ObjectId BlockDefinitionId,
        string? BlockDefinitionComments,
        string? BlockDefinitionGeometrySha256,
        bool? BlockDefinitionUsesByBlockColor,
        double[]? BlockPosition,
        double[]? BlockScaleFactors,
        double? BlockRotation,
        short? ColorIndex,
        double[]? LineEndpoints,
        int? LineWeight,
        bool OwnershipValid)
    {
        /// <summary>
        /// Protected office definition (car or traffic arrow) proven equal to its audited source by the stable
        /// fingerprint, with the canonical header. VERIFY uses it when an exact stamped hash drifted.
        /// </summary>
        public bool BlockDefinitionMatchesPinnedSource { get; init; }
    }

    internal sealed class AnnotationRegistryReadResult
    {
        internal bool IsValid { get; set; } = true;
        internal bool EntryExists { get; set; }
        internal string? Error { get; set; }
        internal List<LiveSectionAnnotationEvidence> Entries { get; } = new();
    }

    internal sealed record AnnotationEnvelopeEvidence(
        string Id, bool IsValid, double[]? Bounds, string? Error);

    internal sealed record AnnotationInventoryEvidence(
        bool IsValid, int LayerEntityCount, int RegisteredCount,
        IReadOnlyList<string> Problems)
    {
        internal IReadOnlyList<AnnotationInventoryLogic.Registered> DeadEntries { get; init; } =
            Array.Empty<AnnotationInventoryLogic.Registered>();
        internal IReadOnlyDictionary<string, string> RegistryDataHashes { get; init; } =
            new Dictionary<string, string>();
    }

    /// <summary>
    /// Tracks the annotation entities the tool drew for each section so a re-apply
    /// replaces them instead of stacking a second copy on top — the same idempotency
    /// promise the sections themselves keep. Handles live in a named dictionary under
    /// the drawing's NOD, keyed by the section's logical key: fast to find, saved with
    /// the drawing, and never dependent on scanning six million entities.
    /// </summary>
    internal static class SectionAnnotationRegistry
    {
        private const string DictName = "MAHOD_CIVIL_DELIVERY_ANNOTATIONS";

        /// <summary>Erases every annotation previously recorded for this logical key.</summary>
        internal static int EraseExisting(Transaction tr, Database db, string logicalKey)
        {
            var erased = 0;
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictName)) return 0;
                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
                // ApplyCore performs the expensive two-way model-space inventory once
                // before touching any record.  The deletion boundary independently
                // re-proves the one fact it needs from the registry itself: no primary
                // handle (legacy or current) appears under two logical keys.  This is
                // O(registry), not O(model-space × record-count), and it never adopts a
                // layer entity that was not explicitly registered.
                RequireUniquePrimaryRegistryHandles(tr, dict);
                var key = SafeKey(logicalKey);
                if (!dict.Contains(key)) return 0;

                var xrec = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForRead);
                if (xrec.Data == null)
                    throw new InvalidOperationException("Annotation registry record has no data.");

                // Authorisation preflight: a handle alone is never ownership proof.
                // Validate the complete registry and every live entity before opening
                // the first object for write. A stale/reused handle or changed entity
                // blocks the atomic rerun. A legacy raw handle is migration evidence
                // only when it resolves to one live entity on the exact owned layer;
                // no unregistered layer entity is ever adopted.
                var entities = new List<(ObjectId Id, string Hex)>();
                var fingerprints = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                var projections = new List<(string Handle, string Fingerprint)>();
                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.TypeCode != (int)DxfCode.Text || tv.Value is not string value)
                        throw new InvalidOperationException(
                            "Annotation registry contains a non-text entry.");

                    if (value.StartsWith("P:", StringComparison.Ordinal))
                    {
                        if (!TryParseProjectionEntry(value, out _, out var projectionHex,
                                out var projectionFingerprint))
                            throw new InvalidOperationException(
                                $"Invalid projection annotation entry '{value}' cannot authorise deletion.");
                        // A projection entry never authorises deletion by itself; it
                        // must bind to the identical primary handle below.  A missing
                        // legacy fingerprint is therefore accepted only as migration
                        // metadata and is checked against that primary identity.
                        projections.Add((projectionHex, projectionFingerprint ?? string.Empty));
                        continue;
                    }

                    if (!TryParsePrimaryEntry(value, out var primaryHex, out var registeredFingerprint) ||
                        !long.TryParse(primaryHex, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var hv))
                        throw new InvalidOperationException(
                            $"Invalid annotation entry '{value}' cannot authorise deletion.");
                    var hex = hv.ToString("X", CultureInfo.InvariantCulture);
                    if (!fingerprints.TryAdd(hex, registeredFingerprint ?? string.Empty))
                        throw new InvalidOperationException(
                            $"Duplicate annotation handle '{hex}' in registry.");
                    if (!db.TryGetObjectId(new Handle(hv), out var id) || id.IsErased ||
                        tr.GetObject(id, OpenMode.ForRead, openErased: false) is not Entity entity ||
                        entity.IsErased)
                        throw new InvalidOperationException(
                            $"Registered annotation {hex} is not a readable live Entity.");
                    if (!SectionAnnotationResourceContracts.IsKnownAnnotationLayer(entity.Layer))
                        throw new InvalidOperationException(
                            $"Registered annotation {hex} is on foreign layer '{entity.Layer}'.");
                    var liveFingerprint = SectionProjectionAnnotationSemantics.Fingerprint(entity);
                    if (registeredFingerprint != null &&
                        !string.Equals(registeredFingerprint, liveFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Registered annotation {hex} changed since it was recorded.");
                    fingerprints[hex] = liveFingerprint;
                    entities.Add((id, hex));
                }

                foreach (var projection in projections)
                {
                    if (!fingerprints.TryGetValue(projection.Handle, out var primaryFingerprint) ||
                        (projection.Fingerprint.Length != 0 &&
                         !string.Equals(primaryFingerprint, projection.Fingerprint,
                             StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(
                            $"Projection annotation {projection.Handle} is not bound to an identical primary entry.");
                }

                foreach (var item in entities)
                {
                    try
                    {
                        var entity = (Entity)tr.GetObject(
                            item.Id, OpenMode.ForWrite, openErased: false);
                        entity.Erase();
                        if (!entity.IsErased)
                            throw new InvalidOperationException(
                                $"Registered annotation {item.Hex} did not report erased.");
                        erased++;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Registered annotation {item.Hex} could not be erased.", ex);
                    }
                }
                if (!dict.IsWriteEnabled) dict.UpgradeOpen();
                if (!xrec.IsWriteEnabled) xrec.UpgradeOpen();
                dict.Remove(key);
                xrec.Erase();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Existing annotations for '{logicalKey}' could not be reconciled; rerun was aborted.", ex);
            }
            return erased;
        }

        private static void RequireUniquePrimaryRegistryHandles(
            Transaction tr, DBDictionary dict)
        {
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (tr.GetObject(entry.Value, OpenMode.ForRead) is not Xrecord xrec ||
                    xrec.Data == null)
                    throw new InvalidOperationException(
                        $"Annotation registry {entry.Key} has no readable Xrecord.");
                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.TypeCode != (int)DxfCode.Text || tv.Value is not string value)
                        throw new InvalidOperationException(
                            $"Annotation registry {entry.Key} contains non-text data.");
                    if (value.StartsWith("P:", StringComparison.Ordinal)) continue;
                    if (!TryParsePrimaryEntry(value, out var primaryHex, out _) ||
                        !long.TryParse(primaryHex, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var handleValue))
                        throw new InvalidOperationException(
                            $"Annotation registry {entry.Key} contains invalid primary evidence '{value}'.");
                    var hex = handleValue.ToString("X", CultureInfo.InvariantCulture);
                    if (!owners.TryAdd(hex, entry.Key))
                        throw new InvalidOperationException(
                            $"Annotation {hex} is registered by both {owners[hex]} and {entry.Key}.");
                }
            }
        }

        /// <summary>Records the annotation handles drawn for this logical key.</summary>
        internal static void Record(
            Transaction tr,
            Database db,
            string logicalKey,
            IReadOnlyList<Handle> handles,
            Func<string, OwnershipMetadata> ownershipForFingerprint,
            IReadOnlyDictionary<string, IReadOnlyList<Handle>>? projectionHandles = null,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? projectionFingerprints = null)
        {
            if (handles.Count == 0) return;
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            DBDictionary dict;
            if (nod.Contains(DictName))
            {
                dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForWrite);
            }
            else
            {
                dict = new DBDictionary();
                nod.SetAt(DictName, dict);
                tr.AddNewlyCreatedDBObject(dict, true);
            }

            var key = SafeKey(logicalKey);
            if (dict.Contains(key))
                throw new InvalidOperationException(
                    "Annotation registry entry still exists after reconciliation; refusing to overwrite evidence.");

            var xrec = new Xrecord();
            var values = new List<TypedValue>();
            foreach (var h in handles)
            {
                var hex = h.Value.ToString("X", CultureInfo.InvariantCulture);
                if (!db.TryGetObjectId(h, out var id) || id.IsErased ||
                    tr.GetObject(id, OpenMode.ForRead, openErased: false) is not Entity entity)
                    throw new InvalidOperationException(
                        $"Primary annotation {hex} is not a readable live Entity.");
                var fingerprint = SectionProjectionAnnotationSemantics.Fingerprint(entity);
                if (!entity.IsWriteEnabled) entity.UpgradeOpen();
                var ownership = ownershipForFingerprint(fingerprint);
                if (!string.Equals(ownership.LogicalKey, logicalKey, StringComparison.Ordinal) ||
                    !string.Equals(ownership.Feature, "sections", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ownership.Role, "annotation", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ownership.InputFingerprint, fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Primary annotation {hex} received invalid ownership evidence.");
                SectionOwnershipService.Write(tr, entity, ownership);
                values.Add(new TypedValue((int)DxfCode.Text,
                    $"A:{hex}:{fingerprint}"));
            }
            if (projectionHandles != null)
            {
                foreach (var (projectionKey, mapped) in projectionHandles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    foreach (var h in mapped)
                    {
                        var hex = h.Value.ToString("X", CultureInfo.InvariantCulture);
                        if (projectionFingerprints == null ||
                            !projectionFingerprints.TryGetValue(projectionKey, out var byHandle) ||
                            !byHandle.TryGetValue(hex, out var fingerprint) ||
                            string.IsNullOrWhiteSpace(fingerprint))
                            throw new InvalidOperationException(
                                $"Projection {projectionKey} annotation {hex} has no semantic fingerprint.");
                        values.Add(new TypedValue((int)DxfCode.Text,
                            $"P:{projectionKey}:{hex}:{fingerprint}"));
                    }
                }
            }
            xrec.Data = new ResultBuffer(values.ToArray());
            dict.SetAt(key, xrec);
            tr.AddNewlyCreatedDBObject(xrec, true);
        }

        /// <summary>
        /// Counts annotation handles that still resolve to live database objects. VERIFY
        /// uses this to prove the visible deliverable, not merely the Civil container.
        /// </summary>
        internal static int CountExisting(Transaction tr, Database db, string logicalKey)
        {
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictName)) return 0;
                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
                var key = SafeKey(logicalKey);
                if (!dict.Contains(key)) return 0;

                var xrec = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForRead);
                if (xrec.Data == null) return 0;

                var count = 0;
                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.Value is not string value || value.StartsWith("P:", StringComparison.Ordinal))
                        continue;
                    if (!TryParsePrimaryEntry(value, out var hex, out _)) continue;
                    if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv)) continue;
                    if (!db.TryGetObjectId(new Handle(hv), out var id) || id.IsErased) continue;
                    try
                    {
                        var obj = tr.GetObject(id, OpenMode.ForRead, openErased: false);
                        if (obj != null && !obj.IsErased) count++;
                    }
                    catch { }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Reads every primary registered annotation handle and its small live
        /// presentation contract without opening any object for write.  Malformed,
        /// duplicated or missing handles remain visible to VERIFY instead of being
        /// collapsed into a permissive count.
        /// </summary>
        internal static AnnotationRegistryReadResult ReadAnnotationContractEvidence(
            Transaction tr, Database db, string logicalKey,
            SectionOfficeBlockFingerprintDiagnostics? fingerprintDiagnostics = null)
        {
            var result = new AnnotationRegistryReadResult();
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictName)) return result;
                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
                var key = SafeKey(logicalKey);
                if (!dict.Contains(key)) return result;
                result.EntryExists = true;

                var xrec = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForRead);
                if (xrec.Data == null)
                    throw new InvalidOperationException("Annotation registry record has no data.");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.Value is not string value || value.StartsWith("P:", StringComparison.Ordinal))
                        continue;
                    if (!TryParsePrimaryEntry(value, out var primaryHex, out var registeredFingerprint) ||
                        !long.TryParse(primaryHex, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var hv))
                        throw new InvalidOperationException($"Invalid annotation handle '{value}' in registry.");

                    var hex = hv.ToString("X", CultureInfo.InvariantCulture);
                    if (!seen.Add(hex))
                        throw new InvalidOperationException($"Duplicate annotation handle '{hex}' in registry.");

                    Entity? entity = null;
                    if (db.TryGetObjectId(new Handle(hv), out var id) && !id.IsErased)
                    {
                        try
                        {
                            entity = tr.GetObject(id, OpenMode.ForRead, openErased: false) as Entity;
                            if (entity?.IsErased == true) entity = null;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"Registered annotation {hex} could not be opened for verification.", ex);
                        }
                    }

                    string? layer = null;
                    string? liveFingerprint = null;
                    string? text = null;
                    string? textStyleName = null;
                    double[]? textPosition = null;
                    double[]? textAlignmentPoint = null;
                    double? textHeight = null;
                    double? textRotation = null;
                    double? textWidthFactor = null;
                    double? textOblique = null;
                    int? textHorizontalMode = null;
                    int? textVerticalMode = null;
                    bool? textMirroredInX = null;
                    bool? textMirroredInY = null;
                    string? blockDefinitionName = null;
                    var blockDefinitionId = ObjectId.Null;
                    string? blockDefinitionComments = null;
                    string? blockDefinitionGeometrySha256 = null;
                    bool? blockDefinitionUsesByBlockColor = null;
                    var blockDefinitionMatchesPinnedSource = false;
                    double[]? blockPosition = null;
                    double[]? blockScaleFactors = null;
                    double? blockRotation = null;
                    short? colorIndex = null;
                    double[]? lineEndpoints = null;
                    int? lineWeight = null;
                    var ownershipValid = false;
                    if (entity != null)
                    {
                        layer = entity.Layer;
                        liveFingerprint = SectionProjectionAnnotationSemantics.Fingerprint(entity);
                        var ownership = SectionOwnershipService.Read(tr, entity);
                        ownershipValid = registeredFingerprint != null &&
                            ownership != null &&
                            string.Equals(ownership.Feature, "sections",
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(ownership.Role, "annotation",
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(ownership.LogicalKey, logicalKey,
                                StringComparison.Ordinal) &&
                            string.Equals(ownership.InputFingerprint, liveFingerprint,
                                StringComparison.OrdinalIgnoreCase);
                        colorIndex = (short)entity.ColorIndex;
                        lineWeight = (int)entity.LineWeight;
                        if (entity is DBText dbText)
                        {
                            text = dbText.TextString;
                            textStyleName = dbText.TextStyleName;
                            var position = dbText.Position;
                            var alignment = dbText.HorizontalMode == TextHorizontalMode.TextLeft &&
                                            dbText.VerticalMode == TextVerticalMode.TextBase
                                ? position
                                : dbText.AlignmentPoint;
                            textPosition = new[] { position.X, position.Y, position.Z };
                            textAlignmentPoint = new[]
                                { alignment.X, alignment.Y, alignment.Z };
                            textHeight = dbText.Height;
                            textRotation = dbText.Rotation;
                            textWidthFactor = dbText.WidthFactor;
                            textOblique = dbText.Oblique;
                            textHorizontalMode = (int)dbText.HorizontalMode;
                            textVerticalMode = (int)dbText.VerticalMode;
                            textMirroredInX = dbText.IsMirroredInX;
                            textMirroredInY = dbText.IsMirroredInY;
                        }
                        else if (entity is MText mText)
                        {
                            text = mText.Text;
                        }
                        else if (entity is BlockReference blockReference)
                        {
                            if (tr.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead)
                                is not BlockTableRecord definition)
                            {
                                throw new InvalidOperationException(
                                    $"Registered block reference {hex} has no readable definition.");
                            }
                            blockDefinitionName = definition.Name;
                            blockDefinitionId = blockReference.BlockTableRecord;
                            blockDefinitionComments = definition.Comments;
                            blockDefinitionGeometrySha256 =
                                fingerprintDiagnostics == null
                                    ? SectionVehicleBlockService.GeometryFingerprint(tr, definition)
                                    : SectionVehicleBlockService.ReadFingerprintWithDiagnostics(
                                        tr, definition, fingerprintDiagnostics);
                            blockDefinitionUsesByBlockColor =
                                SectionTrafficDirectionArrowService.UsesByBlockDisplay(
                                    tr, definition);
                            blockDefinitionMatchesPinnedSource =
                                SectionVehicleBlockService.MatchesPinnedOfficeSource(tr, definition);
                            var p = blockReference.Position;
                            var s = blockReference.ScaleFactors;
                            blockPosition = new[] { p.X, p.Y, p.Z };
                            blockScaleFactors = new[] { s.X, s.Y, s.Z };
                            blockRotation = blockReference.Rotation;
                        }
                        else if (entity is Line line)
                        {
                            lineEndpoints = new[]
                            {
                                line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z,
                                line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z,
                            };
                        }
                    }

                    result.Entries.Add(new LiveSectionAnnotationEvidence(
                        hex,
                        entity?.ObjectId ?? ObjectId.Null,
                        entity != null,
                        entity?.GetType().Name,
                        layer,
                        registeredFingerprint,
                        liveFingerprint,
                        text,
                        textStyleName,
                        textPosition,
                        textAlignmentPoint,
                        textHeight,
                        textRotation,
                        textWidthFactor,
                        textOblique,
                        textHorizontalMode,
                        textVerticalMode,
                        textMirroredInX,
                        textMirroredInY,
                        blockDefinitionName,
                        blockDefinitionId,
                        blockDefinitionComments,
                        blockDefinitionGeometrySha256,
                        blockDefinitionUsesByBlockColor,
                        blockPosition,
                        blockScaleFactors,
                        blockRotation,
                        colorIndex,
                        lineEndpoints,
                        lineWeight,
                        ownershipValid)
                    { BlockDefinitionMatchesPinnedSource = blockDefinitionMatchesPinnedSource });
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Returns a fail-closed visual envelope for the exact registered primary
        /// annotations. Every handle and semantic fingerprint is re-read; a stale,
        /// legacy or unreadable entry makes the envelope invalid instead of shrinking
        /// the visible footprint used by layout verification.
        /// </summary>
        internal static AnnotationEnvelopeEvidence ReadEnvelope(
            Transaction tr, Database db, string logicalKey)
        {
            try
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictName))
                    return new AnnotationEnvelopeEvidence(
                        SafeKey(logicalKey), false, null, "Annotation registry is absent.");
                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
                var key = SafeKey(logicalKey);
                if (!dict.Contains(key))
                    return new AnnotationEnvelopeEvidence(
                        key, false, null, "Annotation registry entry is absent.");
                return ReadEnvelopeAtKey(tr, db, dict, key);
            }
            catch (Exception ex)
            {
                return new AnnotationEnvelopeEvidence(
                    SafeKey(logicalKey), false, null, ex.Message);
            }
        }

        /// <summary>
        /// Reads every other registered annotation envelope. This also covers
        /// annotations drawn around a reused manual SectionView, which intentionally
        /// has no Mahod ownership metadata on the Civil object itself.
        /// </summary>
        internal static IReadOnlyList<AnnotationEnvelopeEvidence> ReadOtherEnvelopes(
            Transaction tr, Database db, string selfLogicalKey)
        {
            var results = new List<AnnotationEnvelopeEvidence>();
            var selfKey = SafeKey(selfLogicalKey);
            try
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictName)) return results;
                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (string.Equals(entry.Key, selfKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    results.Add(ReadEnvelopeAtKey(tr, db, dict, entry.Key));
                }
            }
            catch (Exception ex)
            {
                results.Add(new AnnotationEnvelopeEvidence(
                    "annotation-registry", false, null, ex.Message));
            }
            return results;
        }

        private static AnnotationEnvelopeEvidence ReadEnvelopeAtKey(
            Transaction tr, Database db, DBDictionary dict, string key)
        {
            try
            {
                if (tr.GetObject(dict.GetAt(key), OpenMode.ForRead) is not Xrecord xrec ||
                    xrec.Data == null)
                    throw new InvalidOperationException(
                        $"Annotation registry entry {key} has no readable Xrecord data.");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.TypeCode != (int)DxfCode.Text || tv.Value is not string value)
                        throw new InvalidOperationException(
                            $"Annotation registry entry {key} contains non-text data.");
                    if (value.StartsWith("P:", StringComparison.Ordinal)) continue;
                    if (!TryParsePrimaryEntry(value, out var primaryHex,
                            out var registeredFingerprint) ||
                        !long.TryParse(primaryHex, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var hv))
                        throw new InvalidOperationException(
                            $"Annotation registry entry {key} contains invalid primary evidence.");
                    var hex = hv.ToString("X", CultureInfo.InvariantCulture);
                    if (!seen.Add(hex))
                        throw new InvalidOperationException(
                            $"Annotation registry entry {key} repeats handle {hex}.");
                    if (!db.TryGetObjectId(new Handle(hv), out var id) || id.IsErased ||
                        tr.GetObject(id, OpenMode.ForRead, openErased: false) is not Entity entity ||
                        entity.IsErased)
                        throw new InvalidOperationException(
                            $"Annotation registry entry {key} points to missing entity {hex}.");
                    if (!SectionAnnotationResourceContracts.IsKnownAnnotationLayer(entity.Layer))
                        throw new InvalidOperationException(
                            $"Annotation registry entry {key} points to foreign-layer entity {hex}.");
                    var liveFingerprint = SectionProjectionAnnotationSemantics.Fingerprint(entity);
                    if (registeredFingerprint != null &&
                        !string.Equals(registeredFingerprint, liveFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Annotation registry entity {hex} differs from its recorded fingerprint.");

                    Extents3d ext;
                    try { ext = entity.GeometricExtents; }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Annotation registry entity {hex} has unreadable extents.", ex);
                    }
                    var values = new[]
                    {
                        ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y,
                    };
                    if (values.Any(v => !double.IsFinite(v)) ||
                        values[2] < values[0] || values[3] < values[1])
                        throw new InvalidOperationException(
                            $"Annotation registry entity {hex} has invalid extents.");
                    minX = Math.Min(minX, values[0]);
                    minY = Math.Min(minY, values[1]);
                    maxX = Math.Max(maxX, values[2]);
                    maxY = Math.Max(maxY, values[3]);
                }

                if (seen.Count == 0 || !double.IsFinite(minX) || !double.IsFinite(minY) ||
                    !double.IsFinite(maxX) || !double.IsFinite(maxY) ||
                    maxX <= minX || maxY <= minY)
                    throw new InvalidOperationException(
                        $"Annotation registry entry {key} has no positive-area visual envelope.");
                return new AnnotationEnvelopeEvidence(
                    key, true, new[] { minX, minY, maxX, maxY }, null);
            }
            catch (Exception ex)
            {
                return new AnnotationEnvelopeEvidence(key, false, null, ex.Message);
            }
        }

        /// <summary>
        /// Proves that the dedicated annotation layer and the registry describe the
        /// same complete entity set. This detects a handle removed from an Xrecord:
        /// without the global inventory the old entity survives re-APPLY as a visible
        /// orphan while the newly written registry and VERIFY agree with each other.
        /// Legacy registered entities without per-entity ownership are allowed only
        /// as an upgrade input; they can never be UNCHANGED and the next APPLY writes
        /// exact ownership to every replacement.
        /// </summary>
        internal static AnnotationInventoryEvidence ValidateOwnedLayerInventory(
            Transaction tr, Database db, IReadOnlyCollection<string>? recoveryLogicalKeys = null)
        {
            var problems = new List<string>();
            var registered = new Dictionary<string, (string Key, string? Fingerprint, bool LegacyRaw)>(
                StringComparer.OrdinalIgnoreCase);
            var registeredEvidence = new List<AnnotationInventoryLogic.Registered>();
            var layerEvidence = new List<AnnotationInventoryLogic.LayerEntity>();
            var layerHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadEntries = new List<AnnotationInventoryLogic.Registered>();
            var projectionEntries = new List<(string Key, string Value)>();
            var registryDataHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (nod.Contains(DictName))
                {
                    var dict = (DBDictionary)tr.GetObject(
                        nod.GetAt(DictName), OpenMode.ForRead);
                    foreach (DBDictionaryEntry entry in dict)
                    {
                        if (tr.GetObject(entry.Value, OpenMode.ForRead) is not Xrecord xrec ||
                            xrec.Data == null)
                        {
                            problems.Add($"registry {entry.Key} has no readable Xrecord");
                            continue;
                        }
                        registryDataHashes[entry.Key] = RegistryDataHash(xrec);
                        foreach (TypedValue tv in xrec.Data)
                        {
                            if (tv.TypeCode != (int)DxfCode.Text || tv.Value is not string value)
                            {
                                problems.Add($"registry {entry.Key} contains non-text data");
                                continue;
                            }
                            if (value.StartsWith("P:", StringComparison.Ordinal))
                            {
                                projectionEntries.Add((entry.Key, value));
                                continue;
                            }
                            if (!TryParsePrimaryEntry(value, out var primaryHex,
                                    out var fingerprint) ||
                                !long.TryParse(primaryHex, NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out var hv))
                            {
                                problems.Add($"registry {entry.Key} contains legacy/invalid primary evidence");
                                continue;
                            }
                            var hex = hv.ToString("X", CultureInfo.InvariantCulture);
                            var legacyRaw = !value.StartsWith("A:", StringComparison.Ordinal);
                            if (!legacyRaw && string.IsNullOrWhiteSpace(fingerprint))
                            {
                                problems.Add($"registry {entry.Key} contains invalid primary evidence");
                                continue;
                            }
                            if (!registered.TryAdd(hex, (entry.Key, fingerprint, legacyRaw)))
                                problems.Add($"annotation {hex} is registered more than once");
                            else if (!legacyRaw)
                                registeredEvidence.Add(new AnnotationInventoryLogic.Registered(
                                    hex, entry.Key, fingerprint!));
                        }
                    }
                }

                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.IsNull || id.IsErased) continue;
                    Entity? entity;
                    try { entity = tr.GetObject(id, OpenMode.ForRead, openErased: false) as Entity; }
                    catch (Exception ex)
                    {
                        problems.Add($"model-space entity {id.Handle} is unreadable: {ex.Message}");
                        continue;
                    }
                    if (entity == null ||
                        !SectionAnnotationResourceContracts.IsKnownAnnotationLayer(entity.Layer))
                        continue;

                    var hex = entity.Handle.ToString();
                    layerHandles.Add(hex);
                    if (!registered.TryGetValue(hex, out var proof))
                    {
                        problems.Add($"owned annotation layer contains unregistered entity {hex}");
                        layerEvidence.Add(new AnnotationInventoryLogic.LayerEntity(
                            hex, string.Empty,
                            AnnotationInventoryLogic.OwnershipState.LegacyAbsent));
                        continue;
                    }
                    string liveFingerprint;
                    try { liveFingerprint = SectionProjectionAnnotationSemantics.Fingerprint(entity); }
                    catch (Exception ex)
                    {
                        problems.Add($"annotation {hex} fingerprint is unreadable: {ex.Message}");
                        continue;
                    }
                    if (!proof.LegacyRaw &&
                        !string.Equals(liveFingerprint, proof.Fingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        problems.Add($"annotation {hex} differs from registry fingerprint");

                    if (proof.LegacyRaw)
                    {
                        // The bare handle is an explicit registry entry, not a layer
                        // adoption heuristic.  Bind it to the live fingerprint solely
                        // for the two-way topology proof; PLAN still sees no registered
                        // fingerprint and therefore forces UPDATE/migration.
                        OwnershipMetadata? legacyOwnership;
                        try { legacyOwnership = SectionOwnershipService.Read(tr, entity); }
                        catch (Exception ex)
                        {
                            problems.Add($"legacy annotation {hex} ownership is corrupt: {ex.Message}");
                            continue;
                        }
                        if (legacyOwnership != null &&
                            !(string.Equals(legacyOwnership.Feature, "sections",
                                  StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(legacyOwnership.Role, "annotation",
                                  StringComparison.OrdinalIgnoreCase) &&
                              !string.IsNullOrWhiteSpace(legacyOwnership.LogicalKey) &&
                              string.Equals(SafeKey(legacyOwnership.LogicalKey), proof.Key,
                                  StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(legacyOwnership.InputFingerprint, liveFingerprint,
                                  StringComparison.OrdinalIgnoreCase)))
                        {
                            problems.Add(
                                $"legacy annotation {hex} carries foreign or mismatched ownership");
                            layerEvidence.Add(new AnnotationInventoryLogic.LayerEntity(
                                hex, liveFingerprint,
                                AnnotationInventoryLogic.OwnershipState.Invalid));
                            continue;
                        }
                        registeredEvidence.Add(new AnnotationInventoryLogic.Registered(
                            hex, proof.Key, liveFingerprint));
                        layerEvidence.Add(new AnnotationInventoryLogic.LayerEntity(
                            hex, liveFingerprint,
                            AnnotationInventoryLogic.OwnershipState.LegacyAbsent));
                        continue;
                    }

                    OwnershipMetadata? ownership;
                    try { ownership = SectionOwnershipService.Read(tr, entity); }
                    catch (Exception ex)
                    {
                        problems.Add($"annotation {hex} ownership is corrupt: {ex.Message}");
                        continue;
                    }
                    if (ownership == null)
                    {
                        layerEvidence.Add(new AnnotationInventoryLogic.LayerEntity(
                            hex, liveFingerprint,
                            AnnotationInventoryLogic.OwnershipState.LegacyAbsent));
                        continue; // registered legacy upgrade input
                    }
                    var ownershipValid =
                        string.Equals(ownership.Feature, "sections",
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ownership.Role, "annotation",
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(ownership.LogicalKey) &&
                        string.Equals(SafeKey(ownership.LogicalKey), proof.Key,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ownership.InputFingerprint, liveFingerprint,
                            StringComparison.OrdinalIgnoreCase);
                    layerEvidence.Add(new AnnotationInventoryLogic.LayerEntity(
                        hex, liveFingerprint, ownershipValid
                            ? AnnotationInventoryLogic.OwnershipState.Valid
                            : AnnotationInventoryLogic.OwnershipState.Invalid));
                    if (!ownershipValid)
                        problems.Add($"annotation {hex} ownership does not bind its registry entry");
                }

                foreach (var legacy in registered.Where(item => item.Value.LegacyRaw &&
                             !layerHandles.Contains(item.Key)))
                    registeredEvidence.Add(new AnnotationInventoryLogic.Registered(
                        legacy.Key, legacy.Value.Key, string.Empty));

                if (recoveryLogicalKeys is { Count: > 0 })
                {
                    // Projection references cannot independently authorise removal.
                    // Every one must still bind an identical primary entry.
                    foreach (var projection in projectionEntries)
                    {
                        if (!TryParseProjectionEntry(projection.Value, out _, out var hex, out var fingerprint) ||
                            !long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv) ||
                            !registered.TryGetValue(hv.ToString("X", CultureInfo.InvariantCulture), out var primary) ||
                            !string.Equals(primary.Key, projection.Key, StringComparison.OrdinalIgnoreCase) ||
                            (fingerprint != null && !string.Equals(fingerprint, primary.Fingerprint,
                                StringComparison.OrdinalIgnoreCase)))
                            problems.Add($"registry {projection.Key} has an unbound projection entry");
                    }
                    var lookups = registeredEvidence.Where(x => !layerHandles.Contains(x.Handle))
                        .Select(x => new AnnotationInventoryLogic.HandleLookup(x.Handle,
                            ProbeHandleState(tr, db, x.Handle))).ToList();
                    var recovery = AnnotationInventoryLogic.EvaluateRecovery(
                        registeredEvidence, layerEvidence, lookups, recoveryLogicalKeys.Select(SafeKey));
                    problems.AddRange(recovery.Problems);
                    if (problems.Count == 0) deadEntries.AddRange(recovery.DeadEntries);
                }
                else
                {
                    var topology = AnnotationInventoryLogic.Evaluate(registeredEvidence, layerEvidence);
                    problems.AddRange(topology.Problems);
                }
            }
            catch (Exception ex)
            {
                problems.Add("annotation inventory failed: " + ex.Message);
            }

            return new AnnotationInventoryEvidence(
                problems.Count == 0, layerHandles.Count, registered.Count,
                problems.OrderBy(problem => problem, StringComparer.Ordinal).ToList())
                { DeadEntries = deadEntries, RegistryDataHashes = registryDataHashes };
        }

        private static string RegistryDataHash(Xrecord record) => ArtifactHash.Sha256OfText(
            string.Join("\n", (record.Data ?? throw new InvalidOperationException("Registry has no data."))
                .Cast<TypedValue>().Select(value => $"{value.TypeCode}:{value.Value}")));

        private static AnnotationInventoryLogic.HandleState ProbeHandleState(
            Transaction tr, Database db, string hex)
        {
            try
            {
                if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    return AnnotationInventoryLogic.HandleState.Unreadable;
                if (!db.TryGetObjectId(new Handle(value), out var id))
                    return AnnotationInventoryLogic.HandleState.Missing;
                if (id.IsNull) return AnnotationInventoryLogic.HandleState.Unreadable;
                if (id.IsErased) return AnnotationInventoryLogic.HandleState.Erased;
                var obj = tr.GetObject(id, OpenMode.ForRead, openErased: false);
                return obj == null ? AnnotationInventoryLogic.HandleState.Unreadable :
                    obj.IsErased ? AnnotationInventoryLogic.HandleState.Erased :
                    AnnotationInventoryLogic.HandleState.Live;
            }
            catch { return AnnotationInventoryLogic.HandleState.Unreadable; }
        }

        /// <summary>Prunes only independently proven dead registry references, never
        /// live entities. Caller owns the transaction and must abort on any later failure.</summary>
        internal static int RepairDeadEntries(
            Transaction tr, Database db, IReadOnlyCollection<string> eligibleLogicalKeys)
        {
            var evidence = ValidateOwnedLayerInventory(tr, db, eligibleLogicalKeys);
            if (!evidence.IsValid)
                throw new InvalidOperationException(string.Join(" | ", evidence.Problems));
            if (evidence.DeadEntries.Count == 0) return 0;
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
            var prepared = new List<(string Key, Xrecord Record, string ExpectedHash, TypedValue[] Remaining)>();
            foreach (var group in evidence.DeadEntries.GroupBy(x => x.RegistryKey, StringComparer.OrdinalIgnoreCase))
            {
                var xrec = (Xrecord)tr.GetObject(dict.GetAt(group.Key), OpenMode.ForRead);
                if (!evidence.RegistryDataHashes.TryGetValue(group.Key, out var expectedHash) ||
                    RegistryDataHash(xrec) != expectedHash)
                    throw new InvalidOperationException("Registry changed after recovery evidence was captured.");
                var dead = group.Select(x => x.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remaining = new List<TypedValue>();
                foreach (TypedValue tv in xrec.Data ?? throw new InvalidOperationException("Registry changed during repair."))
                {
                    var value = tv.Value as string ?? throw new InvalidOperationException("Invalid registry entry.");
                    string hex;
                    var parsed = value.StartsWith("P:", StringComparison.Ordinal)
                        ? TryParseProjectionEntry(value, out _, out hex, out _)
                        : TryParsePrimaryEntry(value, out hex, out _);
                    if (!parsed || !long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv))
                        throw new InvalidOperationException("Invalid registry handle during repair.");
                    hex = hv.ToString("X", CultureInfo.InvariantCulture);
                    if (!dead.Contains(hex)) { remaining.Add(tv); continue; }
                    if (ProbeHandleState(tr, db, hex) is not (AnnotationInventoryLogic.HandleState.Missing or
                        AnnotationInventoryLogic.HandleState.Erased))
                        throw new InvalidOperationException($"Annotation {hex} is no longer proven dead.");
                }
                prepared.Add((group.Key, xrec, expectedHash, remaining.ToArray()));
            }
            // Complete all read-only validation before opening any registry object for write.
            if (prepared.Any(item => RegistryDataHash(item.Record) != item.ExpectedHash))
                throw new InvalidOperationException("Registry changed before recovery mutation.");
            foreach (var item in prepared)
            {
                if (!item.Record.IsWriteEnabled) item.Record.UpgradeOpen();
                if (item.Remaining.Length == 0)
                {
                    if (!dict.IsWriteEnabled) dict.UpgradeOpen();
                    dict.Remove(item.Key);
                    item.Record.Erase();
                }
                else item.Record.Data = new ResultBuffer(item.Remaining);
            }
            var strict = ValidateOwnedLayerInventory(tr, db);
            if (!strict.IsValid)
                throw new InvalidOperationException("Annotation registry repair did not restore strict inventory: " +
                    string.Join(" | ", strict.Problems));
            return evidence.DeadEntries.Count;
        }

        internal static string RegistryKeyFor(string logicalKey) => SafeKey(logicalKey);

        private static bool TryParsePrimaryEntry(
            string value, out string handleHex, out string? fingerprint)
        {
            handleHex = string.Empty;
            fingerprint = null;
            if (value.StartsWith("A:", StringComparison.Ordinal))
            {
                var separator = value.IndexOf(':', 2);
                if (separator <= 2) return false;
                handleHex = value.Substring(2, separator - 2);
                fingerprint = value.Substring(separator + 1);
                return fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit) &&
                       long.TryParse(handleHex, NumberStyles.HexNumber,
                           CultureInfo.InvariantCulture, out _);
            }

            // Legacy primary entries held only the handle. Keep them readable so
            // re-APPLY can erase them, but VERIFY treats the absent fingerprint as
            // unproven and refuses a green result.
            handleHex = value;
            return long.TryParse(handleHex, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out _);
        }

        /// <summary>Live annotation counts keyed by exact planned projection identity.</summary>
        internal static Dictionary<string, int> ReadProjectionEvidence(
            Transaction tr, Database db, string logicalKey)
        {
            var read = ReadProjectionSemanticEvidence(tr, db, logicalKey);
            if (!read.IsValid) return new Dictionary<string, int>(StringComparer.Ordinal);
            return read.Entries.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Count(v => v.LiveFingerprint != null),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// Registry expectations paired with fingerprints recomputed from the live
        /// entities. Legacy entries without a semantic hash remain readable, but are
        /// deliberately unproven so VERIFY requires one clean re-apply.
        /// </summary>
        internal static ProjectionRegistryReadResult ReadProjectionSemanticEvidence(
            Transaction tr, Database db, string logicalKey)
        {
            var result = new ProjectionRegistryReadResult();
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictName)) return result;
                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictName), OpenMode.ForRead);
                var key = SafeKey(logicalKey);
                if (!dict.Contains(key)) return result;
                var xrec = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForRead);
                if (xrec.Data == null)
                    throw new InvalidOperationException("Annotation registry record has no data.");

                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.Value is not string value || !value.StartsWith("P:", StringComparison.Ordinal))
                        continue;
                    if (!TryParseProjectionEntry(value, out var projectionKey, out var hex, out var expected))
                        throw new InvalidOperationException($"Invalid projection registry entry '{value}'.");

                    string? live = null;
                    if (long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv) &&
                        db.TryGetObjectId(new Handle(hv), out var id) && !id.IsErased)
                    {
                        try
                        {
                            if (tr.GetObject(id, OpenMode.ForRead, openErased: false) is Entity entity &&
                                !entity.IsErased)
                                live = SectionProjectionAnnotationSemantics.Fingerprint(entity);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"Projected annotation {hex} could not be read for verification.", ex);
                        }
                    }

                    if (!result.Entries.TryGetValue(projectionKey, out var entries))
                        result.Entries[projectionKey] = entries = new List<LiveProjectionAnnotationEvidence>();
                    entries.Add(new LiveProjectionAnnotationEvidence(hex, expected, live));
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private static bool TryParseProjectionEntry(
            string value, out string projectionKey, out string handleHex, out string? fingerprint)
        {
            projectionKey = handleHex = string.Empty;
            fingerprint = null;
            var lastColon = value.LastIndexOf(':');
            if (lastColon <= 2) return false;
            var tail = value.Substring(lastColon + 1);

            // Current format: P:<key>:<handle>:<64-char SHA-256>. Legacy format had
            // no final fingerprint and must remain parseable for safe migration.
            if (tail.Length == 64 && tail.All(Uri.IsHexDigit))
            {
                fingerprint = tail.ToLowerInvariant();
                var handleColon = value.LastIndexOf(':', lastColon - 1);
                if (handleColon <= 2) return false;
                projectionKey = value.Substring(2, handleColon - 2);
                handleHex = value.Substring(handleColon + 1, lastColon - handleColon - 1);
            }
            else
            {
                projectionKey = value.Substring(2, lastColon - 2);
                handleHex = tail;
            }
            return projectionKey.Length > 0 &&
                   long.TryParse(handleHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        }

        /// <summary>Dictionary keys reject some characters a logical key may carry.</summary>
        private static string SafeKey(string logicalKey) =>
            "K" + ArtifactHash.Sha256OfText(logicalKey).Substring(0, 24);
    }

    internal static class SectionProjectionAnnotationSemantics
    {
        /// <summary>
        /// Canonical transparency text without touching Alpha/IsClear/IsSolid on a
        /// ByLayer or ByBlock value (native eInvalidKey). Null = invalid contract.
        /// </summary>
        internal static string? TransparencyText(Autodesk.AutoCAD.Colors.Transparency value) =>
            value.IsByAlpha ? "ByAlpha:" + value.Alpha.ToString(CultureInfo.InvariantCulture)
            : value.IsByLayer ? "ByLayer"
            : value.IsByBlock ? "ByBlock"
            : null;

        internal static string Fingerprint(Entity entity)
        {
            var geometry = new List<double>();
            string? text = null;
            string? lineType = null;
            switch (entity)
            {
                case Line line:
                    geometry.AddRange(new[]
                    {
                        line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z,
                        line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z,
                    });
                    lineType = line.Linetype;
                    break;
                case Circle circle:
                    geometry.AddRange(new[]
                    {
                        circle.Center.X, circle.Center.Y, circle.Center.Z, circle.Radius,
                        circle.Normal.X, circle.Normal.Y, circle.Normal.Z,
                    });
                    break;
                case DBText dbText:
                    var alignment = dbText.HorizontalMode == TextHorizontalMode.TextLeft &&
                                    dbText.VerticalMode == TextVerticalMode.TextBase
                        ? dbText.Position
                        : dbText.AlignmentPoint;
                    geometry.AddRange(new[]
                    {
                        dbText.Position.X, dbText.Position.Y, dbText.Position.Z,
                        alignment.X, alignment.Y, alignment.Z,
                        dbText.Height, dbText.Rotation, dbText.WidthFactor, dbText.Oblique,
                        dbText.IsMirroredInX ? 1 : 0, dbText.IsMirroredInY ? 1 : 0,
                        dbText.Normal.X, dbText.Normal.Y, dbText.Normal.Z,
                        dbText.Thickness,
                        (int)dbText.HorizontalMode, (int)dbText.VerticalMode,
                    });
                    text = dbText.TextString + "\u001fstyle=" + dbText.TextStyleName +
                           "\u001fannotative=" + dbText.Annotative +
                           "\u001fpaper-orientation=" + dbText.PaperOrientation;
                    break;
                case Polyline polyline:
                    geometry.Add(polyline.Elevation);
                    geometry.Add(polyline.Closed ? 1 : 0);
                    geometry.Add(polyline.Normal.X);
                    geometry.Add(polyline.Normal.Y);
                    geometry.Add(polyline.Normal.Z);
                    for (var i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        var point = polyline.GetPoint2dAt(i);
                        geometry.Add(point.X);
                        geometry.Add(point.Y);
                        geometry.Add(polyline.GetBulgeAt(i));
                        geometry.Add(polyline.GetStartWidthAt(i));
                        geometry.Add(polyline.GetEndWidthAt(i));
                    }
                    lineType = polyline.Linetype;
                    break;
                case BlockReference block:
                    geometry.AddRange(new[]
                    {
                        block.Position.X, block.Position.Y, block.Position.Z,
                        block.ScaleFactors.X, block.ScaleFactors.Y, block.ScaleFactors.Z,
                        block.Rotation,
                        block.Normal.X, block.Normal.Y, block.Normal.Z,
                    });
                    text = block.Name;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported projected annotation type {entity.GetType().Name}.");
            }

            if (geometry.Count == 0 || geometry.Any(value => !double.IsFinite(value)))
                throw new InvalidOperationException(
                    $"Annotation {entity.Handle} has empty or non-finite geometry evidence.");

            var color = entity.Color;
            int? argb = color.IsByLayer || color.IsByBlock
                ? null
                : color.ColorValue.ToArgb();
            var colorMethod = color.ColorMethod.ToString();
            var layer = entity.Layer;
            var visible = entity.Visible;
            var lineWeight = (int)entity.LineWeight;
            var effectiveLinetype = entity.Linetype;
            var linetypeScale = entity.LinetypeScale;
            if (string.IsNullOrWhiteSpace(layer) || string.IsNullOrWhiteSpace(effectiveLinetype) ||
                !double.IsFinite(linetypeScale) || linetypeScale <= 0)
                throw new InvalidOperationException(
                    $"Annotation {entity.Handle} has an unreadable layer/linetype contract.");
            var value = entity.Transparency;
            // Alpha / IsClear / IsSolid are defined only for a ByAlpha value: on a
            // ByLayer or ByBlock value the native getters throw eInvalidKey (live 07/09
            // 07:12 — the first decoration to reach this fingerprint died here). A
            // ByLayer/ByBlock annotation is opaque through the annotation-layer and
            // protected-block contracts that are verified separately.
            if (!visible || (value.IsByAlpha && value.Alpha < byte.MaxValue))
                throw new InvalidOperationException(
                    $"Annotation {entity.Handle} is hidden or translucent.");
            var transparency = TransparencyText(value) ?? throw new InvalidOperationException(
                $"Annotation {entity.Handle} has an invalid transparency contract.");
            lineType = $"{lineType ?? effectiveLinetype ?? string.Empty}" +
                       $"\u001feffective-linetype={effectiveLinetype ?? "(unreadable)"}" +
                       $"\u001flinetype-scale={linetypeScale.ToString("R", CultureInfo.InvariantCulture)}" +
                       $"\u001ftransparency={transparency}" +
                       $"\u001fvisible={visible}\u001flineweight={lineWeight}";

            return SectionProjectionLogic.ProjectionAnnotationFingerprint(
                new SectionProjectionLogic.ProjectionAnnotationSemantic(
                    entity.GetType().Name, geometry, (short)entity.ColorIndex, argb,
                    colorMethod, text, lineType, layer));
        }
    }
}
