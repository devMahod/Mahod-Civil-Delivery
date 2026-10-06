using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates a Corridor (3D road model) from alignment + profile + assembly.
    ///
    /// Civil 3D 2026 API:
    /// - CorridorCollection.Add(name) — creates empty corridor
    /// - corridor.Baselines.Add(name, alignmentId, profileId) — adds baseline
    /// - baseline.BaselineRegions.Add(regionName, assemblyId) — adds region with assembly
    /// - corridor.Rebuild() — builds the 3D model
    ///
    /// Fallback: LISP command _-CREATECORRIDOR if API fails.
    /// </summary>
    public class CreateCorridorTool : DrawingToolBase
    {
        /// <summary>Corridor name pending surface creation (set before SendStringToExecute).</summary>
        internal static string? _pendingCorridorName;

        public override string Name => "create_corridor";
        public override string Description =>
            "Creates a Corridor (3D road model) by combining alignment + profile + assembly. " +
            "The corridor generates cross-sections along the alignment and builds a 3D road surface.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the corridor (e.g. 'Road73_Corridor')""
                },
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the alignment to use as baseline centerline""
                },
                ""profile_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the design (FG) profile for vertical alignment""
                },
                ""assembly_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the assembly (cross-section template)""
                },
                ""frequency"": {
                    ""type"": ""number"",
                    ""description"": ""Cross-section sampling interval on tangent segments in meters (default: 20)""
                },
                ""frequency_curves"": {
                    ""type"": ""number"",
                    ""description"": ""Sampling interval on curves and spirals in meters (default: 10, finer than tangents for accuracy)""
                },
                ""target_surface_name"": {
                    ""type"": ""string"",
                    ""description"": ""Surface to use as target for daylight slopes (optional, uses the same surface as EG if not specified)""
                }
            },
            ""required"": [""name"", ""alignment_name"", ""profile_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            // ── Parse parameters ──────────────────────────────────
            var corridorName = GetRequiredStringParam(parameters, "name");
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var assemblyName = GetStringParam(parameters, "assembly_name");
            var frequency = GetDoubleParam(parameters, "frequency") ?? 20.0;
            var frequencyCurves = GetDoubleParam(parameters, "frequency_curves") ?? 10.0;
            var targetSurfaceName = GetStringParam(parameters, "target_surface_name");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // ── Check if corridor already exists ──────────────────
            foreach (ObjectId existingId in civilDoc.CorridorCollection)
            {
                var existing = tr.GetObject(existingId, OpenMode.ForRead) as CivilDb.Corridor;
                if (existing != null && existing.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Ok(new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["corridor_name"] = corridorName,
                        ["already_existed"] = true,
                        ["baselines_count"] = existing.Baselines.Count,
                        ["message"] = $"Corridor '{corridorName}' already exists with {existing.Baselines.Count} baseline(s)."
                    });
                }
            }

            // ── Find alignment ────────────────────────────────────
            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            // ── Find profile (FG) ─────────────────────────────────
            var profileId = ObjectFinder.FindProfile(civilDoc, tr, profileName, alignmentName);
            if (profileId == null)
            {
                // Try without alignment qualification
                profileId = ObjectFinder.FindProfile(civilDoc, tr, profileName);
            }
            if (profileId == null)
                return ToolResult.NotFound("Profile", profileName);

            // ── Find assembly ─────────────────────────────────────
            ObjectId assemblyId = ObjectId.Null;

            if (!string.IsNullOrEmpty(assemblyName))
            {
                // Find by name
                foreach (ObjectId id in civilDoc.AssemblyCollection)
                {
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Assembly;
                    if (asm != null && asm.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        assemblyId = id;
                        break;
                    }
                }
                if (assemblyId == ObjectId.Null)
                    return ToolResult.NotFound("Assembly", assemblyName);
            }
            else
            {
                // Use first available assembly
                foreach (ObjectId id in civilDoc.AssemblyCollection)
                {
                    assemblyId = id;
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Assembly;
                    assemblyName = asm?.Name ?? "Unknown";
                    break;
                }
                if (assemblyId == ObjectId.Null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "No assemblies found in drawing. Create an assembly first using create_assembly.");
            }

            // ── Find target surface — auto-detect EG if not specified ────
            ObjectId targetSurfaceId = ObjectId.Null;
            if (string.IsNullOrEmpty(targetSurfaceName))
            {
                // Auto-detect: enumerate ALL surfaces, pick first non-corridor TIN surface
                var surfaceIds = civilDoc.GetSurfaceIds();
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] Auto-detect target surface: {surfaceIds.Count} surfaces in drawing");
                foreach (ObjectId id in surfaceIds)
                {
                    try
                    {
                        var surfObj = tr.GetObject(id, OpenMode.ForRead);
                        string? surfName = null;
                        bool isTin = false;

                        // Try TinSurface first, then generic Surface
                        if (surfObj is CivilDb.TinSurface tin)
                        {
                            surfName = tin.Name;
                            isTin = true;
                        }
                        else if (surfObj is CivilDb.Surface surf)
                        {
                            surfName = surf.Name;
                        }

                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI]   Surface '{surfName}' (TIN={isTin}, type={surfObj?.GetType().Name})");

                        if (surfName != null && isTin &&
                            !surfName.Contains("_Top") && !surfName.Contains("_Bot") &&
                            !surfName.Contains("Corridor") && !surfName.Contains("- EG"))
                        {
                            targetSurfaceId = id;
                            targetSurfaceName = surfName;
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI]   → Selected as target surface: '{surfName}'");
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI]   Surface read failed: {ex.Message}");
                    }
                }
            }
            else
            {
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    try
                    {
                        var surface = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Surface;
                        if (surface != null && surface.Name.Equals(targetSurfaceName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetSurfaceId = id;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetSurfaceId == ObjectId.Null)
                    return ToolResult.NotFound("Surface", targetSurfaceName!);
            }

            // Strict gate: a corridor without a target surface produces daylight slopes
            // that never reach EG — that is a broken road model, not a warning.
            if (targetSurfaceId == ObjectId.Null)
            {
                return ToolResult.Fail(
                    "NO_TARGET_SURFACE",
                    "לא נמצא משטח מטרה (EG) לשיפועי החיבור של המסדרון. " +
                    "ודא שקיים משטח TIN בשרטוט או ציין target_surface_name מפורש.");
            }

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] Target surface for daylight: '{targetSurfaceName}' (id={targetSurfaceId})");

            // ── Create corridor via API ───────────────────────────
            string creationMethod = "unknown";
            ApiOutcome? outcome = null;
            int baselinesCount = 0;
            int regionsCount = 0;

            try
            {
                outcome = CreateCorridorViaApi(
                    tr, civilDoc, corridorName,
                    alignmentId.Value, profileId.Value, assemblyId,
                    targetSurfaceId, frequency, frequencyCurves);
                creationMethod = "API_CorridorCollection";

                // Read back stats
                if (outcome.CorridorId != ObjectId.Null)
                {
                    var corridor = tr.GetObject(outcome.CorridorId, OpenMode.ForRead) as CivilDb.Corridor;
                    if (corridor != null)
                    {
                        baselinesCount = corridor.Baselines.Count;
                        foreach (CivilDb.Baseline bl in corridor.Baselines)
                        {
                            regionsCount += bl.BaselineRegions.Count;
                        }
                    }
                }
            }
            catch (Exception apiEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] CreateCorridorTool API failed: {apiEx.Message}\n{apiEx.StackTrace}");

                // Fallback: LISP command
                if (string.IsNullOrEmpty(assemblyName))
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Failed to create corridor via API: {apiEx.Message}. No assembly name for LISP fallback.");

                try
                {
                    bool queued = CreateCorridorViaLisp(
                        corridorName, alignmentName, profileName, assemblyName);
                    if (queued)
                    {
                        creationMethod = "LISP_CreateCorridor";
                    }
                }
                catch (Exception lispEx)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Failed to create corridor. API: {apiEx.Message}. LISP: {lispEx.Message}");
                }
            }

            // ── Strict gate: failed rebuild is a failure, not a warning ───
            // A corridor that did not rebuild has no geometry — downstream steps
            // (corridor surface, sample lines, section views) would all act on an
            // empty model. No silent continue.
            if (outcome != null && !outcome.RebuildSucceeded)
            {
                // Invalidate caches even on failure — depending on transaction handling
                // the corridor object may still exist in the drawing.
                cache.RemoveByPattern("list_corridors:");
                cache.RemoveByPattern("get_corridor_info:");
                cache.RemoveByPattern("get_drawing_summary:");
                return ToolResult.Fail(
                    "CORRIDOR_REBUILD_FAILED",
                    $"בניית המסדרון '{corridorName}' נכשלה בשלב ה-Rebuild: {outcome.RebuildError}. " +
                    "המסדרון לא נבנה — בדוק את ה-Assembly, הפרופיל ומשטח המטרה.",
                    JsonSerializer.Serialize(new
                    {
                        rebuild_error = outcome.RebuildError,
                        surface_targets_assigned = outcome.SurfaceTargetsAssigned,
                        target_assignment_error = outcome.TargetAssignmentError,
                    }));
            }

            // ── Invalidate cache ──────────────────────────────────
            cache.RemoveByPattern("list_corridors:");
            cache.RemoveByPattern("get_corridor_info:");
            cache.RemoveByPattern("get_corridor_cross_section:");
            cache.RemoveByPattern("get_drawing_summary:");

            // ── Build result ──────────────────────────────────────
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["corridor_name"] = corridorName,
                ["creation_method"] = creationMethod,
                ["alignment_name"] = alignmentName,
                ["profile_name"] = profileName,
                ["assembly_name"] = assemblyName!,
                ["frequency_m"] = frequency,
                ["baselines_count"] = baselinesCount,
                ["regions_count"] = regionsCount,
                ["message"] = BuildResultMessage(corridorName, creationMethod, alignmentName,
                    profileName, assemblyName!, baselinesCount, regionsCount)
            };

            if (targetSurfaceId != ObjectId.Null)
                result["target_surface"] = targetSurfaceName!;

            if (outcome != null)
            {
                result["rebuild"] = new Dictionary<string, object>
                {
                    ["succeeded"] = outcome.RebuildSucceeded,
                };
                var targetAssignment = new Dictionary<string, object>
                {
                    ["surface_targets_assigned"] = outcome.SurfaceTargetsAssigned,
                };
                if (outcome.TargetAssignmentError != null)
                    targetAssignment["error"] = outcome.TargetAssignmentError;
                result["target_assignment"] = targetAssignment;

                // Deferred operations run AFTER this transaction commits; report their
                // queue status honestly — the actual outcome must be verified via
                // readback (list_surfaces) on a later call.
                var deferredSurface = new Dictionary<string, object>
                {
                    ["operation"] = "corridor_surface_creation",
                    ["command"] = "MAHOD_BUILDCORRIDORSURFACE",
                    ["status"] = outcome.DeferredSurfaceQueued ? "queued" : "queue_failed",
                    ["note"] = "runs after the transaction commits; verify via list_surfaces readback",
                };
                if (outcome.DeferredSurfaceError != null)
                    deferredSurface["error"] = outcome.DeferredSurfaceError;
                result["deferred_operations"] = new List<object> { deferredSurface };
            }
            else if (creationMethod == "LISP_CreateCorridor")
            {
                // LISP queue is asynchronous — the corridor does NOT exist yet when this
                // result returns. Flag it so the agent verifies via readback.
                result["status"] = "queued_unverified";
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>Readback outcome of the managed-API corridor creation chain.</summary>
        private sealed class ApiOutcome
        {
            public ObjectId CorridorId { get; set; } = ObjectId.Null;
            public bool RebuildSucceeded { get; set; }
            public string? RebuildError { get; set; }
            public int SurfaceTargetsAssigned { get; set; }
            public string? TargetAssignmentError { get; set; }
            public bool DeferredSurfaceQueued { get; set; }
            public string? DeferredSurfaceError { get; set; }
        }

        /// <summary>
        /// Create corridor via managed API.
        /// Steps: Add corridor → Add baseline → Add region → Set frequency → Rebuild.
        /// Returns an <see cref="ApiOutcome"/> with honest per-step status — rebuild and
        /// target-assignment failures are reported, never silently swallowed.
        /// </summary>
        private static ApiOutcome CreateCorridorViaApi(
            Transaction tr,
            CivilDocument civilDoc,
            string corridorName,
            ObjectId alignmentId,
            ObjectId profileId,
            ObjectId assemblyId,
            ObjectId targetSurfaceId,
            double frequency,
            double frequencyCurves)
        {
            var outcome = new ApiOutcome();
            // Step 1: Create corridor
            // CorridorCollection.Add(string name) → ObjectId
            var corridorCollection = civilDoc.CorridorCollection;
            ObjectId corridorId = ObjectId.Null;

            // Try Add(string name) overload
            var addMethods = corridorCollection.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Add")
                .ToList();

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] CorridorCollection.Add overloads: {addMethods.Count}");

            foreach (var method in addMethods)
            {
                var parms = method.GetParameters();
                try
                {
                    if (parms.Length == 1 && parms[0].ParameterType == typeof(string))
                    {
                        corridorId = (ObjectId)method.Invoke(corridorCollection, new object[] { corridorName });
                        break;
                    }
                    else if (parms.Length == 2 &&
                             parms[0].ParameterType == typeof(string) &&
                             parms[1].ParameterType == typeof(string))
                    {
                        corridorId = (ObjectId)method.Invoke(corridorCollection, new object[] { corridorName, "" });
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CorridorCollection.Add {parms.Length}-param failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (corridorId == ObjectId.Null)
                throw new InvalidOperationException("Failed to create corridor via CorridorCollection.Add");

            // Step 2: Add baseline (alignment + profile)
            var corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as CivilDb.Corridor;
            if (corridor == null)
                throw new InvalidOperationException("Failed to open newly created corridor");

            string baselineName = $"BL - {corridorName}";

            // Baselines.Add(string name, ObjectId alignmentId, ObjectId profileId)
            CivilDb.Baseline baseline = null!;
            try
            {
                baseline = corridor.Baselines.Add(baselineName, alignmentId, profileId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] Baselines.Add 3-param failed: {ex.Message}");

                // Try reflection for alternative signatures
                var blAddMethods = corridor.Baselines.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Add")
                    .ToList();

                foreach (var method in blAddMethods)
                {
                    var parms = method.GetParameters();
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] Baselines.Add overload: ({string.Join(", ", parms.Select(p => p.ParameterType.Name))})");
                    try
                    {
                        if (parms.Length == 3)
                        {
                            var result = method.Invoke(corridor.Baselines, new object[] { baselineName, alignmentId, profileId });
                            if (result is CivilDb.Baseline bl)
                            {
                                baseline = bl;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (baseline == null)
                    throw new InvalidOperationException($"Failed to add baseline: {ex.Message}");
            }

            // Step 3: Add region with assembly
            // Civil 3D computes an AppliedAssembly at region-end. If region-end is
            // past the FG profile's defined range, Civil 3D extrapolates elevation
            // there — the last AA ends up with slightly-off FG + partly-extrapolated
            // alignment tangent, producing extra link segments and a visibly "weird"
            // corridor end. Clamp region-end to
            //   min(baseline.EndStation, profile.EndingStation) - REGION_END_MARGIN
            // so the last AA always sits inside the well-defined profile range.
            const double REGION_END_MARGIN = 0.5;
            double regionStart = baseline.StartStation;
            double regionEnd = baseline.EndStation;
            try
            {
                var fgProfile = tr.GetObject(profileId, OpenMode.ForRead) as CivilDb.Profile;
                if (fgProfile != null)
                {
                    double profEnd = fgProfile.EndingStation;
                    if (profEnd < regionEnd) regionEnd = profEnd;
                }
            }
            catch (Exception profEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] Could not read FG profile end: {profEx.Message}");
            }
            regionEnd = Math.Max(regionStart + 1.0, regionEnd - REGION_END_MARGIN);

            string regionName = $"Region - {corridorName}";
            bool regionAdded = false;
            try
            {
                var regAddMethods = baseline.BaselineRegions.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Add")
                    .ToList();

                // Prefer 4-arg overload (name, assemblyId, startStation, endStation)
                foreach (var method in regAddMethods)
                {
                    var parms = method.GetParameters();
                    if (parms.Length == 4 &&
                        parms[0].ParameterType == typeof(string) &&
                        parms[2].ParameterType == typeof(double) &&
                        parms[3].ParameterType == typeof(double))
                    {
                        try
                        {
                            method.Invoke(baseline.BaselineRegions, new object[]
                            {
                                regionName, assemblyId, regionStart, regionEnd
                            });
                            regionAdded = true;
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] BaselineRegions.Add(4-arg) " +
                                $"sta=[{regionStart:F2},{regionEnd:F2}] " +
                                $"(baseline end={baseline.EndStation:F2}, margin={REGION_END_MARGIN}m)");
                            break;
                        }
                        catch (Exception ex4)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] BaselineRegions.Add(4-arg) failed: {ex4.InnerException?.Message ?? ex4.Message}");
                        }
                    }
                }

                // Fall back to the 2-arg overload
                if (!regionAdded)
                {
                    baseline.BaselineRegions.Add(regionName, assemblyId);
                    regionAdded = true;
                    System.Diagnostics.Debug.WriteLine(
                        "[MahodAI] BaselineRegions.Add(2-arg) — region uses default baseline extents");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] BaselineRegions.Add failed: {ex.Message}");

                if (!regionAdded)
                    throw new InvalidOperationException($"Failed to add region: {ex.Message}");
            }

            // Step 4: Set variable frequency on the region
            // Igor's approach: region.AppliedAssemblySetting.FrequencyAlongTangents etc.
            try
            {
                if (baseline.BaselineRegions.Count > 0)
                {
                    var region = baseline.BaselineRegions[0];

                    // Access AppliedAssemblySetting property (Igor's pattern)
                    var settingProp = region.GetType().GetProperty("AppliedAssemblySetting");
                    if (settingProp != null)
                    {
                        var setting = settingProp.GetValue(region);
                        if (setting != null)
                        {
                            var settingType = setting.GetType();

                            // FrequencyAlongTangents — straight segments
                            SetPropertySafe(settingType, setting, "FrequencyAlongTangents", frequency);

                            // FrequencyAlongCurves — horizontal curves (finer)
                            SetPropertySafe(settingType, setting, "FrequencyAlongCurves", frequencyCurves);

                            // FrequencyAlongSpirals — spiral transitions (same as curves)
                            SetPropertySafe(settingType, setting, "FrequencyAlongSpirals", frequencyCurves);

                            // FrequencyAlongProfileCurves — vertical curves
                            SetPropertySafe(settingType, setting, "FrequencyAlongProfileCurves", frequency);

                            // FrequencyAlongTargetCurves — target alignment curves
                            SetPropertySafe(settingType, setting, "FrequencyAlongTargetCurves", frequency);

                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] Frequency set via AppliedAssemblySetting: tangent={frequency}m, curve={frequencyCurves}m");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[MahodAI] AppliedAssemblySetting is null — frequency not set");
                        }
                    }
                    else
                    {
                        // Fallback: try setting directly on region (older API)
                        var regionType = region.GetType();
                        SetPropertySafe(regionType, region, "FrequencyAlongTangents", frequency);
                        SetPropertySafe(regionType, region, "FrequencyAlongCurves", frequencyCurves);
                        SetPropertySafe(regionType, region, "FrequencyAlongSpirals", frequencyCurves);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] Setting frequency failed (non-critical): {ex.Message}");
            }

            // Step 5: Set target surface
            var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument?.Editor;
            if (targetSurfaceId != ObjectId.Null)
            {
                ed?.WriteMessage($"\n[MahodAI] Setting target surface (id={targetSurfaceId})...\n");
                try
                {
                    outcome.SurfaceTargetsAssigned =
                        SetCorridorTargetSurface(corridor, targetSurfaceId, ed);
                }
                catch (Exception ex)
                {
                    outcome.TargetAssignmentError = ex.Message;
                    ed?.WriteMessage($"\n[MahodAI] Setting target surface FAILED: {ex.Message}\n");
                }
            }

            // Rebuild corridor AFTER targets are set — Igor's pattern proves this works
            // within the same transaction. The region already has target info from
            // GetTargets()/SetTargets() which is in-memory state on the region object.
            // A failed rebuild is recorded in the outcome and FAILS the tool upstream —
            // a corridor that never rebuilt has no geometry.
            try
            {
                corridor.Rebuild();
                outcome.RebuildSucceeded = true;
                ed?.WriteMessage($"\n[MahodAI] Corridor rebuilt successfully after target assignment\n");
            }
            catch (Exception rebuildEx)
            {
                outcome.RebuildSucceeded = false;
                outcome.RebuildError = rebuildEx.Message;
                ed?.WriteMessage($"\n[MahodAI] In-tx Rebuild FAILED: {rebuildEx.Message}\n");
            }

            // Step 6: Queue corridor surface creation for AFTER this transaction commits.
            // CRITICAL: corridor surface CANNOT be created in the same transaction as
            // the corridor itself — the corridor has no geometry yet (it needs commit first).
            // Igor's code always creates surfaces on ALREADY-EXISTING corridors.
            // Solution: register a deferred command via SendStringToExecute. Queue status
            // (NOT the eventual outcome — that needs readback) is reported in the result.
            if (outcome.RebuildSucceeded)
            {
                try
                {
                    _pendingCorridorName = corridorName;
                    var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .DocumentManager.MdiActiveDocument;
                    if (doc != null)
                    {
                        doc.SendStringToExecute("MAHOD_BUILDCORRIDORSURFACE\n", true, false, false);
                        outcome.DeferredSurfaceQueued = true;
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI] Queued MAHOD_BUILDCORRIDORSURFACE for '{corridorName}'");
                    }
                    else
                    {
                        outcome.DeferredSurfaceError = "No active document to queue the deferred command.";
                    }
                }
                catch (Exception ex)
                {
                    outcome.DeferredSurfaceError = ex.Message;
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] Queue deferred surface failed: {ex.Message}");
                }
            }
            else
            {
                outcome.DeferredSurfaceError = "Skipped — corridor rebuild failed.";
            }

            outcome.CorridorId = corridorId;
            return outcome;
        }

        /// <summary>
        /// Safely set a property on an object via reflection (non-throwing).
        /// </summary>
        private static void SetPropertySafe(Type type, object obj, string propertyName, object value)
        {
            try
            {
                var prop = type.GetProperty(propertyName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(obj, value);
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] Set {propertyName} = {value}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] Set {propertyName} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set target surface for corridor subassemblies (daylight slopes).
        /// Igor's pattern: region.GetTargets() → set TargetIds → region.SetTargets()
        /// MUST use BaselineRegion, NOT Corridor or Baseline.
        /// Returns the total number of surface targets assigned across all regions.
        /// </summary>
        private static int SetCorridorTargetSurface(
            CivilDb.Corridor corridor, ObjectId surfaceId,
            Autodesk.AutoCAD.EditorInput.Editor? ed = null)
        {
            var surfaceIds = new ObjectIdCollection();
            surfaceIds.Add(surfaceId);
            int totalAssigned = 0;

            int blCount = corridor.Baselines.Count;
            System.Diagnostics.Debug.WriteLine($"[MahodAI] SetCorridorTargetSurface: {blCount} baselines");
            ed?.WriteMessage($"\n[MahodAI] Baselines: {blCount}\n");

            foreach (CivilDb.Baseline bl in corridor.Baselines)
            {
                int regCount = bl.BaselineRegions.Count;
                ed?.WriteMessage($"\n[MahodAI] Baseline '{bl.Name}', regions: {regCount}\n");

                foreach (CivilDb.BaselineRegion region in bl.BaselineRegions)
                {
                    ed?.WriteMessage($"\n[MahodAI] Region '{region.Name}' — getting targets...\n");

                    CivilDb.SubassemblyTargetInfoCollection targets;
                    try
                    {
                        targets = region.GetTargets();
                    }
                    catch (System.Exception ex)
                    {
                        ed?.WriteMessage($"\n[MahodAI] region.GetTargets() FAILED: {ex.Message}\n");
                        continue;
                    }

                    System.Diagnostics.Debug.WriteLine($"[MahodAI] Region '{region.Name}': {targets.Count} targets");
                    ed?.WriteMessage($"\n[MahodAI] Got {targets.Count} targets\n");

                    int assignedCount = 0;
                    foreach (CivilDb.SubassemblyTargetInfo target in targets)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI]   target: '{target.SubassemblyName}' type={target.TargetType}");
                        ed?.WriteMessage($"\n[MahodAI]   target: '{target.SubassemblyName}' type={target.TargetType}\n");

                        // ONLY assign Surface targets — Elevation targets need Profile ObjectId
                        // (not Surface), Offset targets need Alignment ObjectId.
                        // Assigning wrong ObjectId type causes ArgumentException in AeccDbMgd.dll
                        // which kills the entire target assignment loop!
                        if (target.TargetType == CivilDb.SubassemblyLogicalNameType.Surface)
                        {
                            try
                            {
                                target.TargetIds = surfaceIds;
                                assignedCount++;
                                System.Diagnostics.Debug.WriteLine(
                                    $"[MahodAI]   → ASSIGNED Surface target to '{target.SubassemblyName}'");
                            }
                            catch (System.Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[MahodAI]   → FAILED Surface target: {ex.Message}");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI]   → skipped {target.TargetType} (only Surface targets get EG)");
                        }
                    }

                    if (assignedCount > 0)
                    {
                        region.SetTargets(targets);
                        totalAssigned += assignedCount;
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI] SetTargets OK: {assignedCount} targets assigned for region '{region.Name}'");
                        ed?.WriteMessage($"\n[MahodAI] SetTargets OK ({assignedCount} targets assigned)\n");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[MahodAI] WARNING: no Surface/Elevation targets found in assembly!");
                        ed?.WriteMessage($"\n[MahodAI] WARNING: no Surface/Elevation targets found in assembly!\n");
                    }
                }
            }

            return totalAssigned;
        }

        /// <summary>
        /// Fallback: Create corridor via LISP command.
        /// </summary>
        private static bool CreateCorridorViaLisp(
            string corridorName, string alignmentName, string profileName, string assemblyName)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // _-CREATECORRIDOR prompts: Name, Baseline alignment, Profile, Assembly, Target surface, Frequency
            string lispCmd = "(progn " +
                "(setvar \"CMDECHO\" 0) " +
                $"(command \"_-CREATECORRIDOR\" \"{corridorName}\" \"{alignmentName}\" \"{profileName}\" \"{assemblyName}\" \"\" \"\") " +
                "(setvar \"CMDECHO\" 1) " +
                "(princ))";

            doc.SendStringToExecute(lispCmd + "\n", true, false, false);
            return true;
        }

        private static string BuildResultMessage(
            string corridorName, string method,
            string alignmentName, string profileName, string assemblyName,
            int baselines, int regions)
        {
            if (method.StartsWith("API_"))
            {
                return $"Corridor '{corridorName}' created successfully. " +
                    $"Baseline: {alignmentName} + {profileName}, Assembly: {assemblyName}. " +
                    $"{baselines} baseline(s), {regions} region(s). " +
                    "Corridor has been rebuilt. Check 3D view to see the road model.";
            }
            else if (method.StartsWith("LISP_"))
            {
                return $"Corridor '{corridorName}' creation command sent via LISP. " +
                    $"Components: alignment='{alignmentName}', profile='{profileName}', assembly='{assemblyName}'. " +
                    "The corridor will be created after the command completes.";
            }
            return $"Corridor '{corridorName}' creation attempted.";
        }
    }
}