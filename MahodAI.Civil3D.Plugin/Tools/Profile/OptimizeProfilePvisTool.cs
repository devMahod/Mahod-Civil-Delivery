using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Optimizes vertical profile PVI positions to best fit a reference surface/profile
    /// while respecting constraints. Ported from MahodCivilNet CBestFitProfile.
    /// </summary>
    public class OptimizeProfilePvisTool : DrawingToolBase
    {
        public override string Name => "optimize_profile_pvis";
        public override string Description => "Optimizes vertical profile PVI positions to best fit a reference surface/profile while ENFORCING vertical constraints (min vertical-curve K and/or radius, max grade, min PVI spacing, min curve length). Every exposed constraint is enforced and each PVI reports requested-vs-achieved K/R with units. Returns optimized PVI list.";
        public override string Category => ToolCategories.Profile;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private const double DT2 = 1.0;    // Iteration step for curve chord
        private const double DX = 1.0;     // Iteration step for horizontal PVI movement
        private const double DY = 0.01;    // Iteration step for vertical PVI movement
        private const int MaxIterations = 5000;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var numPvis = GetIntParam(parameters, "num_pvis") ?? 5;
            // P0-04: the vertical-curve constraint. The agent (CriteriaStore) supplies the
            // governing value for the road class / design speed and crest/sag case: a minimum
            // K (min_k, m per %) and/or a minimum parabolic radius (min_radius, m). Either or
            // both may be given; each is enforced when > 0. Previously min_radius was accepted
            // and echoed but NEVER used — the profile could optimise to any curvature.
            var minRadius = GetDoubleParam(parameters, "min_radius") ?? 0.0;
            var minK = GetDoubleParam(parameters, "min_k") ?? 0.0;
            if (minRadius < 0) minRadius = 0.0;
            if (minK < 0) minK = 0.0;
            var maxGrade = GetDoubleParam(parameters, "max_grade") ?? 0.08; // 8%
            var minT2 = GetDoubleParam(parameters, "min_curve_length") ?? 10.0;
            var minPviSpacing = GetDoubleParam(parameters, "min_pvi_spacing") ?? 30.0;
            var sampleInterval = GetDoubleParam(parameters, "sample_interval") ?? 5.0;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "CivilDocument not available");

            // Find alignment and profile
            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);
            if (alignment == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var profile = CivilObjectFinder.FindProfileByName(tr, civilDoc, profileName, alignmentName);
            if (profile == null)
                return ToolResult.NotFound("Profile", profileName);

            ct.ThrowIfCancellationRequested();

            // Sample reference elevations
            double startSta = profile.StartingStation;
            double endSta = profile.EndingStation;
            var samples = new List<(double Station, double Elevation)>();
            for (double s = startSta; s <= endSta; s += sampleInterval)
            {
                try { samples.Add((s, profile.ElevationAt(s))); }
                catch { }
            }
            try { samples.Add((endSta, profile.ElevationAt(endSta))); }
            catch { }

            if (samples.Count < 3)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Insufficient profile samples for optimization");

            ct.ThrowIfCancellationRequested();

            // Initialize PVIs evenly distributed
            numPvis = Math.Clamp(numPvis, 3, 20);
            var pvis = InitializePvis(samples, numPvis);

            // Optimization loop
            double bestCost = ComputeCost(pvis, samples);
            var bestPvis = pvis.Select(p => (p.Station, p.Elevation, p.T2)).ToList();
            int noImprovement = 0;

            for (int iter = 0; iter < MaxIterations && noImprovement < 500; iter++)
            {
                if (iter % 100 == 0) ct.ThrowIfCancellationRequested();

                bool improved = false;

                for (int i = 1; i < pvis.Count - 1; i++) // Don't move endpoints
                {
                    var original = pvis[i];

                    // Try moving station
                    foreach (double dx in new[] { DX, -DX })
                    {
                        pvis[i] = (original.Station + dx, original.Elevation, original.T2);
                        if (IsFeasible(pvis, i, minRadius, minK, maxGrade, minT2, minPviSpacing))
                        {
                            double cost = ComputeCost(pvis, samples);
                            if (cost < bestCost - 1e-6)
                            {
                                bestCost = cost;
                                bestPvis = pvis.Select(p => (p.Station, p.Elevation, p.T2)).ToList();
                                improved = true;
                                continue;
                            }
                        }
                        pvis[i] = bestPvis[i];
                    }

                    // Try moving elevation
                    foreach (double dy in new[] { DY, -DY })
                    {
                        pvis[i] = (original.Station, original.Elevation + dy, original.T2);
                        if (IsFeasible(pvis, i, minRadius, minK, maxGrade, minT2, minPviSpacing))
                        {
                            double cost = ComputeCost(pvis, samples);
                            if (cost < bestCost - 1e-6)
                            {
                                bestCost = cost;
                                bestPvis = pvis.Select(p => (p.Station, p.Elevation, p.T2)).ToList();
                                improved = true;
                                continue;
                            }
                        }
                        pvis[i] = bestPvis[i];
                    }

                    // Try adjusting curve chord
                    foreach (double dt in new[] { DT2, -DT2 })
                    {
                        double newT2 = Math.Max(minT2, original.T2 + dt);
                        pvis[i] = (original.Station, original.Elevation, newT2);
                        if (IsFeasible(pvis, i, minRadius, minK, maxGrade, minT2, minPviSpacing))
                        {
                            double cost = ComputeCost(pvis, samples);
                            if (cost < bestCost - 1e-6)
                            {
                                bestCost = cost;
                                bestPvis = pvis.Select(p => (p.Station, p.Elevation, p.T2)).ToList();
                                improved = true;
                                continue;
                            }
                        }
                        pvis[i] = bestPvis[i];
                    }
                }

                if (!improved) noImprovement++;
                else noImprovement = 0;
            }

            // Build result
            var optimizedPvis = bestPvis.Select((p, idx) =>
            {
                double gradeIn = idx > 0
                    ? (p.Elevation - bestPvis[idx - 1].Elevation) / (p.Station - bestPvis[idx - 1].Station)
                    : 0;
                double gradeOut = idx < bestPvis.Count - 1
                    ? (bestPvis[idx + 1].Elevation - p.Elevation) / (bestPvis[idx + 1].Station - p.Station)
                    : 0;

                bool interior = idx > 0 && idx < bestPvis.Count - 1;
                var vc = interior
                    ? VerticalConstraintEvaluator.Evaluate(idx, gradeIn, gradeOut, p.T2)
                    : null;

                return new
                {
                    index = idx,
                    station = Math.Round(p.Station, 2),
                    elevation = Math.Round(p.Elevation, 3),
                    curve_length_m = Math.Round(p.T2, 1),
                    grade_in_percent = Math.Round(gradeIn * 100, 2),
                    grade_out_percent = Math.Round(gradeOut * 100, 2),
                    // P0-04: requested-vs-achieved vertical curvature, with explicit units.
                    curve_type = vc == null ? "endpoint" : (vc.IsCrest ? "crest" : vc.IsSag ? "sag" : "straight"),
                    algebraic_grade_diff_percent = vc == null ? (double?)null : Math.Round(vc.AlgebraicGradeDiffPercent, 2),
                    achieved_k_m_per_pct = vc?.KValue.HasValue == true ? Math.Round(vc.KValue.Value, 1) : (double?)null,
                    achieved_radius_m = vc?.RadiusM.HasValue == true ? Math.Round(vc.RadiusM.Value, 1) : (double?)null,
                };
            }).ToList();

            return ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                profile_name = profileName,
                // Report ONLY what is enforced, with explicit units. min_radius / min_k are
                // omitted when 0 (not required) so the payload never implies a constraint that
                // was not applied.
                constraints = new
                {
                    min_vertical_radius_m = minRadius > 0 ? (double?)minRadius : null,
                    min_k_m_per_pct = minK > 0 ? (double?)minK : null,
                    max_grade_percent = Math.Round(maxGrade * 100, 2),
                    min_curve_length_m = minT2,
                    min_pvi_spacing_m = minPviSpacing,
                    curvature_enforced = minRadius > 0 || minK > 0,
                },
                optimization = new { iterations_used = "up_to_" + MaxIterations, final_cost = Math.Round(bestCost, 4), sample_count = samples.Count },
                pvi_count = optimizedPvis.Count,
                pvis = optimizedPvis
            });
        }

        private static List<(double Station, double Elevation, double T2)> InitializePvis(
            List<(double Station, double Elevation)> samples, int count)
        {
            var pvis = new List<(double Station, double Elevation, double T2)>();
            double startSta = samples[0].Station;
            double endSta = samples[samples.Count - 1].Station;
            double interval = (endSta - startSta) / (count - 1);

            for (int i = 0; i < count; i++)
            {
                double station = startSta + i * interval;
                // Find closest sample for initial elevation
                var closest = samples.MinBy(s => Math.Abs(s.Station - station));
                double t2 = i == 0 || i == count - 1 ? 0 : 20.0; // No curve at endpoints
                pvis.Add((station, closest.Elevation, t2));
            }
            return pvis;
        }

        private static double ComputeCost(List<(double Station, double Elevation, double T2)> pvis,
            List<(double Station, double Elevation)> samples)
        {
            double cost = 0;
            foreach (var (station, targetElev) in samples)
            {
                double profileElev = InterpolateProfile(pvis, station);
                double diff = profileElev - targetElev;
                cost += diff * diff;
            }
            return cost;
        }

        private static double InterpolateProfile(List<(double Station, double Elevation, double T2)> pvis, double station)
        {
            if (pvis.Count < 2) return 0;
            if (station <= pvis[0].Station) return pvis[0].Elevation;
            if (station >= pvis[pvis.Count - 1].Station) return pvis[pvis.Count - 1].Elevation;

            for (int i = 1; i < pvis.Count; i++)
            {
                if (station <= pvis[i].Station)
                {
                    double t = (station - pvis[i - 1].Station) / (pvis[i].Station - pvis[i - 1].Station);
                    return pvis[i - 1].Elevation + t * (pvis[i].Elevation - pvis[i - 1].Elevation);
                }
            }
            return pvis[pvis.Count - 1].Elevation;
        }

        private static bool IsFeasible(List<(double Station, double Elevation, double T2)> pvis,
            int index, double minRadius, double minK, double maxGrade, double minT2, double minSpacing)
        {
            var p = pvis[index];

            // Check T2 >= minT2
            if (p.T2 < minT2 && index > 0 && index < pvis.Count - 1) return false;

            // Check spacing with neighbors
            if (index > 0 && p.Station - pvis[index - 1].Station < minSpacing) return false;
            if (index < pvis.Count - 1 && pvis[index + 1].Station - p.Station < minSpacing) return false;

            // Check grades
            if (index > 0)
            {
                double grade = Math.Abs((p.Elevation - pvis[index - 1].Elevation) / (p.Station - pvis[index - 1].Station));
                if (grade > maxGrade) return false;
            }
            if (index < pvis.Count - 1)
            {
                double grade = Math.Abs((pvis[index + 1].Elevation - p.Elevation) / (pvis[index + 1].Station - p.Station));
                if (grade > maxGrade) return false;
            }

            // P0-04: actually ENFORCE the vertical-curve constraint at interior PVIs. Compute
            // the achieved K/R from the in/out grades and the curve length (T2) and reject any
            // curve sharper than the required minimum K and/or radius. This is the check the
            // dead min_radius parameter used to imply but never performed.
            if ((minRadius > 0 || minK > 0) && index > 0 && index < pvis.Count - 1)
            {
                double gradeIn = (p.Elevation - pvis[index - 1].Elevation) / (p.Station - pvis[index - 1].Station);
                double gradeOut = (pvis[index + 1].Elevation - p.Elevation) / (pvis[index + 1].Station - p.Station);
                var report = VerticalConstraintEvaluator.Evaluate(index, gradeIn, gradeOut, p.T2);
                if (!VerticalConstraintEvaluator.SatisfiesCurvature(report, minK, minRadius))
                    return false;
            }

            return true;
        }
    }
}
