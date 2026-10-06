using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Profile
{
    /// <summary>
    /// Gets the elevation at a specific station along a profile.
    /// </summary>
    public class GetProfileElevationAtStationTool : DrawingToolBase
    {
        public override string Name => "get_profile_elevation_at_station";
        public override string Description => "Gets the elevation at a specific station along a profile.";
        public override string Category => ToolCategories.Profile;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

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

            if (!station.HasValue)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Station parameter is required");
            }

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the profile
            var profile = CivilObjectFinder.FindProfileByName(tr, civilDoc, profileName, alignmentName);
            if (profile == null)
            {
                return ToolResult.NotFound("Profile", profileName);
            }

            // Determine the owning alignment name
            string foundAlignmentName = alignmentName ?? string.Empty;
            if (string.IsNullOrEmpty(foundAlignmentName))
            {
                foreach (ObjectId alId in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
                {
                    var al = tr.GetObject(alId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (al == null) continue;
                    foreach (ObjectId pfId in al.GetProfileIds())
                    {
                        if (pfId == profile.ObjectId) { foundAlignmentName = al.Name; break; }
                    }
                    if (!string.IsNullOrEmpty(foundAlignmentName)) break;
                }
            }

            // Validate station is within range
            if (station.Value < profile.StartingStation || station.Value > profile.EndingStation)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    $"Station {station.Value} is outside profile range ({profile.StartingStation} - {profile.EndingStation})");
            }

            try
            {
                var elevation = profile.ElevationAt(station.Value);

                // Get grade at station
                double? grade = null;
                string? entityType = null;
                try
                {
                    foreach (ProfileEntity entity in profile.Entities)
                    {
                        if (station.Value >= entity.StartStation && station.Value <= entity.EndStation)
                        {
                            entityType = entity.EntityType.ToString();
                            if (entity is ProfileTangent tangent)
                            {
                                grade = tangent.Grade * 100.0;
                            }
                            break;
                        }
                    }
                }
                catch { }

                var result = new ProfileElevationResult
                {
                    ProfileName = profile.Name,
                    AlignmentName = foundAlignmentName,
                    Station = station.Value,
                    Elevation = elevation,
                    Grade = grade,
                    EntityType = entityType
                };

                return await Task.FromResult(ToolResult.Ok(result));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, $"Failed to get elevation at station: {ex.Message}");
            }
        }
    }

    #region Result Models

    public class ProfileElevationResult
    {
        public string ProfileName { get; set; } = string.Empty;
        public string AlignmentName { get; set; } = string.Empty;
        public double Station { get; set; }
        public double Elevation { get; set; }
        public double? Grade { get; set; }
        public string? EntityType { get; set; }
    }

    #endregion
}
