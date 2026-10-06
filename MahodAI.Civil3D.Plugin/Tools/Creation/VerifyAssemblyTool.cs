using System;
using System.Collections;
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
    /// Verifies that an Assembly has subassemblies and reads their parameters.
    /// Uses reflection to access subassembly data (API varies across Civil 3D versions).
    /// </summary>
    public class VerifyAssemblyTool : DrawingToolBase
    {
        public override string Name => "verify_assembly";
        public override string Description =>
            "Verifies an Assembly has subassemblies and returns a summary of its components " +
            "(lanes, shoulders, slopes, ditches) with their parameters.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""assembly_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the assembly to verify""
                }
            },
            ""required"": [""assembly_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var assemblyName = GetRequiredStringParam(parameters, "assembly_name");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find assembly
            CivilDb.Assembly? assembly = null;

            try
            {
                foreach (ObjectId id in civilDoc.AssemblyCollection)
                {
                    var asm = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Assembly;
                    if (asm != null && asm.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        assembly = asm;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to search assemblies: {ex.Message}");
            }

            if (assembly == null)
                return ToolResult.NotFound("Assembly", assemblyName);

            // Read subassemblies via reflection (API varies by version)
            var subassemblies = new List<Dictionary<string, object>>();
            int subCount = 0;

            try
            {
                // Try multiple approaches to get subassembly ObjectIds
                var subIds = GetSubassemblyIds(assembly);

                foreach (ObjectId subId in subIds)
                {
                    ct.ThrowIfCancellationRequested();
                    if (subId == ObjectId.Null) continue;

                    try
                    {
                        var sub = tr.GetObject(subId, OpenMode.ForRead);
                        if (sub == null) continue;

                        subCount++;
                        var subInfo = new Dictionary<string, object>
                        {
                            ["name"] = GetPropertyValue(sub, "Name") ?? "Unknown",
                        };

                        // Try to get Side property
                        var side = GetPropertyValue(sub, "Side");
                        if (side != null)
                            subInfo["side"] = side.ToString()!;

                        // Read numeric parameters via reflection
                        var numericParams = ReadParamsDouble(sub);
                        if (numericParams.Count > 0)
                            subInfo["parameters"] = numericParams;

                        // Read string parameters via reflection
                        var stringParams = ReadParamsString(sub);
                        if (stringParams.Count > 0)
                            subInfo["codes"] = stringParams;

                        subassemblies.Add(subInfo);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] VerifyAssembly error: {ex.Message}");
            }

            string summary = BuildSummary(assemblyName, subassemblies);

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["assembly_name"] = assemblyName,
                ["subassembly_count"] = subCount,
                ["subassemblies"] = subassemblies,
                ["summary"] = summary,
                ["message"] = subCount > 0
                    ? $"Assembly '{assemblyName}' contains {subCount} subassemblies."
                    : $"Assembly '{assemblyName}' is empty — no subassemblies found."
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Get subassembly ObjectIds from Assembly via reflection.
        /// Tries multiple property/method names across Civil 3D versions.
        /// </summary>
        private static List<ObjectId> GetSubassemblyIds(CivilDb.Assembly assembly)
        {
            var ids = new List<ObjectId>();

            // Try GetSubassemblyIds() method
            try
            {
                var method = assembly.GetType().GetMethod("GetSubassemblyIds");
                if (method != null)
                {
                    var result = method.Invoke(assembly, null);
                    if (result is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item is ObjectId oid)
                                ids.Add(oid);
                        }
                        if (ids.Count > 0) return ids;
                    }
                }
            }
            catch { }

            // Try Groups property → iterate groups → get subassemblies
            try
            {
                var groupsProp = assembly.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(assembly);
                    if (groups is IEnumerable groupsEnum)
                    {
                        foreach (var group in groupsEnum)
                        {
                            // Try GetSubassemblyIds on each group
                            try
                            {
                                var getSubs = group.GetType().GetMethod("GetSubassemblyIds");
                                if (getSubs != null)
                                {
                                    var subResult = getSubs.Invoke(group, null);
                                    if (subResult is IEnumerable subEnum)
                                    {
                                        foreach (var item in subEnum)
                                        {
                                            if (item is ObjectId oid)
                                                ids.Add(oid);
                                        }
                                    }
                                }
                            }
                            catch { }

                            // Try Subassemblies property on group
                            try
                            {
                                var subsProp = group.GetType().GetProperty("Subassemblies");
                                if (subsProp != null)
                                {
                                    var subs = subsProp.GetValue(group);
                                    if (subs is IEnumerable subsEnum)
                                    {
                                        foreach (var sub in subsEnum)
                                        {
                                            var oidProp = sub.GetType().GetProperty("ObjectId");
                                            if (oidProp != null)
                                            {
                                                var oid = oidProp.GetValue(sub);
                                                if (oid is ObjectId objId)
                                                    ids.Add(objId);
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return ids;
        }

        /// <summary>
        /// Read numeric parameters from a subassembly via reflection.
        /// </summary>
        private static Dictionary<string, double> ReadParamsDouble(DBObject sub)
        {
            var result = new Dictionary<string, double>();
            try
            {
                var paramsProp = sub.GetType().GetProperty("ParamsDouble");
                if (paramsProp != null)
                {
                    var paramsColl = paramsProp.GetValue(sub);
                    if (paramsColl is IEnumerable paramsEnum)
                    {
                        foreach (var param in paramsEnum)
                        {
                            try
                            {
                                var displayName = param.GetType().GetProperty("DisplayName")?.GetValue(param)?.ToString();
                                var value = param.GetType().GetProperty("Value")?.GetValue(param);
                                if (displayName != null && value is double dVal)
                                    result[displayName] = Math.Round(dVal, 4);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Read string parameters from a subassembly via reflection.
        /// </summary>
        private static Dictionary<string, string> ReadParamsString(DBObject sub)
        {
            var result = new Dictionary<string, string>();
            try
            {
                var paramsProp = sub.GetType().GetProperty("ParamsString");
                if (paramsProp != null)
                {
                    var paramsColl = paramsProp.GetValue(sub);
                    if (paramsColl is IEnumerable paramsEnum)
                    {
                        foreach (var param in paramsEnum)
                        {
                            try
                            {
                                var displayName = param.GetType().GetProperty("DisplayName")?.GetValue(param)?.ToString();
                                var value = param.GetType().GetProperty("Value")?.GetValue(param)?.ToString();
                                if (displayName != null && !string.IsNullOrEmpty(value))
                                    result[displayName] = value;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private static object? GetPropertyValue(DBObject obj, string propName)
        {
            try
            {
                return obj.GetType().GetProperty(propName)?.GetValue(obj);
            }
            catch { return null; }
        }

        private static string BuildSummary(string assemblyName, List<Dictionary<string, object>> subs)
        {
            if (subs.Count == 0)
                return $"Assembly '{assemblyName}' is empty.";

            var lines = new List<string>
            {
                $"**Assembly '{assemblyName}'** — {subs.Count} subassemblies:\n"
            };

            var leftSubs = subs.Where(s => s.GetValueOrDefault("side")?.ToString() == "Left").ToList();
            var rightSubs = subs.Where(s => s.GetValueOrDefault("side")?.ToString() == "Right").ToList();
            var otherSubs = subs.Where(s =>
            {
                var side = s.GetValueOrDefault("side")?.ToString() ?? "";
                return side != "Left" && side != "Right";
            }).ToList();

            if (leftSubs.Count > 0)
            {
                lines.Add($"**צד שמאל ({leftSubs.Count}):**");
                foreach (var sub in leftSubs)
                    lines.Add($"  - {sub.GetValueOrDefault("name", "?")}");
            }

            if (rightSubs.Count > 0)
            {
                lines.Add($"**צד ימין ({rightSubs.Count}):**");
                foreach (var sub in rightSubs)
                    lines.Add($"  - {sub.GetValueOrDefault("name", "?")}");
            }

            if (otherSubs.Count > 0)
            {
                lines.Add($"**אחר ({otherSubs.Count}):**");
                foreach (var sub in otherSubs)
                    lines.Add($"  - {sub.GetValueOrDefault("name", "?")}");
            }

            return string.Join("\n", lines);
        }
    }
}
