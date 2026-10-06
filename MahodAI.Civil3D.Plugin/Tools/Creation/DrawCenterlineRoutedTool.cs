using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using CivilDb = Autodesk.Civil.DatabaseServices;
using CivilSpiralType = Autodesk.Civil.SpiralType;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Surface-routed centerline drawer (Plan B / v2): user clicks A and B; tool builds
    /// the buildable polygon (TIN ⊖ obstacle layer), runs grid A* with medial-axis bias,
    /// simplifies the path, shows a transient route preview for a Hebrew confirm/cancel
    /// prompt, then creates a Civil 3D Alignment with curves at every PI.
    /// Curve radius is auto-relaxed per PI down to <c>r_floor</c> on infeasibility, with
    /// each relaxation reported in the result's <c>warnings</c> array.
    /// Contract guarantees:
    ///   • Clicks off the surface (outside AABB or beyond the snap threshold) FAIL with a
    ///     Hebrew message — they are never boundary-clamped into a "valid" endpoint.
    ///   • When an endpoint is snapped within threshold, the SNAPPED point is used for the
    ///     final geometry and reported via <c>start_snap</c>/<c>end_snap</c>.
    ///   • The final created geometry is re-sampled every ~10 m against the buildable mask;
    ///     <c>contained</c>/<c>outside_stations</c> report the honest result.
    /// </summary>
    public class DrawCenterlineRoutedTool : DrawingToolBase
    {
        /// <summary>Sampling step (m) for the post-creation containment readback.</summary>
        private const double ContainmentStepM = 5.0;
        public override string Name => "draw_centerline_routed";

        public override string Description =>
            "INTERACTIVE + AUTO-ROUTED: draw a road centerline that follows the active TIN " +
            "surface and skirts obstacle polylines. User clicks the start and end in the drawing; " +
            "the tool computes a path through the buildable region with smooth curves at every PI. " +
            "Use this when the user wants the road to follow the surface / navigate around obstacles. " +
            "For a literal straight A→B, use `draw_centerline` instead.";

        public override string Category => ToolCategories.Creation;

        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Modal picks + preview confirm — the executor flushes chat renders first.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""name"":           { ""type"": ""string"",  ""description"": ""Alignment name (English only)."" },
                ""radius"":         { ""type"": ""number"",  ""description"": ""Comfort/target curve radius (m), from road class / design speed. RELAXED by default: a PI that cannot host it relaxes per-PI down to r_floor and the road still draws (deviations flagged for engineer approval). Set allow_relaxation=false for the strict gate that rejects such a PI."" },
                ""r_floor"":        { ""type"": ""number"",  ""description"": ""Lowest radius (m) the per-PI relaxation may fall to (relaxed mode). Ignored when allow_relaxation=false (floor = radius). Default 30."" },
                ""allow_relaxation"": { ""type"": ""boolean"", ""description"": ""Default true — per-PI radius/spiral may relax (down to r_floor) and a sharp PI is tolerated so the road always draws; deviations are committed with requires_engineer_approval=true and the exact per-PI requested/achieved values (never a silent success). Set false for the strict production gate that rejects + rolls back a PI which cannot host the required radius/spiral."" },
                ""preview_only"":   { ""type"": ""boolean"", ""description"": ""Default false. When true, a final alignment that leaves the buildable area is reported as a non-committing candidate (requires_engineer_approval) instead of being rejected — nothing is persisted."" },
                ""obstacle_layer"": { ""type"": ""string"",  ""description"": ""AutoCAD layer of closed polylines defining no-build regions. Default 'MAHOD_NOBUILD'. Empty string skips subtraction."" },
                ""surface_name"":   { ""type"": ""string"",  ""description"": ""TIN surface name. Default: first available."" },
                ""cell_size_m"":    { ""type"": ""number"",  ""description"": ""Grid cell size for routing (m). Default 3. Smaller = finer routing, more PIs centred away from corridor walls. Auto-scaled up if total cells would exceed 250k."" },
                ""layer"":          { ""type"": ""string"",  ""description"": ""Layer for the alignment. Default '0'."" },
                ""use_spirals"":    { ""type"": ""boolean"", ""description"": ""Insert clothoid Spiral-Curve-Spiral (SCS) compound entities at each PI instead of plain Arc entities — matches engineer-built alignments. Default true."" },
                ""spiral_length_m"":{ ""type"": ""number"",  ""description"": ""Length (m) of each transition spiral when use_spirals=true. Default 50.0 (Israeli interurban convention)."" },
                ""any_angle"":      { ""type"": ""boolean"", ""description"": ""Use Theta* any-angle routing (line-of-sight relaxations) instead of legacy 8-direction A*. Default true. Set false only for diagnostic comparison; any_angle=true gives shorter, straighter paths with fewer PIs."" },
                ""boundary_bias"":  { ""type"": ""number"",  ""description"": ""How strongly the router pulls toward the corridor's medial axis vs. the shortest path. Higher = more centred (longer paths in narrow zones). Default 4.0. Try 6-8 in tight urban surfaces, 2-3 in wide rural ones."" },
                ""max_tangent_m"":  { ""type"": ""number"",  ""description"": ""Maximum straight-tangent length (m) before forcing an extra PI sampled from the raw A* path. Default 0 = OFF (a straight stretch stays one tangent, which is what a road should be). Only applies to the legacy densified fallback — an accepted tangent-arc fit is straight by construction and is never split. Set e.g. 500 only for a diagnostic comparison."" },
                ""min_tangent_m"":  { ""type"": ""number"",  ""description"": ""Minimum TRUE straight (m) required between two consecutive curves, on top of each curve's own tangent run (T = R·tan(Δ/2) + Ls/2). Two curves that leave less than this between them are broken-back and read as a wiggle, so the gentler of the two is merged out. Default 0 = auto: max(2 × spiral_length_m, 0.2 × radius)."" },
                ""follow_existing"": { ""type"": ""boolean"", ""description"": ""Bias routing toward an existing road on the surface. First tries surface breaklines (the green road centerline) and closed polylines (preferred zones); falls back to slope-based detection (flat strips) if neither is found. Default true. Set false for a fresh alignment that ignores any existing road."" },
                ""follow_strength"": { ""type"": ""number"",  ""description"": ""Cost discount on flat cells in the slope-based fallback. 0=no bias, 1=free. Default 0.6. Only used when no breaklines/zones exist on the surface."" },
                ""slope_threshold"": { ""type"": ""number"",  ""description"": ""Slope (rise/run) above which cells get NO discount in the slope-based fallback. Default 0.08 (~8%)."" },
                ""guide_layer"":     { ""type"": ""string"",  ""description"": ""Optional. Restrict breakline guide scan to entities on this layer. Default: read all breaklines on the surface."" },
                ""zone_layer"":      { ""type"": ""string"",  ""description"": ""Optional. Restrict closed-polyline zone scan to this layer. Default: any closed polyline in the A↔B AABB with perimeter ≥ 80m."" },
                ""preview_confirm"": { ""type"": ""boolean"", ""description"": ""Show a transient preview polyline of the routed centerline and ask the user to confirm (Hebrew אישור/ביטול) before creating the alignment. Default true. On cancel the tool returns success=true with cancelled=true and makes NO change to the drawing."" },
                ""min_clearance_m"":{ ""type"": ""number"",  ""description"": ""Minimum clearance (m) the centerline must keep from the surface boundary on BOTH sides, so a full road cross-section fits. 0 = off."" },
                ""max_triangle_edge_m"":{ ""type"": ""number"",  ""description"": ""Drop TIN triangles whose longest edge exceeds this (m) from the walkable mask. An unconstrained surface triangulates to its convex hull, bridging concave bays with long 'sliver' triangles that are not real ground — keeping them makes the road cut off-surface while still reporting 'contained'. 0 = auto (median longest edge × 8, floored at 30 m)."" },
                ""routing_mode"":   { ""type"": ""string"",  ""description"": ""'shortest' (default) = legacy length-minimising route with a weak medial-axis nudge (boundary_bias). 'centered' = ride the surface medial axis: distance-to-boundary becomes the objective so the centerline follows the band's CENTRAL SPINE instead of hugging a border. Use 'centered' for a fresh road on a bare survey surface. Ignores boundary_bias."" },
                ""centredness"":    { ""type"": ""number"",  ""description"": ""Strength of the medial-axis pull when routing_mode='centered'. A wall cell costs (1 + centredness)× a fully-central cell, with a non-saturating gradient across the full band width. Default 6. Higher = harder centering (risk of meandering into wide bays); lower = closer to shortest. Ignored in 'shortest' mode."" },
                ""target_clearance_m"": { ""type"": ""number"", ""description"": ""routing_mode='centered' only. Caps the centering reward: once a cell is at least this far (m) from the surface boundary it counts as fully-central, so the route stops chasing MORE width and goes straight through adequately-clear ground (kills unnecessary bows in wide bands). It still centers inside narrower necks. 0 = no cap (pure medial axis = bows toward the widest point everywhere). Typical 15-30 m."" },
                ""elastic_floor_m"": { ""type"": ""number"", ""description"": ""routing_mode='centered' only. Comfort off-edge margin (m) the post-route smoother holds in the WIDE band while straightening unnecessary bows/swings into straight runs; in narrow necks it self-lowers to the neck's own width (never below min_clearance_m). This is the main 'smoothness' knob: it removes the medial-axis swings without hugging the edge. 0 = falls back to target_clearance_m, else no smoothing. Typical 15-25 m."" },
                ""elastic_smooth"": { ""type"": ""boolean"", ""description"": ""routing_mode='centered' only. Enable the elastic shortcut smoother (default true). Set false to see the raw centered path (diagnostic A/B)."" },
                ""width_window_k"": { ""type"": ""number"", ""description"": ""routing_mode='centered' only. Width-adaptive centering in the A* cost: the centering reward is normalised PER CELL against the LOCAL band width (a box-max of the clearance field over a window of half-size k*localClearance) instead of one global maximum. 0 = off. NOTE: this is blind to the open side where the path already hugs a wall (the hugging cell's clearance is small so its window is tiny); prefer center_balance for true centering. Typical 0.4-0.7."" },
                ""center_balance"": { ""type"": ""boolean"", ""description"": ""routing_mode='centered' only. Direct ribbon centering (default false). After routing, each centerline vertex marches PERPENDICULAR to BOTH surface edges and shifts toward the midpoint (equal room left and right), iterated to convergence. Unlike width_window_k this measures the FULL distance to each edge from the vertex's actual position, so it re-centers a wall-hugging road no matter how far the open side runs ('add room to the tight side'). Smoothness is enforced by a curvature cap at the design radius and a width-scaled dead-band; every move stays on-surface and at/above min_clearance_m; replaces the elastic smoother when on."" },
                ""center_deadband_frac"": { ""type"": ""number"", ""description"": ""center_balance only. Dead-band on the centering, as a fraction of LOCAL surface width: the road only re-centers for off-centre offsets BEYOND this fraction — within it the road stays straight instead of chasing small width changes. Because it scales with width, a very wide reach tolerates a large absolute offset (stays straight) while a narrow band still centers tightly. 0 = always chase exact centre (wiggly). Typical 0.2-0.35."" },
                ""debug_draw"":     { ""type"": ""boolean"", ""description"": ""DIAGNOSTIC. When true, draws the router's internal view onto debug layers: MAHOD_DBG_MASK (walkable-mask boundary), MAHOD_DBG_RAW (raw A* path before smoothing), MAHOD_DBG_PTS (clicked vs snapped endpoints). Lets an engineer SEE why the route went where it did. Default false."" },
                ""priority_path"":   { ""type"": ""array"", ""description"": ""Engineer-drawn priority path the road must FOLLOW as first priority over the surface — ordered [[x,y],...] world coordinates. Each drawn segment is honored where the surface allows it; spans the surface can't host are auto-rerouted (and reported). Skips the interactive start/end pick. Provide this OR priority_path_layer."", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } } },
                ""priority_path_layer"": { ""type"": ""string"", ""description"": ""Alternative to priority_path: the plugin reads the longest polyline on this layer as the drawn priority path (the engineer's 'box'/centerline). Use when the path is drawn in the model rather than passed as coordinates."" },
                ""road_evidence"":  { ""type"": ""string"", ""description"": ""Route-evidence cascade for the from-scratch flow. 'auto' (default): derive the centerline from the EXISTING road's surveyed edge linework (asphalt/kerb/shoulder layers) inside the corridor zone loop and follow it in priority mode, falling back to the loop's midline when no linework qualifies. 'zone': skip the linework and use the corridor-loop midline directly. 'off': plain surface routing (previous behaviour). Ignored when priority_path / priority_path_layer is given."" },
                ""road_edge_layers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Layer-name fragments (case-insensitive) identifying road-edge survey linework, matched against the SHORT layer name after the last '$' (nested block layers like 'R73-2021$0$11KAV-ASFALT' match 'KAV-ASFALT'). Default: ASFALT/ASPHALT/אספלט, EVEN-SAFA/KERB/CURB/אבן שפה, SHULAIM/SHOULDER/שוליים, DERECH-SLULA."" }
            },
            ""required"": [""name"", ""radius""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            string name;
            try { name = GetRequiredStringParam(parameters, "name"); }
            catch (Exception ex) { return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, ex.Message)); }

            double? radiusOpt = GetDoubleParam(parameters, "radius");
            if (!radiusOpt.HasValue || radiusOpt.Value <= 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, "radius must be a positive number (m)."));
            double radius = radiusOpt.Value;
            double rFloor = GetDoubleParam(parameters, "r_floor") ?? 30.0;
            if (rFloor <= 0) rFloor = 30.0;
            if (rFloor > radius) rFloor = radius;
            // Owner decision 2026-07-27: default to the historical RELAXED behaviour so a road
            // always DRAWS (per-PI radius/spiral relax down to r_floor; a sharp PI is tolerated)
            // and deviations are surfaced as requires_engineer_approval — never a hard reject that
            // leaves nothing drawn (the HORIZONTAL_CURVE_INFEASIBLE stop). Pass allow_relaxation=false
            // to opt back into the strict P0-03 production gate (reject + roll back a PI that cannot
            // host the required radius/spiral).
            bool allowRelaxation = GetBoolParam(parameters, "allow_relaxation", defaultValue: true);
            var horizontalPolicy = allowRelaxation
                ? CurveAttacher.HorizontalDesignPolicy.Relaxed
                : CurveAttacher.HorizontalDesignPolicy.Strict;

            string obstacleLayer = GetStringParam(parameters, "obstacle_layer") ?? "MAHOD_NOBUILD";
            string? surfaceName = GetStringParam(parameters, "surface_name");
            double cellSize = GetDoubleParam(parameters, "cell_size_m") ?? 3.0;
            if (cellSize <= 0) cellSize = 3.0;
            string layer = GetStringParam(parameters, "layer") ?? "0";
            bool useSpirals = GetBoolParam(parameters, "use_spirals", defaultValue: true);
            double spiralLengthM = GetDoubleParam(parameters, "spiral_length_m") ?? 50.0;
            if (spiralLengthM <= 0) useSpirals = false;
            bool anyAngle = GetBoolParam(parameters, "any_angle", defaultValue: true);
            double boundaryBias = GetDoubleParam(parameters, "boundary_bias") ?? 4.0;
            if (boundaryBias < 0) boundaryBias = 4.0;
            // Default OFF: forcing a PI every 500 m used to chop straights into pieces that the
            // straightener was then forbidden to merge back (it never merges into a chord longer
            // than max_tangent_m). A road's straight stretch should be ONE tangent.
            double maxTangentM = GetDoubleParam(parameters, "max_tangent_m") ?? 0.0;
            if (maxTangentM < 0) maxTangentM = 0.0;
            double minTangentM = GetDoubleParam(parameters, "min_tangent_m") ?? 0.0;
            if (minTangentM < 0) minTangentM = 0.0;
            bool followExisting = GetBoolParam(parameters, "follow_existing", defaultValue: true);
            double followStrength = GetDoubleParam(parameters, "follow_strength") ?? 0.6;
            if (followStrength < 0) followStrength = 0;
            if (followStrength > 1) followStrength = 1;
            double slopeThreshold = GetDoubleParam(parameters, "slope_threshold") ?? 0.08;
            if (slopeThreshold <= 0) slopeThreshold = 0.08;
            string? guideLayer = GetStringParam(parameters, "guide_layer");
            string? zoneLayer = GetStringParam(parameters, "zone_layer");
            if (string.IsNullOrWhiteSpace(guideLayer)) guideLayer = null;
            if (string.IsNullOrWhiteSpace(zoneLayer)) zoneLayer = null;
            bool previewConfirm = GetBoolParam(parameters, "preview_confirm", defaultValue: true);
            // P0-02: production is fail-closed. If the FINAL alignment leaves the buildable
            // area (or the readback throws), the tool rejects and the executor rolls the
            // creation back. `preview_only=true` downgrades that to a non-committing
            // CandidateGenerated so a caller can inspect the deviation without persisting an
            // off-surface alignment.
            bool previewOnly = GetBoolParam(parameters, "preview_only", defaultValue: false);
            double minClearanceM = GetDoubleParam(parameters, "min_clearance_m") ?? 0.0;
            if (minClearanceM < 0) minClearanceM = 0.0;
            if (minClearanceM > 60) minClearanceM = 60.0;
            double maxTriangleEdgeM = GetDoubleParam(parameters, "max_triangle_edge_m") ?? 0.0;
            if (maxTriangleEdgeM < 0) maxTriangleEdgeM = 0.0;
            string routingMode = (GetStringParam(parameters, "routing_mode") ?? "shortest")
                .Trim().ToLowerInvariant();
            bool centered = routingMode == "centered";
            double centredness = GetDoubleParam(parameters, "centredness") ?? 6.0;
            if (centredness < 0) centredness = 0.0;
            double targetClearanceM = GetDoubleParam(parameters, "target_clearance_m") ?? 0.0;
            if (targetClearanceM < 0) targetClearanceM = 0.0;
            double elasticFloorM = GetDoubleParam(parameters, "elastic_floor_m") ?? 0.0;
            if (elasticFloorM < 0) elasticFloorM = 0.0;
            bool elasticSmooth = GetBoolParam(parameters, "elastic_smooth", defaultValue: true);
            double widthWindowK = GetDoubleParam(parameters, "width_window_k") ?? 0.0;
            if (widthWindowK < 0) widthWindowK = 0.0;
            bool centerBalance = GetBoolParam(parameters, "center_balance", defaultValue: false);
            double centerDeadbandFrac = GetDoubleParam(parameters, "center_deadband_frac") ?? 0.0;
            if (centerDeadbandFrac < 0) centerDeadbandFrac = 0.0;
            bool debugDraw = GetBoolParam(parameters, "debug_draw", defaultValue: false);

            // Road-evidence cascade: derive the route from the EXISTING road / corridor zone.
            string roadEvidence = (GetStringParam(parameters, "road_evidence") ?? "auto")
                .Trim().ToLowerInvariant();
            var roadEdgePatterns = ParseStringArray(parameters, "road_edge_layers")
                                   ?? DefaultRoadEdgePatterns;

            // ── Priority path (engineer-drawn) — the road FOLLOWS it over the surface ──
            string? priorityLayer = GetStringParam(parameters, "priority_path_layer");
            List<Pt2>? priorityWaypoints = ParsePriorityPath(parameters);
            if (priorityWaypoints == null && !string.IsNullOrWhiteSpace(priorityLayer))
            {
                try { priorityWaypoints = Routing.GuideDiscovery.ReadPriorityPath(tr, priorityLayer!); }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(
                        ToolErrorCodes.ExecutionFailed,
                        $"Failed to read priority path from layer '{priorityLayer}': {ex.Message}",
                        ex.ToString()));
                }
                if (priorityWaypoints == null || priorityWaypoints.Count < 2)
                    return Task.FromResult(ToolResult.Fail(
                        ToolErrorCodes.InvalidParameters,
                        $"No usable polyline found on priority_path_layer '{priorityLayer}'. " +
                        "Draw an open polyline along the desired road path and retry."));
            }
            bool priorityMode = priorityWaypoints != null && priorityWaypoints.Count >= 2;

            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document"));

            if (ObjectFinder.FindAlignment(civilDoc, tr, name) != null)
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    $"Alignment '{name}' already exists."));

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active document"));

            // Endpoints come from the drawn priority path (no interactive pick) when one is
            // supplied; otherwise the engineer clicks start/end as before.
            Pt2 a, b;
            if (priorityMode)
            {
                a = priorityWaypoints![0];
                b = priorityWaypoints[priorityWaypoints.Count - 1];
            }
            else
            {
                // Steady crosshair for both picks: no running snaps (which make the cursor jump
                // between survey vertices on a dense base), no grid snap, no ortho. Restored on
                // exit. The picked points are snapped onto the buildable mask by the router
                // anyway, so snapping them to arbitrary survey geometry buys nothing.
                using var pickScope = new Utilities.InteractivePickScope();

                // ── Pick start point ───────────────────────────────────────────
                ToolUiNotifier.Step("🖱️ **שלב 1/3 — בחר נקודת התחלה** על המשטח (Esc לביטול).");
                PromptPointResult startRes;
                try
                {
                    startRes = doc.Editor.GetPoint(new PromptPointOptions("\nבחר נקודת התחלה על המשטח:") { AllowNone = false });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, ex.Message, ex.ToString()));
                }
                if (startRes.Status != PromptStatus.OK)
                    return Task.FromResult(ToolResult.Cancelled(new
                    {
                        success = true,
                        cancelled = true,
                        status = "cancel",
                        message = "User cancelled at start point. No alignment created.",
                    }));

                // ── Pick end point ─────────────────────────────────────────────
                // Dashed rubber band from the start point so the engineer can see the span
                // being defined while aiming (owner request, 2026-08-02). Snaps/ortho stay
                // suspended by the pick scope above, so the crosshair itself is still steady.
                ToolUiNotifier.Step("🖱️ **שלב 2/3 — בחר נקודת סיום** (קו מקווקו נמתח מנקודת ההתחלה; Esc לביטול).");
                PromptPointResult endRes;
                try
                {
                    endRes = doc.Editor.GetPoint(
                        new PromptPointOptions("\nבחר נקודת סיום:")
                        {
                            AllowNone = false,
                            UseBasePoint = true,
                            BasePoint = startRes.Value,
                            UseDashedLine = true,
                        });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, ex.Message, ex.ToString()));
                }
                if (endRes.Status != PromptStatus.OK)
                    return Task.FromResult(ToolResult.Cancelled(new
                    {
                        success = true,
                        cancelled = true,
                        status = "cancel",
                        message = "User cancelled at end point. No alignment created.",
                    }));

                a = new Pt2(startRes.Value.X, startRes.Value.Y);
                b = new Pt2(endRes.Value.X, endRes.Value.Y);
                if (a.DistanceTo(b) < 1e-6)
                    return Task.FromResult(ToolResult.Fail(
                        ToolErrorCodes.InvalidParameters,
                        "Start and end points are identical. Pick two distinct points."));
            }

            // ── Road-evidence cascade (from-scratch flow only) ─────────────
            // The engineer picked A/B; if the drawing carries evidence of where the road should
            // go — surveyed road-edge linework inside the corridor loop, or at least the loop
            // itself — derive the centerline from it and switch to PRIORITY mode. The tangent-arc
            // fit then draws long tangents + one curve per real bend ON the old road, instead of
            // centering between the survey-band edges (which wanders wherever the band widens).
            object? roadEvidencePayload = null;
            bool evidenceMode = false;
            if (!priorityMode && roadEvidence != "off")
            {
                try
                {
                    var evInflate = Math.Max(50.0, 0.3 * a.DistanceTo(b));
                    var evAabb = new NetTopologySuite.Geometries.Envelope(
                        Math.Min(a.X, b.X) - evInflate, Math.Max(a.X, b.X) + evInflate,
                        Math.Min(a.Y, b.Y) - evInflate, Math.Max(a.Y, b.Y) + evInflate);
                    var zones = Routing.GuideDiscovery.Find(tr, ObjectId.Null, evAabb, null, zoneLayer).Zones;
                    var zone = PickEvidenceZone(zones, a, b);

                    // Corridor shape cascade: one closed loop, else two open parallel side
                    // polylines (engineers draw the blue corridor both ways).
                    List<Pt2>? sideA = null, sideB = null;
                    string? corridorKind = zone != null ? "closed_loop" : null;
                    if (zone == null)
                    {
                        (sideA, sideB) = Routing.GuideDiscovery.FindOpenSidePair(tr, evAabb, a, b, zoneLayer);
                        if (sideA != null && sideB != null) corridorKind = "open_pair";
                    }
                    Utilities.MahodLogger.Info(
                        $"[road-evidence] corridor={corridorKind ?? "none"} closed_zones={zones.Count} mode={roadEvidence}");

                    if (corridorKind != null)
                    {
                        // Inside-corridor filter for the survey linework: the loop itself, or a
                        // pseudo-loop stitched from the two open sides.
                        IReadOnlyList<Pt2> filterLoop;
                        if (zone != null) filterLoop = zone;
                        else
                        {
                            var pseudo = new List<Pt2>(sideA!);
                            var rev = new List<Pt2>(sideB!);
                            rev.Reverse();
                            pseudo.AddRange(rev);
                            filterLoop = pseudo;
                        }

                        Routing.RoadEvidenceExtractor.Result? extracted = null;
                        if (roadEvidence != "zone")
                        {
                            var chains = Routing.GuideDiscovery.CollectRoadEdges(
                                tr, evAabb, roadEdgePatterns, filterLoop);
                            if (chains.Count > 0)
                                extracted = zone != null
                                    ? Routing.RoadEvidenceExtractor.Extract(zone, a, b, chains)
                                    : Routing.RoadEvidenceExtractor.Extract(sideA!, sideB!, a, b, chains);
                            Utilities.MahodLogger.Info(
                                $"[road-evidence] edge_chains={chains.Count} extracted=" +
                                (extracted != null
                                    ? $"coverage={extracted.Coverage:F2} pts={extracted.Centerline.Count} " +
                                      $"layers=[{string.Join(",", extracted.LayersUsed)}]"
                                    : "null"));
                        }

                        List<Pt2>? evidencePath = extracted?.Centerline;
                        string? evidenceSource = extracted != null ? "survey_linework" : null;
                        if (evidencePath == null)
                        {
                            var midline = zone != null
                                ? Routing.RoadEvidenceExtractor.ZoneMidline(zone, a, b)
                                : Routing.RoadEvidenceExtractor.SidesMidline(sideA!, sideB!, a, b);
                            if (midline is { Count: >= 2 })
                            {
                                evidencePath = midline;
                                evidenceSource = "zone_midline";
                            }
                        }
                        if (evidencePath != null)
                        {
                            priorityWaypoints = evidencePath;
                            priorityMode = true;
                            evidenceMode = true;
                            roadEvidencePayload = new
                            {
                                source = evidenceSource,
                                corridor = corridorKind,
                                coverage = extracted != null ? Math.Round(extracted.Coverage, 2) : (double?)null,
                                half_width_left_m = extracted != null ? Math.Round(extracted.HalfWidthLeft, 2) : (double?)null,
                                half_width_right_m = extracted != null ? Math.Round(extracted.HalfWidthRight, 2) : (double?)null,
                                layers_used = extracted?.LayersUsed,
                                waypoints = evidencePath.Count,
                            };
                            Utilities.MahodLogger.Info(
                                $"[road-evidence] source={evidenceSource} corridor={corridorKind} " +
                                $"waypoints={evidencePath.Count} " +
                                $"coverage={(extracted != null ? extracted.Coverage.ToString("F2") : "-")}");
                        }
                        else
                        {
                            Utilities.MahodLogger.Info(
                                "[road-evidence] corridor found but no usable centerline — plain routing");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Utilities.MahodLogger.Error("[road-evidence] cascade failed — plain routing", ex);
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI][road-evidence] failed: {ex.Message} — falling back to plain routing");
                }
            }

            // ── Build walkable mask by sampling the TIN ────────────────────
            BuildableRegion.BuildResult region;
            try { region = BuildableRegion.Build(tr, civilDoc, surfaceName, obstacleLayer, cellSize, ct,
                                                 maxTriangleEdgeM: maxTriangleEdgeM); }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Buildable region construction failed: {ex.Message}",
                    ex.ToString()));
            }

            if (!region.Success || region.Walkable == null || region.Envelope == null)
            {
                return Task.FromResult(ToolResult.Fail(
                    "NO_BUILDABLE",
                    region.FailureReason ?? "Could not build walkable region. " + string.Join(" ", region.Warnings)));
            }

            // ── Build per-cell cost discount. Two layers, combined via min:
            //   1. Entity-based guides: surface breaklines (green road) and
            //      closed polylines (blue preferred zones) are read directly,
            //      rasterised, and given strong/moderate cost discounts.
            //      This is the preferred signal — it's authoritative.
            //   2. Slope-based fallback (RoadAffinity): infers road location
            //      from surface flatness. Only meaningful when (1) yields
            //      nothing, but harmless to layer underneath when it doesn't.
            // costMul stays null when follow_existing=false.
            double[,]? costMul = null;
            int guideLineCount = 0;
            int guideZoneCount = 0;
            if (followExisting && !priorityMode)
            {
                try
                {
                    var inflate = Math.Max(50.0, 0.3 * a.DistanceTo(b));
                    var aabb = new NetTopologySuite.Geometries.Envelope(
                        Math.Min(a.X, b.X) - inflate, Math.Max(a.X, b.X) + inflate,
                        Math.Min(a.Y, b.Y) - inflate, Math.Max(a.Y, b.Y) + inflate);
                    var guides = Routing.GuideDiscovery.Find(
                        tr, region.SurfaceId, aabb, guideLayer, zoneLayer);
                    foreach (var w in guides.Warnings) region.Warnings.Add(w);
                    guideLineCount = guides.Centerlines.Count;
                    guideZoneCount = guides.Zones.Count;
                    if (guideLineCount > 0 || guideZoneCount > 0)
                    {
                        costMul = Routing.GuideRasterizer.Build(
                            guides, region.Walkable, region.Envelope, region.CellSize);
                    }
                }
                catch (Exception ex)
                {
                    region.Warnings.Add($"Guide discovery failed: {ex.Message} (falling back to slope-based affinity)");
                }

                if (costMul == null && region.Slope != null && followStrength > 0)
                {
                    try
                    {
                        costMul = Routing.RoadAffinity.BuildCostField(
                            region.Walkable, region.Slope, slopeThreshold, followStrength);
                    }
                    catch (Exception ex)
                    {
                        region.Warnings.Add($"RoadAffinity.BuildCostField failed: {ex.Message} (falling back to unbiased routing)");
                        costMul = null;
                    }
                }
            }

            // ── Route ───────────────────────────────────────────────────────
            // Priority mode FOLLOWS the drawn path (rerouting only infeasible spans); the
            // normal mode routes A→B with the medial-axis / follow-existing cost field.
            GridRouter.RouteResult route;
            try
            {
                // The spiral length the alignment will actually be BUILT with must reach the
                // router: both the tangent-arc fit and the straightening pass size each curve's
                // tangent demand as T = R·tan(Δ/2) + Ls/2, and passing 0 understated it by Ls/2
                // per side — the pass then kept PIs whose curves could not fit, and CurveAttacher
                // shortened spirals / halved radii to force them in (the visible wiggle).
                double routerSpiralM = (useSpirals && spiralLengthM > 0) ? spiralLengthM : 0.0;
                route = priorityMode
                    ? GridRouter.RoutePriority(region.Walkable, region.Envelope, region.CellSize,
                                               priorityWaypoints!, ct,
                                               anyAngle: anyAngle, boundaryBias: boundaryBias,
                                               maxTangentM: maxTangentM, minClearanceM: minClearanceM,
                                               designRadiusM: radius, spiralLenM: routerSpiralM,
                                               minTangentM: minTangentM)
                    : GridRouter.Route(region.Walkable, region.Envelope, region.CellSize, a, b, ct,
                                       maxPis: 20, anyAngle: anyAngle, boundaryBias: boundaryBias,
                                       maxTangentM: maxTangentM, costMul: costMul, minClearanceM: minClearanceM,
                                       centered: centered, centredness: centredness,
                                       targetClearanceM: targetClearanceM,
                                       elasticFloorM: elasticFloorM, elasticSmooth: elasticSmooth,
                                       widthWindowK: widthWindowK, centerBalance: centerBalance,
                                       designRadiusM: radius, centerDeadbandFrac: centerDeadbandFrac,
                                       debug: debugDraw, spiralLenM: routerSpiralM,
                                       minTangentM: minTangentM);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Routing failed: {ex.Message}",
                    ex.ToString()));
            }

            if (!route.Success && evidenceMode)
            {
                // Evidence routing is OPPORTUNISTIC: any failure here (an off-surface span of the
                // old road, a snap failure at a survey gap) falls back to the plain routed flow
                // rather than failing a request the legacy path could still serve.
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI][road-evidence] priority routing failed " +
                    $"({route.FailureCode}: {route.FailureReason}) — retrying plain routing");
                roadEvidencePayload = new { source = "fallback_plain_routing", reason = route.FailureCode };
                priorityMode = false;
                evidenceMode = false;
                try
                {
                    double retrySpiralM = (useSpirals && spiralLengthM > 0) ? spiralLengthM : 0.0;
                    route = GridRouter.Route(region.Walkable, region.Envelope, region.CellSize, a, b, ct,
                                       maxPis: 20, anyAngle: anyAngle, boundaryBias: boundaryBias,
                                       maxTangentM: maxTangentM, costMul: costMul, minClearanceM: minClearanceM,
                                       centered: centered, centredness: centredness,
                                       targetClearanceM: targetClearanceM,
                                       elasticFloorM: elasticFloorM, elasticSmooth: elasticSmooth,
                                       widthWindowK: widthWindowK, centerBalance: centerBalance,
                                       designRadiusM: radius, centerDeadbandFrac: centerDeadbandFrac,
                                       debug: debugDraw, spiralLenM: retrySpiralM,
                                       minTangentM: minTangentM);
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(
                        ToolErrorCodes.ExecutionFailed,
                        $"Routing failed: {ex.Message}",
                        ex.ToString()));
                }
            }

            if (!route.Success)
            {
                // Priority-path span the surface cannot host even with a detour — tell the
                // engineer it's impossible (the "report when it can't follow the drawn line" case).
                if (route.FailureCode == GridRouter.FailureCodes.PrioritySpanInfeasible)
                {
                    return Task.FromResult(ToolResult.Fail(
                        GridRouter.FailureCodes.PrioritySpanInfeasible,
                        "התוואי שסומן אינו עביר על המשטח: " +
                        $"{route.FailureReason}. " +
                        "קטע מהתוואי המסומן יוצא מגבולות המשטח ואין דרך לעקוף אותו על הקרקע. " +
                        "תקן את הקו המסומן או הרחב את המשטח ונסה שוב.",
                        route.FailureReason));
                }
                // Off-surface clicks fail explicitly — never boundary-clamp-and-continue.
                if (route.FailureCode == GridRouter.FailureCodes.StartOffSurface)
                {
                    return Task.FromResult(ToolResult.Fail(
                        "POINT_OFF_SURFACE",
                        "נקודת ההתחלה שנבחרה אינה על המשטח. אנא בחר נקודת התחלה על גבי המשטח ונסה שוב.",
                        route.FailureReason));
                }
                if (route.FailureCode == GridRouter.FailureCodes.EndOffSurface)
                {
                    return Task.FromResult(ToolResult.Fail(
                        "POINT_OFF_SURFACE",
                        "נקודת הסיום שנבחרה אינה על המשטח. אנא בחר נקודת סיום על גבי המשטח ונסה שוב.",
                        route.FailureReason));
                }
                if (route.FailureCode == GridRouter.FailureCodes.ClearanceInsufficient)
                {
                    return Task.FromResult(ToolResult.Fail(
                        "CLEARANCE_INSUFFICIENT",
                        $"לא קיים מסדרון רחב מספיק כדי לשמור על מרווח של {minClearanceM:F1} מ' מגבול המשטח בשני הצדדים " +
                        "(מקום לנתיבים + שוליים + ניקוז). נסה (1) להרחיב את המשטח, (2) לקצר את התוואי / לבחור נקודות פנימיות יותר, " +
                        "או (3) להקטין את רוחב הכביש (min_clearance_m).",
                        route.FailureReason));
                }
                // If the sliver filter dropped triangles, an over-aggressive trim can sever a
                // legitimately thin neck of the real band — surface that as a specific, actionable
                // hint (raise max_triangle_edge_m) instead of the generic "no path".
                string sliverHint = region.TrianglesKept < region.TrianglesTotal
                    ? $" ייתכן שמסנן המשטח הסיר רצועה צרה לגיטימית " +
                      $"(נשמרו {region.TrianglesKept:N0}/{region.TrianglesTotal:N0} משולשים, סף קצה {region.MaxEdgeThresholdM:F0} מ'); " +
                      $"נסה להגדיל את max_triangle_edge_m."
                    : "";
                return Task.FromResult(ToolResult.Fail(
                    "NO_PATH",
                    $"לא נמצא מסלול חוקי בין A ל-B בתוך המשטח ({route.FailureReason}). " +
                    "נסה (1) להוריד cell_size_m, (2) להזיז A או B אל תוך המשטח, (3) לבדוק שכבת מכשולים." +
                    sliverHint));
            }
            if (route.Path.Length < 2)
            {
                return Task.FromResult(ToolResult.Fail(
                    "NO_PATH",
                    "Routed path has fewer than 2 PIs after simplification — can't build alignment."));
            }

            double routeLengthM = 0;
            for (int i = 0; i < route.Path.Length - 1; i++)
                routeLengthM += route.Path[i].DistanceTo(route.Path[i + 1]);

            var startSnapPayload = BuildSnapPayload(route.StartSnap);
            var endSnapPayload = BuildSnapPayload(route.EndSnap);

            // ── Debug overlay: draw the router's internal view BEFORE the preview gate, so it
            //    persists for inspection even if the engineer cancels at the preview. Best-effort. ──
            RouteDebugDrawer.DebugStats? dbg = null;
            if (debugDraw)
            {
                try
                {
                    var dbgDb = HostApplicationServices.WorkingDatabase;
                    dbg = RouteDebugDrawer.Draw(tr, dbgDb, region.Walkable, region.Envelope,
                                                region.CellSize, route, a, b);
                }
                catch (Exception ex)
                {
                    region.Warnings.Add($"Debug overlay failed: {ex.Message}");
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI][routed-debug] mode={(centered ? "centered" : "shortest")} centredness={centredness:F1} " +
                    $"clickedStart=({a.X:F2},{a.Y:F2}) clickedEnd=({b.X:F2},{b.Y:F2}) " +
                    $"snapStart={(route.StartSnap?.Snapped == true ? $"{route.StartSnap.DistanceM:F1}m->({route.StartSnap.Point.X:F2},{route.StartSnap.Point.Y:F2})" : "none")} " +
                    $"snapEnd={(route.EndSnap?.Snapped == true ? $"{route.EndSnap.DistanceM:F1}m->({route.EndSnap.Point.X:F2},{route.EndSnap.Point.Y:F2})" : "none")} " +
                    $"rawPathPts={route.RawPath?.Length ?? 0} simplifiedPIs={route.Path.Length} routeLen={routeLengthM:F1}m " +
                    $"maskSegs={dbg?.MaskSegments ?? 0}{(dbg?.MaskTruncated == true ? "(truncated)" : "")} " +
                    $"grid={route.GridNx}x{route.GridNy}@{route.CellSize:F1}m walkable={route.CellsWalkable}");

                // Chord-vs-path verdicts: which bows are forced by the band vs. unnecessary
                // medial-axis dives. This is the evidence that drives the fix — logged to plugin.log.
                try
                {
                    foreach (var line in RouteDiagnostics.Report(
                        region.Walkable, region.Envelope, region.CellSize,
                        route.RawPath ?? Array.Empty<Pt2>(), route.DistanceToBoundary, minClearanceM))
                    {
                        System.Diagnostics.Debug.WriteLine(line);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[routed-diag] failed: {ex.Message}");
                }
            }

            // ── Preview + Hebrew confirm gate ──────────────────────────────
            // Transient polyline of the routed centerline; the engineer confirms ONCE
            // (אישור) or cancels (ביטול). Cancel returns success=true + cancelled=true
            // with NO mutation of the drawing.
            if (previewConfirm)
            {
                bool confirmed;
                try
                {
                    confirmed = ShowPreviewAndConfirm(doc.Editor, route.Path);
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(
                        ToolErrorCodes.ExecutionFailed,
                        $"Route preview failed: {ex.Message}",
                        ex.ToString()));
                }

                if (!confirmed)
                {
                    return Task.FromResult(ToolResult.Cancelled(new
                    {
                        success = true,
                        cancelled = true,
                        status = "cancel",
                        message = "המשתמש ביטל את יצירת הציר לאחר תצוגה מקדימה של התוואי. לא בוצע שינוי בשרטוט.",
                        route_length_m = Math.Round(routeLengthM, 2),
                        pi_count = route.Path.Length,
                        start_snap = startSnapPayload,
                        end_snap = endSnapPayload,
                        surface_name = region.SurfaceName,
                    }));
                }
            }

            // ── Resolve alignment defaults ─────────────────────────────────
            ObjectId styleId = ObjectId.Null;
            try { foreach (ObjectId id in civilDoc.Styles.AlignmentStyles) { styleId = id; break; } }
            catch { }

            ObjectId labelSetId = ObjectId.Null;
            try { foreach (ObjectId id in civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles) { labelSetId = id; break; } }
            catch { }

            var db = HostApplicationServices.WorkingDatabase;
            ObjectId layerId = db.Clayer;
            try
            {
                var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (lt != null && lt.Has(layer)) layerId = lt[layer];
            }
            catch { }

            // ── Create alignment ───────────────────────────────────────────
            ObjectId alignmentId;
            try
            {
                alignmentId = CivilDb.Alignment.Create(
                    civilDoc, name, ObjectId.Null, layerId, styleId, labelSetId,
                    CivilDb.AlignmentType.Centerline);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Alignment.Create failed: {ex.Message}",
                    ex.ToString()));
            }

            CivilDb.Alignment alignment;
            try { alignment = (CivilDb.Alignment)tr.GetObject(alignmentId, OpenMode.ForWrite); }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Could not open new alignment for write: {ex.Message}",
                    ex.ToString()));
            }

            // ── Wire CurveAttacher to Civil 3D entity API ──────────────────
            // Pt2 is the routing engine's coordinate type; convert to Civil 3D Point3d at the boundary.
            CurveAttacher.AddLineFn addLine = (s, e) =>
            {
                var ln = alignment.Entities.AddFixedLine(
                    new Point3d(s.X, s.Y, 0),
                    new Point3d(e.X, e.Y, 0));
                return ln.EntityId;
            };

            CurveAttacher.TryAddCurveFn tryCurve = (prev, next, r) =>
            {
                try
                {
                    alignment.Entities.AddFreeCurve(
                        prev, next, r,
                        CivilDb.CurveParamType.Radius,
                        isGreaterThan180: false,
                        CivilDb.CurveType.Compound);
                    return true;
                }
                catch { return false; }
            };

            // Spiral-Curve-Spiral attempt: tried first per PI when use_spirals=true. Mirrors
            // the AddFreeSCS call in FixAlignmentGeometryTool — same clothoid type and equal
            // entry/exit spiral lengths, which matches Israeli interurban convention. The
            // delegate now receives the spiral length per call so CurveAttacher can vary it
            // (e.g. shorten 50→25→12.5) before falling back to a plain arc.
            CurveAttacher.TryAddScsFn? tryScs = null;
            if (useSpirals && spiralLengthM > 0)
            {
                tryScs = (prev, next, r, L) =>
                {
                    try
                    {
                        alignment.Entities.AddFreeSCS(
                            prev, next,
                            L, L,
                            CivilDb.SpiralParamType.Length,
                            r,
                            isGreaterThan180: false,
                            CivilSpiralType.Clothoid);
                        return true;
                    }
                    catch { return false; }
                };
            }

            CurveAttacher.AttachResult attached;
            try { attached = CurveAttacher.Attach(route.Path, radius, rFloor, addLine, tryCurve, tryScs, spiralLengthM, horizontalPolicy); }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"CurveAttacher failed: {ex.Message}",
                    ex.ToString()));
            }
            if (!attached.Success)
            {
                // P0-03: an obligatory PI could not take the required curve. Reject with the
                // per-PI deviations so the executor rolls the creation back (strict mode).
                return Task.FromResult(ToolResult.Rejected(
                    "HORIZONTAL_CURVE_INFEASIBLE",
                    attached.FailureReason ?? "CurveAttacher returned failure",
                    BuildCurveViolations(attached, radius, spiralLengthM, "hard")));
            }

            // ── Post-creation containment readback (P0-02) ─────────────────
            // The grid route only proves PI-to-PI chords; the attached arcs/SCS bulge
            // between them. Sample the FINAL alignment geometry against the buildable mask.
            // A fixed step alone can step over a short curve bulge, so force a sample at
            // every entity endpoint and curve apex (¼/½/¾) via CollectCriticalStations.
            // This is a HARD GATE: on failure the tool rejects and the executor rolls the
            // creation back (unless preview_only).
            bool contained = true;
            bool readbackThrew = false;
            string? readbackError = null;
            var outsideStations = Array.Empty<double>();
            var outsideSamples = new List<(double Station, Pt2 Point)>();
            var subClearanceSamples = new List<(double Station, Pt2 Point)>();
            int containmentSamples = 0;
            try
            {
                var criticalStations = CollectCriticalStations(alignment);
                var stations = ContainmentSampler.BuildStations(
                    alignment.StartingStation, alignment.EndingStation, ContainmentStepM,
                    criticalStations);
                var samples = new List<(double Station, Pt2 Point)>(stations.Length);
                foreach (var st in stations)
                {
                    try
                    {
                        double sx = 0, sy = 0;
                        alignment.PointLocation(st, 0, ref sx, ref sy);
                        samples.Add((st, new Pt2(sx, sy)));
                    }
                    catch
                    {
                        // PointLocation failure at a station means we cannot prove that
                        // station — count it as outside rather than silently skipping.
                        samples.Add((st, new Pt2(double.MaxValue, double.MaxValue)));
                    }
                }
                var containment = ContainmentSampler.Check(
                    samples, region.Walkable, region.Envelope, region.CellSize,
                    dtb: route.DistanceToBoundary, minClearanceM: minClearanceM);
                containmentSamples = containment.SamplesChecked;

                // The wire contract's `contained`/`outside_stations` mean OFF-SURFACE only —
                // "there is no ground data under the axis", the P0-02 absolute. A sample that
                // IS on ground but closer to the surface boundary than min_clearance_m is a
                // different animal: an existing road inside its own narrow survey band can
                // never satisfy a boundary clearance the band doesn't have. Those become
                // engineer-approval warnings below instead of a hard reject.
                contained = containment.OffSurfaceSamples.Count == 0;
                outsideSamples = new List<(double, Pt2)>(containment.OffSurfaceSamples);
                outsideStations = containment.OffSurfaceSamples
                    .Select(s => Math.Round(s.Station, 1)).ToArray();
                subClearanceSamples = new List<(double, Pt2)>(containment.SubClearanceSamples);

                Utilities.MahodLogger.Info(
                    $"[containment] off_surface={outsideSamples.Count} " +
                    $"sub_clearance={subClearanceSamples.Count} samples={containmentSamples} " +
                    $"min_clearance_m={minClearanceM:F1}");
                foreach (var (st, p) in outsideSamples.Take(12))
                    Utilities.MahodLogger.Info(
                        $"[containment] OFF-SURFACE at {st:F1} ({p.X:F1},{p.Y:F1})");
                foreach (var (st, p) in subClearanceSamples.Take(12))
                    Utilities.MahodLogger.Info(
                        $"[containment] sub-clearance at {st:F1} ({p.X:F1},{p.Y:F1})");
            }
            catch (Exception ex)
            {
                // A thrown readback proves NOTHING about containment — never downgrade it
                // to a warning and commit (the old fail-open bug). Treat it as a hard gate
                // failure so the executor aborts the transaction.
                contained = false;
                readbackThrew = true;
                readbackError = ex.Message;
                region.Warnings.Add($"Containment readback failed: {ex.Message}");
                Utilities.MahodLogger.Error("[containment] readback threw", ex);
            }

            // ── Hard gate: no ground data under the final geometry (P0-02) ──
            if (!contained)
            {
                var violations = BuildContainmentViolations(
                    readbackThrew, readbackError, outsideSamples, minClearanceM);

                if (!previewOnly)
                {
                    // Production: reject → executor aborts → the created alignment is
                    // rolled back with the rest of the transaction. Fail-closed.
                    return Task.FromResult(ToolResult.Rejected(
                        readbackThrew ? "CONTAINMENT_VALIDATION_FAILED" : "FINAL_GEOMETRY_OUTSIDE_BUILDABLE",
                        readbackThrew
                            ? $"בדיקת ההכלה נכשלה ({readbackError}); הציר לא נוצר."
                            : $"הציר הסופי חורג משטח הבנייה ב-{outsideStations.Length} תחנות (אין נתוני קרקע); הציר לא נוצר.",
                        violations));
                }

                // preview_only: don't commit (CandidateGenerated aborts the transaction too),
                // but report the deviation so a caller can decide. NOTE: a persistent visual
                // preview via transient graphics is a documented follow-up — for now the
                // candidate is reported to the agent, not drawn.
                return Task.FromResult(ToolResult.Candidate(
                    new
                    {
                        success = true,
                        preview_only = true,
                        contained = false,
                        outside_stations = outsideStations,
                        containment_samples = containmentSamples,
                        surface_name = region.SurfaceName,
                        message = "תצוגה מקדימה בלבד: הציר הסופי חורג משטח הבנייה ולא נשמר.",
                    },
                    new EngineeringGateResult
                    {
                        HardGatesPassed = false,
                        RequiresEngineerApproval = true,
                        Violations = violations,
                    }));
            }

            // ── Invalidate caches ──────────────────────────────────────────
            cache.RemoveByPattern("list_alignments:");
            cache.RemoveByPattern("get_drawing_summary:");

            // ── Build result payload ───────────────────────────────────────
            var warningPayload = attached.Warnings.Select(w => new
            {
                pi_index = w.PiIndex,
                placed_as = w.PlacedAs,
                requested_r_m = Math.Round(w.RequestedM, 1),
                achieved_r_m = Math.Round(w.AchievedM, 1),
                reason = w.Reason,
            }).ToList();

            // Field-diagnosable one-liner: which geometry engine actually drew the road.
            Utilities.MahodLogger.Info(
                $"[route] geometry={route.PathSource} pis={route.Path.Length} " +
                $"tangents={route.TangentCount} priority={priorityMode} evidence={evidenceMode} " +
                $"rerouted_spans={route.ReroutedSpanCount}");

            Pt2 routedStart = route.Path[0];
            Pt2 routedEnd = route.Path[^1];

            var okResult = ToolResult.Ok(new
            {
                success = true,
                cancelled = false,
                alignment_name = alignment.Name,
                length_m = Math.Round(alignment.Length, 2),
                route_length_m = Math.Round(routeLengthM, 2),
                pi_count = route.Path.Length,
                priority_path = priorityMode,
                road_evidence = roadEvidencePayload,
                rerouted_span_count = route.ReroutedSpanCount,
                scs_count = attached.ScsCount,
                arc_count = attached.ArcCount,
                spiral_length_m = useSpirals ? spiralLengthM : 0,
                start_point = new { x = Math.Round(routedStart.X, 3), y = Math.Round(routedStart.Y, 3) },
                end_point = new { x = Math.Round(routedEnd.X, 3), y = Math.Round(routedEnd.Y, 3) },
                start_snap = startSnapPayload,
                end_snap = endSnapPayload,
                contained,
                outside_stations = outsideStations,
                // Ground exists at these stations, but the centerline is closer to the surface
                // boundary than min_clearance_m — committed WITH requires_engineer_approval.
                clearance_shortfall_stations = subClearanceSamples
                    .Select(s => Math.Round(s.Station, 1)).ToArray(),
                clearance_required_m = minClearanceM,
                containment_samples = containmentSamples,
                containment_step_m = ContainmentStepM,
                buildable_area_m2 = Math.Round(region.CellsWalkable * region.CellSize * region.CellSize, 1),
                obstacle_count = region.ObstacleCount,
                surface_name = region.SurfaceName,
                grid = new
                {
                    cell_size_m = Math.Round(route.CellSize, 2),
                    cells_total = route.CellsTotal,
                    cells_walkable = route.CellsWalkable,
                    nx = route.GridNx,
                    ny = route.GridNy,
                    any_angle = anyAngle,
                    routing_mode = centered ? "centered" : "shortest",
                    centredness = centered ? centredness : 0.0,
                    target_clearance_m = centered ? targetClearanceM : 0.0,
                    elastic_smooth = centered && elasticSmooth,
                    elastic_floor_m = (centered && elasticSmooth)
                        ? (elasticFloorM > 0 ? elasticFloorM : targetClearanceM) : 0.0,
                    width_window_k = centered ? widthWindowK : 0.0,
                    center_balance = centered && centerBalance,
                    center_deadband_frac = (centered && centerBalance) ? centerDeadbandFrac : 0.0,
                    boundary_bias = boundaryBias,
                    max_tangent_m = maxTangentM,
                    min_tangent_m = minTangentM,
                    // "tangent_arc_fit" = engineer-style geometry (long straight tangents, one
                    // curve per real direction change). "densified" = the legacy fallback, used
                    // only when no straightened fit stayed inside the buildable area.
                    geometry = route.PathSource,
                    tangent_count = route.TangentCount,
                    follow_existing = followExisting && costMul != null,
                    follow_strength = followStrength,
                    slope_threshold = slopeThreshold,
                    guide_centerlines = guideLineCount,
                    guide_zones = guideZoneCount,
                },
                mask = new
                {
                    triangles_total = region.TrianglesTotal,
                    triangles_kept = region.TrianglesKept,
                    kept_long_fat = region.TrianglesKeptLongFat,
                    dropped_thin = region.TrianglesDroppedThin,
                    dropped_mega = region.TrianglesDroppedMega,
                    hidden_by_civil3d = region.TrianglesHidden,
                    // Isolated 1-2 cell rasterisation pinholes closed after the sliver filter;
                    // they used to block tangent-fit chords and trip the containment gate.
                    cells_hole_filled = region.CellsHoleFilled,
                    length_gate_m = Math.Round(region.MaxEdgeThresholdM, 1),
                    median_edge_m = Math.Round(region.MedianEdgeM, 1),
                    p90_edge_m = Math.Round(region.P90EdgeM, 1),
                    p99_edge_m = Math.Round(region.P99EdgeM, 1),
                },
                debug = debugDraw ? new
                {
                    raw_path_points = route.RawPath?.Length ?? 0,
                    simplified_pis = route.Path.Length,
                    mask_boundary_segments = dbg?.MaskSegments ?? 0,
                    mask_truncated = dbg?.MaskTruncated ?? false,
                    clicked_start = new { x = Math.Round(a.X, 3), y = Math.Round(a.Y, 3) },
                    clicked_end = new { x = Math.Round(b.X, 3), y = Math.Round(b.Y, 3) },
                    layers = new[] { RouteDebugDrawer.MaskLayer, RouteDebugDrawer.RawLayer, RouteDebugDrawer.PtsLayer },
                } : null,
                region_warnings = region.Warnings,
                warnings = warningPayload,
                message = BuildHebrewMessage(alignment.Name, alignment.Length, route.Path.Length,
                                             attached.ScsCount, attached.ArcCount, attached.Warnings.Count,
                                             route.StartSnap, route.EndSnap, contained, outsideStations.Length,
                                             minClearanceM, route.PathSource),
            });

            // P0-03: a relaxed candidate (only reachable with allow_relaxation=true) is buildable
            // and contained, so it COMMITS and the user sees it — but it must never look like an
            // approved design. Flag it loudly on the typed channel: requires_engineer_approval +
            // the exact per-PI deviations. The hard gate still passed (geometry is buildable), so
            // the executor commits; the agent routes the approval flag to the human gate.
            // Sub-clearance stations (ground exists, boundary margin short — the existing-road-
            // in-a-narrow-band case) ride the same approval channel.
            bool clearanceShortfall = subClearanceSamples.Count > 0;
            if (attached.HasDeviations || clearanceShortfall)
            {
                var vio = BuildCurveViolations(attached, radius, spiralLengthM, "warning");
                if (clearanceShortfall)
                    vio.AddRange(BuildClearanceViolations(subClearanceSamples, minClearanceM));
                okResult.Engineering = new EngineeringGateResult
                {
                    HardGatesPassed = true,
                    RequiresEngineerApproval = true,
                    Violations = vio,
                };
            }

            return Task.FromResult(okResult);
        }

        // ── Preview + confirm helpers ───────────────────────────────────────

        /// <summary>Default road-edge layer fragments (Israeli survey conventions, transliterated + Hebrew).</summary>
        private static readonly string[] DefaultRoadEdgePatterns =
        {
            "ASFALT", "ASPHALT", "אספלט",            // asphalt edge (KAV-ASFALT)
            "EVEN-SAFA", "KERB", "CURB", "אבן שפה",  // kerb line
            "SHULAIM", "SHOULDER", "שוליים",          // shoulders
            "DERECH-SLULA",                           // paved road
        };

        private static string[]? ParseStringArray(JsonElement parameters, string name)
        {
            if (!parameters.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                    list.Add(e.GetString()!.Trim());
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// The corridor zone for evidence extraction: prefer a loop containing BOTH picks,
        /// then one containing either, else none (a random far-away loop must never win).
        /// </summary>
        private static List<Pt2>? PickEvidenceZone(List<List<Pt2>> zones, Pt2 a, Pt2 b)
        {
            if (zones == null || zones.Count == 0) return null;
            List<Pt2>? either = null;
            foreach (var z in zones)
            {
                bool inA = Routing.RoadEvidenceExtractor.PointInLoop(z, a);
                bool inB = Routing.RoadEvidenceExtractor.PointInLoop(z, b);
                if (inA && inB) return z;
                if ((inA || inB) && either == null) either = z;
            }
            return either;
        }

        /// <summary>
        /// Parses the optional <c>priority_path</c> param: an ordered array of [x,y] pairs the
        /// road must follow. Returns null when absent/empty so the caller can fall back to
        /// <c>priority_path_layer</c> or the interactive pick.
        /// </summary>
        private static List<Pt2>? ParsePriorityPath(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("priority_path", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return null;

            var pts = new List<Pt2>();
            foreach (var pair in arr.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array) continue;
                var coords = pair.EnumerateArray().ToList();
                if (coords.Count < 2) continue;
                if (coords[0].ValueKind != JsonValueKind.Number ||
                    coords[1].ValueKind != JsonValueKind.Number) continue;
                pts.Add(new Pt2(coords[0].GetDouble(), coords[1].GetDouble()));
            }
            return pts.Count >= 2 ? pts : null;
        }

        private static object? BuildSnapPayload(GridRouter.EndpointSnap? snap)
        {
            if (snap == null) return null;
            return new
            {
                snapped = snap.Snapped,
                distance_m = Math.Round(snap.DistanceM, 2),
            };
        }

        /// <summary>
        /// Draws the routed path as a transient (non-database) polyline, asks the engineer
        /// to confirm in Hebrew, and ALWAYS erases the transient before returning —
        /// on confirm, on cancel, and on exception alike.
        /// </summary>
        private static bool ShowPreviewAndConfirm(Editor ed, Pt2[] path)
        {
            var tm = Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager;
            var preview = new Polyline(path.Length);
            try
            {
                for (int i = 0; i < path.Length; i++)
                    preview.AddVertexAt(i, new Point2d(path[i].X, path[i].Y), 0, 0, 0);
                preview.ColorIndex = 1;   // red — clearly a preview, not committed geometry

                tm.AddTransient(preview,
                    Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode.Highlight,
                    128, new IntegerCollection());
                ed.UpdateScreen();

                ToolUiNotifier.Step(
                    "👀 **שלב 3/3 — אישור התצוגה המקדימה:** התוואי המוצע מסומן בשרטוט. " +
                    "הקש **Enter** בשורת הפקודה לאישור, או **Esc / ביטול** לביטול.");
                var pko = new PromptKeywordOptions(
                    "\nהתוואי המוצע מסומן בשרטוט. לאשר יצירת ציר לפי התוואי?")
                {
                    AllowNone = true,
                };
                pko.Keywords.Add("Approve", "אישור", "אישור");
                pko.Keywords.Add("Cancel", "ביטול", "ביטול");
                pko.Keywords.Default = "Approve";

                var res = ed.GetKeywords(pko);
                return res.Status == PromptStatus.OK &&
                       string.Equals(res.StringResult, "Approve", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                // The engineer has answered — drop the instruction NOW. Waiting
                // for the tool to return leaves "הקש Enter לאישור" on screen for
                // the whole corridor build, long after there is nothing to press.
                ToolUiNotifier.ClearStep();
                try { tm.EraseTransient(preview, new IntegerCollection()); } catch { }
                try { ed.UpdateScreen(); } catch { }
                preview.Dispose();
            }
        }

        /// <summary>
        /// P0-02: collects "mandatory" sample stations from the FINAL alignment's entities —
        /// every entity's start/end plus its ¼/½/¾ interior points. Forcing a sample at each
        /// curve apex (½) means a short arc/SCS bulge that leaves the buildable area can no
        /// longer hide between two fixed-step samples. Best-effort: any failure returns an
        /// empty set and the readback falls back to the fixed step alone.
        /// </summary>
        private static List<double> CollectCriticalStations(CivilDb.Alignment alignment)
        {
            var stations = new List<double>();
            try
            {
                foreach (CivilDb.AlignmentEntity ent in alignment.Entities)
                {
                    // StartStation/EndStation live on the concrete subtypes (AlignmentLine /
                    // AlignmentArc / AlignmentSpiral / AlignmentSCS), not the base
                    // AlignmentEntity — read them reflectively (same pattern the profile
                    // tools use for K/CurveLength).
                    double? s0 = ReadDoubleProp(ent, "StartStation");
                    double? s1 = ReadDoubleProp(ent, "EndStation");
                    if (s0 == null || s1 == null) continue;

                    double a = s0.Value, b = s1.Value;
                    if (b < a) (a, b) = (b, a);
                    double len = b - a;
                    if (len <= 1e-6) continue;
                    stations.Add(a);
                    stations.Add(a + len * 0.25);
                    stations.Add(a + len * 0.5);
                    stations.Add(a + len * 0.75);
                    stations.Add(b);
                }
            }
            catch
            {
                // Entity enumeration unavailable — fall back to the fixed grid.
            }
            return stations;
        }

        /// <summary>Reflectively reads a <see cref="double"/> property by name; null if absent/unreadable.</summary>
        private static double? ReadDoubleProp(object obj, string propName)
        {
            try
            {
                var p = obj.GetType().GetProperty(propName);
                if (p == null) return null;
                var v = p.GetValue(obj);
                if (v == null) return null;
                return Convert.ToDouble(v);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// P0-02: turns a failed containment readback into structured, located
        /// <see cref="EngineeringViolation"/>s for the agent/UI. Caps the count so a badly
        /// off-surface alignment doesn't flood the payload.
        /// </summary>
        private static List<EngineeringViolation> BuildContainmentViolations(
            bool readbackThrew, string? readbackError,
            List<(double Station, Pt2 Point)> outsideSamples, double minClearanceM)
        {
            var list = new List<EngineeringViolation>();
            if (readbackThrew)
            {
                list.Add(new EngineeringViolation
                {
                    Code = "CONTAINMENT_VALIDATION_FAILED",
                    Message = $"Containment readback threw: {readbackError}",
                    Severity = "hard",
                });
                return list;
            }

            const int cap = 12;
            int i = 0;
            foreach (var (station, p) in outsideSamples)
            {
                if (i++ >= cap) break;
                var v = new EngineeringViolation
                {
                    Code = "FINAL_GEOMETRY_OUTSIDE_BUILDABLE",
                    Message = $"Final alignment leaves the buildable area at station {station:F1} m.",
                    Severity = "hard",
                    Station = Math.Round(station, 2),
                    Requested = minClearanceM > 0 ? minClearanceM : (double?)null,
                };
                // A double.MaxValue point is a PointLocation failure, not a real coordinate.
                if (p.X < 1e29 && p.Y < 1e29)
                {
                    v.X = Math.Round(p.X, 3);
                    v.Y = Math.Round(p.Y, 3);
                }
                list.Add(v);
            }
            if (outsideSamples.Count > cap)
            {
                list.Add(new EngineeringViolation
                {
                    Code = "FINAL_GEOMETRY_OUTSIDE_BUILDABLE",
                    Message = $"... and {outsideSamples.Count - cap} more outside station(s).",
                    Severity = "hard",
                });
            }
            return list;
        }

        /// <summary>
        /// Sub-clearance stations: ground data exists, but the centerline sits closer to the
        /// surface boundary than the requested routing clearance. Warnings for the human gate —
        /// an existing road inside its own narrow survey band cannot widen the band.
        /// </summary>
        private static List<EngineeringViolation> BuildClearanceViolations(
            List<(double Station, Pt2 Point)> samples, double minClearanceM)
        {
            const int cap = 12;
            var list = new List<EngineeringViolation>();
            int i = 0;
            foreach (var (station, p) in samples)
            {
                if (i++ >= cap) break;
                var v = new EngineeringViolation
                {
                    Code = "CLEARANCE_SHORTFALL",
                    Message = $"Centerline closer than {minClearanceM:F1} m to the surface boundary at station {station:F1} m.",
                    Severity = "warning",
                    Station = Math.Round(station, 2),
                    Requested = minClearanceM,
                };
                if (p.X < 1e29 && p.Y < 1e29)
                {
                    v.X = Math.Round(p.X, 3);
                    v.Y = Math.Round(p.Y, 3);
                }
                list.Add(v);
            }
            if (samples.Count > cap)
            {
                list.Add(new EngineeringViolation
                {
                    Code = "CLEARANCE_SHORTFALL",
                    Message = $"... and {samples.Count - cap} more sub-clearance station(s).",
                    Severity = "warning",
                });
            }
            return list;
        }

        /// <summary>
        /// P0-03: maps CurveAttacher per-PI deviations to structured
        /// <see cref="EngineeringViolation"/>s. severity="hard" for the strict-mode rejection,
        /// "warning" for a relaxed, approval-requiring candidate.
        /// </summary>
        private static List<EngineeringViolation> BuildCurveViolations(
            CurveAttacher.AttachResult attached, double radius, double spiralLengthM, string severity)
        {
            var list = new List<EngineeringViolation>();
            foreach (var w in attached.Warnings)
            {
                string code = w.PlacedAs == "none"
                    ? "SHARP_PI_NO_CURVE"
                    : (w.PlacedAs == "Arc" ? "SCS_DOWNGRADED_TO_ARC" : "RADIUS_OR_SPIRAL_RELAXED");
                list.Add(new EngineeringViolation
                {
                    Code = code,
                    Message = w.Reason,
                    Severity = severity,
                    Index = w.PiIndex,
                    Requested = Math.Round(w.RequestedM, 1),
                    Achieved = Math.Round(w.AchievedM, 1),
                });
            }
            return list;
        }

        private static string BuildHebrewMessage(string name, double length, int piCount, int scsCount, int arcCount, int warnCount,
                                                 GridRouter.EndpointSnap? startSnap, GridRouter.EndpointSnap? endSnap,
                                                 bool contained, int outsideCount, double minClearanceM = 0.0,
                                                 string pathSource = "densified")
        {
            string msg = $"ציר '{name}' צויר באורך {length:F0} מ' עם {piCount} PI";
            if (scsCount > 0 && arcCount > 0)
                msg += $" ({scsCount} ספירלות, {arcCount} עקומות).";
            else if (scsCount > 0)
                msg += $" ({scsCount} ספירלות).";
            else if (arcCount > 0)
                msg += $" ({arcCount} עקומות).";
            else
                msg += ".";
            if (pathSource == "tangent_arc_fit")
                msg += " הגאומטריה נבנתה כישרים ארוכים עם עקומה אחת בכל שינוי כיוון אמיתי.";
            else
                msg += " התוואי לא אפשר התאמת ישרים (הגאומטריה עוקבת אחרי המסדרון) — ייתכנו עקומות רבות.";
            if (startSnap is { Snapped: true })
                msg += $" נקודת ההתחלה הוצמדה למשטח ({startSnap.DistanceM:F1} מ' מהנקודה שנבחרה).";
            if (endSnap is { Snapped: true })
                msg += $" נקודת הסיום הוצמדה למשטח ({endSnap.DistanceM:F1} מ' מהנקודה שנבחרה).";
            if (!contained)
            {
                if (minClearanceM > 0)
                    msg += $" אזהרה: התוואי אינו עומד בדרישת המרווח/הרוחב המינימלי ({minClearanceM:F1} מ' מגבול המשטח) " +
                           $"ב-{outsideCount} נקודות דגימה — אין מקום לחתך הכביש המלא. ראה outside_stations.";
                else
                    msg += $" אזהרה: הגאומטריה הסופית חורגת מהמשטח ב-{outsideCount} נקודות דגימה — ראה outside_stations.";
            }
            if (warnCount > 0)
                msg += $" {warnCount} אזהרות — ראה רשימה.";
            return msg;
        }
    }
}
