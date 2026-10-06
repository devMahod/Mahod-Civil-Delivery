using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates a new empty TIN surface in the drawing.
    /// </summary>
    public class CreateSurfaceTool : DrawingToolBase
    {
        public override string Name => "create_surface";
        public override string Description =>
            "Creates a new empty TIN surface in the drawing.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var styleName = GetStringParam(parameters, "style_name");
            var layerName = GetStringParam(parameters, "layer_name") ?? "0";

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            try
            {
                // Check if surface with this name already exists
                foreach (ObjectId existingId in civilDoc.GetSurfaceIds())
                {
                    var existing = tr.GetObject(existingId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (existing != null && existing.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Surface '{surfaceName}' already exists");
                    }
                }

                // Resolve surface style
                ObjectId styleId = ObjectId.Null;
                try
                {
                    if (!string.IsNullOrEmpty(styleName))
                    {
                        for (int i = 0; i < civilDoc.Styles.SurfaceStyles.Count; i++)
                        {
                            var sid = civilDoc.Styles.SurfaceStyles[i];
                            var s = tr.GetObject(sid, OpenMode.ForRead);
                            var sName = s?.GetType().GetProperty("Name")?.GetValue(s)?.ToString();
                            if (sName != null && sName.Equals(styleName, StringComparison.OrdinalIgnoreCase))
                            {
                                styleId = sid;
                                break;
                            }
                        }
                    }

                    // Fall back to first available style
                    if (styleId.IsNull && civilDoc.Styles.SurfaceStyles.Count > 0)
                    {
                        styleId = civilDoc.Styles.SurfaceStyles[0];
                    }
                }
                catch { }

                if (styleId.IsNull)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "No surface styles available in the drawing");

                // Create the TIN surface
                ObjectId surfaceId = TinSurface.Create(surfaceName, styleId);

                if (surfaceId.IsNull)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Surface creation returned null");

                // Set layer if specified
                var surface = tr.GetObject(surfaceId, OpenMode.ForWrite) as TinSurface;
                if (surface == null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Could not read created surface");

                if (!string.Equals(layerName, "0", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Ensure layer exists
                        var db = HostApplicationServices.WorkingDatabase;
                        var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                        if (lt != null && !lt.Has(layerName))
                        {
                            lt.UpgradeOpen();
                            var newLayer = new LayerTableRecord { Name = layerName };
                            lt.Add(newLayer);
                            tr.AddNewlyCreatedDBObject(newLayer, true);
                        }
                        surface.Layer = layerName;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI] CreateSurface: Could not set layer to '{layerName}': {ex.Message}");
                    }
                }

                // Get the applied style name for confirmation
                string appliedStyleName = "";
                try
                {
                    var styleObj = tr.GetObject(surface.StyleId, OpenMode.ForRead);
                    appliedStyleName = styleObj?.GetType().GetProperty("Name")?.GetValue(styleObj)?.ToString() ?? "";
                }
                catch { }

                cache.RemoveByPattern("get_drawing_summary:");

                return await Task.FromResult(ToolResult.Ok(new
                {
                    success = true,
                    surface_name = surface.Name,
                    style = appliedStyleName,
                    layer = surface.Layer,
                    message = $"TIN surface '{surface.Name}' created on layer '{surface.Layer}'" +
                        (!string.IsNullOrEmpty(appliedStyleName) ? $" with style '{appliedStyleName}'" : "") +
                        ". Surface is empty — add data via breaklines, point groups, or DEM import."
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to create surface: {ex.Message}");
            }
        }
    }
}
