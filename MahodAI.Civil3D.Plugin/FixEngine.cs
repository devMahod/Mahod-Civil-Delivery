using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms; // רק בשביל FolderBrowserDialog
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcDb = Autodesk.AutoCAD.DatabaseServices;

namespace MahodAI.Civil3D.Plugin
{
    public static class FixEngine
    {
        public sealed class FixResult
        {
            public List<string> Applied { get; } = new();
            public List<string> Info { get; } = new();
            public List<string> Failed { get; } = new();
            public string HtmlSummary { get; set; } = "";
        }

        public static FixResult ApplyFixes(IReadOnlyCollection<string> fixIds)
        {
            var result = new FixResult();

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager
                .MdiActiveDocument;

            if (doc == null)
            {
                result.Failed.Add("אין מסמך פעיל.");
                result.HtmlSummary = WrapHtml("<p>❌ אין מסמך פעיל.</p>");
                return result;
            }

            var db = doc.Database;
            var ed = doc.Editor;

            if (fixIds == null || fixIds.Count == 0)
            {
                result.Info.Add("לא נבחרו תיקונים.");
                result.HtmlSummary = WrapHtml("<p>ℹ️ לא נבחרו תיקונים לביצוע.</p>");
                return result;
            }

            using (doc.LockDocument())
            {
                try
                {
                    ed.Command("._UNDO", "_BEGIN");
                }
                catch
                {
                }

                try
                {
                    foreach (var rawId in fixIds.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var id = rawId.Trim();
                        if (string.IsNullOrEmpty(id))
                            continue;

                        try
                        {
                            switch (id.ToUpperInvariant())
                            {
                                case "FIX-PURGE-REGAPPS":
                                {
                                    int removed = PurgeRegApps(db);
                                    if (removed > 0)
                                        result.Applied.Add($"FIX-PURGE-REGAPPS – הוסרו {removed} רשומות RegApp.");
                                    else
                                        result.Info.Add("FIX-PURGE-REGAPPS – לא נמצאו RegApps לניקוי.");
                                    break;
                                }

                                case "FIX-PURGE-BASIC":
                                {
                                    bool ok = RunBasicPurge(ed);
                                    if (ok)
                                        result.Applied.Add("FIX-PURGE-BASIC – בוצע PURGE בסיסי (ALL).");
                                    else
                                        result.Info.Add("FIX-PURGE-BASIC – PURGE לא בוצע (ביטול או כשל).");
                                    break;
                                }

                                case "FIX-NORMALIZE-UNITS":
                                {
                                    bool changed = NormalizeUnits(db);
                                    if (changed)
                                        result.Applied.Add("FIX-NORMALIZE-UNITS – עודכנו הגדרות INSUNITS.");
                                    else
                                        result.Info.Add("FIX-NORMALIZE-UNITS – לא נדרש שינוי ב-INSUNITS.");
                                    break;
                                }

                                case "FIX-FREEZE-EMPTY-LAYERS":
                                {
                                    int frozen = FreezeEmptyLayers(db);
                                    if (frozen > 0)
                                        result.Applied.Add($"FIX-FREEZE-EMPTY-LAYERS – הוקפאו {frozen} שכבות ריקות.");
                                    else
                                        result.Info.Add("FIX-FREEZE-EMPTY-LAYERS – לא נמצאו שכבות ריקות להקפאה.");
                                    break;
                                }

                                case "FIX-RELINK-XREFS":
                                {
                                    var status = RelinkXrefs(db, ed);
                                    if (status == RelinkStatus.Applied)
                                        result.Applied.Add("FIX-RELINK-XREFS – עודכנו נתיבי XREF ונטענו מחדש.");
                                    else if (status == RelinkStatus.UserCancelled)
                                        result.Info.Add("FIX-RELINK-XREFS – בוטל על ידי המשתמש.");
                                    else
                                        result.Info.Add("FIX-RELINK-XREFS – לא נמצאו XREF מתאימים לעדכון.");
                                    break;
                                }

                                default:
                                    result.Info.Add($"{rawId} – מזהה תיקון לא מוכר (דילוג).");
                                    break;
                            }
                        }
                        catch (System.Exception exFix)
                        {
                            result.Failed.Add($"{rawId} – שגיאה: {exFix.Message}");
                        }
                    }
                }
                finally
                {
                    try
                    {
                        ed.Command("._UNDO", "_END");
                    }
                    catch
                    {
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("<h3>🔧 דוח תיקונים אוטומטיים</h3><ul>");

            foreach (var a in result.Applied)
                sb.Append("<li>✅ " + Html(a) + "</li>");
            foreach (var i in result.Info)
                sb.Append("<li>ℹ️ " + Html(i) + "</li>");
            foreach (var f in result.Failed)
                sb.Append("<li>⚠️ " + Html(f) + "</li>");

            if (result.Applied.Count == 0 &&
                result.Info.Count == 0 &&
                result.Failed.Count == 0)
            {
                sb.Append("<li>ℹ️ לא בוצעו שינויים.</li>");
            }

            sb.Append("</ul>");
            sb.Append("<p><em>כל הפעולות בוצעו בקבוצת UNDO אחת (כפוף להצלחת הפקודות). ניתן לבטל ב-UNDO.</em></p>");

            result.HtmlSummary = WrapHtml(sb.ToString());
            return result;
        }

        #region Fix Implementations

        private static int PurgeRegApps(Database db)
        {
            int removed = 0;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var regTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                    var toErase = new List<ObjectId>();

                    foreach (ObjectId id in regTable)
                    {
                        var rec = (RegAppTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!rec.IsErased &&
                            !string.Equals(rec.Name, "ACAD", StringComparison.OrdinalIgnoreCase))
                        {
                            toErase.Add(id);
                        }
                    }

                    if (toErase.Count > 0)
                    {
                        regTable.UpgradeOpen();

                        foreach (var id in toErase)
                        {
                            try
                            {
                                var rec = (RegAppTableRecord)tr.GetObject(id, OpenMode.ForWrite, false);
                                if (rec != null && !rec.IsErased)
                                {
                                    // מספיק Erase על הרשומה; אין צורך ב-RegAppTable.Remove
                                    rec.Erase(true);
                                    removed++;
                                }
                            }
                            catch
                            {
                                // מתעלמים משגיאה על רשומה בודדת
                            }
                        }
                    }

                    tr.Commit();
                }
            }
            catch
            {
            }

            return removed;
        }

        private static bool RunBasicPurge(Editor ed)
        {
            try
            {
                ed.Command("._-PURGE", "_ALL", "*", "_N");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool NormalizeUnits(Database db)
        {
            try
            {
                if (db.Insunits == UnitsValue.Undefined)
                {
                    db.Insunits = UnitsValue.Meters;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static int FreezeEmptyLayers(Database db)
        {
            int frozen = 0;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead, false) is AcDb.Entity e &&
                            !string.IsNullOrEmpty(e.Layer))
                        {
                            used.Add(e.Layer);
                        }
                    }

                    foreach (ObjectId lid in lt)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (ltr.IsErased || ltr.IsFrozen)
                            continue;

                        if (!used.Contains(ltr.Name))
                        {
                            try
                            {
                                ltr.UpgradeOpen();
                                ltr.IsFrozen = true;
                                frozen++;
                            }
                            catch
                            {
                            }
                        }
                    }

                    tr.Commit();
                }
            }
            catch
            {
            }

            return frozen;
        }

        private enum RelinkStatus
        {
            None,
            Applied,
            UserCancelled
        }

        private static RelinkStatus RelinkXrefs(Database db, Editor ed)
        {
            try
            {
                using (var dlg = new FolderBrowserDialog
                {
                    Description = "בחר תיקיית בסיס לעדכון נתיבי XREF:",
                    ShowNewFolderButton = false
                })
                {
                    var dr = dlg.ShowDialog();
                    if (dr != DialogResult.OK ||
                        string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    {
                        return RelinkStatus.UserCancelled;
                    }

                    string baseDir = dlg.SelectedPath;
                    bool any = false;

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                        foreach (ObjectId id in bt)
                        {
                            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (!btr.IsFromExternalReference)
                                continue;

                            if (btr.XrefStatus == XrefStatus.Resolved)
                                continue;

                            string fileName = SafeGetFileName(btr.PathName);
                            if (string.IsNullOrWhiteSpace(fileName))
                                continue;

                            string candidate = System.IO.Path.Combine(baseDir, fileName);
                            if (System.IO.File.Exists(candidate))
                            {
                                try
                                {
                                    btr.UpgradeOpen();
                                    btr.PathName = candidate;
                                    any = true;
                                }
                                catch
                                {
                                }
                            }
                        }

                        tr.Commit();
                    }

                    if (any)
                    {
                        try
                        {
                            ed.Command("._XREF", "_RELOAD", "*", "");
                        }
                        catch
                        {
                        }

                        return RelinkStatus.Applied;
                    }

                    return RelinkStatus.None;
                }
            }
            catch
            {
                return RelinkStatus.None;
            }
        }

        private static string SafeGetFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return System.IO.Path.GetFileName(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion

        #region HTML Helpers

        private static string WrapHtml(string body)
        {
            return
"<!DOCTYPE html><html dir='rtl' lang='he'><head><meta charset='UTF-8'/>" +
"<style>body{font-family:'Segoe UI','Tahoma';direction:rtl;padding:12px;background:#ffffff;color:#111827;}" +
"h3{color:#111827;border-bottom:2px solid #e5e7eb;padding-bottom:4px;margin-top:0;margin-bottom:8px;}" +
"ul{margin:4px 0 8px 0;padding-right:20px;}li{margin-bottom:3px;}</style></head><body>" +
body + "</body></html>";
        }

        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            return s
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        #endregion
    }
}