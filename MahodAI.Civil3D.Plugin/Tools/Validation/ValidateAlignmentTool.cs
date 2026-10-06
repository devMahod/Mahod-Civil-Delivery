using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Extracts raw alignment geometry measurements for standards comparison.
    /// Returns measured values only — NO hardcoded thresholds.
    /// The AI agent compares actual values against RAG-retrieved standards.
    /// </summary>
    public class ValidateAlignmentTool : DrawingToolBase
    {
        public override string Name => "validate_alignment";
        public override string Description => "Extracts alignment horizontal geometry measurements: curve radii, spiral lengths, tangent lengths, and design speed. Returns raw data for standards comparison.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var designSpeedParam = GetIntParam(parameters, "design_speed_kph");

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

            // Design speed: parameter from backend takes priority, fallback to alignment's own
            int? designSpeedKph = designSpeedParam ?? ReadAlignmentDesignSpeed(alignment);

            // Read ALL speed segments (multi-speed alignment support)
            var speedSegments = ReadAlignmentSpeedSegments(alignment);

            var result = new AlignmentValidationResult
            {
                AlignmentName = alignment.Name,
                DesignSpeedKph = designSpeedKph ?? 0,
                Length = Math.Round(alignment.Length, 2),
                StartStation = Math.Round(alignment.StartingStation, 2),
                EndStation = Math.Round(alignment.EndingStation, 2),
                Elements = new List<AlignmentElementMeasurement>(),
                Statistics = new AlignmentMeasurementStats(),
                SpeedSegments = speedSegments
            };

            // Extract raw measurements for each element
            double minRadius = double.MaxValue;
            double minSpiralLength = double.MaxValue;
            double minTangentLength = double.MaxValue;
            int curveCount = 0;
            int spiralCount = 0;
            int tangentCount = 0;

            int elementIndex = 0;
            foreach (AlignmentEntity entity in alignment.Entities)
            {
                ct.ThrowIfCancellationRequested();

                switch (entity.EntityType)
                {
                    case AlignmentEntityType.Arc:
                        var arc = entity as AlignmentArc;
                        if (arc != null)
                        {
                            result.Elements.Add(new AlignmentElementMeasurement
                            {
                                Index = elementIndex,
                                Type = "Arc",
                                Station = Math.Round(arc.StartStation, 2),
                                EndStation = Math.Round(arc.EndStation, 2),
                                Length = Math.Round(arc.Length, 2),
                                Radius = Math.Round(arc.Radius, 1),
                                Direction = arc.Clockwise ? "Right" : "Left",
                                DeltaAngle = Math.Round(arc.Delta * 180.0 / Math.PI, 3)
                            });
                            if (arc.Radius < minRadius) minRadius = arc.Radius;
                            curveCount++;
                        }
                        break;

                    case AlignmentEntityType.Spiral:
                        var spiral = entity as AlignmentSpiral;
                        if (spiral != null)
                        {
                            result.Elements.Add(new AlignmentElementMeasurement
                            {
                                Index = elementIndex,
                                Type = "Spiral",
                                Station = Math.Round(spiral.StartStation, 2),
                                EndStation = Math.Round(spiral.EndStation, 2),
                                Length = Math.Round(spiral.Length, 2),
                                SpiralType = spiral.SpiralDefinition.ToString()
                            });
                            if (spiral.Length < minSpiralLength) minSpiralLength = spiral.Length;
                            spiralCount++;
                        }
                        break;

                    case AlignmentEntityType.Line:
                        var line = entity as AlignmentLine;
                        if (line != null)
                        {
                            // Bearing in DEGREES (Civil 3D's AlignmentLine.Direction is radians).
                            // Needed by the agent's Table 5.5 deflection-angle filter so it
                            // can distinguish a real PI-without-curve from a near-tangent-tangent
                            // bend that doesn't require a horizontal curve at this design speed.
                            double bearingDeg = (line.Direction * 180.0 / Math.PI) % 360.0;
                            if (bearingDeg < 0) bearingDeg += 360.0;
                            result.Elements.Add(new AlignmentElementMeasurement
                            {
                                Index = elementIndex,
                                Type = "Tangent",
                                Station = Math.Round(line.StartStation, 2),
                                EndStation = Math.Round(line.EndStation, 2),
                                Length = Math.Round(line.Length, 4),
                                Bearing = Math.Round(bearingDeg, 3)
                            });
                            if (line.Length < minTangentLength) minTangentLength = line.Length;
                            tangentCount++;
                        }
                        break;

                    case AlignmentEntityType.SpiralCurveSpiral:
                        var scs = entity as AlignmentSCS;
                        if (scs != null)
                        {
                            // Report the SCS as a compound element with sub-measurements
                            var scsElement = new AlignmentElementMeasurement
                            {
                                Index = elementIndex,
                                Type = "SCS",
                                Station = Math.Round(scs.StartStation, 2),
                                EndStation = Math.Round(scs.EndStation, 2),
                                Length = Math.Round(scs.Length, 2),
                                Radius = Math.Round(scs.Arc.Radius, 1),
                                Direction = scs.Arc.Clockwise ? "Right" : "Left",
                                SpiralInLength = Math.Round(scs.SpiralIn.Length, 2),
                                SpiralOutLength = Math.Round(scs.SpiralOut.Length, 2),
                                ArcLength = Math.Round(scs.Arc.Length, 2),
                                DeltaAngle = Math.Round(scs.Arc.Delta * 180.0 / Math.PI, 3)
                            };
                            // Group total (tangent-to-tangent deflection) —
                            // the post-fix h_deflection re-check reads THIS
                            // tool and drops coverage for the whole alignment
                            // when an SCS lacks it. Reflection guard mirrors
                            // GetAlignmentGeometryTool (spiral sub-entity
                            // Delta is version-fickle across 2026/2027).
                            try
                            {
                                double? inDelta = ReadSubEntityDeltaRadians(scs.SpiralIn);
                                double? outDelta = ReadSubEntityDeltaRadians(scs.SpiralOut);
                                if (inDelta.HasValue && outDelta.HasValue)
                                    scsElement.TotalDeltaAngle = Math.Round(
                                        (inDelta.Value + scs.Arc.Delta + outDelta.Value) * (180.0 / Math.PI), 3);
                            }
                            catch { /* leave null */ }
                            result.Elements.Add(scsElement);

                            if (scs.Arc.Radius < minRadius) minRadius = scs.Arc.Radius;
                            if (scs.SpiralIn.Length < minSpiralLength) minSpiralLength = scs.SpiralIn.Length;
                            if (scs.SpiralOut.Length < minSpiralLength) minSpiralLength = scs.SpiralOut.Length;
                            curveCount++;
                            spiralCount += 2;
                        }
                        break;
                }

                elementIndex++;
            }

            // Fill statistics (raw measurements, no comparison)
            result.Statistics.TotalElements = elementIndex;
            result.Statistics.CurveCount = curveCount;
            result.Statistics.SpiralCount = spiralCount;
            result.Statistics.TangentCount = tangentCount;
            result.Statistics.MinRadius = minRadius < double.MaxValue ? Math.Round(minRadius, 1) : (double?)null;
            result.Statistics.MinSpiralLength = minSpiralLength < double.MaxValue ? Math.Round(minSpiralLength, 2) : (double?)null;
            result.Statistics.MinTangentLength = minTangentLength < double.MaxValue ? Math.Round(minTangentLength, 4) : (double?)null;

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Read the design speed from the alignment's DesignSpeeds collection.
        /// Returns the maximum speed found, or null if unavailable.
        /// </summary>
        private static int? ReadAlignmentDesignSpeed(Autodesk.Civil.DatabaseServices.Alignment alignment)
        {
            try
            {
                var speeds = alignment.DesignSpeeds;
                if (speeds != null && speeds.Count > 0)
                {
                    double maxSpeed = 0;
                    foreach (DesignSpeed ds in speeds)
                    {
                        if (ds.Value > maxSpeed)
                            maxSpeed = ds.Value;
                    }
                    if (maxSpeed > 0)
                        return (int)Math.Round(maxSpeed);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadAlignmentDesignSpeed error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Read ALL speed segments from the alignment's DesignSpeeds collection.
        /// Returns list of (station, speed) pairs for multi-speed alignment support.
        /// </summary>
        private static List<AlignmentSpeedSegment>? ReadAlignmentSpeedSegments(
            Autodesk.Civil.DatabaseServices.Alignment alignment)
        {
            try
            {
                var speeds = alignment.DesignSpeeds;
                if (speeds == null || speeds.Count <= 1)
                    return null; // Single speed or none — no need for segments

                var segments = new List<AlignmentSpeedSegment>();
                foreach (DesignSpeed ds in speeds)
                {
                    segments.Add(new AlignmentSpeedSegment
                    {
                        Station = Math.Round(ds.Station, 2),
                        SpeedKph = (int)Math.Round(ds.Value)
                    });
                }
                segments.Sort((a, b) => a.Station.CompareTo(b.Station));
                return segments.Count > 0 ? segments : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadAlignmentSpeedSegments error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Reads a spiral sub-entity's Delta (radians) via reflection —
        /// AlignmentSubEntitySpiral property availability varies across
        /// Civil 3D 2026/2027. Returns null when the property is missing
        /// or throws. Mirrors GetAlignmentGeometryTool.
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
    }

    #region Result Models

    public class AlignmentValidationResult
    {
        public string AlignmentName { get; set; } = string.Empty;
        public int DesignSpeedKph { get; set; }
        public double Length { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public List<AlignmentElementMeasurement> Elements { get; set; } = new();
        public AlignmentMeasurementStats? Statistics { get; set; }
        /// <summary>Speed segments for multi-speed alignments. Null if single speed.</summary>
        public List<AlignmentSpeedSegment>? SpeedSegments { get; set; }
    }

    public class AlignmentSpeedSegment
    {
        public double Station { get; set; }
        public int SpeedKph { get; set; }
    }

    public class AlignmentElementMeasurement
    {
        public int Index { get; set; }
        public string Type { get; set; } = string.Empty;
        public double Station { get; set; }
        public double EndStation { get; set; }
        public double Length { get; set; }
        public double? Radius { get; set; }
        public string? Direction { get; set; }
        public string? SpiralType { get; set; }
        public double? SpiralInLength { get; set; }
        public double? SpiralOutLength { get; set; }
        public double? ArcLength { get; set; }
        // Deflection (delta) angle in DEGREES (added 2026-07-07 for the Table
        // 5.5 curve-necessity check). Populated for Arc and SCS elements —
        // for SCS this is the CENTRAL ARC delta only; null for tangents and
        // standalone spirals.
        public double? DeltaAngle { get; set; }
        // SCS group total (spiral-in + arc + spiral-out, DEGREES) — the
        // tangent-to-tangent deflection Table 5.5 is stated against. Null on
        // releases whose spiral sub-entity doesn't expose Delta.
        public double? TotalDeltaAngle { get; set; }
        // Bearing in DEGREES (added 2026-05-04 for Table 5.5 deflection filter).
        // Populated for Tangent elements; null for arcs/spirals (they have a
        // changing bearing along their length — the deflection logic looks at
        // tangents only).
        public double? Bearing { get; set; }
    }

    public class AlignmentMeasurementStats
    {
        public int TotalElements { get; set; }
        public int CurveCount { get; set; }
        public int SpiralCount { get; set; }
        public int TangentCount { get; set; }
        public double? MinRadius { get; set; }
        public double? MinSpiralLength { get; set; }
        public double? MinTangentLength { get; set; }
    }

    // Keep DesignViolation and ValidationSummary for backward compatibility
    // (used by other tools that may reference them)
    public class DesignCriteria
    {
        public double MinRadius { get; set; }
        public double MinSpiralLength { get; set; }
        public double MinTangent { get; set; }
    }

    public class DesignViolation
    {
        public int ElementIndex { get; set; }
        public string ElementType { get; set; } = string.Empty;
        public double Station { get; set; }
        public string ViolationType { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning";
        public string Message { get; set; } = string.Empty;
        public double ActualValue { get; set; }
        public double RequiredValue { get; set; }
    }

    public class ValidationSummary
    {
        public int TotalElements { get; set; }
        public int ViolationCount { get; set; }
        public bool PassedValidation { get; set; }
    }

    #endregion
}
