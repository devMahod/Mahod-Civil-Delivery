using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Opens a DWG for reading no matter who else has it open.
    ///
    /// The naive ReadDwgFile(path, FileShare.Read, …) DEMANDS that nobody holds the file
    /// for writing — and AutoCAD holds every open drawing for writing. So the moment an
    /// engineer had CL.dwg open in another tab, "בחר קובץ CL" died with a raw
    /// eFileSharingViolation (caught live 2026-08-26), while the guide explicitly promises
    /// the CL drawing may stay open. Three levels, most faithful first:
    ///
    ///   1. The drawing is open in THIS session → use that document's database directly.
    ///      No file access at all, and the engineer's unsaved edits are included.
    ///   2. Side database with FileShare.ReadWrite → tolerates writers elsewhere.
    ///   3. Shadow copy → stream the bytes (sharing-tolerant) to %TEMP% and read the copy;
    ///      the copy is deleted on dispose.
    /// </summary>
    public sealed class SideDwg : IDisposable
    {
        public Database Db { get; }

        /// <summary>How the drawing was reached — recorded in logs so support can tell.</summary>
        public string Source { get; }

        private readonly bool _ownsDb;
        private readonly string? _tempCopy;

        private SideDwg(Database db, string source, bool ownsDb, string? tempCopy)
        {
            Db = db;
            Source = source;
            _ownsDb = ownsDb;
            _tempCopy = tempCopy;
        }

        public static SideDwg OpenReadOnly(string path)
        {
            var full = Path.GetFullPath(path);

            // 1. Already open in this session: the live database is the truth (it even
            //    carries unsaved edits) and touches no file lock. Never disposed here —
            //    the document owns it.
            try
            {
                foreach (Document doc in AcadApp.DocumentManager)
                {
                    var name = doc.Database?.Filename;
                    if (!string.IsNullOrEmpty(name) &&
                        string.Equals(Path.GetFullPath(name), full, StringComparison.OrdinalIgnoreCase))
                        return new SideDwg(doc.Database!, "open-document", ownsDb: false, tempCopy: null);
                }
            }
            catch { /* headless contexts have no DocumentManager */ }

            // 2. Side database, tolerating a writer elsewhere (another session, the network).
            try
            {
                var db = new Database(false, true);
                db.ReadDwgFile(full, FileShare.ReadWrite, allowCPConversion: true, password: null);
                db.CloseInput(true);
                return new SideDwg(db, "side-database", ownsDb: true, tempCopy: null);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // fall through to the shadow copy
            }
            catch (IOException)
            {
                // fall through to the shadow copy
            }

            // 3. Shadow copy. FileStream with ReadWrite|Delete sharing reads bytes that
            //    File.Copy (and ReadDwgFile) cannot.
            var temp = Path.Combine(Path.GetTempPath(),
                "mhd_sidedwg_" + Guid.NewGuid().ToString("N") + ".dwg");
            using (var src = new FileStream(full, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }

            try
            {
                var db = new Database(false, true);
                db.ReadDwgFile(temp, FileShare.ReadWrite, allowCPConversion: true, password: null);
                db.CloseInput(true);
                return new SideDwg(db, "shadow-copy", ownsDb: true, tempCopy: temp);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            if (_ownsDb)
            {
                try { Db.Dispose(); } catch { }
            }
            if (_tempCopy != null)
            {
                try { File.Delete(_tempCopy); } catch { }
            }
        }
    }
}
