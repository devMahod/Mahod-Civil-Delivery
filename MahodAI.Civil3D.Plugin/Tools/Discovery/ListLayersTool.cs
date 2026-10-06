using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.Discovery
{
    /// <summary>
    /// Lists layer information with entity counts.
    /// </summary>
    public class ListLayersTool : DrawingToolBase
    {
        public override string Name => "list_layers";
        public override string Description => "Lists all layers in the drawing with their properties (color, linetype, visibility) and entity counts.";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var filter = GetStringParam(parameters, "filter");
            var limit = GetIntParam(parameters, "limit") ?? 500;
            var includeEntityCounts = GetBoolParam(parameters, "include_entity_counts", true);
            var onlyUsed = GetBoolParam(parameters, "only_used", false);

            var db = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Database
                ?? throw new InvalidOperationException("No active document");
            var layerTable = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;

            if (layerTable == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Could not access layer table");
            }

            // Count entities per layer if requested
            var entityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? entityCountError = null;
            if (includeEntityCounts)
            {
                try { entityCounts = CountEntitiesPerLayer(tr, db, ct); }
                catch (Exception ex) { entityCountError = ex.Message; }
            }

            var layers = new List<LayerInfo>();
            foreach (ObjectId layerId in layerTable)
            {
                ct.ThrowIfCancellationRequested();

                if (layers.Count >= limit) break;

                var layer = tr.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
                if (layer == null) continue;

                // Apply filter if specified
                if (!string.IsNullOrEmpty(filter) &&
                    !layer.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entityCount = entityCounts.TryGetValue(layer.Name, out var count) ? count : 0;

                // Skip unused layers if onlyUsed is true
                if (onlyUsed && entityCount == 0) continue;

                var linetypeName = GetLinetypeName(tr, layer.LinetypeObjectId);

                layers.Add(new LayerInfo
                {
                    Name = layer.Name,
                    Description = layer.Description,
                    IsOn = !layer.IsOff,
                    IsFrozen = layer.IsFrozen,
                    IsLocked = layer.IsLocked,
                    Color = new ColorInfo
                    {
                        ColorIndex = layer.Color.ColorIndex,
                        ColorName = layer.Color.ColorName ?? layer.Color.ToString(),
                        Red = layer.Color.ColorValue.R,
                        Green = layer.Color.ColorValue.G,
                        Blue = layer.Color.ColorValue.B
                    },
                    Linetype = linetypeName,
                    LineWeight = layer.LineWeight.ToString(),
                    PlotStyleName = layer.PlotStyleName,
                    IsPlottable = layer.IsPlottable,
                    EntityCount = entityCount
                });
            }

            var result = new ListLayersResult
            {
                Layers = layers,
                TotalCount = layers.Count,
                FilterApplied = filter,
                EntityCountNote = entityCountError != null ? $"Entity counts unavailable: {entityCountError}" : null
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private Dictionary<string, int> CountEntitiesPerLayer(Transaction tr, Database db, CancellationToken ct)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null) return counts;

                var modelSpace = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (modelSpace == null) return counts;

                foreach (ObjectId entityId in modelSpace)
                {
                    ct.ThrowIfCancellationRequested();

                    var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                    if (entity != null)
                    {
                        var layerName = entity.Layer;
                        if (!counts.ContainsKey(layerName))
                            counts[layerName] = 0;
                        counts[layerName]++;
                    }
                }
            }
            catch
            {
                // Ignore errors in counting
            }

            return counts;
        }

        private string GetLinetypeName(Transaction tr, ObjectId linetypeId)
        {
            try
            {
                if (linetypeId.IsNull) return "ByLayer";
                var linetype = tr.GetObject(linetypeId, OpenMode.ForRead) as LinetypeTableRecord;
                return linetype?.Name ?? "ByLayer";
            }
            catch
            {
                return "ByLayer";
            }
        }
    }

    #region Result Models

    public class ListLayersResult
    {
        public List<LayerInfo> Layers { get; set; } = new();
        public int TotalCount { get; set; }
        public string? FilterApplied { get; set; }
        public string? EntityCountNote { get; set; }
    }

    public class LayerInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsOn { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsLocked { get; set; }
        public ColorInfo? Color { get; set; }
        public string Linetype { get; set; } = string.Empty;
        public string LineWeight { get; set; } = string.Empty;
        public string? PlotStyleName { get; set; }
        public bool IsPlottable { get; set; }
        public int EntityCount { get; set; }
    }

    public class ColorInfo
    {
        public short ColorIndex { get; set; }
        public string ColorName { get; set; } = string.Empty;
        public byte Red { get; set; }
        public byte Green { get; set; }
        public byte Blue { get; set; }
    }

    #endregion
}
