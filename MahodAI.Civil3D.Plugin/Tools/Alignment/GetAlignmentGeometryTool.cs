using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Gets detailed geometry for an alignment including curves, tangents, and spirals.
    /// </summary>
    public class GetAlignmentGeometryTool : DrawingToolBase
    {
        public override string Name => "get_alignment_geometry";
        public override string Description => "Gets detailed horizontal geometry for an alignment including all elements (lines, curves, spirals), PI points, and design parameters.";
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
            var includeElements = GetBoolParam(parameters, "include_elements", true);
            var includeSuperelevation = GetBoolParam(parameters, "include_superelevation", false);

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the alignment
            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);

            if (alignment == null)
            {
                return ToolResult.NotFound("Alignment", alignmentName);
            }

            var result = new AlignmentGeometryResult
            {
                Name = alignment.Name,
                Description = alignment.Description,
                Length = alignment.Length,
                StartStation = alignment.StartingStation,
                EndStation = alignment.EndingStation,
                StartPoint = new Point2D { X = alignment.StartPoint.X, Y = alignment.StartPoint.Y },
                EndPoint = new Point2D { X = alignment.EndPoint.X, Y = alignment.EndPoint.Y }
            };

            var warnings = new List<string>();

            // Build alignment context (speed-at-station + junction proximity).
            // Shares one cached DrawingSummary across all tool calls in the
            // batch via ``cache`` — without that, 5 parallel get_*_geometry
            // calls each ran a full extraction and saturated the 30 s tool
            // timeout (agent.log 2026-05-27T15:13:44 lines 107–115).
            var contextResolver = AlignmentContextResolver.For(alignment.Name, cache);

            // Extract elements
            if (includeElements)
            {
                result.Elements = ExtractElements(alignment, contextResolver, ct, warnings);
                result.Statistics = CalculateStatistics(result.Elements);
            }

            // Extract superelevation if requested
            if (includeSuperelevation)
            {
                result.Superelevation = ExtractSuperelevation(alignment, ct);
            }

            if (warnings.Count > 0)
                result.ExtractionWarnings = warnings;

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private List<AlignmentElementInfo> ExtractElements(
            Autodesk.Civil.DatabaseServices.Alignment alignment,
            AlignmentContextResolver contextResolver,
            CancellationToken ct,
            List<string> warnings)
        {
            var elements = new List<AlignmentElementInfo>();

            try
            {
                int index = 0;
                foreach (AlignmentEntity entity in alignment.Entities)
                {
                    ct.ThrowIfCancellationRequested();

                    var element = new AlignmentElementInfo
                    {
                        Index = index++,
                        EntityType = entity.EntityType.ToString()
                    };

                    // Extract type-specific properties with direct API access
                    switch (entity.EntityType)
                    {
                        case AlignmentEntityType.Line:
                            var line = (AlignmentLine)entity;
                            element.StartStation = line.StartStation;
                            element.EndStation = line.EndStation;
                            element.Length = line.Length;
                            element.StartPoint = new Point2D { X = line.StartPoint.X, Y = line.StartPoint.Y };
                            element.EndPoint = new Point2D { X = line.EndPoint.X, Y = line.EndPoint.Y };
                            element.Direction = line.Direction * (180.0 / Math.PI);
                            break;

                        case AlignmentEntityType.Arc:
                            var arc = (AlignmentArc)entity;
                            element.StartStation = arc.StartStation;
                            element.EndStation = arc.EndStation;
                            element.Length = arc.Length;
                            element.Radius = arc.Radius;
                            element.CenterPoint = new Point2D { X = arc.CenterPoint.X, Y = arc.CenterPoint.Y };
                            element.StartPoint = new Point2D { X = arc.StartPoint.X, Y = arc.StartPoint.Y };
                            element.EndPoint = new Point2D { X = arc.EndPoint.X, Y = arc.EndPoint.Y };
                            element.DeltaAngle = arc.Delta * (180.0 / Math.PI);
                            element.IsClockwise = arc.Clockwise;
                            element.ChordLength = arc.ChordLength;
                            break;

                        case AlignmentEntityType.Spiral:
                            var spiral = (AlignmentSpiral)entity;
                            element.StartStation = spiral.StartStation;
                            element.EndStation = spiral.EndStation;
                            element.Length = spiral.Length;
                            element.SpiralType = spiral.SpiralDefinition.ToString();
                            element.RadiusIn = spiral.RadiusIn;
                            element.RadiusOut = spiral.RadiusOut;
                            element.AValue = spiral.A;
                            element.StartPoint = new Point2D { X = spiral.StartPoint.X, Y = spiral.StartPoint.Y };
                            element.EndPoint = new Point2D { X = spiral.EndPoint.X, Y = spiral.EndPoint.Y };
                            // Extract additional spiral parameters via reflection
                            // (K, LongTangent, ShortTangent may be computed properties)
                            try
                            {
                                var spiralType = spiral.GetType();
                                var kProp = spiralType.GetProperty("K");
                                if (kProp != null)
                                    element.KValue = Convert.ToDouble(kProp.GetValue(spiral));
                                var ltProp = spiralType.GetProperty("LongTangent");
                                if (ltProp != null)
                                    element.LongTangent = Convert.ToDouble(ltProp.GetValue(spiral));
                                var stProp = spiralType.GetProperty("ShortTangent");
                                if (stProp != null)
                                    element.ShortTangent = Convert.ToDouble(stProp.GetValue(spiral));
                            }
                            catch { /* Properties may not be available in all configurations */ }
                            break;

                        case AlignmentEntityType.SpiralCurveSpiral:
                            var scs = (AlignmentSCS)entity;
                            element.StartStation = scs.StartStation;
                            element.EndStation = scs.EndStation;
                            element.Length = scs.Length;
                            element.Radius = scs.Arc.Radius;
                            element.SpiralInLength = scs.SpiralIn.Length;
                            element.SpiralOutLength = scs.SpiralOut.Length;
                            element.ArcLength = scs.Arc.Length;
                            element.DeltaAngle = scs.Arc.Delta * (180.0 / Math.PI);
                            element.IsClockwise = scs.Arc.Clockwise;
                            // Extract A values from spiral-in and spiral-out
                            try { element.AValue = scs.SpiralIn.A; } catch { }
                            try { element.SpiralOutAValue = scs.SpiralOut.A; } catch { }
                            // Total group deflection = spiral-in + central arc +
                            // spiral-out deltas. DeltaAngle above keeps its
                            // central-arc-only semantics for SCS. Spiral
                            // sub-entity Delta is version-fickle across
                            // 2026/2027 — read via reflection like
                            // K/LongTangent/ShortTangent; null when unavailable.
                            try
                            {
                                double? spiralInDelta = ReadSubEntityDeltaRadians(scs.SpiralIn);
                                double? spiralOutDelta = ReadSubEntityDeltaRadians(scs.SpiralOut);
                                if (spiralInDelta.HasValue && spiralOutDelta.HasValue)
                                    element.TotalDeltaAngle =
                                        (spiralInDelta.Value + scs.Arc.Delta + spiralOutDelta.Value) * (180.0 / Math.PI);
                            }
                            catch { /* leave null */ }
                            break;

                        default:
                            // Fallback: use reflection for unknown/compound entity types (SpiralLineSpiral, SpiralSpiral, etc.)
                            try
                            {
                                var entityType = entity.GetType();
                                var startStationProp = entityType.GetProperty("StartStation");
                                if (startStationProp != null)
                                    element.StartStation = Convert.ToDouble(startStationProp.GetValue(entity));
                                var endStationProp = entityType.GetProperty("EndStation");
                                if (endStationProp != null)
                                    element.EndStation = Convert.ToDouble(endStationProp.GetValue(entity));
                                var lengthProp = entityType.GetProperty("Length");
                                if (lengthProp != null)
                                    element.Length = Convert.ToDouble(lengthProp.GetValue(entity));
                            }
                            catch { }

                            // Try to extract spiral properties via reflection for compound entities
                            try
                            {
                                var entType = entity.GetType();
                                var spiralInProp = entType.GetProperty("SpiralIn");
                                var spiralOutProp = entType.GetProperty("SpiralOut");
                                if (spiralInProp != null)
                                {
                                    var spiralIn = spiralInProp.GetValue(entity);
                                    var lenProp = spiralIn?.GetType().GetProperty("Length");
                                    if (lenProp != null)
                                        element.SpiralInLength = Convert.ToDouble(lenProp.GetValue(spiralIn));
                                }
                                if (spiralOutProp != null)
                                {
                                    var spiralOut = spiralOutProp.GetValue(entity);
                                    var lenProp = spiralOut?.GetType().GetProperty("Length");
                                    if (lenProp != null)
                                        element.SpiralOutLength = Convert.ToDouble(lenProp.GetValue(spiralOut));
                                }
                            }
                            catch { }
                            break;
                    }

                    // Per-element AI-planner context. Speed is resolved at
                    // the element midpoint; junction proximity uses the
                    // same 150 m approach buffer the profile tool uses.
                    var mid = 0.5 * (element.StartStation + element.EndStation);
                    element.ResolvedDesignSpeedKph = contextResolver.ResolveSpeedKph(mid);
                    element.NearIntersection = contextResolver.NearestJunction(
                        element.StartStation, element.EndStation);

                    elements.Add(element);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractElements error: {ex.Message}");
                warnings.Add($"Element extraction error (partial data): {ex.Message}");
            }

            return elements;
        }

        /// <summary>
        /// Reads a spiral sub-entity's Delta (radians) via reflection —
        /// AlignmentSubEntitySpiral property availability varies across
        /// Civil 3D 2026/2027. Returns null when the property is missing
        /// or throws.
        /// </summary>
        private static double? ReadSubEntityDeltaRadians(object? subEntity)
        {
            if (subEntity == null) return null;
            try
            {
                var prop = subEntity.GetType().GetProperty("Delta");
                if (prop != null)
                    return Convert.ToDouble(prop.GetValue(subEntity));
            }
            catch { /* property missing / threw on this release */ }
            return null;
        }

        private List<SuperelevationPoint> ExtractSuperelevation(Autodesk.Civil.DatabaseServices.Alignment alignment, CancellationToken ct)
        {
            var points = new List<SuperelevationPoint>();

            try
            {
                // Method 1: Try direct access to SuperelevationCriticalStations
                // This throws ArgumentException when superelevation is not configured
                dynamic criticalStations;
                try
                {
                    criticalStations = alignment.SuperelevationCriticalStations;
                }
                catch (ArgumentException)
                {
                    // Superelevation not configured — expected
                    return points;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    return points;
                }
                if (criticalStations != null && criticalStations.Count > 0)
                {
                    foreach (var station in criticalStations)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            var superPoint = new SuperelevationPoint
                            {
                                Station = station.Station,
                                CriticalPointType = MapStationType(station.StationType)
                            };

                            TryGetSuperelevationSlopes(station, superPoint);
                            points.Add(superPoint);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error reading superelevation station: {ex.Message}");
                        }
                    }

                    // Remove overlapping critical stations between consecutive curves
                    // (ported from MahodCivilNet CAlignment.RemoveOverlaps)
                    if (points.Count > 1)
                    {
                        // Sort by station
                        points.Sort((a, b) => a.Station.CompareTo(b.Station));

                        // Remove duplicate stations (keep last value at each station)
                        var cleaned = new List<SuperelevationPoint>();
                        for (int i = 0; i < points.Count; i++)
                        {
                            // If next point has same station (within tolerance), skip current
                            if (i < points.Count - 1 &&
                                Math.Abs(points[i].Station - points[i + 1].Station) < 0.01)
                                continue;
                            cleaned.Add(points[i]);
                        }
                        points = cleaned;
                    }

                    return points;
                }

                // Method 2: Try to get superelevation curve manager via reflection
                var managerProp = alignment.GetType().GetProperty("SuperelevationCurveManager");
                if (managerProp != null)
                {
                    var curveManager = managerProp.GetValue(alignment);
                    if (curveManager != null)
                    {
                        var getCurvesMethod = curveManager.GetType().GetMethod("GetAllCurves");
                        if (getCurvesMethod != null)
                        {
                            var curves = getCurvesMethod.Invoke(curveManager, null) as System.Collections.IEnumerable;
                            if (curves != null)
                            {
                                foreach (var curve in curves)
                                {
                                    ct.ThrowIfCancellationRequested();

                                    try
                                    {
                                        var startStationProp = curve.GetType().GetProperty("StartStation");
                                        var endStationProp = curve.GetType().GetProperty("EndStation");

                                        if (startStationProp != null && endStationProp != null)
                                        {
                                            double startStation = Convert.ToDouble(startStationProp.GetValue(curve));
                                            double endStation = Convert.ToDouble(endStationProp.GetValue(curve));

                                            var startPoint = new SuperelevationPoint
                                            {
                                                Station = startStation,
                                                CriticalPointType = "CurveStart"
                                            };

                                            var endPoint = new SuperelevationPoint
                                            {
                                                Station = endStation,
                                                CriticalPointType = "CurveEnd"
                                            };

                                            // Try to get rate
                                            var rateProp = curve.GetType().GetProperty("SuperelevationRate");
                                            if (rateProp != null)
                                            {
                                                double rate = Convert.ToDouble(rateProp.GetValue(curve)) * 100.0;
                                                startPoint.LeftSlope = rate;
                                                startPoint.RightSlope = -rate;
                                                endPoint.LeftSlope = rate;
                                                endPoint.RightSlope = -rate;
                                            }

                                            points.Add(startPoint);
                                            points.Add(endPoint);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSuperelevation error: {ex.Message}");
            }

            // Remove overlapping critical stations (fallback path)
            if (points.Count > 1)
            {
                points.Sort((a, b) => a.Station.CompareTo(b.Station));

                var cleaned = new List<SuperelevationPoint>();
                for (int i = 0; i < points.Count; i++)
                {
                    if (i < points.Count - 1 &&
                        Math.Abs(points[i].Station - points[i + 1].Station) < 0.01)
                        continue;
                    cleaned.Add(points[i]);
                }
                points = cleaned;
            }

            return points;
        }

        private static string MapStationType(object stationType)
        {
            var typeStr = stationType?.ToString() ?? "";
            return typeStr switch
            {
                "FullSuperelevation" => "FullSuper",
                "LevelCrown" => "LevelCrown",
                "ReverseCrown" => "RC",
                "NormalCrown" => "NC",
                "BeginNormalShoulder" => "BeginNormalShoulder",
                "EndNormalShoulder" => "EndNormalShoulder",
                "LowShoulderMatch" => "LowShoulderMatch",
                "HighShoulderMatch" => "HighShoulderMatch",
                _ => typeStr
            };
        }

        private static void TryGetSuperelevationSlopes(object station, SuperelevationPoint superPoint)
        {
            try
            {
                var type = station.GetType();

                // Try various property names for left slopes
                var leftProps = new[] { "LeftLaneSlope", "LeftSlope", "InsideLaneSlope" };
                foreach (var propName in leftProps)
                {
                    var prop = type.GetProperty(propName);
                    if (prop != null)
                    {
                        superPoint.LeftSlope = Convert.ToDouble(prop.GetValue(station)) * 100.0;
                        break;
                    }
                }

                // Try various property names for right slopes
                var rightProps = new[] { "RightLaneSlope", "RightSlope", "OutsideLaneSlope" };
                foreach (var propName in rightProps)
                {
                    var prop = type.GetProperty(propName);
                    if (prop != null)
                    {
                        superPoint.RightSlope = Convert.ToDouble(prop.GetValue(station)) * 100.0;
                        break;
                    }
                }

                // Try to get transition type
                var transProp = type.GetProperty("TransitionType") ?? type.GetProperty("Transition");
                if (transProp != null)
                {
                    var val = transProp.GetValue(station);
                    if (val != null)
                        superPoint.TransitionType = val.ToString();
                }
            }
            catch { }
        }

        private AlignmentStatisticsInfo CalculateStatistics(List<AlignmentElementInfo> elements)
        {
            var stats = new AlignmentStatisticsInfo();

            foreach (var element in elements)
            {
                switch (element.EntityType)
                {
                    case "Line":
                        stats.TangentCount++;
                        stats.TotalTangentLength += element.Length;
                        break;

                    case "Arc":
                        stats.CurveCount++;
                        stats.TotalCurveLength += element.Length;
                        if (element.Radius.HasValue)
                        {
                            if (stats.MinRadius == 0 || element.Radius.Value < stats.MinRadius)
                                stats.MinRadius = element.Radius.Value;
                            if (element.Radius.Value > stats.MaxRadius)
                                stats.MaxRadius = element.Radius.Value;
                        }
                        break;

                    case "Spiral":
                        stats.SpiralCount++;
                        stats.TotalSpiralLength += element.Length;
                        break;

                    case "SpiralCurveSpiral":
                    case "SpiralLineSpiral":
                    case "SpiralSpiral":
                        stats.CompoundCurveCount++;
                        if (element.Radius.HasValue)
                        {
                            if (stats.MinRadius == 0 || element.Radius.Value < stats.MinRadius)
                                stats.MinRadius = element.Radius.Value;
                            if (element.Radius.Value > stats.MaxRadius)
                                stats.MaxRadius = element.Radius.Value;
                        }
                        break;
                }
            }

            return stats;
        }
    }

    #region Result Models

    public class AlignmentGeometryResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public double Length { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public Point2D? StartPoint { get; set; }
        public Point2D? EndPoint { get; set; }
        public List<AlignmentElementInfo>? Elements { get; set; }
        public AlignmentStatisticsInfo? Statistics { get; set; }
        public List<SuperelevationPoint>? Superelevation { get; set; }
        public List<string>? ExtractionWarnings { get; set; }
    }

    public class AlignmentElementInfo
    {
        public int Index { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double Length { get; set; }
        public Point2D? StartPoint { get; set; }
        public Point2D? EndPoint { get; set; }

        // Line properties
        public double? Direction { get; set; }

        // Arc properties
        public double? Radius { get; set; }
        public Point2D? CenterPoint { get; set; }
        public double? DeltaAngle { get; set; }
        public bool? IsClockwise { get; set; }
        public double? ChordLength { get; set; }

        // Spiral properties
        public string? SpiralType { get; set; }
        public double? RadiusIn { get; set; }
        public double? RadiusOut { get; set; }
        public double? AValue { get; set; }
        public double? KValue { get; set; }
        public double? LongTangent { get; set; }
        public double? ShortTangent { get; set; }

        // Compound curve properties
        public double? SpiralInLength { get; set; }
        public double? SpiralOutLength { get; set; }
        public double? SpiralOutAValue { get; set; }
        public double? ArcLength { get; set; }
        public double? TangentLength { get; set; }

        /// <summary>
        /// Total deflection (degrees) consumed by a SpiralCurveSpiral group:
        /// spiral-in + central arc + spiral-out deltas. DeltaAngle keeps its
        /// central-arc-only semantics for SCS. Null when a spiral sub-entity
        /// doesn't expose Delta on this Civil 3D release.
        /// </summary>
        public double? TotalDeltaAngle { get; set; }

        /// <summary>
        /// Design speed (km/h) resolved at the element midpoint from the
        /// alignment's speed_segments (falls back to the primary
        /// DesignSpeedKph). Lets the analyzer apply per-segment thresholds
        /// on multi-speed alignments without having to interpolate.
        /// </summary>
        [JsonPropertyName("resolved_design_speed_kph")]
        public double? ResolvedDesignSpeedKph { get; set; }

        /// <summary>
        /// Nearest detected junction within 150 m of the element's
        /// station range. Null when no junction is nearby. When present,
        /// signals to the analyzer that junction-specific thresholds
        /// (e.g. Vol 2 / Table 8.3 curb-return, approach-curve radii)
        /// apply on top of the open-road criteria.
        /// </summary>
        [JsonPropertyName("near_intersection")]
        public NearIntersection? NearIntersection { get; set; }
    }

    public class SuperelevationPoint
    {
        public double Station { get; set; }
        public double LeftSlope { get; set; }      // Percent
        public double RightSlope { get; set; }     // Percent
        public string? CriticalPointType { get; set; }  // NC, RC, FullSuper, LevelCrown
        public string? TransitionType { get; set; }
    }

    public class AlignmentStatisticsInfo
    {
        public int TangentCount { get; set; }
        public int CurveCount { get; set; }
        public int SpiralCount { get; set; }
        public int CompoundCurveCount { get; set; }
        public double TotalTangentLength { get; set; }
        public double TotalCurveLength { get; set; }
        public double TotalSpiralLength { get; set; }
        public double MinRadius { get; set; }
        public double MaxRadius { get; set; }
    }

    #endregion
}
