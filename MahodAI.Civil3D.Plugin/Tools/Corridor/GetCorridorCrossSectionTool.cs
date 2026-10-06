using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Gets cross section data at a specific station along a corridor.
    /// </summary>
    public class GetCorridorCrossSectionTool : DrawingToolBase
    {
        public override string Name => "get_corridor_cross_section";
        public override string Description => "Gets cross section data at a specific station along a corridor, including all points and codes.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

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
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Station parameter is required");
            }

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

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
            {
                return ToolResult.NotFound("Corridor", corridorName);
            }

            // Find the baseline
            Baseline? targetBaseline = null;
            foreach (Baseline baseline in corridor.Baselines)
            {
                if (string.IsNullOrEmpty(baselineName) ||
                    baseline.Name.Equals(baselineName, StringComparison.OrdinalIgnoreCase))
                {
                    targetBaseline = baseline;
                    break;
                }
            }

            if (targetBaseline == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ObjectNotFound, "No baseline found in corridor");
            }

            // Check station range
            if (station.Value < targetBaseline.StartStation || station.Value > targetBaseline.EndStation)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    $"Station {station.Value} is outside baseline range ({targetBaseline.StartStation} - {targetBaseline.EndStation})");
            }

            var result = new CrossSectionResult
            {
                CorridorName = corridor.Name,
                BaselineName = targetBaseline.Name,
                Station = station.Value,
                Points = new List<CrossSectionPoint>()
            };

            // Get the cross section data
            try
            {
                // Find the containing region and assembly name
                foreach (BaselineRegion region in targetBaseline.BaselineRegions)
                {
                    if (station.Value >= region.StartStation && station.Value <= region.EndStation)
                    {
                        result.RegionName = region.Name;

                        // Get assembly info
                        if (!region.AssemblyId.IsNull)
                        {
                            var assembly = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Assembly;
                            result.AssemblyName = assembly?.Name;
                        }

                        break;
                    }
                }

                // Extract cross-section points from corridor feature lines at the target station.
                // Use reflection since the exact property name varies by Civil 3D API version.
                Autodesk.Civil.DatabaseServices.Alignment? baselineAlignment = null;
                if (!targetBaseline.AlignmentId.IsNull)
                {
                    try { baselineAlignment = tr.GetObject(targetBaseline.AlignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment; }
                    catch { }
                }

                try
                {
                    // Strategy 1: Baseline.MainBaselineFeatureLines.FeatureLineCollectionMap (Civil 3D 2026+)
                    // Hierarchy: BaselineFeatureLines (NOT iterable) → .FeatureLineCollectionMap (iterable)
                    //   → foreach FeatureLineCollection (iterable) → foreach CorridorFeatureLine
                    //     → .CodeName, .FeatureLinePoints (iterable) → .Station, .XYZ
                    bool foundPoints = false;
                    var mainFLProp = targetBaseline.GetType().GetProperty("MainBaselineFeatureLines");
                    if (mainFLProp != null)
                    {
                        var baselineFL = mainFLProp.GetValue(targetBaseline);
                        if (baselineFL != null)
                        {
                            var mapProp = baselineFL.GetType().GetProperty("FeatureLineCollectionMap");
                            var map = mapProp?.GetValue(baselineFL) as System.Collections.IEnumerable;

                            if (map != null)
                            {
                                foreach (var flCollection in map) // FeatureLineCollection
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var flcEnum = flCollection as System.Collections.IEnumerable;
                                    if (flcEnum == null) continue;

                                    foreach (var fl in flcEnum) // CorridorFeatureLine
                                    {
                                        string code = fl.GetType().GetProperty("CodeName")?.GetValue(fl)?.ToString() ?? "";

                                        var ptsProp = fl.GetType().GetProperty("FeatureLinePoints");
                                        var ptsRaw = ptsProp?.GetValue(fl) as System.Collections.IEnumerable;
                                        if (ptsRaw == null) continue;

                                        foreach (var pt in ptsRaw) // FeatureLinePoint
                                        {
                                            ct.ThrowIfCancellationRequested();
                                            try
                                            {
                                                var ptType = pt.GetType();
                                                double ptSta = 0;
                                                var staProp = ptType.GetProperty("Station");
                                                if (staProp != null)
                                                    ptSta = Convert.ToDouble(staProp.GetValue(pt));

                                                if (Math.Abs(ptSta - station.Value) > 0.5) continue;

                                                var xyzProp = ptType.GetProperty("XYZ");
                                                if (xyzProp == null) continue;
                                                var xyz = xyzProp.GetValue(pt);
                                                if (xyz == null) continue;

                                                var xyzType = xyz.GetType();
                                                double ptX = Convert.ToDouble(xyzType.GetProperty("X")?.GetValue(xyz) ?? 0);
                                                double ptY = Convert.ToDouble(xyzType.GetProperty("Y")?.GetValue(xyz) ?? 0);
                                                double ptZ = Convert.ToDouble(xyzType.GetProperty("Z")?.GetValue(xyz) ?? 0);

                                                double ptOffset = 0;
                                                if (baselineAlignment != null)
                                                {
                                                    double dummy = 0;
                                                    baselineAlignment.StationOffset(ptX, ptY, ref dummy, ref ptOffset);
                                                }

                                                result.Points.Add(new CrossSectionPoint
                                                {
                                                    Offset = Math.Round(ptOffset, 3),
                                                    Elevation = Math.Round(ptZ, 3),
                                                    Code = code
                                                });
                                                foundPoints = true;
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Strategy 2: Corridor-level CorridorFeatureLines (older Civil 3D)
                    if (!foundPoints)
                    {
                        var corridorType = corridor.GetType();
                        var flineProp = corridorType.GetProperty("CorridorFeatureLines")
                                     ?? corridorType.GetProperty("FeatureLines");
                        var flineCollection = flineProp?.GetValue(corridor) as System.Collections.IEnumerable;

                        if (flineCollection != null)
                        {
                            foreach (var cfl in flineCollection)
                            {
                                ct.ThrowIfCancellationRequested();
                                var cflType = cfl.GetType();
                                var codeProp = cflType.GetProperty("CodeName") ?? cflType.GetProperty("Name");
                                var pointsProp = cflType.GetProperty("FeatureLinePoints");
                                if (pointsProp == null) continue;

                                var pts = pointsProp.GetValue(cfl) as System.Collections.IEnumerable;
                                if (pts == null) continue;

                                string? code = codeProp?.GetValue(cfl)?.ToString();

                                foreach (var pt in pts)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    try
                                    {
                                        var ptType = pt.GetType();
                                        var staProp = ptType.GetProperty("Station");
                                        var pointProp = ptType.GetProperty("Point");
                                        if (staProp == null || pointProp == null) continue;

                                        double ptSta = Convert.ToDouble(staProp.GetValue(pt));
                                        if (Math.Abs(ptSta - station.Value) > 0.5) continue;

                                        var p3d = pointProp.GetValue(pt);
                                        if (p3d == null) continue;
                                        var p3dType = p3d.GetType();
                                        double ptX = Convert.ToDouble(p3dType.GetProperty("X")?.GetValue(p3d) ?? 0);
                                        double ptY = Convert.ToDouble(p3dType.GetProperty("Y")?.GetValue(p3d) ?? 0);
                                        double ptZ = Convert.ToDouble(p3dType.GetProperty("Z")?.GetValue(p3d) ?? 0);

                                        double ptOffset = 0;
                                        if (baselineAlignment != null)
                                        {
                                            double dummy = 0;
                                            baselineAlignment.StationOffset(ptX, ptY, ref dummy, ref ptOffset);
                                        }

                                        result.Points.Add(new CrossSectionPoint
                                        {
                                            Offset = Math.Round(ptOffset, 3),
                                            Elevation = Math.Round(ptZ, 3),
                                            Code = code
                                        });
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    // (no points found is not necessarily an error — station may be between feature line sample points)
                }
                catch (Exception fex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] CorridorFeatureLine extraction: {fex.Message}");
                }

                // Sort points by offset
                result.Points.Sort((a, b) => a.Offset.CompareTo(b.Offset));

                // Calculate width
                if (result.Points.Count > 0)
                {
                    result.LeftWidth = Math.Abs(result.Points.Where(p => p.Offset < 0).MinBy(p => p.Offset)?.Offset ?? 0);
                    result.RightWidth = result.Points.Where(p => p.Offset > 0).MaxBy(p => p.Offset)?.Offset ?? 0;
                    result.TotalWidth = result.LeftWidth + result.RightWidth;
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, $"Failed to extract cross section: {ex.Message}");
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class CrossSectionResult
    {
        public string CorridorName { get; set; } = string.Empty;
        public string BaselineName { get; set; } = string.Empty;
        public double Station { get; set; }
        public string? RegionName { get; set; }
        public string? AssemblyName { get; set; }
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }
        public double TotalWidth { get; set; }
        public List<CrossSectionPoint> Points { get; set; } = new();
    }

    public class CrossSectionPoint
    {
        public double Offset { get; set; }
        public double Elevation { get; set; }
        public string? Code { get; set; }
        public string? LinkName { get; set; }
    }

    #endregion
}
