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
    /// Modifies the elevation of a PVI (Point of Vertical Intersection) in a profile.
    /// </summary>
    public class ModifyProfilePVIElevationTool : DrawingToolBase
    {
        public override string Name => "modify_profile_pvi_elevation";
        public override string Description => "Modifies the elevation of a PVI (Point of Vertical Intersection) in a layout profile. This affects the vertical alignment grades entering and leaving this PVI.";
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

            // Verify it's a layout profile (not surface profile)
            if (!ProfileTypeGuard.IsModifiable(profile.ProfileType))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Profile '{profileName}' is a {profile.ProfileType} profile. Only layout/design/FG profiles can be modified.");

            int idx = pviIndex.Value;
            int pviCount = 0;
            foreach (CivilDb.ProfilePVI _ in profile.PVIs)
            {
                pviCount++;
            }

            if (idx < 0 || idx >= pviCount)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"PVI index {idx} out of range (0-{pviCount - 1})");

            // Find and modify the PVI
            double oldElevation = 0;
            double station = 0;
            int currentIndex = 0;

            foreach (CivilDb.ProfilePVI pvi in profile.PVIs)
            {
                if (currentIndex == idx)
                {
                    oldElevation = pvi.Elevation;
                    station = pvi.RawStation;
                    pvi.Elevation = newElevation.Value;
                    break;
                }
                currentIndex++;
            }

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_profile_elevation_at_station:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                pvi_index = idx,
                station = Math.Round(station, 3),
                old_elevation = Math.Round(oldElevation, 3),
                new_elevation = Math.Round(newElevation.Value, 3)
            }));
        }
    }
}
