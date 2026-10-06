using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Recovers only the overlay disposition of an empty anonymous redirected INSERT.
/// Owns separate read-only disk databases, never a loaded/borrowed database. Call
/// ValidateSourcesUnchanged before publishing the traversal; Dispose only releases
/// owned resources. No geometry, quantity, mapping or price is produced here.
/// </summary>
internal sealed class XrefOriginalReferenceCache : IDisposable
{
    internal enum Decision { NotApplicable, ExcludeByOverlay, Refused }
    internal sealed record Result(Decision Decision, string Detail)
    {
        public string? LogicalSourcePath { get; init; }
        public string? ReadSourcePath { get; init; }
        public bool UsedExplicitLocalReadPath { get; init; }
    }
    internal sealed record SourceReadIdentity(string LogicalSourcePath, string ReadSourcePath,
        bool UsedExplicitLocalReadPath);
    private sealed record SourceReadBinding(SourceReadIdentity Identity, string Sha,
        XrefQuantityPolicy.SourceSnapshotIdentity Snapshot);
    internal readonly record struct Vector(double X, double Y, double Z)
    {
        internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
        internal bool IsZero => X == 0 && Y == 0 && Z == 0;
    }
    internal sealed record ReferenceIdentity(string Handle, string OwnerHandle, string Layer,
        Vector Position, Vector Normal, Vector Scale, double Rotation);
    internal sealed record DefinitionIdentity(string Name, string Path,
        bool IsExternal, bool IsOverlay, bool IsAnonymous, bool IsLayout);

    internal sealed record FileStamp(long Length, long LastWriteTicks, long CreationTicks);
    private sealed class Entry(Database database, string sha, FileStamp stamp,
        XrefQuantityPolicy.SourceSnapshotIdentity snapshot) : IDisposable
    {
        internal Database Database { get; } = database;
        internal string Sha { get; } = sha;
        internal FileStamp Stamp { get; } = stamp;
        internal XrefQuantityPolicy.SourceSnapshotIdentity Snapshot { get; } = snapshot;
        public void Dispose() => Database.Dispose();
    }

    internal const int MaximumSources = 64;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refusedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceReadBinding> _sourceReadBindings = new(StringComparer.OrdinalIgnoreCase);
    private string? _failure;
    private bool _disposed;
    internal int CachedSourceCount => _entries.Count;
    internal int SourceDatabaseReadCount { get; private set; }

    internal Result Query(BlockReference loadedReference, BlockTableRecord loadedDefinition,
        string parentSourcePath, string expectedSha256, Database? verifiedLoadedParentDatabase,
        string expectedLeafHandle, string exactSourceLayerPrefix,
        bool isInsideExternalReference, bool hasExternalOverlayAncestor,
        string? explicitLocalReadPath = null)
    {
        if (_disposed) return Refused("original-reference-cache-disposed");
        if (_failure != null) return Refused(_failure);
        if (!isInsideExternalReference) return NotApplicable("not inside an external reference");
        SourceReadIdentity? readIdentity = null;
        Result Finish(Result result) => WithSourceReadIdentity(result, readIdentity);
        try
        {
            var candidate = CheckCandidate(loadedDefinition.IsAnonymous,
                loadedDefinition.IsFromExternalReference, loadedDefinition.IsFromOverlayReference,
                loadedDefinition.IsLayout, () =>
                {
                    // One MoveNext is enough; never enumerate an entire definition.
                    foreach (ObjectId _ in loadedDefinition) return true;
                    return false;
                });
            if (candidate != null) return candidate;
            var requestedDefinitionId = loadedReference.BlockTableRecord;
            var openedDefinitionId = loadedDefinition.ObjectId;
            if (!LoadedDefinitionIdsMatch(requestedDefinitionId, openedDefinitionId))
                return Refused("loaded reference/definition identity mismatch");

            if (!IsHandle(expectedLeafHandle)) return Refused("invalid original leaf handle");
            if (verifiedLoadedParentDatabase == null)
                return Refused("verified loaded parent database unavailable");
            readIdentity = ResolveReadIdentity(parentSourcePath,
                verifiedLoadedParentDatabase.Filename, loadedReference.Database.Filename,
                explicitLocalReadPath);
            if (!IsSha256(expectedSha256)) return Finish(Refused("invalid expected source SHA256"));

            var loadedSnapshot = Snapshot(verifiedLoadedParentDatabase);
            var referenceSnapshot = Snapshot(loadedReference.Database);
            var loadedFailure = XrefQuantityPolicy.LoadedSnapshotFailure(
                referenceSnapshot, loadedSnapshot, loadedSnapshot);
            if (loadedFailure != null) return Finish(Refused(loadedFailure));
            var bindingFailure = BindReadSource(readIdentity, expectedSha256, loadedSnapshot);
            if (bindingFailure != null) return Finish(Refused(bindingFailure));

            var entry = GetOrRead(readIdentity.ReadSourcePath, expectedSha256, loadedSnapshot);
            var currentFailure = XrefQuantityPolicy.LoadedSnapshotFailure(
                loadedSnapshot, entry.Snapshot, Snapshot(entry.Database));
            if (currentFailure != null) return Finish(Refused(currentFailure));
            RequireSafeLocalFile(readIdentity.ReadSourcePath);
            if (Stamp(readIdentity.ReadSourcePath) != entry.Stamp)
                return Finish(FailSource("original-source-file-metadata-changed: " + readIdentity.ReadSourcePath));

            using var tr = entry.Database.TransactionManager.StartOpenCloseTransaction();
            var id = entry.Database.GetObjectId(false,
                new Handle(long.Parse(expectedLeafHandle, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture)), 0);
            if (id.IsNull || id.IsErased ||
                tr.GetObject(id, OpenMode.ForRead) is not BlockReference original)
                return Finish(Refused("original leaf is unavailable or is not a BlockReference"));
            if (original.OwnerId.IsNull ||
                tr.GetObject(original.OwnerId, OpenMode.ForRead) is not BlockTableRecord)
                return Finish(Refused("original reference owner is not a block table record"));
            if (tr.GetObject(original.BlockTableRecord, OpenMode.ForRead) is not BlockTableRecord definition)
                return Finish(Refused("original definition unavailable"));

            return Finish(EvaluateOriginal(Capture(loadedReference), Capture(original),
                new(definition.Name, definition.PathName, definition.IsFromExternalReference,
                    definition.IsFromOverlayReference, definition.IsAnonymous, definition.IsLayout),
                expectedLeafHandle, exactSourceLayerPrefix, isInsideExternalReference,
                hasExternalOverlayAncestor));
        }
        catch (System.Exception ex)
        {
            return Finish(Refused("original-reference-read: " + ex.GetType().Name + ": " + ex.Message));
        }
    }

    // Path association only: never probes the logical (possibly network) source.
    // The caller must supply expectedSha256 from the current verified loaded source,
    // not from the alias itself. A filename or an old receipt alone is insufficient.
    internal static SourceReadIdentity ResolveReadIdentity(string parentSourcePath,
        string? verifiedLoadedFilename, string? loadedReferenceFilename, string? explicitLocalReadPath)
    {
        var logical = CanonicalLocalPath(parentSourcePath);
        if (!SamePath(logical, verifiedLoadedFilename) || !SamePath(logical, loadedReferenceFilename))
            throw new InvalidOperationException("loaded reference/verified parent filename mismatch");
        var read = explicitLocalReadPath == null ? logical : CanonicalLocalPath(explicitLocalReadPath);
        return new(logical, read, explicitLocalReadPath != null);
    }

    internal string? BindReadSource(SourceReadIdentity identity, string sha,
        XrefQuantityPolicy.SourceSnapshotIdentity loadedSnapshot)
    {
        if (_disposed) return "original-reference-cache-disposed";
        if (_failure != null) return _failure;
        if (!IsSha256(sha)) return "invalid expected source SHA256";
        var invalidSnapshot = XrefQuantityPolicy.LoadedSnapshotFailure(loadedSnapshot, loadedSnapshot, loadedSnapshot);
        if (invalidSnapshot != null) return invalidSnapshot;
        if (_sourceReadBindings.TryGetValue(identity.LogicalSourcePath, out var existing))
        {
            if (!SamePath(existing.Identity.ReadSourcePath, identity.ReadSourcePath) ||
                !SameHash(existing.Sha, sha) || !XrefQuantityPolicy.SameSnapshot(existing.Snapshot, loadedSnapshot))
                return _failure = "original-source-read-binding-changed: " + identity.LogicalSourcePath;
            return null;
        }
        if (_sourceReadBindings.Count >= MaximumSources) return "original-source-cache-limit";
        _sourceReadBindings.Add(identity.LogicalSourcePath, new(identity, sha, loadedSnapshot));
        return null;
    }

    internal static Result WithSourceReadIdentity(Result result, SourceReadIdentity? identity) => identity == null
        ? result
        : result with
        {
            LogicalSourcePath = identity.LogicalSourcePath,
            ReadSourcePath = identity.ReadSourcePath,
            UsedExplicitLocalReadPath = identity.UsedExplicitLocalReadPath,
            // Existing callers persist Detail. Keep both identities visible even
            // before a caller adopts the additive structured receipt properties.
            Detail = identity.UsedExplicitLocalReadPath
                ? result.Detail + "; logical-source=" + identity.LogicalSourcePath + "; read-source=" + identity.ReadSourcePath
                : result.Detail,
        };

    /// <summary>Required final gate; hashes once per cached source, not per INSERT.</summary>
    internal string? ValidateSourcesUnchanged()
    {
        if (_disposed) return "original-reference-cache-disposed";
        if (_failure != null) return _failure;
        foreach (var pair in _entries)
        {
            try
            {
                RequireSafeLocalFile(pair.Key);
                if (Stamp(pair.Key) != pair.Value.Stamp || !SameHash(Hash(pair.Key), pair.Value.Sha))
                    return _failure = "original-source-changed-before-publish: " + pair.Key;
                if (!XrefQuantityPolicy.SameSnapshot(pair.Value.Snapshot, Snapshot(pair.Value.Database)))
                    return _failure = "original-source-cache-revision-changed: " + pair.Key;
            }
            catch (System.Exception ex)
            {
                return _failure = "original-source-final-verification: " + pair.Key + ": " + ex.Message;
            }
        }
        return null;
    }

    private Entry GetOrRead(string path, string sha,
        XrefQuantityPolicy.SourceSnapshotIdentity loadedSnapshot)
    {
        if (_refusedSources.TryGetValue(path, out var refused))
            throw new InvalidOperationException(refused);
        if (_entries.TryGetValue(path, out var existing))
        {
            if (!SameHash(existing.Sha, sha))
            {
                _failure = "conflicting-original-source-hash: " + path;
                throw new InvalidOperationException(_failure);
            }
            return existing;
        }
        if (_entries.Count + _refusedSources.Count >= MaximumSources)
            throw new InvalidOperationException("original-source-cache-limit");

        Database? owned = null;
        try
        {
            var before = VerifyInitialSourceFile(path, sha);
            owned = new(false, true);
            SourceDatabaseReadCount++;
            owned.ReadDwgFile(path, FileShare.Read, allowCPConversion: false, password: "");
            owned.CloseInput(true);
            var diskBeforeHash = Snapshot(owned);
            if (!SameHash(Hash(path), sha) || Stamp(path) != before)
                throw new InvalidOperationException("original-source-changed-during-read");
            var diskAfterHash = Snapshot(owned);
            var failure = XrefQuantityPolicy.LoadedSnapshotFailure(
                loadedSnapshot, diskBeforeHash, diskAfterHash);
            if (failure != null) throw new InvalidOperationException(failure);
            var entry = new Entry(owned, sha, before, diskAfterHash);
            _entries.Add(path, entry);
            owned = null; // Ownership transferred to this cache, not to the caller.
            return entry;
        }
        catch (System.Exception ex)
        {
            // A failing source is not reopened/rehashed for every redirected INSERT.
            _refusedSources[path] = ex.GetType().Name + ": " + ex.Message;
            throw;
        }
        finally { owned?.Dispose(); }
    }

    // Detached, pure association policy. Exact values deliberately avoid geometric
    // approximation. Angles may differ only by an exact integral full revolution.
    internal static bool LoadedDefinitionIdsMatch(ObjectId requested, ObjectId opened)
    {
        if (!UsableId(requested) || !UsableId(opened)) return false;
        // Autodesk ObjectId.ConvertToRedirectedId resolves an XREF forwarding ID
        // to the actual ID. Compare both actual IDs, never merely their handles.
        var redirectedRequested = requested.ConvertToRedirectedId();
        var redirectedOpened = opened.ConvertToRedirectedId();
        return UsableId(redirectedRequested) && UsableId(redirectedOpened) &&
               redirectedRequested == redirectedOpened;
    }

    private static bool UsableId(ObjectId id) =>
        !id.IsNull && id.IsValid && !id.IsErased && !id.IsEffectivelyErased;

    internal static Result? CheckCandidate(bool anonymous, bool external, bool overlay, bool layout,
        Func<bool> hasAnyChild)
    {
        if (!anonymous || external || overlay || layout)
            return NotApplicable("loaded definition is not an ordinary anonymous definition");
        try
        {
            return hasAnyChild() ? NotApplicable("loaded anonymous definition is not empty") : null;
        }
        catch (System.Exception ex)
        {
            return Refused("loaded anonymous enumeration failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static Result EvaluateOriginal(ReferenceIdentity loaded, ReferenceIdentity original,
        DefinitionIdentity definition, string expectedLeafHandle, string exactSourceLayerPrefix,
        bool isInsideExternalReference, bool hasExternalOverlayAncestor)
    {
        if (!isInsideExternalReference) return NotApplicable("not inside an external reference");
        if (!IsHandle(expectedLeafHandle) || !IsHandle(loaded.Handle) || !IsHandle(original.Handle) ||
            !SameHandle(loaded.Handle, expectedLeafHandle) || !SameHandle(original.Handle, expectedLeafHandle))
            return Refused("original leaf handle mismatch");
        if (!IsHandle(loaded.OwnerHandle) || !IsHandle(original.OwnerHandle) ||
            !SameHandle(loaded.OwnerHandle, original.OwnerHandle))
            return Refused("original owner handle mismatch");
        if (!ValidGeometry(loaded) || !ValidGeometry(original) ||
            loaded.Position != original.Position || loaded.Normal != original.Normal ||
            loaded.Scale != original.Scale ||
            Math.IEEERemainder(loaded.Rotation - original.Rotation, 2 * Math.PI) != 0)
            return Refused("original reference placement mismatch or unavailable");
        if (!SameLayer(loaded.Layer, original.Layer, exactSourceLayerPrefix))
            return Refused("original reference layer mismatch");
        if (definition.IsLayout || string.IsNullOrWhiteSpace(definition.Name))
            return Refused("original definition metadata unavailable or layout");
        if (!definition.IsExternal && definition.IsOverlay)
            return Refused("original external/overlay metadata inconsistent");
        if (!definition.IsExternal)
            return NotApplicable("original definition is ordinary; retain ordinary measurement handling");
        if (definition.IsAnonymous || string.IsNullOrWhiteSpace(definition.Path))
            return Refused("original external definition metadata incomplete");
        return XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(definition.IsExternal,
            definition.IsOverlay, isInsideExternalReference, hasExternalOverlayAncestor)
            ? new(Decision.ExcludeByOverlay, "verified original external overlay excluded by existing XREF policy; " +
                "name=" + definition.Name + "; path=" + definition.Path + "; leaf=" + original.Handle)
            : Refused("original external reference is not excluded by overlay policy");
    }

    internal static bool SameLayer(string loaded, string original, string exactPrefix) =>
        !string.IsNullOrWhiteSpace(loaded) && !string.IsNullOrWhiteSpace(original) &&
        (string.Equals(loaded, original, StringComparison.Ordinal) ||
         (!string.IsNullOrWhiteSpace(exactPrefix) &&
          string.Equals(loaded, exactPrefix + "|" + original, StringComparison.Ordinal)));

    private static bool ValidGeometry(ReferenceIdentity value) =>
        value.Position.IsFinite && value.Normal.IsFinite && !value.Normal.IsZero &&
        value.Scale.IsFinite && value.Scale.X != 0 && value.Scale.Y != 0 && value.Scale.Z != 0 &&
        double.IsFinite(value.Rotation);
    private static ReferenceIdentity Capture(BlockReference value) => new(
        value.Handle.ToString(), value.OwnerId.Handle.ToString(), value.Layer,
        new(value.Position.X, value.Position.Y, value.Position.Z),
        new(value.Normal.X, value.Normal.Y, value.Normal.Z),
        new(value.ScaleFactors.X, value.ScaleFactors.Y, value.ScaleFactors.Z), value.Rotation);
    private static XrefQuantityPolicy.SourceSnapshotIdentity Snapshot(Database db) =>
        new(db.FingerprintGuid, db.VersionGuid, db.NumberOfSaves, db.Tduupdate.Ticks);
    private static bool IsHandle(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 16 && long.TryParse(value, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out var handle) && handle > 0;
    private static bool SameHandle(string left, string right) =>
        long.Parse(left, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) ==
        long.Parse(right, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    internal static bool IsSha256(string? value) => value?.Length == 64 &&
        System.Linq.Enumerable.All(value, Uri.IsHexDigit);
    private static bool SameHash(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }
    // Same pre-open checks as GetOrRead originally performed, kept independently
    // testable without constructing a native Database. Not a DWG validity proof.
    internal static FileStamp VerifyInitialSourceFile(string path, string expectedSha256)
    {
        RequireSafeLocalFile(path);
        var before = Stamp(path);
        if (!SameHash(Hash(path), expectedSha256))
            throw new InvalidOperationException("original-source-hash-mismatch");
        return before;
    }
    private static bool SamePath(string canonical, string? other) => !string.IsNullOrWhiteSpace(other) &&
        string.Equals(canonical, CanonicalLocalPath(other), StringComparison.OrdinalIgnoreCase);
    internal static string CanonicalLocalPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Length < 3 || !char.IsAsciiLetter(normalized[0]) ||
            normalized[1] != ':' || normalized[2] != '\\' || normalized.IndexOf(':', 2) >= 0)
            throw new InvalidOperationException("original source must be an absolute local drive path");
        return Path.GetFullPath(normalized);
    }
    private static void RequireSafeLocalFile(string path)
    {
        // Guard before existence/attributes/hash; never probe a mapped network drive.
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidOperationException("original source must be on a local drive");
        var ancestors = new Stack<string>();
        for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            ancestors.Push(cursor);
        // Inspect parent directories before any descendant access, so a junction
        // cannot redirect a later attribute/hash/read operation onto the network.
        foreach (var cursor in ancestors)
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("original source path contains a reparse point");
        if (!File.Exists(path)) throw new FileNotFoundException("original source unavailable", path);
    }
    private static Result NotApplicable(string detail) => new(Decision.NotApplicable, detail);
    private static Result Refused(string detail) => new(Decision.Refused, detail);
    private Result FailSource(string detail) { _failure = detail; return Refused(detail); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
        _sourceReadBindings.Clear();
    }
}
