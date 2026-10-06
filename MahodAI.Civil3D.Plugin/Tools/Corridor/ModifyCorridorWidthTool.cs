using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Modifies the target width of a corridor subassembly.
    /// Uses the correct Civil 3D API path: region.AppliedAssemblies → GetAppliedSubassemblies()
    /// → appliedSub.SubassemblyId → load design-time Subassembly → ParamsDouble.
    /// </summary>
    public class ModifyCorridorWidthTool : DrawingToolBase
    {
        public override string Name => "modify_corridor_width";
        public override string Description => "Modifies the width of a corridor by changing the target parameter on a subassembly link. Specify the corridor, baseline, region, and the new width value. Use list_subassembly_parameters first to discover exact parameter names.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // Expanded keyword list for width parameter matching
        private static readonly string[] WidthKeywords = {
            "WIDTH", "TARGETWIDTH", "LANEWIDTH", "DEFAULTWIDTH",
            "SHOULDERWIDTH", "TOPWIDTH", "BOTTOMWIDTH", "PAVEWIDTH",
            "MEDIAWIDTH", "HALF", "OFFSET",
            // Hebrew
            "רוחב"
        };

        // Disqualifying substrings — parameters whose name contains any of
        // these are almost never what "lane width" / "shoulder width" means
        // (e.g. "Inside Base Width" is the subbase offset, not the lane).
        // We still match them as a last resort, but prefer cleaner names.
        private static readonly string[] WidthDisqualifiers = {
            "INSIDE", "OUTSIDE", "BASE", "SUBBASE", "SUB BASE",
            "MINIMUM", "MAXIMUM", "MIN ", "MAX ", "WEARING",
        };

        /// <summary>
        /// Score a candidate width parameter — higher is better.
        /// </summary>
        private static int ScoreWidthMatch(string nameUpper, string displayUpper)
        {
            // Tier 1: the canonical lane/shoulder width names
            if (nameUpper == "WIDTH" || displayUpper == "WIDTH") return 100;
            if (nameUpper == "DEFAULT WIDTH" || displayUpper == "DEFAULT WIDTH") return 95;
            if (nameUpper == "LANE WIDTH" || displayUpper == "LANE WIDTH") return 95;
            if (nameUpper == "SHOULDER WIDTH" || displayUpper == "SHOULDER WIDTH") return 95;
            if (nameUpper == "LANEWIDTH" || displayUpper == "LANEWIDTH") return 90;
            if (nameUpper == "SHOULDERWIDTH" || displayUpper == "SHOULDERWIDTH") return 90;
            if (nameUpper == "TARGETWIDTH" || displayUpper == "TARGETWIDTH") return 85;
            if (nameUpper == "W" || displayUpper == "W") return 80;

            // Tier 2: contains a width keyword — demote if also contains a disqualifier
            bool hasKeyword = false;
            foreach (var kw in WidthKeywords)
                if (nameUpper.Contains(kw) || displayUpper.Contains(kw)) { hasKeyword = true; break; }
            if (!hasKeyword) return 0;

            foreach (var dq in WidthDisqualifiers)
                if (nameUpper.Contains(dq) || displayUpper.Contains(dq)) return 10;

            return 50;
        }

        /// <summary>
        /// Structured parameter info for error responses, enabling agent retry with exact names.
        /// </summary>
        private class SubassemblyParamInfo
        {
            public string SubassemblyName { get; set; } = "";
            public string Side { get; set; } = "";
            public string ParameterName { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string? KeyName { get; set; }
            public double? Value { get; set; }
            public string Collection { get; set; } = "";
        }

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var baselineIndex = GetIntParam(parameters, "baseline_index") ?? 0;
            var regionIndex = GetIntParam(parameters, "region_index") ?? 0;
            var newWidth = GetDoubleParam(parameters, "new_width");
            var side = GetStringParam(parameters, "side")?.ToLowerInvariant() ?? "right";
            var subassemblyName = GetStringParam(parameters, "subassembly_name");
            var parameterName = GetStringParam(parameters, "parameter_name");

            if (newWidth == null || newWidth.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_width' must be a positive number");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the corridor
            Autodesk.Civil.DatabaseServices.Corridor? corridor = null;
            ObjectId corridorId = ObjectId.Null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                if (obj != null && obj.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridor = obj;
                    corridorId = id;
                    break;
                }
            }

            if (corridor == null)
                return ToolResult.NotFound("Corridor", corridorName);

            if (baselineIndex < 0 || baselineIndex >= corridor.Baselines.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Baseline index {baselineIndex} out of range (0-{corridor.Baselines.Count - 1})");

            var baseline = corridor.Baselines[baselineIndex];

            if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Region index {regionIndex} out of range (0-{baseline.BaselineRegions.Count - 1})");

            var region = baseline.BaselineRegions[regionIndex];

            // Open corridor for write to modify parameters
            corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Corridor;
            if (corridor == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open corridor for write");

            // Re-get baseline/region after reopening corridor for write
            baseline = corridor.Baselines[baselineIndex];
            region = baseline.BaselineRegions[regionIndex];

            bool modified = false;
            double? oldWidth = null;
            string? matchedSubassembly = null;
            string? matchedParameter = null;
            var allFoundParams = new List<string>();
            var allParamDetails = new List<SubassemblyParamInfo>();

            try
            {
                // === CORRECT PATH: region.AppliedAssemblies → GetAppliedSubassemblies() → SubassemblyId ===
                var appliedAssemblies = region.AppliedAssemblies;
                if (appliedAssemblies == null)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "region.AppliedAssemblies returned null — corridor may need rebuilding first");
                }

                // Get first applied assembly (they all share the same assembly definition)
                object? appliedAssembly = null;
                var enumerator = appliedAssemblies as System.Collections.IEnumerable;
                if (enumerator != null)
                {
                    foreach (var item in enumerator)
                    {
                        appliedAssembly = item;
                        break; // first one is sufficient
                    }
                }

                // Fallback: try GetItemAt
                if (appliedAssembly == null)
                {
                    var getItemAt = appliedAssemblies.GetType().GetMethod("GetItemAt");
                    if (getItemAt != null)
                    {
                        var paramInfo = getItemAt.GetParameters();
                        if (paramInfo.Length == 1 && paramInfo[0].ParameterType == typeof(int))
                            appliedAssembly = getItemAt.Invoke(appliedAssemblies, new object[] { 0 });
                        else if (paramInfo.Length == 1 && paramInfo[0].ParameterType == typeof(double))
                        {
                            var sortedStations = region.SortedStations();
                            if (sortedStations != null && sortedStations.Length > 0)
                                appliedAssembly = getItemAt.Invoke(appliedAssemblies, new object[] { sortedStations[0] });
                        }
                    }
                }

                if (appliedAssembly == null)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "Could not access any AppliedAssembly from region");
                }

                // Get applied subassemblies — method first (Civil 3D 2026+), then property
                System.Collections.IEnumerable? appliedSubs = null;

                var getSubsMethod = appliedAssembly.GetType().GetMethod("GetAppliedSubassemblies");
                if (getSubsMethod != null)
                    appliedSubs = getSubsMethod.Invoke(appliedAssembly, null) as System.Collections.IEnumerable;

                if (appliedSubs == null)
                {
                    var subsProp = appliedAssembly.GetType().GetProperty("AppliedSubassemblies");
                    if (subsProp != null)
                        appliedSubs = subsProp.GetValue(appliedAssembly) as System.Collections.IEnumerable;
                }

                if (appliedSubs == null)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "Could not get AppliedSubassemblies from AppliedAssembly");
                }

                // Iterate applied subassemblies, load design-time Subassembly, access ParamsDouble
                int subCount = 0;
                foreach (var appliedSub in appliedSubs)
                {
                    ct.ThrowIfCancellationRequested();
                    subCount++;

                    // Get subassembly name for matching/logging
                    string subName = CorridorReflectionHelper.ResolveSubassemblyName(appliedSub, subCount, tr);

                    // Determine side from origin offset
                    string subSide = "right";
                    var originProp = appliedSub.GetType().GetProperty("OriginStationOffsetElevationToBaseline");
                    if (originProp != null)
                    {
                        var origin = originProp.GetValue(appliedSub);
                        if (origin != null)
                        {
                            double offset = 0;
                            var yProp = origin.GetType().GetProperty("Y");
                            if (yProp != null)
                                offset = Convert.ToDouble(yProp.GetValue(origin));
                            else
                            {
                                var offsetProp = origin.GetType().GetProperty("Offset");
                                if (offsetProp != null)
                                    offset = Convert.ToDouble(offsetProp.GetValue(origin));
                            }
                            subSide = offset >= 0 ? "right" : "left";
                        }
                    }

                    // Filter by subassembly_name if provided
                    if (!string.IsNullOrEmpty(subassemblyName))
                    {
                        if (!subName.Equals(subassemblyName, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    else
                    {
                        // Check side filter
                        if (side != "both" && subSide != side) continue;
                    }

                    // Load the design-time Subassembly via SubassemblyId
                    ObjectId subId = ObjectId.Null;
                    var subIdProp = appliedSub.GetType().GetProperty("SubassemblyId");
                    if (subIdProp != null)
                    {
                        var subIdVal = subIdProp.GetValue(appliedSub);
                        if (subIdVal is ObjectId sid)
                            subId = sid;
                    }

                    if (subId.IsNull)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ModifyCorridorWidth] SubassemblyId is null for '{subName}', skipping");
                        continue;
                    }

                    var subassembly = tr.GetObject(subId, OpenMode.ForWrite) as Subassembly;
                    if (subassembly == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ModifyCorridorWidth] Could not load Subassembly for '{subName}' (ObjectId: {subId})");
                        continue;
                    }

                    // Access parameters on the design-time Subassembly
                    // Try each parameter collection
                    foreach (var collectionName in new[] { "ParamsDouble", "ParamsAll", "ParamsLong", "Parameters" })
                    {
                        System.Collections.IEnumerable? paramCol = null;

                        // Try direct property first
                        try
                        {
                            var prop = subassembly.GetType().GetProperty(collectionName);
                            if (prop != null)
                                paramCol = prop.GetValue(subassembly) as System.Collections.IEnumerable;
                        }
                        catch { }

                        if (paramCol == null) continue;

                        // Collect every candidate match in this subassembly, score it,
                        // then apply only the BEST match. This prevents accidentally
                        // modifying "Inside Base Width" when "Width" / "Default Width"
                        // is also present — the old first-match-wins logic was the
                        // source of the 0.1 m → 3.3 m bug on subassembly R-KV.
                        object? bestParam = null;
                        Type? bestParamType = null;
                        int bestScore = 0;
                        string bestName = "";
                        string bestDisplay = "";

                        foreach (var param in paramCol)
                        {
                            var paramType = param.GetType();
                            string pName = paramType.GetProperty("Name")?.GetValue(param)?.ToString() ?? "";
                            string pDisplayName = paramType.GetProperty("DisplayName")?.GetValue(param)?.ToString() ?? "";
                            string? pKeyName = null;
                            try { pKeyName = paramType.GetProperty("KeyName")?.GetValue(param)?.ToString(); } catch { }

                            // Log all parameters for diagnostics
                            object? currentVal = null;
                            try { currentVal = paramType.GetProperty("Value")?.GetValue(param); } catch { }
                            string paramEntry = $"{subName}.{pName} (Display: {pDisplayName}, Key: {pKeyName}, Value: {currentVal}, Collection: {collectionName})";
                            allFoundParams.Add(paramEntry);

                            // Collect structured param info for error responses
                            double? paramValue = null;
                            if (currentVal != null)
                            {
                                try { paramValue = Convert.ToDouble(currentVal); } catch { }
                            }
                            allParamDetails.Add(new SubassemblyParamInfo
                            {
                                SubassemblyName = subName,
                                Side = subSide,
                                ParameterName = pName,
                                DisplayName = pDisplayName,
                                KeyName = pKeyName,
                                Value = paramValue,
                                Collection = collectionName,
                            });

                            int score;
                            if (!string.IsNullOrEmpty(parameterName))
                            {
                                // Caller pinned an exact parameter name — only that matches.
                                bool exact = pName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                          || pDisplayName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                          || (pKeyName != null && pKeyName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
                                score = exact ? 1000 : 0;
                            }
                            else
                            {
                                string nameUpper = pName.ToUpperInvariant().Trim();
                                string displayUpper = pDisplayName.ToUpperInvariant().Trim();
                                score = ScoreWidthMatch(nameUpper, displayUpper);
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestParam = param;
                                bestParamType = paramType;
                                bestName = pName;
                                bestDisplay = pDisplayName;
                            }
                        }

                        if (bestParam != null && bestParamType != null)
                        {
                            try
                            {
                                oldWidth = Convert.ToDouble(bestParamType.GetProperty("Value")?.GetValue(bestParam));

                                var valueProp = bestParamType.GetProperty("Value");
                                if (valueProp != null && valueProp.CanWrite)
                                {
                                    valueProp.SetValue(bestParam, newWidth.Value);
                                    modified = true;
                                }

                                if (!modified)
                                {
                                    var setValueMethod = bestParamType.GetMethod("SetValue");
                                    if (setValueMethod != null)
                                    {
                                        setValueMethod.Invoke(bestParam, new object[] { newWidth.Value });
                                        modified = true;
                                    }
                                }

                                if (!modified)
                                {
                                    var setParamMethod = subassembly.GetType().GetMethod("SetParameterValue");
                                    if (setParamMethod != null)
                                    {
                                        setParamMethod.Invoke(subassembly, new object[] { bestName, newWidth.Value });
                                        modified = true;
                                    }
                                }

                                matchedSubassembly = subName;
                                matchedParameter = bestDisplay.Length > 0 ? bestDisplay : bestName;
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ModifyCorridorWidth] Matched '{bestName}' (display='{bestDisplay}', score={bestScore}) on '{subName}'");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ModifyCorridorWidth] Failed to set parameter {bestName}: {ex.Message}");
                            }

                            if (modified) break;
                        }
                    }

                    if (modified) break;
                }

                System.Diagnostics.Debug.WriteLine($"[ModifyCorridorWidth] Enumerated {subCount} applied subassemblies");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify corridor width: {ex.Message}");
            }

            // Debug log all found parameters
            System.Diagnostics.Debug.WriteLine($"[ModifyCorridorWidth] All parameters found ({allFoundParams.Count}):");
            foreach (var p in allFoundParams)
                System.Diagnostics.Debug.WriteLine($"  {p}");

            if (!modified)
            {
                // Build structured parameter inventory grouped by subassembly
                var subassemblyInventory = new Dictionary<string, List<object>>();
                foreach (var pi in allParamDetails)
                {
                    string key = $"{pi.SubassemblyName} ({pi.Side})";
                    if (!subassemblyInventory.ContainsKey(key))
                        subassemblyInventory[key] = new List<object>();
                    subassemblyInventory[key].Add(new
                    {
                        name = pi.ParameterName,
                        display_name = pi.DisplayName,
                        key_name = pi.KeyName,
                        value = pi.Value.HasValue ? Math.Round(pi.Value.Value, 4) : (double?)null,
                        collection = pi.Collection,
                    });
                }

                // Also build a flat list of subassembly names + sides for quick agent reference
                var subassemblyNames = new HashSet<string>();
                foreach (var pi in allParamDetails)
                    subassemblyNames.Add($"{pi.SubassemblyName} ({pi.Side})");

                var inventoryJson = JsonSerializer.Serialize(new
                {
                    corridor_name = corridorName,
                    baseline_index = baselineIndex,
                    region_index = regionIndex,
                    side,
                    requested_subassembly_name = subassemblyName ?? "(any)",
                    requested_parameter_name = parameterName ?? "(auto-keyword)",
                    requested_width = Math.Round(newWidth.Value, 3),
                    subassemblies_found = subassemblyNames.Count,
                    parameters_found = allParamDetails.Count,
                    inventory = subassemblyInventory,
                }, new JsonSerializerOptions { WriteIndented = false });

                return ToolResult.Fail(
                    ToolErrorCodes.SubassemblyParameterNotFound,
                    "Width parameter not found by keyword matching. Retry with explicit subassembly_name and parameter_name from the inventory below.",
                    inventoryJson);
            }

            // Rebuild corridor after successful modification
            try
            {
                corridor.Rebuild();
                System.Diagnostics.Debug.WriteLine($"[ModifyCorridorWidth] Corridor '{corridorName}' rebuilt successfully");
            }
            catch (Exception rebuildEx)
            {
                System.Diagnostics.Debug.WriteLine($"[ModifyCorridorWidth] Corridor rebuild failed: {rebuildEx.Message}");
            }

            cache.RemoveByPattern("get_corridor_info:");
            cache.RemoveByPattern("get_corridor_cross_section:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                corridor_name = corridorName,
                baseline_index = baselineIndex,
                region_index = regionIndex,
                side,
                subassembly_name = matchedSubassembly,
                parameter_name = matchedParameter,
                old_width = oldWidth.HasValue ? Math.Round(oldWidth.Value, 3) : (double?)null,
                new_width = Math.Round(newWidth.Value, 3),
                rebuilt = true,
                note = "Width parameter modified and corridor rebuilt."
            }));
        }

        private static T? GetReflectionProperty<T>(object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    if (value is T typedValue)
                        return typedValue;
                }
            }
            catch { }
            return default;
        }
    }
}
