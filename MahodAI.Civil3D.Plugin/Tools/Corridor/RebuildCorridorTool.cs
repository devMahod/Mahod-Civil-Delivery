using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Rebuilds a corridor to apply pending parameter changes.
    /// </summary>
    public class RebuildCorridorTool : DrawingToolBase
    {
        public override string Name => "rebuild_corridor";
        public override string Description => "Rebuilds a corridor to apply pending changes (width, slope, assembly modifications). This regenerates all cross-sections and surfaces.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the corridor
            Autodesk.Civil.DatabaseServices.Corridor? corridor = null;
            ObjectId corridorId = ObjectId.Null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                if (obj != null && obj.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridor = obj;
                    corridorId = id;
                    break;
                }
            }

            if (corridor == null)
                return ToolResult.NotFound("Corridor", corridorName);

            // Open for write and rebuild
            corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Corridor;
            if (corridor == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open corridor for write");

            try
            {
                corridor.Rebuild();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to rebuild corridor: {ex.Message}");
            }

            cache.RemoveByPattern("get_corridor_info:");
            cache.RemoveByPattern("get_corridor_cross_section:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                corridor_name = corridorName,
                baselines_count = corridor.Baselines.Count,
                status = "rebuilt"
            }));
        }
    }
}
