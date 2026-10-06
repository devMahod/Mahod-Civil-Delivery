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

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Re-points an existing corridor baseline-region to a DIFFERENT assembly
    /// (cross-section template) and rebuilds the corridor.
    ///
    /// Why this tool exists: Civil 3D's .NET API cannot author subassemblies
    /// (lanes / shoulders / curbs) from scratch. So genuinely upgrading a road's
    /// section — e.g. a single-lane corridor → a 2-lane or divided section — is
    /// done by swapping the WHOLE assembly on the region, not by "adding a lane".
    /// The agent's "expand road" flow first calls <c>clone_assembly_from_library</c>
    /// to bring in a complete 2-lane/divided assembly, then calls this tool to
    /// re-point the region at it.
    ///
    /// Swap strategy (both attempted, first that works wins):
    ///  1. Set <c>BaselineRegion.AssemblyId</c> directly if the API exposes it as
    ///     writable (version-dependent — probed via reflection).
    ///  2. Otherwise remove the region(s) and re-add via
    ///     <c>BaselineRegions.Add(name, newAssemblyId, startStation, endStation)</c>
    ///     preserving each region's name + station range.
    ///
    /// After the swap the EG surface target(s) are re-applied (a fresh assembly
    /// starts with no targets, so daylight links would never reach EG otherwise),
    /// then <c>corridor.Rebuild()</c> runs. A failed rebuild fails the tool — same
    /// contract as <see cref="Creation.CreateCorridorTool"/> — so the transaction
    /// aborts and the drawing is left unmodified.
    /// </summary>
    public class SetRegionAssemblyTool : DrawingToolBase
    {
        public override string Name => "set_region_assembly";
        public override string Description =>
            "Re-points an existing corridor region to a different assembly (cross-section template) " +
            "and rebuilds the corridor. Use to change a road's section — e.g. after " +
            "clone_assembly_from_library provides a 2-lane or divided assembly, swap the region onto it. " +
            "baseline_index defaults to 0; omit region_index to swap every region of the baseline.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""corridor_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the corridor to modify.""
                },
                ""assembly_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the assembly to apply to the region(s). Must already exist in the drawing (create it first via clone_assembly_from_library or create_assembly).""
                },
                ""baseline_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Index of the baseline (default 0 — the main centerline).""
                },
                ""region_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Index of a single region to swap. Omit to swap ALL regions of the baseline.""
                },
                ""target_surface_name"": {
                    ""type"": ""string"",
                    ""description"": ""Surface to re-apply as the daylight target after the swap (optional — the region's existing EG target is preserved automatically, else the first TIN surface is auto-detected).""
                }
            },
            ""required"": [""corridor_name"", ""assembly_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var assemblyName = GetRequiredStringParam(parameters, "assembly_name");
            var baselineIndex = GetIntParam(parameters, "baseline_index") ?? 0;
            var regionIndex = GetIntParam(parameters, "region_index"); // null = all regions
            var targetSurfaceName = GetStringParam(parameters, "target_surface_name");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // ── Find the corridor ────────────────────────────────────────
            ObjectId corridorId = ObjectId.Null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Corridor;
                if (obj != null && obj.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridorId = id;
                    break;
                }
            }
            if (corridorId == ObjectId.Null)
                return ToolResult.NotFound("Corridor", corridorName);

            // ── Find the target assembly ─────────────────────────────────
            ObjectId newAssemblyId = ObjectId.Null;
            foreach (ObjectId id in civilDoc.AssemblyCollection)
            {
                var asm = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Assembly;
                if (asm != null && asm.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    newAssemblyId = id;
                    break;
                }
            }
            if (newAssemblyId == ObjectId.Null)
                return ToolResult.NotFound("Assembly", assemblyName);

            // ── Reopen corridor for write ────────────────────────────────
            var corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as CivilDb.Corridor;
            if (corridor == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open corridor for write");

            if (baselineIndex < 0 || baselineIndex >= corridor.Baselines.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Baseline index {baselineIndex} out of range (0-{corridor.Baselines.Count - 1})");

            var baseline = corridor.Baselines[baselineIndex];
            int regionCount = baseline.BaselineRegions.Count;
            if (regionCount == 0)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Baseline {baselineIndex} of corridor '{corridorName}' has no regions to swap.");

            if (regionIndex.HasValue && (regionIndex.Value < 0 || regionIndex.Value >= regionCount))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Region index {regionIndex.Value} out of range (0-{regionCount - 1})");

            // ── Snapshot the regions we intend to swap ───────────────────
            // Capture name / stations / any Surface target ids BEFORE mutating,
            // so we can re-add + re-target faithfully if the direct AssemblyId
            // set is not supported.
            var targets = new List<RegionSnapshot>();
            for (int i = 0; i < regionCount; i++)
            {
                if (regionIndex.HasValue && i != regionIndex.Value)
                    continue;

                var region = baseline.BaselineRegions[i];
                var snap = new RegionSnapshot
                {
                    Index = i,
                    Name = SafeRegionName(region, i, corridorName),
                    OldAssemblyName = ResolveAssemblyName(tr, region),
                    SurfaceTargetIds = CaptureSurfaceTargetIds(region),
                };
                TryReadStations(region, snap);
                targets.Add(snap);
            }

            // ── Resolve the surface to re-apply as daylight target ───────
            // Preference: explicit param → a surface captured off an old region →
            // first non-corridor TIN surface in the drawing.
            ObjectId surfaceId = ResolveTargetSurface(tr, civilDoc, targetSurfaceName, targets);

            // ── Perform the swap ─────────────────────────────────────────
            string swapMethod;
            try
            {
                if (TrySetAssemblyIdDirect(baseline, targets, newAssemblyId, out swapMethod))
                {
                    // direct property set succeeded on every target region
                }
                else
                {
                    RemoveAndReAddRegions(baseline, targets, newAssemblyId);
                    swapMethod = "remove_readd";
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to re-point region assembly: {ex.Message}");
            }

            // ── Re-apply the EG surface target on the (now-swapped) regions ─
            int surfaceTargetsAssigned = 0;
            string? targetError = null;
            if (surfaceId != ObjectId.Null)
            {
                try
                {
                    surfaceTargetsAssigned = ReapplySurfaceTargets(baseline, surfaceId);
                }
                catch (Exception ex)
                {
                    targetError = ex.Message;
                }
            }

            // ── Rebuild — a failed rebuild fails the tool (txn aborts) ────
            bool rebuilt;
            string? rebuildError = null;
            try
            {
                corridor.Rebuild();
                rebuilt = true;
            }
            catch (Exception ex)
            {
                rebuilt = false;
                rebuildError = ex.Message;
            }

            if (!rebuilt)
            {
                return ToolResult.Fail(
                    "CORRIDOR_REBUILD_FAILED",
                    $"החלפת ה-Assembly ב-'{corridorName}' נכשלה בשלב ה-Rebuild: {rebuildError}. " +
                    "המסדרון לא עודכן — ודא שה-Assembly '" + assemblyName + "' תואם לפרופיל ולמשטח המטרה.",
                    JsonSerializer.Serialize(new { rebuild_error = rebuildError, swap_method = swapMethod }));
            }

            // ── Invalidate caches ────────────────────────────────────────
            cache.RemoveByPattern("list_corridors:");
            cache.RemoveByPattern("get_corridor_info:");
            cache.RemoveByPattern("get_corridor_cross_section:");
            cache.RemoveByPattern("get_drawing_summary:");

            var swappedRegions = targets.Select(t => new
            {
                index = t.Index,
                name = t.Name,
                old_assembly = t.OldAssemblyName,
            }).ToList();

            return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
            {
                ["success"] = true,
                ["corridor_name"] = corridorName,
                ["assembly_name"] = assemblyName,
                ["baseline_index"] = baselineIndex,
                ["regions_swapped"] = targets.Count,
                ["swapped_regions"] = swappedRegions,
                ["swap_method"] = swapMethod,
                ["surface_targets_assigned"] = surfaceTargetsAssigned,
                ["target_assignment_error"] = targetError!,
                ["rebuilt"] = true,
                ["message"] =
                    $"מסדרון '{corridorName}' עודכן: {targets.Count} מקטע(ים) הוחלפו ל-Assembly '{assemblyName}' " +
                    $"({surfaceTargetsAssigned} יעדי משטח שויכו מחדש). המסדרון נבנה מחדש."
            }));
        }

        // ─────────────────────────────────────────────────────────────────
        // Region snapshot + helpers
        // ─────────────────────────────────────────────────────────────────

        private sealed class RegionSnapshot
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
            public string? OldAssemblyName { get; set; }
            public bool HasStations { get; set; }
            public double StartStation { get; set; }
            public double EndStation { get; set; }
            public ObjectIdCollection? SurfaceTargetIds { get; set; }
        }

        private static string SafeRegionName(CivilDb.BaselineRegion region, int index, string corridorName)
        {
            try
            {
                if (!string.IsNullOrEmpty(region.Name)) return region.Name;
            }
            catch { }
            return $"Region {index} - {corridorName}";
        }

        private static string? ResolveAssemblyName(Transaction tr, CivilDb.BaselineRegion region)
        {
            try
            {
                var prop = region.GetType().GetProperty("AssemblyId");
                if (prop?.GetValue(region) is ObjectId asmId && asmId != ObjectId.Null)
                {
                    var asm = tr.GetObject(asmId, OpenMode.ForRead) as CivilDb.Assembly;
                    return asm?.Name;
                }
            }
            catch { }
            return null;
        }

        private static void TryReadStations(CivilDb.BaselineRegion region, RegionSnapshot snap)
        {
            try
            {
                var startProp = region.GetType().GetProperty("StartStation");
                var endProp = region.GetType().GetProperty("EndStation");
                if (startProp != null && endProp != null)
                {
                    snap.StartStation = Convert.ToDouble(startProp.GetValue(region));
                    snap.EndStation = Convert.ToDouble(endProp.GetValue(region));
                    snap.HasStations = snap.EndStation > snap.StartStation;
                }
            }
            catch { }
        }

        /// <summary>Capture the EG surface ids off any Surface-type target so we can
        /// re-apply them after the swap (a swapped-in assembly has empty targets).</summary>
        private static ObjectIdCollection? CaptureSurfaceTargetIds(CivilDb.BaselineRegion region)
        {
            try
            {
                var targets = region.GetTargets();
                foreach (CivilDb.SubassemblyTargetInfo target in targets)
                {
                    if (target.TargetType == CivilDb.SubassemblyLogicalNameType.Surface &&
                        target.TargetIds != null && target.TargetIds.Count > 0)
                    {
                        var copy = new ObjectIdCollection();
                        foreach (ObjectId oid in target.TargetIds) copy.Add(oid);
                        return copy;
                    }
                }
            }
            catch { }
            return null;
        }

        private static ObjectId ResolveTargetSurface(
            Transaction tr, CivilDocument civilDoc, string? targetSurfaceName,
            List<RegionSnapshot> targets)
        {
            // 1. Explicit name wins.
            if (!string.IsNullOrEmpty(targetSurfaceName))
            {
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    try
                    {
                        var surf = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Surface;
                        if (surf != null && surf.Name.Equals(targetSurfaceName, StringComparison.OrdinalIgnoreCase))
                            return id;
                    }
                    catch { }
                }
            }

            // 2. A surface captured off one of the old regions (preserve EG).
            foreach (var t in targets)
            {
                if (t.SurfaceTargetIds != null && t.SurfaceTargetIds.Count > 0)
                    return t.SurfaceTargetIds[0];
            }

            // 3. First non-corridor TIN surface (same heuristic as CreateCorridorTool).
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.TinSurface tin)
                    {
                        var n = tin.Name ?? "";
                        if (!n.Contains("_Top") && !n.Contains("_Bot") &&
                            !n.Contains("Corridor") && !n.Contains("- EG"))
                            return id;
                    }
                }
                catch { }
            }
            return ObjectId.Null;
        }

        /// <summary>Attempt the clean path: set <c>BaselineRegion.AssemblyId</c> directly.
        /// Returns true only if the property is writable AND a read-back confirms the
        /// new value actually took on every target region. The read-back matters:
        /// some Civil 3D versions expose a writable-looking AssemblyId that silently
        /// no-ops — without the check we'd report success on an unchanged section.</summary>
        private static bool TrySetAssemblyIdDirect(
            CivilDb.Baseline baseline, List<RegionSnapshot> targets, ObjectId newAssemblyId,
            out string method)
        {
            method = "set_assembly_id";
            PropertyInfo? prop = typeof(CivilDb.BaselineRegion).GetProperty("AssemblyId");
            if (prop == null || !prop.CanWrite)
                return false;

            foreach (var t in targets)
            {
                var region = baseline.BaselineRegions[t.Index];
                try
                {
                    prop.SetValue(region, newAssemblyId);
                    // Read back — a silent no-op means this path is unusable here.
                    if (prop.GetValue(region) is not ObjectId applied || applied != newAssemblyId)
                        return false;
                }
                catch { return false; } // fall through to remove/re-add
            }
            return true;
        }

        /// <summary>Fallback swap: remove the target regions and re-add them over the
        /// same station range with the new assembly. Removes high→low index so the
        /// collection indices of not-yet-processed regions stay valid.</summary>
        private static void RemoveAndReAddRegions(
            CivilDb.Baseline baseline, List<RegionSnapshot> targets, ObjectId newAssemblyId)
        {
            var regions = baseline.BaselineRegions;

            // Locate an Add(name, assemblyId, start, end) overload once.
            var add4 = regions.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 4 &&
                                     m.GetParameters()[0].ParameterType == typeof(string) &&
                                     m.GetParameters()[2].ParameterType == typeof(double) &&
                                     m.GetParameters()[3].ParameterType == typeof(double));

            // Removal method: RemoveAt(int) preferred, else Remove(BaselineRegion).
            var removeAt = regions.GetType().GetMethod("RemoveAt", new[] { typeof(int) });

            foreach (var t in targets.OrderByDescending(x => x.Index))
            {
                if (removeAt != null)
                {
                    removeAt.Invoke(regions, new object[] { t.Index });
                }
                else
                {
                    var region = regions[t.Index];
                    var remove = regions.GetType().GetMethod("Remove", new[] { region.GetType() });
                    if (remove == null)
                        throw new InvalidOperationException(
                            "BaselineRegionCollection exposes neither a writable AssemblyId nor a Remove/RemoveAt method — cannot swap assembly on this Civil 3D version.");
                    remove.Invoke(regions, new object[] { region });
                }
            }

            foreach (var t in targets.OrderBy(x => x.Index))
            {
                if (t.HasStations && add4 != null)
                {
                    add4.Invoke(regions, new object[] { t.Name, newAssemblyId, t.StartStation, t.EndStation });
                }
                else
                {
                    regions.Add(t.Name, newAssemblyId);
                }
            }
        }

        /// <summary>Re-assign the EG surface to every Surface-type target across all
        /// regions of the baseline. Mirrors CreateCorridorTool.SetCorridorTargetSurface:
        /// ONLY Surface targets are touched (Elevation/Offset need Profile/Alignment ids
        /// and would throw).</summary>
        private static int ReapplySurfaceTargets(CivilDb.Baseline baseline, ObjectId surfaceId)
        {
            var surfaceIds = new ObjectIdCollection { surfaceId };
            int totalAssigned = 0;

            foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
            {
                CivilDb.SubassemblyTargetInfoCollection targets;
                try { targets = region.GetTargets(); }
                catch { continue; }

                int assigned = 0;
                foreach (CivilDb.SubassemblyTargetInfo target in targets)
                {
                    if (target.TargetType == CivilDb.SubassemblyLogicalNameType.Surface)
                    {
                        try { target.TargetIds = surfaceIds; assigned++; }
                        catch { }
                    }
                }
                if (assigned > 0)
                {
                    region.SetTargets(targets);
                    totalAssigned += assigned;
                }
            }
            return totalAssigned;
        }
    }
}
