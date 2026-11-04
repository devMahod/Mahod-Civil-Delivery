using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The plan references a typical section is cut from, opened for one run and put back after
    /// (<see cref="TypicalPlanReferenceLogic"/>, 2026-11-04): unloaded references that carry the road are loaded, a
    /// reference whose file moved is read from where it is found, and the project's marking plan is overlaid from beside
    /// the geometry plan when the drawing references none. <see cref="Dispose"/> unloads, re-points and detaches them
    /// again, so the drawing is saved as the engineer left it. Call with the document locked and no transaction open.
    /// </summary>
    internal sealed class TypicalPlanReferences : IDisposable
    {
        private readonly Database _db;
        private readonly ObjectIdCollection _loaded = new();
        private readonly List<(ObjectId Id, string Path)> _repathed = new();
        private readonly ObjectIdCollection _attached = new();

        public List<string> Notes { get; } = new();

        /// <summary>Whether a marking plan is among the references after opening (lane lines can be read).</summary>
        public bool HasMarkingPlan { get; private set; }

        private TypicalPlanReferences(Database db) => _db = db;

        public static TypicalPlanReferences Open(Database db)
        {
            var scope = new TypicalPlanReferences(db);
            try { scope.OpenAll(); }
            catch (Exception ex)
            {
                scope.Notes.Add("ההפניות של התנוחה לא נפתחו לקריאה: " + ex.Message);
                MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("[TypicalSections] plan references", ex);
            }
            return scope;
        }

        private void OpenAll()
        {
            var hostFolder = string.IsNullOrEmpty(_db.Filename) ? null : Path.GetDirectoryName(_db.Filename);
            var toLoad = new ObjectIdCollection();
            var loadedNames = new List<string>();
            var notFound = new List<string>();
            var references = new List<(string Name, string? File)>();
            var transform = Autodesk.AutoCAD.Geometry.Matrix3d.Identity;
            var transformFound = false;

            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!record.IsFromExternalReference) continue;
                    var status = record.XrefStatus;
                    if (status == XrefStatus.Unreferenced) continue;
                    var name = record.Name;
                    var file = status == XrefStatus.Resolved ? ResolvedFile(record) : null;
                    var carries = TypicalPlanReferenceLogic.Carries(name) || TypicalPlanReferenceLogic.Carries(record.PathName);
                    if (status is XrefStatus.Unloaded or XrefStatus.FileNotFound or XrefStatus.Unresolved)
                    {
                        var candidate = TypicalPlanReferenceLogic.Candidates(record.PathName, hostFolder).FirstOrDefault(File.Exists);
                        file = candidate;
                        if (carries)
                        {
                            if (candidate == null) notFound.Add(name);
                            else
                            {
                                if (status != XrefStatus.Unloaded || !SamePath(candidate, record.PathName, hostFolder))
                                {
                                    record.UpgradeOpen();
                                    _repathed.Add((id, record.PathName));
                                    record.PathName = candidate;
                                }
                                if (status == XrefStatus.Unloaded) _loaded.Add(id);
                                toLoad.Add(id);
                                loadedNames.Add(name);
                            }
                        }
                    }
                    references.Add((name, file));
                    if (!transformFound && TypicalPlanReferenceLogic.IsGeometry(name))
                    {
                        foreach (ObjectId referenceId in record.GetBlockReferenceIds(true, false))
                        {
                            if (tr.GetObject(referenceId, OpenMode.ForRead) is BlockReference reference && reference.OwnerId == SymbolUtilityServices.GetBlockModelSpaceId(_db))
                            {
                                transform = reference.BlockTransform;
                                transformFound = true;
                                break;
                            }
                        }
                    }
                }
                tr.Commit();
            }
            if (toLoad.Count > 0) _db.ReloadXrefs(toLoad);

            // The lane lines: the project's marking plan from beside its geometry plan, when the drawing references none.
            var attachedNames = new List<string>();
            var marking = TypicalPlanReferenceLogic.MarkingPlans(references, folder => Directory.EnumerateFiles(folder, "*.dwg"));
            HasMarkingPlan = references.Any(r => TypicalPlanReferenceLogic.IsMarking(r.Name) && r.File != null);
            foreach (var file in marking)
            {
                var blockName = UniqueName(Path.GetFileNameWithoutExtension(file));
                var recordId = _db.OverlayXref(file, blockName);
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(_db), OpenMode.ForWrite);
                    var reference = new BlockReference(Autodesk.AutoCAD.Geometry.Point3d.Origin, recordId) { BlockTransform = transform };
                    space.AppendEntity(reference);
                    tr.AddNewlyCreatedDBObject(reference, true);
                    tr.Commit();
                }
                _attached.Add(recordId);
                attachedNames.Add(Path.GetFileNameWithoutExtension(file));
                HasMarkingPlan = true;
            }

            if (TypicalPlanReferenceLogic.OpenedNoteHe(loadedNames, attachedNames) is { } opened) Notes.Add(opened);
            if (TypicalPlanReferenceLogic.NotFoundNoteHe(notFound) is { } missing) Notes.Add(missing);
        }

        private static string? ResolvedFile(BlockTableRecord record)
        {
            try { return record.GetXrefDatabase(false)?.Filename; }
            catch { return null; }
        }

        private static bool SamePath(string candidate, string stored, string? hostFolder)
        {
            try
            {
                var full = Path.IsPathRooted(stored) || hostFolder == null ? Path.GetFullPath(stored) : Path.GetFullPath(Path.Combine(hostFolder, stored));
                return string.Equals(full, candidate, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private string UniqueName(string stem)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var table = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            var name = "MHD-TYP-READ_" + stem;
            for (var i = 2; table.Has(name); i++) name = $"MHD-TYP-READ_{stem}_{i}";
            return name;
        }

        /// <summary>Puts the drawing back: detaches what was overlaid, unloads what was loaded, restores moved paths.</summary>
        public void Dispose()
        {
            foreach (ObjectId id in _attached)
            {
                try { _db.DetachXref(id); }
                catch (Exception ex) { MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("[TypicalSections] detach marking plan", ex); }
            }
            _attached.Clear();
            if (_loaded.Count > 0)
            {
                try { _db.UnloadXrefs(_loaded); }
                catch (Exception ex) { MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("[TypicalSections] unload plan references", ex); }
                _loaded.Clear();
            }
            if (_repathed.Count > 0)
            {
                try
                {
                    var reload = new ObjectIdCollection();
                    using (var tr = _db.TransactionManager.StartTransaction())
                    {
                        foreach (var (id, path) in _repathed)
                        {
                            var record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                            record.PathName = path;
                            if (record.XrefStatus != XrefStatus.Unloaded) reload.Add(id);
                        }
                        tr.Commit();
                    }
                    if (reload.Count > 0) _db.ReloadXrefs(reload);
                }
                catch (Exception ex) { MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("[TypicalSections] restore reference paths", ex); }
                _repathed.Clear();
            }
        }
    }
}
