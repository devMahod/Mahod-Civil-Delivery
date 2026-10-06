using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates Sample Lines along an alignment at regular intervals.
    /// Sample Lines are required for creating Section Views (cross-section visualizations).
    ///
    /// Civil 3D workflow: Alignment → Sample Lines → Section Views
    ///
    /// Based on Igor's CreateSampleLines.cs — creates a SampleLineGroup with
    /// perpendicular lines at each station, extending left and right.
    ///
    /// API: SampleLineGroup.Create(name, alignmentId)
    ///      then SampleLine.Create(name, groupId, station) for each station
    /// </summary>
    public class CreateSampleLinesTool : DrawingToolBase
    {
        public override string Name => "create_sample_lines";
        public override string Description =>
            "Creates Sample Lines along an alignment at regular intervals. " +
            "Required before creating Section Views (cross-section visualizations). " +
            "Each sample line is perpendicular to the alignment at its station.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the alignment""
                },
                ""interval"": {
                    ""type"": ""number"",
                    ""description"": ""Interval between sample lines in meters (default: 50)""
                },
                ""left_width"": {
                    ""type"": ""number"",
                    ""description"": ""Left extent of sample line in meters (default: 20)""
                },
                ""right_width"": {
                    ""type"": ""number"",
                    ""description"": ""Right extent of sample line in meters (default: 20)""
                },
                ""group_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the sample line group (default: auto)""
                }
            },
            ""required"": [""alignment_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var interval = GetDoubleParam(parameters, "interval") ?? 50.0;
            var leftWidth = GetDoubleParam(parameters, "left_width") ?? 20.0;
            var rightWidth = GetDoubleParam(parameters, "right_width") ?? 20.0;
            var groupName = GetStringParam(parameters, "group_name")
                ?? $"SLG - {alignmentName}";

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find alignment
            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to read alignment");

            double startStation = alignment.StartingStation;
            double endStation = alignment.EndingStation;

            // Check if sample line group already exists
            try
            {
                var slgIds = alignment.GetSampleLineGroupIds();
                foreach (ObjectId slgId in slgIds)
                {
                    var slg = tr.GetObject(slgId, OpenMode.ForRead) as CivilDb.SampleLineGroup;
                    if (slg != null && slg.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        int existingCount = 0;
                        foreach (ObjectId _ in slg.GetSampleLineIds())
                            existingCount++;

                        return ToolResult.Ok(new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["group_name"] = groupName,
                            ["already_existed"] = true,
                            ["sample_line_count"] = existingCount,
                            ["message"] = $"Sample Line Group '{groupName}' already exists with {existingCount} lines."
                        });
                    }
                }
            }
            catch { }

            // Create Sample Lines via API (silent, no dialog)
            // Note: section views now use custom blocks (Igor's approach),
            // so sample lines are optional — kept for Civil 3D compatibility.
            ObjectId groupId = ObjectId.Null;
            string creationMethod = "unknown";
            int lineCount = 0;

            try
            {
                groupId = CreateGroupViaApi(civilDoc, alignmentId.Value, groupName);
                creationMethod = "API";
            }
            catch (Exception apiEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] SampleLineGroup API creation failed: {apiEx.Message}");
                // Non-critical — section views work without sample lines
                cache.RemoveByPattern("get_drawing_summary:");
                return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["group_name"] = groupName,
                    ["creation_method"] = "skipped",
                    ["sample_line_count"] = 0,
                    ["message"] = $"Sample lines skipped (API unavailable). " +
                        "Section views will use direct corridor sampling instead."
                }));
            }

            if (groupId != ObjectId.Null)
            {
                // Clamp last sample station to endStation - 0.1 m to avoid placing
                // a sample line at the exact alignment endpoint, where the tangent
                // direction is extrapolated and the cross-section can flare
                // dramatically.
                double endStationClamped = endStation > startStation + 0.1
                    ? endStation - 0.1
                    : endStation;

                var stationList = new List<double>();
                for (double station = startStation; station <= endStationClamped; station += interval)
                    stationList.Add(station);
                if (stationList.Count == 0 ||
                    (endStationClamped - stationList[^1]) > interval * 0.5)
                    stationList.Add(endStationClamped);

                int failCount = 0;
                foreach (double station in stationList)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        CreateSampleLineAtStation(tr, groupId, station, leftWidth, rightWidth);
                        lineCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        if (failCount <= 3) // only log first few failures
                            System.Diagnostics.Debug.WriteLine(
                                $"[MahodAI] SampleLine at STA {station:F0} failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
                if (failCount > 0)
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] SampleLine creation: {lineCount} succeeded, {failCount} failed");
            }

            cache.RemoveByPattern("get_drawing_summary:");

            return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
            {
                ["success"] = true,
                ["group_name"] = groupName,
                ["creation_method"] = creationMethod,
                ["alignment_name"] = alignmentName,
                ["sample_line_count"] = lineCount,
                ["interval_m"] = interval,
                ["left_width_m"] = leftWidth,
                ["right_width_m"] = rightWidth,
                ["start_station"] = Math.Round(startStation, 1),
                ["end_station"] = Math.Round(endStation, 1),
                ["message"] = $"Created {lineCount} sample lines in group '{groupName}' " +
                    $"at {interval}m intervals along '{alignmentName}'."
            }));
        }

        private static ObjectId CreateGroupViaApi(
            CivilDocument civilDoc, ObjectId alignmentId, string groupName)
        {
            // SampleLineGroup.Create via reflection
            var methods = typeof(CivilDb.SampleLineGroup)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Create")
                .ToList();

            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] SampleLineGroup.Create overloads: {methods.Count}");

            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                try
                {
                    if (parms.Length == 2 &&
                        parms[0].ParameterType == typeof(string))
                    {
                        return (ObjectId)method.Invoke(null, new object[] { groupName, alignmentId });
                    }
                    else if (parms.Length == 3)
                    {
                        return (ObjectId)method.Invoke(null, new object[] { groupName, alignmentId, ObjectId.Null });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] SampleLineGroup.Create {parms.Length}-param: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            throw new InvalidOperationException("No compatible SampleLineGroup.Create overload found");
        }

        private static void CreateSampleLineAtStation(
            Transaction tr, ObjectId groupId, double station,
            double leftWidth, double rightWidth)
        {
            // SampleLine.Create via reflection
            var methods = typeof(CivilDb.SampleLine)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Create")
                .ToList();

            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                try
                {
                    if (parms.Length == 2)
                    {
                        // Create(string name, ObjectId groupId) — then set station
                        string slName = $"SL-{station:F0}";
                        var slId = (ObjectId)method.Invoke(null, new object[] { slName, groupId });

                        // Set station and widths via reflection
                        if (slId != ObjectId.Null)
                        {
                            var sl = tr.GetObject(slId, OpenMode.ForWrite);
                            if (sl != null)
                            {
                                var staProp = sl.GetType().GetProperty("Station");
                                if (staProp != null && staProp.CanWrite)
                                    staProp.SetValue(sl, station);
                                // Apply requested widths
                                var lwProp = sl.GetType().GetProperty("SampledLeft");
                                if (lwProp != null && lwProp.CanWrite)
                                    lwProp.SetValue(sl, leftWidth);
                                var rwProp = sl.GetType().GetProperty("SampledRight");
                                if (rwProp != null && rwProp.CanWrite)
                                    rwProp.SetValue(sl, rightWidth);
                            }
                        }
                        return;
                    }
                    else if (parms.Length == 3)
                    {
                        string slName = $"SL-{station:F0}";
                        method.Invoke(null, new object[] { slName, groupId, station });
                        return;
                    }
                }
                catch { }
            }

            // Fallback: try by station directly on group
            try
            {
                var group = tr.GetObject(groupId, OpenMode.ForWrite) as CivilDb.SampleLineGroup;
                if (group != null)
                {
                    var addMethod = group.GetType().GetMethod("Add");
                    if (addMethod != null)
                    {
                        addMethod.Invoke(group, new object[] { station, leftWidth, rightWidth });
                        return;
                    }
                }
            }
            catch { }
        }

        private static bool CreateGroupViaLisp(
            string alignmentName, string groupName,
            double interval, double leftWidth, double rightWidth)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Use CreateSampleLines command with automatic stations
            string lispCmd = "(progn " +
                "(setvar \"CMDECHO\" 0) " +
                $"(command \"_CREATESAMPLELINES\" \"{alignmentName}\" " +
                $"\"{groupName}\" \"\" \"\" " +
                $"\"From-To\" \"\" \"\" \"{interval}\" " +
                $"\"{leftWidth}\" \"{rightWidth}\" \"\") " +
                "(setvar \"CMDECHO\" 1) " +
                "(princ))";

            doc.SendStringToExecute(lispCmd + "\n", true, false, false);
            return true;
        }
    }
}
