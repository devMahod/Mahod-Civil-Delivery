using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates cross-section view blocks at regular intervals along a corridor.
    ///
    /// Based on Igor's CreateMultipleSectionView.cs approach:
    /// - Does NOT use native Civil 3D SectionView API (broken — no AddSectionSource)
    /// - Instead draws custom blocks with polylines showing EG + corridor shape
    /// - Uses TinSurface.FindElevationAtXY() for ground profile
    /// - Uses AppliedAssembly.Links for corridor shape
    /// </summary>
    public class CreateSectionViewsTool : DrawingToolBase
    {
        public override string Name => "create_section_views";
        public override string Description =>
            "Creates cross-section view blocks showing road cross-section at each station. " +
            "Shows existing ground profile and corridor shape (lanes, shoulders, slopes). " +
            "Draws custom blocks arranged in a grid layout.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the alignment""
                },
                ""corridor_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the corridor (default: auto-detect)""
                },
                ""surface_name"": {
                    ""type"": ""string"",
                    ""description"": ""EG surface name (default: auto-detect)""
                },
                ""interval"": {
                    ""type"": ""number"",
                    ""description"": ""Station interval in meters (default: 50)""
                },
                ""left_width"": {
                    ""type"": ""number"",
                    ""description"": ""Left extent in meters (default: 25)""
                },
                ""right_width"": {
                    ""type"": ""number"",
                    ""description"": ""Right extent in meters (default: 25)""
                },
                ""columns"": {
                    ""type"": ""integer"",
                    ""description"": ""Number of columns in grid layout (default: 5)""
                },
                ""origin_x"": {
                    ""type"": ""number"",
                    ""description"": ""Optional X-coordinate for the section-views grid origin. If both origin_x and origin_y are supplied, overrides auto-placement.""
                },
                ""origin_y"": {
                    ""type"": ""number"",
                    ""description"": ""Optional Y-coordinate for the section-views grid origin. If both origin_x and origin_y are supplied, overrides auto-placement.""
                },
                ""vertical_exaggeration"": {
                    ""type"": ""number"",
                    ""description"": ""Vertical exaggeration factor (default: 5). 1=no exaggeration, 5=5x vertical stretch. Makes elevation differences easier to see.""
                }
            },
            ""required"": [""alignment_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        // ── Constants ──
        private const double SAMPLE_STEP = 0.5;     // Surface sampling interval (meters)
        private const double GRID_ELEV_STEP = 2.0;  // Elevation grid lines every 2m
        private const double GRID_OFFS_STEP = 5.0;  // Offset grid lines every 5m
        private const double TITLE_HEIGHT = 1.5;     // Title text height
        private const double BAND_HEIGHT = 5.0;      // Data band height below section
        private const double GAP_X = 15.0;           // Horizontal gap between blocks
        private const double GAP_Y = 10.0;           // Vertical gap between blocks
        // Existing ground = the firm's cross-section convention: layer "HW-CS-EX",
        // color 40 (olive), DASHED — mirrors MahodCivilNet's csLayerExist = "HW-CS-EX".
        private const string LAYER_EG_EXIST = "HW-CS-EX";
        private const short COLOR_EG = 40;           // Color-40 olive — existing-ground convention
        private const short COLOR_CORR = 1;          // Red — corridor
        private const short COLOR_CL = 2;            // Yellow — centerline
        private const short COLOR_GRID = 8;          // Gray — grid
        private const short COLOR_TEXT = 7;           // White — text

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var corridorName = GetStringParam(parameters, "corridor_name");
            var surfaceName = GetStringParam(parameters, "surface_name");
            var interval = GetDoubleParam(parameters, "interval") ?? 50.0;
            var leftWidth = GetDoubleParam(parameters, "left_width") ?? 25.0;
            var rightWidth = GetDoubleParam(parameters, "right_width") ?? 25.0;
            var columns = GetIntParam(parameters, "columns") ?? 5;
            var originXOverride = GetDoubleParam(parameters, "origin_x");
            var originYOverride = GetDoubleParam(parameters, "origin_y");
            var vertCoef = GetDoubleParam(parameters, "vertical_exaggeration") ?? 1.0;
            if (vertCoef < 1.0) vertCoef = 1.0;
            if (vertCoef > 20.0) vertCoef = 20.0;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // ── Find alignment ──
            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);
            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to read alignment");

            // ── Find corridor ──
            CivilDb.Corridor? corridor = null;
            CivilDb.Profile? fgProfile = null;
            foreach (ObjectId cId in civilDoc.CorridorCollection)
            {
                var c = tr.GetObject(cId, OpenMode.ForRead) as CivilDb.Corridor;
                if (c == null) continue;
                if (!string.IsNullOrEmpty(corridorName))
                {
                    if (c.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                    { corridor = c; break; }
                }
                else
                {
                    // Auto-detect: find corridor that uses this alignment
                    foreach (CivilDb.Baseline bl in c.Baselines)
                    {
                        if (bl.AlignmentId == alignmentId.Value)
                        { corridor = c; break; }
                    }
                    if (corridor != null) break;
                }
            }

            if (corridor == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"No corridor found for alignment '{alignmentName}'. Create a corridor first.");

            // Rebuild corridor to ensure targets (daylight slopes) are applied
            try
            {
                var corrWrite = tr.GetObject(corridor.ObjectId, OpenMode.ForWrite) as CivilDb.Corridor;
                corrWrite?.Rebuild();
            }
            catch { }

            // Get FG profile from corridor baseline
            if (corridor.Baselines.Count > 0)
            {
                try
                {
                    var profileId = corridor.Baselines[0].ProfileId;
                    fgProfile = tr.GetObject(profileId, OpenMode.ForRead) as CivilDb.Profile;
                }
                catch { }
            }

            // ── Find EG surface ──
            CivilDb.TinSurface? egSurface = null;
            foreach (ObjectId sId in civilDoc.GetSurfaceIds())
            {
                var s = tr.GetObject(sId, OpenMode.ForRead) as CivilDb.TinSurface;
                if (s == null) continue;
                if (!string.IsNullOrEmpty(surfaceName))
                {
                    if (s.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                    { egSurface = s; break; }
                }
                else
                {
                    // Auto-detect: first TIN surface that's not a corridor surface
                    if (!s.Name.Contains("_Top") && !s.Name.Contains("_Bot") &&
                        !s.Name.Contains("Corridor"))
                    { egSurface = s; break; }
                }
            }

            if (egSurface == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "No EG surface found. Specify surface_name parameter.");

            // ── Build station list ──
            // Clamp last station to endSt - 0.1 m so the sample line's direction
            // is derived from the last real tangent segment, not from the
            // alignment endpoint (where the tangent is extrapolated). Without
            // this, the final cross-section can flare dramatically.
            double startSt = alignment.StartingStation;
            double endSt = alignment.EndingStation;
            double clampedEnd = endSt > startSt + 0.1 ? endSt - 0.1 : endSt;
            var stations = new List<double>();
            for (double st = startSt; st <= clampedEnd; st += interval)
                stations.Add(st);
            // Add clamped end station only if far enough from previous (>half interval)
            if (stations.Count == 0 || (clampedEnd - stations.Last()) > interval * 0.5)
                stations.Add(clampedEnd);

            // Tolerance for matching section-view stations to AppliedAssemblies.
            // Scales with interval: small interval → tight tolerance (avoid
            // false matches), large interval → generous tolerance (cover corridor
            // frequency gaps). Capped at 12 m (the previous hardcoded value).
            double aaTolerance = Math.Clamp(interval * 0.3, 3.0, 12.0);

            // ── Build AppliedAssembly lookup ──
            var aaMap = BuildAppliedAssemblyMap(corridor);

            // Log corridor station range
            if (aaMap.Count > 0)
            {
                double aaMin = aaMap.Keys.Min();
                double aaMax = aaMap.Keys.Max();
                var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                    .DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage($"\n[MahodAI] Corridor data range: STA {aaMin:F0} - {aaMax:F0} " +
                    $"({aaMap.Count} assemblies). Alignment: STA {startSt:F0} - {endSt:F0}\n");
            }

            // ── Ensure layers ──
            var db = HostApplicationServices.WorkingDatabase;
            EnsureLayer(tr, db, "MAHOD-SV", COLOR_TEXT);
            // Existing ground = firm layer "HW-CS-EX" (color 40, dashed) — mirrors MahodCivilNet's
            // csLayerExist. If the firm template already defines it, EnsureLayer keeps it; otherwise
            // it's created color-40 + dash2 (with a self-defined dash fallback if dash2 is absent).
            // The line itself is rendered as explicit dash segments (see DrawExistingGroundDashed)
            // so a dashed existing-ground reads correctly regardless of LTSCALE / block linetype.
            EnsureLayer(tr, db, LAYER_EG_EXIST, COLOR_EG, "dash2");
            EnsureLayer(tr, db, "MAHOD-SV-CORR", COLOR_CORR);
            EnsureLayer(tr, db, "MAHOD-SV-CL", COLOR_CL);
            EnsureLayer(tr, db, "MAHOD-SV-GRID", COLOR_GRID);
            EnsureLayer(tr, db, "MAHOD-SV-TEXT", COLOR_TEXT);

            // ── Get model space ──
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tr.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

            // ── Determine origin point: caller override, or right of THIS alignment's Profile View ──
            double originX = 0, originY = 0;
            if (originXOverride.HasValue && originYOverride.HasValue)
            {
                originX = originXOverride.Value;
                originY = originYOverride.Value;
            }
            else
            {
                try
                {
                    CivilDb.ProfileView? bestPV = null;
                    foreach (ObjectId entId in ms!)
                    {
                        try
                        {
                            var ent = tr.GetObject(entId, OpenMode.ForRead);
                            if (ent is CivilDb.ProfileView pv)
                            {
                                // Prefer ProfileView belonging to this alignment
                                if (pv.AlignmentId == alignmentId.Value)
                                {
                                    bestPV = pv;
                                    break;
                                }
                                // Fallback: use last PV found
                                if (bestPV == null) bestPV = pv;
                            }
                        }
                        catch { }
                    }

                    if (bestPV != null)
                    {
                        var pvExt = bestPV.GeometricExtents;
                        originX = pvExt.MaxPoint.X + 100.0;
                        originY = pvExt.MaxPoint.Y;
                    }
                    else
                    {
                        originX = db.Extmin.X;
                        originY = db.Extmin.Y - 50.0;
                    }
                }
                catch { originY = -500; }
            }

            // ── Clean up existing section blocks for this alignment ──
            try
            {
                string blockPrefix = $"MahodSV_{alignmentName}_";
                var btCleanup = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var msCleanup = tr.GetObject(btCleanup![BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
                var toErase = new List<ObjectId>();

                foreach (ObjectId entId in msCleanup!)
                {
                    try
                    {
                        var ent = tr.GetObject(entId, OpenMode.ForRead);
                        if (ent is BlockReference blkRef)
                        {
                            var btrCheck = tr.GetObject(blkRef.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                            if (btrCheck != null && btrCheck.Name.StartsWith(blockPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                toErase.Add(entId);
                            }
                        }
                    }
                    catch { }
                }

                if (toErase.Count > 0)
                {
                    foreach (var id in toErase)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite);
                        ent.Erase();
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] Cleaned up {toErase.Count} existing section view blocks for '{alignmentName}'");
                }
            }
            catch (Exception cleanupEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] Section view cleanup failed (non-critical): {cleanupEx.Message}");
            }

            // ── Pass 1: Pre-calculate max elevation range across all stations ──
            double globalMaxElevRange = 20.0; // default minimum
            foreach (var staPre in stations)
            {
                try
                {
                    var egL = SampleSurface(alignment, egSurface, staPre, -leftWidth, -1.0);
                    var egR = SampleSurface(alignment, egSurface, staPre, rightWidth, 1.0);
                    var (cL, cR) = GetCorridorTopProfile(aaMap, fgProfile, staPre, aaTolerance);
                    var allPts = new List<(double offs, double elev)>();
                    allPts.AddRange(egL); allPts.AddRange(egR);
                    allPts.AddRange(cL); allPts.AddRange(cR);
                    if (allPts.Count > 0)
                    {
                        double eMin = allPts.Min(p => p.elev);
                        double eMax = allPts.Max(p => p.elev);
                        eMin = Math.Floor(eMin / GRID_ELEV_STEP) * GRID_ELEV_STEP - GRID_ELEV_STEP;
                        eMax = Math.Ceiling(eMax / GRID_ELEV_STEP) * GRID_ELEV_STEP + GRID_ELEV_STEP;
                        double range = eMax - eMin;
                        if (range > globalMaxElevRange) globalMaxElevRange = range;
                    }
                }
                catch { }
            }
            // Block height for layout spacing (uses global max to avoid overlap)
            double fixedBlockHeight = globalMaxElevRange * vertCoef + BAND_HEIGHT + TITLE_HEIGHT;

            // ── Pass 2: Create section blocks ──
            int created = 0;
            double maxBlockWidth = leftWidth + rightWidth + GAP_X + 10; // extra for labels
            double lastGoodProfileElev = 0; // fallback for stations beyond FG profile extent

            foreach (var station in stations)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Sample EG surface
                    var egLeft = SampleSurface(alignment, egSurface, station, -leftWidth, -1.0);
                    var egRight = SampleSurface(alignment, egSurface, station, rightWidth, 1.0);

                    // Get corridor Top surface profile — Igor's approach
                    var (corrLeft, corrRight) = GetCorridorTopProfile(aaMap, fgProfile, station, aaTolerance);

                    // Skip stations with no corridor data — don't create empty blocks
                    if (corrLeft.Count == 0 && corrRight.Count == 0)
                    {
                        // Log what codes exist at this station for diagnostics
                        var aaDbg = FindAppliedAssembly(aaMap, station, aaTolerance);
                        if (aaDbg != null)
                        {
                            var codes = new HashSet<string>();
                            foreach (CivilDb.CalculatedLink cl in aaDbg.Links)
                                foreach (string c in cl.CorridorCodes) codes.Add(c);
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] STA {station:F0}: 0 Top links. Available codes: {string.Join(", ", codes)}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] STA {station:F0}: no AppliedAssembly found");
                        }
                        continue;
                    }

                    // Calculate elevation range per station (like Igor)
                    var allPoints = new List<(double offs, double elev)>();
                    allPoints.AddRange(egLeft); allPoints.AddRange(egRight);
                    allPoints.AddRange(corrLeft); allPoints.AddRange(corrRight);
                    if (allPoints.Count == 0) continue;

                    double elevMin = allPoints.Min(p => p.elev);
                    double elevMax = allPoints.Max(p => p.elev);
                    // Snap to grid + 1 step padding
                    elevMin = Math.Floor(elevMin / GRID_ELEV_STEP) * GRID_ELEV_STEP - GRID_ELEV_STEP;
                    elevMax = Math.Ceiling(elevMax / GRID_ELEV_STEP) * GRID_ELEV_STEP + GRID_ELEV_STEP;
                    // Ensure minimum height of 10m real range
                    if (elevMax - elevMin < 10.0)
                    {
                        double mid = (elevMin + elevMax) / 2;
                        elevMin = mid - 5.0;
                        elevMax = mid + 5.0;
                    }
                    double height = (elevMax - elevMin) * vertCoef;

                    // Create block
                    string blockName = $"MahodSV_{alignmentName}_{station:F0}_{created}";
                    var btr = new BlockTableRecord { Name = blockName };
                    var btWrite = tr.GetObject(db.BlockTableId, OpenMode.ForWrite) as BlockTable;
                    btWrite!.Add(btr);
                    tr.AddNewlyCreatedDBObject(btr, true);

                    // Section view box bounds in drawing coordinates. Everything
                    // drawn inside the section block is clipped to this rectangle
                    // so it can't bleed into the neighbouring block in the grid.
                    double boxXMin = -leftWidth;
                    double boxXMax = rightWidth;
                    double boxYMin = 0;
                    double boxYMax = height;

                    // Draw grid with elevation labels (vertCoef applied inside)
                    DrawGrid(tr, btr, boxXMin, boxXMax, boxYMin, boxYMax, elevMin, vertCoef);

                    // Draw EG surface (color-40, DASHED — firm existing-ground convention) clipped
                    // to box. Rendered as explicit dash segments so it always reads dashed.
                    DrawExistingGroundDashed(tr, btr, LAYER_EG_EXIST, egLeft, elevMin, vertCoef,
                        boxXMin, boxXMax, boxYMin, boxYMax);
                    DrawExistingGroundDashed(tr, btr, LAYER_EG_EXIST, egRight, elevMin, vertCoef,
                        boxXMin, boxXMax, boxYMin, boxYMax);

                    // Draw corridor Top surface (red) — clipped to box.
                    DrawPolyline(tr, btr, "MAHOD-SV-CORR", corrLeft, elevMin, vertCoef,
                        boxXMin, boxXMax, boxYMin, boxYMax);
                    DrawPolyline(tr, btr, "MAHOD-SV-CORR", corrRight, elevMin, vertCoef,
                        boxXMin, boxXMax, boxYMin, boxYMax);

                    // Draw daylight lines — connect outermost corridor points to
                    // EG surface, clipped to box (extends to box edge if no EG
                    // intersection in range).
                    DrawDaylightLine(tr, btr, corrLeft, egLeft, elevMin, vertCoef,
                        boxXMin, boxXMax, boxYMin, boxYMax);
                    DrawDaylightLine(tr, btr, corrRight, egRight, elevMin, vertCoef,
                        boxXMin, boxXMax, boxYMin, boxYMax);

                    // Draw centerline
                    DrawLine(tr, btr, "MAHOD-SV-CL", 0, 0, 0, height);

                    // Get KEY assembly points by codes (not all link endpoints)
                    var aa = FindAppliedAssembly(aaMap, station, aaTolerance);
                    double profileElev = 0;
                    try
                    {
                        if (fgProfile != null)
                        {
                            double querySt = station;
                            if (querySt >= fgProfile.EndingStation)
                                querySt = fgProfile.EndingStation - 0.01;
                            if (querySt <= fgProfile.StartingStation)
                                querySt = fgProfile.StartingStation + 0.01;
                            profileElev = fgProfile.ElevationAt(querySt);
                        }
                    }
                    catch { }
                    // Fallback: if profile doesn't cover this station, use last known elevation
                    if (profileElev == 0 && station > 0 && lastGoodProfileElev > 0)
                        profileElev = lastGoodProfileElev;
                    if (profileElev > 0) lastGoodProfileElev = profileElev;
                    var keyPts = GetKeyAssemblyPoints(aa, profileElev);

                    // Filter labels + band data to points within the section view
                    // box. A point at offset > rightWidth would draw a tick mark
                    // and rotated label outside the box, into the neighbouring
                    // block's space.
                    var keyPtsInBox = keyPts
                        .Where(p => p.offs >= boxXMin - 1e-6 && p.offs <= boxXMax + 1e-6)
                        .ToList();

                    // Draw labels at key points only
                    DrawCorridorLabels(tr, btr, keyPtsInBox, elevMin, height, vertCoef);

                    // Draw title (include scale info when exaggerated)
                    DrawTitle(tr, btr, station, boxXMin, boxXMax, height, vertCoef);

                    // Draw elevation/offset bands with key point data
                    DrawBands(tr, btr, boxXMin, boxXMax, elevMin, elevMax, keyPtsInBox);

                    // Insert block into model space — row-major layout (left to right, then next row)
                    int col = created % columns;
                    int row = created / columns;

                    double totalBlockHeight = fixedBlockHeight + GAP_Y;
                    double totalBlockWidth = maxBlockWidth;

                    double insX = originX + col * totalBlockWidth;
                    double insY = originY - row * totalBlockHeight;

                    var blkRef = new BlockReference(new Point3d(insX, insY, 0), btr.ObjectId);
                    blkRef.Layer = "MAHOD-SV";
                    ms!.AppendEntity(blkRef);
                    tr.AddNewlyCreatedDBObject(blkRef, true);

                    created++;
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] Section view at station {station:F0}: {ex.Message}");
                }
            }

            cache.RemoveByPattern("get_drawing_summary:");

            return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
            {
                ["success"] = true,
                ["alignment_name"] = alignmentName,
                ["corridor_name"] = corridor.Name,
                ["surface_name"] = egSurface.Name,
                ["section_count"] = created,
                ["station_count"] = stations.Count,
                ["interval_m"] = interval,
                ["creation_method"] = "CustomBlocks",
                ["vertical_exaggeration"] = vertCoef,
                ["message"] = $"Created {created} cross-section views for '{alignmentName}' " +
                    $"(corridor: {corridor.Name}, surface: {egSurface.Name}). " +
                    $"Interval: {interval}m, width: {leftWidth}+{rightWidth}m. " +
                    $"Vertical exaggeration: {vertCoef:F0}x."
            }));
        }

        // ══════════════════════════════════════════════════════════
        // Data extraction methods
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Sample EG surface elevations along a line perpendicular to alignment at station.
        /// Returns list of (offset, elevation) pairs.
        /// </summary>
        private static List<(double offs, double elev)> SampleSurface(
            CivilDb.Alignment alignment, CivilDb.TinSurface surface,
            double station, double maxOffset, double side)
        {
            var result = new List<(double, double)>();
            double absMax = Math.Abs(maxOffset);

            for (double d = 0; d <= absMax; d += SAMPLE_STEP)
            {
                double offset = side * d;
                double x = 0, y = 0;
                try
                {
                    alignment.PointLocation(station, offset, ref x, ref y);
                    double elev = surface.FindElevationAtXY(x, y);
                    result.Add((offset, elev));
                }
                catch { /* Point outside surface */ }
            }
            return result;
        }

        /// <summary>
        /// Build a lookup: station → AppliedAssembly from corridor.
        /// </summary>
        private static Dictionary<double, CivilDb.AppliedAssembly> BuildAppliedAssemblyMap(
            CivilDb.Corridor corridor)
        {
            var map = new Dictionary<double, CivilDb.AppliedAssembly>();
            try
            {
                foreach (CivilDb.Baseline bl in corridor.Baselines)
                {
                    foreach (CivilDb.BaselineRegion region in bl.BaselineRegions)
                    {
                        foreach (CivilDb.AppliedAssembly aa in region.AppliedAssemblies)
                        {
                            if (aa.Points.Count > 0)
                            {
                                double st = aa.Points[0].StationOffsetElevationToBaseline.X;
                                st = Math.Round(st, 2);
                                if (!map.ContainsKey(st))
                                    map[st] = aa;
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] BuildAppliedAssemblyMap: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Get KEY structural points from an AppliedAssembly.
        /// Uses CorridorCodes to identify meaningful points (Crown, ETW, Daylight, etc.)
        /// instead of all link endpoints. Results in ~6-12 labeled points, not 20+.
        /// </summary>
        private static List<(double offs, double elev)> GetKeyAssemblyPoints(
            CivilDb.AppliedAssembly? aa, double profileElev)
        {
            var result = new List<(double offs, double elev)>();
            if (aa == null) return result;

            // Always add centerline (offset=0)
            result.Add((0, profileElev));

            try
            {
                // Collect ALL unique points with their codes
                var pointsByOffset = new SortedDictionary<double, (double elev, string code)>();

                foreach (CivilDb.CalculatedPoint cp in aa.Points)
                {
                    var soe = cp.StationOffsetElevationToBaseline;
                    double offs = Math.Round(soe.Y, 3);
                    double elev = soe.Z + profileElev;

                    // Get best code for this point
                    string bestCode = "";
                    foreach (string code in cp.CorridorCodes)
                    {
                        bestCode = code;
                        break;
                    }

                    // Skip Datum/SubBase internal points
                    if (bestCode == "Datum" || bestCode == "SubBase") continue;

                    // Keep point with highest priority code at each offset
                    if (!pointsByOffset.ContainsKey(offs))
                        pointsByOffset[offs] = (elev, bestCode);
                }

                // Filter to key structural points — minimum 1m apart
                double lastOffset = double.MinValue;
                foreach (var kvp in pointsByOffset)
                {
                    double offs = kvp.Key;
                    double elev = kvp.Value.elev;

                    // Always include first/last points and points >= 1m from previous
                    if (Math.Abs(offs - lastOffset) >= 1.0 || offs == pointsByOffset.Keys.First() || offs == pointsByOffset.Keys.Last())
                    {
                        // Don't duplicate centerline
                        if (Math.Abs(offs) > 0.01)
                        {
                            result.Add((offs, elev));
                            lastOffset = offs;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] GetKeyAssemblyPoints: {ex.Message}");
            }

            result.Sort((a, b) => a.offs.CompareTo(b.offs));
            return result;
        }

        /// <summary>
        /// Find nearest AppliedAssembly for a given station.
        /// Tolerance must cover half the corridor frequency (e.g. 10m for 20m
        /// frequency). Callers pass a frequency-scaled tolerance so the window
        /// doesn't mask end-of-alignment gaps when interval is small.
        /// </summary>
        private static CivilDb.AppliedAssembly? FindAppliedAssembly(
            Dictionary<double, CivilDb.AppliedAssembly> aaMap, double station,
            double tolerance = 12.0)
        {
            double stRound = Math.Round(station, 2);
            if (aaMap.TryGetValue(stRound, out var aa))
                return aa;

            double bestDist = double.MaxValue;
            CivilDb.AppliedAssembly? best = null;
            foreach (var kvp in aaMap)
            {
                double dist = Math.Abs(kvp.Key - station);
                if (dist < bestDist && dist < tolerance)
                { bestDist = dist; best = kvp.Value; }
            }
            return best;
        }

        /// <summary>
        /// Get corridor TOP surface profile at a station — Igor's approach.
        /// Filters links by code "Top", collects points, sorts by offset,
        /// returns two ordered polylines: left side and right side.
        /// </summary>
        private static (List<(double offs, double elev)> left, List<(double offs, double elev)> right)
            GetCorridorTopProfile(
                Dictionary<double, CivilDb.AppliedAssembly> aaMap,
                CivilDb.Profile? fgProfile, double station,
                double tolerance = 12.0)
        {
            var left = new List<(double offs, double elev)>();
            var right = new List<(double offs, double elev)>();

            var aa = FindAppliedAssembly(aaMap, station, tolerance);
            if (aa == null) return (left, right);

            // Get baseline profile elevation (same as Igor: pf.ElevationAt(st))
            double profileElev = 0;
            try
            {
                if (fgProfile != null)
                {
                    // Clamp station slightly inside profile range to avoid edge exceptions
                    double querySt = station;
                    if (querySt >= fgProfile.EndingStation)
                        querySt = fgProfile.EndingStation - 0.01;
                    if (querySt <= fgProfile.StartingStation)
                        querySt = fgProfile.StartingStation + 0.01;
                    profileElev = fgProfile.ElevationAt(querySt);
                }
            }
            catch { }

            // Collect raw link segments with code "Top" — Igor's getLinkSample pattern
            var rawLinks = new List<(double x1, double y1, double x2, double y2)>();
            try
            {
                foreach (CivilDb.CalculatedLink cl in aa.Links)
                {
                    if (cl.CalculatedPoints.Count < 2) continue;

                    // Only links with code "Top" (Igor: if (c == code) where code="Top")
                    bool isTop = false;
                    foreach (string code in cl.CorridorCodes)
                    {
                        if (code == "Top") { isTop = true; break; }
                    }
                    if (!isTop) continue;

                    var cp1 = cl.CalculatedPoints[0];
                    var cp2 = cl.CalculatedPoints[1];
                    double offs1 = cp1.StationOffsetElevationToBaseline.Y;
                    double elev1 = cp1.StationOffsetElevationToBaseline.Z + profileElev;
                    double offs2 = cp2.StationOffsetElevationToBaseline.Y;
                    double elev2 = cp2.StationOffsetElevationToBaseline.Z + profileElev;

                    rawLinks.Add((offs1, elev1, offs2, elev2));
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] GetCorridorTopProfile at {station:F0}: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] STA {station:F0}: {rawLinks.Count} Top links, profileElev={profileElev:F2}");

            if (rawLinks.Count == 0) return (left, right);

            // Collect all unique points and separate by side
            // Igor: removeOtherSide(lsRaw, bRight) — separates left/right by offset sign
            var leftPoints = new SortedDictionary<double, double>();   // offset → elev (ascending offset)
            var rightPoints = new SortedDictionary<double, double>();  // offset → elev (ascending offset)

            foreach (var seg in rawLinks)
            {
                // Add both endpoints
                AddPointToSide(leftPoints, rightPoints, seg.x1, seg.y1);
                AddPointToSide(leftPoints, rightPoints, seg.x2, seg.y2);
            }

            // Ensure both sides share the centerline point so polylines connect
            // Left side ends at offset 0, right side starts at offset 0
            if (leftPoints.Count > 0 && !rightPoints.ContainsKey(0))
            {
                // Find the point closest to 0 on the left side
                double lastLeftOff = 0;
                double lastLeftElev = profileElev;
                foreach (var kvp in leftPoints)
                { lastLeftOff = kvp.Key; lastLeftElev = kvp.Value; }
                if (lastLeftOff <= 0.001)
                    rightPoints[0] = lastLeftElev;
            }
            if (rightPoints.Count > 0 && !leftPoints.ContainsKey(0))
            {
                // Find the point closest to 0 on the right side
                foreach (var kvp in rightPoints)
                {
                    if (kvp.Key >= -0.001)
                    { leftPoints[0] = kvp.Value; break; }
                }
            }

            // Build ordered polylines
            foreach (var kvp in leftPoints)
                left.Add((kvp.Key, kvp.Value));
            foreach (var kvp in rightPoints)
                right.Add((kvp.Key, kvp.Value));

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] STA {station:F0}: left={left.Count} pts, right={right.Count} pts");

            return (left, right);
        }

        private static void AddPointToSide(
            SortedDictionary<double, double> leftPts,
            SortedDictionary<double, double> rightPts,
            double offset, double elev)
        {
            var target = offset <= 0.001 ? leftPts : rightPts;
            double roundedOff = Math.Round(offset, 3);
            // If duplicate offset, keep higher elevation (Top surface)
            if (!target.ContainsKey(roundedOff) || elev > target[roundedOff])
                target[roundedOff] = elev;
        }

        // ══════════════════════════════════════════════════════════
        // Drawing methods
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Draw daylight (slope) line from outermost corridor point to where it meets EG surface.
        /// Walks outward along EG points to find the intersection between a standard slope
        /// (1:1.5 cut, 1:2 fill) and the EG surface. Clips at the section view's box bounds
        /// so a daylight that extends past the visible offset doesn't bleed into adjacent
        /// section view blocks.
        /// </summary>
        private static void DrawDaylightLine(Transaction tr, BlockTableRecord btr,
            List<(double offs, double elev)> corrPts, List<(double offs, double elev)> egPts,
            double elevMin, double vertCoef,
            double xMin = double.NegativeInfinity, double xMax = double.PositiveInfinity,
            double yMin = double.NegativeInfinity, double yMax = double.PositiveInfinity)
        {
            if (corrPts.Count == 0 || egPts.Count < 2) return;

            // Find outermost corridor point — the one furthest from the centerline.
            // corrLeft is sorted ascending (-10, -5, ..., 0), so corrPts[Count-1]
            // would land on the centerline at offset 0 instead of the leftmost
            // point. Pick by max |offset| so this works for both sides.
            var outerCorr = corrPts[0];
            double maxAbs = Math.Abs(outerCorr.offs);
            for (int i = 1; i < corrPts.Count; i++)
            {
                if (Math.Abs(corrPts[i].offs) > maxAbs)
                {
                    outerCorr = corrPts[i];
                    maxAbs = Math.Abs(outerCorr.offs);
                }
            }
            double corrOff = outerCorr.offs;
            double corrElev = outerCorr.elev;
            double sign = corrOff >= 0 ? 1.0 : -1.0; // direction away from center

            // Find EG elevation at corridor edge
            double egAtEdge = InterpolateEG(egPts, corrOff);
            if (double.IsNaN(egAtEdge)) return;

            double elevDiff = corrElev - egAtEdge;
            if (Math.Abs(elevDiff) < 0.3) return; // no significant cut/fill

            // Israeli standard slopes: fill 1:2 (gentler, dE/dX = 0.5),
            // cut 1:1.5 (steeper, dE/dX = 0.667).
            // FILL: corrElev > egAtEdge → slope goes DOWN-and-OUT to meet EG below.
            // CUT:  corrElev < egAtEdge → slope goes UP-and-OUT to meet EG above.
            // dirSign expresses elevation direction: -1 down for FILL, +1 up for CUT.
            bool isFill = elevDiff > 0;
            double slopeGrade = isFill ? 0.5 : 0.667;
            double dirSign = isFill ? -1.0 : 1.0;

            // Walk along EG points outward from corridor edge to find intersection
            double daylightOff = double.NaN;
            double daylightElev = double.NaN;

            for (int i = 0; i < egPts.Count - 1; i++)
            {
                var p1 = egPts[i];
                var p2 = egPts[i + 1];

                // Only look at EG segments BEYOND the corridor edge (outward)
                if (sign > 0 && p2.offs <= corrOff) continue;
                if (sign < 0 && p1.offs >= corrOff) continue;

                // Slope elevation at p1 and p2 offsets — sign matches cut vs fill.
                double slopeAtP1 = corrElev + dirSign * slopeGrade * Math.Abs(p1.offs - corrOff);
                double slopeAtP2 = corrElev + dirSign * slopeGrade * Math.Abs(p2.offs - corrOff);

                // Check if slope line crosses EG segment
                double diff1 = slopeAtP1 - p1.elev;
                double diff2 = slopeAtP2 - p2.elev;

                if (diff1 * diff2 <= 0) // sign change = intersection
                {
                    // Linear interpolation to find intersection point
                    double t = Math.Abs(diff1) / (Math.Abs(diff1) + Math.Abs(diff2) + 0.001);
                    daylightOff = p1.offs + t * (p2.offs - p1.offs);
                    daylightElev = p1.elev + t * (p2.elev - p1.elev);
                    break;
                }
            }

            // No intersection found within sampled EG: extend the slope line to the
            // box boundary so the user can SEE the slope direction even if EG dives
            // off the side. Without this fallback, sections in steep cross-slope
            // terrain would have no daylight indicator at all.
            if (double.IsNaN(daylightOff))
            {
                if (double.IsPositiveInfinity(xMax) || double.IsNegativeInfinity(xMin))
                    return;
                daylightOff = sign > 0 ? xMax : xMin;
                daylightElev = corrElev + dirSign * slopeGrade * Math.Abs(daylightOff - corrOff);
            }

            // Convert to drawing coords (Y exaggerated by vertCoef).
            double dx1 = corrOff;
            double dy1 = (corrElev - elevMin) * vertCoef;
            double dx2 = daylightOff;
            double dy2 = (daylightElev - elevMin) * vertCoef;

            // Clip the line to the section view box so it can't bleed into the
            // neighbouring block's territory.
            if (!ClipLineToBox(dx1, dy1, dx2, dy2, xMin, yMin, xMax, yMax,
                out double cx1, out double cy1, out double cx2, out double cy2))
                return;

            DrawLine(tr, btr, "MAHOD-SV-CORR", cx1, cy1, cx2, cy2);
        }

        /// <summary>Interpolate EG elevation at a given offset.</summary>
        private static double InterpolateEG(List<(double offs, double elev)> egPts, double offset)
        {
            for (int i = 0; i < egPts.Count - 1; i++)
            {
                var p1 = egPts[i];
                var p2 = egPts[i + 1];
                if ((offset >= p1.offs && offset <= p2.offs) ||
                    (offset <= p1.offs && offset >= p2.offs))
                {
                    double t = (offset - p1.offs) / (p2.offs - p1.offs);
                    return p1.elev + t * (p2.elev - p1.elev);
                }
            }
            return double.NaN;
        }

        private static void DrawPolyline(Transaction tr, BlockTableRecord btr,
            string layer, List<(double offs, double elev)> points, double elevMin,
            double vertCoef = 1.0,
            double xMin = double.NegativeInfinity, double xMax = double.PositiveInfinity,
            double yMin = double.NegativeInfinity, double yMax = double.PositiveInfinity,
            bool plineGen = false, double linetypeScale = 0.0)
        {
            if (points.Count < 2) return;

            // Convert to drawing coords once (Y is exaggerated by vertCoef).
            var dpts = new List<(double x, double y)>(points.Count);
            for (int i = 0; i < points.Count; i++)
                dpts.Add((points[i].offs, (points[i].elev - elevMin) * vertCoef));

            // Fast path: no clipping requested — draw single polyline as before.
            bool noClip = double.IsNegativeInfinity(xMin) && double.IsPositiveInfinity(xMax)
                && double.IsNegativeInfinity(yMin) && double.IsPositiveInfinity(yMax);
            if (noClip)
            {
                var pl = new Polyline();
                pl.SetDatabaseDefaults();
                pl.Layer = layer;
                pl.Plinegen = plineGen; // continuous linetype pattern across vertices (dashes)
                if (linetypeScale > 0) pl.LinetypeScale = linetypeScale;
                for (int i = 0; i < dpts.Count; i++)
                    pl.AddVertexAt(i, new Point2d(dpts[i].x, dpts[i].y), 0, 0, 0);
                btr.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                return;
            }

            // Clip each segment with Liang-Barsky; emit one polyline per
            // contiguous run that stays inside the box. A segment that exits
            // and re-enters the box is split into two polylines.
            var current = new List<(double x, double y)>();
            void Flush()
            {
                if (current.Count >= 2)
                {
                    var pl = new Polyline();
                    pl.SetDatabaseDefaults();
                    pl.Layer = layer;
                    pl.Plinegen = plineGen; // continuous linetype pattern across vertices (dashes)
                    if (linetypeScale > 0) pl.LinetypeScale = linetypeScale;
                    for (int j = 0; j < current.Count; j++)
                        pl.AddVertexAt(j, new Point2d(current[j].x, current[j].y), 0, 0, 0);
                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                }
                current.Clear();
            }

            for (int i = 0; i < dpts.Count - 1; i++)
            {
                var a = dpts[i];
                var b = dpts[i + 1];
                if (!ClipLineToBox(a.x, a.y, b.x, b.y, xMin, yMin, xMax, yMax,
                    out double cx1, out double cy1, out double cx2, out double cy2))
                {
                    Flush();
                    continue;
                }

                if (current.Count == 0)
                {
                    current.Add((cx1, cy1));
                    current.Add((cx2, cy2));
                }
                else
                {
                    var last = current[current.Count - 1];
                    if (Math.Abs(last.x - cx1) < 1e-6 && Math.Abs(last.y - cy1) < 1e-6)
                    {
                        current.Add((cx2, cy2));
                    }
                    else
                    {
                        // Discontinuity (segment was clipped on entry) — start new run.
                        Flush();
                        current.Add((cx1, cy1));
                        current.Add((cx2, cy2));
                    }
                }
            }
            Flush();
        }

        /// <summary>
        /// Draws an existing-ground line as explicit DASH segments along the surface path.
        /// Civil 3D's longitudinal existing ground is a dashed olive line; the firm's
        /// cross-sections follow the same convention (layer "HW-CS-EX", color 40, dashed).
        /// Drawing the dashes geometrically (rather than via a linetype + LTSCALE trick that
        /// was rendering solid inside the section block) guarantees a visible dashed line at
        /// section scale on any drawing. Each tick is a solid little segment (Linetype
        /// "Continuous") on the dashed layer, so it inherits the layer colour (40) without the
        /// layer's own dash re-breaking the tick. Dashes run with a continuous phase across the
        /// whole polyline and are clipped to the section-view box.
        /// </summary>
        private static void DrawExistingGroundDashed(
            Transaction tr, BlockTableRecord btr, string layer,
            List<(double offs, double elev)> points, double elevMin, double vertCoef,
            double xMin, double xMax, double yMin, double yMax,
            double dashLen = 1.5, double gapLen = 1.0)
        {
            if (points.Count < 2) return;

            // Convert to drawing coords once (Y exaggerated by vertCoef).
            var dpts = new List<(double x, double y)>(points.Count);
            for (int i = 0; i < points.Count; i++)
                dpts.Add((points[i].offs, (points[i].elev - elevMin) * vertCoef));

            double period = dashLen + gapLen;
            if (period <= 1e-6) return;
            double phase = 0.0; // [0,dashLen) = dash, [dashLen,period) = gap

            for (int i = 0; i < dpts.Count - 1; i++)
            {
                double ax = dpts[i].x, ay = dpts[i].y;
                double bx = dpts[i + 1].x, by = dpts[i + 1].y;
                double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (segLen < 1e-9) continue;
                double ux = (bx - ax) / segLen, uy = (by - ay) / segLen;

                double t = 0.0;
                while (t < segLen - 1e-9)
                {
                    double posInPeriod = phase % period;
                    bool inDash = posInPeriod < dashLen;
                    double remain = inDash ? (dashLen - posInPeriod) : (period - posInPeriod);
                    double step = Math.Min(remain, segLen - t);

                    if (inDash && step > 1e-6)
                    {
                        double x1 = ax + ux * t, y1 = ay + uy * t;
                        double x2 = ax + ux * (t + step), y2 = ay + uy * (t + step);
                        if (ClipLineToBox(x1, y1, x2, y2, xMin, yMin, xMax, yMax,
                                out double cx1, out double cy1, out double cx2, out double cy2))
                        {
                            var ln = new Line(new Point3d(cx1, cy1, 0), new Point3d(cx2, cy2, 0));
                            ln.SetDatabaseDefaults();
                            ln.Layer = layer;
                            ln.Linetype = "Continuous"; // each tick solid; layer colour (40) applies
                            btr.AppendEntity(ln);
                            tr.AddNewlyCreatedDBObject(ln, true);
                        }
                    }

                    t += step;
                    phase += step;
                }
            }
        }

        /// <summary>
        /// Liang-Barsky line clipping. Returns true and clipped endpoints if any
        /// part of the line lies inside the box; false if entirely outside.
        /// Box is half-open: [xMin, xMax] × [yMin, yMax].
        /// </summary>
        private static bool ClipLineToBox(
            double x1, double y1, double x2, double y2,
            double xMin, double yMin, double xMax, double yMax,
            out double cx1, out double cy1, out double cx2, out double cy2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x1 - xMin, xMax - x1, y1 - yMin, yMax - y1 };
            double tEnter = 0.0;
            double tExit = 1.0;

            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    // Line parallel to this edge — outside if q is negative.
                    if (q[i] < 0)
                    {
                        cx1 = cy1 = cx2 = cy2 = 0;
                        return false;
                    }
                }
                else
                {
                    double t = q[i] / p[i];
                    if (p[i] < 0)
                    {
                        if (t > tExit) { cx1 = cy1 = cx2 = cy2 = 0; return false; }
                        if (t > tEnter) tEnter = t;
                    }
                    else
                    {
                        if (t < tEnter) { cx1 = cy1 = cx2 = cy2 = 0; return false; }
                        if (t < tExit) tExit = t;
                    }
                }
            }

            cx1 = x1 + tEnter * dx;
            cy1 = y1 + tEnter * dy;
            cx2 = x1 + tExit * dx;
            cy2 = y1 + tExit * dy;
            return true;
        }

        private static void DrawLine(Transaction tr, BlockTableRecord btr,
            string layer, double x1, double y1, double x2, double y2)
        {
            var ln = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            ln.SetDatabaseDefaults();
            ln.Layer = layer;
            btr.AppendEntity(ln);
            tr.AddNewlyCreatedDBObject(ln, true);
        }

        private static void DrawGrid(Transaction tr, BlockTableRecord btr,
            double xMin, double xMax, double yMin, double yMax,
            double elevMin = 0, double vertCoef = 1.0)
        {
            // Horizontal grid lines with elevation labels on left
            // Grid step in real elevation units; Y position scaled by vertCoef
            double elevRange = yMax / vertCoef; // yMax already includes vertCoef
            for (double elev = 0; elev <= elevRange + 0.01; elev += GRID_ELEV_STEP)
            {
                double y = elev * vertCoef;
                var ln = new Line(new Point3d(xMin, y, 0), new Point3d(xMax, y, 0));
                ln.SetDatabaseDefaults();
                ln.Layer = "MAHOD-SV-GRID";
                ln.LinetypeScale = 0.5;
                btr.AppendEntity(ln);
                tr.AddNewlyCreatedDBObject(ln, true);

                // Elevation label on left side — show real elevation value
                double actualElev = elevMin + elev;
                var txtE = new DBText();
                txtE.SetDatabaseDefaults();
                txtE.Layer = "MAHOD-SV-TEXT";
                txtE.TextString = $"{actualElev:F0}";
                txtE.Height = 0.8;
                txtE.Position = new Point3d(xMin - 3.5, y - 0.4, 0);
                btr.AppendEntity(txtE);
                tr.AddNewlyCreatedDBObject(txtE, true);
            }
            // Vertical grid lines
            for (double x = Math.Ceiling(xMin / GRID_OFFS_STEP) * GRID_OFFS_STEP; x <= xMax; x += GRID_OFFS_STEP)
            {
                var ln = new Line(new Point3d(x, yMin, 0), new Point3d(x, yMax, 0));
                ln.SetDatabaseDefaults();
                ln.Layer = "MAHOD-SV-GRID";
                ln.LinetypeScale = 0.5;
                btr.AppendEntity(ln);
                tr.AddNewlyCreatedDBObject(ln, true);
            }
            // Border
            var border = new Polyline();
            border.SetDatabaseDefaults();
            border.Layer = "MAHOD-SV";
            border.AddVertexAt(0, new Point2d(xMin, yMin), 0, 0, 0);
            border.AddVertexAt(1, new Point2d(xMax, yMin), 0, 0, 0);
            border.AddVertexAt(2, new Point2d(xMax, yMax), 0, 0, 0);
            border.AddVertexAt(3, new Point2d(xMin, yMax), 0, 0, 0);
            border.Closed = true;
            btr.AppendEntity(border);
            tr.AddNewlyCreatedDBObject(border, true);
        }

        private static void DrawTitle(Transaction tr, BlockTableRecord btr,
            double station, double xMin, double xMax, double height,
            double vertCoef = 1.0)
        {
            var txt = new DBText();
            txt.SetDatabaseDefaults();
            txt.Layer = "MAHOD-SV-TEXT";
            txt.TextString = $"STA {station:F0}";
            txt.Height = TITLE_HEIGHT;
            txt.Position = new Point3d((xMin + xMax) / 2 - 3, height + 1, 0);
            btr.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);

            // Show vertical exaggeration ratio if != 1
            if (vertCoef > 1.01)
            {
                var txtScale = new DBText();
                txtScale.SetDatabaseDefaults();
                txtScale.Layer = "MAHOD-SV-TEXT";
                txtScale.TextString = $"V.E. 1:{vertCoef:F0}";
                txtScale.Height = TITLE_HEIGHT * 0.5;
                txtScale.Position = new Point3d((xMin + xMax) / 2 - 3, height + 1 + TITLE_HEIGHT + 0.3, 0);
                btr.AppendEntity(txtScale);
                tr.AddNewlyCreatedDBObject(txtScale, true);
            }
        }

        /// <summary>
        /// Draw labels at KEY assembly points — Igor's approach.
        /// Label polyline with ticks, elevation values above, width below, slopes on segments.
        /// </summary>
        private static void DrawCorridorLabels(Transaction tr, BlockTableRecord btr,
            List<(double offs, double elev)> keyPts, double elevMin, double height,
            double vertCoef = 1.0)
        {
            if (keyPts.Count < 2) return;

            double textH = 0.6;
            double rowTop = height - 1.5;  // Label polyline Y position

            // ── Label polyline with tick marks at each key point ──
            var labelPoly = new Polyline();
            labelPoly.SetDatabaseDefaults();
            labelPoly.Layer = "MAHOD-SV-TEXT";
            int vtx = 0;

            for (int i = 0; i < keyPts.Count; i++)
            {
                var pt = keyPts[i];

                // Tick mark on label row
                labelPoly.AddVertexAt(vtx++, new Point2d(pt.offs, rowTop), 0, 0, 0);
                labelPoly.AddVertexAt(vtx++, new Point2d(pt.offs, rowTop + 0.5), 0, 0, 0);
                labelPoly.AddVertexAt(vtx++, new Point2d(pt.offs, rowTop), 0, 0, 0);

                // ── Elevation value (vertical, above tick) ──
                var txtElev = new DBText();
                txtElev.SetDatabaseDefaults();
                txtElev.Layer = "MAHOD-SV-TEXT";
                txtElev.TextString = $"{pt.elev:F2}";
                txtElev.Height = textH;
                txtElev.Rotation = Math.PI / 2;
                txtElev.Position = new Point3d(pt.offs - 0.15, rowTop + 0.6, 0);
                btr.AppendEntity(txtElev);
                tr.AddNewlyCreatedDBObject(txtElev, true);

                // ── Width + Slope between consecutive key points ──
                if (i < keyPts.Count - 1)
                {
                    var next = keyPts[i + 1];
                    double dx = Math.Abs(next.offs - pt.offs);
                    double midX = (pt.offs + next.offs) / 2;

                    // Width value (horizontal, below label row)
                    var txtW = new DBText();
                    txtW.SetDatabaseDefaults();
                    txtW.Layer = "MAHOD-SV-TEXT";
                    txtW.TextString = $"{dx:F2}";
                    txtW.Height = textH * 0.85;
                    txtW.Position = new Point3d(midX - 0.6, rowTop - textH - 0.2, 0);
                    btr.AppendEntity(txtW);
                    tr.AddNewlyCreatedDBObject(txtW, true);

                    // Slope label on the corridor segment
                    // Use real elevation delta for slope (not exaggerated)
                    double dy = next.elev - pt.elev;
                    double slopePct = Math.Abs(dx) > 0.01 ? (dy / dx) * 100.0 : 0;
                    if (Math.Abs(slopePct) >= 0.5 && dx >= 1.5)
                    {
                        string slopeText;
                        if (Math.Abs(slopePct) <= 15)
                            slopeText = $"{Math.Abs(slopePct):F1}%";
                        else if (Math.Abs(dy) > 0.01)
                            slopeText = $"1:{Math.Abs(dx / dy):F1}";
                        else
                            continue;

                        // Position at midpoint, Y scaled by vertCoef
                        double midY = ((pt.elev - elevMin) + (next.elev - elevMin)) / 2 * vertCoef;
                        var txtSlope = new DBText();
                        txtSlope.SetDatabaseDefaults();
                        txtSlope.Layer = "MAHOD-SV-CORR";
                        txtSlope.TextString = slopeText;
                        txtSlope.Height = textH * 0.8;
                        txtSlope.Position = new Point3d(midX - 0.5, midY + 0.3, 0);
                        btr.AppendEntity(txtSlope);
                        tr.AddNewlyCreatedDBObject(txtSlope, true);
                    }
                }
            }

            btr.AppendEntity(labelPoly);
            tr.AddNewlyCreatedDBObject(labelPoly, true);
        }

        private static void DrawBands(Transaction tr, BlockTableRecord btr,
            double xMin, double xMax, double elevMin, double elevMax,
            List<(double offs, double elev)>? corrPts = null)
        {
            double bandY = -BAND_HEIGHT;
            double textH = 0.8;

            // Band border
            var border = new Polyline();
            border.SetDatabaseDefaults();
            border.Layer = "MAHOD-SV-TEXT";
            border.AddVertexAt(0, new Point2d(xMin, bandY), 0, 0, 0);
            border.AddVertexAt(1, new Point2d(xMax, bandY), 0, 0, 0);
            border.AddVertexAt(2, new Point2d(xMax, 0), 0, 0, 0);
            border.AddVertexAt(3, new Point2d(xMin, 0), 0, 0, 0);
            border.Closed = true;
            btr.AppendEntity(border);
            tr.AddNewlyCreatedDBObject(border, true);

            // Divider line at mid-band
            DrawLine(tr, btr, "MAHOD-SV-TEXT", xMin, bandY / 2, xMax, bandY / 2);

            // Labels
            var lblElev = new DBText();
            lblElev.SetDatabaseDefaults();
            lblElev.Layer = "MAHOD-SV-TEXT";
            lblElev.TextString = "Elevation";
            lblElev.Height = 1.0;
            lblElev.Position = new Point3d(xMin - 7, bandY * 0.75 - 0.5, 0);
            btr.AppendEntity(lblElev);
            tr.AddNewlyCreatedDBObject(lblElev, true);

            var lblOffs = new DBText();
            lblOffs.SetDatabaseDefaults();
            lblOffs.Layer = "MAHOD-SV-TEXT";
            lblOffs.TextString = "Offset";
            lblOffs.Height = 1.0;
            lblOffs.Position = new Point3d(xMin - 7, bandY * 0.25 - 0.5, 0);
            btr.AppendEntity(lblOffs);
            tr.AddNewlyCreatedDBObject(lblOffs, true);

            // Draw corridor point data in bands (like Igor's Elevation/Offset rows)
            // Skip points closer than 1.5m to avoid text overlap in bands
            if (corrPts != null && corrPts.Count > 0)
            {
                double ptTextH = 0.5;
                double lastDrawnX = double.MinValue;
                foreach (var pt in corrPts)
                {
                    if (pt.offs - lastDrawnX < 1.5 && lastDrawnX != double.MinValue)
                        continue;
                    lastDrawnX = pt.offs;

                    // Vertical separator line in band
                    DrawLine(tr, btr, "MAHOD-SV-TEXT", pt.offs, bandY, pt.offs, 0);

                    // Elevation value (vertical text)
                    var te = new DBText();
                    te.SetDatabaseDefaults();
                    te.Layer = "MAHOD-SV-TEXT";
                    te.TextString = $"{pt.elev:F2}";
                    te.Height = ptTextH;
                    te.Rotation = Math.PI / 2;
                    te.Position = new Point3d(pt.offs + 0.1, bandY * 0.95, 0);
                    btr.AppendEntity(te);
                    tr.AddNewlyCreatedDBObject(te, true);

                    // Offset value (vertical text)
                    var to = new DBText();
                    to.SetDatabaseDefaults();
                    to.Layer = "MAHOD-SV-TEXT";
                    to.TextString = $"{pt.offs:F1}";
                    to.Height = ptTextH;
                    to.Rotation = Math.PI / 2;
                    to.Position = new Point3d(pt.offs + 0.1, bandY * 0.45, 0);
                    btr.AppendEntity(to);
                    tr.AddNewlyCreatedDBObject(to, true);
                }
            }

            // Offset values at grid positions
            for (double x = Math.Ceiling(xMin / GRID_OFFS_STEP) * GRID_OFFS_STEP; x <= xMax; x += GRID_OFFS_STEP)
            {
                var t = new DBText();
                t.SetDatabaseDefaults();
                t.Layer = "MAHOD-SV-TEXT";
                t.TextString = $"{x:F0}";
                t.Height = textH * 0.8;
                t.Position = new Point3d(x - 1, bandY * 0.25 - textH / 2, 0);
                btr.AppendEntity(t);
                tr.AddNewlyCreatedDBObject(t, true);
            }
        }

        // ══════════════════════════════════════════════════════════
        // Utility methods
        // ══════════════════════════════════════════════════════════

        private static void EnsureLayer(
            Transaction tr, Database db, string name, short colorIndex, string? linetype = null)
        {
            var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null) return;

            ObjectId ltId = ObjectId.Null;
            if (!string.IsNullOrEmpty(linetype) &&
                !string.Equals(linetype, "Continuous", System.StringComparison.OrdinalIgnoreCase))
                ltId = GetOrLoadLinetype(db, tr, linetype!);

            if (!lt.Has(name))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord();
                ltr.Name = name;
                ltr.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
                if (!ltId.IsNull) ltr.LinetypeObjectId = ltId;
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            else if (!ltId.IsNull)
            {
                // Keep an existing layer in sync with the dashed-existing convention.
                if (tr.GetObject(lt[name], OpenMode.ForWrite) is LayerTableRecord existing)
                    existing.LinetypeObjectId = ltId;
            }
        }

        /// <summary>Get-or-load a linetype by name; returns Null when it can't be loaded.</summary>
        private static ObjectId GetOrLoadLinetype(Database db, Transaction tr, string name)
        {
            try
            {
                var lt = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                if (lt == null) return ObjectId.Null;
                if (lt.Has(name)) return lt[name];

                // Deterministic metre-scale dashed pattern for cross-section EG lines, so the
                // dash is visible at section scale regardless of which acad*.lin is installed
                // (acad.lin DASHED2 is ~0.25 units → looks solid; acadiso ~3.175 → varies).
                if (string.Equals(name, "MAHOD_SEC_DASH", System.StringComparison.OrdinalIgnoreCase))
                {
                    lt.UpgradeOpen();
                    var rec = new LinetypeTableRecord
                    {
                        Name = name,
                        AsciiDescription = "MahodAI section dashed _ _ _",
                        PatternLength = 2.5,
                        NumDashes = 2,
                    };
                    rec.SetDashLengthAt(0, 1.5);   // 1.5 m dash
                    rec.SetDashLengthAt(1, -1.0);  // 1.0 m gap
                    var newId = lt.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                    return newId;
                }

                try { db.LoadLineTypeFile(name, "acadiso.lin"); }
                catch { try { db.LoadLineTypeFile(name, "acad.lin"); } catch { } }
                lt = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                if (lt != null && lt.Has(name)) return lt[name];
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SV linetype '{name}' load failed: {ex.Message}");
            }
            return ObjectId.Null;
        }
    }
}
