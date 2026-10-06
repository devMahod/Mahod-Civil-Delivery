using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Samples a corridor cross-section at an arbitrary station by intersecting 3D feature lines.
    /// More flexible than assembly-based sampling — works at any station, not just assembly stations.
    /// </summary>
    public class SampleCrossSectionAtStationTool : DrawingToolBase
    {
        public override string Name => "sample_cross_section_at_station";
        public override string Description =>
            "Samples a corridor cross-section at an arbitrary station by intersecting 3D feature lines. " +
            "More flexible than assembly-based sampling - works at any station, not just assembly stations.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var station = GetDoubleParam(parameters, "station");
            var baselineName = GetStringParam(parameters, "baseline_name");

            if (!station.HasValue)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Parameter 'station' is required");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the corridor
            Autodesk.Civil.DatabaseServices.Corridor? corridor = null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                if (obj != null && obj.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridor = obj;
                    break;
                }
            }

            if (corridor == null)
                return ToolResult.NotFound("Corridor", corridorName);

            // Find baseline (by name or first)
            Baseline? baseline = null;
            foreach (Baseline b in corridor.Baselines)
            {
                if (!string.IsNullOrEmpty(baselineName))
                {
                    if (b.Name.Equals(baselineName, StringComparison.OrdinalIgnoreCase))
                    {
                        baseline = b;
                        break;
                    }
                }
                else
                {
                    baseline = b;
                    break;
                }
            }

            if (baseline == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    string.IsNullOrEmpty(baselineName)
                        ? "No baseline found in corridor"
                        : $"Baseline '{baselineName}' not found in corridor");

            double targetStation = station.Value;
            if (targetStation < baseline.StartStation - 0.5 || targetStation > baseline.EndStation + 0.5)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Station {targetStation:F2} is outside corridor range [{baseline.StartStation:F2} - {baseline.EndStation:F2}]");

            // Get baseline alignment for offset computation and direction
            CivilAlignment? baselineAlignment = null;
            if (!baseline.AlignmentId.IsNull)
            {
                try
                {
                    baselineAlignment = tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) as CivilAlignment;
                }
                catch { }
            }

            if (baselineAlignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Could not access baseline alignment");

            try
            {
                // Get alignment direction at target station to define perpendicular section line
                double easting = 0, northing = 0;
                baselineAlignment.PointLocation(targetStation, 0, ref easting, ref northing);

                // Get direction by sampling a tiny offset along the alignment
                double delta = 0.1;
                double e2 = 0, n2 = 0;
                double sampleSta = targetStation + delta;
                if (sampleSta > baselineAlignment.EndingStation)
                    sampleSta = targetStation - delta;

                baselineAlignment.PointLocation(sampleSta, 0, ref e2, ref n2);

                double dx = e2 - easting;
                double dy = n2 - northing;
                if (sampleSta < targetStation) { dx = -dx; dy = -dy; }

                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-10)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Could not determine alignment direction at station");

                // Perpendicular direction (rotate 90 degrees)
                double perpX = -dy / len;
                double perpY = dx / len;

                // Section line extends 200m each side (generous)
                double extent = 200.0;
                double secX1 = easting - perpX * extent;
                double secY1 = northing - perpY * extent;
                double secX2 = easting + perpX * extent;
                double secY2 = northing + perpY * extent;

                // Extract all corridor feature line 3D points (same reflection pattern as SampleCrossSectionsTool)
                var featureLineSegments = new List<(string Code, double X1, double Y1, double Z1, double X2, double Y2, double Z2)>();

                var mainFLProp = baseline.GetType().GetProperty("MainBaselineFeatureLines");
                if (mainFLProp != null)
                {
                    var baselineFL = mainFLProp.GetValue(baseline);
                    if (baselineFL != null)
                    {
                        var mapProp = baselineFL.GetType().GetProperty("FeatureLineCollectionMap");
                        var map = mapProp?.GetValue(baselineFL) as System.Collections.IEnumerable;

                        if (map != null)
                        {
                            foreach (var flCollection in map)
                            {
                                ct.ThrowIfCancellationRequested();

                                var flcEnum = flCollection as System.Collections.IEnumerable;
                                if (flcEnum == null) continue;

                                foreach (var fl in flcEnum)
                                {
                                    string code = fl.GetType().GetProperty("CodeName")?.GetValue(fl)?.ToString() ?? "";
                                    var ptsProp = fl.GetType().GetProperty("FeatureLinePoints");
                                    var ptsRaw = ptsProp?.GetValue(fl) as System.Collections.IEnumerable;
                                    if (ptsRaw == null) continue;

                                    // Collect points for this feature line
                                    var pts = new List<(double X, double Y, double Z)>();
                                    foreach (var pt in ptsRaw)
                                    {
                                        try
                                        {
                                            var ptType = pt.GetType();
                                            var xyzProp = ptType.GetProperty("XYZ");
                                            if (xyzProp == null) continue;
                                            var xyz = xyzProp.GetValue(pt);
                                            if (xyz == null) continue;

                                            var xyzType = xyz.GetType();
                                            double ptX = Convert.ToDouble(xyzType.GetProperty("X")?.GetValue(xyz) ?? 0);
                                            double ptY = Convert.ToDouble(xyzType.GetProperty("Y")?.GetValue(xyz) ?? 0);
                                            double ptZ = Convert.ToDouble(xyzType.GetProperty("Z")?.GetValue(xyz) ?? 0);
                                            pts.Add((ptX, ptY, ptZ));
                                        }
                                        catch { }
                                    }

                                    // Build segments from consecutive points
                                    for (int i = 0; i < pts.Count - 1; i++)
                                    {
                                        featureLineSegments.Add((code,
                                            pts[i].X, pts[i].Y, pts[i].Z,
                                            pts[i + 1].X, pts[i + 1].Y, pts[i + 1].Z));
                                    }
                                }
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] SampleCrossSectionAtStation: {featureLineSegments.Count} segments, station {targetStation:F2}");

                // Intersect section line with all feature line segments
                var intersections = new List<(string Code, double X, double Y, double Z, double Offset)>();

                foreach (var (code, x1, y1, z1, x2, y2, z2) in featureLineSegments)
                {
                    ct.ThrowIfCancellationRequested();

                    if (GeometryHelper.SegmentIntersection3D(
                        secX1, secY1, secX2, secY2,
                        x1, y1, z1, x2, y2, z2,
                        out double ix, out double iy, out double iz))
                    {
                        // Compute offset from baseline
                        double sta = 0, offset = 0;
                        try
                        {
                            baselineAlignment.StationOffset(ix, iy, ref sta, ref offset);
                        }
                        catch { continue; }

                        intersections.Add((code, ix, iy, iz, offset));
                    }
                }

                // Deduplicate: keep one point per code per side (closest to section line station)
                var deduplicated = intersections
                    .GroupBy(p => $"{p.Code}|{(p.Offset >= 0 ? "R" : "L")}")
                    .Select(g => g.OrderBy(p => Math.Abs(p.Offset)).First())
                    .OrderBy(p => p.Offset)
                    .ToList();

                var points = deduplicated.Select(p => new
                {
                    offset = Math.Round(p.Offset, 3),
                    elevation = Math.Round(p.Z, 3),
                    code = p.Code,
                    x = Math.Round(p.X, 3),
                    y = Math.Round(p.Y, 3)
                }).ToList();

                double leftWidth = 0, rightWidth = 0;
                if (points.Count > 0)
                {
                    var leftmost = points.Where(p => p.offset < 0).MinBy(p => p.offset);
                    var rightmost = points.Where(p => p.offset > 0).MaxBy(p => p.offset);
                    leftWidth = leftmost != null ? Math.Abs(leftmost.offset) : 0;
                    rightWidth = rightmost?.offset ?? 0;
                }

                return await Task.FromResult(ToolResult.Ok(new
                {
                    corridor_name = corridor.Name,
                    baseline_name = baseline.Name,
                    station = Math.Round(targetStation, 3),
                    center_x = Math.Round(easting, 3),
                    center_y = Math.Round(northing, 3),
                    point_count = points.Count,
                    left_width = Math.Round(leftWidth, 3),
                    right_width = Math.Round(rightWidth, 3),
                    total_width = Math.Round(leftWidth + rightWidth, 3),
                    points
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to sample cross-section at station: {ex.Message}");
            }
        }
    }
}
