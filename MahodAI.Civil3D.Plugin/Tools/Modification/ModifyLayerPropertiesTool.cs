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
    /// Modifies properties of a layer (color, linetype, lineweight, frozen/locked/on state).
    /// Only fields actually supplied are applied.
    /// </summary>
    public class ModifyLayerPropertiesTool : DrawingToolBase
    {
        public override string Name => "modify_layer_properties";
        public override string Description => "Modifies properties of an existing layer. Only fields actually supplied (color, linetype, lineweight, is_frozen, is_locked, is_on) are applied.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""layer_name"": { ""type"": ""string"", ""description"": ""Name of the layer to modify"" },
                ""color"": { ""type"": ""string"", ""description"": ""Color — hex (#RRGGBB), AutoCAD color index (0-256), or color name (red/yellow/green/cyan/blue/magenta/white)"" },
                ""linetype"": { ""type"": ""string"", ""description"": ""Linetype name (must already be loaded). e.g. Continuous, DASHED, HIDDEN"" },
                ""lineweight"": { ""type"": ""number"", ""description"": ""AutoCAD lineweight enum value (e.g. 25 = 0.25mm, -1 = ByLayer, -2 = ByBlock, -3 = Default)"" },
                ""is_frozen"": { ""type"": ""boolean"" },
                ""is_locked"": { ""type"": ""boolean"" },
                ""is_on"": { ""type"": ""boolean"" }
            },
            ""required"": [""layer_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var layerName = GetRequiredStringParam(parameters, "layer_name");

            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null || !lt.Has(layerName))
                return Task.FromResult(ToolResult.NotFound("Layer", layerName));

            var layerId = lt[layerName];
            var layer = tr.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
            if (layer == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "פתיחת השכבה נכשלה"));

            var changes = new Dictionary<string, object?>();
            var warnings = new List<string>();

            try
            {
                layer.UpgradeOpen();

                // Color
                var colorParam = GetStringParam(parameters, "color");
                if (!string.IsNullOrEmpty(colorParam))
                {
                    if (TryParseColor(colorParam, out var color, out var colorErr))
                    {
                        layer.Color = color!;
                        changes["color"] = colorParam;
                    }
                    else
                    {
                        warnings.Add($"color: {colorErr}");
                    }
                }

                // Linetype
                var linetype = GetStringParam(parameters, "linetype");
                if (!string.IsNullOrEmpty(linetype))
                {
                    var ltt = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                    if (ltt != null && ltt.Has(linetype))
                    {
                        layer.LinetypeObjectId = ltt[linetype];
                        changes["linetype"] = linetype;
                    }
                    else
                    {
                        warnings.Add($"linetype '{linetype}' אינו טעון בשרטוט");
                    }
                }

                // Lineweight
                var lwParam = GetDoubleParam(parameters, "lineweight");
                if (lwParam.HasValue)
                {
                    try
                    {
                        var lw = (LineWeight)(int)lwParam.Value;
                        layer.LineWeight = lw;
                        changes["lineweight"] = (int)lwParam.Value;
                    }
                    catch
                    {
                        warnings.Add($"lineweight: ערך לא חוקי {lwParam.Value}");
                    }
                }

                // Frozen / locked / on
                if (parameters.TryGetProperty("is_frozen", out var fProp) &&
                    (fProp.ValueKind == JsonValueKind.True || fProp.ValueKind == JsonValueKind.False))
                {
                    layer.IsFrozen = fProp.ValueKind == JsonValueKind.True;
                    changes["is_frozen"] = layer.IsFrozen;
                }
                if (parameters.TryGetProperty("is_locked", out var lProp) &&
                    (lProp.ValueKind == JsonValueKind.True || lProp.ValueKind == JsonValueKind.False))
                {
                    layer.IsLocked = lProp.ValueKind == JsonValueKind.True;
                    changes["is_locked"] = layer.IsLocked;
                }
                if (parameters.TryGetProperty("is_on", out var onProp) &&
                    (onProp.ValueKind == JsonValueKind.True || onProp.ValueKind == JsonValueKind.False))
                {
                    // LayerTableRecord.IsOff is the stored flag
                    layer.IsOff = !(onProp.ValueKind == JsonValueKind.True);
                    changes["is_on"] = !layer.IsOff;
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"עדכון השכבה נכשל: {ex.Message}"));
            }

            if (changes.Count == 0 && warnings.Count > 0)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "לא בוצעו שינויים: " + string.Join("; ", warnings)));
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                layer_name = layerName,
                changes,
                warnings = warnings.ToArray(),
            }));
        }

        private static bool TryParseColor(string input, out Autodesk.AutoCAD.Colors.Color? color, out string? error)
        {
            color = null;
            error = null;
            input = input.Trim();

            // Hex #RRGGBB
            if (input.StartsWith("#") && input.Length == 7)
            {
                try
                {
                    byte r = Convert.ToByte(input.Substring(1, 2), 16);
                    byte g = Convert.ToByte(input.Substring(3, 2), 16);
                    byte b = Convert.ToByte(input.Substring(5, 2), 16);
                    color = Autodesk.AutoCAD.Colors.Color.FromRgb(r, g, b);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            // Integer index
            if (short.TryParse(input, out var idx))
            {
                if (idx < 0 || idx > 256)
                {
                    error = "color index must be 0..256";
                    return false;
                }
                color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, idx);
                return true;
            }

            // Named colors
            short named = input.ToLowerInvariant() switch
            {
                "red" => 1,
                "yellow" => 2,
                "green" => 3,
                "cyan" => 4,
                "blue" => 5,
                "magenta" => 6,
                "white" or "black" => 7,
                _ => -1,
            };
            if (named >= 0)
            {
                color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, named);
                return true;
            }

            error = $"unrecognized color '{input}'";
            return false;
        }
    }
}
