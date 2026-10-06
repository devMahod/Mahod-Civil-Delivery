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
    /// Creates profiles for an alignment:
    /// 1. EG (Existing Ground) profile — sampled from a surface along the alignment
    /// 2. FG (Finished Ground / Design) profile — layout profile with PVI points and vertical curves
    ///
    /// Civil 3D 2026 API:
    /// - EG: Profile.CreateFromSurface(name, alignmentId, surfaceId, layerId, styleId, labelSetId)
    ///       via reflection (signature varies across versions)
    /// - FG: Profile.CreateByLayout(name, alignmentId, layerId, styleId, labelSetId)
    ///       then profile.PVIs.AddPVI(station, elevation) for each PVI point
    ///
    /// Fallback: LISP commands if managed API fails.
    /// </summary>
    public class CreateProfileTool : DrawingToolBase
    {
        public override string Name => "create_profile";
        public override string Description =>
            "Creates profiles for an alignment: (1) EG profile sampled from surface, " +
            "(2) optionally a design (FG) profile with PVI points and vertical curves. " +
            "Both profiles are bound to the specified alignment.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the existing alignment to create profiles for""
                },
                ""surface_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the surface to sample for EG profile""
                },
                ""create_design_profile"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to also create a design (FG) profile (default: true)""
                },
                ""design_profile_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the design profile (default: '<alignment>_FG')""
                },
                ""pvi_points"": {
                    ""type"": ""array"",
                    ""items"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""number"" },
                        ""minItems"": 2,
                        ""maxItems"": 2
                    },
                    ""description"": ""Array of [station, elevation] PVI points for design profile. Minimum 2 points (start and end).""
                },
                ""vertical_curve_length"": {
                    ""type"": ""number"",
                    ""description"": ""Default vertical curve length in meters for all internal PVIs (default: 100)""
                }
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
            // ── Parse parameters ──────────────────────────────────
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var surfaceName = GetRequiredStringParam(parameters, "surface_name");
            var createDesignProfile = GetBoolParam(parameters, "create_design_profile", true);
            var designProfileName = GetStringParam(parameters, "design_profile_name");
            var verticalCurveLength = GetDoubleParam(parameters, "vertical_curve_length") ?? 100.0;

            // Parse PVI points
            List<(double station, double elevation)> pviPoints = new();
            if (parameters.TryGetProperty("pvi_points", out var pviArray) &&
                pviArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in pviArray.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array) continue;
                    var coords = point.EnumerateArray().ToList();
                    if (coords.Count >= 2)
                    {
                        pviPoints.Add((coords[0].GetDouble(), coords[1].GetDouble()));
                    }
                }
            }

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // ── Find alignment ────────────────────────────────────
            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to read alignment");

            // ── Find surface ──────────────────────────────────────
            ObjectId surfaceId = ObjectId.Null;
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                var surface = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Surface;
                if (surface != null && surface.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    surfaceId = id;
                    break;
                }
            }

            if (surfaceId == ObjectId.Null)
                return ToolResult.NotFound("Surface", surfaceName);

            var db = HostApplicationServices.WorkingDatabase;

            // ── Step 1: Create EG profile from surface ────────────
            string egProfileName = $"{alignmentName} - EG";
            ObjectId egProfileId = ObjectId.Null;
            string egCreationMethod = "unknown";

            // Mirror MahodCivilNet: select the firm's TEMPLATE styles/layers BY NAME rather than
            // fabricating them. EG existing-ground = style "IL Existing Ground" (ByLayer) on the
            // firm's dashed color-40 longitudinal layer "LS-Exist" — the dashed olive look comes
            // from that layer. Fall back to the legacy fabricated style/layer only when the firm
            // template isn't present in the drawing.
            ObjectId egStyleId = ResolveProfileStyleByNames(civilDoc, tr, EG_PROFILE_STYLE_NAMES);
            if (egStyleId.IsNull) egStyleId = GetOrCreateMahodProfileStyle(civilDoc, db, tr, isFg: false);
            ObjectId egLayerId = ResolveLayerByNames(tr, db, EG_PROFILE_LAYER_NAMES);
            if (egLayerId.IsNull) egLayerId = GetOrCreateProfileLayer(tr, db, isFg: false);
            // EG line uses the minimal "Standard" label set (like MahodCivilNet) — the existing
            // elevations live in the profile-view band, not as labels along the line.
            ObjectId egLabelSetId = ResolveLabelSetByNames(civilDoc, tr, EG_LABEL_SET_NAMES);
            if (egLabelSetId.IsNull) egLabelSetId = GetFirstProfileLabelSetStyle(civilDoc);

            // Check if EG profile already exists
            var existingEgId = ObjectFinder.FindProfile(civilDoc, tr, egProfileName, alignmentName);
            if (existingEgId != null)
            {
                egProfileId = existingEgId.Value;
                egCreationMethod = "already_existed";
            }
            else
            {
                // Try managed API: Profile.CreateFromSurface
                try
                {
                    egProfileId = CreateEGProfileViaApi(tr, civilDoc, alignmentId.Value, surfaceId, egProfileName, egStyleId, egLayerId, egLabelSetId);
                    egCreationMethod = "API_CreateFromSurface";
                }
                catch (Exception apiEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CreateProfileTool EG API failed: {apiEx.Message}");

                    // Fallback: LISP command
                    try
                    {
                        bool queued = CreateEGProfileViaLisp(alignmentName, surfaceName, egProfileName);
                        if (queued)
                        {
                            egCreationMethod = "LISP_CreateProfileFromSurface";
                            // LISP is async — we can't get the ObjectId back
                        }
                    }
                    catch (Exception lispEx)
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Failed to create EG profile. API: {apiEx.Message}. LISP: {lispEx.Message}");
                    }
                }
            }

            // Re-apply the firm style + layer whether the profile was just created (the minimal
            // CreateFromSurface overload can drop the layer arg) or reused from a prior run — so
            // the dashed olive EG lands even when "<align> - EG" already exists.
            ApplyProfileAppearance(tr, egProfileId, egStyleId, egLayerId, isEg: true);

            // ── Read EG profile data ──────────────────────────────
            double egMinElev = 0, egMaxElev = 0;
            int egSampleCount = 0;

            if (egProfileId != ObjectId.Null)
            {
                try
                {
                    var egProfile = tr.GetObject(egProfileId, OpenMode.ForRead) as CivilDb.Profile;
                    if (egProfile != null)
                    {
                        egMinElev = egProfile.ElevationMin;
                        egMaxElev = egProfile.ElevationMax;

                        // Count entities as proxy for sample density
                        try { egSampleCount = egProfile.Entities.Count; } catch { }
                    }
                }
                catch { }
            }

            // ── Step 2: Create FG (design) profile ────────────────
            string fgProfileName = designProfileName ?? $"{alignmentName} - FG";
            ObjectId fgProfileId = ObjectId.Null;
            string fgCreationMethod = "not_requested";
            int fgPviCount = 0;
            var fgPviDetails = new List<object>();
            int pviRequested = pviPoints.Count;
            var pviErrors = new List<string>();
            bool fgReused = false;

            if (createDesignProfile && pviPoints.Count >= 2)
            {
                // Firm-template FG (design) styling, selected BY NAME (legacy fabrication only as
                // fallback): style "Design" (red) on layer "HW-PR-01", labels "Mahod Design".
                ObjectId fgStyleId = ResolveProfileStyleByNames(civilDoc, tr, FG_PROFILE_STYLE_NAMES);
                if (fgStyleId.IsNull) fgStyleId = GetOrCreateMahodProfileStyle(civilDoc, db, tr, isFg: true);
                ObjectId fgLayerId = ResolveLayerByNames(tr, db, FG_PROFILE_LAYER_NAMES);
                if (fgLayerId.IsNull) fgLayerId = GetOrCreateProfileLayer(tr, db, isFg: true);
                ObjectId fgLabelSetId = ResolveLabelSetByNames(civilDoc, tr, FG_LABEL_SET_NAMES);
                if (fgLabelSetId.IsNull) fgLabelSetId = GetFirstProfileLabelSetStyle(civilDoc);

                // Check if FG profile already exists
                var existingFgId = ObjectFinder.FindProfile(civilDoc, tr, fgProfileName, alignmentName);
                if (existingFgId != null)
                {
                    fgProfileId = existingFgId.Value;
                    fgCreationMethod = "already_existed";
                    fgReused = true;

                    // Count existing PVIs
                    try
                    {
                        var existingFg = tr.GetObject(fgProfileId, OpenMode.ForRead) as CivilDb.Profile;
                        if (existingFg != null)
                        {
                            foreach (CivilDb.ProfilePVI _ in existingFg.PVIs)
                                fgPviCount++;
                        }
                    }
                    catch { }
                }
                else
                {
                    // Try managed API: Profile.CreateByLayout
                    try
                    {
                        fgProfileId = CreateFGProfileViaApi(tr, civilDoc, alignmentId.Value, fgProfileName, fgStyleId, fgLayerId, fgLabelSetId);
                        fgCreationMethod = "API_CreateByLayout";
                    }
                    catch (Exception apiEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI] CreateProfileTool FG API failed: {apiEx.Message}");

                        // Fallback: LISP command for layout profile
                        try
                        {
                            bool queued = CreateFGProfileViaLisp(alignmentName, fgProfileName);
                            if (queued)
                            {
                                fgCreationMethod = "LISP_CreateByLayout";
                            }
                        }
                        catch (Exception lispEx)
                        {
                            return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                                $"Failed to create FG profile. API: {apiEx.Message}. LISP: {lispEx.Message}");
                        }
                    }

                    // Add PVI points to the FG profile. Per-PVI failures are RECORDED, not
                    // swallowed — the tool fails unless added count == requested count.
                    if (fgProfileId != ObjectId.Null && pviPoints.Count >= 2)
                    {
                        try
                        {
                            var fgProfile = tr.GetObject(fgProfileId, OpenMode.ForWrite) as CivilDb.Profile;
                            if (fgProfile == null)
                            {
                                pviErrors.Add($"לא ניתן לפתוח את פרופיל התכן '{fgProfileName}' לכתיבה.");
                            }
                            else
                            {
                                // Pre-validate + clamp via the pure planner. End-station clamp is
                                // 0.1 m: Civil 3D throws on a PVI at the exact endStation, and the
                                // pullback gives the corridor's last AppliedAssembly a clean
                                // landing zone (see CreateCorridorTool REGION_END_MARGIN).
                                var plan = ProfilePviPlanner.Plan(
                                    pviPoints.Select(p => (p.station, p.elevation)).ToList(),
                                    alignment.StartingStation,
                                    alignment.EndingStation,
                                    endClamp: 0.1);
                                pviErrors.AddRange(plan.Errors);
                                if (plan.ClampedCount > 0)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[MahodAI] Clamped {plan.ClampedCount} PVI points to " +
                                        $"endStation-0.1m (endStation={alignment.EndingStation:F2})");
                                }

                                foreach (var (station, elevation) in plan.Valid)
                                {
                                    try
                                    {
                                        fgProfile.PVIs.AddPVI(station, elevation);
                                        fgPviCount++;
                                        fgPviDetails.Add(new
                                        {
                                            station = Math.Round(station, 3),
                                            elevation = Math.Round(elevation, 3)
                                        });
                                    }
                                    catch (Exception pviEx)
                                    {
                                        pviErrors.Add(
                                            $"הוספת PVI בתחנה {station:F2} (רום {elevation:F2}) נכשלה: {pviEx.Message}");
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[MahodAI] AddPVI at station {station}: {pviEx.Message}");
                                    }
                                }

                                // Add vertical curves to internal PVIs (not first/last)
                                if (verticalCurveLength > 0 && fgPviCount >= 3)
                                {
                                    AddVerticalCurves(fgProfile, verticalCurveLength);
                                }
                            }
                        }
                        catch (Exception pviEx)
                        {
                            pviErrors.Add($"הוספת נקודות ה-PVI נכשלה: {pviEx.Message}");
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] PVI addition failed: {pviEx.Message}");
                        }
                    }
                    else if (fgProfileId == ObjectId.Null)
                    {
                        // LISP-queued FG: the layout profile may appear asynchronously, but the
                        // requested PVIs were NOT added — that is a failure under the strict
                        // contract, not a "success with manual follow-up".
                        pviErrors.Add(
                            "פרופיל התכן נוצר באמצעות פקודת LISP ולכן לא ניתן היה להוסיף את נקודות ה-PVI אוטומטית.");
                    }
                }

                // Apply the firm FG style + layer (red design line) whether the profile was just
                // created or reused from a prior run.
                if (fgProfileId != ObjectId.Null)
                    ApplyProfileAppearance(tr, fgProfileId, fgStyleId, fgLayerId);
            }
            else if (createDesignProfile && pviPoints.Count < 2)
            {
                fgCreationMethod = "skipped_insufficient_pvi_points";
            }

            // ── Invalidate cache ──────────────────────────────────
            cache.RemoveByPattern("list_profiles:");
            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_profile_elevation_at_station:");
            cache.RemoveByPattern("validate_profile:");
            cache.RemoveByPattern("get_drawing_summary:");

            // ── Strict success gate ───────────────────────────────
            // When a design profile with PVIs was requested and we actually attempted to
            // build it (not a reuse of an existing profile), success requires that EVERY
            // requested PVI was added. Anything less is a failure with per-PVI errors —
            // a half-built FG profile must never report success.
            bool fgAttempted = createDesignProfile && pviPoints.Count >= 2 && !fgReused;
            if (fgAttempted && (fgPviCount != pviRequested || pviErrors.Count > 0))
            {
                string errorList = pviErrors.Count > 0
                    ? " שגיאות: " + string.Join(" | ", pviErrors)
                    : string.Empty;
                return ToolResult.Fail(
                    "PVI_ADD_INCOMPLETE",
                    $"יצירת פרופיל התכן '{fgProfileName}' נכשלה: נוספו {fgPviCount} מתוך {pviRequested} נקודות PVI.{errorList}",
                    JsonSerializer.Serialize(new
                    {
                        pvi_requested = pviRequested,
                        pvi_added = fgPviCount,
                        pvi_errors = pviErrors,
                        fg_creation_method = fgCreationMethod,
                    }));
            }

            // ── Build result ──────────────────────────────────────
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["alignment_name"] = alignmentName,
                ["surface_name"] = surfaceName,
                ["pvi_requested"] = pviRequested,
                ["pvi_added"] = fgAttempted ? fgPviCount : 0,
                ["pvi_errors"] = pviErrors,
                ["eg_profile"] = new Dictionary<string, object>
                {
                    ["name"] = egProfileName,
                    ["creation_method"] = egCreationMethod,
                    ["min_elevation"] = Math.Round(egMinElev, 3),
                    ["max_elevation"] = Math.Round(egMaxElev, 3),
                    ["entity_count"] = egSampleCount
                },
                ["fg_profile"] = new Dictionary<string, object>
                {
                    ["name"] = fgProfileName,
                    ["creation_method"] = fgCreationMethod,
                    ["reused"] = fgReused,
                    ["pvi_count"] = fgPviCount,
                    ["pvi_points"] = fgPviDetails,
                    ["vertical_curve_length"] = verticalCurveLength
                },
                ["message"] = BuildResultMessage(egProfileName, egCreationMethod, fgProfileName, fgCreationMethod, fgPviCount)
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Create EG profile via managed API: Profile.CreateFromSurface
        /// </summary>
        private static ObjectId CreateEGProfileViaApi(
            Transaction tr,
            CivilDocument civilDoc,
            ObjectId alignmentId,
            ObjectId surfaceId,
            string profileName,
            ObjectId styleId,
            ObjectId layerId,
            ObjectId labelSetId)
        {
            // Style / layer / label set are resolved by the caller (firm template styles by name,
            // legacy fabrication only as fallback). Existing ground = "IL Existing Ground" on the
            // dashed color-40 layer "LS-Exist".

            // Find CreateFromSurface overloads via reflection
            var methods = typeof(CivilDb.Profile)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "CreateFromSurface")
                .ToList();

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] Found {methods.Count} CreateFromSurface overloads");

            ObjectId profileId = ObjectId.Null;

            foreach (var method in methods)
            {
                var parms = method.GetParameters();

                try
                {
                    if (parms.Length == 6)
                    {
                        // Expected: (string name, ObjectId alignmentId, ObjectId surfaceId, ObjectId layerId, ObjectId styleId, ObjectId labelSetId)
                        profileId = (ObjectId)method.Invoke(null, new object[]
                        {
                            profileName, alignmentId, surfaceId, layerId, styleId, labelSetId
                        });
                        break;
                    }
                    else if (parms.Length == 7 &&
                             parms[0].ParameterType == typeof(CivilDocument))
                    {
                        // Possible: (CivilDocument, string, ObjectId, ObjectId, ObjectId, ObjectId, ObjectId)
                        profileId = (ObjectId)method.Invoke(null, new object[]
                        {
                            civilDoc, profileName, alignmentId, surfaceId, layerId, styleId, labelSetId
                        });
                        break;
                    }
                    else if (parms.Length == 5)
                    {
                        // Minimal: (string, ObjectId, ObjectId, ObjectId, ObjectId)
                        profileId = (ObjectId)method.Invoke(null, new object[]
                        {
                            profileName, alignmentId, surfaceId, styleId, labelSetId
                        });
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CreateFromSurface {parms.Length}-param overload failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (profileId == ObjectId.Null)
            {
                throw new InvalidOperationException(
                    $"No compatible Profile.CreateFromSurface overload found. " +
                    $"Discovered {methods.Count} candidate(s).");
            }

            return profileId;
        }

        /// <summary>
        /// Create FG (layout/design) profile via managed API: Profile.CreateByLayout
        /// </summary>
        private static ObjectId CreateFGProfileViaApi(
            Transaction tr,
            CivilDocument civilDoc,
            ObjectId alignmentId,
            string profileName,
            ObjectId styleId,
            ObjectId layerId,
            ObjectId labelSetId)
        {
            // Style / layer / label set are resolved by the caller (firm template styles by name,
            // legacy fabrication only as fallback). Design = "Design" (red) on layer "HW-PR-01".

            // Find CreateByLayout overloads via reflection
            var methods = typeof(CivilDb.Profile)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "CreateByLayout")
                .ToList();

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] Found {methods.Count} CreateByLayout overloads");

            ObjectId profileId = ObjectId.Null;

            foreach (var method in methods)
            {
                var parms = method.GetParameters();

                try
                {
                    if (parms.Length == 5)
                    {
                        // Expected: (string name, ObjectId alignmentId, ObjectId layerId, ObjectId styleId, ObjectId labelSetId)
                        profileId = (ObjectId)method.Invoke(null, new object[]
                        {
                            profileName, alignmentId, layerId, styleId, labelSetId
                        });
                        break;
                    }
                    else if (parms.Length == 6 &&
                             parms[0].ParameterType == typeof(CivilDocument))
                    {
                        profileId = (ObjectId)method.Invoke(null, new object[]
                        {
                            civilDoc, profileName, alignmentId, layerId, styleId, labelSetId
                        });
                        break;
                    }
                    else if (parms.Length == 4)
                    {
                        // Minimal: (string, ObjectId, ObjectId, ObjectId)
                        profileId = (ObjectId)method.Invoke(null, new object[]
                        {
                            profileName, alignmentId, styleId, labelSetId
                        });
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] CreateByLayout {parms.Length}-param overload failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (profileId == ObjectId.Null)
            {
                throw new InvalidOperationException(
                    $"No compatible Profile.CreateByLayout overload found. " +
                    $"Discovered {methods.Count} candidate(s).");
            }

            return profileId;
        }

        /// <summary>
        /// Add circular vertical curves at internal PVIs.
        /// Uses AddFreeCircularCurveByPVIAndLength(ProfilePVI, double) — same approach as Igor's plugin.
        /// If curve length is too large for a PVI, tries with length/4 as fallback.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogCurve(string msg)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D", "curve_debug.log");
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
                System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {msg}\n");
            }
            catch { }
        }

        private static void AddVerticalCurves(CivilDb.Profile profile, double curveLength)
        {
            try
            {
                // Collect PVIs into a list for indexed access
                var pviList = new List<CivilDb.ProfilePVI>();
                foreach (CivilDb.ProfilePVI pvi in profile.PVIs)
                {
                    pviList.Add(pvi);
                }

                LogCurve($"AddVerticalCurves START: {pviList.Count} PVIs, curveLength={curveLength}");

                if (pviList.Count < 3) return;

                var entities = profile.Entities;

                // Log available methods on ProfileEntityCollection for debugging
                var methods = entities.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Add") || m.Name.Contains("Curve"))
                    .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                    .Distinct()
                    .ToList();
                LogCurve($"ProfileEntityCollection methods: {string.Join("; ", methods)}");

                int curvesAdded = 0;

                // Add curves at internal PVIs (skip first and last)
                for (int i = 1; i < pviList.Count - 1; i++)
                {
                    bool added = false;

                    // Try 1: AddFreeCircularCurveByPVIAndLength (Igor's approach)
                    try
                    {
                        entities.AddFreeCircularCurveByPVIAndLength(pviList[i], curveLength);
                        curvesAdded++;
                        added = true;
                        LogCurve($"PVI {i} (st={pviList[i].RawStation:F1}): AddFreeCircular OK");
                    }
                    catch (Exception ex1)
                    {
                        LogCurve($"PVI {i} (st={pviList[i].RawStation:F1}): AddFreeCircular FAIL: {ex1.Message}");

                        // Fallback: try with quarter length
                        try
                        {
                            entities.AddFreeCircularCurveByPVIAndLength(pviList[i], curveLength / 4);
                            curvesAdded++;
                            added = true;
                            LogCurve($"PVI {i}: AddFreeCircular/4 OK");
                        }
                        catch (Exception ex2)
                        {
                            LogCurve($"PVI {i}: AddFreeCircular/4 FAIL: {ex2.Message}");
                        }
                    }

                    // Try 2: If AddFreeCircular failed, try via reflection with other methods
                    if (!added)
                    {
                        try
                        {
                            var method = entities.GetType().GetMethod("AddFreeParabolaCurveByPVIAndLength",
                                new[] { typeof(CivilDb.ProfilePVI), typeof(double) });
                            if (method != null)
                            {
                                method.Invoke(entities, new object[] { pviList[i], curveLength });
                                curvesAdded++;
                                added = true;
                                LogCurve($"PVI {i}: AddFreeParabola OK");
                            }
                            else
                            {
                                LogCurve($"PVI {i}: AddFreeParabola method NOT FOUND");
                            }
                        }
                        catch (Exception ex3)
                        {
                            LogCurve($"PVI {i}: AddFreeParabola FAIL: {ex3.InnerException?.Message ?? ex3.Message}");
                        }
                    }
                }

                LogCurve($"AddVerticalCurves END: {curvesAdded}/{pviList.Count - 2} curves added");
            }
            catch (Exception ex)
            {
                LogCurve($"AddVerticalCurves FAILED: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback: Create EG profile via LISP command.
        /// </summary>
        private static bool CreateEGProfileViaLisp(string alignmentName, string surfaceName, string profileName)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Use CreateProfileFromSurface command
            string lispCmd = $"(progn " +
                $"(setvar \"CMDDIA\" 0) " +
                $"(command \"_CREATESURFACEPROFILE\" \"{alignmentName}\" \"{surfaceName}\" \"\") " +
                $"(setvar \"CMDDIA\" 1))";

            doc.SendStringToExecute(lispCmd, true, false, false);
            return true;
        }

        /// <summary>
        /// Fallback: Create FG (layout) profile via LISP command.
        /// </summary>
        private static bool CreateFGProfileViaLisp(string alignmentName, string profileName)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Use CreateProfileLayout command
            string lispCmd = $"(progn " +
                $"(setvar \"CMDDIA\" 0) " +
                $"(command \"_CREATEPROFILELAYOUT\" \"{profileName}\" \"\") " +
                $"(setvar \"CMDDIA\" 1))";

            doc.SendStringToExecute(lispCmd, true, false, false);
            return true;
        }

        /// <summary>
        /// Get profile style by index (0 = first, 1 = second, etc.).
        /// Falls back to first style if requested index doesn't exist.
        /// </summary>
        private static ObjectId GetProfileStyleByIndex(CivilDocument civilDoc, int index = 0)
        {
            try
            {
                ObjectId firstId = ObjectId.Null;
                int current = 0;
                foreach (ObjectId id in civilDoc.Styles.ProfileStyles)
                {
                    if (current == 0) firstId = id;
                    if (current == index) return id;
                    current++;
                }
                return firstId; // Fallback to first
            }
            catch { }
            return ObjectId.Null;
        }

        /// <summary>
        /// Get the first available profile label set style ID.
        /// </summary>
        private static ObjectId GetFirstProfileLabelSetStyle(CivilDocument civilDoc)
        {
            try
            {
                foreach (ObjectId id in civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles)
                {
                    return id;
                }
            }
            catch { }
            return ObjectId.Null;
        }

        // ── Firm-template (MahodCivilNet) style/layer selection by name ──
        // Stage-3 profile output mirrors the firm's CivilNet drawings by selecting the firm's
        // existing TEMPLATE styles/layers BY NAME (confirmed from the live drawing's style
        // inventory, 2026-06-19) instead of fabricating styles. Names are tried in order; the
        // first present in the drawing wins. Callers fall back to the legacy fabricated
        // style/layer only when none is present (non-firm drawings).
        private static readonly string[] EG_PROFILE_STYLE_NAMES = { "IL Existing Ground", "Existing Ground" };
        private static readonly string[] FG_PROFILE_STYLE_NAMES = { "Design", "Finished Ground", "Design Style" };
        private static readonly string[] EG_PROFILE_LAYER_NAMES = { "LS-Exist" };
        private static readonly string[] FG_PROFILE_LAYER_NAMES = { "HW-PR-01" };
        private static readonly string[] FG_LABEL_SET_NAMES = { "Mahod Design" };
        private static readonly string[] EG_LABEL_SET_NAMES = { "Standard" };

        /// <summary>Resolves a ProfileStyle ObjectId by trying each name in order (case-insensitive). Null if none.</summary>
        private static ObjectId ResolveProfileStyleByNames(CivilDocument civilDoc, Transaction tr, string[] names)
        {
            try
            {
                foreach (var want in names)
                    foreach (ObjectId id in civilDoc.Styles.ProfileStyles)
                        if (tr.GetObject(id, OpenMode.ForRead)
                                is Autodesk.Civil.DatabaseServices.Styles.ProfileStyle ps
                            && string.Equals(ps.Name, want, StringComparison.OrdinalIgnoreCase))
                            return id;
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ResolveProfileStyleByNames failed: {ex.Message}"); }
            return ObjectId.Null;
        }

        /// <summary>Resolves a ProfileLabelSet ObjectId by trying each name in order. Null if none.</summary>
        private static ObjectId ResolveLabelSetByNames(CivilDocument civilDoc, Transaction tr, string[] names)
        {
            try
            {
                foreach (var want in names)
                    foreach (ObjectId id in civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles)
                        if (tr.GetObject(id, OpenMode.ForRead)
                                is Autodesk.Civil.DatabaseServices.Styles.StyleBase sb
                            && string.Equals(sb.Name, want, StringComparison.OrdinalIgnoreCase))
                            return id;
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ResolveLabelSetByNames failed: {ex.Message}"); }
            return ObjectId.Null;
        }

        /// <summary>Station length one dash period should span so the EG reads dashed on screen and on paper.</summary>
        private const double EgDashPeriodM = 10.0;

        /// <summary>
        /// Makes the existing-ground line visibly dashed. Reads the pattern the profile would
        /// actually draw with (its own linetype, else its layer's) and sets the profile's
        /// LinetypeScale so one period spans <see cref="EgDashPeriodM"/> of station. When the
        /// effective linetype is continuous, the MahodAI dash pattern is applied to the profile
        /// itself — the firm's style/layer is never mutated, since it is shared with other views.
        /// </summary>
        private static void ApplyVisibleEgDash(Transaction tr, CivilDb.Profile p)
        {
            try
            {
                var db = p.Database ?? HostApplicationServices.WorkingDatabase;
                ObjectId ltId = p.LinetypeId;
                bool viaLayer = false;
                if (ltId.IsNull || string.Equals(p.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                {
                    if (tr.GetObject(db.LayerTableId, OpenMode.ForRead) is LayerTable lt && lt.Has(p.Layer) &&
                        tr.GetObject(lt[p.Layer], OpenMode.ForRead) is LayerTableRecord ltr)
                    {
                        ltId = ltr.LinetypeObjectId;
                        viaLayer = true;
                    }
                }

                double patternLength = 0;
                string ltName = "";
                if (!ltId.IsNull && tr.GetObject(ltId, OpenMode.ForRead) is LinetypeTableRecord rec)
                {
                    ltName = rec.Name;
                    if (rec.NumDashes > 1) patternLength = Math.Abs(rec.PatternLength);
                }

                if (patternLength <= 1e-6)
                {
                    // Continuous (or unreadable) — give the profile its own dashed pattern.
                    ObjectId dashId = GetOrLoadLinetype(db, tr, "MAHOD_PROF_DASH");
                    if (dashId.IsNull) return;
                    p.LinetypeId = dashId;
                    if (tr.GetObject(dashId, OpenMode.ForRead) is LinetypeTableRecord dashRec)
                        patternLength = Math.Abs(dashRec.PatternLength);
                    ltName = "MAHOD_PROF_DASH";
                    viaLayer = false;
                }
                if (patternLength <= 1e-6) return;

                double scale = Math.Round(EgDashPeriodM / patternLength, 3);
                if (scale <= 0) return;
                p.LinetypeScale = scale;
                Utilities.MahodLogger.Info(
                    $"[profile] EG '{p.Name}': dash '{ltName}'{(viaLayer ? " (from layer)" : "")} " +
                    $"period {patternLength:F2} m x ltscale {scale} = {EgDashPeriodM:F0} m per dash cycle.");
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ApplyVisibleEgDash failed: {ex.Message}"); }
        }

        /// <summary>Resolves a layer ObjectId by trying each name in order. Null if none present.</summary>
        private static ObjectId ResolveLayerByNames(Transaction tr, Database db, string[] names)
        {
            try
            {
                if (tr.GetObject(db.LayerTableId, OpenMode.ForRead) is LayerTable lt)
                    foreach (var want in names)
                        if (lt.Has(want)) return lt[want];
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ResolveLayerByNames failed: {ex.Message}"); }
            return ObjectId.Null;
        }

        /// <summary>
        /// Applies the resolved firm style + layer to a profile (new or reused). This is what
        /// actually lands the dashed olive EG / red FG: the firm EG style is ByLayer (so the
        /// LAYER drives its look), and the minimal CreateFromSurface overload can drop the layer
        /// argument — so both StyleId and Layer are set here explicitly. Best-effort.
        /// </summary>
        private static void ApplyProfileAppearance(Transaction tr, ObjectId profileId, ObjectId styleId, ObjectId layerId,
                                                   bool isEg = false)
        {
            if (profileId.IsNull) return;
            try
            {
                if (tr.GetObject(profileId, OpenMode.ForWrite) is not CivilDb.Profile p) return;
                if (!styleId.IsNull)
                {
                    try { p.StyleId = styleId; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] set Profile.StyleId failed: {ex.Message}"); }
                }
                if (!layerId.IsNull)
                {
                    try
                    {
                        if (tr.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord ltr)
                            p.Layer = ltr.Name;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] set Profile.Layer failed: {ex.Message}"); }
                }
                // Neutral linetype scale by default: an inherited CELTSCALE would otherwise
                // stretch or collapse the dash so it renders solid regardless of the pattern.
                try { p.LinetypeScale = 1.0; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] set Profile.LinetypeScale failed: {ex.Message}"); }

                // The EG line must READ as dashed at working zoom. The firm's layer (LS-Exist)
                // carries "dash2" = 2 m on / 2 m off — invisible on an 11 km profile, which is
                // why the engineer saw a solid line even though everything was "dashed" on paper
                // (measured in the live drawing 2026-07-28). Scale the profile's own linetype so
                // one dash period is ~10 m of station, i.e. ~1 cm at the usual 1:1000 plot.
                if (isEg) ApplyVisibleEgDash(tr, p);
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ApplyProfileAppearance failed: {ex.Message}"); }
        }

        /// <summary>
        /// Get-or-create a dedicated profile layer with the MahodAI convention:
        /// EG → green (3) dashed, FG → red (1) heavier. The ProfileStyle controls the
        /// rendered line; this layer is belt-and-suspenders for the style-API fallback path.
        /// Existing layers are re-synced to the convention (idempotent).
        /// </summary>
        private static ObjectId GetOrCreateProfileLayer(
            Transaction tr, Database db, bool isFg)
        {
            string layerName = isFg ? "C-ROAD-PROF-FG" : "C-ROAD-PROF-EG";
            short colorIndex = isFg ? (short)1 : (short)3;     // 1 red, 3 green
            string linetype = isFg ? "Continuous" : "MAHOD_PROF_DASH"; // FG solid, EG dashed
            LineWeight lw = isFg ? LineWeight.LineWeight050 : LineWeight.LineWeight018;

            try
            {
                var ltRead = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (ltRead == null) return db.Clayer;

                ObjectId ltId = GetOrLoadLinetype(db, tr, linetype);
                var color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);

                if (ltRead.Has(layerName))
                {
                    var existing = tr.GetObject(ltRead[layerName], OpenMode.ForWrite) as LayerTableRecord;
                    if (existing != null)
                    {
                        existing.Color = color;
                        if (!ltId.IsNull) existing.LinetypeObjectId = ltId;
                        existing.LineWeight = lw;
                    }
                    return ltRead[layerName];
                }

                var ltr = new LayerTableRecord
                {
                    Name = layerName,
                    Color = color,
                    LineWeight = lw
                };
                if (!ltId.IsNull) ltr.LinetypeObjectId = ltId;

                var ltWrite = tr.GetObject(db.LayerTableId, OpenMode.ForWrite) as LayerTable;
                if (ltWrite != null)
                {
                    ObjectId layerId = ltWrite.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    return layerId;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetOrCreateProfileLayer({layerName}) failed: {ex.Message}");
            }

            return db.Clayer;
        }

        /// <summary>
        /// Get-or-create a dedicated ProfileStyle with the MahodAI line convention:
        /// FG = solid red, heavier; EG = green, dense dashes. The style (not the layer)
        /// is what Civil 3D actually renders for the profile line, so this is the primary
        /// styling lever. Falls back to the indexed style if the style API is unavailable.
        /// </summary>
        private static ObjectId GetOrCreateMahodProfileStyle(
            CivilDocument civilDoc, Database db, Transaction tr, bool isFg)
        {
            string name = isFg ? "MahodAI FG (Red Line)" : "MahodAI EG (Existing)";
            short colorIndex = isFg ? (short)1 : (short)3;
            string linetype = isFg ? "Continuous" : "MAHOD_PROF_DASH";
            LineWeight lw = isFg ? LineWeight.LineWeight050 : LineWeight.LineWeight018;

            try
            {
                var styles = civilDoc.Styles.ProfileStyles;
                ObjectId styleId = ObjectId.Null;
                foreach (ObjectId id in styles)
                {
                    // Typed Name read — reflection on the style's Name getter throws
                    // "Property Get method was not found" on Civil 3D style objects.
                    if (tr.GetObject(id, OpenMode.ForRead)
                            is Autodesk.Civil.DatabaseServices.Styles.ProfileStyle existing
                        && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                    { styleId = id; break; }
                }
                if (styleId.IsNull)
                    styleId = styles.Add(name);

                var ps = tr.GetObject(styleId, OpenMode.ForWrite)
                    as Autodesk.Civil.DatabaseServices.Styles.ProfileStyle;
                if (ps != null)
                {
                    GetOrLoadLinetype(db, tr, linetype); // ensure linetype exists / is re-synced
                    var color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
                    ApplyProfileLineDisplay(ps, color, linetype, lw);
                }
                return styleId;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetOrCreateMahodProfileStyle({name}) failed: {ex.Message}");
                return GetProfileStyleByIndex(civilDoc, isFg ? 1 : 0);
            }
        }

        /// <summary>
        /// Sets the Line + Curve components of a ProfileStyle (Profile view direction) to the
        /// given color / linetype / lineweight. Property setters use reflection to tolerate
        /// member-name differences across Civil 3D versions; the display getter + enum are
        /// referenced directly so the build pins the correct names.
        /// </summary>
        private static void ApplyProfileLineDisplay(
            Autodesk.Civil.DatabaseServices.Styles.ProfileStyle ps,
            Autodesk.AutoCAD.Colors.Color color, string linetypeName, LineWeight lw)
        {
            var components = new[]
            {
                Autodesk.Civil.DatabaseServices.Styles.ProfileDisplayStyleProfileType.Line,
                Autodesk.Civil.DatabaseServices.Styles.ProfileDisplayStyleProfileType.Curve,
            };
            foreach (var comp in components)
            {
                try
                {
                    var disp = ps.GetDisplayStyleProfile(comp);
                    if (disp == null) continue;
                    disp.Visible = true;
                    disp.Color = color;
                    if (!string.IsNullOrEmpty(linetypeName)) disp.Linetype = linetypeName;
                    disp.Lineweight = lw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] ApplyProfileLineDisplay({comp}) failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Existing-ground dash, in metres — the SAME pattern the cross-sections draw
        /// (<c>CreateSectionViewsTool.DrawExistingGroundDashed</c>), so the longitudinal EG line
        /// reads like the EG line in the sections. It must stay SHORT: a profile sampled from a
        /// surface is a chain of short tangents and the linetype pattern restarts on every one,
        /// so any pattern longer than a typical tangent renders the whole line solid. The old
        /// rule (alignment length / 40, clamped 8…600 m) produced exactly that — solid where the
        /// ground is jagged, sparse dots where it is smooth.
        /// </summary>
        private const double EgDashLenM = 1.5;
        private const double EgGapLenM = 1.0;

        /// <summary>
        /// Get-or-load a linetype by name; returns Continuous if the load fails. The MahodAI
        /// profile dash is RE-SYNCED when it already exists — the record used to be created once
        /// with whatever period that run computed and then reused forever, so a stale pattern
        /// from an earlier (shorter) road survived into every later drawing.
        /// </summary>
        private static ObjectId GetOrLoadLinetype(Database db, Transaction tr, string name)
        {
            try
            {
                if (string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase))
                    return db.ContinuousLinetype;
                var lt = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                if (lt == null) return db.ContinuousLinetype;

                bool isProfDash = string.Equals(name, "MAHOD_PROF_DASH", StringComparison.OrdinalIgnoreCase);
                if (lt.Has(name))
                {
                    if (isProfDash) SyncProfileDashPattern(tr, lt[name]);
                    return lt[name];
                }

                if (isProfDash)
                {
                    lt.UpgradeOpen();
                    var rec = new LinetypeTableRecord
                    {
                        Name = name,
                        AsciiDescription = "MahodAI profile dashed _ _ _",
                        PatternLength = EgDashLenM + EgGapLenM,
                        NumDashes = 2,
                    };
                    rec.SetDashLengthAt(0, EgDashLenM);
                    rec.SetDashLengthAt(1, -EgGapLenM);
                    var newId = lt.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                    return newId;
                }

                try { db.LoadLineTypeFile(name, "acadiso.lin"); }
                catch { try { db.LoadLineTypeFile(name, "acad.lin"); } catch { } }
                lt = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                if (lt != null && lt.Has(name)) return lt[name];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetOrLoadLinetype('{name}') failed: {ex.Message}");
            }
            return db.ContinuousLinetype;
        }

        /// <summary>
        /// Rewrites an existing MAHOD_PROF_DASH record to the current dash convention. Idempotent
        /// and best-effort: a drawing carrying the old long-period pattern is repaired in place,
        /// which is what makes the EG line actually read dashed on a re-run.
        /// </summary>
        private static void SyncProfileDashPattern(Transaction tr, ObjectId linetypeId)
        {
            try
            {
                if (tr.GetObject(linetypeId, OpenMode.ForRead) is not LinetypeTableRecord rec) return;
                double want = EgDashLenM + EgGapLenM;
                bool upToDate = rec.NumDashes == 2
                    && Math.Abs(rec.PatternLength - want) < 1e-6
                    && Math.Abs(rec.DashLengthAt(0) - EgDashLenM) < 1e-6
                    && Math.Abs(rec.DashLengthAt(1) + EgGapLenM) < 1e-6;
                if (upToDate) return;

                rec.UpgradeOpen();
                rec.NumDashes = 2;
                rec.PatternLength = want;
                rec.SetDashLengthAt(0, EgDashLenM);
                rec.SetDashLengthAt(1, -EgGapLenM);
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] SyncProfileDashPattern failed: {ex.Message}"); }
        }

        private static string BuildResultMessage(
            string egName, string egMethod,
            string fgName, string fgMethod,
            int fgPviCount)
        {
            var parts = new List<string>();

            if (egMethod == "already_existed")
                parts.Add($"EG profile '{egName}' already existed — reused.");
            else if (egMethod.StartsWith("API_"))
                parts.Add($"EG profile '{egName}' created from surface via managed API.");
            else if (egMethod.StartsWith("LISP_"))
                parts.Add($"EG profile '{egName}' creation queued via LISP command.");

            if (fgMethod == "not_requested")
                parts.Add("FG profile was not requested.");
            else if (fgMethod == "skipped_insufficient_pvi_points")
                parts.Add("FG profile skipped — need at least 2 PVI points.");
            else if (fgMethod == "already_existed")
                parts.Add($"FG profile '{fgName}' already existed — reused ({fgPviCount} PVIs).");
            else if (fgMethod.StartsWith("API_"))
                parts.Add($"FG profile '{fgName}' created with {fgPviCount} PVI points.");
            else if (fgMethod.StartsWith("LISP_"))
                parts.Add($"FG profile '{fgName}' creation queued via LISP. PVI points need to be added manually.");

            return string.Join(" ", parts);
        }
    }
}
