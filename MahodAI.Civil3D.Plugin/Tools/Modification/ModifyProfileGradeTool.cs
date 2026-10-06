using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Modifies the grade between two PVIs by adjusting a PVI elevation.
    /// </summary>
    public class ModifyProfileGradeTool : DrawingToolBase
    {
        public override string Name => "modify_profile_grade";
        public override string Description => "Modifies the grade between two PVIs in a layout profile by adjusting the target PVI elevation. Specify the PVI index and the desired new elevation to achieve the target grade.";
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
            var pviIndex = GetIntParam(parameters, "pvi_index");
            var newElevation = GetDoubleParam(parameters, "new_elevation");

            if (pviIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'pvi_index' is missing");
            if (newElevation == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_elevation' is missing");

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

            // Collect all PVIs
            var pviList = new List<(double Station, double Elevation)>();
            foreach (CivilDb.ProfilePVI pvi in profile.PVIs)
            {
                pviList.Add((pvi.RawStation, pvi.Elevation));
            }

            int idx = pviIndex.Value;
            if (idx < 0 || idx >= pviList.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"PVI index {idx} out of range (0-{pviList.Count - 1})");

            double oldElevation = pviList[idx].Elevation;
            double station = pviList[idx].Station;

            // Calculate old grades
            double? oldGradeIn = null;
            double? oldGradeOut = null;
            if (idx > 0)
            {
                var prev = pviList[idx - 1];
                var dist = station - prev.Station;
                if (dist > 0)
                    oldGradeIn = ((oldElevation - prev.Elevation) / dist) * 100.0;
            }
            if (idx < pviList.Count - 1)
            {
                var next = pviList[idx + 1];
                var dist = next.Station - station;
                if (dist > 0)
                    oldGradeOut = ((next.Elevation - oldElevation) / dist) * 100.0;
            }

            // Modify the PVI elevation
            int currentIndex = 0;
            foreach (CivilDb.ProfilePVI pvi in profile.PVIs)
            {
                if (currentIndex == idx)
                {
                    pvi.Elevation = newElevation.Value;
                    break;
                }
                currentIndex++;
            }

            // Calculate new grades
            double? newGradeIn = null;
            double? newGradeOut = null;
            if (idx > 0)
            {
                var prev = pviList[idx - 1];
                var dist = station - prev.Station;
                if (dist > 0)
                    newGradeIn = ((newElevation.Value - prev.Elevation) / dist) * 100.0;
            }
            if (idx < pviList.Count - 1)
            {
                var next = pviList[idx + 1];
                var dist = next.Station - station;
                if (dist > 0)
                    newGradeOut = ((next.Elevation - newElevation.Value) / dist) * 100.0;
            }

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_profile_elevation_at_station:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                pvi_index = idx,
                station = Math.Round(station, 3),
                old_elevation = Math.Round(oldElevation, 3),
                new_elevation = Math.Round(newElevation.Value, 3),
                grade_in = new
                {
                    old_value = oldGradeIn.HasValue ? Math.Round(oldGradeIn.Value, 3) : (double?)null,
                    new_value = newGradeIn.HasValue ? Math.Round(newGradeIn.Value, 3) : (double?)null
                },
                grade_out = new
                {
                    old_value = oldGradeOut.HasValue ? Math.Round(oldGradeOut.Value, 3) : (double?)null,
                    new_value = newGradeOut.HasValue ? Math.Round(newGradeOut.Value, 3) : (double?)null
                }
            }));
        }
    }
}
