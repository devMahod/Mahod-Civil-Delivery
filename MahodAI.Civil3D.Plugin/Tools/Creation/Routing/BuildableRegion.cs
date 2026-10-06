using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Union;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Builds the walkable-cell mask for routing by sampling the active TIN surface:
    /// a cell is walkable iff its centre lies inside a TIN triangle. Closed polylines on
    /// <c>obstacleLayer</c> (if present) further subtract cells from the mask.
    ///
    /// Replaces the previous polygon-based approach: the L-shape footprint of an urban
    /// surface is well captured by per-cell <c>FindTriangleAtXY</c> queries, which use
    /// Civil 3D's internal triangle index (O(log N) per query). For a 280k-triangle
    /// surface and a 250×250 grid this completes in single-digit seconds — versus
    /// minutes for ExtractBorder + NetTopologySuite Contains.
    /// </summary>
    public static class BuildableRegion
    {
        public sealed class BuildResult
        {
            public bool Success { get; init; }
            public string? FailureReason { get; init; }

            public bool[,]? Walkable { get; init; }
            /// <summary>
            /// Per-cell ground slope as rise/run (i.e. tan of slope angle) on the TIN
            /// surface. Cells outside any triangle stay at 0. Smoothed with a small
            /// 3x3 box blur over walkable cells to denoise per-triangle stepping.
            /// Used downstream by RoadAffinity to discount cells that look like an
            /// existing road (locally flat strip on the surface).
            /// </summary>
            public double[,]? Slope { get; init; }
            public Envelope? Envelope { get; init; }
            public double CellSize { get; init; }
            public int Nx { get; init; }
            public int Ny { get; init; }
            public int CellsWalkable { get; init; }

            // ── Sliver-filter diagnostics (so callers/telemetry can SEE the mask is honest) ──
            /// <summary>Total triangles enumerated from the TIN.</summary>
            public int TrianglesTotal { get; init; }
            /// <summary>Triangles kept after the max-edge sliver filter (rasterised into the mask).</summary>
            public int TrianglesKept { get; init; }
            /// <summary>Max-edge length (m) above which a triangle is treated as a hull/bay sliver and dropped.</summary>
            public double MaxEdgeThresholdM { get; init; }
            /// <summary>Median per-triangle longest edge (m) — the data-density anchor the auto threshold scales from.</summary>
            public double MedianEdgeM { get; init; }
            /// <summary>90th / 99th percentile of per-triangle longest edge (m) — telemetry to tune the threshold.</summary>
            public double P90EdgeM { get; init; }
            public double P99EdgeM { get; init; }
            /// <summary>Triangles dropped because they were long AND needle-thin (aspect over the limit) — bridges.</summary>
            public int TrianglesDroppedThin { get; init; }
            /// <summary>Triangles dropped by the pathological mega-triangle backstop (hull-spanning).</summary>
            public int TrianglesDroppedMega { get; init; }
            /// <summary>Long-but-FAT triangles KEPT (legitimately coarse / sparse open ground the road may use).</summary>
            public int TrianglesKeptLongFat { get; init; }
            /// <summary>Triangles Civil 3D itself hid (IsVisible=false: boundary / max-length / hide edits).</summary>
            public int TrianglesHidden { get; init; }

            /// <summary>Isolated one/two-cell mask pinholes closed after rasterisation (see <see cref="FillIsolatedHoles"/>).</summary>
            public int CellsHoleFilled { get; init; }

            public string? SurfaceName { get; init; }
            public ObjectId SurfaceId { get; init; }
            public int ObstacleCount { get; init; }
            public List<string> Warnings { get; init; } = new();

            public static BuildResult Fail(string reason, List<string> warnings)
                => new() { Success = false, FailureReason = reason, Warnings = warnings };
        }

        /// <summary>
        /// Samples the TIN surface and returns a walkable mask suitable for grid routing.
        /// </summary>
        /// <param name="targetMaxCells">
        /// Upper bound on grid cells. Cell size is auto-increased if the requested size
        /// would produce more cells than this, to keep routing within seconds even on
        /// huge surfaces. Default 1_000_000 → ~1000×1000 grid (~32 MB walkable mask).
        /// </param>
        /// <param name="maxTriangleEdgeM">
        /// Triangles whose longest edge exceeds this (m) are treated as hull/bay slivers
        /// and dropped from the walkable mask. An unconstrained Civil 3D TIN triangulates
        /// to the convex hull of its points, bridging concave bays and the inside of a
        /// curved survey band with long thin triangles that are NOT real ground — leaving
        /// them walkable makes the router cut across off-surface terrain while the
        /// containment readback still certifies "contained". 0 = auto (median longest edge
        /// × <see cref="AutoEdgeFactor"/>, floored at <see cref="MinEdgeFloorM"/>).
        /// </param>
        public static BuildResult Build(
            Transaction tr,
            CivilDocument civilDoc,
            string? surfaceName,
            string obstacleLayer,
            double requestedCellSize,
            CancellationToken ct,
            int targetMaxCells = 1_000_000,
            double maxTriangleEdgeM = 0.0)
        {
            var warnings = new List<string>();

            // 1. Resolve surface
            TinSurface? surface = ResolveSurface(civilDoc, tr, surfaceName);
            if (surface == null)
            {
                return BuildResult.Fail(
                    $"No TIN surface found (requested: '{surfaceName ?? "<first available>"}').",
                    warnings);
            }

            // 2. Bbox + grid sizing
            Extents3d ext;
            try { ext = surface.GeometricExtents; }
            catch (Exception ex)
            {
                return BuildResult.Fail($"Could not read surface extents: {ex.Message}", warnings);
            }

            double width = ext.MaxPoint.X - ext.MinPoint.X;
            double height = ext.MaxPoint.Y - ext.MinPoint.Y;
            if (width <= 0 || height <= 0)
                return BuildResult.Fail("Surface extents are degenerate.", warnings);

            double cellSize = requestedCellSize <= 0 ? 5.0 : requestedCellSize;
            // Auto-scale cellSize up if the grid would exceed targetMaxCells.
            int nx0 = (int)Math.Ceiling(width / cellSize);
            int ny0 = (int)Math.Ceiling(height / cellSize);
            if ((long)nx0 * ny0 > targetMaxCells)
            {
                double scale = Math.Sqrt((double)nx0 * ny0 / targetMaxCells);
                double newCell = cellSize * scale;
                warnings.Add(
                    $"cell_size_m auto-scaled from {cellSize:F2} to {newCell:F2} m to keep grid ≤ {targetMaxCells:N0} cells " +
                    $"(surface span {width:F0}×{height:F0} m).");
                cellSize = newCell;
            }
            int nx = Math.Max(1, (int)Math.Ceiling(width / cellSize));
            int ny = Math.Max(1, (int)Math.Ceiling(height / cellSize));

            var envelope = new Envelope(ext.MinPoint.X, ext.MinPoint.X + nx * cellSize,
                                        ext.MinPoint.Y, ext.MinPoint.Y + ny * cellSize);

            // 3. Build walkable mask by RASTERISING TIN triangles into the cell grid.
            //    DO NOT use FindTriangleAtXY per cell — Civil 3D throws PointNotOnEntityException
            //    on every off-surface query, and 200k+ exceptions add minutes of stack-walking
            //    to a routing call that should take seconds.
            //
            //    Two-pass, with a SLIVER FILTER between them. An unconstrained Civil 3D TIN
            //    triangulates to the convex hull of its points (triangles ≈ 2·points), so it
            //    bridges every concave bay and the inside of a curved survey band with long thin
            //    "sliver" triangles. Those slivers are NOT real ground: if left walkable the
            //    router rides across them (road cuts off-surface) AND the containment readback
            //    then certifies the off-surface road as "contained". So:
            //      Pass 1 — enumerate triangles once; skip anything Civil 3D itself hides
            //               (IsVisible=false: boundary/max-length/hide build settings), and
            //               cache the visible triangles' planar coords + slope + longest/shortest
            //               edge.
            //      Filter — two scale-aware signals separate real ground from bridging slivers:
            //               (1) LENGTH  — longest edge > (caller override OR median × factor):
            //                   a long edge means a data gap (no survey points to subdivide it).
            //               (2) THINNESS — aspect ratio (longest/shortest edge) > MaxAspectRatio:
            //                   a Delaunay triangulation maximises the min angle, so it only
            //                   produces needle-thin triangles at the hull / across gaps. Thinness
            //                   is therefore a scale-free sliver signal that catches mid-length
            //                   needles the length test misses — without dropping legitimately
            //                   coarse-but-fat ground (which length-alone could not avoid).
            //      Pass 2 — rasterise ONLY triangles that pass BOTH signals.
            int triCount;
            try { triCount = surface.Triangles.Count; } catch { triCount = 0; }
            int cap = triCount > 0 ? triCount : 4;

            int trisProcessed = 0;
            int trianglesHidden = 0;   // Civil 3D IsVisible=false (already-cleaned surfaces)
            var triX1 = new List<double>(cap); var triY1 = new List<double>(cap);
            var triX2 = new List<double>(cap); var triY2 = new List<double>(cap);
            var triX3 = new List<double>(cap); var triY3 = new List<double>(cap);
            var triTan = new List<double>(cap);
            var triLongEdge = new List<double>(cap);
            var triShortEdge = new List<double>(cap);
            try
            {
                foreach (TinSurfaceTriangle tri in surface.Triangles)
                {
                    if ((++trisProcessed & 0x3FFF) == 0 && ct.IsCancellationRequested)
                        return BuildResult.Fail("Surface sampling cancelled.", warnings);

                    bool visible;
                    Point3d p1, p2, p3;
                    try
                    {
                        // IsVisible is the authoritative flag Civil 3D sets when the surface
                        // HAS a boundary / max-triangle-length / hidden-edit build setting.
                        // Honouring it first means we never re-include ground the engineer
                        // explicitly hid, nor punch holes the platform already vetted. On an
                        // unconstrained TIN (the MK case) every triangle is visible, so the
                        // length/thinness heuristic below does the real work.
                        visible = tri.IsVisible;
                        p1 = tri.Vertex1.Location;
                        p2 = tri.Vertex2.Location;
                        p3 = tri.Vertex3.Location;
                    }
                    catch { continue; }
                    if (!visible) { trianglesHidden++; continue; }

                    // Triangle plane normal n = (p2-p1) × (p3-p1).
                    // Slope (rise/run) of the plane = sqrt(nx²+ny²) / |nz|.
                    double ux = p2.X - p1.X, uy = p2.Y - p1.Y, uz = p2.Z - p1.Z;
                    double vx = p3.X - p1.X, vy = p3.Y - p1.Y, vz = p3.Z - p1.Z;
                    double nxN = uy * vz - uz * vy;
                    double nyN = uz * vx - ux * vz;
                    double nzN = ux * vy - uy * vx;
                    double tanSlope = (Math.Abs(nzN) < 1e-9)
                        ? 1.0   // near-vertical plane, treat as steep
                        : Math.Sqrt(nxN * nxN + nyN * nyN) / Math.Abs(nzN);

                    var (longEdge, shortEdge) = TriangleEdgesXY(
                        p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);

                    triX1.Add(p1.X); triY1.Add(p1.Y);
                    triX2.Add(p2.X); triY2.Add(p2.Y);
                    triX3.Add(p3.X); triY3.Add(p3.Y);
                    triTan.Add(tanSlope);
                    triLongEdge.Add(longEdge);
                    triShortEdge.Add(shortEdge);
                }
            }
            catch (Exception ex)
            {
                return BuildResult.Fail($"Triangle iteration failed: {ex.Message}", warnings);
            }

            int trianglesTotal = triLongEdge.Count;
            if (trianglesTotal == 0)
                return BuildResult.Fail(
                    "Surface has no visible triangles; cannot build a walkable region.", warnings);

            double medianEdge = MedianOf(triLongEdge);
            double p90Edge = PercentileOf(triLongEdge, 0.90);
            double p99Edge = PercentileOf(triLongEdge, 0.99);
            // Length gate for the needle test (only SELECTS long triangles; thinness decides drop).
            double edgeThreshold = maxTriangleEdgeM > 0
                ? maxTriangleEdgeM
                : ComputeAutoMaxEdge(triLongEdge, AutoEdgeFactor, MinEdgeFloorM);
            // Pathological-mega backstop: drop any triangle (fat or thin) whose longest edge spans a
            // large fraction of the whole drawing — a convex-hull triangle, never real ground.
            double hugeEdgeCap = Math.Max(HugeEdgeAbsFloorM, HugeEdgeSpanFraction * Math.Max(width, height));

            bool[,] walkable = new bool[nx, ny];
            double[,] slope = new double[nx, ny];
            int walkableCount = 0;
            int trianglesKept = 0;
            int droppedThin = 0;    // long AND thin → needle bridge
            int droppedMega = 0;    // longest edge > hugeEdgeCap → hull mega-triangle
            int keptLongFat = 0;    // long but FAT → legitimate coarse / sparse open ground, KEPT
            try
            {
                for (int t = 0; t < trianglesTotal; t++)
                {
                    if ((t & 0x3FFF) == 0 && ct.IsCancellationRequested)
                        return BuildResult.Fail("Surface rasterisation cancelled.", warnings);

                    // Sliver filter: drop hull-spanning mega-triangles and long THIN needle bridges
                    // (the slivers a Delaunay TIN draws across the hull / concave bays). Keep large
                    // FAT triangles — legitimately coarse or sparsely-surveyed open ground — so the
                    // road can use that space instead of hugging the dense-data edge.
                    bool tooLong = triLongEdge[t] > edgeThreshold;
                    bool thin = triShortEdge[t] > 1e-9
                        && (triLongEdge[t] / triShortEdge[t]) > MaxAspectRatio;
                    if (triLongEdge[t] > hugeEdgeCap) { droppedMega++; continue; }
                    if (tooLong && thin) { droppedThin++; continue; }
                    if (tooLong) keptLongFat++;
                    trianglesKept++;

                    double a1X = triX1[t], a1Y = triY1[t];
                    double a2X = triX2[t], a2Y = triY2[t];
                    double a3X = triX3[t], a3Y = triY3[t];
                    double tanSlope = triTan[t];

                    double minX = Math.Min(a1X, Math.Min(a2X, a3X));
                    double maxX = Math.Max(a1X, Math.Max(a2X, a3X));
                    double minY = Math.Min(a1Y, Math.Min(a2Y, a3Y));
                    double maxY = Math.Max(a1Y, Math.Max(a2Y, a3Y));

                    int cgxMin = Math.Max(0, (int)Math.Floor((minX - envelope.MinX) / cellSize));
                    int cgxMax = Math.Min(nx - 1, (int)Math.Floor((maxX - envelope.MinX) / cellSize));
                    int cgyMin = Math.Max(0, (int)Math.Floor((minY - envelope.MinY) / cellSize));
                    int cgyMax = Math.Min(ny - 1, (int)Math.Floor((maxY - envelope.MinY) / cellSize));

                    for (int gx = cgxMin; gx <= cgxMax; gx++)
                    {
                        double cx = envelope.MinX + (gx + 0.5) * cellSize;
                        for (int gy = cgyMin; gy <= cgyMax; gy++)
                        {
                            if (walkable[gx, gy]) continue;   // already covered by an earlier triangle
                            double cy = envelope.MinY + (gy + 0.5) * cellSize;
                            if (PointInTriangle(cx, cy, a1X, a1Y, a2X, a2Y, a3X, a3Y))
                            {
                                walkable[gx, gy] = true;
                                slope[gx, gy] = tanSlope;
                                walkableCount++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return BuildResult.Fail($"Triangle rasterisation failed: {ex.Message}", warnings);
            }

            string sliverSummary =
                $"sliver filter: kept {trianglesKept:N0}/{trianglesTotal:N0} triangles " +
                $"(of which {keptLongFat:N0} long-but-fat coarse/open ground kept); " +
                $"dropped {droppedThin:N0} needle-thin + {droppedMega:N0} hull-mega, " +
                $"{trianglesHidden:N0} hidden by Civil 3D; " +
                $"length-gate {edgeThreshold:F1} m, mega-cap {hugeEdgeCap:F0} m, aspect > {MaxAspectRatio:F0}; " +
                $"edge median/p90/p99 {medianEdge:F1}/{p90Edge:F1}/{p99Edge:F1} m; " +
                $"{walkableCount:N0} walkable cells.";
            warnings.Add(sliverSummary);

            if (walkableCount == 0)
                return BuildResult.Fail(
                    "Surface sampling produced no walkable cells after the sliver filter " +
                    $"(max-edge {edgeThreshold:F1} m, median {medianEdge:F1} m). " +
                    "Pass a larger max_triangle_edge_m or check surface coverage.", warnings);

            // 3b. Close isolated PINHOLES punched by the sliver filter. Measured on the road-73
            //     survey (2026-07-28, via the live MCP): 489 of 278k triangles were dropped as
            //     needle-thin — a rounding error overall, but a handful sat directly under the
            //     existing road and, at the auto-scaled 7.7 m cell size, each became a single
            //     unwalkable cell ringed by 7-8 walkable ones. Consequences were severe and
            //     invisible: every tangent-fit chord crossing one was rejected (road drawn as a
            //     dense PI chain instead of long tangents), and the containment gate reported
            //     those stations as "no ground data" and rolled the whole axis back — while the
            //     TIN itself returned perfectly good elevations there (FindElevationAtXY).
            //     A cell surrounded on (nearly) all sides by buildable ground is a rasterisation
            //     artifact, not a void: a genuine off-surface excursion (a concave bay bridged by
            //     hull slivers) is WIDE and survives this, so the P0-02 gate keeps its teeth.
            //     Runs BEFORE obstacle subtraction so an engineer's no-build zone is never filled.
            int holesFilled = FillIsolatedHoles(walkable, nx, ny);
            walkableCount += holesFilled;
            if (holesFilled > 0)
            {
                string holeMsg = $"mask pinholes closed: {holesFilled:N0} cell(s) " +
                                 $"({100.0 * holesFilled / Math.Max(1, walkableCount):F2}% of walkable) " +
                                 $"at {cellSize:F1} m cells.";
                warnings.Add(holeMsg);
                try { Utilities.MahodLogger.Info($"[mask] {holeMsg} {sliverSummary}"); } catch { }
            }

            // 4. Subtract obstacles (closed polylines on obstacleLayer) from the mask.
            int obstacleCount = 0;
            if (!string.IsNullOrEmpty(obstacleLayer))
            {
                obstacleCount = ApplyObstacleSubtraction(tr, walkable, envelope, cellSize, nx, ny, obstacleLayer, warnings, ct);
                // Recount walkable after obstacles
                walkableCount = 0;
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny; y++)
                        if (walkable[x, y]) walkableCount++;
            }

            // 5. Smooth the slope field over walkable cells with a 3x3 box blur.
            //    Single triangles produce per-cell slope steps that look like noise to
            //    the road-affinity discount; one pass of mean filtering is enough to
            //    let the discount "see" a coherent flat strip without bleeding non-
            //    walkable zeros into the road interior.
            BlurSlopeOverWalkable(slope, walkable, nx, ny);

            return new BuildResult
            {
                Success = true,
                Walkable = walkable,
                Slope = slope,
                Envelope = envelope,
                CellSize = cellSize,
                Nx = nx,
                Ny = ny,
                CellsWalkable = walkableCount,
                TrianglesTotal = trianglesTotal,
                TrianglesKept = trianglesKept,
                MaxEdgeThresholdM = edgeThreshold,
                MedianEdgeM = medianEdge,
                P90EdgeM = p90Edge,
                P99EdgeM = p99Edge,
                TrianglesDroppedThin = droppedThin,
                TrianglesDroppedMega = droppedMega,
                TrianglesKeptLongFat = keptLongFat,
                TrianglesHidden = trianglesHidden,
                CellsHoleFilled = holesFilled,
                SurfaceName = surface.Name,
                SurfaceId = surface.ObjectId,
                ObstacleCount = obstacleCount,
                Warnings = warnings,
            };
        }

        // ── Sliver-filter constants + pure helpers (unit-testable, no Civil 3D) ──

        /// <summary>
        /// Length gate for the needle test: a triangle is only a candidate sliver when its longest
        /// edge exceeds median × this factor (floored at <see cref="MinEdgeFloorM"/>). This only
        /// SELECTS long triangles; whether a long triangle is actually dropped is then decided by
        /// the thinness test (<see cref="MaxAspectRatio"/>). Large-but-FAT triangles (legitimately
        /// coarse / sparsely-surveyed open ground) pass the length gate but fail the thinness test,
        /// so they are KEPT — the router can still use that open ground instead of hugging the dense
        /// data edge.
        /// </summary>
        public const double AutoEdgeFactor = 4.0;

        /// <summary>Floor (m) for the auto length gate so a very dense survey
        /// (tiny median) still tolerates ordinary triangle variation and small interior gaps.</summary>
        public const double MinEdgeFloorM = 30.0;

        /// <summary>
        /// Aspect ratio (longest edge / shortest edge) above which a long triangle is a needle-thin
        /// sliver. A Delaunay triangulation maximises the minimum angle, so dense real ground — and
        /// large but genuine sparse ground — is near-equilateral (aspect ≈ 1-4); the long thin
        /// needles only appear bridging the convex hull / concave bays / the inside of a curved
        /// band. Thinness is therefore the discriminator that drops bridges while keeping fat coarse
        /// ground. 8 is a conservative cut that keeps even moderately-elongated boundary triangles.
        /// </summary>
        public const double MaxAspectRatio = 8.0;

        /// <summary>Absolute floor (m) for the pathological mega-triangle backstop.</summary>
        public const double HugeEdgeAbsFloorM = 200.0;

        /// <summary>
        /// Fraction of the surface span (max of width/height) above which ANY triangle — fat or thin
        /// — is dropped as a pathological convex-hull mega-triangle. Keeps ordinary sparse open
        /// areas usable while preventing a single hull triangle that spans a large fraction of the
        /// whole drawing from making an empty quadrant "walkable".
        /// </summary>
        public const double HugeEdgeSpanFraction = 0.25;

        /// <summary>Longest of a triangle's three planar (XY) edges.</summary>
        public static double TriangleLongestEdgeXY(
            double x1, double y1, double x2, double y2, double x3, double y3)
            => TriangleEdgesXY(x1, y1, x2, y2, x3, y3).longest;

        /// <summary>Longest and shortest of a triangle's three planar (XY) edges.</summary>
        public static (double longest, double shortest) TriangleEdgesXY(
            double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double e1 = Hypot(x2 - x1, y2 - y1);
            double e2 = Hypot(x3 - x2, y3 - y2);
            double e3 = Hypot(x1 - x3, y1 - y3);
            double longest = Math.Max(e1, Math.Max(e2, e3));
            double shortest = Math.Min(e1, Math.Min(e2, e3));
            return (longest, shortest);
        }

        private static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

        /// <summary>
        /// Drop decision for one triangle. True (drop) when EITHER:
        ///   • it is a pathological mega-triangle (<paramref name="longestEdge"/> &gt;
        ///     <paramref name="hugeEdgeCapM"/>) — a convex-hull triangle spanning a large fraction
        ///     of the whole drawing; OR
        ///   • it is a needle bridge: long (longest edge &gt; <paramref name="lengthThresholdM"/>)
        ///     AND thin (aspect ratio &gt; <paramref name="maxAspectRatio"/>).
        /// A long-but-FAT triangle (legitimately coarse / sparsely-surveyed open ground) is KEPT,
        /// so the router can use that open ground instead of being pinned to the dense data edge.
        /// </summary>
        public static bool IsSliver(double longestEdge, double shortestEdge,
                                    double lengthThresholdM, double maxAspectRatio, double hugeEdgeCapM)
        {
            if (longestEdge > hugeEdgeCapM) return true;
            if (longestEdge > lengthThresholdM && shortestEdge > 1e-9 &&
                (longestEdge / shortestEdge) > maxAspectRatio) return true;
            return false;
        }

        /// <summary>
        /// Data-driven max-edge threshold: median(longest edges) × <paramref name="factor"/>,
        /// floored at <paramref name="floorM"/>. The median is robust to the long sliver tail
        /// (slivers are a small fraction of triangles), so the threshold tracks the real
        /// data density and scales correctly for both dense urban and sparse rural surveys.
        /// Returns <paramref name="floorM"/> for an empty input.
        /// </summary>
        public static double ComputeAutoMaxEdge(IReadOnlyList<double> longestEdges, double factor, double floorM)
        {
            if (longestEdges == null || longestEdges.Count == 0) return floorM;
            double median = MedianOf(longestEdges);
            return Math.Max(floorM, median * factor);
        }

        /// <summary>Median of a value list (does not mutate the input).</summary>
        public static double MedianOf(IReadOnlyList<double> values) => PercentileOf(values, 0.50);

        /// <summary>
        /// Linear-interpolated percentile (<paramref name="q"/> in [0,1]) of a value list.
        /// Does not mutate the input. Returns 0 for an empty list.
        /// </summary>
        public static double PercentileOf(IReadOnlyList<double> values, double q)
        {
            if (values == null || values.Count == 0) return 0.0;
            var sorted = values.ToArray();
            Array.Sort(sorted);
            int n = sorted.Length;
            if (n == 1) return sorted[0];
            if (q <= 0) return sorted[0];
            if (q >= 1) return sorted[n - 1];
            double pos = q * (n - 1);
            int lo = (int)Math.Floor(pos);
            double frac = pos - lo;
            return sorted[lo] + frac * (sorted[lo + 1] - sorted[lo]);
        }

        /// <summary>
        /// Closes isolated pinholes in the walkable mask: an unwalkable cell with at least
        /// <paramref name="minWalkableNeighbours"/> of its 8 neighbours walkable is a
        /// rasterisation artifact (a dropped sliver triangle, or a cell centre that landed in a
        /// data gap), not a hole in the ground, so it becomes walkable.
        ///
        /// Bounded by construction: with the default 6-of-8 threshold and 2 passes, single cells
        /// and 1×2 cracks close, while any void 2×2 or larger survives every pass (each of its
        /// cells sees at most 5 walkable neighbours). A genuine off-surface region — a concave
        /// bay whose bridging hull slivers the filter removed — is far wider than that and is
        /// therefore preserved, which is what keeps the containment gate honest.
        /// </summary>
        /// <returns>Number of cells turned walkable.</returns>
        public static int FillIsolatedHoles(bool[,] walkable, int nx, int ny,
                                            int minWalkableNeighbours = 6, int passes = 2)
        {
            if (walkable == null) throw new ArgumentNullException(nameof(walkable));
            int filled = 0;
            var toFill = new List<(int X, int Y)>();
            for (int pass = 0; pass < passes; pass++)
            {
                toFill.Clear();
                for (int x = 0; x < nx; x++)
                {
                    for (int y = 0; y < ny; y++)
                    {
                        if (walkable[x, y]) continue;
                        int n = 0;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int qx = x + dx, qy = y + dy;
                                if (qx >= 0 && qy >= 0 && qx < nx && qy < ny && walkable[qx, qy]) n++;
                            }
                        }
                        if (n >= minWalkableNeighbours) toFill.Add((x, y));
                    }
                }
                if (toFill.Count == 0) break;
                // Apply after the scan so cells filled in this pass cannot cascade within it.
                foreach (var (x, y) in toFill) walkable[x, y] = true;
                filled += toFill.Count;
            }
            return filled;
        }

        // 3x3 mean filter over walkable cells only. Non-walkable neighbours are
        // skipped so steep "outside" zeros don't bleed into the road interior.
        private static void BlurSlopeOverWalkable(double[,] slope, bool[,] walkable, int nx, int ny)
        {
            var src = (double[,])slope.Clone();
            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++)
                {
                    if (!walkable[x, y]) continue;
                    double sum = 0;
                    int n = 0;
                    int xMin = Math.Max(0, x - 1), xMax = Math.Min(nx - 1, x + 1);
                    int yMin = Math.Max(0, y - 1), yMax = Math.Min(ny - 1, y + 1);
                    for (int xx = xMin; xx <= xMax; xx++)
                        for (int yy = yMin; yy <= yMax; yy++)
                        {
                            if (!walkable[xx, yy]) continue;
                            sum += src[xx, yy];
                            n++;
                        }
                    if (n > 0) slope[x, y] = sum / n;
                }
            }
        }

        // ── Geometry helper ─────────────────────────────────────────────────
        // Half-plane test via cross-products. A point is inside the triangle iff
        // all three signed areas have the same sign (allowing zero on edges).
        private static bool PointInTriangle(double px, double py,
                                            double ax, double ay,
                                            double bx, double by,
                                            double cx, double cy)
        {
            double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        // ── Surface resolution ──────────────────────────────────────────────
        private static TinSurface? ResolveSurface(CivilDocument civilDoc, Transaction tr, string? surfaceName)
        {
            ObjectIdCollection ids;
            try { ids = civilDoc.GetSurfaceIds(); }
            catch { return null; }

            TinSurface? firstTin = null;
            foreach (ObjectId id in ids)
            {
                var s = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                if (s == null) continue;
                if (!string.IsNullOrEmpty(surfaceName) &&
                    s.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
                firstTin ??= s;
            }
            return string.IsNullOrEmpty(surfaceName) ? firstTin : null;
        }

        // ── Obstacle subtraction ────────────────────────────────────────────
        private static int ApplyObstacleSubtraction(
            Transaction tr,
            bool[,] walkable,
            Envelope env,
            double cellSize,
            int nx,
            int ny,
            string obstacleLayer,
            List<string> warnings,
            CancellationToken ct)
        {
            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            var obstacles = ReadClosedPolylinesOnLayer(tr, obstacleLayer, factory, warnings);
            if (obstacles.Count == 0) return 0;

            // Union obstacles for a single point-in-polygon query per cell.
            Geometry obstUnion = obstacles.Count == 1
                ? (Geometry)obstacles[0]
                : CascadedPolygonUnion.Union(obstacles.Cast<Geometry>().ToList());

            var prepared = PreparedGeometryFactory.Prepare(obstUnion);
            int sampled = 0;
            for (int gx = 0; gx < nx; gx++)
            {
                double wx = env.MinX + (gx + 0.5) * cellSize;
                for (int gy = 0; gy < ny; gy++)
                {
                    if (!walkable[gx, gy]) continue;
                    double wy = env.MinY + (gy + 0.5) * cellSize;
                    sampled++;
                    if ((sampled & 0xFFF) == 0 && ct.IsCancellationRequested)
                        return obstacles.Count;   // partial mask is fine; caller will see cancellation downstream
                    var pt = factory.CreatePoint(new Coordinate(wx, wy));
                    if (prepared.Contains(pt))
                        walkable[gx, gy] = false;
                }
            }
            return obstacles.Count;
        }

        private static List<Polygon> ReadClosedPolylinesOnLayer(
            Transaction tr,
            string layerName,
            GeometryFactory factory,
            List<string> warnings)
        {
            var polygons = new List<Polygon>();
            try
            {
                var db = HostApplicationServices.WorkingDatabase;
                var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (lt == null || !lt.Has(layerName))
                {
                    warnings.Add($"Obstacle layer '{layerName}' does not exist; skipping subtraction.");
                    return polygons;
                }
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null) return polygons;
                var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return polygons;

                foreach (ObjectId entId in btr)
                {
                    var ent = tr.GetObject(entId, OpenMode.ForRead) as AcEntity;
                    if (ent == null) continue;
                    if (!ent.Layer.Equals(layerName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ent is not Polyline pl || pl.NumberOfVertices < 3) continue;

                    var coords = new List<Coordinate>(pl.NumberOfVertices + 1);
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        var p = pl.GetPoint2dAt(i);
                        coords.Add(new Coordinate(p.X, p.Y));
                    }
                    if (!coords[0].Equals2D(coords[^1])) coords.Add(new Coordinate(coords[0].X, coords[0].Y));
                    if (coords.Count < 4) continue;
                    try { polygons.Add(factory.CreatePolygon(coords.ToArray())); }
                    catch (Exception ex) { warnings.Add($"Skipped malformed obstacle polyline: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to scan obstacle layer '{layerName}': {ex.Message}");
            }
            return polygons;
        }
    }
}
