using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Civil adapter for the shared loaded-XREF snapshot policy.  Section collectors
    /// traverse AutoCAD's loaded database, so hashing only PathName can falsely bind
    /// cached old geometry to a newer file.  One validation per XREF definition proves
    /// loaded identity == stable disk identity around the SHA-256 read.
    /// </summary>
    internal static class SectionXrefSnapshotGuard
    {
        internal sealed record Check(
            string? ResolvedPath,
            string? Sha256,
            string? Failure,
            string? Detail)
        {
            public bool IsFresh => Failure == null && !string.IsNullOrWhiteSpace(Sha256);
        }

        internal sealed class Cache
        {
            private readonly Dictionary<ObjectId, Check> _byDefinition = new();

            public Check Validate(BlockTableRecord definition, string? parentSourcePath)
            {
                if (_byDefinition.TryGetValue(definition.ObjectId, out var cached))
                    return cached;
                var result = ValidateCore(definition, parentSourcePath);
                _byDefinition[definition.ObjectId] = result;
                return result;
            }
        }

        private static Check ValidateCore(
            BlockTableRecord definition,
            string? parentSourcePath)
        {
            var resolved = ClInstructionReader.ResolvePath(
                definition.PathName, parentSourcePath);
            if (string.IsNullOrWhiteSpace(resolved))
                return new Check(resolved, null, "xref-path-unresolved",
                    definition.PathName);

            XrefQuantityPolicy.SourceSnapshotIdentity? loaded = null;
            string? loadedDetail = null;
            try
            {
                var loadedDatabase = definition.GetXrefDatabase(false);
                if (loadedDatabase == null)
                    loadedDetail = "GetXrefDatabase returned null";
                else
                    loaded = SnapshotIdentity(loadedDatabase);
            }
            catch (Exception ex)
            {
                loadedDetail = ex.Message;
            }

            if (!TryReadDiskIdentity(resolved, out var before, out var beforeFailure))
                return new Check(resolved, null, "disk-xref-identity-unavailable",
                    beforeFailure);
            var hash = ClInstructionReader.HashFileShared(resolved);
            if (string.IsNullOrWhiteSpace(hash))
                return new Check(resolved, null, "disk-xref-hash-unavailable", resolved);
            if (!TryReadDiskIdentity(resolved, out var after, out var afterFailure))
                return new Check(resolved, null, "disk-xref-identity-unavailable",
                    afterFailure);

            var failure = XrefQuantityPolicy.LoadedSnapshotFailure(loaded, before, after);
            return failure == null
                ? new Check(resolved, hash, null, null)
                : new Check(resolved, hash, failure, loadedDetail);
        }

        private static XrefQuantityPolicy.SourceSnapshotIdentity SnapshotIdentity(
            Database database) =>
            new(database.FingerprintGuid, database.VersionGuid,
                database.NumberOfSaves, database.Tduupdate.Ticks);

        private static bool TryReadDiskIdentity(
            string path,
            out XrefQuantityPolicy.SourceSnapshotIdentity identity,
            out string? failure)
        {
            identity = null!;
            failure = null;
            try
            {
                // Header variables only. CloseInput(true) would page every object of
                // the XREF into memory ("Upgrading old drawing" twice per XREF on each
                // PLAN/APPLY/VERIFY, live 29/09); disposing the database releases the file.
                using var database = new Database(false, true);
                database.ReadDwgFile(
                    path, FileShare.ReadWrite, allowCPConversion: false, password: null);
                identity = SnapshotIdentity(database);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }
    }
}
