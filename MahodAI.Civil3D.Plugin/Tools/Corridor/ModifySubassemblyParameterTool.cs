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
    /// Modifies a single subassembly parameter by exact DisplayName.
    /// No keyword guessing — the caller must provide the exact parameter_name
    /// (as returned by list_subassembly_parameters).
    /// </summary>
    public class ModifySubassemblyParameterTool : DrawingToolBase
    {
        public override string Name => "modify_subassembly_parameter";
        public override string Description =>
            "Modifies a corridor subassembly parameter by exact name. " +
            "Requires corridor_name, subassembly_name, parameter_name (exact DisplayName from list_subassembly_parameters), and new_value. " +
            "On parameter-not-found returns the full parameter inventory for retry.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var subassemblyName = GetRequiredStringParam(parameters, "subassembly_name");
            var parameterName = GetRequiredStringParam(parameters, "parameter_name");
            var newValue = GetDoubleParam(parameters, "new_value");
            var baselineIndex = GetIntParam(parameters, "baseline_index") ?? 0;
            var regionIndex = GetIntParam(parameters, "region_index") ?? 0;

            if (newValue == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "new_value is required");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the corridor
            Autodesk.Civil.DatabaseServices.Corridor? corridor = null;
            foreach (ObjectId id in civilDoc.CorridorCollection)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                if (obj != null && obj.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                {
                    corridor = obj;
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

            // Open corridor for write
            corridor.UpgradeOpen();
            baseline = corridor.Baselines[baselineIndex];
            var region = baseline.BaselineRegions[regionIndex];

            bool modified = false;
            double? oldValue = null;
            string? matchedSubassembly = null;
            string? matchedParameter = null;
            var allParamDetails = new List<ParamInfo>();

            try
            {
                var appliedAssemblies = region.AppliedAssemblies;
                if (appliedAssemblies == null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "region.AppliedAssemblies returned null — corridor may need rebuilding first");

                // Get first applied assembly
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
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "Could not access any AppliedAssembly from region");

                // Get applied subassemblies
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
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "Could not get AppliedSubassemblies from AppliedAssembly");

                int subCount = 0;
                foreach (var appliedSub in appliedSubs)
                {
                    ct.ThrowIfCancellationRequested();
                    subCount++;

                    string subName = CorridorReflectionHelper.ResolveSubassemblyName(appliedSub, subCount, tr);

                    // Determine side
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
                                var offsetProp2 = origin.GetType().GetProperty("Offset");
                                if (offsetProp2 != null)
                                    offset = Convert.ToDouble(offsetProp2.GetValue(origin));
                            }
                            subSide = offset >= 0 ? "right" : "left";
                        }
                    }

                    // Filter by subassembly_name — strict exact match
                    if (!subName.Equals(subassemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Load the design-time Subassembly
                    ObjectId subId = ObjectId.Null;
                    var subIdProp = appliedSub.GetType().GetProperty("SubassemblyId");
                    if (subIdProp != null)
                    {
                        var subIdVal = subIdProp.GetValue(appliedSub);
                        if (subIdVal is ObjectId sid)
                            subId = sid;
                    }

                    if (subId.IsNull)
                        continue;

                    var subassembly = tr.GetObject(subId, OpenMode.ForWrite) as Subassembly;
                    if (subassembly == null)
                        continue;

                    // Search parameter collections
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

                            object? currentVal = null;
                            try { currentVal = paramType.GetProperty("Value")?.GetValue(param); } catch { }

                            double? numVal = null;
                            if (currentVal != null)
                            {
                                try { numVal = Convert.ToDouble(currentVal); } catch { }
                            }

                            allParamDetails.Add(new ParamInfo
                            {
                                Name = pName,
                                DisplayName = pDisplayName,
                                KeyName = pKeyName,
                                Value = numVal,
                                Collection = collectionName,
                            });

                            // Exact match on DisplayName, Name, or KeyName
                            bool isMatch = pDisplayName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                        || pName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                        || (pKeyName != null && pKeyName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));

                            if (isMatch)
                            {
                                try
                                {
                                    oldValue = currentVal != null ? Convert.ToDouble(currentVal) : null;

                                    var valueProp = paramType.GetProperty("Value");
                                    if (valueProp != null && valueProp.CanWrite)
                                    {
                                        valueProp.SetValue(param, newValue.Value);
                                        modified = true;
                                    }

                                    if (!modified)
                                    {
                                        var setValueMethod = paramType.GetMethod("SetValue");
                                        if (setValueMethod != null)
                                        {
                                            setValueMethod.Invoke(param, new object[] { newValue.Value });
                                            modified = true;
                                        }
                                    }

                                    if (!modified)
                                    {
                                        var setParamMethod = subassembly.GetType().GetMethod("SetParameterValue");
                                        if (setParamMethod != null)
                                        {
                                            setParamMethod.Invoke(subassembly, new object[] { pName, newValue.Value });
                                            modified = true;
                                        }
                                    }

                                    matchedSubassembly = subName;
                                    matchedParameter = pDisplayName.Length > 0 ? pDisplayName : pName;
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[ModifySubassemblyParameter] Failed to set {pName}: {ex.Message}");
                                }
                                if (modified) break;
                            }
                        }
                        if (modified) break;
                    }
                    if (modified) break;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[ModifySubassemblyParameter] Searched {subCount} subassemblies");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to modify parameter: {ex.Message}");
            }

            if (!modified)
            {
                // Return full parameter inventory for the matched subassembly
                var inventoryParams = new List<object>();
                foreach (var pi in allParamDetails)
                {
                    inventoryParams.Add(new
                    {
                        name = pi.Name,
                        display_name = pi.DisplayName,
                        key_name = pi.KeyName,
                        value = pi.Value.HasValue ? Math.Round(pi.Value.Value, 4) : (double?)null,
                        collection = pi.Collection,
                    });
                }

                return ToolResult.Fail(
                    ToolErrorCodes.SubassemblyParameterNotFound,
                    $"Parameter '{parameterName}' not found on subassembly '{subassemblyName}'. " +
                    "Available parameters listed in details. Retry with exact display_name.",
                    JsonSerializer.Serialize(new
                    {
                        corridor_name = corridorName,
                        subassembly_name = subassemblyName,
                        requested_parameter = parameterName,
                        available_parameters = inventoryParams,
                    }));
            }

            // Rebuild corridor
            try
            {
                corridor.Rebuild();
                System.Diagnostics.Debug.WriteLine(
                    $"[ModifySubassemblyParameter] Corridor '{corridorName}' rebuilt");
            }
            catch (Exception rebuildEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ModifySubassemblyParameter] Rebuild failed: {rebuildEx.Message}");
            }

            cache.RemoveByPattern("get_corridor_info:");
            cache.RemoveByPattern("get_corridor_cross_section:");
            cache.RemoveByPattern("list_subassembly_parameters:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                corridor_name = corridorName,
                subassembly_name = matchedSubassembly,
                parameter_name = matchedParameter,
                old_value = oldValue.HasValue ? Math.Round(oldValue.Value, 6) : (double?)null,
                new_value = Math.Round(newValue.Value, 6),
                rebuilt = true,
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

        private class ParamInfo
        {
            public string Name { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string? KeyName { get; set; }
            public double? Value { get; set; }
            public string Collection { get; set; } = "";
        }
    }
}
