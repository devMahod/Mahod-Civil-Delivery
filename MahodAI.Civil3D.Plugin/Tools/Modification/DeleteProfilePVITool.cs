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
    /// Removes a PVI (Point of Vertical Intersection) from a layout profile.
    /// </summary>
    public class DeleteProfilePVITool : DrawingToolBase
    {
        public override string Name => "delete_profile_pvi";
        public override string Description => "Removes a PVI (Point of Vertical Intersection) from a layout profile by index. The adjacent tangents will be merged.";
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

            if (pviIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'pvi_index' is missing");

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

            // Don't allow removing first or last PVI (they define the profile extent)
            if (pviList.Count <= 2)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Cannot remove PVI: profile must have at least 2 PVIs");

            if (idx == 0 || idx == pviList.Count - 1)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Cannot remove the first or last PVI of the profile. Only intermediate PVIs can be removed.");

            double removedStation = pviList[idx].Station;
            double removedElevation = pviList[idx].Elevation;

            // Remove the PVI by station
            try
            {
                var removeMethod = profile.PVIs.GetType().GetMethod("RemovePVI")
                                ?? profile.PVIs.GetType().GetMethod("Remove");
                if (removeMethod != null)
                    removeMethod.Invoke(profile.PVIs, new object[] { removedStation });
                else
                    throw new InvalidOperationException("RemovePVI method not available in this Civil 3D version");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to remove PVI: {ex.Message}");
            }

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_profile_elevation_at_station:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                removed_pvi_index = idx,
                removed_station = Math.Round(removedStation, 3),
                removed_elevation = Math.Round(removedElevation, 3),
                remaining_pvi_count = pviList.Count - 1
            }));
        }
    }
}
