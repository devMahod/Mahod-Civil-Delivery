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

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Extracts raw profile vertical geometry measurements for standards comparison.
    /// Returns measured values + computed available sight distance — NO hardcoded thresholds.
    /// The AI agent compares actual values against RAG-retrieved standards.
    /// </summary>
    public class ValidateProfileTool : DrawingToolBase
    {
        public override string Name => "validate_profile";
        public override string Description => "Extracts profile vertical geometry measurements: grades, K-values, curve types, and computed available sight distance. Returns raw data for standards comparison.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // Physical constants for sight distance calculation (NOT standards)
        private const double EyeHeight = 1.08;   // meters — driver eye height above road
        private const double ObjectHeight = 0.60; // meters — object height on road surface

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var designSpeedParam = GetIntParam(parameters, "design_speed_kph");

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the profile and its parent alignment
            var profile = CivilObjectFinder.FindProfileByName(tr, civilDoc, profileName, alignmentName);
            if (profile == null)
            {
                return ToolResult.NotFound("Profile", profileName);
            }

            // Find the parent alignment that owns this profile
            Autodesk.Civil.DatabaseServices.Alignment? parentAlignment = null;
            string foundAlignmentName = string.Empty;
            if (!string.IsNullOrEmpty(alignmentName))
            {
                parentAlignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);
                foundAlignmentName = parentAlignment?.Name ?? alignmentName;
            }
            else
            {
                foreach (ObjectId alId in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
                {
                    var al = tr.GetObject(alId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (al == null) continue;
                    foreach (ObjectId pfId in al.GetProfileIds())
                    {
                        if (pfId == profile.ObjectId)
                        {
                            parentAlignment = al;
                            foundAlignmentName = al.Name;
                            break;
                        }
                    }
                    if (parentAlignment != null) break;
                }
            }

            // Determine design speed from parameter or parent alignment
            // Design speed: parameter from backend takes priority, fallback to parent alignment
            int? designSpeedKph = designSpeedParam ?? ReadParentAlignmentDesignSpeed(parentAlignment);

            var result = new ProfileValidationResult
            {
                ProfileName = profile.Name,
                AlignmentName = foundAlignmentName,
                DesignSpeedKph = designSpeedKph ?? 0,
                ProfileType = GetProfileType(profile),
                StartStation = Math.Round(profile.StartingStation, 2),
                EndStation = Math.Round(profile.EndingStation, 2),
                Elements = new List<ProfileElementMeasurement>(),
                Statistics = new ProfileMeasurementStats()
            };

            // Per-element design speed: a multi-speed alignment (e.g. 70 km/h on
            // 0–698 then 100 km/h after) must NOT be checked at a single global
            // speed — a crest in the 70-segment is governed by the 70-km/h
            // threshold. Resolve speed at each element's midpoint from the
            // alignment's speed segments (same resolver get_profile_geometry uses).
            var speedResolver = AlignmentContextResolver.For(foundAlignmentName, cache);

            // Extract raw measurements for each element
            double maxGrade = 0;
            double? minKCrest = null;
            double? minKSag = null;
            int tangentCount = 0;
            int crestCount = 0;
            int sagCount = 0;

            int elementIndex = 0;
            foreach (ProfileEntity entity in profile.Entities)
            {
                ct.ThrowIfCancellationRequested();

                double mid = 0.5 * (entity.StartStation + entity.EndStation);
                double? resolvedSpeed = speedResolver.ResolveSpeedKph(mid);

                var entityTypeName = entity.EntityType.ToString();
                if (entityTypeName == "Tangent")
                {
                    var tangent = entity as ProfileTangent;
                    if (tangent != null)
                    {
                        double gradePercent = Math.Round(Math.Abs(tangent.Grade) * 100.0, 2);
                        result.Elements.Add(new ProfileElementMeasurement
                        {
                            Index = elementIndex,
                            Type = "Tangent",
                            Station = Math.Round(tangent.StartStation, 2),
                            EndStation = Math.Round(tangent.EndStation, 2),
                            Length = Math.Round(tangent.Length, 2),
                            GradePercent = gradePercent,
                            ResolvedDesignSpeedKph = resolvedSpeed
                        });
                        if (gradePercent > maxGrade) maxGrade = gradePercent;
                        tangentCount++;
                    }
                }
                else
                {
                    // Vertical curve (crest or sag)
                    var elem = ExtractCurveElement(entity, elementIndex);
                    if (elem != null)
                    {
                        elem.ResolvedDesignSpeedKph = resolvedSpeed;
                        result.Elements.Add(elem);
                        if (elem.CurveType == "Crest")
                        {
                            crestCount++;
                            if (elem.KValue.HasValue)
                            {
                                if (!minKCrest.HasValue || elem.KValue.Value < minKCrest.Value)
                                    minKCrest = elem.KValue;
                            }
                        }
                        else if (elem.CurveType == "Sag")
                        {
                            sagCount++;
                            if (elem.KValue.HasValue)
                            {
                                if (!minKSag.HasValue || elem.KValue.Value < minKSag.Value)
                                    minKSag = elem.KValue;
                            }
                        }
                    }
                }

                elementIndex++;
            }

            // Fill statistics
            result.Statistics.TotalElements = elementIndex;
            result.Statistics.TangentCount = tangentCount;
            result.Statistics.CrestCurveCount = crestCount;
            result.Statistics.SagCurveCount = sagCount;
            result.Statistics.MaxGradePercent = Math.Round(maxGrade, 2);
            result.Statistics.MinKCrest = minKCrest.HasValue ? Math.Round(minKCrest.Value, 1) : (double?)null;
            result.Statistics.MinKSag = minKSag.HasValue ? Math.Round(minKSag.Value, 1) : (double?)null;
            // Radius = K * 100 (parabolic geometry)
            result.Statistics.MinRadiusCrestM = minKCrest.HasValue ? Math.Round(minKCrest.Value * 100.0, 0) : (double?)null;
            result.Statistics.MinRadiusSagM = minKSag.HasValue ? Math.Round(minKSag.Value * 100.0, 0) : (double?)null;

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private ProfileElementMeasurement? ExtractCurveElement(ProfileEntity entity, int index)
        {
            double kValue = 0;
            double curveLength = entity.Length;
            string curveType = "Unknown";

            try
            {
                var kProp = entity.GetType().GetProperty("K");
                if (kProp != null)
                {
                    var kObj = kProp.GetValue(entity);
                    if (kObj != null)
                        kValue = Math.Abs(Convert.ToDouble(kObj));
                }
                // DEBUG: Log raw K and computed RadiusM for Arthur's investigation
                System.Diagnostics.Debug.WriteLine(
                    $"[ValidateProfile] Element {index}: K={kValue}, RadiusM={kValue * 100.0}, " +
                    $"Station={entity.StartStation:F2}, Length={entity.Length:F2}");

                var curveLengthProp = entity.GetType().GetProperty("CurveLength");
                if (curveLengthProp != null)
                {
                    var clObj = curveLengthProp.GetValue(entity);
                    if (clObj != null)
                        curveLength = Convert.ToDouble(clObj);
                }

                var curveTypeProp = entity.GetType().GetProperty("CurveType");
                if (curveTypeProp != null)
                {
                    var ctObj = curveTypeProp.GetValue(entity);
                    if (ctObj != null)
                    {
                        var ctStr = ctObj.ToString() ?? "";
                        if (ctStr.Contains("Crest"))
                            curveType = "Crest";
                        else if (ctStr.Contains("Sag"))
                            curveType = "Sag";
                    }
                }
            }
            catch
            {
                return null;
            }

            // Compute vertical curve radius R from K value.
            // IMPORTANT: Do NOT use entity.Radius — Civil 3D's "Radius" property for
            // parabolic vertical curves returns the K-value, NOT the geometric radius R.
            // Israeli standards (tables 6.3-6.5) specify R in meters.
            // For a parabolic curve: R = K × 100 (where K = L/A, A in %).
            double? radiusM = kValue > 0 ? kValue * 100.0 : (double?)null;

            var elem = new ProfileElementMeasurement
            {
                Index = index,
                Type = "VerticalCurve",
                CurveType = curveType,
                Station = Math.Round(entity.StartStation, 2),
                EndStation = Math.Round(entity.EndStation, 2),
                Length = Math.Round(curveLength, 2),
                KValue = kValue > 0 ? Math.Round(kValue, 1) : (double?)null,
                RadiusM = radiusM.HasValue && radiusM.Value > 0 ? Math.Round(radiusM.Value, 1) : (double?)null
            };

            // Compute available sight distance (pure geometry math, no standards)
            // SSD = sqrt(2 * K * h1) + sqrt(2 * K * h2)
            // h1 = eye height (1.08m), h2 = object height (0.60m)
            if (curveType == "Crest" && kValue > 0)
            {
                double ssd = Math.Sqrt(2.0 * kValue * EyeHeight) + Math.Sqrt(2.0 * kValue * ObjectHeight);
                elem.AvailableSightDistanceM = Math.Round(ssd, 1);
            }

            return elem;
        }

        private static string GetProfileType(Autodesk.Civil.DatabaseServices.Profile profile)
        {
            try
            {
                return profile.ProfileType.ToString();
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Read design speed from the parent alignment's DesignSpeeds collection.
        /// </summary>
        private static int? ReadParentAlignmentDesignSpeed(
            Autodesk.Civil.DatabaseServices.Alignment? alignment)
        {
            if (alignment == null) return null;
            try
            {
                var speeds = alignment.DesignSpeeds;
                if (speeds != null && speeds.Count > 0)
                {
                    double maxSpeed = 0;
                    foreach (DesignSpeed ds in speeds)
                    {
                        if (ds.Value > maxSpeed)
                            maxSpeed = ds.Value;
                    }
                    if (maxSpeed > 0)
                        return (int)Math.Round(maxSpeed);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadParentAlignmentDesignSpeed error: {ex.Message}");
            }
            return null;
        }
    }

    #region Result Models

    public class ProfileValidationResult
    {
        public string ProfileName { get; set; } = string.Empty;
        public string AlignmentName { get; set; } = string.Empty;
        public int DesignSpeedKph { get; set; }
        public string ProfileType { get; set; } = string.Empty;
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public List<ProfileElementMeasurement> Elements { get; set; } = new();
        public ProfileMeasurementStats? Statistics { get; set; }
    }

    public class ProfileElementMeasurement
    {
        public int Index { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? CurveType { get; set; }
        public double Station { get; set; }
        public double EndStation { get; set; }
        public double Length { get; set; }
        public double? GradePercent { get; set; }
        public double? KValue { get; set; }
        /// <summary>
        /// Vertical curve radius in meters (R = K × 100).
        /// This is what Israeli standards tables 6.3-6.5 specify.
        /// </summary>
        public double? RadiusM { get; set; }
        public double? AvailableSightDistanceM { get; set; }

        /// <summary>
        /// Design speed (km/h) resolved at this element's midpoint from the
        /// parent alignment's speed segments. On a multi-speed alignment this
        /// differs per element; the analyzer MUST compare each element against
        /// the threshold for ITS resolved speed, not a single global speed.
        /// </summary>
        [JsonPropertyName("resolved_design_speed_kph")]
        public double? ResolvedDesignSpeedKph { get; set; }
    }

    public class ProfileMeasurementStats
    {
        public int TotalElements { get; set; }
        public int TangentCount { get; set; }
        public int CrestCurveCount { get; set; }
        public int SagCurveCount { get; set; }
        public double MaxGradePercent { get; set; }
        public double? MinKCrest { get; set; }
        public double? MinKSag { get; set; }
        /// <summary>Min vertical curve radius (crest) in meters — R = K × 100.</summary>
        public double? MinRadiusCrestM { get; set; }
        /// <summary>Min vertical curve radius (sag) in meters — R = K × 100.</summary>
        public double? MinRadiusSagM { get; set; }
    }

    // Keep old models for backward compat
    public class ProfileDesignCriteria
    {
        public double MaxGrade { get; set; }
        public double MinKCrest { get; set; }
        public double MinKSag { get; set; }
    }

    public class ProfileViolation
    {
        public int ElementIndex { get; set; }
        public string ElementType { get; set; } = string.Empty;
        public double Station { get; set; }
        public string ViolationType { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning";
        public string Message { get; set; } = string.Empty;
        public double ActualValue { get; set; }
        public double RequiredValue { get; set; }
    }

    public class ProfileValidationSummary
    {
        public int TotalElements { get; set; }
        public int ViolationCount { get; set; }
        public bool PassedValidation { get; set; }
    }

    #endregion
}
