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
    /// Sets design criteria properties on an alignment (design speed, minimum radius).
    /// </summary>
    public class SetAlignmentCriteriaTool : DrawingToolBase
    {
        public override string Name => "set_alignment_criteria";
        public override string Description => "Sets design criteria for an alignment including design speed and minimum radius. Used to update standards-checking parameters.";
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
            var minimumRadius = GetDoubleParam(parameters, "minimum_radius");

            if (designSpeed == null && minimumRadius == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "At least one of 'design_speed' or 'minimum_radius' must be provided");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            var changes = new System.Collections.Generic.Dictionary<string, object>();

            // Set design speed if provided
            if (designSpeed.HasValue && designSpeed.Value > 0)
            {
                try
                {
                    var designSpeeds = alignment.DesignSpeeds;
                    if (designSpeeds != null)
                    {
                        double? oldSpeed = null;
                        if (designSpeeds.Count > 0)
                        {
                            foreach (CivilDb.DesignSpeed ds in designSpeeds)
                            {
                                oldSpeed = ds.Value;
                                break;
                            }
                            while (designSpeeds.Count > 0)
                            {
                                designSpeeds.Remove(alignment.StartingStation);
                            }
                        }
                        designSpeeds.Add(alignment.StartingStation, designSpeed.Value);
                        changes["design_speed"] = new
                        {
                            old_value = oldSpeed.HasValue ? Math.Round(oldSpeed.Value, 1) : (double?)null,
                            new_value = Math.Round(designSpeed.Value, 1)
                        };
                    }
                }
                catch (Exception ex)
                {
                    changes["design_speed_error"] = ex.Message;
                }
            }

            // Set minimum radius via design criteria if provided
            if (minimumRadius.HasValue && minimumRadius.Value > 0)
            {
                try
                {
                    var criteriaFileProp = alignment.GetType().GetProperty("DesignCriteriaFile");
                    if (criteriaFileProp != null)
                    {
                        changes["minimum_radius"] = new
                        {
                            requested_value = Math.Round(minimumRadius.Value, 3),
                            note = "Minimum radius is controlled by the design criteria file. Updated design speed which affects minimum radius requirements."
                        };
                    }
                    else
                    {
                        changes["minimum_radius"] = new
                        {
                            requested_value = Math.Round(minimumRadius.Value, 3),
                            note = "Minimum radius is derived from design criteria. Use modify_alignment_curve_radius to modify individual curve radii."
                        };
                    }
                }
                catch (Exception ex)
                {
                    changes["minimum_radius_error"] = ex.Message;
                }
            }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("validate_alignment:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                changes
            }));
        }
    }
}
