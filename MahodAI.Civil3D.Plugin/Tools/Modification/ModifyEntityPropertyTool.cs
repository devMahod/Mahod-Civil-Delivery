using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Sets a whitelisted property on an entity identified by handle.
    /// Allowed: Layer, ColorIndex, Linetype, LinetypeScale, Lineweight, Visible.
    /// Rejects Handle/ObjectId and anything else.
    /// </summary>
    public class ModifyEntityPropertyTool : DrawingToolBase
    {
        public override string Name => "modify_entity_property";
        public override string Description => "Sets a whitelisted property on a drawing entity (identified by hex handle). Allowed: Layer, ColorIndex, Linetype, LinetypeScale, Lineweight, Visible.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        private static readonly string[] AllowedProperties = new[]
        {
            "Layer", "ColorIndex", "Linetype", "LinetypeScale", "Lineweight", "Visible"
        };

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"" },
                ""property_name"": { ""type"": ""string"", ""enum"": [""Layer"",""ColorIndex"",""Linetype"",""LinetypeScale"",""Lineweight"",""Visible""] },
                ""value"": { }
            },
            ""required"": [""entity_handle"", ""property_name"", ""value""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var handleStr = GetRequiredStringParam(parameters, "entity_handle");
            var propertyName = GetRequiredStringParam(parameters, "property_name");

            if (!parameters.TryGetProperty("value", out var valueProp))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, "חסר ערך 'value'"));

            // Explicitly reject identity fields
            if (propertyName.Equals("Handle", StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals("ObjectId", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.NotSupported,
                    $"לא ניתן לשנות את המאפיין '{propertyName}' — מזהה פנימי של האובייקט."));
            }

            bool allowed = false;
            string canonical = propertyName;
            foreach (var a in AllowedProperties)
            {
                if (a.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    canonical = a;
                    break;
                }
            }
            if (!allowed)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.NotSupported,
                    $"מאפיין '{propertyName}' אינו נתמך. מותר: {string.Join(", ", AllowedProperties)}"));
            }

            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            if (!TryResolveHandle(db, handleStr, out var id, out var err))
                return Task.FromResult(ToolResult.NotFound("Entity", handleStr + (err != null ? $" ({err})" : "")));

            var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (entity == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "האובייקט אינו Entity"));

            try
            {
                entity.UpgradeOpen();

                switch (canonical)
                {
                    case "Layer":
                    {
                        if (valueProp.ValueKind != JsonValueKind.String)
                            return Fail("'Layer' חייב להיות מחרוזת");
                        var layerName = valueProp.GetString() ?? "";
                        var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                        if (lt == null || !lt.Has(layerName))
                            return Task.FromResult(ToolResult.NotFound("Layer", layerName));
                        entity.Layer = layerName;
                        break;
                    }
                    case "ColorIndex":
                    {
                        if (valueProp.ValueKind != JsonValueKind.Number)
                            return Fail("'ColorIndex' חייב להיות מספר 0..256");
                        var v = valueProp.GetDouble();
                        if (v < 0 || v > 256 || v != Math.Floor(v))
                            return Fail("'ColorIndex' חייב להיות מספר שלם 0..256");
                        entity.ColorIndex = (short)v;
                        break;
                    }
                    case "Linetype":
                    {
                        if (valueProp.ValueKind != JsonValueKind.String)
                            return Fail("'Linetype' חייב להיות מחרוזת");
                        var lname = valueProp.GetString() ?? "";
                        var ltt = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                        if (ltt == null || !ltt.Has(lname))
                            return Task.FromResult(ToolResult.NotFound("Linetype", lname));
                        entity.Linetype = lname;
                        break;
                    }
                    case "LinetypeScale":
                    {
                        if (valueProp.ValueKind != JsonValueKind.Number)
                            return Fail("'LinetypeScale' חייב להיות מספר חיובי");
                        var scale = valueProp.GetDouble();
                        if (scale <= 0)
                            return Fail("'LinetypeScale' חייב להיות גדול מאפס");
                        entity.LinetypeScale = scale;
                        break;
                    }
                    case "Lineweight":
                    {
                        if (valueProp.ValueKind != JsonValueKind.Number)
                            return Fail("'Lineweight' חייב להיות מספר");
                        var lw = valueProp.GetDouble();
                        entity.LineWeight = (LineWeight)(int)lw;
                        break;
                    }
                    case "Visible":
                    {
                        if (valueProp.ValueKind != JsonValueKind.True && valueProp.ValueKind != JsonValueKind.False)
                            return Fail("'Visible' חייב להיות true/false");
                        entity.Visible = valueProp.ValueKind == JsonValueKind.True;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"עדכון מאפיין נכשל: {ex.Message}"));
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                entity_handle = entity.Handle.ToString(),
                property = canonical,
                value = valueProp.GetRawText(),
            }));
        }

        private static Task<ToolResult> Fail(string message)
            => Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, message));

        private static bool TryResolveHandle(Database db, string handleStr, out ObjectId id, out string? error)
        {
            id = ObjectId.Null;
            error = null;
            try
            {
                long hvalue = Convert.ToInt64(handleStr, 16);
                var handle = new Handle(hvalue);
                if (!db.TryGetObjectId(handle, out id))
                {
                    error = "handle not found in current database";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
