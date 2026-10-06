using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Surface
{
    /// <summary>
    /// Samples surface elevations along an alignment or between two points.
    /// </summary>
    public class SampleSurfaceProfileTool : DrawingToolBase
    {
        public override string Name => "sample_surface_profile";
        public override string Description => "Samples surface elevations along an alignment or between two points. Useful for comparing surface to profile or analyzing terrain along a path.";
        public override string Category => ToolCategories.Surface;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var interval = GetDoubleParam(parameters, "interval") ?? 10.0;
            var startStation = GetDoubleParam(parameters, "start_station");
            var endStation = GetDoubleParam(parameters, "end_station");
            var maxSamples = GetIntParam(parameters, "max_samples") ?? 1000;

            // Alternative: sample between two points
            var startX = GetDoubleParam(parameters, "start_x");
            var startY = GetDoubleParam(parameters, "start_y");
            var endX = GetDoubleParam(parameters, "end_x");
            var endY = GetDoubleParam(parameters, "end_y");

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Find the surface
            Autodesk.Civil.DatabaseServices.Surface? surface = null;
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                if (obj != null && obj.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    surface = obj;
                    break;
                }
            }

            if (surface == null)
            {
                return ToolResult.NotFound("Surface", surfaceName);
            }

            List<SurfaceProfileSample> samples;

            if (!string.IsNullOrEmpty(alignmentName))
            {
                samples = SampleAlongAlignment(tr, civilDoc, surface, alignmentName, interval, startStation, endStation, maxSamples, ct);
            }
            else if (startX.HasValue && startY.HasValue && endX.HasValue && endY.HasValue)
            {
                samples = SampleBetweenPoints(surface, startX.Value, startY.Value, endX.Value, endY.Value, interval, maxSamples, ct);
            }
            else
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Either alignment_name or start/end coordinates are required");
            }

            var result = new SampleSurfaceProfileResult
            {
                SurfaceName = surface.Name,
                AlignmentName = alignmentName,
                Interval = interval,
                SampleCount = samples.Count,
                Samples = samples
            };

            // Calculate statistics
            if (samples.Count > 0)
            {
                var elevations = samples.Where(s => s.Elevation.HasValue).Select(s => s.Elevation!.Value).ToList();
                if (elevations.Count > 0)
                {
                    result.MinElevation = elevations.Min();
                    result.MaxElevation = elevations.Max();
                    result.MeanElevation = elevations.Average();
                }
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private List<SurfaceProfileSample> SampleAlongAlignment(
            Transaction tr,
            CivilDocument civilDoc,
            Autodesk.Civil.DatabaseServices.Surface surface,
            string alignmentName,
            double interval,
            double? startStation,
            double? endStation,
            int maxSamples,
            CancellationToken ct)
        {
            var samples = new List<SurfaceProfileSample>();

            // Find alignment
            Autodesk.Civil.DatabaseServices.Alignment? alignment = null;
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                if (obj != null && obj.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
                {
                    alignment = obj;
                    break;
                }
            }

            if (alignment == null) return samples;

            var sampleStart = startStation ?? alignment.StartingStation;
            var sampleEnd = endStation ?? alignment.EndingStation;

            sampleStart = Math.Max(sampleStart, alignment.StartingStation);
            sampleEnd = Math.Min(sampleEnd, alignment.EndingStation);

            var station = sampleStart;
            while (station <= sampleEnd && samples.Count < maxSamples)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    double x = 0, y = 0;
                    alignment.PointLocation(station, 0, ref x, ref y);

                    var sample = new SurfaceProfileSample
                    {
                        Station = station,
                        X = x,
                        Y = y
                    };

                    try
                    {
                        sample.Elevation = surface.FindElevationAtXY(x, y);
                    }
                    catch
                    {
                        sample.OutsideSurface = true;
                    }

                    samples.Add(sample);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleSurfaceProfile at station {station}: {ex.Message}");
                }

                station += interval;
            }

            // Add final station
            if (station > sampleEnd && samples.Count < maxSamples)
            {
                try
                {
                    double x = 0, y = 0;
                    alignment.PointLocation(sampleEnd, 0, ref x, ref y);

                    var sample = new SurfaceProfileSample
                    {
                        Station = sampleEnd,
                        X = x,
                        Y = y
                    };

                    try
                    {
                        sample.Elevation = surface.FindElevationAtXY(x, y);
                    }
                    catch
                    {
                        sample.OutsideSurface = true;
                    }

                    if (samples.Count == 0 || Math.Abs(samples[samples.Count - 1].Station!.Value - sampleEnd) > 0.001)
                    {
                        samples.Add(sample);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SampleSurfaceProfile final station: {ex.Message}");
                }
            }

            return samples;
        }

        private List<SurfaceProfileSample> SampleBetweenPoints(
            Autodesk.Civil.DatabaseServices.Surface surface,
            double startX, double startY,
            double endX, double endY,
            double interval,
            int maxSamples,
            CancellationToken ct)
        {
            var samples = new List<SurfaceProfileSample>();

            var dx = endX - startX;
            var dy = endY - startY;
            var totalLength = Math.Sqrt(dx * dx + dy * dy);

            if (totalLength <= 0) return samples;

            var dirX = dx / totalLength;
            var dirY = dy / totalLength;

            double distance = 0;
            while (distance <= totalLength && samples.Count < maxSamples)
            {
                ct.ThrowIfCancellationRequested();

                var x = startX + dirX * distance;
                var y = startY + dirY * distance;

                var sample = new SurfaceProfileSample
                {
                    Distance = distance,
                    X = x,
                    Y = y
                };

                try
                {
                    sample.Elevation = surface.FindElevationAtXY(x, y);
                }
                catch
                {
                    sample.OutsideSurface = true;
                }

                samples.Add(sample);
                distance += interval;
            }

            // Add final point
            if (distance > totalLength && samples.Count < maxSamples)
            {
                var sample = new SurfaceProfileSample
                {
                    Distance = totalLength,
                    X = endX,
                    Y = endY
                };

                try
                {
                    sample.Elevation = surface.FindElevationAtXY(endX, endY);
                }
                catch
                {
                    sample.OutsideSurface = true;
                }

                if (samples.Count == 0 || Math.Abs(samples[samples.Count - 1].Distance!.Value - totalLength) > 0.001)
                {
                    samples.Add(sample);
                }
            }

            return samples;
        }
    }

    #region Result Models

    public class SampleSurfaceProfileResult
    {
        public string SurfaceName { get; set; } = string.Empty;
        public string? AlignmentName { get; set; }
        public double Interval { get; set; }
        public int SampleCount { get; set; }
        public double? MinElevation { get; set; }
        public double? MaxElevation { get; set; }
        public double? MeanElevation { get; set; }
        public List<SurfaceProfileSample> Samples { get; set; } = new();
    }

    public class SurfaceProfileSample
    {
        public double? Station { get; set; }
        public double? Distance { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double? Elevation { get; set; }
        public bool OutsideSurface { get; set; }
    }

    #endregion
}
