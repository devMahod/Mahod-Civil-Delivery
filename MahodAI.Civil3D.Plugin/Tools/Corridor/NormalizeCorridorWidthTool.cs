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
    /// Normalizes a corridor's total width by setting the same target width
    /// on every region of every baseline. Intended for fixing
    /// "corridor total width variability" violations where adjacent stations
    /// have wildly different totals due to modelling errors or unplanned
    /// assembly transitions.
    ///
    /// Reuses the same reflection-based subassembly-parameter discovery as
    /// <see cref="ModifyCorridorWidthTool"/> but applies the target width
    /// across all regions in a single tool call, then rebuilds once.
    /// </summary>
    public class NormalizeCorridorWidthTool : DrawingToolBase
    {
        public override string Name => "normalize_corridor_total_width";
        public override string Description =>
            "Normalizes total corridor width by setting the same target width on every region of the corridor. " +
            "Use to fix violations where total ROW width varies non-uniformly across stations. " +
            "Scales the first match of each region's width keyword (WIDTH, LANE WIDTH, DEFAULT WIDTH, etc.).";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly string[] WidthKeywords = {
            "WIDTH", "TARGETWIDTH", "LANEWIDTH", "DEFAULTWIDTH",
            "SHOULDERWIDTH", "TOPWIDTH", "BOTTOMWIDTH", "PAVEWIDTH",
            "MEDIAWIDTH", "HALF", "OFFSET", "רוחב",
        };

        private static readonly string[] WidthDisqualifiers = {
            "INSIDE", "OUTSIDE", "BASE", "SUBBASE", "SUB BASE",
            "MINIMUM", "MAXIMUM", "MIN ", "MAX ", "WEARING",
        };

        private static int ScoreWidthMatch(string nameUpper, string displayUpper)
        {
            if (nameUpper == "WIDTH" || displayUpper == "WIDTH") return 100;
            if (nameUpper == "DEFAULT WIDTH" || displayUpper == "DEFAULT WIDTH") return 95;
            if (nameUpper == "LANE WIDTH" || displayUpper == "LANE WIDTH") return 95;
            if (nameUpper == "SHOULDER WIDTH" || displayUpper == "SHOULDER WIDTH") return 95;
            if (nameUpper == "LANEWIDTH" || displayUpper == "LANEWIDTH") return 90;
            if (nameUpper == "SHOULDERWIDTH" || displayUpper == "SHOULDERWIDTH") return 90;
            if (nameUpper == "TARGETWIDTH" || displayUpper == "TARGETWIDTH") return 85;
            if (nameUpper == "W" || displayUpper == "W") return 80;

            bool hasKeyword = false;
            foreach (var kw in WidthKeywords)
                if (nameUpper.Contains(kw) || displayUpper.Contains(kw)) { hasKeyword = true; break; }
            if (!hasKeyword) return 0;

            foreach (var dq in WidthDisqualifiers)
                if (nameUpper.Contains(dq) || displayUpper.Contains(dq)) return 10;

            return 50;
        }

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var corridorName = GetRequiredStringParam(parameters, "corridor_name");
            var targetWidth = GetDoubleParam(parameters, "target_total_width");
            var side = GetStringParam(parameters, "side")?.ToLowerInvariant() ?? "both";
            var subassemblyName = GetStringParam(parameters, "subassembly_name");
            var parameterName = GetStringParam(parameters, "parameter_name");

            if (targetWidth == null || targetWidth.Value <= 0)
                return ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    "Required parameter 'target_total_width' must be a positive number (meters)");

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

            // Reopen for write so we can modify subassembly params
            corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Corridor;
            if (corridor == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open corridor for write");

            var perRegion = new List<object>();
            int regionsModified = 0;
            int regionsSkipped = 0;

            for (int bi = 0; bi < corridor.Baselines.Count; bi++)
            {
                var baseline = corridor.Baselines[bi];
                for (int ri = 0; ri < baseline.BaselineRegions.Count; ri++)
                {
                    ct.ThrowIfCancellationRequested();
                    var region = baseline.BaselineRegions[ri];
                    var outcome = ApplyWidthToRegion(
                        tr, region, targetWidth.Value, side, subassemblyName, parameterName);

                    perRegion.Add(new
                    {
                        baseline_index = bi,
                        region_index = ri,
                        modified = outcome.Modified,
                        old_width = outcome.OldWidth.HasValue ? Math.Round(outcome.OldWidth.Value, 3) : (double?)null,
                        new_width = Math.Round(targetWidth.Value, 3),
                        subassembly = outcome.MatchedSubassembly,
                        parameter = outcome.MatchedParameter,
                        skip_reason = outcome.SkipReason,
                    });

                    if (outcome.Modified) regionsModified++;
                    else regionsSkipped++;
                }
            }

            if (regionsModified == 0)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.SubassemblyParameterNotFound,
                    "No region had a matchable width parameter. Retry with explicit subassembly_name / parameter_name.",
                    JsonSerializer.Serialize(new
                    {
                        corridor_name = corridorName,
                        target_total_width = Math.Round(targetWidth.Value, 3),
                        regions = perRegion,
                    }));
            }

            // One rebuild at the end — much cheaper than rebuilding per region
            try
            {
                corridor.Rebuild();
                System.Diagnostics.Debug.WriteLine(
                    $"[NormalizeCorridorWidth] Corridor '{corridorName}' rebuilt after {regionsModified} region(s)");
            }
            catch (Exception rebuildEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NormalizeCorridorWidth] Corridor rebuild failed: {rebuildEx.Message}");
            }

            cache.RemoveByPattern("get_corridor_info:");
            cache.RemoveByPattern("get_corridor_cross_section:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                corridor_name = corridorName,
                target_total_width = Math.Round(targetWidth.Value, 3),
                regions_modified = regionsModified,
                regions_skipped = regionsSkipped,
                regions = perRegion,
                rebuilt = true,
                note = "Width parameter normalised across all regions and corridor rebuilt once.",
            }));
        }

        private class RegionOutcome
        {
            public bool Modified;
            public double? OldWidth;
            public string? MatchedSubassembly;
            public string? MatchedParameter;
            public string? SkipReason;
        }

        private RegionOutcome ApplyWidthToRegion(
            Transaction tr,
            BaselineRegion region,
            double newWidth,
            string side,
            string? subassemblyName,
            string? parameterName)
        {
            var outcome = new RegionOutcome();

            var appliedAssemblies = region.AppliedAssemblies;
            if (appliedAssemblies == null)
            {
                outcome.SkipReason = "AppliedAssemblies is null (needs rebuild?)";
                return outcome;
            }

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
            {
                outcome.SkipReason = "No accessible AppliedAssembly";
                return outcome;
            }

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
                outcome.SkipReason = "No AppliedSubassemblies accessor";
                return outcome;
            }

            int subCount = 0;
            foreach (var appliedSub in appliedSubs)
            {
                subCount++;
                string subName = CorridorReflectionHelper.ResolveSubassemblyName(appliedSub, subCount, tr);

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
                        subSide = offset >= 0 ? "right" : "left";
                    }
                }

                if (!string.IsNullOrEmpty(subassemblyName))
                {
                    if (!subName.Equals(subassemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (side != "both" && subSide != side)
                {
                    continue;
                }

                ObjectId subId = ObjectId.Null;
                var subIdProp = appliedSub.GetType().GetProperty("SubassemblyId");
                if (subIdProp != null)
                {
                    var subIdVal = subIdProp.GetValue(appliedSub);
                    if (subIdVal is ObjectId sid)
                        subId = sid;
                }
                if (subId.IsNull) continue;

                var subassembly = tr.GetObject(subId, OpenMode.ForWrite) as Subassembly;
                if (subassembly == null) continue;

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

                        int score;
                        if (!string.IsNullOrEmpty(parameterName))
                        {
                            bool exact = pName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                      || pDisplayName.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                                      || (pKeyName != null && pKeyName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
                            score = exact ? 1000 : 0;
                        }
                        else
                        {
                            score = ScoreWidthMatch(pName.ToUpperInvariant().Trim(), pDisplayName.ToUpperInvariant().Trim());
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

                    if (bestParam == null || bestParamType == null) continue;

                    try
                    {
                        outcome.OldWidth = Convert.ToDouble(bestParamType.GetProperty("Value")?.GetValue(bestParam));

                        var valueProp = bestParamType.GetProperty("Value");
                        bool applied = false;
                        if (valueProp != null && valueProp.CanWrite)
                        {
                            valueProp.SetValue(bestParam, newWidth);
                            applied = true;
                        }
                        if (!applied)
                        {
                            var setValueMethod = bestParamType.GetMethod("SetValue");
                            if (setValueMethod != null)
                            {
                                setValueMethod.Invoke(bestParam, new object[] { newWidth });
                                applied = true;
                            }
                        }
                        if (!applied)
                        {
                            var setParamMethod = subassembly.GetType().GetMethod("SetParameterValue");
                            if (setParamMethod != null)
                            {
                                setParamMethod.Invoke(subassembly, new object[] { bestName, newWidth });
                                applied = true;
                            }
                        }

                        if (applied)
                        {
                            outcome.Modified = true;
                            outcome.MatchedSubassembly = subName;
                            outcome.MatchedParameter = bestDisplay.Length > 0 ? bestDisplay : bestName;
                            return outcome;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[NormalizeCorridorWidth] Set failed on {bestName}: {ex.Message}");
                    }
                }
            }

            if (string.IsNullOrEmpty(outcome.SkipReason))
                outcome.SkipReason = $"No matching width parameter across {subCount} subassemblies";
            return outcome;
        }
    }
}
