using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Models;
using AcDb = Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Creates a compact summary of drawing data suitable for LLM consumption.
    /// Focuses on engineering-relevant properties, not raw geometry.
    /// Includes feature lines (with ROW boundary detection) and corridor lane widths.
    /// </summary>
    public class DrawingSummaryExtractor
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Extract compact summary for LLM.
        /// </summary>
        public DrawingSummaryForAI ExtractSummary() => ExtractSummary(includeIntersections: true);

        /// <summary>
        /// Extract compact summary for LLM.
        /// When <paramref name="includeIntersections"/> is false, the
        /// expensive alignment-pair intersection scan (which throws
        /// thousands of <c>PointNotOnEntityException</c> on real drawings)
        /// is skipped. Use the fast path for the scope-selection dialog;
        /// the analyze pipeline still needs the full extraction.
        /// </summary>
        public DrawingSummaryForAI ExtractSummary(bool includeIntersections)
        {
            var summary = new DrawingSummaryForAI();

            try
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return summary;

                CivilDocument? civilDoc = null;
                try { civilDoc = CivilApplication.ActiveDocument; }
                catch { }

                var db = doc.Database;

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // Basic metadata - matching agent expected format
                    summary.Metadata.DrawingName = System.IO.Path.GetFileName(doc.Name);
                    summary.Metadata.FilePath = doc.Name;
                    var unitsName = GetUnitsName(db);
                    summary.Metadata.Units = string.IsNullOrEmpty(unitsName) ? null : unitsName;

                    // Get extents
                    var (extents, entityCount) = GetDrawingExtents(db, tr);
                    summary.Statistics.TotalEntities = entityCount;

                    // Estimate coordinate system
                    summary.Metadata.CoordinateSystem = EstimateCoordinateSystem(extents);
                    if (summary.Metadata.CoordinateSystem == "Undetected" || string.IsNullOrEmpty(summary.Metadata.CoordinateSystem))
                        summary.Metadata.CoordinateSystem = null;

                    if (civilDoc != null)
                    {
                        // Extract surfaces summary
                        ExtractSurfacesSummary(tr, civilDoc, summary);

                        // Extract alignments summary
                        ExtractAlignmentsSummary(tr, civilDoc, summary);

                        // Extract profiles summary
                        ExtractProfilesSummary(tr, civilDoc, summary);

                        // Extract corridors summary
                        ExtractCorridorsSummary(tr, civilDoc, summary);

                        // Extract pipe networks summary
                        ExtractPipeNetworksSummary(tr, civilDoc, summary);

                        // Extract feature lines summary (for ROW check)
                        ExtractFeatureLinesSummary(tr, civilDoc, summary);

                        // Detect intersections between alignments. The fast
                        // path (used to render the scope dialog) skips this
                        // — see DetectCrossing for why it's expensive.
                        if (includeIntersections)
                        {
                            DetectIntersections(tr, civilDoc, summary);
                        }

                        // Segment alignments into standard/intersection/ramp zones
                        SegmentAlignments(summary);

                        // Extract lane widths and max corridor widths from full corridor data
                        ExtractCorridorDetailedData(tr, civilDoc, summary);
                    }

                    // Layer summary
                    ExtractLayersSummary(db, tr, summary);

                    tr.Commit();
                }

                // Calculate totals
                summary.CalculateTotals();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSummary error: {ex.Message}");
                summary.QualityIssues.Add($"Extraction error: {ex.Message}");
            }

            return summary;
        }

        /// <summary>
        /// Maximum payload size in bytes before reducing detail.
        /// </summary>
        private const int MaxPayloadBytes = 500 * 1024; // 500KB

        /// <summary>
        /// Extract summary as JSON string.
        /// Automatically reduces detail if payload exceeds size limit.
        /// </summary>
        public string ExtractSummaryAsJson() => ExtractSummaryAsJson(includeIntersections: true);

        /// <summary>
        /// Extract summary as JSON string.
        /// When <paramref name="includeIntersections"/> is false, skips the
        /// expensive intersection scan (use this for the scope-selection
        /// dialog where only entity counts and per-alignment metadata are
        /// needed; intersections must be present before <c>analyze</c>).
        /// </summary>
        public string ExtractSummaryAsJson(bool includeIntersections)
        {
            var summary = ExtractSummary(includeIntersections);

            // Apply initial data limits
            TrimSummaryData(summary);

            string json = JsonSerializer.Serialize(summary, JsonOptions);

            // If still too large, reduce further
            if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
            {
                ReduceSummaryDetail(summary);
                json = JsonSerializer.Serialize(summary, JsonOptions);
                System.Diagnostics.Debug.WriteLine(
                    $"Summary reduced to {Encoding.UTF8.GetByteCount(json)} bytes after detail reduction");
            }

            // Save debug copy
            SaveDebugJson(json, "summary");

            return json;
        }

        /// <summary>
        /// Apply initial data limits to keep payload reasonable.
        /// </summary>
        private void TrimSummaryData(DrawingSummaryForAI summary)
        {
            foreach (var align in summary.Alignments)
            {
                // Limit curves to max 50
                if (align.Curves.Count > 50)
                    align.Curves = align.Curves.Take(50).ToList();

                // Limit superelevation to max 20
                if (align.Superelevation != null && align.Superelevation.Count > 20)
                    align.Superelevation = align.Superelevation.Take(20).ToList();
            }

            foreach (var profile in summary.Profiles)
            {
                // Limit PVI points to max 30
                if (profile.PviPoints.Count > 30)
                    profile.PviPoints = profile.PviPoints.Take(30).ToList();

                // Limit vertical curves to max 20
                if (profile.VerticalCurves.Count > 20)
                    profile.VerticalCurves = profile.VerticalCurves.Take(20).ToList();
            }

            // Limit layers to top 10 by entity count
            if (summary.Layers.Count > 10)
            {
                summary.Layers = summary.Layers
                    .OrderByDescending(l => l.EntityCount)
                    .Take(10)
                    .ToList();
            }
        }

        /// <summary>
        /// Further reduce summary detail when payload is too large.
        /// Drops superelevation data, reduces curve detail, and clears cross-section data.
        /// </summary>
        private void ReduceSummaryDetail(DrawingSummaryForAI summary)
        {
            System.Diagnostics.Debug.WriteLine("Reducing summary detail due to payload size");

            foreach (var align in summary.Alignments)
            {
                // Drop superelevation completely
                align.Superelevation = null;
                align.HasSuperelevation = null;

                // Reduce curves to max 20
                if (align.Curves.Count > 20)
                    align.Curves = align.Curves.Take(20).ToList();
            }

            foreach (var profile in summary.Profiles)
            {
                // Reduce PVI points to max 10
                if (profile.PviPoints.Count > 10)
                    profile.PviPoints = profile.PviPoints.Take(10).ToList();
            }
        }

        #region Surface Extraction

        private void ExtractSurfacesSummary(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                foreach (ObjectId surfaceId in civilDoc.GetSurfaceIds())
                {
                    try
                    {
                        var surface = tr.GetObject(surfaceId, OpenMode.ForRead) as CivilSurface;
                        if (surface == null) continue;

                        var surfaceInfo = new SurfaceSummary
                        {
                            Name = surface.Name,
                            Type = surface.GetType().Name.Replace("Surface", "")
                        };

                        // Try to get general surface properties first
                        try
                        {
                            var ext = surface.GeometricExtents;
                            surfaceInfo.ElevationMin = Math.Round(ext.MinPoint.Z, 2);
                            surfaceInfo.ElevationMax = Math.Round(ext.MaxPoint.Z, 2);
                            System.Diagnostics.Debug.WriteLine($"Surface '{surface.Name}': Z range {ext.MinPoint.Z:F2} to {ext.MaxPoint.Z:F2}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Surface '{surface.Name}': Failed to get extents - {ex.Message}");
                        }

                        if (surface is TinSurface tin)
                        {
                            surfaceInfo.PointCount = SafeGet(() => tin.Vertices.Count, 0);
                            surfaceInfo.TriangleCount = SafeGet(() => tin.Triangles.Count, 0);

                            System.Diagnostics.Debug.WriteLine($"TinSurface '{surface.Name}': {surfaceInfo.PointCount} points, {surfaceInfo.TriangleCount} triangles");

                            if (surfaceInfo.TriangleCount > 0)
                            {
                                // Sample slope analysis (sample 100 triangles max)
                                var slopes = CalculateSlopesSample(tin, 100);
                                if (slopes.Count > 0)
                                {
                                    surfaceInfo.SlopeMinPercent = Math.Round(slopes.Min(), 1);
                                    surfaceInfo.SlopeMaxPercent = Math.Round(slopes.Max(), 1);
                                    surfaceInfo.SlopeMeanPercent = Math.Round(slopes.Average(), 1);
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"WARNING: TinSurface '{surface.Name}' has 0 triangles - surface may need rebuilding");
                            }
                        }
                        else if (surface is GridSurface grid)
                        {
                            surfaceInfo.Type = "Grid";
                            // GridSurface doesn't have Vertices/Triangles in the same way
                            System.Diagnostics.Debug.WriteLine($"GridSurface '{surface.Name}': Type={grid.GetType().Name}");
                        }
                        else if (surface is TinVolumeSurface vol)
                        {
                            surfaceInfo.Type = "Volume";
                            try
                            {
                                var props = vol.GetVolumeProperties();
                                surfaceInfo.CutVolume = Math.Round(props.UnadjustedCutVolume, 1);
                                surfaceInfo.FillVolume = Math.Round(props.UnadjustedFillVolume, 1);
                                surfaceInfo.NetVolume = Math.Round(props.UnadjustedNetVolume, 1);
                            }
                            catch { }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Unknown surface type '{surface.Name}': {surface.GetType().FullName}");
                        }

                        summary.Surfaces.Add(surfaceInfo);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract surface: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to extract surfaces: {ex.Message}");
            }
        }

        private List<double> CalculateSlopesSample(TinSurface surface, int maxSamples)
        {
            var slopes = new List<double>();
            try
            {
                var triangles = surface.Triangles;
                if (triangles == null || triangles.Count == 0) return slopes;

                int step = Math.Max(1, triangles.Count / maxSamples);
                int count = 0;

                foreach (TinSurfaceTriangle tri in triangles)
                {
                    if (count++ % step != 0) continue;

                    try
                    {
                        var v1 = tri.Vertex1.Location;
                        var v2 = tri.Vertex2.Location;
                        var v3 = tri.Vertex3.Location;

                        double ax = v2.X - v1.X, ay = v2.Y - v1.Y, az = v2.Z - v1.Z;
                        double bx = v3.X - v1.X, by = v3.Y - v1.Y, bz = v3.Z - v1.Z;

                        double nx = ay * bz - az * by;
                        double ny = az * bx - ax * bz;
                        double nz = ax * by - ay * bx;

                        double horizontalMag = Math.Sqrt(nx * nx + ny * ny);
                        if (Math.Abs(nz) > 0.0001)
                        {
                            double slope = Math.Abs(horizontalMag / nz) * 100.0;
                            if (slope < 500) slopes.Add(slope);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return slopes;
        }

        #endregion

        #region Alignment Extraction

        private void ExtractAlignmentsSummary(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
                        if (alignment == null) continue;

                        var alignInfo = new AlignmentSummary
                        {
                            Name = alignment.Name,
                            Description = string.IsNullOrWhiteSpace(alignment.Description) ? null : alignment.Description,
                            Length = Math.Round(alignment.Length, 2),
                            StartStation = Math.Round(alignment.StartingStation, 2),
                            EndStation = Math.Round(alignment.EndingStation, 2)
                        };

                        // Tier 1: DesignSpeeds collection — read all entries as-is.
                        // Civil 3D speeds are trusted (engineer sets them in Alignment Properties).
                        try
                        {
                            var speeds = alignment.DesignSpeeds;
                            if (speeds != null && speeds.Count > 0)
                            {
                                double maxSpeed = 0;
                                var segments = new List<SpeedSegmentInfo>();
                                foreach (DesignSpeed ds in speeds)
                                {
                                    segments.Add(new SpeedSegmentInfo
                                    {
                                        Station = Math.Round(ds.Station, 2),
                                        SpeedKph = ds.Value
                                    });
                                    if (ds.Value > maxSpeed)
                                        maxSpeed = ds.Value;
                                }
                                if (maxSpeed > 0)
                                    alignInfo.DesignSpeedKph = maxSpeed;

                                // Compute start/end station ranges
                                double alignEnd = Math.Round(alignment.EndingStation, 2);
                                for (int si = 0; si < segments.Count; si++)
                                {
                                    segments[si].StartStation = segments[si].Station;
                                    segments[si].EndStation = si < segments.Count - 1
                                        ? segments[si + 1].Station
                                        : alignEnd;
                                    PopulateDesignCriteria(segments[si]);
                                }
                                alignInfo.SpeedSegments = segments;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"DesignSpeed extraction error for {alignment.Name}: {ex.Message}");
                        }

                        // Tier 2: Parse Description for speed
                        if (!alignInfo.DesignSpeedKph.HasValue)
                        {
                            alignInfo.DesignSpeedKph = ExtractSpeedFromText(alignment.Description);
                        }

                        // Tier 3: Parse Name for speed
                        if (!alignInfo.DesignSpeedKph.HasValue)
                        {
                            alignInfo.DesignSpeedKph = ExtractSpeedFromText(alignment.Name);
                        }

                        // Analyze geometry (curves with radii)
                        AnalyzeAlignmentGeometry(alignment, alignInfo);

                        // Get profile names
                        try
                        {
                            foreach (ObjectId pid in alignment.GetProfileIds())
                            {
                                var profile = tr.GetObject(pid, OpenMode.ForRead) as Profile;
                                if (profile != null)
                                    alignInfo.ProfileNames.Add(profile.Name);
                            }
                        }
                        catch { }

                        // Extract superelevation data
                        ExtractSuperelevation(alignment, alignInfo);

                        // If superelevation list is empty, null it out
                        if (alignInfo.Superelevation != null && alignInfo.Superelevation.Count == 0)
                            alignInfo.Superelevation = null;

                        // Set road type hint based on design speed + name heuristics
                        alignInfo.RoadTypeHint = ClassifyRoadType(alignInfo);

                        summary.Alignments.Add(alignInfo);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static readonly string[] AgriculturalKeywords = { "חקלאי", "שירות", "גישה", "שדה", "agricultural", "service", "access" };

        private static string? ClassifyRoadType(AlignmentSummary info)
        {
            // Check name for agricultural keywords first
            if (!string.IsNullOrEmpty(info.Name))
            {
                var nameLower = info.Name.ToLowerInvariant();
                foreach (var kw in AgriculturalKeywords)
                {
                    if (nameLower.Contains(kw.ToLowerInvariant()) || info.Name.Contains(kw))
                        return "agricultural";
                }
            }

            // Speed-based classification
            if (info.DesignSpeedKph.HasValue)
                return info.DesignSpeedKph.Value >= 80 ? "interurban" : "urban";

            return null;
        }

        private void AnalyzeAlignmentGeometry(Alignment alignment, AlignmentSummary info)
        {
            try
            {
                double minRadius = double.MaxValue;
                double maxRadius = 0;
                int tangentCount = 0, curveCount = 0, spiralCount = 0;

                foreach (AlignmentEntity entity in alignment.Entities)
                {
                    if (entity is AlignmentLine)
                    {
                        tangentCount++;
                    }
                    else if (entity is AlignmentArc arc)
                    {
                        curveCount++;
                        if (arc.Radius < minRadius) minRadius = arc.Radius;
                        if (arc.Radius > maxRadius) maxRadius = arc.Radius;

                        // Add curve details
                        info.Curves.Add(new CurveInfo
                        {
                            Type = "Arc",
                            StartStation = Math.Round(arc.StartStation, 2),
                            EndStation = Math.Round(arc.EndStation, 2),
                            Length = Math.Round(arc.Length, 2),
                            Radius = Math.Round(arc.Radius, 1),
                            Direction = arc.Clockwise ? "Right" : "Left"
                        });
                    }
                    else if (entity is AlignmentSpiral spiral)
                    {
                        spiralCount++;
                    }
                    else if (entity is AlignmentSCS scs)
                    {
                        curveCount++;
                        spiralCount += 2;
                        double radius = scs.Arc.Radius;
                        if (radius < minRadius) minRadius = radius;
                        if (radius > maxRadius) maxRadius = radius;

                        // Add SCS curve details with spirals
                        var curveInfo = new CurveInfo
                        {
                            Type = "SCS",
                            StartStation = Math.Round(scs.StartStation, 2),
                            EndStation = Math.Round(scs.EndStation, 2),
                            Length = Math.Round(scs.Length, 2),
                            Radius = Math.Round(radius, 1),
                            Direction = scs.Arc.Clockwise ? "Right" : "Left"
                        };

                        // Get spiral lengths
                        try
                        {
                            curveInfo.SpiralInLength = Math.Round(scs.SpiralIn.Length, 2);
                            curveInfo.SpiralOutLength = Math.Round(scs.SpiralOut.Length, 2);
                        }
                        catch { }

                        info.Curves.Add(curveInfo);
                    }
                    else if (entity is AlignmentSTS sts)
                    {
                        // Spiral-Tangent-Spiral
                        spiralCount += 2;
                        tangentCount++;
                    }
                }

                info.TangentCount = tangentCount;
                info.CurveCount = curveCount;
                info.SpiralCount = spiralCount;

                if (minRadius < double.MaxValue)
                    info.MinRadius = Math.Round(minRadius, 1);
                if (maxRadius > 0)
                    info.MaxRadius = Math.Round(maxRadius, 1);

                // Compute flagged curves (radius below required minimum per speed segment)
                if (info.SpeedSegments != null && info.SpeedSegments.Count > 0 && info.Curves.Count > 0)
                {
                    foreach (var curve in info.Curves)
                    {
                        // Find the speed segment covering this curve's start station
                        SpeedSegmentInfo? seg = null;
                        foreach (var s in info.SpeedSegments)
                        {
                            if (curve.StartStation >= s.StartStation && curve.StartStation <= s.EndStation)
                            { seg = s; break; }
                        }
                        if (seg == null) seg = info.SpeedSegments[0];

                        if (seg.MinRadius.HasValue && curve.Radius < seg.MinRadius.Value)
                        {
                            if (info.FlaggedCurves == null) info.FlaggedCurves = new List<FlaggedCurve>();
                            info.FlaggedCurves.Add(new FlaggedCurve
                            {
                                StartStation = curve.StartStation,
                                EndStation = curve.EndStation,
                                Radius = curve.Radius,
                                RequiredRadius = seg.MinRadius.Value,
                                SpeedKph = seg.SpeedKph
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AnalyzeAlignmentGeometry error: {ex.Message}");
            }
        }

        private void ExtractSuperelevation(Alignment alignment, AlignmentSummary info)
        {
            try
            {
                // Pre-check: verify superelevation data exists before accessing
                // SuperelevationCriticalStations throws ArgumentException when
                // superelevation is not configured for this alignment
                dynamic superStations;
                try
                {
                    superStations = alignment.SuperelevationCriticalStations;
                }
                catch (ArgumentException)
                {
                    // Superelevation not configured for this alignment — expected, not an error
                    info.HasSuperelevation = false;
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // COM interop failure — alignment has no superelevation data
                    info.HasSuperelevation = false;
                    return;
                }
                if (superStations == null || superStations.Count == 0)
                {
                    return;
                }

                info.HasSuperelevation = true;

                // Sample superelevation at critical stations (limit to avoid huge payload)
                int maxSamples = Math.Min(superStations.Count, 20);
                int step = Math.Max(1, superStations.Count / maxSamples);

                for (int i = 0; i < superStations.Count; i += step)
                {
                    try
                    {
                        var station = superStations[i];
                        double sta = station.Station;

                        // Get superelevation values at this station
                        double leftSlope = 0, rightSlope = 0;
                        string pivotMethod = "";

                        try
                        {
                            // Try to get actual superelevation data via reflection
                            var leftProp = station.GetType().GetProperty("LeftSlope")
                                        ?? station.GetType().GetProperty("LeftSuperelev");
                            var rightProp = station.GetType().GetProperty("RightSlope")
                                         ?? station.GetType().GetProperty("RightSuperelev");
                            var pivotProp = station.GetType().GetProperty("PivotMethod");

                            if (leftProp != null)
                                leftSlope = Convert.ToDouble(leftProp.GetValue(station) ?? 0);
                            if (rightProp != null)
                                rightSlope = Convert.ToDouble(rightProp.GetValue(station) ?? 0);
                            if (pivotProp != null)
                                pivotMethod = pivotProp.GetValue(station)?.ToString() ?? "";
                        }
                        catch { }

                        info.Superelevation.Add(new SuperelevationInfo
                        {
                            Station = Math.Round(sta, 2),
                            LeftSlopePercent = Math.Round(leftSlope * 100, 2),  // Convert to percent
                            RightSlopePercent = Math.Round(rightSlope * 100, 2),
                            PivotMethod = pivotMethod
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSuperelevation error: {ex.Message}");
            }
        }

        #endregion

        #region Intersection Detection

        /// <summary>
        /// Detect intersections between alignments using endpoint proximity and crossing analysis.
        /// </summary>
        private void DetectIntersections(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                // Collect alignment objects with their start/end points.
                // AABBs are stored alongside in a parallel list so the
                // existing helper signatures (which take a 5-tuple) don't
                // need to change. The AABBs let us skip the expensive
                // DetectCrossing call entirely for non-overlapping pairs
                // and skip per-sample StationOffset for samples that can't
                // possibly project onto the other alignment.
                var alignments = new List<(Alignment alignment, double startX, double startY, double endX, double endY)>();
                var bounds = new List<AlignmentBounds>();

                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
                        if (alignment == null) continue;

                        alignments.Add((
                            alignment,
                            alignment.StartPoint.X, alignment.StartPoint.Y,
                            alignment.EndPoint.X, alignment.EndPoint.Y
                        ));
                        bounds.Add(ComputeAlignmentBounds(alignment));
                    }
                    catch { }
                }

                if (alignments.Count < 2) return;

                const double endpointThreshold = 5.0; // meters — proximity for endpoint matching
                const double offsetThreshold = 5.0;   // meters — proximity for alignment-to-alignment

                // Build corridor lookup: alignment name → junction/roundabout corridor name
                var corridorLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var corr in summary.Corridors)
                {
                    if (corr.CorridorType is "Junction" or "Roundabout")
                    {
                        foreach (var an in corr.AlignmentNames)
                            corridorLookup[an] = corr.Name;
                    }
                }

                for (int i = 0; i < alignments.Count; i++)
                {
                    for (int j = i + 1; j < alignments.Count; j++)
                    {
                        try
                        {
                            var a = alignments[i];
                            var b = alignments[j];
                            var aBounds = bounds[i];
                            var bBounds = bounds[j];

                            // Check all four endpoint-to-endpoint combinations
                            CheckEndpointPair(a, "start", b, "start", endpointThreshold, summary, corridorLookup);
                            CheckEndpointPair(a, "start", b, "end", endpointThreshold, summary, corridorLookup);
                            CheckEndpointPair(a, "end", b, "start", endpointThreshold, summary, corridorLookup);
                            CheckEndpointPair(a, "end", b, "end", endpointThreshold, summary, corridorLookup);

                            // Check endpoint of A on body of B (T-junction detection)
                            CheckEndpointOnAlignment(a, "start", b, offsetThreshold, summary, corridorLookup);
                            CheckEndpointOnAlignment(a, "end", b, offsetThreshold, summary, corridorLookup);
                            CheckEndpointOnAlignment(b, "start", a, offsetThreshold, summary, corridorLookup);
                            CheckEndpointOnAlignment(b, "end", a, offsetThreshold, summary, corridorLookup);

                            // Check for mid-alignment crossings — skip when
                            // the alignments' bounding boxes don't overlap
                            // (with a margin equal to the offset threshold),
                            // since a crossing is geometrically impossible.
                            if (BoundsOverlap(aBounds, bBounds, offsetThreshold))
                            {
                                DetectCrossing(a.alignment, b.alignment, offsetThreshold, bBounds, summary, corridorLookup);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Intersection check error for pair ({alignments[i].alignment.Name}, {alignments[j].alignment.Name}): {ex.Message}");
                        }
                    }
                }

                // Second pass — enrich each detected intersection with curb-return
                // geometry inferred from neighbouring arcs/SCS. Added 2026-05-04
                // per engineer feedback to enable §8.3 / Table 8.3 compliance
                // checks in the agent (see EnrichWithCurbReturnGeometry).
                foreach (var ix in summary.Intersections)
                {
                    EnrichWithCurbReturnGeometry(tr, civilDoc, ix);
                }

                // ── TEMP DIAGNOSTIC (remove after intersection root-cause) ──
                // Dump the full detected-intersection inventory: which alignments
                // got an approach and at what station. This is the ground truth we
                // need to see whether the mainline (e.g. "73") is registered as an
                // approach to the ramp junction, or only the ramp ("7355") is.
                System.Diagnostics.Debug.WriteLine(
                    $"[INTERSECTION-DIAG] DetectIntersections found "
                    + $"{summary.Intersections.Count} intersection(s) across "
                    + $"{alignments.Count} alignment(s)");
                foreach (var ix in summary.Intersections)
                {
                    var names = string.Join(",", ix.AlignmentNames ?? new List<string>());
                    System.Diagnostics.Debug.WriteLine(
                        $"[INTERSECTION-DIAG]   type={ix.IntersectionType} "
                        + $"alignments=[{names}] X={ix.X} Y={ix.Y} "
                        + $"angle={ix.IntersectionAngle}");
                    foreach (var ap in ix.Approaches ?? new List<IntersectionApproach>())
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[INTERSECTION-DIAG]     approach align={ap.AlignmentName} "
                            + $"station={ap.Station} isEndpoint={ap.IsEndpoint} "
                            + $"endpointType={ap.EndpointType}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DetectIntersections error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if two alignment endpoints are close to each other (Fork or Merge).
        /// </summary>
        private void CheckEndpointPair(
            (Alignment alignment, double startX, double startY, double endX, double endY) a,
            string aEnd,
            (Alignment alignment, double startX, double startY, double endX, double endY) b,
            string bEnd,
            double threshold,
            DrawingSummaryForAI summary,
            Dictionary<string, string> corridorLookup)
        {
            double ax = aEnd == "start" ? a.startX : a.endX;
            double ay = aEnd == "start" ? a.startY : a.endY;
            double bx = bEnd == "start" ? b.startX : b.endX;
            double by = bEnd == "start" ? b.startY : b.endY;

            double dist = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
            if (dist > threshold) return;

            // Already detected this intersection?
            double mx = (ax + bx) / 2, my = (ay + by) / 2;
            if (IsDuplicateIntersection(summary, a.alignment.Name, b.alignment.Name, mx, my, threshold))
                return;

            // Determine type: both starts → Fork, both ends → Merge, otherwise Fork
            string type = (aEnd == "start" && bEnd == "start") ? "Fork"
                        : (aEnd == "end" && bEnd == "end") ? "Merge"
                        : "Fork";

            double staA = aEnd == "start" ? a.alignment.StartingStation : a.alignment.EndingStation;
            double staB = bEnd == "start" ? b.alignment.StartingStation : b.alignment.EndingStation;

            double bearingA = GetBearingAtStation(a.alignment, staA);
            double bearingB = GetBearingAtStation(b.alignment, staB);
            double angle = NormalizeAngle(Math.Abs(bearingA - bearingB));

            var intersection = new IntersectionSummary
            {
                IntersectionType = type,
                AlignmentNames = new List<string> { a.alignment.Name, b.alignment.Name },
                X = Math.Round(mx, 2),
                Y = Math.Round(my, 2),
                IntersectionAngle = Math.Round(angle, 1),
                Approaches = new List<IntersectionApproach>
                {
                    new IntersectionApproach
                    {
                        AlignmentName = a.alignment.Name,
                        Station = Math.Round(staA, 2),
                        BearingDegrees = Math.Round(bearingA, 1),
                        IsEndpoint = true,
                        EndpointType = aEnd
                    },
                    new IntersectionApproach
                    {
                        AlignmentName = b.alignment.Name,
                        Station = Math.Round(staB, 2),
                        BearingDegrees = Math.Round(bearingB, 1),
                        IsEndpoint = true,
                        EndpointType = bEnd
                    }
                }
            };

            // Enrich with corridor info
            EnrichWithCorridorInfo(intersection, corridorLookup);
            summary.Intersections.Add(intersection);
        }

        /// <summary>
        /// Check if an alignment endpoint lies on the body of another alignment (T-junction).
        /// </summary>
        private void CheckEndpointOnAlignment(
            (Alignment alignment, double startX, double startY, double endX, double endY) endpointAlign,
            string whichEnd,
            (Alignment alignment, double startX, double startY, double endX, double endY) bodyAlign,
            double threshold,
            DrawingSummaryForAI summary,
            Dictionary<string, string> corridorLookup)
        {
            double px = whichEnd == "start" ? endpointAlign.startX : endpointAlign.endX;
            double py = whichEnd == "start" ? endpointAlign.startY : endpointAlign.endY;

            try
            {
                double station = 0, offset = 0;
                bodyAlign.alignment.StationOffset(px, py, ref station, ref offset);

                if (Math.Abs(offset) > threshold) return;

                // Make sure it's not near the body alignment's own endpoints
                double bodyStartDist = Math.Sqrt(
                    (px - bodyAlign.startX) * (px - bodyAlign.startX) +
                    (py - bodyAlign.startY) * (py - bodyAlign.startY));
                double bodyEndDist = Math.Sqrt(
                    (px - bodyAlign.endX) * (px - bodyAlign.endX) +
                    (py - bodyAlign.endY) * (py - bodyAlign.endY));

                if (bodyStartDist < threshold || bodyEndDist < threshold) return;

                // Get the actual point on the body alignment
                double bx = 0, by = 0;
                bodyAlign.alignment.PointLocation(station, 0, ref bx, ref by);

                if (IsDuplicateIntersection(summary, endpointAlign.alignment.Name, bodyAlign.alignment.Name, bx, by, threshold))
                    return;

                double staEp = whichEnd == "start" ? endpointAlign.alignment.StartingStation : endpointAlign.alignment.EndingStation;
                double bearingEp = GetBearingAtStation(endpointAlign.alignment, staEp);
                double bearingBody = GetBearingAtStation(bodyAlign.alignment, station);
                double angle = NormalizeAngle(Math.Abs(bearingEp - bearingBody));

                var intersection = new IntersectionSummary
                {
                    IntersectionType = "T",
                    AlignmentNames = new List<string> { endpointAlign.alignment.Name, bodyAlign.alignment.Name },
                    X = Math.Round(bx, 2),
                    Y = Math.Round(by, 2),
                    IntersectionAngle = Math.Round(angle, 1),
                    Approaches = new List<IntersectionApproach>
                    {
                        new IntersectionApproach
                        {
                            AlignmentName = endpointAlign.alignment.Name,
                            Station = Math.Round(staEp, 2),
                            BearingDegrees = Math.Round(bearingEp, 1),
                            IsEndpoint = true,
                            EndpointType = whichEnd
                        },
                        new IntersectionApproach
                        {
                            AlignmentName = bodyAlign.alignment.Name,
                            Station = Math.Round(station, 2),
                            BearingDegrees = Math.Round(bearingBody, 1),
                            IsEndpoint = false,
                            EndpointType = ""
                        }
                    }
                };

                EnrichWithCorridorInfo(intersection, corridorLookup);
                summary.Intersections.Add(intersection);
            }
            catch { } // StationOffset may throw if point is outside alignment range
        }

        /// <summary>
        /// Axis-aligned bounding box for an alignment, used as a cheap
        /// pre-filter before expensive station-offset projection.
        /// </summary>
        private readonly struct AlignmentBounds
        {
            public double MinX { get; }
            public double MaxX { get; }
            public double MinY { get; }
            public double MaxY { get; }

            public AlignmentBounds(double minX, double maxX, double minY, double maxY)
            {
                MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY;
            }
        }

        /// <summary>
        /// Approximate alignment AABB by sampling the start, end, and a
        /// fixed number of interior stations. Cheap (no exceptions can
        /// fire — every sampled station is on the alignment) and tight
        /// enough to reject geometrically distant alignment pairs.
        /// </summary>
        private static AlignmentBounds ComputeAlignmentBounds(Alignment alignment)
        {
            const int interiorSamples = 16;
            double startSta = alignment.StartingStation;
            double endSta = alignment.EndingStation;
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;

            void sample(double sta)
            {
                try
                {
                    double px = 0, py = 0;
                    alignment.PointLocation(sta, 0, ref px, ref py);
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;
                }
                catch { }
            }

            sample(startSta);
            sample(endSta);
            double range = endSta - startSta;
            if (range > 0)
            {
                for (int k = 1; k <= interiorSamples; k++)
                {
                    sample(startSta + range * k / (interiorSamples + 1));
                }
            }

            // Guard against the (unlikely) case where every sample threw.
            if (minX == double.PositiveInfinity)
            {
                minX = alignment.StartPoint.X;
                maxX = alignment.StartPoint.X;
                minY = alignment.StartPoint.Y;
                maxY = alignment.StartPoint.Y;
            }
            return new AlignmentBounds(minX, maxX, minY, maxY);
        }

        /// <summary>
        /// AABB-overlap test with a per-side margin. Two boxes overlap
        /// when neither's right edge is to the left of the other's left
        /// edge (similarly for vertical).
        /// </summary>
        private static bool BoundsOverlap(AlignmentBounds a, AlignmentBounds b, double margin)
        {
            return a.MinX - margin <= b.MaxX
                && a.MaxX + margin >= b.MinX
                && a.MinY - margin <= b.MaxY
                && a.MaxY + margin >= b.MinY;
        }

        /// <summary>
        /// Detect mid-alignment crossings by sampling one alignment and checking offset sign changes on the other.
        /// </summary>
        private void DetectCrossing(
            Alignment alignA, Alignment alignB,
            double threshold,
            AlignmentBounds boundsB,
            DrawingSummaryForAI summary,
            Dictionary<string, string> corridorLookup)
        {
            try
            {
                double sampleInterval = 20.0; // meters
                double startSta = alignA.StartingStation;
                double endSta = alignA.EndingStation;

                double prevOffset = double.NaN;
                double prevStation = startSta;
                double prevX = 0, prevY = 0;

                // Pad the test by `threshold` so we don't reject samples
                // that are slightly outside but still geometrically near
                // alignB — those are legitimate crossings the agent cares
                // about.
                double bMinX = boundsB.MinX - threshold;
                double bMaxX = boundsB.MaxX + threshold;
                double bMinY = boundsB.MinY - threshold;
                double bMaxY = boundsB.MaxY + threshold;

                for (double sta = startSta; sta <= endSta; sta += sampleInterval)
                {
                    double px = 0, py = 0;
                    alignA.PointLocation(sta, 0, ref px, ref py);

                    // Skip samples that can't possibly project onto alignB.
                    // Without this guard, alignB.StationOffset throws
                    // PointNotOnEntityException for every off-curve sample
                    // — thousands of expensive throws per drawing.
                    if (px < bMinX || px > bMaxX || py < bMinY || py > bMaxY)
                    {
                        prevOffset = double.NaN;
                        prevStation = sta;
                        continue;
                    }

                    try
                    {
                        double bStation = 0, bOffset = 0;
                        alignB.StationOffset(px, py, ref bStation, ref bOffset);

                        if (!double.IsNaN(prevOffset) && prevOffset * bOffset < 0)
                        {
                            // Sign change detected — refine with binary search
                            var (crossX, crossY, crossStaA, crossStaB) = RefineCrossing(
                                alignA, alignB, prevStation, sta, 0.5);

                            if (!IsDuplicateIntersection(summary, alignA.Name, alignB.Name, crossX, crossY, threshold))
                            {
                                double bearingA = GetBearingAtStation(alignA, crossStaA);
                                double bearingB = GetBearingAtStation(alignB, crossStaB);
                                double angle = NormalizeAngle(Math.Abs(bearingA - bearingB));

                                var intersection = new IntersectionSummary
                                {
                                    IntersectionType = "Cross",
                                    AlignmentNames = new List<string> { alignA.Name, alignB.Name },
                                    X = Math.Round(crossX, 2),
                                    Y = Math.Round(crossY, 2),
                                    IntersectionAngle = Math.Round(angle, 1),
                                    Approaches = new List<IntersectionApproach>
                                    {
                                        new IntersectionApproach
                                        {
                                            AlignmentName = alignA.Name,
                                            Station = Math.Round(crossStaA, 2),
                                            BearingDegrees = Math.Round(bearingA, 1),
                                            IsEndpoint = false,
                                            EndpointType = ""
                                        },
                                        new IntersectionApproach
                                        {
                                            AlignmentName = alignB.Name,
                                            Station = Math.Round(crossStaB, 2),
                                            BearingDegrees = Math.Round(bearingB, 1),
                                            IsEndpoint = false,
                                            EndpointType = ""
                                        }
                                    }
                                };

                                EnrichWithCorridorInfo(intersection, corridorLookup);
                                summary.Intersections.Add(intersection);
                            }
                        }

                        prevOffset = bOffset;
                        prevStation = sta;
                        prevX = px;
                        prevY = py;
                    }
                    catch
                    {
                        prevOffset = double.NaN;
                        prevStation = sta;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DetectCrossing error ({alignA.Name}, {alignB.Name}): {ex.Message}");
            }
        }

        /// <summary>
        /// Binary search to refine crossing point between two stations on alignment A.
        /// </summary>
        private (double x, double y, double stationA, double stationB) RefineCrossing(
            Alignment alignA, Alignment alignB,
            double staLow, double staHigh, double tolerance)
        {
            for (int iter = 0; iter < 20; iter++)
            {
                double staMid = (staLow + staHigh) / 2;
                double px = 0, py = 0;
                alignA.PointLocation(staMid, 0, ref px, ref py);

                double bStation = 0, bOffset = 0;
                alignB.StationOffset(px, py, ref bStation, ref bOffset);

                if (Math.Abs(bOffset) < tolerance)
                {
                    return (px, py, staMid, bStation);
                }

                // Determine which side the low end is on
                double lowPx = 0, lowPy = 0;
                alignA.PointLocation(staLow, 0, ref lowPx, ref lowPy);
                double lowBSta = 0, lowBOff = 0;
                alignB.StationOffset(lowPx, lowPy, ref lowBSta, ref lowBOff);

                if (lowBOff * bOffset > 0)
                    staLow = staMid;
                else
                    staHigh = staMid;
            }

            // Return best approximation
            double midSta = (staLow + staHigh) / 2;
            double fx = 0, fy = 0;
            alignA.PointLocation(midSta, 0, ref fx, ref fy);
            double fbSta = 0, fbOff = 0;
            alignB.StationOffset(fx, fy, ref fbSta, ref fbOff);
            return (fx, fy, midSta, fbSta);
        }

        /// <summary>
        /// Get the tangent bearing (in degrees, 0-360) at a station on an alignment.
        /// </summary>
        private double GetBearingAtStation(Alignment alignment, double station)
        {
            try
            {
                double delta = 1.0; // meters
                double sta1 = Math.Max(alignment.StartingStation, station - delta);
                double sta2 = Math.Min(alignment.EndingStation, station + delta);

                double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                alignment.PointLocation(sta1, 0, ref x1, ref y1);
                alignment.PointLocation(sta2, 0, ref x2, ref y2);

                double bearing = Math.Atan2(x2 - x1, y2 - y1) * (180.0 / Math.PI);
                if (bearing < 0) bearing += 360;
                return bearing;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Normalize an angle difference to 0-180 degrees.
        /// </summary>
        private double NormalizeAngle(double angleDeg)
        {
            angleDeg = angleDeg % 360;
            if (angleDeg > 180) angleDeg = 360 - angleDeg;
            return angleDeg;
        }

        /// <summary>
        /// Check if an intersection near (x, y) between the same alignment pair already exists.
        /// </summary>
        private bool IsDuplicateIntersection(DrawingSummaryForAI summary, string nameA, string nameB, double x, double y, double threshold)
        {
            foreach (var ix in summary.Intersections)
            {
                if (ix.AlignmentNames.Contains(nameA) && ix.AlignmentNames.Contains(nameB))
                {
                    double dist = Math.Sqrt((ix.X - x) * (ix.X - x) + (ix.Y - y) * (ix.Y - y));
                    if (dist < threshold * 2)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Enrich intersection with corridor info if a Junction/Roundabout corridor references these alignments.
        /// </summary>
        private void EnrichWithCorridorInfo(IntersectionSummary intersection, Dictionary<string, string> corridorLookup)
        {
            foreach (var name in intersection.AlignmentNames)
            {
                if (corridorLookup.TryGetValue(name, out var corridorName))
                {
                    intersection.CorridorName = corridorName;
                    // If corridor is a roundabout, override type
                    if (corridorName.ToLowerInvariant().Contains("כיכר") ||
                        corridorName.ToLowerInvariant().Contains("roundabout") ||
                        corridorName.ToLowerInvariant().Contains("מעגל"))
                    {
                        intersection.IntersectionType = "Roundabout";
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Enrich a detected intersection with curb-return geometry inferred
        /// from neighbouring alignment arcs (added 2026-05-04 per engineer
        /// feedback). Walks each alignment in the drawing and harvests:
        ///   • CurbReturnRadiiM — radii of arcs/SCS within ~30 m of the
        ///     intersection point (typical curb fillet zone).
        ///   • ApproachCurveMinRadiusM — smallest arc radius within 50 m
        ///     STATION distance from the approach station on the same
        ///     alignment (the approach radius §8.3 / Table 8.3 governs).
        /// Falls back gracefully when no arcs are present in range.
        /// </summary>
        private void EnrichWithCurbReturnGeometry(
            Transaction tr,
            CivilDocument civilDoc,
            IntersectionSummary intersection)
        {
            const double CURB_RETURN_RADIUS_M = 50.0;  // search bubble around intersection point
            const double APPROACH_STATION_RANGE_M = 75.0; // station window on each approach alignment

            try
            {
                var ix = intersection.X;
                var iy = intersection.Y;
                var radii = new List<double>();
                double? approachMin = null;

                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    Alignment? align;
                    try
                    {
                        align = tr.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
                    }
                    catch { continue; }
                    if (align == null) continue;

                    var isApproach = intersection.AlignmentNames.Contains(align.Name);
                    var approach = intersection.Approaches
                        .FirstOrDefault(p => p.AlignmentName == align.Name);

                    foreach (AlignmentEntity entity in align.Entities)
                    {
                        try
                        {
                            double radius;
                            double startSta, endSta;
                            double cx, cy;

                            switch (entity.EntityType)
                            {
                                case AlignmentEntityType.Arc:
                                    var arc = entity as AlignmentArc;
                                    if (arc == null) continue;
                                    radius = arc.Radius;
                                    startSta = arc.StartStation;
                                    endSta = arc.EndStation;
                                    cx = (arc.StartPoint.X + arc.EndPoint.X) / 2.0;
                                    cy = (arc.StartPoint.Y + arc.EndPoint.Y) / 2.0;
                                    break;

                                case AlignmentEntityType.SpiralCurveSpiral:
                                    var scs = entity as AlignmentSCS;
                                    if (scs?.Arc == null) continue;
                                    radius = scs.Arc.Radius;
                                    startSta = scs.Arc.StartStation;
                                    endSta = scs.Arc.EndStation;
                                    cx = (scs.Arc.StartPoint.X + scs.Arc.EndPoint.X) / 2.0;
                                    cy = (scs.Arc.StartPoint.Y + scs.Arc.EndPoint.Y) / 2.0;
                                    break;

                                default:
                                    continue;
                            }

                            // 1) Curb-return: arc midpoint near the intersection point.
                            var dx = cx - ix;
                            var dy = cy - iy;
                            var distM = Math.Sqrt(dx * dx + dy * dy);
                            if (distM <= CURB_RETURN_RADIUS_M && radius > 0)
                            {
                                radii.Add(Math.Round(radius, 1));
                            }

                            // 2) Approach curve: arc whose station overlaps the
                            // approach station window on the SAME alignment.
                            if (isApproach && approach != null && radius > 0)
                            {
                                var lo = approach.Station - APPROACH_STATION_RANGE_M;
                                var hi = approach.Station + APPROACH_STATION_RANGE_M;
                                bool overlaps = endSta >= lo && startSta <= hi;
                                if (overlaps && (approachMin == null || radius < approachMin))
                                {
                                    approachMin = Math.Round(radius, 1);
                                }
                            }
                        }
                        catch { /* skip malformed entity */ }
                    }
                }

                if (radii.Count > 0)
                {
                    intersection.CurbReturnRadiiM = radii;
                    intersection.MinCurbReturnRadiusM = radii.Min();
                }
                if (approachMin.HasValue)
                {
                    intersection.ApproachCurveMinRadiusM = approachMin.Value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"EnrichWithCurbReturnGeometry error: {ex.Message}");
            }
        }

        #endregion

        #region Profile Extraction

        private void ExtractProfilesSummary(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
                        if (alignment == null) continue;

                        foreach (ObjectId profileId in alignment.GetProfileIds())
                        {
                            try
                            {
                                var profile = tr.GetObject(profileId, OpenMode.ForRead) as Profile;
                                if (profile == null) continue;

                                var profInfo = new ProfileSummary
                                {
                                    Name = profile.Name,
                                    AlignmentName = alignment.Name,
                                    ProfileType = GetProfileType(profile)
                                };

                                // Find alignment speed segments for grade checking
                                List<SpeedSegmentInfo>? alignSpeedSegs = null;
                                var alignSummary = summary.Alignments.FirstOrDefault(a => a.Name == alignment.Name);
                                alignSpeedSegs = alignSummary?.SpeedSegments;

                                // PVI analysis
                                AnalyzeProfilePVIs(profile, profInfo, alignSpeedSegs);

                                // Only keep PVI details for design profiles — surface profiles just need statistics
                                if (profInfo.ProfileType is not ("design" or "layout"))
                                {
                                    profInfo.PviPoints = new List<PviPointInfo>();
                                    profInfo.VerticalCurves = new List<VerticalCurveInfo>();
                                }

                                summary.Profiles.Add(profInfo);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private string GetProfileType(Profile profile)
        {
            try
            {
                var typeProp = profile.GetType().GetProperty("ProfileType");
                if (typeProp != null)
                {
                    var val = typeProp.GetValue(profile);
                    if (val != null)
                    {
                        string typeStr = val.ToString() ?? "";
                        if (typeStr.Contains("Layout") || typeStr.Contains("FiniteGrade") || typeStr.Contains("FinishedGround"))
                            return "layout";
                        if (typeStr == "Design")
                            return "design";
                        if (typeStr.Contains("ExistingGround") || typeStr.Contains("EG"))
                            return "existing_ground";
                        if (typeStr.Contains("Surface"))
                            return "surface";
                    }
                }
            }
            catch { }

            // Fallback: name-based classification with Hebrew naming conventions
            // MK = מצב קיים (existing state), MM = מצב מתוכנן (planned/design state)
            try
            {
                var name = profile.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    var lower = name.ToLowerInvariant();
                    // Design profiles (Hebrew: MM = מצב מתוכנן = planned state)
                    if (lower.Contains("design") || lower.Contains("תכן")
                        || lower.StartsWith("mm-") || lower.StartsWith("mm "))
                        return "design";
                    // Existing ground profiles (Hebrew: MK = מצב קיים = existing state)
                    if (lower.Contains("exist") || lower.Contains("קיים")
                        || lower.StartsWith("mk-") || lower.StartsWith("mk ")
                        || lower.StartsWith("0-mk") || lower.Contains("taala"))
                        return "existing_ground";
                    if (lower.Contains("surface") || lower.Contains("משטח"))
                        return "surface";
                }
            }
            catch { }

            // Last resort: PVI heuristic
            // Design profiles have few PVIs (engineer-placed control points)
            // Surface-sampled profiles have many PVIs from dense terrain sampling
            try
            {
                var pvis = profile.PVIs;
                if (pvis != null)
                {
                    if (pvis.Count <= 20)
                        return "design";
                    if (pvis.Count > 50)
                        return "existing_ground";
                }
            }
            catch { }

            return "unknown";
        }

        private void AnalyzeProfilePVIs(Profile profile, ProfileSummary info, List<SpeedSegmentInfo>? speedSegments = null)
        {
            try
            {
                var pvis = profile.PVIs;
                if (pvis == null || pvis.Count == 0) return;

                info.PviCount = pvis.Count;

                double minGrade = double.MaxValue;
                double maxGrade = double.MinValue;
                double minElev = double.MaxValue;
                double maxElev = double.MinValue;

                // Store PVI data for detailed output
                var pviList = new List<(double station, double elevation)>();
                foreach (ProfilePVI pvi in pvis)
                {
                    pviList.Add((pvi.RawStation, pvi.Elevation));
                    if (pvi.Elevation < minElev) minElev = pvi.Elevation;
                    if (pvi.Elevation > maxElev) maxElev = pvi.Elevation;
                }

                // Calculate grades and populate detailed PVI list
                for (int i = 0; i < pviList.Count; i++)
                {
                    var pviInfo = new PviPointInfo
                    {
                        Station = Math.Round(pviList[i].station, 2),
                        Elevation = Math.Round(pviList[i].elevation, 3)
                    };

                    // Grade in (from previous PVI)
                    if (i > 0)
                    {
                        double dStation = pviList[i].station - pviList[i - 1].station;
                        if (Math.Abs(dStation) > 0.1)
                        {
                            double gradeIn = (pviList[i].elevation - pviList[i - 1].elevation) / dStation * 100.0;
                            pviInfo.GradeInPercent = Math.Round(gradeIn, 2);
                            if (gradeIn < minGrade) minGrade = gradeIn;
                            if (gradeIn > maxGrade) maxGrade = gradeIn;
                        }
                    }

                    // Grade out (to next PVI)
                    if (i < pviList.Count - 1)
                    {
                        double dStation = pviList[i + 1].station - pviList[i].station;
                        if (Math.Abs(dStation) > 0.1)
                        {
                            double gradeOut = (pviList[i + 1].elevation - pviList[i].elevation) / dStation * 100.0;
                            pviInfo.GradeOutPercent = Math.Round(gradeOut, 2);
                        }
                    }

                    info.PviPoints.Add(pviInfo);
                }

                if (minElev < double.MaxValue)
                {
                    info.ElevationMin = Math.Round(minElev, 2);
                    info.ElevationMax = Math.Round(maxElev, 2);
                }

                if (minGrade < double.MaxValue)
                {
                    info.MinGradePercent = Math.Round(minGrade, 2);
                    info.MaxGradePercent = Math.Round(maxGrade, 2);
                }

                // Compute flagged grades
                if (speedSegments != null && speedSegments.Count > 0 && pviList.Count >= 2)
                {
                    for (int i = 1; i < pviList.Count; i++)
                    {
                        double dStation = pviList[i].station - pviList[i - 1].station;
                        if (Math.Abs(dStation) < 0.1) continue;
                        double grade = (pviList[i].elevation - pviList[i - 1].elevation) / dStation * 100.0;

                        // Find speed segment covering this grade
                        double midStation = (pviList[i].station + pviList[i - 1].station) / 2;
                        SpeedSegmentInfo? seg = null;
                        foreach (var s in speedSegments)
                        {
                            if (midStation >= s.StartStation && midStation <= s.EndStation)
                            { seg = s; break; }
                        }
                        if (seg == null) seg = speedSegments[0];

                        if (seg.MaxGrade.HasValue && Math.Abs(grade) > seg.MaxGrade.Value)
                        {
                            if (info.FlaggedGrades == null) info.FlaggedGrades = new List<FlaggedGrade>();
                            info.FlaggedGrades.Add(new FlaggedGrade
                            {
                                FromStation = Math.Round(pviList[i - 1].station, 2),
                                ToStation = Math.Round(pviList[i].station, 2),
                                GradePercent = Math.Round(grade, 2),
                                MaxAllowedGrade = seg.MaxGrade.Value
                            });
                        }
                    }
                }

                // K-value analysis from vertical curves
                AnalyzeVerticalCurves(profile, info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AnalyzeProfilePVIs error: {ex.Message}");
            }
        }

        private void AnalyzeVerticalCurves(Profile profile, ProfileSummary info)
        {
            try
            {
                var entities = profile.Entities;
                if (entities == null) return;

                double minK = double.MaxValue;
                double maxK = 0;
                int curveCount = 0;

                foreach (ProfileEntity entity in entities)
                {
                    // Check for vertical curve entities (parabolic curves)
                    // Use reflection since type name may vary by Civil 3D version
                    string typeName = entity.GetType().Name;
                    if (typeName.Contains("Parabola") || typeName.Contains("Circular") ||
                        typeName.Contains("VerticalCurve") || typeName.Contains("ParaCurve"))
                    {
                        curveCount++;
                        try
                        {
                            // Try to get curve properties via reflection
                            var lengthProp = entity.GetType().GetProperty("Length");
                            var gradeInProp = entity.GetType().GetProperty("GradeIn");
                            var gradeOutProp = entity.GetType().GetProperty("GradeOut");
                            var pviStaProp = entity.GetType().GetProperty("PVIStation")
                                          ?? entity.GetType().GetProperty("Station");
                            var pviElevProp = entity.GetType().GetProperty("PVIElevation")
                                           ?? entity.GetType().GetProperty("Elevation");

                            double length = 0, gradeIn = 0, gradeOut = 0, pviSta = 0, pviElev = 0;

                            if (lengthProp != null)
                                length = Convert.ToDouble(lengthProp.GetValue(entity) ?? 0);
                            if (gradeInProp != null)
                                gradeIn = Convert.ToDouble(gradeInProp.GetValue(entity) ?? 0);
                            if (gradeOutProp != null)
                                gradeOut = Convert.ToDouble(gradeOutProp.GetValue(entity) ?? 0);
                            if (pviStaProp != null)
                                pviSta = Convert.ToDouble(pviStaProp.GetValue(entity) ?? 0);
                            if (pviElevProp != null)
                                pviElev = Convert.ToDouble(pviElevProp.GetValue(entity) ?? 0);

                            double gradeDiff = Math.Abs(gradeOut - gradeIn);
                            double k = gradeDiff > 0.001 ? length / gradeDiff : 0;

                            if (k > 0 && k < 10000)
                            {
                                if (k < minK) minK = k;
                                if (k > maxK) maxK = k;
                            }

                            // Determine curve type (crest = downward, sag = upward)
                            string curveType = gradeOut > gradeIn ? "Sag" : "Crest";

                            // Add detailed curve info
                            info.VerticalCurves.Add(new VerticalCurveInfo
                            {
                                PviStation = Math.Round(pviSta, 2),
                                PviElevation = Math.Round(pviElev, 3),
                                Length = Math.Round(length, 2),
                                KValue = Math.Round(k, 1),
                                Type = curveType,
                                GradeInPercent = Math.Round(gradeIn * 100, 2),  // Convert to percent
                                GradeOutPercent = Math.Round(gradeOut * 100, 2)
                            });
                        }
                        catch { }
                    }
                }

                info.VerticalCurveCount = curveCount;
                if (minK < double.MaxValue)
                {
                    info.MinKValue = Math.Round(minK, 1);
                    info.MaxKValue = Math.Round(maxK, 1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AnalyzeVerticalCurves error: {ex.Message}");
            }
        }

        #endregion

        #region Alignment Segmentation

        /// <summary>
        /// Segments each alignment into zones: standard, intersection_zone (~150m buffer), ramp.
        /// Uses detected intersections and ramp corridors.
        /// </summary>
        private static void SegmentAlignments(DrawingSummaryForAI summary)
        {
            const double IntersectionBuffer = 150.0; // meters on each side of intersection

            // Build ramp corridors by baseline alignment name
            var rampCorridors = new Dictionary<string, List<CorridorSummary>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in summary.Corridors)
            {
                if (c.CorridorType == "Ramp" && !string.IsNullOrEmpty(c.BaselineAlignment))
                {
                    if (!rampCorridors.ContainsKey(c.BaselineAlignment))
                        rampCorridors[c.BaselineAlignment] = new();
                    rampCorridors[c.BaselineAlignment].Add(c);
                }
            }

            foreach (var aln in summary.Alignments)
            {
                var zones = new List<(double start, double end, string type, string? name)>();

                // Intersection zones: find intersections involving this alignment
                foreach (var ix in summary.Intersections ?? new List<IntersectionSummary>())
                {
                    foreach (var approach in ix.Approaches ?? new List<IntersectionApproach>())
                    {
                        if (string.Equals(approach.AlignmentName, aln.Name, System.StringComparison.OrdinalIgnoreCase))
                        {
                            double sta = approach.Station;
                            double zoneStart = Math.Max(aln.StartStation, sta - IntersectionBuffer);
                            double zoneEnd = Math.Min(aln.EndStation, sta + IntersectionBuffer);
                            var ixName = string.Join("/", ix.AlignmentNames ?? new List<string>());
                            zones.Add((zoneStart, zoneEnd, "intersection_zone", ixName));
                        }
                    }
                }

                // Ramp zones: mark full alignment as ramp if classified as ramp corridor
                if (rampCorridors.TryGetValue(aln.Name, out var ramps))
                {
                    foreach (var ramp in ramps)
                    {
                        zones.Add((aln.StartStation, aln.EndStation, "ramp", ramp.Name));
                    }
                }

                if (zones.Count == 0)
                {
                    // Entire alignment is standard
                    aln.Segments = new List<AlignmentSegment>
                    {
                        new AlignmentSegment
                        {
                            SegmentType = "standard",
                            StartStation = Math.Round(aln.StartStation, 2),
                            EndStation = Math.Round(aln.EndStation, 2),
                        }
                    };
                    continue;
                }

                // Sort zones by start station and merge overlapping
                zones.Sort((a, b) => a.start.CompareTo(b.start));

                // Build segments by filling gaps with "standard"
                var segments = new List<AlignmentSegment>();
                double cursor = aln.StartStation;

                foreach (var (zStart, zEnd, zType, zName) in zones)
                {
                    if (zStart > cursor + 0.1)
                    {
                        segments.Add(new AlignmentSegment
                        {
                            SegmentType = "standard",
                            StartStation = Math.Round(cursor, 2),
                            EndStation = Math.Round(zStart, 2),
                        });
                    }

                    segments.Add(new AlignmentSegment
                    {
                        SegmentType = zType,
                        StartStation = Math.Round(Math.Max(zStart, cursor), 2),
                        EndStation = Math.Round(zEnd, 2),
                        IntersectionName = zType == "intersection_zone" ? zName : null,
                        RampCorridorName = zType == "ramp" ? zName : null,
                    });

                    cursor = Math.Max(cursor, zEnd);
                }

                // Trailing standard segment
                if (cursor < aln.EndStation - 0.1)
                {
                    segments.Add(new AlignmentSegment
                    {
                        SegmentType = "standard",
                        StartStation = Math.Round(cursor, 2),
                        EndStation = Math.Round(aln.EndStation, 2),
                    });
                }

                aln.Segments = segments;
            }
        }

        #endregion

        #region Corridor Extraction

        private void ExtractCorridorsSummary(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                foreach (ObjectId corridorId in civilDoc.CorridorCollection)
                {
                    try
                    {
                        var corridor = tr.GetObject(corridorId, OpenMode.ForRead) as Corridor;
                        if (corridor == null) continue;

                        var corrInfo = new CorridorSummary
                        {
                            Name = corridor.Name
                        };

                        // Analyze baselines and regions
                        double totalLength = 0;
                        var assembliesUsed = new HashSet<string>();
                        bool firstBaseline = true;

                        foreach (Baseline baseline in corridor.Baselines)
                        {
                            corrInfo.BaselineCount++;

                            try
                            {
                                var alignId = baseline.AlignmentId;
                                if (!alignId.IsNull)
                                {
                                    var align = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                                    if (align != null)
                                    {
                                        if (!corrInfo.AlignmentNames.Contains(align.Name))
                                            corrInfo.AlignmentNames.Add(align.Name);

                                        // Set primary baseline alignment (first baseline)
                                        if (firstBaseline)
                                            corrInfo.BaselineAlignment = align.Name;
                                    }
                                }

                                // Get baseline profile
                                var profileId = baseline.ProfileId;
                                if (!profileId.IsNull && firstBaseline)
                                {
                                    var profile = tr.GetObject(profileId, OpenMode.ForRead) as Profile;
                                    if (profile != null)
                                        corrInfo.BaselineProfile = profile.Name;
                                }
                            }
                            catch { }

                            firstBaseline = false;

                            foreach (BaselineRegion region in baseline.BaselineRegions)
                            {
                                corrInfo.RegionCount++;
                                totalLength += region.EndStation - region.StartStation;

                                try
                                {
                                    if (!region.AssemblyId.IsNull)
                                    {
                                        var assembly = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as Assembly;
                                        if (assembly != null)
                                            assembliesUsed.Add(assembly.Name);
                                    }
                                }
                                catch { }
                            }
                        }

                        corrInfo.TotalLength = Math.Round(totalLength, 2);
                        corrInfo.AssembliesUsed = assembliesUsed.ToList();

                        // Classify corridor type
                        corrInfo.CorridorType = ClassifyCorridorType(
                            corridor.Name, corridor.Description,
                            corrInfo.AlignmentNames, corrInfo.BaselineCount);

                        // Detect ditch/lane assemblies from assembly names
                        string assemblyNamesLower = string.Join(" ", assembliesUsed).ToLowerInvariant();
                        corrInfo.HasDitches = assemblyNamesLower.Contains("ditch") ||
                                              assemblyNamesLower.Contains("תעלה") ||
                                              assemblyNamesLower.Contains("ניקוז") ||
                                              assemblyNamesLower.Contains("drain") ||
                                              assemblyNamesLower.Contains("swale");
                        corrInfo.HasLanes = assemblyNamesLower.Contains("lane") ||
                                            assemblyNamesLower.Contains("נתיב") ||
                                            assemblyNamesLower.Contains("pave");

                        // Count corridor surfaces
                        try
                        {
                            corrInfo.SurfaceCount = corridor.CorridorSurfaces.Count;
                        }
                        catch { }

                        // Sample-line spacing — engineers asked for accurate
                        // "חתך N+X.X" running-distance refs in violation tables,
                        // and section spacing varies per project. We collect the
                        // sample-line stations for this corridor's baseline
                        // alignment and infer the typical spacing.
                        TryPopulateSectionSpacing(tr, civilDoc, corridor, corrInfo);

                        summary.Corridors.Add(corrInfo);
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion

        #region Feature Line Extraction

        private void ExtractFeatureLinesSummary(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                var extractor = new Extractors.FeatureLineExtractor();
                var featureLines = extractor.ExtractAll(tr, civilDoc);

                foreach (var fl in featureLines)
                {
                    var flSummary = new FeatureLineSummaryForAI
                    {
                        Name = fl.Name,
                        Layer = fl.Layer,
                        Length = Math.Round(fl.Length, 2),
                        IsClosed = fl.IsClosed,
                        IsRowBoundary = IsRowLayer(fl.Layer)
                    };

                    if (fl.ElevationRange != null)
                    {
                        flSummary.ElevationMin = Math.Round(fl.ElevationRange.Min, 2);
                        flSummary.ElevationMax = Math.Round(fl.ElevationRange.Max, 2);
                    }

                    summary.FeatureLines.Add(flSummary);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractFeatureLinesSummary error: {ex.Message}");
            }
        }

        private static bool IsRowLayer(string layerName)
        {
            string lower = (layerName ?? "").ToLowerInvariant();
            return lower.Contains("row") || lower.Contains("זכות דרך") ||
                   lower.Contains("גבול דרך") || lower.Contains("r.o.w") ||
                   lower.Contains("boundary") ||
                   (lower.Contains("right") && lower.Contains("way"));
        }

        /// <summary>
        /// Extract detailed corridor data (lane widths, max widths) using CorridorExtractor.
        /// </summary>
        private static readonly string[] ShoulderPatterns = { "shoulder", "shul", "שוליים" };

        private static bool IsShoulderSubassembly(string nameLower)
        {
            foreach (var p in ShoulderPatterns)
                if (nameLower.Contains(p)) return true;
            return false;
        }

        /// <summary>
        /// Compute adaptive sampling frequency based on corridor total length.
        /// </summary>
        private static double ComputeSamplingFrequency(double totalLength)
        {
            if (totalLength <= 1000) return 20;   // ≤1km → every 20m
            if (totalLength <= 5000) return 50;   // 1-5km → every 50m
            if (totalLength <= 20000) return 100;  // 5-20km → every 100m
            return 200;                             // >20km → every 200m
        }

        private void ExtractCorridorDetailedData(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                var corridorExtractor = new Extractors.CorridorExtractor();
                var corridorDataList = corridorExtractor.ExtractAll(tr, civilDoc);

                // Match extracted data to summary corridors by name
                foreach (var corrSummary in summary.Corridors)
                {
                    var corridorData = corridorDataList.FirstOrDefault(
                        cd => cd.Name == corrSummary.Name);

                    if (corridorData == null)
                        continue;

                    double maxLeftWidth = 0;
                    double maxRightWidth = 0;
                    var laneWidthMap = new Dictionary<string, LaneWidthInfo>();
                    var allSamples = new List<(double station, Models.CrossSectionSample xs)>();

                    foreach (var baseline in corridorData.Baselines)
                    {
                        foreach (var region in baseline.Regions)
                        {
                            foreach (var xs in region.SampleCrossSections)
                            {
                                allSamples.Add((xs.Station, xs));

                                // Track max corridor widths for ROW check
                                if (xs.LeftWidth > maxLeftWidth)
                                    maxLeftWidth = xs.LeftWidth;
                                if (xs.RightWidth > maxRightWidth)
                                    maxRightWidth = xs.RightWidth;

                                // Aggregate lane widths (use typical/median from cross-sections)
                                foreach (var lane in xs.Lanes)
                                {
                                    string key = $"{lane.Side}_{lane.SubassemblyName}";
                                    if (!laneWidthMap.ContainsKey(key))
                                    {
                                        laneWidthMap[key] = new LaneWidthInfo
                                        {
                                            Side = lane.Side,
                                            SubassemblyName = lane.SubassemblyName,
                                            Width = lane.Width,
                                            SlopePercent = lane.Slope
                                        };
                                    }
                                    else
                                    {
                                        var existing = laneWidthMap[key];
                                        existing.Width = Math.Round(
                                            (existing.Width + lane.Width) / 2.0, 3);
                                        if (lane.Slope.HasValue && existing.SlopePercent.HasValue)
                                            existing.SlopePercent = Math.Round(
                                                (existing.SlopePercent.Value + lane.Slope.Value) / 2.0, 2);
                                    }
                                }
                            }
                        }
                    }

                    if (maxLeftWidth > 0)
                        corrSummary.MaxLeftWidth = Math.Round(maxLeftWidth, 3);
                    if (maxRightWidth > 0)
                        corrSummary.MaxRightWidth = Math.Round(maxRightWidth, 3);

                    corrSummary.LaneWidths = laneWidthMap.Values.ToList();

                    // Build compact cross-section measurements (adaptive sampling)
                    if (allSamples.Count > 0)
                    {
                        allSamples.Sort((a, b) => a.station.CompareTo(b.station));
                        double totalLength = corrSummary.TotalLength > 0
                            ? corrSummary.TotalLength
                            : (allSamples.Last().station - allSamples.First().station);
                        double freq = ComputeSamplingFrequency(totalLength);

                        var measurements = new List<CrossSectionMeasurement>();
                        double nextStation = allSamples.First().station;

                        foreach (var (station, xs) in allSamples)
                        {
                            if (station < nextStation - 0.1 && measurements.Count > 0)
                                continue; // Skip until next sampling point

                            var m = new CrossSectionMeasurement
                            {
                                Station = Math.Round(station, 3),
                                TotalWidth = Math.Round(xs.TotalWidth, 3),
                                HasLeftDitch = xs.Ditches.Any(d => d.Side == "Left"),
                                HasRightDitch = xs.Ditches.Any(d => d.Side == "Right"),
                            };

                            // Lane widths and slopes per side
                            foreach (var lane in xs.Lanes)
                            {
                                if (lane.Side == "Left")
                                {
                                    m.LaneWidthLeft = Math.Round(lane.Width, 3);
                                    m.CrossSlopeLeft = lane.Slope.HasValue
                                        ? Math.Round(Math.Abs(lane.Slope.Value), 2) : null;
                                }
                                else if (lane.Side == "Right")
                                {
                                    m.LaneWidthRight = Math.Round(lane.Width, 3);
                                    m.CrossSlopeRight = lane.Slope.HasValue
                                        ? Math.Round(Math.Abs(lane.Slope.Value), 2) : null;
                                }
                            }

                            // Shoulder widths from links classified as shoulder
                            foreach (var link in xs.Links)
                            {
                                string subLower = (link.SubassemblyName ?? "").ToLowerInvariant();
                                if (!IsShoulderSubassembly(subLower) &&
                                    link.ClassifiedType != "shoulder")
                                    continue;

                                // Shoulder width = distance from lane edge to shoulder edge
                                double absOffset = Math.Abs(link.Offset);
                                if (link.Side == "Left" && (!m.ShoulderWidthLeft.HasValue || absOffset > m.ShoulderWidthLeft.Value))
                                    m.ShoulderWidthLeft = Math.Round(absOffset - xs.LeftWidth + xs.TotalWidth / 2.0, 3);
                                else if (link.Side == "Right" && (!m.ShoulderWidthRight.HasValue || absOffset > m.ShoulderWidthRight.Value))
                                    m.ShoulderWidthRight = Math.Round(absOffset - xs.RightWidth + xs.TotalWidth / 2.0, 3);
                            }

                            // Include raw subassembly info for first station only
                            // (agent uses SMALL LLM to classify these names once)
                            if (measurements.Count == 0)
                            {
                                var subs = new List<SubassemblyInfo>();
                                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var link in xs.Links)
                                {
                                    string name = link.SubassemblyName ?? "";
                                    string key = $"{link.Side}_{name}";
                                    if (string.IsNullOrEmpty(name) || seenNames.Contains(key))
                                        continue;
                                    seenNames.Add(key);

                                    // Compute width as distance from origin to outermost point
                                    double width = Math.Abs(link.Offset);
                                    subs.Add(new SubassemblyInfo
                                    {
                                        Name = name,
                                        Side = link.Side,
                                        Offset = Math.Round(link.Offset, 3),
                                        Width = Math.Round(width, 3),
                                        Slope = link.Slope.HasValue ? Math.Round(Math.Abs(link.Slope.Value), 2) : null,
                                    });
                                }
                                if (subs.Count > 0)
                                    m.Subassemblies = subs;
                            }

                            measurements.Add(m);
                            nextStation = station + freq;

                            if (measurements.Count >= 200) // Cap at 200 stations
                                break;
                        }

                        if (measurements.Count > 0)
                            corrSummary.CrossSectionMeasurements = measurements;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractCorridorDetailedData error: {ex.Message}");
            }
        }

        #endregion

        #region Pipe Network Extraction

        private void ExtractPipeNetworksSummary(Transaction tr, CivilDocument civilDoc, DrawingSummaryForAI summary)
        {
            try
            {
                foreach (ObjectId networkId in civilDoc.GetPipeNetworkIds())
                {
                    try
                    {
                        var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                        if (network == null) continue;

                        var netInfo = new PipeNetworkSummary
                        {
                            Name = network.Name
                        };

                        // Determine network type from name
                        string nameLower = network.Name.ToLowerInvariant();
                        if (nameLower.Contains("storm") || nameLower.Contains("drain") || nameLower.Contains("ניקוז"))
                            netInfo.Type = "Storm";
                        else if (nameLower.Contains("sanitary") || nameLower.Contains("sewer") || nameLower.Contains("ביוב"))
                            netInfo.Type = "Sanitary";
                        else if (nameLower.Contains("water") || nameLower.Contains("מים"))
                            netInfo.Type = "Water";
                        else if (nameLower.Contains("combined") || nameLower.Contains("משולב"))
                            netInfo.Type = "Combined";
                        else
                            netInfo.Type = network.Name;

                        // Count structures
                        double totalPipeLength = 0;
                        var pipeSizes = new Dictionary<string, int>();

                        foreach (ObjectId structId in network.GetStructureIds())
                        {
                            netInfo.StructureCount++;
                        }

                        foreach (ObjectId pipeId in network.GetPipeIds())
                        {
                            try
                            {
                                var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                                if (pipe != null)
                                {
                                    netInfo.PipeCount++;
                                    totalPipeLength += pipe.Length2DCenterToCenter;

                                    // Track pipe sizes
                                    string sizeKey = $"{Math.Round(pipe.InnerDiameterOrWidth * 1000)}mm";
                                    if (!pipeSizes.ContainsKey(sizeKey))
                                        pipeSizes[sizeKey] = 0;
                                    pipeSizes[sizeKey]++;
                                }
                            }
                            catch { }
                        }

                        netInfo.TotalPipeLength = Math.Round(totalPipeLength, 1);
                        netInfo.PipeSizes = pipeSizes;

                        summary.PipeNetworks.Add(netInfo);
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion

        #region Layers Summary

        private void ExtractLayersSummary(Database db, Transaction tr, DrawingSummaryForAI summary)
        {
            try
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // Count entities per layer
                var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ms)
                {
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead, false) is AcDb.Entity e)
                        {
                            string layer = e.Layer ?? "";
                            if (!layerCounts.ContainsKey(layer))
                                layerCounts[layer] = 0;
                            layerCounts[layer]++;
                        }
                    }
                    catch { }
                }

                // Build layer metadata lookup (color ACI + linetype)
                var layerProps = new Dictionary<string, (int colorAci, string linetype)>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId lid in lt)
                {
                    try
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        layerProps[ltr.Name] = (ltr.Color.ColorIndex, ltr.LinetypeObjectId != ObjectId.Null
                            ? ((LinetypeTableRecord)tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead)).Name
                            : "Continuous");
                    }
                    catch { }
                }

                // Build layer list: all classified layers + top 10 unclassified by entity count
                var allLayers = new List<LayerInfo>();
                var unclassifiedLayers = new List<LayerInfo>();

                foreach (var kv in layerCounts)
                {
                    var name = kv.Key;
                    var count = kv.Value;
                    layerProps.TryGetValue(name, out var props);
                    var classification = Services.Extraction.LayerCodeRegistry.Classify(name, props.colorAci, props.linetype);

                    var info = new LayerInfo
                    {
                        Name = name,
                        EntityCount = count,
                        ColorAci = props.colorAci,
                        Linetype = props.linetype,
                    };

                    if (classification != null)
                    {
                        info.ElementType = classification.ElementType;
                        info.ElementCategory = classification.Category;
                        info.NumericCode = classification.NumericCode;
                        allLayers.Add(info);
                    }
                    else
                    {
                        unclassifiedLayers.Add(info);
                    }
                }

                // Add top 10 unclassified by entity count
                allLayers.AddRange(unclassifiedLayers.OrderByDescending(l => l.EntityCount).Take(10));

                summary.Layers = allLayers
                    .OrderByDescending(l => l.ElementType != null) // classified first
                    .ThenByDescending(l => l.EntityCount)
                    .ToList();
            }
            catch { }
        }

        #endregion

        #region Helpers

        private string GetUnitsName(Database db)
        {
            return db.Insunits switch
            {
                UnitsValue.Millimeters => "Millimeters",
                UnitsValue.Centimeters => "Centimeters",
                UnitsValue.Meters => "Meters",
                UnitsValue.Feet => "Feet",
                UnitsValue.Inches => "Inches",
                _ => db.Insunits.ToString()
            };
        }

        private (ExtentsSummary?, int) GetDrawingExtents(Database db, Transaction tr)
        {
            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                int entityCount = 0;

                foreach (ObjectId id in ms)
                {
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead, false) is AcDb.Entity e)
                        {
                            entityCount++;
                            try
                            {
                                var ext = e.GeometricExtents;
                                if (ext.MinPoint.X < minX) minX = ext.MinPoint.X;
                                if (ext.MinPoint.Y < minY) minY = ext.MinPoint.Y;
                                if (ext.MaxPoint.X > maxX) maxX = ext.MaxPoint.X;
                                if (ext.MaxPoint.Y > maxY) maxY = ext.MaxPoint.Y;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (minX < double.MaxValue)
                {
                    return (new ExtentsSummary
                    {
                        MinX = Math.Round(minX, 1),
                        MinY = Math.Round(minY, 1),
                        MaxX = Math.Round(maxX, 1),
                        MaxY = Math.Round(maxY, 1)
                    }, entityCount);
                }
            }
            catch { }

            return (null, 0);
        }

        private string EstimateCoordinateSystem(ExtentsSummary? extents)
        {
            if (extents == null) return "Undetected";

            // Israel TM Grid
            if (extents.MinX > 100000 && extents.MinX < 300000 &&
                extents.MinY > 300000 && extents.MinY < 800000)
                return "Israel TM Grid (EPSG:2039)";

            // UTM
            if (extents.MinX > 100000 && extents.MinX < 900000 &&
                extents.MinY > 0 && extents.MinY < 10000000)
                return "UTM (zone unknown)";

            return "Undetected";
        }

        private T SafeGet<T>(Func<T> func, T defaultValue)
        {
            try { return func(); }
            catch { return defaultValue; }
        }

        /// <summary>
        /// Read sample-line stations for the corridor's baseline alignment(s)
        /// and infer the section spacing. Stations are emitted in `SectionStations`
        /// for irregular spacings; the typical interval is stored in
        /// `SectionSpacingM`. Both fields are optional — the agent falls back
        /// to a 50 m default when neither is populated.
        /// </summary>
        private void TryPopulateSectionSpacing(
            Transaction tr,
            CivilDocument civilDoc,
            Corridor corridor,
            CorridorSummary corrInfo)
        {
            try
            {
                var stations = new SortedSet<double>();

                foreach (Baseline baseline in corridor.Baselines)
                {
                    if (baseline.AlignmentId.IsNull) continue;
                    var align = tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) as Alignment;
                    if (align == null) continue;

                    ObjectIdCollection groupIds;
                    try { groupIds = align.GetSampleLineGroupIds(); }
                    catch { continue; }
                    if (groupIds == null) continue;

                    foreach (ObjectId groupId in groupIds)
                    {
                        if (groupId.IsNull) continue;
                        var group = tr.GetObject(groupId, OpenMode.ForRead) as SampleLineGroup;
                        if (group == null) continue;

                        foreach (ObjectId slId in group.GetSampleLineIds())
                        {
                            if (slId.IsNull) continue;
                            var sl = tr.GetObject(slId, OpenMode.ForRead) as SampleLine;
                            if (sl == null) continue;
                            stations.Add(Math.Round(sl.Station, 3));
                        }
                    }
                }

                if (stations.Count < 2) return;

                corrInfo.SectionStations = stations.ToList();

                // Infer spacing from the most common gap. Round to 0.1 m to
                // collapse tiny floating-point differences before mode selection.
                var gaps = new List<double>();
                double prev = double.NaN;
                foreach (var s in stations)
                {
                    if (!double.IsNaN(prev)) gaps.Add(Math.Round(s - prev, 1));
                    prev = s;
                }
                if (gaps.Count == 0) return;
                var modeGap = gaps
                    .GroupBy(g => g)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .First()
                    .Key;
                if (modeGap > 0) corrInfo.SectionSpacingM = modeGap;
            }
            catch
            {
                // Sample lines are optional — leave both fields null.
            }
        }

        /// <summary>
        /// Classify corridor type based on name keywords and baseline count.
        /// </summary>
        private string ClassifyCorridorType(string? corridorName, string? corridorDesc,
            List<string> alignmentNames, int baselineCount)
        {
            var roundaboutKw = new[] { "מעגל", "כיכר", "roundabout", "circle" };
            var junctionKw = new[] { "צומת", "junction", "intersection" };
            var rampKw = new[] { "רמפ", "מחלף", "ramp", "interchange", "slip" };

            string combined = ((corridorName ?? "") + " " + (corridorDesc ?? "") + " " +
                string.Join(" ", alignmentNames)).ToLowerInvariant();

            if (roundaboutKw.Any(k => combined.Contains(k)))
                return "Roundabout";
            if (junctionKw.Any(k => combined.Contains(k)))
                return "Junction";
            if (rampKw.Any(k => combined.Contains(k)))
                return "Ramp";
            if (baselineCount > 1)
                return "Junction";

            return "Road";
        }

        /// <summary>
        /// Extract design speed from text ONLY when there is an explicit speed indicator.
        /// Must have a clear keyword (מהירות, V=, km/h, speed=) — bare numbers are NOT speeds.
        /// </summary>
        private double? ExtractSpeedFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Only patterns with EXPLICIT speed indicators — no bare number matching
            var patterns = new[]
            {
                @"מהירות\s*[\-:=]?\s*(\d{2,3})",          // מהירות 60, מהירות=60
                @"(\d{2,3})\s*קמ""ש",                       // 60 קמ"ש
                @"(\d{2,3})\s*קמ/ש",                        // 60 קמ/ש
                @"[Vv][\-_=]\s*(\d{2,3})",                  // V=60, V-60, v=60 (requires separator)
                @"(\d{2,3})\s*[Kk][Mm]/?[Hh]",             // 60 km/h, 60 KMH
                @"[Ss][Pp][Ee][Ee][Dd][\-_:=]\s*(\d{2,3})", // speed=60 (requires separator)
            };
            // REMOVED: @"[\-_](\d{2,3})(?:[\-_]|$)" — too aggressive, matches alignment
            // names/IDs like "test_100" or "Road_70" that are NOT speed designations.

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(text, pattern);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int speed))
                {
                    if (speed >= 30 && speed <= 130)
                        return speed;
                }
            }

            return null;
        }

        // Design criteria population REMOVED — agent-side standards_israel.py is
        // the single source of truth for Israeli design standard values.
        // Plugin sends raw speed data only; agent computes thresholds.
        private static void PopulateDesignCriteria(SpeedSegmentInfo seg)
        {
            // Intentionally empty — standards are applied server-side only.
            // SpeedSegmentInfo fields (MinRadius, MaxGrade, etc.) remain null.
        }

        private void SaveDebugJson(string json, string prefix)
        {
            try
            {
                string debugFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Constants.AppConstants.AppDataFolder);

                if (!Directory.Exists(debugFolder))
                    Directory.CreateDirectory(debugFolder);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string debugFile = Path.Combine(debugFolder, $"{prefix}_{timestamp}.json");

                File.WriteAllText(debugFile, json);
                System.Diagnostics.Debug.WriteLine($"Debug JSON saved to {debugFile}");
            }
            catch { }
        }

        #endregion
    }

    #region Summary Models - Matching Agent Expected Schema

    /// <summary>
    /// Root model matching the MahodAI Agent expected schema.
    /// </summary>
    public class DrawingSummaryForAI
    {
        [JsonPropertyName("metadata")]
        public DrawingMetadataForAgent Metadata { get; set; } = new();

        [JsonPropertyName("statistics")]
        public DrawingStatisticsForAgent Statistics { get; set; } = new();

        [JsonPropertyName("surfaces")]
        public List<SurfaceSummary> Surfaces { get; set; } = new();

        [JsonPropertyName("alignments")]
        public List<AlignmentSummary> Alignments { get; set; } = new();

        [JsonPropertyName("profiles")]
        public List<ProfileSummary> Profiles { get; set; } = new();

        [JsonPropertyName("corridors")]
        public List<CorridorSummary> Corridors { get; set; } = new();

        [JsonPropertyName("pipe_networks")]
        public List<PipeNetworkSummary> PipeNetworks { get; set; } = new();

        [JsonPropertyName("feature_lines")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FeatureLineSummaryForAI>? FeatureLines { get; set; } = new();

        [JsonPropertyName("intersections")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<IntersectionSummary>? Intersections { get; set; } = new();

        [JsonPropertyName("layers")]
        public List<LayerInfo> Layers { get; set; } = new();

        [JsonPropertyName("quality_issues")]
        public List<string> QualityIssues { get; set; } = new();

        public void CalculateTotals()
        {
            // Calculate totals from surfaces
            Statistics.TotalPoints = Surfaces.Sum(s => s.PointCount);
            Statistics.TotalTriangles = Surfaces.Sum(s => s.TriangleCount);

            // Calculate elevation range from surfaces
            var surfacesWithElev = Surfaces.Where(s => s.ElevationMin.HasValue && s.ElevationMax.HasValue).ToList();
            if (surfacesWithElev.Count > 0)
            {
                Statistics.ZMin = surfacesWithElev.Min(s => s.ElevationMin!.Value);
                Statistics.ZMax = surfacesWithElev.Max(s => s.ElevationMax!.Value);
            }
        }
    }

    public class DrawingMetadataForAgent
    {
        [JsonPropertyName("drawing_name")]
        public string DrawingName { get; set; } = "";

        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("units")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Units { get; set; }

        [JsonPropertyName("coordinate_system")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CoordinateSystem { get; set; }

        [JsonPropertyName("dwg_version")]
        public string? DwgVersion { get; set; }
    }

    public class DrawingStatisticsForAgent
    {
        [JsonPropertyName("total_entities")]
        public int TotalEntities { get; set; }

        [JsonPropertyName("total_points")]
        public int TotalPoints { get; set; }

        [JsonPropertyName("total_triangles")]
        public int TotalTriangles { get; set; }

        [JsonPropertyName("z_min")]
        public double? ZMin { get; set; }

        [JsonPropertyName("z_max")]
        public double? ZMax { get; set; }
    }

    public class SurfaceSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";  // Tin, Grid, Volume

        [JsonPropertyName("point_count")]
        public int PointCount { get; set; }

        [JsonPropertyName("triangle_count")]
        public int TriangleCount { get; set; }

        [JsonPropertyName("elevation_min")]
        public double? ElevationMin { get; set; }

        [JsonPropertyName("elevation_max")]
        public double? ElevationMax { get; set; }

        [JsonPropertyName("slope_min_percent")]
        public double? SlopeMinPercent { get; set; }

        [JsonPropertyName("slope_max_percent")]
        public double? SlopeMaxPercent { get; set; }

        [JsonPropertyName("slope_mean_percent")]
        public double? SlopeMeanPercent { get; set; }

        // Volume surfaces
        [JsonPropertyName("cut_volume")]
        public double? CutVolume { get; set; }

        [JsonPropertyName("fill_volume")]
        public double? FillVolume { get; set; }

        [JsonPropertyName("net_volume")]
        public double? NetVolume { get; set; }
    }

    public class AlignmentSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("design_speed_kph")]
        public double? DesignSpeedKph { get; set; }

        [JsonPropertyName("speed_segments")]
        public List<SpeedSegmentInfo>? SpeedSegments { get; set; }

        [JsonPropertyName("min_radius")]
        public double? MinRadius { get; set; }

        [JsonPropertyName("max_radius")]
        public double? MaxRadius { get; set; }

        [JsonPropertyName("tangent_count")]
        public int TangentCount { get; set; }

        [JsonPropertyName("curve_count")]
        public int CurveCount { get; set; }

        [JsonPropertyName("spiral_count")]
        public int SpiralCount { get; set; }

        [JsonPropertyName("has_superelevation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HasSuperelevation { get; set; }

        [JsonPropertyName("profile_names")]
        public List<string> ProfileNames { get; set; } = new();

        // Detailed curve data
        [JsonPropertyName("curves")]
        public List<CurveInfo> Curves { get; set; } = new();

        // Superelevation data
        [JsonPropertyName("superelevation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SuperelevationInfo>? Superelevation { get; set; } = new();

        [JsonPropertyName("road_type_hint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RoadTypeHint { get; set; }

        [JsonPropertyName("flagged_curves")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FlaggedCurve>? FlaggedCurves { get; set; }

        [JsonPropertyName("segments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<AlignmentSegment>? Segments { get; set; }
    }

    public class AlignmentSegment
    {
        [JsonPropertyName("segment_type")]
        public string SegmentType { get; set; } = "standard"; // standard, intersection_zone, ramp

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("intersection_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IntersectionName { get; set; }

        [JsonPropertyName("ramp_corridor_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RampCorridorName { get; set; }
    }

    public class SpeedSegmentInfo
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("speed_kph")]
        public double SpeedKph { get; set; }

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("min_radius")]
        public double? MinRadius { get; set; }

        [JsonPropertyName("max_grade")]
        public double? MaxGrade { get; set; }

        [JsonPropertyName("min_k_crest")]
        public double? MinKCrest { get; set; }

        [JsonPropertyName("min_k_sag")]
        public double? MinKSag { get; set; }

        [JsonPropertyName("min_spiral_length")]
        public double? MinSpiralLength { get; set; }

        [JsonPropertyName("is_default_speed")]
        public bool IsDefaultSpeed { get; set; }
    }

    public class CurveInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";  // Arc, SCS, STS, etc.

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "";  // Left, Right

        [JsonPropertyName("spiral_in_length")]
        public double? SpiralInLength { get; set; }

        [JsonPropertyName("spiral_out_length")]
        public double? SpiralOutLength { get; set; }
    }

    public class SuperelevationInfo
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("left_slope_percent")]
        public double LeftSlopePercent { get; set; }

        [JsonPropertyName("right_slope_percent")]
        public double RightSlopePercent { get; set; }

        [JsonPropertyName("pivot_method")]
        public string PivotMethod { get; set; } = "";
    }

    public class ProfileSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("alignment_name")]
        public string AlignmentName { get; set; } = "";

        [JsonPropertyName("profile_type")]
        public string ProfileType { get; set; } = "";  // Design, ExistingGround, Surface

        [JsonPropertyName("pvi_count")]
        public int PviCount { get; set; }

        [JsonPropertyName("elevation_min")]
        public double? ElevationMin { get; set; }

        [JsonPropertyName("elevation_max")]
        public double? ElevationMax { get; set; }

        [JsonPropertyName("min_grade_percent")]
        public double? MinGradePercent { get; set; }

        [JsonPropertyName("max_grade_percent")]
        public double? MaxGradePercent { get; set; }

        [JsonPropertyName("vertical_curve_count")]
        public int VerticalCurveCount { get; set; }

        [JsonPropertyName("min_k_value")]
        public double? MinKValue { get; set; }

        [JsonPropertyName("max_k_value")]
        public double? MaxKValue { get; set; }

        // Detailed PVI data
        [JsonPropertyName("pvi_points")]
        public List<PviPointInfo> PviPoints { get; set; } = new();

        // Detailed vertical curves
        [JsonPropertyName("vertical_curves")]
        public List<VerticalCurveInfo> VerticalCurves { get; set; } = new();

        [JsonPropertyName("flagged_grades")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FlaggedGrade>? FlaggedGrades { get; set; }
    }

    public class PviPointInfo
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("elevation")]
        public double Elevation { get; set; }

        [JsonPropertyName("grade_in_percent")]
        public double? GradeInPercent { get; set; }

        [JsonPropertyName("grade_out_percent")]
        public double? GradeOutPercent { get; set; }
    }

    public class VerticalCurveInfo
    {
        [JsonPropertyName("pvi_station")]
        public double PviStation { get; set; }

        [JsonPropertyName("pvi_elevation")]
        public double PviElevation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("k_value")]
        public double KValue { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";  // Crest, Sag

        [JsonPropertyName("grade_in_percent")]
        public double GradeInPercent { get; set; }

        [JsonPropertyName("grade_out_percent")]
        public double GradeOutPercent { get; set; }
    }

    public class CorridorSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("corridor_type")]
        public string CorridorType { get; set; } = "Road";

        [JsonPropertyName("baseline_alignment")]
        public string BaselineAlignment { get; set; } = "";

        [JsonPropertyName("baseline_profile")]
        public string BaselineProfile { get; set; } = "";

        [JsonPropertyName("total_length")]
        public double TotalLength { get; set; }

        [JsonPropertyName("baseline_count")]
        public int BaselineCount { get; set; }

        [JsonPropertyName("region_count")]
        public int RegionCount { get; set; }

        [JsonPropertyName("surface_count")]
        public int SurfaceCount { get; set; }

        [JsonPropertyName("alignment_names")]
        public List<string> AlignmentNames { get; set; } = new();

        [JsonPropertyName("assemblies_used")]
        public List<string> AssembliesUsed { get; set; } = new();

        [JsonPropertyName("has_ditches")]
        public bool HasDitches { get; set; }

        [JsonPropertyName("has_lanes")]
        public bool HasLanes { get; set; }

        [JsonPropertyName("lane_widths")]
        public List<LaneWidthInfo> LaneWidths { get; set; } = new();

        [JsonPropertyName("max_left_width")]
        public double? MaxLeftWidth { get; set; }

        [JsonPropertyName("max_right_width")]
        public double? MaxRightWidth { get; set; }

        [JsonPropertyName("cross_section_measurements")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CrossSectionMeasurement>? CrossSectionMeasurements { get; set; }

        [JsonPropertyName("section_spacing_m")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SectionSpacingM { get; set; }

        [JsonPropertyName("section_stations")]
        public List<double> SectionStations { get; set; } = new();
    }

    public class CrossSectionMeasurement
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("lane_width_left")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? LaneWidthLeft { get; set; }

        [JsonPropertyName("lane_width_right")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? LaneWidthRight { get; set; }

        [JsonPropertyName("shoulder_width_left")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ShoulderWidthLeft { get; set; }

        [JsonPropertyName("shoulder_width_right")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ShoulderWidthRight { get; set; }

        [JsonPropertyName("cross_slope_left")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? CrossSlopeLeft { get; set; }

        [JsonPropertyName("cross_slope_right")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? CrossSlopeRight { get; set; }

        [JsonPropertyName("total_width")]
        public double TotalWidth { get; set; }

        [JsonPropertyName("has_left_ditch")]
        public bool HasLeftDitch { get; set; }

        [JsonPropertyName("has_right_ditch")]
        public bool HasRightDitch { get; set; }

        [JsonPropertyName("subassemblies")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SubassemblyInfo>? Subassemblies { get; set; }
    }

    public class SubassemblyInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("side")]
        public string Side { get; set; } = "";

        [JsonPropertyName("offset")]
        public double Offset { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("slope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Slope { get; set; }
    }

    public class LaneWidthInfo
    {
        [JsonPropertyName("side")]
        public string Side { get; set; } = "";

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("slope_percent")]
        public double? SlopePercent { get; set; }

        [JsonPropertyName("subassembly_name")]
        public string SubassemblyName { get; set; } = "";
    }

    public class FeatureLineSummaryForAI
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("layer")]
        public string Layer { get; set; } = "";

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("is_closed")]
        public bool IsClosed { get; set; }

        [JsonPropertyName("is_row_boundary")]
        public bool IsRowBoundary { get; set; }

        [JsonPropertyName("elevation_min")]
        public double? ElevationMin { get; set; }

        [JsonPropertyName("elevation_max")]
        public double? ElevationMax { get; set; }
    }

    public class PipeNetworkSummary
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";  // Storm, Sanitary, Combined

        [JsonPropertyName("structure_count")]
        public int StructureCount { get; set; }

        [JsonPropertyName("pipe_count")]
        public int PipeCount { get; set; }

        [JsonPropertyName("total_pipe_length")]
        public double TotalPipeLength { get; set; }

        [JsonPropertyName("pipe_sizes")]
        public Dictionary<string, int> PipeSizes { get; set; } = new();
    }

    public class LayerInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("entity_count")]
        public int EntityCount { get; set; }

        [JsonPropertyName("color_aci")]
        public int? ColorAci { get; set; }

        [JsonPropertyName("linetype")]
        public string? Linetype { get; set; }

        [JsonPropertyName("element_type")]
        public string? ElementType { get; set; }

        [JsonPropertyName("element_category")]
        public string? ElementCategory { get; set; }

        [JsonPropertyName("numeric_code")]
        public int? NumericCode { get; set; }
    }

    /// <summary>
    /// Summary of a detected alignment intersection.
    /// </summary>
    public class IntersectionSummary
    {
        [JsonPropertyName("intersection_type")]
        public string IntersectionType { get; set; } = "Cross"; // T, Cross, Fork, Merge, Roundabout

        [JsonPropertyName("alignment_names")]
        public List<string> AlignmentNames { get; set; } = new();

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("approaches")]
        public List<IntersectionApproach> Approaches { get; set; } = new();

        [JsonPropertyName("intersection_angle")]
        public double? IntersectionAngle { get; set; } // degrees, 0-180

        [JsonPropertyName("corridor_name")]
        public string? CorridorName { get; set; } // associated Junction corridor, if any

        // ── §8.3 enrichment (added 2026-05-04 per engineer feedback) ──
        // Curb-return radii inferred from arc/SCS elements on either approach
        // alignment within ~30 m of the intersection point. Empty list when
        // no fillet arcs are found near the junction.
        [JsonPropertyName("curb_return_radii_m")]
        public List<double> CurbReturnRadiiM { get; set; } = new();

        // The smallest curb return radius found near this intersection.
        // Used by the agent to compare against §8.3 / Table 8.3 minimum.
        [JsonPropertyName("min_curb_return_radius_m")]
        public double? MinCurbReturnRadiusM { get; set; }

        // The smallest horizontal-curve radius on either approach alignment
        // within 50 m of the intersection station. §8.3 imposes a minimum
        // approach-curve radius distinct from curb returns. Null when no
        // such curve exists in range.
        [JsonPropertyName("approach_curve_min_radius_m")]
        public double? ApproachCurveMinRadiusM { get; set; }
    }

    /// <summary>
    /// One alignment's approach to an intersection.
    /// </summary>
    public class IntersectionApproach
    {
        [JsonPropertyName("alignment_name")]
        public string AlignmentName { get; set; } = "";

        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("bearing_degrees")]
        public double BearingDegrees { get; set; } // tangent direction at intersection

        [JsonPropertyName("is_endpoint")]
        public bool IsEndpoint { get; set; } // true if start/end of alignment

        [JsonPropertyName("endpoint_type")]
        public string EndpointType { get; set; } = ""; // "start", "end", or "" for mid
    }

    public class FlaggedCurve
    {
        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        [JsonPropertyName("required_radius")]
        public double RequiredRadius { get; set; }

        [JsonPropertyName("speed_kph")]
        public double SpeedKph { get; set; }
    }

    public class FlaggedGrade
    {
        [JsonPropertyName("from_station")]
        public double FromStation { get; set; }

        [JsonPropertyName("to_station")]
        public double ToStation { get; set; }

        [JsonPropertyName("grade_percent")]
        public double GradePercent { get; set; }

        [JsonPropertyName("max_allowed_grade")]
        public double MaxAllowedGrade { get; set; }
    }

    /// <summary>
    /// Internal helper class for extents calculation.
    /// </summary>
    public class ExtentsSummary
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }

    #endregion
}
