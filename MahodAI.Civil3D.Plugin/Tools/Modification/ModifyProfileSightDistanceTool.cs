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
    /// Adjusts a vertical curve length to meet a required sight distance.
    /// </summary>
    public class ModifyProfileSightDistanceTool : DrawingToolBase
    {
        public override string Name => "modify_profile_sight_distance";
        public override string Description => "Adjusts a vertical curve in a layout profile to meet a required stopping sight distance. Calculates and sets the needed K-value or curve length.";
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
            var requiredSightDistance = GetDoubleParam(parameters, "required_sight_distance");

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");
            if (requiredSightDistance == null || requiredSightDistance.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'required_sight_distance' must be a positive number (meters)");

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

            // Read current values
            double? oldKValue = null;
            double? oldLength = null;

            try
            {
                var entityType = entity.GetType();
                var kProp = entityType.GetProperty("K");
                if (kProp != null)
                    oldKValue = Convert.ToDouble(kProp.GetValue(entity));

                var curveLengthProp = entityType.GetProperty("CurveLength");
                if (curveLengthProp != null)
                    oldLength = Convert.ToDouble(curveLengthProp.GetValue(entity));
            }
            catch { }

            // Calculate grade difference (A) from adjacent tangent grades
            double gradeIn = 0, gradeOut = 0;
            try
            {
                var entityType = entity.GetType();
                var gradeInProp = entityType.GetProperty("GradeIn");
                var gradeOutProp = entityType.GetProperty("GradeOut");
                if (gradeInProp != null)
                    gradeIn = Convert.ToDouble(gradeInProp.GetValue(entity));
                if (gradeOutProp != null)
                    gradeOut = Convert.ToDouble(gradeOutProp.GetValue(entity));
            }
            catch { }

            double algebraicDiff = Math.Abs(gradeOut - gradeIn) * 100.0; // Convert to percent
            if (algebraicDiff < 0.001)
                algebraicDiff = 1.0; // Minimum to avoid division by zero

            // Calculate required K-value for sight distance
            // For crest curves: K = S²/(200*(√h1 + √h2)²) where h1=1.08m (eye), h2=0.60m (object)
            // For sag curves: K = S²/(200*(h + S*tan(1°))) where h=0.60m (headlight height)
            // Simplified: required curve length L = K * A
            double requiredK = requiredSightDistance.Value * requiredSightDistance.Value / (200.0 * algebraicDiff);
            double requiredLength = requiredK * algebraicDiff;

            // Apply the new curve length
            bool modified = false;
            try
            {
                var entityType = entity.GetType();

                var curveLengthProp = entityType.GetProperty("CurveLength");
                if (curveLengthProp != null && curveLengthProp.CanWrite)
                {
                    curveLengthProp.SetValue(entity, requiredLength);
                    modified = true;
                }
                else
                {
                    var lengthProp = entityType.GetProperty("Length");
                    if (lengthProp != null && lengthProp.CanWrite)
                    {
                        lengthProp.SetValue(entity, requiredLength);
                        modified = true;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify vertical curve: {ex.Message}");
            }

            if (!modified)
                return ToolResult.Fail(ToolErrorCodes.NotSupported,
                    $"Could not modify curve length on {entityTypeName} entity. The property may be read-only.");

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                element_index = idx,
                entity_type = entityTypeName,
                required_sight_distance = Math.Round(requiredSightDistance.Value, 3),
                algebraic_difference_percent = Math.Round(algebraicDiff, 3),
                calculated_k_value = Math.Round(requiredK, 3),
                old_length = oldLength.HasValue ? Math.Round(oldLength.Value, 3) : (double?)null,
                new_length = Math.Round(requiredLength, 3),
                old_k_value = oldKValue.HasValue ? Math.Round(oldKValue.Value, 3) : (double?)null
            }));
        }
    }
}
