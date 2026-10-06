using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Freezes a batch of layers. Continues on per-layer failure; reports both.
    /// </summary>
    public class BatchFreezeLayersTool : DrawingToolBase
    {
        public override string Name => "batch_freeze_layers";
        public override string Description => "Freezes a batch of layers by name. Collects failures per layer and still succeeds if at least one succeeded.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""layer_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
            },
            ""required"": [""layer_names""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
            => Task.FromResult(BatchLayerStateTool.Run(Name, tr, parameters, frozen: true));
    }

    /// <summary>
    /// Thaws a batch of layers. Continues on per-layer failure; reports both.
    /// </summary>
    public class BatchThawLayersTool : DrawingToolBase
    {
        public override string Name => "batch_thaw_layers";
        public override string Description => "Thaws a batch of layers by name. Collects failures per layer and still succeeds if at least one succeeded.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""layer_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
            },
            ""required"": [""layer_names""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
            => Task.FromResult(BatchLayerStateTool.Run(Name, tr, parameters, frozen: false));
    }

    internal static class BatchLayerStateTool
    {
        public static ToolResult Run(string toolName, Transaction tr, JsonElement parameters, bool frozen)
        {
            var names = ReadStringArray(parameters, "layer_names");
            if (names == null || names.Length == 0)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "חייב לספק מערך 'layer_names' עם שם שכבה אחד לפחות");
            }

            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל");

            var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "פתיחת טבלת שכבות נכשלה");

            var successes = new List<string>();
            var failures = new Dictionary<string, string>();

            foreach (var name in names)
            {
                try
                {
                    if (!lt.Has(name))
                    {
                        failures[name] = "not found";
                        continue;
                    }

                    var layerId = lt[name];
                    // Can't freeze the current (active) layer.
                    if (frozen && layerId == db.Clayer)
                    {
                        failures[name] = "cannot freeze the active layer";
                        continue;
                    }

                    var record = tr.GetObject(layerId, OpenMode.ForWrite) as LayerTableRecord;
                    if (record == null)
                    {
                        failures[name] = "failed to open";
                        continue;
                    }
                    record.IsFrozen = frozen;
                    successes.Add(name);
                }
                catch (Exception ex)
                {
                    failures[name] = ex.Message;
                }
            }

            if (successes.Count == 0)
            {
                var msg = "כל השכבות נכשלו: " + string.Join("; ", failures.Keys);
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, msg,
                    System.Text.Json.JsonSerializer.Serialize(failures));
            }

            var result = ToolResult.Ok(new
            {
                tool = toolName,
                requested = names.Length,
                succeeded = successes.ToArray(),
                failed = failures,
            });
            if (failures.Count > 0 && result.Metadata == null)
            {
                result.Metadata = new ToolResultMetadata
                {
                    Warnings = BuildWarnings(failures),
                };
            }
            return result;
        }

        private static string[] BuildWarnings(Dictionary<string, string> failures)
        {
            var arr = new string[failures.Count];
            int i = 0;
            foreach (var kvp in failures)
                arr[i++] = $"{kvp.Key}: {kvp.Value}";
            return arr;
        }

        private static string[]? ReadStringArray(JsonElement parameters, string name)
        {
            if (!parameters.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v)) list.Add(v!);
                }
            }
            return list.ToArray();
        }
    }
}
