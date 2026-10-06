using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Shared plumbing for the polyline-edit tool family: handle resolution, entity picking,
    /// mid-transaction graphics flushing and new-entity creation.
    /// </summary>
    public static class PolylineToolSupport
    {
        /// <summary>Resolves a hex handle string to an ObjectId in the given database.</summary>
        public static bool TryResolveHandle(Database db, string handleStr, out ObjectId id)
        {
            id = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(handleStr)) return false;
            try
            {
                var handle = new Handle(Convert.ToInt64(handleStr.Trim(), 16));
                return db.TryGetObjectId(handle, out id);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the tools' common target selectors into ObjectIds:
        /// <c>entity_handle</c> (one), <c>entity_handles</c> (many) or <c>layer</c> (every polyline
        /// on that layer). Unresolvable handles are reported rather than skipped silently.
        /// </summary>
        public static List<ObjectId> ResolveTargets(
            Transaction tr,
            Database db,
            JsonElement parameters,
            out List<string> unresolved)
        {
            var ids = new List<ObjectId>();
            unresolved = new List<string>();

            if (parameters.TryGetProperty("entity_handle", out var single) &&
                single.ValueKind == JsonValueKind.String)
            {
                var h = single.GetString();
                if (!string.IsNullOrEmpty(h))
                {
                    if (TryResolveHandle(db, h!, out var id)) ids.Add(id);
                    else unresolved.Add(h!);
                }
            }

            if (parameters.TryGetProperty("entity_handles", out var many) &&
                many.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in many.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var h = item.GetString();
                    if (string.IsNullOrEmpty(h)) continue;
                    if (TryResolveHandle(db, h!, out var id)) ids.Add(id);
                    else unresolved.Add(h!);
                }
            }

            if (ids.Count == 0 &&
                parameters.TryGetProperty("layer", out var layerProp) &&
                layerProp.ValueKind == JsonValueKind.String)
            {
                var layer = layerProp.GetString();
                if (!string.IsNullOrEmpty(layer))
                    ids.AddRange(PolylineAdapter.FindPolylines(tr, db, layer));
            }

            return ids;
        }

        /// <summary>How a tool's target list was arrived at.</summary>
        public enum TargetSource
        {
            /// <summary>The agent named the objects (handles or a layer).</summary>
            Parameters,

            /// <summary>The engineer had already selected them before asking.</summary>
            ImpliedSelection,

            /// <summary>The engineer selected them at our prompt.</summary>
            Prompted,

            /// <summary>The engineer cancelled the selection prompt.</summary>
            Cancelled,

            /// <summary>Nothing to work on and no way to ask (no active document).</summary>
            Empty
        }

        /// <summary>
        /// Resolves targets the way every PLTOOLS command does: parameters first, then whatever the
        /// engineer had already selected, and finally by ASKING.
        ///
        /// This exists because the first live test hit the gap: "remove angles on polyline" with
        /// nothing selected made the tool fail with "supply entity_handle, entity_handles or layer"
        /// — a message about OUR parameter names, addressed to an engineer who has no way to supply
        /// a handle. Every LISP original opens with <c>ssget</c>; so do we now.
        /// </summary>
        /// <param name="promptHebrew">What to ask for, e.g. "בחרי פוליליינים לניקוי".</param>
        /// <param name="allowMultiple">false prompts for exactly one object.</param>
        public static List<ObjectId> ResolveTargetsOrPrompt(
            Transaction tr,
            Database db,
            JsonElement parameters,
            string promptHebrew,
            out List<string> unresolved,
            out TargetSource source,
            bool allowMultiple = true)
        {
            var ids = ResolveTargets(tr, db, parameters, out unresolved);
            if (ids.Count > 0)
            {
                source = TargetSource.Parameters;
                return ids;
            }

            var doc = ActiveDocument;
            if (doc == null)
            {
                source = TargetSource.Empty;
                return ids;
            }
            var ed = doc.Editor;

            // PickFirst: the engineer selected the polyline and THEN asked. Honour it — that is
            // the most natural way to say "this one".
            try
            {
                var implied = ed.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value != null)
                {
                    foreach (SelectedObject selected in implied.Value)
                    {
                        if (selected == null) continue;
                        if (!IsEditableTarget(tr, selected.ObjectId)) continue;
                        ids.Add(selected.ObjectId);
                        if (!allowMultiple) break;
                    }
                    if (ids.Count > 0)
                    {
                        ed.SetImpliedSelection(Array.Empty<ObjectId>());
                        source = TargetSource.ImpliedSelection;
                        return ids;
                    }
                }
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-edit] implied selection unavailable: {ex.Message}");
            }

            // Nothing given and nothing selected: ask.
            ToolUiNotifier.Step($"🖱️ {promptHebrew}");
            using var pickScope = new Utilities.InteractivePickScope();

            if (!allowMultiple)
            {
                var picked = PickEntity(ed, promptHebrew);
                if (picked == null || picked.Status != PromptStatus.OK)
                {
                    source = TargetSource.Cancelled;
                    return ids;
                }
                ids.Add(picked.ObjectId);
                source = TargetSource.Prompted;
                return ids;
            }

            var opts = new PromptSelectionOptions
            {
                MessageForAdding = "\n" + promptHebrew,
                RejectObjectsOnLockedLayers = true,
            };

            PromptSelectionResult result;
            try
            {
                result = ed.GetSelection(opts, PolylineSelectionFilter());
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-edit] selection prompt failed: {ex.Message}");
                source = TargetSource.Cancelled;
                return ids;
            }

            if (result.Status != PromptStatus.OK || result.Value == null)
            {
                source = TargetSource.Cancelled;
                return ids;
            }

            foreach (SelectedObject selected in result.Value)
            {
                if (selected == null) continue;
                ids.Add(selected.ObjectId);
            }

            source = ids.Count > 0 ? TargetSource.Prompted : TargetSource.Cancelled;
            return ids;
        }

        /// <summary>
        /// Resolves a single target: <c>entity_handle</c>, else the implied selection, else a pick.
        /// Returns ObjectId.Null when the engineer cancelled.
        /// </summary>
        public static ObjectId ResolveSingleTargetOrPrompt(
            Transaction tr,
            Database db,
            JsonElement parameters,
            string promptHebrew,
            out TargetSource source)
        {
            var ids = ResolveTargetsOrPrompt(
                tr, db, parameters, promptHebrew, out _, out source, allowMultiple: false);
            return ids.Count > 0 ? ids[0] : ObjectId.Null;
        }

        /// <summary>Selection filter for the entity types this family can edit.</summary>
        public static SelectionFilter PolylineSelectionFilter() => new(new[]
        {
            new TypedValue((int)DxfCode.Operator, "<OR"),
            new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
            new TypedValue((int)DxfCode.Start, "POLYLINE"),
            new TypedValue((int)DxfCode.Start, "HATCH"),
            new TypedValue((int)DxfCode.Operator, "OR>"),
        });

        /// <summary>True when the id is something this tool family knows how to edit.</summary>
        private static bool IsEditableTarget(Transaction tr, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return false;
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                return obj is Polyline or Polyline2d or Polyline3d or Hatch;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The active document, or null when there is none.</summary>
        public static Document? ActiveDocument => AcApp.DocumentManager.MdiActiveDocument;

        /// <summary>
        /// Asks the engineer to click a polyline (or hatch). Mirrors the instruction into the chat
        /// first, so the hint is on screen BEFORE the modal prompt takes over.
        /// </summary>
        public static PromptEntityResult? PickEntity(Editor ed, string hebrewPrompt)
        {
            ToolUiNotifier.Step($"🖱️ {hebrewPrompt}");
            var opts = new PromptEntityOptions("\n" + hebrewPrompt)
            {
                AllowNone = false,
                AllowObjectOnLockedLayer = false,
            };
            opts.SetRejectMessage("\nהאובייקט אינו פוליליין או Hatch — נסי שוב.");
            opts.AddAllowedClass(typeof(Polyline), exactMatch: false);
            opts.AddAllowedClass(typeof(Polyline2d), exactMatch: false);
            opts.AddAllowedClass(typeof(Polyline3d), exactMatch: false);
            opts.AddAllowedClass(typeof(Hatch), exactMatch: false);

            try
            {
                return ed.GetEntity(opts);
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-edit] entity pick failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Makes edits made inside the still-open tool transaction visible on screen, so a vertex
        /// disappears the moment it is clicked instead of only when the whole interactive session
        /// commits. Without this the engineer clicks blind — the very thing that makes the LISP
        /// version feel responsive is its per-click <c>entupd</c>.
        /// </summary>
        public static void FlushGraphics(Document doc)
        {
            try
            {
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.TransactionManager.FlushGraphics();
                doc.Editor.UpdateScreen();
            }
            catch (Exception ex)
            {
                // Purely cosmetic: never fail an edit because the screen refresh did.
                Utilities.MahodLogger.Info($"[polyline-edit] graphics flush failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Transforms a UCS point (what the editor returns) into WCS (what the adapter expects).
        /// </summary>
        public static Point3d UcsToWorld(Editor ed, Point3d ucsPoint) =>
            ucsPoint.TransformBy(ed.CurrentUserCoordinateSystem);

        /// <summary>
        /// Creates a lightweight polyline from planar points and appends it to model space.
        /// </summary>
        public static ObjectId CreatePolyline(
            Transaction tr,
            Database db,
            IReadOnlyList<Pt2> points,
            bool closed,
            double elevation,
            string? layer,
            IReadOnlyList<double>? bulges = null)
        {
            var pl = new Polyline();
            for (int i = 0; i < points.Count; i++)
            {
                double bulge = bulges != null && i < bulges.Count ? bulges[i] : 0.0;
                pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), bulge, 0, 0);
            }
            pl.Closed = closed;
            pl.Elevation = elevation;

            if (!string.IsNullOrEmpty(layer) && EnsureLayer(tr, db, layer!))
                pl.Layer = layer!;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        /// <summary>Creates a 3D polyline from 3D points and appends it to model space.</summary>
        public static ObjectId CreatePolyline3d(
            Transaction tr,
            Database db,
            IReadOnlyList<Point3d> points,
            bool closed,
            string? layer)
        {
            var collection = new Point3dCollection();
            foreach (var p in points) collection.Add(p);

            var p3 = new Polyline3d(Poly3dType.SimplePoly, collection, closed);
            if (!string.IsNullOrEmpty(layer) && EnsureLayer(tr, db, layer!))
                p3.Layer = layer!;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            ms.AppendEntity(p3);
            tr.AddNewlyCreatedDBObject(p3, true);
            return p3.ObjectId;
        }

        /// <summary>Copies the visual properties that make a converted entity look unchanged.</summary>
        public static void CopyEntityProperties(Entity source, Entity target)
        {
            try
            {
                target.Layer = source.Layer;
                target.Color = source.Color;
                target.LinetypeId = source.LinetypeId;
                target.LinetypeScale = source.LinetypeScale;
                target.LineWeight = source.LineWeight;
                if (!source.MaterialId.IsNull) target.MaterialId = source.MaterialId;
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-edit] property copy partial: {ex.Message}");
            }
        }

        /// <summary>Creates the layer if it does not exist yet. Returns false when it cannot.</summary>
        public static bool EnsureLayer(Transaction tr, Database db, string layerName)
        {
            try
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (lt.Has(layerName)) return true;

                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = layerName };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
                return true;
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Info($"[polyline-edit] layer '{layerName}' create failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Handle string of an entity, for round-tripping ids back to the agent.</summary>
        public static string HandleOf(Transaction tr, ObjectId id)
        {
            try
            {
                return tr.GetObject(id, OpenMode.ForRead).Handle.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>The Hebrew name of an entity type, for chat-facing messages.</summary>
        public static string HebrewTypeName(DBObject obj) => obj switch
        {
            Polyline => "פוליליין",
            Polyline2d => "פוליליין (סוג ישן)",
            Polyline3d => "פוליליין תלת-ממדי",
            Hatch => "Hatch",
            _ => obj.GetType().Name
        };
    }
}
