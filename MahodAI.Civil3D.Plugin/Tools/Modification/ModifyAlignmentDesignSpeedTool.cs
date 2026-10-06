using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Modifies the design speed for an alignment.
    /// </summary>
    public class ModifyAlignmentDesignSpeedTool : DrawingToolBase
    {
        public override string Name => "modify_alignment_design_speed";
        public override string Description => "Modifies the design speed for an alignment. Sets the design speed value used for standards checking and criteria validation.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var designSpeed = GetDoubleParam(parameters, "design_speed");

            if (designSpeed == null || designSpeed.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'design_speed' must be a positive number (kph)");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            // Try to read current design speed
            double? oldSpeed = null;
            try
            {
                var designSpeeds = alignment.DesignSpeeds;
                if (designSpeeds != null && designSpeeds.Count > 0)
                {
                    foreach (CivilDb.DesignSpeed ds in designSpeeds)
                    {
                        oldSpeed = ds.Value;
                        break;
                    }
                }
            }
            catch
            {
                // Design speed collection may not be available
            }

            // Set new design speed
            try
            {
                var designSpeeds = alignment.DesignSpeeds;
                if (designSpeeds != null)
                {
                    while (designSpeeds.Count > 0)
                    {
                        designSpeeds.Remove(alignment.StartingStation);
                    }
                    designSpeeds.Add(alignment.StartingStation, designSpeed.Value);
                }
                else
                {
                    return ToolResult.Fail(ToolErrorCodes.NotSupported,
                        "Design speed collection not available on this alignment");
                }
            }
            catch (Exception ex)
            {
                // Fallback: try via reflection for API compatibility
                try
                {
                    var prop = alignment.GetType().GetProperty("DesignSpeed");
                    if (prop != null && prop.CanWrite)
                    {
                        oldSpeed ??= (double?)prop.GetValue(alignment);
                        prop.SetValue(alignment, designSpeed.Value);
                    }
                    else
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Failed to set design speed: {ex.Message}");
                    }
                }
                catch (Exception innerEx)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Failed to set design speed: {innerEx.Message}");
                }
            }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("get_drawing_summary:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                old_design_speed = oldSpeed.HasValue ? Math.Round(oldSpeed.Value, 1) : (double?)null,
                new_design_speed = Math.Round(designSpeed.Value, 1)
            }));
        }
    }
}
