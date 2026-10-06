using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Utilities;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

namespace MahodAI.Civil3D.Plugin.Tools.Profile
{
    /// <summary>
    /// Interpolates a smooth curve through given station/elevation points using cubic spline.
    /// Returns densely sampled elevations along the curve.
    /// </summary>
    public class InterpolateProfileWithSplineTool : DrawingToolBase
    {
        public override string Name => "interpolate_profile_with_spline";
        public override string Description => "Interpolates a smooth curve through given station/elevation points using cubic spline. Returns densely sampled elevations along the curve.";
        public override string Category => ToolCategories.Profile;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private const double DefaultProfileSampleInterval = 5.0;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var outputInterval = GetDoubleParam(parameters, "output_interval") ?? 1.0;
            var startStation = GetDoubleParam(parameters, "start_station");
            var endStation = GetDoubleParam(parameters, "end_station");

            // Collect input points either from explicit array or from existing profile
            var stations = new List<double>();
            var elevations = new List<double>();

            if (parameters.TryGetProperty("points", out var pointsProp) &&
                pointsProp.ValueKind == JsonValueKind.Array)
            {
                // Parse explicit points array
                foreach (var pt in pointsProp.EnumerateArray())
                {
                    if (pt.TryGetProperty("station", out var sProp) &&
                        pt.TryGetProperty("elevation", out var eProp) &&
                        sProp.ValueKind == JsonValueKind.Number &&
                        eProp.ValueKind == JsonValueKind.Number)
                    {
                        stations.Add(sProp.GetDouble());
                        elevations.Add(eProp.GetDouble());
                    }
                }
            }
            else
            {
                // Extract from existing profile
                var profileName = GetStringParam(parameters, "profile_name");
                var alignmentName = GetStringParam(parameters, "alignment_name");

                if (string.IsNullOrEmpty(profileName))
                {
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        "Either 'points' array or 'profile_name' must be provided");
                }

                if (civilDoc == null)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
                }

                var profile = CivilObjectFinder.FindProfileByName(tr, civilDoc, profileName, alignmentName);
                if (profile == null)
                {
                    return ToolResult.NotFound("Profile", profileName);
                }

                // Sample existing profile at DefaultProfileSampleInterval intervals
                var sampleStation = profile.StartingStation;
                while (sampleStation <= profile.EndingStation)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var elev = profile.ElevationAt(sampleStation);
                        stations.Add(sampleStation);
                        elevations.Add(elev);
                    }
                    catch
                    {
                        // Skip stations where elevation can't be determined
                    }

                    sampleStation += DefaultProfileSampleInterval;
                }

                // Include the final station
                if (stations.Count == 0 || Math.Abs(stations[stations.Count - 1] - profile.EndingStation) > 0.001)
                {
                    try
                    {
                        var elev = profile.ElevationAt(profile.EndingStation);
                        stations.Add(profile.EndingStation);
                        elevations.Add(elev);
                    }
                    catch { }
                }
            }

            if (stations.Count < 2)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "At least 2 points are required for spline interpolation");
            }

            // Sort by station
            var sortedIndices = Enumerable.Range(0, stations.Count)
                .OrderBy(i => stations[i])
                .ToArray();

            var sortedStations = sortedIndices.Select(i => stations[i]).ToArray();
            var sortedElevations = sortedIndices.Select(i => elevations[i]).ToArray();

            // Remove duplicates (same station)
            var uniqueStations = new List<double> { sortedStations[0] };
            var uniqueElevations = new List<double> { sortedElevations[0] };

            for (int i = 1; i < sortedStations.Length; i++)
            {
                if (Math.Abs(sortedStations[i] - uniqueStations[uniqueStations.Count - 1]) > 1e-9)
                {
                    uniqueStations.Add(sortedStations[i]);
                    uniqueElevations.Add(sortedElevations[i]);
                }
            }

            if (uniqueStations.Count < 2)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "At least 2 distinct station values are required for spline interpolation");
            }

            var x = uniqueStations.ToArray();
            var y = uniqueElevations.ToArray();

            // Compute cubic spline coefficients
            var spline = ComputeNaturalCubicSpline(x, y);

            // Determine output range
            var outStart = startStation ?? x[0];
            var outEnd = endStation ?? x[x.Length - 1];

            // Clamp to input data range
            outStart = Math.Max(outStart, x[0]);
            outEnd = Math.Min(outEnd, x[x.Length - 1]);

            if (outStart >= outEnd)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Invalid output station range");
            }

            if (outputInterval <= 0)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "output_interval must be positive");
            }

            // Sample the spline at output_interval spacing
            var outputPoints = new List<SplineOutputPoint>();
            double minElev = double.MaxValue;
            double maxElev = double.MinValue;

            var station = outStart;
            while (station <= outEnd)
            {
                ct.ThrowIfCancellationRequested();

                var elev = EvaluateSpline(spline, x, station);
                outputPoints.Add(new SplineOutputPoint
                {
                    Station = Math.Round(station, 4),
                    Elevation = Math.Round(elev, 4)
                });

                minElev = Math.Min(minElev, elev);
                maxElev = Math.Max(maxElev, elev);

                station += outputInterval;
            }

            // Add final point if not already included
            if (outputPoints.Count == 0 || Math.Abs(outputPoints[outputPoints.Count - 1].Station - outEnd) > 0.001)
            {
                var elev = EvaluateSpline(spline, x, outEnd);
                outputPoints.Add(new SplineOutputPoint
                {
                    Station = Math.Round(outEnd, 4),
                    Elevation = Math.Round(elev, 4)
                });

                minElev = Math.Min(minElev, elev);
                maxElev = Math.Max(maxElev, elev);
            }

            // Compute total arc length (approximation via chord lengths)
            double totalLength = 0;
            for (int i = 1; i < outputPoints.Count; i++)
            {
                var ds = outputPoints[i].Station - outputPoints[i - 1].Station;
                var de = outputPoints[i].Elevation - outputPoints[i - 1].Elevation;
                totalLength += Math.Sqrt(ds * ds + de * de);
            }

            var result = new SplineInterpolationResult
            {
                InputPointCount = x.Length,
                OutputPointCount = outputPoints.Count,
                OutputInterval = outputInterval,
                StartStation = outStart,
                EndStation = outEnd,
                MinElevation = Math.Round(minElev, 4),
                MaxElevation = Math.Round(maxElev, 4),
                TotalLength = Math.Round(totalLength, 4),
                Points = outputPoints
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        #region Cubic Spline Implementation

        /// <summary>
        /// Computes natural cubic spline coefficients for the given data points.
        /// Natural boundary conditions: second derivative = 0 at endpoints.
        /// </summary>
        private static SplineCoefficients ComputeNaturalCubicSpline(double[] x, double[] y)
        {
            int n = x.Length - 1; // number of intervals

            var h = new double[n];
            for (int i = 0; i < n; i++)
                h[i] = x[i + 1] - x[i];

            // Set up tridiagonal system for second derivatives (c)
            // Natural spline: c[0] = 0, c[n] = 0
            var c = new double[n + 1];

            if (n > 1)
            {
                // Right-hand side
                var rhs = new double[n - 1];
                for (int i = 0; i < n - 1; i++)
                {
                    rhs[i] = 6.0 * ((y[i + 2] - y[i + 1]) / h[i + 1] - (y[i + 1] - y[i]) / h[i]);
                }

                // Tridiagonal matrix coefficients (for indices 1..n-1)
                var lower = new double[n - 1]; // sub-diagonal
                var diag = new double[n - 1];  // diagonal
                var upper = new double[n - 1]; // super-diagonal

                for (int i = 0; i < n - 1; i++)
                {
                    diag[i] = 2.0 * (h[i] + h[i + 1]);
                    if (i > 0)
                        lower[i] = h[i];
                    if (i < n - 2)
                        upper[i] = h[i + 1];
                }

                // Solve tridiagonal system using Thomas algorithm
                var solution = SolveTridiagonal(lower, diag, upper, rhs);

                for (int i = 0; i < n - 1; i++)
                    c[i + 1] = solution[i];
            }

            // c[0] = 0 and c[n] = 0 (natural boundary)

            // Compute a, b, d coefficients
            var a = new double[n];
            var b = new double[n];
            var d = new double[n];

            for (int i = 0; i < n; i++)
            {
                a[i] = y[i];
                d[i] = (c[i + 1] - c[i]) / (3.0 * h[i]);
                b[i] = (y[i + 1] - y[i]) / h[i] - h[i] * (c[i + 1] + 2.0 * c[i]) / 3.0;
            }

            return new SplineCoefficients
            {
                A = a,
                B = b,
                C = c,
                D = d
            };
        }

        /// <summary>
        /// Solves a tridiagonal system using the Thomas algorithm.
        /// </summary>
        private static double[] SolveTridiagonal(double[] lower, double[] diag, double[] upper, double[] rhs)
        {
            int n = diag.Length;
            var cPrime = new double[n];
            var dPrime = new double[n];

            // Forward sweep
            cPrime[0] = upper[0] / diag[0];
            dPrime[0] = rhs[0] / diag[0];

            for (int i = 1; i < n; i++)
            {
                var m = diag[i] - lower[i] * cPrime[i - 1];
                cPrime[i] = i < n - 1 ? upper[i] / m : 0;
                dPrime[i] = (rhs[i] - lower[i] * dPrime[i - 1]) / m;
            }

            // Back substitution
            var result = new double[n];
            result[n - 1] = dPrime[n - 1];

            for (int i = n - 2; i >= 0; i--)
            {
                result[i] = dPrime[i] - cPrime[i] * result[i + 1];
            }

            return result;
        }

        /// <summary>
        /// Evaluates the spline at a given station value.
        /// </summary>
        private static double EvaluateSpline(SplineCoefficients spline, double[] x, double station)
        {
            // Find the correct interval
            int n = x.Length - 1;
            int i = n - 1; // default to last interval

            for (int j = 0; j < n; j++)
            {
                if (station <= x[j + 1])
                {
                    i = j;
                    break;
                }
            }

            var dx = station - x[i];
            return spline.A[i] + spline.B[i] * dx + spline.C[i] * dx * dx + spline.D[i] * dx * dx * dx;
        }

        #endregion

        #region Internal Types

        private class SplineCoefficients
        {
            public double[] A { get; set; } = Array.Empty<double>();
            public double[] B { get; set; } = Array.Empty<double>();
            public double[] C { get; set; } = Array.Empty<double>(); // length n+1 (includes endpoints)
            public double[] D { get; set; } = Array.Empty<double>();
        }

        #endregion
    }

    #region Result Models

    public class SplineInterpolationResult
    {
        public int InputPointCount { get; set; }
        public int OutputPointCount { get; set; }
        public double OutputInterval { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double MinElevation { get; set; }
        public double MaxElevation { get; set; }
        public double TotalLength { get; set; }
        public List<SplineOutputPoint> Points { get; set; } = new();
    }

    public class SplineOutputPoint
    {
        public double Station { get; set; }
        public double Elevation { get; set; }
    }

    #endregion
}
