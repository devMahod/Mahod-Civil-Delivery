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
    /// Inserts a new PVI (Point of Vertical Intersection) into a layout profile.
    /// </summary>
    public class AddProfilePVITool : DrawingToolBase
    {
        public override string Name => "add_profile_pvi";
        public override string Description => "Inserts a new PVI (Point of Vertical Intersection) into a layout profile at the specified station and elevation.";
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
            var station = GetDoubleParam(parameters, "station");
            var elevation = GetDoubleParam(parameters, "elevation");

            if (station == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'station' is missing");
            if (elevation == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'elevation' is missing");

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

            // Check that station is not already occupied by an existing PVI
            foreach (CivilDb.ProfilePVI existingPvi in profile.PVIs)
            {
                if (Math.Abs(existingPvi.RawStation - station.Value) < 0.01)
                {
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"A PVI already exists at station {station.Value:F3}. Use modify_profile_pvi_elevation to change its elevation.");
                }
            }

            // Count existing PVIs before adding
            int pviCountBefore = 0;
            foreach (CivilDb.ProfilePVI _ in profile.PVIs)
                pviCountBefore++;

            // Add the new PVI
            try
            {
                profile.PVIs.AddPVI(station.Value, elevation.Value);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to add PVI: {ex.Message}");
            }

            // Count PVIs after adding
            int pviCountAfter = 0;
            foreach (CivilDb.ProfilePVI _ in profile.PVIs)
                pviCountAfter++;

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_profile_elevation_at_station:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                station = Math.Round(station.Value, 3),
                elevation = Math.Round(elevation.Value, 3),
                pvi_count_before = pviCountBefore,
                pvi_count_after = pviCountAfter
            }));
        }
    }
}
