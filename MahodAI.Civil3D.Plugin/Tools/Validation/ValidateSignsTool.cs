using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Extractors;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Validates road sign placement against Israeli standards.
    /// Checks: spacing, required signs for road features, size, height, placement side.
    /// </summary>
    public class ValidateSignsTool : DrawingToolBase
    {
        public override string Name => "validate_signs";
        public override string Description => "Heuristic (UNCERTIFIED) check of road-sign placement: spacing, curve-warning presence, speed-sign consistency, placement side. Not a sourced Israeli-standard compliance check — every result is quarantined (certified=false) and a design failing the checks returns outcome=rejected with typed violations; an incomplete check returns indeterminate, never a pass.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // P0-05: these thresholds are UNSOURCED heuristics, not a certified compliance
        // check. Every result is quarantined with this note until a versioned, traffic-
        // engineer-approved Israeli rules provider replaces them. The tool must NEVER report
        // a bare "passed" that reads as a certified compliance PASS.
        private const string QuarantineNote =
            "אזהרה: בדיקת התמרור מבוססת על ערכי ברירת מחדל לא מאומתים (מרווח 50 מ', סף עקומה 500 מ', צד ימין) שטרם עוגנו לתקן. אין להסתמך עליה כאישור תאימות. (P0-05: uncertified heuristic — pending a sourced Israeli sign-rules provider.)";

        // Minimum sign spacing in meters (Israeli standard)
        private const double MinSignSpacing = 50.0;
        
        // Required warning sign distance before hazard (meters, by speed)
        private static readonly Dictionary<int, double> WarningDistanceBySpeed = new()
        {
            { 40, 80 },  { 50, 100 }, { 60, 150 },
            { 70, 200 }, { 80, 250 }, { 90, 300 },
            { 100, 350 }, { 110, 400 }, { 120, 450 },
        };

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var designSpeedParam = GetIntParam(parameters, "design_speed_kph");
            var roadType = GetStringParam(parameters, "road_type") ?? "interurban";

            // Extract signs
            var extractor = new SignMarkingExtractor();
            var extractionResult = extractor.ExtractAll(tr, civilDoc, tr.TransactionManager.TopTransaction != null 
                ? HostApplicationServices.WorkingDatabase : null) as SignMarkingExtractionResult;

            if (extractionResult == null)
                return ToolResult.Fail("EXTRACTION_FAILED", "Failed to extract sign data");

            var signs = extractionResult.Signs;
            if (!string.IsNullOrEmpty(alignmentName))
                signs = signs.Where(s => s.AlignmentName.Equals(alignmentName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (signs.Count == 0)
            {
                // P0-05: "no signs found" is NOT a compliance PASS — the design context does
                // not prove none are required. Report NOT_EVALUATED, never passed=true.
                return ToolResult.Ok(new
                {
                    alignment_name = alignmentName ?? "all",
                    total_signs = 0,
                    violations = new List<object>(),
                    status = "not_evaluated",
                    certified = false,
                    passed = (bool?)null,
                    quarantine_note = QuarantineNote,
                    message = "No signs found — validation NOT evaluated (absence of signs is not proof of compliance).",
                });
            }

            int designSpeed = designSpeedParam ?? 80;
            var violations = new List<SignViolation>();

            // Check 1: Sign spacing
            CheckSignSpacing(signs, violations);

            // Check 2: Required signs for curves (warning signs). P0-05: if this check cannot
            // complete (exception), the whole validation is INDETERMINATE — never a silent PASS.
            bool curveCheckComplete = CheckCurveWarningSigns(signs, alignmentName, designSpeed, civilDoc, tr, violations);

            // Check 3: Speed limit sign consistency
            CheckSpeedLimitConsistency(signs, designSpeed, violations);

            // Check 4: Sign placement side
            CheckPlacementSide(signs, roadType, violations);

            if (!curveCheckComplete)
            {
                // P0-05: the curve-warning check threw. Do not report PASS on partial results.
                return ToolResult.Fail(
                    "VALIDATION_INDETERMINATE",
                    "Sign validation could not complete (curve-warning check failed); result is indeterminate, not a pass.");
            }

            bool passed = violations.Count == 0;
            var payload = new
            {
                alignment_name = alignmentName ?? "all",
                design_speed_kph = designSpeed,
                road_type = roadType,
                total_signs = signs.Count,
                signs_by_type = signs.GroupBy(s => s.SignType).ToDictionary(g => g.Key, g => g.Count()),
                violation_count = violations.Count,
                violations = violations,
                passed,
                certified = false,
                quarantine_note = QuarantineNote,
            };

            // P0-05/P0-01: a failed validation is a Rejected typed result carrying structured
            // violations — NOT an outer ToolResult.Ok whose payload quietly says passed=false.
            if (!passed)
            {
                return await Task.FromResult(ToolResult.Rejected(
                    "SIGN_VALIDATION_FAILED",
                    $"{violations.Count} sign violation(s) found (uncertified heuristic).",
                    MapViolations(violations),
                    data: payload));
            }

            return await Task.FromResult(ToolResult.Ok(payload));
        }

        /// <summary>Maps the tool's SignViolations to the typed P0-01 engineering channel.</summary>
        private static List<EngineeringViolation> MapViolations(List<SignViolation> violations)
        {
            var list = new List<EngineeringViolation>(violations.Count);
            foreach (var v in violations)
            {
                list.Add(new EngineeringViolation
                {
                    Code = v.Type,
                    Message = v.Message,
                    // Uncertified heuristic: surface as a warning, not a hard/sourced violation.
                    Severity = "warning",
                    Station = v.Station,
                    Requested = v.RequiredValue,
                    Achieved = v.ActualValue,
                });
            }
            return list;
        }

        private void CheckSignSpacing(List<RoadSignData> signs, List<SignViolation> violations)
        {
            var byAlignment = signs.GroupBy(s => s.AlignmentName);
            foreach (var group in byAlignment)
            {
                var sorted = group.OrderBy(s => s.Station).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    double spacing = sorted[i].Station - sorted[i - 1].Station;
                    if (spacing < MinSignSpacing && spacing > 0)
                    {
                        violations.Add(new SignViolation
                        {
                            Type = "MinSignSpacing",
                            Severity = "Warning",
                            Station = sorted[i].Station,
                            AlignmentName = group.Key,
                            Message = $"Signs too close: {spacing:F1}m apart (min {MinSignSpacing}m)",
                            ActualValue = spacing,
                            RequiredValue = MinSignSpacing,
                            SignCode = sorted[i].SignCode,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// P0-05: returns true only when the check actually completed. On any exception it
        /// returns false so the caller can report the whole validation INDETERMINATE instead
        /// of swallowing the error and yielding a false PASS.
        /// </summary>
        private bool CheckCurveWarningSigns(
            List<RoadSignData> signs, string? alignmentName,
            int designSpeed, CivilDocument civilDoc, Transaction tr,
            List<SignViolation> violations)
        {
            if (civilDoc == null) return true;

            double requiredDist = WarningDistanceBySpeed.TryGetValue(
                GetClosestSpeed(designSpeed), out var d) ? d : 200;

            try
            {
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                {
                    var alignment = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (alignment == null) continue;
                    if (!string.IsNullOrEmpty(alignmentName) && 
                        !alignment.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Find curves that should have warning signs
                    foreach (AlignmentEntity entity in alignment.Entities)
                    {
                        if (entity.EntityType == AlignmentEntityType.Arc)
                        {
                            var arc = entity as AlignmentArc;
                            if (arc == null) continue;

                            // Sharp curves need warning signs (401/402)
                            if (arc.Radius < 500) // Below 500m radius = needs warning
                            {
                                bool hasWarningSign = signs.Any(s =>
                                    s.AlignmentName == alignment.Name &&
                                    (s.SignCode == "401" || s.SignCode == "402") &&
                                    s.Station >= arc.StartStation - requiredDist - 50 &&
                                    s.Station <= arc.StartStation);

                                if (!hasWarningSign)
                                {
                                    violations.Add(new SignViolation
                                    {
                                        Type = "MissingCurveWarning",
                                        Severity = "Error",
                                        Station = arc.StartStation,
                                        AlignmentName = alignment.Name,
                                        Message = $"Missing curve warning sign (401/402) before curve at station {arc.StartStation:F0} (R={arc.Radius:F0}m). Should be placed {requiredDist:F0}m before.",
                                        RequiredValue = requiredDist,
                                    });
                                }
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // P0-05: DO NOT swallow to Debug and continue — that produced a false PASS.
                // Signal indeterminate so the tool refuses to report compliance.
                System.Diagnostics.Debug.WriteLine($"CheckCurveWarningSigns error: {ex.Message}");
                return false;
            }
        }

        private void CheckSpeedLimitConsistency(
            List<RoadSignData> signs, int designSpeed, List<SignViolation> violations)
        {
            var speedSigns = signs.Where(s => s.SignCode.StartsWith("2") && s.SignCode.Length == 3).ToList();

            foreach (var sign in speedSigns)
            {
                var signSpeedOpt = SpeedFromSignCode(sign.SignCode);
                if (signSpeedOpt.HasValue)
                {
                    int signSpeed = signSpeedOpt.Value;
                    if (signSpeed > designSpeed + 10)
                    {
                        violations.Add(new SignViolation
                        {
                            Type = "SpeedLimitExceedsDesign",
                            Severity = "Error",
                            Station = sign.Station,
                            AlignmentName = sign.AlignmentName,
                            Message = $"Speed limit sign ({signSpeed} km/h) exceeds design speed ({designSpeed} km/h)",
                            ActualValue = signSpeed,
                            RequiredValue = designSpeed,
                            SignCode = sign.SignCode,
                        });
                    }
                }
            }
        }

        private void CheckPlacementSide(
            List<RoadSignData> signs, string roadType, List<SignViolation> violations)
        {
            foreach (var sign in signs)
            {
                if (string.IsNullOrEmpty(sign.Side)) continue;

                // Regulatory and warning signs should be on the right side
                if ((sign.SignType == "regulatory" || sign.SignType == "warning") && sign.Side == "left")
                {
                    // Exception: on divided roads, left side is acceptable for median-placed signs
                    if (roadType != "urban" && roadType != "divided")
                    {
                        violations.Add(new SignViolation
                        {
                            Type = "WrongPlacementSide",
                            Severity = "Warning",
                            Station = sign.Station,
                            AlignmentName = sign.AlignmentName,
                            Message = $"Sign {sign.SignCode} ({sign.SignType}) placed on left side — should be on right",
                            SignCode = sign.SignCode,
                        });
                    }
                }
            }
        }

        private int GetClosestSpeed(int speed)
        {
            return WarningDistanceBySpeed.Keys
                .OrderBy(k => Math.Abs(k - speed))
                .FirstOrDefault();
        }

        /// <summary>
        /// P0-05: maps a 2xx speed-limit sign code to km/h using the plugin's own extractor
        /// dictionary (201→20, 202→30, … 211→120), i.e. (code−199)×10. The previous
        /// (code−200)×10 was 10 km/h low for every value. Returns null for non-2xx / malformed
        /// codes. This is an UNCERTIFIED heuristic (see QuarantineNote); the sourced rules
        /// provider will read the value from the sign attribute rather than the code number.
        /// </summary>
        internal static int? SpeedFromSignCode(string? signCode)
        {
            if (string.IsNullOrEmpty(signCode) || signCode.Length != 3 || signCode[0] != '2')
                return null;
            if (!int.TryParse(signCode, out int code))
                return null;
            return (code - 199) * 10;
        }
    }

    public class SignViolation
    {
        public string Type { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning";
        public double Station { get; set; }
        public string AlignmentName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public double ActualValue { get; set; }
        public double RequiredValue { get; set; }
        public string SignCode { get; set; } = string.Empty;
    }
}
