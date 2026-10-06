using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.Commands;
using MahodAI.Civil3D.Plugin.Utilities;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using Exception = System.Exception;

[assembly: CommandClass(typeof(ListAssembliesCommand))]

namespace MahodAI.Civil3D.Plugin.Commands
{
    /// <summary>
    /// P1-01 inventory: opens every DWG/DWT under the firm content locations (and the current
    /// drawing) as a read-only side database and lists ALL Civil 3D assemblies it contains,
    /// with each assembly's subassemblies. This is the "extract them all" survey — it tells us
    /// which ready-made road cross-sections already exist (in __Mahod 2027.dwt, Roundabout
    /// Assemblies.dwg, project files, …) so the MahodAI library can be assembled by importing
    /// them by name rather than rebuilding from subassemblies (which the Civil 3D .NET API
    /// cannot do reliably for DLL-based subassemblies).
    ///
    /// Writes %LOCALAPPDATA%\MahodAI_Civil3D\assembly_inventory_&lt;ts&gt;.json (+ _latest).
    /// Run interactively: MAHOD_LIST_ASSEMBLIES.
    /// </summary>
    public class ListAssembliesCommand
    {
        // Roots scanned for DWG/DWT files that may contain assemblies. Missing roots are skipped.
        private static readonly string[] ScanRoots = new[]
        {
            @"C:\WORK\2027\MAHOD_3D",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "C3D 2027", "enu", "Assemblies"),
        };

        [CommandMethod("MAHOD_LIST_ASSEMBLIES", CommandFlags.Modal)]
        public void Run()
        {
            var startedAt = DateTime.Now;
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            var ed = doc?.Editor;
            ed?.WriteMessage("\n[MAHOD_LIST_ASSEMBLIES] Surveying assemblies in the current drawing and firm content…\n");

            var files = new List<DrawingInventory>();

            // 1) The current drawing (in-memory — do NOT re-open it as a side DB).
            try
            {
                if (doc != null)
                {
                    var inv = InventoryCurrentDrawing(doc);
                    if (inv != null) files.Add(inv);
                }
            }
            catch (Exception ex)
            {
                MahodLogger.Warning($"LIST_ASSEMBLIES current-drawing scan failed: {ex.Message}");
            }

            // 2) Every DWG/DWT under the scan roots, as read-only side databases. Skip the
            // currently-open drawing — it's already inventoried above and re-opening it as a
            // side DB just yields eFileSharingViolation.
            string? openPath = null;
            try { openPath = doc?.Name; } catch { }
            foreach (var path in EnumerateCandidateFiles())
            {
                if (!string.IsNullOrEmpty(openPath) &&
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(openPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    files.Add(InventorySideFile(path));
                }
                catch (Exception ex)
                {
                    files.Add(new DrawingInventory { File = path, Error = ex.Message });
                }
            }

            int totalAssemblies = 0;
            foreach (var f in files) totalAssemblies += f.Assemblies.Count;

            // ── Report ────────────────────────────────────────────────────────
            ed?.WriteMessage($"\n[MAHOD_LIST_ASSEMBLIES] {totalAssemblies} assembly(ies) across {files.Count} drawing(s):\n");
            foreach (var f in files)
            {
                if (!string.IsNullOrEmpty(f.Error))
                {
                    ed?.WriteMessage($"  ⚠ {Short(f.File)} — {f.Error}\n");
                    continue;
                }
                if (f.Assemblies.Count == 0) continue;
                ed?.WriteMessage($"  {Short(f.File)}:\n");
                foreach (var a in f.Assemblies)
                    ed?.WriteMessage($"     • {a.Name}  ({a.SubassemblyCount} subassemblies: {string.Join(", ", a.Subassemblies)})\n");
            }

            var report = new
            {
                run_id = startedAt.ToString("yyyyMMdd_HHmmss"),
                started_at = startedAt.ToString("o"),
                scan_roots = ScanRoots,
                total_assemblies = totalAssemblies,
                drawings = files,
                note = "Assemblies found here can be imported into MahodAI_Assemblies_2027.dwg (rename to MahodAI_<road_type>) and consumed by clone_assembly_from_library.",
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? reportPath = TryWriteReport(startedAt, json);
            ed?.WriteMessage($"\nReport: {reportPath}\n");
            MahodLogger.Info($"LIST_ASSEMBLIES total={totalAssemblies} drawings={files.Count} report={reportPath ?? "<write-failed>"}");
        }

        private static IEnumerable<string> EnumerateCandidateFiles()
        {
            foreach (var root in ScanRoots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> found;
                try
                {
                    var dwg = Directory.EnumerateFiles(root, "*.dwg", SearchOption.AllDirectories);
                    var dwt = Directory.EnumerateFiles(root, "*.dwt", SearchOption.AllDirectories);
                    var list = new List<string>();
                    list.AddRange(dwg);
                    list.AddRange(dwt);
                    found = list;
                }
                catch (Exception ex)
                {
                    MahodLogger.Warning($"LIST_ASSEMBLIES enumerate '{root}' failed: {ex.Message}");
                    continue;
                }
                foreach (var f in found) yield return f;
            }
        }

        private static DrawingInventory? InventoryCurrentDrawing(Document doc)
        {
            var db = doc.Database;
            CivilDocumentHolder civil;
            try { civil = new CivilDocumentHolder(Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db)); }
            catch { return null; }

            var inv = new DrawingInventory { File = doc.Name + " (current)" };
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                CollectAssemblies(tr, db, civil.Doc, inv);
                tr.Commit();
            }
            return inv;
        }

        private static DrawingInventory InventorySideFile(string path)
        {
            var inv = new DrawingInventory { File = path };
            using var sideDb = new Database(false, true);
            sideDb.ReadDwgFile(path, FileShare.Read, allowCPConversion: true, password: null);
            Autodesk.Civil.ApplicationServices.CivilDocument civilDoc;
            try { civilDoc = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(sideDb); }
            catch { inv.Error = "not a Civil 3D drawing"; return inv; }

            using var tr = sideDb.TransactionManager.StartTransaction();
            CollectAssemblies(tr, sideDb, civilDoc, inv);
            tr.Commit();
            return inv;
        }

        private static void CollectAssemblies(
            Transaction tr, Database db, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, DrawingInventory inv)
        {
            // Assemblies come from TWO places: the CivilDocument.AssemblyCollection (populated
            // for an open drawing) AND raw model-space Assembly entities (how library/template
            // DWGs store them when not open). Union both, de-duped by ObjectId, so unopened
            // side-database drawings (stock, __Mahod 2027.dwt, firm libraries) are captured too.
            var seen = new HashSet<ObjectId>();
            var assemblyIds = new List<ObjectId>();
            foreach (ObjectId id in civilDoc.AssemblyCollection)
                if (seen.Add(id)) assemblyIds.Add(id);
            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                    if (tr.GetObject(id, OpenMode.ForRead) is CivilAssembly && seen.Add(id))
                        assemblyIds.Add(id);
            }
            catch { /* model-space scan best-effort */ }

            foreach (ObjectId aid in assemblyIds)
            {
                if (tr.GetObject(aid, OpenMode.ForRead) is not CivilAssembly asm) continue;
                var a = new AssemblyInfo { Name = asm.Name };
                try
                {
                    // Version-tolerant traversal (GetSubassemblyIds() is non-public); read each
                    // subassembly's Name via reflection to stay robust across API versions.
                    foreach (ObjectId sid in GetSubassemblyIds(asm))
                    {
                        if (sid == ObjectId.Null) continue;
                        var sub = tr.GetObject(sid, OpenMode.ForRead);
                        var name = sub?.GetType().GetProperty("Name")?.GetValue(sub)?.ToString();
                        a.Subassemblies.Add(string.IsNullOrEmpty(name) ? "Subassembly" : name!);
                    }
                }
                catch (Exception ex)
                {
                    a.Subassemblies.Add($"<error: {ex.Message}>");
                }
                a.SubassemblyCount = a.Subassemblies.Count;
                inv.Assemblies.Add(a);
            }
        }

        /// <summary>
        /// Version-tolerant subassembly-id traversal (mirrors CloneAssemblyFromLibraryTool):
        /// (1) Assembly.GetSubassemblyIds(); else per group (2) group.GetSubassemblyIds() and
        /// (3) group.Subassemblies → each item's ObjectId. Swallows per-path errors.
        /// </summary>
        private static List<ObjectId> GetSubassemblyIds(object assembly)
        {
            var ids = new List<ObjectId>();
            try
            {
                var method = assembly.GetType().GetMethod("GetSubassemblyIds", Type.EmptyTypes);
                if (method?.Invoke(assembly, null) is System.Collections.IEnumerable direct)
                {
                    foreach (var item in direct)
                        if (item is ObjectId oid) ids.Add(oid);
                    if (ids.Count > 0) return ids;
                }
            }
            catch { }

            try
            {
                if (assembly.GetType().GetProperty("Groups")?.GetValue(assembly) is System.Collections.IEnumerable groups)
                {
                    foreach (var group in groups)
                    {
                        try
                        {
                            var getSubs = group.GetType().GetMethod("GetSubassemblyIds", Type.EmptyTypes);
                            if (getSubs?.Invoke(group, null) is System.Collections.IEnumerable subIds)
                                foreach (var item in subIds)
                                    if (item is ObjectId oid) ids.Add(oid);
                        }
                        catch { }

                        try
                        {
                            if (group.GetType().GetProperty("Subassemblies")?.GetValue(group)
                                is System.Collections.IEnumerable subs)
                            {
                                foreach (var sub in subs)
                                    if (sub?.GetType().GetProperty("ObjectId")?.GetValue(sub)
                                        is ObjectId oid && oid != ObjectId.Null && !ids.Contains(oid))
                                        ids.Add(oid);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return ids;
        }

        private static string Short(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }

        private static string? TryWriteReport(DateTime startedAt, string json)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D");
                Directory.CreateDirectory(dir);
                var stamped = Path.Combine(dir, $"assembly_inventory_{startedAt:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(stamped, json);
                File.WriteAllText(Path.Combine(dir, "assembly_inventory_latest.json"), json);
                return stamped;
            }
            catch (Exception ex)
            {
                MahodLogger.Error("LIST_ASSEMBLIES failed to write report", ex);
                return null;
            }
        }

        // Tiny holder so the current-drawing path can bail cleanly when it isn't a Civil doc.
        private sealed class CivilDocumentHolder
        {
            public Autodesk.Civil.ApplicationServices.CivilDocument Doc { get; }
            public CivilDocumentHolder(Autodesk.Civil.ApplicationServices.CivilDocument doc) { Doc = doc; }
        }
    }

    public sealed class DrawingInventory
    {
        public string File { get; set; } = string.Empty;
        public string? Error { get; set; }
        public List<AssemblyInfo> Assemblies { get; set; } = new();
    }

    public sealed class AssemblyInfo
    {
        public string Name { get; set; } = string.Empty;
        public int SubassemblyCount { get; set; }
        public List<string> Subassemblies { get; set; } = new();
    }
}
