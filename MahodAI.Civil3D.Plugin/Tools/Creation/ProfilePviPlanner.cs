using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Pure PVI pre-validation and clamping for <see cref="CreateProfileTool"/>:
    /// stations near the alignment end are clamped (Civil 3D rejects a PVI at the exact
    /// end station), out-of-range and duplicate stations are rejected with Hebrew
    /// per-point errors. No AutoCAD dependency — unit-testable.
    /// </summary>
    public static class ProfilePviPlanner
    {
        public sealed class PlanResult
        {
            /// <summary>Points accepted for AddPVI, clamped where needed, in input order.</summary>
            public List<(double Station, double Elevation)> Valid { get; } = new();
            /// <summary>Hebrew per-point errors for rejected points.</summary>
            public List<string> Errors { get; } = new();
            public int ClampedCount { get; set; }
        }

        /// <summary>
        /// Validates and clamps the requested PVI points against the alignment station range.
        /// </summary>
        public static PlanResult Plan(
            IReadOnlyList<(double Station, double Elevation)> requested,
            double startStation,
            double endStation,
            double endClamp = 0.1,
            double duplicateTolerance = 0.01)
        {
            var result = new PlanResult();
            if (requested == null || requested.Count == 0) return result;
            if (endClamp < 0) endClamp = 0;

            var acceptedStations = new List<double>();
            foreach (var (station, elevation) in requested)
            {
                double s = station;

                if (double.IsNaN(s) || double.IsInfinity(s) ||
                    double.IsNaN(elevation) || double.IsInfinity(elevation))
                {
                    result.Errors.Add($"נקודת PVI לא חוקית (תחנה {s}, רום {elevation}).");
                    continue;
                }

                if (s < startStation - 1e-6)
                {
                    result.Errors.Add(
                        $"תחנה {s:F2} נמצאת לפני תחילת הציר (תחנה {startStation:F2}) — הנקודה נדחתה.");
                    continue;
                }

                if (s > endStation + 1e-6)
                {
                    result.Errors.Add(
                        $"תחנה {s:F2} נמצאת אחרי סוף הציר (תחנה {endStation:F2}) — הנקודה נדחתה.");
                    continue;
                }

                // Clamp stations at/near the end — Civil 3D throws on a PVI at the exact
                // end station, and a small pullback gives the corridor's last applied
                // assembly a clean landing zone (see CreateCorridorTool REGION_END_MARGIN).
                if (s > endStation - endClamp)
                {
                    s = endStation - endClamp;
                    result.ClampedCount++;
                }

                bool duplicate = false;
                foreach (var existing in acceptedStations)
                {
                    if (Math.Abs(existing - s) < duplicateTolerance)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    result.Errors.Add($"תחנה {s:F2} כפולה — נקודת PVI נוספת באותה תחנה נדחתה.");
                    continue;
                }

                acceptedStations.Add(s);
                result.Valid.Add((s, elevation));
            }

            return result;
        }
    }
}
