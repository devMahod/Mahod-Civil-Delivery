using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Calculates cut/fill earthwork volumes along a corridor.
    ///
    /// Uses trapezoidal area integration from Igor's CalcSectionVolumes:
    /// 1. For each cross-section, calculate cut and fill areas by comparing
    ///    corridor design surface elevation vs existing ground (base surface)
    /// 2. Between consecutive stations: volume = (area1 + area2) / 2 * distance
    /// 3. Handles zero-crossing (mixed cut/fill within a single segment)
    /// </summary>
    public class CalcVolumesTool : DrawingToolBase
    {
        public override string Name => "calc_volumes";
        public override string Description =>
            "Calculates earthwork cut/fill volumes along a corridor. " +
            "Returns per-station areas and cumulative volumes in cubic meters.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""corridor_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the corridor""
                },
                ""base_surface_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the existing ground surface. If omitted, uses first TIN surface.""
                },
                ""interval"": {
                    ""type"": ""number"",
                    ""description"": ""Station interval for volume calc in meters (default: 20)""
                }
            },
            ""required"": [""corridor_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var baseSurfaceName = GetStringParam(parameters, "base_surface_name");
            var interval = GetDoubleParam(parameters, "interval") ?? 20.0;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find corridor
            CivilDb.Corridor? corridor = null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Corridor;
                if (c != null && c.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridor = c;
                    break;
                }
            }

            if (corridor == null)
                return ToolResult.NotFound("Corridor", corridorName);

            // Find base surface
            TinSurface? baseSurface = null;
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                var surface = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                if (surface == null) continue;

                if (!string.IsNullOrEmpty(baseSurfaceName))
                {
                    if (surface.Name.Equals(baseSurfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        baseSurface = surface;
                        break;
                    }
                }
                else
                {
                    // Skip corridor-generated surfaces
                    if (surface.Name.Contains("_Top") || surface.Name.Contains("_Bot") ||
                        surface.Name.Contains("Corridor"))
                        continue;
                    baseSurface = surface;
                    baseSurfaceName = surface.Name;
                    break;
                }
            }

            if (baseSurface == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Base surface '{baseSurfaceName ?? "any"}' not found.");

            // Calculate volumes
            double totalCut = 0, totalFill = 0;
            var stationVolumes = new List<Dictionary<string, object>>();

            foreach (Baseline baseline in corridor.Baselines)
            {
                ct.ThrowIfCancellationRequested();

                foreach (BaselineRegion region in baseline.BaselineRegions)
                {
                    var assemblies = region.AppliedAssemblies;
                    if (assemblies == null || assemblies.Count == 0) continue;

                    double prevCutArea = 0, prevFillArea = 0;
                    double prevStation = double.NaN;

                    double lastSampledStation = double.NaN;
                    foreach (AppliedAssembly asm in assemblies)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Get station via reflection
                        double station = GetStation(asm);

                        // Honor interval — skip stations too close to last sampled
                        if (!double.IsNaN(lastSampledStation) &&
                            Math.Abs(station - lastSampledStation) < interval * 0.9)
                            continue;
                        lastSampledStation = station;

                        // Get all link points from this assembly
                        double cutArea = 0, fillArea = 0;

                        var appliedSubs = GetAppliedSubassemblies(asm);
                        if (appliedSubs == null) continue;

                        foreach (var sub in appliedSubs)
                        {
                            var links = GetLinks(sub);
                            if (links == null) continue;

                            foreach (var link in links)
                            {
                                var points = GetLinkPoints(link);
                                if (points.Count < 2) continue;

                                // For each pair of consecutive link points,
                                // compare design elevation with base surface
                                for (int i = 0; i < points.Count - 1; i++)
                                {
                                    var p1 = points[i];
                                    var p2 = points[i + 1];

                                    double egElev1 = 0, egElev2 = 0;
                                    try
                                    {
                                        egElev1 = baseSurface.FindElevationAtXY(p1.X, p1.Y);
                                        egElev2 = baseSurface.FindElevationAtXY(p2.X, p2.Y);
                                    }
                                    catch { continue; } // Point outside surface extent

                                    double dY1 = p1.Z - egElev1;
                                    double dY2 = p2.Z - egElev2;
                                    double dX = Math.Sqrt(
                                        Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));

                                    if (dX < 0.001) continue;

                                    // Trapezoidal area with zero-crossing (Igor's method)
                                    CalcTrapezoidArea(dY1, dY2, dX, ref cutArea, ref fillArea);
                                }
                            }
                        }

                        // Volume between this and previous station
                        if (!double.IsNaN(prevStation))
                        {
                            double dist = Math.Abs(station - prevStation);
                            if (dist > 0)
                            {
                                totalCut += (prevCutArea + cutArea) / 2 * dist;
                                totalFill += (prevFillArea + fillArea) / 2 * dist;
                            }
                        }

                        stationVolumes.Add(new Dictionary<string, object>
                        {
                            ["station"] = Math.Round(station, 1),
                            ["cut_area_m2"] = Math.Round(cutArea, 2),
                            ["fill_area_m2"] = Math.Round(fillArea, 2),
                            ["cumulative_cut_m3"] = Math.Round(totalCut, 1),
                            ["cumulative_fill_m3"] = Math.Round(totalFill, 1),
                        });

                        prevCutArea = cutArea;
                        prevFillArea = fillArea;
                        prevStation = station;
                    }
                }
            }

            double netVolume = totalFill - totalCut;

            // Trim station list for output (max 50 entries)
            var outputStations = stationVolumes.Count <= 50
                ? stationVolumes
                : stationVolumes
                    .Where((_, i) => i % Math.Max(1, stationVolumes.Count / 25) == 0)
                    .ToList();

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["corridor_name"] = corridorName,
                ["base_surface"] = baseSurfaceName!,
                ["total_cut_m3"] = Math.Round(totalCut, 1),
                ["total_fill_m3"] = Math.Round(totalFill, 1),
                ["net_volume_m3"] = Math.Round(netVolume, 1),
                ["balance"] = netVolume > 0 ? "excess_fill" : "excess_cut",
                ["station_count"] = stationVolumes.Count,
                ["stations"] = outputStations,
                ["message"] = $"Volumes for corridor '{corridorName}': " +
                    $"Cut={totalCut:N0} m³, Fill={totalFill:N0} m³, " +
                    $"Net={(netVolume > 0 ? "+" : "")}{netVolume:N0} m³ " +
                    $"({(netVolume > 0 ? "excess fill" : "excess cut")}). " +
                    $"Calculated at {stationVolumes.Count} stations."
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Trapezoidal area calculation with zero-crossing handling (Igor's method).
        /// </summary>
        private static void CalcTrapezoidArea(
            double dY1, double dY2, double dX, ref double cutArea, ref double fillArea)
        {
            if (dY1 >= 0 && dY2 >= 0)
            {
                fillArea += (dY1 + dY2) / 2 * dX;
            }
            else if (dY1 <= 0 && dY2 <= 0)
            {
                cutArea += (-dY1 + -dY2) / 2 * dX;
            }
            else if (dY1 >= 0 && dY2 < 0)
            {
                double dX1 = Math.Abs(dY1) * dX / (Math.Abs(dY1) + Math.Abs(dY2));
                fillArea += dX1 * dY1 / 2;
                cutArea += (dX - dX1) * (-dY2) / 2;
            }
            else // dY1 < 0 && dY2 >= 0
            {
                double dX1 = Math.Abs(dY1) * dX / (Math.Abs(dY1) + Math.Abs(dY2));
                cutArea += dX1 * (-dY1) / 2;
                fillArea += (dX - dX1) * dY2 / 2;
            }
        }

        // ── Reflection helpers ──

        private static double GetStation(AppliedAssembly asm)
        {
            try
            {
                var prop = asm.GetType().GetProperty("Station");
                if (prop != null)
                {
                    var val = prop.GetValue(asm);
                    if (val is double d) return d;
                }
            }
            catch { }
            return 0;
        }

        private static IEnumerable? GetAppliedSubassemblies(AppliedAssembly asm)
        {
            var method = asm.GetType().GetMethod("GetAppliedSubassemblies");
            if (method != null)
            {
                var result = method.Invoke(asm, null) as IEnumerable;
                if (result != null) return result;
            }
            var prop = asm.GetType().GetProperty("AppliedSubassemblies");
            return prop?.GetValue(asm) as IEnumerable;
        }

        private static IEnumerable? GetLinks(object appliedSub)
        {
            var prop = appliedSub.GetType().GetProperty("Links")
                     ?? appliedSub.GetType().GetProperty("CalculatedLinks");
            if (prop != null)
                return prop.GetValue(appliedSub) as IEnumerable;

            var method = appliedSub.GetType().GetMethod("GetLinks")
                       ?? appliedSub.GetType().GetMethod("GetCalculatedLinks");
            return method?.Invoke(appliedSub, null) as IEnumerable;
        }

        private static List<Point3d> GetLinkPoints(object link)
        {
            var points = new List<Point3d>();
            var pointsProp = link.GetType().GetProperty("CalculatedPoints")
                           ?? link.GetType().GetProperty("Points");
            if (pointsProp == null) return points;

            var raw = pointsProp.GetValue(link);
            if (raw is IEnumerable enumerable)
            {
                foreach (var pt in enumerable)
                {
                    var xyzProp = pt.GetType().GetProperty("XYZ");
                    if (xyzProp != null)
                    {
                        var xyz = xyzProp.GetValue(pt);
                        if (xyz is Point3d p3d) points.Add(p3d);
                    }
                }
            }
            return points;
        }
    }
}
