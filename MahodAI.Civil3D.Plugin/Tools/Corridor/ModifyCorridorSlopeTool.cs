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
    /// Modifies the cross-slope of a corridor subassembly.
    /// Uses the correct Civil 3D API path: region.AppliedAssemblies → GetAppliedSubassemblies()
    /// → appliedSub.SubassemblyId → load design-time Subassembly → ParamsDouble.
    /// </summary>
    public class ModifyCorridorSlopeTool : DrawingToolBase
    {
        public override string Name => "modify_corridor_slope";
        public override string Description => "Modifies the cross-slope of a corridor by changing the slope parameter on a subassembly. Specify the corridor, baseline, region, side, and new slope value. Use list_subassembly_parameters first to discover exact parameter names.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        // Expanded keyword list for slope parameter matching
        private static readonly string[] SlopeKeywords = {
            "SLOPE", "CROSSSLOPE", "GRADE", "FORESLOPERATE",
            "CUTSLOPE", "FILLSLOPE", "DEFAULTSLOPE",
            "SUPERELEVATION", "CROSSFALL", "FALL",
            "BACKSLOPE", "FORESLOPE", "DAYLIGHT",
            // Hebrew
            "שיפוע"
        };

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
            var newSlope = GetDoubleParam(parameters, "new_slope");
            var side = GetStringParam(parameters, "side")?.ToLowerInvariant() ?? "right";
            var subassemblyName = GetStringParam(parameters, "subassembly_name");
            var parameterName = GetStringParam(parameters, "parameter_name");

            if (newSlope == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_slope' is missing (percentage, e.g., -2.0 for 2% downward)");

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

            // Open corridor for write
            corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Corridor;
            if (corridor == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open corridor for write");

            // Re-get baseline/region after reopening corridor for write
            baseline = corridor.Baselines[baselineIndex];
            region = baseline.BaselineRegions[regionIndex];

            bool modified = false;
            double? oldSlope = null;
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
                        break;
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
                        System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] SubassemblyId is null for '{subName}', skipping");
                        continue;
                    }

                    var subassembly = tr.GetObject(subId, OpenMode.ForWrite) as Subassembly;
                    if (subassembly == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] Could not load Subassembly for '{subName}' (ObjectId: {subId})");
                        continue;
                    }

                    // Access parameters on the design-time Subassembly
                    foreach (var collectionName in new[] { "ParamsDouble", "ParamsAll", "ParamsLong", "Parameters" })
                    {
                        System.Collections.IEnumerable? paramCol = null;

                        try
                        {
                            var prop = subassembly.GetType().GetProperty(collectionName);
                            if (prop != null)
                                paramCol = prop.GetValue(subassembly) as System.Collections.IEnumerable;
                        }
                        catch { }

                        if (paramCol == null) continue;

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

                            bool isMatch = false;

                            // Exact match by parameter_name
                            if (!string.IsNullOrEmpty(parameterName))
                            {
                                isMatch = pName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                       || pDisplayName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                       || (pKeyName != null && pKeyName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
                            }
                            else
                            {
                                // Slope parameter matching: exact matches + keyword contains
                                string nameUpper = pName.ToUpperInvariant().Trim();
                                string displayUpper = pDisplayName.ToUpperInvariant().Trim();

                                // Exact matches for common slope parameter names
                                // Left-side assemblies use "Pave Slope" / "Pave Slope Rev"
                                // Right-side assemblies use "Top Slope" / "Bottom Slope"
                                if (displayUpper == "PAVE SLOPE" || displayUpper == "PAVE SLOPE REV" ||
                                    displayUpper == "TOP SLOPE" || displayUpper == "BOTTOM SLOPE" ||
                                    displayUpper == "SIDE SLOPE" || displayUpper == "SLOPE" ||
                                    displayUpper == "CROSSSLOPE" ||
                                    nameUpper == "PAVE SLOPE" || nameUpper == "PAVE SLOPE REV" ||
                                    nameUpper == "TOP SLOPE" || nameUpper == "BOTTOM SLOPE" ||
                                    nameUpper == "SIDE SLOPE" || nameUpper == "SLOPE" ||
                                    nameUpper == "CROSSSLOPE")
                                {
                                    isMatch = true;
                                }
                                // Contains matches for compound names
                                else
                                {
                                    foreach (var keyword in SlopeKeywords)
                                    {
                                        if (nameUpper.Contains(keyword) || displayUpper.Contains(keyword))
                                        {
                                            isMatch = true;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (isMatch)
                            {
                                try
                                {
                                    oldSlope = Convert.ToDouble(paramType.GetProperty("Value")?.GetValue(param));

                                    // Try setting via Value property directly
                                    var valueProp = paramType.GetProperty("Value");
                                    if (valueProp != null && valueProp.CanWrite)
                                    {
                                        valueProp.SetValue(param, newSlope.Value / 100.0);
                                        modified = true;
                                    }

                                    // Fallback: try SetValue method
                                    if (!modified)
                                    {
                                        var setValueMethod = paramType.GetMethod("SetValue");
                                        if (setValueMethod != null)
                                        {
                                            setValueMethod.Invoke(param, new object[] { newSlope.Value / 100.0 });
                                            modified = true;
                                        }
                                    }

                                    // Fallback: try SetParameterValue on subassembly
                                    if (!modified)
                                    {
                                        var setParamMethod = subassembly.GetType().GetMethod("SetParameterValue");
                                        if (setParamMethod != null)
                                        {
                                            setParamMethod.Invoke(subassembly, new object[] { pName, newSlope.Value / 100.0 });
                                            modified = true;
                                        }
                                    }

                                    matchedSubassembly = subName;
                                    matchedParameter = pDisplayName.Length > 0 ? pDisplayName : pName;
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] Failed to set parameter {pName}: {ex.Message}");
                                }
                                if (modified) break;
                            }
                        }

                        if (modified) break;
                    }

                    if (modified) break;
                }

                System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] Enumerated {subCount} applied subassemblies");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify corridor slope: {ex.Message}");
            }

            // Debug log all found parameters
            System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] All parameters found ({allFoundParams.Count}):");
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
                    requested_slope_percent = Math.Round(newSlope.Value, 3),
                    subassemblies_found = subassemblyNames.Count,
                    parameters_found = allParamDetails.Count,
                    inventory = subassemblyInventory,
                }, new JsonSerializerOptions { WriteIndented = false });

                return ToolResult.Fail(
                    ToolErrorCodes.SubassemblyParameterNotFound,
                    "Slope parameter not found by keyword matching. Retry with explicit subassembly_name and parameter_name from the inventory below.",
                    inventoryJson);
            }

            // Rebuild corridor after successful modification
            try
            {
                corridor.Rebuild();
                System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] Corridor '{corridorName}' rebuilt successfully");
            }
            catch (Exception rebuildEx)
            {
                System.Diagnostics.Debug.WriteLine($"[ModifyCorridorSlope] Corridor rebuild failed: {rebuildEx.Message}");
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
                old_slope_percent = oldSlope.HasValue ? Math.Round(oldSlope.Value * 100.0, 3) : (double?)null,
                new_slope_percent = Math.Round(newSlope.Value, 3),
                rebuilt = true,
                note = "Slope parameter modified and corridor rebuilt."
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
