using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
#if MAHOD_MAP3D
using Autodesk.Gis.Map.Platform;
using OSGeo.MapGuide;
#endif

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers
{
    /// <summary>
    /// Analyzes DWG layers and connected FDO data layers
    /// </summary>
    public static class LayersAnalyzer
    {
        /// <summary>
        /// Analyzes layers and entities in the active drawing
        /// </summary>
        public static LayersSummary Analyze()
        {
            var summary = new LayersSummary();

            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    summary.Error = "אין שרטוט פעיל פתוח.";
                    return summary;
                }

                var db = doc.Database;
                summary.FileName = Path.GetFileName(doc.Name);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    AnalyzeDwgLayers(db, tr, summary);
                    tr.Commit();
                }

                // Analyze FDO layers
                AnalyzeFdoLayers(summary);

                return summary;
            }
            catch (Exception ex)
            {
                summary.Error = ex.Message;
                return summary;
            }
        }

        private static void AnalyzeDwgLayers(Database db, Transaction tr, LayersSummary summary)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var layerEntityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entityTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long totalEntities = 0;

            foreach (ObjectId entId in ms)
            {
                if (!entId.IsValid) continue;
                if (tr.GetObject(entId, OpenMode.ForRead, false) is not Entity ent) continue;

                totalEntities++;

                var ln = ent.Layer ?? string.Empty;
                if (!layerEntityCounts.TryAdd(ln, 1))
                    layerEntityCounts[ln]++;

                var type = ent.GetType().Name;
                if (!entityTypeCounts.TryAdd(type, 1))
                    entityTypeCounts[type]++;
            }

            summary.TotalEntityCount = totalEntities;
            summary.EntityTypeCounts = entityTypeCounts;

            foreach (ObjectId lid in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                var name = ltr.Name;

                layerEntityCounts.TryGetValue(name, out var entityCount);

                summary.Layers.Add(new DwgLayerInfo
                {
                    Name = name,
                    EntityCount = entityCount,
                    IsOff = ltr.IsOff,
                    IsFrozen = ltr.IsFrozen,
                    IsLocked = ltr.IsLocked
                });

                if (entityCount == 0)
                    summary.EmptyLayerCount++;
            }

            summary.TotalLayerCount = summary.Layers.Count;

            summary.TopLayers = layerEntityCounts
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new LayerEntityCount { LayerName = kv.Key, Count = kv.Value })
                .ToList();

            // Get extents
            try
            {
                summary.ExtentsMin = db.Extmin.ToString();
                summary.ExtentsMax = db.Extmax.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] LayersAnalyzer get extents: {ex.Message}");
            }
        }

        private static void AnalyzeFdoLayers(LayersSummary summary)
        {
#if !MAHOD_MAP3D
            // Civil 3D 2026 component: built against the Civil3D.NET reference package,
            // which carries no Map 3D / FDO assemblies. Everything else is identical;
            // this optional analysis simply reports nothing rather than blocking the build.
            _ = summary;
#else
            try
            {
                var map = AcMapMap.GetCurrentMap();
                if (map == null)
                    return;

                var layers = map.GetLayers();
                if (layers == null || layers.Count == 0)
                    return;

                MgFeatureService? featureService = null;
                try
                {
                    var serviceObj = AcMapServiceFactory.GetService(MgServiceType.FeatureService);
                    featureService = serviceObj as MgFeatureService;
                }
                catch
                {
                    featureService = null;
                }

                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i] is not AcMapLayer layer)
                        continue;

                    var resId = layer.FeatureSourceId;
                    var clsName = layer.FeatureClassName;

                    if (string.IsNullOrWhiteSpace(resId) || string.IsNullOrWhiteSpace(clsName))
                        continue;

                    var info = new FdoLayerInfo
                    {
                        LayerName = layer.Name,
                        ResourceId = resId,
                        ClassName = clsName
                    };

                    if (featureService != null)
                    {
                        try
                        {
                            var rid = new MgResourceIdentifier(resId);
                            var opts = new MgFeatureQueryOptions();

                            MgFeatureReader? reader = null;
                            int count = 0;
                            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            try
                            {
                                reader = featureService.SelectFeatures(rid, clsName, opts);

                                bool first = true;
                                while (reader != null && reader.ReadNext())
                                {
                                    count++;

                                    if (first)
                                    {
                                        first = false;
                                        int pc = reader.GetPropertyCount();
                                        for (int pi = 0; pi < pc; pi++)
                                        {
                                            var pname = reader.GetPropertyName(pi);
                                            if (IsUserField(pname))
                                                fields.Add(pname);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                try { reader?.Close(); } catch { /* Ignore cleanup */ }
                            }

                            info.FeatureCount = count;
                            if (fields.Count > 0)
                                info.FieldNames = fields.OrderBy(n => n).Take(20).ToList();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MahodAI] LayersAnalyzer query FDO layer '{info.LayerName}': {ex.Message}");
                        }
                    }

                    summary.FdoLayers.Add(info);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] AnalyzeFdoLayers: {ex.Message}");
            }
#endif
        }

        private static bool IsUserField(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var n = name;
            if (n.Equals("FeatId", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("Geometry", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("Layer", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("Linetype", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("Text", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("EntityHand", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("SubClasses", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("PaperSpace", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }

        /// <summary>
        /// Builds HTML report from layers summary
        /// </summary>
        public static string BuildHtmlReport(LayersSummary summary)
        {
            if (summary == null)
                return "<h3>❌ שגיאה</h3><p>לא התקבל סיכום שכבות.</p>";

            if (!string.IsNullOrEmpty(summary.Error))
                return $"<h3>❌ שגיאה</h3><p>{Esc(summary.Error)}</p>";

            var sb = new StringBuilder();
            sb.Append("<div dir='rtl' style='text-align:right;'>");
            sb.Append("<h3>🧩 דו\"ח שכבות וישויות (DWG + שכבות נתונים מחוברות)</h3>");
            sb.Append($"<p><strong>קובץ:</strong> {Esc(summary.FileName ?? "")}</p>");

            // DWG Layers section
            sb.Append("<h4>🗂️ שכבות DWG (Model Space)</h4><ul>");
            sb.Append($"<li>סה\"כ שכבות DWG: {summary.TotalLayerCount}</li>");
            sb.Append($"<li>שכבות DWG ללא ישויות: {summary.EmptyLayerCount}</li>");

            var emptyLayers = summary.Layers.Where(l => l.EntityCount == 0).Take(10).ToList();
            if (emptyLayers.Count > 0)
            {
                sb.Append("<li>דוגמאות שכבות ריקות: ");
                sb.Append(Esc(string.Join(", ", emptyLayers.Select(l => l.Name))));
                sb.Append("</li>");
            }

            if (summary.TopLayers.Count > 0)
            {
                sb.Append("<li>שכבות פעילות (Top 10 לפי כמות ישויות): ");
                sb.Append(string.Join(", ", summary.TopLayers.Select(l => $"{Esc(l.LayerName)} ({l.Count})")));
                sb.Append("</li>");
            }

            sb.Append("</ul>");

            // Entity types section
            sb.Append("<h4>📐 ישויות DWG</h4><ul>");
            sb.Append($"<li>סה\"כ ישויות DWG במודל: {summary.TotalEntityCount}</li>");

            void AddType(string typeName, string label)
            {
                if (summary.EntityTypeCounts.TryGetValue(typeName, out var c) && c > 0)
                    sb.Append($"<li>{label}: {c}</li>");
            }

            AddType("Line", "Line");
            AddType("Polyline", "Polyline");
            AddType("Polyline2d", "Polyline 2D");
            AddType("Polyline3d", "Polyline 3D");
            AddType("Circle", "Circle");
            AddType("Arc", "Arc");
            AddType("Spline", "Spline");
            AddType("Hatch", "Hatch");
            AddType("MText", "MText");
            AddType("DBText", "Text");
            AddType("BlockReference", "BlockReference");

            sb.Append("</ul>");

            // Extents section
            if (!string.IsNullOrEmpty(summary.ExtentsMin) && !string.IsNullOrEmpty(summary.ExtentsMax))
            {
                sb.Append("<h4>🗺️ Extents DWG (משוער)</h4>");
                sb.Append($"<p>נקודת מינימום: {Esc(summary.ExtentsMin)}<br/>");
                sb.Append($"נקודת מקסימום: {Esc(summary.ExtentsMax)}</p>");
            }

            // FDO Layers section
            if (summary.FdoLayers.Count > 0)
            {
                sb.Append("<h4>🗺️ שכבות נתונים מחוברות (FDO / Data Connect)</h4>");
                sb.Append("<p>שכבות מחוברות (למשל WORK_ID, SHP_1, SHP_2) עם מידע מרכזי מתוך המקור.</p>");
                sb.Append("<table style='border-collapse:collapse;width:100%;font-size:11px;' border='1' cellspacing='0' cellpadding='4'>");
                sb.Append("<tr>");
                sb.Append("<th>שם שכבה</th>");
                sb.Append("<th>Feature Class</th>");
                sb.Append("<th>Resource Id</th>");
                sb.Append("<th>מס׳ Features</th>");
                sb.Append("<th>שדות עיקריים</th>");
                sb.Append("</tr>");

                foreach (var info in summary.FdoLayers.OrderByDescending(f => f.FeatureCount))
                {
                    sb.Append("<tr>");
                    sb.Append($"<td>{Esc(info.LayerName)}</td>");
                    sb.Append($"<td>{Esc(info.ClassName)}</td>");
                    sb.Append($"<td>{Esc(info.ResourceId)}</td>");
                    sb.Append($"<td>{(info.FeatureCount > 0 ? info.FeatureCount.ToString() : "-")}</td>");

                    if (info.FieldNames != null && info.FieldNames.Count > 0)
                        sb.Append($"<td>{Esc(string.Join(", ", info.FieldNames))}</td>");
                    else
                        sb.Append("<td>-</td>");

                    sb.Append("</tr>");
                }

                sb.Append("</table>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }

    /// <summary>
    /// Summary of layer analysis results
    /// </summary>
    public class LayersSummary
    {
        public string? FileName { get; set; }
        public string? Error { get; set; }
        public int TotalLayerCount { get; set; }
        public int EmptyLayerCount { get; set; }
        public long TotalEntityCount { get; set; }
        public string? ExtentsMin { get; set; }
        public string? ExtentsMax { get; set; }
        public List<DwgLayerInfo> Layers { get; set; } = new();
        public List<LayerEntityCount> TopLayers { get; set; } = new();
        public Dictionary<string, int> EntityTypeCounts { get; set; } = new();
        public List<FdoLayerInfo> FdoLayers { get; set; } = new();
    }

    /// <summary>
    /// DWG layer information
    /// </summary>
    public class DwgLayerInfo
    {
        public string Name { get; set; } = string.Empty;
        public int EntityCount { get; set; }
        public bool IsOff { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsLocked { get; set; }
    }

    /// <summary>
    /// Layer entity count for top layers list
    /// </summary>
    public class LayerEntityCount
    {
        public string LayerName { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>
    /// FDO/Map 3D connected layer information
    /// </summary>
    public class FdoLayerInfo
    {
        public string LayerName { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public int FeatureCount { get; set; }
        public List<string>? FieldNames { get; set; }
    }
}
