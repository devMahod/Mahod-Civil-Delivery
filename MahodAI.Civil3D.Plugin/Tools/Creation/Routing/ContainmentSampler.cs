using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Pure containment-sampling math for the post-creation readback of a routed alignment:
    /// the final Civil 3D geometry (lines + arcs + spirals) is sampled every few metres and
    /// each sample is checked against the buildable walkable mask. The grid router only
    /// proves PI-to-PI chords; curve attachment bulges the geometry between chords, so this
    /// is the honest "did the FINAL alignment stay on the surface" check.
    /// No Civil 3D dependency — unit-testable.
    /// </summary>
    public static class ContainmentSampler
    {
        public sealed class ContainmentResult
        {
            public bool Contained { get; init; }

            /// <summary>Samples with NO ground data — off the walkable mask/envelope. Absolute failures (P0-02).</summary>
            public IReadOnlyList<(double Station, Pt2 Point)> OffSurfaceSamples { get; init; }
                = Array.Empty<(double, Pt2)>();

            /// <summary>
            /// Samples ON ground data but closer to the surface boundary than the requested
            /// clearance. An existing road inside its own narrow survey band can never satisfy a
            /// boundary clearance the band doesn't have — these are engineering warnings for the
            /// human gate, not proof of missing ground.
            /// </summary>
            public IReadOnlyList<(double Station, Pt2 Point)> SubClearanceSamples { get; init; }
                = Array.Empty<(double, Pt2)>();
            public IReadOnlyList<double> OutsideStations { get; init; } = Array.Empty<double>();
            /// <summary>Outside samples with their world points, for building located violations (P0-02).</summary>
            public IReadOnlyList<(double Station, Pt2 Point)> OutsideSamples { get; init; }
                = Array.Empty<(double, Pt2)>();
            public int SamplesChecked { get; init; }
        }

        /// <summary>
        /// Builds the station list to sample: start, every <paramref name="stepM"/>, and the
        /// exact end station (so the endpoint is always proven).
        /// </summary>
        public static double[] BuildStations(double startStation, double endStation, double stepM)
            => BuildStations(startStation, endStation, stepM, null);

        /// <summary>
        /// Builds the station list to sample, merging the fixed <paramref name="stepM"/> grid
        /// with <paramref name="mandatoryStations"/> (P0-02): entity endpoints and curve
        /// critical points (¼/½/¾ apex) supplied by the caller from the FINAL alignment's
        /// entities. A fixed grid alone can step over a short arc/SCS bulge that leaves the
        /// buildable area between two in-mask samples; forcing a sample at every entity's
        /// mid-station closes that gap regardless of how short the entity is.
        /// Result is de-duplicated (within 1e-6 m) and ascending.
        /// </summary>
        public static double[] BuildStations(
            double startStation, double endStation, double stepM,
            IEnumerable<double>? mandatoryStations)
        {
            if (stepM <= 0) stepM = 10.0;
            if (endStation < startStation) (startStation, endStation) = (endStation, startStation);

            var stations = new List<double> { startStation };
            for (double s = startStation + stepM; s < endStation - 1e-9; s += stepM)
                stations.Add(s);
            if (endStation > startStation + 1e-9)
                stations.Add(endStation);

            if (mandatoryStations != null)
            {
                foreach (var st in mandatoryStations)
                {
                    if (st > startStation + 1e-9 && st < endStation - 1e-9)
                        stations.Add(st);
                }
            }

            stations.Sort();
            var deduped = new List<double>(stations.Count);
            foreach (var s in stations)
            {
                if (deduped.Count == 0 || s - deduped[^1] > 1e-6)
                    deduped.Add(s);
            }
            return deduped.ToArray();
        }

        /// <summary>
        /// Checks each (station, point) sample against the walkable mask. A sample is outside
        /// when its world point falls outside the grid envelope or in a non-walkable cell.
        /// </summary>
        public static ContainmentResult Check(
            IReadOnlyList<(double Station, Pt2 Point)> samples,
            bool[,] walkable,
            Envelope env,
            double cellSize,
            int[,]? dtb = null,
            double minClearanceM = 0.0)
        {
            if (walkable == null) throw new ArgumentNullException(nameof(walkable));
            if (env == null) throw new ArgumentNullException(nameof(env));
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));

            int nx = walkable.GetLength(0);
            int ny = walkable.GetLength(1);
            var outside = new List<double>();
            var outsideSamples = new List<(double Station, Pt2 Point)>();
            var offSurface = new List<(double Station, Pt2 Point)>();
            var subClearance = new List<(double Station, Pt2 Point)>();

            foreach (var (station, p) in samples)
            {
                var kind = Classify(p, walkable, env, cellSize, nx, ny, dtb, minClearanceM);
                if (kind == SampleKind.Inside) continue;
                outside.Add(station);
                outsideSamples.Add((station, p));
                if (kind == SampleKind.OffSurface) offSurface.Add((station, p));
                else subClearance.Add((station, p));
            }

            return new ContainmentResult
            {
                Contained = outside.Count == 0,
                OutsideStations = outside,
                OutsideSamples = outsideSamples,
                OffSurfaceSamples = offSurface,
                SubClearanceSamples = subClearance,
                SamplesChecked = samples.Count,
            };
        }

        private enum SampleKind { Inside, OffSurface, SubClearance }

        private static SampleKind Classify(Pt2 p, bool[,] walkable, Envelope env, double cellSize,
                                           int nx, int ny, int[,]? dtb, double minClearanceM)
        {
            if (p.X < env.MinX || p.X > env.MaxX || p.Y < env.MinY || p.Y > env.MaxY)
                return SampleKind.OffSurface;
            int cx = (int)Math.Floor((p.X - env.MinX) / cellSize);
            int cy = (int)Math.Floor((p.Y - env.MinY) / cellSize);
            // A point exactly on the max edge maps onto the last cell.
            cx = Math.Min(cx, nx - 1);
            cy = Math.Min(cy, ny - 1);
            if (cx < 0 || cy < 0) return SampleKind.OffSurface;
            if (!walkable[cx, cy]) return SampleKind.OffSurface;
            // Honest clearance check. Use the SAME nearest-cell threshold as GridRouter's erosion
            // (must match exactly, or this readback would reject routes the router legitimately
            // produced on a coarse grid). dtb is in cells.
            if (dtb != null && minClearanceM > 0)
            {
                int thr = Math.Max(1, (int)Math.Round(minClearanceM / cellSize, MidpointRounding.AwayFromZero));
                if (dtb[cx, cy] < thr) return SampleKind.SubClearance;
            }
            return SampleKind.Inside;
        }
    }
}
