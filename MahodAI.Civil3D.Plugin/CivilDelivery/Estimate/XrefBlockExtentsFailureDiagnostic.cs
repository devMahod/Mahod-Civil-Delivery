using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Evidence only, after an external ordinary INSERT's extents have failed.
/// Never measures, resolves a filename, changes classification, or opens a DWG.
/// A borrowed source database is supplied only after the traversal's existing
/// saved-snapshot check succeeded; this helper never owns/disposes that database.
/// </summary>
internal static class XrefBlockExtentsFailureDiagnostic
{
    internal const int MaximumSources = 128;
    internal const int MaximumChildren = 256;
    internal const string ArtifactName = "xref_block_extents_failure_diagnostics.json";
    internal sealed record Property(string Name, object? Value, string? Error);
    internal sealed record Child(string? Handle, string? Type, string? Error);
    internal sealed record Children(IReadOnlyList<Child> Items, bool Truncated, string? Error);
    internal sealed record Block(IReadOnlyList<Property> Reference,
        IReadOnlyList<Property> Definition, Children Children);
    internal sealed record ReadResult(Block? Block, string? Error);
    internal sealed record Snapshot(string? RequestedLeafHandle, ReadResult Loaded,
        string? BorrowedDatabaseFilename, ReadResult Original, string? AssociationError);
    internal sealed record SourceSnapshot(ProvenanceRef Source, string OriginalExtentsError,
        Snapshot Evidence);

    internal sealed class Batch
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        public string Purpose => "read-only loaded/original INSERT comparison after extents failure; no quantity or classification decision";
        public List<SourceSnapshot> Sources { get; } = new();
        public int DuplicateAttempts { get; private set; }
        public int OmittedByDiagnosticLimit { get; private set; }

        public void Add(ProvenanceRef source, string originalError, Func<Snapshot> read)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(new[] {
                source.SourcePathOrUri, source.DrawingChecksum, source.SourceHandle, source.XrefPath });
            if (_seen.Contains(key)) { DuplicateAttempts++; return; }
            if (Sources.Count >= MaximumSources) { OmittedByDiagnosticLimit++; return; }
            _seen.Add(key);
            Snapshot snapshot;
            try { snapshot = read(); }
            catch (System.Exception ex)
            {
                snapshot = new(null, new(null, Error(ex)), null,
                    new(null, "not read: diagnostic callback failed"), "diagnostic callback failed");
            }
            Sources.Add(new(source, originalError, snapshot));
        }
    }

    // A pure delegate boundary: unavailable/mismatched identity does not invoke
    // the original-object callback, and every failed read remains explicit.
    internal static Snapshot Capture(ProvenanceRef source, Func<Block> readLoaded,
        Func<string?>? readBorrowedFilename, Func<string, Block>? readOriginal)
    {
        var loaded = Read(readLoaded);
        var leaf = source.SourceHandle?.Split('/').LastOrDefault();
        var association = SourceIdentityFailure(source, leaf);
        string? filename = null;
        if (association == null && (readBorrowedFilename == null || readOriginal == null))
            association = "verified loaded parent database unavailable";
        if (association == null)
        {
            try
            {
                filename = readBorrowedFilename!();
                if (string.IsNullOrWhiteSpace(filename) ||
                    !string.Equals(Path.GetFullPath(filename), Path.GetFullPath(source.SourcePathOrUri!),
                        StringComparison.OrdinalIgnoreCase))
                    association = "borrowed database filename does not match the verified source path";
            }
            catch (System.Exception ex) { association = "borrowed database filename: " + Error(ex); }
        }
        var original = association == null
            ? Read(() => readOriginal!(leaf!))
            : new ReadResult(null, "not read: " + association);
        return new(leaf, loaded, filename, original, association);
    }

    private static string? SourceIdentityFailure(ProvenanceRef source, string? leaf)
    {
        if (!string.Equals(source.SourceKind, "xref", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(source.SourcePathOrUri) ||
            !CatalogIdentity.IsValidSha256(source.DrawingChecksum) ||
            string.IsNullOrWhiteSpace(source.XrefPath) ||
            !string.Equals(source.EntityType, "BlockReference", StringComparison.OrdinalIgnoreCase))
            return "incomplete external BlockReference source identity";
        if (string.IsNullOrWhiteSpace(source.SourceHandle) ||
            source.SourceHandle.Split('/').Any(part => !long.TryParse(part,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value <= 0) ||
            string.IsNullOrWhiteSpace(leaf))
            return "invalid full source handle path";
        return null;
    }

    internal static IReadOnlyList<Property> CaptureProperties(
        params (string Name, Func<object?> Read)[] readers) => readers.Select(reader =>
    {
        try { return new Property(reader.Name, reader.Read(), null); }
        catch (System.Exception ex) { return new Property(reader.Name, null, Error(ex)); }
    }).ToArray();

    internal static Children CaptureChildren(Func<IEnumerable<Func<Child>>> enumerate)
    {
        var items = new List<Child>();
        try
        {
            foreach (var read in enumerate())
            {
                if (items.Count == MaximumChildren) return new(items, true, null);
                try { items.Add(read()); }
                catch (System.Exception ex) { items.Add(new(null, null, Error(ex))); }
            }
            return new(items, false, null);
        }
        catch (System.Exception ex) { return new(items, false, Error(ex)); }
    }

    internal static Snapshot CaptureNative(BlockReference loaded, Transaction hostTransaction,
        ProvenanceRef source, Database? verifiedLoadedParent) => Capture(source,
        () => ReadNativeBlock(loaded, hostTransaction),
        verifiedLoadedParent == null ? null : () => verifiedLoadedParent.Filename,
        verifiedLoadedParent == null ? null : leaf =>
        {
            // createIfNotFound=false; a failed lookup is evidence, never a new object.
            var id = verifiedLoadedParent.GetObjectId(false,
                new Handle(long.Parse(leaf, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)), 0);
            if (id.IsNull || id.IsErased) throw new InvalidOperationException("original leaf is null or erased");
            using var sourceTransaction = verifiedLoadedParent.TransactionManager.StartOpenCloseTransaction();
            var obj = sourceTransaction.GetObject(id, OpenMode.ForRead);
            if (obj is not BlockReference original)
                throw new InvalidOperationException("original leaf is not BlockReference: " + obj.GetType().FullName);
            if (!string.Equals(original.Handle.ToString(), leaf, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("original leaf handle mismatch");
            var evidence = ReadNativeBlock(original, sourceTransaction);
            sourceTransaction.Abort();
            return evidence;
        });

    private static Block ReadNativeBlock(BlockReference reference, Transaction tr)
    {
        var referenceProperties = CaptureProperties(
            ("handle", () => reference.Handle.ToString()), ("name", () => reference.Name),
            ("layer", () => reference.Layer), ("owner_handle", () => reference.OwnerId.Handle.ToString()),
            ("block_definition_handle", () => reference.BlockTableRecord.Handle.ToString()),
            ("position", () => Values(reference.Position.X, reference.Position.Y, reference.Position.Z)),
            ("normal", () => Values(reference.Normal.X, reference.Normal.Y, reference.Normal.Z)),
            ("scale", () => Values(reference.ScaleFactors.X, reference.ScaleFactors.Y, reference.ScaleFactors.Z)),
            ("rotation", () => Number(reference.Rotation)));
        BlockTableRecord definition;
        try { definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead); }
        catch (System.Exception ex)
        {
            return new(referenceProperties, new[] { new Property("open_definition", null, Error(ex)) },
                new(Array.Empty<Child>(), false, "not read: definition unavailable"));
        }
        var properties = CaptureProperties(
            ("handle", () => definition.Handle.ToString()), ("name", () => definition.Name),
            ("path_name", () => definition.PathName),
            ("is_from_external_reference", () => definition.IsFromExternalReference),
            ("is_from_overlay_reference", () => definition.IsFromOverlayReference),
            ("is_resolved", () => definition.IsResolved), ("is_unloaded", () => definition.IsUnloaded),
            ("is_anonymous", () => definition.IsAnonymous), ("is_layout", () => definition.IsLayout));
        return new(referenceProperties, properties, CaptureChildren(Readers));

        IEnumerable<Func<Child>> Readers()
        {
            foreach (ObjectId id in definition)
                yield return () =>
                {
                    var child = tr.GetObject(id, OpenMode.ForRead);
                    return new(child.Handle.ToString(), child.GetType().FullName, null);
                };
        }
    }

    private static ReadResult Read(Func<Block> read)
    {
        try
        {
            var block = read();
            return block == null ? new(null, "block reader returned null") : new(block, null);
        }
        catch (System.Exception ex) { return new(null, Error(ex)); }
    }
    private static string Error(System.Exception ex) => ex.GetType().Name + ": " + ex.Message;
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string[] Values(params double[] values) => Array.ConvertAll(values, Number);
}
