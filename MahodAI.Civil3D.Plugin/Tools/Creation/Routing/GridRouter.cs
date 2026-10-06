using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Grid-based A* router. Operates on a pre-built walkable mask produced by
    /// <see cref="BuildableRegion"/>; no Civil 3D dependency, so unit-testable.
    /// </summary>
    public static class GridRouter
    {
        /// <summary>Machine-readable failure codes so callers can map to user-facing messages.</summary>
        public static class FailureCodes
        {
            public const string EmptyMask = "EMPTY_MASK";
            public const string Cancelled = "CANCELLED";
            public const string NoPath = "NO_PATH";
            /// <summary>Start click is outside the surface AABB or beyond the snap threshold.</summary>
            public const string StartOffSurface = "START_OFF_SURFACE";
            /// <summary>End click is outside the surface AABB or beyond the snap threshold.</summary>
            public const string EndOffSurface = "END_OFF_SURFACE";
            /// <summary>No corridor stays at least the requested clearance from the boundary on both sides.</summary>
            public const string ClearanceInsufficient = "CLEARANCE_INSUFFICIENT";
            /// <summary>An engineer-drawn priority span leaves the surface and no detour exists across it.</summary>
            public const string PrioritySpanInfeasible = "PRIORITY_SPAN_INFEASIBLE";
        }

        /// <summary>
        /// Describes what happened to a clicked endpoint: whether it had to be snapped to the
        /// nearest walkable cell, how far the snap moved it, and the point actually used for
        /// the routed geometry. The raw click is NEVER substituted back after snapping.
        /// </summary>
        public sealed class EndpointSnap
        {
            public bool Snapped { get; init; }
            public double DistanceM { get; init; }
            /// <summary>The point used as the route endpoint (raw click if not snapped).</summary>
            public Pt2 Point { get; init; }
        }

        public sealed class RouteResult
        {
            public bool Success { get; init; }
            public Pt2[] Path { get; init; } = Array.Empty<Pt2>();
            public int CellsTotal { get; init; }
            public int CellsWalkable { get; init; }
            public int GridNx { get; init; }
            public int GridNy { get; init; }
            public double CellSize { get; init; }
            public string? FailureReason { get; init; }
            public string? FailureCode { get; init; }
            public EndpointSnap? StartSnap { get; init; }
            public EndpointSnap? EndSnap { get; init; }
            /// <summary>
            /// Distance-to-boundary field (in cells) computed by the router's multi-source BFS,
            /// exposed so the post-creation containment readback can honestly verify clearance.
            /// </summary>
            public int[,]? DistanceToBoundary { get; init; }

            /// <summary>
            /// DEBUG ONLY (populated when Route is called with debug=true): the raw A* path
            /// (cell-centre polyline) BEFORE simplification / densification / curve fitting, so
            /// a debug overlay can show exactly what the search chose vs. what was drawn.
            /// </summary>
            public Pt2[] RawPath { get; init; } = Array.Empty<Pt2>();

            /// <summary>
            /// Number of engineer-drawn priority spans that had to be rerouted off the drawn
            /// line because the surface would not host the straight segment (0 for a normal
            /// route, or a priority route the surface accepted verbatim).
            /// </summary>
            public int ReroutedSpanCount { get; init; }

            /// <summary>
            /// How the PI list was produced: <c>"tangent_arc_fit"</c> when
            /// <see cref="TangentArcFitter"/> produced a legal engineer-style alignment (long
            /// tangents, one curve per real direction change), or <c>"densified"</c> for the
            /// legacy deviation-densified PI list. Reported to the caller so an engineer can
            /// tell which geometry engine drew the road.
            /// </summary>
            public string PathSource { get; init; } = "densified";

            /// <summary>Straight tangents in an accepted tangent-arc fit (0 when densified).</summary>
            public int TangentCount { get; init; }

            public static RouteResult Ok(Pt2[] path, int total, int walkable, int nx, int ny, double cell,
                                         EndpointSnap? startSnap = null, EndpointSnap? endSnap = null,
                                         int[,]? dtb = null, Pt2[]? rawPath = null,
                                         string pathSource = "densified", int tangentCount = 0)
                => new()
                {
                    Success = true,
                    Path = path,
                    CellsTotal = total,
                    CellsWalkable = walkable,
                    GridNx = nx,
                    GridNy = ny,
                    CellSize = cell,
                    StartSnap = startSnap,
                    EndSnap = endSnap,
                    DistanceToBoundary = dtb,
                    RawPath = rawPath ?? Array.Empty<Pt2>(),
                    PathSource = pathSource,
                    TangentCount = tangentCount,
                };

            public static RouteResult Fail(string reason, int total, int walkable, int nx, int ny, double cell,
                                           string? code = null)
                => new()
                {
                    Success = false,
                    FailureReason = reason,
                    FailureCode = code,
                    CellsTotal = total,
                    CellsWalkable = walkable,
                    GridNx = nx,
                    GridNy = ny,
                    CellSize = cell,
                };
        }

        /// <summary>
        /// Routes from <paramref name="a"/> to <paramref name="b"/> through the walkable mask.
        /// Uses Theta* (any-angle A*) by default — parent-skip relaxations via line-of-sight
        /// produce piecewise-straight paths instead of the 8-direction staircase. Pass
        /// <paramref name="anyAngle"/>=false to fall back to the legacy octile A*.
        /// A "stay-away-from-boundary" cost bias pulls the route toward the medial axis.
        /// </summary>
        public static RouteResult Route(
            bool[,] walkable,
            Envelope env,
            double cellSize,
            Pt2 a,
            Pt2 b,
            CancellationToken ct,
            int maxPis = 20,
            bool anyAngle = true,
            double boundaryBias = 4.0,
            double maxTangentM = 0.0,
            double[,]? costMul = null,
            double maxSnapM = 20.0,
            double minClearanceM = 0.0,
            bool centered = false,
            double centredness = 6.0,
            double targetClearanceM = 0.0,
            double elasticFloorM = 0.0,
            bool elasticSmooth = true,
            double widthWindowK = 0.0,
            bool centerBalance = false,
            double designRadiusM = 0.0,
            double centerDeadbandFrac = 0.0,
            bool debug = false,
            double spiralLenM = 0.0,
            double minTangentM = 0.0)
        {
            if (walkable == null) throw new ArgumentNullException(nameof(walkable));
            if (env == null) throw new ArgumentNullException(nameof(env));
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (boundaryBias < 0) boundaryBias = 0;
            if (maxTangentM < 0) maxTangentM = 0;

            int nx = walkable.GetLength(0);
            int ny = walkable.GetLength(1);
            int total = nx * ny;

            int walkableCount = 0;
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    if (walkable[x, y]) walkableCount++;
            if (walkableCount == 0)
                return RouteResult.Fail("walkable mask is empty", total, 0, nx, ny, cellSize,
                                        FailureCodes.EmptyMask);

            // Distance-to-boundary (in cells) via multi-source BFS from non-walkable cells
            int[,] dtb = MultiSourceBfsDistance(walkable, nx, ny, ct);
            if (ct.IsCancellationRequested)
                return RouteResult.Fail("cancelled during distance transform", total, walkableCount, nx, ny, cellSize,
                                        FailureCodes.Cancelled);

            // Minimum-clearance erosion threshold (in cells). minClearanceM = 0 → feature OFF
            // (every gate below is guarded with dtbThresholdCells > 0, so behaviour is unchanged).
            // NEAREST-cell rounding (not ceiling): on a coarse auto-scaled grid (e.g. 7.7m cells on
            // a large surface) ceiling would enforce ~2× the requested clearance (9.5m→15.4m) and
            // reject valid corridors. Rounding to the nearest cell keeps the enforced clearance close
            // to the request while still guaranteeing the full cross-section fits on BOTH sides.
            // NOTE: ContainmentSampler MUST use this identical formula so routing and the post-creation
            // readback agree (a stricter readback would fail every route the router just produced).
            int dtbThresholdCells = minClearanceM > 0 && cellSize > 0
                ? Math.Max(1, (int)Math.Round(minClearanceM / cellSize, MidpointRounding.AwayFromZero))
                : 0;

            // Resolve endpoints. Strict contract (replaces the old clamp-and-continue):
            //   • Click outside the grid AABB → FAIL (WorldToCell would silently clamp to a
            //     boundary cell, turning an arbitrarily distant click into a "valid" endpoint).
            //   • Click inside the AABB but on a non-walkable cell → snap to the nearest
            //     walkable cell ONLY if the snap distance is within the threshold; otherwise FAIL.
            //   • When snapped, the SNAPPED point becomes the route endpoint — the raw click is
            //     never substituted back (the old behaviour reintroduced the off-surface error).
            // Threshold floor of 2 cells tolerates rasterisation edge effects on coarse grids.
            double snapThresholdM = Math.Max(maxSnapM, 2.0 * cellSize);

            var startResolve = ResolveEndpoint(a, walkable, env, cellSize, nx, ny, snapThresholdM,
                                               dtb, dtbThresholdCells, minClearanceM);
            if (startResolve.snap == null)
            {
                string startCode = startResolve.clearance
                    ? FailureCodes.ClearanceInsufficient : FailureCodes.StartOffSurface;
                return RouteResult.Fail($"start point is not on the surface ({startResolve.error})",
                                        total, walkableCount, nx, ny, cellSize, startCode);
            }
            var endResolve = ResolveEndpoint(b, walkable, env, cellSize, nx, ny, snapThresholdM,
                                             dtb, dtbThresholdCells, minClearanceM);
            if (endResolve.snap == null)
            {
                string endCode = endResolve.clearance
                    ? FailureCodes.ClearanceInsufficient : FailureCodes.EndOffSurface;
                return RouteResult.Fail($"end point is not on the surface ({endResolve.error})",
                                        total, walkableCount, nx, ny, cellSize, endCode);
            }

            EndpointSnap startSnap = startResolve.snap;
            EndpointSnap endSnap = endResolve.snap;
            Pt2 aUsed = startSnap.Point;
            Pt2 bUsed = endSnap.Point;
            (int ax, int ay) = (startResolve.cx, startResolve.cy);
            (int bx, int by) = (endResolve.cx, endResolve.cy);

            // Validate costMul dimensions if supplied; ignore (treat as null) on mismatch.
            double[,]? validatedCostMul = costMul;
            if (validatedCostMul != null &&
                (validatedCostMul.GetLength(0) != nx || validatedCostMul.GetLength(1) != ny))
            {
                validatedCostMul = null;
            }

            // Centered ("medial-axis") cost field. The centered cost charges each cell in
            // proportion to how far BELOW the global maximum clearance it sits, so the optimum
            // rides the band's central spine. Unlike the legacy (1 + alpha/dtb) nudge — which
            // saturates a few cells off the wall and so cannot win against the length term —
            // this keeps a strong gradient across the FULL width.
            //
            // Crucially it uses a SMOOTH Chamfer distance transform, NOT the integer 4-connected
            // BFS dtb. The integer field has flat PLATEAUS of equal value through a wide band,
            // where the ridge is ambiguous and the octile path wobbles cell-to-cell (each wobble
            // becomes a spurious PI). The Chamfer field is near-Euclidean and continuous, so it
            // has a single clean ridge → a smooth centred path with far fewer bends.
            double[,]? centerField = null;
            double centerMax = 1.0;
            // Per-cell centering normaliser (null = legacy single global centerMax). When set, the
            // cost normalises each cell against the LOCAL band width instead of the global maximum,
            // so the spine centers 1:1 with the surface width at every station (a 150 m neck and a
            // 751 m room both center) — a fixed global scalar can only center one width at a time.
            double[,]? localMax = null;
            if (centered)
            {
                if (centredness < 0) centredness = 0;
                centerField = ChamferDistanceFieldCells(walkable, nx, ny);
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny; y++)
                        if (walkable[x, y] && centerField[x, y] > centerMax)
                            centerMax = centerField[x, y];
                double globalRidge = centerMax;   // uncapped global half-width, for window sizing

                // Target-clearance CAP (the "adequate, not maximum" fix). The medial-axis cost
                // rewards distance-to-boundary; with no ceiling, a wide band (clearance 40-240 m
                // where the road needs ~7 m) makes the route bow toward the widest spot at every
                // step. Capping the reward at targetClearanceM means every cell with at least that
                // clearance is equally cheap (factor 1), so among them the search takes the SHORTEST
                // (straight) path — killing unnecessary bows — while still centering inside genuinely
                // narrow necks (clearance < target). centerMax doubles as both the cap and the
                // normaliser in the cost, so just clamp it down to the target (in cells).
                if (targetClearanceM > 0 && cellSize > 0)
                {
                    double targetCells = targetClearanceM / cellSize;
                    centerMax = Math.Max(1.0, Math.Min(targetCells, centerMax));
                }

                // Per-cell LOCAL ridge-width ceiling (opt-in via widthWindowK). For each cell take a
                // separable box-MAX of the Chamfer field over a window whose half-size scales with
                // the LOCAL clearance (k * centerField), so a cell's normaliser is "the widest ridge
                // within reach of its own band" — reach grows 1:1 with band width. Box-MAX (not the
                // rejected min-along-path grassfire) preserves the centering gradient: an off-spine
                // cell inherits its band ridge's large value while a wall cell's tiny window keeps it
                // local. The same target_clearance_m cap is applied PER CELL as the lobe-dive guard.
                if (widthWindowK > 0)
                {
                    int maxWindowCells = Math.Max(8, (int)Math.Ceiling(globalRidge));
                    localMax = LocalRidgeWidthCeiling(centerField, walkable, nx, ny, widthWindowK, maxWindowCells);
                    double targetCells = (targetClearanceM > 0 && cellSize > 0) ? targetClearanceM / cellSize : 0;
                    for (int x = 0; x < nx; x++)
                        for (int y = 0; y < ny; y++)
                        {
                            if (!walkable[x, y]) { localMax[x, y] = 1.0; continue; }
                            double lm = Math.Max(localMax[x, y], Math.Max(centerField[x, y], 1e-9)); // >= own clearance, >0
                            if (targetCells > 0) lm = Math.Max(1.0, Math.Min(targetCells, lm));       // per-cell dive cap
                            localMax[x, y] = lm;
                        }
                }
            }

            // Centered mode MUST use per-cell octile A*, not Theta*. Theta*'s any-angle
            // re-parenting collapses to a straight line in open ground (every node is
            // re-parented to the start via line-of-sight), which ignores a smooth cost-field
            // gradient — it only honours a NARROW discount band (follow_existing) because the
            // direct LoS edge then crosses expensive cells. The centeredness field is smooth
            // everywhere, so only the faithful per-cell octile search climbs to the spine.
            bool useAnyAngle = anyAngle && !centered;
            List<(int x, int y)>? cells = AStar((ax, ay), (bx, by), walkable, dtb, nx, ny, cellSize, ct, useAnyAngle, boundaryBias, validatedCostMul, dtbThresholdCells, centered, centredness, centerField, centerMax, localMax);
            if (ct.IsCancellationRequested)
                return RouteResult.Fail("cancelled during A* search", total, walkableCount, nx, ny, cellSize,
                                        FailureCodes.Cancelled);
            if (cells == null)
            {
                // When a clearance floor is in force, an empty result almost always means the
                // eroded corridor pinches below the required width — message it specifically so
                // the caller can tell the engineer to widen the surface / reduce the road width.
                string noPathCode = dtbThresholdCells > 0
                    ? FailureCodes.ClearanceInsufficient : FailureCodes.NoPath;
                string noPathReason = dtbThresholdCells > 0
                    ? $"no path keeps {minClearanceM:F1} m clearance from the boundary on both sides"
                    : "no path between A and B inside the walkable region";
                return RouteResult.Fail(noPathReason,
                                        total, walkableCount, nx, ny, cellSize, noPathCode);
            }

            // Theta*'s parent-skip relaxation collapses long line-of-sight
            // stretches in the reconstructed path into single jumps. The
            // simplifier/EnsureLoS pass NEEDS the sparse representation —
            // Theta* anchors are exactly the topological bend points and
            // discarding them would force the simplifier to invent split
            // positions from the inflated bresenham line (which kicks the bend
            // off the natural corner). The densifier on the other hand NEEDS
            // a dense per-cell trace to sample intermediate PIs.
            //
            // Resolution: keep both representations. `rawPts` stays sparse
            // through simplification; we inflate to `denseRawPts` and a
            // sparse→dense index map just before the densifier runs.
            var rawPts = cells.Select(c => CellCenter(c.x, c.y, env, cellSize)).ToList();
            // Replace endpoints with the RESOLVED endpoints: the exact click when it was
            // walkable, or the snapped point when it wasn't. NEVER the raw off-surface click.
            rawPts[0] = aUsed;
            rawPts[^1] = bUsed;

            // Elastic shortcut smoother (centered mode only): straighten the dense centered
            // trace BEFORE simplification. A clearance objective alone oscillates — low target
            // hugs, high target swings. The smoother instead removes only the UNNEEDED bows:
            // it greedily replaces a bowed sub-run with the straight chord whenever that chord
            // stays on the mask AND every cell holds a clearance FLOOR; a genuine band curve /
            // neck (chord leaves the mask, or a cell drops below floor) is left intact. The floor
            // self-lowers in narrow necks (so they're never severed) but enforces a comfortable
            // margin in the wide band (so it cannot collapse onto a curve inside). Acceptance uses
            // the SAME integer dtb the containment readback uses, so anything kept also passes it.
            bool smoothed = false;
            if (centered && centerBalance && rawPts.Count >= 3)
            {
                // Ribbon centering: directly balance the road between the two surface edges. Unlike
                // a clearance-window cost (blind to the open side when the path hugs a wall), this
                // marches PERPENDICULAR to both edges from the path's actual position and shifts to
                // the midpoint — so it re-centers a hugging road regardless of how far the open side
                // runs. The integer-dtb min-clearance guard and the mask keep every move legal.
                int minClrCells = (minClearanceM > 0 && cellSize > 0)
                    ? Math.Max(1, (int)Math.Round(minClearanceM / cellSize, MidpointRounding.AwayFromZero)) : 1;
                double dsM = cellSize * 4.0;               // resample spacing (kills the cell-scale staircase)
                int maxReachCells = Math.Max(64, nx + ny); // safety cap on a perpendicular march
                rawPts = RibbonCenter(rawPts, walkable, dtb, env, cellSize, nx, ny,
                                      minClrCells, dsM, iters: 40, balanceLambda: 0.5, maxReachCells,
                                      designRadiusM, centerDeadbandFrac);
                smoothed = true;
            }
            else if (centered && elasticSmooth)
            {
                double floorM = elasticFloorM > 0 ? elasticFloorM
                              : (targetClearanceM > 0 ? targetClearanceM : 0.0);
                int comfortFloorCells = (floorM > 0 && cellSize > 0)
                    ? Math.Max(1, (int)Math.Round(floorM / cellSize, MidpointRounding.AwayFromZero))
                    : 0;
                if (comfortFloorCells > 0 && rawPts.Count >= 3)
                {
                    int maxShortcutCells = Math.Max(8, (int)Math.Ceiling(400.0 / cellSize));
                    rawPts = ElasticSmooth(rawPts, walkable, dtb, env, cellSize, nx, ny,
                                           comfortFloorCells, dtbThresholdCells,
                                           maxShortcutCells, maxIters: 12, lambda: 0.5);
                    smoothed = true;
                }
            }

            // DEBUG: snapshot the raw search path (cell centres, resolved endpoints) AFTER the
            // smoother, so the debug overlay + RouteDiagnostics show the path actually drawn.
            Pt2[] rawDebug = debug ? rawPts.ToArray() : Array.Empty<Pt2>();

            // ── Engineer-style geometry fit (preferred) ─────────────────────
            // Fit long straight tangents to the routed path and put ONE curve at each real
            // direction change, instead of densifying the path into a PI every time it bows a
            // couple of cells off its chord (which is what produced chains of short curves with
            // relaxed radii). The fitter only returns an alignment it has PROVEN drawable — every
            // tangent chord mask-legal and every curve bulge sampled inside the buildable area —
            // so a failure here simply falls through to the legacy densified chain below.
            int fitFloorCells = dtbThresholdCells > 0 ? dtbThresholdCells : 1;
            double minTangentUsed = minTangentM > 0
                ? minTangentM
                : Math.Max(2.0 * spiralLenM, 0.2 * designRadiusM);
            var fit = TangentArcFitter.Fit(
                rawPts, designRadiusM, spiralLenM, minTangentUsed,
                chordViable: (p0, p1) => ChordViable(p0, p1, walkable, dtb, env, cellSize, nx, ny,
                                                     fitFloorCells, fitFloorCells),
                pointLegal: p => PointLegal(p, walkable, dtb, env, cellSize, nx, ny, fitFloorCells));
            if (fit != null && fit.Pis.Length >= 2)
            {
                string fitMsg =
                    $"[tangent-fit] pis={fit.Pis.Length} tangents={fit.TangentCount} " +
                    $"straightness={fit.StraightnessFactor:F1} merged={fit.MergedPis} " +
                    $"tightTangent={fit.HasTightTangent} (raw pts={rawPts.Count})";
                System.Diagnostics.Debug.WriteLine("[MahodAI]" + fitMsg);
                try { Utilities.MahodLogger.Info(fitMsg); } catch { }
                return RouteResult.Ok(fit.Pis, total, walkableCount, nx, ny, cellSize,
                                      startSnap, endSnap, dtb, rawDebug,
                                      pathSource: "tangent_arc_fit", tangentCount: fit.TangentCount);
            }
            System.Diagnostics.Debug.WriteLine(
                "[MahodAI][tangent-fit] no legal fit — falling back to the densified PI chain");
            try { Utilities.MahodLogger.Info("[tangent-fit] no legal fit — falling back to the densified PI chain"); } catch { }

            // Direction-change-aware simplification. Replaces Douglas-Peucker
            // (which scores by perpendicular distance and is therefore curvature-
            // blind: a 90° corner with 0.5m deviation looks identical to a 0.5m
            // wobble on a straight road). CurvatureSimplifier walks the raw path
            // with a sliding window and keeps cells at local maxima of smoothed
            // direction-change angle — i.e. exactly the bend points. Smooth
            // curves with no sharp corner produce no peaks; their PIs come from
            // the deviation/length densifier downstream.
            (Pt2[] simplified, int[] simplifiedRawIdx) =
                CurvatureSimplifier.Simplify(rawPts, windowCells: 8,
                                             angleThresholdRad: 0.35, maxPis: maxPis);

            // Topological-bend fixer: the simplifier picks corners by angle on
            // the raw path, but with Theta* the raw path can collapse a 90°
            // corridor turn into a small angle (e.g. when A and B aren't on the
            // corridor-arm ends). For every consecutive pair of kept PIs whose
            // chord exits the walkable mask, find the raw cell with the
            // maximum perpendicular deviation and insert it. Recurse until
            // every chord is line-of-sight clear. This guarantees the
            // simplified alignment stays inside the corridor at every bend.
            (simplified, simplifiedRawIdx) = EnsureLosClearChords(
                simplified, simplifiedRawIdx, rawPts,
                walkable, env, cellSize, nx, ny);

            // Note: do NOT centre-pull yet. Centre-pull shifts anchors
            // laterally off their raw cell positions; running it here would
            // make the densifier see fake "deviation" between the centre-
            // pulled chord and the raw cells (which still sit on the original
            // Bresenham line) and insert detour PIs that zigzag back to the
            // original path. We densify on the unshifted anchors first, then
            // centre-pull all PIs (originals + densified) at the very end.

            // Inflate to a dense per-cell trace just for densifier consumption.
            // Theta* parent-skip leaves rawPts sparse (~7 cells for an 11 km
            // path) — densifier needs cells to sample, so we Bresenham-fill
            // between consecutive sparse anchors and produce a sparse→dense
            // index map. simplifiedRawIdx[i] points into rawPts (sparse); the
            // map turns it into the equivalent index into denseRawPts.
            List<Pt2> denseRawPts;
            int[] simplifiedDenseIdx;
            if (smoothed)
            {
                // The smoother already produced the path we want as `rawPts`; it is the raw
                // source for the densifier. Inflating from the original `cells` here would
                // resurrect the UN-smoothed octile trace and undo the straightening.
                denseRawPts = rawPts;
                simplifiedDenseIdx = (int[])simplifiedRawIdx.Clone();
                for (int i = 0; i < simplifiedDenseIdx.Length; i++)
                    simplifiedDenseIdx[i] = Math.Clamp(simplifiedDenseIdx[i], 0, denseRawPts.Count - 1);
            }
            else
            {
                var (denseCells, sparseToDense) = InflateCellsAlongLosWithMap(cells);
                denseRawPts = denseCells.Select(c => CellCenter(c.x, c.y, env, cellSize)).ToList();
                if (denseRawPts.Count > 0) denseRawPts[0] = aUsed;
                if (denseRawPts.Count > 1) denseRawPts[^1] = bUsed;
                simplifiedDenseIdx = new int[simplifiedRawIdx.Length];
                for (int i = 0; i < simplifiedRawIdx.Length; i++)
                {
                    int s = simplifiedRawIdx[i];
                    simplifiedDenseIdx[i] = (s >= 0 && s < sparseToDense.Length)
                        ? sparseToDense[s]
                        : Math.Min(Math.Max(0, s), denseRawPts.Count - 1);
                }
            }

            // Densification along the raw A* path. Two triggers:
            //   1. maxTangentM > 0  → break tangents longer than that into pieces.
            //   2. Always-on curvature trigger: any tangent whose raw A* path
            //      deviates from its chord by more than devThresholdM gets split.
            //      DP collapses cost-following raw paths back to straight chords
            //      whenever the chord is LoS-clear in the walkable mask — DP is
            //      cost-blind. The deviation trigger restores those PIs so the
            //      simplified alignment tracks the cost-discount field (e.g. an
            //      existing road) through curves instead of cutting corners.
            //   Midpoints are sampled from the raw A* cells between anchors so
            //   the densified path follows the corridor's actual curve.
            // devThreshold is DESIGN-AWARE. A flat 2-cell (6 m) threshold is far below road scale:
            // a single clean curve of radius R bows m = L²/8R off its chord, so 6 m is reached
            // after only L = √(48R) ≈ 138 m at R = 400 — i.e. the densifier used to chop one good
            // curve into a PI every ~140 m, each becoming its own spiral-curve-spiral. Allow the
            // bow a design-radius curve can absorb over a reasonable tangent instead, floored at
            // the old 2 cells (so a coarse grid still splits) and capped so genuinely sharp
            // corridor bends are still tracked.
            double devThresholdM = designRadiusM > 0
                ? Math.Max(cellSize * 2.0, Math.Min(25.0, designRadiusM * 0.05))
                : cellSize * 2.0;
            simplified = DensifyAlongRawPath(simplified, simplifiedDenseIdx, denseRawPts,
                                             walkable, env, cellSize, nx, ny,
                                             maxTangentM, devThresholdM);
            // CenterPullPis is a short-range (±8 cell) rescue that nudges PIs toward higher
            // dtb. In centered mode the path already rides the smooth medial ridge, so this
            // pass adds nothing but per-PI lateral jitter (it reads the integer dtb plateau and
            // can shift adjacent PIs to different tied cells) — skip it. It still runs in
            // shortest mode, where it is the only thing that laterally centres.
            if (!centered)
                simplified = CenterPullPis(simplified, walkable, dtb, env, cellSize, nx, ny, dtbThresholdCells);

            // Post-fit straightening — collapse near-collinear PIs (straight runs the
            // densifier drew as many grid-quantized segments) and drop minor PIs whose
            // curves can't fit the design radius, so CurveAttacher lays one clean tangent/
            // curve instead of halving radii into a wiggle. Removals stay mask-legal
            // (ChordViable on the same integer dtb the containment readback uses) and
            // radius-feasible, so this can only straighten — never push off-surface or
            // under the design radius. See AlignmentSimplifier.
            if (simplified.Length >= 3)
            {
                int straightenFloorCells = (minClearanceM > 0 && cellSize > 0)
                    ? Math.Max(1, (int)Math.Round(minClearanceM / cellSize, MidpointRounding.AwayFromZero))
                    : 1;
                // spiralLenM is the REAL per-side spiral the tool will build with. Passing 0 here
                // (the old bug) understated every curve's tangent demand by Ls/2 per side — 50 m
                // per shared tangent at the 50 m default — so the pass kept PIs whose curves could
                // not actually fit, and CurveAttacher then shortened spirals and halved radii to
                // force them in. minTangentM additionally requires true straight between curves.
                simplified = AlignmentSimplifier.Straighten(
                    simplified, designRadiusM, spiralLenM,
                    chordViable: (p0, p1) => ChordViable(
                        p0, p1, walkable, dtb, env, cellSize, nx, ny,
                        straightenFloorCells, straightenFloorCells),
                    maxTangentM: maxTangentM,
                    minTangentM: minTangentUsed);
            }

            return RouteResult.Ok(simplified, total, walkableCount, nx, ny, cellSize, startSnap, endSnap, dtb, rawDebug);
        }

        /// <summary>
        /// Routes a centerline that FOLLOWS an engineer-drawn priority path (the blue "box"
        /// polyline) as first priority over the surface. Each drawn segment is honored verbatim
        /// where the surface hosts it; only the spans the surface won't allow are A*-rerouted
        /// (and counted). When a span has no detour at all the route fails with
        /// <see cref="FailureCodes.PrioritySpanInfeasible"/>, naming the span — the "tell me when
        /// it's impossible" case. Shares the same mask, dtb field, A* core and straightening pass
        /// as <see cref="Route"/>, and returns the same <see cref="RouteResult"/> shape, so the
        /// caller's curve-fitting + containment readback are unchanged.
        /// </summary>
        public static RouteResult RoutePriority(
            bool[,] walkable,
            Envelope env,
            double cellSize,
            IReadOnlyList<Pt2> waypoints,
            CancellationToken ct,
            bool anyAngle = true,
            double boundaryBias = 4.0,
            double maxTangentM = 0.0,
            double minClearanceM = 0.0,
            double designRadiusM = 0.0,
            double spiralLenM = 0.0,
            double minTangentM = 0.0)
        {
            if (walkable == null) throw new ArgumentNullException(nameof(walkable));
            if (env == null) throw new ArgumentNullException(nameof(env));
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (waypoints == null || waypoints.Count < 2)
                throw new ArgumentException("priority path needs at least 2 waypoints", nameof(waypoints));

            int nx = walkable.GetLength(0);
            int ny = walkable.GetLength(1);
            int total = nx * ny;

            int walkableCount = 0;
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    if (walkable[x, y]) walkableCount++;
            if (walkableCount == 0)
                return RouteResult.Fail("walkable mask is empty", total, 0, nx, ny, cellSize,
                                        FailureCodes.EmptyMask);

            int[,] dtb = MultiSourceBfsDistance(walkable, nx, ny, ct);
            if (ct.IsCancellationRequested)
                return RouteResult.Fail("cancelled during distance transform", total, walkableCount,
                                        nx, ny, cellSize, FailureCodes.Cancelled);

            int dtbThresholdCells = minClearanceM > 0 && cellSize > 0
                ? Math.Max(1, (int)Math.Round(minClearanceM / cellSize, MidpointRounding.AwayFromZero))
                : 0;
            double snapThresholdM = Math.Max(20.0, 2.0 * cellSize);

            // Snap every drawn vertex onto a walkable cell. A vertex that can't be snapped means
            // the engineer drew off the surface there → fail naming the offending vertex.
            int n = waypoints.Count;
            var snapped = new Pt2[n];
            EndpointSnap? startSnap = null, endSnap = null;
            for (int i = 0; i < n; i++)
            {
                var r = ResolveEndpoint(waypoints[i], walkable, env, cellSize, nx, ny,
                                        snapThresholdM, dtb, dtbThresholdCells, minClearanceM);
                if (r.snap == null)
                {
                    string code = r.clearance
                        ? FailureCodes.ClearanceInsufficient : FailureCodes.PrioritySpanInfeasible;
                    return RouteResult.Fail(
                        $"drawn vertex #{i + 1} is not on the surface ({r.error})",
                        total, walkableCount, nx, ny, cellSize, code);
                }
                snapped[i] = r.snap.Point;
                if (i == 0) startSnap = r.snap;
                if (i == n - 1) endSnap = r.snap;
            }

            int floor = dtbThresholdCells; // ChordViable floor (0 → on-mask line-of-sight only)
            var plan = PriorityPathPlanner.Build(
                snapped,
                chordViable: (p0, p1) => ChordViable(
                    p0, p1, walkable, dtb, env, cellSize, nx, ny, floor, floor),
                subRoute: (p0, p1) =>
                {
                    var (cx0, cy0) = WorldToCell(p0, env, cellSize, nx, ny);
                    var (cx1, cy1) = WorldToCell(p1, env, cellSize, nx, ny);
                    var cells = AStar((cx0, cy0), (cx1, cy1), walkable, dtb, nx, ny, cellSize, ct,
                                      anyAngle, boundaryBias, null, dtbThresholdCells);
                    if (cells == null || cells.Count < 2) return null;
                    var pts = cells.Select(c => CellCenter(c.x, c.y, env, cellSize)).ToList();
                    pts[0] = p0;          // pin exact snapped endpoints (never the cell centre)
                    pts[^1] = p1;
                    return pts;
                });

            if (plan.Infeasible)
                return RouteResult.Fail(
                    $"priority span #{plan.InfeasibleSpanIndex + 1} cannot be routed on the surface",
                    total, walkableCount, nx, ny, cellSize, FailureCodes.PrioritySpanInfeasible);

            var path = plan.Path.ToArray();
            if (path.Length < 2)
                return RouteResult.Fail("priority path collapsed to fewer than 2 PIs",
                                        total, walkableCount, nx, ny, cellSize, FailureCodes.NoPath);

            // ── Engineer-style geometry fit (same contract as Route) ────────
            // A dense priority path — a road-evidence centerline or a detailed engineer-drawn
            // polyline — arrives as hundreds of short verbatim segments; honoring each one puts a
            // PI at every vertex and CurveAttacher fillets them all (the "mess of small turns").
            // Fit long tangents + ONE curve per real bend, proven drawable on the same mask;
            // a null fit keeps the legacy per-vertex chain, so worst case is unchanged.
            if (path.Length >= 3 && designRadiusM > 0)
            {
                double fitMinTangent = minTangentM > 0
                    ? minTangentM
                    : Math.Max(2.0 * spiralLenM, 0.2 * designRadiusM);

                // Clearance-floor ladder: try the full min-clearance erosion first (clean when
                // the corridor is wide), then bare on-mask (1 cell). A priority path FOLLOWING
                // an existing road may legitimately run closer to the survey-band edge than the
                // routing clearance — the containment gate downstream reports that as an
                // engineer-approval warning now, so the fit must not refuse geometry the gate
                // would accept. Without this, one tight neck rejected every ladder rung and the
                // whole road fell back to a PI per waypoint (road 73, 2026-07-28).
                int fullFloor = dtbThresholdCells > 0 ? dtbThresholdCells : 1;
                foreach (int fitFloor in fullFloor > 1 ? new[] { fullFloor, 1 } : new[] { 1 })
                {
                    var fit = TangentArcFitter.Fit(
                        path, designRadiusM, spiralLenM, fitMinTangent,
                        chordViable: (p0, p1) => ChordViable(
                            p0, p1, walkable, dtb, env, cellSize, nx, ny, fitFloor, fitFloor),
                        pointLegal: p => PointLegal(p, walkable, dtb, env, cellSize, nx, ny, fitFloor));
                    if (fit != null && fit.Pis.Length >= 2)
                    {
                        string fitMsg =
                            $"[tangent-fit] priority: pis={fit.Pis.Length} tangents={fit.TangentCount} " +
                            $"straightness={fit.StraightnessFactor:F1} merged={fit.MergedPis} " +
                            $"tightTangent={fit.HasTightTangent} floor_cells={fitFloor} (waypoints={n})";
                        System.Diagnostics.Debug.WriteLine("[MahodAI]" + fitMsg);
                        try { Utilities.MahodLogger.Info(fitMsg); } catch { }
                        return new RouteResult
                        {
                            Success = true,
                            Path = fit.Pis,
                            CellsTotal = total,
                            CellsWalkable = walkableCount,
                            GridNx = nx,
                            GridNy = ny,
                            CellSize = cellSize,
                            StartSnap = startSnap,
                            EndSnap = endSnap,
                            DistanceToBoundary = dtb,
                            ReroutedSpanCount = plan.ReroutedSpanIndices.Count,
                            PathSource = "tangent_arc_fit",
                            TangentCount = fit.TangentCount,
                        };
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI][tangent-fit] priority: no legal fit at floor {fitFloor} cells");
                    try { Utilities.MahodLogger.Info($"[tangent-fit] priority: no legal fit at floor {fitFloor} cells"); } catch { }
                }
            }

            // Straighten the rerouted (A*) spans; drawn-straight spans already are straight. Same
            // mask-legal + radius-feasible guarantees as the normal route, honoring maxTangentM.
            if (path.Length >= 3)
            {
                int sFloor = dtbThresholdCells > 0 ? dtbThresholdCells : 1;
                path = AlignmentSimplifier.Straighten(
                    path, designRadiusM, spiralLenM,
                    chordViable: (p0, p1) => ChordViable(
                        p0, p1, walkable, dtb, env, cellSize, nx, ny, sFloor, sFloor),
                    maxTangentM: maxTangentM,
                    minTangentM: minTangentM > 0
                        ? minTangentM
                        : Math.Max(2.0 * spiralLenM, 0.2 * designRadiusM));
            }

            return new RouteResult
            {
                Success = true,
                Path = path,
                CellsTotal = total,
                CellsWalkable = walkableCount,
                GridNx = nx,
                GridNy = ny,
                CellSize = cellSize,
                StartSnap = startSnap,
                EndSnap = endSnap,
                DistanceToBoundary = dtb,
                ReroutedSpanCount = plan.ReroutedSpanIndices.Count,
            };
        }

        // ── Elastic shortcut smoother (centered-mode post-process) ──────────
        // Deterministic straightening of the dense centered trace. Endpoints are frozen.
        //   Pass A (decimate): greedily replace a bowed sub-run with the longest straight chord
        //     that stays on-mask AND holds the clearance floor — the diag's "BOW-UNNEEDED→straight"
        //     rule. A genuine bend/neck (chord off-mask or a cell below floor) is left as PIs.
        //   Pass B (relax): nudge each surviving interior vertex toward its neighbours' midpoint,
        //     accepting only if both half-chords stay viable — removes residual small kinks.
        // Floor = max(hardFloor, min(comfortFloor, maxDtbAlongChord)): comfortFloor in the wide
        // band (no hug), self-lowering to the neck's own width in a pinch (never severed), with the
        // existing min-clearance erosion as the absolute backstop.
        private static List<Pt2> ElasticSmooth(
            List<Pt2> pts, bool[,] walkable, int[,] dtb, Envelope env, double cellSize,
            int nx, int ny, int comfortFloorCells, int hardFloorCells,
            int maxShortcutCells, int maxIters, double lambda)
        {
            if (pts.Count < 3) return pts;
            for (int iter = 0; iter < maxIters; iter++)
            {
                bool ca = DecimatePass(pts, walkable, dtb, env, cellSize, nx, ny,
                                       comfortFloorCells, hardFloorCells, maxShortcutCells);
                bool cb = RelaxPass(pts, walkable, dtb, env, cellSize, nx, ny,
                                    comfortFloorCells, hardFloorCells, lambda);
                if (!ca && !cb) break;   // a full A+B sweep changed nothing → converged
            }
            return pts;
        }

        private static bool DecimatePass(List<Pt2> pts, bool[,] walkable, int[,] dtb,
            Envelope env, double cellSize, int nx, int ny,
            int comfortFloorCells, int hardFloorCells, int maxShortcutCells)
        {
            bool changed = false;
            int i = 0;
            while (i < pts.Count - 1)
            {
                int best = i + 1;
                int k = i + 1;
                while (k < pts.Count - 1 && (k - i) <= maxShortcutCells)
                {
                    k++;
                    if (ChordViable(pts[i], pts[k], walkable, dtb, env, cellSize, nx, ny,
                                    comfortFloorCells, hardFloorCells))
                        best = k;
                    else
                        break;
                }
                if (best > i + 1)
                {
                    pts.RemoveRange(i + 1, best - (i + 1));   // chord i→best replaces the skipped run
                    changed = true;
                }
                i = best;
            }
            return changed;
        }

        private static bool RelaxPass(List<Pt2> pts, bool[,] walkable, int[,] dtb,
            Envelope env, double cellSize, int nx, int ny,
            int comfortFloorCells, int hardFloorCells, double lambda)
        {
            bool changed = false;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var mid = new Pt2((pts[i - 1].X + pts[i + 1].X) * 0.5,
                                  (pts[i - 1].Y + pts[i + 1].Y) * 0.5);
                var cand = new Pt2(pts[i].X + lambda * (mid.X - pts[i].X),
                                   pts[i].Y + lambda * (mid.Y - pts[i].Y));
                if (cand.X < env.MinX || cand.X >= env.MaxX ||
                    cand.Y < env.MinY || cand.Y >= env.MaxY) continue;
                if (ChordViable(pts[i - 1], cand, walkable, dtb, env, cellSize, nx, ny,
                                comfortFloorCells, hardFloorCells) &&
                    ChordViable(cand, pts[i + 1], walkable, dtb, env, cellSize, nx, ny,
                                comfortFloorCells, hardFloorCells))
                {
                    if (Math.Abs(cand.X - pts[i].X) + Math.Abs(cand.Y - pts[i].Y) > 1e-6)
                    {
                        pts[i] = cand;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        // Chord viable iff on-mask (LineOfSight) AND every chord cell's dtb >= floor, where
        // floor = max(hardFloor, min(comfortFloor, maxDtbAlongChord)). dtb is the SAME integer
        // field the containment readback uses, so an accepted chord also passes that readback.
        private static bool ChordViable(Pt2 p0, Pt2 p1, bool[,] walkable, int[,] dtb,
            Envelope env, double cellSize, int nx, int ny,
            int comfortFloorCells, int hardFloorCells)
        {
            var (ax, ay) = WorldToCell(p0, env, cellSize, nx, ny);
            var (bx, by) = WorldToCell(p1, env, cellSize, nx, ny);
            if (!LineOfSight(ax, ay, bx, by, walkable, nx, ny)) return false;

            int maxDtb = 0;
            ForEachChordCell(ax, ay, bx, by, dtb, nx, ny, v => { if (v > maxDtb) maxDtb = v; });
            int floor = Math.Max(hardFloorCells, Math.Min(comfortFloorCells, maxDtb));

            bool ok = true;
            ForEachChordCell(ax, ay, bx, by, dtb, nx, ny, v => { if (v < floor) ok = false; });
            return ok;
        }

        // Single-point legality on the SAME fields the chord test and the containment readback
        // use: inside the grid, on the walkable mask, and at least `floorCells` from the boundary.
        // Used by TangentArcFitter to sample curve bulges (a chord test cannot see them).
        private static bool PointLegal(Pt2 p, bool[,] walkable, int[,] dtb, Envelope env,
                                       double cellSize, int nx, int ny, int floorCells)
        {
            int cx = (int)Math.Floor((p.X - env.MinX) / cellSize);
            int cy = (int)Math.Floor((p.Y - env.MinY) / cellSize);
            if (cx < 0 || cy < 0 || cx >= nx || cy >= ny) return false;
            if (!walkable[cx, cy]) return false;
            if (floorCells > 0 && dtb[cx, cy] < floorCells) return false;
            return true;
        }

        // Bresenham raster of a chord; invokes the callback with each in-bounds cell's dtb.
        private static void ForEachChordCell(int x0, int y0, int x1, int y1,
            int[,] dtb, int nx, int ny, Action<int> visit)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy, x = x0, y = y0;
            while (true)
            {
                if (x >= 0 && y >= 0 && x < nx && y < ny) visit(dtb[x, y]);
                if (x == x1 && y == y1) return;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx)  { err += dx; y += sy; }
            }
        }

        // ── Ribbon centering (centered-mode post-process) ───────────────────
        // Balances the road between the two surface edges WITHOUT the cell-scale spaghetti the raw
        // staircase produced. Three ingredients make it stable:
        //   1. RESAMPLE the dense octile staircase to uniform spacing first — the staircase's
        //      7.7 m zig-zag (whose flipping perpendicular caused adjacent vertices to shift in
        //      opposite directions = loops) is gone, so headings are clean.
        //   2. Each iteration: shift every interior vertex toward the perpendicular MIDPOINT of the
        //      band (centering force), tapered to 0 over the first/last few vertices so the frozen
        //      endpoints don't kink, capped per iteration, validated on-mask and >= min clearance.
        //   3. TAUBIN smoothing (a +k then a -k Laplacian) — removes residual high-frequency wobble
        //      WITHOUT the shrinkage a plain Laplacian causes (plain smoothing pulls curves toward
        //      their chord = de-centers; Taubin preserves the centered low-frequency meander).
        // A perpendicular march is capped so a ray escaping along the valley cannot yank the path.
        private static List<Pt2> RibbonCenter(
            List<Pt2> pts, bool[,] walkable, int[,] dtb, Envelope env, double cellSize,
            int nx, int ny, int minClearanceCells, double dsM, int iters, double balanceLambda,
            int maxReachCells, double designRadiusM, double deadbandFrac)
        {
            if (pts.Count < 3) return pts;
            var path = Resample(pts, dsM);          // clean uniform polyline (kills the staircase)
            if (path.Count < 3) return pts;

            double step = cellSize;
            double maxReach = maxReachCells * cellSize;
            double maxStep = dsM;                   // per-iteration lateral cap
            const int endFreeze = 2;                // taper the centering force to 0 near the ends
            if (deadbandFrac < 0) deadbandFrac = 0;
            int n = path.Count;

            bool Legal(Pt2 c)
            {
                int cx = (int)Math.Floor((c.X - env.MinX) / cellSize);
                int cy = (int)Math.Floor((c.Y - env.MinY) / cellSize);
                if (cx < 0 || cy < 0 || cx >= nx || cy >= ny) return false;
                if (!walkable[cx, cy]) return false;
                if (minClearanceCells > 0 && dtb[cx, cy] < minClearanceCells) return false;
                return true;
            }

            // Relaxed gate for EMERGENCY de-spiking only: stay strictly inside the surface (never on
            // a boundary cell) but allow eroding the comfort clearance. A near-reversal spike pressed
            // against a wall cannot be flattened under the full min-clearance gate — the flatten move
            // is vetoed, so the spike (and its radius→0 alignment cusp) survives. The curvature cap
            // tries the full-clearance move first and only falls back to this gate when that is
            // impossible: a road skimming one cell closer at a single point beats an un-buildable cusp.
            int smoothFloorCells = Math.Max(1, minClearanceCells / 3);
            bool LegalSmooth(Pt2 c)
            {
                int cx = (int)Math.Floor((c.X - env.MinX) / cellSize);
                int cy = (int)Math.Floor((c.Y - env.MinY) / cellSize);
                if (cx < 0 || cy < 0 || cx >= nx || cy >= ny) return false;
                if (!walkable[cx, cy]) return false;
                if (dtb[cx, cy] < smoothFloorCells) return false;
                return true;
            }

            for (int it = 0; it < iters; it++)
            {
                // Centering force: shift each interior vertex toward the perpendicular midpoint,
                // but only by the imbalance BEYOND a width-scaled dead-band. Within deadbandFrac of
                // the local width the road is "central enough" → no move → it stays straight instead
                // of chasing every wiggle of the band centre (the dead-band is a fraction of width,
                // so a 10 km-wide reach tolerates a far larger absolute offset than a 500 m one).
                var next = new Pt2[n];
                next[0] = path[0]; next[n - 1] = path[n - 1];
                for (int i = 1; i < n - 1; i++)
                {
                    double hx = path[i + 1].X - path[i - 1].X, hy = path[i + 1].Y - path[i - 1].Y;
                    double hlen = Math.Sqrt(hx * hx + hy * hy);
                    next[i] = path[i];
                    if (hlen < 1e-9) continue;
                    double pxu = -hy / hlen, pyu = hx / hlen;
                    double dR = MarchToEdge(path[i], pxu, pyu, step, maxReach, walkable, env, cellSize, nx, ny);
                    double dL = MarchToEdge(path[i], -pxu, -pyu, step, maxReach, walkable, env, cellSize, nx, ny);
                    double imbalance = dR - dL;
                    double tol = deadbandFrac * (dR + dL);          // width-scaled dead-band
                    double excess = imbalance > tol ? imbalance - tol
                                  : (imbalance < -tol ? imbalance + tol : 0.0);
                    double taper = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)(endFreeze + 1));
                    double off = balanceLambda * taper * excess * 0.5;   // + → toward the wider side
                    if (off > maxStep) off = maxStep; else if (off < -maxStep) off = -maxStep;
                    if (Math.Abs(off) < 1e-9) continue;
                    var cand = new Pt2(path[i].X + off * pxu, path[i].Y + off * pyu);
                    if (Legal(cand)) next[i] = cand;
                }
                path = new List<Pt2>(next);

                // Taubin smoothing: shrink-free de-noising (removes wobble, keeps the meander).
                TaubinPass(path, 0.50, Legal);
                TaubinPass(path, -0.53, Legal);

                // Interleave the curvature cap EVERY iteration so centering and smoothness reach a
                // JOINT equilibrium. Capping only once at the end let the balance force rebuild the
                // band-centre meander 40× over (each iteration re-pulls toward the — possibly wavy —
                // medial axis); the end-cap then could not fully unwind it, which is what left the
                // residual scallops. Capping inside the loop means the road is never allowed to weave
                // tighter than the design radius in the first place, so small width undulations are
                // ignored by construction — the road literally cannot turn sharply enough to track them.
                CurvatureLimit(path, designRadiusM, dsM, Legal, LegalSmooth, passes: 8);
            }

            // Final settle: flatten any turn still sharper than the design radius so the road is "as
            // slight as the rules allow". A turn no tighter than R always hosts a curve, so this is what
            // eliminates the radius→0 alignment cusps. It trades a little centering AT a corner for
            // smoothness (exactly where a road should), and the relaxed gate lets a spike pinned against
            // a wall flatten even when the comfort-clearance gate would veto the move.
            CurvatureLimit(path, designRadiusM, dsM, Legal, LegalSmooth, passes: 200);
            return path;
        }

        // Flatten turns sharper than the design radius. At uniform spacing ds, a turn of angle θ at a
        // vertex implies radius ≈ ds/θ, so the max allowed per-vertex turn is ds/R. Where the turn
        // exceeds it, nudge the vertex toward its neighbours' chord (reducing the angle) in proportion
        // to the overshoot; iterating spreads a sharp corner into a gentle arc of radius ~R. Legal-
        // gated, endpoints frozen.
        private static void CurvatureLimit(List<Pt2> path, double radiusM, double dsM,
            Func<Pt2, bool> legalStrict, Func<Pt2, bool> legalRelaxed, int passes)
        {
            if (radiusM <= 0 || dsM <= 0 || path.Count < 3) return;
            double maxTurn = Math.Min(Math.PI * 0.5, dsM / radiusM);
            for (int pass = 0; pass < passes; pass++)
            {
                bool any = false;
                for (int i = 1; i < path.Count - 1; i++)
                {
                    double v1x = path[i].X - path[i - 1].X, v1y = path[i].Y - path[i - 1].Y;
                    double v2x = path[i + 1].X - path[i].X, v2y = path[i + 1].Y - path[i].Y;
                    double l1 = Math.Sqrt(v1x * v1x + v1y * v1y), l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                    if (l1 < 1e-9 || l2 < 1e-9) continue;
                    double dot = Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1.0, 1.0);
                    double turn = Math.Acos(dot);
                    if (turn <= maxTurn) continue;
                    double frac = Math.Min(0.6, (turn - maxTurn) / turn);   // flatten proportional to overshoot
                    double mx = (path[i - 1].X + path[i + 1].X) * 0.5;
                    double my = (path[i - 1].Y + path[i + 1].Y) * 0.5;
                    var cand = new Pt2(path[i].X + frac * (mx - path[i].X), path[i].Y + frac * (my - path[i].Y));
                    // Prefer the move that keeps full comfort clearance; only if that is vetoed (a
                    // spike pinned against a wall) fall back to the relaxed gate so the cusp can't
                    // survive. Endpoints frozen.
                    if (legalStrict(cand)) { path[i] = cand; any = true; }
                    else if (legalRelaxed(cand)) { path[i] = cand; any = true; }
                }
                if (!any) break;
            }
        }

        // One Laplacian pass with factor k (Taubin uses a +k shrink pass then a -k anti-shrink pass).
        private static void TaubinPass(List<Pt2> path, double k, Func<Pt2, bool> legal)
        {
            int n = path.Count;
            if (n < 3) return;
            var outp = new Pt2[n];
            outp[0] = path[0]; outp[n - 1] = path[n - 1];
            for (int i = 1; i < n - 1; i++)
            {
                double mx = (path[i - 1].X + path[i + 1].X) * 0.5;
                double my = (path[i - 1].Y + path[i + 1].Y) * 0.5;
                var cand = new Pt2(path[i].X + k * (mx - path[i].X), path[i].Y + k * (my - path[i].Y));
                outp[i] = legal(cand) ? cand : path[i];
            }
            for (int i = 1; i < n - 1; i++) path[i] = outp[i];
        }

        // Uniform arc-length resampling at spacing ds (metres). Endpoints preserved.
        private static List<Pt2> Resample(List<Pt2> pts, double ds)
        {
            var outp = new List<Pt2>();
            if (pts.Count == 0 || ds <= 0) return new List<Pt2>(pts);
            outp.Add(pts[0]);
            double carried = 0.0;   // distance already accumulated toward the next sample
            for (int i = 1; i < pts.Count; i++)
            {
                Pt2 a = pts[i - 1], b = pts[i];
                double seg = a.DistanceTo(b);
                if (seg < 1e-9) continue;
                double dirx = (b.X - a.X) / seg, diry = (b.Y - a.Y) / seg;
                double along = ds - carried;       // distance from a to the first new sample
                while (along <= seg + 1e-9)
                {
                    outp.Add(new Pt2(a.X + dirx * along, a.Y + diry * along));
                    along += ds;
                }
                carried = seg - (along - ds);
            }
            if (outp[^1].DistanceTo(pts[^1]) > ds * 0.25) outp.Add(pts[^1]);
            else outp[^1] = pts[^1];
            return outp;
        }

        // March from a world point along a unit direction in cellSize steps until the mask edge
        // (off-grid or non-walkable) or the reach cap; returns the distance travelled in metres.
        private static double MarchToEdge(Pt2 p, double dirx, double diry, double step, double maxReach,
            bool[,] walkable, Envelope env, double cellSize, int nx, int ny)
        {
            double d = 0;
            while (d < maxReach)
            {
                d += step;
                double wx = p.X + dirx * d, wy = p.Y + diry * d;
                int cx = (int)Math.Floor((wx - env.MinX) / cellSize);
                int cy = (int)Math.Floor((wy - env.MinY) / cellSize);
                if (cx < 0 || cy < 0 || cx >= nx || cy >= ny || !walkable[cx, cy]) return d - step;
            }
            return maxReach;
        }

        // ── Endpoint resolution (strict snap decision) ──────────────────────
        // Returns the EndpointSnap describing the resolved endpoint plus the grid cell to
        // route from, or (null, error) when the click cannot be accepted:
        //   • outside the grid AABB, or
        //   • no walkable cell within snapThresholdM of the click.
        internal static (EndpointSnap? snap, int cx, int cy, string? error, bool clearance) ResolveEndpoint(
            Pt2 p, bool[,] walkable, Envelope env, double cellSize, int nx, int ny, double snapThresholdM,
            int[,] dtb, int minClearanceCells, double minClearanceM)
        {
            // AABB guard BEFORE WorldToCell — WorldToCell clamps out-of-range clicks to a
            // boundary cell, which used to convert distant clicks into "valid" endpoints.
            if (p.X < env.MinX || p.X > env.MaxX || p.Y < env.MinY || p.Y > env.MaxY)
                return (null, -1, -1, "outside the surface bounding box", false);

            var (cx, cy) = WorldToCell(p, env, cellSize, nx, ny);
            if (IsInBounds(cx, cy, nx, ny) && walkable[cx, cy] &&
                (minClearanceCells == 0 || dtb[cx, cy] >= minClearanceCells))
                return (new EndpointSnap { Snapped = false, DistanceM = 0.0, Point = p }, cx, cy, null, false);

            int maxR = (int)Math.Ceiling(snapThresholdM / cellSize) + 1;

            // When a clearance floor is in force, the snapped endpoint must ALSO have adequate
            // distance-to-boundary — otherwise the centerline starts/ends hugging the edge.
            if (minClearanceCells > 0)
            {
                if (!TrySnapToWalkableWithMinDtb(cx, cy, walkable, dtb, nx, ny, maxR, minClearanceCells,
                                                 out int sxc, out int syc))
                    return (null, -1, -1,
                            $"no cell with {minClearanceM:F1} m clearance within {snapThresholdM:F0} m", true);
                Pt2 snappedC = CellCenter(sxc, syc, env, cellSize);
                double distC = p.DistanceTo(snappedC);
                if (distC > snapThresholdM)
                    return (null, -1, -1,
                            $"nearest cell with {minClearanceM:F1} m clearance is {distC:F1} m away (threshold {snapThresholdM:F0} m)",
                            true);
                return (new EndpointSnap { Snapped = true, DistanceM = distC, Point = snappedC }, sxc, syc, null, false);
            }

            if (!TrySnapToWalkable(cx, cy, walkable, nx, ny, maxR, out int sx, out int sy))
                return (null, -1, -1, $"no walkable cell within {snapThresholdM:F0} m", false);

            Pt2 snapped = CellCenter(sx, sy, env, cellSize);
            double dist = p.DistanceTo(snapped);
            if (dist > snapThresholdM)
                return (null, -1, -1, $"nearest walkable cell is {dist:F1} m away (threshold {snapThresholdM:F0} m)", false);

            return (new EndpointSnap { Snapped = true, DistanceM = dist, Point = snapped }, sx, sy, null, false);
        }

        // ── Densify tangents along the raw A* path ─────────────────────────
        // For every anchor pair, two triggers can split the tangent into pieces
        // sampled from the raw A* cells between the anchors:
        //   • length:     centre-pulled chord exceeds maxSegM (off when 0).
        //   • curvature:  raw cells deviate from the chord by more than
        //                 devThresholdM. This is always on. It restores PIs
        //                 that DP collapsed back to a straight chord because
        //                 DP is cost-blind: when A→B is LoS-clear in the
        //                 walkable mask, DP drops the curve even when the raw
        //                 path was riding a discount band (e.g., existing road).
        // Falls back to chord interpolation when a raw sample fails the LoS
        // check (rare; happens at sharp bends where raw diverges from chord).
        // Each inserted PI gets centre-pulled by the next pass.
        private static Pt2[] DensifyAlongRawPath(
            Pt2[] anchors,
            int[] anchorRawIdx,
            List<Pt2> rawPts,
            bool[,] walkable,
            Envelope env,
            double cellSize,
            int nx,
            int ny,
            double maxSegM,
            double devThresholdM)
        {
            if (anchors.Length < 2) return anchors;
            var result = new List<Pt2>(anchors.Length * 2);
            result.Add(anchors[0]);
            for (int i = 1; i < anchors.Length; i++)
            {
                Pt2 prev = anchors[i - 1];
                Pt2 next = anchors[i];
                int a = anchorRawIdx[i - 1];
                int b = anchorRawIdx[i];
                double segLen = prev.DistanceTo(next);

                bool exceedsLen = (maxSegM > 0 && segLen > maxSegM);
                double maxDev = (b > a + 1) ? MaxRawDeviation(prev, next, rawPts, a, b) : 0.0;
                bool exceedsDev = (devThresholdM > 0 && maxDev > devThresholdM);

                if ((exceedsLen || exceedsDev) && b > a + 1)
                {
                    int splitsByLen = exceedsLen ? (int)Math.Ceiling(segLen / maxSegM) : 1;
                    // Per-segment deviation falls roughly linearly with split count
                    // for moderately-curved segments — overshoot a touch so post-
                    // densify deviation lands comfortably below the threshold.
                    int splitsByDev = exceedsDev
                        ? Math.Max(2, (int)Math.Ceiling(maxDev / Math.Max(devThresholdM * 0.5, cellSize)))
                        : 1;
                    int splits = Math.Max(splitsByLen, splitsByDev);
                    splits = Math.Min(splits, b - a);   // can't insert more than raw cells available
                    Pt2 lastPlaced = prev;
                    for (int k = 1; k < splits; k++)
                    {
                        int rawIdx = a + (int)Math.Round((double)k * (b - a) / splits);
                        rawIdx = Math.Clamp(rawIdx, a + 1, b - 1);
                        Pt2 cand = rawPts[rawIdx];

                        var (lpx, lpy) = WorldToCell(lastPlaced, env, cellSize, nx, ny);
                        var (cx, cy) = WorldToCell(cand, env, cellSize, nx, ny);
                        var (npx, npy) = WorldToCell(next, env, cellSize, nx, ny);

                        if (LineOfSight(lpx, lpy, cx, cy, walkable, nx, ny) &&
                            LineOfSight(cx, cy, npx, npy, walkable, nx, ny))
                        {
                            result.Add(cand);
                            lastPlaced = cand;
                        }
                        else
                        {
                            // Fall back to chord interpolation. The chord between
                            // centre-pulled anchors is LoS-clear by construction,
                            // so any point on it is LoS-clear from both endpoints.
                            double t = k / (double)splits;
                            var fallback = new Pt2(
                                prev.X + t * (next.X - prev.X),
                                prev.Y + t * (next.Y - prev.Y));
                            result.Add(fallback);
                            lastPlaced = fallback;
                        }
                    }
                }
                result.Add(next);
            }
            return result.ToArray();
        }

        // Max perpendicular distance from any raw cell strictly between anchors
        // to the chord (anchor[i-1] → anchor[i]). Used to detect tangents that
        // DP collapsed into a chord even though the raw A* path was curved
        // (e.g., riding a discount band that DP is blind to).
        private static double MaxRawDeviation(Pt2 prev, Pt2 next, List<Pt2> rawPts, int a, int b)
        {
            double maxDev = 0.0;
            for (int k = a + 1; k < b; k++)
            {
                double d = PerpDistanceToSegment(rawPts[k], prev, next);
                if (d > maxDev) maxDev = d;
            }
            return maxDev;
        }

        // ── Centre-pull post-pass ───────────────────────────────────────────
        // For each interior PI, search perpendicular to the chord between its
        // neighbours for a position with greater distance-to-boundary; accept
        // the shift only if both adjacent line segments stay inside the
        // walkable mask. Endpoints (the user's clicks) are never moved.
        private static Pt2[] CenterPullPis(Pt2[] pis, bool[,] walkable, int[,] dtb,
                                           Envelope env, double cellSize, int nx, int ny,
                                           int minClearanceCells = 0)
        {
            if (pis.Length < 3) return pis;
            const int maxLateralCells = 8;
            const int dtbImprovementCells = 1;

            var result = (Pt2[])pis.Clone();
            for (int i = 1; i < result.Length - 1; i++)
            {
                Pt2 prev = result[i - 1];
                Pt2 next = result[i + 1];
                double dx = next.X - prev.X;
                double dy = next.Y - prev.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                // Perpendicular to chord (rotated 90°)
                double nxDir = -dy / len;
                double nyDir = dx / len;

                var (ox, oy) = WorldToCell(result[i], env, cellSize, nx, ny);
                int origDtb = walkable[ox, oy] ? dtb[ox, oy] : 0;

                Pt2 best = result[i];
                int bestDtb = origDtb;
                var (px, py) = WorldToCell(prev, env, cellSize, nx, ny);
                var (qx, qy) = WorldToCell(next, env, cellSize, nx, ny);

                for (int k = -maxLateralCells; k <= maxLateralCells; k++)
                {
                    if (k == 0) continue;
                    double offset = k * cellSize;
                    var cand = new Pt2(result[i].X + nxDir * offset,
                                       result[i].Y + nyDir * offset);
                    // Reject candidates whose world coordinates fall outside the
                    // grid envelope before WorldToCell silently clamps the index
                    // and the walkable check ends up reading the wrong cell.
                    if (cand.X < env.MinX || cand.X >= env.MaxX ||
                        cand.Y < env.MinY || cand.Y >= env.MaxY) continue;
                    var (cx, cy) = WorldToCell(cand, env, cellSize, nx, ny);
                    if (!IsInBounds(cx, cy, nx, ny)) continue;
                    if (!walkable[cx, cy]) continue;
                    int candDtb = dtb[cx, cy];
                    // Never let a lateral centre-pull shift the PI into the eroded band.
                    if (minClearanceCells > 0 && candDtb < minClearanceCells) continue;
                    if (candDtb <= bestDtb) continue;
                    if (!LineOfSight(px, py, cx, cy, walkable, nx, ny)) continue;
                    if (!LineOfSight(cx, cy, qx, qy, walkable, nx, ny)) continue;
                    bestDtb = candDtb;
                    best = cand;
                }

                if (bestDtb >= origDtb + dtbImprovementCells)
                    result[i] = best;
            }
            return result;
        }

        // ── LoS-clear chord fixer ───────────────────────────────────────────
        // For each consecutive pair of kept PIs whose chord exits the walkable
        // mask, find the raw-path cell with maximum perpendicular deviation
        // from the chord and insert it as a new PI. Recurse until every chord
        // is line-of-sight clear in the walkable mask. Equivalent in shape to
        // an LoS-aware Douglas-Peucker, but with no perpendicular-distance
        // tolerance — the only criterion is "chord must stay inside walkable".
        private static (Pt2[] kept, int[] rawIdx) EnsureLosClearChords(
            Pt2[] anchors, int[] anchorRawIdx, List<Pt2> rawPts,
            bool[,] walkable, Envelope env, double cellSize, int nx, int ny)
        {
            if (anchors.Length < 2) return (anchors, anchorRawIdx);

            var keep = new SortedSet<int>();
            foreach (var idx in anchorRawIdx) keep.Add(idx);

            var todo = new Stack<(int s, int e)>();
            for (int i = 1; i < anchorRawIdx.Length; i++)
                todo.Push((anchorRawIdx[i - 1], anchorRawIdx[i]));

            int safety = 0;
            while (todo.Count > 0 && safety < 10_000)
            {
                safety++;
                var (s, e) = todo.Pop();
                if (e <= s + 1) continue;

                var (sx, sy) = WorldToCell(rawPts[s], env, cellSize, nx, ny);
                var (ex, ey) = WorldToCell(rawPts[e], env, cellSize, nx, ny);
                if (LineOfSight(sx, sy, ex, ey, walkable, nx, ny)) continue;

                int splitIdx = -1;
                double maxDist = 0;
                for (int i = s + 1; i < e; i++)
                {
                    double d = PerpDistanceToSegment(rawPts[i], rawPts[s], rawPts[e]);
                    if (d > maxDist) { maxDist = d; splitIdx = i; }
                }
                if (splitIdx < 0) continue;

                if (keep.Add(splitIdx))
                {
                    todo.Push((s, splitIdx));
                    todo.Push((splitIdx, e));
                }
            }

            var idxList = keep.ToArray();
            var pts = new Pt2[idxList.Length];
            for (int i = 0; i < idxList.Length; i++) pts[i] = rawPts[idxList[i]];
            return (pts, idxList);
        }

        // ── Inflate Theta*-collapsed path back to a per-cell trace ──────────
        // Walks consecutive cells in the path; whenever two cells aren't
        // 8-neighbour-adjacent (i.e. the relaxation skipped intermediates via
        // line-of-sight), Bresenham-fills the gap. Cells produced are exactly
        // the LoS line A* committed to. Returns the dense list AND a parallel
        // map from sparse-index → dense-index so callers can translate
        // anchor references.
        private static (List<(int x, int y)> dense, int[] sparseToDense)
            InflateCellsAlongLosWithMap(List<(int x, int y)> sparse)
        {
            if (sparse == null || sparse.Count == 0)
                return (new List<(int x, int y)>(), Array.Empty<int>());

            var dense = new List<(int x, int y)>(sparse.Count);
            var map = new int[sparse.Count];
            dense.Add(sparse[0]);
            map[0] = 0;
            for (int i = 1; i < sparse.Count; i++)
            {
                var (px, py) = sparse[i - 1];
                var (cx, cy) = sparse[i];
                int adx = Math.Abs(cx - px);
                int ady = Math.Abs(cy - py);
                if (adx <= 1 && ady <= 1)
                {
                    dense.Add(sparse[i]);
                    map[i] = dense.Count - 1;
                    continue;
                }
                int sx = px < cx ? 1 : -1;
                int sy = py < cy ? 1 : -1;
                int err = adx - ady;
                int x = px, y = py;
                while (true)
                {
                    int e2 = 2 * err;
                    if (e2 > -ady) { err -= ady; x += sx; }
                    if (e2 <  adx) { err += adx; y += sy; }
                    dense.Add((x, y));
                    if (x == cx && y == cy) break;
                }
                map[i] = dense.Count - 1;
            }
            return (dense, map);
        }

        // ── Bresenham line-of-sight on the walkable grid ────────────────────
        // Returns true iff every cell on the integer raster line from (x0,y0)
        // to (x1,y1) is walkable. Reused by Phase 2's Theta* expansion.
        private static bool LineOfSight(int x0, int y0, int x1, int y1,
                                        bool[,] walkable, int nx, int ny)
        {
            if (!IsInBounds(x0, y0, nx, ny) || !IsInBounds(x1, y1, nx, ny)) return false;
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;
            while (true)
            {
                if (!walkable[x, y]) return false;
                if (x == x1 && y == y1) return true;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx)  { err += dx; y += sy; }
            }
        }

        // Worst-case cost multiplier along a bresenham raster line. Used by the
        // Theta* parent→neighbour edge so a long LoS span isn't priced as cheap
        // just because its endpoints sit on a discount strip — if it leaves the
        // strip mid-span, the worst cell governs.
        private static double MaxCostMulAlong(int x0, int y0, int x1, int y1,
                                              double[,] costMul, int nx, int ny)
        {
            double best = 1.0;
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;
            while (true)
            {
                if (x >= 0 && y >= 0 && x < nx && y < ny)
                {
                    double v = costMul[x, y];
                    if (v > best) best = v;
                }
                if (x == x1 && y == y1) return best;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx)  { err += dx; y += sy; }
            }
        }

        // ── A* ──────────────────────────────────────────────────────────────
        private static readonly (int dx, int dy)[] _neigh =
        {
            ( 1,  0), (-1,  0), ( 0,  1), ( 0, -1),
            ( 1,  1), ( 1, -1), (-1,  1), (-1, -1),
        };

        private static List<(int x, int y)>? AStar(
            (int x, int y) start,
            (int x, int y) goal,
            bool[,] walkable,
            int[,] dtb,
            int nx,
            int ny,
            double cellSize,
            CancellationToken ct,
            bool anyAngle,
            double boundaryBias,
            double[,]? costMul,
            int dtbThresholdCells = 0,
            bool centered = false,
            double centredness = 6.0,
            double[,]? centerField = null,
            double centerMax = 1.0,
            double[,]? localMax = null)
        {
            double alpha = cellSize * boundaryBias;
            double sqrt2 = Math.Sqrt(2.0);

            var gScore = new double[nx, ny];
            var cameFromX = new int[nx, ny];
            var cameFromY = new int[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                {
                    gScore[x, y] = double.PositiveInfinity;
                    cameFromX[x, y] = -1;
                    cameFromY[x, y] = -1;
                }
            gScore[start.x, start.y] = 0;

            var open = new PriorityQueue<(int x, int y), double>();
            open.Enqueue(start, Heuristic(start, goal, cellSize));
            var closed = new bool[nx, ny];

            int popped = 0;
            while (open.Count > 0)
            {
                var cur = open.Dequeue();
                popped++;
                if ((popped & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;

                if (cur.x == goal.x && cur.y == goal.y)
                    return Reconstruct(cameFromX, cameFromY, cur);

                if (closed[cur.x, cur.y]) continue;
                closed[cur.x, cur.y] = true;

                // Theta*: if cur has a parent and it sees the neighbour directly,
                // skip cur and re-parent the neighbour from cur's parent. Produces
                // piecewise-straight any-angle paths instead of an 8-direction
                // staircase. Falls back to standard expansion when blocked or disabled.
                int parentX = cameFromX[cur.x, cur.y];
                int parentY = cameFromY[cur.x, cur.y];

                for (int i = 0; i < _neigh.Length; i++)
                {
                    int nxC = cur.x + _neigh[i].dx;
                    int nyC = cur.y + _neigh[i].dy;
                    if (!IsInBounds(nxC, nyC, nx, ny)) continue;
                    if (!walkable[nxC, nyC]) continue;
                    // Clearance erosion: exclude cells closer than the required clearance to the
                    // boundary so the search rides only cells where a full cross-section fits.
                    if (dtbThresholdCells > 0 && dtb[nxC, nyC] < dtbThresholdCells) continue;
                    if (closed[nxC, nyC]) continue;

                    double tentative;
                    int relaxParentX, relaxParentY;

                    if (anyAngle && parentX >= 0 &&
                        (dtbThresholdCells == 0 ||
                         (dtb[parentX, parentY] >= dtbThresholdCells && dtb[nxC, nyC] >= dtbThresholdCells)) &&
                        LineOfSight(parentX, parentY, nxC, nyC, walkable, nx, ny))
                    {
                        // Parent → neighbour direct edge. Bias sampled at the
                        // segment midpoint cell — averaging would be more accurate
                        // but this is plenty for the smoothing the bias provides.
                        int dxw = nxC - parentX;
                        int dyw = nyC - parentY;
                        double distWorld = cellSize * Math.Sqrt(dxw * dxw + dyw * dyw);
                        int mx = (parentX + nxC) / 2;
                        int my = (parentY + nyC) / 2;
                        // For an LoS edge spanning many cells, take the worst (max)
                        // costMul along the bresenham line — penalises crossing back
                        // into non-discount terrain mid-segment.
                        double mul = (costMul == null) ? 1.0
                            : MaxCostMulAlong(parentX, parentY, nxC, nyC, costMul, nx, ny);
                        // Theta* (any-angle) runs ONLY in shortest mode — centered mode forces
                        // per-cell octile (see Route), because Theta*'s LoS re-parenting
                        // straight-lines through open ground and ignores a smooth cost field.
                        double dtbMid = Math.Max(dtb[mx, my], 1) * cellSize;
                        double biasFactor = 1.0 + alpha / dtbMid;
                        tentative = gScore[parentX, parentY] + distWorld * mul * biasFactor;
                        relaxParentX = parentX;
                        relaxParentY = parentY;
                    }
                    else
                    {
                        bool diag = _neigh[i].dx != 0 && _neigh[i].dy != 0;
                        double step = (diag ? sqrt2 : 1.0) * cellSize;
                        double mul = costMul?[nxC, nyC] ?? 1.0;
                        double biasFactor;
                        if (centered && centerField != null)
                        {
                            // Centered mode: non-saturating cost on a SMOOTH (Chamfer) distance
                            // field. The smooth field has a unique ridge instead of the integer
                            // BFS field's flat plateaus, so the min-cost path rides a single
                            // clean spine instead of wobbling across tied cells — that wobble
                            // was the source of the spurious bends / inflated PI count.
                            // Normalise against the LOCAL band width when a localMax field is
                            // present (width-adaptive centering); else the single global centerMax.
                            double lm = localMax != null ? localMax[nxC, nyC] : centerMax;
                            double d = Math.Min(Math.Max(centerField[nxC, nyC], 0.0), lm);
                            biasFactor = 1.0 + centredness * (lm - d) / lm;
                        }
                        else
                        {
                            double dtbWorld = Math.Max(dtb[nxC, nyC], 1) * cellSize;
                            biasFactor = 1.0 + alpha / dtbWorld;
                        }
                        tentative = gScore[cur.x, cur.y] + step * mul * biasFactor;
                        relaxParentX = cur.x;
                        relaxParentY = cur.y;
                    }

                    if (tentative < gScore[nxC, nyC])
                    {
                        gScore[nxC, nyC] = tentative;
                        cameFromX[nxC, nyC] = relaxParentX;
                        cameFromY[nxC, nyC] = relaxParentY;
                        open.Enqueue((nxC, nyC), tentative + Heuristic((nxC, nyC), goal, cellSize));
                    }
                }
            }
            return null;
        }

        private static double Heuristic((int x, int y) a, (int x, int y) b, double cellSize)
        {
            int dx = Math.Abs(a.x - b.x);
            int dy = Math.Abs(a.y - b.y);
            return cellSize * (Math.Max(dx, dy) + (Math.Sqrt(2.0) - 1.0) * Math.Min(dx, dy));
        }

        private static List<(int x, int y)> Reconstruct(int[,] fromX, int[,] fromY, (int x, int y) goal)
        {
            var path = new List<(int x, int y)>();
            (int x, int y) cur = goal;
            int safety = 0;
            while (cur.x >= 0 && cur.y >= 0 && safety < 1_000_000)
            {
                path.Add(cur);
                int px = fromX[cur.x, cur.y];
                int py = fromY[cur.x, cur.y];
                if (px < 0) break;
                cur = (px, py);
                safety++;
            }
            path.Reverse();
            return path;
        }

        // ── Multi-source BFS for distance-to-boundary (in cells) ────────────
        private static int[,] MultiSourceBfsDistance(bool[,] walkable, int nx, int ny, CancellationToken ct)
        {
            var dist = new int[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    dist[x, y] = walkable[x, y] ? int.MaxValue : 0;

            var q = new Queue<(int x, int y)>();
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    if (!walkable[x, y]) q.Enqueue((x, y));

            (int dx, int dy)[] n4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            int processed = 0;
            while (q.Count > 0)
            {
                if ((++processed & 0xFFFF) == 0 && ct.IsCancellationRequested) return dist;
                var c = q.Dequeue();
                for (int i = 0; i < n4.Length; i++)
                {
                    int x2 = c.x + n4[i].dx;
                    int y2 = c.y + n4[i].dy;
                    if (!IsInBounds(x2, y2, nx, ny)) continue;
                    int cand = dist[c.x, c.y] + 1;
                    if (cand < dist[x2, y2])
                    {
                        dist[x2, y2] = cand;
                        q.Enqueue((x2, y2));
                    }
                }
            }
            return dist;
        }

        // ── Chamfer distance-to-boundary (in cell units, near-Euclidean) ────
        // A two-pass (3-4) chamfer distance transform: distance of each walkable cell to the
        // nearest non-walkable cell. Unlike the 4-connected BFS above (integer L1 with wide
        // flat plateaus through a band), the chamfer metric is continuous and near-isotropic,
        // so it has a SINGLE clean ridge — the centered router rides it smoothly instead of
        // wobbling across tied plateau cells. Used ONLY for the centered cost field; the
        // integer BFS dtb above is kept for clearance erosion and the containment readback so
        // those semantics are unchanged. Orthogonal step costs 3, diagonal 4; the result is
        // divided by 3 so the field reads in (approximate) cell units. ~3% of true Euclidean.
        private static double[,] ChamferDistanceFieldCells(bool[,] walkable, int nx, int ny)
        {
            const double ORTHO = 3.0, DIAG = 4.0, NORM = 3.0;
            double big = (nx + ny) * ORTHO + 1.0;
            var d = new double[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    d[x, y] = walkable[x, y] ? big : 0.0;

            // Forward pass: top-left → bottom-right (W, NW, N, NE already finalised).
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    if (!walkable[x, y]) continue;
                    double v = d[x, y];
                    if (x > 0)              v = Math.Min(v, d[x - 1, y] + ORTHO);
                    if (y > 0)              v = Math.Min(v, d[x, y - 1] + ORTHO);
                    if (x > 0 && y > 0)     v = Math.Min(v, d[x - 1, y - 1] + DIAG);
                    if (x < nx - 1 && y > 0) v = Math.Min(v, d[x + 1, y - 1] + DIAG);
                    d[x, y] = v;
                }

            // Backward pass: bottom-right → top-left (E, SE, S, SW).
            for (int y = ny - 1; y >= 0; y--)
                for (int x = nx - 1; x >= 0; x--)
                {
                    if (!walkable[x, y]) continue;
                    double v = d[x, y];
                    if (x < nx - 1)             v = Math.Min(v, d[x + 1, y] + ORTHO);
                    if (y < ny - 1)             v = Math.Min(v, d[x, y + 1] + ORTHO);
                    if (x < nx - 1 && y < ny - 1) v = Math.Min(v, d[x + 1, y + 1] + DIAG);
                    if (x > 0 && y < ny - 1)     v = Math.Min(v, d[x - 1, y + 1] + DIAG);
                    d[x, y] = v;
                }

            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    if (walkable[x, y]) d[x, y] /= NORM;
            return d;
        }

        // ── Local ridge-width ceiling (per-cell centering normaliser) ────────
        // Separable two-pass sliding-window MAX of the Chamfer field. Each cell's half-window
        // = clamp(k * cf[x,y], 1, maxWindowCells), so the reach scales with the LOCAL clearance:
        // a wall cell (small cf) sees only its immediate band, while a near-ridge cell sees across
        // its band to the ridge. The result is "the widest ridge within reach of my band", which
        // the centered cost uses as the local normaliser. Unlike a min-along-path grassfire this
        // box-MAX has NO collapsing term, so the centering gradient survives (a wall cell inherits
        // its band ridge's large value -> high cost -> pulled inward). Deterministic: fixed pass
        // order, integer windows, no float-tie branch. Non-walkable cells contribute 0 and are
        // skipped as window members. O(N * maxWindowCells) worst case (maxWindowCells bounded by
        // the global ridge); most cells have tiny windows so it is far cheaper in practice.
        private static double[,] LocalRidgeWidthCeiling(double[,] cf, bool[,] walkable,
            int nx, int ny, double k, int maxWindowCells)
        {
            var tmp = new double[nx, ny];   // pass 1: horizontal box-max
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    if (!walkable[x, y]) { tmp[x, y] = 0.0; continue; }
                    int w = Math.Clamp((int)(k * cf[x, y]), 1, maxWindowCells);
                    double m = cf[x, y];
                    for (int dx = -w; dx <= w; dx++)
                    {
                        int xx = x + dx;
                        if (xx >= 0 && xx < nx && walkable[xx, y] && cf[xx, y] > m) m = cf[xx, y];
                    }
                    tmp[x, y] = m;
                }

            var outp = new double[nx, ny]; // pass 2: vertical box-max over the horizontal result
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                {
                    if (!walkable[x, y]) { outp[x, y] = 0.0; continue; }
                    int w = Math.Clamp((int)(k * cf[x, y]), 1, maxWindowCells);
                    double m = tmp[x, y];
                    for (int dy = -w; dy <= w; dy++)
                    {
                        int yy = y + dy;
                        if (yy >= 0 && yy < ny && walkable[x, yy] && tmp[x, yy] > m) m = tmp[x, yy];
                    }
                    outp[x, y] = m;
                }
            return outp;
        }

        // ── Snap to nearest walkable (small spiral search) ──────────────────
        private static bool TrySnapToWalkable(int x0, int y0, bool[,] walkable, int nx, int ny,
                                              int maxRadiusCells, out int sx, out int sy)
        {
            int maxR = Math.Min(maxRadiusCells, Math.Max(nx, ny));
            for (int r = 1; r <= maxR; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = x0 + dx, y = y0 + dy;
                        if (!IsInBounds(x, y, nx, ny)) continue;
                        if (walkable[x, y])
                        {
                            sx = x; sy = y;
                            return true;
                        }
                    }
            }
            sx = sy = -1;
            return false;
        }

        // ── Snap to nearest cell that is walkable AND has adequate clearance ─
        // Same outward ring search as TrySnapToWalkable, but additionally requires
        // dtb[x,y] >= minDtb so a snapped endpoint never lands in the eroded band.
        private static bool TrySnapToWalkableWithMinDtb(
            int x0, int y0, bool[,] walkable, int[,] dtb, int nx, int ny,
            int maxRadiusCells, int minDtb, out int sx, out int sy)
        {
            int maxR = Math.Min(maxRadiusCells, Math.Max(nx, ny));
            for (int r = 1; r <= maxR; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = x0 + dx, y = y0 + dy;
                        if (!IsInBounds(x, y, nx, ny)) continue;
                        if (!walkable[x, y]) continue;
                        if (dtb[x, y] < minDtb) continue;
                        sx = x; sy = y;
                        return true;
                    }
            }
            sx = sy = -1;
            return false;
        }

        private static (int x, int y) WorldToCell(Pt2 p, Envelope env, double cell, int nx, int ny)
        {
            int x = (int)Math.Floor((p.X - env.MinX) / cell);
            int y = (int)Math.Floor((p.Y - env.MinY) / cell);
            x = Math.Clamp(x, 0, nx - 1);
            y = Math.Clamp(y, 0, ny - 1);
            return (x, y);
        }

        private static Pt2 CellCenter(int gx, int gy, Envelope env, double cell)
            => new(env.MinX + (gx + 0.5) * cell, env.MinY + (gy + 0.5) * cell);

        private static bool IsInBounds(int x, int y, int nx, int ny)
            => x >= 0 && y >= 0 && x < nx && y < ny;

        private static double PerpDistanceToSegment(Pt2 p, Pt2 a, Pt2 b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return p.DistanceTo(a);
            return Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / len;
        }
    }
}
