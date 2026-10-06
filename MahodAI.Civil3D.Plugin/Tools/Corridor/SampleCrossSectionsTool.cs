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
    /// Samples cross-sections from a corridor at regular intervals or specified stations.
    /// Returns offset/elevation/code data for each station, suitable for batch analysis.
    /// </summary>
    public class SampleCrossSectionsTool : DrawingToolBase
    {
        public override string Name => "sample_cross_sections";
        public override string Description =>
            "Samples cross-sections from a corridor at specified stations or regular intervals. " +
            "Returns offset and elevation data for each cross-section. Use for batch lane width, " +
            "shoulder width, and cross-slope analysis across the full corridor length.";
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
            var frequency = GetDoubleParam(parameters, "frequency") ?? 50.0;
            var maxStations = GetIntParam(parameters, "max_stations") ?? 100;

            // Parse optional explicit stations array
            double[]? explicitStations = null;
            if (parameters.TryGetProperty("stations", out var stationsEl) &&
                stationsEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<double>();
                foreach (var s in stationsEl.EnumerateArray())
                {
                    if (s.TryGetDouble(out double v)) list.Add(v);
                }
                if (list.Count > 0) explicitStations = list.ToArray();
            }

            if (frequency <= 0) frequency = 50.0;
            if (maxStations <= 0) maxStations = 100;
            if (maxStations > 500) maxStations = 500;

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

            // Find the first baseline
            Baseline? baseline = null;
            foreach (Baseline b in corridor.Baselines)
            {
                baseline = b;
                break;
            }

            if (baseline == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No baseline found in corridor");

            double startSta = baseline.StartStation;
            double endSta = baseline.EndStation;
            double totalLength = endSta - startSta;

            // Build station list
            List<double> stationsToSample;
            bool hasMore = false;

            if (explicitStations != null)
            {
                stationsToSample = explicitStations
                    .Where(s => s >= startSta - 0.5 && s <= endSta + 0.5)
                    .OrderBy(s => s)
                    .Take(maxStations)
                    .ToList();
                hasMore = explicitStations.Length > maxStations;
            }
            else
            {
                stationsToSample = new List<double>();
                // Walk up to (but not past) endSta - 0.5 to avoid sampling exactly
                // at the alignment endpoint, where the alignment tangent direction
                // is extrapolated and AppliedAssemblies may have been computed at
                // a different nearby station. Sampling at the exact end is what
                // produced the wide "flared" last cross-section.
                double clampedEnd = endSta - 0.5;
                if (clampedEnd <= startSta) clampedEnd = endSta; // very short alignment — fall back

                double sta = startSta;
                while (sta <= clampedEnd + 0.001)
                {
                    stationsToSample.Add(sta);
                    sta += frequency;
                }
                // Include clampedEnd as the final sample if the last tick is more
                // than 0.5 m away from it.
                if (stationsToSample.Count == 0 ||
                    Math.Abs(stationsToSample[^1] - clampedEnd) > 0.5)
                    stationsToSample.Add(clampedEnd);

                if (stationsToSample.Count > maxStations)
                {
                    stationsToSample = stationsToSample.Take(maxStations).ToList();
                    hasMore = true;
                }
            }

            // Get baseline alignment for offset computation
            Autodesk.Civil.DatabaseServices.Alignment? baselineAlignment = null;
            if (!baseline.AlignmentId.IsNull)
            {
                try
                {
                    baselineAlignment = tr.GetObject(baseline.AlignmentId, OpenMode.ForRead)
                        as Autodesk.Civil.DatabaseServices.Alignment;
                }
                catch { }
            }

            // Pre-fetch all corridor feature line points for fast station lookup.
            // Civil 3D 2026 hierarchy (from Autodesk API docs):
            //   baseline.MainBaselineFeatureLines           → BaselineFeatureLines (NOT iterable)
            //       .FeatureLineCollectionMap                → IEnumerable<FeatureLineCollection>
            //           foreach FeatureLineCollection        → IEnumerable<CorridorFeatureLine>
            //               .CodeName                       → string
            //               .FeatureLinePoints              → IEnumerable<FeatureLinePoint>
            //                   .Station                    → double
            //                   .XYZ                        → Point3d
            var flinePoints = new List<(string Code, double Station, double X, double Y, double Z)>();

            var mainFLProp = baseline.GetType().GetProperty("MainBaselineFeatureLines");
            if (mainFLProp != null)
            {
                var baselineFL = mainFLProp.GetValue(baseline);
                if (baselineFL != null)
                {
                    // Key: access FeatureLineCollectionMap, NOT BaselineFeatureLines directly
                    var mapProp = baselineFL.GetType().GetProperty("FeatureLineCollectionMap");
                    var map = mapProp?.GetValue(baselineFL) as System.Collections.IEnumerable;

                    if (map != null)
                    {
                        foreach (var flCollection in map) // FeatureLineCollection
                        {
                            ct.ThrowIfCancellationRequested();

                            // Each FeatureLineCollection is IEnumerable<CorridorFeatureLine>
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
                                    try
                                    {
                                        var ptType = pt.GetType();

                                        // Get station
                                        double ptSta = 0;
                                        var staProp = ptType.GetProperty("Station");
                                        if (staProp != null)
                                            ptSta = Convert.ToDouble(staProp.GetValue(pt));

                                        // Get XYZ (Point3d)
                                        var xyzProp = ptType.GetProperty("XYZ");
                                        if (xyzProp == null) continue;
                                        var xyz = xyzProp.GetValue(pt);
                                        if (xyz == null) continue;

                                        var xyzType = xyz.GetType();
                                        double ptX = Convert.ToDouble(xyzType.GetProperty("X")?.GetValue(xyz) ?? 0);
                                        double ptY = Convert.ToDouble(xyzType.GetProperty("Y")?.GetValue(xyz) ?? 0);
                                        double ptZ = Convert.ToDouble(xyzType.GetProperty("Z")?.GetValue(xyz) ?? 0);

                                        flinePoints.Add((code, ptSta, ptX, ptY, ptZ));
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: FeatureLineCollectionMap property {(mapProp == null ? "not found" : "found but value is null/not IEnumerable")} on {baselineFL.GetType().FullName}");
                    }

                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: primary path extracted {flinePoints.Count} feature line points");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: MainBaselineFeatureLines property returned null");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: MainBaselineFeatureLines property not found on {baseline.GetType().FullName}");
            }

            // Fallback 1: Try CorridorFeatureLines on baseline (older API path)
            if (flinePoints.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: Trying fallback via CorridorFeatureLines");
                try
                {
                    var corrFLProp = baseline.GetType().GetProperty("CorridorFeatureLines");
                    var corrFL = corrFLProp?.GetValue(baseline) as System.Collections.IEnumerable;
                    if (corrFL != null)
                    {
                        foreach (var fl in corrFL)
                        {
                            ct.ThrowIfCancellationRequested();
                            string code = fl.GetType().GetProperty("CodeName")?.GetValue(fl)?.ToString() ?? "";

                            var ptsProp = fl.GetType().GetProperty("FeatureLinePoints");
                            var ptsRaw = ptsProp?.GetValue(fl) as System.Collections.IEnumerable;
                            if (ptsRaw == null) continue;

                            foreach (var pt in ptsRaw)
                            {
                                try
                                {
                                    var ptType = pt.GetType();
                                    double ptSta = 0;
                                    var staProp = ptType.GetProperty("Station");
                                    if (staProp != null)
                                        ptSta = Convert.ToDouble(staProp.GetValue(pt));

                                    var xyzProp = ptType.GetProperty("XYZ");
                                    if (xyzProp == null) continue;
                                    var xyz = xyzProp.GetValue(pt);
                                    if (xyz == null) continue;

                                    var xyzType = xyz.GetType();
                                    double ptX = Convert.ToDouble(xyzType.GetProperty("X")?.GetValue(xyz) ?? 0);
                                    double ptY = Convert.ToDouble(xyzType.GetProperty("Y")?.GetValue(xyz) ?? 0);
                                    double ptZ = Convert.ToDouble(xyzType.GetProperty("Z")?.GetValue(xyz) ?? 0);

                                    flinePoints.Add((code, ptSta, ptX, ptY, ptZ));
                                }
                                catch { }
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: CorridorFeatureLines fallback extracted {flinePoints.Count} points");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: CorridorFeatureLines {(corrFLProp == null ? "property not found" : "returned null")}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: CorridorFeatureLines fallback failed: {ex.Message}");
                }
            }

            // Fallback 2: Extract from AppliedAssemblies if feature lines still unavailable
            if (flinePoints.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: Feature lines unavailable, falling back to AppliedAssembly extraction");
                try
                {
                    foreach (BaselineRegion region in baseline.BaselineRegions)
                    {
                        ct.ThrowIfCancellationRequested();

                        var appliedAssemblies = region.AppliedAssemblies;
                        if (appliedAssemblies == null) continue;

                        var assemblyEnum = appliedAssemblies as System.Collections.IEnumerable;
                        if (assemblyEnum == null) continue;

                        foreach (var assembly in assemblyEnum)
                        {
                            ct.ThrowIfCancellationRequested();

                            // Get applied subassemblies — method first (Civil 3D 2026+), then property fallback
                            System.Collections.IEnumerable? appliedSubs = null;
                            var getSubsMethod = assembly.GetType().GetMethod("GetAppliedSubassemblies");
                            if (getSubsMethod != null)
                            {
                                appliedSubs = getSubsMethod.Invoke(assembly, null) as System.Collections.IEnumerable;
                            }
                            if (appliedSubs == null)
                            {
                                var subsProp = assembly.GetType().GetProperty("AppliedSubassemblies");
                                if (subsProp != null)
                                    appliedSubs = subsProp.GetValue(assembly) as System.Collections.IEnumerable;
                            }
                            if (appliedSubs == null) continue;

                            foreach (var appliedSub in appliedSubs)
                            {
                                string subName = "";
                                try
                                {
                                    subName = appliedSub.GetType().GetProperty("SubassemblyName")?.GetValue(appliedSub)?.ToString()
                                           ?? appliedSub.GetType().GetProperty("Name")?.GetValue(appliedSub)?.ToString()
                                           ?? "";
                                }
                                catch { }

                                // Get links — property then method fallback
                                System.Collections.IEnumerable? links = null;
                                var linksProp = appliedSub.GetType().GetProperty("Links")
                                              ?? appliedSub.GetType().GetProperty("CalculatedLinks");
                                if (linksProp != null)
                                    links = linksProp.GetValue(appliedSub) as System.Collections.IEnumerable;
                                if (links == null)
                                {
                                    var getLinksMethod = appliedSub.GetType().GetMethod("GetLinks")
                                                      ?? appliedSub.GetType().GetMethod("GetCalculatedLinks");
                                    if (getLinksMethod != null)
                                        links = getLinksMethod.Invoke(appliedSub, null) as System.Collections.IEnumerable;
                                }
                                if (links == null) continue;

                                foreach (var link in links)
                                {
                                    try
                                    {
                                        // Get corridor codes for this link
                                        string code = "";
                                        var codesProp = link.GetType().GetProperty("CorridorCodes");
                                        if (codesProp != null)
                                        {
                                            var codes = codesProp.GetValue(link) as System.Collections.IEnumerable;
                                            if (codes != null)
                                            {
                                                foreach (var c in codes) { code = c?.ToString() ?? ""; break; }
                                            }
                                        }
                                        if (string.IsNullOrEmpty(code)) code = subName;

                                        // Get calculated points
                                        var pointsProp = link.GetType().GetProperty("CalculatedPoints")
                                                        ?? link.GetType().GetProperty("Points");
                                        if (pointsProp == null) continue;
                                        var pointsRaw = pointsProp.GetValue(link) as System.Collections.IEnumerable;
                                        if (pointsRaw == null) continue;

                                        foreach (var pt in pointsRaw)
                                        {
                                            try
                                            {
                                                // StationOffsetElevationToBaseline: X=Station, Y=Offset, Z=Elevation
                                                var soeProp = pt.GetType().GetProperty("StationOffsetElevationToBaseline");
                                                if (soeProp == null) continue;
                                                var soe = soeProp.GetValue(pt);
                                                if (soe == null) continue;

                                                var soeType = soe.GetType();
                                                double ptSta = Convert.ToDouble(soeType.GetProperty("X")?.GetValue(soe) ?? 0);

                                                // Get XYZ for actual coordinates
                                                var xyzProp = pt.GetType().GetProperty("XYZ");
                                                if (xyzProp == null) continue;
                                                var xyz = xyzProp.GetValue(pt);
                                                if (xyz == null) continue;

                                                var xyzType = xyz.GetType();
                                                double ptX = Convert.ToDouble(xyzType.GetProperty("X")?.GetValue(xyz) ?? 0);
                                                double ptY = Convert.ToDouble(xyzType.GetProperty("Y")?.GetValue(xyz) ?? 0);
                                                double ptZ = Convert.ToDouble(xyzType.GetProperty("Z")?.GetValue(xyz) ?? 0);

                                                flinePoints.Add((code, ptSta, ptX, ptY, ptZ));
                                            }
                                            catch { }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: AppliedAssembly fallback failed: {ex.Message}");
                }
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleCrossSections: AppliedAssembly fallback extracted {flinePoints.Count} points");
            }

            // Sample each station
            var sampledStations = new List<SampledCrossSection>();

            foreach (double targetSta in stationsToSample)
            {
                ct.ThrowIfCancellationRequested();

                var points = new List<CrossSectionSamplePoint>();

                foreach (var (code, ptSta, ptX, ptY, ptZ) in flinePoints)
                {
                    if (Math.Abs(ptSta - targetSta) > 0.5) continue;

                    double offset = 0;
                    if (baselineAlignment != null)
                    {
                        try
                        {
                            double dummy = 0;
                            baselineAlignment.StationOffset(ptX, ptY, ref dummy, ref offset);
                        }
                        catch { }
                    }

                    points.Add(new CrossSectionSamplePoint
                    {
                        Offset = Math.Round(offset, 3),
                        Elevation = Math.Round(ptZ, 3),
                        Code = code
                    });
                }

                points.Sort((a, b) => a.Offset.CompareTo(b.Offset));

                double leftWidth = 0, rightWidth = 0;
                if (points.Count > 0)
                {
                    var leftmost = points.Where(p => p.Offset < 0).MinBy(p => p.Offset);
                    var rightmost = points.Where(p => p.Offset > 0).MaxBy(p => p.Offset);
                    leftWidth = leftmost != null ? Math.Abs(leftmost.Offset) : 0;
                    rightWidth = rightmost?.Offset ?? 0;
                }

                sampledStations.Add(new SampledCrossSection
                {
                    Station = Math.Round(targetSta, 3),
                    LeftWidth = Math.Round(leftWidth, 3),
                    RightWidth = Math.Round(rightWidth, 3),
                    TotalWidth = Math.Round(leftWidth + rightWidth, 3),
                    Points = points
                });
            }

            return await Task.FromResult(ToolResult.Ok(new
            {
                corridor_name = corridor.Name,
                baseline_name = baseline.Name,
                total_length = Math.Round(totalLength, 3),
                frequency_used = frequency,
                station_count = sampledStations.Count,
                has_more = hasMore,
                stations = sampledStations
            }));
        }

    }

    #region Result Models

    public class SampledCrossSection
    {
        public double Station { get; set; }
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }
        public double TotalWidth { get; set; }
        public List<CrossSectionSamplePoint> Points { get; set; } = new();
    }

    public class CrossSectionSamplePoint
    {
        public double Offset { get; set; }
        public double Elevation { get; set; }
        public string? Code { get; set; }
    }

    #endregion
}
