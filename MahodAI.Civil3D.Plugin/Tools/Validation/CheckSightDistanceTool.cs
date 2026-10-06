using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Checks sight distance along a profile at regular intervals.
    ///
    /// Algorithm from Igor's CheckSightDistance.cs:
    /// 1. Sample profile at 1m intervals to get station-elevation pairs
    /// 2. For each check station, place observer eye (elevation + eyeHeight)
    /// 3. Cast ray forward to target (elevation + targetHeight)
    /// 4. Check if ray intersects any profile segment (line-of-sight blocked)
    /// 5. Record maximum visible distance
    /// 6. Compare against required stopping/passing distance from Israeli standards
    ///
    /// Israeli stopping sight distance (meters):
    ///   30km/h=30, 40=45, 50=65, 60=85, 70=110, 80=140, 90=175, 100=210, 110=250, 120=290
    /// </summary>
    public class CheckSightDistanceTool : DrawingToolBase
    {
        public override string Name => "check_sight_distance";
        public override string Description =>
            "Checks sight distance along a profile. Compares available forward sight distance " +
            "against required stopping/passing distance per Israeli standards. " +
            "Reports pass/fail for each checked station.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // Eye height and target height per Israeli standards
        private const double EYE_HEIGHT = 1.05;    // meters above road surface
        private const double TARGET_HEIGHT = 0.15;  // meters (stopping: object on road)
        private const double PASSING_TARGET_HEIGHT = 1.05; // meters (passing: oncoming car)
        private const double SAMPLE_STEP = 1.0;     // Profile sampling resolution (meters)

        // Israeli stopping sight distance table (meters)
        private static readonly Dictionary<int, double> STOPPING_DISTANCE = new()
        {
            [30] = 30, [40] = 45, [50] = 65, [60] = 85, [70] = 110,
            [80] = 140, [90] = 175, [100] = 210, [110] = 250, [120] = 290
        };

        private static readonly Dictionary<int, double> PASSING_DISTANCE = new()
        {
            [30] = 120, [40] = 180, [50] = 250, [60] = 320, [70] = 400,
            [80] = 480, [90] = 560, [100] = 640, [110] = 720, [120] = 800
        };

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the alignment""
                },
                ""profile_name"": {
                    ""type"": ""string"",
                    ""description"": ""FG profile name (default: auto-detect)""
                },
                ""check_type"": {
                    ""type"": ""string"",
                    ""enum"": [""stopping"", ""passing""],
                    ""description"": ""Type of sight distance check (default: stopping)""
                },
                ""design_speed"": {
                    ""type"": ""number"",
                    ""description"": ""Design speed in km/h (default: 100)""
                },
                ""check_interval"": {
                    ""type"": ""number"",
                    ""description"": ""Interval between check stations in meters (default: 50)""
                }
            },
            ""required"": [""alignment_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var profileName = GetStringParam(parameters, "profile_name");
            var checkType = GetStringParam(parameters, "check_type") ?? "stopping";
            var designSpeed = (int)(GetDoubleParam(parameters, "design_speed") ?? 100.0);
            var checkInterval = GetDoubleParam(parameters, "check_interval") ?? 50.0;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find alignment
            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to read alignment");

            // Find FG profile
            CivilDb.Profile? fgProfile = null;
            if (!string.IsNullOrEmpty(profileName))
            {
                var profId = ObjectFinder.FindProfile(civilDoc, tr, profileName, alignmentName);
                if (profId != null)
                    fgProfile = tr.GetObject(profId.Value, OpenMode.ForRead) as CivilDb.Profile;
            }

            // Auto-detect FG profile if not specified
            if (fgProfile == null)
            {
                string[] fgPatterns = { "_FG", "- FG", "_Design", "Design", "FG" };
                foreach (ObjectId profId in alignment.GetProfileIds())
                {
                    var prof = tr.GetObject(profId, OpenMode.ForRead) as CivilDb.Profile;
                    if (prof == null) continue;

                    // Skip EG profiles (from surface)
                    try
                    {
                        var profileType = prof.GetType().GetProperty("ProfileType");
                        if (profileType != null)
                        {
                            var pType = profileType.GetValue(prof)?.ToString();
                            if (pType != null && pType.Contains("Surface")) continue;
                        }
                    }
                    catch { }

                    foreach (var pattern in fgPatterns)
                    {
                        if (prof.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            fgProfile = prof;
                            break;
                        }
                    }
                    if (fgProfile != null) break;
                }

                // Fallback: if no FG-named profile found, use first non-surface profile
                if (fgProfile == null)
                {
                    foreach (ObjectId profId in alignment.GetProfileIds())
                    {
                        var prof = tr.GetObject(profId, OpenMode.ForRead) as CivilDb.Profile;
                        if (prof == null) continue;
                        try
                        {
                            var profileType = prof.GetType().GetProperty("ProfileType");
                            if (profileType != null)
                            {
                                var pType = profileType.GetValue(prof)?.ToString();
                                if (pType != null && pType.Contains("Surface")) continue;
                            }
                        }
                        catch { }
                        fgProfile = prof;
                        break;
                    }
                }
            }

            if (fgProfile == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"No FG profile found for alignment '{alignmentName}'. Create a design profile first.");

            // Get required distance from standards
            bool isPassing = checkType.Equals("passing", StringComparison.OrdinalIgnoreCase);
            var distTable = isPassing ? PASSING_DISTANCE : STOPPING_DISTANCE;
            double requiredDistance = GetNearestValue(distTable, designSpeed);
            double targetHeight = isPassing ? PASSING_TARGET_HEIGHT : TARGET_HEIGHT;

            // Sample profile at SAMPLE_STEP intervals
            double startStation = alignment.StartingStation;
            double endStation = alignment.EndingStation;
            var profilePoints = SampleProfile(fgProfile, startStation, endStation, SAMPLE_STEP);

            if (profilePoints.Count < 10)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Profile too short for sight distance analysis.");

            // Check sight distance at each interval
            var results = new List<Dictionary<string, object>>();
            int passCount = 0;
            int failCount = 0;
            int sectionNum = 0;

            for (double station = startStation; station <= endStation - requiredDistance; station += checkInterval)
            {
                ct.ThrowIfCancellationRequested();
                sectionNum++;

                double visibleDistance = CalcSightDistance(
                    profilePoints, station, endStation, EYE_HEIGHT, targetHeight, requiredDistance * 1.5);

                bool passes = visibleDistance >= requiredDistance;
                if (passes) passCount++;
                else failCount++;

                results.Add(new Dictionary<string, object>
                {
                    ["section"] = sectionNum,
                    ["station"] = Math.Round(station, 1),
                    ["sight_distance_m"] = Math.Round(visibleDistance, 1),
                    ["required_m"] = requiredDistance,
                    ["status"] = passes ? "PASS" : "FAIL"
                });
            }

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["alignment_name"] = alignmentName,
                ["profile_name"] = fgProfile.Name,
                ["check_type"] = checkType,
                ["design_speed_kmh"] = designSpeed,
                ["required_distance_m"] = requiredDistance,
                ["eye_height_m"] = EYE_HEIGHT,
                ["target_height_m"] = targetHeight,
                ["total_checks"] = passCount + failCount,
                ["pass_count"] = passCount,
                ["fail_count"] = failCount,
                ["stations"] = results,
                ["message"] = $"Sight distance check ({checkType}) for '{fgProfile.Name}' at {designSpeed} km/h: " +
                    $"{passCount} PASS, {failCount} FAIL out of {passCount + failCount} stations. " +
                    $"Required: {requiredDistance}m."
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Sample profile elevations at regular intervals.
        /// Returns list of (station, elevation) pairs.
        /// </summary>
        private static List<(double station, double elevation)> SampleProfile(
            CivilDb.Profile profile, double startStation, double endStation, double step)
        {
            var points = new List<(double, double)>();
            for (double s = startStation; s <= endStation; s += step)
            {
                try
                {
                    double elev = profile.ElevationAt(s);
                    points.Add((s, elev));
                }
                catch { }
            }
            // Ensure end station is included
            if (points.Count > 0 && Math.Abs(points.Last().Item1 - endStation) > step / 2)
            {
                try
                {
                    double elev = profile.ElevationAt(endStation);
                    points.Add((endStation, elev));
                }
                catch { }
            }
            return points;
        }

        /// <summary>
        /// Calculate sight distance from a given station using line-of-sight ray casting.
        /// Algorithm from Igor's CheckSightDistance.cs.
        ///
        /// Places observer at (station, elevation + eyeHeight).
        /// Casts ray forward to (station + d, elevation_at(station+d) + targetHeight).
        /// Checks if ray intersects any profile segment.
        /// Returns maximum distance before line-of-sight is blocked.
        /// </summary>
        private static double CalcSightDistance(
            List<(double station, double elevation)> profile,
            double observerStation,
            double endStation,
            double eyeHeight,
            double targetHeight,
            double maxDistance)
        {
            // Find observer elevation
            double observerElev = InterpolateElevation(profile, observerStation) + eyeHeight;
            double maxVisible = 0;

            // Scan forward from observer
            for (double d = 10; d <= maxDistance; d += 1.0)
            {
                double targetStation = observerStation + d;
                if (targetStation > endStation) break;

                double targetElev = InterpolateElevation(profile, targetStation) + targetHeight;

                // Check if line of sight (observer → target) intersects any profile segment
                bool blocked = false;
                for (int i = 0; i < profile.Count - 1; i++)
                {
                    var (s1, e1) = profile[i];
                    var (s2, e2) = profile[i + 1];

                    // Only check segments between observer and target
                    if (s2 <= observerStation || s1 >= targetStation) continue;

                    // Check intersection of line (observerSta, observerElev)→(targetSta, targetElev)
                    // with segment (s1, e1)→(s2, e2)
                    if (SegmentsIntersect(
                        observerStation, observerElev,
                        targetStation, targetElev,
                        s1, e1, s2, e2))
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked)
                {
                    return maxVisible;
                }

                maxVisible = d;
            }

            return maxVisible;
        }

        /// <summary>
        /// Check if two line segments intersect in 2D (station-elevation plane).
        /// </summary>
        private static bool SegmentsIntersect(
            double ax1, double ay1, double ax2, double ay2,
            double bx1, double by1, double bx2, double by2)
        {
            double dx_a = ax2 - ax1;
            double dy_a = ay2 - ay1;
            double dx_b = bx2 - bx1;
            double dy_b = by2 - by1;

            double denom = dx_a * dy_b - dy_a * dx_b;
            if (Math.Abs(denom) < 1e-10) return false; // Parallel

            double t = ((bx1 - ax1) * dy_b - (by1 - ay1) * dx_b) / denom;
            double u = ((bx1 - ax1) * dy_a - (by1 - ay1) * dx_a) / denom;

            // Intersection point must be within both segments
            // For the profile segment: 0 <= u <= 1
            // For the sight line: we care about 0.01 < t < 0.99 (not at endpoints)
            return t > 0.01 && t < 0.99 && u >= 0 && u <= 1;
        }

        /// <summary>
        /// Interpolate elevation from profile sample points.
        /// </summary>
        private static double InterpolateElevation(
            List<(double station, double elevation)> profile, double station)
        {
            if (profile.Count == 0) return 0;
            if (station <= profile[0].station) return profile[0].elevation;
            if (station >= profile[^1].station) return profile[^1].elevation;

            for (int i = 0; i < profile.Count - 1; i++)
            {
                if (profile[i].station <= station && station <= profile[i + 1].station)
                {
                    double dx = profile[i + 1].station - profile[i].station;
                    if (dx <= 0) return profile[i].elevation;
                    double t = (station - profile[i].station) / dx;
                    return profile[i].elevation + t * (profile[i + 1].elevation - profile[i].elevation);
                }
            }
            return profile[^1].elevation;
        }

        /// <summary>
        /// Get nearest value from a speed→distance table.
        /// </summary>
        private static double GetNearestValue(Dictionary<int, double> table, int speed)
        {
            if (table.ContainsKey(speed)) return table[speed];
            int nearest = table.Keys.OrderBy(k => Math.Abs(k - speed)).First();
            return table[nearest];
        }
    }
}
