using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Lists all subassembly parameters (names, display names, values) for a corridor's assemblies.
    /// Uses the correct Civil 3D API path: region.AppliedAssemblies → GetAppliedSubassemblies()
    /// → appliedSub.SubassemblyId → load design-time Subassembly → ParamsDouble/ParamsAll.
    /// Use this diagnostic tool to discover parameter names before modifying corridor width or slope.
    /// </summary>
    public class ListSubassemblyParametersTool : DrawingToolBase
    {
        public override string Name => "list_subassembly_parameters";
        public override string Description => "Lists all subassembly parameters (names, display names, values) for a corridor's assemblies. Use this to discover parameter names before modifying corridor width or slope.";
        public override string Category => ToolCategories.Corridor;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

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
            var subassemblyFilter = GetStringParam(parameters, "subassembly_name");

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

            var region = baseline.BaselineRegions[regionIndex];

            var result = new ListSubassemblyParametersResult
            {
                CorridorName = corridorName,
                BaselineIndex = baselineIndex,
                BaselineName = baseline.Name,
                RegionIndex = regionIndex,
                RegionName = region.Name,
                Subassemblies = new List<SubassemblyParameterInfo>()
            };

            // Get assembly name for reference
            try
            {
                if (!region.AssemblyId.IsNull)
                {
                    var assembly = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as Assembly;
                    if (assembly != null)
                        result.AssemblyName = assembly.Name;
                }
            }
            catch { }

            // === CORRECT PATH: region.AppliedAssemblies → GetAppliedSubassemblies() → SubassemblyId ===
            try
            {
                var appliedAssemblies = region.AppliedAssemblies;
                if (appliedAssemblies == null)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        "region.AppliedAssemblies returned null — corridor may need rebuilding first");
                }

                // Get first applied assembly (all stations share the same assembly definition)
                object? appliedAssembly = null;

                // Method 1: enumerate and pick first item
                var enumerator = appliedAssemblies as System.Collections.IEnumerable;
                if (enumerator != null)
                {
                    foreach (var item in enumerator)
                    {
                        appliedAssembly = item;
                        break;
                    }
                }

                // Method 2: GetItemAt fallback
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

                // Iterate applied subassemblies, load design-time Subassembly, extract ALL parameters
                int subCount = 0;
                foreach (var appliedSub in appliedSubs)
                {
                    ct.ThrowIfCancellationRequested();
                    subCount++;

                    string subName = CorridorReflectionHelper.ResolveSubassemblyName(appliedSub, subCount, tr);

                    // Apply subassembly name filter if specified
                    if (!string.IsNullOrEmpty(subassemblyFilter) &&
                        !subName.Equals(subassemblyFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Determine side from origin offset
                    double offset = 0;
                    var originProp = appliedSub.GetType().GetProperty("OriginStationOffsetElevationToBaseline");
                    if (originProp != null)
                    {
                        var origin = originProp.GetValue(appliedSub);
                        if (origin != null)
                        {
                            var yProp = origin.GetType().GetProperty("Y");
                            if (yProp != null)
                                offset = Convert.ToDouble(yProp.GetValue(origin));
                            else
                            {
                                var offsetProp2 = origin.GetType().GetProperty("Offset");
                                if (offsetProp2 != null)
                                    offset = Convert.ToDouble(offsetProp2.GetValue(origin));
                            }
                        }
                    }
                    string side = offset >= 0 ? "Right" : "Left";

                    var subInfo = new SubassemblyParameterInfo
                    {
                        Name = subName,
                        Side = side,
                        OriginOffset = Math.Round(offset, 3),
                        Source = "AppliedAssembly→SubassemblyId",
                        Parameters = new List<ParameterDetail>()
                    };

                    // Load the design-time Subassembly via SubassemblyId
                    ObjectId subId = ObjectId.Null;
                    var subIdProp = appliedSub.GetType().GetProperty("SubassemblyId");
                    if (subIdProp != null)
                    {
                        var subIdVal = subIdProp.GetValue(appliedSub);
                        if (subIdVal is ObjectId sid)
                            subId = sid;
                    }

                    if (!subId.IsNull)
                    {
                        try
                        {
                            var subassembly = tr.GetObject(subId, OpenMode.ForRead) as Subassembly;
                            if (subassembly != null)
                            {
                                // Try to get subassembly code name
                                try
                                {
                                    var codeNameProp = subassembly.GetType().GetProperty("CodeName");
                                    if (codeNameProp != null)
                                        subInfo.CodeName = codeNameProp.GetValue(subassembly)?.ToString();
                                }
                                catch { }

                                // Extract parameters from design-time Subassembly
                                // Try ParamsAll first (most complete)
                                ExtractParametersFromObject(subassembly, "ParamsAll", subInfo.Parameters);

                                // Try individual typed collections
                                if (subInfo.Parameters.Count == 0)
                                {
                                    foreach (var paramPropName in new[] { "ParamsBool", "ParamsDouble", "ParamsLong", "ParamsString", "ParamsPoint" })
                                    {
                                        ExtractParametersFromObject(subassembly, paramPropName, subInfo.Parameters);
                                    }
                                }

                                // Generic fallback
                                if (subInfo.Parameters.Count == 0)
                                {
                                    ExtractParametersFromObject(subassembly, "Parameters", subInfo.Parameters);
                                }

                                subInfo.Source = "AppliedAssembly→SubassemblyId→Subassembly";
                            }
                            else
                            {
                                subInfo.Source = "AppliedAssembly→SubassemblyId (could not cast to Subassembly)";
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ListSubassemblyParameters] Failed to load Subassembly {subId}: {ex.Message}");
                            subInfo.Source = $"AppliedAssembly→SubassemblyId (load error: {ex.Message})";
                        }
                    }
                    else
                    {
                        subInfo.Source = "AppliedAssembly (SubassemblyId is null)";
                    }

                    // If design-time path found nothing, try extracting params from the applied sub itself
                    if (subInfo.Parameters.Count == 0)
                    {
                        ExtractParametersFromObject(appliedSub, "ParamsAll", subInfo.Parameters);

                        if (subInfo.Parameters.Count == 0)
                        {
                            foreach (var paramPropName in new[] { "ParamsBool", "ParamsDouble", "ParamsLong", "ParamsString", "ParamsPoint", "Parameters" })
                            {
                                ExtractParametersFromObject(appliedSub, paramPropName, subInfo.Parameters);
                            }
                        }

                        // Last resort: enumerate all properties
                        if (subInfo.Parameters.Count == 0)
                        {
                            EnumerateAllProperties(appliedSub, subInfo.Parameters);
                            if (subInfo.Parameters.Count > 0)
                                subInfo.Source += " + PropertyEnumeration";
                        }
                    }

                    result.Subassemblies.Add(subInfo);
                }

                System.Diagnostics.Debug.WriteLine($"[ListSubassemblyParameters] Enumerated {subCount} applied subassemblies via AppliedAssemblies path");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ListSubassemblyParameters] AppliedAssembly parameter extraction error: {ex.Message}");
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to extract subassembly parameters: {ex.Message}");
            }

            result.TotalSubassemblies = result.Subassemblies.Count;
            result.TotalParameters = result.Subassemblies.Sum(s => s.Parameters.Count);

            // Debug log
            System.Diagnostics.Debug.WriteLine(
                $"[ListSubassemblyParameters] Corridor '{corridorName}': " +
                $"{result.TotalSubassemblies} subassemblies, {result.TotalParameters} total parameters");

            foreach (var sub in result.Subassemblies)
            {
                System.Diagnostics.Debug.WriteLine($"  Subassembly '{sub.Name}' ({sub.Side}, Source: {sub.Source}): {sub.Parameters.Count} params");
                foreach (var p in sub.Parameters)
                {
                    System.Diagnostics.Debug.WriteLine($"    {p.Name} (Display: {p.DisplayName}) = {p.Value} [{p.ValueType}]");
                }
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Extracts parameters from an object's named parameter collection via reflection.
        /// </summary>
        private static void ExtractParametersFromObject(object sourceObject, string collectionPropertyName, List<ParameterDetail> results)
        {
            try
            {
                var prop = sourceObject.GetType().GetProperty(collectionPropertyName);
                if (prop == null) return;

                var collection = prop.GetValue(sourceObject) as System.Collections.IEnumerable;
                if (collection == null) return;

                foreach (var param in collection)
                {
                    try
                    {
                        var paramType = param.GetType();

                        var detail = new ParameterDetail
                        {
                            Name = paramType.GetProperty("Name")?.GetValue(param)?.ToString() ?? "",
                            DisplayName = paramType.GetProperty("DisplayName")?.GetValue(param)?.ToString() ?? "",
                            CollectionName = collectionPropertyName
                        };

                        // Read value
                        try
                        {
                            var valueObj = paramType.GetProperty("Value")?.GetValue(param);
                            detail.Value = valueObj?.ToString() ?? "";
                            detail.ValueType = valueObj?.GetType().Name ?? "Unknown";
                            if (valueObj is double d)
                                detail.NumericValue = d;
                            else if (valueObj is int i)
                                detail.NumericValue = i;
                            else if (valueObj is long l)
                                detail.NumericValue = l;
                            else if (valueObj is float f)
                                detail.NumericValue = f;
                            else if (double.TryParse(detail.Value, System.Globalization.NumberStyles.Any,
                                         System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                                detail.NumericValue = parsed;
                        }
                        catch
                        {
                            detail.Value = "<read error>";
                            detail.ValueType = "Unknown";
                        }

                        // Check access (is it writable?)
                        try
                        {
                            var valueProp = paramType.GetProperty("Value");
                            detail.IsWritable = valueProp?.CanWrite ?? false;
                        }
                        catch
                        {
                            detail.IsWritable = false;
                        }

                        // Read KeyName if available
                        try
                        {
                            detail.KeyName = paramType.GetProperty("KeyName")?.GetValue(param)?.ToString();
                        }
                        catch { }

                        // Skip duplicate entries
                        if (!string.IsNullOrEmpty(detail.Name) || !string.IsNullOrEmpty(detail.DisplayName))
                        {
                            results.Add(detail);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ListSubassemblyParameters] Param read error: {ex.Message}");
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Enumerates all public properties on an object to discover potential parameters.
        /// Used as a last-resort fallback when named collections yield nothing.
        /// </summary>
        private static void EnumerateAllProperties(object obj, List<ParameterDetail> results)
        {
            try
            {
                var properties = obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                foreach (var prop in properties)
                {
                    // Skip indexers and collection-type properties
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (prop.Name == "SyncRoot" || prop.Name == "IsSynchronized") continue;

                    try
                    {
                        var value = prop.GetValue(obj);
                        results.Add(new ParameterDetail
                        {
                            Name = prop.Name,
                            DisplayName = prop.Name,
                            Value = value?.ToString() ?? "<null>",
                            ValueType = prop.PropertyType.Name,
                            IsWritable = prop.CanWrite,
                            CollectionName = "Properties"
                        });
                    }
                    catch { }
                }
            }
            catch { }
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

    #region Result Models

    public class ListSubassemblyParametersResult
    {
        public string CorridorName { get; set; } = string.Empty;
        public int BaselineIndex { get; set; }
        public string BaselineName { get; set; } = string.Empty;
        public int RegionIndex { get; set; }
        public string RegionName { get; set; } = string.Empty;
        public string? AssemblyName { get; set; }
        public int TotalSubassemblies { get; set; }
        public int TotalParameters { get; set; }
        public List<SubassemblyParameterInfo> Subassemblies { get; set; } = new();
    }

    public class SubassemblyParameterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? CodeName { get; set; }
        public string Side { get; set; } = string.Empty;
        public double? OriginOffset { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParameterDetail> Parameters { get; set; } = new();
    }

    public class ParameterDetail
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? KeyName { get; set; }
        public string Value { get; set; } = string.Empty;
        public double? NumericValue { get; set; }
        public string ValueType { get; set; } = string.Empty;
        public bool IsWritable { get; set; }
        public string? CollectionName { get; set; }
    }

    #endregion
}
