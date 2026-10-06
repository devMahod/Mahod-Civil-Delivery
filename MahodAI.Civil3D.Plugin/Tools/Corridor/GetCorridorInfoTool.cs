using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Gets detailed information about a corridor including lane widths and cross-slopes.
    /// Extracts actual geometry from corridor cross-sections.
    /// </summary>
    public class GetCorridorInfoTool : DrawingToolBase
    {
        public override string Name => "get_corridor_info";
        public override string Description => "Gets corridor structure, lane widths, cross-slopes, and shoulder data from cross-section geometry.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // Subassembly name patterns for lane detection
        private static readonly string[] LanePatterns = {
            "lane", "נתיב", "pave", "travel", "etw", "ltw",
            "carriageway", "roadway", "driving", "נסיעה"
        };

        private static readonly string[] ShoulderPatterns = {
            "shoulder", "שוליים", "curb", "gutter", "median",
            "מפרדה", "edge", "שפה"
        };

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var includeRegions = GetBoolParam(parameters, "include_regions", true);
            var includeSurfaces = GetBoolParam(parameters, "include_surfaces", false);

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

            var result = new CorridorInfoResult
            {
                Name = corridor.Name,
                Description = corridor.Description
            };

            // Get baselines with regions and lane widths
            result.Baselines = new List<BaselineInfo>();
            foreach (Baseline baseline in corridor.Baselines)
            {
                ct.ThrowIfCancellationRequested();

                var baselineInfo = new BaselineInfo
                {
                    Name = baseline.Name,
                    StartStation = baseline.StartStation,
                    EndStation = baseline.EndStation
                };

                // Get alignment and profile
                Autodesk.Civil.DatabaseServices.Alignment? baselineAlignment = null;
                try
                {
                    if (!baseline.AlignmentId.IsNull)
                    {
                        baselineAlignment = tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                        baselineInfo.AlignmentName = baselineAlignment?.Name;
                    }
                    if (!baseline.ProfileId.IsNull)
                    {
                        var profile = tr.GetObject(baseline.ProfileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                        baselineInfo.ProfileName = profile?.Name;
                    }
                }
                catch { }

                // Get regions with lane widths
                if (includeRegions)
                {
                    baselineInfo.Regions = new List<RegionInfo>();
                    foreach (BaselineRegion region in baseline.BaselineRegions)
                    {
                        ct.ThrowIfCancellationRequested();

                        var regionInfo = new RegionInfo
                        {
                            Name = region.Name,
                            StartStation = region.StartStation,
                            EndStation = region.EndStation
                        };

                        // Get assembly name
                        try
                        {
                            if (!region.AssemblyId.IsNull)
                            {
                                var assembly = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Assembly;
                                regionInfo.AssemblyName = assembly?.Name;
                            }
                        }
                        catch { }

                        // Extract lane widths from cross-section data
                        try
                        {
                            ExtractCrossSectionData(region, regionInfo, baselineAlignment, ct, tr);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Cross-section extraction failed for region {region.Name}: {ex.Message}");
                        }

                        if (regionInfo.LaneWidths == null || regionInfo.LaneWidths.Count == 0)
                            regionInfo.LaneWidthsNote = "Lane widths unavailable — use get_corridor_cross_section tool for detailed cross-section geometry.";

                        baselineInfo.Regions.Add(regionInfo);
                    }
                }

                result.Baselines.Add(baselineInfo);
            }

            // Aggregate lane widths across all regions for summary
            var allLanes = new List<LaneWidthInfo>();
            if (result.Baselines != null)
            {
                foreach (var bl in result.Baselines)
                {
                    if (bl.Regions == null) continue;
                    foreach (var reg in bl.Regions)
                    {
                        if (reg.LaneWidths != null)
                            allLanes.AddRange(reg.LaneWidths);
                    }
                }
            }
            result.LaneWidths = allLanes;

            // Get corridor surfaces
            if (includeSurfaces)
            {
                result.Surfaces = new List<CorridorSurfaceInfo>();
                try
                {
                    foreach (CorridorSurface surface in corridor.CorridorSurfaces)
                    {
                        result.Surfaces.Add(new CorridorSurfaceInfo
                        {
                            Name = surface.Name,
                            SurfaceId = surface.SurfaceId.ToString()
                        });
                    }
                }
                catch { }
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Extract lane widths and cross-slopes from corridor cross-section data using reflection.
        /// Compatible across Civil 3D API versions with multiple fallback strategies.
        /// </summary>
        private void ExtractCrossSectionData(
            BaselineRegion region,
            RegionInfo regionInfo,
            Autodesk.Civil.DatabaseServices.Alignment? baselineAlignment,
            CancellationToken ct,
            Transaction tr)
        {
            var sortedStations = region.SortedStations();
            if (sortedStations == null || sortedStations.Length == 0)
                return;

            // Sample at mid-point of region for representative cross-section
            int midIdx = sortedStations.Length / 2;
            double station = sortedStations[midIdx];

            var appliedAssemblies = region.AppliedAssemblies;
            if (appliedAssemblies == null)
                return;

            // Get applied assembly at the sample station
            object? appliedAssembly = null;

            // Method 1: Enumerate and pick middle item
            try
            {
                var enumerator = appliedAssemblies as System.Collections.IEnumerable;
                if (enumerator != null)
                {
                    int i = 0;
                    foreach (var item in enumerator)
                    {
                        if (i == midIdx) { appliedAssembly = item; break; }
                        i++;
                        if (i > midIdx + 1) break;
                    }
                }
            }
            catch { }

            // Method 2: Try GetItemAt
            if (appliedAssembly == null)
            {
                try
                {
                    var getItemAt = appliedAssemblies.GetType().GetMethod("GetItemAt");
                    if (getItemAt != null)
                    {
                        var paramInfo = getItemAt.GetParameters();
                        if (paramInfo.Length == 1 && paramInfo[0].ParameterType == typeof(double))
                            appliedAssembly = getItemAt.Invoke(appliedAssemblies, new object[] { station });
                        else if (paramInfo.Length == 1 && paramInfo[0].ParameterType == typeof(int))
                            appliedAssembly = getItemAt.Invoke(appliedAssemblies, new object[] { midIdx });
                    }
                }
                catch { }
            }

            if (appliedAssembly == null)
                return;

            // Report ALL subassemblies with their widths and slopes.
            var allSubassemblies = new List<LaneWidthInfo>();
            double leftWidth = 0, rightWidth = 0;

            try
            {
                // Civil 3D 2026: GetAppliedSubassemblies() is a METHOD, not a property
                System.Collections.IEnumerable? appliedSubs = null;

                // Try method first (Civil 3D 2026+)
                var getSubsMethod = appliedAssembly.GetType().GetMethod("GetAppliedSubassemblies");
                if (getSubsMethod != null)
                {
                    appliedSubs = getSubsMethod.Invoke(appliedAssembly, null) as System.Collections.IEnumerable;
                }

                // Fallback: try property (older Civil 3D versions)
                if (appliedSubs == null)
                {
                    var subassembliesProp = appliedAssembly.GetType().GetProperty("AppliedSubassemblies");
                    if (subassembliesProp != null)
                    {
                        appliedSubs = subassembliesProp.GetValue(appliedAssembly) as System.Collections.IEnumerable;
                    }
                }

                if (appliedSubs == null)
                    return;

                int subCount = 0;
                foreach (var appliedSub in appliedSubs)
                {
                    ct.ThrowIfCancellationRequested();
                    subCount++;

                    try
                    {
                        // Get subassembly name (uses expanded resolution chain)
                        string subName = CorridorReflectionHelper.ResolveSubassemblyName(appliedSub, subCount, tr);

                        // Get origin offset to determine side
                        // OriginStationOffsetElevationToBaseline returns Point3d: X=Station, Y=Offset, Z=Elevation
                        double offset = 0;
                        double originElevation = 0;
                        var originProp = appliedSub.GetType().GetProperty("OriginStationOffsetElevationToBaseline");
                        if (originProp != null)
                        {
                            var origin = originProp.GetValue(appliedSub);
                            if (origin != null)
                            {
                                var yProp = origin.GetType().GetProperty("Y");
                                var zProp = origin.GetType().GetProperty("Z");
                                if (yProp != null)
                                {
                                    offset = Convert.ToDouble(yProp.GetValue(origin));
                                    originElevation = zProp != null ? Convert.ToDouble(zProp.GetValue(origin)) : 0;
                                }
                                else
                                {
                                    // Fallback: try Offset/Elevation property names
                                    var offsetProp = origin.GetType().GetProperty("Offset");
                                    if (offsetProp != null)
                                        offset = Convert.ToDouble(offsetProp.GetValue(origin));
                                    var elevProp = origin.GetType().GetProperty("Elevation");
                                    if (elevProp != null)
                                        originElevation = Convert.ToDouble(elevProp.GetValue(origin));
                                }
                            }
                        }

                        string side = offset >= 0 ? "Right" : "Left";

                        // Process links to get offsets and widths
                        double maxLinkAbsOffset = Math.Abs(offset);
                        double? outermostElevation = null;

                        // Get links collection — try property then method fallbacks
                        System.Collections.IEnumerable? links = null;
                        var linksProp = appliedSub.GetType().GetProperty("Links")
                                      ?? appliedSub.GetType().GetProperty("CalculatedLinks");
                        if (linksProp != null)
                        {
                            links = linksProp.GetValue(appliedSub) as System.Collections.IEnumerable;
                        }
                        // Method fallback for Civil 3D 2026
                        if (links == null)
                        {
                            var getLinksMethod = appliedSub.GetType().GetMethod("GetLinks")
                                              ?? appliedSub.GetType().GetMethod("GetCalculatedLinks");
                            if (getLinksMethod != null)
                                links = getLinksMethod.Invoke(appliedSub, null) as System.Collections.IEnumerable;
                        }

                        if (links != null)
                        {
                            foreach (var link in links)
                            {
                                try
                                {

                                    // Get points — try property, then enumerate
                                    var pointsProp = link.GetType().GetProperty("CalculatedPoints")
                                                    ?? link.GetType().GetProperty("Points");
                                    if (pointsProp == null) continue;

                                    var pointsRaw = pointsProp.GetValue(link);
                                    if (pointsRaw == null) continue;

                                    // Each link is a line segment between exactly 2 points:
                                    // CalculatedPoints[0] = start, CalculatedPoints[1] = end.
                                    // Use IList indexing to get both points for proper width/slope calc.
                                    object? point0 = null;
                                    object? point1 = null;
                                    int pointCount = 0;

                                    if (pointsRaw is System.Collections.IList pointsList && pointsList.Count >= 2)
                                    {
                                        pointCount = pointsList.Count;
                                        point0 = pointsList[0];
                                        point1 = pointsList[1];
                                    }
                                    else if (pointsRaw is System.Collections.IEnumerable pointsEnum)
                                    {
                                        foreach (var p in pointsEnum)
                                        {
                                            if (pointCount == 0) point0 = p;
                                            else if (pointCount == 1) point1 = p;
                                            pointCount++;
                                        }
                                    }

                                    if (point0 == null || pointCount == 0) continue;

                                    // Helper: extract offset and elevation from a calculated point
                                    bool ExtractPointOffsetElev(object point, out double ptOffset, out double ptElev)
                                    {
                                        ptOffset = 0;
                                        ptElev = 0;

                                        var soeePropInner = point.GetType().GetProperty("StationOffsetElevationToBaseline");
                                        if (soeePropInner != null)
                                        {
                                            var soee = soeePropInner.GetValue(point);
                                            if (soee != null)
                                            {
                                                var yP = soee.GetType().GetProperty("Y");
                                                var zP = soee.GetType().GetProperty("Z");
                                                if (yP != null)
                                                {
                                                    ptOffset = Convert.ToDouble(yP.GetValue(soee));
                                                    ptElev = zP != null ? Convert.ToDouble(zP.GetValue(soee)) : 0;
                                                    return true;
                                                }
                                                else
                                                {
                                                    var offP = soee.GetType().GetProperty("Offset");
                                                    if (offP != null)
                                                    {
                                                        ptOffset = Convert.ToDouble(offP.GetValue(soee));
                                                        var elvP = soee.GetType().GetProperty("Elevation");
                                                        ptElev = elvP != null ? Convert.ToDouble(elvP.GetValue(soee)) : 0;
                                                        return true;
                                                    }
                                                }
                                            }
                                        }

                                        // Fallback: read XYZ property and compute offset via baseline alignment
                                        var xyzPropInner = point.GetType().GetProperty("XYZ");
                                        if (xyzPropInner != null && baselineAlignment != null)
                                        {
                                            var xyz = xyzPropInner.GetValue(point);
                                            if (xyz != null)
                                            {
                                                var xP = xyz.GetType().GetProperty("X");
                                                var yP = xyz.GetType().GetProperty("Y");
                                                var zP = xyz.GetType().GetProperty("Z");
                                                if (xP != null && yP != null)
                                                {
                                                    double px = Convert.ToDouble(xP.GetValue(xyz));
                                                    double py = Convert.ToDouble(yP.GetValue(xyz));
                                                    ptElev = zP != null ? Convert.ToDouble(zP.GetValue(xyz)) : 0;
                                                    try
                                                    {
                                                        double dummySta = 0;
                                                        baselineAlignment.StationOffset(px, py, ref dummySta, ref ptOffset);
                                                        return true;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }

                                        return false;
                                    }

                                    // Extract offset/elevation from both link points
                                    if (!ExtractPointOffsetElev(point0, out double offset0, out double elev0)) continue;

                                    double linkOffset;
                                    double linkElev;

                                    if (point1 != null && ExtractPointOffsetElev(point1, out double offset1, out double elev1))
                                    {
                                        // Use both points: width = offset difference, pick outermost for tracking
                                        if (Math.Abs(offset1) >= Math.Abs(offset0))
                                        {
                                            linkOffset = offset1;
                                            linkElev = elev1;
                                        }
                                        else
                                        {
                                            linkOffset = offset0;
                                            linkElev = elev0;
                                        }
                                    }
                                    else
                                    {
                                        // Single point fallback
                                        linkOffset = offset0;
                                        linkElev = elev0;
                                    }

                                    double absOff = Math.Abs(linkOffset);
                                    if (absOff > maxLinkAbsOffset)
                                    {
                                        maxLinkAbsOffset = absOff;
                                        outermostElevation = linkElev;
                                    }

                                    // Track overall corridor widths
                                    if (linkOffset >= 0 && absOff > rightWidth) rightWidth = absOff;
                                    else if (linkOffset < 0 && absOff > leftWidth) leftWidth = absOff;
                                }
                                catch (Exception linkEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"    Link point extraction failed: {linkEx.GetType().Name}: {linkEx.Message}");
                                }
                            }
                        }

                        // Compute width and slope for this subassembly
                        double originAbsOffset = Math.Abs(offset);
                        double subWidth = maxLinkAbsOffset - originAbsOffset;
                        double? subSlope = null;

                        if (subWidth > 0.01 && outermostElevation.HasValue)
                        {
                            double elevDiff = outermostElevation.Value - originElevation;
                            subSlope = Math.Round((elevDiff / subWidth) * 100.0, 2);
                        }

                        // Report ALL subassemblies with measurable width
                        if (subWidth > 0.01)
                        {
                            string subNameLower = subName.ToLowerInvariant();
                            string category = "other";
                            if (IsLaneSubassembly(subNameLower))
                                category = "lane";
                            else if (IsShoulderSubassembly(subNameLower))
                                category = "shoulder";

                            allSubassemblies.Add(new LaneWidthInfo
                            {
                                Side = side,
                                Width = Math.Round(subWidth, 3),
                                SlopePercent = subSlope,
                                SubassemblyName = subName,
                                Category = category
                            });
                        }
                    }
                    catch (Exception subEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Subassembly extraction error: {subEx.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"Corridor cross-section at station {station}: {subCount} subassemblies, " +
                    $"{allSubassemblies.Count} with width, L={leftWidth:F2} R={rightWidth:F2}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractCrossSectionData error: {ex.Message}");
            }

            regionInfo.LaneWidths = allSubassemblies;
            regionInfo.LeftWidth = Math.Round(leftWidth, 3);
            regionInfo.RightWidth = Math.Round(rightWidth, 3);
            regionInfo.TotalWidth = Math.Round(leftWidth + rightWidth, 3);
            regionInfo.SampleStation = station;
        }

        private static bool IsLaneSubassembly(string nameLower)
        {
            return LanePatterns.Any(p => nameLower.Contains(p));
        }

        private static bool IsShoulderSubassembly(string nameLower)
        {
            return ShoulderPatterns.Any(p => nameLower.Contains(p));
        }

        private static T? GetReflectionProperty<T>(object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    if (value is T typedValue)
                        return typedValue;
                }
            }
            catch { }
            return default;
        }
    }

    #region Result Models

    public class CorridorInfoResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<BaselineInfo>? Baselines { get; set; }
        public List<LaneWidthInfo>? LaneWidths { get; set; }
        public List<CorridorSurfaceInfo>? Surfaces { get; set; }
    }

    public class BaselineInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? AlignmentName { get; set; }
        public string? ProfileName { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public List<RegionInfo>? Regions { get; set; }
    }

    public class RegionInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? AssemblyName { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double? SampleStation { get; set; }
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }
        public double TotalWidth { get; set; }
        public List<LaneWidthInfo>? LaneWidths { get; set; }
        public List<ShoulderWidthInfo>? ShoulderWidths { get; set; }
        public string? LaneWidthsNote { get; set; }
    }

    public class LaneWidthInfo
    {
        public string Side { get; set; } = string.Empty;
        public double Width { get; set; }
        public double? SlopePercent { get; set; }
        public string SubassemblyName { get; set; } = string.Empty;
        public string Category { get; set; } = "other"; // "lane", "shoulder", or "other"
    }

    public class ShoulderWidthInfo
    {
        public string Side { get; set; } = string.Empty;
        public double Width { get; set; }
        public string SubassemblyName { get; set; } = string.Empty;
    }

    public class CorridorSurfaceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string SurfaceId { get; set; } = string.Empty;
    }

    #endregion
}
