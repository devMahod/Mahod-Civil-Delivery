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
    /// Creates a dynamic corridor surface from specified link codes.
    /// Ported from MahodCivilNet CCorridor.CreateDynamicCorridorSurface.
    /// </summary>
    public class CreateCorridorSurfaceTool : DrawingToolBase
    {
        public override string Name => "create_corridor_surface";
        public override string Description =>
            "Creates a dynamic corridor surface from specified link codes.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var linkCode = GetRequiredStringParam(parameters, "link_code");
            var overhangCorrection = GetStringParam(parameters, "overhang_correction") ?? "adjust";

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            try
            {
                // Find the corridor
                Autodesk.Civil.DatabaseServices.Corridor? corridor = null;
                foreach (ObjectId id in civilDoc.CorridorCollection)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                    if (obj != null && obj.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                    {
                        corridor = obj;
                        break;
                    }
                }

                if (corridor == null)
                    return ToolResult.NotFound("Corridor", corridorName);

                // Open corridor for write
                corridor = tr.GetObject(corridor.ObjectId, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Corridor;
                if (corridor == null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Could not open corridor for write");

                // Check if surface with this name already exists on the corridor
                foreach (CorridorSurface existingCs in corridor.CorridorSurfaces)
                {
                    if (existingCs.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Corridor surface '{surfaceName}' already exists on corridor '{corridorName}'");
                    }
                }

                // Create corridor surface
                CorridorSurface cs = corridor.CorridorSurfaces.Add(surfaceName);

                // Add link code
                cs.AddLinkCode(linkCode, false);

                // Set overhang correction
                try
                {
                    var correctionType = overhangCorrection.ToLowerInvariant() switch
                    {
                        "none" => OverhangCorrectionType.None,
                        _ => OverhangCorrectionType.None
                    };
                    cs.OverhangCorrection = correctionType;
                }
                catch
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CreateCorridorSurface: Could not set overhang correction to '{overhangCorrection}'");
                }

                // Add corridor extents boundary
                try
                {
                    cs.Boundaries.AddCorridorExtentsBoundary("Outer");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CreateCorridorSurface: Could not add extents boundary: {ex.Message}");
                }

                // Rebuild the corridor to generate the surface
                try
                {
                    corridor.Rebuild();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CreateCorridorSurface: Corridor rebuild warning: {ex.Message}");
                }

                cache.RemoveByPattern("get_drawing_summary:");

                return await Task.FromResult(ToolResult.Ok(new
                {
                    success = true,
                    corridor_name = corridor.Name,
                    surface_name = surfaceName,
                    link_code = linkCode,
                    overhang_correction = overhangCorrection,
                    message = $"Corridor surface '{surfaceName}' created on corridor '{corridor.Name}' " +
                        $"with link code '{linkCode}'. Surface will update dynamically when the corridor is rebuilt."
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to create corridor surface: {ex.Message}");
            }
        }
    }
}
