using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Profile
{
    /// <summary>
    /// Gets detailed geometry for a profile including PVIs, grades, and K-values.
    /// </summary>
    public class GetProfileGeometryTool : DrawingToolBase
    {
        public override string Name => "get_profile_geometry";
        public override string Description => "Gets detailed vertical geometry for a profile including PVI points, grades, K-values, and vertical curve data.";
        public override string Category => ToolCategories.Profile;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(45);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var includePviDetails = GetBoolParam(parameters, "include_pvi_details", true);

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

            var warnings = new List<string>();

            var normalizedType = ListProfilesTool.NormalizeProfileType(profile.ProfileType.ToString());

            var result = new ProfileGeometryResult
            {
                Name = profile.Name,
                Description = profile.Description,
                AlignmentName = foundAlignmentName,
                ProfileType = normalizedType,
                IsEditable = normalizedType is "design" or "layout",
                StartStation = profile.StartingStation,
                EndStation = profile.EndingStation,
                MinElevation = profile.ElevationMin,
                MaxElevation = profile.ElevationMax
            };

            // Extract PVI data for editable (design/layout) profiles
            if (normalizedType is "design" or "layout" && includePviDetails)
            {
                result.PVIs = ExtractPVIs(profile, ct, warnings);
                result.Statistics = CalculateStatistics(result.PVIs);
            }

            // Build alignment context (speed-at-station + junction proximity).
            // Shares one cached DrawingSummary across all tool calls in the
            // batch via ``cache`` — without that, 5 parallel get_*_geometry
            // calls each ran a full extraction and saturated the 30 s tool
            // timeout (agent.log 2026-05-27T15:13:44 lines 107–115).
            var contextResolver = AlignmentContextResolver.For(foundAlignmentName, cache);

            // Extract entity data — annotated with resolved speed + junction proximity
            result.Entities = ExtractEntities(profile, contextResolver, ct, warnings, foundAlignmentName);

            // Annotate PVIs with resolved speed too (PVI station = grade-break point;
            // analyzer uses it to compare grade against max-grade-at-that-speed).
            if (result.PVIs != null)
            {
                foreach (var pvi in result.PVIs)
                    pvi.ResolvedDesignSpeedKph = contextResolver.ResolveSpeedKph(pvi.Station);
            }

            if (warnings.Count > 0)
                result.ExtractionWarnings = warnings;

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private List<PviInfo> ExtractPVIs(Autodesk.Civil.DatabaseServices.Profile profile, CancellationToken ct, List<string> warnings)
        {
            var pvis = new List<PviInfo>();

            try
            {
                int index = 0;
                foreach (ProfilePVI pvi in profile.PVIs)
                {
                    ct.ThrowIfCancellationRequested();

                    var pviInfo = new PviInfo
                    {
                        Index = index++,
                        Station = pvi.RawStation,
                        Elevation = pvi.Elevation
                    };

                    // Calculate grades
                    if (index > 1 && pvis.Count > 0)
                    {
                        var prevPvi = pvis[pvis.Count - 1];
                        var distance = pvi.RawStation - prevPvi.Station;
                        if (distance > 0)
                        {
                            var elevChange = pvi.Elevation - prevPvi.Elevation;
                            pviInfo.GradeIn = (elevChange / distance) * 100.0; // Percentage
                        }
                    }

                    pvis.Add(pviInfo);
                }

                // Calculate grade out for each PVI
                for (int i = 0; i < pvis.Count - 1; i++)
                {
                    var current = pvis[i];
                    var next = pvis[i + 1];
                    var distance = next.Station - current.Station;
                    if (distance > 0)
                    {
                        var elevChange = next.Elevation - current.Elevation;
                        current.GradeOut = (elevChange / distance) * 100.0;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractPVIs error: {ex.Message}");
                warnings.Add($"PVI extraction error: {ex.Message}");
            }

            return pvis;
        }

        private List<ProfileEntityInfo> ExtractEntities(
            Autodesk.Civil.DatabaseServices.Profile profile,
            AlignmentContextResolver contextResolver,
            CancellationToken ct,
            List<string> warnings,
            string alignmentName = "")
        {
            var entities = new List<ProfileEntityInfo>();

            try
            {
                int index = 0;
                foreach (ProfileEntity entity in profile.Entities)
                {
                    ct.ThrowIfCancellationRequested();

                    var entityInfo = new ProfileEntityInfo
                    {
                        Index = index++,
                        EntityType = entity.EntityType.ToString(),
                        StartStation = entity.StartStation,
                        EndStation = entity.EndStation,
                        StartElevation = entity.StartElevation,
                        EndElevation = entity.EndElevation,
                        Length = entity.Length
                    };

                    // Per-element context for the AI planner.
                    // Speed is resolved at the element midpoint; junction
                    // proximity uses the engineer-validated 150 m buffer
                    // so curves ending just before a junction (the profile
                    // 73 case at 540–661 vs junction 700.6) are flagged.
                    var mid = 0.5 * (entity.StartStation + entity.EndStation);
                    entityInfo.ResolvedDesignSpeedKph = contextResolver.ResolveSpeedKph(mid);
                    entityInfo.NearIntersection = contextResolver.NearestJunction(
                        entity.StartStation, entity.EndStation);

                    // ── TEMP DIAGNOSTIC (remove after intersection root-cause) ──
                    // Logs the per-curve near-junction decision so we can see why
                    // a crest curve does/doesn't get tagged. Tagged for grep/removal.
                    var niDiag = entityInfo.NearIntersection == null
                        ? "none"
                        : $"junction@{entityInfo.NearIntersection.JunctionStation} "
                          + $"offset={entityInfo.NearIntersection.OffsetM}m "
                          + $"others=[{string.Join(",", entityInfo.NearIntersection.OtherAlignments)}]";
                    System.Diagnostics.Debug.WriteLine(
                        $"[INTERSECTION-DIAG] align='{alignmentName}' entity#{entityInfo.Index} "
                        + $"type={entityInfo.EntityType} "
                        + $"sta={entityInfo.StartStation:F2}-{entityInfo.EndStation:F2} "
                        + $"nearIntersection={niDiag}");

                    // Type-specific properties
                    var entityTypeName = entity.EntityType.ToString();
                    if (entityTypeName == "Tangent")
                    {
                        var tangent = entity as ProfileTangent;
                        if (tangent != null)
                        {
                            entityInfo.Grade = tangent.Grade * 100.0; // Percentage
                        }
                    }
                    else
                    {
                        // Handle curve types via reflection (Circular, Parabolic, etc.)
                        try
                        {
                            var curveLengthProp = entity.GetType().GetProperty("CurveLength");
                            if (curveLengthProp != null)
                                entityInfo.CurveLength = Convert.ToDouble(curveLengthProp.GetValue(entity));

                            var kProp = entity.GetType().GetProperty("K");
                            if (kProp != null)
                                entityInfo.KValue = Convert.ToDouble(kProp.GetValue(entity));

                            var highLowProp = entity.GetType().GetProperty("HighLowPointStation");
                            if (highLowProp != null)
                                entityInfo.HighPoint = Convert.ToDouble(highLowProp.GetValue(entity));

                            var curveTypeProp = entity.GetType().GetProperty("CurveType");
                            if (curveTypeProp != null)
                            {
                                var ctValue = curveTypeProp.GetValue(entity)?.ToString();
                                entityInfo.IsCrest = ctValue?.Contains("Crest") == true;
                                entityInfo.IsSag = ctValue?.Contains("Sag") == true;
                            }
                        }
                        catch { }
                    }

                    entities.Add(entityInfo);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractEntities error: {ex.Message}");
                warnings.Add($"Entity extraction error: {ex.Message}");
            }

            return entities;
        }

        private ProfileStatisticsInfo CalculateStatistics(List<PviInfo> pvis)
        {
            var stats = new ProfileStatisticsInfo
            {
                PviCount = pvis.Count
            };

            double maxGrade = 0;
            double minGrade = 0;
            double totalUpgrade = 0;
            double totalDowngrade = 0;

            foreach (var pvi in pvis)
            {
                if (pvi.GradeOut.HasValue)
                {
                    var grade = pvi.GradeOut.Value;
                    if (grade > maxGrade) maxGrade = grade;
                    if (grade < minGrade) minGrade = grade;

                    if (grade > 0) totalUpgrade += grade;
                    else totalDowngrade += Math.Abs(grade);
                }
            }

            stats.MaxGrade = maxGrade;
            stats.MinGrade = minGrade;
            stats.TotalUpgrade = totalUpgrade;
            stats.TotalDowngrade = totalDowngrade;

            return stats;
        }
    }

    #region Result Models

    public class ProfileGeometryResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string AlignmentName { get; set; } = string.Empty;
        public string ProfileType { get; set; } = string.Empty;
        public bool IsEditable { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double MinElevation { get; set; }
        public double MaxElevation { get; set; }
        public List<PviInfo>? PVIs { get; set; }
        public List<ProfileEntityInfo>? Entities { get; set; }
        public ProfileStatisticsInfo? Statistics { get; set; }
        public List<string>? ExtractionWarnings { get; set; }
    }

    public class PviInfo
    {
        public int Index { get; set; }
        public double Station { get; set; }
        public double Elevation { get; set; }
        public double? GradeIn { get; set; }
        public double? GradeOut { get; set; }

        /// <summary>
        /// Design speed (km/h) resolved at this PVI station from the
        /// alignment's speed_segments (falls back to the primary
        /// DesignSpeedKph when no segments are defined).
        /// </summary>
        [JsonPropertyName("resolved_design_speed_kph")]
        public double? ResolvedDesignSpeedKph { get; set; }
    }

    public class ProfileEntityInfo
    {
        public int Index { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double StartElevation { get; set; }
        public double EndElevation { get; set; }
        public double Length { get; set; }
        public double? Grade { get; set; }
        public double? CurveLength { get; set; }
        public double? KValue { get; set; }
        public double? HighPoint { get; set; }
        public bool? IsCrest { get; set; }
        public bool? IsSag { get; set; }

        /// <summary>
        /// Design speed (km/h) resolved at the element midpoint from the
        /// alignment's speed_segments (falls back to the primary
        /// DesignSpeedKph). Critical for multi-speed alignments where
        /// the binding threshold differs between segments.
        /// </summary>
        [JsonPropertyName("resolved_design_speed_kph")]
        public double? ResolvedDesignSpeedKph { get; set; }

        /// <summary>
        /// Nearest detected junction within 150 m of the element's
        /// station range. Null when no junction is nearby. When present,
        /// signals to the analyzer that junction-specific thresholds
        /// (e.g. Vol 2 / Table 8.3 — DSD vertical radii) apply on top of
        /// the open-road criteria.
        /// </summary>
        [JsonPropertyName("near_intersection")]
        public NearIntersection? NearIntersection { get; set; }
    }

    public class ProfileStatisticsInfo
    {
        public int PviCount { get; set; }
        public double MaxGrade { get; set; }
        public double MinGrade { get; set; }
        public double TotalUpgrade { get; set; }
        public double TotalDowngrade { get; set; }
    }

    #endregion
}
