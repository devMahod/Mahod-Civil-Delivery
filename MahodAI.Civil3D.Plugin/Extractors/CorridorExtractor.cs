using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Models;

namespace MahodAI.Civil3D.Plugin.Extractors
{
    /// <summary>
    /// Extracts comprehensive corridor data from Civil 3D drawings.
    /// Lane widths are computed from subassembly link offsets. Max corridor widths
    /// (LeftWidth/RightWidth) are used for ROW encroachment checks.
    /// </summary>
    public class CorridorExtractor
    {
        private const int MaxCrossSectionSamples = 10;

        /// <summary>
        /// Extract all corridors from the drawing.
        /// </summary>
        public List<CorridorData> ExtractAll(Transaction tr, CivilDocument civilDoc)
        {
            var results = new List<CorridorData>();

            if (civilDoc == null)
                return results;

            try
            {
                foreach (ObjectId corridorId in civilDoc.CorridorCollection)
                {
                    var corridorData = Extract(tr, corridorId);
                    if (corridorData != null)
                        results.Add(corridorData);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CorridorExtractor.ExtractAll error: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Extract data for a single corridor.
        /// </summary>
        public CorridorData? Extract(Transaction tr, ObjectId corridorId)
        {
            try
            {
                var corridor = tr.GetObject(corridorId, OpenMode.ForRead, false) as Corridor;
                if (corridor == null)
                    return null;

                var data = new CorridorData
                {
                    Name = corridor.Name ?? string.Empty,
                    Description = corridor.Description ?? string.Empty
                };

                // Get style via reflection
                ExtractorHelpers.SafeExecute(() =>
                {
                    if (!corridor.StyleId.IsNull)
                    {
                        var style = tr.GetObject(corridor.StyleId, OpenMode.ForRead);
                        data.Style = ExtractorHelpers.GetPropertyValue<string>(style, "Name") ?? string.Empty;
                    }
                }, "GetCorridorStyle");

                // Extract baselines
                ExtractBaselines(tr, corridor, data);

                // Classify corridor type (Road, Junction, Roundabout, Ramp)
                data.CorridorType = ClassifyCorridorType(tr, corridor, data);

                // Extract corridor surfaces
                ExtractCorridorSurfaces(tr, corridor, data);

                // Calculate statistics
                CalculateStatistics(data);

                return data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CorridorExtractor.Extract error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Classify corridor type based on heuristics: baseline count, name keywords.
        /// </summary>
        private string ClassifyCorridorType(Transaction tr, Corridor corridor, CorridorData data)
        {
            // Hebrew and English keywords for classification
            var junctionKeywords = new[] { "צומת", "junction", "intersection" };
            var roundaboutKeywords = new[] { "מעגל", "כיכר", "roundabout", "circle" };
            var rampKeywords = new[] { "רמפ", "מחלף", "ramp", "interchange", "slip" };

            string corridorNameLower = (corridor.Name ?? "").ToLowerInvariant();
            string corridorDescLower = (corridor.Description ?? "").ToLowerInvariant();

            // Check corridor name and description
            string combined = corridorNameLower + " " + corridorDescLower;

            // Also check alignment names
            foreach (var baseline in data.Baselines)
            {
                combined += " " + (baseline.AlignmentName ?? "").ToLowerInvariant();
            }

            if (roundaboutKeywords.Any(k => combined.Contains(k)))
                return "Roundabout";
            if (junctionKeywords.Any(k => combined.Contains(k)))
                return "Junction";
            if (rampKeywords.Any(k => combined.Contains(k)))
                return "Ramp";

            // Multiple baselines (>1) typically indicate junction or ramp
            if (data.Baselines.Count > 1)
                return "Junction";

            return "Road";
        }

        private void ExtractBaselines(Transaction tr, Corridor corridor, CorridorData data)
        {
            try
            {
                foreach (Baseline baseline in corridor.Baselines)
                {
                    var baselineData = new CorridorBaseline
                    {
                        StartStation = baseline.StartStation,
                        EndStation = baseline.EndStation
                    };

                    // Get alignment name
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        if (!baseline.AlignmentId.IsNull)
                        {
                            var alignment = tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) as Alignment;
                            baselineData.AlignmentName = alignment?.Name ?? string.Empty;
                        }
                    }, "GetBaselineAlignment");

                    // Get profile name
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        if (!baseline.ProfileId.IsNull)
                        {
                            var profile = tr.GetObject(baseline.ProfileId, OpenMode.ForRead) as Profile;
                            baselineData.ProfileName = profile?.Name ?? string.Empty;
                        }
                    }, "GetBaselineProfile");

                    // Extract regions
                    ExtractRegions(tr, baseline, baselineData);

                    data.Baselines.Add(baselineData);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractBaselines error: {ex.Message}");
            }
        }

        private void ExtractRegions(Transaction tr, Baseline baseline, CorridorBaseline baselineData)
        {
            try
            {
                foreach (BaselineRegion region in baseline.BaselineRegions)
                {
                    var regionData = new CorridorRegion
                    {
                        Name = region.Name ?? string.Empty,
                        StartStation = region.StartStation,
                        EndStation = region.EndStation,
                        Length = region.EndStation - region.StartStation
                    };

                    // Get assembly name
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        if (!region.AssemblyId.IsNull)
                        {
                            var assembly = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as Assembly;
                            regionData.AssemblyName = assembly?.Name ?? string.Empty;
                        }
                    }, "GetRegionAssembly");

                    // Get sampling frequency via reflection
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        var freqProp = region.GetType().GetProperty("Frequency");
                        if (freqProp != null)
                        {
                            regionData.SamplingInterval = Convert.ToDouble(freqProp.GetValue(region));
                        }
                    }, "GetSamplingFrequency");

                    // Extract sample cross-sections
                    ExtractSampleCrossSections(region, regionData);

                    baselineData.Regions.Add(regionData);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractRegions error: {ex.Message}");
            }
        }

        private void ExtractSampleCrossSections(BaselineRegion region, CorridorRegion regionData)
        {
            try
            {
                var sortedStations = region.SortedStations();
                if (sortedStations == null || sortedStations.Length == 0)
                    return;

                // Sample at key locations (start, 25%, 50%, 75%, end)
                var sampleIndices = new List<int>();
                if (sortedStations.Length <= MaxCrossSectionSamples)
                {
                    sampleIndices = Enumerable.Range(0, sortedStations.Length).ToList();
                }
                else
                {
                    int[] percentages = { 0, 25, 50, 75, 100 };
                    foreach (int pct in percentages)
                    {
                        int idx = (int)((sortedStations.Length - 1) * pct / 100.0);
                        if (!sampleIndices.Contains(idx))
                            sampleIndices.Add(idx);
                    }
                }

                foreach (int idx in sampleIndices)
                {
                    double station = sortedStations[idx];

                    ExtractorHelpers.SafeExecute(() =>
                    {
                        var appliedAssemblies = region.AppliedAssemblies;
                        if (appliedAssemblies == null)
                            return;

                        // Try to get assembly at station via method or indexer
                        object? appliedAssembly = null;
                        ExtractorHelpers.SafeExecute(() =>
                        {
                            var method = appliedAssemblies.GetType().GetMethod("AssemblyAtStation");
                            if (method != null)
                            {
                                appliedAssembly = method.Invoke(appliedAssemblies, new object[] { station });
                            }
                        }, "GetAssemblyAtStation");

                        if (appliedAssembly == null)
                            return;

                        var sampleData = new CrossSectionSample
                        {
                            Station = station
                        };

                        // Extract links using reflection for compatibility
                        double leftWidth = 0, rightWidth = 0;

                        ExtractorHelpers.SafeExecute(() =>
                        {
                            var subassembliesProp = appliedAssembly.GetType().GetProperty("AppliedSubassemblies");
                            if (subassembliesProp == null)
                                return;

                            var appliedSubs = subassembliesProp.GetValue(appliedAssembly) as System.Collections.IEnumerable;
                            if (appliedSubs == null)
                                return;

                            foreach (var appliedSub in appliedSubs)
                            {
                                ExtractorHelpers.SafeExecute(() =>
                                {
                                    // Get subassembly name
                                    string subName = ExtractorHelpers.GetPropertyValue<string>(appliedSub, "SubassemblyName")
                                                   ?? ExtractorHelpers.GetPropertyValue<string>(appliedSub, "Name")
                                                   ?? string.Empty;

                                    // Get origin offset to determine side
                                    double offset = 0;
                                    double originElevation = 0;
                                    var originProp = appliedSub.GetType().GetProperty("OriginStationOffsetElevationToBaseline");
                                    if (originProp != null)
                                    {
                                        var origin = originProp.GetValue(appliedSub);
                                        if (origin != null)
                                        {
                                            var offsetProp = origin.GetType().GetProperty("Offset");
                                            if (offsetProp != null)
                                                offset = Convert.ToDouble(offsetProp.GetValue(origin));
                                            var elevProp = origin.GetType().GetProperty("Elevation");
                                            if (elevProp != null)
                                                originElevation = Convert.ToDouble(elevProp.GetValue(origin));
                                        }
                                    }

                                    string side = offset >= 0 ? "Right" : "Left";
                                    string subNameLower = subName.ToLowerInvariant();

                                    // Detect ditch subassemblies
                                    if (IsDitchSubassembly(subNameLower))
                                    {
                                        sampleData.Ditches.Add(new DitchInfo
                                        {
                                            Side = side,
                                            SubassemblyName = subName,
                                            InvertElevation = originElevation
                                        });
                                    }

                                    // Get calculated links
                                    var linksProp = appliedSub.GetType().GetProperty("Links")
                                                  ?? appliedSub.GetType().GetProperty("CalculatedLinks");
                                    if (linksProp == null)
                                        return;

                                    var links = linksProp.GetValue(appliedSub) as System.Collections.IEnumerable;
                                    if (links == null)
                                        return;

                                    foreach (var link in links)
                                    {
                                        var linkData = new CrossSectionLink
                                        {
                                            SubassemblyName = subName,
                                            Side = side
                                        };

                                        // Get codes
                                        ExtractorHelpers.SafeExecute(() =>
                                        {
                                            var codesProp = link.GetType().GetProperty("CorridorCodes")
                                                         ?? link.GetType().GetProperty("Codes");
                                            if (codesProp != null)
                                            {
                                                var codes = codesProp.GetValue(link) as System.Collections.IEnumerable;
                                                if (codes != null)
                                                {
                                                    var codeList = new List<string>();
                                                    foreach (var code in codes)
                                                    {
                                                        if (code != null)
                                                            codeList.Add(code.ToString() ?? "");
                                                        if (codeList.Count >= 3)
                                                            break;
                                                    }
                                                    linkData.Code = string.Join(",", codeList);
                                                }
                                            }
                                        }, "GetLinkCodes");

                                        // Get points for offset/elevation
                                        ExtractorHelpers.SafeExecute(() =>
                                        {
                                            var pointsProp = link.GetType().GetProperty("CalculatedPoints")
                                                          ?? link.GetType().GetProperty("Points");
                                            if (pointsProp == null)
                                                return;

                                            var points = pointsProp.GetValue(link) as System.Collections.IList;
                                            if (points == null || points.Count == 0)
                                                return;

                                            var lastPoint = points[points.Count - 1];
                                            var soeeProp = lastPoint?.GetType().GetProperty("StationOffsetElevationToBaseline");
                                            if (soeeProp != null)
                                            {
                                                var soee = soeeProp.GetValue(lastPoint);
                                                if (soee != null)
                                                {
                                                    var offsetProp = soee.GetType().GetProperty("Offset");
                                                    var elevProp = soee.GetType().GetProperty("Elevation");
                                                    if (offsetProp != null)
                                                        linkData.Offset = Convert.ToDouble(offsetProp.GetValue(soee));
                                                    if (elevProp != null)
                                                        linkData.Elevation = Convert.ToDouble(elevProp.GetValue(soee));
                                                }
                                            }

                                            // Track widths
                                            double absOffset = Math.Abs(linkData.Offset);
                                            if (linkData.Offset >= 0 && absOffset > rightWidth)
                                                rightWidth = absOffset;
                                            else if (linkData.Offset < 0 && absOffset > leftWidth)
                                                leftWidth = absOffset;
                                        }, "GetLinkPoints");

                                        sampleData.Links.Add(linkData);
                                    }

                                    // Detect lane subassemblies (after links are processed so we can compute width)
                                    if (IsLaneSubassembly(subNameLower))
                                    {
                                        double laneWidth = 0;
                                        double? laneSlope = null;

                                        // Find the outermost link offset belonging to this lane subassembly
                                        double originAbsOffset = Math.Abs(offset);
                                        double maxLinkAbsOffset = originAbsOffset;
                                        double? outermostElevation = null;

                                        foreach (var lnk in sampleData.Links.Where(l => l.SubassemblyName == subName))
                                        {
                                            double absOff = Math.Abs(lnk.Offset);
                                            if (absOff > maxLinkAbsOffset)
                                            {
                                                maxLinkAbsOffset = absOff;
                                                outermostElevation = lnk.Elevation;
                                            }
                                        }

                                        laneWidth = maxLinkAbsOffset - originAbsOffset;

                                        // Calculate cross-slope from origin to outermost point
                                        if (laneWidth > 0.01 && outermostElevation.HasValue)
                                        {
                                            double elevDiff = outermostElevation.Value - originElevation;
                                            laneSlope = Math.Round((elevDiff / laneWidth) * 100.0, 2);
                                        }

                                        if (laneWidth > 0.01)
                                        {
                                            sampleData.Lanes.Add(new LaneInfo
                                            {
                                                Side = side,
                                                SubassemblyName = subName,
                                                Width = Math.Round(laneWidth, 3),
                                                Slope = laneSlope
                                            });
                                        }
                                    }
                                }, "ProcessSubassembly");
                            }
                        }, "ExtractSubassemblies");

                        sampleData.LeftWidth = ExtractorHelpers.Round(leftWidth, 3);
                        sampleData.RightWidth = ExtractorHelpers.Round(rightWidth, 3);
                        sampleData.TotalWidth = ExtractorHelpers.Round(leftWidth + rightWidth, 3);

                        // Classify elements by elevation differential
                        ClassifyCrossSectionElements(sampleData);

                        regionData.SampleCrossSections.Add(sampleData);
                    }, $"ExtractCrossSection_{station}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSampleCrossSections error: {ex.Message}");
            }
        }

        private void ExtractCorridorSurfaces(Transaction tr, Corridor corridor, CorridorData data)
        {
            try
            {
                foreach (CorridorSurface corSurface in corridor.CorridorSurfaces)
                {
                    var surfaceInfo = new CorridorSurfaceInfo
                    {
                        Name = corSurface.Name ?? string.Empty,
                        SurfaceType = corSurface.GetType().Name
                    };

                    ExtractorHelpers.SafeExecute(() =>
                    {
                        // Check if surface is built
                        if (!corSurface.SurfaceId.IsNull)
                        {
                            var surface = tr.GetObject(corSurface.SurfaceId, OpenMode.ForRead) as TinSurface;
                            if (surface != null)
                            {
                                surfaceInfo.IsBuilt = surface.Triangles.Count > 0;

                                if (surfaceInfo.IsBuilt)
                                {
                                    var extents = surface.GeometricExtents;
                                    surfaceInfo.ElevationRange = new ElevationRange
                                    {
                                        Min = extents.MinPoint.Z,
                                        Max = extents.MaxPoint.Z,
                                        Mean = (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0
                                    };
                                }
                            }
                        }
                    }, "GetCorridorSurfaceDetails");

                    data.Surfaces.Add(surfaceInfo);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractCorridorSurfaces error: {ex.Message}");
            }
        }

        private static bool IsDitchSubassembly(string nameLower)
        {
            return nameLower.Contains("ditch") || nameLower.Contains("תעלה") ||
                   nameLower.Contains("ניקוז") || nameLower.Contains("drain") ||
                   nameLower.Contains("swale") || nameLower.Contains("channel");
        }

        private static bool IsLaneSubassembly(string nameLower)
        {
            return nameLower.Contains("lane") || nameLower.Contains("נתיב") ||
                   nameLower.Contains("pave") || nameLower.Contains("travel") ||
                   nameLower.Contains("etw");
        }

        /// <summary>
        /// Classify cross-section links by elevation differential relative to road surface.
        /// Raised elements (>0.10m above) → sidewalk/median/island.
        /// Depressed elements (>0.15m below) → ditch/channel.
        /// </summary>
        private static void ClassifyCrossSectionElements(CrossSectionSample sample)
        {
            if (sample.Links.Count == 0) return;

            // Find road surface reference elevation: link closest to offset 0
            var sortedLinks = sample.Links.OrderBy(l => Math.Abs(l.Offset)).ToList();
            double refElevation = sortedLinks.First().Elevation;

            double roadMinOffset = double.MaxValue, roadMaxOffset = double.MinValue;
            double? leftSidewalkStart = null, leftSidewalkEnd = null;
            double? rightSidewalkStart = null, rightSidewalkEnd = null;
            double? medianMinOffset = null, medianMaxOffset = null;

            foreach (var link in sortedLinks)
            {
                double delta = link.Elevation - refElevation;
                link.ElevationDelta = Math.Round(delta, 3);

                string nameLower = (link.SubassemblyName ?? "").ToLowerInvariant();
                string codeLower = (link.Code ?? "").ToLowerInvariant();
                bool isLane = nameLower.Contains("lane") || nameLower.Contains("נתיב") ||
                              nameLower.Contains("pave") || nameLower.Contains("travel") || nameLower.Contains("etw");
                bool isShoulder = nameLower.Contains("shoulder") || nameLower.Contains("שוליים") ||
                                  codeLower.Contains("shoulder");

                if (isLane || (Math.Abs(delta) < 0.10 && isShoulder))
                {
                    link.ClassifiedType = isLane ? "road_surface" : "shoulder";
                    if (link.Offset < roadMinOffset) roadMinOffset = link.Offset;
                    if (link.Offset > roadMaxOffset) roadMaxOffset = link.Offset;
                }
                else if (delta > 0.10)
                {
                    // Raised element
                    bool isSidewalk = nameLower.Contains("sidewalk") || nameLower.Contains("sdwk") ||
                                      nameLower.Contains("מדרכה") || codeLower.Contains("sidewalk");
                    bool isMedian = nameLower.Contains("median") || nameLower.Contains("אי הפרדה") ||
                                    codeLower.Contains("median") || codeLower.Contains("island");

                    if (isMedian || (Math.Abs(link.Offset) < 1.0 && delta > 0.10))
                    {
                        link.ClassifiedType = "median";
                        if (!medianMinOffset.HasValue || link.Offset < medianMinOffset) medianMinOffset = link.Offset;
                        if (!medianMaxOffset.HasValue || link.Offset > medianMaxOffset) medianMaxOffset = link.Offset;
                    }
                    else if (isSidewalk || delta > 0.10)
                    {
                        link.ClassifiedType = "sidewalk";
                        if (link.Side == "Left")
                        {
                            if (!leftSidewalkStart.HasValue || link.Offset < leftSidewalkStart) leftSidewalkStart = link.Offset;
                            if (!leftSidewalkEnd.HasValue || link.Offset > leftSidewalkEnd) leftSidewalkEnd = link.Offset;
                        }
                        else
                        {
                            if (!rightSidewalkStart.HasValue || link.Offset < rightSidewalkStart) rightSidewalkStart = link.Offset;
                            if (!rightSidewalkEnd.HasValue || link.Offset > rightSidewalkEnd) rightSidewalkEnd = link.Offset;
                        }
                    }
                }
                else if (delta < -0.15)
                {
                    link.ClassifiedType = "ditch";
                }
                else if (link.ClassifiedType == null)
                {
                    link.ClassifiedType = isShoulder ? "shoulder" : "other";
                }
            }

            // Build classification summary
            double roadWidth = (roadMaxOffset != double.MinValue && roadMinOffset != double.MaxValue)
                ? roadMaxOffset - roadMinOffset : 0;

            sample.Classification = new CrossSectionClassification
            {
                RoadSurfaceWidth = Math.Round(roadWidth, 2),
                LeftSidewalkWidth = leftSidewalkStart.HasValue && leftSidewalkEnd.HasValue
                    ? Math.Round(Math.Abs(leftSidewalkEnd.Value - leftSidewalkStart.Value), 2) : null,
                RightSidewalkWidth = rightSidewalkStart.HasValue && rightSidewalkEnd.HasValue
                    ? Math.Round(Math.Abs(rightSidewalkEnd.Value - rightSidewalkStart.Value), 2) : null,
                MedianWidth = medianMinOffset.HasValue && medianMaxOffset.HasValue
                    ? Math.Round(Math.Abs(medianMaxOffset.Value - medianMinOffset.Value), 2) : null,
                HasLeftDitch = sample.Ditches.Any(d => d.Side == "Left") || sample.Links.Any(l => l.ClassifiedType == "ditch" && l.Side == "Left"),
                HasRightDitch = sample.Ditches.Any(d => d.Side == "Right") || sample.Links.Any(l => l.ClassifiedType == "ditch" && l.Side == "Right"),
            };
        }

        private void CalculateStatistics(CorridorData data)
        {
            data.Statistics = new CorridorStatistics
            {
                BaselineCount = data.Baselines.Count,
                SurfaceCount = data.Surfaces.Count
            };

            var assemblyNames = new HashSet<string>();
            int totalSamples = 0;
            double totalLength = 0;

            foreach (var baseline in data.Baselines)
            {
                foreach (var region in baseline.Regions)
                {
                    data.Statistics.RegionCount++;
                    totalLength += region.Length;
                    totalSamples += region.SampleCrossSections.Count;

                    if (!string.IsNullOrEmpty(region.AssemblyName))
                        assemblyNames.Add(region.AssemblyName);
                }
            }

            data.Statistics.AssemblyCount = assemblyNames.Count;
            data.Statistics.TotalLength = ExtractorHelpers.Round(totalLength, 2);
            data.Statistics.SampleCount = totalSamples;
        }
    }
}
