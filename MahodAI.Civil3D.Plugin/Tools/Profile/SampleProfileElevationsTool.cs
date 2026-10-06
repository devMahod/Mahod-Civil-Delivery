using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Profile
{
    /// <summary>
    /// Samples profile elevations at regular intervals.
    /// </summary>
    public class SampleProfileElevationsTool : DrawingToolBase
    {
        public override string Name => "sample_profile_elevations";
        public override string Description => "Samples profile elevations at regular station intervals. Useful for comparing profiles or generating elevation data.";
        public override string Category => ToolCategories.Profile;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var interval = GetDoubleParam(parameters, "interval") ?? 10.0;
            var startStation = GetDoubleParam(parameters, "start_station");
            var endStation = GetDoubleParam(parameters, "end_station");
            var maxSamples = GetIntParam(parameters, "max_samples") ?? 1000;

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the profile
            var profile = CivilObjectFinder.FindProfileByName(tr, civilDoc, profileName, alignmentName);
            if (profile == null)
            {
                return ToolResult.NotFound("Profile", profileName);
            }

            // Determine the owning alignment name
            string foundAlignmentName = alignmentName ?? string.Empty;
            if (string.IsNullOrEmpty(foundAlignmentName))
            {
                foreach (ObjectId alId in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
                {
                    var al = tr.GetObject(alId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (al == null) continue;
                    foreach (ObjectId pfId in al.GetProfileIds())
                    {
                        if (pfId == profile.ObjectId) { foundAlignmentName = al.Name; break; }
                    }
                    if (!string.IsNullOrEmpty(foundAlignmentName)) break;
                }
            }

            // Determine station range
            var sampleStart = startStation ?? profile.StartingStation;
            var sampleEnd = endStation ?? profile.EndingStation;

            // Clamp to profile range
            sampleStart = Math.Max(sampleStart, profile.StartingStation);
            sampleEnd = Math.Min(sampleEnd, profile.EndingStation);

            if (sampleStart >= sampleEnd)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Invalid station range");
            }

            // Ensure interval is positive
            if (interval <= 0)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Interval must be positive");
            }

            // Sample elevations
            var samples = new List<ProfileSample>();
            var station = sampleStart;
            bool truncated = false;

            while (station <= sampleEnd && samples.Count < maxSamples)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var elevation = profile.ElevationAt(station);
                    samples.Add(new ProfileSample
                    {
                        Station = station,
                        Elevation = elevation
                    });
                }
                catch
                {
                    // Skip stations where elevation can't be determined
                }

                station += interval;
            }

            // Add final station if not already included
            if (station > sampleEnd && samples.Count < maxSamples)
            {
                try
                {
                    var elevation = profile.ElevationAt(sampleEnd);
                    if (samples.Count == 0 || Math.Abs(samples[samples.Count - 1].Station - sampleEnd) > 0.001)
                    {
                        samples.Add(new ProfileSample
                        {
                            Station = sampleEnd,
                            Elevation = elevation
                        });
                    }
                }
                catch { }
            }

            truncated = station <= sampleEnd && samples.Count >= maxSamples;

            var result = new SampleProfileResult
            {
                ProfileName = profile.Name,
                AlignmentName = foundAlignmentName,
                Interval = interval,
                StartStation = sampleStart,
                EndStation = sampleEnd,
                SampleCount = samples.Count,
                Truncated = truncated,
                Samples = samples
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class SampleProfileResult
    {
        public string ProfileName { get; set; } = string.Empty;
        public string AlignmentName { get; set; } = string.Empty;
        public double Interval { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public int SampleCount { get; set; }
        public bool Truncated { get; set; }
        public List<ProfileSample> Samples { get; set; } = new();
    }

    public class ProfileSample
    {
        public double Station { get; set; }
        public double Elevation { get; set; }
    }

    #endregion
}
