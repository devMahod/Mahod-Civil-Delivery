using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Moves (translates) an entity by a given delta vector.
    /// Identify the entity with either <c>entity_handle</c> (hex) or <c>object_id</c> (long).
    /// </summary>
    public class MoveEntityTool : DrawingToolBase
    {
        public override string Name => "move_entity";
        public override string Description => "Moves a drawing entity by the given dx/dy/dz delta. Identify the entity by 'entity_handle' (hex string) or 'object_id' (long).";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"", ""description"": ""Entity handle as hex string. Provide this OR object_id."" },
                ""object_id"": { ""type"": ""integer"", ""description"": ""Entity ObjectId as long. Provide this OR entity_handle."" },
                ""delta_x"": { ""type"": ""number"" },
                ""delta_y"": { ""type"": ""number"" },
                ""delta_z"": { ""type"": ""number"", ""description"": ""Default 0"" }
            },
            ""required"": [""delta_x"", ""delta_y""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var handleStr = GetStringParam(parameters, "entity_handle");
            long? objectIdLong = null;
            if (parameters.TryGetProperty("object_id", out var oidProp) &&
                oidProp.ValueKind == JsonValueKind.Number &&
                oidProp.TryGetInt64(out var oid))
            {
                objectIdLong = oid;
            }

            if (string.IsNullOrEmpty(handleStr) && !objectIdLong.HasValue)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "יש לספק 'entity_handle' או 'object_id'"));

            var dx = GetDoubleParam(parameters, "delta_x");
            var dy = GetDoubleParam(parameters, "delta_y");
            var dz = GetDoubleParam(parameters, "delta_z") ?? 0.0;
            if (!dx.HasValue || !dy.HasValue)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "חובה לספק delta_x ו-delta_y"));

            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            // Resolve by handle. If only object_id was supplied, interpret it as a Handle value (long form).
            string effectiveHandleStr = !string.IsNullOrEmpty(handleStr)
                ? handleStr!
                : objectIdLong!.Value.ToString("X");

            if (!TryResolveHandle(db, effectiveHandleStr, out var id, out var err))
                return Task.FromResult(ToolResult.NotFound("Entity", effectiveHandleStr + (err != null ? $" ({err})" : "")));

            if (id.IsNull || id.IsErased)
                return Task.FromResult(ToolResult.NotFound("Entity", effectiveHandleStr));

            Entity? entity;
            try
            {
                entity = tr.GetObject(id, OpenMode.ForWrite) as Entity;
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"פתיחת האובייקט נכשלה: {ex.Message}"));
            }

            if (entity == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "האובייקט אינו Entity הניתן להזזה"));

            try
            {
                var matrix = Matrix3d.Displacement(new Vector3d(dx.Value, dy.Value, dz));
                entity.TransformBy(matrix);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"הזזת האובייקט נכשלה: {ex.Message}"));
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                entity_handle = entity.Handle.ToString(),
                entity_type = entity.GetType().Name,
                delta_x = dx.Value,
                delta_y = dy.Value,
                delta_z = dz,
            }));
        }

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
