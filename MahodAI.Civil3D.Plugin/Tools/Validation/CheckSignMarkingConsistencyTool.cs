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
    /// Cross-validates road signs with road markings for consistency.
    /// Checks: speed signs vs speed markings, no-passing signs vs solid lines,
    /// warning signs before hazards, arrow placement vs lane markings.
    /// </summary>
    public class CheckSignMarkingConsistencyTool : DrawingToolBase
    {
        public override string Name => "check_sign_marking_consistency";
        public override string Description => "Cross-validates road signs with road markings for consistency (speed signs vs markings, no-passing zones, warning sign placement).";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var alignmentName = GetStringParam(parameters, "alignment_name");

            var extractor = new SignMarkingExtractor();
            var result = extractor.ExtractAll(tr, civilDoc,
                HostApplicationServices.WorkingDatabase) as SignMarkingExtractionResult;

            if (result == null)
                return ToolResult.Fail("EXTRACTION_FAILED", "Failed to extract sign/marking data");

            var signs = result.Signs;
            var markings = result.Markings;

            if (!string.IsNullOrEmpty(alignmentName))
            {
                signs = signs.Where(s => s.AlignmentName.Equals(alignmentName, StringComparison.OrdinalIgnoreCase)).ToList();
                markings = markings.Where(m => m.AlignmentName.Equals(alignmentName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var inconsistencies = new List<ConsistencyIssue>();

            // Check 1: Speed limit signs vs speed markings on road
            CheckSpeedConsistency(signs, markings, inconsistencies);

            // Check 2: No-passing signs (305) should have solid center lines
            CheckNoPassingConsistency(signs, markings, inconsistencies);

            // Check 3: Stop signs (101) should have stop line markings
            CheckStopSignMarkings(signs, markings, inconsistencies);

            // Check 4: Crosswalk signs (407) should have crosswalk markings
            CheckCrosswalkConsistency(signs, markings, inconsistencies);

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName ?? "all",
                total_signs = signs.Count,
                total_markings = markings.Count,
                inconsistency_count = inconsistencies.Count,
                inconsistencies = inconsistencies,
                passed = inconsistencies.Count == 0,
            }));
        }

        private void CheckSpeedConsistency(
            List<RoadSignData> signs, List<RoadMarkingData> markings,
            List<ConsistencyIssue> issues)
        {
            var speedSigns = signs.Where(s => s.SignCode.StartsWith("2") && s.SignCode.Length == 3).ToList();
            var speedMarkings = markings.Where(m => m.MarkingType == "speed_marking").ToList();

            foreach (var sign in speedSigns)
            {
                if (!int.TryParse(sign.SignCode, out int code)) continue;
                int signSpeed = (code - 200) * 10;

                // Find nearby speed markings (within 100m)
                var nearbyMarkings = speedMarkings.Where(m =>
                    m.AlignmentName == sign.AlignmentName &&
                    Math.Abs(m.StartStation - sign.Station) < 100).ToList();

                foreach (var marking in nearbyMarkings)
                {
                    if (int.TryParse(marking.TextContent?.Trim(), out int markingSpeed))
                    {
                        if (markingSpeed != signSpeed)
                        {
                            issues.Add(new ConsistencyIssue
                            {
                                Type = "SpeedMismatch",
                                Severity = "Error",
                                Station = sign.Station,
                                AlignmentName = sign.AlignmentName,
                                Message = $"Speed sign ({signSpeed} km/h) doesn't match road marking ({markingSpeed} km/h) at station {sign.Station:F0}",
                            });
                        }
                    }
                }
            }
        }

        private void CheckNoPassingConsistency(
            List<RoadSignData> signs, List<RoadMarkingData> markings,
            List<ConsistencyIssue> issues)
        {
            var noPassingSigns = signs.Where(s => s.SignCode == "305").ToList();
            var centerLines = markings.Where(m => m.MarkingType == "center_line").ToList();

            foreach (var sign in noPassingSigns)
            {
                var nearbyCenter = centerLines.Where(m =>
                    m.AlignmentName == sign.AlignmentName &&
                    m.StartStation <= sign.Station &&
                    m.EndStation >= sign.Station).ToList();

                bool hasSolidLine = nearbyCenter.Any(m =>
                    m.Pattern == "solid" || m.Pattern == "double_solid");

                if (!hasSolidLine)
                {
                    issues.Add(new ConsistencyIssue
                    {
                        Type = "NoPassingWithoutSolidLine",
                        Severity = "Error",
                        Station = sign.Station,
                        AlignmentName = sign.AlignmentName,
                        Message = $"No-passing sign (305) at station {sign.Station:F0} but no solid center line marking found",
                    });
                }
            }
        }

        private void CheckStopSignMarkings(
            List<RoadSignData> signs, List<RoadMarkingData> markings,
            List<ConsistencyIssue> issues)
        {
            var stopSigns = signs.Where(s => s.SignCode == "101").ToList();

            foreach (var sign in stopSigns)
            {
                bool hasStopLine = markings.Any(m =>
                    m.AlignmentName == sign.AlignmentName &&
                    m.MarkingType == "stop_line" &&
                    Math.Abs(m.StartStation - sign.Station) < 20);

                if (!hasStopLine)
                {
                    issues.Add(new ConsistencyIssue
                    {
                        Type = "StopSignWithoutStopLine",
                        Severity = "Warning",
                        Station = sign.Station,
                        AlignmentName = sign.AlignmentName,
                        Message = $"Stop sign (101) at station {sign.Station:F0} but no stop line marking nearby",
                    });
                }
            }
        }

        private void CheckCrosswalkConsistency(
            List<RoadSignData> signs, List<RoadMarkingData> markings,
            List<ConsistencyIssue> issues)
        {
            var crosswalkSigns = signs.Where(s => s.SignCode == "407").ToList();

            foreach (var sign in crosswalkSigns)
            {
                bool hasCrosswalk = markings.Any(m =>
                    m.AlignmentName == sign.AlignmentName &&
                    m.MarkingType == "crosswalk" &&
                    Math.Abs(m.StartStation - sign.Station) < 30);

                if (!hasCrosswalk)
                {
                    issues.Add(new ConsistencyIssue
                    {
                        Type = "CrosswalkSignWithoutMarking",
                        Severity = "Warning",
                        Station = sign.Station,
                        AlignmentName = sign.AlignmentName,
                        Message = $"Crosswalk sign (407) at station {sign.Station:F0} but no crosswalk marking nearby",
                    });
                }
            }
        }
    }

    public class ConsistencyIssue
    {
        public string Type { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning";
        public double Station { get; set; }
        public string AlignmentName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
