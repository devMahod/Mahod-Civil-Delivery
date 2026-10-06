using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Extractors
{
    /// <summary>
    /// Extracts alignment data including horizontal curves, spirals, and profiles.
    /// Only performs data extraction - analysis is handled by Analyzers.
    /// </summary>
    public class AlignmentExtractor : IDataExtractor
    {
        public string ObjectType => "Alignment";
        public int Priority => 10;

        public bool IsAvailable(CivilDocument? civilDoc) => civilDoc != null;

        public object? ExtractAll(Transaction tr, CivilDocument? civilDoc, Database db)
        {
            if (civilDoc == null) return null;

            var result = new AlignmentExtractorResult();

            try
            {
                result.Alignments = ExtractAlignments(tr, civilDoc);
                result.AlignmentDetails = ExtractAlignmentDetails(tr, civilDoc);
                result.Profiles = ExtractProfiles(tr, civilDoc);
                result.SuperElevations = ExtractSuperElevations(tr, civilDoc);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AlignmentExtractor error: {ex.Message}");
            }

            return result;
        }

        public object? ExtractByIds(Transaction tr, IEnumerable<ObjectId> ids) => null;

        #region Alignment Extraction

        /// <summary>
        /// Count curves and spirals from an alignment entity (including compound curves)
        /// </summary>
        private void CountEntityCurvesAndSpirals(AlignmentEntity entity, ref int curveCount, ref int spiralCount, ref double minRadius)
        {
            switch (entity)
            {
                case AlignmentArc arc:
                    curveCount++;
                    if (arc.Radius < minRadius) minRadius = arc.Radius;
                    break;

                case AlignmentSpiral:
                    spiralCount++;
                    break;

                case AlignmentSCS scs:
                    // Spiral-Curve-Spiral: 1 arc + 2 spirals
                    curveCount++;
                    spiralCount += 2;
                    if (scs.Arc.Radius < minRadius) minRadius = scs.Arc.Radius;
                    break;

                case AlignmentSTS:
                    // Spiral-Tangent-Spiral: 2 spirals
                    spiralCount += 2;
                    break;
            }
        }

        private List<AlignmentInfo> ExtractAlignments(Transaction tr, CivilDocument civilDoc)
        {
            var list = new List<AlignmentInfo>();

            try
            {
                foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        if (tr.GetObject(alignId, OpenMode.ForRead, false) is Alignment alignment)
                        {
                            int curveCount = 0, spiralCount = 0;
                            double minRadius = double.MaxValue;

                            foreach (AlignmentEntity entity in alignment.Entities)
                            {
                                CountEntityCurvesAndSpirals(entity, ref curveCount, ref spiralCount, ref minRadius);
                            }

                            list.Add(new AlignmentInfo
                            {
                                Name = alignment.Name ?? string.Empty,
                                Length = alignment.Length,
                                MinRadius = minRadius < double.MaxValue ? minRadius : 0,
                                Style = alignment.StyleName ?? string.Empty,
                                CurveCount = curveCount,
                                SpiralCount = spiralCount
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract alignment {alignId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractAlignments error: {ex.Message}");
            }

            return list;
        }

        private List<AlignmentDetailData> ExtractAlignmentDetails(Transaction tr, CivilDocument civilDoc)
        {
            var list = new List<AlignmentDetailData>();

            try
            {
                foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        if (tr.GetObject(alignId, OpenMode.ForRead, false) is Alignment alignment)
                        {
                            var detail = new AlignmentDetailData
                            {
                                Name = alignment.Name ?? string.Empty,
                                Length = alignment.Length,
                                StartStation = alignment.StartingStation,
                                EndStation = alignment.EndingStation
                            };

                            double minRadius = double.MaxValue;

                            // Extract design speeds
                            try
                            {
                                var speeds = alignment.DesignSpeeds;
                                if (speeds != null)
                                {
                                    foreach (DesignSpeed ds in speeds)
                                    {
                                        if (!detail.DesignSpeeds.Contains(ds.Value))
                                            detail.DesignSpeeds.Add(ds.Value);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MahodAI] AlignmentExtractor extract design speeds: {ex.Message}");
                            }

                            // Extract horizontal curves (including compound curve types)
                            foreach (AlignmentEntity entity in alignment.Entities)
                            {
                                ExtractHorizontalEntity(entity, detail, ref minRadius);
                            }

                            detail.MinRadius = minRadius < double.MaxValue ? minRadius : 0;

                            // Extract vertical curves
                            ExtractVerticalCurves(tr, alignment, detail);

                            list.Add(detail);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract alignment details {alignId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractAlignmentDetails error: {ex.Message}");
            }

            return list;
        }

        /// <summary>
        /// Extract horizontal entity data, handling both simple and compound curve types
        /// </summary>
        private void ExtractHorizontalEntity(AlignmentEntity entity, AlignmentDetailData detail, ref double minRadius)
        {
            try
            {
                switch (entity)
                {
                    case AlignmentArc arc:
                        if (arc.Radius < minRadius) minRadius = arc.Radius;
                        detail.HorizontalCurves.Add(new HorizontalCurveData
                        {
                            Type = "Arc",
                            StartStation = arc.StartStation,
                            EndStation = arc.EndStation,
                            Length = arc.Length,
                            Radius = arc.Radius,
                            IsClockwise = arc.Clockwise,
                            Direction = arc.Clockwise ? "CW" : "CCW"
                        });
                        break;

                    case AlignmentSpiral spiral:
                        AddSpiralData(detail, spiral, "Spiral");
                        break;

                    case AlignmentSCS scs:
                        // Spiral-Curve-Spiral: extract all three components
                        AddSpiralData(detail, scs.SpiralIn, "SCS_SpiralIn");
                        if (scs.Arc.Radius < minRadius) minRadius = scs.Arc.Radius;
                        detail.HorizontalCurves.Add(new HorizontalCurveData
                        {
                            Type = "SCS_Arc",
                            StartStation = scs.Arc.StartStation,
                            EndStation = scs.Arc.EndStation,
                            Length = scs.Arc.Length,
                            Radius = scs.Arc.Radius,
                            IsClockwise = scs.Arc.Clockwise,
                            Direction = scs.Arc.Clockwise ? "CW" : "CCW"
                        });
                        AddSpiralData(detail, scs.SpiralOut, "SCS_SpiralOut");
                        break;

                    case AlignmentSTS sts:
                        // Spiral-Tangent-Spiral: extract spirals (tangent is just a line)
                        AddSpiralData(detail, sts.SpiralIn, "STS_SpiralIn");
                        detail.HorizontalCurves.Add(new HorizontalCurveData
                        {
                            Type = "STS_Tangent",
                            StartStation = sts.Tangent.StartStation,
                            EndStation = sts.Tangent.EndStation,
                            Length = sts.Tangent.Length
                        });
                        AddSpiralData(detail, sts.SpiralOut, "STS_SpiralOut");
                        break;

                    // AlignmentLine (tangent) - not a curve, but can track it
                    case AlignmentLine line:
                        // Tangents don't need to be in HorizontalCurves for curve analysis
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractHorizontalEntity error: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper to add standalone spiral data to the detail list
        /// </summary>
        private void AddSpiralData(AlignmentDetailData detail, AlignmentSpiral spiral, string type)
        {
            // Calculate A-value for clothoid: A = sqrt(L * R)
            // Use the finite radius (the one connected to the curve, not the tangent)
            double finiteRadius = GetFiniteRadius(spiral.RadiusIn, spiral.RadiusOut);
            double aValue = finiteRadius > 0 && spiral.Length > 0
                ? Math.Sqrt(spiral.Length * finiteRadius)
                : 0;

            detail.HorizontalCurves.Add(new HorizontalCurveData
            {
                Type = type,
                StartStation = spiral.StartStation,
                EndStation = spiral.EndStation,
                Length = spiral.Length,
                RadiusIn = spiral.RadiusIn,
                RadiusOut = spiral.RadiusOut,
                AValue = aValue,
                Direction = spiral.Direction == SpiralDirectionType.DirectionLeft ? "CCW" : "CW"
            });
        }

        /// <summary>
        /// Helper to add sub-entity spiral data to the detail list (from compound curves)
        /// </summary>
        private void AddSpiralData(AlignmentDetailData detail, AlignmentSubEntitySpiral spiral, string type)
        {
            // Calculate A-value for clothoid: A = sqrt(L * R)
            // Use the finite radius (the one connected to the curve, not the tangent)
            // RadiusIn=0 means entry from tangent, RadiusOut=0 means exit to tangent
            double finiteRadius = GetFiniteRadius(spiral.RadiusIn, spiral.RadiusOut);
            double aValue = finiteRadius > 0 && spiral.Length > 0
                ? Math.Sqrt(spiral.Length * finiteRadius)
                : 0;

            detail.HorizontalCurves.Add(new HorizontalCurveData
            {
                Type = type,
                StartStation = spiral.StartStation,
                EndStation = spiral.EndStation,
                Length = spiral.Length,
                RadiusIn = spiral.RadiusIn,
                RadiusOut = spiral.RadiusOut,
                AValue = aValue,
                Direction = spiral.Direction == SpiralDirectionType.DirectionLeft ? "CCW" : "CW"
            });
        }

        /// <summary>
        /// Get the finite radius from a spiral (the one connected to the curve)
        /// For entry spiral: RadiusIn=0/infinity, RadiusOut=curve radius
        /// For exit spiral: RadiusIn=curve radius, RadiusOut=0/infinity
        /// </summary>
        private double GetFiniteRadius(double radiusIn, double radiusOut)
        {
            // Both zero or infinity means invalid
            bool inValid = radiusIn > 0 && !double.IsInfinity(radiusIn);
            bool outValid = radiusOut > 0 && !double.IsInfinity(radiusOut);

            if (inValid && outValid)
            {
                // Both valid, use the smaller one (the curve end)
                return Math.Min(radiusIn, radiusOut);
            }
            if (inValid) return radiusIn;
            if (outValid) return radiusOut;
            return 0;
        }

        private void ExtractVerticalCurves(Transaction tr, Alignment alignment, AlignmentDetailData detail)
        {
            try
            {
                foreach (ObjectId pid in alignment.GetProfileIds())
                {
                    try
                    {
                        if (tr.GetObject(pid, OpenMode.ForRead, false) is Profile profile)
                        {
                            foreach (ProfileEntity entity in profile.Entities)
                            {
                                if (entity is ProfileCircular circular)
                                {
                                    detail.VerticalCurves.Add(new VerticalCurveData
                                    {
                                        Station = circular.StartStation,
                                        Length = circular.Length,
                                        GradeIn = circular.GradeIn * 100.0,
                                        GradeOut = circular.GradeOut * 100.0,
                                        CurveType = circular.CurveType == VerticalCurveType.Crest ? "Crest" : "Sag",
                                        Radius = circular.Radius,
                                        KValue = CalculateKValue(circular)
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract profile {pid}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ExtractVerticalCurves: {ex.Message}");
            }
        }

        private double CalculateKValue(ProfileCircular circular)
        {
            double gradeChange = Math.Abs(circular.GradeOut - circular.GradeIn) * 100.0;
            if (gradeChange > 0.01)
                return Math.Round(circular.Length / gradeChange, 1);
            return 0;
        }

        #endregion

        #region Profile Extraction

        private List<ProfileInfo> ExtractProfiles(Transaction tr, CivilDocument civilDoc)
        {
            var list = new List<ProfileInfo>();

            try
            {
                foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        if (tr.GetObject(alignId, OpenMode.ForRead, false) is Alignment alignment)
                        {
                            foreach (ObjectId pid in alignment.GetProfileIds())
                            {
                                try
                                {
                                    if (tr.GetObject(pid, OpenMode.ForRead, false) is Profile profile)
                                    {
                                        var info = ExtractProfileInfo(alignment.Name, profile);
                                        if (info != null)
                                            list.Add(info);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to extract profile {pid}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract alignment profiles {alignId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProfiles error: {ex.Message}");
            }

            return list;
        }

        private ProfileInfo? ExtractProfileInfo(string alignmentName, Profile profile)
        {
            try
            {
                var info = new ProfileInfo
                {
                    AlignmentName = alignmentName,
                    ProfileName = profile.Name ?? string.Empty,
                    ProfileType = ClassifyProfileType(profile)
                };

                double minGrade = double.MaxValue;
                double maxGrade = double.MinValue;

                foreach (ProfileEntity entity in profile.Entities)
                {
                    var segment = new ProfileSegmentInfo
                    {
                        StartStation = entity.StartStation,
                        EndStation = entity.EndStation,
                        Length = entity.Length
                    };

                    if (entity is ProfileTangent tangent)
                    {
                        segment.SegmentType = "Tangent";
                        segment.GradePercent = tangent.Grade * 100.0;

                        if (segment.GradePercent < minGrade) minGrade = segment.GradePercent;
                        if (segment.GradePercent > maxGrade) maxGrade = segment.GradePercent;
                    }
                    else if (entity is ProfileCircular circular)
                    {
                        segment.SegmentType = "VerticalCurve";
                        segment.VerticalCurveLength = circular.Length;
                        segment.GradeIn = circular.GradeIn * 100.0;
                        segment.GradeOut = circular.GradeOut * 100.0;
                        segment.GradePercent = (segment.GradeIn.Value + segment.GradeOut.Value) / 2;
                        segment.VerticalCurveType = circular.CurveType == VerticalCurveType.Crest ? "Crest" : "Sag";
                        segment.Radius = circular.Radius;
                        segment.PviStation = circular.PVIStation;
                        segment.PviElevation = circular.PVIElevation;
                        segment.KValue = CalculateKValue(circular);
                    }

                    info.Segments.Add(segment);
                }

                info.MinGradePercent = minGrade < double.MaxValue ? minGrade : 0;
                info.MaxGradePercent = maxGrade > double.MinValue ? maxGrade : 0;

                return info;
            }
            catch
            {
                return null;
            }
        }

        private string ClassifyProfileType(Profile profile)
        {
            // 1. Try reading the Civil 3D ProfileType enum directly
            try
            {
                var typeStr = profile.ProfileType.ToString();
                var normalized = Tools.Profile.ListProfilesTool.NormalizeProfileType(typeStr);
                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
            }
            catch { /* ProfileType may not be accessible on all profile types */ }

            // 2. Fall back to name-based heuristics
            var name = profile.Name;
            if (string.IsNullOrEmpty(name))
                return "unknown";

            var lower = name.ToLowerInvariant();
            if (lower.Contains("design") || lower.Contains("תכן")) return "design";
            if (lower.Contains("exist") || lower.Contains("קיים") || lower.StartsWith("mk")) return "existing_ground";
            if (lower.Contains("surface") || lower.Contains("משטח")) return "surface";
            if (lower.StartsWith("mm") || lower.Contains("layout") || lower.Contains("fg")) return "fg";

            // 3. Return the raw name as type rather than "Unknown"
            return lower;
        }

        #endregion

        #region Superelevation Extraction

        private List<SuperElevationInfo> ExtractSuperElevations(Transaction tr, CivilDocument civilDoc)
        {
            var list = new List<SuperElevationInfo>();

            try
            {
                foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
                {
                    try
                    {
                        if (tr.GetObject(alignId, OpenMode.ForRead, false) is Alignment alignment)
                        {
                            var superInfo = ExtractAlignmentSuperElevation(alignment);
                            if (superInfo != null && superInfo.Curves.Count > 0)
                            {
                                list.Add(superInfo);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract superelevation {alignId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSuperElevations error: {ex.Message}");
            }

            return list;
        }

        private SuperElevationInfo? ExtractAlignmentSuperElevation(Alignment alignment)
        {
            try
            {
                var info = new SuperElevationInfo
                {
                    AlignmentName = alignment.Name ?? string.Empty
                };

                // Check if superelevation curves exist
                var superCurves = alignment.SuperelevationCurves;
                if (superCurves == null || superCurves.Count == 0)
                    return null;

                foreach (var superCurve in superCurves)
                {
                    try
                    {
                        var curveData = new SuperElevationCurveData
                        {
                            CurveStation = superCurve.StartStation,
                            CurveRadius = GetCurveRadiusAtStation(alignment, superCurve.StartStation)
                        };

                        // Get critical stations for this curve
                        var criticalStations = superCurve.CriticalStations;
                        if (criticalStations != null)
                        {
                            double maxSuper = 0;

                            foreach (SuperelevationCriticalStation station in criticalStations)
                            {
                                try
                                {
                                    var stationData = new SuperElevationStation
                                    {
                                        Station = station.Station
                                    };

                                    // Try to get transition type/description
                                    try { stationData.StationType = station.TransitionRegionType.ToString(); }
                                    catch (Exception ex) { stationData.StationType = "Transition"; System.Diagnostics.Debug.WriteLine($"[MahodAI] SuperElevation get transition type: {ex.Message}"); }

                                    // Try to get slopes using reflection or available properties
                                    double leftSlope = 0;
                                    double rightSlope = 0;

                                    try
                                    {
                                        // Use dynamic to access properties that may vary by version
                                        dynamic dynStation = station;
                                        try { leftSlope = dynStation.LeftInsideLaneSlope; } catch { /* dynamic property access */ }
                                        try { rightSlope = dynStation.RightInsideLaneSlope; } catch { /* dynamic property access */ }

                                        // If inside lane not available, try outside
                                        if (leftSlope == 0 && rightSlope == 0)
                                        {
                                            try { leftSlope = dynStation.LeftOutsideLaneSlope; } catch { /* dynamic property access */ }
                                            try { rightSlope = dynStation.RightOutsideLaneSlope; } catch { /* dynamic property access */ }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MahodAI] SuperElevation get slopes: {ex.Message}");
                                    }

                                    stationData.LeftSlope = leftSlope * 100.0;
                                    stationData.RightSlope = rightSlope * 100.0;

                                    curveData.Stations.Add(stationData);

                                    // Track max superelevation
                                    double absLeft = Math.Abs(leftSlope * 100.0);
                                    double absRight = Math.Abs(rightSlope * 100.0);
                                    if (absLeft > maxSuper) maxSuper = absLeft;
                                    if (absRight > maxSuper) maxSuper = absRight;
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SuperElevation process critical station: {ex.Message}");
                                }
                            }

                            curveData.FullSuperRate = maxSuper;
                        }

                        // Add curve even if we couldn't get station details
                        info.Curves.Add(curveData);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] SuperElevation extract curve: {ex.Message}");
                    }
                }

                return info.Curves.Count > 0 ? info : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractAlignmentSuperElevation error: {ex.Message}");
                return null;
            }
        }

        private double GetCurveRadiusAtStation(Alignment alignment, double station)
        {
            try
            {
                foreach (AlignmentEntity entity in alignment.Entities)
                {
                    // Check entity type and get station range
                    if (entity is AlignmentArc arc)
                    {
                        if (station >= arc.StartStation && station <= arc.EndStation)
                            return arc.Radius;
                    }
                    else if (entity is AlignmentSCS scs)
                    {
                        if (station >= scs.StartStation && station <= scs.EndStation)
                            return scs.Arc.Radius;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetCurveRadiusAtStation: {ex.Message}");
            }
            return 0;
        }

        #endregion
    }

    /// <summary>
    /// Result container for AlignmentExtractor
    /// </summary>
    public class AlignmentExtractorResult
    {
        public List<AlignmentInfo> Alignments { get; set; } = new();
        public List<AlignmentDetailData> AlignmentDetails { get; set; } = new();
        public List<ProfileInfo> Profiles { get; set; } = new();
        public List<SuperElevationInfo> SuperElevations { get; set; } = new();
    }
}
