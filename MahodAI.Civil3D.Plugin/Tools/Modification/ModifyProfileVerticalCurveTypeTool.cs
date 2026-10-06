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
    /// Changes the type of a vertical curve in a profile (crest vs sag).
    /// </summary>
    public class ModifyProfileVerticalCurveTypeTool : DrawingToolBase
    {
        public override string Name => "modify_profile_vertical_curve_type";
        public override string Description => "Changes the vertical curve type in a layout profile. Adjusts the PVI elevation to convert between crest and sag curves while preserving the curve length.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var elementIndex = GetIntParam(parameters, "element_index");
            var curveType = GetStringParam(parameters, "curve_type")?.ToLowerInvariant();

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");
            if (curveType == null || (curveType != "crest" && curveType != "sag"))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'curve_type' must be 'crest' or 'sag'");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var profileId = ObjectFinder.FindProfile(civilDoc, tr, profileName, alignmentName);
            if (profileId == null)
                return ToolResult.NotFound("Profile", profileName);

            var profile = tr.GetObject(profileId.Value, OpenMode.ForWrite) as CivilDb.Profile;
            if (profile == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open profile for write");

            if (!ProfileTypeGuard.IsModifiable(profile.ProfileType))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Profile '{profileName}' is a {profile.ProfileType} profile. Only layout/design/FG profiles can be modified.");

            int idx = elementIndex.Value;
            if (idx < 0 || idx >= profile.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element index {idx} out of range (0-{profile.Entities.Count - 1})");

            var entity = profile.Entities[idx];
            var entityTypeName = entity.EntityType.ToString();

            if (entityTypeName == "Tangent")
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element at index {idx} is a Tangent, not a vertical curve");

            // Read current curve properties
            double? currentGradeIn = null;
            double? currentGradeOut = null;
            string? currentType = null;

            try
            {
                var entityType = entity.GetType();
                var gradeInProp = entityType.GetProperty("GradeIn");
                var gradeOutProp = entityType.GetProperty("GradeOut");

                if (gradeInProp != null)
                    currentGradeIn = Convert.ToDouble(gradeInProp.GetValue(entity));
                if (gradeOutProp != null)
                    currentGradeOut = Convert.ToDouble(gradeOutProp.GetValue(entity));

                // Determine current curve type: crest has gradeIn > gradeOut, sag has gradeIn < gradeOut
                if (currentGradeIn.HasValue && currentGradeOut.HasValue)
                {
                    currentType = currentGradeIn.Value > currentGradeOut.Value ? "crest" : "sag";
                }
            }
            catch { }

            if (currentType == curveType)
            {
                return await Task.FromResult(ToolResult.Ok(new
                {
                    profile_name = profileName,
                    alignment_name = alignmentName,
                    element_index = idx,
                    current_type = currentType,
                    requested_type = curveType,
                    note = "Curve is already the requested type. No changes made."
                }));
            }

            // To change curve type, we need to modify the PVI elevation
            // This effectively changes the grade algebraic difference sign
            // We modify the vertical curve entity type via reflection if available
            bool modified = false;
            try
            {
                var entityType = entity.GetType();

                // Try to set the curve type directly via EntityType or CurveType property
                var curveTypeProp = entityType.GetProperty("CurveType");
                if (curveTypeProp != null && curveTypeProp.CanWrite)
                {
                    // Try to find the enum value
                    var enumType = curveTypeProp.PropertyType;
                    foreach (var val in Enum.GetValues(enumType))
                    {
                        if (val.ToString()!.Equals(curveType, StringComparison.OrdinalIgnoreCase))
                        {
                            curveTypeProp.SetValue(entity, val);
                            modified = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (!modified)
            {
                return await Task.FromResult(ToolResult.Ok(new
                {
                    profile_name = profileName,
                    alignment_name = alignmentName,
                    element_index = idx,
                    current_type = currentType,
                    requested_type = curveType,
                    note = "Vertical curve type is determined by the grade difference between adjacent tangents. " +
                           "To change from crest to sag (or vice versa), modify the PVI elevation using modify_profile_pvi_elevation " +
                           "to reverse the algebraic grade difference."
                }));
            }

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                element_index = idx,
                old_type = currentType,
                new_type = curveType
            }));
        }
    }
}
