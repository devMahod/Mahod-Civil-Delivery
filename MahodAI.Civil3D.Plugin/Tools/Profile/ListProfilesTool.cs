using System;
using System.Collections.Generic;
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
    /// Lists all profiles with summary information.
    /// </summary>
    public class ListProfilesTool : DrawingToolBase
    {
        public override string Name => "list_profiles";
        public override string Description => "Lists all profiles in the drawing with their names, associated alignments, station ranges, and elevation ranges.";
        public override string Category => ToolCategories.Profile;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            var alignmentFilter = GetStringParam(parameters, "alignment_name");
            var profileTypeFilter = GetStringParam(parameters, "profile_type");
            var limit = GetIntParam(parameters, "limit") ?? 100;

            var profiles = new List<ProfileSummary>();

            foreach (ObjectId alignmentId in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
            {
                ct.ThrowIfCancellationRequested();
                if (profiles.Count >= limit) break;

                var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                if (alignment == null) continue;

                // Apply alignment filter if specified
                if (!string.IsNullOrEmpty(alignmentFilter) &&
                    !alignment.Name.Equals(alignmentFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    ct.ThrowIfCancellationRequested();
                    if (profiles.Count >= limit) break;

                    var profile = tr.GetObject(profileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                    if (profile == null) continue;

                    var profileType = NormalizeProfileType(profile.ProfileType.ToString());

                    // Apply profile type filter if specified
                    if (!string.IsNullOrEmpty(profileTypeFilter) &&
                        !profileType.Contains(profileTypeFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var summary = new ProfileSummary
                    {
                        Name = profile.Name,
                        Description = profile.Description,
                        AlignmentName = alignment.Name,
                        ProfileType = profileType,
                        IsEditable = profileType is "design" or "layout",
                        StartStation = profile.StartingStation,
                        EndStation = profile.EndingStation,
                        Length = profile.EndingStation - profile.StartingStation
                    };

                    // Get elevation range
                    try
                    {
                        summary.MinElevation = profile.ElevationMin;
                        summary.MaxElevation = profile.ElevationMax;
                    }
                    catch { }

                    // Count PVIs for editable (design/layout) profiles
                    if (profileType is "design" or "layout")
                    {
                        try
                        {
                            summary.PviCount = profile.PVIs.Count;
                        }
                        catch { }
                    }

                    profiles.Add(summary);
                }
            }

            var result = new ListProfilesResult
            {
                Profiles = profiles,
                TotalCount = profiles.Count
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Normalize Civil 3D ProfileType enum string to consistent lowercase values.
        /// </summary>
        internal static string NormalizeProfileType(string typeStr)
        {
            if (string.IsNullOrEmpty(typeStr)) return "unknown";
            if (typeStr == "Design") return "design";
            if (typeStr.Contains("Layout") || typeStr.Contains("FiniteGrade")) return "layout";
            if (typeStr == "FG" || typeStr.Contains("FinishedGround")) return "fg";
            if (typeStr == "EG" || typeStr.Contains("ExistingGround")) return "existing_ground";
            if (typeStr.Contains("Surface")) return "surface";
            return typeStr.ToLowerInvariant();
        }
    }

    #region Result Models

    public class ListProfilesResult
    {
        public List<ProfileSummary> Profiles { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class ProfileSummary
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string AlignmentName { get; set; } = string.Empty;
        public string ProfileType { get; set; } = string.Empty;
        public bool IsEditable { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double Length { get; set; }
        public double? MinElevation { get; set; }
        public double? MaxElevation { get; set; }
        public int? PviCount { get; set; }
    }

    #endregion
}
