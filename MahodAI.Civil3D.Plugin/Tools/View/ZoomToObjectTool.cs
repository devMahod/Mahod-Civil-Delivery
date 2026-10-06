using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.View
{
    /// <summary>
    /// Zooms the active viewport to the extents of a specified entity or named Civil 3D object.
    /// Identify by <c>entity_handle</c>, or by <c>object_name</c> + <c>object_type</c>.
    /// </summary>
    public class ZoomToObjectTool : DrawingToolBase
    {
        public override string Name => "zoom_to_object";
        public override string Description => "Zooms the active viewport to the extents of a specified entity (by handle) or named Civil 3D object (alignment, profile, corridor, surface, pipe_network).";
        public override string Category => ToolCategories.Utility;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"" },
                ""object_name"": { ""type"": ""string"" },
                ""object_type"": { ""type"": ""string"", ""enum"": [""alignment"",""profile"",""corridor"",""surface"",""pipe_network""] },
                ""padding"": { ""type"": ""number"", ""description"": ""Extra padding around extents as fraction of size (default 0.1)"" }
            }
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var handleStr = GetStringParam(parameters, "entity_handle");
            var objectName = GetStringParam(parameters, "object_name");
            var objectType = GetStringParam(parameters, "object_type")?.ToLowerInvariant();
            var padding = GetDoubleParam(parameters, "padding") ?? 0.1;

            if (string.IsNullOrEmpty(handleStr) && string.IsNullOrEmpty(objectName))
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "יש לספק 'entity_handle' או 'object_name' + 'object_type'"));
            }
            if (string.IsNullOrEmpty(handleStr) && string.IsNullOrEmpty(objectType))
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "כאשר מספקים 'object_name' יש לספק גם 'object_type'"));
            }

            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            Extents3d? extents = null;
            string resolvedDescriptor = "";

            if (!string.IsNullOrEmpty(handleStr))
            {
                if (!TryResolveHandle(db, handleStr!, out var id, out var err))
                    return Task.FromResult(ToolResult.NotFound("Entity", handleStr + (err != null ? $" ({err})" : "")));

                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null)
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "האובייקט אינו Entity"));

                try { extents = entity.GeometricExtents; }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"שליפת תחום גאומטרי נכשלה: {ex.Message}"));
                }
                resolvedDescriptor = $"entity:{entity.Handle}";
            }
            else
            {
                if (civilDoc == null)
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא מסמך Civil 3D"));

                ObjectId? targetId = null;
                switch (objectType!)
                {
                    case "alignment":
                        targetId = ObjectFinder.FindAlignment(civilDoc, tr, objectName!);
                        break;
                    case "profile":
                        targetId = ObjectFinder.FindProfile(civilDoc, tr, objectName!);
                        break;
                    case "corridor":
                        foreach (ObjectId cid in civilDoc.CorridorCollection)
                        {
                            var c = tr.GetObject(cid, OpenMode.ForRead) as CivilDb.Corridor;
                            if (c != null && c.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetId = cid; break;
                            }
                        }
                        break;
                    case "surface":
                        foreach (ObjectId sid in civilDoc.GetSurfaceIds())
                        {
                            var s = tr.GetObject(sid, OpenMode.ForRead) as CivilDb.Surface;
                            if (s != null && s.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetId = sid; break;
                            }
                        }
                        break;
                    case "pipe_network":
                        foreach (ObjectId nid in civilDoc.GetPipeNetworkIds())
                        {
                            var n = tr.GetObject(nid, OpenMode.ForRead) as CivilDb.Network;
                            if (n != null && n.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetId = nid; break;
                            }
                        }
                        break;
                    default:
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            $"סוג אובייקט לא נתמך: {objectType}"));
                }

                if (targetId == null)
                    return Task.FromResult(ToolResult.NotFound(objectType!, objectName!));

                var obj = tr.GetObject(targetId.Value, OpenMode.ForRead) as Entity;
                if (obj == null)
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"לא ניתן לפתוח את {objectType} '{objectName}'"));

                try { extents = obj.GeometricExtents; }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"שליפת תחום גאומטרי נכשלה: {ex.Message}"));
                }
                resolvedDescriptor = $"{objectType}:{objectName}";
            }

            if (!extents.HasValue)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "לא נמצא תחום גאומטרי לאובייקט"));

            try
            {
                ApplyView(extents.Value, padding);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"הפעלת תצוגת זום נכשלה: {ex.Message}"));
            }

            var min = extents.Value.MinPoint;
            var max = extents.Value.MaxPoint;

            return Task.FromResult(ToolResult.Ok(new
            {
                target = resolvedDescriptor,
                min = new[] { min.X, min.Y, min.Z },
                max = new[] { max.X, max.Y, max.Z },
                width = max.X - min.X,
                height = max.Y - min.Y,
                padding,
            }));
        }

        private static void ApplyView(Extents3d ext, double padding)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var editor = doc.Editor;
            double width = Math.Max(1e-3, ext.MaxPoint.X - ext.MinPoint.X);
            double height = Math.Max(1e-3, ext.MaxPoint.Y - ext.MinPoint.Y);
            double cx = (ext.MinPoint.X + ext.MaxPoint.X) * 0.5;
            double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5;

            double factor = 1.0 + Math.Max(0, padding);
            width *= factor;
            height *= factor;

            using var view = new ViewTableRecord
            {
                CenterPoint = new Point2d(cx, cy),
                Width = width,
                Height = height,
            };
            editor.SetCurrentView(view);
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
