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

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Extracts superelevation data for an alignment including critical station slopes
    /// for all lane segments. Handles overlapping curve transitions.
    /// Ported from MahodCivilNet CSuperelevation.
    /// </summary>
    public class GetSuperelevationDataTool : DrawingToolBase
    {
        public override string Name => "get_superelevation_data";
        public override string Description => "Extracts superelevation data for an alignment including critical station slopes for all lane segments (left/right inner/outer). Handles overlapping curve transitions.";
        public override string Category => ToolCategories.Alignment;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(45);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var sampleInterval = GetDoubleParam(parameters, "sample_interval"); // optional: if set, interpolate at intervals

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "CivilDocument not available");

            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);
            if (alignment == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            ct.ThrowIfCancellationRequested();

            // Extract critical stations from all superelevation curves
            var leftOutSlopes = new List<(double Station, double Slope)>();
            var leftInSlopes = new List<(double Station, double Slope)>();
            var rightOutSlopes = new List<(double Station, double Slope)>();
            var rightInSlopes = new List<(double Station, double Slope)>();
            var warnings = new List<string>();

            try
            {
                foreach (var sc in alignment.SuperelevationCurves)
                {
                    foreach (Autodesk.Civil.DatabaseServices.SuperelevationCriticalStation cs in sc.CriticalStations)
                    {
                        double station = cs.Station;

                        try { leftOutSlopes.Add((station, cs.GetSlope(Autodesk.Civil.SuperelevationCrossSegmentType.LeftOutLaneCrossSlope))); }
                        catch { }

                        try { leftInSlopes.Add((station, cs.GetSlope(Autodesk.Civil.SuperelevationCrossSegmentType.LeftInLaneCrossSlope))); }
                        catch { }

                        try { rightOutSlopes.Add((station, cs.GetSlope(Autodesk.Civil.SuperelevationCrossSegmentType.RightOutLaneCrossSlope))); }
                        catch { }

                        try { rightInSlopes.Add((station, cs.GetSlope(Autodesk.Civil.SuperelevationCrossSegmentType.RightInLaneCrossSlope))); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read superelevation curves: {ex.Message}");
            }

            // Remove overlaps between consecutive curves (ported from MahodCivilNet RemoveOverlaps)
            RemoveOverlaps(leftOutSlopes);
            RemoveOverlaps(leftInSlopes);
            RemoveOverlaps(rightOutSlopes);
            RemoveOverlaps(rightInSlopes);

            // Sort by station
            leftOutSlopes.Sort((a, b) => a.Station.CompareTo(b.Station));
            leftInSlopes.Sort((a, b) => a.Station.CompareTo(b.Station));
            rightOutSlopes.Sort((a, b) => a.Station.CompareTo(b.Station));
            rightInSlopes.Sort((a, b) => a.Station.CompareTo(b.Station));

            ct.ThrowIfCancellationRequested();

            // Build result
            object result;
            if (sampleInterval.HasValue && sampleInterval.Value > 0)
            {
                // Interpolated output at regular intervals
                var samples = new List<object>();
                double start = alignment.StartingStation;
                double end = alignment.EndingStation;
                for (double s = start; s <= end; s += sampleInterval.Value)
                {
                    samples.Add(new
                    {
                        station = Math.Round(s, 2),
                        left_outside = Math.Round(InterpolateSlope(leftOutSlopes, s), 4),
                        left_inside = Math.Round(InterpolateSlope(leftInSlopes, s), 4),
                        right_outside = Math.Round(InterpolateSlope(rightOutSlopes, s), 4),
                        right_inside = Math.Round(InterpolateSlope(rightInSlopes, s), 4)
                    });
                }
                result = new
                {
                    alignment_name = alignmentName,
                    mode = "interpolated",
                    sample_interval = sampleInterval.Value,
                    sample_count = samples.Count,
                    samples,
                    warnings
                };
            }
            else
            {
                // Critical stations output
                var criticalStations = new List<object>();
                var allStations = new SortedSet<double>();
                foreach (var s in leftOutSlopes) allStations.Add(s.Station);
                foreach (var s in rightOutSlopes) allStations.Add(s.Station);

                foreach (double station in allStations)
                {
                    criticalStations.Add(new
                    {
                        station = Math.Round(station, 2),
                        left_outside = Math.Round(InterpolateSlope(leftOutSlopes, station), 4),
                        left_inside = Math.Round(InterpolateSlope(leftInSlopes, station), 4),
                        right_outside = Math.Round(InterpolateSlope(rightOutSlopes, station), 4),
                        right_inside = Math.Round(InterpolateSlope(rightInSlopes, station), 4)
                    });
                }

                result = new
                {
                    alignment_name = alignmentName,
                    mode = "critical_stations",
                    curve_count = alignment.SuperelevationCurves.Count,
                    critical_station_count = criticalStations.Count,
                    critical_stations = criticalStations,
                    warnings
                };
            }

            return ToolResult.Ok(result);
        }

        /// <summary>
        /// Remove overlapping superelevation critical stations between consecutive curves.
        /// Ported from MahodCivilNet CAlignment.RemoveOverlaps().
        /// When two curves overlap, the transition between EndFullSuper of one curve
        /// and BeginFullSuper of the next produces duplicate/conflicting stations.
        /// This method removes the overlapping region, keeping the last valid station.
        /// </summary>
        private static void RemoveOverlaps(List<(double Station, double Slope)> slopes)
        {
            if (slopes.Count < 4) return;

            // Sort by station first
            slopes.Sort((a, b) => a.Station.CompareTo(b.Station));

            // Find and remove overlapping regions
            var cleaned = new List<(double Station, double Slope)>();
            double lastStation = double.MinValue;

            for (int i = 0; i < slopes.Count; i++)
            {
                if (slopes[i].Station >= lastStation - 0.01) // Allow tiny tolerance
                {
                    cleaned.Add(slopes[i]);
                    lastStation = slopes[i].Station;
                }
                // Skip duplicate/overlapping stations
            }

            // Remove exact duplicate stations (keep last value)
            var deduplicated = new List<(double Station, double Slope)>();
            for (int i = 0; i < cleaned.Count; i++)
            {
                if (i == cleaned.Count - 1 || Math.Abs(cleaned[i].Station - cleaned[i + 1].Station) > 0.01)
                {
                    deduplicated.Add(cleaned[i]);
                }
            }

            slopes.Clear();
            slopes.AddRange(deduplicated);
        }

        /// <summary>
        /// Linear interpolation of slope at a given station.
        /// </summary>
        private static double InterpolateSlope(List<(double Station, double Slope)> slopes, double station)
        {
            if (slopes.Count == 0) return 0;
            if (slopes.Count == 1) return slopes[0].Slope;
            if (station <= slopes[0].Station) return slopes[0].Slope;
            if (station >= slopes[slopes.Count - 1].Station) return slopes[slopes.Count - 1].Slope;

            for (int i = 1; i < slopes.Count; i++)
            {
                if (station <= slopes[i].Station)
                {
                    return GeometryHelper.InterpolateByStation(
                        slopes[i - 1].Station, slopes[i - 1].Slope,
                        slopes[i].Station, slopes[i].Slope,
                        station);
                }
            }
            return slopes[slopes.Count - 1].Slope;
        }
    }
}
