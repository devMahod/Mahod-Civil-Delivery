using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;
using LayerInfo = MahodAI.Civil3D.Plugin.Services.Extraction.Models.LayerInfo;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Extractors
{
    /// <summary>
    /// Extracts general geometry data: layers, blocks, xrefs, extents, entity statistics.
    /// Also counts Civil 3D objects (surfaces, corridors, pipe networks, parcels).
    /// </summary>
    public class GeometryExtractor : IDataExtractor
    {
        public string ObjectType => "Geometry";
        public int Priority => 100;

        public bool IsAvailable(CivilDocument? civilDoc) => true;

        public object? ExtractAll(Transaction tr, CivilDocument? civilDoc, Database db)
        {
            var result = new GeometryExtractorResult();

            try
            {
                // Extract layers
                result.Layers = ExtractLayers(tr, db);
                result.LayerCount = result.Layers.Count;

                // Extract blocks
                result.Blocks = ExtractBlocks(tr, db);
                result.BlockCount = result.Blocks.Count;

                // Extract XRefs
                result.XRefs = ExtractXRefs(tr, db);

                // Get drawing extents
                result.Extents = ExtractExtents(db);

                // Get entity statistics
                ExtractEntityStats(tr, db, result);

                // Get drawing info
                result.Units = GetDrawingUnits(db);
                result.CoordinateSystem = GetCoordinateSystem(db);
                result.DwgVersion = db.OriginalFileVersion.ToString();

                // Count Civil 3D objects
                if (civilDoc != null)
                    CountCivil3DObjects(civilDoc, result);

                // Calculate geometry summary
                result.Geometry = CalculateGeometrySummary(tr, db);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GeometryExtractor error: {ex.Message}");
            }

            return result;
        }

        public object? ExtractByIds(Transaction tr, IEnumerable<ObjectId> ids) => null;

        #region Layer Extraction

        private List<Models.LayerInfo> ExtractLayers(Transaction tr, Database db)
        {
            var list = new List<Models.LayerInfo>();

            try
            {
                var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var entityCounts = CountEntitiesByLayer(tr, db);

                foreach (ObjectId layerId in layerTable)
                {
                    if (tr.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord layer)
                    {
                        entityCounts.TryGetValue(layer.Name, out int count);

                        list.Add(new Models.LayerInfo
                        {
                            Name = layer.Name,
                            ColorAci = layer.Color.ColorIndex,
                            IsOff = layer.IsOff,
                            IsFrozen = layer.IsFrozen,
                            IsLocked = layer.IsLocked,
                            IsPlottable = layer.IsPlottable,
                            EntityCount = count,
                            InfrastructureType = ClassifyInfrastructureType(layer.Name)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractLayers error: {ex.Message}");
            }

            return list.OrderByDescending(l => l.EntityCount).ToList();
        }

        private Dictionary<string, int> CountEntitiesByLayer(Transaction tr, Database db)
        {
            var counts = new Dictionary<string, int>();

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                    {
                        string layer = ent.Layer ?? "0";
                        counts[layer] = counts.TryGetValue(layer, out int c) ? c + 1 : 1;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GeometryExtractor count entities per layer: {ex.Message}");
            }

            return counts;
        }

        private string ClassifyInfrastructureType(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return "Other";

            var upper = layerName.ToUpperInvariant();

            if (upper.Contains("ROAD") || upper.Contains("RD-") || upper.Contains("HWY") ||
                upper.Contains("PVMT") || upper.Contains("PAVE") || upper.Contains("CURB") ||
                upper.Contains("ALIGN") || upper.Contains("CORRIDOR") || upper.Contains("כביש"))
                return "Road";

            if (upper.Contains("DRAIN") || upper.Contains("STORM") || upper.Contains("CULVERT") ||
                upper.Contains("ניקוז") || upper.Contains("תעלה"))
                return "Drainage";

            if (upper.Contains("WATER") || upper.Contains("WTR") || upper.Contains("מים"))
                return "Water";

            if (upper.Contains("SEWER") || upper.Contains("SAN") || upper.Contains("ביוב"))
                return "Sewer";

            if (upper.Contains("ELEC") || upper.Contains("POWER") || upper.Contains("חשמל"))
                return "Electric";

            if (upper.Contains("TELE") || upper.Contains("COMM") || upper.Contains("תקשורת"))
                return "Telecom";

            if (upper.Contains("GAS") || upper.Contains("גז"))
                return "Gas";

            if (upper.Contains("SURV") || upper.Contains("TOPO") || upper.Contains("מדידה"))
                return "Survey";

            if (upper.Contains("CONT") || upper.Contains("ELEV") || upper.Contains("קוי"))
                return "Contour";

            if (upper.Contains("BLDG") || upper.Contains("BUILD") || upper.Contains("בנין"))
                return "Building";

            if (upper.Contains("VEG") || upper.Contains("TREE") || upper.Contains("צמחיה"))
                return "Vegetation";

            if (upper.Contains("TEXT") || upper.Contains("ANNO") || upper.Contains("DIM"))
                return "Annotation";

            if (upper.Contains("C-") || upper.Contains("CIVIL") || upper.Contains("SURFACE") ||
                upper.Contains("PROFILE") || upper.Contains("PARCEL"))
                return "Civil3D";

            return "Other";
        }

        #endregion

        #region Block Extraction

        private List<BlockInfo> ExtractBlocks(Transaction tr, Database db)
        {
            var list = new List<BlockInfo>();

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var blockCounts = new Dictionary<string, (int count, HashSet<string> layers)>();

                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is BlockReference blkRef)
                    {
                        string name = blkRef.Name ?? "";
                        if (!blockCounts.ContainsKey(name))
                            blockCounts[name] = (0, new HashSet<string>());

                        var entry = blockCounts[name];
                        entry.count++;
                        entry.layers.Add(blkRef.Layer ?? "0");
                        blockCounts[name] = entry;
                    }
                }

                foreach (var kvp in blockCounts.OrderByDescending(x => x.Value.count))
                {
                    list.Add(new BlockInfo
                    {
                        Name = kvp.Key,
                        Count = kvp.Value.count,
                        Layers = kvp.Value.layers.ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractBlocks error: {ex.Message}");
            }

            return list;
        }

        #endregion

        #region XRef Extraction

        private List<XRefInfo> ExtractXRefs(Transaction tr, Database db)
        {
            var list = new List<XRefInfo>();

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                foreach (ObjectId id in bt)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is BlockTableRecord btr && btr.IsFromExternalReference)
                    {
                        var info = new XRefInfo
                        {
                            Name = btr.Name,
                            Path = btr.PathName ?? "",
                            IsResolved = !btr.IsUnloaded,
                            Status = btr.IsUnloaded ? "Unloaded" : "Loaded"
                        };

                        // Read content from resolved (loaded) XRefs
                        if (!btr.IsUnloaded)
                        {
                            try
                            {
                                ExtractXRefContent(tr, btr, info);
                            }
                            catch { }
                        }

                        list.Add(info);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractXRefs error: {ex.Message}");
            }

            return list;
        }

        private static void ExtractXRefContent(Transaction tr, BlockTableRecord btr, XRefInfo info)
        {
            const int MaxEntities = 10000;
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            foreach (ObjectId entId in btr)
            {
                if (total >= MaxEntities) break;

                try
                {
                    var ent = tr.GetObject(entId, OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (ent == null) continue;

                    total++;
                    layers.Add(ent.Layer ?? "0");

                    string typeName = ent.GetRXClass().DxfName ?? ent.GetType().Name;
                    if (!typeCounts.TryAdd(typeName, 1))
                        typeCounts[typeName]++;

                    if (ent is BlockReference blkRef)
                    {
                        try
                        {
                            var innerBtr = tr.GetObject(blkRef.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                            if (innerBtr != null && !innerBtr.IsFromExternalReference)
                                blockNames.Add(innerBtr.Name);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            info.Layers = new List<string>(layers);
            info.BlockNames = new List<string>(blockNames);
            info.EntityTypeCounts = typeCounts;
            info.TotalEntityCount = total;
        }

        #endregion

        #region Extents & Stats

        private ExtentsInfo ExtractExtents(Database db)
        {
            try
            {
                var ext = db.Extmax - db.Extmin;
                return new ExtentsInfo
                {
                    MinX = db.Extmin.X,
                    MinY = db.Extmin.Y,
                    MinZ = db.Extmin.Z,
                    MaxX = db.Extmax.X,
                    MaxY = db.Extmax.Y,
                    MaxZ = db.Extmax.Z,
                    Width = ext.X,
                    Height = ext.Y
                };
            }
            catch
            {
                return new ExtentsInfo();
            }
        }

        private void ExtractEntityStats(Transaction tr, Database db, GeometryExtractorResult result)
        {
            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                int total = 0;
                var typeCounts = new Dictionary<string, int>();

                foreach (ObjectId id in ms)
                {
                    total++;
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                    {
                        string typeName = ent.GetType().Name;
                        typeCounts[typeName] = typeCounts.TryGetValue(typeName, out int c) ? c + 1 : 1;
                    }
                }

                result.TotalEntityCount = total;
                result.EntityTypeCounts = typeCounts;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractEntityStats error: {ex.Message}");
            }
        }

        private string GetDrawingUnits(Database db)
        {
            try
            {
                return db.Insunits switch
                {
                    UnitsValue.Meters => "Meters",
                    UnitsValue.Centimeters => "Centimeters",
                    UnitsValue.Millimeters => "Millimeters",
                    UnitsValue.Feet => "Feet",
                    UnitsValue.Inches => "Inches",
                    _ => db.Insunits.ToString()
                };
            }
            catch
            {
                return "Unknown";
            }
        }

        private string GetCoordinateSystem(Database db)
        {
            try
            {
                var dbExt = db.GetType().GetProperty("CoordinateSystemCode");
                if (dbExt != null)
                {
                    var val = dbExt.GetValue(db);
                    if (val != null) return val.ToString() ?? "Unknown";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GeometryExtractor get coordinate system: {ex.Message}");
            }
            return "Unknown";
        }

        private void CountCivil3DObjects(CivilDocument civilDoc, GeometryExtractorResult result)
        {
            try { result.SurfaceCount = civilDoc.GetSurfaceIds()?.Count ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] GeometryExtractor count surfaces: {ex.Message}"); }
            try { result.CorridorCount = civilDoc.CorridorCollection?.Count ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] GeometryExtractor count corridors: {ex.Message}"); }
            try { result.PipeNetworkCount = civilDoc.GetPipeNetworkIds()?.Count ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] GeometryExtractor count pipe networks: {ex.Message}"); }
        }

        private GeometrySummary CalculateGeometrySummary(Transaction tr, Database db)
        {
            var summary = new GeometrySummary();

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                double totalLength = 0;
                double totalArea = 0;
                double maxZ = 0;
                int zAnomalyCount = 0;

                foreach (ObjectId id in ms)
                {
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                        {
                            var bounds = ent.GeometricExtents;
                            if (Math.Abs(bounds.MaxPoint.Z) > 0.001)
                            {
                                zAnomalyCount++;
                                if (bounds.MaxPoint.Z > maxZ) maxZ = bounds.MaxPoint.Z;
                            }

                            if (ent is Curve curve)
                            {
                                try { totalLength += curve.GetDistanceAtParameter(curve.EndParam); }
                                catch { /* Curve length may not be computable */ }
                            }

                            if (ent is Hatch hatch)
                            {
                                try { totalArea += hatch.Area; }
                                catch { /* Hatch area may not be computable */ }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] GeometryExtractor process entity: {ex.Message}");
                    }
                }

                summary.TotalLengthDrawingUnits = totalLength;
                summary.TotalAreaDrawingUnits = totalArea;
                summary.MaxZ = maxZ;
                summary.ZAnomalyCount = zAnomalyCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CalculateGeometrySummary error: {ex.Message}");
            }

            return summary;
        }

        #endregion
    }

    /// <summary>
    /// Result container for GeometryExtractor
    /// </summary>
    public class GeometryExtractorResult
    {
        public string Units { get; set; } = string.Empty;
        public string CoordinateSystem { get; set; } = string.Empty;
        public string DwgVersion { get; set; } = string.Empty;
        public List<Models.LayerInfo> Layers { get; set; } = new();
        public List<BlockInfo> Blocks { get; set; } = new();
        public List<XRefInfo> XRefs { get; set; } = new();
        public ExtentsInfo Extents { get; set; } = new();
        public GeometrySummary Geometry { get; set; } = new();
        public int TotalEntityCount { get; set; }
        public Dictionary<string, int> EntityTypeCounts { get; set; } = new();
        public int LayerCount { get; set; }
        public int BlockCount { get; set; }
        public int SurfaceCount { get; set; }
        public int CorridorCount { get; set; }
        public int PipeNetworkCount { get; set; }
        public int ParcelCount { get; set; }
    }
}
