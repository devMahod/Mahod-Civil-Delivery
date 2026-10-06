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
    /// Checks design clearances (cut/fill analysis) between profile and surface.
    /// Also reports stations where the alignment leaves the surface entirely
    /// (outside_surface_count / outside_stations + OutsideSurface violations).
    /// </summary>
    public class CheckDesignClearancesTool : DrawingToolBase
    {
        public override string Name => "check_design_clearances";
        public override string Description => "Analyzes cut and fill between a design profile and existing ground surface along an alignment. Reports areas of excessive cut/fill and stations where the alignment leaves the surface.";
        public override string Category => ToolCategories.Validation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": { ""type"": ""string"", ""description"": ""Name of the alignment to sample along"" },
                ""surface_name"":   { ""type"": ""string"", ""description"": ""Name of the existing ground surface"" },
                ""profile_name"":   { ""type"": ""string"", ""description"": ""Design (FG) profile name. Default: first layout/design profile on the alignment."" },
                ""interval"":       { ""type"": ""number"", ""description"": ""Sampling interval along the alignment in meters (default: 10)"" },
                ""max_cut"":        { ""type"": ""number"", ""description"": ""Cut depth threshold in meters (default: 10)"" },
                ""max_fill"":       { ""type"": ""number"", ""description"": ""Fill height threshold in meters (default: 5)"" }
            },
            ""required"": [""alignment_name"", ""surface_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var profileName = GetStringParam(parameters, "profile_name");
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var interval = GetDoubleParam(parameters, "interval") ?? 10.0;
            var maxCut = GetDoubleParam(parameters, "max_cut") ?? 10.0;
            var maxFill = GetDoubleParam(parameters, "max_fill") ?? 5.0;

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

            // Find the surface
            var surface = CivilObjectFinder.FindSurfaceByName(tr, civilDoc, surfaceName);
            if (surface == null)
            {
                return ToolResult.NotFound("Surface", surfaceName);
            }

            // Find the profile (optional - use surface profile if not specified)
            Autodesk.Civil.DatabaseServices.Profile? profile = null;
            if (!string.IsNullOrEmpty(profileName))
            {
                profile = CivilObjectFinder.FindProfileByName(tr, civilDoc, profileName, alignmentName);
                if (profile == null)
                {
                    return ToolResult.NotFound("Profile", profileName);
                }
            }
            else
            {
                // Find the first layout profile or use surface profile
                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    var p = tr.GetObject(profileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                    if (p != null && (p.ProfileType.ToString() == "Layout" || p.ProfileType.ToString() == "Design"))
                    {
                        profile = p;
                        break;
                    }
                }
            }

            var result = new ClearanceCheckResult
            {
                AlignmentName = alignment.Name,
                ProfileName = profile?.Name ?? "Surface",
                SurfaceName = surface.Name,
                Interval = interval,
                MaxCutThreshold = maxCut,
                MaxFillThreshold = maxFill,
                Samples = new List<ClearanceSample>(),
                Violations = new List<ClearanceViolation>(),
                Statistics = new ClearanceStatistics()
            };

            // Sample along alignment. Stations where the alignment leaves the surface are
            // RECORDED (OutsideSurface flag + outside_stations + violations) — previously
            // they were only Debug.WriteLine'd and silently skipped.
            var station = alignment.StartingStation;
            double totalCut = 0;
            double totalFill = 0;
            int cutCount = 0;
            int fillCount = 0;
            double maxCutFound = 0;
            double maxFillFound = 0;

            int outsideExceptionCount = 0;
            int classifyExceptionCount = 0;
            double? outsideRunStart = null;   // start station of the current outside-surface run
            double outsideRunEnd = 0;

            // Bound total samples so a pathological interval can't stall the tool.
            double totalLength = alignment.EndingStation - alignment.StartingStation;
            if (interval <= 0) interval = 10.0;
            const int maxSamples = 50_000;
            if (totalLength / interval > maxSamples)
                interval = totalLength / maxSamples;
            result.Interval = interval;   // report the EFFECTIVE interval

            void CloseOutsideRun()
            {
                if (outsideRunStart == null) return;
                result.Violations.Add(new ClearanceViolation
                {
                    Station = outsideRunStart.Value,
                    Type = "OutsideSurface",
                    Severity = "Error",
                    ActualValue = Math.Round(outsideRunEnd - outsideRunStart.Value, 2),
                    ThresholdValue = 0,
                    Message = $"הציר יוצא מגבולות המשטח בין תחנה {outsideRunStart.Value:F0} לתחנה {outsideRunEnd:F0}"
                });
                outsideRunStart = null;
            }

            while (station <= alignment.EndingStation)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    double x = 0, y = 0;
                    alignment.PointLocation(station, 0, ref x, ref y);

                    var sample = new ClearanceSample
                    {
                        Station = station,
                        X = x,
                        Y = y
                    };

                    // Get surface elevation — record the sample as outside-surface on failure
                    try
                    {
                        sample.SurfaceElevation = surface.FindElevationAtXY(x, y);
                        CloseOutsideRun();
                    }
                    catch (ArgumentException)
                    {
                        outsideExceptionCount++;
                        sample.OutsideSurface = true;
                        sample.Type = "OutsideSurface";
                        result.Samples.Add(sample);
                        result.OutsideStations.Add(Math.Round(station, 2));
                        outsideRunStart ??= station;
                        outsideRunEnd = station;
                        station += interval;
                        continue;
                    }
                    catch
                    {
                        outsideExceptionCount++;
                        sample.OutsideSurface = true;
                        sample.Type = "OutsideSurface";
                        result.Samples.Add(sample);
                        result.OutsideStations.Add(Math.Round(station, 2));
                        outsideRunStart ??= station;
                        outsideRunEnd = station;
                        station += interval;
                        continue;
                    }

                    // Get profile elevation
                    if (profile != null && !sample.OutsideSurface)
                    {
                        try
                        {
                            sample.ProfileElevation = profile.ElevationAt(station);
                            sample.Difference = sample.ProfileElevation.Value - sample.SurfaceElevation.Value;

                            // Positive = fill, Negative = cut
                            if (sample.Difference > 0)
                            {
                                sample.Type = "Fill";
                                totalFill += sample.Difference.Value;
                                fillCount++;
                                if (sample.Difference.Value > maxFillFound)
                                    maxFillFound = sample.Difference.Value;

                                if (sample.Difference.Value > maxFill)
                                {
                                    result.Violations.Add(new ClearanceViolation
                                    {
                                        Station = station,
                                        Type = "ExcessiveFill",
                                        Severity = "Warning",
                                        ActualValue = sample.Difference.Value,
                                        ThresholdValue = maxFill,
                                        Message = $"Fill depth ({sample.Difference.Value:F2}m) exceeds threshold ({maxFill}m)"
                                    });
                                }
                            }
                            else if (sample.Difference < 0)
                            {
                                sample.Type = "Cut";
                                totalCut += Math.Abs(sample.Difference.Value);
                                cutCount++;
                                if (Math.Abs(sample.Difference.Value) > maxCutFound)
                                    maxCutFound = Math.Abs(sample.Difference.Value);

                                if (Math.Abs(sample.Difference.Value) > maxCut)
                                {
                                    result.Violations.Add(new ClearanceViolation
                                    {
                                        Station = station,
                                        Type = "ExcessiveCut",
                                        Severity = "Warning",
                                        ActualValue = Math.Abs(sample.Difference.Value),
                                        ThresholdValue = maxCut,
                                        Message = $"Cut depth ({Math.Abs(sample.Difference.Value):F2}m) exceeds threshold ({maxCut}m)"
                                    });
                                }
                            }
                            else
                            {
                                sample.Type = "Match";
                            }
                        }
                        catch (Exception)
                        {
                            classifyExceptionCount++;
                        }
                    }

                    result.Samples.Add(sample);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] CheckDesignClearances sample at station {station}: {ex.Message}");
                }

                station += interval;
            }

            // Close a trailing outside-surface run that reached the alignment end
            CloseOutsideRun();
            result.OutsideSurfaceCount = outsideExceptionCount;

            // Log summary of skipped points (instead of per-exception spam)
            if (outsideExceptionCount > 0 || classifyExceptionCount > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] CheckDesignClearances '{alignmentName}': " +
                    $"{outsideExceptionCount} points outside surface, " +
                    $"{classifyExceptionCount} classify errors, " +
                    $"{result.Samples.Count} total samples");
            }

            // Calculate statistics
            result.Statistics.SampleCount = result.Samples.Count;
            result.Statistics.CutSampleCount = cutCount;
            result.Statistics.FillSampleCount = fillCount;
            result.Statistics.AverageCut = cutCount > 0 ? totalCut / cutCount : 0;
            result.Statistics.AverageFill = fillCount > 0 ? totalFill / fillCount : 0;
            result.Statistics.MaxCut = maxCutFound;
            result.Statistics.MaxFill = maxFillFound;
            result.Statistics.ViolationCount = result.Violations.Count;

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class ClearanceCheckResult
    {
        public string AlignmentName { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string SurfaceName { get; set; } = string.Empty;
        public double Interval { get; set; }
        public double MaxCutThreshold { get; set; }
        public double MaxFillThreshold { get; set; }
        /// <summary>Number of samples that fell outside the surface boundary.</summary>
        public int OutsideSurfaceCount { get; set; }
        /// <summary>Stations (per sampled interval) where the alignment leaves the surface.</summary>
        public List<double> OutsideStations { get; set; } = new();
        public List<ClearanceSample> Samples { get; set; } = new();
        public List<ClearanceViolation> Violations { get; set; } = new();
        public ClearanceStatistics? Statistics { get; set; }
    }

    public class ClearanceSample
    {
        public double Station { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double? SurfaceElevation { get; set; }
        public double? ProfileElevation { get; set; }
        public double? Difference { get; set; }
        public string Type { get; set; } = string.Empty;
        public bool OutsideSurface { get; set; }
    }

    public class ClearanceViolation
    {
        public double Station { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Severity { get; set; } = "Warning";
        public double ActualValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ClearanceStatistics
    {
        public int SampleCount { get; set; }
        public int CutSampleCount { get; set; }
        public int FillSampleCount { get; set; }
        public double AverageCut { get; set; }
        public double AverageFill { get; set; }
        public double MaxCut { get; set; }
        public double MaxFill { get; set; }
        public int ViolationCount { get; set; }
    }

    #endregion
}
