using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DbfDataReader;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers
{
    /// <summary>
    /// Analyzes shapefile (.shp) files for GIS data quality and structure
    /// </summary>
    public static class ShapefileAnalyzer
    {
        /// <summary>
        /// Opens a file dialog to select a shapefile
        /// </summary>
        public static string? PromptForShapefilePath()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Shapefile (*.shp)|*.shp",
                    Title = "בחר קובץ SHP לניתוח",
                    CheckFileExists = true,
                    Multiselect = false
                };

                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Analyzes a shapefile and returns summary information
        /// </summary>
        public static ShapefileSummary Analyze(string shpPath)
        {
            var summary = new ShapefileSummary
            {
                FilePath = shpPath
            };

            try
            {
                if (string.IsNullOrWhiteSpace(shpPath) || !File.Exists(shpPath))
                {
                    summary.Issues.Add("קובץ SHP לא נמצא.");
                    return summary;
                }

                string baseDir = Path.GetDirectoryName(shpPath) ?? string.Empty;
                string nameNoExt = Path.GetFileNameWithoutExtension(shpPath);

                string shxPath = Path.Combine(baseDir, nameNoExt + ".shx");
                string dbfPath = Path.Combine(baseDir, nameNoExt + ".dbf");
                string prjPath = Path.Combine(baseDir, nameNoExt + ".prj");

                // Calculate total size
                long size = 0;
                if (File.Exists(shpPath)) size += new FileInfo(shpPath).Length;
                if (File.Exists(shxPath)) size += new FileInfo(shxPath).Length;
                if (File.Exists(dbfPath)) size += new FileInfo(dbfPath).Length;
                summary.TotalSizeBytes = size;

                // Check for additional shapefiles in folder
                try
                {
                    var others = Directory.GetFiles(baseDir, "*.shp")
                        .Where(p => !p.Equals(shpPath, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    summary.HasAdditionalShapefilesInFolder = others.Count > 0;
                }
                catch { }

                // Read CRS from PRJ file
                if (File.Exists(prjPath))
                {
                    summary.CrsWkt = File.ReadAllText(prjPath);
                    summary.CrsEpsg = DetectEpsgFromPrj(summary.CrsWkt);
                }
                else
                {
                    summary.Issues.Add("לא נמצא קובץ PRJ. מומלץ לוודא מערכת קואורדינטות (לדוגמה EPSG:2039).");
                }

                // Analyze geometry
                AnalyzeGeometry(shpPath, summary);

                // Analyze DBF attributes
                if (File.Exists(dbfPath))
                    AnalyzeDbf(dbfPath, summary);
                else
                    summary.Issues.Add("לא נמצא קובץ DBF. לא ניתן לנתח שדות, STATUS ו-ID.");

                return summary;
            }
            catch (Exception ex)
            {
                summary.Issues.Add("שגיאה כללית בניתוח SHP: " + ex.Message);
                return summary;
            }
        }

        private static void AnalyzeGeometry(string shpPath, ShapefileSummary summary)
        {
            try
            {
                var gf = NtsGeometryServices.Instance.CreateGeometryFactory();
                using var reader = new ShapefileDataReader(shpPath, gf);

                int featureCount = 0;
                var geomCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var normGeoms = new List<string>();

                while (reader.Read())
                {
                    if (reader.Geometry is not Geometry g)
                        continue;

                    featureCount++;

                    string type = g.OgcGeometryType.ToString();
                    if (!geomCounts.TryAdd(type, 1))
                        geomCounts[type]++;

                    if (g is Polygon or MultiPolygon)
                    {
                        double area = g.Area;
                        if (area > 0)
                        {
                            summary.TotalAreaSquareMeters += area;
                            if (area < 100) summary.SmallAreaCountUnder100++;
                            if (area > 1_000_000) summary.LargeAreaCountOver1M++;
                        }
                    }

                    if (g is Polygon poly)
                    {
                        if (!poly.Shell.IsClosed)
                            summary.OpenRingCount++;

                        foreach (var hole in poly.Holes)
                            if (!hole.IsClosed)
                                summary.OpenRingCount++;
                    }

                    if (!g.IsValid)
                        summary.SelfIntersectingCount++;

                    try
                    {
                        string wkt = g.Normalized().AsText();
                        normGeoms.Add(wkt);
                    }
                    catch { }
                }

                summary.FeatureCount = featureCount;

                summary.GeometryType = geomCounts.Count > 0
                    ? geomCounts.OrderByDescending(kv => kv.Value).First().Key
                    : "Unknown / Empty";

                // Check for duplicate geometries
                if (normGeoms.Count > 1)
                {
                    var set = new HashSet<string>();
                    int dup = 0;
                    foreach (var w in normGeoms)
                    {
                        if (!set.Add(w))
                            dup++;
                    }

                    summary.DuplicateGeometryCount = dup;
                    if (dup > 0)
                        summary.Issues.Add($"זוהו {dup} גיאומטריות כפולות (Duplicate).");
                }

                if (summary.SelfIntersectingCount > 0)
                    summary.Issues.Add($"זוהו {summary.SelfIntersectingCount} ישויות עם גיאומטריה לא תקינה (Self-Intersecting/Invalid).");

                if (summary.OpenRingCount > 0)
                    summary.Issues.Add($"זוהו {summary.OpenRingCount} טבעות שאינן סגורות (Open Rings).");
            }
            catch (Exception ex)
            {
                summary.Issues.Add("שגיאה בקריאת גיאומטריית SHP: " + ex.Message);
            }
        }

        private static void AnalyzeDbf(string dbfPath, ShapefileSummary summary)
        {
            try
            {
                var options = new DbfDataReaderOptions { Encoding = Encoding.GetEncoding(1255) };
                using var reader = new DbfDataReader.DbfDataReader(dbfPath, options);

                var table = reader.DbfTable;

                int fieldCount = table.Columns.Count;
                var fields = new List<ShapefileFieldInfo>(fieldCount);
                var nullCounts = new int[fieldCount];

                int statusIndex = FindStatusFieldIndex(table);
                int idIndex = FindIdFieldIndex(table);

                var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var idValues = idIndex >= 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                int idDuplicates = 0;

                // Define fields
                for (int i = 0; i < fieldCount; i++)
                {
                    var col = table.Columns[i];
                    var fi = new ShapefileFieldInfo
                    {
                        Name = col.ColumnName,
                        Type = col.ColumnType.ToString(),
                        Length = col.Length,
                        DecimalCount = col.DecimalCount
                    };

                    if (fi.Name.Length > 10)
                        summary.Issues.Add($"שם שדה ארוך מהמותר ב-Shapefile (10): {fi.Name}");

                    if (fi.Name.Any(ch => ch > 127))
                        summary.Issues.Add($"שם שדה {fi.Name} מכיל תווים לא-ASCII. יש לוודא קידוד.");

                    fields.Add(fi);
                }

                int recordCount = 0;

                // Read records
                while (reader.Read())
                {
                    recordCount++;

                    for (int i = 0; i < fieldCount; i++)
                    {
                        object val = reader.GetValue(i);
                        if (val == null || val is DBNull || string.IsNullOrWhiteSpace(val.ToString()))
                            nullCounts[i]++;
                    }

                    // STATUS
                    if (statusIndex >= 0)
                    {
                        var raw = reader.GetValue(statusIndex)?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(raw))
                        {
                            if (!statusCounts.TryAdd(raw, 1))
                                statusCounts[raw]++;
                        }
                    }

                    // ID uniqueness check
                    if (idIndex >= 0 && idValues != null)
                    {
                        var idVal = reader.GetValue(idIndex)?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(idVal))
                        {
                            if (!idValues.Add(idVal))
                                idDuplicates++;
                        }
                    }
                }

                // Update NullCount
                for (int i = 0; i < fieldCount; i++)
                    fields[i].NullCount = nullCounts[i];

                summary.Fields = fields;

                if (summary.FeatureCount == 0)
                    summary.FeatureCount = recordCount;

                summary.StatusCounts = statusCounts;
                summary.IdDuplicateCount = idDuplicates;

                // Suspected key fields
                if (idIndex >= 0)
                    summary.SuspectedKeyFields.Add(table.Columns[idIndex].ColumnName);

                foreach (var f in fields)
                {
                    if (f.Name.Equals("WORK_ID", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Equals("WORK_NAME", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!summary.SuspectedKeyFields.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                            summary.SuspectedKeyFields.Add(f.Name);
                    }
                }

                if (idIndex >= 0 && idDuplicates > 0)
                    summary.Issues.Add(
                        $"זוהו {idDuplicates} כפילויות בעמודת ID ({table.Columns[idIndex].ColumnName}).");
            }
            catch (Exception ex)
            {
                summary.Issues.Add("שגיאה בקריאת DBF: " + ex.Message);
            }
        }

        private static int FindStatusFieldIndex(DbfTable table)
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                string name = table.Columns[i].ColumnName;
                string upper = name.ToUpperInvariant();
                if (upper == "STATUS" || upper.Contains("STAT"))
                    return i;
            }
            return -1;
        }

        private static int FindIdFieldIndex(DbfTable table)
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                string name = table.Columns[i].ColumnName;
                string upper = name.ToUpperInvariant();
                if (upper == "WORK_ID" || upper == "ID" || upper.EndsWith("_ID"))
                    return i;
            }
            return -1;
        }

        private static string? DetectEpsgFromPrj(string? wkt)
        {
            if (string.IsNullOrWhiteSpace(wkt))
                return null;

            string t = wkt.ToUpperInvariant();

            if (t.Contains("EPSG:2039") ||
                t.Contains("ISRAEL_TM_GRID") ||
                (t.Contains("ISRAEL") && t.Contains("TM")))
                return "EPSG:2039";

            if (t.Contains("WGS_1984") || t.Contains("WGS 84"))
                return "EPSG:4326";

            return null;
        }

        /// <summary>
        /// Builds HTML report from shapefile summary
        /// </summary>
        public static string BuildHtmlReport(ShapefileSummary s, bool asSectionForMainReport = false)
        {
            if (s == null)
                return "<p>❌ לא התקבל סיכום Shapefile.</p>";

            var sb = new StringBuilder();

            if (asSectionForMainReport)
            {
                sb.Append("<hr/><div dir='rtl' style='text-align:right;'>");
                sb.Append("<h3>📂 תקציר ניתוח Shapefile</h3>");
            }
            else
            {
                sb.Append("<div dir='rtl' style='text-align:right;'>");
                sb.Append("<h3>📂 דו\"ח ניתוח Shapefile</h3>");
            }

            if (!string.IsNullOrEmpty(s.FilePath))
                sb.Append($"<p><strong>קובץ:</strong> {Esc(Path.GetFileName(s.FilePath))}</p>");

            sb.Append("<h4>מבנה כללי</h4><ul>");
            sb.Append($"<li>גודל כולל (SHP+SHX+DBF): {s.TotalSizeBytes / (1024.0 * 1024.0):N2} MB</li>");
            if (!string.IsNullOrEmpty(s.GeometryType))
                sb.Append($"<li>סוג גיאומטריה דומיננטי: {Esc(s.GeometryType)}</li>");
            sb.Append($"<li>מספר ישויות (Features): {s.FeatureCount:N0}</li>");
            sb.Append("</ul>");

            sb.Append("<h4>טבלת מאפיינים (DBF)</h4>");
            if (s.Fields.Count == 0)
            {
                sb.Append("<p>לא נמצאו שדות או שלא ניתן היה לקרוא את קובץ ה-DBF.</p>");
            }
            else
            {
                sb.Append($"<p>מספר שדות: {s.Fields.Count}</p>");
                sb.Append("<table style='border-collapse:collapse;width:100%;font-size:11px;' border='1' cellspacing='0' cellpadding='4'>");
                sb.Append("<tr><th>שם</th><th>סוג</th><th>אורך</th><th>NULL</th></tr>");
                foreach (var f in s.Fields)
                    sb.Append($"<tr><td>{Esc(f.Name)}</td><td>{Esc(f.Type)}</td><td>{f.Length}</td><td>{f.NullCount}</td></tr>");
                sb.Append("</table>");
            }

            if (s.SuspectedKeyFields.Count > 0)
            {
                sb.Append("<h4>🔑 שדות מזהים ייחודיים אפשריים</h4><ul>");
                foreach (var k in s.SuspectedKeyFields)
                    sb.Append($"<li>{Esc(k)}</li>");
                if (s.IdDuplicateCount > 0)
                    sb.Append($"<li style='color:#b91c1c;'>אזהרה: זוהו {s.IdDuplicateCount} כפילויות בעמודת ID.</li>");
                sb.Append("</ul>");
            }

            if (s.StatusCounts.Count > 0)
            {
                sb.Append("<h4>🏷️ סטטוס (STATUS)</h4><ul>");
                foreach (var kv in s.StatusCounts.OrderByDescending(k => k.Value))
                    sb.Append($"<li>{Esc(kv.Key)}: {kv.Value}</li>");
                sb.Append("</ul>");
            }

            sb.Append("<h4>מערכת קואורדינטות (CRS)</h4><ul>");
            if (!string.IsNullOrEmpty(s.CrsEpsg))
                sb.Append($"<li>EPSG מזוהה: <strong>{Esc(s.CrsEpsg)}</strong></li>");
            else if (!string.IsNullOrEmpty(s.CrsWkt))
                sb.Append("<li>קיים PRJ אך ללא זיהוי EPSG חד-משמעי. מומלץ לוודא מול מומחה GIS.</li>");
            else
                sb.Append("<li>לא נמצא PRJ. מומלץ לוודא CRS (למשל EPSG:2039) לפני שימוש.</li>");
            sb.Append("</ul>");

            if (s.TotalAreaSquareMeters > 0)
            {
                sb.Append("<h4>שטחים (בקירוב)</h4><ul>");
                sb.Append($"<li>שטח כולל: {s.TotalAreaSquareMeters:N0} מ\"ר</li>");
                sb.Append($"<li>ישויות &lt; 100 מ\"ר: {s.SmallAreaCountUnder100:N0}</li>");
                sb.Append($"<li>ישויות &gt; 1,000,000 מ\"ר: {s.LargeAreaCountOver1M:N0}</li>");
                sb.Append("</ul>");
            }

            if (s.SelfIntersectingCount > 0 ||
                s.DuplicateGeometryCount > 0 ||
                s.OpenRingCount > 0 ||
                s.Issues.Count > 0)
            {
                sb.Append("<h4>🧩 טופולוגיה וממצאים</h4><ul>");
                if (s.SelfIntersectingCount > 0)
                    sb.Append($"<li>{s.SelfIntersectingCount} ישויות עם גיאומטריה לא תקינה (Self-Intersecting/Invalid).</li>");
                if (s.DuplicateGeometryCount > 0)
                    sb.Append($"<li>{s.DuplicateGeometryCount} גיאומטריות כפולות (Duplicate).</li>");
                if (s.OpenRingCount > 0)
                    sb.Append($"<li>{s.OpenRingCount} טבעות שאינן סגורות (Open Rings).</li>");
                foreach (var issue in s.Issues)
                    sb.Append($"<li>{Esc(issue)}</li>");
                sb.Append("</ul>");
            }

            if (s.HasAdditionalShapefilesInFolder)
                sb.Append("<p>ℹ️ קיימים קבצי SHP נוספים באותה תיקייה – ייתכן סט שכבות קשורות.</p>");

            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
