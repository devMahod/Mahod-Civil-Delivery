using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Extractors;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Validates road marking compliance against Israeli standards.
    /// Checks: center line type, edge lines, lane line patterns, stop lines, crosswalks.
    /// </summary>
    public class ValidateMarkingsTool : DrawingToolBase
    {
        public override string Name => "validate_markings";
        public override string Description => "Heuristic (UNCERTIFIED) check of road markings: center-line presence/pattern, edge lines, colour. Not a sourced Israeli-standard compliance check — every result is quarantined (certified=false); a design failing the checks returns outcome=rejected with typed violations.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // P0-05: unsourced heuristics, quarantined until a versioned rules provider replaces them.
        private const string QuarantineNote =
            "אזהרה: בדיקת הסימונים מבוססת על כללי ברירת מחדל לא מאומתים ואינה אישור תאימות לתקן. (P0-05: uncertified heuristic — pending a sourced Israeli marking-rules provider.)";

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var designSpeedParam = GetIntParam(parameters, "design_speed_kph");
            var roadType = GetStringParam(parameters, "road_type") ?? "interurban";

            var extractor = new SignMarkingExtractor();
            var extractionResult = extractor.ExtractAll(tr, civilDoc,
                HostApplicationServices.WorkingDatabase) as SignMarkingExtractionResult;

            if (extractionResult == null)
                return ToolResult.Fail("EXTRACTION_FAILED", "Failed to extract marking data");

            var markings = extractionResult.Markings;
            if (!string.IsNullOrEmpty(alignmentName))
                markings = markings.Where(m => m.AlignmentName.Equals(alignmentName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (markings.Count == 0)
            {
                // P0-05: absence of markings is not proof of compliance — NOT_EVALUATED.
                return ToolResult.Ok(new
                {
                    alignment_name = alignmentName ?? "all",
                    total_markings = 0,
                    violations = new List<object>(),
                    status = "not_evaluated",
                    certified = false,
                    passed = (bool?)null,
                    quarantine_note = QuarantineNote,
                    message = "No markings found — validation NOT evaluated.",
                });
            }

            int designSpeed = designSpeedParam ?? 80;
            var violations = new List<MarkingViolation>();

            // Check 1: Center line exists
            CheckCenterLineExists(markings, alignmentName, roadType, violations);

            // Check 2: Edge lines for high-speed roads
            CheckEdgeLines(markings, alignmentName, designSpeed, roadType, violations);

            // Check 3: Center line pattern matches road type
            CheckCenterLinePattern(markings, roadType, violations);

            // Check 4: Marking color compliance
            CheckMarkingColors(markings, violations);

            bool passed = violations.Count == 0;
            var payload = new
            {
                alignment_name = alignmentName ?? "all",
                design_speed_kph = designSpeed,
                road_type = roadType,
                total_markings = markings.Count,
                markings_by_type = markings.GroupBy(m => m.MarkingType).ToDictionary(g => g.Key, g => g.Count()),
                violation_count = violations.Count,
                violations = violations,
                passed,
                certified = false,
                quarantine_note = QuarantineNote,
            };

            // P0-05/P0-01: a failed validation is a Rejected typed result, not Ok-with-passed=false.
            if (!passed)
            {
                var mapped = new List<EngineeringViolation>(violations.Count);
                foreach (var v in violations)
                {
                    mapped.Add(new EngineeringViolation
                    {
                        Code = v.Type,
                        Message = v.Message,
                        Severity = "warning",
                        Station = v.Station,
                    });
                }
                return await Task.FromResult(ToolResult.Rejected(
                    "MARKING_VALIDATION_FAILED",
                    $"{violations.Count} marking violation(s) found (uncertified heuristic).",
                    mapped,
                    data: payload));
            }

            return await Task.FromResult(ToolResult.Ok(payload));
        }

        private void CheckCenterLineExists(
            List<RoadMarkingData> markings, string? alignmentName,
            string roadType, List<MarkingViolation> violations)
        {
            // Non-urban roads with 2+ lanes should have center lines
            if (roadType == "urban") return;

            var alignments = markings.Select(m => m.AlignmentName).Distinct();
            foreach (var name in alignments)
            {
                if (!string.IsNullOrEmpty(alignmentName) && name != alignmentName)
                    continue;

                bool hasCenterLine = markings.Any(m => 
                    m.AlignmentName == name && 
                    m.MarkingType == "center_line");

                if (!hasCenterLine)
                {
                    violations.Add(new MarkingViolation
                    {
                        Type = "MissingCenterLine",
                        Severity = "Error",
                        AlignmentName = name,
                        Message = $"Missing center line marking for {name}",
                    });
                }
            }
        }

        private void CheckEdgeLines(
            List<RoadMarkingData> markings, string? alignmentName,
            int designSpeed, string roadType, List<MarkingViolation> violations)
        {
            // Roads with speed >= 60 km/h should have edge lines
            if (designSpeed < 60) return;

            var alignments = markings.Select(m => m.AlignmentName).Distinct();
            foreach (var name in alignments)
            {
                if (!string.IsNullOrEmpty(alignmentName) && name != alignmentName)
                    continue;

                bool hasEdgeLines = markings.Any(m =>
                    m.AlignmentName == name &&
                    m.MarkingType == "edge_line");

                if (!hasEdgeLines)
                {
                    violations.Add(new MarkingViolation
                    {
                        Type = "MissingEdgeLines",
                        Severity = "Warning",
                        AlignmentName = name,
                        Message = $"Missing edge line markings for {name} (required for speeds >= 60 km/h)",
                    });
                }
            }
        }

        private void CheckCenterLinePattern(
            List<RoadMarkingData> markings, string roadType,
            List<MarkingViolation> violations)
        {
            var centerLines = markings.Where(m => m.MarkingType == "center_line").ToList();

            foreach (var cl in centerLines)
            {
                // Undivided interurban roads should have double solid center lines in no-passing zones
                // Single dashed center lines indicate passing is allowed
                // This is a basic check — full check requires sight distance analysis
                if (roadType == "interurban" && cl.Pattern == "dashed")
                {
                    // Not necessarily wrong, but flag for review
                    violations.Add(new MarkingViolation
                    {
                        Type = "CenterLinePatternReview",
                        Severity = "Info",
                        AlignmentName = cl.AlignmentName,
                        Station = cl.StartStation,
                        Message = $"Dashed center line at station {cl.StartStation:F0}-{cl.EndStation:F0} — verify passing is safe (sight distance)",
                    });
                }
            }
        }

        private void CheckMarkingColors(
            List<RoadMarkingData> markings, List<MarkingViolation> violations)
        {
            foreach (var m in markings)
            {
                // P0-05: a YELLOW EDGE LINE is a legitimate Israeli marking (sign/marking 807 —
                // continuous yellow road-edge line where there are no curbstones), so it must
                // NOT be flagged. The previous rule rejected it outright.
                if (m.MarkingType == "edge_line")
                    continue;

                // Center-line colour: Israeli center lines are normally white. This remains an
                // UNCERTIFIED heuristic (no sourced clause), so surface it as an Info note to
                // verify — not a hard violation — pending the rules provider.
                if (m.MarkingType == "center_line" && m.Color == "yellow")
                {
                    violations.Add(new MarkingViolation
                    {
                        Type = "CenterLineColorReview",
                        Severity = "Info",
                        AlignmentName = m.AlignmentName,
                        Station = m.StartStation,
                        Message = $"Yellow center line at station {m.StartStation:F0} — verify against the applicable marking standard (uncertified check).",
                    });
                }
            }
        }
    }

    public class MarkingViolation
    {
        public string Type { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning";
        public double Station { get; set; }
        public string AlignmentName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public double ActualValue { get; set; }
        public double RequiredValue { get; set; }
    }
}
